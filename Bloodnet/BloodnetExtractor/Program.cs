using System.Text.Json;

namespace BloodnetExtractor;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (!ExtractionOptions.TryParse(args, out var options))
        {
            PrintUsage();
            return 1;
        }

        try
        {
            var extractor = new ArchiveExtractor(options);
            extractor.Run();
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine(exception.StackTrace);
            return 1;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: BloodnetExtractor <input .PL file or directory> <output directory> [--preview-width-mode packed|expanded]");
        Console.WriteLine("The extractor always writes decoded packed pixel buffers for recognized image entries.");
        Console.WriteLine("Preview PNGs default to packed layout because the final expansion parameter comes from caller-side metadata, not the entry header.");
    }
}

internal sealed record ExtractionOptions(string InputPath, string OutputDirectory, PreviewWidthMode PreviewWidthMode)
{
    public static bool TryParse(string[] args, out ExtractionOptions options)
    {
        options = null!;

        if (args.Length < 2)
        {
            return false;
        }

        var inputPath = args[0];
        var outputDirectory = args[1];
        var previewWidthMode = PreviewWidthMode.Packed;

        for (var index = 2; index < args.Length; index++)
        {
            if (string.Equals(args[index], "--preview-width-mode", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Length)
                {
                    return false;
                }

                previewWidthMode = args[index + 1].Equals("expanded", StringComparison.OrdinalIgnoreCase)
                    ? PreviewWidthMode.ExpandedEstimate
                    : PreviewWidthMode.Packed;
                index++;
                continue;
            }

            return false;
        }

        options = new ExtractionOptions(inputPath, outputDirectory, previewWidthMode);
        return true;
    }
}

internal enum PreviewWidthMode
{
    Packed,
    ExpandedEstimate,
}

