## Asset archive / map loading (Desert2: desert.dat)

### EnsureDesertDatMountedAndLoadMap (formerly FUN_002233b8 @ 0x002233b8)
- **Summary:** One-time archive setup and map resource load.
- **Key strings:** `"Desert2:"`, `"desert.dat"`, `"DES4:ANI"`, `"!MAP"`.
- **Behavior:**
	- On first run, checks a global flag and then:
		- Calls an A4-relative function pointer twice (likely disk/volume availability check).
		- Sets a “mounted” flag and, if the second check fails, sets a secondary “needs attention” flag.
		- Invokes several A4-relative helper routines, then calls a function with `Desert2:` + `desert.dat`.
		- Calls `FUN_0021fb62("desert.dat")` and `FUN_00228110()` (likely disk prompt / archive initialization).
	- Always sets a global state flag and calls `FUN_00222610()` (UI/coordinate update).
	- Selects/opens `DES4:ANI` (A4-relative call with the string).
	- Looks up the `!MAP` resource via an A4-relative call, yielding a handle.
	- If a handle is returned, loads/decodes it into memory at `0x2b1e` (A4-relative call with handle + dest).
	- Releases the handle and performs a final A4-relative cleanup call.

**Name rationale:** The function ensures `desert.dat` is active and then loads the `!MAP` resource into memory.

### Callers
- **FUN_0022348e**: Calls `EnsureDesertDatMountedAndLoadMap()` then performs a single A4-relative cleanup call. Likely a short “load map and refresh” wrapper.
- **LoadDes4AniResourcesAndMap (formerly FUN_0022351a)**: Performs animation/view setup, opens `DES4:ANI`, loads several animation resources, then calls `EnsureDesertDatMountedAndLoadMap()` before freeing those resources.

### Notes / hypotheses for indirect calls
- `A4-0x7ff2(Desert2:, desert.dat)` likely selects or mounts the Desert2 volume/archive.
- `A4-0x7d76("DES4:ANI")` likely selects/opens an animation resource namespace.
- `A4-0x7dd0("!MAP")` appears to return a resource handle.
- `A4-0x7e06(handle, 0x2b1e)` likely loads/decompresses resource data into a fixed buffer.
- `A4-0x7da6(handle)` likely releases the resource handle.
- `A4-0x7d7c()` likely performs a flush/close/cleanup for resource access.

## DES4:ANI pre-load (no direct parsing observed)

### LoadDes4AniResourcesAndMap (formerly FUN_0022351a @ 0x0022351a)
- **Summary:** Opens the `DES4:ANI` archive and pre-loads a small set of named animation resources, then loads the `!MAP` resource via `EnsureDesertDatMountedAndLoadMap()`. The function **does not parse ANI data directly**; it delegates resource loading to A4-relative helpers.
- **Key strings:** `"DES4:ANI"`, `"SANT"` (at `DAT_00223637`), `"XMARK"`, plus a 4-entry name table (likely `"AMAN"`, `"PMAN"`, `"CMAN"`, `"TMAN"`).
- **Behavior:**
	- Calls a setup routine, sets multiple UI/position flags, and selects `DES4:ANI`.
	- Iterates 4 names from an A4-relative table (A4-0x7aa2), calling a loader to get a handle and then a “use/lock” routine per handle.
	- Loads `SANT` and `XMARK` via the same handle workflow.
	- Calls `EnsureDesertDatMountedAndLoadMap()` to load `!MAP`.
	- Releases all loaded handles (likely unload/free) and restores UI/position state.

**Call context:** Invoked from `FUN_00221cc8` in state `0x0F`, suggesting this is part of a mode transition that needs animation resources plus the map.

## Archive index parsing / resource lookup

### OpenArchiveAndLoadIndex (formerly FUN_0023f2c4 @ 0x0023f2c4)
- **Summary:** Opens a `VOL:FILE` archive (e.g., `DES4:ANI`) and loads the index table into memory.
- **Behavior (confirmed):**
	- Splits the input string at `:` into volume and file components.
	- Calls the volume mount/select routine (same A4-0x7ff2 seen in map loading).
	- If the requested archive matches the currently open one, returns success without reloading.
	- Otherwise, closes any previous archive state and opens the file.
	- Reads a **2-byte entry count** into `A4+0x2b18` and a **4-byte table size** into `A4+0x2b14`.
	- Allocates a buffer of `table_size` bytes and reads the entire index table into it (`A4+0x2ade`).
	- Returns success if all steps complete; failure if open/alloc fails.

**Matches the observed archive layout:**
- File header: `u16 entry_count`, `u32 table_size` (total bytes of index).
- Index data immediately follows (size = `table_size`).
- Asset data begins at offset `6 + table_size`.

### FindEntryAndSeek (formerly FUN_0023f3de @ 0x0023f3de)
- **Summary:** Scans the loaded index for a named entry, then seeks the file to the entry’s data start.
- **Entry layout (per loop):**
	- `name` (NUL-terminated, variable length)
	- `u32 offset` (relative to data start)
	- `u32 size`
