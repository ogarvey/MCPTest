# Damage Incorporated - Palette Handling Analysis

Last updated: 2026-02-25

## Scope

Investigate how `damage.exe` interprets palette/color data around:

- `FUN_00461c34` (init path containing `Creating Palette` and `Setting Palette` strings)

## Current Findings

### 0) Parser-confirmed `clut` retrieval path (strict)

The executable has a direct FourCC chunk lookup path:

- `FUN_004414e8`, `FUN_0044156c`, `FUN_004415f0` call
  - `FUN_00467384(0x636c7574 /* 'clut' */, tablePtr, &outLen)`
- Disassembly confirms each caller then:
  - allocates exactly `0x606` bytes,
  - copies exactly `0x606` bytes from returned pointer,
  - conditionally calls `NormalizeClutBufferEndianness`,
  - frees the temporary parser buffer.

So for palette usage, the game consumes **only the first `0x606` bytes** of the resolved CLUT chunk payload in this path.

`FUN_00467384` behavior (from disassembly, not heuristic):

- Uses parser context tables at `0x48b2b8` / `0x48b238` / `0x48b338`.
- Reads a chunk header into stack scratch and checks first dword against requested FourCC.
- Uses header dwords as traversal metadata (`[+0x0]` tag, `[+0x4]` next/offset link, `[+0x8]` allocation size).
- Allocates `header_dword_2` bytes and reads chunk data from
  - `baseOffset + linkedOffset + (table field at +0x4e highword)`.
- Returns pointer and writes copied byte count through output pointer.

This explains screenshot cases where extra bytes/text appear near/after CLUT area: callers that build active palette buffers still copy only `0x606` bytes.

Related table-field mapping (from `FUN_00467200`, `FUN_00467634`, `FUN_00467f2c`, `FUN_00467f8c`, `FUN_00467f70`):

- `+0x7e` = normalization flag (`0` no swap, `1` swap rules via `FUN_00401090`).
- `+0x48` = base file offset used in index calculations.
- `+0x4a` = stride/count component used by offset calculator.
- `+0x4c` / `+0x50` / `+0x52` = header/index geometry terms (defaults observed in builder path include `0x10` and `0x0A`).
- `+0x4e` high word = data-start bias added after chunk/link resolution in `FUN_00467384`.

Pipeline-separation result from strict xref pass:

- `g_ClutRaw606` (`DAT_0048c598`) has one observed writer: `LoadClutResourceToBuffer` (`FUN_004601a8`).
- `FUN_004414e8`/`FUN_0044156c`/`FUN_004415f0` (which call `FUN_00467384('clut', ...)`) feed render/image paths (e.g., via `FUN_0045f700`, `FUN_00442034`) and free temporary buffers.
- No current xref evidence shows those parser-returned buffers being copied into `g_ClutRaw606` directly.

### 0) `clut` block format (confirmed against code + your screenshot)

Your screenshot lines up with the loader path:

- `FUN_004601a8` loads a resource tagged by string `"clut"`.
- It copies exactly `0x606` bytes into `DAT_0048c598`.
- `FUN_0045f680` immediately runs normalization on that `0x606` region.

This strongly supports a CLUT payload size of `0x606` bytes.

Observed/derived layout:

- `offset +0x00 .. +0x05`: 6-byte CLUT header (format/control words, currently partially unknown).
- `offset +0x06 .. +0x605`: palette payload interpreted as `256` entries of `6` bytes each (`3 x uint16` channels).

Math check:

- `6 + (256 * 6) = 1542 = 0x606`.

So your “4x00 then BE size `0x00000606`” interpretation is consistent with what the game expects for CLUT payload length.

Definitive byte-use statement from Ghidra xrefs/decompile (no heuristic):

- All reads of `g_ClutRaw606` in `damage.exe` are through:
  - `ClutGetEntryByIndex`: `g_ClutRaw606 + 6 + index*6`
  - `ClutGetEntryByIndexPlus6`: `g_ClutRaw606 + 6 + (index+6)*6`
  - `FUN_0045f7bc`: reads channel words at `+6`, `+8`, `+0xA` from `index*6` base
- Therefore the engine-side CLUT reader assumes **6-byte header + 6-byte entries** and takes high bytes of 16-bit RGB words.

Definitive entry-count behavior from Ghidra:

- `FUN_004610c8` uploads palette entries using count = `word ptr [source + 0]`.
- `FUN_00462148` loops `i = 0..255`, but only reads color words when `i < word0`; remaining entries are zero-filled.
- Entry address formula in `FUN_00462148`: `source + 6 + i*6` (R16 at `+0`, G16 at `+2`, B16 at `+4` relative to entry start).

