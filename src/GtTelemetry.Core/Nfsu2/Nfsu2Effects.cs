using System.Buffers.Binary;
using GtTelemetry.Core.Browser;
using GtTelemetry.Core.Memory;

namespace GtTelemetry.Core.Nfsu2;

/// <summary>The kinds of DirectInput effect, numbered as the runtime numbers them (the GUID's first byte).</summary>
public enum Nfsu2EffectKind
{
    Constant = 0x20, Ramp, Square, Sine, Triangle, SawtoothUp, SawtoothDown, Spring, Damper, Inertia, Friction, Custom,
}

/// <summary>One of the game's DirectInput effects, as the browser runtime keeps it (g_eff in runtime/dinput_pad.c).</summary>
public readonly record struct Nfsu2Effect(
    int Slot, uint Object, Nfsu2EffectKind Kind,
    uint DurationUs, uint Gain, uint DelayUs,
    int Magnitude, int Offset, uint PeriodUs,
    bool Envelope, uint AttackLevel, uint AttackUs, uint FadeLevel, uint FadeUs,
    bool Playing, uint Iterations, double Started, bool Unloaded)
{
    public const uint Infinite = 0xFFFFFFFF;

    public bool IsPeriodic => Kind is >= Nfsu2EffectKind.Square and <= Nfsu2EffectKind.SawtoothDown;
    public bool IsCondition => Kind is >= Nfsu2EffectKind.Spring and <= Nfsu2EffectKind.Friction;

    public string Name => Kind switch
    {
        Nfsu2EffectKind.SawtoothUp => "Sawtooth up",
        Nfsu2EffectKind.SawtoothDown => "Sawtooth down",
        _ => Kind.ToString(),
    };
}

/// <summary>A spring's or damper's parameters for the wheel, from the game's force feedback manager.</summary>
public readonly record struct Nfsu2Condition(int Offset, int Coefficient, int Saturation);

/// <summary>What the game's force feedback is doing this tick.</summary>
public sealed record Nfsu2Ffb(
    bool Running,
    IReadOnlyList<Nfsu2Effect> Effects,
    Nfsu2Condition Spring,
    int Damper,
    int ConstantForce)
{
    public static readonly Nfsu2Ffb Stopped = new(false, [], default, 0, 0);
}

/// <summary>
/// Reads the game's DirectInput effects on nfsu2.xyz: the runtime's effect table (found once, by its signature), and
/// the wheel's spring, damper and constant force from the game's force feedback manager, which the table doesn't keep.
/// One round trip a tick. Read-only.
/// </summary>
public sealed class Nfsu2Effects(BrowserMemorySource memory)
{
    // The runtime's effect table: 64 entries of 80 bytes, in the runtime's own data above the guest's memory.
    public const int Count = 64;
    private const int Stride = 80;
    private const uint ScanFrom = 0x28000000;
    private const int ScanSize = 0x04000000;
    private static readonly TimeSpan ScanAgain = TimeSpan.FromSeconds(3);

    // The game's force feedback manager and its last values per device slot (0 the wheel): docs/nfsu2/FINDINGS.md.
    private const uint FfManager = 0x00870760;
    private const int CacheFrom = 0x2BA0;
    private const int CacheSpring = 0x2BA0 - CacheFrom;      // s32 ×3: offset, coefficient, saturation
    private const int CacheConstant = 0x2BB8 - CacheFrom;    // s32: ±10000
    private const int CacheDamper = 0x2BC8 - CacheFrom;      // s32: coefficient
    private const int CacheSize = 0x2BCC - CacheFrom;

    private const uint WorldStep = 0x00865154;               // f32: 0 while the world stands
    private const uint GameFlow = 0x008654A4;                // u32: 6 in the world
    private const uint GuestFrom = 0x00010000, GuestTo = 0x28000000;

    private uint table, manager;
    private DateTime lastScan = DateTime.MinValue;

    /// <summary>Where the effect table is, 0 until it's found.</summary>
    public uint Table => table;

    public Nfsu2Ffb Read()
    {
        if (!memory.IsConnected)
        {
            table = manager = 0;
            return Nfsu2Ffb.Stopped;
        }
        try { return ReadState(); }
        catch (MemorySourceException)
        {
            table = manager = 0;
            return Nfsu2Ffb.Stopped;
        }
    }

