using System.Globalization;

namespace GtTelemetry.Core.Display;

/// <summary>
/// GT5 display modes through RPCS3's community "Aspect Ratio" patch (Shy_Doge, for 2.11 on the US and EU discs): it
/// widens the 3D view and keeps the HUD 16:9 in the middle. The patch is turned on and its value chosen in RPCS3's
/// patch_config.yml, and RPCS3 must stretch the picture to the window (the per-game config, as for GT6). Both are read
/// when the game boots, so the app doesn't need to be open.
/// </summary>
public static class Gt5Display
{
    public static readonly IReadOnlyList<Ps3DisplayMode> Modes =
    [
        new("16:9", "16:9 (standard)", "The game as it ships.", 16f / 9),
        new("21:9", "21:9 ultrawide", "Wider 3D view, HUD in the middle. For 3440×1440 and 2560×1080.", 43f / 18),
        new("32:9", "32:9 super ultrawide", "Wider 3D view, HUD in the middle. For 5120×1440.", 32f / 9),
    ];

    public static Ps3DisplayMode? ForId(string? id) => Modes.FirstOrDefault(m => m.Id == id);

    /// <summary>The patch's executable hash for each disc (both 2.11).</summary>
    private static readonly Dictionary<string, string> Hashes = new()
    {
        ["BCUS98114"] = "PPU-d73f342bf28ee016ef3d0ccb309b1acb03d8ecce",
        ["BCES00569"] = "PPU-7a5ee7bc2fef9566dd80e35893fe2c5571197726",
    };

    private const string Patch = "Aspect Ratio", Game = "Gran Turismo 5", Version = "02.11", Values = "Configurable Values";

    /// <summary>The patch's values, written exactly as its allowed values in patch.yml so RPCS3 takes them.</summary>
    private static string? PatchValue(Ps3DisplayMode mode) => mode.Id switch
    {
        "21:9" => "2.388888888888889",
        "32:9" => "3.555555555555556",
        _ => null,
    };

    /// <summary>RPCS3's own fixes for GT5 (its config database, 2026-11-04), used if its copy of the database is missing.</summary>
    private static readonly (string Section, string Key, string Value)[] KnownFixes =
    [
        ("Core", "Accurate RSX reservation access", "true"),
        ("Core", "SPU Block Size", "Mega"),
        ("Core", "Sleep Timers Accuracy", "As Host"),
        ("Video", "Disable Blit Engine Upscaling", "true"),
    ];

    public static string PatchConfigPath(string rpcs3Folder) => Path.Combine(rpcs3Folder, "config", "patch_config.yml");

    private static string[] KeyPath(string titleId, string key) => [Hashes[titleId], Patch, Game, titleId, Version, .. key.Split('/')];

    public static bool Supports(string titleId) => Hashes.ContainsKey(titleId);

    /// <summary>Whether RPCS3 has downloaded the patch (Manage > Game Patches > Download Latest Patches).</summary>
    public static bool PatchDownloaded(string rpcs3Folder, string titleId)
    {
        var file = Path.Combine(rpcs3Folder, "patches", "patch.yml");
        return Supports(titleId) && File.Exists(file) && File.ReadAllText(file).Contains(Hashes[titleId] + ":", StringComparison.Ordinal);
    }

    /// <summary>The mode patch_config.yml sets: 16:9 while the patch is off, else the closest to its value (32:9, the
    /// patch's default, when there's no value).</summary>
    public static Ps3DisplayMode ReadMode(string rpcs3Folder, string titleId)
    {
        var path = PatchConfigPath(rpcs3Folder);
        if (!Supports(titleId) || !File.Exists(path)) return Modes[0];
        var text = File.ReadAllText(path);
        if (YamlLines.Get(text, KeyPath(titleId, "Enabled")) != "true") return Modes[0];
        double value = double.TryParse(YamlLines.Get(text, KeyPath(titleId, $"{Values}/{Patch}")), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v : 32.0 / 9;
        return Modes.Skip(1).MinBy(m => Math.Abs(m.Aspect - value))!;
    }

    /// <summary>Writes the game's RPCS3 config (stretched for the wide modes) and turns the patch on with the mode's value,
    /// or off for 16:9. Files that change are backed up first.</summary>
    public static DisplayApplyResult Apply(string rpcs3Folder, string titleId, Ps3DisplayMode mode)
    {
        if (!Supports(titleId)) throw new IOException($"There's no RPCS3 widescreen patch for {titleId}.");
        var config = Gt6Display.ApplyConfig(rpcs3Folder, titleId, mode, KnownFixes);

        var path = PatchConfigPath(rpcs3Folder);
        var old = File.Exists(path) ? File.ReadAllText(path) : null;
        var text = YamlLines.Set(old ?? "", KeyPath(titleId, "Enabled"), PatchValue(mode) == null ? "false" : "true");
        if (PatchValue(mode) is { } value) text = YamlLines.Set(text, KeyPath(titleId, $"{Values}/{Patch}"), value);
        var changed = config.Changed.ToList();
        var backups = config.Backups.ToList();
        if (text != old)
        {
            if (old != null) backups.Add(Gt6Display.Backup(path));
            File.WriteAllText(path, text);
            changed.Add(path);
        }
        return new DisplayApplyResult(changed, backups);
    }
}
