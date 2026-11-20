using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fernplay.Core.Audio;

namespace Fernplay.App;

/// <summary>
/// The player's small display area in either look (Fernplay's, a classic skin's, its windowshade strip). A left-click
/// steps to the next mode, a right-click opens <see cref="VisMenu"/>; the window feeds it on the display timer.
/// </summary>
public interface IVisDisplay
{
    /// <summary>True in Fernplay's look: the area is big enough for the vectorscope.</summary>
    bool HasVectorscope { get; }

    /// <summary>The mode shown (the saved one, or the oscilloscope for a saved vectorscope on a classic skin).</summary>
    VisMode Mode { get; }

    /// <summary>Spectrum bands the bars want right now.</summary>
    int BandCount { get; }

    /// <summary>The settings changed (or were first given): redraw in the new mode and style.</summary>
    void Apply(VisSettings settings);

    void PushLevels(float[] levels);

    /// <summary>The latest stereo samples, oldest first; <paramref name="live"/> false while paused or stopped.</summary>
    void PushWave(float[] left, float[] right, bool live);

    /// <summary>A left-click (or UI Automation's Invoke): the window steps to the next mode.</summary>
    event Action<IVisDisplay>? Clicked;
}

/// <summary>Shared wiring of a display element: click, hand cursor, tooltip; never takes the keyboard focus.</summary>
internal static class VisInput
{
    public const string Tip = "Click: bars, oscilloscope, off. Right-click for options.";
    public const string TipWithVectorscope = "Click: bars, oscilloscope, vectorscope, off. Right-click for options.";

    public static void Attach(FrameworkElement e, bool vectorscope, Action click)
    {
        e.Focusable = false;
        e.Cursor = Cursors.Hand;
        e.ToolTip = vectorscope ? TipWithVectorscope : Tip;
        e.MouseLeftButtonDown += (_, args) =>
        {
            click();
            args.Handled = true; // not a window drag or a double-click on the bar behind it
        };
    }

    public static string ModeName(VisMode m) => m switch
    {
        VisMode.Bars => "bars",
        VisMode.Oscilloscope => "oscilloscope",
        VisMode.Vectorscope => "vectorscope",
        _ => "off",
    };
}

/// <summary>UI Automation for a display area: named after its mode, Invoke steps to the next one.</summary>
internal sealed class VisPeer(FrameworkElement owner, IVisDisplay display, Action invoke) : FrameworkElementAutomationPeer(owner), IInvokeProvider
{
    protected override string GetNameCore() => $"Display area: {VisInput.ModeName(display.Mode)}";
    protected override string GetHelpTextCore() => display.HasVectorscope ? VisInput.TipWithVectorscope : VisInput.Tip;
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;
    protected override string GetClassNameCore() => Owner.GetType().Name;
    protected override bool IsKeyboardFocusableCore() => false;
    public override object? GetPattern(PatternInterface p) => p == PatternInterface.Invoke ? this : base.GetPattern(p);
    public void Invoke() => invoke();
}

