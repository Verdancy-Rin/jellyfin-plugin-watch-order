using Jellyfin.Plugin.WatchOrder.Models;
using Jellyfin.Plugin.WatchOrder.Parsing;
using Jellyfin.Plugin.WatchOrder.Services;
using Xunit;

namespace Jellyfin.Plugin.WatchOrder.Tests;

/// <summary>
/// The matching rules: how an order's entries become items in a library. Uses <see cref="InMemoryLibrary"/>, which
/// follows Jellyfin 12.1's lookup rules, so these test the plugin's logic rather than Jellyfin's database.
/// </summary>
public sealed class ResolverTests
{
    private readonly InMemoryLibrary _library = new();

    private ResolvedWatchOrder Resolve(string entriesJson, bool allowTitle = true)
    {
        var json = "{ \"id\": \"t\", \"name\": \"T\", \"entries\": [" + entriesJson + "] }";
        Assert.True(WatchOrderParser.TryParse(json, out var definition, out var errors), string.Join("; ", errors));
        return new WatchOrderResolver(_library).Resolve(definition!, allowTitle);
    }

    // ---------------------------------------------------------------- films

    [Fact]
    public void Film_ById_EvenWhenTheTitleDiffers()
    {
        var film = _library.Film("Iron Man (Director's Cut)", 2008, ("Tmdb", "1726"));
        var step = Assert.Single(Resolve("""{ "type": "movie", "title": "Iron Man", "year": 2008, "providerIds": { "Tmdb": "1726" } }""").Steps);
        Assert.Equal(film.Id, step.ItemId);
        Assert.Equal(MatchStatus.Matched, step.Status);
    }

    [Fact]
    public void Film_IdKeysInAnyCase()
    {
        var film = _library.Film("Thor", 2011, ("Tmdb", "10195"), ("Imdb", "tt0800369"));
        Assert.Equal(film.Id, Assert.Single(Resolve("""{ "type": "movie", "providerIds": { "TMDB": "10195" } }""").Steps).ItemId);
        Assert.Equal(film.Id, Assert.Single(Resolve("""{ "type": "movie", "providerIds": { "imdb": "tt0800369" } }""").Steps).ItemId);
    }

    [Fact]
    public void Film_AnyOneIdIsEnough()
    {
        // The order's TMDb id is wrong, but its IMDb id is right.
        var film = _library.Film("Thor", 2011, ("Tmdb", "10195"), ("Imdb", "tt0800369"));
        Assert.Equal(film.Id, Assert.Single(Resolve("""{ "type": "movie", "providerIds": { "Tmdb": "999", "Imdb": "tt0800369" } }""").Steps).ItemId);
    }

    [Fact]
    public void Film_IdBeatsATitleMatch()
    {
        _library.Film("Dune", 2021, ("Tmdb", "438631"));
        var older = _library.Film("Dune", 1984, ("Tmdb", "841"));
        var step = Assert.Single(Resolve("""{ "type": "movie", "title": "Dune", "year": 2021, "providerIds": { "Tmdb": "841" } }""").Steps);
        Assert.Equal(older.Id, step.ItemId);
    }

    [Fact]
    public void Film_CollectionIdsAreIgnored_SoTheyDontMatchTheWrongFilm()
    {
        // Every film in a collection shares its collection id; matching on it would pick whichever comes first.
        _library.Film("Star Wars: Episode I", 1999, ("TmdbCollection", "10"), ("Tmdb", "1893"));
        var ep2 = _library.Film("Star Wars: Episode II", 2002, ("TmdbCollection", "10"), ("Tmdb", "1894"));
        var step = Assert.Single(Resolve("""{ "type": "movie", "title": "Star Wars: Episode II", "year": 2002, "providerIds": { "TmdbCollection": "10" } }""").Steps);
        Assert.Equal(ep2.Id, step.ItemId);
        Assert.Equal(MatchStatus.MatchedByTitle, step.Status);
    }

    [Fact]
    public void Film_ByTitleAndYear_TellsRemakesApart()
    {
        var old = _library.Film("Dune", 1984);
        var remake = _library.Film("Dune", 2021);
        Assert.Equal(remake.Id, Assert.Single(Resolve("""{ "type": "movie", "title": "Dune", "year": 2021 }""").Steps).ItemId);
        Assert.Equal(old.Id, Assert.Single(Resolve("""{ "type": "movie", "title": "Dune", "year": 1984 }""").Steps).ItemId);
    }

