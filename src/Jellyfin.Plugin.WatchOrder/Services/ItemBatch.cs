using System.Diagnostics;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WatchOrder.Services;

/// <summary>
/// Loads an order's items, with everyone's watched data, in a few queries. Asking Jellyfin for them one at a time
/// (<c>GetItemById</c>) is fine for a few items, but its item cache is small (100 items per processor core by
/// default), so for a long order such as Naruto and Boruto each item comes from the database again every time,
/// one query at a time, with its images and everything else attached.
/// </summary>
public static class ItemBatch
{
    // Ids per query, so a very long order doesn't make one enormous SQL statement.
    private const int ChunkSize = 250;

    /// <summary>Loads the items with the given ids; ids not in the library are left out.</summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="ids">Item ids, in any order; repeats are fine.</param>
    /// <param name="logger">Where to say how long it took, or null.</param>
    /// <returns>The items by id, each with its watched data for every user.</returns>
    public static Dictionary<Guid, BaseItem> Load(ILibraryManager libraryManager, IEnumerable<Guid> ids, ILogger? logger = null)
    {
        var timer = Stopwatch.StartNew();
        var result = new Dictionary<Guid, BaseItem>();
        foreach (var chunk in ids.Where(id => id != Guid.Empty).Distinct().Chunk(ChunkSize))
        {
            var items = libraryManager.GetItemList(new InternalItemsQuery
            {
                ItemIds = chunk,

                // As GetItemById would: any item with that id, including alternate versions.
                IncludeOwnedItems = true,

                // Watched data, and provider ids (Jellyfin works out which watched data is an item's from them);
                // no images or other extras.
                DtoOptions = new DtoOptions(false)
                {
                    EnableImages = false,
                    EnableUserData = true,
                    Fields = new[] { ItemFields.ProviderIds }
                }
            });
            foreach (var item in items)
            {
                result[item.Id] = item;
            }
        }

        SlowLog.Took(logger, timer, "Loading {Count} items and their watched data", result.Count);
        return result;
    }

    /// <summary>The items behind an order's steps.</summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="steps">The steps.</param>
    /// <param name="logger">Where to say how long it took, or null.</param>
    /// <returns>The items by id.</returns>
    public static Dictionary<Guid, BaseItem> ForSteps(ILibraryManager libraryManager, IEnumerable<Models.ResolvedStep> steps, ILogger? logger = null)
        => Load(libraryManager, steps.Where(s => s.ItemId is not null).Select(s => s.ItemId!.Value), logger);
}
