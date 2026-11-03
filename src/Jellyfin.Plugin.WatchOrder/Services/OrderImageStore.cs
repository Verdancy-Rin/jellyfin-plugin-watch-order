using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Jellyfin.Plugin.WatchOrder.Models;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.WatchOrder.Services;

/// <summary>What the server holds of an order's own picture.</summary>
public sealed class StoredImage
{
    /// <summary>Gets or sets <c>Link</c> (downloaded from the order's <c>image.url</c>) or <c>Upload</c> (an admin's upload).</summary>
    public string Kind { get; set; } = "Link";

    /// <summary>Gets or sets the link it was downloaded from, for <c>Link</c>.</summary>
    public string? Url { get; set; }

    /// <summary>Gets or sets the file name in the images folder, once there's a copy.</summary>
    public string? File { get; set; }

    /// <summary>Gets or sets the picture's content type.</summary>
    public string? ContentType { get; set; }

    /// <summary>Gets or sets when the copy was made (UTC).</summary>
    public DateTime? SavedAt { get; set; }

    /// <summary>Gets or sets why the last download failed, for the admin; null when it worked.</summary>
    public string? Error { get; set; }
}

/// <summary>
/// Keeps the server's own copy of each order's picture, in <c>.../WatchOrder/images/</c>: downloaded once from the
/// order's <c>image.url</c> (so a link that dies later doesn't matter), or uploaded by an admin. Downloads are limited
/// to JPEG, PNG and WebP pictures of up to 10 MB from public addresses: an imported order comes from someone else,
/// and its link mustn't be able to make the server fetch things from inside the home network.
/// </summary>
public class OrderImageStore
{
    /// <summary>The largest picture accepted.</summary>
    public const int MaxBytes = 10 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _folder;
    private readonly ILogger<OrderImageStore> _logger;
    private readonly Func<IPAddress, bool> _allowAddress;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _fetching = new(1, 1);
    private ConcurrentDictionary<string, StoredImage>? _images;
    private HttpClient? _client;

    public OrderImageStore(IApplicationPaths applicationPaths, ILogger<OrderImageStore> logger)
        : this(Path.Combine(applicationPaths.PluginConfigurationsPath, "WatchOrder", "images"), logger, IsPublicAddress)
    {
    }

    internal OrderImageStore(string folder, ILogger<OrderImageStore> logger, Func<IPAddress, bool> allowAddress)
    {
        _folder = folder;
        _logger = logger;
        _allowAddress = allowAddress;
    }

    /// <summary>Gets what the server holds for an order, or null.</summary>
    /// <param name="orderId">The order id.</param>
    /// <returns>The stored image record, or null.</returns>
    public StoredImage? Get(string orderId) => Load().TryGetValue(orderId, out var image) ? image : null;

    /// <summary>Opens an order's picture, if the server has one.</summary>
    /// <param name="orderId">The order id.</param>
    /// <returns>The file's path and content type, or null.</returns>
    public (string Path, string ContentType)? Open(string orderId)
    {
        if (Get(orderId) is not { File: { } file, ContentType: { } type })
        {
            return null;
        }

        var path = Path.Combine(_folder, file);
        return System.IO.File.Exists(path) ? (path, type) : null;
    }

