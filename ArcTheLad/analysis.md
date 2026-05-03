# Arc The Lad Analysis

## Current Ghidra Anchors

- Verified the likely Arc executable is open in Ghidra as `SLUS_012.24` on instance `8193`.
- The previously active instance was `SLUS_123.45`, so make sure analysis is being done against `SLUS_012.24` before following any addresses below.
- Quick string scan found no literal `.IMG` or `.DAT` filenames in the executable.
- Named `open` and `read` functions do exist, but they are thin syscall stubs only:
  - `open` at `801793e4`
  - `read` at `801793a4`
- Useful graphics-side strings are present in the binary:
  - `timaddr=%08x\n` at `8011b330`
  - `GPU timeout:QUE=(%2d,%2d),CODE=(%d,%d,%08X)\n` at `8011b218`
  - `time out in decoding !\n` at `8011ad58`

## Working Hypothesis

Arc The Lad probably does not keep `.IMG` / `.DAT` filenames as plain literals in the EXE. The game likely builds paths dynamically, uses file tables, or opens resources by index/offset rather than by hard-coded strings.

Because TIM/GPU-related debug strings are linked into the executable, the shortest route to the graphics loader is likely:

1. Find the TIM decode / image upload helpers.
2. Identify their first non-library caller.
3. Trace backward from that caller to the code that fills the source buffer.

That should expose whether the graphics come directly from an `.IMG` file, from a paired `.DAT` index, or from an additional decompression layer.

## New Findings

- The first genuinely useful image-side routine is `FUN_8011d804`.
	- It is not a raw file reader.
	- It takes a runtime graphics object, looks up a 4-byte sprite/atlas entry, resolves CLUT and TPage values, applies flip/semi-trans style flags, computes UV corners, and emits a textured quad primitive.
- The higher-level controller is `FUN_8011d09c`.
	- It walks a 2D grid of tiles/cells.
	- It reads 16-bit tile IDs from bank data.
	- It remaps those IDs through `DAT_801f3858`.
	- It emits textured quads by calling `FUN_8011d804`, or a special fallback path `FUN_8011d998` when the remapped tile ID is `0x3ff`.
- The bank/runtime setup path is now visible:
	- `FUN_801242e0` selects the active graphics bank from `DAT_801aea7c`.
	- `FUN_8011c768` iterates the bank descriptors under that root and creates runtime bank objects.
	- `FUN_8011c8bc` allocates one runtime bank object and stores the bank descriptor pointer at runtime object `+0x74`.
- The previously unidentified code around `8011bfc8` has now been recovered manually from raw MIPS bytes.
	- It is a bootstrap callback, not a direct file loader.
	- It clears local state flags, sets `DAT_801aea7c = 0x80118838`, `DAT_801aea80 = 0x800c8fc8`, and `DAT_801aea84 = 0x800ceffc`.
	- It clears `0x80119800` for `0x1400` bytes, then runs a sequence of renderer/state initialization routines.
	- The address `0x80118838` behaves like a RAM/work-buffer root rather than initialized ROM data.
- Re-check of `open` / `read` callers showed they are memory-card helpers, not the asset loader.
	- This strengthens the case that Arc loads graphics through a custom resource path, likely backed by CD reads and a relocation step.

## Recovered Population Path

- `FUN_8011fe98` copies a large fixed set of resource blocks into static buffers such as `DAT_80119c38` .. `DAT_8011a118`.
- Those copies come from `FUN_80175244(id)`.
- `FUN_80175244` does not read files.
	- It zeros scratch buffer `DAT_801ab988`.
	- It copies a `0x4c`-byte record from `DAT_8019fbe0 + id * 0x4c` into that scratch buffer.
	- The source bytes at `DAT_8019fbe0` are already present in memory and look like initialized EXE-side table data.
- Practical consequence: the specific `FUN_8011fe98` -> `FUN_80175244` path is currently populated from static tables, not from external `Sxxxx.DAT` / `Sxxxx.IMG` file reads.
- This likely means the path recovered so far is for built-in presets / UI / battle resources, while external scene resources are handled elsewhere.

## Callback Chain Result

- The `FUN_8012c340(param_1 == 10, DAT_801aa1ac != 1)` branch that queues `LAB_80157f90` does not lead to the scene graphics loader.
- Raw decoding of the undiscovered block at `80157f90` shows:
	- it calls `FUN_8017a614(0)` and `FUN_8013272c()`
	- sets `DAT_801f9ed0 = 2`
	- queues callbacks at `80157fe4` and `801682bc`
- The follow-up callback at `80157fe4` calls:
	- `FUN_8012c47c(1)` -> `FUN_8012acec(1, &DAT_801aa1d4)`
	- `FUN_8012c46c(1)` -> `DAT_801a98d8 = 1`
- `FUN_8012acec` builds a path rooted at `"BASLUS-01224ARC1-00"` and appends `"*"` before enumerating files.
- `FUN_8012adf8`, which is reached from the nearby `8012c7cc` state callback, opens `BASLUS-01224ARC1-00X` and reads `0x200` bytes.
- `FUN_8012a92c` uses `_card_info` and other card helpers.
- Practical consequence: this entire callback chain is memory-card/status handling plus UI/resource init, not the external scene DAT/IMG graphics loader.

## Likely Graphics Parsing Routine

The best current extractor target is the pair:

1. `FUN_8011d09c`
2. `FUN_8011d804`

Reason:

- `FUN_8011d09c` is the first Arc-specific routine that clearly interprets graphics content rather than just drawing generic PsyQ primitives.
- `FUN_8011d804` exposes the compact per-sprite entry format that must ultimately come from the game assets.
- Together they define enough format behavior to start building a C# test parser once the corresponding on-disk data block is found.

## Inferred Runtime Layout

These layouts are still partial, but they are now concrete enough to guide file hunting.

### Resource Root (`DAT_801aea7c`)

- `+0x04`: pointer to an array of bank descriptor pointers
- `+0x08`: active bank index
- `+0x10`: additional init-time data used later by `FUN_801242e0`

### Bank Descriptor (partial)

- `+0x04`: atlas metadata pointer
	- metadata bytes `+4/+5/+6/+7` behave like `tilesX`, `tilesY`, `tileW`, `tileH`
- `+0x08`: pointer to a 4-byte sprite entry table
- `+0x10`: pointer to a CLUT/TPage descriptor table or pointer array
- `+0x20`: mode/limit field used by `FUN_8011d09c`
- `+0x28`: pointer to 16-bit tilemap/frame data consumed by `FUN_8011d09c`

