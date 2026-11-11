using System.Windows;
using Fernplay.Core.Podcasts;

namespace Fernplay.App;

/// <summary>Asks for a podcast's feed address, reads the feed, and says why when it cannot be added.</summary>
public partial class PodcastAddDialog : SkinDialog
{
    private readonly PodcastService _podcasts;
    private bool _busy;

    public PodcastAddDialog(PodcastService podcasts)
    {
        _podcasts = podcasts;
        InitializeComponent();
        AttachDragHandle(TitleBar);
        Loaded += (_, _) =>
        {
            // A feed address on the clipboard is most likely what the owner came to add.
            try
            {
                if (Clipboard.ContainsText() && PodcastClient.Normalize(Clipboard.GetText()) is not null && Clipboard.GetText().Trim() is { Length: < 2000 } text
                    && (text.Contains("://", StringComparison.Ordinal) || text.StartsWith("feed:", StringComparison.OrdinalIgnoreCase)))
                    AddressBox.Text = text;
            }
            catch (System.Runtime.InteropServices.COMException) { } // clipboard held by another program
            AddressBox.Focus();
            AddressBox.SelectAll();
        };
    }

    /// <summary>The podcast added (or the one already there), once the dialog closed with OK.</summary>
    public PodcastShow? Podcast { get; private set; }

    private void AddressBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        AddButton.IsEnabled = !_busy && AddressBox.Text.Trim().Length > 0;
        if (!_busy) Note(null);
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || AddressBox.Text.Trim().Length == 0) return;
        _busy = true;
        AddButton.IsEnabled = false;
        AddressBox.IsReadOnly = true;
        Note("Reading the feed…");
        var (show, error) = await _podcasts.SubscribeAsync(AddressBox.Text);
        _busy = false;
        if (!IsVisible) return; // cancelled while the feed was read (a podcast found meanwhile is still added)
        AddressBox.IsReadOnly = false;
        if (show is not null)
        {
            Podcast = show;
            DialogResult = true; // already subscribed: still go to it
            return;
        }
        Note(error);
        AddButton.IsEnabled = true;
        AddressBox.Focus();
    }

    private void Note(string? text)
    {
        NoteText.Text = text ?? "";
        NoteText.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
