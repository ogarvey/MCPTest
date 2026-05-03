# Comix Zone Sprite Header Analysis

## Hypothesis: Composite Sprite Structure

Based on the provided log, the "Remaining Bytes" in the sprite header appear to be a list of **5-byte records** defining the components (sub-sprites) that make up the final sprite.

### Header Structure
The header seems to consist of:
1.  **Fixed Part (16 bytes)**:
    -   `Offset` (4 bytes)
    -   `Pos` (4 bytes: X, Y shorts?)
    -   `Size` (4 bytes: W, H shorts?)
    -   `SpriteOffset` (4 bytes)
    -   `Unk1` (2 bytes)
    -   `Unk2` (2 bytes): **Total Header Size** (Fixed + Variable Data).

2.  **Variable Part (Unk2 - 16 bytes)**:
    -   A sequence of 5-byte records.
    -   (Optional padding to align the next sprite).

### Component Record Format (5 Bytes)
Hypothesized format: `[X Offset] [Index] [Y Offset] [Flags] [Palette/Attribute]`

-   **Byte 0: X Offset (Signed)**
    -   Matches the `Pos.X` in the log for single-component sprites.
-   **Byte 1: Component Index**
    -   Likely an ID for the body part or sub-sprite.
-   **Byte 2: Y Offset (Signed)**
    -   Matches the `Pos.Y` in the log for single-component sprites.
-   **Byte 3: Flags**
    -   Unknown flags (e.g., flipping, blending).
-   **Byte 4: Palette / Attribute**
    -   **This is the likely link to the palette.**
    -   Low bits might be the Palette ID.
    -   High bits might be priority or other flags.

### Analysis of Samples

#### Sprite 0
-   **Header Size (`Unk2`)**: 21 bytes.
-   **Variable Data**: 5 bytes (`21 - 16`).
-   **Log Data**: `E4 17 C9 00 E0 00 00` (7 bytes, includes 2 bytes padding).
-   **Record 1**: `E4 17 C9 00 E0`
    -   **X**: `E4` (-28). Matches Log `Pos.X`.
    -   **Index**: `17` (23).
    -   **Y**: `C9` (-55). Matches Log `Pos.Y`.
    -   **Flags**: `00`.
    -   **Palette**: `E0` (Palette 0? With flags `1110...`).

#### Sprite 1
-   **Header Size (`Unk2`)**: 21 bytes.
-   **Variable Data**: 5 bytes.
-   **Log Data**: `F5 05 BF CC E0 00 00` (7 bytes, includes 2 bytes padding).
-   **Record 1**: `F5 05 BF CC E0`
    -   **X**: `F5` (-11). Matches Log `Pos.X`.
    -   **Index**: `05` (5).
    -   **Y**: `BF` (-65). Matches Log `Pos.Y`.
    -   **Flags**: `CC`.
    -   **Palette**: `E0` (Palette 0?).

#### Sprite 51
-   **Header Size (`Unk2`)**: 31 bytes.
-   **Variable Data**: 15 bytes (`31 - 16`).
-   **Log Data**: `EF 09 C2 00 02 EF 0A C2 E0 F2 F8 01 E0 01 F1` (15 bytes).
-   **Record 1**: `EF 09 C2 00 02`
    -   X: `EF` (-17).
    -   Index: `09`.
    -   Y: `C2` (-62).
    -   Palette: `02` (**Palette 2**).
-   **Record 2**: `EF 0A C2 E0 F2`
    -   X: `EF` (-17).
    -   Index: `0A`.
    -   Y: `C2` (-62).
    -   Palette: `F2` (Palette 2? `1111 0010`).
-   **Record 3**: `F8 01 E0 01 F1`
    -   X: `F8` (-8).
    -   Index: `01`.
    -   Y: `E0` (-32).
    -   Palette: `F1` (Palette 1? `1111 0001`).

#### Sprite 52
-   **Header Size (`Unk2`)**: 36 bytes.
-   **Variable Data**: 20 bytes (`36 - 16`).
-   **Log Data**: `EB 32 BB 00 03 08 33 BB D3 F9 EB 08 C8 DF F2 F8 01 DF 01 F1` (20 bytes).
-   **Record 1**: `EB 32 BB 00 03`
    -   Palette: `03` (**Palette 3**).
-   **Record 2**: `08 33 BB D3 F9`
    -   Palette: `F9` (Palette 9? `1111 1001`).
-   **Record 3**: `EB 08 C8 DF F2`
    -   Palette: `F2` (Palette 2?).
-   **Record 4**: `F8 01 DF 01 F1`
    -   Palette: `F1` (Palette 1?).

### Conclusion
The **5th byte** of each component record appears to contain the **Palette Index** (likely in the lower nibble or bits), possibly combined with flags in the higher bits.
