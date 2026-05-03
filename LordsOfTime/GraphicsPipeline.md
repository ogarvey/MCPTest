# Graphics pipeline (DecodeRle16_Fill3500)

## Goal

Understand how asset data flows into a renderable image so we can export image data to PNG from original game files.

---

## Decoder focus: `DecodeRle16_Fill3500`

**Purpose (current understanding)**

- RLE-style 16-bit word decoder with a constant-fill mode (`0x3500`).
- Writes output in a wrapped layout with a stride of `0x7d` words and a skip of `0x7e` words per line, suggesting interleaved/planar or screen-buffer layout.

**Inputs (by register)**

- `A5`: source stream pointer (compressed data).
- `A2`: destination buffer pointer (screen/bitmap base + offset).

**Stream format**

- Each 16-bit `count` value is read from `A5`.
- `count == 0` terminates.
- If `count & 0x8000 == 0`: copy `count` *pairs* of words from source to destination.
- If `count & 0x8000 != 0`: write `count & 0x7FFF` *pairs* of constant word `0x3500`.

---

## Known usage (call site in `FUN_00230cfa`)

`DecodeRle16_Fill3500` is invoked only in the `DAT_00262330 == 0x15` branch.

**Setup (from disassembly around 0x00230e40):**

- `A5 = DAT_00262534 + sum(DAT_002ee550[0..DAT_00262a83))`
  - `DAT_00262534` appears to be a base pointer for compressed data blocks.
  - `DAT_002ee550` looks like a table of block sizes or offsets.
- `A2 = 0x234358 + *(0x2EE30C + (DAT_0026e124 * 4))`
  - `0x234358` is a screen/bitmap base used by `Blit5PlaneCell` as well.
  - `0x2EE30C` is likely a table of per-plane/region offsets.

**Interpretation**

This suggests record type `0x15` decodes a compressed block into a screen/bitmap buffer region, likely a graphics asset (screen, tile, sprite layer, etc.).

**Uncached path (new)**

When the cached block is missing (`DAT_00262534` entry is `-1`), `FUN_00230cfa` performs a `DOSBase - 0x2a` read into `DAT_0026fa28` (see xrefs at `0x0023129e` / `0x002312ce`), then calls `DecodeRle16_Fill3500` with `A5 = DAT_0026fa28`. After decode, it copies the compressed block from `DAT_0026fa28` into the cache at `DAT_00262534 + sum(DAT_002ee550[0..DAT_00262a83))`.

This strongly implies:

- `DAT_0026fa28` is the **staging buffer** for compressed block reads.
- `DAT_002ee550[block]` likely holds the **compressed block size in bytes**, since it is used to copy `(size >> 2)` longs into the cache.

**Read call details (from 0x00231240 memory window)**

Immediately before the uncached decode, the code sets up a `DOSBase - 0x2a` call with:

- `D1 = FileHandle` (`move.l (0x002624dc).l, D1`)
- `D2 = 0x0026fa28` (staging buffer)
- `D3 = 0x00006590` (read length)

Then it decodes directly from `A5 = 0x0026fa28`.

The size table at `DAT_002ee550` includes `0x6590`, so the uncached read is likely using a **block-size constant** that matches the current `DAT_00262a83` entry. We still need to confirm whether the read length is always pulled from the table or is fixed for this specific record path.

Sample of `DAT_002ee550` values (first 16 entries):

`0x1388, 0x3E80, 0x4650, 0x4650, 0x2AF8, 0x4650, 0x61A8, 0x6590, 0x61A8, 0x4A38, 0x4A38, 0x61A8, 0x6590, 0x61A8, 0x4A38, 0x3A98`

---

## Record-0x15 request helper

`FUN_0022d372` (unnamed) prepares a record-0x15 request and immediately calls `FUN_00230cfa()`:

- Sets `DAT_00262330 = 0x15` (record type)
- Sets `DAT_00262a83 = D0b` (block index)
- Updates `DAT_00262328` to a string template with a letter slot (likely a filename or label)

**Implication:** `DAT_00262a83` selects which compressed block is decoded, and `FUN_0022d372` is the primary “request image block” helper that drives the pipeline.

