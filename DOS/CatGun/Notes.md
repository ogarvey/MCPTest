# CatGun (DOS)

## 2026-05-09 - FUN_0002a630 DAT loader/parser chain

- `FUN_0002a630` is directly involved in parsing `map/*.dat` files after they are loaded into memory. It is not a generic container parser for `CATGUN.BLK` in the inspected path.
- Immediate call chain confirmed in Ghidra:
	- `FUN_0002a500(selector)` ensures the shared DAT buffer at `DAT_00060c00` exists, clears related globals, then calls `FUN_0002a630(selector)` when `selector < 6`.
	- `FUN_0002a630(selector)` builds a `map/<name>.dat` path in the string buffer at `DAT_00060c04`, loads it with `FUN_0004fd90`, then interprets the loaded bytes as a pointer-rich resource package.
	- `FUN_0004fd90` is a binary file loader in the inspected path.
	- `FUN_0002acd0(int *ptr, int base)` is a relocation helper: if `*ptr != 0`, it adds `base`.

- Selector to filename mapping recovered from direct wrappers and local disassembly:
	- `0`: formatted with `"lv%02ds%d"` at `0002a684-0002a692`, so this case generates level/stage names such as `lv01s1.dat`.
	- `1`: `intro.dat` via `FUN_00035000`.
	- `2`: `highscor.dat` via `FUN_0004a820`.
	- `3`: `charsel.dat` via `FUN_00047c10`.
	- `4`: `leader.dat` via `FUN_00036f40`.
	- `5`: `<DAT_0006558c>.dat` via `FUN_00043070`; one confirmed caller sets `DAT_0006558c = "evilbase"`, so `evilbase.dat` is one concrete case.

- Header/structure behavior inside `FUN_0002a630`:
	- Reads header fields from the loaded base at `DAT_00060c00` and copies counts/pointers into globals.
	- Rebases many offsets from header offsets `+0x1c` through `+0x68` against the DAT base.
	- Iterates several tables and rebases nested pointers with `FUN_0002acd0`.
	- Calls `FUN_000514d0` on one class of embedded records to patch handler/function-pointer-like fields based on leading magic values.
	- If byte `*(base + 0)` is `0x0d`, it performs additional fixups through `FUN_00043d30`.

- Extraction-relevant observations:
	- `DAT_000883e0` is a table of `0x28`-byte named resource entries. `FUN_0002c150(name, ...)` linearly searches this table by entry name.
	- Confirmed names looked up elsewhere after loading include `"INTROBCK"`, `"LEVELS"`, `"FONT"`, and `"LEADER"`.
	- This strongly suggests the loaded DAT contains a named internal resource directory rather than a flat bitmap stream.

- Current sample mismatch:
	- `Samples/map/intro.dat` and `Samples/map/charsel.dat` both currently begin with repeated `48 5A 52 44` (`"HZRD"`) bytes rather than the rebased header layout `FUN_0002a630` expects in memory.
	- No decompression/deobfuscation step was identified in the inspected `FUN_0002a500 -> FUN_0002a630 -> FUN_0004fd90 -> FUN_00053da7` path.
	- Based on the inspected code, the current extracted sample DATs should not yet be treated as ground truth for the in-memory structure consumed by `FUN_0002a630`.
	- Before implementing extraction against `Samples/map/*.dat`, we should identify whether the sample extraction is incomplete/incorrect or whether an earlier transform exists elsewhere in the executable.

## 2026-05-09 - Downstream DAT consumers and the `0xF9C0` observation

- No inspected code uses a hard-coded `0xF9C0` offset. The game reaches DAT payloads indirectly through header-rebased pointers and named resource entries populated by `FUN_0002a630`.

- The main dispatcher for loaded DAT content is `FUN_000110a0()`:
	- It switches on `DAT_0008849b`, the first byte of the loaded DAT header.
	- For type `8`, it calls `FUN_0002c470()` and `FUN_00020fc0()`.
	- For other types, it dispatches to specialized setup routines such as `FUN_00016120()` (`"CLOUD_LAYER"`) and other screen/game-state handlers.

- Level/map usage path:
	- `FUN_0002a630()` builds three layer descriptors from the structure copied out of `base + *(base + 0x58)`. It stores per-layer dimensions in `DAT_00065398`/`DAT_0006539c` and per-layer cell arrays in `DAT_000655a4[]`.
	- `FUN_0002c470()` walks those layer cell arrays and resolves 7-byte resource references from `DAT_000883e8`. It searches for the named resource `"PLAYER"` via `DAT_000883e0` and uses that to initialize the player/start state.
	- `FUN_000110a0()` later scans nearby cells from the same `DAT_000655a4[]` layer arrays and calls `FUN_0002c360()` on visible/in-range entries.
	- `FUN_0002c360()` resolves each cell reference to a named entry in `DAT_000883e0`, then dispatches setup handlers based on the resolved resource name.
	- `FUN_0002ca90()` is one concrete downstream consumer of those named entries; it looks up `"PLAYER"`, `"PSTARS"`, `"PILOT"`, and `"CHARGE"` and uses them to initialize scene objects.

- Scene/object usage path:
	- When the loaded DAT header byte is `0x0d`, `FUN_00043d30()` performs extra fixups and exposes additional tables from offsets `+0x6c` through `+0x90`.
	- `FUN_00043d30()` populates `DAT_00094a24`/`DAT_00094a28[]` with DAT-defined object pointers and patches their method tables.
	- `FUN_00042580()` iterates those object arrays and calls their methods, so this is a direct execution path driven by DAT-defined object records.
	- `FUN_000430d0()` consumes `DAT_00094d4c` as a text/script block, reading lines until newline or NUL and processing them through the scene handler.

- Palette section usage:
	- `DAT_0006557c = base + *(base + 0x54)` is used like a banked palette area, not like raw bitmap/tile payload.
	- Evidence:
	  - Several callers step through it in `0x300`-byte units, which matches `256 * 3` RGB palette bytes.
	  - `FUN_0003b000(index, ...)` calls `FUN_0003dd90(DAT_0006557c + index * 0x300, ...)`.
	  - `FUN_0003da80()` also selects banks `DAT_0006557c + ((event - 0x0b) * 0x300)`.
	  - `FUN_0003dd90()` computes byte deltas against the current RGB state; it is a palette transition/interpolation helper.
	  - `FUN_00020fc0()` copies palette blocks around inside `DAT_0006557c`, again in `0x300`-aligned units.

- Named resource table usage outside the level path:
	- `FUN_00035000()` looks up `"INTROBCK"`, `"LEVELS"`, and `"FONT"` after loading `intro.dat`.
	- `FUN_00036f40()` looks up `"LEADER"` after loading `leader.dat`.
	- `FUN_0004ab90()` looks up `"DIFONT1"` and `"RBFONT1"` after loading `highscor.dat`.
	- `FUN_00016120()` looks up `"CLOUD_LAYER"`.

- Raw sample corroboration:
	- The extracted `intro.dat` and `charsel.dat` both stop showing the repeated `HZRD` lead-in at `0xF9C0`; bytes at that point look like plausible opaque payload rather than placeholder/header text.
	- A raw string scan of `intro.dat` found `INTROBCK` at `0x12D83F`, `LEVELS` at `0x12D86D`, and `FONT` at `0x12D874`.
	- This supports a split between opaque payload regions and a separate on-disk resource-name directory, but the inspected code still reaches both through rebased pointers rather than by seeking to a fixed literal offset like `0xF9C0`.

## 2026-05-09 - Resource table follow-up

- `FUN_0002c360()` resolves level cell references in two stages:
	- First it walks a static name-handler table at `0x0003AF3C`.
	- Then it walks a second table selected by `DAT_0006027c` through the pointer array at `0x00060C0C`.

- Names confirmed in the first static table at `0x0003AF3C` include:
	- `PLT0`, `PLT1`, `PLT2`, `PLT3`
	- `PLT0_PICKUP`, `PLT1_PICKUP`, `PLT2_PICKUP`, `PLT3_PICKUP`
	- `SCROLL_BORDER`, `KILL`, `END`
	- `SPEED_POWERUP`, `CYCLE_POWERUP`, `FIRE_POWERUP`, `PODS_POWERUP`
	- The associated handler entrypoints point into code near `0x0003B000` and `0x0003B1A0`.

- Code-side interpretation of some first-stage handlers:
	- `FUN_0003B000()` operates on `DAT_0006557C + index * 0x300`, confirming the `PLT*` resources are palette-bank selectors/transitions rather than sprite or tile payload.
	- `FUN_0003B1A0()` triggers a generic state/effect path and is used by some non-palette resource handlers from the same table.

- Names confirmed in one second-stage table (pointer `0x00013F38`, selected through `0x00060C0C`) include:
	- `PIPE_DRIP`, `PIPE_LEAK_HOR`, `PIPE_LEAK_VER`
	- `TEXTURE`, `SLIMER`
	- `STEEL_DRIP0`, `STEEL_DRIP1`
	- plus additional level-object names such as `BBREACTOR` and `1BOX4`.

- These second-stage names are present in the raw level sample `Samples/map/lv01s1.dat`:
	- `PIPE_DRIP` at `0x10703B`
	- `PIPE_LEAK_HOR` at `0x107045`
	- `PIPE_LEAK_VER` at `0x107053`
	- `SLIMER` at `0x1070D1`
	- `STEEL_DRIP0` at `0x1070D8`
	- `STEEL_DRIP1` at `0x1070E4`
	- `TEXTURE` at `0x107261`
	- This confirms that the per-level dispatch names recovered from code really do come from the level DAT resource directory.

- `INTROBCK` entry follow-up:
	- `FUN_00035000()` resolves the `INTROBCK` named entry from `DAT_000883E0`.
	- It reads `*(entry + 4)` as a pointer to a secondary structure.
	- From that secondary structure, it stores:
	  - `*(ptr + 0x24)` into `DAT_00092764`
	  - `*(ptr + 0x54)` into `DAT_00092734`
	- `FUN_00036A00()` consumes `DAT_00092734` as a `short *` and reads four `0x400`-element planes from it (`[0]`, `[0x400]`, `[0x800]`, `[0xC00]`), then derives screen/world coordinates from those planes.
	- An unrecognized code block beginning at `0x000357E0` reads `DAT_00092764`, `DAT_00092768`, `DAT_0009276C`, and `DAT_00092770`. This strongly indicates that `INTROBCK` points to a structured background resource with at least one geometry/coordinate block and one additional data block, but the exact semantics of the `DAT_00092764` block are still unresolved.

- Additional raw-file corroboration:
	- `Samples/map/lv01s1.dat` also changes character at `0xF9C0`; unlike the initial `HZRD` lead-in, bytes there look like structured binary/payload data.
	- `Samples/map/intro.dat` contains a readable resource-name pool around `0x12D82B` (`HMUSIC/MAINTHEM.WAV`, `INTROBCK`, `TITLE`, `VICTORY`, `CATSIGN`, `PLINGY`, `OPTIONS`, `LEVELS`, `FONT`, ...).
	- `Samples/map/lv01s1.dat` contains a readable resource-name pool around `0x106F40` (`PLAYER`, `CYCLE_POWERUP`, `FIRE_POWERUP`, `PODS_POWERUP`, `PIPE_DRIP`, `PIPE_LEAK_*`, `SLIMER`, `TEXTURE`, ...).

- Current limit:
	- The raw samples clearly contain the same resource names recovered from code, but I have not yet found a simple file-absolute pointer table or a simple 32-bit relative-offset scheme that directly reconstructs the in-memory `0x28`-byte `DAT_000883E0` entry layout from the extracted files.
	- So the resource-name pools are now confirmed on disk, but the exact raw directory record format remains unresolved.

## 2026-05-09 - Ordered follow-up: one concrete level handler and raw-directory tests

- Level-1 second-stage dispatch table (`0x00013F38`) can now be mapped to concrete names and handlers. Confirmed pairs include:
	- `PIPE_DRIP` -> `0x00014190`
	- `PIPE_LEAK_HOR` -> `0x000141F0`
	- `PIPE_LEAK_VER` -> `0x00014200`
	- `BUBBLEZ_HOR0R` -> `0x00014300`
	- `BUBBLEZ_HOR1R` -> `0x00014310`
	- `BUBBLEZ_HOR0L` -> `0x00014300`
	- `BUBBLEZ_HOR1L` -> `0x00014310`
	- `BUBBLEZ_VER0U` -> `0x00014440`
	- `BUBBLEZ_VER1U` -> `0x00014450`
	- `BUBBLEZ_VER0D` -> `0x00014440`
	- `BUBBLEZ_VER1D` -> `0x00014450`
	- `SLIMER` -> `0x000144D0`
	- `STEEL_DRIP0` -> `0x00014710`
	- `STEEL_DRIP1` -> `0x00014720`
	- `BBREACTOR` -> `0x000149B0`

- Concrete classification: `SLIMER` is object/entity code, not tile/background data.
	- The handler stub at `0x000144D0`:
	  - reads the current 7-byte cell reference through `DAT_000884A4`
	  - uses byte `+4` from that 7-byte reference as a variant/type selector
	  - calls `FUN_000136E0(0x14590, ...)` to create/start a runtime object/state block
	  - immediately calls `FUN_0002C2C0()` on that object, which is the generic entity initializer that binds the current cell/resource entry from `DAT_000883E0`
	  - then adjusts object state/position fields (including `+0x14`, `+0x18`, `+0x48`, `+0x78`, `+0x79`, `+0x80`)
	- This is sufficient to classify `SLIMER` as a spawned gameplay object/actor path.

