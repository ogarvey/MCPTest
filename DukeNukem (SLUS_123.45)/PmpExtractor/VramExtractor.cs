using System.Buffers.Binary;

internal static class VramExtractor
{
    public const int FullVramWidth = 1024;
    public const int FullVramHeight = 512;

    public static VramExtractionResult Extract(PmpFile pmp, string outputDirectory)
    {
        byte[] vramSection = pmp.ReadSection(pmp.Header.VramSection);
        string vramDirectory = Path.Combine(outputDirectory, "vram");
        Directory.CreateDirectory(vramDirectory);
        CleanupPreviousExports(vramDirectory);

        List<VramUploadResult> uploads = new();
        ushort[] canvas = new ushort[FullVramWidth * FullVramHeight];
        int offset = 0;
        int maxX = 0;
        int maxY = 0;

        for (int index = 0; offset < vramSection.Length; index++)
        {
            if (!VramUpload.TryRead(vramSection, offset, out VramUpload? maybeUpload))
            {
                throw new InvalidDataException($"Invalid VRAM upload descriptor at offset 0x{offset:X}.");
            }

            VramUpload upload = maybeUpload!;
            byte[] decoded = upload.IsCompressed
                ? PmpLzDecompressor.Decompress(vramSection.AsSpan(upload.PayloadOffset, upload.StoredByteCount), upload.OutputSize)
                : vramSection.AsSpan(upload.PayloadOffset, upload.StoredByteCount).ToArray();
            ushort[] words = DecodeWords(decoded);

            BlitUpload(canvas, upload, decoded);

            string rgba5551Path = Path.Combine(
                vramDirectory,
                $"rect-{index:D2}-x{upload.X:D4}-y{upload.Y:D3}-w{upload.Width:D4}-h{upload.Height:D3}.rgba5551.png");
            PngWriter.WritePsx16Png(rgba5551Path, upload.Width, upload.Height, words);

            string indexed8Path = Path.Combine(
                vramDirectory,
                $"rect-{index:D2}-x{upload.X:D4}-y{upload.Y:D3}-w{upload.Width * 2:D4}-h{upload.Height:D3}.indexed8.png");
            PngWriter.WriteIndexed8PreviewPng(indexed8Path, upload.Width, upload.Height, words);

            string indexed4Path = Path.Combine(
                vramDirectory,
                $"rect-{index:D2}-x{upload.X:D4}-y{upload.Y:D3}-w{upload.Width * 4:D4}-h{upload.Height:D3}.indexed4.png");
            PngWriter.WriteIndexed4PreviewPng(indexed4Path, upload.Width, upload.Height, words);

            uploads.Add(new VramUploadResult(
                index,
                new VramPreviewPaths(rgba5551Path, indexed8Path, indexed4Path),
                offset,
                upload.X,
                upload.Y,
                upload.Width,
                upload.Height,
                upload.Width,
                upload.Width * 2,
                upload.Width * 4,
                upload.UnknownWord0,
                upload.OutputSize,
                upload.StoredByteCount,
                upload.IsCompressed));

            maxX = Math.Max(maxX, upload.X + upload.Width);
            maxY = Math.Max(maxY, upload.Y + upload.Height);
            offset += upload.TotalByteCount;
        }

        if (pmp.Header.MaybeVramRectCount > 0 && uploads.Count != pmp.Header.MaybeVramRectCount)
        {
            throw new InvalidDataException(
                $"VRAM upload count mismatch. Header says {pmp.Header.MaybeVramRectCount}, parsed {uploads.Count}.");
        }

        string fullAtlasPath = Path.Combine(vramDirectory, "atlas-rgba5551-full.png");
        PngWriter.WritePsx16Png(fullAtlasPath, FullVramWidth, FullVramHeight, canvas);

        string usedAtlasPath = Path.Combine(vramDirectory, $"atlas-rgba5551-used-{maxX}x{maxY}.png");
        PngWriter.WritePsx16Png(usedAtlasPath, maxX, maxY, CropCanvas(canvas, maxX, maxY));

        return new VramExtractionResult(
            pmp.Header.MaybeVramRectCount,
            uploads.Count,
            maxX,
            maxY,
            "Upload coordinates and widths are in PSX VRAM 16-bit word units. Per-rect previews are emitted in raw rgba5551, indexed8, and indexed4 interpretations because the final bit depth and CLUT are not known at upload time.",
            fullAtlasPath,
            usedAtlasPath,
            uploads);
    }

