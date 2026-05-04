# Forgotten Saga (DOS) Notes

## Filetypes of Interest
- `.spb`: Potentially sprite files.
- `.mob`: Generic image container, possibly for character models and/or objects.
- `.dat`: Generic container format, one is a smacker format, the others are unknown.
- `.img`: Possibly image files, but they are not in a known format.
- `.son`: Assumed audio file, only one present `Sound.son`

## Concrete Findings
- `FUN_0005cb3f` is not an asset routine. It is part of the Borland stack-probe / stack-check path. The tiny functions that directly call it, such as `FUN_0001afd4`, `FUN_000162bf`, `FUN_0001b083`, and `FUN_0001b0c4`, are 2-instruction wrapper stubs that push a frame size and then continue into the real body at the next address.
- The real `.spb` / `.mob` archive access path is currently visible at these post-stub entry points:
	- `0001afde`: open archive, read fixed header, allocate and read per-entry table.
	- `0001b08d`: close archive and free the table.
	- `0001b2fe`: search the entry table with `strcmp`-style matching.
	- `0001b0ce`: fetch a subresource by entry name and subresource index.
- `00013c90` is a file-open helper that passes the caller-provided filename with mode string `"rb"`. `00013cba` is a checked read helper that wraps the engine's buffered read function.
- `0005cd10` is a `strcmp`-style string compare. This confirms that the per-entry table contains null-terminated names, even though many names decode to non-ASCII bytes in the samples.
- The probe output confirms the entry names in sampled `.spb` archives are CP949-encoded Korean text rather than random binary. For example, `MAGIC.SPB` entry `0` decodes as `피라에로우`.
- `00033512` is a verified `diskres.spb` fetch wrapper. Its callers pass the returned payload to `0001105c` with `ECX = 0`, which reaches the first confirmed image decoder at `00010243`.
- `0001105c` is a mode dispatcher indexed by `ECX`. The jump table at `000a5548` points mode `0` to `00010243`, mode `1` to `0001040d`, mode `2` to `000105d0`, mode `3` to `000107d9`, mode `4` to `000109ea`, and mode `5` to `00010bde`.
- The real MOB-specific archive loader path is now source-backed as well:
	- `00013e0c`: open a `.mob` file in mode `"rb"`, read the fixed header, derive `entryCount` from `word@0x406`, allocate `entryCount * 40` bytes, and read the top-level entry table into memory.
	- `00013ec9`: free the current MOB top-level table and close the active MOB file handle.
	- `00014197`: search the loaded MOB top-level table by name and return the matching entry index.
	- `00014022`: fetch the full `metadataOffset` region for a top-level entry index.
	- `00014094`: fetch the full `dataOffset` region for a top-level entry index.
- `00014022` and `00014094` are the binary-side confirmation that sampled MOB top-level records really are `40` bytes and that the two trailing dwords are two separate region offsets. Both routines compute `entryIndex * 40`, then use either `+0x20` or `+0x24` as the per-entry seek target.
- `00013f01` is a name-based helper over a loaded MOB top-level table that returns the loaded `metadataOffset` region for a matching top-level entry name.
- `00013f54` is not a top-level accessor. Given a loaded metadata region in `EAX` and a metadata-record index in `EDX`, it returns `recordPayloadPtr = metadataRegionBase + relativeOffset` from the metadata region's `name[32] + u32 relativeOffset` table.
- `00013f84` is a generic `u32 outerSize + u16 count + u32 offsets[]` block accessor: for a loaded block pointer and sub-index, it returns `blockBase + blockOffsets[subIndex]`.
- Sampled `MISSILE.MOB` metadata record payloads now parse cleanly as `u16 elementCount`, `u16 unknown`, then `elementCount * 20` bytes. The runtime-backed data-item index is the fourth `u16` in each `20` byte element (element offset `+6`).
- Example: `MISSILE.MOB` entry `0` record `피라팔방향` resolves to eight data-item indices: `4, 9, 14, 19, 24, 29, 34, 39`.
- The runtime-side placement helpers are now partly recovered:
	- `000202ed` advances within a metadata record sequence and stores per-frame offsets as `(currentWord1 - firstWord1, currentWord2 - firstWord2)` using the second and third `u16` in each `20` byte element.
	- `000203de` is a sibling advance helper in the same local block that applies the same plain sequence-relative offset rule without the optional output bridge used by `000202ed`.
	- `000204bd` resolves an explicit element index inside a metadata record and stores `(currentWord1 - firstWord1, currentWord2 - firstWord2 - currentItemHeight)`, where `currentItemHeight` is read from runtime item offset `+0xAC` and matches sampled MISSILE file payload `u32@+0xA8`.
	- This means metadata words `1` and `2` are not best modeled as absolute top-left draw coordinates. They are signed 16-bit sequence-relative anchor terms, and one render path applies an additional Y correction from the current item.
