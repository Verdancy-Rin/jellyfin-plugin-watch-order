using Jellyfin.Plugin.WatchOrder.Models;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.WatchOrder.Services;

/// <summary>
/// Layers a user's watched state onto a resolved order. It is read live from Jellyfin, so watching or marking
/// an item anywhere in Jellyfin moves the user along. A fresh run (see <see cref="OrderRun"/>) only counts
/// what was watched after it began.
/// </summary>
public class WatchOrderProgressService
{
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ILibraryManager _libraryManager;
    private readonly UserStateStore _userState;

    public WatchOrderProgressService(IUserManager userManager, IUserDataManager userDataManager, ILibraryManager libraryManager, UserStateStore userState)
    {
        _userManager = userManager;
        _userDataManager = userDataManager;
        _libraryManager = libraryManager;
        _userState = userState;
    }

    /// <summary>
    /// Builds one user's progress through an order, in their current run.
    /// </summary>
    /// <param name="order">The resolved order.</param>
    /// <param name="userId">The user id.</param>
    /// <returns>The progress, or null when the user doesn't exist.</returns>
    public UserProgress? GetProgress(ResolvedWatchOrder order, Guid userId)
    {
        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return null;
        }

        var run = _userState.GetRun(userId, order.Id);
        var mode = _userState.GetOptionalMode(userId, order.Id);
        var progress = new UserProgress
        {
            OrderId = order.Id,
            OrderName = order.Name,
            OrderDescription = order.Description,
            SkipOptional = mode != OptionalMode.WatchAll,
            OptionalMode = mode
        };

        foreach (var step in order.Steps)
        {
            var played = false;
            var available = false;
            if (step.ItemId is { } itemId && _libraryManager.GetItemById(itemId) is { } item)
            {
                // Respect library access: users only see items they're allowed to see.
                if (!item.IsVisible(user))
                {
                    continue;
                }

                var data = _userDataManager.GetUserData(user, item);
                var playedInJellyfin = data?.Played ?? false;
                played = run?.IsWatched(itemId.ToString("N"), playedInJellyfin, data?.LastPlayedDate) ?? playedInJellyfin;
                available = true;
            }

            progress.Steps.Add(new UserStep { Step = step, Played = played, Available = available, Skipped = UserStateStore.IsSkipped(mode, step.Optional, step.Recommended) });
        }

        Tally(progress);

        var finished = progress.Next is null && progress.AvailableCount > 0;
        progress.Run = new RunInfo
        {
            Chosen = run is not null,
            Fresh = run?.Fresh ?? false,
            StartedAt = run?.StartedAt,
            Number = run?.Number ?? 1,
            TimesCompleted = (run?.Completed ?? 0) + (finished ? 1 : 0),
            CanUndo = run?.CanUndo ?? false
        };
        return progress;
    }

    /// <summary>
    /// Works out the counts and the next step from the steps' watched, available and skipped flags.
    /// Skipped (optional) steps don't count and are never next.
    /// </summary>
    /// <param name="progress">The progress, with its steps filled in.</param>
    internal static void Tally(UserProgress progress)
    {
        var counted = progress.Steps.Where(s => s.Available && !s.Skipped).ToList();
        progress.AvailableCount = counted.Count;
        progress.WatchedCount = counted.Count(s => s.Played);
        progress.OptionalCount = progress.Steps.Count(s => s.Available && s.Step.Optional);
        progress.RecommendedCount = progress.Steps.Count(s => s.Available && s.Step.Optional && s.Step.Recommended);
        progress.SkippedCount = progress.Steps.Count(s => s.Available && s.Skipped);
        progress.Next = progress.Steps.FirstOrDefault(s => !s.Played && !s.Skipped && s.Step.ItemId is not null);
    }

    /// <summary>
    /// Whether each item in the order is watched in Jellyfin right now (ignoring any run), for starting a fresh run.
    /// </summary>
    /// <param name="order">The resolved order.</param>
    /// <param name="userId">The user id.</param>
    /// <returns>Item ids ("N" format) and their watched flags.</returns>
    public Dictionary<string, bool> GetPlayedNow(ResolvedWatchOrder order, Guid userId)
    {
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return result;
        }

        foreach (var step in order.Steps)
        {
            if (step.ItemId is { } itemId && _libraryManager.GetItemById(itemId) is { } item)
            {
                result[itemId.ToString("N")] = _userDataManager.GetUserData(user, item)?.Played ?? false;
            }
        }

        return result;
    }

    /// <summary>
    /// Marks an item watched or not watched for the user. Outside a fresh run this is Jellyfin's own watched flag.
    /// In a fresh run, marking watched also marks it in Jellyfin, but marking not watched only applies to the run,
    /// so the user's history elsewhere is left alone.
    /// </summary>
    /// <param name="order">The resolved order.</param>
    /// <param name="userId">The user id.</param>
    /// <param name="itemId">The item, which must be in the order.</param>
    /// <param name="watched">Watched or not.</param>
    /// <returns><c>false</c> when the user or item can't be found or isn't in the order.</returns>
    public bool SetWatched(ResolvedWatchOrder order, Guid userId, Guid itemId, bool watched)
    {
        var user = _userManager.GetUserById(userId);
        if (user is null || !order.Steps.Any(s => s.ItemId == itemId)
            || _libraryManager.GetItemById(itemId) is not { } item || !item.IsVisible(user))
        {
            return false;
        }

        var run = _userState.GetRun(userId, order.Id);
        var now = DateTime.UtcNow;
        if (run is { Fresh: true })
        {
            _userState.MarkInRun(userId, order.Id, itemId.ToString("N"), watched, now);
            if (watched && !(_userDataManager.GetUserData(user, item)?.Played ?? false))
            {
                item.MarkPlayed(user, now, true);
            }

            return true;
        }

        if (watched)
        {
            item.MarkPlayed(user, now, true);
        }
        else
        {
            item.MarkUnplayed(user);
        }

        return true;
    }
}
