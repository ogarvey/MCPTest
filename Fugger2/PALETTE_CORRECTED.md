# CORRECTED Palette Analysis - Fugger 2

## Key Correction

**Previous understanding was INCORRECT**. The actual palette structure is:

### VGA Palette (Primary - Extract This!)
- **`g_vgaPalette256` at 0x0008383c** - **768 bytes**
  - Standard VGA palette format: 256 colors × 3 bytes (R, G, B)
  - RGB values are 6-bit (0-63) for VGA DAC
  - **This is loaded at runtime via BIOS interrupt 0x10, function 0x1012**
  - **This is the palette you need to extract!**

### Fade Table (Utility Data)
- **`g_fadeTable` at 0x000835ba** 
  - Starts with string "0123456789abcdef????????\r\n$"
  - Fade/brightness lookup table for palette fading effects
  - Used by `PrintDebugHex` function

### Palette Remap/Translation Tables
These are NOT the primary palettes but rather **color remapping tables** used for sprite effects:

- **`g_paletteRemapTable` at 0x00083b78** - 4096 bytes
  - 8-bit palette remapping table
  - 16 palettes × 256 entries
  - Used when palette index >= 0x80 to remap sprite colors

- **`g_paletteRemap16_LowBytes` at 0x00083d58** - 4096 bytes
- **`g_paletteRemap16_HighBytes` at 0x00083f38** - 4096 bytes
  - 16-bit palette remap tables (low/high byte split)

### Solid Color Maps
- **`g_solidColorMap8` at 0x00083b3c**
- **`g_solidColorMap16` at 0x00083b3d**
  - Used when palette < 0x80 for solid color fills

## Functions

### InitializeGraphicsMode (0x00012d74)
**This is the key function!** It initializes graphics and **loads the VGA palette**:

```c
// At address 0x000130b8, it calls BIOS interrupt 0x10 function 0x1012:
pcVar1 = (code *)swi(0x10);
(*pcVar1)(0x1012, &g_vgaPalette256, 0x100);  // Load 256 colors
```

**Function 0x1012**: "Set block of DAC color registers"
- Loads 256 RGB triplets from `g_vgaPalette256` into VGA DAC

### PrintDebugHex (0x00012c51)
Debug function that uses `g_fadeTable` for hex digit conversion (not palette-related)

### DrawRLESprite (0x00013a56)
Uses the various palette/remap tables depending on rendering mode:

**8-bit mode with remapping (palette >= 0x80):**
```c
*(undefined *)puVar20 = (&g_paletteRemapTable)[(uint)(byte)*param_4 + (param_5 & 0x7f) * 0x10];
```

**16-bit mode with remapping (palette >= 0x80):**
```c
*puVar19 = CONCAT11((&g_paletteRemap16_HighBytes)[iVar18 + (uint)(byte)*param_4],
                    (&g_paletteRemap16_LowBytes)[iVar18 + (uint)(byte)*param_4]);
```

## How the Palette System Works

### Palette Loading (At Startup)
1. `InitializeGraphicsMode` is called
2. Sets up video mode (VGA/SVGA)
3. Loads `g_vgaPalette256` (768 bytes) into VGA hardware via INT 10h/1012h
4. VGA DAC now contains 256 RGB colors

### Sprite Rendering
When `DrawRLESprite` is called with palette flags:

- **palette == 0**: Direct pixel copy (no palette lookup)
- **0 < palette < 0x80**: Solid color fill mode using `g_solidColorMap8/16`
- **palette >= 0x80**: Color remapping using `g_paletteRemapTable` or `g_paletteRemap16_*`

### The Confusion
The remap tables at 0x00083b78, 0x00083d58, 0x00083f38 are **NOT the primary palettes**. They are:
- Color translation tables for sprite effects
- Secondary lookups that map sprite indices to different palette variations
- Used for things like player colors, team colors, lighting effects

## What to Extract

### For Basic Color Decoding:
**Extract `g_vgaPalette256` at 0x0008383c (768 bytes)**

This gives you the 256-color VGA palette in format:
```
R0, G0, B0, R1, G1, B1, ..., R255, G255, B255
```

Each value is 6-bit (0-63), scale to 8-bit by: `RGB8 = RGB6 * 4` or `RGB8 = (RGB6 << 2) | (RGB6 >> 4)`

### Extraction Methods

#### Method 1: Ghidra GUI
1. Go to address `0x0008383c`
2. Select 768 bytes (0x300 bytes)
3. File → Export → Binary
4. Save as `vga_palette.bin`

#### Method 2: Ghidra Script
```python
from ghidra.program.model.address import Address

def save_palette():
    addr = currentProgram.getAddressFactory().getAddress("0x0008383c")
    bytes = getBytes(addr, 768)
    with open("c:/temp/fugger2_vga_palette.bin", 'wb') as f:
        f.write(bytearray(bytes))

save_palette()
```

#### Method 3: DOSBox Memory Dump
```
# In DOSBox debugger after game starts:
MEMDUMPBIN 0835:083C 768 vga_palette.bin
```

## Updated Decompressor Usage

```csharp
// Extract VGA palette (768 bytes) from 0x0008383c
byte[] vgaPalette = File.ReadAllBytes("vga_palette.bin");

// Convert to 8-bit RGB (256 colors × 3 bytes)
byte[] palette8bit = new byte[768];
for (int i = 0; i < 768; i++) {
    // VGA uses 6-bit color (0-63), scale to 8-bit (0-255)
    palette8bit[i] = (byte)((vgaPalette[i] << 2) | (vgaPalette[i] >> 4));
}

// Decompress icon
var (indexedData, width, height) = IconRLEDecompressor.DecompressAuto(
    iconData, ColorDepth.Palette8Bit);

// Apply VGA palette
byte[] rgbData = new byte[width * height * 3];
for (int i = 0; i < indexedData.Length; i++) {
    byte paletteIndex = indexedData[i];
    rgbData[i * 3 + 0] = palette8bit[paletteIndex * 3 + 0]; // R
    rgbData[i * 3 + 1] = palette8bit[paletteIndex * 3 + 1]; // G
    rgbData[i * 3 + 2] = palette8bit[paletteIndex * 3 + 2]; // B
}

// Save as image
// ... create bitmap from rgbData ...
```

## Ghidra Variables Updated

✅ `g_vgaPalette256` (0x0008383c) - VGA palette (768 bytes)  
✅ `g_fadeTable` (0x000835ba) - Fade/brightness table  
✅ `g_paletteRemapTable` (0x00083b78) - 8-bit remap table (4096 bytes)  
✅ `g_paletteRemap16_LowBytes` (0x00083d58) - 16-bit remap low (4096 bytes)  
✅ `g_paletteRemap16_HighBytes` (0x00083f38) - 16-bit remap high (4096 bytes)  
✅ `g_solidColorMap8` (0x00083b3c) - Solid color map 8-bit  
✅ `g_solidColorMap16` (0x00083b3d) - Solid color map 16-bit  
✅ `InitializeGraphicsMode` (0x00012d74) - Loads VGA palette  
✅ `PrintDebugHex` (0x00012c51) - Debug hex print function  

---
*Analysis corrected: October 25, 2025*
*Thanks to user's discovery of the actual VGA palette at 0x0008383c!*