- **Behavior:**
	- Compares names using `StrEqualsIgnoreCase`.
	- On match, seeks to `offset + table_size + 6` and stores `size` in `A4+0xac2`.
	- Returns the archive handle on success, `0` on miss.

### CloseArchiveAndFreeIndex (formerly FUN_0023f266 @ 0x0023f266)
- **Summary:** Closes the current archive, frees the index buffer, and clears cached state.

### StrEqualsIgnoreCase (formerly FUN_0023f204 @ 0x0023f204)
- **Summary:** ASCII case-insensitive string equality used for index name matching.

### ReadU32 (formerly FUN_0023f2aa @ 0x0023f2aa)
- **Summary:** Simple 32-bit load helper used to read `offset` and `size` from the index table.

## Decompression (resource payloads)

### LzwDecompressResource (formerly FUN_0023f69c @ 0x0023f69c)
- **Summary:** LZW-style decompressor (9–12 bit codes) used for resource payloads after `FindEntryAndSeek`.
- **Observed input layout (from file stream):**
	- **Caller is expected to consume the marker** (`"LZ"`) before invoking.
	- 1 byte (saved and written to final output byte).
	- 4 bytes: uncompressed size (big endian).
	- Remaining bytes: compressed bitstream (`compressed_size - 7`).
- **Output:** Allocates `uncompressed_size` buffer and expands codes into it using a 0x100–0xFFF dictionary.
- **Dictionary structure:**
	- Prefix table at `A4+0x2b26` (u16 array).
	- Suffix table at `A4+0x2b2e` (byte array).
- **Control values:**
	- Initial code width = 9 bits, grows to 12 (`LzwIncreaseCodeWidth`).
	- `A4+0x2b1e` is used as an end/stop code (initially `0x1ff`).
	- `A4+0x2b22` is a special code threshold used during decode (initially `0x1fe`).


### LzwReadBits (formerly FUN_0023f592 @ 0x0023f592)
- **Summary:** Bitstream reader; pulls `code_width` bits from `A4+0x3ae4` using a cached 32-bit shift register at `A4+0x3ad8`.

### LzwInitCodebook (formerly FUN_0023f518 @ 0x0023f518)
- **Summary:** Initializes LZW state (code width, masks, and next code).

### LzwIncreaseCodeWidth (formerly FUN_0023f54c @ 0x0023f54c)
- **Summary:** Increases code width (max 12) and updates masks when dictionary grows.

### LzwAllocTables / LzwFreeTables (formerly FUN_0023f4a6 / FUN_0023f4d6)
- **Summary:** Allocates/frees LZW prefix/suffix tables (`0x273a` and `0x139d` bytes respectively).

### LzwReadDecodedBytes (formerly FUN_0023f4fa @ 0x0023f4fa)
- **Summary:** Copies bytes from the current decoded output cursor (`A4+0x2a82`) to a caller buffer and advances the cursor.

### DecodeSmsResource (formerly FUN_0023dc42 @ 0x0023dc42)
- **Summary:** Parses an `SMS` payload from the **already LZW‑decoded** stream, allocates plane buffers, and dispatches to the SMS planar RLE decoder.
- **Marker check:** Reads 4 bytes and compares against `0x534D5300 + (int8)A4-0x3388`, i.e. `"SMS"` plus a variable 4th byte (often `0x00`).
- **Header (0x16 bytes, big‑endian):**
	- `u32 payload_size`
	- `u16 width`
	- `u16 height`
	- `u16 unknownA`
	- `u16 unknownB`
	- `u8 plane_count`
	- `u8 layout_mode`
	- `u16 unknownC`
	- `u32 plane_descriptor_nibbles` (8 nibbles, LSB‑first)
	- `u16 extended_header_flags`
- **Extended header:** If `(extended_header_flags & 0xFF00) != 0`, allocates 0x80 bytes and reads **0x60 + 0x20** bytes into it. Sets `A4+0x2a6a = 1` when present. This is the only palette‑adjacent activity inside this function.
- **Palette hint:** `0x60 + 0x20 = 0x80` bytes, which aligns with **64 colors × 2 bytes** (Amiga 12‑bit RGB/BGR words). No direct palette interpretation occurs here.
- **Plane allocation:** Rounds width to bytes (`bytes_per_row`), computes `bytes_per_row * height`, then allocates up to 8 plane buffers. The nibble table defines which slots are constant (`0x8`/`0xC` = zero, `0xD` = all‑ones) vs. backed by allocated buffers.
- **Dispatch:** Calls `DecodeSmsPlanarRle` with the decoded payload pointer, packed `D0/D1` args, and the layout flag. On success, it rebuilds the 8‑slot plane table and returns 1.
- **Callers:** No other call sites found; this routine is the sole SMS decode entry point currently visible.
- **Callers (note):** No direct xrefs found; likely invoked via an indirect dispatch table. Track the caller by tracing where the SMS marker path is chosen (pre‑LZW wrapper) or by placing a runtime breakpoint at `0x0023dc42`.

