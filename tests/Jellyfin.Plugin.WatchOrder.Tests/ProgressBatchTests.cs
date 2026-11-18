using System.Reflection;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.WatchOrder.Models;
using Jellyfin.Plugin.WatchOrder.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.WatchOrder.Tests;

/// <summary>
/// Progress loads an order's items in batches rather than one at a time: Jellyfin's item cache is small, so for a
/// long order one-at-a-time means a database query per item, every time progress is worked out.
/// </summary>
public sealed class ProgressBatchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wo-progress-" + Guid.NewGuid().ToString("N"));
    private readonly User _user = new("ryan", "Default", "Default");
    private readonly List<Movie> _films = new();
    private readonly HashSet<Guid> _watched = new();
    private readonly FakeLibrary _library;
    private readonly WatchOrderProgressService _progress;

    public ProgressBatchTests()
    {
        for (var i = 0; i < 600; i++)
        {
            _films.Add(new Movie { Id = Guid.NewGuid(), Name = "Film " + i });
        }

        _library = new FakeLibrary(_films);
        var users = Fake<IUserManager>((method, args) => method == "GetUserById" ? _user : null);
        var userData = Fake<IUserDataManager>((method, args) => method == "GetUserData"
            ? new UserItemData { Key = "k", Played = _watched.Contains(((BaseItem)args[1]!).Id) }
            : null);
        _progress = new WatchOrderProgressService(users, userData, _library.Proxy, new UserStateStore(_dir, NullLogger<UserStateStore>.Instance));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private ResolvedWatchOrder Order(int missing = 0)
    {
        var order = new ResolvedWatchOrder { Id = "long", Name = "Long" };
        foreach (var film in _films)
        {
            order.Steps.Add(new ResolvedStep { Position = order.Steps.Count + 1, ItemId = film.Id, Label = film.Name, Status = MatchStatus.Matched });
        }

        for (var i = 0; i < missing; i++)
        {
            order.Steps.Add(new ResolvedStep { Position = order.Steps.Count + 1, Label = "Missing " + i, Status = MatchStatus.Missing });
        }

        return order;
    }

    [Fact]
    public void LongOrder_IsLoadedInAFewQueries_NotOnePerItem()
    {
        _watched.UnionWith(_films.Take(250).Select(f => f.Id));
        var progress = _progress.GetProgress(Order(missing: 2), _user.Id)!;

        Assert.Equal(0, _library.ByIdCalls);
        Assert.Equal(3, _library.ListCalls); // 600 items, 250 per query
        Assert.Equal(600, progress.AvailableCount);
        Assert.Equal(250, progress.WatchedCount);
        Assert.Equal(_films[250].Id, progress.Next!.Step.ItemId);
        Assert.Equal(2, progress.Steps.Count(s => !s.Available));
    }

    [Fact]
    public void ItemsNoLongerInTheLibrary_AreNotAvailable()
    {
        _library.Remove(_films[10].Id);
        var progress = _progress.GetProgress(Order(), _user.Id)!;
        Assert.False(progress.Steps[10].Available);
        Assert.Equal(599, progress.AvailableCount);
    }

    [Fact]
    public void ItemsLoadedOnce_CanBeUsedForSeveralUsers()
    {
        var order = Order();
        var items = ItemBatch.ForSteps(_library.Proxy, order.Steps);
        var calls = _library.ListCalls;
        _progress.GetProgress(order, _user.Id, items);
        _progress.GetProgress(order, _user.Id, items);
        Assert.Equal(calls, _library.ListCalls);
    }

    [Fact]
    public void PlayedNow_UsesTheBatchToo()
    {
        _watched.Add(_films[3].Id);
        var played = _progress.GetPlayedNow(Order(), _user.Id);
        Assert.Equal(0, _library.ByIdCalls);
        Assert.True(played[_films[3].Id.ToString("N")]);
        Assert.Equal(600, played.Count);
    }

    private static T Fake<T>(Func<string, object?[], object?> handler)
        where T : class
    {
        var proxy = DispatchProxy.Create<T, Handler>();
        ((Handler)(object)proxy).OnCall = handler;
        return proxy;
    }

    internal static T FakeOf<T>(Func<string, object?[], object?> handler)
        where T : class => Fake<T>(handler);

    public class Handler : DispatchProxy
    {
        public Func<string, object?[], object?> OnCall { get; set; } = (_, _) => null;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => OnCall(targetMethod!.Name, args ?? Array.Empty<object?>());
    }

    private sealed class FakeLibrary
    {
        private readonly Dictionary<Guid, BaseItem> _items;

        public FakeLibrary(IEnumerable<BaseItem> items)
        {
            _items = items.ToDictionary(i => i.Id);
            Proxy = Fake<ILibraryManager>((method, args) =>
            {
                switch (method)
                {
                    case "GetItemList":
                        ListCalls++;
                        var query = (InternalItemsQuery)args[0]!;
                        Assert.True(query.ItemIds.Length <= 250);
                        Assert.True(query.IncludeOwnedItems);
                        Assert.False(query.DtoOptions.EnableImages);
                        Assert.True(query.DtoOptions.EnableUserData);
                        return (IReadOnlyList<BaseItem>)query.ItemIds.Where(_items.ContainsKey).Select(id => _items[id]).ToList();
                    case "GetItemById":
                        ByIdCalls++;
                        return _items.GetValueOrDefault((Guid)args[0]!);
                    default:
                        return null;
                }
            });
        }

        public ILibraryManager Proxy { get; }

        public int ListCalls { get; private set; }

        public int ByIdCalls { get; private set; }

        public void Remove(Guid id) => _items.Remove(id);
    }
}
