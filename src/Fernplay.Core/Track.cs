using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Fernplay.Core;

/// <summary>One file in the playlist, with what Windows knows about it.</summary>
public sealed partial record Track(string Path, string? Title, string? Artist, string? Album, TimeSpan? Duration, int? BitrateKbps, int? TrackNumber = null)
{
    /// <summary>An internet radio stream (http or https address) rather than a file.</summary>
    public bool IsStream => IsStreamPath(Path);

    public static bool IsStreamPath(string path) =>
        path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>A station from a playlist: its name from the playlist, or the address when there is none.</summary>
    public static Track Stream(string url, string? name) => new(url, string.IsNullOrWhiteSpace(name) ? null : name.Trim(), null, null, null, null);

    /// <summary>
    /// "03 - Artist - Title"; "Artist - Title" without a track number. A title tagged "03 - Title" gives its
    /// number when the tag has none and is never numbered twice. The file name when there is no title.
    /// </summary>
    public string DisplayName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Title)) return IsStream ? StreamLabel(Path) : System.IO.Path.GetFileNameWithoutExtension(Path);
            var title = Title!.Trim();
            int? number = TrackNumber is > 0 ? TrackNumber : null;
            var lead = LeadingNumber().Match(title);
            if (lead.Success)
            {
                number ??= int.Parse(lead.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                title = lead.Groups[2].Value;
            }
            var name = string.IsNullOrWhiteSpace(Artist) ? title : $"{Artist!.Trim()} - {title}";
            return number is > 0 ? $"{number:00} - {name}" : name;
        }
    }

    /// <summary>"03 - Title", "03. Title", "3 – Title": a track number written into the title tag.</summary>
    [GeneratedRegex(@"^(\d{1,3})\s*[-.–]\s+(\S.*)$")]
    private static partial Regex LeadingNumber();

    /// <summary>"Artist - Title", the form other players expect in an M3U #EXTINF line.</summary>
    public string ArtistTitle =>
        !string.IsNullOrWhiteSpace(Title)
            ? (string.IsNullOrWhiteSpace(Artist) ? Title!.Trim() : $"{Artist!.Trim()} - {Title!.Trim()}")
            : IsStream ? StreamLabel(Path) : System.IO.Path.GetFileNameWithoutExtension(Path);

    /// <summary>"stream.antenne.de/2000er-hits" for a station without a name.</summary>
    private static string StreamLabel(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? (u.Host + u.AbsolutePath).TrimEnd('/') : url;

    /// <summary>Reads tags through the Windows property system (the same data Explorer shows). Never throws.</summary>
    public static Track FromFile(string path)
    {
        try { return TagReader.Read(path); }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new Track(path, null, null, null, null, null);
        }
    }
}
