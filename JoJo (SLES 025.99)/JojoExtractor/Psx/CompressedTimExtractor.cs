using System.Buffers.Binary;
using System.Globalization;
using JojoExtractor.Pac;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace JojoExtractor.Psx;

public sealed record PsxRect(int X, int Y, int WordWidth, int Height);

public sealed record CompressedTimInfo(
    int EntryIndex,
    ushort Opcode,
    uint CompressedLength,
    uint? RamDestination,
    int DecompressedLength,
    uint TimFlags,
    int BitsPerPixel,
    bool HasClut,
    PsxRect ImageRect,
    PsxRect? ClutRect,
    int ImagePixelWidth,
    int ImagePixelHeight,
    int ClutBankCount);

public sealed record CompressedTimExport(
    int EntryIndex,
    ushort Opcode,
    string TimPath,
    string ManifestPath,
    IReadOnlyList<string> PngPaths,
    CompressedTimInfo Info);

public sealed record EmbeddedTimInfo(
    int EntryIndex,
    int TimIndex,
    int TimCount,
    ushort Opcode,
    uint RawLength,
    int TimOffset,
    int TimLength,
    uint? RamDestination,
    uint TimFlags,
    int BitsPerPixel,
    bool HasClut,
    PsxRect ImageRect,
    PsxRect? ClutRect,
    int ImagePixelWidth,
    int ImagePixelHeight,
    int ClutBankCount);

public sealed record EmbeddedTimExport(
    int EntryIndex,
    ushort Opcode,
    string TimPath,
    string ManifestPath,
    IReadOnlyList<string> PngPaths,
    EmbeddedTimInfo Info);

public static class CompressedTimExtractor
{
    public const ushort CompressedTimOpcode = 0x0122;
    public const ushort SsOpenCompressedTimOpcode = 0x0123;

    private static readonly uint[] RamDestinationPointers =
    {
        0x80115800, 0x8010d800, 0x8010b800, 0x8010a000,
        0x800f3800, 0x80109000, 0x801f1e00, 0x801f2700,
        0x801f8700, 0x801f1800, 0x801f9700, 0x80116000,
        0x8010dc00, 0x80184000, 0x80186800, 0x8011d800,
        0x80118800, 0x801f9b00, 0x8012d800, 0x8012e800,
        0x80132800, 0x80133800, 0x80134800, 0x80117800,
        0x80119800, 0x80129800, 0x80185800, 0x80187800,
        0x80189800, 0x80199800, 0x8011e800, 0x80125800,
        0x8011d800, 0x8010c000, 0x80119800, 0x8011f800,
    };

    public static bool IsCompressedTimEntry(PacEntry entry)
    {
        ushort opcode = (ushort)(entry.Flags & 0xffff);
        return IsCompressedTimOpcode(opcode);
    }

    public static bool IsCompressedTimOpcode(ushort opcode)
    {
        return opcode is CompressedTimOpcode or SsOpenCompressedTimOpcode;
    }

    public static bool HasCompressedTimEntries(PacFile pac)
    {
        return pac.Entries.Any(IsCompressedTimEntry);
    }

    public static bool HasEmbeddedTimEntries(PacFile pac)
    {
        return pac.Entries.Any(entry => IsEmbeddedTimEntry(pac, entry));
    }

    public static bool IsEmbeddedTimEntry(PacFile pac, PacEntry entry)
    {
        if (IsCompressedTimEntry(entry))
            return false;

        return TryParseTim(pac.GetEntryData(entry), out _);
    }

    public static uint? GetDefaultRamDestination(ushort opcode)
    {
        if ((opcode & 0x0f00) != 0x0100)
            return null;

        int index = opcode & 0xff;
        return index >= 0 && index < RamDestinationPointers.Length
            ? RamDestinationPointers[index]
            : null;
    }

    public static CompressedTimInfo Analyze(PacFile pac, PacEntry entry)
    {
        if (!IsCompressedTimEntry(entry))
            throw new InvalidDataException($"Entry {entry.Index} opcode 0x{(ushort)(entry.Flags & 0xffff):X4} is not a code-backed compressed TIM payload.");

        byte[] decompressed = DecompressFun800267a8(pac.GetEntryData(entry));
        TimData tim = ParseTim(decompressed);
        return BuildInfo(entry, decompressed.Length, tim);
    }

