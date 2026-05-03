# Jurassic Park - Asset Analysis

This document details the analysis of the asset loading and decompression logic for the DOS game Jurassic Park.

## Method Analysis

### `decompress_asset` (formerly `FUN_1e1c_5cb6`)

This method appears to be a decompression routine. It reads a compressed data stream, decompresses it, and writes the output to a buffer. The core logic seems to involve bitwise operations and lookups, which is characteristic of many classic compression algorithms.

Key observations:
- The function `FUN_1e1c_5dc5` is called multiple times, likely to read words from the compressed data stream.
- A value, `0x7c2b` is XORed with the values read from the stream, suggesting some form of simple encryption or obfuscation.
- The logic contains two main branches, one for handling repeated data (a form of RLE - Run-Length Encoding) and another for handling unique data.
- The decompressed data is written to a buffer pointed to by `pbVar9`.

The algorithm looks like a variation of LZ77/LZSS, a common compression technique in that era. The code reads a flag bit. If the bit is 1, it's a literal copy. If it's 0, it's a back-reference to previously decompressed data. The back-reference consists of a length and an offset.

I will now proceed with renaming the variables and the function itself to better reflect their purpose.
