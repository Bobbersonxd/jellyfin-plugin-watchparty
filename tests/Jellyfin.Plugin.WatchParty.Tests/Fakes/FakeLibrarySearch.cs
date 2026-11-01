using Jellyfin.Plugin.WatchParty.Playback;
using Jellyfin.Plugin.WatchParty.Voting;

namespace Jellyfin.Plugin.WatchParty.Tests.Fakes;

public sealed class FakeLibrarySearch : ILibrarySearch
{
    public List<Nominee> Items { get; } = [];

    public IReadOnlyList<Nominee> Search(string query, int limit) =>
        Items.Where(i => i.Title.Contains(query, StringComparison.OrdinalIgnoreCase)).Take(limit).ToArray();

    public string? GetTitle(Guid itemId) => Items.FirstOrDefault(i => i.ItemId == itemId)?.Title;
}
