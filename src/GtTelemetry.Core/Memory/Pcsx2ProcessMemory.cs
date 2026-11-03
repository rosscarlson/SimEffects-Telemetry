using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace GtTelemetry.Core.Memory;

/// <summary>
/// Fast path for bulk reads: PCSX2 (Qt, 1.7+) exports the host pointer to EE RAM as the data symbol <c>EEmem</c>.
/// We locate it through the export table of the running pcsx2-qt.exe and copy RAM with ReadProcessMemory —
/// a full 32 MB snapshot takes a few milliseconds instead of hundreds over PINE.
/// Used for scanning; PINE stays the primary source for everything else.
/// </summary>
public sealed class Pcsx2ProcessMemory : IMemorySource
{
    public const uint EeRamSize = 32 * 1024 * 1024;

    private const uint ProcessVmRead = 0x0010, ProcessVmWrite = 0x0020, ProcessVmOperation = 0x0008, ProcessQueryInformation = 0x0400;

    private readonly object gate = new();
    private IntPtr handle;
    private ulong eeMem;

    public string Name => "PCSX2 process memory";

    public bool IsConnected => handle != IntPtr.Zero && eeMem != 0;

    public int ProcessId { get; private set; }

    public string? ExecutablePath { get; private set; }

    public void Connect()
    {
        lock (gate)
        {
            DisconnectCore();
            var proc = Process.GetProcesses()
                .Where(p => p.ProcessName.StartsWith("pcsx2", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => p.ProcessName.Contains("qt", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault()
                ?? throw new MemorySourceException("PCSX2 isn't running. Start PCSX2 and load the game, then connect.");

            handle = OpenProcess(ProcessVmRead | ProcessVmWrite | ProcessVmOperation | ProcessQueryInformation, false, proc.Id);
            if (handle == IntPtr.Zero)
                throw new MemorySourceException("Windows blocked access to the PCSX2 process. Run both apps at the same privilege level (both normal, or both as administrator).",
                    new Win32Exception(Marshal.GetLastWin32Error()));

            try
            {
                var module = proc.MainModule ?? throw new MemorySourceException("Couldn't read the PCSX2 module list.");
                ExecutablePath = module.FileName;
                ProcessId = proc.Id;
                ulong exportVar = FindExport((ulong)module.BaseAddress, "EEmem")
                    ?? throw new MemorySourceException("This PCSX2 build doesn't export EEmem (needs PCSX2 1.7 or newer). Fast snapshots are unavailable; PINE still works.");
                eeMem = ReadU64(exportVar);
                if (eeMem == 0)
                    throw new MemorySourceException("PCSX2 hasn't allocated EE memory yet. Start the game, then connect.");
            }
            catch
            {
                DisconnectCore();
                throw;
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
        eeMem = 0;
        ProcessId = 0;
    }

    public void Dispose() => Disconnect();

    public unsafe void Read(uint address, Span<byte> dest)
    {
        // Mask off the KSEG/uncached mirror bits so 0x20xxxxxx / 0x30xxxxxx addresses land in RAM.
        address &= EeRamSize - 1;
        if (address + (ulong)dest.Length > EeRamSize)
            throw new ArgumentOutOfRangeException(nameof(address), "Read runs past the end of EE RAM.");
        lock (gate)
        {
            if (!IsConnected) throw new MemorySourceException("Not attached to the PCSX2 process. Connect first.");
            fixed (byte* p = dest)
            {
                if (!ReadProcessMemory(handle, (IntPtr)(eeMem + address), (IntPtr)p, (nint)dest.Length, out _))
                {
                    DisconnectCore();
                    throw new MemorySourceException("Lost access to the PCSX2 process. Was it closed?", new Win32Exception(Marshal.GetLastWin32Error()));
                }
            }
        }
    }

    public unsafe void Write(uint address, ReadOnlySpan<byte> data)
    {
        address &= EeRamSize - 1;
        lock (gate)
        {
            if (!IsConnected) throw new MemorySourceException("Not attached to the PCSX2 process. Connect first.");
            fixed (byte* p = data)
            {
                if (!WriteProcessMemory(handle, (IntPtr)(eeMem + address), (IntPtr)p, (nint)data.Length, out _))
                    throw new MemorySourceException("Windows refused the write to PCSX2 memory.", new Win32Exception(Marshal.GetLastWin32Error()));
            }
        }
    }

    // ---- PE export lookup in the remote process ----

    private ulong? FindExport(ulong moduleBase, string name)
    {
        uint peOffset = ReadU32(moduleBase + 0x3C);
        ulong optional = moduleBase + peOffset + 24;
        ushort magic = ReadU16(optional);
        ulong dataDirs = optional + (magic == 0x20B ? 112u : 96u);
        uint exportRva = ReadU32(dataDirs);
        if (exportRva == 0) return null;

        ulong dir = moduleBase + exportRva;
        uint count = ReadU32(dir + 24);
        uint functions = ReadU32(dir + 28), names = ReadU32(dir + 32), ordinals = ReadU32(dir + 36);
        for (uint i = 0; i < count; i++)
        {
            uint nameRva = ReadU32(moduleBase + names + i * 4);
            if (ReadAsciiZ(moduleBase + nameRva, 64) != name) continue;
            ushort ordinal = ReadU16(moduleBase + ordinals + i * 2);
            return moduleBase + ReadU32(moduleBase + functions + ordinal * 4u);
        }
        return null;
    }

    private unsafe void ReadRaw(ulong address, Span<byte> dest)
    {
        fixed (byte* p = dest)
            if (!ReadProcessMemory(handle, (IntPtr)address, (IntPtr)p, (nint)dest.Length, out _))
                throw new MemorySourceException("Couldn't read the PCSX2 executable image.", new Win32Exception(Marshal.GetLastWin32Error()));
    }

    private ushort ReadU16(ulong a) { Span<byte> b = stackalloc byte[2]; ReadRaw(a, b); return BitConverter.ToUInt16(b); }

    private uint ReadU32(ulong a) { Span<byte> b = stackalloc byte[4]; ReadRaw(a, b); return BitConverter.ToUInt32(b); }

    private ulong ReadU64(ulong a) { Span<byte> b = stackalloc byte[8]; ReadRaw(a, b); return BitConverter.ToUInt64(b); }

    private string ReadAsciiZ(ulong a, int max)
    {
        Span<byte> b = stackalloc byte[max];
        ReadRaw(a, b);
        int len = b.IndexOf((byte)0);
        return Encoding.ASCII.GetString(len < 0 ? b : b[..len]);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr h, IntPtr address, IntPtr buffer, nint size, out nint read);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr h, IntPtr address, IntPtr buffer, nint size, out nint written);
}
