using System.Buffers.Binary;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

return Run(args);

static int Run(string[] args)
{
    ProbeOptions options;

    try
    {
        options = ParseOptions(args);
    }
    catch (ArgumentException ex)
    {
        Console.Error.WriteLine(ex.Message);
        PrintUsage();
        return 1;
    }

    if (options.InputPaths.Count == 0)
    {
        PrintUsage();
        return 1;
    }

    foreach (var path in options.InputPaths)
    {
        try
        {
            DumpSummary(path, options.ExportDirectory);
        }
        catch (Exception ex)
        {
            Console.WriteLine(Path.GetFileName(path));
            Console.WriteLine($"  error: {ex.Message}");
        }

        Console.WriteLine();
    }

    return 0;
}

static ProbeOptions ParseOptions(string[] args)
{
    string? exportDirectory = null;
    var inputPaths = new List<string>();

    for (var index = 0; index < args.Length; index++)
    {
        switch (args[index])
        {
            case "--export":
                if (index + 1 >= args.Length)
                {
                    throw new ArgumentException("Missing output directory after --export.");
                }

                exportDirectory = Path.GetFullPath(args[++index]);
                break;

            default:
                inputPaths.Add(args[index]);
                break;
        }
    }

    return new ProbeOptions(exportDirectory, inputPaths);
}

static void PrintUsage()
{
    Console.Error.WriteLine("Usage: dotnet run --project DOS/TimeSlaughter/TimeSlaughterVolProbe -- [--export <dir>] <VOL files...>");
}

static void DumpSummary(string path, string? exportDirectory)
{
    var data = File.ReadAllBytes(path);
    var kind = DetectKind(data);

    Console.WriteLine(Path.GetFileName(path));
    Console.WriteLine($"  kind: {kind}");
    Console.WriteLine($"  size: {data.Length}");

    switch (kind)
    {
        case VolKind.PaletteFirst:
        {
            var summary = ParseBackground(data);
            DumpBackgroundSummary(summary);

            if (exportDirectory is not null)
            {
                var exportedDirectory = ExportBackground(summary, path, exportDirectory);
                Console.WriteLine($"  exported: {exportedDirectory}");
            }

            break;
        }

        case VolKind.MidgetPower:
        {
            var summary = ParseMidgetPower(data);
            DumpMidgetPowerSummary(summary);

            if (exportDirectory is not null)
            {
                var exportedDirectory = ExportMidgetPower(summary, data, path, exportDirectory);
                Console.WriteLine($"  exported: {exportedDirectory}");
            }

            break;
        }

        default:
            Console.WriteLine($"  head32: {ToHex(data.AsSpan(0, Math.Min(32, data.Length)))}");

            if (exportDirectory is not null)
            {
                var exportedDirectory = ExportUnknown(data, path, exportDirectory);
                Console.WriteLine($"  exported: {exportedDirectory}");
            }

            break;
    }
}

static void DumpBackgroundSummary(BackgroundVolSummary summary)
{
    var slices = BuildBackgroundSliceBoundaries(summary.OffsetTable, summary.FirstData.Length);
    var recordEntries = ParseBackgroundRecordEntries(summary.RecordTable, summary.Blob35a);

    Console.WriteLine("  palette: 256 x 6-bit VGA triplets");
    Console.WriteLine($"  offsetTableCount: {summary.OffsetTable.Count} ({summary.NonZeroOffsetCount} nonzero)");
    Console.WriteLine($"  offsetMax: {summary.MaxOffset}");
    Console.WriteLine($"  firstDataSize: {summary.FirstDataSize}");
    Console.WriteLine($"  firstDataStart: 0x{summary.FirstDataStart:X}");
    Console.WriteLine($"  firstDataSliceCount: {slices.Count}");
    Console.WriteLine($"  postDataRelativeSkip: {summary.PostDataRelativeSkip}");
    Console.WriteLine($"  recordCount: {summary.RecordCount}");
    Console.WriteLine($"  activeRecordCount: {recordEntries.Count(entry => entry.IsActive)}");
    Console.WriteLine($"  recordTableStart: 0x{summary.RecordTableStart:X}");
    Console.WriteLine($"  recordTableBytes: {summary.RecordTableBytes}");
    Console.WriteLine($"  blob35aSize: {summary.Blob35aSize}");
    Console.WriteLine($"  fixedBlockSizes: {summary.FixedBlock04.Length}, {summary.FixedBlock08.Length}");
    Console.WriteLine($"  block0CSize: {summary.Block0CSize}");
    Console.WriteLine($"  block10Size: {summary.Block10Size}");
    Console.WriteLine($"  block14Size: {summary.Block14Size}");
    Console.WriteLine($"  block18Size: {summary.Block18Size}");
    Console.WriteLine($"  block1CSize: {summary.Block1CSize}");
    Console.WriteLine($"  trailingMidiBytes: {summary.TrailingMidi?.Data.Length ?? 0}");
    Console.WriteLine($"  consumedBytes: 0x{summary.ConsumedBytes:X}");
    Console.WriteLine($"  remainingBytes: {summary.RemainingBytes}");
}

static void DumpMidgetPowerSummary(MidgetPowerSummary summary)
{
    Console.WriteLine($"  magic: {summary.Magic}");
    Console.WriteLine($"  dword0x0C: {summary.HeaderDwordAt0C}");
    Console.WriteLine($"  candidatePaletteOffset: 0x{summary.CandidatePaletteOffset:X}");
    Console.WriteLine($"  candidatePaletteLooksVga: {summary.CandidatePaletteLooksVga}");
    if (summary.PrimarySpriteSet is not null)
    {
        Console.WriteLine($"  mainRemapOffset: 0x{summary.PrimarySpriteSet.MainRemapOffset:X}");
        Console.WriteLine($"  primaryReferencedEntryCount: {summary.PrimarySpriteSet.ReferencedEntryCount}");
        Console.WriteLine($"  primaryDecodedEntryCount: {summary.PrimarySpriteSet.Entries.Count}");
    }

    Console.WriteLine($"  tail16: {ToHex(summary.First16BytesAfterHeader)}");
}

static ReadOnlySpan<byte> MidgetPowerHeader() => "MIDGETPOWER\0"u8;

static VolKind DetectKind(ReadOnlySpan<byte> data)
{
    var header = MidgetPowerHeader();
    if (data.Length >= header.Length && data[..header.Length].SequenceEqual(header))
    {
        return VolKind.MidgetPower;
    }

    if (LooksLike6BitVgaPalette(data))
    {
        return VolKind.PaletteFirst;
    }

    return VolKind.Unknown;
}

static bool LooksLike6BitVgaPalette(ReadOnlySpan<byte> data)
{
    if (data.Length < 0x300)
    {
        return false;
    }

    for (var index = 0; index < 0x300; index++)
    {
        if (data[index] > 0x3F)
        {
            return false;
        }
    }

    return true;
}

static bool LooksLikeStandardMidi(ReadOnlySpan<byte> data)
{
    if (data.Length < 22)
    {
        return false;
    }

    if (!data[..4].SequenceEqual("MThd"u8))
    {
        return false;
    }

    var headerLength = ReadUInt32BigEndian(data, 4);
    if (headerLength != 6)
    {
        return false;
    }

    var trackHeaderOffset = checked(8 + (int)headerLength);
    if (trackHeaderOffset + 8 > data.Length)
    {
        return false;
    }

    if (!data.Slice(trackHeaderOffset, 4).SequenceEqual("MTrk"u8))
    {
        return false;
    }

    var trackLength = ReadUInt32BigEndian(data, trackHeaderOffset + 4);
    return trackHeaderOffset + 8 + trackLength <= data.Length;
}

static BackgroundVolSummary ParseBackground(ReadOnlySpan<byte> data)
{
    EnsureLength(data, 0x300 + 0x140 + 0xA0 + 4, "palette-first header");

    var palette6Bit = data[..0x300].ToArray();
    var offsetTable = new uint[0x50];
    for (var index = 0; index < offsetTable.Length; index++)
    {
        offsetTable[index] = ReadUInt32(data, 0x300 + index * 4);
    }

    var metadataA0 = data.Slice(0x300 + 0x140, 0xA0).ToArray();
    var position = 0x300 + 0x140 + 0xA0;
    var firstDataSize = ReadUInt32(data, position);
    var firstDataStart = checked(position + 4);
    EnsureLength(data, checked(firstDataStart + (int)firstDataSize), "first background blob");
    var firstData = data.Slice(firstDataStart, checked((int)firstDataSize)).ToArray();
    position = checked(firstDataStart + (int)firstDataSize);

    var postDataRelativeSkip = ReadUInt16(data, position);
    position += 2;
    EnsureLength(data, checked(position + postDataRelativeSkip), "post-data relative skip");
    position = checked(position + postDataRelativeSkip);

    var recordCount = checked(ReadUInt16(data, position) + 1);
    position += 2;

    var recordTableStart = position;
    var recordTableBytes = checked(recordCount * 10);
    EnsureLength(data, checked(position + recordTableBytes), "record table");
    var recordTable = data.Slice(position, recordTableBytes).ToArray();
    position = checked(position + recordTableBytes);

    var blob35aSize = ReadUInt16(data, position);
    var blob35aStart = checked(position + 2);
    EnsureLength(data, checked(blob35aStart + blob35aSize), "blob35a");
    var blob35a = data.Slice(blob35aStart, blob35aSize).ToArray();
    position = checked(blob35aStart + blob35aSize);

    EnsureLength(data, checked(position + 0xC8 * 2), "fixed 0xC8 blocks");
    var fixedBlock04 = data.Slice(position, 0xC8).ToArray();
    position += 0xC8;
    var fixedBlock08 = data.Slice(position, 0xC8).ToArray();
    position += 0xC8;

    var block0CWordCount = ReadUInt16(data, position);
    var block0CSize = checked(block0CWordCount * 2);
    var block0CStart = checked(position + 2);
    EnsureLength(data, checked(block0CStart + block0CSize), "block0C");
    var block0C = data.Slice(block0CStart, block0CSize).ToArray();
    position = checked(block0CStart + block0CSize);

    var block10 = ReadSizedBlock(data, ref position, out var block10Size, "block10");
    var block14 = ReadSizedBlock(data, ref position, out var block14Size, "block14");
    var block18 = ReadSizedBlock(data, ref position, out var block18Size, "block18");
    var block1C = ReadSizedBlock(data, ref position, out var block1CSize, "block1C");

    BackgroundMidiTrailer? trailingMidi = null;
    if (position + 2 <= data.Length)
    {
        var trailerSize = ReadUInt16(data, position);
        var trailerStart = checked(position + 2);

        if (trailerStart + trailerSize == data.Length)
        {
            var trailerData = data.Slice(trailerStart, trailerSize).ToArray();
            if (LooksLikeStandardMidi(trailerData))
            {
                trailingMidi = new BackgroundMidiTrailer(trailerSize, trailerData);
                position = checked(trailerStart + trailerSize);
            }
        }
    }

    if (position > data.Length)
    {
        throw new InvalidDataException("Recovered background walk runs past EOF.");
    }

    var nonZeroOffsetCount = offsetTable.Count(value => value != 0);
    var maxOffset = offsetTable.Max();
    var remainingTail = data[position..].ToArray();

    return new BackgroundVolSummary(
        OffsetTable: offsetTable,
        NonZeroOffsetCount: nonZeroOffsetCount,
        MaxOffset: maxOffset,
        Palette6Bit: palette6Bit,
        MetadataA0: metadataA0,
        FirstDataSize: firstDataSize,
        FirstDataStart: firstDataStart,
        FirstData: firstData,
        PostDataRelativeSkip: postDataRelativeSkip,
        RecordCount: recordCount,
        RecordTableStart: recordTableStart,
        RecordTableBytes: recordTableBytes,
        RecordTable: recordTable,
        Blob35aSize: blob35aSize,
        Blob35a: blob35a,
        FixedBlock04: fixedBlock04,
        FixedBlock08: fixedBlock08,
        Block0CSize: block0CSize,
        Block0C: block0C,
        Block10Size: block10Size,
        Block10: block10,
        Block14Size: block14Size,
        Block14: block14,
        Block18Size: block18Size,
        Block18: block18,
        Block1CSize: block1CSize,
        Block1C: block1C,
        TrailingMidi: trailingMidi,
        ConsumedBytes: position,
        RemainingBytes: data.Length - position,
        RemainingTail: remainingTail);
}

