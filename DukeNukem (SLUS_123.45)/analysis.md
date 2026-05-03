# Duke Nukem (PSX) PMP Loader Notes

## Primary Function

- Address: `80086a68`
- Renamed in Ghidra to: `LoadLevelPmpPackage`
- Current signature:

```c
int LoadLevelPmpPackage(
    byte *mapName,
    int *spawnX,
    int *spawnY,
    int *spawnZ,
    short *spawnHeading,
    short *spawnSector,
    int loadMode)
```

## High-Level Semantics

`LoadLevelPmpPackage` is the level/package loader for `.PMP` files on CD.

What it does:

1. Builds the PMP path from the level name using `EPISODE%c.%s.PMP`.
2. Locates the file on CD.
3. Reads a `0xa8`-byte PMP header block into RAM and mirrors it into globals.
4. Loads and uploads VRAM image chunks.
5. Loads the speech-bank block and uploads the speech payload into SPU memory.
6. Loads the main packed level-data chunk.
7. Expands sector/edge/entity data structures into the level heap.
8. Computes edge lengths and rebuilds entity slot lists.
9. Loads extra lookup tables, packed graphics, and speech buffers.
10. Resolves the spawn sector from spawn X/Y and starts the XA track for the level.

## Load Mode Semantics

- `0`: full level load
- `1`: load package and restore saved dynamic state
- `2`: fast reload variant; sets the special flag then falls back into the full-load path
- `3`: partial load path that skips the main geometry/entity rebuild block but still loads later metadata tables
- `4`: special full-load variant; sets the special flag then falls back into mode `0`

The special flag is the local `local_48` path in the decompile and suppresses the final draw-move/vibration-related tail block.

## PMP Helper Functions

- `80086518` -> `PmpLzDecompress`
- `800865e8` -> `LocatePmpFileOnCd`
- `80086678` -> `ReadPmpSectorsFromCd`
- `80086758` -> `ReadPmpChunkIntoScratch`
- `80086820` -> `CopyOrDecompressPmpChunk`
- `80086894` -> `maybeRecycleSpeechBankAssignments`
- `80086990` -> `maybeTrimSpeechBanksToHeapLimit`

## Direct Callees Renamed

- `8007e35c` -> `SelectActiveOrderingTable`
- `8007e444` -> `FlushPendingOrderingTable`
- `8003dd68` -> `WaitForSpuTransferIdle`
- `8003dde4` -> `maybeUploadSpeechDataToSpu`
- `80087dcc` -> `StartXaStreamRange`
- `80087f08` -> `PlayLevelXaTrack`
- `80087fb0` -> `FindSectorSearchCell`
- `80088034` -> `ResolveSectorForPoint`
- `8008840c` -> `ComputeEdgeLengths`
- `8006f8d4` -> `InitializeEntitySlotLists`
- `8006fa18` -> `maybeAssignEntitySlot`
- `8006fb08` -> `AllocateEntitySlotForSector`
- `8006fbf4` -> `AllocateEntitySlotForType`
- `8006fcd8` -> `ReleaseEntitySlot`
- `8006fd08` -> `RemoveEntitySlotFromSectorList`
- `8006fe28` -> `RemoveEntitySlotFromTypeList`
- `8008b9a8` -> `BuildActiveEntitySlotBitset`
- `8008ba48` -> `CopyHalfwordBlock`
- `8008bae4` -> `XorHalfwordBlock`
- `8008bb20` -> `XorAndWriteBlock`
- `8008bb78` -> `RestoreXoredBlock`
- `8008bca4` -> `maybeSaveRestoreLevelStateTables`
- `800867ac` -> `maybeStopVibration`

## PMP Header Fields Renamed

These global fields are populated from the first `0xa8` bytes of the PMP file:

- `gPmpVramChunkFileOffset`
- `gPmpVramChunkPackedSize`
- `gPmpSpeechBanksChunkFileOffset`
- `gPmpSpeechBanksChunkPackedSize`
- `gPmpLevelDataChunkFileOffset`
- `gPmpLevelDataChunkPackedSize`
- `gPmpPackedGfxFileOffset`
- `gPmpPackedGfxByteCount`
- `gPmpSpeechBufferFileOffset`
- `gPmpSpeechBufferByteCount`
- `gPmpSpawnX`
- `gPmpSpawnY`
- `gPmpSpawnZ`
- `gPmpSpawnHeading`
- `gPmpSpawnSector`
- `gPmpVramRectCount`
- `gPmpLookupEntryCount`
- `gPmpSectorCount`
- `gPmpEdgeCount`
- `gPmpLoadedEntityCount`

