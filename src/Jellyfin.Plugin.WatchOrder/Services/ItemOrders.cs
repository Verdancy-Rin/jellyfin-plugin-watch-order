using Jellyfin.Plugin.WatchOrder.Models;

namespace Jellyfin.Plugin.WatchOrder.Services;

/// <summary>
/// Describes where a library item sits in an order, for the item's page.
/// </summary>
public static class ItemOrders
{
    /// <summary>
    /// Describes the item's place in one order for one user.
    /// </summary>
    /// <param name="order">The resolved order.</param>
    /// <param name="progress">The user's progress through it.</param>
    /// <param name="matches">Whether a step's item is the one asked about (or from the series/season asked about).</param>
    /// <param name="single">Whether the item is a single film or episode (so "what comes after it" makes sense).</param>
    /// <returns>The description, or null when the item isn't in the order.</returns>
    public static ItemOrderInfo? Describe(ResolvedWatchOrder order, UserProgress progress, Func<Guid, bool> matches, bool single)
    {
        var steps = progress.Steps;
        var hits = new List<int>();
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].Step.ItemId is { } id && steps[i].Available && matches(id))
            {
                hits.Add(i);
            }
        }

        if (hits.Count == 0)
        {
            return null;
        }

        var first = steps[hits[0]];
        ResolvedStep? after = null;
        if (single)
        {
            after = steps.Skip(hits[0] + 1)
                .FirstOrDefault(s => s.Step.ItemId is { } id && s.Available && !s.Skipped && !matches(id))?.Step;
        }

        return new ItemOrderInfo
        {
            OrderId = order.Id,
            Name = order.Name,
            Position = first.Step.Position,
            Total = order.Steps.Count,
            Count = hits.Count,
            Watched = hits.All(i => steps[i].Played),
            Skipped = hits.All(i => steps[i].Skipped),
            After = after,
            Next = progress.Next?.Step,
            WatchedCount = progress.WatchedCount,
            AvailableCount = progress.AvailableCount
        };
    }
}
