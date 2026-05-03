using System.Buffers.Binary;

public sealed class RgbPalette
{
    public const int ColorCount = 256;
    public const int Rgb888Size = ColorCount * 3;
    public const int DefaultDumSineOffset = 0x4034;

    private RgbPalette(RgbColor[] colors)
    {
        Colors = colors;
    }

    public RgbColor[] Colors { get; }

    public static RgbPalette LoadRgb888(string path, int offset)
    {
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "Palette offset must be non-negative.");
        }

        return ParseRgb888(File.ReadAllBytes(path), offset, path);
    }

    public static RgbPalette ParseRgb888(ReadOnlySpan<byte> data, int offset, string sourceName)
    {
        if (offset > data.Length || data.Length - offset < Rgb888Size)
        {
            throw new InvalidDataException($"{sourceName} does not contain a 768-byte RGB888 palette at offset 0x{offset:X}.");
        }

        var colors = new RgbColor[ColorCount];
        var paletteData = data.Slice(offset, Rgb888Size);
        for (var index = 0; index < colors.Length; index++)
        {
            var sourceOffset = index * 3;
            colors[index] = new RgbColor(
                paletteData[sourceOffset],
                paletteData[sourceOffset + 1],
                paletteData[sourceOffset + 2]);
        }

        return new RgbPalette(colors);
    }

    public static RgbPalette ParseColorMap(ReadOnlySpan<byte> data, string sourceName)
    {
        if (data.Length == 0 || data.Length % 3 != 0 || data.Length > Rgb888Size)
        {
            throw new InvalidDataException($"{sourceName} does not contain a valid PBM color map.");
        }

        var colors = new RgbColor[ColorCount];
        var colorCount = data.Length / 3;
        for (var index = 0; index < colorCount; index++)
        {
            var sourceOffset = index * 3;
            colors[index] = new RgbColor(
                data[sourceOffset],
                data[sourceOffset + 1],
                data[sourceOffset + 2]);
        }

        return new RgbPalette(colors);
    }
}

public readonly record struct RgbColor(byte Red, byte Green, byte Blue);
