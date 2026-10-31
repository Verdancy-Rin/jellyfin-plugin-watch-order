namespace Jellyfin.Plugin.WatchOrder.Models;

/// <summary>
/// Response from the builder's preview endpoint.
/// </summary>
public sealed class PreviewResult
{
    /// <summary>Gets or sets validation errors for the draft's entries; empty when valid.</summary>
    public IReadOnlyList<string> Errors { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets the draft matched against the library, or null when it has errors.</summary>
    public ResolvedWatchOrder? Order { get; set; }
}
