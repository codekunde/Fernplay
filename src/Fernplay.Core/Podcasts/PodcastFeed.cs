using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Fernplay.Core.Podcasts;

/// <summary>One episode as the feed describes it.</summary>
public sealed record FeedEpisode(string Id, string Title, string Url, DateTime? Published, TimeSpan? Duration, long? Size, int? Number);

/// <summary>A podcast as its feed describes it: the show and its episodes that have audio.</summary>
public sealed record FeedShow(string Title, string? Author, string? ImageUrl, string? Link, IReadOnlyList<FeedEpisode> Episodes);

/// <summary>The address does not hold a podcast feed (a web page, an error page, a feed without audio).</summary>
public sealed class FeedException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Reads a podcast feed: RSS 2.0 with Apple's podcast tags (nearly every podcast), or Atom with enclosure links.
/// Only episodes with an audio file are kept. DTDs are ignored, so a feed cannot pull in other files or expand entities.
/// </summary>
public static partial class PodcastFeed
{
    private static readonly XNamespace Itunes = "http://www.itunes.com/dtds/podcast-1.0.dtd";
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";

    public static FeedShow Parse(string xml)
    {
        using var reader = new StringReader(xml);
        return Parse(reader);
    }

    public static FeedShow Parse(Stream stream)
    {
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        return Parse(reader);
    }

