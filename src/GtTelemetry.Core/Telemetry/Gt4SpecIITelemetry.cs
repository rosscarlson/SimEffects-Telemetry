using System.Buffers.Binary;
using System.Diagnostics;
using GtTelemetry.Core.Memory;

namespace GtTelemetry.Core.Telemetry;

/// <summary>
/// GT4 Spec II telemetry from the confirmed address map (docs/gt4-spec2/memory-map.md). The game places the car and its
/// input block differently in each race, so the car is found from a pointer and the input block by its markers, and both
/// are checked every frame. The car body, wheels and dash values come from one bulk read of the car block; the pedals,
/// steering and frame counter from the input block.
/// </summary>
public sealed class Gt4SpecIITelemetry(Pcsx2Connection connection) : ITelemetrySource
{
    // Holds car + 0x1AEB0 in every race recorded so far. The addresses below are the car's layout as first mapped,
    // with the car at CarBlock; the live car can be anywhere in the race heap.
    private const uint CarPointer = 0x007342B8;
    private const uint CarPointerOffset = 0x1AEB0;
    private const uint RaceHeapStart = 0x01000000;

    // Car block: transform, wheels, body state and copies of the dash values.
    private const uint CarBlock = 0x01E6E750;
    private const int CarBlockSize = 0x900;
    private const uint Position = 0x01E6E754;       // f32 ×3, world X, Y, Z (Z up)
    private const uint AxisForward = 0x01E6E760;    // f32 ×3, unit vector
    private const uint AxisRight = 0x01E6E76C;
    private const uint AxisUp = 0x01E6E778;
    private const uint Wheel0 = 0x01E6E794;         // 4 wheel structs, FL FR RL RR
    private const int WheelStride = 0xF4;
    private const uint ThrottleNorm = 0x01E6EBFC;   // f32 0–1, after the game's assists
    private const uint BrakeNorm = 0x01E6EC00;      // f32 0–1
    private const uint GearCopy = 0x01E6EC94;       // u8
    private const uint SpeedAbs = 0x01E6ECB0;       // |v|, m/s
    private const uint WorldVelocity = 0x01E6ECB8;  // f32 ×3, world
    private const uint BodyAccel = 0x01E6ECC4;      // f32 ×3: forward, right, up, m/s²
    private const uint BodyRates = 0x01E6ECF0;      // f32 ×3: yaw (left +), pitch (nose up +), roll (right down +), rad/s
    private const uint RpmCopy = 0x01E6EDA4;        // f32
    private const uint SpeedForward = 0x01E6EDC0;   // signed, m/s
    private const uint SteerNorm = 0x01E6F04C;      // f32: 0 full left, 0.5 centre, 1 full right

    // Input block, about 0x6B16 below the car (it shifts by 4 between races), recognised by two markers. Offsets are
    // from the throttle field.
    private const int InputSearchFrom = -0x6C00, InputSearchTo = -0x6A00;
    private const int InputExpected = -0x6B16;
    private const int InputMarkerA = -0x10, InputMarkerB = 0x30;
    private const uint MarkerA = 0x006DEB50, MarkerB = 0x006B4A20;
    private const int InputSteer = -4;      // s16 ±4096, left +
    private const int InputThrottle = 0;    // u16 0–4096
    private const int InputBrake = 2;       // u16 0–4096
    private const int InputFrame = 0x20;    // u32 frame counter: 60 per second, stops while the game is paused
    private const int InputBlockSize = InputMarkerB + 4 - InputMarkerA;

    // The counter ticks at 60 Hz, so a few polls can land on the same frame; longer than this means paused.
    private const int PausedAfterMs = 150;
    private const int ResolveEveryMs = 500;

    // Wheel struct offsets.
    private const int WheelSurfaceSpeed = 0x40;    // tyre surface speed, m/s
    private const int WheelSlipAngle = 0x4C;       // rad
    private const int WheelSlipRatio = 0x58;       // (ground − wheel) / wheel: negative under power
    private const int WheelSuspension = 0xB4;      // m, more negative = more compressed
    private const int WheelSurface = 0xA9;         // u8 surface code

    private const float FixedOne = 4096f;
    private const float PeakSlipRatio = 0.1f;
    private const float PeakSlipAngle = 0.15f;      // rad, about 8.6°
    private const float TyreRadius = 0.32f;         // m; not mapped yet, only scales wheel rotation speed

