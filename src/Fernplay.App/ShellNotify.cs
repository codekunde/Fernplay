using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Fernplay.App;

/// <summary>Small Windows shell calls for the Explorer set-up.</summary>
public static class ShellNotify
{
    /// <summary>Tells Explorer that file types changed, so menus and icons refresh without a sign-out.</summary>
    public static void AssociationsChanged() => SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);

    /// <summary>
    /// Opens Windows' page for choosing Fernplay as the default player. Windows only lets the user make
    /// that choice; an app cannot set itself.
    /// </summary>
    public static void OpenDefaultApps()
    {
        try
        {
            // Windows 10: the "Set defaults by app" page for Fernplay. Windows 11 opens Fernplay's page in Settings.
            var type = Type.GetTypeFromCLSID(new Guid("1968106d-f3b5-44cf-890e-116fcb9ecef1"));
            if (type is not null && Activator.CreateInstance(type) is IApplicationAssociationRegistrationUI ui)
            {
                try
                {
                    if (ui.LaunchAdvancedAssociationUI(Fernplay.Core.ShellRegistration.AppName) >= 0) return;
                }
                finally { Marshal.ReleaseComObject(ui); }
            }
        }
        catch (COMException) { }
        catch (InvalidCastException) { }
        Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true });
    }

    [ComImport, Guid("1f76a169-f994-40ac-8fc8-0959e8874710"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationAssociationRegistrationUI
    {
        [PreserveSig] int LaunchAdvancedAssociationUI([MarshalAs(UnmanagedType.LPWStr)] string appRegName);
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
