using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using Fernplay.App.Classic;
using Fernplay.Core.Audio;
using Fernplay.Core.Skins;
using L = Fernplay.Core.Skins.ClassicLayout;

namespace Fernplay.App;

/// <summary>
/// The equalizer drawn from a classic skin (EQMAIN.BMP). Its sliders and ON switch are bound to Fernplay's own (hidden)
/// controls, so the same handlers change the sound, and presets and saved settings work as before.
/// </summary>
public partial class EqWindow
{
    private sealed class ClassicView
    {
        public required SkinBitmaps Bitmaps;
        public required Canvas Root;
        public required Image TitleBar;
        public required SpriteEqGraph Graph;
    }

    private ClassicView? _classic;
    private int _skinSize = 2;

    /// <summary>Switches to a classic skin, or back to Fernplay's look (null). Panels docked below move with the new height.</summary>
    public void ApplySkin(ClassicSkin? skin, int size)
    {
        _skinSize = Math.Clamp(size, 1, 3);
        ChangeLook(() =>
        {
            if (skin is null)
            {
                _classic = null;
                ClassicHost.Content = null;
                ClassicHost.Visibility = Visibility.Collapsed;
                FernplayView.Visibility = Visibility.Visible;
            }
            else
            {
                _classic = BuildClassic(skin);
                ClassicHost.Content = _classic.Root;
                ClassicHost.Visibility = Visibility.Visible;
                FernplayView.Visibility = Visibility.Collapsed;
                ApplyClassicScale();
                ClassicSync();
            }
        });
    }

    private void ApplyClassicScale()
    {
        if (_classic is null) return;
        double s = ClassicScale(_skinSize);
        _classic.Root.LayoutTransform = new ScaleTransform(s, s);
    }

    private ClassicView BuildClassic(ClassicSkin skin)
    {
        var bm = new SkinBitmaps(skin);
        var root = new Canvas { Width = L.Width, Height = L.Height, ClipToBounds = true, Background = Brushes.Black, SnapsToDevicePixels = true };
        RenderOptions.SetBitmapScalingMode(root, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetEdgeMode(root, EdgeMode.Aliased);

        T Place<T>(T e, (int X, int Y) at) where T : UIElement
        {
            Canvas.SetLeft(e, at.X); Canvas.SetTop(e, at.Y);
            root.Children.Add(e);
            return e;
        }
        Image Img(string sprite, (int X, int Y) at, string? tip = null)
        {
            var s = SpriteMap.Get(sprite);
            var img = new Image { Source = bm[sprite], Width = s.W, Height = s.H, Stretch = Stretch.Fill };
            if (tip is not null) img.ToolTip = tip;
            return Place(img, at);
        }
        SpriteButton Btn(string sprite, (int X, int Y) at, string name, string tip, RoutedEventHandler click)
        {
            var s = SpriteMap.Get(sprite);
            var b = new SpriteButton { Normal = bm[sprite], Pressed = bm[sprite + ".Pressed"], Width = s.W, Height = s.H, ToolTip = tip };
            AutomationProperties.SetName(b, name);
            b.Click += click;
            return Place(b, at);
        }
        // A band or the preamp: same value, tooltip and name as Fernplay's slider, so its handler sets the EQ.
        SpriteSlider Band(Slider mirror, (int X, int Y) at)
        {
            var s = Place(new SpriteSlider
            {
                Style = (Style)FindResource("ClassicEqSlider"),
                Width = L.EqSliderWidth, Height = L.EqSliderHeight,
                Minimum = -Equalizer.MaxDb, Maximum = Equalizer.MaxDb, SmallChange = 1, LargeChange = 3, ThumbWidth = 11, ThumbHeight = 11,
                ThumbImage = bm["Eq.Thumb"], ThumbPressedImage = bm["Eq.Thumb.Pressed"],
                Backgrounds = v => bm.Get(SpriteMap.EqSliderBackground(L.EqSliderStep(v))),
            }, at);
            AutomationProperties.SetName(s, AutomationProperties.GetName(mirror));
            s.SetBinding(RangeBase.ValueProperty, new Binding(nameof(Slider.Value)) { Source = mirror, Mode = BindingMode.TwoWay });
            s.SetBinding(FrameworkElement.ToolTipProperty, new Binding(nameof(ToolTip)) { Source = mirror });
            s.RefreshBackground();
            SliderReset.OnDoubleClick(s, 0); // sets the bound Fernplay slider too
            return s;
        }

        Img("Eq.Background", (0, 0));

        // Title bar: drag handle with the close button on it (the skin's shade button is part of the title sprite).
        var titleBar = new Grid { Width = L.Width, Height = 14, Background = Brushes.Transparent };
        var titleImg = new Image { Source = bm[IsActive ? "Eq.Title.Active" : "Eq.Title.Inactive"], Width = L.Width, Height = 14 };
        titleBar.Children.Add(titleImg);
        Place(titleBar, (0, 0));
        AttachDragHandle(titleBar);
        Btn("Eq.Close", L.EqClose, "Hide the equalizer", "Hide the equalizer", Close_Click);

        // ON: the same switch as Fernplay's (its Checked/Unchecked handler turns the EQ on or off).
        var on = Place(new SpriteToggle
        {
            Off = bm["Eq.On"], OffPressed = bm["Eq.On.Pressed"], On = bm["Eq.On.Active"], OnPressed = bm["Eq.On.Active.Pressed"],
            Width = 26, Height = 12, ToolTip = "Turn the equalizer on or off",
        }, L.EqOn);
        AutomationProperties.SetName(on, "Equalizer on");
        on.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(ToggleButton.IsChecked)) { Source = OnToggle, Mode = BindingMode.TwoWay });
        Img("Eq.Auto", L.EqAuto, "Auto EQ per track: not in Fernplay yet");

        var graph = Place(new SpriteEqGraph(bm), L.EqGraph);

        var presets = Btn("Eq.Presets", L.EqPresets, "Presets", "Choose a preset", (s, _) =>
        {
            PresetsMenu.PlacementTarget = (UIElement)s;
            PresetsMenu.Placement = PlacementMode.Bottom;
            PresetsMenu.IsOpen = true;
        });

        Band(PreampSlider, L.EqPreamp);
        for (int i = 0; i < 10; i++) Band(_bands[i], (L.EqBandX[i], L.EqSliderY));

        return new ClassicView { Bitmaps = bm, Root = root, TitleBar = titleImg, Graph = graph };
    }

    /// <summary>Redraws the skinned graph from the EQ (called from <see cref="Sync"/>).</summary>
    private void ClassicSync()
    {
        if (_classic is { } v) v.Graph.Show(_eq.Bands, _eq.Preamp);
    }

    private void ClassicActivation(bool active)
    {
        if (_classic is { } v) v.TitleBar.Source = v.Bitmaps[active ? "Eq.Title.Active" : "Eq.Title.Inactive"];
    }
}
