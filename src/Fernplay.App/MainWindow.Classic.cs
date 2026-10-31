using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fernplay.App.Classic;
using Fernplay.Core;
using Fernplay.Core.Skins;
using L = Fernplay.Core.Skins.ClassicLayout;

namespace Fernplay.App;

/// <summary>
/// The player drawn from a classic skin (.wsz). Its controls drive the same handlers and the same (hidden) controls as
/// Fernplay's own look, so playback, settings, keys and the media session behave the same in both.
/// </summary>
public partial class MainWindow
{
    /// <summary>The elements of the classic view that change while playing.</summary>
    private sealed class ClassicView
    {
        public required SkinBitmaps Bitmaps;
        public required Canvas Root;
        public required Image TitleBar, State, Light, Mono, Stereo;
        public required SpriteClock Clock;
        public required SpriteSpectrum Spectrum;
        public required SpriteText Title, Kbps, Khz;
        public required SpriteSlider Balance;
        public required SpriteButton ShadeButton;
        public required ShadeView Shade;
    }

    /// <summary>The player rolled up to its 14 px strip (windowshade).</summary>
    private sealed class ShadeView
    {
        public required Canvas Root;
        public required Image Bar;
        public required SpriteText Sign, Minutes, Seconds;
        public required SpriteSpectrum Spectrum;
        public required SpriteButton Unshade;
    }

    private ClassicView? _classic;
    private int _skinSize = 2;
    private bool _shaded; // remembered across starts and changes of look

    /// <summary>True while the player is rolled up to its strip (windowshade), in either look.</summary>
    public bool IsShaded => _shaded;

    /// <summary>Just before the player rolls up or down, while every panel is still where it was.</summary>
    public event Action? ShadeChanging;

    /// <summary>After the player rolled up or down (panels docked below have moved with it).</summary>
    public event Action? ShadeChanged;

