using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Fernplay.Core;

/// <summary>The newest published release, as GitHub reports it, with its files.</summary>
public sealed record ReleaseInfo(Version Version, string Tag, string PageUrl, IReadOnlyList<ReleaseAsset>? Assets = null)
{
    /// <summary>The release's installer (Fernplay-x.y.z-win-x64.msi), or null when it has none.</summary>
    public ReleaseAsset? Msi => Assets?.FirstOrDefault(a =>
        a.Name.StartsWith("Fernplay-", StringComparison.OrdinalIgnoreCase) && a.Name.EndsWith("-win-x64.msi", StringComparison.OrdinalIgnoreCase));
}

/// <summary>One file of a release: name, download address, size in bytes and SHA-256 (lower-case hex; null when GitHub gave none).</summary>
public sealed record ReleaseAsset(string Name, string DownloadUrl, long Size, string? Sha256);

/// <summary>Answer of an update check: a newer release, up to date, or why it could not be checked.</summary>
public sealed record UpdateResult(ReleaseInfo? Latest, bool IsNewer, string? Error);

/// <summary>
/// Asks GitHub for Fernplay's latest release (only when the user presses the button; nothing runs by itself).
/// </summary>
public static class UpdateCheck
{
    public const string LatestApi = "https://api.github.com/repos/codekunde/Fernplay/releases/latest";
    public const string ReleasesPage = "https://github.com/codekunde/Fernplay/releases/latest";

    public static async Task<UpdateResult> CheckAsync(string currentVersion, HttpMessageHandler? handler = null, CancellationToken ct = default)
    {
        try
        {
            using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            http.Timeout = TimeSpan.FromSeconds(15);
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestApi);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Fernplay", currentVersion.Length > 0 ? currentVersion : "0"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return new(null, false, $"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            var latest = Parse(await response.Content.ReadAsStringAsync(ct));
            if (latest is null) return new(null, false, "GitHub's answer had no version in it.");
            return new(latest, IsNewer(latest.Version, currentVersion), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new(null, false, ex is TaskCanceledException ? "No answer from GitHub (timed out)." : "Could not reach GitHub. Check the internet connection.");
        }
    }

    /// <summary>Reads tag_name and html_url from GitHub's release JSON; null when there is no usable version.</summary>
    public static ReleaseInfo? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("tag_name", out var tagEl) || tagEl.GetString() is not { } tag) return null;
        if (ParseVersion(tag) is not { } version) return null;
        string page = root.TryGetProperty("html_url", out var url) && url.GetString() is { Length: > 0 } u ? u : ReleasesPage;
        var assets = new List<ReleaseAsset>();
        if (root.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var a in list.EnumerateArray())
            {
                if (a.ValueKind != JsonValueKind.Object) continue;
                string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                string? download = a.TryGetProperty("browser_download_url", out var d) ? d.GetString() : null;
                if (name is not { Length: > 0 } || download is not { Length: > 0 }) continue;
                long size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : 0;
                // GitHub gives "digest": "sha256:<hex>" for each file.
                string? digest = a.TryGetProperty("digest", out var g) ? g.GetString() : null;
                string? sha = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                    && digest.Length == 7 + 64 && digest[7..].All(Uri.IsHexDigit) ? digest[7..].ToLowerInvariant() : null;
                assets.Add(new ReleaseAsset(name, download, size, sha));
            }
        return new(version, tag, page, assets);
    }

    /// <summary>"v0.5.1", "0.5.1" or "Fernplay 0.5.1" → 0.5.1 (missing parts are 0); null when there is no number.</summary>
    public static Version? ParseVersion(string text)
    {
        var m = System.Text.RegularExpressions.Regex.Match(text, @"(\d+)(?:\.(\d+))?(?:\.(\d+))?");
        if (!m.Success) return null;
        int Part(int i) => m.Groups[i].Success ? int.Parse(m.Groups[i].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        return new Version(Part(1), Part(2), Part(3));
    }

    public static bool IsNewer(Version latest, string current) =>
        ParseVersion(current) is not { } now || latest > now;
}
