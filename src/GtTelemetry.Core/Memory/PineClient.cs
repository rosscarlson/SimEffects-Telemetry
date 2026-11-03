using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace GtTelemetry.Core.Memory;

public enum EmulatorStatus { Running = 0, Paused = 1, Shutdown = 2, Unknown = -1 }

/// <summary>
/// Client for the PINE IPC protocol (PCSX2, RPCS3, DuckStation). On Windows PINE listens on TCP 127.0.0.1:slot.
/// Wire format: request  = [u32 total size][opcode u8][args]...  (several commands may be batched)
///              reply    = [u32 total size][u8 result: 0 OK / 0xFF fail][data]...
/// See docs/pine.md.
/// </summary>
public sealed class PineClient : IMemorySource
{
    public const int DefaultSlot = 28011;

    // PCSX2 limits (pcsx2/PINE.h): MAX_IPC_SIZE 650000, MAX_IPC_RETURN_SIZE 450000.
    private const int MaxReplyData = 450_000 - 5;
    private const int Read64PerBatch = 50_000;

    private enum Op : byte
    {
        Read8 = 0, Read16 = 1, Read32 = 2, Read64 = 3,
        Write8 = 4, Write16 = 5, Write32 = 6, Write64 = 7,
        Version = 8, SaveState = 9, LoadState = 0xA, Title = 0xB, Id = 0xC, Uuid = 0xD, GameVersion = 0xE, Status = 0xF,
    }

    private readonly object gate = new();
    private TcpClient? tcp;
    private NetworkStream? stream;
    private byte[] replyBuffer = new byte[MaxReplyData + 64];

    public PineClient(int slot = DefaultSlot) => Slot = slot;

    public int Slot { get; }

    public string Name => $"PINE (slot {Slot})";

    public bool IsConnected => tcp?.Connected == true;

    public void Connect()
    {
        lock (gate)
        {
            DisconnectCore();
            var client = new TcpClient { NoDelay = true, ReceiveTimeout = 3000, SendTimeout = 3000 };
            try
            {
                if (!client.ConnectAsync("127.0.0.1", Slot).Wait(1500))
                    throw new SocketException((int)SocketError.TimedOut);
            }
            catch (Exception ex)
            {
                client.Dispose();
                throw new MemorySourceException(
                    $"Couldn't reach PINE on port {Slot}. In PCSX2, turn on Settings → Advanced → PINE Settings → Enable, " +
                    $"check the slot is {Slot}, and make sure a game is running.", ex is AggregateException ae ? ae.InnerException : ex);
            }
            tcp = client;
            stream = client.GetStream();
        }
    }

    public void Disconnect()
    {
        lock (gate) DisconnectCore();
    }

    private void DisconnectCore()
    {
        stream?.Dispose();
        tcp?.Dispose();
        stream = null;
        tcp = null;
    }

    public void Dispose() => Disconnect();

    // ---- Info ----

    public string GetVersion() => GetString(Op.Version);

    public string GetTitle() => GetString(Op.Title);

    public string GetGameId() => GetString(Op.Id);

    public string GetGameUuid() => GetString(Op.Uuid);

    public string GetGameVersion() => GetString(Op.GameVersion);

    public EmulatorStatus GetStatus()
    {
        var reply = Transact([(byte)Op.Status], 4);
        uint s = BinaryPrimitives.ReadUInt32LittleEndian(reply);
        return s <= 2 ? (EmulatorStatus)s : EmulatorStatus.Unknown;
    }

    private string GetString(Op op)
    {
        lock (gate)
        {
            var reply = TransactCore([(byte)op], expectedData: -1);
            int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(reply);
            var bytes = reply.Slice(4, Math.Min(len, reply.Length - 4));
            return Encoding.UTF8.GetString(bytes).TrimEnd('\0');
        }
    }

    // ---- Memory ----