### 4-Byte Sprite Entry (`FUN_8011d804`)

- byte `0`: base U
- byte `1`: base V
- byte `2` low nibble: CLUT/TPage descriptor index
- byte `2` high nibble: CLUT palette-slot addend, applied to CLUT X in `0x10` steps before `GetClut(...)`
- byte `3` bit `0/1`: flip flags
- byte `3` bit `2`: likely semi-transparency-related flag

## Important Consequence For File Extraction

The routines above consume direct pointers, not file offsets. That still suggests a relocation step before `DAT_801aea7c` is handed to the renderer, but the current recovered initialization path is not itself the external loader.

So the likely sequence is now:

1. some higher-level loader or state callback populates a RAM buffer or table set
2. bootstrap code writes the root pointer to `DAT_801aea7c`
3. `FUN_801242e0` selects the active bank and builds runtime objects
4. runtime rendering eventually reaches `FUN_8011d09c` and `FUN_8011d804`

The unresolved question is where the non-static path fills that RAM root for external assets.

## Best Next Ghidra Target

The next high-value step is no longer the TIM string hunt, and it is no longer `8011bfc8` by itself.

It is:

1. find the higher-level callback / state path that eventually fills `0x80118838` or an equivalent resource buffer
2. identify where external scene assets diverge from the static-table path used by `FUN_80175244`
3. trace callback chains registered by the bootstrap and menu/state controller code until a true external population step appears

That code should expose the exact in-file layout needed for extraction.

## Best First File-Level Test

The newly added test files narrow the immediate target.

- `S1012.IMG` starts with `0x56414270` (`VABp`) and is almost certainly a PlayStation sound bank, not graphics.
- `S1011.IMG` has no obvious header and begins with dense nibble-like patterns such as `22344333`, which is much more plausible for packed 4bpp image data.
- `S1011.DAT` and `S1012.DAT` share the same header start:
	- `0xC0000023`
	- `0x2020150C`
	- followed by small 16-bit values like `0008, 0009, 000A, 000B, ...`
- That makes `S1011.DAT` + `S1011.IMG` the better graphics candidate pair for future extractor tests.

Even before the external loader is fully understood, there is now a practical test shape for that pair.

1. Find a file that appears to contain repeated 4-byte sprite entries plus small metadata blocks matching `tilesX`, `tilesY`, `tileW`, `tileH`.
2. Build a small C# probe that reads candidate 4-byte entries and reproduces the UV logic from `FUN_8011d804`.
3. Validate whether repeated entries produce sensible sprite rectangles and flip behavior.
4. If a companion 16-bit tilemap exists, reproduce the ID remap and fallback behavior from `FUN_8011d09c`.

This will not finish the extractor, but it is enough to discriminate between “generic image blob” and “pointer-relocated sprite bank” very quickly.

## Probe Results

- Added a dependency-free probe script at `ArcTheLad/probe_arc_scene.py`.
- Running it on `S1011.DAT` + `S1011.IMG` produced the first useful on-disk candidates.
- The earlier `0x10000`-byte 8bpp page assumption was wrong.
	- Re-reading `FUN_8011d804` showed that sprite-entry byte `2` high nibble is added to CLUT X before `GetClut(...)`.
	- That is palette-slot behavior, not direct page selection.
	- This is consistent with a 4bpp-style path, not an 8bpp-style path.
- `S1011.IMG` is exactly `0x30000` bytes, which divides cleanly into six `0x8000` parts.
	- The current probe writes six raw parts plus six `256x256` grayscale placeholder views under `ArcTheLad/S1011_probe_split/`.
	- This is the best current physical split of the IMG container.
- `S1011.DAT` contains highly structured 4-byte runs that look like sprite/atlas descriptors, not random data.
	- Strongest candidate rows start at `0x000958`, `0x0009D8`, `0x000A58`, and `0x000AD8`.
	- Example records:
	  - `0x000958`: `(00,00,21,00), (20,00,21,00), (40,00,21,00), ...`
	  - `0x0009D8`: `(00,20,21,00), (20,20,21,00), (40,20,21,00), ...`
	  - `0x000A58`: `(00,40,21,00), (20,40,21,00), (40,40,21,00), ...`
- Those rows look like a regular UV grid in exactly the 4-byte shape consumed by `FUN_8011d804`.
- Current interpretation of sprite-entry byte `2`:
	- low nibble: index into the per-bank CLUT/TPage descriptor table
	- high nibble: palette-slot addend for CLUT X, in `0x10` steps
- Practical consequence: the DAT rows are descriptor grids, but they are not direct raw IMG offsets by themselves. The actual texture-page mapping still depends on the low-nibble-selected descriptor entry.
- Best current extractor hypothesis:
	- `S1011.DAT` is a relocated RAM image with base `0x800CF000`, not a plain offset table.
	- The best descriptor bank is at `DAT+0x4984C`, with a matching descriptor-pointer array at `DAT+0x498EC`.
	- The first four records resolve to palettes at `DAT+0x2138`, `DAT+0x2338`, `DAT+0x2538`, and `DAT+0x2738`, with page modes `4bpp, 4bpp, 8bpp, 8bpp`.
	- `S1011.IMG` is stored as one row-sliced atlas: each scanline is `0x80 + 0x80 + 0x100 + 0x100 = 0x300` bytes, which matches the full `0x30000` IMG size over `0x100` rows.
	- The correct probe path is therefore: resolve the relocated descriptor bank, deinterleave the IMG per scanline into four logical pages, then apply the DAT-resident CLUT data.

## Validation Against Added Test Files

- The added `S10xx` pairs split into at least three families.
- Confirmed graphics-family pairs that match the current row-sliced atlas model:
	- `S1011`, `S1023`, `S1031`, `S1072`
	- These all have `IMG` size `0x30000`, no `pBAV` / `VABp` audio-bank header, and a compatible pointer-backed descriptor bank that the current probe accepts.
	- The current probe now writes decoded previews as RGBA PNG rather than BMP so texture transparency survives export.
	- Transparency rule used for previews: texel / CLUT color `0x0000` -> fully transparent, PSX bit15 set -> alpha `0x80`, otherwise opaque.
	- Verified alpha-bearing outputs on `S1011` and `S1072`; `S1023` and `S1031` also carry true transparent texels, but no semi-transparent ones were observed in the checked row sheets.

