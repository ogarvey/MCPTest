using System.Buffers.Binary;
using System.Text;

namespace ZyCleaver;

internal static class CadParser
{
    public static CadFile Parse(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: false);

        var magicBytes = ReadExact(reader, 4);
        var magic = Encoding.ASCII.GetString(magicBytes);

        if (!magicBytes.AsSpan(0, 3).SequenceEqual("CAD"u8))
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} does not start with CAD magic.");
        }

        var unknown0 = reader.ReadUInt16();
        var auxiliaryFlag = reader.ReadByte();
        var auxiliaryBlock = auxiliaryFlag == 0 ? null : ReadExact(reader, 0x400);
        var unknown1 = reader.ReadUInt16();
        var rawDataSize = reader.ReadInt32();

        if (rawDataSize < 0)
        {
            throw new InvalidDataException($"Negative raw data size in {Path.GetFileName(path)}.");
        }

        var rawData = ReadExact(reader, rawDataSize);
        var frameCount = reader.ReadUInt16();
        var frames = new List<CadFrameRecord>(frameCount);

        for (var index = 0; index < frameCount; index++)
        {
            var frameBytes = ReadExact(reader, CadFile.FrameRecordSize);
            frames.Add(ParseFrameRecord(index, frameBytes, rawData.Length));
        }

        var sequenceEntryCount = reader.ReadUInt16();
        var sequenceEntries = new List<CadSequenceEntry>(sequenceEntryCount);

        for (var index = 0; index < sequenceEntryCount; index++)
        {
            var rawTarget = reader.ReadInt32();
            var value = reader.ReadUInt16();
            sequenceEntries.Add(ParseSequenceEntry(index, rawTarget, value, frameCount));
        }

        var sequenceStartCount = reader.ReadUInt16();
        var sequenceStarts = new List<CadSequenceStart>(sequenceStartCount);
        var sequenceTableByteLength = sequenceEntries.Count * CadFile.SequenceEntrySize;

        for (var index = 0; index < sequenceStartCount; index++)
        {
            var byteOffset = reader.ReadInt32();

            if (sequenceTableByteLength == 0)
            {
                throw new InvalidDataException("Sequence starts exist even though the sequence table is empty.");
            }

            if (byteOffset < 0 || byteOffset >= sequenceTableByteLength)
            {
                throw new InvalidDataException($"Sequence start {index} points outside the sequence table: 0x{byteOffset:X8}");
            }

            int? entryIndex = byteOffset % CadFile.SequenceEntrySize == 0
                ? byteOffset / CadFile.SequenceEntrySize
                : null;

            sequenceStarts.Add(new CadSequenceStart(index, byteOffset, entryIndex));
        }

        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException(
                $"{Path.GetFileName(path)} has {stream.Length - stream.Position} unread bytes after the CAD tables.");
        }

        return new CadFile(
            Path.GetFullPath(path),
            magic,
            unknown0,
            auxiliaryFlag,
            auxiliaryBlock,
            unknown1,
            rawData,
            frames,
            sequenceEntries,
            sequenceStarts,
            BuildImageChunks(rawData, frames));
    }

    private static IReadOnlyList<CadImageChunk> BuildImageChunks(byte[] rawData, IReadOnlyList<CadFrameRecord> frames)
    {
        var references = new Dictionary<int, List<int>>();

        foreach (var frame in frames)
        {
            AddReference((int)frame.PrimaryDataOffset, frame.Index);

            if (frame.IsComposite)
            {
                AddReference((int)frame.SecondaryDataOffset, frame.Index);
            }
        }

        var sortedOffsets = references.Keys.OrderBy(offset => offset).ToArray();
        var chunks = new List<CadImageChunk>(sortedOffsets.Length);

        for (var index = 0; index < sortedOffsets.Length; index++)
        {
            var offset = sortedOffsets[index];

            if (offset < 0 || offset + 4 > rawData.Length)
            {
                throw new InvalidDataException($"Image chunk offset 0x{offset:X8} points outside the raw data blob.");
            }

            var nextOffset = index + 1 < sortedOffsets.Length ? sortedOffsets[index + 1] : rawData.Length;

            if (nextOffset <= offset)
            {
                throw new InvalidDataException($"Image chunk offsets are not strictly increasing around 0x{offset:X8}.");
            }

            var width = BinaryPrimitives.ReadUInt16LittleEndian(rawData.AsSpan(offset, sizeof(ushort)));
            var height = BinaryPrimitives.ReadUInt16LittleEndian(rawData.AsSpan(offset + sizeof(ushort), sizeof(ushort)));
            var lengthGuess = nextOffset - offset;
            var frameIndices = references[offset].Distinct().OrderBy(frameIndex => frameIndex).ToArray();

            chunks.Add(new CadImageChunk(offset, width, height, lengthGuess, frameIndices));
        }

        return chunks;

        void AddReference(int offset, int frameIndex)
        {
            if (!references.TryGetValue(offset, out var frameIndices))
            {
                frameIndices = new List<int>();
                references.Add(offset, frameIndices);
            }

            frameIndices.Add(frameIndex);
        }
    }

    private static CadFrameRecord ParseFrameRecord(int index, byte[] bytes, int rawDataLength)
    {
        var compositeFlag = ReadUInt16(bytes, 0x4c);
        var part1X = ReadInt16(bytes, 0x4e);
        var part1Y = ReadInt16(bytes, 0x50);
        var part2X = ReadInt16(bytes, 0x52);
        var part2Y = ReadInt16(bytes, 0x54);
        var primaryDataOffset = ReadUInt32(bytes, 0x56);
        var secondaryDataOffset = ReadUInt32(bytes, 0x5a);

        if (primaryDataOffset > rawDataLength)
        {
            throw new InvalidDataException($"Frame {index} points past the raw data blob with primary offset 0x{primaryDataOffset:X8}.");
        }

        if (compositeFlag != 0 && secondaryDataOffset > rawDataLength)
        {
            throw new InvalidDataException($"Composite frame {index} points past the raw data blob with secondary offset 0x{secondaryDataOffset:X8}.");
        }

        return new CadFrameRecord(
            index,
            bytes,
            compositeFlag,
            part1X,
            part1Y,
            part2X,
            part2Y,
            primaryDataOffset,
            secondaryDataOffset);
    }

    private static CadSequenceEntry ParseSequenceEntry(int index, int rawTarget, ushort value, int frameCount)
    {
        var kind = rawTarget switch
        {
            -2 => CadSequenceEntryKind.LoopBacktrack,
            -1 => CadSequenceEntryKind.Transition,
            _ => CadSequenceEntryKind.Normal
        };

        int? frameIndex = null;
        int? backtrackEntryCount = null;

        if (kind == CadSequenceEntryKind.Normal)
        {
            if (rawTarget < 0)
            {
                throw new InvalidDataException($"Sequence entry {index} has a negative frame offset: {rawTarget}.");
            }

            if (rawTarget % CadFile.FrameRecordSize != 0)
            {
                throw new InvalidDataException($"Sequence entry {index} is not aligned to 0x{CadFile.FrameRecordSize:X} bytes: 0x{rawTarget:X8}.");
            }

            frameIndex = rawTarget / CadFile.FrameRecordSize;

            if (frameIndex >= frameCount)
            {
                throw new InvalidDataException($"Sequence entry {index} points past the frame table: frame {frameIndex}.");
            }
        }
        else if (kind == CadSequenceEntryKind.LoopBacktrack && value % CadFile.SequenceEntrySize == 0)
        {
            backtrackEntryCount = value / CadFile.SequenceEntrySize;
        }

        return new CadSequenceEntry(
            index,
            index * CadFile.SequenceEntrySize,
            rawTarget,
            value,
            kind,
            frameIndex,
            backtrackEntryCount);
    }

    private static byte[] ReadExact(BinaryReader reader, int length)
    {
        var bytes = reader.ReadBytes(length);

        if (bytes.Length != length)
        {
            throw new EndOfStreamException($"Expected {length} bytes, got {bytes.Length}.");
        }

        return bytes;
    }

    private static short ReadInt16(byte[] bytes, int offset)
    {
        return BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(offset, sizeof(short)));
    }

    private static ushort ReadUInt16(byte[] bytes, int offset)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, sizeof(ushort)));
    }

    private static uint ReadUInt32(byte[] bytes, int offset)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, sizeof(uint)));
    }
}
