using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Fernplay.App;

/// <summary>
/// Fernplay's entry in Windows' media session (SystemMediaTransportControls): the media keys reach Fernplay while
/// another app has focus, and the Windows media overlay shows the track with its buttons.
/// Hand-written COM interop instead of the Windows SDK projection, which would add about 23 MB to the exe.
/// Interface layouts and GUIDs are from the Windows 10 SDK 10.0.19041 headers (windows.media.h,
/// systemmediatransportcontrolsinterop.h). Slot numbers count from 0; IInspectable takes slots 0 to 5.
/// Any failure leaves the session off and Fernplay works as before (media keys only while it has focus).
/// </summary>
public sealed unsafe class MediaSession : IDisposable
{
    public enum Button { Play = 0, Pause = 1, Stop = 2, Next = 6, Previous = 7 }
    public enum Status { Closed = 0, Stopped = 2, Playing = 3, Paused = 4 }

    private static readonly Guid IidInterop = new("ddb0472d-c911-4a1f-86d9-dc3d71a95f5a");
    private static readonly Guid IidControls = new("99fa3ff4-1742-42a6-902e-087d41f965ec");
    private static readonly Guid IidHandler = new("0557e996-7b23-5bae-aa81-ea0d671143a4"); // ITypedEventHandler<SMTC, ButtonPressedEventArgs>
    private static readonly Guid IidUnknown = new("00000000-0000-0000-c000-000000000046");
    private static readonly Guid IidAgile = new("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90");

    // ISystemMediaTransportControls
    private const int PutPlaybackStatus = 7, GetDisplayUpdater = 8, PutIsEnabled = 11, PutIsPlayEnabled = 13,
        PutIsStopEnabled = 15, PutIsPauseEnabled = 17, PutIsPreviousEnabled = 25, PutIsNextEnabled = 27,
        AddButtonPressed = 32, RemoveButtonPressed = 33;
    // ISystemMediaTransportControlsDisplayUpdater
    private const int PutType = 7, GetMusicProperties = 12, ClearAll = 16, Update = 17;
    // IMusicDisplayProperties
    private const int PutTitle = 7, PutArtist = 11;
    // ISystemMediaTransportControlsButtonPressedEventArgs
    private const int GetButton = 6;

    private IntPtr _controls;
    private IntPtr _display;
    private IntPtr _music;
    private long _token;
    private Handler* _handler;
    private GCHandle _self;

    /// <summary>A media button was pressed (keyboard or overlay). Raised on a background thread.</summary>
    public event Action<Button>? ButtonPressed;

    public bool IsActive => _controls != IntPtr.Zero;

    /// <summary>Joins the media session for a window; returns null when Windows refuses (then nothing changes).</summary>
    public static MediaSession? TryCreate(IntPtr hwnd)
    {
        var s = new MediaSession();
        try
        {
            s.Start(hwnd);
            return s;
        }
        catch (Exception ex) when (ex is COMException or EntryPointNotFoundException or DllNotFoundException or InvalidOperationException)
        {
            s.Dispose();
            return null;
        }
    }

