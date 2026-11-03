using System.Windows.Input;

namespace GtTelemetry.App.Input;

public enum HotkeyKind { None, Keyboard, Joystick }

// The app-wide start/stop hotkey: a key (with modifiers) or a button on a
// wheel, button box or controller, which is matched by its USB vendor and
// product id so it survives reconnecting.
public sealed class HotkeyBinding
{
    public HotkeyKind Kind { get; set; }
    public int VirtualKey { get; set; }
    public ModifierKeys Modifiers { get; set; }
    public ushort JoyVendor { get; set; }
    public ushort JoyProduct { get; set; }
    public string JoyName { get; set; } = "";
    public int Button { get; set; }  // 0-based

    public override string ToString() => Kind switch
    {
        HotkeyKind.Keyboard => KeyText(VirtualKey, Modifiers),
        HotkeyKind.Joystick => $"{JoyName} button {Button + 1}",
        _ => "Not set",
    };

    public static string KeyText(int vk, ModifierKeys mods)
    {
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        var key = KeyInterop.KeyFromVirtualKey(vk);
        parts.Add(key switch
        {
            >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
            >= Key.NumPad0 and <= Key.NumPad9 => $"Num {key - Key.NumPad0}",
            Key.Add => "Num +",
            Key.Subtract => "Num -",
            Key.Oem3 => "`",
            Key.OemMinus => "-",
            Key.OemPlus => "=",
            Key.Return => "Enter",
            Key.Next => "Page Down",
            Key.Prior => "Page Up",
            Key.Scroll => "Scroll Lock",
            Key.Pause => "Pause",
            _ => key.ToString(),
        });
        return string.Join("+", parts);
    }
}