    public static IReadOnlyList<CompressedTimExport> Export(PacFile pac, string pacPath, string outDir)
    {
        Directory.CreateDirectory(outDir);

        string baseName = Path.GetFileNameWithoutExtension(pacPath);
        var outputs = new List<CompressedTimExport>();
        foreach (PacEntry entry in pac.Entries.Where(IsCompressedTimEntry))
        {
            byte[] compressed = pac.GetEntryData(entry).ToArray();
            byte[] decompressed = DecompressFun800267a8(compressed);
            TimData tim = ParseTim(decompressed);
            CompressedTimInfo info = BuildInfo(entry, decompressed.Length, tim);

            string prefix = $"{baseName}_entry{entry.Index:D3}_op{info.Opcode:X4}";
            string timPath = Path.Combine(outDir, prefix + "_decompressed.tim");
            File.WriteAllBytes(timPath, decompressed);

            IReadOnlyList<string> pngPaths = SavePngs(tim, prefix, outDir);
            string manifestPath = Path.Combine(outDir, prefix + "_manifest.txt");
            File.WriteAllText(manifestPath, BuildManifest(pacPath, info, timPath, pngPaths));

            outputs.Add(new CompressedTimExport(entry.Index, info.Opcode, timPath, manifestPath, pngPaths, info));
        }

        return outputs;
    }

    public static IReadOnlyList<EmbeddedTimExport> ExportEmbedded(PacFile pac, string pacPath, string outDir)
    {
        Directory.CreateDirectory(outDir);

        string baseName = Path.GetFileNameWithoutExtension(pacPath);
        var outputs = new List<EmbeddedTimExport>();
        foreach (PacEntry entry in pac.Entries.Where(entry => IsEmbeddedTimEntry(pac, entry)))
        {
            byte[] timBytes = pac.GetEntryData(entry).ToArray();
            IReadOnlyList<TimData> tims = ParseTimSequence(timBytes);
            for (int timIndex = 0; timIndex < tims.Count; timIndex++)
            {
                TimData tim = tims[timIndex];
                EmbeddedTimInfo info = BuildEmbeddedInfo(entry, tim, timIndex, tims.Count);

                string prefix = tims.Count == 1
                    ? $"{baseName}_entry{entry.Index:D3}_op{info.Opcode:X4}"
                    : $"{baseName}_entry{entry.Index:D3}_op{info.Opcode:X4}_tim{timIndex:D3}";
                string timPath = Path.Combine(outDir, prefix + "_embedded.tim");
                File.WriteAllBytes(timPath, timBytes.AsSpan(tim.StartOffset, tim.TotalLength).ToArray());

                IReadOnlyList<string> pngPaths = SavePngs(tim, prefix, outDir);
                string manifestPath = Path.Combine(outDir, prefix + "_manifest.txt");
                File.WriteAllText(manifestPath, BuildEmbeddedManifest(pacPath, info, timPath, pngPaths));

                outputs.Add(new EmbeddedTimExport(entry.Index, info.Opcode, timPath, manifestPath, pngPaths, info));
            }
        }

        return outputs;
    }

    public static byte[] DecompressFun800267a8(ReadOnlySpan<byte> source, int maxOutputBytes = 0x800000)
    {
        var outputWords = new List<ushort>(Math.Min(maxOutputBytes / 2, Math.Max(0x1000, source.Length * 2)));
        int sourceOffset = 0;

        while (true)
        {
            ushort control = ReadUInt16(source, ref sourceOffset);
            for (int bit = 0; bit < 16; bit++)
            {
                bool isCopy = (control & (0x8000 >> bit)) != 0;
                if (!isCopy)
                {
                    outputWords.Add(ReadUInt16(source, ref sourceOffset));
                }
                else
                {
                    ushort token = ReadUInt16(source, ref sourceOffset);
                    int count = token >> 11;
                    int offsetWords = token;
                    if (count == 0)
                    {
                        count = ReadUInt16(source, ref sourceOffset);
                    }
                    else
                    {
                        offsetWords = token & 0x07ff;
                    }

                    if (offsetWords == 0 && count == 0)
                        return WordsToBytes(outputWords);

                    if (offsetWords <= 0 || offsetWords > outputWords.Count)
                        throw new InvalidDataException($"Invalid FUN_800267a8 back-reference offset {offsetWords} at source byte 0x{sourceOffset:X}.");

                    for (int i = 0; i < count; i++)
                    {
                        if (outputWords.Count * 2 >= maxOutputBytes)
                            throw new InvalidDataException($"FUN_800267a8 output exceeded 0x{maxOutputBytes:X} bytes.");

                        outputWords.Add(outputWords[outputWords.Count - offsetWords]);
                    }
                }

                if (outputWords.Count * 2 >= maxOutputBytes)
                    throw new InvalidDataException($"FUN_800267a8 output exceeded 0x{maxOutputBytes:X} bytes.");
            }
        }
    }

