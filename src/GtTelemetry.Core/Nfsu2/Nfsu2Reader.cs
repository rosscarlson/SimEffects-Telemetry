using System.Buffers.Binary;
using System.Diagnostics;
using GtTelemetry.Core.Browser;
using GtTelemetry.Core.Memory;
using GtTelemetry.Core.Telemetry;

namespace GtTelemetry.Core.Nfsu2;

/// <summary>
/// NFSU2 telemetry on nfsu2.xyz, through the extension (docs/nfsu2/FINDINGS.md, data/nfsu2-web/addresses.json). Player
/// 1's car is <c>[[0x00D00000] + 4]</c>; the reader keeps the player, the car and its input block, and checks them on
/// every frame. One frame is one round trip: the game's globals, the car and its inputs in a single read.
/// Read-only.
/// </summary>
/// <remarks>
/// The game's world has Z up; a car's orientation is three rows: forward, left and up. Guest addresses are offsets
/// into the page's WebAssembly memory.
/// </remarks>
public sealed class Nfsu2Reader(BrowserMemorySource memory) : ITelemetrySource
{
    // Globals (runtime/game.h in th0mk/nfsu2-web).
    private const uint WorldBlock = 0x00865150;     // read 0x40 bytes: the world step and the world timer
    private const int WorldDt = 0x04;               // f32 [0x865154]: this frame's world step, s; 0 while the world stands
    private const int WorldTimer = 0x3C;            // u32 [0x86518C]: the race clock, 1/4000 s
    private const int WorldBlockSize = 0x40;
    private const uint GameFlow = 0x008654A4;       // u32: 3 front end, 4/5 loading, 6 in the world
    private const uint Player1 = 0x00D00000;        // Player*: +4 its car

    // A car (the World's list).
    private const int CarInput = 0x30;              // its input block
    private const int CarPosition = 0x60;           // f32 ×3, world
    private const int CarVelocity = 0x70;           // f32 ×3, world, m/s
    private const int CarAccel = 0x80;              // f32 ×3, world, m/s², gravity excluded
    private const int CarForward = 0x90;            // f32 ×3, unit; then left (+0xA0) and up (+0xB0)
    private const int CarLeft = 0xA0;
    private const int CarUp = 0xB0;
    private const int CarSpin = 0x110;              // f32 ×3, world, rad/s
    private const int CarSpeed = 0x430;             // f32 |v|, m/s
    private const int CarGear = 0x434;              // s32: 0 reverse, 1 neutral, 2 first …
    private const int CarRpm = 0x440;               // f32
    private const int CarSize = 0x444;

    // Its input block, read from +0x1E0.
    private const int InputFrom = 0x1E0;
    private const int InputSteer = 0x1E0 - InputFrom;       // s16: ±8192 at full lock with a wheel, + left
    private const int InputThrottle = 0x20C - InputFrom;    // f32 0–1
    private const int InputHandbrake = 0x210 - InputFrom;   // f32 0–1
    private const int InputBrake = 0x214 - InputFrom;       // f32 0–1
    private const int InputSize = 0x218 - InputFrom;

    // Full left is +8192 (measured with a wheel on 2026-11-09, the same either way).
    private const float SteerScale = -1f / 8192f;

    private const uint GuestFrom = 0x00010000, GuestTo = 0x28000000;
    private const int PausedAfterMs = 150;

    private readonly Stopwatch timerAge = new();
    private uint player, car, input;
    private uint lastTimer;
    private float maxRpm, idleRpm;

    /// <summary>The car in use, 0 if none.</summary>
    public uint Car => car;

    public TelemetryFrame Read()
    {
        if (!memory.IsConnected)
        {
            Forget();
            return TelemetryFrame.NotDriving;
        }
        try { return ReadFrame(); }
        catch (MemorySourceException)
        {
            Forget();
            return TelemetryFrame.NotDriving;
        }
    }

