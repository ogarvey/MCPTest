using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace FamousFive2;

public sealed class BrgFile
{
    public required string SourcePath { get; init; }
    public required string Magic { get; init; }
    public required uint HeaderValue1 { get; init; }
    public required uint HeaderValue2 { get; init; }
    public required uint HeaderValue3 { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required ushort Subtype { get; init; }
    public required IReadOnlyList<uint> FrameOffsets { get; init; }
    public required byte[] FrameData { get; init; }
    public required int FrameCount { get; init; }
    public ushort[]? Palette565 { get; init; }
    public Type3PageSet? Type3Pages { get; init; }
    public ushort Type3FramesPerPage { get; init; }
    public int Type3OutputWidth => Width * 2;
    public int Type3OutputHeight => Height * 2;
}

public sealed class Type3PageSet
{
    public required IReadOnlyList<ushort[]> Pages { get; init; }
}

public static class BrgDecoder
{
    public static BrgFile Load(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

        string magic = new string(reader.ReadChars(2));
        if (magic != "BR")
        {
            throw new InvalidDataException($"{path} does not start with BR magic.");
        }

        uint headerValue1 = reader.ReadUInt32();
        uint headerValue2 = reader.ReadUInt32();
        uint headerValue3 = reader.ReadUInt32();
        int width = checked((int)reader.ReadUInt32());
        int height = checked((int)reader.ReadUInt32());
        ushort subtype = reader.ReadUInt16();

        return subtype switch
        {
            1 or 2 => LoadSubtype12(path, magic, headerValue1, headerValue2, headerValue3, width, height, subtype, reader),
            3 => LoadSubtype3(path, magic, headerValue1, headerValue2, headerValue3, width, height, subtype, reader),
            _ => throw new NotSupportedException($"Unsupported BRG subtype {subtype}.")
        };
    }

    private static BrgFile LoadSubtype12(
        string path,
        string magic,
        uint headerValue1,
        uint headerValue2,
        uint headerValue3,
        int width,
        int height,
        ushort subtype,
        BinaryReader reader)
    {
        byte[] paletteBytes = reader.ReadBytes(0x200);
        if (paletteBytes.Length != 0x200)
        {
            throw new EndOfStreamException("BRG palette is truncated.");
        }

        ushort[] palette = MemoryMarshal.Cast<byte, ushort>(paletteBytes).ToArray();
        uint frameDataSize = reader.ReadUInt32();
        int frameCount = checked((int)reader.ReadUInt32());
        uint[] frameOffsets = ReadFrameOffsets(reader, frameCount);
        byte[] frameData = reader.ReadBytes(checked((int)frameDataSize));

        return new BrgFile
        {
            SourcePath = path,
            Magic = magic,
            HeaderValue1 = headerValue1,
            HeaderValue2 = headerValue2,
            HeaderValue3 = headerValue3,
            Width = width,
            Height = height,
            Subtype = subtype,
            FrameOffsets = frameOffsets,
            FrameData = frameData,
            FrameCount = frameCount,
            Palette565 = palette
        };
    }

    private static BrgFile LoadSubtype3(
        string path,
        string magic,
        uint headerValue1,
        uint headerValue2,
        uint headerValue3,
        int width,
        int height,
        ushort subtype,
        BinaryReader reader)
    {
        int pageCount = checked((int)reader.ReadUInt32());
        int frameCount = checked((int)reader.ReadUInt32());
        ushort framesPerPage = reader.ReadUInt16();
        ushort reserved = reader.ReadUInt16();
        _ = reserved;

        int rawPageBytes = checked(pageCount * 0x800);
        byte[] rawPages = reader.ReadBytes(rawPageBytes);
        if (rawPages.Length != rawPageBytes)
        {
            throw new EndOfStreamException("BRG type 3 block pages are truncated.");
        }

        uint[] frameOffsets = ReadFrameOffsets(reader, frameCount);
        int frameDataSize = frameCount == 0 ? 0 : checked((int)frameOffsets[^1]);
        byte[] frameData = reader.ReadBytes(frameDataSize);

        var pages = new List<ushort[]>(pageCount);
        for (int pageIndex = 0; pageIndex < pageCount; pageIndex++)
        {
            ushort[] page = DecodeType3Page(rawPages.AsSpan(pageIndex * 0x800, 0x800));
            pages.Add(page);
        }

        return new BrgFile
        {
            SourcePath = path,
            Magic = magic,
            HeaderValue1 = headerValue1,
            HeaderValue2 = headerValue2,
            HeaderValue3 = headerValue3,
            Width = width,
            Height = height,
            Subtype = subtype,
            FrameOffsets = frameOffsets,
            FrameData = frameData,
            FrameCount = frameCount,
            Type3FramesPerPage = framesPerPage,
            Type3Pages = new Type3PageSet { Pages = pages }
        };
    }

