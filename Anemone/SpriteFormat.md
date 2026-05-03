# Anemone Sprite Format Analysis

## File Structure

The sprite file (`.spr`) has the following structure:

1.  **File Header** (52 bytes)
    *   Signature: "Game Sprite File" (null-terminated, rest is padding/unknown).
    *   Read by `validate_sprite_header`.

2.  **Sprite List Header**
    *   `Count` (4 bytes): Number of sprites in the file.
    *   `Unknown` (4 bytes): Possibly a flag for extra data (like palettes).
    *   Read by `read_sprite_images`.

3.  **Sprite Data** (Repeated `Count` times)
    *   **Sprite Header** (16 bytes)
        *   `Width` (4 bytes)
        *   `Height` (4 bytes)
        *   `Compressed Size` (4 bytes)
        *   `Flags` (4 bytes): Determines if data is compressed/transparent.
    *   **Sprite Pixel Data** (`Compressed Size` bytes)
        *   Read into a buffer.
    *   Processed by `read_sprite_image_metadata`.

## Decompression Logic

The decompression logic is handled in `decompress_sprite_pixels` (formerly `thunk_FUN_0046a380`).

### Uncompressed / Raw (`Flags` == 0)
If the `Flags` field in the sprite header is 0, the data is treated as raw 16-bit pixels.
*   Size: `Width * Height * 2` bytes.
*   The data is simply converted from RGB 555 to RGB 565.

### Compressed / Line-Based (`Flags` != 0)
If the `Flags` field is non-zero, the data uses a line-based RLE/Skip format.

**Structure:**
1.  **Line Count** (2 bytes): Usually equals `Height`.
2.  **Lines**:
    *   **Segment Count** (1 byte): Number of segments in this line.
    *   **Segments** (Repeated `Segment Count` times):
        *   **Skip** (2 bytes): Number of pixels to skip (transparent).
        *   **Run** (2 bytes): Number of *bytes* of pixel data to follow.
        *   **Pixels** (`Run` bytes): The pixel data.

**Offset Table:**
The function `build_sprite_offset_table` scans this same structure to build an array of pointers, pointing to the start of the data for each line. This allows for fast row access during rendering.

## Pixel Format
The pixels are stored as 16-bit values.
*   **Source**: Likely RGB 555 (1 bit unused, 5 Red, 5 Green, 5 Blue).
*   **Conversion**: The function `convert_555_to_565` converts them to RGB 565 by shifting the Red and Green components.
    *   Formula: `((pixel & 0x7fe0) << 1) | (pixel & 0x1f)`
