using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fernplay.Core.Podcasts;

/// <summary>
/// Reads feeds over the internet. Also turns what people paste into a feed address: feed:// and itpc:// links, an
/// address without http, or a podcasts.apple.com page (looked up through Apple's public search API).
/// </summary>
public sealed partial class PodcastClient(HttpMessageHandler? handler = null, string version = "0")
{
    /// <summary>Feeds of long-running shows reach a few MB; anything far larger is not a feed.</summary>
    public const long MaxFeedBytes = 30 * 1024 * 1024;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http = CreateHttp(handler);

    private static HttpClient CreateHttp(HttpMessageHandler? handler)
    {
        var http = handler is null
            ? new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, AllowAutoRedirect = true, MaxAutomaticRedirections = 10 })
            : new HttpClient(handler, disposeHandler: false);
        http.Timeout = Timeout;
        return http;
    }

    /// <summary>
    /// The feed address in what was typed or pasted, or null when it cannot be one. An Apple Podcasts page gives null
    /// here; <see cref="ResolveAsync"/> looks it up.
    /// </summary>
    public static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string s = text.Trim().Trim('"', '\'', '<', '>');
        // feed:https://x, feed://x, itpc://x, pcast://x, podcast://x: podcast-app link forms of an ordinary address.
        foreach (var scheme in new[] { "feed:", "itpc:", "pcast:", "podcast:" })
        {
            if (!s.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) continue;
            s = s[scheme.Length..];
            if (s.StartsWith("//", StringComparison.Ordinal)) s = "https:" + s;
            break;
        }
        if (!s.Contains("://", StringComparison.Ordinal)) s = "https://" + s;
        return Uri.TryCreate(s, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" && u.Host.Contains('.') ? u.ToString() : null;
    }

    /// <summary>
    /// The Apple Podcasts show id in a podcasts.apple.com (or itunes.apple.com) page address, or in a pod.link address
    /// (pod.link/1186056137, which uses the same id); null for anything else.
    /// </summary>
    public static string? AppleId(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
        var m = u.Host.EndsWith("apple.com", StringComparison.OrdinalIgnoreCase) ? AppleIdPattern().Match(u.AbsolutePath)
            : u.Host.Equals("pod.link", StringComparison.OrdinalIgnoreCase) || u.Host.Equals("www.pod.link", StringComparison.OrdinalIgnoreCase) ? PodLinkPattern().Match(u.AbsolutePath)
            : null;
        return m is { Success: true } ? m.Groups[1].Value : null;
    }

    [GeneratedRegex(@"/id(\d{3,})")]
    private static partial Regex AppleIdPattern();

    [GeneratedRegex(@"^/(\d{3,})(?:/|$)")]
    private static partial Regex PodLinkPattern();

    /// <summary>The feed address for what was typed: as it is, or looked up for an Apple Podcasts page. Throws FeedException.</summary>
    public async Task<string> ResolveAsync(string text, CancellationToken token = default)
    {
        string url = Normalize(text) ?? throw new FeedException("That is not a web address. Paste the podcast's RSS feed address (it often ends in .xml or /rss).");
        if (AppleId(url) is not { } id) return url;
        using var response = await SendAsync($"https://itunes.apple.com/lookup?id={id}&entity=podcast", token);
        await using var body = await response.Content.ReadAsStreamAsync(token);
        using var json = await JsonDocument.ParseAsync(body, cancellationToken: token);
        foreach (var r in json.RootElement.GetProperty("results").EnumerateArray())
            if (r.TryGetProperty("feedUrl", out var f) && f.GetString() is { Length: > 0 } feed) return feed;
        throw new FeedException("Apple Podcasts has no public feed for that show (some are Apple-only). Try the show's own website for its RSS feed.");
    }

    /// <summary>Reads and parses a feed. Throws FeedException with a message to show.</summary>
    public async Task<FeedShow> FetchAsync(string feedUrl, CancellationToken token = default)
    {
        using var response = await SendAsync(feedUrl, token);
        if (response.Content.Headers.ContentLength > MaxFeedBytes) throw new FeedException("That address sends a file far too large to be a feed.");
        if (response.Content.Headers.ContentType?.MediaType is { } type && type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            throw new FeedException("That is the address of an audio file, not of a podcast feed.");
        await using var body = await response.Content.ReadAsStreamAsync(token);
        using var limited = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = await body.ReadAsync(buffer, token)) > 0)
        {
            limited.Write(buffer, 0, n);
            if (limited.Length > MaxFeedBytes) throw new FeedException("That address sends a file far too large to be a feed.");
        }
        limited.Position = 0;
        var show = PodcastFeed.Parse(limited);
        if (show.Episodes.Count == 0) throw new FeedException($"{show.Title}: the feed lists no audio episodes.");
        return show;
    }

    private async Task<HttpResponseMessage> SendAsync(string url, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Fernplay", version));
        request.Headers.Accept.ParseAdd("application/rss+xml, application/atom+xml, application/xml;q=0.9, text/xml;q=0.9, */*;q=0.5");
        HttpResponseMessage response;
        try { response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token); }
        catch (TaskCanceledException ex) when (!token.IsCancellationRequested) { throw new FeedException("No answer from the podcast's server (timed out).", ex); }
        catch (HttpRequestException ex) { throw new FeedException($"Could not reach {HostOf(url)}: {ex.Message}", ex); }
        if (response.IsSuccessStatusCode) return response;
        int code = (int)response.StatusCode;
        response.Dispose();
        throw new FeedException(code switch
        {
            404 or 410 => $"{HostOf(url)} says the feed is not there (error {code}). The podcast may have moved or ended.",
            401 or 403 => $"{HostOf(url)} refused the feed (error {code}). Private feeds need their own address with the key in it.",
            _ => $"{HostOf(url)} answered with error {code}.",
        });
    }

    private static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;
}
