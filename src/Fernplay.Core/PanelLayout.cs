namespace Fernplay.Core;

/// <summary>A panel's place on screen, in device-independent pixels.</summary>
public readonly record struct Box(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
}

/// <summary>
/// Height of the playlist's list area (skin pixels), set by dragging the panel's bottom edge.
/// It snaps to whole rows so no half-cut line shows at the bottom.
/// </summary>
public static class PlaylistHeight
{
    public const double Default = 116;
    public const int MinRows = 4;

    /// <param name="raw">Height the drag asks for.</param>
    /// <param name="rowHeight">Height of one row.</param>
    /// <param name="inset">Fixed part of the list area (its border).</param>
    /// <param name="max">Tallest allowed (down to the bottom of the screen).</param>
    /// <param name="edges">Heights at which the panel's bottom lines up with another panel's bottom. Within half a row
    /// of one, the height snaps to it instead of to whole rows (the spare part goes under the list, see <see cref="Split"/>).</param>
    public static double Snap(double raw, double rowHeight, double inset, double max, IEnumerable<double>? edges = null)
    {
        if (rowHeight <= 0) return Math.Max(raw, inset);
        int rows = (int)Math.Round((raw - inset) / rowHeight);
        int maxRows = Math.Max(MinRows, (int)Math.Floor((max - inset) / rowHeight));
        double whole = Math.Clamp(rows, MinRows, maxRows) * rowHeight + inset;
        double min = MinRows * rowHeight + inset;
        double best = whole, bestDistance = double.MaxValue;
        foreach (double e in edges ?? [])
        {
            double d = Math.Abs(raw - e);
            if (e >= min && e <= Math.Max(max, min) && d < rowHeight / 2 && d < bestDistance) (best, bestDistance) = (e, d);
        }
        return best;
    }

    /// <summary>Splits a height into the list's part (whole rows) and the spare part shown as space under the list.</summary>
    public static (double List, double Spare) Split(double height, double rowHeight, double inset)
    {
        if (rowHeight <= 0 || height <= inset) return (height, 0);
        double list = Math.Floor((height - inset) / rowHeight + 1e-6) * rowHeight + inset;
        return (list, Math.Max(0, height - list));
    }
}

/// <summary>Width of the playlist in Fernplay's look, in skin pixels.</summary>
public static class PlaylistWidth
{
    /// <summary>The player's width: the playlist is never narrower.</summary>
    public const double Default = 280;
}

/// <summary>
/// How the player, EQ and playlist panels stick together: edges snap within a distance, and panels whose
/// edges touch form a group that moves with the main panel.
/// </summary>
public static class PanelLayout
{
    public const double SnapDistance = 10;
    private const double Touch = 1.5;

    /// <summary>True when two panels share an edge (stacked or side by side) and overlap along it.</summary>
    public static bool Touching(Box a, Box b)
    {
        bool stacked = (Math.Abs(a.Bottom - b.Top) < Touch || Math.Abs(a.Top - b.Bottom) < Touch)
                       && a.Left < b.Right - Touch && b.Left < a.Right - Touch;
        bool sideBySide = (Math.Abs(a.Right - b.Left) < Touch || Math.Abs(a.Left - b.Right) < Touch)
                          && a.Top < b.Bottom - Touch && b.Top < a.Bottom - Touch;
        return stacked || sideBySide;
    }

    /// <summary>Indexes of every box connected to <paramref name="start"/> through touching edges (start included).</summary>
    public static List<int> Group(IReadOnlyList<Box> boxes, int start)
    {
        var group = new List<int> { start };
        for (int i = 0; i < group.Count; i++)
            for (int j = 0; j < boxes.Count; j++)
                if (!group.Contains(j) && Touching(boxes[group[i]], boxes[j])) group.Add(j);
        return group;
    }

    /// <summary>The smallest nudge per axis (at most <see cref="SnapDistance"/>) that lines an edge up with another panel or the screen.</summary>
    public static (double X, double Y) SnapOffset(Box r, IEnumerable<Box> others, Box workArea)
    {
        double bestX = double.MaxValue, bestY = double.MaxValue;
        void TryX(double d) { if (Math.Abs(d) <= SnapDistance && Math.Abs(d) < Math.Abs(bestX)) bestX = d; }
        void TryY(double d) { if (Math.Abs(d) <= SnapDistance && Math.Abs(d) < Math.Abs(bestY)) bestY = d; }

        foreach (var t in others)
        {
            bool nearVertically = r.Top < t.Bottom + SnapDistance && t.Top < r.Bottom + SnapDistance;
            bool nearHorizontally = r.Left < t.Right + SnapDistance && t.Left < r.Right + SnapDistance;
            if (nearVertically) { TryX(t.Left - r.Left); TryX(t.Right - r.Left); TryX(t.Left - r.Right); TryX(t.Right - r.Right); }
            if (nearHorizontally) { TryY(t.Top - r.Top); TryY(t.Bottom - r.Top); TryY(t.Top - r.Bottom); TryY(t.Bottom - r.Bottom); }
        }
        TryX(workArea.Left - r.Left); TryX(workArea.Right - r.Right);
        TryY(workArea.Top - r.Top); TryY(workArea.Bottom - r.Bottom);
        return (bestX == double.MaxValue ? 0 : bestX, bestY == double.MaxValue ? 0 : bestY);
    }
}
