using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Fernplay.App;

/// <summary>
/// A borderless skinned dialog (settings, about): dragged by its title bar, closed with Esc.
/// Not a panel, so it takes no part in snapping or docking.
/// </summary>
public class SkinDialog : Window
{
    public SkinDialog()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.Black;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    protected void AttachDragHandle(FrameworkElement handle) =>
        handle.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && SkinWindow.IsInsideButton(d, handle)) return;
            DragMove();
        };

    protected void Close_Click(object sender, RoutedEventArgs e) => Close();
}

/// <summary>The app icon from the embedded .ico, at the frame closest to (not below) a pixel size.</summary>
public static class AppIcon
{
    private static readonly Lazy<BitmapFrame[]> Frames = new(() =>
    {
        var decoder = BitmapDecoder.Create(new Uri("pack://application:,,,/Assets/Fernplay.ico"),
            BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        return decoder.Frames.OrderBy(f => f.PixelWidth).ToArray();
    });

    public static ImageSource Frame(int px) =>
        Frames.Value.FirstOrDefault(f => f.PixelWidth >= px) ?? Frames.Value[^1];

    public static string Version
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }
}