    [Fact]
    public void Film_YearMayBeOffByOne_ButAnExactYearWins()
    {
        var a = _library.Film("Solaris", 1972);
        var b = _library.Film("Solaris", 2002);
        Assert.Equal(a.Id, Assert.Single(Resolve("""{ "type": "movie", "title": "Solaris", "year": 1971 }""").Steps).ItemId);
        Assert.Equal(b.Id, Assert.Single(Resolve("""{ "type": "movie", "title": "Solaris", "year": 2002 }""").Steps).ItemId);

        var c2008 = _library.Film("Twin Film", 2008);
        var c2009 = _library.Film("Twin Film", 2009);
        Assert.Equal(c2009.Id, Assert.Single(Resolve("""{ "type": "movie", "title": "Twin Film", "year": 2009 }""").Steps).ItemId);
        Assert.Equal(c2008.Id, Assert.Single(Resolve("""{ "type": "movie", "title": "Twin Film", "year": 2008 }""").Steps).ItemId);
    }

    [Fact]
    public void Film_YearTwoOffIsTooFar()
    {
        _library.Film("Solaris", 1972);
        var step = Assert.Single(Resolve("""{ "type": "movie", "title": "Solaris", "year": 1974 }""").Steps);
        Assert.Null(step.ItemId);
        Assert.Equal(MatchStatus.Missing, step.Status);
    }

    [Fact]
    public void Film_TitlesMatchLikeJellyfinDoes_IgnoringPunctuationCaseAndAccents()
    {
        var film = _library.Film("Star Wars - The Clone Wars", 2008);
        var amelie = _library.Film("Amélie", 2001);
        Assert.Equal(film.Id, Assert.Single(Resolve("""{ "type": "movie", "title": "star wars: the clone wars", "year": 2008 }""").Steps).ItemId);
        Assert.Equal(amelie.Id, Assert.Single(Resolve("""{ "type": "movie", "title": "Amelie", "year": 2001 }""").Steps).ItemId);
    }

    [Fact]
    public void Film_TitleMatchingTurnedOff_OnlyIdsCount()
    {
        _library.Film("Dune", 2021);
        Assert.Null(Assert.Single(Resolve("""{ "type": "movie", "title": "Dune", "year": 2021 }""", allowTitle: false).Steps).ItemId);
    }

    [Fact]
    public void Film_NotInTheLibrary_LeavesAPlaceholderWithItsLabel()
    {
        var resolved = Resolve("""{ "type": "movie", "title": "Not Here", "year": 2030, "note": "Coming soon" }""");
        var step = Assert.Single(resolved.Steps);
        Assert.Null(step.ItemId);
        Assert.Equal("Not Here (2030)", step.Label);
        Assert.Equal("Coming soon", step.Note);
        Assert.Equal(1, resolved.MissingCount);
    }

    // --------------------------------------------------------------- series

    [Fact]
    public void Series_SameNameDifferentYears()
    {
        var classic = _library.Show("Doctor Who", 1963);
        var modern = _library.Show("Doctor Who", 2005);
        _library.Episodes(classic, 1, new[] { 1 });
        var modernEps = _library.Episodes(modern, 1, new[] { 1 });
        var step = Assert.Single(Resolve("""{ "type": "episode", "series": "Doctor Who", "year": 2005, "season": 1, "episode": 1 }""").Steps);
        Assert.Equal(modernEps[0].Id, step.ItemId);
    }

    [Fact]
    public void Episode_ById_AndIsLabelledWithItsTitle()
    {
        var show = _library.Show("Andor", 2022, ("Tvdb", "393189"));
        var eps = _library.Episodes(show, 1, new[] { 1, 2, 3 });
        var step = Assert.Single(Resolve("""{ "type": "episode", "series": "Andor", "providerIds": { "Tvdb": "393189" }, "season": 1, "episode": 3 }""").Steps);
        Assert.Equal(eps[2].Id, step.ItemId);
        Assert.Equal("Andor S01E03", step.Label);
        Assert.Equal("Andor: Andor 1x3", step.DisplayName);
    }

