# Zyclunt (DOS) Notes

## General Notes
- Game uses various formats for storing data, samples of which we have in the `Samples` directory.

## Ghidra Investigation

### 2026-05-03 - `fix_off32_00013faf` anchor
- Ghidra labels the function at `0x00013faf`, but raw bytes show the real entry starts at `0x00013fac`.
- Ghidra currently shows no direct xrefs to the function entry itself.
- The real entry loads `0x000c0354` (`"standard.ish"`) into `EAX`, calls `0x0001332d`, writes the returned pointer/object into global `DAT_000e2318`, emits `"file error.\n"` on failure via a call to `0x000bb49d`, then runs a 9-iteration loop that calls helper stubs at `0x000130dd`, `0x00013134`, and `0x000131f4`.
- Those immediate callees, plus `0x0001332d`, are not useful semantic anchors in their current Ghidra state. They decompile as trivial calls into `0x000bb0f4`, but raw bytes show this is Borland prologue/thunk behavior rather than the full body of the surrounding logic.
- Current conclusion: this path initializes `standard.ish` data from `zyclunt.jam`; it is not the direct `.CAD` loader.

### 2026-05-03 - Asset/archive path recovered from raw bytes
- Nearby string data at `0x000c0361` contains `"file error.\n"`, `.cad`, `rb`, `JAM2File`, `zyclunt.jam`, `CAD`, and several asset filenames.
- A concrete live asset-loading path exists in the code region around `0x000110d4` to `0x0001118f`.
- This region loads filenames such as `e-cin.abm` and `f-cin.abm`, then calls `0x00037b1c` and `0x00037e15`.
- Ghidra currently truncates both `0x00037b1c` and `0x00037e15` to Borland-style prologue stubs, but raw memory reads show real code immediately after those prologues.

### 2026-05-03 - Recovered meaning of `0x00037b1c` and `0x0002c085`
- Raw bytes at `0x00037b1c` show it moves `0x000c04b0` (`"zyclunt.jam"`) into `EAX`, forwards the caller's original `EAX` into `EDX`, and calls `0x0002c085`.
- Raw bytes at `0x0002c085` show a strong archive lookup pattern:
- loads `EDX = 0x000c0375` which is `"rb"`
- opens the file named in `EAX`
- reads 9 bytes and compares them against `0x000c0378` which is `"JAM2File"`
- then enters a repeated-read loop that appears to read fixed-size entry records and compare entry names against the caller-supplied target name in `EBP`
- Strong evidence: `0x0002c085` is part of the `zyclunt.jam` archive reader / entry lookup logic.

### 2026-05-03 - Actual meaning of `0x0001332d`
- The actual body of `0x0001332d` begins after the Borland stack-check prologue.
- Raw bytes show it passes `"standard.ish"` into the archive-open helper around `0x0002bfa7`, using `zyclunt.jam` as the container file.
- After opening the inner file, it reads:
- a 4-byte count
- `count * 4` bytes worth of pointers to allocated 9-byte name entries
- `count * 4` bytes worth of pointers to allocated 0x10-byte metadata entries
- Current conclusion: `0x0001332d` loads a table-like resource structure from `standard.ish`, not a `.CAD` payload directly.

### 2026-05-03 - Near-anchor archive setup at `0x000134b0`
- Another live `zyclunt.jam` reference exists at `0x000134d1`, inside an undefined code block beginning around `0x000134b0`.
- Raw bytes in this block show:
- `EAX = 0x000c0268` (`"zyclunt.jam"`)
- `EDX = [caller supplied pointer]`
- a call to another undefined routine at `0x0002bfa9`
- an error path using `"%s file not found.\n"` at `0x000c028a`
- subsequent allocation/read logic that appears to size arrays from data read out of the archive
- Nearby literal data includes `"MSHtrk"`, which may be a record/header marker handled by this path.
- Current conclusion: there is a second, more general archive-backed asset loading path near the original `0x13faf` region, and it is likely more relevant than `fix_off32_00013faf` itself.

### 2026-05-03 - File I/O wrapper semantics inferred from call sites
- The call site at `0x0002c085` strongly suggests the undefined routine at `0x000bb771` is a file-open wrapper taking `EAX = filename` and `EDX = mode` (`"rb"`).
- The same call site strongly suggests the undefined routine at `0x000bb12f` is a file-read wrapper taking `ECX = file handle/object`, `EAX = destination buffer`, `EDX = byte count`, and `EBX = element/count multiplier`.
- This gives us a usable file-I/O layer without having to locate raw DOS interrupts first.

