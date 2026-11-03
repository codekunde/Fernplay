using System.IO;
using System.Windows.Threading;
using Fernplay.Core;
using Fernplay.Core.Library;

namespace Fernplay.App;

/// <summary>
/// The library behind the library window: the index file next to the settings (saved a moment after a change),
/// background scans, and play counting. Plays count only once the owner has added a folder, so a Fernplay that never
/// uses the library writes nothing for it.
/// </summary>
public sealed class LibraryService
{
    private readonly PlayerController _c;
    private readonly DispatcherTimer _save = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _listen = new() { Interval = TimeSpan.FromSeconds(1) };
    private CancellationTokenSource? _scan;
    private string? _listening; // path of the track being listened to
    private TimeSpan _heard;
    private bool _counted;

    public static string FilePath => Path.Combine(Path.GetDirectoryName(UserSettings.FilePath)!, "library.json");

    public LibraryIndex Index { get; }

    /// <summary>"Scanning… 1,250 files" while a scan runs; null otherwise.</summary>
    public string? ScanStatus { get; private set; }

    /// <summary>The last scan's summary ("Scanned in 2.4 s: 3 new, 1 changed, 0 gone; Music2 not found").</summary>
    public string? LastScan { get; private set; }

    public event EventHandler? ScanStatusChanged;

    public LibraryService(PlayerController controller)
    {
        _c = controller;
        Index = LibraryIndex.Load(FilePath);
        Index.Changed += (_, _) => { _save.Stop(); _save.Start(); };
        _save.Tick += (_, _) => { _save.Stop(); SaveNow(); };
        _listen.Tick += (_, _) => Listen();
        _listen.Start();
        _c.TagsSaved += (_, path) => _ = RefreshFileAsync(path);
    }

    /// <summary>A file's tags were edited: read it again now rather than at the next scan.</summary>
    private async Task RefreshFileAsync(string path)
    {
        if (Index.Find(path) is null) return;
        var file = await Task.Run(() =>
        {
            var info = new FileInfo(path);
            return new ScannedFile(path, TagInfo.FromFile(path), info.Length, info.LastWriteTimeUtc);
        });
        Index.Refresh(file);
    }

    public void SaveNow()
    {
        if (Index.Folders.Count == 0 && !File.Exists(FilePath)) return; // never used: no file
        try { Index.Save(FilePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _c.ShowNotice($"Library not saved: {ex.Message}"); }
    }

    public bool IsScanning => _scan is not null;

    /// <summary>
    /// Scans all library folders in the background and applies the result on the UI thread. Asked again while a scan
    /// runs (a folder was added), it scans once more when the running one ends.
    /// </summary>
    public async Task ScanAsync()
    {
        if (_scan is not null) { _again = true; return; }
        do
        {
            _again = false;
            if (Index.Folders.Count == 0) return;
            await ScanOnceAsync();
        }
        while (_again);
    }

    private bool _again;

    private async Task ScanOnceAsync()
    {
        _scan = new CancellationTokenSource();
        var token = _scan.Token;
        var folders = Index.Folders.ToList();
        var known = LibraryScanner.Snapshot(Index);
        var progress = new Progress<int>(n => SetScanStatus($"Scanning… {n:N0} files"));
        SetScanStatus("Scanning…");
        try
        {
            var changes = await Task.Run(() => new LibraryScanner().Scan(folders, known, progress, token), token);
            Index.Apply(changes);
            // A folder removed while the scan ran: its files came back with the scan; take them out again.
            foreach (var gone in folders.Where(f => !Index.Folders.Contains(f, StringComparer.OrdinalIgnoreCase))) Index.RemoveFolder(gone);
            int added = changes.Upserts.Count(u => !known.ContainsKey(u.Path)), changed = changes.Upserts.Count - added;
            string missing = changes.MissingFolders.Count == 0 ? "" : $"; not found: {string.Join(", ", changes.MissingFolders.Select(Path.GetFileName))}";
            LastScan = $"Scanned in {changes.Elapsed.TotalSeconds:0.0} s: {added:N0} new, {changed:N0} changed, {changes.Removed.Count:N0} gone{missing}";
        }
        catch (OperationCanceledException) { LastScan = "Scan stopped."; }
        finally
        {
            _scan.Dispose();
            _scan = null;
            SetScanStatus(null);
        }
    }

    public void StopScan()
    {
        _again = false;
        _scan?.Cancel();
    }

    private void SetScanStatus(string? text)
    {
        ScanStatus = text;
        ScanStatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Counts listening time of the playing file once a second (time actually heard, so seeking does not count) and
    /// records one play when <see cref="PlayRules"/> says it counts.
    /// </summary>
    private void Listen()
    {
        var now = _c.NowPlaying;
        if (now is null || now.IsStream) { _listening = null; return; }
        if (!string.Equals(now.Path, _listening, StringComparison.OrdinalIgnoreCase) || _c.Engine.Position < TimeSpan.FromSeconds(2) && _heard > TimeSpan.FromSeconds(5))
        {
            // A new track, or the same one started again (repeat): a new play.
            _listening = now.Path; _heard = TimeSpan.Zero; _counted = false;
        }
        if (_c.Engine.State != PlayState.Playing) return;
        _heard += _listen.Interval;
        if (_counted || Index.Folders.Count == 0) return;
        var length = _c.Engine.IsLoaded ? _c.Engine.Duration : now.Duration ?? TimeSpan.Zero;
        if (!PlayRules.Counts(_heard, length > TimeSpan.Zero ? length : null)) return;
        _counted = true;
        Index.RecordPlay(now.Path, DateTime.UtcNow);
    }
}
