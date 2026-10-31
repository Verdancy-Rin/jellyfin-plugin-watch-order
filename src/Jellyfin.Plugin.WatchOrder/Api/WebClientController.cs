using System.Reflection;
using Jellyfin.Plugin.WatchOrder.Services;
using Jellyfin.Plugin.WatchOrder.WebClient;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.WatchOrder.Api;

/// <summary>
/// Serves the user page script and reports whether it is active.
/// </summary>
[ApiController]
[Route("WatchOrder")]
public class WebClientController : ControllerBase
{
    private const string ScriptResource = "Jellyfin.Plugin.WatchOrder.WebClient.client.js";
    private const string UserIdClaim = "Jellyfin-UserId";

    private readonly UserStateStore _userState;

    public WebClientController(UserStateStore userState)
    {
        _userState = userState;
    }

    /// <summary>
    /// The user page script. Anonymous because the web app loads it before anyone signs in;
    /// it contains no data, and every API call it makes is authenticated.
    /// </summary>
    /// <returns>The script.</returns>
    [HttpGet("ClientScript")]
    [AllowAnonymous]
    [Produces("application/javascript")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetClientScript()
    {
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ScriptResource);
        if (stream is null)
        {
            return NotFound();
        }

        // The URL carries the plugin version, so it can be cached for a long time.
        Response.Headers.CacheControl = "public, max-age=86400";
        return File(stream, "application/javascript; charset=utf-8");
    }

    /// <summary>Settings the user page needs, readable by any signed-in user, including their own autoplay choice.</summary>
    /// <returns>The settings.</returns>
    [HttpGet("ClientSettings")]
    [Authorize]
    public ActionResult<object> GetClientSettings()
    {
        var location = Plugin.Instance?.Configuration.ButtonLocation;
        var autoplayMode = UserStateStore.NormaliseAutoplayMode(Plugin.Instance?.Configuration.AutoplayFollow);
        var userId = CurrentUserId();
        return Ok(new
        {
            ButtonLocation = location is "TopBarIcon" or "Both" ? location : "LibraryBar",
            LibraryBarPosition = Plugin.Instance?.Configuration.LibraryBarPosition == "AfterFavourites" ? "AfterFavourites" : "End",
            ShowHomeRow = Plugin.Instance?.Configuration.ShowHomeRow ?? true,
            AutoplayFollowMode = autoplayMode,
            AutoplayFollow = userId is { } id && UserStateStore.AutoplayFollowsFor(autoplayMode, _userState.GetAutoplayFollow(id))
        });
    }

    /// <summary>Sets whether autoplay follows the current user's watch orders.</summary>
    /// <param name="autoplayFollow">On or off.</param>
    /// <returns>204 on success.</returns>
    [HttpPost("UserSettings")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public ActionResult SetUserSettings([FromQuery] bool autoplayFollow)
    {
        if (CurrentUserId() is not { } userId)
        {
            return Unauthorized();
        }

        _userState.SetAutoplayFollow(userId, autoplayFollow);
        return NoContent();
    }

    private Guid? CurrentUserId()
    {
        var value = User.FindFirst(UserIdClaim)?.Value;
        return Guid.TryParse(value, out var id) ? id : null;
    }

    /// <summary>Whether the user page is set up, for the admin page.</summary>
    /// <returns>Status flags.</returns>
    [HttpGet("WebClientStatus")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<object> GetStatus()
    {
        return Ok(new
        {
            FileTransformationInstalled = FileTransformationRegistrar.IsRegistered,
            Enabled = Plugin.Instance?.Configuration.ShowInWebApp ?? false
        });
    }
}