    public static Image<Rgba32> DecodeFrame(BrgFile brg, int frameIndex)
    {
        return brg.Subtype switch
        {
            1 or 2 => DecodeSubtype12Frame(brg, frameIndex),
            3 => DecodeSubtype3Frame(brg, frameIndex),
            _ => throw new NotSupportedException($"Unsupported BRG subtype {brg.Subtype}.")
        };
    }

    private static Image<Rgba32> DecodeSubtype12Frame(BrgFile brg, int frameIndex)
    {
        ushort[] palette = brg.Palette565 ?? throw new InvalidOperationException("Subtype 1/2 requires a palette.");
        ReadOnlySpan<byte> frame = GetFrameBytes(brg, frameIndex);

        int width = brg.Width;
        int height = brg.Height;
        var decodedPixels = new Rgba32[width * height];

        byte transparentIndex = frame.Length >= 2 && frame[1] == 0xFF ? frame[0] : (byte)0xFF;
        int pixelIndex = 0;
        int offset = 0;

        while (offset + 4 <= frame.Length && pixelIndex < decodedPixels.Length)
        {
            uint token = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(offset, 4));
            byte b0 = (byte)(token & 0xFF);
            byte b1 = (byte)((token >> 8) & 0xFF);
            byte b2 = (byte)((token >> 16) & 0xFF);
            byte b3 = (byte)((token >> 24) & 0xFF);

            if (b1 == 0xFF)
            {
                int runLength = (int)(token >> 16);
                int repeatedPixelCount = runLength * 4;
                for (int i = 0; i < repeatedPixelCount && pixelIndex < decodedPixels.Length; i++)
                {
                    decodedPixels[pixelIndex++] = PaletteIndexToRgba(palette, b0, transparentIndex);
                }
            }
            else
            {
                decodedPixels[pixelIndex++] = PaletteIndexToRgba(palette, b0, transparentIndex);
                if (pixelIndex < decodedPixels.Length) decodedPixels[pixelIndex++] = PaletteIndexToRgba(palette, b1, transparentIndex);
                if (pixelIndex < decodedPixels.Length) decodedPixels[pixelIndex++] = PaletteIndexToRgba(palette, b2, transparentIndex);
                if (pixelIndex < decodedPixels.Length) decodedPixels[pixelIndex++] = PaletteIndexToRgba(palette, b3, transparentIndex);
            }

            offset += 4;
        }

        return CreateImage(width, height, decodedPixels);
    }

    private static Image<Rgba32> DecodeSubtype3Frame(BrgFile brg, int frameIndex)
    {
        if (brg.Type3Pages is null || brg.Type3Pages.Pages.Count == 0)
        {
            throw new InvalidOperationException("Subtype 3 requires decoded block pages.");
        }

        ReadOnlySpan<byte> frame = GetFrameBytes(brg, frameIndex);
        int framesPerPage = Math.Max(1, (int)brg.Type3FramesPerPage);
        int pageIndex = Math.Min(brg.Type3Pages.Pages.Count - 1, frameIndex / framesPerPage);
        ushort[] page = brg.Type3Pages.Pages[pageIndex];

        int outputWidth = brg.Type3OutputWidth;
        int outputHeight = brg.Type3OutputHeight;

        var decodedPixels = new Rgba32[outputWidth * outputHeight];

        if (frame.IsEmpty)
        {
            return CreateImage(outputWidth, outputHeight, decodedPixels);
        }

        byte escapeMarker = frame[0];
        byte specialIndex = frame.Length >= 3 && frame[2] == escapeMarker ? frame[1] : (byte)0xFF;
        int blockX = 0;
        int blockY = 0;
        int blockWidth = brg.Width;
        int blockHeight = brg.Height;
        int offset = 1;

        while (offset + 4 <= frame.Length && blockY < blockHeight)
        {
            uint token = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(offset, 4));
            byte b0 = (byte)(token & 0xFF);
            byte b1 = (byte)((token >> 8) & 0xFF);
            byte b2 = (byte)((token >> 16) & 0xFF);
            byte b3 = (byte)((token >> 24) & 0xFF);

            if (b1 == escapeMarker)
            {
                int runLength = (int)(token >> 16);
                byte blockIndex = b0 == specialIndex ? specialIndex : b0;
                for (int i = 0; i < runLength && blockY < blockHeight; i++)
                {
                    for (int j = 0; j < 4 && blockY < blockHeight; j++)
                    {
                        WriteType3Block(decodedPixels, outputWidth, outputHeight, blockX, blockY, page, blockIndex, specialIndex);
                        AdvanceType3Cursor(ref blockX, ref blockY, blockWidth);
                    }
                }
            }
            else
            {
                byte[] blockIndices = [b0, b1, b2, b3];
                foreach (byte blockIndex in blockIndices)
                {
                    if (blockY >= blockHeight)
                    {
                        break;
                    }

                    WriteType3Block(decodedPixels, outputWidth, outputHeight, blockX, blockY, page, blockIndex, specialIndex);
                    AdvanceType3Cursor(ref blockX, ref blockY, blockWidth);
                }
            }

            offset += 4;
        }

