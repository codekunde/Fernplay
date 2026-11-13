using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Fernplay.Core.Audio;
using static Fernplay.App.Visualizer.Gl;

namespace Fernplay.App.Visualizer;

/// <summary>
/// The picture: a child window inside WPF that projectM draws into with OpenGL. The GL context, projectM, its preset
/// playlist and every GL call live on one render thread; the UI thread creates and destroys the window and sends
/// commands. WPF cannot draw over it (airspace), so all controls sit outside it.
/// </summary>
internal sealed class VisualizerView : HwndHost
{
    private const string ClassName = "FernplayVisualizer";
    private static readonly WndProc Proc = WindowProc; // kept alive for the window class
    private static readonly ConcurrentDictionary<IntPtr, VisualizerView> Views = new();
    private static bool _registered;

    private readonly PcmRing _pcm;
    private readonly ConcurrentQueue<Action<IntPtr, IntPtr>> _commands = new(); // (projectM, playlist) on the render thread
    private IntPtr _hwnd;
    private Thread? _render;
    private volatile bool _stop, _paused, _resizing;
    private string _folder;
    private bool _locked, _shuffle;
    private double _presetSeconds;
    // Fernplay times the presets itself (projectM stays locked): projectM applies a new preset duration only from the
    // next preset on, so a change in Settings waited out the old time. Restarted on every switch (render thread).
    private readonly Stopwatch _presetShown = new();

    // Kept alive while projectM may call them.
    private Presets.Switched? _switched;
    private Presets.SwitchFailed? _failed;

    /// <summary>The preset now showing (its place in the A–Z list, file name without .milk); raised on the UI thread.</summary>
    public event Action<int, string>? PresetChanged;
    /// <summary>Right-click on the picture (the child window gets the mouse, not WPF); raised on the UI thread.</summary>
    public event Action? MenuWanted;
    /// <summary>The visualizer cannot run here (reason for the user); raised on the UI thread, once.</summary>
    public event Action<string>? Failed;
    /// <summary>The folder has no presets projectM can read; raised on the UI thread.</summary>
    public event Action<string>? NoPresets;
    /// <summary>Double-click on the picture (the child window gets the mouse, not WPF); raised on the UI thread.</summary>
    public event Action? DoubleClicked;
    /// <summary>The child window took the keyboard focus; the WPF window should take it back. UI thread.</summary>
    public event Action? FocusWanted;

    public VisualizerView(PcmRing pcm, string folder, double presetSeconds, bool locked, bool shuffle)
    {
        _pcm = pcm;
        _folder = folder;
        _presetSeconds = presetSeconds;
        _locked = locked;
        _shuffle = shuffle;
    }

    /// <summary>Hidden or minimised: no frames are drawn (the GPU rests), nothing else changes.</summary>
    public bool Paused { get => _paused; set => _paused = value; }

    /// <summary>
    /// The window is being resized by hand: no frames are drawn until it ends. Drawing on while Windows moves the child
    /// window, and projectM rebuilding its textures for every new size, made dragging the grip sluggish (2026-11-13).
    /// </summary>
    public bool Resizing { get => _resizing; set => _resizing = value; }

    /// <summary>Shuffle off: NEXT goes on in A–Z order from the preset showing.</summary>
    public void SetShuffle(bool on) => _commands.Enqueue((_, pl) => Presets.projectm_playlist_set_shuffle(pl, _shuffle = on));

    /// <summary>Shows the preset at this place in the A–Z list at once.</summary>
    public void Play(int index) => _commands.Enqueue((_, pl) => Presets.projectm_playlist_set_position(pl, (uint)index, true));

    /// <summary>Every preset (full paths, A–Z by file name) and the place of the one showing; answered on the UI thread.</summary>
    public void RequestPresets(Action<string[], int> answer) => _commands.Enqueue((_, pl) =>
    {
        var items = Presets.Items(pl);
        int position = items.Length > 0 ? (int)Presets.projectm_playlist_get_position(pl) : -1;
        Raise(_ => answer(items, position));
    });

    public void Next() => _commands.Enqueue((_, pl) => DiagnosticLog.Write($"visualizer: next -> {Presets.projectm_playlist_play_next(pl, false)}"));
    public void Back() => _commands.Enqueue((_, pl) => DiagnosticLog.Write($"visualizer: back -> {Presets.projectm_playlist_play_last(pl, false)}"));

