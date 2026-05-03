# Laser Lords - `PlayerMovementHandler?` Analysis

## Context
- **Game**: Laser Lords
- **Platform**: Philips CD-i
- **Binary open in Ghidra**: `cdi_tribes.rtf_0_0_0.bin`
- **Function under investigation**: `PlayerMovementHandler?`
- **Address**: `0x0000B98C`
- **Analysis date**: April 6, 2026

---

## Executive Summary
`PlayerMovementHandler?` is **not just a movement routine**.

It is effectively the game's **main per-frame player update handler**, responsible for:
- reading player input,
- dispatching walking / climbing / crouching / jump-like movement,
- applying gravity and fall damage,
- handling conveyors, ladders, ropes, and doors,
- performing room/screen transitions,
- dispatching directional attacks based on equipped weapon,
- triggering several level-specific scripted hazards,
- and even running a special healing-pool check.

### Better proposed name
A more accurate name would be something like:
- `HandlePlayerInputMovementAndInteractions`
- `UpdatePlayerFromInput`
- `PlayerControllerTick`

`PlayerMovementHandler?` is **close**, but too narrow for what the routine actually does.

---

## High-Level Control Flow
At a high level the function does this each update:

1. **Read current input** via `FUN_0001016c(...)`.
2. If a special modal movement state is active (`DAT_800028d0 != 0`), route control to `FUN_0000de76()` and suppress most normal logic.
3. Resolve any **deferred movement / landing / jump / leap** state machines already in progress.
4. Apply **gravity / fall damage / falling animation**.
5. Apply **environment-driven movement** (conveyors, ropes, ladders).
6. If the player presses the interaction button (`0xCF`), process **door use and room transitions**.
7. If the input is a direction or directional-action code, dispatch to the appropriate **move / climb / attack handler**.
8. Finish with `ArgoHealingPoolHandler()`.

---

## Important Called Functions and What They Do

### `FUN_0001016c(...)` - input decoder
This is the front-end input translator.

It reads raw input via `thunk_FUN_0001b9d4(...)`, then returns abstract codes such as:
- `0x4B` / `0x4D` / `0x48` / `0x50` → behave like **left / right / up / down**,
- `0x47` / `0x49` / `0x4F` / `0x51` → behave like **diagonal directions**,
- `0xCF` → appears to be **confirm / interact / use door**,
- `0x73`, `0x74`, `0x75`, `0x76`, `0x77`, `0x83`, `0x84`, `0x85` → directional **attack/action variants**.

This makes the top-level structure of `PlayerMovementHandler?` much easier to understand.

---

### `PlayerMoveUpCheck`, `PlayerMoveDownCheck`, `PlayerMoveLeftCheck`, `PlayerMoveRightCheck`
These names are **good assumptions**.

They do exactly what the names suggest:
- move the player by one unit in the requested direction,
- detect when the player crosses the room boundary,
- call the correct "enter adjacent screen" helper,
- return `1` if movement is blocked because there is no connected screen.

The visible playfield limits appear to be approximately:
- `X = 0 .. 0x27` (`0..39`)
- `Y = 0 .. 0x12` (`0..18`)

So the room grid is roughly **40 x 19** in collision-space units.

---

### `GetTilesSurroundingPlayer(x, y)`
This assumption also makes sense.

It caches nearby tile ids into globals for:
- feet,
- below feet,
- left/right of feet,
- head,
- left/right of head,
- legs,
- waist.

This is the central collision-sampling helper used by almost every movement branch.

---

### `SetFlagsBasedOnActiveTile(tile_id)`
This is one of the most important helpers in the whole chain.

It classifies the active tile into flags such as:
- solid / supportable ground,
- rope,
- ladder,
- door,
- conveyor variants,
- blocking/hazardous tile types.

The existing assumption for this function is **correct**.

It also shows that several currently named globals are valid, but some are only **partially** understood (see the review table below).

---

### `PlayerAnimControl?`
This name is also basically correct.

It decides whether the player should:
- display the normal standing/walking sprite,
- show a rope-climb frame,
- or show a ladder-climb frame.

