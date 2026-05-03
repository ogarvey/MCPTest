# AtomicBomberman .ani Analysis

_Date: 2026-02-16_

## Session Log

- Initialized analysis workspace for AtomicBomberman.
- Target functions identified by user: `FUN_0041cd03`, `FUN_00411d17`.
- Connected to Ghidra instance (`BM.EXE`, project `Win`) and confirmed both function entry points.
- Traced callers/callees/callgraphs for both targets.
- Renamed target functions and key supporting functions in the ANI load/decode path.
- Added function comments in Ghidra at key ANI pipeline entry points.
- Updated several function signatures to give more meaningful parameter names in decompilation.
- Recovered additional ANI helper functions for sequence/frame mapping and mode-0x11 decompression.
- Added first working C# extractor class: `AtomicBombermanAniExtractor`.
- Corrected extractor after raw-hex review: top-level ANI chunks are parsed as `TAG(4)+LEN(4)` payload (no inferred `type` field).
- Traced `.RMP` loading and application path to concrete functions and data tables.

## Working Hypotheses

- `FUN_00411d17` is a **resource-path canonicalizer/remapper** used broadly by asset loading code.
- `FUN_0041cd03` is a **STAT/state-record parser** for ANI data, building per-state frame mapping metadata.

## Key Evidence and Behavioral Notes

### Target 1: `FUN_00411d17` (renamed)

- Called from many places across executable (39 xrefs), consistent with shared filename/path preparation.
- Extracts basename from path (`'\\'` scan), uppercases, splits extension (`'.'`), then remaps extension families.
- Observed extension handling:
	- `plt`/`but` -> `pcx`
	- `ani` -> `ani`
	- `ali` -> `ali`
	- `snd`/`hds`/`cds` -> `rss` (with additional `rb` check on one branch)
	- `res` -> `res`
	- `cam` -> `cam`
	- `sch` -> `sch`
- Writes result into rotating global buffers at `DAT_00460268 + DAT_004604e8 * 0x80`.
- Final path goes through `FUN_00412cd6` (now renamed `NormalizeAndObfuscateResourcePath`).

### Target 2: `FUN_0041cd03` (renamed)

- Single direct caller in ANI pipeline (`FUN_0041d3b5`, now renamed `AniLoadAndResolveStates`).
- Operates on current ANI context (`in_EAX` structure with fields at `+0x6c`, `+0x70`, `+0x74`, `+0x78`).
- Scans for `STAT` records and parses related entries, storing per-state metadata in allocated table entries.
- Uses an internal pool allocator (`FUN_0041c7a5`, renamed `AniAllocStateTableEntry`) and dynamic memory alloc for state arrays.
- Includes corruption checks and diagnostics:
	- `corrupt ani file` message
	- `invalid frameno` message with bounds check against state/frame count

### ANI Loading Flow Observed

1. `AniLoadAndResolveStates` prepares/normalizes path.
2. `BuildCanonicalResourcePath` remaps extension and canonicalizes returned path string.
3. `AniOpenAndIndexChunks` opens `.ani`, validates header (`CHFILE`), indexes chunk stream.
4. First pass: `AniDecodeFrameRecord` for `FRAM` sections.
5. Second pass: `AniParseStateRecords` for `STAT` sections.
6. `AniReleaseLoaderContext` closes/free temporary stream resources.

### Container / Chunk Facts Confirmed

- File starts with ASCII signature: `CHFILE`.
- Sample header shows `CHFILEANI` and then chunk stream beginning at offset `0x10`.
- Confirmed record tags in ANI path:
	- `FRAM`
	- `CIMG`
	- `SEQ `
	- `STAT`
- Corrected parser assumption: raw file chunk headers appear as `tag + len`, payload immediately follows.

## Applied Renames in Ghidra

### User-targeted functions

- `FUN_00411d17` -> `BuildCanonicalResourcePath`
- `FUN_0041cd03` -> `AniParseStateRecords`

### Related pipeline functions renamed for clarity

