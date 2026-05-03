# Bloodnet Analysis

This document tracks the analysis of the game Bloodnet, focusing on asset loading and decompression logic.

## Overview

Bloodnet is a DOS game that uses a sophisticated asset loading system based on .PL (presumably "Palette" or "Packed Library") archive files. The game employs DOS interrupt 0x21 for file operations and implements a multi-layered architecture for asset processing.

## Key Discoveries

### DOS File I/O Chain
The low-level DOS file helpers are now verified against Ghidra disassembly:

- `22f2:004e` uses `INT 21h`, `AX=3D00h` to open a file read-only
- `22f2:0016` uses `INT 21h`, `AH=3Fh` to read from a file handle
- `22f2:0096` uses `INT 21h`, `AH=42h` to seek
- `22f2:00b4` uses `INT 21h`, `AX=4201h`, `CX=DX=0` to query current position
- `22f2:00fa` uses `INT 21h`, `AH=3Eh` to close a handle

The previously documented `28f7:*` "ReadFile" and "ProcessNextDataChunk" chain is **not** supported by the code. Those functions are unrelated runtime/UI logic, not archive I/O.

### .PL File Format
The game uses .PL archive files with the following structure (as specified by user):
- **2 bytes**: Number of images contained (ushort)
- **4 bytes**: Offset to file table (uint)
- **File table**: Array of 12-byte entries, each containing:
  - 4 bytes: Offset to file data
  - 8 bytes: Filename (null-terminated)
- **Note**: The code confirms that the 4-byte header field is the file-table offset. It does **not** rely on the first image starting at `0x06`; that may be common in samples, but it is not a parser assumption.

**Status**: ✅ **FOUND** - Actual .PL parsing functions located and analyzed:

### Functions Identified and Analyzed

#### Core File I/O Functions

1. **DosFileRead** (`22f2:0016`)
   - Verified `INT 21h` / `AH=3Fh` wrapper
   - Reads `CX` bytes from handle `BX` into `DS:DX`
   - Returns DOS read count or `0xFFFF` on error

2. **DosOpenReadOnly** (`22f2:004e`)
   - Verified `INT 21h` / `AX=3D00h`
   - Opens archive files in read-only mode

3. **DosSeek** (`22f2:0096`)
   - Verified `INT 21h` / `AH=42h`
   - Used both to jump to archive tables and to position the file pointer on selected entries

4. **OpenPLArchive** (`2147:00a2`)
   - Validates archive state object
   - Clears the current archive base offset
   - Opens the file and immediately calls the header reader

5. **ReadPLHeader** (`2147:0000`)
   - Reads exactly 6 bytes from the archive base
   - Interprets them as `count` + `tableOffset`
   - Allocates `count * 12` bytes and reads the file table from `base + tableOffset`
   - This confirms the documented `.PL` layout

6. **FindPLFileEntry** (`2147:0142`)
   - Normalizes the requested name to an 8-byte stem
   - Searches the loaded 12-byte table entries
   - Computes the absolute entry offset as `archiveBase + entryOffset`
   - Optionally seeks to the entry and optionally returns the entry size using the next table entry or the table offset for the last entry

7. **ClosePLArchive** (`2147:0106`)
   - Closes the underlying file handle when appropriate
   - Frees the allocated file table buffer

8. **OpenNestedPLFromEntry** (`2147:029c`)
   - Uses `FindPLFileEntry` to locate a named entry inside the current archive
   - Captures the current file position with `22f2:00b4`
   - Re-runs `ReadPLHeader` from that entry position
   - This means the engine supports `.PL` archives embedded inside other `.PL` archives

#### Asset Decode Functions

9. **LoadImageEntryByName** (`2106:0344`)
   - Thin wrapper around `FindPLFileEntry`
   - Immediately hands the selected entry to the real image loader/decoder

10. **LoadAndDecodeImageEntry** (`2106:00f5`)
   - Reads a verified per-entry `0x1C`-byte asset header
   - Uses header fields to allocate output, read payload bytes, and optionally read palette/remap data
   - If the header format byte at `4c5a` is `0x10`, the payload is treated as raw data
   - Otherwise it calls `2106:002c`, which is the current best decompression candidate