    [Fact]
    public void Season_ExpandsToTheEpisodesThatAreThere_InOrder_SkippingMissingOnes()
    {
        var show = _library.Show("Steven Universe", 2013, ("Tvdb", "270701"));
        var eps = _library.Episodes(show, 1, new[] { 3, 1, 2, 5 }, missing: new[] { 4 });
        var steps = Resolve("""{ "type": "season", "series": "Steven Universe", "providerIds": { "Tvdb": "270701" }, "season": 1, "optional": true }""").Steps;
        Assert.Equal(new[] { eps[1].Id, eps[2].Id, eps[0].Id, eps[3].Id }, steps.Select(s => s.ItemId!.Value));
        Assert.Equal(new[] { 1, 2, 3, 4 }, steps.Select(s => s.Position));
        Assert.Equal(new[] { "Steven Universe S01E01", "Steven Universe S01E02", "Steven Universe S01E03", "Steven Universe S01E05" }, steps.Select(s => s.Label));
        Assert.All(steps, s => Assert.True(s.Optional));
        Assert.All(steps, s => Assert.Equal(0, s.EntryIndex));
    }

    [Fact]
    public void Season_WithNothingInTheLibrary_LeavesOnePlaceholder()
    {
        _library.Show("Steven Universe", 2013, ("Tvdb", "270701"));
        var step = Assert.Single(Resolve("""{ "type": "season", "series": "Steven Universe", "providerIds": { "Tvdb": "270701" }, "season": 2 }""").Steps);
        Assert.Null(step.ItemId);
        Assert.Equal("Steven Universe Season 2", step.Label);
    }

    [Fact]
    public void Specials_AreSeasonZero()
    {
        var show = _library.Show("Urusei Yatsura", 1981);
        var specials = _library.Episodes(show, 0, new[] { 1, 2 });
        _library.Episodes(show, 1, new[] { 1 });
        var steps = Resolve("""{ "type": "season", "series": "Urusei Yatsura", "year": 1981, "season": 0 }""").Steps;
        Assert.Equal(specials.Select(e => e.Id), steps.Select(s => s.ItemId!.Value));
    }

    [Fact]
    public void Range_HasOneStepPerEpisode_WithGapsWhereEpisodesAreMissing()
    {
        var show = _library.Show("The Clone Wars", 2008, ("Tvdb", "83268"));
        var eps = _library.Episodes(show, 7, new[] { 9, 10, 12 });
        var steps = Resolve("""{ "type": "episodes", "series": "The Clone Wars", "providerIds": { "Tvdb": "83268" }, "season": 7, "from": 9, "to": 12 }""").Steps;
        Assert.Equal(new Guid?[] { eps[0].Id, eps[1].Id, null, eps[2].Id }, steps.Select(s => s.ItemId));
        Assert.Equal(MatchStatus.Missing, steps[2].Status);
        Assert.Equal("The Clone Wars S07E11", steps[2].Label);
    }

    [Fact]
    public void Episode_OfASeriesThatIsntThere_IsMissing()
    {
        var step = Assert.Single(Resolve("""{ "type": "episode", "series": "Nowhere", "season": 1, "episode": 1 }""").Steps);
        Assert.Null(step.ItemId);
        Assert.Equal(MatchStatus.Missing, step.Status);
    }

    [Fact]
    public void Series_IsLookedUpOnce_HoweverManyEntriesUseIt()
    {
        var show = _library.Show("Andor", 2022, ("Tvdb", "393189"));
        _library.Episodes(show, 1, Enumerable.Range(1, 12));
        var steps = Resolve(string.Join(",", Enumerable.Range(1, 12).Select(n =>
            $$"""{ "type": "episode", "series": "Andor", "providerIds": { "Tvdb": "393189" }, "season": 1, "episode": {{n}} }""")));
        Assert.Equal(12, steps.Steps.Count(s => s.ItemId is not null));
        Assert.Equal(1, _library.SeriesLookups);
    }

    [Fact]
    public void Series_ByTitle_ShowsAsMatchedByTitle()
    {
        var show = _library.Show("Steven Universe", 2013);
        _library.Episodes(show, 1, new[] { 1 });
        var step = Assert.Single(Resolve("""{ "type": "episode", "series": "Steven Universe", "season": 1, "episode": 1 }""").Steps);
        Assert.Equal(MatchStatus.MatchedByTitle, step.Status);
    }

    // ----------------------------------------------------------- whole orders

