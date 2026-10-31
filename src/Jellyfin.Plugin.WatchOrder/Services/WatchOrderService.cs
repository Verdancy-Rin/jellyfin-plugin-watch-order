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

    /// <summary>Saves an order and resolves it immediately.</summary>
    /// <param name="definition">A validated definition.</param>
    /// <returns>The resolved order.</returns>
    public ResolvedWatchOrder Save(WatchOrderDefinition definition)
    {
        _store.Save(definition);
        var resolved = _cache[definition.Id] = _resolver.Resolve(definition);
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
        var defs = _store.GetAll();
        _cache.Clear();

        for (var i = 0; i < defs.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resolved = _resolver.Resolve(defs[i]);
            _cache[defs[i].Id] = resolved;
            _logger.LogDebug("Resolved {Id}: {Count} steps, {Missing} missing", resolved.Id, resolved.Steps.Count, resolved.MissingCount);
            progress?.Report(100.0 * (i + 1) / defs.Count);
        }

        _logger.LogInformation("Refreshed {Count} watch orders", defs.Count);
        OrdersChanged?.Invoke(this, null);
    }
}
