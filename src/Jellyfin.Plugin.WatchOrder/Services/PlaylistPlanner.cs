using Jellyfin.Plugin.WatchOrder.Models;

namespace Jellyfin.Plugin.WatchOrder.Services;

/// <summary>How to bring a playlist in line with what's left in an order.</summary>
public enum PlaylistChangeKind
{
    /// <summary>It's already right.</summary>
    None,

    /// <summary>Only some items need taking out (the usual case: something was just watched).</summary>
    Remove,

    /// <summary>Anything else: set its contents outright.</summary>
    Replace
}

/// <summary>A change to make to a playlist.</summary>
/// <param name="Kind">What sort of change.</param>
/// <param name="Remove">For <see cref="PlaylistChangeKind.Remove"/>, the items to take out.</param>
public readonly record struct PlaylistChange(PlaylistChangeKind Kind, IReadOnlyList<Guid> Remove);

/// <summary>
/// Works out what a user's playlist of an order should hold, and the smallest change to get there.
/// </summary>
public static class PlaylistPlanner
{
    /// <summary>The prefix of every playlist the plugin keeps, so they sort together.</summary>
    public const string NamePrefix = "Watch Order: ";

    /// <summary>Gets the playlist name for an order.</summary>
    /// <param name="orderName">The order's name.</param>
    /// <returns>The playlist name.</returns>
    public static string NameFor(string orderName) => NamePrefix + orderName;

    /// <summary>
    /// What's left for the user, in order: items in their library they haven't watched in this watch-through,
    /// leaving out optional entries they skip. An item listed twice appears once, at its first place.
    /// </summary>
    /// <param name="progress">The user's progress through the order.</param>
    /// <returns>Item ids, in order.</returns>
    public static IReadOnlyList<Guid> Remaining(UserProgress progress)
    {
        var seen = new HashSet<Guid>();
        var left = new List<Guid>();
        foreach (var step in progress.Steps)
        {
            if (step.Step.ItemId is { } id && step.Available && !step.Played && !step.Skipped && seen.Add(id))
            {
                left.Add(id);
            }
        }

        return left;
    }

    /// <summary>
    /// Compares a playlist's contents with what it should hold.
    /// </summary>
    /// <param name="current">What's in it now, in order; null when that isn't known.</param>
    /// <param name="wanted">What should be in it, in order, without repeats.</param>
    /// <returns>The change to make.</returns>
    public static PlaylistChange Compare(IReadOnlyList<Guid>? current, IReadOnlyList<Guid> wanted)
    {
        if (current is null)
        {
            return new PlaylistChange(PlaylistChangeKind.Replace, Array.Empty<Guid>());
        }

        if (current.SequenceEqual(wanted))
        {
            return new PlaylistChange(PlaylistChangeKind.None, Array.Empty<Guid>());
        }

        // Taking items out works when what's wanted is what's there, in the same order, minus some items, and
        // nothing is in there twice (removing an item takes out every copy of it).
        if (current.Distinct().Count() == current.Count && IsSubsequence(wanted, current))
        {
            var keep = wanted.ToHashSet();
            return new PlaylistChange(PlaylistChangeKind.Remove, current.Where(id => !keep.Contains(id)).ToList());
        }

        return new PlaylistChange(PlaylistChangeKind.Replace, Array.Empty<Guid>());
    }

    private static bool IsSubsequence(IReadOnlyList<Guid> part, IReadOnlyList<Guid> whole)
    {
        var i = 0;
        foreach (var id in whole)
        {
            if (i < part.Count && part[i] == id)
            {
                i++;
            }
        }

        return i == part.Count;
    }
}
