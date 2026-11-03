import re, sys
sys.argv = ['x']; exec(open('vtalign.py').read().split('want =')[0])
def offs(t): return [int(x, 16) for x in re.findall(r'\+ (0x[0-9a-f]+)\]', t)]
for i, (_, t) in enumerate(a):
    j = m.get(i)
    ta = t; tb = b[j[0]][1] if j else ''
    oa, ob = offs(ta), offs(tb)
    if 'imul' in ta and '0x4b0' in ta:
        ra = [hex(o - 0x11B0) for o in oa if 0x11B0 <= o < 0x11B0 + 0x4B0]
        rb = [hex(o - 0x1750) for o in ob if 0x1750 <= o < 0x1750 + 0x1490]
        print('%3d -> %-4s %-30s %-30s %s' % (i, j[0] if j else '-', ra, rb, ta[:90]))
    elif any(o in (0xA24, 0x170) for o in oa):
        print('BOOST %3d -> %s | %s | %s' % (i, j, ta[:120], tb[:120]))
