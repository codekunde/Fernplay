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
            var (sx, sy) = PanelLayout.SnapOffset(moving, others, new Box(wa.Left, wa.Top, wa.Width, wa.Height));
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

    /// <summary>Every visible panel connected to this one through touching edges.</summary>
    public static List<SkinWindow> DockedTo(SkinWindow start)
    {
        var visible = Panels.Where(w => w.IsVisible || w == start).ToList();
        return PanelLayout.Group(visible.Select(w => w.Box).ToList(), visible.IndexOf(start)).Select(i => visible[i]).ToList();
    }

    private Box Box => new(Left, Top, ActualWidth, ActualHeight);
    private static bool IsInsideButton(DependencyObject d, FrameworkElement stopAt)
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
