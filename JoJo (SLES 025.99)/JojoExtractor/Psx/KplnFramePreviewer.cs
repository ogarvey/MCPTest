using System.Buffers.Binary;
using JojoExtractor.Pac;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace JojoExtractor.Psx;

public readonly record struct KplnFramePart(
    int TileOffsetWords,
    int Columns,
    int Rows,
    int XOffset,
    int YOffset,
    int Marker,
    bool Continues);

public static class KplnFramePreviewer
{
    public const int TilePixels = 16;
    public const ushort DefaultFrameOpcode = 0x0802;
    public const ushort DirectFrameOpcode = 0x0800;
    public const ushort CompressedTileOpcode = 0x0801;
    public const int DefaultClutBaseY = 0x1e8;

    public static int GetFrameCount(PacFile pac, ushort frameOpcode = DefaultFrameOpcode)
    {
        ReadOnlySpan<byte> frameData = GetEntryData(pac, frameOpcode);
        int recordCount = frameData.Length / 12;
        for (int i = 0; i < recordCount; i++)
        {
            if (ReadU16(frameData, i * 12 + 8) == 0)
                return i;
        }

        return recordCount;
    }

    public static Image<Rgba32> RenderFrame(
        PacFile pac,
        int frameIndex,
        ushort frameOpcode = DefaultFrameOpcode,
        int paletteId = 0,
        int side = 0,
        int scale = 2,
        int clutBase = 0,
        int? clutRowBase = null)
    {
        if (frameIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        if (paletteId < 0)
            throw new ArgumentOutOfRangeException(nameof(paletteId));
        if (side is not (0 or 1))
            throw new ArgumentOutOfRangeException(nameof(side), "Side must be 0 or 1.");
        if (scale < 1)
            throw new ArgumentOutOfRangeException(nameof(scale));
        if (clutBase < 0)
            throw new ArgumentOutOfRangeException(nameof(clutBase));

        ReadOnlySpan<byte> frameData = GetEntryData(pac, frameOpcode);
        ReadOnlySpan<byte> atlasData = GetEntryData(pac, 0x0202);
        VramUploadLayout uploadLayout = VramTexturePreviewer.GetLayout(2);
        int uploadBytesPerRow = uploadLayout.WordWidth * 2;
        int uploadHeight = atlasData.Length / uploadBytesPerRow;
        var textureBase = GetTextureBase(frameOpcode, side);
        var clutVram = BuildClutVram(pac, paletteId, side);
        int effectiveClutRowBase = clutRowBase ?? DefaultClutBaseY + side;

        var parts = ReadFrameParts(frameData, frameIndex);
        if (parts.Count == 0)
            throw new InvalidDataException($"Frame {frameIndex} has no drawable parts.");

        var bounds = GetBounds(parts);
        int width = Math.Max(1, bounds.maxX - bounds.minX);
        int height = Math.Max(1, bounds.maxY - bounds.minY);
        using var image = new Image<Rgba32>(width, height, new Rgba32(0, 0, 0, 0));

        foreach (var part in parts)
        {
            int matrixStart = part.TileOffsetWords * 2;
            int xBase = -part.XOffset - bounds.minX;
            int yBase = -part.YOffset - bounds.minY;

            for (int col = 0; col < part.Columns; col++)
            {
                for (int row = 0; row < part.Rows; row++)
                {
                    int tileIndexOffset = matrixStart + (col * part.Rows + row) * 2;
                    if (tileIndexOffset + 2 > frameData.Length)
                        continue;

                    ushort tileWord = BinaryPrimitives.ReadUInt16LittleEndian(frameData.Slice(tileIndexOffset, 2));
                    if (tileWord == 0xffff)
                        continue;

                    DrawTile(
                        image,
                        atlasData,
                        clutVram,
                        uploadLayout,
                        uploadBytesPerRow,
                        uploadHeight,
                        tileWord,
                        textureBase.xUnits,
                        textureBase.yPage,
                        clutBase,
                        effectiveClutRowBase,
                        xBase + col * TilePixels,
                        yBase + row * TilePixels);
                }
            }
        }

        return scale == 1 ? image.Clone() : Scale(image, scale);
    }

    public static Image<Rgba32> RenderCachedFrame(
        PacFile pac,
        int frameIndex,
        ushort frameOpcode = DefaultFrameOpcode,
        int paletteId = 0,
        int side = 0,
        int scale = 2,
        int clutBase = 0,
        int? clutRowBase = null,
        int clutMode = 0,
        int renderMode = 0,
        int orientation = 0)
    {
        if (frameIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        if (paletteId < 0)
            throw new ArgumentOutOfRangeException(nameof(paletteId));
        if (side is not (0 or 1))
            throw new ArgumentOutOfRangeException(nameof(side), "Side must be 0 or 1.");
        if (scale < 1)
            throw new ArgumentOutOfRangeException(nameof(scale));
        if (clutBase < 0)
            throw new ArgumentOutOfRangeException(nameof(clutBase));
        if (renderMode is < 0 or > 0xff)
            throw new ArgumentOutOfRangeException(nameof(renderMode));
        if (orientation is < 0 or > 3)
            throw new ArgumentOutOfRangeException(nameof(orientation));

        ReadOnlySpan<byte> frameData = GetEntryData(pac, frameOpcode);
        ReadOnlySpan<byte> compressedTiles = GetEntryData(pac, CompressedTileOpcode);
        var clutVram = BuildClutVram(pac, paletteId, side);
        int effectiveClutRowBase = clutRowBase ?? DefaultClutBaseY + side;

        var parts = ReadFrameParts(frameData, frameIndex);
        if (parts.Count == 0)
            throw new InvalidDataException($"Frame {frameIndex} has no drawable parts.");

        var bounds = GetBounds(parts);
        int width = Math.Max(1, bounds.maxX - bounds.minX);
        int height = Math.Max(1, bounds.maxY - bounds.minY);
        using var image = new Image<Rgba32>(width, height, new Rgba32(0, 0, 0, 0));

        foreach (var part in parts)
        {
            int cellCount = part.Columns * part.Rows;
            int maskStart = part.TileOffsetWords * 4;
            int maskBytes = ((cellCount + 31) / 32) * 4;
            int offsetListStart = maskStart + maskBytes;
            int offsetIndex = 0;
            int xBase = -part.XOffset - bounds.minX;
            int yBase = -part.YOffset - bounds.minY;
            int control = 0;
            int controlBitsLeft = 0;

            for (int col = 0; col < part.Columns; col++)
            {
                for (int row = 0; row < part.Rows; row++)
                {
                    if (controlBitsLeft == 0)
                    {
                        int maskOffset = maskStart + (col * part.Rows + row) / 8;
                        if (maskOffset >= frameData.Length)
                            continue;

                        control = frameData[maskOffset];
                        controlBitsLeft = 8;
                    }

                    bool isVisible = (control & 0x80) != 0;
                    control = (control << 1) & 0xff;
                    controlBitsLeft--;

                    if (!isVisible)
                        continue;

                    if (offsetIndex >= part.Marker)
                        continue;

                    int entryOffset = offsetListStart + offsetIndex * 4;
                    offsetIndex++;
                    if (entryOffset + 4 > frameData.Length)
                        continue;

                    uint tileEntry = BinaryPrimitives.ReadUInt32LittleEndian(frameData.Slice(entryOffset, 4));
                    int compressedOffset = (int)(tileEntry & 0x00ffffff);
                    if (compressedOffset < 0 || compressedOffset >= compressedTiles.Length)
                        continue;

                    byte[] tile = DecompressTile(compressedTiles.Slice(compressedOffset));
                    int clutSelector = GetCachedClutSelector(tileEntry, clutBase, clutMode, renderMode);
                    int transform = GetCachedTransform(tileEntry, renderMode, orientation);
                    DrawCachedTile(
                        image,
                        tile,
                        clutVram,
                        clutSelector,
                        transform,
                        effectiveClutRowBase,
                        xBase + col * TilePixels,
                        yBase + row * TilePixels);
                }
            }
        }

        return scale == 1 ? image.Clone() : Scale(image, scale);
    }

    public static List<KplnFramePart> ReadFrameParts(ReadOnlySpan<byte> frameData, int frameIndex)
    {
        var parts = new List<KplnFramePart>();
        int recordOffset = frameIndex * 12;
        while (recordOffset + 12 <= frameData.Length)
        {
            int marker = ReadU16(frameData, recordOffset + 8);
            if (marker == 0)
                return parts;

            ushort dimensions = ReadU16(frameData, recordOffset + 2);
            var part = new KplnFramePart(
                TileOffsetWords: ReadU16(frameData, recordOffset),
                Columns: dimensions & 0xff,
                Rows: dimensions >> 8,
                XOffset: ReadU16(frameData, recordOffset + 4),
                YOffset: ReadU16(frameData, recordOffset + 6),
                Marker: marker,
                Continues: ReadU16(frameData, recordOffset + 10) != 0);

            parts.Add(part);

            if (!part.Continues)
                return parts;

            recordOffset += 12;
        }

        return parts;
    }

    private static (int minX, int minY, int maxX, int maxY) GetBounds(IEnumerable<KplnFramePart> parts)
    {
        int minX = int.MaxValue;
        int minY = int.MaxValue;
        int maxX = int.MinValue;
        int maxY = int.MinValue;

        foreach (var part in parts)
        {
            int x = -part.XOffset;
            int y = -part.YOffset;
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x + part.Columns * TilePixels);
            maxY = Math.Max(maxY, y + part.Rows * TilePixels);
        }

        if (minX == int.MaxValue)
            return (0, 0, TilePixels, TilePixels);

        return (minX, minY, maxX, maxY);
    }

    private static void DrawTile(
        Image<Rgba32> image,
        ReadOnlySpan<byte> atlasData,
        Rgba32[,] clutVram,
        VramUploadLayout uploadLayout,
        int uploadBytesPerRow,
        int uploadHeight,
        ushort tileWord,
        int textureBaseXUnits,
        int textureBaseYPage,
        int clutBase,
        int clutBaseY,
        int dstX,
        int dstY)
    {
        int textureValue = textureBaseXUnits * 0x40 + (tileWord & 0x07ff) + textureBaseYPage * 0x100;
        int texturePage = textureValue >> 8;
        int textureU = textureValue & 0x00f0;
        int textureV = (textureValue & 0x000f) * TilePixels;
        int texturePageWordX = (texturePage & 0x0f) * 0x40;
        int texturePageY = (texturePage & 0x10) == 0 ? 0 : 0x100;
        int clutSelector = clutBase + (tileWord >> 11);

        for (int y = 0; y < TilePixels; y++)
        {
            int vramY = texturePageY + textureV + y;
            int sourceY = vramY - uploadLayout.Y;
            int targetY = dstY + y;
            if (sourceY < 0 || sourceY >= uploadHeight || targetY < 0 || targetY >= image.Height)
                continue;

            for (int x = 0; x < TilePixels; x++)
            {
                int vramPixelX = texturePageWordX * 4 + textureU + x;
                int sourceX = vramPixelX - uploadLayout.X * 4;
                int targetX = dstX + x;
                if (sourceX < 0 || sourceX >= uploadLayout.WordWidth * 4 || targetX < 0 || targetX >= image.Width)
                    continue;

                int pixelIndex = Read4BppPixel(atlasData, uploadBytesPerRow, sourceX, sourceY);
                if (pixelIndex == 0)
                    continue;

                Rgba32 color = ReadClutColor(clutVram, clutBaseY, clutSelector, pixelIndex);
                if (color.A == 0)
                    continue;

                image[targetX, targetY] = color;
            }
        }
    }

    private static (int xUnits, int yPage) GetTextureBase(ushort frameOpcode, int side)
    {
        return frameOpcode switch
        {
            DirectFrameOpcode => (((side * 0x100 + 0x180) >> 4), 0x10),
            0x0802 => (0, 0),
            _ => throw new ArgumentOutOfRangeException(nameof(frameOpcode), $"No code-backed KPLN texture base for opcode 0x{frameOpcode:X4}.")
        };
    }

    private static byte[] DecompressTile(ReadOnlySpan<byte> data)
    {
        var output = new byte[0x80];
        int source = 0;
        int target = 0;
        int control = 0;
        int controlBitsLeft = 0;

        while (target < output.Length)
        {
            if (controlBitsLeft == 0)
            {
                if (source >= data.Length)
                    throw new InvalidDataException("Compressed tile stream ended before control byte.");

                control = data[source++];
                controlBitsLeft = 8;
            }

            bool compressed = (control & 1) != 0;
            control >>= 1;
            controlBitsLeft--;

            if (!compressed)
            {
                if (source >= data.Length)
                    throw new InvalidDataException("Compressed tile literal exceeds stream length.");

                output[target++] = data[source++];
                continue;
            }

            if (source >= data.Length)
                throw new InvalidDataException("Compressed tile token exceeds stream length.");

            byte token = data[source++];
            int length = token & 0x0f;
            int distance = token >> 4;
            if (distance == 0)
            {
                if (source >= data.Length)
                    throw new InvalidDataException("Compressed tile run exceeds stream length.");

                byte value = data[source++];
                int runLength = length + 1;
                for (int i = 0; i < runLength && target < output.Length; i++)
                    output[target++] = value;
            }
            else
            {
                for (int i = 0; i < length && target < output.Length; i++)
                {
                    int copyFrom = target - distance;
                    output[target++] = copyFrom >= 0 ? output[copyFrom] : (byte)0;
                }
            }
        }

        return output;
    }

    private static void DrawCachedTile(
        Image<Rgba32> image,
        ReadOnlySpan<byte> tile,
        Rgba32[,] clutVram,
        int clutSelector,
        int transform,
        int clutBaseY,
        int dstX,
        int dstY)
    {
        bool flipY = (transform & 1) != 0;
        bool flipX = (transform & 2) != 0;

        for (int y = 0; y < TilePixels; y++)
        {
            int sourceY = flipY ? TilePixels - 1 - y : y;
            int targetY = dstY + y;
            if (targetY < 0 || targetY >= image.Height)
                continue;

            for (int x = 0; x < TilePixels; x++)
            {
                int sourceX = flipX ? TilePixels - 1 - x : x;
                int targetX = dstX + x;
                if (targetX < 0 || targetX >= image.Width)
                    continue;

                int pixelIndex = Read4BppPixel(tile, TilePixels / 2, sourceX, sourceY);
                if (pixelIndex == 0)
                    continue;

                Rgba32 color = ReadClutColor(clutVram, clutBaseY, clutSelector, pixelIndex);
                if (color.A != 0)
                    image[targetX, targetY] = color;
            }
        }
    }

    private static int GetCachedClutSelector(uint tileEntry, int clutBase, int clutMode, int renderMode)
    {
        if (clutMode > 0)
            return clutBase;

        int descriptorSelector = renderMode < 4
            ? (int)((tileEntry >> 24) & 0x3f)
            : (int)(tileEntry >> 24);
        return clutBase + descriptorSelector;
    }

    private static int GetCachedTransform(uint tileEntry, int renderMode, int orientation)
    {
        int descriptorTransform = (int)(tileEntry >> 30);
        return renderMode is 4 or 5 or 0x87
            ? orientation
            : descriptorTransform ^ orientation;
    }

    private static Rgba32 ReadClutColor(Rgba32[,] clutVram, int clutBaseY, int clutSelector, int pixelIndex)
    {
        int vramY = clutBaseY + clutSelector / 0x18;
        int clutX = (clutSelector % 0x18) * 16 + pixelIndex;
        int y = vramY - KplnClutPreviewer.BaseVramY;
        if (y < 0 || y >= clutVram.GetLength(0) || clutX < 0 || clutX >= clutVram.GetLength(1))
            return new Rgba32((byte)(pixelIndex * 17), (byte)(pixelIndex * 17), (byte)(pixelIndex * 17), pixelIndex == 0 ? (byte)0 : (byte)255);

        return clutVram[y, clutX];
    }

    private static Rgba32[,] BuildClutVram(PacFile pac, int paletteId, int side)
    {
        int paletteCount = KplnClutPreviewer.GetPaletteCount(pac);
        if (paletteId >= paletteCount)
            throw new ArgumentOutOfRangeException(nameof(paletteId), $"Palette id {paletteId} is outside 0..{paletteCount - 1}.");

        var clutVram = new Rgba32[KplnClutPreviewer.PreviewRows, KplnClutPreviewer.VramClutRowWords];
        WriteKplnClutPlacement(clutVram, pac, paletteId, side);
        WriteKplnClutPlacement(clutVram, pac, paletteId, side ^ 1);
        return clutVram;
    }

    private static void WriteKplnClutPlacement(Rgba32[,] clutVram, PacFile pac, int paletteId, int side)
    {
        WriteClutWords(clutVram, 0x1e8 + side, 0x000, Slice(GetEntryData(pac, 0x0803), paletteId * 0x100, 0x100));
        WriteDynamicClutWords(clutVram, 0x1ef + side, 0x000, GetEntryData(pac, 0x0804), paletteId);
        WriteDynamicClutWords(clutVram, 0x1e8 + side, 0x080, GetEntryData(pac, 0x0805), paletteId);
        WriteDynamicClutWords(clutVram, 0x1f1 + side, 0x000, GetEntryData(pac, 0x0806), paletteId);

        ReadOnlySpan<byte> fixedRows = GetEntryData(pac, 0x0807);
        int fixedY = 499 + side * 2;
        WriteClutWords(clutVram, fixedY, 0x000, SlicePadded(fixedRows, 0, 0x300));
        WriteClutWords(clutVram, fixedY + 1, 0x000, SlicePadded(fixedRows, 0x300, 0x300));
    }

    private static void WriteDynamicClutWords(Rgba32[,] clutVram, int vramY, int xWord, ReadOnlySpan<byte> data, int paletteId)
    {
        int stride = data.Length / 2;
        WriteClutWords(clutVram, vramY, xWord, Slice(data, paletteId * stride, stride));
    }

    private static void WriteClutWords(Rgba32[,] clutVram, int vramY, int xWord, ReadOnlySpan<byte> data)
    {
        int y = vramY - KplnClutPreviewer.BaseVramY;
        if (y < 0 || y >= clutVram.GetLength(0))
            throw new InvalidDataException($"CLUT row 0x{vramY:X} is outside preview window.");

        int words = data.Length / 2;
        for (int i = 0; i < words && xWord + i < clutVram.GetLength(1); i++)
        {
            ushort raw = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(i * 2, 2));
            var color = PsxColor.FromBgr15(raw);
            clutVram[y, xWord + i] = new Rgba32(color.R, color.G, color.B, color.A);
        }
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

    private static int Read4BppPixel(ReadOnlySpan<byte> data, int bytesPerRow, int x, int y)
    {
        byte value = data[y * bytesPerRow + x / 2];
        return (x & 1) == 0 ? value & 0x0f : value >> 4;
    }

    private static ushort ReadU16(ReadOnlySpan<byte> data, int offset)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
    }

    private static ReadOnlySpan<byte> GetEntryData(PacFile pac, ushort opcode)
    {
        foreach (var entry in pac.Entries)
        {
            if ((ushort)(entry.Flags & 0xffff) == opcode)
                return pac.GetEntryData(entry);
        }

        throw new InvalidDataException($"PAC does not contain opcode 0x{opcode:X4}.");
    }

    private static Image<Rgba32> Scale(Image<Rgba32> source, int scale)
    {
        var scaled = new Image<Rgba32>(source.Width * scale, source.Height * scale);
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                Rgba32 pixel = source[x, y];
                for (int yy = 0; yy < scale; yy++)
                {
                    for (int xx = 0; xx < scale; xx++)
                        scaled[x * scale + xx, y * scale + yy] = pixel;
                }
            }
        }

        return scaled;
    }
}
