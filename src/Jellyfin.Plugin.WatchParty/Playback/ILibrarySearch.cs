using Jellyfin.Plugin.WatchParty.Voting;

namespace Jellyfin.Plugin.WatchParty.Playback;

/// <summary>Finds movies and episodes by name for /nominate autocomplete.</summary>
public interface ILibrarySearch
{
    IReadOnlyList<Nominee> Search(string query, int limit);

    /// <summary>Returns the display title for an item, or null if it no longer exists.</summary>
    string? GetTitle(Guid itemId);
}
