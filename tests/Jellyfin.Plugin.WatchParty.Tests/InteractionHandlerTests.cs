using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.WatchParty.Commands;
using Jellyfin.Plugin.WatchParty.Configuration;
using Jellyfin.Plugin.WatchParty.Discord;
using Jellyfin.Plugin.WatchParty.Tests.Fakes;
using Jellyfin.Plugin.WatchParty.Voting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jellyfin.Plugin.WatchParty.Tests;

public class InteractionHandlerTests
{
    private const string HostRole = "999";
    private readonly FakeTimeProvider _time = new();
    private readonly FakeHost _host = new();
    private readonly FakeLibrarySearch _search = new();
    private readonly Ballot _ballot = new();
    private readonly CommandRouter _router;
    private readonly InteractionHandler _handler;
    private readonly PluginConfiguration _config = new() { HostRoleId = HostRole };
    private int _saves;

    public InteractionHandlerTests()
    {
        var engine = new VoteEngine(_time, new Random(1), () => new VoteSettings(60, 1));
        _router = new CommandRouter(engine, _ballot, _host, NullLogger<CommandRouter>.Instance);
        _handler = new InteractionHandler(_router, _search, () => _config, id =>
        {
            _config.GuildId = id;
            _saves++;
        });
    }

    private static JsonElement Interaction(int type, object data, string userId = "1", string[]? roles = null, bool dm = false, string guildId = "555")
    {
        var obj = new Dictionary<string, object?>
        {
            ["guild_id"] = dm ? null : guildId,
            ["id"] = "int1",
            ["token"] = "itok",
            ["type"] = type,
            ["data"] = data,
        };
        if (dm)
        {
            obj["user"] = new { id = userId };
        }
        else
        {
            obj["member"] = new { user = new { id = userId }, roles = roles ?? [] };
        }

        return JsonSerializer.SerializeToElement(obj);
    }

    private static JsonElement Command(string name, object[]? options = null, string userId = "1", string[]? roles = null) =>
        Interaction(2, new { name, options = options ?? [] }, userId, roles);

    private static JsonElement Button(string customId, string userId) =>
        Interaction(3, new { custom_id = customId, component_type = 2 }, userId);

    private static JsonArray Buttons(JsonObject response) =>
        response["data"]!["components"]![0]!["components"]!.AsArray();

    private static bool IsEphemeral(JsonObject response) => response["data"]!["flags"]!.GetValue<int>() == 64;