- Important negative result: `TEXTURE` is present in the raw `lv01s1.dat` name pool, but it does not appear in the level-1 second-stage dispatch table at `0x00013F38` before that table terminates. So `TEXTURE` is not handled by this specific `FUN_0002C360()` dispatch table for level 1; it likely enters through a different lookup path.

- Raw-directory hypothesis tests against `lv01s1.dat`:
	- I treated the bytes immediately before the readable name pool (`0x106800` through `0x106F40`) as a candidate compact directory region.
	- I tested whether that region contains direct 16-bit or 32-bit relative offsets to known names using the readable pool near `0x106F40` (`PIPE_DRIP`, `PIPE_LEAK_HOR`, `PIPE_LEAK_VER`, `SLIMER`, `TEXTURE`).
	- I also tested whether the same region contains simple 16-bit low-file-offset references to those names.
	- All of those searches returned no matches.
	- So the immediate pre-pool bytes in `lv01s1.dat` do not behave like a simple compact directory storing direct name offsets.

- Additional interpretation from those tests:
	- The raw level sample still clearly contains the same level resource names in the same broad order as the code-side dispatch table (`PIPE_DRIP`, `PIPE_LEAK_*`, `BUBBLEZ_*`, `SLIMER`, `STEEL_DRIP*`).
	- However, the exact raw record format that becomes the rebased in-memory `DAT_000883E0` entries is still not visible as a simple pointer table in the extracted file.
	- This strengthens the earlier conclusion that the extracted samples are not exposing the in-memory `0x28`-byte entry table in a direct, trivially reconstructible on-disk form.

## 2026-05-09 - Archive materialization path under `FUN_0004fd90`

- Startup/archive mounting path:
	- `FUN_00037640()` attempts to mount `GHX:/CATGUN.BLK` first, falls back to `CATGUN.BLK` if needed, then immediately reads `X:/POWER.CFG` via `FUN_00037700()`.
	- This establishes that the executable exposes archive contents through a virtual filesystem layer rather than treating `map/*.dat` as guaranteed loose files on disk.

- `CATGUN.BLK` directory materialization:
	- `FUN_0004ff50(path)` opens the archive with `FUN_000539e8()` and stores the handle in `DAT_0006146C`.
	- It reads an 8-byte archive header into `DAT_0009FF4C` / `DAT_0009FF50`, then allocates `DAT_0009FF50 - 8` bytes and reads the remaining directory blob into `DAT_0009FF54`.
	- `FUN_0004FEB0(path)` treats `DAT_0009FF4C` as an entry count and walks `DAT_0009FF54` in `0x30`-byte records.
	- Within each `0x30`-byte record, the code compares the request path against the record name at `+0x00`, then uses `dword +0x28` as the file offset and `dword +0x2C` as the file size.

- Actual `.dat` load behavior:
	- `FUN_0004FD90(path, buffer_or_size)` calls `FUN_0004FEB0(path)` first.
	- On an archive hit, `FUN_0004FEB0()` seeks the already-open `CATGUN.BLK` handle to the entry offset with `FUN_0005574C(..., whence=0)` and returns the entry size.
	- `FUN_0004FD90()` then allocates exactly that many bytes when needed and copies the entry body with `FUN_00053DA7(dst, size, DAT_0006146C)`.
	- `FUN_00053DA7()` is buffered file I/O only; its large-copy path bottoms out in `FUN_0005876F()`, which is DOS `int 21h` read. Seeking bottoms out in `FUN_000586FC()`, which is DOS `int 21h` lseek.
	- No decompression, deobfuscation, or wrapper stripping appears anywhere in this inspected archive-hit path.

- Extraction implication:
	- The current runtime path for `map/*.dat` is: resolve BLK directory entry -> seek to entry offset -> raw read `entry_size` bytes -> hand that buffer to `FUN_0002A630()`.
	- So the mismatch between `FUN_0002A630()`'s in-memory expectations and the current `Samples/map/*.dat` files is not explained by a hidden transform inside `FUN_0004FD90()` / `FUN_00053DA7()`.
	- The remaining grounded possibilities are narrower:
	  - the current extracted samples are not the exact raw BLK entry bytes the game reads, or
	  - the BLK directory points directly at an inner payload offset/length while the current sample extraction preserved an outer wrapper layer.

## 2026-05-09 - Direct validation against `Samples/CATGUN.BLK`

- The recovered BLK layout is now confirmed from the real archive bytes in `Samples/CATGUN.BLK`:
	- Header dword `0x00` = `0x21` (`33`) entries.
	- Header dword `0x04` = `0x640`, which matches the first data offset rather than the raw unaligned directory byte count.
	- `33 * 0x30 + 8 = 0x638`, so the directory region is padded/aligned up to `0x640` before file data starts.

- The parsed `0x30`-byte records match the code-derived field layout exactly:
	- bytes `+0x00..+0x27`: NUL-terminated path/name string
	- dword `+0x28`: file offset
	- dword `+0x2C`: file size
	- Example entries recovered directly from the archive:
	  - `edfont.lbm` -> offset `0x640`, size `0x370A`
	  - `dialtest.rsc` -> offset `0x3D60`, size `0x2DD0`
	  - `map/intro.dat` -> offset `0x27C840`, size `0x144C9D`
	  - `map/lv01s1.dat` -> offset `0x4C7660`, size `0x1DDBC5`

- Crucial result: the extracted sample files are not raw BLK entry bytes.
	- For representative files, archive slice sizes match the extracted file sizes exactly, but the bytes do not.
	- Direct comparisons:
	  - Raw BLK `edfont.lbm` starts with `FORM 00 00 37 02 PBM BMHD`, while extracted `Samples/edfont.lbm` starts with unrelated non-IFF bytes.
	  - Raw BLK `map/intro.dat` starts with `02 00 00 00 00 00 00 00 00 03 00 00 ...`, while extracted `Samples/map/intro.dat` starts with repeated `HZRD`.
	  - Raw BLK `map/charsel.dat` and `map/lv01s1.dat` show the same pattern: raw BLK bytes look like the runtime-consumed structures, extracted files start with repeated `HZRD` blocks.

- Additional discriminating check on `map/intro.dat`:
	- The extracted `HZRD` opening sequence first appears at offset `0x9C` inside the raw BLK entry.
	- The extracted file is not a simple rotation of the raw entry around that offset.
	- So the prior extraction was not a byte-for-byte BLK unpack; it performed some additional transformation or selected a derived view of the data.

- Updated conclusion:
	- Ghidra and the real BLK both agree on the runtime source of truth: `FUN_0002A630()` receives the raw BLK entry bytes, not the currently extracted `HZRD`-prefixed files.
	- For extraction work, we should treat `Samples/CATGUN.BLK` as canonical and use the loose extracted files only as secondary artifacts that may represent a transformed/debug-oriented view.

## 2026-05-09 - First-pass raw DAT parser in DogKnife

- `DogKnife` now contains a first-pass raw DAT parser in addition to the BLK extractor.
	- Entry point: `dotnet run --project DOS/CatGun/DogKnife/DogKnife.csproj -- --dump-dat <path-to-raw-dat>`
	- Current parser scope is intentionally conservative: it only uses fields that were directly validated against `FUN_0002A630()` and real raw DAT bytes.

- Header fields now parsed directly from disk, matching the Ghidra loader logic:
	- byte `0x00`: DAT type (`DAT_0008849B` in runtime)
	- byte `0x01`: DAT variant/subtype (`DAT_0008849A` in runtime)
	- dword `0x04`: copied to `DAT_000883B8`
	- bytes `0x08..0x0C`: copied to `DAT_000655BD`, `DAT_000655BC`, `DAT_000655BF`, `DAT_000655BB`, `DAT_000655BE`
	- dword `0x10`: count for the `0x30`-byte table at offset `0x40`
	- dword `0x14`: resource-entry count for the `0x28`-byte table at offset `0x4C`
	- dword `0x18`: count for the `0x0D`-byte table at offset `0x64`
	- dwords `0x1C..0x68`: raw on-disk offsets used by `FUN_0002A630()` for the rebased tables and blocks it exposes globally.

- Resource-entry table parsing is now confirmed from real files:
	- Header `+0x4C` points to the named resource table.
	- Each resource entry is `0x28` bytes.
	- The first dword in each entry is a string offset; rebasing that field yields the expected resource names.
	- The parser also exposes the additional rebased pointer fields at `+0x04`, `+0x08`, `+0x0C`, `+0x14`, and `+0x18` without assigning unverified semantics to them yet.

- Validation results from DogKnife:
	- Raw `intro.dat` parses as type `0x02`, resource count `12`, resource table offset `0x130C74`.
	- The parsed intro resource names include `INTROBCK`, `TITLE`, `VICTORY`, `CATSIGN`, `PLINGY`, `OPTIONS`, `LEVELS`, `FONT`, `LETTERS`, `MAIN_MENU`, `OPTIONS_MENU`, and `DISPLAY`.
	- Raw `lv01s1.dat` parses as type `0x0B`, variant `0x63`, resource count `58`, resource table offset `0x11510C`.
	- The parsed level resource names include `PLAYER`, `PIPE_DRIP`, `PIPE_LEAK_HOR`, `PIPE_LEAK_VER`, `SLIMER`, `STEEL_DRIP0`, `STEEL_DRIP1`, `TEXTURE`, `GENERAL_BULLET`, and others already corroborated from code and strings.

- Current parser limit:
	- This pass stops at the top-level header and named resource directory.
	- It does not yet decode the table at `0x1C`, the `0x30`-byte records at `0x40`, the patch/palette/layer blocks, or the per-resource payload formats behind entry field `+0x04`.
	- Those should be the next expansion points now that the raw DAT header and resource directory are grounded in code and tooling.

## 2026-05-10 - DAT parser expansion: `0x1C` reference table and `0x58` layer block

- `DogKnife` now parses the `0x1C` cell-reference table and the `0x58` layer block in addition to the top-level header and named resource directory.
	- The implementation remains conservative: it only exposes raw fields and code-validated relationships.

- `0x1C` table findings, now grounded in both code and raw `lv01s1.dat` bytes:
	- `FUN_0002C360()` and `FUN_0002C470()` use the low 16 bits of each 4-byte layer cell as an index into `DAT_000883E8`.
	- Each reference-table entry is `7` bytes.
	- Byte `+5` of each `7`-byte entry selects the named resource entry in `DAT_000883E0`.
	- In raw `lv01s1.dat`, the `0x1C` table starts at `0x94` and the next block begins at `0x528`, yielding `167` full `7`-byte entries plus `3` trailing bytes before the next offset.
	- The maximum low-16-bit cell reference index actually used by layer 0 is `166`, which exactly fits those `167` entries.
	- Example resolved raw entries from `lv01s1.dat`:
	  - entry `0`: `00-00-00-00-00-00-00` -> resource index `0` -> `PLAYER`
	  - entry `2`: `13-00-F9-00-00-05-01` -> resource index `5` -> `PAW`
	  - entry `10`: `1E-00-F4-00-00-05-01` -> resource index `5` -> `PAW`

- `0x58` layer-block findings, now grounded in both `FUN_0002A630()` and raw `lv01s1.dat` bytes:
	- The layer block consists of exactly `3` consecutive layer records.
	- Each layer record is:
	  - a `0x10`-byte descriptor
	  - immediately followed by `width * height * 4` bytes of cell data
	- The last two dwords of each `0x10`-byte descriptor are the layer width and height.
	- For `lv01s1.dat`, all three layer descriptors have:
	  - `dword +0x00 = 0`
	  - `dword +0x04 = 0`
	  - `dword +0x08 = 120`
	  - `dword +0x0C = 320`
	- That matches the `FUN_0002A630()` load logic, which copies `3` descriptors, reads width/height from offsets `+8` and `+0xC`, then advances by `0x10 + width * height * 4` for each layer.

- Validation from the updated DogKnife parser on raw `lv01s1.dat`:
	- Layer 0: descriptor `0x118ADC`, cell data `0x118AEC`, size `120 x 320`, `106` non-zero cells, max reference index `166`
	- Layer 1: descriptor `0x13E2EC`, cell data `0x13E2FC`, size `120 x 320`, `39` non-zero cells, max reference index `153`
	- Layer 2: descriptor `0x163AFC`, cell data `0x163B0C`, size `120 x 320`, `21` non-zero cells, max reference index `159`

- Updated practical state:
	- We can now parse the top-level DAT header, named resource directory, cell-reference table, and full layer grids directly from raw BLK-extracted DATs.
	- The next unresolved step is still the per-resource payload structure behind each resource entry’s `+0x04` field, since that is the most likely route to actual graphics/material extraction.

## 2026-05-10 - DAT parser expansion: shared payload groups behind resource entry `+0x04`

- `DogKnife` now parses the raw data behind resource entry field `+0x04` as shared payload groups.
	- The parser does not assign speculative field names yet.
	- It groups resources by their unique non-zero `+0x04` pointer, bounds each group by the next higher known pointer in the DAT, and exposes the payload as a sequence of `0x30`-byte blocks.

