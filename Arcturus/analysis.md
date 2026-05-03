# Arcturus - Asset Decompression Analysis

## Session Log

### 2026-02-18
- Created analysis workspace for Arcturus.
- Goal: identify and rename `data.pak` loading/decompression related functions and variables in Ghidra.
- Selected Ghidra instance: `ArcExe.exe` (port 8193).
- Confirmed startup loader function builds and mounts:
	- `data.pak`
	- `data1.pak`
	- `data20.pak` ... `data99.pak` (conditional existence check)

## Confirmed Renames (Ghidra)

### Primary targets
- `FUN_0061aa68` -> `BuildFormattedString`
	- Purpose: printf-style path formatter (used with `%sdata%2d.pak`).
- `FUN_0062a04d` -> `CheckFileAccess`
	- Purpose: `access()`-style existence/attribute check via `GetFileAttributesA`.
- `FUN_00467120` -> `MountResourceArchiveByPath`
	- Purpose: open/mount archive source and register archive file table.
- `FUN_005a55d4` -> `GameStartup_LoadInitialArchives`
	- Purpose: startup sequence that resolves path and mounts initial archives.

### Additional related renames
- `FUN_004671d3` -> `RegisterArchiveFileTable`
- `FUN_00467574` -> `OpenArchiveSourceOrNestedEntry`
- `FUN_004683e0` -> `InitFileHandleStream`
- `FUN_00628274` -> `OpenFileWithModeFlags`
- `FUN_0062828b` -> `OpenFileWithModeFlagsImpl`
- `FUN_00468830` -> `DecodeLzssChunkIntoRingBuffer`
- `FUN_00468720` -> `WindowedFileSubstream_Read`
- `FUN_004686c0` -> `WindowedFileSubstream_GetByte`
- `FUN_00467d36` -> `WindowedFileSubstream_Seek`
- `FUN_00467e08` -> `WindowedFileSubstream_Tell`
- `FUN_00467e78` -> `CompressedSubstream_Read`
- `FUN_00467f48` -> `CompressedSubstream_GetByte`
- `FUN_00467fa0` -> `CompressedSubstream_Seek`
- `FUN_0046804e` -> `CompressedSubstream_Tell`
- `FUN_004689f0` -> `FileHandleStream_FillReadBuffer`
- `FUN_004685a0` -> `ArchiveSubstream_FillReadBuffer`

### Global/data rename
- `DAT_00e0285c` -> `g_ArchiveTableListHead`

## Functional Findings

1. `GameStartup_LoadInitialArchives` constructs archive file names using `BuildFormattedString`, then calls:
	 - `CheckFileAccess(path, 0)`
	 - `MountResourceArchiveByPath(path)` if accessible.

2. `MountResourceArchiveByPath` behavior:
	 - Attempts direct open using `OpenFileWithModeFlags(..., 0x8000, 0)`.
	 - If direct open fails, falls back to `OpenArchiveSourceOrNestedEntry` (supports nested archive entries from previously loaded tables).
	 - On success, calls `RegisterArchiveFileTable`.

3. `RegisterArchiveFileTable` parses an archive index/table and links it into `g_ArchiveTableListHead`.

4. Confirmed decompression path:
	 - `CompressedSubstream_Read/GetByte` refill via `DecodeLzssChunkIntoRingBuffer`.
	 - Decoder is LZSS-like with:
		 - 8-bit control flags
		 - literal and back-reference tokens
		 - 0x2000-byte ring window
		 - copy length derived as high nibble + 2.

## Archive Entry Layout (Mapped)

From `RegisterArchiveFileTable`, `OpenArchiveSourceOrNestedEntry`, and stream constructors:

- Footer at EOF-9:
	- `uint32 tableOffset`
	- `uint32 entryCount`
	- `byte footerTag`

- Per-entry serialized data in table:
	- `byte nameLength`
	- `byte compressionType` (`0` = stored/raw, `1` = compressed)
	- `uint32 dataOffset`
	- `uint32 storedSize`
	- `uint32 unpackedSize`
	- `char name[nameLength+1]` (null-terminated)

