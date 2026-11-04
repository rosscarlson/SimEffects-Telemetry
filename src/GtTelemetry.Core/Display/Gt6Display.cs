using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using GtTelemetry.Core.Memory;

namespace GtTelemetry.Core.Display;

/// <param name="Aspect">The width the game draws for: 16/9 as it ships.</param>
public sealed record Ps3DisplayMode(string Id, string Name, string Description, float Aspect)
{
    public override string ToString() => Name;
}

/// <summary>
/// GT6 display modes. The game keeps its screen shape in one float of a display block on its heap (found 2026-11-04 on
/// 1.05: 1280 × 720 twice, then the aspect twice, then 1, 0.5, 0.5, 1.0). Changing the first aspect widens the 3D view,
/// the mirror and the HUD canvas at once, with the HUD kept 16:9 in the middle. The game sets it to 16:9 at boot and
/// leaves it alone after, so it's written once per boot (see <see cref="Gt6AspectWriter"/>). RPCS3 must stretch the
/// picture to the window, which is a per-game RPCS3 config (see <see cref="ApplyConfig"/>).
/// </summary>
public static class Gt6Display
{
    public static readonly IReadOnlyList<Ps3DisplayMode> Modes =
    [
        new("16:9", "16:9 (standard)", "The game as it ships.", 16f / 9),
        new("21:9", "21:9 ultrawide", "Wider 3D view, HUD in the middle. For 3440×1440 and 2560×1080.", 43f / 18),
        new("32:9", "32:9 super ultrawide", "Wider 3D view, HUD in the middle. For 5120×1440.", 32f / 9),
    ];

    public static Ps3DisplayMode? ForId(string? id) => Modes.FirstOrDefault(m => m.Id == id);

    public const string StretchKey = "Stretch To Display Area";

    /// <summary>RPCS3's own fixes for GT6 (its config database, 2026-11-04), used if its copy of the database is missing.
    /// RPCS3 drops these once a game has a custom config, and GT6 flickers without them.</summary>
    private static readonly (string Section, string Key, string Value)[] KnownFixes =
    [
        ("Core", "Accurate RSX reservation access", "true"),
        ("Video", "Read Color Buffers", "true"),
        ("Video", "Write Color Buffers", "true"),
    ];

    /// <summary>RPCS3's folder (the one with config\config.yml): the configured one if valid, else the running RPCS3's.</summary>
    public static string? FindRpcs3Folder(string? configured)
    {
        if (IsRpcs3Folder(configured)) return configured;
        foreach (var p in Process.GetProcessesByName("rpcs3"))
        {
            using (p)
            {
                string? exe = null;
                try { exe = p.MainModule?.FileName; }
                catch (Exception) { /* access denied (elevated RPCS3): skip */ }
                var dir = exe == null ? null : Path.GetDirectoryName(exe);
                if (IsRpcs3Folder(dir)) return dir;
            }
        }
        return null;
    }

