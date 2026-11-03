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

        // Which series each step belongs to (the library's series id when it's there, else the order's name), for
        // making sections automatically; null for films.
        var seriesKeys = new List<string?>();
        var named = new List<OrderSection>();
        var lastEntry = -1;

        foreach (var step in WatchOrderParser.Expand(definition))
        {
            var entry = step.EntryIndex < definition.Entries.Count ? definition.Entries[step.EntryIndex] : null;
            var optional = (entry?.Optional ?? false, entry is { Optional: true, Recommended: true });
            if (step.EntryIndex != lastEntry && entry?.Section is { } heading && !string.IsNullOrWhiteSpace(heading))
            {
                named.Add(new OrderSection { Name = heading.Trim(), FirstPosition = resolved.Steps.Count + 1 });
            }

            lastEntry = step.EntryIndex;
            var countBefore = resolved.Steps.Count;
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

            string? key = null;
            if (step.Type != WatchOrderEntryType.Movie)
            {
                var (series, _) = FindSeries(step, allowTitle, seriesCache);
                key = series is not null ? series.Id.ToString("N") : "name:" + (step.Title ?? string.Empty).Trim().ToUpperInvariant();
            }

            for (var i = countBefore; i < resolved.Steps.Count; i++)
            {
                seriesKeys.Add(key);
            }
        }

        AssignSections(resolved, named, seriesKeys);

        if (definition.Image is { HasItem: true } image)
        {
            var kind = image.Type == "series" ? BaseItemKind.Series : BaseItemKind.Movie;
            var ids = new WatchOrderEntry { ProviderIds = image.ProviderIds ?? new Dictionary<string, string>() }.ProviderIds;
            resolved.ImageItemId = FindTopLevel(kind, image.Title, image.Year, ids, allowTitle).Item?.Id;
        }

        return resolved;
    }

    /// <summary>
    /// Puts each step in a section: the ones the order names; failing that, one per run of a series when the order
    /// is a few long runs of different series (films join the run they're in); failing that, one section with no
    /// heading.
    /// </summary>
    /// <param name="order">The resolved order.</param>
    /// <param name="named">The sections the order names, with their first positions.</param>
    /// <param name="seriesKeys">For each step, which series it's from; null for films.</param>
    internal static void AssignSections(ResolvedWatchOrder order, List<OrderSection> named, IReadOnlyList<string?> seriesKeys)
    {
        var steps = order.Steps;
        var sections = new List<OrderSection>();
        var starts = new List<int>(); // 0-based step index where each section starts

        if (named.Count > 0)
        {
            if (named[0].FirstPosition > 1)
            {
                sections.Add(new OrderSection { Name = null, FirstPosition = 1 });
                starts.Add(0);
            }

            foreach (var section in named)
            {
                // Two headings on entries with nothing between them: the later one wins.
                if (starts.Count > 0 && starts[^1] == section.FirstPosition - 1)
                {
                    sections.RemoveAt(sections.Count - 1);
                    starts.RemoveAt(starts.Count - 1);
                }

                sections.Add(section);
                starts.Add(section.FirstPosition - 1);
            }
        }
        else if (AutomaticSections(steps, seriesKeys) is { } runs)
        {
            foreach (var (start, name) in runs)
            {
                sections.Add(new OrderSection { Name = name, FirstPosition = start + 1 });
                starts.Add(start);
            }

            order.SectionsAutomatic = true;
        }
        else
        {
            sections.Add(new OrderSection { Name = null, FirstPosition = 1 });
            starts.Add(0);
        }

        for (var s = 0; s < sections.Count; s++)
        {
            var end = s + 1 < starts.Count ? starts[s + 1] : steps.Count;
            sections[s].StepCount = end - starts[s];
            for (var i = starts[s]; i < end; i++)
            {
                steps[i].SectionIndex = s;
            }
        }

        order.Sections = sections;
    }

    private static List<(int Start, string Name)>? AutomaticSections(List<ResolvedStep> steps, IReadOnlyList<string?> seriesKeys)
    {
        const int MaxSections = 12;
        var episodes = seriesKeys.Count(k => k is not null);
        if (steps.Count == 0 || episodes * 2 < steps.Count)
        {
            return null; // mostly films: there's no run of series to split on
        }

        var runs = new List<(int Start, string Key, string Name)>();
        for (var i = 0; i < steps.Count; i++)
        {
            if (seriesKeys[i] is not { } key || (runs.Count > 0 && runs[^1].Key == key))
            {
                continue;
            }

            if (runs.Any(r => r.Key == key) || runs.Count == MaxSections)
            {
                return null; // a series comes back after another one: the order hops between them
            }

            // A run starts at its first episode, or at the start of the order for the first run, so films before
            // a series' first episode stay with the run before.
            runs.Add((runs.Count == 0 ? 0 : i, key, steps[i].Series ?? steps[i].Label));
        }

        return runs.Count < 2 ? null : runs.Select(r => (r.Start, r.Name)).ToList();
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
            Recommended = optional.Recommended,
            Kind = step.Type == WatchOrderEntryType.Movie ? "Movie" : "Episode",
            Series = step.Type == WatchOrderEntryType.Movie ? null : (item as MediaBrowser.Controller.Entities.TV.Episode)?.SeriesName is { Length: > 0 } name ? name : step.Title,
            Season = step.Season,
            Episode = step.Type == WatchOrderEntryType.Season ? item?.IndexNumber : step.Episode
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