11. **DecodeIndexedImageStream** (`2106:002c`)
   - Accepts only format codes `1` or `2`
   - Decodes a nibble-based stream into indexed pixels
   - Opcode `0xF` emits an absolute symbol
   - Other nibbles advance through a delta table
   - In format `2`, opcode `0xE` becomes a repeat/run-length operation
   - This is the first function in the verified chain that performs real data decompression rather than archive I/O

12. **GetOrLoadImageAsset** (`13ab:000c`)
   - Caches image assets by an ID in `AL`
   - Calls `2106:0344`, then post-processes the decoded image
   - Relevant for graphics assets, but not itself the decompressor

### 0x1C Image Entry Header
The image/member-local header read by `LoadAndDecodeImageEntry` is now mapped with much higher confidence. The routine reads exactly `0x1C` bytes into `4c56`, and the observed member sizes in the sample `.PL` files match the following layout:

| Offset | Size | Meaning | Evidence |
| --- | --- | --- | --- |
| `0x00` | `byte` | Flags | Bit `0` adds an inline palette after the payload. Bit `1` adds an inline symbol table after the payload or after the palette. |
| `0x01` | `byte` | Symbol count minus 1 | `LoadAndDecodeImageEntry` stores `4c57 + 1` in `4b54`. Sample member size deltas match this exactly. |
| `0x02` | `byte` | Palette color count minus 1 | When flag bit `0` is set, the loader reads `(4c58 + 1) * 3` bytes. Background entries with value `0xFF` consume `768` bytes, matching a 256-color palette. |
| `0x03` | `byte` | Transparent symbol/index | Raw assets use `4c59` directly. Compressed assets translate `4c59` through the symbol table before `ApplyPaletteRemapAndTransparency` converts it to `0xFF`. |
| `0x04` | `byte` | Format code | `0x10` is treated as raw pixel data. `0x01` and `0x02` are accepted by `DecodeIndexedImageStream`. |
| `0x05..0x13` | `15 bytes` | Delta table for nibble opcodes `0x0..0xE` | `DecodeIndexedImageStream` indexes `4c5b + nibble`. Opcode `0xF` is the absolute-symbol escape. |
| `0x14` | `word` | Tail literal count | `DecodeIndexedImageStream` uses `4c6a` after the main loop to translate a raw suffix already present at the end of the in-place input buffer. |
| `0x16` | `word` | Height | Verified against extracted `BACKGRND` samples: the decoded buffer displays correctly when this word is treated as the image height. |
| `0x18` | `word` | Width | Verified against extracted `BACKGRND` samples: `ALLEY1` is correct at `320x200` when this word is treated as the image width. |
| `0x1A` | `word` | Payload length | The loader reads exactly this many bytes for the image payload before processing optional palette/symbol data. |

#### Empirical Extension
The shipping sample files show one additional variant that is not yet tied back to a verified code path:

- Entries with header flags `0x06` or `0x07` are larger than the base `0x1C + payload + optional palette + optional symbol table` model by exactly `symbolCount` bytes.
- `BNTITLE.PL` is dominated by this variant.
- The current extractor treats that extra trailing region as an auxiliary blob of `symbolCount` bytes and writes it out, but its semantic meaning is still unresolved.
- This is an on-disk observation, not yet a Ghidra-proven header field.

### Sample Validation
The sample `.PL` files validate the header map cleanly:

- `SPRITE.PL::morph` has entry size `0x4555`, payload length `0x4505`, and symbol count `0x33 + 1 = 0x34`. `0x1C + 0x4505 + 0x34 = 0x4555`.
- `SPRITE.PL::fig1` has entry size `0x15C7`, payload length `0x1587`, and symbol count `0x23 + 1 = 0x24`. `0x1C + 0x1587 + 0x24 = 0x15C7`.
- `OBJECTS.PL::BAR1CHR` has entry size `0x109`, payload length `0xCE`, and symbol count `0x1E + 1 = 0x1F`. `0x1C + 0xCE + 0x1F = 0x109`.
- `BACKGRND.PL::ALLEY1` has entry size `0xAA57`, payload length `0xA675`, palette bytes `(0xFF + 1) * 3 = 0x300`, and symbol table length `0xC5 + 1 = 0xC6`. `0x1C + 0xA675 + 0x300 + 0xC6 = 0xAA57`.
- `INTRO.PL::TEXTS1` has entry size `0x5634`, payload length `0x52F3`, palette bytes `0x300`, and symbol table length `0x24 + 1 = 0x25`. `0x1C + 0x52F3 + 0x300 + 0x25 = 0x5634`.

