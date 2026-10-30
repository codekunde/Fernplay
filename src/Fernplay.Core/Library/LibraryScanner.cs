using System.Diagnostics;

namespace Fernplay.Core.Library;

/// <summary>A file the scan read: its tags (null when Windows could not read them) and file facts.</summary>
public sealed record ScannedFile(string Path, TagInfo? Tags, long Size, DateTime Modified);

/// <summary>What a scan found, to be applied to the index on its own thread (<see cref="LibraryIndex.Apply"/>).</summary>
public sealed record ScanChanges(
    IReadOnlyList<ScannedFile> Upserts, IReadOnlyList<string> Removed, IReadOnlyList<string> Playlists,
    IReadOnlyList<string> ScannedFolders, IReadOnlyList<string> MissingFolders, int Unchanged, DateTime ScannedAt, TimeSpan Elapsed);

/// <summary>
/// Walks the library folders and reports new, changed and gone files. Reads tags only for files whose size or
/// modified time changed. A folder that is not there (a drive not plugged in) is reported missing and its tracks
/// are kept. Runs on any thread: it only reads a snapshot of the index.
/// </summary>
public sealed class LibraryScanner(Func<string, TagInfo?>? readTags = null)
{
    private readonly Func<string, TagInfo?> _readTags = readTags ?? TagInfo.FromFile;

    private static readonly EnumerationOptions Walk = new()
    {
        RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
    };

    /// <summary>The index's current file facts, taken on the index's thread before a scan.</summary>
    public static Dictionary<string, (long Size, DateTime Modified)> Snapshot(LibraryIndex index) =>
        index.All.ToDictionary(t => t.Path, t => (t.Size, t.Modified), StringComparer.OrdinalIgnoreCase);

    public ScanChanges Scan(IReadOnlyList<string> folders, IReadOnlyDictionary<string, (long Size, DateTime Modified)> known,
        IProgress<int>? progress = null, CancellationToken cancel = default, DateTime? now = null)
    {
        var clock = Stopwatch.StartNew();
        var upserts = new List<ScannedFile>();
        var playlists = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scanned = new List<string>();
        var missing = new List<string>();
        int unchanged = 0, done = 0;

        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder)) { missing.Add(folder); continue; }
            scanned.Add(folder);
            foreach (var file in Directory.EnumerateFiles(folder, "*", Walk))
            {
                cancel.ThrowIfCancellationRequested();
                string ext = Path.GetExtension(file);
                if (ShellRegistration.PlaylistExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) { playlists.Add(file); continue; }
                if (!Playlist.SupportedExtensions.Contains(ext)) continue;
                if (!seen.Add(file)) continue; // a folder inside another library folder
                FileInfo info;
                try { info = new FileInfo(file); _ = info.Length; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                var modified = info.LastWriteTimeUtc;
                if (known.TryGetValue(file, out var k) && k.Size == info.Length && k.Modified == modified) unchanged++;
                else upserts.Add(new ScannedFile(file, PathTags.Fill(_readTags(file), file, folder), info.Length, modified));
                if (++done % 50 == 0) progress?.Report(done);
            }
        }
        progress?.Report(done);

        // Gone: known files under a folder that was scanned (not a missing one) and not seen now.
        var removed = known.Keys.Where(p => !seen.Contains(p) && scanned.Any(f => LibraryIndex.IsUnder(p, f))).ToList();
        return new ScanChanges(upserts, removed, playlists, scanned, missing, unchanged, now ?? DateTime.UtcNow, clock.Elapsed);
    }
}

/// <summary>When a play counts towards the play count and history.</summary>
public static class PlayRules
{
    public static readonly TimeSpan MaxNeeded = TimeSpan.FromMinutes(4);

    /// <summary>Half the track, or four minutes for long tracks (as other players count); a track without a known length needs four minutes.</summary>
    public static bool Counts(TimeSpan played, TimeSpan? length) =>
        played >= (length is { } l && l > TimeSpan.Zero ? TimeSpan.FromTicks(Math.Min(l.Ticks / 2, MaxNeeded.Ticks)) : MaxNeeded);
}
