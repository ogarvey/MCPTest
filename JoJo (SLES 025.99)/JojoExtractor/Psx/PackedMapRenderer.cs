using System.Buffers.Binary;
using System.Globalization;
using JojoExtractor.Pac;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace JojoExtractor.Psx;

public sealed record PackedMapDimensions(int WidthTiles, int HeightTiles, string Source);

public sealed record PackedMapClutSource(
    PacEntry Entry,
    int DataOffset,
    int DataLength,
    int VramX,
    int VramY,
    int VramWordWidth,
    int VramHeight,
    string Evidence);

public sealed record PackedMapCandidate(
    PacEntry ImageEntry,
    PacEntry MapEntry,
    PackedMapClutSource ClutSource,
    VramUploadLayout Layout,
    int TexturePageBase,
    IReadOnlyList<PackedMapDimensions> Dimensions,
    string Evidence);

public sealed record PackedMapExport(
    string PngPath,
    int Width,
    int Height,
    int WidthTiles,
    int HeightTiles);

public static class PackedMapRenderer
{
    private const int TilePixels = 16;
    private const int VramPixelWidth4Bpp = 1024 * 4;
    private const int VramHeight = 512;
    private const int StageClutY = 0x1e0;
    private const int StageClutWordWidth = 0x180;

    private static readonly (int Width, int Height, string Code)[] Fun800297ecDimensionCodes =
    {
        (0x20, 0x10, "0x21"),
        (0x20, 0x30, "0x23"),
        (0x30, 0x10, "0x31"),
        (0x30, 0x20, "0x32"),
        (0x40, 0x10, "0x41"),
        (0x40, 0x20, "0x42"),
    };

    public static IReadOnlyList<PackedMapCandidate> FindCandidates(PacFile pac)
    {
        PacEntry[] imageEntries = pac.Entries.Where(IsDirectVramEntry).ToArray();
        PacEntry[] mapEntries = pac.Entries.Where(entry => GetOpcode(entry) == 0x0102).ToArray();
        if (imageEntries.Length == 0 || mapEntries.Length == 0)
            return Array.Empty<PackedMapCandidate>();

        var candidates = new List<PackedMapCandidate>();
        foreach (PacEntry mapEntry in mapEntries)
        {
            PackedMapDimensions[] dimensions = GetDimensionCandidates((int)mapEntry.DataLength).ToArray();
            if (dimensions.Length == 0)
                continue;

            ReadOnlySpan<byte> mapData = pac.GetEntryData(mapEntry);
            foreach (PacEntry imageEntry in imageEntries)
            {
                VramUploadLayout layout;
                try
                {
                    layout = VramTexturePreviewer.GetLayout(GetOpcode(imageEntry) & 0xff);
                }
                catch
                {
                    continue;
                }

                int texturePageBase = (layout.X / 0x40) + (layout.Y >= 0x100 ? 0x10 : 0);
                var matchingClutSources = new List<PackedMapClutSource>();
                foreach (PackedMapClutSource source in FindClutSources(pac))
                {
                    if (MapClutsFitSource(mapData, source, maxCellsToCheck: 4096))
                        matchingClutSources.Add(source);
                }

                if (matchingClutSources.Any(IsEmbeddedTimClutSource))
                    matchingClutSources = matchingClutSources.Where(IsEmbeddedTimClutSource).ToList();

                foreach (PackedMapClutSource clutSource in matchingClutSources)
                {
                    candidates.Add(new PackedMapCandidate(
                        imageEntry,
                        mapEntry,
                        clutSource,
                        layout,
                        texturePageBase,
                        dimensions,
                        "FUN_8002b62c packed-map renderer: object+0x48 supplies a 32-bit cell map, object+0x44/+0x46 supply tile dimensions, each nonzero cell low halfword is written as the PSX CLUT coordinate, and the high halfword supplies texture page bucket plus 16x16 u/v coordinates. Setup paths at 0x800297ec, 0x8002c7ec, and 0x8002c924 populate these object fields for direct-VRAM maps."));
                }
            }
        }

        return candidates;
    }