- `FUN_0041d3b5` -> `AniLoadAndResolveStates`
- `FUN_0041d1a7` -> `AniOpenAndIndexChunks`
- `FUN_0041c837` -> `AniDecodeFrameRecord`
- `FUN_0041d350` -> `AniReleaseLoaderContext`
- `FUN_0041c7a5` -> `AniAllocStateTableEntry`
- `FUN_004129fb` -> `StrToUpperInPlace`
- `FUN_00412cd6` -> `NormalizeAndObfuscateResourcePath`
- `FUN_00451dc4` -> `MemCopyBytes`
- `FUN_00451b5f` -> `StreamReadCount`
- `FUN_0044413a` -> `MemCmpCount`

### Additional ANI functions renamed in this pass

- `FUN_0041c707` -> `AniAllocChunkRecord`
- `FUN_0041c0ba` -> `AniDecodeMode11Rle16`
- `FUN_0041c1ab` -> `AniAllocFrameEntry`
- `FUN_0041c238` -> `AniAllocFramePixelBuffer`
- `FUN_0041c299` -> `AniTrimTransparentBorder`
- `FUN_0041daa7` -> `AniSeqNoToFrameNo`
- `FUN_0041db41` -> `AniSeqNoToStateOffsets`
- `FUN_0041d695` -> `AniLoadAllAnimations`
- `FUN_0044428e` -> `StreamTell`
- `FUN_004442df` -> `StreamReadUInt32`

### Additional remap/render functions renamed

- `FUN_00414a65` -> `RemapTableLoadOrGenerate`
- `FUN_00415ed1` -> `DrawCurrentRemapPreviewOverlay`
- `FUN_004158cf` -> `QueueSpriteWithRemapIndex`
- `FUN_00415a1c` -> `QueueSpriteWithRemapTable`
- `FUN_004156c7` -> `BlitSpriteUsingRemapTable`
- `FUN_00415b22` -> `FlushSpriteDrawQueue`

## Signature / Parameter Naming Updates

- `BuildCanonicalResourcePath(char *inputPath, undefined4 retHi)`
- `AniParseStateRecords(undefined4 streamCtx, int stateIndex)`
- `AniLoadAndResolveStates(char *aniPath)`
- `AniOpenAndIndexChunks(undefined4 aniPath, undefined4 retHi)`
- `AniDecodeFrameRecord(undefined4 streamCtx, int stateIndex)`

> Note: local-variable renaming is not exposed via the current automation interface, so variable improvements were applied via function naming, comments, and parameter naming where possible.

## Variable/Field Interpretation (Current Confidence)

Within ANI context structure (`in_EAX` in several routines):

- `+0x6c` -> likely `recordCount` / number of indexed chunk records
- `+0x70` -> pointer to indexed record array (`0x14` stride)
- `+0x74` -> active stream/file handle
- `+0x78` -> base frame index offset / global frame base for bounds validation

Global ANI pools observed:

- `DAT_00461b6c` / `DAT_00461b68` -> frame entry array + count
- `DAT_00461b5c` / `DAT_00461b58` -> state entry array + count
- `DAT_00461b70` -> total allocated frame pixel bytes counter

Within `AniParseStateRecords`:

- `param_2` -> current state index being populated
- local table at `entry + 0x38` -> per-state array (5 dwords per state record in output table)
- values copied from short locals (`local_24`, `local_20`, `local_1c`) appear to be state tuple fields (timing/index/count style)
- optional sub-array allocated when FRAM list present (`local_60[4]`), storing triples per frame entry.

## Renaming Plan

Continue in next pass with:

- data-structure reconstruction (`struct` definitions for ANI context and state/frame entries)
- field-level variable/type propagation in `AniParseStateRecords` and `AniDecodeFrameRecord`
- identify exact decompression/encryption mode used in `AniDecodeFrameRecord` branch `local_18 == 0x11`.

## Decompression / Pixel Findings (Important)

- `AniDecodeMode11Rle16` (`0x11`) is now understood:
	- control byte `0xFF` terminates/fails
	- control high-bit clear: copy `(n+1)` literal 16-bit words
	- control high-bit set: repeat one 16-bit word `((n & 0x7F)+1)` times
- `AniDecodeFrameRecord` validates pixel format by `(flags & 7) == 4` in known working path.
- Original game maps decoded 16-bit values through lookup table `DAT_00495390` to 8-bit indexed output.

