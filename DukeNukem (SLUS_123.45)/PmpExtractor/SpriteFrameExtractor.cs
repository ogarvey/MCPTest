using System.Buffers.Binary;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

internal static class SpriteFrameExtractor
{
    private static readonly string[] KnownChunkNames =
    {
        "sector-data",
        "edge-data",
        "entity-data",
        "late-table-0",
        "late-table-1",
        "late-table-2",
        "late-table-3",
        "sector-search-data",
        "texture-group-table",
        "frame-metadata-table",
    };

    public static SpriteExtractionResult Extract(PmpFile pmp, string outputDirectory)
    {
        byte[] levelData = pmp.ReadSection(pmp.Header.LevelDataSection);
        List<EmbeddedChunk> chunks = ParseLevelDataChunks(levelData);
        if (chunks.Count < 10)
        {
            throw new InvalidDataException($"Expected at least 10 embedded level-data chunks, parsed {chunks.Count}.");
        }

        byte[] textureGroupBytes = DecodeChunk(levelData, chunks[8]);
        byte[] frameMetadataBytes = DecodeChunk(levelData, chunks[9]);
        List<TextureGroupEntry> textureGroups = ParseTextureGroupEntries(textureGroupBytes);
        List<SpriteFrameMetadataEntry> frameMetadataEntries = ParseFrameMetadataEntries(frameMetadataBytes);

        int rawLookupOffset = chunks[9].NextDescriptorOffset;
        int lookupEntryCount = ResolveLookupEntryCount(pmp.Header.MaybeLookupEntryCount, frameMetadataEntries.Count, levelData.Length - rawLookupOffset);
        ushort[] lookupIndices = ReadLookupIndices(levelData, rawLookupOffset, lookupEntryCount);
        ushort[] atlasWords = BuildVramAtlasWords(pmp);
        byte[] packedGfxBytes = pmp.ReadSection(pmp.Header.PackedGfxSection);

        string spriteDirectory = Path.Combine(outputDirectory, "frames");
        Directory.CreateDirectory(spriteDirectory);
        CleanupPreviousExports(spriteDirectory);

        List<SpriteFrameResult> frames = new();
        for (int recordIndex = 0; recordIndex < lookupEntryCount && recordIndex < frameMetadataEntries.Count; recordIndex++)
        {
            int lookupIndex = lookupIndices[recordIndex];
            if ((uint)lookupIndex >= 0x1800u)
            {
                continue;
            }

            SpriteFrameMetadataEntry metadata = frameMetadataEntries[recordIndex];
            int textureGroupIndex = metadata.TextureGroupIndex;
            if ((uint)textureGroupIndex >= textureGroups.Count)
            {
                continue;
            }

            TextureGroupEntry textureGroup = textureGroups[textureGroupIndex];
            ResolvedSpriteFrame frame = ResolveFrame(recordIndex, lookupIndex, metadata, textureGroup);
            PackedGfxFrame? packedGfxFrame = TryDecodePackedGfxFrame(packedGfxBytes, frame);
            if (packedGfxFrame is not null)
            {
                frame = frame with
                {
                    Width = packedGfxFrame.Width,
                    Height = packedGfxFrame.Height,
                    U = 0,
                    V = 0,
                    PackedGfxFrame = packedGfxFrame,
                };
            }

            if (frame.Width <= 0 || frame.Height <= 0)
            {
                continue;
            }

            string outputPath = Path.Combine(
                spriteDirectory,
                $"lookup-{lookupIndex:D4}-record-{recordIndex:D4}.{frame.TextureMode}.png");
            WriteResolvedFrame(outputPath, frame, atlasWords);

            frames.Add(new SpriteFrameResult(
                recordIndex,
                lookupIndex,
                outputPath,
                frame.TextureMode,
                frame.Width,
                frame.Height,
                frame.TexturePageWord,
                frame.TexturePageBaseX,
                frame.TexturePageBaseY,
                frame.U,
                frame.V,
                frame.ClutWord,
                frame.ClutX,
                frame.ClutY,
                frame.AnchorX,
                frame.AnchorY,
                ResolveOriginX(frame.Width, frame.AnchorX),
                ResolveOriginY(frame.Height, frame.AnchorY),
                ResolveAlignedLeft(frame.Width, frame.AnchorX),
                ResolveAlignedTop(frame.Height, frame.AnchorY),
                ResolveAlignedRight(frame.Width, frame.AnchorX),
                ResolveAlignedBottom(frame.Height, frame.AnchorY),
                frame.AnimationWord,
                frame.TextureGroupIndex,
                frame.PackedGfxFrame is null ? "atlas" : "packed-gfx",
                frame.PackedGfxFrame?.DescriptorOffset));
        }

        string textureGroupPath = Path.Combine(outputDirectory, "sprite-texture-groups.bin");
        File.WriteAllBytes(textureGroupPath, textureGroupBytes);
        string frameMetadataPath = Path.Combine(outputDirectory, "sprite-frame-metadata.bin");
        File.WriteAllBytes(frameMetadataPath, frameMetadataBytes);
        string lookupIndexPath = Path.Combine(outputDirectory, "sprite-lookup-indices.bin");
        File.WriteAllBytes(lookupIndexPath, levelData.AsSpan(rawLookupOffset, lookupEntryCount * sizeof(ushort)).ToArray());
        GroupedSpriteExportResult groupedSprites = ExportAlignedGroups(outputDirectory, frames);

        return new SpriteExtractionResult(
            textureGroupPath,
            frameMetadataPath,
            lookupIndexPath,
            textureGroups.Count,
            frameMetadataEntries.Count,
            lookupEntryCount,
            spriteDirectory,
            groupedSprites.GroupDirectory,
            BuildChunkDescriptors(chunks),
            groupedSprites.Groups,
            frames);
    }

