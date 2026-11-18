using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.WatchOrder.Services;

/// <summary>
/// The library lookups the resolver needs, and nothing else. Jellyfin's real library sits behind
/// <see cref="JellyfinLibraryLookup"/>; tests use an in-memory library that follows the same rules.
/// </summary>
public interface ILibraryLookup
{
    /// <summary>Finds films or series with any of the given provider ids (exact provider name and value).</summary>
    /// <param name="kind">Movie or Series.</param>
    /// <param name="providerIds">Provider name to id.</param>
    /// <returns>Matching items.</returns>
    IReadOnlyList<BaseItem> FindByProviderIds(BaseItemKind kind, IReadOnlyDictionary<string, string> providerIds);

    /// <summary>
    /// Finds films or series by name. Jellyfin compares "clean" names: accents removed, lower case, and punctuation
    /// treated as spaces, so "Star Wars: The Clone Wars" and "Star Wars - The Clone Wars" are the same.
    /// </summary>
    /// <param name="kind">Movie or Series.</param>
    /// <param name="name">The name.</param>
    /// <returns>Matching items.</returns>
    IReadOnlyList<BaseItem> FindByName(BaseItemKind kind, string name);

    /// <summary>
    /// Finds every episode of a series that's in the library (not missing ones), in all its seasons. The resolver
    /// asks once per series and picks seasons and episodes out of the result, because one query per episode is far
    /// too slow on a big library.
    /// </summary>
    /// <param name="series">The series.</param>
    /// <returns>The episodes, in any order.</returns>
    IReadOnlyList<BaseItem> FindEpisodes(BaseItem series);
}

/// <summary>The resolver's lookups against Jellyfin's library.</summary>
public class JellyfinLibraryLookup : ILibraryLookup
{
    private readonly ILibraryManager _libraryManager;

    /// <summary>Initializes a new instance of the <see cref="JellyfinLibraryLookup"/> class.</summary>
    /// <param name="libraryManager">The library manager.</param>
    public JellyfinLibraryLookup(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    /// <inheritdoc />
    public IReadOnlyList<BaseItem> FindByProviderIds(BaseItemKind kind, IReadOnlyDictionary<string, string> providerIds)
        => _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { kind },
            Recursive = true,
            HasAnyProviderId = providerIds.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase),
            DtoOptions = Bare()
        });

    /// <inheritdoc />
    public IReadOnlyList<BaseItem> FindByName(BaseItemKind kind, string name)
        => _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { kind },
            Recursive = true,
            Name = name,
            DtoOptions = Bare()
        });

    /// <inheritdoc />
    public IReadOnlyList<BaseItem> FindEpisodes(BaseItem series)
    {
        // By the series' key, as Jellyfin's own series pages do: there's an index for it, unlike "under this series",
        // which reads through every episode in the library.
        var key = series.GetPresentationUniqueKey();
        if (!string.IsNullOrEmpty(key))
        {
            var byKey = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                SeriesPresentationUniqueKey = key,
                IsVirtualItem = false,
                DtoOptions = Bare()
            });

            // Series in different libraries can share a key; keep this one's episodes when they say which they're in.
            var own = byKey.Where(e => e is not Episode ep || ep.SeriesId == Guid.Empty || ep.SeriesId == series.Id).ToList();
            if (own.Count > 0)
            {
                return own;
            }
        }

        // No key, or nothing found by it (an older database, say): everything under the series, in one query.
        return _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            AncestorIds = new[] { series.Id },
            Recursive = true,
            IsVirtualItem = false,
            DtoOptions = Bare()
        });
    }

    /// <summary>
    /// Just the items' own fields. Matching only needs ids, names, years and episode numbers; by default Jellyfin also
    /// loads images, provider ids, settings and every user's watched data for each item, which is most of the work.
    /// </summary>
    private static DtoOptions Bare() => new(false);
}