    public void Read(uint address, Span<byte> dest)
    {
        if (dest.IsEmpty) return;

        // Small reads of 1/2/4/8 bytes go out as a single native-width command.
        if (dest.Length is 1 or 2 or 4 or 8 && address % (uint)dest.Length == 0)
        {
            var op = dest.Length switch { 1 => Op.Read8, 2 => Op.Read16, 4 => Op.Read32, _ => Op.Read64 };
            Span<byte> req = stackalloc byte[5];
            req[0] = (byte)op;
            BinaryPrimitives.WriteUInt32LittleEndian(req[1..], address);
            lock (gate) TransactCore(req, dest.Length).CopyTo(dest);
            return;
        }

        // Everything else is read as aligned 64-bit words in batches, then trimmed.
        uint start = address & ~7u;
        uint end = (uint)((address + (ulong)dest.Length + 7) & ~7ul);
        int words = (int)((end - start) / 8);
        int skip = (int)(address - start);
        int written = 0;

        lock (gate)
        {
            for (int w = 0; w < words; w += Read64PerBatch)
            {
                int n = Math.Min(Read64PerBatch, words - w);
                var req = new byte[n * 5];
                for (int i = 0; i < n; i++)
                {
                    req[i * 5] = (byte)Op.Read64;
                    BinaryPrimitives.WriteUInt32LittleEndian(req.AsSpan(i * 5 + 1), start + (uint)(w + i) * 8);
                }
                var data = TransactCore(req, n * 8);
                if (w == 0) data = data[skip..];
                int take = Math.Min(data.Length, dest.Length - written);
                data[..take].CopyTo(dest[written..]);
                written += take;
            }
        }
    }

    /// <summary>
    /// Reads many small values in a single round trip. Each request is (address, size) with size 1/2/4/8;
    /// values are written into <paramref name="dest"/> back to back in request order.
    /// </summary>
    public void ReadMany(ReadOnlySpan<(uint Address, int Size)> requests, Span<byte> dest)
    {
        var req = new byte[requests.Length * 5];
        int total = 0;
        for (int i = 0; i < requests.Length; i++)
        {
            req[i * 5] = requests[i].Size switch
            {
                1 => (byte)Op.Read8, 2 => (byte)Op.Read16, 4 => (byte)Op.Read32, 8 => (byte)Op.Read64,
                _ => throw new ArgumentException("Size must be 1, 2, 4 or 8."),
            };
            BinaryPrimitives.WriteUInt32LittleEndian(req.AsSpan(i * 5 + 1), requests[i].Address);
            total += requests[i].Size;
        }
        lock (gate) TransactCore(req, total).CopyTo(dest);
    }

    public void Write(uint address, ReadOnlySpan<byte> data)
    {
        // Byte-granular writes keep this simple and alignment-safe; writes are small (camera values etc.).
        var req = new List<byte>(data.Length * 6);
        var addr = new byte[4];
        int i = 0;
        while (i < data.Length)
        {
            uint a = address + (uint)i;
            int remaining = data.Length - i;
            (Op op, int size) = remaining >= 4 && a % 4 == 0 ? (Op.Write32, 4)
                : remaining >= 2 && a % 2 == 0 ? (Op.Write16, 2)
                : (Op.Write8, 1);
            req.Add((byte)op);
            BinaryPrimitives.WriteUInt32LittleEndian(addr, a);
            req.AddRange(addr);
            req.AddRange(data.Slice(i, size).ToArray());
            i += size;
        }
        lock (gate) TransactCore(req.ToArray(), 0);
    }

    private ReadOnlySpan<byte> Transact(ReadOnlySpan<byte> commands, int expectedData)
    {
        lock (gate) return TransactCore(commands, expectedData).ToArray();
    }

    /// <summary>Sends one (possibly batched) request and returns the reply data after the result byte.</summary>
    private ReadOnlySpan<byte> TransactCore(ReadOnlySpan<byte> commands, int expectedData)
    {
        if (stream == null) throw new MemorySourceException("Not connected to PINE. Connect first.");
        try
        {
            var msg = new byte[commands.Length + 4];
            BinaryPrimitives.WriteUInt32LittleEndian(msg, (uint)msg.Length);
            commands.CopyTo(msg.AsSpan(4));
            stream.Write(msg);

            Span<byte> header = stackalloc byte[4];
            stream.ReadExactly(header);
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(header);
            if (size < 5 || size > 16 * 1024 * 1024)
                throw new MemorySourceException($"PINE sent a malformed reply ({size} bytes). Reconnect and try again.");
            if (replyBuffer.Length < size) replyBuffer = new byte[size];
            stream.ReadExactly(replyBuffer, 0, size - 4);
            if (replyBuffer[0] != 0)
                throw new MemorySourceException("PCSX2 rejected the request. Is a game running (not just the BIOS or game list)?");
            var data = replyBuffer.AsSpan(1, size - 5);
            if (expectedData >= 0 && data.Length < expectedData)
                throw new MemorySourceException($"PINE reply was short ({data.Length} of {expectedData} bytes).");
            return data;
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            DisconnectCore();
            throw new MemorySourceException("Lost the PINE connection. Is PCSX2 still running with PINE enabled?", ex);
        }
    }
}
