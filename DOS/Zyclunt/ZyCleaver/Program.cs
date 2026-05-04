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

if (options.PaletteTableOutputPath is not null)
{
	return RunPaletteTableExportMode(options);
}

if (options.ShowPaletteSuggestions)
{
	return RunPaletteSuggestionMode(options);
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

var shouldRenderCadFrames = options.Inputs.Count > 0;

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

		if (shouldRenderCadFrames)
		{
			WriteRenderedCadFrames(cadFile, GetDefaultCadFrameOutputDirectory(cadFile), options.PaletteFilePath, options.UseAllCadPalettes);
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
			frame.AnchorX,
			frame.AnchorY,
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

static void WriteRenderedCadFrames(CadFile cadFile, string outputDirectory, string? paletteOverridePath, bool useAllCadPalettes)
{
	Directory.CreateDirectory(outputDirectory);

	var paletteSelections = ResolveCadPalettes(cadFile, paletteOverridePath, useAllCadPalettes);
	var decodedChunksByOffset = new Dictionary<uint, DecodedRawChunk>();
	var frameLayouts = cadFile.Frames
		.Select(frame => BuildFrameRenderLayout(cadFile, frame, decodedChunksByOffset))
		.ToArray();

	if (frameLayouts.Length == 0)
	{
		Console.WriteLine($"  palette exports: {paletteSelections.Count}");
		Console.WriteLine("  aligned frames: no frames present");
		Console.WriteLine();
		return;
	}

	var frameLayoutsByIndex = frameLayouts.ToDictionary(layout => layout.Frame.Index);
	var frameBoundsByIndex = frameLayouts.ToDictionary(layout => layout.Frame.Index, layout => CalculatePartBounds(layout.Parts));
	var sequencePlans = BuildSequenceRenderPlans(cadFile);

	foreach (var paletteSelection in paletteSelections)
	{
		WritePaletteRenderExport(cadFile, outputDirectory, paletteSelection, frameLayouts, frameLayoutsByIndex, frameBoundsByIndex, sequencePlans);
	}
}

static IReadOnlyList<CadPaletteSelection> ResolveCadPalettes(CadFile cadFile, string? paletteOverridePath, bool useAllCadPalettes)
{
	if (paletteOverridePath is not null)
	{
		var overridePalette = PaletteFile.Load(paletteOverridePath);
		return
		[
			new CadPaletteSelection(
				Path.GetFileNameWithoutExtension(overridePalette.SourcePath),
				overridePalette,
				$"{Path.GetFileName(overridePalette.SourcePath)} [explicit override]")
		];
	}

	var cadName = Path.GetFileNameWithoutExtension(cadFile.SourcePath);
	var paletteDirectory = GetDefaultPaletteDirectory();
	var selections = new List<CadPaletteSelection>();
	var seenPalettePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	foreach (var candidate in PaletteHintCatalog.GetCandidates(cadName))
	{
		if (string.Equals(candidate.PaletteFileName, "multiple", StringComparison.OrdinalIgnoreCase))
		{
			continue;
		}

		var candidatePath = Path.Combine(paletteDirectory, candidate.PaletteFileName);

		if (!File.Exists(candidatePath))
		{
			continue;
		}

		if (!seenPalettePaths.Add(candidatePath))
		{
			continue;
		}

		var palette = PaletteFile.Load(candidatePath);
		selections.Add(new CadPaletteSelection(
			Path.GetFileNameWithoutExtension(candidate.PaletteFileName),
			palette,
			$"{candidate.PaletteFileName} [{candidate.Source}]"));

		if (!useAllCadPalettes)
		{
			break;
		}
	}

	return selections.Count > 0
		? selections
		: [new CadPaletteSelection("debug-colors", null, "no known palette match; using debug colors")];
}

static void WritePaletteRenderExport(
	CadFile cadFile,
	string outputDirectory,
	CadPaletteSelection paletteSelection,
	IReadOnlyList<FrameRenderLayout> frameLayouts,
	IReadOnlyDictionary<int, FrameRenderLayout> frameLayoutsByIndex,
	IReadOnlyDictionary<int, FrameCanvasBounds> frameBoundsByIndex,
	IReadOnlyList<SequenceRenderPlan> sequencePlans)
{
	var baseName = Path.GetFileNameWithoutExtension(cadFile.SourcePath);
	var paletteOutputDirectory = Path.Combine(outputDirectory, paletteSelection.DirectoryName);
	var framesOutputDirectory = Path.Combine(paletteOutputDirectory, "frames");
	var sequencesOutputDirectory = Path.Combine(paletteOutputDirectory, "sequences");
	var sequenceOutputFilesByKey = new Dictionary<(int SequenceIndex, int FrameIndex), string>();
	var sequenceBoundsByIndex = new Dictionary<int, FrameCanvasBounds>();

	Directory.CreateDirectory(paletteOutputDirectory);
	Directory.CreateDirectory(framesOutputDirectory);
	Directory.CreateDirectory(sequencesOutputDirectory);

	foreach (var layout in frameLayouts)
	{
		var frameBounds = frameBoundsByIndex[layout.Frame.Index];
		using var image = new Image<Rgba32>(frameBounds.Width, frameBounds.Height);

		foreach (var part in layout.Parts)
		{
			DrawDecodedChunk(image, part.Chunk, part.X - frameBounds.MinX, part.Y - frameBounds.MinY, paletteSelection.Palette);
		}

		var outputPath = Path.Combine(framesOutputDirectory, GetFrameFileName(baseName, layout.Frame.Index));
		image.Save(outputPath);
	}

	foreach (var sequencePlan in sequencePlans)
	{
		var sequenceDirectoryName = GetSequenceDirectoryName(sequencePlan.SequenceStart.Index);
		var sequenceOutputDirectory = Path.Combine(sequencesOutputDirectory, sequenceDirectoryName);
		var sequenceLayouts = sequencePlan.FrameIndices.Select(frameIndex => frameLayoutsByIndex[frameIndex]).ToArray();

		Directory.CreateDirectory(sequenceOutputDirectory);

		if (sequenceLayouts.Length == 0)
		{
			continue;
		}

		var sequenceBounds = CalculateSharedBounds(sequenceLayouts);
		sequenceBoundsByIndex.Add(sequencePlan.SequenceStart.Index, sequenceBounds);

		foreach (var layout in sequenceLayouts)
		{
			using var image = new Image<Rgba32>(sequenceBounds.Width, sequenceBounds.Height);

			foreach (var part in layout.Parts)
			{
				DrawDecodedChunk(image, part.Chunk, part.X - sequenceBounds.MinX, part.Y - sequenceBounds.MinY, paletteSelection.Palette);
			}

			var fileName = GetSequenceFrameFileName(baseName, sequencePlan.SequenceStart.Index, layout.Frame.Index);
			var outputPath = Path.Combine(sequenceOutputDirectory, fileName);
			image.Save(outputPath);
			sequenceOutputFilesByKey.Add((sequencePlan.SequenceStart.Index, layout.Frame.Index), $"sequences/{sequenceDirectoryName}/{fileName}");
		}
	}

	var manifestPath = Path.Combine(paletteOutputDirectory, $"{baseName}.frames.json");
	var manifest = new
	{
		cadFile.SourcePath,
		SelectedPalette = paletteSelection.Palette?.SourcePath,
		PaletteDescription = paletteSelection.Description,
		PaletteDirectory = paletteSelection.DirectoryName,
		FramesDirectory = "frames",
		SequencesDirectory = "sequences",
		Frames = frameLayouts.Select(layout =>
		{
			var frameBounds = frameBoundsByIndex[layout.Frame.Index];
			return new
			{
				layout.Frame.Index,
				layout.Frame.AnchorX,
				layout.Frame.AnchorY,
				layout.Frame.CompositeFlag,
				layout.Frame.IsComposite,
				Canvas = new
				{
					frameBounds.Width,
					frameBounds.Height,
					frameBounds.MinX,
					frameBounds.MinY,
					AlignmentOffsetX = -frameBounds.MinX,
					AlignmentOffsetY = -frameBounds.MinY
				},
				OutputFile = $"frames/{GetFrameFileName(baseName, layout.Frame.Index)}",
				Parts = layout.Parts.Select(part => new
				{
					part.RawDataOffset,
					part.X,
					part.Y,
					part.Chunk.Width,
					part.Chunk.Height
				}).ToArray()
			};
		}).ToArray(),
		Sequences = sequencePlans.Select(sequencePlan =>
		{
			var sequenceDirectoryName = GetSequenceDirectoryName(sequencePlan.SequenceStart.Index);
			var hasCanvas = sequenceBoundsByIndex.TryGetValue(sequencePlan.SequenceStart.Index, out var sequenceBounds);
			return new
			{
				sequencePlan.SequenceStart.Index,
				sequencePlan.SequenceStart.ByteOffset,
				sequencePlan.SequenceStart.EntryIndex,
				sequencePlan.EndEntryIndexExclusive,
				OutputDirectory = $"sequences/{sequenceDirectoryName}",
				Canvas = hasCanvas
					? new
					{
						sequenceBounds.Width,
						sequenceBounds.Height,
						sequenceBounds.MinX,
						sequenceBounds.MinY,
						AlignmentOffsetX = -sequenceBounds.MinX,
						AlignmentOffsetY = -sequenceBounds.MinY
					}
					: null,
				Frames = sequencePlan.FrameIndices.Select(frameIndex => new
				{
					FrameIndex = frameIndex,
					OutputFile = sequenceOutputFilesByKey.TryGetValue((sequencePlan.SequenceStart.Index, frameIndex), out var outputFile)
						? outputFile
						: null
				}).ToArray(),
				Entries = sequencePlan.Entries.Select(entry => new
				{
					entry.Index,
					entry.EntryOffset,
					entry.RawTarget,
					entry.Value,
					Kind = entry.Kind.ToString(),
					entry.FrameIndex,
					entry.BacktrackEntryCount,
					OutputFile = entry.FrameIndex is int frameIndex && sequenceOutputFilesByKey.TryGetValue((sequencePlan.SequenceStart.Index, frameIndex), out var outputFile)
						? outputFile
						: null
				}).ToArray()
			};
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
		}).ToArray()
	};

	File.WriteAllText(
		manifestPath,
		JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

	Console.WriteLine($"  palette: {paletteSelection.Description}");
	Console.WriteLine($"  source frames: {framesOutputDirectory} ({frameLayouts.Count} frames)");
	Console.WriteLine($"  sequences: {sequencesOutputDirectory} ({sequencePlans.Count} sequences)");
	Console.WriteLine($"  frame manifest: {manifestPath}");
	Console.WriteLine();
}

static IReadOnlyList<SequenceRenderPlan> BuildSequenceRenderPlans(CadFile cadFile)
{
	var plans = new List<SequenceRenderPlan>(cadFile.SequenceStarts.Count);

	foreach (var sequenceStart in cadFile.SequenceStarts)
	{
		if (sequenceStart.EntryIndex is not int startEntryIndex)
		{
			plans.Add(new SequenceRenderPlan(sequenceStart, null, Array.Empty<CadSequenceEntry>(), Array.Empty<int>()));
			continue;
		}

		var endEntryIndexExclusive = cadFile.SequenceStarts
			.Where(candidate => candidate.EntryIndex is int candidateEntryIndex && candidateEntryIndex > startEntryIndex)
			.Select(candidate => candidate.EntryIndex!.Value)
			.DefaultIfEmpty(cadFile.SequenceEntries.Count)
			.Min();

		var entries = cadFile.SequenceEntries
			.Skip(startEntryIndex)
			.Take(endEntryIndexExclusive - startEntryIndex)
			.ToArray();
		var frameIndices = entries
			.Where(entry => entry.FrameIndex is not null)
			.Select(entry => entry.FrameIndex!.Value)
			.Distinct()
			.ToArray();

		plans.Add(new SequenceRenderPlan(sequenceStart, endEntryIndexExclusive, entries, frameIndices));
	}

	return plans;
}

static FrameRenderLayout BuildFrameRenderLayout(
	CadFile cadFile,
	CadFrameRecord frame,
	IDictionary<uint, DecodedRawChunk> decodedChunksByOffset)
{
	var parts = new List<FrameRenderPart>(frame.IsComposite ? 2 : 1);

	if (frame.IsComposite)
	{
		parts.Add(BuildFrameRenderPart(cadFile, frame.PrimaryDataOffset, frame.AnchorX + frame.Part1X, frame.AnchorY + frame.Part1Y, decodedChunksByOffset));
		parts.Add(BuildFrameRenderPart(cadFile, frame.SecondaryDataOffset, frame.AnchorX + frame.Part2X, frame.AnchorY + frame.Part2Y, decodedChunksByOffset));
	}
	else
	{
		parts.Add(BuildFrameRenderPart(cadFile, frame.PrimaryDataOffset, frame.AnchorX, frame.AnchorY, decodedChunksByOffset));
	}

	return new FrameRenderLayout(frame, parts);
}

static FrameRenderPart BuildFrameRenderPart(
	CadFile cadFile,
	uint rawDataOffset,
	int x,
	int y,
	IDictionary<uint, DecodedRawChunk> decodedChunksByOffset)
{
	if (!decodedChunksByOffset.TryGetValue(rawDataOffset, out var decodedChunk))
	{
		if (rawDataOffset >= cadFile.RawData.Length)
		{
			throw new InvalidDataException($"Frame render part points past the raw data blob: 0x{rawDataOffset:X8}.");
		}

		decodedChunk = RawChunkDecoder.Decode(cadFile.RawData.AsSpan((int)rawDataOffset));
		decodedChunksByOffset.Add(rawDataOffset, decodedChunk);
	}

	return new FrameRenderPart(rawDataOffset, x, y, decodedChunk);
}

static FrameCanvasBounds CalculatePartBounds(IReadOnlyList<FrameRenderPart> parts)
{
	var minX = int.MaxValue;
	var minY = int.MaxValue;
	var maxX = int.MinValue;
	var maxY = int.MinValue;

	foreach (var part in parts)
	{
		minX = Math.Min(minX, part.X);
		minY = Math.Min(minY, part.Y);
		maxX = Math.Max(maxX, part.X + part.Chunk.Width);
		maxY = Math.Max(maxY, part.Y + part.Chunk.Height);
	}

	return new FrameCanvasBounds(minX, minY, maxX, maxY);
}

static FrameCanvasBounds CalculateSharedBounds(IReadOnlyList<FrameRenderLayout> frameLayouts)
{
	var allParts = frameLayouts.SelectMany(layout => layout.Parts).ToArray();
	return CalculatePartBounds(allParts);
}

static void DrawDecodedChunk(Image<Rgba32> image, DecodedRawChunk decodedChunk, int destinationX, int destinationY, PaletteFile? palette)
{
	for (var y = 0; y < decodedChunk.Height; y++)
	{
		for (var x = 0; x < decodedChunk.Width; x++)
		{
			var pixelIndex = (y * decodedChunk.Width) + x;

			if (!decodedChunk.WrittenMask[pixelIndex])
			{
				continue;
			}

			var paletteIndex = decodedChunk.Pixels[pixelIndex];
			image[destinationX + x, destinationY + y] = MapPixelColor(paletteIndex, true, palette);
		}
	}
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

static int RunPaletteSuggestionMode(CommandLineOptions options)
{
	IReadOnlyList<string> cadNames;

	try
	{
		cadNames = ResolvePaletteSuggestionInputs(options.Inputs);
	}
	catch (Exception ex)
	{
		Console.Error.WriteLine(ex.Message);
		return 1;
	}

	if (cadNames.Count == 0)
	{
		Console.Error.WriteLine("No CAD basenames found.");
		return 1;
	}

	var paletteDirectory = GetDefaultPaletteDirectory();

	foreach (var cadName in cadNames)
	{
		var candidates = PaletteHintCatalog.GetCandidates(cadName);
		Console.WriteLine(cadName);

		if (candidates.Count == 0)
		{
			Console.WriteLine("  no known binary-backed palette matches recorded yet");
			Console.WriteLine();
			continue;
		}

		foreach (var candidate in candidates)
		{
			var status = candidate.PaletteFileName == "multiple"
				? "summary"
				: File.Exists(Path.Combine(paletteDirectory, candidate.PaletteFileName))
					? "present in Samples/PalFiles"
					: "not found in Samples/PalFiles";

			Console.WriteLine($"  {candidate.PaletteFileName} [{candidate.Source}; {status}]");
			Console.WriteLine($"    {candidate.Evidence}");
		}

		Console.WriteLine();
	}

	return 0;
}

static int RunPaletteTableExportMode(CommandLineOptions options)
{
	IReadOnlyList<string> cadNames;

	try
	{
		cadNames = ResolvePaletteSuggestionInputs(options.Inputs);
	}
	catch (Exception ex)
	{
		Console.Error.WriteLine(ex.Message);
		return 1;
	}

	if (cadNames.Count == 0)
	{
		Console.Error.WriteLine("No CAD basenames found.");
		return 1;
	}

	var outputPath = options.PaletteTableOutputPath!;
	var outputDirectory = Path.GetDirectoryName(outputPath);

	if (!string.IsNullOrEmpty(outputDirectory))
	{
		Directory.CreateDirectory(outputDirectory);
	}

	var paletteDirectory = GetDefaultPaletteDirectory();
	var lines = new List<string> { "cad_name,palette_file,source,status,evidence" };
	var resolvedCadCount = 0;

	foreach (var cadName in cadNames)
	{
		var candidates = PaletteHintCatalog.GetCandidates(cadName);

		if (candidates.Count == 0)
		{
			lines.Add($"{EscapeCsv(cadName)},,unknown,unresolved,");
			continue;
		}

		resolvedCadCount++;

		foreach (var candidate in candidates)
		{
			var status = File.Exists(Path.Combine(paletteDirectory, candidate.PaletteFileName))
				? "present in Samples/PalFiles"
				: "not found in Samples/PalFiles";

			lines.Add(string.Join(
				',',
				EscapeCsv(cadName),
				EscapeCsv(candidate.PaletteFileName),
				EscapeCsv(candidate.Source),
				EscapeCsv(status),
				EscapeCsv(candidate.Evidence)));
		}
	}

	File.WriteAllLines(outputPath, lines);

	Console.WriteLine($"palette table: {outputPath}");
	Console.WriteLine($"  cad entries: {cadNames.Count}");
	Console.WriteLine($"  resolved: {resolvedCadCount}");
	Console.WriteLine($"  unresolved: {cadNames.Count - resolvedCadCount}");
	Console.WriteLine();

	return 0;
}

static IReadOnlyList<string> ResolvePaletteSuggestionInputs(IReadOnlyList<string> inputs)
{
	var resolvedNames = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
	var candidates = inputs.Count == 0 ? new[] { GetDefaultCadPaletteInputDirectory() } : inputs;

	foreach (var candidate in candidates)
	{
		if (File.Exists(candidate))
		{
			if (!candidate.EndsWith(".cad", StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidOperationException($"Unsupported file type for palette suggestion mode: {candidate}");
			}

			resolvedNames.Add(Path.GetFileNameWithoutExtension(candidate));
			continue;
		}

		if (Directory.Exists(candidate))
		{
			foreach (var file in Directory.EnumerateFiles(candidate, "*.cad", SearchOption.TopDirectoryOnly))
			{
				resolvedNames.Add(Path.GetFileNameWithoutExtension(file));
			}

			continue;
		}

		if (Path.HasExtension(candidate))
		{
			throw new FileNotFoundException($"Input path does not exist: {candidate}");
		}

		resolvedNames.Add(candidate);
	}

	return resolvedNames.ToArray();
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
	PaletteFile? palette = null;

	if (options.PaletteFilePath is not null)
	{
		try
		{
			palette = PaletteFile.Load(options.PaletteFilePath);
			Console.WriteLine($"palette: {palette.SourcePath}");
			Console.WriteLine();
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine(ex.Message);
			return 1;
		}
	}

	var exitCode = 0;

	foreach (var inputFile in inputFiles)
	{
		try
		{
			var chunkBytes = File.ReadAllBytes(inputFile);
			var decodedChunk = RawChunkDecoder.Decode(chunkBytes);
			PrintRawChunkSummary(inputFile, chunkBytes.Length, decodedChunk);
			WriteDecodedRawChunkPng(inputFile, decodedChunk, outputDirectory, palette);
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
	var opaquePixels = decodedChunk.WrittenMask.Count(isWritten => isWritten);
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

static void WriteDecodedRawChunkPng(string inputFile, DecodedRawChunk decodedChunk, string outputDirectory, PaletteFile? palette)
{
	using var image = new Image<Rgba32>(decodedChunk.Width, decodedChunk.Height);

	for (var y = 0; y < decodedChunk.Height; y++)
	{
		for (var x = 0; x < decodedChunk.Width; x++)
		{
			var pixelIndex = (y * decodedChunk.Width) + x;
			var paletteIndex = decodedChunk.Pixels[pixelIndex];
			image[x, y] = MapPixelColor(paletteIndex, decodedChunk.WrittenMask[pixelIndex], palette);
		}
	}

	var outputPath = Path.Combine(outputDirectory, $"{Path.GetFileNameWithoutExtension(inputFile)}.png");
	image.Save(outputPath);

	Console.WriteLine($"  png: {outputPath}");
	Console.WriteLine();
}

static Rgba32 MapPixelColor(byte paletteIndex, bool isWritten, PaletteFile? palette)
{
	if (!isWritten)
	{
		return new Rgba32(0, 0, 0, 0);
	}

	if (palette is not null)
	{
		return palette.Colors[paletteIndex];
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
	var projectDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
	return Path.GetFullPath(Path.Combine(projectDirectory, "..", "Samples"));
}

static string GetDefaultRawChunkDirectory()
{
	var projectDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
	return Path.GetFullPath(Path.Combine(projectDirectory, "..", "TestOutput", "raw-chunks"));
}

static string GetDefaultCadPaletteInputDirectory()
{
	var cadFilesDirectory = Path.Combine(GetDefaultSamplesDirectory(), "CadFiles");
	return Directory.Exists(cadFilesDirectory) ? cadFilesDirectory : GetDefaultSamplesDirectory();
}

static string GetDefaultCadFrameOutputDirectory(CadFile cadFile)
{
	return Path.Combine(GetDefaultCadFrameOutputRootDirectory(), Path.GetFileNameWithoutExtension(cadFile.SourcePath));
}

static string GetDefaultCadFrameOutputRootDirectory()
{
	var projectDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
	return Path.GetFullPath(Path.Combine(projectDirectory, "..", "TestOutput", "rendered-frames"));
}

static string GetDefaultPaletteDirectory()
{
	var projectDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
	return Path.GetFullPath(Path.Combine(projectDirectory, "..", "Samples", "PalFiles"));
}

static string GetFrameFileName(string baseName, int frameIndex)
{
	return $"{baseName}_frame{frameIndex:D4}.png";
}

static string GetSequenceDirectoryName(int sequenceIndex)
{
	return $"sequence{sequenceIndex:D4}";
}

static string GetSequenceFrameFileName(string baseName, int sequenceIndex, int frameIndex)
{
	return $"{baseName}_seq{sequenceIndex:D4}_frame{frameIndex:D4}.png";
}

static string EscapeCsv(string value)
{
	if (value.IndexOfAny([',', '"', '\r', '\n']) < 0)
	{
		return value;
	}

	return $"\"{value.Replace("\"", "\"\"")}\"";
}

static bool TryParseArguments(string[] args, out CommandLineOptions options, out int exitCode)
{
	var inputs = new List<string>();
	string? jsonOutputDirectory = null;
	string? rawChunkOutputDirectory = null;
	string? decodedChunkOutputDirectory = null;
	string? paletteFilePath = null;
	string? paletteTableOutputPath = null;
	var showPaletteSuggestions = false;
	var useAllCadPalettes = false;

	for (var index = 0; index < args.Length; index++)
	{
		var argument = args[index];

		switch (argument)
		{
			case "-h":
			case "--help":
				PrintHelp();
					options = new CommandLineOptions(Array.Empty<string>(), null, null, null, null, null, false, false);
				exitCode = 0;
				return false;

			case "--json":
				if (index + 1 >= args.Length)
				{
					Console.Error.WriteLine("Missing directory after --json.");
						options = new CommandLineOptions(Array.Empty<string>(), null, null, null, null, null, false, false);
					exitCode = 1;
					return false;
				}

				jsonOutputDirectory = Path.GetFullPath(args[++index]);
				break;

			case "--suggest-palettes":
				showPaletteSuggestions = true;
				break;

			case "--export-palette-table":
				if (index + 1 >= args.Length)
				{
					Console.Error.WriteLine("Missing file path after --export-palette-table.");
						options = new CommandLineOptions(Array.Empty<string>(), null, null, null, null, null, false, false);
					exitCode = 1;
					return false;
				}

				paletteTableOutputPath = Path.GetFullPath(args[++index]);
				break;

				case "--all-palettes":
					useAllCadPalettes = true;
					break;

			case "--extract-raw":
				if (index + 1 >= args.Length)
				{
					Console.Error.WriteLine("Missing directory after --extract-raw.");
						options = new CommandLineOptions(Array.Empty<string>(), null, null, null, null, null, false, false);
					exitCode = 1;
					return false;
				}

				rawChunkOutputDirectory = Path.GetFullPath(args[++index]);
				break;

			case "--decode-chunks":
				if (index + 1 >= args.Length)
				{
					Console.Error.WriteLine("Missing directory after --decode-chunks.");
						options = new CommandLineOptions(Array.Empty<string>(), null, null, null, null, null, false, false);
					exitCode = 1;
					return false;
				}

				decodedChunkOutputDirectory = Path.GetFullPath(args[++index]);
				break;

			case "--palette":
				if (index + 1 >= args.Length)
				{
					Console.Error.WriteLine("Missing palette file after --palette.");
						options = new CommandLineOptions(Array.Empty<string>(), null, null, null, null, null, false, false);
					exitCode = 1;
					return false;
				}

				paletteFilePath = Path.GetFullPath(args[++index]);
				break;

			default:
				inputs.Add(argument);
				break;
		}
	}

	options = new CommandLineOptions(inputs, jsonOutputDirectory, rawChunkOutputDirectory, decodedChunkOutputDirectory, paletteFilePath, paletteTableOutputPath, showPaletteSuggestions, useAllCadPalettes);
	exitCode = 0;
	return true;
}

static void PrintHelp()
{
	Console.WriteLine("ZyCleaver - Zyclunt CAD parser");
	Console.WriteLine();
	Console.WriteLine("Usage:");
	Console.WriteLine("  dotnet run -- [--json <outputDir>] [--extract-raw <outputDir>] [--all-palettes] [<cad file>|<directory> ...]");
	Console.WriteLine("  dotnet run -- --decode-chunks <outputDir> [--palette <palette.pal>] [<raw chunk .bin>|<directory> ...]");
	Console.WriteLine("  dotnet run -- --suggest-palettes [<cad file>|<cad basename>|<directory> ...]");
	Console.WriteLine("  dotnet run -- --export-palette-table <output.csv> [<cad file>|<cad basename>|<directory> ...]");
	Console.WriteLine();
	Console.WriteLine("If no CAD inputs are supplied, the tool parses *.cad files from ../Samples.");
	Console.WriteLine("When explicit CAD inputs are supplied, the tool also writes per-palette frame and sequence exports to ../TestOutput/rendered-frames/<cad name>/<palette name>.");
	Console.WriteLine("Sequence PNGs now use sequence-local canvas bounds instead of one shared canvas across the whole CAD.");
	Console.WriteLine("Use --all-palettes to export one palette folder for every known palette candidate available on disk.");
	Console.WriteLine("If no raw chunk inputs are supplied with --decode-chunks, the tool decodes *.bin files from ../TestOutput/raw-chunks.");
	Console.WriteLine("Supported palette files are the 768-byte VGA palettes from ../Samples/PalFiles.");
	Console.WriteLine("Palette suggestions and exported tables default to ../Samples/CadFiles when that directory exists.");
	Console.WriteLine("Palette suggestions are based on palette filenames and stage-table matches already recovered from the binary.");
}

internal sealed record CommandLineOptions(IReadOnlyList<string> Inputs, string? JsonOutputDirectory, string? RawChunkOutputDirectory, string? DecodedChunkOutputDirectory, string? PaletteFilePath, string? PaletteTableOutputPath, bool ShowPaletteSuggestions, bool UseAllCadPalettes);

internal sealed record CadPaletteSelection(string DirectoryName, PaletteFile? Palette, string Description);

internal sealed record FrameRenderLayout(CadFrameRecord Frame, IReadOnlyList<FrameRenderPart> Parts);

internal sealed record FrameRenderPart(uint RawDataOffset, int X, int Y, DecodedRawChunk Chunk);

internal sealed record SequenceRenderPlan(CadSequenceStart SequenceStart, int? EndEntryIndexExclusive, IReadOnlyList<CadSequenceEntry> Entries, IReadOnlyList<int> FrameIndices);

internal readonly record struct FrameCanvasBounds(int MinX, int MinY, int MaxX, int MaxY)
{
	public int Width => MaxX - MinX;

	public int Height => MaxY - MinY;
}
