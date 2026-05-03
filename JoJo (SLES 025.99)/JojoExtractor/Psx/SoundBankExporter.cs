using System.Globalization;
using JojoExtractor.Pac;

namespace JojoExtractor.Psx;

public sealed record SoundBankPartInfo(
    int EntryIndex,
    ushort Opcode,
    uint Length,
    string Role,
    string Extension,
    bool HasVabHeaderSignature);

public sealed record SoundBankExport(
    int EntryIndex,
    ushort Opcode,
    string Role,
    string OutputPath);

public static class SoundBankExporter
{
    public static bool HasSoundBankEntries(PacFile pac) =>
        pac.Entries.Any(IsSoundBankEntry);

    public static bool IsSoundBankEntry(PacEntry entry) =>
        (((ushort)(entry.Flags & 0xffff)) & 0x0f00) == 0x0400;

    public static IReadOnlyList<SoundBankPartInfo> Inspect(PacFile pac)
    {
        return pac.Entries
            .Where(IsSoundBankEntry)
            .Select(entry => GetPartInfo(entry, pac.GetEntryData(entry)))
            .ToArray();
    }

    public static IReadOnlyList<SoundBankExport> Export(PacFile pac, string pacPath, string outDir, out string manifestPath)
    {
        Directory.CreateDirectory(outDir);

        string baseName = Path.GetFileNameWithoutExtension(pacPath);
        SoundBankPartInfo[] parts = Inspect(pac).ToArray();
        var outputs = new List<SoundBankExport>();

        foreach (SoundBankPartInfo part in parts)
        {
            PacEntry entry = pac.Entries[part.EntryIndex];
            string fileName = $"{baseName}_entry{part.EntryIndex:D2}_opcode{part.Opcode:X4}_{part.Role}{part.Extension}";
            string outputPath = Path.Combine(outDir, fileName);
            File.WriteAllBytes(outputPath, pac.GetEntryData(entry).ToArray());
            outputs.Add(new SoundBankExport(part.EntryIndex, part.Opcode, part.Role, outputPath));
        }

        manifestPath = Path.Combine(outDir, baseName + "_sound_bank_manifest.txt");
        File.WriteAllText(manifestPath, BuildManifest(pacPath, pac, parts, outputs));
        return outputs;
    }

    public static SoundBankPartInfo GetPartInfo(PacEntry entry, ReadOnlySpan<byte> data)
    {
        ushort opcode = (ushort)(entry.Flags & 0xffff);
        string role;
        string extension;
        switch (opcode & 0xf000)
        {
            case 0x1000:
                role = "vab-header";
                extension = ".vh";
                break;
            case 0x2000:
                role = "vab-body";
                extension = ".vb";
                break;
            case 0x3000:
                role = "sound-control-table";
                extension = ".soundtbl.bin";
                break;
            case 0x4000:
                role = "sound-pointer-table";
                extension = ".soundptr.bin";
                break;
            default:
                role = "sound-bank-part";
                extension = ".bin";
                break;
        }

        return new SoundBankPartInfo(
            entry.Index,
            opcode,
            entry.DataLength,
            role,
            extension,
            HasVabHeaderSignature(data));
    }

    private static bool HasVabHeaderSignature(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && data[0] == (byte)'p' && data[1] == (byte)'B' && data[2] == (byte)'A' && data[3] == (byte)'V';

    private static string BuildManifest(
        string pacPath,
        PacFile pac,
        IReadOnlyList<SoundBankPartInfo> parts,
        IReadOnlyList<SoundBankExport> outputs)
    {
        var lines = new List<string>
        {
            $"source_pac={pacPath}",
            $"total_size=0x{pac.TotalSize:X}",
            $"entry_count={pac.EntryCount.ToString(CultureInfo.InvariantCulture)}",
            "mode=native-sound-bank-export",
            "code_evidence=FUN_800184c0 routes opcode class 0x0400 by the opcode high nibble. Opcodes 0x1xxx select PTR_DAT_80059c7c sound headers, 0x2xxx queue the VAB/SPU body path and call SpuSetTransferMode/SsVabOpenHeadSticky, 0x3xxx select PTR_DAT_80059c88 sound control data, and 0x4xxx select the DAT_80059c80 table target. Payloads are exported as native PlayStation sound-bank parts; no PCM conversion or graphics interpretation is inferred.",
            string.Empty,
            "entries:"
        };

        foreach (SoundBankPartInfo part in parts)
        {
            SoundBankExport? output = outputs.FirstOrDefault(item => item.EntryIndex == part.EntryIndex);
            string path = output is null ? "-" : output.OutputPath;
            lines.Add(
                $"entry={part.EntryIndex.ToString(CultureInfo.InvariantCulture)} opcode=0x{part.Opcode:X4} role={part.Role} length=0x{part.Length:X} pBAV={part.HasVabHeaderSignature.ToString(CultureInfo.InvariantCulture).ToLowerInvariant()} output={path}");
        }

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }
}