    private static List<EmbeddedChunk> ParseLevelDataChunks(byte[] levelData)
    {
        List<EmbeddedChunk> chunks = new();
        int offset = 0;

        while (EmbeddedChunk.TryRead(levelData, offset, out EmbeddedChunk? maybeChunk, allowEmpty: true))
        {
            EmbeddedChunk chunk = maybeChunk!;
            chunks.Add(chunk);
            offset = chunk.NextDescriptorOffset;
        }

        return chunks;
    }

    private static byte[] DecodeChunk(byte[] levelData, EmbeddedChunk chunk)
    {
        return chunk.IsCompressed
            ? PmpLzDecompressor.Decompress(levelData.AsSpan(chunk.PayloadOffset, chunk.StoredByteCount), chunk.OutputSize)
            : levelData.AsSpan(chunk.PayloadOffset, chunk.StoredByteCount).ToArray();
    }

    private static List<TextureGroupEntry> ParseTextureGroupEntries(byte[] bytes)
    {
        if ((bytes.Length % TextureGroupEntry.Size) != 0)
        {
            throw new InvalidDataException($"Texture group table length 0x{bytes.Length:X} is not divisible by {TextureGroupEntry.Size}.");
        }

        List<TextureGroupEntry> groups = new(bytes.Length / TextureGroupEntry.Size);
        for (int offset = 0; offset < bytes.Length; offset += TextureGroupEntry.Size)
        {
            groups.Add(TextureGroupEntry.Parse(bytes.AsSpan(offset, TextureGroupEntry.Size)));
        }

        return groups;
    }

    private static List<SpriteFrameMetadataEntry> ParseFrameMetadataEntries(byte[] bytes)
    {
        if ((bytes.Length % SpriteFrameMetadataEntry.Size) != 0)
        {
            throw new InvalidDataException($"Frame metadata table length 0x{bytes.Length:X} is not divisible by {SpriteFrameMetadataEntry.Size}.");
        }

        List<SpriteFrameMetadataEntry> entries = new(bytes.Length / SpriteFrameMetadataEntry.Size);
        for (int offset = 0; offset < bytes.Length; offset += SpriteFrameMetadataEntry.Size)
        {
            entries.Add(SpriteFrameMetadataEntry.Parse(bytes.AsSpan(offset, SpriteFrameMetadataEntry.Size)));
        }

        return entries;
    }

    private static int ResolveLookupEntryCount(int headerLookupCount, int frameMetadataCount, int rawLookupByteCount)
    {
        int rawLookupCount = rawLookupByteCount / sizeof(ushort);
        if (headerLookupCount > 0)
        {
            return Math.Min(headerLookupCount, Math.Min(frameMetadataCount, rawLookupCount));
        }

        return Math.Min(frameMetadataCount, rawLookupCount);
    }

    private static ushort[] ReadLookupIndices(byte[] levelData, int offset, int count)
    {
        ushort[] indices = new ushort[count];
        for (int index = 0; index < count; index++)
        {
            indices[index] = BinaryPrimitives.ReadUInt16LittleEndian(levelData.AsSpan(offset + index * sizeof(ushort), sizeof(ushort)));
        }

        return indices;
    }

