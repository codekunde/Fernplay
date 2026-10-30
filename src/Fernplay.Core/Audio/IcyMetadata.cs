using System.Text;

namespace Fernplay.Core.Audio;

/// <summary>Reads the song title out of an ICY metadata block (the text Icecast and Shoutcast put inside the stream).</summary>
public static class IcyMetadata
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>The block as text: UTF-8 when it is valid UTF-8, otherwise Latin-1 (older servers); padding zeros removed.</summary>
    public static string Decode(ReadOnlySpan<byte> block)
    {
        int end = block.IndexOf((byte)0);
        if (end >= 0) block = block[..end];
        try { return StrictUtf8.GetString(block); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(block); }
    }

    /// <summary>
    /// The StreamTitle value ("Artist - Title" on most stations), trimmed; "" when the station sends an empty one;
    /// null when the block has no StreamTitle. The value ends at "';", so an apostrophe inside it is kept.
    /// </summary>
    public static string? StreamTitle(string text)
    {
        const string key = "StreamTitle='";
        int start = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        start += key.Length;
        int end = text.IndexOf("';", start, StringComparison.Ordinal);
        if (end < 0) end = text.LastIndexOf('\'');
        if (end < start) end = text.Length;
        return text[start..end].Trim();
    }
}