    public static IReadOnlyList<PackedMapExport> ExportAll(PacFile pac, string pacPath, string outDir, IReadOnlyList<PackedMapCandidate> candidates)
    {
        if (candidates.Count == 0)
            return Array.Empty<PackedMapExport>();

        Directory.CreateDirectory(outDir);
        string baseName = Path.GetFileNameWithoutExtension(pacPath);
        var outputs = new List<PackedMapExport>();

        foreach (PackedMapCandidate candidate in candidates)
        {
            foreach (PackedMapDimensions dimensions in candidate.Dimensions)
            {
                using Image<Rgba32> image = Render(pac, candidate, dimensions);
                string suffix = $"image{candidate.ImageEntry.Index:D2}_map{candidate.MapEntry.Index:D2}_clut{candidate.ClutSource.Entry.Index:D2}_{dimensions.WidthTiles:X2}x{dimensions.HeightTiles:X2}";
                string pngPath = Path.Combine(outDir, $"{baseName}_packed_map_{suffix}.png");
                image.SaveAsPng(pngPath);
                outputs.Add(new PackedMapExport(pngPath, image.Width, image.Height, dimensions.WidthTiles, dimensions.HeightTiles));
            }
        }

        string manifestPath = Path.Combine(outDir, baseName + "_packed_maps_manifest.txt");
        File.WriteAllText(manifestPath, BuildManifest(pacPath, candidates, outputs));
        return outputs;
    }

    public static Image<Rgba32> Render(PacFile pac, PackedMapCandidate candidate, PackedMapDimensions dimensions)
    {
        ReadOnlySpan<byte> imageData = pac.GetEntryData(candidate.ImageEntry);
        ReadOnlySpan<byte> mapData = pac.GetEntryData(candidate.MapEntry);
        ReadOnlySpan<byte> fullClutData = pac.GetEntryData(candidate.ClutSource.Entry);
        ReadOnlySpan<byte> clutData = fullClutData.Slice(candidate.ClutSource.DataOffset, candidate.ClutSource.DataLength);
        byte[,] vram = BuildVramIndexCanvas(imageData, candidate.Layout);

        var image = new Image<Rgba32>(dimensions.WidthTiles * TilePixels, dimensions.HeightTiles * TilePixels, new Rgba32(0, 0, 0, 0));
        for (int tileY = 0; tileY < dimensions.HeightTiles; tileY++)
        {
            for (int tileX = 0; tileX < dimensions.WidthTiles; tileX++)
            {
                int cellOffset = GetPackedCellOffset(tileX, tileY, dimensions.WidthTiles);
                if (cellOffset < 0 || cellOffset + 4 > mapData.Length)
                    continue;

                ushort clutValue = BinaryPrimitives.ReadUInt16LittleEndian(mapData.Slice(cellOffset, 2));
                if (clutValue == 0)
                    continue;

                ushort textureValue = BinaryPrimitives.ReadUInt16LittleEndian(mapData.Slice(cellOffset + 2, 2));
                DrawTile(image, vram, clutData, candidate.ClutSource, candidate.TexturePageBase, clutValue, textureValue, tileX * TilePixels, tileY * TilePixels);
            }
        }

        return image;
    }

    private static IEnumerable<PackedMapDimensions> GetDimensionCandidates(int mapLength)
    {
        if (mapLength <= 0 || mapLength % 4 != 0)
            yield break;

        int cells = mapLength / 4;
        foreach ((int width, int height, string code) in Fun800297ecDimensionCodes)
        {
            if (width * height == cells)
            {
                yield return new PackedMapDimensions(
                    width,
                    height,
                    $"0x800297ec dimension table byte {code} gives width=0x{width:X}, height=0x{height:X}; this candidate also exactly matches map payload length 0x{mapLength:X} as width*height*4 bytes.");
            }
        }
    }