- This grouping is now strongly validated on raw `lv01s1.dat`:
	- Every observed non-zero `+0x04` payload region in the level DAT partitions cleanly into a whole-number count of `0x30`-byte blocks.
	- Related resources frequently share the same `+0x04` pointer, which means the game is reusing one payload block array across multiple named resources.
	- Confirmed examples:
	  - `PIPE_LEAK_HOR` and `PIPE_LEAK_VER` share payload start `0x108ECC`.
	  - `BUBBLEZ_HOR0R`, `BUBBLEZ_HOR1R`, `BUBBLEZ_HOR0L`, and `BUBBLEZ_HOR1L` share payload start `0x10913C`.
	  - `BOX0` through `BOX4` share payload start `0x10A8AC`.
	  - `BRIDGE_*` color/direction variants share payload start `0x10A9CC`.

- Representative validated payload-group sizes from raw `lv01s1.dat`:
	- `PLAYER`: `0x570` bytes -> `29` blocks
	- `PAW`: `0x2A0` bytes -> `14` blocks
	- `PIPE_DRIP`: `0x2A0` bytes -> `14` blocks
	- `SLIMER`: `0xBA0` bytes -> `62` blocks
	- `TEXTURE`: `0x90` bytes -> `3` blocks
	- `BLOBS`: `0x1F80` bytes -> `168` blocks

- The same `0x30`-byte grouping also holds on raw `intro.dat`:
	- `INTROBCK`: `0x60` bytes -> `2` blocks
	- `TITLE`: `0x30` bytes -> `1` block
	- `DISPLAY`: `0xC00` bytes -> `64` blocks
	- `FONT`: `0x360` bytes -> `18` blocks

- Important code/byte correlation for `INTROBCK`:
	- `FUN_00035000()` reads `INTROBCK`'s resource-entry `+0x04` pointer, then consumes offsets `+0x24` and `+0x54` from that payload.
	- Raw `intro.dat` now parses `INTROBCK`'s `+0x04` payload as exactly two `0x30`-byte blocks.
	- That means `+0x24` lands inside block 0 and `+0x54` lands inside block 1, which is consistent with the code reading two separate substructures from one shared payload family rather than from a bespoke one-off layout.

- Current interpretation limit:
	- We can now recover the full `+0x04` payload regions and their `0x30`-byte block structure.
	- We have not yet assigned stable semantics to the individual block fields beyond their raw positions.
	- The most likely adjacent next step is resource entry `+0x08`, because:
	  - `FUN_0002C2C0()` copies resource entry `+0x08` into object state,
	  - the raw bytes at those `+0x08` pointers look like compact byte-coded sequence tables with `0xFF` delimiters,
	  - and many resources that share a `+0x04` payload still use different `+0x08` pointers, suggesting per-resource animation/sequence selection over shared frame data.

## 2026-05-10 - DAT parser expansion: shared sequence groups behind resource entry `+0x08`

- `DogKnife` now parses the raw data behind resource entry field `+0x08` as shared sequence groups.
	- The parser still avoids speculative names.
	- It groups resources by their unique non-zero `+0x08` pointer, bounds each group by the next higher known pointer in the DAT, then parses the active sequence bytes up to the last `0xFF` terminator.
	- Any bytes after the last `0xFF` and before the next structural boundary are now reported as trailing spill, not as active sequence data.

- This structure is strongly validated on raw `lv01s1.dat`:
	- Every observed non-zero `+0x08` group is short and `0xFF`-terminated.
	- Many named resources share the same `+0x08` pointer, just as they share `+0x04` payload groups.
	- Representative examples:
	  - `CYCLE_POWERUP`, `FIRE_POWERUP`, `PODS_POWERUP`, `SHIELD_POWERUP` share `0x114FBC` -> `00 01 02 03 04 04 03 02 01 00 FF`
	  - `PAW` uses `0x114FC7` -> `00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D FF`
	  - `BUBBLEZ_HOR0R` / `BUBBLEZ_HOR1R` use ascending evens `00 02 04 ... 18 FF`
	  - `BUBBLEZ_HOR0L` / `BUBBLEZ_HOR1L` use the reverse `18 16 14 ... 00 FF`
	  - `TEXTURE` uses `00 00 01 01 02 02 01 01 FF`

- Intro/menu resources also fit the same parser surface:
	- `VICTORY` -> `00 01 02 ... 13 FF`
	- `CATSIGN` -> `00 01 02 03 04 05 06 07 06 05 04 03 02 01 FF`
	- `PLINGY` -> active bytes `00 01 02 03 04 05 06 07 08 07 06 05 04 03 02 01 00 FF`, plus `2` trailing bytes (`48 5A`) before the next structural boundary.
	- That `PLINGY` case is the grounded reason the parser now trims active data to the last `0xFF` terminator and reports trailing spill separately.

- Crucial correlation toward graphics export:
	- For raw `lv01s1.dat`, the active bytes from every parsed `+0x08` sequence group stay within the block count of the corresponding resource's `+0x04` payload group.
	- Representative validated pairings:
	  - `PAW`: `14` payload blocks, sequence values `0x00..0x0D`
	  - `PIPE_DRIP`: `14` payload blocks, sequence values `0x00..0x0D`
	  - `BUBBLEZ_HOR0R`: `26` payload blocks, sequence values `0x00..0x18` stepping by `2`
	  - `STEEL_DRIP1`: `24` payload blocks, sequence values `0x0C..0x17`
	  - `TEXTURE`: `3` payload blocks, sequence values `00 00 01 01 02 02 01 01`

- Updated interpretation:
	- Resource entry `+0x04` gives a shared array of `0x30`-byte payload blocks.
	- Resource entry `+0x08` gives a shared, compact selector/order table over those blocks.
	- The `+0x08` bytes now look much more like frame/block indices than like image data or arbitrary script bytes.

- Next export-focused step:
	- Decode the actual pixel/shape data referenced by one `0x30` block family, then use the parsed `+0x08` selector tables as frame order.
	- `TEXTURE`, `PAW`, or another small repeated family is now a reasonable first target because we can already recover both the available blocks and the display order.

## 2026-05-10 - First `TEXTURE` graphic export probe

- `DogKnife` now has a first narrow export command for the `TEXTURE` family:
	- `dotnet run --project DOS/CatGun/DogKnife/DogKnife.csproj -- --export-textures <path-to-raw-dat>`
	- Default output root is `DOS/CatGun/TestOutput/DatExports/<dat-name>/TEXTURE`

- This exporter is intentionally limited to the currently grounded `TEXTURE` slice:
	- It finds the `TEXTURE` resource entry.
	- It resolves the shared payload group behind resource entry `+0x04`.
	- It resolves the shared sequence group behind resource entry `+0x08`.
	- It interprets each `0x30` payload block using only the fields that are now strongly supported by file structure:
	  - `+0x08` = width
	  - `+0x0C` = height
	  - `+0x24` = raw indexed pixel data offset

- Grounded evidence behind that interpretation for raw `lv01s1.dat`:
	- `TEXTURE` payload block count = `3`
	- All three blocks report `64 x 128`
	- Their data offsets are `0xC441C`, `0xC641C`, and `0xC841C`
	- Each successive data offset advances by `0x2000`, which exactly matches `64 * 128 = 8192 = 0x2000` bytes
	- The parsed `TEXTURE` sequence bytes are `00 00 01 01 02 02 01 01`, and all values fit within the `3` available payload blocks

- Palette handling for this first probe:
	- The raw palette region in `lv01s1.dat` spans `0x116CDC..0x118ADC`, length `0x1E00`
	- `0x1E00 / 0x300 = 10`, so the exporter treats this as `10` palette banks of `256 * RGB` triplets
	- Palette bytes are VGA-style `0..0x3F` components and are expanded to `0..255` for PNG output
	- For `TEXTURE`, block field `+0x20` is:
	  - block 0: `0x00030004`
	  - block 1: `0x00030104`
	  - block 2: `0x00030204`
	- The exporter currently uses byte 2 of that field (`3`) as the best current palette-bank probe, because it is shared across all three `TEXTURE` blocks while byte 1 tracks the block/frame id (`0`, `1`, `2`)
	- Important limit: that palette-bank interpretation is still a probe, not yet code-proven from a consumer function

- Actual export results now produced from raw `lv01s1.dat`:
	- Output root: `TestOutput/DatExports/lv01s1/TEXTURE`
	- `grayscale/blocks`: `3` PNGs
	- `grayscale/frames`: `8` PNGs following sequence order `00 00 01 01 02 02 01 01`
	- `palette_bank_03/blocks`: `3` PNGs
	- `palette_bank_03/frames`: `8` PNGs
	- `metadata.txt` records the parsed block sizes, data offsets, frame order, palette-bank count, and chosen palette-bank probe

- Practical outcome:
	- We now have the first end-to-end graphics export path from raw CatGun DAT data to PNG output.
	- The current exporter is limited to the `TEXTURE` family, but it is grounded on validated structural relationships rather than on guessed generic sprite semantics.
	- The next likely extension is either:
	  - confirm the palette-bank field from code and keep refining `TEXTURE`, or
	  - move to a second `0x30`-block family such as `PAW`, reusing the now-validated sequence-table machinery.

## 2026-05-10 - Generic raw-plane exporter and `PAW` probe export

- `DogKnife` now has a generic raw-plane export command in addition to the dedicated `TEXTURE` probe path:
	- `dotnet run --project DOS/CatGun/DogKnife/DogKnife.csproj -- --export-resource-planes <path-to-raw-dat> --resource <name>`

- This command reuses the currently grounded export assumptions only for resource families whose parsed `0x30` blocks already provide all of the following:
	- positive width at `+0x08`
	- positive height at `+0x0C`
	- in-file pixel-data pointer at `+0x24`
	- optional frame order from resource entry `+0x08`

- First use beyond `TEXTURE`: raw `lv01s1.dat` resource `PAW`
	- Output root: `TestOutput/DatExports/lv01s1/PAW`
	- Export completed successfully.
	- Artifact layout:
	  - `grayscale/blocks`
	  - `grayscale/frames`
	  - `palette_bank_02/blocks`
	  - `palette_bank_02/frames`
	  - `metadata.txt`

- Grounded `PAW` structure now recovered by the exporter:
	- Payload group: `0x1084DC..0x10877C`
	- Block count: `14`
	- Sequence group: `0x114FC7..0x114FD6`
	- Frame order: `00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D`
	- Every exported block currently parses as `23 x 19`
	- Example block data offsets:
	  - block 0 -> `0x18034`
	  - block 1 -> `0x1825C`
	  - block 13 -> `0x196E4`

- `PAW` compared with `TEXTURE`:
	- `TEXTURE` still has the cleaner proof because its `+0x24` data pointers advance by exactly `width * height` (`0x2000`) per block.
	- `PAW` does not have that same simple fixed-step spacing, but each block still has a valid in-file `+0x24` plane of exactly `23 * 19 = 437` bytes, and the parsed sequence table cleanly indexes all `14` blocks.
	- That is enough to treat `PAW` as a second working raw-plane export probe rather than as a one-off `TEXTURE` exception.

- Palette status remains the same:
	- The engine clearly uses palette banks as `bank * 0x300 + DAT_0006557C` slices (e.g. `FUN_00049640()`, `FUN_0003B000()`, `FUN_0003DD90()`).
	- We still have not code-proven which parsed block field supplies the bank index for `TEXTURE`/`PAW` object draws.
	- The current exporter therefore still marks palette output as a probe and records the chosen candidate bank in metadata.

- Updated practical state:
	- We now have two working export probes from raw DAT data to PNGs:
	  - `TEXTURE`
	  - `PAW`
	- The next valuable code-driven step is still to prove the palette-bank source in Ghidra, so the palette choice stops being heuristic.

## 2026-05-10 - `PAW` export correction from runtime code

- The prior `PAW` raw-plane probe should now be treated as invalid.

- Ghidra-backed runtime path:
	- `FUN_0003D030()` resolves the named `PAW` resource and binds callback `LAB_0003B6D0`.
	- `LAB_0003B6D0` funnels through `FUN_00013040()` when the effect reaches its frame-staging path.
	- `FUN_00013040()` does not read `block + 0x24` as a `width * height` byte plane. It stages the current frame's `+0x24` pointer into a work item and stores the destination separately.
	- The queue processors `FUN_00012EA0()` / `FUN_00012FE0()` later consume those staged work items; for the type used by `PAW`, the staged `+0x24` pointer is treated as executable/code-driven blit payload rather than as raw indexed image bytes.

- Concrete disassembly-grounded reason the exporter was wrong:
	- `FUN_00013040()` stores the current frame payload pointer from `(*(obj->resource + 4) + frame * 0x30 + 0x24)` into the queued work item.
	- The worker path later dispatches through that staged field as code/input, which means `PAW` block payloads are on a decode/blit path that is fundamentally different from the raw `TEXTURE` probe path.
	- This also matches the observed first `PAW` payload bytes at `0x18034`: `50 83 E0 03 5F FF 14 85 ...`, which look like executable x86-style code, not a validated 8-bit pixel plane.

- Updated status:
	- `TEXTURE` remains only a visually successful probe pending full code-proof of its draw path and palette-bank source.
	- `PAW` must no longer be treated as a working raw-plane export family.
	- `DogKnife`'s generic `--export-resource-planes` path should be considered disabled until a family-specific decoder is proven from Ghidra.

## 2026-05-10 - Shared sprite/decode queue recovery

