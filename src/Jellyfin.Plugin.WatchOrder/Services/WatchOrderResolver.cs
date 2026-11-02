using Jellyfin.Data.Enums;
using Jellyfin.Plugin.WatchOrder.Models;
using Jellyfin.Plugin.WatchOrder.Parsing;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.WatchOrder.Services;

/// <summary>
/// Matches the steps of an order to items in the Jellyfin library.
/// Matching order: provider ids first, then (if enabled) title + year.
/// </summary>
public class WatchOrderResolver
{
    private readonly ILibraryLookup _library;

    public WatchOrderResolver(ILibraryLookup library)
    {
        _library = library;
    }

    /// <summary>
    /// Resolves a definition against the library.
    /// </summary>
    /// <param name="definition">A valid definition.</param>
    /// <returns>The resolved order.</returns>
    public ResolvedWatchOrder Resolve(WatchOrderDefinition definition)
        => Resolve(definition, Plugin.Instance?.Configuration.AllowTitleMatching ?? true);

    /// <summary>
    /// Resolves a definition against the library.
    /// </summary>
    /// <param name="definition">A valid definition.</param>
    /// <param name="allowTitle">Whether entries may fall back to matching on title and year.</param>
    /// <returns>The resolved order.</returns>
    public ResolvedWatchOrder Resolve(WatchOrderDefinition definition, bool allowTitle)
    {
        var resolved = new ResolvedWatchOrder
        {
            Id = definition.Id,
            Name = definition.Name,
            Description = definition.Description,
            ResolvedAtUtc = DateTime.UtcNow
        };

        // Series are often referenced many times in one order; look each one up once.
        var seriesCache = new Dictionary<string, (BaseItem? Item, MatchStatus Status)>(StringComparer.OrdinalIgnoreCase);

        foreach (var step in WatchOrderParser.Expand(definition))
        {
            var entry = step.EntryIndex < definition.Entries.Count ? definition.Entries[step.EntryIndex] : null;
            var optional = (entry?.Optional ?? false, entry is { Optional: true, Recommended: true });
            switch (step.Type)
            {
                case WatchOrderEntryType.Movie:
                {
                    var (item, status) = FindTopLevel(BaseItemKind.Movie, step.Title, step.Year, step.ProviderIds, allowTitle);
                    Add(resolved, step, step.Label, item, status, optional);
                    break;
                }

                case WatchOrderEntryType.Episode:
                {
                    var (series, status) = FindSeries(step, allowTitle, seriesCache);
                    var episode = series is null ? null : FindEpisodes(series, step.Season!.Value, step.Episode).FirstOrDefault();
                    Add(resolved, step, step.Label, episode, episode is null ? MatchStatus.Missing : status, optional);
                    break;
                }

                case WatchOrderEntryType.Season:
                {
                    var (series, status) = FindSeries(step, allowTitle, seriesCache);
                    var episodes = series is null ? new List<BaseItem>() : FindEpisodes(series, step.Season!.Value, null);

                    if (episodes.Count == 0)
                    {
                        // Keep a placeholder so the gap is visible in the UI.
                        Add(resolved, step, step.Label, null, MatchStatus.Missing, optional);
                    }

                    foreach (var ep in episodes)
                    {
                        Add(resolved, step, $"{step.Title} S{step.Season:00}E{ep.IndexNumber:00}", ep, status, optional);
                    }

                    break;
                }
            }
        }

        return resolved;
    }

    /// <summary>
    /// Gets the name to show for a step: "Series: Episode title" for an episode with a title, otherwise the label.
    /// </summary>
    /// <param name="label">The step's label, e.g. <c>Andor S01E03</c>.</param>
    /// <param name="seriesTitle">The series name from the order, used if the library item has none.</param>
    /// <param name="item">The matched library item, if any.</param>
    /// <returns>The name to show.</returns>
    public static string DisplayNameFor(string label, string? seriesTitle, BaseItem? item)
    {
        if (item is MediaBrowser.Controller.Entities.TV.Episode episode && !string.IsNullOrWhiteSpace(episode.Name))
        {
            var series = string.IsNullOrWhiteSpace(episode.SeriesName) ? seriesTitle : episode.SeriesName;
            return string.IsNullOrWhiteSpace(series) ? episode.Name : $"{series}: {episode.Name}";
        }

        return label;
    }

    private static void Add(ResolvedWatchOrder order, WatchOrderStep step, string label, BaseItem? item, MatchStatus status, (bool Optional, bool Recommended) optional)
    {
        order.Steps.Add(new ResolvedStep
        {
            Position = order.Steps.Count + 1,
            EntryIndex = step.EntryIndex,
            Label = label,
            DisplayName = DisplayNameFor(label, step.Title, item),
            ItemId = item?.Id,
            Status = item is null ? MatchStatus.Missing : status,
            Note = step.Note,
            Optional = optional.Optional,
            Recommended = optional.Recommended
        });
    }

    private (BaseItem? Item, MatchStatus Status) FindSeries(
        WatchOrderStep step,
        bool allowTitle,
        Dictionary<string, (BaseItem? Item, MatchStatus Status)> cache)
    {
        var key = string.Join('|', step.ProviderIds.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}")) + "|" + step.Title + "|" + step.Year;
        if (!cache.TryGetValue(key, out var hit))
        {
            hit = FindTopLevel(BaseItemKind.Series, step.Title, step.Year, step.ProviderIds, allowTitle);
            cache[key] = hit;
        }

        return hit;
    }

    private (BaseItem? Item, MatchStatus Status) FindTopLevel(
        BaseItemKind kind,
        string? title,
        int? year,
        IReadOnlyDictionary<string, string> providerIds,
        bool allowTitle)
    {
        if (providerIds.Count > 0)
        {
            var byId = _library.FindByProviderIds(kind, providerIds).FirstOrDefault();

            if (byId is not null)
            {
                return (byId, MatchStatus.Matched);
            }
        }

        if (allowTitle && !string.IsNullOrWhiteSpace(title))
        {
            var candidates = _library.FindByName(kind, title);

            // Prefer an exact year match; allow +/-1 because release and first-air years often disagree between sources.
            var byTitle = candidates.FirstOrDefault(c => year is null || c.ProductionYear == year)
                ?? candidates.FirstOrDefault(c => year is not null && c.ProductionYear is not null && Math.Abs(c.ProductionYear.Value - year.Value) <= 1);

            if (byTitle is not null)
            {
                return (byTitle, MatchStatus.MatchedByTitle);
            }
        }

        return (null, MatchStatus.Missing);
    }

    private List<BaseItem> FindEpisodes(BaseItem series, int season, int? episode)
    {
        return _library.FindEpisodes(series.Id, season, episode)
            .OrderBy(e => e.IndexNumber ?? int.MaxValue)
            .ToList();
    }
}