    private static CompressedTimInfo BuildInfo(PacEntry entry, int decompressedLength, TimData tim)
    {
        int imagePixelWidth = GetPixelWidth(tim.BitsPerPixel, tim.ImageRect.WordWidth);
        int clutBankCount = tim.ClutData is null
            ? 0
            : tim.BitsPerPixel switch
            {
                4 => tim.ClutData.Length / 32,
                8 => tim.ClutData.Length / 512,
                _ => 0,
            };

        ushort opcode = (ushort)(entry.Flags & 0xffff);
        return new CompressedTimInfo(
            entry.Index,
            opcode,
            entry.DataLength,
            GetDefaultRamDestination(opcode),
            decompressedLength,
            tim.Flags,
            tim.BitsPerPixel,
            tim.ClutData is not null,
            tim.ImageRect,
            tim.ClutRect,
            imagePixelWidth,
            tim.ImageRect.Height,
            clutBankCount);
    }

    private static EmbeddedTimInfo BuildEmbeddedInfo(PacEntry entry, TimData tim, int timIndex, int timCount)
    {
        int imagePixelWidth = GetPixelWidth(tim.BitsPerPixel, tim.ImageRect.WordWidth);
        int clutBankCount = tim.ClutData is null
            ? 0
            : tim.BitsPerPixel switch
            {
                4 => tim.ClutData.Length / 32,
                8 => tim.ClutData.Length / 512,
                _ => 0,
            };

        ushort opcode = (ushort)(entry.Flags & 0xffff);
        return new EmbeddedTimInfo(
            entry.Index,
            timIndex,
            timCount,
            opcode,
            entry.DataLength,
            tim.StartOffset,
            tim.TotalLength,
            GetDefaultRamDestination(opcode),
            tim.Flags,
            tim.BitsPerPixel,
            tim.ClutData is not null,
            tim.ImageRect,
            tim.ClutRect,
            imagePixelWidth,
            tim.ImageRect.Height,
            clutBankCount);
    }

    private static bool TryParseTim(ReadOnlySpan<byte> data, out TimData? tim)
    {
        tim = null;
        if (data.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(data[..4]) != 0x10)
            return false;

        try
        {
            tim = ParseTim(data.ToArray());
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static TimData ParseTim(byte[] data)
    {
        return ParseTimAt(data, 0);
    }

    private static IReadOnlyList<TimData> ParseTimSequence(byte[] data)
    {
        var tims = new List<TimData>();
        int? nextOffset = 0;

        while (nextOffset is int offset && offset < data.Length)
        {
            TimData tim = ParseTimAt(data, offset);
            tims.Add(tim);
            nextOffset = FindNextTimOffset(data, tim.StartOffset + tim.TotalLength);
        }

        if (tims.Count == 0)
            throw new InvalidDataException("Embedded TIM payload does not contain a valid TIM stream at offset 0.");

        return tims;
    }

    private static int? FindNextTimOffset(byte[] data, int searchOffset)
    {
        if (searchOffset >= data.Length || IsZeroPadding(data.AsSpan(searchOffset)))
            return null;

        int alignedOffset = (searchOffset + 3) & ~3;
        for (int offset = alignedOffset; offset <= data.Length - 20; offset += 4)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4)) != 0x10)
                continue;