- Leading-`pBAV` same-stem pairs:
	- `S1012`, `S1013`, `S1021`, `S1022`, `S1032`, `S1041`, `S1051`, `S1071`
	- Their `IMG` files start with `pBAV` / `VABp`-style audio-bank headers, but the same-stem files also carry trailing graphics payloads behind that leading audio container.
	- The old probe misclassified them because it tried to decode the full `IMG` buffer directly. The updated parser now recognizes the leading VAB container and selects the exact descriptor-sized graphics trailer.
	- Verified trailer-backed same-stem decode offsets so far:
	  - `IMG+0x1F000`: `S1012`, `S1013`, `S1021`, `S1022`, `S1032`, `S1071`
	  - `IMG+0x2F000`: `S1041`, `S1051`
	- Practical consequence: a leading `pBAV` header does not mean “audio only”; DAT-backed graphics can still live at the end of the same file.
- Current unresolved outlier:
	- `S1061`
	- `IMG` size is `0x30000` and it is not `pBAV` / `VABp`-like, but the current pointer-backed descriptor-bank scan does not find the same `DAT+0x4984C` structure.
	- Treat this as a separate layout family for future investigation.

## Outlier Follow-up: S1051 and S1061

- The original “no output” diagnosis for `S1051` / `S1061` turned out to be partly a probe limitation.
- Both files do contain a pointer-backed descriptor bank rooted at `DAT+0x4984C`, but it has `3` records rather than `4`.
	- Pointer array is therefore at `DAT+0x498C4` instead of `DAT+0x498EC`.
	- The probe has now been updated to accept both `4`-record and `3`-record banks.
- `S1051`
	- `IMG` begins with `pBAV` / `VABp` audio-bank bytes, but the same-stem file also carries a trailing graphics payload.
	- The recovered `3`-record bank describes `0x20000` bytes (`4bpp, 4bpp, 8bpp`), and the updated probe now decodes that exact trailer from `IMG+0x2F000` as a `512`-bytes/row row-sliced atlas.
	- This means the earlier “wrong resource type” conclusion was too strong; the real issue was that the probe was treating the whole container as audio instead of selecting the graphics trailer.
- `S1061`
	- `IMG` is non-`VABp` and the recovered `3`-record bank describes `0x28000` bytes as a row-sliced atlas (`8bpp, 8bpp, 4bpp`).
	- The remaining unexplained tail is `0x8000` bytes, exactly one `4bpp 256x256` page worth of data.
	- However, no matching fourth descriptor record or fourth palette block was found in the obvious `DAT+0x4984C` family; `DAT+0x21D8` is zero-filled and there are no pointers to `0x800D11D8`.
	- Partial decode of the first `0x28000` bytes was rendered successfully to `S1061_probe_partial`, so `S1061` is best treated as “real graphics bank plus one unresolved extra segment,” not as a total probe failure.

## Ghidra-Grounded DAT Structure Notes

- The code path around `FUN_801242e0 -> FUN_8011c768 -> FUN_8011d09c -> FUN_8011d804` explains the DAT-side structure much more cleanly than the old probe heuristics.
- Important correction: the `0x28` records at `DAT+0x4984C` are texture descriptors, not the top-level bank objects used by the compositor.
	- `FUN_8011d804` follows `bank + 0x10` to a pointer array of these texture descriptors.
	- Each 4-byte sprite entry uses packed byte `2` low nibble as the texture-descriptor index and high nibble as the CLUT X addend / palette slot.
- Root object layout at relocated RAM address `0x80118838` (`DAT+0x49838` in the extracted files):
	- `root + 0x00` -> pointer to the texture-descriptor pointer array (`S1011: DAT+0x498EC`, `S1061: DAT+0x498C4`)
	- `root + 0x04` -> pointer to the bank-descriptor pointer array (`S1011: DAT+0x49984`, `S1061: DAT+0x49958`)
	- `root + 0x08` -> active bank index
	- `root + 0x0C` -> script/config tables consumed by `FUN_801208bc`
- `FUN_8011c854` iterates `root + 0x00` and copies `0x200` bytes from each texture descriptor's first pointer into the CLUT cache via `FUN_80152a80`; this is the DAT-side palette upload path.
- Bank descriptor layout confirmed by `FUN_8011c8bc` / `FUN_8011d09c`:
	- `bank + 0x04` -> size/meta block
	- `bank + 0x08` -> 4-byte sprite-entry table
	- `bank + 0x10` -> texture-descriptor pointer array
	- `bank + 0x18` -> optional tile-index remap script (null in `S1011` and `S1061`); each live record is 12 bytes: target tile id, sequence pointer, current sequence step, current repeat counter
	- `bank + 0x28` is populated at runtime as `size_ptr + 8`, i.e. the 16-bit tile-map pointer
- Important extractor correction:
	- The sprite-entry byte `2` low nibble must be resolved through each bank's own `bank + 0x10` pointer array, not treated as a global descriptor index.
	- `probe_arc_scene.py` now follows that bank-local pointer chain directly, which matches `FUN_8011d804` more closely than the earlier shortcut.
	- The bank parser no longer rejects banks just because `bank + 0x10` differs from the root-level texture array at `root + 0x00`.
	- Revalidated after this correction on `1/S1011` and `22/S2052`; the remaining extraction gap is now piece grouping / remap semantics, not descriptor-slot resolution.
- The size/meta block contains:
	- bytes `+4/+5` = tile-map width/height in tiles
	- bytes `+6/+7` = tile width/height in pixels
	- `+8` onward = 16-bit tile IDs consumed by `FUN_8011d09c`
- `FUN_8011cdec` initializes the tile-index remap table `DAT_801f3858` to identity. `FUN_8011cfc4` only overwrites it when `bank + 0x18` points to a remap script.
	- Each 12-byte remap record writes `DAT_801f3858[target] = sequence[current_step + 1]`, then advances the in-record step/counter state for the next frame.
	- Offline extraction can therefore treat the DAT-side `current_step` as the first-frame remap state without inventing a heuristic table format.
	- For `S1011` and `S1061`, `bank + 0x18` is null, so the low 12 bits of the tile IDs are already direct sprite-entry indices.
	- Confirmed remap-bearing scene samples now include `21/S2021`, `21/S2022`, `23/S2061`, `31/S3031`, `32/S3061`, `6/S6031`, `8/S8031`, `B/SB041`, `C1/SC011`, and `F/SF071`.
	- `probe_arc_scene.py` now resolves the current remap step directly from that 12-byte runtime record layout and was revalidated on `21/S2021`.

- `FUN_8011d09c` uses sprite-entry flag pattern `(flags & 0x0C) == 0x08` as an occupancy-marker rule when bank flag `0x04` is clear.
	- The rule is not a special “texel 0 becomes opaque” mode; `FUN_8011d804` does not interpret bit `0x08` that way.
	- The runtime-style occupancy mask is now applied in the main bank draw paths in `probe_arc_scene.py`, instead of being left in an unused diagnostic variant.
	- Revalidated on `1/S1023`, where the missing `0x08` masking path was the main remaining renderer gap.

