using JojoExtractor.Pac;

namespace JojoExtractor.Psx;

/// <summary>
/// Disabled until PAC entry semantics are backed by executable code.
/// The current Ghidra-backed facts are narrower than what a PAC-wide
/// auto-export pipeline would need: specific loader paths such as
/// FUN_8001da50 prove a 0x8000-byte stripe upload path and a CLUT upload
/// path, but we do not yet have code that maps PAC directory flags or
/// adjacent entries to those loader modes.
/// </summary>
public static class PacAutoExporter
{
    public sealed record FrameOutput(
        int PixelEntryIndex, int FrameIndex,
        int ClutEntryIndex, int BankIndex,
        string PngPath);

    public static IReadOnlyList<FrameOutput> Export(string pacPath, string outDir)
    {
        _ = pacPath;
        _ = outDir;
        throw new NotSupportedException(
            "PAC auto export is disabled: no Ghidra-verified code currently maps PAC flags or adjacent entries to image/CLUT semantics. Use 'extract', 'image', or 'palettes' only for manually verified cases.");
    }
}
