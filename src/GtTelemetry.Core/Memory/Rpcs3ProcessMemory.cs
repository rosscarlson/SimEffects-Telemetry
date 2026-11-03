using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace GtTelemetry.Core.Memory;

/// <summary>A guest memory range that can be read.</summary>
public readonly record struct GuestRegion(uint Start, uint Size);

/// <summary>Lists the guest memory that's mapped, for sources whose address space has holes.</summary>
public interface IRegionSource
{
    IReadOnlyList<GuestRegion> Regions(uint from, uint to);
}

/// <summary>
/// RPCS3's PS3 memory, read straight from the emulator process. RPCS3 reserves the PS3's 32-bit address space at a fixed
/// host address (0x300000000), so guest address A is host 0x300000000 + A. Only the pages the game has mapped are
/// committed. PS3 data is big-endian: callers convert.
/// </summary>
public sealed partial class Rpcs3ProcessMemory : IMemorySource, IRegionSource
{
    public const ulong GuestBase = 0x3_0000_0000;

    private const uint ProcessVmRead = 0x0010, ProcessVmWrite = 0x0020, ProcessVmOperation = 0x0008, ProcessQueryInformation = 0x0400;
    private const uint MemCommit = 0x1000;
    private const uint PageReadonly = 0x02, PageReadwrite = 0x04;

    private readonly object gate = new();
    private IntPtr handle;
    private Process? process;

    public string Name => "RPCS3 process memory";

    public bool IsConnected => handle != IntPtr.Zero;

    public int ProcessId { get; private set; }

    /// <summary>The running game as RPCS3 shows it in its window title, e.g. "Gran Turismo 6", and its title ID.</summary>
    public (string? Title, string? Id) Game()
    {
        string? title;
        lock (gate)
        {
            if (process == null) return (null, null);
            try
            {
                process.Refresh();
                title = process.MainWindowTitle;
            }
            catch (InvalidOperationException) { return (null, null); }
        }
        return ParseTitle(title);
    }

    /// <summary>RPCS3's game window title: "FPS: 59.93 | Vulkan | 0.0.43-20561 | Gran Turismo 6 [BCUS98296]".</summary>
    public static (string? Title, string? Id) ParseTitle(string? windowTitle)
    {
        if (string.IsNullOrEmpty(windowTitle)) return (null, null);
        var m = TitleId().Match(windowTitle);
        if (!m.Success) return (null, null);
        string name = windowTitle[..m.Index].Trim();
        int bar = name.LastIndexOf('|');
        return (bar >= 0 ? name[(bar + 1)..].Trim() : name, m.Groups[1].Value);
    }

    /// <summary>The title ID of the game in a running RPCS3, without attaching. Null if RPCS3 isn't running a game.</summary>
    public static string? RunningGameId()
    {
        foreach (var p in Process.GetProcessesByName("rpcs3"))
        {
            using (p)
            {
                try
                {
                    if (ParseTitle(p.MainWindowTitle).Id is { } id) return id;
                }
                catch (InvalidOperationException) { }
            }
        }
        return null;
    }

    public void Connect()
    {
        lock (gate)
        {
            DisconnectCore();
            var proc = Process.GetProcessesByName("rpcs3").FirstOrDefault()
                ?? throw new MemorySourceException("RPCS3 isn't running. Start RPCS3 and the game, then connect.");
            handle = OpenProcess(ProcessVmRead | ProcessVmWrite | ProcessVmOperation | ProcessQueryInformation, false, proc.Id);
            if (handle == IntPtr.Zero)
                throw new MemorySourceException("Windows blocked access to the RPCS3 process. Run both apps at the same privilege level (both normal, or both as administrator).",
                    new Win32Exception(Marshal.GetLastWin32Error()));
            process = proc;
            ProcessId = proc.Id;
            // The game's executable is loaded at 0x10000; if that isn't mapped, no game is running.
            if (RegionsCore(0x10000, 0x20000).Count == 0)
            {
                DisconnectCore();
                throw new MemorySourceException("RPCS3 is open but no game is running. Start the game.");
            }
        }
    }

    public void Disconnect()
    {
        lock (gate) DisconnectCore();
    }

