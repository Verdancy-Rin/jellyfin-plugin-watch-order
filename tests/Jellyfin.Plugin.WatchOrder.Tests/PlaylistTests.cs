using Jellyfin.Plugin.WatchOrder.Models;
using Jellyfin.Plugin.WatchOrder.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.WatchOrder.Tests;

public sealed class PlaylistTests : IDisposable
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();
    private static readonly Guid C = Guid.NewGuid();
    private static readonly Guid D = Guid.NewGuid();

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wo-pl-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private static UserStep Step(Guid? id, bool played = false, bool available = true, bool skipped = false)
        => new() { Step = new ResolvedStep { ItemId = id, Label = "x" }, Played = played, Available = available, Skipped = skipped };

    [Fact]
    public void Remaining_IsWhatsLeftInOrder()
    {
        var progress = new UserProgress
        {
            Steps =
            {
                Step(A, played: true),
                Step(null, available: false),          // not in the library
                Step(B),
                Step(C, skipped: true),                // optional, skipped
                Step(D),
                Step(B),                               // listed twice: once, at its first place
            }
        };
        Assert.Equal(new[] { B, D }, PlaylistPlanner.Remaining(progress));
    }

    [Fact]
    public void Compare_Same_DoesNothing()
    {
        Assert.Equal(PlaylistChangeKind.None, PlaylistPlanner.Compare(new[] { A, B }, new[] { A, B }).Kind);
    }

    [Fact]
    public void Compare_SomethingWatched_TakesItOut()
    {
        var change = PlaylistPlanner.Compare(new[] { A, B, C }, new[] { B, C });
        Assert.Equal(PlaylistChangeKind.Remove, change.Kind);
        Assert.Equal(new[] { A }, change.Remove);

        // Out of order too (marked a later one watched).
        change = PlaylistPlanner.Compare(new[] { A, B, C }, new[] { A, C });
        Assert.Equal(new[] { B }, change.Remove);
    }

    [Fact]
    public void Compare_SomethingNewOrMoved_ReplacesTheContents()
    {
        Assert.Equal(PlaylistChangeKind.Replace, PlaylistPlanner.Compare(new[] { B, C }, new[] { A, B, C }).Kind); // un-watched / new in library
        Assert.Equal(PlaylistChangeKind.Replace, PlaylistPlanner.Compare(new[] { A, B }, new[] { B, A }).Kind);    // order edited
        Assert.Equal(PlaylistChangeKind.Replace, PlaylistPlanner.Compare(new[] { A, B, A }, new[] { A, B }).Kind); // someone added a copy
        Assert.Equal(PlaylistChangeKind.Replace, PlaylistPlanner.Compare(null, new[] { A }).Kind);                 // contents unknown
    }

    [Theory]
    [InlineData("OptOut", null, true, true, true)]     // taken up: on by default
    [InlineData("OptOut", null, false, true, false)]   // not taken up: no playlist
    [InlineData("OptOut", false, true, true, false)]   // turned off (or deleted it)
    [InlineData("OptOut", true, false, true, true)]    // turned on: that's taking it up
    [InlineData("OptIn", null, true, true, false)]     // opt-in: only when they turn it on
    [InlineData("OptIn", true, false, true, true)]
    [InlineData("Off", true, true, true, false)]       // off for everyone
    [InlineData("OptOut", true, true, false, false)]   // finished: removed until a new watch-through
    [InlineData(null, null, true, true, true)]         // unset: the default
    public void WantsPlaylist(string? mode, bool? choice, bool takenUp, bool left, bool expected)
    {
        Assert.Equal(expected, UserStateStore.WantsPlaylist(mode, choice, takenUp, left));
    }

    [Fact]
    public void Store_KeepsChoicesAndPlaylistIds_AcrossARestart()
    {
        var user = Guid.NewGuid();
        var store = new UserStateStore(_dir, NullLogger<UserStateStore>.Instance);
        Assert.Null(store.GetPlaylistChoice(user, "mcu"));
        Assert.Null(store.GetPlaylistId(user, "mcu"));

        store.SetPlaylistChoice(user, "mcu", false);
        store.SetPlaylistChoice(user, "su", true);
        store.SetPlaylistId(user, "su", A);

        var reloaded = new UserStateStore(_dir, NullLogger<UserStateStore>.Instance);
        Assert.False(reloaded.GetPlaylistChoice(user, "mcu"));
        Assert.True(reloaded.GetPlaylistChoice(user, "su"));
        Assert.Equal(A, reloaded.GetPlaylistId(user, "su"));
        Assert.Single(reloaded.GetPlaylistIds(user));

        // Turned back on after turning it off; and the playlist forgotten when it goes.
        reloaded.SetPlaylistChoice(user, "mcu", true);
        reloaded.SetPlaylistId(user, "su", null);
        Assert.True(reloaded.GetPlaylistChoice(user, "mcu"));
        Assert.Empty(reloaded.GetPlaylistIds(user));
    }

    [Fact]
    public void Name_SortsTogether()
    {
        Assert.Equal("Watch Order: MCU Phase One", PlaylistPlanner.NameFor("MCU Phase One"));
    }
}
