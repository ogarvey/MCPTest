# JoJo (SLES_025.99) — Reverse-Engineering Notes

This file captures **confirmed facts only**. Anything assumed is marked `(assumption)`
and must be verified before we rely on it in code.

---

## 1. Disc layout (verified by listing the extracted ISO)

ISO path: `C:\Dev\Gaming\Sony\PSX\Games\JoJo's Bizarre Adventure (Europe)`

| Folder | Contents (verified) |
|--------|---------------------|
| `C/`   | `*.FIN` files and `*.CLT` files. Each `.CLT` is exactly 0x200 bytes. |
| `M/`   | `*.BIN` files. Many groups: `AC_*`, `ED*`, `EM*`, `PL*` etc. |
| `P/`   | `*.PAC` container files. |
| `X/`   | `*.XA` audio (CD-XA streams). |
| root   | `SLES_025.99` (executable), `SYSTEM.CNF`, `ZNULL.DAT`. |

The PSX executable (`SLES_025.99`) is what we're analysing in Ghidra.

---

## 2. PAC container format (verified)

Header (little-endian):

| Offset | Type   | Meaning                                                   |
|--------|--------|-----------------------------------------------------------|
| 0x00   | u32    | `entry_count`                                             |
| 0x04   | u32    | `total_size` (= total file length on disc, in bytes)      |

Followed immediately by `entry_count` directory records, each 8 bytes:

| Offset | Type   | Meaning                                                   |
|--------|--------|-----------------------------------------------------------|
| 0x00   | u32    | `flags` — low 16 bits are a loader VM opcode              |
| 0x04   | u32    | `data_length` — payload size in bytes                     |

Payload placement rules (verified against `MTIT.PAC`, `OP_COL.PAC`, `PLK00.PAC`,
`PSEL.PAC`, `NOW_00.PAC`):

- First payload starts at file offset `0x800`.
- Each subsequent payload starts at the next `0x800`-aligned offset after the
  previous payload ends. This matches PSX CD sector alignment.
- `total_size` always equals the aligned end of the last payload.

The C# parser implementing this lives at
`JojoExtractor/Pac/PacFile.cs` and was confirmed against `MTIT.PAC`
(3 entries, ends at `0xA000`).

### 2.1 PAC header records are loader VM records (code-backed)

The file-ID streaming path proves that the 8-byte PAC entry records are not
just a container directory. `FUN_80018470` seeds the loader VM from the first
sector of a streamed file by pointing `state+0x4c` at file offset `0x08`; then
`FUN_800184c0` reads repeated records as:

```c
opcode = *(uint16_t *)(state->recordPtr + 0);
length = *(uint32_t *)(state->recordPtr + 4);
state->recordPtr += 8;
```

This matches the PAC entry table exactly: the low 16 bits of `flags` are the
VM opcode, and `data_length` is the per-record transfer/stride length. Payloads
still begin at file offset `0x800` and advance by each length rounded up to a
0x800-byte sector boundary, matching the existing PAC parser.

Concrete validations:

| File | Header/VM records |
|------|-------------------|
| `P/KPLN00.PAC` | `0x0202/0x20000`, then `0x0800..0x0807` entries with lengths `0x74b4`, `0x58c54`, `0xd2d0`, `0x200`, `0x40`, `0x280`, `0x280`, `0x3c0`. |
| `P/PLK00.PAC` | `0x0810/0x1af0`, `0x0811/0x36a8`. |
| `P/PSJ_000.PAC` | `0x0201/0x18000`, `0x0101/0x1740`, `0x0102/0x0c00`, `0x0103/0x1740`. |

This is the first code-backed bridge from PAC entries into runtime behavior.
Future parser work should interpret entry `flags` through `FUN_800184c0`'s
opcode classes rather than through filename adjacency or visual palette
matching.

### Observed `flags` values (verified by running the extractor)

Confirmed entry directories from `info`:

| File         | Entries (flags / length)                                                |
|--------------|--------------------------------------------------------------------------|
| `OP_COL.PAC` | 0x0803/0x200, 0x0804/0x20, 0x0805/0x200, 0x0806/0x200, 0x0807/0x280, 0x0817/0x240, 0x0818/0x200 |
| `MTIT.PAC`   | 0x0201/0x8000, 0x0101/0x80, 0x0102/0x1000                                |
| `PLK00.PAC`  | 0x0810/0x1AF0, 0x0811/0x36A8                                             |
| `KPLN00.PAC` | 0x0202/0x20000, 0x0800/0x74B4, 0x0801/0x58C54, 0x0802/0xD2D0, 0x0803/0x200, 0x0804/0x40, 0x0805/0x280, 0x0806/0x280, 0x0807/0x3C0 |
| `POKE0.PAC`  | 0x0122/0x1C506                                                           |
| `NOW_00.PAC` | 0x0122/0xAF6                                                             |
| `PSJ_000.PAC`| 0x0201/0x18000, 0x0101/0x1740, 0x0102/0xC00, 0x0103/0x1740               |
| `PSEL.PAC`   | 0x0204/0x17300, 0x0800/0x9DE, 0x0801/0x3790D, 0x0802/0x4D68, 0x0803/0x80, 0x0808/0x2DB65, 0x0809/0x2E70, 0x0814/0xBD90, 0x0815/0x163C, 0x0816/0x60, 0x0201/0x10000, 0x020F/0x10000, 0x0804/0x660, 0x0102/0x1000 |

#### Opcode classes (partly code-backed, exact formats still being decoded):

The 32-bit `flags` field is always of the form `0x0000_HHLL`. The upper byte
of the lower 16 bits (`HH`) groups the entries into clear categories:

| `HH`   | seen sizes                              | current interpretation                 |
|--------|-----------------------------------------|----------------------------------------|
| `0x01` | 0x80, 0x1000, 0x1740, 0xC00             | pointer-table/RAM-destination class in `FUN_800184c0`; exact content varies |
| `0x02` | 0x8000, 0x10000, 0x18000, 0x17300       | direct streamed VRAM image class       |
| `0x08` | 0x20, 0x60, 0x200, 0x240, 0x280, ...    | image/CLUT runtime pool class; content selected later by consumers |
| `0x01` w/ `LL`=`0x22`/`0x23` | 0x934, 0x28468, 0x1C506 | `FUN_800267a8`-compressed TIM payloads consumed by `FUN_80025e64` |

**Verified concrete fact:** several `0x08xx` entries in `OP_COL.PAC` and the
`0x0101` entry in `MTIT.PAC` are exact multiples of 32 bytes. 32 bytes = 16
16-bit colors = one PSX 4bpp CLUT bank. So those entries can be decoded as
N-bank PSX BGR-15 palettes without further reverse-engineering.

---

## 3. Ghidra functions inspected

Working in Ghidra project `ami`, file `SLES_025.99`. PSX MIPS R3000.

### 3.1 Asset-loading wrappers (decompiled)

- `FUN_8001a1d8` — sequentially loads `char/obj/pxl/sup.cpx`,
  `char/obj/map/sup.cmp`, `char/obj/pxl/vs.cpx`, `char/obj/map/vs.cmp`,
  `char/obj/color/vs.clt`, then calls `LoadImage`/`FUN_80027454` to push
  pixels into VRAM at `(x=0, y=499, w=0x180, h=1)`.
- `FUN_8001a338` — loads `kabe_01.pix`, `kabe_10.pix`, `kabe_10.clt`,
  `kabe_01.map`. Uses a loop to upload `kabe_10.pix` to VRAM in 0x40-wide,
  0x100-tall stripes starting at `x = 0x198`.
- `FUN_8001bc04`, `FUN_8001bad4`, `FUN_8001bd34` — set up DAT_8008a26x /
  DAT_8008a28x globals that look like a small struct of pointers
  (e.g. `DAT_8008a280 = &DAT_80115800`) and then `LoadImage` to VRAM.
- `FUN_8001da50` — same shape as 8001a338 (stripe upload at `x=0x198+`,
  using a 0x40-wide rect) for general "pix" assets.

These functions all call **`FUN_80012d28(buffer, "filename")`** to load files.

### 3.2 The mystery: `FUN_80012d28` — RESOLVED enough to move on

Disassembly of the function as Ghidra sees it, verified by direct
`memory_read`:

```
80012d20: jr ra ; nop      <-- separate stub #1
80012d28: jr ra ; nop      <-- separate stub #2 (this is the one wrappers call)
80012d30: addiu sp,sp,-... <-- start of next real function (memcard init)
```

So `FUN_80012d28` really is *just* two MIPS instructions in the static image.
However, every caller's disassembly does:

```
jal 0x80012d28        ; call
sw  s0, 0x10(sp)      ; (delay slot, unrelated)
...
move s0, v0           ; the caller IS reading v0 as a length
```

**Verified facts:**

- Pre-call register state at, e.g. `8001a200`: `a0 = 0x80183800` (the buffer),
  `a1 = 0x800549f0` (= the literal string `"char/obj/pxl/sup.cpx"`).
- The caller treats `v0` as a length (uses it to advance a write cursor).
- The static body cannot produce a meaningful `v0`.

**Conclusion:** the body at `0x80012d28` is patched at runtime — either by
copying overlay code into RAM or by setting a function pointer somewhere that
trampolines to the real loader. Since `FUN_800127bc` (the function before
`0x80012d20`) is just a hex/text formatter and not the loader, the real
loader is somewhere else and we have not yet located the boot code that
installs it.

