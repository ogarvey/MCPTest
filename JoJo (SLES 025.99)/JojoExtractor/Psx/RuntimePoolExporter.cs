using System.Globalization;
using JojoExtractor.Pac;

namespace JojoExtractor.Psx;

public sealed record RuntimePoolPartInfo(
    int EntryIndex,
    ushort Opcode,
    uint Length,
    int PoolSlot,
    uint StrideLength);

public sealed record RuntimePoolExport(
    int EntryIndex,
    ushort Opcode,
    int PoolSlot,
    string OutputPath);

public static class RuntimePoolExporter
{
    public static bool HasRuntimePoolEntries(PacFile pac) =>
        pac.Entries.Any(IsRuntimePoolEntry);

    public static bool IsRuntimePoolEntry(PacEntry entry) =>
        (((ushort)(entry.Flags & 0xffff)) & 0x0f00) == 0x0800;

    public static IReadOnlyList<RuntimePoolPartInfo> Inspect(PacFile pac)
    {
        return pac.Entries
            .Where(IsRuntimePoolEntry)
            .Select(entry =>
            {
                ushort opcode = (ushort)(entry.Flags & 0xffff);
                return new RuntimePoolPartInfo(
                    entry.Index,
                    opcode,
                    entry.DataLength,
                    opcode & 0xff,
                    (entry.DataLength + 3U) & 0xffff_fffcU);
            })
            .ToArray();
    }

    public static IReadOnlyList<RuntimePoolExport> Export(PacFile pac, string pacPath, string outDir, out string manifestPath)
    {
        Directory.CreateDirectory(outDir);

        string baseName = Path.GetFileNameWithoutExtension(pacPath);
        RuntimePoolPartInfo[] parts = Inspect(pac).ToArray();
        var outputs = new List<RuntimePoolExport>();

        foreach (RuntimePoolPartInfo part in parts)
        {
            PacEntry entry = pac.Entries[part.EntryIndex];
            string fileName = $"{baseName}_entry{part.EntryIndex:D2}_opcode{part.Opcode:X4}_pool{part.PoolSlot:X2}.pool.bin";
            string outputPath = Path.Combine(outDir, fileName);
            File.WriteAllBytes(outputPath, pac.GetEntryData(entry).ToArray());
            outputs.Add(new RuntimePoolExport(part.EntryIndex, part.Opcode, part.PoolSlot, outputPath));
        }

        manifestPath = Path.Combine(outDir, baseName + "_runtime_pool_manifest.txt");
        File.WriteAllText(manifestPath, BuildManifest(pacPath, pac, parts, outputs));
        return outputs;
    }

    private static string BuildManifest(
        string pacPath,
        PacFile pac,
        IReadOnlyList<RuntimePoolPartInfo> parts,
        IReadOnlyList<RuntimePoolExport> outputs)
    {
        var lines = new List<string>
        {
            $"source_pac={pacPath}",
            $"total_size=0x{pac.TotalSize:X}",
            $"entry_count={pac.EntryCount.ToString(CultureInfo.InvariantCulture)}",
            "mode=native-runtime-pool-export",
            "code_evidence=FUN_800184c0 routes opcode class 0x0800 through PTR_DAT_80059bf4 using (opcode low byte + state byte 0x7f). It stores the resolved source pointer in DAT_80079928[slot] and the aligned payload stride in DAT_800799b0[slot]. These files are exported as native runtime-pool payloads; no higher-level image, script, or table consumer is inferred here.",
            string.Empty,
            "entries:"
        };

        foreach (RuntimePoolPartInfo part in parts)
        {
            RuntimePoolExport? output = outputs.FirstOrDefault(item => item.EntryIndex == part.EntryIndex);
            string path = output is null ? "-" : output.OutputPath;
            lines.Add(
                $"entry={part.EntryIndex.ToString(CultureInfo.InvariantCulture)} opcode=0x{part.Opcode:X4} pool_slot_low=0x{part.PoolSlot:X2} length=0x{part.Length:X} aligned_stride=0x{part.StrideLength:X} output={path}");
        }

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }
}
