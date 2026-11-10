"""Finds guest memory words equal to given values (e.g. the effect objects, to find the game's own effect wrappers).

    python -I -u tools/nfsu2/find_refs.py 0x8813340 0x88320d0 ... [--from 0x400000] [--to 0x28000000]
"""
import sys

import numpy as np

sys.path.insert(0, __import__("os").path.dirname(__file__))
from nfsu2probe import Probe


def arg(name, default):
    if name in sys.argv:
        i = sys.argv.index(name)
        v = int(sys.argv[i + 1], 0)
        del sys.argv[i:i + 2]
        return v
    return default


def main():
    lo, hi = arg("--from", 0x400000), arg("--to", 0x28000000)
    vals = [int(x, 0) for x in sys.argv[1:]]
    p = Probe()
    chunk = 4 * 1024 * 1024
    want = np.array(vals, dtype="<u4")
    for a in range(lo, hi, chunk):
        try:
            u = np.frombuffer(p.read(a, chunk), dtype="<u4")
        except IOError:
            continue
        for i in np.nonzero(np.isin(u, want))[0]:
            print(f"{a + 4 * int(i):#010x} -> {int(u[i]):#x}", flush=True)


if __name__ == "__main__":
    main()
