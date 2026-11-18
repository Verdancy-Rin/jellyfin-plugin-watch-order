using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.WatchOrder.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.WatchOrder.Tests;

/// <summary>
/// A small in-memory library for resolver tests. It follows Jellyfin 12.1's rules for the three lookups the resolver
/// makes (see <see cref="ILibraryLookup"/>): provider ids match on exact name and value, names match on Jellyfin's
/// "clean" form, and only episodes that are actually there (not missing ones) are returned.
/// </summary>
internal sealed class InMemoryLibrary : ILibraryLookup
{
    private readonly List<BaseItem> _items = new();

    public int SeriesLookups { get; private set; }

    public Movie Film(string name, int? year, params (string Provider, string Id)[] ids)
    {
        var film = new Movie { Id = Guid.NewGuid(), Name = name, ProductionYear = year };
        foreach (var (provider, id) in ids)
        {
            film.SetProviderId(provider, id);
        }

        _items.Add(film);
        return film;
    }

    public Series Show(string name, int? year, params (string Provider, string Id)[] ids)
    {
        var series = new Series { Id = Guid.NewGuid(), Name = name, ProductionYear = year };
        foreach (var (provider, id) in ids)
        {
            series.SetProviderId(provider, id);
        }

        _items.Add(series);
        return series;
    }

    /// <summary>Adds episodes <paramref name="numbers"/> of a season; <paramref name="missing"/> ones are known but not there.</summary>
    public List<Episode> Episodes(Series series, int season, IEnumerable<int> numbers, IEnumerable<int>? missing = null)
    {
        var added = new List<Episode>();
        foreach (var n in numbers)
        {
            added.Add(AddEpisode(series, season, n, false));
        }

        foreach (var n in missing ?? Array.Empty<int>())
        {
            AddEpisode(series, season, n, true);
        }

        return added;
    }

    public int FilmLookups { get; private set; }

    public string[] LastFilmPairs { get; private set; } = Array.Empty<string>();

    public int NameLookups { get; private set; }

    public IReadOnlyList<BaseItem> FindByProviderIds(BaseItemKind kind, IReadOnlyCollection<KeyValuePair<string, string>> providerIds)
    {
        if (kind == BaseItemKind.Series)
        {
            SeriesLookups++;
        }
        else
        {
            FilmLookups++;
            LastFilmPairs = providerIds.Select(p => $"{p.Key}={p.Value}").ToArray();
        }

        // Any one (provider, value) pair matching is enough, as in Jellyfin's HasAnyProviderIds.
        return Of(kind).Where(i => providerIds.Any(p => i.ProviderIds.Any(own => own.Key == p.Key && own.Value == p.Value))).Reverse().ToList();
    }

    public IReadOnlyList<BaseItem> FindByName(BaseItemKind kind, string name)
    {
        NameLookups++;
        if (kind == BaseItemKind.Series)
        {
            SeriesLookups++;
        }

        var clean = Clean(name);
        return Of(kind).Where(i => Clean(i.Name) == clean).ToList();
    }

    public int EpisodeLookups { get; private set; }

    public IReadOnlyList<BaseItem> FindEpisodes(BaseItem series)
    {
        EpisodeLookups++;
        return _items.OfType<Episode>()
            .Where(e => e.SeriesId == series.Id && !e.IsVirtualItem)
            .Reverse() // Jellyfin doesn't promise an order; the resolver sorts them
            .Cast<BaseItem>()
            .ToList();
    }

    // Jellyfin's StringExtensions.GetCleanValue: no accents, lower case, punctuation as spaces, spaces collapsed.
    internal static string Clean(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var noAccents = new string(decomposed.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
        var spaced = Regex.Replace(noAccents.ToLowerInvariant(), @"[^\p{L}\p{N}\s]", " ");
        return Regex.Replace(spaced, @"\s+", " ").Trim();
    }

    private Episode AddEpisode(Series series, int season, int number, bool missing)
    {
        var episode = new Episode
        {
            Id = Guid.NewGuid(),
            Name = $"{series.Name} {season}x{number}",
            SeriesId = series.Id,
            SeriesName = series.Name,
            ParentIndexNumber = season,
            IndexNumber = number,
            IsVirtualItem = missing
        };
        _items.Add(episode);
        return episode;
    }

    private IEnumerable<BaseItem> Of(BaseItemKind kind) => kind switch
    {
        BaseItemKind.Movie => _items.OfType<Movie>(),
        BaseItemKind.Series => _items.OfType<Series>(),
        _ => Enumerable.Empty<BaseItem>()
    };
}
