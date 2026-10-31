namespace Jellyfin.Plugin.WatchOrder.Models;

/// <summary>
/// A single target after range entries have been expanded. A <see cref="WatchOrderEntryType.Season"/>
/// step stays as one step here, because how many episodes it holds is only known once it is matched
/// against the library.
/// </summary>
/// <param name="EntryIndex">Index of the source entry in the definition.</param>
/// <param name="Type">Movie, Episode or Season.</param>
/// <param name="Title">Film title or series name.</param>
/// <param name="Year">Optional year.</param>
/// <param name="Season">Season number for episode and season steps.</param>
/// <param name="Episode">Episode number for episode steps.</param>
/// <param name="ProviderIds">Provider ids of the film or series.</param>
/// <param name="Note">Optional note.</param>
public sealed record WatchOrderStep(
    int EntryIndex,
    WatchOrderEntryType Type,
    string? Title,
    int? Year,
    int? Season,
    int? Episode,
    IReadOnlyDictionary<string, string> ProviderIds,
    string? Note)
{
    /// <summary>Gets a human-readable label, e.g. <c>Andor S01E03</c>.</summary>
    public string Label => Type switch
    {
        WatchOrderEntryType.Movie => Year is null ? Title ?? "?" : $"{Title} ({Year})",
        WatchOrderEntryType.Season => $"{Title} Season {Season}",
        _ => $"{Title} S{Season:00}E{Episode:00}"
    };
}