### 2026-05-03 - CAD filename helper and loader
- Raw bytes at `0x0002bf78` show a helper that copies a basename string into a destination buffer and then appends `.cad` from `0x000c0370`.
- This helper is called from `0x0002c1e7`.
- The surrounding function really starts at `0x0002c1cc`.
- Raw bytes at `0x0002c1cc` show the following sequence:
- build `<basename>.cad` in a stack buffer via `0x0002bf78`
- open that inner file from `zyclunt.jam` via the helper around `0x0002bfa7`
- read 4 bytes and compare them against `0x000c038d` (`"CAD"`)
- on mismatch, close the file and return `-2`
- on success, continue reading several sections from the CAD payload
- Observed CAD sections/reads in `0x0002c1cc`:
- a conditional 0x400-byte auxiliary block, controlled by a flag byte read into local storage
- a 4-byte-sized blob that is heap-allocated and read as-is
- a 2-byte count that drives allocation and reading of `count * 0x5e` bytes
- a 2-byte count that drives allocation and reading of `count * 6` bytes
- a 2-byte count that drives allocation and reading of `count * 4` bytes
- After reading those tables, the code normalizes or fixes up selected fields in-place, including data at offsets `0x4c`, `0x56`, and `0x5a` inside the 0x5e-byte records.
- Helper `0x000bc266` is not an allocator here; when the auxiliary-block flag is set and the caller passed `EDX = 0`, the loader uses `0x000bc266` to skip/seek past the 0x400-byte block instead of reading it.
- Current conclusion: `0x0002c1cc` is the main CAD file loader/parser currently recovered from the original binary.

### 2026-05-03 - CAD layout validated against sample files
- The read order from `0x0002c1cc` matches both sample files exactly, with no trailing bytes left over.
- `1up.cad`:
- magic = `CAD\0`
- `unk0 = 2`
- `aux_flag = 0`
- `unk1 = 3`
- `raw_data_size = 1830`
- `count_5e = 4`
- `count_6 = 5`
- `count_4 = 1`
- `b1.cad`:
- magic = `CAD\0`
- `unk0 = 2`
- `aux_flag = 0`
- `unk1 = 48`
- `raw_data_size = 63875`
- `count_5e = 50`
- `count_6 = 64`
- `count_4 = 14`
- This strongly confirms the recovered CAD loader is reading the format correctly.

### 2026-05-03 - CAD table relationships
- The `count * 0x5e` table appears to be the primary frame/object descriptor table.
- The loader adds the raw-data blob base to dword offsets at `+0x56` and, conditionally, `+0x5a` inside each 0x5e-byte record.
- The field at `+0x4c` controls whether `+0x5a` is forced to zero or also treated as a raw-data offset.
- The `count * 6` table is a link/sequence table:
- each 6-byte entry begins with a dword offset into the 0x5e table, stored on disk as a relative offset such as `0x00000000`, `0x0000005e`, `0x000000bc`, etc.
- the loader converts that dword into an in-memory pointer by adding the 0x5e-table base, except for sentinel values `-1` and `-2`
- the trailing 2-byte field is still unresolved, but sample values strongly suggest timing, duration, or a small command/state code
- The `count * 4` table is a second-level offset table into the 6-byte table.
- Sample evidence: in `1up.cad`, the only 4-byte entry is `0`, pointing at the start of the 6-byte sequence list. In `b1.cad`, entries such as `0x00000000`, `0x0000002a`, and `0x00000054` match offsets into the 6-byte table.
- Current conclusion: the CAD format is very likely organized as raw image data + frame descriptors + animation/link entries + sequence-start offsets.

### 2026-05-03 - Confirmed CAD basenames from callers
- Literal basename callers of `0x0002c1cc` recovered from raw call scanning:
- `0x0002cb64` uses `"acsr"` at `0x000c0391`
- `0x00032d2a` uses `"title"` at `0x000c0400`
- `0x00035cc9` uses `"ending"` at `0x000c0458`
- `0x00035d02` uses `"endroll"` at `0x000c045f`
- `0x000375a2` uses `"sclear"` at `0x000c04a2`
- `0x000375db` appears to use `"sou"`, inferred from the raw string region immediately following `sclear`
- Dynamic or table-driven callers also exist at `0x000100be`, `0x0002ca79`, `0x0002cb10`, `0x0002cc4c`, and `0x0002cd43`.
- Current conclusion: the binary does not just know the `.cad` extension; it actively constructs and opens concrete `<basename>.cad` resources from `zyclunt.jam`.

