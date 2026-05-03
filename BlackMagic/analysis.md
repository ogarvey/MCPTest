# BlackMagic SCM Analysis Log

## Session: 2026-02-24

### Scope
- Target binary: `bm.exe`
- Target function: `FUN_00408f90`
- Goal: recover SCM sprite loading/alignment logic and improve naming in Ghidra.

### Initial Findings
- `FUN_00408f90` builds a sprite resource path (`%s`/`%s.SCM` style format), opens the file, reads a 0x28-byte header, allocates four buffers from header sizes, reads four data blocks and a palette block, and validates per-frame descriptors.
- Header interpretation observed in code is consistent with 5 offset/size pairs.
- Third block entries are treated as 12-byte records and validated with `width * height == length`.
- A 1024-byte palette is copied into a global palette buffer and flagged as active.

### Hypothesis
- `FUN_00408f90` is a full SCM package loader/initializer for sprite sets, not just decompression.
- Alignment likely involves block-2 lookup data used downstream during draw/frame composition (not in this function).

### Callgraph & Xref Notes
- Callgraph confirms `FUN_00408f90` orchestrates:
	- file open/path build (`FUN_00416560`),
	- offset-based reads (`FUN_004165e0`),
	- global alloc+lock (`FUN_00414e10`),
	- error/assert stub (`FUN_00416990`),
	- and Win32 APIs (`wsprintfA`, `CreateFileA`, `SetFilePointer`, `ReadFile`, `GlobalAlloc`, `GlobalLock`, `GlobalUnlock`).
- Inbound call references to loader currently observed at:
	- `00402113` (`CALL 0x00408f90`)
	- `00402206` (`CALL 0x00408f90`)
- In this fresh project these caller regions are not yet promoted to function boundaries by the exposed MCP analyzers, so caller-function renaming is pending manual/function-discovery work.

### Ghidra Renames Applied
- `FUN_00408f90` -> `LoadScmSpritePackage`
- `FUN_00416560` -> `OpenResourceFileAndGetSize`
- `FUN_004165e0` -> `ReadFileBlockAtOffset`
- `FUN_00414e10` -> `GlobalAllocLockOrNull`
- `FUN_00416990` -> `ResourceReadErrorStub`

### Signature/Metadata Updates
- `LoadScmSpritePackage` signature updated to:
	- `int LoadScmSpritePackage(int spriteSetCtx, int pathVariant)`
- Helper signatures updated with meaningful parameter names:
	- `HANDLE OpenResourceFileAndGetSize(undefined4 resourceId, DWORD *outFileSize, int pathVariant)`
	- `BOOL ReadFileBlockAtOffset(HANDLE fileHandle, LPVOID outBuffer, DWORD bytesToRead, DWORD fileOffset)`
	- `undefined GlobalAllocLockOrNull(SIZE_T bytes)`
- Function comment added on `LoadScmSpritePackage` describing block role mapping and descriptor validation (`width * height == length`).

### Global Data Naming Updates
- `DAT_0043f8a0` -> `g_ScmPaletteReadBuffer`
- `DAT_0043f4a0` -> `g_ScmPaletteActive`
- Attempted rename at `0043fca0` (palette-valid flag) failed due transaction/data-item state in current analysis state.

### Alignment-Focused Next Targets
1. Identify first consumer of `spriteSetCtx + 0x50/0x54/0x5c/0x60` to map exact semantics of each loaded block.
2. Prioritize code that iterates block-2 entries from `[spriteSetCtx+0x50]` using count `[spriteSetCtx+0x64]` (set as `block3_size >> 2`).
3. Locate render/composition routine that combines block-2 entries with block-3 descriptor (`w/h/len/offset`) to derive per-frame origin/anchor alignment.

## Session Update: FUN_00401ef0 Recovered

### Key Result
- Recovered function `FUN_00401ef0` is the main Black Magic window procedure and is now renamed to `BlackMagicMainWndProc`.
- It is the caller that initializes SCM resources and later drives sprite placement/alignment state transitions.

