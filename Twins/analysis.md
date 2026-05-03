# Twins Game Analysis

## Initial Analysis of Game_Main_Logic (FUN_00019a23)

This function appears to be the main game loop or high-level state machine. It initializes the system, checks for game files, and then enters a loop where it loads different assets based on the game state.

## Findings

### Functions

| Original Name | New Name | Description |
|---|---|---|
| FUN_00019a23 | Game_Main_Logic | Main game loop/state machine. |
| FUN_0001de00 | Load_VIS_Asset | Loads .VIS assets (animations/cutscenes). |
| FUN_0001984e | Play_Title_Sequence | Plays the title sequence (previously Load_DMD_Asset). |
| FUN_000187b7 | Load_DAC_Assets | Loads and parses .DAC files (TND.DAC, TNK.DAC, etc.) for the title sequence. |
| FUN_00018031 | Load_DMD_Wrapper | Wrapper for loading DMD files. |
| FUN_000142fd | Load_DMD_File | Loads and processes .DMD files (Digital Music Data). |
| FUN_00014875 | Init_DMD_Player | Initializes the music player with loaded DMD data. |
| FUN_0001cb53 | Draw_Sprite_Planar | Draws a sprite to VGA planar memory (Mode X). |
| FUN_00018339 | Load_DWV_Asset | Loads .DWV assets (likely sound effects). |
| FUN_0001c3c8 | Load_DFX_Asset | Loads .DFX assets. |
| FUN_00017213 | Load_Palette | Loads .PAL palette files. |
| FUN_0001b947 | Load_ETX_Asset | Loads .ETX assets. |
| FUN_00012223 | Exit_Game | Exits the game. |
| FUN_0001f199 | Print_String | Prints a string to the console. |
| FUN_0001f03b | Exit_Process | Terminates the process. |
| FUN_00013110 | Copy_Data | Copies memory (MemCpy/VRAM Copy). |
| FUN_00010158 | Load_Compressed_Resource | Loads and decompresses a resource container (used for Palettes). |
| FUN_00013429 | Decompress_Generic | The decompression algorithm used by Load_Compressed_Resource. |
| FUN_00014264 | Load_Decompression_Tables | Allocates memory and loads the decompression tables from a file. |
| FUN_0001eab9 | Allocate_Memory | Allocates memory (Malloc). |
| FUN_0001ea79 | File_Open | Opens a file (DOS Int 21h, AH=3Dh). |
| FUN_0001ea88 | File_Read | Reads from a file (DOS Int 21h, AH=3Fh). |
| FUN_0001ea5a | File_Close | Closes a file (DOS Int 21h, AH=3Eh). |
| FUN_000132f7 | Decompress_Update_State | Updates the decompression state/model. |
| FUN_0001339d | Decompress_Reset_Table | Resets part of the decompression table/buffer. |
| FUN_0001318d | Stream_Read_Word | Reads a 2-byte word from the current stream buffer. |
| FUN_0001315d | Stream_Read_Byte | Reads a 1-byte byte from the current stream buffer. |
| FUN_00013120 | Stream_Read_Buffer | Reads a buffer of bytes from the current stream buffer. |
| FUN_000122e2 | Allocate_Memory_Safe | Allocates memory and exits on failure. |

### Variables

| Original Name | New Name | Description |
|---|---|---|
| DAT_0003b053 | Game_Mode_Flag | Controls the mode (0=Error, 1=Intro, 2=Game). |
| DAT_0003b052 | Game_State | Current state within the game loop. |
| DAT_0003b051 | Level_Index | Index used for loading level-specific DFX files. |
| DAT_00031028 | Resource_Count | Number of entries in a compressed resource file. |
| DAT_0003363c | Decompression_Tables | Large table used by Decompress_Generic. |
| DAT_000311e0 | Decompression_Work_Buffer | Work buffer used during decompression. |
| DAT_000311fc | Decompression_Table_Filename | Filename of the file containing decompression tables. |
| DAT_000311f4 | File_Handle | Handle for the open file. |
| DAT_000311f8 | Bytes_Read_Var | Variable to store the number of bytes read. |
| DAT_00038aa5 | DAC_Section1_Buffer | Buffer for the first section of DAC data. |
| DAT_00038a7d | DAC_Section1_Data | Processed data from the first section of DAC files. |
| DAT_00031c6c | DAC_Frame_Count | Counter for the number of DAC frames loaded. |
| DAT_0003855c | DAC_Frames | Array of DAC frame structures (X, Y, W, H, Ptr). |

## Evidence for Decompression Table
The variable `Decompression_Tables` (DAT_0003363c) is identified as a decompression table (likely for an entropy coder or dictionary-based scheme) rather than a color lookup table for the following reasons:

1.  **Usage in `Decompress_Generic`:** The table is accessed using complex indexing logic involving bitwise operations and state updates, characteristic of decompression algorithms (like arithmetic coding or adaptive models).
2.  **Size:** The table size is `0x676c` (26,476 bytes). A standard VGA fade table (256 colors * 64 levels) would be 16,384 bytes. The specific size suggests a structured data model.
3.  **Context:** It is loaded and used specifically within the resource loading pipeline that handles "Compressed Size" and "Decompressed Size" fields.