        return CreateImage(outputWidth, outputHeight, decodedPixels);
    }

    private static void AdvanceType3Cursor(ref int blockX, ref int blockY, int blockWidth)
    {
        blockX++;
        if (blockX >= blockWidth)
        {
            blockX = 0;
            blockY++;
        }
    }

    private static void WriteType3Block(
        Rgba32[] pixels,
        int outputWidth,
        int outputHeight,
        int blockX,
        int blockY,
        ushort[] page,
        byte blockIndex,
        byte specialIndex)
    {
        int entryOffset = blockIndex * 4;
        if (entryOffset + 3 >= page.Length)
        {
            return;
        }

        int pixelX = blockX * 2;
        int pixelY = blockY * 2;
        if (pixelX + 1 >= outputWidth || pixelY + 1 >= outputHeight)
        {
            return;
        }

        bool forceTransparent = blockIndex == specialIndex;

        pixels[pixelY * outputWidth + pixelX] = Type3ColorToRgba(page[entryOffset], forceTransparent);
        pixels[pixelY * outputWidth + pixelX + 1] = Type3ColorToRgba(page[entryOffset + 1], forceTransparent);
        pixels[(pixelY + 1) * outputWidth + pixelX] = Type3ColorToRgba(page[entryOffset + 2], forceTransparent);
        pixels[(pixelY + 1) * outputWidth + pixelX + 1] = Type3ColorToRgba(page[entryOffset + 3], forceTransparent);
    }

    private static Image<Rgba32> CreateImage(int width, int height, Rgba32[] decodedPixels)
    {
        var image = new Image<Rgba32>(width, height);
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < height; y++)
            {
                decodedPixels.AsSpan(y * width, width).CopyTo(accessor.GetRowSpan(y));
            }
        });

        return image;
    }

    private static ushort[] DecodeType3Page(ReadOnlySpan<byte> rawPage)
    {
        ushort[] values = MemoryMarshal.Cast<byte, ushort>(rawPage.ToArray()).ToArray();

        for (int index = 0; index < values.Length; index++)
        {
            ushort raw = values[index];
            values[index] = (raw & 0x8000) == 0 ? (ushort)0 : (ushort)(raw & 0x7FFF);
        }

        for (int index = 0; index < values.Length; index += 4)
        {
            (values[index + 1], values[index + 2]) = (values[index + 2], values[index + 1]);
        }

        return values;
    }

    private static uint[] ReadFrameOffsets(BinaryReader reader, int frameCount)
    {
        var offsets = new uint[frameCount];
        for (int i = 0; i < frameCount; i++)
        {
            offsets[i] = reader.ReadUInt32();
        }

        return offsets;
    }

    private static ReadOnlySpan<byte> GetFrameBytes(BrgFile brg, int frameIndex)
    {
        int start = frameIndex == 0 ? 0 : checked((int)brg.FrameOffsets[frameIndex - 1]);
        int end = checked((int)brg.FrameOffsets[frameIndex]);

        if (start < 0 || end < start || end > brg.FrameData.Length)
        {
            throw new InvalidDataException($"Frame {frameIndex} has invalid offset bounds {start}..{end}.");
        }

        return brg.FrameData.AsSpan(start, end - start);
    }

    private static Rgba32 PaletteIndexToRgba(ushort[] palette, byte paletteIndex, byte transparentIndex)
    {
        bool transparent = paletteIndex == transparentIndex;
        ushort color = palette[paletteIndex];
        return Rgb565ToRgba(color, transparent);
    }

    private static Rgba32 Type3ColorToRgba(ushort color, bool forceTransparent)
    {
        if (forceTransparent || color == 0)
        {
            return new Rgba32(0, 0, 0, 0);
        }

        return Rgb555ToRgba(color, false);
    }

    private static Rgba32 Rgb555ToRgba(ushort color, bool transparent)
    {
        if (transparent)
        {
            return new Rgba32(0, 0, 0, 0);
        }

        int red = (color >> 10) & 0x1F;
        int green = (color >> 5) & 0x1F;
        int blue = color & 0x1F;

        byte r = (byte)((red << 3) | (red >> 2));
        byte g = (byte)((green << 3) | (green >> 2));
        byte b = (byte)((blue << 3) | (blue >> 2));
        return new Rgba32(r, g, b, 255);
    }

    private static Rgba32 Rgb565ToRgba(ushort color, bool transparent)
    {
        if (transparent)
        {
            return new Rgba32(0, 0, 0, 0);
        }

        int red = (color >> 11) & 0x1F;
        int green = (color >> 5) & 0x3F;
        int blue = color & 0x1F;

        byte r = (byte)((red << 3) | (red >> 2));
        byte g = (byte)((green << 2) | (green >> 4));
        byte b = (byte)((blue << 3) | (blue >> 2));
        return new Rgba32(r, g, b, 255);
    }
}

public static class BrgExporter
{
    public static Image<Rgba32> ExportFrame(BrgFile brg, int frameIndex)
    {
        return BrgDecoder.DecodeFrame(brg, frameIndex);
    }
}