It is animation-state management, not gameplay logic by itself.

---

### `FUN_0000fa42(x, y)`
This one is **not** a general collision function.

It checks a very specific condition involving:
- `NPC_X_POS`,
- `MAIN_FLOOR_Y_POS`,
- and a few other state globals.

So it looks more like a **specific NPC / actor body-block test** than generic world collision.

If this function was assumed to mean something like `IsTileBlocked()` or `CheckMapCollision()`, that assumption does **not** fit the implementation.

---

## Detailed Breakdown of `PlayerMovementHandler?`

### 1. Special modal movement state: `DAT_800028d0`
If `DAT_800028d0 != 0`, the function immediately calls `FUN_0000de76()`.

`FUN_0000de76()`:
- maintains latched movement directions in `DAT_80004c06` and `DAT_80004c0a`,
- checks surrounding tiles before moving,
- performs movement independently of the normal handler,
- and usually suppresses the rest of the player update.

### Interpretation
This is **not just a simple “busy” flag**.
It looks more like a **special movement mode / auto-move / constrained-control state**.

> This is one of the main places where the current assumptions are still incomplete.

---

### 2. Deferred drop / landing handling
There are two early state-machine branches:

#### `DAT_800028e8`
This causes the player to be moved **down** one or two times, then refreshes surrounding tiles and animation.

This looks like a **deferred vertical correction / forced drop resolution**.

#### `DAT_800028ec`
This alternates between animation frames `0x0E` and `0x11`, toggles `IS_EVEN_ANIM_FRAME`, and counts down to zero.

This is very likely a **landing animation / post-impact recovery timer**.

That assumption makes sense.

---

### 3. Short jump / hop state machine: `DAT_80002914`
This branch is one of the clearest pieces of evidence in the function.

When active, the routine runs a **5-step arc** using:
- `FUN_0000c82e()` for the rightward version,
- `FUN_0000c7da()` for the leftward version.

Those helpers perform this pattern:
- steps 1–2: move horizontally **and upward**,
- step 3: move **horizontally**,
- steps 4–5: move horizontally **and downward**.

### Interpretation
This is very likely a **short jump / hop / vault arc**.

The supporting state variables appear to be:
- `DAT_80002914` → jump/hop active
- `DAT_8000290c` → moving/jumping right
- `DAT_80002910` → moving/jumping left
- `DAT_80002908` → jump phase counter

This assumption is **strongly supported** by the called function behavior.

---

### 4. Longer leap / clamber state machine: `DAT_80002904`
When `DAT_80002904` is active, the function routes into:
- `FUN_0000d7e2()` if `DAT_80002900 != 0`
- `FUN_0000db38()` if `DAT_800028fc != 0`

These are longer, multi-stage movement sequences that:
- move the player in a large sideways arc,
- update animation frame-by-frame,
- test for head/feet obstruction,
- end with a landing animation and `FastblastCheck()`.

### Interpretation
This appears to be a **larger leap / clamber / special traversal arc**.

The exact gameplay verb is still a bit uncertain, but it is clearly **not ordinary walking**.

Likely meanings:
- `DAT_80002904` → large traversal action active
- `DAT_80002900` → left variant
- `DAT_800028fc` → right variant
- `DAT_800028f8` → phase counter

---

### 5. Gravity and fall damage
The block around `DAT_8000293c` is very informative.

What it does:
- if the player is unsupported (`Grounded__ == 0`) and not in a restricted state, it increments `DAT_8000293c`,
- calls `PlayerMoveDownCheck()`,
- updates falling animation frames,
- and when the player lands, if `DAT_8000293c > 4`, it applies damage.

### Damage formula
```c
SubtractHealth((DAT_8000293c >> 2) * 10, 0, 1);
```
with a clamp:
```c
if (DAT_8000293c > 0x28) DAT_8000293c = 0x28;
```

### Interpretation
- `DAT_8000293c` = **fall-duration / fall-distance counter**
- `DAT_80002938` = **fall animation frame index**

This part of the function is internally consistent and makes good sense.

---

### 6. Conveyors, ropes, and ladders
The movement system is strongly tile-driven.

