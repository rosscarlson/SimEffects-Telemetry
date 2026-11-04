using System.Text;

namespace GtTelemetry.Core.Display;

/// <summary>
/// The installed version of a PS3 game in RPCS3: APP_VER in dev_hdd0/game/&lt;title ID&gt;/PARAM.SFO, which the latest
/// installed update writes. The app's memory layouts and display patches are for one version of each game.
/// </summary>
public static class Ps3GameVersion
{
    /// <summary>The version each PS3 adapter needs, by adapter ID.</summary>
    public static readonly IReadOnlyDictionary<string, string> Required = new Dictionary<string, string>
    {
        ["gt5"] = "02.11",
        ["gt6"] = "01.05",
    };

    /// <summary>APP_VER for the title, or null if it isn't installed in RPCS3's dev_hdd0 or can't be read.</summary>
    public static string? Read(string rpcs3Folder, string titleId)
    {
        var file = Path.Combine(rpcs3Folder, "dev_hdd0", "game", titleId, "PARAM.SFO");
        try { return File.Exists(file) ? ReadSfo(File.ReadAllBytes(file), "APP_VER") : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>A string value from a PSF file: header, then 16-byte index entries (key offset, format, length, max
    /// length, data offset), all little-endian.</summary>
    public static string? ReadSfo(byte[] data, string key)
    {
        if (data.Length < 20 || data[0] != 0 || data[1] != 'P' || data[2] != 'S' || data[3] != 'F') return null;
        int keys = BitConverter.ToInt32(data, 8), values = BitConverter.ToInt32(data, 12), count = BitConverter.ToInt32(data, 16);
        for (int i = 0; i < count; i++)
        {
            int e = 20 + i * 16;
            if (e + 16 > data.Length) return null;
            int k = keys + BitConverter.ToUInt16(data, e), len = BitConverter.ToInt32(data, e + 4), v = values + BitConverter.ToInt32(data, e + 12);
            if (k >= data.Length) return null;
            int end = Array.IndexOf(data, (byte)0, k);
            if (end < 0 || Encoding.ASCII.GetString(data, k, end - k) != key) continue;
            if (v < 0 || len < 0 || v + len > data.Length) return null;
            return Encoding.UTF8.GetString(data, v, len).TrimEnd('\0');
        }
        return null;
    }
}