## Why S1061 Output Was Wrong

- `S1061` is not failing because the graphics data is absent; it is failing because the current probe composes the output from guessed “regular row” runs instead of the bank-selected tile map and sprite table that the game actually uses.
- Active bank for `S1061` is bank `0`.
	- size/meta block: `DAT+0x290`
	- sprite-entry table: `DAT+0x3D8`
	- texture-descriptor pointer array: `DAT+0x498C4`
	- tile-map shape: `10 x 8` tiles of `32 x 32` pixels
- The active `S1061` tile map at `DAT+0x298` contains direct indices like `0001, 0002, 0003, ...`; there is no remap script and no `0x3FF` fallback tile in the active bank.
- The sprite-entry table at `DAT+0x3D8` uses entries like `(00,00,01,08), (20,00,00,08), ...`. The old probe heuristics wrongly rejected or ignored many of these because they assumed `flags < 8`, but `FUN_8011d804` only interprets bits `0`, `1`, and `2` directly and does not reject bit `3`.
- This means the current `regular_rows_*` / guessed-row outputs are not grounded in the runtime bank structure for `S1061`; a correct extractor should compose from `bank.size_ptr + 8` tile IDs and `bank.sprite_ptr`, not from pattern-matched row runs elsewhere in the DAT.
- After switching the probe to active-bank composition, `S1061` now renders a plausible `320x256` scene sheet from bank `0`.
	- Output path: `S1061_probe_mixed/scene/active_bank_00_320x256.png`
	- The recovered layout is `row-sliced atlas (640 bytes/row used, 0x80 bytes/row ignored from 0x300-byte rows)`.
	- Active bank only uses texture descriptors `0` and `1` (both `8bpp`), so the remaining `0x8000` tail and the unused `4bpp` descriptor are not part of the active scene path.

## Full Validation Sweep After Active-Bank Composer

- Re-ran the updated probe across all `S*.DAT` / `S*.IMG` pairs.
- Graphics-family pairs now producing active-bank scene sheets:
	- `S1011` -> `scene/active_bank_01_384x672.png`
	- `S1023` -> `scene/active_bank_01_320x512.png`
	- `S1031` -> `scene/active_bank_01_896x352.png`
	- `S1061` -> `scene/active_bank_00_320x256.png`
	- `S1072` -> `scene/active_bank_02_320x384.png`
- Audio/non-graphics pairs still correctly produce no scene output:
	- `S1012`, `S1013`, `S1021`, `S1022`, `S1032`, `S1041`, `S1051`, `S1071`
	- All still fail on descriptor-backed page-byte total versus `IMG` size and/or `VABp` payload detection.
- Visual validation snapshot:
	- `S1011`: coherent cliff / stair scene, looks good.
	- `S1031`: coherent cliff / bridge scene, looks good.
	- `S1061`: now clearly scene-like and much improved, but still has a missing / clipped region at the upper-left edge.
	- `S1072`: scene-like snowy path composition, but still contains unresolved gray masked / placeholder-looking shapes near the top.
	- `S1023`: no longer scrambled, but reads more like a sparse props / object layer than a fully composed room background; may need another bank/layer or additional script handling.

## Multi-Bank Composite Follow-up

- Added a diagnostic multi-bank compositor to `probe_arc_scene.py`.
	- It now writes `scene/all_banks_desc_...png` and `scene/all_banks_asc_...png` whenever more than one bank is present.
	- The compositor is alpha-aware, so fully transparent texels no longer erase already drawn layers during composition.
- `FUN_8011c768` still creates bank runtime objects from bank index `3` down to `0`, but visual validation shows the plausible final composition for the tested cases is bank-layering in ascending bank index order.
- `S1023`:
	- `active_bank_01_320x512.png` was only a sparse object layer.
	- `all_banks_asc_320x512.png` produces a coherent room/interior scene.
	- `all_banks_desc_320x512.png` buries furniture/props under the wall/floor layer, which looks wrong.
	- Conclusion: `S1023` is a real multi-bank composition case.
- `S1072`:
	- bank `1` is not corruption; it is a separate overlay layer containing cloud / gate elements.
	- `all_banks_asc_320x384.png` is much more plausible than the active-bank-only output and than the descending-order composite.
	- One large dark circular artifact remains near the top-center, so the secondary bank is correct in principle but still not fully explained.
- `S1061`:
	- combining all banks does not cleanly solve the remaining left-edge / upper-left defect.
	- the extra bank content looks more like alternate overlay/effect geometry than a missing background layer.
	- Conclusion: keep treating `S1061` primarily as an active-bank scene for now; its remaining defect needs a different explanation.

## Runtime Viewport Follow-up

- `S1023` transparency issue is still unresolved, but the previous `CLUT[0]` theory was not a good direction.
	- The earlier ascending-order `all_banks` output is still the best visual match according to the current validation target.
	- The later experiment that treated indexed texel value `0` as visible via `CLUT[0]` was reverted.
	- Working assumption restored: indexed value `0` remains transparent in the current probe for 4bpp/8bpp textured pages.
- Ghidra-grounded correction from `FUN_8011d09c`:
	- The bank tile map is not rendered directly as a final scene sheet.
	- Runtime draws a `320x240` viewport over the map using the bank descriptor scroll fields at `bank + 0x1C` / `bank + 0x1E`.
	- The draw loop skips the outer row/column and samples map tiles at `(scroll_tile + view_tile - 2)`.
	- For `32x32` tiles this produces a `13 x 11` draw window with offscreen margin tiles for scrolling.
- Probe update:
	- `probe_arc_scene.py` now writes `scene/runtime_bank_XX_320x240.png` for the active bank using the initial scroll values recovered from the bank descriptor.
	- It also writes `scene/runtime_all_banks_desc_320x240.png` and `scene/runtime_all_banks_asc_320x240.png` for diagnostic multi-bank runtime compositing.
- `S1023` specifics:
	- All bank descriptors use initial scroll `(0, 224)`; bank `3` additionally sets the `+0x20` depth-mode field to `1`.
	- Root script tables at `root + 0x0C` are present but empty (`-1` terminator immediately), so the scene is not being driven by the `FUN_801208bc` root-script path.
	- Runtime-viewport bank subset testing is still useful diagnostically, but it should not override the stronger visual result from the full ascending-order `all_banks` composite.
	- Current conclusion:
		- bank `0` should not be suppressed.
		- the previously missing `0x08` sprite-flag occupancy path from `FUN_8011d09c` is now implemented in the main probe renderer.
		- the best current presentation for `S1023` remains the ascending-order `all_banks` output; remaining cleanup is now about export shape and optional animation stepping, not that flag path.

