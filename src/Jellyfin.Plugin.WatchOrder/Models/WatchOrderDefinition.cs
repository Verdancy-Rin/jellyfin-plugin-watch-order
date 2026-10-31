using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.WatchOrder.Models;

/// <summary>
/// A viewing order as written in a <c>.json</c> file. See <c>docs/order-format.md</c>.
/// </summary>
public sealed class WatchOrderDefinition
{
    /// <summary>
    /// Gets or sets the format version. Only a change that older plugins would misread raises it; new optional
    /// fields don't. See "Compatibility" in <c>docs/order-format.md</c>.
    /// </summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Gets or sets the unique slug, e.g. <c>star-wars-chronological</c>. Also used as the file name.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets an optional description shown in the UI.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the author or source credit for the order.</summary>
    public string? Author { get; set; }

    /// <summary>
    /// Gets or sets the order's own version: 1 to start with, raised by its author each time they publish a change,
    /// so a copy can tell when a newer one is available. Null in files that don't say (treated as 1).
    /// </summary>
    public int? Version { get; set; }

    /// <summary>
    /// Gets or sets where the latest copy of this order is published (an https link to the file), if anywhere.
    /// </summary>
    public string? Source { get; set; }

    /// <summary>Gets or sets the ordered entries.</summary>
    public List<WatchOrderEntry> Entries { get; set; } = new();

    /// <summary>
    /// Gets or sets fields this version of the plugin doesn't know (added by a newer version, or another tool).
    /// They're kept as they are when the order is saved, edited or exported, so nothing is lost.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}
