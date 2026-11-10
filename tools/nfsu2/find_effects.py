"""Finds the runtime's force-feedback effect table (g_eff in th0mk/nfsu2-web runtime/dinput_pad.c) in the page's
memory, and prints it, once or live.

The signature: an entry is { u32 obj; int kind (0x20..0x2B); ... }, and the guest effect object it points to holds the
entry's slot + 1 at obj+4 (CreateEffect: MR32(obj + 4) = slot + 1). The runtime's data is above the guest's memory
(0x28000000, runtime/guest_span.h).

    python -I -u tools/nfsu2/find_effects.py [--from 0x28000000] [--size 0x4000000] [--base 0x...] [--stride 80] [--watch]
"""
import struct
import sys
import time
from collections import Counter

import numpy as np

sys.path.insert(0, __import__("os").path.dirname(__file__))
from nfsu2probe import Probe

NAMES = ["constant", "ramp", "square", "sine", "triangle", "saw up", "saw down",
         "spring", "damper", "inertia", "friction", "custom"]


def arg(name, default):
    if name in sys.argv:
        return int(sys.argv[sys.argv.index(name) + 1], 0)
    return default


def scan(p, start, size):
    found = []
    chunk = 4 * 1024 * 1024
    for a in range(start, start + size, chunk):
        try:
            u = np.frombuffer(p.read(a, chunk), dtype="<u4")
        except IOError as e:
            print(f"stop at {a:#x}: {e}")
            break
        kind_ok = (u[1:] >= 0x20) & (u[1:] <= 0x2B)
        obj_ok = (u[:-1] >= 0x10000) & (u[:-1] < 0x28000000)
        for i in np.nonzero(kind_ok & obj_ok)[0]:
            found.append((a + 4 * int(i), int(u[i]), int(u[i + 1])))
    print(f"{len(found)} candidates")
    hits = []
    for addr, obj, kind in found[:20000]:
        try:
            v = p.u32(obj + 4)
        except IOError:
            continue
        if 1 <= v <= 64:
            hits.append((addr, obj, kind, v))
    return hits


def decode(b, stride):
    out = []
    for i in range(64):
        e = b[i * stride:(i + 1) * stride]
        obj, kind, dur, gain, delay, mag, off, period, env = struct.unpack_from("<IiIIIiiIi", e, 0)
        att_lvl, att_us, fade_lvl, fade_us, playing, iters = struct.unpack_from("<IIIIiI", e, 36)
        if obj:
            name = NAMES[kind - 0x20] if 0x20 <= kind <= 0x2B else f"?{kind:#x}"
            out.append(f"#{i:2} {name:9} obj {obj:#010x} {'PLAY' if playing else 'stop'} mag {mag:6} off {off:6} "
                       f"period {period:7}us dur {dur if dur != 0xFFFFFFFF else 'inf':>9} gain {gain:5} delay {delay}"
                       + (f" env {att_lvl}/{att_us} {fade_lvl}/{fade_us}" if env else ""))
    return out


def main():
    p = Probe()
    stride = arg("--stride", 80)
    base = arg("--base", 0)
    if not base:
        hits = scan(p, arg("--from", 0x28000000), arg("--size", 0x4000000))
        for h in hits[:40]:
            print(f"  entry at {h[0]:#x} obj {h[1]:#x} kind {h[2]:#x} slot {h[3] - 1}")
        bases = Counter(addr - (v - 1) * stride for addr, _, _, v in hits)
        if not bases:
            print("No effect table found (no effects created yet? is the wheel's FFB on?)")
            return
        base = bases.most_common(1)[0][0]
        print(f"g_eff at {base:#x} (stride {stride}, {bases.most_common(1)[0][1]} entries agree)")
    last = None
    while True:
        lines = decode(p.read(base, 64 * stride), stride)
        if lines != last:
            print(time.strftime("%H:%M:%S"), f"{len(lines)} effects")
            for l in lines:
                print("  " + l)
            last = lines
        if "--watch" not in sys.argv:
            break
        time.sleep(0.05)


if __name__ == "__main__":
    main()
