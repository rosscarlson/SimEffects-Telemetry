import re, sys
data = open(sys.argv[1], 'rb').read()
off = int(sys.argv[2], 16); span = int(sys.argv[3], 16)
for m in re.finditer(rb'[\x20-\x7e]{3,}', data[off-span:off+span]):
    print('%08x %s' % (off-span+m.start(), m.group().decode()))
