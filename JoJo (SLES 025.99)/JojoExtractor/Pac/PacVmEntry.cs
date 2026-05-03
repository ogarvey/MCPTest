namespace JojoExtractor.Pac;

public readonly record struct PacVmEntry(
    int Index,
    ushort Opcode,
    ushort OpcodeClass,
    byte OpcodeLow,
    uint DataLength,
    uint StrideLength,
    uint SectorLength,
    long DataOffset)
{
    public static PacVmEntry FromPacEntry(PacEntry entry)
    {
        ushort opcode = (ushort)(entry.Flags & 0xffff);
        ushort opcodeClass = (ushort)(opcode & 0x0f00);
        byte opcodeLow = (byte)(opcode & 0xff);
        uint strideLength = Align(entry.DataLength, 4);
        uint sectorLength = Align(entry.DataLength, PacFile.SectorSize);

        return new PacVmEntry(
            entry.Index,
            opcode,
            opcodeClass,
            opcodeLow,
            entry.DataLength,
            strideLength,
            sectorLength,
            entry.DataOffset);
    }

    public static string GetClassName(ushort opcodeClass) => opcodeClass switch
    {
        0x0100 => "pointer/RAM table",
        0x0200 => "direct VRAM image",
        0x0400 => "audio/table dispatch",
        0x0800 => "runtime pool",
        _ => "unknown"
    };

    private static uint Align(uint value, int alignment)
    {
        uint mask = (uint)alignment - 1;
        return (value + mask) & ~mask;
    }
}
