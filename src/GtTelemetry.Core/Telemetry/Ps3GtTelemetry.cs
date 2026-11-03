using System.Buffers.Binary;
using System.Diagnostics;
using GtTelemetry.Core.Memory;

namespace GtTelemetry.Core.Telemetry;

/// <summary>
/// Where one PS3 Gran Turismo keeps the player's car: a single block in the race heap holding the body (transform,
/// velocity, rates, acceleration), four wheel structs and the driver inputs. Offsets are from the start of the block;
/// GT5 and GT6 share most of the body layout and differ in the wheel struct size and where the dash values sit.
/// </summary>
public sealed record Ps3CarLayout
{
    public required string Game { get; init; }

    public required int BlockSize { get; init; }

    public int Position { get; init; } = 0x70;          // f32 ×3, world X, Y, Z (Z up)
    public int AxisForward { get; init; } = 0x7C;       // f32 ×3, unit vectors in world space
    public int AxisRight { get; init; } = 0x88;
    public int AxisUp { get; init; } = 0x94;
    public required int Velocity { get; init; }         // f32 ×3, world, m/s
    public required int AngularVelocity { get; init; }  // f32 ×3, car frame: pitch (nose up +), roll (right side down +), yaw (left +), rad/s
    public required int Wheel0 { get; init; }           // 4 wheel structs: FL, FR, RL, RR
    public required int WheelStride { get; init; }
    public required int Rpm { get; init; }              // f32
    public required int Gear { get; init; }             // u8; 1 when stopped in automatic
    public required int Accel { get; init; }            // f32 ×3, car frame: forward, right, up, m/s², gravity excluded
    public required int Speed { get; init; }            // f32 |v|, m/s
    public required int Steer { get; init; }            // f32 0 full left, 0.5 centre, 1 full right
    public required int Throttle { get; init; }         // f32 0–1
    public required int Brake { get; init; }            // f32 0–1

    // Wheel struct offsets.
    public int WheelMatrix { get; init; } = 0x34;       // f32 3×3: rows follow the car's forward, right and up
    public int WheelSlipAngle { get; init; } = 0x5C;    // rad
    public int WheelSurfaceSpeed { get; init; } = 0x6C; // tyre surface speed, m/s
    public int WheelSlipRatio { get; init; } = 0x74;    // (ground − wheel) / wheel: negative under power
    public required int WheelSuspension { get; init; }  // suspension length, m: smaller = more compressed
    public required int WheelSurface { get; init; }     // u8 surface code

    /// <summary>Maps the wheel surface code to a kerb flag and an off-track rumble (0–1).</summary>
    public required Func<byte, (bool RumbleStrip, float Rumble)> SurfaceMap { get; init; }

    /// <summary>GT6 1.00 (BCUS98296), mapped 2026-11-03 at the car block 0x42787400. Surface codes: 0 tarmac, 1 kerb, 2 grass
    /// or other low-grip ground (the game's grip drops from 1.2 to 0.96 on it).</summary>
    public static readonly Ps3CarLayout Gt6 = new()
    {
        Game = "GT6",
        BlockSize = 0x1000,
        AngularVelocity = 0x58,
        Velocity = 0x64,
        Wheel0 = 0x120,
        WheelStride = 0x168,
        Rpm = 0x73C,
        Gear = 0x7D8,
        Accel = 0x898,
        Speed = 0x8A8,
        Steer = 0x934,
        Throttle = 0x938,
        Brake = 0x93C,
        WheelSuspension = 0x124,
        WheelSurface = 0x149,
        SurfaceMap = code => code switch
        {
            1 => (true, 0f),
            2 => (false, 0.5f),
            _ => (false, 0f),
        },
    };

    /// <summary>GT5 (BCUS98114), mapped 2026-11-03 at Laguna Seca with the car block at 0x424C1280. Surface codes: 0 tarmac,
    /// 1 kerb, 4 the rough strip just past a kerb or the track edge, 2 grass.</summary>
    public static readonly Ps3CarLayout Gt5 = new()
    {
        Game = "GT5",
        BlockSize = 0xB00,
        Wheel0 = 0xCC,
        WheelStride = 0x14C,
        Throttle = 0x660,
        Brake = 0x66C,
        Rpm = 0x6D4,
        Gear = 0x73C,
        Accel = 0x814,
        Speed = 0x824,
        Velocity = 0x82C,
        AngularVelocity = 0x844,
        Steer = 0xA88,
        WheelSuspension = 0x118,
        WheelSurface = 0x139,
        SurfaceMap = code => code switch
        {
            1 => (true, 0f),
            2 => (false, 0.5f),
            4 => (false, 0.7f),
            _ => (false, 0f),
        },
    };
}

