namespace Jellyfin.Plugin.WatchOrder.Models;

/// <summary>
/// One order on the user page: how far the user has got and what's next.
/// </summary>
public sealed class OrderProgressSummary
{
    /// <summary>Gets or sets the order id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the order name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the description.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets how many available items the user has watched.</summary>
    public int WatchedCount { get; set; }

    /// <summary>Gets or sets how many items are in the library and visible to the user.</summary>
    public int AvailableCount { get; set; }

    /// <summary>Gets or sets the total number of steps, including ones missing from the library but not skipped ones.</summary>
    public int TotalCount { get; set; }

    /// <summary>Gets or sets the user's home screen choice for this order.</summary>
    public Services.HomeState HomeState { get; set; }

    /// <summary>Gets or sets a value indicating whether the order is on the user's home screen now.</summary>
    public bool OnHomeScreen { get; set; }

    /// <summary>Gets or sets a value indicating whether the user skips optional entries in this order.</summary>
    public bool SkipOptional { get; set; }

    /// <summary>Gets or sets how many items are skipped because they're optional.</summary>
    public int SkippedCount { get; set; }

    /// <summary>Gets or sets the user's current watch-through.</summary>
    public RunInfo Run { get; set; } = new();

    /// <summary>Gets or sets the next unwatched item, or null when the user has finished.</summary>
    public ResolvedStep? Next { get; set; }
}