    /// <summary>Rolls the player up to its strip or down again: the classic skin's strip, or Fernplay's own.</summary>
    public void SetShaded(bool shaded)
    {
        if (shaded == _shaded) return;
        UIElement rollUp = _classic is { } v ? v.ShadeButton : ShadeButton, rollDown = _classic is { } w ? w.Shade.Unshade : UnshadeButton;
        bool hadFocus = rollUp.IsKeyboardFocused || rollDown.IsKeyboardFocused;
        ShadeChanging?.Invoke();
        _shaded = shaded;
        _settings.PlayerShaded = shaded;
        ChangeLook(ShowShadeState);
        UpdateClock();
        ShadeChanged?.Invoke();
        // Keyboard users keep their place: the focus moves to the button that rolls it back.
        if (hadFocus)
        {
            var target = shaded ? rollDown : rollUp;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => target.Focus());
        }
    }

    /// <summary>Shows the view for the current look (classic or Fernplay's) and windowshade state.</summary>
    private void ShowShadeState()
    {
        bool classic = _classic is not null;
        ClassicHost.Visibility = classic ? Visibility.Visible : Visibility.Collapsed;
        if (_classic is { } v) ClassicHost.Content = _shaded ? v.Shade.Root : v.Root;
        FernplayView.Visibility = !classic && !_shaded ? Visibility.Visible : Visibility.Collapsed;
        FernplayShade.Visibility = !classic && _shaded ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The classic skin in use, or null for Fernplay's own look.</summary>
    public ClassicSkin? Skin { get; private set; }

    public static SkinLibrary Skins { get; } = new(UserSettings.SkinsFolder);

    /// <summary>
    /// Switches the player and the equalizer to a classic skin, or back to Fernplay's look (null). Panels docked below
    /// move with the new heights.
    /// </summary>
    public void ApplySkin(ClassicSkin? skin, int size)
    {
        SkinApplying?.Invoke();
        _skinSize = Math.Clamp(size, 1, 3);
        Skin = skin;
        ChangeLook(() =>
        {
            if (skin is null)
            {
                _classic = null;
                ClassicHost.Content = null;
                ShowShadeState();
            }
            else
            {
                _classic = BuildClassic(skin);
                ShowShadeState();
                ApplyClassicScale();
            }
            ShowNowPlaying();
            UpdateState();
        });
        (_eq as EqWindow)?.ApplySkin(skin, _skinSize);
        (_playlist as PlaylistWindow)?.ApplySkin(skin, _skinSize);
        SkinApplied?.Invoke(skin, _skinSize);
    }

    /// <summary>After every change of skin or size (the library follows).</summary>
    public event Action<ClassicSkin?, int>? SkinApplied;

    /// <summary>Just before a change of skin or size, while every panel is still where it was.</summary>
    public event Action? SkinApplying;

    /// <summary>Applies a skin file: checks it, copies it into the skins folder, switches to it, remembers it.</summary>
    public bool ApplySkinFile(string path)
    {
        try
        {
            var inFolder = Skins.Import(path);
            ApplySkin(ClassicSkin.Load(inFolder), _settings.SkinSize);
            _settings.Skin = System.IO.Path.GetFileName(inFolder);
            return true;
        }
        catch (SkinLoadException ex)
        {
            _c.ShowNotice($"Skin not used: {ex.Message}");
            return false;
        }
    }

    public void UseFernplayLook()
    {
        ApplySkin(null, _settings.SkinSize);
        _settings.Skin = null;
    }

    public void SetSkinSize(int size)
    {
        _settings.SkinSize = Math.Clamp(size, 1, 3);
        if (Skin is not null) ApplySkin(Skin, _settings.SkinSize);
    }

    /// <summary>Whole screen pixels per skin pixel, so the pixel art stays sharp at any Windows scaling.</summary>
    private void ApplyClassicScale()
    {
        if (_classic is null) return;
        double s = ClassicScale(_skinSize);
        _classic.Root.LayoutTransform = new ScaleTransform(s, s);
        _classic.Shade.Root.LayoutTransform = new ScaleTransform(s, s);
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
        SpriteButton Btn(string sprite, (int X, int Y) at, string name, string tip, RoutedEventHandler click, string? key = null)
        {
            var s = SpriteMap.Get(sprite);
            var b = new SpriteButton { Normal = bm[sprite], Pressed = bm[sprite + ".Pressed"], Width = s.W, Height = s.H, ToolTip = tip };
            AutomationProperties.SetName(b, name);
            if (key is not null) AutomationProperties.SetAcceleratorKey(b, key);
            b.Click += click;
            return Place(b, at);
        }
        SpriteToggle Tog(string sprite, (int X, int Y) at, string name, string tip, ToggleButton mirror)
        {
            var s = SpriteMap.Get(sprite);
            var t = new SpriteToggle { Off = bm[sprite], OffPressed = bm[sprite + ".Pressed"], On = bm[sprite + ".On"], OnPressed = bm[sprite + ".On.Pressed"], Width = s.W, Height = s.H, ToolTip = tip };
            AutomationProperties.SetName(t, name);
            // Same state as Fernplay's own switch: its Checked/Unchecked handlers do the work.
            t.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(ToggleButton.IsChecked)) { Source = mirror, Mode = BindingMode.TwoWay });
            return Place(t, at);
        }

        Img("Main.Background", (0, 0));

        // Title bar: drag handle with the window buttons on it.
        var titleBar = new Grid { Width = L.Width, Height = 14, Background = Brushes.Transparent };
        var titleImg = new Image { Source = bm["Title.Active"], Width = L.Width, Height = 14 };
        titleBar.Children.Add(titleImg);
        Place(titleBar, L.TitleBar);
        AttachDragHandle(titleBar);
        titleBar.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount != 2) return;
            SetShaded(true); // double-click on the title bar rolls the player up, as on the classic player
            e.Handled = true;
        };
        Btn("Title.Options", L.Options, "Fernplay menu", "Settings and about", (_, _) => OpenClassicMenu(root));
        Btn("Title.Minimize", L.Minimize, "Minimise", "Minimise", Minimize_Click);
        var shadeButton = Btn("Title.Shade", L.Shade, "Windowshade", "Roll up the player (Ctrl+W, or double-click the title bar)", (_, _) => SetShaded(true), "Ctrl+W");
        Btn("Title.Close", L.Close, "Close Fernplay", "Close Fernplay", Close_Click);
        Img("Title.Clutter", L.Clutter, "Not in Fernplay yet");

        // Display.
        var state = Img("State.Stopped", L.State);
        var light = Img("State.Ok", L.StateLight);
        var clock = Place(new SpriteClock(bm) { IsHitTestVisible = false }, L.Minus);
        var clockButton = Btn("Title.Options", L.Minus, "Time: switch between played and left", "Switch between time played and time left", Clock_Click);
        clockButton.Normal = clockButton.Pressed = null; // an invisible button over the digits
        clockButton.Width = clock.Width; clockButton.Height = clock.Height;
        var spectrum = Place(new SpriteSpectrum(skin), L.Spectrum);
        var title = Place(new SpriteText(bm) { Scrolls = true, Width = L.TitleWidth, Height = 6 }, L.Title);
        var kbps = Place(new SpriteText(bm) { Width = L.KbpsChars * SkinFont.CharWidth, Height = 6 }, L.Kbps);
        var khz = Place(new SpriteText(bm) { Width = L.KhzChars * SkinFont.CharWidth, Height = 6 }, L.Khz);
        var mono = Img("Mono.Off", L.Mono);
        var stereo = Img("Stereo.Off", L.Stereo);

        // Volume: the same value as Fernplay's volume slider.
        var volume = Place(new SpriteSlider
        {
            Width = 68, Height = 13, Minimum = 0, Maximum = 100, SmallChange = 5, LargeChange = 10, ThumbWidth = 14, ThumbHeight = 11, ThumbTop = 1,
            ThumbImage = bm["Volume.Thumb"], ThumbPressedImage = bm["Volume.Thumb.Pressed"],
            Backgrounds = v => bm.Get(SpriteMap.VolumeBackground(L.VolumeStep(v / 100))),
        }, L.Volume);
        AutomationProperties.SetName(volume, "Volume");
        volume.SetBinding(RangeBase.ValueProperty, new Binding(nameof(Slider.Value)) { Source = VolumeSlider, Mode = BindingMode.TwoWay });
        volume.SetBinding(FrameworkElement.ToolTipProperty, new Binding(nameof(ToolTip)) { Source = VolumeSlider });
        volume.PreviewMouseWheel += Volume_MouseWheel;
        volume.RefreshBackground();

        // Balance: the same value as Fernplay's balance slider, whose handlers snap it and set the sound.
        var balance = Place(new SpriteSlider
        {
            Width = 38, Height = 13, Minimum = -100, Maximum = 100, SmallChange = 10, LargeChange = 20, ThumbWidth = 14, ThumbHeight = 11, ThumbTop = 1,
            ThumbImage = bm["Balance.Thumb"], ThumbPressedImage = bm["Balance.Thumb.Pressed"],
            Backgrounds = v => bm.Get(SpriteMap.BalanceBackground(L.BalanceStep(v / 100))),
        }, L.Balance);
        AutomationProperties.SetName(balance, "Balance");
        balance.SetBinding(RangeBase.ValueProperty, new Binding(nameof(Slider.Value)) { Source = BalanceSlider, Mode = BindingMode.TwoWay });
        balance.SetBinding(FrameworkElement.ToolTipProperty, new Binding(nameof(ToolTip)) { Source = BalanceSlider });
        balance.PreviewMouseWheel += Balance_MouseWheel;
        balance.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(Balance_DragStarted));
        balance.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(Balance_DragCompleted));
        balance.RefreshBackground();

        Tog("EqSwitch", L.EqSwitch, "Equalizer panel", "Show or hide the equalizer", EqToggle);
        Tog("PlSwitch", L.PlSwitch, "Playlist panel", "Show or hide the playlist", PlaylistToggle);

        // Position: the same value, length and on/off as Fernplay's seek bar, so its handlers seek.
        var position = Place(new SpriteSlider
        {
            Width = 248, Height = 10, Minimum = 0, SmallChange = 5, LargeChange = 30, ThumbWidth = 29, ThumbHeight = 10,
            BackgroundImage = bm["Position.Background"], ThumbImage = bm["Position.Thumb"], ThumbPressedImage = bm["Position.Thumb.Pressed"],
        }, L.Position);
        AutomationProperties.SetName(position, "Position in the track");
        position.SetBinding(RangeBase.MaximumProperty, new Binding(nameof(Slider.Maximum)) { Source = SeekSlider });
        position.SetBinding(RangeBase.ValueProperty, new Binding(nameof(Slider.Value)) { Source = SeekSlider, Mode = BindingMode.TwoWay });
        position.SetBinding(IsEnabledProperty, new Binding(nameof(IsEnabled)) { Source = SeekSlider });
        position.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(Seek_DragStarted));
        position.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(Seek_DragCompleted));

        // Transport, eject, shuffle, repeat.
        Btn("Buttons.Previous", L.Previous, "Previous track", "Previous track (Z)", Prev_Click, "Z");
        Btn("Buttons.Play", L.Play, "Play", "Play (X; Space plays or pauses)", Play_Click, "X");
        Btn("Buttons.Pause", L.Pause, "Pause", "Pause, again to resume (C)", Pause_Click, "C");
        Btn("Buttons.Stop", L.Stop, "Stop", "Stop (V)", Stop_Click, "V");
        Btn("Buttons.Next", L.Next, "Next track", "Next track (B)", Next_Click, "B");
        var eject = Btn("Buttons.Eject", L.Eject, "Open music files", "Open music files (Ctrl+O); right-click for a folder", Open_Click);
        var ejectMenu = new ContextMenu();
        var files = new MenuItem { Header = "Open files…" }; files.Click += Open_Click;
        var folder = new MenuItem { Header = "Open folder…" }; folder.Click += OpenFolder_Click;
        ejectMenu.Items.Add(files); ejectMenu.Items.Add(folder);
        eject.ContextMenu = ejectMenu;
        Tog("Shuffle", L.Shuffle, "Shuffle", "Play the list in a random order", ShuffleToggle);
        var repeat = Tog("Repeat", L.Repeat, "Repeat", "Repeat: off, whole list, this track", RepeatToggle);
        repeat.SetBinding(FrameworkElement.ToolTipProperty, new Binding { Path = new PropertyPath(AutomationProperties.HelpTextProperty), Source = RepeatToggle, StringFormat = "Repeat: {0} (click for off, whole list, this track)" });

        // The logo opens About, as on the classic player.
        var about = Place(new SpriteButton { Width = 13, Height = 15, ToolTip = "About Fernplay" }, L.About);
        AutomationProperties.SetName(about, "About Fernplay");
        about.Click += About_Click;

        return new ClassicView
        {
            Bitmaps = bm, Root = root, TitleBar = titleImg, State = state, Light = light, Mono = mono, Stereo = stereo,
            Clock = clock, Spectrum = spectrum, Title = title, Kbps = kbps, Khz = khz, Balance = balance,
            ShadeButton = shadeButton, Shade = BuildShade(skin, bm),
        };
    }

    private void OpenClassicMenu(UIElement root)
    {
        AppMenu.PlacementTarget = root;
        AppMenu.Placement = PlacementMode.Relative;
        AppMenu.HorizontalOffset = L.Options.X;
        AppMenu.VerticalOffset = L.Options.Y + 9;
        AppMenu.IsOpen = true;
    }

    /// <summary>
    /// The windowshade strip (275x14): the skin's shade bar with the window buttons, mini spectrum, time, mini transport
    /// over the buttons drawn in the bar, and the mini position bar. The whole strip drags the player; a double-click on it
    /// rolls the player down again.
    /// </summary>
    private ShadeView BuildShade(ClassicSkin skin, SkinBitmaps bm)
    {
        var root = new Canvas { Width = L.Width, Height = L.ShadeHeight, ClipToBounds = true, Background = Brushes.Black, SnapsToDevicePixels = true };
        RenderOptions.SetBitmapScalingMode(root, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetEdgeMode(root, EdgeMode.Aliased);

        T Place<T>(T e, (int X, int Y) at) where T : UIElement
        {
            Canvas.SetLeft(e, at.X); Canvas.SetTop(e, at.Y);
            root.Children.Add(e);
            return e;
        }
        SpriteButton Btn(string? sprite, (int X, int Y) at, (int W, int H) size, string name, string tip, RoutedEventHandler click, string? key = null)
        {
            var b = new SpriteButton { Width = size.W, Height = size.H, ToolTip = tip };
            if (sprite is not null) { b.Normal = bm[sprite]; b.Pressed = bm[sprite + ".Pressed"]; } // else invisible, over art in the bar
            AutomationProperties.SetName(b, name);
            if (key is not null) AutomationProperties.SetAcceleratorKey(b, key);
            b.Click += click;
            return Place(b, at);
        }

        var bar = new Grid { Width = L.Width, Height = L.ShadeHeight, Background = Brushes.Transparent };
        var barImg = new Image { Source = bm["Title.Shade.Active"], Width = L.Width, Height = L.ShadeHeight };
        bar.Children.Add(barImg);
        Place(bar, (0, 0));
        AttachDragHandle(bar);
        bar.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount != 2) return;
            SetShaded(false);
            e.Handled = true;
        };

        Btn("Title.Options", L.Options, (9, 9), "Fernplay menu", "Settings and about", (_, _) => OpenClassicMenu(root));
        Btn("Title.Minimize", L.Minimize, (9, 9), "Minimise", "Minimise", Minimize_Click);
        var unshade = Btn("Title.Unshade", L.Shade, (9, 9), "Windowshade off", "Roll down the player (Ctrl+W, or double-click the bar)", (_, _) => SetShaded(false), "Ctrl+W");
        Btn("Title.Close", L.Close, (9, 9), "Close Fernplay", "Close Fernplay", Close_Click);

        var spectrum = Place(SpriteSpectrum.Small(skin), L.ShadeSpectrum);
        var sign = Place(new SpriteText(bm) { Width = SkinFont.CharWidth, Height = SkinFont.CharHeight, IsHitTestVisible = false }, L.ShadeSign);
        var minutes = Place(new SpriteText(bm) { Width = 2 * SkinFont.CharWidth, Height = SkinFont.CharHeight, IsHitTestVisible = false }, L.ShadeMinutes);
        var seconds = Place(new SpriteText(bm) { Width = 2 * SkinFont.CharWidth, Height = SkinFont.CharHeight, IsHitTestVisible = false }, L.ShadeSeconds);
        Btn(null, L.ShadeSign, (L.ShadeSeconds.X + 2 * SkinFont.CharWidth - L.ShadeSign.X, SkinFont.CharHeight),
            "Time: switch between played and left", "Switch between time played and time left", Clock_Click);

        (string Name, string Tip, RoutedEventHandler Click, string? Key)[] transport =
        [
            ("Previous track", "Previous track (Z)", Prev_Click, "Z"),
            ("Play", "Play (X; Space plays or pauses)", Play_Click, "X"),
            ("Pause", "Pause, again to resume (C)", Pause_Click, "C"),
            ("Stop", "Stop (V)", Stop_Click, "V"),
            ("Next track", "Next track (B)", Next_Click, "B"),
            ("Open music files", "Open music files (Ctrl+O)", Open_Click, null),
        ];
        for (int i = 0; i < transport.Length; i++)
        {
            var (x, w) = L.ShadeButtons[i];
            var t = transport[i];
            Btn(null, (x, L.ShadeButtonY), (w, L.ShadeButtonHeight), t.Name, t.Tip, t.Click, t.Key);
        }

        // Mini position bar: the same value, length and on/off as Fernplay's seek bar, so its handlers seek.
        var position = Place(new SpriteSlider
        {
            Width = L.ShadePositionWidth, Height = L.ShadePositionHeight, Minimum = 0, SmallChange = 5, LargeChange = 30,
            ThumbWidth = L.ShadeThumbWidth, ThumbHeight = L.ShadePositionHeight,
            BackgroundImage = bm["Shade.Position.Background"], ThumbImage = bm["Shade.Position.Thumb.Left"], ThumbPressedImage = bm["Shade.Position.Thumb.Left"],
        }, L.ShadePosition);
        AutomationProperties.SetName(position, "Position in the track");
        position.SetBinding(RangeBase.MaximumProperty, new Binding(nameof(Slider.Maximum)) { Source = SeekSlider });
        position.SetBinding(RangeBase.ValueProperty, new Binding(nameof(Slider.Value)) { Source = SeekSlider, Mode = BindingMode.TwoWay });
        position.SetBinding(IsEnabledProperty, new Binding(nameof(IsEnabled)) { Source = SeekSlider });
        position.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(Seek_DragStarted));
        position.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(Seek_DragCompleted));
        position.ValueChanged += (_, _) =>
        {
            var knob = bm[L.ShadeThumb(position.Maximum > 0 ? position.Value / position.Maximum : 0)];
            position.ThumbImage = knob;
            position.ThumbPressedImage = knob;
        };

        return new ShadeView { Root = root, Bar = barImg, Sign = sign, Minutes = minutes, Seconds = seconds, Spectrum = spectrum, Unshade = unshade };
    }

    private bool _balanceDragging;

    // Balance lives on Fernplay's BalanceSlider; the classic skin's slider is bound to it and shares these handlers.
    private void Balance_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return; // the constructor setting the saved value
        // A click, key or wheel step settles at once (snaps, or steps out of the centre zone); while dragging the knob
        // follows the mouse and only the sound snaps.
        if (!_balanceDragging)
        {
            double settled = L.SettleBalance(e.OldValue / 100, e.NewValue / 100) * 100;
            if (settled != e.NewValue) { BalanceSlider.Value = settled; return; } // comes back here with the settled value
        }
        double snapped = L.SnapBalance(e.NewValue / 100) * 100;
        _c.Engine.Balance = (float)(snapped / 100);
        _settings.Balance = snapped / 100;
        BalanceSlider.ToolTip = BalanceText(snapped);
    }

    // Snap to the centre when the knob is let go, not during the drag (snapping each small move held it in the centre).
    private void Balance_DragStarted(object sender, DragStartedEventArgs e) => _balanceDragging = true;

    private void Balance_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _balanceDragging = false;
        BalanceSlider.Value = L.SnapBalance(BalanceSlider.Value / 100) * 100;
    }

    /// <summary>Wheel over the balance: 10 % per notch (touchpad deltas summed), right = up; snaps through the centre.</summary>
    private void Balance_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        _balanceWheel += e.Delta;
        int notches = _balanceWheel / Mouse.MouseWheelDeltaForOneLine;
        if (notches != 0)
        {
            _balanceWheel -= notches * Mouse.MouseWheelDeltaForOneLine;
            BalanceSlider.Value = Math.Clamp(BalanceSlider.Value + notches * 10, -100, 100);
        }
        e.Handled = true;
    }

    private int _balanceWheel;

    private static string BalanceText(double v) =>
        v == 0 ? "Balance: centre" : $"Balance: {Math.Abs(v):0}% {(v < 0 ? "left" : "right")}";

    // ===== Called from the shared display code =====

    private void ClassicShowNowPlaying(Fernplay.Core.Audio.AudioFormatInfo? format)
    {
        if (_classic is not { } v) return;
        v.Title.Text = TitleMarquee.Text;
        v.Kbps.Text = L.Box(KbpsText.Text, L.KbpsChars);
        v.Khz.Text = L.Box(KhzText.Text, L.KhzChars);
        v.Mono.Source = v.Bitmaps[format?.Channels == 1 ? "Mono.On" : "Mono.Off"];
        v.Stereo.Source = v.Bitmaps[format?.Channels >= 2 ? "Stereo.On" : "Stereo.Off"];
    }

    private void ClassicUpdateState(PlayState state)
    {
        if (_classic is not { } v) return;
        v.State.Source = v.Bitmaps[state switch { PlayState.Playing => "State.Playing", PlayState.Paused => "State.Paused", _ => "State.Stopped" }];
        // Thin light: red while connecting or reconnecting, green while playing, off otherwise.
        bool connecting = _c.Status is not null && _c.NowPlaying is { IsStream: true } && state != PlayState.Playing;
        v.Light.Source = connecting ? v.Bitmaps["State.Buffering"] : state == PlayState.Playing ? v.Bitmaps["State.Ok"] : null;
    }

    private void ClassicUpdateClock(TimeSpan shown, bool blink)
    {
        if (_classic is not { } v) return;
        bool blank = !_c.Engine.IsLoaded || blink;
        v.Clock.Show(shown, blank);
        var (sign, minutes, seconds) = blank ? ("", "", "") : L.ShadeTime(shown);
        v.Shade.Sign.Text = sign; v.Shade.Minutes.Text = minutes; v.Shade.Seconds.Text = seconds;
    }

    private void ClassicAnimate(float[] levels)
    {
        if (_classic is not { } v) return;
        if (_shaded) { v.Shade.Spectrum.Push(levels); return; }
        v.Title.Step();
        v.Spectrum.Push(levels);
    }

    private void ClassicActivation(bool active)
    {
        if (_classic is not { } v) return;
        v.TitleBar.Source = v.Bitmaps[active ? "Title.Active" : "Title.Inactive"];
        v.Shade.Bar.Source = v.Bitmaps[active ? "Title.Shade.Active" : "Title.Shade.Inactive"];
    }

    private bool IsSkinDrop(string[] paths) => paths.Length == 1 && SkinLibrary.IsSkinFile(paths[0]) && System.IO.File.Exists(paths[0]);
}
