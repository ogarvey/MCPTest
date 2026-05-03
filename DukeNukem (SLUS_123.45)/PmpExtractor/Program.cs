using System.Text.Json;

if (args.Length < 2)
{
    Console.WriteLine("Usage: DukeNukem.PmpExtractor <input.pmp> <output-dir> [--level-chunks N]");
    return 1;
}

string inputPath = Path.GetFullPath(args[0]);
string outputDirectory = Path.GetFullPath(args[1]);
int levelChunkLimit = ParseLevelChunkLimit(args);

Directory.CreateDirectory(outputDirectory);

PmpFile pmp = PmpFile.Load(inputPath);
ExtractionResult result = PmpExtractor.Extract(pmp, outputDirectory, levelChunkLimit);

var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,
};

string summaryPath = Path.Combine(outputDirectory, "summary.json");
File.WriteAllText(summaryPath, JsonSerializer.Serialize(result, jsonOptions));

Console.WriteLine($"Extracted {result.SectionFiles.Count} top-level sections.");
Console.WriteLine($"Decoded {result.Vram.Uploads.Count} VRAM uploads.");
Console.WriteLine($"Decoded {result.LevelDataChunks.Count} level-data chunks.");
Console.WriteLine($"Exported {result.Sprites.Frames.Count} sprite frames.");
Console.WriteLine($"Wrote {result.Sprites.Groups.Count} aligned sprite groups.");
Console.WriteLine($"Wrote summary to {summaryPath}");
return 0;

static int ParseLevelChunkLimit(string[] arguments)
{
    for (int index = 2; index < arguments.Length; index++)
    {
        if (!string.Equals(arguments[index], "--level-chunks", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (index + 1 >= arguments.Length || !int.TryParse(arguments[index + 1], out int value) || value < 0)
        {
            throw new ArgumentException("Expected a non-negative integer after --level-chunks.");
        }

        return value;
    }

    return 8;
}
