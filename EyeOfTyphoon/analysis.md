# Eye of Typhoon - Reverse Engineering Analysis

## Game Information
- **Platform**: DOS
- **Graphics Format**: CHA files
- **Analysis Date**: November 12, 2025

## Objective
Identify and understand the sprite reading and decompression/decoding logic used by the game.

## Functions Under Investigation

### Initial Function List
The following functions have been identified as potentially related to sprite handling:
- `FUN_00426087` - To be analyzed
- `FUN_00420b48` - To be analyzed
- `FUN_0042037e` - To be analyzed
- `FUN_00425dc8` - To be analyzed

---

## Analysis Sessions

### Session 1 - Initial Function Analysis
**Date**: November 12, 2025

#### Status
Completed initial analysis of all four functions.

---

## Detailed Function Analysis

### FUN_00426087 (Address: 0x00426087)
**Proposed Name**: `LoadCHAFile` or `LoadSpriteData`

**Purpose**: Main sprite/graphics loading function that loads multiple related files:
- `.cha` files (sprite/character data)
- `.pid` files (picture index/directory data)
- `.act` files (action/animation data)
- `.idx` files (index data)
- `.pal` files (palette data)
- `distance.dat` file
- `.lic` file

**Key Observations**:
- Takes two parameters: `param_1` (byte - appears to be a character/sprite ID) and `param_2` (int - appears to be a mode/type selector: 0, 1, 3, or other)
- Opens files using pattern `"/data/maindata/%s.cha"` where %s is derived from param_1
- Reads file sizes, allocates buffers, and loads data into global arrays:
  - `DAT_0045782c[]` - Array of sprite data pointers
  - `DAT_00457830` - Width data
  - `DAT_00457834` - Height data
  - `DAT_00457838` - Action/animation data
  - `DAT_0045783c` - Index data (256 entries * 5 bytes)
  - `DAT_00457844` - Palette data (768 bytes - RGB triplets)
  - `DAT_00457840` - Additional palette data (400 bytes)
  - `DAT_00457848` - Distance data (0x66 bytes)
  - `DAT_0045784c` - License/lookup data (0x6994 bytes)
- Special case handling for `param_1 == 3`
- Returns 1 on success, 0 on failure

**Called By**:
- `FUN_00415b61` (4 calls)
- `FUN_00415f2b` (1 call)
- `FUN_0042581b` (4 calls)

---

### FUN_00420b48 (Address: 0x00420b48)
**Proposed Name**: `LoadEndingAnimation` or `ShowEndingScreen`

**Purpose**: Loads and displays ending/demo graphics

**Key Observations**:
- Loads three specific files:
  - `/data/demo/endding.cha` (note the typo in filename!)
  - `/data/demo/endding.pal`
  - `/data/demo/endding.pid`
- Allocates buffers and loads data
- Builds sprite pointer array from PID (picture index data)
- Calls display/rendering functions:
  - `FUN_0042076d` - Appears to be a rendering function
  - `FUN_0041bc5d` - Called with constants (0x15, 0x10) - possibly delay/timing
  - `FUN_00420a16` - Another rendering function
  - `FUN_00420a3a` - Another rendering function
- Frees allocated memory after use
- No return value

**Called By**:
- `FUN_00425170` (1 call)

---

### FUN_0042037e (Address: 0x0042037e)
**Proposed Name**: `ShowIntroSequence` or `PlayDemoSequence`

**Purpose**: Loads and plays the introduction/demo sequence with multiple animations

**Key Observations**:
- Loads audio files first:
  - `/data/soundata/ho0e.voc`
  - `/data/soundata/eot.voc`
- Loads three sets of animation graphics:
  1. **newdemo** set (`.cha`, `.pal`, `.pid`)
  2. **victory** set (`.cha`, `.pal`, `.pid`)
  3. **powell0** set (`.bak`, `.bpl`)
