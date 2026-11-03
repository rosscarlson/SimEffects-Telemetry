using System.Buffers.Binary;
using System.Globalization;

namespace GtTelemetry.Core.Scanning;

/// <summary>Primitive value types we scan for. All guest data is little-endian (PS2 EE, and PS3 is handled per adapter).</summary>
public enum ValueKind { U8, S8, U16, S16, U32, S32, F32 }

public static class ValueKinds
{
    public static readonly ValueKind[] All = Enum.GetValues<ValueKind>();

    /// <summary>The default scan set: signed variants are opt-in because they duplicate their unsigned twins.</summary>
    public static readonly ValueKind[] Default = [ValueKind.U8, ValueKind.U16, ValueKind.U32, ValueKind.F32];

    public static int Size(this ValueKind k) => k switch
    {
        ValueKind.U8 or ValueKind.S8 => 1,
        ValueKind.U16 or ValueKind.S16 => 2,
        _ => 4,
    };

    public static bool IsFloat(this ValueKind k) => k == ValueKind.F32;

    /// <summary>Smallest difference we treat as "changed" for this kind.</summary>
    public static double Epsilon(this ValueKind k) => k == ValueKind.F32 ? 1e-6 : 0.5;

    public static string Label(this ValueKind k) => k.ToString().ToLowerInvariant();

    public static ValueKind Parse(string s) => s.Trim().ToLowerInvariant() switch
    {
        "u8" or "byte" => ValueKind.U8,
        "s8" or "i8" or "sbyte" => ValueKind.S8,
        "u16" or "ushort" => ValueKind.U16,
        "s16" or "i16" or "short" => ValueKind.S16,
        "u32" or "uint" => ValueKind.U32,
        "s32" or "i32" or "int" => ValueKind.S32,
        "f32" or "float" or "single" => ValueKind.F32,
        _ => throw new FormatException($"Unknown value type '{s}'. Use u8, s8, u16, s16, u32, s32 or f32."),
    };

    public static double Read(this ValueKind k, ReadOnlySpan<byte> b) => k switch
    {
        ValueKind.U8 => b[0],
        ValueKind.S8 => (sbyte)b[0],
        ValueKind.U16 => BinaryPrimitives.ReadUInt16LittleEndian(b),
        ValueKind.S16 => BinaryPrimitives.ReadInt16LittleEndian(b),
        ValueKind.U32 => BinaryPrimitives.ReadUInt32LittleEndian(b),
        ValueKind.S32 => BinaryPrimitives.ReadInt32LittleEndian(b),
        _ => BinaryPrimitives.ReadSingleLittleEndian(b),
    };

    public static byte[] Encode(this ValueKind k, double v)
    {
        var b = new byte[k.Size()];
        switch (k)
        {
            case ValueKind.U8: b[0] = (byte)v; break;
            case ValueKind.S8: b[0] = (byte)(sbyte)v; break;
            case ValueKind.U16: BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)v); break;
            case ValueKind.S16: BinaryPrimitives.WriteInt16LittleEndian(b, (short)v); break;
            case ValueKind.U32: BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)v); break;
            case ValueKind.S32: BinaryPrimitives.WriteInt32LittleEndian(b, (int)v); break;
            default: BinaryPrimitives.WriteSingleLittleEndian(b, (float)v); break;
        }
        return b;
    }

    /// <summary>Formats with a real minus sign, per the design guide.</summary>
    public static string Format(this ValueKind k, double v)
    {
        string s = k.IsFloat()
            ? (double.IsFinite(v) && Math.Abs(v) < 1e7 && (v == 0 || Math.Abs(v) >= 1e-4) ? v.ToString("0.####", CultureInfo.InvariantCulture) : v.ToString("G6", CultureInfo.InvariantCulture))
            : v.ToString("0", CultureInfo.InvariantCulture);
        return s.Replace('-', '−');
    }

    /// <summary>Plausible floats only: finite, not denormal garbage, not astronomically large.</summary>
    public static bool IsSaneFloat(double v) => double.IsFinite(v) && (v == 0 || (Math.Abs(v) > 1e-9 && Math.Abs(v) < 1e8));

    public static uint ParseAddress(string s)
    {
        s = s.Trim().Replace("_", "");
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return uint.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    public static string FormatAddress(uint a) => $"0x{a:X8}";
}