    private static IEnumerable<PackedMapClutSource> FindClutSources(PacFile pac)
    {
        foreach (PacEntry entry in pac.Entries)
        {
            ReadOnlySpan<byte> data = pac.GetEntryData(entry);
            if (TryReadTimClutSource(entry, data, out PackedMapClutSource? timSource) && timSource is not null)
            {
                yield return timSource;
                continue;
            }

            ushort opcode = GetOpcode(entry);
            if (opcode == 0x0101 && data.Length >= ClutDecoder.BankBytes && data.Length % 2 == 0)
            {
                yield return new PackedMapClutSource(
                    entry,
                    0,
                    data.Length,
                    0,
                    StageClutY,
                    StageClutWordWidth,
                    Math.Max(1, (data.Length + StageClutWordWidth * 2 - 1) / (StageClutWordWidth * 2)),
                    "FUN_8001a734/FUN_8001a890/FUN_8001a8fc upload DAT_8010d800 as a CLUT slab at VRAM row 0x1e0; opcode 0x0101 maps to DAT_8010d800 through PTR_DAT_8005988c.");
            }
        }
    }

    private static bool IsEmbeddedTimClutSource(PackedMapClutSource source) =>
        source.DataOffset != 0;

    private static bool TryReadTimClutSource(PacEntry entry, ReadOnlySpan<byte> data, out PackedMapClutSource? source)
    {
        source = null;
        if (data.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(data[..4]) != 0x10)
            return false;

        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 4));
        if ((flags & 0x08) == 0)
            return false;

        int blockLength = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(8, 4));
        if (blockLength < 12 || 8 + blockLength > data.Length)
            return false;

        int x = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(12, 2));
        int y = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(14, 2));
        int width = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(16, 2));
        int height = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(18, 2));
        int clutLength = blockLength - 12;
        if (width <= 0 || height <= 0 || clutLength <= 0)
            return false;

        source = new PackedMapClutSource(
            entry,
            20,
            clutLength,
            x,
            y,
            width,
            height,
            "self-contained TIM CLUT block: FUN_8002b62c consumes PSX CLUT coordinates from packed-map cells, and this entry supplies a TIM CLUT RECT matching those coordinates.");
        return true;
    }

    private static bool MapClutsFitSource(ReadOnlySpan<byte> mapData, PackedMapClutSource source, int maxCellsToCheck)
    {
        int checkedCells = 0;
        int matchedCells = 0;
        int cells = mapData.Length / 4;
        for (int cell = 0; cell < cells && checkedCells < maxCellsToCheck; cell++)
        {
            ushort clutValue = BinaryPrimitives.ReadUInt16LittleEndian(mapData.Slice(cell * 4, 2));
            if (clutValue == 0)
                continue;

            checkedCells++;
            if (TryGetClutOffset(source, clutValue, ClutDecoder.BankColors - 1, out int offset) && offset + 2 <= source.DataLength)
                matchedCells++;
        }

        return matchedCells > 0 && matchedCells == checkedCells;
    }

    private static int GetPackedCellOffset(int tileX, int tileY, int widthTiles)
    {
        int widthStride = widthTiles & 0x0f0;
        if (widthStride <= 0)
            return -1;

        int cellIndex = (tileX & 0x0f)
            + (((tileX & 0x0f0) + (tileY & 0x0f)) * 0x10)
            + ((tileY & 0x0ff0) * widthStride);
        return cellIndex * 4;
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

    private static void DrawTile(
        Image<Rgba32> image,
        byte[,] vram,
        ReadOnlySpan<byte> clutData,
        PackedMapClutSource clutSource,
        int texturePageBase,
        ushort clutValue,
        ushort textureValue,
        int dstX,
        int dstY)
    {
        int texturePage = texturePageBase + ((textureValue >> 6) & 0x1f);
        int textureU = textureValue & 0x00f0;
        int textureV = (textureValue & 0x000f) * TilePixels;
        int texturePageWordX = (texturePage & 0x0f) * 0x40;
        int texturePageY = (texturePage & 0x10) == 0 ? 0 : 0x100;

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

                Rgba32 color = ReadClutColor(clutData, clutSource, clutValue, pixelIndex);
                if (color.A != 0)
                    image[targetX, targetY] = color;
            }
        }
    }

    private static Rgba32 ReadClutColor(ReadOnlySpan<byte> clutData, PackedMapClutSource source, ushort clutValue, int pixelIndex)
    {
        if (!TryGetClutOffset(source, clutValue, pixelIndex, out int offset) || offset < 0 || offset + 2 > clutData.Length)
        {
            byte gray = (byte)(pixelIndex * 17);
            return new Rgba32(gray, gray, gray, pixelIndex == 0 ? (byte)0 : (byte)255);
        }

        ushort raw = BinaryPrimitives.ReadUInt16LittleEndian(clutData.Slice(offset, 2));
        PsxColor.Rgba8 color = PsxColor.FromBgr15(raw);
        return new Rgba32(color.R, color.G, color.B, color.A);
    }

    private static bool TryGetClutOffset(PackedMapClutSource source, ushort clutValue, int pixelIndex, out int offset)
    {
        int clutY = clutValue >> 6;
        int clutX = (clutValue & 0x3f) * ClutDecoder.BankColors + pixelIndex;
        int relativeX = clutX - source.VramX;
        int relativeY = clutY - source.VramY;
        if (relativeX < 0 || relativeY < 0 || relativeX >= source.VramWordWidth || relativeY >= source.VramHeight)
        {
            offset = -1;
            return false;
        }

        offset = (relativeY * source.VramWordWidth + relativeX) * ClutDecoder.BytesPerColor;
        return true;
    }

    private static string BuildManifest(string pacPath, IReadOnlyList<PackedMapCandidate> candidates, IReadOnlyList<PackedMapExport> outputs)
    {
        var lines = new List<string>
        {
            $"source_pac={pacPath}",
            "mode=packed-map-render",
            "code_evidence=FUN_8002b62c consumes 32-bit packed map cells from object+0x48. The low halfword is used as a PSX CLUT coordinate; the high halfword supplies texture page bucket plus 16x16 u/v coordinates. The map address swizzle is ((x&0x0f)+(((x&0x0f0)+(y&0x0f))*0x10)+((y&0x0ff0)*(width&0x0f0)))*4.",
            string.Empty,
            "candidates:"
        };

        foreach (PackedMapCandidate candidate in candidates)
        {
            lines.Add($"image_entry={candidate.ImageEntry.Index} image_opcode=0x{GetOpcode(candidate.ImageEntry):X4} image_layout={candidate.Layout.TableIndex} map_entry={candidate.MapEntry.Index} map_opcode=0x{GetOpcode(candidate.MapEntry):X4} clut_entry={candidate.ClutSource.Entry.Index} texture_page_base=0x{candidate.TexturePageBase:X}");
            lines.Add($"candidate_evidence={candidate.Evidence}");
            lines.Add($"clut_evidence={candidate.ClutSource.Evidence}");
            foreach (PackedMapDimensions dimensions in candidate.Dimensions)
                lines.Add($"dimension_candidate={dimensions.WidthTiles}x{dimensions.HeightTiles} evidence={dimensions.Source}");
        }

        lines.Add(string.Empty);
        lines.Add("outputs:");
        foreach (PackedMapExport output in outputs)
            lines.Add($"png={output.PngPath} pixels={output.Width}x{output.Height} tiles={output.WidthTiles}x{output.HeightTiles}");

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static bool IsDirectVramEntry(PacEntry entry) =>
        (GetOpcode(entry) & 0x0f00) == 0x0200;

    private static ushort GetOpcode(PacEntry entry) =>
        (ushort)(entry.Flags & 0xffff);
}