**Block indices seen at startup (new):**

Disassembly near `0x0021f8d0` shows explicit `D0` values before the four startup calls to `FUN_0022d372()`:

- `DAT_0026e124 = 0`, `D0 = 0x07`
- `DAT_0026e124 = 1`, `D0 = 0x08`
- `DAT_0026e124 = 2`, `D0 = 0x0C`
- `DAT_0026e124 = 3`, `D0 = 0x0D`

These map to size table entries at `DAT_002ee550`:

- `0x07 -> 0x6590`
- `0x08 -> 0x61A8`
- `0x0C -> 0x6590`
- `0x0D -> 0x61A8`

This aligns with the uncached read length of `0x6590` we observed in the decode path for at least some of these blocks.

**Other callers (variable index):**

At `0x00225420` (in `FUN_00225256`), `D0` is loaded from `DAT_0026e120` and then adjusted before each `FUN_0022d372` call:

- `D0 = DAT_0026e120 + 0`
- `D0 = DAT_0026e120 + 1`
- `D0 = DAT_0026e120 + 5`
- `D0 = DAT_0026e120 + 6`

So block indices are **not fixed**; the selection is dynamic in other contexts. This increases the likelihood that the read length should be driven by `DAT_002ee550[D0]` rather than a hardcoded constant, and we should confirm how the uncached read length is computed in those cases.

---

## Current pipeline hypothesis

1. **File access / stream init** via `DAT_002624e4` vtable (likely `dos.library` vectors). These calls appear to open a resource/volume and prime a stream buffer.
2. **Asset record dispatch** in `FUN_00230cfa` reads a record type into `DAT_00262330`.
3. For type `0x15`, the engine computes:
   - **Source pointer** in a compressed heap (`DAT_00262534 + sum(table)`),
   - **Destination pointer** in the screen buffer (`0x234358 + offset`).
4. **Decode** using `DecodeRle16_Fill3500` into the screen/bitmap buffer.
5. **Display copy** may occur via `Blit5PlaneCell` or related routines (not yet tied to this branch).

---

## Data gaps to close

- **What fills `DAT_00262534`?** Identify where compressed data is loaded from disk.
- **What is the structure at `DAT_002ee550`?** Confirm whether entries are sizes or offsets.
- **What is `DAT_0026e124`?** Determine how it selects the destination region.
- **What is the real pixel layout of `0x234358`?** Confirm planar/interleaved format and dimensions.

---

## New findings (source buffer + plane selector)

### `DAT_00262534` (compressed data heap)

- **Only direct write** found in `start` (initialization).
- The pointer is allocated via an Exec-style call and then the buffer is filled with `0xFFFFFFFF` sentinel pairs (same pattern as `DAT_0026252c`).
- No other direct writes to the pointer itself were found; actual **data population likely happens via indirect writes** (stream reads into this buffer), not via `move.l` to the global.

**Implication:** `DAT_00262534` is a pre-allocated heap for compressed asset blocks. We still need to find the file read path that writes into it.

### `DAT_002ee550` (block size/offset table)

- No writes found; referenced only for reading and summing.
- Likely a **static table** of per-block sizes or offsets used to index into `DAT_00262534`.

### `DAT_0026e124` (plane/buffer selector)

- Written frequently with values `0..3` in multiple routines (`start`, `FUN_00224530`, `FUN_00225256`, `FUN_0022c9b6`, `FUN_0022cb1a`, `FUN_0022cc92`, `FUN_0022ce30`, `FUN_0022e418`).
- These writes are immediately followed by `FUN_0022d372()` calls, suggesting `DAT_0026e124` selects a **plane or sub-buffer** for subsequent updates.

**Implication:** The decode destination offset table at `0x2EE30C` is indexed by this selector. This strongly points to a 4-plane or 4-region layout.

---

## Confirmed library bases

- `DOSBase` (`0x002624e4`) is opened from "dos.library" via `OpenLibrary`.
- `GfxBase` (`0x002624e8`) is opened from "graphics.library".

These confirm that file I/O and graphics primitives are coming from standard Amiga libraries. Exact vector mappings still need confirmation, but the base identities are now solid.

---