internal sealed class ArchiveExtractor
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly ExtractionOptions options;

    public ArchiveExtractor(ExtractionOptions options)
    {
        this.options = options;
    }

    public void Run()
    {
        Directory.CreateDirectory(options.OutputDirectory);

        foreach (var filePath in EnumerateInputFiles(options.InputPath))
        {
            Console.WriteLine($"Extracting {Path.GetFileName(filePath)}");
            var archiveData = File.ReadAllBytes(filePath);
            var archive = PlArchiveReader.ReadArchive(archiveData, Path.GetFileName(filePath));

            var archiveOutputDirectory = Path.Combine(
                options.OutputDirectory,
                Path.GetFileNameWithoutExtension(filePath));

            ExtractArchive(archive, archiveOutputDirectory);
        }
    }

    private IEnumerable<string> EnumerateInputFiles(string inputPath)
    {
        if (File.Exists(inputPath))
        {
            yield return Path.GetFullPath(inputPath);
            yield break;
        }

        if (!Directory.Exists(inputPath))
        {
            throw new DirectoryNotFoundException($"Input path not found: {inputPath}");
        }

        foreach (var filePath in Directory.EnumerateFiles(inputPath, "*.PL", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            yield return filePath;
        }
    }

    private void ExtractArchive(PlArchive archive, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        WriteJson(
            Path.Combine(outputDirectory, "archive.json"),
            new
            {
                archive.Name,
                archive.EntryCount,
                archive.TableOffset,
                Entries = archive.Entries.Select(entry => new
                {
                    entry.Index,
                    entry.Name,
                    entry.Offset,
                    entry.Size,
                }),
            });

        foreach (var entry in archive.Entries)
        {
            var entryStem = Path.Combine(outputDirectory, $"{entry.Index:D4}_{SanitizeFileName(entry.Name)}");
            File.WriteAllBytes(entryStem + ".bin", entry.Data);

            if (PlArchiveReader.TryReadArchive(entry.Data, entry.Name, out var nestedArchive, out _))
            {
                WriteJson(
                    entryStem + ".json",
                    new
                    {
                        entry.Index,
                        entry.Name,
                        entry.Offset,
                        entry.Size,
                        Type = "NestedPlArchive",
                    });

                ExtractArchive(nestedArchive, entryStem + "_pl");
                continue;
            }

            if (BloodnetImageDecoder.TryDecode(entry.Data, out var decodedImage, out var failureReason))
            {
                WriteDecodedImage(entryStem, entry, decodedImage, archive.Name);
                continue;
            }

            if (BloodnetImageDecoder.TryProbe(entry.Data, out var imageProbe, out _))
            {
                WriteFailedImageContainer(entryStem, entry, imageProbe, failureReason);
                continue;
            }

            WriteJson(
                entryStem + ".json",
                new
                {
                    entry.Index,
                    entry.Name,
                    entry.Offset,
                    entry.Size,
                    Type = "RawEntry",
                    Reason = failureReason,
                });
        }
    }

    private void WriteDecodedImage(string entryStem, PlEntry entry, BloodnetDecodedImage image, string archiveName)
    {
        File.WriteAllBytes(entryStem + ".packed.bin", image.PackedPixels);

        if (image.InlinePalette is not null)
        {
            File.WriteAllBytes(entryStem + ".palette.rgb", image.InlinePalette);
        }

        if (image.AuxiliaryTable is not null)
        {
            File.WriteAllBytes(entryStem + ".aux.bin", image.AuxiliaryTable);
        }

        var previewWidth = options.PreviewWidthMode == PreviewWidthMode.ExpandedEstimate && image.EstimatedExpandedWidth is not null
            ? image.EstimatedExpandedWidth.Value
            : image.Header.Width;

        byte? transparentPixelIndex = null;
        if (ShouldUseTransparentPreview(archiveName, image.Header))
        {
            transparentPixelIndex = image.EffectiveTransparentPixelIndex;
        }

        if (previewWidth * image.Header.Height == image.PackedPixels.Length)
        {
            ImagePreviewWriter.WritePreviewPng(
                entryStem + ".preview.png",
                image.PackedPixels,
                previewWidth,
                image.Header.Height,
                image.InlinePalette,
                transparentPixelIndex);
        }

        var paletteNote = image.HasInlinePalette
            ? "Preview uses the inline palette embedded in this entry."
            : "No inline palette is embedded in this entry. The game likely supplies an external/shared palette context, so this extractor preview falls back to grayscale.";

        WriteJson(
            entryStem + ".json",
            new
            {
                entry.Index,
                entry.Name,
                entry.Offset,
                entry.Size,
                Type = "ImageEntry",
                Header = new
                {
                    Flags = $"0x{image.Header.Flags:X2}",
                    image.Header.SymbolCount,
                    image.Header.PaletteColorCount,
                    TransparentIndex = $"0x{image.Header.TransparentIndex:X2}",
                    EffectiveTransparentIndex = $"0x{image.EffectiveTransparentPixelIndex:X2}",
                    Format = $"0x{image.Header.Format:X2}",
                    DeltaTable = image.Header.DeltaTable.Select(value => $"0x{value:X2}"),
                    image.Header.TailLiteralCount,
                    image.Header.Width,
                    image.Header.Height,
                    image.Header.PayloadLength,
                },
                image.DecodedUsing2106_002cModel,
                image.HasInlinePalette,
                image.HasInlineSymbolTable,
                image.HasAuxiliaryTable,
                image.AuxiliaryTableLength,
                image.EstimatedExpandedWidth,
                PaletteNote = paletteNote,
                image.LayoutNote,
                PreviewWidth = previewWidth,
            });
    }

    private void WriteFailedImageContainer(string entryStem, PlEntry entry, BloodnetImageProbe probe, string decodeFailureReason)
    {
        if (probe.InlinePalette is not null)
        {
            File.WriteAllBytes(entryStem + ".palette.rgb", probe.InlinePalette);
        }

        if (probe.AuxiliaryTable is not null)
        {
            File.WriteAllBytes(entryStem + ".aux.bin", probe.AuxiliaryTable);
        }

        var paletteNote = probe.HasInlinePalette
            ? "This entry carries an inline palette, but the stream decoder failed before a packed buffer could be emitted."
            : "This entry has no inline palette. The game likely supplies an external/shared palette context, and the stream decoder also failed for this entry.";

        WriteJson(
            entryStem + ".json",
            new
            {
                entry.Index,
                entry.Name,
                entry.Offset,
                entry.Size,
                Type = "ImageEntryDecodeFailed",
                Header = new
                {
                    Flags = $"0x{probe.Header.Flags:X2}",
                    probe.Header.SymbolCount,
                    probe.Header.PaletteColorCount,
                    TransparentIndex = $"0x{probe.Header.TransparentIndex:X2}",
                    EffectiveTransparentIndex = $"0x{probe.EffectiveTransparentPixelIndex:X2}",
                    Format = $"0x{probe.Header.Format:X2}",
                    DeltaTable = probe.Header.DeltaTable.Select(value => $"0x{value:X2}"),
                    probe.Header.TailLiteralCount,
                    probe.Header.Width,
                    probe.Header.Height,
                    probe.Header.PayloadLength,
                },
                probe.HasInlinePalette,
                probe.HasInlineSymbolTable,
                probe.HasAuxiliaryTable,
                probe.AuxiliaryTableLength,
                PaletteNote = paletteNote,
                DecodeFailureReason = decodeFailureReason,
            });
    }

    private static void WriteJson(string filePath, object value)
    {
        File.WriteAllText(filePath, JsonSerializer.Serialize(value, JsonOptions));
    }

    private static bool ShouldUseTransparentPreview(string archiveName, BloodnetImageHeader header)
    {
        if (archiveName.Equals("SPRITE.PL", StringComparison.OrdinalIgnoreCase)
            || archiveName.Equals("OBJECTS.PL", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (archiveName.Equals("CHARGEN.PL", StringComparison.OrdinalIgnoreCase)
            || archiveName.Equals("INTRFACE.PL", StringComparison.OrdinalIgnoreCase))
        {
            return header.Height <= 80;
        }

        return false;
    }

    private static string SanitizeFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unnamed";
        }

        var invalidCharacters = Path.GetInvalidFileNameChars();
        var characters = value
            .Select(character => invalidCharacters.Contains(character) ? '_' : character)
            .ToArray();

        return new string(characters);
    }
}
