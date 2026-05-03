# Alpha Storm (DOS) Notes

## LIF files

The LIF files hold monster/enemy sprites. This was traced from `FUN_00023e6f` in the stripped `ALF.LE` binary:

- `FUN_00023e6f` is not the decoder. It is an on-demand cache loader for monster resources. It maps the current entity's graphics id through `fix_off32_0009bf08`, loads a filename from the monster LIF filename table at `fix_off32_0009ba70`, calls `FUN_000113a5`, and stores the returned file buffer in `DAT_00062a90[index]`.
- `FUN_000113a5` is a thin wrapper over `FUN_000113b4`, returning the loaded file buffer from `DAT_00062814`.
- `FUN_00015bea` and `FUN_00028690` are the interesting consumers of `DAT_00062a90`. `FUN_00015bea` reads the first two dwords as sprite canvas dimensions for bounds/scaling. `FUN_00028690` selects a frame offset from the table and decodes scanline RLE into the render buffer.

Current LIF structure:

```text
0x00  uint32  sprite canvas width
0x04  uint32  sprite canvas height
0x08  uint32  frame count
0x0C  uint32[frame count] frame offsets, relative to 0x0C
...   frame data
```

For `Samples/P_PIR.LIF`, the header is `width=135`, `height=114`, `frame count=104`. The first relative frame offset is `0x1A0`, which is exactly `104 * 4`; the first absolute frame offset is therefore `0x0C + 0x1A0 = 0x1AC`.

Frame data is stored as height scanlines. Each scanline starts with a one-byte row length, and that length includes the length byte itself. The renderer advances to the next encoded source row with `row += row[0]`.

Each row's command stream begins at `row + 1` and decodes to exactly the file header width:

```text
signed byte command

command == 0:
	next byte is transparent skip count

command > 0:
	next byte is a palette index repeated command + 1 times

command < 0:
	next -command bytes are literal palette indices
```

The first row of `P_PIR.LIF` frame 0 begins `03 00 87`: row length 3, transparent skip of 0x87/135 pixels, matching the file width. A validation pass using this codec decoded all 104 frames in `P_PIR.LIF` with no row-width mismatches.

DUM_SINE.BIN contains a candidate 256-color RGB888 palette at offset `0x4034`. The block is 768 bytes: three bytes per palette index in red, green, blue order. This is not yet traced from the executable, but it matches the palette screenshot and produces correctly colored `P_PIR.LIF` output.

Extractor status:

```text
cd DOS/Alpha Storm/Extractor
dotnet run -- ../Samples/P_PIR.LIF
```

The extractor auto-detects `DUM_SINE.BIN` beside the input LIF and applies the palette at `0x4034`. For other palette sources, use `--palette <file>` and optionally `--palette-offset <offset>`.

## WAC files

Two different WAC layouts have been confirmed so far.

### MAPSIDES.WAC screen bank

- `MAPSIDES.WAC` is a top-level offset table. The first dword is `0x0C`, so the sample has 3 entries.
- Each entry is a custom block-coded full-screen image stream. The consumer-side decode path is reached from `FUN_0001f22b`, which calls `FUN_00028dac`.
- `FUN_00028dac` reads a repeated block grammar: a block header, three symbol tables, and a top-level symbol stream. `FUN_00028efb`, `FUN_00028eb9`, and `FUN_00028ee3` expand that grammar into bytes.
- For `Samples/MAPSIDES.WAC`, all three entries decode to exactly `307200` bytes, which is `640 * 480`.
- Each decoded entry leaves a tiny trailer (`1-2` bytes in the observed samples) after the compressed stream.

### TURNIES.WAC sprite-set bank

- `TURNIES.WAC` is also a top-level offset table, but its entries do not use the `FUN_00028dac` block codec.
- The file starts with `0x40`, so it contains 16 entries.
- Every top-level entry parses directly as a nested LIF sprite set:

```text
0x00  uint32 width
0x04  uint32 height
0x08  uint32 frame count   (64 in TURNIES.WAC)
0x0C  uint32[frame count] relative frame offsets
...   LIF frame data
```

- Observed `TURNIES.WAC` entry sizes vary, e.g. `53x21`, `64x30`, `78x37`, `107x64`, but all 16 entries contain 64 frames.

Extractor status:

```text
cd DOS/Alpha Storm/Extractor
dotnet run -- ../Samples/MAPSIDES.WAC
dotnet run -- ../Samples/TURNIES.WAC
```

The extractor exports `MAPSIDES.WAC` as full-screen images and `TURNIES.WAC` as nested sprite sets automatically.

## PAC files

- PAC screen files are standard IFF PBM containers, not a custom Alpha Storm wrapper.
- `Samples/VIC1.PAC` starts with `FORM ... PBM ` and contains these key chunks:

```text
BMHD  bitmap header, width=640, height=480, planes=8, masking=0, compression=1
CMAP  256-color palette, 768 bytes
BODY  ByteRun1-compressed 8-bit pixel data
```

- The BODY chunk for `VIC1.PAC` expands with standard PBM `ByteRun1` rules to exactly `640 * 480` bytes.
- PAC files embed their own palette via `CMAP`, so they do not require `DUM_SINE.BIN` unless the palette is explicitly overridden.

Extractor status:

```text
cd DOS/Alpha Storm/Extractor
dotnet run -- ../Samples/VIC1.PAC
```

## TEX files

- TEX filenames are built during startup in `FUN_00010040`, which constructs `TEXAS\*.TEX` paths such as `BASETEX.TEX`, `PBASETEX.TEX`, `VBASETEX.TEX`, and `TEMPTEX.TEX`.
- `FUN_00016731`, the world texture consumer, clamps the texture index to `0x45` and computes the base pointer as `*DAT_00062a34 + index * 0x4000`.
- The same consumer masks texture coordinates with `0x3fff` and steps in `0x80`-byte row increments, so the live TEX bank is addressed as `128 x 128` textures in memory.
- `Samples/BASETEX.TEX` is `0x118000` bytes long, which is exactly `70 * 128 * 128`.
- The sample has no header. It partitions cleanly into 70 fixed-size blocks of `128 * 128 = 16384` bytes with no remainder.
- The earlier `128x112` hypothesis was false because it matched the file size but not the actual consumer. It produced 80 outputs and caused texture bleed between images.
- The current TEX interpretation is therefore:

```text
raw bank of 70 textures
texture size: 128x128
pixel format: 8-bit indexed
storage: row-major, one byte per pixel, no per-texture header
```

- `BASETEX.TEX` uses broad 8-bit palette ranges, so the current extractor treats TEX as ordinary indexed images and applies the same `DUM_SINE.BIN` palette auto-detection used by the other non-PAC formats.

Extractor status:

```text
cd DOS/Alpha Storm/Extractor
dotnet run -- ../Samples/BASETEX.TEX
```

This TEX interpretation is now consumer-backed: the engine-side bank math is consistent with 70 `128x128` textures, which matches the sample length exactly.
