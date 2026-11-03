using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32;

namespace GtTelemetry.App.Camera;

/// <summary>A button on a game controller (wheel, button box, gamepad). The device is its USB vendor and product ID,
/// so the binding survives the controller being plugged into another port.</summary>
public readonly record struct PadButton(string Device, int Button)
{
    public override string ToString() => $"{Device}|{Button}";

    public static PadButton? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        int bar = text.LastIndexOf('|');
        return bar > 0 && int.TryParse(text[(bar + 1)..], out int b) ? new PadButton(text[..bar], b) : null;
    }
}

/// <summary>
/// Polls game controllers through the Windows joystick API (winmm), which keeps reporting while the app is in the
/// background, unlike the newer APIs. It sees up to 16 controllers and their first 32 buttons. Raises button presses on
/// the UI thread, repeating while a button is held, like a key does.
/// </summary>
public sealed class Joysticks : IDisposable
{
    private const int MaxDevices = 16;
    private const int JOY_RETURNBUTTONS = 0x80;
    private const int RepeatDelayMs = 400, RepeatEveryMs = 90;

    [StructLayout(LayoutKind.Sequential)]
    private struct JoyInfoEx
    {
        public int Size, Flags, X, Y, Z, R, U, V, Buttons, ButtonNumber, Pov, Reserved1, Reserved2;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct JoyCaps
    {
        public ushort Mid, Pid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        public uint Xmin, Xmax, Ymin, Ymax, Zmin, Zmax, NumButtons, PeriodMin, PeriodMax, Rmin, Rmax, Umin, Umax, Vmin, Vmax, Caps, MaxAxes, NumAxes, MaxButtons;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string RegKey;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string OemVxd;
    }

    [DllImport("winmm.dll")] private static extern int joyGetPosEx(int id, ref JoyInfoEx info);
    [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "joyGetDevCapsW")] private static extern int joyGetDevCaps(IntPtr id, ref JoyCaps caps, int size);

    private sealed class Device(int id, string key, string name)
    {
        public int Id = id;
        public string Key = key, Name = name;
        public int Buttons;
        public long[] HeldSince = new long[32];
        public long[] LastRepeat = new long[32];
    }

    private readonly Dispatcher ui;
    private readonly Thread thread;
    private volatile bool stop;
    private Device[] devices = [];
    private readonly Dictionary<string, string> names = [];

    /// <summary>A button went down (repeat false) or is still held (repeat true).</summary>
    public event Action<PadButton, bool>? Pressed;

    public Joysticks(Dispatcher ui)
    {
        this.ui = ui;
        thread = new Thread(Run) { IsBackground = true, Name = "Joysticks" };
        thread.Start();
    }

    /// <summary>The controller's name for showing, e.g. "Logitech G923 Racing Wheel".</summary>
    public string NameOf(string device)
    {
        lock (names) return names.TryGetValue(device, out var n) ? n : "Controller " + device;
    }

    private void Run()
    {
        long lastScan = long.MinValue;
        while (!stop)
        {
            long now = Environment.TickCount64;
            // Asking an empty slot is slow, so look for newly plugged controllers only every few seconds.
            if (now - lastScan > 3000)
            {
                Scan();
                lastScan = now;
            }
            foreach (var d in devices) Poll(d, now);
            Thread.Sleep(15);
        }
    }

    private void Scan()
    {
        var found = new List<Device>();
        for (int id = 0; id < MaxDevices; id++)
        {
            var info = new JoyInfoEx { Size = Marshal.SizeOf<JoyInfoEx>(), Flags = JOY_RETURNBUTTONS };
            if (joyGetPosEx(id, ref info) != 0) continue;
            var caps = new JoyCaps();
            if (joyGetDevCaps(id, ref caps, Marshal.SizeOf<JoyCaps>()) != 0) continue;
            string key = $"{caps.Mid:X4}:{caps.Pid:X4}";
            // Two identical controllers: tell them apart by slot.
            if (found.Any(f => f.Key == key)) key += $"#{id}";
            string name = OemName(caps.Mid, caps.Pid) ?? (string.IsNullOrWhiteSpace(caps.Name) ? "Controller " + key : caps.Name);
            lock (names) names[key] = name;
            var existing = devices.FirstOrDefault(d => d.Id == id && d.Key == key);
            found.Add(existing ?? new Device(id, key, name) { Buttons = info.Buttons });
        }
        devices = [.. found];
    }

    /// <summary>The name Windows shows in "Game controllers", from the registry.</summary>
    private static string? OemName(ushort vid, ushort pid)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey($@"System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_{vid:X4}&PID_{pid:X4}")
                          ?? Registry.LocalMachine.OpenSubKey($@"System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_{vid:X4}&PID_{pid:X4}");
            return (k?.GetValue("OEMName") as string)?.Trim() is { Length: > 0 } n ? n : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void Poll(Device d, long now)
    {
        var info = new JoyInfoEx { Size = Marshal.SizeOf<JoyInfoEx>(), Flags = JOY_RETURNBUTTONS };
        if (joyGetPosEx(d.Id, ref info) != 0) return;
        int was = d.Buttons, isNow = info.Buttons;
        d.Buttons = isNow;
        for (int b = 0; b < 32; b++)
        {
            int bit = 1 << b;
            if ((isNow & bit) == 0) continue;
            var button = new PadButton(d.Key, b + 1);
            if ((was & bit) == 0)
            {
                d.HeldSince[b] = d.LastRepeat[b] = now;
                Raise(button, false);
            }
            else if (now - d.HeldSince[b] > RepeatDelayMs && now - d.LastRepeat[b] > RepeatEveryMs)
            {
                d.LastRepeat[b] = now;
                Raise(button, true);
            }
        }
    }

    private void Raise(PadButton b, bool repeat) => ui.BeginInvoke(() => Pressed?.Invoke(b, repeat));

    public void Dispose()
    {
        stop = true;
        thread.Join(500);
    }
}