/// <summary>
/// The display area's options, for its right-click menu and the app menu's "Display" submenu: the mode, bar style,
/// peaks, falloff speeds and oscilloscope style. Items are made again each time the menu opens, so they show the
/// current settings and the current look's modes.
/// </summary>
internal static class VisMenu
{
    public static void Fill(ItemsControl menu, VisSettings s, bool hasVectorscope, Action changed)
    {
        menu.Items.Clear();
        var all = new List<(MenuItem Item, Func<bool> IsOn)>();
        bool syncing = false;

        void Sync()
        {
            syncing = true;
            foreach (var (item, isOn) in all) item.IsChecked = isOn();
            syncing = false;
        }

        // Checked/Unchecked, not Click: Narrator and UI Automation toggle without a click. A choice that is on stays on
        // when it is clicked again (radio behaviour).
        MenuItem Choice(string header, string name, Func<bool> isOn, Action set)
        {
            var m = new MenuItem { Header = header, IsCheckable = true, IsChecked = isOn() };
            AutomationProperties.SetName(m, name);
            m.Checked += (_, _) => { if (syncing) return; set(); changed(); Sync(); };
            m.Unchecked += (_, _) => { if (syncing) return; Sync(); };
            all.Add((m, isOn));
            return m;
        }

        MenuItem Switch(string header, string name, Func<bool> isOn, Action<bool> set)
        {
            var m = new MenuItem { Header = header, IsCheckable = true, IsChecked = isOn() };
            AutomationProperties.SetName(m, name);
            m.Checked += (_, _) => { if (syncing) return; set(true); changed(); };
            m.Unchecked += (_, _) => { if (syncing) return; set(false); changed(); };
            all.Add((m, isOn));
            return m;
        }

        MenuItem Speeds(string header, string name, Func<Falloff> get, Action<Falloff> set)
        {
            var sub = new MenuItem { Header = header };
            AutomationProperties.SetName(sub, name);
            foreach (var f in new[] { Falloff.Slow, Falloff.Normal, Falloff.Fast })
                sub.Items.Add(Choice(f.ToString(), $"{name}: {f.ToString().ToLowerInvariant()}", () => get() == f, () => set(f)));
            return sub;
        }

        foreach (var mode in VisCycle.Modes(hasVectorscope))
        {
            string text = mode switch { VisMode.Bars => "Spectrum bars", VisMode.Oscilloscope => "Oscilloscope", VisMode.Vectorscope => "Stereo vectorscope", _ => "Off" };
            menu.Items.Add(Choice(text, $"Display: {VisInput.ModeName(mode)}", () => VisCycle.Shown(s.Mode, hasVectorscope) == mode, () => s.Mode = mode));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Choice("Thick bars", "Thick bars", () => s.Bars == BarStyle.Thick, () => s.Bars = BarStyle.Thick));
        menu.Items.Add(Choice("Thin bars", "Thin bars", () => s.Bars == BarStyle.Thin, () => s.Bars = BarStyle.Thin));
        menu.Items.Add(Switch("Peaks", "Peak caps", () => s.Peaks, on => s.Peaks = on));
        menu.Items.Add(Speeds("Bar falloff", "Bar falloff", () => s.BarFalloff, f => s.BarFalloff = f));
        menu.Items.Add(Speeds("Peak falloff", "Peak falloff", () => s.PeakFalloff, f => s.PeakFalloff = f));
        menu.Items.Add(new Separator());
        menu.Items.Add(Choice("Oscilloscope dots", "Oscilloscope dots", () => s.Scope == ScopeStyle.Dots, () => s.Scope = ScopeStyle.Dots));
        menu.Items.Add(Choice("Oscilloscope lines", "Oscilloscope lines", () => s.Scope == ScopeStyle.Lines, () => s.Scope = ScopeStyle.Lines));
    }
}

/// <summary>
/// Fernplay's own display area, over the whole LCD behind the clock. Bars and the oscilloscope use the 14 px strip at
/// the bottom (green to yellow to red, peak caps that fall slowly); the stereo vectorscope a square at the left, drawn
/// into a small bitmap that fades each frame (a short phosphor trail) and fades out when the music stops.
/// </summary>
public sealed class SpectrumDisplay : FrameworkElement, IVisDisplay
{
    public const int ThickBands = 19;
    private const double StripHeight = 14, VectorMax = 32, VectorLeft = 8;
    private const int Res = 80; // vectorscope bitmap pixels per side

    private VisSettings _s = new();
    private VisMode _mode = VisMode.Bars;
    private readonly SpectrumSmoother _smooth = new();
    private readonly float[] _scope = new float[256]; // one sample per column, for the strip
    private int _scopeCount;
    private bool _live;

    private static readonly Brush Cap = Frozen(Color.FromRgb(0xC8, 0xCC, 0xD6));
    private static readonly Brush Lcd = Frozen(Color.FromRgb(0x4B, 0xE8, 0x68));
    private static readonly Pen ScopePen = FrozenPen(Color.FromRgb(0x4B, 0xE8, 0x68), 0.9);
    private static readonly Pen Guide = FrozenPen(Color.FromRgb(0x1C, 0x3A, 0x22), 0.5);
    private Brush? _bar;
    private double _barHeight = -1;

    // Vectorscope
    private WriteableBitmap? _vbm;
    private readonly float[] _glow = new float[Res * Res];
    private readonly uint[] _pixels = new uint[Res * Res];
    private static readonly uint[] Palette = BuildPalette();
    private bool _glowing;
    private float _envelope;

    public SpectrumDisplay()
    {
        VisInput.Attach(this, true, () => Clicked?.Invoke(this));
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
    }

