using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fernplay.Core.Skins;

namespace Fernplay.App.Classic;

/// <summary>
/// The frame of a classic "general" window (the library) drawn from GEN.BMP: title bar with the title in the skin's
/// letters, sides, bottom corners, and the close button while pressed. Drawn in skin pixels at a whole-number scale
/// (<see cref="SkinScale"/> screen DIPs per skin pixel), so the pixel art stays sharp; the window keeps its size a
/// multiple of that scale.
/// </summary>
public sealed class GenFrame : FrameworkElement
{
    private readonly Dictionary<(string Name, int W, int H), BitmapSource?> _cut = [];
    private GenSkin? _gen;
    private SkinBitmaps? _bitmaps;

    public GenFrame()
    {
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        SnapsToDevicePixels = true;
        IsHitTestVisible = false; // the window's own transparent handles take the mouse
    }

    public GenSkin? Gen
    {
        get => _gen;
        set { _gen = value; _bitmaps = value is null ? null : new SkinBitmaps(value.Skin); _cut.Clear(); _composed = null; InvalidateVisual(); }
    }

    private double _scale = 1;
    public double SkinScale { get => _scale; set { _scale = value; InvalidateVisual(); } }

    private string _title = "";
    public string Title { get => _title; set { _title = value; InvalidateVisual(); } }

    private bool _active = true;
    public bool IsActiveLook { get => _active; set { if (_active != value) { _active = value; InvalidateVisual(); } } }

    private bool _closePressed;
    public bool ClosePressed { get => _closePressed; set { if (_closePressed != value) { _closePressed = value; InvalidateVisual(); } } }

    /// <summary>A sprite, or its top-left w x h part (the last tile in a row or column is cut, not squeezed).</summary>
    private BitmapSource? Part(string name, int w, int h)
    {
        if (_bitmaps is null) return null;
        if (_cut.TryGetValue((name, w, h), out var b)) return b;
        var full = _bitmaps[name];
        if (full is not null && (w < full.PixelWidth || h < full.PixelHeight))
        {
            var crop = new CroppedBitmap(full, new Int32Rect(0, 0, Math.Min(w, full.PixelWidth), Math.Min(h, full.PixelHeight)));
            crop.Freeze();
            full = crop;
        }
        return _cut[(name, w, h)] = full;
    }

    private BitmapSource? Letter(int sheetX, int w)
    {
        if (_gen is null) return null;
        int y = _active ? GenSkin.ActiveLettersY : GenSkin.InactiveLettersY;
        var key = ($"letter@{y}", sheetX, w);
        if (_cut.TryGetValue(key, out var b)) return b;
        var img = _gen.Skin.Sheets["GEN"].Crop(sheetX, y, w, GenSkin.LetterHeight);
        return _cut[key] = img is null ? null : SkinBitmaps.ToBitmap(img);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_gen is null || _scale <= 0) return;
        int w = (int)Math.Round(ActualWidth / _scale), h = (int)Math.Round(ActualHeight / _scale);
        if (w <= 0 || h <= 0) return;
        // The frame is put together at the skin's own pixels and scaled once, as one image. Scaling each 25 px tile on
        // its own at 1.5x put tiles on half pixels, and some drew a 1 px line from the row next to it: light dashes along
        // Bento's title bar (owner, 2026-11-13).
        var key = (w, h, _active, _closePressed, _title);
        if (_composed is null || _composedKey != key)
        {
            var visual = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
            using (var frame = visual.RenderOpen()) DrawFrame(frame, w, h);
            var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            (_composed, _composedKey) = (bitmap, key);
        }
        dc.DrawImage(_composed, new Rect(0, 0, w * _scale, h * _scale));
    }

    private RenderTargetBitmap? _composed;
    private (int, int, bool, bool, string) _composedKey;

    /// <summary>The whole frame at the skin's own pixels (1 unit = 1 skin pixel).</summary>
    private void DrawFrame(DrawingContext dc, int w, int h)
    {
        if (_gen is null) return;
        string state = _active ? "Active" : "Inactive";

        void Draw(string sprite, int x, int y, int pw, int ph)
        {
            if (pw <= 0 || ph <= 0) return;
            if (Part(sprite, pw, ph) is { } bmp) dc.DrawImage(bmp, new Rect(x, y, bmp.PixelWidth, bmp.PixelHeight));
        }

        // Sides first (the corners and bars overlap them): repeated middle pieces, the "bottom" piece at their foot.
        int sideTop = GenSkin.TitleHeight, sideBottom = h - GenSkin.BottomHeight;
        int footTop = Math.Max(sideTop, sideBottom - 24);
        for (int y = sideTop; y < footTop; y += 29)
        {
            Draw("Gen.MiddleLeft", 0, y, GenSkin.LeftWidth, Math.Min(29, footTop - y));
            Draw("Gen.MiddleRight", w - GenSkin.RightWidth, y, GenSkin.RightWidth, Math.Min(29, footTop - y));
        }
        Draw("Gen.MiddleLeftBottom", 0, footTop, GenSkin.LeftWidth, sideBottom - footTop);
        Draw("Gen.MiddleRightBottom", w - GenSkin.RightWidth, footTop, GenSkin.RightWidth, sideBottom - footTop);

        // Bottom: left corner, fill, right corner (the right one wins where a narrow window makes them meet).
        for (int x = 125; x < w - 125; x += 25) Draw("Gen.BottomFill", x, sideBottom, Math.Min(25, w - 125 - x), GenSkin.BottomHeight);
        Draw("Gen.BottomLeft", 0, sideBottom, Math.Min(125, w), GenSkin.BottomHeight);
        if (w > 125) Draw("Gen.BottomRight", Math.Max(0, w - 125), sideBottom, 125, GenSkin.BottomHeight);

        // Title bar and title.
        int textWidth = _gen.TextWidth(_title);
        var (pieces, textX) = GenSkin.TitleRow(w, textWidth);
        foreach (var (sprite, x, pw) in pieces) Draw($"Gen.{sprite}.{state}", x, 0, pw, GenSkin.TitleHeight);
        if (textX is { } tx)
            foreach (var (lx, sheetX, lw) in _gen.TextLayout(_title))
                if (Letter(sheetX, lw) is { } bmp) dc.DrawImage(bmp, new Rect(tx + lx, GenSkin.LettersTop, lw, GenSkin.LetterHeight));

        if (_closePressed) Draw("Gen.Close.Pressed", w - GenSkin.CloseRight, GenSkin.CloseTop, GenSkin.CloseSize, GenSkin.CloseSize);
    }
}
