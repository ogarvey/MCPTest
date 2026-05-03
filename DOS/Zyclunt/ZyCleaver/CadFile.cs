namespace ZyCleaver;

internal sealed class CadFile
{
    public const int FrameRecordSize = 0x5e;
    public const int SequenceEntrySize = 6;

    public CadFile(
        string sourcePath,
        string magic,
        ushort unknown0,
        byte auxiliaryFlag,
        byte[]? auxiliaryBlock,
        ushort unknown1,
        byte[] rawData,
        IReadOnlyList<CadFrameRecord> frames,
        IReadOnlyList<CadSequenceEntry> sequenceEntries,
        IReadOnlyList<CadSequenceStart> sequenceStarts,
        IReadOnlyList<CadImageChunk> imageChunks)
    {
        SourcePath = sourcePath;
        Magic = magic;
        Unknown0 = unknown0;
        AuxiliaryFlag = auxiliaryFlag;
        AuxiliaryBlock = auxiliaryBlock;
        Unknown1 = unknown1;
        RawData = rawData;
        Frames = frames;
        SequenceEntries = sequenceEntries;
        SequenceStarts = sequenceStarts;
        ImageChunks = imageChunks;
    }

    public string SourcePath { get; }

    public string Magic { get; }

    public ushort Unknown0 { get; }

    public byte AuxiliaryFlag { get; }

    public byte[]? AuxiliaryBlock { get; }

    public ushort Unknown1 { get; }

    public byte[] RawData { get; }

    public IReadOnlyList<CadFrameRecord> Frames { get; }

    public IReadOnlyList<CadSequenceEntry> SequenceEntries { get; }

    public IReadOnlyList<CadSequenceStart> SequenceStarts { get; }

    public IReadOnlyList<CadImageChunk> ImageChunks { get; }
}

internal sealed record CadFrameRecord(
    int Index,
    byte[] Bytes,
    ushort CompositeFlag,
    short Part1X,
    short Part1Y,
    short Part2X,
    short Part2Y,
    uint PrimaryDataOffset,
    uint SecondaryDataOffset)
{
    public bool IsComposite => CompositeFlag != 0;
}

internal enum CadSequenceEntryKind
{
    Normal,
    LoopBacktrack,
    Transition
}

internal sealed record CadSequenceEntry(
    int Index,
    int EntryOffset,
    int RawTarget,
    ushort Value,
    CadSequenceEntryKind Kind,
    int? FrameIndex,
    int? BacktrackEntryCount);

internal sealed record CadSequenceStart(int Index, int ByteOffset, int? EntryIndex);

internal sealed record CadImageChunk(int Offset, ushort Width, ushort Height, int LengthGuess, IReadOnlyList<int> ReferencedByFrames);
