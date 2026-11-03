using System.Diagnostics;

namespace GtTelemetry.Core.Display;

/// <summary>Finds PCSX2's data folder (the one holding inis, cheats and gamesettings).</summary>
public static class Pcsx2Folders
{
    /// <summary>
    /// The configured folder if it's valid (either the data folder or the install folder), otherwise the folder of the
    /// running PCSX2, otherwise Documents\PCSX2. Null if none of them looks like PCSX2.
    /// </summary>
    public static string? Find(string? configured)
    {
        foreach (var dir in Candidates(configured))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var data = DataFolderFor(dir);
            if (Directory.Exists(Path.Combine(data, "inis"))) return data;
        }
        return null;
    }

    /// <summary>Portable installs (portable.txt next to the exe) keep their data beside the exe; others use Documents\PCSX2.</summary>
    public static string DataFolderFor(string folder)
    {
        if (Directory.Exists(Path.Combine(folder, "inis"))) return folder;
        if (File.Exists(Path.Combine(folder, "portable.txt"))) return folder;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PCSX2");
    }

    public static bool IsRunning() => Process.GetProcesses().Any(p => p.ProcessName.StartsWith("pcsx2", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string?> Candidates(string? configured)
    {
        yield return configured;
        foreach (var p in Process.GetProcesses().Where(p => p.ProcessName.StartsWith("pcsx2", StringComparison.OrdinalIgnoreCase)))
        {
            string? exe = null;
            try { exe = p.MainModule?.FileName; }
            catch (Exception) { /* access denied (elevated PCSX2): skip */ }
            if (exe != null) yield return Path.GetDirectoryName(exe);
        }
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PCSX2");
    }
}
