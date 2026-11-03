using System.ComponentModel;
using System.IO;
using System.Text;
using GtTelemetry.App.Input;

namespace GtTelemetry.App.Azimuth;

// One of Azimuth's wheel bindings (a wheel_* line in data\azimuth.ini).
public sealed class BindingRow : INotifyPropertyChanged
{
    string _value = "";
    bool _capturing;
    Func<string, string>? _describe;

    public required string Key { get; init; }     // without "wheel_"
    public required string Label { get; init; }
    public required CaptureKind Kind { get; init; }

    public string Value
    {
        get => _value;
        set { _value = value ?? ""; Changed(nameof(Value)); Changed(nameof(Display)); Changed(nameof(IsSet)); }
    }
    public bool IsSet => _value.Length > 0;
    public bool IsCapturing
    {
        get => _capturing;
        set { _capturing = value; Changed(nameof(IsCapturing)); Changed(nameof(Display)); }
    }
    public string Display => _capturing ? "Waiting…" : !IsSet ? "Not set" : _describe?.Invoke(_value) ?? _value;

    public void SetDescriber(Func<string, string> describe) { _describe = describe; Changed(nameof(Display)); }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

// Azimuth keeps its wheel bindings in the [launcher] section of
// data\azimuth.ini (UTF-16), shared by every game it runs:
//   wheel_<name>=<pid><vid>:axis:<slot>:<from>:<to>   (from = at rest, to = turned right / pressed)
//   wheel_<name>=<pid><vid>:button:<n>                (n from 0; Azimuth shows n + 1)
//   wheel_<name>=<pid><vid>:hat:<n>:up|down|left|right
// plus wheel_rotation (degrees) and wheel_force (percent).
public static class AzimuthControls
{
    public static readonly (string key, string label, CaptureKind kind)[] Actions =
    {
        ("steering", "Steering", CaptureKind.Steering),
        ("throttle", "Throttle", CaptureKind.Pedal),
        ("brake", "Brake", CaptureKind.Pedal),
        ("clutch", "Clutch", CaptureKind.Pedal),
        ("handbrake", "Handbrake", CaptureKind.Pedal),
        ("a", "A", CaptureKind.Button),
        ("b", "B", CaptureKind.Button),
        ("x", "X", CaptureKind.Button),
        ("y", "Y", CaptureKind.Button),
        ("view", "View", CaptureKind.Button),
        ("menu", "Menu", CaptureKind.Button),
        ("left_paddle", "Left paddle", CaptureKind.Button),
        ("right_paddle", "Right paddle", CaptureKind.Button),
        ("dpad_up", "D-pad up", CaptureKind.Button),
        ("dpad_down", "D-pad down", CaptureKind.Button),
        ("dpad_left", "D-pad left", CaptureKind.Button),
        ("dpad_right", "D-pad right", CaptureKind.Button),
        ("gear_1", "Gear 1", CaptureKind.Button),
        ("gear_2", "Gear 2", CaptureKind.Button),
        ("gear_3", "Gear 3", CaptureKind.Button),
        ("gear_4", "Gear 4", CaptureKind.Button),
        ("gear_5", "Gear 5", CaptureKind.Button),
        ("gear_6", "Gear 6", CaptureKind.Button),
        ("gear_7", "Gear 7", CaptureKind.Button),
        ("reverse", "Reverse", CaptureKind.Button),
    };

    const string Section = "launcher";

    public static string IniPath(string folder) => Path.Combine(folder, "data", "azimuth.ini");

    public static List<BindingRow> NewRows() =>
        Actions.Select(a => new BindingRow { Key = a.key, Label = a.label, Kind = a.kind }).ToList();

    public sealed record Loaded(Dictionary<string, string> Bindings, string? Rotation, string? Force);

    public static Loaded? Load(string folder)
    {
        string path = IniPath(folder);
        if (!File.Exists(path)) return null;
        var values = ReadSection(File.ReadAllLines(path, Encoding.Unicode));
        var bindings = Actions.ToDictionary(a => a.key, a => values.TryGetValue("wheel_" + a.key, out var v) ? v : "");
        return new Loaded(bindings, values.GetValueOrDefault("wheel_rotation"), values.GetValueOrDefault("wheel_force"));
    }

