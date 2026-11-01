using Jellyfin.Plugin.WatchParty.Api;
using Jellyfin.Plugin.WatchParty.Commands;
using Jellyfin.Plugin.WatchParty.Configuration;
using Jellyfin.Plugin.WatchParty.Discord;
using Jellyfin.Plugin.WatchParty.Tests.Fakes;
using Jellyfin.Plugin.WatchParty.Voting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.WatchParty.Tests;

public class CommandControllerTests
{
    private sealed class FakeBotStatus : IBotStatus
    {
        public BotState State { get; set; } = BotState.Connected;

        public string? BotUserName { get; set; } = "PartyBot";

        public string? ApplicationId { get; set; }
    }

    private readonly FakeTimeProvider _time = new();
    private readonly FakeHost _host = new();
    private readonly Ballot _ballot = new();
    private readonly PluginConfiguration _config = new() { WebhookSecret = "s3cret" };
    private readonly FakeBotStatus _bot = new();
    private readonly CommandRouter _router;
    private readonly CommandController _controller;

    public CommandControllerTests()
    {
        var engine = new VoteEngine(_time, new Random(1), () => new VoteSettings(60, 1));
        _router = new CommandRouter(engine, _ballot, _host, NullLogger<CommandRouter>.Instance);
        _controller = new CommandController(_router, () => _config, _bot, _host);
    }

    private static int Status(IActionResult result) => result switch
    {
        ObjectResult o => o.StatusCode ?? 200,
        StatusCodeResult s => s.StatusCode,
        _ => throw new InvalidOperationException(result.GetType().Name),
    };

    private static CommandResponse Body(IActionResult result) => Assert.IsType<CommandResponse>(((ObjectResult)result).Value);

    [Fact]
    public async Task EmptySecret_404()
    {
        _config.WebhookSecret = string.Empty;

        Assert.Equal(404, Status(await _controller.Command(new CommandRequest { Action = "pause", User = "a" }, string.Empty, default)));
    }

    [Fact]
    public async Task WrongSecret_401()
    {
        Assert.Equal(401, Status(await _controller.Command(new CommandRequest { Action = "pause", User = "a" }, "nope", default)));
        Assert.Equal(401, Status(await _controller.Command(new CommandRequest { Action = "pause", User = "a" }, null, default)));
    }

    [Fact]
    public async Task UnknownAction_400()
    {
        Assert.Equal(400, Status(await _controller.Command(new CommandRequest { Action = "explode", User = "a" }, "s3cret", default)));
    }

    [Fact]
    public async Task ValidPause_StartsVote_200()
    {
        var result = await _controller.Command(new CommandRequest { Action = "PAUSE", User = "a" }, "s3cret", default);

        Assert.Equal(200, Status(result));
        var body = Body(result);
        Assert.Equal("VoteStarted", body.Kind);
        Assert.Equal(_router.ActiveVote!.Id, body.VoteId);
    }

    [Fact]
    public async Task Force_RunsNow()
    {
        await _controller.Command(new CommandRequest { Action = "resume", User = "a", Force = true }, "s3cret", default);

        Assert.Equal([VoteAction.Resume], _host.Sent);
    }

    [Fact]
    public async Task RouterError_409()
    {
        _host.Status = new Playback.HostStatus(false, null, false);

        var result = await _controller.Command(new CommandRequest { Action = "pause", User = "a" }, "s3cret", default);

        Assert.Equal(409, Status(result));
        Assert.Equal("Host screen offline", Body(result).Message);
    }

    [Fact]
    public async Task VoteBody_CastsOnActive()
    {
        await _controller.Command(new CommandRequest { Action = "pause", User = "a" }, "s3cret", default);

        var result = await _controller.Command(new CommandRequest { User = "b", Vote = "no" }, "s3cret", default);

        Assert.Equal(200, Status(result));
        Assert.Equal(1, _router.ActiveVote!.Counts["no"]);
    }

    [Fact]
    public async Task VoteIndex_MapsToOptionKey()
    {
        var heat = Guid.NewGuid();
        _router.Nominate(Guid.NewGuid(), "Alien");
        _router.Nominate(heat, "Heat");
        await _controller.Command(new CommandRequest { Action = "pick", User = "a" }, "s3cret", default);

        await _controller.Command(new CommandRequest { User = "b", Vote = "1" }, "s3cret", default);

        Assert.Equal(1, _router.ActiveVote!.Counts[heat.ToString("N")]);
    }

    [Fact]
    public async Task VoteIndexOutOfRange_400()
    {
        await _controller.Command(new CommandRequest { Action = "pause", User = "a" }, "s3cret", default);

        Assert.Equal(400, Status(await _controller.Command(new CommandRequest { User = "b", Vote = "7" }, "s3cret", default)));
    }

    [Fact]
    public async Task Cancel_WithForce_Cancels()
    {
        await _controller.Command(new CommandRequest { Action = "pause", User = "a" }, "s3cret", default);

        await _controller.Command(new CommandRequest { Action = "cancel", User = "a", Force = true }, "s3cret", default);

        Assert.Null(_router.ActiveVote);
    }

    [Fact]
    public void Status_ReturnsInviteUrlWhenAppIdKnown()
    {
        _bot.ApplicationId = "app123";

        var body = Assert.IsType<BotStatusResponse>(((ObjectResult)_controller.Status()).Value);

        Assert.Equal("Connected", body.State);
        Assert.Equal("PartyBot", body.BotUserName);
        Assert.Equal("https://discord.com/oauth2/authorize?client_id=app123&scope=bot%20applications.commands&permissions=0", body.InviteUrl);
    }

    [Fact]
    public void Status_NoAppId_NullInvite()
    {
        var body = Assert.IsType<BotStatusResponse>(((ObjectResult)_controller.Status()).Value);

        Assert.Null(body.InviteUrl);
    }

    [Fact]
    public void Status_IncludesHostAndHomeGuild()
    {
        _config.GuildId = "555";
        _host.Status = new Playback.HostStatus(true, "Alien", false, "Firefox");

        var body = Assert.IsType<BotStatusResponse>(((ObjectResult)_controller.Status()).Value);

        Assert.Equal("555", body.HomeGuildId);
        Assert.Equal("Firefox", body.HostDevice);
        Assert.Equal("Alien", body.NowPlaying);
    }

    [Fact]
    public void Status_NoHost_NullHostFields()
    {
        _host.Status = new Playback.HostStatus(false, null, false);

        var body = Assert.IsType<BotStatusResponse>(((ObjectResult)_controller.Status()).Value);

        Assert.Null(body.HostDevice);
        Assert.Null(body.HomeGuildId);
    }
}
