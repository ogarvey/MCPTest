using System.Buffers.Binary;
using System.Globalization;
using JojoExtractor.Pac;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace JojoExtractor.Psx;

public sealed record DirectVramFrameCandidate(
    PacEntry ImageEntry,
    PacEntry FrameEntry,
    PacEntry ClutEntry,
    VramUploadLayout Layout,
    int FrameCount,
    string Evidence);

public sealed record DirectVramFrameExport(
    int FrameIndex,
    string PngPath,
    int Width,
    int Height);

public static class DirectVramFrameRenderer
{
    private const int TilePixels = 16;
    private const int VramPixelWidth4Bpp = 1024 * 4;
    private const int VramHeight = 512;

    public static bool TryFindCandidate(PacFile pac, out DirectVramFrameCandidate? candidate)
    {
        IReadOnlyList<DirectVramFrameCandidate> candidates = FindCandidates(pac);
        candidate = candidates.Count == 1 ? candidates[0] : null;
        return candidate is not null;
    }

    public static IReadOnlyList<DirectVramFrameCandidate> FindCandidates(PacFile pac)
    {
        DirectVramFrameCandidate[] knownRuntimeCandidates = FindFun8001bad4Candidates(pac).ToArray();
        if (knownRuntimeCandidates.Length > 0)
            return knownRuntimeCandidates;

        return TryFindSingleCandidate(pac, out DirectVramFrameCandidate? candidate) && candidate is not null
            ? new[] { candidate }
            : Array.Empty<DirectVramFrameCandidate>();
    }

    private static bool TryFindSingleCandidate(PacFile pac, out DirectVramFrameCandidate? candidate)
    {
        candidate = null;
        PacEntry[] imageEntries = pac.Entries
            .Where(entry => (((ushort)(entry.Flags & 0xffff)) & 0x0f00) == 0x0200)
            .ToArray();
        if (imageEntries.Length != 1)
            return false;

        PacEntry imageEntry = imageEntries[0];
        ushort imageOpcode = (ushort)(imageEntry.Flags & 0xffff);
        VramUploadLayout layout;
        try
        {
            layout = VramTexturePreviewer.GetLayout(imageOpcode & 0xff);
        }
        catch
        {
            return false;
        }

        var frameCandidates = new List<(PacEntry Entry, int FrameCount)>();
        foreach (PacEntry entry in pac.Entries.Where(entry => entry.Index != imageEntry.Index))
        {
            if (TryGetFrameCount(pac.GetEntryData(entry), out int frameCount))
                frameCandidates.Add((entry, frameCount));
        }

        if (frameCandidates.Count != 1)
            return false;

        PacEntry frameEntry = frameCandidates[0].Entry;
        PacEntry[] clutCandidates = pac.Entries
            .Where(entry => entry.Index != imageEntry.Index && entry.Index != frameEntry.Index)
            .Where(entry => ClutDecoder.LooksLikeClut(pac.GetEntryData(entry)))
            .ToArray();
        if (clutCandidates.Length != 1)
            return false;

        candidate = new DirectVramFrameCandidate(
            imageEntry,
            frameEntry,
            clutCandidates[0],
            layout,
            frameCandidates[0].FrameCount,
            "FUN_80020b74/FUN_800209ec/FUN_8001ffd4 direct-frame path; single direct atlas, single validated 12-byte table, and single CLUT-bank record.");
        return true;
    }

