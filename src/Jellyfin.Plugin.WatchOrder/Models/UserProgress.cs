namespace Jellyfin.Plugin.WatchOrder.Models;

/// <summary>The user's playlist of what's left in an order.</summary>
public sealed class PlaylistInfo
{
    /// <summary>Gets or sets the admin setting: <c>OptOut</c>, <c>OptIn</c> or <c>Off</c>.</summary>
    public string Mode { get; set; } = "OptOut";

    /// <summary>Gets or sets a value indicating whether the user has a playlist of this order while there's anything left.</summary>
    public bool On { get; set; }

    /// <summary>Gets or sets the playlist's id, once it exists.</summary>
    public Guid? Id { get; set; }
}

/// <summary>
/// Where the user is in their watch-throughs of an order.
/// </summary>
public sealed class RunInfo
{
    /// <summary>
    /// Gets or sets a value indicating whether the user has chosen how to start the order. Until they do,
    /// everything they've already watched counts; the page offers "count them or start fresh" when that's some.
    /// </summary>
    public bool Chosen { get; set; }

    /// <summary>Gets or sets a value indicating whether this run ignores what was watched before it began.</summary>
    public bool Fresh { get; set; }

    /// <summary>Gets or sets when the run began (UTC), when there is one.</summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>Gets or sets the watch-through number: 1 the first time through.</summary>
    public int Number { get; set; } = 1;

    /// <summary>Gets or sets how many times the user has finished the order, including this run if finished.</summary>
    public int TimesCompleted { get; set; }

    /// <summary>Gets or sets a value indicating whether the last fresh start can be undone.</summary>
    public bool CanUndo { get; set; }
}

/// <summary>
/// A resolved step with the requesting user's played state.
/// </summary>
public sealed class UserStep
{
    /// <summary>Gets or sets the underlying step.</summary>
    public required ResolvedStep Step { get; set; }

    /// <summary>Gets or sets a value indicating whether the user has watched this item (in the current run).</summary>
    public bool Played { get; set; }

    /// <summary>Gets or sets a value indicating whether the item is in the library and visible to the user.</summary>
    public bool Available { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the step is skipped: it's optional and the user chose to skip optional
    /// entries in this order. Skipped steps don't count towards progress and are never queued or autoplayed.
    /// </summary>
    public bool Skipped { get; set; }
}

/// <summary>
/// One user's progress through a watch order.
/// </summary>
public sealed class UserProgress
{
    /// <summary>Gets or sets the order id.</summary>
    public string OrderId { get; set; } = string.Empty;

    /// <summary>Gets or sets the order name.</summary>
    public string OrderName { get; set; } = string.Empty;

    /// <summary>Gets or sets the order description.</summary>
    public string? OrderDescription { get; set; }

    /// <summary>Gets or sets the user's home screen choice for this order.</summary>
    public Services.HomeState HomeState { get; set; }

    /// <summary>Gets or sets a value indicating whether the order is on the user's home screen now.</summary>
    public bool OnHomeScreen { get; set; }

    /// <summary>Gets or sets the user's current watch-through.</summary>
    public RunInfo Run { get; set; } = new();

    /// <summary>Gets or sets the user's playlist of what's left (filled in by the API).</summary>
    public PlaylistInfo? Playlist { get; set; }

    /// <summary>Gets or sets a value indicating whether the user skips optional entries in this order.</summary>
    public bool SkipOptional { get; set; }

    /// <summary>Gets or sets how many optional items in the order are in the library.</summary>
    public int OptionalCount { get; set; }

    /// <summary>Gets or sets how many items are skipped because they're optional.</summary>
    public int SkippedCount { get; set; }

    /// <summary>Gets or sets every step with played state.</summary>
    public List<UserStep> Steps { get; set; } = new();

    /// <summary>Gets or sets the next unwatched, available step, or null when finished.</summary>
    public UserStep? Next { get; set; }

    /// <summary>Gets or sets the number of available steps watched (skipped ones aren't counted).</summary>
    public int WatchedCount { get; set; }

    /// <summary>Gets or sets the number of steps available in the library (skipped ones aren't counted).</summary>
    public int AvailableCount { get; set; }
}
