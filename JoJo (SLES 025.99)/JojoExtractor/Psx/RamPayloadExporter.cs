using System.Globalization;
using JojoExtractor.Pac;

namespace JojoExtractor.Psx;

public sealed record RamPayloadPartInfo(
    int EntryIndex,
    ushort Opcode,
    uint Length,
    uint StrideLength,
    uint? DefaultRamDestination);

public sealed record RamPayloadExport(
    int EntryIndex,
    ushort Opcode,
    string OutputPath,
    RamPayloadPartInfo Info);

public static class RamPayloadExporter
{
    public static bool IsRamPayloadEntry(PacFile pac, PacEntry entry)
    {
        ushort opcode = (ushort)(entry.Flags & 0xffff);
        if ((opcode & 0x0f00) != 0x0100)
            return false;

        return !CompressedTimExtractor.IsCompressedTimOpcode(opcode)
            && !CompressedTimExtractor.IsEmbeddedTimEntry(pac, entry);
    }

    public static IReadOnlyList<RamPayloadPartInfo> Inspect(PacFile pac)
    {
        return pac.Entries
            .Where(entry => IsRamPayloadEntry(pac, entry))
            .Select(GetPartInfo)
            .ToArray();
    }

    public static IReadOnlyList<RamPayloadExport> ExportUndecoded(
        PacFile pac,
        string pacPath,
        string outDir,
        IReadOnlySet<int> decodedEntryIndexes,
        out string? manifestPath)
    {
        RamPayloadPartInfo[] parts = pac.Entries
            .Where(entry => !decodedEntryIndexes.Contains(entry.Index) && IsRamPayloadEntry(pac, entry))
            .Select(GetPartInfo)
            .ToArray();

        if (parts.Length == 0)
        {
            manifestPath = null;
            return Array.Empty<RamPayloadExport>();
        }

        Directory.CreateDirectory(outDir);

        string baseName = Path.GetFileNameWithoutExtension(pacPath);
        var outputs = new List<RamPayloadExport>();
        foreach (RamPayloadPartInfo part in parts)
        {
            PacEntry entry = pac.Entries[part.EntryIndex];
            string fileName = $"{baseName}_entry{part.EntryIndex:D2}_opcode{part.Opcode:X4}.ram.bin";
            string outputPath = Path.Combine(outDir, fileName);
            File.WriteAllBytes(outputPath, pac.GetEntryData(entry).ToArray());
            outputs.Add(new RamPayloadExport(part.EntryIndex, part.Opcode, outputPath, part));
        }

        manifestPath = Path.Combine(outDir, baseName + "_ram_payload_manifest.txt");
        File.WriteAllText(manifestPath, BuildManifest(pacPath, pac, parts, outputs));
        return outputs;
    }

    private static RamPayloadPartInfo GetPartInfo(PacEntry entry)
    {
        ushort opcode = (ushort)(entry.Flags & 0xffff);
        return new RamPayloadPartInfo(
            entry.Index,
            opcode,
            entry.DataLength,
            (entry.DataLength + 3U) & 0xffff_fffcU,
            CompressedTimExtractor.GetDefaultRamDestination(opcode));
    }

    private static string BuildManifest(
        string pacPath,
        PacFile pac,
        IReadOnlyList<RamPayloadPartInfo> parts,
        IReadOnlyList<RamPayloadExport> outputs)
    {
        var lines = new List<string>
        {
            $"source_pac={pacPath}",
            $"total_size=0x{pac.TotalSize:X}",
            $"entry_count={pac.EntryCount.ToString(CultureInfo.InvariantCulture)}",
            "mode=native-ram-payload-export",
            "code_evidence=FUN_800184c0 routes opcode class 0x0100 through PTR_DAT_8005988c using (opcode low byte + state byte 0x7c). The loader copies these payloads to the resolved RAM destination and advances by the aligned stride. Entries already decoded by more specific TIM/frame handlers are not duplicated here; remaining records are exported as native RAM payloads until their consumer is traced.",
            string.Empty,
            "entries:"
        };

        foreach (RamPayloadPartInfo part in parts)
        {
            RamPayloadExport? output = outputs.FirstOrDefault(item => item.EntryIndex == part.EntryIndex);
            string path = output is null ? "-" : output.OutputPath;
            string destination = part.DefaultRamDestination is uint ramDestination
                ? $"0x{ramDestination:X8}"
                : "unknown";
            lines.Add(
                $"entry={part.EntryIndex.ToString(CultureInfo.InvariantCulture)} opcode=0x{part.Opcode:X4} length=0x{part.Length:X} aligned_stride=0x{part.StrideLength:X} default_ram_destination={destination} output={path}");
        }

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }
}
