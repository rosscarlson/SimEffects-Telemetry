using System.Runtime.InteropServices;

namespace GtTelemetry.Core.Azimuth.Games;

// One spoken instruction in a probe drive, At seconds after recording starts.
public readonly record struct ProbeCue(double At, string Say);

// Everything the telemetry engine and the probe need to know about one game
// running in azimuth.exe: how to check the build, how to recognise a car's
// physics object in memory, and how to turn it into a CarSample.
public abstract class GameDefinition
{
    public abstract string Id { get; }          // stored in settings, e.g. "fh2"
    public abstract string Name { get; }        // "Forza Horizon 2"
    public abstract string ShortName { get; }   // "FH2"
    public abstract string BuildDescription { get; }

    // Null when telemetry works for this game; otherwise why it doesn't yet.
    public virtual string? NotMappedReason => null;
    public bool IsMapped => NotMappedReason == null;

    // The car's physics object: its size, and the vtable pairs (primary at
    // +0, sub-object at +SubObjectOffset) that identify it.
    public virtual int BlockSize => 0;
    // How much of each car the probe drive records (BlockSize unless that's
    // too big to keep for every car).
    public virtual int ProbeBlockSize => BlockSize;
    public virtual int SubObjectOffset => 0;
    public virtual IReadOnlyList<(ulong Primary, ulong Sub)> VtablePairs => Array.Empty<(ulong, ulong)>();

    public virtual bool CheckFingerprint(GameProcess g, out string why)
    {
        why = NotMappedReason ?? "";
        return false;
    }

    // The driving camera views, for the camera adjuster.
    public virtual IReadOnlyList<CameraView> CameraViews => Array.Empty<CameraView>();

    public virtual bool IsPlayer(ReadOnlySpan<byte> block) => false;
    public virtual bool IsAi(ReadOnlySpan<byte> block) => false;
    public virtual void Fill(ReadOnlySpan<byte> block, CarSample s) { }

    // Spoken cues for the probe drive; about 2.5 minutes.
    public virtual IReadOnlyList<ProbeCue> ProbeScript => DefaultProbeScript;
    public virtual int ProbeSeconds => 150;

    public static readonly ProbeCue[] DefaultProbeScript =
    {
        new(0,   "Probe started. Sit still in neutral with the engine idling."),
        new(10,  "Stay stopped. Rev to the limiter three times."),
        new(22,  "Accelerate hard through the gears to about sixty miles per hour."),
        new(37,  "Hold a steady fifty miles per hour."),
        new(47,  "Brake hard to a stop."),
        new(55,  "Select reverse and back up slowly."),
        new(63,  "Stop. Drive forward slowly, steer full left, then full right."),
        new(75,  "Drive off road over rough ground, bumps or kerbs."),
        new(100, "Back on the road. Do a handbrake turn or a big slide."),
        new(115, "Pause the game now, and wait."),
        new(123, "Unpause and drive normally."),
    };

    public bool IsCar(ReadOnlySpan<byte> block)
    {
        if (block.Length < SubObjectOffset + 8 || block.Length < 8) return false;
        ulong a = MemoryMarshal.Read<ulong>(block);
        ulong b = MemoryMarshal.Read<ulong>(block[SubObjectOffset..]);
        foreach (var (p, s) in VtablePairs)
            if (a == p && b == s) return true;
        return false;
    }

    // Checks the PE timestamp of the game image Azimuth maps at imageBase.
    protected static bool CheckPeTimestamp(GameProcess g, ulong imageBase, uint expected, out string why)
    {
        if (!g.TryU32(imageBase + 0x3C, out uint lfanew) || !g.TryU32(imageBase + lfanew + 8, out uint stamp))
        {
            why = $"The game isn't loaded yet (nothing mapped at 0x{imageBase:X}).";
            return false;
        }
        if (stamp != expected)
        {
            why = $"A different game or build is running (PE timestamp {stamp:X8}, expected {expected:X8}). Check the active profile.";
            return false;
        }
        why = "";
        return true;
    }
}

// One driving camera view (Camera::CCam* classes), found by vtable. Its
// vertical FOV is either in the object (Fov, radians) or in the car's view
// data it points to (FovData -> +FovDataField, degrees); in-car views also
// have a seat offset from the steering wheel (x right, y up, z forward,
// metres). The game sets these when the view starts and reads them every
// frame. Priority picks the view in use when several update at once
// (lower wins).
public sealed record CameraView(string Id, string Name, ulong[] Vtables, int Priority,
    int? Fov = null, int? Seat = null, int? FovData = null, int FovDataField = 0);

public static class GameCatalog
{
    public static readonly IReadOnlyList<GameDefinition> All = new GameDefinition[] { Fh2.Fh2Game.Instance, Fh3.Fh3Game.Instance };

    public static GameDefinition Find(string? id) => All.FirstOrDefault(g => g.Id == id) ?? All[0];

    // Which supported game azimuth.exe is running (each build has its own PE
    // timestamp at the image base), or null with the reason.
    public static GameDefinition? Detect(GameProcess g, out string why, out bool loaded)
    {
        loaded = false;
        string first = "";
        foreach (var game in All)
        {
            if (game.CheckFingerprint(g, out why)) { loaded = true; return game; }
            if (first.Length == 0) first = why;
            if (!why.StartsWith("The game isn't loaded", StringComparison.Ordinal)) loaded = true;
        }
        why = loaded
            ? "Azimuth is running a game or build this app doesn't support (it supports Forza Horizon 2 1.0.0.64 and the Xbox One Forza Horizon 3 1.0.125.0)."
            : "Waiting for a game to start in Azimuth.";
        return null;
    }
}
