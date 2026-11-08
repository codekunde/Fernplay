using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Fernplay.Core.Audio;

/// <summary>
/// First stage after the decoders: one fixed format (stereo float at the rate the output was opened with) that stays
/// open across tracks. Holds the playing track, the next one queued, and during a crossfade the old track fading out.
/// With a fade of zero the next track follows gapless. It never fills a buffer with silence on its own: when the
/// playing track runs dry and nothing is queued it returns short, so the output stops and the end is seen.
/// Events are raised on the audio thread (or the caller's thread for Seek), never while the lock is held.
/// </summary>
public sealed class TrackMixer : ISampleProvider
{
    private readonly Lock _gate = new();
    private Deck? _current;
    private Deck? _queued;
    private TimeSpan _queuedFade;
    private Deck? _outgoing;      // fading out under _current
    private long _fadePos, _fadeLength; // in frames
    private bool _nearEndRaised;
    private volatile bool _reachedEnd;
    private int _currentId;
    private float[] _scratch = [];

    public TrackMixer(int sampleRate) => WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);

    public WaveFormat WaveFormat { get; }

    /// <summary>How long before the end of the playing track <see cref="NearEnd"/> fires (fade + time to open the next file).</summary>
    public TimeSpan NearEndLead { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>The playing track ran dry with nothing queued (a natural end).</summary>
    public bool ReachedEnd => _reachedEnd;

    /// <summary>Goes up each time the playing track changes (set, or handed over to the queued one).</summary>
    public int CurrentId { get { lock (_gate) return _currentId; } }

    public ITrackDecoder? Current { get { lock (_gate) return _current?.Decoder; } }

    public bool HasQueued { get { lock (_gate) return _queued is not null; } }

    /// <summary>True while an old track is still fading out underneath.</summary>
    public bool IsFading { get { lock (_gate) return _outgoing is not null; } }

    /// <summary>The playing track is within <see cref="NearEndLead"/> of its end: time to queue the next one. Carries <see cref="CurrentId"/>.</summary>
    public event EventHandler<int>? NearEnd;

    /// <summary>The queued track took over (at the start of its fade, or gapless at the end). Carries its decoder.</summary>
    public event EventHandler<ITrackDecoder>? Switched;

    /// <summary>
    /// Plays this track from where its decoder stands, at once: no fade. Everything else held is let go.
    /// <paramref name="gain"/> is the track's own level (ReplayGain), applied before the mix.
    /// </summary>
    public void SetCurrent(ITrackDecoder? decoder, float gain = 1f)
    {
        List<Deck> drop = [];
        lock (_gate)
        {
            AddIf(drop, _current); AddIf(drop, _queued); AddIf(drop, _outgoing);
            _current = decoder is null ? null : new Deck(decoder, WaveFormat.SampleRate) { Gain = gain };
            _queued = null; _outgoing = null;
            _currentId++;
            _nearEndRaised = false;
            _reachedEnd = false;
        }
        DisposeAll(drop);
    }

    /// <summary>
    /// Queues the track that follows the playing one. fade zero = gapless. Refused (false, nothing kept) when the playing
    /// track is no longer the one with <paramref name="forCurrentId"/>; the caller then disposes the decoder.
    /// </summary>
    public bool Queue(ITrackDecoder decoder, TimeSpan fade, int forCurrentId, float gain = 1f)
    {
        Deck? old;
        lock (_gate)
        {
            if (_current is null || forCurrentId != _currentId) return false;
            old = _queued;
            _queued = new Deck(decoder, WaveFormat.SampleRate) { Gain = gain };
            _queuedFade = fade < TimeSpan.Zero ? TimeSpan.Zero : fade;
        }
        old?.Dispose();
        return true;
    }

    /// <summary>Drops the queued track (the playlist changed, or a setting did). NearEnd fires again if the end is close.</summary>
    public void CancelQueued()
    {
        Deck? old;
        lock (_gate)
        {
            old = _queued;
            _queued = null;
            _nearEndRaised = false;
        }
        old?.Dispose();
    }

    /// <summary>Changes the level of a track held here (playing, queued or fading out); false when it is not held.</summary>
    public bool SetGain(ITrackDecoder decoder, float gain)
    {
        lock (_gate)
        {
            bool found = false;
            foreach (var d in new[] { _current, _queued, _outgoing })
                if (d is not null && d.Decoder == decoder) { d.Gain = gain; found = true; }
            return found;
        }
    }

    /// <summary>Drops the old track that is fading out (Stop: nothing should keep sounding).</summary>
    public void EndFade()
    {
        Deck? old;
        lock (_gate) { old = _outgoing; _outgoing = null; }
        old?.Dispose();
    }

    /// <summary>Moves the playing track. A queued track stays queued; its fade starts from wherever the end now is.</summary>
    public void Seek(TimeSpan position)
    {
        int? nearEnd;
        lock (_gate)
        {
            if (_current is null) return;
            _current.Seek(position);
            _reachedEnd = false;
            if (_queued is null) _nearEndRaised = false;
            nearEnd = CheckNearEnd();
        }
        if (nearEnd is { } id) NearEnd?.Invoke(this, id);
    }

    public int Read(Span<float> buffer)
    {
        int written = 0;
        int? nearEnd = null;
        ITrackDecoder? switched = null;
        List<Deck> drop = [];
        lock (_gate)
        {
            if (_scratch.Length < buffer.Length) _scratch = new float[buffer.Length];
            while (written < buffer.Length && _current is not null)
            {
                var span = buffer[written..];
                // A crossfade starts when the playing track's estimated remainder fits the fade.
                if (_queued is not null && _outgoing is null && _queuedFade > TimeSpan.Zero)
                {
                    long fadeFrames = Frames(_queuedFade);
                    long left = _current.RemainingFrames;
                    if (left >= 0 && left <= fadeFrames)
                    {
                        _outgoing = _current;
                        _fadeLength = Math.Max(1, left);
                        _fadePos = 0;
                        switched = TakeQueued();
                        continue;
                    }
                    // Read only up to the fade start, so it begins on the exact frame.
                    if (left > fadeFrames) span = span[..(int)Math.Min(span.Length, (left - fadeFrames) * 2)];
                }

                int n = _current.Read(span);
                if (_outgoing is not null) MixOutgoing(span, n, drop);
                written += n;
                if (n < span.Length)
                {
                    // The playing track ended. During a fade (a very short new file) the old one is let go too.
                    if (_outgoing is not null) { AddIf(drop, _outgoing); _outgoing = null; }
                    if (_queued is not null) { AddIf(drop, _current); switched = TakeQueued(); continue; } // gapless
                    _reachedEnd = true;
                    break;
                }
            }
            nearEnd = CheckNearEnd();
        }
        DisposeAll(drop);
        if (switched is not null) Switched?.Invoke(this, switched);
        if (nearEnd is { } id) NearEnd?.Invoke(this, id);
        return written;
    }

    // Called under the lock. Returns the id to report when NearEnd should fire now.
    private int? CheckNearEnd()
    {
        if (_nearEndRaised || _queued is not null || _current is null) return null;
        long left = _current.RemainingFrames;
        if (left < 0 || left > Frames(NearEndLead)) return null;
        _nearEndRaised = true;
        return _currentId;
    }

    // Called under the lock: the queued deck becomes the playing one.
    private ITrackDecoder TakeQueued()
    {
        _current = _queued!;
        _queued = null;
        _currentId++;
        _nearEndRaised = false;
        return _current.Decoder;
    }

    // Called under the lock: equal-power crossfade, the new track's n samples already in span.
    private void MixOutgoing(Span<float> span, int n, List<Deck> drop)
    {
        var old = _outgoing!;
        var oldBuf = _scratch.AsSpan(0, n);
        int got = old.Read(oldBuf);
        if (got < n) oldBuf[got..].Clear();
        for (int i = 0; i + 1 < n; i += 2)
        {
            double t = Math.Min(1.0, (double)_fadePos / _fadeLength);
            float gin = (float)Math.Sin(t * Math.PI / 2), gout = (float)Math.Cos(t * Math.PI / 2);
            span[i] = span[i] * gin + oldBuf[i] * gout;
            span[i + 1] = span[i + 1] * gin + oldBuf[i + 1] * gout;
            _fadePos++;
        }
        if (_fadePos >= _fadeLength || got < n) { AddIf(drop, old); _outgoing = null; }
    }

    /// <summary>The gains at a point of the fade, 0 = start, 1 = end: (incoming, outgoing). Their squares add up to one.</summary>
    public static (double In, double Out) FadeGains(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return (Math.Sin(t * Math.PI / 2), Math.Cos(t * Math.PI / 2));
    }

    private long Frames(TimeSpan t) => (long)(t.TotalSeconds * WaveFormat.SampleRate);

    private static void AddIf(List<Deck> list, Deck? d) { if (d is not null) list.Add(d); }

    // Decoders are let go off the audio thread (Media Foundation readers prefer not to be released there).
    private static void DisposeAll(List<Deck> decks)
    {
        if (decks.Count == 0) return;
        _ = Task.Run(() => { foreach (var d in decks) d.Dispose(); });
    }

    /// <summary>One track converted to the mixer's format, counting the frames it has given.</summary>
    private sealed class Deck(ITrackDecoder decoder, int rate) : IDisposable
    {
        private ISampleProvider _samples = Convert(decoder, rate);
        private long _frames = (long)(decoder.CurrentTime.TotalSeconds * rate);
        private readonly long _total = decoder.TotalTime > TimeSpan.Zero ? (long)(decoder.TotalTime.TotalSeconds * rate) : -1;

        public ITrackDecoder Decoder => decoder;

        /// <summary>The track's own level, 1 = as decoded; set under the mixer's lock.</summary>
        public float Gain { get; set; } = 1f;

        /// <summary>Frames left by the decoder's stated length; -1 for a live stream. Only an estimate (VBR files).</summary>
        public long RemainingFrames => _total < 0 ? -1 : Math.Max(0, _total - _frames);

        /// <summary>Fills span completely unless the track ends (a resampler may hand back less than asked mid-track).</summary>
        public int Read(Span<float> span)
        {
            int n = 0;
            while (n < span.Length)
            {
                int got = _samples.Read(span[n..]);
                if (got <= 0) break;
                n += got;
            }
            _frames += n / 2;
            float g = Gain;
            if (g != 1f)
                for (int i = 0; i < n; i++) span[i] *= g;
            return n;
        }

        public void Seek(TimeSpan position)
        {
            decoder.Seek(position);
            _samples = Convert(decoder, rate); // a fresh resampler, so nothing from before the seek is heard
            _frames = (long)(decoder.CurrentTime.TotalSeconds * rate);
        }

        public void Dispose() => decoder.Dispose();

        private static ISampleProvider Convert(ITrackDecoder d, int rate)
        {
            ISampleProvider s = d.WaveFormat.Channels switch
            {
                1 => new MonoToStereoSampleProvider(d),
                2 => d,
                _ => new StereoDownmix(d),
            };
            return s.WaveFormat.SampleRate == rate ? s : new WdlResamplingSampleProvider(s, rate);
        }
    }
}

/// <summary>
/// More than two channels down to stereo. For 5.1 (FL FR C LFE SL SR) the usual mix: centre and surrounds at -3 dB,
/// LFE left out, scaled so a full-scale signal does not clip. Other layouts keep the first two channels.
/// </summary>
public sealed class StereoDownmix(ISampleProvider source) : ISampleProvider
{
    private const float Half = 0.7071f, Scale = 1f / (1f + 2 * Half);
    private readonly int _in = source.WaveFormat.Channels;
    private float[] _buf = [];

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);

    public int Read(Span<float> buffer)
    {
        int frames = buffer.Length / 2;
        if (_buf.Length < frames * _in) _buf = new float[frames * _in];
        int n = source.Read(_buf.AsSpan(0, frames * _in)) / _in;
        for (int f = 0; f < n; f++)
        {
            var x = _buf.AsSpan(f * _in, _in);
            if (_in == 6)
            {
                buffer[2 * f] = (x[0] + Half * x[2] + Half * x[4]) * Scale;
                buffer[2 * f + 1] = (x[1] + Half * x[2] + Half * x[5]) * Scale;
            }
            else { buffer[2 * f] = x[0]; buffer[2 * f + 1] = x[1]; }
        }
        return n * 2;
    }
}
