using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.WatchParty.Commands;
using Jellyfin.Plugin.WatchParty.Configuration;
using Jellyfin.Plugin.WatchParty.Playback;
using Jellyfin.Plugin.WatchParty.Voting;

namespace Jellyfin.Plugin.WatchParty.Discord;

/// <summary>Turns a Discord interaction into router calls and builds the callback body. Never calls Discord itself.</summary>
public sealed class InteractionHandler
{
    private const int Pong = 1;
    private const int ChannelMessage = 4;
    private const int UpdateMessage = 7;
    private const int AutocompleteResult = 8;
    private const int EphemeralFlag = 64;
    private const string CustomIdPrefix = "wp:";

    private readonly CommandRouter _router;
    private readonly ILibrarySearch _search;
    private readonly Func<PluginConfiguration> _config;

    public InteractionHandler(CommandRouter router, ILibrarySearch search, Func<PluginConfiguration> config)
    {
        _router = router;
        _search = search;
        _config = config;
    }

    public async Task<JsonObject> HandleAsync(JsonElement interaction, CancellationToken ct)
    {
        var type = interaction.GetProperty("type").GetInt32();
        if (type == 1)
        {
            return new JsonObject { ["type"] = Pong };
        }

        if (!interaction.TryGetProperty("member", out var member) || member.ValueKind != JsonValueKind.Object)
        {
            return Message("Use this in a server", ephemeral: true);
        }

        var homeGuild = _config().GuildId.Trim();
        if (homeGuild.Length > 0
            && (!interaction.TryGetProperty("guild_id", out var guild) || guild.GetString() != homeGuild))
        {
            return type == 4
                ? new JsonObject { ["type"] = AutocompleteResult, ["data"] = new JsonObject { ["choices"] = new JsonArray() } }
                : Message("This bot only works in its home server", ephemeral: true);
        }

        var user = $"<@{member.GetProperty("user").GetProperty("id").GetString()}>";
        var data = interaction.GetProperty("data");

        return type switch
        {
            2 => await HandleCommandAsync(data, user, HasHostRole(member), interaction.GetProperty("token").GetString(), ct).ConfigureAwait(false),
            3 => HandleButton(data, user),
            4 => HandleAutocomplete(data),
            _ => Message("Unsupported interaction", ephemeral: true),
        };
    }

    public static JsonObject VoteMessage(Vote vote, int type)
    {
        var verb = CommandRouter.Verb(vote.Action);
        var content = new StringBuilder($"**Vote: {verb}** — ends <t:{vote.EndsAt.ToUnixTimeSeconds()}:R>");
        AppendTally(content, vote);

        var buttons = new JsonArray();
        foreach (var option in vote.Options)
        {
            var label = $"{option.Label} ({vote.Counts[option.Key]})";
            buttons.Add(new JsonObject
            {
                ["type"] = 2,
                ["style"] = option.Key switch { "yes" => 3, "no" => 4, _ => 1 },
                ["label"] = label.Length > 80 ? label[..80] : label,
                ["custom_id"] = $"{CustomIdPrefix}{vote.Id:N}:{option.Key}",
            });
        }

        return new JsonObject
        {
            ["type"] = type,
            ["data"] = new JsonObject
            {
                ["content"] = content.ToString(),
                ["allowed_mentions"] = NoPings(),
                ["components"] = new JsonArray(new JsonObject { ["type"] = 1, ["components"] = buttons }),
            },
        };
    }

    public static JsonObject OutcomeMessage(VoteOutcome outcome)
    {
        var vote = outcome.Result.Vote;
        var content = new StringBuilder($"**Vote: {CommandRouter.Verb(vote.Action)}** — ended");
        AppendTally(content, vote);
        content.Append("\n\n").Append(outcome.Message);
        return new JsonObject
        {
            ["content"] = content.ToString(),
            ["allowed_mentions"] = NoPings(),
            ["components"] = new JsonArray(),
        };
    }

    public static bool TryParseCustomId(string id, out Guid voteId, out string optionKey)
    {
        voteId = Guid.Empty;
        optionKey = string.Empty;
        if (!id.StartsWith(CustomIdPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var parts = id[CustomIdPrefix.Length..].Split(':', 2);
        if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "N", out voteId))
        {
            return false;
        }

        optionKey = parts[1];
        return true;
    }

