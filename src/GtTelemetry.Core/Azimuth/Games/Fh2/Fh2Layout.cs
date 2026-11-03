namespace GtTelemetry.Core.Azimuth.Games.Fh2;

// Addresses and field offsets for forza_durango_release_final.exe 1.0.0.64
// (PE timestamp 565501ED), loaded by Azimuth at its preferred base
// 0x140000000. Found by static analysis of the game executable; see
// docs/FINDINGS.md for how each one was derived.
//
// The player's car physics live in a CCarDynamics object on the heap. It is
// found by its vtables (static addresses in the game image), so nothing here
// depends on heap addresses that change between sessions.
public static class Fh2Layout
{
    public const ulong ImageBase = 0x140000000;
    public const uint PeTimestamp = 0x565501ED;

    // CCarDynamics primary vtable and its ICarDynamics sub-object vtable at
    // +0x3A0, plus the same pair for TRefCountedObjectThreadSafe<CCarDynamics>.
    public const ulong VtCarDynamics = 0x142EB4B08;
    public const ulong VtICarDynamics = 0x142EB4BB8;
    public const ulong VtCarDynamicsRc = 0x142EB5D68;
    public const ulong VtICarDynamicsRc = 0x142EB5E18;
    public const int ICarDynamicsOffset = 0x3A0;

    // Fingerprint: ICarDynamics vtable entry 0x1A0 (the telemetry HUD's RPM
    // getter) must point at this function.
    public const ulong VtRpmSlot = VtICarDynamics + 0x1A0;
    public const ulong RpmGetter = 0x1412C81A0;

    // One read covers every field below.
    public const int BlockSize = 0x5400;

    public const float RadSToRpm = 9.549296f;

    // Offsets from the ICarDynamics pointer (CCarDynamics + 0x3A0), each
    // taken from the getter the game's own telemetry HUD calls.
    public static class Icd
    {
        public const int SteerAngle = 0x008;      // / SteerMax = HUD SteeringAngle
        public const int SteerMax = 0x058;
        public const int EngineRadS = 0x148;      // current engine speed
        public const int Torque = 0x18C;          // HUD EngineTorque
        public const int Power = 0x194;           // HUD EnginePower
        public const int EngineMaxRadS = 0x1CC;   // HUD clamps RPM to this
        public const int EngineIdleRadS = 0x5C0;  // normalised RPM = (cur-idle)/(max-idle)
        public const int BoostActive = 0xA24;     // non-zero: car has forced induction
        public const int BoostRaw = 0x170;
        public const int Clutch = 0xA8C;          // HUD ClutchMeter
        public const int Gear = 0xAA0;            // byte; indexes the ratio table
        public const int GearCount = 0xABC;
        public const int GearRatios = 0xAC0;      // 12-byte stride
        public const int FinalDrive = 0xB90;
        public const int Inputs = 0xDF8;          // +0 accel, +4 brake, +0x10 handbrake
        public const int Speed = 0xE28;           // m/s, always positive (vtable getter 0x2A0)
        public const int Position = 0xEF0;        // vec4, world position

        // Which car is the player's (probe drive, 2026-10-29): this byte is 1
        // on the player's car and 0 on every AI and traffic car; the int at
        // AiControlled is 0 on the player's car and 1 on all others.
        public const int PlayerFlag = 0x4EE7;
        public const int AiControlled = 0xFBC;

        // Per-wheel blocks, FL FR RL RR. Offsets below are within a block.
        public const int Wheel0 = 0x11B0;
        public const int WheelStride = 0x4B0;
        public const int WheelTirePtr = 0x1640;    // -> tire data; +8 = diameter, inches
    }

    public static class Wheel
    {
        public const int SuspensionPos = 0x000;   // wheel height vs body, m (more negative = extended)
        public const int SpinRadS = 0x00C;        // wheel rotation, rad/s
        public const int SlipAngle = 0x024;       // rad, signed (0 in straight-line braking)
        public const int ContactVLong = 0x2C4;    // contact-patch velocity along the tyre, m/s
        public const int TireRadius = 0x3C0;      // m
        public const int SuspensionDroop = 0x3F4; // SuspensionPos at full extension
        public const int SuspensionBump = 0x3FC;  // SuspensionPos at full compression
        public const int SuspensionRest = 0x484;  // SuspensionPos at rest

        public static int At(int wheel, int field) => Icd.Wheel0 + wheel * Icd.WheelStride + field;
    }

    public static int Abs(int icdOffset) => ICarDynamicsOffset + icdOffset;
}
