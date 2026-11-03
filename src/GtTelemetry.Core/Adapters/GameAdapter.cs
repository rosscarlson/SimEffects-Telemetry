namespace GtTelemetry.Core.Adapters;

public enum MemorySourceKind { Pcsx2Pine, Rpcs3Pine, NativeProcess, HelperApp }

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

    /// <summary>Disc serials / title IDs this adapter recognizes (as reported by PINE).</summary>
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
        MemorySourceKind.NativeProcess => "Native process",
        MemorySourceKind.HelperApp => "Helper app",
        _ => k.ToString(),
    };
}

/// <summary>Every game the app knows about. Only GT4 Spec II is wired up in this milestone; the rest are declared so the UI and
/// architecture account for them from day one.</summary>
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

    public static readonly IReadOnlyList<GameAdapter> All =
    [
        Gt4SpecII,
        new()
        {
            Id = "gt5", Name = "Gran Turismo 5", Platform = "PlayStation 3", Source = MemorySourceKind.Rpcs3Pine,
            GameIds = ["BCUS98114", "BCES00569"],
            Features = [new(Feature.MemoryAccess, Support.Planned, "RPCS3 PINE — milestone 6"), new(Feature.Telemetry, Support.Planned, "Milestone 6"),
                        new(Feature.CameraControl, Support.Planned, "Milestone 6"), new(Feature.DisplayPatches, Support.Planned, "RPCS3 patches"),
                        new(Feature.PerCarProfiles, Support.Planned, "")],
        },
        new()
        {
            Id = "gt6", Name = "Gran Turismo 6", Platform = "PlayStation 3", Source = MemorySourceKind.Rpcs3Pine,
            GameIds = ["BCUS98394", "BCES01893"],
            Features = [new(Feature.MemoryAccess, Support.Planned, "RPCS3 PINE — milestone 6"), new(Feature.Telemetry, Support.Planned, "Milestone 6"),
                        new(Feature.CameraControl, Support.Planned, "Milestone 6"), new(Feature.DisplayPatches, Support.Planned, "RPCS3 patches"),
                        new(Feature.PerCarProfiles, Support.Planned, "")],
        },
        new()
        {
            Id = "fh1-3", Name = "Forza Horizon 1–3", Platform = "Pinyon Shift recomp", Source = MemorySourceKind.NativeProcess,
            Features = [new(Feature.MemoryAccess, Support.Planned, "Absorb existing work — milestone 6"), new(Feature.Telemetry, Support.Planned, "Existing telemetry"),
                        new(Feature.CameraControl, Support.Unavailable, ""), new(Feature.DisplayPatches, Support.Unavailable, "Handled by the recomp"),
                        new(Feature.PerCarProfiles, Support.Unavailable, "")],
        },
        new()
        {
            Id = "fm4", Name = "Forza Motorsport 4", Platform = "Xbox 360 · Azimuth helper", Source = MemorySourceKind.HelperApp,
            Features = [new(Feature.MemoryAccess, Support.Planned, "Absorb existing helper app — milestone 6"), new(Feature.Telemetry, Support.Planned, "Existing telemetry"),
                        new(Feature.CameraControl, Support.Unavailable, ""), new(Feature.DisplayPatches, Support.Unavailable, ""),
                        new(Feature.PerCarProfiles, Support.Unavailable, "")],
        },
    ];

    public static GameAdapter? ForGameId(string? id) => All.FirstOrDefault(a => a.Matches(id));
}
