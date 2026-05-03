# Icon Format Clarification - CRITICAL UPDATE

## The Confusion Resolved

**The icon DAT files contain RGB16 data directly, NOT palette indices!**

## Evidence from DrawRLESprite

### For 16-bit Mode (video mode 2-4), when `bVar3 == 0`:

```c
// Read skip count (1 byte)
uVar8 = (byte)*param_4;
if (uVar8 < 0xFE) {
    puVar20 = puVar20 + uVar8;  // Skip transparent pixels
    
    // Read pixel count (1 byte)
    uVar7 = *(byte *)((int)param_4 + 1);
    param_4 = param_4 + 1;
    
    // Copy RGB16 values directly
    puVar14 = param_4 + uVar7;  // uVar7 ushorts
    for (; param_4 != puVar14; param_4 = param_4 + 1) {
        *puVar20 = *param_4;  // Direct ushort copy (RGB16)
        puVar20 = puVar20 + 1;
    }
}
```

**Key observation:** `param_4` is `ushort *` (16-bit pointer). The pixel data is read as **16-bit values** and copied directly to the screen buffer without any palette lookup!

### For 8-bit Mode (video mode < 2), when `bVar3 == 0`:

```c
for (; param_4 != puVar14; param_4 = (ushort *)((int)param_4 + 1)) {
    *(byte *)puVar20 = (byte)*param_4;  // Copy 8-bit palette index
    puVar20 = (ushort *)((int)puVar20 + 1);
}
```

In 8-bit mode, it reads **8-bit palette indices** and writes them to screen buffer. The VGA hardware then uses the loaded palette to display colors.

## What the VGA Palette Is For

The VGA palette at **0x0008383c** is loaded into the **VGA hardware DAC** (Digital-to-Analog Converter) during graphics initialization:

```c
// InitializeGraphicsMode (0x00012d74)
pcVar1 = (code *)swi(0x10);
(*pcVar1)(0x1012, &g_vgaPalette256, 0x100);  // BIOS INT 10h, AX=1012h
```

This palette is used by the **VGA hardware** to convert 8-bit palette indices (0-255) to RGB analog signals for CRT monitors in **8-bit VGA mode** (320x200, 256 colors).

In **16-bit SVGA modes**, the VGA palette is bypassed - the game writes RGB values directly!

## Icon File Format (CORRECTED)

### For 16-bit Icons (Most Common)

RLE format:
```
Loop:
  Skip Count (1 byte, 0x00-0xFD) - Number of transparent pixels to skip
  Pixel Count (1 byte) - Number of RGB16 pixels to follow
  RGB16 Data (Pixel Count × 2 bytes) - Actual RGB565 or RGB555 color values
  
  Control bytes:
    0xFE - End of scanline, move to next row
    0xFF - End of sprite
```

### For 8-bit Icons (Less Common)

RLE format:
```
Loop:
  Skip Count (1 byte, 0x00-0xFD) - Number of transparent pixels
  Pixel Count (1 byte) - Number of palette indices
  Palette Indices (Pixel Count × 1 byte) - VGA palette indices (0-255)
  
  Control bytes:
    0xFE - End of scanline
    0xFF - End of sprite
```

## When Palette Remapping IS Used

The remap tables (`g_paletteRemapTable`, `g_paletteRemap16_LowBytes/HighBytes`) are used when `bVar3 >= 0x80`:

```c
// For 16-bit mode with remapping (bVar3 >= 0x80):
else {
    iVar18 = (param_5 & 0x7f) * 0x10;  // Select remap palette
    for (; param_4 != puVar20; param_4 = (ushort *)((int)param_4 + 1)) {
        *puVar19 = CONCAT11((&g_paletteRemap16_HighBytes)[iVar18 + (uint)(byte)*param_4],
                            (&g_paletteRemap16_LowBytes)[iVar18 + (uint)(byte)*param_4]);
        puVar19 = puVar19 + 1;
    }
}
```

In this case, the icon stores **8-bit indices** that get remapped to RGB16 through the remap tables. This allows:
- Team colors (red team vs blue team)
- Lighting effects (dark corridors vs bright areas)
- Player customization

But the **default rendering mode (bVar3 == 0) uses direct RGB16 values!**

## How to Decode Icons (CORRECTED)

Your decompressor was almost correct! The issue is treating the data correctly based on video mode:

### For 16-bit Icons (Default Case)

```csharp
public static (byte[] rgbData, int width, int height) DecompressAuto(
    byte[] compressedData, 
    ColorDepth colorDepth = ColorDepth.RGB16)  // RGB16 is default!
{
    // Auto-detect dimensions
    var (width, height) = DetectDimensions(compressedData);
    
    if (colorDepth == ColorDepth.RGB16) {
        // Read as ushort values directly
        ushort[] rgb16Data = new ushort[width * height];
        // ... RLE decompression treating pixel data as ushorts ...
        
        // Convert RGB565/555 to RGB888
        byte[] rgb24 = new byte[width * height * 3];
        for (int i = 0; i < rgb16Data.Length; i++) {
            var (r, g, b) = ConvertRGB565ToRGB888(rgb16Data[i]);
            rgb24[i*3 + 0] = r;
            rgb24[i*3 + 1] = g;
            rgb24[i*3 + 2] = b;
        }
        return (rgb24, width, height);
    }
    // else handle 8-bit palette mode...
}
```

### The Problem You Encountered

When you treated RGB16 data as 8-bit palette indices:
- Each RGB16 value (2 bytes) was split into two separate pixels
- Width appeared doubled
- Colors were nonsense (random palette lookups)

## Correct Interpretation

**Icon files store the FINAL color data ready for display:**
- In 16-bit modes: RGB16 values (RGB565 or RGB555)
- In 8-bit mode: VGA palette indices (0-255)

**The VGA palette is for the hardware, not for decoding icons!**

## Updated IconRLEDecompressor.cs

Your decompressor should default to RGB16 mode:

```csharp
public enum ColorDepth
{
    Palette8Bit,    // Uses VGA palette (rare for icons)
    RGB16,          // Direct RGB565/555 (DEFAULT for icon files!)
    RGB32
}
```

The pixel count in the RLE stream tells you:
- **For RGB16**: Number of ushorts (16-bit values) to read
- **For Palette8Bit**: Number of bytes (8-bit indices) to read

## Why This Makes Sense

Fugger 2 runs in SVGA modes (640x480 16-bit, 1024x768 16-bit) where direct RGB is more efficient than palette lookups. The icon files were created for these high-color modes, not VGA's 256-color mode.

The VGA palette exists for:
1. Backwards compatibility with 320x200 VGA mode
2. Text mode colors
3. The fade table for screen transitions

---
*Clarified: October 25, 2025*
*Based on detailed DrawRLESprite code analysis*
