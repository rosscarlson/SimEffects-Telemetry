namespace GtTelemetry.Core.Azimuth.Games.Fh3;

// Addresses and field offsets for the Xbox One Forza Horizon 3
// forza_durango_release_final.exe (Opus 1.0.125.0, PE timestamp 5B16ECCA),
// loaded by Azimuth at its preferred base 0x140000000. Found by matching its
// ICarDynamics vtable (474 entries) against FH2's getter by getter, then
// checked read-only against the running game; see docs/FINDINGS.md.
public static class Fh3Layout
{
    public const ulong ImageBase = 0x140000000;
    public const uint PeTimestamp = 0x5B16ECCA;

    // CCarDynamics primary vtable and its ICarDynamics sub-object vtable at
    // +0x4B0, plus the same pair for
    // TRefCountedObjectThreadSafeWithDeletionInfo<CCarDynamics>.
    public const ulong VtCarDynamics = 0x144680EA0;
    public const ulong VtICarDynamics = 0x144680F30;
    public const ulong VtCarDynamicsRc = 0x144682578;
    public const ulong VtICarDynamicsRc = 0x144682608;
    public const int ICarDynamicsOffset = 0x4B0;

    // Fingerprint: ICarDynamics vtable entry 0x1B8 (the clamped RPM getter,
    // FH2's 0x1A0) must point at this function.
    public const ulong VtRpmSlot = VtICarDynamics + 0x1B8;
    public const ulong RpmGetter = 0x141D3FE10;

    // Covers every field below (the player flag is near the end of the object).
    public const int BlockSize = 0xD140;
    // The probe drive keeps engine, inputs and wheels only, to stay a
    // reasonable size with ~30 cars.
    public const int ProbeBlockSize = 0x7000;

    // Offsets from the ICarDynamics pointer (CCarDynamics + 0x4B0); the FH2
    // vtable entry each one matches is in docs/FINDINGS.md.
    public static class Icd
    {
        public const int SteerAngle = 0x008;
        public const int SteerMax = 0x058;
        public const int EngineRadS = 0x148;
        public const int Torque = 0x18C;
        public const int Power = 0x194;
        public const int EngineMaxRadS = 0x1D4;
        public const int EngineIdleRadS = 0x5C8;
        public const int Clutch = 0xA94;
        public const int Gear = 0xAA8;            // byte
        public const int GearCount = 0xAC4;
        public const int Inputs = 0x1298;         // +0 accel, +4 brake, +0x10 handbrake
        public const int Speed = 0x12D4;          // m/s
        public const int Position = 0x13A0;      // vec4
        public const int PlayerFlag = 0xCB89;     // byte, 1 = player's car
        public const int AiControlled = 0x1534;   // int, 0 on the player's car

        public const int Wheel0 = 0x1750;
        public const int WheelStride = 0x1490;
    }

    public static class Wheel
    {
        public const int SuspensionPos = 0x000;
        public const int SpinRadS = 0x00C;
        public const int SlipAngle = 0x048;
        public const int ContactVLong = 0x2FC;
        public const int TireRadius = 0x414;
        public const int SuspensionDroop = 0x458;
        public const int SuspensionBump = 0x460;
        public const int SuspensionRest = 0x524;
    }

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
}
