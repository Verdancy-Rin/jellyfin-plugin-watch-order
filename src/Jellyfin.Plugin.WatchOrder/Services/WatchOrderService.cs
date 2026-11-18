using System.Collections.Concurrent;
using Jellyfin.Plugin.WatchOrder.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WatchOrder.Services;

/// <summary>
/// Holds the resolved form of every order in memory, so API calls don't hit the library each time.
/// </summary>
public class WatchOrderService
{
    private readonly WatchOrderStore _store;
    private readonly WatchOrderResolver _resolver;
    private readonly ILogger<WatchOrderService> _logger;
    private readonly ConcurrentDictionary<string, ResolvedWatchOrder> _cache = new(StringComparer.Ordinal);

    public WatchOrderService(WatchOrderStore store, WatchOrderResolver resolver, ILogger<WatchOrderService> logger)
    {
        _store = store;
        _resolver = resolver;
        _logger = logger;
    }

    /// <summary>
    /// Raised after an order is saved or deleted (with its id), or after every order is re-matched (with null).
    /// </summary>
    public event EventHandler<string?>? OrdersChanged;

    /// <summary>Gets all order definitions.</summary>
    /// <returns>The definitions.</returns>
    public IReadOnlyList<WatchOrderDefinition> GetDefinitions() => _store.GetAll();

    /// <summary>
    /// Gets a resolved order, resolving it first if it isn't cached.
    /// </summary>
    /// <param name="id">The order id.</param>
    /// <returns>The resolved order, or null when no such order exists.</returns>
    public ResolvedWatchOrder? Get(string id)
    {
        if (_cache.TryGetValue(id, out var cached))
        {
            return cached;
        }

        var def = _store.Get(id);
        return def is null ? null : _cache[id] = _resolver.Resolve(def);
    }

    /// <summary>
    /// Matches a draft against the library without saving or caching it. Used by the builder
    /// to show live in-library / missing markers while an order is being edited.
    /// </summary>
    /// <param name="definition">A validated draft.</param>
    /// <returns>The resolved draft.</returns>
    public ResolvedWatchOrder Preview(WatchOrderDefinition definition) => _resolver.Resolve(definition);

    /// <summary>Looks up the films and series of several orders in one go, so resolving each of them is quick.</summary>
    /// <param name="definitions">The orders.</param>
    public void Prefetch(IEnumerable<WatchOrderDefinition> definitions) => _resolver.Prefetch(definitions);

    /// <summary>Saves an order and resolves it immediately.</summary>
    /// <param name="definition">A validated definition.</param>
    /// <returns>The resolved order.</returns>
    public ResolvedWatchOrder Save(WatchOrderDefinition definition)
    {
        _store.Save(definition);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var resolved = _cache[definition.Id] = _resolver.Resolve(definition);
        _logger.LogInformation(
            "Matched {Order}: {Found} of {Steps} films and episodes are in the library ({Seconds:0.0} s)",
            definition.Name,
            resolved.Steps.Count(s => s.ItemId is not null),
            resolved.Steps.Count,
            timer.Elapsed.TotalSeconds);
        OrdersChanged?.Invoke(this, definition.Id);
        return resolved;
    }

    /// <summary>Deletes an order.</summary>
    /// <param name="id">The order id.</param>
    /// <returns><c>true</c> when it existed.</returns>
    public bool Delete(string id)
    {
        _cache.TryRemove(id, out _);
        var deleted = _store.Delete(id);
        OrdersChanged?.Invoke(this, id);
        return deleted;
    }

    /// <summary>
    /// Re-resolves every order against the current library.
    /// </summary>
    /// <param name="progress">Optional progress (0-100).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public void RefreshAll(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var defs = _store.GetAll();

        // Every order's films and series in one go, rather than a query per order.
        _resolver.Prefetch(defs);

        // Each order is replaced as it's re-matched, so pages keep showing the old matches meanwhile.
        for (var i = 0; i < defs.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resolved = _resolver.Resolve(defs[i]);
            _cache[defs[i].Id] = resolved;
            _logger.LogDebug("Resolved {Id}: {Count} steps, {Missing} missing", resolved.Id, resolved.Steps.Count, resolved.MissingCount);
            progress?.Report(100.0 * (i + 1) / defs.Count);
        }

        var current = defs.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in _cache.Keys.Where(id => !current.Contains(id)).ToList())
        {
            _cache.TryRemove(gone, out _);
        }

        _logger.LogInformation("Refreshed {Count} watch orders in {Seconds:0.0} s", defs.Count, timer.Elapsed.TotalSeconds);
        OrdersChanged?.Invoke(this, null);
    }
}