## Full Data Drop Notes

- The newly added game data is not a simple one-`DAT`/one-`IMG` archive.
- Current inventory under `ArcTheLad/`:
	- `240` `.DAT` files
	- `189` `.IMG` files
- Important shared-IMG families:
	- `E1` through `E5`: each directory has `10` `.DAT` files but only one shared `.IMG` (`SE01.IMG` .. `SE05.IMG`).
	- Those shared `E*` images are `VABp`-style audio payloads, not graphics.
- Several other directories also have DAT/IMG count mismatches (`9`, `B`, `C1`, `D`, `F`), so pairing logic will need to account for shared or missing image resources rather than assuming identical stems everywhere.

## S1023 Transparency Follow-up

- Added a diagnostic black-base output path to `probe_arc_scene.py` for all scene renders.
	- The probe now writes both the original alpha-preserving scene PNG and a `_blackbase` companion with the same pixels composited over opaque black.
	- This keeps the original transparency information intact while making it easy to test whether the remaining `S1023` issue is just transparent texels over an empty background.
- Current used-palette check across the known graphics-family samples (`S1011`, `S1023`, `S1031`, `S1061`, `S1072`) found only one case where a used 4bpp palette has both nonzero `CLUT[0]` and zero-index texels:
	- `S1023` bank `0`, descriptor `3`, palette slots `10` and `13`
- Localized bank-0 follow-up for `S1023`:
	- The repeated border sprites at indices `3` and `12` use descriptor `3`, palette slot `13`, and contain `847` zero texels out of `1024` with nonzero `CLUT[0] = 0x842`.
	- Those sprites occur at the left and right map borders (`x = 0` and `x = 9`, `y = 8..14`) and remain the strongest candidates for the residual transparency mismatch.
	- The upper patterned region is mostly formed by solid flagged tiles using palette slot `10`, so the visible issue is not explained by a blanket “all flagged bank-0 tiles should fill index 0” rule.

## Wider Sample Outside Folder 1

- Ran the current same-stem DAT/IMG probe across all pairable folders outside `ArcTheLad/1`.
	- Batch size: `170` same-stem pairs processed.
	- New rendered-scene hits: `9` pairs.
	- Current exporter behavior: scene folders are now trimmed to a single final output, `scene/all_banks_asc_*_blackbase.png`.
	- The older active-bank, runtime, descending, masked, and flag-0x8 scene variants were useful diagnostics during reverse engineering, but they are no longer written to `scene/` by default.
	- Important correction: the low hit count in this sweep is partly a pairing artifact, not a scene-decoder failure.
	- Folder `22` proves the point: `S2052` through `S205B` all carry the same pointer-backed scene-bank structure as `S2051`, `S2053`, and `S205C`, and they successfully decode against any of the three non-`VABp` IMGs in that folder.
	- Practical consequence: same-stem DAT/IMG matching is too strict for broader batch coverage; folder-level shared-image pairing is required.
- Folders with at least one rendered scene:
	- `21`: `S2033`
	- `22`: `S2051`, `S2053`, `S205C`
	- `4`: `S4031`
	- `D`: `SD013`, `SD014`
	- `F`: `SF051`, `SF0B1`
- Folders with same-stem pairs but no rendered scene under the current probe:
	- `23`, `31`, `32`, `5`, `6`, `7`, `8`, `9`, `B`, `C1`, `C2`
- `E1` through `E5` were not included in this same-stem sweep because they use one shared IMG per directory rather than one IMG per DAT.
	- Those shared `SE0x.IMG` files still look like `VABp`-style audio payloads, so they remain low-priority for graphics extraction.
- Current sprite-output gap:
	- The probe now exports bank-used contact sheets under each pair's `sprites/` directory, but these are scene-bank quads from the `FUN_8011d09c -> FUN_8011d804` path, not confirmed character / actor sprites.
	- Ghidra cross-reference check: `FUN_8011d804` is only called from `FUN_8011d09c`, so this path is scoped to the scene-bank compositor.
	- Newly identified separate textured-sprite subsystem: `FUN_801693d4` dispatches to `FUN_80169414` / `FUN_801698f4`, which build textured packets from a different data structure entirely and are better candidates for UI / actor / non-scene sprite work.
	- Practical consequence: the current `sprites/` folders are still useful diagnostics, but they should not be presented as recovered character sprites.
- Preferred wider-sweep outputs to inspect first are the new black-base ascending composites:
	- `21/S2033_probe_mixed/scene/all_banks_asc_320x256_blackbase.png`
	- `22/S2051_probe_mixed/scene/all_banks_asc_512x256_blackbase.png`
	- `22/S2053_probe_mixed/scene/all_banks_asc_512x256_blackbase.png`
	- `22/S205C_probe_mixed/scene/all_banks_asc_512x256_blackbase.png`
	- `4/S4031_probe_mixed/scene/all_banks_asc_896x352_blackbase.png`
	- `D/SD013_probe_mixed/scene/all_banks_asc_320x384_blackbase.png`
	- `D/SD014_probe_mixed/scene/all_banks_asc_320x384_blackbase.png`
	- `F/SF051_probe_mixed/scene/all_banks_asc_320x768_blackbase.png`
	- `F/SF0B1_probe_mixed/scene/all_banks_asc_512x256_blackbase.png`

## pBAV Trailer Correction

- The current Ghidra-grounded correction is that `pBAV` / `VABp` marks a leading audio container, not the end of the graphics story for the file.
	- `FUN_80186af4` / `FUN_8018a9b0` explain the leading VAB head/body parsing.
	- The game-side VAB wrapper does not infer a split on its own: `FUN_80128aa8(headPtr, bodyPtr, vabId)` receives separate header/body pointers from its caller, so the file-family split is caller-driven.
	- Current wrapper-side recovery from the undefined caller stubs:
		- `0x80129EC0` calls `FUN_80128AA8(0x800C4000, 0x800C9000, 0)` after queuing callback `0x80139F00`.
		- `0x8012A1C4` calls `FUN_80128AA8(0x800C9000, 0x800CF000, 1)` unless `FUN_80129FD0` says the current scene id is in the hardcoded 18-entry skip list at `DAT_8019416C`.
		- `0x8012A120` and `0x8012A2C0` are scene-indexed staging helpers that seed global load metadata from the `0x80193384` / `0x80193388` table families before queuing callback `0x8013A380`.
	- The small loader callback stack around `DAT_801AA3EC` is now identified:
		- `ResetLoadCallbackStack` = `FUN_8011C670`
		- `SetCurrentLoadCallback` = `FUN_8011C680`
		- `PushLoadCallback` = `FUN_8011C6A8`
		- `PopLoadCallback` = `FUN_8011C6F8`
		- `RunCurrentLoadCallback` = `FUN_8011C728`
