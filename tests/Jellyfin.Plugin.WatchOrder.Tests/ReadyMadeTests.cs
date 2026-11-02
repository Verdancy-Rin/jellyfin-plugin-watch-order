using Jellyfin.Plugin.WatchOrder.Models;
using Jellyfin.Plugin.WatchOrder.Parsing;
using Jellyfin.Plugin.WatchOrder.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.WatchOrder.Tests;

public sealed class ReadyMadeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wo-origins-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private static WatchOrderDefinition Order(int? version = 1, string title = "A") => new()
    {
        Id = "o",
        Name = "O",
        Version = version,
        Entries = { new WatchOrderEntry { Type = WatchOrderEntryType.Movie, Title = title } }
    };

    [Fact]
    public void Bundle_HasTheMcuStarWarsAndAnimeOrders_AndNotPersonalOnes()
    {
        var groups = ReadyMadeOrders.Load(typeof(ReadyMadeOrders).Assembly);
        Assert.Equal(new[] { "Marvel Cinematic Universe", "Star Wars", "Anime" }, groups.Select(g => g.Name));
        var ids = groups.SelectMany(g => g.Orders).Select(o => o.Id).ToList();
        Assert.Equal(15, ids.Count);
        Assert.Contains("naruto", ids);
        Assert.Contains("naruto-and-boruto", ids);
        Assert.Contains("mcu-phase-1", ids);
        Assert.Contains("star-wars-complete", ids);
        Assert.Contains("cardcaptor-sakura", ids);
        Assert.Contains("urusei-yatsura-1981", ids);
        Assert.DoesNotContain("steven-universe", ids);
        Assert.All(groups.SelectMany(g => g.Orders), o => Assert.True(o.Version >= 1, o.Id + " has a version"));
        Assert.All(groups.SelectMany(g => g.Orders), o => Assert.False(o.Extra?.ContainsKey("$schema") ?? false));
    }

    [Fact]
    public void Bundle_ListsEveryOrderFileInItsFolders()
    {
        // Every file in a bundled folder should be offered, so a new order isn't forgotten from ready-made.json.
        var root = FindRepo();
        var listed = ReadyMadeOrders.Load(typeof(ReadyMadeOrders).Assembly).SelectMany(g => g.Orders).Select(o => o.Id).ToHashSet();
        foreach (var folder in new[] { "mcu", "star-wars", "cardcaptor-sakura", "naruto", "urusei-yatsura" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "orders", folder), "*.json"))
            {
                Assert.Contains(Path.GetFileNameWithoutExtension(file), listed);
            }
        }
    }

    [Theory]
    [InlineData(1, 1, false, false, "Added")]
    [InlineData(2, 1, false, false, "UpdateAvailable")]
    [InlineData(1, 1, true, false, "Edited")]
    [InlineData(2, 1, true, false, "EditedUpdateAvailable")]
    [InlineData(1, 1, false, true, "IdTaken")]
    public void State(int bundledVersion, int addedVersion, bool edited, bool fromElsewhere, string expected)
    {
        var added = Order(addedVersion);
        var origin = new OrderOrigin { Kind = fromElsewhere ? "Link" : "ReadyMade", Version = addedVersion, Hash = OrderOriginStore.HashOf(added) };
        var current = edited ? Order(addedVersion, "Changed here") : added;
        Assert.Equal(expected, ReadyMadeOrders.StateOf(Order(bundledVersion), current, origin));
    }

    [Fact]
    public void State_NotAdded_AndAnOrderMadeHereWithTheSameId()
    {
        Assert.Equal("NotAdded", ReadyMadeOrders.StateOf(Order(), null, null));
        Assert.Equal("IdTaken", ReadyMadeOrders.StateOf(Order(), Order(), null));
    }

    [Fact]
    public void Origins_SurviveARestart_AndNoticeEdits()
    {
        var path = Path.Combine(_dir, "origins.json");
        var store = new OrderOriginStore(path, NullLogger<OrderOriginStore>.Instance);
        store.Set(Order(3), "Link", "https://example.com/o.json");

        var reloaded = new OrderOriginStore(path, NullLogger<OrderOriginStore>.Instance);
        var origin = reloaded.Get("o")!;
        Assert.Equal("Link", origin.Kind);
        Assert.Equal(3, origin.Version);
        Assert.False(OrderOriginStore.IsEdited(origin, Order(3)));
        Assert.True(OrderOriginStore.IsEdited(origin, Order(3, "B")));

        reloaded.Remove("o");
        Assert.Null(new OrderOriginStore(path, NullLogger<OrderOriginStore>.Instance).Get("o"));
    }

    [Theory]
    [InlineData("https://github.com/Owner/Repo/blob/main/orders/mcu/mcu-phase-1.json", "https://raw.githubusercontent.com/Owner/Repo/main/orders/mcu/mcu-phase-1.json")]
    [InlineData("https://github.com/Owner/Repo/raw/v1.0/x.json", "https://raw.githubusercontent.com/Owner/Repo/v1.0/x.json")]
    [InlineData("https://gist.github.com/someone/0123abcd", "https://gist.githubusercontent.com/someone/0123abcd/raw")]
    [InlineData("https://example.com/orders/x.json?v=2", "https://example.com/orders/x.json?v=2")]
    [InlineData("  https://example.com/x.json  ", "https://example.com/x.json")]
    public void Links_PointAtTheFile(string pasted, string expected)
    {
        Assert.Equal(expected, OrderLinks.ToFileLink(pasted)!.ToString());
    }

    [Theory]
    [InlineData("ftp://example.com/x.json")]
    [InlineData("file:///etc/passwd")]
    [InlineData("not a link")]
    [InlineData("")]
    public void Links_OnlyWebLinks(string pasted)
    {
        Assert.Null(OrderLinks.ToFileLink(pasted));
    }

    private static string FindRepo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "orders", "ready-made.json")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }
}