    private static void BlitUpload(ushort[] canvas, VramUpload upload, byte[] decoded)
    {
        int expectedByteCount = upload.Width * upload.Height * sizeof(ushort);
        if (decoded.Length != expectedByteCount)
        {
            throw new InvalidDataException(
                $"VRAM upload decoded to 0x{decoded.Length:X} bytes, expected 0x{expectedByteCount:X}.");
        }

        for (int row = 0; row < upload.Height; row++)
        {
            int sourceRowOffset = row * upload.Width * sizeof(ushort);
            int destinationRowOffset = (upload.Y + row) * FullVramWidth + upload.X;
            for (int column = 0; column < upload.Width; column++)
            {
                ushort pixel = BinaryPrimitives.ReadUInt16LittleEndian(decoded.AsSpan(sourceRowOffset + column * sizeof(ushort), sizeof(ushort)));
                canvas[destinationRowOffset + column] = pixel;
            }
        }
    }

    private static ushort[] CropCanvas(ushort[] fullCanvas, int width, int height)
    {
        ushort[] cropped = new ushort[width * height];
        for (int row = 0; row < height; row++)
        {
            Array.Copy(fullCanvas, row * FullVramWidth, cropped, row * width, width);
        }

        return cropped;
    }

    private static ushort[] DecodeWords(byte[] decoded)
    {
        ushort[] words = new ushort[decoded.Length / sizeof(ushort)];
        for (int index = 0; index < words.Length; index++)
        {
            words[index] = BinaryPrimitives.ReadUInt16LittleEndian(decoded.AsSpan(index * sizeof(ushort), sizeof(ushort)));
        }

        return words;
    }

    private static void CleanupPreviousExports(string vramDirectory)
    {
        foreach (string path in Directory.EnumerateFiles(vramDirectory, "rect-*"))
        {
            File.Delete(path);
        }

        foreach (string path in Directory.EnumerateFiles(vramDirectory, "atlas-*"))
        {
            File.Delete(path);
        }
    }
}

internal sealed record VramExtractionResult(
    int HeaderRectCount,
    int ParsedRectCount,
    int UsedWidth,
    int UsedHeight,
    string Interpretation,
    string FullAtlasPath,
    string UsedAtlasPath,
    IReadOnlyList<VramUploadResult> Uploads);

internal sealed record VramUploadResult(
    int Index,
    VramPreviewPaths PreviewPaths,
    int DescriptorOffset,
    int VramWordX,
    int VramY,
    int VramWordWidth,
    int VramHeight,
    int Rgba5551Width,
    int Indexed8Width,
    int Indexed4Width,
    int UnknownWord0,
    int OutputSize,
    int StoredByteCount,
    bool IsCompressed);

internal sealed record VramPreviewPaths(
    string Rgba5551Path,
    string Indexed8Path,
    string Indexed4Path);

internal sealed class VramUpload
{
    private VramUpload()
    {
    }

    public int X { get; private init; }

    public int Y { get; private init; }

    public int Width { get; private init; }

    public int Height { get; private init; }

    public int UnknownWord0 { get; private init; }

    public int OutputSize { get; private init; }

    public int StoredByteCount { get; private init; }

    public int PayloadOffset { get; private init; }

    public bool IsCompressed { get; private init; }

    public int TotalByteCount => 8 + 12 + StoredByteCount;

    public static bool TryRead(ReadOnlySpan<byte> data, int descriptorOffset, out VramUpload? upload)
    {
        upload = null;
        if (descriptorOffset < 0 || descriptorOffset + 20 > data.Length)
        {
            return false;
        }

        int x = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(descriptorOffset + 0, sizeof(short)));
        int y = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(descriptorOffset + 2, sizeof(short)));
        int width = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(descriptorOffset + 4, sizeof(short)));
        int height = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(descriptorOffset + 6, sizeof(short)));
        if (x < 0 || y < 0 || width <= 0 || height <= 0)
        {
            return false;
        }

        if (x + width > VramExtractor.FullVramWidth || y + height > VramExtractor.FullVramHeight)
        {
            return false;
        }

        if (!EmbeddedChunk.TryRead(data, descriptorOffset + 8, out EmbeddedChunk? maybeChunk))
        {
            return false;
        }

        EmbeddedChunk chunk = maybeChunk!;
        int expectedOutputSize = width * height * sizeof(ushort);
        if (chunk.OutputSize != expectedOutputSize)
        {
            return false;
        }

        upload = new VramUpload
        {
            X = x,
            Y = y,
            Width = width,
            Height = height,
            UnknownWord0 = chunk.UnknownWord0,
            OutputSize = chunk.OutputSize,
            StoredByteCount = chunk.StoredByteCount,
            PayloadOffset = chunk.PayloadOffset,
            IsCompressed = chunk.IsCompressed,
        };

        return true;
    }
}
