using System.Buffers.Binary;

public sealed class BifFile
{
    private const int DirectoryEntrySize = 8;

    private BifFile(IReadOnlyList<BifImage> images)
    {
        Images = images;
    }

    public IReadOnlyList<BifImage> Images { get; }

    public static BifFile Load(string path)
    {
        return Parse(File.ReadAllBytes(path));
    }

    public static BifFile Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < DirectoryEntrySize)
        {
            throw new InvalidDataException("BIF file is too small to contain a directory.");
        }

        var firstOffset = BinaryPrimitives.ReadUInt32LittleEndian(data[..4]);
        if (firstOffset == 0 || firstOffset > data.Length || firstOffset % DirectoryEntrySize != 0)
        {
            throw new InvalidDataException($"Invalid BIF first entry offset: 0x{firstOffset:X}.");
        }

        var imageCount = checked((int)firstOffset / DirectoryEntrySize);
        var images = new List<BifImage>(imageCount);

        for (var index = 0; index < imageCount; index++)
        {
            var entryOffset = index * DirectoryEntrySize;
            var start = ReadOffset(data.Slice(entryOffset, 4), index, firstOffset, data.Length);
            var width = ReadPositiveUInt16(data.Slice(entryOffset + 4, 2), "width", index);
            var height = ReadPositiveUInt16(data.Slice(entryOffset + 6, 2), "height", index);
            var end = index + 1 < imageCount
                ? ReadOffset(data.Slice(entryOffset + DirectoryEntrySize, 4), index + 1, firstOffset, data.Length)
                : data.Length;

            if (end <= start)
            {
                throw new InvalidDataException($"BIF entry {index} ends before it starts.");
            }

            images.Add(DecodeImage(index, width, height, start, end, data));
        }

        return new BifFile(images);
    }

    private static BifImage DecodeImage(int index, int width, int height, int start, int end, ReadOnlySpan<byte> data)
    {
        var pixels = new byte[checked(width * height)];
        var alpha = new byte[pixels.Length];
        var sourceOffset = start;
        var bounds = PixelBounds.Empty;
        var opaquePixels = 0;

        for (var y = 0; y < height; y++)
        {
            var x = 0;

            while (true)
            {
                if (sourceOffset >= end)
                {
                    throw new InvalidDataException($"BIF entry {index}, row {y} is truncated.");
                }

                var command = data[sourceOffset++];
                if (command == 0)
                {
                    if (sourceOffset >= end)
                    {
                        throw new InvalidDataException($"BIF entry {index}, row {y} has a truncated transparent run.");
                    }

                    var transparentCount = data[sourceOffset++];
                    if (transparentCount == 0)
                    {
                        break;
                    }

                    x = CheckedAdvance(index, y, x, transparentCount, width);
                    continue;
                }

                if (command < 0x80)
                {
                    if (sourceOffset >= end)
                    {
                        throw new InvalidDataException($"BIF entry {index}, row {y} has a truncated solid run.");
                    }

                    var count = command + 1;
                    var value = data[sourceOffset++];
                    FillOpaqueRun(pixels, alpha, width, y, x, count, value, ref bounds, ref opaquePixels);
                    x = CheckedAdvance(index, y, x, count, width);
                    continue;
                }

                var literalCount = DecodeLiteralCount(command, data, ref sourceOffset, end, index, y);
                if (sourceOffset + literalCount > end)
                {
                    throw new InvalidDataException($"BIF entry {index}, row {y} has a truncated literal run.");
                }

                for (var runIndex = 0; runIndex < literalCount; runIndex++)
                {
                    SetOpaquePixel(pixels, alpha, width, x + runIndex, y, data[sourceOffset + runIndex], ref bounds, ref opaquePixels);
                }

                sourceOffset += literalCount;
                x = CheckedAdvance(index, y, x, literalCount, width);
            }

            if (x != width)
            {
                throw new InvalidDataException($"BIF entry {index}, row {y} decodes to {x} pixel(s); expected {width}.");
            }

            if ((sourceOffset & 1) != 0)
            {
                sourceOffset++;
                if (sourceOffset > end)
                {
                    throw new InvalidDataException($"BIF entry {index}, row {y} alignment passes the end of the entry.");
                }
            }
        }

        return new BifImage(index, width, height, start, sourceOffset - start, pixels, alpha, opaquePixels, bounds);
    }

    private static int ReadOffset(ReadOnlySpan<byte> source, int index, uint directoryLength, int dataLength)
    {
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(source);
        if (offset < directoryLength || offset >= dataLength)
        {
            throw new InvalidDataException($"BIF entry {index} offset 0x{offset:X} points outside the encoded data.");
        }

        return (int)offset;
    }

    private static int ReadPositiveUInt16(ReadOnlySpan<byte> source, string fieldName, int index)
    {
        var value = BinaryPrimitives.ReadUInt16LittleEndian(source);
        if (value == 0)
        {
            throw new InvalidDataException($"BIF entry {index} has an invalid {fieldName} of zero.");
        }

        return value;
    }

    private static int DecodeLiteralCount(byte command, ReadOnlySpan<byte> data, ref int sourceOffset, int end, int index, int y)
    {
        var countByte = command;
        if (countByte == 0x80)
        {
            if (sourceOffset >= end)
            {
                throw new InvalidDataException($"BIF entry {index}, row {y} has a truncated escaped literal count.");
            }

            countByte = data[sourceOffset++];
        }

        var count = (256 - countByte) & 0xff;
        if (count == 0)
        {
            throw new InvalidDataException($"BIF entry {index}, row {y} has a zero-length literal run.");
        }

        return count;
    }

    private static int CheckedAdvance(int imageIndex, int y, int x, int count, int width)
    {
        var next = x + count;
        if (next > width)
        {
            throw new InvalidDataException($"BIF entry {imageIndex}, row {y} run passes the row width.");
        }

        return next;
    }

    private static void FillOpaqueRun(byte[] pixels, byte[] alpha, int width, int y, int x, int count, byte value,
        ref PixelBounds bounds, ref int opaquePixels)
    {
        for (var runIndex = 0; runIndex < count; runIndex++)
        {
            SetOpaquePixel(pixels, alpha, width, x + runIndex, y, value, ref bounds, ref opaquePixels);
        }
    }

    private static void SetOpaquePixel(byte[] pixels, byte[] alpha, int width, int x, int y, byte value,
        ref PixelBounds bounds, ref int opaquePixels)
    {
        var pixelOffset = y * width + x;
        pixels[pixelOffset] = value;
        alpha[pixelOffset] = 255;
        opaquePixels++;
        bounds = bounds.Include(x, y);
    }
}

public sealed record BifImage(
    int Index,
    int Width,
    int Height,
    int EncodedOffset,
    int EncodedLength,
    byte[] Pixels,
    byte[] Alpha,
    int OpaquePixelCount,
    PixelBounds Bounds);