    private static IEnumerable<DirectVramFrameCandidate> FindFun8001bad4Candidates(PacFile pac)
    {
        foreach ((uint frameDestination, uint clutDestination, int textureBaseXUnits, int textureBaseYPage) in new[]
        {
            (0x80115800u, 0x8010d800u, 0x18, 0x10),
            (0x80116000u, 0x8010dc00u, 0x24, 0x00),
        })
        {
            PacEntry? frameEntry = null;
            int frameCount = 0;
            foreach (PacEntry entry in pac.Entries.Where(entry => GetRamDestination(entry) == frameDestination))
            {
                if (!TryGetFrameCount(pac.GetEntryData(entry), out int count))
                    continue;

                if (frameEntry is not null)
                {
                    frameEntry = null;
                    break;
                }

                frameEntry = entry;
                frameCount = count;
            }

            if (frameEntry is null)
                continue;

            PacEntry[] clutEntries = pac.Entries
                .Where(entry => GetRamDestination(entry) == clutDestination)
                .Where(entry => ClutDecoder.LooksLikeClut(pac.GetEntryData(entry)))
                .ToArray();
            if (clutEntries.Length != 1)
                continue;

            PacEntry[] imageEntries = pac.Entries
                .Where(IsDirectVramEntry)
                .Select(entry => (Entry: entry, Layout: TryGetLayout(entry)))
                .Where(item => item.Layout.HasValue
                    && (item.Layout.Value.X >> 4) == textureBaseXUnits
                    && (item.Layout.Value.Y >= 0x100 ? 0x10 : 0) == textureBaseYPage)
                .Select(item => item.Entry)
                .ToArray();
            if (imageEntries.Length != 1)
                continue;

            VramUploadLayout layout = VramTexturePreviewer.GetLayout(((ushort)(imageEntries[0].Flags & 0xffff)) & 0xff);
            yield return new DirectVramFrameCandidate(
                imageEntries[0],
                frameEntry.Value,
                clutEntries[0],
                layout,
                frameCount,
                "FUN_8001bad4 direct-frame setup: DAT_8008a280/DAT_8008a284 are assigned frame-table bases 0x80115800/0x80116000, DAT_8008a260..263 assign texture bases 0x18/0x10 and 0x24/0, and LoadImage uploads CLUT data from 0x8010d800/0x8010dc00 before FUN_80020b74/FUN_8001ffd4 consume the tables.");
        }
    }

    public static IReadOnlyList<DirectVramFrameExport> ExportAll(PacFile pac, string pacPath, string outDir, IReadOnlyList<DirectVramFrameCandidate> candidates)
    {
        if (candidates.Count == 0)
            return Array.Empty<DirectVramFrameExport>();

        if (candidates.Count == 1)
            return Export(pac, pacPath, outDir, candidates[0]);

        Directory.CreateDirectory(outDir);
        var outputs = new List<DirectVramFrameExport>();
        foreach (DirectVramFrameCandidate candidate in candidates)
        {
            string setDir = Path.Combine(outDir, $"image{candidate.ImageEntry.Index:D2}_frames{candidate.FrameEntry.Index:D2}_clut{candidate.ClutEntry.Index:D2}");
            outputs.AddRange(Export(pac, pacPath, setDir, candidate));
        }

        return outputs;
    }

    public static IReadOnlyList<DirectVramFrameExport> Export(PacFile pac, string pacPath, string outDir, DirectVramFrameCandidate candidate)
    {
        Directory.CreateDirectory(outDir);

        ReadOnlySpan<byte> frameData = pac.GetEntryData(candidate.FrameEntry);
        ReadOnlySpan<byte> imageData = pac.GetEntryData(candidate.ImageEntry);
        ReadOnlySpan<byte> clutData = pac.GetEntryData(candidate.ClutEntry);
        byte[,] vram = BuildVramIndexCanvas(imageData, candidate.Layout);

        string baseName = Path.GetFileNameWithoutExtension(pacPath);
        var outputs = new List<DirectVramFrameExport>();
        for (int frameIndex = 0; frameIndex < candidate.FrameCount; frameIndex++)
        {
            using Image<Rgba32> image = RenderFrame(frameData, vram, clutData, candidate.Layout, frameIndex);
            string pngPath = Path.Combine(outDir, $"{baseName}_direct_frame{frameIndex:D4}.png");
            image.SaveAsPng(pngPath);
            outputs.Add(new DirectVramFrameExport(frameIndex, pngPath, image.Width, image.Height));
        }

        string manifestPath = Path.Combine(outDir, baseName + "_direct_frames_manifest.txt");
        File.WriteAllText(manifestPath, BuildManifest(pacPath, candidate, outputs));
        return outputs;
    }

