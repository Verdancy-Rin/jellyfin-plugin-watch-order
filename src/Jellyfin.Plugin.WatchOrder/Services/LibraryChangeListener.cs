using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WatchOrder.Services;

/// <summary>
/// Re-resolves orders after the library changes, so newly added films and episodes
/// fill gaps without a manual refresh. Changes are debounced: a library scan that adds
/// 500 episodes triggers one refresh after things go quiet.
/// </summary>
public sealed class LibraryChangeListener : IHostedService, IDisposable
{
    private readonly ILibraryManager _libraryManager;
    private readonly WatchOrderService _service;
    private readonly ILogger<LibraryChangeListener> _logger;
    private readonly object _lock = new();
    private Timer? _timer;

    public LibraryChangeListener(ILibraryManager libraryManager, WatchOrderService service, ILogger<LibraryChangeListener> logger)
    {
        _libraryManager = libraryManager;
        _service = service;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnLibraryChanged;
        _libraryManager.ItemRemoved += OnLibraryChanged;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnLibraryChanged;
        _libraryManager.ItemRemoved -= OnLibraryChanged;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose() => _timer?.Dispose();

    private void OnLibraryChanged(object? sender, ItemChangeEventArgs e)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.AutoRefreshOnLibraryChange)
        {
            return;
        }

        // Only films and episodes can affect an order.
        if (e.Item is not (MediaBrowser.Controller.Entities.Movies.Movie or MediaBrowser.Controller.Entities.TV.Episode or MediaBrowser.Controller.Entities.TV.Series))
        {
            return;
        }

        var delay = TimeSpan.FromSeconds(Math.Max(5, config.RefreshDebounceSeconds));
        lock (_lock)
        {
            _timer ??= new Timer(_ => Refresh(), null, Timeout.Infinite, Timeout.Infinite);
            _timer.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }

    private void Refresh()
    {
        try
        {
            _service.RefreshAll();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Watch order refresh after library change failed");
        }
    }
}