### New Renames Applied
- `FUN_00401ef0` -> `BlackMagicMainWndProc`
- `FUN_00409260` -> `ResolveScmFrameAlignRect`
- `FUN_0040f510` -> `DrawScmUnitSprite`
- `FUN_0040fcb0` -> `DrawScmEffectSprite`

### Confirmed SCM Alignment Data Path
From `ResolveScmFrameAlignRect`:

1. Select SCM set/context
- `setId = *(short*)(spriteInstance + 0x4)`
- Context record stride is `0x68` bytes (`s_Play_a_00434610 + setId * 0x68`).

2. Read frame index and facing from instance
- `frameIndex = *(short*)(spriteInstance + 0x0E)`
- facing read from `*(short*)(spriteInstance + 0x22)` and normalized (`2->0`, `3->1`) with `mirrorX=1` when normalized.

3. Bounds check against SCM-loaded count
- Compares `frameIndex` against context field at `+0x64`.
- This maps directly to value set by `LoadScmSpritePackage`: `*(ctx+0x64) = blockSizeAtCtxPlus0x5C >> 2`.

4. Resolve alignment entry address
- `entry = *(uint32*)(ctx+0x5C + frameIndex*4) + *(uint32*)(ctx+0x60)`
- Then reads:
	- `xOff  = *(int16*)(entry + 2)`
	- `yOff  = *(int16*)(entry + 4)`
	- `width = *(int16*)(entry + 6)`
	- `height= *(int16*)(entry + 8)`

This strongly indicates:
- `ctx+0x5C` = frame-to-alignment offset table (dword offsets)
- `ctx+0x60` = base pointer of alignment record blob
- `ctx+0x64` = number of dword offset entries

### How Alignment Is Applied During Draw
In both `DrawScmUnitSprite` and `DrawScmEffectSprite`:

- `ResolveScmFrameAlignRect` provides `(xOff, yOff, width, height, mirrorX)`.
- Screen anchor is computed as:
	- `drawX = worldX + xOff` when not mirrored
	- `drawX = worldX - (width + xOff)` when mirrored
	- `drawY = worldY + yOff`
- Camera scroll offsets are then subtracted (`DAT_005466f0`, `DAT_005466e8`) before final blit.

This is the concrete frame alignment logic: per-frame offsets from SCM alignment records are applied before rendering, with a mirror correction that flips x-anchor using frame width.

### Additional Notes
- `BlackMagicMainWndProc` calls `LoadScmSpritePackage` at `00402113` and `00402206` (map and playable/unit SCM sets).
- Attempts to rename raw data symbols at `0043466C/00434670/00434674` failed due current Ghidra data transaction state; function-level analysis still confirms these as the critical `+0x5C/+0x60/+0x64` SCM context fields.

## 22-byte Record Decoding (from attached dumps)

### Definitive Struct Interpretation
The "22-byte struct" is a **special case** of a variable-length record used by block-2:

- Generic form: `10 + 6 * partCount` bytes
- Your rows are 22 bytes because `partCount == 2`

Recovered layout:

```
offset +00 u16 partCount
offset +02 s16 anchorX
offset +04 s16 anchorY
offset +06 u16 frameWidth
offset +08 u16 frameHeight
offset +0A ScmFramePartRef part0   // 6 bytes
offset +10 ScmFramePartRef part1   // 6 bytes

ScmFramePartRef:
	+00 u16 spriteDescIndex   // index into block-3 12-byte descriptor table
	+02 s16 partOffsetX
	+04 s16 partOffsetY
```

### Why block-2 entry count != block-3 entry count
Block-2 records are **composite frame layouts**, not raw image descriptors. Each block-2 record references one or more block-3 sprite descriptors via `spriteDescIndex` fields (`part0_spriteDescIndex`, `part1_spriteDescIndex`, ...).

So:
- block-3 = pool of image descriptors (`w,h,len,pixelOffset`) in 12-byte entries
- block-2 = per-frame composition/alignment recipes that can reuse those descriptors

