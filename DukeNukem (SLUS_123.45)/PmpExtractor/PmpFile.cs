using System.Buffers.Binary;

internal sealed class PmpFile
{
    public const int HeaderSize = 0xA8;

    private PmpFile(string filePath, byte[] data, PmpHeader header)
    {
        FilePath = filePath;
        Data = data;
        Header = header;
    }

    public string FilePath { get; }

    public byte[] Data { get; }

    public PmpHeader Header { get; }

    public static PmpFile Load(string filePath)
    {
        byte[] data = File.ReadAllBytes(filePath);
        if (data.Length < HeaderSize)
        {
            throw new InvalidDataException($"PMP is too small to contain a {HeaderSize:X} byte header.");
        }

        PmpHeader header = PmpHeader.Parse(data.AsSpan(0, HeaderSize));
        return new PmpFile(filePath, data, header);
    }

    public byte[] ReadSection(PmpSection section)
    {
        ValidateSection(section);
        return Data.AsSpan(section.FileOffset, section.PackedSize).ToArray();
    }

    public bool IsSectionInBounds(PmpSection section)
    {
        if (section.FileOffset < 0 || section.PackedSize < 0)
        {
            return false;
        }

        long endOffset = (long)section.FileOffset + section.PackedSize;
        return endOffset <= Data.Length;
    }

    private void ValidateSection(PmpSection section)
    {
        if (!IsSectionInBounds(section))
        {
            throw new InvalidDataException(
                $"Section '{section.Name}' is out of bounds: offset=0x{section.FileOffset:X}, size=0x{section.PackedSize:X}, fileLength=0x{Data.Length:X}.");
        }
    }
}

internal sealed record PmpSection(string Name, int FileOffset, int PackedSize)
{
    public int AlignedByteCount => (PackedSize + 0x7ff) & ~0x7ff;

    public bool IsSectorAligned => (FileOffset & 0x7ff) == 0;

    public int SectorOffset => FileOffset >> 11;
}

internal sealed class PmpHeader
{
    private PmpHeader()
    {
    }

    public uint Magic { get; private init; }

    public int Revision { get; private init; }

    public IReadOnlyList<uint> RawDwords { get; private init; } = Array.Empty<uint>();

    public int UnknownWord08 { get; private init; }

    public int UnknownWord0C { get; private init; }

    public PmpSection VramSection { get; private init; } = null!;

    public PmpSection SpeechBanksSection { get; private init; } = null!;

    public PmpSection LevelDataSection { get; private init; } = null!;

    public PmpSection PackedGfxSection { get; private init; } = null!;

    public PmpSection SpeechBufferSection { get; private init; } = null!;

    public int MaybeLevelDataUnpackedSize { get; private init; }

    public int MaybeSpawnX { get; private init; }

    public int MaybeSpawnY { get; private init; }

    public int MaybeSpawnZ { get; private init; }

    public short MaybeSpawnHeading { get; private init; }

    public short MaybeSpawnSector { get; private init; }

    public short MaybeVramRectCount { get; private init; }

    public short MaybeLookupEntryCount { get; private init; }

    public static PmpHeader Parse(ReadOnlySpan<byte> header)
    {
        if (header.Length != PmpFile.HeaderSize)
        {
            throw new ArgumentException($"Expected a {PmpFile.HeaderSize:X} byte header.", nameof(header));
        }

        uint[] rawDwords = new uint[header.Length / sizeof(uint)];
        for (int index = 0; index < rawDwords.Length; index++)
        {
            rawDwords[index] = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(index * sizeof(uint), sizeof(uint)));
        }

        return new PmpHeader
        {
            Magic = ReadUInt32(header, 0x00),
            Revision = ReadInt32(header, 0x04),
            UnknownWord08 = ReadInt32(header, 0x08),
            UnknownWord0C = ReadInt32(header, 0x0C),
            VramSection = new PmpSection("vram", ReadInt32(header, 0x10), ReadInt32(header, 0x14)),
            SpeechBanksSection = new PmpSection("speech-banks", ReadInt32(header, 0x24), ReadInt32(header, 0x28)),
            LevelDataSection = new PmpSection("level-data", ReadInt32(header, 0x38), ReadInt32(header, 0x3C)),
            PackedGfxSection = new PmpSection("packed-gfx", ReadInt32(header, 0x4C), ReadInt32(header, 0x50)),
            SpeechBufferSection = new PmpSection("speech-buffer", ReadInt32(header, 0x60), ReadInt32(header, 0x64)),
            MaybeLevelDataUnpackedSize = ReadInt32(header, 0x34),
            MaybeSpawnX = ReadInt32(header, 0x6C),
            MaybeSpawnY = ReadInt32(header, 0x70),
            MaybeSpawnZ = ReadInt32(header, 0x74),
            MaybeSpawnHeading = ReadInt16(header, 0x78),
            MaybeSpawnSector = ReadInt16(header, 0x7A),
            MaybeVramRectCount = ReadInt16(header, 0x7C),
            MaybeLookupEntryCount = ReadInt16(header, 0x7E),
            RawDwords = rawDwords,
        };
    }

    public IReadOnlyList<PmpSection> EnumerateSections()
    {
        return new[]
        {
            VramSection,
            SpeechBanksSection,
            LevelDataSection,
            PackedGfxSection,
            SpeechBufferSection,
        };
    }

    private static short ReadInt16(ReadOnlySpan<byte> data, int offset)
    {
        return BinaryPrimitives.ReadInt16LittleEndian(data.Slice(offset, sizeof(short)));
    }

    private static int ReadInt32(ReadOnlySpan<byte> data, int offset)
    {
        return BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, sizeof(int)));
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, sizeof(uint)));
    }
}