- Record selection happens above those helpers, not inside the metadata record shape. Source-backed `13f54` call sites now show explicit metadata-record indices chosen by the owner/state code before playback: index `0` at `0002756a`, `00034879`, and `00035145`; index `1` at `00037ee1`, `0003aacf`, `0003ac2a`, `0003ad71`, `0003b0fa`, and `0003cfec`; index `2` at `00035cff` and `0003df9b`; index `4` at `000381d1`.
- `0002fa52` consumes an already-selected metadata record payload pointer, caches it, initializes the record state with `00020265`, then advances it with `000202ed`. This is the clearest confirmation so far that record choice is made in higher-level owner/state logic before the placement helpers run.
- `OBJECT.MOB` exposed the signedness directly: entry `428` (`새3`) record `ANI_0` contains `619` elements, and its X placement word spans `4..65535` if read as unsigned but only `-14..745` if read as signed. The signed interpretation is the only one compatible with a sane runtime canvas.
- `OBJECT.MOB` does not currently show a source-backed metadata flag for "mask" entries. The first two entries (`버꾸기 시계`, `똑딱시계2`) each have one metadata record driving three data items, while entries `2` and `3` (`의자`, `꽃병`) have no metadata records at all and only a single data item each.
- That zero-metadata/single-item shape is not unique to the user's suspected mask pair: only three `OBJECT.MOB` entries currently match it (`의자`, `꽃병`, `부서진감옥문`), which makes it a plausible static-object class but not a proven mask class.
- The suspected `OBJECT.MOB` entries `2` and `3` also do not decode like simple 1-bit or monochrome masks. Their only data items use `11` and `21` distinct palette indices respectively, and their top-level names decode as normal object names (`의자`, `꽃병`). Current conclusion: there is no reliable metadata-only mask discriminator yet.
- Runtime trace of the `obj\object.mob` consumer so far does not support a mask-specific path. The `0x1d49c` loader opens `OBJECT.MOB`, resolves per-entry metadata/data pointers through `0x13f54` / `0x13fc8`, computes placed item bounds in `0x1d738`, and later `0x2f06f` walks the resulting object table to stamp an 8x8 occupancy/collision map via `0x2f1cb`. No traced branch in that path classifies entries as masks.
- The traced `OBJECT.MOB` consumer path also has no observed use of the archive header palette. `0x13e0c` only caches the archive handle (`0x0bb884`), entry count (`0x0bb888`), and copied 40-byte entry table (`0x0bb88c`). The setup/consumer routines above only fetch metadata/data regions and object bounds from that state.
- Entry `74` (`봉화대-커스`) strongly supports the wrong-palette hypothesis. It has one metadata record and five 16x48 data items, each using `15..16` distinct palette indices. Its active indices include `21`, `23..28`, and `75..79`; under the current header-palette assumption those entries are all `(255, 0, 255)`, which exactly explains the magenta-looking export without requiring the payload to be a mask.
- The generic MOB visual path is now partly source-backed. `FUN_0002b66d` resolves a MOB data item with `0x13f84`, places the returned item payload in `EBX`, and calls the generic blitter dispatcher `0x1105c` with `ECX = 0`. That path confirms that mode `0` is a real on-screen renderer, not just an offline decode helper.
- The mode-0 renderer at `0x10243` does not read any palette from the MOB payload or archive header. It clips the sprite, computes the destination inside the global framebuffer at `0x0a0720` using row offsets from `0x0a55ba`, then copies the literal source bytes directly with `rep movsd` / `rep movsb`. In other words, MOB/SPB sprite data reaches the screen as raw 8-bit indices.
- Palette upload is a separate global video path. `0x58693`, `0x58739`, and `0x58c85` write `0x300` bytes to VGA DAC ports `0x3c8` / `0x3c9` from the working palette buffer at `0x0f676c`. That working buffer is derived from a separate base palette buffer at `0x0f646c` / `0x0f6502` (`0x58e20`, `0x58e50`) rather than from the currently loaded `.mob` archive.
- Current source-backed conclusion: even before we pin down the exact world-object draw call for `OBJECT.MOB`, the runtime render path already proves that `OBJECT.MOB` header palette bytes are not the palette authority for on-screen object rendering. The extractor's current header-palette assumption is therefore very likely the cause of the magenta outputs.
- `0x5d778` is plain memcpy with `dest = EAX`, `source = EDX`, and `count = EBX`. That resolved the direction of both the scene-header wrapper copy and the palette-buffer copies.
- `0x224cb` is a direct raw VGA DAC uploader: it waits on port `0x3da`, then writes `0x300` bytes from the caller buffer to ports `0x3c8` / `0x3c9`. The title-screen path at `0x5a500` loads `tit.raw`, reads `tit.pal` into `0x102ee0` through `0x5a804`, and then uploads it through `0x224cb`.
- `0x1afd4` is the named `.spb` header loader. It opens the requested archive, reads the full `0x408` header into `0x0c4c64`, builds the `36`-byte top-level entry table in `0x0c5070`, and optionally copies that full header to a caller buffer.
- `0x3351f` is a `diskres.spb` wrapper on top of that loader. It calls `0x1afd4` for `diskres.spb`, copies the `0x400`-byte tail at header offset `0x06` back to a caller buffer, then resolves the requested subresource through `0x1b0da` / `0x1b083`. The state machine at `0x17f99` allocates a local `0x400` buffer, calls `0x3351f`, and then immediately hands the returned pixel payload to `0x1105c`.
- Current concrete palette-path conclusion: gameplay-style SPB/MOB draws still blit raw indices, but the palette-bearing data traveling alongside them is the active scene/resource `.spb` header tail loaded by the named-header wrapper (for example `diskres.spb`), not the per-object `.mob` header. The exact gameplay DAC-upload call for that branch is still unresolved, but extractor-side palette selection should now follow the active scene/resource header rather than `OBJECT.MOB` or `BASEPAL.PAL`.
- The first concrete non-menu gameplay branch sits elsewhere: `0x18ee0` loads `static.dat` into globals `0x0c50d8` / `0x0c50d4`, and the world/object state machine at `0x1cbe4` later consumes that state. This separates the world/object path from the earlier `char.dat` and `diskres.spb` state machines.
- The world/object state machine at `0x1cbe4` is now partly source-backed, but the earlier `0x1ab52 -> 0x1b314` interpretation was a bad raw decode and should not be reused. In the corrected state-`3` block, when `0x0c50e0` is null the code loads `EAX = [0x0c5428]` and `EDX = [0x0c5430]`, calls `0x21325`, then calls `0x1e82a` and stores the result in `0x0c50e0`.
- The concrete `0x0c542c` consumers seen in the corrected decode are different from the earlier guess. Around `0x1cbd3`, the state machine checks `0x0c542c`; if it is null it calls `0x1ab48(0)` and stores the return value in `0x0c50e8`, and if both `0x0c542c` and `0x0c50e8` are non-null it calls `0x1cced(EAX = [0x0c50e8], EDX = [0x0c542c])` before continuing into `0x1dc34`.
- `0x1cced` is now partly decoded and it does not look palette-related. It walks a linked structure through field `+0x23c`, clears bit `0x04` at byte field `+0x1c0`, and rebases per-node coordinates at `+0x1d0` / `+0x1d4` using the active object's signed words at `+0x22` / `+0x24`. That makes the corrected `0x0c542c` branch a placement/list-adjustment path, not the missing gameplay palette authority.
- The earlier easy interpretation of `map\forga.fam` is still disproven at the file level: the live file on disk does not begin with the `0x408` archive header (`0x3176` at file offset `0`). More importantly, the previously claimed world-side `0x408` loader at `0x1ab52` was based on a mis-anchored byte stream and is now retracted. The exact world-side container or record behind the gameplay branch is still unresolved.
- `FORGA.FAM` is still useful, but it is not the earlier retracted `0x408` archive. The runtime loader at `0x1ad10` now makes the on-disk structure concrete: file magic `v1T`, first-table count `0x0123` (`291`) at file offset `0x0004`, first table `291 * 16` bytes from `0x0006..0x1235`, then a second header at `0x1236` with dword `0x00b85aa4` and second-table count `0x0069` (`105`), followed by a second `16`-byte table from `0x123C..0x18CB`.
- Both `FORGA.FAM` tables use the same runtime lookup shape: `char name[12] + u32 dataOffset`. The first table contains scene-like names (`AREA02`, `AREA27-1`, `JUNGLE`, `SEC1-1`, `GORAKS`, `WORLD`, `AREA34`), while the second table contains the shared resource names consumed by the same scene path (`AREA02`, `AREA04`, `AREA09`, `AREA13`, `AREA18`, `AREA19`, `AREA20`, `AREA25`, `AREA27`, `AREA27-1`, `JUNGLE1`, `sec1-1`, ...).
- `0x1af87` is the table-name lookup helper for both tables. It walks fixed `16`-byte records with `strcmp`, so the string-at-`+0x0` / offset-at-`+0xC` model is now runtime-backed rather than just sample-backed.
- `0x1ae15` is the concrete scene loader. It looks up a first-table record by the requested scene name, seeks to `dataOffset + 4`, reads a `12`-byte embedded name, two words into the scene state, `0x18` more header bytes, then peeks a dword and calls the shared decompressor. After that, it uses the embedded `12`-byte name to look up a second-table record and loads a second decompressed buffer from that table.
- First-table scene records are now partly source-backed: the leading dword at file payload offset `0x00` matches `payloadSpan - 0x2C`, the embedded name lives at `0x04..0x0F`, words at `0x10..0x17` are stable area dimensions/flags, bytes `0x18..0x2B` are additional header data, and the dword at `0x2C` is the decompressed output length for the first blob. Sample-backed examples stay consistent with the runtime arithmetic: `AREA02 -> 200 * 190 * 5 = 190000`, `AREA04 -> 200 * 160 * 5 = 160000`, `SEC1-1 -> 40 * 100 * 5 = 20000`.
- The first and second FAM blob types share the same decompression wrapper. `0x59c24` peeks a 4-byte dword and rewinds the file pointer, and `0x59e9e` is a 4 KB window LZ-style stream that begins with the decompressed-size dword. For first-table records, that LZ stream begins at payload offset `0x2C`. For second-table records, the file payload begins with `u32 storedSpan = payloadSpan - 4`, and the shared LZ stream begins at payload offset `0x04`.
- The scene-to-resource bridge inside `0x1ae15` is now concrete enough to guide reconstruction work: the first decompressed buffer size is `field10 * field12 * 5`, and the second decompressed buffer is split with runtime pointers at `size - 0x10400` and `size - 0x10000`. On the `AREA02` sample, the `0x400` bytes at `size - 0x10400` decode cleanly as RGBX-like palette quads, and the last `0x10000` bytes are a separate raw indexed-data region. The exact meaning of the `5` bytes per first-buffer cell is still unresolved.
- `Scordato` now mirrors the runtime-backed two-table model for `.fam` files. Running it on `Samples\MAP\FORGA.FAM` writes `manifest.json`, `first_table\chunk_*.bin`, and `second_table\blob_*.bin` under `TestOutput\Scordato\FORGA`, and the manifest now exposes the two table counts, the shared LZ metadata, the first-buffer `field10 * field12 * 5` check, and the second-buffer `size - 0x10400` / `size - 0x10000` tail split.
- Practical status after this trace pass: the title path is still the only proven direct DAC upload (`tit.pal -> 0x5a804 -> 0x224cb`). The corrected `static.dat`-driven world/object branch is real, but the newly decoded `0x1cced` helper shows that one major `0x0c542c` path is only doing placement/list adjustment, so the missing gameplay palette authority is still elsewhere.
- Sampled `.mob` files do not use the `.spb` 36-byte top-level entry table. `MAIN.MOB` and `MISSILE.MOB` both parse cleanly as 40-byte top-level records:
	- `char name[32]`
	- `u32 metadataOffset`
	- `u32 dataOffset`
