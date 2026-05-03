# Voodoo Game Reverse Engineering Analysis

## Overview
Analysis of an old Windows game called "Voodoo" to identify asset decompression logic and understand the game's internal structure.

## Date Started
September 2, 2025

## Analysis Progress

### Functions Analyzed
- [x] FUN_0042f580 - **String comparison function** (renamed to `StringCompare`)
- [x] FUN_0042f9b6 - **File loader/parser** (renamed to `LoadBurpFile`) - Loads "Burp" format files
- [x] FUN_00434af0 - **File reading wrapper** (renamed to `FileReadWrapper`) - Handles actual file I/O operations
- [x] FUN_00434f8f - **Memory allocation handler** - Manages dynamic memory allocation
- [x] FUN_00407a10 - **Asset system initialization** - Loads multiple Burp files and initializes asset system
- [x] FUN_00406e20 - **Asset lookup function** - Searches asset table by name and ID
- [x] FUN_00407220 - **Asset reference counter** - Manages asset references and memory

### Key Findings

#### LoadBurpFile (FUN_0042f9b6) Analysis - Asset File Loader
**Function Purpose**: Loads and parses files with the "Burp" signature - appears to be the main asset file format.

**Function Signature**: `int * LoadBurpFile(void)`

**Key Characteristics**:
1. **File Signature Check**: Looks for the signature `0x70727542` ("Burp" in reverse ASCII)
2. **Memory Allocation**: Allocates a structure to hold file data
3. **Format Parsing**: Reads header information and sets up data structures
4. **Error Handling**: Returns NULL on failure
5. **Asset Structure Creation**: Creates data structures for asset management

**Usage**: Called multiple times during initialization to load different asset files:
- Called from FUN_00407a10 (asset system initialization)
- Multiple Burp files loaded: sprites, sounds, animations, etc.

#### Asset Lookup System (FUN_00406e20) - Asset Search Function
**Function Purpose**: Searches through loaded assets by name and ID.

**Key Characteristics**:
1. **Linear Search**: Searches through an asset table (max 0x50 = 80 entries)
2. **String Comparison**: Uses StringCompare function to match asset names
3. **ID Matching**: Also matches by asset ID parameter
4. **Return Values**: Returns true if asset found, false otherwise
5. **Asset Index**: Sets the found asset index for further operations

**Data Structure**: Each asset entry is 0x29 (41) bytes:
- Offset 0x1f: Asset ID (short)
- Offset 0x21: Asset data pointer (int)
- Offset 0x19: Asset name string

#### Asset Reference Management (FUN_00407220) - Memory Management
**Function Purpose**: Manages asset references and memory allocation.

**Key Characteristics**:
1. **Reference Counting**: Tracks how many times an asset is loaded
2. **Memory Management**: Allocates/deallocates memory for assets
3. **Usage Tracking**: Maintains global counters for memory usage
4. **Efficiency**: Reuses already loaded assets instead of reloading

**Global Variables**:
- `DAT_00455368`: Total memory used counter
- `DAT_00455364`: Available memory counter
- `DAT_004609e6`: Active asset counter

### Asset System Architecture Discovered

#### File Format: "Burp" Files
- **Signature**: 0x70727542 ("Burp" in reverse ASCII)
- **Purpose**: Container format for game assets (sprites, sounds, data)
- **Structure**: Header + asset data
- **Loading**: Multiple Burp files loaded during initialization

#### Asset Management System
1. **Initialization**: LoadBurpFile loads multiple asset files
2. **Lookup**: FUN_00406e20 searches for assets by name/ID
3. **Reference Counting**: FUN_00407220 manages memory and references
4. **Data Access**: Assets accessed through pointer structures

#### Asset Table Structure
- **Capacity**: Up to 80 assets (0x50)
- **Entry Size**: 41 bytes (0x29) per entry
- **Search Method**: Linear search with string comparison
- **Indexing**: Assets identified by name string and numeric ID

