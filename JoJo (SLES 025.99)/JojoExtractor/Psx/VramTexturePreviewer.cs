using JojoExtractor.Pac;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace JojoExtractor.Psx;

public readonly record struct VramUploadLayout(
    int TableIndex,
    int X,
    int Y,
    int WordWidth,
    int ChunkHeight,
    int DeltaX,
    int DeltaY,
    int StepByteThreshold,
    int StepX,
    int StepY,
    int StepWordWidth,
    int StepChunkHeight,
    int StepDeltaX,
    int StepDeltaY);

public static class VramTexturePreviewer
{
    private static readonly VramUploadLayout[] Layouts =
    {
        new(0, 0x03e0, 0x0100, 0x0020, 0x0020, 0, 0x20, 0x8000, 0, 0, 0, 0, 0, 0),
        new(1, 0x0180, 0x0000, 0x0040, 0x0010, 0, 0x10, 0x8000, 0x40, -0x100, 0, 0, 0, 0),
        new(2, 0x0180, 0x0100, 0x0100, 0x0004, 0, 0x04, 0, 0x40, -0x100, 0, 0, 0, 0),
        new(3, 0x0280, 0x0100, 0x0100, 0x0004, 0, 0x04, 0, 0x40, -0x100, 0, 0, 0, 0),
        new(4, 0x0180, 0x0100, 0x0004, 0x0010, 0, 0x10, 0x0800, 0x04, -0x100, 0, 0, 0, 0),
        new(5, 0x0280, 0x0100, 0x0004, 0x0010, 0, 0x10, 0x0800, 0x04, -0x100, 0, 0, 0, 0),
        new(6, 0x0380, 0x0100, 0x0004, 0x0010, 0, 0x10, 0x0800, 0x04, -0x100, 0, 0, 0, 0),
        new(7, 0x03c0, 0x0000, 0x0004, 0x0010, 0, 0x10, 0x0800, 0x04, -0x100, 0, 0, 0, 0),
        new(8, 0x0380, 0x0000, 0x0004, 0x0010, 0, 0x10, 0x0800, 0x04, -0x100, 0, 0, 0, 0),
        new(9, 0x0240, 0x0000, 0x0004, 0x0010, 0, 0x10, 0x0800, 0x04, -0x100, 0, 0, 0, 0),
        new(10, 0x0180, 0x0100, 0x0004, 0x0010, 0, 0x10, 0x0800, 0x04, -0x100, 0, 0, 0, 0),
        new(11, 0x0240, 0x0000, 0x0040, 0x0010, 0, 0x10, 0x8000, 0x40, -0x100, 0, 0, 0, 0),
        new(12, 0x0340, 0x0000, 0x0004, 0x0010, 0, 0x10, 0x0800, 0x04, -0x100, 0, 0, 0, 0),
        new(13, 0x0300, 0x0100, 0x0004, 0x0010, 0, 0x10, 0x0800, 0x04, -0x100, 0, 0, 0, 0),
        new(14, 0x01c0, 0x0100, 0x0004, 0x0010, 0, 0x10, 0x0800, 0x04, -0x100, 0, 0, 0, 0),
        new(15, 0x0198, 0x0000, 0x0040, 0x0010, 0, 0x10, 0x8000, 0x40, -0x100, 0, 0, 0, 0),
        new(16, 0x0300, 0x0000, 0x0004, 0x0010, 0, 0x10, 0x0800, 0x04, -0x100, 0, 0, 0, 0),
        new(17, 0x0340, 0x0100, 0x0004, 0x0010, 0, 0x10, 0x0800, 0x04, -0x100, 0, 0, 0, 0),
        new(18, 0x0180, 0x0100, 0x0040, 0x0010, 0, 0x10, 0x8000, 0x40, -0x100, 0, 0, 0, 0),
        new(19, 0x0200, 0x0000, 0x0004, 0x0010, 0, 0x10, 0x0800, 0x04, -0x100, 0, 0, 0, 0),
        new(20, 0x0180, 0x0100, 0x0040, 0x0010, 0, 0x10, 0x8000, 0x40, -0x100, 0, 0, 0, 0),
        new(21, 0x01c0, 0x0100, 0x0040, 0x0010, 0, 0x10, 0x8000, 0x40, -0x100, 0, 0, 0, 0),
        new(22, 0x0300, 0x0100, 0x0004, 0x0010, 0, 0x10, 0x0800, 0x04, -0x100, 0, 0, 0, 0),
        new(23, 0x01c8, 0x0000, 0x0040, 0x0010, 0, 0x10, 0x8000, 0x40, -0x100, 0, 0, 0, 0),
        new(24, 0x0240, 0x0100, 0x0004, 0x0010, 0, 0x10, 0x0800, 0x04, -0x100, 0, 0, 0, 0),
        new(25, 0x02c0, 0x0000, 0x0004, 0x0010, 0, 0x10, 0x0800, 0x04, -0x100, 0, 0, 0, 0),
    };