- With that 40-byte top table, top-level MOB names decode cleanly in CP949. Examples from `MISSILE.MOB`: `피라에로우`, `피라핸드`, `피라대저`, `피라배리어`, `피라볼`. Examples from `MAIN.MOB`: `인전여`, `인전남`, `히로`, `인기남`.
- For sampled `.mob` entries, `metadataOffset` points to a self-describing metadata region with:
	- `u32 outerSize`
	- `u16 recordCount`
	- `recordCount` records of `char name[32] + u32 relativeOffset`
	- record payloads starting at those relative offsets
- For sampled `.mob` entries, `dataOffset` points to a second self-describing region with:
	- `u32 outerSize`
	- `u16 itemCount`
	- `u32 itemOffsets[itemCount]`
	- for each item, `u32 payloadSize + payload`
- These two-region findings are verified on at least `MAIN.MOB` entry `0` and `MISSILE.MOB` entry `0`, and the same 40-byte top-level record layout produces clean names and self-consistent region spans across the rest of those files.

## Confirmed Archive Header Layout
- Files start with a fixed `0x408` byte header.
- The 32-bit little-endian field at file offset `0x404` contains:
	- High word: active entry count.
	- Low word: an archive-specific marker, not a global constant.
- Sampled `.spb` low-word markers now observed: `0x0053`, `0x008B`, `0x009F`, `0x00B7`, and `0x00FF`.
- The first 16-bit value at file offset `0x0000` is the archive type code:
	- `0x2711` for sampled `.spb` files.
	- `0x2712` for sampled `.mob` files.
