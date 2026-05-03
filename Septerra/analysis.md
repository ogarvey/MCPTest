# Septerra Core - Asset Loading Analysis

## Overview
This document tracks the reverse engineering analysis of Septerra Core's asset loading system.

**Target Binary**: Unknown (Windows game)
**Analysis Date**: September 2, 2025
**Focus**: Asset reading logic for .DB files referenced in septerra.mft

## Key Functions Under Investigation

### FUN_00410150 → WinMain / GameMain
- **Initial Name**: FUN_00410150
- **Proposed Name**: `WinMain` or `GameMain`
- **Status**: Analyzed
- **Context**: Main game entry point function - initializes game, loads septerra.mft, handles main game loop
- **Analysis**: Complete - This is the main Windows application entry point

**Function Signature**: `undefined4 __stdcall FUN_00410150(HINSTANCE hInstance, undefined4 reserved, char *cmdLine, int showCmd)`

**Key Functionality**:
1. **Window Creation**: Creates the main game window with "Septerra Core" title
2. **Command Line Processing**: Parses various command line switches (-A, -C, -D, -E, -F, etc.)
3. **Asset System Initialization**: Loads `septerra.mft` file via `FUN_00443930`
4. **Main Game Loop**: Handles Windows messages and game state updates
5. **Graphics Initialization**: Sets up graphics modes and display
6. **Error Handling**: Includes exception handling and error reporting

**Variables to Rename**:
- `param_1` → `hInstance` (Windows instance handle)
- `param_2` → `reserved` (typically unused in WinMain)
- `param_3` → `cmdLine` (command line string)
- `param_4` → `showCmd` (window show command)
- `local_158[4], local_154[4], local_150[4], local_14c[4]` → Contains "Septerra Core" string
- `local_144` → `wndClass` (WNDCLASSA structure)
- `local_27c` → `iniPath` (path to septerra.ini)
- `local_11c` → `mftPath` (path to septerra.mft)

### LoadMasterFileTable (FUN_00443930) - DETAILED ANALYSIS
- **Initial Name**: FUN_00443930
- **New Name**: `LoadMasterFileTable`
- **Status**: Fully analyzed and renamed
- **Context**: Core asset system initialization - loads and parses septerra.mft

**Function Signature**: `void __cdecl LoadMasterFileTable(char *mftPath, char *assetBuffer, undefined4 errorCallback)`

**Detailed Functionality**:
1. **Buffer Management**: Copies asset buffer name to global storage at `DAT_004d1a40`
2. **Database Array Init**: Initializes asset database array (0x43bc = 17,340 entries)
3. **Path Processing**: Copies MFT path and strips to directory (removes filename)
4. **File Operations**:
   - Opens septerra.mft file via `FileOpen` (read mode)
   - Gets file size via `FileGetSize`
   - Allocates memory buffer via `MemoryAlloc`
   - Reads entire file into memory via `FileRead`
5. **Text Processing**: Converts newlines to null terminators for line-by-line parsing
6. **Database Entry Processing**:
   - Parses each line from MFT file
   - Calls `ParseDatabaseEntry` to determine entry type
   - Checks for `[LOCAL]` vs `[OPTION]` entries
   - Opens corresponding .DB files for each entry
   - Stores file handles in asset database structure
7. **Thread Safety**: Initializes critical section for thread-safe asset access

**Global Variables Identified**:
- `DAT_004d1a40`: Asset buffer name storage
- `DAT_004d18f4`: Error callback function pointer
- `DAT_004c0a00`: Main asset database array (17,340 entries)
- `DAT_004d1b40`: Database counter/index
- `DAT_004c08e0`: MFT file path storage
- `DAT_00497744`: MFT file handle
- `DAT_004c09e0`: MFT file content buffer
- `DAT_00497748`: Database file handle
- `DAT_004d1b44`: Database file content buffer
- `DAT_004d18f8`: Database entry count (filesize >> 5)
- `DAT_004d18f0`: Max entries (0x100 = 256)
- `DAT_004d1a00`: Additional buffer allocation
- `DAT_004d1a38`: Current database entry index
- `lpCriticalSection_004d1a20`: Thread synchronization

