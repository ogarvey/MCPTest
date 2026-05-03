using JojoExtractor.Pac;
using JojoExtractor.Psx;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    PrintUsage();
    return 0;
}

try
{
    return args[0].ToLowerInvariant() switch
    {
        "info"     => CmdInfo(args[1..]),
        "vm"       => CmdVm(args[1..]),
        "report"   => CmdReport(args[1..]),
        "kpln-clut" => CmdKplnClut(args[1..]),
        "vram-preview" => CmdVramPreview(args[1..]),
        "packed-map" => CmdPackedMap(args[1..]),
        "kpln-preview" => CmdKplnPreview(args[1..]),
        "kpln-frames" => CmdKplnFrames(args[1..]),
        "kpln-contexts" => CmdKplnContexts(args[1..]),
        "extract"  => CmdExtract(args[1..]),
        "batch"    => CmdBatch(args[1..]),
        "palettes" => CmdPalettes(args[1..]),
        "clt"      => CmdClt(args[1..]),
        "image"    => CmdImage(args[1..]),
        "auto"     => CmdAuto(args[1..]),
        "auto-batch" => CmdAutoBatch(args[1..]),
        _          => Fail($"Unknown command: {args[0]}")
    };
}

catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 2;
}

static int CmdVm(string[] args)
{
    if (args.Length < 1) return Fail("Usage: jojoextract vm <file.pac> [poolOffset]");

    int? poolOffset = null;
    if (args.Length >= 2)
    {
        if (!TryParseNumber(args[1], out int parsedOffset) || parsedOffset < 0)
            return Fail($"Bad pool offset '{args[1]}'. Use decimal or 0x-prefixed hex.");

        poolOffset = parsedOffset;
    }

    string pacPath = args[0];
    var pac = PacFile.Load(pacPath);
    string fileName = Path.GetFileName(pacPath);
    string family = Path.GetFileNameWithoutExtension(pacPath).ToUpperInvariant();

    Console.WriteLine($"File:        {fileName}");
    Console.WriteLine($"Total size:  0x{pac.TotalSize:X} ({pac.TotalSize} bytes)");
    Console.WriteLine($"Entry count: {pac.EntryCount}");
    Console.WriteLine("VM source:   PAC directory records at file offset 0x08");

    if (poolOffset is not null)
        Console.WriteLine($"Pool offset: 0x{poolOffset.Value:X}");
    else if (family.StartsWith("KPLN", StringComparison.Ordinal))
        Console.WriteLine("Known pool offsets: side 0 => +0, side 1 => +8 (FUN_80019914)");
    else if (family.StartsWith("PLK", StringComparison.Ordinal))
        Console.WriteLine("Known pool offset: ((player byte 0x136 & 0x7f) << 1) (FUN_80019b8c)");

    Console.WriteLine();
    Console.WriteLine("Idx  Opcode  Class  Low  Length    Stride4   Sector    Payload   Effect");
    Console.WriteLine("---  ------  -----  ---  --------  --------  --------  --------  ------------------------------");

    foreach (var entry in pac.Entries.Select(PacVmEntry.FromPacEntry))
    {
        string effect = DescribeVmEffect(entry, family, poolOffset);
        Console.WriteLine(
            $"{entry.Index,3}  {Hex(entry.Opcode, 4)}  {Hex(entry.OpcodeClass, 3)}  " +
            $"{Hex(entry.OpcodeLow, 2)}  {Hex(entry.DataLength, 6)}  {Hex(entry.StrideLength, 6)}  " +
            $"{Hex(entry.SectorLength, 6)}  {Hex(entry.DataOffset, 6)}  {effect}");
    }

    return 0;
}

static int CmdInfo(string[] args)
{
    if (args.Length < 1) return Fail("Usage: jojoextract info <file.pac>");

    var pac = PacFile.Load(args[0]);
    Console.WriteLine($"File:        {Path.GetFileName(args[0])}");
    Console.WriteLine($"Total size:  0x{pac.TotalSize:X} ({pac.TotalSize} bytes)");
    Console.WriteLine($"Entry count: {pac.EntryCount}");
    Console.WriteLine();
    Console.WriteLine("Idx  Flags       Length     Offset      EndOffset");
    Console.WriteLine("---  ----------  ---------  ----------  ----------");
    foreach (var e in pac.Entries)
    {
        long end = e.DataOffset + e.DataLength;
        Console.WriteLine($"{e.Index,3}  0x{e.Flags:X8}  0x{e.DataLength,7:X}  0x{e.DataOffset,8:X}  0x{end,8:X}");
    }
    return 0;
}

static int CmdReport(string[] args)
{
    if (args.Length < 1) return Fail("Usage: jojoextract report <file.pac|directory>");

    string path = args[0];
    if (Directory.Exists(path))
    {
        string[] pacPaths = Directory.GetFiles(path, "*.PAC", SearchOption.TopDirectoryOnly)
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (pacPaths.Length == 0)
            return Fail($"No *.PAC files found in {path}.");

        PacAssetReport[] reports = pacPaths
            .Select(pacPath => PacAssetReporter.Analyze(pacPath, PacFile.Load(pacPath)))
            .ToArray();

        Console.WriteLine($"PAC report summary: {path}");
        Console.WriteLine($"Files: {reports.Length}");
        Console.WriteLine();
        Console.WriteLine("File            Mapping             VM profile                         Handlers");
        Console.WriteLine("--------------  ------------------  ---------------------------------  ------------------------------");
        foreach (PacAssetReport report in reports)
        {
            string handlers = report.ProvenHandlers.Count == 0
                ? "-"
                : string.Join(", ", report.ProvenHandlers.Select(PacAssetReporter.GetShortHandlerName).Distinct(StringComparer.Ordinal));
            Console.WriteLine($"{report.FileName,-14}  {report.MappingSummary,-18}  {Truncate(report.OpcodeProfile, 33),-33}  {handlers}");
        }

        PrintReportTotals(reports);

        return 0;
    }

    if (!File.Exists(path))
        return Fail($"PAC file or directory not found: {path}");

    var singlePac = PacFile.Load(path);
    PrintDetailedReport(PacAssetReporter.Analyze(path, singlePac));
    return 0;
}

static void PrintReportTotals(IReadOnlyList<PacAssetReport> reports)
{
    Console.WriteLine();
    Console.WriteLine("Mapping totals:");
    foreach (var group in reports.GroupBy(report => report.MappingSummary).OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal))
        Console.WriteLine($"  {group.Key,-18} {group.Count(),4}");

    Console.WriteLine();
    Console.WriteLine("Handler totals:");
    var handlerGroups = reports
        .SelectMany(report => report.ProvenHandlers.Select(PacAssetReporter.GetShortHandlerName).Distinct(StringComparer.Ordinal))
        .GroupBy(handler => handler)
        .OrderByDescending(group => group.Count())
        .ThenBy(group => group.Key, StringComparer.Ordinal)
        .ToArray();
    if (handlerGroups.Length == 0)
    {
        Console.WriteLine("  - none");
    }
    else
    {
        foreach (var group in handlerGroups)
            Console.WriteLine($"  {group.Key,-18} {group.Count(),4}");
    }

    Console.WriteLine();
    Console.WriteLine("Top VM profiles:");
    foreach (var group in reports.GroupBy(report => report.OpcodeProfile).OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal).Take(12))
        Console.WriteLine($"  {group.Count(),4}  {group.Key}");
}