- Immediately after the fixed header, sampled `.spb` files use an entry table with `entryCount` records of `36` bytes each.
- In those `.spb` records, the trailing 32-bit value at record offset `0x20` is the entry data offset.
- Sampled `.mob` files instead use `40` byte top-level records with two trailing dwords at offsets `0x20` and `0x24`.

## Verified Header Palette Candidate
- Sampled `.spb` headers contain a sample-backed embedded palette region that fits best as `255` `RGBX` entries starting at file offset `0x06`.
- The strongest evidence for `0x06` instead of `0x02` or `0x04` is decoded image behavior:
	- `DISKRES.SPB` uses indices `128` and `129` as dominant black/white UI colors only when interpreted from `0x06`.
	- The same alignment yields plausible skin tones and grayscale ramps for `BFACE.SPB` portraits.
	- Shifting the palette start to `0x02` or `0x04` turns those same images into obviously wrong magenta-heavy output.
- Across the current decoded `.spb` sample set, the highest observed palette index is `254`; index `255` is not used.
- `Scordato` now treats this as `255` valid entries at `0x06`, with index `255` left unset.
- The bytes at `0x02..0x05` and `0x402..0x403` vary across archives and are still unresolved. This means the fixed header is not yet fully solved, even though the palette alignment itself is now sample-backed.