    public event Action<IVisDisplay>? Clicked;
    public bool HasVectorscope => true;
    public VisMode Mode => _mode;
    public int BandCount => _s.Bars == BarStyle.Thin ? Math.Max(ThickBands, (int)(ActualWidth / 2)) : ThickBands;

    protected override AutomationPeer OnCreateAutomationPeer() => new VisPeer(this, this, () => Clicked?.Invoke(this));

    public void Apply(VisSettings settings)
    {
        _s = settings;
        var mode = VisCycle.Shown(settings.Mode, true);
        if (mode != _mode)
        {
            _smooth.Clear();
            _live = false;
            ClearGlow();
            _mode = mode;
        }
        InvalidateVisual();
    }

    public void PushLevels(float[] levels)
    {
        _smooth.Push(levels, _s.BarFalloff, _s.PeakFalloff);
        InvalidateVisual();
    }

    public void PushWave(float[] left, float[] right, bool live)
    {
        if (_mode == VisMode.Vectorscope) { PushVector(left, right, live); return; }
        _live = live;
        if (live)
        {
            int cols = Math.Min(_scope.Length, Math.Max(1, (int)ActualWidth));
            int n = Math.Min(ScopeMath.Samples, left.Length), start = left.Length - n;
            for (int x = 0; x < cols; x++)
            {
                int k = start + ScopeMath.SampleIndex(x, cols, n);
                _scope[x] = (left[k] + right[k]) * 0.5f;
            }
            _scopeCount = cols;
        }
        InvalidateVisual();
    }

    private void PushVector(float[] left, float[] right, bool live)
    {
        if (!live && !_glowing) return; // faded out: nothing to do while paused or stopped
        const float fade = 0.70f;
        float max = 0;
        for (int i = 0; i < _glow.Length; i++)
        {
            float g = _glow[i] * fade;
            if (g < 0.004f) g = 0;
            _glow[i] = g;
            if (g > max) max = g;
        }
        if (live)
        {
            float peak = 0;
            for (int i = 0; i < left.Length; i++)
            {
                var (x, y) = VectorscopeMath.Rotate(left[i], right[i]);
                peak = Math.Max(peak, Math.Max(Math.Abs(x), Math.Abs(y)));
            }
            _envelope = VectorscopeMath.Envelope(_envelope, peak);
            float gain = VectorscopeMath.Gain(_envelope);
            for (int i = 0; i < left.Length; i++)
            {
                var (x, y) = VectorscopeMath.Rotate(left[i], right[i]);
                var (px, py) = VectorscopeMath.ToPixel(x * gain, y * gain, Res);
                int k = py * Res + px;
                float g = Math.Min(1f, _glow[k] + 0.22f);
                _glow[k] = g;
                if (g > max) max = g;
            }
        }
        _glowing = max > 0;
        for (int i = 0; i < _pixels.Length; i++) _pixels[i] = Palette[(int)(_glow[i] * 255)];
        _vbm ??= new WriteableBitmap(Res, Res, 96, 96, PixelFormats.Pbgra32, null);
        _vbm.WritePixels(new Int32Rect(0, 0, Res, Res), _pixels, Res * 4, 0);
        InvalidateVisual();
    }

    private void ClearGlow()
    {
        Array.Clear(_glow);
        _envelope = 0;
        if (!_glowing) return;
        _glowing = false;
        Array.Clear(_pixels);
        _vbm?.WritePixels(new Int32Rect(0, 0, Res, Res), _pixels, Res * 4, 0);
    }

    private Rect Strip => new(0, Math.Max(0, ActualHeight - StripHeight), ActualWidth, Math.Min(StripHeight, ActualHeight));