### Compression token format (LZSS-like)
- Read one control byte, consume bits LSB-first.
- If control bit == 0: read one literal byte.
- If control bit == 1: read 16-bit token (little-endian):
	- `distance = token & 0x0FFF`
	- `length   = (token >> 12) + 2`
	- Copy from `(currentOutPos - distance)` in a 0x2000 ring buffer.

## Code Draft Added

- `ArcturusPakExtractor.cs`
	- Parses archive footer and file table.
	- Supports entry extraction.
	- Implements `DecompressLzssLike` for compression type `1`.
	- Can write index report for verification.

- `TestExtractor.cs`
	- Minimal CLI harness for quick validation against real `.pak` files.

## Remaining validation work

- Run extractor on real `data.pak` and compare extracted file sizes against table metadata.
- Verify whether any entries use compression types other than `0` and `1`.
- Confirm whether all archives (`data.pak`, `data1.pak`, `data20..99.pak`) share identical table format.

## Current Conclusion
- `data.pak` (and numbered variants) are mounted through a stream abstraction.
- Archive tables are registered globally for lookup.
- Compressed entries are decoded by an LZSS-style ring-buffer decoder (`DecodeLzssChunkIntoRingBuffer`).
- The three requested primary functions and their immediate supporting methods now have semantic names/comments in Ghidra.

## Target Functions
- `BuildFormattedString` (was `FUN_0061aa68`)
- `CheckFileAccess` (was `FUN_0062a04d`)
- `MountResourceArchiveByPath` (was `FUN_00467120`)
- Caller context: `GameStartup_LoadInitialArchives` (was `FUN_005a55d4`)

## Notes
- This is a fresh Ghidra project; most auto-generated function names are not semantic.
- Names in this document are hypotheses until validated by control flow and call relationships.

---

## Sprite (.spr) Parsing / Rendering Investigation

### New sprite-related renames (current instance)
- `FUN_00427451` -> `LoadSpriteResourceFromSpr`
- `FUN_00441492` -> `InitMemFileFromStream`
- `FUN_00441563` -> `CMemFile_Read`
- `FUN_0044165a` -> `CMemFile_Seek`
- `FUN_00427e30` -> `SpriteIndexedFrame_Ctor`
- `FUN_00427e60` -> `SpriteRgbaFrame_Ctor`
- `FUN_00427e80` -> `SpriteIndexedFrameList_PushBackOwned`
- `FUN_00427eb0` -> `SpriteRgbaFrameList_PushBackOwned`
- `FUN_0042ab04` -> `Upload32bppFrameToTextureSurface`

### Core findings
`LoadSpriteResourceFromSpr` confirms that `.spr` supports **multiple variants** in one logical format family.

Observed header behavior:
- Reads 6-byte base header.
- Validates magic `"SP"` (`0x5053`).
- Uses a `version`/feature word to gate optional sections.
- Uses a base frame count from header for the primary frame loop.

### Confirmed format branches

1. **Legacy/simple indexed variant** (common case)
	 - Primary frames only.
	 - Per frame:
		 - `uint16 width`
		 - `uint16 height`
		 - `width * height` bytes (8bpp palette indices)
	 - Pixel conversion path uses palette table and writes 16-bit opaque/transparent values.

2. **Palette-override indexed variant** (`version > 0x100`)
	 - Loader seeks to EOF-`0x400` and reads a `0x400` byte palette block (256 * RGBA/RGBX-like entries).
	 - Seeks back to header area and decodes primary indexed frames as above, but with the loaded palette.

3. **Extended mixed variant** (`version > 0x1FF`)
	 - Includes all indexed behavior above.
	- Additional `uint16 extraFrameCount` read after base header (at offset +6).
	- After primary indexed frames, parses a second frame list:
		 - `uint16 width`
		 - `uint16 height`
		 - `width * height * 4` bytes (32-bit pixels)
	 - Each 32-bit frame is passed to `Upload32bppFrameToTextureSurface` and stored in a separate alpha-capable frame container.
	 - Channel order is **not guaranteed ARGB**; conversion code maps bytes dynamically into destination surface masks.