static MidgetPowerSummary ParseMidgetPower(ReadOnlySpan<byte> data)
{
    EnsureLength(data, 0x1C, "MIDGETPOWER header");

    var headerDwordAt0C = ReadUInt32(data, 0x0C);
    var first16BytesAfterHeader = data.Slice(0x0C, 16).ToArray();
    var candidatePaletteOffset = checked(0x10 + (int)headerDwordAt0C);
    var candidatePaletteLooksVga =
        candidatePaletteOffset >= 0 &&
        candidatePaletteOffset + 0x300 <= data.Length &&
        LooksLike6BitVgaPalette(data.Slice(candidatePaletteOffset, 0x300));
    var primarySpriteSet = TryParseMidgetPowerPrimarySpriteSet(data, out var recoveredPrimarySpriteSet)
        ? recoveredPrimarySpriteSet
        : null;

    return new MidgetPowerSummary(
        Magic: Encoding.ASCII.GetString(data[..11]),
        HeaderDwordAt0C: headerDwordAt0C,
        First16BytesAfterHeader: first16BytesAfterHeader,
        CandidatePaletteOffset: candidatePaletteOffset,
        CandidatePaletteLooksVga: candidatePaletteLooksVga,
        PrimarySpriteSet: primarySpriteSet);
}

static string ExportBackground(BackgroundVolSummary summary, string sourcePath, string exportRoot)
{
    var outputDirectory = PrepareExportDirectory(exportRoot, sourcePath);
    var stem = Path.GetFileNameWithoutExtension(sourcePath);
    var loaderSplitOverride = TryGetLoaderSplitOverride(stem);
    var slices = BuildBackgroundSliceBoundaries(summary.OffsetTable, summary.FirstData.Length);

    File.WriteAllText(
        Path.Combine(outputDirectory, "summary.txt"),
        BuildBackgroundExportSummary(summary, sourcePath, slices, loaderSplitOverride));
    File.WriteAllText(Path.Combine(outputDirectory, "offset_table.tsv"), BuildOffsetTableReport(summary.OffsetTable));
    File.WriteAllText(Path.Combine(outputDirectory, "first_data_slices.tsv"), BuildSliceReport(slices));
    File.WriteAllBytes(Path.Combine(outputDirectory, "palette_6bit.bin"), summary.Palette6Bit);
    File.WriteAllBytes(Path.Combine(outputDirectory, "palette_rgb24.bin"), ExpandVgaPalette(summary.Palette6Bit));
    File.WriteAllText(Path.Combine(outputDirectory, "palette.gpl"), BuildGplPalette(summary.Palette6Bit, stem));
    File.WriteAllBytes(Path.Combine(outputDirectory, "metadata_a0.bin"), summary.MetadataA0);
    File.WriteAllBytes(Path.Combine(outputDirectory, "first_data.bin"), summary.FirstData);
    File.WriteAllBytes(Path.Combine(outputDirectory, "record_table.bin"), summary.RecordTable);
    File.WriteAllBytes(Path.Combine(outputDirectory, "blob35a.bin"), summary.Blob35a);
    File.WriteAllText(Path.Combine(outputDirectory, "record_descriptors.txt"), BuildBackgroundRecordReport(summary));
    File.WriteAllBytes(Path.Combine(outputDirectory, "fixed_block_04.bin"), summary.FixedBlock04);
    File.WriteAllBytes(Path.Combine(outputDirectory, "fixed_block_08.bin"), summary.FixedBlock08);
    File.WriteAllBytes(Path.Combine(outputDirectory, "block_0c.bin"), summary.Block0C);
    File.WriteAllBytes(Path.Combine(outputDirectory, "block_10.bin"), summary.Block10);
    File.WriteAllBytes(Path.Combine(outputDirectory, "block_14.bin"), summary.Block14);
    File.WriteAllBytes(Path.Combine(outputDirectory, "block_18.bin"), summary.Block18);
    File.WriteAllBytes(Path.Combine(outputDirectory, "block_1c.bin"), summary.Block1C);

    if (summary.TrailingMidi is not null)
    {
        File.WriteAllBytes(Path.Combine(outputDirectory, "trailing_music.mid"), summary.TrailingMidi.Data);
    }

    if (summary.RemainingTail.Length > 0)
    {
        File.WriteAllBytes(Path.Combine(outputDirectory, "remaining_tail.bin"), summary.RemainingTail);
    }

    ExportDecodedBackgroundPlanes(summary, outputDirectory, loaderSplitOverride);

    var slicesDirectory = Path.Combine(outputDirectory, "first_data_slices");
    Directory.CreateDirectory(slicesDirectory);
    var decodedSlicesDirectory = Path.Combine(outputDirectory, "first_data_slices_png");
    Directory.CreateDirectory(decodedSlicesDirectory);

    foreach (var slice in slices)
    {
        var fileName = $"{slice.Ordinal:D3}_idx_{slice.PrimaryTableIndex:D2}_off_{slice.Offset:X6}_len_{slice.Length:X6}.bin";
        var sliceData = summary.FirstData.AsSpan(slice.Offset, slice.Length).ToArray();
        File.WriteAllBytes(Path.Combine(slicesDirectory, fileName), sliceData);

        if (TryDecodeFirstDataSlice(sliceData, out var decodedSlice))
        {
            SaveIndexedPng(
                Path.Combine(decodedSlicesDirectory, Path.GetFileNameWithoutExtension(fileName) + ".png"),
                new DecodedIndexedPlane(decodedSlice.Width, decodedSlice.Height, decodedSlice.Pixels),
                summary.Palette6Bit);
        }
    }

    ExportActiveRecordAssemblies(summary, outputDirectory);

    return outputDirectory;
}

static string ExportMidgetPower(MidgetPowerSummary summary, ReadOnlySpan<byte> data, string sourcePath, string exportRoot)
{
    var outputDirectory = PrepareExportDirectory(exportRoot, sourcePath);
    var stem = Path.GetFileNameWithoutExtension(sourcePath);
    var previewPalette6Bit = default(byte[]);
    var previewPaletteKind = string.Empty;

    if (summary.CandidatePaletteLooksVga)
    {
        previewPalette6Bit = data.Slice(summary.CandidatePaletteOffset, 0x300).ToArray();
        previewPaletteKind = "candidate-vga";
    }
    else if (summary.PrimarySpriteSet is not null &&
             TryLoadCanonicalMidgetPowerPreviewPalette(sourcePath, summary.PrimarySpriteSet, out var canonicalPalette6Bit, out var canonicalPaletteSource))
    {
        previewPalette6Bit = canonicalPalette6Bit;
        previewPaletteKind = $"stage-canonical:{canonicalPaletteSource}";
    }
    else
    {
        previewPalette6Bit = BuildGreyscalePalette6Bit();
        previewPaletteKind = "greyscale";
    }

    var primaryEntryCount = summary.PrimarySpriteSet is not null
        ? ExportMidgetPowerPrimarySpriteSet(summary.PrimarySpriteSet, outputDirectory, previewPalette6Bit)
        : 0;

    File.WriteAllText(
        Path.Combine(outputDirectory, "summary.txt"),
        BuildMidgetPowerExportSummary(summary, sourcePath, data.Length, primaryEntryCount, previewPaletteKind));
    File.WriteAllBytes(Path.Combine(outputDirectory, "header_100.bin"), data[..Math.Min(0x100, data.Length)].ToArray());
    File.WriteAllBytes(Path.Combine(outputDirectory, "preview_palette_6bit.bin"), previewPalette6Bit);
    File.WriteAllBytes(Path.Combine(outputDirectory, "preview_palette_rgb24.bin"), ExpandVgaPalette(previewPalette6Bit));
    File.WriteAllText(Path.Combine(outputDirectory, "preview_palette.gpl"), BuildGplPalette(previewPalette6Bit, stem + "-preview"));

    if (summary.CandidatePaletteLooksVga)
    {
        var palette = data.Slice(summary.CandidatePaletteOffset, 0x300).ToArray();
        File.WriteAllBytes(Path.Combine(outputDirectory, "candidate_palette_6bit.bin"), palette);
        File.WriteAllBytes(Path.Combine(outputDirectory, "candidate_palette_rgb24.bin"), ExpandVgaPalette(palette));
        File.WriteAllText(Path.Combine(outputDirectory, "candidate_palette.gpl"), BuildGplPalette(palette, stem + "-candidate"));
    }

    return outputDirectory;
}

static string ExportUnknown(ReadOnlySpan<byte> data, string sourcePath, string exportRoot)
{
    var outputDirectory = PrepareExportDirectory(exportRoot, sourcePath);
    var head32 = ToHex(data[..Math.Min(32, data.Length)]);
    var sliceScanCount = ExportEmbeddedSliceScan(data, outputDirectory, BuildGreyscalePalette6Bit());

    File.WriteAllText(
        Path.Combine(outputDirectory, "summary.txt"),
        BuildUnknownExportSummary(sourcePath, data.Length, head32, sliceScanCount));
    File.WriteAllBytes(Path.Combine(outputDirectory, "head32.bin"), data[..Math.Min(32, data.Length)].ToArray());

    return outputDirectory;
}

static string PrepareExportDirectory(string exportRoot, string sourcePath)
{
    var outputDirectory = Path.Combine(exportRoot, Path.GetFileNameWithoutExtension(sourcePath));

    if (Directory.Exists(outputDirectory))
    {
        Directory.Delete(outputDirectory, recursive: true);
    }

    Directory.CreateDirectory(outputDirectory);
    return outputDirectory;
}

static IReadOnlyList<BackgroundSliceBoundary> BuildBackgroundSliceBoundaries(IReadOnlyList<uint> offsetTable, int blobLength)
{
    var tableIndicesByOffset = new Dictionary<uint, List<int>>();

    for (var index = 0; index < offsetTable.Count; index++)
    {
        var offset = offsetTable[index];
        if (offset == 0)
        {
            continue;
        }

        if (!tableIndicesByOffset.TryGetValue(offset, out var indices))
        {
            indices = new List<int>();
            tableIndicesByOffset.Add(offset, indices);
        }

        indices.Add(index);
    }

    var orderedOffsets = tableIndicesByOffset.Keys.OrderBy(value => value).ToArray();
    var slices = new List<BackgroundSliceBoundary>(orderedOffsets.Length);

    for (var ordinal = 0; ordinal < orderedOffsets.Length; ordinal++)
    {
        var start = checked((int)orderedOffsets[ordinal]);
        if (start < 0 || start >= blobLength)
        {
            throw new InvalidDataException($"Slice offset 0x{start:X} falls outside the first background blob.");
        }

        var end = ordinal + 1 < orderedOffsets.Length
            ? checked((int)orderedOffsets[ordinal + 1])
            : blobLength;

        if (end < start || end > blobLength)
        {
            throw new InvalidDataException($"Slice boundary 0x{end:X} is invalid for a blob of size 0x{blobLength:X}.");
        }

        var tableIndices = tableIndicesByOffset[orderedOffsets[ordinal]].ToArray();
        slices.Add(new BackgroundSliceBoundary(
            Ordinal: ordinal,
            Offset: start,
            Length: end - start,
            TableIndices: tableIndices,
            PrimaryTableIndex: tableIndices[0]));
    }

    return slices;
}

