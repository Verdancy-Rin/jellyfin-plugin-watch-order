namespace Jellyfin.Plugin.WatchOrder.Services;

/// <summary>One step of an order as the autoplay planner sees it.</summary>
/// <param name="ItemId">The library item, or null when it isn't in the library (or the user can't see it).</param>
/// <param name="Watched">Whether the user has watched it in their current watch-through.</param>
/// <param name="IsEpisode">Whether the item is an episode.</param>
/// <param name="Skipped">Whether it's an optional entry the user skips: never queued, and not "next".</param>
public readonly record struct PlanStep(Guid? ItemId, bool Watched, bool IsEpisode, bool Skipped = false);

/// <summary>
/// Decides what to put next in a player's queue so autoplay follows an order.
/// </summary>
public static class AutoplayPlanner
{
    /// <summary>The most items queued in one go.</summary>
    public const int MaxBlock = 20;

    /// <summary>
    /// Works out which items to insert straight after the one now playing, or none.
    /// </summary>
    /// <remarks>
    /// Only acts when the user is at the edge of the order: the next item in the order after the one playing hasn't
    /// been watched in this watch-through. Re-watching an old item leaves autoplay alone. Only acts when the player
    /// already has something queued next, so it never makes something autoplay that wouldn't have. Inserts the
    /// order's upcoming items up to the point where the order rejoins the existing queue. The web app expands a single
    /// queued episode into the rest of its series, so a lone episode gets the item after it too.
    /// </remarks>
    /// <param name="steps">The order's steps, in order.</param>
    /// <param name="playing">The item that just started.</param>
    /// <param name="queuedNext">The item the player will play next, or null when nothing is queued.</param>
    /// <returns>Items to insert, in order; empty to leave the queue alone.</returns>
    public static IReadOnlyList<Guid> Plan(IReadOnlyList<PlanStep> steps, Guid playing, Guid? queuedNext)
    {
        if (queuedNext is not { } next)
        {
            return Array.Empty<Guid>();
        }

        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].ItemId != playing)
            {
                continue;
            }

            var following = steps.Skip(i + 1).FirstOrDefault(s => s.ItemId is not null && s.ItemId != playing && !s.Skipped);
            if (following.ItemId is null || following.Watched)
            {
                continue; // end of the order, or not at the edge (re-watching)
            }

            var upcoming = new List<PlanStep>();
            var seen = new HashSet<Guid> { playing };
            foreach (var step in steps.Skip(i + 1))
            {
                if (step.ItemId is { } id && !step.Watched && !step.Skipped && seen.Add(id))
                {
                    upcoming.Add(step);
                }
            }

            if (upcoming[0].ItemId == next)
            {
                return Array.Empty<Guid>(); // already right
            }

            var block = upcoming.TakeWhile(s => s.ItemId != next).Take(MaxBlock).ToList();
            if (block.Count == 1 && block[0].IsEpisode && upcoming.Count > 1)
            {
                block.Add(upcoming[1]);
            }

            return block.Select(s => s.ItemId!.Value).ToList();
        }

        return Array.Empty<Guid>();
    }
}