    private static FeedShow Parse(TextReader text)
    {
        XDocument doc;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, IgnoreComments = true };
            using var xml = XmlReader.Create(text, settings);
            doc = XDocument.Load(xml);
        }
        catch (XmlException ex) { throw new FeedException("This address does not hold a podcast feed (it is not RSS).", ex); }

        var root = doc.Root ?? throw new FeedException("The feed is empty.");
        var show = root.Name.LocalName switch
        {
            "rss" => ParseRss(root.Element("channel") ?? throw new FeedException("The feed has no channel.")),
            "feed" when root.Name.Namespace == Atom => ParseAtom(root),
            _ => throw new FeedException("This address does not hold a podcast feed (it is not RSS)."),
        };
        return show;
    }

    private static FeedShow ParseRss(XElement channel)
    {
        string title = Text(channel.Element("title")) ?? "Untitled podcast";
        string? author = Text(channel.Element(Itunes + "author")) ?? Text(channel.Element(Itunes + "owner")?.Element(Itunes + "name"))
            ?? Text(channel.Element("managingEditor"));
        string? image = Attr(channel.Element(Itunes + "image"), "href") ?? Text(channel.Element("image")?.Element("url"));
        string? link = Text(channel.Element("link"));
        var episodes = new List<FeedEpisode>();
        foreach (var item in channel.Elements("item"))
        {
            var enclosure = item.Elements("enclosure").FirstOrDefault(e => IsAudio(Attr(e, "type"), Attr(e, "url")));
            string? url = Attr(enclosure, "url");
            if (url is null || !Track.IsStreamPath(url)) continue;
            string id = Text(item.Element("guid")) ?? url;
            string epTitle = Text(item.Element(Itunes + "title")) ?? Text(item.Element("title")) ?? "Untitled episode";
            episodes.Add(new FeedEpisode(id, epTitle, url, ParseDate(Text(item.Element("pubDate"))),
                ParseDuration(Text(item.Element(Itunes + "duration"))), PositiveLong(Attr(enclosure, "length")),
                PositiveInt(Text(item.Element(Itunes + "episode")))));
        }
        return new FeedShow(title, author, image, link, episodes);
    }

    private static FeedShow ParseAtom(XElement feed)
    {
        string title = Text(feed.Element(Atom + "title")) ?? "Untitled podcast";
        string? author = Text(feed.Element(Atom + "author")?.Element(Atom + "name"));
        string? image = Text(feed.Element(Atom + "logo")) ?? Text(feed.Element(Atom + "icon"));
        string? link = feed.Elements(Atom + "link").Where(l => Attr(l, "rel") is null or "alternate").Select(l => Attr(l, "href")).FirstOrDefault();
        var episodes = new List<FeedEpisode>();
        foreach (var entry in feed.Elements(Atom + "entry"))
        {
            var enclosure = entry.Elements(Atom + "link").FirstOrDefault(l => Attr(l, "rel") == "enclosure" && IsAudio(Attr(l, "type"), Attr(l, "href")));
            string? url = Attr(enclosure, "href");
            if (url is null || !Track.IsStreamPath(url)) continue;
            episodes.Add(new FeedEpisode(Text(entry.Element(Atom + "id")) ?? url, Text(entry.Element(Atom + "title")) ?? "Untitled episode", url,
                ParseDate(Text(entry.Element(Atom + "published")) ?? Text(entry.Element(Atom + "updated"))), null,
                PositiveLong(Attr(enclosure, "length")), null));
        }
        return new FeedShow(title, author, image, link, episodes);
    }

    /// <summary>An audio enclosure: type audio/*, or no type and an audio file name.</summary>
    private static bool IsAudio(string? type, string? url)
    {
        if (url is null) return false;
        if (type is not null) return type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase);
        string path = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.AbsolutePath : url;
        return Path.GetExtension(path).ToLowerInvariant() is ".mp3" or ".m4a" or ".aac" or ".ogg" or ".opus" or ".wav" or ".flac";
    }

    private static string? Text(XElement? e) => e is null || string.IsNullOrWhiteSpace(e.Value) ? null : WhiteSpace().Replace(e.Value.Trim(), " ");
    private static string? Attr(XElement? e, string name) => e?.Attribute(name)?.Value is { } v && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

    private static long? PositiveLong(string? s) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;
    private static int? PositiveInt(string? s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;

    /// <summary>"1943", "32:23", "1:02:03" or "32:23.5"; null when missing or nonsense.</summary>
    public static TimeSpan? ParseDuration(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var parts = s.Trim().Split(':');
        if (parts.Length > 3) return null;
        double seconds = 0;
        foreach (var p in parts)
        {
            if (!double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || n < 0) return null;
            seconds = seconds * 60 + n;
        }
        return seconds >= 1 && seconds < 7 * 86400 ? TimeSpan.FromSeconds(Math.Round(seconds)) : null;
    }

    private static readonly (string Name, string Offset)[] Zones =
        [("UT", "+0000"), ("GMT", "+0000"), ("Z", "+0000"), ("EST", "-0500"), ("EDT", "-0400"), ("CST", "-0600"), ("CDT", "-0500"),
         ("MST", "-0700"), ("MDT", "-0600"), ("PST", "-0800"), ("PDT", "-0700"), ("BST", "+0100"), ("CET", "+0100"), ("CEST", "+0200")];

    private static readonly string[] DateFormats =
        ["d MMM yyyy HH:mm:ss zzz", "d MMM yyyy HH:mm zzz", "d MMM yy HH:mm:ss zzz", "d MMM yyyy HH:mm:ss", "d MMM yyyy", "d MMMM yyyy HH:mm:ss zzz"];

    /// <summary>
    /// A feed date in UTC: RFC 822 as RSS has it ("Fri, 06 Nov 2026 22:30:00 +0000", "6 Nov 2026 09:00 GMT"), or ISO 8601
    /// (Atom). Feeds get this wrong in many ways; what cannot be read gives null.
    /// </summary>
    public static DateTime? ParseDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var iso) && s.Length >= 10 && char.IsDigit(s[0]) && s[4] == '-')
            return iso.UtcDateTime;
        int comma = s.IndexOf(',');
        if (comma is > 0 and < 10) s = s[(comma + 1)..].Trim(); // the weekday is often wrong; drop it
        s = WhiteSpace().Replace(s, " ");
        foreach (var (name, offset) in Zones)
            if (s.EndsWith(" " + name, StringComparison.OrdinalIgnoreCase)) { s = s[..^name.Length] + offset; break; }
        s = ZoneDigits().Replace(s, " $1$2:$3"); // +0000 -> +00:00 for zzz
        if (DateTimeOffset.TryParseExact(s, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var d))
            return d.UtcDateTime;
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out d) ? d.UtcDateTime : null;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();

    [GeneratedRegex(@" ([+-])(\d\d)(\d\d)$")]
    private static partial Regex ZoneDigits();
}
