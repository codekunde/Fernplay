using System.Runtime.InteropServices;

namespace Fernplay.App.Visualizer;

/// <summary>
/// libprojectM 4.1.8's C API (projectM-4/*.h), the part the visualizer uses. The DLLs are built by
/// tools/Build-ProjectM.ps1 and sit next to Fernplay.exe. C bool is one byte; file paths go over in the ANSI code page
/// because projectM opens them through a narrow std::string.
/// </summary>
internal static class ProjectM
{
    private const string Dll = "projectM-4.dll";

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr projectm_create();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void projectm_destroy(IntPtr pm);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr projectm_get_version_string();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void projectm_free_string(IntPtr s);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void projectm_set_texture_search_paths(IntPtr pm, IntPtr[] paths, nuint count);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern uint projectm_pcm_get_max_samples();
    /// <summary><paramref name="count"/> = samples per channel; stereo is LRLR.</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern unsafe void projectm_pcm_add_float(IntPtr pm, float* samples, uint count, int channels);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void projectm_set_window_size(IntPtr pm, nuint w, nuint h);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void projectm_set_fps(IntPtr pm, int fps);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void projectm_set_preset_locked(IntPtr pm, [MarshalAs(UnmanagedType.U1)] bool locked);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void projectm_opengl_render_frame(IntPtr pm);

    public const int Stereo = 2;

    /// <summary>
    /// projectM never calls glewInit itself, so the host must, after making the context current and before
    /// projectm_create, on the same glew32.dll that projectM-4.dll imports (otherwise it calls null GL pointers).
    /// </summary>
    [DllImport("glew32.dll", CallingConvention = CallingConvention.Winapi)] public static extern uint glewInit();
}

/// <summary>projectM's playlist library: the preset folder, shuffle, auto-advance, next and back through a history.</summary>
internal static class Presets
{
    private const string Dll = "projectM-4-playlist.dll";

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr projectm_playlist_create(IntPtr pm);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void projectm_playlist_destroy(IntPtr pl);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void projectm_playlist_clear(IntPtr pl);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, BestFitMapping = false)]
    public static extern uint projectm_playlist_add_path(IntPtr pl, string path, [MarshalAs(UnmanagedType.U1)] bool recurse, [MarshalAs(UnmanagedType.U1)] bool duplicates);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr projectm_playlist_item(IntPtr pl, uint index);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void projectm_playlist_free_string(IntPtr s);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void projectm_playlist_set_shuffle(IntPtr pl, [MarshalAs(UnmanagedType.U1)] bool on);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void projectm_playlist_set_retry_count(IntPtr pl, uint n);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern uint projectm_playlist_play_next(IntPtr pl, [MarshalAs(UnmanagedType.U1)] bool hardCut);
    /// <summary>Back through the history (the last 1000; presets that failed are not in it).</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern uint projectm_playlist_play_last(IntPtr pl, [MarshalAs(UnmanagedType.U1)] bool hardCut);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern uint projectm_playlist_size(IntPtr pl);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr projectm_playlist_items(IntPtr pl, uint start, uint count);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void projectm_playlist_free_string_array(IntPtr array);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern uint projectm_playlist_get_position(IntPtr pl);
    /// <summary>Plays the preset at this place in the playlist (raises the switched callback).</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern uint projectm_playlist_set_position(IntPtr pl, uint position, [MarshalAs(UnmanagedType.U1)] bool hardCut);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, BestFitMapping = false)]
    public static extern uint projectm_playlist_add_presets(IntPtr pl, [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)] string[] files,
        uint count, [MarshalAs(UnmanagedType.U1)] bool duplicates);

    /// <summary>Every preset's full path, in playlist order.</summary>
    public static string[] Items(IntPtr pl)
    {
        uint n = projectm_playlist_size(pl);
        if (n == 0) return [];
        var array = projectm_playlist_items(pl, 0, n);
        if (array == IntPtr.Zero) return [];
        try
        {
            var list = new List<string>((int)n);
            for (int i = 0; ; i++) // a null-terminated array of char*
            {
                var s = Marshal.ReadIntPtr(array, i * IntPtr.Size);
                if (s == IntPtr.Zero) break;
                list.Add(Marshal.PtrToStringAnsi(s) ?? "");
            }
            return [.. list];
        }
        finally { projectm_playlist_free_string_array(array); }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void Switched([MarshalAs(UnmanagedType.U1)] bool hardCut, uint index, IntPtr user);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void SwitchFailed(IntPtr filename, IntPtr message, IntPtr user);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void projectm_playlist_set_preset_switched_event_callback(IntPtr pl, Switched? cb, IntPtr user);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void projectm_playlist_set_preset_switch_failed_event_callback(IntPtr pl, SwitchFailed? cb, IntPtr user);

    public static string Item(IntPtr pl, uint i)
    {
        var p = projectm_playlist_item(pl, i);
        if (p == IntPtr.Zero) return "";
        try { return Marshal.PtrToStringAnsi(p) ?? ""; } finally { projectm_playlist_free_string(p); }
    }
}

