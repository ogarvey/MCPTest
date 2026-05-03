# Extracting the Color Palette from Fugger 2

## Problem
The decompressed icon data contains **palette indices** (0-255), not direct RGB values. To get correct colors, we need to extract the palette lookup tables from the game.

## Palette Locations in Ghidra

Based on analysis of `DrawRLESprite` (0x00013a56), the palettes are stored at:

### For 8-bit Mode
- **Symbol:** `g_palette8bit`
- **Address:** `0x00083b78`
- **Size:** Approximately 4KB (256 entries × 16 palettes)
- **Format:** Palette index remapping or RGB values

### For 16-bit Mode
- **Low Byte Table:** `g_palette16_LowBytes` @ `0x00083d58`
- **High Byte Table:** `g_palette16_HighBytes` @ `0x00083f38`
- **Size:** Each table has ~4KB (256 entries × 16 palettes)
- **Format:** RGB565 or RGB555 (little-endian)

## How to Extract Palettes in Ghidra

### Method 1: Export Binary Data

1. In Ghidra, go to **Window → Memory Map**
2. Navigate to address `0x00083d58` (for 16-bit palette low bytes)
3. Right-click → **Select Bytes** → Enter size (try 4096 or 8192 bytes)
4. **File → Export Program**
5. Choose **Binary** format
6. Select "Selection" option
7. Save as `palette_low.bin`

8. Repeat for `0x00083f38` (high bytes) → save as `palette_high.bin`

### Method 2: Using Ghidra Script (Python)

```python
# Save this as extract_palette.py in Ghidra's script folder
from ghidra.program.model.address import Address

def extract_palette(addr_str, size, filename):
    addr = toAddr(addr_str)
    bytes_data = getBytes(addr, size)
    
    f = open(filename, 'wb')
    for b in bytes_data:
        f.write(chr(b & 0xFF))
    f.close()
    print("Extracted {} bytes from {} to {}".format(size, addr_str, filename))

# Extract 16-bit palette tables
extract_palette("0x00083d58", 4096, "C:\\temp\\palette_low.bin")
extract_palette("0x00083f38", 4096, "C:\\temp\\palette_high.bin")
extract_palette("0x00083b78", 4096, "C:\\temp\\palette_8bit.bin")
```

### Method 3: Memory Dump at Runtime

If you can run the game in DOSBox debugger:
1. Run the game until graphics are loaded
2. Press `Alt+Pause` to break into debugger
3. Dump memory: `MEMDUMPBIN 0x83D58 4096 palette.bin`

## Combining Low and High Bytes

Once you have both files:

```csharp
byte[] paletteLow = File.ReadAllBytes("palette_low.bin");
byte[] paletteHigh = File.ReadAllBytes("palette_high.bin");
byte[] palette16 = new byte[Math.Min(paletteLow.Length, paletteHigh.Length) * 2];

for (int i = 0; i < paletteLow.Length && i < paletteHigh.Length; i++)
{
    palette16[i * 2] = paletteLow[i];       // Low byte
    palette16[i * 2 + 1] = paletteHigh[i];  // High byte
}

File.WriteAllBytes("palette_rgb565.bin", palette16);
```

## Using the Palette

```csharp
// 1. Decompress the RLE icon
var (indexedData, width, height) = IconRLEDecompressor.DecompressAuto(
    compressedData, 
    IconRLEDecompressor.ColorDepth.Palette8Bit); // Use 8-bit for palette indices!

// 2. Load the palette
byte[] palette16 = File.ReadAllBytes("palette_rgb565.bin");

// 3. Apply palette
byte[] rgb16Data = IconRLEDecompressor.ApplyPalette16(indexedData, palette16);

// 4. Convert to RGB888 if needed
byte[] rgb888Data = IconRLEDecompressor.ConvertRGB565ToRGB888(rgb16Data);
```

## Alternative: Find Palette in Game Files

The palette might also be stored in the icon characteristic files loaded by `InitializeIconGraphicsSystem`:
- `icons?b\icon??in.dat` - Icon characteristics/metadata (may contain palette!)
- Check file `icon??cs.dat` - Character/font data

Try opening these files in a hex editor and look for:
- RGB triplets (gradually changing color values)
- Patterns like: `00 00 00 08 00 00 10 00 00 ...` (VGA palette format)
- Size around 768 bytes (256 colors × 3 bytes RGB)

## Next Steps

1. Extract the palette data using one of the methods above
2. Verify the palette format (RGB565 vs RGB555) by checking color bit patterns
3. Update the decompressor to use palette indices (8-bit mode)
4. Apply the correct palette to get proper colors!