    private static bool TryGetFrameCount(ReadOnlySpan<byte> frameData, out int frameCount)
    {
        frameCount = 0;
        if (frameData.Length < 12)
            return false;

        int firstMatrixOffset = ReadU16(frameData, 0) * 2;
        if (firstMatrixOffset < 12 || firstMatrixOffset > frameData.Length || firstMatrixOffset % 12 != 0)
            return false;

        int recordOffset = 0;
        while (recordOffset + 12 <= firstMatrixOffset)
        {
            int partOffset = recordOffset;
            bool sawFinalPart = false;
            while (partOffset + 12 <= firstMatrixOffset)
            {
                if (!TryReadPart(frameData, partOffset, firstMatrixOffset, out DirectFramePart part))
                    return false;

                partOffset += 12;
                if (!part.Continues)
                {
                    sawFinalPart = true;
                    break;
                }
            }

            if (!sawFinalPart)
                return false;

            frameCount++;
            recordOffset = partOffset;
        }

        return frameCount > 0 && recordOffset == firstMatrixOffset;
    }

    private static Image<Rgba32> RenderFrame(ReadOnlySpan<byte> frameData, byte[,] vram, ReadOnlySpan<byte> clutData, VramUploadLayout layout, int frameIndex)
    {
        List<DirectFramePart> parts = ReadFrameParts(frameData, frameIndex);
        if (parts.Count == 0)
            throw new InvalidDataException($"Frame {frameIndex} has no drawable parts.");

        var bounds = GetBounds(parts);
        int width = Math.Max(1, bounds.MaxX - bounds.MinX);
        int height = Math.Max(1, bounds.MaxY - bounds.MinY);
        var image = new Image<Rgba32>(width, height, new Rgba32(0, 0, 0, 0));

        int textureBaseXUnits = layout.X >> 4;
        int textureBaseYPage = layout.Y >= 0x100 ? 0x10 : 0;

        foreach (DirectFramePart part in parts)
        {
            int matrixStart = part.TileOffsetWords * 2;
            int xBase = -part.XOffset - bounds.MinX;
            int yBase = -part.YOffset - bounds.MinY;

            for (int col = 0; col < part.Columns; col++)
            {
                for (int row = 0; row < part.Rows; row++)
                {
                    int tileOffset = matrixStart + (col * part.Rows + row) * 2;
                    ushort tileWord = ReadU16(frameData, tileOffset);
                    if (tileWord == 0xffff)
                        continue;

                    DrawTile(image, vram, clutData, tileWord, textureBaseXUnits, textureBaseYPage, xBase + col * TilePixels, yBase + row * TilePixels);
                }
            }
        }

        return image;
    }

    private static List<DirectFramePart> ReadFrameParts(ReadOnlySpan<byte> frameData, int frameIndex)
    {
        int firstMatrixOffset = ReadU16(frameData, 0) * 2;
        int recordOffset = 0;
        for (int i = 0; i < frameIndex; i++)
        {
            do
            {
                if (!TryReadPart(frameData, recordOffset, firstMatrixOffset, out DirectFramePart skipped))
                    throw new InvalidDataException($"Frame table ended before frame {frameIndex}.");

                recordOffset += 12;
                if (!skipped.Continues)
                    break;
            } while (true);
        }

        var parts = new List<DirectFramePart>();
        do
        {
            if (!TryReadPart(frameData, recordOffset, firstMatrixOffset, out DirectFramePart part))
                break;

            parts.Add(part);
            recordOffset += 12;
            if (!part.Continues)
                break;
        } while (true);

        return parts;
    }

