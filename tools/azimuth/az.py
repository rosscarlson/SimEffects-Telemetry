"""Read-only static-analysis helpers for azimuth.exe."""
import re, struct, sys, pefile, capstone, numpy as np
EXE = r"G:\Games\Forza Horizon 2\azimuth.exe"
pe = pefile.PE(EXE, fast_load=True)
BASE = pe.OPTIONAL_HEADER.ImageBase
IMG = bytes(pe.get_memory_mapped_image())
TEXT = next(s for s in pe.sections if s.Name.startswith(b'.text'))
TVA, TSZ = TEXT.VirtualAddress, TEXT.Misc_VirtualSize
md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64); md.detail = False
def off2va(off): return BASE + pe.get_rva_from_offset(off)
def rd(va, n): return IMG[va-BASE: va-BASE+n]
def xrefs(target):
    """Instruction addresses whose rip-relative disp32 hits target (verified by disassembly)."""
    t = np.frombuffer(IMG[TVA:TVA+TSZ], dtype=np.uint8)
    d = (t[:-3].astype(np.int64) | t[1:-2].astype(np.int64) << 8 | t[2:-1].astype(np.int64) << 16 | t[3:].astype(np.int64) << 24)
    d = np.where(d >= 2**31, d - 2**32, d)
    pos = np.arange(len(d)) + TVA + BASE
    out = set()
    for k in (0, 1, 2, 4):
        for p in np.nonzero(pos + 4 + k + d == target)[0]:
            p = int(p)
            for back in range(1, 8):
                start = pos[p] - back
                ins = next(md.disasm(rd(int(start), 16), int(start)), None)
                if ins and ins.address + ins.size == pos[p] + 4 + k and ('rip' in ins.op_str):
                    out.add(ins.address); break
    return sorted(out)
def dis(va, n=40):
    for i, ins in enumerate(md.disasm(rd(va, n*10), va)):
        if i >= n: break
        print(f"{ins.address:x}  {ins.mnemonic:6} {ins.op_str}")
def func_start(va):
    """Walk back to the previous int3/nop padding."""
    p = va
    while p > BASE+TVA and not (rd(p-1,1) in (b'\xcc', b'\xc3') and rd(p-2,1) in (b'\xcc', b'\xc3', b'\x90')): p -= 1
    return p
