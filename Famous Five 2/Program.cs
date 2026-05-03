using FamousFive2;
using SixLabors.ImageSharp.Formats.Png;

Console.WriteLine("Famous Five 2 - BRG Exporter");
Console.WriteLine($"Arguments: {string.Join(' ', args)}");

if (args.Length < 1 || args.Length > 3)
{
  Console.WriteLine("Usage:");
  Console.WriteLine("  dotnet run --project \"Famous Five 2\" -- <input.brg>");
  Console.WriteLine("  dotnet run --project \"Famous Five 2\" -- <input.brg> <output-dir>");
  Console.WriteLine("  dotnet run --project \"Famous Five 2\" -- <input.brg> <output-dir> <frame-index>");
  Console.WriteLine("  dotnet run --project \"Famous Five 2\" -- <input-folder>");
  Console.WriteLine("  dotnet run --project \"Famous Five 2\" -- <input-folder> <output-dir>");
  return;
}

string inputPath = Path.GetFullPath(args[0]);
int? frameIndex = null;

if (args.Length >= 3)
{
  frameIndex = int.Parse(args[2]);
}

if (Directory.Exists(inputPath))
{
  if (frameIndex.HasValue)
  {
    throw new ArgumentException("Frame index is only supported when the input path is a single .brg file.");
  }

  string outputRootDirectory = args.Length >= 2
      ? Path.GetFullPath(args[1])
      : Path.Combine(inputPath, "exported_brg");

  Directory.CreateDirectory(outputRootDirectory);

  string[] brgFiles = Directory.GetFiles(inputPath, "*.brg", SearchOption.TopDirectoryOnly)
      .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
      .ToArray();

  Console.WriteLine($"Folder: {inputPath}");
  Console.WriteLine($"Found {brgFiles.Length} .brg files");

  foreach (string brgFilePath in brgFiles)
  {
    string perFileOutputDirectory = Path.Combine(outputRootDirectory, Path.GetFileNameWithoutExtension(brgFilePath));
    ExportBrgFile(brgFilePath, perFileOutputDirectory, null);
  }

  return;
}

if (!File.Exists(inputPath))
{
  throw new FileNotFoundException($"Input path does not exist: {inputPath}");
}

string outputDirectory = args.Length >= 2
    ? Path.GetFullPath(args[1])
    : Path.Combine(Path.GetDirectoryName(inputPath)!, Path.GetFileNameWithoutExtension(inputPath));

ExportBrgFile(inputPath, outputDirectory, frameIndex);

static void ExportBrgFile(string inputPath, string outputDirectory, int? frameIndex)
{
  Directory.CreateDirectory(outputDirectory);

  BrgFile brg = BrgDecoder.Load(inputPath);

  Console.WriteLine($"File: {inputPath}");
  Console.WriteLine($"Magic: {brg.Magic}");
  Console.WriteLine($"Subtype: {brg.Subtype}");
  Console.WriteLine($"Base size: {brg.Width}x{brg.Height}");
  Console.WriteLine($"Frames: {brg.FrameCount}");

  IReadOnlyList<int> framesToExport = frameIndex.HasValue
      ? [frameIndex.Value]
      : Enumerable.Range(0, brg.FrameCount).ToArray();

  foreach (int index in framesToExport)
  {
    if (index < 0 || index >= brg.FrameCount)
    {
      throw new ArgumentOutOfRangeException(nameof(frameIndex), $"Frame index {index} is outside 0..{brg.FrameCount - 1}.");
    }

    using var image = BrgExporter.ExportFrame(brg, index);
    string outputPath = Path.Combine(outputDirectory, $"{Path.GetFileNameWithoutExtension(inputPath)}_{index:D4}.png");
    using var outputStream = File.Create(outputPath);
    image.Save(outputStream, new PngEncoder());
    Console.WriteLine($"Wrote {outputPath}");
  }
}
