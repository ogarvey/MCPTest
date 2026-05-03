# 20SYS_R Analysis

## Goal
Analyze `cweUnpackFile` and `cweCVolMaker::Unpack` to understand the decompression algorithm.

## Progress
- [x] Analyze `cweUnpackFile`
- [x] Analyze `cweCVolMaker::Unpack`
- [x] Rename variables
- [x] Document algorithm

## Analysis Findings

### Call Chain
1. `cweUnpackFile` (10005d40)
   - Prepares destination paths.
   - Calls `cweCVolMaker::Unpack` with lookup key `"$cwe20PF$"`.
2. `cweCVolMaker::Unpack` (10006aa0)
   - Wrapper around `cweCVolume::FileUnpack`.
3. `cweCVolume::FileUnpack` (10003200)
   - Uses `cweHashUGet` to look up the file entry using the provided key (e.g., `"$cwe20PF$"`).
   - The key acts as an identifier for the file entry in the volume's hash table.
   - Creates destination folder.
   - Calls `LowLevelRead` to read and decompress data.
   - Writes data to disk.
4. `LowLevelRead` (10005ee0)
   - Delegates to `FUN_10006190`.
5. `FUN_10006190` (10006190)
   - Manages decompression loop.
   - Calls `cweCPacker::Process`.
6. `cweCPacker::Process` (10007fa0)
   - Implements a state machine for decompression.
   - Uses zlib 1.0.4 (inflate).

### Variable Renaming
- **cweUnpackFile**:
    - `param_1` -> `archivePath`
    - `param_2` -> `destinationPath`
    - `param_3` -> `checkExists`
    - `local_20` -> `volMaker`
    - `local_22c` -> `destDir`
    - `local_128` -> `destFileName`
- **cweCVolMaker::Unpack**:
    - `param_1` -> `lookupKey` (Was `entryName`)
    - `param_2` -> `destDir`
    - `param_3` -> `destFileName`
- **cweCVolume::FileUnpack**:
    - `param_1` -> `lookupKey`
    - `param_2` -> `destDir`
    - `param_3` -> `destFileName`
    - `puVar3` -> `fileEntry`

### Algorithm
The DLL uses zlib 1.0.4 to decompress a specific file entry identified by the key `"$cwe20PF$"` from the archive. The decompression logic is encapsulated in `cweCPacker::Process`. The key `"$cwe20PF$"` is used to retrieve the file's metadata (offset, size, etc.) from the volume's hash table.