## Verified Sample Header Values
- `FACE.SPB`: `dword@0x404 = 0x005300B7`, so `entryCount = 83`.
- `MAGIC.SPB`: `dword@0x404 = 0x002A00B7`, so `entryCount = 42`.
- `MAIN.MOB`: `dword@0x404 = 0x001700B7`, so `entryCount = 23`.

## Verified `.spb` Entry Block Layout
- For sampled `.spb` entries, the block at `entry.dataOffset` is structured as:
	- `u32 outerSize`
	- `u16 subresourceCount`
	- `u32 subresourceOffsets[subresourceCount]`
	- For each subresource: at `blockStart + subresourceOffset`, there is `u32 payloadSize` followed immediately by `payloadSize` bytes of payload.
- This was verified against adjacent entry boundaries in the samples:
	- `MAGIC.SPB` entry `0` at `0x09F0`: `outerSize = 0x1A1`, `subresourceCount = 1`, first subresource offset `0x0A`, first payload size `0x197`, next entry at `0x0B95`.
	- `FACE.SPB` entry `0` at `0x0FB4`: `outerSize = 0x1605`, `subresourceCount = 1`, first subresource offset `0x0A`, first payload size `0x15FB`, next entry at `0x25BD`.
- In both cases, `nextEntryOffset - entryOffset = outerSize + 4`, and `nextEntryOffset - (entryOffset + firstSubresourceOffset + 4) = payloadSize`.

