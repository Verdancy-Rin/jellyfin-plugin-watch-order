using System.Net.Mime;
using Jellyfin.Plugin.WatchOrder.Models;
using Jellyfin.Plugin.WatchOrder.Parsing;
using Jellyfin.Plugin.WatchOrder.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.WatchOrder.Api;

/// <summary>
/// REST API for watch orders. Read endpoints are open to any signed-in user;
/// write endpoints need an administrator.
/// </summary>
[ApiController]
[Authorize]
[Route("WatchOrder")]
[Produces(MediaTypeNames.Application.Json)]
public class WatchOrderController : ControllerBase
{
    // Jellyfin's user-id claim (InternalClaimTypes lives in Jellyfin.Api, which plugins don't reference).
    private const string UserIdClaim = "Jellyfin-UserId";
    private const string AdminPolicy = Policies.RequiresElevation;

    private readonly WatchOrderService _service;
    private readonly WatchOrderProgressService _progress;
    private readonly UserStateStore _userState;
    private readonly PlaylistSync _playlists;
    private readonly ILibraryManager _libraryManager;
    private readonly OrderStatsService _stats;
    private readonly OrderOriginStore _origins;
    private readonly ReadyMadeOrders _readyMade;
    private readonly OrderLinks _links;

    public WatchOrderController(
        WatchOrderService service,
        WatchOrderProgressService progress,
        UserStateStore userState,
        PlaylistSync playlists,
        ILibraryManager libraryManager,
        OrderStatsService stats,
        OrderOriginStore origins,
        ReadyMadeOrders readyMade,
        OrderLinks links)
    {
        _origins = origins;
        _readyMade = readyMade;
        _links = links;
        _stats = stats;
        _libraryManager = libraryManager;
        _service = service;
        _progress = progress;
        _userState = userState;
        _playlists = playlists;
    }

    /// <summary>Lists every order.</summary>
    /// <returns>Order summaries.</returns>
    [HttpGet("Orders")]
    public ActionResult<IEnumerable<object>> GetOrders()
    {
        return Ok(_service.GetDefinitions().Select(d =>
        {
            var origin = _origins.Get(d.Id);
            var bundled = origin is { Kind: "ReadyMade" } ? _readyMade.Get(d.Id) : null;
            return new
            {
                d.Id,
                d.Name,
                d.Description,
                d.Author,
                Version = d.Version ?? 1,
                d.Source,
                EntryCount = d.Entries.Count,
                MissingCount = _service.Get(d.Id)?.MissingCount,
                Origin = origin?.Kind,
                OriginUrl = origin?.Url,
                Edited = origin is not null && OrderOriginStore.IsEdited(origin, d),
                UpdateAvailable = bundled is not null && (bundled.Version ?? 1) > origin!.Version
            };
        }));
    }

