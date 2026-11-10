using GtTelemetry.Core.Memory;

namespace GtTelemetry.Core.Browser;

/// <summary>
/// A browser game's memory through the extension (<see cref="BrowserBridge"/>): NFSU2 on nfsu2.xyz, whose guest
/// addresses are offsets into the page's WebAssembly memory. Read-only: the game is played on public servers, so
/// nothing is ever written to it.
/// </summary>
public sealed class BrowserMemorySource(BrowserBridge bridge) : IMemorySource, ISnapshotSource
{
    /// <summary>The largest read sent in one request (the extension caps a range at 16 MB).</summary>
    public const int Chunk = 4 * 1024 * 1024;

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    public BrowserBridge Bridge => bridge;

    public string Name => "Browser (nfsu2.xyz)";

    public bool IsConnected => bridge.HasMemory;

    public void Connect()
    {
        try { bridge.Start(); }
        catch (BrowserBridgeException ex) { throw new MemorySourceException(ex.Message, ex); }
        if (!bridge.IsPageConnected)
            throw new MemorySourceException("The NFSU2 page isn't connected. Open nfsu2.xyz in Chrome with the SimEffects extension turned on.");
        if (!bridge.HasMemory)
            throw new MemorySourceException("The NFSU2 page is open but the game hasn't started yet. Press Play on nfsu2.xyz.");
    }

    public void Disconnect() { }

    public void Read(uint address, Span<byte> dest)
    {
        try
        {
            for (int done = 0; done < dest.Length; done += Chunk)
            {
                int n = Math.Min(Chunk, dest.Length - done);
                bridge.Read(address + (uint)done, dest.Slice(done, n), Timeout);
            }
        }
        catch (BrowserBridgeException ex) { throw new MemorySourceException(ex.Message, ex); }
    }

    /// <summary>Several ranges in one round trip (what a telemetry frame needs).</summary>
    public byte[][] ReadMany(IReadOnlyList<(uint Address, int Length)> ranges)
    {
        try { return bridge.ReadAsync(ranges, Timeout).GetAwaiter().GetResult(); }
        catch (BrowserBridgeException ex) { throw new MemorySourceException(ex.Message, ex); }
    }

    public void ReadBulk(uint address, Span<byte> dest) => Read(address, dest);

    public void Write(uint address, ReadOnlySpan<byte> data) =>
        throw new MemorySourceException("NFSU2 is played on nfsu2.xyz's public servers, so the app never writes to it.");

    public void Dispose() { }
}