`SetFlagsBasedOnActiveTile()` reveals dedicated flags for:
- vertical conveyors (`DAT_80002978`, `DAT_8000297c`, `ConveyorTile_`, `ConveyorTile__`),
- horizontal conveyors (`HorizConveyorTile_`, `HorizConveyorTile__`, `HorizConveyorTile___`),
- rope tiles (`RopeTile_`),
- ladder tiles (`DAT_80002998`, `DAT_8000299c`, `LadderTile__`, `LadderTile___`).

The handler also uses `DAT_800028e4` as a frame toggle so some conveyor effects only apply **every other update**.

### Good assumption
The conveyor / ladder / rope naming is mostly consistent with the actual code.

### Less-good assumption
`Grounded_` and `Grounded__` are **not interchangeable**.
They behave differently and should not be treated as the same concept.

---

### 7. Basic direction handlers
The routine dispatches directly to movement helpers based on decoded input:

| Input code | Effective behavior |
|---|---|
| `0x4D` | move right (`FUN_0000ccb2`) |
| `0x4B` | move left (`FUN_0000ce16`) |
| `0x48` | move up / climb / jump-related (`FUN_0000cf7a`) |
| `0x50` | move down / crouch / descend (`FUN_0000d17a`) |
| `0x47` / `0x49` | upward diagonal / jump-like initiation |
| `0x4F` / `0x51` | downward diagonal movement |
| `0xCF` | interact / door use |

The right/left handlers also:
- turn the player if currently facing the opposite direction,
- test blocking tiles around head and feet,
- check the NPC/body-block routine (`FUN_0000fa42`),
- and let gravity pull the player down if the next tile is unsupported.

---

### 8. Door interaction and room transitions
The `0xCF` branch does much more than a simple movement check.

If the player is standing on a `DoorTile_`, the function:
- reads the destination screen from `(&DAT_80002160)[CURRENT_SCREEN_ID * 8 + DAT_8000298c]`,
- performs several hard-coded special-case checks,
- changes `CURRENT_SCREEN_ID`,
- reloads tile data,
- searches for the entry spawn point,
- sets `PLAYER_POS_X` / `PLAYER_POS_Y`,
- flips facing direction,
- redraws the player.

### Hard-coded special cases found in this routine
- **Level 3, screen `0x18`**: “Lasers fire from the gate.” + damage.
- **Level 1, screen `0x2E`**: “The door is locked.”
- **Level 0, screen `0x1C`**: “Rays shoot from the crypt.” with immunity/death handling.

This is another strong sign that the function is **broader than “movement”**.

---

### 9. Weapon / attack dispatch
After the movement and interaction checks, the function routes directional attack inputs depending on:
- `PLAYER_FACING_L_R`,
- `PLAYER_CROUCHED`,
- `EQUIPPED_WEAPON_TYPE`.

It calls different handlers for different weapons and directions, for example:
- `FUN_0000eb22()`
- `FUN_0000ec3e()`
- `FUN_0000ebb0()`
- `FUN_0000e928()`
- `FUN_0000e9b6()`
- `FUN_0000ea3c()`
- `FUN_0000ea94()`
- `FUN_0000eccc()`

The exact weapon mapping is not fully resolved here, but the pattern is obvious:
this function is also the **top-level combat input dispatcher**.

---

### 10. Hidden level-specific post-processing
At the end of the function it always calls:
- `ArgoHealingPoolHandler()`

That handler heals the player when:
- `LEVEL_ID == 1`
- `CURRENT_SCREEN_ID == 0x33`
- `PLAYER_POS_X` is inside a specific range.

Again, this is global per-frame player logic, not just motion.

---

## Assumption Review

