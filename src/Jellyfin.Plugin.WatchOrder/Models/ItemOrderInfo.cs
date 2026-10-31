namespace Jellyfin.Plugin.WatchOrder.Models;

/// <summary>
/// One order a library item belongs to, for the "Watch orders" section of the item's page.
/// For a series or season, it describes the order's entries from that series or season.
/// </summary>
public sealed class ItemOrderInfo
{
    /// <summary>Gets or sets the order id.</summary>
    public string OrderId { get; set; } = string.Empty;

    /// <summary>Gets or sets the order name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the item's place in the order (1-based), or the first entry from the series/season.</summary>
    public int Position { get; set; }

    /// <summary>Gets or sets how many entries the order has.</summary>
    public int Total { get; set; }

    /// <summary>Gets or sets how many entries in the order are this item (or from this series/season).</summary>
    public int Count { get; set; }

    /// <summary>Gets or sets a value indicating whether the user has watched it in their current watch-through.</summary>
    public bool Watched { get; set; }

    /// <summary>Gets or sets a value indicating whether it's an optional entry the user skips.</summary>
    public bool Skipped { get; set; }

    /// <summary>Gets or sets what the order plays after this item (for a single film or episode), if anything.</summary>
    public ResolvedStep? After { get; set; }

    /// <summary>Gets or sets the user's next item in the order, or null when they've finished it.</summary>
    public ResolvedStep? Next { get; set; }

    /// <summary>Gets or sets how many items the user has watched.</summary>
    public int WatchedCount { get; set; }

    /// <summary>Gets or sets how many items are in the library and visible to the user.</summary>
    public int AvailableCount { get; set; }
}
