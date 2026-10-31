using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Jellyfin.Plugin.WatchOrder.Models;
using Jellyfin.Plugin.WatchOrder.Parsing;

namespace Jellyfin.Plugin.WatchOrder.Services;

/// <summary>A group of ready-made orders, e.g. "Star Wars".</summary>
public sealed class ReadyMadeGroup
{
    /// <summary>Gets or sets the group's name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets its orders, in the order to show them.</summary>
    public List<WatchOrderDefinition> Orders { get; set; } = new();
}

/// <summary>A ready-made order and how it stands on this server, for the dashboard.</summary>
public sealed class ReadyMadeInfo
{
    /// <summary>Gets or sets the order id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the description.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the bundled version.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Gets or sets how many entries it has.</summary>
    public int EntryCount { get; set; }

    /// <summary>Gets or sets how many of its films and episodes are in the library.</summary>
    public int InLibrary { get; set; }

    /// <summary>Gets or sets how many films and episodes it has (seasons count once per episode found, or once if none are).</summary>
    public int ItemCount { get; set; }

    /// <summary>
    /// Gets or sets how it stands: <c>NotAdded</c>, <c>Added</c>, <c>UpdateAvailable</c>, <c>Edited</c> (added, then
    /// changed here), <c>EditedUpdateAvailable</c>, or <c>IdTaken</c> (another order here has the same id).
    /// </summary>
    public string State { get; set; } = "NotAdded";

    /// <summary>Gets or sets the version added, when it has been.</summary>
    public int? AddedVersion { get; set; }
}

/// <summary>
/// The orders bundled with the plugin. Admins add the ones they want from the dashboard; nothing is added on its own.
/// </summary>
public class ReadyMadeOrders
{
    private const string Prefix = "ReadyMade/";

    private readonly WatchOrderService _orders;
    private readonly OrderOriginStore _origins;
    private readonly IReadOnlyList<ReadyMadeGroup> _groups;
    private readonly ConcurrentDictionary<string, (int InLibrary, int Items)> _matchCache = new(StringComparer.Ordinal);

    public ReadyMadeOrders(WatchOrderService orders, OrderOriginStore origins)
    {
        _orders = orders;
        _origins = origins;
        _groups = Load(typeof(ReadyMadeOrders).Assembly);
        _orders.OrdersChanged += (_, id) =>
        {
            if (id is null)
            {
                _matchCache.Clear(); // the library changed
            }
        };
    }

    /// <summary>Gets the bundled groups and orders.</summary>
    public IReadOnlyList<ReadyMadeGroup> Groups => _groups;

    /// <summary>Gets a bundled order by id.</summary>
    /// <param name="id">The order id.</param>
    /// <returns>The order, or null.</returns>
    public WatchOrderDefinition? Get(string id) => _groups.SelectMany(g => g.Orders).FirstOrDefault(o => o.Id == id);

    /// <summary>Gets every group with how each order stands here.</summary>
    /// <returns>Groups of orders.</returns>
    public IReadOnlyList<(string Group, IReadOnlyList<ReadyMadeInfo> Orders)> List()
    {
        var installed = _orders.GetDefinitions().ToDictionary(d => d.Id, StringComparer.Ordinal);
        return _groups.Select(g => (g.Name, (IReadOnlyList<ReadyMadeInfo>)g.Orders.Select(o => Describe(o, installed)).ToList())).ToList();
    }

    /// <summary>Works out how a bundled order stands against what's on the server.</summary>
    /// <param name="bundled">The bundled order.</param>
    /// <param name="current">The order with that id on the server, if any.</param>
    /// <param name="origin">Where the server's copy came from, if recorded.</param>
    /// <returns>The state name.</returns>
    public static string StateOf(WatchOrderDefinition bundled, WatchOrderDefinition? current, OrderOrigin? origin)
    {
        if (current is null)
        {
            return "NotAdded";
        }

        if (origin is not { Kind: "ReadyMade" })
        {
            return "IdTaken";
        }

        var newer = (bundled.Version ?? 1) > origin.Version;
        var edited = OrderOriginStore.IsEdited(origin, current);
        return (newer, edited) switch
        {
            (true, true) => "EditedUpdateAvailable",
            (true, false) => "UpdateAvailable",
            (false, true) => "Edited",
            _ => "Added"
        };
    }

    private ReadyMadeInfo Describe(WatchOrderDefinition bundled, Dictionary<string, WatchOrderDefinition> installed)
    {
        installed.TryGetValue(bundled.Id, out var current);
        var origin = _origins.Get(bundled.Id);
        var (inLibrary, items) = _matchCache.GetOrAdd(bundled.Id + "@" + (bundled.Version ?? 1), _ =>
        {
            var resolved = _orders.Preview(bundled);
            return (resolved.Steps.Count(s => s.ItemId is not null), resolved.Steps.Count);
        });

        return new ReadyMadeInfo
        {
            Id = bundled.Id,
            Name = bundled.Name,
            Description = bundled.Description,
            Version = bundled.Version ?? 1,
            EntryCount = bundled.Entries.Count,
            InLibrary = inLibrary,
            ItemCount = items,
            State = StateOf(bundled, current, origin),
            AddedVersion = origin is { Kind: "ReadyMade" } && current is not null ? origin.Version : null
        };
    }

    internal static IReadOnlyList<ReadyMadeGroup> Load(Assembly assembly)
    {
        using var catalogueStream = assembly.GetManifestResourceStream(Prefix + "ready-made.json")
            ?? throw new InvalidOperationException("The ready-made order list is missing from the plugin.");
        using var catalogue = JsonDocument.Parse(catalogueStream);

        var groups = new List<ReadyMadeGroup>();
        foreach (var group in catalogue.RootElement.GetProperty("groups").EnumerateArray())
        {
            var result = new ReadyMadeGroup { Name = group.GetProperty("name").GetString() ?? string.Empty };
            foreach (var file in group.GetProperty("orders").EnumerateArray())
            {
                using var stream = assembly.GetManifestResourceStream(Prefix + file.GetString())
                    ?? throw new InvalidOperationException($"Ready-made order {file} is missing from the plugin.");
                using var reader = new StreamReader(stream);
                if (!WatchOrderParser.TryParse(reader.ReadToEnd(), out var definition, out var errors))
                {
                    throw new InvalidOperationException($"Ready-made order {file} is invalid: {string.Join("; ", errors)}");
                }

                // The file's "$schema" points into the repo, which means nothing once it's on a server.
                definition!.Extra?.Remove("$schema");
                result.Orders.Add(definition);
            }

            groups.Add(result);
        }

        return groups;
    }
}