    /// <summary>Gets a short version tag for an order's picture, so browsers fetch it again when it changes.</summary>
    /// <param name="orderId">The order id.</param>
    /// <returns>The tag, or null when there's no picture.</returns>
    public string? VersionOf(string orderId)
        => Open(orderId) is null ? null : Get(orderId)?.SavedAt?.Ticks.ToString("x", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Brings an order's downloaded picture in line with its <c>image.url</c>: downloads it when the link is new or
    /// changed (or when <paramref name="force"/> is set), and removes the copy when the order no longer has a link.
    /// An admin's upload is left alone.
    /// </summary>
    /// <param name="definition">The order.</param>
    /// <param name="force">Download again even if the link hasn't changed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stored record afterwards, or null when there's nothing.</returns>
    public async Task<StoredImage?> SyncAsync(WatchOrderDefinition definition, bool force, CancellationToken cancellationToken)
    {
        var url = definition.Image?.Url?.Trim();
        var current = Get(definition.Id);
        if (current is { Kind: "Upload" })
        {
            return current;
        }

        if (string.IsNullOrEmpty(url))
        {
            if (current is not null)
            {
                Remove(definition.Id);
            }

            return null;
        }

        if (!force && current is not null && string.Equals(current.Url, url, StringComparison.Ordinal) && (current.File is not null || current.Error is not null))
        {
            return current; // already tried this link; "Refresh image" tries again
        }

        await _fetching.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (bytes, type, error) = await DownloadAsync(url, cancellationToken).ConfigureAwait(false);
            if (bytes is null)
            {
                _logger.LogWarning("Watch Order: couldn't fetch the image of {Order} from {Url}: {Error}", definition.Id, url, error);
                var failed = new StoredImage { Kind = "Link", Url = url, Error = error };

                // Keep a copy that already worked for this link, rather than losing it to a passing outage.
                if (current is { File: not null } && string.Equals(current.Url, url, StringComparison.Ordinal))
                {
                    failed.File = current.File;
                    failed.ContentType = current.ContentType;
                    failed.SavedAt = current.SavedAt;
                }

                Put(definition.Id, failed);
                return failed;
            }

            return Write(definition.Id, bytes, type!, "Link", url);
        }
        finally
        {
            _fetching.Release();
        }
    }

    /// <summary>Saves an admin's upload as an order's picture, in place of any downloaded one.</summary>
    /// <param name="orderId">The order id.</param>
    /// <param name="bytes">The file.</param>
    /// <returns>The stored record, or an error for the admin.</returns>
    public (StoredImage? Image, string? Error) SaveUpload(string orderId, byte[] bytes)
    {
        if (bytes.Length > MaxBytes)
        {
            return (null, "That picture is over 10 MB.");
        }

        if (ContentTypeOf(bytes) is not { } type)
        {
            return (null, "That isn't a JPEG, PNG or WebP picture.");
        }

        return (Write(orderId, bytes, type, "Upload", null), null);
    }

    /// <summary>Removes an order's picture: its upload, or its downloaded copy.</summary>
    /// <param name="orderId">The order id.</param>
    public void Remove(string orderId)
    {
        if (Load().TryRemove(orderId, out var image))
        {
            DeleteFile(image.File);
            Save();
        }
    }

