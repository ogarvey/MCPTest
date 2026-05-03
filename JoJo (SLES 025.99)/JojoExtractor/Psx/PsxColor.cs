namespace JojoExtractor.Psx;

/// <summary>
/// Decodes PSX 16-bit "BGR-15" pixels.
///
/// Per the official PlayStation hardware reference, every 16-bit pixel in
/// PSX VRAM is laid out (little-endian half-word) as:
///
///     bits  0-4   : Red    (5-bit, 0..31)
///     bits  5-9   : Green  (5-bit, 0..31)
///     bits 10-14  : Blue   (5-bit, 0..31)
///     bit  15     : STP    (semi-transparency / mask flag)
///
/// A 16-bit value of 0x0000 is treated by the GPU as fully transparent;
/// every other value with STP=0 is opaque, and STP=1 enables semi-transparency
/// blending. For decoding to PNG we follow the standard convention used by
/// PSX texture viewers: 0x0000 -> alpha 0; everything else -> alpha 255.
///
/// The 5-bit channels are expanded to 8-bit using the standard
/// `(c &lt;&lt; 3) | (c &gt;&gt; 2)` rounding so that 0x1F maps to 0xFF.
/// </summary>
public static class PsxColor
{
    public readonly record struct Rgba8(byte R, byte G, byte B, byte A);

    public static Rgba8 FromBgr15(ushort raw)
    {
        int r5 = raw & 0x1F;
        int g5 = (raw >> 5) & 0x1F;
        int b5 = (raw >> 10) & 0x1F;

        byte r = (byte)((r5 << 3) | (r5 >> 2));
        byte g = (byte)((g5 << 3) | (g5 >> 2));
        byte b = (byte)((b5 << 3) | (b5 >> 2));
        byte a = raw == 0 ? (byte)0 : (byte)255;

        return new Rgba8(r, g, b, a);
    }
}
