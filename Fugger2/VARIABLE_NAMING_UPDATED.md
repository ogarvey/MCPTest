# Ghidra Variable Renaming - Fugger 2 (UPDATED)

## Summary of Renamed Variables

This document tracks all variables that have been renamed in Ghidra for better code readability.

**IMPORTANT UPDATE (Oct 25, 2025):** The palette system has been re-analyzed. The primary VGA palette is at 0x0008383c, not 0x00083b78!

---

## Palette Data ⚠️ CORRECTED

### Primary VGA Palette (EXTRACT THIS!)
| Address | Old Name | New Name | Description | Status |
|---------|----------|----------|-------------|--------|
| 0x0008383c | DAT_0008383c | `g_vgaPalette256` | **VGA palette - 768 bytes (256 colors × 3 RGB bytes), 6-bit values (0-63)** | ✅ Renamed + Commented |
| 0x000835ba | s_0123456789abcdef... | `g_fadeTable` | Fade/brightness table, hex digit lookup string | ✅ Renamed + Commented |

### Color Remap Tables (Secondary - For Sprite Effects)
| Address | Old Name | New Name | Description | Status |
|---------|----------|----------|-------------|--------|
| 0x00083b78 | DAT_00083b78 | `g_paletteRemapTable` | 8-bit palette remap table (4096 bytes, 16 palettes × 256 entries) | ⚠️ Commented only* |
| 0x00083d58 | DAT_00083d58 | `g_paletteRemap16_LowBytes` | 16-bit remap low bytes (4096 bytes) | ⚠️ Commented only* |
| 0x00083f38 | DAT_00083f38 | `g_paletteRemap16_HighBytes` | 16-bit remap high bytes (4096 bytes) | ⚠️ Commented only* |
| 0x00083b3c | DAT_00083b3c | `g_solidColorMap8` | Solid color map for 8-bit mode (palette < 0x80) | ⚠️ Commented only* |
| 0x00083b3d | DAT_00083b3d | `g_solidColorMap16` | Solid color map for 16-bit mode (palette < 0x80) | ⚠️ Commented only* |

**Note:** Remap table symbols could not be renamed via API (offset-indexed access limitation). Detailed comments added at addresses.

---

## Icon Cache Management

| Address | Old Name | New Name | Description |
|---------|----------|----------|-------------|
| 0x000c3070 | DAT_000c3070 | `g_iconMemoryPointers` | Array of 512 pointers to loaded icon data |
| 0x000c3c70 | DAT_000c3c70 | `g_iconStateFlags` | Array of 512 state flags (0=free, 2=loaded) |
| 0x000d46d0 | DAT_000d46d0 | `g_iconSizes` | Array of 512 icon sizes in bytes |
| 0x000d68bc | DAT_000d68bc | `g_iconMemoryAvailable` | Bytes available in icon memory pool |
| 0x000d68b8 | DAT_000d68b8 | `g_iconMemoryUsed` | Bytes currently used in icon memory |
| 0x000d68c0 | DAT_000d68c0 | `g_iconMemoryBase` | Base address of icon memory pool |
| 0x00084d44 | DAT_00084d44 | `g_iconFilenameBuffer` | String buffer for building icon filenames |
| 0x000d690a | DAT_000d690a | `g_currentIconSlot` | Currently processing icon slot index |
| 0x000c2670 | DAT_000c2670 | `g_iconFlags` | Array of 512 icon attribute flags |
| 0x000c2c70 | DAT_000c2c70 | `g_iconData1` | Icon metadata array 1 |
| 0x000c2870 | DAT_000c2870 | `g_iconData2` | Icon metadata array 2 |

---

## Video/Graphics System

| Address | Old Name | New Name | Description |
|---------|----------|----------|-------------|
| 0x000832ac | DAT_000832ac | `g_videoModeInfo` | Video mode config (offset +0x82 = color depth) |
| 0x000ef38a | DAT_000ef38a | `g_videoMemorySegment` | Video memory segment selector (DOS) |
| 0x0008334d | DAT_0008334d | `g_screenWidth` | Screen width in pixels |
| 0x00083351 | DAT_00083351 | `g_screenPitch` | Screen scanline pitch (bytes per row) |
| 0x00084d3c | DAT_00084d3c | `g_drawNestingLevel` | Drawing operation nesting counter |

---

## Functions Renamed

| Address | Old Name | New Name | Purpose |
|---------|----------|----------|---------|
| 0x0002f300 | FUN_0002f300 | `InitializeIconGraphicsSystem` | Initialize icon cache, load icon metadata |
| 0x00012d74 | FUN_00012d74 | `InitializeGraphicsMode` | **Initialize graphics mode, load VGA palette via INT 10h** |
| 0x0002dbc0 | FUN_0002dbc0 | `LoadIconFile` | Load icon with LRU cache management |
| 0x00013a56 | FUN_00013a56 | `DrawRLESprite` | Decompress and draw RLE sprite with palette lookup |
| 0x000134af | FUN_000134af | `BlitSprite` | Fast uncompressed sprite blit with transparency |
| 0x0002e400 | FUN_0002e400 | `HandleMouseInput` | Mouse event handler |
| 0x00012c51 | FUN_00012c51 | `PrintDebugHex` | Debug hex printing (uses g_fadeTable) |
| 0x0006a060 | FUN_0006a060 | `FileOpen` | DOS file open wrapper |
| 0x0006a084 | FUN_0006a084 | `FileOpenImpl` | DOS file open implementation |
| 0x0006a2d2 | FUN_0006a2d2 | `FileRead` | DOS file read with CR/LF handling |
| 0x0006adea | thunk_FUN_0006adea | `FileClose` | DOS file close wrapper |

