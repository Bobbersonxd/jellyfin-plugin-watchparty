using Jellyfin.Plugin.WatchParty.Configuration;
using Jellyfin.Plugin.WatchParty.Voting;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WatchParty.Playback;

/// <summary>Controls the configured host session through Jellyfin's session manager.</summary>
public sealed class HostController : IHostController
{
    private readonly ISessionManager _sessions;
    private readonly ILibraryManager _library;
    private readonly Func<PluginConfiguration> _config;
    private readonly ILogger<HostController> _log;

    public HostController(ISessionManager sessions, ILibraryManager library, Func<PluginConfiguration> config, ILogger<HostController> log)
    {
        _sessions = sessions;
        _library = library;
        _config = config;
        _log = log;
    }

    public HostStatus GetStatus()
    {
        var session = FindHost();
        if (session is null)
        {
            return new HostStatus(false, null, false);
        }

        var queue = session.NowPlayingQueue;
        var hasNext = false;
        if (queue is { Count: > 0 } && session.PlaylistItemId is { } current)
        {
            var index = queue.ToList().FindIndex(q => q.PlaylistItemId == current);
            hasNext = index >= 0 && index < queue.Count - 1;
        }

        return new HostStatus(true, session.NowPlayingItem?.Name, hasNext, session.DeviceName);
    }

    public async Task<bool> SendAsync(VoteAction action, CancellationToken ct)
    {
        var session = FindHost();
        if (session is null)
        {
            return false;
        }

        var command = action switch
        {
            VoteAction.Pause => PlaystateCommand.Pause,
            VoteAction.Resume => PlaystateCommand.Unpause,
            VoteAction.Skip => PlaystateCommand.NextTrack,
            VoteAction.Stop => PlaystateCommand.Stop,
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Not a playstate action"),
        };

        try
        {
            await _sessions.SendPlaystateCommand(null, session.Id, new PlaystateRequest { Command = command }, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Watch Party could not send {Command} to host {Device}", command, session.DeviceName);
            return false;
        }
    }

    public async Task<bool> PlayAsync(Guid itemId, CancellationToken ct)
    {
        var session = FindHost();
        if (session is null || _library.GetItemById(itemId) is null)
        {
            return false;
        }

        try
        {
            var request = new PlayRequest { ItemIds = [itemId], PlayCommand = PlayCommand.PlayNow };
            await _sessions.SendPlayCommand(null, session.Id, request, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Watch Party could not play {ItemId} on host {Device}", itemId, session.DeviceName);
            return false;
        }
    }

    private SessionInfo? FindHost()
    {
        var sessions = _sessions.Sessions.ToArray();
        var picked = HostPicker.Pick(
            sessions.Select(s => new HostCandidate(s.Id, s.DeviceName ?? string.Empty, s.SupportsRemoteControl, s.NowPlayingItem is not null, s.LastActivityDate)),
            _config().HostDeviceName ?? string.Empty);
        return picked is null ? null : sessions.FirstOrDefault(s => s.Id == picked.SessionId);
    }
}
