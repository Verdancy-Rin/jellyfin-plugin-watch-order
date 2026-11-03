using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Jellyfin.Plugin.WatchOrder.Models;
using Jellyfin.Plugin.WatchOrder.Parsing;
using Jellyfin.Plugin.WatchOrder.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.WatchOrder.Tests;

public sealed class SectionsAndImageTests : IDisposable
{
    private readonly InMemoryLibrary _library = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wo-img-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private ResolvedWatchOrder Resolve(string json)
    {
        Assert.True(WatchOrderParser.TryParse(json, out var definition, out var errors), string.Join("; ", errors));
        return new WatchOrderResolver(_library).Resolve(definition!, true);
    }

    private static string Order(string entries, string extra = "") => $$"""{ "id": "t", "name": "T", {{extra}} "entries": [ {{entries}} ] }""";

    [Fact]
    public void NamedSections_SplitAtTheirEntries_AndStepsBeforeTheFirstGetAnUnnamedSection()
    {
        _library.Film("Prologue", 2000);
        _library.Film("A", 2001);
        _library.Film("B", 2002);
        _library.Film("C", 2003);
        var order = Resolve(Order("""
            { "type": "movie", "title": "Prologue", "year": 2000 },
            { "type": "movie", "title": "A", "year": 2001, "section": "Phase One" },
            { "type": "movie", "title": "B", "year": 2002 },
            { "type": "movie", "title": "C", "year": 2003, "section": " Phase Two " }
            """));

        Assert.Equal(new string?[] { null, "Phase One", "Phase Two" }, order.Sections.Select(s => s.Name));
        Assert.Equal(new[] { 1, 2, 4 }, order.Sections.Select(s => s.FirstPosition));
        Assert.Equal(new[] { 1, 2, 1 }, order.Sections.Select(s => s.StepCount));
        Assert.Equal(new[] { 0, 1, 1, 2 }, order.Steps.Select(s => s.SectionIndex));
        Assert.False(order.SectionsAutomatic);
    }

    [Fact]
    public void ASectionOnARange_CoversEveryEpisodeInIt()
    {
        var show = _library.Show("Show", 2010);
        _library.Episodes(show, 1, Enumerable.Range(1, 4));
        var order = Resolve(Order("""
            { "type": "episodes", "series": "Show", "season": 1, "from": 1, "to": 2, "section": "Part 1" },
            { "type": "episodes", "series": "Show", "season": 1, "from": 3, "to": 4, "section": "Part 2" }
            """));

        Assert.Equal(new[] { 0, 0, 1, 1 }, order.Steps.Select(s => s.SectionIndex));
        Assert.All(order.Steps, s => Assert.Equal("Episode", s.Kind));
        Assert.Equal(new int?[] { 1, 2, 3, 4 }, order.Steps.Select(s => s.Episode));
        Assert.All(order.Steps, s => Assert.Equal("Show", s.Series));
    }

    [Fact]
    public void RunsOfDifferentSeries_BecomeSectionsNamedAfterThem_WithFilmsJoiningTheRunTheyreIn()
    {
        var naruto = _library.Show("Naruto", 2002);
        var shippuden = _library.Show("Naruto Shippuden", 2007);
        _library.Episodes(naruto, 1, Enumerable.Range(1, 3));
        _library.Episodes(shippuden, 1, Enumerable.Range(1, 3));
        _library.Film("Naruto the Movie", 2004);
        _library.Film("Shippuden the Movie", 2007);
        var order = Resolve(Order("""
            { "type": "episodes", "series": "Naruto", "season": 1, "from": 1, "to": 2 },
            { "type": "movie", "title": "Naruto the Movie", "year": 2004 },
            { "type": "episode", "series": "Naruto", "season": 1, "episode": 3 },
            { "type": "movie", "title": "Shippuden the Movie", "year": 2007 },
            { "type": "season", "series": "Naruto Shippuden", "season": 1 }
            """));

        Assert.True(order.SectionsAutomatic);
        Assert.Equal(new string?[] { "Naruto", "Naruto Shippuden" }, order.Sections.Select(s => s.Name));

        // The film after Naruto's last episode stays with Naruto: a run starts at its series' first episode.
        Assert.Equal(new[] { 0, 0, 0, 0, 0, 1, 1, 1 }, order.Steps.Select(s => s.SectionIndex));
    }

