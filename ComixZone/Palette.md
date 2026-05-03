# Comix Zone Palette Analysis

## Palette Location
The palette data location is currently **unknown**.
Previous analysis identified `0x004325a5` as the source, but this area appears to be empty (zeros) in the executable file.
However, the code in `WndProc` reads from this address, implying it is populated at runtime.

## Wingpal and Identity Palette
The presence of `Wingpal.exe` suggests the game uses the **WinG** graphics library.
WinG requires an **Identity Palette** to achieve high-performance drawing.
-   **Identity Palette**: A palette where the first 10 and last 10 colors match the system palette exactly. The middle 236 colors are defined by the game.
-   **Wingpal**: A utility provided with the WinG SDK (or custom-built) to capture the system palette and generate an identity palette.

It is highly likely that the game's palette data (the middle 236 colors) originated from `Wingpal` or was generated using it.
The data might be:
1.  Stored inside `Wingpal.exe` and loaded/copied by the game (unlikely unless `Wingpal` is executed).
2.  Embedded in `ComixZone.exe` in a different location (e.g., compressed or in a resource) and copied to `0x004325a5` at runtime.
3.  Stored in `SPRITES.BIN` or another data file and loaded to `0x004325a5`.

## Loading Mechanism (Revised)
The `WndProc` function updates the palette entries 10-245 from `0x004325a5`.
Since `0x004325a5` is empty in the file, there must be a missing initialization step that populates this memory area.

### Code Snippet (Decompiled)
```c
// Loop from index 10 to 245
for (i = 10; i < 246; i++) {
    // Destination: BITMAPINFO palette (RGBQUAD: Blue, Green, Red, Reserved)
    // Source: Memory at 0x004325a5 (Red, Green, Blue, Unused)
    
    // Set Red
    dest[i].rgbRed = source_base[i * 4];     // Reads from 0x004325a5 + offset
    
    // Set Green
    dest[i].rgbGreen = source_base[i * 4 + 1]; // Reads from 0x004325a6 + offset
    
    // Set Blue
    dest[i].rgbBlue = source_base[i * 4 + 2];  // Reads from 0x004325a7 + offset
    
    // Set Reserved
    dest[i].rgbReserved = 0;
}
```

## Next Steps
-   Analyze `Wingpal.exe` to see if it contains the palette data.
-   Investigate how `0x004325a5` is populated in `ComixZone.exe` (look for writes to this address or `memcpy` calls).
-   Check if `SPRITES.BIN` contains the palette data at the beginning or end.
