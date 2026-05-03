## Current focus

Initial analysis of `FUN_00230cfa`, `FUN_0022c3fc`, `FUN_0022e07c` (fresh project; names are placeholders unless library).

---

## `FUN_00230cfa`

**High-level behavior**

- Acts like a **dispatcher/state machine** driven by `DAT_00262330` (command/record type).
- Frequently checks `DAT_0026252c` (pointer to a base table/asset block) and tests entries for `-1` to decide whether data is present.
- Uses a **function pointer table** at `DAT_002624e4` with negative offsets (e.g., `-0x1e`, `-0x54`, `-0x7e`, `-0x5a`, `-0x2a`, `-0x30`, `-0x24`). These look like file/stream I/O primitives (read byte/word, refill, etc.).
- When stream I/O indicates “no data” (`DAT_002624e0 == 0`), it resets state and calls `FUN_0022e07c()` (which itself triggers re-init/retry logic) then loops until data becomes available again.

**Notable branches**

- `DAT_00262330 == 0x21 / 0x29 / 0x23 / 0x16 / 0x13`: checks asset table offsets from `DAT_0026252c` and calls `FUN_00230cf2()` or `FUN_00231388()` if data is present.
- `DAT_00262330 == 0x15`: uses `FUN_00233d4a()` and `DAT_00262534` (offset?) to locate a block; if present, calls `FUN_00233d68()`; later copies a block from `DAT_0026fa28` into that destination.
- `DAT_00262330 == 0x17 / 0x18`: reads multiple values via pointer table ops (`-0x2a` or `-0x30`) in a fixed loop (`0x5d` iterations), then stores `DAT_00262510` (for 0x17).
- Several cases call `FUN_002311d8()` or `FUN_00231242()` five times and then `FUN_00231656()` — likely fixed-size reads or a repeated decoding step.

**Interpretation (tentative)**

This function looks like the **main asset record dispatcher**: it reads a record type (`DAT_00262330`), checks/updates cache tables (`DAT_0026252c`, `DAT_00262534`), and then invokes record-specific read/decode routines via table-driven I/O. `FUN_0022e07c()` is used as a recovery/refill path when the read stream underflows.

---

## `FUN_0022c3fc`

**High-level behavior**

- Stores `in_A1` into `DAT_0026e13c`.
- Outer loop runs until `uVar1 == 0x1f`.
- Inner loop repeatedly calls `FUN_0022c49e()` four times per iteration, then decrements `uVar1` until it wraps to `0xffffffff`.
- On **every even** `uVar1` value, it calls:
	- `FUN_0022d9bc()` (captures registers into globals)
	- sets `DAT_0026e328 = 1`, `DAT_0026e034 = 7000`, `DAT_0026e030 = 1`
	- `FUN_00231980()` (menu/UI operation?)
	- `FUN_0022d9da()` (returns `*DAT_0026251c`)

**Callee summary**

- `FUN_0022c49e()` copies 5 planes/rows of bytes from a source screen buffer at `0x234358` into a destination buffer at `0x2d504c`, using coordinates `(in_D0, in_D1)` (bounds: x < 0x1e, y < 0x1b). This is **blit/copy** logic, not decompression.
- `FUN_0022d9bc()` stores all A/D registers into globals and sets `DAT_0026251c = &DAT_002623ec`.
- `FUN_0022d9da()` returns `*DAT_0026251c` (a simple getter for that saved register block).

**Interpretation (tentative)**

`FUN_0022c3fc()` looks like a **frame timing + blitting loop** (screen copy) combined with a periodic UI/menu update on even iterations. It does not appear to be an asset decoder, but rather part of display refresh or a transition effect.

---

## `FUN_0022e07c`

**High-level behavior**

```
FUN_0022e07c():
	FUN_0022dfea();
	FUN_0022e044();
```

**Callee summary**

- `FUN_0022dfea()` saves all A/D registers to globals, increments `DAT_00262a88`, sets several state variables (`DAT_0026233c`, `DAT_00262340`, `DAT_0026234a`), points `DAT_0026e038` to `DAT_002d504c`, then calls `FUN_0022deac()`.
- `FUN_0022e044()` stores `in_D0` into `DAT_00262500`. If `DAT_00262a88 < 6`, it repeatedly calls `FUN_0022dfea()` until it returns `1`, then resets `DAT_00262a88` to 0.

**Interpretation (tentative)**

This pair looks like a **stream/pipeline reset & retry** sequence. It is invoked by `FUN_00230cfa()` when stream reads return 0, suggesting that `FUN_0022e07c()` reinitializes a buffer/decoder (possibly related to the screen buffer at `DAT_002d504c`) and waits for a “ready” flag.

---

## Next steps

