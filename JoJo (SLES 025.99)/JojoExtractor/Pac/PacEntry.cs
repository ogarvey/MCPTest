namespace JojoExtractor.Pac;

/// <summary>
/// One entry inside a .PAC container.
/// </summary>
/// <param name="Index">Zero-based position of the entry in the directory.</param>
/// <param name="Flags">First field of the entry header (purpose not yet confirmed).</param>
/// <param name="DataLength">Declared length of the entry payload in bytes.</param>
/// <param name="DataOffset">Absolute byte offset of the payload inside the source PAC.</param>
public readonly record struct PacEntry(int Index, uint Flags, uint DataLength, long DataOffset);
