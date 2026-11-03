using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WatchOrder.Services;

/// <summary>
/// Downloads an order's picture when it's saved with a new <c>image.url</c>, and drops the copy when the order is
/// deleted. At startup it fetches any picture that's missing (an order file copied in by hand, say).
/// </summary>
public sealed class OrderImageSync : IHostedService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(45);

    private readonly WatchOrderService _orders;
    private readonly OrderImageStore _images;
    private readonly ILogger<OrderImageSync> _logger;
    private readonly CancellationTokenSource _stop = new();

    public OrderImageSync(WatchOrderService orders, OrderImageStore images, ILogger<OrderImageSync> logger)
    {
        _orders = orders;
        _images = images;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _orders.OrdersChanged += OnOrdersChanged;
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await Task.Delay(StartupDelay, _stop.Token).ConfigureAwait(false);
                    await SyncAsync(null).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Shutting down.
                }
            },
            CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _orders.OrdersChanged -= OnOrdersChanged;
        await _stop.CancelAsync().ConfigureAwait(false);
        _stop.Dispose();
    }

    private void OnOrdersChanged(object? sender, string? orderId)
    {
        // A full refresh (null) only re-matches the library; pictures don't change with it.
        if (orderId is not null)
        {
            _ = Task.Run(() => SyncAsync(orderId), CancellationToken.None);
        }
    }

    private async Task SyncAsync(string? orderId)
    {
        try
        {
            var definitions = _orders.GetDefinitions();
            if (orderId is not null && definitions.All(d => d.Id != orderId))
            {
                _images.Remove(orderId); // deleted
                return;
            }

            foreach (var definition in definitions.Where(d => orderId is null || d.Id == orderId))
            {
                await _images.SyncAsync(definition, false, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Watch Order: couldn't update order images");
        }
    }
}
