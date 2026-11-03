"""Read-only: find FH3 camera objects by vtable and print FOV (+0x1F0) and offset (+0x1B0)."""
import sys, struct, math, numpy as np
src = open(r'C:\Users\ross\OneDrive\Code\FH2\tools\live.py').read().split('PAIRS =')[0]
sys.argv = ['x', 'none']; exec(src)
VT = {0x14463acb0: 'Driver', 0x14463a710: 'Cockpit', 0x14463a9e0: 'Hood', 0x144630430: 'BumperHigh', 0x1446309d0: 'Wheel', 0x144630ca0: 'Side', 0x144630700: 'RearView'}
found = []; a = 0; m = MBI()
while k32.VirtualQueryEx(h, a, ctypes.byref(m), ctypes.sizeof(m)):
    if m.State == 0x1000 and m.Protect in (0x04, 0x40) and m.Type == 0x20000:
        for off in range(0, m.RegionSize, 8 << 20):
            n = min(8 << 20, m.RegionSize - off); b = rd(m.BaseAddress + off, n)
            if not b: continue
            q = np.frombuffer(b[:len(b) // 8 * 8], dtype='<u8')
            for v in VT:
                for i in np.nonzero(q == v)[0]: found.append((m.BaseAddress + off + int(i) * 8, VT[v]))
    a = m.BaseAddress + m.RegionSize
g = rd(0x144639f58, 4); print('static Driver FOV', struct.unpack('<f', g)[0], 'offsets', struct.unpack('<3f', rd(0x144e72b70, 12)), 'triple', struct.unpack('<f', rd(0x144e72964, 4))[0], 'cockpit ratio', struct.unpack('<f', rd(0x144639ee0, 4))[0])
for addr, name in found:
    b = rd(addr, 0x260)
    fov = struct.unpack_from('<f', b, 0x1f0)[0]
    print('%-10s %x fov %.4f (%.1f deg) off %s +240 %s flag238 %d' % (name, addr, fov, math.degrees(fov), ['%.3f' % x for x in struct.unpack_from('<3f', b, 0x1b0)], ['%.3f' % x for x in struct.unpack_from('<4f', b, 0x240)], b[0x238]))
