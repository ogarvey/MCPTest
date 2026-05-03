using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System.Text.Json;
using ZyCleaver;

if (!TryParseArguments(args, out var options, out var argumentExitCode))
{
	return argumentExitCode;
}

if (options.DecodedChunkOutputDirectory is not null)
{
	return RunRawChunkDecodeMode(options);
}

IReadOnlyList<string> inputFiles;

try
{
	inputFiles = ResolveInputFiles(options.Inputs);
}
catch (Exception ex)
{
	Console.Error.WriteLine(ex.Message);
	return 1;
}

if (inputFiles.Count == 0)
{
	Console.Error.WriteLine("No CAD files found.");
	return 1;
}

if (options.JsonOutputDirectory is not null)
{
	Directory.CreateDirectory(options.JsonOutputDirectory);
}

if (options.RawChunkOutputDirectory is not null)
{
	Directory.CreateDirectory(options.RawChunkOutputDirectory);
}

var exitCode = 0;

foreach (var inputFile in inputFiles)
{
	try
	{
		var cadFile = CadParser.Parse(inputFile);
		PrintSummary(cadFile);

		if (options.JsonOutputDirectory is not null)
		{
			WriteJsonSummary(cadFile, options.JsonOutputDirectory);
		}

		if (options.RawChunkOutputDirectory is not null)
		{
			WriteRawChunks(cadFile, options.RawChunkOutputDirectory);
		}
	}
	catch (Exception ex)
	{
		exitCode = 1;
		Console.Error.WriteLine($"{Path.GetFileName(inputFile)}: {ex.Message}");
	}
}

return exitCode;

static void PrintSummary(CadFile cadFile)
{
	var normalEntries = cadFile.SequenceEntries.Count(entry => entry.Kind == CadSequenceEntryKind.Normal);
	var loopEntries = cadFile.SequenceEntries.Count(entry => entry.Kind == CadSequenceEntryKind.LoopBacktrack);
	var transitionEntries = cadFile.SequenceEntries.Count(entry => entry.Kind == CadSequenceEntryKind.Transition);
	var uniquePrimaryOffsets = cadFile.Frames.Select(frame => frame.PrimaryDataOffset).Distinct().Count();
	var uniqueSecondaryOffsets = cadFile.Frames.Where(frame => frame.IsComposite).Select(frame => frame.SecondaryDataOffset).Distinct().Count();
	var startPreview = string.Join(
		", ",
		cadFile.SequenceStarts.Take(8).Select(start => $"{start.Index}:0x{start.ByteOffset:X4}->{(start.EntryIndex?.ToString() ?? "?")}"));
	var framePreview = string.Join(
		"; ",
		cadFile.Frames.Take(Math.Min(4, cadFile.Frames.Count)).Select(frame =>
			$"[{frame.Index}] {(frame.IsComposite ? "composite" : "single")} p0=0x{frame.PrimaryDataOffset:X} p1={(frame.IsComposite ? $"0x{frame.SecondaryDataOffset:X}" : "-")}"));

	Console.WriteLine(cadFile.SourcePath);
	Console.WriteLine($"  magic: {cadFile.Magic}");
	Console.WriteLine($"  unk0: {cadFile.Unknown0}");
	Console.WriteLine($"  auxFlag: {cadFile.AuxiliaryFlag} ({(cadFile.AuxiliaryBlock is null ? "no aux block" : "0x400-byte aux block")})");
	Console.WriteLine($"  unk1: {cadFile.Unknown1}");
	Console.WriteLine($"  rawDataSize: {cadFile.RawData.Length}");
	Console.WriteLine($"  frameCount: {cadFile.Frames.Count}");
	Console.WriteLine($"  sequenceEntryCount: {cadFile.SequenceEntries.Count} (normal={normalEntries}, loop={loopEntries}, transition={transitionEntries})");
	Console.WriteLine($"  sequenceStartCount: {cadFile.SequenceStarts.Count}");
	Console.WriteLine($"  imageChunkCount: {cadFile.ImageChunks.Count}");
	Console.WriteLine($"  uniquePrimaryOffsets: {uniquePrimaryOffsets}");
	Console.WriteLine($"  uniqueSecondaryOffsets: {uniqueSecondaryOffsets}");

	if (startPreview.Length > 0)
	{
		Console.WriteLine($"  sequenceStarts: {startPreview}{(cadFile.SequenceStarts.Count > 8 ? ", ..." : string.Empty)}");
	}

	if (framePreview.Length > 0)
	{
		Console.WriteLine($"  framePreview: {framePreview}");
	}

	var chunkPreview = string.Join(
		"; ",
		cadFile.ImageChunks.Take(Math.Min(4, cadFile.ImageChunks.Count)).Select(chunk =>
			$"off=0x{chunk.Offset:X} {chunk.Width}x{chunk.Height} len~{chunk.LengthGuess} frames=[{string.Join(",", chunk.ReferencedByFrames)}]"));

	if (chunkPreview.Length > 0)
	{
		Console.WriteLine($"  chunkPreview: {chunkPreview}");
	}

	Console.WriteLine();
}

