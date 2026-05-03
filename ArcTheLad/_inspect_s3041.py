import struct, sys, os

data = open(r'ArcTheLad/31/S3041.DAT','rb').read()
print('DAT size', hex(len(data)))
POINTER_BASE_FLOOR = 0x80000000
DRS = 0x28
found = None
for dc in (4,3):
    minsz = dc*DRS + dc*4
    for db in range(0, len(data)-minsz, 4):
        pao = db + dc*DRS
        ptrs = [struct.unpack_from('<I', data, pao+i*4)[0] for i in range(dc)]
        rb = ptrs[0]-db
        if rb < POINTER_BASE_FLOOR:
            continue
        ok = True
        for i in range(dc):
            if ptrs[i] != rb+db+i*DRS:
                ok = False
                break
        if not ok:
            continue
        # quick sanity: at least values[8] in [0..2] for first record
        v0 = struct.unpack_from('<I', data, db+8*4)[0]
        if v0 > 2:
            continue
        found = (dc, db, rb)
        break
    if found:
        break

if not found:
    print('no bank found')
    sys.exit(1)

dc, db, rb = found
print('FOUND dc=', dc, 'db=', hex(db), 'reloc=', hex(rb))
for i in range(dc):
    base = db+i*DRS
    v = [struct.unpack_from('<I', data, base+j*4)[0] for j in range(10)]
    print(f'  desc[{i}] @0x{base:X}:')
    for j, x in enumerate(v):
        print(f'    +0x{j*4:02X}: 0x{x:08X}  ({x})')
