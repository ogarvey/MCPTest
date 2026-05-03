# Disruptor (SLES_005.35) WAD Analysis

Last updated: 2026-04-20

## Scope

Current focus is the boot-time path around `InitializeEngineAndLoadWadDirectory` (formerly `FUN_8001252c`) to identify how `\WAD.IN;1` is located, read, and later consumed.

## Renames Applied In Ghidra

- `FUN_80020c78` -> `GameMain`
- `FUN_8001252c` -> `InitializeEngineAndLoadWadDirectory`
- `FUN_80012444` -> `InitializeSpuState`
- `FUN_800119dc` -> `FindCdFileLbaByPath`
- `FUN_80011914` -> `ReadCdFileSliceAsync`
- `FUN_80011858` -> `ReadCdFileSliceSync`
- `FUN_800114ac` -> `AllocateMainHeapBlock`
- `FUN_80012d54` -> `StopSpuVoiceSlot`
- `FUN_80014c7c` -> `InitializeSceneRuntimeState`
- `FUN_80014998` -> `LoadCommonUiSpritePackage`
- `FUN_80016a3c` -> `RelocateSceneSubpackageTextures`
- `FUN_80016e44` -> `RelocateSceneObjectPackage`
- `FUN_80017070` -> `CompactLoadedSceneAssetQueue`
- `FUN_80017264` -> `EvictSceneAssetSlot`
- `FUN_8001774c` -> `StreamSceneSubpackages`
- `FUN_800180d4` -> `LoadSceneCharacterPackage`
- `FUN_80019604` -> `LoadSceneEnemyPackage`
- `FUN_80044b1c` -> `CopyBufferWords`
- `FUN_80045a10` -> `ApplyObjectDeltaTransform`
- `FUN_80045fe4` -> `DecodePackedImageToBuffer`

## Global Names Applied In Ghidra

- `DAT_80071804` -> `gWadBaseLba`
- `DAT_800779f0` -> `gMainHeapBase`
- `DAT_800779fc` -> `gCharacterPackageBuffer`
- `DAT_80077a10` -> `gPackedImageDecodeBuffer`
- `DAT_800716c8` -> `gMainHeapLimit`
- `DAT_80071a34` -> `gCurrentCharacterPackage`
- `DAT_8007794f` -> `gRequestedCharacterPackageIndex`
- `UNK_80075b50` -> `gStageCharacterPackageOffsetTable`
- `DAT_80075b70` -> `gWadDirectoryTable`

## Findings So Far

### 1. `InitializeEngineAndLoadWadDirectory` is a boot initializer, not a decompressor

This function performs broad system setup before it touches `WAD.IN`:

- GPU/display environment setup via `ResetGraph`, `SetDefDrawEnv`, `SetDefDispEnv`, `SetDispMask`
- event creation/enabling
- memory card and pad initialization
- SPU setup via `InitializeSpuState`
- CD-ROM initialization and callback setup
- main heap/work buffer allocation via `AllocateMainHeapBlock(0x184000)`

This is clearly a startup/bootstrap routine called once from `GameMain` immediately after `__main()`.

### 2. `WAD.IN` is resolved as a normal CD file and stored as a base LBA

The key sequence is:

1. `FindCdFileLbaByPath("\\WAD.IN;1")`
2. store result in `gWadBaseLba`
3. `ReadCdFileSliceSync(gWadBaseLba, gMainHeapBase, 0x654, 0)`

`FindCdFileLbaByPath` wraps:

- `CdSearchFile`
- `CdPosToInt`

So the function returns the starting logical block address of the ISO9660 file.

### 3. `ReadCdFileSliceSync` reads byte ranges from inside a CD file

`ReadCdFileSliceSync(baseLba, dest, byteCount, fileOffset)` does the following:

- validates `dest + byteCount < gMainHeapLimit`
- converts `baseLba + (fileOffset >> 11)` to `CdlLOC`
- issues `CdControl(2, ...)`
- computes `sectorCount = (byteCount + 0x7ff) >> 11`
- calls `CdRead`
- blocks until the CD callback clears the in-progress flag

This means later calls are not opening separate files. They are reading slices within a larger file whose base LBA is `gWadBaseLba`.

### 4. The first `0x654` bytes of `WAD.IN` behave like a directory table

After reading `0x654` bytes from `WAD.IN` into `gMainHeapBase`, the routine copies those bytes into `gWadDirectoryTable`.

The first consumer found so far is `FUN_80014998`, which does:

- `ReadCdFileSliceSync(gWadBaseLba, DAT_80077a18, DAT_80075b74, DAT_80075b70)`

Given the sync-read signature, this means:

- `gWadDirectoryTable + 0x0` is used as a byte offset within `WAD.IN`
- `gWadDirectoryTable + 0x4` is used as the byte count to read

That is strong evidence the copied block is a resource directory, most likely a packed array of `(offset, size)` entries.

### 5. The directory is used for subresource streaming, not full-file decompression

Multiple later systems reuse `gWadBaseLba` with different offsets and lengths. This strongly suggests:

- `WAD.IN` is a packed container file
- the startup routine loads a small fixed metadata block from its beginning
- later code streams individual assets by offset/size from that metadata

At this stage, no decompression has been observed in `InitializeEngineAndLoadWadDirectory` itself.

### 6. `FUN_800153b4` loads a compound WAD package and rebases internal offsets

Another important downstream consumer is `FUN_800153b4`.

Observed behavior:

- reads a large block from `WAD.IN` using an offset table at `DAT_80075df8 + index * 4`
- uploads portions of the loaded data directly to VRAM with `LoadImage`
- reads a second block immediately after the first
- walks many tables inside the loaded data and converts file-relative offsets into RAM pointers by adding the base buffer address
- computes GPU `TPage` values for embedded image structures
- transfers an audio region to SPU RAM with `SpuRead`

This looks like structured package loading plus relocation/fixup, not decompression. That pushes the likely decode/decompress boundary further downstream into resource-specific routines.

### 7. `DecodePackedImageToBuffer` is the first confirmed asset decode stage

The first clearly non-trivial decode routine on the `WAD.IN` path is now identified as `DecodePackedImageToBuffer` (formerly `FUN_80045fe4`).

What it does:

- clears the destination buffer before decoding
- treats the source object as a small header followed by three auxiliary data substreams and a control stream
- consumes signed control bytes
- negative control bytes advance the destination pointer without writing, which effectively encodes skips/transparent runs
- non-negative control bytes choose a destination offset/alignment mode, copy literal word data, and dispatch into one of several decode variants via an internal jump table

Why this matters:

- callers do not invoke CD routines here, so this is post-stream decoding of data already loaded from `WAD.IN`
- callers pass the decoded output directly to `LoadImage`, so this routine is reconstructing GPU-ready image/texture data
- this is the first strong candidate for the game's real image extraction/decoding logic rather than just package streaming

Current interpretation of the packed source layout passed to `DecodePackedImageToBuffer`:

- `param_1[0]`: offset from the header to auxiliary stream A
- `param_1[1]`: size of auxiliary stream A
- `param_1[2]`: size of auxiliary stream B
- control stream starts at `param_1 + 3`
- stream A starts at `(param_1 + 3) + param_1[0]`
- stream B starts after stream A
- stream C starts after stream B

The exact semantics of the internal decode variants are still open, but the format is no longer just a flat `(offset, size)` package payload. At least some image assets use a compact custom packed representation.

### 7a. Packed image chunk layout is now constrained enough for an extractor

The packed image chunk passed to `DecodePackedImageToBuffer` now looks stable enough to model in C#.

Observed chunk layout:

- dword `+0x00`: offset from the end of the 12-byte header to auxiliary stream A
- dword `+0x04`: byte length of auxiliary stream A
- dword `+0x08`: byte length of auxiliary stream B
- bytes `+0x0c ...`: control-byte stream
- auxiliary stream A begins at `chunk + 0x0c + header[0]`
- auxiliary stream B begins immediately after stream A
- literal/main data stream begins immediately after stream B

Operational model inside `DecodePackedImageToBuffer`:

- clear the entire output buffer first
- read signed control bytes from the control stream
- `0xff` ends the decode
- negative control bytes advance the destination pointer by `control & 0x7f` without writing anything
- the first non-negative control byte advances the destination pointer and also selects one of four byte-alignment modes via `control & 3`
- the next control byte splits into two fields:
	- low 2 bits: extra trailing dword copies
	- high 6 bits: literal-copy count and variant selector
- before the variant handler runs, the decoder copies `(control2 & 0x3f) * 4` bytes from the literal/main data stream into the destination
- the top 2 bits of the second control byte select one of four inline decode handlers at `0x80046318`

Alignment handling is important:

- mode 0: no prefix bytes are seeded before the main literal copy
- mode 1: seed 3 prefix bytes from auxiliary stream A, then continue aligned copying
- mode 2: seed 2 prefix bytes from auxiliary stream B, then continue aligned copying
- mode 3: seed 1 prefix byte from auxiliary stream A, then continue aligned copying

