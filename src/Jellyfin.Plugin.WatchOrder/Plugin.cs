using Jellyfin.Plugin.WatchOrder.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.WatchOrder;

/// <summary>
/// Entry point for the Watch Order plugin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>The plugin's unique id. Must match <c>build.yaml</c> and <c>manifest.json</c>.</summary>
    public const string PluginGuid = "760c9732-e114-481f-b6c9-5a2a169111c1";

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>Gets the running plugin instance.</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Watch Order";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse(PluginGuid);

    /// <inheritdoc />
    public override string Description =>
        "Watch films and episodes from across a franchise in a single, defined order, with per-user progress.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        var ns = GetType().Namespace;

        yield return new PluginPageInfo
        {
            Name = "WatchOrder",
            DisplayName = "Watch Order",
            EmbeddedResourcePath = $"{ns}.Configuration.Web.configPage.html",
            EnableInMainMenu = true,
            MenuSection = "server",
            MenuIcon = "format_list_numbered"
        };

        yield return new PluginPageInfo
        {
            Name = "WatchOrderConfigJs",
            EmbeddedResourcePath = $"{ns}.Configuration.Web.configPage.js"
        };
    }
}
