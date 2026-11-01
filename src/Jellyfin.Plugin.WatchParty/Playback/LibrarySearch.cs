using System.Globalization;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.WatchParty.Voting;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.WatchParty.Playback;

/// <summary>Searches movies and episodes in the Jellyfin library.</summary>
public sealed class LibrarySearch : ILibrarySearch
{
    private readonly ILibraryManager _library;

    public LibrarySearch(ILibraryManager library)
    {
        _library = library;
    }

    public IReadOnlyList<Nominee> Search(string query, int limit)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var items = _library.GetItemList(new InternalItemsQuery
        {
            SearchTerm = query.Trim(),
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
            Limit = limit,
            Recursive = true,
            IsVirtualItem = false,
        });

        return items.Select(i => new Nominee(i.Id, Label(i))).ToArray();
    }

    public string? GetTitle(Guid itemId)
    {
        var item = _library.GetItemById(itemId);
        return item is null ? null : Label(item);
    }

    private static string Label(BaseItem item)
    {
        if (item is Episode episode)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} S{1:00}E{2:00} {3}",
                episode.SeriesName,
                episode.ParentIndexNumber ?? 0,
                episode.IndexNumber ?? 0,
                episode.Name);
        }

        return item.ProductionYear is { } year
            ? string.Format(CultureInfo.InvariantCulture, "{0} ({1})", item.Name, year)
            : item.Name;
    }
}
