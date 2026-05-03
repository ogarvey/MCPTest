# Gargoyles Remastered Analysis

This document details the analysis of the Gargoyles Remastered game executable.

## Initial Findings

- The game appears to use `.mtc` and `.mdf` files for its assets.
- A function at `0x004318c0` seems to be involved in processing these files.

## Asset Loading

The function `load_asset_by_name` (previously `FUN_004318c0`) is responsible for loading game assets. It takes an asset name as input and attempts to locate and load the corresponding asset data.

The asset loading process appears to be as follows:
1. The function constructs file paths for `.mdf` and `.mtc` files based on the asset name.
2. It checks a cache to see if the asset information is already available.
3. If the asset is not in the cache, it reads the asset data from the `.mdf` and `.mtc` files.
4. The asset data is then read from the file.

### Renamed Functions

The following functions were identified and renamed during the analysis of `load_asset_by_name`:

| Original Name  | New Name                          | Description                                           |
|----------------|-----------------------------------|-------------------------------------------------------|
| `FUN_004318c0` | `load_asset_by_name`              | Loads an asset by its name.                           |
| `FUN_00433060` | `get_asset_info_by_name`          | Retrieves asset information by name.                  |
| `FUN_00432f60` | `get_asset_info_by_name_and_size` | Retrieves asset information by name and size.         |
| `FUN_00430a10` | `read_asset_from_file`            | Reads asset data from a file.                         |
| `FUN_00431140` | `read_asset_data`                | Reads asset data from a file into a buffer.                              |
| `FUN_004331e0` | `get_asset_info_from_cache`       | Retrieves asset information from the cache.           |
| `FUN_00432e70` | `release_asset_info`              | Releases asset information.                           |
| `FUN_00431da0` | `release_asset_from_cache`        | Releases an asset from the cache.                     |
| `FUN_00433560` | `get_asset_size_from_info`        | Gets the asset size from its information.             |
| `FUN_00432ff0` | `create_asset_info`               | Creates an asset information structure.               |
| `FUN_00430fc0` | `release_memory`                  | Releases allocated memory.                            |

### Renamed Variables in `load_asset_by_name`

| Original Name | New Name            | Description                               |
|---------------|---------------------|-------------------------------------------|
| `param_1`     | `asset_name`        | The name of the asset to load.            |
| `local_c10`   | `asset_info`        | Information about the asset.              |
| `local_c14`   | `asset_size`        | The size of the asset.                    |
| `local_30c`   | `mdf_path`          | The path to the `.mdf` file.              |
| `local_60c`   | `mtc_path`          | The path to the `.mtc` file.              |
| `local_c0c`   | `asset_path_buffer` | A buffer for constructing asset paths.    |

### `read_asset_data`

The `read_asset_data` function is responsible for reading the asset data from the file and writing it to an output buffer. The function reads the data in chunks of 1MB (`0x100000` bytes).

The logic is as follows:

1.  The function calculates the number of 1MB chunks and the number of remaining bytes.
2.  If the total size is less than 1MB, it calls `read_asset_chunk` to read the entire asset in one go.
3.  If the size is larger than 1MB, it iteratively reads the data in 1MB chunks using `ReadFileEx`.
4.  After reading all the full chunks, it reads the remaining bytes.
5.  The function uses a completion routine (`read_file_completion_routine`) to handle the asynchronous file reads. It waits for each read operation to complete before proceeding.

#### Pseudocode

```c
function read_asset_data(asset_info, offset, size, output_buffer) {
  num_chunks = size / 0x100000;
  remaining_bytes = size % 0x100000;

  if (num_chunks == 0 && remaining_bytes == 0) {
    read_asset_chunk(asset_info, offset, size, output_buffer);
    wait_for_completion();
  } else {
    current_offset = offset;
    current_output_buffer = output_buffer;

    for (i = 0; i < num_chunks; i++) {
      read_chunk_async(asset_info, current_offset, 0x100000, current_output_buffer);
      wait_for_completion();
      current_offset += 0x100000;
      current_output_buffer += 0x100000;
    }

    if (remaining_bytes > 0) {
      read_chunk_async(asset_info, current_offset, remaining_bytes, current_output_buffer);
      wait_for_completion();
    }
  }
}
```

#### Renamed Functions

| Original Name                | New Name                       | Description                                      |
|------------------------------|--------------------------------|--------------------------------------------------|
| `FUN_00431040`               | `read_asset_chunk`             | Reads a chunk of asset data.                     |
| `FUN_00430710`               | `log_error`                    | Logs an error message.                           |
| `lpCompletionRoutine_004307a0` | `read_file_completion_routine` | Completion routine for asynchronous file reads.  |

#### Renamed Variables

