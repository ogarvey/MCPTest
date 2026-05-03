# Time Slaughter (DOS)

## Notes

### 2026-05-01: Initial `.VOL` loader split

- `FUN_0001944c` is startup / bootstrap, not the archive parser. It handles command-line flags, CD detection, subsystem init, then dispatches into the actual asset loaders.
- High-confidence DOS file wrappers:
	- `FUN_00031667`: DOS `int 21h`, `AH=3Dh`, open existing file read-only.
	- `FUN_00031676`: DOS `int 21h`, `AH=3Fh`, read from file handle into `ECX:EBX`.
	- `FUN_00031648`: DOS `int 21h`, `AH=3Eh`, close file handle.
	- `FUN_000344d7`: DOS `int 21h`, `AH=42h`, seek (`BL` = origin, `EDX` = offset).

### Palette-first family

- `FUN_000202cc` is the main loader for palette-first `.VOL` files. Confirmed callers include `selectbg.vol` and the `bg.vol` path from `FUN_0001944c`.
- Verified on disk: `BG1.VOL` and `TITLEBG.VOL` start with a 768-byte VGA palette (`0x300` bytes, all channels `<= 0x3F`).
- Recovered read order from `FUN_000202cc`:
	1. Open file read-only.
	2. Read `0x300` bytes of palette.
	3. Read `0x140` bytes = 80 little-endian `u32` relative offsets.
	4. Read `0xA0` bytes of still-unknown metadata.
	5. Read one `u32` size, allocate, then read the first main blob.
	6. Read one `u16` relative skip and seek forward by that amount.
	7. Read one `u16` record-count-minus-one, then read `(count + 1) * 10` bytes of records.
	8. Read one `u16` size and a blob stored at struct offset `+0x35A`.
	9. Read two fixed `0xC8`-byte blocks.
	10. Read one `u16` word-count and a `wordCount * 2` block.
	11. Read four more `u16`-sized blobs.
	12. Tested files then end with a little-endian `u16` byte count followed by a standard MIDI stream starting with `MThd`.
- `FUN_00015af4` matches the palette-family trailer once positioned at `MThd`: it reads the 14-byte MIDI header, rejects non-type-0 streams (`format != 0`), reads the 8-byte `MTrk` header, allocates the big-endian track length, and reads the track payload.
- Sanity checks:
	- `BG1.VOL`: first 80-entry offset table has 57 nonzero entries, max offset `54028`, first blob size `54348`, post-blob skip `8598`, record count `24`.
	- `TITLEBG.VOL`: same layout, 72 nonzero offsets, max offset `38574`, first blob size `39774`, post-blob skip `7142`.
	- MIDI trailer validation: `BG1.VOL` = `8597` MIDI bytes, `TITLEBG.VOL` = `3605`, `SELECTBG.VOL` = `5617`.
- The outer palette-first container now parses to EOF on tested samples. The remaining unknowns are the semantics of the recovered blocks and the first-data slices, not the outer file layout.

### `MIDGETPOWER` family

- `FUN_0001fa04` is the strongest character / fighter archive loader.
- It builds `<fighter>.vol` by appending `.vol`, opens the file read-only, reads 12 bytes, and compares them against the uppercase `MIDGETPOWER` magic.
- After the magic check it immediately calls `FUN_0002d87c`, then runs two seek-table loops (`1..4`, then `1..6`) driven by 4-byte values read from the file.
- Helper routines:
	- `FUN_0002d808`: read `u32 size`, allocate `size`, read `size` bytes.
	- `FUN_0002d87c`: read `u32 size`, allocate `size`, read `size` bytes, then keep `size - 0x1E` as the adjusted payload length.
- Files confirmed to start with `MIDGETPOWER` at offset `0`: `ASYLUM`, `BUDDY`, `CHI`, `JINSOKU`, `LAZARUS`, `MOJUMBO`, `PIERRE`, `PORTAL`, `RAVAGE`, `SAVAGE`, `SPICE`, `STAINE`, `UG`, `VLAD`.
- `SAVAGE.VOL` validation: `dword@0x0C = 17836`, giving a candidate offset of `0x45BC`, but the 768 bytes there do **not** pass a strict 6-bit VGA palette check. So that field is not yet safe to treat as a direct palette pointer.
- `FUN_000109a0` is not a geometry assembler; it is a palette-remap pass over the same row-compressed slice format already seen in palette-first `first_data` entries.
	- It reads `width/height` from bytes `+1/+2`, starts row data at `slice + 4 + height * 2`, and walks the same `0x80` skip / literal-copy row encoding.
	- The only extra behavior is literal-byte remapping through a 256-byte translation table.
