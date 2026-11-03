namespace GtTelemetry.Core.Display;

/// <summary>
/// Minimal line-preserving editor for PCSX2's INI files: comments, order and untouched keys stay as they were. Keys can
/// repeat ([Cheats] has one <c>Enable = name</c> line per cheat).
/// </summary>
public sealed class IniDocument
{
    private readonly List<string> lines;
    private readonly string newline;

    public IniDocument(string text)
    {
        newline = text.Contains("\r\n") || text.Length == 0 ? "\r\n" : "\n";
        lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
    }

    public static IniDocument Load(string path) => new(File.Exists(path) ? File.ReadAllText(path) : "");

    public override string ToString() => lines.Count == 0 ? "" : string.Join(newline, lines) + newline;

    public IReadOnlyList<string> GetAll(string section, string key) =>
        Entries(section).Where(e => e.Key == key).Select(e => e.Value).ToList();

    public string? Get(string section, string key) => GetAll(section, key).FirstOrDefault();

    /// <summary>Sets a single-valued key, replacing every existing line for it.</summary>
    public void Set(string section, string key, string value)
    {
        RemoveWhere(section, (k, _) => k == key);
        Add(section, key, value);
    }

    /// <summary>Adds a key line at the end of the section, creating the section if needed.</summary>
    public void Add(string section, string key, string value)
    {
        var (start, end) = Section(section);
        string line = $"{key} = {value}";
        if (start < 0)
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
            lines.Add($"[{section}]");
            lines.Add(line);
            return;
        }
        int at = end;
        while (at > start + 1 && lines[at - 1].Trim().Length == 0) at--;
        lines.Insert(at, line);
    }

    public void RemoveWhere(string section, Func<string, string, bool> match)
    {
        var (start, end) = Section(section);
        if (start < 0) return;
        for (int i = end - 1; i > start; i--)
            if (Parse(lines[i]) is { } kv && match(kv.Key, kv.Value)) lines.RemoveAt(i);
    }

    private IEnumerable<(string Key, string Value)> Entries(string section)
    {
        var (start, end) = Section(section);
        if (start < 0) yield break;
        for (int i = start + 1; i < end; i++)
            if (Parse(lines[i]) is { } kv) yield return kv;
    }

    /// <summary>Header line index and the index just past the section's last line, or (-1, -1).</summary>
    private (int Start, int End) Section(string section)
    {
        int start = lines.FindIndex(l => l.Trim() == $"[{section}]");
        if (start < 0) return (-1, -1);
        int end = start + 1;
        while (end < lines.Count && !lines[end].TrimStart().StartsWith('[')) end++;
        return (start, end);
    }

    private static (string Key, string Value)? Parse(string line)
    {
        var t = line.Trim();
        if (t.Length == 0 || t[0] is ';' or '#') return null;
        int eq = t.IndexOf('=');
        return eq <= 0 ? null : (t[..eq].Trim(), t[(eq + 1)..].Trim());
    }
}
