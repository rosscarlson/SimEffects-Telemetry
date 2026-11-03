using System.Diagnostics;
using GtTelemetry.Core.Azimuth.Games;

namespace GtTelemetry.Core.Azimuth;

/// <summary>Whether azimuth.exe is running, and which supported game it runs (null with the reason if none).</summary>
public sealed record AzimuthStatus(bool Running, int Pid, GameDefinition? Game, string Why)
{
    public static readonly AzimuthStatus NotRunning = new(false, 0, null, "Azimuth isn't running.");

    /// <summary>A quick look: opens azimuth.exe read-only and checks the game's build stamp. Reads a few bytes.</summary>
    public static AzimuthStatus Check()
    {
        if (Process.GetProcessesByName("azimuth").Length == 0) return NotRunning;
        using var g = GameProcess.Attach();
        if (g == null) return new(true, 0, null, "Couldn't open azimuth.exe to read it.");
        var game = GameCatalog.Detect(g, out string why, out _);
        return new(true, g.Pid, game, why);
    }
}