### Dimension Validation
- `BACKGRND.PL::ALLEY1` decodes to a `64000`-byte indexed buffer that displays correctly as `320x200` when the inline RGB palette is applied directly.
- This confirms that the current extractor had the two dimension words reversed, not that the decompressor was wrong.
- It also shows that at least for `BACKGRND` assets, the emitted decode buffer is already in the stored entry dimensions; no additional width expansion is needed to view the image correctly.

### Sprite-Specific Findings
- Sprite loading does not use the same simple palette assumptions as backgrounds.
- The sprite-specific loader at `1b3c:2453` sets up an external context before calling `LoadImageEntryByName`, then always calls `ApplyPaletteRemapAndTransparency` afterward.
- This is strong evidence that many sprite entries rely on an external/shared palette context rather than embedding their own palette data.
- The root cause of the sprite decode failures turned out to be in the extractor, not in a third on-disk sprite variant.
- Ghidra disassembly of `DecodeIndexedImageStream` (`2106:002c`) shows that when format `0x02` hits opcode `0x0E` and the first length byte is `0xFF`, the next two bytes are assembled in stream order as `CH` then `CL` before adding `2`.
- The extractor was previously treating that extended run length as little-endian, which produced bogus lengths like `0xA501` and caused false `Decoded run length exceeds the remaining output pixels.` failures.
- After correcting that byte order, all `344` entries in `SPRITE.PL` decode as image entries with no remaining `ImageEntryDecodeFailed` or `RawEntry` cases.
- In extracted data, this matches the observed split:
   - Many decoded `SPRITE.PL` entries are `0x02` containers with no inline palette.
   - Many portrait-like entries are `0x03` containers with inline palettes.
- Therefore, a missing `.palette.rgb` file for a decoded sprite entry does **not** necessarily mean extraction failed; it often means the entry never carried an inline palette.
- `MALEC`, `MALEF`, and `MALEG` all share the same `66x160` five-band packed layout and the same dominant background index (`0xFF`), so `MALEG` does not currently look like a one-off decompression outlier.

### Effective Transparent Pixel Findings
- The header's `TransparentIndex` byte is not always the pixel value that appears in the decoded output.
- For compressed assets (`format 0x01`/`0x02`), the effective transparent pixel is `symbolTable[TransparentIndex]` after the inline symbol table remap.
- Example: `SPRITE.PL::RED` stores `TransparentIndex = 0x14`, but its decoded dominant background pixel is `EffectiveTransparentIndex = 0x1E`.
- This translated transparent pixel behavior is not sprite-exclusive.
- Archive scan results show the same pattern in `OBJECTS.PL`, parts of `CHARGEN.PL`, and many `INTRFACE.PL` entries.
- For extractor previews, the validated policy is:
   - Always apply transparency for `SPRITE.PL` and `OBJECTS.PL`.
   - Apply transparency for smaller `CHARGEN.PL` and `INTRFACE.PL` assets that behave like overlays.
   - Keep `BACKGRND.PL`, `INTRO.PL`, `BNTITLE.PL`, and full-screen interface screens opaque for standalone previews.

### External Palette Supply Findings
- Assets without inline palettes do not appear to pull RGB triples from the image entry stream at all.
- `LoadAndDecodeImageEntry` (`2106:00f5`) only reads palette bytes from the file when header flag bit `0` is set. If that bit is clear, no palette read occurs.
- The post-decode color mapping instead comes from `ApplyPaletteRemapAndTransparency` (`17c8:005e`).
- That function does **not** use the image entry payload directly; it first calls `FUN_21dd_004e` with `BX = imageStruct + 0x0A`, meaning the palette selector lives in descriptor fields immediately after the core image struct.
- `GetOrLoadImageAsset` (`13ab:000c`) returns `pcVar2 + 6` as the image struct, so the palette selector consumed by `ApplyPaletteRemapAndTransparency` is stored at `pcVar2 + 0x10` and following bytes in the cached asset record.
- The sprite loader `FUN_1b3c_2453` follows the same pattern: it returns `pcVar3 + 6`, and `ApplyPaletteRemapAndTransparency` therefore consumes palette-selection metadata from `pcVar3 + 0x10` rather than from `SPRITE.PL` entry-local palette bytes.
- The helper callbacks configured before `LoadImageEntryByName` make the split clearer:
   - `1814:07a0` supplies only a pixel buffer pointer from `9554:9556` and is used by the sprite-specific path.
   - `1814:0837` can supply both a pixel buffer (`9554:9556`) and a palette pointer (`9550:9552`) and is used by the general cached-image path.
