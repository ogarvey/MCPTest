# Fugger 2 - Reverse Engineering Analysis

## Overview
Analyzing DOS game Fugger 2 to understand graphics reading and decoding/decompressing logic.

## Graphics Format
- Graphics stored in DAT files (`icon??.dat`)
- Uses **Run-Length Encoding (RLE)** compression
- Multiple color depths supported (8-bit, 16-bit, 32-bit)
- Icons managed in a cache system with 512 (0x200) slots

## Key Functions Identified

### InitializeIconGraphicsSystem (0x0002f300)
**Previously:** FUN_0002f300  
**Purpose:** Main initialization routine for the icon/graphics system

**Details:**
- Initializes 512 icon slots in memory
- Loads icon characteristic files:
  - `icons?b\icon??in.dat` - Icon characteristics/metadata
  - `icons?b\icon??cs.dat` - Character/font data
- Pre-loads 34 (0x22) icons from the main icon set
- Pre-loads 16 (0x10) icons from extended set (starting at 0x16d)
- Sets up interrupt handlers and memory management structures
- Initializes mouse input handler

**Key Data Structures:**
- `g_iconMemoryPointers[512]` (0x000c3070) - Icon memory pointers
- `g_iconStateFlags[512]` (0x000c3c70) - Icon state flags (0=free, 2=loaded)
- `g_iconSizes[512]` (0x000d46d0) - Icon sizes/lengths
- `g_iconMemoryAvailable` (0x000d68bc) - Available memory counter
- `g_iconMemoryUsed` (0x000d68b8) - Used memory counter
- `g_iconMemoryBase` (0x000d68c0) - Base address of icon memory pool
- `g_iconFilenameBuffer` (0x00084d44) - Buffer for building icon filenames
- `g_currentIconSlot` (0x000d690a) - Current icon slot being processed

### LoadIconFile (0x0002dbc0)
**Previously:** FUN_0002dbc0  
**Purpose:** Load/cache individual icon files with LRU eviction

**Details:**
- Takes icon index (0-0xFFF), returns pointer to loaded icon data
- Implements intelligent caching with LRU eviction
- File naming: `icons\icon??.dat` where ?? = hex digits of icon ID
- Checks if icon already loaded before file I/O
- When memory full, searches for removable icons (state < 2)
- Copies old icon data before eviction
- Error messages: "openicon: no removable icon", "openicon: no iconfile"

**Algorithm:**
1. Mask icon index to 0xFFF (12-bit)
2. Check if already loaded (cache hit)
3. If not loaded and memory full, evict LRU icon
4. Build filename from icon index
5. Open, read, and cache icon data
6. Update memory counters and state flags

### DrawRLESprite (0x00013a56)
**Previously:** FUN_00013a56  
**Purpose:** Decompress and render RLE-encoded sprite data to screen buffer

**Details:**
- **RLE Format Identified:**
  - `0xFE` = End of scanline marker
  - `0xFF` = End of sprite marker
  - `< 0xFD` = Skip count (transparent pixels)
  - Followed by pixel count + pixel data
  
**RLE Decoding Algorithm:**
```
while (not end of sprite):
    byte = read()
    if byte == 0xFE:
        // Move to next scanline
        destPtr += scanlineWidth
    elif byte == 0xFF:
        // End of sprite
        break
    elif byte < 0xFE:
        // Skip 'byte' transparent pixels
        destPtr += byte
        // Read pixel count
        count = read()
        // Copy 'count' pixels
        for i in 0..count:
            destPtr[i] = read()
        destPtr += count
```

**Color Depth Support:**
- 8-bit palette mode (1 byte per pixel)
- 16-bit color mode (2 bytes per pixel, likely RGB565/555)
- 32-bit color mode (4 bytes per pixel, RGBA)
- Palette lookup/translation support

**Special Features:**
- Transparency support (0x00 = transparent in certain modes)
- Horizontal/vertical flipping support (flags in param)
- Multiple destination buffers (video memory segments)
- Clipping support

### BlitSprite (0x000134af)
**Previously:** FUN_000134af  
**Purpose:** Fast uncompressed sprite blitting with transparency

**Details:**
- Direct pixel copying from source to destination
- Transparency checking (0x00 pixels skipped)
- Optimized with 32-bit copying for aligned data
- Supports multiple color depths
- Used for already-decompressed graphics

### File I/O Functions
- **FileOpen (0x0006a060)**: DOS INT 21h file open wrapper
- **FileRead (0x0006a2d2)**: DOS INT 21h file read with CR/LF handling
- **FileClose (0x0006adea)**: DOS INT 21h file close wrapper

### Input Handler
- **HandleMouseInput (0x0002e400)**: Mouse event handler, checks coordinates and button states

## Graphics Data Format Analysis

### Icon DAT File Structure (Hypothesis)
Based on the code analysis, icon DAT files appear to contain:

1. **RLE-compressed sprite data** 
2. **Marker bytes:**
   - `0xFE` = newline/next scanline
   - `0xFF` = end of sprite
   - `< 0xFE` = skip count + run length encoding

### Why RGB16 interpretation looks "recognizable but wrong"
The DAT files are **RLE-compressed** AND **palette-indexed**, not raw RGB pixel data! 

**Critical Discovery:** The pixel data in the DAT files are **palette indices** (0-255), not RGB values.

## Palette Data ⚠️ CORRECTED INFORMATION

**IMPORTANT UPDATE:** The primary VGA palette is located at a different address than previously thought!

### Primary VGA Palette (EXTRACT THIS!)
- **`g_vgaPalette256` at 0x0008383c** - **768 bytes**
  - Standard VGA palette format: 256 colors × 3 bytes (R, G, B)
  - RGB values are 6-bit (0-63) for VGA DAC
  - Loaded at runtime via BIOS INT 10h function 1012h by `InitializeGraphicsMode`
  - **This is the PRIMARY palette to extract for proper color decoding!**

