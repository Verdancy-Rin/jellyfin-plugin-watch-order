using Jellyfin.Plugin.WatchOrder.Models;
using Jellyfin.Plugin.WatchOrder.Parsing;
using Jellyfin.Plugin.WatchOrder.Services;
using Xunit;

namespace Jellyfin.Plugin.WatchOrder.Tests;

/// <summary>
/// Library lookups on a big library are slow (Jellyfin reads through every film or series for a provider-id query),
/// so an order asks once per kind, and lookups are remembered until the library changes.
/// </summary>
public sealed class LookupCacheTests
{
    private readonly InMemoryLibrary _library = new();

    private static WatchOrderDefinition Order(string entriesJson)
    {
        Assert.True(WatchOrderParser.TryParse($$"""{ "schemaVersion": 1, "id": "t", "name": "T", "entries": [ {{entriesJson}} ] }""", out var definition, out var errors), string.Join("; ", errors));
        return definition!;
    }

    private static string Films(int count, int missing = 0) => string.Join(",", Enumerable.Range(1, count + missing).Select(n =>
        $$"""{ "type": "movie", "title": "Film {{n}}", "year": 2000, "providerIds": { "Tmdb": "{{n}}" } }"""));

    private void AddFilms(int count)
    {
        for (var n = 1; n <= count; n++)
        {
            _library.Film($"Film {n}", 2000, ("Tmdb", n.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
    }

    [Fact]
    public void AnOrder_LooksUpAllItsFilmsAtOnce_AndAllItsSeriesAtOnce()
    {
        AddFilms(40);
        var a = _library.Show("A", 2010, ("Tvdb", "1"));
        var b = _library.Show("B", 2011, ("Tvdb", "2"));
        _library.Episodes(a, 1, Enumerable.Range(1, 3));
        _library.Episodes(b, 1, Enumerable.Range(1, 3));
        var order = Order(Films(40) + """
            , { "type": "season", "series": "A", "providerIds": { "Tvdb": "1" }, "season": 1 }
            , { "type": "season", "series": "B", "providerIds": { "Tvdb": "2" }, "season": 1 }
            """);

        var resolved = new WatchOrderResolver(_library).Resolve(order, allowTitle: false);

        Assert.Equal(46, resolved.Steps.Count(s => s.ItemId is not null));
        Assert.Equal(1, _library.FilmLookups);
        Assert.Equal(1, _library.SeriesLookups);
        Assert.Equal(2, _library.EpisodeLookups);
    }

    [Fact]
    public void EachEntry_GetsItsOwnFilm_FromTheSharedLookup()
    {
        var thor = _library.Film("Thor", 2011, ("Tmdb", "10195"));
        var ironMan = _library.Film("Iron Man", 2008, ("Imdb", "tt0371746"));
        var resolved = new WatchOrderResolver(_library).Resolve(Order("""
            { "type": "movie", "title": "Iron Man", "providerIds": { "Tmdb": "1726", "Imdb": "tt0371746" } },
            { "type": "movie", "title": "Thor", "providerIds": { "Tmdb": "10195" } },
            { "type": "movie", "title": "Not here", "providerIds": { "Tmdb": "999" } }
            """), allowTitle: false);
        Assert.Equal(new Guid?[] { ironMan.Id, thor.Id, null }, resolved.Steps.Select(s => s.ItemId));
        Assert.Equal(1, _library.FilmLookups);
    }

    [Fact]
    public void CheckingAgain_UsesWhatsRemembered_IncludingWhatsMissing()
    {
        AddFilms(10);
        var show = _library.Show("Show", 2010, ("Tvdb", "5"));
        _library.Episodes(show, 1, Enumerable.Range(1, 4));
        var cached = new CachedLibraryLookup(_library, null);
        var resolver = new WatchOrderResolver(cached);
        var order = Order(Films(10, missing: 3) + """, { "type": "season", "series": "Show", "providerIds": { "Tvdb": "5" }, "season": 1 }""");

        var first = resolver.Resolve(order, allowTitle: false);
        var lookups = (_library.FilmLookups, _library.SeriesLookups, _library.EpisodeLookups);
        var second = resolver.Resolve(order, allowTitle: false);

        Assert.Equal(lookups, (_library.FilmLookups, _library.SeriesLookups, _library.EpisodeLookups));
        Assert.Equal(first.Steps.Select(s => s.ItemId), second.Steps.Select(s => s.ItemId));
        Assert.Equal(3, second.MissingCount);
    }

    [Fact]
    public void AddingAnEntry_LooksUpOnlyTheNewFilm()
    {
        AddFilms(11);
        var cached = new CachedLibraryLookup(_library, null);
        var resolver = new WatchOrderResolver(cached);
        resolver.Resolve(Order(Films(10)), allowTitle: false);

        resolver.Resolve(Order(Films(11)), allowTitle: false);
        Assert.Equal(2, _library.FilmLookups);
        Assert.Equal(new[] { "Tmdb=11" }, _library.LastFilmPairs);
    }

    [Fact]
    public void SeveralOrders_ArePrefetchedTogether()
    {
        AddFilms(30);
        var cached = new CachedLibraryLookup(_library, null);
        var resolver = new WatchOrderResolver(cached);
        var orders = new[] { Order(Films(10)), Order(Films(20)), Order(Films(30)) };

        resolver.Prefetch(orders);
        var results = orders.Select(o => resolver.Resolve(o, allowTitle: false)).ToList();

        Assert.Equal(1, _library.FilmLookups);
        Assert.Equal(new[] { 0, 0, 0 }, results.Select(r => r.MissingCount));
    }

    [Fact]
    public void ALibraryChange_ForgetsEverything()
    {
        AddFilms(5);
        var cached = new CachedLibraryLookup(_library, null);
        var resolver = new WatchOrderResolver(cached);
        var order = Order(Films(6));
        Assert.Equal(1, resolver.Resolve(order, allowTitle: false).MissingCount);

        _library.Film("Film 6", 2000, ("Tmdb", "6"));
        Assert.Equal(1, resolver.Resolve(order, allowTitle: false).MissingCount); // remembered: still missing
        cached.Clear();
        Assert.Equal(0, resolver.Resolve(order, allowTitle: false).MissingCount);
    }

    [Fact]
    public void TitleMatching_StillWorks_AndIsRemembered()
    {
        var film = _library.Film("Old Film", 1990);
        var cached = new CachedLibraryLookup(_library, null);
        var resolver = new WatchOrderResolver(cached);
        var order = Order("""{ "type": "movie", "title": "Old Film", "year": 1990, "providerIds": { "Tmdb": "404" } }""");
        var step = Assert.Single(resolver.Resolve(order, allowTitle: true).Steps);
        Assert.Equal((film.Id, MatchStatus.MatchedByTitle), (step.ItemId!.Value, step.Status));
        var names = _library.NameLookups;
        resolver.Resolve(order, allowTitle: true);
        Assert.Equal(names, _library.NameLookups);
    }
}
