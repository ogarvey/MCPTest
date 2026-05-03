# WMTMANIM DLL Analysis

This document details the analysis of the WMTMANIM DLL, focusing on how it processes `.ANX` files.

## Initial Findings

The user has pointed to `FUN_1020_0e5e` as a potential starting point for understanding how `.ANX` files are processed. This function is believed to be involved with colors, compression, and RLE (Run-Length Encoding).

## `LoadAnxAndCreateSprite` (formerly `FUN_1020_0e5e`)

This function is the core of the ANX file processing. It performs the following steps:

1.  **Finds a free sprite slot** by calling `FindFreeSpriteSlot` (formerly `FUN_1020_0de5`).
2.  **Initializes the sprite slot** by calling `InitializeSpriteSlot` (formerly `FUN_1020_0e1b`).
3.  **Opens the .ANX file** using standard file I/O functions.
4.  **Reads the file header** to get information about the image, such as width, height, and compression type.
5.  **Handles color palettes**:
    *   If the ANX file contains a palette, it calls `GetColorsFromTable` (formerly `COLORSFROMTABLE`) to read the color data.
    *   It then calls `CreateMasterPalette` (formerly `CREATEMASTERPALETTE`) to create a palette for the sprite.
6.  **Decompresses the frame data**:
    *   It calls `DecompressAnxFrame` (formerly `UNCOMPRESSANXFRAME`) for standard decompression.
    *   For RLE-compressed frames, it calls `DecompressRleFrame` (formerly `ANXUNFHARLEFRAME`).
7.  **Copies the frame data to a DIB** by calling `CopyFrameToDib` (formerly `COPYANXFRAMERECTTODIB`).
8.  **Adds the sprite to the game engine**:
    *   `AddSpriteToEngine` (formerly `ADDSPRITE`) adds the main sprite structure.
    *   `AddSpriteDibToEngine` (formerly `ADDSPRITEDIB`) adds the DIB data.
    *   `SetSpriteDib` (formerly `SETSPRITESPRITEDIB`) links the DIB to the sprite.
9.  **Allocates memory for frame data** by calling `AllocateMemoryForFrameData` (formerly `FUN_1000_0344`).

This function is central to understanding how the game loads and displays animated sprites from `.ANX` files.