## Verified `.mob` Top-Level Layout
- Sampled `.mob` files share the fixed `0x408` header, the archive type code field, and the `dword@0x404` entry-count/header-marker tail, but they do not share the `.spb` 36-byte top-level record format.
- The verified `.mob` top-level record size is `40` bytes.
- At the top level, the first trailing dword (`metadataOffset`) points to a metadata block whose `outerSize + 4` matches the block span.
- The second trailing dword (`dataOffset`) points to a separate data block. In sampled files, those `dataOffset` values are monotonic and each pointed-to block is also self-describing.

## Verified `.mob` Inner Regions
- Sampled `.mob` metadata regions are name-addressed, not plain offset tables.
- Example: `MISSILE.MOB` entry `0` at `metadataOffset = 0x0B10` begins with `outerSize = 1462`, `recordCount = 10`, then ten records of `name[32] + relativeOffset` such as `피라팔방향`, `피라상`, `피라좌상`, and `피라폭발`.
- Example: `MAIN.MOB` entry `0` at `metadataOffset = 0x07A0` begins with `outerSize = 8242`, `recordCount = 53`, then named records such as `스탠드`, `핀치`, `데미지`, and `죽음`.
- Sampled `.mob` data regions reuse the same outer packing pattern as verified `.spb` entry blocks: `u32 outerSize`, `u16 itemCount`, `u32 itemOffsets[itemCount]`, then `u32 payloadSize + payload` per item.
- Example: `MISSILE.MOB` entry `0` at `dataOffset = 0x585E` has `outerSize = 16579`, `itemCount = 45`, and its first item offset is `0x00BA`, which equals `6 + (45 * 4)`.
- Example: `MAIN.MOB` entry `0` at `dataOffset = 0x2D5FA` has `outerSize = 169075`, `itemCount = 146`, and its first item offset is `0x024E`, which equals `6 + (146 * 4)`.
- The payload semantics of those `.mob` data items are still unresolved. Their payloads do not start with the verified `.spb` image header shape.

## Runtime-Backed MOB Consumer Findings
- `00013e0c` is the active MOB archive initializer. It reads `word@0x406` as the top-level entry count and allocates `entryCount * 40`, which is the binary-side confirmation of the `40` byte top-level record size.
- `00014022` and `00014094` are paired region fetchers over that loaded table:
	- `00014022` seeks to `entry[entryIndex].metadataOffset` at top-level offset `+0x20`.
	- `00014094` seeks to `entry[entryIndex].dataOffset` at top-level offset `+0x24`.
