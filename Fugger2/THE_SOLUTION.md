# THE SOLUTION - No Palette Needed!

## TL;DR

**Your icons are ALREADY in RGB16 format. Just decompress and convert RGB565→RGB24. No palette needed!**

## What Was Wrong

You were treating the decompressed RGB16 data as 8-bit palette indices, which caused:
- **Width doubled** (each 16-bit RGB value split into two 8-bit "indices")
- **Wrong colors** (random palette lookups on RGB data)
- **Noise/artifacts** (as seen in your attached image)

## The Correct Process

```csharp
using Fugger2;

// Load icon file
byte[] iconData = File.ReadAllBytes("icon00.dat");

// Decompress directly to RGB24 (auto-detects width/height)
var (rgb24Data, width, height) = IconRLEDecompressor.DecompressToRGB24(iconData);

// Save as raw RGB24
File.WriteAllBytes("icon00_rgb24.raw", rgb24Data);

// Or create a bitmap
Bitmap bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);
BitmapData bmpData = bmp.LockBits(
    new Rectangle(0, 0, width, height),
    ImageLockMode.WriteOnly,
    PixelFormat.Format24bppRgb);
Marshal.Copy(rgb24Data, 0, bmpData.Scan0, rgb24Data.Length);
bmp.UnlockBits(bmpData);
bmp.Save("icon00.png");
```

## Why The Confusion?

The game has TWO rendering paths in `DrawRLESprite`:

### Path 1: Direct RGB16 (Default - bVar3 == 0)
```c
for (; param_4 != puVar14; param_4 = param_4 + 1) {
    *puVar20 = *param_4;  // Direct ushort copy!
    puVar20 = puVar20 + 1;
}
```
**Icon files use this mode** - they store RGB565/555 values directly.

### Path 2: Palette Remapping (bVar3 >= 0x80)
```c
*puVar19 = CONCAT11((&g_paletteRemap16_HighBytes)[index],
                    (&g_paletteRemap16_LowBytes)[index]);
```
This is for **special effects** (team colors, lighting) - NOT the default!

## The VGA Palette Purpose

The VGA palette at **0x0008383c** is for:
1. **8-bit VGA mode** (320x200, 256 colors) - hardware palette
2. **Text mode** colors
3. **Fade effects** (screen transitions)

It is **NOT** used to decode 16-bit icon files!

## Test Your Icons

Compile and run:

```bash
csc IconRLEDecompressor.cs TestIconDecoder.cs
TestIconDecoder.exe icon00.dat
```

This creates:
- `icon00_rgb565.raw` - RGB24 data
- `icon00_rgb555.raw` - Alternative if RGB565 looks wrong

Open in GIMP:
1. File → Open → icon00_rgb565.raw
2. Image Type: **RGB**
3. Width/Height: (shown in console)
4. Click OK

**You should see correct colors now!**

## If Colors Still Look Wrong

1. **Try RGB555 instead of RGB565** - The `DecompressToRGB24()` method has a parameter:
   ```csharp
   var (rgb24, w, h) = DecompressToRGB24(data, useRGB555: true);
   ```

2. **Check for byte swapping** - Some systems store RGB16 in different endian orders

3. **Verify it's actually a 16-bit icon** - Some rare icons might be 8-bit

## Code Changes Made

1. **IconRLEDecompressor.cs** - Updated comments explaining RGB16 is default
2. **Added `DecompressToRGB24()` method** - One-step decompress + convert
3. **Created TestIconDecoder.cs** - Simple test program
4. **ICON_FORMAT_CLARIFIED.md** - Detailed technical explanation

## The Bottom Line

```
Icon DAT file (RLE compressed)
         ↓
   Decompress RLE
         ↓
   RGB16 data (RGB565 or RGB555)
         ↓
   Convert to RGB24
         ↓
      Display!

NO PALETTE STEP NEEDED!
```

---
*Problem solved: October 25, 2025*
*The palette was a red herring - icons already have RGB data!*