    /// <summary>Works out a picture's content type from its first bytes: JPEG, PNG or WebP, else null.</summary>
    /// <param name="bytes">The file.</param>
    /// <returns>The content type, or null.</returns>
    public static string? ContentTypeOf(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            return "image/png";
        }

        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }

        return null;
    }

    /// <summary>
    /// Whether an address is on the public internet: not loopback, private, link-local, carrier-grade NAT,
    /// multicast or reserved.
    /// </summary>
    /// <param name="address">The address.</param>
    /// <returns><c>true</c> for a public address.</returns>
    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 0 || b[0] == 10 || b[0] == 127 || b[0] >= 224
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 198 && (b[1] == 18 || b[1] == 19)));
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();

            // NAT64 (64:ff9b::/96) carries an IPv4 address: judge that.
            if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B && b[4..12].All(x => x == 0))
            {
                return IsPublicAddress(new IPAddress(b[12..16]));
            }

            return !(IPAddress.IsLoopback(address) || address.Equals(IPAddress.IPv6None) || address.Equals(IPAddress.IPv6Any)
                || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast
                || (b[0] & 0xFE) == 0xFC); // unique local, fc00::/7
        }

        return false;
    }

    private async Task<(byte[]? Bytes, string? Type, string? Error)> DownloadAsync(string link, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var url) || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
        {
            return (null, null, "The image link isn't an http(s) link.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var response = await Client().GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (null, null, $"The image link gave an error ({(int)response.StatusCode} {response.ReasonPhrase}).");
            }

            if (response.Content.Headers.ContentLength > MaxBytes)
            {
                return (null, null, "The picture is over 10 MB.");
            }

            var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                using var buffer = new MemoryStream();
                var chunk = new byte[81920];
                int read;
                while ((read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > MaxBytes)
                    {
                        return (null, null, "The picture is over 10 MB.");
                    }

                    buffer.Write(chunk, 0, read);
                }

                var bytes = buffer.ToArray();
                return ContentTypeOf(bytes) is { } type
                    ? (bytes, type, null)
                    : (null, null, "The link isn't a JPEG, PNG or WebP picture.");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, null, "The image link took too long to respond.");
        }
        catch (HttpRequestException ex) when (FindBlocked(ex) is { } blocked)
        {
            return (null, null, blocked.Message);
        }
        catch (HttpRequestException ex)
        {
            return (null, null, $"Couldn't reach the image link ({ex.Message}).");
        }
    }

    private static BlockedAddressException? FindBlocked(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            if (ex is BlockedAddressException blocked)
            {
                return blocked;
            }
        }

        return null;
    }

    private HttpClient Client()
    {
        if (_client is not null)
        {
            return _client;
        }

        var allow = _allowAddress;
        var handler = new SocketsHttpHandler
        {
            // No proxy: the address check below must see where the server really connects.
            UseProxy = false,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3,
            ConnectTimeout = TimeSpan.FromSeconds(10),

            // Every connection, including after a redirect, goes to an address that's been checked.
            ConnectCallback = async (context, token) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token).ConfigureAwait(false);
                var target = addresses.FirstOrDefault(allow)
                    ?? throw new BlockedAddressException($"The image link points at a private or local address ({context.DnsEndPoint.Host}), which isn't allowed.");
                var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(target, context.DnsEndPoint.Port), token).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(40) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin-WatchOrder-Plugin");
        client.DefaultRequestHeaders.Accept.ParseAdd("image/jpeg, image/png, image/webp");
        return _client = client;
    }

    private StoredImage Write(string orderId, byte[] bytes, string type, string kind, string? url)
    {
        Directory.CreateDirectory(_folder);
        var extension = type switch { "image/png" => ".png", "image/webp" => ".webp", _ => ".jpg" };
        var savedAt = DateTime.UtcNow;
        var file = $"{orderId}-{savedAt.Ticks:x}{extension}";
        var temp = Path.Combine(_folder, file + ".tmp");
        System.IO.File.WriteAllBytes(temp, bytes);
        System.IO.File.Move(temp, Path.Combine(_folder, file), overwrite: true);

        var previous = Get(orderId);
        var image = new StoredImage { Kind = kind, Url = url, File = file, ContentType = type, SavedAt = savedAt };
        Put(orderId, image);
        if (previous?.File is { } old && old != file)
        {
            DeleteFile(old);
        }

        return image;
    }

    private void Put(string orderId, StoredImage image)
    {
        Load()[orderId] = image;
        Save();
    }

    private void DeleteFile(string? file)
    {
        if (file is null)
        {
            return;
        }

        try
        {
            System.IO.File.Delete(Path.Combine(_folder, file));
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Watch Order: couldn't delete old image {File}", file);
        }
    }

    private ConcurrentDictionary<string, StoredImage> Load()
    {
        if (_images is not null)
        {
            return _images;
        }

        lock (_lock)
        {
            if (_images is not null)
            {
                return _images;
            }

            Dictionary<string, StoredImage>? read = null;
            var path = Path.Combine(_folder, "images.json");
            try
            {
                if (System.IO.File.Exists(path))
                {
                    read = JsonSerializer.Deserialize<Dictionary<string, StoredImage>>(System.IO.File.ReadAllText(path), JsonOptions);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                _logger.LogWarning(ex, "Ignoring unreadable {File}", path);
            }

            return _images = new ConcurrentDictionary<string, StoredImage>(read ?? new Dictionary<string, StoredImage>(), StringComparer.Ordinal);
        }
    }

    private void Save()
    {
        lock (_lock)
        {
            Directory.CreateDirectory(_folder);
            var path = Path.Combine(_folder, "images.json");
            var temp = path + ".tmp";
            System.IO.File.WriteAllText(temp, JsonSerializer.Serialize(new SortedDictionary<string, StoredImage>(Load(), StringComparer.Ordinal), JsonOptions));
            System.IO.File.Move(temp, path, overwrite: true);
        }
    }

    /// <summary>A download refused because the link points at a private or local address.</summary>
    private sealed class BlockedAddressException : IOException
    {
        public BlockedAddressException(string message)
            : base(message)
        {
        }
    }
}