- Both region fetchers first read the on-disk `u32 outerSize`, allocate `outerSize + 4` bytes, store that total size into the first dword of the returned buffer, then copy the raw region bytes immediately after it. This is consistent with the sample-backed `outerSize + 4` span relationship already observed from files.
- `000141eb` is the metadata-record loader for a chosen top-level entry. It seeks into the `metadataOffset` region, optionally searches the `0x24`-byte named record descriptors, then reads the pointed-to record payload as:
	- `u16 elementCount`
	- `u16 unknown`
	- `elementCount * 20` bytes of element records
- `00014714` post-processes those `20` byte metadata elements by grouping on the word at element offset `+6`.
- `000144f0` is the bridge from metadata into the `dataOffset` region. It treats the grouped word from metadata offset `+6` as a data-item index, looks up that item in the `dataOffset` block's `u32 itemOffsets[]` table, then reads a fixed `0xA8` byte header plus a variable tail for that item.
- Runtime/file reconciliation for sampled extracted `item_*.bin` payloads:
	- the runtime loader starts at the item's leading `u32 payloadSize`, while Scordato's `item_*.bin` starts immediately after that size dword.
	- because of that 4-byte shift, the runtime field at offset `0xA4` corresponds to file payload offset `0xA0` in `item_*.bin`.
	- sampled `MISSILE` and `MAIN` items satisfy `filePayloadSize + 4 = 0xA8 + u32@file+0xA0`.
- For sampled `MISSILE.MOB` data items, there is now a sample-backed sprite decode path:
	- decoded width = `u16@file+0x04 + 1`
	- decoded height = `u16@file+0x06 + 1`
	- `u32@file+0xA4` matches the decoded width
	- `u32@file+0xA8` matches the decoded height
	- the row-offset table starts at file payload offset `0xB0`
	- each row stream starts at `&rowOffset[row] + rowOffset[row]`
	- row command opcodes match the verified SPB mode-0 encoding: `0x80..0xFF` literal run, `0x40..0x7F` transparent skip, `0x00` row end
- Example decoded `MISSILE.MOB` entry `0` item payloads:
	- `item_000.bin` decodes to `5 x 10`
	- `item_001.bin` decodes to `6 x 12`
	- `item_004.bin` decodes to `3 x 24`
- `MISSILE.MOB` entry `0` record `피라팔방향` is still a real structural outlier in the file: its item indices `4, 9, 14, 19, 24, 29, 34, 39` match the terminal frame of the eight following directional records `피라상` through `피라우상`.
- The same structural pattern also shows up outside the original MISSILE sample. Example: `EFFECT.MOB` entry `34` (`소울라이트닝`) record `활팔방향` has eight elements `0, 3, 6, 9, 12, 15, 18, 21`, and those indices reappear at a consistent slot inside the next eight directional records.
- That file-level pattern is no longer treated as a runtime classifier. The newer trace shows the engine choosing record indices explicitly in caller state machines, so a file-only selector/sequence split is not source-backed even when the structural pattern is real.
- The second `u16` in metadata record payloads is still unresolved. The loader clearly uses the first `u16` as the count of `20` byte metadata elements, but no equally strong interpretation is yet verified for the second word.

## Image Payload Status
- The start of sampled `.spb` payloads has a repeated, verified structure:
	- `u32 width`
	- `u32 height`
	- `u32 dataStart`
	- `u32 rowMetadata[height]`
	- compressed / encoded image data starting at `dataStart`
- This header shape is verified in at least these samples:
	- `MAGIC.SPB` entries `0` to `3`: `width = 24`, `height = 24`, `dataStart = 108`, and `108 = 12 + (24 * 4)`.
	- `FACE.SPB` entry `0`: `width = 67`, `height = 57`, `dataStart = 240`, and `240 = 12 + (57 * 4)`.
- `00010243` confirms that `rowMetadata[row]` is a self-relative offset from the address of that row's metadata dword to the start of that row's command stream. In other words, the decoder computes `rowStream = &rowMetadata[row] + rowMetadata[row]`.
- The decoded command stream is now source-backed:
	- `0x80..0xFF`: copy `opcode & 0x3F` literal 8-bit pixel indices from the payload stream.
	- `0x40..0x7F`: skip `opcode & 0x3F` transparent pixels in the output.
	- `0x00`: end the current row.
