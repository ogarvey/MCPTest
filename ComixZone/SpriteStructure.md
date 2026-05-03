# Comix Zone Sprite Structure Analysis

## Overview
Sprites in Comix Zone appear to be composite objects. A single "Sprite" entry in the `SPRITES.BIN` file can define a collection of sub-sprites (components) that are assembled to form the final character or object.

## Sprite Header Format
The header is variable-length.

| Offset | Size | Description |
| :--- | :--- | :--- |
| 0x00 | 4 | **Offset** (in file?) |
| 0x04 | 2 | **X Offset** (Signed Short) |
| 0x06 | 2 | **Y Offset** (Signed Short) |
| 0x08 | 2 | **Width** |
| 0x0A | 2 | **Height** |
| 0x0C | 4 | **Sprite Data Offset** (Relative to `SPRITES.BIN` data start) |
| 0x10 | 2 | **Unk1** (Flags? Usually 0) |
| 0x12 | 2 | **Total Header Size** (Unk2) |
| 0x14 | Variable | **Component Records** (5 bytes each) |

*Note: Offsets in this table are based on the log structure. In memory (short* pointer), indices are different.*

## Component Record Format
Each component record is 5 bytes long.

| Offset | Size | Description |
| :--- | :--- | :--- |
| 0x00 | 1 | **X Offset** (Signed Byte) |
| 0x01 | 1 | **Component Index** (Index of the sprite to use) |
| 0x02 | 1 | **Y Offset** (Signed Byte) |
| 0x03 | 1 | **Flags** (Flipping, etc.) |
| 0x04 | 1 | **Palette / Attributes** |

## Code Verification
-   **`PrepareSprite` (`FUN_00407713`)**: This function prepares a *single* sprite component for drawing. It reads the fixed header (X, Y, W, H) and the Data Offset. Crucially, it accepts an input argument (in `AX` register) which it combines with `Unk1` to set the draw flags/palette. Since `Unk1` is usually 0, the palette information comes entirely from the caller.
-   **`FUN_00407b6d`**: This function iterates over a list of component objects. It calls `PrepareSprite` for each component, passing the flags/palette from the component object.
-   **Conclusion**: The component objects must be initialized from the "Remaining Bytes" in the sprite header, confirming that the palette information is stored there.

## Palette Byte
The 5th byte of the component record contains the palette index.
-   `0x02` -> Palette 2
-   `0xF2` -> Palette 2 (with flags)
-   `0xE0` -> Palette 0 (with flags)

The lower nibble (or bits) likely represents the palette index (0-15), while the upper bits are flags.