static void PrintDetailedReport(PacAssetReport report)
{
    Console.WriteLine($"File:        {report.FileName}");
    Console.WriteLine($"Total size:  0x{report.TotalSize:X} ({report.TotalSize} bytes)");
    Console.WriteLine($"Entry count: {report.EntryCount}");
    Console.WriteLine($"VM profile:  {report.OpcodeProfile}");
    Console.WriteLine();

    Console.WriteLine("Mapping evidence:");
    foreach (var evidence in report.MappingEvidence)
        Console.WriteLine($"  [{evidence.Confidence}] {evidence.Source}: {evidence.Details}");
    Console.WriteLine();

    Console.WriteLine("VM classes:");
    foreach (var summary in report.ClassSummaries)
        Console.WriteLine($"  0x{summary.OpcodeClass:X4}  {summary.Name,-22} entries={summary.Count,2} total=0x{summary.TotalLength:X}");
    Console.WriteLine();

    Console.WriteLine("Proven extractor handlers:");
    if (report.ProvenHandlers.Count == 0)
        Console.WriteLine("  - none yet");
    else
        foreach (string handler in report.ProvenHandlers)
            Console.WriteLine($"  - {handler}");
    Console.WriteLine();

    if (report.DirectVramRecords.Count > 0)
    {
        Console.WriteLine("Direct VRAM records:");
        foreach (var record in report.DirectVramRecords)
        {
            string size = record.Width is int width && record.Height is int height
                ? $"layout={record.LayoutIndex} placed4={width}x{height}"
                : $"unplaced: {record.Error}";
            Console.WriteLine($"  entry {record.EntryIndex,3} opcode=0x{record.Opcode:X4} length=0x{record.Length:X} {size}");
        }
        Console.WriteLine();
    }

    if (report.CompressedTimRecords.Count > 0)
    {
        Console.WriteLine("Compressed TIM RAM records:");
        foreach (var record in report.CompressedTimRecords)
        {
            string destination = record.RamDestination is uint ramDestination
                ? $"ram=0x{ramDestination:X8}"
                : "ram=unknown";
            Console.WriteLine($"  entry {record.EntryIndex,3} opcode=0x{record.Opcode:X4} length=0x{record.Length:X} {destination} {record.Format}");
        }
        Console.WriteLine();
    }

    if (report.SoundBankRecords.Count > 0)
    {
        Console.WriteLine("Sound-bank records:");
        foreach (var record in report.SoundBankRecords)
        {
            string signature = record.HasVabHeaderSignature ? "pBAV" : "-";
            Console.WriteLine($"  entry {record.EntryIndex,3} opcode=0x{record.Opcode:X4} length=0x{record.Length:X} role={record.Role} signature={signature}");
        }
        Console.WriteLine();
    }

    if (report.RuntimePoolRecords.Count > 0)
    {
        Console.WriteLine("Runtime-pool records:");
        foreach (var record in report.RuntimePoolRecords)
            Console.WriteLine($"  entry {record.EntryIndex,3} opcode=0x{record.Opcode:X4} length=0x{record.Length:X} pool_slot_low=0x{record.PoolSlot:X2} aligned_stride=0x{record.StrideLength:X}");
        Console.WriteLine();
    }

    if (report.RamPayloadRecords.Count > 0)
    {
        Console.WriteLine("Native RAM payload records:");
        foreach (var record in report.RamPayloadRecords)
        {
            string destination = record.DefaultRamDestination is uint ramDestination ? $"0x{ramDestination:X8}" : "unknown";
            Console.WriteLine($"  entry {record.EntryIndex,3} opcode=0x{record.Opcode:X4} length=0x{record.Length:X} ram={destination} aligned_stride=0x{record.StrideLength:X}");
        }
        Console.WriteLine();
    }

    if (report.PaletteBankCandidates.Count > 0)
    {
        Console.WriteLine("Palette-bank compatible entries (not paired):");
        foreach (var candidate in report.PaletteBankCandidates.Take(16))
            Console.WriteLine($"  entry {candidate.EntryIndex,3} opcode=0x{candidate.Opcode:X4} length=0x{candidate.Length:X} banks={candidate.BankCount}");
        if (report.PaletteBankCandidates.Count > 16)
            Console.WriteLine($"  ... {report.PaletteBankCandidates.Count - 16} more");
        Console.WriteLine();
    }

    Console.WriteLine("Pending parser work:");
    if (report.PendingWork.Count == 0)
        Console.WriteLine("  - no unresolved classes detected by this report");
    else
        foreach (string pending in report.PendingWork)
            Console.WriteLine($"  - {pending}");
}

static string Truncate(string text, int maxLength)
{
    return text.Length <= maxLength ? text : text[..Math.Max(0, maxLength - 3)] + "...";
}

static int CmdKplnClut(string[] args)
{
    if (args.Length < 1) return Fail("Usage: jojoextract kpln-clut <KPLNxx.PAC> [paletteId|all] [outputDir]");

    string pacPath = args[0];
    var pac = PacFile.Load(pacPath);
    int paletteCount = KplnClutPreviewer.GetPaletteCount(pac);

    int[] paletteIds;
    if (args.Length >= 2 && !args[1].Equals("all", StringComparison.OrdinalIgnoreCase))
    {
        if (!TryParseNumber(args[1], out int paletteId) || paletteId < 0 || paletteId >= paletteCount)
            return Fail($"Palette id must be 0..{paletteCount - 1} or 'all'.");

        paletteIds = new[] { paletteId };
    }
    else
    {
        paletteIds = Enumerable.Range(0, paletteCount).ToArray();
    }

    string outDir = args.Length >= 3
        ? args[2]
        : Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(pacPath)) ?? ".",
            Path.GetFileNameWithoutExtension(pacPath) + "_clut_preview");

    Directory.CreateDirectory(outDir);
    string baseName = Path.GetFileNameWithoutExtension(pacPath);

    foreach (int paletteId in paletteIds)
    {
        using var image = KplnClutPreviewer.Render(pac, paletteId);
        string outPath = Path.Combine(outDir, $"{baseName}_palette{paletteId}_runtime-clut.png");
        image.SaveAsPng(outPath);
        Console.WriteLine($"  wrote {Path.GetFileName(outPath)} ({image.Width}x{image.Height})");
    }

    Console.WriteLine($"Rendered {paletteIds.Length} runtime CLUT preview PNG(s) to {outDir}.");
    return 0;
}

static int CmdVramPreview(string[] args)
{
    if (args.Length < 1) return Fail("Usage: jojoextract vram-preview <file.pac> [entryIdx|auto|all] [4|8|both] [tableOffset] [output.png|outputDir]");

    bool renderAll = args.Length < 2 || args[1].Equals("auto", StringComparison.OrdinalIgnoreCase) || args[1].Equals("all", StringComparison.OrdinalIgnoreCase);
    bool renderBothBpp = args.Length >= 3 && (args[2].Equals("both", StringComparison.OrdinalIgnoreCase) || args[2].Equals("all", StringComparison.OrdinalIgnoreCase));

    if (!renderAll && !renderBothBpp)
        return CmdVramPreviewSingle(args);

    string pacPath = args[0];
    var pac = PacFile.Load(pacPath);

    PacEntry[] entries;
    if (renderAll)
    {
        entries = pac.Entries
            .Where(entry => ((ushort)(entry.Flags & 0xffff) & 0x0f00) == 0x0200)
            .ToArray();
        if (entries.Length == 0)
            return Fail("PAC has no 0x0200 direct VRAM image entries.");
    }
    else
    {
        if (!TryParseNumber(args[1], out int entryIndex) || entryIndex < 0 || entryIndex >= pac.EntryCount)
            return Fail($"Entry index must be 0..{pac.EntryCount - 1}, 'auto', or 'all'.");

        entries = new[] { pac.Entries[entryIndex] };
    }

    int[] bitsPerPixelValues;
    if (renderBothBpp)
    {
        bitsPerPixelValues = new[] { 4, 8 };
    }
    else if (args.Length >= 3)
    {
        if (!int.TryParse(args[2], out int bitsPerPixel) || bitsPerPixel is not (4 or 8))
            return Fail("bits per pixel must be 4, 8, or both.");

        bitsPerPixelValues = new[] { bitsPerPixel };
    }
    else
    {
        bitsPerPixelValues = new[] { 4 };
    }

    int tableOffset = 0;
    if (args.Length >= 4 && (!TryParseNumber(args[3], out tableOffset) || tableOffset < 0))
        return Fail($"Bad table offset '{args[3]}'. Use decimal or 0x-prefixed hex.");

    bool multiOutput = renderAll || entries.Length > 1 || bitsPerPixelValues.Length > 1;
    string outputPath = args.Length >= 5
        ? args[4]
        : Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(pacPath)) ?? ".",
            multiOutput
                ? Path.GetFileNameWithoutExtension(pacPath) + "_vram_preview"
                : $"{Path.GetFileNameWithoutExtension(pacPath)}_entry{entries[0].Index}_{bitsPerPixelValues[0]}bpp_gray.png");

    string baseName = Path.GetFileNameWithoutExtension(pacPath);
    int written = 0;
    int failed = 0;
    if (multiOutput)
        Directory.CreateDirectory(outputPath);
    else
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)) ?? ".");

    foreach (PacEntry entry in entries)
    {
        ushort opcode = (ushort)(entry.Flags & 0xffff);
        foreach (int bitsPerPixel in bitsPerPixelValues)
        {
            try
            {
                var layout = VramTexturePreviewer.GetLayout((opcode & 0xff) + tableOffset);
                string outPath = multiOutput
                    ? Path.Combine(outputPath, $"{baseName}_entry{entry.Index:D3}_op{opcode:X4}_layout{layout.TableIndex}_{bitsPerPixel}bpp_placed_gray.png")
                    : outputPath;

                using var image = VramTexturePreviewer.Render(pac, entry, bitsPerPixel, tableOffset);
                image.SaveAsPng(outPath);
                Console.WriteLine($"  wrote {Path.GetFileName(outPath)} ({image.Width}x{image.Height}, entry {entry.Index}, {bitsPerPixel}bpp, placed layout {layout.TableIndex})");
                written++;
            }
            catch (Exception ex) when (multiOutput)
            {
                failed++;
                Console.Error.WriteLine($"  [skip] entry {entry.Index} {bitsPerPixel}bpp: {ex.Message}");
            }
        }
    }

    Console.WriteLine($"Rendered {written} direct VRAM preview PNG(s) to {(multiOutput ? outputPath : Path.GetDirectoryName(Path.GetFullPath(outputPath)))}" + (failed == 0 ? "." : $" ({failed} skipped)."));
    return failed == 0 ? 0 : 1;
}

