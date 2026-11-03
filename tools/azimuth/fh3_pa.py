import numpy as np
d = np.load('fh3probe.npz'); T = d['t']; B = d['b']; S = 0x4B0
def f(o): return B[:, S+o:S+o+4].copy().view(np.float32)[:, 0]
def u8(o): return B[:, S+o]
W0, WS = 0x1750, 0x1490
def w(k, o): return f(W0 + k*WS + o)
pos = np.stack([f(0x13a0), f(0x13a4), f(0x13a8)], 1)
dt = np.diff(T); pspd = np.r_[0, np.linalg.norm(np.diff(pos, axis=0), axis=1) / dt]
marks = [(float(x.split('|')[0]), x.split('|')[1][:28]) for x in d['marks'] if 'new car' not in x]
edges = [m[0] for m in marks] + [1e9]
for i, (t0, name) in enumerate(marks[:-1]):
    s = (T >= t0) & (T < edges[i+1])
    if not s.any(): continue
    def r(x, fmt='%.2f'): return (fmt + '..' + fmt) % (np.min(x[s]), np.max(x[s]))
    gears = sorted(set(u8(0xaa8)[s].tolist()))
    print('%5.0f %-28s rpm %s gear %s thr %s brk %s hb %s clu %s steer %s spd %s posSpd %s' % (t0, name, r(f(0x148)*9.549, '%.0f'), gears,
        r(f(0x1298)), r(f(0x129c)), r(f(0x12a8)), r(f(0xa94)), r(f(8)/f(0x58)), r(f(0x12d4), '%.1f'), r(pspd, '%.1f')))
    print('      spin ' + ' | '.join(r(w(k, 0xc), '%.0f') for k in range(4)) + '   susp ' + ' | '.join(r(w(k, 0), '%.3f') for k in range(4)))
    print('      slipA ' + ' | '.join(r(w(k, 0x48)) for k in range(4)) + '   vl ' + ' | '.join(r(w(k, 0x2fc), '%.1f') for k in range(4)))
print('count', set(B[:, S+0xac4].tolist()), 'droop', [w(k,0x458)[0] for k in range(4)], 'bump', [w(k,0x460)[0] for k in range(4)], 'rest', [w(k,0x524)[0] for k in range(4)], 'rad', [w(k,0x414)[0] for k in range(4)])
# reverse: gear byte while spin negative
rev = np.mean([w(k, 0xc) for k in range(4)], 0) < -1
print('gear bytes while wheels spin backwards:', sorted(set(u8(0xaa8)[rev].tolist())), 'ratio table gear0:', f(0xac8)[0], f(0xadc)[0])
# steer sign vs slide: during full-lock phase, compare left/right wheel spin when steer extreme
s = (T > 63) & (T < 75) & (f(0x12d4) > 1)
st = (f(8)/f(0x58))[s]; sp = [w(k, 0xc)[s] for k in range(4)]
for sign in (-1, 1):
    m = st * sign > 0.5
    if m.any(): print('steer %+d: FL %.2f FR %.2f RL %.2f RR %.2f (spin/mean)' % (sign, *[np.mean(x[m]) / np.mean(np.mean(sp, 0)[m]) for x in sp]))
# braking: front vs rear susp
s = (T > 47) & (T < 52) & (f(0x129c) > 0.5)
print('hard braking susp change from rest:', [np.mean(w(k, 0)[s] - w(k, 0x524)[s]) for k in range(4)])
# slip spikes: compute slip ratio
for k in range(4):
    vx = w(k, 0x2fc); sr = (w(k, 0xc) * w(k, 0x414) - vx) / np.maximum(np.abs(vx), 2)
    big = np.abs(sr) > 3
    print('wheel %d: |slip ratio|>3 in %d frames; max %.1f; when big, susp-droop min %.3f' % (k, big.sum(), np.max(np.abs(sr)), np.min((w(k,0)-w(k,0x458))[big]) if big.any() else 0))