    /// <summary>Locked: the preset stays until Next or Back (no automatic change).</summary>
    public void SetLocked(bool locked) => _commands.Enqueue((_, _) => { _locked = locked; _presetShown.Restart(); });

    /// <summary>Takes effect for the preset showing now: one shown longer than the new time moves on at once.</summary>
    public void SetPresetSeconds(double seconds) => _commands.Enqueue((_, _) => _presetSeconds = seconds);

    public void SetFolder(string folder) => _commands.Enqueue((pm, pl) => LoadFolder(pm, pl, _folder = folder));

    // ================= Window =================

    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        if (!_registered)
        {
            var wc = new WNDCLASSEX
            {
                // No CS_HREDRAW/CS_VREDRAW: while the window is resized (no frames drawn) the last frame stays and only
                // the part that is new gets painted black.
                cbSize = Marshal.SizeOf<WNDCLASSEX>(), style = CS_OWNDC | CS_DBLCLKS,
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Proc), hInstance = GetModuleHandle(null),
                hCursor = LoadCursor(IntPtr.Zero, 32512), lpszClassName = ClassName, // IDC_ARROW
                hbrBackground = GetStockObject(BLACK_BRUSH),
            };
            if (RegisterClassEx(ref wc) == 0) throw new InvalidOperationException($"RegisterClassEx failed ({Marshal.GetLastWin32Error()})");
            _registered = true;
        }
        _hwnd = CreateWindowEx(0, ClassName, "", WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN,
            0, 0, 16, 16, parent.Handle, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastWin32Error()})");
        Views[_hwnd] = this;
        _render = new Thread(RenderLoop) { IsBackground = true, Name = "visualizer" };
        _render.Start();
        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        _stop = true;
        _render?.Join(5000); // projectM and the GL context go before their window does
        Views.TryRemove(hwnd.Handle, out _);
        DestroyWindow(hwnd.Handle);
    }

    private static IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l)
    {
        // projectM paints every pixel, so no flash of the class background; while resizing nothing is drawn, so the
        // part of the window that is new is painted black.
        if (msg == WM_ERASEBKGND && !(Views.TryGetValue(hwnd, out var e) && e._resizing)) return 1;
        if (msg == WM_LBUTTONDBLCLK && Views.TryGetValue(hwnd, out var v))
            v.Dispatcher.BeginInvoke(() => v.DoubleClicked?.Invoke());
        if (msg == WM_RBUTTONUP && Views.TryGetValue(hwnd, out var m))
            m.Dispatcher.BeginInvoke(() => m.MenuWanted?.Invoke());
        // The picture never keeps the keyboard: with the focus in this child window WPF gets no keys at all, so Esc,
        // N, P and the player's keys would stop working. Hand it back to WPF.
        if (msg == WM_SETFOCUS && Views.TryGetValue(hwnd, out var f))
            f.Dispatcher.BeginInvoke(() => f.FocusWanted?.Invoke());
        return DefWindowProc(hwnd, msg, w, l);
    }

    private void Raise(Action<VisualizerView> raise) => Dispatcher.BeginInvoke(() => raise(this));

    // ================= Render thread =================

    private const int Fps = 60;

    private unsafe void RenderLoop()
    {
        IntPtr dc = IntPtr.Zero, rc = IntPtr.Zero, pm = IntPtr.Zero, pl = IntPtr.Zero, timer = IntPtr.Zero;
        try
        {
            dc = GetDC(_hwnd);
            var pfd = new PIXELFORMATDESCRIPTOR
            {
                nSize = (ushort)Marshal.SizeOf<PIXELFORMATDESCRIPTOR>(), nVersion = 1,
                dwFlags = PFD_DRAW_TO_WINDOW | PFD_SUPPORT_OPENGL | PFD_DOUBLEBUFFER, cColorBits = 32, cAlphaBits = 8, cDepthBits = 24, cStencilBits = 8,
            };
            int format = ChoosePixelFormat(dc, ref pfd);
            if (format == 0 || !SetPixelFormat(dc, format, ref pfd)) throw new VisualizerException("Windows has no OpenGL pixel format for this window.");
            rc = wglCreateContext(dc);
            if (rc == IntPtr.Zero || !wglMakeCurrent(dc, rc)) throw new VisualizerException("OpenGL is not available (no graphics driver, or a remote desktop session).");
            string gl = $"{Str(glGetString(GL_RENDERER))}, OpenGL {Str(glGetString(GL_VERSION))}";
            DiagnosticLog.Write($"visualizer: {gl}");
            // Paced by a timer below, not by vsync: waiting for vsync kept a whole CPU core busy on NVIDIA (measured).
            if (wglGetProcAddress("wglSwapIntervalEXT") is var swap && swap != IntPtr.Zero)
                Marshal.GetDelegateForFunctionPointer<SwapIntervalExt>(swap)(0);

            if (ProjectM.glewInit() is var glew and not 0) throw new VisualizerException($"OpenGL could not be set up (GLEW error {glew}).");
            pm = ProjectM.projectm_create();
            if (pm == IntPtr.Zero) throw new VisualizerException($"The graphics driver is too old for the visualizer (needs OpenGL 3.3; this one has {gl}).");
            var version = ProjectM.projectm_get_version_string();
            DiagnosticLog.Write($"visualizer: projectM {Marshal.PtrToStringAnsi(version)}");
            ProjectM.projectm_free_string(version);
            ProjectM.projectm_set_fps(pm, Fps);
            ProjectM.projectm_set_preset_locked(pm, true); // never switches by itself: the loop below times the presets

            pl = Presets.projectm_playlist_create(pm);
            Presets.projectm_playlist_set_shuffle(pl, _shuffle);
            Presets.projectm_playlist_set_retry_count(pl, 10); // a preset that fails to load is skipped, up to 10 in a row
            var plHandle = pl;
            _switched = (_, index, _) =>
            {
                var name = Path.GetFileNameWithoutExtension(Presets.Item(plHandle, index));
                _presetShown.Restart(); // called on this thread, from inside the playlist's next/back
                Raise(v => v.PresetChanged?.Invoke((int)index, name));
            };
            _failed = (file, message, _) =>
                DiagnosticLog.Write($"visualizer: preset not loaded: {Path.GetFileName(Marshal.PtrToStringAnsi(file))}: {Marshal.PtrToStringAnsi(message)}");
            Presets.projectm_playlist_set_preset_switched_event_callback(pl, _switched, IntPtr.Zero);
            Presets.projectm_playlist_set_preset_switch_failed_event_callback(pl, _failed, IntPtr.Zero);
            LoadFolder(pm, pl, _folder);

            timer = CreateWaitableTimerEx(IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);
            var clock = Stopwatch.StartNew();
            long frame = 0;
            int w = 0, h = 0;
            while (!_stop)
            {
                while (_commands.TryDequeue(out var command)) command(pm, pl);
                if (_paused)
                {
                    _presetShown.Stop(); // a minimised visualizer does not use up the preset's time
                    Thread.Sleep(50);
                    clock.Restart();
                    frame = 0;
                    continue;
                }
                if (_resizing)
                {
                    Thread.Sleep(15); // the size is taken once the drag ends
                    clock.Restart();
                    frame = 0;
                    continue;
                }
                _presetShown.Start();
                if (!_locked && _presetShown.Elapsed.TotalSeconds >= _presetSeconds)
                {
                    _presetShown.Restart(); // also when the folder has no presets: try again after the time, not every frame
                    Presets.projectm_playlist_play_next(pl, false);
                }
                GetClientRect(_hwnd, out var r);
                if (r.Right != w || r.Bottom != h)
                {
                    (w, h) = (Math.Max(1, r.Right), Math.Max(1, r.Bottom));
                    long t0 = Stopwatch.GetTimestamp();
                    ProjectM.projectm_set_window_size(pm, (nuint)w, (nuint)h);
                    glViewport(0, 0, w, h);
                    DiagnosticLog.Write($"visualizer: size {w}x{h} ({Stopwatch.GetElapsedTime(t0).TotalMilliseconds:0.0} ms)");
                }
                var pcm = _pcm.Drain();
                int max = (int)ProjectM.projectm_pcm_get_max_samples();
                fixed (float* p = pcm)
                    for (int off = 0; off < pcm.Length / 2; off += max)
                        ProjectM.projectm_pcm_add_float(pm, p + off * 2, (uint)Math.Min(max, pcm.Length / 2 - off), ProjectM.Stereo);
                ProjectM.projectm_opengl_render_frame(pm);
                SwapBuffers(dc);

                frame++;
                double wait = frame * 1000.0 / Fps - clock.Elapsed.TotalMilliseconds;
                if (wait < -100) { clock.Restart(); frame = 0; } // fell far behind (a resize, a slow preset): start the beat again
                else if (wait > 0) Wait(timer, wait);
            }
        }
        catch (Exception ex) when (ex is VisualizerException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            string reason = ex is VisualizerException ? ex.Message : "The visualizer's files (projectM-4.dll, glew32.dll) are missing or damaged. Reinstalling Fernplay puts them back.";
            DiagnosticLog.Write($"visualizer failed: {ex.GetType().Name}: {ex.Message}");
            Raise(v => v.Failed?.Invoke(reason));
        }
        finally
        {
            if (pl != IntPtr.Zero) Presets.projectm_playlist_destroy(pl);
            if (pm != IntPtr.Zero) ProjectM.projectm_destroy(pm);
            if (rc != IntPtr.Zero) { wglMakeCurrent(IntPtr.Zero, IntPtr.Zero); wglDeleteContext(rc); }
            if (dc != IntPtr.Zero) ReleaseDC(_hwnd, dc);
            if (timer != IntPtr.Zero) CloseHandle(timer);
        }
    }

    private void LoadFolder(IntPtr pm, IntPtr pl, string folder)
    {
        // Textures that presets name are looked for in the preset folder and a "textures" folder in it or next to it
        // (MilkDrop's layout: presets\ and textures\ side by side).
        var places = new[] { folder, Path.Combine(folder, "textures"), Path.Combine(Path.GetDirectoryName(folder) ?? folder, "textures") }
            .Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var ptrs = places.Select(Marshal.StringToHGlobalAnsi).ToArray();
        try { ProjectM.projectm_set_texture_search_paths(pm, ptrs, (nuint)ptrs.Length); }
        finally { foreach (var p in ptrs) Marshal.FreeHGlobal(p); }

        Presets.projectm_playlist_clear(pl);
        uint added = Directory.Exists(folder) ? Presets.projectm_playlist_add_path(pl, folder, true, false) : 0;
        DiagnosticLog.Write($"visualizer: {added} presets in {folder}");
        if (added == 0) { Raise(v => v.NoPresets?.Invoke(folder)); return; }
        // A–Z by file name, not by folder and ignoring case (projectM's own sort puts "Z" before "a"), so the chooser's
        // list and NEXT with shuffle off follow the names.
        var sorted = Presets.Items(pl)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ThenBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        Presets.projectm_playlist_clear(pl);
        Presets.projectm_playlist_add_presets(pl, sorted, (uint)sorted.Length, false);
        Presets.projectm_playlist_play_next(pl, true);
    }

    private static void Wait(IntPtr timer, double ms)
    {
        if (timer == IntPtr.Zero) { Thread.Sleep((int)Math.Ceiling(ms)); return; }
        long due = -(long)(ms * 10_000); // relative, in 100 ns
        if (SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false)) WaitForSingleObject(timer, 1000);
        else Thread.Sleep((int)Math.Ceiling(ms));
    }

    private static string Str(IntPtr p) => Marshal.PtrToStringAnsi(p) ?? "?";

    // A high-resolution waitable timer (Windows 10 1803+) waits about 1 ms precisely; Sleep can take 15.6 ms when no
    // program has raised the system timer resolution, which would make 60 fps stutter. Null on older Windows: Sleep.
    private const uint CreateWaitableTimerHighResolution = 0x2, TimerAllAccess = 0x1F0003;
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWaitableTimerEx(IntPtr attributes, string? name, uint flags, uint access);
    [DllImport("kernel32", SetLastError = true)]
    private static extern bool SetWaitableTimer(IntPtr timer, ref long due, int period, IntPtr completion, IntPtr arg, bool resume);
    [DllImport("kernel32")] private static extern uint WaitForSingleObject(IntPtr handle, uint ms);
    [DllImport("kernel32")] private static extern bool CloseHandle(IntPtr handle);
}

/// <summary>Why the visualizer cannot run, in words for the user.</summary>
internal sealed class VisualizerException(string message) : Exception(message);
