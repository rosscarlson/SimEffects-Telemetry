using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using GtTelemetry.App.Input;
using GtTelemetry.App.Theming;
using GtTelemetry.Core.Azimuth;

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

    /// <summary>Camera action → controller button, "VID:PID|button". None by default.</summary>
    public Dictionary<string, string> CameraButtons { get; set; } = [];

    /// <summary>Seat and FOV per car and view, keyed "adapter|game seat position".</summary>
    public Dictionary<string, CameraProfile> CameraProfiles { get; set; } = [];

    /// <summary>FH2/FH3 camera changes per view, keyed "game/view" (Azimuth Telemetry's CameraAdjust).</summary>
    public Dictionary<string, CameraAdjust> AzimuthCamera { get; set; } = [];

    /// <summary>Starts and stops telemetry from anywhere: a key (system-wide) or a controller button
    /// ("VID:PID|button"). Whichever was set last.</summary>
    public HotkeyBinding StartStopKey { get; set; } = new();

    public string? StartStopButton { get; set; }

    /// <summary>Say "telemetry on/off" when the start/stop hotkey is used.</summary>
    public bool AnnounceToggle { get; set; } = true;

    /// <summary>Where azimuth.exe is, when this app isn't next to it.</summary>
    public string? AzimuthFolder { get; set; }

    /// <summary>Render Aspect Ratio size for Azimuth.</summary>
    public int? AspectWidth { get; set; }

    public int? AspectHeight { get; set; }

    /// <summary>Azimuth Telemetry's settings were carried over (once).</summary>
    public bool ImportedAzimuthTelemetry { get; set; }

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
            var s = File.Exists(FilePath) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings() : new AppSettings();
            s.StartStopKey ??= new HotkeyBinding();
            s.AzimuthCamera ??= [];
            if (!s.ImportedAzimuthTelemetry) s.ImportAzimuthTelemetry();
            return s;
        }
        catch
        {
            // Corrupt or from a future version: fall back to defaults.
        }
        return new AppSettings();
    }

    /// <summary>The Forza Horizon games used to have their own app, Azimuth Telemetry. Carries its Azimuth folder,
    /// render size, camera changes and start/stop hotkey over the first time. Its camera keys aren't: the camera card is
    /// shared by every game now, and its defaults were on the number pad.</summary>
    private void ImportAzimuthTelemetry()
    {
        ImportedAzimuthTelemetry = true;
        string old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AzimuthTelemetry", "settings.json");
        if (!File.Exists(old)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(old));
            var r = doc.RootElement;
            string? Str(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            int? Int(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
            AzimuthFolder ??= Str("AzimuthFolder");
            AspectWidth ??= Int("AspectWidth");
            AspectHeight ??= Int("AspectHeight");
            if (r.TryGetProperty("AnnounceToggle", out var say) && say.ValueKind == JsonValueKind.False) AnnounceToggle = false;
            if (r.TryGetProperty("CameraAdjust", out var cam))
                foreach (var e in cam.EnumerateObject())
                    if (e.Value.Deserialize<CameraAdjust>(Json) is { IsZero: false } a) AzimuthCamera.TryAdd(e.Name, a);
            if (r.TryGetProperty("Hotkey", out var hk) && hk.Deserialize<HotkeyBinding>(Json) is { } h && StartStopKey.Kind == HotkeyKind.None && StartStopButton == null)
            {
                if (h.Kind == HotkeyKind.Keyboard) StartStopKey = h;
                // Azimuth Telemetry numbered buttons from 0; the camera card's PadButton counts from 1.
                else if (h.Kind == HotkeyKind.Joystick) StartStopButton = $"{h.JoyVendor:X4}:{h.JoyProduct:X4}|{h.Button + 1}";
            }
        }
        catch
        {
            // Nothing worth stopping for: the settings can be set again.
        }
        Save();
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
