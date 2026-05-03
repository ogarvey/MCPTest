# Eye of Typhoon - Reverse Engineering

This folder contains the reverse engineering work for the DOS game "Eye of Typhoon" (태풍의 눈).

## Files

- **analysis.md** - Detailed analysis documentation of all findings
- **CHASpriteDecompressor.cs** - Sprite decompression implementation
- **TestExtractor.cs** - Example program to extract sprites from game files

## Quick Start

### Understanding the Graphics Format

Eye of Typhoon uses these file types for graphics:
- **CHA files** - Compressed sprite data (RLE-compressed, VGA Mode X format)
- **PID files** - Picture Index Data (sprite size directory)
- **PAL files** - VGA palette data (256 colors, RGB format)

### Using the Decompressor

```csharp
// Load files
byte[] chaData = File.ReadAllBytes("character.cha");
byte[] pidData = File.ReadAllBytes("character.pid");

// Extract all sprites
var sprites = CHASpriteDecompressor.ExtractAllSprites(chaData, pidData);

// Access individual sprite
var sprite = sprites[0];
Console.WriteLine($"Sprite: {sprite.Width}x{sprite.Height}");

// Pixels are 8-bit indexed (0 = transparent, 1-15 = palette indices)
byte[] pixels = sprite.Pixels;
```

### Sprite Format Details

**CHA File Structure:**
- Multiple sprites concatenated together
- Sprite sizes defined in corresponding PID file

**Individual Sprite Structure:**
```
Offset  Size    Description
------  ------  -----------
0x00    WORD    Width (in pixels)
0x02    WORD    Height (in pixels)
0x04    BYTE[]  RLE-compressed pixel data
```

**Note**: Width is stored as actual pixels. The rendering code divides by 4 for VGA Mode X planar processing.

**RLE Compression:**
- Control byte < 0x80: Single pixel (low nibble = palette index)
- Control byte >= 0x80: Run of pixels
  - Bits 0-6 (& 0x7F) = run length
  - Next byte (& 0x0F) = palette index to repeat

## Ghidra Function Mapping

### Main Functions
| Address    | Function Name         | Purpose |
|------------|-----------------------|---------|
| 0x00426087 | LoadCHAFile          | Main sprite data loader |
| 0x00420b48 | LoadEndingAnimation  | Loads ending/demo graphics |
| 0x0042037e | ShowIntroSequence    | Plays intro sequence |
| 0x00425dc8 | LoadGanjiData        | Loads special graphics set |

### Decompression Functions
| Address    | Function Name          | Purpose |
|------------|------------------------|---------|
| 0x00422b98 | BlitSpriteRLE_Down    | Draws sprites downward (RLE decode) |
| 0x004226c5 | BlitSpriteRLE_Up      | Draws sprites upward (RLE decode) |
| 0x0042292c | BlitSpriteRLE_UpReverse | Draws sprites upward reversed |

### Helper Functions
| Address    | Function Name    | Purpose |
|------------|------------------|---------|
| 0x00429354 | OpenFile        | Opens a file |
| 0x004295c0 | FileRead        | Reads from file (fread-like) |
| 0x0042a828 | GetFileSize     | Gets file size |
| 0x00429490 | BufferedRead    | Low-level buffered read |
| 0x00429034 | CloseFile       | Closes file handle |
| 0x0042b484 | AllocateMemory  | Allocates memory (malloc) |
| 0x0042b348 | FreeMemory      | Frees memory (free) |
| 0x00429768 | ReadDWord       | Reads 32-bit value |

## Technical Details

### VGA Mode X
The game uses VGA Mode X (planar 320x200x256):
- 4 color planes
- 4 bits per pixel per plane
- Direct VGA hardware access via port 0x3C4
- Sprites stored with width ÷ 4 due to planar architecture

### Transparency
- Palette index 0 is transparent (not drawn)
- RLE runs of 0 pixels skip without drawing

### Color Remapping
Sprites support color remapping via a paletteBase parameter that is OR'd with pixel values during rendering.

## Next Steps

1. Test with actual game files
2. Implement palette loading (.PAL files)
3. Analyze animation data (ACT/IDX files)
4. Create sprite viewer with palette support
5. Document animation system

## References

- Analysis document: `analysis.md`
- Ghidra project: (Eye of Typhoon DOS executable)
- Original game: DOS, Korean, circa 1990s
