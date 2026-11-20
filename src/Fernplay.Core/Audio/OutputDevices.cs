using NAudio.CoreAudioApi;

namespace Fernplay.Core.Audio;

/// <summary>A playback device Windows knows: its endpoint ID (what Fernplay saves), name, and whether it is connected.</summary>
public sealed record AudioDevice(string Id, string Name, bool Connected);

/// <summary>Where the list of playback devices comes from (Windows, or a fake in tests).</summary>
public interface IAudioDeviceSource
{
    /// <summary>Every playback device Windows knows, connected or not.</summary>
    IReadOnlyList<AudioDevice> List();
}

/// <summary>
/// The choice of playback device: the Settings list, and which device to play on. A saved ID that is empty or not
/// connected means the Windows default; it is kept, so the device is used again once it is back.
/// </summary>
public static class OutputDevices
{
    public const string DefaultName = "Windows default (follows changes)";

    /// <summary>One line of the Settings list; <see cref="Id"/> null = the Windows default.</summary>
    public sealed record Entry(string? Id, string Name)
    {
        public override string ToString() => Name; // what UI Automation and screen readers read for a list item
    }

    public static string? Clean(string? id) => string.IsNullOrWhiteSpace(id) ? null : id.Trim();

    /// <summary>The device to play on: the saved one while it is connected, else null (the Windows default).</summary>
    public static string? Resolve(string? saved, IReadOnlyList<AudioDevice> devices) =>
        Clean(saved) is { } id && devices.Any(d => d.Connected && SameId(d.Id, id)) ? id : null;

    /// <summary>
    /// The Settings list: the Windows default first, then each connected device by name, then the saved device when
    /// it is not connected, marked so ("(not connected)"), so the choice shows and is not lost.
    /// </summary>
    public static IReadOnlyList<Entry> Entries(string? saved, IReadOnlyList<AudioDevice> devices)
    {
        var list = new List<Entry> { new(null, DefaultName) };
        list.AddRange(devices.Where(d => d.Connected)
            .DistinctBy(d => d.Id, StringComparer.OrdinalIgnoreCase)
            .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(d => new Entry(d.Id, d.Name)));
        if (Clean(saved) is { } id && !list.Any(e => e.Id is not null && SameId(e.Id, id)))
        {
            var known = devices.FirstOrDefault(d => SameId(d.Id, id));
            list.Add(new Entry(id, $"{(string.IsNullOrWhiteSpace(known?.Name) ? "Saved device" : known.Name)} (not connected)"));
        }
        return list;
    }

    /// <summary>Index of the saved choice in <paramref name="entries"/> (0, the default, when it is not there).</summary>
    public static int IndexOf(IReadOnlyList<Entry> entries, string? saved)
    {
        if (Clean(saved) is not { } id) return 0;
        for (int i = 1; i < entries.Count; i++)
            if (entries[i].Id is { } e && SameId(e, id)) return i;
        return 0;
    }

    private static bool SameId(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Windows' playback devices (Core Audio), and <see cref="Changed"/> when one is added, removed, plugged in or
/// unplugged. Changed comes from a Windows thread, often several times in a row for one plug.
/// </summary>
public sealed class SystemAudioDevices : IAudioDeviceSource, IDisposable
{
    private readonly MMDeviceEnumerator? _enumerator;
    private readonly MMDeviceNotificationClient? _notifications;

    public SystemAudioDevices()
    {
        try
        {
            _enumerator = new MMDeviceEnumerator();
            // On Windows' audio thread: only the event is raised there (no calls back into the audio stack).
            _notifications = _enumerator.CreateNotificationClient(useSynchronizationContext: false);
            _notifications.DeviceStateChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
            _notifications.DeviceAdded += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
            _notifications.DeviceRemoved += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"audio devices: cannot watch for changes: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public event EventHandler? Changed;

    public IReadOnlyList<AudioDevice> List()
    {
        var list = new List<AudioDevice>();
        try
        {
            using var devices = new MMDeviceEnumerator();
            foreach (var device in devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.All))
            {
                using (device)
                {
                    try { list.Add(new AudioDevice(device.ID, device.FriendlyName, device.State == DeviceState.Active)); }
                    catch (Exception) { } // a device that is gone for good may not answer: leave it out
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"audio devices: cannot list them: {ex.GetType().Name}: {ex.Message}");
        }
        return list;
    }

    public void Dispose()
    {
        try
        {
            _notifications?.Dispose();
            _enumerator?.Dispose();
        }
        catch (Exception) { } // closing: nothing to do about it
    }
}
