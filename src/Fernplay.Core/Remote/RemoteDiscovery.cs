using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Fernplay.Core.Remote;

/// <summary>
/// Lets the phone find the PC: answers the broadcast "FERNPLAY?" on a UDP port with the PC's name, the remote's TCP port
/// and the certificate fingerprint (the phone checks it against the one it pinned). Runs only while the remote is on.
/// </summary>
public sealed class RemoteDiscovery : IDisposable
{
    public const int DefaultPort = 47801;
    public const string Question = "FERNPLAY?";

    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<byte[]> _answer;

    public RemoteDiscovery(int port, string pcName, int tcpPort, string fingerprint)
    {
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
        Port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
        var answer = Answer(pcName, tcpPort, fingerprint);
        _answer = () => answer;
        _ = ListenAsync(_stop.Token);
    }

    public int Port { get; }

    /// <summary>The reply: {"fernplay":1,"name":…,"port":…,"fp":…}.</summary>
    internal static byte[] Answer(string pcName, int tcpPort, string fingerprint) =>
        Encoding.UTF8.GetBytes(new JsonObject { ["fernplay"] = 1, ["name"] = pcName, ["port"] = tcpPort, ["fp"] = fingerprint }.ToJsonString());

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult got;
            try { got = await _udp.ReceiveAsync(ct); }
            catch (Exception) when (ct.IsCancellationRequested) { return; }
            catch (SocketException) { continue; } // e.g. a port-unreachable from an earlier reply
            catch (ObjectDisposedException) { return; }
            if (!RemoteServer.IsLocalNetwork(got.RemoteEndPoint.Address)) continue;
            if (got.Buffer.Length > 64 || Encoding.ASCII.GetString(got.Buffer).Trim() != Question) continue;
            try { await _udp.SendAsync(_answer(), got.RemoteEndPoint, ct); }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException) { }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _udp.Dispose();
    }
}
