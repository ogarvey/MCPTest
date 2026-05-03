# Palette-Related Functions and Data - Fugger 2

## Summary

After thorough investigation, **there are NO dedicated functions** that specifically manage or initialize the palette data in Fugger 2. The palette tables are **statically compiled** into the executable as data arrays.

## Palette Data Locations (Static Arrays)

These are **data arrays**, not functions:

| Address | Symbol Name (attempted) | Actual Name in Code | Description |
|---------|-------------------------|---------------------|-------------|
| 0x00083b78 | g_palette8bit | `DAT_00083b78` | 8-bit palette table (~4KB) |
| 0x00083d58 | g_palette16_LowBytes | `DAT_00083d58` | 16-bit palette low bytes (~4KB) |
| 0x00083f38 | g_palette16_HighBytes | `DAT_00083f38` | 16-bit palette high bytes (~4KB) |

**Note:** These symbols could not be auto-renamed via API because they are accessed with offset indexing. They remain as `DAT_*` in decompiled code but have detailed comments at their addresses.

## Functions That USE Palette Data

### DrawRLESprite (0x00013a56)
**Primary palette consumer** - Uses the palette tables during sprite rendering:

**Usage locations:**
- **0x00013e34**: References `DAT_00083b78` for 8-bit palette lookup
- **0x00013fac**: References `DAT_00083d58` (low bytes) for 16-bit palette
- **0x00013fb3**: References `DAT_00083f38` (high bytes) for 16-bit palette

**Code patterns:**
```c
// 8-bit mode palette lookup (bVar3 >= 0x80):
*(undefined *)puVar20 = (&DAT_00083b78)[(uint)(byte)*param_4 + (param_5 & 0x7f) * 0x10];

// 16-bit mode palette lookup (bVar3 >= 0x80):
*puVar19 = CONCAT11((&DAT_00083f38)[iVar18 + (uint)(byte)*param_4],
                    (&DAT_00083d58)[iVar18 + (uint)(byte)*param_4]);
```

### InitializeIconGraphicsSystem (0x0002f300)
**Does NOT initialize palettes** - Only sets up icon cache and loads icon metadata.

The function loads:
- Icon characteristics (`icon??in.dat`)
- Character/font data (`icon??cs.dat`)
- But NOT palette data (already in executable)

## Why There Are No Palette Management Functions

1. **Static Compilation**: The palettes are compiled directly into the executable as data
2. **No Runtime Loading**: Unlike some games that load `.PAL` files, Fugger 2 has hardcoded palettes
3. **Direct Access**: `DrawRLESprite` accesses the palette arrays directly without wrapper functions
4. **Single Palette Set**: The game doesn't appear to swap or modify palettes at runtime

## Palette Structure

Based on the code analysis:

### 8-bit Palette (`DAT_00083b78`)
- **Size**: ~4KB
- **Structure**: 16 palettes × 256 entries each
- **Access**: `palette8bit[(paletteIndex & 0x7F) * 0x10 + pixelIndex]`
- **Usage**: Selected by lower 7 bits of rendering flags parameter

### 16-bit Palette (`DAT_00083d58` + `DAT_00083f38`)
- **Size**: ~4KB each (low and high bytes)
- **Structure**: 16 palettes × 256 entries each
- **Access**: Combined low + high bytes form RGB565/555 color
- **Usage**: Selected by lower 7 bits of rendering flags parameter

## Extracting Palette Data

Since there are no functions to analyze, extract the palette data directly:

### In Ghidra GUI:
1. Navigate to address `0x00083d58`
2. Select ~4096 bytes
3. File → Export → Binary
4. Repeat for `0x00083f38` and `0x00083b78`

### Using Ghidra Script:
```python
# Extract palette data
from ghidra.program.model.address import Address

def save_bytes(addr, size, filename):
    address = currentProgram.getAddressFactory().getAddress(addr)
    bytes = getBytes(address, size)
    with open(filename, 'wb') as f:
        f.write(bytearray(bytes))

# Extract all palette tables
save_bytes("0x00083d58", 4096, "palette16_low.bin")
save_bytes("0x00083f38", 4096, "palette16_high.bin")
save_bytes("0x00083b78", 4096, "palette8bit.bin")
```

## Alternative: Check Game Files

The palettes might also exist in game data files:
- `icon??in.dat` - Icon characteristics (worth checking for palette data)
- `icon??cs.dat` - Character/font file (might contain palette info)

## Conclusion

**No functions to rename** - The palette system is entirely data-driven with statically compiled lookup tables. The only function that interacts with palettes is `DrawRLESprite`, which has already been renamed and documented.

For proper color decoding, you must:
1. Extract the palette data arrays from the executable
2. Use palette indices from decompressed icons
3. Look up RGB values in the extracted palette tables

---
*Analysis completed: October 25, 2025*
