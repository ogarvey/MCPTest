# Animaniacs - Reverse Engineering Notes

## Session Info
- Date: 2026-02-24
- Target Binary: `animaniacs.exe`
- Ghidra Project: `Win`
- Active Instance Port: `8192`

## Current Focus
- Address under review: `0x00408f90`
- Related lead from previous tracing: `FUN_0041b8e0` (now renamed)

## Findings Log

### 2026-02-24 - Session Start
- Created investigation folder and notes file.
- Connected to Ghidra instance with Animaniacs executable.

### 2026-02-24 - Address Resolution (`0x00408f90`)
- `0x00408f90` is **not** currently defined as a standalone function entry in this fresh analysis state.
- Byte pattern at `0x00408f90` begins with function epilogue bytes (`5F 5E 5D 5B C2 04 00`), indicating tail/transition code.
- Closest concrete function entry after this region is `0x00408fb0`.
- Nearby large routine ending around this area is `FUN_00408780`, which configures many behavior parameters and string fields.

### 2026-02-24 - Behavioral Findings
- `FUN_00408fb0` (renamed to `UpdateRangedAttackState`) appears to be a ranged/projectile attack update step:
	- decrements a cooldown/timer field (`this + 0x94`)
	- conditionally calls projectile spawn helper when timer expires and mode flags permit
	- triggers setup/effect routines and transitions actor state (`this + 0x4a`)
- `FUN_00409340` (renamed to `TrySpawnRangedProjectile`) handles in-range projectile creation and initial velocity assignment.
- Caller `FUN_004096d0` (renamed to `UpdateRangedCombatAI`) computes range/heading constraints and invokes `UpdateRangedAttackState` when conditions match.
- Lead `FUN_0041b8e0` resolved to cache/resource cleanup logic and renamed to `ReleaseAssetCacheEntryByName`.

### 2026-02-24 - Notes on Graphics Path
- Current functions in this address cluster look like combat/projectile behavior and object lifecycle control, not direct decompression/conversion.
- `ReleaseAssetCacheEntryByName` likely participates in asset lifecycle (lookup/unlink/free), but not raw decode itself.

## Rename Decisions
- `FUN_00408fb0` -> `UpdateRangedAttackState`
	- Signature: `void UpdateRangedAttackState(void * this, int ownerUnitId)`
- `FUN_00409340` -> `TrySpawnRangedProjectile`
	- Signature: `void TrySpawnRangedProjectile(void * this, int ownerUnitId)`
- `FUN_004096d0` -> `UpdateRangedCombatAI`
	- Signature: `void UpdateRangedCombatAI(void * this, int ownerUnitId)`
- `FUN_0041b8e0` -> `ReleaseAssetCacheEntryByName`
	- Signature: `int ReleaseAssetCacheEntryByName(void * this, char * assetName)`
- `0x00408f90`
	- No function rename applied (not recognized as standalone function entry in this project state).

## Open Questions
- Which function actually performs bit/byte-level graphic decompression in `animaniacs.exe`?
- Is the decompressor reached from projectile/effect spawn (`UpdateRangedAttackState` path), or from a separate global asset loader?
- Should we pivot next to callees near file/memory payload handling (e.g., routines that touch `CMemFile` buffers and parser/decode loops)?
