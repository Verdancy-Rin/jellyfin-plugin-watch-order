using Jellyfin.Plugin.WatchOrder.Services;
using Xunit;

namespace Jellyfin.Plugin.WatchOrder.Tests;

public sealed class AutoplayPlannerTests
{
    private static readonly Guid E09 = Guid.NewGuid();
    private static readonly Guid E10 = Guid.NewGuid();
    private static readonly Guid E11 = Guid.NewGuid();
    private static readonly Guid Film = Guid.NewGuid();
    private static readonly Guid Future1 = Guid.NewGuid();
    private static readonly Guid Future2 = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();

    private static PlanStep Ep(Guid id, bool watched = false) => new(id, watched, true);

    private static PlanStep Movie(Guid id, bool watched = false) => new(id, watched, false);

    [Fact]
    public void FilmBetweenEpisodes_IsQueuedBeforeTheNextEpisode()
    {
        var steps = new[] { Ep(E09), Movie(Film), Ep(E10), Ep(E11) };
        Assert.Equal(new[] { Film }, AutoplayPlanner.Plan(steps, E09, queuedNext: E10));
    }

    [Fact]
    public void AlreadyQueuedRight_DoesNothing()
    {
        var steps = new[] { Ep(E09), Ep(E10), Ep(E11) };
        Assert.Empty(AutoplayPlanner.Plan(steps, E09, queuedNext: E10));
    }

    [Fact]
    public void NothingQueued_DoesNothing()
    {
        // Autoplay was off, or it was the last thing queued: never start something that wouldn't have played.
        var steps = new[] { Ep(E09), Movie(Film), Ep(E10) };
        Assert.Empty(AutoplayPlanner.Plan(steps, E09, queuedNext: null));
    }

    [Fact]
    public void LoneEpisode_GetsTheItemAfterIt_SoThePlayerDoesntExpandItIntoItsSeries()
    {
        var steps = new[] { Ep(E09), Ep(Future1), Ep(E10), Ep(E11) };
        Assert.Equal(new[] { Future1, E10 }, AutoplayPlanner.Plan(steps, E09, queuedNext: E10));
    }

    [Fact]
    public void QueuesUpToWhereTheOrderRejoinsTheQueue()
    {
        var steps = new[] { Ep(E09), Ep(Future1), Ep(Future2), Movie(Film), Ep(E10) };
        Assert.Equal(new[] { Future1, Future2, Film }, AutoplayPlanner.Plan(steps, E09, queuedNext: E10));
    }

    [Fact]
    public void QueueGoesSomewhereElseEntirely_QueuesTheOrderAhead_UpToALimit()
    {
        var steps = new List<PlanStep> { Ep(E09) };
        for (var i = 0; i < 30; i++)
        {
            steps.Add(Ep(Guid.NewGuid()));
        }

        var plan = AutoplayPlanner.Plan(steps, E09, queuedNext: Other);
        Assert.Equal(AutoplayPlanner.MaxBlock, plan.Count);
        Assert.Equal(steps[1].ItemId, plan[0]);
    }

    [Fact]
    public void RewatchingAnOldItem_LeavesAutoplayAlone()
    {
        var steps = new[] { Ep(E09, watched: true), Movie(Film, watched: true), Ep(E10), Ep(E11) };
        Assert.Empty(AutoplayPlanner.Plan(steps, E09, queuedNext: E10));
    }

    [Fact]
    public void SkipsWatchedAndMissingItems()
    {
        // Missing from the library (null) and already-watched items further on are skipped.
        var steps = new[] { Ep(E09), new PlanStep(null, false, false), Movie(Film), Ep(Future1, watched: true), Ep(E10) };
        Assert.Equal(new[] { Film }, AutoplayPlanner.Plan(steps, E09, queuedNext: E10));
    }

    [Fact]
    public void LastItemInTheOrder_DoesNothing()
    {
        var steps = new[] { Ep(E09), Movie(Film) };
        Assert.Empty(AutoplayPlanner.Plan(steps, Film, queuedNext: E10));
    }

    [Fact]
    public void ItemNotInTheOrder_DoesNothing()
    {
        Assert.Empty(AutoplayPlanner.Plan(new[] { Ep(E09), Movie(Film) }, Other, queuedNext: E10));
    }

    [Fact]
    public void ItemListedTwice_UsesTheOccurrenceAtTheEdge()
    {
        // E09 appears early (already past) and again later at the edge of what's been watched.
        var steps = new[] { Ep(E09), Ep(E10, watched: true), Movie(Film, watched: true), Ep(E09), Ep(Future1), Ep(Future2) };
        Assert.Equal(new[] { Future1, Future2 }, AutoplayPlanner.Plan(steps, E09, queuedNext: E10));
    }

    [Theory]
    [InlineData("OptIn", null, false)]
    [InlineData("OptIn", true, true)]
    [InlineData("OptOut", null, true)]
    [InlineData("OptOut", false, false)]
    [InlineData("Off", true, false)]
    [InlineData(null, true, true)]
    [InlineData("nonsense", null, true)] // unknown: the default, on unless turned off
    public void AutoplaySetting(string? mode, bool? userChoice, bool expected)
    {
        Assert.Equal(expected, UserStateStore.AutoplayFollowsFor(mode, userChoice));
    }
}

public sealed class QueuedNextTests
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();
    private static readonly Guid C = Guid.NewGuid();

    private static MediaBrowser.Model.Session.QueueItem Q(Guid id, string entry) => new() { Id = id, PlaylistItemId = entry };

    [Fact]
    public void UsesThePlayingQueueEntry_EvenWhenTheSameItemIsQueuedTwice()
    {
        var queue = new[] { Q(A, "p1"), Q(B, "p2"), Q(A, "p3"), Q(C, "p4") };
        Assert.Equal(C, AutoplayFollower.QueuedNext(queue, "p3", A));
        Assert.Equal(B, AutoplayFollower.QueuedNext(queue, "p1", A));
    }

    [Fact]
    public void WithoutAnEntryId_UsesTheItem()
    {
        Assert.Equal(B, AutoplayFollower.QueuedNext(new[] { Q(A, "x"), Q(B, "y") }, null, A));
    }

    [Fact]
    public void LastInQueue_OrNoQueue_IsNull()
    {
        Assert.Null(AutoplayFollower.QueuedNext(new[] { Q(A, "p1"), Q(B, "p2") }, "p2", B));
        Assert.Null(AutoplayFollower.QueuedNext(null, null, A));
        Assert.Null(AutoplayFollower.QueuedNext(Array.Empty<MediaBrowser.Model.Session.QueueItem>(), null, A));
    }
}