    [Fact]
    public async Task Command_Pause_StartsVote_ReturnsVoteMessageWithButtons()
    {
        var response = await _handler.HandleAsync(Command("pause"), default);

        Assert.Equal(4, response["type"]!.GetValue<int>());
        var vote = _router.ActiveVote!;
        Assert.Equal("itok", vote.DiscordToken);
        Assert.Equal("<@1>", vote.StartedBy);
        var buttons = Buttons((JsonObject)response);
        Assert.Equal(2, buttons.Count);
        Assert.Equal($"wp:{vote.Id:N}:yes", buttons[0]!["custom_id"]!.GetValue<string>());
        Assert.Contains("**Vote: pause**", response["data"]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Command_ForceWithoutRole_StartsVoteInstead()
    {
        await _handler.HandleAsync(Command("pause", [new { name = "force", type = 5, value = true }]), default);

        Assert.NotNull(_router.ActiveVote);
        Assert.Empty(_host.Sent);
    }

    [Fact]
    public async Task Command_ForceWithRole_RunsImmediately()
    {
        var response = await _handler.HandleAsync(Command("pause", [new { name = "force", type = 5, value = true }], roles: [HostRole]), default);

        Assert.Equal([VoteAction.Pause], _host.Sent);
        Assert.Null(_router.ActiveVote);
        Assert.False(IsEphemeral(response));
    }

    [Fact]
    public async Task Command_RoleWithoutForce_StillVotes()
    {
        await _handler.HandleAsync(Command("pause", roles: [HostRole]), default);

        Assert.NotNull(_router.ActiveVote);
    }

    [Fact]
    public async Task Command_HostOffline_Ephemeral()
    {
        _host.Status = new Playback.HostStatus(false, null, false);

        var response = await _handler.HandleAsync(Command("skip"), default);

        Assert.True(IsEphemeral(response));
        Assert.Equal("Host screen offline", response["data"]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Button_Yes_ReturnsType7UpdatedCount()
    {
        await _handler.HandleAsync(Command("pause"), default);
        var vote = _router.ActiveVote!;

        var response = await _handler.HandleAsync(Button($"wp:{vote.Id:N}:yes", "2"), default);

        Assert.Equal(7, response["type"]!.GetValue<int>());
        Assert.Equal("Yes (2)", Buttons((JsonObject)response)[0]!["label"]!.GetValue<string>());
    }

    [Fact]
    public async Task Button_StaleVote_EphemeralEnded()
    {
        var response = await _handler.HandleAsync(Button($"wp:{Guid.NewGuid():N}:yes", "2"), default);

        Assert.Equal(4, response["type"]!.GetValue<int>());
        Assert.True(IsEphemeral(response));
        Assert.Equal("This vote has ended", response["data"]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Autocomplete_ReturnsType8_Max25_NamesTruncated100()
    {
        for (var i = 0; i < 30; i++)
        {
            _search.Items.Add(new Nominee(Guid.NewGuid(), "Alien " + new string('x', 120) + i));
        }

        var response = await _handler.HandleAsync(
            Interaction(4, new { name = "nominate", options = new[] { new { name = "title", type = 3, value = "alien", focused = true } } }),
            default);

        Assert.Equal(8, response["type"]!.GetValue<int>());
        var choices = response["data"]!["choices"]!.AsArray();
        Assert.Equal(25, choices.Count);
        Assert.All(choices, c => Assert.True(c!["name"]!.GetValue<string>().Length <= 100));
        Assert.Equal(_search.Items[0].ItemId.ToString("N"), choices[0]!["value"]!.GetValue<string>());
    }

    [Fact]
    public async Task Nominate_AddsToBallot()
    {
        var alien = new Nominee(Guid.NewGuid(), "Alien (1979)");
        _search.Items.Add(alien);

        var response = await _handler.HandleAsync(Command("nominate", [new { name = "title", type = 3, value = alien.ItemId.ToString("N") }]), default);

        Assert.Equal(alien, Assert.Single(_ballot.Nominees));
        Assert.Contains("Alien (1979)", response["data"]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Nominate_FreeText_UsesFirstSearchMatch()
    {
        var alien = new Nominee(Guid.NewGuid(), "Alien (1979)");
        _search.Items.Add(alien);

        await _handler.HandleAsync(Command("nominate", [new { name = "title", type = 3, value = "alien" }]), default);

        Assert.Equal(alien, Assert.Single(_ballot.Nominees));
    }

    [Fact]
    public async Task Nominate_NoMatch_Ephemeral()
    {
        var response = await _handler.HandleAsync(Command("nominate", [new { name = "title", type = 3, value = "zzz" }]), default);

        Assert.True(IsEphemeral(response));
        Assert.Empty(_ballot.Nominees);
    }

    [Fact]
    public async Task CancelVote_WithRole_Cancels()
    {
        await _handler.HandleAsync(Command("pause"), default);

        await _handler.HandleAsync(Command("cancelvote", roles: [HostRole]), default);

        Assert.Null(_router.ActiveVote);
    }

    [Fact]
    public async Task Dm_ReturnsEphemeralUseInServer()
    {
        var response = await _handler.HandleAsync(Interaction(2, new { name = "pause" }, dm: true), default);

        Assert.True(IsEphemeral(response));
        Assert.Equal("Use this in a server", response["data"]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Messages_DisableMentionPings()
    {
        var response = await _handler.HandleAsync(Command("pause"), default);

        Assert.Empty(response["data"]!["allowed_mentions"]!["parse"]!.AsArray());
    }

    [Fact]
    public void ParseCustomId_RoundTrips()
    {
        var id = Guid.NewGuid();

        Assert.True(InteractionHandler.TryParseCustomId($"wp:{id:N}:yes", out var parsedId, out var key));
        Assert.Equal(id, parsedId);
        Assert.Equal("yes", key);
        Assert.False(InteractionHandler.TryParseCustomId("other:thing", out _, out _));
    }

    [Fact]
    public async Task OutcomeMessage_HasNoComponents()
    {
        await _handler.HandleAsync(Command("pause"), default);
        VoteOutcome? outcome = null;
        _router.Completed += o =>
        {
            outcome = o;
            return Task.CompletedTask;
        };
        _time.Advance(TimeSpan.FromSeconds(60));

        var message = InteractionHandler.OutcomeMessage(outcome!);

        Assert.Empty(message["components"]!.AsArray());
        Assert.Contains("passed", message["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task OtherGuild_WhenGuildSet_RejectedEphemeral()
    {
        _config.GuildId = "555";

        var response = await _handler.HandleAsync(Interaction(2, new { name = "pause" }, guildId: "666"), default);

        Assert.True(IsEphemeral(response));
        Assert.Null(_router.ActiveVote);
    }

    [Fact]
    public async Task HomeGuild_WhenGuildSet_Works()
    {
        _config.GuildId = " 555 ";

        await _handler.HandleAsync(Command("pause"), default);

        Assert.NotNull(_router.ActiveVote);
    }

    [Fact]
    public async Task OtherGuild_Autocomplete_ReturnsNoChoices()
    {
        _config.GuildId = "555";
        _search.Items.Add(new Nominee(Guid.NewGuid(), "Alien"));

        var response = await _handler.HandleAsync(
            Interaction(4, new { name = "nominate", options = new[] { new { name = "title", type = 3, value = "alien", focused = true } } }, guildId: "666"),
            default);

        Assert.Equal(8, response["type"]!.GetValue<int>());
        Assert.Empty(response["data"]!["choices"]!.AsArray());
    }

    [Fact]
    public async Task EmptyGuild_FirstInteraction_LocksAndSaves()
    {
        await _handler.HandleAsync(Command("pause"), default);

        Assert.Equal("555", _config.GuildId);
        Assert.Equal(1, _saves);
        Assert.NotNull(_router.ActiveVote);
    }

    [Fact]
    public async Task AfterLock_OtherGuild_Rejected()
    {
        await _handler.HandleAsync(Command("nowplaying"), default);

        var response = await _handler.HandleAsync(Interaction(2, new { name = "pause" }, guildId: "666"), default);

        Assert.True(IsEphemeral(response));
        Assert.Null(_router.ActiveVote);
        Assert.Equal(1, _saves);
    }
}
