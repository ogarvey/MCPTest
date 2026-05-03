import os, struct, sys

ROOT = os.path.dirname(os.path.abspath(__file__))

def inspect(path):
    with open(path, 'rb') as f:
        d = f.read()
    print(f"\n=== {os.path.relpath(path, ROOT)} ({len(d):#x} bytes) ===")
    print('  first 32 bytes:', d[:32].hex())
    print('  ascii head    :', d[:16])
    for sig in (b'pBAV', b'VABp', b'pQES', b'SEQp'):
        i = d.find(sig)
        if 0 <= i < 0x40000:
            print(f'  sig {sig} at {i:#x}')

for folder in ('E1', 'E2', 'E3', 'E4', 'E5'):
    for name in sorted(os.listdir(os.path.join(ROOT, folder))):
        if not name.upper().endswith(('.IMG', '.DAT')):
            continue
        full = os.path.join(ROOT, folder, name)
        if os.path.isfile(full):
            inspect(full)
            # only inspect first DAT per folder + the IMG
            if name.upper().endswith('.DAT'):
                # show one DAT then break
                pass
    print('---')