## C# Implementation Added

Created file:

- `AtomicBomberman/AtomicBombermanAniExtractor.cs`
- `AtomicBomberman/ExtractAniFrames.cs` (simple CLI entry point)

Current implementation capabilities:

- parses `CHFILE` ANI records using `TAG+LEN`
- indexes chunk headers with resilient tag+len scanning and padding-byte resync
- decodes FRAM/CIMG frame payloads
- supports CIMG encodings:
	- `0x00` raw
	- `0x11` RLE16
- outputs decoded frames as RGBA and exports `*.bmp` sequence

Current intentional differences from game renderer:

- game uses `DAT_00495390` 16-bit->8-bit mapping; extractor currently converts 16-bit directly to RGB (default RGB565)
- STAT/SEQ playback-order reconstruction is not yet applied in exporter (frames are dumped in FRAM order)

### Correction Applied After Hex Validation

- Removed the previous inferred `type`/`dataOffset` header assumption from parser.
- Added robust FRAM fallback: if legacy FRAM header parse fails, scanner searches embedded `CIMG` subchunks inside FRAM payload.
- Added chunk resync behavior to tolerate alignment/padding bytes between chunks.

### Quick Usage (Current Extractor)

- Call `AtomicBombermanAniExtractor.ExportFramesAsBmp(inputAni, outputDir)` from your own tool code.
- Or run the included entry point `ExtractAniFrames` with:
	- arg0: input `.ani` path
	- arg1: output directory

## Next Steps To Reach Full Converter Goal

1. add STAT/SEQ interpretation to export named animation/state sequences in playback order
2. verify FRAM/CIMG header fields and offsets against additional ANI samples
3. optionally emulate `DAT_00495390` path for stricter visual parity with original 8-bit renderer
4. optional PNG export mode (ImageSharp or custom PNG encoder)

## New Clue: `.RMP` Color Remapping

User-provided note:

- `color remaping default values (RGB pairs, from 0 to 9 of the .RMP files)`
- sample pairs include ranges like `200..202`, `205..207`, `210..212`, `215..217` with percentage-like values.

### What is already confirmed in code

- There are **two remap layers** in the engine:
	1. **16-bit -> 8-bit conversion** (`DAT_00495390`) used in ANI/frame decode and many render paths.
	2. **8-bit palette-index remap tables** loaded/generated from `.RMP` and stored in `DAT_00460564[0..9]` (each 256 bytes).

- `.RMP` load/generate routine is `RemapTableLoadOrGenerate` (`00414a65`):
	- Formats filename as `"%u.rmp"`.
	- If `param_3 == 0` and file exists: reads 256-byte table into `DAT_00460564[slot]`, then reads 3 slider bytes.
	- Else: procedurally builds a 256-entry nearest-color remap from palette + RGB percentages and can write it back.

- Application point for `.RMP` remap is `BlitSpriteUsingRemapTable` (`004156c7`):
	- For each source pixel index `p`:
		- if `p == 0` keep transparent `0`
		- else output `remap[p]` where `remap` is table pointer from `DAT_00460564[slot]`
	- Then blits remapped scanlines to destination.

- `QueueSpriteWithRemapIndex` selects table `0..9` and enqueues command; `FlushSpriteDrawQueue` dispatches to `BlitSpriteUsingRemapTable`.
- `DrawCurrentRemapPreviewOverlay` uses string `"Remap table #%u (%u.rmp)"` and draws a UI preview of currently selected table/sliders.

Equivalent decompiled logic shapes:

- Base conversion: `outPixel = DAT_00495390[pixel16]`
- Table remap blit: `outPixel = (srcIndex == 0) ? 0 : remap[srcIndex]`

### Current interpretation

- `.RMP` files are now confirmed to hold per-index (0..255) palette remap tables for player/team-style recoloring and related effects.
- They are not direct writes into `DAT_00495390`; instead they are a second-stage remap over already-indexed sprite pixels.
- The 200+/205+/210+/215+ groups still likely correspond to palette bands targeted by these remaps.

### Next trace target