    public static VramUploadLayout GetLayout(int tableIndex)
    {
        if (tableIndex < 0 || tableIndex >= Layouts.Length)
            throw new ArgumentOutOfRangeException(nameof(tableIndex), $"No embedded DAT_8005991c layout for index {tableIndex}.");

        return Layouts[tableIndex];
    }

    public static Image<Rgba32> Render(PacFile pac, PacEntry entry, int bitsPerPixel, int tableOffset = 0)
    {
        if (bitsPerPixel is not (4 or 8))
            throw new ArgumentOutOfRangeException(nameof(bitsPerPixel), "bitsPerPixel must be 4 or 8.");

        ushort opcode = (ushort)(entry.Flags & 0xffff);
        if ((opcode & 0x0f00) != 0x0200)
            throw new InvalidDataException($"Entry {entry.Index} opcode 0x{opcode:X4} is not a 0x0200 direct VRAM image entry.");

        var layout = GetLayout((opcode & 0xff) + tableOffset);
        ReadOnlySpan<byte> data = pac.GetEntryData(entry);
        return bitsPerPixel == 4
            ? RenderPlaced4Bpp(data, layout)
            : RenderPlaced8Bpp(data, layout);
    }

    public static (int Width, int Height, VramUploadLayout Layout) GetPlacedSize(PacFile pac, PacEntry entry, int bitsPerPixel, int tableOffset = 0)
    {
        if (bitsPerPixel is not (4 or 8))
            throw new ArgumentOutOfRangeException(nameof(bitsPerPixel), "bitsPerPixel must be 4 or 8.");

        ushort opcode = (ushort)(entry.Flags & 0xffff);
        if ((opcode & 0x0f00) != 0x0200)
            throw new InvalidDataException($"Entry {entry.Index} opcode 0x{opcode:X4} is not a 0x0200 direct VRAM image entry.");

        var layout = GetLayout((opcode & 0xff) + tableOffset);
        int pixelsPerWord = bitsPerPixel == 4 ? 4 : 2;
        GetTouchedBounds(layout, (int)entry.DataLength, pixelsPerWord, out int minX, out int minY, out int maxX, out int maxY);
        return (maxX - minX, maxY - minY, layout);
    }

    public static Image<Rgba32> RenderLinear(PacFile pac, PacEntry entry, int bitsPerPixel, int tableOffset = 0)
    {
        if (bitsPerPixel is not (4 or 8))
            throw new ArgumentOutOfRangeException(nameof(bitsPerPixel), "bitsPerPixel must be 4 or 8.");

        ushort opcode = (ushort)(entry.Flags & 0xffff);
        if ((opcode & 0x0f00) != 0x0200)
            throw new InvalidDataException($"Entry {entry.Index} opcode 0x{opcode:X4} is not a 0x0200 direct VRAM image entry.");

        var layout = GetLayout((opcode & 0xff) + tableOffset);
        ReadOnlySpan<byte> data = pac.GetEntryData(entry);
        return bitsPerPixel == 4
            ? RenderLinear4Bpp(data, layout.WordWidth)
            : RenderLinear8Bpp(data, layout.WordWidth);
    }