    private void Start(IntPtr hwnd)
    {
        IntPtr factory = IntPtr.Zero;
        var name = "Windows.Media.SystemMediaTransportControls";
        Check(WindowsCreateString(name, (uint)name.Length, out var hName));
        try
        {
            var iid = IidInterop;
            Check(RoGetActivationFactory(hName, ref iid, out factory));
            var controlsIid = IidControls;
            IntPtr controls;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)Slot(factory, 6))(factory, hwnd, &controlsIid, &controls));
            _controls = controls;
        }
        finally
        {
            if (factory != IntPtr.Zero) Marshal.Release(factory);
            WindowsDeleteString(hName);
        }

        PutBool(PutIsEnabled, true);
        PutBool(PutIsPlayEnabled, true);
        PutBool(PutIsPauseEnabled, true);
        PutBool(PutIsStopEnabled, true);
        PutBool(PutIsNextEnabled, true);
        PutBool(PutIsPreviousEnabled, true);

        IntPtr display, music;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(_controls, GetDisplayUpdater))(_controls, &display));
        _display = display;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, int, int>)Slot(_display, PutType))(_display, 1)); // MediaPlaybackType.Music
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(_display, GetMusicProperties))(_display, &music));
        _music = music;

        _self = GCHandle.Alloc(this, GCHandleType.Weak);
        _handler = Handler.Create(GCHandle.ToIntPtr(_self));
        long token;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, long*, int>)Slot(_controls, AddButtonPressed))(_controls, (IntPtr)_handler, &token));
        _token = token;
        SetStatus(Status.Closed);
    }

    public void SetStatus(Status status)
    {
        if (_controls == IntPtr.Zero) return;
        ((delegate* unmanaged[Stdcall]<IntPtr, int, int>)Slot(_controls, PutPlaybackStatus))(_controls, (int)status);
    }

    /// <summary>What the overlay shows. Null title clears it.</summary>
    public void SetTrack(string? title, string? artist)
    {
        if (_display == IntPtr.Zero || _music == IntPtr.Zero) return;
        if (title is null)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(_display, ClearAll))(_display);
            ((delegate* unmanaged[Stdcall]<IntPtr, int, int>)Slot(_display, PutType))(_display, 1);
        }
        else
        {
            PutString(_music, PutTitle, title);
            PutString(_music, PutArtist, artist ?? "");
        }
        ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(_display, Update))(_display);
    }

    private void OnButton(int button)
    {
        if (Enum.IsDefined(typeof(Button), button)) ButtonPressed?.Invoke((Button)button);
    }

    private void PutBool(int slot, bool value) =>
        Check(((delegate* unmanaged[Stdcall]<IntPtr, byte, int>)Slot(_controls, slot))(_controls, value ? (byte)1 : (byte)0));

    private static void PutString(IntPtr obj, int slot, string value)
    {
        if (WindowsCreateString(value, (uint)value.Length, out var h) != 0) return;
        try { ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Slot(obj, slot))(obj, h); }
        finally { WindowsDeleteString(h); }
    }

    private static void* Slot(IntPtr obj, int slot) => (*(void***)obj)[slot];

    private static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    public void Dispose()
    {
        if (_controls != IntPtr.Zero)
        {
            if (_token != 0) ((delegate* unmanaged[Stdcall]<IntPtr, long, int>)Slot(_controls, RemoveButtonPressed))(_controls, _token);
            SetStatus(Status.Closed);
        }
        if (_music != IntPtr.Zero) { Marshal.Release(_music); _music = IntPtr.Zero; }
        if (_display != IntPtr.Zero) { Marshal.Release(_display); _display = IntPtr.Zero; }
        if (_controls != IntPtr.Zero) { Marshal.Release(_controls); _controls = IntPtr.Zero; }
        if (_handler != null) { Handler.Release(_handler); _handler = null; }
        if (_self.IsAllocated) _self.Free();
    }

    /// <summary>
    /// The ButtonPressed event handler as a COM object: IUnknown (QueryInterface, AddRef, Release) plus Invoke.
    /// Agile, so Windows calls it straight from its own thread. Freed when the last reference goes.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Handler
    {
        private void** _vtable;
        private int _refs;
        private IntPtr _owner; // weak GCHandle to the MediaSession

        private static readonly void** Vtable = CreateVtable();

        private static void** CreateVtable()
        {
            var v = (void**)NativeMemory.Alloc((nuint)(4 * sizeof(void*)));
            v[0] = (delegate* unmanaged[Stdcall]<Handler*, Guid*, void**, int>)&QueryInterface;
            v[1] = (delegate* unmanaged[Stdcall]<Handler*, uint>)&AddRef;
            v[2] = (delegate* unmanaged[Stdcall]<Handler*, uint>)&ReleaseRef;
            v[3] = (delegate* unmanaged[Stdcall]<Handler*, IntPtr, IntPtr, int>)&Invoke;
            return v;
        }

        public static Handler* Create(IntPtr owner)
        {
            var h = (Handler*)NativeMemory.AllocZeroed((nuint)sizeof(Handler));
            h->_vtable = Vtable;
            h->_refs = 1;
            h->_owner = owner;
            return h;
        }

        public static void Release(Handler* h)
        {
            h->_owner = IntPtr.Zero; // late calls after Dispose do nothing
            ReleaseRefCore(h);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int QueryInterface(Handler* self, Guid* riid, void** ppv)
        {
            if (*riid == IidUnknown || *riid == IidHandler || *riid == IidAgile)
            {
                *ppv = self;
                Interlocked.Increment(ref self->_refs);
                return 0;
            }
            *ppv = null;
            return unchecked((int)0x80004002); // E_NOINTERFACE
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint AddRef(Handler* self) => (uint)Interlocked.Increment(ref self->_refs);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint ReleaseRef(Handler* self) => ReleaseRefCore(self);

        private static uint ReleaseRefCore(Handler* self)
        {
            int n = Interlocked.Decrement(ref self->_refs);
            if (n == 0) NativeMemory.Free(self);
            return (uint)n;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int Invoke(Handler* self, IntPtr sender, IntPtr args)
        {
            try
            {
                int button;
                if (((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Slot(args, GetButton))(args, &button) < 0) return 0;
                if (self->_owner != IntPtr.Zero && GCHandle.FromIntPtr(self->_owner).Target is MediaSession s) s.OnButton(button);
            }
            catch (Exception) { } // never let an exception cross into Windows
            return 0;
        }
    }

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int WindowsCreateString(string source, uint length, out IntPtr hstring);

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);
}