- Builds sprite pointer arrays for each set
- Plays sequence with conditional checks on `_DAT_004316c4` (likely a global flag for "skip intro" or similar)
- Calls various rendering/animation functions:
  - `FUN_0041f6af`
  - `FUN_0041f84e`
  - `FUN_0041fe91`
  - `FUN_0041febe`
  - `FUN_004201d5`
  - `FUN_0041bcbd` - Appears to be called if sequence is interrupted
- Cleans up all allocated memory and sound resources
- No return value

**Called By**:
- `FUN_00425170` (1 call)

---

### FUN_00425dc8 (Address: 0x00425dc8)
**Proposed Name**: `LoadGanjiData` or `LoadSpecialGraphics`

**Purpose**: Loads a specific graphics set called "ganji"

**Key Observations**:
- Loads files:
  - `/data/maindata/ganji.cha`
  - `/data/maindata/ganji.pid`
  - `/data/maindata/ganji.pal`
- Pattern similar to other loaders
- Stores data in:
  - `DAT_00451c08` - Base sprite data pointer
  - `(&DAT_00451c08)[n]` - Array of sprite pointers
  - `DAT_00451d50` - Palette data (48 bytes / 0x30)
- Returns 1 on success, 0 on failure

**Called By**:
- `FUN_00425170` (1 call)

---

## Common Helper Functions Identified

### FUN_00429354 (Address: 0x00429354)
**Proposed Name**: `OpenFile` or `FileOpen`
- Opens a file given a path and mode
- Returns a file handle structure

### FUN_004295c0 (Address: 0x004295c0)
**Proposed Name**: `FileRead` or `fread`
- Reads data from a file handle
- Parameters: buffer, element_size, count, file_handle
- Similar to standard C `fread()`

### FUN_0042a828 (Address: 0x0042a828)
**Proposed Name**: `GetFileSize`
- Gets the size of a file
- Uses Windows API `GetFileSize()`

### FUN_00429490 (Address: 0x00429490)
**Proposed Name**: `ReadFileData` or `BufferedRead`
- Low-level file reading with buffering
- Handles file I/O operations

### FUN_00429034 (Address: 0x00429034)
**Proposed Name**: `CloseFile` or `FileClose`
- Closes a file handle

### FUN_0042b484 (Address: 0x0042b484)
**Proposed Name**: `AllocateMemory` or `malloc`
- Allocates memory buffer

### FUN_0042b348 (Address: 0x0042b348)
**Proposed Name**: `FreeMemory` or `free`
- Frees allocated memory

---

## File Format Analysis

### .CHA Files
- Character/sprite graphics data
- Contains compressed or raw image data
- Referenced by .PID files for individual sprite extraction

### .PID Files (Picture Index Data)
- Contains array of DWORDs (sizes)
- First DWORD is often skipped/used differently
- Each entry represents the size of one sprite in the .CHA file
- Used to build offset table for sprite extraction

### .PAL Files
- Palette data
- 768 bytes for standard VGA palette (256 colors × 3 RGB bytes)
- Sometimes scaled by 2 (multiplied when reading)

### .ACT / .IDX Files
- Animation/action data
- Index lookup tables
- Likely control sprite sequences

---

## Data Structures

### Sprite Data Arrays
Based on the code, the game uses these global structures:
- **Sprite pointer array**: Points to individual decompressed sprites
- **Width/Height arrays**: Store dimensions (as WORD/uint16)
- **Palette data**: RGB triplets for VGA display
- **Index tables**: 256 entries of 5-byte records for sprite lookups

---

## Next Steps

### Variable Renaming Candidates

**In FUN_00426087**:
- `param_1` → `characterId` or `spriteSetId`
- `param_2` → `loadMode` or `spriteType`
- `local_18` → `spriteCount` or `numSprites`
- File handle variables could be named after their file types

**In FUN_00420b48**:
- `ppiVar2` → `chaData` or `spriteData`
- `local_8` → `palData` or `paletteData`
- `ppiVar3` → `pidData` or `indexData`
- `local_68` → `spritePointers`

**Similar patterns for other functions**

---

## Renaming Completed

### Main Functions Renamed (Session 1)
**Date**: November 12, 2025