    private void DisconnectCore()
    {
        if (handle != IntPtr.Zero) CloseHandle(handle);
        handle = IntPtr.Zero;
        process?.Dispose();
        process = null;
        ProcessId = 0;
    }

    public void Dispose() => Disconnect();

    /// <summary>True while the attached RPCS3 is still running; drops the attachment once it has exited.</summary>
    public bool CheckAlive()
    {
        lock (gate)
        {
            if (process == null) return false;
            bool exited;
            try { exited = process.HasExited; }
            catch (InvalidOperationException) { exited = true; }
            catch (Win32Exception) { exited = false; }
            if (exited) DisconnectCore();
            return !exited;
        }
    }

    public unsafe void Read(uint address, Span<byte> dest)
    {
        lock (gate)
        {
            if (!IsConnected) throw new MemorySourceException("Not attached to RPCS3. Connect first.");
            fixed (byte* p = dest)
            {
                if (!ReadProcessMemory(handle, (IntPtr)(long)(GuestBase + address), (IntPtr)p, (nint)dest.Length, out _))
                {
                    int err = Marshal.GetLastWin32Error();
                    // Unmapped guest memory fails the same way as a closed process; only the latter is fatal.
                    if (!CheckAliveCore()) throw new MemorySourceException("Lost access to RPCS3. Was it closed?", new Win32Exception(err));
                    throw new MemorySourceException($"Guest address 0x{address:X8} isn't mapped.", new Win32Exception(err));
                }
            }
        }
    }

    private bool CheckAliveCore()
    {
        try
        {
            if (process == null || process.HasExited)
            {
                DisconnectCore();
                return false;
            }
        }
        catch (InvalidOperationException)
        {
            DisconnectCore();
            return false;
        }
        return true;
    }

    public unsafe void Write(uint address, ReadOnlySpan<byte> data)
    {
        lock (gate)
        {
            if (!IsConnected) throw new MemorySourceException("Not attached to RPCS3. Connect first.");
            fixed (byte* p = data)
            {
                if (!WriteProcessMemory(handle, (IntPtr)(long)(GuestBase + address), (IntPtr)p, (nint)data.Length, out _))
                    throw new MemorySourceException("Windows refused the write to RPCS3 memory.", new Win32Exception(Marshal.GetLastWin32Error()));
            }
        }
    }

    /// <summary>Readable (committed, read-only or read/write) guest ranges between <paramref name="from"/> and
    /// <paramref name="to"/>, adjacent ranges merged.</summary>
    public IReadOnlyList<GuestRegion> Regions(uint from, uint to)
    {
        lock (gate)
        {
            if (!IsConnected) return [];
            return RegionsCore(from, to);
        }
    }

    private List<GuestRegion> RegionsCore(uint from, uint to)
    {
        var list = new List<GuestRegion>();
        ulong a = GuestBase + from, end = GuestBase + to;
        while (a < end)
        {
            if (VirtualQueryEx(handle, (IntPtr)(long)a, out var m, (nint)Marshal.SizeOf<MemoryBasicInformation>()) == 0) break;
            ulong start = (ulong)m.BaseAddress, size = (ulong)m.RegionSize;
            if (size == 0) break;
            if (m.State == MemCommit && (m.Protect == PageReadonly || m.Protect == PageReadwrite))
            {
                ulong s = Math.Max(start, GuestBase + from), e = Math.Min(start + size, end);
                uint gs = (uint)(s - GuestBase), gsize = (uint)(e - s);
                if (list.Count > 0 && list[^1].Start + list[^1].Size == gs) list[^1] = list[^1] with { Size = list[^1].Size + gsize };
                else list.Add(new GuestRegion(gs, gsize));
            }
            a = start + size;
        }
        return list;
    }

    [GeneratedRegex(@"\[([A-Z]{4}\d{5})\]\s*$")]
    private static partial Regex TitleId();

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public uint Padding1;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
        public uint Padding2;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr h, IntPtr address, IntPtr buffer, nint size, out nint read);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr h, IntPtr address, IntPtr buffer, nint size, out nint written);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint VirtualQueryEx(IntPtr h, IntPtr address, out MemoryBasicInformation info, nint length);
}