- The main fighter remap table in `FUN_0001fa04` is now grounded enough to parse directly.
	- After the initial `FUN_0002d87c` block and the two relative-seek loops (`4` entries, then `6` entries), the loader reads a `0x0F`-byte XOR-`0x8F` secondary name into `obj+3`.
	- The first grounded file-owned sprite table after that is: `0x100`-byte remap table, `u32[256]` offset table, `u32[256]` length/presence table, `u32 blobSize`, then the blob itself.
	- The loader builds `obj+0x7dd` from `blobBase + offsets[i]` when `lengths[i] != 0`.
	- Corrected file offsets: `SAVAGE secondaryNameOffset=0x87100, mainRemapOffset=0x8710F, blobOffset=0x87A13`; `MOJUMBO secondaryNameOffset=0x5A7A7, mainRemapOffset=0x5A7B6, blobOffset=0x5B0BA`.
- After the primary 256-slot sprite table, the loader reads 14 more `FUN_0002d87c` blocks stored at `obj+0xbe5` (8 bytes/entry = ptr+adjustedLen pairs, advances by 8 per iteration).
	- These 14 secondary blocks are **not** additional sprite data. Inspection of `SAVAGE.VOL` secondary blocks shows:
		- Blocks 0–10: runs of `0xFF`/`0x00` bytes only → collision masks or hitbox bitmaps.
		- Block 11: small 1–6 range values → animation state tables or AI behavior data.
	- None of the secondary blocks contain `FUN_000109a0`-style (skip/literal RLE) slice entries; the slice scanner finds 0 entries across all 12 non-zero secondary blocks.
	- The primary 256-slot table is the complete sprite bank; the secondary 14 blocks hold gameplay/collision data.
	- Slot usage for SAVAGE: 160 of 256 slots filled; large run of empty slots from ~182 to 255, consistent with unused animation slots.
- Probe scope has shifted away from assembly for now.
	- `Unknown` `.VOL` exports still use the grounded `FUN_000109a0` slice scan where no better file walk is known yet.
	- `MIDGETPOWER` export no longer scans the whole file; it now exports the first grounded pointer-table sprite set owned by `FUN_0001fa04`.
	- Actual RGB preview colors now come from a canonical stage palette chosen from the palette-first `BG*.VOL` files.
	- Cross-check: for `SAVAGE.VOL`, all `117` raw palette indices used by the structured primary entries match the same RGB triplet across the stage/background palettes; `MOJUMBO.VOL` also exports correctly with `previewPaletteKind=stage-canonical:BG1`.
	- Validation: `SAVAGE.VOL` now exports `160` referenced / `160` decodable grounded primary entries; `MOJUMBO.VOL` exports `196` referenced / `196` decodable grounded primary entries.

### Other `.VOL` family

- `SELECT.VOL` and `TITLE.VOL` do **not** start with either a raw VGA palette or `MIDGETPOWER`.
- `FUN_0002d994` is not a generic character loader; it loads `selectbg.vol`, then opens `select.vol`, so its `MIDGETPOWER` reference should be treated as select-screen specific until proven otherwise.
- Unknown `.VOL` files now also get the same grounded slice scan during export, so graphics-bearing files can start surfacing without waiting for full family-specific assembly logic.
- Current batch scan hits in the unknown family: `MISC=100`, `FONT3=81`, `TITLE=13`, `SELECT=3`; `FONT`, `FONT2`, `GRIP`, `SOUND`, `SPIN`, and `TIMBRES` currently yield no decodable `FUN_000109a0`-style slices.

### Probe tooling

- Added a small C# console probe at `DOS/TimeSlaughter/TimeSlaughterVolProbe`.
- Current scope:
	- Detect `PaletteFirst` vs `MidgetPower` vs `Unknown`.
	- Walk and print the recovered palette-first header structure.
	- Report the basic `MIDGETPOWER` header fields without claiming a full decode yet.
