using System.Buffers.Binary;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace JojoExtractor.Psx;

/// <summary>
/// Decoders for PSX indexed-colour image data (4bpp and 8bpp linear layout).
///
/// PSX VRAM is a 1024x512 grid of 16-bit half-words. When `LoadImage` uploads
/// indexed-colour pixels, the bytes are written verbatim into VRAM starting
/// at the rectangle's top-left, row-major. Within each 16-bit half-word:
///
///   4bpp: 4 pixels per word, lowest nibble = leftmost pixel.
///         Equivalently, in byte order (little-endian): for each byte the
///         low nibble is the left pixel and the high nibble is the right.
///   8bpp: 2 pixels per word, lowest byte = leftmost pixel.
///         Equivalently, bytes are read in storage order, one per pixel.
///
/// CLUT lookups always use 16-bit BGR-15 entries (see <see cref="PsxColor"/>).
/// </summary>
public static class IndexedImageDecoder
{
    /// <summary>
    /// Decodes a 4bpp linear indexed image of the given pixel <paramref name="width"/>.
    /// The number of rows is derived from the buffer length. The CLUT is a flat
    /// 16-entry, 32-byte BGR-15 table.
    /// </summary>
    public static Image<Rgba32> Decode4bpp(ReadOnlySpan<byte> pixels, int width, ReadOnlySpan<byte> clut16)
    {
        if (width <= 0 || (width & 1) != 0)
            throw new ArgumentException("4bpp width must be a positive even number of pixels.", nameof(width));
        if (clut16.Length != 32)
            throw new ArgumentException($"4bpp CLUT must be exactly 32 bytes (got {clut16.Length}).", nameof(clut16));

        int bytesPerRow = width / 2;
        if (pixels.Length % bytesPerRow != 0)
            throw new ArgumentException(
                $"Pixel buffer length 0x{pixels.Length:X} is not a multiple of bytes-per-row 0x{bytesPerRow:X} (width {width}).",
                nameof(pixels));

        Span<Rgba32> palette = stackalloc Rgba32[16];
        FillPalette(clut16, palette);

        int height = pixels.Length / bytesPerRow;
        var image = new Image<Rgba32>(width, height);
        for (int y = 0; y < height; y++)
        {
            int rowStart = y * bytesPerRow;
            for (int xByte = 0; xByte < bytesPerRow; xByte++)
            {
                byte b = pixels[rowStart + xByte];
                int x = xByte * 2;
                image[x,     y] = palette[b & 0x0F];
                image[x + 1, y] = palette[(b >> 4) & 0x0F];
            }
        }
        return image;
    }

    /// <summary>
    /// Decodes an 8bpp linear indexed image of the given pixel <paramref name="width"/>.
    /// The CLUT is a flat 256-entry, 512-byte BGR-15 table.
    /// </summary>
    public static Image<Rgba32> Decode8bpp(ReadOnlySpan<byte> pixels, int width, ReadOnlySpan<byte> clut256)
    {
        if (width <= 0)
            throw new ArgumentException("8bpp width must be positive.", nameof(width));
        if (clut256.Length != 512)
            throw new ArgumentException($"8bpp CLUT must be exactly 512 bytes (got {clut256.Length}).", nameof(clut256));
        if (pixels.Length % width != 0)
            throw new ArgumentException(
                $"Pixel buffer length 0x{pixels.Length:X} is not a multiple of width {width}.",
                nameof(pixels));

        var palette = new Rgba32[256];
        FillPalette(clut256, palette);

        int height = pixels.Length / width;
        var image = new Image<Rgba32>(width, height);
        for (int y = 0; y < height; y++)
        {
            int rowStart = y * width;
            for (int x = 0; x < width; x++)
                image[x, y] = palette[pixels[rowStart + x]];
        }
        return image;
    }

    private static void FillPalette(ReadOnlySpan<byte> clutBytes, Span<Rgba32> palette)
    {
        for (int i = 0; i < palette.Length; i++)
        {
            ushort raw = BinaryPrimitives.ReadUInt16LittleEndian(clutBytes.Slice(i * 2, 2));
            var c = PsxColor.FromBgr15(raw);
            palette[i] = new Rgba32(c.R, c.G, c.B, c.A);
        }
    }

    /// <summary>
    /// Returns a copy of one 16-colour CLUT bank (32 bytes) from a flat
    /// CLUT buffer. Bank 0 is the first 32 bytes, bank 1 is the next, etc.
    /// </summary>
    public static byte[] GetClutBank(ReadOnlySpan<byte> flatClut, int bankIndex)
    {
        const int bank = 32;
        int start = bankIndex * bank;
        if (start < 0 || start + bank > flatClut.Length)
            throw new ArgumentOutOfRangeException(nameof(bankIndex),
                $"Bank {bankIndex} is outside CLUT of length 0x{flatClut.Length:X}.");
        return flatClut.Slice(start, bank).ToArray();
    }
}