    /// <summary>
    /// Lists every order with the current user's progress, for the user page.
    /// Orders with nothing the user can see in the library are left out.
    /// </summary>
    /// <returns>Progress summaries, started orders first.</returns>
    [HttpGet("Progress")]
    public ActionResult<IEnumerable<OrderProgressSummary>> GetProgress()
    {
        if (CurrentUserId() is not { } userId)
        {
            return Unauthorized();
        }

        var summaries = new List<OrderProgressSummary>();
        foreach (var definition in _service.GetDefinitions())
        {
            if (_service.Get(definition.Id) is not { } order || _progress.GetProgress(order, userId) is not { } progress
                || progress.AvailableCount == 0)
            {
                continue;
            }

            var homeState = _userState.GetHomeState(userId, definition.Id);
            summaries.Add(new OrderProgressSummary
            {
                Id = definition.Id,
                Name = definition.Name,
                Description = definition.Description,
                WatchedCount = progress.WatchedCount,
                AvailableCount = progress.AvailableCount,
                TotalCount = progress.Steps.Count(s => !s.Skipped),
                SkipOptional = progress.SkipOptional,
                SkippedCount = progress.SkippedCount,
                HomeState = homeState,
                OnHomeScreen = UserStateStore.IsOnHomeScreen(homeState, progress.Run.Chosen, progress.Next is null),
                Run = progress.Run,
                Next = progress.Next?.Step
            });
        }

        // In progress first, then not started, then finished; alphabetical within each group.
        return Ok(summaries
            .OrderBy(s => s.WatchedCount == 0 ? 1 : s.Next is null ? 2 : 0)
            .ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase));
    }

    /// <summary>
    /// Sets whether an order appears on the current user's home screen.
    /// </summary>
    /// <param name="id">The order id.</param>
    /// <param name="state"><c>Shown</c>, <c>Hidden</c>, or <c>Default</c> to go back to automatic.</param>
    /// <returns>204 on success.</returns>
    [HttpPost("Orders/{id}/HomeState")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult SetHomeState([FromRoute] string id, [FromQuery] HomeState state)
    {
        if (CurrentUserId() is not { } userId)
        {
            return Unauthorized();
        }

        if (_service.Get(id) is null)
        {
            return NotFound();
        }

        _userState.SetHomeState(userId, id, state);
        _playlists.Request(userId, id);
        return NoContent();
    }

    /// <summary>
    /// Chooses how the current user's watch-through of an order counts.
    /// <c>Continue</c> keeps counting what they've already watched (and does nothing if they've already chosen).
    /// <c>Fresh</c> starts a new watch-through from now, ignoring what was watched before, without changing
    /// anything in Jellyfin; it is also "Start over". A fresh start puts the order on the home screen and can be undone.
    /// </summary>
    /// <param name="id">The order id.</param>
    /// <param name="mode"><c>Continue</c> or <c>Fresh</c>.</param>
    /// <returns>The user's progress afterwards.</returns>
    [HttpPost("Orders/{id}/Run")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<UserProgress> StartRun([FromRoute] string id, [FromQuery] string mode)
    {
        var order = _service.Get(id);
        if (order is null || CurrentUserId() is not { } userId || _progress.GetProgress(order, userId) is not { } before)
        {
            return NotFound();
        }

        if (string.Equals(mode, "Continue", StringComparison.OrdinalIgnoreCase))
        {
            _userState.ContinueRun(userId, id);
        }
        else if (string.Equals(mode, "Fresh", StringComparison.OrdinalIgnoreCase))
        {
            var finished = before.Next is null && before.AvailableCount > 0;
            _userState.StartFresh(userId, id, _progress.GetPlayedNow(order, userId), finished, DateTime.UtcNow);
            if (_userState.GetHomeState(userId, id) != HomeState.Shown)
            {
                _userState.SetHomeState(userId, id, HomeState.Shown);
            }
        }
        else
        {
            return BadRequest("mode must be Continue or Fresh.");
        }

        _playlists.Request(userId, id);
        return GetOrder(id);
    }

    /// <summary>Sets whether the current user skips optional entries in an order.</summary>
    /// <param name="id">The order id.</param>
    /// <param name="skip">Skip them or not.</param>
    /// <returns>The user's progress afterwards.</returns>
    [HttpPost("Orders/{id}/SkipOptional")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<UserProgress> SetSkipOptional([FromRoute] string id, [FromQuery] bool skip)
    {
        if (_service.Get(id) is null || CurrentUserId() is not { } userId)
        {
            return NotFound();
        }

        _userState.SetSkipOptional(userId, id, skip);
        _playlists.Request(userId, id);
        return GetOrder(id);
    }

    /// <summary>Undoes the current user's last fresh start of an order, going back to the watch-through before it.</summary>
    /// <param name="id">The order id.</param>
    /// <returns>The user's progress afterwards, or 409 when there is nothing to undo.</returns>
    [HttpPost("Orders/{id}/Run/Undo")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<UserProgress> UndoRun([FromRoute] string id)
    {
        if (_service.Get(id) is null || CurrentUserId() is not { } userId)
        {
            return NotFound();
        }

        if (!_userState.UndoRun(userId, id))
        {
            return Conflict();
        }

        _playlists.Request(userId, id);
        return GetOrder(id);
    }

    /// <summary>
    /// Sets whether the current user keeps a playlist of what's left in an order, which any Jellyfin app can play
    /// to carry on the order. Turning it on also takes the order up. The playlist appears (or goes) within seconds.
    /// </summary>
    /// <param name="id">The order id.</param>
    /// <param name="on">Keep a playlist or not.</param>
    /// <returns>The user's progress afterwards.</returns>
    [HttpPost("Orders/{id}/Playlist")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<UserProgress> SetPlaylist([FromRoute] string id, [FromQuery] bool on)
    {
        if (_service.Get(id) is null || CurrentUserId() is not { } userId)
        {
            return NotFound();
        }

        _userState.SetPlaylistChoice(userId, id, on);
        _playlists.Request(userId, id);
        return GetOrder(id);
    }

    /// <summary>
    /// Marks an item in an order watched or not watched for the current user. In a fresh watch-through, marking
    /// it not watched only applies to that watch-through and leaves the user's Jellyfin history alone.
    /// </summary>
    /// <param name="id">The order id.</param>
    /// <param name="itemId">The item id.</param>
    /// <param name="watched">Watched or not.</param>
    /// <returns>204 on success.</returns>
    [HttpPost("Orders/{id}/Items/{itemId}/Watched")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult SetWatched([FromRoute] string id, [FromRoute] Guid itemId, [FromQuery] bool watched)
    {
        var order = _service.Get(id);
        if (order is null || CurrentUserId() is not { } userId)
        {
            return NotFound();
        }

        if (!_progress.SetWatched(order, userId, itemId, watched))
        {
            return NotFound();
        }

        _playlists.Request(userId, id);
        return NoContent();
    }

    /// <summary>Gets an order with the current user's progress.</summary>
    /// <param name="id">The order id.</param>
    /// <returns>The user's progress.</returns>
    [HttpGet("Orders/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<UserProgress> GetOrder([FromRoute] string id)
    {
        var order = _service.Get(id);
        if (order is null || CurrentUserId() is not { } userId)
        {
            return NotFound();
        }

        if (_progress.GetProgress(order, userId) is not { } progress)
        {
            return NotFound();
        }

        progress.HomeState = _userState.GetHomeState(userId, id);
        progress.OnHomeScreen = UserStateStore.IsOnHomeScreen(progress.HomeState, progress.Run.Chosen, progress.Next is null);
        var mode = UserStateStore.NormaliseAutoplayMode(Plugin.Instance?.Configuration.PlaylistSync);
        var choice = _userState.GetPlaylistChoice(userId, id);
        progress.Playlist = new PlaylistInfo
        {
            Mode = mode,
            On = UserStateStore.WantsPlaylist(mode, choice, progress.Run.Chosen || progress.HomeState == HomeState.Shown, hasItemsLeft: true),
            Id = _userState.GetPlaylistId(userId, id)
        };
        return Ok(progress);
    }

    /// <summary>
    /// Lists the orders a film or episode is in, with its place in each and what comes after it, for the item's page.
    /// For a series or season, lists the orders with entries from it.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <returns>The orders, in name order; empty when it's in none.</returns>
    [HttpGet("Items/{itemId}/Orders")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<ItemOrderInfo>> GetItemOrders([FromRoute] Guid itemId)
    {
        if (CurrentUserId() is not { } userId)
        {
            return Unauthorized();
        }

        var item = _libraryManager.GetItemById(itemId);
        Func<Guid, bool> matches;
        var single = true;
        switch (item)
        {
            case null:
                return Ok(Array.Empty<ItemOrderInfo>());
            case Series or Season:
                single = false;
                matches = id => _libraryManager.GetItemById(id) is Episode episode && (episode.SeriesId == itemId || episode.SeasonId == itemId);
                break;
            default:
                matches = id => id == itemId;
                break;
        }

        var result = new List<ItemOrderInfo>();
        foreach (var definition in _service.GetDefinitions())
        {
            if (_service.Get(definition.Id) is not { } order || !order.Steps.Any(s => s.ItemId is { } id && matches(id)))
            {
                continue;
            }

            if (_progress.GetProgress(order, userId) is { } progress && ItemOrders.Describe(order, progress, matches, single) is { } info)
            {
                result.Add(info);
            }
        }

        return Ok(result.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase));
    }

    /// <summary>Gets only the next item to watch, for a "Next in order" row or button.</summary>
    /// <param name="id">The order id.</param>
    /// <returns>The next step, or 204 when the user has finished the order.</returns>
    [HttpGet("Orders/{id}/Next")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<UserStep> GetNext([FromRoute] string id)
    {
        var order = _service.Get(id);
        if (order is null || CurrentUserId() is not { } userId || _progress.GetProgress(order, userId) is not { } progress)
        {
            return NotFound();
        }

        return progress.Next is null ? NoContent() : Ok(progress.Next);
    }

    /// <summary>Gets the raw definition, for exporting or editing.</summary>
    /// <param name="id">The order id.</param>
    /// <returns>The definition.</returns>
    [HttpGet("Orders/{id}/Definition")]
    [Authorize(Policy = AdminPolicy)]
    public ActionResult<WatchOrderDefinition> GetDefinition([FromRoute] string id)
    {
        var def = _service.GetDefinitions().FirstOrDefault(d => d.Id == id);
        return def is null ? NotFound() : Ok(def);
    }

    /// <summary>Gets the admin view of a resolved order, including which items are missing.</summary>
    /// <param name="id">The order id.</param>
    /// <returns>The resolved order.</returns>
    [HttpGet("Orders/{id}/Resolved")]
    [Authorize(Policy = AdminPolicy)]
    public ActionResult<ResolvedWatchOrder> GetResolved([FromRoute] string id)
    {
        return _service.Get(id) is { } order ? Ok(order) : NotFound();
    }

    /// <summary>Imports or replaces an order.</summary>
    /// <param name="definition">The order definition.</param>
    /// <param name="fromUrl">The link it was imported from, if it was; remembered so the copy can be checked against it later.</param>
    /// <returns>The resolved order, or validation errors.</returns>
    [HttpPost("Orders")]
    [Authorize(Policy = AdminPolicy)]
    [Consumes(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<ResolvedWatchOrder> SaveOrder([FromBody] WatchOrderDefinition definition, [FromQuery] string? fromUrl = null)
    {
        var errors = WatchOrderParser.Validate(definition);
        if (errors.Count > 0)
        {
            return BadRequest(new { Errors = errors });
        }

        var resolved = _service.Save(definition);
        if (!string.IsNullOrWhiteSpace(fromUrl) && StoredDefinition(definition.Id) is { } saved)
        {
            // Imported from a link: remember it, so the copy can be checked against it later.
            _origins.Set(saved, "Link", fromUrl.Trim());
        }

        return Ok(resolved);
    }

    /// <summary>
    /// Fetches an order file from a link, for importing. GitHub file pages and Gists are turned into their raw files.
    /// Limited to http(s) links and files up to 1 MB.
    /// </summary>
    /// <param name="url">The link.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The link fetched and the file's text, or an error message.</returns>
    [HttpGet("Fetch")]
    [Authorize(Policy = AdminPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> Fetch([FromQuery] string url, CancellationToken cancellationToken)
    {
        var (fetched, text, error) = await _links.FetchAsync(url, cancellationToken).ConfigureAwait(false);
        return error is null ? Ok(new { Url = fetched!.ToString(), Text = text }) : BadRequest(new { Errors = new[] { error } });
    }

    /// <summary>
    /// Lists the orders bundled with the plugin, grouped, with how much of each is in the library and whether it has
    /// been added (or edited since, or has an update).
    /// </summary>
    /// <returns>Groups of ready-made orders.</returns>
    [HttpGet("ReadyMade")]
    [Authorize(Policy = AdminPolicy)]
    public ActionResult GetReadyMade() => Ok(_readyMade.List().Select(g => new { Group = g.Group, g.Orders }));

    /// <summary>
    /// Adds a ready-made order, or updates it to the bundled version. Replacing an order that has been edited here, or
    /// another order with the same id, needs <paramref name="replace"/>.
    /// </summary>
    /// <param name="id">The ready-made order's id.</param>
    /// <param name="replace">Replace an edited copy, or another order with the same id.</param>
    /// <returns>The order's new state, 404 when there's no such ready-made order, or 409 with its state when it would
    /// replace something without <paramref name="replace"/>.</returns>
    [HttpPost("ReadyMade/{id}")]
    [Authorize(Policy = AdminPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult AddReadyMade([FromRoute] string id, [FromQuery] bool replace = false)
    {
        if (_readyMade.Get(id) is not { } bundled)
        {
            return NotFound();
        }

        var state = ReadyMadeOrders.StateOf(bundled, StoredDefinition(id), _origins.Get(id));
        if (!replace && state is "IdTaken" or "Edited" or "EditedUpdateAvailable")
        {
            return Conflict(new { State = state });
        }

        _service.Save(bundled);
        if (StoredDefinition(id) is { } saved)
        {
            _origins.Set(saved, "ReadyMade", null);
        }

        return Ok(new { State = "Added", Version = bundled.Version ?? 1 });
    }

    /// <summary>
    /// Matches a draft against the library without saving it. The builder calls this as entries
    /// change, so it accepts drafts that don't have an id or name yet and only checks the entries.
    /// </summary>
    /// <param name="draft">The draft order.</param>
    /// <returns>Entry errors, or the matched draft.</returns>
    [HttpPost("Preview")]
    [Authorize(Policy = AdminPolicy)]
    [Consumes(MediaTypeNames.Application.Json)]
    public ActionResult<PreviewResult> Preview([FromBody] WatchOrderDefinition draft)
    {
        var probe = new WatchOrderDefinition
        {
            Id = "preview",
            Name = string.IsNullOrWhiteSpace(draft.Name) ? "Preview" : draft.Name,
            Entries = draft.Entries ?? new List<WatchOrderEntry>()
        };

        var errors = WatchOrderParser.Validate(probe);
        return errors.Count > 0
            ? Ok(new PreviewResult { Errors = errors })
            : Ok(new PreviewResult { Order = _service.Preview(probe) });
    }

    /// <summary>Deletes an order.</summary>
    /// <param name="id">The order id.</param>
    /// <returns>204 on success.</returns>
    [HttpDelete("Orders/{id}")]
    [Authorize(Policy = AdminPolicy)]
    public ActionResult DeleteOrder([FromRoute] string id)
    {
        if (!_service.Delete(id))
        {
            return NotFound();
        }

        _origins.Remove(id);
        return NoContent();
    }

    /// <summary>
    /// How everyone is getting on with each order: who's part-way through, stalled, finished or yet to start,
    /// and each user's progress, next item and when they last watched something in it.
    /// </summary>
    /// <returns>One entry per order.</returns>
    [HttpGet("Stats")]
    [Authorize(Policy = AdminPolicy)]
    public ActionResult<IEnumerable<OrderStats>> GetStats() => Ok(_stats.GetAll(DateTime.UtcNow));

    /// <summary>Re-matches every order against the library now.</summary>
    /// <returns>204 on success.</returns>
    [HttpPost("Refresh")]
    [Authorize(Policy = AdminPolicy)]
    public ActionResult Refresh()
    {
        _service.RefreshAll();
        return NoContent();
    }

    // The order as stored, read back from its file, so fingerprints match what later reads see.
    private WatchOrderDefinition? StoredDefinition(string id) => _service.GetDefinitions().FirstOrDefault(d => d.Id == id);

    private Guid? CurrentUserId()
    {
        var value = User.FindFirst(UserIdClaim)?.Value;
        return Guid.TryParse(value, out var id) ? id : null;
    }
}
