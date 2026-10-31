using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.WatchOrder.Models;
using Jellyfin.Plugin.WatchOrder.Parsing;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WatchOrder.Services;

/// <summary>Where an order on this server came from, so updates can be offered without overwriting local edits.</summary>
public sealed class OrderOrigin
{
    /// <summary>Gets or sets <c>ReadyMade</c> (bundled with the plugin) or <c>Link</c> (imported from a link).</summary>
    public string Kind { get; set; } = "ReadyMade";

    /// <summary>Gets or sets the link it was imported from, for <c>Link</c>.</summary>
    public string? Url { get; set; }

    /// <summary>Gets or sets the order's version when it was added.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Gets or sets a fingerprint of the order as added, to tell whether it has been edited since.</summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>Gets or sets when it was added (UTC).</summary>
    public DateTime AddedAt { get; set; }
}

/// <summary>
/// Remembers where each added order came from, in <c>.../WatchOrder/origins.json</c>. Orders made in the builder,
/// or imported from a file, have no entry.
/// </summary>
public class OrderOriginStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly ILogger<OrderOriginStore> _logger;
    private readonly object _lock = new();
    private ConcurrentDictionary<string, OrderOrigin>? _origins;

    public OrderOriginStore(IApplicationPaths applicationPaths, ILogger<OrderOriginStore> logger)
        : this(Path.Combine(applicationPaths.PluginConfigurationsPath, "WatchOrder", "origins.json"), logger)
    {
    }

    internal OrderOriginStore(string path, ILogger<OrderOriginStore> logger)
    {
        _path = path;
        _logger = logger;
    }

    /// <summary>Gets a fingerprint of an order's content, as it's saved.</summary>
    /// <param name="definition">The order.</param>
    /// <returns>A hex SHA-256 of the saved JSON.</returns>
    public static string HashOf(WatchOrderDefinition definition)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(definition, WatchOrderParser.JsonOptions))));

    /// <summary>Gets where an order came from, or null.</summary>
    /// <param name="orderId">The order id.</param>
    /// <returns>The origin, or null.</returns>
    public OrderOrigin? Get(string orderId) => Load().TryGetValue(orderId, out var origin) ? origin : null;

    /// <summary>Records where an order came from, as it is now.</summary>
    /// <param name="definition">The order as saved.</param>
    /// <param name="kind"><c>ReadyMade</c> or <c>Link</c>.</param>
    /// <param name="url">The link, for <c>Link</c>.</param>
    public void Set(WatchOrderDefinition definition, string kind, string? url)
    {
        Load()[definition.Id] = new OrderOrigin
        {
            Kind = kind,
            Url = url,
            Version = definition.Version ?? 1,
            Hash = HashOf(definition),
            AddedAt = DateTime.UtcNow
        };
        Save();
    }

    /// <summary>Forgets where an order came from (it was deleted, or replaced by something else).</summary>
    /// <param name="orderId">The order id.</param>
    public void Remove(string orderId)
    {
        if (Load().TryRemove(orderId, out _))
        {
            Save();
        }
    }

    /// <summary>Whether an order has been changed since it was added.</summary>
    /// <param name="origin">Where it came from.</param>
    /// <param name="current">The order now.</param>
    /// <returns><c>true</c> when edited.</returns>
    public static bool IsEdited(OrderOrigin origin, WatchOrderDefinition current) => !string.Equals(origin.Hash, HashOf(current), StringComparison.Ordinal);

    private ConcurrentDictionary<string, OrderOrigin> Load()
    {
        if (_origins is not null)
        {
            return _origins;
        }

        lock (_lock)
        {
            if (_origins is not null)
            {
                return _origins;
            }

            Dictionary<string, OrderOrigin>? read = null;
            try
            {
                if (File.Exists(_path))
                {
                    read = JsonSerializer.Deserialize<Dictionary<string, OrderOrigin>>(File.ReadAllText(_path), JsonOptions);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                _logger.LogWarning(ex, "Ignoring unreadable {File}", _path);
            }

            return _origins = new ConcurrentDictionary<string, OrderOrigin>(read ?? new Dictionary<string, OrderOrigin>(), StringComparer.Ordinal);
        }
    }

    private void Save()
    {
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new SortedDictionary<string, OrderOrigin>(Load(), StringComparer.Ordinal), JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
    }
}
