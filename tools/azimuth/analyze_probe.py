"""Offline analysis of an FH2Telemetry probe recording.

usage: python -I analyze_probe.py probe-XXXX.bin

Finds the player's car (the one revving while stationary), reports the raw
gear byte per phase, and ranks per-wheel float fields by how they behave in
each phase, to label suspension, slip and rumble offsets.
"""
import gzip, struct, sys, math
from collections import defaultdict

ICD = 0x3A0
WHEEL0, STRIDE = 0x11B0, 0x4B0
RPM = 9.549296


def load(path):
    with open(path, 'rb') as f:
        head = f.read(16)
        assert head[:8] == b'FH2PROBE', 'not a probe file'
        bs = struct.unpack('<I', head[12:16])[0]
        data = gzip.decompress(f.read())
    frames, marks, p = [], [], 0
    while p < len(data):
        kind = data[p]; p += 1
        if kind == 1:
            t, n = struct.unpack_from('<dI', data, p); p += 12
            cars = {}
            for _ in range(n):
                a = struct.unpack_from('<Q', data, p)[0]; p += 8
                cars[a] = data[p:p + bs]; p += bs
            frames.append((t, cars))
        elif kind == 2:
            t, ln = struct.unpack_from('<dH', data, p); p += 10
            marks.append((t, data[p:p + ln].decode())); p += ln
        else:
            raise ValueError('bad record %d at %d' % (kind, p))
    return bs, frames, marks


def f32(b, icd): return struct.unpack_from('<f', b, ICD + icd)[0]


def phase_of(marks):
    def ph(t):
        cur = 'start'
        for mt, txt in marks:
            if mt <= t: cur = txt
        return cur
    return ph


def main(path):
    bs, frames, marks = load(path)
    ph = phase_of(marks)
    print('%d frames, %.1f s, block 0x%x' % (len(frames), frames[-1][0], bs))
    for t, m in marks: print('  %6.1f  %s' % (t, m))

    # Player car: largest RPM swing during the stationary rev phase.
    rev = [m for m in marks if m[1].startswith('Stay stopped')]
    t0, t1 = (rev[0][0], rev[0][0] + 12) if rev else (10, 22)
    swing = defaultdict(lambda: [1e9, -1e9])
    for t, cars in frames:
        if t0 <= t <= t1:
            for a, b in cars.items():
                r = f32(b, 0x148) * RPM
                swing[a][0] = min(swing[a][0], r); swing[a][1] = max(swing[a][1], r)
    print('\nRPM range during stationary revs, per car:')
    for a, (lo, hi) in sorted(swing.items(), key=lambda x: x[1][0] - x[1][1]):
        print('  %X  %6.0f .. %6.0f' % (a, lo, hi))
    player = max(swing, key=lambda a: swing[a][1] - swing[a][0])
    print('=> player car %X' % player)

    series = [(t, cars[player]) for t, cars in frames if player in cars]

    # Bytes that differ between the player and every other car (flag hunt).
    others = [a for a in swing if a != player]
    if others:
        t, cars = series[len(series) // 2][0], frames[len(frames) // 2][1]
        pb = cars.get(player)
        if pb:
            diffs = []
            for off in range(0, bs):
                v = pb[off]
                ov = {cars[o][off] for o in others if o in cars}
                if ov and v not in ov and len(ov) == 1: diffs.append((off, v, ov.pop()))
            print('\nbytes where player differs from all other cars (same value among them): %d' % len(diffs))
            for off, v, o in diffs[:60]: print('  +0x%04x (icd %+#x): player %d others %d' % (off, off - ICD, v, o))

    print('\nraw gear byte (icd+0xAA0) per phase:')
    byph = defaultdict(set)
    for t, b in series: byph[ph(t)].add(b[ICD + 0xAA0])
    for k, v in byph.items(): print('  %-70s %s' % (k[:70], sorted(v)))
    print('  gear count', struct.unpack_from('<i', series[0][1], ICD + 0xABC)[0],
          'ratios', [round(f32(series[0][1], 0xAC0 + 12 * i), 3) for i in range(10)])

    # Per-wheel float fields: std-dev per phase.
    def std(xs):
        xs = [x for x in xs if math.isfinite(x)]
        if len(xs) < 3: return 0
        m = sum(xs) / len(xs); return math.sqrt(sum((x - m) ** 2 for x in xs) / len(xs))
    phases = [m[1] for m in marks if m[1] != 'end']
    print('\nper-wheel fields (wheel 0), std-dev by phase; columns:', ' | '.join(p[:12] for p in phases))
    for rel in range(0, STRIDE, 4):
        off = WHEEL0 + rel
        row = []
        for p in phases:
            xs = [f32(b, off) for t, b in series if ph(t) == p]
            row.append(std(xs))
        if max(row) > 1e-4 and max(row) < 1e6:
            w = [round(std([f32(b, off + k * STRIDE) for t, b in series]), 3) for k in range(4)]
            print('  wheel+0x%03x  %s   all-wheels std %s' % (rel, ' '.join('%9.3g' % r for r in row), w))


if __name__ == '__main__':
    main(sys.argv[1])
