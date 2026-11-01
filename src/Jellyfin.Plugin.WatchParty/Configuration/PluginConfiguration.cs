using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.WatchParty.Configuration;

/// <summary>Watch Party settings.</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    public string DiscordBotToken { get; set; } = string.Empty;

    public string GuildId { get; set; } = string.Empty;

    public string HostRoleId { get; set; } = string.Empty;

    public string ChannelWebhookUrl { get; set; } = string.Empty;

    public string HostDeviceName { get; set; } = string.Empty;

    public string WebhookSecret { get; set; } = string.Empty;

    public int VoteDurationSeconds { get; set; } = 60;

    public int MinVoters { get; set; } = 1;
}
