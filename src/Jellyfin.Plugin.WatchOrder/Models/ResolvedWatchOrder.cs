namespace Jellyfin.Plugin.WatchOrder.Models;

/// <summary>
/// Whether a step was found in the library.
/// </summary>
public enum MatchStatus
{
    /// <summary>Matched using a provider id (reliable).</summary>
    Matched,

    /// <summary>Matched using title and year only (worth checking).</summary>
    MatchedByTitle,

    /// <summary>Not in the library.</summary>
    Missing
}

/// <summary>
/// One playable position in a resolved order. Season steps produce one of these per episode.
/// </summary>
public sealed class ResolvedStep
{
    /// <summary>Gets or sets the 1-based position in the order.</summary>
    public int Position { get; set; }

    /// <summary>Gets or sets the index of the source entry in the definition.</summary>
    public int EntryIndex { get; set; }

    /// <summary>Gets or sets the display label.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the name to show people: "Series: Episode title" for an episode in the library, otherwise the label.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Gets or sets the Jellyfin item id, when matched.</summary>
    public Guid? ItemId { get; set; }

    /// <summary>Gets or sets the match status.</summary>
    public MatchStatus Status { get; set; }

    /// <summary>Gets or sets the optional note.</summary>
    public string? Note { get; set; }

    /// <summary>Gets or sets a value indicating whether the entry this step comes from is optional.</summary>
    public bool Optional { get; set; }

    /// <summary>Gets or sets a value indicating whether the entry is optional but recommended.</summary>
    public bool Recommended { get; set; }
}

/// <summary>
/// A watch order matched against the current library. Shared by all users; per-user
/// played state is layered on top by <c>WatchOrderProgressService</c>.
/// </summary>
public sealed class ResolvedWatchOrder
{
    /// <summary>Gets or sets the order id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the description.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets when the order was last matched.</summary>
    public DateTime ResolvedAtUtc { get; set; }

    /// <summary>Gets or sets the resolved steps, in order.</summary>
    public List<ResolvedStep> Steps { get; set; } = new();

    /// <summary>Gets the number of steps missing from the library.</summary>
    public int MissingCount => Steps.Count(s => s.Status == MatchStatus.Missing);
}