---

## Code Comments Added

### InitializeGraphicsMode (0x00012d74)
- **Function comment:** "Initialize graphics mode and load VGA palette. Sets up VGA/SVGA video mode, loads 256-color palette via BIOS INT 10h function 1012h, initializes mouse driver."
- **0x000130b8:** "Load VGA palette: INT 10h, AX=1012h, ES:DX=&g_vgaPalette256, BX=0, CX=256"

### DrawRLESprite (0x00013a56)
- **Function comment:** "Main RLE sprite drawing function. Decompresses RLE-encoded sprite data and renders to screen buffer. Supports palette lookup, transparency, and multiple color depths (8-bit, 16-bit, 32-bit)."
- **0x00013ab8:** "RLE control byte: 0xFF = end sprite, 0xFE = end scanline, <=0xFD = skip count"
- **0x00013ac9:** "Skip transparent pixels, then read pixel run count"
- **0x00013e78:** "8-bit palette remap: pixel_byte -> g_paletteRemapTable[pixel_byte + palette_offset]"
- **0x00013f42:** "16-bit palette remap: combines g_paletteRemap16_HighBytes and g_paletteRemap16_LowBytes"

### LoadIconFile (0x0002dbc0)
- **Function comment:** "Loads icon file with LRU cache management. Takes icon index (0-0xFFF), checks cache, evicts old icons if memory full, opens icon??.dat file, reads data, returns pointer to loaded icon."

### InitializeIconGraphicsSystem (0x0002f300)
- **Function comment:** "Initializes the icon/graphics system. Loads icon characteristics, sets up 512-slot cache, pre-loads common icons, sets up mouse handler."

### Palette Data Addresses
- **0x0008383c (g_vgaPalette256):** "VGA palette (768 bytes). 256 colors × 3 bytes RGB. 6-bit values (0-63). Loaded into VGA DAC by InitializeGraphicsMode."
- **0x000835ba (g_fadeTable):** "Fade/brightness lookup table. Starts with hex digit string. Used by PrintDebugHex and palette fading effects."
- **0x00083b78 (g_paletteRemapTable):** "8-bit palette remap table (4096 bytes). 16 palettes × 256 entries. Used when palette >= 0x80 for color remapping."
- **0x00083d58 (g_paletteRemap16_LowBytes):** "16-bit remap low bytes (4096 bytes). Combined with high bytes for 16-bit color remapping."
- **0x00083f38 (g_paletteRemap16_HighBytes):** "16-bit remap high bytes (4096 bytes). Combined with low bytes for complete 16-bit values."

---

## Key Findings from Renaming

1. **VGA Palette System**: The game loads a standard 256-color VGA palette (768 bytes at 0x0008383c) at startup via BIOS interrupt. This is the primary palette for color decoding!

2. **Color Remapping**: Additional remap tables (0x00083b78, etc.) provide color translation for sprite effects - NOT primary palettes!

3. **Icon Cache**: Sophisticated LRU cache with 512 slots, tracking state, size, and memory usage for efficient resource management.

4. **Color Depth Support**: Video mode info at offset +0x82 determines rendering path:
   - < 2: 8-bit palette mode
   - < 5: 16-bit RGB mode
   - >= 5: 32-bit RGB mode

5. **Memory Management**: Tight memory constraints typical of DOS era - comprehensive tracking of available/used memory, base addresses, and dynamic eviction.

---

## How to Decode Icon Colors (CORRECTED)

### Step 1: Extract VGA Palette
Extract 768 bytes from **0x0008383c** (not 0x00083b78!) from the game executable in Ghidra.

### Step 2: Convert 6-bit to 8-bit
VGA uses 6-bit color (0-63). Scale to 8-bit (0-255):
```csharp
byte[] palette8bit = new byte[768];
for (int i = 0; i < 768; i++) {
    palette8bit[i] = (byte)((vgaPalette[i] << 2) | (vgaPalette[i] >> 4));
}
```

### Step 3: Apply to Decompressed Icons
```csharp
// Decompress to get palette indices
var (indexedData, width, height) = IconRLEDecompressor.DecompressAuto(iconData);

// Map indices to RGB
byte[] rgbData = new byte[width * height * 3];
for (int i = 0; i < indexedData.Length; i++) {
    byte index = indexedData[i];
    rgbData[i*3 + 0] = palette8bit[index*3 + 0]; // R
    rgbData[i*3 + 1] = palette8bit[index*3 + 1]; // G
    rgbData[i*3 + 2] = palette8bit[index*3 + 2]; // B
}
```

---

## References

See also:
- `PALETTE_CORRECTED.md` - Complete palette system documentation
- `analysis.md` - Main reverse engineering analysis
- `IconRLEDecompressor.cs` - Decompressor tool

---
*Last updated: October 25, 2025*
*Corrected palette addresses based on user discovery*