This makes the format look less like general-purpose compression and more like a compact row-byte reconstruction scheme that uses multiple side streams to repair alignment and fill runs efficiently.

The four inline variant handlers at `0x80046318` are now partially resolved:

- variant 0: no extra side-stream bytes are inserted; return directly to the next control byte
- variant 1: copy 1 byte from stream A lane data (`streamA + 3`, then advance by 4) into the current destination group, then advance destination by 4
- variant 2: copy 2 bytes from stream B into the current destination group, then advance destination by 4
- variant 3: copy the low 24 bits from stream A into the current destination group, then advance destination by 4

This is a much stronger result than the earlier “unknown jump table” hypothesis. The table is effectively a 4-way lane-fill selector for 32-bit output groups:

- 0-byte repair
- 1-byte repair
- 2-byte repair
- 3-byte repair

That implies the packed image format stores output in split lanes:

- the main/literal stream supplies fully aligned dwords
- stream A supplies 1-byte and 3-byte repairs
- stream B supplies 2-byte repairs

In other words, the format appears to be optimizing sparse byte-lane occupancy inside 32-bit image rows, not performing a heavyweight entropy decode.

### 7b. Caller-side descriptor semantics for images

The main high-level caller at `FUN_8004363c` constrains the image descriptor format enough to start parsing it directly.

Observed caller behavior:

- the descriptor entry begins with a pointer to the packed image chunk passed to `DecodePackedImageToBuffer`
- the caller computes destination buffer size as `rowBytes * height`
- after decode, the caller uploads with `LoadImage`
- the upload rectangle width is `rowBytes >> 1`
- the upload rectangle height is `height`

That means the packed decoder output is already laid out as raw 16-bit PSX image data:

- `rowBytes` is the byte width of the decoded image plane
- `LoadImage` receives width in 16-bit words, hence `rowBytes >> 1`

There also appears to be an optional second image plane or companion image:

- one image is decoded to `DAT_80077a10`
- if a secondary descriptor index is not `0xff`, a second packed image is decoded immediately after the first image buffer, aligned to a 4-byte boundary
- that second decoded block is uploaded to a second VRAM rectangle

This is a strong hint that some image records contain either:

- paired texture pages
- image + mask data
- image + CLUT/overlay companion data

The exact semantic meaning of the second image is still open, but extractor support should preserve it rather than discarding it.

### 7c. The packed images live inside the relocated character package

`LoadSceneCharacterPackage` now gives a usable package-level view of where the image descriptors come from.

Current working layout of `gCurrentCharacterPackage`:

- the package is streamed into `gCharacterPackageBuffer`
- after loading, it is treated as a live package rooted at `gCurrentCharacterPackage`
- relocation converts several file-relative offsets into live pointers by adding the package base plus `0x88`
- this strongly suggests a fixed package header of `0x88` bytes followed by variable data blocks

Current header sketch:

- dword `+0x00`: pointer to primary image descriptor table
- dword `+0x04`: pointer to secondary image descriptor table
- dword `+0x08`: pointer to texture/CLUT descriptor table
- dword `+0x0c`: pointer to render-group/object table
- dword `+0x10`: pointer to audio/SPU voice table
- dword `+0x14`: relocation bias used while fixing primary image descriptor entries
- dword `+0x18`: relocation bias used while fixing secondary image descriptor entries
- dword `+0x24`: primary image descriptor count, with entry size `0x14`
- dword `+0x28`: secondary image descriptor count, with entry size `0x0c`
- dword `+0x2c`: texture/CLUT descriptor count, with entry size `0x0c`
- byte `+0x7d`: current primary image descriptor index selected for rendering
- byte `+0x7e`: current frame timer/countdown for the active primary descriptor
- byte `+0x81`: render-group count, with entry size `0x38`

Source of this package on disc:

- `LoadSceneCharacterPackage` reads the first `0x11000` bytes from `WAD.IN` into `gCharacterPackageBuffer`
- the byte offset comes from `gStageCharacterPackageOffsetTable`
- the lookup formula is:
	- stage group: `((DAT_80071838 + 1) >> 1)`
	- character slot: `gRequestedCharacterPackageIndex`
	- table stride: `0x28` bytes per stage group
	- entry size: `4` bytes per package offset
- after the first block is present, the loader reads a second block of length `header[0x0d]` from `fileOffset + 0x11000` into `buffer + 0x88`