- The per-frame graphics work queue is now partially recovered from the allocator/worker path around `FUN_00012D40()`, `FUN_00012EA0()`, and `FUN_00012FE0()`.

- Queue record shape recovered from staging/worker code:
	- record size: `0x18` bytes
	- `+0x00`: owner/context pointer
	- `+0x04`: payload pointer or payload descriptor pointer
	- `+0x08`: destination surface pointer
	- `+0x0C`: auxiliary decoder field (used by some record types)
	- `+0x14`: linked-list/next index in the general queue
	- `+0x16`: record type

- Type `1` record path:
	- staged by `FUN_00013040()`
	- `record[+0x04] = current_block->+0x24`
	- `record[+0x08] = destination pointer in the `0x180`-pitch surface`
	- consumed by `FUN_00012EA0()` / `FUN_00012FE0()` as:
	  - `EAX = record[+0x08]`
	  - `CALL record[+0x04]`
	- This proves the `+0x24` payload for this family is executable blit code, not a raw byte plane.
	- `FUN_0003BDC0()` is now confirmed as a generic wrapper for this path: it runs the shared update/visibility checks (`FUN_0003FBF0()`, `FUN_00040360()`) and then falls through to `FUN_00013040()`.
	- `FUN_0003BDC0()` has many parameter/xref users across the program, so this executable-payload path is shared beyond `PAW` rather than being a one-off special case.
	- Raw `lv01s1.dat` examples such as `PAW`, `NITRO_POWERUP`, and `1UP` block-0 payloads all begin `50 83 E0 03 5F FF 14 85 ...`, which matches this executable-payload interpretation.

- Type `3` record path is the first shared non-code decoder:
	- staged by `FUN_00013100()` and `FUN_00013430()`
	- `record[+0x04] = current_block->+0x24`
	- `record[+0x08] = destination pointer`
	- `record[+0x0C] = current_block->+0x28`
	- consumed by `FUN_00012EA0()` / `FUN_00012FE0()` via `FUN_00028A00()` with:
	  - `ESI = record[+0x04]`
	  - `EDI = record[+0x08]`
	  - `EBX = record[+0x0C]`

- `FUN_00028A00()` decoder behavior recovered from disassembly:
	- `ESI` source begins with `u16 segmentCount`.
	- Then for each segment:
	  - read `u16 destSkip`
	  - add `destSkip` to `EDI`
	  - read `u16 spanLength`
	  - rewrite `spanLength` bytes already present at `EDI` through a 256-byte lookup page derived from `EBX`
	- The lookup page is addressed by replacing the low byte of the `EBX` page pointer with the existing destination pixel value, so the effective table page is `EBX & 0xFFFFFF00`.
	- This is a sparse remap/mask blitter over existing framebuffer content, not a standalone raw sprite unpacker.
	- Direct callers outside the queue path also exist (`0x2EAE0`, `0x2EE35`, `0x2F68A`, `0x36123`, `0x42CAA`), which supports this being a shared graphics routine rather than a one-off effect helper.
	- Named-resource tie-in now confirmed: `FUN_0002CA90()` assigns `DAT_000884BC = "PLAYER"`, and `FUN_00038130()` calls `FUN_00013430(DAT_000884BC, ...)`, so the shared type-3 path is definitely used on the `PLAYER` resource.
	- The same `FUN_00038130()` path also stages direct type-1 payload calls from `DAT_000884B4`, which `FUN_0002CA90()` assigns to `"PILOT"`. So the player/pilot rendering path mixes both shared and direct graphics payload families.

- Type `6` record path is a second shared decoder family:
	- queued records of this type are consumed by `FUN_00012EA0()` through `FUN_00028C64()`.
	- Entry-state recovered from `FUN_00012EA0()`:
	  - `EAX = *(struct + 0x08)`
	  - `EBX = *(struct + 0x0C)`
	  - `ESI = *(struct + 0x24)`
	  - `EDI = record[+0x08]`
	- `FUN_00028C64()` walks a byte stream at `ESI`, splits each byte into high/low nibbles, and dispatches through a `16`-entry handler table at `0x00028CAB`.
	- The handlers write fixed patterns into the current destination row and the row at `+0x180`, while the outer loop advances by `+0x300` per step.
	- This is therefore a compact nibble-opcode pattern/stencil blitter, not a raw plane reader.

- Current implication for extractor work:
	- There is no single validated “sprite decoder” yet.
	- At minimum, we now have three distinct graphics payload families:
	  - direct executable blit payloads at block `+0x24`
	  - shared span-remap decoder `FUN_00028A00()` with auxiliary page at block `+0x28`
	  - shared nibble-opcode decoder `FUN_00028C64()` on a separate structured input path
	- The next grounded step is to tie specific named resources to these families so DogKnife can add per-family decoders instead of heuristic exporters.

## 2026-05-10 - Loader fixups for the global `0x30` block table

- `FUN_0002A630()` is now confirmed to patch the same global `0x30`-byte block space that `DogKnife` is parsing behind resource entry `+0x04`.
	- Raw `lv01s1.dat` header `+0x40` (`Table40Offset`) = `0x107BAC`.
	- Header `+0x10` (`Table40EntryCount`) = `1131`.
	- `0x107BAC + 1131 * 0x30 = 0x114FBC`, which is exactly the end of the last parsed payload-block region and immediately precedes the resource table at `0x11510C`.
	- So the loader's `DAT_000883C8` loop is not some unrelated side table; it is the canonical global block array behind all the resource `+0x04` pointers.

- `FUN_0002ACD0(ptr, base)` is the generic pointer rebaser used by the loader.
	- Behavior is simple and exact: if `*ptr != 0`, replace it with `*ptr + base`.
	- `FUN_0002A630()` uses it on resource entry fields `+0x04`, `+0x08`, `+0x0C`, `+0x14`, and `+0x18`.
	- It also uses it inside the `0x30`-block loop on block-local fields `+0x24` and/or `+0x28` depending on the block type byte at `+0x20`.

- Block type semantics recovered from `FUN_0002A630()`'s `DAT_000883C8` loop:
	- type `1`: rebase block `+0x24`, then call `FUN_000514D0()` on that rebased payload pointer
	- type `3` and type `5`: rebase both block `+0x24` and block `+0x28`
	- type `4` and type `8`: rebase block `+0x24` only
	- type `2` and type `9`: no local `+0x24/+0x28` fixup in this loop
	- This matches the runtime decoder families already recovered later in the frame queue: direct executable payloads rely on patched `+0x24`, while shared remap-family blocks need both `+0x24` and `+0x28` rebased.

- `FUN_000514D0()` is the direct-payload patch helper for type-`1` executable blit blobs.
	- If the first dword is `0x03E08350` (raw bytes `50 83 E0 03`), it writes `0x0004D31C` to payload dword `+0x08`, then follows a nested pointer at `payload + 0x10 + payload[3]`.
	- If that nested location begins with dword `0xE8905550` (raw bytes `50 55 90 E8`), it patches nested dword `+0x04` to the relative target `0x0004D4DC - nested_ptr - 8`.
	- If the first dword is `0xE083F88B` (raw bytes `8B F8 83 E0`), it writes `0x0004D100` to payload dword `+0x08`.
	- So the raw executable payload blobs are not directly runnable as-extracted: the loader both rebases them and patches embedded dispatcher/relative-call fields before the queue worker later calls them.

- Raw block checks now line up exactly with that patch logic:
	- `PLAYER` blocks `0..7` and `PILOT` blocks `0..5` all begin `50 83 E0 03 5F FF 14 85 1C 19 19 00 ...`, which is the `FUN_000514D0()` signature `0x03E08350`.
	- For raw `lv01s1.dat`, the loader type byte is the low byte of block dword `+0x20`.
	- `PLAYER` splits cleanly by that byte:
	  - blocks `0..15` and `22..28` are type `1`
	  - blocks `16..21` are type `3`, with shared `+0x28 = 0x00107900`
	- `PILOT` is `45/45` blocks of type `1` in raw `lv01s1.dat`.
	- `PSTARS` is `22/22` blocks of type `1` in raw `lv01s1.dat`.
	- Example raw `PLAYER` headers:
	  - block `1`: `+0x24=0x000104E4`, `+0x28=0x000BB8A0`
	  - block `2`: `+0x24=0x000109E0`, `+0x28=0x00000018`
	  - block `7`: `+0x24=0x00012278`, `+0x28=0x000BB71C`
	- The explicit type split now explains why the `PLAYER` family appears on both the shared type-`3` path and the direct type-`1` path depending on block/state selection.
	- `PSTARS` does not share the same top-level stub on every block:
	  - blocks `0/1` begin with immediate pattern-writer style code/data (`BB C8 CC CF CC ...`)
	  - blocks `2/3` begin `50 55 90 E8 ...`, which matches the nested signature that `FUN_000514D0()` patches when reached through the direct executable family.
	- `PSTARS` is therefore in the executable-payload bucket, but it is not just a trivial clone of the `PLAYER`/`PILOT` top-level stub.

## 2026-05-10 - Additional named state-handler mappings

- `FUN_00038E90()` is a second concrete `PLAYER` state handler beyond `FUN_00038130()`.
	- It calls `FUN_00013430(DAT_000884BC, ...)`, so it definitely uses the shared type-`3` `PLAYER` remap path.
	- It also stages a direct type-`1` `PILOT` payload from `DAT_000884B4`.
	- It stages additional direct type-`1` `PLAYER` payloads from `DAT_000884BC`.
	- It periodically spawns `PSTARS` via `FUN_000347E0()`.
	- So this state is a mixed composition of shared `PLAYER`, direct `PILOT`, direct `PLAYER`, and spawned `PSTARS` work.

- `FUN_00039880()` is a pure direct-`PLAYER` render state.
	- It does not call `FUN_00013430()`.
	- It allocates queue work, then stages direct type-`1` payloads only from `DAT_000884BC`.
	- This proves `PLAYER` is not exclusively a shared-remap family; some `PLAYER` frames are straight executable payloads.

- `FUN_00039C20()` adds the first concrete runtime tie for `CHARGE`.
	- If `*(state + 0x344) != 0`, it calls `FUN_0002D130()` before the usual queue staging.
	- `FUN_0002D130()` pulls frames from `DAT_000884A8` and stages them directly as type-`1` payloads, so `CHARGE` is on the executable-payload family.
	- The same `FUN_00039C20()` path then stages `PILOT` from `DAT_000884B4` and multiple direct `PLAYER` payloads from `DAT_000884BC`.
	- This gives one verified mixed state that uses `CHARGE`, `PILOT`, and `PLAYER` together on the direct payload path.

## 2026-05-10 - DogKnife loader-aware Table40 and type-3 probe export

- DogKnife now materializes the global `Table40` block table directly from DAT header `+0x40` / `+0x10`, so the code has a first-class view of the same `0x30`-byte block space that `FUN_0002A630()` rebases and patches at load time.
- `DatPayloadBlock30` now exposes the loader type from the low byte of block dword `+0x20`, and `--dump-dat` now reports loader-type distributions for each payload group instead of only dumping the first block raw.
- DogKnife now has a conservative shared-remap probe command:
	- `--export-type3-probes <raw-dat> --resource <name>`
	- It does not pretend to export final sprite pixels.
	- Instead it exports code-backed diagnostics for loader type-`3` blocks:
	  - remap coverage masks reconstructed from the `FUN_00028A00()` `u16 segmentCount` / `u16 destSkip` / `u16 spanLength` stream
	  - raw and grayscale lookup-page dumps from block `+0x28`
	  - metadata with segment counts, touched-pixel totals, runtime-stride bounding boxes, and skipped non-type-`3` blocks
- Validation on raw `lv01s1.dat` with `--resource PLAYER`:
	- payload group `0x107BAC..0x10811C`
	- loader types `0x01:23, 0x03:6`
	- exported type-`3` blocks `16..21`
	- all six share lookup page `0x107900`
	- block `16` probe mask crops to `34x18`, matching the declared `Value08/Value0C = 0x22/0x12`
	- later blocks shrink inward (`32x16`, `30x14`, `26x12`, `24x10`, `22x8`), which is consistent with the remap stream only touching subregions of the runtime destination surface
- This keeps the type-`3` work grounded: the shared family is now inspectable and partially reconstructible in DogKnife, but final sprite output still needs the true pre-remap destination content rather than a guessed synthetic base surface.

## 2026-05-10 - Type-3 PLAYER remap overlaps earlier direct PLAYER output

- `FUN_00038130()` call-site disassembly sharpens the type-`3` `PLAYER` picture beyond the earlier generic queue summary:
	- signed Y-offset table at `0x380E0` = `[0, -1, -2, -4, -5, -6, -6, -5, -4, -2, -1, 0]`
	- type-`3` selector table at `0x380F8` = `[0, 1, 2, 3, 4, 5, 5, 4, 3, 2, 1, 0]`
	- the shared-remap call is `FUN_00013430(DAT_000884BC, objX + 5, selector + 0x10, 0)` with hidden `EBX = objY + 0x11`
	- for the selected type-`3` blocks `16..21`, `FUN_00013430()` therefore stages destination top row `objY + 0x11` and queue bucket `objY + 0x11 + block.Value0C`
