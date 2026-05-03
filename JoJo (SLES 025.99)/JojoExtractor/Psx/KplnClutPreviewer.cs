using System.Buffers.Binary;
using JojoExtractor.Pac;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace JojoExtractor.Psx;

public static class KplnClutPreviewer
{
    public const int VramClutRowWords = 0x180;
    public const int BaseVramY = 0x1e0;
    public const int PreviewRows = 0x18;

    public static int GetPaletteCount(PacFile pac)
    {
        int countFrom0803 = GetEntryData(pac, 0x0803).Length / 0x100;
        int countFrom0804 = GetDynamicPaletteCount(GetEntryData(pac, 0x0804));
        int countFrom0805 = GetDynamicPaletteCount(GetEntryData(pac, 0x0805));
        int countFrom0806 = GetDynamicPaletteCount(GetEntryData(pac, 0x0806));
        return new[] { countFrom0803, countFrom0804, countFrom0805, countFrom0806 }.Min();
    }

    public static Image<Rgba32> Render(PacFile pac, int paletteId, int side = 0, int scaleX = 2, int scaleY = 8)
    {
        if (paletteId < 0)
            throw new ArgumentOutOfRangeException(nameof(paletteId));
        if (side is not (0 or 1))
            throw new ArgumentOutOfRangeException(nameof(side), "Side must be 0 or 1.");
        if (scaleX < 1 || scaleY < 1)
            throw new ArgumentOutOfRangeException(nameof(scaleX), "Scale must be positive.");

        int paletteCount = GetPaletteCount(pac);
        if (paletteId >= paletteCount)
            throw new ArgumentOutOfRangeException(nameof(paletteId), $"Palette id {paletteId} is outside 0..{paletteCount - 1}.");

        using var raw = new Image<Rgba32>(VramClutRowWords, PreviewRows, new Rgba32(0, 0, 0, 0));

        WriteWords(raw, 0x1e8 + side, 0x000, Slice(GetEntryData(pac, 0x0803), paletteId * 0x100, 0x100));
        WriteDynamicWords(raw, 0x1ef + side, 0x000, GetEntryData(pac, 0x0804), paletteId);
        WriteDynamicWords(raw, 0x1e8 + side, 0x080, GetEntryData(pac, 0x0805), paletteId);
        WriteDynamicWords(raw, 0x1f1 + side, 0x000, GetEntryData(pac, 0x0806), paletteId);

        ReadOnlySpan<byte> fixedRows = GetEntryData(pac, 0x0807);
        int fixedY = 499 + side * 2;
        WriteWords(raw, fixedY, 0x000, SlicePadded(fixedRows, 0, 0x300));
        WriteWords(raw, fixedY + 1, 0x000, SlicePadded(fixedRows, 0x300, 0x300));

        return Scale(raw, scaleX, scaleY);
    }

    private static int GetDynamicPaletteCount(ReadOnlySpan<byte> data)
    {
        return data.Length >= 4 && data.Length % 4 == 0 ? 2 : 0;
    }

    private static ReadOnlySpan<byte> GetEntryData(PacFile pac, ushort opcode)
    {
        PacEntry? entry = pac.Entries.FirstOrDefault(e => (ushort)(e.Flags & 0xffff) == opcode);
        if (entry is null || entry.Value.Flags == 0 && opcode != 0)
            throw new InvalidDataException($"PAC does not contain opcode 0x{opcode:X4}.");

        return pac.GetEntryData(entry.Value);
    }

    private static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> data, int start, int length)
    {
        if (start < 0 || length < 0 || start + length > data.Length)
            throw new InvalidDataException($"Requested CLUT slice 0x{start:X}+0x{length:X} exceeds entry length 0x{data.Length:X}.");

        return data.Slice(start, length);
    }

    private static byte[] SlicePadded(ReadOnlySpan<byte> data, int start, int length)
    {
        var result = new byte[length];
        if (start < data.Length)
        {
            int available = Math.Min(length, data.Length - start);
            data.Slice(start, available).CopyTo(result);
        }

        return result;
    }

    private static void WriteWords(Image<Rgba32> image, int vramY, int xWord, ReadOnlySpan<byte> data)
    {
        int y = vramY - BaseVramY;
        if (y < 0 || y >= image.Height)
            throw new InvalidDataException($"CLUT row 0x{vramY:X} is outside preview window.");

        int words = data.Length / 2;
        for (int i = 0; i < words && xWord + i < image.Width; i++)
        {
            ushort raw = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(i * 2, 2));
            var color = PsxColor.FromBgr15(raw);
            image[xWord + i, y] = new Rgba32(color.R, color.G, color.B, color.A);
        }
    }

    private static void WriteDynamicWords(Image<Rgba32> image, int vramY, int xWord, ReadOnlySpan<byte> data, int paletteId)
    {
        int stride = data.Length / 2;
        WriteWords(image, vramY, xWord, Slice(data, paletteId * stride, stride));
    }

    private static Image<Rgba32> Scale(Image<Rgba32> source, int scaleX, int scaleY)
    {
        var scaled = new Image<Rgba32>(source.Width * scaleX, source.Height * scaleY);
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                Rgba32 pixel = source[x, y];
                for (int yy = 0; yy < scaleY; yy++)
                {
                    for (int xx = 0; xx < scaleX; xx++)
                        scaled[x * scaleX + xx, y * scaleY + yy] = pixel;
                }
            }
        }

        return scaled;
    }
}
