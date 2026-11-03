using System.Text.Json;
using System.Text.Json.Serialization;
using GtTelemetry.Core.Scanning;

namespace GtTelemetry.Core.Sessions;

/// <summary>What a candidate value must do while a step is being sampled.</summary>
public enum StepExpect
{
    /// <summary>No constraint inside the step (it may still carry a label for cross-step comparison).</summary>
    Any,
    /// <summary>Holds (within tolerance) for the whole sampling window.</summary>
    Stable,
    /// <summary>Never drops by more than the tolerance between samples, and ends clearly higher than it started.</summary>
    Rising,
    /// <summary>Never rises by more than the tolerance between samples, and ends clearly lower than it started.</summary>
    Falling,
    /// <summary>Ends different from where it started.</summary>
    Changing,
}

/// <summary>
/// One spoken instruction plus a sampling window. Steps with a <see cref="Label"/> are compared with each other at the end
/// of each step: candidates must order the same way the labels do (all rising with the label, or all falling), and
/// steps with equal labels must agree with each other.
/// </summary>
public sealed class SessionStep
{
    /// <summary>What to tell the driver.</summary>
    public string Say { get; set; } = "";

    /// <summary>Seconds to wait after speaking before sampling starts (reaction + settling time).</summary>
    public double Settle { get; set; } = 1.5;

    /// <summary>Seconds to sample for.</summary>
    public double Sample { get; set; } = 2.0;

    public double? Label { get; set; }

    public StepExpect Expect { get; set; } = StepExpect.Stable;

    /// <summary>Relative tolerance for noise inside the step (0.02 = 2 % of the value).</summary>
    public double Tolerance { get; set; } = 0.02;

    /// <summary>Absolute noise allowance on top of <see cref="Tolerance"/> (useful near zero, e.g. a centred wheel).</summary>
    public double ToleranceAbs { get; set; }
}

public sealed class SessionScript
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string Description { get; set; } = "";

    /// <summary>What Ross needs to set up before starting (shown on screen and spoken first).</summary>
    public string Setup { get; set; } = "";

    public string Intro { get; set; } = "";

    public string Outro { get; set; } = "Done, thanks. You can relax.";

    /// <summary>Value types to search; defaults to u8, u16, u32, f32.</summary>
    public List<string>? Types { get; set; }

    /// <summary>Relative tolerance when two steps with the same label are compared.</summary>
    public double RepeatTolerance { get; set; } = 0.05;

    public List<SessionStep> Steps { get; set; } = [];

    [JsonIgnore]
    public string? SourcePath { get; set; }

    public ValueKind[] Kinds() => Types is { Count: > 0 } ? Types.Select(ValueKinds.Parse).Distinct().ToArray() : ValueKinds.Default;

    public TimeSpan EstimatedDuration() =>
        TimeSpan.FromSeconds(Steps.Sum(s => s.Settle + s.Sample + 2.5) + 6);

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static SessionScript Load(string path)
    {
        var s = JsonSerializer.Deserialize<SessionScript>(File.ReadAllText(path), Json)
                ?? throw new InvalidDataException($"{path} is empty.");
        if (string.IsNullOrEmpty(s.Id)) s.Id = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(s.Name)) s.Name = s.Id;
        s.SourcePath = path;
        return s;
    }

    /// <summary>Loads every *.json script in <paramref name="dir"/>, sorted by file name; bad files are reported, not thrown.</summary>
    public static (List<SessionScript> Scripts, List<string> Errors) LoadAll(string dir)
    {
        var scripts = new List<SessionScript>();
        var errors = new List<string>();
        if (!Directory.Exists(dir)) return (scripts, errors);
        foreach (var f in Directory.GetFiles(dir, "*.json"))
        {
            try { scripts.Add(Load(f)); }
            catch (Exception ex) { errors.Add($"{Path.GetFileName(f)}: {ex.Message}"); }
        }
        return (scripts.OrderBy(s => Path.GetFileName(s.SourcePath)).ToList(), errors);
    }
}