**Supporting Functions Renamed**:
- `FUN_00468d79` → `FileOpen`: Opens files with mode flags
- `FUN_00468c8f` → `FileSeek`: File seek operations
- `FUN_00468b0c` → `FileGetSize`: Gets file size
- `FUN_004679fe` → `MemoryAlloc`: Allocates memory (with fallback to HeapAlloc)
- `FUN_004689f5` → `FileRead`: Reads data from files
- `FUN_00444540` → `ErrorHandler`: Handles errors via callback
- `FUN_004443f0` → `ParseDatabaseEntry`: Parses `[LOCAL]` and `[OPTION]` entries

## File Structure Analysis - DETAILED

### septerra.mft Format
The Master File Table appears to use a text-based format with the following structure:
- **Line-based**: Each database entry is on a separate line
- **Section Headers**: Uses `[LOCAL]` and `[OPTION]` markers to categorize entries
- **Entry Processing**: Each line after a section header represents a .DB file to load
- **Size Calculation**: Database entry count = filesize >> 5 (divide by 32)

### .DB Files Structure
- **Purpose**: Contains actual game assets (sprites, sounds, etc.)
- **Access**: Opened during MFT processing and handles stored in asset database
- **Threading**: Protected by critical sections for multi-threaded access
- **Capacity**: System supports up to 17,340 database entries

### septerra.ini
- **Purpose**: Configuration file with game settings
- **Processing**: Loaded separately from MFT system

### Asset Database Architecture
- **Array Size**: 17,340 entries (0x43bc)
- **Entry Size**: Each entry is 0x110 bytes (272 bytes)
- **Structure per entry**:
  - Offset 0x00: Pointer to database name string
  - Offset 0x04: File handle for .DB file
  - Offset 0x08: Additional file data
  - Offset 0x0C: Entry flags (bit 0 = LOCAL flag)
  - Offset 0x10: Database file path (252 bytes)

## Command Line Options
The game supports various command line switches:
- `-A`: Asset-related configuration
- `-C`: Configuration option
- `-D`: Debug mode disable
- `-E`: Engine configuration  
- `-F`: Fullscreen mode
- `-H`: Help/configuration
- `-L`: Language/locale settings
- `-M`: Music/sound configuration
- `-N`: Network configuration
- `-O`: Special options (OLDSAVE detected)
- `-P`: Performance settings
- `-Q`: Quality settings
- `-R`: Refresh rate settings
- `-S`: Sound configuration
- `-V`: Version/video settings
- `-W`: Window mode
- `-X`: Extended options
- `-Y`: Additional configuration

## Findings

### Successfully Renamed Functions
1. **FUN_00410150** → **WinMain**: Main application entry point
   - Handles Windows initialization, command line parsing, and main game loop
   - Loads asset system via septerra.mft file
   - Manages window creation and message processing

2. **FUN_00443930** → **LoadMasterFileTable**: Asset database loader  
   - Parses septerra.mft master file table
   - Initializes asset database system for .DB files
   - Sets up critical sections for thread-safe asset access

3. **Supporting File I/O Functions**:
   - **FUN_00468d79** → **FileOpen**: File opening with mode support
   - **FUN_00468c8f** → **FileSeek**: File seek operations
   - **FUN_00468b0c** → **FileGetSize**: File size determination
   - **FUN_004679fe** → **MemoryAlloc**: Memory allocation (heap + fallback)
   - **FUN_004689f5** → **FileRead**: File reading operations
   - **FUN_00444540** → **ErrorHandler**: Error handling via callbacks
   - **FUN_004443f0** → **ParseDatabaseEntry**: MFT entry parsing ([LOCAL]/[OPTION])

