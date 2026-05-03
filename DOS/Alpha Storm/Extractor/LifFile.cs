using System.Buffers.Binary;

public sealed class LifFile
{
    private const int HeaderSize = 12;

    private LifFile(int width, int height, IReadOnlyList<LifFrame> frames)
    {
        Width = width;
        Height = height;
        Frames = frames;
    }

    public int Width { get; }

    public int Height { get; }

    public IReadOnlyList<LifFrame> Frames { get; }

    public static LifFile Load(string path)
    {
        return Parse(File.ReadAllBytes(path));
    }

    public static LifFile Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize)
        {
            throw new InvalidDataException("LIF file is too small to contain a header.");
        }

        var width = ReadPositiveInt32(data[..4], "width");
        var height = ReadPositiveInt32(data.Slice(4, 4), "height");
        var frameCount = ReadPositiveInt32(data.Slice(8, 4), "frame count");

        var offsetTableLength = checked(frameCount * 4);
        var offsetTableEnd = HeaderSize + offsetTableLength;
        if (data.Length < offsetTableEnd)
        {
            throw new InvalidDataException("LIF file ends before the frame offset table is complete.");
        }

        var offsets = new int[frameCount];
        for (var index = 0; index < offsets.Length; index++)
        {
            var relativeOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(HeaderSize + index * 4, 4));
            if (relativeOffset > int.MaxValue - HeaderSize)
            {
                throw new InvalidDataException($"Frame {index} offset is too large.");
            }

            var absoluteOffset = HeaderSize + (int)relativeOffset;
            if (absoluteOffset < offsetTableEnd || absoluteOffset >= data.Length)
            {
                throw new InvalidDataException($"Frame {index} offset points outside the image data.");
            }

            if (index > 0 && absoluteOffset <= offsets[index - 1])
            {
                throw new InvalidDataException($"Frame {index} offset is not greater than the previous frame offset.");
            }

            offsets[index] = absoluteOffset;
        }

        var frames = new List<LifFrame>(frameCount);
        for (var index = 0; index < offsets.Length; index++)
        {
            var start = offsets[index];
            var end = index + 1 < offsets.Length ? offsets[index + 1] : data.Length;
            frames.Add(DecodeFrame(index, width, height, start, end, data));
        }

        return new LifFile(width, height, frames);
    }

    private static LifFrame DecodeFrame(int index, int width, int height, int start, int end, ReadOnlySpan<byte> data)
    {
        var pixels = new byte[checked(width * height)];
        var alpha = new byte[pixels.Length];
        var rowStart = start;
        var bounds = PixelBounds.Empty;
        var opaquePixels = 0;

        for (var y = 0; y < height; y++)
        {
            if (rowStart >= end)
            {
                throw new InvalidDataException($"Frame {index} ended before row {y}.");
            }

            var rowLength = data[rowStart];
            if (rowLength == 0)
            {
                throw new InvalidDataException($"Frame {index}, row {y} has a zero row length.");
            }

            var rowEnd = rowStart + rowLength;
            if (rowEnd > end || rowEnd > data.Length)
            {
                throw new InvalidDataException($"Frame {index}, row {y} extends beyond its encoded frame data.");
            }

            var commandOffset = rowStart + 1;
            var x = 0;
            while (commandOffset < rowEnd && x < width)
            {
                var command = unchecked((sbyte)data[commandOffset++]);
                if (command == 0)
                {
                    if (commandOffset >= rowEnd)
                    {
                        throw new InvalidDataException($"Frame {index}, row {y} has a truncated transparent run.");
                    }

                    x = CheckedAdvance(index, y, x, data[commandOffset++], width);
                }
                else if (command > 0)
                {
                    if (commandOffset >= rowEnd)
                    {
                        throw new InvalidDataException($"Frame {index}, row {y} has a truncated solid run.");
                    }

                    var count = command + 1;
                    var value = data[commandOffset++];
                    FillOpaqueRun(pixels, alpha, width, y, x, count, value, ref bounds, ref opaquePixels);
                    x = CheckedAdvance(index, y, x, count, width);
                }
                else
                {
                    var count = -command;
                    if (commandOffset + count > rowEnd)
                    {
                        throw new InvalidDataException($"Frame {index}, row {y} has a truncated literal run.");
                    }

                    for (var runIndex = 0; runIndex < count; runIndex++)
                    {
                        SetOpaquePixel(pixels, alpha, width, x + runIndex, y, data[commandOffset + runIndex], ref bounds, ref opaquePixels);
                    }

                    commandOffset += count;
                    x = CheckedAdvance(index, y, x, count, width);
                }
            }

            if (x != width)
            {
                throw new InvalidDataException($"Frame {index}, row {y} decodes to {x} pixel(s); expected {width}.");
            }

            rowStart = rowEnd;
        }

        return new LifFrame(index, start, rowStart - start, pixels, alpha, opaquePixels, bounds);
    }

    private static int ReadPositiveInt32(ReadOnlySpan<byte> source, string fieldName)
    {
        var value = BinaryPrimitives.ReadUInt32LittleEndian(source);
        if (value == 0 || value > int.MaxValue)
        {
            throw new InvalidDataException($"Invalid LIF {fieldName}: {value}.");
        }

        return (int)value;
    }

    private static int CheckedAdvance(int frameIndex, int y, int x, int count, int width)
    {
        var next = x + count;
        if (next > width)
        {
            throw new InvalidDataException($"Frame {frameIndex}, row {y} run passes the row width.");
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

public sealed record LifFrame(
    int Index,
    int EncodedOffset,
    int EncodedLength,
    byte[] Pixels,
    byte[] Alpha,
    int OpaquePixelCount,
    PixelBounds Bounds);

public readonly record struct PixelBounds(int Left, int Top, int Right, int Bottom)
{
    public static PixelBounds Empty { get; } = new(0, 0, -1, -1);

    public bool IsEmpty => Right < Left || Bottom < Top;

    public PixelBounds Include(int x, int y)
    {
        return IsEmpty
            ? new PixelBounds(x, y, x, y)
            : new PixelBounds(Math.Min(Left, x), Math.Min(Top, y), Math.Max(Right, x), Math.Max(Bottom, y));
    }
}
