namespace GtTelemetry.Core.Memory;

/// <summary>
/// Read/write access to an emulated (or native) address space. Addresses are guest addresses
/// as the game sees them (e.g. PS2 EE addresses 0x00000000–0x01FFFFFF).
/// Implementations are thread-safe.
/// </summary>
public interface IMemorySource : IDisposable
{
    string Name { get; }

    bool IsConnected { get; }

    void Connect();

    void Disconnect();

    /// <summary>Reads <paramref name="dest"/>.Length bytes starting at <paramref name="address"/>.</summary>
    void Read(uint address, Span<byte> dest);

    void Write(uint address, ReadOnlySpan<byte> data);
}

/// <summary>Anything that can hand over a bulk copy of guest RAM (used by scans and guided sessions).</summary>
public interface ISnapshotSource
{
    void ReadBulk(uint address, Span<byte> dest);
}

/// <summary>Thrown when a memory source can't be reached or rejects a request. The message explains the fix.</summary>
public sealed class MemorySourceException(string message, Exception? inner = null) : Exception(message, inner);