static int CmdVramPreviewSingle(string[] args)
{
    if (args.Length < 1) return Fail("Usage: jojoextract vram-preview <file.pac> [entryIdx] [4|8] [tableOffset] [output.png]");

    string pacPath = args[0];
    var pac = PacFile.Load(pacPath);

    int entryIndex;
    if (args.Length >= 2 && !args[1].Equals("auto", StringComparison.OrdinalIgnoreCase))
    {
        if (!TryParseNumber(args[1], out entryIndex) || entryIndex < 0 || entryIndex >= pac.EntryCount)
            return Fail($"Entry index must be 0..{pac.EntryCount - 1} or 'auto'.");
    }
    else
    {
        int? autoEntryIndex = pac.Entries
            .Where(e => ((ushort)(e.Flags & 0xffff) & 0x0f00) == 0x0200)
            .Select(e => (int?)e.Index)
            .FirstOrDefault();
        if (autoEntryIndex is null)
            return Fail("PAC has no 0x0200 direct VRAM image entry.");

        entryIndex = autoEntryIndex.Value;
    }

    int bitsPerPixel = 4;
    if (args.Length >= 3 && (!int.TryParse(args[2], out bitsPerPixel) || bitsPerPixel is not (4 or 8)))
        return Fail("bits per pixel must be 4 or 8.");

    int tableOffset = 0;
    if (args.Length >= 4 && (!TryParseNumber(args[3], out tableOffset) || tableOffset < 0))
        return Fail($"Bad table offset '{args[3]}'. Use decimal or 0x-prefixed hex.");

    string outPath = args.Length >= 5
        ? args[4]
        : Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(pacPath)) ?? ".",
            $"{Path.GetFileNameWithoutExtension(pacPath)}_entry{entryIndex}_{bitsPerPixel}bpp_placed_gray.png");

    PacEntry entry = pac.Entries[entryIndex];
    ushort opcode = (ushort)(entry.Flags & 0xffff);
    var layout = VramTexturePreviewer.GetLayout((opcode & 0xff) + tableOffset);

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? ".");
    using var image = VramTexturePreviewer.Render(pac, entry, bitsPerPixel, tableOffset);
    image.SaveAsPng(outPath);

    Console.WriteLine($"Wrote {outPath} ({image.Width}x{image.Height}, {bitsPerPixel}bpp placed grayscale).");
    Console.WriteLine($"Layout DAT_8005991c[{layout.TableIndex}]: x=0x{layout.X:X}, y=0x{layout.Y:X}, w=0x{layout.WordWidth:X} words, chunkH=0x{layout.ChunkHeight:X}, dx=0x{layout.DeltaX:X}, dy=0x{layout.DeltaY:X}, stepBytes=0x{layout.StepByteThreshold:X}.");
    return 0;
}

static int CmdKplnPreview(string[] args)
{
    if (args.Length < 1) return Fail("Usage: jojoextract kpln-preview <KPLNxx.PAC> [outputDir]");

    string pacPath = args[0];
    var pac = PacFile.Load(pacPath);
    string baseName = Path.GetFileNameWithoutExtension(pacPath);
    string outDir = args.Length >= 2
        ? args[1]
        : Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(pacPath)) ?? ".",
            baseName + "_preview");

    Directory.CreateDirectory(outDir);

    int paletteCount = KplnClutPreviewer.GetPaletteCount(pac);
    string clutDir = Path.Combine(outDir, "runtime_clut");
    Directory.CreateDirectory(clutDir);
    for (int paletteId = 0; paletteId < paletteCount; paletteId++)
    {
        using var clutImage = KplnClutPreviewer.Render(pac, paletteId);
        string clutPath = Path.Combine(clutDir, $"{baseName}_palette{paletteId}_runtime-clut.png");
        clutImage.SaveAsPng(clutPath);
        Console.WriteLine($"  wrote {Path.GetRelativePath(outDir, clutPath)} ({clutImage.Width}x{clutImage.Height})");
    }

    var directEntries = pac.Entries
        .Where(e => ((ushort)(e.Flags & 0xffff) & 0x0f00) == 0x0200)
        .ToArray();
    foreach (var entry in directEntries)
    {
        using var textureImage = VramTexturePreviewer.Render(pac, entry, 4);
        string texturePath = Path.Combine(outDir, $"{baseName}_entry{entry.Index}_4bpp_placed_gray.png");
        textureImage.SaveAsPng(texturePath);
        Console.WriteLine($"  wrote {Path.GetRelativePath(outDir, texturePath)} ({textureImage.Width}x{textureImage.Height})");
    }

    Console.WriteLine($"Generated KPLN preview bundle in {outDir}.");
    return 0;
}