| Existing assumption | Verdict | Notes |
|---|---|---|
| `PlayerMovementHandler?` is a movement routine | **Partly true, but too narrow** | It is the master player update / input / interaction / combat routine. |
| `PlayerMoveUpCheck` / `DownCheck` / `LeftCheck` / `RightCheck` are correctly named | **Yes** | These are simple coordinate-step + screen-transition helpers. |
| `GetTilesSurroundingPlayer` is correctly named | **Yes** | It caches collision samples around the player. |
| `SetFlagsBasedOnActiveTile` is correctly named | **Yes** | This is the key tile classification function. |
| `PlayerAnimControl?` is basically an animation controller | **Yes** | It picks standing / rope / ladder presentation. |
| `Grounded__` means “the player is supported / standing on something” | **Mostly yes** | It is the broad supportability flag used for fall logic. |
| `Grounded_` means the same thing as `Grounded__` | **No** | It is a different solidity/collision classification and should not be merged mentally with `Grounded__`. |
| `FUN_0000fa42` is generic map collision | **No** | It looks like a specific NPC/body-block check. |
| `DAT_80002914` is just a random flag | **No** | It is clearly a short jump/hop state machine. |
| `DAT_80002904` is just another generic motion flag | **No** | It is a larger multi-frame traversal sequence. |
| `DAT_800028d0` is simply “busy animating” | **Too vague / uncertain** | It gates into a separate movement mode handled by `FUN_0000de76()`. |

---

## Things That Still Do **Not** Fully Make Sense
These are the main unresolved pieces after inspecting the function and its callees:

1. **Exact meaning of `DAT_800028d0`**
   - It is definitely a special movement/control mode.
   - But it does **not** look like a simple “busy” or “stunned” flag.
   - It may represent an auto-move, sliding, or some room-specific movement mode.

2. **Exact gameplay distinction between the two traversal state machines**
   - `DAT_80002914` is clearly a short hop/jump arc.
   - `DAT_80002904` is clearly a larger traversal arc.
   - The exact player-facing design distinction (“jump”, “leap”, “mantle”, “vault”, etc.) would benefit from observing it in-game.

3. **Unclassified tile flags**
   - `DAT_80002954`, `DAT_80002958`, and `DAT_8000295c` are still not fully identified from this function alone.
   - They are likely special tile classes, but not enough context is present here to name them confidently.

4. **Current function signature in Ghidra is not trustworthy**
   - Ghidra shows `undefined PlayerMovementHandler?(void)`,
   - but the decompiler repeatedly shows a `param_1` in register `D0`.
   - This may just be calling-convention noise from the stack-check wrapper, but the current signature is probably not final.

---

## Suggested Renames / Notes for Future Work
If you want to clean up the decompilation further, these would be reasonable next-step renames:

| Current name | Suggested meaning |
|---|---|
| `PlayerMovementHandler?` | `HandlePlayerInputMovementAndInteractions` |
| `DAT_80002914` | `PLAYER_SHORT_JUMP_ACTIVE` |
| `DAT_80002910` | `SHORT_JUMP_LEFT` |
| `DAT_8000290c` | `SHORT_JUMP_RIGHT` |
| `DAT_80002908` | `SHORT_JUMP_PHASE` |
| `DAT_80002904` | `PLAYER_LONG_TRAVERSAL_ACTIVE` |
| `DAT_80002900` | `LONG_TRAVERSAL_LEFT` |
| `DAT_800028fc` | `LONG_TRAVERSAL_RIGHT` |
| `DAT_800028f8` | `LONG_TRAVERSAL_PHASE` |
| `DAT_8000293c` | `FALL_COUNTER` |
| `DAT_80002938` | `FALL_ANIM_PHASE` |
| `DAT_800028e4` | `CONVEYOR_FRAME_TOGGLE` |
| `FUN_0000fa42` | `IsBlockedByNPCOrActor` |

---

## Final Conclusion
Most of the **existing high-level assumptions do make sense**:
- the tile helpers are named well,
- the directional move-check helpers are correct,
- the ladder/rope/conveyor interpretation is mostly sound.

The main corrections are:
- `PlayerMovementHandler?` is **too narrowly named**,
- `Grounded_` and `Grounded__` should **not** be treated as the same flag,
- `FUN_0000fa42` is **not** generic collision,
- and `DAT_800028d0` remains the biggest unresolved state variable.

Overall, this routine is best understood as the game's **player controller tick** rather than a pure movement function.