static void ExportDecodedBackgroundPlanes(
    BackgroundVolSummary summary,
    string outputDirectory,
    int? loaderSplitOverride)
{
    TryExportDecodedBackgroundPlane(summary.Palette6Bit, summary.FixedBlock04, summary.Block10, outputDirectory, "decoded_block_10");
    TryExportDecodedBackgroundPlane(summary.Palette6Bit, summary.FixedBlock08, summary.Block14, outputDirectory, "decoded_block_14");
    TryExportDecodedBackgroundPlane(summary.Palette6Bit, summary.Block0C, summary.Block18, outputDirectory, "decoded_block_18");

    if (TryBuildZeroScrollComposite(summary, out var compositePreview))
    {
        File.WriteAllBytes(Path.Combine(outputDirectory, "composite_view_zero_scroll_indices.bin"), compositePreview.Pixels);
        SaveIndexedPng(Path.Combine(outputDirectory, "composite_view_zero_scroll.png"), compositePreview, summary.Palette6Bit);
    }

    if (TryBuildBattleInitScrollComposite(summary, out var battleInitPreview))
    {
        File.WriteAllBytes(
            Path.Combine(outputDirectory, "composite_view_battle_init_scroll_indices.bin"),
            battleInitPreview.Pixels);
        SaveIndexedPng(
            Path.Combine(outputDirectory, "composite_view_battle_init_scroll.png"),
            battleInitPreview,
            summary.Palette6Bit);
    }

    if (loaderSplitOverride is int splitY &&
        TryResolveCompositeSplitY(summary, null, out var encodedSplitY) &&
        splitY != encodedSplitY)
    {
        if (TryBuildZeroScrollComposite(summary, out var loaderSplitZeroPreview, splitY))
        {
            File.WriteAllBytes(
                Path.Combine(outputDirectory, "composite_view_zero_scroll_loader_split_indices.bin"),
                loaderSplitZeroPreview.Pixels);
            SaveIndexedPng(
                Path.Combine(outputDirectory, "composite_view_zero_scroll_loader_split.png"),
                loaderSplitZeroPreview,
                summary.Palette6Bit);
        }

        if (TryBuildBattleInitScrollComposite(summary, out var loaderSplitBattlePreview, splitY))
        {
            File.WriteAllBytes(
                Path.Combine(outputDirectory, "composite_view_battle_init_scroll_loader_split_indices.bin"),
                loaderSplitBattlePreview.Pixels);
            SaveIndexedPng(
                Path.Combine(outputDirectory, "composite_view_battle_init_scroll_loader_split.png"),
                loaderSplitBattlePreview,
                summary.Palette6Bit);
        }
    }
}

static void ExportActiveRecordAssemblies(BackgroundVolSummary summary, string outputDirectory)
{
    if (TryBuildActiveRecordPlacements(summary, out var placements))
    {
        File.WriteAllText(Path.Combine(outputDirectory, "active_record_layer_report.txt"), BuildActiveRecordLayerReport(placements));

        if (TryBuildRelativeActiveRecordLayer(placements, out var relativeLayer))
        {
            SaveIndexedPngWithCoverage(
                Path.Combine(outputDirectory, "active_record_layer_relative.png"),
                relativeLayer,
                summary.Palette6Bit);
        }

        if (TryBuildZeroScrollActiveRecordComposite(summary, placements, out var compositeWithRecords))
        {
            File.WriteAllBytes(
                Path.Combine(outputDirectory, "composite_view_zero_scroll_with_active_records_indices.bin"),
                compositeWithRecords.Pixels);
            SaveIndexedPng(
                Path.Combine(outputDirectory, "composite_view_zero_scroll_with_active_records.png"),
                compositeWithRecords,
                summary.Palette6Bit);
        }
    }
}

static bool TryBuildActiveRecordPlacements(
    BackgroundVolSummary summary,
    out List<ActiveRecordPlacement> placements)
{
    placements = new List<ActiveRecordPlacement>();

    var slices = BuildBackgroundSliceBoundaries(summary.OffsetTable, summary.FirstData.Length);
    var slicesByTableIndex = new Dictionary<int, BackgroundSliceBoundary>();
    foreach (var slice in slices)
    {
        foreach (var tableIndex in slice.TableIndices)
        {
            slicesByTableIndex[tableIndex] = slice;
        }
    }

    foreach (var entry in ParseBackgroundRecordEntries(summary.RecordTable, summary.Blob35a))
    {
        if (!entry.IsActive || entry.SliceIndex is null || entry.OffsetX is null || entry.OffsetY is null)
        {
            continue;
        }

        if (!slicesByTableIndex.TryGetValue(entry.SliceIndex.Value, out var sliceBoundary))
        {
            continue;
        }

        var sliceData = summary.FirstData.AsSpan(sliceBoundary.Offset, sliceBoundary.Length);
        if (!TryDecodeFirstDataSliceWithCoverage(sliceData, out var decodedSlice))
        {
            continue;
        }

        placements.Add(new ActiveRecordPlacement(
            RecordIndex: entry.Index,
            SliceIndex: entry.SliceIndex.Value,
            SliceOrdinal: sliceBoundary.Ordinal,
            X: entry.OffsetX.Value,
            Y: entry.OffsetY.Value,
            Slice: decodedSlice));
    }

    return placements.Count > 0;
}

static bool TryBuildRelativeActiveRecordLayer(
    IReadOnlyList<ActiveRecordPlacement> placements,
    out DecodedCoveredIndexedPlane plane)
{
    plane = default!;

    if (placements.Count == 0)
    {
        return false;
    }

    var minX = placements.Min(placement => placement.X);
    var minY = placements.Min(placement => placement.Y);
    var maxX = placements.Max(placement => placement.X + placement.Slice.Width);
    var maxY = placements.Max(placement => placement.Y + placement.Slice.Height);
    var width = maxX - minX;
    var height = maxY - minY;

    if (width <= 0 || height <= 0)
    {
        return false;
    }

    var pixels = new byte[checked(width * height)];
    var coverage = new bool[pixels.Length];

    foreach (var placement in placements)
    {
        BlitCoveredPlane(
            placement.Slice,
            pixels,
            coverage,
            width,
            height,
            placement.X - minX,
            placement.Y - minY);
    }

    plane = new DecodedCoveredIndexedPlane(width, height, pixels, coverage);
    return true;
}

static bool TryBuildZeroScrollActiveRecordComposite(
    BackgroundVolSummary summary,
    IReadOnlyList<ActiveRecordPlacement> placements,
    out DecodedIndexedPlane plane)
{
    plane = default!;

    if (!TryBuildZeroScrollComposite(summary, out var composite))
    {
        return false;
    }

    var pixels = composite.Pixels.ToArray();
    foreach (var placement in placements)
    {
        BlitCoveredPlaneOpaque(placement.Slice, pixels, composite.Width, composite.Height, placement.X, placement.Y);
    }

    plane = new DecodedIndexedPlane(composite.Width, composite.Height, pixels);
    return true;
}

static void BlitCoveredPlane(
    DecodedCoveredIndexedPlane source,
    byte[] destinationPixels,
    bool[] destinationCoverage,
    int destinationWidth,
    int destinationHeight,
    int destinationX,
    int destinationY)
{
    for (var sourceY = 0; sourceY < source.Height; sourceY++)
    {
        var targetY = destinationY + sourceY;
        if ((uint)targetY >= (uint)destinationHeight)
        {
            continue;
        }

        var sourceRowBase = sourceY * source.Width;
        var destinationRowBase = targetY * destinationWidth;

        for (var sourceX = 0; sourceX < source.Width; sourceX++)
        {
            var sourceIndex = sourceRowBase + sourceX;
            if (!source.Coverage[sourceIndex])
            {
                continue;
            }

            var targetX = destinationX + sourceX;
            if ((uint)targetX >= (uint)destinationWidth)
            {
                continue;
            }

            var destinationIndex = destinationRowBase + targetX;
            destinationPixels[destinationIndex] = source.Pixels[sourceIndex];
            destinationCoverage[destinationIndex] = true;
        }
    }
}

static void BlitCoveredPlaneOpaque(
    DecodedCoveredIndexedPlane source,
    byte[] destinationPixels,
    int destinationWidth,
    int destinationHeight,
    int destinationX,
    int destinationY)
{
    for (var sourceY = 0; sourceY < source.Height; sourceY++)
    {
        var targetY = destinationY + sourceY;
        if ((uint)targetY >= (uint)destinationHeight)
        {
            continue;
        }

        var sourceRowBase = sourceY * source.Width;
        var destinationRowBase = targetY * destinationWidth;

        for (var sourceX = 0; sourceX < source.Width; sourceX++)
        {
            var sourceIndex = sourceRowBase + sourceX;
            if (!source.Coverage[sourceIndex])
            {
                continue;
            }

            var targetX = destinationX + sourceX;
            if ((uint)targetX >= (uint)destinationWidth)
            {
                continue;
            }

            destinationPixels[destinationRowBase + targetX] = source.Pixels[sourceIndex];
        }
    }
}

static string BuildActiveRecordLayerReport(IReadOnlyList<ActiveRecordPlacement> placements)
{
    var builder = new StringBuilder();
    var minX = placements.Min(placement => placement.X);
    var minY = placements.Min(placement => placement.Y);
    var maxX = placements.Max(placement => placement.X + placement.Slice.Width);
    var maxY = placements.Max(placement => placement.Y + placement.Slice.Height);

    builder.AppendLine($"count={placements.Count}");
    builder.AppendLine($"bounds={minX},{minY} -> {maxX},{maxY}");
    builder.AppendLine("recordIndex sliceIndex sliceOrdinal x y width height normalizedX normalizedY");

    foreach (var placement in placements)
    {
        builder.Append(placement.RecordIndex);
        builder.Append(' ');
        builder.Append(placement.SliceIndex);
        builder.Append(' ');
        builder.Append(placement.SliceOrdinal);
        builder.Append(' ');
        builder.Append(placement.X);
        builder.Append(' ');
        builder.Append(placement.Y);
        builder.Append(' ');
        builder.Append(placement.Slice.Width);
        builder.Append(' ');
        builder.Append(placement.Slice.Height);
        builder.Append(' ');
        builder.Append(placement.X - minX);
        builder.Append(' ');
        builder.AppendLine((placement.Y - minY).ToString());
    }

    return builder.ToString();
}

static void TryExportDecodedBackgroundPlane(
    ReadOnlySpan<byte> palette6Bit,
    ReadOnlySpan<byte> rowOffsetTable,
    ReadOnlySpan<byte> encodedBlock,
    string outputDirectory,
    string stem)
{
    if (!TryDecodeLiteralSkipPlane(rowOffsetTable, encodedBlock, out var plane))
    {
        return;
    }

    File.WriteAllBytes(Path.Combine(outputDirectory, stem + "_indices.bin"), plane.Pixels);
    SaveIndexedPng(Path.Combine(outputDirectory, stem + ".png"), plane, palette6Bit);
}

static bool TryDecodeLiteralSkipPlane(
    ReadOnlySpan<byte> rowOffsetTable,
    ReadOnlySpan<byte> encodedBlock,
    out DecodedIndexedPlane plane)
{
    plane = default!;

    if (!TryReadPlaneHeader(encodedBlock, out var width, out var height))
    {
        return false;
    }

    if (rowOffsetTable.Length < checked(height * 2))
    {
        return false;
    }

    var pixels = new byte[checked(width * height)];

    for (var rowIndex = 0; rowIndex < height; rowIndex++)
    {
        var rowOffset = ReadUInt16(rowOffsetTable, rowIndex * 2);
        if (rowOffset < 4 || rowOffset >= encodedBlock.Length)
        {
            return false;
        }

        var sourceOffset = rowOffset;
        var destinationOffset = checked(rowIndex * width);
        var remaining = width;

        while (remaining > 0)
        {
            if (sourceOffset >= encodedBlock.Length)
            {
                return false;
            }

            var control = encodedBlock[sourceOffset++];

            if ((control & 0x80) != 0)
            {
                var skipLength = control & 0x7F;
                if (skipLength == 0 || skipLength > remaining)
                {
                    return false;
                }

                destinationOffset += skipLength;
                remaining -= skipLength;
                continue;
            }

            if (control == 0)
            {
                return false;
            }

            var literalLength = Math.Min(control, remaining);
            if (sourceOffset + literalLength > encodedBlock.Length)
            {
                return false;
            }

            encodedBlock.Slice(sourceOffset, literalLength).CopyTo(pixels.AsSpan(destinationOffset, literalLength));
            destinationOffset += literalLength;
            remaining -= literalLength;

            if (control > literalLength)
            {
                break;
            }

            sourceOffset += control;
        }
    }

    plane = new DecodedIndexedPlane(width, height, pixels);
    return true;
}

static bool TryBuildZeroScrollComposite(
    BackgroundVolSummary summary,
    out DecodedIndexedPlane plane,
    int? splitYOverride = null)
{
    return TryBuildComposite(summary, frontScrollX: 0, midScrollX: 0, backgroundScrollX: 0, splitYOverride, out plane);
}

static bool TryBuildBattleInitScrollComposite(
    BackgroundVolSummary summary,
    out DecodedIndexedPlane plane,
    int? splitYOverride = null)
{
    const int battleInitScroll = 200;

    var frontScrollX = (battleInitScroll * 8) / 10;
    var midScrollX = frontScrollX / 2;
    var backgroundScrollX = frontScrollX / 4;

    return TryBuildComposite(summary, frontScrollX, midScrollX, backgroundScrollX, splitYOverride, out plane);
}