All function renamings have been successfully applied in Ghidra:

| Original Address | Original Name  | New Name              | Purpose |
|-----------------|----------------|-----------------------|---------|
| 0x00426087      | FUN_00426087   | `LoadCHAFile`        | Main sprite data loader |
| 0x00420b48      | FUN_00420b48   | `LoadEndingAnimation` | Ending/demo animation loader |
| 0x0042037e      | FUN_0042037e   | `ShowIntroSequence`   | Introduction sequence player |
| 0x00425dc8      | FUN_00425dc8   | `LoadGanjiData`       | Loads ganji graphics set |

### Helper Functions Renamed

| Original Address | Original Name  | New Name              | Purpose |
|-----------------|----------------|-----------------------|---------|
| 0x00429354      | FUN_00429354   | `OpenFile`           | Opens a file and returns handle |
| 0x004295c0      | FUN_004295c0   | `FileRead`           | Reads data from file (fread-like) |
| 0x0042a828      | FUN_0042a828   | `GetFileSize`        | Gets file size using Windows API |
| 0x00429490      | FUN_00429490   | `BufferedRead`       | Low-level buffered file reading |
| 0x00429034      | FUN_00429034   | `CloseFile`          | Closes file handle |
| 0x0042b484      | FUN_0042b484   | `AllocateMemory`     | Allocates memory buffer (malloc) |
| 0x0042b348      | FUN_0042b348   | `FreeMemory`         | Frees allocated memory (free) |

---

## Next Steps for Further Analysis

### Variable Renaming
The next phase would involve renaming variables within these functions to improve readability:
- Parameter names (e.g., `param_1` → `characterId`)
- Local variables (e.g., `local_18` → `spriteCount`)
- Buffer pointers (e.g., `ppbVar1` → `chaFileHandle`)

### Additional Functions to Investigate
Based on the cross-references, these calling functions may also benefit from analysis:
- `FUN_00415b61` - Calls LoadCHAFile 4 times
- `FUN_00415f2b` - Calls LoadCHAFile once
- `FUN_0042581b` - Calls LoadCHAFile 4 times
- `FUN_00425170` - Calls all three demo/intro functions

### Rendering Functions
These appear to be display/rendering functions that work with the loaded data:
- `FUN_0042076d` - Rendering function (used in ending)
- `FUN_00420a16` - Rendering function (used in ending)
- `FUN_00420a3a` - Rendering function (used in ending)
- `FUN_0041f6af` - Animation player (intro sequence)
- `FUN_0041f84e` - Animation player (intro sequence)
- `FUN_0041fe91` - Animation player (intro sequence)
- `FUN_0041febe` - Animation player (intro sequence)
- `FUN_004201d5` - Animation player (intro sequence)

---

## CHA Sprite Decompression Analysis

### Decompression Functions Identified

| Original Address | Original Name  | New Name                 | Purpose |
|-----------------|----------------|--------------------------|---------|
| 0x00422b98      | FUN_00422b98   | `BlitSpriteRLE_Down`    | Draws sprites downward with RLE decompression |
| 0x004226c5      | FUN_004226c5   | `BlitSpriteRLE_Up`      | Draws sprites upward with RLE decompression |
| 0x0042292c      | FUN_0042292c   | `BlitSpriteRLE_UpReverse` | Draws sprites upward (reversed) with RLE decompression |
| 0x00429768      | FUN_00429768   | `ReadDWord`             | Reads a 32-bit value from file |

### CHA Sprite Format (VGA Mode X + RLE Compression)

The sprites are stored in a **Run-Length Encoded (RLE) format** designed for **VGA Mode X** (planar 4-bit graphics).

#### Sprite Data Structure
```
Offset  Size    Description
------  ------  -----------
0x00    WORD    Width (in pixels)
0x02    WORD    Height (in pixels)
0x04    WORD    X Offset (signed, for sprite alignment/hotspot)
0x06    WORD    Y Offset (signed, for sprite alignment/hotspot)
0x08    WORD    Unknown (possibly flags or additional offset)
0x0A    BYTE[]  RLE-compressed pixel data
```

