namespace GtTelemetry.Core.Display;

/// <param name="Pcsx2Aspect">PCSX2's [EmuCore/GS] AspectRatio: "16:9", or "Stretch" so a wider image fills the window.</param>
/// <param name="Cheat">The pnach cheat group that widens the game, or null for the game's own 16:9.</param>
public sealed record DisplayMode(string Id, string Name, string Description, string Pcsx2Aspect, string? Cheat)
{
    public override string ToString() => Name;
}

/// <param name="Closest">The mode nearest the current setup (the same width), to preselect when it's custom.</param>
public sealed record DisplayState(DisplayMode? Mode, string Description, DisplayMode? Closest);

public sealed record DisplayApplyResult(IReadOnlyList<string> Changed, IReadOnlyList<string> Backups);

/// <summary>
/// GT4 Spec II display modes, applied through PCSX2's per-game settings and the community cheat file
/// (Silentwarior112's ultrawide patches). Files are backed up before every change. PCSX2 reads them when the game boots.
/// </summary>
public static class Gt4SpecIIDisplay
{
    public const string Serial = "SCUS-97436";
    public const string Crc = "646B2E29";

    // The community "32:9 Ultrawide Hack" only widens the 3D view, so the HUD and menus stretch, and its "64:9" group
    // pairs aspect 4.0 with the 32:9 HUD values. So the 32:9 and 64:9 groups are generated here from the formulas behind
    // Silentwarior112's 21:9, 32:9 and 48:9 values (see Ultrawide), which reproduce his 32:9 and 48:9 HUD values exactly.
    public const string ManagedCheat = "SimEffects 32:9 Ultrawide (HUD scaled)";
    public const string ManagedCheat64 = "SimEffects 64:9 Triple Screens (HUD scaled)";
    private const string BlockStart = "// ---- SimEffects Telemetry: managed display patches. SimEffects Telemetry rewrites this block; edit outside it. ----";
    private const string BlockEnd = "// ---- end of SimEffects Telemetry block ----";

    // Names from when the app was called GT Telemetry, still recognised and replaced on the next Apply.
    private static readonly Dictionary<string, string> LegacyCheats = new()
    {
        ["GT Telemetry 32:9 Ultrawide (HUD scaled)"] = ManagedCheat,
        ["GT Telemetry 64:9 Triple Screens (HUD scaled)"] = ManagedCheat64,
    };
    private const string LegacyBlockStart = "// ---- GT Telemetry: managed display patches. GT Telemetry rewrites this block; edit outside it. ----";
    private const string LegacyBlockEnd = "// ---- end of GT Telemetry block ----";
    private static readonly string[] ManagedBlock =
    [
        BlockStart,
        .. Ultrawide(ManagedCheat, 32, "True 32:9 in races (5120×1440), HUD and menus rescaled."),
        "",
        .. Ultrawide(ManagedCheat64, 64, "True 64:9 in races (10240×1440), HUD and menus rescaled."),
        BlockEnd,
    ];

    /// <summary>Every cheat that changes the game's shape. Only one may be enabled at a time.</summary>
    private static readonly string[] ShapeCheats =
    [
        "True 16:10 / Steam Deck Patch", "21:9 Ultrawide Hack", "32:9 Ultrawide Hack", "64:9 Ultrawide Hack",
        "48:9 Ultrawide Hack", "48:9 Triple Screen (No menu scaling)", ManagedCheat, ManagedCheat64,
        .. LegacyCheats.Keys,
    ];

    public static readonly IReadOnlyList<DisplayMode> Modes =
    [
        new("16:9", "16:9", "The game's own widescreen, shown at 16:9.", "16:9", null),
        new("21:9", "21:9 ultrawide", "Wider 3D view, HUD and menus rescaled (community patch).", "Stretch", "21:9 Ultrawide Hack"),
        new("32:9", "32:9 super ultrawide", "Wider 3D view, HUD and menus rescaled. For 5120×1440.", "Stretch", ManagedCheat),
        new("48:9", "48:9 triple screens", "Three 16:9 screens as one wide view, e.g. 7680×1440 (community patch).", "Stretch", "48:9 Ultrawide Hack"),
        new("64:9", "64:9 triple screens", "One wide view across triples at 10240×1440, HUD and menus rescaled.", "Stretch", ManagedCheat64),
    ];

    /// <summary>
    /// A pnach group for a width of <paramref name="ninths"/>:9. With a = ninths / 16 (1 at 16:9): 3D aspect a; HUD canvas
    /// −880a + 480 to 444a − 140; HUD text and ModelFaces × 0.75 / a; mirror width 487.5a; menu text (12 / ninths) × 1.0666;
    /// loading spinner ±320a and image ±320 / a. Floats go in lui immediates, so they're rounded to their upper 16 bits.
    /// </summary>
    public static string[] Ultrawide(string name, int ninths, string description)
    {
        double a = ninths / 16.0;
        string Lui(double v) => $"3C01{(BitConverter.SingleToUInt32Bits((float)v) + 0x8000) >> 16:X4}";
        string Word(string addr, double v, string what) => $"patch=1,EE,{addr},word,{Lui(v)} // {what} ({v:0.####})";
        return
        [
            $"[{name}]",
            $"description={description}",
            "author=SimEffects Telemetry, from Silentwarior112's ultrawide patches",
            $"patch=1,EE,0099C3C8,word,{BitConverter.SingleToUInt32Bits((float)a):X8} // 3D rendering aspect ({a:0.####})",
            Word("001C10FC", -880 * a + 480, "HUD canvas left"),
            Word("001C1130", 444 * a - 140, "HUD canvas right"),
            Word("001C519C", 0.75 / a, "race HUD text width"),
            $"patch=1,EE,001EB65C,short,{(int)Math.Round(487.5 * a, MidpointRounding.AwayFromZero):X4} // rear view mirror width ({Math.Round(487.5 * a, MidpointRounding.AwayFromZero)})",
            $"patch=1,EE,2033D7DC,extended,{Lui(12.0 / ninths * 1.0666)} // menu text width ({12.0 / ninths * 1.0666:0.####})",
            "patch=1,EE,2033D7E0,extended,34210000",
            Word("00353604", 0.75 / a, "ModelFaces"),
            Word("001023E8", 320 * a, "loading spinner"),
            Word("00102408", -320 * a, "loading spinner"),
            Word("001028C0", -320 / a, "loading image"),
            Word("001028C8", 320 / a, "loading image"),
            Word("001028B0", -320 * a, "loading image frame"),
            Word("001028B8", 320 * a, "loading image frame"),
        ];
    }