- Inspect the function pointer table at `DAT_002624e4` to label the I/O primitives (read byte/word, read block, etc.).
- Decompile `FUN_00230cf2`, `FUN_00231388`, `FUN_00233d4a`, `FUN_00233d68`, `FUN_0022ec28`, `FUN_002317b6`, `FUN_00231926` to identify the actual **asset decode paths** used by `FUN_00230cfa()`.

---

## Follow-up: likely I/O + graphics related helpers

### `CopyWords_A3_to_A2` (was `FUN_00230cf2`)

- Tight loop copying 16-bit words from `A3` to `A2` for `D0` elements.
- Pure memory copy; not a decoder. Used by `FUN_00230cfa()` when a table entry is present.

### `CopyWords_A2_to_A3` (was `FUN_00231388`)

- Same pattern as `FUN_00230cf2`, but copies from `A2` to `A3` (reverse direction).
- Pure memory copy; not a decoder.

### `FUN_00233d4a`

- Sums `unaff_D4` elements from table `DAT_002ee550` and returns the total.
- Likely computes a base offset into a resource block or a record list.

### `DecodeRle16_Fill3500` (was `FUN_00233d68`)

- Reads a **run-length–style stream** from `A5` and writes 16-bit words into `A2`.
- Command format: each 16-bit `uVar1` count; if `uVar1 == 0` then end.
- If high bit clear: copy `uVar1` *pairs* of words from source to destination.
- If high bit set: output `uVar1 & 0x7FFF` *pairs* of **constant** words `0x3500`.
- Writes are arranged in a wrapped layout using a line width of `0x7d` words, then skips by `0x7e` (suggests **interleaved screen/bitmap layout**).

**Graphics relevance:** This looks like a **16-bit RLE-style decompressor** into a planar/row-strided buffer (likely image/tile/bitmap data). The constant fill value `0x3500` might be a transparent or background color word.

### `DelayLoop_D0` (was `FUN_0022ec28`) / `DelayLoop_100000` (was `FUN_002317b6`)

- Simple busy-wait delays (countdown loops). Not related to decoding.

### `WaitForStreamReady` (was `FUN_00231926`)

- Polls an I/O function pointer at `DAT_002624e4 - 0x72` until non-zero, and repeats while `DAT_0026f470 == 0x51`.
- This looks like **stream polling** (wait for data / sync), reinforcing that `DAT_002624e4` is a stream or file I/O vtable.

---

## Updated interpretation (compression/graphics signals)

- `DecodeRle16_Fill3500` is the first clear **decompressor** encountered. It is RLE-like on 16-bit words and writes in a wrapped layout, consistent with Amiga planar or interleaved bitmap buffers.
- The **actual file reading** still appears to be via the function table at `DAT_002624e4` (negative offsets). We should map those entry points next to identify `readByte`, `readWord`, `readBlock`, `seek`, etc.

## Next steps (refined)

- Identify call sites that set `A2`/`A5` before `FUN_00233d68` to understand source/dest buffers and the input stream.
- Map the I/O vtable at `DAT_002624e4` by decompiling the functions at offsets: `-0x1e`, `-0x54`, `-0x7e`, `-0x5a`, `-0x2a`, `-0x30`, `-0x24`, `-0x72`.
- Search for other routines writing in `0x7d/0x7e` strides or using constant word fills (likely other graphics decode paths).

---

## I/O vtable mapping (preliminary)

`DAT_002624e4` is loaded into `A6` and used as a **library/driver base** with negative offsets (classic Amiga pattern). Based on the WinUAE library list (dos.library present), the usage strongly suggests **dos.library file/stream operations**, but exact vectors still need confirmation.

Observed offsets and evidence:

- **`A6 - 0x1e`**
	- Called in `FUN_00230cfa` using values from `(0x8,A4)` and `(0x46,A4)` and returning `D0` into `DAT_002624dc`.
	- Likely **open/read-init** of a resource using filename/descriptor stored in the `DAT_00262320` structure.

- **`A6 - 0x54`**
	- Used as a polling call in `FUN_00223184`, `FUN_002255b4`, `FUN_002257f8`. If it returns zero, the code retries or prompts for disk.
	- Likely **file existence / directory scan / open** (returns handle or status).

- **`A6 - 0x7e`**
	- Called after `-0x54` succeeds, often followed by `A6 - 0x5a` when non-zero.
	- Likely **read/examine** of the handle returned by `-0x54`.

- **`A6 - 0x5a`**
	- Called when `-0x7e` returns non-zero; appears to finalize/advance.
	- Likely **close/next** in a file or directory iteration sequence.

- **`A6 - 0x72`**
	- Polled in `FUN_00231926` until non-zero; used with `DAT_0026f470 == 0x51` loop.
	- Looks like **stream ready / input polling**.

- **`A6 - 0xc6`**
	- Used after disk prompts and at transitions (e.g., after `FUN_0022e07c()` and volume messages).
	- Likely **reset/flush/close volume**.