**Correction:** this loader path still matters for reconstructing the whole
graphics pipeline. For manual PAC experiments we can parse the container bytes
directly, but deterministic extraction must account for every path that can
feed renderer tables or VRAM: virtual-name loads (`.cpx/.cmp/.clt/.pix/.map`),
file-ID streaming loads, direct VRAM uploads, RAM metadata loads, and runtime
image-pool indirections. Do not narrow the analysis to only `.BIN` or only
`.PAC` files.

> **TODO:** identify the runtime-patched implementation or dispatch target for
> `FUN_80012d28` if the virtual path to disc-file mapping becomes necessary.
> The static call sites are still useful because they show how loaded bytes are
> assigned to runtime graphics tables.

### 3.3 `LoadImage` and `FUN_80027454`

- `LoadImage` is the PSX BIOS/libgs primitive that DMAs a buffer to VRAM
  given a `RECT { short x, y, w, h }`.
- `FUN_80027454(mode, RECT*, src)` — copies the same source into one or both
  of two work buffers at base addresses `DAT_80067130` and `DAT_8006d7a0`,
  laid out as a 2D buffer with stride `0x300` bytes per row (= 0x180 shorts,
  matching VRAM line width). `mode` selects which buffer(s) to write to:
  - `0` → `DAT_80067130` only
  - `1` → `DAT_8006d7a0` only
  - other → both

### 3.4 Strings of interest (from Ghidra)

Confirmed string table contents include:

- `char/obj/pxl/sup.cpx`, `char/obj/pxl/vs.cpx`, `char/obj/pxl/kselect.cpx`
- `char/obj/map/sup.cmp`, `char/obj/map/vs.cmp`, `char/obj/map/kselect.cmp`
- `char/obj/color/vs.clt`
- `./char/scr/demo/kabe_01.pix`, `kabe_10.pix`, `kabe_10.clt`, `kabe_01.map`
- `./char/scr/stage/psj_*g.clt` (many stage colour tables)
- `pl00_hit.bin` … `pl19_hit.bin` (per-character hit-box tables)

> The `char/...` paths are **not** present on disc as such. The disc is flat
> (`C/`, `M/`, `P/`, `X/`). So the loader translates these virtual paths to
> CD sectors via some lookup. **Action item:** find that lookup table.

---

## 4. Verified decoders (working in `JojoExtractor`)

### 4.1 PSX BGR-15 → RGBA8 (`Psx/PsxColor.cs`)

A 16-bit PSX VRAM word is `bbbbb_ggggg_rrrrr_S` little-endian. We expand each
5-bit channel to 8-bit with `(c << 3) | (c >> 2)` (canonical rounding) and
treat raw `0x0000` as fully transparent. Verified against:

- `C/PL07.CLT` and `C/PL0A.CLT` (both 512 bytes, 16 banks each)
- All 7 entries of `P/OP_COL.PAC` (sizes 0x20..0x280, all multiples of 32)

Each decoded as a clean palette image with no out-of-range bytes.

### 4.2 CLUT bank decoder (`Psx/ClutDecoder.cs`)

Treats any buffer whose length is a non-zero multiple of 32 bytes as
`N` × 16-colour banks. Used by:

- `jojoextract clt <file.clt>`         — for raw `.CLT` files
- `jojoextract palettes <file.pac>`    — for every PAC entry that fits the rule

The output PNG has one row per bank and one column per palette entry,
upscaled by 16× so each cell is visually distinguishable.

### 4.2.1 PAC loader-VM inspection (`Pac/PacVmEntry.cs`)

`jojoextract vm <file.pac> [poolOffset]` prints the Ghidra-verified loader VM
view of a PAC file. It reports each entry's opcode, opcode class, low-byte
index, payload offset, 4-byte stride (`(length + 3) & ~3`), sector-aligned
advance, and known runtime pool-slot effects for `KPLNxx.PAC` / `PLKxx.PAC`.

This is an inspection tool only. It does not decode pixels or pair CLUTs; it
keeps the next parsing step tied to `FUN_80018470`, `FUN_800184c0`,
`FUN_80019914`, and `FUN_80019b8c`.

`jojoextract report <file.pac|directory>` is the code-backed classification
layer for the generic extractor work. For a single PAC it reports:

- known file-ID/name mapping evidence, separated into exact, family, candidate,
  and unproven classifications;
- VM opcode profile and per-class entry totals;
- currently proven extractor handlers (`auto` cached-frame assembly, direct
  placed VRAM previews, compressed TIM export, embedded TIM export, or raw
  entry fallback);
- direct `0x0200` placed 4bpp dimensions without rendering PNGs;
- palette-bank-compatible entries as format candidates only, never as automatic
  image/CLUT pairings;
- unresolved parser work for `0x0100`, `0x0800`, and other unproven classes.

Directory mode prints one line per PAC. It validated over all 751 `P/*.PAC`
files without crashing, giving a practical way to see which files are already
covered by proven handlers and which are merely unclassified. An `unproven`
mapping in this report means no code-backed bridge is implemented yet; it does
not mean the asset is unused.

Current full-`P/` aggregate from `report <P-dir>`:

| Bucket | Count |
|--------|-------|
| mapping `unproven` | 662 |
| mapping `candidate only` | 38 |
| mapping `code-backed exact` | 29 |
| mapping `code-backed family` | 22 |
| handler `compressed-tim` | 242 |
| handler `direct-vram` | 220 |
| handler `raw-entries` | 163 |
| handler `embedded-tim` | 153 |
| handler `direct-vram-frames` | 31 |
| handler `cached-frames` | 28 |
| handler `kpln-clut` | 26 |

Most common VM profiles in the same scan: `01` (242 files), `08 08 01 01`
(102 files), `04 04 04` (92 files), `02 01 01 01` (42 files),
`02 01 01 02 01 01` (39 files), `02 01 01` (34 files), and
`02 02 01 01` (33 files). These are the best next families to trace in Ghidra
because a single consumer path could unlock many PACs at once.

`jojoextract auto` is now total over PAC payload extraction: if no decoded
graphics handler is proven for a PAC, or if a decoded handler cannot complete
for that PAC, it writes raw entry `.bin` files plus a manifest instead of
guessing an image format. This fallback is still code-backed by the PAC loader
VM directory (`FUN_80018470` / `FUN_800184c0`): entry records start at file
offset `0x08`, payloads start at `0x800`, and each payload advances by the
sector-aligned record length.

`auto` also writes raw supplements when a handler decodes only part of a PAC.
Some direct-VRAM files still only have placed atlas previews until their
companion records match a proven consumer. Palette-bank-compatible RAM records
are not paired with generic direct images without caller evidence. Current CLUT
anchors include `FUN_8001a734`, `FUN_8001a890`, and `FUN_8001a8fc`, which load
file IDs via `FUN_8001eda0` into `DAT_8010d800` and upload a `0x180 x 8` CLUT
slab at VRAM y=`0x1e0`; `FUN_8001a458` does the same for a `0x180 x 1` slab at
y=`0x1ee`.

`COCKPIT.PAC` and `ARAKI.PAC` now validate as a narrower direct-frame family:
one class `0x0200` direct VRAM atlas, one 12-byte direct frame table, and one
CLUT-bank blob. The code path is `FUN_8001f324 -> FUN_80020b74 ->
FUN_8001ffd4` for static records, with `FUN_800209ec` providing the equivalent
dynamic-object setup. `FUN_80020b74` / `FUN_800209ec` compute frame descriptor
addresses as `mapBase + frameIndex * 0x0c`; `FUN_8001ffd4` then reads direct
`ushort` tile-word matrices from the same map base, treats `0xffff` as an empty
cell, draws 16x16 sprites, uses `tileWord & 0x07ff` for texture coordinates,
and uses `tileWord >> 11` with caller CLUT mode/base fields for palette
selection. The extractor exports every validated frame record in the PAC-local
table rather than claiming which runtime frame/context was selected in-game.
Focused validation over exact VM profile `02 01 01` completed with 34 selected,
34 succeeded, 0 skipped, and 0 failed at `out/P_020101_direct_frames`. That
batch produced 30 directories with assembled direct frames, 374 direct-frame
PNGs, and 4 raw-manifest directories for direct-image files whose companions do
not validate as this table shape.

`KS_CLRxx.PAC` validates as a wider two-set instance of the same direct-frame
consumer. `FUN_8001bad4` streams file IDs from `DAT_8005a268`, assigns
`DAT_8008a280 = &DAT_80115800` and `DAT_8008a284 = &DAT_80116000`, sets the
texture-base bytes to `0x18/0x10` and `0x24/0`, and uploads CLUT slabs from
`DAT_8010d800` and `DAT_8010dc00`. Those RAM destinations match the PAC entries
`0x0100/0x010b` for the two validated 12-byte frame tables and `0x0101/0x010c`
for their CLUT banks; the texture bases match the direct VRAM layouts selected
by `0x020a` and `0x0209`. `DirectVramFrameRenderer` now emits every validated
candidate in this traced setup instead of requiring the old single atlas/table
triple. Exact-profile validation for `02 01 01 02 01 01` produced 39 selected,
39 succeeded, 0 skipped, 0 failed, 78 direct-frame manifests, and 1716 assembled
PNG frames at `out/P_020101020101_direct_frames`; a pixel sanity pass found 0
blank frames.