    private static bool TryReadPart(ReadOnlySpan<byte> frameData, int recordOffset, int firstMatrixOffset, out DirectFramePart part)
    {
        part = default;
        if (recordOffset < 0 || recordOffset + 12 > firstMatrixOffset || recordOffset + 12 > frameData.Length)
            return false;

        int tileOffsetWords = ReadU16(frameData, recordOffset);
        ushort dimensions = ReadU16(frameData, recordOffset + 2);
        int columns = dimensions & 0xff;
        int rows = dimensions >> 8;
        int marker = ReadU16(frameData, recordOffset + 8);
        bool continues = ReadU16(frameData, recordOffset + 10) != 0;
        int matrixStart = tileOffsetWords * 2;
        int cellCount = columns * rows;

        if (columns <= 0 || rows <= 0 || marker <= 0 || matrixStart < firstMatrixOffset || matrixStart + cellCount * 2 > frameData.Length)
            return false;

        part = new DirectFramePart(
            tileOffsetWords,
            columns,
            rows,
            ReadU16(frameData, recordOffset + 4),
            ReadU16(frameData, recordOffset + 6),
            marker,
            continues);
        return true;
    }

    private static byte[,] BuildVramIndexCanvas(ReadOnlySpan<byte> data, VramUploadLayout layout)
    {
        var canvas = new byte[VramHeight, VramPixelWidth4Bpp];
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
            if (chunkBytes <= 0 || offset + chunkBytes > data.Length)
                throw new InvalidDataException($"Payload length 0x{data.Length:X} does not match layout {layout.TableIndex}.");

            Draw4BppChunk(data.Slice(offset, chunkBytes), canvas, x * 4, y, wordWidth, chunkHeight);

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

        return canvas;
    }

    private static void Draw4BppChunk(ReadOnlySpan<byte> chunk, byte[,] canvas, int pixelX, int pixelY, int wordWidth, int chunkHeight)
    {
        int bytesPerRow = wordWidth * 2;
        for (int y = 0; y < chunkHeight; y++)
        {
            int rowStart = y * bytesPerRow;
            int targetY = pixelY + y;
            if (targetY < 0 || targetY >= VramHeight)
                continue;

            for (int byteIndex = 0; byteIndex < bytesPerRow; byteIndex++)
            {
                byte value = chunk[rowStart + byteIndex];
                int targetX = pixelX + byteIndex * 2;
                if (targetX < 0 || targetX + 1 >= VramPixelWidth4Bpp)
                    continue;

                canvas[targetY, targetX] = (byte)(value & 0x0f);
                canvas[targetY, targetX + 1] = (byte)(value >> 4);
            }
        }
    }

    private static void DrawTile(Image<Rgba32> image, byte[,] vram, ReadOnlySpan<byte> clutData, ushort tileWord, int textureBaseXUnits, int textureBaseYPage, int dstX, int dstY)
    {
        int textureValue = textureBaseXUnits * 0x40 + (tileWord & 0x07ff) + textureBaseYPage * 0x100;
        int texturePage = textureValue >> 8;
        int textureU = textureValue & 0x00f0;
        int textureV = (textureValue & 0x000f) * TilePixels;
        int texturePageWordX = (texturePage & 0x0f) * 0x40;
        int texturePageY = (texturePage & 0x10) == 0 ? 0 : 0x100;
        int clutBank = tileWord >> 11;

        for (int y = 0; y < TilePixels; y++)
        {
            int sourceY = texturePageY + textureV + y;
            int targetY = dstY + y;
            if (sourceY < 0 || sourceY >= VramHeight || targetY < 0 || targetY >= image.Height)
                continue;

            for (int x = 0; x < TilePixels; x++)
            {
                int sourceX = texturePageWordX * 4 + textureU + x;
                int targetX = dstX + x;
                if (sourceX < 0 || sourceX >= VramPixelWidth4Bpp || targetX < 0 || targetX >= image.Width)
                    continue;

                int pixelIndex = vram[sourceY, sourceX];
                if (pixelIndex == 0)
                    continue;

                Rgba32 color = ReadClutColor(clutData, clutBank, pixelIndex);
                if (color.A != 0)
                    image[targetX, targetY] = color;
            }
        }
    }