    private static ushort[] BuildVramAtlasWords(PmpFile pmp)
    {
        byte[] vramSection = pmp.ReadSection(pmp.Header.VramSection);
        ushort[] atlas = new ushort[VramExtractor.FullVramWidth * VramExtractor.FullVramHeight];
        int offset = 0;

        while (offset < vramSection.Length)
        {
            if (!VramUpload.TryRead(vramSection, offset, out VramUpload? maybeUpload))
            {
                throw new InvalidDataException($"Invalid VRAM upload while building atlas at offset 0x{offset:X}.");
            }

            VramUpload upload = maybeUpload!;
            byte[] decoded = upload.IsCompressed
                ? PmpLzDecompressor.Decompress(vramSection.AsSpan(upload.PayloadOffset, upload.StoredByteCount), upload.OutputSize)
                : vramSection.AsSpan(upload.PayloadOffset, upload.StoredByteCount).ToArray();

            for (int row = 0; row < upload.Height; row++)
            {
                for (int column = 0; column < upload.Width; column++)
                {
                    ushort word = BinaryPrimitives.ReadUInt16LittleEndian(
                        decoded.AsSpan((row * upload.Width + column) * sizeof(ushort), sizeof(ushort)));
                    atlas[(upload.Y + row) * VramExtractor.FullVramWidth + upload.X + column] = word;
                }
            }

            offset += upload.TotalByteCount;
        }

        return atlas;
    }

    private static ResolvedSpriteFrame ResolveFrame(int recordIndex, int lookupIndex, SpriteFrameMetadataEntry metadata, TextureGroupEntry group)
    {
        ushort texturePageWord = (ushort)((metadata.TextureDescriptorWord & 0x1f) | ((group.Flags & 0x13) << 7));
        int descriptorModeBits = (metadata.TextureDescriptorWord >> 5) & 0x3;
        int textureModeBits = (texturePageWord >> 7) & 0x3;
        string textureMode = textureModeBits switch
        {
            0 => "indexed4",
            1 => "indexed8",
            2 => "rgba5551",
            _ => "unknown",
        };
        TextureWindow textureWindow = ResolveTextureWindow(metadata.U, metadata.V, metadata.Width, metadata.Height, descriptorModeBits);

        return new ResolvedSpriteFrame(
            recordIndex,
            lookupIndex,
            metadata.Width,
            metadata.Height,
            textureMode,
            texturePageWord,
            (texturePageWord & 0x0f) * 64,
            ((texturePageWord >> 4) & 0x1) * 256,
            metadata.U,
            metadata.V,
            group.ClutWord,
            (group.ClutWord & 0x3f) * 16,
            group.ClutWord >> 6,
            metadata.AnchorX,
            metadata.AnchorY,
            metadata.AnimationWord,
                metadata.TextureGroupIndex,
                textureWindow);
    }

    private static void WriteResolvedFrame(string path, ResolvedSpriteFrame frame, ushort[] atlas)
    {
        using Image<Rgba32> image = new(frame.Width, frame.Height);
        for (int y = 0; y < frame.Height; y++)
        {
            for (int x = 0; x < frame.Width; x++)
            {
                if (frame.PackedGfxFrame is not null)
                {
                    image[x, y] = ReadPackedIndexed8Color(atlas, frame, frame.PackedGfxFrame, x, y);
                    continue;
                }

                image[x, y] = frame.TextureMode switch
                {
                    "indexed4" => ReadIndexed4Color(atlas, frame, x, y),
                    "indexed8" => ReadIndexed8Color(atlas, frame, x, y),
                    "rgba5551" => ReadRgba5551Color(atlas, frame, x, y),
                    _ => new Rgba32(255, 0, 255, 255),
                };
            }
        }

        image.SaveAsPng(path);
    }

    private static PackedGfxFrame? TryDecodePackedGfxFrame(byte[] packedGfxBytes, ResolvedSpriteFrame frame)
    {
        if (frame.TextureMode != "indexed8" || frame.U != 0 || frame.V != 0)
        {
            return null;
        }

        PackedGfxGroup? group = TryGetPackedGfxGroup(packedGfxBytes, frame.TextureGroupIndex);
        if (group is null)
        {
            return null;
        }

        int relativeIndex = frame.LookupIndex - group.LookupStart;
        if ((uint)relativeIndex >= group.LookupCount)
        {
            return null;
        }

        int selectorOffset = group.GroupOffset + 8 + group.DescriptorOffsetCount * sizeof(int) + relativeIndex;
        if ((uint)selectorOffset >= packedGfxBytes.Length)
        {
            return null;
        }

        byte selector = packedGfxBytes[selectorOffset];
        int descriptorPointerOffset = group.GroupOffset + 8 + selector * sizeof(int);
        if ((uint)descriptorPointerOffset > packedGfxBytes.Length - sizeof(int))
        {
            return null;
        }

        int descriptorOffset = BinaryPrimitives.ReadInt32LittleEndian(packedGfxBytes.AsSpan(descriptorPointerOffset, sizeof(int)));
        int descriptorBase = group.GroupOffset + descriptorOffset;
        if ((uint)descriptorBase > packedGfxBytes.Length - 6)
        {
            return null;
        }

        return DecodePackedGfxDescriptor(packedGfxBytes, descriptorOffset, descriptorBase);
    }

