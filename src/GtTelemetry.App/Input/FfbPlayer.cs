using GtTelemetry.Core.Nfsu2;
using Vortice.DirectInput;

namespace GtTelemetry.App.Input;

/// <summary>A wheel that can play force feedback.</summary>
public sealed record FfbWheel(Guid Instance, string Name);

/// <summary>How strongly each kind of the game's effects plays, 0–1, times the overall strength.</summary>
public sealed record FfbGains(double Strength, double Constant, double Vibration, double Spring, double Damper);

/// <summary>One of the game's effects as the player shows it.</summary>
public sealed record FfbShown(int Slot, string Name, string Detail, bool Playing);

/// <summary>
/// Plays the game's own DirectInput effects on a wheel, one for one: each effect the game has made becomes the same
/// effect on the wheel, started and stopped when the game starts and stops it. The wheel is held exclusively (DirectInput
/// lets only one program play effects), in the background, so it plays while the browser has focus. Chrome reads the
/// wheel through HID, not DirectInput, so steering keeps working.
/// </summary>
public sealed class FfbPlayer(IntPtr window) : IDisposable
{
    private IDirectInput8? di;
    private IDirectInputDevice8? device;
    private Guid deviceId;
    private Guid? openedFor;
    private int axis;                                  // the wheel's first force feedback axis, as an object id
    private readonly Dictionary<int, Played> played = [];
    private DateTime retryAt = DateTime.MinValue;

    /// <summary>Why the wheel isn't playing, or null.</summary>
    public string? Error { get; private set; }

    public string? WheelName { get; private set; }

    public bool IsOpen => device != null;

