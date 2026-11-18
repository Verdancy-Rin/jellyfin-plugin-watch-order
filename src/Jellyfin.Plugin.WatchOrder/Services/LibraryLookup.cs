using System.Collections.Concurrent;
using System.Diagnostics;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WatchOrder.Services;

/// <summary>
/// The library lookups the resolver needs, and nothing else. Jellyfin's real library sits behind
/// <see cref="JellyfinLibraryLookup"/> (with <see cref="CachedLibraryLookup"/> in front of it); tests use an in-memory
/// library that follows the same rules.
/// </summary>
public interface ILibraryLookup
{
    /// <summary>
    /// Finds films or series with any of the given provider ids (exact provider name and value), with their provider
    /// ids filled in so the caller can tell which item has which. The resolver asks once per order with every id in it:
    /// Jellyfin can't use an index for these, so each query reads through every film or series in the library.
    /// </summary>
    /// <param name="kind">Movie or Series.</param>
    /// <param name="providerIds">(provider name, id) pairs.</param>
    /// <returns>The items that have at least one of them, in any order.</returns>
    IReadOnlyList<BaseItem> FindByProviderIds(BaseItemKind kind, IReadOnlyCollection<KeyValuePair<string, string>> providerIds);

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
    // Provider pairs per query, so a very long order doesn't make one enormous SQL statement.
    private const int MaxPairsPerQuery = 300;

    private readonly ILibraryManager _libraryManager;

    /// <summary>Initializes a new instance of the <see cref="JellyfinLibraryLookup"/> class.</summary>
    /// <param name="libraryManager">The library manager.</param>
    public JellyfinLibraryLookup(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    /// <inheritdoc />
    public IReadOnlyList<BaseItem> FindByProviderIds(BaseItemKind kind, IReadOnlyCollection<KeyValuePair<string, string>> providerIds)
    {
        var found = new List<BaseItem>();
        var seen = new HashSet<Guid>();
        foreach (var chunk in providerIds.Distinct().Chunk(MaxPairsPerQuery))
        {
            var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { kind },
                Recursive = true,
                HasAnyProviderIds = chunk
                    .GroupBy(p => p.Key, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.Select(p => p.Value).Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal),
                DtoOptions = Bare(withProviderIds: true)
            });
            found.AddRange(items.Where(i => seen.Add(i.Id)));
        }

        return found;
    }

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
    /// Just the items' own fields (and their provider ids, when asked). Matching only needs ids, names, years and
    /// episode numbers. Jellyfin otherwise joins in images, every user's watched data and more for each item, which is
    /// most of the work; <c>new DtoOptions(false)</c> alone still includes images and user data.
    /// </summary>
    private static DtoOptions Bare(bool withProviderIds = false) => new(false)
    {
        EnableImages = false,
        EnableUserData = false,
        Fields = withProviderIds ? new[] { ItemFields.ProviderIds } : Array.Empty<ItemFields>()
    };
}

/// <summary>
/// Remembers library lookups until the library changes, so re-checking an order (every change in the order editor,
/// saving, re-matching, the ready-made list) doesn't repeat queries that read through the whole library.
/// </summary>
public sealed class CachedLibraryLookup : ILibraryLookup, IDisposable
{
    // A safety limit: past this many remembered lookups, start again rather than grow without end.
    private const int MaxEntries = 50_000;

    private readonly ILibraryLookup _inner;
    private readonly ILibraryManager? _libraryManager;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<string, BaseItem[]> _byProviderId = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, BaseItem[]> _byName = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, BaseItem[]> _episodes = new();
    private long _generation;

    /// <summary>Initializes a new instance of the <see cref="CachedLibraryLookup"/> class for Jellyfin's library.</summary>
    /// <param name="libraryManager">The library manager, whose changes clear what's remembered.</param>
    /// <param name="logger">The logger.</param>
    public CachedLibraryLookup(ILibraryManager libraryManager, ILogger<CachedLibraryLookup> logger)
        : this(new JellyfinLibraryLookup(libraryManager), libraryManager, logger)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="CachedLibraryLookup"/> class.</summary>
    /// <param name="inner">The lookups to remember.</param>
    /// <param name="libraryManager">The library manager, whose changes clear what's remembered; null in tests.</param>
    /// <param name="logger">The logger; null in tests.</param>
    internal CachedLibraryLookup(ILibraryLookup inner, ILibraryManager? libraryManager, ILogger? logger = null)
    {
        _inner = inner;
        _libraryManager = libraryManager;
        _logger = logger;
        if (libraryManager is not null)
        {
            libraryManager.ItemAdded += OnLibraryChanged;
            libraryManager.ItemUpdated += OnLibraryChanged;
            libraryManager.ItemRemoved += OnLibraryChanged;
        }
    }

