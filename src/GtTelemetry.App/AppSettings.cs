using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using GtTelemetry.App.Theming;

namespace GtTelemetry.App;

public sealed class WatchSetting
{
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public string Type { get; set; } = "f32";
}

/// <summary>A saved seat (metres from the game's own: right, up, forward) and FOV for one car's view.</summary>
public sealed class CameraProfile
{
    public string View { get; set; } = "";
    public float Right { get; set; }
    public float Up { get; set; }
    public float Forward { get; set; }
    /// <summary>The FOV multiplier's immediate (0x3F80 = 1.0), or null for the game's FOV.</summary>
    public ushort? Fov { get; set; }
    public DateTime Saved { get; set; }
}

/// <summary>Persisted to %APPDATA%\SimEffectsTelemetry\settings.json.</summary>
public sealed class AppSettings
{
    public ThemeChoice Theme { get; set; } = ThemeChoice.Dark;

    public string AdapterId { get; set; } = "gt4-spec2";

    public int PineSlot { get; set; } = 28011;

    public bool FastSnapshots { get; set; } = true;

    public bool AutoConnect { get; set; } = true;

    public bool SpeakInstructions { get; set; } = true;

    public string? Voice { get; set; }

    public int VoiceRate { get; set; } = 1;

    public bool SaveSnapshots { get; set; }

    public string? LastSession { get; set; }

    public string TelemetryHost { get; set; } = "127.0.0.1";

    public int TelemetryPort { get; set; } = 8000;

    public string TelemetryFormat { get; set; } = "Horizon";

    public int TelemetryHz { get; set; } = 60;

    /// <summary>Start streaming as soon as the app opens.</summary>
    public bool TelemetryAutoStart { get; set; } = true;

    /// <summary>PCSX2's install or data folder; empty means find it from the running PCSX2.</summary>
    public string? Pcsx2Folder { get; set; }

    public List<WatchSetting> Watch { get; set; } = [];

    /// <summary>FOV and seat hotkeys (while PCSX2 has focus).</summary>
    public bool CameraEnabled { get; set; } = true;

    /// <summary>Camera action → key combination, e.g. "Ctrl+Alt+Up". Missing actions use the defaults.</summary>
    public Dictionary<string, string> CameraKeys { get; set; } = [];

    /// <summary>Seat and FOV per car and view, keyed "adapter|game seat position".</summary>
    public Dictionary<string, CameraProfile> CameraProfiles { get; set; } = [];

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SimEffectsTelemetry", "settings.json");

    // Where settings lived while the app was called GT Telemetry; read once if there's nothing at FilePath yet.
    private static string LegacyFilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GTTelemetry", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath) && File.Exists(LegacyFilePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.Copy(LegacyFilePath, FilePath);
            }
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
        }
        catch
        {
            // Corrupt or from a future version: fall back to defaults.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch
        {
            // Settings are a convenience; never crash over them.
        }
    }
}
