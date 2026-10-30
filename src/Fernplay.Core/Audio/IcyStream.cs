namespace Fernplay.Core.Audio;

/// <summary>
/// Reads a stream sent with ICY metadata and hands out only the audio. After every <c>metaInt</c> audio bytes the
/// server sends one length byte (× 16) and that many bytes of text. Each read is filled completely unless the
/// stream ends, because NAudio's Mp3Frame treats a short read as the end. Not thread-safe: one reader.
/// </summary>
internal sealed class IcyStream(Stream source, int metaInt) : Stream
{
    private readonly int _metaInt = metaInt;
    private int _left = metaInt;
    private string? _title;
    private bool _hasTitle;

    /// <summary>The newest StreamTitle read since the last call ("" for an empty one).</summary>
    public bool TryTakeTitle(out string title)
    {
        title = _title ?? "";
        bool had = _hasTitle;
        _hasTitle = false;
        return had;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = ReadSome(buffer[total..]);
            if (n <= 0) break;
            total += n;
        }
        _audioRead += total;
        return total;
    }

    private int ReadSome(Span<byte> buffer)
    {
        if (_metaInt <= 0) return source.Read(buffer);
        if (_left == 0)
        {
            int lengthByte = source.ReadByte();
            if (lengthByte < 0) return 0;
            if (lengthByte > 0)
            {
                var block = new byte[lengthByte * 16];
                if (!FillFromSource(block)) return 0;
                if (IcyMetadata.StreamTitle(IcyMetadata.Decode(block)) is { } title) { _title = title; _hasTitle = true; }
            }
            _left = _metaInt;
        }
        int read = source.Read(buffer[..Math.Min(buffer.Length, _left)]);
        if (read > 0) _left -= read;
        return read;
    }

    private bool FillFromSource(Span<byte> into)
    {
        int got = 0;
        while (got < into.Length)
        {
            int n = source.Read(into[got..]);
            if (n <= 0) return false;
            got += n;
        }
        return true;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    /// <summary>Audio bytes read so far (Mp3Frame notes it as each frame's offset); cannot be set.</summary>
    public override long Position { get => _audioRead; set => throw new NotSupportedException(); }
    private long _audioRead;
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) source.Dispose();
        base.Dispose(disposing);
    }
}