This is exactly why the counts are not expected to match 1:1.

### Code Evidence Summary
- `ResolveScmFrameAlignRect` reads record at:
	- `entry = *(ctx+0x5C + frameIndex*4) + *(ctx+0x60)`
	- then consumes `+2,+4,+6,+8` as anchor/size values.
- `FUN_004094d0`, `FUN_004098f0`, `FUN_00409db0` iterate `partCount` and consume the part array beginning at `entry+0x0A` in 6-byte strides.
- For each part, they load block-3 descriptor by `spriteDescIndex` from `ctx+0x54` (12-byte descriptor table).

### Quick decode example (first row in screenshot)
`02 00 EC FF D4 FF 28 00 38 00 00 00 EC FF F4 FF 01 00 F4 FF D4 FF`

- `partCount = 2`
- `anchorX = -20`, `anchorY = -44`
- `frameWidth = 40`, `frameHeight = 56`
- `part0: spriteDescIndex=0, offset=(-20,-12)`
- `part1: spriteDescIndex=1, offset=(-12,-44)`

## Ghidra Data Types Added
- `/BlackMagic/ScmFramePartRef` (6 bytes)
- `/BlackMagic/ScmFrameAlignRecord2` (22 bytes, fixed `partCount=2` observed case)

These are now available in the project type manager for annotation while reversing.

## Part Semantics (part0 vs part1)

### What code proves
- `ComposeScmFrameParts` (and masked/remapped variants) iterates parts in strict record order from `entry+0x0A` with `local_10 += 3` (6 bytes each iteration).
- Each part is blitted immediately into the same temporary composite buffer.
- Therefore draw layering is deterministic:
	- `part0` draws first (back/under layer)
	- `part1` draws after it (front/over layer)

This is not a guess: no sorting or reordering exists in the compositor path.

### Practical interpretation for your 22-byte rows
- Since all observed rows have `partCount=2`, they describe a **two-piece composite frame**.
- `part0_spriteDescIndex` and `part1_spriteDescIndex` are both indices into block-3 descriptor table.
- `part1` should be interpreted as the overlay/top piece for that frame (often upper-body/weapon/head-like offset behavior), while `part0` is the base piece.

Confidence: **high** for draw order (code-proven), **medium** for visual role labels (base/top inferred from ordering + offset trends, not explicit semantic flags in data).

### Why this matches your dump patterns
- In sample rows, second-part Y offsets are frequently more negative than first-part Y offsets, which is consistent with the second part being drawn higher and over the base.
- Reused/shifted descriptor indices across rows are expected because block-2 composes frames from a reusable block-3 descriptor pool.

### New naming pass applied in Ghidra
- `FUN_004094d0` -> `ComposeScmFrameParts`
- `FUN_004098f0` -> `ComposeScmFramePartsMasked`
- `FUN_00409db0` -> `ComposeScmFramePartsRemapped`
- `FUN_0040a240` -> `RasterizeScmFrameComposite`
- `FUN_0040a620` -> `BlitScmFrameDirect`

These names make the layering behavior explicit when navigating call paths from `DrawScmUnitSprite`/`DrawScmEffectSprite`.

## C# Parser Added

Created: `BlackMagic/ScmFileParser.cs`

### What it does
- Reads `.scm` header (`0x28` bytes, 5 offset/size pairs) with bounds validation.
- Reads first 4 blocks and parses:
	- block1 -> `uint` frame offset table
	- block2 -> variable composite records (`10 + 6*partCount`)
	- block3 -> 12-byte sprite descriptors (`w,h,len,offset`)
	- block4 -> palette bytes (expects 1024-byte RGBX; includes block5 fallback when needed)
- Validates that composite record part references point to valid block3 descriptor indices.

### Exposed typed models
- `ScmHeader` / `BlockRegion`
- `SpriteDescriptor`
- `FramePartRef`
- `CompositeFrameRecord`
- `ParsedScmFile`

