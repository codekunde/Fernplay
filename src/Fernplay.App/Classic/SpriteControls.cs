using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fernplay.Core.Skins;

namespace Fernplay.App.Classic;

/// <summary>A classic skin's sprites as frozen WPF bitmaps (1 sprite pixel = 1 layout unit), cut once and kept.</summary>
public sealed class SkinBitmaps(ClassicSkin skin)
{
    private readonly Dictionary<Sprite, BitmapSource?> _cache = [];

    public ClassicSkin Skin { get; } = skin;

    public BitmapSource? this[string name] => Get(SpriteMap.Get(name));

    public BitmapSource? Get(Sprite s)
    {
        if (!_cache.TryGetValue(s, out var b)) _cache[s] = b = Skin.GetSprite(s) is { } img ? ToBitmap(img) : null;
        return b;
    }

    public static BitmapSource ToBitmap(SkinImage img)
    {
        // 0xAARRGGBB in little-endian memory is B, G, R, A: exactly Bgra32.
        var b = BitmapSource.Create(img.Width, img.Height, 96, 96, PixelFormats.Bgra32, null, img.Pixels, img.Width * 4);
        b.Freeze();
        return b;
    }

    public static Color ToColor(uint argb) => Color.FromArgb(255, (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
}

/// <summary>A push button drawn from a normal and a pressed sprite.</summary>
public sealed class SpriteButton : Button
{
    public static readonly DependencyProperty NormalProperty = DependencyProperty.Register(nameof(Normal), typeof(ImageSource), typeof(SpriteButton));
    public static readonly DependencyProperty PressedProperty = DependencyProperty.Register(nameof(Pressed), typeof(ImageSource), typeof(SpriteButton));
    public ImageSource? Normal { get => (ImageSource?)GetValue(NormalProperty); set => SetValue(NormalProperty, value); }
    public ImageSource? Pressed { get => (ImageSource?)GetValue(PressedProperty); set => SetValue(PressedProperty, value); }

    static SpriteButton() => DefaultStyleKeyProperty.OverrideMetadata(typeof(SpriteButton), new FrameworkPropertyMetadata(typeof(SpriteButton)));
}

/// <summary>A switch drawn from four sprites: off, off pressed, on, on pressed.</summary>
public sealed class SpriteToggle : ToggleButton
{
    public static readonly DependencyProperty OffProperty = DependencyProperty.Register(nameof(Off), typeof(ImageSource), typeof(SpriteToggle));
    public static readonly DependencyProperty OffPressedProperty = DependencyProperty.Register(nameof(OffPressed), typeof(ImageSource), typeof(SpriteToggle));
    public static readonly DependencyProperty OnProperty = DependencyProperty.Register(nameof(On), typeof(ImageSource), typeof(SpriteToggle));
    public static readonly DependencyProperty OnPressedProperty = DependencyProperty.Register(nameof(OnPressed), typeof(ImageSource), typeof(SpriteToggle));
    public ImageSource? Off { get => (ImageSource?)GetValue(OffProperty); set => SetValue(OffProperty, value); }
    public ImageSource? OffPressed { get => (ImageSource?)GetValue(OffPressedProperty); set => SetValue(OffPressedProperty, value); }
    public ImageSource? On { get => (ImageSource?)GetValue(OnProperty); set => SetValue(OnProperty, value); }
    public ImageSource? OnPressed { get => (ImageSource?)GetValue(OnPressedProperty); set => SetValue(OnPressedProperty, value); }

    static SpriteToggle() => DefaultStyleKeyProperty.OverrideMetadata(typeof(SpriteToggle), new FrameworkPropertyMetadata(typeof(SpriteToggle)));
}

/// <summary>
/// A horizontal slider drawn from sprites: the background follows the value (the volume and balance sheets hold 28
/// steps), the knob has a normal and a pressed sprite. A click on the bar jumps the knob there, as the classic player does.
/// </summary>
public sealed class SpriteSlider : Slider
{
    public static readonly DependencyProperty BackgroundImageProperty = DependencyProperty.Register(nameof(BackgroundImage), typeof(ImageSource), typeof(SpriteSlider));
    public static readonly DependencyProperty ThumbImageProperty = DependencyProperty.Register(nameof(ThumbImage), typeof(ImageSource), typeof(SpriteSlider));
    public static readonly DependencyProperty ThumbPressedImageProperty = DependencyProperty.Register(nameof(ThumbPressedImage), typeof(ImageSource), typeof(SpriteSlider));
    public static readonly DependencyProperty ThumbWidthProperty = DependencyProperty.Register(nameof(ThumbWidth), typeof(double), typeof(SpriteSlider), new PropertyMetadata(14.0));
    public static readonly DependencyProperty ThumbHeightProperty = DependencyProperty.Register(nameof(ThumbHeight), typeof(double), typeof(SpriteSlider), new PropertyMetadata(11.0));
    public static readonly DependencyProperty ThumbTopProperty = DependencyProperty.Register(nameof(ThumbTop), typeof(double), typeof(SpriteSlider), new PropertyMetadata(0.0));

    public ImageSource? BackgroundImage { get => (ImageSource?)GetValue(BackgroundImageProperty); set => SetValue(BackgroundImageProperty, value); }
    public ImageSource? ThumbImage { get => (ImageSource?)GetValue(ThumbImageProperty); set => SetValue(ThumbImageProperty, value); }
    public ImageSource? ThumbPressedImage { get => (ImageSource?)GetValue(ThumbPressedImageProperty); set => SetValue(ThumbPressedImageProperty, value); }
    public double ThumbWidth { get => (double)GetValue(ThumbWidthProperty); set => SetValue(ThumbWidthProperty, value); }
    public double ThumbHeight { get => (double)GetValue(ThumbHeightProperty); set => SetValue(ThumbHeightProperty, value); }
    /// <summary>Knob offset from the top of the bar (volume and balance knobs sit 1 px down).</summary>
    public double ThumbTop { get => (double)GetValue(ThumbTopProperty); set => SetValue(ThumbTopProperty, value); }

    /// <summary>Picks the background for a value (null = keep the one set).</summary>
    public Func<double, ImageSource?>? Backgrounds { get; set; }

    static SpriteSlider() => DefaultStyleKeyProperty.OverrideMetadata(typeof(SpriteSlider), new FrameworkPropertyMetadata(typeof(SpriteSlider)));

    public SpriteSlider()
    {
        IsMoveToPointEnabled = true;
        Focusable = true;
    }

    protected override void OnValueChanged(double oldValue, double newValue)
    {
        base.OnValueChanged(oldValue, newValue);
        RefreshBackground();
    }

    public void RefreshBackground()
    {
        if (Backgrounds is { } pick && pick(Value) is { } bg) BackgroundImage = bg;
    }
}

/// <summary>A number → a top margin (the slider knob's offset).</summary>
public sealed class TopMargin : System.Windows.Data.IValueConverter
{
    public static TopMargin Instance { get; } = new();
    public object Convert(object value, Type t, object p, System.Globalization.CultureInfo c) => new Thickness(0, value is double d ? d : 0, 0, 0);
    public object ConvertBack(object value, Type t, object p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Text in the skin's 5x6 font (TEXT.BMP). Scrolls when it does not fit and <see cref="Scrolls"/> is on.</summary>
public sealed class SpriteText : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(SpriteText), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((SpriteText)d)._offset = 0));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    private readonly SkinBitmaps _skin;
    private readonly Dictionary<char, BitmapSource?> _glyphs = [];
    private int _offset;
    private const string Separator = "  ***  ";

    public bool Scrolls { get; init; }

    public SpriteText(SkinBitmaps skin) => _skin = skin;

    protected override AutomationPeer OnCreateAutomationPeer() => new TextPeer(this, () => Text.Trim());

    /// <summary>One pixel to the left (called by the window's animation timer).</summary>
    public void Step()
    {
        if (!Scrolls || Text.Length * SkinFont.CharWidth <= ActualWidth) return;
        _offset++;
        InvalidateVisual();
    }

    private BitmapSource? Glyph(char c)
    {
        if (!_glyphs.TryGetValue(c, out var g)) _glyphs[c] = g = _skin.Get(SkinFont.Glyph(c));
        return g;
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));
        string text = Text;
        if (Scrolls && text.Length * SkinFont.CharWidth > ActualWidth)
        {
            text += Separator;
            int loop = text.Length * SkinFont.CharWidth;
            int start = -(_offset % loop);
            DrawRun(dc, text, start);
            DrawRun(dc, text, start + loop);
        }
        else DrawRun(dc, text, 0);
        dc.Pop();
    }

    private void DrawRun(DrawingContext dc, string text, int x)
    {
        foreach (char c in text)
        {
            if (x > ActualWidth) break;
            if (x + SkinFont.CharWidth > 0 && Glyph(c) is { } g) dc.DrawImage(g, new Rect(x, 0, SkinFont.CharWidth, SkinFont.CharHeight));
            x += SkinFont.CharWidth;
        }
    }
}