    /// <summary>Forgets everything remembered. Called whenever anything in the library is added, changed or removed.</summary>
    public void Clear()
    {
        Interlocked.Increment(ref _generation);
        _byProviderId.Clear();
        _byName.Clear();
        _episodes.Clear();
    }

    /// <inheritdoc />
    public IReadOnlyList<BaseItem> FindByProviderIds(BaseItemKind kind, IReadOnlyCollection<KeyValuePair<string, string>> providerIds)
    {
        var generation = Interlocked.Read(ref _generation);
        var key = (KeyValuePair<string, string> p) => $"{kind}|{p.Key}|{p.Value}";
        var wanted = providerIds.Distinct().ToList();
        var missing = wanted.Where(p => !_byProviderId.ContainsKey(key(p))).ToList();
        IReadOnlyList<BaseItem> fetched = Array.Empty<BaseItem>();
        if (missing.Count > 0)
        {
            var timer = Stopwatch.StartNew();
            fetched = _inner.FindByProviderIds(kind, missing);
            SlowLog.Took(_logger, timer, "Looking up {Count} {Kind} provider ids in the library ({Known} already known, {Found} items found)", missing.Count, kind, wanted.Count - missing.Count, fetched.Count);
        }
        else
        {
            _logger?.LogDebug("All {Count} {Kind} provider ids were already known", wanted.Count, kind);
        }

        // Remember each pair asked about, including those nothing has, unless the library changed meanwhile.
        if (missing.Count > 0 && Interlocked.Read(ref _generation) == generation)
        {
            TrimIfFull();
            foreach (var pair in missing)
            {
                _byProviderId[key(pair)] = fetched.Where(i => HasProviderId(i, pair)).ToArray();
            }
        }

        var result = new List<BaseItem>(fetched);
        var seen = new HashSet<Guid>(fetched.Select(i => i.Id));
        foreach (var pair in wanted)
        {
            if (_byProviderId.TryGetValue(key(pair), out var items))
            {
                result.AddRange(items.Where(i => seen.Add(i.Id)));
            }
        }

        return result;
    }

    /// <inheritdoc />
    public IReadOnlyList<BaseItem> FindByName(BaseItemKind kind, string name)
        => Remember(_byName, $"{kind}|{name}", () =>
        {
            var timer = Stopwatch.StartNew();
            var found = _inner.FindByName(kind, name);
            SlowLog.Took(_logger, timer, "Looking up the {Kind} \"{Name}\" by title ({Found} found)", kind, name, found.Count);
            return found;
        });

    /// <inheritdoc />
    public IReadOnlyList<BaseItem> FindEpisodes(BaseItem series)
        => Remember(_episodes, series.Id, () =>
        {
            var timer = Stopwatch.StartNew();
            var found = _inner.FindEpisodes(series);
            SlowLog.Took(_logger, timer, "Looking up the episodes of {Series} ({Found} found)", series.Name, found.Count);
            return found;
        });

    /// <inheritdoc />
    public void Dispose()
    {
        if (_libraryManager is not null)
        {
            _libraryManager.ItemAdded -= OnLibraryChanged;
            _libraryManager.ItemUpdated -= OnLibraryChanged;
            _libraryManager.ItemRemoved -= OnLibraryChanged;
        }
    }

    /// <summary>Whether an item has a (provider, id) pair; provider names compared as Jellyfin's lookup does.</summary>
    internal static bool HasProviderId(BaseItem item, KeyValuePair<string, string> pair)
        => item.ProviderIds is { } ids && ids.TryGetValue(pair.Key, out var value) && string.Equals(value, pair.Value, StringComparison.Ordinal);

    private IReadOnlyList<BaseItem> Remember<TKey>(ConcurrentDictionary<TKey, BaseItem[]> cache, TKey key, Func<IReadOnlyList<BaseItem>> fetch)
        where TKey : notnull
    {
        if (cache.TryGetValue(key, out var hit))
        {
            return hit;
        }

        var generation = Interlocked.Read(ref _generation);
        var items = fetch().ToArray();
        if (Interlocked.Read(ref _generation) == generation)
        {
            TrimIfFull();
            cache[key] = items;
        }

        return items;
    }

    private void TrimIfFull()
    {
        if (_byProviderId.Count + _byName.Count + _episodes.Count > MaxEntries)
        {
            Clear();
        }
    }

    private void OnLibraryChanged(object? sender, ItemChangeEventArgs e)
    {
        // Logged once per change, not once per item: a library scan updates many items, and only the first finds
        // anything to forget.
        var count = _byProviderId.Count + _byName.Count + _episodes.Count;
        Clear();
        if (count > 0)
        {
            _logger?.LogDebug("The library changed ({Item}); forgot {Count} remembered lookups", e.Item?.Name, count);
        }
    }
}