static void WriteJsonSummary(CadFile cadFile, string outputDirectory)
{
	var summary = new
	{
		cadFile.SourcePath,
		cadFile.Magic,
		cadFile.Unknown0,
		cadFile.AuxiliaryFlag,
		AuxiliaryBlockSize = cadFile.AuxiliaryBlock?.Length ?? 0,
		cadFile.Unknown1,
		RawDataSize = cadFile.RawData.Length,
		Frames = cadFile.Frames.Select(frame => new
		{
			frame.Index,
			frame.CompositeFlag,
			frame.IsComposite,
			frame.Part1X,
			frame.Part1Y,
			frame.Part2X,
			frame.Part2Y,
			frame.PrimaryDataOffset,
			frame.SecondaryDataOffset,
			RawHex = Convert.ToHexString(frame.Bytes)
		}).ToArray(),
		SequenceEntries = cadFile.SequenceEntries.Select(entry => new
		{
			entry.Index,
			entry.EntryOffset,
			entry.RawTarget,
			entry.Value,
			Kind = entry.Kind.ToString(),
			entry.FrameIndex,
			entry.BacktrackEntryCount
		}).ToArray(),
		SequenceStarts = cadFile.SequenceStarts.Select(start => new
		{
			start.Index,
			start.ByteOffset,
			start.EntryIndex
		}).ToArray(),
		ImageChunks = cadFile.ImageChunks.Select(chunk => new
		{
			chunk.Offset,
			chunk.Width,
			chunk.Height,
			chunk.LengthGuess,
			chunk.ReferencedByFrames
		}).ToArray()
	};

	var outputPath = Path.Combine(outputDirectory, $"{Path.GetFileNameWithoutExtension(cadFile.SourcePath)}.cad.json");
	File.WriteAllText(
		outputPath,
		JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));

	Console.WriteLine($"  json: {outputPath}");
	Console.WriteLine();
}

static void WriteRawChunks(CadFile cadFile, string outputDirectory)
{
	var baseName = Path.GetFileNameWithoutExtension(cadFile.SourcePath);

	foreach (var chunk in cadFile.ImageChunks)
	{
		var chunkBytes = cadFile.RawData.AsSpan(chunk.Offset, chunk.LengthGuess).ToArray();
		var outputPath = Path.Combine(
			outputDirectory,
			$"{baseName}_off{chunk.Offset:X6}_{chunk.Width}x{chunk.Height}.bin");

		File.WriteAllBytes(outputPath, chunkBytes);
	}

	Console.WriteLine($"  raw chunks: {outputDirectory}");
	Console.WriteLine();
}

