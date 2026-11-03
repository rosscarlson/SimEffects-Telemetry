using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace GtTelemetry.App.Input;

// A system-wide keyboard hotkey (RegisterHotKey), so it works while the game
// has focus.
public sealed class GlobalHotkey : IDisposable
{
    const int WM_HOTKEY = 0x0312;
    const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;
    readonly int _id;     // one per hotkey in this window
    readonly bool _repeat;  // held key repeats (camera steps)

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    readonly HwndSource _source;
    bool _registered;

    public event Action? Pressed;

    public GlobalHotkey(HwndSource source, int id = 0x4854, bool repeat = false)
    {
        _id = id;
        _repeat = repeat;
        _source = source;
        _source.AddHook(Hook);
    }

    // False if another app already owns this combination.
    public bool Register(int vk, ModifierKeys mods)
    {
        Unregister();
        uint m = _repeat ? 0 : MOD_NOREPEAT;
        if (mods.HasFlag(ModifierKeys.Alt)) m |= MOD_ALT;
        if (mods.HasFlag(ModifierKeys.Control)) m |= MOD_CONTROL;
        if (mods.HasFlag(ModifierKeys.Shift)) m |= MOD_SHIFT;
        if (mods.HasFlag(ModifierKeys.Windows)) m |= MOD_WIN;
        _registered = RegisterHotKey(_source.Handle, _id, m, (uint)vk);
        return _registered;
    }

    public void Unregister()
    {
        if (_registered) UnregisterHotKey(_source.Handle, _id);
        _registered = false;
    }

    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == _id)
        {
            Pressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _source.RemoveHook(Hook);
    }
}