    [Fact]
    public void AnOrderThatHopsBetweenSeries_OrIsMostlyFilms_IsntSplitAutomatically()
    {
        var a = _library.Show("A", 2010);
        var b = _library.Show("B", 2011);
        _library.Episodes(a, 1, Enumerable.Range(1, 2));
        _library.Episodes(b, 1, Enumerable.Range(1, 2));
        _library.Film("F1", 2001);
        _library.Film("F2", 2002);
        _library.Film("F3", 2003);

        var hops = Resolve(Order("""
            { "type": "episode", "series": "A", "season": 1, "episode": 1 },
            { "type": "episode", "series": "B", "season": 1, "episode": 1 },
            { "type": "episode", "series": "A", "season": 1, "episode": 2 }
            """));
        var films = Resolve(Order("""
            { "type": "movie", "title": "F1", "year": 2001 },
            { "type": "movie", "title": "F2", "year": 2002 },
            { "type": "episode", "series": "A", "season": 1, "episode": 1 },
            { "type": "movie", "title": "F3", "year": 2003 },
            { "type": "episode", "series": "B", "season": 1, "episode": 1 }
            """));

        foreach (var order in new[] { hops, films })
        {
            Assert.False(order.SectionsAutomatic);
            var only = Assert.Single(order.Sections);
            Assert.Null(only.Name);
            Assert.Equal(order.Steps.Count, only.StepCount);
            Assert.All(order.Steps, s => Assert.Equal(0, s.SectionIndex));
        }
    }

    [Fact]
    public void Image_NamesALibraryItem_ByProviderIdOrTitle()
    {
        var film = _library.Film("The Avengers", 2012, ("Tmdb", "24428"));
        var show = _library.Show("Naruto", 2002);
        _library.Film("Iron Man", 2008);

        var byId = Resolve(Order("""{ "type": "movie", "title": "Iron Man", "year": 2008 }""", """ "image": { "type": "movie", "providerIds": { "tmdb": "24428" } }, """));
        var byName = Resolve(Order("""{ "type": "movie", "title": "Iron Man", "year": 2008 }""", """ "image": { "type": "series", "title": "Naruto" }, """));
        var urlOnly = Resolve(Order("""{ "type": "movie", "title": "Iron Man", "year": 2008 }""", """ "image": { "url": "https://example.com/a.jpg" }, """));

        Assert.Equal(film.Id, byId.ImageItemId);
        Assert.Equal(show.Id, byName.ImageItemId);
        Assert.Null(urlOnly.ImageItemId);
    }

    [Theory]
    [InlineData("""{ "type": "film", "title": "X" }""", "image.type")]
    [InlineData("""{ "type": "movie" }""", "title or providerIds")]
    [InlineData("""{ "title": "X" }""", "needs a type")]
    [InlineData("""{ "url": "ftp://example.com/a.jpg" }""", "image.url")]
    [InlineData("""{ }""", "image needs a film or show")]
    public void Image_Validation(string image, string message)
    {
        Assert.False(WatchOrderParser.TryParse(Order("""{ "type": "movie", "title": "A" }""", $"\"image\": {image},"), out _, out var errors));
        Assert.Contains(errors, e => e.Contains(message, StringComparison.Ordinal));
    }

    [Fact]
    public void Section_MustHaveAHeading()
    {
        Assert.False(WatchOrderParser.TryParse(Order("""{ "type": "movie", "title": "A", "section": "  " }"""), out _, out var errors));
        Assert.Contains(errors, e => e.Contains("section needs a heading", StringComparison.Ordinal));
    }