### DecodeSmsPlanarRle (formerly FUN_0023f11a @ 0x0023f11a)
- **Summary:** SMS planar RLE expansion into bitplane buffers.
- **Marker:** `0xAE` indicates a run (`value`, `count`), where `count` is stored as a byte and interpreted as `count + 1`.
- **Layout:** Two write orders:
	- If `layout_mode == 0`: column‑major across planes, then columns.
	- Otherwise: column‑major within a plane, then advance to the next plane.
- **Arg packing:** The compiler passes packed values in registers. From the decompile, the low/high words map as:
	- `D0.low = bytes_per_row`, `D0.high = height`
	- `D1.low = layout_mode`, `D1.high = plane_count`
- **Notes:** This function only writes planar data; it does not interpret palettes or convert to chunky pixels.

### Resource decode dispatcher (pre-LZW)
- **Observation:** There is a small wrapper immediately before `LzwDecompressResource` that reads the first 2 bytes from the file into a tiny stack buffer, then calls the string-compare routine (`A4-0x7d46`) against a PC-relative constant and conditionally branches to `LzwDecompressResource` on equality.
- **Implication:** This wrapper likely checks for the `"LZ"` marker and *only then* enters the LZW path. If the compare fails, an alternative path executes (candidate for `SMS` handling).
- **Next validation (UAE):** Set a breakpoint around the compare/branch site (~`0x0023F650–0x0023F65E`) and log:
	- The 2 bytes read from the file.
	- The compare result (branch taken or not).
	- The next function executed when the branch is **not** taken (likely SMS decode).

### StrCmp (formerly FUN_0023f8e8 @ 0x0023f8e8)
- **Summary:** `strcmp`-style byte string compare (returns 0 if equal, -1 if a<b, 1 if a>b). Likely used by the pre-LZW wrapper for the `"LZ"` marker check.

## Game state dispatch

### HandleGameStateMachine (formerly FUN_00221cc8 @ 0x00221cc8)
- **Summary:** Central state-machine dispatcher. Switches on `A4-0x3080` and executes per-state handlers, returning `-1` for most transitions or a status from `param_2`/input polling.
- **Key behaviors by state (partial):**
	- **1:** Calls `RunScreenTransitionEffect` then `RunPhoneCallScene`.
	- **2:** Calls `RunScreenTransitionEffect`, returns `-1`.
	- **3:** UI/transition sequence (`A4-0x7de2`, `A4-0x7ef0`, `A4-0x7f08`), toggles flags and may call `FUN_00221c92` or set `A4-0x1c8`.
	- **4:** Calls `HandleSleepSelection`, `FUN_002223f4`, then conditionally mounts `Desert2:` assets (string pointers `0x2244/0x224d/0x2258` path), otherwise calls `ShowWakeUpNarration`.
	- **6:** Resets flags, runs transition helpers, and may call `FUN_0022803a` + `FUN_00221c92`.
	- **8:** Calls `FUN_00222264` (caller loop includes this state).
	- **9:** Transition + set `A4-0x7ae4` flag.
	- **0xB/0xC/0xE:** Complex transitions and conditional scene logic (multiple flag combinations).
	- **0xF:** Calls `LoadDes4AniResourcesAndMap`, then sets up views and calls `FUN_00227cbc`.
- **Return flow:** After each state handler, if `A4-0x31d4` is set, returns `-1`. Otherwise, if `param_2` requests polling, it calls `HandleMenuInputAndSetState` and loops until `param_2._2_2_` is zero.

### Callers
- **FUN_00220bd2**: Calls `HandleGameStateMachine` within a larger flow (likely a main loop or event handler).
- **FUN_00222264**: Invokes `HandleGameStateMachine` (state `8` path suggests this is a sub-mode loop).
- **FUN_002281fe**: Calls `HandleGameStateMachine` during another higher-level flow.

## Manx/RTL vector calls (GetManxContext)

### ManxLibCall_LVO_3C (formerly FUN_0024046a @ 0x0024046a)
- **Summary:** Indirect library call via base pointer at `A4+0x3af8` (`movea.l (A4+0x3af8),A6; jmp -0x3C(A6)`).
- **Usage:** Called by `GetManxContext` and likely part of a Manx/RTL vector table (paired with other nearby LVO calls such as `FUN_0024045c`).
- **Next validation:** Identify the library base stored at `A4+0x3af8` (e.g., Manx runtime) to name the LVO more specifically.

**Runtime observation (UAE):**
- `A4+0x3af8` points to `0x00121EC`.
- LVO `-0x3C` resolves to `0x00121B0`, which is a jump table entry that points to `0x00F9FBD4`.
- Next step: identify what code lives at `0x00F9FBD4` (likely in a library segment) to name the LVO accurately.
