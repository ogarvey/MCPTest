# Claw Analysis

## Initial Information
- Graphics stored in PID files.
- 32-byte header.
- First 4 bytes: `0x0a000000`.
- Followed by 7 integers.
- Width/Height likely in 2nd/3rd or 3rd/4th integers.
- Image data is RLE compressed.

## Findings

### File Loading
- `LoadAsset` (004c0a10) determines file type by extension.
- `LoadPID` (004c0510) loads the entire file into memory and calls `ParsePID`.
- `ParsePID` (004c0300) parses the PID header and calls `DecompressPIDRLE`.

### PID Header Format
Based on `ParsePID`:
- Offset 0: Signature (Likely `0x0a000000`, though not explicitly checked in `ParsePID`, possibly checked elsewhere or ignored).
- Offset 4: Flags (uint).
- Offset 8: Width (uint).
- Offset 12: Height (uint).
- Offset 16-31: Unknown/Unused (Header is 32 bytes).
- Offset 32: Start of Compressed Data.

### Decompression Algorithm
`DecompressPIDRLE` (004bfdd0) implements a PCX-style RLE decompression:
- Iterates through each row (`height` times).
- For each row, it fills pixels until `width` is reached.
- Reads a byte `b`.
- The game binary uses `0xC0` as the mask (top 2 bits).
- However, analysis of `FRAME001.PID` suggests a `0x80` mask (top bit) is used for some files, as it contains values like `0xBE` (which would be raw in `0xC0` mode but a run in `0x80` mode) and is compressed.
- If `(b & Mask) == Mask`:
  - It's a run.
  - `count = b & ~Mask`.
  - `value = next byte`.
  - Output `value` repeated `count` times.
- Else:
  - It's a raw value.
  - Output `b`.
- Handles spillover if a run crosses the row boundary.

### Alternative Compression (Hypothesis)
The user has identified a potential alternative compression scheme in some files:
- `[Opcode] [Count] [Data]`
- If `Opcode & 0x80` is set: Skip `Opcode & 0x7F` pixels (Transparency).
- If `Opcode & 0x80` is clear: Copy `Opcode` bytes from input to output.
- This matches the pattern `BE 04 ...` (Skip 62, Copy 4) and `92 BD 06 ...` (Skip 18, Skip 61, Copy 6).
- This logic is NOT present in `DecompressPIDRLE` (004bfdd0). It might be in another function or the file is not a standard PID file handled by `LoadPID`.

### Functions Renamed
- `FUN_004bfdd0` -> `DecompressPIDRLE`
- `FUN_004c0300` -> `ParsePID`
- `FUN_004c0510` -> `LoadPID`
- `FUN_004c0a10` -> `LoadAsset`

### Implementation
A C# implementation of the decompressor has been created in `PidDecompressor.cs`. It implements the `0x80` mask logic to support the observed file format, noting the discrepancy with the decompiled code.