    private readonly Stopwatch statusAge = new();
    private readonly Stopwatch frameAge = new();
    private readonly Stopwatch resolveAge = new();
    private uint lastFrame;
    private uint car;          // live car block address, 0 if not found
    private uint input;        // live throttle field address, 0 if not found
    private readonly byte[] block = new byte[CarBlockSize];
    private readonly byte[] inputBlock = new byte[InputBlockSize];
    private readonly float[] suspExtended = new float[4];
    private readonly float[] suspCompressed = new float[4];
    private bool suspInitialised;
    private bool emulatorRunning;
    private float maxRpm;
    private float idleRpm;

    /// <summary>Surface codes (wheel byte +0xA9), from a guided drive at Laguna Seca: 0 circuit tarmac, 2 other tarmac
    /// (pit lane), 1 kerb, 3 gravel or sand, 4 grass, 7 rough verge. Kerbs elsewhere can be 10 (seen on both sides of
    /// the car in a pause-on-kerb recording, 2026-11-03).</summary>
    public static Func<byte, (bool RumbleStrip, float Rumble)> SurfaceMap { get; set; } = code => code switch
    {
        1 or 10 => (true, 0f),
        3 => (false, 0.8f),
        4 => (false, 0.5f),
        7 => (false, 0.8f),
        _ => (false, 0f),
    };

    /// <summary>
    /// The game's redline and suspension limits aren't mapped, so they're learned while driving (the highest RPM seen,
    /// the suspension range seen). Reset when changing cars.
    /// </summary>
    public void ResetCarLimits()
    {
        maxRpm = 0;
        idleRpm = 0;
        suspInitialised = false;
    }

    public TelemetryFrame Read()
    {
        // Emulator pause/run state costs a round trip; once a second is plenty.
        if (!statusAge.IsRunning || statusAge.ElapsedMilliseconds > 1000)
        {
            emulatorRunning = connection.Pine.GetStatus() == EmulatorStatus.Running;
            statusAge.Restart();
        }
        if (!emulatorRunning) return TelemetryFrame.NotDriving;

        if (car == 0 || !resolveAge.IsRunning || resolveAge.ElapsedMilliseconds > ResolveEveryMs) Resolve();
        if (car == 0) return TelemetryFrame.NotDriving;

        connection.Read(car, block);
        float throttle, brake, steer;
        if (input != 0 && ReadInput())
        {
            // In the game's pause menu the emulator keeps running but the physics freeze; report not driving so motion
            // and vibration effects stop rather than holding the last frame.
            uint frame = BinaryPrimitives.ReadUInt32LittleEndian(inputBlock.AsSpan(InputFrame - InputMarkerA));
            if (frame != lastFrame || !frameAge.IsRunning)
            {
                lastFrame = frame;
                frameAge.Restart();
            }
            else if (frameAge.ElapsedMilliseconds > PausedAfterMs)
            {
                return TelemetryFrame.NotDriving;
            }
            throttle = BinaryPrimitives.ReadUInt16LittleEndian(inputBlock.AsSpan(InputThrottle - InputMarkerA)) / FixedOne;
            brake = BinaryPrimitives.ReadUInt16LittleEndian(inputBlock.AsSpan(InputBrake - InputMarkerA)) / FixedOne;
            steer = -BinaryPrimitives.ReadInt16LittleEndian(inputBlock.AsSpan(InputSteer - InputMarkerA)) / FixedOne;
        }
        else
        {
            // No input block: the car's own copies, without pause detection.
            throttle = F(ThrottleNorm);
            brake = F(BrakeNorm);
            steer = Math.Clamp((F(SteerNorm) - 0.5f) * 2f, -1f, 1f);
        }

        float rpm = F(RpmCopy);
        int gear = block[GearCopy - CarBlock];
        float speed = F(SpeedAbs), forwardSpeed = F(SpeedForward);
        var fwd = Vec(AxisForward);

        // Between races the pointer can lead to stale or unrelated data; only report values that look like a car.
        bool sane = rpm is >= 0 and < 20000 && speed < 200 && gear is >= 0 and <= 8
                    && throttle is >= 0 and <= 1.01f && brake is >= 0 and <= 1.01f && Math.Abs(steer) <= 1.01f
                    && Math.Abs(Dot(fwd, fwd) - 1) < 0.01f;
        if (!sane)
        {
            car = 0;   // look again next time
            return TelemetryFrame.NotDriving;
        }

        if (rpm > maxRpm) maxRpm = rpm;
        if (throttle < 0.02f && speed < 0.5f && rpm > 300 && (idleRpm == 0 || rpm < idleRpm)) idleRpm = rpm;

        var right = Vec(AxisRight);
        var up = Vec(AxisUp);
        var vel = Vec(WorldVelocity);
        var pos = Vec(Position);

        return new TelemetryFrame
        {
            Driving = true,
            Rpm = rpm,
            MaxRpm = Math.Max(maxRpm, 6000),
            IdleRpm = idleRpm > 0 ? idleRpm : 800,
            SpeedMps = speed,
            VelocityForwardMps = forwardSpeed,
            VelocityRightMps = Dot(vel, right),
            VelocityUpMps = Dot(vel, up),
            AccelForward = F(BodyAccel),
            AccelRight = F(BodyAccel + 4),
            AccelUp = F(BodyAccel + 8),
            // The game's rates are yaw left +, pitch nose up +, roll right side down +; the frame's are the opposite.
            YawRate = -F(BodyRates),
            PitchRate = -F(BodyRates + 4),
            RollRate = -F(BodyRates + 8),
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
            uint w = Wheel0 + (uint)(i * WheelStride);
            float surfaceSpeed = F(w + WheelSurfaceSpeed);
            float susp = F(w + WheelSuspension);
            if (!suspInitialised)
            {
                suspExtended[i] = susp + 0.04f;
                suspCompressed[i] = susp - 0.08f;
            }
            suspExtended[i] = Math.Max(suspExtended[i], susp);
            suspCompressed[i] = Math.Min(suspCompressed[i], susp);
            float travel = suspExtended[i] - suspCompressed[i];
            var (strip, rumble) = SurfaceMap(block[w + WheelSurface - CarBlock]);

            wheels[i] = new WheelState
            {
                SuspensionMeters = suspExtended[i] - susp,
                SuspensionNormalized = travel > 0 ? (suspExtended[i] - susp) / travel : 0,
                SlipRatio = Math.Clamp(-F(w + WheelSlipRatio) / PeakSlipRatio, -10f, 10f),
                SlipAngle = Math.Clamp(F(w + WheelSlipAngle) / PeakSlipAngle * angleFade, -10f, 10f),
                RotationSpeed = surfaceSpeed / TyreRadius,
                OnRumbleStrip = strip,
                SurfaceRumble = rumble * Math.Clamp(speed / 10f, 0f, 1f),
            };
        }
        suspInitialised = true;
        return wheels;
    }

