namespace Jellyfin.Plugin.WatchOrder.Models;

/// <summary>How everyone is getting on with one order, for the admin page.</summary>
public sealed class OrderStats
{
    /// <summary>Gets or sets the order id.</summary>
    public string OrderId { get; set; } = string.Empty;

    /// <summary>Gets or sets the order name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets how many users are part-way through it (watched some, not finished).</summary>
    public int InProgress { get; set; }

    /// <summary>Gets or sets how many users have taken it up but not watched anything in it yet.</summary>
    public int NotStarted { get; set; }

    /// <summary>Gets or sets how many users have finished it (their current watch-through).</summary>
    public int Finished { get; set; }

    /// <summary>Gets or sets how many of those part-way through haven't watched anything in it for a while.</summary>
    public int Stalled { get; set; }

    /// <summary>Gets or sets every user who has taken the order up or watched something in it.</summary>
    public List<UserOrderStats> Users { get; set; } = new();
}

/// <summary>One user's progress through an order, for the admin page.</summary>
public sealed class UserOrderStats
{
    /// <summary>Gets or sets the user id.</summary>
    public Guid UserId { get; set; }

    /// <summary>Gets or sets the user name.</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>Gets or sets <c>NotStarted</c>, <c>InProgress</c>, <c>Stalled</c> or <c>Finished</c>.</summary>
    public string Status { get; set; } = "NotStarted";

    /// <summary>Gets or sets a value indicating whether the user has taken the order up (rather than just watched some of it).</summary>
    public bool TakenUp { get; set; }

    /// <summary>Gets or sets how many items the user has watched in their current watch-through.</summary>
    public int WatchedCount { get; set; }

    /// <summary>Gets or sets how many items are in the library and visible to the user.</summary>
    public int AvailableCount { get; set; }

    /// <summary>Gets or sets which watch-through the user is on (1 for the first).</summary>
    public int WatchThrough { get; set; } = 1;

    /// <summary>Gets or sets how many times the user has finished the order.</summary>
    public int TimesFinished { get; set; }

    /// <summary>Gets or sets the user's next item, or null when they've finished.</summary>
    public ResolvedStep? Next { get; set; }

    /// <summary>Gets or sets when the user last played anything in the order (UTC), if ever.</summary>
    public DateTime? LastWatched { get; set; }

    /// <summary>Gets or sets a value indicating whether the user skips some or all optional entries.</summary>
    public bool SkipOptional { get; set; }

    /// <summary>Gets or sets what the user does with optional entries.</summary>
    public Services.OptionalMode OptionalMode { get; set; }
}