So the on-disc character image package is not a single flat blob for extraction purposes. It is loaded in two phases:

- phase 1: fixed `0x11000` bytes
- phase 2: variable continuation whose size is recorded inside the package header

That is the current best candidate package family for extracting rendered character images.

Primary image descriptor table observations:

- entry size is `0x14`
- first dword is a relative offset to the packed image chunk, rebased through `header[5]`
- byte `+0x07` is `rowBytes`
- byte `+0x08` is `height`
- byte `+0x0c` seeds the runtime frame timer/countdown stored at package `+0x7e`
- byte `+0x0d` is a frame-control/transition mode byte used by the animation update path
- byte `+0x0e` is checked before calling the effect-spawn path at `FUN_8002d9cc`
- byte `+0x10` is passed to `FUN_80012a20` as an event/sound identifier
- byte `+0x12` is the real companion-image selector used by `FUN_8004363c`; `0xff` means no companion image

Secondary table observations:

- entry size is `0x0c`
- first dword is also a relative offset to a packed image chunk, rebased through `header[6]`
- byte `+0x07` is `rowBytes`
- byte `+0x08` is `height`
- `FUN_8004363c` decodes this table as the active primary descriptor's companion image when primary byte `+0x12 != 0xff`
- in the validated package, only primary descriptor `1` references companion entry `0`

Correction from runtime tracing:

- `LoadSceneCharacterPackage` seeds package byte `+0x7e` from primary descriptor byte `+0x0c`
- `FUN_80032638` decrements package byte `+0x7e` every update and uses it as a frame timer
- the muzzle-flash/effect path is still triggered from the active primary descriptor via `FUN_8002d9cc`, which consumes render-group data from package dword `+0x0c`
- that does not replace the companion-image path: `FUN_8004363c` separately decodes and uploads a secondary-table image selected by primary byte `+0x12`
- the earlier conclusion that `secondary_00.png` was not a real image asset was incorrect

This is enough to start a concrete extractor model:

- parse the character package header
- enumerate `primaryCount` entries of size `0x14`
- decode each primary packed image chunk
- if `companionIndex != 0xff`, decode the matching secondary table entry too
- export each decoded plane as raw 16-bit PSX image data, then convert or visualize later

### 7d. Real WAD-backed validation on a concrete package

Using the real `Disruptor/WAD.IN`, a strong candidate character package was validated at file offset `0x0089D800`.

Its header is:

- primary table pointer: `0x00000000`
- secondary table pointer: `0x00000064`
- texture table pointer: `0x00000070`
- render-group table pointer: `0x0000031c`
- audio table pointer: `0x00000354`
- primary chunk bias: `0x00000414`
- secondary chunk bias: `0x00002460`
- primary count: `5`
- secondary count: `1`
- texture count: `1`
- continuation size (`header[0x0d]`): `0x25d4`

The first primary image descriptor in this package resolves as:

- descriptor entry offset: `0x0000`
- relative chunk offset: `0x0000`
- actual packed chunk offset inside continuation: `0x0414`
- `rowBytes = 0x52`
- `height = 0x43`

The packed chunk header at continuation offset `0x0414` is:

- control-to-streamA offset: `0x000000a0`
- stream A byte length: `0x00000098`
- stream B byte length: `0x00000050`

That matches the inferred `DecodePackedImageToBuffer` chunk format closely enough to treat the current image path as validated, not just hypothesized.

### 7e. Decoder prototype result against the real chunk

A direct prototype of the inferred packed-image decoder was run against the first primary chunk from the validated package above.

Observed result:

- descriptor dimensions: `rowBytes = 0x52`, `height = 0x43`
- nominal decoded size: `0x52 * 0x43 = 5494` bytes
- decoder control events processed: `79`
- control stream bytes consumed: `159 / 160`
- stream A/B/main usage stayed in bounds
- final logical write position ended at `5496`, which is `rowBytes * height` rounded up to the next 4-byte boundary

This is a practical extractor detail:

- the semantic image size is still `rowBytes * height`
- but the scratch buffer used during decode should be rounded up to a dword boundary with `((rowBytes * height) + 3) & ~3`

### 7f. First real plane dumps

A first extractor script now exists at `Disruptor/extract_character_package_images.py`.

Validated run against package `0x0089D800` produced:

- `Disruptor/dumped_planes/package_0089D800/primary_00.raw`
- `Disruptor/dumped_planes/package_0089D800/primary_00.ppm`
- `Disruptor/dumped_planes/package_0089D800/primary_02.raw`
- `Disruptor/dumped_planes/package_0089D800/primary_02.ppm`
- `Disruptor/dumped_planes/package_0089D800/secondary_00.raw`
- `Disruptor/dumped_planes/package_0089D800/secondary_00.ppm`
- `Disruptor/dumped_planes/package_0089D800/metadata.json`

Current results from that package:

- primary descriptor `0` decodes cleanly (`rowBytes=0x52`, `height=0x43`)
- primary descriptor `2` decodes cleanly (`rowBytes=0x54`, `height=0x4b`)
- secondary descriptor `0` decodes cleanly (`rowBytes=0x1a`, `height=0x14`)
- primary descriptor `1` currently fails partway through decode with a side-stream bounds error, even with a larger scratch buffer

That failure is useful evidence:

- the basic package family and chunk format are correct
- but at least one primary descriptor entry in this family still uses a slightly different decode case or descriptor interpretation than the current model

Update after fixing the decoder:

- `primary_01` now decodes cleanly
- the root cause was in the extractor, not the package data
- mode 1 and mode 2 prefix handling should use fixed prefix bytes from the start of stream A / stream B
- those prefix bytes are not consumed each time the mode occurs
- only the lane-fill variants consume the rolling stream positions

Current successful outputs from package `0x0089D800` are now:

- `primary_00.raw` / `primary_00.ppm` / `primary_00.png`
- `primary_01.raw` / `primary_01.ppm` / `primary_01.png`
- `primary_02.raw` / `primary_02.ppm` / `primary_02.png`
- `primary_03.raw` / `primary_03.ppm` / `primary_03.png`
- `primary_04.raw` / `primary_04.ppm` / `primary_04.png`
- `secondary_00.raw` / `secondary_00.ppm` / `secondary_00.png`

Additional observation:

- `primary_03` and `primary_04` point at the same packed chunk as `primary_00`
- their descriptor metadata differs slightly, but the chunk offset and decoded output are currently identical

### 7g. Current color issue: decoded shapes are plausible, but many planes are likely indexed

The current evidence says the geometry/shape side of the image decode is broadly correct, but the color interpretation is still only partially solved.

What is now validated:

- the direct packed-image decode produces stable image planes with plausible silhouettes
- the resulting primary planes have byte distributions more consistent with indexed texture data than with arbitrary 16-bit color words
- `primary_01` can be rendered as an 8bpp indexed texture using `secondary_00` as a 256-color palette source, producing a more plausible visual result than the raw 16-bit interpretation

Likely palette sources currently exposed by the extractor:

- first-stage package CLUT row 0 from the initial `0x11000`-byte package block
- first-stage package CLUT row 1 from the initial `0x11000`-byte package block
- `secondary_00` treated as a 256-entry 16-bit palette plane

The extractor now emits all of these as alternate preview PNGs for each successfully decoded primary plane.

Current working interpretation:

- many primary planes are probably 8bpp indexed textures
- at least some palette data lives outside the packed primary image chunk itself
- the remaining color work is mostly palette selection / CLUT mapping, not packed-image bitstream reconstruction

Additional character-package findings from the validated `0x0089D800` sample:

- the render-group texture descriptors in this package are consistently flagged as 8bpp (`descriptor[0x0d] == 1`)
- for that 8bpp render-group path, `LoadSceneCharacterPackage` builds TPage with `GetTPage(1, abr, ...)`
- the same path uses a fixed sprite CLUT location via `GetClut(0x200, 0x4c)` rather than using the descriptor-local CLUT bytes directly
- that fixed CLUT row is not copied raw from the first package block; it is generated from the palette block at first-stage offset `0x488` by `FUN_80016980`, which calls `FUN_8004501c` to expand a 256-color base palette into a multi-row ramp before uploading it to VRAM at `(0x200, 0x4c)`
- this explains why the earlier `firstblock_row0` previews looked close but not definitively correct: they were palette guesses, not the exact runtime-generated CLUT row

Transparency status:

- transparent pixels are also part of the missing visual fidelity; treating every decoded plane as opaque RGB was incorrect for inspection purposes
- extractor previews now include RGBA outputs that treat PSX color `0x0000` as fully transparent and preserve bit 15 as partial alpha in preview form
- exact in-game translucency still depends on GPU semi-transparency state (`ABR`) and polygon flags, so RGBA previews are an inspection aid rather than a perfect final renderer

### 7h. Image asset identification model for character packages

The extractor is now using a stricter image-identification model based on cross-referencing the package bytes with `LoadSceneCharacterPackage` in Ghidra, rather than treating every primary descriptor as an independent image asset.