    [Fact]
    public void SectionAndImage_RoundTrip_AndKeepUnknownImageFields()
    {
        var json = Order("""{ "type": "movie", "title": "A", "section": "Start" }""", """ "image": { "type": "movie", "title": "A", "url": "https://example.com/a.png", "focus": "top" }, """);
        Assert.True(WatchOrderParser.TryParse(json, out var definition, out _));
        var saved = JsonSerializer.Serialize(definition, WatchOrderParser.JsonOptions);
        Assert.True(WatchOrderParser.TryParse(saved, out var again, out _));

        Assert.Equal("Start", again!.Entries[0].Section);
        Assert.Equal("https://example.com/a.png", again.Image!.Url);
        Assert.Contains("\"focus\": \"top\"", saved, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "image/jpeg")]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0 }, "image/png")]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 1, 2, 3, 4, 0x57, 0x45, 0x42, 0x50 }, "image/webp")]
    [InlineData(new byte[] { 0x3C, 0x68, 0x74, 0x6D, 0x6C }, null)] // "<html"
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }, null)] // GIF isn't accepted
    public void Pictures_AreRecognisedByTheirContent(byte[] bytes, string? type)
    {
        Assert.Equal(type, OrderImageStore.ContentTypeOf(bytes));
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("140.82.112.3", true)]
    [InlineData("2606:4700::6810:84e5", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.20.0.5", false)]
    [InlineData("192.168.1.10", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.100.1.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd12:3456::1", false)]
    [InlineData("::ffff:192.168.1.1", false)]
    [InlineData("64:ff9b::a00:1", false)] // NAT64 of 10.0.0.1
    public void OnlyPublicAddresses_AreFetchedFrom(string address, bool allowed)
    {
        Assert.Equal(allowed, OrderImageStore.IsPublicAddress(IPAddress.Parse(address)));
    }

    private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };

    [Fact]
    public async Task Link_IsDownloadedOnce_KeptWhenTheLinkDies_AndRemovedWithTheLink()
    {
        using var server = new TinyServer();
        server.Respond("/a.png", 200, Png);
        var store = new OrderImageStore(_dir, NullLogger<OrderImageStore>.Instance, _ => true);
        var definition = Definition($"{server.Base}/a.png");

        var first = await store.SyncAsync(definition, false, default);
        Assert.NotNull(first!.File);
        Assert.Equal("image/png", first.ContentType);
        Assert.Equal(Png, File.ReadAllBytes(store.Open("t")!.Value.Path));

        // Same link again: not downloaded again.
        await store.SyncAsync(definition, false, default);
        Assert.Equal(1, server.Hits("/a.png"));

        // The link dies; refreshing keeps the copy and says why.
        server.Respond("/a.png", 404, Array.Empty<byte>());
        var refreshed = await store.SyncAsync(definition, true, default);
        Assert.NotNull(refreshed!.Error);
        Assert.NotNull(store.Open("t"));

        // The order no longer has a link: the copy goes.
        definition.Image = null;
        await store.SyncAsync(definition, false, default);
        Assert.Null(store.Open("t"));
        Assert.Empty(Directory.GetFiles(_dir, "t-*"));
    }

    [Fact]
    public async Task Link_ThatIsntAPicture_OrPointsAtALocalAddress_IsRefused()
    {
        using var server = new TinyServer();
        server.Respond("/page", 200, "<html></html>"u8.ToArray());
        var lenient = new OrderImageStore(_dir, NullLogger<OrderImageStore>.Instance, _ => true);
        var strict = new OrderImageStore(Path.Combine(_dir, "strict"), NullLogger<OrderImageStore>.Instance, OrderImageStore.IsPublicAddress);

        var page = await lenient.SyncAsync(Definition($"{server.Base}/page"), false, default);
        Assert.Contains("isn't a JPEG, PNG or WebP", page!.Error, StringComparison.Ordinal);

        server.Respond("/a.png", 200, Png);
        var local = await strict.SyncAsync(Definition($"{server.Base}/a.png"), false, default);
        Assert.Contains("private or local address", local!.Error, StringComparison.Ordinal);
        Assert.Equal(0, server.Hits("/a.png"));
    }

    [Fact]
    public async Task Upload_TakesThePlaceOfTheLink_AndIsLeftAloneBySync()
    {
        var store = new OrderImageStore(_dir, NullLogger<OrderImageStore>.Instance, _ => false);
        Assert.NotNull(store.SaveUpload("t", new byte[] { 1, 2, 3 }).Error);

        var (image, error) = store.SaveUpload("t", Png);
        Assert.Null(error);
        Assert.Equal("Upload", image!.Kind);

        var after = await store.SyncAsync(Definition("https://example.com/other.png"), true, default);
        Assert.Equal("Upload", after!.Kind);
        Assert.NotNull(store.VersionOf("t"));

        store.Remove("t");
        Assert.Null(store.Open("t"));
    }

    private static WatchOrderDefinition Definition(string url) => new()
    {
        Id = "t",
        Name = "T",
        Image = new OrderImage { Url = url },
        Entries = { new WatchOrderEntry { Type = WatchOrderEntryType.Movie, Title = "A" } }
    };

    /// <summary>A loopback web server that answers set paths, for testing downloads without the internet.</summary>
    private sealed class TinyServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Dictionary<string, (int Status, byte[] Body)> _responses = new();
        private readonly Dictionary<string, int> _hits = new();

        public TinyServer()
        {
            var port = FreePort();
            Base = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(Base + "/");
            _listener.Start();
            _ = Task.Run(Loop);
        }

        public string Base { get; }

        public void Respond(string path, int status, byte[] body)
        {
            lock (_responses)
            {
                _responses[path] = (status, body);
            }
        }

        public int Hits(string path)
        {
            lock (_responses)
            {
                return _hits.GetValueOrDefault(path);
            }
        }

        public void Dispose() => _listener.Close();

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private async Task Loop()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return;
                }

                (int Status, byte[] Body) response;
                lock (_responses)
                {
                    var path = context.Request.Url!.AbsolutePath;
                    _hits[path] = _hits.GetValueOrDefault(path) + 1;
                    response = _responses.GetValueOrDefault(path, (404, Array.Empty<byte>()));
                }

                context.Response.StatusCode = response.Status;
                await context.Response.OutputStream.WriteAsync(response.Body);
                context.Response.Close();
            }
        }
    }
}