- Important correction after comparing `S2052` against the known-good `S2053` / `S2051` / `S205C` family:
	- `S2052.IMG` is a leading-`pBAV` container, while `S2051.IMG`, `S2053.IMG`, and `S205C.IMG` are plain `0x30000` graphics IMGs.
	- The actual scene graphics block for this family is the full trailing `0x30000` bytes, not the trimmed descriptor-sized tail.
	- Direct binary proof: `S2052.IMG[0x1F000:]` is byte-identical to `S2051.IMG`, `S2053.IMG`, and `S205C.IMG`.
	- The old broad batch was wrong because it started the trailing payload at `IMG+0x27000`, chopping away the row padding that belongs to the full `0x300`-byte source stride.
	- `probe_arc_scene.py` now recovers the full trailing scene graphics block for these `pBAV` files and emits normal `pages/`, `scene/`, and scene-bank `sprites/` outputs again.
- Current validated same-stem `pBAV` rule for the scene-family files:
	- leading `pBAV` / `VABp` audio container at the front
	- full trailing scene graphics block at `IMG+(file_size-0x30000)`
	- recovered plain-image source layout: `0x300` bytes per row over `0x100` rows
	- example validated decodes: `S2052`, `SD011`, `SD012`, `SD015`, and the folder `1` `pBAV` cases listed above.
- Same-stem page-decode coverage outside the shared-IMG `E1`-`E5` directories is now much broader than the earlier 9-scene sweep suggested.
	- Current same-stem page-decode count: `137` successful DAT/IMG pairs.
	- Full regeneration did complete successfully across those decodable pairs, but the intermediate `pBAV` pass that trimmed to descriptor bytes was wrong for files like `S2052`; corrected outputs now use the full trailing graphics block.

## False Leads Removed From The Actor-Sprite Search

- `FUN_801693d4` is not the missing external scene actor loader.
	- It dispatches on object byte `+0x34` to `FUN_80169414` or `FUN_801698f4`.
	- The live objects currently passed in (`DAT_8019b2e0`, `DAT_8019b33c`, `DAT_8019b398`, `DAT_8019b3f4`) already contain hard-wired bitmap/palette pointers such as `0x8019b0b0`, `0x8019b16c`, `0x801b4008`, and `0x801b9968`.
	- `FUN_80169414` / `FUN_801698f4` consume those built-in tables directly to emit flat or transformed textured packets. No scene-file population step is visible on this branch.
	- `FUN_8016158c` is a shared primitive constructor for the same `0x801b4008` / `0x801b9968` buffers, which reinforces that this path is a static overlay / HUD object system.
- `FUN_8011dc70 -> FUN_8011e078 -> FUN_8011e7c0 -> FUN_8011ea88` is also not the actor-asset path.
	- `FUN_8011e078` prebuilds packet arrays in `DAT_801f6350` / `DAT_801f65d0` using a constant `GetTPage(0,0,0x140,0)` and `GetClut(0x20,0x1e0)`.
	- `FUN_8011ea88` only applies static UV quads from `DAT_80192940` onto those prebuilt packets.
	- This looks like a fixed-table animated effect / overlay path, not dynamic character-sprite loading.
- Practical consequence: the next actor-sprite search should move away from these renderer families and back toward the code that populates scene/runtime objects from external resources or scene-script state.

## Scene Record Table Correction

- The earlier shorthand about separate `0x80193384` / `0x80193388` table families was too loose.
	- Current best fit is one scene-indexed `12`-byte record table starting at `0x80193384`.
	- Helper `0x8012A040` reads `record.word0` via `0x80193384 + scene*12`.
	- Helper `0x8012A240` reads `record.word1` via `0x80193388 + scene*12`, i.e. the second word of that same `12`-byte record.
	- The raw words at `0x80193384` look like repeated triples of `[small id, small id, KSEG0 pointer]`, e.g. `(0x4C2, 0x42D, 0x801304D0)`, `(0x5F6, 0x561, 0x8015875C)`, `(0x6EB, 0x656, 0x8015875C)`.
- Current record model:
	- `word0`: sibling per-scene key used by the `0x8012A040` path
	- `word1`: graphics/DAT-side key used by the `0x8012A240` path
	- `word2`: scene-specific handler / descriptor pointer, not another small file id
- Practical consequence:
	- `0x8012A240` is now the highest-priority graphics staging branch, because it reads `record.word1`, caches it, and stages into `0x800CF000` with size/budget `0x4A800` before pushing callback `0x8013A380`.
	- `0x8012A040` still matters as the sibling branch for `record.word0`, but it is no longer the best candidate for the missing scene graphics path.
	- The third record word should be treated as runtime interpretation / handler state unless later code proves it influences asset location directly.
- Best next check from here:
	- recover the common callback path around `FUN_8013A35C` / internal label `0x8013A380` and confirm that `record.word1` alone determines the external block staged into `0x800CF000`.
	- once that is confirmed, bulk extraction can prioritize enumerating `record.word1` values while treating `record.word2` as post-load scene logic rather than file selection.
	- Newly confirmed successful folders: `1`, `21`, `22`, `23`, `31`, `32`, `4`, `5`, `6`, `7`, `8`, `9`, `B`, `C1`, `C2`, `D`, `F`.
	- Folder `22` correction: `S2052.IMG` contains the same full scene graphics block as the plain graphics IMGs `S2051.IMG`, `S2053.IMG`, and `S205C.IMG`; it is no longer a withheld case.
	- Folder `D` correction: `SD011`, `SD012`, `SD013`, `SD014`, `SD015`, `SD021`, `SD031`, `SD041`, and `SD071` now decode as same-stem pairs.
- Current single exporter outlier after the full pass: `D/SD041`.
	- `SD041` decodes descriptor-backed pages and writes page previews, but no graphics root currently matches the recovered descriptor bank, so there is still no bank-grounded `scene/` or `sprites/` output for that pair.
	- Treat `SD041` as a remaining layout / root-discovery exception rather than a pBAV trailer failure.