### Functions Analyzed
- [x] FUN_0042f580 - **Asset configuration parser** (renamed to `AssetConfigParser`)
- [x] FUN_0042f9b6 - **File loader/parser** (renamed to `LoadBurpFile`) - Loads "Burp" format files
- [x] FUN_00434af0 - **File reading wrapper** (renamed to `FileReadWrapper`) - Handles actual file I/O operations
- [x] FUN_00434f8f - **Memory allocation handler** - Manages dynamic memory allocation
- [x] FUN_00407a10 - **Asset system initialization** (renamed to `InitializeAssetSystem`) - Loads multiple Burp files and initializes asset system
- [x] FUN_00406e20 - **Asset lookup function** (renamed to `AssetLookupByName`) - Searches asset table by name and ID
- [x] FUN_00407220 - **Asset reference counter** (renamed to `AssetGetHandle`) - Manages asset references and memory
- [x] FUN_00426a50 - **SPRITE PROCESSOR** (renamed to `ProcessSpriteData`) - Sprite rendering system

### Key Findings

### 🔄 MAJOR CORRECTION: Understanding the Burp File Format

**CRITICAL INSIGHT**: Based on the detailed Burp format specification provided, I need to correct my analysis of the LoadBurpFile function and the overall asset system.

#### Corrected Burp File Format Understanding

**File Structure**:
1. **Magic Header**: "Burp" signature (0x70727542)
2. **Entry Count**: 4-byte little-endian uint (number of assets in file)
3. **Entry Table**: count × 20 bytes per entry with structure:
   ```
   struct BurpEntry {
       uint UncompressedLength;  // Size after decompression
       uint CompressedLength;    // Size of compressed data  
       uint NameOffset;          // Offset to name in name table
       uint DataOffset;          // Offset to compressed data
       uint Type;                // Asset type identifier
   }
   ```
4. **Asset Data**: Compressed asset data at various offsets
5. **Name Table**: At end of file, each entry starts with 0x49 + length byte + name

#### LoadBurpFile (FUN_0042f9b6) - CORRECTED Analysis
Looking at the code with this new understanding:

```c
// After checking "Burp" signature (0x70727542)
FUN_0042a20b();                    // Read entry count (local_30)
if (local_34 != 0x70727542) { ... }

uVar2 = FUN_0042a8d0();            // Allocate memory for structure
local_24 = (int *)uVar2;
if (local_24 != (int *)0x0) {
    *(short *)((int)local_24 + 10) = local_1c;
    *(short *)(local_24 + 2) = (short)extraout_EDX;
    local_24[1] = local_30;         // Store entry count
    *local_24 = 8;                  // Base size
    if ((extraout_EDX & 1) == 0) {
        local_24[8] = -1;
    } else {
        FUN_0042a20b();
        *local_24 = *local_24 + local_30 * 0x14;  // Add space for count * 20 bytes (0x14 = 20)
    }
}
```

**Key Insight**: The line `*local_24 = *local_24 + local_30 * 0x14` confirms the format - it's allocating space for `count * 20` bytes, exactly matching the entry table structure you described!

#### Asset Data Access Pattern - CORRECTED
Looking at functions like FUN_0041ffc0, I can see the actual asset access pattern:

1. **AssetGetHandle** gets an asset handle/index
2. **Asset Data Pointer**: `*(short **)((int)&DAT_0045fd35 + handle * 0x29)` gets the actual asset data
3. **Data Structure**: The first words appear to be width/height (DAT_00455ddc, DAT_00455dde)
4. **Data Pointer**: `_DAT_00498f3c = DAT_00498f40 + 2` points to actual pixel/sprite data

#### Missing Piece: Actual Decompression Function
**REALIZATION**: The ProcessSpriteData function I identified is NOT the decompression function - it's the sprite RENDERING function that works with already-decompressed data!

**What's Missing**: There must be a function that:
1. Takes a Burp entry index
2. Seeks to the DataOffset in the Burp file  
3. Reads CompressedLength bytes
4. Decompresses them to UncompressedLength bytes
5. Returns pointer to decompressed data

This decompression likely happens between AssetGetHandle and when the data is accessed by functions like FUN_0041ffc0.

#### Asset System Architecture - CORRECTED