**Note**: The width is stored as actual pixels, NOT divided by 4. The division by 4 happens in the rendering function for VGA Mode X planar processing, but the file format stores the real width.

The X/Y offsets are used for sprite alignment during animation - they define the "hotspot" or anchor point of the sprite relative to its top-left corner. This allows animated frames to be positioned correctly relative to each other.

**IDX File Structure:**
The `.idx` file contains 5-byte records (256 entries) with animation sequence information:

```
Offset  Size    Description
------  ------  -----------
0x00    DWORD   Offset into ACT file for this animation sequence
0x04    BYTE    Number of frames in this animation sequence
```

The game uses these to look up animation data:
1. Reads the sprite/animation index (0-255)
2. Looks up the 5-byte record in IDX file
3. Uses the DWORD offset to find the animation data in the ACT file
4. The ACT file contains frame-by-frame animation data (timing, sprite indices, etc.)
5. The BYTE count tells how many frames are in the sequence

**Example usage from Ghidra:**
```c
animPtr = *(int *)(idxData + animIndex * 5) + actFileBase;
frameCount = *(byte *)(idxData + 4 + animIndex * 5);
```

**ACT File Structure:**
The `.act` file contains animation sequence data. Each animation consists of multiple frames, where each frame is **40 bytes (0x28)** with the following structure:

```
Offset  Size    Description
------  ------  -----------
0x00    WORD    Sprite index (which sprite from CHA file to display)
0x02    BYTE    Frame delay/timing (how long to display this frame)
0x03    BYTE    Unknown
0x04    SHORT   X Position offset (signed pixels, relative positioning)
0x06    SHORT   Y Position offset (signed pixels, relative positioning)
0x08    ...     Additional frame data (flags, effects, etc.)
...
Total: 40 bytes per frame
```

**Animation Playback Flow:**
1. Game looks up animation index (0-255) in IDX file
2. Gets offset into ACT file and frame count
3. Reads ACT file at offset to get animation sequence
4. For each frame (40 bytes each):
   - Read sprite index at offset +0 (which CHA sprite to show)
   - Read frame delay at offset +2 (how many ticks to display)
   - Read X/Y offsets at +4/+6 as signed shorts (positioning relative to base position)
   - Advances to next frame after delay expires
5. Loops or stops based on animation type

> **Status note (March 8, 2026):** The interpretation above is now considered suspect. Reanalysis in Ghidra shows that the game definitely uses the first 8 bytes of each 0x28-byte ACT record as described below, but the rest of the 0x28-byte record has **not** yet been validated.

### ACT Reanalysis (March 8, 2026)

The most reliable ACT consumers are now:

- `FUN_00418947` - draws one ACT-driven sprite frame for a main actor
- `FUN_00419022` - draws ACT-driven attached/secondary frames
- `FUN_0041ab44` - advances main actor animation state using IDX + ACT
- `FUN_00413d72` - advances secondary animation state using IDX + ACT
- `FUN_0041439d` - initializes a secondary animation and seeds the current ACT frame pointer

#### What is now confirmed

Each animation sequence is still selected from the `.idx` file with a 5-byte record:

```c
actSequenceBase = *(uint32_t *)(idxData + animIndex * 5) + actBase;
frameCount      = *(uint8_t  *)(idxData + animIndex * 5 + 4);
```

The game then advances within that sequence in **0x28-byte steps**:

```c
currentFramePtr = actSequenceBase + currentFrameIndex * 0x28;
```

The following ACT frame fields are now directly confirmed by code usage:

```text
Offset  Size   Confirmed usage
------  -----  -----------------------------------------------------------
0x00    WORD   Sprite index into CHA sprite table
0x02    BYTE   Frame delay / duration counter reload value
0x03    BYTE   Flags controlling orientation / anchoring behavior
0x04    SHORT  Signed X anchor offset used during draw position calculation
0x06    SHORT  Signed Y anchor offset used during draw position calculation
0x08    ...    Remaining 0x20 bytes still unconfirmed
```