Implication:

- If header word0 is `0x80` (or similar), only first `0x80` entries are used.
- Used palette bytes become `6 + count*6` (e.g., count `0x80` => `0x306`), so bytes after that can contain non-palette data/text without affecting palette conversion.

### 1) The snippet function is initialization + display-mode split

`FUN_00461c34` is a startup routine that:

1. Initializes graphics resources.
2. Builds/clears a 256-entry palette buffer.
3. Calls a virtual method to create palette state (`*in_EAX + 0x14`).
4. If bit depth is 8 (`uStack_6c == 8`), calls `(*DAT_0048c62c + 0x7c)` with `DAT_0048c61c` (likely DirectDraw `SetPalette` on primary/front/back surface).
5. If bit depth is 16 (`uStack_6c == 0x10`), calls `FUN_0046235c` to configure channel masks/shifts for 16-bit conversion.

New confirmation from callers:

- `FUN_00461b84` obtains `EAX` from `DirectDrawCreate(...)` and then calls `FUN_00461c34`.
- Given DirectDraw vtable ordering, `(*in_EAX + 0x14)` in `FUN_00461c34` is high-confidence `IDirectDraw::CreatePalette`.
- This aligns with later use of `DAT_0048c61c` at vtable `+0x18` (palette entry upload pattern consistent with `IDirectDrawPalette::SetEntries`).

### 2) `FUN_004024fc` and `FUN_00461f14` are logging/error wrappers

- `FUN_004024fc` emits status/error strings and optional `MessageBoxA`.
- `FUN_00461f14` maps `HRESULT` values (`DDERR_*`) to message strings via repeated calls to `FUN_004024fc`.

These are not performing palette math directly.

### 3) Real 8-bit palette upload path found

The palette entries are built in `FUN_00462148`, called by `FUN_004610c8`:

- `FUN_00462148(param_1, outPalette)` writes 256 entries of 4 bytes each.
- For each entry `i`:
  - `out[i].byte0 = (source[i*3 + 0] >> 8)`
  - `out[i].byte1 = (source[i*3 + 1] >> 8)`
  - `out[i].byte2 = (source[i*3 + 2] >> 8)`
  - `out[i].byte3 = 0`
- The source format therefore appears to store channel values as **16-bit words per channel**, and the game takes the **high byte** when building DirectDraw palette entries.

Then `FUN_004610c8` calls `(*DAT_0048c61c + 0x18)(..., 0, 0, count, entries)` which matches a palette-entry upload pattern (consistent with `IDirectDrawPalette::SetEntries`).

Source object chain now identified:

- `FUN_0045f194` returns `&DAT_0048c63c` (active display/palette descriptor block).
- `FUN_0045e5d8` refreshes/copies the active descriptor into `DAT_0048c63c`.
- `FUN_0046119c` decompiles as empty but has a thunk (`thunk_FUN_0046119c`) that reads `DAT_0048c63c`, matching the implicit `in_EAX` source used by `FUN_00462148`.

Interpretation: `FUN_00462148` is reading palette-channel words from the current descriptor object rooted at `DAT_0048c63c`, then converting those 16-bit channel words to 8-bit palette entries via `>> 8`.

Raw CLUT entry access helpers:

- `FUN_004600cc(index, out)` reads one 6-byte entry from `DAT_0048c598 + 6 + index*6`.
- `FUN_004600a4(index, out)` reads from `DAT_0048c598 + 6 + (index+6)*6`.

These helpers confirm:

- CLUT entry size is 6 bytes.
- Each entry is treated as three 16-bit channel words.

Implication: treating source palette bytes as simple packed 8-bit RGB triplets will fail if source channels are 16-bit or fixed-point-like and require `>> 8` extraction.

### 4) 16-bit mode does not use 8-bit palette directly

`FUN_0046235c` computes and stores per-channel bit masks/shifts into globals:

- `DAT_0048c66c`, `DAT_0048c666`, `DAT_0048c660` (channel masks)
- `DAT_0048c66a`, `DAT_0048c65c`, `DAT_0048c65a` (right-shift counts)
- `DAT_0048c670`, `DAT_0048c662`, `DAT_0048c65e` (channel max masks)

`FUN_0045c1ac` then uses those globals to remap/compose pixel values through lookup tables during software rendering in 16-bit mode.

## Callgraph Notes (from `mcp_ghydra_analysis_get_callgraph`)

For `FUN_00461c34` depth 3, direct relevant edges include:

- `FUN_00461c34 -> FUN_004024fc`
- `FUN_00461c34 -> FUN_00461f14`
- `FUN_00461c34 -> FUN_004022bc`
- `FUN_00461c34 -> FUN_0046235c`

Plus repeated status/error handling chains through `FUN_004024fc`.

## Working Hypothesis

The game keeps palette colors in a 16-bit-per-channel intermediate/source representation and only converts to 8-bit palette entries by taking each channel's high byte (`>> 8`) at upload time. For 16-bit display paths, it computes dynamic RGB bitfield transforms instead of applying an 8-bit hardware palette.

Channel order update:

- In `FUN_00462148`, bytes are written as index0/index1/index2 from consecutive channel words before flags byte = 0.
- In DirectDraw `PALETTEENTRY`, this maps naturally to `peRed`, `peGreen`, `peBlue`, `peFlags`.
- Current best fit is therefore **RGB word triplets -> high-byte extraction**, not BGR.

Additional corroboration:

- `FUN_0044d610` builds a Windows `LOGPALETTE` and explicitly maps:
  - word0 high byte -> `peRed`
  - word1 high byte -> `peGreen`
  - word2 high byte -> `peBlue`

So channel order is now high-confidence **R, G, B** per 6-byte entry.

### 5) Endianness normalization behavior

`FUN_00401090` applies rule-driven in-place byte swapping to CLUT data (using control streams at `DAT_00486542` and `DAT_0048653c`).

Practical implication:

- CLUT content can be stored in non-native byte order and normalized before use.
- After normalization, the palette readers (`FUN_004600cc`/`FUN_004600a4`) operate on 16-bit channel words, and rendering paths consume `word >> 8`.

This matches your big-endian-size observation and explains why direct naive RGB byte parsing produces wrong colors.

### 6) Draft `DAT_0048c63c` structure map

`DAT_0048c63c` is the active palette/display descriptor root copied around by `FUN_0045e5d8`, `FUN_0045ecec`, and `FUN_0045ee44`.

Current draft (size appears to be `0x1E` bytes from repeated block copies of 7 dwords + 1 word):

- `+0x00` `uint16 mode/type` (frequently compared/forced to `1`)
- `+0x05` `uint8 stateFlag` (`DAT_0048c640._1_1_`)
- remaining words: descriptor parameters used by palette/display mode transitions (still being named)

Neighboring bytes at `0x48c63a` and `0x48c638` also participate in mode logic, so final struct boundaries may include a small pre-header.

### 7) Ghidra annotation progress

Applied in current Ghidra project (`damage.exe`):

- Renamed `DAT_0048c63c` -> `g_ClutState`
- Renamed `DAT_0048c598` -> `g_ClutRaw606`
- Renamed `FUN_004601a8` -> `LoadClutResourceToBuffer`
- Renamed `FUN_0045f680` -> `NormalizeClutBufferEndianness`
- Renamed `FUN_004600cc` -> `ClutGetEntryByIndex`
- Renamed `FUN_004600a4` -> `ClutGetEntryByIndexPlus6`

Created struct type:

- `/damage/DI_ClutState` (size `0x1E`, 15 x `word` fields)

Note: direct `data_set_type` application at address `0x0048c63c` currently fails via MCP transaction API, but the struct exists and can be applied manually in Ghidra UI if desired.

## Practical Decode Guidance

When parsing asset palette data for Damage Incorporated:

1. Try interpreting each channel as `uint16` and use `channel8 = channel16 >> 8`.
2. Confirm channel order (R,G,B vs B,G,R) against known UI/art colors.
3. For 16-bit render paths, emulate mask/shift logic (`FUN_0046235c` + `FUN_0045c1ac`) rather than assuming palettized output.

## Palette Conversion Tool (ready to use)

Added utility:

- `DamageIncorporated/clut_to_rgba.py`

What it does:

- Accepts a file containing either:
  - a `clut` marker + `0x606` payload, or
  - a raw `0x606` payload at a known offset.
- Decodes `256` entries (`R16/G16/B16`) to normal `RGBA`.
- Emits optional `.json`, `.csv`, and `.gpl` palette files.
- Emits raw RGB binary (`3 bytes per entry`) for downstream tooling.
- Can batch-dump one RGB binary per `clut` tag.

Strictness mode:

- Default behavior is now strict for RE repeatability: when using `clut` tag input, you must provide `--tag-payload-offset` (or direct `--payload-offset`).
- Heuristic offset selection is available only with explicit `--allow-heuristic-offset`.

Engine-aligned count mode:

- Converter now honors header word0 by default (matches engine behavior).
- Use `--force-full-256` only for diagnostic inspection.

