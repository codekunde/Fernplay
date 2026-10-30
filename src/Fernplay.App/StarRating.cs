using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;

namespace Fernplay.App;

/// <summary>
/// Five stars; a click on the third gives three stars, a click on the current rating clears it. Raises
/// <see cref="PickedEvent"/> (bubbling) with the new value in <see cref="Value"/>. The mouse shows what a click would give.
/// </summary>
public sealed class StarRating : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(int), typeof(StarRating),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly RoutedEvent PickedEvent = EventManager.RegisterRoutedEvent("Picked", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(StarRating));

    private static readonly Brush On = Freeze(new SolidColorBrush(Color.FromRgb(0xE8, 0xC5, 0x47)));   // amber
    private static readonly Brush Off = Freeze(new SolidColorBrush(Color.FromRgb(0x3C, 0x52, 0x42)));  // dim, still seen on a selected row
    private static readonly Brush Hover = Freeze(new SolidColorBrush(Color.FromRgb(0xF2, 0xE1, 0x9A)));
    private static readonly Geometry Star = MakeStar();

    private int _hover; // star under the mouse (1-5), 0 = none

    public StarRating()
    {
        Cursor = Cursors.Hand;
        ToolTip = "Click a star to rate; click the same star again to clear (or press 0 to 5)";
    }

    public int Value
    {
        get => (int)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public event RoutedEventHandler Picked { add => AddHandler(PickedEvent, value); remove => RemoveHandler(PickedEvent, value); }

    protected override Size MeasureOverride(Size availableSize) => new(29, 6);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize)); // the gaps take clicks too
        double size = Math.Min(RenderSize.Height, RenderSize.Width / 5) * 0.9, step = RenderSize.Width / 5;
        for (int i = 0; i < 5; i++)
        {
            var brush = _hover > 0 ? (i < _hover ? Hover : Off) : (i < Value ? On : Off);
            dc.PushTransform(new MatrixTransform(size, 0, 0, size, i * step + (step - size) / 2, (RenderSize.Height - size) / 2));
            dc.DrawGeometry(brush, null, Star);
            dc.Pop();
        }
    }

    private int StarAt(Point p) => Math.Clamp((int)(p.X / (RenderSize.Width / 5)) + 1, 1, 5);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int h = StarAt(e.GetPosition(this));
        if (h != _hover) { _hover = h; InvalidateVisual(); }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hover = 0;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        int n = StarAt(e.GetPosition(this));
        SetCurrentValue(ValueProperty, n == Value ? 0 : n); // keeps the binding
        e.Handled = true;
        RaiseEvent(new RoutedEventArgs(PickedEvent, this));
    }

    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer(StarRating owner) : System.Windows.Automation.Peers.FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(StarRating);
        protected override System.Windows.Automation.Peers.AutomationControlType GetAutomationControlTypeCore() => System.Windows.Automation.Peers.AutomationControlType.Text;
        protected override string GetNameCore() => owner.Value == 0 ? "Not rated" : $"{owner.Value} of 5 stars";
    }

    /// <summary>A five-pointed star in a 1x1 box.</summary>
    private static Geometry MakeStar()
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            for (int i = 0; i < 10; i++)
            {
                double r = i % 2 == 0 ? 0.5 : 0.2, a = Math.PI / 5 * i - Math.PI / 2;
                var p = new Point(0.5 + r * Math.Cos(a), 0.53 + r * Math.Sin(a));
                if (i == 0) ctx.BeginFigure(p, isFilled: true, isClosed: true); else ctx.LineTo(p, true, false);
            }
        }
        g.Freeze();
        return g;
    }

    private static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }
}
