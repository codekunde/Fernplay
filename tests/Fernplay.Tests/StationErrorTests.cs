using System.Net;
using System.Net.Sockets;
using System.Text;
using Fernplay.Core;
using Fernplay.Core.Audio;

namespace Fernplay.Tests;

/// <summary>A station that cannot be reached says why, not just "offline" (real connections to localhost).</summary>
public sealed class StationErrorTests
{
    /// <summary>Answers one request with a fixed HTTP status line, then closes.</summary>
    private static (string Url, Task Served) OneAnswer(string statusLine)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var served = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var s = client.GetStream();
            var buf = new byte[4096];
            _ = await s.ReadAtLeastAsync(buf, 1, throwOnEndOfStream: false); // the request; content does not matter
            await s.WriteAsync(Encoding.ASCII.GetBytes($"{statusLine}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
            listener.Stop();
        });
        return ($"http://127.0.0.1:{port}/stream", served);
    }

    private static string Fail(string url)
    {
        var ex = Assert.ThrowsAny<Exception>(() => IcyMp3Decoder.TryOpen(url));
        return PlaybackErrors.Describe(ex, url, "Test FM");
    }

    [Fact]
    public async Task Not_found_says_the_address_changed()
    {
        var (url, served) = OneAnswer("HTTP/1.1 404 Not Found");
        var text = Fail(url);
        await served;
        Assert.Contains("not there (404)", text);
        Assert.DoesNotContain("offline", text);
    }

    [Fact]
    public async Task Forbidden_says_the_station_refused()
    {
        var (url, served) = OneAnswer("HTTP/1.1 403 Forbidden");
        var text = Fail(url);
        await served;
        Assert.Contains("refused Fernplay (403)", text);
    }

    [Fact]
    public void Refused_connection_points_at_a_firewall_when_a_browser_plays_it()
    {
        // A port nobody listens on: bind, note it, release.
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        var text = Fail($"http://127.0.0.1:{port}/");
        Assert.Contains("refused or cut off", text);
        Assert.Contains("Fernplay.exe", text);
    }

    [Theory]
    [InlineData(HttpRequestError.SecureConnectionError, "secure (https) connection")]
    [InlineData(HttpRequestError.NameResolutionError, "could not be found")]
    [InlineData(HttpRequestError.ProxyTunnelError, "proxy")]
    public void Http_errors_name_their_cause(HttpRequestError kind, string expected)
    {
        var ex = new HttpRequestException(kind, "x");
        Assert.Contains(expected, PlaybackErrors.Describe(ex, "https://radio.example/live", "Test FM"));
    }

    [Fact]
    public void Blocked_socket_and_timeout_are_told_apart()
    {
        var blocked = new HttpRequestException(HttpRequestError.ConnectionError, "x", new SocketException((int)SocketError.AccessDenied));
        Assert.Contains("blocked Fernplay", PlaybackErrors.Describe(blocked, "https://radio.example/live"));
        Assert.Contains("within 15 seconds", PlaybackErrors.Describe(new TimeoutException("No answer"), "https://radio.example/live"));
    }

    [Fact]
    public void Unknown_errors_show_what_windows_said()
    {
        var text = PlaybackErrors.Describe(new InvalidOperationException("outer", new IOException("The pipe broke.")), "https://radio.example/live", "Test FM");
        Assert.Equal($"Can't connect to Test FM: The pipe broke (0x{new IOException().HResult:X8}).", text);
    }

    [Fact]
    public void Detail_lists_the_chain_with_error_kinds()
    {
        var ex = new HttpRequestException(HttpRequestError.ConnectionError, "outer", new SocketException((int)SocketError.ConnectionRefused));
        var d = PlaybackErrors.Detail(ex);
        Assert.Contains("[ConnectionError]", d);
        Assert.Contains("[ConnectionRefused]", d);
    }
}
