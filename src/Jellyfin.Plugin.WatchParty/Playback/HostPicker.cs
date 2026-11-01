namespace Jellyfin.Plugin.WatchParty.Playback;

/// <summary>A Jellyfin session that could be the host screen.</summary>
public sealed record HostCandidate(string SessionId, string DeviceName, bool RemoteControl, bool Playing, DateTime LastActivity);

/// <summary>Chooses the host screen: the configured device, or (when none is configured) whatever is playing.</summary>
public static class HostPicker
{
    public static HostCandidate? Pick(IEnumerable<HostCandidate> candidates, string configuredName)
    {
        var controllable = candidates.Where(c => c.RemoteControl);
        var name = configuredName.Trim();
        if (name.Length > 0)
        {
            return controllable.FirstOrDefault(c => string.Equals(c.DeviceName, name, StringComparison.OrdinalIgnoreCase));
        }

        return controllable
            .OrderByDescending(c => c.Playing)
            .ThenByDescending(c => c.LastActivity)
            .FirstOrDefault();
    }
}
