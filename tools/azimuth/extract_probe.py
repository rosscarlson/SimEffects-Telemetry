"""Extract per-car series from a probe file into npz for fast analysis."""
import sys, numpy as np
sys.path.insert(0, '.')
from analyze_probe import load
bs, frames, marks = load(sys.argv[1])
player = int(sys.argv[2], 16)
ts, blocks = [], []
others = {}
for t, cars in frames:
    if player in cars:
        ts.append(t); blocks.append(np.frombuffer(cars[player], np.uint8))
    for a, b in cars.items():
        if a != player: others.setdefault(a, []).append((t, np.frombuffer(b, np.uint8)))
np.savez(sys.argv[3], t=np.array(ts), b=np.stack(blocks),
         marks=np.array([f'{t:.2f}|{m}' for t, m in marks]))
# other cars: keep blocks for those whose engine is active (rpm varies)
act = {}
for a, lst in others.items():
    arr = np.stack([x[1] for x in lst]); 
    rpm = arr[:, 0x3a0+0x148:0x3a0+0x14c].copy().view(np.float32)[:, 0]
    act[f'{a:X}'] = (np.array([x[0] for x in lst]), arr)
np.savez(sys.argv[3].replace('.npz', '_others.npz'), **{k + '_t': v[0] for k, v in act.items()}, **{k + '_b': v[1] for k, v in act.items()})
print(len(ts), 'player frames;', len(act), 'other cars')