/// <summary>The clock from the skin's digit sheet: minus (time left), mm:ss. Blank when <see cref="Blank"/>.</summary>
public sealed class SpriteClock : FrameworkElement
{
    private readonly SkinBitmaps _skin;
    private TimeSpan _time;
    private bool _blank = true;

    public SpriteClock(SkinBitmaps skin)
    {
        _skin = skin;
        Width = ClassicLayout.DigitX[3] + 9 - ClassicLayout.Minus.X;
        Height = 13;
    }

    public string Text => _blank ? "" : $"{(_time < TimeSpan.Zero ? "-" : "")}{(int)Math.Abs(_time.TotalMinutes)}:{Math.Abs(_time.Seconds):00}";

    public void Show(TimeSpan time, bool blank)
    {
        if (time == _time && blank == _blank) return;
        _time = time; _blank = blank;
        InvalidateVisual();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new TextPeer(this, () => Text);

    protected override void OnRender(DrawingContext dc)
    {
        if (_blank) return; // the background shows through
        var (minus, digits) = ClassicLayout.Clock(_time);
        int ox = ClassicLayout.Minus.X;
        bool ex = _skin.Skin.DigitSheet == "NUMS_EX";
        if (ex)
        {
            if (_skin[minus ? "NumsEx.Minus" : "NumsEx.Blank"] is { } m) dc.DrawImage(m, new Rect(0, 0, 9, 13));
        }
        else if (minus && _skin["Numbers.Minus"] is { } m) dc.DrawImage(m, new Rect(3, 6, 5, 1)); // a dash in the minus cell
        for (int i = 0; i < 4; i++)
            if (_skin.Get(_skin.Skin.Digit(digits[i])) is { } d) dc.DrawImage(d, new Rect(ClassicLayout.DigitX[i] - ox, 0, 9, 13));
    }
}

/// <summary>
/// The classic EQ graph (113x19): the skin's graph background, the preamp line, and the curve through the 10 bands with
/// each pixel in the colour of its row (EQMAIN's 1x19 colour strip). Columns are joined so steep curves stay unbroken.
/// </summary>
public sealed class SpriteEqGraph : FrameworkElement
{
    private readonly SkinBitmaps _skin;
    private readonly Brush[] _rows = new Brush[ClassicLayout.EqGraphHeight];
    private int[] _curve = ClassicLayout.EqCurve(new double[10]);
    private int _preamp = ClassicLayout.EqGraphY(0);

