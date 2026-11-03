using System.Runtime.InteropServices;
using GtTelemetry.Core.Azimuth.Games;

namespace GtTelemetry.Core.Azimuth;

// Finds every car-physics object in the game's heap by its vtable pair.
public static class CarScanner
{
    const int Chunk = 8 << 20;
    const int Piece = 64 << 10;

    public static List<ulong> Scan(GameProcess g, GameDefinition game, CancellationToken ct = default)
    {
        var found = new List<ulong>();
        if (game.VtablePairs.Count == 0) return found;
        var buf = new byte[Chunk + 8];
        // The cars are on the game's private heap. Mapped memory is several
        // GB more in FH3 (about 30 s to read), so it's only searched when
        // the heap has none.
        Scan(g, game, buf, g.WritableRegions(mapped: false), found, ct);
        if (found.Count == 0) Scan(g, game, buf, g.WritableRegions(mapped: true), found, ct);
        return found;
    }

    // Every object whose first qword is one of these vtables (the heap only).
    public static List<(ulong Address, ulong Vtable)> FindObjects(GameProcess g, IReadOnlyCollection<ulong> vtables, CancellationToken ct = default)
    {
        var found = new List<(ulong, ulong)>();
        var buf = new byte[Chunk];
        foreach (var (rbase, rsize) in g.WritableRegions(mapped: false))
            for (ulong off = 0; off < rsize; off += Chunk)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min((ulong)Chunk, rsize - off);
                if (!g.Read(rbase + off, buf.AsSpan(0, n))) continue;
                var q = MemoryMarshal.Cast<byte, ulong>(buf.AsSpan(0, n));
                foreach (ulong vt in vtables)
                    for (int start = 0; start < q.Length;)
                    {
                        int i = q[start..].IndexOf(vt);
                        if (i < 0) break;
                        found.Add((rbase + off + (ulong)(start + i) * 8, vt));
                        start += i + 1;
                    }
            }
        return found;
    }

    static void Scan(GameProcess g, GameDefinition game, byte[] buf, IEnumerable<(ulong, ulong)> regions, List<ulong> found, CancellationToken ct)
    {
        foreach (var (rbase, rsize) in regions)
        {
            for (ulong off = 0; off < rsize; off += Chunk)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min((ulong)Chunk, rsize - off);
                var span = buf.AsSpan(0, n);
                if (g.Read(rbase + off, span))
                {
                    Search(g, game, rbase + off, span, found);
                    continue;
                }
                // Part of the chunk changed under us (the game freed or
                // re-protected pages); retry it in small pieces.
                for (int p = 0; p < n; p += Piece)
                {
                    var piece = buf.AsSpan(p, Math.Min(Piece, n - p));
                    if (g.Read(rbase + off + (ulong)p, piece)) Search(g, game, rbase + off + (ulong)p, piece, found);
                }
            }
        }
    }

    static void Search(GameProcess g, GameDefinition game, ulong at, ReadOnlySpan<byte> span, List<ulong> found)
    {
        var q = MemoryMarshal.Cast<byte, ulong>(span);
        foreach (var (primary, sub) in game.VtablePairs)
        {
            for (int start = 0; start < q.Length;)
            {
                int i = q[start..].IndexOf(primary);  // vectorised
                if (i < 0) break;
                ulong addr = at + (ulong)(start + i) * 8;
                if (g.TryU64(addr + (ulong)game.SubObjectOffset, out ulong v) && v == sub && !found.Contains(addr))
                    found.Add(addr);
                start += i + 1;
            }
        }
    }
}