    [Fact]
    public void MixedOrder_KeepsItsSequence_AndCountsWhatsMissing()
    {
        var ironMan = _library.Film("Iron Man", 2008, ("Tmdb", "1726"));
        var show = _library.Show("Agent Carter", 2015, ("Tmdb", "61550"));
        var s1 = _library.Episodes(show, 1, new[] { 1, 2 });
        var resolved = Resolve("""
            { "type": "season", "series": "Agent Carter", "providerIds": { "Tmdb": "61550" }, "season": 1 },
            { "type": "movie", "title": "Iron Man", "year": 2008, "providerIds": { "Tmdb": "1726" } },
            { "type": "movie", "title": "The Incredible Hulk", "year": 2008, "providerIds": { "Tmdb": "1724" } }
            """);
        Assert.Equal(new Guid?[] { s1[0].Id, s1[1].Id, ironMan.Id, null }, resolved.Steps.Select(s => s.ItemId));
        Assert.Equal(new[] { 0, 0, 1, 2 }, resolved.Steps.Select(s => s.EntryIndex));
        Assert.Equal(1, resolved.MissingCount);
    }

    [Fact]
    public void FromOrderFile_ToPlaylistAndAutoplay()
    {
        // An order with a film between two episodes, an optional special, and an episode that isn't in the library.
        var show = _library.Show("Steven Universe", 2013, ("Tvdb", "270701"));
        var eps = _library.Episodes(show, 1, new[] { 1, 2, 3 });
        var special = _library.Episodes(show, 0, new[] { 1 })[0];
        var film = _library.Film("Steven Universe: The Movie", 2019, ("Tmdb", "533592"));
        var resolved = Resolve("""
            { "type": "episodes", "series": "Steven Universe", "providerIds": { "Tvdb": "270701" }, "season": 1, "from": 1, "to": 2 },
            { "type": "episode", "series": "Steven Universe", "providerIds": { "Tvdb": "270701" }, "season": 0, "episode": 1, "optional": true },
            { "type": "movie", "title": "Steven Universe: The Movie", "year": 2019, "providerIds": { "Tmdb": "533592" } },
            { "type": "episode", "series": "Steven Universe", "providerIds": { "Tvdb": "270701" }, "season": 1, "episode": 4 },
            { "type": "episode", "series": "Steven Universe", "providerIds": { "Tvdb": "270701" }, "season": 1, "episode": 3 }
            """);

        // The user has watched episode 1 and skips optional entries.
        var watched = new HashSet<Guid> { eps[0].Id };
        var progress = new UserProgress();
        foreach (var step in resolved.Steps)
        {
            progress.Steps.Add(new UserStep
            {
                Step = step,
                Available = step.ItemId is not null,
                Played = step.ItemId is { } id && watched.Contains(id),
                Skipped = step.Optional
            });
        }

        // Their playlist: what's left, in order, without the special or the missing episode.
        Assert.Equal(new[] { eps[1].Id, film.Id, eps[2].Id }, PlaylistPlanner.Remaining(progress));

        // Autoplay: episode 2 starts with episode 3 queued after it (the player's usual next); the film goes in between.
        var plan = AutoplayPlanner.Plan(
            progress.Steps.Select(s => new PlanStep(s.Available ? s.Step.ItemId : null, s.Played, s.Step.Label.Contains(" S", StringComparison.Ordinal), s.Skipped)).ToList(),
            eps[1].Id,
            queuedNext: eps[2].Id);
        Assert.Equal(new[] { film.Id }, plan);
        Assert.DoesNotContain(special.Id, plan);
    }

    [Fact]
    public void BundledOrder_MatchesWhateverPartOfItIsInTheLibrary()
    {
        // The ready-made Phase One order against a library with three of its films, under different titles.
        var phaseOne = ReadyMadeOrders.Load(typeof(ReadyMadeOrders).Assembly).SelectMany(g => g.Orders).Single(o => o.Id == "mcu-phase-1");
        var ironMan = _library.Film("Iron Man 1", 2008, ("Tmdb", "1726"));
        var thor = _library.Film("THOR", 2011, ("Tmdb", "10195"));
        var avengers = _library.Film("Marvel's The Avengers", 2012, ("Tmdb", "24428"));

        var resolved = new WatchOrderResolver(_library).Resolve(phaseOne, allowTitle: false);
        var matched = resolved.Steps.Where(s => s.ItemId is not null).Select(s => s.ItemId!.Value).ToList();
        Assert.Equal(new[] { ironMan.Id, thor.Id, avengers.Id }, matched);
        Assert.Equal(phaseOne.Entries.Count - 3, resolved.MissingCount);
    }
}
