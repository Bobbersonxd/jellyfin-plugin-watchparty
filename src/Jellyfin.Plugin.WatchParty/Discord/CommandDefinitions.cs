using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.WatchParty.Discord;

/// <summary>Slash commands registered with Discord when the bot connects.</summary>
public static class CommandDefinitions
{
    private const int StringOption = 3;
    private const int BooleanOption = 5;

    public static JsonArray All => new(
        Vote("pause", "Vote to pause the host screen"),
        Vote("resume", "Vote to resume playback"),
        Vote("skip", "Vote to skip to the next item"),
        Vote("stop", "Vote to stop playback"),
        Make("nominate", "Nominate a title for the next pick vote", new JsonObject
        {
            ["type"] = StringOption,
            ["name"] = "title",
            ["description"] = "Movie or episode to nominate",
            ["required"] = true,
            ["autocomplete"] = true,
        }),
        Make("ballot", "Show the nominated titles"),
        Make("pick", "Start a vote between the nominated titles"),
        Make("cancelvote", "Cancel the running vote (host role only)"),
        Make("nowplaying", "Show what the host screen is playing"));

    private static JsonObject Vote(string name, string description) => Make(name, description, new JsonObject
    {
        ["type"] = BooleanOption,
        ["name"] = "force",
        ["description"] = "Host role only: do it now without a vote",
        ["required"] = false,
    });

    private static JsonObject Make(string name, string description, params JsonObject[] options) => new()
    {
        ["name"] = name,
        ["type"] = 1,
        ["description"] = description,
        ["dm_permission"] = false,
        ["options"] = new JsonArray(options.Cast<JsonNode>().ToArray()),
    };
}
