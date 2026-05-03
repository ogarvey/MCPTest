# Fantastic Dizzy (Sega Mega Drive) Notes

## General Notes
- The game is a platformer where players control Dizzy, an anthropomorphic egg, as he embarks on a quest to rescue his friends and defeat the evil wizard Zaks.
- Platform: Sega Mega Drive (also known as Sega Genesis in North America).
- Analyzing the game for assets and level layout
- Binary open in Ghidra for reverse engineering and asset extraction.

## Tile / Tilemap Mapping
- `LoadScreenByIndex` at `0x00005930` indexes `ScreenDescriptorTable` at `0x0003E474`.
- Each sampled screen descriptor begins with a pointer to a per-screen `IMP!` block. Example: table[0] -> descriptor `0x0003E5AE` -> background block `0x00071804`.
- Callgraph from `LoadScreenByIndex` shows the base background path as `LoadRoomTilesAndTilemap` plus `LoadCommonScreenTiles`; sibling calls after that appear to handle additional overlay or object data.
- `VDP_SetVRAMWriteAddress` at `0x00000A42` is the generic VRAM write helper.
- `FUN_00000A84` clears two VRAM regions before a screen is built:
	- `0x600` zero words through VDP command `0x6000 / 0x0003`, matching a `64 x 24 x 2` byte plane map.
	- `0x800` zero words through `0x4000 / 0x0003`, matching `0x1000` bytes of pattern data at VRAM base.
- `LoadRoomTilesAndTilemap` at `0x000053EE` takes the descriptor's first pointer in `A0`, decompresses the `IMP!` block to `0xFFFF0100`, and treats it as:
	1. `u16 tileCount`
	2. `u16 mapWidth`
	3. `u16 mapHeight`
	4. `16 x u16` palette words
	5. `mapWidth * mapHeight x u16` tilemap words
	6. `tileCount * 0x20` bytes of tile graphics
- After decompression, `LoadRoomTilesAndTilemap`:
	- uploads `tileCount` tiles from the tail of the block to VRAM at `DAT_ffff8050 << 5`
	- stores the current base tile in `DAT_ffff833E`
	- writes the tilemap to a 64-column plane at VRAM `0xC000`
	- adds the base tile index to each map word and ORs `0x2000`, so the room background uses palette line 1
	- copies the 16 palette words into RAM palette workspace at `0xFFFF9F6C`
- `LoadCommonScreenTiles` at `0x000054A0` decompresses a fixed blob from `0x00073638` and appends more `0x20` byte tiles after the room-specific tile set. This looks like a shared/common bank used across screens.
- The palette buffers are now clearer:
	- `0xFFFF9F4C..0xFFFF9FCB` is a 64-word target palette workspace covering all four CRAM lines.
	- The room block's 16 palette words land at `0xFFFF9F6C`, which is line 1 inside that target palette workspace.
	- `0xFFFF9FCC..0xFFFFA04B` is a separate 64-word live CRAM image buffer.
	- `FUN_00000814` builds the live CRAM image from the target palette workspace, and `FUN_00000860` uploads that live image to hardware CRAM.
- Descriptor follow-up fields are partially decoded:
	- longword `+0x04` usually points to an 8-byte record list consumed by `FUN_0000588C`. Each record is a pair of pointers: a packed `IMP!` chunk/tile source block and a 5-word chunk-definition list. `FUN_00001F38` expands these into chunk templates at `0xFFFFBCCA`, and `DAT_ffff805A` becomes the base chunk index used by later overlay records.
	- Some descriptors also appear to store a direct 5-word chunk-definition list at `+0x04`, using the current room block as the source tilemap instead of a separate `IMP!` source. Screen `30` (`0x0003EC90`) is the current example: `+0x04 -> 0x00040B4C` decodes cleanly as chunk definitions, not as a source/defs pointer pair list.
	- word `+0x08` selects a table-driven script via `FUN_000058CC`; it indexes a pointer table at `0xFFFF6000`, then walks records that render byte strings through `FUN_00005588`, so this field is text or label data rather than tile graphics.
	- words `+0x0A..` are a list of 3-word overlay records terminated by `0xFFFF`; `FUN_00005904` feeds them to `FUN_000021E2` as `(x, y, chunkOffset)` style records, with the third word added to `DAT_ffff805A` before indexing chunk templates at `0xFFFFBCCA`.
- The room block at `0x00071804` begins with `49 4D 50 21` (`IMP!`), confirming the first descriptor field is a compressed background asset block.

## Extraction Direction
- For static room backgrounds, start by iterating `ScreenDescriptorTable`, following descriptor field 0, and decompressing each `IMP!` block.
- Decode each 32-byte tile as Mega Drive 4bpp pattern data, render `mapWidth x mapHeight` cells, and optionally pad or crop against the engine's 64-column plane layout.
- Use the 16 palette words embedded in the room block as palette line 1 for the background. For offline extraction this is already enough to color the room tilemap correctly, even though the runtime engine composites a full 64-entry CRAM image in RAM before upload.
- For a fuller room composite, process descriptor longword `+0x04` first to build the chunk-template bank, then treat `+0x0A` as overlay placement records into that bank. The `+0x08` script remains an optional text or label pass on top.
- If a final image is missing shared HUD or overlay art, append the common tile bank loaded by `LoadCommonScreenTiles`.

## Python Prototype
- `extract_room_screens.py` in the same folder now implements a working proof-of-concept for the room background path plus the first chunk/overlay pass.
- Example command:
	- `python "Sega/MegaDrive/FantasticDizzy/extract_room_screens.py" --screens 0 30`
- Known-good sample outputs so far:
	- screen `0` -> room block `0x00071804` -> `40 x 24`, `214` tiles, `0` missing tile references, `79` chunk templates, `1` rendered overlay
	- screen `30` -> room block `0x00040EC4` -> `40 x 15`, `411` tiles, `0` missing tile references, `78` chunk templates, `0` rendered overlays
- The prototype now handles the external `0xC500` tile-graphics tail used by chunk source block `0x00056F48`, and it can build chunk templates either from `(source IMP!, defs)` pair lists or from direct 5-word definition lists rooted in the current room block.
- Screen `30` still needs follow-up interpretation on the overlay placement side: the current parse builds templates from `0x00040B4C`, but its sampled overlay record does not yet place any visible pixels in the offline render.
- The text/script pass from descriptor `+0x08` remains unimplemented.


