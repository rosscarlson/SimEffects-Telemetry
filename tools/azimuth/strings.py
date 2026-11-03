import re, sys
data = open(sys.argv[1], 'rb').read()
pat = re.compile(sys.argv[2].encode(), re.I)
seen = set()
for m in re.finditer(rb'[\x20-\x7e]{5,}', data):
    s = m.group()
    if pat.search(s) and s not in seen:
        seen.add(s); print('A %08x %s' % (m.start(), s.decode()))
for m in re.finditer(rb'(?:[\x20-\x7e]\x00){5,}', data):
    s = m.group().decode('utf-16le').encode()
    if pat.search(s) and s not in seen:
        seen.add(s); print('W %08x %s' % (m.start(), s.decode()))
