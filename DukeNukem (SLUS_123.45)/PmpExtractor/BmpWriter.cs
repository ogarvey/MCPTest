using System.Buffers.Binary;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

internal static class PngWriter
{
    public static Rgba32 FromPsx16(ushort pixel)
    {
        byte red = Expand5((pixel >> 0) & 0x1F);
        byte green = Expand5((pixel >> 5) & 0x1F);
        byte blue = Expand5((pixel >> 10) & 0x1F);
        byte alpha = pixel == 0 ? (byte)0 : (byte)255;
        return new Rgba32(red, green, blue, alpha);
    }

    public static void WritePsx16Png(string path, int width, int height, byte[] littleEndianPixelBytes)
    {
        if (littleEndianPixelBytes.Length != width * height * sizeof(ushort))
        {
            throw new ArgumentException("Unexpected pixel byte count for PNG export.", nameof(littleEndianPixelBytes));
        }

        ushort[] pixels = new ushort[width * height];
        for (int index = 0; index < pixels.Length; index++)
        {
            pixels[index] = BinaryPrimitives.ReadUInt16LittleEndian(
                littleEndianPixelBytes.AsSpan(index * sizeof(ushort), sizeof(ushort)));
        }

        WritePsx16Png(path, width, height, pixels);
    }

    public static void WritePsx16Png(string path, int width, int height, ushort[] pixels)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "PNG dimensions must be positive.");
        }

        if (pixels.Length < width * height)
        {
            throw new ArgumentException("Not enough pixel data for PNG export.", nameof(pixels));
        }

        using Image<Rgba32> image = new(width, height);
        for (int index = 0; index < width * height; index++)
        {
            int x = index % width;
            int y = index / width;
            image[x, y] = FromPsx16(pixels[index]);
        }

        image.SaveAsPng(path);
    }

    public static void WriteIndexed8PreviewPng(string path, int vramWordWidth, int height, ushort[] pixels)
    {
        if (vramWordWidth <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(vramWordWidth), "PNG dimensions must be positive.");
        }

        if (pixels.Length < vramWordWidth * height)
        {
            throw new ArgumentException("Not enough VRAM words for indexed 8bpp preview export.", nameof(pixels));
        }

        int width = vramWordWidth * 2;
        using Image<Rgba32> image = new(width, height);
        for (int index = 0; index < vramWordWidth * height; index++)
        {
            ushort word = pixels[index];
            byte leftIndex = (byte)(word & 0xff);
            byte rightIndex = (byte)(word >> 8);

            int x = (index % vramWordWidth) * 2;
            int y = index / vramWordWidth;
            image[x, y] = PreviewColor8(leftIndex);
            image[x + 1, y] = PreviewColor8(rightIndex);
        }

        image.SaveAsPng(path);
    }

    public static void WriteIndexed4PreviewPng(string path, int vramWordWidth, int height, ushort[] pixels)
    {
        if (vramWordWidth <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(vramWordWidth), "PNG dimensions must be positive.");
        }

        if (pixels.Length < vramWordWidth * height)
        {
            throw new ArgumentException("Not enough VRAM words for indexed 4bpp preview export.", nameof(pixels));
        }

        int width = vramWordWidth * 4;
        using Image<Rgba32> image = new(width, height);
        for (int index = 0; index < vramWordWidth * height; index++)
        {
            ushort word = pixels[index];
            byte nibble0 = (byte)(word & 0x0f);
            byte nibble1 = (byte)((word >> 4) & 0x0f);
            byte nibble2 = (byte)((word >> 8) & 0x0f);
            byte nibble3 = (byte)((word >> 12) & 0x0f);

            int x = (index % vramWordWidth) * 4;
            int y = index / vramWordWidth;
            image[x, y] = PreviewColor4(nibble0);
            image[x + 1, y] = PreviewColor4(nibble1);
            image[x + 2, y] = PreviewColor4(nibble2);
            image[x + 3, y] = PreviewColor4(nibble3);
        }

        image.SaveAsPng(path);
    }

    private static byte Expand5(int value)
    {
        return (byte)((value << 3) | (value >> 2));
    }

    private static Rgba32 PreviewColor8(byte index)
    {
        return new Rgba32(index, index, index, index == 0 ? (byte)0 : (byte)255);
    }

    private static Rgba32 PreviewColor4(byte index)
    {
        byte value = (byte)((index << 4) | index);
        return new Rgba32(value, value, value, index == 0 ? (byte)0 : (byte)255);
    }
}
