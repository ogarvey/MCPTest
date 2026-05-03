# Comix Zone Sprite Drawing Analysis

## Drawing Functions
The game uses different functions to draw sprite lines depending on flags (likely flipping and palette remapping).

1.  **`DrawSpriteLine_RLE` (`FUN_004045b9`)**:
    -   Standard drawing.
    -   Copies pixels directly: `*pbVar7 = *pbVar6;`
    -   No palette remapping.

2.  **`FUN_00404607`**:
    -   **Palette Remapping**.
    -   Uses a lookup table: `*puVar7 = *(undefined1 *)(unaff_EBX + (uint)*pbVar5);`
    -   `unaff_EBX` is the base address of the lookup table.
    -   `*pbVar5` is the pixel value from the sprite data.

3.  **`FUN_00404655`**:
    -   **Flipped Drawing** (Horizontal Flip).
    -   Writes backwards: `pbVar7 = pbVar7 + -1;`
    -   No palette remapping.

4.  **`FUN_004046a7`**:
    -   **Flipped + Palette Remapping**.
    -   Writes backwards.
    -   Uses lookup table: `*puVar7 = *(undefined1 *)(unaff_EBX + (uint)*pbVar5);`

## Palette Lookup Table (`unaff_EBX`)
In `FUN_00404607` and `FUN_004046a7`, `unaff_EBX` is used as the base for the palette lookup.
We need to find where `unaff_EBX` is set before calling these functions.

These functions are called from `DrawSprite_RLE` (`FUN_0040422c`).
In `DrawSprite_RLE`:
-   `unaff_EBP` is the structure set up by `PrepareSprite`.
-   `unaff_EBP[4]` contains the flags.
-   If `unaff_EBP[4] & 1` is set, it calls the remapping functions.

We need to find where `unaff_EBX` is set in `DrawSprite_RLE` or passed to it.
Looking at `DrawSprite_RLE` decompilation:
It doesn't seem to set `unaff_EBX` explicitly in the decompiled code.
However, `unaff_EBP` is a pointer to a structure.
`PrepareSprite` sets:
`*(int *)(unaff_EBP + 6) = *(int *)(psVar2 + 5) + g_SpritesBinData;`
This is offset 12 (bytes). This is the sprite data pointer.

Wait, `unaff_EBP` is `short*`.
`unaff_EBP[0]` -> X
`unaff_EBP[1]` -> Y
`unaff_EBP[2]` -> Width (or similar)
`unaff_EBP[3]` -> Height (or similar)
`unaff_EBP[4]` -> Flags
`unaff_EBP[5]` -> ?
`unaff_EBP[6]` -> Data Pointer (Low)
`unaff_EBP[7]` -> Data Pointer (High)

Where is the palette table pointer?
In `DrawSprite_RLE`, `unaff_EBX` is used.
If `unaff_EBX` is not set in `DrawSprite_RLE`, it must be preserved from the caller or set in `PrepareSprite`.
But `PrepareSprite` doesn't seem to set `EBX`.

Let's check `PrepareSprite` again.
`unaff_EBP` is passed in (in `EDI`? No, `unaff_EBP` is a register variable).
Actually, `PrepareSprite` uses `unaff_EBP` as a pointer to the "Draw Command" structure.
`DrawSprite_RLE` uses `unaff_EBP` as the same pointer.

If `unaff_EBX` is the palette table, where does it come from?
Maybe it's a global variable? Or passed in another register?

Let's look at `FUN_00407b6d` (the caller of `PrepareSprite`).
It doesn't seem to set `EBX` for the draw command.
However, `DrawSprite_RLE` is NOT called by `FUN_00407b6d`.
`FUN_00407b6d` only calls `PrepareSprite`.
`PrepareSprite` fills a structure.
The actual drawing must happen later, by iterating over these structures.

`FUN_004049bf` calls `PrepareSprite` then `FUN_00404a7c`.
`FUN_00404a7c` might be the one that triggers drawing?
Or `FUN_004076ad`?

Wait, `DrawSprite_RLE` (`FUN_0040422c`) is the function that does the drawing.
Who calls `DrawSprite_RLE`?
XRefs to `DrawSprite_RLE`:
-   `FUN_00404869`
-   `FUN_00404d1b`

Let's check `FUN_00404869`.