            try
            {
                ParseTimAt(data, offset);
                return offset;
            }
            catch (InvalidDataException)
            {
            }
        }

        return null;
    }

    private static TimData ParseTimAt(byte[] data, int offset)
    {
        if (offset < 0 || data.Length - offset < 20)
            throw new InvalidDataException($"TIM stream at 0x{offset:X} is too short for buffer length 0x{data.Length:X}.");

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
        if (magic != 0x10)
            throw new InvalidDataException($"TIM stream at 0x{offset:X} does not start with magic 0x00000010 (got 0x{magic:X8}).");

        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4, 4));
        int pixelMode = (int)(flags & 0x07);
        int bitsPerPixel = pixelMode switch
        {
            0 => 4,
            1 => 8,
            2 => 16,
            3 => 24,
            _ => throw new InvalidDataException($"Unsupported TIM pixel mode {pixelMode} in flags 0x{flags:X8}.")
        };

        bool hasClut = (flags & 0x08) != 0;
        int blockOffset = offset + 8;
        TimBlock? clutBlock = null;
        if (hasClut)
        {
            clutBlock = ReadTimBlock(data, blockOffset, "CLUT");
            blockOffset = clutBlock.NextOffset;
        }

        TimBlock imageBlock = ReadTimBlock(data, blockOffset, "image");
        return new TimData(flags, bitsPerPixel, imageBlock.Rect, imageBlock.Data, clutBlock?.Rect, clutBlock?.Data, offset, imageBlock.NextOffset - offset);
    }

    private static bool IsZeroPadding(ReadOnlySpan<byte> data)
    {
        foreach (byte value in data)
        {
            if (value != 0)
                return false;
        }

        return true;
    }

    private static TimBlock ReadTimBlock(byte[] data, int offset, string name)
    {
        if (offset < 0 || offset + 12 > data.Length)
            throw new InvalidDataException($"TIM {name} block at 0x{offset:X} is outside decompressed buffer length 0x{data.Length:X}.");

        int blockLength = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
        if (blockLength < 12 || offset + blockLength > data.Length)
            throw new InvalidDataException($"TIM {name} block length 0x{blockLength:X} at 0x{offset:X} is invalid for buffer length 0x{data.Length:X}.");

        var rect = new PsxRect(
            BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset + 4, 2)),
            BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset + 6, 2)),
            BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset + 8, 2)),
            BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset + 10, 2)));

        byte[] blockData = data.AsSpan(offset + 12, blockLength - 12).ToArray();
        return new TimBlock(rect, blockData, offset + blockLength);
    }

    private static IReadOnlyList<string> SavePngs(TimData tim, string prefix, string outDir)
    {
        var paths = new List<string>();
        int width = GetPixelWidth(tim.BitsPerPixel, tim.ImageRect.WordWidth);
        ReadOnlySpan<byte> imageBytes = GetUploadedImageBytes(tim);

        switch (tim.BitsPerPixel)
        {
            case 4:
                if (tim.ClutData is null || tim.ClutData.Length < 32)
                {
                    using Image<Rgba32> gray = Render4BppGray(imageBytes, width, tim.ImageRect.Height);
                    paths.Add(SaveImage(gray, outDir, $"{prefix}_tim_4bpp_gray.png"));
                }
                else
                {
                    int banks = tim.ClutData.Length / 32;
                    for (int bank = 0; bank < banks; bank++)
                    {
                        byte[] clut = IndexedImageDecoder.GetClutBank(tim.ClutData, bank);
                        using Image<Rgba32> image = IndexedImageDecoder.Decode4bpp(imageBytes, width, clut);
                        paths.Add(SaveImage(image, outDir, $"{prefix}_tim_4bpp_bank{bank:D2}.png"));
                    }
                }
                break;

            case 8:
                if (tim.ClutData is null || tim.ClutData.Length < 512)
                {
                    using Image<Rgba32> gray = Render8BppGray(imageBytes, width, tim.ImageRect.Height);
                    paths.Add(SaveImage(gray, outDir, $"{prefix}_tim_8bpp_gray.png"));
                }
                else
                {
                    int cluts = tim.ClutData.Length / 512;
                    for (int clutIndex = 0; clutIndex < cluts; clutIndex++)
                    {
                        ReadOnlySpan<byte> clut = tim.ClutData.AsSpan(clutIndex * 512, 512);
                        using Image<Rgba32> image = IndexedImageDecoder.Decode8bpp(imageBytes, width, clut);
                        paths.Add(SaveImage(image, outDir, $"{prefix}_tim_8bpp_clut{clutIndex:D2}.png"));
                    }
                }
                break;

            case 16:
                using (Image<Rgba32> image = Render16Bpp(imageBytes, width, tim.ImageRect.Height))
                    paths.Add(SaveImage(image, outDir, $"{prefix}_tim_16bpp.png"));
                break;
        }

        return paths;
    }

    private static ReadOnlySpan<byte> GetUploadedImageBytes(TimData tim)
    {
        int expectedBytes = checked(tim.ImageRect.WordWidth * tim.ImageRect.Height * 2);
        if (tim.ImageData.Length < expectedBytes)
            throw new InvalidDataException($"TIM image block has 0x{tim.ImageData.Length:X} bytes, but RECT requires 0x{expectedBytes:X} bytes.");

        return tim.ImageData.AsSpan(0, expectedBytes);
    }

    private static int GetPixelWidth(int bitsPerPixel, int wordWidth)
    {
        return bitsPerPixel switch
        {
            4 => wordWidth * 4,
            8 => wordWidth * 2,
            16 => wordWidth,
            24 => wordWidth * 2 / 3,
            _ => throw new ArgumentOutOfRangeException(nameof(bitsPerPixel)),
        };
    }

    private static Image<Rgba32> Render4BppGray(ReadOnlySpan<byte> pixels, int width, int height)
    {
        var image = new Image<Rgba32>(width, height);
        int bytesPerRow = width / 2;
        for (int y = 0; y < height; y++)
        {
            int rowStart = y * bytesPerRow;
            for (int byteIndex = 0; byteIndex < bytesPerRow; byteIndex++)
            {
                byte value = pixels[rowStart + byteIndex];
                int x = byteIndex * 2;
                image[x, y] = Gray4(value & 0x0f);
                image[x + 1, y] = Gray4(value >> 4);
            }
        }

        return image;
    }

    private static Image<Rgba32> Render8BppGray(ReadOnlySpan<byte> pixels, int width, int height)
    {
        var image = new Image<Rgba32>(width, height);
        for (int y = 0; y < height; y++)
        {
            int rowStart = y * width;
            for (int x = 0; x < width; x++)
            {
                byte value = pixels[rowStart + x];
                image[x, y] = new Rgba32(value, value, value, 255);
            }
        }

        return image;
    }

    private static Image<Rgba32> Render16Bpp(ReadOnlySpan<byte> pixels, int width, int height)
    {
        var image = new Image<Rgba32>(width, height);
        for (int y = 0; y < height; y++)
        {
            int rowStart = y * width * 2;
            for (int x = 0; x < width; x++)
            {
                ushort raw = BinaryPrimitives.ReadUInt16LittleEndian(pixels.Slice(rowStart + x * 2, 2));
                var color = PsxColor.FromBgr15(raw);
                image[x, y] = new Rgba32(color.R, color.G, color.B, color.A);
            }
        }

        return image;
    }

    private static Rgba32 Gray4(int value)
    {
        byte gray = (byte)(value * 17);
        return new Rgba32(gray, gray, gray, 255);
    }

    private static string SaveImage(Image<Rgba32> image, string outDir, string fileName)
    {
        string path = Path.Combine(outDir, fileName);
        image.SaveAsPng(path);
        return path;
    }

    private static string BuildManifest(string pacPath, CompressedTimInfo info, string timPath, IReadOnlyList<string> pngPaths)
    {
        string destination = info.RamDestination is uint ramDestination
            ? $"0x{ramDestination:X8}"
            : "unknown";
        string clutRect = info.ClutRect is PsxRect rect
            ? FormatRect(rect)
            : "none";

        return string.Join(Environment.NewLine, new[]
        {
            $"source_pac={pacPath}",
            $"entry_index={info.EntryIndex.ToString(CultureInfo.InvariantCulture)}",
            $"opcode=0x{info.Opcode:X4}",
            $"compressed_length=0x{info.CompressedLength:X}",
            $"default_ram_destination={destination}",
            $"decompressed_tim_length=0x{info.DecompressedLength:X}",
            $"tim_flags=0x{info.TimFlags:X8}",
            $"bits_per_pixel={info.BitsPerPixel.ToString(CultureInfo.InvariantCulture)}",
            $"has_clut={info.HasClut.ToString(CultureInfo.InvariantCulture).ToLowerInvariant()}",
            $"clut_rect={clutRect}",
            $"clut_bank_count={info.ClutBankCount.ToString(CultureInfo.InvariantCulture)}",
            $"image_rect={FormatRect(info.ImageRect)}",
            $"image_pixels={info.ImagePixelWidth}x{info.ImagePixelHeight}",
            $"tim_output={timPath}",
            $"png_outputs={pngPaths.Count.ToString(CultureInfo.InvariantCulture)}",
            "code_evidence=FUN_800184c0 maps opcode class 0x0100 through PTR_DAT_8005988c. Opcode 0x0122 defaults to PTR_DAT_8005988c[0x22] == 0x80119800; opcode 0x0123 defaults to PTR_DAT_8005988c[0x23] == 0x8011F800. FUN_80025e64 calls FUN_800267a8(src, src+0x40000), then uploads the decompressed TIM image and optional CLUT blocks with LoadImage.",
            string.Empty
        }) + string.Join(Environment.NewLine, pngPaths.Select(path => $"png_output={path}")) + Environment.NewLine;
    }

    private static string BuildEmbeddedManifest(string pacPath, EmbeddedTimInfo info, string timPath, IReadOnlyList<string> pngPaths)
    {
        string destination = info.RamDestination is uint ramDestination
            ? $"0x{ramDestination:X8}"
            : "unknown";
        string clutRect = info.ClutRect is PsxRect rect
            ? FormatRect(rect)
            : "none";

        return string.Join(Environment.NewLine, new[]
        {
            $"source_pac={pacPath}",
            $"entry_index={info.EntryIndex.ToString(CultureInfo.InvariantCulture)}",
            $"tim_index={info.TimIndex.ToString(CultureInfo.InvariantCulture)}",
            $"tim_count={info.TimCount.ToString(CultureInfo.InvariantCulture)}",
            $"opcode=0x{info.Opcode:X4}",
            $"entry_raw_length=0x{info.RawLength:X}",
            $"tim_offset=0x{info.TimOffset:X}",
            $"tim_length=0x{info.TimLength:X}",
            $"default_ram_destination={destination}",
            $"tim_flags=0x{info.TimFlags:X8}",
            $"bits_per_pixel={info.BitsPerPixel.ToString(CultureInfo.InvariantCulture)}",
            $"has_clut={info.HasClut.ToString(CultureInfo.InvariantCulture).ToLowerInvariant()}",
            $"clut_rect={clutRect}",
            $"clut_bank_count={info.ClutBankCount.ToString(CultureInfo.InvariantCulture)}",
            $"image_rect={FormatRect(info.ImageRect)}",
            $"image_pixels={info.ImagePixelWidth}x{info.ImagePixelHeight}",
            $"tim_output={timPath}",
            $"png_outputs={pngPaths.Count.ToString(CultureInfo.InvariantCulture)}",
            "code_evidence=FUN_800184c0 maps opcode class 0x0100 through PTR_DAT_8005988c and copies these entries to their RAM destinations. This handler only decodes entries whose payload is already a complete PlayStation TIM stream with its own TIM image block and optional TIM CLUT block; no external CLUT association is inferred.",
            string.Empty
        }) + string.Join(Environment.NewLine, pngPaths.Select(path => $"png_output={path}")) + Environment.NewLine;
    }

    private static string FormatRect(PsxRect rect)
    {
        return $"x=0x{rect.X:X},y=0x{rect.Y:X},w=0x{rect.WordWidth:X},h=0x{rect.Height:X}";
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> source, ref int offset)
    {
        if (offset + 2 > source.Length)
            throw new InvalidDataException($"Unexpected end of FUN_800267a8 stream at byte 0x{offset:X}.");

        ushort value = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(offset, 2));
        offset += 2;
        return value;
    }

    private static byte[] WordsToBytes(IReadOnlyList<ushort> words)
    {
        byte[] bytes = new byte[words.Count * 2];
        for (int i = 0; i < words.Count; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2, 2), words[i]);

        return bytes;
    }

    private sealed record TimData(
        uint Flags,
        int BitsPerPixel,
        PsxRect ImageRect,
        byte[] ImageData,
        PsxRect? ClutRect,
        byte[]? ClutData,
        int StartOffset,
        int TotalLength);

    private sealed record TimBlock(PsxRect Rect, byte[] Data, int NextOffset);
}