static int CmdKplnFrames(string[] args)
{
    if (args.Length < 1) return Fail("Usage: jojoextract kpln-frames <KPLNxx.PAC> [first] [count] [outputDir] [frameOpcode] [paletteId] [side] [cache|cache-auto|direct] [clutBase] [clutRowBase] [clutMode] [renderMode] [orientation]");

    string pacPath = args[0];
    var pac = PacFile.Load(pacPath);
    ushort frameOpcode = KplnFramePreviewer.DefaultFrameOpcode;
    if (args.Length >= 5)
    {
        if (!TryParseNumber(args[4], out int opcodeValue) || opcodeValue is < 0 or > 0xffff)
            return Fail("Frame opcode must be a 16-bit value such as 0x0800 or 0x0802.");

        frameOpcode = (ushort)opcodeValue;
    }

    int paletteId = 0;
    if (args.Length >= 6 && (!TryParseNumber(args[5], out paletteId) || paletteId < 0))
        return Fail("Palette id must be non-negative.");

    int side = 0;
    if (args.Length >= 7 && (!TryParseNumber(args[6], out side) || side is not (0 or 1)))
        return Fail("Side must be 0 or 1.");

    string renderer = args.Length >= 8 ? args[7].ToLowerInvariant() : "cache";
    if (renderer is not ("cache" or "cache-auto" or "cache-auto-strict" or "direct"))
        return Fail("Renderer must be 'cache', 'cache-auto', 'cache-auto-strict', or 'direct'.");

    int clutBase = 0;
    if (args.Length >= 9 && (!TryParseNumber(args[8], out clutBase) || clutBase < 0))
        return Fail("CLUT base must be non-negative.");

    int clutRowBase = KplnFramePreviewer.DefaultClutBaseY + side;
    if (args.Length >= 10 && (!TryParseNumber(args[9], out clutRowBase) || clutRowBase < 0))
        return Fail("CLUT row base must be non-negative.");

    int clutMode = 0;
    if (args.Length >= 11 && !TryParseNumber(args[10], out clutMode))
        return Fail("CLUT mode must be a signed integer matching object byte +0x1d.");
    clutMode = NormalizeSignedByte(clutMode);

    int renderMode = 0;
    if (args.Length >= 12 && (!TryParseNumber(args[11], out renderMode) || renderMode is < 0 or > 0xff))
        return Fail("Render mode must be 0..255 matching object byte +0xb0.");

    int orientation = 0;
    if (args.Length >= 13 && (!TryParseNumber(args[12], out orientation) || orientation is < 0 or > 3))
        return Fail("Orientation must be 0..3 matching drawState +0x3e.");

    string clutSuffix = clutBase == 0 && clutRowBase == KplnFramePreviewer.DefaultClutBaseY + side && clutMode == 0 && renderMode == 0 && orientation == 0
        ? string.Empty
        : $"_b{clutBase}_y{clutRowBase:X3}_m{clutMode}_r{renderMode:X2}_o{orientation}";

    int frameCount = KplnFramePreviewer.GetFrameCount(pac, frameOpcode);

    int first = 0;
    if (args.Length >= 2 && (!TryParseNumber(args[1], out first) || first < 0 || first >= frameCount))
        return Fail($"First frame must be 0..{frameCount - 1}.");

    int count = Math.Min(32, frameCount - first);
    if (args.Length >= 3 && (!TryParseNumber(args[2], out count) || count < 1 || first + count > frameCount))
        return Fail($"Count must be 1..{frameCount - first}.");

    string baseName = Path.GetFileNameWithoutExtension(pacPath);
    string outDir = args.Length >= 4
        ? args[3]
        : Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(pacPath)) ?? ".",
            baseName + "_frames");

    Directory.CreateDirectory(outDir);
    IReadOnlyDictionary<int, KplnRenderContext> autoContexts = new Dictionary<int, KplnRenderContext>();
    if (renderer is "cache-auto" or "cache-auto-strict")
    {
        string? companionPath = KplnRenderContextFinder.TryFindDefaultCompanionPath(pacPath, side);
        if (companionPath is null)
            return Fail("Could not auto-locate companion M/PLxx.BIN for this KPLN PAC.");

        autoContexts = KplnRenderContextFinder.FindBestContexts(pac, companionPath, side, frameOpcode);
        Console.WriteLine($"Auto context source: {companionPath}");
        Console.WriteLine($"Auto contexts found:  {autoContexts.Count}");
    }

    var rendered = new List<(int Frame, Image<Rgba32> Image)>();
    int skippedFrames = 0;
    for (int frame = first; frame < first + count; frame++)
    {
        KplnRenderContext? autoContext = autoContexts.TryGetValue(frame, out var foundContext) ? foundContext : null;
        if (renderer == "cache-auto-strict" && autoContext is null)
        {
            skippedFrames++;
            continue;
        }

        var image = renderer is "cache" or "cache-auto"
            ? KplnFramePreviewer.RenderCachedFrame(
                pac,
                frame,
                frameOpcode,
                paletteId,
                side,
                clutBase: autoContext?.ClutBase ?? clutBase,
                clutRowBase: autoContext?.ClutRowBase ?? clutRowBase,
                clutMode: autoContext?.ClutMode ?? clutMode,
                renderMode: autoContext?.RenderMode ?? renderMode,
                orientation: autoContext?.Orientation ?? orientation)
            : renderer == "cache-auto-strict"
            ? KplnFramePreviewer.RenderCachedFrame(
                pac,
                frame,
                frameOpcode,
                paletteId,
                side,
                clutBase: autoContext!.Value.ClutBase,
                clutRowBase: autoContext.Value.ClutRowBase,
                clutMode: autoContext.Value.ClutMode,
                renderMode: autoContext.Value.RenderMode,
                orientation: autoContext.Value.Orientation)
            : KplnFramePreviewer.RenderFrame(pac, frame, frameOpcode, paletteId, side, clutBase: clutBase, clutRowBase: clutRowBase);
        string outPath = Path.Combine(outDir, $"{baseName}_{renderer}_{frameOpcode:X4}_p{paletteId}_s{side}{clutSuffix}_frame{frame:D4}.png");
        image.SaveAsPng(outPath);
        string contextText = autoContext is KplnRenderContext context
            ? $" ctx=b{context.ClutBase},y{context.ClutRowBase:X3},m{context.ClutMode},r{context.RenderMode:X2},o{context.Orientation}"
            : string.Empty;
        Console.WriteLine($"  wrote {Path.GetFileName(outPath)} ({image.Width}x{image.Height}){contextText}");
        rendered.Add((frame, image));
    }

    if (rendered.Count > 1)
    {
        using var sheet = MakeContactSheet(rendered.Select(x => x.Image).ToList(), columns: 8, margin: 8);
        string sheetPath = Path.Combine(outDir, $"{baseName}_{renderer}_{frameOpcode:X4}_p{paletteId}_s{side}{clutSuffix}_frames_{first:D4}_{first + count - 1:D4}_sheet.png");
        sheet.SaveAsPng(sheetPath);
        Console.WriteLine($"  wrote {Path.GetFileName(sheetPath)} ({sheet.Width}x{sheet.Height})");
    }

    foreach (var item in rendered)
        item.Image.Dispose();

    if (skippedFrames > 0)
        Console.WriteLine($"  skipped {skippedFrames} frame(s) without recovered caller context");

    if (rendered.Count == 0)
        return Fail("No frame previews were rendered.");

    Console.WriteLine($"Rendered {rendered.Count} frame preview PNG(s) to {outDir}.");
    return 0;
}

static int CmdKplnContexts(string[] args)
{
    if (args.Length < 1) return Fail("Usage: jojoextract kpln-contexts <KPLNxx.PAC> [companion.bin|auto] [side]");

    string pacPath = args[0];
    var pac = PacFile.Load(pacPath);

    int side = 0;
    if (args.Length >= 3 && (!TryParseNumber(args[2], out side) || side is not (0 or 1)))
        return Fail("Side must be 0 or 1.");

    string? companionPath = null;
    if (args.Length >= 2 && !args[1].Equals("auto", StringComparison.OrdinalIgnoreCase))
        companionPath = args[1];
    else
        companionPath = KplnRenderContextFinder.TryFindDefaultCompanionPath(pacPath, side);

    if (companionPath is null)
        return Fail("Could not auto-locate companion M/PLxx.BIN for this KPLN PAC.");

    var contexts = KplnRenderContextFinder.FindContexts(pac, companionPath, side);
    var best = contexts
        .GroupBy(context => context.FrameIndex)
        .Select(group => group.OrderByDescending(context => context.Score).First())
        .OrderBy(context => context.FrameIndex)
        .ToArray();

    Console.WriteLine($"Companion: {companionPath}");
    Console.WriteLine($"Contexts:  {contexts.Count} candidates, {best.Length} best frame match(es)");
    Console.WriteLine();
    Console.WriteLine("Frame  Base  Row    Mode  RMode  Orient  Asset  Score  Source");
    Console.WriteLine("-----  ----  -----  ----  -----  ------  -----  -----  --------------------------------");
    foreach (var context in best)
    {
        string asset = context.AssetSlot is int slot ? slot.ToString() : "-";
        Console.WriteLine(
            $"{context.FrameIndex,5}  {context.ClutBase,4}  0x{context.ClutRowBase:X3}  {context.ClutMode,4}  " +
            $"0x{context.RenderMode:X2}  {context.Orientation,6}  {asset,5}  {context.Score,5}  {context.Source}");
    }

    return 0;
}

static Image<Rgba32> MakeContactSheet(IReadOnlyList<Image<Rgba32>> images, int columns, int margin)
{
    int cellWidth = images.Max(image => image.Width);
    int cellHeight = images.Max(image => image.Height);
    int rows = (images.Count + columns - 1) / columns;
    var sheet = new Image<Rgba32>(columns * cellWidth + (columns + 1) * margin, rows * cellHeight + (rows + 1) * margin, new Rgba32(40, 40, 40, 255));

    for (int i = 0; i < images.Count; i++)
    {
        int col = i % columns;
        int row = i / columns;
        int dstX = margin + col * (cellWidth + margin) + (cellWidth - images[i].Width) / 2;
        int dstY = margin + row * (cellHeight + margin) + (cellHeight - images[i].Height) / 2;
        Blit(sheet, images[i], dstX, dstY);
    }

    return sheet;
}

static void Blit(Image<Rgba32> target, Image<Rgba32> source, int dstX, int dstY)
{
    for (int y = 0; y < source.Height; y++)
    {
        for (int x = 0; x < source.Width; x++)
        {
            Rgba32 pixel = source[x, y];
            if (pixel.A != 0)
                target[dstX + x, dstY + y] = pixel;
        }
    }
}

static int CmdExtract(string[] args)
{
    if (args.Length < 1) return Fail("Usage: jojoextract extract <file.pac> [outputDir]");

    string pacPath = args[0];
    string outDir = args.Length >= 2
        ? args[1]
        : Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(pacPath)) ?? ".",
            Path.GetFileNameWithoutExtension(pacPath) + "_extracted");

    ExtractOne(pacPath, outDir, verbose: true);
    return 0;
}

static string DescribeVmEffect(PacVmEntry entry, string family, int? poolOffset)
{
    return entry.OpcodeClass switch
    {
        0x0100 => PacVmEntry.GetClassName(entry.OpcodeClass),
        0x0200 => "direct streamed LoadImage payload",
        0x0400 => PacVmEntry.GetClassName(entry.OpcodeClass),
        0x0800 => DescribeRuntimePoolEffect(entry, family, poolOffset),
        _ => PacVmEntry.GetClassName(entry.OpcodeClass)
    };
}