1. identify exact `.RMP` on-disk binary layout details (header/metadata vs pure 256-byte map)
2. map slider bytes (`DAT_00460bd0/da/e4`) to semantic channels/order used in generator path
3. mirror `BlitSpriteUsingRemapTable` behavior in C# exporter as optional post-pass remap
4. validate with known unit/player sprites that use remap indices 0..9

## Notes

Fresh Ghidra project: default auto-generated names are not trusted unless clearly imported/library-labeled.

## Clarification: `.RMP` size vs loader behavior

User-provided hex screenshots show `.RMP` files are 259 bytes.

This matches `RemapTableLoadOrGenerate` exactly:

1. reads `0x100` bytes into remap table (`DAT_00460564[slot]`)
2. reads **3 additional bytes** via three byte-read calls (`FUN_0045266a`)

So on-disk layout is effectively:

- bytes `0x000..0x0FF`: 256-byte index remap table
- bytes `0x100..0x102`: 3 slider/control bytes (seen as e.g. `64 64 64`, `64 00 0A`)

Total = `256 + 3 = 259` bytes.

Critical behavior in loader (easy to miss):

- After reading the 256-byte map, loader normalizes it:
	- for each index `i` in `0..255`, if `map[i] == 0` then `map[i] = i`.
- This means `.RMP` files are effectively sparse override maps; zero bytes are treated as "identity", not "map to 0".
- If this normalization is omitted in reimplementation, remap output becomes heavily incorrect.

## Clarification: where 8-bit index comes from when ANI decode yields 16-bit values

`AniDecodeFrameRecord` decodes source pixels to 16-bit words (`ushort`) then immediately translates each word through LUT `DAT_00495390`:

- `dst8 = DAT_00495390[src16]`

Therefore 16-bit decoded values are not final display colors; they are keys into a precomputed 16-bit->8-bit conversion table. This is why final sprite buffers remain 8bpp indexed.

After that, optional `.RMP` remap is a second stage in `BlitSpriteUsingRemapTable`:

- `dst8 = (src8 == 0) ? 0 : remap[src8]`

So pipeline is:

1. decode ANI pixel stream -> `src16`
2. convert via `DAT_00495390` -> `src8`
3. optional `.RMP` table remap -> `dst8`

## Clarification: how `DAT_00495390` is populated

Confirmed in Ghidra there are two population paths:

1. **Primary load path (from color-table resource/file)**
	- In `FUN_0042c9fc`, after palette read/setup, code calls function pointer `DAT_0049d3e8` with:
		- `EDX = 0x495390` (destination `DAT_00495390`)
		- `EBX = 0x8000` (32768 bytes)
	- This is a direct bulk fill of the 15-bit (32x32x32) -> 8-bit LUT.

2. **Generated/rebuilt path (algorithmic quantization)**
	- `FUN_0042d03c` computes nearest palette mapping in 5-bit RGB cube space.
	- It sets destination pointers into `DAT_00495390` and calls `FUN_0042d37c` / `FUN_0042d500` / `FUN_0042d7a4`.
	- Inner routine `FUN_0042d7a4` writes palette indices through the pointer that targets `DAT_00495390`.

Interpretation:

- Normally, `DAT_00495390` is loaded as a precomputed 32KB table.
- It can also be regenerated from current palette data when alternate init path is used.

## Debug note: common remap mismatch causing wrong colors

If output looks plausible in direct RGB16 render but wrong after remap+palette:

- Do **not** treat `.RMP` as RGB remap; it remaps **8-bit indices** only.
- Use LUT index as `lut15to8[p16 & 0x7FFF]` (mask to 15-bit domain).
- Apply transparency test on the original `p16` key path first (`(flags & 4) && p16 == transparentKey16 -> 0`).
- Apply `.RMP` only after LUT (`idx = rmp[idx]` for `idx != 0`).
- Final RGB lookup should use the same base palette associated with `color.pal`/LUT build.

Important practical caveat:

- Using `bombpal.pcx` plus `.RMP` can be a **double remap** depending on what `bombpal.pcx` encodes.
- No direct `bombpal` string usage has been found in current BM.EXE scan, while `"color.pal"` is referenced explicitly.