    private static PackedGfxFrame DecodePackedGfxDescriptor(byte[] packedGfxBytes, int descriptorOffset, int descriptorBase)
    {
        int width = packedGfxBytes[descriptorBase];
        int height = packedGfxBytes[descriptorBase + 1];
        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException($"Packed-gfx descriptor at 0x{descriptorOffset:X} has invalid size {width}x{height}.");
        }

        byte[] pixels = new byte[width * height];
        int tokenOffset = descriptorBase + 4;
        if ((uint)tokenOffset > packedGfxBytes.Length - 2)
        {
            throw new InvalidDataException($"Packed-gfx descriptor at 0x{descriptorOffset:X} is missing its token stream.");
        }

        byte token = packedGfxBytes[tokenOffset];
        int remaining = token >> 6;
        byte value = (byte)(token & 0x3f);
        byte nextToken = packedGfxBytes[tokenOffset + 1];
        int streamOffset = tokenOffset + 2;
        if (remaining == 0)
        {
            remaining = packedGfxBytes[tokenOffset + 1];
            if ((uint)streamOffset >= packedGfxBytes.Length)
            {
                throw new InvalidDataException($"Packed-gfx descriptor at 0x{descriptorOffset:X} ends mid-token.");
            }

            nextToken = packedGfxBytes[streamOffset++];
        }

        for (int pixelOffset = 0; pixelOffset < pixels.Length; pixelOffset++)
        {
            pixels[pixelOffset] = value;
            remaining--;
            if (remaining != 0 || pixelOffset + 1 >= pixels.Length)
            {
                continue;
            }

            token = nextToken;
            remaining = token >> 6;
            value = (byte)(token & 0x3f);
            if ((uint)streamOffset >= packedGfxBytes.Length)
            {
                throw new InvalidDataException($"Packed-gfx descriptor at 0x{descriptorOffset:X} ends mid-token.");
            }

            nextToken = packedGfxBytes[streamOffset++];
            if (remaining == 0)
            {
                remaining = nextToken;
                if ((uint)streamOffset >= packedGfxBytes.Length)
                {
                    throw new InvalidDataException($"Packed-gfx descriptor at 0x{descriptorOffset:X} ends mid-token.");
                }

                nextToken = packedGfxBytes[streamOffset++];
            }
        }

