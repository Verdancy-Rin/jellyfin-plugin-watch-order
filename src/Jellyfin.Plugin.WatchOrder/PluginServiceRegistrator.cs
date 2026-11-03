using Jellyfin.Plugin.WatchOrder.Services;
using Jellyfin.Plugin.WatchOrder.WebClient;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.WatchOrder;

/// <summary>
/// Registers the plugin's services with Jellyfin's dependency injection container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<WatchOrderStore>();
        serviceCollection.AddSingleton<ILibraryLookup, JellyfinLibraryLookup>();
        serviceCollection.AddSingleton<WatchOrderResolver>();
        serviceCollection.AddSingleton<WatchOrderService>();
        serviceCollection.AddSingleton<WatchOrderProgressService>();
        serviceCollection.AddSingleton<UserStateStore>();
        serviceCollection.AddSingleton<OrderStatsService>();
        serviceCollection.AddSingleton<OrderOriginStore>();
        serviceCollection.AddSingleton<ReadyMadeOrders>();
        serviceCollection.AddSingleton<OrderLinks>();
        serviceCollection.AddSingleton<OrderImageStore>();

        // Keeps the server's copy of each order's picture in line with its image link.
        serviceCollection.AddHostedService<OrderImageSync>();

        // Re-resolves orders when items are added to or removed from the library.
        serviceCollection.AddHostedService<LibraryChangeListener>();

        // Queues an order's next item after the one playing, so autoplay follows the order.
        serviceCollection.AddHostedService<AutoplayFollower>();

        // Keeps each user's playlist of what's left in the orders they follow.
        serviceCollection.AddSingleton<PlaylistSync>();
        serviceCollection.AddHostedService(provider => provider.GetRequiredService<PlaylistSync>());

        // Adds the Watch orders page to the web app when File Transformation is installed.
        serviceCollection.AddHostedService<FileTransformationRegistrar>();
    }
}
