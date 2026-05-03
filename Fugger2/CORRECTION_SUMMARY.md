# Palette Correction Summary

## What Changed

Thanks to your discovery, we've corrected the palette analysis!

### Incorrect (Before)
- **0x00083b78, 0x00083d58, 0x00083f38** were thought to be the primary palettes
- These are actually **color remapping/translation tables** for sprite effects

### Correct (Now)
- **0x0008383c** - `g_vgaPalette256` - **PRIMARY VGA PALETTE (768 bytes)**
  - Format: 256 colors × 3 bytes (R, G, B)
  - 6-bit values (0-63) for VGA DAC
  - **This is what you need to extract!**

- **0x000835ba** - `g_fadeTable` - Fade/brightness lookup table
  - Starts with "0123456789abcdef????????\r\n$"

## Updated Ghidra Variables

All renamed successfully:

✅ **g_vgaPalette256** (0x0008383c) - VGA palette  
✅ **g_fadeTable** (0x000835ba) - Fade table  
✅ **g_paletteRemapTable** (0x00083b78) - 8-bit remap  
✅ **g_paletteRemap16_LowBytes** (0x00083d58) - 16-bit remap low  
✅ **g_paletteRemap16_HighBytes** (0x00083f38) - 16-bit remap high  
✅ **g_solidColorMap8** (0x00083b3c) - Solid color 8-bit  
✅ **g_solidColorMap16** (0x00083b3d) - Solid color 16-bit  

✅ **InitializeGraphicsMode** (0x00012d74) - Loads VGA palette via INT 10h  
✅ **PrintDebugHex** (0x00012c51) - Uses fade table for hex conversion  

## How It Works

### At Startup
1. `InitializeGraphicsMode` is called
2. Sets up VGA/SVGA video mode
3. **Loads `g_vgaPalette256` into VGA hardware:**
   ```c
   // BIOS INT 10h, function 1012h (Set DAC block)
   (*pcVar1)(0x1012, &g_vgaPalette256, 0x100);  // 256 colors
   ```
4. VGA DAC now contains the 256-color palette

### When Drawing Sprites
`DrawRLESprite` uses different rendering modes:

- **palette == 0**: Direct pixel copy (no lookup)
- **0 < palette < 0x80**: Solid color fill using `g_solidColorMap8/16`
- **palette >= 0x80**: Color remapping using `g_paletteRemapTable` tables

The remap tables allow sprites to use different color variations (team colors, lighting effects, etc.) without storing multiple copies.

## What to Extract

### Primary Palette (CRITICAL)
**Address:** 0x0008383c  
**Name:** g_vgaPalette256  
**Size:** 768 bytes  
**Format:** R0,G0,B0, R1,G1,B1, ..., R255,G255,B255  
**Value range:** 0-63 (6-bit VGA color)  

### Extraction in Ghidra
```
1. Go to address 0x0008383c
2. Select 768 bytes (0x300)
3. File → Export → Binary
4. Save as "fugger2_vga_palette.bin"
```

### Or use Python script
```python
from ghidra.program.model.address import Address

addr = currentProgram.getAddressFactory().getAddress("0x0008383c")
bytes = getBytes(addr, 768)
with open("c:/temp/fugger2_palette.bin", 'wb') as f:
    f.write(bytearray(bytes))
```

## Using the Palette

```csharp
// 1. Load extracted palette
byte[] vgaPalette = File.ReadAllBytes("fugger2_vga_palette.bin");

// 2. Scale 6-bit to 8-bit
byte[] rgb8 = new byte[768];
for (int i = 0; i < 768; i++) {
    rgb8[i] = (byte)((vgaPalette[i] << 2) | (vgaPalette[i] >> 4));
}

// 3. Decompress icon
var (indices, w, h) = IconRLEDecompressor.DecompressAuto(iconData);

// 4. Apply palette
byte[] rgbData = new byte[w * h * 3];
for (int i = 0; i < indices.Length; i++) {
    int idx = indices[i] * 3;
    rgbData[i*3 + 0] = rgb8[idx + 0]; // R
    rgbData[i*3 + 1] = rgb8[idx + 1]; // G
    rgbData[i*3 + 2] = rgb8[idx + 2]; // B
}

// 5. Create image from rgbData
```

## Updated Documentation

📄 **PALETTE_CORRECTED.md** - Complete palette system documentation  
📄 **VARIABLE_NAMING_UPDATED.md** - All renamed variables with corrected info  
📄 **analysis.md** - Main analysis (updated with corrections)  
📄 **PALETTE_FUNCTIONS.md** - Old file (now superseded by PALETTE_CORRECTED.md)  

## Next Step

**Extract the 768-byte VGA palette from 0x0008383c** and your icon colors should finally be correct! 🎨

---
*Corrected: October 25, 2025*