### Notes
- Composite records are deduplicated by source offset (because frame offset table can reuse records).
- If desired, next step is a small CLI wrapper that dumps parsed records to JSON/CSV for batch inspection.

## Image Export Upgrade

`BlackMagic/ScmFileParser.cs` now includes ImageSharp export of composite frames with shared alignment:

- `ExportAlignedCompositeFrames(string scmPath, string outputDirectory, byte transparentIndex = 0, bool saveRawFrames = false)`
- `ExportAlignedCompositeFrames(ParsedScmFile parsed, string outputDirectory, byte transparentIndex = 0, bool saveRawFrames = false)`

### Behavior
- Reads all 5 SCM blocks and resolves palette vs pixel-data block automatically.
- Composites each frame from block-2 part refs and block-3 sprite descriptors using block-5 indexed pixels.
- Preserves on-disk part draw order (part0 first, then part1...).
- Exports PNGs to a shared canvas computed from all frame anchor rectangles so frames are consistently aligned across animation.
- Optional `saveRawFrames=true` also writes per-frame local composites to `raw_frames/` for debugging.

## MAP File Draw Pipeline (current reconstruction)

### Entry and file format check
- `LoadMapFileByName` (`00408b00`) builds a path using `"MAP\\%02d\\%s"`, opens the resource, reads a map header area, and validates signature text:
	- `"APPLE SHEED MAP FILE V0.41"`
- On success it dispatches to `ReadMapChunksIntoGlobalBuffers`.

### MAP chunk loading into globals
- `ReadMapChunksIntoGlobalBuffers` (`00408be0`) performs bulk reads from the map file into fixed global buffers:
	- `DAT_00550040` region: primary tile-index/ground data used by fast background renderer.
	- `DAT_0046baa0` region: per-cell packed data used by isometric column/edge visibility rendering.
	- `DAT_00591460` region: additional per-map tables (likely metadata/aux overlays).
- After read, it initializes per-entry state and returns control to game state setup.

### Cache/projection rebuild
- `RebuildMapTileProjectionCache` (`004112e0`) clears tile projection arrays rooted at `DAT_00484280` and iterates source map data (`DAT_0046baa0`) to repopulate derived neighbor/projection caches.
- `PopulateProjectedTileNeighbors` (`00411340`) writes multi-array projection/neighbor outputs per transformed map coordinate.

### Render chain from main frame function
Frame compositor (`FUN_0040e8a0`) chooses one of two background map renderers before sprites/UI:

1. `RenderMapGroundTilesToBackbuffer` (`00410310`) when `DAT_00441ef8 == 0`
	 - Uses camera offsets (`DAT_005466f0`, `DAT_005466e8`) to compute visible tile window.
	 - Samples `DAT_00550040` and blits 16x8 ground tiles into the backbuffer (`DAT_005a4700`) from tile atlas base (`DAT_005466ac`).
	 - Handles checker/parity row alignment via `DAT_0044a3c0` lookup pattern.

2. `RenderMapIsometricColumns` (`00410650`) when `DAT_00441ef8 != 0`
	 - Iterates visible projected cells and reads packed per-cell values from `DAT_0046baa0`.
	 - Computes column height/occlusion extents and face-mask bits (`& 0x3c0`), then calls `DrawProjectedTileColumn`.

### Per-tile column draw details
- `DrawProjectedTileColumn` (`00410af0`) is the central isometric tile-column routine:
	- Draws optional side faces based on bitmask flags (`0x40/0x80/0x100/0x200`) via `DrawProjectedTileSideFaces`.
	- Draws column top face polygon with palette/brush variant chosen by depth.
	- Optional debug text path prints tile value from `DAT_00473d40`.
- `DrawProjectedTileSideFaces` (`00410eb0`) emits side-face quads and updates projection-cache arrays (`DAT_00484280` family) using transformed coordinates from `FUN_00411de0`.

### Overlay/visibility pass
- `RenderMapOverlayByVisibility` (`00410890`) is an additional conditional pass from frame compositor (`FUN_0040e8a0`), driven by game mode/state.
- It consults projected cache arrays (`DAT_00484280` family) and map flags (`DAT_00473d40`) to selectively draw overlays/highlights with `DrawProjectedTileColumn`.