    static Dictionary<string, string> ReadSection(string[] lines)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool inside = false;
        foreach (var raw in lines)
        {
            string line = raw.Trim();
            if (line.StartsWith('[')) { inside = line.Equals($"[{Section}]", StringComparison.OrdinalIgnoreCase); continue; }
            int eq = line.IndexOf('=');
            if (inside && eq > 0) values[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return values;
    }

    // Writes the given keys into [launcher], keeping every other line as it
    // was. The first save keeps a copy of the original file.
    public static void Save(string folder, IReadOnlyDictionary<string, string> keys)
    {
        string path = IniPath(folder);
        var lines = File.ReadAllLines(path, Encoding.Unicode).ToList();
        // Keep the untouched original once (0.1 named it ".before-horizon-telemetry").
        string original = path + ".before-azimuth-telemetry";
        if (!File.Exists(original) && !File.Exists(path + ".before-horizon-telemetry")) File.Copy(path, original);

        int start = lines.FindIndex(l => l.Trim().Equals($"[{Section}]", StringComparison.OrdinalIgnoreCase));
        if (start < 0) { lines.Insert(0, $"[{Section}]"); start = 0; }
        int end = lines.FindIndex(start + 1, l => l.TrimStart().StartsWith('['));
        if (end < 0) end = lines.Count;

        foreach (var (key, value) in keys)
        {
            int at = -1;
            for (int i = start + 1; i < end; i++)
            {
                int eq = lines[i].IndexOf('=');
                if (eq > 0 && lines[i][..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) { at = i; break; }
            }
            if (at >= 0) lines[at] = $"{key}={value}";
            else { lines.Insert(end, $"{key}={value}"); end++; }
        }

        string temp = path + ".new";
        File.WriteAllText(temp, string.Join("\r\n", lines) + "\r\n", Encoding.Unicode);
        File.Move(temp, path, overwrite: true);
    }

    // How far a binding is pressed right now, from the controllers' state: an
    // axis 0-1 (from = 0, to = 1; steering 0.5 is centre), a button or hat
    // direction 0 or 1. Null when its device isn't connected or it doesn't
    // parse. A device listed twice (some wheel bases) counts the copy that's
    // furthest from rest (0, or 0.5 for steering).
    public static double? Evaluate(string value, IReadOnlyList<WheelSnapshot> shots, double rest = 0)
    {
        var parts = value.Split(':');
        if (parts.Length < 3) return null;
        double? best = null;
        foreach (var s in shots)
        {
            if (!s.Device.Id.Equals(parts[0], StringComparison.OrdinalIgnoreCase)) continue;
            double? v = null;
            switch (parts[1])
            {
                case "axis" when parts.Length >= 5 && int.TryParse(parts[2], out int slot) && slot >= 0 && slot < s.Axes.Length
                                 && int.TryParse(parts[3], out int from) && int.TryParse(parts[4], out int to) && from != to:
                    v = Math.Clamp((s.Axes[slot] - from) / (double)(to - from), 0, 1);
                    break;
                case "button" when int.TryParse(parts[2], out int b) && b >= 0 && b < s.Buttons.Length:
                    v = s.Buttons[b] ? 1 : 0;
                    break;
                case "hat" when parts.Length >= 4 && int.TryParse(parts[2], out int h) && h >= 0 && h < s.Povs.Length:
                    v = HatPressed(s.Povs[h], parts[3]) ? 1 : 0;
                    break;
            }
            if (v is double x && (best == null || Math.Abs(x - rest) > Math.Abs(best.Value - rest))) best = x;
        }
        return best;
    }

    // Within 67.5 degrees of the direction, so diagonals light both.
    static bool HatPressed(int pov, string dir)
    {
        if (pov < 0 || (pov & 0xFFFF) == 0xFFFF) return false;
        int want = dir switch { "up" => 0, "right" => 9000, "down" => 18000, "left" => 27000, _ => -1 };
        if (want < 0) return false;
        int d = Math.Abs(pov - want) % 36000;
        return Math.Min(d, 36000 - d) <= 6750;
    }

    // "Simjack-Pedals · Z Axis · 0 → 65535" from "57572497:axis:2:0:65535".
    public static string Describe(string value, IReadOnlyList<WheelDevice> devices)
    {
        var parts = value.Split(':');
        if (parts.Length < 3) return value;
        var dev = devices.FirstOrDefault(d => d.Id.Equals(parts[0], StringComparison.OrdinalIgnoreCase));
        string name = dev?.Name ?? $"Device {parts[0]} (not connected)";
        switch (parts[1])
        {
            case "axis" when parts.Length >= 5 && int.TryParse(parts[2], out int slot):
                string axis = dev != null && dev.AxisNames.TryGetValue(slot, out var n) ? n : $"axis slot {slot}";
                return $"{name} · {axis} · {parts[3]} → {parts[4]}";
            case "button" when int.TryParse(parts[2], out int b):
                return $"{name} · Button {b + 1}";
            case "hat" when parts.Length >= 4 && int.TryParse(parts[2], out int h):
                return $"{name} · Hat {h + 1} {parts[3]}";
        }
        return value;
    }
}