    private Rect VectorSquare
    {
        get
        {
            double size = Math.Min(ActualHeight, VectorMax);
            return new Rect(VectorLeft, (ActualHeight - size) / 2, size, size);
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var strip = Strip;
        // Transparent: the strip (and the vectorscope's square) take the click even where nothing is drawn.
        dc.DrawRectangle(Brushes.Transparent, null, strip);
        switch (_mode)
        {
            case VisMode.Bars: RenderBars(dc, strip); break;
            case VisMode.Oscilloscope: RenderScope(dc, strip); break;
            case VisMode.Vectorscope: RenderVector(dc, VectorSquare); break;
        }
    }

    private void RenderBars(DrawingContext dc, Rect strip)
    {
        int n = _smooth.Count;
        if (n == 0 || strip.Height <= 0) return;
        if (_bar is null || _barHeight != strip.Height) { _bar = BarBrush(strip.Top, strip.Bottom); _barHeight = strip.Height; }
        bool thin = _s.Bars == BarStyle.Thin;
        double step = strip.Width / n, w = thin ? Math.Min(1, step) : step - 1, h = strip.Height;
        var levels = _smooth.Levels;
        var peaks = _smooth.Peaks;
        for (int i = 0; i < n; i++)
        {
            double x = i * step, bh = Math.Round(levels[i] * h);
            // The gradient is fixed to the strip, so tall bars reach the red.
            if (bh > 0) dc.DrawRectangle(_bar, null, new Rect(x, strip.Bottom - bh, w, bh));
            if (_s.Peaks && peaks[i] > 0.02f) dc.DrawRectangle(Cap, null, new Rect(x, Math.Max(strip.Top, strip.Bottom - Math.Round(peaks[i] * h) - 1), w, 0.8));
        }
    }

    private void RenderScope(DrawingContext dc, Rect strip)
    {
        if (!_live || _scopeCount == 0) return;
        double mid = strip.Top + strip.Height / 2, amp = strip.Height / 2 - 0.5, step = strip.Width / _scopeCount;
        double Y(float v) => mid - Math.Clamp(v, -1f, 1f) * amp;
        if (_s.Scope == ScopeStyle.Dots)
        {
            for (int x = 0; x < _scopeCount; x++) dc.DrawRectangle(Lcd, null, new Rect(x * step, Y(_scope[x]) - 0.5, Math.Max(0.8, step * 0.8), 1));
            return;
        }
        var prev = new Point(0, Y(_scope[0]));
        for (int x = 1; x < _scopeCount; x++)
        {
            var p = new Point(x * step, Y(_scope[x]));
            dc.DrawLine(ScopePen, prev, p);
            prev = p;
        }
    }

    private void RenderVector(DrawingContext dc, Rect sq)
    {
        dc.DrawRectangle(Brushes.Transparent, null, sq);
        if (!_glowing || _vbm is null) return; // paused, stopped, or faded out: nothing, guides included
        // Guides: the L and R axes (diagonals) and the mid axis (mono).
        dc.DrawLine(Guide, sq.TopLeft, sq.BottomRight);
        dc.DrawLine(Guide, sq.TopRight, sq.BottomLeft);
        dc.DrawLine(Guide, new Point(sq.Left + sq.Width / 2, sq.Top), new Point(sq.Left + sq.Width / 2, sq.Bottom));
        dc.DrawImage(_vbm, sq);
    }

    private static Brush BarBrush(double top, double bottom)
    {
        var g = new LinearGradientBrush { StartPoint = new Point(0, bottom), EndPoint = new Point(0, top), MappingMode = BrushMappingMode.Absolute };
        g.GradientStops.Add(new GradientStop(Color.FromRgb(0x2F, 0xB8, 0x45), 0));
        g.GradientStops.Add(new GradientStop(Color.FromRgb(0xE3, 0xCF, 0x4A), 0.6));
        g.GradientStops.Add(new GradientStop(Color.FromRgb(0xE8, 0x5A, 0x33), 1));
        g.Freeze();
        return g;
    }

    /// <summary>Glow 0..255 → premultiplied colour: dim LCD green, brighter, to pale mint where many points land.</summary>
    private static uint[] BuildPalette()
    {
        var p = new uint[256];
        for (int i = 0; i < 256; i++)
        {
            double t = i / 255.0, a = Math.Min(1, t * 1.6), w = Math.Clamp((t - 0.55) / 0.45, 0, 1);
            double r = 0x4B + (0xE6 - 0x4B) * w, g = 0xE8 + (0xFF - 0xE8) * w, b = 0x68 + (0xEC - 0x68) * w;
            p[i] = (uint)(a * 255) << 24 | (uint)(r * a) << 16 | (uint)(g * a) << 8 | (uint)(b * a);
        }
        return p;
    }

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static Pen FrozenPen(Color c, double t) { var p = new Pen(new SolidColorBrush(c), t); p.Freeze(); return p; }
}