        return new PackedGfxFrame(descriptorOffset, width, height, pixels);
    }

    private static PackedGfxGroup? TryGetPackedGfxGroup(byte[] packedGfxBytes, int textureGroupIndex)
    {
        if (textureGroupIndex < 0 || packedGfxBytes.Length < sizeof(int))
        {
            return null;
        }

        int offsetTableByteCount = BinaryPrimitives.ReadInt32LittleEndian(packedGfxBytes.AsSpan(0, sizeof(int)));
        if (offsetTableByteCount <= 0 || (offsetTableByteCount % sizeof(int)) != 0 || offsetTableByteCount > packedGfxBytes.Length)
        {
            return null;
        }

        int groupCount = offsetTableByteCount / sizeof(int);
        if ((uint)textureGroupIndex >= groupCount)
        {
            return null;
        }

        int groupOffset = BinaryPrimitives.ReadInt32LittleEndian(packedGfxBytes.AsSpan(textureGroupIndex * sizeof(int), sizeof(int)));
        if (groupOffset < offsetTableByteCount || groupOffset > packedGfxBytes.Length - 8)
        {
            return null;
        }

        int lookupStart = BinaryPrimitives.ReadInt16LittleEndian(packedGfxBytes.AsSpan(groupOffset + 2, sizeof(short)));
        int lookupCount = BinaryPrimitives.ReadInt16LittleEndian(packedGfxBytes.AsSpan(groupOffset + 4, sizeof(short)));
        int descriptorOffsetCount = BinaryPrimitives.ReadInt16LittleEndian(packedGfxBytes.AsSpan(groupOffset + 6, sizeof(short)));
        if (lookupCount <= 0 || descriptorOffsetCount <= 0)
        {
            return null;
        }

        return new PackedGfxGroup(textureGroupIndex, groupOffset, lookupStart, lookupCount, descriptorOffsetCount);
    }

    private static Rgba32 ReadPackedIndexed8Color(ushort[] atlas, ResolvedSpriteFrame frame, PackedGfxFrame packedGfxFrame, int x, int y)
    {
        byte paletteIndex = packedGfxFrame.Pixels[y * packedGfxFrame.Width + x];
        return ReadClutColor(atlas, frame.ClutX + paletteIndex, frame.ClutY);
    }

    private static Rgba32 ReadIndexed4Color(ushort[] atlas, ResolvedSpriteFrame frame, int x, int y)
    {
        int texelX = ApplyTextureWindow(frame.TextureWindow, x, true);
        int texelY = ApplyTextureWindow(frame.TextureWindow, y, false);
        int wordX = frame.TexturePageBaseX + (texelX >> 2);
        int nibbleShift = (texelX & 0x3) * 4;
        ushort texelWord = ReadAtlasWord(atlas, wordX, frame.TexturePageBaseY + texelY);
        int paletteIndex = (texelWord >> nibbleShift) & 0x0f;
        return ReadClutColor(atlas, frame.ClutX + paletteIndex, frame.ClutY);
    }

    private static Rgba32 ReadIndexed8Color(ushort[] atlas, ResolvedSpriteFrame frame, int x, int y)
    {
        int texelX = ApplyTextureWindow(frame.TextureWindow, x, true);
        int texelY = ApplyTextureWindow(frame.TextureWindow, y, false);
        int wordX = frame.TexturePageBaseX + (texelX >> 1);
        ushort texelWord = ReadAtlasWord(atlas, wordX, frame.TexturePageBaseY + texelY);
        int paletteIndex = ((texelX & 0x1) == 0) ? (texelWord & 0xff) : (texelWord >> 8);
        return ReadClutColor(atlas, frame.ClutX + paletteIndex, frame.ClutY);
    }

    private static Rgba32 ReadRgba5551Color(ushort[] atlas, ResolvedSpriteFrame frame, int x, int y)
    {
        int texelX = ApplyTextureWindow(frame.TextureWindow, x, true);
        int texelY = ApplyTextureWindow(frame.TextureWindow, y, false);
        return PngWriter.FromPsx16(ReadAtlasWord(atlas, frame.TexturePageBaseX + texelX, frame.TexturePageBaseY + texelY));
    }

    private static TextureWindow ResolveTextureWindow(int u, int v, int width, int height, int descriptorModeBits)
    {
        int adjustedWidth = descriptorModeBits switch
        {
            1 => width >> 1,
            2 => (width >> 1) + 1,
            _ => width,
        };

        int horizontalSpan = LargestPowerOfTwoAtMost(Math.Max(adjustedWidth, 1));
        int horizontalBase = u;
        if (horizontalSpan != adjustedWidth)
        {
            horizontalBase = u & -horizontalSpan;
            while (horizontalBase + horizontalSpan < u + adjustedWidth)
            {
                horizontalSpan <<= 1;
                horizontalBase = u & -horizontalSpan;
            }
        }

        int verticalSpan = LargestPowerOfTwoAtMost(Math.Max(height, 1));
        int verticalBase = v;
        if (verticalSpan != height)
        {
            verticalSpan = height < 0x80 ? 0x80 : 0x100;
            verticalBase = v & -verticalSpan;
        }

        return new TextureWindow(
            horizontalSpan - 1,
            verticalSpan - 1,
            adjustedWidth,
            descriptorModeBits == 1 ? width - 1 : width)
        {
            StartU = u,
            StartV = v,
        };
    }

    private static int LargestPowerOfTwoAtMost(int value)
    {
        int result = 1;
        while ((result << 1) > 0 && (result << 1) <= value)
        {
            result <<= 1;
        }

        return result;
    }

    private static int ApplyTextureWindow(TextureWindow textureWindow, int coordinate, bool isHorizontal)
    {
        if (isHorizontal)
        {
            int localX = coordinate;
            if (textureWindow.HorizontalMirrorThreshold <= localX)
            {
                localX = textureWindow.HorizontalMirrorBoundary - localX;
            }

            return (textureWindow.StartU & ~textureWindow.WrapMaskX) | ((localX + textureWindow.StartU) & textureWindow.WrapMaskX);
        }

        return (textureWindow.StartV & ~textureWindow.WrapMaskY) | ((coordinate + textureWindow.StartV) & textureWindow.WrapMaskY);
    }

    private static Rgba32 ReadClutColor(ushort[] atlas, int x, int y)
    {
        return PngWriter.FromPsx16(ReadAtlasWord(atlas, x, y));
    }

    private static int ResolveOriginX(int width, int anchorX)
    {
         return (width >> 1) + anchorX;
    }

    private static int ResolveOriginY(int height, int anchorY)
    {
         return (height >> 1) + anchorY;
    }

    private static int ResolveAlignedLeft(int width, int anchorX)
    {
        return -ResolveOriginX(width, anchorX);
    }

    private static int ResolveAlignedTop(int height, int anchorY)
    {
        return -ResolveOriginY(height, anchorY);
    }

    private static int ResolveAlignedRight(int width, int anchorX)
    {
        return width - ResolveOriginX(width, anchorX);
    }

    private static int ResolveAlignedBottom(int height, int anchorY)
    {
        return height - ResolveOriginY(height, anchorY);
    }

    private static GroupedSpriteExportResult ExportAlignedGroups(string outputDirectory, IReadOnlyList<SpriteFrameResult> frames)
    {
        string groupDirectory = Path.Combine(outputDirectory, "frame-groups");
        Directory.CreateDirectory(groupDirectory);
        CleanupPreviousGroupedExports(groupDirectory);

        List<SpriteFrameGroupResult> groups = new();
        List<SpriteFrameResult> packedFrames = frames
            .Where(frame => string.Equals(frame.PixelSource, "packed-gfx", StringComparison.Ordinal))
            .OrderBy(frame => frame.TextureGroupIndex)
            .ThenBy(frame => frame.TextureMode)
            .ThenBy(frame => frame.RecordIndex)
            .ToList();

        if (packedFrames.Count == 0)
        {
            return new GroupedSpriteExportResult(groupDirectory, groups);
        }

        int groupStartIndex = 0;
        for (int frameIndex = 1; frameIndex <= packedFrames.Count; frameIndex++)
        {
            if (frameIndex < packedFrames.Count && AreFramesInSameAutoGroup(packedFrames[frameIndex - 1], packedFrames[frameIndex]))
            {
                continue;
            }

            groups.Add(WriteAlignedGroup(groupDirectory, groups.Count, packedFrames, groupStartIndex, frameIndex - 1));
            groupStartIndex = frameIndex;
        }

        return new GroupedSpriteExportResult(groupDirectory, groups);
    }

    private static bool AreFramesInSameAutoGroup(SpriteFrameResult previous, SpriteFrameResult current)
    {
        return current.PixelSource == previous.PixelSource
            && current.TextureGroupIndex == previous.TextureGroupIndex
            && current.TextureMode == previous.TextureMode
            && !ShouldSplitOppositeShortVerticalPivot(previous, current);
    }

    private static bool ShouldSplitOppositeShortVerticalPivot(SpriteFrameResult previous, SpriteFrameResult current)
    {
        if (Math.Abs(previous.Height - current.Height) > 4 || Math.Max(previous.Height, current.Height) > 48)
        {
            return false;
        }

        VerticalOrientation previousOrientation = MeasureVerticalOrientation(previous.OutputPath);
        VerticalOrientation currentOrientation = MeasureVerticalOrientation(current.OutputPath);
        return previousOrientation is not VerticalOrientation.Equal
            && currentOrientation is not VerticalOrientation.Equal
            && previousOrientation != currentOrientation;
    }

    private static VerticalOrientation MeasureVerticalOrientation(string sourcePath)
    {
        using Image<Rgba32> source = Image.Load<Rgba32>(sourcePath);

        int topSpan = MeasureOpaqueRowSpan(source, startY: 0, stepY: 1);
        int bottomSpan = MeasureOpaqueRowSpan(source, startY: source.Height - 1, stepY: -1);
        if (topSpan == bottomSpan)
        {
            return VerticalOrientation.Equal;
        }

        return topSpan > bottomSpan ? VerticalOrientation.Top : VerticalOrientation.Bottom;
    }

    private static int MeasureOpaqueRowSpan(Image<Rgba32> source, int startY, int stepY)
    {
        for (int y = startY; y >= 0 && y < source.Height; y += stepY)
        {
            int left = source.Width;
            int right = -1;
            for (int x = 0; x < source.Width; x++)
            {
                if (source[x, y].A == 0)
                {
                    continue;
                }

                left = Math.Min(left, x);
                right = Math.Max(right, x);
            }

            if (right >= left)
            {
                return right - left + 1;
            }
        }

        return 0;
    }

    private static SpriteFrameGroupResult WriteAlignedGroup(
        string groupDirectory,
        int groupIndex,
        IReadOnlyList<SpriteFrameResult> frames,
        int startIndex,
        int endIndex)
    {
        List<SpriteFrameResult> groupFrames = new(endIndex - startIndex + 1);
        for (int index = startIndex; index <= endIndex; index++)
        {
            groupFrames.Add(frames[index]);
        }

        SpriteFrameResult firstFrame = groupFrames[0];
        SpriteFrameResult lastFrame = groupFrames[^1];
        List<GroupedFrameLayout> frameLayouts = groupFrames
            .Select(frame => new GroupedFrameLayout(frame, MeasureOpaqueBottom(frame.OutputPath, frame.Height)))
            .ToList();
        int minAlignedLeft = groupFrames.Min(frame => frame.AlignedLeft);
        int maxAlignedRight = groupFrames.Max(frame => frame.AlignedRight);
        int baselineY = frameLayouts.Max(layout => layout.OpaqueBottom);
        int canvasWidth = Math.Max(1, maxAlignedRight - minAlignedLeft);
        int canvasHeight = Math.Max(1, frameLayouts.Max(layout => baselineY - layout.OpaqueBottom + layout.Frame.Height));
        int canvasOriginX = -minAlignedLeft;
        int canvasOriginY = baselineY;
        int minAlignedTop = -canvasOriginY;
        int maxAlignedBottom = canvasHeight - canvasOriginY;

        string folderName = string.Format(
            "group-{0:D4}_{1}_{2}_texgrp-{3:D3}_lookup-{4:D4}-{5:D4}",
            groupIndex,
            firstFrame.PixelSource,
            firstFrame.TextureMode,
            firstFrame.TextureGroupIndex,
            firstFrame.LookupIndex,
            lastFrame.LookupIndex);
        string outputPath = Path.Combine(groupDirectory, folderName);
        Directory.CreateDirectory(outputPath);

        List<GroupedSpriteFrameResult> alignedFrames = new(groupFrames.Count);
        for (int index = 0; index < frameLayouts.Count; index++)
        {
            GroupedFrameLayout layout = frameLayouts[index];
            SpriteFrameResult frame = layout.Frame;
            int offsetX = frame.AlignedLeft - minAlignedLeft;
            int offsetY = baselineY - layout.OpaqueBottom;
            string alignedFramePath = Path.Combine(
                outputPath,
                $"{index:D4}.lookup-{frame.LookupIndex:D4}.record-{frame.RecordIndex:D4}.{frame.TextureMode}.png");

            WriteAlignedFrame(alignedFramePath, frame.OutputPath, canvasWidth, canvasHeight, offsetX, offsetY);
            alignedFrames.Add(new GroupedSpriteFrameResult(
                index,
                frame.RecordIndex,
                frame.LookupIndex,
                alignedFramePath,
                offsetX,
                offsetY,
                frame.Width,
                frame.Height,
                frame.OriginX,
                frame.OriginY));
        }

        return new SpriteFrameGroupResult(
            groupIndex,
            outputPath,
            firstFrame.PixelSource,
            firstFrame.TextureMode,
            firstFrame.TextureGroupIndex,
            firstFrame.RecordIndex,
            lastFrame.RecordIndex,
            firstFrame.LookupIndex,
            lastFrame.LookupIndex,
            canvasWidth,
            canvasHeight,
            canvasOriginX,
            canvasOriginY,
            minAlignedLeft,
            minAlignedTop,
            maxAlignedRight,
            maxAlignedBottom,
            alignedFrames);
    }

    private static int MeasureOpaqueBottom(string sourcePath, int height)
    {
        using Image<Rgba32> source = Image.Load<Rgba32>(sourcePath);

        for (int y = source.Height - 1; y >= 0; y--)
        {
            for (int x = 0; x < source.Width; x++)
            {
                if (source[x, y].A != 0)
                {
                    return y;
                }
            }
        }

        return Math.Max(0, height - 1);
    }

    private static void WriteAlignedFrame(string outputPath, string sourcePath, int canvasWidth, int canvasHeight, int offsetX, int offsetY)
    {
        using Image<Rgba32> source = Image.Load<Rgba32>(sourcePath);
        using Image<Rgba32> canvas = new(canvasWidth, canvasHeight);

        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                canvas[offsetX + x, offsetY + y] = source[x, y];
            }
        }

        canvas.SaveAsPng(outputPath);
    }

    private static ushort ReadAtlasWord(ushort[] atlas, int x, int y)
    {
        if ((uint)x >= VramExtractor.FullVramWidth || (uint)y >= VramExtractor.FullVramHeight)
        {
            return 0;
        }

        return atlas[y * VramExtractor.FullVramWidth + x];
    }

    private static List<SpriteLevelChunkResult> BuildChunkDescriptors(IReadOnlyList<EmbeddedChunk> chunks)
    {
        List<SpriteLevelChunkResult> results = new(chunks.Count);
        for (int index = 0; index < chunks.Count; index++)
        {
            EmbeddedChunk chunk = chunks[index];
            string name = index < KnownChunkNames.Length ? KnownChunkNames[index] : $"late-chunk-{index:D2}";
            results.Add(new SpriteLevelChunkResult(
                index,
                name,
                chunk.DescriptorOffset,
                chunk.OutputSize,
                chunk.StoredByteCount,
                chunk.IsCompressed,
                chunk.NextDescriptorOffset));
        }

        return results;
    }

    private static void CleanupPreviousExports(string spriteDirectory)
    {
        foreach (string path in Directory.EnumerateFiles(spriteDirectory, "*.png"))
        {
            File.Delete(path);
        }
    }

    private static void CleanupPreviousGroupedExports(string groupDirectory)
    {
        foreach (string path in Directory.EnumerateFiles(groupDirectory))
        {
            File.Delete(path);
        }

        foreach (string path in Directory.EnumerateDirectories(groupDirectory))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}