static string DescribeRuntimePoolEffect(PacVmEntry entry, string family, int? poolOffset)
{
    if (poolOffset is int offset)
    {
        return $"pool[{Hex(entry.OpcodeLow + offset, 2)}] stride={Hex(entry.StrideLength)}";
    }

    if (family.StartsWith("KPLN", StringComparison.Ordinal))
    {
        return $"pool[{Hex(entry.OpcodeLow, 2)}] side0, pool[{Hex(entry.OpcodeLow + 8, 2)}] side1, stride={Hex(entry.StrideLength)}";
    }

    if (family.StartsWith("PLK", StringComparison.Ordinal))
    {
        return $"pool[{Hex(entry.OpcodeLow, 2)}+offset], stride={Hex(entry.StrideLength)}";
    }

    return $"pool[{Hex(entry.OpcodeLow, 2)}+offset], stride={Hex(entry.StrideLength)}";
}

static string Hex(long value, int minDigits = 0)
{
    return "0x" + value.ToString("X").PadLeft(minDigits, '0');
}

static bool TryParseNumber(string text, out int value)
{
    if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        return int.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, null, out value);

    return int.TryParse(text, out value);
}

static int NormalizeSignedByte(int value)
{
    return value is >= 0 and <= 255 ? unchecked((sbyte)(byte)value) : value;
}

static int CmdBatch(string[] args)
{
    if (args.Length < 1) return Fail("Usage: jojoextract batch <inputDir> [outputDir]");

    string inputDir = args[0];
    if (!Directory.Exists(inputDir))
        return Fail($"Input directory not found: {inputDir}");

    string outputRoot = args.Length >= 2
        ? args[1]
        : Path.Combine(inputDir, "_extracted");

    Directory.CreateDirectory(outputRoot);

    string[] files = Directory.GetFiles(inputDir, "*.PAC", SearchOption.TopDirectoryOnly);
    Console.WriteLine($"Found {files.Length} PAC files in {inputDir}");

    int ok = 0, failed = 0;
    foreach (var file in files)
    {
        string subDir = Path.Combine(outputRoot, Path.GetFileNameWithoutExtension(file));
        try
        {
            ExtractOne(file, subDir, verbose: false);
            ok++;
        }
        catch (Exception ex)
        {
            failed++;
            Console.Error.WriteLine($"  [FAIL] {Path.GetFileName(file)}: {ex.Message}");
        }
    }

    Console.WriteLine($"Done. Succeeded: {ok}  Failed: {failed}");
    return failed == 0 ? 0 : 1;
}

static void ExtractOne(string pacPath, string outDir, bool verbose)
{
    var pac = PacFile.Load(pacPath);
    Directory.CreateDirectory(outDir);

    string baseName = Path.GetFileNameWithoutExtension(pacPath);
    int padWidth = pac.EntryCount switch
    {
        < 10   => 1,
        < 100  => 2,
        < 1000 => 3,
        _      => 4
    };

    foreach (var entry in pac.Entries)
    {
        var data = pac.GetEntryData(entry);
        string fileName = $"{baseName}_{entry.Index.ToString().PadLeft(padWidth, '0')}_flags{entry.Flags:X8}.bin";
        string outPath = Path.Combine(outDir, fileName);
        File.WriteAllBytes(outPath, data.ToArray());
    }

    if (verbose)
        Console.WriteLine($"Extracted {pac.EntryCount} entries to {outDir}");
}

static int CmdPalettes(string[] args)
{
    if (args.Length < 1) return Fail("Usage: jojoextract palettes <file.pac> [outputDir]");

    string pacPath = args[0];
    string outDir = args.Length >= 2
        ? args[1]
        : Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(pacPath)) ?? ".",
            Path.GetFileNameWithoutExtension(pacPath) + "_palettes");

    var pac = PacFile.Load(pacPath);
    Directory.CreateDirectory(outDir);
    string baseName = Path.GetFileNameWithoutExtension(pacPath);

    int padWidth = pac.EntryCount < 10 ? 1 : pac.EntryCount < 100 ? 2 : 3;
    int rendered = 0, skipped = 0;

    foreach (var entry in pac.Entries)
    {
        var data = pac.GetEntryData(entry);
        if (!ClutDecoder.LooksLikeClut(data))
        {
            Console.WriteLine($"  [skip] entry {entry.Index} length 0x{data.Length:X} is not a multiple of 0x{ClutDecoder.BankBytes:X}");
            skipped++;
            continue;
        }

        string fileName = $"{baseName}_{entry.Index.ToString().PadLeft(padWidth, '0')}_flags{entry.Flags:X8}.png";
        string outPath = Path.Combine(outDir, fileName);

        using var img = ClutDecoder.RenderAsBanksScaled(data, cellSize: 16);
        img.SaveAsPng(outPath);
        Console.WriteLine($"  wrote {fileName}  ({img.Width}x{img.Height} px, {data.Length / ClutDecoder.BankBytes} banks)");
        rendered++;
    }

    Console.WriteLine($"Rendered {rendered} palette PNGs to {outDir} (skipped {skipped}).");
    return 0;
}

static int CmdClt(string[] args)
{
    if (args.Length < 1) return Fail("Usage: jojoextract clt <file.clt> [output.png]");

    string cltPath = args[0];
    if (!File.Exists(cltPath)) return Fail($"File not found: {cltPath}");

    byte[] data = File.ReadAllBytes(cltPath);
    if (!ClutDecoder.LooksLikeClut(data))
        return Fail($"File length 0x{data.Length:X} is not a multiple of 0x{ClutDecoder.BankBytes:X}; not a CLUT.");

    string outPath = args.Length >= 2
        ? args[1]
        : Path.ChangeExtension(cltPath, ".png");

    using var img = ClutDecoder.RenderAsBanksScaled(data, cellSize: 16);
    img.SaveAsPng(outPath);
    Console.WriteLine($"Wrote {outPath} ({img.Width}x{img.Height} px, {data.Length / ClutDecoder.BankBytes} banks).");
    return 0;
}

static int CmdImage(string[] args)
{
    // image <file.pac> <pixelEntry> <clutEntry> <bpp> [width] [outputDir]
    if (args.Length < 4)
        return Fail("Usage: jojoextract image <file.pac> <pixelEntryIdx> <clutEntryIdx> <4|8> [width] [outputDir]");

    string pacPath = args[0];
    if (!int.TryParse(args[1], out int pixIdx)) return Fail($"Bad pixel index '{args[1]}'.");
    if (!int.TryParse(args[2], out int clutIdx)) return Fail($"Bad CLUT index '{args[2]}'.");
    if (!int.TryParse(args[3], out int bpp) || (bpp != 4 && bpp != 8))
        return Fail("bpp must be 4 or 8.");

    int? width = null;
    if (args.Length >= 5 && int.TryParse(args[4], out int w)) width = w;

    string outDir = args.Length >= 6
        ? args[5]
        : Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(pacPath)) ?? ".",
            Path.GetFileNameWithoutExtension(pacPath) + "_image");

    var pac = PacFile.Load(pacPath);
    if (pixIdx < 0 || pixIdx >= pac.EntryCount) return Fail($"pixel index {pixIdx} out of range (0..{pac.EntryCount - 1}).");
    if (clutIdx < 0 || clutIdx >= pac.EntryCount) return Fail($"CLUT index {clutIdx} out of range (0..{pac.EntryCount - 1}).");

    var pixEntry = pac.Entries[pixIdx];
    var clutEntry = pac.Entries[clutIdx];
    var pixels = pac.GetEntryData(pixEntry);
    var clutData = pac.GetEntryData(clutEntry);

    Directory.CreateDirectory(outDir);
    string baseName = $"{Path.GetFileNameWithoutExtension(pacPath)}_pix{pixIdx}_clut{clutIdx}";

    if (bpp == 4)
        return RenderAll4bpp(pixels, clutData, baseName, outDir, width);
    else
        return RenderAll8bpp(pixels, clutData, baseName, outDir, width);
}

static int RenderAll4bpp(ReadOnlySpan<byte> pixels, ReadOnlySpan<byte> clutData, string baseName, string outDir, int? widthOverride)
{
    if (clutData.Length < 32 || clutData.Length % 32 != 0)
        return Fail($"CLUT length 0x{clutData.Length:X} is not a multiple of 32 (one 4bpp bank).");

    int banks = clutData.Length / 32;
    int pixLen = pixels.Length;

    // Candidate widths: caller override OR all power-of-2-ish widths that divide bytesPerRow.
    int[] widths = widthOverride is int wo
        ? new[] { wo }
        : new[] { 64, 128, 256, 384, 512 }
            .Where(w => (w & 1) == 0 && pixLen % (w / 2) == 0)
            .ToArray();

    if (widths.Length == 0)
        return Fail($"No candidate width divides 0x{pixLen:X} bytes evenly. Provide one explicitly.");

    int written = 0;
    // Copy spans into arrays so we can use them inside lambdas/loops without span lifetime issues.
    byte[] pixCopy = pixels.ToArray();
    byte[] clutCopy = clutData.ToArray();

    foreach (int width in widths)
    {
        int height = pixCopy.Length / (width / 2);
        for (int b = 0; b < banks; b++)
        {
            byte[] bank = IndexedImageDecoder.GetClutBank(clutCopy, b);
            using var img = IndexedImageDecoder.Decode4bpp(pixCopy, width, bank);
            string outPath = Path.Combine(outDir, $"{baseName}_4bpp_w{width}_h{height}_bank{b}.png");
            img.SaveAsPng(outPath);
            Console.WriteLine($"  wrote {Path.GetFileName(outPath)}");
            written++;
        }
    }

    Console.WriteLine($"Wrote {written} PNG(s) to {outDir}.");
    return 0;
}

