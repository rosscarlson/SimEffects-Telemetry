"""The game state and the player's car, from the addresses in th0mk/nfsu2-web (runtime/game.h, drift.c, dinput_pad.c).

    python -I -u tools/nfsu2/car.py [--watch]
"""
import math
import struct
import sys
import time

sys.path.insert(0, __import__("os").path.dirname(__file__))
from nfsu2probe import Probe

G_FLOW = 0x008654A4        # 3 front end, 4/5 loading, 6 in the world
G_WORLDDT = 0x00865154     # float s, 0 while the world stands
PLAYERS = 0x00D00000       # wasm build: [+0] player 1


def once(p):
    flow = p.u32(G_FLOW)
    dt = p.f32(G_WORLDDT)
    pl = p.u32(PLAYERS)
    car = p.u32(pl + 4) if p.ptr_ok(pl) else 0
    line = f"flow {flow} dt {dt:.4f} player {pl:#010x} car {car:#010x}"
    if p.ptr_ok(car):
        b = p.read(car, 0x460)
        vx, vy, vz = struct.unpack_from("<3f", b, 0x70)
        fwd, spd = struct.unpack_from("<2f", b, 0x42C)
        gear = struct.unpack_from("<i", b, 0x434)[0]
        rpm = struct.unpack_from("<f", b, 0x440)[0]
        line += (f" | v ({vx:7.2f},{vy:7.2f},{vz:7.2f}) |v| {math.sqrt(vx*vx+vy*vy+vz*vz):6.2f}"
                 f" fwd {fwd:6.2f} speed {spd:6.2f} gear {gear} rpm {rpm:7.1f}")
    return line


def main():
    p = Probe()
    if "--watch" in sys.argv:
        while True:
            print(once(p), flush=True)
            time.sleep(0.25)
    print(once(p))


if __name__ == "__main__":
    main()
