using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WatchOrder.Services;

/// <summary>
/// A user's choice about whether an order appears on their home screen.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HomeState
{
    /// <summary>
    /// No choice made: only shown once the user has taken the order up on its page (chosen how to count what
    /// they'd watched, or played from it). Having watched items in it elsewhere isn't enough.
    /// </summary>
    Default,

    /// <summary>The user added it, or started it from the Watch Orders page: always shown until finished.</summary>
    Shown,

    /// <summary>The user removed it from their home screen.</summary>
    Hidden
}

/// <summary>What a user does with an order's optional entries.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<OptionalMode>))]
public enum OptionalMode
{
    /// <summary>Watch every entry.</summary>
    WatchAll,

    /// <summary>Skip optional entries, but keep the ones marked recommended.</summary>
    KeepRecommended,

    /// <summary>Skip every optional entry.</summary>
    SkipAll
}

/// <summary>
/// A mark the user made on the order page during a fresh run, which overrides what Jellyfin's history says.
/// </summary>
public sealed class RunMark
{
    /// <summary>Gets or sets a value indicating whether the user marked the item as watched (or not watched).</summary>
    public bool Watched { get; set; }

    /// <summary>Gets or sets when the mark was made (UTC). Watching the item to the end after this still counts.</summary>
    public DateTime At { get; set; }
}

/// <summary>
/// One watch-through of an order by one user. Without a run, progress is simply Jellyfin's watched flags.
/// A fresh run ignores what was watched before it began, without changing anything in Jellyfin.
/// </summary>
public sealed class OrderRun
{
    /// <summary>Gets or sets a value indicating whether earlier history is ignored.</summary>
    public bool Fresh { get; set; }

    /// <summary>Gets or sets when the run began (UTC).</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>Gets or sets how many times the user had finished the order before this run.</summary>
    public int Completed { get; set; }