- **`A6 - 0xae`**
	- Used in `FUN_00233090()` to fetch a pointer and clear a field at offset `0x5c`.
	- Possibly **message/IO request retrieval** (structure with a status field).

We should annotate these once we confirm parameter registers via disassembly or identify the library.

---

## Call-site details for `FUN_00233d68` (graphics decode)

`FUN_00233d68` is invoked from `FUN_00230cfa` only, in the `DAT_00262330 == 0x15` branch.

Setup before the jump:

- `A5` = `DAT_00262534` + `sum(DAT_002ee550[0..DAT_00262a83))`
	- This suggests **compressed source data** lives in a table/heap at `DAT_00262534`.
- `A2` = `0x234358` + `*(DAT_0026e124 * 4 + 0x2EE30C)`
	- `0x234358` is a **screen/bitmap base** (also used by blitter copy in `FUN_0022c49e`).
	- The table at `0x2EE30C` likely contains per-plane/region offsets into the screen buffer.

**Implication:** record type `0x15` is likely **graphics decompression** into the screen buffer, using the RLE-like 16-bit decoder in `FUN_00233d68`.

**Uncached path (new evidence from xrefs):**

- `FUN_00230cfa` also calls `DecodeRle16_Fill3500` at `0x002312a4`.
- Just before this call, it loads `A5` with `DAT_0026fa28` (`lea (0x26fa28).l,A5` at `0x0023129e`).
- After decode, it copies `(DAT_002ee550[block] >> 2)` longs from `DAT_0026fa28` into the cache at `DAT_00262534 + sum(DAT_002ee550[0..block))`.

**Interpretation:** `DAT_0026fa28` is a **staging buffer** for compressed block reads; the read size is likely `DAT_002ee550[block]` bytes. The cached path uses `DAT_00262534` as the compressed source when present; the uncached path reads into `DAT_0026fa28`, decodes, then caches the compressed block.

**Read register setup (from raw disassembly near `0x00231240`):**

- `D1 = FileHandle` (`move.l (0x002624dc).l, D1`)
- `D2 = 0x0026fa28` (`move.l #0x0026fa28, D2`)
- `D3 = 0x00006590` (`move.l #0x6590, D3`)
- `jsr (-0x2a, A6)` to read into the staging buffer

This suggests the uncached read uses a **fixed length of 0x6590** in this path (which matches one of the size table entries at `DAT_002ee550`). We still need to confirm whether the length is always table‑driven or this is a fixed‑size record variant.

**Startup block indices (new):** disassembly at `0x0021f8d0` shows `D0` set explicitly before `FUN_0022d372`:

- `D0 = 0x07`, `0x08`, `0x0C`, `0x0D` (paired with `DAT_0026e124 = 0..3`).

These correspond to `DAT_002ee550` sizes `0x6590`, `0x61A8`, `0x6590`, `0x61A8` respectively.

**Variable block indices (new):** another call site (`0x00225420` in `FUN_00225256`) loads `D0` from `DAT_0026e120` and adds offsets `+0`, `+1`, `+5`, `+6` before calling `FUN_0022d372`. This indicates `DAT_00262a83` can be dynamic, not fixed to a small set of constants.

---

## Graphics-related cues found while mapping I/O

- `FUN_002257f8` performs a read/poll sequence and calls `WaitForStreamReady` (stream polling). This likely **feeds file data** that later becomes asset decode input.
- `Blit5PlaneCell` and `DecodeRle16_Fill3500` both target the screen buffer at `0x234358`, indicating that **asset decode output is being blitted directly to the display**.

**Palette location (new):**

The copper list at `DAT_00233f40` includes `COLOR00..COLOR31` register writes (`0x0180..0x01BE`). This suggests the palette is **embedded in the copper list**, not loaded from file assets. The list is static in memory and ends with a `0x9EE1 / 0xFFFE` terminator.

---

## GfxBase wrappers (new)

`FUN_00233000` and `FUN_00233048` are thin wrappers that save register arguments into globals and then call `GfxBase` with LVOs `-0x1c8` and `-0x1ce` respectively.

Observed call sites (`FUN_0022d24e`, `FUN_0022f3c6`, `FUN_002304fc`, `FUN_00230a34`) invoke them as a pair around large batches of blitter setup (`FUN_0022d34e` sequences), with **no argument setup** in registers immediately before the calls.

**Interpretation (tentative):** these LVOs are likely `OwnBlitter` (`-0x1c8`) and `DisownBlitter` (`-0x1ce`), used to bracket blitter operations. Needs confirmation via LVO table or additional context.

---

## Next steps (updated)

- Disassemble around `A6 - 0x1e / -0x54 / -0x7e / -0x5a` call sites to confirm register-based parameters and match to DOS/Exec library vectors.
- Trace where `DAT_00262534` is filled (compressed data source) to determine whether raw files are **compressed or stored pre-expanded**.
- Search for other decoders that write to `0x234358` or use the `0x7d/0x7e` stride pattern.