    private static bool IsRpcs3Folder(string? dir) =>
        !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, "config", "config.yml"));

    public static string ConfigPath(string rpcs3Folder, string titleId) =>
        Path.Combine(rpcs3Folder, "config", "custom_configs", $"config_{titleId}.yml");

    /// <summary>Whether the game's RPCS3 config stretches to the window (null: no custom config, so it doesn't).</summary>
    public static bool? ReadStretch(string rpcs3Folder, string titleId)
    {
        var path = ConfigPath(rpcs3Folder, titleId);
        if (!File.Exists(path)) return null;
        return YamlLines.Get(File.ReadAllText(path), ["Video", StretchKey]) == "true";
    }

    /// <summary>
    /// Writes the game's RPCS3 config: stretched for the wide modes, not for 16:9. A new one starts as a copy of the global
    /// config, as RPCS3's own "Create custom configuration" does. Either way RPCS3's database fixes for the game go in,
    /// since RPCS3 stops applying them once a custom config exists. RPCS3 reads it when the game boots.
    /// </summary>
    public static DisplayApplyResult ApplyConfig(string rpcs3Folder, string titleId, Ps3DisplayMode mode)
    {
        var path = ConfigPath(rpcs3Folder, titleId);
        var old = File.Exists(path) ? File.ReadAllText(path) : null;
        var text = old ?? File.ReadAllText(Path.Combine(rpcs3Folder, "config", "config.yml"));
        foreach (var (section, key, value) in DatabaseFixes(rpcs3Folder, titleId))
            text = YamlLines.Set(text, [section, key], value);
        text = YamlLines.Set(text, ["Video", StretchKey], mode.Id == "16:9" ? "false" : "true");

        var changed = new List<string>();
        var backups = new List<string>();
        if (text != old)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (old != null)
            {
                var backup = $"{path}.bak-{DateTime.Now:yyyyMMdd-HHmmss}";
                for (int i = 2; File.Exists(backup); i++) backup = $"{path}.bak-{DateTime.Now:yyyyMMdd-HHmmss}-{i}";
                File.Copy(path, backup);
                backups.Add(backup);
            }
            File.WriteAllText(path, text);
            changed.Add(path);
        }
        return new DisplayApplyResult(changed, backups);
    }

    /// <summary>The game's settings from RPCS3's config database (GuiConfigs\config_database.dat), or the known GT6 fixes
    /// if it can't be read. List values (library overrides) are left out.</summary>
    public static IReadOnlyList<(string Section, string Key, string Value)> DatabaseFixes(string rpcs3Folder, string titleId)
    {
        try
        {
            var file = Path.Combine(rpcs3Folder, "GuiConfigs", "config_database.dat");
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.GetProperty("games").TryGetProperty(titleId, out var game)
                && game.TryGetProperty("config", out var config) && config.GetString() is { } yaml)
                return YamlLines.Leaves(yaml).Where(l => l.Path.Length == 2).Select(l => (l.Path[0], l.Path[1], l.Value)).ToList();
            return [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return KnownFixes;
        }
    }
}

/// <summary>
/// Sets GT6's aspect in RPCS3's memory. The display block moves with the heap, so it's found by its contents and checked
/// before every write; a search runs at most every few seconds while it's missing (booting, or not GT6).
/// </summary>
public sealed class Gt6AspectWriter(IMemorySource memory, IRegionSource regions)
{
    private const int SearchEveryMs = 5000;
    private static readonly byte[] Tail = [0, 0, 0, 1, 0x3F, 0, 0, 0, 0x3F, 0, 0, 0, 0x3F, 0x80, 0, 0];   // 1, 0.5f, 0.5f, 1.0f
    private readonly Stopwatch searchAge = new();

    /// <summary>The aspect's address, 0 until found.</summary>
    public uint Address { get; private set; }

    /// <summary>Finds the block if needed and writes <paramref name="aspect"/> if it differs. Returns the value it found
    /// there before writing, or null if the block isn't there (yet).</summary>
    public float? Apply(float aspect)
    {
        if (!memory.IsConnected) return null;
        if (Address == 0 || !Check(Address, out _))
        {
            Address = 0;
            if (searchAge.IsRunning && searchAge.ElapsedMilliseconds < SearchEveryMs) return null;
            searchAge.Restart();
            Address = Search();
            if (Address == 0) return null;
        }
        if (!Check(Address, out float current)) return null;
        if (Math.Abs(current - aspect) > 1e-4f)
        {
            var b = new byte[4];
            BinaryPrimitives.WriteSingleBigEndian(b, aspect);
            memory.Write(Address, b);
        }
        return current;
    }

    private bool Check(uint address, out float aspect)
    {
        aspect = 0;
        var b = new byte[0x2C];
        try { memory.Read(address - 0x10, b); }
        catch (MemorySourceException) { return false; }
        return Matches(b, 0x10, out aspect);
    }

    /// <summary>The block around the aspect at <paramref name="at"/> in <paramref name="b"/>: width and height twice, the
    /// aspect, the game's own aspect, then 1, 0.5f, 0.5f, 1.0f.</summary>
    public static bool Matches(ReadOnlySpan<byte> b, int at, out float aspect)
    {
        aspect = 0;
        if (at < 0x10 || at + 0x1C > b.Length || !b.Slice(at + 8, 16).SequenceEqual(Tail)) return false;
        uint w = BinaryPrimitives.ReadUInt32BigEndian(b[(at - 0x10)..]), h = BinaryPrimitives.ReadUInt32BigEndian(b[(at - 0xC)..]);
        if (w is < 320 or > 4096 || h is < 240 or > 4096) return false;
        if (BinaryPrimitives.ReadUInt32BigEndian(b[(at - 8)..]) != w || BinaryPrimitives.ReadUInt32BigEndian(b[(at - 4)..]) != h) return false;
        float own = BinaryPrimitives.ReadSingleBigEndian(b[(at + 4)..]);
        aspect = BinaryPrimitives.ReadSingleBigEndian(b[at..]);
        return own is > 1f and < 2f && aspect is > 1f and < 6f;
    }