For exact profile `02 01 01 01`, `KSAVE.PAC` is a real direct-frame match under
the original single triple rule and exported 78 nonblank assembled frames during
validation. The larger `PSJ/KPSJ/KSDM/KKAK/MTIT/SYO_BG/KTITLE/SS_DEMO` part of
that profile is a different, now-traced packed-map family, not a
`FUN_8001ffd4` 12-byte frame family. `FUN_8002b62c` consumes an object whose
`+0x48` field is the map pointer, `+0x44/+0x46` are width/height in tiles, and
`+0x76/+0x7a` feed the scroll/window offsets. Each nonzero map cell is a
32-bit word: the low halfword is the PSX CLUT coordinate, and the high halfword
selects the texture-page bucket plus 16x16 `u/v` coordinates. The renderer's
cell offset swizzle is:

```text
cellOffset = ((x & 0x0f) + (((x & 0x0f0) + (y & 0x0f)) * 0x10) + ((y & 0x0ff0) * (width & 0x0f0))) * 4
```

Texture coordinates are derived as `texturePageBase + ((textureValue >> 6) &
0x1f)`, `textureU = textureValue & 0x00f0`, and `textureV = (textureValue &
0x000f) * 16`; the page word contributes `x = (page & 0x0f) * 0x40` and
`y = (page & 0x10) ? 0x100 : 0`. The dimension-code string at `DAT_8005b2bc`
backs the standalone candidate sizes (`0x21`, `0x23`, `0x31`, `0x32`, `0x41`,
`0x42`), so files with ambiguous size-equivalent dimensions intentionally emit
all code-backed candidates instead of guessing the scene-specific choice.

`PackedMapRenderer` pairs these maps with the direct `0x0200` VRAM image and
CLUT sources proven by loader/upload code: generic `0x0101` slabs at row
`0x1e0`, plus embedded TIM CLUT blocks when present. When an embedded TIM CLUT
source fits all cells, it is preferred over the generic slab; this removes the
bad duplicate path seen in `KSDM_00.PAC`. Validation: focused `PSJ_000`,
`KPSJ_010`, `KSDM_00`, `KKAK`, `MTIT`, and `SYO_BG` runs all produced nonblank
assembled maps; PSJ/KPSJ sweep produced 40 directories, 63 PNGs, 0 blanks, and
0 low-colour results; the broader `0x0201+0x0102` sweep tested 64 files, with
46 assembled, 18 declined, 79 PNGs, 0 blanks, and 0 low-colour results. Exact
profile `02 01 01 01` auto-batch selected/succeeded 42 files and produced 64
assembled packed-map PNGs with 0 blank or low-colour results. `GAL_SCR.PAC`
still correctly declines because its `0x2c00` map length does not match the
currently traced `FUN_8002b62c` dimension codes.

For exact profile `02 02 01 01`, the `TARxx.PAC` files are uniform two-atlas
plus CLUT-bank bundles (`0x0201`, `0x0217`, `0x0101`, `0x0103`) but do not
contain a validated `FUN_8001ffd4` 12-byte frame table. Exact-profile validation
selected 33 and succeeded 33 with no failures and no direct-frame promotion;
they remain direct VRAM previews plus native RAM payload exports until a real
consumer for the `TAR` CLUT/table data is traced.

Current `TAR` trace state: `FUN_80017cb4` proves the file table base
`DAT_8005661c`, with records addressed as `base + fileId * 0x0c`. In that
table, file ID `0x02d1` has size `0x52000` and matches `TAROT.PAC`, while
`0x02d2..0x02f2` are 33 consecutive `0x21800` records matching
`TAR_00.PAC..TAR_20.PAC` by size/order. `FUN_8001ae70` is the only aligned
immediate `0x02d1` PAC load found so far: it calls `FUN_8001eda0(0x02d1,0,0,0)`,
then uploads `DAT_8010a000` as a `0x180 x 6` CLUT slab at VRAM row `0x1e2` and
updates both CLUT caches through `FUN_80027454(2, rect, DAT_8010a000)`. No
equivalent direct or simple table-driven `FUN_8001eda0` call has been found yet
for the per-card `0x02d2..0x02f2` range. The larger loader block at
`0x8001aedc/0x8001af48` is not current TAR evidence: it has no direct `jal`
xrefs, no raw pointer references in `SLES_025.99` or `M/*.BIN`, and it does not
appear in the live selector tables dispatched by `FUN_8001e36c` and
`FUN_8001e3f4` through `PTR_LAB_8005a414` / `PTR_LAB_8005a464`. Its fixed third
load is file ID `0x0194`, which matches `P/KSS_MAP.PAC` by exact size/count,
so the current evidence says it belongs to a different scene/profile family.
`FUN_8001ae70` itself is also not tarot-exclusive: its second live caller at
`0x8001ac88` conditionally invokes the helper and then loads file ID `0x0195`,
which matches `P/KSTATUS.PAC` by exact size/count. The lone aligned code site
that adds `0x02d0` (`0x8004fb78`) is pointer/callback arithmetic on an object
field, not a PAC file-ID computation. `TARDEV.PAC` maps to the adjacent
`0x02d0` record and has its own `0x0102` length `0x2c00` map-like payload; file
ID `0x01e4` is a distinct smaller `0x0f800` record and must not be conflated
with `0x02d0`.

The important regression check for this profile is that packed 32-bit cells must
not be routed through the direct sprite/frame renderer:

- `PTR_DAT_8005988c` maps low opcode `0x01` to `0x8010d800`, `0x02` to
  `0x8010b800`, and `0x03` to `0x8010a000`.
- `FUN_8001bd34` sets `DAT_8008a280 = &DAT_8010b800`, `DAT_8008a260 = 0x20`,
  `DAT_8008a261 = 0`, and uploads `DAT_8010d800` as a CLUT slab at VRAM
  y=`0x1ea`, width=`0x180`, height=`2`.
- `FUN_800209ec` copies `DAT_8008a280[slot]` into render-object field `+0xa4`
  and the texture-base bytes into `+0xb2/+0xb3`.
- `FUN_8001ffd4` and the raw block at `0x80021dd4` both consume 12-byte
  descriptor streams and 16-bit tile words. Packed-map files such as
  `GAL_SCR.PAC` begin with cells like `7800 0001 7800 0001 ...`, which would be
  invalid as a descriptor stream.
- `FUN_8002b62c` is the traced packed-cell consumer for the files whose lengths
  fit `DAT_8005b2bc`; `GAL_SCR.PAC` remains pending because it does not fit the
  currently proven dimensions.

The exact `08 08 01 01` profile group has also been batch-extracted to
`out/P_08080101_embedded_tim`: 102 selected, 102 succeeded, no skips or
failures. Those 102 PACs produced 102 self-contained embedded TIM files and
204 PNGs, with raw entry supplements preserved for the still-unresolved pool
records. Representative file `KOP_PL00.PAC` has `0x0800`, `0x0801`, `0x0101`,
and `0x0106` records. `0x0106` is a valid embedded TIM stream; `0x0801` is
frame-record-like and `0x0800` looks tile/compression-like, but the runtime
consumer path that joins those records is still not proven.

Exact opcode shape `0800 0801 0101 0106` occurs in 44 PACs (`KOP_PLxx` and
`KSYOxx` families). The broader `08 08 01 01` VM-class profile includes 102
PACs because files such as `KSDxx` use different low-byte `0x0100` opcodes
while still carrying embedded TIM payloads. A previous trail through
`FUN_80019e4c` / `FUN_80019ef4` is not valid evidence for `KOP_PL00.PAC` unless
a file-ID/name bridge is found; length/order checks currently only suggest a
candidate KOP_PL range around file IDs `0x00fc..0x0113`.

Full validation over `P/*.PAC` with multi-TIM embedded parsing and raw
supplements completed with 751 selected, 751 succeeded, 0 skipped, and 0
failed. Output root `out/P_all_auto_multitim` contains 751 PAC directories,
10,144 PNG previews/images, 615 TIM files, 373 embedded TIM files across 153
PAC directories, 2,415 raw entry `.bin` files, 509 raw manifests, and 615
decoded TIM manifests. The raw manifest count is larger than the raw-only
handler total because `auto` also writes supplemental raw entries when proven
decoded handlers cover only part of a PAC.

Embedded TIM entries may contain more than one TIM stream. The extractor now
walks from each TIM block's parsed end to the next valid TIM header, allowing
for padding/gaps between streams. For example, `KDGEVE00.PAC` entry `0x0119`
exports 39 separate 8bpp TIM chunks, and `KBOINGO.PAC` entry `0x011d` exports
12 separate 4bpp TIM chunks instead of only the first stream. Full-file output
counts are 40 TIMs / 42 PNGs for `KDGEVE00.PAC`, and 13 TIMs / 135 PNGs for
`KBOINGO.PAC`.

### 4.2.2 `0x01` compressed TIM payloads (`Psx/CompressedTimExtractor.cs`)

`jojoextract auto <file.pac> [outputDir]` now extracts the exact `01` profile
group when the single RAM record is opcode `0x0122` or `0x0123`.

Code-backed path:

- `FUN_800184c0` maps opcode class `0x0100` through `PTR_DAT_8005988c` and
  stores the selected RAM destination in loader state `+0x50`.
- `PTR_DAT_8005988c[0x22] == 0x80119800`; many callers pass this address to
  `FUN_80025e64` after loading `0x0122` PACs.
- `PTR_DAT_8005988c[0x23] == 0x8011f800`; the call at `0x8001ab44` passes this
  address to `FUN_80025e64`, matching `SSOPEN.PAC`'s `0x0123` record.
- `FUN_80025e64(src)` calls `FUN_800267a8(src, src + 0x40000)`, reads the
  decompressed TIM flags/RECT blocks, and uploads the image plus optional CLUT
  block(s) with `LoadImage`.