Current identification rules for the validated package at `0x0089D800`:

- the runtime primary selector table lives at package header offset `0x78`
- `LoadSceneCharacterPackage` explicitly reads `package[0x78 + slot]` to choose a primary descriptor index
- after choosing that primary descriptor, the loader reads byte `+0x0c` from the selected primary entry as a second-stage selector/auxiliary index
- therefore, primary descriptors are not automatically unique assets; some are aliases that point at the same packed image chunk but carry different selector metadata

Validated result for `0x0089D800`:

- selector table bytes are `[0, 1, 3, 4]`
- the loader writes selector slot `2` as the default active slot during package setup, so the default active primary descriptor is `3`
- primary descriptors `0`, `3`, and `4` all point at the same packed image chunk (`chunk_offset = 0x414`) and should currently be treated as one unique primary image asset with alias descriptors
- primary descriptor `1` is its own unique image asset (`chunk_offset = 0xD3C`)
- primary descriptor `2` is its own unique image asset (`chunk_offset = 0x1A08`), but it is not referenced by the validated package's runtime selector table

This means the validated sample currently contains:

- `3` unique primary image assets
- `1` companion image asset in the secondary table, referenced by primary descriptor `1`
- `5` primary descriptors total, of which `3` are aliases of the same decoded chunk

Practical extractor consequence:

- output should preserve descriptor-level dumps for debugging, but also emit a manifest of unique assets so downstream tooling does not mistake selector aliases for separate images
- default extraction should emit final PNGs for both primary images and secondary companion images
- the extractor now writes `assets_manifest.json` alongside `metadata.json` for this purpose

Extractor workflow update:

- default runs are now asset-first rather than debug-first
- package output folders are cleaned before extraction so stale incorrect previews from earlier experiments do not accumulate
- default extraction now writes canonical image assets under an `assets/` subfolder, plus `metadata.json` and `assets_manifest.json`
- descriptor-level raw/PPM/PNG outputs and palette-guess preview images remain available only in explicit debug mode

### 7i. Broader character-package candidate scan in `WAD.IN`

The extractor now includes a candidate-scan mode that searches `WAD.IN` for likely character-package headers and validates them by attempting to decode the first primary image chunk.

Current validated candidate offsets found in `Disruptor/WAD.IN`:

- `0x0089D800`
- `0x018FB800`
- `0x036A8800`
- `0x05099800`
- `0x07002000`
- `0x079B3800`
- `0x09308800`
- `0x0A25E800`

These are now the best next targets for checking whether the selector-table aliasing and unique-asset grouping rules hold across the wider package family.

### 8. Current extraction pipeline hypothesis

The current end-to-end model is:

1. `InitializeEngineAndLoadWadDirectory` finds `\WAD.IN;1`, stores its base LBA, and copies the first `0x654` bytes into `gWadDirectoryTable`.
2. Later systems treat entries in that table as byte offsets and lengths inside the container.
3. `ReadCdFileSliceSync` streams either whole serialized packages or smaller standalone payloads into RAM.
4. Some packages are used directly after pointer rebasing and table fixups.
5. Packed image sub-assets inside those packages are decoded by `DecodePackedImageToBuffer` into a scratch buffer before being uploaded to VRAM.

So the game appears to extract assets from `WAD.IN` in two layers:

- container extraction: offset/size reads from the WAD directory
- asset decoding: format-specific reconstruction, at least for images, after the raw bytes are already in RAM

### 9. Scene objects and animation use streamed subpackages plus relocation, not one-shot decode

The next major asset path after raw WAD streaming is the scene subpackage system:

- `StreamSceneSubpackages`
- `CompactLoadedSceneAssetQueue`
- `RelocateSceneObjectPackage`
- `RelocateSceneSubpackageTextures`
- `EvictSceneAssetSlot`

What this path does:

- asynchronously streams additional WAD slices using per-scene offset tables
- keeps multiple loaded slices in a movable RAM queue
- compacts/defragments that queue when needed
- reruns relocation after moves so internal pointers remain valid
- registers loaded object packages into runtime lookup tables
- patches texture descriptors with `GetTPage` and `GetClut`
- applies object delta transforms through `ApplyObjectDeltaTransform`

This strongly suggests the game stores at least some scene content as serialized object/animation packages with file-relative pointers. For extraction purposes, these packages look more like relocatable archives than compressed bitstreams.

### 10. Probable 3D/object path

