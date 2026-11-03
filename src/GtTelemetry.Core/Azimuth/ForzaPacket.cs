using System.Buffers.Binary;

namespace GtTelemetry.Core.Azimuth;

// What we know about the player's car in one frame. Ported from the FH1
// PinyonShift fork (src/telemetry/forza_dash.h, CarSample). Wheels are
// FL, FR, RL, RR. Everything left at zero stays zero in the packet.
public sealed class CarSample
{
    public bool Driving;          // a player car exists and the game is running (not paused)
    public float Rpm;
    public float IdleRpm;
    public float RedlineRpm;
    public int Gear;              // 0 neutral, -1 reverse, 1-10
    public float SpeedMps;
    public float VelocityForwardMps;
    public readonly float[] WheelSlip = new float[4];
    public readonly float[] WheelSlipAngle = new float[4];
    public readonly float[] WheelSpeedRadS = new float[4];
    public readonly float[] SuspensionM = new float[4];
    public readonly float[] SuspensionNormalized = new float[4];
    public readonly float[] SurfaceRumble = new float[4];
    public float Throttle;        // 0-1
    public float Brake;           // 0-1
    public float Clutch;          // 0-1
    public float Handbrake;       // 0-1
    public float Steer;           // -1 left to 1 right
    public float Power;           // W
    public float Torque;          // Nm
    public float Boost;
    public readonly float[] Position = new float[3];

    // Copies every field, so another thread can read a stable copy.
    public void CopyTo(CarSample o)
    {
        o.Driving = Driving; o.Rpm = Rpm; o.IdleRpm = IdleRpm; o.RedlineRpm = RedlineRpm; o.Gear = Gear;
        o.SpeedMps = SpeedMps; o.VelocityForwardMps = VelocityForwardMps;
        o.Throttle = Throttle; o.Brake = Brake; o.Clutch = Clutch; o.Handbrake = Handbrake; o.Steer = Steer;
        o.Power = Power; o.Torque = Torque; o.Boost = Boost;
        WheelSlip.CopyTo(o.WheelSlip, 0); WheelSlipAngle.CopyTo(o.WheelSlipAngle, 0);
        WheelSpeedRadS.CopyTo(o.WheelSpeedRadS, 0); SuspensionM.CopyTo(o.SuspensionM, 0);
        SuspensionNormalized.CopyTo(o.SuspensionNormalized, 0); SurfaceRumble.CopyTo(o.SurfaceRumble, 0);
        Position.CopyTo(o.Position, 0);
    }
}

// Forza Motorsport 7 "Data Out" Dash packet (311 bytes) and the Forza
// Horizon 4/5 variant (324 bytes: 12 extra bytes at offset 232). SimHub reads
// the Horizon layout with its Forza Horizon profile.
public static class ForzaPacket
{
    public const int DashSize = 311;
    public const int HorizonSize = 324;
    public const int HorizonGapOffset = 232;
    public const byte DashNeutralGear = 11;

    // Field offsets in the Dash layout.
    public const int IsRaceOn = 0;              // s32
    public const int TimestampMs = 4;           // u32
    public const int EngineMaxRpm = 8;          // f32
    public const int EngineIdleRpm = 12;        // f32
    public const int CurrentEngineRpm = 16;     // f32
    public const int VelocityZ = 40;            // f32 m/s, car forward axis
    public const int NormalizedSuspensionTravel = 68;  // 4 x f32
    public const int TireSlipRatio = 84;        // 4 x f32
    public const int WheelRotationSpeed = 100;  // 4 x f32 rad/s
    public const int SurfaceRumble = 148;       // 4 x f32
    public const int TireSlipAngle = 164;       // 4 x f32
    public const int TireCombinedSlip = 180;    // 4 x f32
    public const int SuspensionTravelMeters = 196;  // 4 x f32
    public const int PositionX = 232;           // 3 x f32, world position
    public const int Speed = 244;               // f32 m/s
    public const int Power = 248;               // f32 W
    public const int Torque = 252;              // f32 Nm
    public const int Boost = 272;               // f32
    public const int Accel = 303;               // u8
    public const int Brake = 304;               // u8
    public const int Clutch = 305;              // u8
    public const int HandBrake = 306;           // u8
    public const int Gear = 307;                // u8
    public const int Steer = 308;               // s8

    public static byte[] EncodeDash(CarSample s, uint timestampMs)
    {
        var p = new byte[DashSize];
        U32(p, TimestampMs, timestampMs);
        // Outside a car (menus, loading, paused) the packet says so and
        // carries no data, as the game's own Data Out does.
        if (!s.Driving) return p;
        U32(p, IsRaceOn, 1);
        F32(p, EngineMaxRpm, s.RedlineRpm);
        F32(p, EngineIdleRpm, s.IdleRpm);
        F32(p, CurrentEngineRpm, s.Rpm);
        F32(p, VelocityZ, s.VelocityForwardMps);
        for (int w = 0; w < 4; w++)
        {
            float ratio = s.WheelSlip[w], angle = s.WheelSlipAngle[w];
            F32(p, TireSlipRatio + 4 * w, ratio);
            F32(p, WheelRotationSpeed + 4 * w, s.WheelSpeedRadS[w]);
            F32(p, NormalizedSuspensionTravel + 4 * w, s.SuspensionNormalized[w]);
            F32(p, SuspensionTravelMeters + 4 * w, s.SuspensionM[w]);
            F32(p, SurfaceRumble + 4 * w, s.SurfaceRumble[w]);
            F32(p, TireSlipAngle + 4 * w, angle);
            F32(p, TireCombinedSlip + 4 * w, MathF.Sqrt(ratio * ratio + angle * angle));
        }
        for (int i = 0; i < 3; i++) F32(p, PositionX + 4 * i, s.Position[i]);
        F32(p, Speed, s.SpeedMps);
        F32(p, Power, s.Power);
        F32(p, Torque, s.Torque);
        F32(p, Boost, s.Boost);
        p[Accel] = Unit(s.Throttle);
        p[Brake] = Unit(s.Brake);
        p[Clutch] = Unit(s.Clutch);
        p[HandBrake] = Unit(s.Handbrake);
        p[Steer] = (byte)Signed(s.Steer);
        p[Gear] = s.Gear < 0 ? (byte)0 : s.Gear == 0 ? DashNeutralGear : (byte)Math.Min(s.Gear, 10);
        return p;
    }

    public static byte[] EncodeHorizon(CarSample s, uint timestampMs)
    {
        var dash = EncodeDash(s, timestampMs);
        var p = new byte[HorizonSize];
        Array.Copy(dash, 0, p, 0, HorizonGapOffset);
        Array.Copy(dash, HorizonGapOffset, p, HorizonGapOffset + 12, DashSize - HorizonGapOffset);
        return p;
    }

    static void U32(byte[] p, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(o), v);

    static void F32(byte[] p, int o, float v) =>
        BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(o), float.IsFinite(v) ? v : 0f);

    static byte Unit(float v) => float.IsFinite(v) ? (byte)MathF.Round(Math.Clamp(v, 0f, 1f) * 255f) : (byte)0;

    static sbyte Signed(float v) => float.IsFinite(v) ? (sbyte)MathF.Round(Math.Clamp(v, -1f, 1f) * 127f) : (sbyte)0;
}
