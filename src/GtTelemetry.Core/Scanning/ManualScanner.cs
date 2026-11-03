using GtTelemetry.Core.Memory;

namespace GtTelemetry.Core.Scanning;

public enum ScanCompare
{
    Unknown, Equal, NotEqual, Greater, Less, Between,
    Changed, Unchanged, Increased, Decreased, IncreasedBy, DecreasedBy,
}

public static class ScanCompares
{
    public static bool NeedsPrevious(this ScanCompare c) => c >= ScanCompare.Changed;

    public static int Operands(this ScanCompare c) => c switch
    {
        ScanCompare.Unknown or ScanCompare.Changed or ScanCompare.Unchanged or ScanCompare.Increased or ScanCompare.Decreased => 0,
        ScanCompare.Between => 2,
        _ => 1,
    };

    public static string Describe(this ScanCompare c) => c switch
    {
        ScanCompare.Unknown => "Unknown initial value",
        ScanCompare.Equal => "Equal to",
        ScanCompare.NotEqual => "Not equal to",
        ScanCompare.Greater => "Greater than",
        ScanCompare.Less => "Less than",
        ScanCompare.Between => "Between",
        ScanCompare.Changed => "Changed",
        ScanCompare.Unchanged => "Unchanged",
        ScanCompare.Increased => "Increased",
        ScanCompare.Decreased => "Decreased",
        ScanCompare.IncreasedBy => "Increased by",
        ScanCompare.DecreasedBy => "Decreased by",
        _ => c.ToString(),
    };
}

/// <summary>Classic first-scan / next-scan narrowing over one value type.</summary>
public sealed class ManualScanner
{
    private readonly Pcsx2Connection connection;
    private byte[] previous = [];
    private byte[] current = [];
    private BitSet? bits;

    public ManualScanner(Pcsx2Connection connection) => this.connection = connection;

    public ValueKind Kind { get; private set; } = ValueKind.F32;

    public uint Start { get; private set; }

    public long Count { get; private set; }

    public int Scans { get; private set; }

    public bool HasScan => bits != null;

    public void Reset()
    {
        bits = null;
        previous = current = [];
        Count = 0;
        Scans = 0;
    }

    public long FirstScan(ValueKind kind, ScanCompare op, double x, double y, double tol, uint start = 0, uint length = Pcsx2Connection.RamSize)
    {
        if (op.NeedsPrevious()) throw new InvalidOperationException("The first scan can't compare against a previous scan.");
        Kind = kind;
        Start = start & ~3u;
        current = new byte[length];
        previous = new byte[length];
        connection.ReadBulk(Start, current);
        bits = new BitSet((int)(length / (uint)kind.Size()), allSet: true);
        Count = Kernels.Filter(kind, bits, current, default, new CompareConst(op, x, y, tol));
        Scans = 1;
        return Count;
    }

    public long NextScan(ScanCompare op, double x, double y, double tol)
    {
        if (bits == null) throw new InvalidOperationException("Run a first scan before a next scan.");
        (previous, current) = (current, previous);
        connection.ReadBulk(Start, current);
        Count = op.NeedsPrevious()
            ? Kernels.Filter(Kind, bits, current, previous, new CompareRelative(op, x, tol))
            : Kernels.Filter(Kind, bits, current, default, new CompareConst(op, x, y, tol));
        Scans++;
        return Count;
    }

    /// <summary>First <paramref name="max"/> surviving addresses with their values at the last scan and the one before.</summary>
    public IReadOnlyList<(uint Address, double Value, double Previous)> Results(int max)
    {
        var list = new List<(uint, double, double)>();
        if (bits == null) return list;
        int size = Kind.Size();
        foreach (int i in bits.Indices())
        {
            int off = i * size;
            double prev = Scans > 1 ? Kind.Read(previous.AsSpan(off)) : double.NaN;
            list.Add((Start + (uint)off, Kind.Read(current.AsSpan(off)), prev));
            if (list.Count >= max) break;
        }
        return list;
    }

    /// <summary>
    /// Finds 32-bit words that point at or just before <paramref name="target"/> (within <paramref name="maxOffset"/>),
    /// i.e. candidate struct base pointers. Matches both plain and KSEG0/uncached-mirror forms.
    /// </summary>
    public static IReadOnlyList<(uint Address, uint Pointer, uint Offset)> FindPointers(ReadOnlySpan<byte> ram, uint ramBase, uint target, uint maxOffset, int max = 500)
    {
        var results = new List<(uint, uint, uint)>();
        uint t = target & 0x1FFFFFF;
        var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(ram);
        for (int i = 0; i < words.Length && results.Count < max; i++)
        {
            uint p = words[i];
            // EE RAM and its mirrors: 0x00/0x20/0x30/0x80/0xA0 xxxxxx (each 32 MB).
            uint seg = p >> 25;
            if (seg != 0 && seg != 0x10 && seg != 0x18 && seg != 0x40 && seg != 0x50) continue;
            uint phys = p & 0x1FFFFFF;
            if (phys <= t && t - phys <= maxOffset && phys != 0)
                results.Add((ramBase + (uint)i * 4, p, t - phys));
        }
        return results;
    }
}
