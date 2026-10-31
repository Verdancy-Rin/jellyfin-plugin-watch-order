using System.Collections.Concurrent;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.WatchOrder.Models;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WatchOrder.Services;

/// <summary>
/// Makes autoplay follow watch orders. When something in an order a user is following starts playing, and the
/// player's next queued item isn't the order's next, it asks the player to play the order's items next
/// (Jellyfin's "play next" remote command, which inserts them straight after the current item).
/// Works with players that report their queue and accept remote commands, such as the web app; others are left alone.
/// </summary>
public sealed class AutoplayFollower : IHostedService
{
    private readonly ISessionManager _sessionManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly WatchOrderService _orders;
    private readonly WatchOrderProgressService _progress;
    private readonly UserStateStore _userState;
    private readonly ILogger<AutoplayFollower> _logger;

    // The last playback handled in each session, so one start is never acted on twice.
    private readonly ConcurrentDictionary<string, string> _handled = new(StringComparer.Ordinal);

    public AutoplayFollower(
        ISessionManager sessionManager,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        WatchOrderService orders,
        WatchOrderProgressService progress,
        UserStateStore userState,
        ILogger<AutoplayFollower> logger)
    {
        _sessionManager = sessionManager;
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
        _sessionManager.PlaybackStart += OnPlayback;
        // Also on progress, in case the player's queue arrived after the start was reported.
        _sessionManager.PlaybackProgress += OnPlayback;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackStart -= OnPlayback;
        _sessionManager.PlaybackProgress -= OnPlayback;
        return Task.CompletedTask;
    }

    private async void OnPlayback(object? sender, PlaybackProgressEventArgs e)
    {
        try
        {
            await HandleAsync(e).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Never let this get in the way of playback.
            _logger.LogWarning(ex, "Watch Order: couldn't check autoplay for {Item}", e.Item?.Name);
        }
    }

    private async Task HandleAsync(PlaybackProgressEventArgs e)
    {
        var mode = Plugin.Instance?.Configuration.AutoplayFollow;
        if (UserStateStore.NormaliseAutoplayMode(mode) == "Off" || e.Item is null || e.Session is not { } session
            || !session.SupportsRemoteControl || session.UserId == Guid.Empty)
        {
            return;
        }

        var userId = session.UserId;
        if (!UserStateStore.AutoplayFollowsFor(mode, _userState.GetAutoplayFollow(userId)))
        {
            return;
        }

        var playing = e.Item.Id;
        var key = playing.ToString("N") + "|" + session.PlaylistItemId;
        if (_handled.TryGetValue(session.Id, out var last) && last == key)
        {
            return;
        }

        var queue = session.NowPlayingQueue;
        if (queue is null || !queue.Any(q => q.Id == playing))
        {
            return; // the player hasn't reported its queue (yet); try again on the next progress report
        }

        // One look per item started, whatever happens next.
        _handled[session.Id] = key;

        var queuedNext = QueuedNext(queue, session.PlaylistItemId, playing);
        if (queuedNext is null)
        {
            return; // nothing would autoplay; leave it that way
        }

        var choice = ChooseOrder(userId, playing, queuedNext.Value);
        if (choice is null)
        {
            return;
        }

        var (order, items) = choice.Value;
        await _sessionManager.SendPlayCommand(
            null,
            session.Id,
            new PlayRequest { ItemIds = items.ToArray(), PlayCommand = PlayCommand.PlayNext, ControllingUserId = userId },
            CancellationToken.None).ConfigureAwait(false);

        _logger.LogInformation(
            "Watch Order: queued {Next} after {Playing} for {User}, following \"{Order}\"",
            _libraryManager.GetItemById(items[0])?.Name,
            e.Item.Name,
            session.UserName,
            order.OrderName);
    }

    /// <summary>The item the player will play after the current one, from the queue it reported.</summary>
    /// <param name="queue">The queue the player reported.</param>
    /// <param name="playlistItemId">The queue entry now playing, when the player reported it.</param>
    /// <param name="playing">The item now playing.</param>
    /// <returns>The next item, or null when nothing is queued after it.</returns>
    internal static Guid? QueuedNext(IReadOnlyList<QueueItem>? queue, string? playlistItemId, Guid playing)
    {
        if (queue is null || queue.Count == 0)
        {
            return null;
        }

        var index = -1;
        if (!string.IsNullOrEmpty(playlistItemId))
        {
            for (var i = 0; i < queue.Count; i++)
            {
                if (queue[i].PlaylistItemId == playlistItemId)
                {
                    index = i;
                    break;
                }
            }
        }

        if (index < 0)
        {
            for (var i = 0; i < queue.Count; i++)
            {
                if (queue[i].Id == playing)
                {
                    index = i;
                    break;
                }
            }
        }

        return index >= 0 && index + 1 < queue.Count ? queue[index + 1].Id : null;
    }

    /// <summary>
    /// Of the orders the user is following that contain the item, the one to follow and what to queue.
    /// Prefers orders they've chosen how to watch, then ones they've put on their home screen, then the one
    /// they watched something from most recently.
    /// </summary>
    private (UserProgress Order, IReadOnlyList<Guid> Items)? ChooseOrder(Guid userId, Guid playing, Guid queuedNext)
    {
        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return null;
        }

        var candidates = new List<(UserProgress Progress, int Rank, DateTime LastActivity, IReadOnlyList<Guid> Items)>();
        foreach (var definition in _orders.GetDefinitions())
        {
            if (_orders.Get(definition.Id) is not { } order || !order.Steps.Any(s => s.ItemId == playing))
            {
                continue;
            }

            var homeState = _userState.GetHomeState(userId, definition.Id);
            var run = _userState.GetRun(userId, definition.Id);
            if (homeState == HomeState.Hidden || _progress.GetProgress(order, userId) is not { } progress)
            {
                continue;
            }

            if (run is null && homeState != HomeState.Shown)
            {
                continue; // not following this one: having watched some of it elsewhere isn't enough
            }

            var lastActivity = DateTime.MinValue;
            var steps = new List<PlanStep>(progress.Steps.Count);
            foreach (var step in progress.Steps)
            {
                var item = step.Step.ItemId is { } id ? _libraryManager.GetItemById(id) : null;
                steps.Add(new PlanStep(item is null ? null : step.Step.ItemId, step.Played, item?.GetBaseItemKind() == BaseItemKind.Episode, step.Skipped));
                if (item is not null && item.Id != playing && _userDataManager.GetUserData(user, item)?.LastPlayedDate is { } played && played > lastActivity)
                {
                    lastActivity = played;
                }
            }

            var items = AutoplayPlanner.Plan(steps, playing, queuedNext);
            if (items.Count > 0)
            {
                var rank = run is not null ? 0 : homeState == HomeState.Shown ? 1 : 2;
                candidates.Add((progress, rank, lastActivity, items));
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        var best = candidates.OrderBy(c => c.Rank).ThenByDescending(c => c.LastActivity).First();
        return (best.Progress, best.Items);
    }
}