- The same state stages direct type-`1` `PLAYER` work using the normal `FUN_00012D40()` bucket convention `top + height`.
- Raw `PLAYER` blocks `0..15` are all direct type-`1` blocks with declared size `44 x 32`, while the shared type-`3` subset `16..21` is `34 x 18`.
- Because the direct `PLAYER` top row in this state is `objY + table[phase]` and the type-`3` remap top row is `objY + 0x11`, the direct `44 x 32` base draw spans well into the remap area for every phase in the recovered table.
- The nearby `PILOT` draw in the same state lands at `objY + table[phase] - 7`, which is too high to be the main remap substrate.
- This does not fully prove queue execution order for every state, but it strongly supports the working model that the type-`3` `PLAYER` family remaps already-drawn direct `PLAYER` pixels rather than representing a standalone sprite plane or the `PILOT` base.

## 2026-05-10 - Type-1 payload families behind 0x4D100 / 0x4D31C / 0x4D4DC

- The loader-patched type-`1` targets are not generic game logic entry points; they are reusable inline blit kernels selected by payload signature.
- `0x4D0F4` / `0x4D100` family:
	- raw top-level signature bytes: `8B F8 83 E0 03 FF 14 85 00 D1 04 00`
	- `FUN_000514D0()` patches payload dword `+0x08` to `0x4D100`
	- the kernels at `0x4D130` and neighbors dispatch on `dest & 3` and perform fixed `16 x 16` copies with destination stride `0x180`
	- inline source bytes begin immediately after the stub at payload `+0x0C`
- `0x4D30C` / `0x4D31C` family:
	- raw top-level signature bytes: `50 83 E0 03 5F FF 14 85 1C D3 04 00`
	- `FUN_000514D0()` patches payload dword `+0x08` to `0x4D31C`
	- the kernels at `0x4D350` and neighbors also dispatch on `dest & 3`, then parse a variable-row dword stream beginning at payload `+0x10`
	- payload dword `+0x0C` is a relative offset from payload `+0x10` to the next stage; the outer `0x4D31C` kernel jumps to the current `ESI` when the row stream finishes
	- recovered row format from the kernel:
	  - `u32 rowCount`
	  - per row: `u32 destSkip`, `u32 encodedDwordCount`, then `(encodedDwordCount + 1)` dwords of inline source data
- `0x4D4D4` / `0x4D4DC` helper family:
	- raw helper signature bytes: `50 55 90 E8 00 00 00 00`
	- `FUN_000514D0()` patches the helper call at payload `+0x04` to target `0x4D4DC`
	- `0x4D4DC` consumes helper data starting at payload `+0x08`
	- its code shows a dword-write phase followed by a word-write phase, so this is a scatter-write helper rather than a simple linear copy kernel
- DogKnife now has `--inspect-type1-probes <raw-dat> --resource <name>` to classify type-`1` blocks conservatively and parse the outer `0x4D31C` stream without attempting emulation.
- Validation on raw `lv01s1.dat`:
	- `PILOT`: `45 / 45` type-`1` blocks are recognized as the `0x4D31C` variable-row copy family
	- `PLAYER` type-`1` subset: `16` variable-row copy blocks, `3` direct `0x4D4DC` helper blocks, `4` remaining unknown signatures
	- `PSTARS`: `11` direct `0x4D4DC` helper blocks and `11` remaining unknown signatures
- So the direct type-`1` family is now at least split into three concrete groups:
	- `0x4D31C` variable-row copy payloads
	- direct `0x4D4DC` scatter-helper payloads
	- a still-unresolved `BB ...` signature family used by `PSTARS` and part of later `PLAYER`

## 2026-05-10 - BB/BL immediate writer family recovered

- The former `BB ...` unknowns are now proven to be a direct straight-line writer family, not a compressed data format.
- Representative raw blocks disassemble to nothing but immediate loads into `EBX`/`BL` followed by direct writes into `EAX + offset` and `RET`.
	- Examples from raw `lv01s1.dat`:
	  - `PSTARS` block `0` at `0x6F09C`: `mov ebx, 0xCCCFCCC8`, then a short sequence of `mov [eax+disp], ebx/bx/bl`, then `ret`
	  - `PLAYER` block `23` at `0x150A0`: `mov ebx, 0xCB8080CB`, several `mov [eax+disp], ebx/bx/bl`, one `mov [eax+disp], imm32`, then `ret`
	  - `PLAYER` block `28` at `0x15368`: `mov bl, 0xCB`, a few byte stores, one immediate byte store, then `ret`
- The currently observed writer encodings are all direct store forms:
	- `BB imm32`
	- `B3 imm8`
	- `89 98 disp32` (`mov [eax+disp32], ebx`)
	- `66 89 98 disp32` (`mov [eax+disp32], bx`)
	- `88 98 disp32` (`mov [eax+disp32], bl`)
	- `88 B8 disp32` (`mov [eax+disp32], bh`)
	- `88 58 disp8` (`mov [eax+disp8], bl`)
	- `C7 80 disp32 imm32`
	- `C6 80 disp32 imm8`
	- `C6 40 disp8 imm8`
- DogKnife `--inspect-type1-probes` now classifies this whole bucket as `PatternWriter` instead of leaving it unknown.
- Updated validation on raw `lv01s1.dat`:
	- `PLAYER` type-`1` subset is now fully classified: `16` `VariableRowCopy`, `3` `ScatterWriteHelper`, `4` `PatternWriter`
	- `PSTARS` is now fully classified: `11` `ScatterWriteHelper`, `11` `PatternWriter`
	- `PILOT` remains `45 / 45` `VariableRowCopy`
- Important constraint: these `PatternWriter` blocks are sparse writers over the destination surface, not full opaque planes.
	- Example `PSTARS` block `0` declares `11 x 11`, but the recovered writes only touch offsets `0x485..0xA85` along a narrow vertical span.
	- So this family is suitable for exact emulation, but not for a naive `width * height` raw-plane interpretation.

## 2026-05-10 - Direct 0x4D4DC helper-prefix blocks can fall through into tail code

- Several direct `PSTARS` blocks that start with the `50 55 90 E8 ...` helper stub do not begin with active helper data.
- For blocks `2`, `3`, `6`, `7`, `10`, and `11`, the first 12 bytes after the stub are all zero, which means the three phase counts consumed by `0x4D4DC` are all zero.
- In those cases the helper returns immediately to the code that follows the zero-count prefix.
- The tail code at payload `+0x14` is itself another direct writer stub; examples:
	- block `2` tail at `0x6F148`
	- block `3` tail at `0x6F1EC`
	- block `6` tail at `0x6F354`
	- block `7` tail at `0x6F3F8`
- Those tails include the same style of immediate pixel writes seen in the recovered `PatternWriter` family, including some `BH` byte stores and immediate dword stores.
- So `0x4D4DC` is not just a self-contained final draw family; some direct payloads use it as a wrapper that skips empty helper phases and then continue executing tail code embedded immediately after the helper prefix.

## 2026-05-10 - Exact direct renderer output now exists for `PatternWriter` and `0x4D4DC`

- `DogKnife` now has `--export-type1-renders <raw-dat> --resource <name>`, which executes the exact direct type-`1` families we can currently justify from Ghidra and writes transparent grayscale PNGs.
- The renderer executes `PatternWriter` stores directly and executes all three `0x4D4DC` helper phases (dword, word, byte) before continuing into the helper tail at the runtime `jmp esi` target.
- The writer coverage needed one additional short word-store form, `66 89 58 <disp8>`, to finish `PSTARS` block `19`.
- Validation on raw `lv01s1.dat`:
	- `PSTARS`: `22 / 22` direct blocks now render successfully to `TestOutput/DatExports/lv01s1/PSTARS/type1_render`.
	- `PLAYER`: `7 / 23` direct blocks now render successfully to `TestOutput/DatExports/lv01s1/PLAYER/type1_render`; the rendered subset is blocks `22..28`.
- `PLAYER` blocks `0..15` are still intentionally skipped because their `0x4D31C` family remains alignment-sensitive at runtime and is not yet recovered precisely enough for a final renderer.

## 2026-05-10 - Exact `0x4D31C` row progression recovered, direct `PLAYER`/`PILOT` now render fully

- Ghidra-backed correction: `0x4D31C` is a 4-way alignment dispatch table at `0x4D31C` targeting copied helpers at `0x4D350`, `0x4D390`, `0x4D400`, and `0x4D470`.
- Helper contract recovered from those copied kernels:
	- the patched payload stub does `push eax; and eax, 3; pop edi; call [0x4D31C + eax*4]`
	- each helper starts with `pop esi`, so `ESI` becomes the return address inside the payload
	- the helper skips payload dword `+0x0C`, reads `rowCount` from payload `+0x10`, then processes rows inline from there
	- per row, `destSkip` is applied relative to the end of the previous copied row, not the previous row start
- The direct-render bug was that DogKnife advanced the `0x4D31C` destination pointer by `destSkip` only; after each row write it also has to advance by that row's copied byte count, matching the helper's `EDI` increments in Ghidra.
- After fixing that exact row progression, the former full-stride `384x32` `PLAYER` outputs collapse to their declared sprite boxes.
- Validation on raw `lv01s1.dat` after the fix:
	- `PILOT`: `45 / 45` direct blocks render successfully, all at declared `30x26`
	- `PLAYER`: `23 / 23` direct blocks render successfully, blocks `0..15` now export as declared `44x32`
	- `PSTARS`: remains `22 / 22`

## 2026-05-10 - Exact `PLAYER` type-3 composite exporter now works

- `DogKnife` now has `--export-type3-composites <raw-dat> --resource PLAYER`.
- The composite path uses the exact `FUN_00038130` phase data already recovered:
	- base direct block set `0..15`
	- overlay selector table `0 1 2 3 4 5 5 4 3 2 1 0`
	- overlay blocks `16..21`
	- overlay origin `x=5`, `y=0x11 - phaseYOffset`
	- phase Y offsets `0 -1 -2 -4 -5 -6 -6 -5 -4 -2 -1 0`
- Important constraint: the exporter is exact about the remap operator and recovered phase tables, but it still writes the full `base block x phase` matrix because base-block selection and remap phase are driven by different `FUN_00038130` state variables.
- Validation on raw `lv01s1.dat`:
	- base direct blocks exported: `16`
	- phase entries per base: `12`
	- total exact composites exported: `192` to `TestOutput/DatExports/lv01s1/PLAYER/type3_composite`

## 2026-05-10 - Exact `FUN_00038130` branch variants now export beyond base-plus-remap

- `FUN_00038130` does not stop at the direct `PLAYER` base plus the shared type-`3` remap; it also stages branch-specific direct overlays.
- Additional state recovered from nearby PLAYER control code:
	- `DAT_00060F32[0..1] = [0, 1]`, so the nearby `PILOT` row matches the `PLAYER` facing row directly in this state.
	- `FUN_00038600()` maps movement/input bits from `obj + 0x4A` into `obj + 0x36D` as eight directional variants `0..7`; `FUN_00038130()` uses that value only in the special branch where `obj + 0x79 == 7`.
	- In the normal branch (`obj + 0x79 = 0..6`), `FUN_00038130()` uses fixed `PILOT` variant `8` for the current facing row and also stages direct `PLAYER` accent blocks `22..28`.
- The normalized positions relative to the direct `PLAYER` base are now exported exactly from the recovered call-site math:
	- `PILOT` overlay at `(x + 7, baseTop - 7)`
	- small direct `PLAYER` accent at `(x + 12, baseTop - 6)`
	- shared type-`3` remap at `(x + 5, baseTop + 0x11 - phaseYOffset)`
- `DogKnife --export-type3-composites` now writes two additional exact state-space families under `type3_composite/`:
	- `fun38130_special`: `16 base blocks x 12 remap phases x 8 PILOT directional variants = 1536` images
	- `fun38130_cycle`: `16 base blocks x 12 remap phases x 7 PLAYER accent states = 1344` images
- This still does not prove the final temporal animation order, but it does move the remaining gap from rendering semantics to state sequencing.

## 2026-05-10 - Batch exact-render export now covers most of `lv01s1.dat`

- `DogKnife` now has `--export-known-renders <raw-dat>`, which runs the currently exact exporters across the whole DAT and writes `known_render_summary.txt` alongside the per-resource outputs.
- Current validated coverage on raw `lv01s1.dat`:
	- resources in DAT: `58`
	- resources with exported assets: `53`
	- resources with remaining unresolved loader families: `8`
	- exporter failures: `0`
- This command now exports, in one pass:
	- all resources with direct type-`1` blocks via `Type1RenderedExporter`
	- `PLAYER` direct type-`1` plus exact type-`3` composite families
	- `TEXTURE` via the existing exact type-`4` texture exporter
- Remaining unresolved families in `lv01s1.dat` are now a short explicit list rather than a general rendering gap:
	- `BLOBS`: residual `type 3`
	- `DISPLAY`: residual `type 3` and `type 4`
	- `LEVINFO0/1/2`: `type 2` and `type 4`
	- `REACTOR`: residual `type 7`
	- `END`, `STOP_BLOCK`: `type 0`

## 2026-05-10 - `BLOBS` grounded to its state dispatcher; exact composition still pending

- `BLOBS` in `lv01s1.dat` now has concrete coverage facts:
	- payload group `0x11303C..0x114FBC`
	- `168` total blocks = `160` direct type-`1` blocks plus `8` shared type-`3` remap blocks
	- direct blocks `08..167` already render exactly as `23x20`
	- remap blocks `00..07` share one lookup page and touch only a narrow `25x3` region, so they are definitely small sparse remaps over existing pixels, not standalone planes
