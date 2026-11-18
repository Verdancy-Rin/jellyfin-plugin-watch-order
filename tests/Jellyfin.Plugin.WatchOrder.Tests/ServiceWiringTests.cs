using System.Reflection;
using Jellyfin.Plugin.WatchOrder.Services;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.WatchOrder.Tests;

/// <summary>
/// The services Jellyfin builds for the plugin get the constructors with loggers (several have more than one), and
/// the lookup cache listens for library changes.
/// </summary>
public sealed class ServiceWiringTests
{
    [Fact]
    public void ServicesAreBuiltWithTheirLoggers()
    {
        var subscribed = new List<string>();
        var library = ProgressBatchTests.FakeOf<ILibraryManager>((method, _) =>
        {
            if (method.StartsWith("add_", StringComparison.Ordinal))
            {
                subscribed.Add(method[4..]);
            }

            return null;
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(library);
        services.AddSingleton(ProgressBatchTests.FakeOf<IUserManager>((_, _) => null));
        services.AddSingleton(ProgressBatchTests.FakeOf<IUserDataManager>((_, _) => null));
        services.AddSingleton<ILibraryLookup, CachedLibraryLookup>();
        services.AddSingleton<WatchOrderResolver>();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = false });

        var lookup = Assert.IsType<CachedLibraryLookup>(provider.GetRequiredService<ILibraryLookup>());
        Assert.NotNull(Field<ILogger>(lookup, "_logger"));
        Assert.Equal(new[] { "ItemAdded", "ItemUpdated", "ItemRemoved" }, subscribed);

        var resolver = provider.GetRequiredService<WatchOrderResolver>();
        Assert.NotNull(Field<ILogger>(resolver, "_logger"));
        Assert.Same(lookup, Field<ILibraryLookup>(resolver, "_library"));
    }

    private static T? Field<T>(object target, string name)
        => (T?)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
}
