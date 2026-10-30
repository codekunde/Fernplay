using NAudio.Wave;

namespace Fernplay.Core.Audio;

/// <summary>
/// First part of the chain (decoder → EQ → output): turns one file into float samples.
/// Read and Seek may be called from different threads; implementations make them safe together.
/// </summary>
public interface ITrackDecoder : ISampleProvider, IDisposable
{
    TimeSpan TotalTime { get; }

    /// <summary>Read position in the file (slightly ahead of what is heard, by the output's buffer).</summary>
    TimeSpan CurrentTime { get; }

    /// <summary>Moves the read position; clamped to the track.</summary>
    void Seek(TimeSpan position);

    AudioFormatInfo Format { get; }
}

/// <summary>What the player shows under the title.</summary>
public sealed record AudioFormatInfo(string Codec, int SampleRate, int Channels)
{
    public string SampleRateText => $"{SampleRate / 1000.0:0.#} kHz";
    public string ChannelsText => Channels switch { 1 => "mono", 2 => "stereo", _ => $"{Channels} channels" };
}