### Utility Data
- **`g_fadeTable` at 0x000835ba** - Fade/brightness lookup table
  - Starts with "0123456789abcdef????????\r\n$"
  - Used for palette fading effects and debug hex conversion

### Color Remapping Tables (Secondary - For Special Effects)
These are NOT primary palettes but color translation/remapping tables:

- **`g_paletteRemapTable` at 0x00083b78** - 8-bit remap table (4096 bytes)
  - 16 palettes × 256 entries
  - Used when palette index >= 0x80 for sprite color remapping effects
  
- **`g_paletteRemap16_LowBytes` at 0x00083d58** - 16-bit remap low bytes (4096 bytes)
- **`g_paletteRemap16_HighBytes` at 0x00083f38** - 16-bit remap high bytes (4096 bytes)
  - Used for 16-bit color remapping/translation

- **`g_solidColorMap8` at 0x00083b3c** - Solid color fills (8-bit mode)
- **`g_solidColorMap16` at 0x00083b3d** - Solid color fills (16-bit mode)

### How Palette System Works
Looking at the `DrawRLESprite` decompiled code:

### How Palette System Works
Looking at the `DrawRLESprite` decompiled code:

```c
// For 8-bit mode with remapping (palette >= 0x80):
*(undefined *)puVar20 = (&g_paletteRemapTable)[(uint)(byte)*param_4 + (param_5 & 0x7f) * 0x10];

// For 16-bit mode with remapping (palette >= 0x80):
*puVar19 = CONCAT11((&g_paletteRemap16_HighBytes)[iVar18 + (uint)(byte)*param_4],
                    (&g_paletteRemap16_LowBytes)[iVar18 + (uint)(byte)*param_4]);
```

The pixel bytes are used as **indices** that go through:
1. **VGA Palette** (0x0008383c) - Primary 256-color palette loaded into VGA hardware
2. **Remap Tables** (0x00083b78, etc.) - Optional color translation for special effects

**Additional Graphics Variables:**
- `g_videoModeInfo` (0x000832ac) - Video mode configuration (offset +0x82 = color depth)
- `g_videoMemorySegment` (0x000ef38a) - Video memory segment selector
- `g_screenWidth` (0x0008334d) - Screen width in pixels
- `g_screenPitch` (0x00083351) - Screen scanline pitch (bytes per row)
- `g_drawNestingLevel` (0x00084d3c) - Drawing operation nesting counter
- `g_iconFlags[512]` (0x000c2670) - Icon attribute flags

**To properly decode:** 
1. Decompress the RLE data to get palette indices
2. Extract the **VGA palette** from 0x0008383c (768 bytes)
3. Map each index through the palette to get final RGB values
4. Convert 6-bit VGA colors (0-63) to 8-bit RGB (0-255) by multiplying by 4

## Additional Key Functions

### InitializeGraphicsMode (0x00012d74)
**Purpose:** Initialize graphics mode and load VGA palette

**Details:**
- Sets up VGA/SVGA video modes
- **Loads the VGA palette** via BIOS INT 10h function 1012h:
  ```c
  pcVar1 = (code *)swi(0x10);
  (*pcVar1)(0x1012, &g_vgaPalette256, 0x100);  // Load 256 colors
  ```
- Initializes mouse driver
- Configures screen width, pitch, and memory segments

### PrintDebugHex (0x00012c51)
**Purpose:** Debug hex printing function
- Uses `g_fadeTable` for hex digit conversion
- Outputs debug messages to console

## Next Steps
1. ✅ ~~Extract sample icon DAT file~~
2. ✅ ~~Implement RLE decompression based on DrawRLESprite logic~~
3. **CRITICAL:** Extract VGA palette data from game executable
   - **Primary palette:** `g_vgaPalette256` at 0x0008383c (768 bytes)
   - Format: 256 colors × 3 bytes (R, G, B), 6-bit values (0-63)
   - See `PALETTE_CORRECTED.md` for extraction guide
4. Apply palette lookup to convert indices → RGB colors
5. Scale 6-bit VGA colors to 8-bit: `RGB8 = VGA6 * 4`

**Current Status:** Decompressor working correctly (shapes are right). Need to extract VGA palette from 0x0008383c for correct colors.

## Technical Notes
- DOS real-mode segmented memory addressing
- VGA/SVGA graphics modes (multiple color depths)
- Efficient memory management for resource-constrained DOS environment
- **Palette-indexed graphics** - Not direct RGB, requires palette lookup tables
- Sophisticated LRU caching system with 512 icon slots

## Ghidra Project Updates
All key variables and functions have been renamed for clarity. See `VARIABLE_NAMING.md` for complete list.

**Key renamed symbols:**
- **Palette data:** `g_vgaPalette256` (0x0008383c), `g_fadeTable`, `g_paletteRemapTable`, `g_paletteRemap16_LowBytes`, `g_paletteRemap16_HighBytes`, `g_solidColorMap8`, `g_solidColorMap16`
- **Icon cache:** `g_iconMemoryPointers`, `g_iconStateFlags`, `g_iconSizes`
- **Video system:** `g_videoModeInfo`, `g_screenWidth`, `g_screenPitch`, `g_videoMemorySegment`
- **Functions:** `InitializeIconGraphicsSystem`, `LoadIconFile`, `DrawRLESprite`, `BlitSprite`, `InitializeGraphicsMode`, `PrintDebugHex`

**See `PALETTE_CORRECTED.md` for complete palette system documentation.**

---
*Analysis started: October 25, 2025*