static int RenderAll8bpp(ReadOnlySpan<byte> pixels, ReadOnlySpan<byte> clutData, string baseName, string outDir, int? widthOverride)
{
    if (clutData.Length < 512)
        return Fail($"CLUT length 0x{clutData.Length:X} is too small for 8bpp (need >= 512 bytes).");

    int cluts = clutData.Length / 512;
    int pixLen = pixels.Length;

    int[] widths = widthOverride is int wo
        ? new[] { wo }
        : new[] { 64, 128, 256, 384, 512 }
            .Where(w => pixLen % w == 0)
            .ToArray();

    if (widths.Length == 0)
        return Fail($"No candidate width divides 0x{pixLen:X} bytes evenly. Provide one explicitly.");

    int written = 0;
    byte[] pixCopy = pixels.ToArray();
    byte[] clutCopy = clutData.ToArray();

    foreach (int width in widths)
    {
        int height = pixCopy.Length / width;
        for (int c = 0; c < cluts; c++)
        {
            var clut = clutCopy.AsSpan(c * 512, 512).ToArray();
            using var img = IndexedImageDecoder.Decode8bpp(pixCopy, width, clut);
            string outPath = Path.Combine(outDir, $"{baseName}_8bpp_w{width}_h{height}_clut{c}.png");
            img.SaveAsPng(outPath);
            Console.WriteLine($"  wrote {Path.GetFileName(outPath)}");
            written++;
        }
    }

    Console.WriteLine($"Wrote {written} PNG(s) to {outDir}.");
    return 0;
}

