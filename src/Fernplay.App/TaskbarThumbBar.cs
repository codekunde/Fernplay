using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Fernplay.Core;

namespace Fernplay.App;

/// <summary>
/// Previous, Play/Pause and Next in the player's taskbar thumbnail, through ITaskbarList3 on the player's window.
/// Not WPF's TaskbarItemInfo: that draws the pictures at one size for the whole session and hides the results, while
/// here each picture is drawn for the window's DPI (<see cref="ThumbBar.Draw"/>) and every call is logged.
/// The buttons are added each time the taskbar (re)creates Fernplay's button ("TaskbarButtonCreated", also after
/// Explorer restarts); a press arrives as WM_COMMAND with THBN_CLICKED. Any failure leaves the thumbnail plain.
/// </summary>
public sealed class TaskbarThumbBar : IDisposable
{
    private readonly Action<ThumbButton> _pressed;
    private readonly uint _buttonCreated = RegisterWindowMessage("TaskbarButtonCreated");
    private HwndSource? _source;
    private ITaskbarList3? _taskbar;
    private bool _added;
    private ThumbButtonView[] _views = ThumbBar.For(PlayState.Stopped, false, 0);
    private string _shown = "";
    private int _iconSize;
    private readonly Dictionary<ThumbGlyph, IntPtr> _icons = [];

    private const int WmCommand = 0x0111, ThbnClicked = 0x1800;

    /// <summary>Call from the window's SourceInitialized (before it is shown, so the first TaskbarButtonCreated is seen).</summary>
    public TaskbarThumbBar(Window window, Action<ThumbButton> pressed)
    {
        _pressed = pressed;
        var hwnd = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(hwnd);
        if (_source is null || _buttonCreated == 0) return;
        // Only matters if Fernplay runs as administrator: Explorer's messages would otherwise be filtered out.
        ChangeWindowMessageFilterEx(hwnd, _buttonCreated, 1, IntPtr.Zero);
        ChangeWindowMessageFilterEx(hwnd, WmCommand, 1, IntPtr.Zero);
        _source.AddHook(Hook);
        window.DpiChanged += (_, _) => { if (_added) Apply(force: true); };
    }

