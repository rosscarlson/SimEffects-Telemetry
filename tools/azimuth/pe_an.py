"""Static-analysis helpers for forza_durango_release_final.exe (read-only)."""
import re, struct, sys, pefile, capstone

EXE = r"G:\ROMs\Forza Horizon 2\Mount\forza_durango_release_final.exe"

class Img:
    def __init__(self, path=EXE):
        self.pe = pefile.PE(path, fast_load=True)
        self.base = self.pe.OPTIONAL_HEADER.ImageBase
        self.img = bytes(self.pe.get_memory_mapped_image())
        self.secs = [(s.Name.rstrip(b'\0').decode(), s.VirtualAddress, s.Misc_VirtualSize) for s in self.pe.sections]
        self.md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
        self.md.detail = True
    def sec(self, name):
        for n, va, sz in self.secs:
            if n == name: return va, sz
    def rd(self, va, n): return self.img[va - self.base: va - self.base + n]
    def q(self, va): return struct.unpack('<Q', self.rd(va, 8))[0]
    def d(self, va): return struct.unpack('<I', self.rd(va, 4))[0]
    def cstr(self, va):
        b = self.rd(va, 256); return b[:b.find(b'\0')].decode('latin1')
    def find_str(self, s):
        """VAs of exact NUL-terminated ascii string."""
        out = []
        for m in re.finditer(re.escape(s.encode()) + b'\0', self.img):
            if m.start() == 0 or self.img[m.start()-1] == 0: out.append(self.base + m.start())
        return out
    def rip_xrefs(self, target, secname='.text'):
        """Instructions whose RIP-relative disp32 resolves to target (fast byte scan + verify)."""
        va, sz = self.sec(secname); start = va; text = self.img[va:va+sz]
        res = []
        # disp32 at position p, instruction end = p+4+k (k = imm bytes after disp, 0/1/4)
        for k in (0, 1, 4):
            for p in range(0, len(text) - 4):
                pass
        return res
    def xrefs(self, target, secname='.text'):
        va, sz = self.sec(secname); text = self.img[va:va+sz]
        res = set()
        tgt_rel = target - self.base
        # brute: for each possible disp position p, end e = p+4+k
        import array
        for k in (0, 1, 4):
            # disp = tgt_rel - (va + p + 4 + k)  -> scan for each p is O(n); use numpy
            pass
        return res
    def dis(self, va, n=200, stop_ret=True):
        out = []
        for ins in self.md.disasm(self.rd(va, n*8), va):
            out.append(ins)
            if len(out) >= n or (stop_ret and ins.mnemonic in ('ret', 'int3') and len(out) > 1): break
        return out

def xrefs_np(img, target):
    """Find all RIP-relative references to target in .text using numpy."""
    import numpy as np
    va, sz = img.sec('.text')
    buf = np.frombuffer(img.img[va:va+sz], dtype=np.uint8)
    n = len(buf) - 8
    d = (buf[0:n].astype(np.int64) | (buf[1:n+1].astype(np.int64) << 8) |
         (buf[2:n+2].astype(np.int64) << 16) | (buf[3:n+3].astype(np.int64) << 24))
    d = np.where(d >= 2**31, d - 2**32, d)
    pos = np.arange(n, dtype=np.int64) + va
    hits = set()
    tr = target - img.base
    for k in (0, 1, 2, 4):
        idx = np.nonzero(pos + 4 + k + d == tr)[0]
        for i in idx:
            dispva = img.base + va + int(i)
            # verify by disassembling a few starting points before disp
            for back in range(1, 8):
                s = dispva - back
                for ins in img.md.disasm(img.rd(s, 16), s):
                    if ins.address + ins.size == dispva + 4 + k:
                        for op in ins.operands:
                            if op.type == capstone.x86.X86_OP_MEM and op.mem.base == capstone.x86.X86_REG_RIP and ins.address + ins.size + op.mem.disp == target:
                                hits.add(ins.address)
                    break
    return sorted(hits)

def fmt(ins): return '%x: %-8s %s' % (ins.address, ins.mnemonic, ins.op_str)