### Current model of MAP usage
The `.map` file is loaded into multiple global tile/metadata buffers, transformed into projection/neighbor caches, and then rendered each frame through either:
- a fast ground-tile backbuffer blit path (`DAT_00550040`), or
- a richer isometric column path (`DAT_0046baa0` + projected cache arrays),
followed by conditional overlay rendering, then sprite/entity/UI passes.

## MAP Layer Extraction Spec (for image export)

Goal: extract layer images for one MAP file without running the game renderer.

### 1) File sections required
`LoadMapFileByName` + `ReadMapChunksIntoGlobalBuffers` imply this on-disk order after signature check:

1. `0x20` bytes: signature block (`"APPLE SHEED MAP FILE V0.41"`)
2. `0x540` bytes: map header/config block
3. `0x100` chunks of `0x400` bytes (with in-memory stride `0x404`) -> ground tile index grid backing `DAT_00550040`
4. `0x100` chunks of `0x200` bytes (with in-memory stride `0x202`) -> packed height/face grid backing `DAT_0046baa0`
5. `0x2c00` bytes -> object/overlay record table backing `DAT_00591460`

### 2) Layer dimensions and strides

#### Ground tile-id layer (`DAT_00550040`)
- Cell type: `u16`
- In-memory indexing used by renderer: `value = *(u16*)(base + ((x * 0x202 + y) * 2))`
- Practical bounds observed in render path:
	- `x`: map-column domain (camera-derived, world tile columns)
	- `y`: map-row domain used in 8px steps in `RenderMapGroundTilesToBackbuffer`
- Serialization detail: file stores contiguous `0x400` bytes per column block; runtime keeps a `+4` byte stride gap (`0x404` total per column).

#### Height/face layer (`DAT_0046baa0`)
- Cell type: packed `u16`
- Indexing pattern: `packed = *(u16*)(base + ((x * 0x101 + y) * 2))`
- Low 6 bits (`packed & 0x3f`): column height level
- Upper face mask bits (`packed & 0x3c0`): side-face flags consumed by `DrawProjectedTileColumn` / `DrawProjectedTileSideFaces`
	- directional masks observed: `0x40`, `0x80`, `0x100`, `0x200`
- World projection (`FUN_00411d20`) confirms height contribution:
	- `screenY += -0x10 * (packed & 0x3f)`

#### Object/overlay record layer (`DAT_00591460`)
- Table size: `0x2c00` bytes = `512` records
- Record stride: `0x16` bytes (22)
- Frequently used fields (offsets from record base):
	- `+0x00` active flag (non-zero = used)
	- `+0x02` mirror/orientation byte (checked for value `1`)
	- `+0x04` SCM frame index/id (used via `DAT_0055002c + frameId*2` and render calls)
	- `+0x0A` world X
	- `+0x0C` world Y
	- `+0x0E/+0x10/+0x12` auxiliary per-object values (debug-labeled as hx/hy/hv in draw path)

### 3) Minimal image outputs to generate

1. `ground_tile_id.png`
- Each pixel = one map cell from ground u16 layer
- Suggested encoding: grayscale by `tileId % 256` (or indexed palette if tileset is known)

2. `height_level.png`
- Each pixel = `(packed & 0x3f)` from packed layer
- 0..63 normalized to 0..255

3. `face_mask.png`
- RGB bit-visualization from packed face bits:
	- R: `0x40|0x80` present
	- G: `0x100` present
	- B: `0x200` present

4. `objects_mask.png`
- Start blank, plot active object records (`rec[0] != 0`) at quantized world->cell coordinates.
- World->cell approximation for non-isometric layer preview:
	- `cellX ~= worldX >> 6`
	- `cellY ~= worldY >> 4`

### 4) Conversion helpers needed for accurate placement
- Isometric/world conversion helpers in binary:
	- `FUN_00411d20`: map cell -> world/screen anchor (uses height layer)
	- `FUN_00408760` / `FUN_00408a40`: inverse transform via projection cache