- `FUN_800267a8` is a 16-bit-word LZ-style decoder: each 16-bit control word
  drives 16 tokens; clear bits copy a literal word, set bits copy from an
  output back-reference, and `(offset=0,count=0)` terminates the stream.

The extractor writes one decompressed `.tim`, rendered PNG(s), and a manifest
for each supported entry. Validation over `P/` found 242 exact `01`-profile
PACs; 241 were `0x0122`, and `SSOPEN.PAC` was the lone `0x0123` sibling. All
242 now have a proven `compressed-tim` handler in `report <P-dir>`.

### 4.2.3 KPLN runtime CLUT previews (`Psx/KplnClutPreviewer.cs`)

`jojoextract kpln-clut <KPLNxx.PAC> [paletteId|all] [outputDir]` reconstructs
the CLUT rows uploaded by `FUN_800195c8` from KPLN pool entries
`0x0803..0x0807`. The command writes preview PNGs of the VRAM CLUT window
starting at row `0x1e0`.

Code-backed upload mapping used by the preview:

| KPLN opcode | `FUN_800195c8` use |
|-------------|--------------------|
| `0x0803` | row `0x1e8 + side`, x `0`, width `0x80`, source `paletteId * 0x100` |
| `0x0804` | row `0x1ef + side`, x `0`, width `stride >> 2`, source `(stride >> 1) * paletteId` |
| `0x0805` | row `0x1e8 + side`, x `0x80`, width `stride >> 2`, source `(stride >> 1) * paletteId` |
| `0x0806` | row `0x1f1 + side`, x `0`, width `stride >> 2`, source `(stride >> 1) * paletteId` |
| `0x0807` | fixed two-row slab at `499 + side * 2`, x `0`, width `0x180`, height `2` |

Validation output: `KPLN00.PAC` and `KPLN03.PAC` each produce two palette-id
previews. `KPLN03.PAC` is the regression case that forced the dynamic stride
rule: its `0x0806` pool is only `0x80` bytes, and the old fixed `0x140` stride
made the palette count zero even though `FUN_800195c8` handles it through the
runtime pool length. The output shows coherent palette rows and fixed CLUT
slabs, so the CLUT-pool slice math matches the code path.

### 4.2.4 Direct VRAM texture previews (`Psx/VramTexturePreviewer.cs`)

`jojoextract vram-preview <file.pac> [entryIdx|auto|all] [4|8|both]
[tableOffset] [output.png|outputDir]` renders class `0x0200` direct
`LoadImage` payloads as grayscale indexed texture data using the
`DAT_8005991c` layout table consumed by `FUN_800184c0` and uploaded by
`FUN_8001902c`. `auto`/`all` now iterates every `0x0200` entry in the PAC;
only an explicit numeric entry index restricts output to one block. The default
direct preview depth is 4bpp; `both` is available only for comparison/debugging.

The embedded `DAT_8005991c` rows currently cover observed direct-image opcode
low bytes `0x00..0x19` from the disc-wide PAC scan. Those rows are copied from
Ghidra memory at `0x8005991c` with stride `0x1c`. The first four 16-bit fields
are VRAM x/y, word width, and chunk height, but the row is a full upload script,
not just a texture width:

| Offset | Meaning used by `FUN_8001902c` |
|--------|---------------------------------|
| `+0x00..+0x06` | initial `RECT` x, y, w, h |
| `+0x08..+0x0a` | per-chunk x/y deltas |
| `+0x0c..+0x0f` | byte threshold for the secondary step |
| `+0x10..+0x1a` | secondary x/y/w/h/dx/dy deltas after the threshold |

This fixes multi-block files such as `KMN08.PAC`, whose four direct image
entries use opcodes `0x0211`, `0x0210`, `0x0204`, and `0x0201`. Layouts such
as `0x0204` intentionally upload 4-word/16-pixel-wide chunks, then step x and
rewind y after `0x800` bytes. A linear preview is therefore misleading; the
extractor now simulates `FUN_8001902c` and crops the touched VRAM rectangle.

`jojoextract auto <file.pac> [outputDir]` is the general graphics extraction
entry point. It inspects the PAC VM opcodes and runs every code-backed extractor
whose required records are present. Current handlers are:

- compressed TIM export for exact `0x0122` / `0x0123` RAM records consumed by
  `FUN_80025e64`;
- embedded TIM export for PAC entries whose payload already contains a complete
  TIM image block and optional TIM CLUT block. This is backed by the loader VM
  copying class `0x0100` records to RAM and by the TIM block structure itself;
  it walks concatenated TIM streams in one entry, but does not infer any
  external CLUT pairing or decode unrelated pool records;
- cached 4bpp frame assembly when `0x0802` frame records, `0x0801` compressed
  tiles, and the runtime CLUT pool records `0x0803..0x0807` are present;
- assembled direct 4bpp VRAM frames when a PAC has exactly one class `0x0200`
  atlas, exactly one direct 12-byte frame table, and exactly one CLUT-bank blob,
  backed by `FUN_80020b74`/`FUN_800209ec` and `FUN_8001ffd4`;
- direct 4bpp VRAM previews for every opcode-class `0x0200` entry;
- raw VM-entry extraction for any PAC that has no proven decoded graphics
  handler, or as supplemental preservation for entries not consumed by the
  decoded handlers that ran.

This keeps KPLN as one internally recognized opcode pattern, not a required
user workflow. CLUT association is intentionally not inferred for generic direct
blocks; the assembled direct-frame handler only runs after validating the
specific `FUN_8001ffd4` direct tile-word table shape.

For `KPLN00.PAC`, entry `0x0202` selects `DAT_8005991c[2]`, whose relevant
fields are x=`0x180`, y=`0x100`, width=`0x100` VRAM words, chunk height=`4`.
That yields:
- 4bpp grayscale preview: `1024x256`
- 8bpp grayscale preview: `512x256`

The 4bpp preview shows dense, coherent texture/tile structure. This is a
positive sanity check for the direct VRAM payload decode, but it is still a
texture-atlas preview only. Correct sprite reconstruction needs the renderer
path that consumes the KPLN runtime-pool metadata, described below.

`jojoextract kpln-preview <KPLNxx.PAC> [outputDir]` generates a small debug
bundle containing the runtime CLUT previews plus grayscale 4bpp direct VRAM
texture previews.

### 4.2.5 KPLN composited frame previews (`Psx/KplnFramePreviewer.cs`)

`jojoextract kpln-frames <KPLNxx.PAC> [first] [count] [outputDir]
[frameOpcode] [paletteId] [side] [cache|cache-auto|direct] [clutBase] [clutRowBase]
[clutMode] [renderMode] [orientation]` renders code-backed KPLN
frame previews. The default renderer is now `cache`, because `KPLN00.PAC`
uses the renderer table entry at `PTR_FUN_8005aa50[0] = FUN_8001f9b4` for the
recognizable Jotaro frames.

The important correction: the KPLN frame records are not always matrices of
final texture/tile words. The earlier direct preview used plausible-looking
record dimensions, but it fed the compositor the wrong tile indices, producing
recognizable silhouettes with incorrect tile contents.

Code-backed KPLN side-0 pool bridge from `FUN_80019914` and `FUN_80026b70`:

| Runtime slot | KPLN pool | Opcode | Renderer use |
|--------------|-----------|--------|--------------|
| `drawState+0x18` / `DAT_8008a280[0]` | pool 2 | `0x0802` | 12-byte frame records plus bitmasks and per-cell descriptor offsets |
| `drawState+0x14` / `DAT_8008a2c0[0]` | pool 1 | `0x0801` | compressed 0x80-byte 4bpp tile streams referenced by low 24 bits of descriptor dwords |
| `DAT_8008a280[2]` | pool 0 | `0x0800` | alternate/direct slot with texture base x=`0x180`, y-page=`0x10` |

`FUN_8001f9b4` calls `FUN_80020ca0`, which caches/allocates decoded frame
data for the record at `drawState+0x28`. `FUN_800205dc` then processes each
12-byte frame part:

- `record[0]` selects the start of a packed bitmask/descriptor list inside
  `drawState+0x18` (`0x0802` for KPLN side 0).
- `columns = record[2] & 0xff`, `rows = record[2] >> 8`.
- `record[8]` is the number of visible tile descriptors, not merely a boolean
  marker.
- The bitmask chooses which cells are drawn.
- Each visible cell consumes one descriptor dword after the mask area.
- The descriptor low 24 bits are an offset into `drawState+0x14` (`0x0801`).
- `FUN_8002078c` decompresses that referenced stream to exactly `0x80` bytes,
  i.e. one 16x16 4bpp tile.

The descriptor high byte is split by the renderer:

- bits `24..29` are the tile-relative CLUT selector used by `FUN_8001f9b4`
  when `drawState+0x48 <= 0` and renderer mode is below 4.
- bits `30..31` select the tile transform helper, then XOR with
  `drawState+0x3e` for object orientation unless the mode is `4`, `5`, or
  `0x87`.

`PTR_LAB_8005aa68` points to helpers at `0x80020858`, `0x80020884`,
`0x800208c4`, and `0x80020910`. Raw instruction decode shows the mapping is:
`0` = identity copy, `1` = vertical flip, `2` = horizontal flip with nibble
swap, `3` = both flips. This fixes the earlier incorrect assumption that bit
0 was horizontal and bit 1 was vertical.

The complete CLUT and transform for a tile are not only in the frame
descriptor. `FUN_800209ec` copies the caller object/static record into the
draw state:

| draw state field | source object field | extractor argument |
|------------------|---------------------|--------------------|
| `+0x48` | `param_2+0x1d` | `clutMode` |
| `+0x42` | `param_2+0x1e` | `clutBase` |
| `+0x90` | `param_2+0x20` | `clutRowBase` |
| `+0x3c` | `param_2+0xb0` | `renderMode` |
| `+0x3e` | derived from `param_2+0x0b` and `param_2+0x12` | `orientation` |

`clutMode` is a signed byte in the game. The CLI normalizes raw byte values
`0x80..0xff` to signed values before selecting the branch. When
`clutMode <= 0`, `FUN_8001f9b4` uses `clutBase + descriptorSelector`;
for `renderMode < 4`, the selector is the descriptor high byte masked with
`0x3f`, otherwise it is the full high byte. When `clutMode > 0`, the renderer
uses `clutBase` only. The final PSX CLUT coordinate is
`(clutRowBase + selector / 0x18, selector % 0x18)`. Raw KPLN frame previews
default to base `0`, row `0x1e8 + side`, mode `0`, render mode `0`, and
orientation `0` because a standalone `KPLNxx.PAC` frame record does not carry
the caller object/static record that chooses those fields.

`jojoextract kpln-contexts <KPLNxx.PAC> [companion.bin|auto] [side]`
automatically searches for caller context records. For `KPLN00.PAC`, `auto`
selects `M/PL00.BIN` via the verified `KPLNxx`/`PLxx` file-ID bridge.
Recovered contexts are filtered to the KPLN CLUT upload window `0x1e0..0x1f7`,
because `FUN_800195c8` only uploads runtime CLUT rows there. This removes false
overlay-store hits such as `KPLN03` rows `0x003` and `0x000`.

Two caller record initializers are code-backed:

- `FUN_80020b74(drawState, record)` consumes compact 0x28-byte static records.
  `record+0x06` -> CLUT mode, `+0x07` -> CLUT base, `+0x0c` -> CLUT row,
  `+0x0e` -> frame, `+0x1d` -> asset slot, `+0x1e/+0x1f` -> orientation bits.
- `FUN_800209ec(drawState, object)` consumes live object records. `object+0x10`
  -> frame, `+0x1d` -> CLUT mode, `+0x1e` -> CLUT base, `+0x20` -> CLUT row,
  `+0xb0` -> render mode, `+0xb1` -> asset slot, `+0x0b/+0x12` -> orientation
  bits.

The companion `M/PLxx.BIN` files are overlays loaded by `FUN_8001ec84` to
`0x800df000`/`0x800f4800`, not simple serialized record arrays. Therefore the
extractor treats files with a valid overlay pointer table as MIPS code and
scans for constant stores to the exact fields above. Stack-relative stores are
ignored so saves like `sw ...,0x10(sp)` are not mistaken for object frame
fields. Raw compact-record scanning is only used for non-overlay companion
data.

The PL overlays also hold animation scripts. `FUN_800268fc(object, script)` is
the animation interpreter; its command handlers `FUN_80026be0` and
`FUN_80026e1c` copy `script+0x02` into `object+0x10`, which is the frame index
later masked by `FUN_800209ec` with `0x0fff`. Script records advance by
`commandByte & 0x2f`. `KplnRenderContextFinder` follows overlay-local script
pointers passed to `FUN_800268fc`, tracks the object register passed as `$a0`,
and only combines nearby context stores that target that same object register.
Strict rendering only accepts a script-backed frame when that same-object setup
also proves the CLUT row/context. In practice PL03 currently proves many
additional frame indices, but not enough render context to emit them safely.

Object defaults and table bridges are now traced one step further. `FUN_80025394`
clears an object pool and seeds the root object row with `object+0x20 = 0x01e8`.
`FUN_80025640` allocates a child object, clears `object+0x1d..0x1f`, writes
`object+0x20/0x21 = 0x01e8`, and links it through `FUN_800259ec`. Just before
live-object rendering, `FUN_800209ec` calls `FUN_80026b70`, which resolves
`object+0xb1` through `DAT_8008a2c0`/`DAT_8008a280` into `object+0xa0/+0xa4`
and texture offsets from `DAT_8008a260`/`DAT_8008a261` into `object+0xb2/+0xb3`.
`FUN_80019914` is the KPLN bridge that fills those tables after loading
`KPLNxx.PAC` with `FUN_8001eda0`; compact static records use their `+0x1d`
asset slot to index `DAT_8008a280` directly in `FUN_80020b74`.

`FUN_800195c8` places KPLN CLUT pools at side-dependent rows. Side 0 writes
`0x0803/0x0805` to row `0x1e8`, `0x0804` to `0x1ef`, `0x0806` to `0x1f1`,
and `0x0807` to `0x1f3..0x1f4`; side 1 shifts those to `0x1e9`, `0x1f0`,
`0x1f2`, and `0x1f5..0x1f6`. Some PL overlay contexts select high CLUT
bases such as `0xca` and `0xf0`; through the `FUN_8001f9b4` selector math
those land on side-1 rows even while rendering frame data from the same KPLN
PAC. The isolated KPLN preview therefore builds both side placements from the
same PAC so all code-backed KPLN CLUT rows are present. This fixed blank
strict frames whose tiles were non-empty but whose selected rows were absent
from the side-0-only preview model.

The missing CLUT trail was also traced one step further. `FUN_80019e4c` calls
`FUN_8001eda0(0x001a, 0, 0, 0)` and uploads `DAT_8010d800` as a `0x180 x 3`
CLUT slab at VRAM rows `0x1eb..0x1ed`. The file-table length/LBA sequence maps
file ID `0x001a` to `P/COCKPIT.PAC`; its `0x0101` entry populates
`DAT_8010d800` with the first `0x800` bytes used by that upload. The upload is
`0x900` bytes, so the final tail still needs runtime-memory provenance. This
is recorded as a global CLUT source, but it is not currently injected into KPLN
frame rendering because the call ordering/context tying it to the weak PL03
script frames is not proven. The remaining blocker is proving which
object/render-table source those script-driven frames actually use.

Validation for `KPLN00.PAC` + `M/PL00.BIN`: the overlay scan finds caller
contexts for frames `0`, `1`, `3`, and `4`; frame `1` uses `clutBase=4` with
row `0x1e8`. `auto` now uses `cache-auto-strict`, applying only recovered
caller contexts and skipping frames whose orientation/CLUT context has not been
proven from the companion overlay. The plain `cache-auto` CLI mode remains
available for exploratory fallback renders.

Remaining limitation: `FUN_8001f324` draws 24 compact records from the runtime
buffer at `DAT_80073e48 + 0x398` stepping backward by `0x28`. That RAM buffer
is reset/filled at runtime and is not serialized directly in `PL00.BIN`.
Recovering all first-screen/static contexts will require either tracing the
specific writer path into `DAT_80073e48`, or parsing a runtime memory dump that
contains that buffer.

The `direct` renderer path remains available for `FUN_8001ffd4` comparison.
It treats `drawState+0x18` data as final 16-bit tile words. That path is not
the one that produced the correct KPLN00 Jotaro frame sheet.

Validation output:

- `out/KPLN00_cache_frames/KPLN00_cache_0802_p0_s0_frames_0000_0047_sheet.png`
  shows recognizable Jotaro sprites and the `JOTARO` name graphic.
- `KPLN03.PAC` is a useful edge case: it has the cached-frame opcode set and a
  short `0x0806` pool. With the dynamic `FUN_800195c8` CLUT width, direct
  overlay stores, and the strict script-context gate, `auto` exports five
  context-backed frames (`0`, `1`, `3`, `4`, `16`) and skips the remaining
  frames without fully recovered caller context.
- A looser PL03 animation-script experiment produced 306 frame-index matches,
  but validation rendered 93 blank frames and many very low-colour frames. The
  blank group is not just a missing global CLUT row: adding the proven
  COCKPIT.PAC rows `0x1eb..0x1ed` did not change the blank count. Those script
  calls therefore likely need another object/render-table or inherited context
  trace before their frames can be accepted.
- `dotnet build` succeeds after adding the cached renderer.

### 4.3 4bpp / 8bpp linear indexed decoder (`Psx/IndexedImageDecoder.cs`)

Standard PSX indexed pixel layout (the bytes `LoadImage` would have written
into VRAM):

- 4bpp: each byte = 2 pixels, low nibble first (left), then high nibble.
- 8bpp: each byte = 1 pixel.

Used by `jojoextract image <pac> <pixIdx> <clutIdx> <4|8> [width]`.

### 4.4 Observed coherence pattern for some `0x02xx` entries

Falsifiable test: count how often pixel(y,x) == pixel(y-1,x) for every
candidate stride. A real natural image has very high vertical coherence at
the correct stride only; random-looking values at every other stride. Run
on three independent PAC files:

| File / entry              | bytes    | 64    | 128   | 256       | 512   | 1024 |
|---------------------------|----------|-------|-------|-----------|-------|------|
| `MTIT.PAC` #0 (0x0201)    | 0x8000   | 24.3% | 25.0% | **71.9%** | 65.0% | 57.8% |
| `PSJ_000.PAC` #0 (0x0201) | 0x18000  | 6.5%  | 6.1%  | **61.7%** | 47.4% | 31.4% |
| `PSEL.PAC` #10 (0x0201)   | 0x10000  | 80.8% | 80.8% | **95.8%** | 94.4% | 92.8% |