def getter_offset(img, name):
    """For a reflected property name, return (getter_va, [r8+off] offsets read)."""
    res = []
    for sva in img.find_str(name):
        for x in xrefs_np(img, sva):
            win = list(img.md.disasm(img.rd(x - 0x40, 0x40), x - 0x40))
            g = None
            for ins in win:
                if ins.mnemonic == 'lea' and 'rip' in ins.op_str:
                    t = ins.address + ins.size + ins.operands[1].mem.disp
                    tv, ts = img.sec('.text')
                    if img.base + tv <= t < img.base + tv + ts: g = t
            if g:
                offs = []
                for ins in img.dis(g, 30):
                    for op in ins.operands:
                        if op.type == capstone.x86.X86_OP_MEM and op.mem.base == capstone.x86.X86_REG_R8:
                            offs.append(hex(op.mem.disp))
                res.append((hex(g), offs))
    return res

def vtables_for(img, cls):
    """Find vtable VAs for an RTTI class name like '.?AVFoo@@'."""
    rv, rs = img.sec('.rdata'); dv, ds = img.sec('.data')
    out = []
    for nva in img.find_str(cls):
        td = nva - 0x10; td_rva = td - img.base
        blob = img.img[rv:rv+rs]
        for m in re.finditer(re.escape(struct.pack('<I', td_rva)), blob):
            col = img.base + rv + m.start() - 0xC
            if img.d(col) != 1: continue
            colb = struct.pack('<Q', col)
            for m2 in re.finditer(re.escape(colb), img.img):
                out.append((img.base + m2.start() + 8, img.d(col + 4)))  # (vtable, offset in complete obj)
    return out

def pdata(img):
    if not hasattr(img, '_pd'):
        va, sz = img.sec('.pdata')
        import numpy as np
        a = np.frombuffer(img.img[va:va+sz - sz % 12], dtype='<u4').reshape(-1, 3)
        img._pd = a
    return img._pd

def func_of(img, addr):
    import numpy as np
    a = pdata(img); r = addr - img.base
    i = np.searchsorted(a[:, 0], r, side='right') - 1
    if i >= 0 and a[i, 0] <= r < a[i, 1]: return img.base + int(a[i, 0]), img.base + int(a[i, 1])
    return None

def funcs_with_disps(img, disps, window=0x400):
    """Functions containing all given disp32 constants (as raw bytes)."""
    va, sz = img.sec('.text'); text = img.img[va:va+sz]
    sets = []
    for d in disps:
        pat = re.escape(struct.pack('<I', d))
        sets.append([img.base + va + m.start() for m in re.finditer(pat, text)])
    import bisect
    cands = set()
    first = sets[0]
    for p in first:
        ok = True
        for s in sets[1:]:
            j = bisect.bisect_left(s, p - window)
            if j >= len(s) or s[j] > p + window: ok = False; break
        if ok:
            f = func_of(img, p)
            if f: cands.add(f)
    return sorted(cands)

def all_vtables(img):
    """[(vtable_va, class_name, nentries)] for every RTTI-described vtable."""
    import numpy as np
    if hasattr(img, '_vts'): return img._vts
    tv, ts = img.sec('.text'); lo, hi = img.base + tv, img.base + tv + ts
    arr = np.frombuffer(img.img[:len(img.img) - len(img.img) % 8], dtype='<u8')
    out = []
    # candidate COL pointers: qwords in image pointing into .rdata
    rv, rs = img.sec('.rdata')
    cand = np.nonzero((arr >= img.base + rv) & (arr < img.base + rv + rs))[0]
    for idx in cand:
        col = int(arr[idx])
        try:
            if img.d(col) != 1: continue
            td = img.base + img.d(col + 12)
            name = img.cstr(td + 0x10)
        except Exception: continue
        if not name.startswith('.?A'): continue
        vt = idx * 8 + 8 + img.base
        n = 0
        while n < 4000:
            f = int(arr[idx + 1 + n]) if idx + 1 + n < len(arr) else 0
            if not (lo <= f < hi): break
            n += 1
        if n: out.append((vt, name, n, img.d(col + 4)))
    img._vts = out
    return out

def prop_getters(img, name):
    """getter VAs registered next to a property-name string."""
    out = []
    tv, ts = img.sec('.text')
    for sva in img.find_str(name):
        for x in xrefs_np(img, sva):
            g = None
            for ins in img.md.disasm(img.rd(x - 0x40, 0x40), x - 0x40):
                if ins.mnemonic == 'lea' and 'rip' in ins.op_str:
                    t = ins.address + ins.size + ins.operands[1].mem.disp
                    if img.base + tv <= t < img.base + tv + ts: g = t
            if g and g not in out: out.append(g)
    return out

def callers_of_vfunc(img, off, need=()):
    """Functions containing 'call qword ptr [reg + off]' for off and all offsets in need."""
    return funcs_with_disps(img, [off] + list(need), window=0x800)