/// <summary>Win32 and WGL calls for the child window projectM draws into.</summary>
internal static class Gl
{
    public const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPSIBLINGS = 0x04000000, WS_CLIPCHILDREN = 0x02000000;
    public const uint CS_OWNDC = 0x0020, CS_HREDRAW = 0x0002, CS_VREDRAW = 0x0001, CS_DBLCLKS = 0x0008;
    public const int WM_ERASEBKGND = 0x0014, WM_LBUTTONDBLCLK = 0x0203, WM_SETFOCUS = 0x0007, WM_RBUTTONUP = 0x0205;
    public const int BLACK_BRUSH = 4;
    [DllImport("gdi32.dll")] public static extern IntPtr GetStockObject(int which);

    public delegate IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public int cbSize; public uint style; public IntPtr lpfnWndProc; public int cbClsExtra; public int cbWndExtra;
        public IntPtr hInstance; public IntPtr hIcon; public IntPtr hCursor; public IntPtr hbrBackground;
        public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32", CharSet = CharSet.Unicode, SetLastError = true)] public static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowEx(int ex, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32")] public static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32")] public static extern IntPtr DefWindowProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
    [DllImport("user32")] public static extern bool GetClientRect(IntPtr hwnd, out RECT r);
    [DllImport("user32")] public static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32")] public static extern IntPtr LoadCursor(IntPtr inst, IntPtr name);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string? name);

    [StructLayout(LayoutKind.Sequential)]
    public struct PIXELFORMATDESCRIPTOR
    {
        public ushort nSize, nVersion; public uint dwFlags; public byte iPixelType, cColorBits, cRedBits, cRedShift, cGreenBits, cGreenShift,
            cBlueBits, cBlueShift, cAlphaBits, cAlphaShift, cAccumBits, cAccumRedBits, cAccumGreenBits, cAccumBlueBits, cAccumAlphaBits,
            cDepthBits, cStencilBits, cAuxBuffers, iLayerType, bReserved; public uint dwLayerMask, dwVisibleMask, dwDamageMask;
    }
    public const uint PFD_DOUBLEBUFFER = 1, PFD_DRAW_TO_WINDOW = 4, PFD_SUPPORT_OPENGL = 0x20;

    [DllImport("gdi32", SetLastError = true)] public static extern int ChoosePixelFormat(IntPtr dc, ref PIXELFORMATDESCRIPTOR pfd);
    [DllImport("gdi32", SetLastError = true)] public static extern bool SetPixelFormat(IntPtr dc, int format, ref PIXELFORMATDESCRIPTOR pfd);
    [DllImport("gdi32")] public static extern bool SwapBuffers(IntPtr dc);

    [DllImport("opengl32", SetLastError = true)] public static extern IntPtr wglCreateContext(IntPtr dc);
    [DllImport("opengl32", SetLastError = true)] public static extern bool wglMakeCurrent(IntPtr dc, IntPtr rc);
    [DllImport("opengl32")] public static extern bool wglDeleteContext(IntPtr rc);
    [DllImport("opengl32", CharSet = CharSet.Ansi)] public static extern IntPtr wglGetProcAddress(string name);
    [DllImport("opengl32")] public static extern IntPtr glGetString(uint name);
    [DllImport("opengl32")] public static extern void glViewport(int x, int y, int w, int h);
    [DllImport("opengl32")] public static extern void glClearColor(float r, float g, float b, float a);
    [DllImport("opengl32")] public static extern void glClear(uint mask);
    public const uint GL_VENDOR = 0x1F00, GL_RENDERER = 0x1F01, GL_VERSION = 0x1F02, GL_COLOR_BUFFER_BIT = 0x4000;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] public delegate bool SwapIntervalExt(int interval);
}