/// <summary>
/// Telemetry for the PS3 Gran Turismos in RPCS3 (docs/ps3/memory-map.md). The car block moves between races, so it's
/// found by its shape (unit body axes, and four wheel structs whose rotation matrices line up with the body) and checked
/// every frame. PS3 memory is big-endian.
/// </summary>
public sealed class Ps3GtTelemetry(Ps3CarLayout layout, IMemorySource memory, IRegionSource regions) : ITelemetrySource
{
    // Where the race heap lives; both games have put the car around 0x42000000.
    public const uint HeapFrom = 0x40000000, HeapTo = 0x50000000;

    private const float PeakSlipRatio = 0.1f;
    private const float PeakSlipAngle = 0.15f;      // rad, about 8.6°
    private const float TyreRadius = 0.32f;         // m; not mapped yet, only scales wheel rotation speed
    private const int PausedAfterMs = 150;
    private const int SearchEveryMs = 2000;

    private readonly Stopwatch changedAge = new();
    private readonly Stopwatch searchAge = new();
    private readonly byte[] block = new byte[layout.BlockSize];
    private readonly byte[] previous = new byte[layout.BlockSize];
    private readonly float[] suspExtended = new float[4];
    private readonly float[] suspCompressed = new float[4];
    private bool suspInitialised;
    private uint car;
    private float maxRpm;
    private float idleRpm;

    public Ps3CarLayout Layout => layout;

    /// <summary>The car block in use, 0 if not found.</summary>
    public uint Car => car;

    /// <summary>How many car blocks the last search found (more than one would mean AI cars share the layout).</summary>
    public int LastSearchCount { get; private set; }

    public void ResetCarLimits()
    {
        maxRpm = 0;
        idleRpm = 0;
        suspInitialised = false;
    }

    public TelemetryFrame Read()
    {
        if (!memory.IsConnected) return TelemetryFrame.NotDriving;
        if (car == 0)
        {
            if (searchAge.IsRunning && searchAge.ElapsedMilliseconds < SearchEveryMs) return TelemetryFrame.NotDriving;
            searchAge.Restart();
            var found = Search();
            LastSearchCount = found.Count;
            if (found.Count == 0) return TelemetryFrame.NotDriving;
            car = found[0];
            ResetCarLimits();
            changedAge.Reset();
        }

        try
        {
            memory.Read(car, block);
        }
        catch (MemorySourceException) when (memory.IsConnected)
        {
            car = 0;   // the heap was remapped between races
            return TelemetryFrame.NotDriving;
        }
        if (!LooksLikeCar(layout, block))
        {
            car = 0;
            return TelemetryFrame.NotDriving;
        }

        // The game's pause menu freezes the physics: nothing in the block changes.
        if (!block.AsSpan().SequenceEqual(previous) || !changedAge.IsRunning)
        {
            block.CopyTo(previous, 0);
            changedAge.Restart();
        }
        else if (changedAge.ElapsedMilliseconds > PausedAfterMs)
        {
            return TelemetryFrame.NotDriving;
        }

        float rpm = F(layout.Rpm), speed = F(layout.Speed);
        float throttle = F(layout.Throttle), brake = F(layout.Brake), steer = Math.Clamp((F(layout.Steer) - 0.5f) * 2f, -1f, 1f);
        int gear = block[layout.Gear] >= 0x80 ? -1 : block[layout.Gear];
        if (!(rpm is >= 0 and < 25000 && speed < 200 && gear <= 10 && throttle is >= 0 and <= 1.01f && brake is >= 0 and <= 1.01f))
            return TelemetryFrame.NotDriving;

        if (rpm > maxRpm) maxRpm = rpm;
        if (throttle < 0.02f && speed < 0.5f && rpm > 300 && (idleRpm == 0 || rpm < idleRpm)) idleRpm = rpm;

        var fwd = Vec(layout.AxisForward);
        var right = Vec(layout.AxisRight);
        var up = Vec(layout.AxisUp);
        var vel = Vec(layout.Velocity);
        var pos = Vec(layout.Position);

        return new TelemetryFrame
        {
            Driving = true,
            Rpm = rpm,
            MaxRpm = Math.Max(maxRpm, 6000),
            IdleRpm = idleRpm > 0 ? idleRpm : 800,
            SpeedMps = speed,
            VelocityForwardMps = Dot(vel, fwd),
            VelocityRightMps = Dot(vel, right),
            VelocityUpMps = Dot(vel, up),
            AccelForward = F(layout.Accel),
            AccelRight = F(layout.Accel + 4),
            AccelUp = F(layout.Accel + 8),
            // The game's rates are pitch nose up +, roll right side down +, yaw left +; the frame's are the opposite.
            PitchRate = -F(layout.AngularVelocity),
            RollRate = -F(layout.AngularVelocity + 4),
            YawRate = -F(layout.AngularVelocity + 8),
            Yaw = MathF.Atan2(fwd.X, fwd.Y),
            Pitch = -MathF.Asin(Math.Clamp(fwd.Z, -1f, 1f)),
            Roll = MathF.Asin(Math.Clamp(right.Z, -1f, 1f)),
            PositionX = pos.X,
            PositionY = pos.Z,
            PositionZ = pos.Y,
            Throttle = throttle,
            Brake = brake,
            Steer = steer,
            Gear = gear,
            Wheels = ReadWheels(speed),
        };
    }

