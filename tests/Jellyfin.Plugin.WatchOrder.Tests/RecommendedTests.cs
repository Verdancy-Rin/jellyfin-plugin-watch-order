using Jellyfin.Plugin.WatchOrder.Models;
using Jellyfin.Plugin.WatchOrder.Parsing;
using Jellyfin.Plugin.WatchOrder.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.WatchOrder.Tests;

public sealed class RecommendedTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wo-rec-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    [Theory]
    [InlineData(OptionalMode.WatchAll, false, false, false)]
    [InlineData(OptionalMode.WatchAll, true, false, false)]
    [InlineData(OptionalMode.WatchAll, true, true, false)]
    [InlineData(OptionalMode.KeepRecommended, false, false, false)] // main story: never skipped
    [InlineData(OptionalMode.KeepRecommended, true, false, true)]   // plain filler: skipped
    [InlineData(OptionalMode.KeepRecommended, true, true, false)]   // recommended filler: kept
    [InlineData(OptionalMode.SkipAll, false, false, false)]
    [InlineData(OptionalMode.SkipAll, true, false, true)]
    [InlineData(OptionalMode.SkipAll, true, true, true)]
    public void WhatGetsSkipped(OptionalMode mode, bool optional, bool recommended, bool skipped)
    {
        Assert.Equal(skipped, UserStateStore.IsSkipped(mode, optional, recommended));
    }

    [Fact]
    public void Progress_KeepRecommended_CountsAndNext()
    {
        var steps = new[] { (false, false), (true, false), (true, true), (false, false) }
            .Select((s, i) => new UserStep
            {
                Step = new ResolvedStep { Position = i + 1, ItemId = Guid.NewGuid(), Optional = s.Item1, Recommended = s.Item2, Label = "x" },
                Available = true,
                Played = i == 0,
                Skipped = UserStateStore.IsSkipped(OptionalMode.KeepRecommended, s.Item1, s.Item2)
            }).ToList();
        var progress = new UserProgress { Steps = steps };
        WatchOrderProgressService.Tally(progress);

        Assert.Equal(3, progress.AvailableCount);              // the plain filler doesn't count
        Assert.Equal(steps[2].Step.ItemId, progress.Next!.Step.ItemId); // next is the recommended filler, not the skipped one
        Assert.Equal(1, progress.SkippedCount);
    }

    [Fact]
    public void Store_EarlierSkipChoice_BecomesSkipAll_AndIsReplacedByANewChoice()
    {
        var user = Guid.NewGuid();
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, user.ToString("N") + ".json"), "{\"SkipOptional\":{\"naruto\":true}}");

        var store = new UserStateStore(_dir, NullLogger<UserStateStore>.Instance);
        Assert.Equal(OptionalMode.SkipAll, store.GetOptionalMode(user, "naruto"));
        Assert.Equal(OptionalMode.WatchAll, store.GetOptionalMode(user, "other"));

        store.SetOptionalMode(user, "naruto", OptionalMode.KeepRecommended);
        var reloaded = new UserStateStore(_dir, NullLogger<UserStateStore>.Instance);
        Assert.Equal(OptionalMode.KeepRecommended, reloaded.GetOptionalMode(user, "naruto"));
        Assert.True(reloaded.GetSkipOptional(user, "naruto"));
        Assert.Contains("\"KeepRecommended\"", File.ReadAllText(Path.Combine(_dir, user.ToString("N") + ".json")), StringComparison.Ordinal);

        reloaded.SetSkipOptional(user, "naruto", false);
        Assert.Equal(OptionalMode.WatchAll, new UserStateStore(_dir, NullLogger<UserStateStore>.Instance).GetOptionalMode(user, "naruto"));
    }

    [Fact]
    public void Format_RecommendedNeedsOptional()
    {
        Assert.True(WatchOrderParser.TryParse("""{ "id": "x", "name": "X", "entries": [ { "type": "movie", "title": "A", "optional": true, "recommended": true } ] }""", out var def, out _));
        Assert.True(def!.Entries[0].Recommended);

        Assert.False(WatchOrderParser.TryParse("""{ "id": "x", "name": "X", "entries": [ { "type": "movie", "title": "A", "recommended": true } ] }""", out _, out var errors));
        Assert.Contains(errors, e => e.Contains("recommended only applies to optional entries", StringComparison.Ordinal));
    }

    [Fact]
    public void Resolver_CarriesRecommendedToEverySeasonEpisode()
    {
        var library = new InMemoryLibrary();
        var show = library.Show("Naruto Shippuden", 2007, ("Tvdb", "79824"));
        library.Episodes(show, 16, new[] { 1, 2, 3 });
        Assert.True(WatchOrderParser.TryParse("""
            { "id": "n", "name": "N", "entries": [
              { "type": "season", "series": "Naruto Shippuden", "providerIds": { "Tvdb": "79824" }, "season": 16, "optional": true, "recommended": true },
              { "type": "movie", "title": "Not Here", "optional": true }
            ] }
            """, out var def, out _));
        var steps = new WatchOrderResolver(library).Resolve(def!, allowTitle: true).Steps;
        Assert.All(steps.Take(3), s => Assert.True(s.Optional && s.Recommended));
        Assert.True(steps[3].Optional);
        Assert.False(steps[3].Recommended);
    }
}