- Remaining gap:
	- This update fixes a large part of the old undercount, but it does not yet solve every shared-IMG directory or add dedicated sprite-sheet export.

## Ghidra Plan

1. Confirm the target binary.
	- In Ghidra, verify the active program is `SLUS_012.24`.
	- Do not mix addresses from `SLUS_123.45` with Arc analysis.

2. Use graphics code as the entry point, not filenames.
	- Start from the `timaddr=%08x` string at `8011b330`.
	- If normal xrefs are missing, search for the scalar/address value `0x8011b330` in instructions. On PSX this may appear as a `lui` / `addiu` pair rather than a direct string reference.
	- Repeat the same process for `8011b218` and `8011ad58`.

3. Identify and label PsyQ library helpers.
	- Any function that only validates TIM data, checks GPU state, or uploads image data to VRAM is probably library-side support code.
	- Rename obvious helpers once identified so the call tree becomes readable.
	- Example already confirmed: `FUN_8017c778` is a GPU timeout check, not the game asset loader.

4. Walk upward to the first game-owned caller.
	- From each TIM/GPU helper, inspect the caller list or call graph.
	- Stop at the first function that does one or more of these:
	  - passes a RAM buffer and size into the helper,
	  - selects an asset by ID,
	  - computes offsets / lengths,
	  - performs decompression or buffer transforms before upload.

5. At that caller, trace the source buffer backward.
	- Determine whether the image bytes come from:
	  - `read` / `open` style file access,
	  - CD sector reads,
	  - a packed archive entry table,
	  - or an in-memory decompression stage.
	- Record all structures involved: file table entries, offsets, lengths, flags, compression markers, palette metadata.

6. Look for TIM signatures in RAM-parsing code.
	- Search for code that checks for TIM magic `0x10` or parses CLUT / image block headers.
	- If found, that function is a strong candidate for the handoff between archive parsing and PlayStation graphics upload.

7. Separate archive parsing from rendering support.
	- The goal is not to fully understand the GPU upload path.
	- The goal is to find the boundary where raw archive bytes become a TIM-like buffer or decompressed bitmap payload.
	- That boundary is what the C# extractor needs to replicate.

8. Build the extractor from the data path, not the render path.
	- Once the archive reader and image decoder boundary is known, implement only:
	  - container discovery,
	  - entry table parsing,
	  - decompression if present,
	  - TIM or custom image decode.
	- Ignore VRAM upload and draw-environment code unless it encodes format details.

## Immediate Next Checks In Ghidra

1. Find the function that references `0x8011b330`.
2. Rename that function if it is clearly a TIM helper.
3. Enumerate its callers.
4. Pick the first caller that also manipulates source buffers or offsets.
5. Document any file table or decompression structure before moving further.

## What To Expect

## DAT_801B23D0 Command Stream Findings

- The most useful upstream parser found so far is `FUN_8015b458`.
	- It is a dense 16-bit command interpreter over the stream rooted at `DAT_801b23d0`.
	- `FUN_8015dec4` is the primitive reader: it returns the next signed 16-bit word and advances the cursor `DAT_801ff310`.
- Recovered sibling branches under `FUN_8015b458` now split into distinct content families:
	- `FUN_8015c754 -> FUN_8016c4c0 -> FUN_8016c520` is a framed byte-stream UI/text/layout parser, not the scene graphics path.
	- `FUN_8015ca98` reads `[selector, value]` and updates global transform state only.
		- It writes a short rotation-like triple at `DAT_801a975c` and an int translation-like triple at `DAT_80192ba4` with set/add semantics.
		- This is useful for runtime placement state, but not a direct asset record.
	- `FUN_8015cbdc` is a reset/init opcode.
		- It clears runtime tables at `DAT_801ff020` and `DAT_801afee0`, resets `DAT_801b23e0`, and seeds helper state through `FUN_8016b9b8`.
		- It does not consume any extra stream fields beyond the opcode itself.
	- `FUN_8015cc44` loads a deferred runtime packet, not asset content.
		- Stream layout: `[mode:u16][argCount:u16][arg0:u16]...`
		- It stores the packet at `DAT_801ff312/314/316...` and marks it live with bit `0x0002`.
	- `FUN_8015acf0` is the first consumer of that deferred packet.
		- Modes `0..6` are delay / slot-wait / object-live / controller or runtime gating behaviors.
		- This still stays on the runtime side rather than decoding stable asset records.

## First Extractable Object-Spawn Record

- The first clearly useful non-UI content branch under `FUN_8015b458` is `FUN_8015bda8`.
	- It consumes exactly 6 signed 16-bit words from `DAT_801b23d0`.
	- This branch allocates or selects a live object, attaches a callback, and writes placement/state fields.
- Current recovered stream layout for `FUN_8015bda8`:
	1. `slotIndex`
		- Selects `DAT_801afee0 + slot * 0x4c`.
		- Sets `slot+0x00 = 0x2b` and later stores the live object pointer at `slot+0x44`.
	2. `resourceId`
		- Passed to `FUN_80133974(0, resourceId)`.
		- This is the best current candidate for the object's sprite/model/resource reference.
		- The low byte is mirrored to `object+0x9c`.
	3. `coordA`
		- Offset against `DAT_801ff064+0x14`, then biased by `+0x100`, then written to `object+0x14`.
	4. `coordB`
		- Offset against `DAT_801ff064+0x18` and written to `object+0x18`.
	5. `variant`
		- Stored at `slot+0x02`.
		- The low byte is mirrored to `object+0x9d`.
		- `slot+0x04` and `slot+0x06` are forced to `0x0200`.
	6. `initParam`
		- Passed into `FUN_80140994(object, initParam)`.
		- `FUN_80140994` stores it at `object+0x4a`, clears local state, then forwards it through `FUN_80140928` -> `FUN_801408d4`.
		- That path seeds the object's first local queued state entry as `{ initParam, -1, -1 }` across the halfword lanes at `object+0x74`, `object+0x76`, and `object+0x78`.
		- Current best interpretation: this is an initial object state / behavior selector, not another resource or placement field.
- Allocation / runtime wiring details:
	- Descriptor resolution: `FUN_80133974(0, resourceId)`
	- Object allocation / lookup: `FUN_8013212c`, with fallback to `FUN_801320a8`
	- Installed runtime callback: `object+0x08 = 0x8015bcf4`
- Current best first-pass extraction model for this command is:
	- `{ slotIndex, resourceId, coordA, coordB, variant, initialStateSelector }`
- Remaining unknowns:
	- exact semantic name of `variant`
	- exact axis naming for `coordA/coordB`
	- exact vocabulary / meaning of the queued state selector values
