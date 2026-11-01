using Jellyfin.Plugin.WatchParty.Voting;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.WatchParty.Tests;

public class VoteEngineTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly List<VoteResult> _resolved = [];
    private VoteSettings _settings = new(60, 1);

    private VoteEngine NewEngine(int seed = 1)
    {
        var engine = new VoteEngine(_time, new Random(seed), () => _settings);
        engine.Resolved += r => _resolved.Add(r);
        return engine;
    }

    private static IReadOnlyList<VoteOption> PickOptions(int n) =>
        Enumerable.Range(0, n).Select(i => new VoteOption($"k{i}", $"Movie {i}")).ToArray();

    [Fact]
    public void TryStart_YesNo_StarterCountedYes()
    {
        var vote = NewEngine().TryStart(VoteAction.Pause, VoteOptions.YesNo, "a", null);

        Assert.NotNull(vote);
        Assert.Equal(1, vote!.Counts["yes"]);
        Assert.Equal(0, vote.Counts["no"]);
    }

    [Fact]
    public void TryStart_WhileActive_ReturnsNull()
    {
        var engine = NewEngine();
        engine.TryStart(VoteAction.Pause, VoteOptions.YesNo, "a", null);

        Assert.Null(engine.TryStart(VoteAction.Skip, VoteOptions.YesNo, "b", null));
    }

    [Fact]
    public void Cast_ChangeVote_ReturnsChanged_AndMovesCount()
    {
        var engine = NewEngine();
        var vote = engine.TryStart(VoteAction.Pause, VoteOptions.YesNo, "a", null)!;

        Assert.Equal(CastStatus.Counted, engine.Cast(vote.Id, "b", "yes"));
        Assert.Equal(CastStatus.Changed, engine.Cast(vote.Id, "b", "no"));

        var active = engine.Active!;
        Assert.Equal(1, active.Counts["yes"]);
        Assert.Equal(1, active.Counts["no"]);
    }

    [Fact]
    public void Cast_UnknownOption_ReturnsUnknownOption()
    {
        var engine = NewEngine();
        var vote = engine.TryStart(VoteAction.Pause, VoteOptions.YesNo, "a", null)!;

        Assert.Equal(CastStatus.UnknownOption, engine.Cast(vote.Id, "b", "maybe"));
    }

    [Fact]
    public void Cast_StaleVoteId_ReturnsNoActiveVote()
    {
        var engine = NewEngine();
        var first = engine.TryStart(VoteAction.Pause, VoteOptions.YesNo, "a", null)!;
        _time.Advance(TimeSpan.FromSeconds(60));
        var second = engine.TryStart(VoteAction.Skip, VoteOptions.YesNo, "a", null)!;

        Assert.Equal(CastStatus.NoActiveVote, engine.Cast(first.Id, "b", "no"));
        Assert.Equal(0, engine.Active!.Counts["no"]);
        Assert.Equal(second.Id, engine.Active.Id);
    }

    [Fact]
    public void Timer_YesMajority_Passes()
    {
        var engine = NewEngine();
        engine.TryStart(VoteAction.Pause, VoteOptions.YesNo, "a", null);

        _time.Advance(TimeSpan.FromSeconds(60));

        var result = Assert.Single(_resolved);
        Assert.True(result.Passed);
        Assert.Equal("passed", result.Reason);
        Assert.Equal("yes", result.Winner!.Key);
        Assert.Null(engine.Active);
    }

    [Fact]
    public void Timer_Tie_Rejected()
    {
        var engine = NewEngine();
        var vote = engine.TryStart(VoteAction.Pause, VoteOptions.YesNo, "a", null)!;
        engine.Cast(vote.Id, "b", "no");

        _time.Advance(TimeSpan.FromSeconds(60));

        var result = Assert.Single(_resolved);
        Assert.False(result.Passed);
        Assert.Equal("rejected", result.Reason);
    }

    [Fact]
    public void Timer_BelowMinVoters_Fails()
    {
        _settings = new VoteSettings(60, 3);
        var engine = NewEngine();
        engine.TryStart(VoteAction.Pause, VoteOptions.YesNo, "a", null);

        _time.Advance(TimeSpan.FromSeconds(60));

        var result = Assert.Single(_resolved);
        Assert.False(result.Passed);
        Assert.Equal("not enough voters", result.Reason);
    }

    [Fact]
    public void Timer_NotFiredEarly()
    {
        var engine = NewEngine();
        engine.TryStart(VoteAction.Pause, VoteOptions.YesNo, "a", null);

        _time.Advance(TimeSpan.FromSeconds(59));

        Assert.Empty(_resolved);
        Assert.NotNull(engine.Active);
    }

    [Fact]
    public void Pick_MostVotesWins()
    {
        var engine = NewEngine();
        var vote = engine.TryStart(VoteAction.Pick, PickOptions(3), "a", null)!;
        Assert.Equal(0, vote.Counts["k0"]);
        engine.Cast(vote.Id, "a", "k0");
        engine.Cast(vote.Id, "b", "k0");
        engine.Cast(vote.Id, "c", "k1");

        _time.Advance(TimeSpan.FromSeconds(60));

        var result = Assert.Single(_resolved);
        Assert.True(result.Passed);
        Assert.Equal("k0", result.Winner!.Key);
    }

    [Fact]
    public void Pick_Tie_UsesRandom()
    {
        var engine = NewEngine(seed: 1);
        var vote = engine.TryStart(VoteAction.Pick, PickOptions(3), "a", null)!;
        engine.Cast(vote.Id, "a", "k0");
        engine.Cast(vote.Id, "b", "k2");

        _time.Advance(TimeSpan.FromSeconds(60));

        var expected = new[] { "k0", "k2" }[new Random(1).Next(2)];
        Assert.Equal(expected, Assert.Single(_resolved).Winner!.Key);
    }

    [Fact]
    public void Pick_NoVotes_Fails_NoVotesReason()
    {
        var engine = NewEngine();
        engine.TryStart(VoteAction.Pick, PickOptions(2), "a", null);

        _time.Advance(TimeSpan.FromSeconds(60));

        var result = Assert.Single(_resolved);
        Assert.False(result.Passed);
        Assert.Null(result.Winner);
        Assert.Equal("no votes", result.Reason);
    }

    [Fact]
    public void Cancel_RaisesResolvedCancelled_AndClearsActive()
    {
        var engine = NewEngine();
        engine.TryStart(VoteAction.Pause, VoteOptions.YesNo, "a", null);

        var result = engine.Cancel();

        Assert.NotNull(result);
        Assert.False(result!.Passed);
        Assert.Equal("cancelled", result.Reason);
        Assert.Same(result, Assert.Single(_resolved));
        Assert.Null(engine.Active);

        _time.Advance(TimeSpan.FromSeconds(60));
        Assert.Single(_resolved);
    }

    [Fact]
    public void Cancel_NoActiveVote_ReturnsNull()
    {
        Assert.Null(NewEngine().Cancel());
    }

    [Fact]
    public void Resolved_HandlerCanStartNewVote()
    {
        var engine = NewEngine();
        Vote? started = null;
        engine.Resolved += _ => started = engine.TryStart(VoteAction.Skip, VoteOptions.YesNo, "z", null);
        engine.TryStart(VoteAction.Pause, VoteOptions.YesNo, "a", null);

        _time.Advance(TimeSpan.FromSeconds(60));

        Assert.NotNull(started);
    }

    [Fact]
    public void Vote_CarriesDiscordToken_AndEndsAt()
    {
        var engine = NewEngine();
        var vote = engine.TryStart(VoteAction.Pause, VoteOptions.YesNo, "a", "tok")!;

        Assert.Equal("tok", vote.DiscordToken);
        Assert.Equal(_time.GetUtcNow().AddSeconds(60), vote.EndsAt);
        Assert.Equal("a", vote.StartedBy);
        Assert.Equal(VoteAction.Pause, vote.Action);
    }

    [Fact]
    public void Cast_ParallelUsers_AllCounted()
    {
        var engine = NewEngine();
        var vote = engine.TryStart(VoteAction.Pause, VoteOptions.YesNo, "a", null)!;

        Parallel.For(0, 200, i => engine.Cast(vote.Id, $"u{i}", "yes"));

        Assert.Equal(201, engine.Active!.Counts["yes"]);
    }
}
