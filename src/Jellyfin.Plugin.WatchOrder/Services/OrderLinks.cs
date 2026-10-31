using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using MediaBrowser.Common.Net;

namespace Jellyfin.Plugin.WatchOrder.Services;

/// <summary>
/// Fetches an order file from a link, for "Import from a link". The server fetches it (browsers can't read most
/// sites' files), with limits: http(s) only, 1 MB, 20 seconds.
/// </summary>
public partial class OrderLinks
{
    /// <summary>The largest order file accepted.</summary>
    public const int MaxBytes = 1024 * 1024;

    private readonly IHttpClientFactory _httpClientFactory;

    public OrderLinks(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Turns the link people copy from their browser into the link of the file itself: a GitHub file page into its
    /// raw file, and a Gist page into its raw file.
    /// </summary>
    /// <param name="link">The link as pasted.</param>
    /// <returns>The link to fetch, or null when it isn't an http(s) link.</returns>
    public static Uri? ToFileLink(string link)
    {
        if (!Uri.TryCreate(link?.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return null;
        }

        var text = uri.GetLeftPart(UriPartial.Path);
        if (GitHubFile().Match(text) is { Success: true } file)
        {
            return new Uri($"https://raw.githubusercontent.com/{file.Groups["owner"].Value}/{file.Groups["repo"].Value}/{file.Groups["path"].Value}");
        }

        if (Gist().Match(text) is { Success: true } gist)
        {
            return new Uri($"https://gist.githubusercontent.com/{gist.Groups["user"].Value}/{gist.Groups["id"].Value}/raw");
        }

        return uri;
    }

    /// <summary>Fetches the text of an order file.</summary>
    /// <param name="link">The link as pasted.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The link fetched and the text, or an error message for the admin.</returns>
    public async Task<(Uri? Url, string? Text, string? Error)> FetchAsync(string link, CancellationToken cancellationToken)
    {
        var url = ToFileLink(link);
        if (url is null)
        {
            return (null, null, "That isn't a web link. Paste a link starting with https://.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var client = _httpClientFactory.CreateClient(NamedClient.Default);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (url, null, $"The link gave an error ({(int)response.StatusCode} {response.ReasonPhrase}). Check it opens in a browser.");
            }

            if (response.Content.Headers.ContentLength > MaxBytes)
            {
                return (url, null, "That file is too big to be an order (over 1 MB).");
            }

            var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var buffer = new byte[MaxBytes + 1];
                var total = 0;
                int read;
                while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), timeout.Token).ConfigureAwait(false)) > 0)
                {
                    total += read;
                }

                if (total > MaxBytes)
                {
                    return (url, null, "That file is too big to be an order (over 1 MB).");
                }

                var text = Encoding.UTF8.GetString(buffer, 0, total).TrimStart('﻿');
                if (text.TrimStart().StartsWith('<'))
                {
                    return (url, null, "That link opens a web page, not an order file. Link to the .json file itself (on GitHub, use the Raw button).");
                }

                return (url, text, null);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (url, null, "The link took too long to respond.");
        }
        catch (HttpRequestException ex)
        {
            return (url, null, $"Couldn't reach that link ({ex.Message}).");
        }
    }

    [GeneratedRegex("^https://github\\.com/(?<owner>[^/]+)/(?<repo>[^/]+)/(?:blob|raw)/(?<path>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubFile();

    [GeneratedRegex("^https://gist\\.github\\.com/(?<user>[^/]+)/(?<id>[0-9a-f]+)/?$", RegexOptions.IgnoreCase)]
    private static partial Regex Gist();
}
