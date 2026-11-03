using static GtTelemetry.Core.Azimuth.Games.Fh2.Fh2Layout;

namespace GtTelemetry.Core.Azimuth.Games.Fh2;

// FH2's CCarDynamics block -> CarSample (the shared CarDynamicsReader with
// FH2's offsets).
public static class Fh2Reader
{
    public const float PeakSlipRatio = CarDynamicsReader.PeakSlipRatio;
    public const float PeakSlipAngle = CarDynamicsReader.PeakSlipAngle;

    public static readonly CarDynamicsLayout Layout = new()
    {
        SubObject = ICarDynamicsOffset,
        SteerAngle = Icd.SteerAngle, SteerMax = Icd.SteerMax,
        EngineRadS = Icd.EngineRadS, EngineMaxRadS = Icd.EngineMaxRadS, EngineIdleRadS = Icd.EngineIdleRadS,
        Torque = Icd.Torque, Power = Icd.Power, Clutch = Icd.Clutch,
        Gear = Icd.Gear, GearCount = Icd.GearCount, Inputs = Icd.Inputs,
        Speed = Icd.Speed, Position = Icd.Position,
        PlayerFlag = Icd.PlayerFlag, AiControlled = Icd.AiControlled,
        Wheel0 = Icd.Wheel0, WheelStride = Icd.WheelStride,
        SuspensionPos = Wheel.SuspensionPos, SpinRadS = Wheel.SpinRadS, SlipAngle = Wheel.SlipAngle,
        ContactVLong = Wheel.ContactVLong, TireRadius = Wheel.TireRadius,
        SuspensionDroop = Wheel.SuspensionDroop, SuspensionBump = Wheel.SuspensionBump, SuspensionRest = Wheel.SuspensionRest,
    };

    public static int MapGear(byte raw, int gearCount) => CarDynamicsReader.MapGear(raw, gearCount);
    public static bool IsPlayer(ReadOnlySpan<byte> b) => CarDynamicsReader.IsPlayer(Layout, b);
    public static bool IsAi(ReadOnlySpan<byte> b) => CarDynamicsReader.IsAi(Layout, b);
    public static void Fill(ReadOnlySpan<byte> b, CarSample s) => CarDynamicsReader.Fill(Layout, b, s);
}