#### Important correction to earlier assumptions

The X/Y values at `+0x04` / `+0x06` are **not enough by themselves** to reconstruct the final on-screen position of a frame.

The game computes final draw coordinates from:

1. A separately maintained actor/effect base position
2. Facing / flip state stored outside the ACT record
3. ACT flag byte at `+0x03`
4. CHA sprite width/height tables
5. ACT signed offsets at `+0x04` / `+0x06`

So the ACT frame record does **not** appear to contain a full standalone absolute frame position.

#### Draw logic observed in Ghidra

From `FUN_00418947` and `FUN_00419022`:

- `ACT[0x00]` selects the CHA sprite
- `ACT[0x02]` reloads the per-frame timer
- `ACT[0x03] & 3`, XORed with actor facing, selects one of four blit variants
  - normal
  - mirrored horizontally
  - vertically flipped
  - both mirrored and vertically flipped
- `ACT[0x04]` and `ACT[0x06]` are applied as signed anchor offsets during draw
- a special case when the low nibble of `ACT[0x03]` is `1` applies width compensation on X, meaning this byte also affects how the X anchor is interpreted

In practice the draw equations look like this:

- Y is derived from `baseY - actYOffset`
- X is derived from `baseX +/- actXOffset`, with optional sprite-width compensation depending on `ACT[0x03]`

That explains why the current `AnimationExporter` output is wrong: it treats ACT records too much like standalone frame placement data, while the game treats them as **sprite selection + timing + anchor/flip metadata layered on top of actor state**.

#### Animation stepping behavior

The animation update code confirms:

- the current ACT frame pointer is stored explicitly in actor/effect state
- the current frame index is tracked separately
- the frame delay is reloaded from `ACT[0x02]`
- advancing one frame means adding `0x28` to the current ACT frame pointer
- restarting a sequence means rebuilding the pointer from `IDX.offset + ACT.base`

This means the game's runtime model is:

```text
IDX entry -> ACT sequence base -> current frame pointer -> CHA sprite index + anchor data
```

#### Current best interpretation

The ACT file is best described, for now, as:

- a table of fixed-size 0x28-byte frame records
- where only the first 8 bytes are currently verified for rendering/animation timing
- and where final sprite placement depends on additional per-actor/per-effect state outside the ACT file

#### Sample-file observations (`JARK_ACT` / `JARK_IDX`)

Using the sample files in [EyeOfTyphoon](EyeOfTyphoon):

- `JARK_IDX` contains 256 entries of the expected `DWORD offset + BYTE frameCount` format
- `JARK_ACT` is 32040 bytes, corresponding to many 0x28-byte records referenced from IDX
- 801 ACT records are referenced by the sample IDX file

For those referenced records:

- bytes `0x00..0x07` vary exactly as expected for sprite index, delay, flags, and signed anchor offsets
- bytes `0x08..0x1D` contain meaningful non-zero data in many records
- bytes `0x0E..0x0F`, `0x16..0x17`, `0x1E..0x1F`, and `0x22..0x27` are almost always zero in the sample

This makes the tail look structurally like one of these two possibilities:

1. **Four repeated 8-byte auxiliary slots**
  - `0x08..0x0F`
  - `0x10..0x17`
  - `0x18..0x1F`
  - `0x20..0x27`
2. **Mostly-unused authoring/editor data** preserved in the file but not consumed by runtime rendering logic

At the moment, runtime code evidence still favors the second interpretation for practical purposes: no confirmed draw/update path reads `ACT + 0x08..0x27`.

#### Working conclusion for tooling

For extraction/export purposes, the safest interpretation is:

- treat `0x00..0x07` as authoritative runtime animation data
- preserve `0x08..0x27` as raw unknown bytes
- do **not** build exporter behavior around the ACT tail until a real code path is found that consumes it

Further work should focus on identifying whether bytes `0x08..0x27` are used for:

- hitboxes / hurtboxes
- effect spawn points
- collision extents
- interpolation / movement helpers
- per-frame scripting flags

