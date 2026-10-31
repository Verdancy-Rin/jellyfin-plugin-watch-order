using Jellyfin.Plugin.WatchOrder.Models;
using Jellyfin.Plugin.WatchOrder.Parsing;
using Xunit;

namespace Jellyfin.Plugin.WatchOrder.Tests;

public class WatchOrderParserTests
{
    private const string ValidOrder = """
        {
          "schemaVersion": 1,
          "id": "test-order",
          "name": "Test order",
          // comments are allowed
          "entries": [
            { "type": "movie", "title": "Some Film", "year": 2008, "providerIds": { "Tmdb": "12180" } },
            { "type": "episodes", "series": "Some Show", "season": 1, "from": 1, "to": 3 },
            { "type": "episode", "series": "Some Show", "season": 2, "episode": 5, "note": "Optional" },
            { "type": "season", "series": "Other Show", "season": 1 },
          ]
        }
        """;

    [Fact]
    public void ValidOrder_Parses()
    {
        var ok = WatchOrderParser.TryParse(ValidOrder, out var def, out var errors);

        Assert.True(ok, string.Join("; ", errors));
        Assert.NotNull(def);
        Assert.Equal("test-order", def!.Id);
        Assert.Equal(4, def.Entries.Count);
        Assert.Equal("12180", def.Entries[0].ProviderIds["tmdb"]); // provider keys are case-insensitive
    }

    [Fact]
    public void ProviderIdKeys_AreNormalisedToJellyfinNames()
    {
        const string json = """{ "id": "ok", "name": "x", "entries": [ { "type": "movie", "providerIds": { "tmdb": " 1 ", "IMDB": "tt2", "Tvdb": "", "TmdbCollection": "10" } } ] }""";

        WatchOrderParser.TryParse(json, out var def, out _);
        var ids = def!.Entries[0].ProviderIds;

        // Jellyfin's provider-id query is case-sensitive, so "tmdb" must become "Tmdb".
        // Blank ids are dropped, and so are collection ids, which would match every film in the collection.
        Assert.Equal(new[] { "Imdb", "Tmdb" }, ids.Keys.OrderBy(k => k));
        Assert.Equal("1", ids["Tmdb"]);
    }

    [Fact]
    public void Expand_TurnsRangesIntoOneStepPerEpisode_AndKeepsSeasonsWhole()
    {
        WatchOrderParser.TryParse(ValidOrder, out var def, out _);

        var steps = WatchOrderParser.Expand(def!);

        Assert.Equal(1 + 3 + 1 + 1, steps.Count);
        Assert.Equal(new int?[] { 1, 2, 3 }, steps.Skip(1).Take(3).Select(s => s.Episode));
        Assert.All(steps.Skip(1).Take(3), s => Assert.Equal(1, s.EntryIndex));
        Assert.Equal(WatchOrderEntryType.Season, steps[^1].Type);
        Assert.Equal("Some Show S02E05", steps[4].Label);
    }

    [Theory]
    [InlineData("""{ "id": "Bad Id", "name": "x", "entries": [ { "type": "movie", "title": "x" } ] }""", "id must be")]
    [InlineData("""{ "id": "ok", "name": "", "entries": [ { "type": "movie", "title": "x" } ] }""", "name is required")]
    [InlineData("""{ "id": "ok", "name": "x", "entries": [] }""", "at least one entry")]
    [InlineData("""{ "id": "ok", "name": "x", "entries": [ { "type": "movie" } ] }""", "needs a title or providerIds")]
    [InlineData("""{ "id": "ok", "name": "x", "entries": [ { "type": "episode", "series": "s", "season": 1 } ] }""", "needs season and episode")]
    [InlineData("""{ "id": "ok", "name": "x", "entries": [ { "type": "episodes", "series": "s", "season": 1, "from": 5, "to": 2 } ] }""", "is after to")]
    [InlineData("""{ "id": "ok", "name": "x", "entries": [ { "type": "season" , "series": "s" } ] }""", "needs season")]
    [InlineData("""{ "schemaVersion": 99, "id": "ok", "name": "x", "entries": [ { "type": "movie", "title": "x" } ] }""", "newer than this plugin")]
    [InlineData("""{ "id": "ok", "name": "x", "entries": [ { "type": "movie", """, "Invalid JSON")]
    public void InvalidOrders_AreRejectedWithAClearError(string json, string expectedError)
    {
        var ok = WatchOrderParser.TryParse(json, out _, out var errors);

        Assert.False(ok);
        Assert.Contains(errors, e => e.Contains(expectedError, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("a/b")]
    [InlineData("")]
    public void UnsafeIds_AreRejected(string id)
    {
        var def = new WatchOrderDefinition { Id = id, Name = "x", Entries = { new WatchOrderEntry { Title = "x" } } };

        Assert.NotEmpty(WatchOrderParser.Validate(def));
    }

    [Fact]
    public void ShippedExampleOrders_AreAllValid()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "orders");
        var files = Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f) != "ready-made.json") // the list of bundled orders, not an order
            .ToList();

        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var ok = WatchOrderParser.TryParse(File.ReadAllText(file), out _, out var errors);
            Assert.True(ok, $"{Path.GetFileName(file)}: {string.Join("; ", errors)}");
        }
    }
}