4. **Asset Reading Functions (.DB File Access)**:
   - **FUN_00443d50** → **AssetLookupByID**: Asset lookup by ID with binary search
   - **FUN_00444010** → **AssetSetPosition**: Set read position within asset
   - **FUN_004440a0** → **AssetReadData**: Main asset data reading with decompression
   - **FUN_004442f0** → **AssetGetSize**: Get asset size (decompressed)
   - **FUN_004448b0** → **DecompressLZO**: LZO decompression algorithm
   - **FUN_004445b0** → **AssetAllocateIndex**: Dynamic asset handle allocation
   - **FUN_00443cd0** → **AssetCleanup**: Cleanup asset system on shutdown

### Key Global Variables (Candidates for Renaming)
- `DAT_004b6f24`: Likely stores HINSTANCE (application instance)
- `DAT_004b62ac`: Main window handle (HWND)
- `DAT_004825f0`, `DAT_004825f4`: Screen resolution width/height
- `DAT_004b62e0`: Fullscreen mode flag
- `DAT_00490028`: Current game state/mode
- `DAT_004835e4`, `DAT_004836e4`: Asset system buffers

### Asset Loading Workflow Discovered
1. Game starts via **WinMain** (formerly FUN_00410150)
2. Command line arguments parsed for game configuration
3. **LoadMasterFileTable** (formerly FUN_00443930) called to load septerra.mft
4. Master file table parsed to identify .DB asset files
5. Asset system initialized with critical sections for thread safety
6. Main game loop begins with asset access available
7. **Asset Access During Gameplay**:
   - Game calls **AssetLookupByID** to get asset handle by ID
   - **AssetSetPosition** used to seek within asset data
   - **AssetReadData** reads asset data, decompressing with **DecompressLZO** if needed
   - **AssetGetSize** provides asset dimensions for memory allocation
   - All operations are thread-safe via critical sections

## TODO
- [x] Analyze FUN_00410150 functionality
- [x] Rename function based on behavior (→ WinMain)
- [x] Analyze variables and parameters
- [x] Trace function calls and usage patterns
- [x] Document asset loading workflow
- [x] Analyze LoadMasterFileTable in detail
- [x] Rename all supporting file I/O functions
- [x] Document MFT file format and database structure
- [ ] Analyze individual .DB file loading/reading functions
- [x] **MAJOR DISCOVERY: Complete .DB file reading system analyzed**
- [ ] Map out complete asset system architecture
- [ ] Identify sprite decompression routines
- [ ] Analyze asset retrieval/lookup functions
- [ ] Document threading and synchronization mechanisms

## BREAKTHROUGH: .DB File Reading System Discovered

### Asset Access Functions (FUN_00444xxx series)
I've discovered the complete asset reading system that operates on the .DB files loaded by the MFT system:

1. **AssetLookupByID** (FUN_00443d50): Core asset lookup function
   - Takes asset ID and returns asset handle index
   - Uses binary search on the database index
   - Dynamically expands asset handle pool if needed
   - Thread-safe with critical section protection

2. **AssetSetPosition** (FUN_00444010): Sets read position within an asset
   - Takes asset handle, position, and seek mode
   - Updates internal position tracking
   - Protected by critical section

3. **AssetReadData** (FUN_004440a0): Main asset data reading function
   - Reads data from assets with optional decompression
   - Handles both raw and compressed (LZO) asset data
   - Performs file seeking within .DB files
   - Thread-safe data access

4. **AssetGetSize** (FUN_004442f0): Returns asset size
   - Gets decompressed size of asset
   - Accounts for compression overhead

5. **DecompressLZO** (FUN_004448b0): LZO decompression algorithm
   - Complex sliding window decompression
   - Used for compressed assets within .DB files
   - High-performance decompression with buffering

### Asset Handle System
- **Dynamic Allocation**: Asset handles expand from 256 to accommodate more assets
- **Handle Structure**: Each handle (16 bytes) contains:
  - Database entry index
  - Current file position
  - Seek position and mode
  - Status flags

