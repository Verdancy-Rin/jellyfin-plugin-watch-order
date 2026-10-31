using Jellyfin.Plugin.WatchOrder.Models;
using Jellyfin.Plugin.WatchOrder.Parsing;
using Jellyfin.Plugin.WatchOrder.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.WatchOrder.Tests;

public sealed class OptionalEntryTests : IDisposable
{
    private static readonly Guid E09 = Guid.NewGuid();
    private static readonly Guid Filler = Guid.NewGuid();
    private static readonly Guid E10 = Guid.NewGuid();
    private static readonly Guid E11 = Guid.NewGuid();

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wo-optional-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private static UserStep Step(int position, Guid? id, bool played = false, bool optional = false, bool skip = false, bool available = true) => new()
    {
        Step = new ResolvedStep { Position = position, ItemId = id, Optional = optional, Label = "Step " + position },
        Played = played,
        Available = id is not null && available,
        Skipped = skip && optional
    };

    [Fact]
    public void NotSkipping_OptionalEntriesCountLikeAnyOther()
    {
        var progress = new UserProgress();
        progress.Steps.AddRange(new[] { Step(1, E09, played: true), Step(2, Filler, optional: true), Step(3, E10) });
        WatchOrderProgressService.Tally(progress);
        Assert.Equal(3, progress.AvailableCount);
        Assert.Equal(1, progress.WatchedCount);
        Assert.Equal(Filler, progress.Next!.Step.ItemId);
        Assert.Equal(1, progress.OptionalCount);
        Assert.Equal(0, progress.SkippedCount);
    }

    [Fact]
    public void Skipping_OptionalEntriesDontCount_AndAreNeverNext()
    {
        var progress = new UserProgress();
        progress.Steps.AddRange(new[] { Step(1, E09, played: true), Step(2, Filler, optional: true, skip: true), Step(3, E10) });
        WatchOrderProgressService.Tally(progress);
        Assert.Equal(2, progress.AvailableCount);
        Assert.Equal(1, progress.WatchedCount);
        Assert.Equal(E10, progress.Next!.Step.ItemId);
        Assert.Equal(1, progress.SkippedCount);
    }

    [Fact]
    public void Skipping_OnlyOptionalLeft_IsFinished()
    {
        var progress = new UserProgress();
        progress.Steps.AddRange(new[] { Step(1, E09, played: true), Step(2, Filler, optional: true, skip: true) });
        WatchOrderProgressService.Tally(progress);
        Assert.Null(progress.Next);
        Assert.Equal(1, progress.WatchedCount);
        Assert.Equal(1, progress.AvailableCount);
    }

    [Fact]
    public void Autoplay_SkipsOptionalEntries_ToTheNextOneThatCounts()
    {
        // Order: E09, filler (optional, skipped), film-free E10. Jellyfin queued E10 already: leave it.
        var steps = new[] { new PlanStep(E09, false, true), new PlanStep(Filler, false, true, Skipped: true), new PlanStep(E10, false, true) };
        Assert.Empty(AutoplayPlanner.Plan(steps, E09, queuedNext: E10));
    }

    [Fact]
    public void Autoplay_JumpsOverAQueuedOptionalEpisode()
    {
        // The series' own queue would play the filler next; the order (skipping optional) wants E10.
        var steps = new[] { new PlanStep(E09, false, true), new PlanStep(Filler, false, true, Skipped: true), new PlanStep(E10, false, true), new PlanStep(E11, false, true) };
        Assert.Equal(new[] { E10, E11 }, AutoplayPlanner.Plan(steps, E09, queuedNext: Filler));
    }

    [Fact]
    public void Autoplay_NotSkipping_PlaysTheOptionalEntry()
    {
        var steps = new[] { new PlanStep(E09, false, true), new PlanStep(Filler, false, false), new PlanStep(E10, false, true) };
        Assert.Equal(new[] { Filler }, AutoplayPlanner.Plan(steps, E09, queuedNext: E10));
    }

    [Fact]
    public void Store_SkipOptional_IsPerUserAndPerOrder_AndSurvivesARestart()
    {
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var store = new UserStateStore(_dir, NullLogger<UserStateStore>.Instance);
        store.SetSkipOptional(alice, "naruto", true);
        Assert.True(store.GetSkipOptional(alice, "naruto"));
        Assert.False(store.GetSkipOptional(alice, "mcu"));
        Assert.False(store.GetSkipOptional(bob, "naruto"));

        var reloaded = new UserStateStore(_dir, NullLogger<UserStateStore>.Instance);
        Assert.True(reloaded.GetSkipOptional(alice, "naruto"));
        reloaded.SetSkipOptional(alice, "naruto", false);
        Assert.False(reloaded.GetSkipOptional(alice, "naruto"));
    }

    [Fact]
    public void OptionalFlag_IsReadFromJson()
    {
        Assert.True(WatchOrderParser.TryParse("""
            { "schemaVersion": 1, "id": "x", "name": "X", "entries": [
              { "type": "episode", "series": "S", "season": 1, "episode": 1 },
              { "type": "episodes", "series": "S", "season": 1, "from": 2, "to": 3, "optional": true }
            ] }
            """, out var def, out var errors), string.Join("; ", errors));
        Assert.False(def!.Entries[0].Optional);
        Assert.True(def.Entries[1].Optional);
    }
}

public sealed class DisplayNameTests
{
    [Fact]
    public void Episode_ShowsSeriesAndEpisodeTitle()
    {
        var episode = new MediaBrowser.Controller.Entities.TV.Episode { Name = "Gem Glow", SeriesName = "Steven Universe" };
        Assert.Equal("Steven Universe: Gem Glow", Services.WatchOrderResolver.DisplayNameFor("Steven Universe S01E01", "SU", episode));
    }

    [Fact]
    public void EpisodeWithoutATitle_OrAFilm_OrMissing_KeepsTheLabel()
    {
        var untitled = new MediaBrowser.Controller.Entities.TV.Episode { SeriesName = "Andor" };
        Assert.Equal("Andor S01E03", Services.WatchOrderResolver.DisplayNameFor("Andor S01E03", "Andor", untitled));
        Assert.Equal("Iron Man (2008)", Services.WatchOrderResolver.DisplayNameFor("Iron Man (2008)", "Iron Man", new MediaBrowser.Controller.Entities.Movies.Movie { Name = "Iron Man" }));
        Assert.Equal("Loki S01E01", Services.WatchOrderResolver.DisplayNameFor("Loki S01E01", "Loki", null));
    }

    [Fact]
    public void EpisodeWithoutASeriesName_UsesTheOrdersSeriesTitle()
    {
        var episode = new MediaBrowser.Controller.Entities.TV.Episode { Name = "Glorious Purpose" };
        Assert.Equal("Loki: Glorious Purpose", Services.WatchOrderResolver.DisplayNameFor("Loki S01E01", "Loki", episode));
    }
}