#### RLE Compression Algorithm

The compression uses a simple byte-oriented RLE scheme:

**Control Byte Format:**
- **Bit 7 (0x80)**: Determines the mode
  - `0` (< 0x80): **Literal pixel** - Low nibble (bits 0-3) contains palette index
  - `1` (>= 0x80): **Run of pixels** - Bits 0-6 (mask 0x7F) contain run length

**Decompression Pseudocode:**
```c
while (not end of sprite) {
    byte controlByte = ReadByte();
    
    if (controlByte < 0x80) {
        // Single pixel
        byte pixelValue = controlByte & 0x0F;  // Low nibble
        if (pixelValue != 0) {  // 0 = transparent
            DrawPixel(pixelValue | paletteBase);
        }
        MoveToNextPixel();
    }
    else {
        // Run of pixels
        byte runLength = controlByte & 0x7F;
        byte pixelValue = ReadByte() & 0x0F;
        
        if (pixelValue == 0) {
            // Transparent run - skip pixels
            SkipPixels(runLength);
        }
        else {
            // Draw run of same pixel
            for (int i = 0; i < runLength; i++) {
                DrawPixel(pixelValue | paletteBase);
                MoveToNextPixel();
            }
        }
    }
}
```

#### VGA Mode X Details

The blitting functions write directly to VGA memory using **Mode X** (planar mode):
- **4 color planes** accessed via VGA sequencer register 0x3C4
- **320x200 resolution** (or variants)
- **256 colors** (4 bits per pixel across planes)
- Uses `out()` instruction to switch between planes
- Pixel data stored as **4-bit nibbles** (0-15 palette index)

**Plane Switching Pattern:**
Each blit function processes 4 planes sequentially by:
1. Setting plane mask: `out(0x3C4, planeMask << 8 | 0x02)`
2. Writing pixels to that plane
3. Rotating to next plane

**Direction Variants:**
- `BlitSpriteRLE_Down`: Draws top-to-bottom, left-to-right (adds to Y)
- `BlitSpriteRLE_Up`: Draws bottom-to-top, left-to-right (subtracts from Y)  
- `BlitSpriteRLE_UpReverse`: Draws bottom-to-top with reversed plane order

#### Transparency Handling

- **Palette index 0** is treated as transparent (not drawn)
- The `& 0x0F != 0` check skips drawing transparent pixels
- Transparent runs skip ahead without writing

#### Palette Application

The functions take a `paletteBase` parameter (byte) that is OR'd with the pixel value:
```c
finalColor = (pixelValue & 0x0F) | paletteBase;
```
This allows color remapping/shifting for different sprite variations.

---

## Summary of Findings

### Session 1 Complete - November 12, 2025

**Functions Renamed**: 15 total
- 4 main CHA loading functions
- 7 helper file I/O and memory functions  
- 3 sprite blitting/decompression functions
- 1 data reading utility

**File Formats Understood**:
- **CHA files**: Contain RLE-compressed sprite data for VGA Mode X
- **PID files**: Picture Index Data - array of sprite sizes (DWORDs)
- **PAL files**: VGA palette data (768 bytes RGB)
- **ACT/IDX files**: Animation/action control data
- **LIC files**: Lookup/license data

**Compression Algorithm**: 
- Simple byte-oriented RLE designed for 4-bit planar VGA graphics
- Two modes: literal pixels (< 0x80) and runs (>= 0x80)
- Supports transparency (palette index 0)
- Works with VGA Mode X planar memory architecture

**Implementation**: 
- C# decompressor created: `CHASpriteDecompressor.cs`
- Can extract and decompress individual sprites from CHA files
- Uses PID files to determine sprite boundaries
- Outputs 8-bit indexed pixel data

### Recommended Next Steps

1. **Test the decompressor** with actual CHA/PID files from the game
2. **Analyze palette loading** to understand color remapping
3. **Investigate animation system** (ACT/IDX file formats)
4. **Create visualization tool** to view extracted sprites
5. **Study the rendering functions** to understand sprite positioning and layering

---