- In the unclipped draw path, `00010243` can also stream directly from `payload + dataStart` and use explicit `0x00` row terminators. In the clipped path, it uses the self-relative per-row table to jump straight to each row's command stream.
- The decoder writes 8-bit indexed pixels directly into the 320x200 framebuffer, so palette application is external to the `.spb` payload format itself.
- A quick decoder check against dumped sample payloads succeeds for `MAGIC` entry `0`, `BFACE` entry `0`, and `DISKRES` entry `0` with no row overrun or opcode mismatch.
- `BASEPAL.PAL` shares a large header prefix with some sampled `.spb` files, but its full relationship to the archive and image decoding path is not yet confirmed.
- `BASEPAL.PAL` is not a raw `256 * 3` VGA file at offset `0`; its total size is `0x432` bytes. The first `0x300` bytes at file offset `0x06` match the embedded header palette bytes in `OBJECT.MOB`, `MAIN.MOB`, and `EFFECT.MOB` exactly.
- That means `BASEPAL.PAL` is source-backed evidence for a shared base palette blob, but it does not by itself solve the `OBJECT.MOB` magenta problem: using `BASEPAL.PAL` instead of the `OBJECT.MOB` header palette produces the same palette indices and colors for those files.

## Other Confirmed Formats
- `TIT.PAL` is exactly `768` bytes, which matches a raw `256 * 3` VGA palette.
- `TIT.RAW` is exactly `307200` bytes, which matches a `640 * 480` 8-bit image.

## Current Probe Tooling
- `Scordato` now parses sampled `.spb` / `.mob` archives, emits manifests, decodes CP949 entry names when possible, and writes PNG outputs under `TestOutput/Scordato`.
- `Scordato` uses the embedded header palette candidate automatically for decoded `.spb` / `.mob` PNG output unless an explicit palette override is supplied.
- `Scordato --palette` now accepts either a raw `768`-byte VGA palette (such as `TIT.PAL`) or a `0x2711` / `0x2712` file carrying an embedded header RGBX palette (such as `BASEPAL.PAL` or sampled archives). Validation run: `OBJECT.MOB` completed successfully with `--palette BASEPAL.PAL`, and the manifest recorded `"embedded header RGBX palette from BASEPAL.PAL"` as the decode source.
- If a `--palette <path>` argument is supplied, that explicit `768` byte raw VGA palette overrides the embedded header palette for PNG output.
- Current `Scordato` default mode is intentionally low-clutter: it writes manifests, decoded PNGs, MOB item PNGs, and explicit-element aligned frame canvases under `metadata/frames/<record-name>/`, but it does not write raw `.bin` dumps or strip PNGs unless requested.
- `Scordato` now treats `runtime-explicit-element-y-minus-height` as the default primary MOB placement mode. The older `runtime-sequence-relative` export is now opt-in and, when enabled, is emitted as the alternate alignment under `metadata/frames-sequence/<record-name>/`.
- `Scordato` command-line switches for the noisier legacy-style output are now:
	- `--emit-binaries` to restore raw archive / region / payload / indexed / mask `.bin` outputs.
	- `--emit-strips` to restore strip PNG export for the enabled alignment modes.
	- `--emit-sequence-relative` to additionally emit the sequence-relative alignment as a secondary view.
- `Scordato` now treats metadata placement words `1` and `2` as signed 16-bit values. This fixes `OBJECT.MOB`, whose `entry_428/meta_000_ANI_0` record previously blew up into a multi-gigabyte strip canvas when `0xFFFF` was misread as `65535` instead of `-1`.
- `Scordato` now skips oversized strip PNGs instead of terminating the whole run. On the current `OBJECT.MOB` sample, `entry_428/meta_000_ANI_0` still gets per-frame exports, but the combined strip is intentionally omitted because its `490247 x 466` canvas is not practical.