- Runtime tie-in is now grounded past the resource string:
	- state init near the `"BLOBS"` string stores the BLOBS resource handle into `DAT_000884B8`
	- `FUN_0002CD50()` dispatches state `0x0B` by calling `FUN_0003A7B0(0)`
	- `FUN_0003A7B0()` is not the sprite renderer; it computes/caps BLOBS viewport deltas and writes them into the global camera/clip slots at `DAT_00063508/0C/...`
	- the state dispatch table at `fix_off32_0002C67C + 0x0B*0x10` resolves the BLOBS handler quartet as `0x3A230, 0x3A2F0, 0x3A500, 0x3A8D0`
	- manual decode of the raw code at `0x3A2F0` now proves that this slot is the BLOBS composite renderer, not just another updater:
		- it begins with `CALL 0x38130`, so the BLOBS state renders the normal `PLAYER` frame first
		- it then loops `5` times and allocates two queue records per pass via `FUN_00012E30()`, which is the queue-slot allocator used by this render queue
		- first queued record is type `1`: `record[+0x16] = 1`, `record[+0x04] = selectedBlock[+0x24]`, `record[+0x08] = base destination`
		- second queued record is type `3`: `record[+0x16] = 3`, `record[+0x04] = overlayBlock[+0x24]`, `record[+0x0C] = overlayBlock[+0x28]`, `record[+0x08] = base destination + 0x1C7F`
		- the phase selector uses the table at `0x3A206 = [0,1,2,3,4,5,6,7,7,6,5,4,3,2,1,0]`
		- after each pass the phase advances by `+3 mod 16`
		- `0x3F300()` is the visibility/output gate for this handler: it clips the BLOBS frame against the active viewport, writes the visible destination x/y back through `0x9278C` / `0x92790`, and only returns `1` when the current `25 x 22` BLOBS frame is onscreen
		- switch table at `0x3A2D4` is now recovered exactly:
			- state `0 -> 0x3A4CC`
			- state `1 -> 0x3A38D`
			- state `2 -> 0x3A3C2`
			- state `3 -> 0x3A4B2`
		- currently proven per-pass math from the shared tail:
			- `slot = (obj[+0xDC] - 0x0A - 7*i) & 0x3F`
			- `phase = (initialPhase + 3*i) & 0x0F`
			- `directIndex = (0x20 * i) + 8 + phaseTable[phase] + (8 * (obj[+0x2E0 + slot] >> 1))`
			- `remapIndex = directIndex & 7 = phaseTable[phase]`
		- state `1` additionally computes fresh base coordinates from the `obj + 0xE0/+0xE4` dword-pair table using `slot`; states `0`, `2`, and `3` land inside the shared tail and deliberately skip different portions of that setup
- Current blocker is no longer whether BLOBS uses the shared type-`3` family; that is now proven. The remaining gap is the exact per-state semantics of the `obj + 0x3A0` switch inside `0x3A2F0`, especially what state `0` and state `3` mean and how state `2` reuses or preserves base coordinates.

## 2026-05-10 - `PLAYER` animation order model tightened; base and remap are separate accumulators

- `FUN_00038130()` decompiles misleadingly for the first two selector fields. Raw bytes prove that, on every call, it advances:
	- `obj + 0x370 += DAT_00093924`, then wraps modulo `0x80000`
	- `obj + 0x374 += DAT_00093924`, then wraps modulo `0xC0000`
	- `obj + 0x378 += DAT_0009393C`, then wraps modulo `0xC0000`
- This means the direct `PLAYER` base frame and the type-`3` remap phase are **not** one fused animation index. They are two separate 16.16-style accumulators advanced in the same render function from the same tick source, but with different periods (`8` vs `12` steps).
- Confirmed direct/phase selection in `FUN_00038130()`:
	- direct base block index = `(highWord(obj+0x370) + (objFacingRow * 8))`
	- remap phase index = `highWord(obj+0x374)`
	- remap selector table at `0x380F8` = `0 1 2 3 4 5 5 4 3 2 1 0`
	- remap Y-offset table at `0x380E0` = `0 -1 -2 -4 -5 -6 -6 -5 -4 -2 -1 0`
- Discrete overlay state remains separate from those continuous accumulators:
	- `obj + 0x79 == 7` selects the special branch
	- `obj + 0x79 = 0..6` selects the cycle branch
	- in the cycle branch, `obj + 0x78` is decremented after each draw; on underflow it reloads to `1` and increments `obj + 0x79`
	- once `obj + 0x79` reaches `7`, `FUN_00038130()` no longer advances it; another function must reset it back to a cycle state
- Nearby non-lifted state code now gives the transition picture more concretely:
	- `0x38110` initializes the `FUN_00038130` state with `obj+0x79 = 7` and related byte flags (`7B=0, 7A=1, 7C=1, 7D=1, 7E=0`)
	- `0x38B40` forces `obj+0x79 = 0`, but does **not** reset `obj+0x78`, `obj+0x370`, `obj+0x374`, or `obj+0x378`
	- `0x38E50` initializes the alternate `0x38E90` family and sets motion fields (`0x20/0x34 = 0x40000`, `0x24/0x38 = 0x90000`), but also does not touch the proven `FUN_00038130` accumulator fields
	- `0x38600` writes `obj+0x36D` from movement/input bits in `obj+0x4A`; `FUN_00038130()` only reads that field in the special branch
	- `0x38410` drives a separate small state machine at `obj+0x36E` (`0 -> 1 -> 2 -> 0` behavior when the gated input bit is active)
- The state tables around `fix_off32_0002C67C` now support the call-order model directly:
	- one quartet is `0x38110, 0x38130, 0x38410, 0x38B40`
	- a second quartet is `0x38E50, 0x38E90, 0x39270, 0x394C0`
	- this strongly supports treating `0x38110/0x38130/0x38410/0x38B40` as one coherent PLAYER state family rather than isolated helpers
- Best grounded current `FUN_00038130` animation-order model:
	- continuous base phase from `obj+0x370`
	- continuous remap/bob phase from `obj+0x374`
	- discrete overlay/accent phase from `obj+0x79` and `obj+0x78`
	- directional special-branch PILOT overlay from `obj+0x36D`
- Remaining proof gap is now precise:
	- runtime value/cadence of `DAT_00093924` and `DAT_0009393C`
	- initial seeding/writers of `obj+0x78`, `obj+0x370`, `obj+0x374`, and `obj+0x378`
	- exact gameplay timing of when the engine enters the `0x38110` quartet versus the `0x38E50` quartet

## 2026-05-10 - `PLAYER` timer globals still have no static writers; nearby pre-states use a different bank

- Fresh xref passes over the whole nearby timer cluster still show only static reads for `DAT_00093924`, `DAT_00093928`, `DAT_0009392C`, `DAT_00093934`, `DAT_00093938`, and `DAT_0009393C` in `CATDEC.LE`; no normal `WRITE` xrefs surfaced for any of them.
- The two odd `DAT_0009393C` data refs at `0x31A2A` and `0x4734A` are not hidden writers for the `PLAYER` selectors. Manual decode shows they are just more readers inside other update routines that subtract/add the same global delta.
- Raw decode of the neighboring non-lifted code at `0x37A30` and `0x37DA0` proves those handlers are using a separate selector/timer family:
	- `0x37A30` initializes `obj+0x7A..0x7E`, `obj+0x360 = 0`, `obj+0x364 = 3`, and motion fields `0x20/0x24/0x34/0x38`; it then advances `obj+0x360 += DAT_00093924`, `obj+0x33C += DAT_00093928`, and `obj+0x368 += DAT_0009393C`
	- `0x37DA0` writes `obj+0x365` and drives motion/clamp banks at `obj+0x1C/0x1E/0x20` and `obj+0x30/0x32/0x34`
	- neither region writes or seeds `obj+0x78`, `obj+0x79`, `obj+0x370`, `obj+0x374`, or `obj+0x378`
- Raw decode of the known quartet entry points tightens the boundary further:
	- `0x38110` sets `obj+0x79 = 7` plus byte flags `7B=0, 7A=1, 7C=1, 7D=1, 7E=0`, but does not touch `obj+0x78/0x370/0x374/0x378`
	- `0x38B40` forces `obj+0x79 = 0`, but again does not seed `obj+0x78/0x370/0x374/0x378`
	- `0x38E50` initializes the alternate family (`7B=1, 7A=0, 7C=0, 7D=0, 7E=0`, motion fields `0x20/0x24/0x34/0x38`) and also does not touch the `FUN_00038130` accumulator bank
- Best current grounded conclusion: the unresolved part of exact `PLAYER` animation order is no longer local to the nearby PLAYER-family handlers. The missing proof is now either a more distant object-seeding path for `obj+0x78/0x370/0x374/0x378` or an external/runtime-fed source for the `0x93924..0x9393C` delta globals.

## 2026-05-10 - `PLAYER` state table layout and concrete state IDs are now proven

- The actual per-state record base is `fix_off32_0002C678`, not `fix_off32_0002C67C`. Each state occupies `0x10` bytes = four function pointers.
- Four sibling dispatchers now account for the four slots directly:
	- `FUN_0002CC00()` calls `[0x2C678 + state*0x10]`
	- `FUN_0002CD50()` calls `[0x2C67C + state*0x10]`
	- `FUN_0002CE80()` calls `[0x2C680 + state*0x10]`
	- `FUN_0002D290()` calls `[0x2C684 + state*0x10]` when `obj+0x58 == 0`
- That makes the previously inferred PLAYER quartets exact state records rather than loose neighboring code:
	- state `0x01` = `0x38110, 0x38130, 0x38410, 0x38B40`
	- state `0x04` = `0x38E50, 0x38E90, 0x39270, 0x394C0`
	- state `0x05` = `0x397E0, 0x38E90, 0x39270, 0x394C0`
	- state `0x0B` = `0x3A230, 0x3A2F0, 0x3A500, 0x3A8D0` (`BLOBS`)
	- state `0x0E` = `0x38E50, 0x38E90, 0x39270, 0x394C0`
	- state `0x0F` = `0x37A30, 0x37AC0, 0x37DA0, 0x38050`
- The earlier neighboring bank result now fits this table cleanly:
	- state `0x01` is the proven `FUN_00038130` family with the unresolved `0x78/0x79/0x370/0x374/0x378` cadence question
	- state `0x0F` and state `0x00` use the separate `0x33C/0x360/0x364/0x365/0x368` bank instead
	- state `0x04/0x05/0x0E` are the alternate family and still do not seed the `FUN_00038130` accumulator bank
- This materially narrows the gameplay-order question: “enter the `0x38110` quartet versus the `0x38E50` quartet” is now equivalent to proving writes that move `DAT_0008849B` between state IDs `0x01` and `0x04/0x05/0x0E`.

## 2026-05-10 - Raw DAT header now proves both the top-level state byte and palette selector bytes

- `FUN_0002A630()` is the clean writer that had been missing from the normal `DAT_0008849B` xref surface. After it loads `map/<name>.dat` into `DAT_00060C00`, it assigns:
	- `DAT_0008849B = DAT_00060C00[0x00]`
	- `DAT_0008849A = DAT_00060C00[0x01]`
	- `DAT_000655BB = DAT_00060C00[0x0B]`
	- `DAT_000655BE = DAT_00060C00[0x0C]`
	- `DAT_0006557C = DAT_00060C00 + *(int *)(DAT_00060C00 + 0x54)`
- This means the same raw DAT header already being parsed by DogKnife contains code-proven palette selector bytes, not just the palette-bank array at offset `0x54`.
- `FUN_0002A500()` resets `DAT_000655BB`, `DAT_000655BE`, and the adjacent control bytes to `0` before calling `FUN_0002A630()`, so the header values are authoritative loader inputs rather than leftovers from an earlier scene.
- `FUN_000110A0()` now gives the top-level state picture concretely:
	- it switches directly on `DAT_0008849B`
	- state `0x08` is the level/gameplay init path that calls `FUN_0002C470()` (PLAYER setup) and then `FUN_00020FC0()`
	- after that state-`0x08` path, it explicitly calls `FUN_0003DD90(DAT_0006557C, 0, 0x20000)`
- Palette implications are now much tighter:
	- `FUN_0003B000(bank, ...)` targets `DAT_0006557C + bank * 0x300`
	- `FUN_00049640()` uses `DAT_000655BB` as one code-proven bank selector
	- `FUN_00020FC0()` uses `DAT_000655BE` as another code-proven bank selector and copies from derived banks like `DAT_000655BE + 1`
	- for state `0x08`, bank `0` is also explicitly passed into the palette transition helper through `FUN_0003DD90(DAT_0006557C, 0, 0x20000)`
- Practical exporter consequence: DogKnife no longer needs to rely only on the old `TEXTURE` block-field heuristic. The raw DAT itself now provides three code-grounded palette candidates worth exporting side-by-side: state-`0x08` bank `0`, header byte `0x0B` (`DAT_000655BB`), and header byte `0x0C` (`DAT_000655BE`).

## 2026-05-10 - `lv01s1.dat` palette validation: header bank `0x0B` is actionable, state-`0x08` bank `0` is not

- A real DogKnife validation run against `TestOutput/BLK_Raw/map/lv01s1.dat` now confirms the loader/header facts on an actual level DAT:
	- `Type = 0x0B`
	- header bytes `0x08..0x0C = 5, 10, 8, 9, 10`
	- palette bank count parsed from the DAT = `10` (`0..9` valid)