### 2026-05-03 - Stage table driven CAD names
- The dynamic CAD callers around `0x0002ca9e`, `0x0002cb78`, and `0x0002cc66` read names from a stage subtable at:
- `0x0001d077 + major * 0x22fa + sub * 0x197`
- `0x22fa / 0x197 = 22`, so each major group contains 22 subtables.
- The front of each 0x197-byte subtable contains stage-level strings such as `.map`, `.crl`, `.cr2`, and `.pal` filenames.
- Example stage headers recovered from the binary:
- major 0, sub 0: `stage''.map`, `stage''.crl`, `stage''.cr2`, `st1_h.pal`
- major 1, sub 0: `st2-1.map`, `st2-1.crl`, `st2-1.cr2`, `st2_s.pal`
- major 2, sub 0: `st3-1.map`, `st3-1.crl`, `st3-1.cr2`, `st3_s.pal`
- major 3, sub 0: `st4-1.map`, `st4-1.crl`, `st4-1.cr2`, `st4_s.pal`
- Two CAD name lists live later in the same subtable:
- list A: count at `+0x4f`, records at `+0x51`, used by the larger dynamic caller/cache path around `0x0002cb78`
- list B: count at `+0x129`, records at `+0x12b`, used by the smaller dynamic caller path around `0x0002ca9e`
- Each list record is `0x0c` bytes: `u16 id` followed by a 10-byte basename string.
- Example recovered CAD names from stage tables:
- stage major 0, sub 0 list A: `player`, `st1_ani`, `s_gear`, `b_gear`, `h_gear`, `g_gear`, `p_gear`, `energy`, `1up`, `cloud`, `b1`, `d_catty`, `fb1`, `h_hugger`, `gaiga`, `build`, `boss1`
- stage major 0, sub 0 list B: `sg_shot`, `bg_bomb`, `hg_home`, `missile`, `shot1`, `h_beam`, `smoke`
- stage major 1, sub 0 list A: `player`, `st2_1ani`, `s_gear`, `b_gear`, `h_gear`, `g_gear`, `p_gear`, `energy`, `1up`, `lamp`, `red`, `dmkmk2`, `f_catty`, `fpd`, `d_catty2`, `fwing`, `elevator`, `st2stone`
- stage major 1, sub 0 list B: `sg_shot`, `bg_bomb`, `hg_home`, `shot1`, `missile`, `vjr_bomb`, `b_smoke`
- This directly explains why sample files such as `1up.cad` and `b1.cad` do not appear as standalone string literals elsewhere in the binary: they are embedded as inline stage-table names.

### 2026-05-03 - Early startup CAD path around `0x00010040`
- The apparent stub at `0x00010040` is another Borland stack-check entry; raw bytes show the real body starts at `0x0001004a`.
- That body allocates `0x30` bytes of stack space, then copies 12 dwords from `0x00100010` into a local buffer via `rep movsd`.
- It uses the incoming stage-major index in `AX` to select an 8-byte entry from that local buffer:
- `basename_ptr = (stack_copy_base = ESP + 4) + major * 8`
- It separately derives a stage record at `0x0001d077 + major * 0x22fa` and loads the palette-like string at `stage + 0x2b` through the normal archive open helper `0x0002bfa7`.
- It then calls `0x0002c1cc` with:
- `EAX = basename_ptr`
- `EBX = 0x0010049e`
- `ECX = 0x001004a2`
- pushed outputs `0x001004a6` and `0x001004aa`
- After the load, it walks the first sequence and first frame immediately:
- `esi = [0x001004aa]`
- `esi = [esi]`
- `eax = [esi]`
- `call 0x0002d520` with screen coordinates `(0x4d, 0x54)`
- Important limitation: `0x00100010` is zero in the static image, so this basename table is runtime-initialized elsewhere. The startup caller structure is now clear, but the exact strings in that table are still unresolved.

