using Vortice.DirectInput;

namespace GtTelemetry.App.Input;

// One game controller as Azimuth sees it: read through DirectInput with the
// standard DIJOYSTATE2 layout, so axis "slots" are the same numbers Azimuth
// writes in its bindings (0-7 X, Y, Z, Rx, Ry, Rz, slider 1-2; 8-15 their
// velocities; 16-23 accelerations; 24-31 forces).
public sealed class WheelDevice
{
    public required string Id { get; init; }          // Azimuth's "<pid><vid>", e.g. 00040eb7
    public required string Name { get; init; }
    public required Guid Instance { get; init; }
    public required IReadOnlyDictionary<int, string> AxisNames { get; init; }  // slot -> name
    public required int ButtonCount { get; init; }
    internal IDirectInputDevice8? Device;
}

public sealed record WheelSnapshot(WheelDevice Device, int[] Axes, bool[] Buttons, int[] Povs);

public sealed class WheelInput : IDisposable
{
    public const int AxisSlots = 32;
    readonly IntPtr _window;
    IDirectInput8? _di;
    List<WheelDevice> _devices = new();

    public WheelInput(IntPtr window) => _window = window;

    public IReadOnlyList<WheelDevice> Devices => _devices;

    public void Refresh()
    {
        ReleaseDevices();
        _di ??= DInput.DirectInput8Create();
        var list = new List<WheelDevice>();
        foreach (var info in _di.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly))
        {
            IDirectInputDevice8? dev = null;
            try
            {
                dev = _di.CreateDevice(info.InstanceGuid);
                dev.SetCooperativeLevel(_window, CooperativeLevel.Background | CooperativeLevel.NonExclusive);
                if (JoystickFormat.Set(dev.NativePointer) < 0) throw new InvalidOperationException("SetDataFormat failed");
                // Which axis landed in each slot of the data format (an object's
                // own Offset is the device's native one, not this).
                var names = new Dictionary<int, string>();
                for (int slot = 0; slot < AxisSlots; slot++)
                    if (JoystickFormat.ObjectAt(dev.NativePointer, FormatOffset(slot)) is string name) names[slot] = name;
                try { dev.Properties.Range = new InputRange(0, 65535); } catch { }  // as Azimuth sets it
                try { dev.Acquire(); } catch { }
                list.Add(new WheelDevice
                {
                    Id = IdOf(info.ProductGuid),
                    Name = string.IsNullOrWhiteSpace(info.ProductName) ? info.InstanceName.Trim() : info.ProductName.Trim(),
                    Instance = info.InstanceGuid,
                    AxisNames = names,
                    ButtonCount = dev.Capabilities.ButtonCount,
                    Device = dev,
                });
            }
            catch
            {
                dev?.Dispose();  // a device that won't open (unplugged mid-way, etc.) is skipped
            }
        }
        _devices = list;
    }

    // DIJOYSTATE2 offsets: 8 position LONGs at 0x00, 4 POVs, 128 buttons, then
    // velocity, acceleration and force blocks of 8 LONGs each from 0xB0.
    static int FormatOffset(int slot) => slot < 8 ? slot * 4 : 0xB0 + (slot - 8) * 4;

    // ProductGuid's first field is PID << 16 | VID, which Azimuth prints as %08x.
    static string IdOf(Guid product) => BitConverter.ToUInt32(product.ToByteArray(), 0).ToString("x8");

    public List<WheelSnapshot> Read()
    {
        var shots = new List<WheelSnapshot>(_devices.Count);
        foreach (var d in _devices)
        {
            if (d.Device is not { } dev) continue;
            var raw = new byte[JoystickFormat.StateSize];
            try { dev.Poll(); } catch { }
            if (JoystickFormat.Read(dev.NativePointer, raw) < 0)
            {
                try { dev.Acquire(); } catch { }
                continue;
            }
            var axes = new int[AxisSlots];
            for (int slot = 0; slot < AxisSlots; slot++) axes[slot] = BitConverter.ToInt32(raw, FormatOffset(slot));
            var povs = new int[JoystickFormat.Povs];
            for (int p = 0; p < povs.Length; p++) povs[p] = BitConverter.ToInt32(raw, JoystickFormat.PovOffset + p * 4);
            var buttons = new bool[JoystickFormat.Buttons];
            for (int b = 0; b < buttons.Length; b++) buttons[b] = (raw[JoystickFormat.ButtonOffset + b] & 0x80) != 0;
            shots.Add(new WheelSnapshot(d, axes, buttons, povs));
        }
        return shots;
    }

    void ReleaseDevices()
    {
        foreach (var d in _devices)
        {
            try { d.Device?.Unacquire(); } catch { }
            d.Device?.Dispose();
            d.Device = null;
        }
        _devices = new();
    }

    public void Dispose()
    {
        ReleaseDevices();
        _di?.Dispose();
        _di = null;
    }
}

