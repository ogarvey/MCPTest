# Angelique Analysis

## Goal
Find the graphics and palette reading and display logic for `.ANG` files.
Specific focus: How are palettes located/populated for files other than `DAISHI.ANG`, `ROOMS.ANG`, and `STILLS.ANG`?

## Functions of Interest
- `FUN_0040aae1`
- `FUN_0040ef79`
- `FUN_0040881e`

## Findings

### `FUN_0040aae1` -> **`LoadAngFileAndPalette`**
- **Purpose**: Loads palette data from specific `.ANG` files (`STILL256.ANG`, `MAJINAI.ANG`, `LOGO.ANG`) based on an index (`param_1`).
- **Logic**:
  - Checks `param_1`. If it's 15 (0xF), it loads `LOGO.ANG` and sets a specific palette range.
  - Otherwise, it loads `STILL256.ANG`.
  - It seeks to an offset calculated from `param_1`: `param_1 * 0x4b308`.
  - It reads start/end indices.
  - It reads 768 bytes (256 colors * 3) of palette data.
  - Calls `SetPalette`.
  - Also loads from `MAJINAI.ANG` if `param_1` is not 15. Seek offset `param_2 * 0x2bc00` or `0x3ee400`.
- **Key Takeaway**: `STILL256.ANG` and `MAJINAI.ANG` seem to act as palette banks or containers for other assets, indexed by the parameters passed to this function.

### `FUN_0040ef79` -> **`LoadNameAndOptionalRoomPalette`**
- **Purpose**: Loads graphics from `NAME.ANG` and optionally a palette from `ROOM.ANG`.
- **Logic**:
  - If `param_2` is non-zero:
    - Opens `ROOM.ANG`.
    - Seeks to `0x9f1800` (which is `0x9f180 * 16`). This suggests `param_2` might select a specific palette index, or it always uses a fallback palette at index 16?
    - Reads 128 colors (384 bytes).
    - Sets palette (start index 1).
  - Opens `NAME.ANG`.
  - Reads data from various offsets (`0x24000`, `0x48000`, etc.).
  - Process graphics using `ProcessGraphicsData`.
- **Key Takeaway**: `ROOM.ANG` contains a palette at offset `0x9f1800` used when loading names. `NAME.ANG` contains graphics but relies on external palettes (from `ROOM.ANG` or potentially pre-loaded ones).

### `FUN_0040881e` -> **`LoadRoomBackground`**
- **Purpose**: Loads a room background image and its palette from `ROOM.ANG`.
- **Details**:
  - Opens `ROOM.ANG`.
  - Seeks to `param_1 * 0x9f180`.
  - Reads 384 bytes (128 colors) palette.
  - SetPalette (index 1, count 127).
  - Loads Image 1 (640 width, line by line).
  - Loads Image 2 (1024 width, line by line).
- **File Structure (`ROOM.ANG` entry)**:
  - Size: `0x9f180` (651648 bytes)
  - Offset 0: Palette (384 bytes)
  - Offset 384: Image 1 (640x480? size matches)
  - Offset 307584: Image 2 (1024x336? size matches)

## Palette Logic Summary
Palettes are primarily found in:
1.  **`ROOM.ANG`**: Each room entry has its own 128-color palette at the beginning. This palette is also used (optionally) when loading `NAME.ANG`.
2.  **`STILL256.ANG`**: Contains 256-color palettes at the beginning of each entry.
3.  **`LOGO.ANG`**: Has a palette.
4.  **`MAJINAI.ANG`**: Loaded alongside `STILL256`.

Files like `NAME.ANG` do not seem to have their own palettes; they rely on the currently loaded palette (e.g., from `ROOM.ANG`). The game seems to use a "current context" palette model, where the main background (Room, Still) sets the palette, and overlay graphics (Names, Faces) use the existing palette indices.

## Global Asset Loading (`LoadGlobalAssets` - `FUN_0040bdf4`)
This function loads several global assets into memory buffers:
- `KAO.ANG`: Reads 0x3c000 bytes (245,760 bytes).
- `KAO2.ANG`: Opens but doesn't appear to read/store (Check this?).
- `CSL.ANG`: Reads 0x2800 bytes (10,240 bytes).
- `CHIP.ANG`: Reads 0xf600 bytes (62,976 bytes).
- `MAP.ANG`: Reads 0x4400 bytes (17,408 bytes).
- `MSG.ANG`: Opens checking existence.

## Unknowns
- `ANGEKAO.PAL`: String exists (`s_A:ANGEKAO.PAL_004403c0`) but no direct code reference found yet. DOES NOT EXIST IN THE FILE SYSTEM. It _MIGHT_ be a palette in the binary, potentially for face graphics (KAO = Face). This would be loaded directly into the upper palette range (128-255) when needed.

## Conclusion on Palette Location
Files other than `DAISHI.ANG`, `ROOMS.ANG`, and `STILLS.ANG` generally **do not have internal palettes**.
- **`NAME.ANG`**: Uses the palette from the currently loaded `ROOM.ANG` (specifically the one at offset `0x9f1800` when `param_2` is set) or relies on the existing system palette.
- **`KAO.ANG` / `KAO2.ANG`**: Loaded as raw bitmaps (likely indices). 
- **`LOGO.ANG`**: Has an internal palette.
- **Overlay/UI Elements (`MSG`, `CSL`, `MAP`, `CHIP`)**: Likely use the `ROOM` palette or a global UI palette.

The game architecture appears to be:
1.  Load "Scene" (Room/Still/Daishi) -> Sets the Base Palette.
2.  Load "Actors/Overlays" (Name, Kao, Msg) -> Draws using indices that reference the Base Palette (or a specific reserved range in it).

## Function Renaming Summary
- `FUN_0040aae1` -> `LoadAngFileAndPalette`
- `FUN_0040ef79` -> `LoadNameAndOptionalRoomPalette`
- `FUN_0040881e` -> `LoadRoomBackground`
- `FUN_0040bdf4` -> `LoadGlobalAssets`
- `FUN_00408c96` -> `DrawRoomBackground`
- `FUN_00403a6d` -> `FileOpen`
- `FUN_00403b96` -> `FileSeek`
- `FUN_00403af7` -> `FileRead`
- `FUN_0040fb93` -> `SetPalette`
- `FUN_00403aed` -> `FileClose`