    /// <summary>Finds the car from its pointer, and the input block near it by its markers.</summary>
    private void Resolve()
    {
        resolveAge.Restart();
        Span<byte> p = stackalloc byte[4];
        connection.Read(CarPointer, p);
        uint ptr = BinaryPrimitives.ReadUInt32LittleEndian(p);
        uint c = ptr - CarPointerOffset;
        if (ptr < CarPointerOffset || c < RaceHeapStart || c + CarBlockSize > Pcsx2Connection.RamSize || (c & 0xF) != 0)
        {
            car = input = 0;
            return;
        }
        if (c != car)
        {
            input = 0;
            ResetCarLimits();
        }
        car = c;
        if (input != 0) return;

        var window = new byte[InputSearchTo - InputSearchFrom + InputBlockSize];
        uint from = (uint)(c + InputSearchFrom + InputMarkerA);
        connection.Read(from, window);
        int best = int.MaxValue;
        for (int i = 0; i + InputBlockSize <= window.Length; i += 2)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(window.AsSpan(i)) != MarkerA
                || BinaryPrimitives.ReadUInt32LittleEndian(window.AsSpan(i + InputMarkerB - InputMarkerA)) != MarkerB) continue;
            uint thr = (uint)(from + i - InputMarkerA);
            int distance = Math.Abs((int)(thr - c) - InputExpected);
            if (distance < best)
            {
                best = distance;
                input = thr;
            }
        }
    }

    /// <summary>Reads the input block, and drops it if its markers have gone (a new race).</summary>
    private bool ReadInput()
    {
        connection.Read((uint)(input + InputMarkerA), inputBlock);
        if (BinaryPrimitives.ReadUInt32LittleEndian(inputBlock) == MarkerA
            && BinaryPrimitives.ReadUInt32LittleEndian(inputBlock.AsSpan(InputMarkerB - InputMarkerA)) == MarkerB) return true;
        input = 0;
        return false;
    }

    private float F(uint address)
    {
        float f = BinaryPrimitives.ReadSingleLittleEndian(block.AsSpan((int)(address - CarBlock)));
        return float.IsFinite(f) ? f : 0f;
    }

    private (float X, float Y, float Z) Vec(uint address) => (F(address), F(address + 4), F(address + 8));

    private static float Dot((float X, float Y, float Z) a, (float X, float Y, float Z) b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
}