    public SpriteEqGraph(SkinBitmaps skin)
    {
        _skin = skin;
        Width = ClassicLayout.EqGraphWidth; Height = ClassicLayout.EqGraphHeight;
        IsHitTestVisible = false;
        var strip = skin.Skin.GetSprite("Eq.Graph.Colors");
        for (int y = 0; y < _rows.Length; y++)
        {
            // A skin without the strip: fall back to the base skin's red-to-green idea.
            var c = strip is not null ? SkinBitmaps.ToColor(strip[0, y]) : Color.FromRgb((byte)(0xD3 - y * 9), (byte)(0x22 + y * 8), 0x20);
            var b = new SolidColorBrush(c); b.Freeze(); _rows[y] = b;
        }
    }

    public void Show(IReadOnlyList<double> bands, double preamp)
    {
        _curve = ClassicLayout.EqCurve(bands);
        _preamp = ClassicLayout.EqGraphY(preamp);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_skin["Eq.Graph.Background"] is { } bg) dc.DrawImage(bg, new Rect(0, 0, Width, Height));
        if (_skin["Eq.Graph.Preamp"] is { } line) dc.DrawImage(line, new Rect(0, _preamp, Width, 1));
        for (int x = 0; x < _curve.Length; x++)
        {
            int y = _curve[x], prev = x > 0 ? _curve[x - 1] : y;
            // From one row short of the previous column's to this one's: a steep step stays a joined line.
            int from = y == prev ? y : Math.Min(y, prev + Math.Sign(y - prev)), to = y == prev ? y : Math.Max(y, prev + Math.Sign(y - prev));
            for (int r = from; r <= to; r++) dc.DrawRectangle(_rows[r], null, new Rect(x, r, 1, 1));
        }
    }
}

