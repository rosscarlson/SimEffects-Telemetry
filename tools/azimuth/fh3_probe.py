"""FH3 probe -> player series npz + phase summary. fh3_probe.py <probe.bin> <out.npz>"""
import sys, numpy as np
sys.path.insert(0, r'C:\Users\ross\OneDrive\Code\FH2\tools')
from analyze_probe import load
bs, frames, marks = load(sys.argv[1])
SUB = 0x4B0
def fv(b, o): return np.frombuffer(b, np.float32, 1, SUB + o)[0]
def iv(b, o): return np.frombuffer(b, np.int32, 1, SUB + o)[0]
# player = not AI-controlled and most RPM variation
stats = {}
for t, cars in frames:
    for a, b in cars.items():
        s = stats.setdefault(a, [0, 0, []]); s[0] += 1; s[1] += iv(b, 0x1534) == 0; s[2].append(fv(b, 0x148))
for a, (n, notai, r) in sorted(stats.items(), key=lambda x: -x[1][0])[:8]:
    print('%X frames %d notAI %d rpm std %.1f' % (a, n, notai, np.std(r) * 9.55))
player = max((a for a in stats if stats[a][1] > stats[a][0] * 0.9), key=lambda a: stats[a][0])
print('player %X' % player)
ts, bl = [], []
for t, cars in frames:
    if player in cars: ts.append(t); bl.append(np.frombuffer(cars[player], np.uint8))
np.savez(sys.argv[2], t=np.array(ts), b=np.stack(bl), marks=np.array(['%.2f|%s' % m for m in marks]))
print(len(ts), 'frames; marks:'); [print('  %.1f %s' % m) for m in marks if 'new car' not in m[1]]
