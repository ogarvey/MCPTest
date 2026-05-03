using System.Buffers.Binary;

internal static class PmpExtractor
{
    public static ExtractionResult Extract(PmpFile pmp, string outputDirectory, int levelChunkLimit)
    {
        List<SectionFileResult> sectionFiles = new();
        foreach (PmpSection section in pmp.Header.EnumerateSections())
        {
            byte[] sectionBytes = pmp.ReadSection(section);
            string sectionPath = Path.Combine(outputDirectory, $"{section.Name}.bin");
            File.WriteAllBytes(sectionPath, sectionBytes);

            sectionFiles.Add(new SectionFileResult(
                section.Name,
                sectionPath,
                section.FileOffset,
                section.PackedSize,
                section.AlignedByteCount,
                section.IsSectorAligned));
        }

        VramExtractionResult vram = VramExtractor.Extract(pmp, outputDirectory);
        List<LevelDataChunkResult> levelDataChunks = ExtractLevelDataChunks(pmp, outputDirectory, levelChunkLimit);
        SpriteExtractionResult sprites = SpriteFrameExtractor.Extract(pmp, outputDirectory);

        return new ExtractionResult(
            pmp.FilePath,
            pmp.Data.Length,
            pmp.Header,
            sectionFiles,
            vram,
            levelDataChunks,
            sprites);
    }

    private static List<LevelDataChunkResult> ExtractLevelDataChunks(PmpFile pmp, string outputDirectory, int levelChunkLimit)
    {
        List<LevelDataChunkResult> results = new();
        byte[] levelData = pmp.ReadSection(pmp.Header.LevelDataSection);
        int offset = 0;

        for (int index = 0; index < levelChunkLimit; index++)
        {
            if (!EmbeddedChunk.TryRead(levelData, offset, out EmbeddedChunk? maybeChunk, allowEmpty: true))
            {
                break;
            }

            EmbeddedChunk chunk = maybeChunk!;

            byte[] decoded = chunk.IsCompressed
                ? PmpLzDecompressor.Decompress(levelData.AsSpan(chunk.PayloadOffset, chunk.StoredByteCount), chunk.OutputSize)
                : levelData.AsSpan(chunk.PayloadOffset, chunk.StoredByteCount).ToArray();

            string outputPath = Path.Combine(outputDirectory, $"level-data.chunk-{index:D2}.bin");
            File.WriteAllBytes(outputPath, decoded);

            results.Add(new LevelDataChunkResult(
                index,
                outputPath,
                chunk.DescriptorOffset,
                chunk.UnknownWord0,
                chunk.OutputSize,
                chunk.StoredByteCount,
                chunk.IsCompressed));

            offset = chunk.NextDescriptorOffset;
        }

        return results;
    }
}

internal sealed record ExtractionResult(
    string SourceFile,
    int SourceFileLength,
    PmpHeader Header,
    IReadOnlyList<SectionFileResult> SectionFiles,
    VramExtractionResult Vram,
    IReadOnlyList<LevelDataChunkResult> LevelDataChunks,
    SpriteExtractionResult Sprites);

internal sealed record SectionFileResult(
    string Name,
    string OutputPath,
    int FileOffset,
    int PackedSize,
    int AlignedByteCount,
    bool IsSectorAligned);

internal sealed record LevelDataChunkResult(
    int Index,
    string OutputPath,
    int DescriptorOffset,
    int UnknownWord0,
    int OutputSize,
    int StoredByteCount,
    bool IsCompressed);

internal sealed class EmbeddedChunk
{
    private EmbeddedChunk()
    {
    }

    public int DescriptorOffset { get; private init; }

    public int UnknownWord0 { get; private init; }

    public int OutputSize { get; private init; }

    public int StoredByteCount { get; private init; }

    public int PayloadOffset { get; private init; }

    public bool IsCompressed { get; private init; }

    public int TotalByteCount => 12 + StoredByteCount;

    public int NextDescriptorOffset => PayloadOffset + StoredByteCount;

    public static bool TryRead(ReadOnlySpan<byte> data, int descriptorOffset, out EmbeddedChunk? chunk, bool allowEmpty = false)
    {
        chunk = null;
        if (descriptorOffset < 0 || descriptorOffset + 12 > data.Length)
        {
            return false;
        }

        int unknownWord0 = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(descriptorOffset, 4));
        int outputSize = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(descriptorOffset + 4, 4));
        int compressedSize = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(descriptorOffset + 8, 4));
        if (outputSize < 0 || compressedSize < 0)
        {
            return false;
        }

        int storedByteCount = compressedSize == 0 ? outputSize : compressedSize;
        if (storedByteCount < 0 || descriptorOffset + 12 + storedByteCount > data.Length)
        {
            return false;
        }

        if (!allowEmpty && (outputSize == 0 || storedByteCount == 0))
        {
            return false;
        }

        chunk = new EmbeddedChunk
        {
            DescriptorOffset = descriptorOffset,
            UnknownWord0 = unknownWord0,
            OutputSize = outputSize,
            StoredByteCount = storedByteCount,
            PayloadOffset = descriptorOffset + 12,
            IsCompressed = compressedSize != 0,
        };

        return true;
    }
}