| Original Name          | New Name                | Description                                    |
|------------------------|-------------------------|------------------------------------------------|
| `param_1`              | `asset_info`            | Information about the asset.                   |
| `param_2`              | `offset`                | The offset to start reading from.              |
| `param_3`              | `size`                  | The size of the data to read.                  |
| `param_4`              | `output_buffer`         | The buffer to write the data to.               |
| `local_14`             | `num_chunks`            | The number of 1MB chunks to read.              |
| `nNumberOfBytesToRead` | `remaining_bytes`       | The number of bytes remaining after the chunks.|
| `local_10`             | `current_chunk`         | The current chunk being processed.             |
| `local_c`              | `current_output_buffer` | The current position in the output buffer.     |
| `local_8`              | `current_offset`        | The current offset in the file.                |

### `read_asset_metadata_from_mtc`

The function `read_asset_metadata_from_mtc` (previously `FUN_00432240`) is responsible for reading the asset metadata from the `.mtc` file. It takes the asset name as input, and populates an `asset_info` structure with the offset and size of the compressed data within the `.mdf` file.

## File Formats

### `.mtc` File Format

The `.mtc` file acts as an index for the `.mdf` file. It has the following structure:

-   `uint32` - Offset to a group of offsets.
-   `uint32` - Unknown.
-   `uint32` - Number of subfiles in the `.mdf` archive.
-   `uint32` - Unknown.
-   An array of `uint32` offsets, with the count given by the "Number of subfiles" field.

Each of these offsets points to a 32-byte structure with the following layout:

-   Unknown bytes.
-   Offset into the `.mdf` file for the subfile.
-   Length of the whole subfile.
-   Length of the actual data within the subfile (without padding).
-   Other unknown data.

### `.mdf` File Format

The `.mdf` file is a large archive containing multiple compressed subfiles. The `.mtc` file provides the necessary information to locate and extract these subfiles.

## Asset Caching

The game uses a caching system to manage loaded assets. This system is initialized by a set of functions called from `initialize_game_systems`.

### Cache Initialization

-   **`initialize_critical_section`** (previously `FUN_00433990`): Initializes a critical section for thread-safe access to the asset caches.
-   **`initialize_asset_cache_small`** (previously `FUN_004336a0`): Initializes a small asset cache with a capacity of 32 items.
-   **`initialize_asset_cache_large`** (previously `FUN_004336f0`): Initializes a large asset cache with a capacity of 768 items.
-   **`initialize_asset_cache_medium`** (previously `FUN_00433740`): Initializes a medium asset cache with a capacity of 128 items.

## Asset Decryption

The game uses AES encryption to protect its assets. The decryption process is handled by the `decompress_asset_data` function (previously `FUN_00430200`), which is called from `process_asset` (previously `FUN_004328d0`).

### Decryption Process

The decryption process uses the Windows Cryptography API (BCrypt) and follows these steps:

1.  **Algorithm Provider**: An algorithm provider is opened for the "AES" algorithm using `BCryptOpenAlgorithmProvider`.
2.  **Chaining Mode**: The chaining mode is set to "ChainingModeCBC" (Cipher Block Chaining) using `BCryptSetProperty`.
3.  **Key Generation**: A symmetric key is generated using `BCryptGenerateSymmetricKey`. The key is a 16-byte value that is passed as a parameter to the function.
4.  **Decryption**: The asset data is decrypted using `BCryptDecrypt`. This function takes the encrypted data, the key, and an initialization vector (IV) as input. The IV is also passed as a parameter.

### `process_asset`

The `process_asset` function is responsible for managing the asset processing workflow. It checks the asset type and determines whether the asset needs to be decrypted.

-   If the asset is encrypted (type `1`), `process_asset` calls `decompress_asset_data` to decrypt the asset.
-   If the asset is not encrypted (type `2`), it's simply copied to the output buffer.

### `decompress_asset_data`

This function implements the AES decryption logic using the BCrypt API.

#### Renamed Variables

| Original Name | New Name         | Description                               |
|---------------|------------------|-------------------------------------------|
| `param_1`     | `output_buffer`  | The buffer for the decrypted data.        |
| `param_2`     | `output_size`    | The size of the decrypted data.           |
| `param_3`     | `input_buffer`   | The buffer containing the encrypted data. |
| `param_4`     | `input_size`     | The size of the encrypted data.           |
| `param_5`     | `iv`             | The initialization vector.                |
| `param_7`     | `key`            | The decryption key.                       |
| `local_8`     | `alg_handle`     | The handle to the algorithm provider.     |
| `local_c`     | `key_handle`     | The handle to the decryption key.         |
| `local_14`    | `result_size`    | The size of the decrypted data.           |
| `local_18`    | `decrypted_buffer`| The buffer for the decrypted data.        |
| `local_1c`    | `object_length`  | The size of the key object.               |
| `local_20`    | `bytes_written`  | The number of bytes written.              |
| `local_10`    | `block_length`   | The block length of the algorithm.        |


IV - 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
Key - 38 45 37 44 35 35 44 42 32 43 43 39 00 00 00 00
