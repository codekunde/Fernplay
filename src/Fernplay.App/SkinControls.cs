using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using Fernplay.Core.Audio;

namespace Fernplay.App;

/// <summary>7-segment clock in the LCD. Unlit segments show faintly, like a real display.</summary>
public sealed class SevenSegment : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(SevenSegment), new FrameworkPropertyMetadata("0:00", FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    private const double W = 7, H = 11, T = 1.4, Gap = 2; // digit size and segment thickness, skin pixels
    private static readonly Brush Lit = Frozen(Color.FromRgb(0x4B, 0xE8, 0x68));
    private static readonly Brush Ghost = Frozen(Color.FromRgb(0x10, 0x2C, 0x16));

    // Segments a..g per digit.
    private static readonly string[] Map = ["abcdef", "bc", "abdeg", "abcdg", "bcfg", "acdfg", "acdefg", "abc", "abcdefg", "abcdfg"];

    protected override Size MeasureOverride(Size available) => new(TextWidth(Text), H);

    protected override AutomationPeer OnCreateAutomationPeer() => new TextPeer(this, () => Text);

    private static double TextWidth(string text) => text.Sum(c => c switch { ':' => 3, '-' => W + Gap, _ => W + Gap }) - Gap;

    protected override void OnRender(DrawingContext dc)
    {
        // Right-aligned so the digits do not jump when the minus sign comes and goes.
        double x = ActualWidth - TextWidth(Text);
        foreach (var c in Text)
        {
            if (c == ':')
            {
                dc.DrawRectangle(Lit, null, new Rect(x + 0.6, 3, 1.4, 1.4));
                dc.DrawRectangle(Lit, null, new Rect(x + 0.6, 7.5, 1.4, 1.4));
                x += 3;
                continue;
            }
            string on = c == '-' ? "g" : char.IsDigit(c) ? Map[c - '0'] : "";
            foreach (var s in "abcdefg") dc.DrawGeometry(on.Contains(s) ? Lit : Ghost, null, Segment(s, x));
            x += W + Gap;
        }
    }

    private static Geometry Segment(char s, double x)
    {
        double m = H / 2;
        Rect r = s switch
        {
            'a' => new Rect(x + T * 0.6, 0, W - T * 1.2, T),
            'b' => new Rect(x + W - T, T * 0.6, T, m - T * 0.9),
            'c' => new Rect(x + W - T, m + T * 0.3, T, m - T * 0.9),
            'd' => new Rect(x + T * 0.6, H - T, W - T * 1.2, T),
            'e' => new Rect(x, m + T * 0.3, T, m - T * 0.9),
            'f' => new Rect(x, T * 0.6, T, m - T * 0.9),
            _ => new Rect(x + T * 0.6, m - T / 2, W - T * 1.2, T),
        };
        return new RectangleGeometry(r);
    }

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
}

/// <summary>Spectrum analyser: bars green to yellow to red with peak caps that fall slowly.</summary>
public sealed class SpectrumBars : FrameworkElement
{
    public const int Bands = 19;
    private readonly float[] _levels = new float[Bands];
    private readonly float[] _peaks = new float[Bands];
    private static readonly Brush Bar;
    private static readonly Brush Cap = Frozen(Color.FromRgb(0xC8, 0xCC, 0xD6));

    static SpectrumBars()
    {
        var g = new LinearGradientBrush { StartPoint = new Point(0, 1), EndPoint = new Point(0, 0), MappingMode = BrushMappingMode.RelativeToBoundingBox };
        g.GradientStops.Add(new GradientStop(Color.FromRgb(0x2F, 0xB8, 0x45), 0));
        g.GradientStops.Add(new GradientStop(Color.FromRgb(0xE3, 0xCF, 0x4A), 0.6));
        g.GradientStops.Add(new GradientStop(Color.FromRgb(0xE8, 0x5A, 0x33), 1));
        g.Freeze();
        Bar = g;
    }

