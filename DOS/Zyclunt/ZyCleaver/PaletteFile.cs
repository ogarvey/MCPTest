using SixLabors.ImageSharp.PixelFormats;

namespace ZyCleaver;

internal sealed class PaletteFile
{
    private PaletteFile(string sourcePath, IReadOnlyList<Rgba32> colors)
    {
        SourcePath = sourcePath;
        Colors = colors;
    }

    public string SourcePath { get; }

    public IReadOnlyList<Rgba32> Colors { get; }

    public static PaletteFile Load(string path)
    {
        var bytes = File.ReadAllBytes(path);

        if (bytes.Length != 768)
        {
            throw new InvalidDataException(
                $"Unsupported palette size for {Path.GetFileName(path)}: expected 768 bytes, got {bytes.Length}." );
        }

        var colors = new Rgba32[256];

        for (var index = 0; index < colors.Length; index++)
        {
            var red = ScaleVga6Bit(bytes[(index * 3) + 0]);
            var green = ScaleVga6Bit(bytes[(index * 3) + 1]);
            var blue = ScaleVga6Bit(bytes[(index * 3) + 2]);
            colors[index] = new Rgba32(red, green, blue, 0xff);
        }

        return new PaletteFile(Path.GetFullPath(path), colors);
    }

    private static byte ScaleVga6Bit(byte value)
    {
        if (value > 0x3f)
        {
            throw new InvalidDataException($"Palette component {value} is outside the expected VGA 6-bit range.");
        }

        return (byte)((value * 255 + 31) / 63);
    }
}