**Complete Pipeline**:
1. **LoadBurpFile**: Loads Burp file, parses entry table with 20-byte entries
2. **AssetLookupByName**: Searches entry table, matches against name table (0x49 + length + name)
3. **AssetGetHandle**: Returns handle to asset entry
4. **[MISSING FUNCTION]**: Decompresses asset from CompressedLength → UncompressedLength 
5. **Asset Access Functions**: Access decompressed data (width/height/pixels)
6. **ProcessSpriteData**: Renders sprites using decompressed data

#### Updated Function Classifications

**CORRECTED UNDERSTANDING**:
- **LoadBurpFile**: ✅ CORRECT - Parses Burp format with 20-byte entry table
- **AssetLookupByName**: ✅ CORRECT - Searches entry table  
- **AssetGetHandle**: ✅ CORRECT - Returns asset handle
- **ProcessSpriteData**: ❌ INCORRECT - This is sprite RENDERING, not decompression
- **[UNKNOWN FUNCTION]**: ❓ MISSING - The actual asset decompression function

### Priority: Find the Asset Decompression Function
The missing piece is the function that takes a Burp entry and decompresses the asset data from CompressedLength to UncompressedLength. This function must:
- Access the DataOffset from the Burp entry
- Read CompressedLength bytes from the file
- Decompress using some algorithm (RLE, LZ, etc.)
- Return buffer with UncompressedLength bytes

#### Complete Asset System Architecture Discovered

### Asset Loading → Processing → Rendering Pipeline
1. **LoadBurpFile**: Loads "Burp" format asset files into memory
2. **AssetLookupByName**: Searches for specific assets by name/ID
3. **AssetGetHandle**: Manages asset references and memory allocation
4. **ProcessSpriteData**: ⭐ Decompresses and processes sprite data for rendering

#### AssetConfigParser (FUN_0042f580) - FINAL CORRECTED ANALYSIS
**FUNCTION RENAMED**: Based on deeper analysis of usage patterns, this function has been renamed from `StringCompare` to **`AssetConfigParser`**!

**Function Purpose**: Configuration parser and asset selection logic that determines which Burp files and assets to load based on game settings.

**Key Usage Patterns**:
1. **Character Type Selection** (in FUN_00407500):
   - Compares against "B" (boy), "G" (girl), "A" (all)
   - Determines character-specific asset loading
   
2. **Language/Locale Selection** (in FUN_00408440):
   - Compares configuration to determine language variant
   - Selects between `F_LANG_G.KID` vs `F_LANG_F.KID` (German vs French)
   - Chooses `DATA_G.KID` vs `DATA_F.KID` for language-specific assets
   
3. **Asset Path Configuration**:
   - Results determine which directory paths are used for assets
   - Affects which Burp files are loaded by the asset system
   - Controls language-specific sprite and data file selection

**Technical Details**:
- **Optimized Implementation**: Uses 4-byte DWORD comparisons for efficiency
- **Null Detection**: Uses bit patterns `0xfefefeff` and `0x80808080` for null byte detection
- **Return Values**: 0 (equal), 1 (first > second), -1 (first < second)

**Critical Role in Asset System**:
- **NOT** just a utility function - it's an **asset selection controller**
- **Determines which Burp files get loaded** during initialization
- **Controls language and character-specific content**
- **Essential for proper asset management**

**Potential Rename**: This function has been **renamed to `AssetConfigParser`** as it's actually an asset selection controller that determines which Burp files get loaded!

**Assessment**: This function is **directly involved in asset loading logic** by determining which asset files to load based on configuration settings!

#### Asset Lookup System (AssetLookupByName) - Asset Search Function
**Function Purpose**: Searches through loaded assets by name and ID.

**Key Characteristics**:
1. **Linear Search**: Searches through an asset table (max 0x50 = 80 entries)
2. **String Comparison**: Uses StringCompare function to match asset names
3. **ID Matching**: Also matches by asset ID parameter
4. **Return Values**: Returns true if asset found, false otherwise
5. **Asset Index**: Sets the found asset index for further operations