## Analysis of Load_VIS_Asset

This function takes a resource list, a filename, and a parameter. It seems to iterate through the resource list and process items.
It calls `Decode_VIS_Data` which appears to be the main decompression loop.

### VIS Decoding Logic

`Decode_VIS_Data` iterates `VIS_Frame_Count` times. It reads data from `VIS_Source_Ptr` and processes it using a switch statement on a byte value (Opcode).

**VIS File Structure:**

| Offset | Size | Description |
|---|---|---|
| 0x0000 | 16 bytes | **Global Header** (First 2 bytes = Frame Count) |
| 0x0010 | 768 bytes | **Palette** (256 RGB entries, 3 bytes each) |
| 0x0310 | 0 bytes | **No Padding** (Data starts immediately) |
| 0x0310 | Variable | **Frame Data** (Sequence of frames) |

**Frame Header (5 bytes):**
*   **Offset 0 (2 bytes):** Chunk Size (Total size of frame including this header).
*   **Offset 2 (1 byte):** Delay (Frames to wait).
*   **Offset 3 (1 byte):** Unused/Skipped.
*   **Offset 4 (1 byte):** Opcode.

| Opcode | Handler | Description |
|---|---|---|
| 0x0C | VIS_Opcode_0C | **RLE/Command Stream**. Used for compressed data with transparency. Likely planar (adds 80 to offset). |
| 0x0D | VIS_Opcode_0D | Variant of 0x0C. |
| 0x0F | VIS_Opcode_0F | Variant of 0x0C. |
| 0x10 | VIS_Opcode_10 | **Raw Block Copy**. Copies `ChunkSize - 5` bytes directly to VRAM. |

This suggests a bytecode-based animation or image format. `VIS_Opcode_10` appears to handle uncompressed blocks, copying them directly to video memory. The other opcodes handle compression (RLE) and likely operate in a planar mode (stride of 80 bytes).

## Analysis of Palette Loading (Load_Compressed_Resource)

The palette files (and likely others) use a container format handled by `Load_Compressed_Resource`.

### Container Format
1.  **Header (4 bytes):** The number of entries is derived from the first 4 bytes. Specifically, `Count = First_DWORD >> 24`.
2.  **Index Table (Count * 8 bytes):**
    *   4 bytes: Compressed Size
    *   4 bytes: Decompressed Size
3.  **Data Blob:** The compressed data for each entry follows the index table.

### Decompression
The function `Decompress_Generic` is called to decompress each entry. It appears to be a complex dictionary-based compression (possibly LZW or Huffman) utilizing a large static table (`Decompression_Tables`).

## Decompression Implementation Details

The decompression algorithm (`Decompress_Generic`) relies on an external file "UNI" which contains the `Decompression_Tables`.

### Key Functions
*   `Load_Decompression_Tables`: Loads "UNI" into memory.
*   `Decompress_Generic`: The main decompression loop.
*   `Decompress_Update_State`: Updates the model/state.
*   `Decompress_Reset_Table`: Resets the work buffer.

### Work Buffer Initialization
The work buffer (`0x12e38` bytes) is initialized by copying data from the `Decompression_Tables` and setting up specific structures.

## Analysis of DMD Assets (Music/Audio)

The `.DMD` files appear to contain music or audio sequence data. The loading process involves:

1.  **Load_DMD_Wrapper (FUN_00018031):**
    *   Opens "twin.flg" (configuration/flags).
    *   Calls `Load_DMD_File` to load the actual .DMD file.
    *   Calls `Init_DMD_Player` to initialize the playback system.

2.  **Load_DMD_File (FUN_000142fd):**
    *   Reads the file into memory.
    *   Processes the data in 2048-byte chunks (0x800 bytes).
    *   Performs bitwise manipulation on 4-byte groups, suggesting a custom format or encryption/obfuscation.
    *   Returns a pointer to the processed data structure.

3.  **Init_DMD_Player (FUN_00014875):**
    *   Takes the loaded data pointer.
    *   Calculates timing tables based on a large constant (`0x369de40000`).
    *   Sets up an interrupt handler or callback (`LAB_00014ad8`), likely for background music playback.

## Title Sequence Logic (Play_Title_Sequence / FUN_0001984e)

The function previously identified as `Load_DMD_Asset` is actually `Play_Title_Sequence`. It orchestrates the intro:

1.  Loads "TITLE.PAL".
2.  Calls `Load_DMD_Wrapper` (via `FUN_00018031`) to load "TITLE.DMD" (music).
3.  Draws the initial title screen using `Draw_Sprite_Planar` (FUN_0001cb53).
4.  Executes a sequence of animations:
    *   `Title_Animation_Phase1` (FUN_00018c5b)
    *   `Title_Animation_Phase2` (FUN_000192b5)
    *   `Title_Animation_Phase3` (FUN_000195f5)

These animation phases loop and update the screen using `Draw_Sprite_Planar`, likely synchronized with the music.
