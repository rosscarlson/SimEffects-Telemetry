using System.Numerics;
using System.Runtime.InteropServices;

namespace GtTelemetry.Core.Scanning;

/// <summary>Keep-or-drop test for one candidate given its value now (<c>a</c>) and a reference value (<c>b</c>).</summary>
public interface IPairPredicate
{
    bool Keep(double a, double b);
}

/// <summary>
/// Tight filtering loops. Generic over the element type and the predicate struct so the JIT emits a specialised
/// loop per combination — a full 32 MB pass takes tens of milliseconds.
/// </summary>
public static class Kernels
{
    /// <summary>Clears every candidate whose (current, reference) pair fails <paramref name="pred"/>. Returns survivors.</summary>
    public static long Filter<TPred>(ValueKind kind, BitSet bits, ReadOnlySpan<byte> current, ReadOnlySpan<byte> reference, TPred pred)
        where TPred : struct, IPairPredicate => kind switch
        {
            ValueKind.U8 => Run<byte, TPred>(bits, current, reference, pred),
            ValueKind.S8 => Run<sbyte, TPred>(bits, current, reference, pred),
            ValueKind.U16 => Run<ushort, TPred>(bits, current, reference, pred),
            ValueKind.S16 => Run<short, TPred>(bits, current, reference, pred),
            ValueKind.U32 => Run<uint, TPred>(bits, current, reference, pred),
            ValueKind.S32 => Run<int, TPred>(bits, current, reference, pred),
            _ => Run<float, TPred>(bits, current, reference, pred),
        };

    private static long Run<T, TPred>(BitSet bits, ReadOnlySpan<byte> current, ReadOnlySpan<byte> reference, TPred pred)
        where T : unmanaged, INumberBase<T>
        where TPred : struct, IPairPredicate
    {
        var cur = MemoryMarshal.Cast<byte, T>(current);
        var refr = MemoryMarshal.Cast<byte, T>(reference.IsEmpty ? current : reference);
        var words = bits.Words;
        int n = Math.Min(cur.Length, bits.Length);
        long survivors = 0;

        for (int wi = 0; wi < words.Length; wi++)
        {
            ulong w = words[wi];
            if (w == 0) continue;
            ulong keep = w;
            while (w != 0)
            {
                int b = BitOperations.TrailingZeroCount(w);
                w &= w - 1;
                int i = (wi << 6) + b;
                bool ok = i < n;
                if (ok)
                {
                    double a = double.CreateTruncating(cur[i]);
                    double r = double.CreateTruncating(refr[i]);
                    if (typeof(T) == typeof(float) && !ValueKinds.IsSaneFloat(a)) ok = false;
                    else ok = pred.Keep(a, r);
                }
                if (!ok) keep &= ~(1UL << b);
            }
            words[wi] = keep;
            survivors += BitOperations.PopCount(keep);
        }
        return survivors;
    }

    /// <summary>
    /// Cross-phase ordering test. <paramref name="sign"/> is +1 if the current phase's label is above the reference
    /// phase's label, −1 if below. Candidates must differ, and the direction (value rises with label, or falls with it)
    /// must agree with every earlier comparison, tracked in <paramref name="pos"/>/<paramref name="neg"/>.
    /// </summary>
    public static long Direction(ValueKind kind, BitSet bits, BitSet pos, BitSet neg, ReadOnlySpan<byte> current, ReadOnlySpan<byte> reference, int sign) => kind switch
    {
        ValueKind.U8 => RunDirection<byte>(bits, pos, neg, current, reference, sign, kind.Epsilon()),
        ValueKind.S8 => RunDirection<sbyte>(bits, pos, neg, current, reference, sign, kind.Epsilon()),
        ValueKind.U16 => RunDirection<ushort>(bits, pos, neg, current, reference, sign, kind.Epsilon()),
        ValueKind.S16 => RunDirection<short>(bits, pos, neg, current, reference, sign, kind.Epsilon()),
        ValueKind.U32 => RunDirection<uint>(bits, pos, neg, current, reference, sign, kind.Epsilon()),
        ValueKind.S32 => RunDirection<int>(bits, pos, neg, current, reference, sign, kind.Epsilon()),
        _ => RunDirection<float>(bits, pos, neg, current, reference, sign, kind.Epsilon()),
    };

