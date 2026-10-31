using Jellyfin.Plugin.WatchOrder.Models;
using Jellyfin.Plugin.WatchOrder.Services;
using Xunit;

namespace Jellyfin.Plugin.WatchOrder.Tests;

public sealed class ItemOrdersTests
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();
    private static readonly Guid C = Guid.NewGuid();
    private static readonly Guid D = Guid.NewGuid();

    private static (ResolvedWatchOrder Order, UserProgress Progress) Make(params (Guid? Id, bool Played, bool Skipped, bool Available)[] steps)
    {
        var order = new ResolvedWatchOrder { Id = "o", Name = "Order" };
        var progress = new UserProgress();
        foreach (var (id, played, skipped, available) in steps)
        {
            var step = new ResolvedStep { Position = order.Steps.Count + 1, ItemId = id, Label = "x" };
            order.Steps.Add(step);
            progress.Steps.Add(new UserStep { Step = step, Played = played, Skipped = skipped, Available = available });
        }

        progress.Next = progress.Steps.FirstOrDefault(s => s.Available && !s.Played && !s.Skipped);
        progress.WatchedCount = progress.Steps.Count(s => s.Played);
        progress.AvailableCount = progress.Steps.Count(s => s.Available && !s.Skipped);
        return (order, progress);
    }

    [Fact]
    public void Item_PlaceAndWhatComesAfter_SkippingWhatTheUserSkipsAndWhatsMissing()
    {
        var (order, progress) = Make((A, true, false, true), (B, false, true, true), (null, false, false, false), (C, false, false, true));
        var info = ItemOrders.Describe(order, progress, id => id == A, single: true)!;
        Assert.Equal(1, info.Position);
        Assert.Equal(4, info.Total);
        Assert.True(info.Watched);
        Assert.Equal(C, info.After!.ItemId);
        Assert.Equal(C, info.Next!.ItemId);
        Assert.Equal(1, info.WatchedCount);
    }

    [Fact]
    public void LastItem_HasNothingAfter()
    {
        var (order, progress) = Make((A, false, false, true), (B, false, false, true));
        Assert.Null(ItemOrders.Describe(order, progress, id => id == B, single: true)!.After);
    }

    [Fact]
    public void NotInTheOrder_IsNull()
    {
        var (order, progress) = Make((A, false, false, true));
        Assert.Null(ItemOrders.Describe(order, progress, id => id == D, single: true));
    }

    [Fact]
    public void Series_CountsItsEntries_FromTheFirst()
    {
        var (order, progress) = Make((D, false, false, true), (A, true, false, true), (C, false, false, true), (B, false, false, true));
        var info = ItemOrders.Describe(order, progress, id => id == A || id == B, single: false)!;
        Assert.Equal(2, info.Position);
        Assert.Equal(2, info.Count);
        Assert.False(info.Watched);
        Assert.Null(info.After);
    }

    [Fact]
    public void SkippedOptionalItem_IsMarkedSkipped()
    {
        var (order, progress) = Make((A, false, true, true), (B, false, false, true));
        var info = ItemOrders.Describe(order, progress, id => id == A, single: true)!;
        Assert.True(info.Skipped);
        Assert.Equal(B, info.After!.ItemId);
    }
}

public sealed class OrderStatsTests
{
    private static readonly DateTime Now = new(2026, 10, 31, 20, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(true, 10, 400, "Finished")]
    [InlineData(false, 0, null, "NotStarted")]
    [InlineData(false, 5, 3, "InProgress")]
    [InlineData(false, 5, 45, "Stalled")]
    [InlineData(false, 5, null, "Stalled")] // watched, but no date (e.g. marked watched before Jellyfin kept dates)
    public void Status(bool finished, int watched, int? daysAgo, string expected)
    {
        DateTime? last = daysAgo is { } d ? Now.AddDays(-d) : null;
        Assert.Equal(expected, Services.OrderStatsService.StatusOf(finished, watched, last, Now));
    }
}