static int CmdAuto(string[] args)
{
    if (args.Length < 1) return Fail("Usage: jojoextract auto <file.pac> [outputDir]");

    string pacPath = args[0];
    var pac = PacFile.Load(pacPath);
    string outDir = args.Length >= 2
        ? args[1]
        : Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(pacPath)) ?? ".",
            Path.GetFileNameWithoutExtension(pacPath) + "_graphics");

    Directory.CreateDirectory(outDir);

    bool exportedAny = false;
    var decodedEntries = new HashSet<int>();
    var handlerErrors = new List<string>();

    if (SoundBankExporter.HasSoundBankEntries(pac))
    {
        try
        {
            Console.WriteLine("Detected 0x0400 VAB/SPU sound-bank payloads; exporting native sound parts.");
            string soundOutDir = exportedAny ? Path.Combine(outDir, "sound_bank") : outDir;
            IReadOnlyList<SoundBankExport> outputs = SoundBankExporter.Export(pac, pacPath, soundOutDir, out string manifestPath);
            foreach (SoundBankExport output in outputs)
            {
                decodedEntries.Add(output.EntryIndex);
                Console.WriteLine($"  wrote {Path.GetFileName(output.OutputPath)} ({output.Role})");
            }

            Console.WriteLine($"  wrote {Path.GetFileName(manifestPath)}");
            if (outputs.Count > 0)
                exportedAny = true;
        }
        catch (Exception ex)
        {
            handlerErrors.Add($"sound bank: {ex.Message}");
            Console.Error.WriteLine($"  [warn] sound-bank export failed: {ex.Message}");
        }
    }

    if (RuntimePoolExporter.HasRuntimePoolEntries(pac))
    {
        try
        {
            Console.WriteLine("Detected 0x0800 runtime-pool payloads; exporting native pool parts.");
            string poolOutDir = exportedAny ? Path.Combine(outDir, "runtime_pool") : outDir;
            IReadOnlyList<RuntimePoolExport> outputs = RuntimePoolExporter.Export(pac, pacPath, poolOutDir, out string manifestPath);
            foreach (RuntimePoolExport output in outputs)
            {
                decodedEntries.Add(output.EntryIndex);
                Console.WriteLine($"  wrote {Path.GetFileName(output.OutputPath)} (pool 0x{output.PoolSlot:X2})");
            }

            Console.WriteLine($"  wrote {Path.GetFileName(manifestPath)}");
            if (outputs.Count > 0)
                exportedAny = true;
        }
        catch (Exception ex)
        {
            handlerErrors.Add($"runtime pool: {ex.Message}");
            Console.Error.WriteLine($"  [warn] runtime-pool export failed: {ex.Message}");
        }
    }

    if (CompressedTimExtractor.HasCompressedTimEntries(pac))
    {
        try
        {
            Console.WriteLine("Detected 0x0122/0x0123 RAM compressed TIM payloads; decompressing and rendering TIM graphics.");
            string timOutDir = exportedAny ? Path.Combine(outDir, "compressed_tim") : outDir;
            IReadOnlyList<CompressedTimExport> outputs = CompressedTimExtractor.Export(pac, pacPath, timOutDir);
            foreach (CompressedTimExport output in outputs)
            {
                decodedEntries.Add(output.EntryIndex);
                Console.WriteLine($"  wrote {Path.GetFileName(output.TimPath)} ({output.Info.DecompressedLength} bytes decompressed TIM)");
                foreach (string pngPath in output.PngPaths)
                    Console.WriteLine($"  wrote {Path.GetFileName(pngPath)} ({output.Info.ImagePixelWidth}x{output.Info.ImagePixelHeight}, {output.Info.BitsPerPixel}bpp)");
                Console.WriteLine($"  wrote {Path.GetFileName(output.ManifestPath)}");
            }

            exportedAny = outputs.Count > 0;
        }
        catch (Exception ex)
        {
            handlerErrors.Add($"compressed TIM: {ex.Message}");
            Console.Error.WriteLine($"  [warn] compressed TIM export failed: {ex.Message}");
        }
    }

    if (CompressedTimExtractor.HasEmbeddedTimEntries(pac))
    {
        try
        {
            Console.WriteLine("Detected self-contained embedded TIM payloads; rendering TIM graphics without external CLUT pairing.");
            string embeddedOutDir = exportedAny ? Path.Combine(outDir, "embedded_tim") : outDir;
            IReadOnlyList<EmbeddedTimExport> outputs = CompressedTimExtractor.ExportEmbedded(pac, pacPath, embeddedOutDir);
            foreach (EmbeddedTimExport output in outputs)
            {
                decodedEntries.Add(output.EntryIndex);
                Console.WriteLine($"  wrote {Path.GetFileName(output.TimPath)} ({output.Info.TimLength} bytes embedded TIM)");
                foreach (string pngPath in output.PngPaths)
                    Console.WriteLine($"  wrote {Path.GetFileName(pngPath)} ({output.Info.ImagePixelWidth}x{output.Info.ImagePixelHeight}, {output.Info.BitsPerPixel}bpp)");
                Console.WriteLine($"  wrote {Path.GetFileName(output.ManifestPath)}");
            }

            if (outputs.Count > 0)
                exportedAny = true;
        }
        catch (Exception ex)
        {
            handlerErrors.Add($"embedded TIM: {ex.Message}");
            Console.Error.WriteLine($"  [warn] embedded TIM export failed: {ex.Message}");
        }
    }

    IReadOnlyList<DirectVramFrameCandidate> directFrameCandidates = DirectVramFrameRenderer.FindCandidates(pac);
    if (directFrameCandidates.Count > 0)
    {
        try
        {
            Console.WriteLine("Detected direct VRAM atlas + 12-byte frame table + CLUT records; exporting assembled coloured frames.");
            string directFrameOutDir = Path.Combine(outDir, "assembled_direct_frames");
            IReadOnlyList<DirectVramFrameExport> outputs = DirectVramFrameRenderer.ExportAll(pac, pacPath, directFrameOutDir, directFrameCandidates);
            foreach (DirectVramFrameExport output in outputs)
                Console.WriteLine($"  wrote {Path.GetFileName(output.PngPath)} ({output.Width}x{output.Height})");

            if (outputs.Count > 0)
            {
                exportedAny = true;
                foreach (DirectVramFrameCandidate candidate in directFrameCandidates)
                {
                    decodedEntries.Add(candidate.ImageEntry.Index);
                    decodedEntries.Add(candidate.FrameEntry.Index);
                    decodedEntries.Add(candidate.ClutEntry.Index);
                }
            }
        }
        catch (Exception ex)
        {
            handlerErrors.Add($"direct VRAM frames: {ex.Message}");
            Console.Error.WriteLine($"  [warn] direct VRAM frame export failed: {ex.Message}");
        }
    }

    IReadOnlyList<PackedMapCandidate> packedMapCandidates = PackedMapRenderer.FindCandidates(pac);
    if (packedMapCandidates.Count > 0)
    {
        try
        {
            Console.WriteLine("Detected FUN_8002b62c-style packed 32-bit cell maps; exporting assembled coloured map candidates.");
            string packedMapOutDir = Path.Combine(outDir, "assembled_packed_maps");
            IReadOnlyList<PackedMapExport> outputs = PackedMapRenderer.ExportAll(pac, pacPath, packedMapOutDir, packedMapCandidates);
            foreach (PackedMapExport output in outputs)
                Console.WriteLine($"  wrote {Path.GetFileName(output.PngPath)} ({output.Width}x{output.Height}, {output.WidthTiles}x{output.HeightTiles} tiles)");

            if (outputs.Count > 0)
            {
                exportedAny = true;
                foreach (PackedMapCandidate candidate in packedMapCandidates)
                {
                    decodedEntries.Add(candidate.ImageEntry.Index);
                    decodedEntries.Add(candidate.MapEntry.Index);
                    decodedEntries.Add(candidate.ClutSource.Entry.Index);
                }
            }
        }
        catch (Exception ex)
        {
            handlerErrors.Add($"packed maps: {ex.Message}");
            Console.Error.WriteLine($"  [warn] packed-map export failed: {ex.Message}");
        }
    }

    if (HasCachedFrameSet(pac))
    {
        try
        {
            Console.WriteLine("Detected cached-frame records; exporting assembled coloured 4bpp frames.");

            string renderer = KplnRenderContextFinder.TryFindDefaultCompanionPath(pacPath, side: 0) is null ? "cache" : "cache-auto-strict";
            int frameCount = KplnFramePreviewer.GetFrameCount(pac);
            int frameResult = CmdKplnFrames(new[]
            {
                pacPath,
                "0",
                frameCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Path.Combine(outDir, "assembled_frames"),
                $"0x{KplnFramePreviewer.DefaultFrameOpcode:X4}",
                "0",
                "0",
                renderer
            });

            if (frameResult == 0)
            {
                exportedAny = true;
                foreach (PacEntry entry in pac.Entries)
                {
                    ushort opcode = (ushort)(entry.Flags & 0xffff);
                    if (opcode is KplnFramePreviewer.CompressedTileOpcode or KplnFramePreviewer.DefaultFrameOpcode || opcode is >= 0x0803 and <= 0x0807)
                        decodedEntries.Add(entry.Index);
                }
            }
            else
                handlerErrors.Add($"cached frames: command returned {frameResult}");
        }
        catch (Exception ex)
        {
            handlerErrors.Add($"cached frames: {ex.Message}");
            Console.Error.WriteLine($"  [warn] cached-frame export failed: {ex.Message}");
        }
    }

    if (HasDirectVramImages(pac))
    {
        try
        {
            Console.WriteLine("Detected direct VRAM image blocks; exporting placed 4bpp grayscale previews.");
            string directOutDir = exportedAny ? Path.Combine(outDir, "direct_vram") : outDir;
            int directResult = CmdVramPreview(new[] { pacPath, "all", "4", "0", directOutDir });

            if (directResult == 0)
            {
                exportedAny = true;
                foreach (PacEntry entry in pac.Entries.Where(entry => ((ushort)(entry.Flags & 0xffff) & 0x0f00) == 0x0200))
                    decodedEntries.Add(entry.Index);
            }
            else
                handlerErrors.Add($"direct VRAM: command returned {directResult}");
        }
        catch (Exception ex)
        {
            handlerErrors.Add($"direct VRAM: {ex.Message}");
            Console.Error.WriteLine($"  [warn] direct VRAM export failed: {ex.Message}");
        }
    }

    if (pac.Entries.Any(entry => !decodedEntries.Contains(entry.Index) && RamPayloadExporter.IsRamPayloadEntry(pac, entry)))
    {
        try
        {
            Console.WriteLine("Detected undecoded 0x0100 RAM payloads; exporting native RAM parts.");
            string ramOutDir = exportedAny ? Path.Combine(outDir, "ram_payloads") : outDir;
            IReadOnlyList<RamPayloadExport> outputs = RamPayloadExporter.ExportUndecoded(pac, pacPath, ramOutDir, decodedEntries, out string? manifestPath);
            foreach (RamPayloadExport output in outputs)
            {
                decodedEntries.Add(output.EntryIndex);
                string destination = output.Info.DefaultRamDestination is uint ramDestination ? $"0x{ramDestination:X8}" : "unknown RAM";
                Console.WriteLine($"  wrote {Path.GetFileName(output.OutputPath)} ({destination})");
            }

            if (manifestPath is not null)
                Console.WriteLine($"  wrote {Path.GetFileName(manifestPath)}");

            if (outputs.Count > 0)
                exportedAny = true;
        }
        catch (Exception ex)
        {
            handlerErrors.Add($"RAM payload: {ex.Message}");
            Console.Error.WriteLine($"  [warn] RAM payload export failed: {ex.Message}");
        }
    }

    if (!exportedAny)
    {
        Console.WriteLine(handlerErrors.Count == 0
            ? "No code-backed graphical payloads recognized yet; extracting raw PAC entries."
            : "No decoded handler completed; extracting raw PAC entries.");
        ExportRawEntriesForAuto(pacPath, outDir, pac);
    }
    else if (handlerErrors.Count > 0)
    {
        Console.WriteLine("One or more decoded handlers did not complete; also extracting raw PAC entries for inspection.");
        ExportRawEntriesForAuto(pacPath, Path.Combine(outDir, "raw_entries"), pac);
    }
    else if (decodedEntries.Count < pac.EntryCount)
    {
        Console.WriteLine("Some PAC entries remain undecoded by current handlers; also extracting raw PAC entries for inspection.");
        ExportRawEntriesForAuto(pacPath, Path.Combine(outDir, "raw_entries"), pac);
    }

    return 0;
}

static void ExportRawEntriesForAuto(string pacPath, string outDir, PacFile pac)
{
    ExtractOne(pacPath, outDir, verbose: false);
    string manifestPath = WriteRawAutoManifest(pacPath, outDir, pac);
    Console.WriteLine($"  wrote {Path.GetFileName(manifestPath)}");
    Console.WriteLine($"Extracted {pac.EntryCount} raw PAC entr{(pac.EntryCount == 1 ? "y" : "ies")} to {outDir}.");
}

static int CmdPackedMap(string[] args)
{
    if (args.Length < 1) return Fail("Usage: jojoextract packed-map <file.pac> [outputDir]");

    string pacPath = args[0];
    if (!File.Exists(pacPath))
        return Fail($"PAC file not found: {pacPath}");

    string outDir = args.Length >= 2
        ? args[1]
        : Path.Combine(Path.GetDirectoryName(pacPath) ?? ".", Path.GetFileNameWithoutExtension(pacPath) + "_packed_maps");

    PacFile pac = PacFile.Load(pacPath);
    IReadOnlyList<PackedMapCandidate> candidates = PackedMapRenderer.FindCandidates(pac);
    if (candidates.Count == 0)
        return Fail("No FUN_8002b62c-style packed-map candidate was found in this PAC.");

    IReadOnlyList<PackedMapExport> outputs = PackedMapRenderer.ExportAll(pac, pacPath, outDir, candidates);
    foreach (PackedMapExport output in outputs)
        Console.WriteLine($"wrote {output.PngPath} ({output.Width}x{output.Height}, {output.WidthTiles}x{output.HeightTiles} tiles)");

    return 0;
}

