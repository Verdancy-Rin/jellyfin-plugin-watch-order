using Jellyfin.Plugin.WatchOrder.Models;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.WatchOrder.Services;

/// <summary>
/// Works out how each user is getting on with each order, for the admin page.
/// </summary>
public class OrderStatsService
{
    /// <summary>How long without watching anything in an order before someone part-way through counts as stalled.</summary>
    public static readonly TimeSpan StalledAfter = TimeSpan.FromDays(30);

    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ILibraryManager _libraryManager;
    private readonly WatchOrderService _orders;
    private readonly WatchOrderProgressService _progress;
    private readonly UserStateStore _userState;

    public OrderStatsService(
        IUserManager userManager,
        IUserDataManager userDataManager,
        ILibraryManager libraryManager,
        WatchOrderService orders,
        WatchOrderProgressService progress,
        UserStateStore userState)
    {
        _userManager = userManager;
        _userDataManager = userDataManager;
        _libraryManager = libraryManager;
        _orders = orders;
        _progress = progress;
        _userState = userState;
    }

    /// <summary>Gets the stats for every order.</summary>
    /// <param name="now">The current time (UTC).</param>
    /// <returns>One entry per order, in name order.</returns>
    public IReadOnlyList<OrderStats> GetAll(DateTime now)
    {
        var users = _userManager.GetUsers().ToList();
        var result = new List<OrderStats>();
        foreach (var definition in _orders.GetDefinitions())
        {
            if (_orders.Get(definition.Id) is not { } order)
            {
                continue;
            }

            var stats = new OrderStats { OrderId = order.Id, Name = order.Name };
            foreach (var user in users)
            {
                if (_progress.GetProgress(order, user.Id) is not { } progress)
                {
                    continue;
                }

                var takenUp = progress.Run.Chosen || _userState.GetRun(user.Id, order.Id) is not null
                    || _userState.GetHomeState(user.Id, order.Id) == HomeState.Shown;
                if (!takenUp && progress.WatchedCount == 0)
                {
                    continue; // nothing to report
                }

                DateTime? lastWatched = null;
                foreach (var step in progress.Steps)
                {
                    if (step.Step.ItemId is { } id && step.Available && _libraryManager.GetItemById(id) is { } item
                        && _userDataManager.GetUserData(user, item)?.LastPlayedDate is { } played && (lastWatched is null || played > lastWatched))
                    {
                        lastWatched = played;
                    }
                }

                var row = new UserOrderStats
                {
                    UserId = user.Id,
                    UserName = user.Username,
                    TakenUp = takenUp,
                    WatchedCount = progress.WatchedCount,
                    AvailableCount = progress.AvailableCount,
                    WatchThrough = progress.Run.Number,
                    TimesFinished = progress.Run.TimesCompleted,
                    Next = progress.Next?.Step,
                    LastWatched = lastWatched,
                    SkipOptional = progress.SkipOptional,
                    Status = StatusOf(progress.Next is null && progress.AvailableCount > 0, progress.WatchedCount, lastWatched, now)
                };
                stats.Users.Add(row);
                switch (row.Status)
                {
                    case "Finished": stats.Finished++; break;
                    case "NotStarted": stats.NotStarted++; break;
                    case "Stalled": stats.Stalled++; stats.InProgress++; break;
                    default: stats.InProgress++; break;
                }
            }

            stats.Users = stats.Users
                .OrderBy(u => u.Status switch { "InProgress" => 0, "Stalled" => 1, "NotStarted" => 2, _ => 3 })
                .ThenByDescending(u => u.WatchedCount)
                .ThenBy(u => u.UserName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            result.Add(stats);
        }

        return result.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Gets where a user is with an order.</summary>
    /// <param name="finished">Whether they've watched everything available.</param>
    /// <param name="watched">How many items they've watched in this watch-through.</param>
    /// <param name="lastWatched">When they last played anything in it.</param>
    /// <param name="now">The current time (UTC).</param>
    /// <returns><c>Finished</c>, <c>NotStarted</c>, <c>Stalled</c> or <c>InProgress</c>.</returns>
    public static string StatusOf(bool finished, int watched, DateTime? lastWatched, DateTime now)
    {
        if (finished)
        {
            return "Finished";
        }

        if (watched == 0)
        {
            return "NotStarted";
        }

        return lastWatched is null || now - lastWatched > StalledAfter ? "Stalled" : "InProgress";
    }
}