## Filetype Tag Validation and Loaders
The function `FUN_0040d2c0` is a simple magic check (compares the first dword in a loaded block and calls the error handler on mismatch). The actual filetype “dispatch” is implicit: different loader functions read an asset, validate a specific tag, then interpret the rest of the structure.

**Observed loaders and tags:**
- `FUN_0040caf0` → tag `0x3532564c` (“LV25”)
- `FUN_0040d830` → tag `0x34304d41` (“AM04”)
- `FUN_0040dad0` → tag `0x30305647` (“GV00”)
- `FUN_0040dbe0` → tag `0x30305854` (“TX00”)
- `FUN_0040de60` → tag `0x30304c49` (“IL00”)
- `FUN_0040f850` → tag `0x34314843` (“CH14”)

**Notes on CH14 and IL00 paths:**
- `FUN_0040f850` (CH14) loads the block, fixes internal offsets, optionally resolves a TX00 dependency via `FUN_0040dbe0`, and builds a pointer table for embedded records.
- `FUN_0040de60` (IL00) performs a straightforward load + offset fixup and returns the block.
- `FUN_0040f2a0` consumes these structures and chooses the CH14 path when the entry type byte is `0x03`.

## Graphics Extraction Pipeline (LV25 → CH14/AM04 → Sprite Instances)
This is the concrete asset path used to build graphics objects (sprites/background tiles) at runtime. These notes are intended as the basis for a C# extractor.

### LV25 Loader (Level/Scene Container)
**Function:** `FUN_0040caf0`
- Loads a resource by id, validates magic `LV25`, then patches many internal pointers by adding the base address.
- Pointer fixups include indices `4..0x31` (not contiguous, but many table pointers).
- Critical fields accessed later:
   - `DAT_0048ffcc + 0x40` → base pointer to a table of records (each 0x28 bytes).
   - `DAT_0048ffcc + 0x44` → count for the 0x28-byte records.
   - `DAT_0048ffcc + 0x20` / `+0x24` → table of 0x14-byte records (used by `FUN_0040ec00`).
   - `DAT_0048ffcc + 0x68` → count of region records (used by `FUN_0040ec90`).
   - `DAT_0048ffcc + 0x64` → base pointer to region records (each 0x40 bytes).
   - `DAT_0048ffcc + 0x28` → base pointer to polygon point list (used by `CreatePolygonRgn`).
   - `DAT_0048ffcc + 0x84` → base pointer to a table of 5-dword records (used by `FUN_0040eea0`).
   - `DAT_0048ffcc + 0x88` → count of the 5-dword records.
   - `DAT_0048ffcc + 0x8c` / `+0x90` → bounds info used by `FUN_0040eea0`.

### CH14 Loader (Character/Sprite Container)
**Function:** `FUN_0040f850`
- Loads resource by id, validates magic `CH14`, then fixes internal offsets (indices `2..0x1A`).
- If `header[1] != -1`, it loads TX00 via `FUN_0040dbe0` and stores pointer at `*(header[0] + 4)`.
- Builds a pointer table for embedded records:
   - `header[2]` = record list base
   - `header[3]` = record count
   - Each record is 0x48 bytes; a list of pointers is built at `*(header[0] + 8)`.
- Uses `header[0x1A]` and `header[0x1B]` to drive `FUN_004100f0` (VSSS-related).

### AM04 Loader (Sprite Sheet / Palette + Frame Data)
**Function:** `FUN_0040d830`
- Loads resource by id, validates magic `AM04`, then fixes offsets at indices `2,4,6,8,0xC,0xE,0x10`.
- If name starts with `I/S/E` then `header[1] = -1`. If `header[1] == 0` and `FUN_004134a0(id) == 1`, then `header[1] = 3`.
- Calls `FUN_0040dee0(header + 6, header[7])` to allocate per-entry blocks (`0x230` bytes each). This is where per-frame palette/metadata storage is initialized.
- These AM04 blocks are what `FUN_004048e0` ultimately uses for pixel decode + palette lookup.