static bool TryBuildComposite(
    BackgroundVolSummary summary,
    int frontScrollX,
    int midScrollX,
    int backgroundScrollX,
    int? splitYOverride,
    out DecodedIndexedPlane plane)
{
    const int viewportWidth = 320;
    const int viewportHeight = 200;

    plane = default!;

    if (summary.Block1C.Length < viewportWidth || summary.Block1C.Length % viewportWidth != 0)
    {
        return false;
    }

    if (!TryResolveCompositeSplitY(summary, null, out var defaultSplitY))
    {
        return false;
    }

    if (!TryResolveCompositeSplitY(summary, splitYOverride, out var effectiveSplitY))
    {
        return false;
    }

    var pixels = new byte[viewportWidth * viewportHeight];
    summary.Block1C.CopyTo(pixels, 0);

    if (!TryApplyMaskedCompositePass(
            sourceA: summary.Block10,
            rowTableA: summary.FixedBlock04,
            sourceB: summary.Block18,
            rowTableB: summary.Block0C,
            background: summary.Block1C,
            destination: pixels,
            leftClipA: frontScrollX,
            topClipA: 10,
            leftClipB: midScrollX,
            topClipB: 5,
            backgroundX: backgroundScrollX,
            backgroundY: 2,
            destX: 0,
            destY: 10))
    {
        return false;
    }

    if (!TryApplyMaskedCompositePass(
            sourceA: summary.Block14,
            rowTableA: summary.FixedBlock08,
            sourceB: summary.Block18,
            rowTableB: summary.Block0C,
            background: summary.Block1C,
            destination: pixels,
            leftClipA: frontScrollX,
            topClipA: 0,
            leftClipB: midScrollX,
            topClipB: 0x5F,
            backgroundX: backgroundScrollX,
            backgroundY: 0x5C,
            destX: 0,
            destY: 100))
    {
        return false;
    }

    ApplyTransparentLowerRows(summary, pixels, frontScrollX, defaultSplitY, effectiveSplitY);

    ApplyPerspectiveRows(summary, pixels, effectiveSplitY);

    plane = new DecodedIndexedPlane(viewportWidth, viewportHeight, pixels);
    return true;
}

static bool TryResolveCompositeSplitY(
    BackgroundVolSummary summary,
    int? splitYOverride,
    out int splitY)
{
    const int viewportHeight = 200;

    if (splitYOverride is int overrideSplitY)
    {
        splitY = overrideSplitY;
        return splitY > 100 && splitY < viewportHeight;
    }

    if (!TryReadPlaneHeader(summary.Block18, out _, out splitY))
    {
        return false;
    }

    return splitY > 100 && splitY < viewportHeight;
}

static void ApplyTransparentLowerRows(
    BackgroundVolSummary summary,
    byte[] destination,
    int sourceX,
    int defaultSplitY,
    int effectiveSplitY)
{
    const int viewportWidth = 320;
    const int viewportHeight = 200;

    if (!TryReadPlaneHeader(summary.Block14, out var sourceWidth, out var sourceHeight))
    {
        return;
    }

    if (defaultSplitY <= 100 || defaultSplitY >= viewportHeight)
    {
        return;
    }

    if (effectiveSplitY <= defaultSplitY)
    {
        return;
    }

    var clippedSourceHeight = Math.Min(sourceHeight, effectiveSplitY - 100);
    var sourceY = defaultSplitY - 100;
    if (sourceY < 0 || sourceY >= clippedSourceHeight)
    {
        return;
    }

    var visibleWidth = Math.Min(sourceWidth - sourceX, viewportWidth);
    var visibleHeight = Math.Min(clippedSourceHeight - sourceY, viewportHeight - defaultSplitY);
    if (visibleWidth <= 0 || visibleHeight <= 0)
    {
        return;
    }

    for (var rowIndex = 0; rowIndex < visibleHeight; rowIndex++)
    {
        if (!TryDecodeRleRowWithCoverage(summary.Block14, summary.FixedBlock08, sourceY + rowIndex, out var rowPixels, out var rowCoverage))
        {
            return;
        }

        var destinationRowBase = (defaultSplitY + rowIndex) * viewportWidth;
        for (var x = 0; x < visibleWidth; x++)
        {
            var sourceIndex = sourceX + x;
            if (sourceIndex < 0 || sourceIndex >= rowCoverage.Length || !rowCoverage[sourceIndex])
            {
                continue;
            }

            destination[destinationRowBase + x] = rowPixels[sourceIndex];
        }
    }
}

static void ApplyPerspectiveRows(BackgroundVolSummary summary, byte[] destination, int splitY)
{
    const int viewportWidth = 320;
    const int viewportHeight = 200;
    const int widenedRowWidth = 0x2F8;

    if (!TryReadPlaneHeader(summary.Block14, out _, out var block14Height))
    {
        return;
    }

    if (summary.Block1C.Length < viewportWidth || summary.Block1C.Length % viewportWidth != 0)
    {
        return;
    }

    var startRow = Math.Min(splitY, viewportHeight);
    var rowCount = Math.Max(0, block14Height + 100 - startRow);
    rowCount = Math.Min(rowCount, block14Height);
    rowCount = Math.Min(rowCount, viewportHeight - startRow);

    if (rowCount <= 0)
    {
        return;
    }

    var sourceStartRow = splitY - 100;
    if (sourceStartRow < 0 || sourceStartRow >= block14Height)
    {
        return;
    }

    if (!TryBuildPerspectiveRows(summary.FixedBlock08, summary.Block14, sourceStartRow, rowCount, out var widenedRows))
    {
        return;
    }

    for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
    {
        var shift = -TruncateTowardZero((160 * rowIndex), 170);
        var sourceStart = 0x3C + shift;
        if (sourceStart < 0 || sourceStart + viewportWidth > widenedRowWidth)
        {
            return;
        }

        var sourceOffset = rowIndex * widenedRowWidth + sourceStart;
        var destinationOffset = (startRow + rowIndex) * viewportWidth;
        widenedRows.AsSpan(sourceOffset, viewportWidth).CopyTo(destination.AsSpan(destinationOffset, viewportWidth));
    }
}

static bool TryBuildPerspectiveRows(
    ReadOnlySpan<byte> rowOffsetTable,
    ReadOnlySpan<byte> encodedBlock,
    int sourceStartRow,
    int rowCount,
    out byte[] widenedRows)
{
    const int rawRowWidth = 0x280;
    const int widenedRowWidth = 0x2F8;

    widenedRows = Array.Empty<byte>();

    if (!TryReadPlaneHeader(encodedBlock, out _, out var encodedHeight))
    {
        return false;
    }

    if (sourceStartRow < 0 || sourceStartRow >= encodedHeight)
    {
        return false;
    }

    rowCount = Math.Min(rowCount, encodedHeight - sourceStartRow);
    if (rowCount <= 0 || rowOffsetTable.Length < encodedHeight * 2)
    {
        return false;
    }

    widenedRows = new byte[rowCount * widenedRowWidth];
    var rawRow = new byte[rawRowWidth];

    for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
    {
        if (!TryDecodeDeltaRleRow(encodedBlock, rowOffsetTable, sourceStartRow + rowIndex, rawRow))
        {
            widenedRows = Array.Empty<byte>();
            return false;
        }

        var widen = TruncateTowardZero(rowIndex * 160, 170);
        var outputWidth = rawRowWidth + widen * 2;
        var destinationStart = 0x3C - widen;
        if (destinationStart < 0 || destinationStart + outputWidth > widenedRowWidth)
        {
            widenedRows = Array.Empty<byte>();
            return false;
        }

        var accumulator = 0;
        var sourceIndex = 0;
        var destinationOffset = rowIndex * widenedRowWidth + destinationStart;

        for (var x = 0; x < outputWidth; x++)
        {
            widenedRows[destinationOffset + x] = rawRow[sourceIndex];
            accumulator += rawRowWidth;
            sourceIndex += accumulator / outputWidth;
            accumulator %= outputWidth;

            if (sourceIndex >= rawRowWidth)
            {
                sourceIndex = rawRowWidth - 1;
            }
        }
    }

    return true;
}

static bool TryDecodeDeltaRleRow(
    ReadOnlySpan<byte> encodedBlock,
    ReadOnlySpan<byte> rowOffsetTable,
    int rowIndex,
    byte[] rowBuffer)
{
    const int rawRowWidth = 0x280;

    if (rowBuffer.Length != rawRowWidth)
    {
        return false;
    }

    if (!TryReadPlaneHeader(encodedBlock, out var width, out var height) || width != rawRowWidth)
    {
        return false;
    }

    if (rowIndex < 0 || rowIndex >= height || rowOffsetTable.Length < height * 2)
    {
        return false;
    }

    var rowOffset = ReadUInt16(rowOffsetTable, rowIndex * 2);
    if (rowOffset < 4 || rowOffset >= encodedBlock.Length)
    {
        return false;
    }

    var sourceOffset = rowOffset;
    var x = 0;

    while (x < rawRowWidth)
    {
        if (sourceOffset >= encodedBlock.Length)
        {
            return false;
        }

        var control = encodedBlock[sourceOffset++];
        if ((control & 0x80) != 0)
        {
            var skipLength = control & 0x7F;
            if (skipLength == 0)
            {
                return false;
            }

            x = Math.Min(rawRowWidth, x + skipLength);
            continue;
        }

        if (control == 0)
        {
            return false;
        }

        var literalLength = Math.Min(control, rawRowWidth - x);
        if (sourceOffset + literalLength > encodedBlock.Length)
        {
            return false;
        }

        encodedBlock.Slice(sourceOffset, literalLength).CopyTo(rowBuffer.AsSpan(x, literalLength));
        x += literalLength;

        if (control > literalLength)
        {
            break;
        }

        sourceOffset += control;
    }

    return true;
}

static int TruncateTowardZero(int numerator, int denominator)
{
    return numerator / denominator;
}

static bool TryApplyMaskedCompositePass(
    ReadOnlySpan<byte> sourceA,
    ReadOnlySpan<byte> rowTableA,
    ReadOnlySpan<byte> sourceB,
    ReadOnlySpan<byte> rowTableB,
    ReadOnlySpan<byte> background,
    byte[] destination,
    int leftClipA,
    int topClipA,
    int leftClipB,
    int topClipB,
    int backgroundX,
    int backgroundY,
    int destX,
    int destY)
{
    const int viewportWidth = 320;
    const int viewportHeight = 200;

    if (!TryReadPlaneHeader(sourceA, out var widthA, out var heightA))
    {
        return false;
    }

    if (!TryReadPlaneHeader(sourceB, out _, out var heightB))
    {
        return false;
    }

    if (background.Length < viewportWidth || background.Length % viewportWidth != 0 || destination.Length < viewportWidth * viewportHeight)
    {
        return false;
    }

    var backgroundHeight = background.Length / viewportWidth;

    var drawableWidth = Math.Min(widthA - leftClipA, viewportWidth - destX);
    var drawableRows = Math.Min(heightA - topClipA, heightB - destY);
    drawableRows = Math.Min(drawableRows, viewportHeight - destY);

    if (drawableWidth <= 0 || drawableRows <= 0)
    {
        return true;
    }

    for (var rowIndex = 0; rowIndex < drawableRows; rowIndex++)
    {
        var sourceRowA = topClipA + rowIndex;
        var sourceRowB = topClipB + rowIndex;
        var backgroundRow = backgroundY + rowIndex;
        var destinationRow = destY + rowIndex;

        if (sourceRowA < 0 || sourceRowB < 0 || destinationRow < 0)
        {
            return false;
        }

        if (destinationRow >= viewportHeight)
        {
            return false;
        }

        if (!TryDecodeRleRowWithCoverage(sourceA, rowTableA, sourceRowA, out var pixelsA, out var coverageA))
        {
            return false;
        }

        if (!TryDecodeRleRowWithCoverage(sourceB, rowTableB, sourceRowB, out var pixelsB, out var coverageB))
        {
            return false;
        }

        var destinationBase = destinationRow * viewportWidth + destX;
        var hasBackgroundRow = backgroundRow >= 0 && backgroundRow < backgroundHeight;
        var backgroundBase = hasBackgroundRow ? backgroundRow * viewportWidth : -1;

        if (destinationBase < 0)
        {
            return false;
        }

        if (destinationBase + drawableWidth > destination.Length)
        {
            return false;
        }

        if (hasBackgroundRow && (backgroundBase < 0 || backgroundBase + viewportWidth > background.Length))
        {
            return false;
        }

        for (var x = 0; x < drawableWidth; x++)
        {
            var indexA = leftClipA + x;
            var indexB = leftClipB + x;

            var hasA = indexA >= 0 && indexA < coverageA.Length && coverageA[indexA];
            var hasB = indexB >= 0 && indexB < coverageB.Length && coverageB[indexB];
            var fallback = hasBackgroundRow
                ? background[backgroundBase + ((backgroundX + x) % viewportWidth)]
                : destination[destinationBase + x];
            var output = hasA
                ? pixelsA[indexA]
                : hasB
                    ? pixelsB[indexB]
                    : fallback;

            destination[destinationBase + x] = output;
        }
    }

    return true;
}

