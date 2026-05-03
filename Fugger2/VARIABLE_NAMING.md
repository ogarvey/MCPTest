# Ghidra Variable Renaming - Fugger 2

## Summary of Renamed Variables

This document tracks all variables that have been renamed in Ghidra for better code readability.

## Palette Data (Critical for Color Decoding)

| Address | Old Name | New Name | Description |
|---------|----------|----------|-------------|
| 0x00083b78 | DAT_00083b78 | `g_palette8bit` | 8-bit palette lookup table (~4KB) |
| 0x00083d58 | DAT_00083d58 | `g_palette16_LowBytes` | 16-bit palette low bytes (~4KB) |
| 0x00083f38 | DAT_00083f38 | `g_palette16_HighBytes` | 16-bit palette high bytes (~4KB) |

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

## Video/Graphics System

| Address | Old Name | New Name | Description |
|---------|----------|----------|-------------|
| 0x000832ac | DAT_000832ac | `g_videoModeInfo` | Video mode config (offset +0x82 = color depth) |
| 0x000ef38a | DAT_000ef38a | `g_videoMemorySegment` | Video memory segment selector (DOS) |
| 0x0008334d | DAT_0008334d | `g_screenWidth` | Screen width in pixels |
| 0x00083351 | DAT_00083351 | `g_screenPitch` | Screen scanline pitch (bytes per row) |
| 0x00084d3c | DAT_00084d3c | `g_drawNestingLevel` | Drawing operation nesting counter |

## Functions Renamed

| Address | Old Name | New Name | Purpose |
|---------|----------|----------|---------|
| 0x0002f300 | FUN_0002f300 | `InitializeIconGraphicsSystem` | Initialize graphics, load palettes, setup cache |
| 0x0002dbc0 | FUN_0002dbc0 | `LoadIconFile` | Load icon with LRU cache management |
| 0x00013a56 | FUN_00013a56 | `DrawRLESprite` | Decompress and draw RLE sprite with palette lookup |
| 0x000134af | FUN_000134af | `BlitSprite` | Fast uncompressed sprite blit with transparency |
| 0x0002e400 | FUN_0002e400 | `HandleMouseInput` | Mouse event handler |
| 0x0006a060 | FUN_0006a060 | `FileOpen` | DOS file open wrapper |
| 0x0006a084 | FUN_0006a084 | `FileOpenImpl` | DOS file open implementation |
| 0x0006a2d2 | FUN_0006a2d2 | `FileRead` | DOS file read with CR/LF handling |
| 0x0006adea | thunk_FUN_0006adea | `FileClose` | DOS file close wrapper |

## Code Comments Added

### DrawRLESprite (0x00013a56)
- **Function comment:** "Main RLE sprite drawing function. Decompresses RLE-encoded sprite data and renders to screen buffer. Supports palette lookup, transparency, and multiple color depths (8-bit, 16-bit, 32-bit)."
- **0x00013ab8:** "RLE control byte: 0xFF = end sprite, 0xFE = end scanline, <=0xFD = skip count"
- **0x00013ac9:** "Skip transparent pixels, then read pixel run count"
- **0x00013e78:** "8-bit palette lookup: pixel_byte -> g_palette8bit[pixel_byte + palette_offset]"
- **0x00013f42:** "16-bit palette lookup: combines g_palette16_HighBytes and g_palette16_LowBytes based on pixel index"

### LoadIconFile (0x0002dbc0)
- **Function comment:** "Loads icon file with LRU cache management. Takes icon index (0-0xFFF), checks cache, evicts old icons if memory full, opens icon??.dat file, reads data, returns pointer to loaded icon."

### InitializeIconGraphicsSystem (0x0002f300)
- **Function comment:** "Initializes the icon/graphics system. Loads icon characteristics, sets up 512-slot cache, pre-loads common icons, initializes palette tables, sets up mouse handler."

## Key Findings from Renaming

1. **Palette System**: The game uses separate lookup tables for different color depths, with the ability to select from 16 different palettes per depth mode.

2. **Icon Cache**: Sophisticated LRU cache with 512 slots, tracking state, size, and memory usage for efficient resource management.

3. **Color Depth Support**: Video mode info at offset +0x82 determines rendering path:
   - < 2: 8-bit palette mode
   - < 5: 16-bit RGB mode
   - >= 5: 32-bit RGB mode

4. **Memory Management**: Tight memory constraints typical of DOS era - comprehensive tracking of available/used memory, base addresses, and dynamic eviction.

## Usage in Analysis

These renamed symbols make the decompiled code much more readable. For example, instead of:

```c
*(undefined *)puVar20 = (&DAT_00083b78)[(uint)(byte)*param_4 + (param_5 & 0x7f) * 0x10];
```

You now see:

```c
*(undefined *)puVar20 = g_palette8bit[(uint)(byte)*param_4 + (param_5 & 0x7f) * 0x10];
```

This immediately clarifies that this is a palette lookup operation, not arbitrary data access.

---
*Last updated: October 25, 2025*
