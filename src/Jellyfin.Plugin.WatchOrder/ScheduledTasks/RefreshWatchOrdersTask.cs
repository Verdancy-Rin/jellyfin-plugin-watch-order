using Jellyfin.Plugin.WatchOrder.Services;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.WatchOrder.ScheduledTasks;

/// <summary>
/// Dashboard &gt; Scheduled Tasks entry that re-matches every order against the library.
/// Runs daily by default and can be run by hand.
/// </summary>
public class RefreshWatchOrdersTask : IScheduledTask
{
    private readonly WatchOrderService _service;

    public RefreshWatchOrdersTask(WatchOrderService service)
    {
        _service = service;
    }

    /// <inheritdoc />
    public string Name => "Refresh watch orders";

    /// <inheritdoc />
    public string Key => "WatchOrderRefresh";

    /// <inheritdoc />
    public string Description => "Matches every franchise watch order against the library and finds missing items.";

    /// <inheritdoc />
    public string Category => "Watch Order";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _service.RefreshAll(progress, cancellationToken);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(24).Ticks
        };
    }
}