static bool TryDecodeRleRowWithCoverage(
    ReadOnlySpan<byte> encodedBlock,
    ReadOnlySpan<byte> rowOffsetTable,
    int rowIndex,
    out byte[] pixels,
    out bool[] coverage)
{
    pixels = Array.Empty<byte>();
    coverage = Array.Empty<bool>();

    if (!TryReadPlaneHeader(encodedBlock, out var width, out var height))
    {
        return false;
    }

    if (rowIndex < 0 || rowIndex >= height || rowOffsetTable.Length < height * 2)
    {
        return false;
    }

    var rowOffset = ReadUInt16(rowOffsetTable, rowIndex * 2);
    if (rowOffset < 4 || rowOffset >= encodedBlock.Length)
    {
        return false;
    }

    pixels = new byte[width];
    coverage = new bool[width];

    var sourceOffset = rowOffset;
    var x = 0;

    while (x < width)
    {
        if (sourceOffset >= encodedBlock.Length)
        {
            return false;
        }

        var control = encodedBlock[sourceOffset++];
        if ((control & 0x80) != 0)
        {
            var skipLength = control & 0x7F;
            if (skipLength == 0)
            {
                return false;
            }

            x = Math.Min(width, x + skipLength);
            continue;
        }

        if (control == 0)
        {
            return false;
        }

        var literalLength = Math.Min(control, width - x);
        if (sourceOffset + literalLength > encodedBlock.Length)
        {
            return false;
        }

        for (var literalIndex = 0; literalIndex < literalLength; literalIndex++)
        {
            pixels[x + literalIndex] = encodedBlock[sourceOffset + literalIndex];
            coverage[x + literalIndex] = true;
        }

        x += literalLength;

        if (control > literalLength)
        {
            break;
        }

        sourceOffset += control;
    }

    return true;
}

static bool TryDecodeFirstDataSlice(ReadOnlySpan<byte> sliceData, out DecodedFirstDataSlice slice)
{
    slice = default!;

    if (!TryDecodeFirstDataSliceWithCoverage(sliceData, out var coveredSlice))
    {
        return false;
    }

    slice = new DecodedFirstDataSlice(
        coveredSlice.AnchorX,
        coveredSlice.Width,
        coveredSlice.Height,
        coveredSlice.Pixels);
    return true;
}

static bool TryDecodeFirstDataSliceWithCoverage(ReadOnlySpan<byte> sliceData, out DecodedCoveredFirstDataSlice slice)
{
    return TryDecodeIndexedSliceWithCoverage(sliceData, out slice, out _);
}

static int ExportEmbeddedSliceScan(
    ReadOnlySpan<byte> data,
    string outputDirectory,
    ReadOnlySpan<byte> palette6Bit,
    byte[]? remapTable = null)
{
    var entries = FindEmbeddedSliceEntries(data);
    File.WriteAllText(Path.Combine(outputDirectory, "slice_scan.tsv"), BuildEmbeddedSliceReport(entries));

    if (entries.Count == 0)
    {
        return 0;
    }

    var sliceDataDirectory = Path.Combine(outputDirectory, "slice_scan_bins");
    var pngDirectory = Path.Combine(outputDirectory, "slice_scan_png");
    Directory.CreateDirectory(sliceDataDirectory);
    Directory.CreateDirectory(pngDirectory);

    for (var index = 0; index < entries.Count; index++)
    {
        var entry = entries[index];
        var stem = $"{index:D4}_off_{entry.Offset:X6}_len_{entry.Length:X6}_ax_{entry.Slice.AnchorX:D3}_{entry.Slice.Width}x{entry.Slice.Height}";
        File.WriteAllBytes(Path.Combine(sliceDataDirectory, stem + ".bin"), data.Slice(entry.Offset, entry.Length).ToArray());
        File.WriteAllBytes(Path.Combine(sliceDataDirectory, stem + "_indices.bin"), entry.Slice.Pixels);
        SaveIndexedPngWithCoverage(Path.Combine(pngDirectory, stem + ".png"), entry.Slice, palette6Bit);
    }

    return entries.Count;
}

static int ExportMidgetPowerPrimarySpriteSet(
    MidgetPowerPrimarySpriteSet primarySpriteSet,
    string outputDirectory,
    ReadOnlySpan<byte> palette6Bit)
{
    File.WriteAllBytes(Path.Combine(outputDirectory, "secondary_name_encoded_xor8f.bin"), primarySpriteSet.EncodedNameBytes);
    File.WriteAllText(Path.Combine(outputDirectory, "secondary_name_offset.txt"), $"0x{primarySpriteSet.EncodedNameOffset:X}");
    File.WriteAllText(Path.Combine(outputDirectory, "secondary_name_decoded.txt"), primarySpriteSet.DecodedName);
    File.WriteAllBytes(Path.Combine(outputDirectory, "main_remap_256.bin"), primarySpriteSet.MainRemapTable);
    File.WriteAllText(Path.Combine(outputDirectory, "main_remap.tsv"), BuildByteLookupReport(primarySpriteSet.MainRemapTable));
    File.WriteAllText(Path.Combine(outputDirectory, "main_remap_offset.txt"), $"0x{primarySpriteSet.MainRemapOffset:X}");
    File.WriteAllText(Path.Combine(outputDirectory, "primary_offsets.tsv"), BuildUInt32LookupReport("offset", primarySpriteSet.Offsets));
    File.WriteAllText(Path.Combine(outputDirectory, "primary_lengths.tsv"), BuildUInt32LookupReport("length", primarySpriteSet.Lengths));
    File.WriteAllBytes(Path.Combine(outputDirectory, "primary_blob.bin"), primarySpriteSet.Blob);
    File.WriteAllText(Path.Combine(outputDirectory, "primary_entries.tsv"), BuildMidgetPowerPrimaryEntryReport(primarySpriteSet.Entries));

    var entriesDirectory = Path.Combine(outputDirectory, "primary_entries");
    var pngDirectory = Path.Combine(outputDirectory, "primary_entries_png");
    Directory.CreateDirectory(entriesDirectory);
    Directory.CreateDirectory(pngDirectory);

    foreach (var entry in primarySpriteSet.Entries)
    {
        var stem = $"{entry.Index:D3}_off_{entry.Offset:X6}_len_{entry.Length:X6}_ax_{entry.Slice.AnchorX:D3}_{entry.Slice.Width}x{entry.Slice.Height}";
        File.WriteAllBytes(
            Path.Combine(entriesDirectory, stem + ".bin"),
            primarySpriteSet.Blob.AsSpan(entry.Offset, entry.Length).ToArray());
        File.WriteAllBytes(Path.Combine(entriesDirectory, stem + "_indices.bin"), entry.Slice.Pixels);
        SaveIndexedPngWithCoverage(Path.Combine(pngDirectory, stem + ".png"), entry.Slice, palette6Bit);
    }

    // Export secondary blocks (14 FUN_0002d87c blocks after primary table)
    var totalSecondaryEntries = 0;
    foreach (var block in primarySpriteSet.SecondaryBlocks)
    {
        if (block.RawSize == 0)
        {
            continue;
        }

        var blockStem = $"secondary_{block.Index:D2}_off_{block.FileOffset:X6}_raw_{block.RawSize:X6}";
        var blockDirectory = Path.Combine(outputDirectory, blockStem);
        Directory.CreateDirectory(blockDirectory);

        File.WriteAllBytes(Path.Combine(blockDirectory, "raw.bin"), block.Data);

        var blockPngDirectory = Path.Combine(blockDirectory, "png");
        Directory.CreateDirectory(blockPngDirectory);

        for (var si = 0; si < block.Entries.Count; si++)
        {
            var se = block.Entries[si];
            var seStem = $"{si:D3}_off_{se.Offset:X4}_len_{se.Length:X4}_ax_{se.Slice.AnchorX:D3}_{se.Slice.Width}x{se.Slice.Height}";
            File.WriteAllBytes(Path.Combine(blockDirectory, seStem + ".bin"), block.Data.AsSpan(se.Offset, se.Length).ToArray());
            File.WriteAllBytes(Path.Combine(blockDirectory, seStem + "_indices.bin"), se.Slice.Pixels);
            SaveIndexedPngWithCoverage(Path.Combine(blockPngDirectory, seStem + ".png"), se.Slice, palette6Bit);
        }

        totalSecondaryEntries += block.Entries.Count;
    }

    Console.WriteLine($"  secondaryBlockCount: {primarySpriteSet.SecondaryBlocks.Count(b => b.RawSize != 0)}, totalSecondaryEntries: {totalSecondaryEntries}");

    // Export animation set
    var animSet = primarySpriteSet.AnimSet;
    if (animSet != null)
    {
        Console.WriteLine($"  animFrameCount: {animSet.FrameCount}");

        File.WriteAllBytes(Path.Combine(outputDirectory, "anim_blob5cd.bin"), animSet.Blob5CD);
        File.WriteAllBytes(Path.Combine(outputDirectory, "anim_lookup_edc.bin"), animSet.LookupTable256);
        File.WriteAllText(Path.Combine(outputDirectory, "anim_lookup_edc.tsv"), BuildByteLookupReport(animSet.LookupTable256));
        File.WriteAllBytes(Path.Combine(outputDirectory, "anim_blobed8.bin"), animSet.BlobED8);
        File.WriteAllText(Path.Combine(outputDirectory, "anim_frames.tsv"), BuildAnimFrameReport(animSet.Frames));

        // Export per-frame sprite with positioning overlay info
        var animPngDirectory = Path.Combine(outputDirectory, "anim_frames_png");
        Directory.CreateDirectory(animPngDirectory);
        foreach (var frame in animSet.Frames)
        {
            var slotIndex = (int)frame.SpriteSlot;
            var matchedEntry = primarySpriteSet.Entries.FirstOrDefault(e => e.Index == slotIndex);
            if (matchedEntry == null) continue;
            var stem = $"{frame.Index:D3}_slot_{slotIndex:D3}_dx_{(int)frame.OffsetX:+0;-#}_dy_{(int)frame.OffsetY:+0;-#}_fl_{frame.Flags:X2}";
            SaveIndexedPngWithCoverage(Path.Combine(animPngDirectory, stem + ".png"), matchedEntry.Slice, palette6Bit);
        }
    }

    return primarySpriteSet.Entries.Count;
}

static List<ScannedSliceEntry> FindEmbeddedSliceEntries(ReadOnlySpan<byte> data)
{
    var entries = new List<ScannedSliceEntry>();

    for (var offset = 0; offset <= data.Length - 6; offset++)
    {
        if (!LooksLikeEmbeddedSliceHeader(data, offset))
        {
            continue;
        }

        if (!TryDecodeIndexedSliceWithCoverage(data[offset..], out var slice, out var consumedLength))
        {
            continue;
        }

        entries.Add(new ScannedSliceEntry(offset, consumedLength, slice));
        offset += Math.Max(1, consumedLength) - 1;
    }

    return entries;
}

static bool LooksLikeEmbeddedSliceHeader(ReadOnlySpan<byte> data, int offset)
{
    if ((uint)offset >= (uint)data.Length || data.Length - offset < 6)
    {
        return false;
    }

    var width = data[offset + 1];
    var height = data[offset + 2];
    if (width == 0 || height == 0 || data[offset + 3] != 0xFF)
    {
        return false;
    }

    var tableLength = checked(4 + height * 2);
    if (data.Length - offset < tableLength)
    {
        return false;
    }

    return ReadUInt16(data, offset + 4) == tableLength;
}