- For exact layer-aligned object plotting, use these transforms instead of raw shifts.

### 5) Practical extraction order
1. Parse signature + header + three payload regions above.
2. Build raw 2D arrays for:
	 - ground `u16`
	 - packed `u16`
	 - object records `22-byte` structs
3. Emit diagnostic PNGs (`ground_tile_id`, `height_level`, `face_mask`) directly from arrays.
4. Add object overlay image by transforming object positions into chosen grid space.

### Confidence notes
- High confidence: file section sizes/order, packed height bits (`&0x3f`), side-face mask usage (`&0x3c0` with 0x40/0x80/0x100/0x200), object record stride `0x16`.
- Medium confidence: exact semantic names for all object-record fields beyond the actively consumed offsets.

## MAP Parser/Exporter Implementation

Created: `BlackMagic/MapFileParser.cs`

### Implemented APIs
- `Parse(string mapPath)`
	- Validates MAP signature (`APPLE SHEED MAP FILE ...`)
	- Reads:
		- `0x540` header block
		- ground chunks (`0x100 * 0x400`) into a `u16` grid (`256 x 512`, stride `0x202`)
		- packed chunks (`0x40 * 0x200`) into a `u16` grid (`64 x 256`, stride `0x101`)
		- object block (`0x2c00`) as `512` records of `22` bytes

- `ExportLayerImages(string mapPath, string outputDirectory)`
	- Generates diagnostic PNG layers:
		1. `ground_tile_id.png`
		2. `height_level.png`
		3. `face_mask.png`
		4. `objects_mask.png`

### Layer encoding implemented
- `ground_tile_id.png`
	- grayscale from low byte of tile id (`tileId & 0xFF`)

- `height_level.png`
	- grayscale from `(packed & 0x3F)` normalized to 0..255

- `face_mask.png`
	- RGB bit visualization:
		- R: any of `0x40/0x80`
		- G: `0x100`
		- B: `0x200`

- `objects_mask.png`
	- plots active object records (`rec[0] != 0`) using approximate world->cell mapping:
		- `cellX = worldX >> 6`
		- `cellY = worldY >> 4`

### Parsed object record fields currently exposed
- `activeFlag` (`+0x00`)
- `mirrorFlag` (`+0x02`)
- `frameId` (`+0x04`)
- `worldX/worldY` (`+0x0A/+0x0C`)
- `auxHx/auxHy/auxHv` (`+0x0E/+0x10/+0x12`)

### Next refinement candidates
- Replace approximate object plotting with exact projection helper transforms (`FUN_00411d20` / inverse mapping path).
- Add optional palette/indexed legend export for tile ids.

## CHP/MASK Terrain Trial Export (experimental)

`BlackMagic/MapFileParser.cs` now includes:

- `ExportTerrainTrialWithChips(mapPath, chp1Path, chp2Path, outputDirectory, mask1Path?, mask2Path?)`

### What it does
- Loads the parsed MAP terrain arrays.
- Loads `CHP` bitmaps (and optional `MASK` bitmaps if present).
- Extracts candidate chip sprites from atlas images via foreground connected-components.
- Renders two trial isometric terrain composites:
	- `terrain_trial_phase0.png`
	- `terrain_trial_phase1.png`
- Writes `terrain_trial_meta.txt` with chip counts and current mapping heuristic.

### Current heuristic (explicit)
- `tileId == 0` => empty
- `highByte != 0` => prefer chips from CHP2
- otherwise use CHP1
- chip index = `lowByte % chipCount`
- terrain cell placement uses observed isometric transform:
	- `x = (col << 6) + 0x20 + (rowParity ? 0x20 : 0)`
	- `y = (row << 4) + 0x10 - ((packed & 0x3F) << 4)`

This is a first-pass visual reconstruction path intended to quickly validate whether CHP/MASK atlases align with MAP tile indices before implementing stricter table-driven mapping.
