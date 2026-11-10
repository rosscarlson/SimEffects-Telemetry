using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace GtTelemetry.App.Input;

/// <summary>
/// NFSU2's Chrome extension, built into the app and written out to a folder in Downloads that Chrome loads it from (Load
/// unpacked).
/// Written each time the app starts, so an app update brings the new extension; Chrome picks it up when it's reloaded.
/// </summary>
public static class ChromeExtension
{
    private const string Prefix = "Nfsu2Extension.";

    /// <summary>In Downloads, where people look for it. Deleting it turns the extension off in Chrome; the app writes it
    /// again when it starts.</summary>
    public static string Folder => Path.Combine(Downloads(), "SimEffects Telemetry Chrome Extension");

    private static string Downloads()
    {
        try
        {
            var downloads = new Guid("374DE290-123F-4565-9164-39C4925E467B");   // FOLDERID_Downloads
            if (SHGetKnownFolderPath(downloads, 0, IntPtr.Zero, out var path) == 0) return path;
        }
        catch { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out string path);

    /// <summary>Writes the extension's files to <see cref="Folder"/>. False if it couldn't.</summary>
    public static bool Write()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var asm = Assembly.GetExecutingAssembly();
            foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith(Prefix)))
            {
                using var src = asm.GetManifestResourceStream(name)!;
                using var dst = File.Create(Path.Combine(Folder, name[Prefix.Length..]));
                src.CopyTo(dst);
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>Opens Chrome's extensions page. False if Chrome wasn't found.</summary>
    public static bool OpenChrome()
    {
        try
        {
            Process.Start(new ProcessStartInfo("chrome", "chrome://extensions") { UseShellExecute = true });
            return true;
        }
        catch { return false; }
    }
}