static bool TryDecodeIndexedSliceWithCoverage(
    ReadOnlySpan<byte> sliceData,
    out DecodedCoveredFirstDataSlice slice,
    out int consumedLength)
{
    slice = default!;
    consumedLength = 0;

    if (sliceData.Length < 6 || sliceData[3] != 0xFF)
    {
        return false;
    }

    var anchorX = sliceData[0];
    var width = (int)sliceData[1];
    var height = (int)sliceData[2];
    if (width == 0 || height == 0)
    {
        return false;
    }

    var tableLength = checked(4 + height * 2);
    if (sliceData.Length < tableLength)
    {
        return false;
    }

    var firstRowOffset = ReadUInt16(sliceData, 4);
    if (firstRowOffset != tableLength)
    {
        return false;
    }

    var rowOffsets = sliceData.Slice(4, height * 2);
    var pixels = new byte[checked(width * height)];
    var coverage = new bool[pixels.Length];
    var previousRowOffset = tableLength;

    for (var rowIndex = 0; rowIndex < height; rowIndex++)
    {
        var rowOffset = ReadUInt16(rowOffsets, rowIndex * 2);
        if (rowOffset < tableLength || rowOffset >= sliceData.Length || rowOffset < previousRowOffset)
        {
            return false;
        }

        previousRowOffset = rowOffset;

        var sourceOffset = (int)rowOffset;
        var rowEnd = sourceOffset;
        var destinationOffset = checked(rowIndex * width);
        var remaining = width;

        while (remaining > 0)
        {
            if (sourceOffset >= sliceData.Length)
            {
                return false;
            }

            var control = sliceData[sourceOffset++];
            rowEnd = Math.Max(rowEnd, sourceOffset);
            if ((control & 0x80) != 0)
            {
                var skipLength = control & 0x7F;
                if (skipLength == 0 || skipLength > remaining)
                {
                    return false;
                }

                destinationOffset += skipLength;
                remaining -= skipLength;
                continue;
            }

            if (control == 0)
            {
                return false;
            }

            var literalLength = Math.Min(control, remaining);
            if (sourceOffset + literalLength > sliceData.Length)
            {
                return false;
            }

            for (var literalIndex = 0; literalIndex < literalLength; literalIndex++)
            {
                pixels[destinationOffset + literalIndex] = sliceData[sourceOffset + literalIndex];
                coverage[destinationOffset + literalIndex] = true;
            }

            rowEnd = Math.Max(rowEnd, sourceOffset + literalLength);
            destinationOffset += literalLength;
            remaining -= literalLength;

            if (control > literalLength)
            {
                break;
            }

            sourceOffset += control;
            rowEnd = Math.Max(rowEnd, sourceOffset);
        }

        consumedLength = Math.Max(consumedLength, rowEnd);
    }

    slice = new DecodedCoveredFirstDataSlice(anchorX, width, height, pixels, coverage);
    return consumedLength > 0;
}

static bool TryReadPlaneHeader(ReadOnlySpan<byte> encodedBlock, out int width, out int height)
{
    width = 0;
    height = 0;

    if (encodedBlock.Length < 4)
    {
        return false;
    }

    width = ReadUInt16(encodedBlock, 0);
    height = ReadUInt16(encodedBlock, 2);

    if (width <= 0 || height <= 0 || width > 4096 || height > 4096)
    {
        return false;
    }

    return true;
}

static void SaveIndexedPng(string path, DecodedIndexedPlane plane, ReadOnlySpan<byte> palette6Bit)
{
    var palette = BuildRgbaPalette(palette6Bit);

    using var image = new Image<Rgba32>(plane.Width, plane.Height);
    for (var y = 0; y < plane.Height; y++)
    {
        var source = plane.Pixels.AsSpan(y * plane.Width, plane.Width);

        for (var x = 0; x < source.Length; x++)
        {
            image[x, y] = palette[source[x]];
        }
    }

    image.Save(path);
}

static void SaveIndexedPngWithCoverage(string path, DecodedCoveredIndexedPlane plane, ReadOnlySpan<byte> palette6Bit)
{
    var palette = BuildRgbaPalette(palette6Bit);

    using var image = new Image<Rgba32>(plane.Width, plane.Height);
    for (var y = 0; y < plane.Height; y++)
    {
        var rowBase = y * plane.Width;

        for (var x = 0; x < plane.Width; x++)
        {
            var index = rowBase + x;
            image[x, y] = plane.Coverage[index]
                ? palette[plane.Pixels[index]]
                : new Rgba32(0, 0, 0, 0);
        }
    }

    image.Save(path);
}

static Rgba32[] BuildRgbaPalette(ReadOnlySpan<byte> palette6Bit)
{
    if (palette6Bit.Length < 0x300)
    {
        throw new InvalidDataException("Expected a 256-color VGA palette.");
    }

    var palette = new Rgba32[256];
    for (var index = 0; index < palette.Length; index++)
    {
        var baseOffset = index * 3;
        palette[index] = new Rgba32(
            (byte)((palette6Bit[baseOffset] * 255) / 63),
            (byte)((palette6Bit[baseOffset + 1] * 255) / 63),
            (byte)((palette6Bit[baseOffset + 2] * 255) / 63),
            index == 0 ? (byte)0 : byte.MaxValue);
    }

    return palette;
}

static byte[] BuildGreyscalePalette6Bit()
{
    var palette = new byte[0x300];
    for (var index = 0; index < 256; index++)
    {
        var value = (byte)((index * 63) / 255);
        var baseOffset = index * 3;
        palette[baseOffset] = value;
        palette[baseOffset + 1] = value;
        palette[baseOffset + 2] = value;
    }

    return palette;
}

static bool TryParseMidgetPowerPrimarySpriteSet(
    ReadOnlySpan<byte> data,
    out MidgetPowerPrimarySpriteSet primarySpriteSet)
{
    primarySpriteSet = default!;

    if (data.Length < 0x10)
    {
        return false;
    }

    var position = 0x0C;
    var firstBlockSize = ReadUInt32(data, position);
    position += 4;
    EnsureLength(data, checked(position + (int)firstBlockSize), "MIDGETPOWER primary block");
    position += (int)firstBlockSize;

    for (var index = 0; index < 4; index++)
    {
        var skip = ReadUInt32(data, position);
        position += 4;
        EnsureLength(data, checked(position + (int)skip), $"MIDGETPOWER skip table A[{index}]");
        position += (int)skip;
    }

    for (var index = 0; index < 6; index++)
    {
        var skip = ReadUInt32(data, position);
        position += 4;
        EnsureLength(data, checked(position + (int)skip), $"MIDGETPOWER skip table B[{index}]");
        position += (int)skip;
    }

    const int encodedNameLength = 0x0F;
    var encodedNameOffset = position;
    EnsureLength(data, checked(position + encodedNameLength), "MIDGETPOWER encoded secondary name");
    var encodedNameBytes = data.Slice(position, encodedNameLength).ToArray();
    position += encodedNameLength;

    var mainRemapOffset = position;
    EnsureLength(data, checked(position + 0x100), "MIDGETPOWER main remap table");
    var mainRemapTable = data.Slice(position, 0x100).ToArray();
    position += 0x100;

    var offsetTableOffset = position;
    EnsureLength(data, checked(position + 0x400), "MIDGETPOWER primary offset table");
    var offsets = new uint[0x100];
    for (var index = 0; index < offsets.Length; index++)
    {
        offsets[index] = ReadUInt32(data, position + index * 4);
    }

    position += 0x400;

    var lengthTableOffset = position;
    EnsureLength(data, checked(position + 0x400), "MIDGETPOWER primary length table");
    var lengths = new uint[0x100];
    for (var index = 0; index < lengths.Length; index++)
    {
        lengths[index] = ReadUInt32(data, position + index * 4);
    }

    position += 0x400;

    var blobSize = ReadUInt32(data, position);
    position += 4;
    EnsureLength(data, checked(position + (int)blobSize), "MIDGETPOWER primary blob");
    var blobOffset = position;
    var blob = data.Slice(position, (int)blobSize).ToArray();
    position += (int)blobSize;

    var entries = new List<MidgetPowerPrimaryEntry>();
    var referencedEntryCount = lengths.Count(length => length != 0);
    for (var index = 0; index < lengths.Length; index++)
    {
        if (lengths[index] == 0)
        {
            continue;
        }

        var entryOffset = checked((int)offsets[index]);
        var entryLength = checked((int)lengths[index]);
        if (entryOffset < 0 || entryLength <= 0 || entryOffset > blob.Length || entryOffset + entryLength > blob.Length)
        {
            continue;
        }

        if (!TryDecodeIndexedSliceWithCoverage(blob.AsSpan(entryOffset, entryLength), out var slice, out _))
        {
            continue;
        }

        entries.Add(new MidgetPowerPrimaryEntry(index, entryOffset, entryLength, slice));
    }

    // Parse 14 secondary FUN_0002d87c blocks (obj+0xbe5 table)
    var secondaryBlocks = new List<MidgetPowerSecondaryBlock>();
    for (var blockIndex = 0; blockIndex < 14; blockIndex++)
    {
        if (position + 4 > data.Length)
        {
            break;
        }

        var blockRawSize = ReadUInt32(data, position);
        position += 4;

        if (blockRawSize == 0)
        {
            secondaryBlocks.Add(new MidgetPowerSecondaryBlock(blockIndex, position - 4, 0, Array.Empty<byte>(), Array.Empty<ScannedSliceEntry>()));
            continue;
        }

        if (position + (int)blockRawSize > data.Length)
        {
            break;
        }

        var blockFileOffset = position;
        var blockData = data.Slice(position, (int)blockRawSize).ToArray();
        position += (int)blockRawSize;

        // Effective data is blockRawSize - 0x1E bytes (as per FUN_0002d87c)
        var effectiveLength = (int)blockRawSize >= 0x1E ? (int)blockRawSize - 0x1E : (int)blockRawSize;
        var blockEntries = FindEmbeddedSliceEntries(blockData.AsSpan(0, effectiveLength));
        secondaryBlocks.Add(new MidgetPowerSecondaryBlock(blockIndex, blockFileOffset, blockRawSize, blockData, blockEntries));
    }

    primarySpriteSet = new MidgetPowerPrimarySpriteSet(
        EncodedNameOffset: encodedNameOffset,
        EncodedNameBytes: encodedNameBytes,
        DecodedName: DecodeMidgetPowerXor8fString(encodedNameBytes),
        MainRemapOffset: mainRemapOffset,
        MainRemapTable: mainRemapTable,
        OffsetTableOffset: offsetTableOffset,
        Offsets: offsets,
        LengthTableOffset: lengthTableOffset,
        Lengths: lengths,
        BlobOffset: blobOffset,
        BlobSize: blobSize,
        Blob: blob,
        ReferencedEntryCount: referencedEntryCount,
        Entries: entries,
        SecondaryBlocks: secondaryBlocks,
        AnimSet: TryParseAnimSet(data, position, out var animSet) ? animSet : null);

    return true;
}

// Parses the animation data block that follows the 14 secondary FUN_0002d87c blocks.
// Layout (confirmed from disassembly at 0x1fe93..0x20010):
//   u16 dataLen1                → allocate blob at obj+0x5CD, read dataLen1 bytes
//   u16 animCount               → stored at obj+0xED6 (low byte is loop count)
//   animCount × 0x32 bytes      → animation frame records at obj+0xC55 (stride 64 in object)
//   0x100 bytes                 → lookup table at obj+0xEDC
//   u16 dataLen2                → allocate blob at obj+0xED8, read dataLen2 bytes
//   (4 bytes stack-local, ignored — terminator or checksum)
static bool TryParseAnimSet(ReadOnlySpan<byte> data, int position, out MidgetPowerAnimSet animSet)
{
    animSet = default!;
    if (position + 2 > data.Length) return false;

    var setFileOffset = position;

    // u16 dataLen1 → obj+0x5CD blob
    var dataLen1 = (int)ReadUInt16(data, position); position += 2;
    if (position + dataLen1 > data.Length) return false;
    var blob5CD = data.Slice(position, dataLen1).ToArray(); position += dataLen1;

    // u16 animCount → obj+0xED6 (use low byte as loop count)
    if (position + 2 > data.Length) return false;
    var animCount = (int)(data[position] & 0xFF); position += 2;   // low byte is the active count

    // animCount × 0x32 bytes → animation frame records (stride 64 in object, only 0x32 written)
    const int FrameBytes = 0x32;
    var frames = new List<MidgetPowerAnimFrame>();
    for (var fi = 0; fi < animCount; fi++)
    {
        if (position + FrameBytes > data.Length) break;
        var frameOffset = position;
        var raw = data.Slice(position, FrameBytes).ToArray();
        frames.Add(new MidgetPowerAnimFrame(fi, frameOffset, raw));
        position += FrameBytes;
    }

    // 0x100 bytes → lookup table at obj+0xEDC
    if (position + 0x100 > data.Length) return false;
    var lookupTable = data.Slice(position, 0x100).ToArray(); position += 0x100;

    // u16 dataLen2 → obj+0xED8 blob
    if (position + 2 > data.Length) return false;
    var dataLen2 = (int)ReadUInt16(data, position); position += 2;
    if (position + dataLen2 > data.Length) return false;
    var blobED8 = data.Slice(position, dataLen2).ToArray(); position += dataLen2;

    animSet = new MidgetPowerAnimSet(setFileOffset, animCount, frames, lookupTable, blob5CD, blobED8);
    return true;
}