### Why files appear “mixed”
- Some `.spr` contain only the primary indexed block.
- Others contain both primary indexed frames and a secondary RGBA frame block.
- Version bits decide whether palette tail and/or secondary RGBA block are present.

### Practical parser decision tree
1. Read first 6 bytes.
2. Verify magic `SP`.
3. Parse `version` and `primaryCount`.
4. If `version > 0x100`, read palette from EOF-`0x400`, then seek back to frame data start.
5. If `version > 0x1FF`, read `extraCount` at offset +6 and treat primary frame stream start as offset +8.
6. Decode `primaryCount` indexed frames (`w,h,indices`).
7. If `extraCount > 0`, decode `extraCount` RGBA frames (`w,h,rgba32`).

### `.act` companion file findings

Confirmed in `LoadActAnimationScript` (was `FUN_00404a54`):
- `.act` files are a separate animation/control format (magic `"AC"`, 16-byte header read first).
- Loader derives sibling sprite path by replacing extension with `.spr` (constant at `DAT_006637fc` = `".spr"`).
- Script entries reference sprite-frame indices and timing/transform-like blocks, then resolve frame pointers from sprite banks.
- For newer ACT versions, records are larger and include an extra selector field that switches between two sprite frame banks.

Implication for 32bpp decode:
- The `.act` pairing explains **which frames/banks are used and when**.
- It does **not** define raw 4-byte channel order for pixel decode.
- Channel interpretation still comes from sprite upload path + destination surface masks (`Upload32bppFrameToTextureSurface`).

### `.act` and sprite-frame alignment

Yes — `.act` is used to align frames.

Evidence from `LoadActAnimationScript` + `FUN_00404975`:
- Each keyframe record stores at least two position-like integers at the start of the keyframe struct.
- Loader resolves the referenced sprite frame (by frame index and bank selector), then passes that frame's two leading size fields (width/height) into `FUN_00404975`.
- `FUN_00404975` adjusts/normalizes the keyframe coordinates against those dimensions (with rounding/quantization math), then writes corrected values back into keyframe `[0]` and `[1]`.

Interpretation:
- `.spr` provides pixel payload + frame dimensions.
- `.act` provides per-frame placement/anchor behavior and timeline selection.
- Final on-screen alignment is driven by `.act` coordinate data normalized using referenced `.spr` frame size.

### Notes for extractor updates
- Do not assume `.spr` is always only indexed+palette.
- Keep support for dual-stream output:
	- indexed frames (palette-based)
	- 32bpp frames (alpha-capable; likely ABGR byte order in file, then remapped by masks)
- When `version <= 0x100`, fallback/default palette path is used by engine logic.

---

## 3D model format investigation (`.rsm` / `.rsx`)

### Registration / dispatch (resource system)

- `FUN_00461206` (existing generic resource loader)
	- Normalizes extension, resolves resource factory from extension map, then calls virtual load at vtable slot `+8`.
	- This is the common dispatch path that eventually reaches both `.rsm` and `.rsx` loaders.

- `FUN_0042075c`
	- Registers `"model\\" + "rsm"` resource type (constructor target `FUN_00423900`).

- `FUN_00422a42`
	- Registers `"model\\" + "rsx"` resource type (constructor target `FUN_00423d30`).

### Confirmed magic constants

- `DAT_00664128` = `"GRSM"` (used by `FUN_00420b83`)
- `DAT_00664198` = `"GRSX"` (used by `FUN_00422de4`)

### `.rsm` conversion path (GRSM)

- `FUN_00420b83`  **(high-confidence `.rsm` parser / converter)**
	- Initializes memfile reader from stream (`InitMemFileFromStream`, `CMemFile_Read`).
	- Validates 4-byte magic against `"GRSM"`.
	- Reads version/format bytes and branches for legacy/newer layouts.
	- Reads model-level properties and node/material name tables (`0x28`-byte name records).
	- Builds mesh/node objects (`operator_new(0xbc)`, `FUN_00420530`) and face/index buffers.
	- Converts legacy face record layout using `FUN_00423c50` (reads `0x14` then writes canonical `0x18` entry with trailing field zeroed).
	- Resolves texture/material references via global registry (`DAT_006b9ae8`).
	- Links sub-mesh relations by name (`FUN_00421a4c`) and stores in internal containers.