    /// <summary>Shows the buttons for this state (does nothing until the taskbar button exists).</summary>
    public void Update(PlayState state, bool loaded, int trackCount)
    {
        _views = ThumbBar.For(state, loaded, trackCount);
        if (_added) Apply(force: false);
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _buttonCreated && _buttonCreated != 0)
        {
            AddButtons(hwnd);
        }
        else if (msg == WmCommand && ((wParam.ToInt64() >> 16) & 0xFFFF) == ThbnClicked && _added)
        {
            int id = (int)(wParam.ToInt64() & 0xFFFF);
            if (Enum.IsDefined(typeof(ThumbButton), id))
            {
                handled = true;
                _pressed((ThumbButton)id);
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>The taskbar made Fernplay's button (at start, or again after Explorer restarted): put the buttons on it.</summary>
    private void AddButtons(IntPtr hwnd)
    {
        try
        {
            if (_taskbar is null)
            {
                var taskbar = (ITaskbarList3)new TaskbarListClass();
                taskbar.HrInit();
                _taskbar = taskbar;
            }
            var buttons = Build(out var stale);
            int hr = _taskbar.ThumbBarAddButtons(hwnd, (uint)buttons.Length, buttons);
            string how = "added";
            // The buttons were already there (the message came again without a new taskbar button): update them.
            if (hr < 0 && _added)
            {
                DiagnosticLog.Write($"taskbar buttons: add refused 0x{hr:X8}, already there; updating");
                hr = _taskbar.ThumbBarUpdateButtons(hwnd, (uint)buttons.Length, buttons);
                how = "updated";
            }
            foreach (var icon in stale) DestroyIcon(icon);
            _added = hr >= 0;
            _shown = Describe();
            DiagnosticLog.Write($"taskbar buttons {(hr >= 0 ? how : "not added")} (0x{hr:X8}), icons {_iconSize} px: {_shown}");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or EntryPointNotFoundException)
        {
            DiagnosticLog.Write($"taskbar buttons not added: {ex.GetType().Name} 0x{ex.HResult:X8}");
        }
    }

    private void Apply(bool force)
    {
        if (_taskbar is null || _source is null) return;
        string now = Describe();
        int size = IconSize();
        if (!force && now == _shown && size == _iconSize) return;
        var buttons = Build(out var stale);
        int hr = _taskbar.ThumbBarUpdateButtons(_source.Handle, (uint)buttons.Length, buttons);
        foreach (var icon in stale) DestroyIcon(icon); // icons of the old size, now replaced on the taskbar
        _shown = now;
        DiagnosticLog.Write($"taskbar buttons{(hr < 0 ? $" not updated (0x{hr:X8})" : "")}: {now}");
    }

    private string Describe() => string.Join(", ", _views.Select(v => $"{v.Tooltip}{(v.Enabled ? "" : " (off)")}"));

    /// <summary>The native button list for the current state; icons are drawn for the current size when it changed.</summary>
    private ThumbButtonNative[] Build(out List<IntPtr> stale)
    {
        stale = [];
        int size = IconSize();
        if (size != _iconSize)
        {
            stale.AddRange(_icons.Values);
            _icons.Clear();
            _iconSize = size;
        }
        return _views.Select(v => new ThumbButtonNative
        {
            Mask = ThbIcon | ThbTooltip | ThbFlags,
            Id = (uint)v.Button,
            Icon = IconFor(v.Glyph),
            Tip = v.Tooltip,
            Flags = v.Enabled ? ThbfEnabled : ThbfDisabled,
        }).ToArray();
    }

    private IntPtr IconFor(ThumbGlyph glyph)
    {
        if (!_icons.TryGetValue(glyph, out var icon)) _icons[glyph] = icon = CreateIcon(ThumbBar.Draw(glyph, _iconSize), _iconSize);
        return icon;
    }

    /// <summary>The small-icon size at the window's DPI: 16 px at 100 %, 20 at 125 %, 24 at 150 %, 32 at 200 %.</summary>
    private int IconSize()
    {
        uint dpi = _source is null ? 96 : GetDpiForWindow(_source.Handle);
        if (dpi == 0) dpi = 96;
        int size = GetSystemMetricsForDpi(49 /* SM_CXSMICON */, dpi);
        return size >= 8 ? size : (int)Math.Round(16 * dpi / 96.0);
    }

    /// <summary>A white icon with the given alpha (a 32-bit DIB with an empty mask).</summary>
    private static unsafe IntPtr CreateIcon(byte[] alpha, int size)
    {
        var header = new BitmapInfoHeader
        {
            Size = (uint)sizeof(BitmapInfoHeader), Width = size, Height = -size, Planes = 1, BitCount = 32,
        };
        IntPtr color = CreateDIBSection(IntPtr.Zero, ref header, 0, out var bits, IntPtr.Zero, 0);
        if (color == IntPtr.Zero) return IntPtr.Zero;
        var px = (uint*)bits;
        for (int i = 0; i < size * size; i++) px[i] = ((uint)alpha[i] << 24) | 0x00FFFFFF;
        var empty = new byte[(size + 15) / 16 * 2 * size]; // 1-bit rows, word-aligned, all 0: the alpha decides
        IntPtr mask;
        fixed (byte* m = empty) mask = CreateBitmap(size, size, 1, 1, (IntPtr)m);
        var info = new IconInfo { IsIcon = true, Mask = mask, Color = color };
        IntPtr icon = CreateIconIndirect(ref info);
        DeleteObject(color);
        DeleteObject(mask);
        return icon;
    }

    public void Dispose()
    {
        _source?.RemoveHook(Hook);
        _source = null;
        foreach (var icon in _icons.Values) DestroyIcon(icon);
        _icons.Clear();
        if (_taskbar is not null) { Marshal.ReleaseComObject(_taskbar); _taskbar = null; }
    }

    // ================= ITaskbarList3 =================

    private const uint ThbIcon = 0x2, ThbTooltip = 0x4, ThbFlags = 0x8;
    private const uint ThbfEnabled = 0x0, ThbfDisabled = 0x1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ThumbButtonNative
    {
        public uint Mask;
        public uint Id;
        public uint Bitmap;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Tip;
        public uint Flags;
    }

    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090"), ClassInterface(ClassInterfaceType.None)]
    private class TaskbarListClass;

    /// <summary>ITaskbarList, ITaskbarList2 and ITaskbarList3 up to the thumbnail toolbar calls (shobjidl_core.h order).</summary>
    [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
        void SetProgressState(IntPtr hwnd, int flags);
        void RegisterTab(IntPtr tab, IntPtr mdi);
        void UnregisterTab(IntPtr tab);
        void SetTabOrder(IntPtr tab, IntPtr insertBefore);
        void SetTabActive(IntPtr tab, IntPtr mdi, uint reserved);
        [PreserveSig] int ThumbBarAddButtons(IntPtr hwnd, uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] ThumbButtonNative[] buttons);
        [PreserveSig] int ThumbBarUpdateButtons(IntPtr hwnd, uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] ThumbButtonNative[] buttons);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        [MarshalAs(UnmanagedType.Bool)] public bool IsIcon;
        public int HotspotX, HotspotY;
        public IntPtr Mask, Color;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint message, uint action, IntPtr changeInfo);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfoHeader header, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitCount, IntPtr bits);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref IconInfo info);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}
