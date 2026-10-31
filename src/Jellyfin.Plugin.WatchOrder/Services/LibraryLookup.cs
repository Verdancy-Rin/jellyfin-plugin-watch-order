using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
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

    /// <summary>Finds a series' episodes that are in the library (not missing ones) in a season, or one episode.</summary>
    /// <param name="seriesId">The series.</param>
    /// <param name="season">The season number (0 for specials).</param>
    /// <param name="episode">One episode number, or null for the whole season.</param>
    /// <returns>The episodes, in any order.</returns>
    IReadOnlyList<BaseItem> FindEpisodes(Guid seriesId, int season, int? episode);
}

/// <summary>The resolver's lookups against Jellyfin's library.</summary>
public class JellyfinLibraryLookup : ILibraryLookup
{
    private readonly ILibraryManager _libraryManager;

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
            HasAnyProviderId = providerIds.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase)
        });

    /// <inheritdoc />
    public IReadOnlyList<BaseItem> FindByName(BaseItemKind kind, string name)
        => _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { kind },
            Recursive = true,
            Name = name
        });

    /// <inheritdoc />
    public IReadOnlyList<BaseItem> FindEpisodes(Guid seriesId, int season, int? episode)
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            AncestorIds = new[] { seriesId },
            Recursive = true,
            ParentIndexNumber = season,
            IsVirtualItem = false
        };

        if (episode is not null)
        {
            query.IndexNumber = episode;
        }

        return _libraryManager.GetItemList(query);
    }
}
