using System.Buffers.Binary;

namespace GtTelemetry.Core.Telemetry;

public enum ForzaFormat
{
    /// <summary>Forza Horizon 4/5 layout, 324 bytes: the Dash fields with 12 extra bytes after the Sled block.</summary>
    Horizon,
    /// <summary>Forza Motorsport 7 "Dash" layout, 311 bytes.</summary>
    Dash,
}

/// <summary>
/// Encodes frames as Forza "Data Out" packets, which SimHub and most dash tools read natively. Ported from the Pinyon
/// Shift encoder (FH2-Wheel <c>forza_dash.cpp</c>) so every game in the app speaks the same format. Fields a game doesn't
/// provide stay zero.
/// </summary>
public static class ForzaDataOut
{
    public const int DashSize = 311;
    public const int HorizonSize = 324;
    private const int HorizonGapOffset = 232;   // end of the Sled block
    private const int HorizonGap = 12;

    /// <summary>FM7 sends neutral as 11 and reverse as 0.</summary>
    public const byte NeutralGear = 11;

    // Offsets in the Dash layout. Sled block:
    private const int IsRaceOn = 0;              // s32
    private const int TimestampMs = 4;           // u32
    private const int EngineMaxRpm = 8;          // f32
    private const int EngineIdleRpm = 12;
    private const int CurrentEngineRpm = 16;
    private const int AccelerationX = 20;        // f32 ×3, car frame: right, up, forward
    private const int VelocityX = 32;            // f32 ×3
    private const int AngularVelocityX = 44;     // f32 ×3: pitch, yaw, roll rates
    private const int YawOffset = 56;            // f32 ×3: yaw, pitch, roll
    private const int NormalizedSuspensionTravel = 68;   // f32 ×4, FL FR RL RR
    private const int TireSlipRatio = 84;
    private const int WheelRotationSpeed = 100;
    private const int WheelOnRumbleStrip = 116;  // s32 ×4
    private const int SurfaceRumble = 148;
    private const int TireSlipAngle = 164;
    private const int TireCombinedSlip = 180;
    private const int SuspensionTravelMeters = 196;
    // Dash fields:
    private const int PositionX = 232;           // f32 ×3
    private const int Speed = 244;               // f32 m/s
    private const int Accel = 303;               // u8
    private const int BrakeOffset = 304;         // u8
    private const int ClutchOffset = 305;        // u8
    private const int HandBrake = 306;           // u8
    private const int GearOffset = 307;          // u8
    private const int SteerOffset = 308;         // s8

    public static int Size(ForzaFormat format) => format == ForzaFormat.Horizon ? HorizonSize : DashSize;

    public static byte[] Encode(TelemetryFrame f, uint timestampMs, ForzaFormat format)
    {
        var dash = new byte[DashSize];
        var s = dash.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(s[TimestampMs..], timestampMs);
        // Outside a car the packet says so and carries no data, as the games' own Data Out does.
        if (f.Driving)
        {
            BinaryPrimitives.WriteInt32LittleEndian(s[IsRaceOn..], 1);
            F32(s, EngineMaxRpm, f.MaxRpm);
            F32(s, EngineIdleRpm, f.IdleRpm);
            F32(s, CurrentEngineRpm, f.Rpm);
            F32x3(s, AccelerationX, f.AccelRight, f.AccelUp, f.AccelForward);
            F32x3(s, VelocityX, f.VelocityRightMps, f.VelocityUpMps, f.VelocityForwardMps);
            F32x3(s, AngularVelocityX, f.PitchRate, f.YawRate, f.RollRate);
            F32x3(s, YawOffset, f.Yaw, f.Pitch, f.Roll);
            if (f.Wheels is { Length: 4 } wheels)
            {
                for (int i = 0; i < 4; i++)
                {
                    var w = wheels[i];
                    F32(s, NormalizedSuspensionTravel + 4 * i, w.SuspensionNormalized);
                    F32(s, TireSlipRatio + 4 * i, w.SlipRatio);
                    F32(s, WheelRotationSpeed + 4 * i, w.RotationSpeed);
                    BinaryPrimitives.WriteInt32LittleEndian(s[(WheelOnRumbleStrip + 4 * i)..], w.OnRumbleStrip ? 1 : 0);
                    F32(s, SurfaceRumble + 4 * i, w.SurfaceRumble);
                    F32(s, TireSlipAngle + 4 * i, w.SlipAngle);
                    // Both slips are normalised to the tyre's peak, so their length is 1 where the tyre lets go in any
                    // direction, as FM7 reports it.
                    F32(s, TireCombinedSlip + 4 * i, MathF.Sqrt(w.SlipRatio * w.SlipRatio + w.SlipAngle * w.SlipAngle));
                    F32(s, SuspensionTravelMeters + 4 * i, w.SuspensionMeters);
                }
            }
            F32x3(s, PositionX, f.PositionX, f.PositionY, f.PositionZ);
            F32(s, Speed, f.SpeedMps);
            s[Accel] = Unit(f.Throttle);
            s[BrakeOffset] = Unit(f.Brake);
            s[ClutchOffset] = Unit(f.Clutch);
            s[HandBrake] = Unit(f.Handbrake);
            s[GearOffset] = f.Gear < 0 ? (byte)0 : f.Gear == 0 ? NeutralGear : (byte)Math.Min(f.Gear, 10);
            s[SteerOffset] = (byte)Signed(f.Steer);
        }
        if (format == ForzaFormat.Dash) return dash;

        var wide = new byte[HorizonSize];
        dash.AsSpan(0, HorizonGapOffset).CopyTo(wide);
        dash.AsSpan(HorizonGapOffset).CopyTo(wide.AsSpan(HorizonGapOffset + HorizonGap));
        return wide;
    }

    private static void F32(Span<byte> s, int offset, float v) =>
        BinaryPrimitives.WriteSingleLittleEndian(s[offset..], float.IsFinite(v) ? v : 0f);

    private static void F32x3(Span<byte> s, int offset, float a, float b, float c)
    {
        F32(s, offset, a);
        F32(s, offset + 4, b);
        F32(s, offset + 8, c);
    }

    private static byte Unit(float v) => float.IsFinite(v) ? (byte)MathF.Round(Math.Clamp(v, 0f, 1f) * 255f) : (byte)0;

    private static sbyte Signed(float v) => float.IsFinite(v) ? (sbyte)MathF.Round(Math.Clamp(v, -1f, 1f) * 127f) : (sbyte)0;
}