public enum CaptureKind { Steering, Pedal, Button }

// Watches the controllers for one binding, the way Azimuth's own Controls
// page asks: turn the wheel to the right; press a pedal fully, then let it
// go; press a button (or a hat/D-pad direction).
public sealed class BindingCapture
{
    const int Moved = 16000;       // about a quarter of the range
    const int Released = 9000;

    readonly CaptureKind _kind;
    readonly Dictionary<(Guid, int), int> _restAxes = new();
    readonly Dictionary<Guid, (bool[] buttons, int[] povs)> _restButtons = new();
    (WheelDevice dev, int slot, int rest, int extreme)? _pedal;

    public BindingCapture(CaptureKind kind, List<WheelSnapshot> rest)
    {
        _kind = kind;
        foreach (var s in rest)
        {
            foreach (var slot in s.Device.AxisNames.Keys) _restAxes[(s.Device.Instance, slot)] = s.Axes[slot];
            _restButtons[s.Device.Instance] = (s.Buttons, s.Povs);
        }
    }

    public string Prompt => _kind switch
    {
        CaptureKind.Steering => "Turn the wheel to the right…",
        CaptureKind.Pedal => _pedal == null ? "Press it fully, then let it go…" : "Now let it go…",
        _ => "Press the button…",
    };

    // The binding in Azimuth's form once captured, else null.
    public string? Feed(List<WheelSnapshot> now)
    {
        return _kind switch
        {
            CaptureKind.Steering => FeedSteering(now),
            CaptureKind.Pedal => FeedPedal(now),
            _ => FeedButton(now),
        };
    }

    string? FeedSteering(List<WheelSnapshot> now)
    {
        var best = Biggest(now);
        if (best is not var (dev, slot, rest, value) || Math.Abs(value - rest) < Moved) return null;
        return value > rest ? $"{dev.Id}:axis:{slot}:0:65535" : $"{dev.Id}:axis:{slot}:65535:0";
    }

    string? FeedPedal(List<WheelSnapshot> now)
    {
        if (_pedal is not { } p)
        {
            var best = Biggest(now);
            if (best is var (dev, slot, rest, value) && Math.Abs(value - rest) >= Moved) _pedal = (dev, slot, rest, value);
            return null;
        }
        var shot = now.FirstOrDefault(s => s.Device.Instance == p.dev.Instance);
        if (shot == null) return null;
        int v = shot.Axes[p.slot];
        if (Math.Abs(v - p.rest) > Math.Abs(p.extreme - p.rest)) _pedal = p with { extreme = v };
        if (Math.Abs(v - p.rest) > Released) return null;
        var done = _pedal.Value;
        return $"{done.dev.Id}:axis:{done.slot}:{Snap(done.rest)}:{Snap(done.extreme)}";
    }

    string? FeedButton(List<WheelSnapshot> now)
    {
        foreach (var s in now)
        {
            if (!_restButtons.TryGetValue(s.Device.Instance, out var rest)) continue;
            int count = Math.Min(s.Buttons.Length, rest.buttons.Length);
            for (int b = 0; b < count; b++)
                if (s.Buttons[b] && !rest.buttons[b]) return $"{s.Device.Id}:button:{b}";
            for (int h = 0; h < Math.Min(s.Povs.Length, rest.povs.Length); h++)
                if (IsCentred(rest.povs[h]) && !IsCentred(s.Povs[h])) return $"{s.Device.Id}:hat:{h}:{Direction(s.Povs[h])}";
        }
        return null;
    }

    (WheelDevice dev, int slot, int rest, int value)? Biggest(List<WheelSnapshot> now)
    {
        (WheelDevice, int, int, int)? best = null;
        int bestDelta = 0;
        foreach (var s in now)
            foreach (var slot in s.Device.AxisNames.Keys)
            {
                if (!_restAxes.TryGetValue((s.Device.Instance, slot), out int rest)) continue;
                int delta = Math.Abs(s.Axes[slot] - rest);
                if (delta > bestDelta) { bestDelta = delta; best = (s.Device, slot, rest, s.Axes[slot]); }
            }
        return best;
    }

    // A pedal that rests or ends within 3% of an end is taken as that end.
    static int Snap(int v) => v < 2000 ? 0 : v > 63535 ? 65535 : v;

    static bool IsCentred(int pov) => pov < 0 || (pov & 0xFFFF) == 0xFFFF;

    static string Direction(int pov)
    {
        int quarter = (int)Math.Round(pov / 9000.0) % 4;
        return quarter switch { 0 => "up", 1 => "right", 2 => "down", _ => "left" };
    }
}
