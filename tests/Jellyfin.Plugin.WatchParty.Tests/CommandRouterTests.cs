using Jellyfin.Plugin.WatchParty.Commands;
using Jellyfin.Plugin.WatchParty.Playback;
using Jellyfin.Plugin.WatchParty.Tests.Fakes;
using Jellyfin.Plugin.WatchParty.Voting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.WatchParty.Tests;

public class CommandRouterTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly FakeHost _host = new();
    private readonly Ballot _ballot = new();
    private readonly List<VoteOutcome> _outcomes = [];
    private readonly CommandRouter _router;
    private readonly VoteEngine _engine;

    public CommandRouterTests()
    {
        _engine = new VoteEngine(_time, new Random(1), () => new VoteSettings(60, 1));
        _router = new CommandRouter(_engine, _ballot, _host, NullLogger<CommandRouter>.Instance);
        _router.Completed += o =>
        {
            _outcomes.Add(o);
            return Task.CompletedTask;
        };
    }

    private void EndVote() => _time.Advance(TimeSpan.FromSeconds(60));

    [Fact]
    public async Task Request_HostOffline_ErrorNoVote()
    {
        _host.Status = new HostStatus(false, null, false);

        var reply = await _router.RequestAsync(VoteAction.Pause, "a", false, null, default);

        Assert.Equal(ReplyKind.Error, reply.Kind);
        Assert.Equal("Host screen offline", reply.Message);
        Assert.Null(_engine.Active);
    }

    [Fact]
    public async Task Request_Privileged_RunsImmediately()
    {
        var reply = await _router.RequestAsync(VoteAction.Pause, "a", true, null, default);

        Assert.Equal(ReplyKind.Done, reply.Kind);
        Assert.Equal("a forced pause", reply.Message);
        Assert.Equal([VoteAction.Pause], _host.Sent);
        Assert.Null(_engine.Active);
    }

    [Fact]
    public async Task Request_StartsVote()
    {
        var reply = await _router.RequestAsync(VoteAction.Pause, "a", false, "tok", default);

        Assert.Equal(ReplyKind.VoteStarted, reply.Kind);
        Assert.NotNull(reply.Vote);
        Assert.Equal("tok", reply.Vote!.DiscordToken);
        Assert.Empty(_host.Sent);
    }

    [Fact]
    public async Task Request_WhileActive_Error()
    {
        await _router.RequestAsync(VoteAction.Pause, "a", false, null, default);

        var reply = await _router.RequestAsync(VoteAction.Stop, "b", false, null, default);

        Assert.Equal(ReplyKind.Error, reply.Kind);
        Assert.Equal("A vote is already in progress", reply.Message);
    }

    [Fact]
    public async Task PassedPauseVote_SendsPause_RaisesCompletedApplied()
    {
        await _router.RequestAsync(VoteAction.Pause, "a", false, null, default);

        EndVote();

        Assert.Equal([VoteAction.Pause], _host.Sent);
        var outcome = Assert.Single(_outcomes);
        Assert.True(outcome.Applied);
        Assert.True(outcome.Result.Passed);
        Assert.Contains("passed", outcome.Message);
    }

    [Fact]
    public async Task RejectedVote_SendsNothing_NotApplied()
    {
        var reply = await _router.RequestAsync(VoteAction.Pause, "a", false, null, default);
        _router.Cast(reply.Vote!.Id, "b", "no");

        EndVote();

        Assert.Empty(_host.Sent);
        var outcome = Assert.Single(_outcomes);
        Assert.False(outcome.Applied);
        Assert.Contains("rejected", outcome.Message);
    }

    [Fact]
    public async Task Skip_NoNextInQueue_SendsStop()
    {
        _host.Status = new HostStatus(true, "Alien", false);

        await _router.RequestAsync(VoteAction.Skip, "a", true, null, default);

        Assert.Equal([VoteAction.Stop], _host.Sent);
    }

    [Fact]
    public async Task Skip_WithNextInQueue_SendsSkip()
    {
        await _router.RequestAsync(VoteAction.Skip, "a", true, null, default);

        Assert.Equal([VoteAction.Skip], _host.Sent);
    }

    [Fact]
    public async Task Pick_FewerThanTwoNominees_Error()
    {
        _router.Nominate(Guid.NewGuid(), "Alien");

        var reply = await _router.RequestAsync(VoteAction.Pick, "a", false, null, default);

        Assert.Equal(ReplyKind.Error, reply.Kind);
        Assert.Null(_engine.Active);
    }

    [Fact]
    public async Task PassedPick_PlaysWinner_ClearsBallot()
    {
        var alien = Guid.NewGuid();
        _router.Nominate(alien, "Alien");
        _router.Nominate(Guid.NewGuid(), "Heat");
        var reply = await _router.RequestAsync(VoteAction.Pick, "a", false, null, default);
        Assert.Equal(["Alien", "Heat"], reply.Vote!.Options.Select(o => o.Label));
        _router.Cast(reply.Vote.Id, "a", alien.ToString("N"));

        EndVote();

        Assert.Equal([alien], _host.Played);
        Assert.Empty(_ballot.Nominees);
        var outcome = Assert.Single(_outcomes);
        Assert.True(outcome.Applied);
        Assert.Contains("Alien", outcome.Message);
    }

    [Fact]
    public async Task Router_PickWinnerUnplayable_KeepsBallot()
    {
        var alien = Guid.NewGuid();
        _router.Nominate(alien, "Alien");
        _router.Nominate(Guid.NewGuid(), "Heat");
        var reply = await _router.RequestAsync(VoteAction.Pick, "a", false, null, default);
        _router.Cast(reply.Vote!.Id, "a", alien.ToString("N"));
        _host.PlayResult = false;

        EndVote();

        Assert.Equal(2, _ballot.Nominees.Count);
        var outcome = Assert.Single(_outcomes);
        Assert.False(outcome.Applied);
        Assert.Contains("could not be applied", outcome.Message);
    }

    [Fact]
    public async Task Router_PassedVote_HostGoneAtResolve_ReportsNotApplied()
    {
        await _router.RequestAsync(VoteAction.Pause, "a", false, null, default);
        _host.Status = new HostStatus(false, null, false);

        EndVote();

        var outcome = Assert.Single(_outcomes);
        Assert.False(outcome.Applied);
        Assert.Contains("passed but could not be applied: Host screen offline", outcome.Message);
    }

    [Fact]
    public async Task Cancel_NotPrivileged_Error()
    {
        await _router.RequestAsync(VoteAction.Pause, "a", false, null, default);

        var reply = _router.Cancel(false);

        Assert.Equal(ReplyKind.Error, reply.Kind);
        Assert.Equal("Only the host can cancel votes", reply.Message);
        Assert.NotNull(_engine.Active);
    }

    [Fact]
    public async Task Cancel_Privileged_EndsVote()
    {
        await _router.RequestAsync(VoteAction.Pause, "a", false, null, default);

        var reply = _router.Cancel(true);

        Assert.Equal(ReplyKind.Done, reply.Kind);
        Assert.Null(_engine.Active);
        Assert.Contains("cancelled", Assert.Single(_outcomes).Message);
    }

    [Fact]
    public void Cast_NoActiveVote_ErrorEnded()
    {
        var reply = _router.Cast(Guid.NewGuid(), "a", "yes");

        Assert.Equal(ReplyKind.Error, reply.Kind);
        Assert.Equal("This vote has ended", reply.Message);
    }

    [Fact]
    public void Nominate_Duplicate_Error()
    {
        var id = Guid.NewGuid();
        Assert.Equal(ReplyKind.Info, _router.Nominate(id, "Alien").Kind);
        Assert.Equal(ReplyKind.Error, _router.Nominate(id, "Alien").Kind);
    }

    [Fact]
    public void ShowBallot_ListsNominees()
    {
        _router.Nominate(Guid.NewGuid(), "Alien");

        Assert.Contains("Alien", _router.ShowBallot().Message);
    }

    [Fact]
    public void NowPlaying_ShowsTitle()
    {
        Assert.Contains("Alien", _router.NowPlaying().Message);
    }
}
