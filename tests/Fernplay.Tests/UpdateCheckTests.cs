using System.Net;
using Fernplay.Core;

namespace Fernplay.Tests;

public sealed class UpdateCheckTests
{
    /// <summary>Answers every request with a fixed status and body; records the request.</summary>
    private sealed class FakeHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Seen { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen = request;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("no network");
    }

    private const string Release051 = """{"tag_name":"v0.5.1","name":"Fernplay 0.5.1","html_url":"https://github.com/codekunde/Fernplay/releases/tag/v0.5.1"}""";

    [Theory]
    [InlineData("v0.5.1", 0, 5, 1)]
    [InlineData("0.6", 0, 6, 0)]
    [InlineData("Fernplay 1.0.2", 1, 0, 2)]
    public void ParseVersion_reads_tags_with_or_without_v(string text, int major, int minor, int build) =>
        Assert.Equal(new Version(major, minor, build), UpdateCheck.ParseVersion(text));

    [Fact]
    public void ParseVersion_without_a_number_is_null() => Assert.Null(UpdateCheck.ParseVersion("latest"));

    [Theory]
    [InlineData("0.5.0", true)]
    [InlineData("0.5.1", false)]
    [InlineData("0.6.0", false)]
    [InlineData("0.10.0", false)] // numeric, not text, comparison
    public void IsNewer_compares_numerically(string current, bool newer) =>
        Assert.Equal(newer, UpdateCheck.IsNewer(new Version(0, 5, 1), current));

    [Fact]
    public async Task Newer_release_is_reported_with_its_page()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, Release051);
        var r = await UpdateCheck.CheckAsync("0.5.0", handler);
        Assert.Null(r.Error);
        Assert.True(r.IsNewer);
        Assert.Equal(new Version(0, 5, 1), r.Latest!.Version);
        Assert.Equal("https://github.com/codekunde/Fernplay/releases/tag/v0.5.1", r.Latest.PageUrl);
        Assert.Equal(UpdateCheck.LatestApi, handler.Seen!.RequestUri!.ToString());
        Assert.Contains("Fernplay/0.5.0", handler.Seen.Headers.UserAgent.ToString()); // GitHub refuses requests without one
    }

    [Fact]
    public async Task Same_version_is_up_to_date()
    {
        var r = await UpdateCheck.CheckAsync("0.5.1", new FakeHandler(HttpStatusCode.OK, Release051));
        Assert.Null(r.Error);
        Assert.False(r.IsNewer);
    }

    [Fact]
    public async Task Http_error_and_no_network_give_a_message_not_an_exception()
    {
        var notFound = await UpdateCheck.CheckAsync("0.5.0", new FakeHandler(HttpStatusCode.NotFound, "{}"));
        Assert.Contains("404", notFound.Error);
        var offline = await UpdateCheck.CheckAsync("0.5.0", new FailingHandler());
        Assert.NotNull(offline.Error);
        Assert.False(offline.IsNewer);
    }

    [Fact]
    public async Task Answer_without_a_tag_or_not_json_gives_a_message()
    {
        var noTag = await UpdateCheck.CheckAsync("0.5.0", new FakeHandler(HttpStatusCode.OK, """{"message":"x"}"""));
        Assert.NotNull(noTag.Error);
        var html = await UpdateCheck.CheckAsync("0.5.0", new FakeHandler(HttpStatusCode.OK, "<html>rate limited</html>"));
        Assert.NotNull(html.Error);
    }
}
