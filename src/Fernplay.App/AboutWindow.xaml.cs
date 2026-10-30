namespace Fernplay.App;

public partial class AboutWindow : SkinDialog
{
    public AboutWindow()
    {
        InitializeComponent();
        AttachDragHandle(TitleBar);
        Logo.Source = AppIcon.Frame(64);
        VersionText.Text = $"Version {AppIcon.Version}";
    }
}
