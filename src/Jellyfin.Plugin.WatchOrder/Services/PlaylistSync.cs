using System.Collections.Concurrent;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Playlists;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WatchOrder.Services;

/// <summary>
/// Keeps a playlist of what's left in each order a user is following, owned by that user, so any Jellyfin app can
/// carry on an order: pressing Play on the playlist plays the next thing in the order. Watched items drop off the top
/// as they finish; the playlist is removed when the order is finished, and made again for a new watch-through.
/// A user deleting the playlist counts as turning it off for that order; turning it back on makes it again.
/// </summary>
public sealed class PlaylistSync : IHostedService, IDisposable
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    private readonly IPlaylistManager _playlists;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly WatchOrderService _orders;
    private readonly WatchOrderProgressService _progress;
    private readonly UserStateStore _userState;
    private readonly ILogger<PlaylistSync> _logger;

    // (user, order) pairs waiting to be synced; an empty order id means "every order for this user".
    private readonly ConcurrentDictionary<(Guid User, string Order), byte> _pending = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _stop = new();
    private Task? _worker;

    public PlaylistSync(
        IPlaylistManager playlists,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        WatchOrderService orders,
        WatchOrderProgressService progress,
        UserStateStore userState,
        ILogger<PlaylistSync> logger)
    {
        _playlists = playlists;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _orders = orders;
        _progress = progress;
        _userState = userState;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved += OnUserDataSaved;
        _libraryManager.ItemRemoved += OnItemRemoved;
        _orders.OrdersChanged += OnOrdersChanged;
        if (Plugin.Instance is { } plugin)
        {
            plugin.ConfigurationChanged += OnConfigurationChanged;
        }

        _worker = Task.Run(() => RunAsync(_stop.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved -= OnUserDataSaved;
        _libraryManager.ItemRemoved -= OnItemRemoved;
        _orders.OrdersChanged -= OnOrdersChanged;
        if (Plugin.Instance is { } plugin)
        {
            plugin.ConfigurationChanged -= OnConfigurationChanged;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        if (_worker is not null)
        {
            await Task.WhenAny(_worker, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stop.Dispose();
        _signal.Dispose();
    }

    /// <summary>Asks for one user's playlist of one order to be brought up to date shortly.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="orderId">The order.</param>
    public void Request(Guid userId, string orderId) => Enqueue(userId, orderId);

    /// <summary>Asks for every playlist of every user to be brought up to date shortly.</summary>
    public void RequestAll()
    {
        foreach (var userId in _userManager.GetUsersIds())
        {
            Enqueue(userId, string.Empty);
        }
    }

    private void Enqueue(Guid userId, string orderId)
    {
        if (_pending.TryAdd((userId, orderId), 0))
        {
            _signal.Release();
        }
    }

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        // Watched state changes when playback finishes or the user marks something; not while it plays.
        if (e.Item is null || e.SaveReason is UserDataSaveReason.PlaybackStart or UserDataSaveReason.PlaybackProgress or UserDataSaveReason.UpdateUserRating)
        {
            return;
        }

        var itemId = e.Item.Id;
        foreach (var definition in _orders.GetDefinitions())
        {
            if (_orders.Get(definition.Id) is { } order && order.Steps.Any(s => s.ItemId == itemId))
            {
                Enqueue(e.UserId, definition.Id);
            }
        }
    }

    private void OnItemRemoved(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is not Playlist playlist)
        {
            return;
        }

        // Our own removals forget the playlist first, so a match here means someone else deleted it.
        foreach (var userId in _userManager.GetUsersIds())
        {
            foreach (var (orderId, playlistId) in _userState.GetPlaylistIds(userId))
            {
                if (playlistId == playlist.Id)
                {
                    Enqueue(userId, orderId);
                }
            }
        }
    }

    private void OnOrdersChanged(object? sender, string? orderId)
    {
        foreach (var userId in _userManager.GetUsersIds())
        {
            Enqueue(userId, orderId ?? string.Empty);
        }
    }

    private void OnConfigurationChanged(object? sender, MediaBrowser.Model.Plugins.BasePluginConfiguration e) => RequestAll();

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            // Let the server finish starting (and orders resolve) before the first pass over everyone.
            await Task.Delay(StartupDelay, token).ConfigureAwait(false);
            RequestAll();

            while (!token.IsCancellationRequested)
            {
                await _signal.WaitAsync(token).ConfigureAwait(false);

                // Let a burst of changes (marking a season watched, say) settle into one update.
                await Task.Delay(Settle, token).ConfigureAwait(false);
                while (_signal.CurrentCount > 0)
                {
                    await _signal.WaitAsync(token).ConfigureAwait(false);
                }

                foreach (var key in _pending.Keys.ToList())
                {
                    _pending.TryRemove(key, out _);
                    await SyncKeyAsync(key.User, key.Order).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private async Task SyncKeyAsync(Guid userId, string orderId)
    {
        var orderIds = orderId.Length > 0
            ? new[] { orderId }
            : _orders.GetDefinitions().Select(d => d.Id).Concat(_userState.GetPlaylistIds(userId).Keys).Distinct(StringComparer.Ordinal).ToArray();

        foreach (var id in orderIds)
        {
            try
            {
                await SyncAsync(userId, id).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Watch Order: couldn't update the playlist of {Order} for user {User}", id, userId);
            }
        }
    }

    /// <summary>Brings one user's playlist of one order up to date now: makes, changes or removes it.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="orderId">The order.</param>
    /// <returns>A task.</returns>
    public async Task SyncAsync(Guid userId, string orderId)
    {
        if (_userManager.GetUserById(userId) is null)
        {
            return;
        }

        var playlistId = _userState.GetPlaylistId(userId, orderId);
        var playlist = playlistId is { } existingId ? _libraryManager.GetItemById(existingId) as Playlist : null;
        if (playlistId is not null && playlist is null)
        {
            // Deleted by the user (or an admin): that's "no playlist for this order" until they turn it back on.
            _userState.SetPlaylistId(userId, orderId, null);
            _userState.SetPlaylistChoice(userId, orderId, false);
            _logger.LogInformation("Watch Order: the playlist of {Order} for user {User} was deleted; turned it off for them", orderId, userId);
            return;
        }

        var order = _orders.Get(orderId);
        var progress = order is null ? null : _progress.GetProgress(order, userId);
        var wanted = progress is null ? Array.Empty<Guid>() : PlaylistPlanner.Remaining(progress);
        var takenUp = _userState.GetRun(userId, orderId) is not null || _userState.GetHomeState(userId, orderId) == HomeState.Shown;
        var mode = Plugin.Instance?.Configuration.PlaylistSync;
        var keep = order is not null && UserStateStore.WantsPlaylist(mode, _userState.GetPlaylistChoice(userId, orderId), takenUp, wanted.Count > 0);

        if (!keep)
        {
            if (playlist is not null)
            {
                // Forget it first, so its removal isn't taken for the user deleting it.
                _userState.SetPlaylistId(userId, orderId, null);
                _libraryManager.DeleteItem(playlist, new DeleteOptions { DeleteFileLocation = true }, true);
                _logger.LogInformation("Watch Order: removed the playlist of {Order} for user {User}", orderId, userId);
            }

            return;
        }

        var name = PlaylistPlanner.NameFor(order!.Name);
        if (playlist is null)
        {
            var created = await _playlists.CreatePlaylist(new PlaylistCreationRequest
            {
                Name = name,
                ItemIdList = wanted.ToArray(),
                UserId = userId,
                MediaType = MediaType.Video,
                Public = false
            }).ConfigureAwait(false);
            _userState.SetPlaylistId(userId, orderId, Guid.Parse(created.Id));
            _logger.LogInformation("Watch Order: made a playlist of {Order} for user {User} with {Count} items", orderId, userId, wanted.Count);
            return;
        }

        IReadOnlyList<Guid>? current = playlist.LinkedChildrenLoaded
            ? playlist.LinkedChildren.Where(c => c.ItemId.HasValue).Select(c => c.ItemId!.Value).ToList()
            : null;
        var change = PlaylistPlanner.Compare(current, wanted);
        switch (change.Kind)
        {
            case PlaylistChangeKind.Remove:
                await _playlists.RemoveItemFromPlaylistAsync(playlist.Id.ToString("N"), change.Remove.Select(id => id.ToString("N"))).ConfigureAwait(false);
                break;
            case PlaylistChangeKind.Replace:
                await _playlists.UpdatePlaylist(new PlaylistUpdateRequest { Id = playlist.Id, UserId = userId, Ids = wanted.ToArray() }).ConfigureAwait(false);
                break;
        }

        if (!string.Equals(playlist.Name, name, StringComparison.Ordinal))
        {
            await _playlists.UpdatePlaylist(new PlaylistUpdateRequest { Id = playlist.Id, UserId = userId, Name = name }).ConfigureAwait(false);
        }
    }
}
