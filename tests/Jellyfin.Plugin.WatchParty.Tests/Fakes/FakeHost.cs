using Jellyfin.Plugin.WatchParty.Playback;
using Jellyfin.Plugin.WatchParty.Voting;

namespace Jellyfin.Plugin.WatchParty.Tests.Fakes;

public sealed class FakeHost : IHostController
{
    public HostStatus Status { get; set; } = new(true, "Alien", true);

    public bool PlayResult { get; set; } = true;

    public List<VoteAction> Sent { get; } = [];

    public List<Guid> Played { get; } = [];

    public HostStatus GetStatus() => Status;

    public Task<bool> SendAsync(VoteAction action, CancellationToken ct)
    {
        if (!Status.Online)
        {
            return Task.FromResult(false);
        }

        Sent.Add(action);
        return Task.FromResult(true);
    }

    public Task<bool> PlayAsync(Guid itemId, CancellationToken ct)
    {
        if (!Status.Online || !PlayResult)
        {
            return Task.FromResult(false);
        }

        Played.Add(itemId);
        return Task.FromResult(true);
    }
}