Default decode mode (matching current RE findings):

- `--word-endian little`
- `--channel-mode high` (i.e., `channel8 = channel16 >> 8`)

Example commands:

- Auto-find first `clut` block and export:
  - `python DamageIncorporated/clut_to_rgba.py <input.bin> --find-first-tag --out-json palette.json --out-gpl palette.gpl --out-csv palette.csv`
- If you already know payload offset:
  - `python DamageIncorporated/clut_to_rgba.py <input.bin> --payload-offset 0x190A4 --out-json palette.json`
- If colors look wrong, test alternate word endianness:
  - `python DamageIncorporated/clut_to_rgba.py <input.bin> --find-first-tag --word-endian big --out-json palette_big.json`
- If `clut` is wrapped and payload start is known from RE:
  - `python DamageIncorporated/clut_to_rgba.py <input.bin> --find-first-tag --tag-payload-offset <hex> --out-json palette.json`
- Write raw RGB bytes for one palette:
  - `python DamageIncorporated/clut_to_rgba.py <input.bin> --tag-offset <off> --tag-payload-offset <rel> --out-rgb-bin palette.rgb`
- Dump one RGB binary per palette tag:
  - `python DamageIncorporated/clut_to_rgba.py <input.bin> --tag-payload-offset <rel> --dump-all-tag-rgb-dir palettes_rgb`

## Next Targets

1. Decode table fields used by `FUN_00467384` (`+0x48`, `+0x4a`, `+0x4e`, `+0x52`, `+0x7e`) from file-header init in `FUN_00467200`/helpers.
2. Fully recover the 6-byte CLUT payload header semantics (`+0x00..+0x05`) by tracing all reads of `DAT_0048c598` before entry lookups.
3. Reconstruct a named struct for `DAT_0048c63c` + adjacent pre-header bytes (`0x48c638..0x48c63b`) to settle boundaries.
4. Extract one real palette blob and compare:
   - naive RGB byte triplets
   - `uint16 >> 8` channels
   - swapped channel orders
5. Locate one callsite that populates the raw channel words to validate endianness (`LE` word assumption) with known in-game colors.

## Update Log

- 2026-02-25: Initial function-chain mapping completed via Ghidra MCP callgraph/decompilation. Confirmed `FUN_00462148` high-byte extraction path and 8-bit vs 16-bit split behavior.
- 2026-02-25: Traced callers of `FUN_00461c34` and tied `*in_EAX` to `DirectDrawCreate` object; identified `+0x14` as high-confidence `CreatePalette` slot and `DAT_0048c63c` as active descriptor root feeding palette conversion.
- 2026-02-25: Correlated screenshot CLUT marker with loader `FUN_004601a8` and confirmed fixed payload size `0x606`; verified entry layout `header(6) + 256*(R16,G16,B16)` and explicit RGB high-byte mapping in `FUN_0044d610`.
- 2026-02-25: Added Ghidra naming pass for CLUT pipeline symbols and created draft `/damage/DI_ClutState` structure (`0x1E` bytes) for ongoing field recovery.
- 2026-02-25: Added wrapper-aware payload offset detection to `clut_to_rgba.py` after observing in-file text contamination when assuming payload starts at `clut+4`.
- 2026-02-25: Switched `clut_to_rgba.py` to strict-by-default offset handling and recorded definitive `g_ClutRaw606` read offsets from Ghidra (`+6 + index*6` entry addressing).
- 2026-02-25: Confirmed via `FUN_004610c8`/`FUN_00462148` that palette entry count is header word0 and trailing bytes after `6 + count*6` are not consumed for palette upload.
- 2026-02-25: Found parser-level FourCC path for CLUT: `FUN_00467384('clut', table, &len)` from `FUN_004414e8/0044156c/004415f0`; disassembly confirms fixed `0x606` copy for active palette buffers and chunk-header-linked retrieval semantics.
- 2026-02-25: Validated against real `DamageIncorporated/images.dim`: file contains 32 `clut` tags; for sampled chunks, payload start is consistently `tag + 0x0A`, and first 6 payload bytes are consistently `06 06 00 00 00 00`.
- 2026-02-25: `images.dim` evidence indicates payload word0 (`0x0606`) is container-size-like metadata in this wrapper, not a usable palette-entry count field at this stage.
- 2026-02-25: Added `clut_to_rgba.py` RGB binary outputs: `--out-rgb-bin` for single palette and `--dump-all-tag-rgb-dir` for per-tag batch export; verified on `images.dim` (32 palette files written at `tag+0xA`).
