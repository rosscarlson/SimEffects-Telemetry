"""Dump a vtable's entries with their first instructions: vtdump.py <exe> <vtable_va> <count> <out>"""
import sys; sys.path.insert(0, r'C:\Users\ross\OneDrive\Code\FH2\tools')
import pe_an
exe, vt, n, out = sys.argv[1], int(sys.argv[2], 16), int(sys.argv[3]), sys.argv[4]
img = pe_an.Img(exe)
with open(out, 'w') as f:
    for i in range(n):
        fn = img.q(vt + i * 8)
        ins = img.dis(fn, 14)
        txt = ' ; '.join('%s %s' % (x.mnemonic, x.op_str) for x in ins)
        pre = '' if len(ins) < 14 else '(14+ insns) '
        f.write('%3d %03x %x | %s%s\n' % (i, i * 8, fn, pre, txt if len(ins) < 14 else ' ; '.join('%s %s' % (x.mnemonic, x.op_str) for x in ins[:5])))