    /// <summary>The attached wheels that can play force feedback.</summary>
    public static List<FfbWheel> Wheels()
    {
        var list = new List<FfbWheel>();
        try
        {
            using var di = DInput.DirectInput8Create();
            foreach (var info in di.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly | DeviceEnumerationFlags.ForceFeedback))
            {
                // Some virtual devices (vJoy) say they do force feedback but have no axis to play it on.
                try
                {
                    using var dev = di.CreateDevice(info.InstanceGuid);
                    if (Actuator(dev) == null) continue;
                }
                catch { continue; }
                string name = string.IsNullOrWhiteSpace(info.ProductName) ? info.InstanceName.Trim() : info.ProductName.Trim();
                int same = list.Count(w => w.Name == name || w.Name.StartsWith(name + " ("));
                list.Add(new(info.InstanceGuid, same == 0 ? name : $"{name} ({same + 1})"));
            }
        }
        catch { }
        return list;
    }

    /// <summary>
    /// Plays this tick's effects. <paramref name="wheel"/> null: the first force feedback wheel. Called on one thread.
    /// </summary>
    public IReadOnlyList<FfbShown> Update(Nfsu2Ffb ffb, Guid? wheel, FfbGains gains)
    {
        if (!Open(wheel)) return Show(ffb, false);
        try
        {
            var live = new HashSet<int>();
            foreach (var e in ffb.Effects)
            {
                if (e.Unloaded || !Plays(e.Kind)) continue;
                live.Add(e.Slot);
                if (!played.TryGetValue(e.Slot, out var p) || p.Object != e.Object)
                {
                    if (p != null) Release(e.Slot);
                    var made = Create(e, ffb, gains);
                    if (made == null) continue;
                    played[e.Slot] = p = new Played(e.Object, made);
                }
                Apply(p, e, ffb, gains);
            }
            foreach (int slot in played.Keys.Where(s => !live.Contains(s)).ToList()) Release(slot);
            Error = null;
        }
        catch (Exception ex)
        {
            // The wheel was unplugged or another program took it: start over in a moment.
            Close($"The wheel stopped taking effects ({ex.Message}).");
        }
        return Show(ffb, true);
    }

    /// <summary>Stops every effect on the wheel (the game paused, the page closed, force feedback turned off).</summary>
    public void StopAll()
    {
        foreach (var p in played.Values)
        {
            try { p.Effect.Stop(); } catch { }
            p.Running = false;
        }
        try { device?.SendForceFeedbackCommand(ForceFeedbackCommand.StopAll); } catch { }
    }

    /// <summary>Lets the wheel go: its effects are removed and other programs can use it.</summary>
    public void Close(string? error = null)
    {
        StopAll();
        foreach (int slot in played.Keys.ToList()) Release(slot);
        if (device != null)
        {
            try { device.SendForceFeedbackCommand(ForceFeedbackCommand.Reset); } catch { }
            try { device.Unacquire(); } catch { }
            device.Dispose();
            device = null;
        }
        WheelName = null;
        Error = error;
        if (error != null) retryAt = DateTime.UtcNow.AddSeconds(3);
    }

    public void Dispose()
    {
        Close();
        di?.Dispose();
        di = null;
    }

    // ---- the wheel ----

    private bool Open(Guid? wanted)
    {
        if (device != null && wanted == openedFor) return true;
        if (device != null) Close();
        if (DateTime.UtcNow < retryAt) return false;
        retryAt = DateTime.UtcNow.AddSeconds(3);

        var wheels = Wheels();
        if (wheels.Count == 0)
        {
            Error = "No wheel with force feedback is plugged in.";
            return false;
        }
        // The chosen wheel first, then the others: a wheel can show up twice, once without force feedback behind it.
        var order = wheels.OrderBy(w => w.Instance == wanted ? 0 : 1).ToList();
        string? firstError = null;
        foreach (var w in order)
        {
            string? error = TryOpen(w);
            if (error == null)
            {
                openedFor = wanted;
                Error = null;
                return true;
            }
            firstError ??= error;
        }
        Error = firstError;
        return false;
    }

    private string? TryOpen(FfbWheel w)
    {
        IDirectInputDevice8? dev = null;
        try
        {
            di ??= DInput.DirectInput8Create();
            dev = di.CreateDevice(w.Instance);
            dev.SetCooperativeLevel(window, CooperativeLevel.Background | CooperativeLevel.Exclusive);
            if (JoystickFormat.Set(dev.NativePointer) < 0) throw new InvalidOperationException("its data format wasn't taken");
            axis = Actuator(dev) ?? throw new InvalidOperationException("it has no force feedback axis");
            // The game's own spring centres the wheel; the wheel's would add to it.
            try { dev.Properties.AutoCenter = false; } catch { }
            try { dev.Properties.ForceFeedbackGain = 10000; } catch { }
            dev.Acquire();
            dev.SendForceFeedbackCommand(ForceFeedbackCommand.Reset);
            dev.SendForceFeedbackCommand(ForceFeedbackCommand.SetActuatorsOn);
        }
        catch (Exception ex)
        {
            dev?.Dispose();
            string why = System.Text.RegularExpressions.Regex.Match(ex.Message, @"ApiCode: \[([^/\]]+)").Groups[1].Value is { Length: > 0 } code ? code : ex.Message;
            return $"{w.Name} wouldn't play force feedback ({why}). Another program may be using it.";
        }
        device = dev;
        deviceId = w.Instance;
        WheelName = w.Name;
        return null;
    }

    /// <summary>The device's first force feedback axis as an object id. (Vortice's GetObjects with the actuator flag
    /// finds none, so the axes are checked one by one.)</summary>
    private static int? Actuator(IDirectInputDevice8 dev)
    {
        foreach (var o in dev.GetObjects(DeviceObjectTypeFlags.Axis))
            if (o.ObjectId.Flags.HasFlag(DeviceObjectTypeFlags.ForceFeedbackActuator)) return (int)o.ObjectId;
        return null;
    }

    // ---- effects ----

    private sealed class Played(uint obj, IDirectInputEffect effect)
    {
        public uint Object { get; } = obj;
        public IDirectInputEffect Effect { get; } = effect;
        public bool Running;
        public double Started;
        public int Gain = -1, Magnitude = int.MinValue, Offset, Coefficient, Saturation;
        public uint Period;
    }

    private static bool Plays(Nfsu2EffectKind k) => k is Nfsu2EffectKind.Constant
        or >= Nfsu2EffectKind.Square and <= Nfsu2EffectKind.SawtoothDown
        or Nfsu2EffectKind.Spring or Nfsu2EffectKind.Damper;

    private static Guid GuidOf(Nfsu2EffectKind k) => k switch
    {
        Nfsu2EffectKind.Constant => EffectGuid.ConstantForce,
        Nfsu2EffectKind.Square => EffectGuid.Square,
        Nfsu2EffectKind.Sine => EffectGuid.Sine,
        Nfsu2EffectKind.Triangle => EffectGuid.Triangle,
        Nfsu2EffectKind.SawtoothUp => EffectGuid.SawtoothUp,
        Nfsu2EffectKind.SawtoothDown => EffectGuid.SawtoothDown,
        Nfsu2EffectKind.Spring => EffectGuid.Spring,
        _ => EffectGuid.Damper,
    };

    private IDirectInputEffect? Create(Nfsu2Effect e, Nfsu2Ffb ffb, FfbGains gains)
    {
        var p = Parameters(e, ffb, gains);
        return device!.CreateEffect(GuidOf(e.Kind), p);
    }

    private EffectParameters Parameters(Nfsu2Effect e, Nfsu2Ffb ffb, FfbGains gains)
    {
        var p = new EffectParameters
        {
            Flags = EffectFlags.Cartesian | EffectFlags.ObjectIds,
            Duration = unchecked((int)e.DurationUs),        // INFINITE stays 0xFFFFFFFF
            Gain = (int)Math.Min(Level(e, gains), 10000),
            SamplePeriod = 0,
            StartDelay = (int)e.DelayUs,
            TriggerButton = -1,
            TriggerRepeatInterval = -1,
            Parameters = Specific(e, ffb, Math.Max(1, Level(e, gains) / 10000)),
        };
        // One axis, pointing the game's way: on Windows the game's +10000 pushes the wheel as it does here.
        p.SetAxes([axis], [1]);
        if (e.Envelope && e.Kind != Nfsu2EffectKind.Spring && e.Kind != Nfsu2EffectKind.Damper)
            p.Envelope = new Envelope { AttackLevel = (int)e.AttackLevel, AttackTime = (int)e.AttackUs, FadeLevel = (int)e.FadeLevel, FadeTime = (int)e.FadeUs };
        return p;
    }

    /// <summary>The game's values; <paramref name="boost"/> above 1 makes them stronger than the game's (strength over
    /// 100%), as far as the wheel's ±10000 goes.</summary>
    private static TypeSpecificParameters Specific(Nfsu2Effect e, Nfsu2Ffb ffb, double boost) => e.Kind switch
    {
        Nfsu2EffectKind.Constant => new ConstantForce { Magnitude = Clamp(e.Magnitude * boost) },
        Nfsu2EffectKind.Spring => Condition(ffb.Spring.Offset, ffb.Spring.Coefficient * boost, ffb.Spring.Saturation * boost),
        Nfsu2EffectKind.Damper => Condition(0, ffb.Damper * boost, 10000),
        _ => new PeriodicForce { Magnitude = Clamp(Math.Abs(e.Magnitude) * boost), Offset = Clamp(e.Offset * boost), Period = (int)e.PeriodUs, Phase = 0 },
    };

    private static ConditionSet Condition(int offset, double coefficient, double saturation)
    {
        int c = Clamp(coefficient), s = (int)Math.Clamp(saturation, 0, 10000);
        return new ConditionSet
        {
            Conditions = [new Vortice.DirectInput.Condition
            {
                Offset = Clamp(offset), PositiveCoefficient = c, NegativeCoefficient = c,
                PositiveSaturation = s, NegativeSaturation = s, DeadBand = 0,
            }],
        };
    }

    private static int Clamp(double v) => (int)Math.Clamp(v, -10000, 10000);

    /// <summary>The effect's gain times its kind's and the overall strength: up to 10000 is the effect's own gain on the
    /// wheel, beyond it the effect's values are scaled up.</summary>
    private static double Level(Nfsu2Effect e, FfbGains g)
    {
        double kind = e.Kind switch
        {
            Nfsu2EffectKind.Constant => g.Constant,
            Nfsu2EffectKind.Spring => g.Spring,
            Nfsu2EffectKind.Damper => g.Damper,
            _ => g.Vibration,
        };
        return Math.Max(0, Math.Min(e.Gain, 10000u) * kind * g.Strength);
    }

    /// <summary>Brings an effect on the wheel up to the game's: its strength, then started or stopped.</summary>
    private void Apply(Played p, Nfsu2Effect e, Nfsu2Ffb ffb, FfbGains gains)
    {
        bool play = e.Playing && ffb.Running;
        int gain = (int)Level(e, gains);
        var (mag, off, coef, sat) = e.Kind switch
        {
            Nfsu2EffectKind.Spring => (0, ffb.Spring.Offset, ffb.Spring.Coefficient, ffb.Spring.Saturation),
            Nfsu2EffectKind.Damper => (0, 0, ffb.Damper, 10000),
            _ => (e.Magnitude, e.Offset, 0, 0),
        };
        if (gain != p.Gain || mag != p.Magnitude || off != p.Offset || coef != p.Coefficient || sat != p.Saturation || e.PeriodUs != p.Period)
        {
            var flags = EffectParameterFlags.Gain | EffectParameterFlags.TypeSpecificParameters;
            // A running effect takes new values as it plays (no restart: the wheel would jerk).
            if (p.Running) flags |= EffectParameterFlags.NoRestart;
            p.Effect.SetParameters(Parameters(e, ffb, gains), flags);
            (p.Gain, p.Magnitude, p.Offset, p.Coefficient, p.Saturation, p.Period) = (gain, mag, off, coef, sat, e.PeriodUs);
        }
        if (play && (!p.Running || e.Started != p.Started))
        {
            // The game started it again (a new start time) or the world moves again.
            p.Effect.Start(e.Iterations == 0 ? 1 : unchecked((int)e.Iterations), EffectPlayFlags.None);
            p.Running = true;
            p.Started = e.Started;
        }
        else if (!play && p.Running)
        {
            p.Effect.Stop();
            p.Running = false;
        }
    }

    private void Release(int slot)
    {
        if (!played.Remove(slot, out var p)) return;
        try { p.Effect.Stop(); } catch { }
        try { p.Effect.Unload(); } catch { }
        p.Effect.Dispose();
    }

    private IReadOnlyList<FfbShown> Show(Nfsu2Ffb ffb, bool open) => ffb.Effects
        .Where(e => !e.Unloaded)
        .Select(e => new FfbShown(e.Slot, e.Name, Detail(e, ffb), open && e.Playing && ffb.Running && played.ContainsKey(e.Slot)))
        .ToList();

    private static string Detail(Nfsu2Effect e, Nfsu2Ffb ffb) => e.Kind switch
    {
        Nfsu2EffectKind.Constant => $"{e.Magnitude:+0;-0;0}",
        Nfsu2EffectKind.Spring => $"strength {ffb.Spring.Coefficient}, limit {ffb.Spring.Saturation}",
        Nfsu2EffectKind.Damper => $"strength {ffb.Damper}",
        _ when e.IsPeriodic => $"{Math.Abs(e.Magnitude)}, {(e.PeriodUs > 0 ? 1e6 / e.PeriodUs : 0):0} Hz",
        _ => "not played",
    };
}