- `FUN_21dd_004e` then drives `24f0:004e` / `24f0:0064`, which use `INT 67h` EMS services, and `206a:*`, which build a 256-entry remap tree from RGB data.
- The verified conclusion is therefore: images without inline palettes are colored through a descriptor-selected, EMS-backed shared palette context, not through extra bytes appended to the `.PL` member.
- The binary also contains the strings `data/bn_pal.cmp` and `data/bn_pal.tbl`, which strongly suggests that these shared palette banks are loaded from `BN_PAL` resources elsewhere in startup code.
- That final `BN_PAL` file-load link is not yet xref-anchored in Ghidra, so it should currently be treated as a strong lead rather than a fully proven endpoint.

#### Graphics Processing Functions

13. **ExpandPackedImageLayout** (`17c8:01d3`)
   - Rewrites the decoded pixel buffer in-place
   - Changes the stored width from `width / 5` to `width * 8 / 5`
   - This is clearly a layout expansion step, but the current evidence does **not** prove a generic planar-to-chunky conversion

14. **ApplyPaletteRemapAndTransparency** (`17c8:005e`)
   - Builds a 256-byte remap table from the active palette context
   - Rewrites image indices through that table
   - Converts the designated transparent index to `0xFF`

## Technical Architecture

### File Loading Process
1. Game requests asset by name
2. **OpenPLArchive** opens and parses a `.PL` archive using DOS file operations
3. **ReadPLHeader** reads the archive header and loads the table of 12-byte file entries
4. **FindPLFileEntry** locates the requested member and seeks to its data
5. **LoadAndDecodeImageEntry** reads the member-local `0x1C`-byte image header
6. **DecodeIndexedImageStream** expands compressed payloads for format `1` and `2` assets
7. **ExpandPackedImageLayout** + **ApplyPaletteRemapAndTransparency** post-process graphics assets for display

### Nested Archive Support
1. `OpenNestedPLFromEntry` can re-base the archive parser at a selected member offset
2. This is strong evidence that some `.PL` members are themselves nested `.PL` containers
3. This matters for extraction: a correct unpacker must allow recursive archive descent, not only top-level `.PL` parsing

### DOS Integration
The game properly integrates with DOS file system through:
- Use of DOS interrupt 0x21 for file operations
- Proper error handling for file I/O failures
- Support for DOS file handles and standard file operations

## Current Status

✅ **Complete**: DOS interrupt usage identified and traced  
✅ **Complete**: Real DOS open/read/seek/close helpers identified  
✅ **Complete**: .PL file format parsing logic discovered and analyzed  
✅ **Complete**: Prior `28f7:*` file/decompression assumptions disproven  
✅ **Complete**: Actual image load path anchored at `2106:0344 -> 2106:00f5 -> 2106:002c`  
✅ **Complete**: Graphics post-processing functions located  
🔄 **In Progress**: Formalizing the format `1` / format `2` nibble-stream decoder  
📝 **Next**: Document the `0x1C` image header fields and reproduce `2106:002c` in a standalone extractor

## Function Reference

### Corrected Assumptions
* `28f7:1cd1` is **not** a DOS interrupt handler; it is an AX-based runtime dispatcher unrelated to archive file I/O
* `28f7:0a56` is **not** a high-level file reader; it operates on screen/world-style coordinates and runtime state
* `28f7:23eb` is **not** a chunk processor or decompressor; it is an interactive runtime/UI loop
* `17c8:01d3` is a real graphics layout transform, but calling it "planar to chunky" is currently too strong

### Legacy Notes
The early `.PL` work was solid, but the middle of the prior asset-loading chain mixed in unrelated 28f7 runtime code. The decompression work should proceed from `2106:00f5` and especially `2106:002c`, not from the 28f7 functions.