- Because `lv01s1.dat` is type `0x0B`, the state-`0x08` startup rule does **not** apply to this file, so DogKnife correctly did **not** export a bank-`0` variant here.
- The new `TEXTURE` exporter emitted two non-grayscale, grounded palette variants for this DAT:
	- `palette_bank_09_header_0B` from header byte `0x0B` / `DAT_000655BB`
	- `palette_bank_03_block_value20` from the older shared-`value20` TEXTURE heuristic
- Header byte `0x0C` was `10`, which is out of range for a `10`-bank palette region (`0..9` valid), so no `header_0C` variant was exported for `lv01s1.dat`.
- Current best palette-reading posture is therefore more precise than before:
	- treat DAT header bytes `0x0B/0x0C` as code-proven palette selectors
	- export them when they point inside the parsed palette-bank array
	- only add the explicit bank-`0` startup candidate when the DAT header `Type` is `0x08`
	- keep the old block-field palette bank only as a labeled legacy heuristic for comparison

## 2026-05-10 - Exact type-`1` and type-`3` exporters now emit colored palette variants too

- DogKnife now applies the same code-grounded DAT palette logic to the exact sprite renderers, not just `TEXTURE`:
	- `Type1RenderedExporter` still writes the legacy grayscale outputs, but now also emits palette-colored variants for any in-range code-proven banks from the DAT header
	- `Type3CompositeExporter` does the same for `PLAYER` base blocks, phase composites, and the grounded `FUN_00038130` special/cycle branch exports
- The palette logic shared across those exporters is now:
	- `header[0x0B] -> DAT_000655BB` candidate bank
	- `header[0x0C] -> DAT_000655BE` candidate bank
	- `bank 0` only when `header[0x00] == 0x08`, because the state-`0x08` startup path explicitly calls `FUN_0003DD90(DAT_0006557C, 0, 0x20000)`
	- missing/invalid palette regions no longer break the exact renderers; they fall back to grayscale-only and record that in metadata
- Validation on `lv01s1.dat` (`Type = 0x0B`, header `0x0B/0x0C = 9/10`, palette bank count `10`) is now concrete:
	- `PLAYER` type-`1` exact export succeeded and emitted `palette_bank_09_header_0B`
	- `PLAYER` type-`3` exact composite export succeeded and emitted `palette_bank_09_header_0B`
	- `PILOT` type-`1` exact export also succeeded and emitted `palette_bank_09_header_0B`
	- no `header_0C` variant was emitted because bank `10` is out of range for this DAT
- Practical consequence: for level DATs like `lv01s1.dat`, the exact sprite exporters are no longer limited to grayscale proof images. We now get side-by-side colored outputs using the best code-grounded bank(s) from the DAT header itself.

## 2026-05-10 - The remaining type-`0x0B` palette mismatch is a live-palette composition issue, not a VGA expansion bug

- The hardware write path is now explicit in Ghidra:
	- `FUN_0004CDA3()` writes the live shadow palette at `DAT_0009F968` straight to VGA DAC ports `0x3C8/0x3C9`
	- it outputs the stored bytes directly; there is no extra brighten/gamma step after the palette bytes leave `DAT_0009F968`
	- so DogKnife's `0..63 -> 0..255` conversion is not the source of the "too dark" symptom
- The type-`0x0B` scene path for `lv01s1.dat` is now clearer too:
	- `FUN_000110A0()` dispatches state `0x0B` to `FUN_00014010()`
	- the active frame/update loop reaches `FUN_0003E760()`
	- when `DAT_000602AA != 0`, that loop calls `FUN_00049640()`
	- `FUN_0003B1A0(param_1 != 0)` is one concrete writer of `DAT_000602AA = 1`, so this is a one-shot palette-trigger path rather than a generic always-on bank select
- The exact palette operations around that path are now grounded:
	- `FUN_0003DD30()` zeroes `DAT_0009F968`, zeroes the transition work buffers, sets `DAT_000938E0 = 1`, and immediately writes the all-black live palette to VGA
	- `FUN_00049640()` then does **not** replace the whole palette with one bank
	- instead it calls `FUN_0003DD90(DAT_0006557C + DAT_000655BB * 0x300, start=0, count=0x80, duration=0x20000)`, which only schedules a transition for entries `0x00..0x7F`
	- then it calls `FUN_0003E080(DAT_0006557C + DAT_000655BE * 0x300, start=0xE0, count=0x20)`, which directly copies only entries `0xE0..0xFF` into `DAT_0009F968` and writes them to VGA immediately
	- that means the live type-`0x0B` palette is a composed runtime state, not a flat `header[0x0B]` bank dump
- Direct export evidence from DogKnife now explains why the current colored outputs can still be "close but dark":
	- `PLAYER/type1_render/grayscale/blocks/block_00_44x32.png` uses only palette indices `0xE0..0xEB`
	- `PLAYER/type1_render/grayscale/blocks/block_22_19x11.png` uses only palette indices `0xCB..0xCE`
	- so the direct `PLAYER` resource does not mainly live in the low half that `FUN_00049640()` transitions from `DAT_000655BB`
	- a flat `palette_bank_09_header_0B` export is therefore only a comparison candidate for type-`0x0B` sprites, not the exact live palette state
- For `lv01s1.dat` specifically, the `FUN_00049640()` upper-slice source is now also checked against raw bytes:
	- header byte `0x0C` / `DAT_000655BE` is `10`
	- the exact raw source window used by `FUN_00049640()` for entries `0xE0..0xFF` starts at `0x118D7C`, which lies **outside** the parsed `0x116CDC..0x118ADC` palette-bank region but still inside the file
	- the first `0x20 * 3` bytes at that raw source are all zero in `lv01s1.dat`
	- therefore `FUN_00049640()` alone cannot explain the bright in-game top-range sprite colors for this DAT; some later palette write/copy path still remains unresolved
- New working conclusion:
	- do **not** treat the current darkness as a VGA expansion bug
	- do **not** treat `palette_bank_09_header_0B` as the exact runtime palette for type-`0x0B` scene sprites
	- the remaining job is to identify the later writer(s) that patch `DAT_0009F968` after the initial `FUN_00049640()` scene setup

## 2026-05-10 - Palette-bank preview export added, and one palette-script path ruled out for plain level rendering

- DogKnife now exports palette-bank previews alongside the palette-aware outputs:
	- each palette-aware export root now contains `palette_banks/original_vga6bit` and `palette_banks/converted_8bit`
	- every parsed bank gets two swatch-sheet PNGs:
		- `original_vga6bit/bank_##.png` renders the raw `0..63` VGA DAC triplets directly as dark RGB swatches
		- `converted_8bit/bank_##.png` renders the same bank after the normal `0..63 -> 0..255` expansion used for PNG output
	- `palette_banks/metadata.txt` lists every generated bank preview file
	- the main export `metadata.txt` now points back to those preview directories
- Validation on `lv01s1.dat` is concrete:
	- `TEXTURE`, exact `PLAYER` type-`1`, and exact `PLAYER` type-`3` exports all emitted `palette_banks/original_vga6bit` and `palette_banks/converted_8bit`
	- for `lv01s1.dat`, each preview directory contains `bank_00.png` through `bank_09.png`
- Tracing update on the remaining live-palette writer path:
	- `FUN_00054EB0()` is the interpreter that dispatches palette-script opcodes, including opcode `0x0B -> FUN_00054E20()` for direct palette segment writes into `DAT_0009F968`
	- but the discovered `FUN_00054EB0()` call chain currently sits under `FUN_00036FC0()` / `FUN_00036F40()`, which is the `LEADER` mode path (`FUN_0002C150("LEADER")`), not the plain `lv01s1.dat` level wrapper `FUN_00010E40()`
	- so that script interpreter is real and important, but it is **not yet** the missing proof for the ordinary type-`0x0B` level palette seen during normal `lv01s1.dat` gameplay
- Current best next tracing target remains the ordinary level loop around `FUN_00010E40() -> FUN_0003E3C0() -> FUN_0003E760()` and the unresolved callers that eventually trigger `FUN_0003B1A0()` / `DAT_000602AA`

## 2026-05-10 - The render queue is palette-agnostic; palette banks are consumed by scene/state code

- New proof from the ordinary render path:
	- sprite/object renderers like `FUN_00038130()` choose sprite records and destination addresses, but do **not** pass a palette-bank selector
	- queued draw records allocated by `FUN_00012D40()` / `FUN_00012E30()` are 0x18-byte command records with fields used for:
		- function pointer at `+0x04`
		- destination pointer at `+0x08`
		- optional extra pointer/data at `+0x0C`
		- operation type byte at `+0x16`
	- no palette-bank field appears in those draw records
	- queue execution in `FUN_00012FE0()` dispatches only by that type byte:
		- type `< 2`: call function pointer with the queued destination/data
		- type `3`: call `FUN_00028A00()`
	- `FUN_00028A00()` is byte-level compositing/copying inside the indexed framebuffer, not a palette selector
	- `FUN_00046000()` is also not a palette selector; it remaps framebuffer indices through a 32-byte-wide lookup table (`destIndex + sourceIndex * 0x20`)
- New proof from the palette side:
	- the DAT palette-bank table base `DAT_0006557C` is written once by `FUN_0002A630()` when the DAT is loaded
	- the currently proven consumers of `DAT_0006557C` are palette/state functions such as:
		- `FUN_000110A0()` startup/state init
		- `FUN_00010E40()` post-loop copy of bank 0 into the live palette
		- `FUN_0003B000()` palette transition helper
		- `FUN_00049640()` type-`0x0B` scene palette composition
		- `FUN_00020FC0()` state-8 palette setup
		- `FUN_0004A820()` another mode/state palette transition
	- no proven sprite-queue or sprite-blit function currently reads `DAT_0006557C`
- Runtime palette model now proven more tightly:
	- multiple palette banks can exist in the DAT, but the renderer still uses a single live VGA palette shadow at `DAT_0009F968`
	- `FUN_0003E080()` copies palette slices into that single live shadow and immediately calls `FUN_0004CDA3()`
	- `FUN_0004CDA3()` writes that single live shadow directly to VGA DAC ports `0x3C8/0x3C9`
	- therefore the current evidence supports **scene/state-level palette selection/composition**, not a per-graphic palette-bank selector in the render queue
- Practical exporter consequence:
	- using the same DAT-wide palette reference variants for all resource exports is consistent with the proven runtime model so far
	- what remains unresolved is **which scene/state code produces the final live palette for `lv01s1.dat`**, not a missing per-resource palette-bank field in the sprite renderer

## 2026-05-10 - Type `0x0B` starts from bank 0, then applies later slice-specific palette changes

- `lv01s1.dat` header bytes directly confirm it is a type-`0x0B` DAT:
	- header bytes begin `0B 63 00 00 00 00 10 00 05 0A 08 09 0A`
- `FUN_000110A0()` does not leave the type-`0x0B` startup palette ambiguous anymore:
	- the table byte at `DAT_00010E1F[0x0B]` is `0x01`
	- for that value, `FUN_000110A0()` sets the palette mode bits and then calls `FUN_0003DD90(DAT_0006557C, 0, 0x20000)`
	- therefore the ordinary type-`0x0B` scene starts from a **full live-palette transition from bank 0**
- Newly decoded type-`0x0B` dynamic palette handlers then replace selected slices after that bank-0 baseline:
	- raw block around `0x1411C` counts down `DAT_000602F8`; when it underflows, it calls `FUN_0003DD90(DAT_0006557C + DAT_000602F4 * 0x300, 1, 0x6000)` with count `0x7F`, then toggles `DAT_000602F4 ^= 1`
		- practical effect: low-slice transition between banks 0 and 1
	- raw block around `0x14EC7` calls `FUN_0003DD90(DAT_0006557C + 0x1500, 1, 0x20000)` with count `0x7F`
		- `0x1500 / 0x300 = 7`, so this is a low-slice transition from bank 7
	- raw block around `0x1527A` calls `FUN_0003DD90(DAT_0006557C + (byte[+0x9C] + 2) * 0x300, 0x60, 0x8000)` with count `0x20`
		- practical effect: a mid-slice transition driven by an object field, not a fixed DAT header byte
- Important negative result from the nearby startup/object sequence:
	- the `0x14F80` call to `FUN_0003B1A0()` is preceded by `xor eax, eax`, so it is `FUN_0003B1A0(0)`
	- `FUN_0003B1A0(0)` does **not** set `DAT_000602AA`; it instead calls `FUN_0003DE70(0x20000, 0)`
	- so this nearby startup path does **not** immediately arm `FUN_00049640()`'s `DAT_000602AA`-gated upper-slice update
- Current best interpretation for static `PLAYER` exports:
	- bank 0 is no longer just a comparison palette; it is the proven type-`0x0B` scene-start baseline
	- later low/mid slice transitions are real and code-proven, but the exact later writer responsible for the bright final upper-range `PLAYER` colors is still unresolved

## 2026-05-10 - Cross-resource palette model: scene-global live palette plus narrow index-remap families

- The bank-0 type-`0x0B` startup baseline is not `PLAYER`-specific:
	- running the shared DogKnife palette-aware `TEXTURE` export on `lv01s1.dat` now emits the same proven baseline variant:
		- `palette_bank_00_state0B_start=bank00`
		- `palette_bank_09_header_0B_low_slice_reference=bank09`
		- plus the existing `TEXTURE` probe variant `palette_bank_03_block_value20=bank03`
	- therefore the shared helper change is applicable across at least one non-`PLAYER` graphic family in the same DAT
