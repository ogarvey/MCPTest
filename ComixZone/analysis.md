# Comix Zone Analysis

## Initial Findings
- User is investigating `SPRITE.bin`.
- File structure: Offset of first sprite, followed by list of offsets to sprite headers.
- Target function for analysis: `FUN_0040379c` (Renamed to `GameMain`).
- Sample sprite data provided suggests a chunk-based format.

## Function Analysis

### GameMain (FUN_0040379c)
- This is the main entry point of the game (WinMain).
- It initializes the window, checks system requirements, and loads resources.
- It calls `LoadFileToMemory` (FUN_00401000) to load `SPRITES.BIN`.
- The pointer to the loaded sprite data is stored in `g_SpritesBinData` (DAT_006298a0).

### Sprite Rendering Pipeline
1.  **PrepareSprite (FUN_00407713)**:
    - Calculates the address of the sprite header using `g_SpritesBinData` and an index.
    - Reads sprite dimensions and offsets.
    - Fills a "Draw Command" structure (pointed to by `EBP`).
    - Sets the data pointer (offset 12 in structure) to the start of the pixel data for the sprite.

2.  **DrawSprite_RLE (FUN_0040422c)**:
    - Iterates over the lines of the sprite (handling clipping and Y coordinates).
    - Calls `DrawSpriteLine_RLE` for each line.

3.  **DrawSpriteLine_RLE (FUN_004045b9)**:
    - This function implements the decompression and drawing logic for a single line (or row) of the sprite.
    - It reads from the sprite data pointer (`ESI`).
    - It writes to the screen/buffer pointer (`EDI`).

## Sprite Data Format & Decompression Logic

The sprite data is stored in a sparse, RLE-like format.
Each sprite consists of a sequence of lines.
Each line consists of a sequence of **Chunks**.
The line ends with a `00` byte.

### Chunk Format
`[Count] [X Offset] [Pixel Data...]`

-   **Count (1 byte)**: The number of opaque pixels in this chunk.
    -   If `Count` is `00`, it marks the **End of Line**.
-   **X Offset (1 byte)**: The starting X coordinate for this chunk, relative to the sprite's left edge (or current line start).
-   **Pixel Data (Count bytes)**: The actual pixel indices/colors.

### Decompression Algorithm (Pseudocode)

```python
def decompress_sprite(data_ptr):
    # Loop through lines (handled by caller DrawSprite_RLE)
    while not end_of_sprite:
        # Loop through chunks in a line (DrawSpriteLine_RLE)
        while True:
            count = read_byte(data_ptr)
            data_ptr += 1
            
            if count == 0:
                # End of Line
                break
            
            x_start = read_byte(data_ptr)
            data_ptr += 1
            
            # Draw 'count' pixels at 'base_x + x_start'
            for i in range(count):
                pixel = read_byte(data_ptr)
                data_ptr += 1
                draw_pixel(base_x + x_start + i, current_y, pixel)
```

### Verification with Sample Data
Sample: `04 04 81 81 81 81 03 0A 81 81 81 00`

1.  **Chunk 1**:
    -   Count: `04`
    -   X Offset: `04`
    -   Data: `81 81 81 81` (4 bytes)
    -   *Draws 4 pixels at X=4.*

2.  **Chunk 2**:
    -   Count: `03`
    -   X Offset: `0A` (10)
    -   Data: `81 81 81` (3 bytes)
    -   *Draws 3 pixels at X=10.*

3.  **End of Line**:
    -   Count: `00` -> Break.

This matches the visual structure of a sprite line with transparent gaps.

## Renamed Functions
-   `FUN_0040379c` -> `GameMain`
-   `FUN_00401000` -> `LoadFileToMemory`
-   `FUN_00407713` -> `PrepareSprite`
-   `FUN_0040422c` -> `DrawSprite_RLE`
-   `FUN_004045b9` -> `DrawSpriteLine_RLE`

## Palette Information
-   **Source**: Reads from `0x004325a5` in memory, but the file appears to contain zeros at this location.
-   **Format**: `PALETTEENTRY` (Red, Green, Blue, Flags).
-   **Loading**: Copied to `BITMAPINFO` in `WndProc` for indices 10-245.
-   **Wingpal**: The presence of `Wingpal.exe` suggests the use of WinG Identity Palettes. The actual palette data might be generated or loaded from elsewhere.
-   See [Palette.md](Palette.md) for details.
