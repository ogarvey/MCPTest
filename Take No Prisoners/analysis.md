# Take No Prisoners Analysis

This document details the analysis of the game "Take No Prisoners".

## Function Analysis

### `_set_osfhandle` (Previously `FUN_004bb5a0`)

*   **Functionality:** This function associates an integer file descriptor with a Windows OS file `HANDLE`. For file descriptors 0, 1, and 2, it correctly sets the standard input, standard output, and standard error handles for the process using the `SetStdHandle` API.
*   **Reasoning for Rename:** The behavior is identical to the `_set_osfhandle` function in the Microsoft C runtime library.

### `_sopen` (Previously `FUN_004bb890`)

*   **Functionality:** This function opens a file using the `CreateFileA` Windows API call, taking a filename, open flags, and sharing flags as parameters. It determines the correct parameters for `CreateFileA` based on the input flags.
*   **Reasoning for Rename:** The function's purpose and parameters directly correspond to the `_sopen` function, which is used for opening a file with file sharing.
*   **Variable Renames:**
    *   `param_1` -> `filename`
    *   `param_2` -> `oflag`
    *   `param_3` -> `shflag`
    *   `local_10` -> `dwDesiredAccess`

### `_fsopen` (Previously `FUN_004b22c0`)

*   **Functionality:** This function opens a file as a stream, processing the `mode` string to determine the access and translation modes. It then calls `_sopen` to perform the actual file opening and initializes a `stream` structure for file I/O.
*   **Reasoning for Rename:** The function's behavior of parsing a mode string and returning a file stream object is characteristic of the `_fsopen` function.
*   **Variable Renames:**
    *   `param_1` -> `filename`
    *   `param_2` -> `mode`
    *   `param_3` -> `shflag`
    *   `param_4` -> `stream`

### `fopen` (Previously `FUN_004ad0e0`)

*   **Functionality:** This function acts as a wrapper for `_fsopen`. It first allocates a stream structure by calling `FUN_004b2490` and then passes this stream along with the filename and mode to `_fsopen` to open the file.
*   **Reasoning for Rename:** This pattern of allocating a stream and then using it to open a file is characteristic of the standard C library function `fopen`.
*   **Variable Renames:**
    *   `param_1` -> `filename`
    *   `param_2` -> `mode`

### `res_by_name_load` (Previously `FUN_0046f6f0`)

*   **Functionality:** This function is a core component of the game's resource management system. It searches for a resource by its filename. If the resource is already loaded, it returns a handle. Otherwise, it loads the resource, adds it to a global list, and returns a new handle. It includes a fallback mechanism to load a "default" resource if the requested one is not found.
*   **Reasoning for Rename:** The name `res_by_name_load` accurately reflects its function of loading resources based on their string identifier. The high number of cross-references to this function indicates its central role as a resource loader.
*   **Variable Renames:**
    *   `param_1` -> `filename`
    *   `local_1c` -> `p_resource_list`
    *   `local_18` -> `resource_idx`
    *   `local_10` -> `default_filename`

### `res_get_data` (Previously `FUN_00470090`)

*   **Functionality:** This function retrieves the data for a given resource. It takes a resource name, looks up the resource using `res_by_name_load`, and then returns a pointer to the resource's data. If the resource is not already loaded, it calls `res_load_and_decompress` to load it from the disk.
*   **Reasoning for Rename:** The name `res_get_data` accurately describes its purpose of fetching resource data.
*   **Variable Renames:**
    *   `param_1` -> `res_name`
    *   `param_2` -> `pp_res_data`

### `res_load_and_decompress` (Previously `FUN_0046fa80`)

*   **Functionality:** This function is responsible for loading a resource from a file and decompressing it if necessary. It reads the raw data from the game's data files and, if the data is compressed, it calls `rle_decompress` to decompress it.
*   **Reasoning for Rename:** The name clearly indicates that the function handles both loading and decompression.
*   **Variable Renames:**
    *   `param_1` -> `p_res_entry`

### `rle_decompress` (Previously `FUN_0046fcb0`)

*   **Functionality:** This function performs Run-Length Encoding (RLE) decompression on the raw file data. This is the key function for understanding how the `.sfd` files are compressed.
*   **Reasoning for Rename:** The name `rle_decompress` directly reflects the algorithm it implements.
*   **Variable Renames:**
    *   `param_1` -> `compressed_data`
    *   `param_2` -> `decompressed_data`
    *   `param_3` -> `compressed_size`
    *   `param_4` -> `is_decompression_run`

*   **Pseudocode:**
    ```
    function rle_decompress(compressed_data, decompressed_data, compressed_size, is_decompression_run)
      // Read the compression type from the first byte
      compression_type = compressed_data[0]

      // Check for a specific compression signature
      if compression_type < 8 and compressed_data[1] == 1 then
        // Get the decompressed size from the next 4 bytes
        decompressed_size = read_integer(compressed_data, 2)

        // If this is just a size-check run, return the decompressed size
        if is_decompression_run == 0 then
          return decompressed_size
        end if

        // Initialize pointers and counters
        output_ptr = decompressed_data
        output_end = decompressed_data + decompressed_size
        input_offset = 6

        // Main decompression loop
        while output_ptr < output_end do
          // Check for buffer overflow
          if compressed_size <= input_offset then
            return 0 // Error
          end if

          // Read the control byte
          control_byte = compressed_data[input_offset]

          // If the top 3 bits match the compression type, it's a run
          if (control_byte >> 5) == compression_type then
            // The next byte is the value to repeat
            run_value = compressed_data[input_offset + 1]
            // The lower 5 bits of the control byte are the run length
            run_length = control_byte & 0x1f

            // Write the run to the output
            for i from 0 to run_length - 1 do
              *output_ptr = run_value
              output_ptr = output_ptr + 1
            end for

            // Advance the input offset by 2 (control byte + value byte)
            input_offset = input_offset + 2
          else
            // It's a literal, so just copy the byte
            *output_ptr = control_byte
            output_ptr = output_ptr + 1
            // Advance the input offset by 1
            input_offset = input_offset + 1
          end if
        end while

        // Check for a specific end-of-stream marker
        if compressed_data[input_offset] == 0x97 and output_ptr == output_end then
          return decompressed_size // Success
        end if
      end if

      return 0 // Error
    end function
    ```