- Default render architecture still supports one scene-global live palette for most graphics:
	- generic queued draw helper `FUN_00013040()` has 98 xrefs in the current program and still carries no palette-bank field in its draw records
	- `FUN_00013430()` and `FUN_00038130()` likewise enqueue graphics without any palette-bank parameter
	- this remains the strongest evidence that most graphics in a DAT consume the same current live palette rather than per-resource palette banks
- Two narrow special render families now stand out, but neither is a per-resource palette-bank selector:
	- `FUN_000473B0() -> FUN_00047410() -> FUN_00046000()`
		- `FUN_000473B0()` first runs a special draw through `FUN_00047410()`, then falls back to a normal queued draw through `FUN_00013040()`
		- `FUN_00047410()` is the only proven consumer of `DAT_00099574`
		- `FUN_00046000()` rewrites framebuffer indices through `destIndex + sourceIndex * 0x20`, i.e. an index remap table, not a palette bank
	- `0x47CD0/0x47DBD -> FUN_00048500()` family
		- setup around `0x482A0` seeds state from `DAT_0006557C` via `FUN_0003E080()` and initializes `DAT_00099580/88/90/94`
		- `FUN_00048500()` is a geometry/index-space transform using trigonometric lookup table `DAT_00011438` and the `DAT_0009958x` state; it does not read `DAT_0006557C`, `DAT_000655BB`, or `DAT_000655BE`
		- so this is another special indexed rendering/remap path layered on top of the live palette, not a competing palette-bank selection mechanism
- Current exporter implication:
	- for most graphics, the right palette model is still the reconstructed scene-global live palette
	- for the narrow special families above, the right output model is live palette **plus** path-specific index remap/transform logic
	- the remaining missing piece for exact colors is still the unresolved later live-palette writer for the bright upper-range colors, not a hidden per-resource bank selector

## 2026-05-10 - Palette selection is scene-global, not per resource

- The current strongest selector model is now explicit:
	- `FUN_0002A630()` loads the DAT and stores three palette-selection globals:
		- `DAT_0006557C` = palette table base pointer from DAT header offset `+0x54`
		- `DAT_000655BB` = DAT header byte `0x0B`
		- `DAT_000655BE` = DAT header byte `0x0C`
	- generic queued draw paths still have no palette-bank field, so sprites/UI elements are not matched to banks individually; they use the current scene-global live palette
- Type `0x0B` map-scene rule for `lv01s1.dat`:
	- `FUN_000110A0()` starts the scene from full bank 0 (`FUN_0003DD90(DAT_0006557C, 0, 0x20000)`)
	- later `FUN_00049640()` does **not** switch the whole scene to the last bank:
		- indices `0x00..0x7F` are taken from bank `DAT_000655BB` (bank 9 here)
		- indices `0xE0..0xFF` are rewritten from `DAT_000655BE`-related input only
		- in `lv01s1.dat`, `DAT_000655BE == 10` while the static bank list is `0..9`, so byte `0x0C` is not acting as a direct bank index for this DAT
	- conclusion: the correct runtime palette for map-scene graphics is often a hybrid live palette, not one of the 10 static banks by itself
- Other proven palette selectors:
	- `FUN_0004A820()` performs a full-bank transition from `DAT_000655BE` after `FUN_000110A0()`; this is a scene/mode-level override, not a per-resource choice
	- `FUN_00020FC0()` builds a derived working palette from `DAT_000655BE` and `DAT_0006557C`, again at scene/mode level
	- helper `FUN_0003B000(param1)` and event path `FUN_0003DA80()` can swap the low slice (`1..0x7F`) to banks `param1` / `0..3` during scripted runtime changes
- Practical export rule:
	- there is no code-proven `resource -> bank N` mapping for generic graphics
	- the correct palette choice comes from the scene/state handler currently owning the screen, not from the sprite/UI resource itself

## 2026-05-10 - DogKnife now writes a scene-level `default/` palette export

- Implemented a shared default palette path in `DatPaletteHelper` and the active exporters (`TEXTURE`, type-`1`, type-`3`):
	- exports now write a `default/` tree alongside `grayscale/` and the diagnostic bank variants
	- `default/` uses the best currently proven scene-level palette strategy instead of hard-wired grayscale when one exists
- Current default strategy rules:
	- type `0x0B`: best-effort hybrid live palette = bank 0 baseline + `0x00..0x7F` from bank `DAT_000655BB`; `0xE0..0xFF` also comes from `DAT_000655BE` only if that header byte is a valid static bank index, otherwise the bank-0 baseline is kept for that tail
	- proven bank-0 startup types currently default straight to bank 0
	- all other DAT types still fall back to grayscale until their scene-level palette strategy is proven
- Validation:
	- `dotnet build DogKnife.csproj` succeeded after the change
	- `PLAYER` type-`1`, `TEXTURE`, and `PLAYER` type-`3` exports for `lv01s1.dat` now all emit `default/` outputs and report the applied default palette summary in both CLI output and metadata
	- `--export-known-renders` against `lv01s1.dat` completed with 53 exported resources, 0 exporter failures, and the expected `default/` trees present in the currently exact export families (`TEXTURE`, type-`1`, type-`3`)

## 2026-05-10 - `FUN_00049640()` is gated and not part of the traced `lv01s1` startup baseline

- New proof tightening the type-`0x0B` model:
	- direct raw-file probe for `lv01s1.dat` shows the bytes at the static source range that `FUN_00049640()` would use for `0xE0..0xFF` when `DAT_000655BE == 10` are all zeroes
	- `FUN_00049640()` has exactly one caller: `FUN_0003E760()`
	- `FUN_0003E760()` only calls `FUN_00049640()` when `DAT_000602AA != 0`
	- `DAT_000602AA` is only written by `FUN_0003B1A0()`:
		- `FUN_0003B1A0(1)` arms `DAT_000602AA = 1`
		- `FUN_0003B1A0(0)` does not arm it; it instead routes through `FUN_0003DE70(0x20000, 0)`
	- the currently traced `lv01s1` startup path still reaches the known `0x14F80 -> FUN_0003B1A0(0)` case, not a proven `FUN_0003B1A0(1)` path
- Current implication:
	- the exact bright final upper tail for `lv01s1` is still unresolved
	- but the evidence now argues more strongly that `FUN_00049640()` is **not** part of the traced normal startup baseline for this DAT, which makes the current default of keeping `0xE0..0xFF` on the bank-0 baseline more defensible than assuming a hidden switch to the last static bank

## 2026-05-10 - Exact loader type-`4` plane export now covers `DISPLAY` and `LEVINFO*`

- The old generic `--export-resource-planes` path is no longer a blanket disabled probe.
	- It is now narrowed to the currently validated loader type-`4` plane families: `DISPLAY`, `LEVINFO0`, `LEVINFO1`, and `LEVINFO2`.
	- `TEXTURE` still keeps its dedicated exporter.
	- Other families like `PAW` remain excluded because the code proof still shows queued helper/decoder execution rather than a direct width-times-height indexed plane read.
- Implementation details:
	- the exporter now renders only loader type-`4` blocks from a mixed payload group
	- it writes the same scene-level `default/` palette output tree plus grayscale and palette-variant diagnostics used by the other exact renderers
	- sequence entries that point at non-type-`4` blocks are skipped explicitly instead of being guessed as raw planes
- Validation on `lv01s1.dat`:
	- `LEVINFO1` exports cleanly as `1` exact type-`4` block and `1` frame with `0` skipped frames
	- `LEVINFO0` exports `1` exact type-`4` block and `5` frames with `25` skipped non-type-`4` sequence entries, which matches its `00 01 02 03 04 05` repeating sequence against a payload group that contains `0x02:42, 0x04:1`
	- `DISPLAY` exports its single exact type-`4` block with no sequence frames
	- rerunning `--export-known-renders` on `lv01s1.dat` now raises `Resources with exports` from `53` to `56`, with `0` exporter failures
- Updated exact coverage summary for `lv01s1.dat`:
	- `DISPLAY`: exported `type4 planes, type1 renders`, unresolved residual `0x03`
	- `LEVINFO0/1/2`: exported `type4 planes`, unresolved residual `0x02`
	- remaining unresolved families are now `BLOBS`/`DISPLAY` type `0x03`, `LEVINFO*` type `0x02`, `REACTOR` type `0x07`, and `END`/`STOP_BLOCK` type `0x00`

## 2026-05-10 - `REACTOR` loader type-`0x07` recovered as an exact particle/effect family

- The residual `REACTOR` type-`0x07` block is not another sprite plane or executable blit blob.
	- `BBREACTOR`'s spawn stub at `0x149B0` is another entity initializer built on `FUN_000136E0()` and `FUN_0002C2C0()`.
	- its object state at `0x14A30` advances through the handler rooted at `0x14CA0`.
	- state flow at `0x14F24` creates a child object with handler `0x14F90` and stores `resource->p04 + 0x13A4`, which is exactly `REACTOR` block `104`'s `Value24`, into the child at `+0x74`.
	- `0x14F90` then calls `FUN_0002A0CA()` on that pointer.
- `FUN_0002A0CA()` gives the block format directly:
	- it consumes a fixed `0x1000` records from the `0xA000`-byte region at block `104`'s `Value24 = 0x5EE94`
	- each record is `10` bytes:
		- `u16 x` in `9.7` fixed point
		- `u16 y` in `9.7` fixed point
		- `s16 dx`
		- `s16 dy`
		- `u8 colorIndex`
		- `u8 delayCounter`
	- active records plot one pixel at `(x >> 7, y >> 7)`, then update `x += dx`, `y += dy`, and `dy += 5`
	- the first `256` frames form one exact deterministic cycle across all `4096` records because the delay field spans `0..255` and each record fires once per cycle
- Important exactness result:
	- the first `256` frames for `lv01s1.dat` block `104` do **not** reach the later `FUN_0002BD08()` RNG reset path, so this first-cycle export is exact without needing any external/random runtime state
- DogKnife implementation:
	- new command: `--export-type7-effects <raw-dat> --resource REACTOR`
	- exporter path: `REACTOR/type7_effect`
	- emits `default/`, `grayscale/`, and palette-variant frame sets plus a cycle-coverage image using the shared scene-level palette logic
- Validation:
	- direct export on `lv01s1.dat` succeeded: `1` type-`7` block, `256` exact frames, `0` exporter failures
	- rerunning `--export-known-renders` now clears `REACTOR`'s residual unresolved loader family and reduces whole-DAT unresolved-resource count from `8` to `7`
- Updated unresolved set for `lv01s1.dat`:
	- `BLOBS`: residual `type 3`
	- `DISPLAY`: residual `type 3`
	- `LEVINFO0/1/2`: residual `type 2`
	- `END`, `STOP_BLOCK`: `type 0`

## 2026-05-10 - `DISPLAY` type-`3` is now split into one exact runtime family plus one still-substrate-dependent remap

- The `DISPLAY` initializer is now grounded too:
	- string xref `"DISPLAY"` leads to `FUN_0002FD20()`
	- that resolver stores the resource entry in `DAT_0008B9B4` and the payload-group base in `DAT_0008B9BC`
	- state `0x0B` selects the handler rooted at `0x2FA80`
- `DISPLAY` block `48` is now an exact runtime bar/gauge family, not a guessed standalone sprite:
	- `FUN_0002EFE0()` case `1/2` reaches `FUN_0002F5C0()`
	- `FUN_0002F5C0()` computes `filledWidth = (value + 0x1FFFFF) >> 21`
	- for `filledWidth > 0`, `FUN_0002F6A0()` copies exactly that many columns from type-`4` block `47`
	- for `filledWidth < 32`, the same function then patches block `48`'s `FUN_00028A00()` stream in place at payload offsets `+0x924..+0x938` and applies it at `x = filledWidth`
	- this yields a deterministic exact `0..32` width family over the proven block-`47` substrate
- `DISPLAY` block `35` is also now tied to a concrete runtime path, but its exact substrate is still unresolved:
	- `FUN_0002EDA0()` uses payload offsets `+0x6B4/+0x6B8`, which are block `35`'s stream/page pair
	- it draws type-`1` blocks `17..24` at `+5` x-strides first, then applies block `35` up to eight times for the remaining slots
	- this proves block `35` is live and shared through `FUN_00028A00()`, but not yet which exact pre-drawn HUD substrate it remaps over in the final framebuffer
- DogKnife implementation:
	- new command: `--export-display-type3 <raw-dat>`
	- exporter path: `DISPLAY/type3_runtime`
	- emits the exact block-`48` width matrix (`33` variants for widths `0..32`) in `default/`, `grayscale/`, and palette-variant trees
	- metadata now documents the separate block-`35` runtime path instead of pretending `DISPLAY` type-`3` is fully closed
- Validation:
	- direct export on `lv01s1.dat` succeeded: `33` exact block-`48` width variants, `1` documented-but-unrendered block (`35`)
	- rerunning `--export-known-renders` succeeds with `0` failures and keeps the unresolved-resource count at `7`
	- `known_render_summary.txt` now reports `DISPLAY: exported=type4 planes, type1 renders, DISPLAY type3 runtime unresolved=0x03 failure=<none>`
