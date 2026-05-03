"""Inspect the post-audio region of every S30xx IMG to find a self-describing header.

Per-file VAB audio_end values were derived from the Sony VAB header
(VAG offset table sum, body starts at fixed 0x6000):
"""
import struct
from pathlib import Path

ROOT = Path(r"c:\Dev\Reverse Engineering\MCPTest\ArcTheLad\31")
files = sorted(ROOT.glob("S30*.IMG"))


def parse_vab_audio_end(data: bytes) -> int:
    if len(data) < 0x20 or data[:3] != b'pBA' and data[:3] != b'VAB':
        return -1
    version = struct.unpack_from('<I', data, 4)[0]
    program_count = struct.unpack_from('<H', data, 0x12)[0]
    vag_count = data[0x16]
    table_off = 0x20 + 0x80 * 0x10 + program_count * 0x200
    entries = vag_count + 1
    sizes = struct.unpack_from(f'<{entries}H', data, table_off)
    body = sum(s << 3 for s in sizes) if version > 4 else sum(s << 2 for s in sizes)
    return 0x6000 + body


for img in files:
    data = img.read_bytes()
    audio_end = parse_vab_audio_end(data)
    file_size = len(data)
    print(f"\n=== {img.name} size=0x{file_size:X} audio_end=0x{audio_end:X} ===")

    # Find first nonzero byte at/after audio_end
    fnz = audio_end
    while fnz < file_size and data[fnz] == 0:
        fnz += 1
    print(f"first_nonzero_after_audio = 0x{fnz:X} (gap=0x{fnz - audio_end:X})")

    # Dump first 64 bytes at audio_end (might be a header) and at fnz (graphics start)
    print(f"@audio_end[0..64]: {data[audio_end:audio_end+64].hex()}")
    print(f"@fnz[0..128]: {data[fnz:fnz+128].hex()}")

    # Also check IMG-end-aligned headers: many archives put a TOC at end
    print(f"@end-64: {data[-64:].hex()}")

    # Look for word values at known boundaries that look like offsets/sizes
    # Show u32 values right at fnz
    u32s = struct.unpack_from('<8I', data, fnz)
    print(f"@fnz u32: {[hex(v) for v in u32s]}")
