namespace Jellyfin.Plugin.WatchOrder.WebClient;

/// <summary>
/// What the File Transformation plugin passes to <see cref="ScriptInjector.Transform"/>.
/// Its JSON is <c>{ "contents": "..." }</c>; property names are matched case-insensitively.
/// </summary>
public sealed class TransformPayload
{
    /// <summary>Gets or sets the current contents of the file being served.</summary>
    public string? Contents { get; set; }
}

/// <summary>
/// Adds the user page script to jellyfin-web's <c>index.html</c>. Called by the File Transformation
/// plugin through reflection, so the class and method names must stay stable.
/// </summary>
public static class ScriptInjector
{
    /// <summary>Attribute that marks the injected tag, so it is never added twice.</summary>
    public const string Marker = "data-watch-order-client";

    /// <summary>
    /// File Transformation callback for <c>index.html</c>.
    /// </summary>
    /// <param name="payload">The file contents.</param>
    /// <returns>The contents with the script tag added, or unchanged when the page is turned off.</returns>
    public static string Transform(TransformPayload payload)
    {
        var html = payload?.Contents ?? string.Empty;
        var plugin = Plugin.Instance;
        if (plugin is null || !plugin.Configuration.ShowInWebApp)
        {
            return html;
        }

        return Inject(html, plugin.Version.ToString());
    }

    /// <summary>
    /// Adds the script tag before <c>&lt;/body&gt;</c>.
    /// </summary>
    /// <param name="html">The page HTML.</param>
    /// <param name="version">Plugin version, used to bust the browser cache after updates.</param>
    /// <returns>The updated HTML.</returns>
    internal static string Inject(string html, string version)
    {
        if (string.IsNullOrEmpty(html) || html.Contains(Marker, StringComparison.Ordinal))
        {
            return html;
        }

        // Only ever change the HTML page itself. File Transformation matches "index.html" as a regular
        // expression, so other files (such as session-login-index-html.*.chunk.js) are passed here too;
        // adding a tag to those would break them.
        // JavaScript can contain "<html" or "</body>" inside strings, so require the file to start as an HTML document.
        var start = html.AsSpan().TrimStart();
        var isHtmlDocument = start.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase)
            || start.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
        var bodyEnd = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (!isHtmlDocument || bodyEnd < 0)
        {
            return html;
        }

        // index.html is served from <base url>/web/, so this resolves to <base url>/WatchOrder/ClientScript
        // and keeps working behind a reverse proxy with a base path.
        var tag = $"<script defer src=\"../WatchOrder/ClientScript?v={Uri.EscapeDataString(version)}\" {Marker}></script>";

        return html.Insert(bodyEnd, tag);
    }
}
