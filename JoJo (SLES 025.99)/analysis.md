
### Test Data
Game files are located in 'C:\Dev\Gaming\Sony\PSX\Games\JoJo's Bizarre Adventure (Europe)'

### Misc info

Game ISO extracted to location above, has 4 folders: 

  - 'C' - Seems to hold two binary .FIN files of unknown purpose, and two .CLT clut files each of 0x200 bytes.
  - 'M' - Holds .bin files which are referenced in the game code, unsure of purpose at present.
  - 'P' - Seems to hold .PAC files, which appear to be a container format for graphics.
  - 'X' - Holds .XA audio files.

### .PAC file format

The .PAC file format appears to be a container format for graphics. Each .PAC file contains a header followed by a series of entries. The header is 0x8 bytes long and contains the following fields:
- `num_entries` (4 bytes): The number of entries in the .PAC file.
- `total size` (4 bytes): The total size of the .PAC file, including the header.
Each entry in the .PAC file is 0x8 bytes long and contains the following fields:
- (unknown) (4 bytes): The purpose of this field is currently unknown.
- (data length) (4 bytes): The length of the data associated with this entry.

Each entry starts at an 0x800 byte bounary, with the first starting at offset 0x800, and the second starting at the nearest 0x800 byte boundary after the end of the first entry's data, and so on. The data associated with each entry is of variable length, as specified by the data length field in the entry header. The data is likely to be graphics data, but the exact format of the data is currently unknown. Appears to have at least some 4bpp tile data, but that is an assumption at this point. The unknown field in the entry header may be some sort of identifier or flag.
