using System.Runtime.InteropServices;

namespace Fernplay.Core;

/// <summary>
/// What a launch asks for: paths to open (replace the list and play) or, with <c>/add</c>, to add to the end.
/// A second Fernplay sends this to the running one (one line per item) and exits.
/// </summary>
public sealed record OpenRequest(bool Add, IReadOnlyList<string> Paths)
{
    public static OpenRequest FromArgs(IEnumerable<string> args, string workingDirectory)
    {
        bool add = false;
        var paths = new List<string>();
        foreach (var a in args)
        {
            if (a.Equals("/add", StringComparison.OrdinalIgnoreCase)) { add = true; continue; }
            if (string.IsNullOrWhiteSpace(a)) continue;
            try { paths.Add(Path.GetFullPath(a, workingDirectory)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        return new OpenRequest(add, paths);
    }

    /// <summary>"open" or "add", then one path per line (paths cannot contain line breaks).</summary>
    public string ToMessage() => string.Join("\n", [Add ? "add" : "open", .. Paths]);

    public static OpenRequest? FromMessage(string message)
    {
        var lines = message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0 || lines[0] is not ("open" or "add")) return null;
        return new OpenRequest(lines[0] == "add", lines[1..]);
    }
}

/// <summary>
/// Opening several files from Explorer starts one Fernplay per file, almost at once, in no fixed order.
/// The requests are gathered until things go quiet, then handled as one: opened files replace the list
/// (in Explorer's name order), added files go to the end.
/// </summary>
public sealed class OpenBatch
{
    private readonly List<string> _open = [];
    private readonly List<string> _add = [];

    public bool IsEmpty => _open.Count == 0 && _add.Count == 0;

    public void Add(OpenRequest request) => (request.Add ? _add : _open).AddRange(request.Paths);

    /// <summary>The gathered paths, each list sorted like Explorer sorts names, duplicates removed; then empty.</summary>
    public (IReadOnlyList<string> Open, IReadOnlyList<string> Add) Take()
    {
        var result = (Sorted(_open), Sorted(_add));
        _open.Clear();
        _add.Clear();
        return result;
    }

    private static List<string> Sorted(List<string> paths) =>
        paths.Distinct(StringComparer.OrdinalIgnoreCase).Order(ExplorerOrder.Instance).ToList();
}

/// <summary>Explorer's name order: numbers inside names compare as numbers ("2" before "10").</summary>
public sealed class ExplorerOrder : IComparer<string>
{
    public static readonly ExplorerOrder Instance = new();
    public int Compare(string? x, string? y) => StrCmpLogicalW(x ?? "", y ?? "");

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string a, string b);
}