static string WriteRawAutoManifest(string pacPath, string outDir, PacFile pac)
{
    string baseName = Path.GetFileNameWithoutExtension(pacPath);
    string manifestPath = Path.Combine(outDir, baseName + "_raw_manifest.txt");
    var lines = new List<string>
    {
        $"source_pac={pacPath}",
        $"total_size=0x{pac.TotalSize:X}",
        $"entry_count={pac.EntryCount}",
        $"vm_profile={GetOpcodeProfile(pac)}",
        "mode=raw-pac-entry-extraction",
        "code_evidence=PAC payload extraction is backed by the loader VM directory consumed by FUN_80018470/FUN_800184c0: records start at file offset 0x08, payloads start at 0x800, and each payload advances by the sector-aligned record length.",
        string.Empty,
        "entries:"
    };

    foreach (PacEntry entry in pac.Entries)
    {
        ushort opcode = (ushort)(entry.Flags & 0xffff);
        lines.Add($"entry={entry.Index} flags=0x{entry.Flags:X8} opcode=0x{opcode:X4} length=0x{entry.DataLength:X} payload_offset=0x{entry.DataOffset:X}");
    }

    File.WriteAllText(manifestPath, string.Join(Environment.NewLine, lines) + Environment.NewLine);
    return manifestPath;
}

static int CmdAutoBatch(string[] args)
{
    if (args.Length < 1) return Fail("Usage: jojoextract auto-batch <inputDir> [outputDir] [vmProfile]");

    string inputDir = args[0];
    if (!Directory.Exists(inputDir))
        return Fail($"Input directory not found: {inputDir}");

    string outputRoot = args.Length >= 2
        ? args[1]
        : Path.Combine(inputDir, "_graphics");
    string? profileFilter = args.Length >= 3 ? args[2] : null;

    Directory.CreateDirectory(outputRoot);

    string[] files = Directory.GetFiles(inputDir, "*.PAC", SearchOption.TopDirectoryOnly)
        .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    int selected = 0;
    int ok = 0;
    int failed = 0;
    int skipped = 0;
    foreach (string file in files)
    {
        PacFile pac;
        try
        {
            pac = PacFile.Load(file);
        }
        catch (Exception ex)
        {
            failed++;
            Console.Error.WriteLine($"  [FAIL] {Path.GetFileName(file)}: {ex.Message}");
            continue;
        }

        string profile = GetOpcodeProfile(pac);
        if (profileFilter is not null && !profile.Equals(profileFilter, StringComparison.OrdinalIgnoreCase))
            continue;

        selected++;
        string outDir = Path.Combine(outputRoot, Path.GetFileNameWithoutExtension(file));
        Console.WriteLine($"[{selected}] {Path.GetFileName(file)} profile={profile}");
        int result;
        try
        {
            result = CmdAuto(new[] { file, outDir });
        }
        catch (Exception ex)
        {
            failed++;
            Console.Error.WriteLine($"  [FAIL] {Path.GetFileName(file)}: {ex.Message}");
            continue;
        }

        if (result == 0)
        {
            ok++;
        }
        else
        {
            skipped++;
            Console.Error.WriteLine($"  [SKIP] {Path.GetFileName(file)}: no successful auto extraction yet.");
        }
    }

    Console.WriteLine($"Auto batch done. Selected: {selected}  Succeeded: {ok}  Skipped: {skipped}  Failed: {failed}  Output: {outputRoot}");
    return failed == 0 && skipped == 0 ? 0 : 1;
}

static string GetOpcodeProfile(PacFile pac)
{
    return string.Join(' ', pac.Entries.Select(entry => (((ushort)(entry.Flags & 0xffff) & 0x0f00) >> 8).ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));
}

static bool HasCachedFrameSet(PacFile pac)
{
    return HasOpcode(pac, KplnFramePreviewer.DefaultFrameOpcode)
        && HasOpcode(pac, KplnFramePreviewer.CompressedTileOpcode)
        && HasOpcode(pac, 0x0803)
        && HasOpcode(pac, 0x0804)
        && HasOpcode(pac, 0x0805)
        && HasOpcode(pac, 0x0806)
        && HasOpcode(pac, 0x0807);
}

static bool HasDirectVramImages(PacFile pac)
{
    return pac.Entries.Any(entry => ((ushort)(entry.Flags & 0xffff) & 0x0f00) == 0x0200);
}

static bool HasOpcode(PacFile pac, ushort opcode)
{
    return pac.Entries.Any(entry => (ushort)(entry.Flags & 0xffff) == opcode);
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}

static void PrintUsage()
{
    Console.WriteLine("""
        jojoextract - JoJo's Bizarre Adventure (SLES_025.99) asset extractor

        Usage:
          jojoextract info    <file.pac>
              Print the entry directory of a single PAC file.

          jojoextract vm      <file.pac> [poolOffset]
              Interpret the PAC directory as the Ghidra-verified loader VM
              record table. Shows opcode class, payload offset, transfer
              lengths, and known runtime pool-slot effects for KPLN/PLK files.

          jojoextract report <file.pac|directory>
              Summarize code-backed file mapping evidence, PAC VM opcode
              profile, currently proven extractor handlers, direct VRAM placed
              dimensions, and unresolved parser work. Directory mode prints a
              one-line summary for every PAC in the folder.

          jojoextract kpln-clut <KPLNxx.PAC> [paletteId|all] [outputDir]
              Reconstruct the runtime CLUT upload rows used by FUN_800195c8
              from KPLN opcodes 0x0803..0x0807 and write preview PNGs. This is
              a focused debug command; `auto` is the general extraction command.

          jojoextract vram-preview <file.pac> [entryIdx|auto|all] [4|8|both] [tableOffset] [output.png|outputDir]
              Render Ghidra-verified 0x0200 direct LoadImage payloads as
              grayscale indexed VRAM texture previews using DAT_8005991c layout
              records. `auto`/`all` renders every direct image block in the PAC;
              a numeric entry index renders one block. The default indexed depth
              is 4bpp; use `both` only for comparison/debugging.

          jojoextract packed-map <file.pac> [outputDir]
              Render FUN_8002b62c-style packed 32-bit cell maps. The low cell
              halfword is treated as the PSX CLUT coordinate and the high
              halfword supplies the 16x16 texture u/v and texture-page bucket.
              This is a focused validation command for PSJ/KPSJ/KSDM-style maps.

          jojoextract kpln-preview <KPLNxx.PAC> [outputDir]
              Generate a small debug bundle containing KPLN runtime CLUT previews
              and grayscale 4bpp direct VRAM texture previews. This is a focused
              debug command; `auto` is the general extraction command.

          jojoextract kpln-frames <KPLNxx.PAC> [first] [count] [outputDir] [frameOpcode] [paletteId] [side] [cache|cache-auto|direct]
              Render color composited KPLN previews. The default cache renderer
              follows FUN_8001f9b4/FUN_800205dc using 0x0802 frame records and
              0x0801 compressed tiles. This is a focused debug command; `auto`
              detects the same cached-frame records without requiring this command.

          jojoextract extract <file.pac> [outputDir]
              Extract every entry of a PAC as a raw .bin file.
              Default output: <pac-folder>/<pac-name>_extracted/

          jojoextract batch   <inputDir>  [outputDir]
              Run extract on every *.PAC in <inputDir>.
              Default output: <inputDir>/_extracted/

          jojoextract palettes <file.pac> [outputDir]
              Render every PAC entry whose length is a multiple of 32 bytes
              (one 16-colour CLUT bank) as a PNG palette image.

          jojoextract clt <file.clt> [output.png]
              Decode a standalone .CLT palette file from the C/ folder.

          jojoextract image <file.pac> <pixelEntryIdx> <clutEntryIdx> <4|8> [width] [outputDir]
              EXPERIMENTAL: decode an indexed-colour image from one PAC entry
              using a CLUT from another. If `width` is omitted, the tool tries
              several candidate widths that evenly divide the buffer; if `bpp=4`
              one PNG is written per CLUT bank, if `bpp=8` one per 256-colour
              CLUT block. Choose the visually correct one and discard the rest.

          jojoextract auto <file.pac> [outputDir]
              Unified PAC graphics export. The command inspects VM opcodes and
              runs every code-backed extractor whose records are present: cached
              4bpp frame assembly, direct tile-word frame assembly, direct 4bpp
              VRAM image previews,
              0x0122/0x0123 RAM-loaded compressed TIM graphics,
              self-contained embedded TIM payloads, and future handlers as
              they are proven. CLUT association is intentionally not inferred
              for generic direct blocks. If no decoded graphics handler is
              proven yet, or if some records are still not consumed by proven
              handlers, `auto` extracts raw PAC entries with a manifest instead
              of guessing a format.

          jojoextract auto-batch <inputDir> [outputDir] [vmProfile]
              Run `auto` for every PAC in a directory, optionally restricted to
              an exact VM profile such as `01`. Default output:
              <inputDir>/_graphics/

        Notes:
          - `extract` writes PAC payloads verbatim; `vram-preview` and `auto`
            render direct LoadImage payloads whose opcode class is known from
            the loader VM path.
          - Each output file name encodes the entry index and the 32-bit Flags
            field from the PAC directory; this aids format classification.
        """);
}
