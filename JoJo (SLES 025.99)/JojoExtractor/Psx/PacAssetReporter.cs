using System.Globalization;
using System.Text.RegularExpressions;
using JojoExtractor.Pac;

namespace JojoExtractor.Psx;

public sealed record PacMappingEvidence(string Confidence, string Source, string Details);

public sealed record PacEntryClassSummary(ushort OpcodeClass, string Name, int Count, long TotalLength);

public sealed record PacDirectVramRecord(
    int EntryIndex,
    ushort Opcode,
    uint Length,
    int? LayoutIndex,
    int? Width,
    int? Height,
    string? Error);

public sealed record PacCompressedTimRecord(
    int EntryIndex,
    ushort Opcode,
    uint Length,
    uint? RamDestination,
    string Format);

public sealed record PacEntryCandidate(int EntryIndex, ushort Opcode, uint Length, int BankCount);

public sealed record PacSoundBankRecord(
    int EntryIndex,
    ushort Opcode,
    uint Length,
    string Role,
    bool HasVabHeaderSignature);

public sealed record PacRuntimePoolRecord(
    int EntryIndex,
    ushort Opcode,
    uint Length,
    int PoolSlot,
    uint StrideLength);

public sealed record PacRamPayloadRecord(
    int EntryIndex,
    ushort Opcode,
    uint Length,
    uint StrideLength,
    uint? DefaultRamDestination);

public sealed record PacAssetReport(
    string FileName,
    uint TotalSize,
    int EntryCount,
    string OpcodeProfile,
    IReadOnlyList<PacMappingEvidence> MappingEvidence,
    IReadOnlyList<PacEntryClassSummary> ClassSummaries,
    IReadOnlyList<PacDirectVramRecord> DirectVramRecords,
    IReadOnlyList<PacCompressedTimRecord> CompressedTimRecords,
    IReadOnlyList<PacSoundBankRecord> SoundBankRecords,
    IReadOnlyList<PacRuntimePoolRecord> RuntimePoolRecords,
    IReadOnlyList<PacRamPayloadRecord> RamPayloadRecords,
    IReadOnlyList<PacEntryCandidate> PaletteBankCandidates,
    IReadOnlyList<string> ProvenHandlers,
    IReadOnlyList<string> PendingWork)
{
    public string MappingSummary => MappingEvidence.FirstOrDefault()?.Confidence ?? "unproven";
}