`RelocateSceneObjectPackage` is the strongest current indicator of 3D/object data:

- it rebases many pointers inside a loaded block
- it processes up to `0x40` object-like records
- each record can own multiple pointer tables and up to `0x10` further references
- it registers objects into runtime lookup tables by index/slot
- it conditionally calls `ApplyObjectDeltaTransform`, which adjusts coordinate-like arrays using packed delta descriptors

`ApplyObjectDeltaTransform` appears to modify position/vertex-like data arrays in-place using packed delta values and linked records. That makes it a strong candidate for object pose/attachment adjustment rather than image or audio handling.

Current working hypothesis:

- 3D/object content is embedded in relocatable scene packages
- those packages contain geometry or object records plus texture references
- some animation/attachment state is applied by data-driven delta transforms instead of a general-purpose decompressor

### 11. Audio path currently looks raw, likely PSX ADPCM/VAG-style

Both `LoadSceneCharacterPackage` and `LoadSceneEnemyPackage` perform direct SPU transfers:

- `SpuRead(..., 0x7800)` in `LoadSceneCharacterPackage`
- `SpuRead(..., 0x8000)` in `LoadSceneEnemyPackage`

Important observation:

- there is no obvious software decode step before these transfers
- the loaders primarily relocate package pointers, upload VRAM data, and then push fixed-size audio blocks straight into SPU RAM

That strongly suggests these package audio payloads are already in a PSX-native sound format, most likely SPU ADPCM / VAG-style data, rather than a custom compressed format requiring CPU-side decompression.

For the future C# extractor, this is encouraging: image extraction likely needs custom decode logic, but audio extraction may only require locating and exporting the raw SPU-compatible blocks with minimal transformation.

### 12. Character/enemy package loaders are composite package readers

`LoadSceneCharacterPackage` and `LoadSceneEnemyPackage` do not look like generic object loaders. Instead they appear to load composite package types that contain:

- one or more image/CLUT blocks uploaded to VRAM
- descriptor tables rebased into live pointers
- a raw SPU audio block
- runtime state used by the current scene/game mode

The exact semantic distinction between “character” and “enemy” is still provisional, but both are clearly package families rather than isolated files.

## Extractor-Oriented Notes

If the goal is a compact C# extractor, the code path now appears to split into at least four asset-handling strategies:

1. Directory-driven container extraction
	Use the boot-loaded WAD directory to resolve offset/size pairs and read raw slices from `WAD.IN`.

2. Packed image decoding
	Implement `DecodePackedImageToBuffer` semantics for custom packed image chunks before converting them to standard bitmap output.

	Current minimum viable image extractor model:

	- parse the character package header and resolve the primary/secondary image descriptor tables
	- read the packed chunk relative offset and apply the appropriate blob bias (`header[5]` for primary, `header[6]` for secondary)
	- determine decoded plane size as `rowBytes * height`
	- allocate a scratch buffer rounded up to a 4-byte boundary
	- run the packed decoder into a temporary buffer using 4-byte group reconstruction:
	  - apply skip controls
	  - seed 0-3 fixed prefix bytes based on destination alignment mode
	  - copy full dwords from the literal/main stream
	  - apply the selected lane-fill variant from the inline table
	- treat the decoded result as raw 16-bit PSX image data ready for VRAM upload
	- if a secondary image descriptor is present, decode and export that companion plane too

3. Relocatable scene package parsing
	Parse scene/object packages by rebasing internal offsets to local slice-relative pointers, then enumerate embedded object/texture/animation-like records.

4. Raw SPU audio export
	Extract SPU blocks from package payloads directly; later determine whether they can be emitted as raw VAG/ADPCM streams or need a small wrapper format.

## Variable Semantics Inside `InitializeEngineAndLoadWadDirectory`

The current Ghidra MCP surface exposed function/data renaming, but not local-variable renaming. Current variable meanings are:

- `local_18[0] = 0x80`: parameter block passed to `CdControl(0x0e, ...)` during CD setup
- `puVar8`: source pointer while copying the initial `0x654` bytes from `gMainHeapBase`
- `puVar9`: destination pointer into `gWadDirectoryTable`
- `puVar10`: end pointer for the copy loop (`gMainHeapBase + 0x194` dwords = `0x650` bytes, followed by final 4-byte copy)
- `uVar4/uVar5/uVar3/uVar7`: temporary word values used by the aligned/unaligned copy logic
- `puVar6`: reused scratch pointer/value, first as heap base and later in the unaligned copy path

