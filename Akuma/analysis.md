# Akuma Reverse Engineering Notes

## Session Log (2026-02-15)

### Scope
- Target: `Akuma.exe`
- Goal: identify graphics/palette decode/decompression logic for `.gos` resources.
- Current focus: `FUN_0040f8a0`.

### Initial Findings
- `FUN_0040f8a0` opens a file path via `CreateFileA` + `CreateFileMappingA` + `MapViewOfFile`.
- It parses a table-like structure from mapped memory and allocates per-entry arrays (`operator_new(count * 8)`).
- The parser uses `0xFF` (`-1`) as a terminator/sentinel for entry iteration.
- Function is heavily called by `FUN_0041c7b0`, which appears to be an early startup resource-loading routine.

### Related Functions Observed
- `FUN_0041c7b0` (large startup routine; calls `FUN_0040f8a0` many times)
- `FUN_0043cbd0` (contains similar `.gos` parsing logic, especially enemy asset batches)

### Next Actions
- Rename `FUN_0040f8a0` to reflect its parser role.
- Improve parameter names/signature for clarity.
- Rename at least one related high-confidence helper function.
- Continue documenting inferred file/table layout as confidence improves.

## Updates Applied (2026-02-15)

### Function Renames
- `FUN_0040f8a0` → `ParseGosSpriteTableFile`
- `FUN_0043cbd0` → `LoadEnemyGosSpriteTables`
- `FUN_0041c7b0` → `LoadGosGraphicsResources`

### Signature Improvements
- `ParseGosSpriteTableFile(LPCSTR gosFilePath, char *outSpriteTable, int maxEntries)`
- `LoadEnemyGosSpriteTables(char *outMissingPath)`
- `LoadGosGraphicsResources(void)`

### `ParseGosSpriteTableFile` Behavior Summary
- Opens/maps a `.gos` file and parses metadata from mapped bytes.
- Uses byte at `file+2` as first entry count/sentinel-driven loop seed.
- Builds fixed-stride in-memory entries (`0x10` bytes each, inferred).
- Allocates per-entry child arrays as `count * 8` bytes.
- Each child item stores:
	- one 1-byte field (likely subtype/opcode)
	- one pointer to variable-length payload in mapped file data
- Stops when sentinel `0xFF` is reached or `maxEntries` is hit.

### Cross-Call Context
- `LoadGosGraphicsResources` calls `ParseGosSpriteTableFile` many times for explicit `img\\*.gos` assets.
- `LoadGosGraphicsResources` also calls `LoadEnemyGosSpriteTables` for enemy asset batches.

### Notes / Confidence
- High confidence: this is a `.gos` table/index parser and not the final pixel decompressor itself.
- Medium confidence: parsed child records likely drive subsequent sprite decode/render logic in later routines.

## Graphics Decode Path Identified (2026-02-15)

### High-confidence render/decode chain
- `RenderActorGosSprite` (`0043b1a0`) is a key consumer of parsed `.gos` table entries.
	- Reads frame data pointers via actor fields that originate from the `.gos` tables loaded by `ParseGosSpriteTableFile`.
	- Uses table bases at `this+0x2c8` / `this+0x2cc` and frame index at `this+0x74`.
	- Calls multiple sprite blit paths depending on effect/mirroring/state.

### Core stream format behavior
- `SkipGosSpanRows` (`0040f9c0`) walks scanlines in a span stream:
	- each row = repeated span commands
	- row terminator = `0xFF`
	- span command layout inferred as `[xSkip][pixelCount][pixelData...]`
	- `pixelData` length is `pixelCount * 2` bytes (16-bit pixels)

### Decoder/blitter routines renamed
- `FUN_00417e00` → `BlitGosSpanSprite_Effect0`
- `FUN_00418e00` → `BlitGosSpanSprite_Effect1`
- `FUN_00419240` → `BlitGosSpanSprite_Effect2`
- `FUN_004182e0` → `DecodeGosSpanRleRowRange`
- `FUN_00419a70` → `DecodeGosSpanRle_DimVariantA`
- `FUN_00419660` → `DecodeGosSpanRle_DimVariantB`

### Interpretation
- The `.gos` sprite payloads appear to be **span-based row streams** with per-row `0xFF` end markers.
- Rendering is done directly into a 16-bit target buffer, with multiple effect variants (normal + dimming/light variants).
- At this stage, this looks like **sprite stream decode + blit**, rather than a classic global-palette indexed expansion step.

### Next likely target
- Track where the per-entry 1-byte metadata captured in `ParseGosSpriteTableFile` is consumed.
- Determine whether that byte selects palette bank/effect family, or just animation subtype.

