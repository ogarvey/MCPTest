# Anemone Reverse Engineering Log

This document details the analysis of the Anemone game binary.

## Function Analysis

### `load_sprite` (formerly `FUN_0046ab20`)

This function is the main entry point for loading a sprite. It takes a file path as input, opens the file, and then calls a series of other functions to read the sprite data.

**Call Tree:**
*   `load_sprite` (`FUN_0046ab20`)
    *   `read_sprite_from_file` (`FUN_0046aac0`)
        *   `read_sprite_file_data` (`FUN_0046ae80`)
            *   `validate_sprite_header` (`FUN_0046aef0`) - Checks for "Game Sprite File" string in the header.
            *   `read_sprite_data_from_file` (`FUN_0046afa0`)
                *   `read_sprite_dimensions` (`FUN_0046a6b0`)
                *   `read_sprite_pixel_data` (`FUN_00469330`)

### RLE Decompression and File Buffering

The game uses a custom file I/O system that includes RLE decompression and extensive debug reporting.

*   **`read_and_decompress_rle`** (formerly `FUN_004b1970`): Orchestrates reading from the custom file stream. It decides whether to decompress data based on stream flags.

*   **`decompress_rle_byte`** (formerly `FUN_004b2af0`): Returns the next byte from the stream's buffer. If the buffer is empty, it calls `allocate_stream_buffer` if necessary, then `read_bytes_from_file` to refill it. It contains assertions that call `debug_report`.

*   **`allocate_stream_buffer`** (formerly `FUN_004b9df0`): Allocates memory for the file stream's internal buffer using a debug version of `malloc`.

*   **`read_bytes_from_file`** (formerly `FUN_004b8020`): A low-level function to read a block of bytes from the file into the stream's buffer.

*   **`memcpy_custom`** (formerly `FUN_004b1000`): A custom memory copy function.

*   **`debug_report`** (formerly `FUN_004b5580`): A comprehensive debug reporting function used for assertions and error logging. It can output to files, the debugger, and message boxes.