### Sprite Instance Construction (CH14 entry → AM04 frame)
**Function:** `FUN_0040f2a0(int *entry, ... )`
- `entry` is a 0x28-byte record (from LV25 or CH14 table).
- `entry[7]` is the resource id used for the sprite source.
- `entry[1]` is a type byte:
   - `0x03` → CH14 path: loads CH14 via `FUN_0040f850`, then selects a frame set using
      `frameIndex = *(byte*)((int)entry + 5)` and `frameTable = *(int *)(ch14 + 8) + frameIndex * 0x120`.
   - `0x00 / other` → AM04 path: loads AM04 via `FUN_0040d830`.
   - `0x02` → TX00-only (returns no runtime sprite).
- This function builds a runtime sprite object and attaches AM04 frame data + palette.

### Region/Polygon Setup (Graphics-Related)
**Function:** `FUN_0040ec90`
- Iterates LV25 region records (`DAT_0048ffcc + 0x64`, count `+0x68`).
- If record has a TX00 id at `+0x28`, it resolves via `FUN_0040dbe0` and stores pointer.
- Creates `HRGN` polygons using points from `DAT_0048ffcc + 0x28` and record-local index/length.
- Classifies regions based on `record[0]` and flag bytes at `record[1]/[2]/[4]`.

### C# Extractor Planning Notes (Next Step)
1. Parse LV25 → rebase pointers → read the 0x28-byte entry list (`+0x40`/`+0x44`).
2. For each entry, read `entry[7]` (resource id) and `entry[1]` (type).
3. If type `0x03`: load CH14, resolve TX00 pointer (optional), select AM04 or embedded frame table.
4. If type other: load AM04 directly and decode frames/pixels using span tables + palette.
5. Use `FUN_004048e0` notes to reconstruct per-frame pixel output.

## septerra.idx Relationship (Initial Hypothesis)
- The `septerra.idx` file appears to provide per-asset metadata that pairs with the .DB files listed in `septerra.mft`.
- The IDX record’s `VolumeIndex` likely maps to the .DB’s position in the MFT list.

**IDX entry structure (32 bytes):**
```
ushort FileId;
ushort Unk1;
uint VolumeIndex;
uint Offset;
uint UncompressedLength;
byte IsCompressed;
byte Unk3;
ushort Unk4;
uint CompressedLength;
long FilenameHash;
```

## Quick Pass: Other Loaders/Tags (Graphics Signals)
**TX00 (`FUN_0040dbe0`)**
- Used by `FUN_0040ec90` (scene/region setup). It resolves a TX00 reference per record and builds Windows GDI polygon regions via `CreatePolygonRgn`, suggesting TX00 may back graphical/region data (UI/sprite masks, walk regions, or textured overlays).

**IL00 (`FUN_0040de60`)**
- Loaded during init in `FUN_0040fc40` (global setup). The structure is simple (offset fixups) and likely acts as an image-list or atlas descriptor used later by higher-level systems.

**CH14 (`FUN_0040f850`)**
- Heavily used by `FUN_0040f2a0` and global setup (`FUN_0040fc40`, `FUN_0043ffb0`). Builds pointer tables and references TX00; likely a core character/sprite/animation container.

**AM04 (`FUN_0040d830`)**
- High fan-out usage across gameplay/scene code. The loader applies many offset fixups and later code accesses per-entry structures; likely animation or actor metadata tied to CH14.

**LV25 (`FUN_0040caf0`)**
- Invoked from main startup and save/load flow (`FUN_00410150`, `FUN_00431860`). Looks like level/world state container, not primarily graphical but drives scene composition.

**GV00 (`FUN_0040dad0`)**
- Called during global refresh (`FUN_0043ffb0`). Appears to load a table and patch pointers; likely game-variable or global state data, not directly graphical.