    private uint Search()
    {
        const int chunk = 4 << 20, overlap = 0x40;
        var buf = new byte[chunk + overlap];
        foreach (var r in regions.Regions(0x40000000, 0x50000000))
        {
            for (uint off = 0; off < r.Size; off += chunk)
            {
                int len = (int)Math.Min((uint)buf.Length, r.Size - off);
                var span = buf.AsSpan(0, len);
                try { memory.Read(r.Start + off, span); }
                catch (MemorySourceException) when (memory.IsConnected) { continue; }
                for (int i = span.IndexOf(Tail); i >= 0;)
                {
                    int at = i - 8;
                    // A block whose sizes start in this chunk is found here (the overlap holds the rest).
                    if (at < chunk + 0x10 && Matches(span, at, out _)) return r.Start + off + (uint)at;
                    int next = span[(i + 4)..].IndexOf(Tail);
                    i = next < 0 ? -1 : i + 4 + next;
                }
            }
        }
        return 0;
    }
}

/// <summary>Reads and sets plain "Key: value" lines in an indented YAML file like RPCS3's config, keeping everything else
/// as it is. Not a YAML parser: no flow style, and list items are skipped.</summary>
public static class YamlLines
{
    private static (int Indent, string Key, string Value)? Parse(string line)
    {
        int indent = line.Length - line.TrimStart(' ').Length;
        var t = line.Trim();
        if (t.Length == 0 || t.StartsWith('#') || t.StartsWith('-')) return null;
        int colon = t.IndexOf(": ", StringComparison.Ordinal);
        if (colon < 0 && t.EndsWith(':')) return (indent, t[..^1], "");
        return colon < 0 ? null : (indent, t[..colon], t[(colon + 2)..]);
    }

    /// <summary>Every key with a value, with its path of parent keys.</summary>
    public static IEnumerable<(string[] Path, string Value)> Leaves(string text)
    {
        var stack = new List<(int Indent, string Key)>();
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (Parse(line) is not { } p) continue;
            while (stack.Count > 0 && stack[^1].Indent >= p.Indent) stack.RemoveAt(stack.Count - 1);
            if (p.Value.Length == 0) stack.Add((p.Indent, p.Key));
            else yield return ([.. stack.Select(s => s.Key), p.Key], p.Value);
        }
    }

    public static string? Get(string text, string[] path) =>
        Leaves(text).FirstOrDefault(l => l.Path.SequenceEqual(path)).Value;

    /// <summary>Sets the value at <paramref name="path"/>, adding the key (and its sections) at the end of its section if
    /// it isn't there.</summary>
    public static string Set(string text, string[] path, string value)
    {
        string nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        int start = 0, end = lines.Count, indent = 0;
        for (int depth = 0; depth < path.Length; depth++)
        {
            bool leaf = depth == path.Length - 1;
            int found = -1, childIndent = -1;
            for (int i = start; i < end; i++)
            {
                if (Parse(lines[i]) is not { } p) continue;
                if (childIndent < 0) childIndent = p.Indent;
                if (p.Indent < childIndent) break;
                if (p.Indent == childIndent && p.Key == path[depth]) { found = i; break; }
            }
            if (childIndent < 0) childIndent = indent;
            if (found < 0)
            {
                // Insert after the section's last line (before trailing blank lines at the end of the file).
                int at = end;
                while (at > start && lines[at - 1].Trim().Length == 0) at--;
                var add = new List<string>();
                for (int d = depth; d < path.Length; d++)
                    add.Add(new string(' ', childIndent + (d - depth) * 2) + path[d] + (d == path.Length - 1 ? ": " + value : ":"));
                lines.InsertRange(at, add);
                return string.Join(nl, lines);
            }
            if (leaf)
            {
                var line = lines[found];
                int colon = line.IndexOf(path[depth] + ":", StringComparison.Ordinal) + path[depth].Length + 1;
                lines[found] = line[..colon] + " " + value;
                return string.Join(nl, lines);
            }
            // Narrow to the section's children: the lines after it, until one that's no deeper than it.
            indent = childIndent + 2;
            start = found + 1;
            int stop = start;
            while (stop < end && (Parse(lines[stop]) is not { } q ? true : q.Indent > childIndent)) stop++;
            end = stop;
        }
        return text;
    }
}
