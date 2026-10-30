using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fernplay.Core.Library;

namespace Fernplay.App;

/// <summary>
/// Album covers for the library: the picture Windows shows for the file (art inside the file), else a cover picture
/// in its folder (folder.jpg and the like). Loaded on one background thread, kept in memory for the session.
/// </summary>
public sealed class CoverCache : IDisposable
{
    public const int Size = 160; // screen pixels; the cover box is 68 skin px = 116 px at 1.7x

    private readonly Dictionary<string, ImageSource?> _done = new(StringComparer.OrdinalIgnoreCase); // UI thread only
    private readonly Dictionary<string, Task<ImageSource?>> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly BlockingCollection<(string Path, TaskCompletionSource<ImageSource?> Result)> _queue = [];
    private readonly ConcurrentDictionary<string, ImageSource?> _folders = new(StringComparer.OrdinalIgnoreCase); // worker thread
    private readonly Thread _worker;

    public CoverCache()
    {
        _worker = new Thread(Work) { IsBackground = true, Name = "Fernplay covers" };
        _worker.SetApartmentState(ApartmentState.STA); // shell thumbnail handlers expect a single-threaded apartment
        _worker.Start();
    }

    /// <summary>The cover for a track (null = none). Call on the UI thread.</summary>
    public Task<ImageSource?> GetAsync(string trackPath)
    {
        if (_done.TryGetValue(trackPath, out var known)) return Task.FromResult(known);
        if (_pending.TryGetValue(trackPath, out var running)) return running;
        var tcs = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add((trackPath, tcs));
        var task = Finish(trackPath, tcs.Task);
        _pending[trackPath] = task;
        return task;
    }

    private async Task<ImageSource?> Finish(string path, Task<ImageSource?> load)
    {
        var image = await load; // continues on the UI thread (the caller's context)
        _done[path] = image;
        _pending.Remove(path);
        return image;
    }

    private void Work()
    {
        foreach (var (path, result) in _queue.GetConsumingEnumerable())
        {
            ImageSource? image = null;
            try { image = Thumbnail(path) ?? FolderPicture(path); }
            catch (Exception) { /* a broken file or handler: no cover */ }
            result.SetResult(image);
        }
    }

    private ImageSource? FolderPicture(string trackPath)
    {
        string? folder = Path.GetDirectoryName(trackPath);
        if (folder is null) return null;
        return _folders.GetOrAdd(folder, f =>
        {
            if (CoverFiles.Find(f) is not { } file) return null;
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(file);
                image.DecodePixelWidth = Size;
                image.CacheOption = BitmapCacheOption.OnLoad; // reads the file now and lets it go
                image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                return null;
            }
        });
    }

    /// <summary>Windows' thumbnail of the file itself: only real pictures (art inside the file), never the file-type icon.</summary>
    private static ImageSource? Thumbnail(string path)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory) != 0 || factory is null) return null;
        try
        {
            if (factory.GetImage(new NativeSize { Cx = Size, Cy = Size }, ThumbnailOnly | BiggerSizeOk, out var hbitmap) != 0 || hbitmap == IntPtr.Zero)
                return null;
            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(hbitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally { DeleteObject(hbitmap); }
        }
        finally { Marshal.ReleaseComObject(factory); }
    }

    public void Dispose() => _queue.CompleteAdding();

    private const int BiggerSizeOk = 0x1, ThumbnailOnly = 0x8; // SIIGBF_*

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize { public int Cx, Cy; }

    [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(NativeSize size, int flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? factory);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
}