## TX00 Loader: Data Processing Notes
**Loader:** `FUN_0040dbe0`
- Reads asset via `AssetLookupByID` + `AssetReadData`, validates magic `TX00`.
- Performs fixups over an array of records: for each record in `count = *(tx00 + 4)` (second dword), it adds the base pointer to the first field of each 0x0C-sized entry (advance by 3 dwords). This implies a table of entries with the first element being an offset-to-pointer.
- Caches results in a table at `DAT_004906ac` with entries `{ptr, assetId, refCount}` to avoid reloading.

**Cache helpers:**
- `FUN_0040de30(assetId)` → returns cached TX00 pointer (or 0).
- `FUN_0040dd60(assetId)` → release by asset id (calls `FUN_0040dda0`).
- `FUN_0040dda0(ptr)` → decrements refcount, frees TX00 block when count hits 0.

**Downstream usage (current visibility):**
- `FUN_0040ec90` resolves a TX00 reference stored at record offset `+0x28` (replaces id with pointer). It then creates polygon regions from a separate point list referenced via `DAT_0048ffcc + 0x28`, so TX00 likely feeds additional per-region graphics data.
- `FUN_0040f850` (CH14 loader) resolves an optional TX00 dependency and writes the pointer into the CH14 header at `*(header + 4)`.
- `FUN_0040f2a0` treats entry type `0x02` as TX00-only and loads it, returning no runtime object.
- `FUN_00431cf0` (actor/scene construction) has a switch case `type == 6` that pulls a TX00 pointer via `FUN_0040de30(piVar1[7])` and stores it in the actor entry (`piVar7[0x21]`). This indicates TX00-backed data is bound to certain actor types at creation time (likely graphic overlays, special effects, or interaction regions).

### TX00 record layout (current best fit)
Based on `FUN_00441390` (lookup), `FUN_0040dbe0` (fixups), and a hex dump from TEXT.DB, TX00 appears to be a simple string table used by UI/text code:
- Header:
   - `+0x00` = magic `TX00`
   - `+0x04` = `count`
   - `+0x08` = `stringDataSize` (likely total bytes in string data block)
   - Entries start at `+0x0C` (stride 0x0C)
- Entry layout (0x0C bytes each):
   - `+0x00` = string ID (lookup key)
   - `+0x04` = string offset (file-relative)
   - `+0x08` = string length (bytes)

**Hex confirmation (first TX00 in TEXT.DB):**
- `count = 0xD9` (217 entries)
- `stringDataSize = 0x07DE`
- First entry: `id=1, offset=0x0A38, len=5`
- Next entry: `id=2, offset=0x0A3D, len=6`
- Offsets are contiguous: `nextOffset - offset == len`
- `0x0A38 == 0x0C + count*0x0C`, so the string data block starts immediately after the entry table.
- Last entries: `id=0x045C, offset=0x1203, len=0x08` and `id=0x04B0, offset=0x120B, len=0x0B`.
- `0x120B + 0x0B = 0x1216`, which matches the reported TX00 size and confirms the block size math.

**TX00 usage evidence:**
- `FUN_00441390(key, tx00)` scans entries and returns the value for a matching key.
- UI selection code (`FUN_00409d10`, `FUN_004034f0`) assigns `DAT_00493660 = FUN_00441390(key, tx00Ptr)` and later the draw queue uses `DAT_00493660` as the current text/string pointer.
- `FUN_0040dbe0` adds the TX00 base to the first dword of each entry. With the hex dump, this likely means the **string IDs are stored as offsets (pointer-like)** and get rebased at load, while `FUN_00441390` compares that rebased value against a key (needs confirmation with another sample or a runtime watch).

**String encoding (not ASCII):**
- Text rendering (`FUN_004420c0`, `FUN_00442300`, `FUN_00442460`) treats the string data as **byte-coded glyph indices**:
   - `0x00` = terminator
   - `0xFE` = line break / wrap sentinel
   - `0xFF` = treated as glyph index `0` for width/advance (likely “space”)
- Each byte value indexes a glyph record in the font sheet (`DAT_0049004c` AM04), so the raw bytes won’t resemble ASCII.

