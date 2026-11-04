namespace GtTelemetry.Core;

/// <summary>
/// Finds the editable data folders. When running from the source tree (bin/…) we use the repo's sessions/, data/ and
/// captures/ folders so discoveries land in the repo; when installed we use the copies next to the executable.
/// </summary>
public static class ProjectPaths
{
    private static readonly Lazy<string> root = new(FindRoot);

    public static string Root => root.Value;

    public static string Sessions => Path.Combine(Root, "sessions");

    public static string Data => Path.Combine(Root, "data");

    public static string Captures => Path.Combine(Root, "captures");

    public static string AddressFile(string gameFolder) => Path.Combine(Data, gameFolder, "addresses.json");

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var d = dir; d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "SimEffectsTelemetry.sln")))
                return d.FullName;
        return dir.FullName;
    }
}
