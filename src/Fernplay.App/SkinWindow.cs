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
        HookSystemEvents();
    }

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
            if (e.Reason is Microsoft.Win32.SessionSwitchReason.SessionUnlock or Microsoft.Win32.SessionSwitchReason.ConsoleConnect or Microsoft.Win32.SessionSwitchReason.RemoteConnect)
                RefitAllSoon();
        };
        Microsoft.Win32.SystemEvents.PowerModeChanged += (_, e) => { if (e.Mode == Microsoft.Win32.PowerModes.Resume) RefitAllSoon(); };
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => RefitAllSoon();
    }

    /// <summary>Refits every panel now and once more shortly after (Windows may still be settling the desktop).</summary>
    private static void RefitAllSoon()
    {
        var ui = Application.Current?.Dispatcher;
        if (ui is null) return;
        ui.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, RefitAll);
        var again = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(600) };
        again.Tick += (_, _) => { again.Stop(); RefitAll(); };
        again.Start();
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