## Palette format/data (what we can confirm)
- Palettes used by the sprite blitter (`FUN_004048e0`) are **not** sourced from TX00; they come from AM04 assets loaded via `FUN_0040d830` (e.g., `DAT_0049004c`, `DAT_00490054`, etc.).
- Palette layout in AM04-backed sheets appears to be blocks of size `0x230`, where the 16-bit palette table starts at `paletteBlock + 4` and entries are `uint16` colors (`index * 2`).
- `FUN_00406020` builds a 0x40000-byte RGB conversion table at `DAT_00490008`, used by palette/gradient logic (e.g., `FUN_004426a0`) for color mixing.

## Render/Blit Pipeline (Software)
**Backbuffer basics:**
- `DAT_0048fff4` appears to be the 16-bit (RGB555) framebuffer base.
- `DAT_004b5f44` is the pitch (width in pixels). Many draw routines assume a 0x280 x 0x180 surface (0x27f/0x17f bounds).

**Primitive draw helpers:**
- `FUN_004431c0` / `FUN_00443630` / `FUN_0043ebd0` draw circles/ellipses/rect outlines directly into the backbuffer (write 16-bit colors via `FUN_00406120` palette packing).

**Sprite blitter / image-to-pixels path:**
- `FUN_004048e0` is the core sprite blitter. It renders a sprite frame into the backbuffer with clipping and optional flips.
   - Inputs:
      - `param_1` = sprite/atlas descriptor (tables for frames, row spans, and pixel data).
      - `param_3` = frame record (offsets, width/height, anchor).
      - `param_4` = clip rect (left, top, right, bottom).
      - `param_5` = flags (bit0 = horizontal flip, bit1 = vertical flip).
   - Palette usage:
      - Base palette block at `*(int*)*param_1` plus `framePaletteIndex * 0x230`.
      - If `paletteBlock + 0x204` is non-zero, it redirects to an override palette.
      - Pixels are 8-bit indices converted to 16-bit colors via `palette + 4 + index*2`.
   - Compression/layout:
      - Each scanline references a list of spans from `param_1[3]` (pairs of `{offset, runLen}`), and pixel bytes from `param_1[4]`.
      - The run list plus pixel indices function like a simple RLE/packed span stream per row.
   - Output:
      - Writes 16-bit pixels directly into `DAT_0048fff4` using pitch `DAT_004b5f44`.

**Implication:**
- `FUN_004048e0` is the closest thing to “image decode.” To reconstruct images, parse the frame record + span table + pixel stream, then apply the palette to produce 16-bit pixels.

**Primary callers of `FUN_004048e0`:**
- `FUN_00404e70` (tile/block render pass)
   - Iterates a grid of “cells” from `DAT_0048ffcc` (per-cell list of frame records, each 0x14 bytes).
   - For each cell, computes a clip rect for the visible sub-rectangle and calls `FUN_004048e0` with frame flags from `*(frame + 0x12)` (flip bits).
   - When `DAT_00490028` is in a specific range, it switches to a single-sheet draw path using `DAT_00490050` instead of `DAT_0048ffcc`.
- `FUN_004053d0` (font/atlas bake into an offscreen surface)
   - Resets global origin to (0,0), initializes a surface from `DAT_0048ffe8`, and uses repeated `FUN_004048e0` calls to draw glyph frames into a packed atlas.
   - Updates `DAT_0048ffec` with per-glyph metadata (offsets and sizes), and accumulates total width in `DAT_004b58d8`.
- `FUN_00408790` (single sprite draw from a sheet)
   - Picks a frame index from `(sheet + 8)` via `(param_3 + param_2 * 0x13)`, applies optional palette override via `DAT_00483500`, computes clip bounds, and blits via `FUN_004048e0`.
- `FUN_00408530` (UI widget composed from sprites)
   - Uses `FUN_00408790` to draw base UI elements, then draws additional frames (e.g., fill/markers) depending on `param_1`/`param_2`.
