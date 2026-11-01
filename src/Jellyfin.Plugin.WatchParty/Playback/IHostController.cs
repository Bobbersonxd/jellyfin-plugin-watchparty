using Jellyfin.Plugin.WatchParty.Voting;

namespace Jellyfin.Plugin.WatchParty.Playback;

/// <summary>What the host screen is doing right now.</summary>
public sealed record HostStatus(bool Online, string? NowPlayingTitle, bool HasNextInQueue, string? DeviceName = null);

/// <summary>Controls the one Jellyfin session the watch party is watching.</summary>
public interface IHostController
{
    HostStatus GetStatus();

    /// <summary>Sends pause/resume/skip/stop. Returns false if the host is gone or the command failed.</summary>
    Task<bool> SendAsync(VoteAction action, CancellationToken ct);

    /// <summary>Starts playing an item now. Returns false if the host or item is gone.</summary>
    Task<bool> PlayAsync(Guid itemId, CancellationToken ct);
}