- Added `--export <dir>` support.
	- Palette-first files now export:
		- raw 6-bit palette, RGB24 palette, and `.gpl` palette,
		- the recovered metadata / block blobs,
		- `first_data.bin`,
		- `first_data_slices/` split from the sorted nonzero offset table,
		- `trailing_music.mid` when the final trailer resolves to a standard MIDI stream,
		- ImageSharp-rendered raw indexed PNGs for the current high-confidence plane pairs,
		- TSV manifests for the offset table and slice boundaries.
	- `MIDGETPOWER` files now export the first `0x100` bytes plus a candidate VGA palette when the `0x10 + dword@0x0C` location looks like a 768-byte 6-bit palette.
	- `Unknown` files still export raw non-assembled slice findings from the grounded `FUN_000109a0` row format scanner.
	- `MIDGETPOWER` files now export the first grounded primary sprite table instead: `primary_entries/`, `primary_entries_png/`, `primary_entries.tsv`, `primary_offsets.tsv`, `primary_lengths.tsv`, `primary_blob.bin`, `secondary_name_*`, and `main_remap_*`.
	- `MIDGETPOWER` exports now also write `main_remap_256.bin`, `main_remap.tsv`, `main_remap_offset.txt`, and `preview_palette.*`.
	- All PNG export paths now treat palette index `0` as transparent unless a coverage mask already makes the pixel transparent sooner.
- Validation so far:
	- `BG1.VOL` export succeeded and produced 57 monotonic first-data slices, matching the 57 nonzero offsets from the first `0x140` bytes after the palette.
	- `BG1.VOL`, `TITLEBG.VOL`, and `SELECTBG.VOL` now export a `trailing_music.mid` and consume to EOF (`remainingBytes = 0`).
	- `SAVAGE.VOL` export now succeeds and writes `160` grounded primary sprite entries with `secondaryNameDecoded=SAVAGE`.
	- `MOJUMBO.VOL` export now succeeds and writes `196` grounded primary sprite entries with `secondaryNameDecoded=MOJUMBO`.
	- Full structured-parse counts for all fighters (all referenced == decodable): `MOJUMBO=196`, `LAZARUS=191`, `PIERRE=185`, `PORTAL=183`, `CHI=173`, `VLAD=170`, `ASYLUM=169`, `SAVAGE=160`, `JINSOKU=163`, `SPICE=163`, `UG=150`, `BUDDY=149`, `RAVAGE=140`, `STAINE=123`. These are strictly the primary-table entries owned by `FUN_0001fa04`, not a whole-file scan.
	- `SAVAGE.VOL` and `MOJUMBO.VOL` both export with `previewPaletteKind=stage-canonical:BG1`.
	- Batch export report now lives at `DOS/TimeSlaughter/_probe_output_all_transparent/archive_report.tsv`.

### Raw background plane decode

- `FUN_00020bd8` is a strong background row decoder, not just a generic copier.
- The compressed stream format inside these blocks is now high-confidence:
	- rows are referenced through a `u16` offset table,
	- each row expands to a fixed width using control bytes,
	- `control & 0x80` means skip/advance by `control & 0x7F`,
	- otherwise `control` is a literal byte count copied from the stream.
- `FUN_00020b4c` then performs a horizontal resample / widening step on each decoded 640-byte row, so the raw exported PNGs are the pre-resample indexed planes rather than the final on-screen trapezoid.
- High-confidence block/table pairings from `BG1.VOL` now exercised by the probe:
	- `fixed_block_04.bin` + `block_10.bin` -> raw indexed plane `640 x 100`.
	- `fixed_block_08.bin` + `block_14.bin` -> raw indexed plane `640 x 100`.
	- `block_0c.bin` + `block_18.bin` -> raw indexed plane `320 x 150`.
- The probe now exports these as `decoded_block_10.png`, `decoded_block_14.png`, and `decoded_block_18.png` plus matching `_indices.bin` files, using ImageSharp for the palette-mapped PNGs.

### First-data slice structure

- The `first_data.bin` blob is no longer just a bag of unknown slices. For `BG1.VOL` and `TITLEBG.VOL`, every exported slice matched the same local structure.
- Current high-confidence per-slice layout:
	- byte `0`: likely horizontal anchor / placement offset,
	- byte `1`: decoded row width,
	- byte `2`: row count / height,
	- byte `3`: constant `0xFF`,
	- then `height` little-endian `u16` row offsets,
	- then row data using the same skip/literal compressor seen in `FUN_00020bd8`.
- Validation details:
	- sampled slices satisfy `firstRowOffset == 4 + height * 2` exactly,
	- `BG1.VOL`: all 57 exported slices decoded successfully to PNG,
	- `TITLEBG.VOL`: all 72 exported slices decoded successfully to PNG.
