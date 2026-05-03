# Veil Of Darkness Analysis

## Initial Findings

### File Format Analysis (User Provided)
The user has partially reverse-engineered a graphics file format.

**Header Structure:**
- 4 bytes: Size of data
- 2 bytes: Unknown
- 2 bytes: Count
- 2 bytes: Unknown
- `Count` * 2 bytes: Offsets to `LineInfo` objects
- `Count` * `LineInfo` objects

**LineInfo Object Structure (6 bytes):**
- 2 bytes (ushort): Count of pixels
- 2 bytes (ushort): Unknown (possibly transparent pixels following, but user doubts this)
- 2 bytes (ushort): Offset from which to take pixel data

**Pixel Data:**
- Follows the LineInfo objects.

**Example LineInfo Data:**
```
01 00 93 00 C6 00
02 00 94 00 C7 00
...
```

## Reverse Engineering Log

### Goal
Locate asset reading and decoding/decompressing logic.

### Findings

#### File I/O Functions
Located standard DOS interrupt wrappers:
- `dos_open_read` (`17c2:488b`): Wraps `INT 21h, AH=3Dh`.
- `dos_read` (`17c2:48d0`): Wraps `INT 21h, AH=3Fh`.
- `dos_close` (`17c2:48c4`): Wraps `INT 21h, AH=3Eh`.
- `dos_seek` (`17c2:4938`): Wraps `INT 21h, AH=42h`.

#### Resource System
The game uses a resource file (likely `RESOURCE.DAT` or similar, string "RESOURCE." found).

- **InitResourceSystem** (`17c2:122f`):
    - Opens the resource file.
    - Reads the first 4 bytes (likely number of resources or index size).
    - Allocates memory for the resource table (`g_ResourceTable`).
    - Initializes the table.

- **LoadResource** (`17c2:13e5`):
    - Takes a buffer pointer.
    - Seeks to the resource offset using `SeekToResource` (`17c2:4954`).
    - Reads 5 bytes header.
    - Checks for signature `0x4845` ('EH').
    - Allocates memory for the resource data.
    - Reads the resource data into the allocated memory.
    - **Note:** The data in memory starts *after* the 5-byte header.

- **GetResource** (`17c2:1471`):
    - Main dispatcher for using resources.
    - Checks if resource is loaded. If not, calls `LoadResource`.
    - Dispatches based on **Resource Type** (Byte 2 of the header).
    - **Type 1**: Compressed Image (LZ77 variant?). Handled by internal routine (Case 1).
    - **Type 2**: Calls `INT 60h`. This interrupt is likely a custom handler for code overlays or a specific resource type.
    - **Type 4**: Calls `DrawResource_Type4` (`17c2:4dcf`). This appears to be a graphics format.

- **LockResource** (`17c2:1965`):
    - Sets bit 0x10 in the resource table entry.
    - Used to mark resources as persistent/locked.

#### Game Logic (See `GameLogic.md` for details)
- **SwitchGameMode** (`1053:1c52`): Handles transitions between Gameplay, Menu, and Intro modes.
- **InitSystemWithResourceFile** (`17c2:1a5c`): Initializes system and loads resource file.
- **InitGameplayScreen** (`1053:1894`): Sets up the main game view.

#### Graphics Decoding (Type 4)
- **DrawResource_Type4** (`17c2:4dcf`):
    - Calculates VRAM offsets.
    - Calls `DrawResource_Type4_Sub` (`17c2:4f68`) or `DrawResource_Type4_Sub2` (`17c2:4e74`).

- **DrawResource_Type4_Sub** (`17c2:4f68`):
    - Appears to be a highly optimized, self-modifying drawing routine.
    - Reads parameters from the resource data:
        - Offset 5 (Byte 10 of file): Loop count?
        - Offset 2 (Byte 7 of file): Unknown.
        - Offset 7 (Byte 12 of file): Unknown.
    - Writes to VGA ports (`0x3c4`).
    - Copies data from resource to VRAM/Buffer.

#### Type 1 Handling (New Finding)
- **GetResource** (`17c2:1471`) Dispatcher:
    - **Type 1**: Compressed Image (LZ77 variant?). Handled by internal routine (Case 1).
    - **Hex Dump Analysis**: Data appears compressed (high entropy, e.g., `FC 0F EF FB...`). Likely LZ77 or similar bitwise compression, possibly related to Anvil of Dawn's TSW1 format.
    - **Type 2**: Calls `INT 60h`. This interrupt is likely a custom handler for code overlays or a specific resource type.

#### Type 4 Handling (Conflict)
- **Code Analysis**: `GetResource` Case 4 calls `DrawResource_Type4`, which writes to VGA ports (`0x3c4`), indicating a graphics format.
- **File Analysis (User Provided)**: A file with Type 4 header (`EH 04 ...`) contains "Creative Voice File" (VOC format).
- **Conclusion**: Type 4 ID is overloaded or context-dependent.
    - **Graphics Type 4**: Handled by `GetResource` -> `DrawResource_Type4`.
    - **Audio Type 4**: Likely loaded by a different mechanism (e.g., direct `LoadResource` call) to avoid triggering the graphics drawing logic in `GetResource`.

- **DrawResource_Type4_Sub** (`17c2:4f68`):
    - Appears to be a highly optimized, self-modifying drawing routine.
    - Reads parameters from the resource data:
        - Offset 5 (Byte 10 of file): Loop count?
        - Offset 2 (Byte 7 of file): Unknown.
        - Offset 7 (Byte 12 of file): Unknown.
    - Writes to VGA ports (`0x3c4`).
    - Copies data from resource to VRAM/Buffer.

### Next Steps
- Investigate `INT 60h`. Find where it is hooked. It might be in a loaded overlay or set dynamically.
- Analyze the data structures passed to `INT 60h`.
- Verify if the "LineInfo" format matches the data pointed to by Type 2 resources.
