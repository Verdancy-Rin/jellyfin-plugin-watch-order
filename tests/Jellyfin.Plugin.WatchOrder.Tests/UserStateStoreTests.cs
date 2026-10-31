using Jellyfin.Plugin.WatchOrder.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.WatchOrder.Tests;

public sealed class UserStateStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wo-state-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    [Theory]
    [InlineData(HomeState.Default, false, false, false)] // not taken up, even if items in it were watched elsewhere
    [InlineData(HomeState.Default, true, false, true)]   // taken up on its page: shown
    [InlineData(HomeState.Shown, false, false, true)]    // added by hand
    [InlineData(HomeState.Hidden, true, false, false)]   // removed by the user
    [InlineData(HomeState.Shown, true, true, false)]     // finished: never shown
    [InlineData(HomeState.Default, true, true, false)]
    public void IsOnHomeScreen(HomeState state, bool following, bool finished, bool expected)
    {
        Assert.Equal(expected, UserStateStore.IsOnHomeScreen(state, following, finished));
    }

    [Fact]
    public void States_ArePerUser_AndSurviveARestart()
    {
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var store = new UserStateStore(_dir, NullLogger<UserStateStore>.Instance);

        store.SetHomeState(alice, "steven-universe", HomeState.Hidden);
        store.SetHomeState(bob, "steven-universe", HomeState.Shown);

        var reloaded = new UserStateStore(_dir, NullLogger<UserStateStore>.Instance);
        Assert.Equal(HomeState.Hidden, reloaded.GetHomeState(alice, "steven-universe"));
        Assert.Equal(HomeState.Shown, reloaded.GetHomeState(bob, "steven-universe"));
        Assert.Equal(HomeState.Default, reloaded.GetHomeState(alice, "other"));
    }

    [Fact]
    public void SettingDefault_ClearsTheChoice()
    {
        var user = Guid.NewGuid();
        var store = new UserStateStore(_dir, NullLogger<UserStateStore>.Instance);

        store.SetHomeState(user, "x", HomeState.Hidden);
        store.SetHomeState(user, "x", HomeState.Default);

        Assert.Equal(HomeState.Default, new UserStateStore(_dir, NullLogger<UserStateStore>.Instance).GetHomeState(user, "x"));
    }

    [Fact]
    public void UnreadableFile_IsIgnored()
    {
        var user = Guid.NewGuid();
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, user.ToString("N") + ".json"), "{ not json");

        var store = new UserStateStore(_dir, NullLogger<UserStateStore>.Instance);

        Assert.Equal(HomeState.Default, store.GetHomeState(user, "x"));
        store.SetHomeState(user, "x", HomeState.Shown); // and can be overwritten
        Assert.Equal(HomeState.Shown, new UserStateStore(_dir, NullLogger<UserStateStore>.Instance).GetHomeState(user, "x"));
    }
}