### 2026-05-03 - Runtime CAD pointer chain and frame draw semantics
- The CAD output globals are not singletons; they participate in a repeating slot layout with stride `0x12` bytes.
- Proven slot fields used by runtime code:
- `0x0010049e + slot * 0x12`: raw-data blob base
- `0x001004a2 + slot * 0x12`: `0x5e` frame table base
- `0x001004a6 + slot * 0x12`: 6-byte sequence-entry table base
- `0x001004aa + slot * 0x12`: 4-byte sequence-start table base
- The consumer around `0x0002d7b5` / `0x0002d86d` materializes the chain exactly as expected:
- load `slot.table4` from `0x001004aa + slot * 0x12`
- choose a sequence start by reading one dword from that 4-byte table
- store that sequence-start pointer in object state at `obj + 0x10`
- dereference the first dword of the 6-byte entry to obtain the current `0x5e` frame pointer at `obj + 0x0c`
- copy the trailing word of the same 6-byte entry into object state at `obj + 0x0a`
- Additional cases such as `0x0002debe`, `0x0002e0b7`, `0x0002e2ee`, `0x0002e5f8`, and `0x0002e6f0` show the same pattern while selecting alternate sequences or indexing deeper into a sequence via `entry_index * 6`.
- Current conclusion: the 4-byte table is definitely a sequence-start table, the 6-byte table definitely stores `{frame_ptr, trailing_word}`, and the engine's live object state keeps both the current sequence-entry pointer and the copied trailing word.

### 2026-05-03 - `0x0002d520` draws one-part or two-part CAD frames
- The apparent `0x0002d520` stub is also mis-bounded in Ghidra; raw bytes show the real body starts at `0x0002d52a`.
- The helper consumes:
- `EAX = current 0x5e frame record`
- `EBX = Y coordinate`
- `EDX = X coordinate`
- It allocates entries from a draw queue backed by `0x000f7178`, using `0x001006bc` as a remaining-slot counter.
- When `word [frame + 0x4c] == 0`, it queues a single draw item:
- draw X = input X
- draw Y = input Y
- image/data pointer = `dword [frame + 0x56]`
- When `word [frame + 0x4c] != 0`, it queues a composite made from two image/data pointers:
- part 1 uses position `(X + word[frame + 0x4e], Y + word[frame + 0x50])` with data pointer `dword [frame + 0x56]`
- part 2 uses position `(X + word[frame + 0x52], Y + word[frame + 0x54])` with data pointer `dword [frame + 0x5a]`
- This is now the strongest source-backed interpretation of the late `0x5e` fields:
- `+0x4c`: single-part vs composite-frame flag
- `+0x4e/+0x50`: first-part X/Y delta
- `+0x52/+0x54`: second-part X/Y delta
- `+0x56/+0x5a`: raw-data-derived image pointers that the loader fixes up in `0x0002c1cc`
- Current limitation: fields earlier than `+0x4c` still need a separate consumer pass.

### 2026-05-03 - Early frame offsets and raw chunk headers
- Additional live draw callers around `0x00060d0c`, `0x000604d9`, and `0x00063409` compute screen-space coordinates before calling `0x0002cfd0` by doing:
- `draw_x = object_x + word[frame + 0x00]`
- `draw_y = object_y + word[frame + 0x02]`
- This proves the first two words of the `0x5e` frame record are anchor offsets applied before the queue helper sees the frame.
- The raw image data pointed to by `dword [frame + 0x56]` is self-describing at the chunk level: each referenced chunk begins with little-endian `u16 width, u16 height`.
- Sample-validated chunk headers:
- `1up.cad`: offsets `0x0000`, `0x0262`, and `0x04c4` all begin with `22 x 35`
- `b1.cad`: offset `0x0000` begins with `50 x 60`; offsets `0x0780` and `0x0dd3` begin with `43 x 61`; offset `0x152c` begins with `51 x 60`
- Reused frame pointers are real reuse, not parser error. Example: `1up.cad` frames 1 and 3 both point at raw offset `0x0262`.
- Current conclusion: graphics extraction should pivot from "decode the full 0x5e record first" to "decode the width/height-prefixed raw chunk stream referenced by the frame record".