- The probe now writes these decoded images to `first_data_slices_png/`, again using ImageSharp for PNG output.
- The separate record/blob path is now partially decoded as well:
	- the on-disk record table is `10` bytes per entry,
	- loader storage begins at object offset `+0x166`,
	- record bytes `+0/+1` are a `blob35a`-relative descriptor offset,
	- record bytes `+2/+3` gate whether the entry is copied into the active compacted list at object offset `+0x2F6`,
	- the loader resolves record `+4` to a live descriptor pointer by adding the `blob35a` base,
	- descriptor byte `+4` is not a palette selector; it indexes the first-data pointer table at object offset `+0x26` (`DAT_0005ffc6` in the current labels),
	- descriptor signed offsets used by `FUN_0002dee8` are read from bytes `+5/+7`.
- Probe validation of that record path:
	- `BG1.VOL` reports `24` raw records but only `3` active compacted entries,
	- `TITLEBG.VOL` reports `31` raw records and `0` active compacted entries,
	- the probe now exports `record_descriptors.txt` so the raw record flags, `blob35a` offsets, descriptor `sliceIndex`, and recovered signed offsets are visible alongside the blob dumps.
- `FUN_00010874` / `FUN_0001060a` are now high-confidence first-data slice blitters.
	- They use slice bytes `+1/+2` as `width/height` and start row decoding at `slice + 4 + height * 2`.
	- Skip runs are transparent; literal runs write pixels directly.
	- Slice byte `0` is **not** consumed by this draw path, so the descriptor / caller supplies placement.
- The probe now exports a real assembled active-record layer when active records exist.
	- `active_record_layer_relative.png` uses the record descriptor X/Y offsets with transparency preserved from skip runs and normalizes the layer to its minimum X/Y.
	- `composite_view_zero_scroll_with_active_records.png` overlays the same active-record slices onto the current zero-scroll background preview.
	- `active_record_layer_report.txt` records the raw and normalized bounds.
- Current validation:
	- `BG1.VOL` produces `3` active placements with bounds `x=-45..602`, `y=-28..53`; two entries use slice index `52` and one uses slice index `53`.
	- `TITLEBG.VOL` still reports `0` active placements, so no active-record layer export is produced there yet.

### Background composite path

- The active scrolling background path is now clearer than the earlier slice-placement hypothesis.
- `FUN_0001d97c` is the main background scroll / update routine after `FUN_000202cc`, not `FUN_0001dc68`.
	- It updates `DAT_00060328` / `DAT_00060330` as horizontal scroll state.
	- It then calls `FUN_00020e84` and `FUN_00020df4` to rebuild the visible background buffer.
- `FUN_00020e84` is the key composite setup routine.
	- It makes two calls into `FUN_0003f4f0`.
	- For zero scroll, the recovered call pattern is:
		- pass 1: `block_10` with `fixed_block_04` against `block_18` with `block_0c`, rendered from source rows `10/5` into the viewport at `y = 10` over `block_1c` starting at `y = 2`.
		- pass 2: `block_14` with `fixed_block_08` against `block_18` with `block_0c`, rendered from source rows `0/0x5F` into the viewport at `y = 100` over `block_1c` starting at `y = 0x5C`.
- `FUN_000202cc` also sets up the widened-row / lower-ground state used by `FUN_00020df4` and `FUN_0003f71a`.
	- `obj + 0x37C` is the split row (`splitY`). If the loader call arrives with `EBX != 0`, that override is used; otherwise it falls back to the object `u16` at `+0x20`.
	- `obj + 0x378` is the widened-row count and is computed as `(block_14.height + 100) - splitY`.
	- When that row count is positive, the loader calls `FUN_00020bd8` to build the widened/perspective row buffer.
	- `FUN_0001944c` does not guess this split for battle backgrounds: it looks up the filename stem from the table at `0x52470`, appends `.vol`, then looks up the split override from `0x5251C` before calling `FUN_000202cc`.
	- The nonzero `bg`/split pairs currently recovered from those tables are: `bg1->0xA0`, `bg2->0xA0`, `bg3->0x8C`, `bg4->0x8C`, `bg9->0x96`, `bg6->0x96`, `bg7->0xA0`, `bg8->0x9E`, `bg12->0xA0`, `bg5->0xA0`, `bg10->0xA0`.
