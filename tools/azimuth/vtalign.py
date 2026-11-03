"""Align FH2's and FH3's ICarDynamics vtables by instruction shape."""
import re, difflib, sys
def load(p):
    out = []
    for line in open(p):
        m = re.match(r'\s*(\d+) (\w+) (\w+) \| (.*)', line.rstrip('\n'))
        out.append((int(m.group(1)), m.group(4)))
    return out
def norm(t):
    t = re.sub(r'0x[0-9a-f]+', 'N', t); t = re.sub(r'\b\d+\b', 'N', t)
    t = t.replace('(14+ insns) ', '').replace('(13+ insns) ', '').replace('(11+ insns) ', '')
    return t
a = load('icardyn_vtable.txt'); b = load('fh3_icardyn_vtable.txt')
na = [norm(t) for _, t in a]; nb = [norm(t) for _, t in b]
sm = difflib.SequenceMatcher(None, na, nb, autojunk=False)
m = {}
for tag, i1, i2, j1, j2 in sm.get_opcodes():
    if tag == 'equal' or (tag == 'replace' and i2 - i1 == j2 - j1):
        for k in range(i2 - i1): m[i1 + k] = (j1 + k, tag)
want = [int(x) for x in sys.argv[1:]]
for i in want:
    j = m.get(i)
    print('FH2 %3d: %s' % (i, a[i][1][:150]))
    print('   FH3 %s: %s' % (j, b[j[0]][1][:150] if j else '-'))
