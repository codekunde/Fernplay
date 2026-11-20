namespace Fernplay.Core;

/// <summary>The three buttons in the taskbar thumbnail (hover over Fernplay's taskbar button). The number is the button's id.</summary>
public enum ThumbButton { Previous = 1, PlayPause = 2, Next = 3 }

public enum ThumbGlyph { Previous, Play, Pause, Next }

/// <summary>What one thumbnail button shows: its picture, tooltip, and whether it can be pressed.</summary>
public readonly record struct ThumbButtonView(ThumbButton Button, ThumbGlyph Glyph, string Tooltip, bool Enabled);

/// <summary>
/// The taskbar thumbnail buttons (Previous, Play/Pause, Next) for a play state, and their pictures drawn pixel by pixel.
/// Previous and Next are on whenever the list has entries, as the player's own buttons are (for a station too: they
/// step to the neighbouring entry). Play/Pause shows Pause only while something plays.
/// </summary>
public static class ThumbBar
{
    public static ThumbButtonView[] For(PlayState state, bool loaded, int trackCount)
    {
        bool hasList = trackCount > 0;
        bool playing = loaded && state == PlayState.Playing;
        // With the list emptied under a playing song, Pause still reaches it.
        bool playPauseOn = hasList || (loaded && state != PlayState.Stopped);
        return
        [
            new(ThumbButton.Previous, ThumbGlyph.Previous, "Previous", hasList),
            new(ThumbButton.PlayPause, playing ? ThumbGlyph.Pause : ThumbGlyph.Play, playing ? "Pause" : "Play", playPauseOn),
            new(ThumbButton.Next, ThumbGlyph.Next, "Next", hasList),
        ];
    }

    /// <summary>
    /// The glyph as alpha values (0 to 255), size × size, rows top to bottom; the taskbar wants it white. Drawn for
    /// each pixel size (16 px at 100 %, 20, 24, 32 at 200 %), so straight edges sit on whole pixels and stay sharp;
    /// only the slanted triangle edges are smoothed. Shapes are about 10/16 of the size high, like Windows' own.
    /// </summary>
    public static byte[] Draw(ThumbGlyph glyph, int size)
    {
        if (size < 8) throw new ArgumentOutOfRangeException(nameof(size));
        var a = new byte[size * size];
        double s = size / 16.0;
        int h = Fit(10 * s, size);
        int top = (size - h) / 2;
        switch (glyph)
        {
            case ThumbGlyph.Pause:
            {
                int w = Math.Max(2, (int)Math.Round(3 * s)), gap = Math.Max(2, (int)Math.Round(2 * s));
                if ((size - (2 * w + gap)) % 2 != 0) gap++;
                int x0 = (size - (2 * w + gap)) / 2;
                Rect(a, size, x0, top, w, h);
                Rect(a, size, x0 + w + gap, top, w, h);
                break;
            }
            case ThumbGlyph.Play:
            {
                int tw = (int)Math.Round(h * 0.866); // equilateral
                int x0 = (size - tw + 1) / 2;        // half a pixel right: a triangle's weight sits left of its box centre
                Triangle(a, size, x0, x0 + tw, top, h);
                break;
            }
            case ThumbGlyph.Next or ThumbGlyph.Previous:
            {
                int tw = (int)Math.Round(h * 0.8), bar = Math.Max(2, (int)Math.Round(2 * s));
                if ((size - (tw + bar)) % 2 != 0) tw--;
                int x0 = (size - (tw + bar)) / 2;
                Triangle(a, size, x0, x0 + tw, top, h);
                Rect(a, size, x0 + tw, top, bar, h);
                if (glyph == ThumbGlyph.Previous) Mirror(a, size);
                break;
            }
        }
        return a;
    }

    /// <summary>The whole number nearest <paramref name="want"/> that centres in <paramref name="size"/> (same odd/even).</summary>
    private static int Fit(double want, int size)
    {
        int n = (int)Math.Floor(want);
        if ((size - n) % 2 != 0) n = want - n > 0.5 ? n + 1 : n - 1;
        return n;
    }

    private static void Rect(byte[] a, int size, int x, int y, int w, int h)
    {
        for (int r = y; r < y + h; r++)
            for (int c = x; c < x + w; c++)
                a[r * size + c] = 255;
    }

    /// <summary>A right-pointing triangle: upright left edge at x0 (on a pixel edge), tip at x1 halfway down; 16×16 samples per pixel.</summary>
    private static void Triangle(byte[] a, int size, int x0, int x1, int top, int h)
    {
        const int n = 16;
        double mid = top + h / 2.0, half = h / 2.0, width = x1 - x0;
        for (int r = top; r < top + h; r++)
            for (int c = x0; c < x1; c++)
            {
                int inside = 0;
                for (int sy = 0; sy < n; sy++)
                {
                    double py = r + (sy + 0.5) / n;
                    for (int sx = 0; sx < n; sx++)
                    {
                        double px = c + (sx + 0.5) / n;
                        if (Math.Abs(py - mid) <= half * (x1 - px) / width) inside++;
                    }
                }
                a[r * size + c] = (byte)Math.Max(a[r * size + c], (int)Math.Round(255.0 * inside / (n * n)));
            }
    }

    private static void Mirror(byte[] a, int size)
    {
        for (int r = 0; r < size; r++)
            Array.Reverse(a, r * size, size);
    }
}