    /// <summary>
    /// Gets or sets, for a fresh run, whether each item (id in "N" format) was watched when the run began.
    /// </summary>
    public Dictionary<string, bool> PlayedAtStart { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Gets or sets the user's own marks during a fresh run, keyed by item id ("N" format).</summary>
    public Dictionary<string, RunMark> Marks { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Gets or sets a value indicating whether <see cref="Previous"/> holds what to go back to on Undo.</summary>
    public bool CanUndo { get; set; }

    /// <summary>Gets or sets the run this one replaced (null when there was none), for Undo.</summary>
    public OrderRun? Previous { get; set; }

    /// <summary>Gets the watch-through number: 1 for the first time through.</summary>
    [JsonIgnore]
    public int Number => Completed + 1;

    /// <summary>
    /// Whether an item counts as watched in this run.
    /// </summary>
    /// <param name="itemKey">The item id in "N" format.</param>
    /// <param name="played">Jellyfin's watched flag now.</param>
    /// <param name="lastPlayed">Jellyfin's last played date now (UTC).</param>
    /// <returns><c>true</c> when watched in this run.</returns>
    public bool IsWatched(string itemKey, bool played, DateTime? lastPlayed)
    {
        if (!Fresh)
        {
            return played;
        }

        if (Marks.TryGetValue(itemKey, out var mark))
        {
            // Marked watched: stays watched. Marked not watched: only watching it to the end afterwards counts.
            return mark.Watched || (played && lastPlayed > mark.At);
        }

        if (!played)
        {
            return false;
        }

        // Jellyfin clears "watched" when playback starts and sets it again at the end, updating the last played
        // date; marking an item watched by hand after clearing it also sets the date. So: watched now, and either
        // not watched when the run began, or played/marked since.
        var knownAtStart = PlayedAtStart.TryGetValue(itemKey, out var playedAtStart);
        return (knownAtStart && !playedAtStart) || lastPlayed >= StartedAt;
    }
}

/// <summary>
/// Per-user state, one small JSON file per user in
/// <c>&lt;config&gt;/plugins/configurations/WatchOrder/users/</c>.
/// Watched history itself always comes from Jellyfin; runs only record where a watch-through began.
/// </summary>
public class UserStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _directory;
    private readonly ILogger<UserStateStore> _logger;
    private readonly ConcurrentDictionary<Guid, UserState> _cache = new();
    private readonly object _writeLock = new();

    public UserStateStore(IApplicationPaths applicationPaths, ILogger<UserStateStore> logger)
        : this(Path.Combine(applicationPaths.PluginConfigurationsPath, "WatchOrder", "users"), logger)
    {
    }

    internal UserStateStore(string directory, ILogger<UserStateStore> logger)
    {
        _directory = directory;
        _logger = logger;
        Directory.CreateDirectory(_directory);
    }

    /// <summary>Gets a user's home screen choice for an order.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="orderId">The order.</param>
    /// <returns>The choice, or <see cref="HomeState.Default"/>.</returns>
    public HomeState GetHomeState(Guid userId, string orderId)
    {
        return Load(userId).Home.TryGetValue(orderId, out var state) ? state : HomeState.Default;
    }

    /// <summary>Records a user's home screen choice for an order.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="orderId">The order.</param>
    /// <param name="state">The choice; <see cref="HomeState.Default"/> clears it.</param>
    public void SetHomeState(Guid userId, string orderId, HomeState state)
    {
        Update(userId, user =>
        {
            if (state == HomeState.Default)
            {
                user.Home.Remove(orderId);
            }
            else
            {
                user.Home[orderId] = state;
            }
        });
    }

    /// <summary>Gets the user's current run of an order, or null when they haven't chosen how to start it.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="orderId">The order.</param>
    /// <returns>The run, or null.</returns>
    public OrderRun? GetRun(Guid userId, string orderId)
    {
        return Load(userId).Runs.TryGetValue(orderId, out var run) ? run : null;
    }

    /// <summary>
    /// Keeps counting what the user has already watched. Does nothing when a run already exists.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="orderId">The order.</param>
    public void ContinueRun(Guid userId, string orderId)
    {
        Update(userId, user => user.Runs.TryAdd(orderId, new OrderRun { StartedAt = DateTime.UtcNow }));
    }

    /// <summary>
    /// Starts a fresh run: from now on only what the user watches (or marks) counts. Replaces any current run,
    /// which can be restored with <see cref="UndoRun"/>.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="orderId">The order.</param>
    /// <param name="playedNow">Whether each item in the order is watched in Jellyfin right now (ids in "N" format).</param>
    /// <param name="finishedNow">Whether the user has finished the order as it stands, which counts as a watch-through.</param>
    /// <param name="now">The start time (UTC).</param>
    public void StartFresh(Guid userId, string orderId, IReadOnlyDictionary<string, bool> playedNow, bool finishedNow, DateTime now)
    {
        Update(userId, user =>
        {
            user.Runs.TryGetValue(orderId, out var current);
            if (current is not null)
            {
                // Only one step of Undo is kept.
                current.Previous = null;
                current.CanUndo = false;
            }

            user.Runs[orderId] = new OrderRun
            {
                Fresh = true,
                StartedAt = now,
                Completed = (current?.Completed ?? 0) + (finishedNow ? 1 : 0),
                PlayedAtStart = new Dictionary<string, bool>(playedNow, StringComparer.Ordinal),
                CanUndo = true,
                Previous = current
            };
        });
    }

    /// <summary>Goes back to the run before the last fresh start.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="orderId">The order.</param>
    /// <returns><c>false</c> when there is nothing to undo.</returns>
    public bool UndoRun(Guid userId, string orderId)
    {
        var undone = false;
        Update(userId, user =>
        {
            if (!user.Runs.TryGetValue(orderId, out var run) || !run.CanUndo)
            {
                return;
            }

            if (run.Previous is null)
            {
                user.Runs.Remove(orderId);
            }
            else
            {
                user.Runs[orderId] = run.Previous;
            }

            undone = true;
        });
        return undone;
    }

    /// <summary>Records the user marking an item watched or not watched during a fresh run.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="orderId">The order.</param>
    /// <param name="itemKey">The item id in "N" format.</param>
    /// <param name="watched">Watched or not.</param>
    /// <param name="now">When (UTC).</param>
    public void MarkInRun(Guid userId, string orderId, string itemKey, bool watched, DateTime now)
    {
        Update(userId, user =>
        {
            if (user.Runs.TryGetValue(orderId, out var run) && run.Fresh)
            {
                run.Marks[itemKey] = new RunMark { Watched = watched, At = now };
            }
        });
    }

    /// <summary>Gets whether the user skips any optional entries in an order.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="orderId">The order.</param>
    /// <returns><c>true</c> when they skip some or all of them.</returns>
    public bool GetSkipOptional(Guid userId, string orderId) => GetOptionalMode(userId, orderId) != OptionalMode.WatchAll;

    /// <summary>Records whether the user skips all optional entries in an order.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="orderId">The order.</param>
    /// <param name="skip">Skip them all, or watch them all.</param>
    public void SetSkipOptional(Guid userId, string orderId, bool skip)
        => SetOptionalMode(userId, orderId, skip ? OptionalMode.SkipAll : OptionalMode.WatchAll);

    /// <summary>Gets what the user does with optional entries in an order.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="orderId">The order.</param>
    /// <returns>Their choice; <see cref="OptionalMode.WatchAll"/> when they haven't made one.</returns>
    public OptionalMode GetOptionalMode(Guid userId, string orderId)
    {
        var user = Load(userId);
        if (user.OptionalMode.TryGetValue(orderId, out var mode))
        {
            return mode;
        }

        // Before recommended entries, the only choice was skipping all optional entries.
        return user.SkipOptional.TryGetValue(orderId, out var skip) && skip ? OptionalMode.SkipAll : OptionalMode.WatchAll;
    }

    /// <summary>Records what the user does with optional entries in an order.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="orderId">The order.</param>
    /// <param name="mode">Their choice.</param>
    public void SetOptionalMode(Guid userId, string orderId, OptionalMode mode)
    {
        Update(userId, user =>
        {
            user.SkipOptional.Remove(orderId);
            if (mode == OptionalMode.WatchAll)
            {
                user.OptionalMode.Remove(orderId);
            }
            else
            {
                user.OptionalMode[orderId] = mode;
            }
        });
    }

    /// <summary>Whether a step is skipped, given what the user does with optional entries.</summary>
    /// <param name="mode">The user's choice for the order.</param>
    /// <param name="optional">Whether the step is optional.</param>
    /// <param name="recommended">Whether it's optional but recommended.</param>
    /// <returns><c>true</c> when it's skipped.</returns>
    public static bool IsSkipped(OptionalMode mode, bool optional, bool recommended) => optional && mode switch
    {
        OptionalMode.SkipAll => true,
        OptionalMode.KeepRecommended => !recommended,
        _ => false
    };

    /// <summary>Gets the user's own autoplay choice, or null when they haven't made one.</summary>
    /// <param name="userId">The user.</param>
    /// <returns>Their choice, or null.</returns>
    public bool? GetAutoplayFollow(Guid userId) => Load(userId).AutoplayFollow;

    /// <summary>Records whether autoplay follows the user's watch orders.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="follow">Their choice.</param>
    public void SetAutoplayFollow(Guid userId, bool follow) => Update(userId, user => user.AutoplayFollow = follow);

    /// <summary>Gets the user's own choice of whether an order keeps a playlist, or null when they haven't made one.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="orderId">The order.</param>
    /// <returns>Their choice, or null.</returns>
    public bool? GetPlaylistChoice(Guid userId, string orderId) => Load(userId).Playlist.TryGetValue(orderId, out var on) ? on : null;

    /// <summary>Records whether the user wants a playlist of what's left in an order.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="orderId">The order.</param>
    /// <param name="on">Their choice; null clears it, so the admin's default applies.</param>
    public void SetPlaylistChoice(Guid userId, string orderId, bool? on)
    {
        Update(userId, user =>
        {
            if (on is { } value)
            {
                user.Playlist[orderId] = value;
            }
            else
            {
                user.Playlist.Remove(orderId);
            }
        });
    }

    /// <summary>Gets the playlist the plugin keeps for a user's order, if any.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="orderId">The order.</param>
    /// <returns>The playlist id, or null.</returns>
    public Guid? GetPlaylistId(Guid userId, string orderId) => Load(userId).PlaylistIds.TryGetValue(orderId, out var id) ? id : null;

    /// <summary>Gets every order the plugin keeps a playlist for, for one user.</summary>
    /// <param name="userId">The user.</param>
    /// <returns>Order id to playlist id.</returns>
    public IReadOnlyDictionary<string, Guid> GetPlaylistIds(Guid userId) => Load(userId).PlaylistIds;

    /// <summary>Records (or forgets) the playlist the plugin keeps for a user's order.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="orderId">The order.</param>
    /// <param name="playlistId">The playlist, or null when there's none.</param>
    public void SetPlaylistId(Guid userId, string orderId, Guid? playlistId)
    {
        Update(userId, user =>
        {
            if (playlistId is { } id)
            {
                user.PlaylistIds[orderId] = id;
            }
            else
            {
                user.PlaylistIds.Remove(orderId);
            }
        });
    }

    /// <summary>
    /// Whether a user should have a playlist of what's left in an order.
    /// </summary>
    /// <param name="mode">The admin setting: <c>OptOut</c> (on for orders the user has taken up, unless they turn it
    /// off), <c>OptIn</c> (only when the user turns it on) or <c>Off</c>.</param>
    /// <param name="userChoice">The user's choice for this order, or null when they haven't made one.</param>
    /// <param name="takenUp">Whether the user has taken the order up (chosen how to count, played from it, or added
    /// it to their home screen).</param>
    /// <param name="hasItemsLeft">Whether anything is left to watch.</param>
    /// <returns><c>true</c> when there should be a playlist.</returns>
    public static bool WantsPlaylist(string? mode, bool? userChoice, bool takenUp, bool hasItemsLeft)
    {
        if (!hasItemsLeft || NormaliseAutoplayMode(mode) == "Off" || userChoice == false)
        {
            return false;
        }

        // Turning it on is itself taking the order up.
        return userChoice == true || (NormaliseAutoplayMode(mode) == "OptOut" && takenUp);
    }

    /// <summary>
    /// Whether autoplay follows a user's watch orders, given the admin setting and their own choice.
    /// </summary>
    /// <param name="mode">The admin setting: <c>OptIn</c>, <c>OptOut</c> or <c>Off</c>.</param>
    /// <param name="userChoice">The user's choice, or null when they haven't made one.</param>
    /// <returns><c>true</c> when it's on for them.</returns>
    public static bool AutoplayFollowsFor(string? mode, bool? userChoice) => NormaliseAutoplayMode(mode) switch
    {
        "Off" => false,
        "OptIn" => userChoice == true,
        _ => userChoice != false
    };

    /// <summary>Normalises the admin autoplay setting.</summary>
    /// <param name="mode">The setting as stored.</param>
    /// <returns><c>OptIn</c>, <c>OptOut</c> or <c>Off</c>; anything else is the default, <c>OptOut</c>.</returns>
    public static string NormaliseAutoplayMode(string? mode) => mode is "OptIn" or "Off" ? mode : "OptOut";

    /// <summary>
    /// Whether an order belongs on the user's home screen right now.
    /// </summary>
    /// <param name="state">The user's choice.</param>
    /// <param name="following">Whether the user has taken the order up: they have a watch-through of it.</param>
    /// <param name="finished">Whether the user has watched everything available.</param>
    /// <returns><c>true</c> when it should be shown.</returns>
    public static bool IsOnHomeScreen(HomeState state, bool following, bool finished)
    {
        return !finished && state switch
        {
            HomeState.Shown => true,
            HomeState.Hidden => false,
            _ => following
        };
    }

    /// <summary>Changes a copy of the user's state and saves it, so readers never see a half-made change.</summary>
    private void Update(Guid userId, Action<UserState> change)
    {
        lock (_writeLock)
        {
            var copy = JsonSerializer.Deserialize<UserState>(JsonSerializer.Serialize(Load(userId), JsonOptions), JsonOptions) ?? new UserState();
            change(copy);
            var path = PathFor(userId);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(copy, JsonOptions));
            File.Move(temp, path, overwrite: true);
            _cache[userId] = copy;
        }
    }