static bool TryLoadCanonicalMidgetPowerPreviewPalette(
    string sourcePath,
    MidgetPowerPrimarySpriteSet primarySpriteSet,
    out byte[] palette6Bit,
    out string paletteSource)
{
    palette6Bit = Array.Empty<byte>();
    paletteSource = string.Empty;

    var requiredIndices = CollectCoveredIndices(primarySpriteSet.Entries);
    if (requiredIndices.Count == 0)
    {
        return false;
    }

    var sourceDirectory = Path.GetDirectoryName(sourcePath);
    if (sourceDirectory is null || !Directory.Exists(sourceDirectory))
    {
        return false;
    }

    var paletteCandidates = new List<(string Stem, byte[] Palette)>();
    foreach (var path in Directory.EnumerateFiles(sourceDirectory, "*.VOL").OrderBy(Path.GetFileName))
    {
        var bytes = File.ReadAllBytes(path);
        if (!LooksLike6BitVgaPalette(bytes))
        {
            continue;
        }

        paletteCandidates.Add((Path.GetFileNameWithoutExtension(path), bytes[..0x300].ToArray()));
    }

    if (paletteCandidates.Count == 0)
    {
        return false;
    }

    var targets = requiredIndices.OrderBy(value => value).ToArray();
    var candidatePalette = paletteCandidates[0].Palette;

    foreach (var target in targets)
    {
        var baseOffset = target * 3;
        foreach (var otherPalette in paletteCandidates.Skip(1))
        {
            if (!otherPalette.Palette.AsSpan(baseOffset, 3).SequenceEqual(candidatePalette.AsSpan(baseOffset, 3)))
            {
                return false;
            }
        }
    }

    palette6Bit = candidatePalette;
    paletteSource = paletteCandidates[0].Stem;
    return true;
}

static HashSet<byte> CollectCoveredIndices(IReadOnlyList<MidgetPowerPrimaryEntry> entries)
{
    var usedIndices = new HashSet<byte>();

    foreach (var entry in entries)
    {
        for (var index = 0; index < entry.Slice.Pixels.Length; index++)
        {
            if (!entry.Slice.Coverage[index])
            {
                continue;
            }

            usedIndices.Add(entry.Slice.Pixels[index]);
        }
    }

    return usedIndices;
}

static string DecodeMidgetPowerXor8fString(ReadOnlySpan<byte> encodedBytes)
{
    var decodedBytes = new List<byte>(encodedBytes.Length);
    foreach (var value in encodedBytes)
    {
        if (value == 0)
        {
            break;
        }

        decodedBytes.Add((byte)(value ^ 0x8F));
    }

    return Encoding.ASCII.GetString(decodedBytes.ToArray());
}

static byte[] ReadSizedBlock(ReadOnlySpan<byte> data, ref int position, out ushort size, string context)
{
    size = ReadUInt16(data, position);
    var blockStart = checked(position + 2);
    EnsureLength(data, checked(blockStart + size), context);
    var block = data.Slice(blockStart, size).ToArray();
    position = checked(blockStart + size);
    return block;
}

static byte[] ExpandVgaPalette(ReadOnlySpan<byte> palette6Bit)
{
    if (palette6Bit.Length % 3 != 0)
    {
        throw new InvalidDataException("Palette data length must be a multiple of 3.");
    }

    var rgb24 = new byte[palette6Bit.Length];
    for (var index = 0; index < palette6Bit.Length; index++)
    {
        rgb24[index] = (byte)((palette6Bit[index] * 255) / 63);
    }

    return rgb24;
}

static string BuildGplPalette(ReadOnlySpan<byte> palette6Bit, string name)
{
    var rgb24 = ExpandVgaPalette(palette6Bit);
    var builder = new StringBuilder();
    builder.AppendLine("GIMP Palette");
    builder.AppendLine($"Name: {name}");
    builder.AppendLine("Columns: 16");
    builder.AppendLine("#");

    for (var index = 0; index < rgb24.Length / 3; index++)
    {
        var baseOffset = index * 3;
        builder.Append(rgb24[baseOffset].ToString().PadLeft(3));
        builder.Append(' ');
        builder.Append(rgb24[baseOffset + 1].ToString().PadLeft(3));
        builder.Append(' ');
        builder.Append(rgb24[baseOffset + 2].ToString().PadLeft(3));
        builder.Append(' ');
        builder.AppendLine($"Index {index:D3}");
    }

    return builder.ToString();
}

static string BuildBackgroundExportSummary(
    BackgroundVolSummary summary,
    string sourcePath,
    IReadOnlyList<BackgroundSliceBoundary> slices,
    int? loaderSplitOverride)
{
    var recordEntries = ParseBackgroundRecordEntries(summary.RecordTable, summary.Blob35a);
    var builder = new StringBuilder();
    builder.AppendLine($"source={sourcePath}");
    builder.AppendLine("kind=PaletteFirst");
    builder.AppendLine($"paletteBytes={summary.Palette6Bit.Length}");
    builder.AppendLine($"offsetTableCount={summary.OffsetTable.Count}");
    builder.AppendLine($"offsetTableNonZero={summary.NonZeroOffsetCount}");
    builder.AppendLine($"offsetMax={summary.MaxOffset}");
    builder.AppendLine($"firstDataSize={summary.FirstDataSize}");
    builder.AppendLine($"firstDataStart=0x{summary.FirstDataStart:X}");
    builder.AppendLine($"firstDataSliceCount={slices.Count}");
    builder.AppendLine($"postDataRelativeSkip={summary.PostDataRelativeSkip}");
    builder.AppendLine($"recordCount={summary.RecordCount}");
    builder.AppendLine($"activeRecordCount={recordEntries.Count(entry => entry.IsActive)}");
    builder.AppendLine($"recordTableStart=0x{summary.RecordTableStart:X}");
    builder.AppendLine($"recordTableBytes={summary.RecordTableBytes}");
    builder.AppendLine($"blob35aSize={summary.Blob35aSize}");
    builder.AppendLine($"fixedBlock04Bytes={summary.FixedBlock04.Length}");
    builder.AppendLine($"fixedBlock08Bytes={summary.FixedBlock08.Length}");
    builder.AppendLine($"block0CBytes={summary.Block0CSize}");
    builder.AppendLine($"block10Bytes={summary.Block10Size}");
    builder.AppendLine($"block14Bytes={summary.Block14Size}");
    builder.AppendLine($"block18Bytes={summary.Block18Size}");
    builder.AppendLine($"block1CBytes={summary.Block1CSize}");
    if (TryResolveCompositeSplitY(summary, null, out var encodedSplitY))
    {
        builder.AppendLine($"encodedSplitY={encodedSplitY}");
    }

    if (loaderSplitOverride is int splitY)
    {
        builder.AppendLine($"loaderSplitOverride={splitY}");
    }

    builder.AppendLine($"trailingMidiBytes={summary.TrailingMidi?.Data.Length ?? 0}");
    builder.AppendLine($"consumedBytes=0x{summary.ConsumedBytes:X}");
    builder.AppendLine($"remainingBytes={summary.RemainingBytes}");
    return builder.ToString();
}

static int? TryGetLoaderSplitOverride(string stem)
{
    return stem.ToUpperInvariant() switch
    {
        "BG1" => 0xA0,
        "BG2" => 0xA0,
        "BG3" => 0x8C,
        "BG4" => 0x8C,
        "BG5" => 0xA0,
        "BG6" => 0x96,
        "BG7" => 0xA0,
        "BG8" => 0x9E,
        "BG9" => 0x96,
        "BG10" => 0xA0,
        _ => null,
    };
}

static string BuildBackgroundRecordReport(BackgroundVolSummary summary)
{
    var entries = ParseBackgroundRecordEntries(summary.RecordTable, summary.Blob35a);
    var builder = new StringBuilder();
    builder.AppendLine("index blobOffset flag2 flag3 active desc2 sliceIndex xOffset yOffset descHead");

    foreach (var entry in entries)
    {
        builder.Append(entry.Index.ToString("D2"));
        builder.Append(' ');
        builder.Append($"0x{entry.BlobOffset:X4}");
        builder.Append(' ');
        builder.Append($"0x{entry.Flag2:X2}");
        builder.Append(' ');
        builder.Append($"0x{entry.Flag3:X2}");
        builder.Append(' ');
        builder.Append(entry.IsActive ? "yes" : "no");
        builder.Append(' ');
        builder.Append(entry.DescriptorByte2 is null ? "--" : $"0x{entry.DescriptorByte2.Value:X2}");
        builder.Append(' ');
        builder.Append(entry.SliceIndex is null ? "--" : $"0x{entry.SliceIndex.Value:X2}");
        builder.Append(' ');
        builder.Append(entry.OffsetX?.ToString() ?? "--");
        builder.Append(' ');
        builder.Append(entry.OffsetY?.ToString() ?? "--");
        builder.Append(' ');
        builder.AppendLine(entry.DescriptorHeadHex);
    }

    return builder.ToString();
}

static List<BackgroundRecordEntry> ParseBackgroundRecordEntries(byte[] recordTable, byte[] blob35a)
{
    var entries = new List<BackgroundRecordEntry>(recordTable.Length / 10);

    for (var offset = 0; offset + 10 <= recordTable.Length; offset += 10)
    {
        var index = offset / 10;
        var blobOffset = ReadUInt16(recordTable, offset);
        var flag2 = recordTable[offset + 2];
        var flag3 = recordTable[offset + 3];
        var isActive = flag2 != 0 && flag3 != 0;

        byte? descriptorByte2 = null;
        byte? sliceIndex = null;
        short? offsetX = null;
        short? offsetY = null;
        var descriptorHeadHex = "--";

        if (blobOffset < blob35a.Length)
        {
            var descriptor = blob35a.AsSpan(blobOffset);
            descriptorHeadHex = ToHex(descriptor[..Math.Min(12, descriptor.Length)]);

            if (descriptor.Length >= 3)
            {
                descriptorByte2 = descriptor[2];
            }

            if (descriptor.Length >= 5)
            {
                sliceIndex = descriptor[4];
            }

            if (descriptor.Length >= 7)
            {
                offsetX = unchecked((short)ReadUInt16(descriptor, 5));
            }

            if (descriptor.Length >= 9)
            {
                offsetY = unchecked((short)ReadUInt16(descriptor, 7));
            }
        }

        entries.Add(new BackgroundRecordEntry(
            Index: index,
            BlobOffset: blobOffset,
            Flag2: flag2,
            Flag3: flag3,
            IsActive: isActive,
            DescriptorByte2: descriptorByte2,
            SliceIndex: sliceIndex,
            OffsetX: offsetX,
            OffsetY: offsetY,
            DescriptorHeadHex: descriptorHeadHex));
    }

    return entries;
}

