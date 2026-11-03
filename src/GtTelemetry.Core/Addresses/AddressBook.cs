using System.Text.Json;
using GtTelemetry.Core.Scanning;
using GtTelemetry.Core.Sessions;

namespace GtTelemetry.Core.Addresses;

public enum AddressStatus { Candidate, Confirmed, Rejected }

public sealed class AddressEntry
{
    public string Name { get; set; } = "";

    /// <summary>Hex string, e.g. "0x0099C3C8", so the JSON is readable and diffable.</summary>
    public string Address { get; set; } = "";

    public string Type { get; set; } = "f32";

    public AddressStatus Status { get; set; } = AddressStatus.Candidate;

    /// <summary>Scale/meaning, e.g. "0–1 normalized", "rpm", "m/s".</summary>
    public string? Units { get; set; }

    public string? Notes { get; set; }

    /// <summary>Which session or scan found it.</summary>
    public string? Source { get; set; }

    public DateTime Updated { get; set; } = DateTime.Now;

    public uint AddressValue => ValueKinds.ParseAddress(Address);

    public ValueKind Kind => ValueKinds.Parse(Type);
}

/// <summary>
/// Named addresses for one game (data/&lt;game&gt;/addresses.json). The repo copy is the source of truth; docs/&lt;game&gt;/memory-map.md
/// documents the confirmed entries in prose.
/// </summary>
public sealed class AddressBook
{
    public string Game { get; set; } = "";

    public List<AddressEntry> Entries { get; set; } = [];

    public static AddressBook Load(string path)
    {
        if (!File.Exists(path)) return new AddressBook();
        return JsonSerializer.Deserialize<AddressBook>(File.ReadAllText(path), SessionScript.Json) ?? new AddressBook();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, SessionScript.Json));
    }

    /// <summary>Adds or replaces the entry with the same name.</summary>
    public AddressEntry Upsert(string name, uint address, ValueKind kind, AddressStatus status, string? notes = null, string? source = null, string? units = null)
    {
        var e = Entries.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (e == null) Entries.Add(e = new AddressEntry { Name = name });
        e.Address = ValueKinds.FormatAddress(address);
        e.Type = kind.Label();
        e.Status = status;
        e.Notes = notes ?? e.Notes;
        e.Source = source ?? e.Source;
        e.Units = units ?? e.Units;
        e.Updated = DateTime.Now;
        return e;
    }
}
