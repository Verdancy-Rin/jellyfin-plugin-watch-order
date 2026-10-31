using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.WatchOrder.Configuration;

/// <summary>
/// Server-wide settings for the plugin, edited on the dashboard config page.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether orders are re-matched automatically when the library changes.
    /// </summary>
    public bool AutoRefreshOnLibraryChange { get; set; } = true;

    /// <summary>
    /// Gets or sets how long to wait after the last library change before re-matching,
    /// so a large scan triggers one refresh rather than hundreds.
    /// </summary>
    public int RefreshDebounceSeconds { get; set; } = 60;

    /// <summary>
    /// Gets or sets a value indicating whether a fallback match on title and year is allowed
    /// when an entry has no provider ids, or none of them match.
    /// </summary>
    public bool AllowTitleMatching { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the Watch orders page is added to the web app for all users.
    /// Needs the File Transformation plugin.
    /// </summary>
    public bool ShowInWebApp { get; set; } = true;

    /// <summary>
    /// Gets or sets where the Watch Orders entry appears in the web app: <c>LibraryBar</c> (next to the
    /// libraries, and in the side menu), <c>TopBarIcon</c> (an icon next to the user menu) or <c>Both</c>.
    /// With <c>LibraryBar</c>, the icon is still shown on screens too narrow for the library bar.
    /// </summary>
    public string ButtonLocation { get; set; } = "LibraryBar";

    /// <summary>
    /// Gets or sets where the Watch Orders link sits in the library bar and side menu:
    /// <c>End</c> (after the libraries) or <c>AfterFavourites</c>.
    /// </summary>
    public string LibraryBarPosition { get; set; } = "End";

    /// <summary>
    /// Gets or sets a value indicating whether an "Up Next in Your Watch Orders" row is added to the home screen
    /// in the web app. Needs the File Transformation plugin.
    /// </summary>
    public bool ShowHomeRow { get; set; } = true;

    /// <summary>
    /// Gets or sets whether autoplay follows watch orders: when something in an order a user is following starts,
    /// the order's next item is queued after it instead of the player's usual next item. <c>OptOut</c> (on unless a
    /// user turns it off; the default), <c>OptIn</c> (users turn it on for themselves) or <c>Off</c>.
    /// </summary>
    public string AutoplayFollow { get; set; } = "OptOut";

    /// <summary>
    /// Gets or sets whether each user gets a playlist of what's left in the orders they're following, kept up to
    /// date as they watch, so any Jellyfin app can carry on an order. <c>OptOut</c> (on for orders a user has taken
    /// up, unless they turn it off; the default), <c>OptIn</c> (users turn it on per order) or <c>Off</c>.
    /// </summary>
    public string PlaylistSync { get; set; } = "OptOut";
}