**Data Structure**: Each asset entry is 0x29 (41) bytes:
- Offset 0x1f: Asset ID (short)
- Offset 0x21: Asset data pointer (int)
- Offset 0x19: Asset name string

#### Asset Reference Management (AssetGetHandle) - Memory Management
**Function Purpose**: Manages asset references and memory allocation.

**Key Characteristics**:
1. **Reference Counting**: Tracks how many times an asset is loaded
2. **Memory Management**: Allocates/deallocates memory for assets
3. **Usage Tracking**: Maintains global counters for memory usage
4. **Efficiency**: Reuses already loaded assets instead of reloading

**Global Variables**:
- `DAT_00455368`: Total memory used counter
- `DAT_00455364`: Available memory counter
- `DAT_004609e6`: Active asset counter

### Asset System Architecture Discovered

#### File Format: "Burp" Files
- **Signature**: 0x70727542 ("Burp" in reverse ASCII)
- **Purpose**: Container format for game assets (sprites, sounds, data)
- **Structure**: Header + asset data + sprite frame data
- **Loading**: Multiple Burp files loaded during initialization

#### Asset Management System
1. **Initialization**: LoadBurpFile loads multiple asset files
2. **Lookup**: AssetLookupByName searches for assets by name/ID  
3. **Reference Counting**: AssetGetHandle manages memory and references
4. **Data Processing**: ProcessSpriteData decompresses and renders sprite data

#### Asset Table Structure
- **Capacity**: Up to 80 assets (0x50)
- **Entry Size**: 41 bytes (0x29) per entry
- **Search Method**: Linear search with string comparison
- **Indexing**: Assets identified by name string and numeric ID

#### Sprite Data Format (Within Burp Files)
- **Frame-based**: Sprites contain multiple frames
- **Coordinate Data**: Each frame has vertex/coordinate arrays
- **Type System**: Different sprite types handled by function pointer table
- **Real-time Processing**: Data processed during rendering, not pre-decompressed

### Current Analysis Status - UPDATED

#### ✅ CONFIRMED DISCOVERIES
1. **Burp File Format**: Complete understanding of structure with 20-byte entry table
2. **LoadBurpFile**: Correctly parses Burp files and entry tables
3. **Asset Management System**: Lookup, reference counting, and handle management
4. **Asset Access Pattern**: How game accesses decompressed asset data
5. **Sprite Rendering**: ProcessSpriteData handles sprite rendering (not decompression)

#### ❌ STILL MISSING - The Core Decompression Function
**CRITICAL GAP**: The function that actually decompresses individual assets from the Burp files.

**What This Function Must Do**:
1. Take a Burp entry index or handle
2. Access the entry table to get DataOffset and CompressedLength
3. Seek to DataOffset in the Burp file
4. Read CompressedLength bytes of compressed data
5. Decompress to UncompressedLength bytes using some algorithm
6. Return pointer to decompressed asset data

**Search Criteria**: Looking for a function that:
- Takes parameters related to compressed/uncompressed sizes
- Contains decompression algorithm (loops, bit manipulation, RLE, LZ, etc.)
- Is called between AssetGetHandle and asset data access
- Might be called lazily when asset is first accessed

#### Potential Decompression Scenarios
1. **Immediate Decompression**: Assets decompressed when Burp file is loaded
2. **Lazy Decompression**: Assets decompressed on first access
3. **Cached Decompression**: Assets decompressed and cached when requested

#### Next Investigation Steps
1. Examine functions called between AssetGetHandle and data access
2. Look for functions with loop patterns typical of decompression algorithms  
3. Search for functions taking size parameters (compressed/uncompressed lengths)
4. Trace the exact path from asset request to data availability

### Function Summary - CORRECTED

#### ✅ Correctly Identified Functions
- **AssetConfigParser** (FUN_0042f580): **Asset selection controller** - Configuration parser that determines which Burp files to load ✅
- **LoadBurpFile** (FUN_0042f9b6): Burp file loader with correct 20-byte entry parsing ✅  
- **AssetLookupByName** (FUN_00406e20): Asset search in entry table ✅
- **AssetGetHandle** (FUN_00407220): Asset handle/reference management ✅
- **InitializeAssetSystem** (FUN_00407a10): Overall asset system initialization ✅

