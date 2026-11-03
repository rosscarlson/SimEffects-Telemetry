using System.Buffers.Binary;
using GtTelemetry.Core.Memory;

namespace GtTelemetry.Core.Camera;

/// <summary>A seat position relative to the game's own, in metres: positive is right, up and forward.</summary>
public readonly record struct SeatOffset(float Right, float Up, float Forward)
{
    public static readonly SeatOffset Zero = default;

    public bool IsZero => Right == 0 && Up == 0 && Forward == 0;
}

/// <summary>
/// One of the car's camera views whose seat can be moved. <see cref="Key"/> is the game's own seat position for the
/// view, which differs between cars, so it identifies the car and view for profiles (no car ID is mapped yet).
/// </summary>
public sealed class CameraView
{
    internal CameraView(string key, string name, float[] defaults, uint[] copies)
    {
        Key = key;
        Name = name;
        Defaults = defaults;
        Copies = copies;
    }

    public string Key { get; }

    /// <summary>"Cockpit" or "Hood".</summary>
    public string Name { get; }

    /// <summary>The game's seat position for this view: x sideways (positive is left), y vertical (negative is up),
    /// z fore/aft (positive is forward), metres from the car's origin.</summary>
    public float[] Defaults { get; }

    /// <summary>The car's camera table holds the view three times; all are written.</summary>
    internal uint[] Copies { get; }

    public SeatOffset Offset { get; internal set; }

    /// <summary>The seat last written to the table.</summary>
    internal byte[] Applied { get; set; } = [];

    /// <summary>The seat the camera was last seen using.</summary>
    internal byte[] Shown { get; set; } = [];
}

/// <summary>What the camera is doing right now.</summary>
public sealed record CameraState(CameraView? View, float? FovMultiplier, float? VerticalFovDegrees);

/// <summary>
/// Live FOV and seat position for GT4 Spec II (docs/gt4-spec2/memory-map.md, "Camera"). The FOV is a multiplier held
/// in an instruction that runs every frame, so it applies to every view at once. The seat comes from a per-car camera
/// table that moves between races and cars; it's found by searching the race heap for the seat the camera is using.
/// Not thread-safe: call from one thread.
/// </summary>
public sealed class Gt4SpecIICamera(IMemorySource memory, ISnapshotSource bulk)
{
    // `lui at, imm` whose imm is the upper half of the FOV multiplier (0x3F80 = 1.0). Blitznoz's Spec II FOV cheat
    // patches the same instruction.
    public const uint FovInstruction = 0x0023E814;
    private const uint LuiAt = 0x3C01;
    public const ushort FovDefault = 0x3F80;

    // The camera object, rebuilt every frame from the current view.
    private const uint CameraObject = 0x00C670A4;
    private const int CameraObjectSize = 0x48;
    private const int CameraSeat = 0x3C;           // f32 ×3, the current view's seat
    private const uint ProjectionSy = 0x0073B2C4;  // f32, vertical projection scale

    private const uint CarPointer = 0x007342B8;
    private const uint HeapStart = 0x01000000, HeapEnd = 0x02000000;
    private const int SearchEveryMs = 3000;

    private readonly List<CameraView> views = [];
    private readonly Dictionary<string, long> notFound = [];
    private readonly byte[] cameraObject = new byte[CameraObjectSize];
    private byte[]? heap;
    private uint car;

    /// <summary>Raised when a view is found for the first time this race, before it's reported as current. Its offset
    /// is zero (the game's own seat).</summary>
    public event Action<CameraView>? ViewFound;

    public CameraView? Current { get; private set; }

    /// <summary>Works out the current view. Cheap when nothing has changed; searches the race heap (about 16 MB) when a
    /// new view appears, at most every few seconds per unknown seat.</summary>
    public CameraState Poll()
    {
        Current = FindCurrent();
        return new CameraState(Current, ReadFov() is ushort imm ? ToMultiplier(imm) : null, ReadVerticalFov());
    }