public static class PacAssetReporter
{
    private static readonly Regex KplnName = new(@"^KPLN(?<id>[0-9A-F]{2})\.PAC$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex PlkName = new(@"^PLK(?<id>[0-9A-F]{2})\.PAC$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex KmnName = new(@"^KMN(?<id>[0-9]{2})\.PAC$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex PsjName = new(@"^PSJ_(?<id>[0-9A-F]{3})\.PAC$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static PacAssetReport Analyze(string pacPath, PacFile pac)
    {
        string fileName = Path.GetFileName(pacPath);
        PacVmEntry[] vmEntries = pac.Entries.Select(PacVmEntry.FromPacEntry).ToArray();

        return new PacAssetReport(
            fileName,
            pac.TotalSize,
            (int)pac.EntryCount,
            BuildOpcodeProfile(vmEntries),
            BuildMappingEvidence(fileName),
            BuildClassSummaries(vmEntries),
            BuildDirectVramRecords(pac),
            BuildCompressedTimRecords(pac),
            BuildSoundBankRecords(pac),
            BuildRuntimePoolRecords(pac),
            BuildRamPayloadRecords(pac),
            BuildPaletteCandidates(vmEntries),
            BuildProvenHandlers(fileName, pac),
            BuildPendingWork(fileName, vmEntries, pac));
    }

    public static string GetShortHandlerName(string handler)
    {
        if (handler.Contains("raw PAC entry", StringComparison.OrdinalIgnoreCase))
            return "raw-entries";
        if (handler.Contains("direct VRAM frame", StringComparison.OrdinalIgnoreCase))
            return "direct-vram-frames";
        if (handler.Contains("cached", StringComparison.OrdinalIgnoreCase))
            return "cached-frames";
        if (handler.Contains("direct", StringComparison.OrdinalIgnoreCase))
            return "direct-vram";
        if (handler.Contains("embedded TIM", StringComparison.OrdinalIgnoreCase))
            return "embedded-tim";
        if (handler.Contains("compressed TIM", StringComparison.OrdinalIgnoreCase))
            return "compressed-tim";
        if (handler.Contains("sound-bank", StringComparison.OrdinalIgnoreCase))
            return "sound-bank";
        if (handler.Contains("runtime-pool", StringComparison.OrdinalIgnoreCase))
            return "runtime-pool";
        if (handler.Contains("RAM payload", StringComparison.OrdinalIgnoreCase))
            return "ram-payloads";
        if (handler.Contains("CLUT", StringComparison.OrdinalIgnoreCase))
            return "kpln-clut";

        return handler;
    }

    private static string BuildOpcodeProfile(IReadOnlyList<PacVmEntry> vmEntries)
    {
        return string.Join(' ', vmEntries.Select(entry => (entry.OpcodeClass >> 8).ToString("X2", CultureInfo.InvariantCulture)));
    }

    private static IReadOnlyList<PacMappingEvidence> BuildMappingEvidence(string fileName)
    {
        var evidence = new List<PacMappingEvidence>();

        Match kpln = KplnName.Match(fileName);
        if (kpln.Success && TryParseHexByte(kpln.Groups["id"].Value, out int kplnIndex) && kplnIndex <= 0x19)
        {
            int fileId = 0x0114 + kplnIndex;
            evidence.Add(new PacMappingEvidence(
                "code-backed exact",
                "FUN_80019914 -> DAT_8005a0c8",
                $"character image-pool stream loads fileId 0x{fileId:X4}; side 0/1 caller contexts use M/PL{kpln.Groups["id"].Value.ToUpperInvariant()}.BIN and M/PL{kpln.Groups["id"].Value.ToUpperInvariant()}X.BIN."));
        }

        Match kmn = KmnName.Match(fileName);
        if (kmn.Success && int.TryParse(kmn.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int kmnNumber) && kmnNumber is >= 7 and <= 9)
        {
            int fileId = 0x00f5 + (kmnNumber - 7);
            string loader = kmnNumber switch
            {
                7 => "FUN_8001b148",
                8 => "FUN_8001b29c",
                _ => "FUN_8001b450"
            };
            evidence.Add(new PacMappingEvidence(
                "code-backed exact",
                "FUN_8001b0c0 KMN dispatch",
                $"{loader} calls FUN_8001eda0(0x{fileId:X4}); file-table length/order maps this ID to {fileName}."));
        }

        Match plk = PlkName.Match(fileName);
        if (plk.Success)
        {
            evidence.Add(new PacMappingEvidence(
                "code-backed family",
                "FUN_80019b8c -> DAT_8005a198",
                "extra/object image-pool stream indexes the PLK file-ID table and writes runtime pool slots 0x10/0x11 plus caller offset; exact per-file row has known repeats/fallbacks and is not embedded here yet."));
        }

        if (PsjName.IsMatch(fileName))
        {
            evidence.Add(new PacMappingEvidence(
                "candidate only",
                "DAT_8005a208 stage CLUT/table sequence",
                "stage PAC size/order matches are strong but not unique enough to use as an exact extractor mapping yet."));
        }

        if (fileName.Equals("COCKPIT.PAC", StringComparison.OrdinalIgnoreCase))
        {
            evidence.Add(new PacMappingEvidence(
                "code-backed exact",
                "FUN_80019e4c -> FUN_8001eda0(0x001A)",
                "file-table LBA/length sequence maps fileId 0x001A to COCKPIT.PAC; entry 0x0101 populates DAT_8010D800, which FUN_80019e4c uploads to CLUT rows 0x1EB..0x1ED."));
        }

        if (evidence.Count == 0)
        {
            evidence.Add(new PacMappingEvidence(
                "unproven",
                "no embedded bridge",
                "no code-backed file-ID/name bridge is implemented for this PAC yet; this means unclassified, not unused."));
        }

        return evidence;
    }

    private static IReadOnlyList<PacEntryClassSummary> BuildClassSummaries(IReadOnlyList<PacVmEntry> vmEntries)
    {
        return vmEntries
            .GroupBy(entry => entry.OpcodeClass)
            .OrderBy(group => group.Key)
            .Select(group => new PacEntryClassSummary(
                group.Key,
                PacVmEntry.GetClassName(group.Key),
                group.Count(),
                group.Sum(entry => (long)entry.DataLength)))
            .ToArray();
    }

    private static IReadOnlyList<PacDirectVramRecord> BuildDirectVramRecords(PacFile pac)
    {
        var records = new List<PacDirectVramRecord>();
        foreach (PacEntry entry in pac.Entries.Where(entry => (((ushort)(entry.Flags & 0xffff)) & 0x0f00) == 0x0200))
        {
            ushort opcode = (ushort)(entry.Flags & 0xffff);
            try
            {
                var size = VramTexturePreviewer.GetPlacedSize(pac, entry, bitsPerPixel: 4);
                records.Add(new PacDirectVramRecord(entry.Index, opcode, entry.DataLength, size.Layout.TableIndex, size.Width, size.Height, null));
            }
            catch (Exception ex)
            {
                records.Add(new PacDirectVramRecord(entry.Index, opcode, entry.DataLength, null, null, null, ex.Message));
            }
        }

        return records;
    }

    private static IReadOnlyList<PacCompressedTimRecord> BuildCompressedTimRecords(PacFile pac)
    {
        return pac.Entries
            .Where(CompressedTimExtractor.IsCompressedTimEntry)
            .Select(entry =>
            {
                ushort opcode = (ushort)(entry.Flags & 0xffff);
                return new PacCompressedTimRecord(
                    entry.Index,
                    opcode,
                    entry.DataLength,
                    CompressedTimExtractor.GetDefaultRamDestination(opcode),
                    "FUN_800267a8-compressed TIM uploaded by FUN_80025e64");
            })
            .ToArray();
    }

    private static IReadOnlyList<PacSoundBankRecord> BuildSoundBankRecords(PacFile pac)
    {
        return SoundBankExporter.Inspect(pac)
            .Select(part => new PacSoundBankRecord(
                part.EntryIndex,
                part.Opcode,
                part.Length,
                part.Role,
                part.HasVabHeaderSignature))
            .ToArray();
    }

    private static IReadOnlyList<PacRuntimePoolRecord> BuildRuntimePoolRecords(PacFile pac)
    {
        return RuntimePoolExporter.Inspect(pac)
            .Select(part => new PacRuntimePoolRecord(
                part.EntryIndex,
                part.Opcode,
                part.Length,
                part.PoolSlot,
                part.StrideLength))
            .ToArray();
    }

    private static IReadOnlyList<PacRamPayloadRecord> BuildRamPayloadRecords(PacFile pac)
    {
        return RamPayloadExporter.Inspect(pac)
            .Select(part => new PacRamPayloadRecord(
                part.EntryIndex,
                part.Opcode,
                part.Length,
                part.StrideLength,
                part.DefaultRamDestination))
            .ToArray();
    }

    private static IReadOnlyList<PacEntryCandidate> BuildPaletteCandidates(IReadOnlyList<PacVmEntry> vmEntries)
    {
        return vmEntries
            .Where(entry => !CompressedTimExtractor.IsCompressedTimOpcode(entry.Opcode) && entry.OpcodeClass is not 0x0200 and not 0x0400 && entry.DataLength >= ClutDecoder.BankBytes && entry.DataLength % ClutDecoder.BankBytes == 0)
            .Select(entry => new PacEntryCandidate(entry.Index, entry.Opcode, entry.DataLength, (int)(entry.DataLength / ClutDecoder.BankBytes)))
            .ToArray();
    }

    private static IReadOnlyList<string> BuildProvenHandlers(string fileName, PacFile pac)
    {
        var handlers = new List<string>();

        if (HasCachedFrameSet(pac))
            handlers.Add("auto: assembled coloured KPLN-style cached 4bpp frames (FUN_8001f9b4/FUN_8002078c).");

        if (HasDirectVramImages(pac))
            handlers.Add("auto: placed direct 4bpp VRAM previews (FUN_800184c0/FUN_8001902c).");

        if (DirectVramFrameRenderer.FindCandidates(pac).Count > 0)
            handlers.Add("auto: assembled direct VRAM frames from direct tile-word records (FUN_80020b74/FUN_800209ec/FUN_8001ffd4).");

        if (PackedMapRenderer.FindCandidates(pac).Count > 0)
            handlers.Add("auto: assembled packed direct VRAM maps from 32-bit cell records (FUN_8002b62c).");

        if (CompressedTimExtractor.HasCompressedTimEntries(pac))
            handlers.Add("auto: decompressed TIM graphics from 0x0122/0x0123 RAM payloads (FUN_800184c0/FUN_80025e64/FUN_800267a8).");

        if (CompressedTimExtractor.HasEmbeddedTimEntries(pac))
            handlers.Add("auto: self-contained embedded TIM payloads with internal image/CLUT blocks; no external CLUT pairing inferred.");

        if (SoundBankExporter.HasSoundBankEntries(pac))
            handlers.Add("auto: native VAB/SPU sound-bank parts from 0x0400 loader records (FUN_800184c0/SsVabOpenHeadSticky).");

        if (RuntimePoolExporter.HasRuntimePoolEntries(pac))
            handlers.Add("auto: native runtime-pool payloads from 0x0800 loader records (FUN_800184c0/DAT_80079928/DAT_800799b0).");

        if (RamPayloadExporter.Inspect(pac).Count > 0)
            handlers.Add("auto: native RAM payloads from undecoded 0x0100 loader records (FUN_800184c0/PTR_DAT_8005988c).");

        if (IsKplnFile(fileName) && HasKplnClutPool(pac))
            handlers.Add("debug: KPLN runtime CLUT previews from pool opcodes 0x0803..0x0807 (FUN_800195c8).");

        if (handlers.Count == 0)
            handlers.Add("auto: raw PAC entry extraction from loader VM directory records (FUN_80018470/FUN_800184c0). No decoded graphics format inferred yet.");

        return handlers;
    }

    private static IReadOnlyList<string> BuildPendingWork(string fileName, IReadOnlyList<PacVmEntry> vmEntries, PacFile pac)
    {
        var pending = new List<string>();
        bool hasDirectVramFrame = DirectVramFrameRenderer.FindCandidates(pac).Count > 0;
        bool hasPackedMap = PackedMapRenderer.FindCandidates(pac).Count > 0;
        bool hasSoundBank = SoundBankExporter.HasSoundBankEntries(pac);
        bool hasRuntimePool = RuntimePoolExporter.HasRuntimePoolEntries(pac);
        bool hasRamPayload = RamPayloadExporter.Inspect(pac).Count > 0;

        if (vmEntries.Any(entry => entry.OpcodeClass == 0x0100 && !CompressedTimExtractor.IsCompressedTimOpcode(entry.Opcode)))
            pending.Add("0x0100 pointer/RAM records are present; final interpretation depends on the consumer that reads the destination table.");

        if (vmEntries.Any(entry => entry.OpcodeClass == 0x0800) && hasRuntimePool && !HasCachedFrameSet(pac) && !(IsKplnFile(fileName) && HasKplnClutPool(pac)))
            pending.Add("0x0800 runtime-pool records are exported natively; any higher-level image/CLUT/script role still depends on a traced consumer for this PAC pattern.");

        if (vmEntries.Any(entry => entry.OpcodeClass == 0x0400) && !hasSoundBank)
            pending.Add("0x0400 records are present; current notes identify this as audio/table dispatch, not a graphics extractor path yet.");

        if (!hasDirectVramFrame && !hasPackedMap && BuildPaletteCandidates(vmEntries).Count > 0)
            pending.Add("palette-bank-compatible entries are format candidates only; report does not pair them with image data without caller code.");

        if (!HasCachedFrameSet(pac) && !HasDirectVramImages(pac) && !CompressedTimExtractor.HasCompressedTimEntries(pac) && !CompressedTimExtractor.HasEmbeddedTimEntries(pac) && !hasSoundBank && !hasRuntimePool && !hasRamPayload)
            pending.Add("no currently proven graphics handler matches this PAC's full VM pattern.");

        return pending;
    }

    private static bool HasCachedFrameSet(PacFile pac)
    {
        return HasOpcode(pac, KplnFramePreviewer.DefaultFrameOpcode)
            && HasOpcode(pac, KplnFramePreviewer.CompressedTileOpcode)
            && HasKplnClutPool(pac);
    }

    private static bool HasKplnClutPool(PacFile pac)
    {
        return HasOpcode(pac, 0x0803)
            && HasOpcode(pac, 0x0804)
            && HasOpcode(pac, 0x0805)
            && HasOpcode(pac, 0x0806)
            && HasOpcode(pac, 0x0807);
    }

    private static bool HasDirectVramImages(PacFile pac)
    {
        return pac.Entries.Any(entry => ((ushort)(entry.Flags & 0xffff) & 0x0f00) == 0x0200);
    }

    private static bool HasOpcode(PacFile pac, ushort opcode)
    {
        return pac.Entries.Any(entry => (ushort)(entry.Flags & 0xffff) == opcode);
    }

    private static bool IsKplnFile(string fileName)
    {
        return KplnName.IsMatch(fileName);
    }

    private static bool TryParseHexByte(string text, out int value)
    {
        return int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }
}
