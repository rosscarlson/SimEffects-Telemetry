using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;

namespace GtTelemetry.App.Camera;

public enum CameraAction { SeatUp, SeatDown, SeatLeft, SeatRight, SeatForward, SeatBack, FovWider, FovNarrower, Reset }

/// <summary>A key plus Ctrl/Alt/Win. Shift isn't part of a binding: holding it makes the step bigger.</summary>
public readonly record struct KeyCombo(ModifierKeys Modifiers, Key Key)
{
    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    private static string KeyName(Key k) => k switch
    {
        Key.OemPlus => "=",
        Key.OemMinus => "-",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemQuestion => "/",
        Key.OemSemicolon => ";",
        Key.OemQuotes => "'",
        Key.OemPipe => "\\",
        Key.OemTilde => "`",
        Key.PageUp => "PgUp",
        Key.PageDown => "PgDn",
        >= Key.D0 and <= Key.D9 => ((char)('0' + (k - Key.D0))).ToString(),
        _ => k.ToString(),
    };

    public static KeyCombo? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var mods = ModifierKeys.None;
        var parts = text.Split('+');
        // "Ctrl+Alt+=" splits fine; "Ctrl+Alt++" doesn't occur because = is the plus key.
        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].Trim().ToLowerInvariant())
            {
                case "ctrl": mods |= ModifierKeys.Control; break;
                case "alt": mods |= ModifierKeys.Alt; break;
                case "win": mods |= ModifierKeys.Windows; break;
                case "shift": break;
                default: return null;
            }
        }
        string name = parts[^1].Trim();
        foreach (Key k in Enum.GetValues<Key>())
            if (k != Key.None && KeyName(k).Equals(name, StringComparison.OrdinalIgnoreCase)) return new KeyCombo(mods, k);
        return null;
    }
}

public static class CameraKeys
{
    private const ModifierKeys CtrlAlt = ModifierKeys.Control | ModifierKeys.Alt;

    /// <summary>No numpad keys: Ross's keyboard doesn't have one.</summary>
    public static readonly IReadOnlyList<(CameraAction Action, string Name, KeyCombo Default)> All =
    [
        (CameraAction.SeatUp, "Seat up", new(CtrlAlt, Key.Up)),
        (CameraAction.SeatDown, "Seat down", new(CtrlAlt, Key.Down)),
        (CameraAction.SeatLeft, "Seat left", new(CtrlAlt, Key.Left)),
        (CameraAction.SeatRight, "Seat right", new(CtrlAlt, Key.Right)),
        (CameraAction.SeatForward, "Seat forward", new(CtrlAlt, Key.PageUp)),
        (CameraAction.SeatBack, "Seat back", new(CtrlAlt, Key.PageDown)),
        (CameraAction.FovWider, "Wider FOV", new(CtrlAlt, Key.OemPlus)),
        (CameraAction.FovNarrower, "Narrower FOV", new(CtrlAlt, Key.OemMinus)),
        (CameraAction.Reset, "Reset this view", new(CtrlAlt, Key.Home)),
    ];

    /// <summary>The action's keys: the saved ones, the default if none are saved, or null if they were taken for
    /// another action.</summary>
    public static KeyCombo? For(AppSettings s, CameraAction a) =>
        s.CameraKeys.TryGetValue(a.ToString(), out var text) ? KeyCombo.Parse(text) : All.First(x => x.Action == a).Default;

    public static PadButton? ButtonFor(AppSettings s, CameraAction a) =>
        s.CameraButtons.TryGetValue(a.ToString(), out var text) ? PadButton.Parse(text) : null;
}

/// <summary>
/// Watches the keyboard while PCSX2 has focus and turns bound combinations into camera actions (and keeps them from
/// reaching PCSX2). A low-level hook rather than global hotkeys, so the keys still work normally in other apps.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104;
    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr hMod, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vk);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? name);

    private readonly HookProc proc;
    private readonly IntPtr hook;
    private readonly Func<KeyCombo, bool, bool> handler;
    private readonly Dictionary<uint, bool> isPcsx2 = [];

    /// <param name="handler">Called on the UI thread with the combination and whether Shift is held; returns true if
    /// it was a camera key (then PCSX2 doesn't see it).</param>
    public KeyboardHook(Func<KeyCombo, bool, bool> handler)
    {
        this.handler = handler;
        proc = Callback;
        hook = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(null), 0);
    }

    public bool Installed => hook != IntPtr.Zero;

    /// <summary>Shift held anywhere (it makes steps bigger for controller buttons too).</summary>
    public static bool ShiftDown() => (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;

    /// <summary>The PCSX2 window that has focus, if any.</summary>
    public IntPtr Pcsx2Foreground()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return IntPtr.Zero;
        GetWindowThreadProcessId(fg, out uint pid);
        // The hook has to answer quickly, so only look up a process the first time it has focus.
        if (!isPcsx2.TryGetValue(pid, out bool yes))
        {
            try { yes = Process.GetProcessById((int)pid).ProcessName.StartsWith("pcsx2", StringComparison.OrdinalIgnoreCase); }
            catch (ArgumentException) { yes = false; }
            catch (InvalidOperationException) { yes = false; }
            isPcsx2[pid] = yes;
        }
        return yes ? fg : IntPtr.Zero;
    }

    private IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN))
        {
            int vk = Marshal.ReadInt32(lParam);
            var key = KeyInterop.KeyFromVirtualKey(vk);
            static bool Down(int v) => (GetAsyncKeyState(v) & 0x8000) != 0;
            var mods = ModifierKeys.None;
            if (Down(VK_CONTROL)) mods |= ModifierKeys.Control;
            if (Down(VK_MENU)) mods |= ModifierKeys.Alt;
            if (Down(VK_LWIN) || Down(VK_RWIN)) mods |= ModifierKeys.Windows;
            if (mods != ModifierKeys.None && Pcsx2Foreground() != IntPtr.Zero)
            {
                try
                {
                    if (handler(new KeyCombo(mods, key), Down(VK_SHIFT))) return 1;
                }
                catch (Exception)
                {
                    // Never let an exception escape a keyboard hook.
                }
            }
        }
        return CallNextHookEx(hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
    }
}
