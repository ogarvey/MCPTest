using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

public static class PngWriter
{
    public static void WriteIndexedPreview(string path, int width, int height, byte[] pixels, byte[] alpha, RgbPalette? palette = null)
    {
        if (pixels.Length != checked(width * height) || alpha.Length != pixels.Length)
        {
            throw new ArgumentException("Pixel and alpha buffers must match the image dimensions.");
        }

        var colors = palette?.Colors;
        using var image = new Image<Rgba32>(width, height);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < height; y++)
            {
                var row = accessor.GetRowSpan(y);
                var rowOffset = y * width;
                for (var x = 0; x < width; x++)
                {
                    var sourceOffset = rowOffset + x;
                    var value = pixels[sourceOffset];
                    row[x] = colors is null
                        ? new Rgba32(value, value, value, alpha[sourceOffset])
                        : new Rgba32(colors[value].Red, colors[value].Green, colors[value].Blue, alpha[sourceOffset]);
                }
            }
        });

        image.Save(path, new PngEncoder());
    }
}
