using System.Text.Json;
using Jellyfin.Plugin.WatchOrder.Models;
using Jellyfin.Plugin.WatchOrder.Parsing;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WatchOrder.Services;

/// <summary>
/// Stores order definitions as one JSON file each in
/// <c>&lt;config&gt;/plugins/configurations/WatchOrder/orders/</c>, so admins can also
/// drop files in by hand or keep them in git.
/// </summary>
public class WatchOrderStore
{
    private readonly ILogger<WatchOrderStore> _logger;
    private readonly string _directory;

    public WatchOrderStore(IApplicationPaths applicationPaths, ILogger<WatchOrderStore> logger)
    {
        _logger = logger;
        _directory = Path.Combine(applicationPaths.PluginConfigurationsPath, "WatchOrder", "orders");
        Directory.CreateDirectory(_directory);
    }

    /// <summary>Gets the folder that holds order files.</summary>
    public string OrdersDirectory => _directory;

    /// <summary>
    /// Loads every valid order. Invalid files are logged and skipped.
    /// </summary>
    /// <returns>The definitions, sorted by name.</returns>
    public IReadOnlyList<WatchOrderDefinition> GetAll()
    {
        var result = new List<WatchOrderDefinition>();

        foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            var def = Load(file);
            if (def is not null)
            {
                result.Add(def);
            }
        }

        return result.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Loads one order by id.</summary>
    /// <param name="id">The order id.</param>
    /// <returns>The definition, or null when missing or invalid.</returns>
    public WatchOrderDefinition? Get(string id)
    {
        var path = PathFor(id);
        return path is not null && File.Exists(path) ? Load(path) : null;
    }

    /// <summary>Writes an order, replacing any existing order with the same id.</summary>
    /// <param name="definition">A validated definition.</param>
    public void Save(WatchOrderDefinition definition)
    {
        var path = PathFor(definition.Id) ?? throw new ArgumentException("Invalid order id.", nameof(definition));
        File.WriteAllText(path, JsonSerializer.Serialize(definition, WatchOrderParser.JsonOptions));
        _logger.LogInformation("Saved watch order {Id}", definition.Id);
    }

    /// <summary>Deletes an order.</summary>
    /// <param name="id">The order id.</param>
    /// <returns><c>true</c> when a file was deleted.</returns>
    public bool Delete(string id)
    {
        var path = PathFor(id);
        if (path is null || !File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        _logger.LogInformation("Deleted watch order {Id}", id);
        return true;
    }

    private WatchOrderDefinition? Load(string path)
    {
        try
        {
            if (WatchOrderParser.TryParse(File.ReadAllText(path), out var def, out var errors))
            {
                return def;
            }

            _logger.LogWarning("Skipping invalid watch order {File}: {Errors}", Path.GetFileName(path), string.Join("; ", errors));
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not read watch order {File}", path);
        }

        return null;
    }

    // Ids are validated as [a-z0-9-] slugs, so this also guards against path traversal.
    private string? PathFor(string id)
    {
        var probe = new WatchOrderDefinition { Id = id, Name = "x", Entries = { new WatchOrderEntry { Title = "x" } } };
        return WatchOrderParser.Validate(probe).Count == 0 ? Path.Combine(_directory, id + ".json") : null;
    }
}