    private CameraView? FindCurrent()
    {
        Span<byte> p = stackalloc byte[4];
        memory.Read(CarPointer, p);
        uint c = BinaryPrimitives.ReadUInt32LittleEndian(p);
        if (c != car)
        {
            // New car or race: the tables found so far belong to the old car.
            car = c;
            views.Clear();
            notFound.Clear();
        }

        memory.Read(CameraObject, cameraObject);
        if (BinaryPrimitives.ReadSingleLittleEndian(cameraObject) != 1f || BinaryPrimitives.ReadSingleLittleEndian(cameraObject.AsSpan(4)) != 100f)
            return null;
        var seat = cameraObject.AsSpan(CameraSeat, 12);

        var check = new byte[12];
        // A table that no longer holds what was written has been replaced (race restart).
        views.RemoveAll(v => { memory.Read(v.Copies[0], check); return !check.AsSpan().SequenceEqual(v.Applied); });
        foreach (var v in views)
        {
            if (seat.SequenceEqual(v.Applied))
            {
                v.Shown = v.Applied;
                return v;
            }
        }
        // Just moved, or moved while the game is paused: the camera still shows the old seat until the next frame runs.
        if (Current != null && views.Contains(Current) && seat.SequenceEqual(Current.Shown)) return Current;

        string seatKey = Convert.ToHexString(seat);
        long now = Environment.TickCount64;
        if (notFound.TryGetValue(seatKey, out long when) && now - when < SearchEveryMs) return null;

        var found = Search(seat);
        if (found.Count == 0)
        {
            notFound[seatKey] = now;
            return null;
        }
        notFound.Remove(seatKey);
        var defaults = new[] { F(seat, 0), F(seat, 4), F(seat, 8) };
        var view = new CameraView(KeyOf(defaults), NameOf(found[0], defaults), defaults, [.. found]) { Applied = seat.ToArray(), Shown = seat.ToArray() };
        views.Add(view);
        ViewFound?.Invoke(view);
        return view;
    }

    private List<uint> Search(ReadOnlySpan<byte> seat)
    {
        heap ??= new byte[HeapEnd - HeapStart];
        bulk.ReadBulk(HeapStart, heap);
        var hits = new List<uint>();
        for (int start = 0; ;)
        {
            int j = heap.AsSpan(start).IndexOf(seat);
            if (j < 0) break;
            int i = start + j;
            if (i % 4 == 0) hits.Add(HeapStart + (uint)i);
            start = i + 1;
        }
        return hits;
    }

    /// <summary>Cockpit entries are type 3 in the table, with the eye off to the driver's side.</summary>
    private string NameOf(uint copy, float[] d)
    {
        Span<byte> t = stackalloc byte[4];
        memory.Read(copy - 4, t);
        return BinaryPrimitives.ReadUInt32LittleEndian(t) == 3 && Math.Abs(d[0]) > 0.05f ? "Cockpit" : "Hood";
    }

    private static string KeyOf(float[] d) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{d[0]:0.0000},{d[1]:0.0000},{d[2]:0.0000}");

    /// <summary>Moves the seat of <paramref name="view"/> (normally <see cref="Current"/>) to its default plus
    /// <paramref name="offset"/>.</summary>
    public void SetSeat(CameraView view, SeatOffset offset)
    {
        var d = view.Defaults;
        var bytes = new byte[12];
        BinaryPrimitives.WriteSingleLittleEndian(bytes, d[0] - offset.Right);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(4), d[1] - offset.Up);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(8), d[2] + offset.Forward);
        foreach (var copy in view.Copies) memory.Write(copy, bytes);
        view.Applied = bytes;
        view.Offset = offset;
    }

    /// <summary>The FOV instruction's immediate, or null if the code there isn't the expected instruction (another
    /// game version, or the game isn't running).</summary>
    public ushort? ReadFov()
    {
        Span<byte> w = stackalloc byte[4];
        memory.Read(FovInstruction, w);
        uint word = BinaryPrimitives.ReadUInt32LittleEndian(w);
        return word >> 16 == LuiAt ? (ushort)word : null;
    }

    /// <summary>Sets the FOV multiplier's immediate (see <see cref="FromMultiplier"/>). False if the instruction isn't
    /// there.</summary>
    public bool SetFov(ushort imm)
    {
        if (ReadFov() == null) return false;
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, imm);
        memory.Write(FovInstruction, b);   // the immediate is the instruction's low half
        return true;
    }

    /// <summary>Vertical FOV as rendered, from the projection matrix.</summary>
    public float? ReadVerticalFov()
    {
        Span<byte> b = stackalloc byte[4];
        memory.Read(ProjectionSy, b);
        float sy = BinaryPrimitives.ReadSingleLittleEndian(b);
        return sy is > 0.05f and < 50f ? 2 * MathF.Atan(1 / sy) * 180 / MathF.PI : null;
    }

    /// <summary>The multiplier an immediate stands for. Above 1 zooms in; below 1 widens.</summary>
    public static float ToMultiplier(ushort imm) => BitConverter.Int32BitsToSingle(imm << 16);

    /// <summary>The nearest immediate to a multiplier (steps of about 0.4–0.8%).</summary>
    public static ushort FromMultiplier(float m) =>
        (ushort)((BitConverter.SingleToInt32Bits(Math.Clamp(m, 0.25f, 4f)) + 0x8000) >> 16);

    private static float F(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadSingleLittleEndian(b[o..]);
}
