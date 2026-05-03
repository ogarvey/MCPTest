using System.Buffers.Binary;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace JojoExtractor.Psx;

/// <summary>
/// Decoder for PSX CLUT (palette) data.
///
/// A CLUT is a flat array of 16-bit BGR-15 colours. The PSX GPU treats:
///   - 16-colour (4bpp) palettes as 32 bytes ( 16 entries x 2 bytes )
///   - 256-colour (8bpp) palettes as 512 bytes (256 entries x 2 bytes )
///
/// On-disc `.CLT` files in this game are uniformly 0x200 = 512 bytes, which
/// corresponds to either:
///   - one full 256-colour CLUT (8bpp), or
///   - sixteen 16-colour CLUTs concatenated (4bpp).
///
/// Without further evidence we render every input as a single row of
/// 16-colour banks (CLUT bank width = 16, height = byteCount / 32). That
/// representation is loss-less and trivially regroupable.
/// </summary>
public static class ClutDecoder
{
    public const int BankColors = 16;
    public const int BytesPerColor = 2;
    public const int BankBytes = BankColors * BytesPerColor; // 32

    /// <summary>
    /// Returns true if the buffer length is a non-zero multiple of one bank
    /// (32 bytes) — i.e. it can be interpreted as one or more 16-colour CLUTs.
    /// </summary>
    public static bool LooksLikeClut(ReadOnlySpan<byte> data) =>
        data.Length > 0 && data.Length % BankBytes == 0;

    /// <summary>
    /// Renders the data as an Image where each row is one 16-colour bank
    /// and each column is one palette entry. Caller must dispose.
    /// </summary>
    public static Image<Rgba32> RenderAsBanks(ReadOnlySpan<byte> data)
    {
        if (!LooksLikeClut(data))
            throw new ArgumentException(
                $"Buffer length {data.Length} is not a multiple of {BankBytes} (one CLUT bank).",
                nameof(data));

        int banks = data.Length / BankBytes;
        var image = new Image<Rgba32>(BankColors, banks);

        for (int bank = 0; bank < banks; bank++)
        {
            for (int idx = 0; idx < BankColors; idx++)
            {
                int byteOffset = bank * BankBytes + idx * BytesPerColor;
                ushort raw = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(byteOffset, 2));
                var c = PsxColor.FromBgr15(raw);
                image[idx, bank] = new Rgba32(c.R, c.G, c.B, c.A);
            }
        }

        return image;
    }

    /// <summary>
    /// Renders the data scaled up so individual palette entries are easy
    /// to inspect by eye. Each entry becomes a `cellSize x cellSize` square.
    /// </summary>
    public static Image<Rgba32> RenderAsBanksScaled(ReadOnlySpan<byte> data, int cellSize = 16)
    {
        if (cellSize < 1)
            throw new ArgumentOutOfRangeException(nameof(cellSize));

        using var src = RenderAsBanks(data);
        var dst = new Image<Rgba32>(src.Width * cellSize, src.Height * cellSize);

        for (int y = 0; y < src.Height; y++)
        {
            for (int x = 0; x < src.Width; x++)
            {
                Rgba32 px = src[x, y];
                for (int dy = 0; dy < cellSize; dy++)
                    for (int dx = 0; dx < cellSize; dx++)
                        dst[x * cellSize + dx, y * cellSize + dy] = px;
            }
        }

        return dst;
    }
}