### 2026-05-03 - Raw chunk decoder recovered from `0x000b51cf`
- The queue worker at `0x000b51a1` iterates `{s16 x, s16 y, u32 ptr}` entries and passes each raw chunk pointer to `0x000b51cf`.
- `0x000b51cf` is a direct unclipped blitter for the width/height-prefixed chunk format used by the extracted `.bin` chunks.
- The chunk layout is:
- `u16 width`
- `u16 height`
- row bytecode stream for exactly `height` rows
- Recovered opcodes from the original binary:
- `0xff`: end current row and advance destination by the fixed screen stride `0x140`
- `0x00 <skip>`: transparent skip; advance the destination X position by `<skip>` pixels
- `0x01..0x7f`: literal copy of `opcode` bytes directly from the stream
- `0x80..0xfe`: repeat packet of `count = opcode & 0x7f`; read a 4-byte pattern and write it using the exact `stosb`/`stosd` behavior from the binary
- This resolves the earlier ambiguity around apparent `ff 00` pairs: `0xff` is the row terminator, and the following `0x00` is usually just the next row's first opcode meaning `skip 0`.
- A standalone C# decoder implementing the exact `0x000b51cf` grammar now exists in `DOS/Zyclunt/ZyCleaver` and successfully decodes all currently extracted raw chunks to debug PNGs.
- Validation results:
- all `1up` and `b1` raw chunks in `DOS/Zyclunt/TestOutput/raw-chunks` decoded without errors
- every currently extracted chunk consumed all but one trailing byte
- the trailing byte is consistently padding in the current samples, not additional image data required by the row bytecode loop
- visual sanity check: the decoded `1up` chunk produces a plausible sprite silhouette in `DOS/Zyclunt/TestOutput/decoded-chunks`
- Current conclusion: we now have a working raw chunk decoder for the extracted CAD image payloads, albeit using a debug palette rather than the game's real palette.

### 2026-05-03 - 6-byte CAD entries are countdown timers with sentinel control entries
- Multiple runtime consumers now show the same pattern around live object field `obj + 0x0a`, including `0x00041a70`, `0x00060d16`, and `0x0006509a`.
- Those paths do the following every tick:
- decrement `obj + 0x0a`
- if it is still nonzero, keep the current frame/sequence entry
- if it reaches zero, advance `obj + 0x10` by 6 bytes to the next 6-byte entry, increment the local frame index, load `obj + 0x0c = dword [obj + 0x10]`, then reload `obj + 0x0a = word [obj + 0x10 + 4]`
- This proves that for ordinary entries the trailing word in the 6-byte table is a countdown duration or dwell time.
- The dword field in the same 6-byte entry is not always a real frame pointer. Runtime code explicitly treats two sentinel values specially:
- `dword == -2`: loop/backtrack control entry. The handler subtracts `word [entry + 4]` from the current 6-byte-entry pointer, reloads the target entry, and restores both `obj + 0x0c` and `obj + 0x0a` from that target.
- `dword == -1`: state/sequence transition entry. The handler backs up one entry, adjusts object-specific sequence/state fields, chooses a new sequence from `obj + 0x14`, and then reloads the current entry. The exact gameplay meaning depends on the caller.
- Current conclusion: the 6-byte CAD table is an animation script table, not just a flat frame list. Normal entries are `{frame_ptr, duration}`; sentinel entries use the same 16-bit field as a control argument.

### Working assumptions
- Confirmed from the binary: `.CAD` assets are requested as `<basename>.cad` and opened from `zyclunt.jam`.
- Assumption: the conditional 0x400-byte auxiliary block in `0x0002c1cc` is a palette, lookup, or decode table, but its exact semantics are still unknown.
- Assumption: the 0x5e-byte records read by `0x0002c1cc` are CAD object/frame descriptors; `+0x00/+0x02` are now proven draw-anchor offsets and late fields at `0x4c..0x5a` have strong draw-path semantics, but the remaining early fields are still unresolved.
- Assumption: the 2-byte `unk1` field after `aux_flag` is a logical object/frame/group count or similar high-level CAD descriptor, but the loader itself does not use it.
- Assumption: sentinel entry `dword == -1` is a high-level sequence/state transition marker, but the exact gameplay-level interpretation is still caller-specific and not yet generalized.
- Assumption: the decoded raw chunk bytes are palette indices into an external palette source, but the exact palette source and per-scene palette selection logic are still unresolved.
- Assumption: some important file-handling code is present but mis-bounded in Ghidra because of Borland-generated prologues/thunks; raw bytes are currently more trustworthy than the decompiler alone in these regions.

### Next checks
- Replace the debug palette in the standalone decoder with the game's real palette source for each scene or asset class.
- Continue decoding the unresolved early `0x5e` fields beyond the now-proven anchor offsets at `+0x00/+0x02`.
- Classify the remaining 6-byte sentinel/control behavior, especially the generalized meaning of `dword == -1` entries across different callers.
- Find the writer that initializes the startup basename table copied from `0x00100010`, or otherwise recover that table from a neighboring caller path.
- Determine the exact meaning of the unresolved header field `unk1`.
- Only drop to raw DOS interrupt usage if the helper layer around `0x0002bfa7`, `0x000bb771`, and `0x000bb12f` stops being sufficient.