    private static long RunDirection<T>(BitSet bits, BitSet pos, BitSet neg, ReadOnlySpan<byte> current, ReadOnlySpan<byte> reference, int sign, double eps)
        where T : unmanaged, INumberBase<T>
    {
        var cur = MemoryMarshal.Cast<byte, T>(current);
        var refr = MemoryMarshal.Cast<byte, T>(reference);
        var words = bits.Words;
        int n = Math.Min(cur.Length, bits.Length);
        long survivors = 0;

        for (int wi = 0; wi < words.Length; wi++)
        {
            ulong w = words[wi];
            if (w == 0) continue;
            ulong keep = w, p = pos.Words[wi], m = neg.Words[wi];
            while (w != 0)
            {
                int b = BitOperations.TrailingZeroCount(w);
                w &= w - 1;
                int i = (wi << 6) + b;
                ulong bit = 1UL << b;
                if (i >= n) { keep &= ~bit; continue; }
                double d = double.CreateTruncating(cur[i]) - double.CreateTruncating(refr[i]);
                if (Math.Abs(d) <= eps || double.IsNaN(d)) { keep &= ~bit; continue; }
                bool rising = (d > 0) == (sign > 0);
                if (rising) { if ((m & bit) != 0) keep &= ~bit; else p |= bit; }
                else { if ((p & bit) != 0) keep &= ~bit; else m |= bit; }
            }
            words[wi] = keep;
            pos.Words[wi] = p;
            neg.Words[wi] = m;
            survivors += BitOperations.PopCount(keep);
        }
        return survivors;
    }
}

// ---- Predicates ----

/// <summary>|a − b| ≤ rel·max(|a|,|b|) + abs.</summary>
public readonly record struct WithinTolerance(double Rel, double Abs) : IPairPredicate
{
    public bool Keep(double a, double b) => Math.Abs(a - b) <= Rel * Math.Max(Math.Abs(a), Math.Abs(b)) + Abs;
}

/// <summary>a ≥ b minus tolerance (non-decreasing with noise allowance).</summary>
public readonly record struct NotBelow(double Rel, double Abs) : IPairPredicate
{
    public bool Keep(double a, double b) => a >= b - (Rel * Math.Abs(b) + Abs);
}

/// <summary>a ≤ b plus tolerance (non-increasing with noise allowance).</summary>
public readonly record struct NotAbove(double Rel, double Abs) : IPairPredicate
{
    public bool Keep(double a, double b) => a <= b + (Rel * Math.Abs(b) + Abs);
}

/// <summary>a − b &gt; max(abs, rel·max(|a|,|b|)) (sign = +1) or b − a &gt; … (sign = −1).</summary>
public readonly record struct NetChange(int Sign, double Rel, double Abs) : IPairPredicate
{
    public bool Keep(double a, double b) => Sign * (a - b) > Math.Max(Abs, Rel * Math.Max(Math.Abs(a), Math.Abs(b)));
}

/// <summary>|a − b| &gt; abs.</summary>
public readonly record struct Differs(double Abs) : IPairPredicate
{
    public bool Keep(double a, double b) => Math.Abs(a - b) > Abs;
}

/// <summary>Compares the current value (a) to fixed operands; ignores the reference.</summary>
public readonly record struct CompareConst(ScanCompare Op, double X, double Y, double Tol) : IPairPredicate
{
    public bool Keep(double a, double b) => Op switch
    {
        ScanCompare.Equal => Math.Abs(a - X) <= Tol,
        ScanCompare.NotEqual => Math.Abs(a - X) > Tol,
        ScanCompare.Greater => a > X,
        ScanCompare.Less => a < X,
        ScanCompare.Between => a >= Math.Min(X, Y) && a <= Math.Max(X, Y),
        _ => true,
    };
}

/// <summary>Compares the current value (a) to the previous scan's value (b).</summary>
public readonly record struct CompareRelative(ScanCompare Op, double X, double Tol) : IPairPredicate
{
    public bool Keep(double a, double b) => Op switch
    {
        ScanCompare.Changed => Math.Abs(a - b) > Tol,
        ScanCompare.Unchanged => Math.Abs(a - b) <= Tol,
        ScanCompare.Increased => a > b + Tol,
        ScanCompare.Decreased => a < b - Tol,
        ScanCompare.IncreasedBy => Math.Abs(a - b - X) <= Tol,
        ScanCompare.DecreasedBy => Math.Abs(b - a - X) <= Tol,
        _ => true,
    };
}
