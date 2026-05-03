# Youngblood WDL File Analysis

## `loadWdlFile`

This method is the entry point for loading a .wdl file. It takes a single argument, which is the path to the .wdl file to load.

It first checks if a file is already being loaded. If so, it prints an error and returns. Otherwise, it sets a flag to indicate that a load is in progress.

It then calls `openFile` to open the .wdl file. If the file cannot be opened, it prints an error and returns.

If the file is opened successfully, it enters a loop that continues as long as the `isLoading` flag is set. Inside the loop, it calls `loadWdlChunk` to process the next chunk in the file.

Once the loop finishes, it calls `closeFile` to close the .wdl file.

## `loadWdlChunk`

This method is responsible for loading and processing a single chunk from the .wdl file. It takes a single argument, which is the handle to the .wdl file.

It first reads the chunk name and size from the file. The chunk name is a 12-byte string, and the chunk size is a 32-bit unsigned integer.

If the chunk size is greater than 0, it allocates memory for the chunk data and reads the chunk data from the file.

It then iterates through a list of registered chunk handlers. For each handler, it compares the chunk name with the handler's name. If they match, it calls the handler's function, passing the chunk data and size as arguments.

If no handler is found for the chunk, it prints an error.

## `registerAllChunkHandlers` - FUN_004015a0 & `registerChunkHandler` - FUN_00401bf7

The `registerAllChunkHandlers` function is responsible for setting up the handlers for the different data chunks within the WDL file. It calls `registerChunkHandler` for each chunk type, providing the chunk name (e.g., "GRA", "ACTOR") and a function pointer to the corresponding handler.

```c
void FUN_004015a0(void)

{
  FUN_00401bf7((uint *)s_LEVFILE_0046d068,FUN_00402080); // Register handler for "LEVFILE" chunk
  FUN_00401bf7((uint *)s_ACTOR_0046d070,FUN_004020ca); // Register handler for "ACTOR" chunk
  FUN_00401bf7((uint *)s_GRA_0046d078,FUN_00402116); // Register handler for "GRA" chunk
  FUN_00401bf7((uint *)s_SEQ_0046d07c,FUN_004021c6); // Register handler for "SEQ" chunk
  FUN_00401bf7((uint *)s_EXLV10_0046d080,FUN_00402207); // Register handler for "EXLV10" chunk
  FUN_00401bf7((uint *)s_RES_0046d088,FUN_0040619e); // Register handler for "RES" chunk
  FUN_00401bf7((uint *)s_GRC_0046d08c,FUN_0040bd90); // Register handler for "GRC" chunk
  FUN_00401bf7((uint *)s_GRH_0046d090,FUN_0040282b); // Register handler for "GRH" chunk
  FUN_00401bf7((uint *)s_GRB_0046d094,FUN_0040255e); // Register handler for "GRB" chunk
  FUN_00401bf7((uint *)s_WLB_0046d098,FUN_00402640); // Register handler for "WLB" chunk
  FUN_00401bf7((uint *)s_SNUG_0046d09c,FUN_00413de0); // Register handler for "SNUG" chunk
  FUN_00401bf7((uint *)s_MLB_0046d0a4,FUN_004027a4); // Register handler for "MLB" chunk
  FUN_00401bf7((uint *)s_TOC_0046d0a8,FUN_00402244); // Register handler for "TOC" chunk  
  FUN_00401bf7((uint *)s_NULL_0046d0ac,FUN_00402244); // Register handler for "NULL" chunk 
  FUN_00401bf7((uint *)s_SELECT_0046d0b4,FUN_004020a0); // Register handler for "SELECT" chunk
  return;
}
```

## `loadGraChunk`

This is the handler for the "GRA" chunk. It takes the raw chunk data and passes it to `processGraData` to be processed.

## `processGraData`

This function acts as a dispatcher. It checks a flag within the GRA data to determine if it's a simple or complex graphics format.

- If the format is simple, it calls `processSimpleGra`.
- If the format is complex, it calls `processComplexGra`.

### `processSimpleGra`

This function handles a simple graphics format. It appears to perform some pointer arithmetic on the data, but the exact nature of the format is not yet clear.

### `processComplexGra`

This function handles a more complex, structured graphics format. It iterates through a list of graphical elements, adjusts their data pointers, and seems to decompress or decode the image data by calling `FUN_00410c06`. It also calculates the maximum width and height from the elements.

## `loadGrbChunk` - FUN_0040255e

This is the handler for the "GRB" chunk. It stores a pointer to the chunk in a small global table (capacity 10), initializes some GRB-related globals, and immediately calls `processGraData` (`FUN_0040ebbc`) on the chunk pointer. The returned graphics descriptor pointer is stored in a parallel global array. It also resets a global state flag (`DAT_0046d7dc = 0`).

Key observations from `FUN_0040255e`:
- Enforces a max of 10 GRB chunks (`DAT_0046d7b8` index).
- Saves the raw chunk pointer at `DAT_00494320[index]`.
- Initializes some per-entry fields: a handle/id at `DAT_00493f98[index*0x18]` and two 0x10 values (likely default width/height or tile size).
- Calls `processGraData` on the chunk pointer and stores result at `DAT_00493f60[index]`.

This implies GRB chunks are just GRA containers with the same internal structure, and decoding can reuse the exact GRA parsing logic.

## `loadWlbChunk` - FUN_00402640

This is the handler for the "WLB" chunk. It stores the chunk pointer into a table (capacity 10), then reads the first byte from the chunk and caches it in a parallel array, and finally advances the stored pointer by 4 bytes.

Key observations from `FUN_00402640`:
- Enforces a max of 10 WLB chunks (`DAT_0046d7bc` index).
- Saves the raw chunk pointer at `DAT_00493f30[index]`.
- Copies the first byte of the chunk into `DAT_00494310[index]`.
- Advances the stored pointer by 4 bytes (`DAT_00493f30[index] += 4`).

Interpretation: the first 4 bytes appear to be a small header (first byte used as a type/flag), and subsequent consumers use the adjusted pointer as the actual data start. There is no image decoding here; it is just lightweight bookkeeping.
