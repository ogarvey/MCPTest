# Veil of Darkness - Game Logic Analysis

## Game State Management

### SwitchGameMode (`FUN_1053_1c52`)
**Address:** `1053:1c52`
**Purpose:** Handles transitions between different game modes (Gameplay, Menu, Intro/Cutscene).

**Parameters:**
- `param_1`: Mode ID
    - `2`: **Gameplay Mode**
    - `3`: **Menu/Inventory Mode** (likely)
    - `4`: **Intro/Cutscene Mode**

**Logic:**
- **Mode 2 (Gameplay):**
    - Calls `InitSystemWithResourceFile("RESOURCE.DAT"?)` (`FUN_17c2_1a5c`).
    - Loads and locks specific resources (IDs `DAT_1f79_27e0 + 4`, `DAT_1f79_27de + 2`).
    - Calls `InitGameplayScreen` (`FUN_1053_1894`).
    - Calls `LoadGameplayResources` (`FUN_1053_1313`).
    - Calls `SetupGameplayUI` (`FUN_1053_6732`).
    - Calls `SetGameMode` (`FUN_1053_1c0d`).

- **Mode 3 (Menu?):**
    - Calls `InitSystemWithResourceFile(...)`.
    - Calls `EnterMenuMode` (`FUN_1053_76d6`).

- **Mode 4 (Intro):**
    - Calls `InitSystemWithResourceFile(...)`.
    - Calls `PlayIntroAnimation` (`FUN_1053_6502`).
    - Calls `SetGameMode`.

## Initialization Functions

### InitSystemWithResourceFile (`FUN_17c2_1a5c`)
**Address:** `17c2:1a5c`
**Purpose:** Initializes the game system, including music, graphics, mouse, and the resource system.
**Parameters:**
- `param_1`: Pointer to the resource filename (e.g., "RESOURCE.DAT").
**Key Operations:**
- Initializes music driver.
- Checks for graphics driver.
- Reads `GRAPH.INI`.
- Initializes Mouse.
- Calls `InitResourceSystem` with the provided filename.

### InitResourceSystem (`17c2:122f`)
**Address:** `17c2:122f`
**Purpose:** Opens the resource file and initializes the resource table.

## Gameplay Functions

### InitGameplayScreen (`FUN_1053_1894`)
**Address:** `1053:1894`
**Purpose:** Sets up the main gameplay view.
- Loops to initialize UI elements.
- Calls `GetResource` for UI assets.
- Calls drawing functions.

### LoadGameplayResources (`FUN_1053_1313`)
**Address:** `1053:1313`
**Purpose:** Preloads a range of common resources used in gameplay.

### SetupGameplayUI (`FUN_1053_6732`)
**Address:** `1053:6732`
**Purpose:** Defines and initializes various UI regions (viewport, message log, status bar).

### EnterMenuMode (`FUN_1053_76d6`)
**Address:** `1053:76d6`
**Purpose:** Handles the transition to the menu/inventory screen.

### PlayIntroAnimation (`FUN_1053_6502`)
**Address:** `1053:6502`
**Purpose:** Plays the intro sequence or cutscenes.
- Uses double buffering (`DAT_1f79_0a97`).
- Loops through frames.

## Resource Management

### LockResource (`FUN_17c2_1965`)
**Address:** `17c2:1965`
**Purpose:** Marks a resource as "locked" or "persistent" in the resource table (sets bit 0x10).
**Usage:** Called immediately after `GetResource` for critical assets.

### GetResource (`17c2:1471`)
**Address:** `17c2:1471`
**Purpose:** Loads a resource if not present, then dispatches to a handler based on type.
**Types:**
- **Type 1:** Compressed Image (LZ77 variant?).
- **Type 2:** Uncompressed/Indexed Image (Calls `INT 60h`).
- **Type 4:** Graphics Format (Calls `DrawResource_Type4`).
