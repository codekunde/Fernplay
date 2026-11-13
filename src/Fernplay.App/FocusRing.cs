using System.Windows;
using System.Windows.Input;

namespace Fernplay.App;

/// <summary>
/// Shows the keyboard focus ring only while the keyboard is in use (like the web's :focus-visible):
/// a key press in a window turns it on, a mouse press turns it off. The ring in Skin.xaml (SkinFocus)
/// binds to <see cref="VisibleProperty"/>, which every element inherits from its window.
/// </summary>
public static class FocusRing
{
    public static readonly DependencyProperty VisibleProperty = DependencyProperty.RegisterAttached(
        "Visible", typeof(bool), typeof(FocusRing),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetVisible(DependencyObject d) => (bool)d.GetValue(VisibleProperty);
    public static void SetVisible(DependencyObject d, bool value) => d.SetValue(VisibleProperty, value);

    /// <summary>Called once at startup: watches every window.</summary>
    public static void Install()
    {
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseDownEvent,
            new MouseButtonEventHandler((s, _) => SetVisible((Window)s, false)), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewKeyDownEvent,
            new KeyEventHandler((s, e) =>
            {
                if (IsModifierOnly(e)) return;
                _before = GetVisible((Window)s);
                SetVisible((Window)s, true);
            }), handledEventsToo: true);
    }

    private static bool _before; // the ring before the key now being handled (one UI thread)

    /// <summary>
    /// The key was a shortcut (Space, Z X C V B, N…), not keyboard navigation: the ring goes back to how it was. Without
    /// this, Space after clicking STOP drew the ring around STOP until the next mouse click (owner, Windows 11, 2026-11-13).
    /// Class handlers run before the windows' own, so this undoes the change made above for the same key.
    /// </summary>
    public static void KeyWasShortcut(Window w) => SetVisible(w, _before);

    // Alt, Ctrl, Shift or Windows alone (Alt+Tab to another app, for example) is not keyboard navigation.
    private static bool IsModifierOnly(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        return key is Key.LeftAlt or Key.RightAlt or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin;
    }
}