#### 🔄 Reclassified Functions  
- **ProcessSpriteData** (FUN_00426a50): Sprite RENDERING (not decompression) 🔄
- **FileReadWrapper** (FUN_00434af0): Low-level file I/O ✅

#### ❓ Missing Functions
- **[UNKNOWN]**: The actual asset decompression function ❓

### The Hunt Continues
The analysis has successfully mapped the asset management system but the core decompression algorithm remains elusive. This function is critical to understanding how the compressed data in Burp files is processed into usable game assets.

### 🎯 MAJOR BREAKTHROUGH: Asset Decompression Functions Found!

After extensive analysis, I've discovered the missing piece of the puzzle - the actual asset decompression functions!

#### DecompressAssetData (FUN_0043987c) - **PRIMARY DECOMPRESSION FUNCTION** ⭐
**Function Purpose**: Main asset decompression function that handles multiple compression formats.

**Key Characteristics**:
1. **Multi-format Support**: Uses function pointer tables for different compression modes
2. **Bit-pattern Analysis**: Examines high bits to determine compression type
3. **RLE Processing**: Handles Run-Length Encoding with repeat counts
4. **Literal Copy**: Direct byte copying for uncompressed sections
5. **Efficient Processing**: Optimized loops for data copying

**Technical Details**:
- **Input**: Compressed data pointer and size
- **Output**: Writes to output buffer (passed in register)
- **Compression Types**: Different algorithms based on bit patterns (0x60, 0x80 masks)
- **Function Tables**: `DAT_00459520` and `PTR_LAB_00459530` for decompression modes

#### BitwiseDecompressor (FUN_0043ab01) - **SECONDARY DECOMPRESSION FUNCTION** ⭐
**Function Purpose**: Handles bit-level decompression with multiple algorithm modes.

**Key Characteristics**:
1. **Bit-level Processing**: Reads individual bits from compressed stream
2. **Mode Selection**: Uses 2-bit patterns to select decompression algorithm
3. **Multiple Algorithms**: Dispatches to FUN_0043a25c, FUN_0043a377, FUN_0043a553
4. **State Machine**: Maintains bit buffer and position state

#### DecompressionDispatcher (FUN_00432342) - **ALGORITHM SELECTOR**
**Function Purpose**: Selects appropriate decompression algorithm based on asset type.

**Dispatch Logic**:
- **Type 0**: Uses `FUN_0043cdb2` (memcpy - uncompressed)
- **Type 2-3**: Uses `DecompressAssetData` (main compression)
- **Type 4**: Uses `BitwiseDecompressor` (bit-level compression)

#### Sprite Rendering Functions (Analyzed)

**FUN_00426c18 - Simple Sprite Renderer (Type 0)**
- **Usage**: Renders simple sprites (likely Type 0).
- **Fast Path** (!Mirrored):
  - Format: `[Skip] [Run4] [Run1] [Data] [EndSkip]`
  - Single segment per row.
  - No Segment Count byte.
- **Mirrored Path** (Mirrored):
  - Format: `[Count] ([Skip] [Run4] [Run1] [Data])...`
  - Multiple segments per row.
  - Renders Right-to-Left.

**FUN_0042666c - Complex Sprite Renderer (Type 1)**
- **Usage**: Renders complex sprites (likely Type 1).
- **Fast Path** (!Mirrored):
  - Format: `[Count] ([Skip] [Run4] [Run1] [Data])... [EndSkip]`
  - Multiple segments per row.
  - Renders Left-to-Right.
- **Mirrored Path** (Mirrored):
  - Same format as FUN_00426c18 Mirrored Path.

**Key Difference**:
- Simple Fast Path has **NO Segment Count**.
- Complex Fast Path **HAS Segment Count**.
- Misinterpreting Simple as Complex causes `Skip` to be read as `Count`. If `Skip=0` (Left-aligned), `Count=0`, resulting in an empty row (Transparent Gap).