static string BuildMidgetPowerExportSummary(
    MidgetPowerSummary summary,
    string sourcePath,
    int fileSize,
    int primaryEntryCount,
    string previewPaletteKind)
{
    var builder = new StringBuilder();
    builder.AppendLine($"source={sourcePath}");
    builder.AppendLine($"kind={VolKind.MidgetPower}");
    builder.AppendLine($"size={fileSize}");
    builder.AppendLine($"magic={summary.Magic}");
    builder.AppendLine($"dword0x0C={summary.HeaderDwordAt0C}");
    builder.AppendLine($"candidatePaletteOffset=0x{summary.CandidatePaletteOffset:X}");
    builder.AppendLine($"candidatePaletteLooksVga={summary.CandidatePaletteLooksVga}");
    if (summary.PrimarySpriteSet is not null)
    {
        builder.AppendLine($"secondaryNameOffset=0x{summary.PrimarySpriteSet.EncodedNameOffset:X}");
        builder.AppendLine($"secondaryNameDecoded={summary.PrimarySpriteSet.DecodedName}");
        builder.AppendLine($"mainRemapOffset=0x{summary.PrimarySpriteSet.MainRemapOffset:X}");
        builder.AppendLine($"primaryOffsetsOffset=0x{summary.PrimarySpriteSet.OffsetTableOffset:X}");
        builder.AppendLine($"primaryLengthsOffset=0x{summary.PrimarySpriteSet.LengthTableOffset:X}");
        builder.AppendLine($"primaryBlobOffset=0x{summary.PrimarySpriteSet.BlobOffset:X}");
        builder.AppendLine($"primaryBlobSize={summary.PrimarySpriteSet.BlobSize}");
        builder.AppendLine($"primaryReferencedEntryCount={summary.PrimarySpriteSet.ReferencedEntryCount}");
    }

    builder.AppendLine($"previewPaletteKind={previewPaletteKind}");
    builder.AppendLine($"primaryDecodedEntryCount={primaryEntryCount}");
    builder.AppendLine($"tail16={ToHex(summary.First16BytesAfterHeader)}");
    return builder.ToString();
}

static string BuildByteLookupReport(ReadOnlySpan<byte> bytes)
{
    var builder = new StringBuilder();
    builder.AppendLine("sourceIndex\ttargetIndex");

    for (var index = 0; index < bytes.Length; index++)
    {
        builder.Append(index);
        builder.Append('\t');
        builder.AppendLine(bytes[index].ToString());
    }

    return builder.ToString();
}

static string BuildAnimFrameReport(IReadOnlyList<MidgetPowerAnimFrame> frames)
{
    var builder = new StringBuilder();
    // Header: index, fileOffset, slot, offsetX, offsetY, flags, duration, nextAnim, then raw hex
    builder.AppendLine("frameIndex\tfileOffset\tslot\toffsetX\toffsetY\tflags\tduration\tnextAnim\traw_hex");
    foreach (var f in frames)
    {
        builder.Append(f.Index); builder.Append('\t');
        builder.Append($"0x{f.FileOffset:X6}"); builder.Append('\t');
        builder.Append(f.SpriteSlot); builder.Append('\t');
        builder.Append(f.OffsetX); builder.Append('\t');
        builder.Append(f.OffsetY); builder.Append('\t');
        builder.Append($"0x{f.Flags:X2}"); builder.Append('\t');
        builder.Append(f.Duration); builder.Append('\t');
        builder.Append(f.NextAnim); builder.Append('\t');
        builder.AppendLine(BitConverter.ToString(f.Raw));
    }
    return builder.ToString();
}


static string BuildUInt32LookupReport(string valueColumnName, IReadOnlyList<uint> values)
{
    var builder = new StringBuilder();
    builder.Append("index\t");
    builder.AppendLine(valueColumnName);

    for (var index = 0; index < values.Count; index++)
    {
        builder.Append(index);
        builder.Append('\t');
        builder.AppendLine(values[index].ToString());
    }

    return builder.ToString();
}

static string BuildMidgetPowerPrimaryEntryReport(IReadOnlyList<MidgetPowerPrimaryEntry> entries)
{
    var builder = new StringBuilder();
    builder.AppendLine("index\toffset\tlength\tanchorX\twidth\theight\tcoveredPixels");

    foreach (var entry in entries)
    {
        builder.Append(entry.Index);
        builder.Append('\t');
        builder.Append(entry.Offset);
        builder.Append('\t');
        builder.Append(entry.Length);
        builder.Append('\t');
        builder.Append(entry.Slice.AnchorX);
        builder.Append('\t');
        builder.Append(entry.Slice.Width);
        builder.Append('\t');
        builder.Append(entry.Slice.Height);
        builder.Append('\t');
        builder.AppendLine(entry.Slice.Coverage.Count(covered => covered).ToString());
    }

    return builder.ToString();
}

static string BuildUnknownExportSummary(string sourcePath, int fileSize, string head32, int sliceScanCount)
{
    var builder = new StringBuilder();
    builder.AppendLine($"source={sourcePath}");
    builder.AppendLine($"kind={VolKind.Unknown}");
    builder.AppendLine($"size={fileSize}");
    builder.AppendLine($"sliceScanCount={sliceScanCount}");
    builder.AppendLine($"head32={head32}");
    return builder.ToString();
}

static string BuildEmbeddedSliceReport(IReadOnlyList<ScannedSliceEntry> entries)
{
    var builder = new StringBuilder();
    builder.AppendLine("index\toffset\tlength\tanchorX\twidth\theight\tcoveredPixels");

    for (var index = 0; index < entries.Count; index++)
    {
        var entry = entries[index];
        builder.Append(index);
        builder.Append('\t');
        builder.Append(entry.Offset);
        builder.Append('\t');
        builder.Append(entry.Length);
        builder.Append('\t');
        builder.Append(entry.Slice.AnchorX);
        builder.Append('\t');
        builder.Append(entry.Slice.Width);
        builder.Append('\t');
        builder.Append(entry.Slice.Height);
        builder.Append('\t');
        builder.AppendLine(entry.Slice.Coverage.Count(covered => covered).ToString());
    }

    return builder.ToString();
}

static string BuildOffsetTableReport(IReadOnlyList<uint> offsetTable)
{
    var builder = new StringBuilder();
    builder.AppendLine("index\toffset");
    for (var index = 0; index < offsetTable.Count; index++)
    {
        builder.Append(index);
        builder.Append('\t');
        builder.AppendLine(offsetTable[index].ToString());
    }

    return builder.ToString();
}

static string BuildSliceReport(IReadOnlyList<BackgroundSliceBoundary> slices)
{
    var builder = new StringBuilder();
    builder.AppendLine("ordinal\toffset\tlength\ttableIndices");
    foreach (var slice in slices)
    {
        builder.Append(slice.Ordinal);
        builder.Append('\t');
        builder.Append(slice.Offset);
        builder.Append('\t');
        builder.Append(slice.Length);
        builder.Append('\t');
        builder.AppendLine(string.Join(',', slice.TableIndices));
    }

    return builder.ToString();
}

static uint ReadUInt32(ReadOnlySpan<byte> data, int offset)
{
    EnsureLength(data, offset + 4, $"u32 at 0x{offset:X}");
    return BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
}

static uint ReadUInt32BigEndian(ReadOnlySpan<byte> data, int offset)
{
    EnsureLength(data, offset + 4, $"be u32 at 0x{offset:X}");
    return BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
}

static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset)
{
    EnsureLength(data, offset + 2, $"u16 at 0x{offset:X}");
    return BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
}

static void EnsureLength(ReadOnlySpan<byte> data, int requiredLength, string context)
{
    if (data.Length < requiredLength)
    {
        throw new InvalidDataException($"Truncated while reading {context}. Need 0x{requiredLength:X} bytes, have 0x{data.Length:X}.");
    }
}

static string ToHex(ReadOnlySpan<byte> data)
{
    return Convert.ToHexString(data);
}

enum VolKind
{
    Unknown,
    PaletteFirst,
    MidgetPower,
}

sealed record ProbeOptions(
    string? ExportDirectory,
    IReadOnlyList<string> InputPaths);

sealed record BackgroundVolSummary(
    IReadOnlyList<uint> OffsetTable,
    int NonZeroOffsetCount,
    uint MaxOffset,
    byte[] Palette6Bit,
    byte[] MetadataA0,
    uint FirstDataSize,
    int FirstDataStart,
    byte[] FirstData,
    ushort PostDataRelativeSkip,
    int RecordCount,
    int RecordTableStart,
    int RecordTableBytes,
    byte[] RecordTable,
    ushort Blob35aSize,
    byte[] Blob35a,
    byte[] FixedBlock04,
    byte[] FixedBlock08,
    int Block0CSize,
    byte[] Block0C,
    ushort Block10Size,
    byte[] Block10,
    ushort Block14Size,
    byte[] Block14,
    ushort Block18Size,
    byte[] Block18,
    ushort Block1CSize,
    byte[] Block1C,
    BackgroundMidiTrailer? TrailingMidi,
    int ConsumedBytes,
    int RemainingBytes,
    byte[] RemainingTail);

sealed record BackgroundMidiTrailer(
    ushort OuterSize,
    byte[] Data);

sealed record BackgroundRecordEntry(
    int Index,
    ushort BlobOffset,
    byte Flag2,
    byte Flag3,
    bool IsActive,
    byte? DescriptorByte2,
    byte? SliceIndex,
    short? OffsetX,
    short? OffsetY,
    string DescriptorHeadHex);

sealed record ActiveRecordPlacement(
    int RecordIndex,
    int SliceIndex,
    int SliceOrdinal,
    int X,
    int Y,
    DecodedCoveredIndexedPlane Slice);

sealed record DecodedIndexedPlane(
    int Width,
    int Height,
    byte[] Pixels);

record DecodedCoveredIndexedPlane(
    int Width,
    int Height,
    byte[] Pixels,
    bool[] Coverage);

sealed record DecodedFirstDataSlice(
    byte AnchorX,
    int Width,
    int Height,
    byte[] Pixels);

sealed record DecodedCoveredFirstDataSlice(
    byte AnchorX,
    int Width,
    int Height,
    byte[] Pixels,
    bool[] Coverage)
    : DecodedCoveredIndexedPlane(Width, Height, Pixels, Coverage);

sealed record BackgroundSliceBoundary(
    int Ordinal,
    int Offset,
    int Length,
    IReadOnlyList<int> TableIndices,
    int PrimaryTableIndex);

sealed record ScannedSliceEntry(
    int Offset,
    int Length,
    DecodedCoveredFirstDataSlice Slice);

sealed record MidgetPowerPrimaryEntry(
    int Index,
    int Offset,
    int Length,
    DecodedCoveredFirstDataSlice Slice);

sealed record MidgetPowerAnimFrame(
    int Index,
    int FileOffset,
    byte[] Raw)
{
    // Convenience accessors — interpretations are tentative until validated against draw code
    public byte SpriteSlot => Raw[0];           // index into primary 256-slot sprite table
    public sbyte OffsetX   => (sbyte)Raw[1];    // signed X displacement from body origin
    public sbyte OffsetY   => (sbyte)Raw[2];    // signed Y displacement from body origin
    public byte Flags      => Raw[3];           // flip / variant flags
    public ushort Duration => (ushort)(Raw[4] | (Raw[5] << 8));
    public ushort NextAnim => (ushort)(Raw[6] | (Raw[7] << 8));
}

sealed record MidgetPowerAnimSet(
    int FileOffset,
    int FrameCount,
    IReadOnlyList<MidgetPowerAnimFrame> Frames,
    byte[] LookupTable256,      // 256-byte obj+0xEDC table
    byte[] Blob5CD,             // obj+0x5CD allocation content
    byte[] BlobED8);            // obj+0xED8 allocation content

sealed record MidgetPowerSecondaryBlock(
    int Index,
    int FileOffset,
    uint RawSize,
    byte[] Data,
    IReadOnlyList<ScannedSliceEntry> Entries);

sealed record MidgetPowerPrimarySpriteSet(
    int EncodedNameOffset,
    byte[] EncodedNameBytes,
    string DecodedName,
    int MainRemapOffset,
    byte[] MainRemapTable,
    int OffsetTableOffset,
    IReadOnlyList<uint> Offsets,
    int LengthTableOffset,
    IReadOnlyList<uint> Lengths,
    int BlobOffset,
    uint BlobSize,
    byte[] Blob,
    int ReferencedEntryCount,
    IReadOnlyList<MidgetPowerPrimaryEntry> Entries,
    IReadOnlyList<MidgetPowerSecondaryBlock> SecondaryBlocks,
    MidgetPowerAnimSet? AnimSet);

sealed record MidgetPowerSummary(
    string Magic,
    uint HeaderDwordAt0C,
    byte[] First16BytesAfterHeader,
    int CandidatePaletteOffset,
    bool CandidatePaletteLooksVga,
    MidgetPowerPrimarySpriteSet? PrimarySpriteSet);