## Metadata Confirmation + Extractor Scaffold (2026-02-15)

### Metadata confirmed from code paths
- Sequence header byte (`entry + 0x00`) = frame count for that animation sequence.
	- Confirmed via animation update logic indexing `*(byte *)(sequenceIndex * 0x10 + tableBase)`.
- Per-frame byte (`frameRecord + 0x04` in file, stored at frameDesc[0]) = frame duration ticks.
	- Confirmed via update logic loading `*(byte *)(frameArray + frameIdx * 8)` into frame timer.

### Sequence header fields used by renderer
- `entry + 0x02` = width
- `entry + 0x04` = height
- `entry + 0x06` = anchorX / x-offset
- `entry + 0x08` = anchorY / y-offset
- `entry + 0x0C` = pointer to frame descriptor array (`8 bytes per frame`)

### Span stream format (high confidence)
- Row stream consists of spans until row terminator `0xFF`.
- Span structure: `[xSkip:byte][pixelCount:byte][pixelData:pixelCount * 2 bytes]`.
- Pixel data is 16-bit packed color, blitted directly into 16-bit target surfaces.

### Attached dump observations
- Both attached files are consistent with:
	- header bytes at start,
	- repeated `FF` row terminators,
	- variable span payloads that fit the decoder behavior above.

### C# class added
- Added [Akuma/AkumaGosExtractor.cs](Akuma/AkumaGosExtractor.cs)
	- Parses `.gos` sequence/frame metadata.
	- Decodes span-RLE frames to bitmaps.
	- Exports all frames as PNG files.
	- Supports RGB565 (default) and RGB555 decode modes.

## `.map` Parsing Logic (2026-02-15)

### Functions identified
- `FUN_00457450` → `LoadMapTilePairs`
- `FUN_004574c0` → `BuildMapTerrainGridFromMapAndTil`
- `FUN_004579b0` → `InitializeStageMapAndTerrain`
- `FUN_00459150` → `LoadMapForCenteredViewport`

### `.map` binary layout (confirmed)
- Byte `0x00`: map width in tiles.
- Byte `0x01`: map height in tiles.
- Then `0x4000` entries of 4 bytes each (total `0x10000` bytes):
	- `int16 pairA`
	- `int16 pairB`

This exactly matches your hex observation: 2 x 2-byte values per tile, and 128 tiles per row:
$$128\ \text{tiles} \times 4\ \text{bytes/tile}=512\ \text{bytes/row}$$

### How the engine interprets each tile pair
In `BuildMapTerrainGridFromMapAndTil`, for each tile:
- `pairA` is the primary lookup index into a stage tile-class table loaded from `.til`.
	- If `pairA == -1` (`0xFFFF`), primary class is treated as 0.
- `pairB` is a secondary/fallback index.
	- If `pairB > 9999`, the code normalizes it with `pairB -= 10000` before use.
	- If `pairB != -1` and primary class is 0, the engine uses class from `pairB`.

So yes: the slot often seen as `0xFFFF` is not padding. It is a second index channel used conditionally, likely for special tile variants/flags/overrides encoded as alternate tile references.

### Related `.til` usage
- A stage-specific `.til` file is opened and read at offset `0x546000`, reading `0xA8C` bytes.
- Those bytes act as a tile classification table consumed by map pair indices.

## Tile Classification Table Semantics (2026-02-15)

### Where the `0xA8C` bytes go
- `BuildMapTerrainGridFromMapAndTil` loads `0xA8C` bytes from `.til` (offset `0x546000`) into `this+0x1001c`.
- For each map tile pair `[pairA, pairB]` from `.map`:
	- `pairA == -1` => class `0`
	- else class = `tilClass[pairA]`
	- if class is `0` and `pairB != -1`, fallback to `tilClass[pairB]` (with `pairB > 9999` normalized by `-10000`).
- Final per-cell class is written into `this+0x10aa8` (`128x128` byte grid).

### Confirmed runtime meaning of values
- `0` = passable/empty.
- non-zero (`1`, `2`, also runtime-written `3`) = blocked for placement/pathing checks.

Evidence:
- `FindNearestPassableTile` (`0045ae80`) explicitly treats a tile as free only when
	`mapClassGrid[y*0x80 + x] == 0`; any non-zero value is rejected.

### Interpretation of `1` vs `2`
- Current high-confidence conclusion: both are non-passable classes for the basic passability query.
- The specific gameplay distinction between `1` and `2` (if any) is not yet fully recovered; they may represent different terrain subtypes that collapse to "blocked" in generic occupancy tests.