    private WheelState[] ReadWheels(float speed)
    {
        var wheels = new WheelState[4];
        // Slip angles mean nothing when crawling; fade them in between 1 and 4 m/s.
        float angleFade = Math.Clamp((speed - 1f) / 3f, 0f, 1f);
        for (int i = 0; i < 4; i++)
        {
            int w = layout.Wheel0 + i * layout.WheelStride;
            float length = F(w + layout.WheelSuspension);
            if (!suspInitialised)
            {
                suspExtended[i] = length + 0.04f;
                suspCompressed[i] = length - 0.08f;
            }
            suspExtended[i] = Math.Max(suspExtended[i], length);
            suspCompressed[i] = Math.Min(suspCompressed[i], length);
            float travel = suspExtended[i] - suspCompressed[i];
            var (strip, rumble) = layout.SurfaceMap(block[w + layout.WheelSurface]);

            wheels[i] = new WheelState
            {
                SuspensionMeters = suspExtended[i] - length,
                SuspensionNormalized = travel > 0 ? (suspExtended[i] - length) / travel : 0,
                SlipRatio = Math.Clamp(-F(w + layout.WheelSlipRatio) / PeakSlipRatio, -10f, 10f),
                SlipAngle = Math.Clamp(F(w + layout.WheelSlipAngle) / PeakSlipAngle * angleFade, -10f, 10f),
                RotationSpeed = F(w + layout.WheelSurfaceSpeed) / TyreRadius,
                OnRumbleStrip = strip,
                SurfaceRumble = rumble * Math.Clamp(speed / 10f, 0f, 1f),
            };
        }
        suspInitialised = true;
        return wheels;
    }

    /// <summary>Searches the race heap for car blocks. Takes a few hundred milliseconds.</summary>
    public List<uint> Search()
    {
        var found = new List<uint>();
        const int chunk = 4 << 20;
        int size = layout.BlockSize;
        var buf = new byte[chunk + size];
        foreach (var r in regions.Regions(HeapFrom, HeapTo))
        {
            for (uint off = 0; off < r.Size; off += chunk)
            {
                int len = (int)Math.Min((uint)buf.Length, r.Size - off);
                var span = buf.AsSpan(0, len);
                try { memory.Read(r.Start + off, span); }
                catch (MemorySourceException) when (memory.IsConnected) { continue; }
                // Blocks starting in this chunk (the overlap covers the ones running past its end). Cheap test first:
                // a unit forward axis.
                for (int start = 0; start < chunk && start + size <= len; start += 4)
                {
                    if (!Unit(span, start + layout.AxisForward)) continue;
                    if (LooksLikeCar(layout, span.Slice(start, size)))
                    {
                        uint addr = r.Start + off + (uint)start;
                        if (!found.Contains(addr)) found.Add(addr);
                    }
                }
            }
        }
        return found;
    }

    /// <summary>A car block: unit, perpendicular body axes, and four wheels whose rotation matrices are unit and stand
    /// upright with the body.</summary>
    public static bool LooksLikeCar(Ps3CarLayout l, ReadOnlySpan<byte> b)
    {
        if (b.Length < l.BlockSize) return false;
        if (!Unit(b, l.AxisForward) || !Unit(b, l.AxisRight) || !Unit(b, l.AxisUp)) return false;
        if (Math.Abs(DotAt(b, l.AxisForward, l.AxisRight)) > 0.02f || Math.Abs(DotAt(b, l.AxisForward, l.AxisUp)) > 0.02f) return false;
        for (int i = 0; i < 4; i++)
        {
            int m = l.Wheel0 + i * l.WheelStride + l.WheelMatrix;
            for (int row = 0; row < 3; row++)
                if (!Unit(b, m + row * 12)) return false;
            if (DotAt(b, m + 24, l.AxisUp) < 0.8f) return false;
        }
        return true;
    }

    private static float Fb(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadSingleBigEndian(s[o..]);

    private static bool Unit(ReadOnlySpan<byte> s, int o)
    {
        float x = Fb(s, o), y = Fb(s, o + 4), z = Fb(s, o + 8);
        float n = x * x + y * y + z * z;
        return float.IsFinite(n) && Math.Abs(n - 1) < 0.02f;
    }

    private static float DotAt(ReadOnlySpan<byte> s, int a, int b) => Fb(s, a) * Fb(s, b) + Fb(s, a + 4) * Fb(s, b + 4) + Fb(s, a + 8) * Fb(s, b + 8);

    private float F(int offset)
    {
        float f = BinaryPrimitives.ReadSingleBigEndian(block.AsSpan(offset));
        return float.IsFinite(f) ? f : 0f;
    }

    private (float X, float Y, float Z) Vec(int offset) => (F(offset), F(offset + 4), F(offset + 8));

    private static float Dot((float X, float Y, float Z) a, (float X, float Y, float Z) b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
}