static IReadOnlyList<string> ResolveInputFiles(IReadOnlyList<string> inputs)
{
	var resolvedFiles = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
	var candidates = inputs.Count == 0 ? new[] { GetDefaultSamplesDirectory() } : inputs;

	foreach (var candidate in candidates)
	{
		if (File.Exists(candidate))
		{
			if (!candidate.EndsWith(".cad", StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidOperationException($"Unsupported file type: {candidate}");
			}

			resolvedFiles.Add(Path.GetFullPath(candidate));
			continue;
		}

		if (Directory.Exists(candidate))
		{
			foreach (var file in Directory.EnumerateFiles(candidate, "*.cad", SearchOption.TopDirectoryOnly))
			{
				resolvedFiles.Add(Path.GetFullPath(file));
			}

			continue;
		}

		throw new FileNotFoundException($"Input path does not exist: {candidate}");
	}

	return resolvedFiles.ToArray();
}

static int RunRawChunkDecodeMode(CommandLineOptions options)
{
	IReadOnlyList<string> inputFiles;

	try
	{
		inputFiles = ResolveRawChunkFiles(options.Inputs);
	}
	catch (Exception ex)
	{
		Console.Error.WriteLine(ex.Message);
		return 1;
	}

	if (inputFiles.Count == 0)
	{
		Console.Error.WriteLine("No raw chunk files found.");
		return 1;
	}

	var outputDirectory = options.DecodedChunkOutputDirectory!;
	Directory.CreateDirectory(outputDirectory);

	var exitCode = 0;

	foreach (var inputFile in inputFiles)
	{
		try
		{
			var chunkBytes = File.ReadAllBytes(inputFile);
			var decodedChunk = RawChunkDecoder.Decode(chunkBytes);
			PrintRawChunkSummary(inputFile, chunkBytes.Length, decodedChunk);
			WriteDecodedRawChunkPng(inputFile, decodedChunk, outputDirectory);
		}
		catch (Exception ex)
		{
			exitCode = 1;
			Console.Error.WriteLine($"{Path.GetFileName(inputFile)}: {ex.Message}");
		}
	}

	return exitCode;
}

static IReadOnlyList<string> ResolveRawChunkFiles(IReadOnlyList<string> inputs)
{
	var resolvedFiles = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
	var candidates = inputs.Count == 0 ? new[] { GetDefaultRawChunkDirectory() } : inputs;

	foreach (var candidate in candidates)
	{
		if (File.Exists(candidate))
		{
			if (!candidate.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidOperationException($"Unsupported raw chunk file type: {candidate}");
			}

			resolvedFiles.Add(Path.GetFullPath(candidate));
			continue;
		}

		if (Directory.Exists(candidate))
		{
			foreach (var file in Directory.EnumerateFiles(candidate, "*.bin", SearchOption.TopDirectoryOnly))
			{
				resolvedFiles.Add(Path.GetFullPath(file));
			}

			continue;
		}

		throw new FileNotFoundException($"Input path does not exist: {candidate}");
	}

	return resolvedFiles.ToArray();
}

static void PrintRawChunkSummary(string inputFile, int fileLength, DecodedRawChunk decodedChunk)
{
	var opaquePixels = decodedChunk.Pixels.Count(pixel => pixel != 0);
	var uniqueIndices = decodedChunk.Pixels.Where(pixel => pixel != 0).Distinct().Count();

	Console.WriteLine(inputFile);
	Console.WriteLine($"  size: {decodedChunk.Width}x{decodedChunk.Height}");
	Console.WriteLine($"  decodedBytes: {decodedChunk.BytesConsumed}/{fileLength}");
	Console.WriteLine($"  opaquePixels: {opaquePixels}");
	Console.WriteLine($"  uniqueIndices: {uniqueIndices}");

	if (decodedChunk.BytesConsumed != fileLength)
	{
		Console.WriteLine($"  note: {fileLength - decodedChunk.BytesConsumed} trailing bytes remain after decoding");
	}

	Console.WriteLine();
}

static void WriteDecodedRawChunkPng(string inputFile, DecodedRawChunk decodedChunk, string outputDirectory)
{
	using var image = new Image<Rgba32>(decodedChunk.Width, decodedChunk.Height);

	for (var y = 0; y < decodedChunk.Height; y++)
	{
		for (var x = 0; x < decodedChunk.Width; x++)
		{
			var paletteIndex = decodedChunk.Pixels[(y * decodedChunk.Width) + x];
			image[x, y] = MapDebugColor(paletteIndex);
		}
	}

	var outputPath = Path.Combine(outputDirectory, $"{Path.GetFileNameWithoutExtension(inputFile)}.png");
	image.Save(outputPath);

	Console.WriteLine($"  png: {outputPath}");
	Console.WriteLine();
}

static Rgba32 MapDebugColor(byte paletteIndex)
{
	if (paletteIndex == 0)
	{
		return new Rgba32(0, 0, 0, 0);
	}

	var red = (byte)((paletteIndex * 73) & 0xff);
	var green = (byte)((paletteIndex * 151) & 0xff);
	var blue = (byte)((paletteIndex * 199) & 0xff);

	if (red == 0 && green == 0 && blue == 0)
	{
		red = 0xff;
	}

	return new Rgba32(red, green, blue, 0xff);
}

static string GetDefaultSamplesDirectory()
{
	var projectDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
	return Path.GetFullPath(Path.Combine(projectDirectory, "..", "Samples"));
}

static string GetDefaultRawChunkDirectory()
{
	var projectDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
	return Path.GetFullPath(Path.Combine(projectDirectory, "..", "TestOutput", "raw-chunks"));
}

static bool TryParseArguments(string[] args, out CommandLineOptions options, out int exitCode)
{
	var inputs = new List<string>();
	string? jsonOutputDirectory = null;
	string? rawChunkOutputDirectory = null;
	string? decodedChunkOutputDirectory = null;

	for (var index = 0; index < args.Length; index++)
	{
		var argument = args[index];

		switch (argument)
		{
			case "-h":
			case "--help":
				PrintHelp();
				options = new CommandLineOptions(Array.Empty<string>(), null, null, null);
				exitCode = 0;
				return false;

			case "--json":
				if (index + 1 >= args.Length)
				{
					Console.Error.WriteLine("Missing directory after --json.");
					options = new CommandLineOptions(Array.Empty<string>(), null, null, null);
					exitCode = 1;
					return false;
				}

				jsonOutputDirectory = Path.GetFullPath(args[++index]);
				break;

			case "--extract-raw":
				if (index + 1 >= args.Length)
				{
					Console.Error.WriteLine("Missing directory after --extract-raw.");
					options = new CommandLineOptions(Array.Empty<string>(), null, null, null);
					exitCode = 1;
					return false;
				}

				rawChunkOutputDirectory = Path.GetFullPath(args[++index]);
				break;

			case "--decode-chunks":
				if (index + 1 >= args.Length)
				{
					Console.Error.WriteLine("Missing directory after --decode-chunks.");
					options = new CommandLineOptions(Array.Empty<string>(), null, null, null);
					exitCode = 1;
					return false;
				}

				decodedChunkOutputDirectory = Path.GetFullPath(args[++index]);
				break;

			default:
				inputs.Add(Path.GetFullPath(argument));
				break;
		}
	}

	options = new CommandLineOptions(inputs, jsonOutputDirectory, rawChunkOutputDirectory, decodedChunkOutputDirectory);
	exitCode = 0;
	return true;
}

static void PrintHelp()
{
	Console.WriteLine("ZyCleaver - Zyclunt CAD parser");
	Console.WriteLine();
	Console.WriteLine("Usage:");
	Console.WriteLine("  dotnet run -- [--json <outputDir>] [--extract-raw <outputDir>] [<cad file>|<directory> ...]");
	Console.WriteLine("  dotnet run -- --decode-chunks <outputDir> [<raw chunk .bin>|<directory> ...]");
	Console.WriteLine();
	Console.WriteLine("If no CAD inputs are supplied, the tool parses *.cad files from ../Samples.");
	Console.WriteLine("If no raw chunk inputs are supplied with --decode-chunks, the tool decodes *.bin files from ../TestOutput/raw-chunks.");
}

internal sealed record CommandLineOptions(IReadOnlyList<string> Inputs, string? JsonOutputDirectory, string? RawChunkOutputDirectory, string? DecodedChunkOutputDirectory);