(Random baseline = 6.25%.) In these three cases width=256 is the strongest
coherence peak. That is useful for manual decoding, but it is still a
heuristic, not executable proof. Therefore:

> **Observed:** `MTIT.PAC` #0, `PSJ_000.PAC` #0, and `PSEL.PAC` #10 can all
> be rendered coherently at width 256.
>
> **Not yet verified in code:** that every `0x02xx` entry is 256 pixels
> wide, that every `0x02xx` entry is 4bpp, or that PAC `flags` themselves
> encode this layout.

### 4.5 VRAM stripe loader path — verified by code

`FUN_8001a338` (the kabe-demo background loader) and the more general
`FUN_8001da50` (the multi-purpose loader called with a slot integer)
both upload "pix" data with **identical VRAM rectangles**:

```c
// Uploads N consecutive 0x8000-byte chunks as 64x256 16-bit-pixel rectangles
local_20.w = 0x40;          // 64 16-bit cells = 256 4bpp pixels wide
local_20.h = 0x100;         // 256 rows tall
local_20.x = base_x + i*0x40;
local_20.y = stripe_y;      // 0x000 or 0x100 (texture page region)
LoadImage(&local_20, p);
p += 0x2000;                // 0x2000 u_long = 0x8000 bytes
```

Each `0x8000`-byte chunk is therefore one 64x256 `LoadImage` rectangle,
which would correspond to a 256x256 image if interpreted as 4bpp. This is
verified for this loader path only.

The CLUT path in `FUN_8001da50` (slot=1):

```c
local_20.y = 0x1e0;  local_20.x = 0;  local_20.w = 0x180;  local_20.h = 8;
LoadImage(&local_20, p);   // 384x8 = 3072 16-bit cells = 192 16-colour banks
```

i.e. up to 192 4bpp CLUT banks uploaded into the standard PSX CLUT region
(y >= 480). Slot=2 loads bytes into RAM only.

This proves that at least one runtime path uploads graphics as 0x8000-byte
stripes and one path uploads a 0x180x8 CLUT slab. It does **not** prove
which PAC entries feed those paths. `FUN_8001da50` currently has only three
known callers, all from `FUN_8001a338`, so using it to classify all PAC
directory flags would overreach.