Related helpers used in conversion:
- `FUN_00420530` (mesh/node object init)
- `FUN_00423c50` (legacy face entry normalization)

### `.rsx` conversion path (GRSX)

- `FUN_00422de4`  **(high-confidence `.rsx` parser / converter)**
	- Initializes memfile reader from stream.
	- Uses `GRSX` signature data block (`DAT_00664198`) and reads version bytes.
	- Reads object/frame counts and root name table.
	- Creates per-entry objects (`operator_new(0x58)`, `FUN_00421bf9`).
	- Enforces sentinel boundaries (`0x12345678`) around record blocks (data integrity checks).
	- Reads per-group vertex arrays (`* 0x0c` stride), alternate buffers, and face arrays (`* 0x18` stride).
	- Resolves texture/material name indices via shared registry (`DAT_006b9ae8`).
	- Fixes cross-node links by name after initial load (`FUN_00422d67`).

Related helpers used in conversion:
- `FUN_00421bf9` (rsx node object init)
- `FUN_00421aea` / `FUN_00421b75` (buffer allocators for `0x0c`-stride vector arrays)

### Current interpretation

- `.rsm` (`GRSM`) and `.rsx` (`GRSX`) are both native 3D resource containers in the same loader family.
- `.rsm` path includes explicit compatibility conversion for older face records.
- `.rsx` path appears segment/chunk oriented with sentinel-guarded blocks and post-pass link resolution.

---

## Model export prototype (OBJ + CAST)

Implemented a standalone exporter project:

- `Arcturus/ModelExportTool/Arcturus.ModelExportTool.csproj`
- `Arcturus/ModelExportTool/ArcturusModelParser.cs`
- `Arcturus/ModelExportTool/ModelExporters.cs`
- `Arcturus/ModelExportTool/ModelTypes.cs`
- `Arcturus/ModelExportTool/Program.cs`

### Supported input containers

- `GRSM` (`.rsm`) version `1.x` (tested parser support path up to minor `4` from loader constraints).
- `GRSX` (`.rsx`) version `1.2+` with `0x12345678` block sentinels.

### Export targets

- Wavefront OBJ (`.obj`) for fast geometry/UV validation.
- CAST (`.cast`) using **Cast.NET** package (`2.0.0-alpha`) with mesh + UV layer + face buffers.

### Conversion assumptions currently used

- Face records are interpreted as 0x18-byte entries:
	- `v0 v1 v2` (vertex indices)
	- `t0 t1 t2` (texture-vertex indices)
	- `textureId`, `flags`, `twoSided`, `smoothGroup`
- Texture vertices are treated as `vec3` where UV are stored in **Y/Z** (based on legacy `v1` conversion behavior in loader).
- OBJ export uses optional V flip (enabled by default; can be disabled by CLI).
- CAST export de-indexes `(vertexIndex, texIndex)` pairs into a unified stream so CAST's shared-index face buffer remains correct when vertex and UV indices differ.

### Build

From `Arcturus/ModelExportTool`:

- `dotnet build`

### CLI

- `Arcturus.ModelExportTool <model.rsm|model.rsx> [--obj|--cast|--both] [--out-dir <dir>] [--no-flip-v]`

Default behavior exports both `.obj` and `.cast` next to the source file.

### Validation run (2026-02-19)

Using user-provided samples:

- `data\\model\\charic\\_sphinxu_attack01.rsx`
	- Loaded as model `Mesh05` with `2` meshes.
	- Export succeeded:
		- OBJ: `_sphinxu_attack01.obj`
		- CAST: `_sphinxu_attack01.cast`
	- OBJ quick counts: `v=304`, `vt=680`, `f=528`.

- `data\\model\\2_dmsofa.rsm`
	- Loaded as model `Box02` with `8` meshes.
	- Export succeeded:
		- OBJ: `2_dmsofa.obj`
		- CAST: `2_dmsofa.cast`
	- OBJ quick counts: `v=88`, `vt=254`, `f=118`.