    private Nfsu2Ffb ReadState()
    {
        if (table == 0)
        {
            if (DateTime.UtcNow - lastScan < ScanAgain) return Nfsu2Ffb.Stopped;
            lastScan = DateTime.UtcNow;
            table = Find();
            if (table == 0) return Nfsu2Ffb.Stopped;
        }

        List<(uint, int)> ranges = [(WorldStep, 4), (GameFlow, 4), (FfManager, 4), (table, Count * Stride)];
        bool cached = manager != 0;
        if (cached) ranges.Add((manager + CacheFrom, CacheSize));
        var r = memory.ReadMany(ranges);

        float dt = BinaryPrimitives.ReadSingleLittleEndian(r[0]);
        uint flow = U32(r[1], 0), m = U32(r[2], 0);
        manager = m is >= GuestFrom and < GuestTo ? m : 0;

        var effects = Decode(r[3]);
        if (effects is null)
        {
            table = 0;
            return Nfsu2Ffb.Stopped;
        }

        Nfsu2Condition spring = default;
        int damper = 0, constant = 0;
        if (cached && manager == m)
        {
            var c = r[4];
            spring = new(S32(c, CacheSpring), S32(c, CacheSpring + 4), S32(c, CacheSpring + 8));
            constant = S32(c, CacheConstant);
            damper = S32(c, CacheDamper);
        }
        return new(flow == 6 && dt > 0 && dt < 0.25f, effects, spring, damper, constant);
    }

    /// <summary>The table's entries in use, or null if what's there isn't the table any more.</summary>
    private static List<Nfsu2Effect>? Decode(byte[] b)
    {
        var list = new List<Nfsu2Effect>();
        for (int i = 0; i < Count; i++)
        {
            int o = i * Stride;
            uint obj = U32(b, o);
            if (obj == 0) continue;
            int kind = S32(b, o + 4);
            if (obj is < GuestFrom or >= GuestTo || kind is < 0x20 or > 0x2B) return null;
            list.Add(new(i, obj, (Nfsu2EffectKind)kind,
                U32(b, o + 8), U32(b, o + 12), U32(b, o + 16),
                S32(b, o + 20), S32(b, o + 24), U32(b, o + 28),
                S32(b, o + 32) != 0, U32(b, o + 36), U32(b, o + 40), U32(b, o + 44), U32(b, o + 48),
                S32(b, o + 52) != 0, U32(b, o + 56), BinaryPrimitives.ReadDoubleLittleEndian(b.AsSpan(o + 64)),
                S32(b, o + 72) != 0));
        }
        return list;
    }

    /// <summary>
    /// Finds the table by its entries: { obj, kind 0x20–0x2B, … } where the game's effect object holds the entry's
    /// slot + 1 at obj+4 (the runtime's CreateEffect). Most entries must agree on where the table starts.
    /// </summary>
    private uint Find()
    {
        var votes = new Dictionary<uint, int>();
        var buf = new byte[BrowserMemorySource.Chunk];
        var candidates = new List<(uint Entry, uint Obj)>();
        for (uint a = ScanFrom; a < ScanFrom + ScanSize; a += (uint)buf.Length)
        {
            try { memory.Read(a, buf); }
            catch (MemorySourceException) { break; }
            for (int o = 0; o + 8 <= buf.Length; o += 4)
            {
                uint obj = U32(buf, o);
                if (obj is < GuestFrom or >= GuestTo) continue;
                uint kind = U32(buf, o + 4);
                if (kind is >= 0x20 and <= 0x2B) candidates.Add((a + (uint)o, obj));
            }
        }
        if (candidates.Count == 0) return 0;

        // Check each candidate's object, a batch a round trip.
        for (int i = 0; i < candidates.Count && i < 20000; i += 256)
        {
            var batch = candidates.Skip(i).Take(256).ToList();
            var r = memory.ReadMany(batch.Select(c => (c.Obj + 4, 4)).ToList());
            for (int k = 0; k < batch.Count; k++)
            {
                uint slot = U32(r[k], 0);
                if (slot is < 1 or > Count) continue;
                uint start = batch[k].Entry - (slot - 1) * Stride;
                votes[start] = votes.GetValueOrDefault(start) + 1;
            }
        }
        if (votes.Count == 0) return 0;
        var best = votes.MaxBy(v => v.Value);
        return best.Key;
    }

    private static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
    private static int S32(byte[] b, int o) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(o));
}
