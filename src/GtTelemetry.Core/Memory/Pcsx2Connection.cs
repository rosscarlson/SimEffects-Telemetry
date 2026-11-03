namespace GtTelemetry.Core.Memory;

public sealed record GameInfo(string? Title, string? Id, string? Uuid, string? GameVersion, string? EmulatorVersion, EmulatorStatus Status);

/// <summary>
/// A PCSX2 connection: PINE is the primary channel (info, small reads, writes); the process-memory reader
/// is an optional accelerator for full-RAM snapshots. Either one alone is enough to scan.
/// </summary>
public sealed class Pcsx2Connection : IMemorySource, ISnapshotSource
{
    public const uint RamSize = Pcsx2ProcessMemory.EeRamSize;

    public Pcsx2Connection(int pineSlot = PineClient.DefaultSlot, bool fastSnapshots = true)
    {
        Pine = new PineClient(pineSlot);
        FastSnapshots = fastSnapshots;
    }

    public PineClient Pine { get; }

    public Pcsx2ProcessMemory Process { get; } = new();

    public bool FastSnapshots { get; }

    public string Name => "PCSX2";

    public bool IsConnected => Pine.IsConnected || Process.IsConnected;

    /// <summary>Error from the last PINE connect attempt, if it failed.</summary>
    public string? PineError { get; private set; }

    /// <summary>Error from the last process attach attempt, if it failed.</summary>
    public string? ProcessError { get; private set; }

    public string SnapshotPath => Process.IsConnected ? "process memory" : Pine.IsConnected ? "PINE" : "none";

    public void Connect()
    {
        PineError = ProcessError = null;
        try { Pine.Connect(); }
        catch (MemorySourceException ex) { PineError = ex.Message; }

        if (FastSnapshots)
        {
            try { Process.Connect(); }
            catch (MemorySourceException ex) { ProcessError = ex.Message; }
        }

        if (!IsConnected) throw new MemorySourceException(PineError ?? ProcessError ?? "Couldn't connect to PCSX2.");
    }

    public void Disconnect()
    {
        Pine.Disconnect();
        Process.Disconnect();
    }

    public void Dispose() => Disconnect();

    public GameInfo? GetGameInfo()
    {
        if (!Pine.IsConnected) return null;
        string? Try(Func<string> f) { try { return f(); } catch (MemorySourceException) when (Pine.IsConnected) { return null; } }
        EmulatorStatus status;
        try { status = Pine.GetStatus(); } catch (MemorySourceException) when (Pine.IsConnected) { status = EmulatorStatus.Unknown; }
        return new GameInfo(Try(Pine.GetTitle), Try(Pine.GetGameId), Try(Pine.GetGameUuid), Try(Pine.GetGameVersion), Try(Pine.GetVersion), status);
    }

    /// <summary>Small reads prefer PINE (the supported channel); falls back to process memory.</summary>
    public void Read(uint address, Span<byte> dest)
    {
        if (Pine.IsConnected && dest.Length <= 4096) Pine.Read(address, dest);
        else if (Process.IsConnected) Process.Read(address, dest);
        else if (Pine.IsConnected) Pine.Read(address, dest);
        else throw new MemorySourceException("Not connected to PCSX2.");
    }

    /// <summary>Bulk read for snapshots: process memory when available, else batched PINE.</summary>
    public void ReadBulk(uint address, Span<byte> dest)
    {
        if (Process.IsConnected) Process.Read(address, dest);
        else if (Pine.IsConnected) Pine.Read(address, dest);
        else throw new MemorySourceException("Not connected to PCSX2.");
    }

    /// <summary>
    /// Reads many typed values at once — one PINE round trip when PINE is up, else direct process reads.
    /// This is the shape telemetry polling uses: a fixed list of fields read every frame.
    /// </summary>
    public double[] ReadValues(IReadOnlyList<(uint Address, Scanning.ValueKind Kind)> items)
    {
        var values = new double[items.Count];
        if (items.Count == 0) return values;
        if (Pine.IsConnected)
        {
            var reqs = new (uint, int)[items.Count];
            int total = 0;
            for (int i = 0; i < items.Count; i++) { reqs[i] = (items[i].Address, Scanning.ValueKinds.Size(items[i].Kind)); total += reqs[i].Item2; }
            var buf = new byte[total];
            Pine.ReadMany(reqs, buf);
            for (int i = 0, off = 0; i < items.Count; off += reqs[i].Item2, i++)
                values[i] = Scanning.ValueKinds.Read(items[i].Kind, buf.AsSpan(off));
        }
        else
        {
            Span<byte> b = stackalloc byte[4];
            for (int i = 0; i < items.Count; i++)
            {
                var span = b[..Scanning.ValueKinds.Size(items[i].Kind)];
                ReadBulk(items[i].Address, span);
                values[i] = Scanning.ValueKinds.Read(items[i].Kind, span);
            }
        }
        return values;
    }

    public void Write(uint address, ReadOnlySpan<byte> data)
    {
        if (Pine.IsConnected) Pine.Write(address, data);
        else if (Process.IsConnected) Process.Write(address, data);
        else throw new MemorySourceException("Not connected to PCSX2.");
    }
}