## Palette source (new)

The copper list at `DAT_00233f40` contains a **static palette block** written as register/value pairs for `COLOR00..COLOR31` (`0x0180..0x01BE`). This strongly suggests the palette is **not read from the file**, but embedded in the game’s copper list.

From the copper list dump:

- `0x0180 = 0x0057`
- `0x0182 = 0x0000`
- `0x0184 = 0x0222`
- `0x0186 = 0x0555`
- `0x0188 = 0x0777`
- `0x018A = 0x0AAA`
- `0x018C = 0x0CCC`
- `0x018E = 0x0FFF`
- `0x0190 = 0x0030`
- `0x0192 = 0x0040`
- `0x0194 = 0x0050`
- `0x0196 = 0x0060`
- `0x0198 = 0x0070`
- `0x019A = 0x0080`
- `0x019C = 0x0090`
- `0x019E = 0x00B0`
- `0x01A0 = 0x000F`
- `0x01A2 = 0x002F`
- `0x01A4 = 0x003E`
- `0x01A6 = 0x005E`
- `0x01A8 = 0x007E`
- `0x01AA = 0x0420`
- `0x01AC = 0x0531`
- `0x01AE = 0x0642`
- `0x01B0 = 0x0753`
- `0x01B2 = 0x0864`
- `0x01B4 = 0x0B97`
- `0x01B6 = 0x0F00`
- `0x01B8 = 0x0F40`
- `0x01BA = 0x0F90`
- `0x01BC = 0x0FE0`
- `0x01BE = 0x000F`

This is a 32‑entry palette (consistent with 5 bitplanes). If we can confirm the pixel format and dimensions, we can use these color registers to build PNGs.

**Display geometry hint (tentative):** the copper list also sets `BPLCON0 = 0x5200` (5 bitplanes) and uses `DDFSTRT = 0x38`, `DDFSTOP = 0xD0` with `DIWSTRT = 0x2C81`, `DIWSTOP = 0xF4C1`, which is a common **low‑res 320×200** setup. This still needs to be validated against the buffer stride/decoder layout.

---

## GfxBase wrappers (LVO -0x1c8 / -0x1ce)

Two tiny wrapper functions (`FUN_00233000`, `FUN_00233048`) save register arguments and then call `GfxBase` with LVOs `-0x1c8` and `-0x1ce`.

Call sites (`FUN_0022d24e`, `FUN_0022f3c6`, `FUN_002304fc`, `FUN_00230a34`) invoke them as a **paired bracket** around large batches of blitter setup calls (`FUN_0022d34e`), with **no explicit argument setup** in registers.

**Tentative ID:** likely `OwnBlitter` / `DisownBlitter` for bracketing blitter usage (needs confirmation via LVO table).

---

## Stream read/skip helpers (observed)

- `FUN_002311d8` and `FUN_00231242` perform a loop of `0x6c` calls to `DOSBase - 0x2a` **with a file handle** in `D1` (`FileHandle`), a destination pointer in `D2` (`A2`), and length `D3 = 0x1e`.
- Each iteration advances `A2` by `0x28` (`FUN_002311d8`) or `0x2e` (`FUN_00231242`), implying **interleaved row/plane data** with padding.
- `FUN_00231656` calls `DOSBase - 0x24` to close the stream/handle.

**Implication:** `DOSBase - 0x2a` is behaving like `Read()` (handle + buffer + length) and these helpers are reading structured graphics data into memory with a fixed stride.

---

## Next concrete steps (updated)

- Trace the **indirect writes into `DAT_00262534`** by locating stream read functions and their destination pointers.
- Decompile `FUN_0022d372` to determine how `DAT_0026e124` affects rendering (plane selection vs. region selection).
- Identify where palette data is loaded/applied; current refs to hardware color registers are not obvious yet.

---

## Next concrete steps

- Trace writes to `DAT_00262534` and `DAT_002ee550` to find the file-reading path and confirm compression.
- Identify any palette load routines and palette memory layout (needed for PNG export).
- Search for other decoders with similar stride patterns (`0x7d/0x7e`) that might handle different asset types.
- Identify the final render buffer dimensions to reconstruct image output correctly.