- Best next target from this branch is the first consumer of the object-local queue rooted at `object+0x74`, with `FUN_801408d4` as the nearest upstream helper.

## Command VM Anchor Correction

- The file-backed scene command pointer used by the `DAT_801b23d0` interpreter is now grounded well enough to expose in the extractor.
	- The relocated slot at `RAM 0x801187FC` resolves cleanly in current scene-family DATs.
	- In validated samples (`S1011`, `S1023`, `S2052` / `S2053`) it resolves as `DAT+0x497FC -> 0x80116800 -> DAT+0x47800`.
	- This is the base of the 16-bit command-word array later copied into `DAT_801b23d0`.
- Important correction for offline parsing:
	- `FUN_80158948` stores the base pointer into `DAT_801b23d0`, but it does not force the current command cursor to `0`.
	- `FUN_8015dec4` actually reads from `DAT_801b23d0 + DAT_801ff310 * 2`.
	- The initial `DAT_801ff310` cursor is selected through `FUN_80158c64 -> FUN_80158e90`, which copies an active `0x22`-byte runtime state record into `DAT_801ff310...`.
	- Practical consequence: word `0` at the file-backed base is not guaranteed to be the first executed command, and some opcodes can rewrite the cursor later.
- Current extractor-safe interpretation:
	- The command-stream base is stable enough to report and compare across DAT families.
	- Full offline object-walk emission is still withheld until the file-backed origin of the active `0x22`-byte state record is recovered cleanly enough to seed `DAT_801ff310` correctly.
- `resourceId` naming is now sharper for future export:
	- `FUN_80133974(0, resourceId)` resolves through EXE pointer table `DAT_800d4044`.
	- Best current extractor label is `graphicResourceId` rather than a generic `resourceId`.

## Extractor Update

- `probe_arc_scene.py` now writes a `Scene command VM anchors` section into `probe_report.txt`.
	- It reports the relocated pointer slot at `RAM 0x801187FC`.
	- It reports the resolved command-word base and the first `32` words at that base.
	- It records the current Ghidra-grounded limitation: the true runtime start depends on the copied `DAT_801ff310` state record, so full offline command walking is not emitted yet.
- Focused validation completed after the update:
	- `1/S1011.DAT` + `1/S1011.IMG` still renders normally and now reports `DAT+0x497FC -> DAT+0x47800`.
	- `22/S2052.DAT` + `22/S2052.IMG` still follows the proven `pBAV` trailing-graphics path and now reports the same VM anchor section.

## Startup Record Selection Boundary

- Startup record selection is now separated cleanly from the file-backed command-base anchor.
	- `FUN_801589D8` is the pre-selection startup builder.
	- It seeds candidate records through `FUN_80158AA4` and `FUN_80158B40` before `FUN_80158C64` performs selection.
	- `FUN_8015B458` is not part of that pre-selection build; it is reached only later through `FUN_80158E90` after a record has already been selected.
- Recovered `FUN_80158C64` selection rule:
	- Scan primary bank `RAM 0x801AE6C8` first in ascending `0x22`-byte slots.
	- Choose the first record with `u16(record+0x02) & 0x0001`.
	- Only if no primary slot qualifies, scan secondary bank `RAM 0x801AFF00`, using bit `0x0010` from paired metadata records at `RAM 0x801AFEE0` with stride `0x4C`.
- Important correction for offline extractor work:
	- In validated scene-family DATs, these startup banks sit outside the relocated DAT image.
	- That matches the current Ghidra model that cold-start selection runs over runtime-built candidate records, not directly over bytes in the scene DAT.
	- Practical consequence: the extractor can report the startup-selection rule now, but should not guess the selected `0x22`-byte record or the initial command cursor from DAT data alone yet.
- Related runtime-state correction:
	- `DAT_801FF330` is a separate cursor-seed global written by opcodes `0x2B` / `0x3C` (with `0x3C` depending on `0x2A` var-table state).
	- Do not model this as `record+0x20`; that earlier hypothesis was wrong.
- Recovered builder inputs under `FUN_801589D8` now split cleanly:
	- `FUN_80158AA4` is effectively a zero-argument wrapper that seeds 5 fixed startup-source records through `FUN_80158B08(pointer,index)`.
	- Its direct sources are EXE-resident pointers at `RAM 0x801AEA8C` and `RAM 0x801AEAD0..0x801AEADC`, so this subset is reproducible offline without scene-file parsing.
	- `FUN_80158B08` materializes `0x4C`-stride records at `RAM 0x801FF020 + index*0x4C` with fixed fields `+0x00 = 3`, `+0x04 = 0`, `+0x06 = 0`, and `+0x44 = sourcePtr`.
	- `FUN_80158B40` is also effectively zero-argument at the call site, but it is not file-backed at this layer: it flattens two already-built runtime lists through `FUN_80158BE8(nodePtr,runningIndex,bankFlag)`.
	- Those lists start at `RAM 0x801AFE70` with `bankFlag = 0`, then `RAM 0x801B2048` with `bankFlag = 1`, and write `0x4C`-stride records at `RAM 0x801AFEE0 + index*0x4C`.
	- `FUN_80158BE8` writes fixed fields `+0x00 = 0x2B`, `+0x04 = 0x0200`, `+0x06 = 0x0200`, `+0x44 = nodePtr`, and `+0x48 = bankFlag`.
- Practical offline-parser consequence:
	- We can implement the fixed `FUN_80158AA4` subset immediately as EXE-backed startup-state context.
	- We should not yet pretend `FUN_80158B40` is scene-file-backed until the upstream builders for `0x801AFE70` / `0x801B2048` are recovered.
- Extractor update after this recovery:
	- `probe_arc_scene.py` now writes a separate `Startup state selection` section into `probe_report.txt`.
	- It reports the primary/secondary candidate-bank addresses, the recovered `FUN_80158C64` rule, and whether those banks land inside or outside the relocated DAT image.
	- It now also records the builder-input split: fixed EXE-backed sources for `FUN_80158AA4`, versus runtime-list inputs for `FUN_80158B40`.
	- Focused validation remains clean on `1/S1011` and `22/S2052`.

If the game uses `.DAT` + `.IMG` pairs the likely split is:

- `.DAT` holds indices, offsets, lengths, or directory metadata.
- `.IMG` holds the actual byte payloads.

If so, the game-side loader should eventually reveal a structure like:

- asset ID -> table lookup
- table entry -> offset / size / type
- read bytes from `.IMG`
- optional decompression
- TIM parse or custom image decode

That is the path to reproduce in C#.