internal sealed record SpriteExtractionResult(
    string TextureGroupTablePath,
    string FrameMetadataTablePath,
    string LookupIndexPath,
    int TextureGroupCount,
    int FrameMetadataCount,
    int LookupEntryCount,
    string SpriteDirectory,
    string GroupedSpriteDirectory,
    IReadOnlyList<SpriteLevelChunkResult> LevelDataChunks,
    IReadOnlyList<SpriteFrameGroupResult> Groups,
    IReadOnlyList<SpriteFrameResult> Frames);

internal sealed record GroupedSpriteExportResult(
    string GroupDirectory,
    IReadOnlyList<SpriteFrameGroupResult> Groups);

internal sealed record SpriteLevelChunkResult(
    int Index,
    string Name,
    int DescriptorOffset,
    int OutputSize,
    int StoredByteCount,
    bool IsCompressed,
    int NextDescriptorOffset);

internal sealed record SpriteFrameResult(
    int RecordIndex,
    int LookupIndex,
    string OutputPath,
    string TextureMode,
    int Width,
    int Height,
    int TexturePageWord,
    int TexturePageBaseX,
    int TexturePageBaseY,
    int U,
    int V,
    int ClutWord,
    int ClutX,
    int ClutY,
    int AnchorX,
    int AnchorY,
    int OriginX,
    int OriginY,
    int AlignedLeft,
    int AlignedTop,
    int AlignedRight,
    int AlignedBottom,
    uint AnimationWord,
    int TextureGroupIndex,
    string PixelSource,
    int? PackedGfxDescriptorOffset);

