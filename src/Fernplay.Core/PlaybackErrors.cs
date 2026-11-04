using System.Runtime.InteropServices;

namespace Fernplay.Core;

/// <summary>Turns a load or playback failure into one sentence the user can act on.</summary>
public static class PlaybackErrors
{
    private const int UnsupportedByteStream = unchecked((int)0xC00D36C4); // MF_E_UNSUPPORTED_BYTESTREAM_TYPE
    private const int ClassNotRegistered = unchecked((int)0x80040154);    // REGDB_E_CLASSNOTREG

    /// <param name="stationName">For a stream: the name to show (the station), instead of the address.</param>
    public static string Describe(Exception ex, string path, string? stationName = null)
    {
        if (Track.IsStreamPath(path))
        {
            var station = stationName ?? path;
            return ex switch
            {
                DllNotFoundException or TypeInitializationException => MissingMediaFeatures,
                COMException { HResult: ClassNotRegistered } => MissingMediaFeatures,
                _ when ex.HResult == UnsupportedByteStream
                    => $"Can't play {station}: the station sends a format Windows can't decode.",
                _ => DescribeConnection(ex, station, Host(path)),
            };
        }
        var name = Path.GetFileName(path);
        return ex switch
        {
            FileNotFoundException or DirectoryNotFoundException => $"Can't find {name}: it was moved or deleted.",
            UnauthorizedAccessException => $"Can't open {name}: Windows denied access to the file.",
            DllNotFoundException or TypeInitializationException => MissingMediaFeatures,
            COMException { HResult: ClassNotRegistered } => MissingMediaFeatures,
            _ when ex.HResult == UnsupportedByteStream || ex.Message.Contains("byte stream type is not supported", StringComparison.OrdinalIgnoreCase)
                => $"Can't play {name}: Windows has no decoder for {Path.GetExtension(path).TrimStart('.').ToUpperInvariant()} files.",
            // Any other Media Foundation error (facility 0xC00D): the decoder could not make sense of the data.
            _ when IsMediaFoundationError(ex) => $"Can't play {name}: the file is damaged or is not a music file.",
            _ => $"Can't play {name}: {ex.Message.TrimEnd('.')}.",
        };
    }

    /// <summary>
    /// Why a station could not be reached, as precisely as the error allows. A browser can play a station that Fernplay
    /// cannot: it has its own connection, proxy and certificate handling, and security programs often trust browsers
    /// but not a new program, so "offline" alone sent people looking in the wrong place.
    /// </summary>
    private static string DescribeConnection(Exception ex, string station, string host)
    {
        var http = Find<System.Net.Http.HttpRequestException>(ex);
        var socket = Find<System.Net.Sockets.SocketException>(ex);
        if (Find<TimeoutException>(ex) is not null || socket?.SocketErrorCode == System.Net.Sockets.SocketError.TimedOut)
            return $"Can't connect to {station}: no answer from {host} within 15 seconds. The station may be down, or a firewall or security program is holding Fernplay's connection.";
        if (http?.StatusCode is { } code)
        {
            int n = (int)code;
            return n switch
            {
                404 or 410 => $"Can't play {station}: {host} says the stream is not there ({n}). Its address has probably changed; get the current link from the station's website.",
                401 or 403 => $"Can't play {station}: {host} refused Fernplay ({n}). Some stations only play in their own player or in certain countries.",
                >= 300 and < 400 => $"Can't play {station}: {host} sent Fernplay to another address it could not follow ({n}).",
                _ => $"Can't play {station}: {host} answered with an error ({n}). Try again later.",
            };
        }
        if (Find<System.Security.Authentication.AuthenticationException>(ex) is not null || http?.HttpRequestError == System.Net.Http.HttpRequestError.SecureConnectionError)
            return $"Can't connect to {station}: the secure (https) connection to {host} failed. Usually a security program that checks https traffic, or a wrong date and time on this PC.";
        if (http?.HttpRequestError == System.Net.Http.HttpRequestError.NameResolutionError || socket?.SocketErrorCode is System.Net.Sockets.SocketError.HostNotFound or System.Net.Sockets.SocketError.NoData)
            return $"Can't connect to {station}: {host} could not be found. Check the address, or this network's DNS.";
        if (http?.HttpRequestError == System.Net.Http.HttpRequestError.ProxyTunnelError)
            return $"Can't connect to {station}: the network's proxy refused the connection to {host}.";
        if (socket?.SocketErrorCode == System.Net.Sockets.SocketError.AccessDenied)
            return $"Can't connect to {station}: Windows or a security program blocked Fernplay from connecting to {host}. Allow Fernplay.exe in its firewall.";
        if (socket?.SocketErrorCode is System.Net.Sockets.SocketError.ConnectionRefused or System.Net.Sockets.SocketError.ConnectionReset or System.Net.Sockets.SocketError.NetworkUnreachable or System.Net.Sockets.SocketError.HostUnreachable
            || http?.HttpRequestError == System.Net.Http.HttpRequestError.ConnectionError)
            return $"Can't connect to {station}: the connection to {host} was refused or cut off. If the station plays in a browser on this PC, a firewall or security program is blocking Fernplay.exe.";
        // Anything else: say what Windows or .NET said rather than guessing.
        var inner = ex;
        while (inner.InnerException is not null) inner = inner.InnerException;
        return $"Can't connect to {station}: {inner.Message.TrimEnd('.')} (0x{inner.HResult:X8}).";
    }

    /// <summary>The exception chain in one line, for the diagnostics log.</summary>
    public static string Detail(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e is not null; e = e.InnerException)
        {
            string extra = e switch
            {
                System.Net.Http.HttpRequestException h => $" [{h.HttpRequestError}{(h.StatusCode is { } s ? $", {(int)s}" : "")}]",
                System.Net.Sockets.SocketException s => $" [{s.SocketErrorCode}]",
                _ => "",
            };
            parts.Add($"{e.GetType().Name} 0x{e.HResult:X8}{extra}: {e.Message}");
        }
        return string.Join(" -> ", parts);
    }

    private static T? Find<T>(Exception ex) where T : Exception
    {
        for (var e = ex; e is not null; e = e.InnerException) if (e is T t) return t;
        return null;
    }

    private static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;

    private static bool IsMediaFoundationError(Exception ex) =>
        (ex.HResult & 0xFFFF0000) == 0xC00D0000 || ex.Message.Contains("0xC00D", StringComparison.OrdinalIgnoreCase);

    public const string MissingMediaFeatures =
        "Windows media features are missing. On a Windows \"N\" edition, install the Media Feature Pack from Microsoft, then restart Fernplay.";
}
