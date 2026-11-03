using static GtTelemetry.Core.Azimuth.Games.Fh2.Fh2Layout;

namespace GtTelemetry.Core.Azimuth.Games.Fh2;

public sealed class Fh2Game : GameDefinition
{
    public static readonly Fh2Game Instance = new();

    public override string Id => "fh2";
    public override string Name => "Forza Horizon 2";
    public override string ShortName => "FH2";
    public override string BuildDescription => "forza_durango_release_final.exe 1.0.0.64 on Azimuth 1.0.x";

    public override int BlockSize => Fh2Layout.BlockSize;
    public override int SubObjectOffset => ICarDynamicsOffset;
    public override IReadOnlyList<(ulong Primary, ulong Sub)> VtablePairs { get; } =
        new[] { (VtCarDynamics, VtICarDynamics), (VtCarDynamicsRc, VtICarDynamicsRc) };

    public override bool CheckFingerprint(GameProcess g, out string why)
    {
        if (!CheckPeTimestamp(g, ImageBase, PeTimestamp, out why)) return false;
        if (!g.TryU64(VtRpmSlot, out ulong f) || f != RpmGetter)
        {
            why = "The car-physics vtable doesn't match this build.";
            return false;
        }
        return true;
    }

    // Camera::CCamDriver (cockpit), CCamCockpit, CCamHood, CCamBumperHigh share
    // FOV 0x1D0 and seat 0x190; the CCamFollow* chase views read FOV from their
    // view data (0x180 -> +0x1C). See docs/FINDINGS.md, In-car camera.
    public override IReadOnlyList<CameraView> CameraViews { get; } = new CameraView[]
    {
        new("cockpit", "Cockpit", new[] { 0x142E78120UL }, 0, Fov: 0x1D0, Seat: 0x190),
        new("dashboard", "Dashboard", new[] { 0x142E77C30UL }, 1, Fov: 0x1D0, Seat: 0x190),
        new("hood", "Hood", new[] { 0x142E77EA8UL }, 3, Fov: 0x1D0, Seat: 0x190),
        new("bumper", "Bumper", new[] { 0x142E703B0UL }, 4, Fov: 0x1D0, Seat: 0x190),
        new("chase", "Chase", new[] { 0x142E783A0UL }, 2, FovData: 0x180, FovDataField: 0x1C),
        new("chase_near", "Chase near", new[] { 0x142E78610UL }, 2, FovData: 0x180, FovDataField: 0x1C),
        new("chase_far", "Chase far", new[] { 0x142E78880UL }, 2, FovData: 0x180, FovDataField: 0x1C),
    };

    public override bool IsPlayer(ReadOnlySpan<byte> block) => Fh2Reader.IsPlayer(block);
    public override bool IsAi(ReadOnlySpan<byte> block) => Fh2Reader.IsAi(block);
    public override void Fill(ReadOnlySpan<byte> block, CarSample s) => Fh2Reader.Fill(block, s);
}
