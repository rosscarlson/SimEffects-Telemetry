using System.Runtime.InteropServices;

namespace GtTelemetry.Core.Azimuth.Games;

// Where one game build keeps each field of its CCarDynamics object. Offsets
// are from the ICarDynamics sub-object (CCarDynamics + SubObject), except the
// wheel fields, which are within one per-wheel block.
public sealed class CarDynamicsLayout
{
    public required int SubObject { get; init; }

    public required int SteerAngle { get; init; }
    public required int SteerMax { get; init; }
    public required int EngineRadS { get; init; }
    public required int EngineMaxRadS { get; init; }
    public required int EngineIdleRadS { get; init; }
    public required int Torque { get; init; }
    public required int Power { get; init; }
    public required int Clutch { get; init; }
    public required int Gear { get; init; }
    public required int GearCount { get; init; }
    public required int Inputs { get; init; }
    public required int Speed { get; init; }
    public required int Position { get; init; }
    public required int PlayerFlag { get; init; }
    public required int AiControlled { get; init; }

    public required int Wheel0 { get; init; }
    public required int WheelStride { get; init; }
    public required int SuspensionPos { get; init; }
    public required int SpinRadS { get; init; }
    public required int SlipAngle { get; init; }
    public required int ContactVLong { get; init; }
    public required int TireRadius { get; init; }
    public required int SuspensionDroop { get; init; }
    public required int SuspensionBump { get; init; }
    public required int SuspensionRest { get; init; }

    public int Abs(int icdOffset) => SubObject + icdOffset;
    public int WheelAt(int wheel, int field) => Wheel0 + wheel * WheelStride + field;
}

// Turns one CCarDynamics memory block into a CarSample. Pure, so it is
// unit-tested with synthetic blocks. Every field comes from the game.
public static class CarDynamicsReader
{
    public const float RadSToRpm = 9.549296f;

    // Physical slip at which a typical tyre peaks, used to scale slip so that
    // 1 is roughly the limit of grip, as Forza's Data Out reports it.
    public const float PeakSlipRatio = 0.10f;
    public const float PeakSlipAngle = 0.14f;  // rad, about 8 degrees
    // Below this speed slip is measured against it, so it doesn't explode.
    const float MinSlipSpeed = 2f;

    // Raw gear byte -> gear (0 neutral, -1 reverse). Raw 0 is reverse (its
    // ratio is negative), 1.. are the forward gears; the count includes
    // reverse.
    public static int MapGear(byte raw, int gearCount) => raw switch
    {
        0 => -1,
        _ when raw >= gearCount && gearCount > 0 => 0,
        _ => raw,
    };

    public static bool IsPlayer(CarDynamicsLayout l, ReadOnlySpan<byte> b) => b[l.Abs(l.PlayerFlag)] == 1;

    public static bool IsAi(CarDynamicsLayout l, ReadOnlySpan<byte> b) => I(l, b, l.AiControlled) != 0;

    static float F(CarDynamicsLayout l, ReadOnlySpan<byte> b, int icd) => MemoryMarshal.Read<float>(b[l.Abs(icd)..]);
    static int I(CarDynamicsLayout l, ReadOnlySpan<byte> b, int icd) => MemoryMarshal.Read<int>(b[l.Abs(icd)..]);
    static float W(CarDynamicsLayout l, ReadOnlySpan<byte> b, int wheel, int field) => F(l, b, l.WheelAt(wheel, field));

    public static void Fill(CarDynamicsLayout l, ReadOnlySpan<byte> b, CarSample s)
    {
        s.Driving = true;
        s.Rpm = MathF.Max(0, F(l, b, l.EngineRadS)) * RadSToRpm;
        s.RedlineRpm = F(l, b, l.EngineMaxRadS) * RadSToRpm;
        s.IdleRpm = F(l, b, l.EngineIdleRadS) * RadSToRpm;
        s.Torque = F(l, b, l.Torque);
        s.Power = F(l, b, l.Power);
        s.Clutch = F(l, b, l.Clutch);
        s.Throttle = F(l, b, l.Inputs + 0x0);
        s.Brake = F(l, b, l.Inputs + 0x4);
        s.Handbrake = F(l, b, l.Inputs + 0x10);
        float steerMax = F(l, b, l.SteerMax);
        s.Steer = steerMax != 0 ? F(l, b, l.SteerAngle) / steerMax : 0;  // -1 left, 1 right
        s.Gear = MapGear(b[l.Abs(l.Gear)], I(l, b, l.GearCount));
        s.SpeedMps = F(l, b, l.Speed);
        for (int i = 0; i < 3; i++) s.Position[i] = F(l, b, l.Position + 4 * i);

        float spinSum = 0;
        for (int w = 0; w < 4; w++)
        {
            float spin = W(l, b, w, l.SpinRadS);
            spinSum += spin;
            s.WheelSpeedRadS[w] = spin;

            // Suspension: metres from rest (positive compressed), and 0 at
            // full extension to 1 at the bump stop.
            float pos = W(l, b, w, l.SuspensionPos);
            float droop = W(l, b, w, l.SuspensionDroop), bump = W(l, b, w, l.SuspensionBump);
            s.SuspensionM[w] = pos - W(l, b, w, l.SuspensionRest);
            s.SuspensionNormalized[w] = bump != droop ? Math.Clamp((pos - droop) / (bump - droop), 0f, 1f) : 0f;

            // Slip ratio from the tyre's surface speed against the contact
            // patch's speed; slip angle is the game's own.
            // A wheel in the air has a contact velocity of exactly 0 (FH3
            // probe drive): no grip to slip against, so report none.
            float vx = W(l, b, w, l.ContactVLong), r = W(l, b, w, l.TireRadius);
            bool airborne = vx == 0 && s.SpeedMps > 1;
            s.WheelSlip[w] = airborne ? 0 : (spin * r - vx) / MathF.Max(MathF.Abs(vx), MinSlipSpeed) / PeakSlipRatio;
            s.WheelSlipAngle[w] = airborne ? 0 : W(l, b, w, l.SlipAngle) / PeakSlipAngle;
        }
        // Forward velocity is negative when reversing (wheels spin backwards).
        s.VelocityForwardMps = spinSum < 0 ? -s.SpeedMps : s.SpeedMps;
    }
}
