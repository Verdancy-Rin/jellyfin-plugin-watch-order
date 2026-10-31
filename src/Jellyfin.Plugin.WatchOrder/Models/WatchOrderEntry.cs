namespace Jellyfin.Plugin.WatchOrder.Models;

/// <summary>
/// The kind of thing a <see cref="WatchOrderEntry"/> points at.
/// </summary>
public enum WatchOrderEntryType
{
    /// <summary>A single film.</summary>
    Movie,

    /// <summary>A single episode of a series.</summary>
    Episode,

    /// <summary>A contiguous range of episodes within one season (<c>from</c>..<c>to</c>).</summary>
    Episodes,

    /// <summary>Every episode of one season, in episode-number order.</summary>
    Season
}

/// <summary>
/// One line of a watch order as written in JSON. Range and season entries
/// expand into several <see cref="WatchOrderStep"/>s.
/// </summary>
public sealed class WatchOrderEntry
{
    /// <summary>Gets or sets the entry type.</summary>
    public WatchOrderEntryType Type { get; set; }

    /// <summary>Gets or sets the film title (for <see cref="WatchOrderEntryType.Movie"/>).</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets the release or first-air year, used to disambiguate title matches.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets the series name (for episode, range and season entries).</summary>
    public string? Series { get; set; }

    /// <summary>Gets or sets the season number. Season 0 is specials.</summary>
    public int? Season { get; set; }

    /// <summary>Gets or sets the episode number (for <see cref="WatchOrderEntryType.Episode"/>).</summary>
    public int? Episode { get; set; }

    /// <summary>Gets or sets the first episode of a range (inclusive).</summary>
    public int? From { get; set; }

    /// <summary>Gets or sets the last episode of a range (inclusive).</summary>
    public int? To { get; set; }

    private static readonly string[] _knownProviders = { "Tmdb", "Imdb", "Tvdb", "TvMaze", "AniDB", "AniList" };

    private Dictionary<string, string> _providerIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets or sets provider ids for the film, or for the series when the entry is an episode type.
    /// Keys are case-insensitive in the file and are normalised to Jellyfin's provider names
    /// (<c>Tmdb</c>, <c>Imdb</c>, <c>Tvdb</c>, ...), because Jellyfin's provider-id lookup is case-sensitive.
    /// </summary>
    public Dictionary<string, string> ProviderIds
    {
        get => _providerIds;
        set
        {
            // System.Text.Json assigns a fresh dictionary, which would drop the case-insensitive comparer.
            _providerIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, id) in value ?? new Dictionary<string, string>())
            {
                // Collection ids (e.g. TmdbCollection) are shared by every film in a collection,
                // and Jellyfin matches on *any* id, so they would match the wrong film.
                if (string.IsNullOrWhiteSpace(id) || key.Contains("Collection", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var canonical = _knownProviders.FirstOrDefault(p => p.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? key;
                _providerIds[canonical] = id.Trim();
            }
        }
    }

    /// <summary>Gets or sets an optional note shown beside the entry, e.g. "Filler, but sets up the finale".</summary>
    public string? Note { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the entry is optional (filler, a spin-off, a side story).
    /// Each user chooses per order whether to skip optional entries; skipped ones don't count towards progress
    /// and are never queued or autoplayed.
    /// </summary>
    public bool Optional { get; set; }

    /// <summary>Gets or sets fields this version of the plugin doesn't know; kept as they are.</summary>
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
}