The copy logic is not semantic game logic. It is compiler-generated handling for a possibly unaligned memcpy-like copy into `gWadDirectoryTable`.

## Current Interpretation Of Memory Layout Established In Boot

- `gMainHeapBase = AllocateMainHeapBlock(0x184000)`
- `gMainHeapLimit = gMainHeapBase + 0x184000`
- several fixed sub-buffers are carved from this region at hard-coded offsets
- `gWadBaseLba` holds the sector index of `\WAD.IN;1`
- `gWadDirectoryTable` receives the first `0x654` bytes of the file for later indexing

## Enemy package extraction notes

Recent validation against `LoadSceneEnemyPackage` and the real `WAD.IN` enemy packages shows that the ad hoc atlas previews were mixing package stages:

- the first-stage enemy package block is `0x9000` bytes and contains the raw atlas bytes plus the base palette block used to build the fixed enemy CLUT
- the enemy atlas preview bytes currently come from first-stage offset `0x864`, size `0x8000`, which is a `0x200 x 0x40` indexed texture sheet
- the palette block used by the 8bpp enemy path currently appears at first-stage offset `0x264`, size `0x200`
- the header dwords at `+0x00` and `+0x04` are not offsets into the first-stage block; they are offsets into the continuation block loaded from `fileOffset + 0x9000`
- for the validated enemy package at `0x00984800`, the continuation length is `0x2e8`, the texture descriptor table is at continuation offset `0x000`, and the render-group table is at continuation offset `0x1f0`
- the validated `0x00984800` texture descriptor table contains `12` entries of size `0x10`; the visible coordinate pairs are `(376,0)`, `(410,0)`, `(444,0)`, `(478,0)`, then the same `x` positions at `y=21` and `y=42`
- the validated `0x00984800` render-group table contains `1` entry of size `0x38`, whose record list begins at continuation offset `0x0c0` and contains `19` records of size `0x10`
- the sampled render records are consistently flagged as `descriptor[0x0d] == 1`, matching the 8bpp path that uses `GetTPage(1, abr, ...)`
- those same sampled enemy render records do not carry varying CLUT coordinates in the active path; they currently look like fixed-CLUT sprites, which matches the loader path that uses a generated enemy CLUT rather than the descriptor-local CLUT bytes
- this means the earlier raw `enemy_preview` atlas PNGs are useful for locating texture data, but not authoritative for final color validation; correct extraction needs to follow the continuation-based render-group records and then apply the runtime CLUT selection rules

Practical consequence:

- enemy extraction should be driven from the continuation render-group records, not from a whole-atlas palette guess
- a first-pass extractor script now exists at `Disruptor/extract_enemy_package_images.py` to dump those render-group crops directly, while the exact `FUN_80016980`/`FUN_8004501c` CLUT-ramp recreation is still being refined

## Open Questions

- Confirm the exact record format inside `gWadDirectoryTable`
- Determine whether all entries are simple `(offset, size)` pairs or whether some include flags/type fields
- Determine whether `DecodePackedImageToBuffer` is best described as a decompressor, planar unpacker, or command-stream image reconstructor
- Determine the exact meaning of the optional second decoded image plane in the caller at `FUN_8004363c`
- Confirm the remaining unknown fields inside the primary `0x14`-byte image descriptor entry
- Validate whether the variant handlers repeat over a row or are consumed once per control event in all observed images
- Confirm whether all packed images use the same descriptor layout or whether there are small per-table variants
- Determine the exact semantic meaning of the primary descriptor byte at `+0x0c`, since one validated package uses values larger than the current secondary table count
- Determine the semantic difference between descriptors `primary_00`, `primary_03`, and `primary_04`, which currently decode to the same chunk
- Determine whether `DAT_80075df8` is a second-level offset table for stage or scene packages
- Identify additional non-image decoders, if any, for audio, animation, or map/script payloads
- Confirm the exact structure of the streamed scene subpackages handled by `StreamSceneSubpackages`
- Determine which relocated records in `RelocateSceneObjectPackage` correspond to geometry, animation tracks, attachments, or scripts
- Confirm the exact on-disc/SPU format of the raw audio blocks copied by `LoadSceneCharacterPackage` and `LoadSceneEnemyPackage`

## Recommended Next Targets

1. Inspect more xrefs to `gWadDirectoryTable` and map which directory entry index each consumer uses.
2. Resolve more validated package candidates into families and see which ones the runtime stage/package tables actually select.
3. Run the manifest-driven extraction across the newly found candidate package set and compare unique asset counts, selector tables, and alias behavior package-to-package.
