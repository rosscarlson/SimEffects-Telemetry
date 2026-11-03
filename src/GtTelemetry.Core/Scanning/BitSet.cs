using System.Numerics;

namespace GtTelemetry.Core.Scanning;

/// <summary>Dense candidate set: bit i set ⇔ element i (address = base + i·size) is still a candidate.</summary>
public sealed class BitSet
{
    public BitSet(int length, bool allSet)
    {
        Length = length;
        Words = new ulong[(length + 63) / 64];
        if (allSet)
        {
            Array.Fill(Words, ulong.MaxValue);
            int tail = length % 64;
            if (tail != 0) Words[^1] = (1UL << tail) - 1;
        }
    }

    public int Length { get; }

    public ulong[] Words { get; }

    public bool this[int i] => (Words[i >> 6] & (1UL << (i & 63))) != 0;

    public long Count()
    {
        long n = 0;
        foreach (var w in Words) n += BitOperations.PopCount(w);
        return n;
    }

    public void Clear(int i) => Words[i >> 6] &= ~(1UL << (i & 63));

    public void Set(int i) => Words[i >> 6] |= 1UL << (i & 63);

    public IEnumerable<int> Indices()
    {
        for (int wi = 0; wi < Words.Length; wi++)
        {
            ulong w = Words[wi];
            while (w != 0)
            {
                yield return wi * 64 + BitOperations.TrailingZeroCount(w);
                w &= w - 1;
            }
        }
    }
}