Still unresolved from the header block:

- `DAT_800b456c`
- `DAT_800b4574`
- `DAT_800b4576`
- `DAT_800b4578`
- `DAT_800b457a`
- `DAT_800b457c`
- `DAT_800b457e`

These drive later metadata-table allocation, but I have not pinned all of them to stable semantics yet.

## Runtime List Globals Renamed

- `gSectorCount`
- `gEdgeCount`
- `gEntitySlotCapacity`
- `gSectorEntityListHeads`
- `gSectorEntityPrev`
- `gSectorEntityNext`
- `gEntityTypeListHeads`
- `gEntityTypePrev`
- `gEntityTypeNext`
- `gFreeEntitySlotHead`

These names make the entity-slot allocator path much more readable.

## Decompression Format (`PmpLzDecompress`)

The PMP compressed chunk format is an LZ-style bitstream.

Observed behavior:

- A control byte is consumed into a 16-bit flag register as `flags = control | 0xff00`.
- Bits are consumed LSB first.
- Bit `0` means literal copy of the next byte.
- Bit `1` means back-reference.
- For back-references:
  - If token `< 0x60`:
    - distance = `((token & 0x0f) << 8) | nextByte`
    - distance `0` terminates the stream
    - length = `(token >> 4) + 3`
    - if `(token >> 4) == 5`, an extra length byte is used and `length = extra + 8`
  - Else:
    - distance = `0x100 - token`
    - length = `2`

This is enough to reproduce the decompressor in C#.

## Important Behavioral Notes

- `ReadPmpChunkIntoScratch` reads a file chunk into the top of the level heap, sector-aligned.
- `CopyOrDecompressPmpChunk` either memcpys raw chunk payload or calls `PmpLzDecompress`.
- The VRAM chunk is a stream of `RECT` headers followed by standard embedded PMP chunk descriptors and payloads.
- Each VRAM `RECT` width is in PSX VRAM 16-bit word units, not necessarily final texture-pixel width.
- Because uploads may contain raw 16bpp data or packed 8bpp / 4bpp indexed texels, a raw `LoadImage` upload cannot be treated as a final sprite image without also knowing the eventual CLUT and draw-mode context.
- `ResolveSectorForPoint` confirms or repairs the spawn sector based on spawn X/Y.
- `PlayLevelXaTrack` derives the XA selection from the episode and map string.
- `maybeSaveRestoreLevelStateTables` is used by load mode `1` to preserve/restore runtime state across a fresh base package load.

## Current Extractor Status

The C# extractor now does the following:

1. Parses the PMP header.
2. Extracts the five top-level PMP sections.
3. Decompresses the first level-data embedded chunks.
4. Parses the full VRAM upload stream.
5. Rebuilds a raw RGBA5551 VRAM atlas preview.
6. Emits three per-upload preview interpretations:
  - `rgba5551`: raw 16bpp VRAM words
  - `indexed8`: each VRAM word expanded to two 8bpp indices
  - `indexed4`: each VRAM word expanded to four 4bpp indices
7. Parses the late level-data chunk chain and the trailing raw lookup-index tail.
8. Rebuilds actual sprite-frame references from:
  - per-frame width / height
  - per-frame anchor / animation dword
  - per-frame texture descriptor + UV
  - per-group CLUT / tpage mode table
9. Exports first-pass resolved sprite-frame PNGs.

These previews are for geometry / packing validation only. Final correct texture colors still require CLUT resolution and the draw-time texture mode.

## Late Level-Data Layout

For `E1L1.PMP`, the level-data section contains 10 embedded descriptors followed by a raw ushort lookup tail.

Observed descriptor chain:

1. chunk `00`: sector data
2. chunk `01`: edge data
3. chunk `02`: entity / runtime table data
4. chunk `03`: zero-length descriptor; valid and intentionally empty for this level
5. chunk `04`: `4`-byte table, count `2`
6. chunk `05`: `4`-byte table, count `21`
7. chunk `06`: `6`-byte table, count `16`
8. chunk `07`: spatial / search table block (`3396` bytes)
9. chunk `08`: texture-group table (`215 * 8 = 1720` bytes)
10. chunk `09`: frame metadata table (`839 * 12 = 10068` bytes)
11. raw tail after chunk `09`: lookup-index array (`839 * 2 = 1678` bytes)

This matches the loader behavior better than the original extractor assumption that the stream stopped after 3 chunks.

## Frame Table Reconstruction

The loader populates runtime sprite lookup tables from the late metadata as follows:

- `DAT_800fd570`: frame width byte
- `DAT_800fed70`: frame height byte
- `DAT_800d6200`: per-frame anchor / animation dword
- `DAT_800dc200`: low word = texture-page-group descriptor, bytes `+2/+3` = `u/v`
- `DAT_8011e0d4`..`DAT_8011e0db`: per-group `8`-byte CLUT / tpage mode table

The effective texture-page word is assembled as:

- low `5` bits from the per-frame descriptor word
- texture depth bits from the per-group table byte at offset `+4`

This is enough to resolve:

- final frame width / height
- frame UV origin
- texture page base
- texture mode (`indexed4` / `indexed8` / possible `rgba5551`)
- CLUT position for indexed frames

## Current Findings From EPISODE1

After switching from raw upload-rectangle export to lookup-table-driven frame export:

- sprite sizing and UV alignment now come from the game’s frame tables, not guessed VRAM rectangles
- all `EPISODE1` levels validate with 10 parsed level-data descriptors
- all `EPISODE1` levels export resolved sprite frames successfully
- across `EPISODE1`, the resolved frame tables use `indexed4` and `indexed8`
- no `rgba5551` frame references were observed in the resolved `EPISODE1` frame tables so far

Additional current constraint:

- `packed-gfx.bin` is not a flat atlas supplement; it is a streamed frame-descriptor table used by the draw path for a variable number of low texture groups
- the first dword of `packed-gfx.bin` is the offset-table byte count, so `groupCount = firstDword / 4`
- observed `packed-gfx` group counts across `EPISODE1`: `4, 5, 6, 5, 6, 5, 2, 1, 1`
- the extractor now decodes those streamed descriptors directly and marks each exported frame as either `atlas` or `packed-gfx`
- for `E1L1`, `220` of `809` exported frames now come from decoded `packed-gfx` descriptors rather than the initial VRAM atlas

That means the earlier appearance of raw `rgba5551` in per-rectangle previews was an artifact of VRAM upload interpretation, not proof that those upload rectangles were directly final sprite images.

## Packed-Gfx Descriptor Layout

`FUN_80084a8c` uses the frame lookup index to resolve streamed graphics like this:

1. read the packed-gfx group table header
2. select the group by `textureGroupIndex`
3. check whether the runtime lookup index falls in that group's `[lookupStart, lookupStart + lookupCount)` range
4. use the per-frame selector-byte table to choose one descriptor slot
5. read the selected descriptor offset from the group's dword offset table
6. decode the descriptor's RLE byte stream into a temporary `indexed8` frame image

Current decoded group layout:

- group table header: dword offsets, count = `firstDword / 4`
- per-group header at `groupOffset`:
  - `+0/+1`: still unresolved
  - `+2`: `lookupStart`
  - `+4`: `lookupCount`
  - `+6`: `descriptorOffsetCount`
- `groupOffset + 8`: dword descriptor-offset table
- `groupOffset + 8 + descriptorOffsetCount * 4`: per-frame selector-byte table, indexed by `lookupIndex - lookupStart`

Current decoded descriptor layout:

- `+0`: width byte
- `+1`: height byte
- `+2/+3`: still unresolved
- `+4...`: RLE token stream

Current decoded RLE rules:

- token low 6 bits = palette index value
- token high 2 bits = short run length when non-zero (`1..3`)
- token high 2 bits = `0` means long run; the next byte is the run length
- values are still taken from the token low 6 bits even for long runs
- the game clips the final run when the frame rectangle is full; the stream does not need to end exactly on a run boundary

Observed extractor constraint that now holds across `EPISODE1`:

- every frame resolved from `packed-gfx` is currently `indexed8`
- every frame resolved from `packed-gfx` currently has `U = 0`, `V = 0`

## Remaining Palette Risk

The remaining likely color issue is not frame slicing anymore. `FUN_80084a8c` can override the base group CLUT with `DAT_800b2af4[param_2]` when that per-call value is non-zero.

Current caller evidence:

- the simple wrapper at `80084a28` forwards the frame index and leaves the second argument implicit in the current calling context
- the heavier world-sprite draw path at `8006e024` calls `FUN_80084a8c((short)frameIndex, drawState[0x13])`
- that draw-state field is loaded earlier from per-object / per-entity bytes in helper setup code such as `800695a0` and `8006b430`

So palette overrides are not purely frame metadata. They are at least partly driven by the caller's render state.

So the current extractor state is:

- atlas-backed frames: size / UV / tpage / CLUT are derived from the main frame tables
- packed-gfx-backed frames: geometry now comes from the packed descriptor itself and colors still use the current base CLUT path
- remaining suspicious outputs are likely tied to the caller-specific `param_2` palette selection path rather than the packed-gfx descriptor geometry path