    private TelemetryFrame ReadFrame()
    {
        List<(uint, int)> ranges = [(WorldBlock, WorldBlockSize), (GameFlow, 4), (Player1, 4)];
        bool known = car != 0;
        if (known) ranges.AddRange([(player + 4, 4), (car, CarSize), (input + InputFrom, InputSize)]);
        var r = memory.ReadMany(ranges);

        float dt = BinaryPrimitives.ReadSingleLittleEndian(r[0].AsSpan(WorldDt));
        uint timer = BinaryPrimitives.ReadUInt32LittleEndian(r[0].AsSpan(WorldTimer));
        uint flow = U32(r[1], 0), p = U32(r[2], 0);
        if (flow != 6 || !Guest(p))
        {
            Forget();
            return TelemetryFrame.NotDriving;
        }
        if (!known || p != player || U32(r[3], 0) != car || U32(r[4], CarInput) != input)
        {
            Resolve(p);
            return TelemetryFrame.NotDriving;
        }

        var c = r[4];
        var i = r[5];
        var fwd = Vec(c, CarForward);
        var left = Vec(c, CarLeft);
        var up = Vec(c, CarUp);
        if (!Unit(fwd) || !Unit(left) || !Unit(up))
        {
            Forget();
            return TelemetryFrame.NotDriving;
        }

        // The world stands while the game is paused, in a menu over the race, or when the tab is hidden (the browser
        // stops the game's frames): the step is 0, or the clock stops.
        if (timer != lastTimer || !timerAge.IsRunning)
        {
            lastTimer = timer;
            timerAge.Restart();
        }
        if (!(dt > 0 && dt < 0.25f) || timerAge.ElapsedMilliseconds > PausedAfterMs) return TelemetryFrame.NotDriving;

        float rpm = F(c, CarRpm), speed = F(c, CarSpeed);
        int rawGear = BinaryPrimitives.ReadInt32LittleEndian(c.AsSpan(CarGear));
        float throttle = Math.Clamp(F(i, InputThrottle), 0, 1), brake = Math.Clamp(F(i, InputBrake), 0, 1);
        if (!(rpm is >= 0 and < 25000 && speed < 200 && rawGear is >= 0 and <= 10)) return TelemetryFrame.NotDriving;

        if (rpm > maxRpm) maxRpm = rpm;
        if (throttle < 0.02f && speed < 0.5f && rpm > 300 && (idleRpm == 0 || rpm < idleRpm)) idleRpm = rpm;

        var right = -left;
        var vel = Vec(c, CarVelocity);
        var spin = Vec(c, CarSpin);
        var accel = Vec(c, CarAccel);
        var pos = Vec(c, CarPosition);
        return new TelemetryFrame
        {
            Driving = true,
            Rpm = rpm,
            MaxRpm = Math.Max(maxRpm, 7000),
            IdleRpm = idleRpm > 0 ? idleRpm : 1000,
            SpeedMps = speed,
            VelocityForwardMps = vel.Dot(fwd),
            VelocityRightMps = vel.Dot(right),
            VelocityUpMps = vel.Dot(up),
            AccelForward = accel.Dot(fwd),
            AccelRight = accel.Dot(right),
            AccelUp = accel.Dot(up),
            // The world's spin is right-handed about the car's axes: about up is turning left, about right is nose up,
            // about forward is rolling right. The frame's are turning right, nose down and rolling left.
            YawRate = -spin.Dot(up),
            PitchRate = -spin.Dot(right),
            RollRate = -spin.Dot(fwd),
            Yaw = MathF.Atan2(fwd.X, fwd.Y),
            Pitch = -MathF.Asin(Math.Clamp(fwd.Z, -1f, 1f)),
            Roll = MathF.Asin(Math.Clamp(right.Z, -1f, 1f)),
            PositionX = pos.X,
            PositionY = pos.Z,
            PositionZ = pos.Y,
            Throttle = throttle,
            Brake = brake,
            Handbrake = Math.Clamp(F(i, InputHandbrake), 0, 1),
            Steer = Math.Clamp(BinaryPrimitives.ReadInt16LittleEndian(i.AsSpan(InputSteer)) * SteerScale, -1f, 1f),
            Gear = rawGear - 1,
        };
    }

    /// <summary>Follows the player to its car and the car's input block (one more round trip, when they change).</summary>
    private void Resolve(uint p)
    {
        Forget();
        uint c = U32(memory.ReadMany([(p + 4, 4)])[0], 0);
        if (!Guest(c)) return;
        uint i = U32(memory.ReadMany([(c + CarInput, 4)])[0], 0);
        if (!Guest(i)) return;
        (player, car, input) = (p, c, i);
        maxRpm = idleRpm = 0;
    }

    private void Forget()
    {
        player = car = input = 0;
        timerAge.Reset();
    }

    private static bool Guest(uint a) => a is >= GuestFrom and < GuestTo;

    private static bool Unit(Vec3 v) => MathF.Abs(v.Dot(v) - 1) < 0.02f;

    private static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));

    private static float F(byte[] b, int o)
    {
        float f = BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(o));
        return float.IsFinite(f) ? f : 0f;
    }

    private static Vec3 Vec(byte[] b, int o) => new(F(b, o), F(b, o + 4), F(b, o + 8));

    private readonly record struct Vec3(float X, float Y, float Z)
    {
        public float Dot(Vec3 o) => X * o.X + Y * o.Y + Z * o.Z;

        public static Vec3 operator -(Vec3 a) => new(-a.X, -a.Y, -a.Z);

    }
}