    public static string SettingsPath(string dataFolder) => Path.Combine(dataFolder, "gamesettings", $"{Serial}_{Crc}.ini");

    /// <summary>The existing cheat file for this game (PCSX2 accepts SERIAL_CRC.pnach or CRC.pnach), or where to create one.</summary>
    public static string CheatPath(string dataFolder)
    {
        var withSerial = Path.Combine(dataFolder, "cheats", $"{Serial}_{Crc}.pnach");
        var crcOnly = Path.Combine(dataFolder, "cheats", $"{Crc}.pnach");
        return File.Exists(withSerial) || !File.Exists(crcOnly) ? withSerial : crcOnly;
    }

    public static DisplayState Read(string dataFolder)
    {
        var ini = IniDocument.Load(SettingsPath(dataFolder));
        var aspect = ini.Get("EmuCore/GS", "AspectRatio");
        var shapes = ini.GetAll("Cheats", "Enable").Where(c => ShapeCheats.Contains(c))
                        .Select(c => LegacyCheats.GetValueOrDefault(c, c)).ToList();
        bool cheatsOn = ini.Get("EmuCore", "EnableCheats") == "true";
        var mode = Modes.FirstOrDefault(m => m.Pcsx2Aspect == aspect
                                             && (m.Cheat == null ? shapes.Count == 0 || !cheatsOn : cheatsOn && shapes.Count == 1 && shapes[0] == m.Cheat));
        if (mode != null) return new DisplayState(mode, mode.Name, mode);
        var global = IniDocument.Load(Path.Combine(dataFolder, "inis", "PCSX2.ini")).Get("EmuCore/GS", "AspectRatio");
        bool wide = shapes.Count > 0 && cheatsOn;
        var parts = new List<string>
        {
            aspect != null ? $"PCSX2 aspect {aspect}" : $"PCSX2 aspect {global ?? "default"} (global)",
            wide ? string.Join(", ", shapes) : "no widescreen cheat",
        };
        var closest = wide ? Modes.FirstOrDefault(m => shapes[0].StartsWith(m.Id)) : Modes[0];
        return new DisplayState(null, "Custom: " + string.Join("; ", parts), closest);
    }

    public static DisplayApplyResult Apply(string dataFolder, DisplayMode mode)
    {
        var changed = new List<string>();
        var backups = new List<string>();

        // Keep the managed cheat group in the cheat file; it does nothing unless enabled.
        var cheatPath = CheatPath(dataFolder);
        var cheatText = File.Exists(cheatPath) ? File.ReadAllText(cheatPath) : "";
        var newCheatText = WithManagedBlock(cheatText);
        if (newCheatText != cheatText) Write(cheatPath, newCheatText, changed, backups);

        var settingsPath = SettingsPath(dataFolder);
        var oldIni = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : "";
        var ini = new IniDocument(oldIni);
        ini.Set("EmuCore/GS", "AspectRatio", mode.Pcsx2Aspect);
        ini.RemoveWhere("Cheats", (k, v) => k == "Enable" && ShapeCheats.Contains(v));
        if (mode.Cheat != null)
        {
            ini.Set("EmuCore", "EnableCheats", "true");
            ini.Add("Cheats", "Enable", mode.Cheat);
        }
        var newIni = ini.ToString();
        if (newIni != oldIni) Write(settingsPath, newIni, changed, backups);

        return new DisplayApplyResult(changed, backups);
    }

    private static string WithManagedBlock(string text)
    {
        string nl = text.Contains("\r\n") || text.Length == 0 ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        int start = lines.IndexOf(BlockStart);
        int end = lines.IndexOf(BlockEnd);
        if (start < 0)
        {
            start = lines.IndexOf(LegacyBlockStart);
            end = lines.IndexOf(LegacyBlockEnd);
        }
        if (start >= 0 && end > start)
        {
            lines.RemoveRange(start, end - start + 1);
            lines.InsertRange(start, ManagedBlock);
        }
        else
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
            lines.AddRange(ManagedBlock);
        }
        return string.Join(nl, lines) + nl;
    }

    private static void Write(string path, string text, List<string> changed, List<string> backups)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            var backup = $"{path}.bak-{DateTime.Now:yyyyMMdd-HHmmss}";
            for (int i = 2; File.Exists(backup); i++) backup = $"{path}.bak-{DateTime.Now:yyyyMMdd-HHmmss}-{i}";
            File.Copy(path, backup);
            backups.Add(backup);
        }
        File.WriteAllText(path, text);
        changed.Add(path);
    }
}
