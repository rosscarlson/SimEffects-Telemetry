using static GtTelemetry.Core.Azimuth.Games.Fh3.Fh3Layout;

namespace GtTelemetry.Core.Azimuth.Games.Fh3;

// Forza Horizon 3, Xbox One build (Opus 1.0.125.0), in Azimuth.
public sealed class Fh3Game : GameDefinition
{
    public static readonly Fh3Game Instance = new();

    public override string Id => "fh3";
    public override string Name => "Forza Horizon 3";
    public override string ShortName => "FH3";
    public override string BuildDescription => "Xbox One forza_durango_release_final.exe 1.0.125.0 on Azimuth 1.0.x";

    public override int BlockSize => Fh3Layout.BlockSize;
    public override int ProbeBlockSize => Fh3Layout.ProbeBlockSize;
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
    // FOV 0x1F0 and seat 0x1B0; the CCamFollow* chase views read FOV from their
    // view data (0x1A0 -> +0x1C). See docs/FINDINGS.md, In-car camera.
    public override IReadOnlyList<CameraView> CameraViews { get; } = new CameraView[]
    {
        new("cockpit", "Cockpit", new[] { 0x14463ACB0UL }, 0, Fov: 0x1F0, Seat: 0x1B0),
        new("dashboard", "Dashboard", new[] { 0x14463A710UL }, 1, Fov: 0x1F0, Seat: 0x1B0),
        new("hood", "Hood", new[] { 0x14463A9E0UL }, 3, Fov: 0x1F0, Seat: 0x1B0),
        new("bumper", "Bumper", new[] { 0x144630430UL }, 4, Fov: 0x1F0, Seat: 0x1B0),
        new("chase", "Chase", new[] { 0x14463AF88UL }, 2, FovData: 0x1A0, FovDataField: 0x1C),
        new("chase_near", "Chase near", new[] { 0x14463B250UL }, 2, FovData: 0x1A0, FovDataField: 0x1C),
        new("chase_far", "Chase far", new[] { 0x14463B518UL }, 2, FovData: 0x1A0, FovDataField: 0x1C),
    };

    public override bool IsPlayer(ReadOnlySpan<byte> block) => CarDynamicsReader.IsPlayer(Layout, block);
    public override bool IsAi(ReadOnlySpan<byte> block) => CarDynamicsReader.IsAi(Layout, block);
    public override void Fill(ReadOnlySpan<byte> block, CarSample s) => CarDynamicsReader.Fill(Layout, block, s);
}
