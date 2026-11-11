using System.Net;
using System.Text;
using Fernplay.Core.Podcasts;

namespace Fernplay.Tests;

public sealed class PodcastTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("fernplay-pod-");

    public void Dispose() => _dir.Delete(recursive: true);

    private const string Rss = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE rss [ <!ENTITY big "xxxxxxxxxx"> ]>
        <rss version="2.0" xmlns:itunes="http://www.itunes.com/dtds/podcast-1.0.dtd">
          <channel>
            <title>  Planet   Test </title>
            <link>https://example.org/show</link>
            <itunes:author>Test Radio</itunes:author>
            <itunes:image href="https://example.org/art.jpg"/>
            <item>
              <title>Older one</title>
              <guid isPermaLink="false">ep-1</guid>
              <pubDate>Wed, 4 Nov 2026 08:00:00 GMT</pubDate>
              <itunes:duration>21:06</itunes:duration>
              <enclosure url="https://cdn.example.org/1.mp3?t=a" length="20264002" type="audio/mpeg"/>
            </item>
            <item>
              <title>Newest one</title>
              <itunes:title>Newest one (itunes)</itunes:title>
              <guid>ep-2</guid>
              <pubDate>Fri, 06 Nov 2026 22:30:00 +0000</pubDate>
              <itunes:duration>1943</itunes:duration>
              <itunes:episode>42</itunes:episode>
              <enclosure url="https://cdn.example.org/2.mp3" length="31091653" type="audio/mpeg"/>
            </item>
            <item>
              <title>A video</title>
              <enclosure url="https://cdn.example.org/v.mp4" type="video/mp4"/>
            </item>
            <item>
              <title>No guid, no type</title>
              <pubDate>6 Nov 2026 09:00 PST</pubDate>
              <enclosure url="https://cdn.example.org/3.m4a"/>
            </item>
          </channel>
        </rss>
        """;

    [Fact]
    public void Rss_feed_gives_the_show_and_its_audio_episodes()
    {
        var show = PodcastFeed.Parse(Rss);
        Assert.Equal("Planet Test", show.Title);
        Assert.Equal("Test Radio", show.Author);
        Assert.Equal("https://example.org/art.jpg", show.ImageUrl);
        Assert.Equal(3, show.Episodes.Count); // the video is left out
        var newest = show.Episodes[1];
        Assert.Equal("ep-2", newest.Id);
        Assert.Equal("Newest one (itunes)", newest.Title);
        Assert.Equal(new DateTime(2026, 11, 6, 22, 30, 0, DateTimeKind.Utc), newest.Published);
        Assert.Equal(TimeSpan.FromSeconds(1943), newest.Duration);
        Assert.Equal(42, newest.Number);
        Assert.Equal(31091653, newest.Size);
        Assert.Equal(new DateTime(2026, 11, 4, 8, 0, 0, DateTimeKind.Utc), show.Episodes[0].Published);
        Assert.Equal(TimeSpan.FromMinutes(21) + TimeSpan.FromSeconds(6), show.Episodes[0].Duration);
        var third = show.Episodes[2];
        Assert.Equal("https://cdn.example.org/3.m4a", third.Id); // no guid: the address
        Assert.Equal(new DateTime(2026, 11, 6, 17, 0, 0, DateTimeKind.Utc), third.Published); // 09:00 PST
    }

    [Fact]
    public void Atom_feed_with_enclosure_links_is_read()
    {
        const string atom = """
            <feed xmlns="http://www.w3.org/2005/Atom">
              <title>Atom Cast</title><author><name>Someone</name></author>
              <entry><id>urn:1</id><title>First</title><published>2026-10-01T10:00:00Z</published>
                <link rel="enclosure" type="audio/mpeg" href="https://x.example/1.mp3" length="100"/></entry>
              <entry><id>urn:2</id><title>No audio</title><link rel="alternate" href="https://x.example/2"/></entry>
            </feed>
            """;
        var show = PodcastFeed.Parse(atom);
        Assert.Equal("Atom Cast", show.Title);
        var e = Assert.Single(show.Episodes);
        Assert.Equal("urn:1", e.Id);
        Assert.Equal(new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc), e.Published);
    }

    [Theory]
    [InlineData("<html><body>Not a feed</body></html>")]
    [InlineData("this is not xml")]
    public void A_web_page_is_not_a_feed(string text) => Assert.Throws<FeedException>(() => PodcastFeed.Parse(text));

    [Theory]
    [InlineData("1943", 1943)]
    [InlineData("32:23", 1943)]
    [InlineData("1:02:03", 3723)]
    [InlineData("12:30.6", 751)]
    [InlineData("", null)]
    [InlineData("abc", null)]
    [InlineData("0", null)]
    public void Durations_in_the_forms_feeds_use(string text, int? seconds) =>
        Assert.Equal(seconds is { } s ? TimeSpan.FromSeconds(s) : null, PodcastFeed.ParseDuration(text));

    [Theory]
    [InlineData("Mon, 06 Nov 2026 22:30:00 +0000", "2026-11-06T22:30:00")] // wrong weekday is ignored
    [InlineData("Fri, 6 Nov 2026 22:30:00 -0500", "2026-11-07T03:30:00")]
    [InlineData("Fri, 06 Nov 2026 22:30 EDT", "2026-11-07T02:30:00")]
    [InlineData("06 Nov 2026", "2026-11-06T00:00:00")]
    [InlineData("2026-11-06T22:30:00+01:00", "2026-11-06T21:30:00")]
    public void Feed_dates_become_utc(string text, string utc) =>
        Assert.Equal(DateTime.Parse(utc, System.Globalization.CultureInfo.InvariantCulture), PodcastFeed.ParseDate(text));

    [Fact]
    public void Unreadable_date_is_null() => Assert.Null(PodcastFeed.ParseDate("sometime last week"));

    private static readonly DateTime Now = new(2026, 11, 11, 3, 0, 0, DateTimeKind.Utc);

    private static FeedShow Feed(params FeedEpisode[] episodes) => new("Show", "Author", null, null, episodes);

    private static FeedEpisode Ep(string id, string url, int day, int minutes = 30) =>
        new(id, "Episode " + id, url, new DateTime(2026, 11, day, 0, 0, 0, DateTimeKind.Utc), TimeSpan.FromMinutes(minutes), 1000, null);

    [Fact]
    public void Episodes_are_newest_first_and_known_by_address()
    {
        var store = new PodcastStore();
        var show = store.Subscribe("https://f.example/rss", Feed(Ep("a", "https://a.mp3", 1), Ep("b", "https://b.mp3", 5)), Now);
        Assert.Equal(["b", "a"], show.Episodes.Select(e => e.Id));
        Assert.True(store.IsEpisode("https://a.mp3"));
        Assert.False(store.IsEpisode("https://radio.example/stream"));
        Assert.False(store.IsEpisode(@"C:\Music\a.mp3"));
    }

    [Fact]
    public void Refresh_keeps_listening_state_and_follows_a_new_address()
    {
        var store = new PodcastStore();
        var show = store.Subscribe("https://f.example/rss", Feed(Ep("a", "https://a.mp3?t=1", 1), Ep("b", "https://b.mp3", 2)), Now);
        store.SetPosition("https://a.mp3?t=1", TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(31), Now);
        store.SetPlayed(["https://b.mp3"], true);

        // The tracking link of "a" changed; "b" left the feed; "c" is new.
        store.Apply(show, Feed(Ep("a", "https://a.mp3?t=2", 1), Ep("c", "https://c.mp3", 3)), Now.AddHours(1));
        Assert.Equal(["c", "b", "a"], show.Episodes.Select(e => e.Id)); // b stays: it was played
        var a = show.Episodes.Single(e => e.Id == "a");
        Assert.Equal(TimeSpan.FromMinutes(10), a.Position);
        Assert.Equal(TimeSpan.FromMinutes(31), a.Duration); // measured beats the feed's 30
        Assert.True(store.IsEpisode("https://a.mp3?t=1")); // a saved playlist still finds it
        Assert.Same(a, store.Find("https://a.mp3?t=1")!.Value.Episode);
        Assert.True(show.Episodes.Single(e => e.Id == "b").Played);
    }

    [Fact]
    public void Only_episodes_out_since_subscribing_count_as_new()
    {
        var store = new PodcastStore();
        var show = store.Subscribe("https://f.example/rss", Feed(Ep("a", "https://a.mp3", 1), Ep("b", "https://b.mp3", 5)), Now);
        Assert.Equal(0, show.NewCount); // the back catalogue
        var later = new FeedEpisode("c", "C", "https://c.mp3", Now.AddDays(1), null, null, null);
        store.Apply(show, Feed(Ep("a", "https://a.mp3", 1), Ep("b", "https://b.mp3", 5), later), Now.AddDays(1));
        Assert.Equal(1, show.NewCount);
        store.SetPosition("https://c.mp3", TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(30), Now.AddDays(1));
        Assert.Equal(0, show.NewCount); // started
    }

    [Fact]
    public void Episodes_never_listened_to_leave_with_the_feed()
    {
        var store = new PodcastStore();
        var show = store.Subscribe("https://f.example/rss", Feed(Ep("a", "https://a.mp3", 1), Ep("b", "https://b.mp3", 2)), Now);
        store.Apply(show, Feed(Ep("b", "https://b.mp3", 2)), Now);
        Assert.Equal(["b"], show.Episodes.Select(e => e.Id));
        Assert.False(store.IsEpisode("https://a.mp3"));
    }

    [Fact]
    public void Near_the_end_an_episode_counts_as_played_and_starts_over()
    {
        var store = new PodcastStore();
        store.Subscribe("https://f.example/rss", Feed(Ep("a", "https://a.mp3", 1, minutes: 40)), Now);
        int changed = 0;
        store.Changed += (_, _) => changed++;

        store.SetPosition("https://a.mp3", TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(40), Now);
        Assert.Equal(1, changed); // started: the list shows it
        Assert.Equal(TimeSpan.FromMinutes(20) - TimeSpan.FromSeconds(5), store.ResumeAt("https://a.mp3"));
        store.SetPosition("https://a.mp3", TimeSpan.FromMinutes(21), TimeSpan.FromMinutes(40), Now);
        Assert.Equal(1, changed); // only the position moved

        store.SetPosition("https://a.mp3", TimeSpan.FromMinutes(39) + TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(40), Now);
        var e = store.Find("https://a.mp3")!.Value.Episode;
        Assert.True(e.Played);
        Assert.Equal(0, e.PositionMs);
        Assert.Null(store.ResumeAt("https://a.mp3"));
        Assert.Equal(2, changed);
    }

    [Theory]
    [InlineData(10, 40, false)]
    [InlineData(37, 40, false)]
    [InlineData(39.1, 40, true)]   // last minute
    [InlineData(95, 100, true)]    // last 5%
    [InlineData(1, 0, false)]      // length unknown
    public void Played_rule(double minutes, double length, bool done) =>
        Assert.Equal(done, PodcastStore.IsDone(TimeSpan.FromMinutes(minutes), TimeSpan.FromMinutes(length)));

    [Fact]
    public void A_few_seconds_in_is_not_worth_resuming()
    {
        var store = new PodcastStore();
        store.Subscribe("https://f.example/rss", Feed(Ep("a", "https://a.mp3", 1)), Now);
        store.SetPosition("https://a.mp3", TimeSpan.FromSeconds(12), TimeSpan.Zero, Now);
        Assert.Null(store.ResumeAt("https://a.mp3"));
    }

    [Fact]
    public void Saved_and_read_back_and_a_broken_file_is_kept_aside()
    {
        var path = Path.Combine(_dir.FullName, "podcasts.json");
        var store = new PodcastStore();
        store.Subscribe("https://f.example/rss", Feed(Ep("a", "https://a.mp3", 1)), Now);
        store.SetPosition("https://a.mp3", TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(30), Now);
        store.Save(path);

        var back = PodcastStore.Load(path);
        Assert.Equal(TimeSpan.FromMinutes(3), back.Find("https://a.mp3")!.Value.Episode.Position);

        File.WriteAllText(path, "{ not json");
        Assert.Empty(PodcastStore.Load(path).Shows);
        Assert.True(File.Exists(path + ".bad"));
    }

    [Fact]
    public void Unsubscribe_forgets_the_episodes()
    {
        var store = new PodcastStore();
        var show = store.Subscribe("https://f.example/rss", Feed(Ep("a", "https://a.mp3", 1)), Now);
        Assert.True(store.Unsubscribe(show));
        Assert.False(store.IsEpisode("https://a.mp3"));
        store.Apply(show, Feed(Ep("a", "https://a.mp3", 1)), Now); // a refresh that finishes afterwards adds nothing back
        Assert.Empty(store.Shows);
    }

    [Theory]
    [InlineData("https://feeds.example.org/show.xml", "https://feeds.example.org/show.xml")]
    [InlineData("  feeds.example.org/show.xml ", "https://feeds.example.org/show.xml")]
    [InlineData("feed://feeds.example.org/rss", "https://feeds.example.org/rss")]
    [InlineData("feed:https://feeds.example.org/rss", "https://feeds.example.org/rss")]
    [InlineData("itpc://feeds.example.org/rss", "https://feeds.example.org/rss")]
    [InlineData("http://feeds.example.org/rss", "http://feeds.example.org/rss")]
    [InlineData("not a url", null)]
    [InlineData("ftp://x.example/rss", null)]
    [InlineData("", null)]
    public void What_people_paste_becomes_a_feed_address(string text, string? url) => Assert.Equal(url, PodcastClient.Normalize(text));

    [Theory]
    [InlineData("https://podcasts.apple.com/us/podcast/planet-money/id290783428", "290783428")]
    [InlineData("https://podcasts.apple.com/nz/podcast/x/id290783428?i=1000", "290783428")]
    [InlineData("https://pod.link/1186056137", "1186056137")]
    [InlineData("https://pod.link/1186056137/episode/abc123", "1186056137")]
    [InlineData("pod.link/1186056137", null)] // not yet normalised: ResolveAsync adds https first
    [InlineData("https://pod.link/about", null)]
    [InlineData("https://feeds.npr.org/510289/podcast.xml", null)]
    public void Apple_podcast_pages_are_recognised(string url, string? id) => Assert.Equal(id, PodcastClient.AppleId(url));

    private sealed class FakeServer(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<Uri> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Asked.Add(request.RequestUri!);
            return Task.FromResult(answer(request));
        }
    }

    private static HttpResponseMessage Text(string body, string type = "application/rss+xml") =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, type) };

    [Fact]
    public async Task Fetch_reads_the_feed_and_says_who_is_asking()
    {
        string? agent = null;
        var server = new FakeServer(r => { agent = r.Headers.UserAgent.ToString(); return Text(Rss); });
        var show = await new PodcastClient(server, "0.9.1").FetchAsync("https://f.example/rss");
        Assert.Equal(3, show.Episodes.Count);
        Assert.Equal("Fernplay/0.9.1", agent);
    }

    [Fact]
    public async Task Fetch_failures_say_what_happened()
    {
        var gone = new PodcastClient(new FakeServer(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        var e1 = await Assert.ThrowsAsync<FeedException>(() => gone.FetchAsync("https://f.example/rss"));
        Assert.Contains("f.example says the feed is not there (error 404)", e1.Message);

        var audio = new PodcastClient(new FakeServer(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new("audio/mpeg") } } }));
        var e2 = await Assert.ThrowsAsync<FeedException>(() => audio.FetchAsync("https://f.example/1.mp3"));
        Assert.Contains("audio file", e2.Message);

        var page = new PodcastClient(new FakeServer(_ => Text("<html><body>hi</body></html>", "text/html")));
        await Assert.ThrowsAsync<FeedException>(() => page.FetchAsync("https://f.example/"));

        var empty = new PodcastClient(new FakeServer(_ => Text("<rss><channel><title>Quiet</title></channel></rss>")));
        var e3 = await Assert.ThrowsAsync<FeedException>(() => empty.FetchAsync("https://f.example/rss"));
        Assert.Contains("no audio episodes", e3.Message);
    }

    [Fact]
    public async Task An_apple_podcasts_page_is_looked_up()
    {
        var server = new FakeServer(_ => Text("""{"resultCount":1,"results":[{"collectionName":"Planet Money","feedUrl":"https://feeds.npr.org/510289/podcast.xml"}]}""", "application/json"));
        var client = new PodcastClient(server);
        Assert.Equal("https://feeds.npr.org/510289/podcast.xml", await client.ResolveAsync("https://podcasts.apple.com/us/podcast/planet-money/id290783428"));
        Assert.Equal("https://itunes.apple.com/lookup?id=290783428&entity=podcast", server.Asked.Single().ToString());
        Assert.Equal("https://f.example/rss", await client.ResolveAsync("f.example/rss")); // not Apple: no lookup
        Assert.Single(server.Asked);
    }
}
