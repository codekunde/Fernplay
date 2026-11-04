using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Fernplay.Core;

namespace Fernplay.App;

/// <summary>
/// A borderless skinned panel that can be dragged by a handle, snaps to the other panels and the screen edges,
/// and (for the main panel) carries every panel docked to it.
/// </summary>
public class SkinWindow : Window
{
    private static readonly List<SkinWindow> Panels = [];

    /// <summary>Screen pixels per skin pixel for every panel and dialog (1.7 = 476 px wide; was 2.0 until 0.1.x).</summary>
    public const double Scale = 1.7;

    /// <summary>Settings: snap to other panels and the screen edges while dragging.</summary>
    public static bool SnapEnabled { get; set; } = true;

    private Point _dragStart;
    private List<(SkinWindow Window, double Left, double Top)>? _dragGroup;

    public SkinWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;
        Background = Brushes.Black;
        Panels.Add(this);
        Closed += (_, _) => Panels.Remove(this);
        StateChanged += (_, _) => { if (WindowState != WindowState.Minimized) RefitAllSoon(); };
        SourceInitialized += (_, _) => AllowMinimizeFromTaskbar();
        HookSystemEvents();
    }

    // WPF leaves out WS_MINIMIZEBOX for a borderless window that cannot be resized. Windows 10's taskbar minimises such a
    // window on a click anyway; Windows 11's does not (a click on the taskbar button then does nothing). The style has
    // no look of its own without a caption, so adding it back changes nothing else.
    private void AllowMinimizeFromTaskbar()
    {
        if (Owner is not null || !ShowInTaskbar) return; // only the player has a taskbar button; owned panels go with it
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        long style = GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
        long wanted = style | WsMinimizeBox | WsSysMenu;
        if (wanted != style) SetWindowLongPtr(hwnd, GwlStyle, new IntPtr(wanted));
    }

    private const int GwlStyle = -16;
    private const long WsMinimizeBox = 0x00020000, WsSysMenu = 0x00080000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    private static bool _systemHooked;

    // After Windows is locked while the player is minimised, the main panel came back 160x28 larger than its content
    // (160x28 is Windows' minimised-window size): content centred in black, shifted off the docked panels. Rolling
    // into the windowshade fixed it because a content size change makes SizeToContent fit again; this refits on
    // restore, unlock, resume and display changes.
    private static void HookSystemEvents()
    {
        if (_systemHooked) return;
        _systemHooked = true;
        Microsoft.Win32.SystemEvents.SessionSwitch += (_, e) =>
        {
            DiagnosticLog.Write($"session {e.Reason}: {DescribePanels()}");
            if (e.Reason is Microsoft.Win32.SessionSwitchReason.SessionLock or Microsoft.Win32.SessionSwitchReason.ConsoleDisconnect or Microsoft.Win32.SessionSwitchReason.RemoteDisconnect)
            {
                _away = true;
                RememberPlaces();
            }
            else if (e.Reason is Microsoft.Win32.SessionSwitchReason.SessionUnlock or Microsoft.Win32.SessionSwitchReason.ConsoleConnect or Microsoft.Win32.SessionSwitchReason.RemoteConnect)
            {
                _away = false;
                RefitAllSoon();
            }
        };
        Microsoft.Win32.SystemEvents.PowerModeChanged += (_, e) =>
        {
            DiagnosticLog.Write($"power {e.Mode}: {DescribePanels()}");
            if (e.Mode == Microsoft.Win32.PowerModes.Suspend) RememberPlaces();
            if (e.Mode == Microsoft.Win32.PowerModes.Resume) RefitAllSoon();
        };
        // A screen that sleeps or goes away (common while locked) makes Windows move each window on its own, which
        // pulls the docked stack apart. Remember the places before, put them back once the screens are as they were.
        Microsoft.Win32.SystemEvents.DisplaySettingsChanging += (_, _) =>
        {
            DiagnosticLog.Write($"display changing: {DescribePanels()}");
            RememberPlaces();
        };
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) =>
        {
            DiagnosticLog.Write($"display changed, screen {ScreenKey()}: {DescribePanels()}");
            RefitAllSoon();
        };
    }

    // ================= Places across lock, sleep and display changes =================

    private static bool _away; // locked or disconnected
    private static (string Screen, List<(SkinWindow Window, double Left, double Top)> Places)? _remembered;
    private static System.Windows.Threading.DispatcherTimer? _forget;

    /// <summary>The desktop's layout in device pixels (all screens); places are only put back onto the same layout.</summary>
    private static string ScreenKey() =>
        $"{GetSystemMetrics(80)} screens, {GetSystemMetrics(76)},{GetSystemMetrics(77)} {GetSystemMetrics(78)}x{GetSystemMetrics(79)}, primary {GetSystemMetrics(0)}x{GetSystemMetrics(1)}";

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index); // 0/1 primary size, 76-79 virtual screen, 80 monitor count

    /// <summary>Keeps every visible panel's place (once; the first, undisturbed one counts until it is put back or forgotten).</summary>
    private static void RememberPlaces()
    {
        var ui = Application.Current?.Dispatcher;
        if (ui is null) return;
        ui.Invoke(() =>
        {
            _forget?.Stop();
            if (_remembered is not null) return;
            var places = Panels.Where(w => w.IsVisible && w.WindowState != WindowState.Minimized).Select(w => (w, w.Left, w.Top)).ToList();
            if (places.Count > 0) _remembered = (ScreenKey(), places);
        });
    }

    /// <summary>Puts the remembered places back when the screens are as they were; forgets them a while after.</summary>
    private static void RestorePlaces()
    {
        if (_remembered is not { } r) return;
        if (r.Screen != ScreenKey()) return; // a display is still missing or changed: wait for the next change
        bool moved = false;
        foreach (var (w, left, top) in r.Places)
        {
            if (!w.IsVisible || w.WindowState == WindowState.Minimized) continue;
            if (Math.Abs(w.Left - left) < 0.5 && Math.Abs(w.Top - top) < 0.5) continue;
            w.Left = left;
            w.Top = top;
            moved = true;
        }
        if (moved) DiagnosticLog.Write($"panels put back: {DescribePanels()}");
        // Windows may still move things for a few seconds after an unlock; keep the places until it has settled.
        _forget ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _forget.Tick -= Forget;
        _forget.Tick += Forget;
        _forget.Stop();
        _forget.Start();
    }

    private static void Forget(object? sender, EventArgs e)
    {
        _forget?.Stop();
        if (!_away) _remembered = null;
    }

    /// <summary>The user moved a panel: what was remembered is no longer wanted.</summary>
    private static void ForgetPlaces()
    {
        _forget?.Stop();
        _remembered = null;
    }

    private static string DescribePanels()
    {
        try
        {
            return Application.Current?.Dispatcher.Invoke(() => string.Join(" | ", Panels.Where(w => w.IsVisible).Select(w =>
                $"{w.GetType().Name} {w.WindowState} {w.Left:0},{w.Top:0} {w.ActualWidth:0}x{w.ActualHeight:0}"))) ?? "";
        }
        catch (Exception ex) when (ex is InvalidOperationException or TaskCanceledException) { return "?"; }
    }

    /// <summary>Refits every panel now and once more shortly after (Windows may still be settling the desktop).</summary>
    private static void RefitAllSoon()
    {
        var ui = Application.Current?.Dispatcher;
        if (ui is null) return;
        ui.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, () => { RefitAll(); RestorePlaces(); });
        foreach (int ms in new[] { 600, 3000 })
        {
            var again = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(ms) };
            again.Tick += (_, _) => { again.Stop(); RefitAll(); RestorePlaces(); };
            again.Start();
        }
    }

    /// <summary>Makes each visible panel's window exactly fit its content again (toggling SizeToContent re-runs the fit).</summary>
    internal static void RefitAll()
    {
        foreach (var w in Panels.ToList())
        {
            if (!w.IsVisible || w.WindowState == WindowState.Minimized || w.SizeToContent != SizeToContent.WidthAndHeight) continue;
            w.SizeToContent = SizeToContent.Manual;
            w.SizeToContent = SizeToContent.WidthAndHeight;
            w.UpdateLayout();
            w.InvalidateVisual();
        }
    }

    /// <summary>The main panel moves its docked panels along; the others move alone (and so come loose).</summary>
    public bool CarriesDocked { get; set; }

    /// <summary>Makes an element (the title bar) drag this panel.</summary>
    protected void AttachDragHandle(FrameworkElement handle)
    {
        handle.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && IsInsideButton(d, handle)) return;
            ForgetPlaces();
            _dragStart = CursorDip();
            var members = CarriesDocked ? DockedTo(this) : [this];
            _dragGroup = members.Select(w => (w, w.Left, w.Top)).ToList();
            handle.CaptureMouse();
            e.Handled = true;
        };
        handle.MouseMove += (_, e) =>
        {
            if (_dragGroup is null || e.LeftButton != MouseButtonState.Pressed) return;
            var p = CursorDip();
            double dx = p.X - _dragStart.X, dy = p.Y - _dragStart.Y;
            var me = _dragGroup.First(g => g.Window == this);
            var moving = new Box(me.Left + dx, me.Top + dy, ActualWidth, ActualHeight);
            var others = Panels.Where(w => w.IsVisible && _dragGroup.All(g => g.Window != w)).Select(w => w.Box);
            var wa = SystemParameters.WorkArea;
            var (sx, sy) = SnapEnabled ? PanelLayout.SnapOffset(moving, others, new Box(wa.Left, wa.Top, wa.Width, wa.Height)) : (0, 0);
            foreach (var (w, l, t) in _dragGroup)
            {
                w.Left = l + dx + sx;
                w.Top = t + dy + sy;
            }
        };
        handle.MouseLeftButtonUp += (_, _) => EndDrag(handle);
        handle.LostMouseCapture += (_, _) => _dragGroup = null;
    }

    private void EndDrag(FrameworkElement handle)
    {
        _dragGroup = null;
        handle.ReleaseMouseCapture();
    }

    /// <summary>
    /// Runs a change of look (Fernplay's or a classic skin, another size) and moves the panels docked below this one by
    /// the change in height, so the stack stays closed.
    /// </summary>
    protected void ChangeLook(Action change)
    {
        double oldTop = Top, oldBottom = Top + ActualHeight;
        var below = IsLoaded && IsVisible ? DockedTo(this).Where(w => w != this && w.Top >= oldBottom - 1).ToList() : [];
        change();
        if (below.Count == 0) return;
        UpdateLayout();
        double dy = Top + ActualHeight - oldBottom;
        if (Math.Abs(Top - oldTop) < 0.5 && dy != 0)
            foreach (var w in below) w.Top += dy;
    }

    /// <summary>Layout scale for a classic skin size: whole screen pixels per skin pixel at this window's DPI.</summary>
    protected double ClassicScale(int size)
    {
        double dpi = PresentationSource.FromVisual(this) is not null ? VisualTreeHelper.GetDpi(this).DpiScaleX : 1;
        return Fernplay.Core.Skins.ClassicLayout.LayoutScale(size, dpi);
    }

    /// <summary>Every visible panel connected to this one through touching edges.</summary>
    public static List<SkinWindow> DockedTo(SkinWindow start)
    {
        var visible = Panels.Where(w => w.IsVisible || w == start).ToList();
        return PanelLayout.Group(visible.Select(w => w.Box).ToList(), visible.IndexOf(start)).Select(i => visible[i]).ToList();
    }

    private Box Box => new(Left, Top, ActualWidth, ActualHeight);
    internal static bool IsInsideButton(DependencyObject d, FrameworkElement stopAt)
    {
        for (var x = d; x is not null && x != stopAt; x = x is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x))
            if (x is System.Windows.Controls.Primitives.ButtonBase) return true;
        return false;
    }

    private Point CursorDip()
    {
        GetCursorPos(out var p);
        var source = PresentationSource.FromVisual(this);
        var m = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        return m.Transform(new Point(p.X, p.Y));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint p);
}