For `flags 0x0204` (PSEL #0, 0x17300 bytes) the coherence test was
inconclusive — different layout, possibly 8bpp or non-power-of-2 stride.

### 4.6 PAC flag survey across the entire game (observed directory shapes only)

Direct binary scan of every `*.PAC` in `P\` gave this global histogram of
the per-entry flag's `HH` byte:

| HH    | count | observed / inferred meaning                                        |
|-------|------:|--------------------------------------------------------------------|
| 0x01  | 1034  | often palette-like or small table-like blobs; many are 32-byte aligned |
| 0x02  |  339  | bulk blobs; some render coherently at width 256                    |
| 0x08  |  948  | mixed: animation/script/sound/context-dependent data               |
| 0x14  |  112  | only inside `COMMON.PAC` family — system data                      |
| 0x24  |  112  | only inside `COMMON.PAC` family — system data                      |
| 0x34  |  112  | only inside `COMMON.PAC` family — system data                      |

**Per-file directory shapes** (a few representative groups):

| File pattern        | Sequence                 | Interpretation                                  |
|---------------------|--------------------------|-------------------------------------------------|
| `PSJ_NNN.PAC`       | `02 01 01 01`            | one bulk blob followed by three `0x01` blobs    |
| `KS_CLR0N.PAC`      | `02 01 01 02 01 01`      | repeated `02/01/01` shape                       |
| `KMN0N.PAC`         | `02 01 08 02 01 08 ...`  | repeated `02/01/08` shape                       |
| `KDGEVE0N.PAC`      | `02 01 08 01 01 01`      | one `02`, then mixed `01`/`08` entries          |
| `KOP_PL0N.PAC`      | `08 08 01 01`            | mixed script/table-like and `01` entries        |
| `KPLN0N.PAC`        | `02 08 08 08 08 08 08 08 08` | one `02`, then many `08` entries            |
| `K{JO,KA,PO}_SND.PAC`| `08 08 08 08`           | `08`-only PACs                                  |
| `KAO_NN`, `KKAO_NN`, `KACL_NN` | `01`            | `01`-only PACs                                  |

> **Observed:** many PACs use repeatable `HH` sequences. For example, every
> `PSJ_NNN.PAC` surveyed so far is `02 01 01 01`.
>
> **Not yet verified in code:** that adjacency alone defines the runtime
> pairing. We do not yet have a Ghidra-identified PAC consumer that reads
> directory flags and dispatches on `HH` in a way we can point to.

### 4.7 Runtime CLUT-bank selection — verified mechanism, incomplete upstream mapping

`FUN_800184c0` is the per-character animation tick. It consumes a script
byte stream (`*(struct + 0x4c)`) of 16-bit opcodes. Opcode high nibble
selects asset class:

| opcode hi-byte | table base       | meaning                                |
|----------------|------------------|----------------------------------------|
| 0x100          | `&PTR_DAT_8005988c` | bgm pointer table                   |
| 0x200          | `&DAT_8005991c`     | seq table (stride 0x1c)             |
| 0x800          | `&PTR_DAT_80059bf4` | image / CLUT pool (see below)       |
| 0x1000, 0x2000 | (vab open)          | sound bank                          |
| 0x3000         | `&PTR_DAT_80059c88` | vag table (stride 0x14)             |
| 0x4000         | `&DAT_80059c80`     | vab data (stride 0x14)              |

For class `0x800`, the table entry is either:
- a direct RAM pointer (e.g. `0x80115800`, `0x8010d800`), used for
  pre-loaded BSS-resident static art, OR
- an *index* with bit `0x08000000` set: low byte = slot ID into the
  runtime image pool. Source RAM address is then computed as:

```c
src = DAT_80079928[slot] + DAT_800799b0[slot]
```

`DAT_80079928[]` holds base RAM pointers to PAC-loaded asset blobs.
`DAT_800799b0[]` holds an offset/stride value previously written into the
runtime pool state. `FUN_800184c0` adds it here; it does not multiply it by
the current frame index.

Cross-references currently show the identified writes to `DAT_80079928[]`
and `DAT_800799b0[]` are inside `FUN_800184c0` itself. Other currently
identified references are reads from `FUN_800195c8`, `FUN_80019914`,
`FUN_8001a040`, and some still-unidentified code. Addresses such as
`DAT_80079968` / `DAT_8007996c` are not separate sources; they are later
slots inside the same `DAT_80079928[]` pool, reached by computed indexes.

The CLUT upload itself happens in `FUN_800195c8`, which uploads 5
contiguous CLUT regions for the character. Each upload destination y is
`*(struct + 0x136) + {0x1e8, 0x1ef, 0x1e8, 0x1f1}` (i.e. **per-character
private CLUT rows in VRAM**). The data source is
`(&DAT_80079928)[uVar1] + (uint)*(byte*)(struct + 0x214) * 0x100` — the
slot's pool pointer plus `(animation_palette_id) * 0x100` bytes.

> **VERIFIED:** the CLUT bank used for a given sprite is selected at runtime
> by the active animation script's palette-id field (`*(struct + 0x214)`).

`FUN_80019914` is the immediate caller of `FUN_800195c8`. It copies three
entries out of `DAT_80079928[]` into the renderer state blocks at
`DAT_8008a280[]` / `DAT_8008a2c0[]`, then calls `FUN_80019a74(param_1)` and
finally `FUN_800195c8(param_1)`.

The upstream mapping from named files to runtime pool slots is now partly
identified for the match/player branches (see section 4.9). What is still
missing is the payload structure inside each PAC record and the exact way those
payloads become tile/frame metadata, raw VRAM image data, or CLUT banks.

### 4.8 Disc streaming loader and `.BIN` evidence

`FUN_8001eda0(fileId, p2, p3, p4)` is a blocking wrapper around the disc
streaming loader. It stores `p2/p3/p4` into loader globals, calls
`FUN_80017f68(fileId)`, then waits until `DAT_80079557 != 0` and retries
while `DAT_80079560 != 0`.

`FUN_80017cb4(dst, fileId)` maps `fileId` through a 12-byte file table at
`DAT_8005661c + fileId * 0x0c`. The first dword is converted with
`DsIntToPos`; the second dword is copied to loader state at `dst + 0x18`.
Corrected finding: the second dword is a transfer byte length. The earlier
`0x0114 == PL00_HIT.BIN` association was wrong because it relied on adjacent
string data. With the correct Ghidra-to-SLES offset mapping, `fileId 0x0114`
has length `0x91000`, which matches `P/KPLN00.PAC`, and the following file
ID starts at the next matching CD position.

Raw executable mapping used for table checks: Ghidra address `0x8005661c`
corresponds to raw `SLES_025.99` offset `0x46e1c` (`fileOffset = address -
0x8000f800`). Reading raw offset `0x5661c` is wrong for this executable.

`FUN_80018074(dest, fileId)` is the explicit-RAM-destination variant. It
sets `DAT_80079568 = dest`, `DAT_80079564 = &DAT_80111008`, clears
`DAT_80079570`, and streams the file using the same CD callbacks.

`FUN_8001902c` is the code-backed streamed **VRAM image uploader**. It pulls
0x800-byte sector buffers from `&UNK_80111800 + ringIndex * 0x800`, uses
per-chunk loader state fields as a `RECT`, calls `LoadImage`, then advances
the RECT and byte counters. Its sibling `FUN_800193e8` streams VAB body data
to SPU via `SsVabTransBodyPartly`; that path is audio, not image data.

The executable contains strings `pl00_hit.bin` through `pl19_hit.bin`, but
those strings are not the `0x0114..0x012d` player image-pool IDs. Code and
size-sequence evidence now identify `DAT_8005a0c8[0..25] == 0x0114..0x012d`
as the `P/KPLN00.PAC..P/KPLN19.PAC` file family. `FUN_80019914` indexes that
table by character id, loads the selected PAC through `FUN_8001eda0`, then
copies runtime pool pointers into renderer state and calls `FUN_800195c8` for
CLUT upload.

The callback region around `0x80018328` is still not a Ghidra function, but
manual decode of the MCP memory bytes shows its key branch:
- when `DAT_80079570 != 0`, it calls `DsGetSector(0x80111000, 0x200)` and
  then `FUN_80018470(&DAT_80079518)`;
- otherwise it calls `FUN_800184c0(&DAT_80079518)` directly.

`FUN_80018470` seeds the animation/asset VM from the sector buffer:
`struct+0x4c = &DAT_80111008`, `struct+0x7b = 1`, `struct+0x59 =
DAT_80111000`, `struct+currentOpcode = DAT_80111008`, and `struct+0x1c =
DAT_8011100c`.

### 4.9 Whole graphics asset pipeline — verified branch map

The executable contains no `.PAC` filename strings. It does contain virtual
asset paths (`.cpx`, `.cmp`, `.clt`, `.pix`, `.map`) plus `.bin` strings. The
runtime pipeline therefore must be traced through loader functions and file-ID
tables rather than by extension guesses.

Verified branches so far:

| Branch | Loader/code path | Runtime destination / consumer |
|--------|------------------|--------------------------------|
| Static object graphics | `FUN_8001a1d8` calls runtime-patched `FUN_80012d28` for `char/obj/pxl/*.cpx`, `char/obj/map/*.cmp`, and `char/obj/color/vs.clt` | Sets `DAT_8008a2c4/DAT_8008a284` for `sup`, `DAT_8008a2cc/DAT_8008a28c` for `vs`, and uploads `vs.clt` to VRAM row 499. |
| Demo/stage direct assets | `FUN_8001a338` + `FUN_8001da50` load `.pix`, `.clt`, `.map` virtual paths | `.pix` uploads 0x8000-byte stripes, `.clt` uploads a 0x180x8 CLUT slab at y=0x1e0, `.map` remains in RAM. |
| Match/player asset scheduler | `FUN_8001988c` calls `FUN_80019914` for both player structs, optionally calls `FUN_80019b8c`, then calls `FUN_8001ec84` for both players | Populates runtime image/map/CLUT pools and renderer table bases. |
| Player image-pool stream | `FUN_80019914` indexes file IDs from `DAT_8005a0c8` (`0x0114..0x012d`) and calls `FUN_8001eda0` | File IDs map by exact length sequence to `P/KPLN00.PAC..P/KPLN19.PAC`. Copies `DAT_80079928[]` slots into `DAT_8008a280[]` / `DAT_8008a2c0[]`, sets tile offsets in `DAT_8008a260/61`, then calls `FUN_800195c8`. |
| Player RAM metadata stream | `FUN_80019a74` indexes `DAT_8005a164` and calls `FUN_80018074` | Loads `M/PLnn_HIT.BIN` (`0x1000` each) to `DAT_800f3800` for side 0 or `DAT_80109000` for side 1. Exact consumer/format still unresolved. |
| Follow-up RAM metadata stream | `FUN_8001ec84` indexes `DAT_8005a5a4` / `DAT_8005a5d8` and calls `FUN_80018074` | Loads `M/PLnn.BIN` to `DAT_800df000` for side 0 and `M/PLnnX.BIN` to `DAT_800f4800` for side 1. Exact consumer/format still unresolved. |
| TKC/TKD splitter/relocator | `FUN_8001e8a8(charA, charB)` indexes `DAT_8005a53c` / `DAT_8005a570` and calls `FUN_80018074` | Loads `M/PLnn_TKD.BIN` and `M/PLnn_TKC.BIN`, copies selected ranges from `DAT_8010d800` into `DAT_800dd800` / `DAT_800ddc00`, and relocates pointer lists into `DAT_800dda00` / `DAT_800dde00`. |
| Extra/object image-pool stream | `FUN_80019b8c` indexes `DAT_8005a198` and calls `FUN_8001eda0` | File IDs map by length sequence to the `P/PLKxx.PAC` family, with deliberate reuses for missing PLK numbers. `DAT_80079968/6c` are computed pool slots written by `FUN_800184c0`, then copied into renderer table slots `DAT_8008a280/2c0[index+7]`. |
| File-ID CLUT slabs | `FUN_8001a458` indexes `DAT_8005a200`; `FUN_8001a734` indexes `DAT_8005a208` | Both stream a file with `FUN_8001eda0` and upload CLUT data from `DAT_8010d800` to VRAM rows `0x1ee` or `0x1e0`. `DAT_8005a208` strongly matches the `P/PSJ_*.PAC` stage sequence by length/order, but exact name mapping still needs more proof. |
| Scene/profile selector tables | `FUN_8001e36c` indexes `DAT_8005a448` and dispatches via `PTR_LAB_8005a414`; `FUN_8001e3f4` indexes `DAT_8005a48c` and dispatches via `PTR_LAB_8005a464` | These are the live selector tables for the nearby scene/menu PAC loaders. They reach `FUN_8001b0c0` for the KMN family, but do not include `0x8001ae70`, `0x8001aedc`, or `0x8001af48`. |
| KMN scene/menu PACs | `FUN_8001b0c0` dispatches to `FUN_8001b148`, `FUN_8001b29c`, or `FUN_8001b450` | These load file IDs `0x00f5`, `0x00f6`, `0x00f7`, matching `P/KMN07.PAC`, `P/KMN08.PAC`, `P/KMN09.PAC` by exact length sequence. The loaders map `0x0100` records into renderer table slots and upload CLUT/palette slabs from runtime pool slots populated by `0x0803..0x0806`. |
| Unreferenced loader cluster | Static block at `0x8001aedc/0x8001af48` | Loads fixed file `0x018f`, then a file ID from `DAT_800ee030`, uploads `DAT_8010a000` and two `DAT_8010d800` slabs, then loads fixed file `0x0194` (`P/KSS_MAP.PAC` by exact size/count). No direct xrefs or pointer-table evidence currently tie it to any live scene. |
| Direct streamed VRAM images | CD callbacks call `FUN_8001902c` | Reads 0x800-byte ring-buffer chunks and calls `LoadImage` using queued RECT state. This proves non-tile image uploads exist. |
| Streamed VAB body audio | CD callbacks call `FUN_800193e8` | Uses `SsVabTransBodyPartly`; not graphics. |

Important loader-table fact: `FUN_80017cb4(state, fileId)` indexes 12-byte
records at `DAT_8005661c + fileId * 0x0c`. The first dword is converted by
`DsIntToPos`; the second dword is copied to `state+0x18`. For explicit RAM
loads (`FUN_80018074`), that second dword is also copied into `DAT_8011100c`,
then `FUN_80018470` seeds the asset VM with `state+0x1c = DAT_8011100c`.

`FUN_800184c0` uses that VM `state+0x1c` as a per-opcode size/advance value.
For image-pool opcodes (`opcode & 0x0f00 == 0x0800`), it:

```c
source = direct pointer from PTR_DAT_80059bf4[index]
  OR DAT_80079928[token & 0xff] + DAT_800799b0[token & 0xff];

DAT_80079928[targetIndex] = source;
DAT_800799b0[targetIndex] = (state->word1c + 3) & ~3;
```

That means `DAT_80079928[]` is the runtime source pointer table and
`DAT_800799b0[]` is an aligned stride/record-size table derived from file/VM
control data, not from PAC adjacency.

For `KPLNxx.PAC`, the code path is now explicit:
- `FUN_80019914` calls `FUN_8001eda0(KPLN_fileId, side, side, side * 8)`.
- The first PAC entry (`0x0202`) is a class `0x200` direct VRAM image stream.
- Entries `0x0800..0x0807` are class `0x800` image/CLUT pool entries. With
  side 0 they write pool slots `0..7`; with side 1 they write slots `8..15`.
- `FUN_80019914` then copies selected pool slots into `DAT_8008a280[]` and
  `DAT_8008a2c0[]`, and `FUN_800195c8` consumes pool entries for CLUT uploads.

For `PLKxx.PAC`, `FUN_80019b8c` calls `FUN_8001eda0(PLK_fileId, 0, 0,
offset)`. The `0x0810` / `0x0811` entries write two later pool slots
(`0x10 + offset` and `0x11 + offset`), which are then copied into renderer
state for the extra/object path.

The `DAT_80079968` / `DAT_8007996c` mystery is resolved by this computed
write path. `FUN_80019b8c` calls `FUN_8001eda0(fileId, 0, 0,
((player->byte136 & 0x7f) << 1))`; `FUN_8001eda0` stores that fourth
argument in loader state byte `0x7f`; then `FUN_800184c0` adds it to the low
byte of each `0x800` stream token when writing `DAT_80079928[targetIndex]` and
`DAT_800799b0[targetIndex]`. The later `FUN_80019b8c` reads from
`DAT_80079928[16 + offset]` / `[17 + offset]` through the symbols
`DAT_80079968` / `DAT_8007996c`.

`FUN_800195c8` consumes those pool entries for CLUT uploads. It uses
`param+0x214` as the active palette id, computes source offsets from
`DAT_800799b0[]`, and uploads multiple CLUT regions to per-player VRAM rows.
For some entries the upload width is `DAT_800799b0[index] >> 2`, and the
source offset is `(DAT_800799b0[index] >> 1) * paletteId`; one entry uses
`paletteId * 0x100`.

#### 4.9.1 Named file-ID bridges verified so far

Exact length-sequence checks against `SLES_025.99` file-table records and the
extracted disc files give these code-backed associations:

| Code table / IDs | Named files |
|------------------|-------------|
| `DAT_8005a0c8`, IDs `0x0114..0x012d` | `P/KPLN00.PAC..P/KPLN19.PAC` |
| `DAT_8005a198`, IDs `0x0211..0x0226` with some repeats | `P/PLKxx.PAC` family, with missing-number fallbacks/reuses |
| `DAT_8005a5a4`, IDs `0x0351,0x0356,...` | `M/PL00.BIN..M/PL19.BIN` |
| `DAT_8005a5d8`, IDs `0x0352,0x0357,...` | `M/PL00X.BIN..M/PL19X.BIN` |
| `DAT_8005a164`, IDs `0x0353,0x0358,...` | `M/PL00_HIT.BIN..M/PL19_HIT.BIN` |
| `DAT_8005a570`, IDs `0x0354,0x0359,...` | `M/PL00_TKC.BIN..M/PL19_TKC.BIN` |
| `DAT_8005a53c`, IDs `0x0355,0x035a,...` | `M/PL00_TKD.BIN..M/PL19_TKD.BIN` |

The first two file groups prove that the per-character M-file order is:
`PLnn.BIN`, `PLnnX.BIN`, `PLnn_HIT.BIN`, `PLnn_TKC.BIN`, `PLnn_TKD.BIN`.
For example, IDs `0x0351..0x0355` match `PL00.BIN`, `PL00X.BIN`,
`PL00_HIT.BIN`, `PL00_TKC.BIN`, and `PL00_TKD.BIN` by exact sizes and CD
order.

The `nn` suffix is the on-disc two-character player suffix, including
`0A..0F` as well as `10..19`. For sprite caller-context recovery, side 0 uses
`PLnn.BIN`; side 1 uses `PLnnX.BIN`. The `_HIT`, `_TKC`, and `_TKD` files are
associated with the same character index but are loaded by separate functions
for hit/collision/table data, not as the first-choice sprite context overlay.

The `DAT_8005a208` stage table is a strong candidate for `P/PSJ_*.PAC`: its
first entries match the canonical stage sequence sizes (`PSJ_000`, `PSJ_010`,
`PSJ_020`, `PSJ_030`, ...), but many stage PACs share identical lengths, so
this table still needs stronger name proof before using it as an extractor
mapping.

### 4.10 Renderer tile composition — verified by code

The per-frame renderer entry point is `FUN_8001f0e8`. It prepares a scratch
state at `0x1f800000`, draws fixed records via `FUN_8001f324`, then walks
the active object list `DAT_80073ed0` and dispatches per object through
`PTR_LAB_8005aa0c[object->type]`.

`FUN_80020b74(drawState, record)` initializes a draw state from a compact
record. Code-backed field facts:
- `record+0x1d` is an asset-set index into `DAT_8008a280[]`.
- `drawState+0x18 = DAT_8008a280[record[0x1d]]` (tile/index-table base).
- `drawState+0x1c = 0x38`, `drawState+0x1e = 0x10` for this path.
- `record+0x0e` is a frame/meta index; `drawState+0x28 = base + index * 0x0c`.

`FUN_800209ec(drawState, object)` is the object version. It first calls
`FUN_80026b70(object)`, which loads:
- `object+0xa0 = DAT_8008a2c0[object[0xb1]]`
- `object+0xa4 = DAT_8008a280[object[0xb1]]`
- `object+0xb2/b3 = DAT_8008a260/61[object[0xb1] * 2]`

It then sets `drawState+0x14/0x18/0x1c/0x1e` from those object fields and
computes `drawState+0x28 = drawState+0x18 + (object->frame & 0x0fff) * 0x0c`.

`FUN_8001ffd4(drawState)` is the tile compositor. It reads the 12-byte meta
record at `drawState+0x28`, then emits one PsyQ `SPRT_16` primitive per
non-empty tile (`tile == 0xffff` is skipped). Important code-backed details:
- It uses 16x16 tiles (`setSprt16`) and advances screen x/y by `+/-0x10`
  depending on flip flags.
- `puVar8[4] == 0` terminates/invalidates a meta record.
- Tile matrix dimensions come from bytes around the meta record
  (`puVar13[-4]` rows and `*((byte*)puVar13 - 7)` columns in the decompile).
- Tile indices are read from `drawState+0x18 + (*metaRecord * 2)`.
- `0xffff` tile entries are transparent/empty.
- Texture-page switching is based on `tileWord & 0x7ff` plus the base offsets
  in `drawState+0x1c/0x1e`.
- CLUT selection for each `SPRT_16` comes from the high bits of the tile word
  (`tileWord >> 0x0b`) combined with draw-state fields `0x42` and `0x90`.

This proves that at least one sprite/object path is tile based and that the
composition format is **not** simply a raw image. A parser for this path
must reconstruct the 12-byte frame/meta records and the tile-index matrix
before selecting texture tiles and CLUTs.

### 4.11 GPU-side CLUT mirror (for runtime palette FX only)

`FUN_80027454` is called after every CLUT upload. It maintains a CPU
mirror of the entire CLUT VRAM region at:
- `&DAT_80067130` — copy A
- `&DAT_8006d7a0` — copy B
- stride `0x300` bytes per VRAM row (= 384 16-bit cells)
- index `(y - 0x1e0) * 0x300 + x * 2`

Only one consumer: `FUN_800278cc`, the palette-flash effect (per-channel
RGB increment with saturation). Not relevant to extraction — useful only
for understanding runtime FX.

### 4.12 Current complete-output pass

`auto` now has code-backed output lanes for every loader VM class observed in
the `P/*.PAC` corpus:
- `0x0100`: compressed TIM (`0x0122/0x0123`), embedded TIM when the payload is
  self-contained, plus native RAM-payload export for undecoded records. The
  native lane is backed by `FUN_800184c0` routing through `PTR_DAT_8005988c`.
- `0x0200`: direct VRAM `LoadImage` previews through the `DAT_8005991c` layout
  table, plus the proven `FUN_8001ffd4` direct tile-word frame assembler when
  the PAC has a single matching atlas/table/CLUT triple.
- `0x0400`: native VAB/SPU sound-bank export. `FUN_800184c0` routes the high
  opcode nibble to VAB header/body/control-table targets and the `0x2xxx` path
  calls `SpuSetTransferMode` / `SsVabOpenHeadSticky`.
- `0x0800`: native runtime-pool export backed by `FUN_800184c0` writes to
  `DAT_80079928` and `DAT_800799b0`, with higher-level consumers still traced
  separately when they drive graphics.

Full batch validation after these lanes:
- `auto-batch P out/P_all_auto_complete_native`: selected `751`, succeeded
  `751`, skipped `0`, failed `0`.
- Aggregate report handlers: `ram-payloads 297`, `runtime-pool 250`,
  `compressed-tim 242`, `direct-vram 220`, `embedded-tim 153`,
  `sound-bank 102`, `direct-vram-frames 31`, `cached-frames 28`,
  `kpln-clut 26`.
- No aggregate `raw-entries` handler remains. Any `raw_entries` folders in the
  full output are supplemental copies emitted for mixed PACs whose decoded
  handlers completed but whose higher-level consumer context is still under
  investigation.

The next real graphics payoff is not raw extraction, but consumer tracing:
direct VRAM + `0x0100` profiles (`02 01 01 01`, `02 01 01 02 01 01`,
`02 02 01 01`) and runtime-pool families (`08 08 01 01`, `KACCNT`, `KACEND`,
`PLK`) need their table consumers traced before promoting more assembled PNGs.

---

## 5. Open questions / next steps

1. **Maintain the whole asset-pipeline map.** Trace `.PAC`, `.BIN`, virtual
   path loads, file-ID streams, direct VRAM uploads, RAM metadata loads, and
   renderer consumption together. Do not focus on one extension family in
   isolation.
2. **Decode PAC payload formats.** The named file-ID bridge and PAC record VM
  grammar are now partly identified for `KPLN`, `PLK`, and likely `PSJ`, but
  we still need the code-backed payload structures that drive
  `FUN_800195c8` and the tile renderer.
3. **Identify the `0x0122` `.cpx` compression.** Name suggests "compressed
   pixels". The workspace `_ghidra_*` folders contain prior decompile
  dumps (e.g. `8016c520`) that may already cover a codec or bytecode parser,
  but those dumps are not yet tied to the current SLES runtime path.
4. **Path -> file lookup.** The loader at `0x80012d28` is a runtime patch
  (see §3.2). Re-visit when needed to connect virtual paths to on-disc files.
5. **`0x08xx` non-palette entries** in `PSEL.PAC` are large (0x9DE..0x3790D)
   and *not* multiples of 32, so they are not CLUTs. Their `HH=0x08` tag
   must mean something different in that context — possibly mapped to a
   per-PAC type table.

---

## 6. Source of truth

- Ghidra instances: ports 8192 and 8193 (project `ami`, file `SLES_025.99`).
- C# extractor project: `JojoExtractor/`.