- `FUN_00020df4` is now clearer than the earlier heuristic port.
	- It copies rows from the widened buffer at `obj + 0x370` into the destination starting at destination row `obj + 0x37C`.
	- It writes exactly `obj + 0x378` rows, one `320`-pixel crop per source row via `FUN_00020d70`.
- `FUN_00020e84` resolves the lower-ground handoff more precisely than the earlier probe heuristic.
	- `FUN_0003f71a` is only called when the default split `obj+0x20` is below the effective split `obj+0x37C`.
	- That transparent pass is anchored at the default split, not the override split: it starts from `block14` source row `obj+0x20 - 100` and draws into destination row `obj+0x20`.
	- Immediately before widened-row build, `FUN_000202cc` patches `block14.height` to `splitY - 100` when `obj+0x20 < splitY`, which makes that transparent pass stop exactly at the override handoff.
	- `FUN_00020bd8` then decodes widened rows starting at row-table index `splitY - 100`, not from row `0`; `FUN_00020df4` places those widened rows starting at destination row `splitY`.
- `FUN_0001d97c` computes runtime scroll state before calling `FUN_00020e84`.
	- `DAT_00060328` is the horizontal camera position clamped to `0..400`.
	- `DAT_00060308` is derived from that camera as `(DAT_00060328 * 8) / 10` and feeds the foreground parallax crop.
	- `DAT_0006030C` is a vertical row-window value derived from actor state, then `DAT_0006032C = DAT_0006030C * 320` is used when copying the finished scene buffer.
- `FUN_0001ad4c` seeds battle backgrounds with `DAT_00060328 = 200`, so the gameplay path is not a true zero-scroll view.
	- The probe now exports `composite_view_battle_init_scroll.png` using that runtime startup camera (`foreground=160`, `mid=80`, `background=40`).
	- This confirmed that the BG1 battle view really is horizontally offset at startup, but the remaining black wedge is still controlled by the unresolved `splitY` source rather than by pure horizontal scroll.
- `FUN_00017340` is a different loader consumer: it calls `FUN_000202cc` with `EAX = 0x5FFA0`, `EDX = 0x59357`, `EBX = 0`, then forces `DAT_00060308 = 0` and `DAT_0006030C = 10` afterwards. Treat that as a separate scene path from the battle-init camera setup in `FUN_0001ad4c`.
- `FUN_0001e074` consumes the widened-row state for sprite placement.
	- It reads `DAT_0006031C` / `DAT_00060318` (the static addresses corresponding to battle object `+0x37C` / `+0x378`) as the Y-base and height clip for projected scene elements.
- `FUN_0003f4f0` is not a simple blit. It walks two compressed row streams in parallel and composites them over the `block_1c` background source.
- `FUN_0003f4f0` overlap semantics are now high-confidence from the ported probe and the decompiled control flow:
	- layer A literal beats everything,
	- otherwise layer B literal beats the background source,
	- background shows through only when both layers are in skip/transparent runs.
- Important scope correction: the decoded `first_data` slices do **not** appear on this immediate scrolling background compositor path. They still look like real image elements, but their runtime placement is separate from the main `FUN_0001d97c -> FUN_00020e84` path until proven otherwise.
- The probe now exports a probe-level `composite_view_zero_scroll.png` preview using a simplified port of the recovered zero-scroll `FUN_00020e84` / `FUN_0003f4f0` call pattern.
	- The compositor preview no longer uses XOR-style overlap; it now follows the recovered top-down layering rule from `FUN_0003f4f0`.
	- It now also ports the zero-scroll `FUN_00020bd8 -> FUN_00020b4c -> FUN_00020df4` perspective-row path, so `BG1.VOL` includes the extra widened rows below the shorter `block_1c` background base.
	- It now exports a second `composite_view_battle_init_scroll.png` preview using the runtime battle-start camera from `FUN_0001ad4c`.
	- Validated outputs exist for `BG1.VOL` and `TITLEBG.VOL`.
	- `BG1.VOL` caveat: `block_1c` is only `44160` bytes (`138` rows at `320` pixels wide), so the preview clamps to the available background rows instead of assuming a full `320 x 200` base buffer.
	- After porting the table-driven split and the exact `FUN_0003f71a` / `FUN_00020bd8` handoff rules into the probe, a fresh `BG1.VOL` battle-init export no longer leaves zero-valued pixels in rows `150..199`; the remaining verification work is now visual alignment, not missing lower-ground fill.
