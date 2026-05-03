# Comix Zone Palette Usage Analysis

## Palette Index in Header
The 5th byte of the component record in the sprite header (identified as "Palette/Attribute") is read by `FUN_00407b6d`.
It is passed to `PrepareSprite` (`FUN_00407713`) and stored in the **Draw Command Flags** (offset 0x08 in the command structure).

## Usage in Drawing
The drawing function `DrawSprite_RLE` (`FUN_0040422c`) checks these flags.
-   **Bit 0 (LSB)**: If set, it enables **Palette Remapping**.
-   **Other Bits**: Do not appear to affect the drawing logic directly in `DrawSprite_RLE`.

## Palette Remapping Mechanism
When remapping is enabled (`Flags & 1`), the function uses a lookup table located at **`0x004329a5`**.
-   The code explicitly loads this address: `LEA EBX, [0x4329a5]`.
-   Pixels are drawn as `Table[PixelValue]`.
-   This table is 1024 bytes long (likely 4 sets of 256 bytes, or just one large table).
-   Since `DrawSprite_RLE` always uses the base address `0x4329a5`, it implies that either:
    1.  There is only one active remapping table for the frame.
    2.  The sprite pixel values are ranged (e.g., 0-63 for Palette 1, 64-127 for Palette 2) so they map to different sections of the table.

## Conclusion
The "Palette Index" in the header acts more like a **Render Flags** field.
-   Values like `0xF1` (Bit 0 set) trigger remapping.
-   Values like `0x02` (Bit 0 clear) use raw pixel values (direct index into system palette).

The actual palette data (RGB values) is likely loaded into the system palette (WinG), and `0x4329a5` serves as a translation table for special effects or color cycling.
