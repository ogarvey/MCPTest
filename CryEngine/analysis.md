# CryEngine Character Model Parsing Analysis

## CharacterManager::CreateInstance (180886cc0)

This method serves as the entry point for creating character instances. It handles file path normalization and dispatches to specific creation methods based on the file extension.

### Logic Flow
1.  **Path Normalization**:
    *   Allocates a buffer (`pathBuffer`) and copies the input `filePath`.
    *   Replaces all backslashes `\` with forward slashes `/`.
    *   Converts the entire path to lowercase.
2.  **Extension Detection**:
    *   Scans backwards from the end of the string to find the last `.` character.
3.  **Dispatch**:
    *   `.chr`: Calls `CreateSKELInstance` (180886f90).
    *   `.cga`: Calls `CreateCGAInstance` (180884f90).
    *   `.cdf`: Calls `LoadCharacterDefinition` (18088c960).
    *   `.skin`: Checked but appears to fall through or be unused in this specific block.
    *   **Other**: Logs an error "CryAnimation: no valid character file-format".

### Key Functions Identified
*   `CreateSKELInstance` @ `180886f90`
*   `CreateCGAInstance` @ `180884f90`
*   `LoadCharacterDefinition` @ `18088c960`
