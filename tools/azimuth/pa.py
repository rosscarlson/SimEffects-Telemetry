import numpy as np
d = np.load('probe1.npz'); T = d['t']; B = d['b']
ICD = 0x3a0
def f(off): return B[:, ICD+off:ICD+off+4].copy().view(np.float32)[:, 0]
def i32(off): return B[:, ICD+off:ICD+off+4].copy().view(np.int32)[:, 0]
def u8(off): return B[:, ICD+off]
pos = np.stack([f(0xef0), f(0xef4), f(0xef8)], 1)
dt = np.diff(T); spd = np.r_[0, np.linalg.norm(np.diff(pos, axis=0), axis=1) / dt]
marks = [(float(x.split('|')[0]), x.split('|')[1]) for x in d['marks'] if 'new car' not in x]