internal sealed record SpriteFrameGroupResult(
    int GroupIndex,
    string OutputPath,
    string PixelSource,
    string TextureMode,
    int TextureGroupIndex,
    int StartRecordIndex,
    int EndRecordIndex,
    int StartLookupIndex,
    int EndLookupIndex,
    int CanvasWidth,
    int CanvasHeight,
    int CanvasOriginX,
    int CanvasOriginY,
    int MinAlignedLeft,
    int MinAlignedTop,
    int MaxAlignedRight,
    int MaxAlignedBottom,
    IReadOnlyList<GroupedSpriteFrameResult> Frames);

internal sealed record GroupedSpriteFrameResult(
    int SequenceIndex,
    int RecordIndex,
    int LookupIndex,
    string OutputPath,
    int OffsetX,
    int OffsetY,
    int Width,
    int Height,
    int OriginX,
    int OriginY);

internal sealed record ResolvedSpriteFrame(
    int RecordIndex,
    int LookupIndex,
    int Width,
    int Height,
    string TextureMode,
    int TexturePageWord,
    int TexturePageBaseX,
    int TexturePageBaseY,
    int U,
    int V,
    int ClutWord,
    int ClutX,
    int ClutY,
    int AnchorX,
    int AnchorY,
    uint AnimationWord,
    int TextureGroupIndex,
    TextureWindow TextureWindow,
    PackedGfxFrame? PackedGfxFrame = null);

