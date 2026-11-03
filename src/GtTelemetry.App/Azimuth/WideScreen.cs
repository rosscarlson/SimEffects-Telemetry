using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace GtTelemetry.App.Azimuth;

public enum ShimState { NoFolder, NoAzimuth, NotInstalled, UpToDate, Outdated, Foreign }

public sealed record ScreenRow(int Left, int Top, int Width, int Height, IReadOnlyList<int> Widths)
{
    public bool IsSpan => Widths.Count > 1;
    public string Aspect
    {
        get
        {
            int g = Gcd(Width, Height);
            return g == 0 ? "?" : $"{Width / g}:{Height / g}";
        }
    }
    static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);
}

// Installs the Azimuth-Span shim (AzimuthSpan/, built into this app) as
// dinput8.dll next to azimuth.exe. It lets Azimuth offer the aspect of a row
// of screens (64:9 for a 2560 + 5120 + 2560 triple) and fills them in
// fullscreen. It changes nothing on disk except that one file.
public static class WideScreen
{
    const string DllName = "dinput8.dll";
    const string Marker = "Azimuth-Span ";  // in the shim's log line, so in its bytes

    static byte[]? _shim;
    public static byte[] Shim => _shim ??= ReadShim();

    static byte[] ReadShim()
    {
        using var s = typeof(WideScreen).Assembly.GetManifestResourceStream("AzimuthSpan.dinput8.dll")
            ?? throw new InvalidOperationException("The shim isn't built into this copy of the app.");
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }

    public static string DllPath(string folder) => Path.Combine(folder, DllName);
    public static string LogPath(string folder) => Path.Combine(folder, "azimuth-span.log");

    public static bool IsAzimuthFolder(string? folder) =>
        !string.IsNullOrWhiteSpace(folder) && File.Exists(Path.Combine(folder, "azimuth.exe"));

    // This app's own folder, when it's been put next to azimuth.exe.
    public static string? FolderNextToApp { get; } =
        IsAzimuthFolder(AppContext.BaseDirectory) ? Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory) : null;

    // Azimuth's folder: this app's own folder when azimuth.exe is beside it,
    // else the saved one (picked with Browse, or found from a running azimuth.exe).
    public static string? Folder(string? saved) => FolderNextToApp ?? (IsAzimuthFolder(saved) ? saved : null);

    // ---- the shim's settings: azimuth-span.ini [span] width= / height= ----

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool WritePrivateProfileStringW(string section, string? key, string? value, string file);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern uint GetPrivateProfileIntW(string section, string key, int fallback, string file);

    static string IniPath(string folder) => Path.Combine(folder, "azimuth-span.ini");

    public static (int? width, int? height) ReadSize(string folder)
    {
        string ini = IniPath(folder);
        if (!File.Exists(ini)) return (null, null);
        int w = (int)GetPrivateProfileIntW("span", "width", 0, ini), h = (int)GetPrivateProfileIntW("span", "height", 0, ini);
        return w > 0 && h > 0 ? (w, h) : (null, null);
    }

    // Null removes them (automatic).
    public static void WriteSize(string folder, int? width, int? height)
    {
        string ini = IniPath(folder);
        if (!File.Exists(ini) && width == null) return;
        if (!WritePrivateProfileStringW("span", "width", width?.ToString(), ini)
            || !WritePrivateProfileStringW("span", "height", height?.ToString(), ini))
            throw new IOException(new System.ComponentModel.Win32Exception().Message);
    }

    public static ShimState State(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return ShimState.NoFolder;
        if (!IsAzimuthFolder(folder)) return ShimState.NoAzimuth;
        string path = DllPath(folder);
        if (!File.Exists(path)) return ShimState.NotInstalled;
        byte[] have;
        try { have = File.ReadAllBytes(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ShimState.Foreign; }  // unreadable: don't touch it
        if (have.AsSpan().SequenceEqual(Shim)) return ShimState.UpToDate;
        return IsOurs(have) ? ShimState.Outdated : ShimState.Foreign;
    }

    static bool IsOurs(byte[] dll) => dll.AsSpan().IndexOf(Encoding.ASCII.GetBytes(Marker)) >= 0;

    public static bool AzimuthRunning(string folder)
    {
        string exe = Path.GetFullPath(Path.Combine(folder, "azimuth.exe"));
        foreach (var p in Process.GetProcessesByName("azimuth"))
        {
            try
            {
                if (string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { return true; }  // can't tell: assume it's this one
            finally { p.Dispose(); }
        }
        return false;
    }

    // Returns null on success, else the reason in a sentence.
    public static string? Install(string folder)
    {
        if (State(folder) == ShimState.Foreign)
            return "There's another dinput8.dll there (another mod?), so it was left alone.";
        if (AzimuthRunning(folder)) return "Close Azimuth first; it has the file open.";
        try
        {
            string temp = DllPath(folder) + ".new";
            File.WriteAllBytes(temp, Shim);
            File.Move(temp, DllPath(folder), overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Couldn't write {DllName}: {ex.Message}";
        }
    }

    public static string? Remove(string folder)
    {
        var state = State(folder);
        if (state == ShimState.Foreign) return "That dinput8.dll isn't this app's, so it was left alone.";
        if (state is not (ShimState.UpToDate or ShimState.Outdated)) return null;
        if (AzimuthRunning(folder)) return "Close Azimuth first; it has the file open.";
        try
        {
            File.Delete(DllPath(folder));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Couldn't delete {DllName}: {ex.Message}";
        }
    }

    public static string? FindRunningAzimuth()
    {
        foreach (var p in Process.GetProcessesByName("azimuth"))
        {
            try
            {
                string? exe = p.MainModule?.FileName;
                if (exe != null) return Path.GetDirectoryName(exe);
            }
            catch { }
            finally { p.Dispose(); }
        }
        return null;
    }

    // ---- screens: the same row rule as the shim (primary plus every screen
    // beside it with the same top and height) ----

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }
    delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO info);

    public static ScreenRow? DetectRow()
    {
        var screens = new List<(RECT r, bool primary)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (m, _, _, _) =>
        {
            var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfoW(m, ref info)) screens.Add((info.Monitor, (info.Flags & 1) != 0));
            return true;
        }, IntPtr.Zero);
        var primary = screens.FirstOrDefault(s => s.primary);
        if (!primary.primary) return null;

        var row = new List<RECT> { primary.r };
        RECT span = primary.r;
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (var (r, _) in screens)
            {
                if (r.Top != span.Top || r.Bottom != span.Bottom) continue;
                if (r.Right == span.Left) { span.Left = r.Left; row.Add(r); grew = true; }
                else if (r.Left == span.Right) { span.Right = r.Right; row.Add(r); grew = true; }
            }
        }
        var widths = row.OrderBy(r => r.Left).Select(r => r.Right - r.Left).ToList();
        return new ScreenRow(span.Left, span.Top, span.Right - span.Left, span.Bottom - span.Top, widths);
    }
}
