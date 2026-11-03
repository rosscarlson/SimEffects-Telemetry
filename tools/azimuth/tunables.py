"""List the game's registered tuning variables: tunables.py <exe> [filter]

Forza registers debug tuning values by name with the address of a static
variable: lea r8,[var] ; mov rcx,rax ; lea rdx,[name] ; call <make> ... then
lea rdx,[group] ; call [reg+0x178]. Prints group/name, address, initial value.
"""
import sys, re, struct
sys.path.insert(0, r'C:\Users\ross\OneDrive\Code\FH2\tools')
import pe_an

img = pe_an.Img(sys.argv[1])
filt = re.compile(sys.argv[2] if len(sys.argv) > 2 else '.', re.I)
va, sz = img.sec('.text')
text = img.img[va:va + sz]
base = img.base + va
SEP = chr(92)

for m in re.finditer(rb'\x4c\x8d\x05(....)\x48\x8b\xc8\x48\x8d\x15(....)\xe8(....)', text, re.S):
    p = base + m.start()
    var = p + 7 + struct.unpack('<i', m.group(1))[0]
    name_a = p + 17 + struct.unpack('<i', m.group(2))[0]
    callee = p + 22 + struct.unpack('<i', m.group(3))[0]
    try:
        name = img.cstr(name_a)
    except Exception:
        continue
    tail = text[m.end(): m.end() + 120]
    g = re.search(rb'\x48\x8d\x15(....)', tail, re.S)
    grp = ''
    if g:
        ga = base + m.end() + g.start() + 7 + struct.unpack('<i', g.group(1))[0]
        try:
            grp = img.cstr(ga)
        except Exception:
            pass
    full = grp + SEP + name
    if not filt.search(full):
        continue
    try:
        val = struct.unpack('<f', img.rd(var, 4))[0]
    except Exception:
        val = float('nan')
    print('%-72s %x  init %-12.5g make %x' % (full, var, val, callee))
