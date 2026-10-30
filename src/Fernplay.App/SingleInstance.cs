using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Fernplay.Core;

namespace Fernplay.App;

/// <summary>
/// One Fernplay per user and settings folder. A second launch (Explorer opens files one process each) hands its
/// request to the running Fernplay over a named pipe and exits. The pipe accepts only the current user.
/// A Fernplay started with another settings folder (FERNPLAY_DATA, test runs) is a separate instance.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();

    private SingleInstance(Mutex mutex, string pipeName)
    {
        _mutex = mutex;
        _pipeName = pipeName;
    }

    /// <summary>
    /// The first Fernplay gets an instance (call <see cref="Listen"/>). Any later one sends the request to the
    /// first and gets null: it should exit. When the first cannot be reached in time, the request is lost and
    /// the caller gets null all the same, so a stuck Fernplay never leads to a pile of players.
    /// </summary>
    public static SingleInstance? Claim(OpenRequest request)
    {
        string key = InstanceKey();
        var mutex = new Mutex(initiallyOwned: true, $@"Local\CodeKunde.Fernplay.{key}", out bool first);
        // Pipe names are machine-wide: the Windows session keeps two signed-in users apart.
        string pipe = $"CodeKunde.Fernplay.{System.Diagnostics.Process.GetCurrentProcess().SessionId}.{key}";
        if (first) return new SingleInstance(mutex, pipe);

        mutex.Dispose();
        AllowSetForegroundWindow(-1); // ASFW_ANY: let the running Fernplay come to the front
        var deadline = DateTime.UtcNow.AddSeconds(10); // the first one may still be starting
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", pipe, PipeDirection.Out, PipeOptions.CurrentUserOnly);
                client.Connect(500);
                var bytes = Encoding.UTF8.GetBytes(request.ToMessage());
                client.Write(bytes);
                client.Flush();
                return null;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException) { Thread.Sleep(100); }
        }
        return null;
    }

    /// <summary>Receives requests from later launches on a background thread; <paramref name="received"/> runs there.</summary>
    public void Listen(Action<OpenRequest> received)
    {
        var token = _stop.Token;
        var thread = new Thread(() =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    server.WaitForConnectionAsync(token).GetAwaiter().GetResult();
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    if (OpenRequest.FromMessage(reader.ReadToEnd()) is { } request) received(request);
                }
                catch (OperationCanceledException) { return; }
                catch (IOException) { } // a sender went away mid-message: wait for the next
            }
        }) { IsBackground = true, Name = "Fernplay instance pipe" };
        thread.Start();
    }

    /// <summary>Same settings folder = same instance. Hash keeps the name short and free of path characters.</summary>
    private static string InstanceKey()
    {
        var folder = Path.GetDirectoryName(UserSettings.FilePath)!.ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(folder)))[..16];
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        _mutex.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