internal sealed record TextureWindow(
    int WrapMaskX,
    int WrapMaskY,
    int HorizontalMirrorThreshold,
    int HorizontalMirrorBoundary)
{
    public int StartU { get; init; }

    public int StartV { get; init; }
}

internal sealed record PackedGfxGroup(
    int TextureGroupIndex,
    int GroupOffset,
    int LookupStart,
    int LookupCount,
    int DescriptorOffsetCount);

internal sealed record GroupedFrameLayout(
    SpriteFrameResult Frame,
    int OpaqueBottom);

internal enum VerticalOrientation
{
    Equal,
    Top,
    Bottom,
}

internal sealed record PackedGfxFrame(
    int DescriptorOffset,
    int Width,
    int Height,
    byte[] Pixels);

internal sealed record TextureGroupEntry(
    ushort ClutWord,
    ushort UnknownWord2,
    byte Flags,
    byte Byte5,
    byte Byte6,
    byte Byte7)
{
    public const int Size = 8;

    public static TextureGroupEntry Parse(ReadOnlySpan<byte> data)
    {
        return new TextureGroupEntry(
            BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(0, 2)),
            BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(2, 2)),
            data[4],
            data[5],
            data[6],
            data[7]);
    }
}

internal sealed record SpriteFrameMetadataEntry(
    byte Width,
    byte Height,
    uint AnimationWord,
    ushort TextureDescriptorWord,
    byte U,
    byte V)
{
    public const int Size = 12;

    public int TextureGroupIndex => TextureDescriptorWord >> 7;

    public int AnchorX => unchecked((sbyte)((AnimationWord >> 8) & 0xff));

    public int AnchorY => unchecked((sbyte)((AnimationWord >> 16) & 0xff));

    public static SpriteFrameMetadataEntry Parse(ReadOnlySpan<byte> data)
    {
        return new SpriteFrameMetadataEntry(
            (byte)BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(0, 2)),
            (byte)BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(2, 2)),
            BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 4)),
            BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(8, 2)),
            data[10],
            data[11]);
    }
}
