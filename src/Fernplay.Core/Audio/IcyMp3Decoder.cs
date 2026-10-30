using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using NAudio;
using NAudio.Wave;

namespace Fernplay.Core.Audio;

/// <summary>
/// Plays an MP3 internet radio station itself, so the song titles the station sends inside the stream (ICY
/// metadata) can be read; Media Foundation hides them. A background thread reads MP3 frames, decodes them with
/// Windows' MP3 codec (ACM) and keeps about five seconds of sound ready. Each title is tied to the point in the
/// sound where it arrived and is reported when that point is played, so it changes with the music (servers send
/// the first 20 to 40 seconds in a burst).
/// </summary>
public sealed class IcyMp3Decoder : ITrackDecoder, ILiveStream
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan BufferAhead = TimeSpan.FromSeconds(5);
    /// <summary>Sound decoded before the station counts as open, and again after the buffer ran dry.</summary>
    private static readonly TimeSpan Refill = TimeSpan.FromSeconds(1);
    /// <summary>No sound from the station for this long counts as a dropped connection.</summary>
    internal static TimeSpan StallLimit { get; set; } = TimeSpan.FromSeconds(15);

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        // Icecast sends header values such as icy-name as UTF-8.
        ResponseHeaderEncodingSelector = (_, _) => Encoding.UTF8,
        AllowAutoRedirect = true,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan, // a station never finishes; ConnectTimeout covers the answer
        DefaultRequestHeaders = { { "User-Agent", "Fernplay/0.1" } },
    };

    private readonly IcyStream _icy;
    private readonly IDisposable? _owner;
    private readonly Mp3WaveFormat _mp3Format;
    private readonly AcmMp3FrameDecompressor _decompressor;
    private readonly BufferedWaveProvider _buffer;
    private readonly ISampleProvider _samples;
    private readonly byte[] _pcm = new byte[65536];
    private readonly int _sampleRate, _channels, _blockAlign;
    private readonly ConcurrentQueue<(long At, string Title)> _titles = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _reader;

    private long _decodedFrames;   // sample frames decoded (reader thread)
    private long _playedFrames;    // sample frames handed to the player (audio thread)
    private volatile bool _finished;
    private long _silentSince;     // Environment.TickCount64 when the buffer ran dry, 0 while sound flows
    private string? _title;

    /// <summary>
    /// Connects to the station. Returns null when it is not MP3 with ICY titles, or Windows has no MP3 codec for
    /// it: Media Foundation then plays it as before. Throws when the station cannot be reached.
    /// </summary>
    public static IcyMp3Decoder? TryOpen(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Icy-MetaData", "1");
        using var timeout = new CancellationTokenSource(ConnectTimeout);
        HttpResponseMessage response;
        try { response = Http.Send(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token); }
        catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.InvalidResponse) { return null; } // e.g. an old "ICY 200 OK" server
        catch (OperationCanceledException ex) when (timeout.IsCancellationRequested) { throw new TimeoutException($"No answer from {url}.", ex); }
        try
        {
            response.EnsureSuccessStatusCode();
            bool mp3 = string.Equals(response.Content.Headers.ContentType?.MediaType, "audio/mpeg", StringComparison.OrdinalIgnoreCase);
            int metaInt = int.TryParse(Header(response, "icy-metaint"), out var m) ? m : 0;
            if (!mp3 || metaInt <= 0) { response.Dispose(); return null; }
            return new IcyMp3Decoder(response.Content.ReadAsStream(), metaInt, Header(response, "icy-name"), response);
        }
        catch (MmException) { response.Dispose(); return null; } // no ACM MP3 codec
        catch { response.Dispose(); throw; }
    }

    private static string? Header(HttpResponseMessage r, string name) =>
        r.Headers.TryGetValues(name, out var v) || r.Content.Headers.TryGetValues(name, out v) ? v.FirstOrDefault() : null;

    /// <summary>Plays from an already open stream (the tests use this). <paramref name="owner"/> is disposed with the decoder.</summary>
    internal IcyMp3Decoder(Stream body, int metaInt, string? stationName, IDisposable? owner = null)
    {
        _icy = new IcyStream(body, metaInt);
        _owner = owner;
        StationName = string.IsNullOrWhiteSpace(stationName) ? null : stationName.Trim();
        var first = ReadFrame() ?? throw new InvalidDataException("The station sent no MP3 audio.");
        _mp3Format = new Mp3WaveFormat(first.SampleRate, ChannelsOf(first), first.FrameLength, first.BitRate);
        _decompressor = new AcmMp3FrameDecompressor(_mp3Format);
        try
        {
            var pcm = _decompressor.OutputFormat;
            (_sampleRate, _channels, _blockAlign) = (pcm.SampleRate, pcm.Channels, pcm.BlockAlign);
            _buffer = new BufferedWaveProvider(pcm, BufferAhead * 2) { ReadFully = false };
            _samples = _buffer.ToSampleProvider();
            Format = new AudioFormatInfo("STREAM", pcm.SampleRate, pcm.Channels, first.BitRate / 1000);
            Decode(first);
            while (_buffer.BufferedDuration < Refill && ReadFrame() is { } next && SameFormat(next)) Decode(next);
        }
        catch
        {
            _decompressor.Dispose();
            throw;
        }
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "Fernplay radio reader" };
        _reader.Start();
    }

    public WaveFormat WaveFormat => _samples.WaveFormat;
    public AudioFormatInfo Format { get; }
    public TimeSpan TotalTime => TimeSpan.Zero; // live: no length
    public TimeSpan CurrentTime => TimeSpan.FromSeconds(Interlocked.Read(ref _playedFrames) / (double)_sampleRate);
    public string? StationName { get; }
    public string? Title => _title;
    public event EventHandler<string?>? TitleChanged;

    /// <summary>Why the stream stopped (null while it runs or after a normal end).</summary>
    public Exception? Error { get; private set; }

    public void Seek(TimeSpan position) { } // nothing to seek in

    /// <summary>
    /// Called by the audio thread. When the station falls behind, silence fills the gap until a second of sound
    /// is ready again (so it does not play in fragments). Once the stream has ended, or sent nothing for
    /// <see cref="StallLimit"/>, the rest is played and then 0 is returned, which the player treats as the end
    /// (the controller then reconnects).
    /// </summary>
    public int Read(Span<float> buffer)
    {
        if (_waiting && !_finished && _buffer.BufferedDuration < Refill) return Silence(buffer, 0);
        _waiting = false;
        _silentSince = 0;
        int n = _samples.Read(buffer);
        if (n > 0)
        {
            Interlocked.Add(ref _playedFrames, n / _channels);
            ReportTitle();
        }
        if (n == buffer.Length || _finished) return n;
        _waiting = true;
        return Silence(buffer, n);
    }

    private bool _waiting; // audio thread: the buffer ran dry, waiting for Refill

    private int Silence(Span<float> buffer, int from)
    {
        buffer[from..].Clear();
        long now = Environment.TickCount64;
        if (_silentSince == 0) _silentSince = now;
        else if (now - _silentSince > StallLimit.TotalMilliseconds) { Abort(); return from; }
        return buffer.Length;
    }

    private void ReportTitle()
    {
        long played = Interlocked.Read(ref _playedFrames);
        string? latest = null;
        bool any = false;
        while (_titles.TryPeek(out var t) && t.At <= played)
        {
            _titles.TryDequeue(out _);
            latest = t.Title;
            any = true;
        }
        if (!any) return;
        var title = string.IsNullOrWhiteSpace(latest) ? null : latest;
        if (title == _title) return;
        _title = title;
        TitleChanged?.Invoke(this, title);
    }

    private void ReadLoop()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                while (_buffer.BufferedDuration > BufferAhead)
                    if (_stop.Token.WaitHandle.WaitOne(50)) return;
                var frame = ReadFrame();
                if (frame is null) break; // the station closed the stream
                // A new sample rate or channel count needs a new decoder: end here and let the player reconnect.
                if (!SameFormat(frame)) break;
                Decode(frame);
            }
        }
        catch (Exception) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or HttpRequestException or ObjectDisposedException or MmException or InvalidOperationException)
        {
            Error ??= ex; // after a stall, the timeout stays the reason
        }
        finally
        {
            _finished = true;
            _decompressor.Dispose();
        }
    }

    /// <summary>Next MP3 frame, with any title that arrived before it queued at its position; null at the end.</summary>
    private Mp3Frame? ReadFrame()
    {
        Mp3Frame? frame;
        try { frame = Mp3Frame.LoadFromStream(_icy); }
        catch (EndOfStreamException) { return null; }
        if (_icy.TryTakeTitle(out var title)) _titles.Enqueue((_decodedFrames, title));
        return frame;
    }

    private void Decode(Mp3Frame frame)
    {
        int bytes = _decompressor.DecompressFrame(frame, _pcm, 0);
        if (bytes <= 0) return;
        _buffer.AddSamples(_pcm, 0, bytes);
        Interlocked.Add(ref _decodedFrames, bytes / _blockAlign);
    }

    private static int ChannelsOf(Mp3Frame f) => f.ChannelMode == ChannelMode.Mono ? 1 : 2;
    private bool SameFormat(Mp3Frame f) => f.SampleRate == _mp3Format.SampleRate && ChannelsOf(f) == _mp3Format.Channels;

    /// <summary>The station went quiet: drop the connection so the reader stops, and report the end.</summary>
    private void Abort()
    {
        _finished = true;
        Error ??= new TimeoutException("The station stopped sending.");
        ThreadPool.QueueUserWorkItem(_ => CloseConnection());
    }

    private void CloseConnection()
    {
        try { _icy.Dispose(); _owner?.Dispose(); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    public void Dispose()
    {
        _stop.Cancel();
        CloseConnection(); // unblocks a network read in progress
        _reader.Join(TimeSpan.FromSeconds(2));
    }
}
