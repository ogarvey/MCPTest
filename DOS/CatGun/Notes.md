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