    private async Task<JsonObject> HandleCommandAsync(JsonElement data, string user, bool hostRole, string? token, CancellationToken ct)
    {
        var name = data.GetProperty("name").GetString();
        var force = Option(data, "force") is { ValueKind: JsonValueKind.True };

        RouterReply reply;
        switch (name)
        {
            case "pause" or "resume" or "skip" or "stop":
                var action = Enum.Parse<VoteAction>(name, ignoreCase: true);
                reply = await _router.RequestAsync(action, user, hostRole && force, token, ct).ConfigureAwait(false);
                break;
            case "pick":
                reply = await _router.RequestAsync(VoteAction.Pick, user, false, token, ct).ConfigureAwait(false);
                break;
            case "cancelvote":
                reply = _router.Cancel(hostRole);
                break;
            case "nominate":
                reply = Nominate(Option(data, "title")?.GetString() ?? string.Empty);
                break;
            case "ballot":
                reply = _router.ShowBallot();
                break;
            case "nowplaying":
                reply = _router.NowPlaying();
                break;
            default:
                reply = new RouterReply(ReplyKind.Error, "Unknown command");
                break;
        }

        return reply.Kind == ReplyKind.VoteStarted && reply.Vote is not null
            ? VoteMessage(reply.Vote, ChannelMessage)
            : Message(reply.Message, reply.Kind == ReplyKind.Error);
    }

    private RouterReply Nominate(string value)
    {
        if (Guid.TryParseExact(value, "N", out var itemId) && _search.GetTitle(itemId) is { } title)
        {
            return _router.Nominate(itemId, title);
        }

        var match = _search.Search(value, 1);
        return match.Count == 0
            ? new RouterReply(ReplyKind.Error, $"Nothing in the library matches \"{value}\"")
            : _router.Nominate(match[0].ItemId, match[0].Title);
    }

    private JsonObject HandleButton(JsonElement data, string user)
    {
        if (!TryParseCustomId(data.GetProperty("custom_id").GetString() ?? string.Empty, out var voteId, out var key))
        {
            return Message("Unknown button", ephemeral: true);
        }

        var reply = _router.Cast(voteId, user, key);
        return reply.Kind != ReplyKind.Error && reply.Vote is not null
            ? VoteMessage(reply.Vote, UpdateMessage)
            : Message(reply.Message, ephemeral: true);
    }

    private JsonObject HandleAutocomplete(JsonElement data)
    {
        var query = string.Empty;
        if (data.TryGetProperty("options", out var options))
        {
            foreach (var option in options.EnumerateArray())
            {
                if (option.TryGetProperty("focused", out var focused) && focused.ValueKind == JsonValueKind.True)
                {
                    query = option.GetProperty("value").GetString() ?? string.Empty;
                }
            }
        }

        var choices = new JsonArray();
        foreach (var nominee in _search.Search(query, 25))
        {
            choices.Add(new JsonObject
            {
                ["name"] = nominee.Title.Length > 100 ? nominee.Title[..100] : nominee.Title,
                ["value"] = nominee.ItemId.ToString("N"),
            });
        }

        return new JsonObject { ["type"] = AutocompleteResult, ["data"] = new JsonObject { ["choices"] = choices } };
    }

    private bool HasHostRole(JsonElement member)
    {
        var role = _config().HostRoleId;
        if (string.IsNullOrWhiteSpace(role) || !member.TryGetProperty("roles", out var roles))
        {
            return false;
        }

        return roles.EnumerateArray().Any(r => r.GetString() == role.Trim());
    }

    private static JsonElement? Option(JsonElement data, string name)
    {
        if (!data.TryGetProperty("options", out var options))
        {
            return null;
        }

        foreach (var option in options.EnumerateArray())
        {
            if (option.GetProperty("name").GetString() == name && option.TryGetProperty("value", out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static void AppendTally(StringBuilder content, Vote vote)
    {
        foreach (var option in vote.Options)
        {
            content.Append('\n').Append(option.Label).Append(": ").Append(vote.Counts[option.Key]);
        }
    }

    private static JsonObject Message(string text, bool ephemeral) => new()
    {
        ["type"] = ChannelMessage,
        ["data"] = new JsonObject
        {
            ["content"] = text,
            ["flags"] = ephemeral ? EphemeralFlag : 0,
            ["allowed_mentions"] = NoPings(),
        },
    };

    private static JsonObject NoPings() => new() { ["parse"] = new JsonArray() };
}
