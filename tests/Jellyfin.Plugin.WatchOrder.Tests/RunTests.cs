using Jellyfin.Plugin.WatchOrder.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.WatchOrder.Tests;

public sealed class RunTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Before = Start.AddYears(-2);
    private static readonly DateTime After = Start.AddDays(1);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wo-runs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private static OrderRun FreshRun() => new()
    {
        Fresh = true,
        StartedAt = Start,
        PlayedAtStart = new Dictionary<string, bool> { ["seen"] = true, ["unseen"] = false }
    };

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void NotFresh_UsesJellyfinsWatchedFlag(bool played, bool expected)
    {
        Assert.Equal(expected, new OrderRun { StartedAt = Start }.IsWatched("seen", played, Before));
    }

    [Fact]
    public void Fresh_WatchedBeforeTheRun_DoesNotCount()
    {
        Assert.False(FreshRun().IsWatched("seen", played: true, lastPlayed: Before));
    }

    [Fact]
    public void Fresh_WatchedAgainToTheEnd_Counts()
    {
        // Jellyfin updates the last played date when playback starts, and sets watched again at the end.
        Assert.True(FreshRun().IsWatched("seen", played: true, lastPlayed: After));
    }

    [Fact]
    public void Fresh_StartedAgainButNotFinished_DoesNotCount()
    {
        // Starting playback clears Jellyfin's watched flag until it reaches the end.
        Assert.False(FreshRun().IsWatched("seen", played: false, lastPlayed: After));
    }

    [Fact]
    public void Fresh_UnwatchedAtStart_MarkedWatchedLater_Counts()
    {
        // Marking by hand keeps an old last played date when there is one, so the start snapshot is what tells.
        Assert.True(FreshRun().IsWatched("unseen", played: true, lastPlayed: Before));
    }

    [Fact]
    public void Fresh_ItemAddedAfterTheRunBegan_CountsOnlyIfWatchedSince()
    {
        var run = FreshRun();
        Assert.False(run.IsWatched("new", played: true, lastPlayed: Before));
        Assert.True(run.IsWatched("new", played: true, lastPlayed: After));
    }

    [Fact]
    public void Fresh_MarkedWatchedOnTheOrderPage_Counts()
    {
        var run = FreshRun();
        run.Marks["seen"] = new RunMark { Watched = true, At = After };
        Assert.True(run.IsWatched("seen", played: true, lastPlayed: Before));
        Assert.True(run.IsWatched("seen", played: false, lastPlayed: After)); // even if playback of it starts again
    }

    [Fact]
    public void Fresh_MarkedNotWatched_OnlyWatchingItAfterwardsCounts()
    {
        var run = FreshRun();
        run.Marks["unseen"] = new RunMark { Watched = false, At = After };
        Assert.False(run.IsWatched("unseen", played: true, lastPlayed: After.AddMinutes(-5)));
        Assert.True(run.IsWatched("unseen", played: true, lastPlayed: After.AddHours(2)));
    }

    [Fact]
    public void Store_FirstChoice_ContinueOrFresh()
    {
        var user = Guid.NewGuid();
        var store = new UserStateStore(_dir, NullLogger<UserStateStore>.Instance);
        Assert.Null(store.GetRun(user, "mcu"));

        store.ContinueRun(user, "mcu");
        var run = store.GetRun(user, "mcu")!;
        Assert.False(run.Fresh);
        Assert.Equal(1, run.Number);

        // Continue again does nothing.
        store.ContinueRun(user, "mcu");
        Assert.Equal(run.StartedAt, store.GetRun(user, "mcu")!.StartedAt);
    }

    [Fact]
    public void Store_StartOver_CountsAFinishedWatchThrough_AndCanBeUndone()
    {
        var user = Guid.NewGuid();
        var store = new UserStateStore(_dir, NullLogger<UserStateStore>.Instance);
        store.ContinueRun(user, "mcu");

        store.StartFresh(user, "mcu", new Dictionary<string, bool> { ["a"] = true }, finishedNow: true, Start);
        var second = store.GetRun(user, "mcu")!;
        Assert.True(second.Fresh);
        Assert.Equal(2, second.Number);
        Assert.True(second.PlayedAtStart["a"]);
        Assert.True(second.CanUndo);

        // Starting over part-way through doesn't count as another watch-through.
        store.StartFresh(user, "mcu", new Dictionary<string, bool>(), finishedNow: false, After);
        Assert.Equal(2, store.GetRun(user, "mcu")!.Number);

        // One step of undo, back to the run it replaced.
        Assert.True(store.UndoRun(user, "mcu"));
        Assert.Equal(Start, store.GetRun(user, "mcu")!.StartedAt);
        Assert.False(store.UndoRun(user, "mcu"));
    }

    [Fact]
    public void Store_UndoFirstFreshStart_GoesBackToNotChosen()
    {
        var user = Guid.NewGuid();
        var store = new UserStateStore(_dir, NullLogger<UserStateStore>.Instance);
        store.StartFresh(user, "mcu", new Dictionary<string, bool>(), finishedNow: false, Start);
        Assert.True(store.UndoRun(user, "mcu"));
        Assert.Null(store.GetRun(user, "mcu"));
    }

    [Fact]
    public void Store_FreshFromFinishedHistory_IsTheSecondWatchThrough()
    {
        var user = Guid.NewGuid();
        var store = new UserStateStore(_dir, NullLogger<UserStateStore>.Instance);
        store.StartFresh(user, "mcu", new Dictionary<string, bool>(), finishedNow: true, Start);
        Assert.Equal(2, store.GetRun(user, "mcu")!.Number);
    }

    [Fact]
    public void Store_Marks_OnlyInFreshRuns_AndRunsSurviveARestart()
    {
        var user = Guid.NewGuid();
        var store = new UserStateStore(_dir, NullLogger<UserStateStore>.Instance);
        store.ContinueRun(user, "a");
        store.MarkInRun(user, "a", "item", true, Start);
        Assert.Empty(store.GetRun(user, "a")!.Marks);

        store.StartFresh(user, "b", new Dictionary<string, bool> { ["item"] = true }, false, Start);
        store.MarkInRun(user, "b", "item", false, After);
        store.SetHomeState(user, "b", HomeState.Shown);

        var reloaded = new UserStateStore(_dir, NullLogger<UserStateStore>.Instance);
        var run = reloaded.GetRun(user, "b")!;
        Assert.True(run.Fresh);
        Assert.False(run.Marks["item"].Watched);
        Assert.Equal(After, run.Marks["item"].At);
        Assert.True(run.PlayedAtStart["item"]);
        Assert.Equal(HomeState.Shown, reloaded.GetHomeState(user, "b"));
        Assert.False(reloaded.GetRun(user, "a")!.Fresh);
    }

    [Fact]
    public void Store_ReadsFilesFromBeforeRunsExisted()
    {
        var user = Guid.NewGuid();
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, user.ToString("N") + ".json"), "{\"Home\":{\"mcu\":\"Hidden\"}}");
        var store = new UserStateStore(_dir, NullLogger<UserStateStore>.Instance);
        Assert.Equal(HomeState.Hidden, store.GetHomeState(user, "mcu"));
        Assert.Null(store.GetRun(user, "mcu"));
        store.ContinueRun(user, "mcu");
        Assert.Equal(HomeState.Hidden, store.GetHomeState(user, "mcu"));
    }
}