    private static Image<Rgba32> RenderLinear4Bpp(ReadOnlySpan<byte> data, int wordWidth)
    {
        int bytesPerRow = wordWidth * 2;
        if (data.Length % bytesPerRow != 0)
            throw new InvalidDataException($"Payload length 0x{data.Length:X} is not a multiple of VRAM row bytes 0x{bytesPerRow:X}.");

        int width = wordWidth * 4;
        int height = data.Length / bytesPerRow;
        var image = new Image<Rgba32>(width, height);

        for (int y = 0; y < height; y++)
        {
            int rowStart = y * bytesPerRow;
            for (int byteIndex = 0; byteIndex < bytesPerRow; byteIndex++)
            {
                byte value = data[rowStart + byteIndex];
                int x = byteIndex * 2;
                image[x, y] = Gray4(value & 0x0f);
                image[x + 1, y] = Gray4(value >> 4);
            }
        }

        return image;
    }

    private static Image<Rgba32> RenderLinear8Bpp(ReadOnlySpan<byte> data, int wordWidth)
    {
        int width = wordWidth * 2;
        if (data.Length % width != 0)
            throw new InvalidDataException($"Payload length 0x{data.Length:X} is not a multiple of 8bpp row bytes 0x{width:X}.");

        int height = data.Length / width;
        var image = new Image<Rgba32>(width, height);

        for (int y = 0; y < height; y++)
        {
            int rowStart = y * width;
            for (int x = 0; x < width; x++)
            {
                byte value = data[rowStart + x];
                image[x, y] = new Rgba32(value, value, value, 255);
            }
        }

        return image;
    }

    private static Rgba32 Gray4(int value)
    {
        byte gray = (byte)(value * 17);
        return new Rgba32(gray, gray, gray, 255);
    }

    private static Image<Rgba32> RenderPlaced4Bpp(ReadOnlySpan<byte> data, VramUploadLayout layout)
    {
        using var canvas = new Image<Rgba32>(1024 * 4, 512);
        DrawPlaced(data, layout, canvas, 4);
        return CropTouched(canvas, layout, data.Length, 4);
    }

    private static Image<Rgba32> RenderPlaced8Bpp(ReadOnlySpan<byte> data, VramUploadLayout layout)
    {
        using var canvas = new Image<Rgba32>(1024 * 2, 512);
        DrawPlaced(data, layout, canvas, 2);
        return CropTouched(canvas, layout, data.Length, 2);
    }

    private static void DrawPlaced(
        ReadOnlySpan<byte> data,
        VramUploadLayout layout,
        Image<Rgba32> canvas,
        int pixelsPerWord)
    {
        int x = layout.X;
        int y = layout.Y;
        int wordWidth = layout.WordWidth;
        int chunkHeight = layout.ChunkHeight;
        int deltaX = layout.DeltaX;
        int deltaY = layout.DeltaY;
        int bytesSinceStep = 0;
        int offset = 0;

        while (offset < data.Length)
        {
            int chunkBytes = checked(wordWidth * chunkHeight * 2);
            if (chunkBytes <= 0)
                throw new InvalidDataException($"Layout {layout.TableIndex} has a non-positive upload chunk size.");
            if (offset + chunkBytes > data.Length)
                throw new InvalidDataException($"Payload length 0x{data.Length:X} ends mid-chunk for layout {layout.TableIndex}.");

            int pixelX = x * pixelsPerWord;
            int pixelWidth = wordWidth * pixelsPerWord;
            if (pixelX < 0 || y < 0 || pixelX + pixelWidth > canvas.Width || y + chunkHeight > canvas.Height)
                throw new InvalidDataException($"Layout {layout.TableIndex} places a chunk outside PSX VRAM at x=0x{x:X}, y=0x{y:X}, w=0x{wordWidth:X}, h=0x{chunkHeight:X}.");

            ReadOnlySpan<byte> chunk = data.Slice(offset, chunkBytes);
            if (pixelsPerWord == 4)
                Draw4BppChunk(chunk, canvas, pixelX, y, wordWidth, chunkHeight);
            else
                Draw8BppChunk(chunk, canvas, pixelX, y, wordWidth, chunkHeight);

            offset += chunkBytes;
            bytesSinceStep += chunkBytes;
            x += deltaX;
            y += deltaY;

            if (layout.StepByteThreshold != 0 && bytesSinceStep == layout.StepByteThreshold)
            {
                bytesSinceStep = 0;
                x += layout.StepX;
                y += layout.StepY;
                wordWidth += layout.StepWordWidth;
                chunkHeight += layout.StepChunkHeight;
                deltaX += layout.StepDeltaX;
                deltaY += layout.StepDeltaY;
            }
        }
    }