    private static Rgba32 ReadClutColor(ReadOnlySpan<byte> clutData, int bank, int pixelIndex)
    {
        int offset = bank * ClutDecoder.BankBytes + pixelIndex * ClutDecoder.BytesPerColor;
        if (offset < 0 || offset + 2 > clutData.Length)
        {
            byte gray = (byte)(pixelIndex * 17);
            return new Rgba32(gray, gray, gray, 255);
        }

        ushort raw = BinaryPrimitives.ReadUInt16LittleEndian(clutData.Slice(offset, 2));
        PsxColor.Rgba8 color = PsxColor.FromBgr15(raw);
        return new Rgba32(color.R, color.G, color.B, color.A);
    }

    private static (int MinX, int MinY, int MaxX, int MaxY) GetBounds(IEnumerable<DirectFramePart> parts)
    {
        int minX = int.MaxValue;
        int minY = int.MaxValue;
        int maxX = int.MinValue;
        int maxY = int.MinValue;

        foreach (DirectFramePart part in parts)
        {
            int x = -part.XOffset;
            int y = -part.YOffset;
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x + part.Columns * TilePixels);
            maxY = Math.Max(maxY, y + part.Rows * TilePixels);
        }

        return minX == int.MaxValue ? (0, 0, TilePixels, TilePixels) : (minX, minY, maxX, maxY);
    }

    private static string BuildManifest(string pacPath, DirectVramFrameCandidate candidate, IReadOnlyList<DirectVramFrameExport> outputs)
    {
        ushort imageOpcode = (ushort)(candidate.ImageEntry.Flags & 0xffff);
        ushort frameOpcode = (ushort)(candidate.FrameEntry.Flags & 0xffff);
        ushort clutOpcode = (ushort)(candidate.ClutEntry.Flags & 0xffff);

        return string.Join(Environment.NewLine, new[]
        {
            $"source_pac={pacPath}",
            $"image_entry={candidate.ImageEntry.Index.ToString(CultureInfo.InvariantCulture)}",
            $"image_opcode=0x{imageOpcode:X4}",
            $"image_layout={candidate.Layout.TableIndex.ToString(CultureInfo.InvariantCulture)}",
            $"frame_entry={candidate.FrameEntry.Index.ToString(CultureInfo.InvariantCulture)}",
            $"frame_opcode=0x{frameOpcode:X4}",
            $"clut_entry={candidate.ClutEntry.Index.ToString(CultureInfo.InvariantCulture)}",
            $"clut_opcode=0x{clutOpcode:X4}",
            $"clut_banks={(candidate.ClutEntry.DataLength / ClutDecoder.BankBytes).ToString(CultureInfo.InvariantCulture)}",
            $"frame_count={candidate.FrameCount.ToString(CultureInfo.InvariantCulture)}",
            $"code_evidence=FUN_800184c0/FUN_8001902c place the opcode-class 0x0200 payload into VRAM using DAT_8005991c layouts. FUN_80020b74 and FUN_800209ec select map bases from DAT_8008a280 and compute descriptor addresses as base + frame*0x0c. FUN_8001ffd4 consumes 12-byte frame records, 16x16 direct tile-word matrices, 0xffff empty cells, tileWord texture coordinates, and tileWord CLUT selectors. candidate_evidence={candidate.Evidence}",
            string.Empty
        }) + string.Join(Environment.NewLine, outputs.Select(output => $"png_output={output.PngPath}")) + Environment.NewLine;
    }

    private static bool IsDirectVramEntry(PacEntry entry) =>
        (((ushort)(entry.Flags & 0xffff)) & 0x0f00) == 0x0200;

    private static uint? GetRamDestination(PacEntry entry) =>
        CompressedTimExtractor.GetDefaultRamDestination((ushort)(entry.Flags & 0xffff));

    private static VramUploadLayout? TryGetLayout(PacEntry entry)
    {
        try
        {
            return VramTexturePreviewer.GetLayout(((ushort)(entry.Flags & 0xffff)) & 0xff);
        }
        catch
        {
            return null;
        }
    }

    private static ushort ReadU16(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));

    private readonly record struct DirectFramePart(
        int TileOffsetWords,
        int Columns,
        int Rows,
        int XOffset,
        int YOffset,
        int Marker,
        bool Continues);
}
