# Alien Trilogy (DOS) Notes

- The game uses a custom file format for graphics, which can be extracted using the `ATExtractor` tool. The extractor currently only supports uncompressed `.B16`, `.BND`, and `.BIN` files, and does not work with compressed versions of these files. See the `Extractor.cs` file for more details.
  
- Example compressed headers:
- ```
- 0000: 46 4F 52 4D 00 09 84 9C 30 30 31 32 46 30 30 30   FORM�	„œ0012F000
- 0010: 00 00 7B B0
- ```

- ```
- 0000: 46 4F 52 4D 00 09 84 9C 30 30 31 30 46 30 30 30   FORM�	„œ0010F000
- 0010: 00 00 2A AC
- ```

- Compressed headers are followed by the ID of the following chunk, `F000` is the first chunk, `F001` is the second chunk, etc. The chunk ID is followed by the compressed data size (4 bytes) and then I believe comes the compressed data.
  
- Example uncompressed headers:
- ```
- 0000: 46 4F 52 4D 00 02 05 68 50 53 58 54 49 4E 46 4F   FORM�??hPSXTINFO
- 0010: 00 00 00 10 00 01 00 01 04 00 2D 00 02 00 02 00 
- 0020: 06 00 01 00
- ```
- ```
- 0000: 46 4F 52 4D 00 02 04 64 50 53 58 54 49 4E 46 4F   FORM�??dPSXTINFO
- 0010: 00 00 00 10 00 01 00 01 08 00 02 00 02 00 02 00 
- 0020: 06 00 01 00
- ```