    private static Image<Rgba32> CropTouched(Image<Rgba32> canvas, VramUploadLayout layout, int dataLength, int pixelsPerWord)
    {
        GetTouchedBounds(layout, dataLength, pixelsPerWord, out int minX, out int minY, out int maxX, out int maxY);
        var cropped = new Image<Rgba32>(maxX - minX, maxY - minY);

        for (int y = minY; y < maxY; y++)
        {
            for (int x = minX; x < maxX; x++)
                cropped[x - minX, y - minY] = canvas[x, y];
        }

        return cropped;
    }

    private static void GetTouchedBounds(VramUploadLayout layout, int dataLength, int pixelsPerWord, out int minX, out int minY, out int maxX, out int maxY)
    {
        int x = layout.X;
        int y = layout.Y;
        int wordWidth = layout.WordWidth;
        int chunkHeight = layout.ChunkHeight;
        int deltaX = layout.DeltaX;
        int deltaY = layout.DeltaY;
        int bytesSinceStep = 0;
        int offset = 0;

        minX = int.MaxValue;
        minY = int.MaxValue;
        maxX = int.MinValue;
        maxY = int.MinValue;

        while (offset < dataLength)
        {
            int chunkBytes = checked(wordWidth * chunkHeight * 2);
            if (chunkBytes <= 0 || offset + chunkBytes > dataLength)
                throw new InvalidDataException($"Payload length 0x{dataLength:X} does not match layout {layout.TableIndex}.");

            int pixelX = x * pixelsPerWord;
            int pixelWidth = wordWidth * pixelsPerWord;
            minX = Math.Min(minX, pixelX);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, pixelX + pixelWidth);
            maxY = Math.Max(maxY, y + chunkHeight);

            offset += chunkBytes;
            bytesSinceStep += chunkBytes;
            x += deltaX;
            y += deltaY;

            if (layout.StepByteThreshold != 0 && bytesSinceStep == layout.StepByteThreshold)
            {
                bytesSinceStep = 0;
                x += layout.StepX;
                y += layout.StepY;
                wordWidth += layout.StepWordWidth;
                chunkHeight += layout.StepChunkHeight;
                deltaX += layout.StepDeltaX;
                deltaY += layout.StepDeltaY;
            }
        }
    }

    private static void Draw4BppChunk(ReadOnlySpan<byte> chunk, Image<Rgba32> canvas, int pixelX, int pixelY, int wordWidth, int chunkHeight)
    {
        int bytesPerRow = wordWidth * 2;
        for (int y = 0; y < chunkHeight; y++)
        {
            int rowStart = y * bytesPerRow;
            for (int byteIndex = 0; byteIndex < bytesPerRow; byteIndex++)
            {
                byte value = chunk[rowStart + byteIndex];
                int x = pixelX + byteIndex * 2;
                canvas[x, pixelY + y] = Gray4(value & 0x0f);
                canvas[x + 1, pixelY + y] = Gray4(value >> 4);
            }
        }
    }

    private static void Draw8BppChunk(ReadOnlySpan<byte> chunk, Image<Rgba32> canvas, int pixelX, int pixelY, int wordWidth, int chunkHeight)
    {
        int bytesPerRow = wordWidth * 2;
        for (int y = 0; y < chunkHeight; y++)
        {
            int rowStart = y * bytesPerRow;
            for (int x = 0; x < bytesPerRow; x++)
            {
                byte value = chunk[rowStart + x];
                canvas[pixelX + x, pixelY + y] = new Rgba32(value, value, value, 255);
            }
        }
    }
}
