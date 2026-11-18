using Jellyfin.Plugin.WatchOrder.Services;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.WatchOrder.Tests;

/// <summary>
/// The dashboard's ready-made list comes back at once and is checked against the library in the background, so a
/// big library can't leave it loading forever.
/// </summary>
public sealed class ReadyMadeCheckTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wo-ready-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryLibrary _library = new();
    private readonly WatchOrderService _service;
    private readonly ReadyMadeOrders _readyMade;

    public ReadyMadeCheckTests()
    {
        var paths = new TestPaths(_dir);
        _service = new WatchOrderService(new WatchOrderStore(paths, NullLogger<WatchOrderStore>.Instance), new WatchOrderResolver(_library), NullLogger<WatchOrderService>.Instance);
        _readyMade = new ReadyMadeOrders(_service, new OrderOriginStore(Path.Combine(_dir, "origins.json"), NullLogger<OrderOriginStore>.Instance), NullLogger<ReadyMadeOrders>.Instance);
        var naruto = _library.Show("Naruto", 2002, ("Tvdb", "78857"));
        _library.Episodes(naruto, 1, Enumerable.Range(1, 220));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private List<ReadyMadeInfo> All() => _readyMade.List().SelectMany(g => g.Orders).ToList();

    [Fact]
    public async Task FirstAsk_ComesBackAtOnce_StillChecking_ThenFillsIn()
    {
        var first = All();
        Assert.NotEmpty(first);
        Assert.All(first, o => Assert.True(o.Checking && o.InLibrary is null && o.ItemCount is null));

        await _readyMade.Checking.WaitAsync(TimeSpan.FromSeconds(30));
        var after = All();
        Assert.All(after, o => Assert.False(o.Checking));
        Assert.All(after, o => Assert.True(o.ItemCount > 0));
        Assert.Contains(after, o => o.Id == "naruto" && o.InLibrary > 0);
    }

    [Fact]
    public async Task LibraryChange_ChecksAgain()
    {
        All();
        await _readyMade.Checking.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.All(All(), o => Assert.False(o.Checking));

        _service.RefreshAll();
        Assert.All(All(), o => Assert.True(o.Checking));
        await _readyMade.Checking.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.All(All(), o => Assert.False(o.Checking));
    }

    [Fact]
    public async Task OneCheckAtATime_HoweverOftenTheListIsAskedFor()
    {
        All();
        var running = _readyMade.Checking;
        All();
        All();
        Assert.Same(running, _readyMade.Checking);
        await running.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private sealed class TestPaths : IApplicationPaths
    {
        public TestPaths(string root) => ProgramDataPath = root;

        public string ProgramDataPath { get; }

        public string WebPath => ProgramDataPath;

        public string ProgramSystemPath => ProgramDataPath;

        public string DataPath => ProgramDataPath;

        public string ImageCachePath => ProgramDataPath;

        public string PluginsPath => ProgramDataPath;

        public string PluginConfigurationsPath => Path.Combine(ProgramDataPath, "plugins");

        public string LogDirectoryPath => ProgramDataPath;

        public string ConfigurationDirectoryPath => ProgramDataPath;

        public string SystemConfigurationFilePath => ProgramDataPath;

        public string CachePath => ProgramDataPath;

        public string TempDirectory => ProgramDataPath;

        public string VirtualDataPath => ProgramDataPath;

        public string TrickplayPath => ProgramDataPath;

        public string BackupPath => ProgramDataPath;

        public void MakeSanityCheckOrThrow()
        {
        }

        public void CreateAndCheckMarker(string path, string markerName, bool recursive = false)
        {
        }
    }
}