## Frame Origin / Alignment Data

Yes, the frame metadata contains per-frame alignment data that can be used to keep animation frames aligned on a larger shared canvas.

Current evidence:

- `DAT_800d6200[frameIndex]` is the per-frame animation / anchor dword
- the byte at bits `8..15` is currently decoded as `AnchorX`
- the byte at bits `16..23` is currently decoded as `AnchorY`
- render setup code such as `8006b430` adds those bytes to the caller/object draw offsets before dispatching the sprite draw helper

That means these anchor bytes are not cosmetic metadata; they are part of the actual runtime placement logic.

The extractor now writes the following additional derived fields for each exported frame:

- `OriginX`
- `OriginY`
- `AlignedLeft`
- `AlignedTop`
- `AlignedRight`
- `AlignedBottom`

The extractor also now emits automatic aligned group exports under `frame-groups/`.

Current grouping heuristic:

- only frames from `packed-gfx`
- same `TextureGroupIndex`
- same `TextureMode`
- additionally split consecutive frames when two short similarly sized crops flip between top-attached and bottom-attached silhouettes, because those states do not share one stable grouped-preview pivot

For each automatic group, the extractor computes a shared canvas from the min/max aligned bounds across all frames in that group, then writes each member frame onto that common canvas with its origin aligned.

Current grouped-preview placement is intentionally split by axis:

- X placement still uses the engine-derived frame origin / anchor math
- Y placement for `frame-groups/` now aligns each exported frame by its lowest visible opaque row instead of the partially understood engine `AnchorY` semantics

That change is specific to grouped preview output. Per-frame metadata still reports the decoded engine-facing `AnchorY`, `OriginY`, `AlignedTop`, and `AlignedBottom` values.

This is not a confirmed engine-level semantic animation id, but for streamed `packed-gfx` assets it is a practical native grouping signal derived from the package structure itself, and it avoids having to hand-maintain frame ranges for most cases.

Current derivation matches the draw-path width/height midpoint math:

- `OriginX = floor(width / 2) + AnchorX`
- `OriginY = floor(height / 2) + AnchorY`
- `AlignedLeft = -OriginX`
- `AlignedTop = -OriginY`
- `AlignedRight = width - OriginX`
- `AlignedBottom = height - OriginY`

The earlier `midpoint - anchor` interpretation had the sign reversed. Rechecking `E2L2` packed-gfx group `texgrp-006` against the exported PNGs showed that the corrected `midpoint + anchor` derivation collapses the turret-support drift from a large horizontal spread down to a stable 1-pixel variation, which matches the runtime helper adding the frame anchor bytes to the caller/object offsets before placement.

Vertical grouped-preview alignment remains different: `E2L2` packed-gfx group `texgrp-001` showed that even after the horizontal fix, using decoded `AnchorY` directly still left a visibly jumpy foot baseline. The current exporter therefore keeps the engine-derived X placement but uses the image's visible bottom row as the grouped-preview Y baseline, which flattens that set to a stable ground line without rewriting the per-frame metadata.

There is one further grouping refinement on top of that baseline rule: `E2L2` `texgrp-005` showed that some packed-gfx ranges still over-group semantically unrelated vertical pivots. In that case `lookup 2370` is a short top-attached crop while `lookup 2371` is a short bottom-attached crop. The exporter now splits that kind of short opposite-orientation pair into separate automatic groups before applying grouped-preview placement.

Practical use:

- if a set of related frames should share one stable origin, choose a common canvas size from the min/max of `AlignedLeft/Top/Right/Bottom` across that set
- then place each sprite so that its local origin lands on the same canvas origin point
- this should preserve the relative frame alignment when switching animation frames, even when per-frame image sizes differ

## Current Blocker

The current Ghidra MCP tool surface does not expose local-variable rename/type operations for stack variables and decompiler temporaries.

That means:

- function names, signatures, and many globals were renamed successfully
- helper semantics are now much clearer
- but some decompiler locals still remain as generated names like `iVar*`, `puVar*`, `local_*`

So this pass is materially improved, but it is not possible to satisfy the stricter requirement of eliminating all decompiler-generated local names using the currently available MCP operations alone.

## Next Step

Use the confirmed loader semantics above to build a C# extractor that:

1. Parses the PMP header.
2. Reads CD/file chunks by offset and packed size.
3. Reimplements `PmpLzDecompress`.
4. Emits raw VRAM/speech/level-data sections for further decoding.
