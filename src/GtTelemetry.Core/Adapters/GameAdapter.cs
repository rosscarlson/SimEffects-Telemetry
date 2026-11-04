namespace GtTelemetry.Core.Adapters;

public enum MemorySourceKind { Pcsx2Pine, Rpcs3Pine, Rpcs3Process, Azimuth }

public enum Feature { MemoryAccess, Telemetry, CameraControl, DisplayPatches, PerCarProfiles }

public enum Support
{
    /// <summary>Works today.</summary>
    Available,
    /// <summary>On the roadmap for this game; shown disabled with the milestone.</summary>
    Planned,
    /// <summary>Not possible or not planned for this game.</summary>
    Unavailable,
}

public sealed record FeatureSupport(Feature Feature, Support Support, string Note);

/// <summary>
/// Describes one game/platform combination and what the app can do with it. The core (telemetry model, UDP output,
/// hotkeys, profiles, UI) is game-agnostic; everything game-specific hangs off an adapter.
/// </summary>
public sealed class GameAdapter
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Platform { get; init; }

    public required MemorySourceKind Source { get; init; }

    /// <summary>Disc serials / title IDs this adapter recognizes (as reported by PINE or RPCS3), or the Azimuth game's id.</summary>
    public IReadOnlyList<string> GameIds { get; init; } = [];

    /// <summary>Folder under data/ holding the address map.</summary>
    public string? DataFolder { get; init; }

    public required IReadOnlyList<FeatureSupport> Features { get; init; }

    public Support SupportOf(Feature f) => Features.FirstOrDefault(x => x.Feature == f)?.Support ?? Support.Unavailable;

    public bool Matches(string? gameId) => gameId != null && GameIds.Any(g => g.Equals(gameId, StringComparison.OrdinalIgnoreCase));

    public override string ToString() => Name;

    public static string FeatureName(Feature f) => f switch
    {
        Feature.MemoryAccess => "Memory access & scanning",
        Feature.Telemetry => "Telemetry output (UDP)",
        Feature.CameraControl => "Live FOV & seat position",
        Feature.DisplayPatches => "Ultrawide & triple screens",
        Feature.PerCarProfiles => "Per-car profiles",
        _ => f.ToString(),
    };

    public static string SourceName(MemorySourceKind k) => k switch
    {
        MemorySourceKind.Pcsx2Pine => "PCSX2 · PINE",
        MemorySourceKind.Rpcs3Pine => "RPCS3 · PINE",
        MemorySourceKind.Rpcs3Process => "RPCS3 · process memory",
        MemorySourceKind.Azimuth => "Azimuth · process memory",
        _ => k.ToString(),
    };
}

/// <summary>Every game the app knows about.</summary>
public static class AdapterCatalog
{
    public static readonly GameAdapter Gt4SpecII = new()
    {
        Id = "gt4-spec2",
        Name = "Gran Turismo 4 Spec II",
        Platform = "PlayStation 2",
        Source = MemorySourceKind.Pcsx2Pine,
        GameIds = ["SCUS-97436"],
        DataFolder = "gt4-spec2",
        Features =
        [
            new(Feature.MemoryAccess, Support.Available, "PINE, plus fast snapshots from PCSX2 process memory"),
            new(Feature.Telemetry, Support.Available, "Forza Data Out (Horizon or Dash format) over UDP, e.g. to SimHub"),
            new(Feature.CameraControl, Support.Available, "FOV, and the seat in the cockpit and hood views, with hotkeys while PCSX2 has focus"),
            new(Feature.DisplayPatches, Support.Available, "16:9, 21:9, 32:9, 48:9 and 64:9 through PCSX2's per-game settings and cheat file"),
            new(Feature.PerCarProfiles, Support.Available, "Camera settings per car and view, recognised by the car's own camera position"),
        ],
    };

    public static readonly GameAdapter Gt5 = new()
    {
        Id = "gt5",
        Name = "Gran Turismo 5",
        Platform = "PlayStation 3",
        Source = MemorySourceKind.Rpcs3Process,
        GameIds = ["BCUS98114", "BCES00569"],
        DataFolder = "gt5",
        Features =
        [
            new(Feature.MemoryAccess, Support.Available, "Read straight from RPCS3's memory; nothing to turn on in RPCS3"),
            new(Feature.Telemetry, Support.Available, "Forza Data Out (Horizon or Dash format) over UDP, e.g. to SimHub"),
            new(Feature.CameraControl, Support.Planned, ""),
            new(Feature.DisplayPatches, Support.Available, "16:9, 21:9 and 32:9 through RPCS3's Aspect Ratio patch (2.11), with RPCS3 stretching to fill the screen"),
        ],
    };

    public static readonly GameAdapter Gt6 = new()
    {
        Id = "gt6",
        Name = "Gran Turismo 6",
        Platform = "PlayStation 3",
        Source = MemorySourceKind.Rpcs3Process,
        GameIds = ["BCUS98296", "BCUS98394", "BCES01893"],
        DataFolder = "gt6",
        Features =
        [
            new(Feature.MemoryAccess, Support.Available, "Read straight from RPCS3's memory; nothing to turn on in RPCS3"),
            new(Feature.Telemetry, Support.Available, "Forza Data Out (Horizon or Dash format) over UDP, e.g. to SimHub"),
            new(Feature.CameraControl, Support.Planned, "Next"),
            new(Feature.DisplayPatches, Support.Available, "16:9, 21:9 and 32:9, set while the game runs, with RPCS3 stretching to fill the screen"),
        ],
    };

    /// <summary>Forza Horizon 2 and 3 in the Azimuth emulator: one entry, since Azimuth Telemetry's engine reads both
    /// and tells them apart itself. The ids are its GameDefinition ids.</summary>
    public static readonly GameAdapter ForzaHorizon = new()
    {
        Id = "fh",
        Name = "Forza Horizon 2 & 3",
        Platform = "Xbox One",
        Source = MemorySourceKind.Azimuth,
        GameIds = ["fh2", "fh3"],
        Features =
        [
            new(Feature.MemoryAccess, Support.Available, "Read straight from azimuth.exe; the game is recognised when it runs"),
            new(Feature.Telemetry, Support.Available, "Forza Data Out (Horizon or Dash format) over UDP, e.g. to SimHub"),
            new(Feature.CameraControl, Support.Available, "FOV in every driving view, and the seat in the in-car views"),
            new(Feature.DisplayPatches, Support.Available, "Any render size, e.g. 10240 × 1440 for triple screens (Render Aspect Ratio)"),
        ],
    };

    public static readonly IReadOnlyList<GameAdapter> All = [Gt4SpecII, Gt5, Gt6, ForzaHorizon];

    /// <summary>The saved game, by id; "fh2" and "fh3" were separate entries before.</summary>
    public static GameAdapter? ForId(string? id) => id is "fh2" or "fh3" ? ForzaHorizon : All.FirstOrDefault(a => a.Id == id);

    public static GameAdapter? ForGameId(string? id) => All.FirstOrDefault(a => a.Matches(id));
}