    private UserState Load(Guid userId)
    {
        return _cache.GetOrAdd(userId, id =>
        {
            var path = PathFor(id);
            if (!File.Exists(path))
            {
                return new UserState();
            }

            try
            {
                return JsonSerializer.Deserialize<UserState>(File.ReadAllText(path), JsonOptions) ?? new UserState();
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                _logger.LogWarning(ex, "Ignoring unreadable Watch Order user state {File}", path);
                return new UserState();
            }
        });
    }

    private string PathFor(Guid userId) => Path.Combine(_directory, userId.ToString("N") + ".json");

    /// <summary>What's stored for one user.</summary>
    public sealed class UserState
    {
        /// <summary>Gets or sets home screen choices, keyed by order id.</summary>
        public Dictionary<string, HomeState> Home { get; set; } = new(StringComparer.Ordinal);

        /// <summary>Gets or sets the current run of each order, keyed by order id.</summary>
        public Dictionary<string, OrderRun> Runs { get; set; } = new(StringComparer.Ordinal);

        /// <summary>Gets or sets the orders in which the user skips optional entries (from before <see cref="OptionalMode"/>).</summary>
        public Dictionary<string, bool> SkipOptional { get; set; } = new(StringComparer.Ordinal);

        /// <summary>Gets or sets what the user does with optional entries, per order; missing means watch them all.</summary>
        public Dictionary<string, OptionalMode> OptionalMode { get; set; } = new(StringComparer.Ordinal);

        /// <summary>Gets or sets whether autoplay follows the user's watch orders; null for the admin's default.</summary>
        public bool? AutoplayFollow { get; set; }

        /// <summary>
        /// Gets or sets the user's own choice of whether each order (by id) keeps a playlist of what's left.
        /// Missing means no choice: the admin's default applies. Deleting the playlist records "no".
        /// </summary>
        public Dictionary<string, bool> Playlist { get; set; } = new(StringComparer.Ordinal);

        /// <summary>Gets or sets the playlist the plugin keeps for each order (by id), so it can update or remove it.</summary>
        public Dictionary<string, Guid> PlaylistIds { get; set; } = new(StringComparer.Ordinal);
    }
}