/// <summary>
/// The classic spectrum: 19 bars of 3 px with a 1 px gap in 76x16, coloured per row from VISCOLOR.TXT (2 = top, 17 = bottom),
/// peak caps in colour 23, drawn over the player background. <see cref="Small"/>: the windowshade strip's 38x5, bars of
/// 1 px, the 16 colours spread over 5 rows, no peak caps.
/// </summary>
public sealed class SpriteSpectrum : FrameworkElement
{
    private readonly float[] _levels = new float[ClassicLayout.SpectrumBars];
    private readonly float[] _peaks = new float[ClassicLayout.SpectrumBars];
    private readonly Brush[] _rows;
    private readonly Brush _peak;
    private readonly int _height, _barWidth;
    private readonly bool _small;

    public SpriteSpectrum(ClassicSkin skin, bool small = false)
    {
        _small = small;
        _height = small ? ClassicLayout.ShadeSpectrumHeight : ClassicLayout.SpectrumHeight;
        _barWidth = small ? 1 : 3;
        Width = small ? ClassicLayout.ShadeSpectrumWidth : ClassicLayout.SpectrumWidth; Height = _height;
        _rows = new Brush[_height];
        for (int y = 0; y < _height; y++)
            _rows[y] = Frozen(SkinBitmaps.ToColor(skin.VisColors[2 + (int)Math.Round(y * 15.0 / (_height - 1))]));
        _peak = Frozen(SkinBitmaps.ToColor(skin.VisColors[23]));
        IsHitTestVisible = false;
    }

    public static SpriteSpectrum Small(ClassicSkin skin) => new(skin, small: true);

    public void Push(float[] levels)
    {
        for (int i = 0; i < _levels.Length && i < levels.Length; i++)
        {
            _levels[i] = Math.Max(levels[i], _levels[i] - 0.08f);
            _peaks[i] = Math.Max(_levels[i], _peaks[i] - 0.015f);
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        int step = _barWidth + 1, hMax = _height;
        for (int i = 0; i < _levels.Length; i++)
        {
            int x = i * step, h = (int)Math.Round(_levels[i] * hMax);
            for (int y = hMax - h; y < hMax; y++) dc.DrawRectangle(_rows[y], null, new Rect(x, y, _barWidth, 1));
            if (_small) continue;
            int p = (int)Math.Round(_peaks[i] * hMax);
            if (p > 0) dc.DrawRectangle(_peak, null, new Rect(x, Math.Max(0, hMax - p - 1), _barWidth, 1));
        }
    }

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
}