    /// <summary>New levels 0..1; bars fall smoothly instead of jumping down.</summary>
    public void Push(float[] levels)
    {
        for (int i = 0; i < Bands && i < levels.Length; i++)
        {
            _levels[i] = Math.Max(levels[i], _levels[i] - 0.08f);
            _peaks[i] = Math.Max(_levels[i], _peaks[i] - 0.015f);
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth / Bands, h = ActualHeight;
        // The gradient spans the whole height, so tall bars reach the red.
        var clip = new RectangleGeometry(new Rect(0, 0, ActualWidth, h));
        dc.PushClip(clip);
        for (int i = 0; i < Bands; i++)
        {
            double x = i * w;
            double bh = Math.Round(_levels[i] * h);
            if (bh > 0)
            {
                dc.PushClip(new RectangleGeometry(new Rect(x, h - bh, w - 1, bh)));
                dc.DrawRectangle(Bar, null, new Rect(x, 0, w - 1, h));
                dc.Pop();
            }
            if (_peaks[i] > 0.02f) dc.DrawRectangle(Cap, null, new Rect(x, h - Math.Round(_peaks[i] * h) - 1, w - 1, 0.8));
        }
        dc.Pop();
    }

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
}

/// <summary>The scrolling song title in the display. Scrolls only when the text does not fit.</summary>
public sealed class Marquee : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(Marquee), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((Marquee)d)._offset = 0));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    /// <summary>Text size in skin pixels (7 in the display, 6 in the rolled-up strip).</summary>
    public double TextSize { get; set; } = 7;

    private double _offset;
    private static readonly Brush Green = Frozen(Color.FromRgb(0x4B, 0xE8, 0x68));
    private const string Separator = "   ***   ";

    protected override AutomationPeer OnCreateAutomationPeer() => new TextPeer(this, () => Text);

    /// <summary>Called by the window's animation timer; moves the text one step.</summary>
    public void Step()
    {
        _offset += 0.5;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));
        var ft = Format(Text);
        double y = (ActualHeight - ft.Height) / 2;
        if (ft.WidthIncludingTrailingWhitespace <= ActualWidth)
            dc.DrawText(ft, new Point(0, y));
        else
        {
            var loop = Format(Text + Separator);
            double w = loop.WidthIncludingTrailingWhitespace;
            double x = -(_offset % w);
            dc.DrawText(loop, new Point(x, y));
            dc.DrawText(loop, new Point(x + w, y));
        }
        dc.Pop();
    }

    private FormattedText Format(string s) => new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
        new Typeface(new FontFamily("Tahoma"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), TextSize, Green,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
}

/// <summary>Small preview of the EQ curve: one point per band, joined smoothly; the dim line is 0 dB.</summary>
public sealed class EqCurve : FrameworkElement
{
    private double[] _bands = new double[10];
    private bool _enabled = true;
    private static readonly Pen Line = FrozenPen(Color.FromRgb(0x4B, 0xE8, 0x68), 0.8);
    private static readonly Pen Off = FrozenPen(Color.FromRgb(0x2A, 0x5A, 0x33), 0.8);
    private static readonly Pen Zero = FrozenPen(Color.FromRgb(0x1C, 0x3A, 0x22), 0.5);

    public void Show(Equalizer eq)
    {
        _bands = eq.Bands;
        _enabled = eq.Enabled;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight, pad = 1.5;
        double Y(double db) => h / 2 - db / Equalizer.MaxDb * (h / 2 - pad);
        dc.DrawLine(Zero, new Point(0, h / 2), new Point(w, h / 2));
        var pts = _bands.Select((db, i) => new Point(pad + i * (w - 2 * pad) / 9, Y(db))).ToList();
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(pts[0], false, false);
            // Catmull-Rom through the band points, as Bezier segments.
            for (int i = 0; i < pts.Count - 1; i++)
            {
                var p0 = pts[Math.Max(i - 1, 0)]; var p1 = pts[i]; var p2 = pts[i + 1]; var p3 = pts[Math.Min(i + 2, pts.Count - 1)];
                var c1 = new Point(p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6);
                var c2 = new Point(p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6);
                ctx.BezierTo(c1, c2, p2, true, true);
            }
        }
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h)));
        dc.DrawGeometry(null, _enabled ? Line : Off, geo);
        dc.Pop();
    }

    private static Pen FrozenPen(Color c, double t) { var p = new Pen(new SolidColorBrush(c), t); p.Freeze(); return p; }
}

/// <summary>Lets screen readers and UI Automation read the text of a drawn element.</summary>
internal sealed class TextPeer(FrameworkElement owner, Func<string> text) : FrameworkElementAutomationPeer(owner)
{
    protected override string GetNameCore() => text();
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;
    protected override string GetClassNameCore() => Owner.GetType().Name;
    protected override bool IsControlElementCore() => true;
}

/// <summary>A double-click anywhere on a slider sets it back to its resting value (EQ bands and preamp 0 dB, balance centre).</summary>
public static class SliderReset
{
    public static void OnDoubleClick(System.Windows.Controls.Slider slider, double value)
    {
        // Preview and handled: the second press must not also start a knob drag or a page step, which would move it off again.
        slider.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount != 2 || !slider.IsEnabled) return;
            slider.Value = value;
            e.Handled = true;
        };
    }
}