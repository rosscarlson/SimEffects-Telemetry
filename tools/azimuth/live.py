"""Read-only look at azimuth.exe: find CCarDynamics objects by vtable pair and dump fields.
live.py scan            -> list cars
live.py watch <secs>    -> log the player's car fields once per 0.5 s to stdout"""
import ctypes, ctypes.wintypes as wt, struct, sys, time, numpy as np
k32 = ctypes.WinDLL('kernel32', use_last_error=True)
class MBI(ctypes.Structure):
    _fields_ = [('BaseAddress', ctypes.c_ulonglong), ('AllocationBase', ctypes.c_ulonglong), ('AllocationProtect', wt.DWORD),
                ('PartitionId', wt.WORD), ('RegionSize', ctypes.c_ulonglong), ('State', wt.DWORD), ('Protect', wt.DWORD), ('Type', wt.DWORD)]
k32.VirtualQueryEx.argtypes = [wt.HANDLE, ctypes.c_ulonglong, ctypes.POINTER(MBI), ctypes.c_size_t]
k32.ReadProcessMemory.argtypes = [wt.HANDLE, ctypes.c_ulonglong, ctypes.c_void_p, ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]
k32.OpenProcess.restype = wt.HANDLE
import subprocess
pid = int([l for l in subprocess.run(['tasklist', '/FI', 'IMAGENAME eq azimuth.exe', '/FO', 'CSV', '/NH'], capture_output=True, text=True).stdout.splitlines() if 'azimuth' in l.lower()][0].split('","')[1])
h = k32.OpenProcess(0x0410, False, pid)
def rd(a, n):
    buf = ctypes.create_string_buffer(n); got = ctypes.c_size_t()
    if not k32.ReadProcessMemory(h, a, buf, n, ctypes.byref(got)): return None
    return buf.raw[:got.value]
hdr = rd(0x140000000, 0x400)
lf = struct.unpack_from('<I', hdr, 0x3c)[0]
print('pid', pid, 'PE stamp %08x' % struct.unpack_from('<I', hdr, lf + 8)[0])
PAIRS = [(0x144680ea0, 0x144680f30), (0x144682578, 0x144682608)]
SUB = 0x4B0
def scan():
    cars = []; a = 0; m = MBI()
    while k32.VirtualQueryEx(h, a, ctypes.byref(m), ctypes.sizeof(m)):
        if m.State == 0x1000 and m.Protect in (0x04, 0x40) and m.Type == 0x20000:
            for off in range(0, m.RegionSize, 8 << 20):
                n = min(8 << 20, m.RegionSize - off); b = rd(m.BaseAddress + off, n)
                if not b: continue
                q = np.frombuffer(b[:len(b) // 8 * 8], dtype='<u8')
                for p, s in PAIRS:
                    for i in np.nonzero(q == p)[0]:
                        j = i + SUB // 8
                        if j < len(q) and q[j] == s: cars.append(m.BaseAddress + off + int(i) * 8)
        a = m.BaseAddress + m.RegionSize
    return cars
BS = 0xD200
def f(b, o): return struct.unpack_from('<f', b, SUB + o)[0]
def i32(b, o): return struct.unpack_from('<i', b, SUB + o)[0]
W0, WS = 0x1750, 0x1490
def row(b):
    rpm = f(b, 0x148) * 9.5493; mx = f(b, 0x1d4) * 9.5493; idle = f(b, 0x5c8) * 9.5493
    w = lambda k, o: f(b, W0 + k * WS + o)
    return ('rpm %6.0f max %6.0f idle %5.0f tq %7.1f pw %9.0f clu %.2f gear %d/%d thr %.2f brk %.2f hb %.2f steer %+.2f spd %5.1f pos %8.1f %8.1f %8.1f ply %d ai %d | spin %s susp %s slipA %s vl %s rad %s droop %s bump %s rest %s' % (
        rpm, mx, idle, f(b, 0x18c), f(b, 0x194), f(b, 0xa94), b[SUB + 0xaa8], i32(b, 0xac4), f(b, 0x1298), f(b, 0x129c), f(b, 0x12a8),
        f(b, 8) / f(b, 0x58) if f(b, 0x58) else 0, f(b, 0x12d4), f(b, 0x13a0), f(b, 0x13a4), f(b, 0x13a8), b[SUB + 0xcb89], i32(b, 0x1534),
        ' '.join('%6.1f' % w(k, 0xc) for k in range(4)), ' '.join('%.3f' % w(k, 0) for k in range(4)),
        ' '.join('%+.3f' % w(k, 0x48) for k in range(4)), ' '.join('%5.1f' % w(k, 0x2fc) for k in range(4)),
        ' '.join('%.3f' % w(k, 0x414) for k in range(4)), ' '.join('%.3f' % w(k, 0x458) for k in range(4)),
        ' '.join('%.3f' % w(k, 0x460) for k in range(4)), ' '.join('%.3f' % w(k, 0x524) for k in range(4))))
cars = scan(); print(len(cars), 'cars')
if sys.argv[1] == 'scan':
    for c in cars:
        b = rd(c, BS)
        if b: print('%x' % c, row(b))
else:
    end = time.time() + float(sys.argv[2])
    while time.time() < end:
        for c in cars:
            b = rd(c, BS)
            if b and b[SUB + 0xcb89] == 1: print('%.1f %x' % (time.time() % 1000, c), row(b), flush=True)
        time.sleep(0.5)
