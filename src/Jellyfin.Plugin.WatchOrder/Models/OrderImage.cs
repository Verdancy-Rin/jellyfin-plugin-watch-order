using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.WatchOrder.Models;

/// <summary>
/// An order's own image: the backdrop of a film or show in the library (<see cref="Type"/> with provider ids or a
/// title), a picture on the web (<see cref="Url"/>), or both, in which case the library item is used when it's there.
/// </summary>
public sealed class OrderImage
{
    /// <summary>Gets or sets <c>movie</c> or <c>series</c>, when the image is a library item's.</summary>
    public string? Type { get; set; }

    /// <summary>Gets or sets the film title or series name.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets the year, to tell remakes apart.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets provider ids of the film or series.</summary>
    public Dictionary<string, string>? ProviderIds { get; set; }

    /// <summary>Gets or sets a link to a JPEG, PNG or WebP picture. The server keeps its own copy.</summary>
    public string? Url { get; set; }

    /// <summary>Gets or sets fields this version of the plugin doesn't know; kept as they are.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    /// <summary>Gets a value indicating whether the image points at a library item.</summary>
    [JsonIgnore]
    public bool HasItem => Type is not null && (!string.IsNullOrWhiteSpace(Title) || ProviderIds is { Count: > 0 });
}
