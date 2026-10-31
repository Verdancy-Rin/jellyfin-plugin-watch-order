using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.WatchOrder.Models;
using Jellyfin.Plugin.WatchOrder.Parsing;
using Xunit;

namespace Jellyfin.Plugin.WatchOrder.Tests;

public sealed class FormatTests
{
    private const string FromANewerPlugin = """
        {
          "schemaVersion": 1,
          "id": "future",
          "name": "From a newer plugin",
          "version": 3,
          "source": "https://example.com/future.json",
          "artwork": { "poster": "https://example.com/p.jpg" },
          "entries": [
            { "type": "movie", "title": "A Film", "runtimeHint": 120, "tags": ["main"] }
          ]
        }
        """;

    [Fact]
    public void UnknownFields_SurviveReadingAndSaving()
    {
        Assert.True(WatchOrderParser.TryParse(FromANewerPlugin, out var def, out var errors), string.Join("; ", errors));
        Assert.Equal(3, def!.Version);
        Assert.Equal("https://example.com/future.json", def.Source);

        var saved = JsonNode.Parse(JsonSerializer.Serialize(def, WatchOrderParser.JsonOptions))!;
        Assert.Equal("https://example.com/p.jpg", (string?)saved["artwork"]!["poster"]);
        Assert.Equal(120, (int?)saved["entries"]![0]!["runtimeHint"]);
        Assert.Equal("main", (string?)saved["entries"]![0]!["tags"]![0]);
        Assert.Equal(3, (int?)saved["version"]);
    }

    [Fact]
    public void VersionAndSource_AreOptional()
    {
        Assert.True(WatchOrderParser.TryParse("""{ "id": "x", "name": "X", "entries": [ { "type": "movie", "title": "A" } ] }""", out var def, out _));
        Assert.Null(def!.Version);
        Assert.Null(def.Source);

        // And left out when saving a file that didn't have them.
        var saved = JsonSerializer.Serialize(def, WatchOrderParser.JsonOptions);
        Assert.DoesNotContain("\"version\"", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("\"source\"", saved, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"version\": 0", "version")]
    [InlineData("\"source\": \"ftp://example.com/x.json\"", "source")]
    [InlineData("\"source\": \"not a link\"", "source")]
    [InlineData("\"schemaVersion\": 2", "schemaVersion 2 is newer")]
    public void BadVersionOrSource_OrANewerFormat_IsRefusedClearly(string field, string message)
    {
        var json = "{ " + field + """, "id": "x", "name": "X", "entries": [ { "type": "movie", "title": "A" } ] }""";
        Assert.False(WatchOrderParser.TryParse(json, out _, out var errors));
        Assert.Contains(errors, e => e.Contains(message, StringComparison.Ordinal));
    }
}
