using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

var projectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
var sampleRoot = Path.GetFullPath(Path.Combine(projectRoot, "..", "Samples"));
var outputRoot = Path.GetFullPath(Path.Combine(projectRoot, "..", "TestOutput", "Scordato"));
var options = ParseArguments(args);

if (options.PalettePath is { } palettePath && !File.Exists(palettePath))
{
	Console.Error.WriteLine($"Palette file not found: {palettePath}");
	return 1;
}

var palette = options.PalettePath is { } resolvedPalettePath
	? IndexedPalette.LoadFile(resolvedPalettePath)
	: null;
var jsonOptions = new JsonSerializerOptions
{
	WriteIndented = true,
	DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
};

var inputFiles = ResolveInputFiles(options.InputPaths.ToArray(), sampleRoot).ToArray();

if (inputFiles.Length == 0)
{
	Console.Error.WriteLine("No .spb, .mob, or .fam files were found to inspect.");
	Console.Error.WriteLine($"Checked: {sampleRoot}");
	return 1;
}

Directory.CreateDirectory(outputRoot);

foreach (var inputFile in inputFiles)
{
	var archiveOutputDir = Path.Combine(outputRoot, Path.GetFileNameWithoutExtension(inputFile));
	ResetDirectory(archiveOutputDir);

	if (Path.GetExtension(inputFile).Equals(".fam", StringComparison.OrdinalIgnoreCase))
	{
		WriteFamInspectionOutput(
			inputFile,
			archiveOutputDir,
			jsonOptions,
			options.WriteBinaryOutputs,
			options.FamProbeSceneNames);
		continue;
	}

	var archive = ArchiveInspector.Inspect(inputFile);

	if (options.WriteBinaryOutputs)
	{
		File.WriteAllBytes(Path.Combine(archiveOutputDir, "header.bin"), archive.HeaderBytes);
		if (archive.HeaderPaletteBytes is not null)
		{
			File.WriteAllBytes(Path.Combine(archiveOutputDir, "header_palette.rgbx.bin"), archive.HeaderPaletteBytes);
		}
	}

	var outputPalette = palette ?? archive.HeaderPalette;

	foreach (var entry in archive.Entries)
	{
		var hasOutput = entry.Subresources.Count > 0
			|| entry.MobMetadataRecords.Count > 0
			|| entry.MobDataItems.Count > 0
			|| entry.MobMetadataRegion is not null
			|| entry.MobDataRegion is not null;

		if (!hasOutput)
		{
			continue;
		}

		var entryDir = Path.Combine(archiveOutputDir, $"entry_{entry.Manifest.Index:D3}");
		Directory.CreateDirectory(entryDir);

		foreach (var subresource in entry.Subresources)
		{
			var baseName = BuildPayloadBaseName(entry.Manifest, subresource.Manifest);
			var payloadFileName = $"{baseName}.bin";
			if (options.WriteBinaryOutputs)
			{
				File.WriteAllBytes(Path.Combine(entryDir, payloadFileName), subresource.Payload);
			}

			if (archive.Manifest.TypeCode == 0x2711 && subresource.Manifest.CandidateRowTableMatch)
			{
				if (SpbImageDecoder.TryDecode(subresource.Payload, out var decodedImage, out var decodeError))
				{
					var indexedFileName = $"{baseName}.indexed.bin";
					var maskFileName = $"{baseName}.mask.bin";

					subresource.Manifest.SpbDecodeSucceeded = true;
					subresource.Manifest.SpbRowOffsetsAreSelfRelative = true;
					subresource.Manifest.SpbCommandEncoding = "0x80..0xFF literal, 0x40..0x7F transparent skip, 0x00 row end";
					subresource.Manifest.DecodedOpaquePixels = decodedImage.OpaquePixelCount;
					subresource.Manifest.DecodedTransparentPixels = decodedImage.TransparentPixelCount;

					if (options.WriteBinaryOutputs)
					{
						File.WriteAllBytes(Path.Combine(entryDir, indexedFileName), decodedImage.Indices);
						File.WriteAllBytes(Path.Combine(entryDir, maskFileName), decodedImage.AlphaMask);
						subresource.Manifest.DecodedIndexedFile = indexedFileName;
						subresource.Manifest.DecodedMaskFile = maskFileName;
					}

					if (outputPalette is not null)
					{
						var pngFileName = $"{baseName}.png";
						ImageWriter.WritePng(Path.Combine(entryDir, pngFileName), decodedImage, outputPalette);
						subresource.Manifest.DecodedPngFile = pngFileName;
						subresource.Manifest.DecodedPaletteSource = outputPalette.SourceDescription;
					}
				}
				else
				{
					subresource.Manifest.SpbDecodeSucceeded = false;
					subresource.Manifest.SpbDecodeError = decodeError;
				}
			}
		}

		if (options.WriteBinaryOutputs && entry.MobMetadataRegion is not null)
		{
			File.WriteAllBytes(Path.Combine(entryDir, "metadata_region.bin"), entry.MobMetadataRegion);
		}

		if (options.WriteBinaryOutputs && entry.MobDataRegion is not null)
		{
			File.WriteAllBytes(Path.Combine(entryDir, "data_region.bin"), entry.MobDataRegion);
		}

		if (options.WriteBinaryOutputs && entry.MobMetadataRecords.Count > 0)
		{
			var metadataDir = Path.Combine(entryDir, "metadata");
			Directory.CreateDirectory(metadataDir);

			foreach (var record in entry.MobMetadataRecords)
			{
				File.WriteAllBytes(
					Path.Combine(metadataDir, BuildMobMetadataFileName(record.Manifest)),
					record.Payload);
			}
		}

		var decodedMobSprites = new Dictionary<int, DecodedSpbImage>();

		if (entry.MobDataItems.Count > 0)
		{
			var dataDir = Path.Combine(entryDir, "data");
			Directory.CreateDirectory(dataDir);

			foreach (var item in entry.MobDataItems)
			{
				var payloadFileName = BuildMobDataFileName(item.Manifest);
				var baseName = Path.GetFileNameWithoutExtension(payloadFileName);
				if (options.WriteBinaryOutputs)
				{
					File.WriteAllBytes(Path.Combine(dataDir, payloadFileName), item.Payload);
				}

				if (item.Manifest.CandidateSpriteLayout)
				{
					if (MobSpriteDecoder.TryDecode(item.Payload, out var decodedImage, out var decodeError))
					{
						var indexedFileName = $"{baseName}.indexed.bin";
						var maskFileName = $"{baseName}.mask.bin";

						item.Manifest.SpriteDecodeSucceeded = true;
						item.Manifest.DecodedOpaquePixels = decodedImage.OpaquePixelCount;
						item.Manifest.DecodedTransparentPixels = decodedImage.TransparentPixelCount;
						decodedMobSprites[item.Manifest.Index] = decodedImage;

						if (options.WriteBinaryOutputs)
						{
							File.WriteAllBytes(Path.Combine(dataDir, indexedFileName), decodedImage.Indices);
							File.WriteAllBytes(Path.Combine(dataDir, maskFileName), decodedImage.AlphaMask);
							item.Manifest.DecodedIndexedFile = indexedFileName;
							item.Manifest.DecodedMaskFile = maskFileName;
						}

						if (outputPalette is not null)
						{
							var pngFileName = $"{baseName}.png";
							ImageWriter.WritePng(Path.Combine(dataDir, pngFileName), decodedImage, outputPalette);
							item.Manifest.DecodedPngFile = pngFileName;
							item.Manifest.DecodedPaletteSource = outputPalette.SourceDescription;
						}
					}
					else
					{
						item.Manifest.SpriteDecodeSucceeded = false;
						item.Manifest.SpriteDecodeError = decodeError;
					}
				}
			}
		}

		if (outputPalette is not null && decodedMobSprites.Count > 0 && entry.MobMetadataRecords.Count > 0)
		{
			var metadataDir = Path.Combine(entryDir, "metadata");
			var framesDir = Path.Combine(metadataDir, "frames");
			var sequenceFramesDir = Path.Combine(metadataDir, "frames-sequence");
			Directory.CreateDirectory(metadataDir);

			foreach (var record in entry.MobMetadataRecords)
			{
				if (record.Manifest.Elements.Count == 0)
				{
					continue;
				}

				var resolvedFrames = new List<MobResolvedFrame>(record.Manifest.Elements.Count);
				var allFramesResolved = true;

				foreach (var element in record.Manifest.Elements)
				{
					if (!decodedMobSprites.TryGetValue(element.DataItemIndex, out var decodedFrame))
					{
						allFramesResolved = false;
						break;
					}

					resolvedFrames.Add(new MobResolvedFrame(element, decodedFrame));
				}

				if (!allFramesResolved || resolvedFrames.Count == 0)
				{
					continue;
				}

				var explicitPlacedFrames = ArchiveInspector.ResolveMobExplicitPlacedFrames(resolvedFrames);
				var explicitPlacement = MobFramePlacement.FromPlacedFrames(explicitPlacedFrames);
				List<MobPlacedFrame>? placedFrames = null;
				MobFramePlacement? sequencePlacement = null;
				if (options.WriteSequenceRelativeOutputs)
				{
					placedFrames = ArchiveInspector.ResolveMobPlacedFrames(resolvedFrames);
					sequencePlacement = MobFramePlacement.FromPlacedFrames(placedFrames);
				}

				var framesBaseName = Path.GetFileNameWithoutExtension(BuildMobMetadataFileName(record.Manifest));
				var explicitPngFileName = $"{framesBaseName}.png";
				var sequencePngFileName = $"{framesBaseName}.sequence.png";
				record.Manifest.DecodedFrameCount = explicitPlacedFrames.Count;
				record.Manifest.DecodedCanvasWidth = explicitPlacement.CanvasWidth;
				record.Manifest.DecodedCanvasHeight = explicitPlacement.CanvasHeight;
				record.Manifest.DecodedPlacementMode = "runtime-explicit-element-y-minus-height";

				if (options.WriteSequenceRelativeOutputs && sequencePlacement.HasValue)
				{
					record.Manifest.AlternateDecodedCanvasWidth = sequencePlacement.Value.CanvasWidth;
					record.Manifest.AlternateDecodedCanvasHeight = sequencePlacement.Value.CanvasHeight;
					record.Manifest.AlternateDecodedPlacementMode = "runtime-sequence-relative";
				}

				if (options.WriteStripOutputs)
				{
					try
					{
						ImageWriter.WritePlacedHorizontalStripPng(
							Path.Combine(metadataDir, explicitPngFileName),
							explicitPlacedFrames,
							explicitPlacement,
							outputPalette);
						record.Manifest.DecodedPngFile = explicitPngFileName;
					}
					catch (MobCanvasTooLargeException ex)
					{
						record.Manifest.DecodedExportError = ex.Message;
						Console.Error.WriteLine($"Skipping explicit strip for {Path.GetFileName(inputFile)} entry {entry.Manifest.Index:D3} record {record.Manifest.Index:D3} ({record.Manifest.DecodedName}): {ex.Message}");
					}

					if (options.WriteSequenceRelativeOutputs && placedFrames is not null && sequencePlacement.HasValue)
					{
						try
						{
							ImageWriter.WritePlacedHorizontalStripPng(
								Path.Combine(metadataDir, sequencePngFileName),
								placedFrames,
								sequencePlacement.Value,
								outputPalette);
							record.Manifest.AlternateDecodedPngFile = sequencePngFileName;
						}
						catch (MobCanvasTooLargeException ex)
						{
							record.Manifest.AlternateDecodedExportError = ex.Message;
							Console.Error.WriteLine($"Skipping sequence strip for {Path.GetFileName(inputFile)} entry {entry.Manifest.Index:D3} record {record.Manifest.Index:D3} ({record.Manifest.DecodedName}): {ex.Message}");
						}
					}
				}

				var explicitSequenceFrameDir = Path.Combine(framesDir, framesBaseName);
				Directory.CreateDirectory(explicitSequenceFrameDir);

				try
				{
					for (var frameIndex = 0; frameIndex < explicitPlacedFrames.Count; frameIndex++)
					{
						var explicitPlacedFrame = explicitPlacedFrames[frameIndex];
						var frameFileName = $"frame_{frameIndex:D3}_item_{explicitPlacedFrame.Element.DataItemIndex:D3}.png";
						ImageWriter.WritePlacedFramePng(
							Path.Combine(explicitSequenceFrameDir, frameFileName),
							explicitPlacedFrame,
							explicitPlacement,
							outputPalette);
						explicitPlacedFrame.Element.DecodedPlacementX = explicitPlacedFrame.PlacementX;
						explicitPlacedFrame.Element.DecodedPlacementY = explicitPlacedFrame.PlacementY;
						explicitPlacedFrame.Element.DecodedPngFile = Path.Combine("frames", framesBaseName, frameFileName);
					}
				}
				catch (MobCanvasTooLargeException ex)
				{
					record.Manifest.DecodedExportError ??= ex.Message;
					Console.Error.WriteLine($"Skipping explicit frame canvases for {Path.GetFileName(inputFile)} entry {entry.Manifest.Index:D3} record {record.Manifest.Index:D3} ({record.Manifest.DecodedName}): {ex.Message}");
				}

				if (options.WriteSequenceRelativeOutputs && placedFrames is not null && sequencePlacement.HasValue)
				{
					var sequenceFrameDir = Path.Combine(sequenceFramesDir, framesBaseName);
					Directory.CreateDirectory(sequenceFrameDir);

					try
					{
						for (var frameIndex = 0; frameIndex < placedFrames.Count; frameIndex++)
						{
							var placedFrame = placedFrames[frameIndex];
							var frameFileName = $"frame_{frameIndex:D3}_item_{placedFrame.Element.DataItemIndex:D3}.png";
							ImageWriter.WritePlacedFramePng(
								Path.Combine(sequenceFrameDir, frameFileName),
								placedFrame,
								sequencePlacement.Value,
								outputPalette);
							placedFrame.Element.AlternateDecodedPlacementX = placedFrame.PlacementX;
							placedFrame.Element.AlternateDecodedPlacementY = placedFrame.PlacementY;
							placedFrame.Element.AlternateDecodedPngFile = Path.Combine("frames-sequence", framesBaseName, frameFileName);
						}
					}
					catch (MobCanvasTooLargeException ex)
					{
						record.Manifest.AlternateDecodedExportError ??= ex.Message;
						Console.Error.WriteLine($"Skipping sequence frame canvases for {Path.GetFileName(inputFile)} entry {entry.Manifest.Index:D3} record {record.Manifest.Index:D3} ({record.Manifest.DecodedName}): {ex.Message}");
					}
				}
			}
		}
	}

	File.WriteAllText(
		Path.Combine(archiveOutputDir, "manifest.json"),
		JsonSerializer.Serialize(archive.Manifest, jsonOptions));

	Console.WriteLine(
		$"{Path.GetFileName(inputFile)}: type=0x{archive.Manifest.TypeCode:X4}, entries={archive.Manifest.EntryCount}, output={archiveOutputDir}");
}

return 0;

static IEnumerable<string> ResolveInputFiles(string[] args, string defaultRoot)
{
	if (args.Length == 0)
	{
		return Directory.EnumerateFiles(defaultRoot)
			.Where(IsSupportedArchive)
			.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase);
	}

	var resolved = new List<string>();

	foreach (var arg in args)
	{
		var fullPath = Path.GetFullPath(arg);

		if (Directory.Exists(fullPath))
		{
			resolved.AddRange(Directory.EnumerateFiles(fullPath).Where(IsSupportedArchive));
			continue;
		}

		if (File.Exists(fullPath) && IsSupportedArchive(fullPath))
		{
			resolved.Add(fullPath);
		}
	}

	return resolved.Distinct(StringComparer.OrdinalIgnoreCase);
}

static bool IsSupportedArchive(string path)
{
	var extension = Path.GetExtension(path);
	return extension.Equals(".spb", StringComparison.OrdinalIgnoreCase)
		|| extension.Equals(".mob", StringComparison.OrdinalIgnoreCase)
		|| extension.Equals(".fam", StringComparison.OrdinalIgnoreCase);
}

static AppOptions ParseArguments(string[] args)
{
	var inputPaths = new List<string>();
	var famProbeSceneNames = new List<string>();
	string? palettePath = null;
	var writeBinaryOutputs = false;
	var writeStripOutputs = false;
	var writeSequenceRelativeOutputs = false;

	for (var index = 0; index < args.Length; index++)
	{
		var arg = args[index];

		if (arg.Equals("--palette", StringComparison.OrdinalIgnoreCase)
			|| arg.Equals("-p", StringComparison.OrdinalIgnoreCase))
		{
			if (index + 1 >= args.Length)
			{
				throw new ArgumentException("Expected a palette path after --palette.");
			}

			palettePath = Path.GetFullPath(args[++index]);
			continue;
		}

		if (arg.Equals("--emit-binaries", StringComparison.OrdinalIgnoreCase)
			|| arg.Equals("--write-binaries", StringComparison.OrdinalIgnoreCase))
		{
			writeBinaryOutputs = true;
			continue;
		}

		if (arg.Equals("--emit-strips", StringComparison.OrdinalIgnoreCase))
		{
			writeStripOutputs = true;
			continue;
		}

		if (arg.Equals("--emit-sequence-relative", StringComparison.OrdinalIgnoreCase)
			|| arg.Equals("--emit-sequence-frames", StringComparison.OrdinalIgnoreCase))
		{
			writeSequenceRelativeOutputs = true;
			continue;
		}

		if (arg.Equals("--probe-scene", StringComparison.OrdinalIgnoreCase)
			|| arg.Equals("--fam-probe-scene", StringComparison.OrdinalIgnoreCase))
		{
			if (index + 1 >= args.Length)
			{
				throw new ArgumentException("Expected a scene name after --probe-scene.");
			}

			famProbeSceneNames.Add(args[++index]);
			continue;
		}

		inputPaths.Add(arg);
	}

	return new AppOptions
	{
		InputPaths = inputPaths,
		PalettePath = palettePath,
		WriteBinaryOutputs = writeBinaryOutputs,
		WriteStripOutputs = writeStripOutputs,
		WriteSequenceRelativeOutputs = writeSequenceRelativeOutputs,
		FamProbeSceneNames = famProbeSceneNames
	};
}

static string BuildPayloadBaseName(ArchiveEntryManifest entry, ArchiveSubresourceManifest subresource)
{
	var baseName = $"sub_{subresource.Index:D3}";

	if (subresource.CandidateWidth is { } width && subresource.CandidateHeight is { } height)
	{
		baseName += $"_{width}x{height}";
	}

	return baseName;
}

static string BuildMobMetadataFileName(MobMetadataRecordManifest manifest)
{
	var baseName = $"meta_{manifest.Index:D3}";
	if (!string.IsNullOrWhiteSpace(manifest.DecodedName))
	{
		baseName += $"_{SanitizeFileComponent(manifest.DecodedName)}";
	}

	return $"{baseName}.bin";
}

static string BuildMobDataFileName(MobDataItemManifest manifest)
{
	return $"item_{manifest.Index:D3}.bin";
}

static string SanitizeFileComponent(string value)
{
	var invalid = Path.GetInvalidFileNameChars();
	var builder = new StringBuilder(value.Length);

	foreach (var character in value.Trim())
	{
		builder.Append(invalid.Contains(character) ? '_' : character);
	}

	var sanitized = builder.ToString().Trim().TrimEnd('.');
	return string.IsNullOrEmpty(sanitized) ? "unnamed" : sanitized;
}

static void ResetDirectory(string path)
{
	if (Directory.Exists(path))
	{
		Directory.Delete(path, recursive: true);
	}

	Directory.CreateDirectory(path);
}

static void WriteFamInspectionOutput(
	string inputFile,
	string outputDir,
	JsonSerializerOptions jsonOptions,
	bool writeBinaryOutputs,
	IReadOnlyList<string> famProbeSceneNames)
{
	var retainDecompressedPayloads = writeBinaryOutputs || famProbeSceneNames.Count > 0;
	var inspection = FamInspector.Inspect(inputFile, retainDecompressedPayloads);
	var firstTableDir = Path.Combine(outputDir, "first_table");
	var secondTableDir = Path.Combine(outputDir, "second_table");
	Directory.CreateDirectory(firstTableDir);
	Directory.CreateDirectory(secondTableDir);

	if (writeBinaryOutputs)
	{
		File.WriteAllBytes(Path.Combine(outputDir, "header.bin"), inspection.HeaderBytes);
		File.WriteAllBytes(Path.Combine(outputDir, "first_table.bin"), inspection.FirstTableBytes);
		File.WriteAllBytes(Path.Combine(outputDir, "second_header.bin"), inspection.SecondHeaderBytes);
		File.WriteAllBytes(Path.Combine(outputDir, "second_table.bin"), inspection.SecondTableBytes);
	}

	foreach (var entry in inspection.Entries)
	{
		var relativeDir = entry.Manifest.TableName == "first" ? "first_table" : "second_table";
		var absoluteDir = entry.Manifest.TableName == "first" ? firstTableDir : secondTableDir;
		var filePrefix = entry.Manifest.TableName == "first" ? "chunk" : "blob";
		var payloadFileName = $"{filePrefix}_{entry.Manifest.Index:D3}_{SanitizeFileComponent(entry.Manifest.DecodedName)}.bin";
		File.WriteAllBytes(Path.Combine(absoluteDir, payloadFileName), entry.Payload);
		entry.Manifest.OutputFile = Path.Combine(relativeDir, payloadFileName);

		if (writeBinaryOutputs && entry.DecompressedPayload is not null)
		{
			var decodedFileName = $"{filePrefix}_{entry.Manifest.Index:D3}_{SanitizeFileComponent(entry.Manifest.DecodedName)}.decoded.bin";
			File.WriteAllBytes(Path.Combine(absoluteDir, decodedFileName), entry.DecompressedPayload);
			entry.Manifest.DecompressedOutputFile = Path.Combine(relativeDir, decodedFileName);

			if (entry.Manifest.RuntimePaletteOffset is { } paletteOffset
				&& entry.Manifest.RuntimePaletteLength is { } paletteLength
				&& entry.Manifest.RuntimeLookupTableOffset is { } lookupTableOffset
				&& entry.Manifest.RuntimeLookupTableLength is { } lookupTableLength)
			{
				var paletteFileName = $"{filePrefix}_{entry.Manifest.Index:D3}_{SanitizeFileComponent(entry.Manifest.DecodedName)}.tail-palette.rgbx.bin";
				var lookupTableFileName = $"{filePrefix}_{entry.Manifest.Index:D3}_{SanitizeFileComponent(entry.Manifest.DecodedName)}.tail-lookup-table.bin";
				File.WriteAllBytes(
					Path.Combine(absoluteDir, paletteFileName),
					entry.DecompressedPayload.AsSpan(paletteOffset, paletteLength).ToArray());
				File.WriteAllBytes(
					Path.Combine(absoluteDir, lookupTableFileName),
					entry.DecompressedPayload.AsSpan(lookupTableOffset, lookupTableLength).ToArray());
				entry.Manifest.RuntimePaletteOutputFile = Path.Combine(relativeDir, paletteFileName);
				entry.Manifest.RuntimeLookupTableOutputFile = Path.Combine(relativeDir, lookupTableFileName);
			}
		}
	}

	if (famProbeSceneNames.Count > 0)
	{
		WriteFamSceneProbeOutputs(outputDir, inspection, famProbeSceneNames);
	}

	File.WriteAllText(
		Path.Combine(outputDir, "manifest.json"),
		JsonSerializer.Serialize(inspection.Manifest, jsonOptions));

	Console.WriteLine(
		$"{Path.GetFileName(inputFile)}: type=FAM, firstTable={inspection.Manifest.FirstTableCount}, secondTable={inspection.Manifest.SecondTableCount}, output={outputDir}");
}

static void WriteFamSceneProbeOutputs(
	string outputDir,
	FamInspection inspection,
	IReadOnlyList<string> requestedSceneNames)
{
	var requested = new HashSet<string>(requestedSceneNames, StringComparer.OrdinalIgnoreCase);
	var probesRoot = Path.Combine(outputDir, "scene_probes");
	Directory.CreateDirectory(probesRoot);

	var secondTableByName = inspection.Entries
		.Where(entry => entry.Manifest.TableName == "second")
		.GroupBy(entry => entry.Manifest.DecodedName, StringComparer.OrdinalIgnoreCase)
		.ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

	foreach (var firstEntry in inspection.Entries.Where(entry => entry.Manifest.TableName == "first" && requested.Contains(entry.Manifest.DecodedName)))
	{
		firstEntry.Manifest.ProbeMode = "loader-derived-attr-base-overlay-scene";
		var resourceName = firstEntry.Manifest.EmbeddedName ?? firstEntry.Manifest.DecodedName;
		firstEntry.Manifest.ProbeResourceName = resourceName;

		if (!secondTableByName.TryGetValue(resourceName, out var secondEntry))
		{
			firstEntry.Manifest.ProbeError = $"No second-table resource named '{resourceName}' was found.";
			continue;
		}

		firstEntry.Manifest.ProbeResourceIndex = secondEntry.Manifest.Index;
		var probeDirName = $"scene_{firstEntry.Manifest.Index:D3}_{SanitizeFileComponent(firstEntry.Manifest.DecodedName)}";
		var probeDir = Path.Combine(probesRoot, probeDirName);
		ResetDirectory(probeDir);

		if (!FamSceneProbeWriter.TryWriteSceneDebugProbe(
			probeDir,
			firstEntry,
			secondEntry,
			out var planeFiles,
			out var palettePreviewFile,
			out var lookupTablePreviewFile,
			out var probeNotes,
			out var error))
		{
			firstEntry.Manifest.ProbeError = error;
			continue;
		}

		firstEntry.Manifest.ProbeOutputDirectory = Path.Combine("scene_probes", probeDirName);
		firstEntry.Manifest.ProbePlaneFiles.AddRange(planeFiles.Select(fileName => Path.Combine("scene_probes", probeDirName, fileName)));
		if (palettePreviewFile is not null)
		{
			firstEntry.Manifest.ProbePalettePreviewFile = Path.Combine("scene_probes", probeDirName, palettePreviewFile);
		}

		if (lookupTablePreviewFile is not null)
		{
			firstEntry.Manifest.ProbeLookupTablePreviewFile = Path.Combine("scene_probes", probeDirName, lookupTablePreviewFile);
		}

		firstEntry.Manifest.ProbeNotes.AddRange(probeNotes);
	}
}

static class ArchiveInspector
{
	private const int HeaderSize = 0x408;
	private const int SpbEntrySize = 36;
	private const int MobTopLevelEntrySize = 40;
	private const int MobMetadataRecordSize = 36;
	private const int MobMetadataElementSize = 20;
	private const int HeaderPaletteOffset = 0x06;
	private const int HeaderPaletteEntryCount = 255;
	private const string HeaderPaletteFormat = "RGBX";
	private static readonly Encoding KoreanEncoding = Encoding.GetEncoding(949);

	public static ArchiveInspection Inspect(string path)
	{
		var data = File.ReadAllBytes(path);

		if (data.Length < HeaderSize)
		{
			throw new InvalidDataException($"{path} is smaller than the fixed archive header.");
		}

		var typeCode = ReadUInt16(data, 0x0000);
		var headerTail = ReadUInt32(data, 0x0404);
		var entryCount = (ushort)(headerTail >> 16);
		var headerMarker = (ushort)(headerTail & 0xFFFF);
		var headerPaletteBytes = TryReadHeaderPaletteBytes(data);
		var headerPalette = headerPaletteBytes is not null
			? IndexedPalette.FromHeaderRgbx(headerPaletteBytes, HeaderPaletteOffset, HeaderPaletteEntryCount)
			: null;
		var entrySize = typeCode == 0x2712 ? MobTopLevelEntrySize : SpbEntrySize;

		var manifests = new List<ArchiveEntryManifest>(entryCount);
		var entries = new List<ArchiveEntryPayloads>(entryCount);

		for (var index = 0; index < entryCount; index++)
		{
			var entryOffset = HeaderSize + (index * entrySize);
			if (entryOffset + entrySize > data.Length)
			{
				break;
			}

			var rawNameBytes = data.AsSpan(entryOffset, 32).ToArray();
			var dataOffset = ReadUInt32(data, entryOffset + 32);
			var mobDataOffset = typeCode == 0x2712
				? ReadUInt32(data, entryOffset + 36)
				: (uint?)null;

			manifests.Add(new ArchiveEntryManifest
			{
				Index = index,
				DecodedName = DecodeName(rawNameBytes),
				RawNameHex = Convert.ToHexString(rawNameBytes),
				DataOffset = dataOffset,
				MobDataOffset = mobDataOffset
			});
		}

		if (typeCode == 0x2712)
		{
			PopulateMobEntries(data, manifests, entries);
		}
		else
		{
			PopulateSpbEntries(data, manifests, entries);
		}

		var manifest = new ArchiveManifest
		{
			FileName = Path.GetFileName(path),
			FullPath = path,
			TypeCode = typeCode,
			HeaderMarker = headerMarker,
			EntryCount = (ushort)entries.Count,
			HeaderSize = HeaderSize,
			EntrySize = entrySize,
			HeaderPaletteOffset = headerPaletteBytes is not null ? HeaderPaletteOffset : null,
			HeaderPaletteEntryCount = headerPaletteBytes is not null ? HeaderPaletteEntryCount : null,
			HeaderPaletteFormat = headerPaletteBytes is not null ? HeaderPaletteFormat : null,
			Entries = entries.Select(entry => entry.Manifest).ToList()
		};

		return new ArchiveInspection(
			manifest,
			entries,
			data.AsSpan(0, HeaderSize).ToArray(),
			headerPalette,
			headerPaletteBytes);
	}

	private static byte[]? TryReadHeaderPaletteBytes(byte[] data)
	{
		var requiredLength = HeaderPaletteOffset + (HeaderPaletteEntryCount * 4);
		if (data.Length < requiredLength)
		{
			return null;
		}

		return data.AsSpan(HeaderPaletteOffset, HeaderPaletteEntryCount * 4).ToArray();
	}

	private static void PopulateSpbEntries(byte[] data, List<ArchiveEntryManifest> manifests, List<ArchiveEntryPayloads> entries)
	{
		var validOffsets = manifests
			.Select(manifest => manifest.DataOffset)
			.Where(offset => offset > 0 && offset < data.Length)
			.OrderBy(offset => offset)
			.ToList();

		foreach (var entryManifest in manifests)
		{
			var subresources = new List<ArchiveSubresourcePayload>();

			if (entryManifest.DataOffset > 0 && entryManifest.DataOffset + 6 <= data.Length)
			{
				var blockOffset = checked((int)entryManifest.DataOffset);
				var blockSize = ReadUInt32(data, blockOffset);
				var subresourceCount = ReadUInt16(data, blockOffset + 4);
				var actualBlockEnd = FindNextOffset(validOffsets, entryManifest.DataOffset, (uint)data.Length);
				var actualBlockSpan = actualBlockEnd - entryManifest.DataOffset;

				entryManifest.OuterSize = blockSize;
				entryManifest.SubresourceCount = subresourceCount;
				entryManifest.ExpectedBlockSpan = blockSize + 4;
				entryManifest.ActualBlockSpan = actualBlockSpan;

				var subOffsets = new List<uint>(subresourceCount);
				var minimumSubresourceOffset = (uint)(6 + (subresourceCount * 4));

				for (var subIndex = 0; subIndex < subresourceCount; subIndex++)
				{
					var subTableOffset = blockOffset + 6 + (subIndex * 4);
					if (subTableOffset + 4 > data.Length)
					{
						break;
					}

					subOffsets.Add(ReadUInt32(data, subTableOffset));
				}

				for (var subIndex = 0; subIndex < subOffsets.Count; subIndex++)
				{
					var subOffset = subOffsets[subIndex];

					if (subOffset < minimumSubresourceOffset || subOffset >= actualBlockSpan)
					{
						continue;
					}

					var subHeaderOffset64 = (ulong)entryManifest.DataOffset + subOffset;
					if (subHeaderOffset64 + 4 > (ulong)data.Length)
					{
						continue;
					}

					var subHeaderOffset = (int)subHeaderOffset64;

					if (subHeaderOffset + 4 > data.Length)
					{
						continue;
					}

					var payloadSize = ReadUInt32(data, subHeaderOffset);
					var payloadOffset = subHeaderOffset + 4;
					var nextRelativeOffset = subOffsets
						.Where(offset => offset > subOffset)
						.DefaultIfEmpty(actualBlockSpan)
						.Min();
					var actualPayloadSpan = nextRelativeOffset > subOffset
						? nextRelativeOffset - subOffset - 4
						: 0;
					var boundedPayloadSize = Math.Min(payloadSize, actualPayloadSpan);
					var availablePayload = Math.Max(0, Math.Min((int)boundedPayloadSize, data.Length - payloadOffset));
					var payload = data.AsSpan(payloadOffset, availablePayload).ToArray();
					var candidateWidth = TryReadReasonableUInt32(payload, 0);
					var candidateHeight = TryReadReasonableUInt32(payload, 4);
					var candidateDataOffset = TryReadPayloadOffset(payload, 8);

					var subresourceManifest = new ArchiveSubresourceManifest
					{
						Index = subIndex,
						RelativeOffset = subOffset,
						PayloadSize = payloadSize,
						ActualPayloadSpan = actualPayloadSpan,
						CandidateWidth = candidateWidth,
						CandidateHeight = candidateHeight,
						CandidateDataOffset = candidateDataOffset,
						CandidateRowTableMatch = candidateWidth.HasValue
							&& candidateHeight.HasValue
							&& candidateDataOffset.HasValue
							&& candidateDataOffset.Value == 12 + (candidateHeight.Value * 4)
					};

					entryManifest.Subresources.Add(subresourceManifest);
					subresources.Add(new ArchiveSubresourcePayload(subresourceManifest, payload));
				}
			}

			entries.Add(new ArchiveEntryPayloads(entryManifest, subresources, new(), new(), null, null));
		}
	}

	private static void PopulateMobEntries(byte[] data, List<ArchiveEntryManifest> manifests, List<ArchiveEntryPayloads> entries)
	{
		foreach (var entryManifest in manifests)
		{
			var metadataRecords = new List<MobMetadataRecordPayload>();
			var dataItems = new List<MobDataItemPayload>();
			byte[]? metadataRegion = null;
			byte[]? dataRegion = null;

			if (entryManifest.DataOffset > 0 && entryManifest.DataOffset + 6 <= data.Length)
			{
				var metadataOffset = checked((int)entryManifest.DataOffset);
				var outerSize = ReadUInt32(data, metadataOffset);
				var recordCount = ReadUInt16(data, metadataOffset + 4);
				var actualSpan = ComputeBoundedSpan(data, entryManifest.DataOffset, outerSize + 4);

				entryManifest.OuterSize = outerSize;
				entryManifest.SubresourceCount = recordCount;
				entryManifest.ExpectedBlockSpan = outerSize + 4;
				entryManifest.ActualBlockSpan = actualSpan;
				metadataRegion = data.AsSpan(metadataOffset, (int)actualSpan).ToArray();

				if ((ulong)6 + ((ulong)recordCount * MobMetadataRecordSize) <= actualSpan)
				{
					var offsets = new List<uint>(recordCount);

					for (var recordIndex = 0; recordIndex < recordCount; recordIndex++)
					{
						var recordOffset = metadataOffset + 6 + (recordIndex * MobMetadataRecordSize);
						offsets.Add(ReadUInt32(data, recordOffset + 32));
					}

					for (var recordIndex = 0; recordIndex < recordCount; recordIndex++)
					{
						var recordOffset = metadataOffset + 6 + (recordIndex * MobMetadataRecordSize);
						var rawNameBytes = data.AsSpan(recordOffset, 32).ToArray();
						var relativeOffset = offsets[recordIndex];
						var minimumOffset = (uint)(6 + (recordCount * MobMetadataRecordSize));

						if (relativeOffset < minimumOffset || relativeOffset >= actualSpan)
						{
							continue;
						}

						var nextRelativeOffset = offsets
							.Where(offset => offset > relativeOffset)
							.DefaultIfEmpty(actualSpan)
							.Min();
						var actualRecordSpan = nextRelativeOffset > relativeOffset
							? nextRelativeOffset - relativeOffset
							: 0;
						var recordPayloadOffset = metadataOffset + (int)relativeOffset;
						var payload = data.AsSpan(recordPayloadOffset, (int)actualRecordSpan).ToArray();
						var metadataManifest = new MobMetadataRecordManifest
						{
							Index = recordIndex,
							DecodedName = DecodeName(rawNameBytes),
							RawNameHex = Convert.ToHexString(rawNameBytes),
							RelativeOffset = relativeOffset,
							ActualSpan = actualRecordSpan,
							WordPreview16 = BuildU16Preview(payload)
						};

						if (TryParseMobMetadataElements(payload, out var elementCount, out var unknownWord, out var elements))
						{
							metadataManifest.ElementCount = elementCount;
							metadataManifest.UnknownWord = unknownWord;
							metadataManifest.Elements.AddRange(elements);
						}

						entryManifest.MobMetadataRecords.Add(metadataManifest);
						metadataRecords.Add(new MobMetadataRecordPayload(metadataManifest, payload));
					}
				}
			}

			if (entryManifest.MobDataOffset is { } mobDataOffset && mobDataOffset > 0 && mobDataOffset + 6 <= data.Length)
			{
				var dataOffset = checked((int)mobDataOffset);
				var outerSize = ReadUInt32(data, dataOffset);
				var itemCount = ReadUInt16(data, dataOffset + 4);
				var actualSpan = ComputeBoundedSpan(data, mobDataOffset, outerSize + 4);

				entryManifest.MobDataOuterSize = outerSize;
				entryManifest.MobDataItemCount = itemCount;
				entryManifest.MobDataExpectedSpan = outerSize + 4;
				entryManifest.MobDataActualSpan = actualSpan;
				dataRegion = data.AsSpan(dataOffset, (int)actualSpan).ToArray();

				if ((ulong)6 + ((ulong)itemCount * 4) <= actualSpan)
				{
					var offsets = new List<uint>(itemCount);

					for (var itemIndex = 0; itemIndex < itemCount; itemIndex++)
					{
						offsets.Add(ReadUInt32(data, dataOffset + 6 + (itemIndex * 4)));
					}

					for (var itemIndex = 0; itemIndex < itemCount; itemIndex++)
					{
						var relativeOffset = offsets[itemIndex];
						var minimumOffset = (uint)(6 + (itemCount * 4));

						if (relativeOffset < minimumOffset || relativeOffset >= actualSpan)
						{
							continue;
						}

						var itemHeaderOffset = dataOffset + (int)relativeOffset;
						if (itemHeaderOffset + 4 > data.Length)
						{
							continue;
						}

						var payloadSize = ReadUInt32(data, itemHeaderOffset);
						var nextRelativeOffset = offsets
							.Where(offset => offset > relativeOffset)
							.DefaultIfEmpty(actualSpan)
							.Min();
						var actualPayloadSpan = nextRelativeOffset > relativeOffset
							? nextRelativeOffset - relativeOffset - 4
							: 0;
						var payloadOffset = itemHeaderOffset + 4;
						var boundedPayloadSize = Math.Min(payloadSize, actualPayloadSpan);
						var availablePayload = Math.Max(0, Math.Min((int)boundedPayloadSize, data.Length - payloadOffset));
						var payload = data.AsSpan(payloadOffset, availablePayload).ToArray();
						var dataManifest = new MobDataItemManifest
						{
							Index = itemIndex,
							RelativeOffset = relativeOffset,
							PayloadSize = payloadSize,
							ActualPayloadSpan = actualPayloadSpan,
							WordPreview16 = BuildU16Preview(payload)
						};

						if (MobSpriteDecoder.TryInspectLayout(payload, out var layout))
						{
							dataManifest.CandidateWidth = layout.Width;
							dataManifest.CandidateHeight = layout.Height;
							dataManifest.CandidateTailLength = layout.TailLength;
							dataManifest.CandidateRowCount = layout.RowCount;
							dataManifest.CandidateDataStart = layout.DataStart;
							dataManifest.CandidateSpriteLayout = true;
						}

						entryManifest.MobDataItems.Add(dataManifest);
						dataItems.Add(new MobDataItemPayload(dataManifest, payload));
					}
				}
			}

			entries.Add(new ArchiveEntryPayloads(entryManifest, new(), metadataRecords, dataItems, metadataRegion, dataRegion));
		}
	}

	private static uint ComputeBoundedSpan(byte[] data, uint offset, uint expectedSpan)
	{
		if (offset >= data.Length)
		{
			return 0;
		}

		var available = (uint)(data.Length - offset);
		return Math.Min(expectedSpan, available);
	}

	private static string BuildU16Preview(byte[] payload, int maxWords = 12)
	{
		if (payload.Length < 2)
		{
			return string.Empty;
		}

		var wordCount = Math.Min(maxWords, payload.Length / 2);
		var words = new List<string>(wordCount);

		for (var index = 0; index < wordCount; index++)
		{
			words.Add(ReadUInt16(payload, index * 2).ToString());
		}

		return string.Join(", ", words);
	}

	private static bool TryParseMobMetadataElements(
		byte[] payload,
		out ushort elementCount,
		out ushort unknownWord,
		out List<MobMetadataElementManifest> elements)
	{
		elementCount = 0;
		unknownWord = 0;
		elements = new List<MobMetadataElementManifest>();

		if (payload.Length < 4)
		{
			return false;
		}

		elementCount = ReadUInt16(payload, 0);
		unknownWord = ReadUInt16(payload, 2);
		var expectedLength = 4 + (elementCount * MobMetadataElementSize);

		if (payload.Length < expectedLength)
		{
			return false;
		}

		elements = new List<MobMetadataElementManifest>(elementCount);

		for (var elementIndex = 0; elementIndex < elementCount; elementIndex++)
		{
			var elementOffset = 4 + (elementIndex * MobMetadataElementSize);
			var words = new ushort[MobMetadataElementSize / 2];

			for (var wordIndex = 0; wordIndex < words.Length; wordIndex++)
			{
				words[wordIndex] = ReadUInt16(payload, elementOffset + (wordIndex * 2));
			}

			elements.Add(new MobMetadataElementManifest
			{
				Index = elementIndex,
				DataItemIndex = words[3],
				CandidatePlacementX = unchecked((short)words[1]),
				CandidatePlacementY = unchecked((short)words[2]),
				Words16 = words.ToList(),
				WordPreview16 = string.Join(", ", words.Select(static value => value.ToString()))
			});
		}

		return true;
	}

	public static List<MobPlacedFrame> ResolveMobPlacedFrames(
		IReadOnlyList<MobResolvedFrame> resolvedFrames)
	{
		var firstFrame = resolvedFrames[0];
		var firstX = firstFrame.Element.CandidatePlacementX ?? 0;
		var firstY = firstFrame.Element.CandidatePlacementY ?? 0;

		var placedFrames = new List<MobPlacedFrame>(resolvedFrames.Count);

		foreach (var frame in resolvedFrames)
		{
			var placementX = (frame.Element.CandidatePlacementX ?? 0) - firstX;
			var placementY = (frame.Element.CandidatePlacementY ?? 0) - firstY;

			placedFrames.Add(new MobPlacedFrame(frame.Element, frame.Image, placementX, placementY));
		}

		return placedFrames;
	}

	public static List<MobPlacedFrame> ResolveMobExplicitPlacedFrames(
		IReadOnlyList<MobResolvedFrame> resolvedFrames)
	{
		var firstFrame = resolvedFrames[0];
		var firstX = firstFrame.Element.CandidatePlacementX ?? 0;
		var firstY = firstFrame.Element.CandidatePlacementY ?? 0;
		var placedFrames = new List<MobPlacedFrame>(resolvedFrames.Count);

		foreach (var frame in resolvedFrames)
		{
			var placementX = (frame.Element.CandidatePlacementX ?? 0) - firstX;
			var placementY = (frame.Element.CandidatePlacementY ?? 0) - firstY - frame.Image.Height;
			placedFrames.Add(new MobPlacedFrame(frame.Element, frame.Image, placementX, placementY));
		}

		return placedFrames;
	}

	private static string DecodeName(byte[] rawNameBytes)
	{
		var zeroIndex = Array.IndexOf(rawNameBytes, (byte)0);
		var count = zeroIndex >= 0 ? zeroIndex : rawNameBytes.Length;
		if (count == 0)
		{
			return string.Empty;
		}

		var decoded = KoreanEncoding.GetString(rawNameBytes, 0, count).Trim();
		return decoded;
	}

	private static uint FindNextOffset(List<uint> sortedOffsets, uint currentOffset, uint fallback)
	{
		foreach (var offset in sortedOffsets)
		{
			if (offset > currentOffset)
			{
				return offset;
			}
		}

		return fallback;
	}

	private static ushort ReadUInt16(byte[] data, int offset)
	{
		return BitConverter.ToUInt16(data, offset);
	}

	private static uint ReadUInt32(byte[] data, int offset)
	{
		return BitConverter.ToUInt32(data, offset);
	}

	private static uint? TryReadReasonableUInt32(byte[] payload, int offset)
	{
		if (payload.Length < offset + 4)
		{
			return null;
		}

		var value = BitConverter.ToUInt32(payload, offset);
		if (value == 0 || value > 4096)
		{
			return null;
		}

		return value;
	}

	private static uint? TryReadPayloadOffset(byte[] payload, int offset)
	{
		if (payload.Length < offset + 4)
		{
			return null;
		}

		var value = BitConverter.ToUInt32(payload, offset);
		if (value == 0 || value > payload.Length)
		{
			return null;
		}

		return value;
	}
}

static class FamInspector
{
	private const int HeaderSize = 6;
	private const int DirectoryEntrySize = 16;
	private const int NameFieldSize = 12;
	private const int SecondaryHeaderSize = 6;
	private const int FirstChunkLzOffset = 0x2C;
	private const int SecondChunkLzOffset = 0x04;
	private const int SecondaryPaletteTailLength = 0x400;
	private const int SecondaryLookupTableLength = 0x10000;
	private static readonly Encoding AsciiEncoding = Encoding.ASCII;

	public static FamInspection Inspect(string path, bool retainDecompressedPayloads = false)
	{
		var data = File.ReadAllBytes(path);

		if (data.Length < HeaderSize)
		{
			throw new InvalidDataException($"{path} is smaller than the FAM header.");
		}

		var magic = AsciiEncoding.GetString(data, 0, 4).TrimEnd('\0');
		var firstTableCount = ReadUInt16(data, 4);
		var firstTableOffset = HeaderSize;
		var firstTableSize = firstTableCount * DirectoryEntrySize;
		var firstTableEndOffset = firstTableOffset + firstTableSize;

		if (firstTableEndOffset + SecondaryHeaderSize > data.Length)
		{
			throw new InvalidDataException($"{path} declares {firstTableCount} first-table FAM entries, but the first table overruns the file.");
		}

		var secondHeaderOffset = firstTableEndOffset;
		var secondHeaderValue = ReadUInt32(data, secondHeaderOffset);
		var secondTableCount = ReadUInt16(data, secondHeaderOffset + 4);
		var secondTableOffset = secondHeaderOffset + SecondaryHeaderSize;
		var secondTableSize = secondTableCount * DirectoryEntrySize;
		var secondTableEndOffset = secondTableOffset + secondTableSize;

		if (secondTableEndOffset > data.Length)
		{
			throw new InvalidDataException($"{path} declares {secondTableCount} second-table FAM entries, but the second table overruns the file.");
		}

		var entryManifests = new List<FamEntryManifest>(firstTableCount + secondTableCount);
		entryManifests.AddRange(ParseEntryManifests(data, firstTableOffset, firstTableCount, "first"));
		entryManifests.AddRange(ParseEntryManifests(data, secondTableOffset, secondTableCount, "second"));

		var sortedOffsets = entryManifests
			.Select(manifest => manifest.DataOffset)
			.Where(offset => offset >= secondTableEndOffset && offset < data.Length)
			.Distinct()
			.OrderBy(offset => offset)
			.ToList();

		var entryPayloads = BuildEntryPayloads(
			data,
			entryManifests,
			sortedOffsets,
			secondTableEndOffset,
			retainDecompressedPayloads);

		var manifestModel = new FamManifest
		{
			FileName = Path.GetFileName(path),
			FullPath = path,
			Magic = magic,
			FirstTableCount = firstTableCount,
			FirstTableOffset = firstTableOffset,
			FirstTableEndOffset = firstTableEndOffset,
			SecondHeaderOffset = secondHeaderOffset,
			SecondHeaderValue = secondHeaderValue,
			SecondTableCount = secondTableCount,
			SecondTableOffset = secondTableOffset,
			SecondTableEndOffset = secondTableEndOffset,
			HeaderSize = HeaderSize,
			EntrySize = DirectoryEntrySize,
			Entries = entryManifests
		};

		return new FamInspection(
			manifestModel,
			entryPayloads,
			data.AsSpan(0, HeaderSize).ToArray(),
			data.AsSpan(firstTableOffset, firstTableSize).ToArray(),
			data.AsSpan(secondHeaderOffset, SecondaryHeaderSize).ToArray(),
			data.AsSpan(secondTableOffset, secondTableSize).ToArray());
	}

	private static List<FamEntryManifest> ParseEntryManifests(
		byte[] data,
		int tableOffset,
		int entryCount,
		string tableName)
	{
		var manifests = new List<FamEntryManifest>(entryCount);

		for (var index = 0; index < entryCount; index++)
		{
			var entryOffset = tableOffset + (index * DirectoryEntrySize);
			var rawNameBytes = data.AsSpan(entryOffset, NameFieldSize).ToArray();
			var dataOffset = ReadUInt32(data, entryOffset + NameFieldSize);

			manifests.Add(new FamEntryManifest
			{
				TableName = tableName,
				Index = index,
				DecodedName = DecodeName(rawNameBytes, index),
				RawNameHex = Convert.ToHexString(rawNameBytes),
				DataOffset = dataOffset
			});
		}

		return manifests;
	}

	private static List<FamEntryPayload> BuildEntryPayloads(
		byte[] data,
		List<FamEntryManifest> manifests,
		List<uint> sortedOffsets,
		int minimumDataOffset,
		bool retainDecompressedPayloads)
	{
		var payloads = new List<FamEntryPayload>(manifests.Count);

		foreach (var manifest in manifests)
		{
			var dataOffset = manifest.DataOffset;
			var nextOffset = FindNextOffset(sortedOffsets, dataOffset, (uint)data.Length);

			if (dataOffset < minimumDataOffset || dataOffset > data.Length)
			{
				throw new InvalidDataException($"FAM {manifest.TableName} entry {manifest.Index} points outside the data region: 0x{dataOffset:X8}.");
			}

			if (nextOffset < dataOffset || nextOffset > data.Length)
			{
				throw new InvalidDataException($"FAM {manifest.TableName} entry {manifest.Index} has an invalid next offset: 0x{nextOffset:X8}.");
			}

			var payloadLength = checked((int)(nextOffset - dataOffset));
			var payload = data.AsSpan((int)dataOffset, payloadLength).ToArray();
			manifest.DataSpan = (uint)payloadLength;
			manifest.FirstBytesHex = BuildHexPreview(payload, 24);
			manifest.AsciiPreview.AddRange(ExtractAsciiPreview(payload, 8, 256));

			byte[]? decompressedPayload = null;
			if (manifest.TableName == "first")
			{
				PopulateFirstTableMetadata(manifest, payload, retainDecompressedPayloads, out decompressedPayload);
			}
			else
			{
				PopulateSecondTableMetadata(manifest, payload, retainDecompressedPayloads, out decompressedPayload);
			}

			payloads.Add(new FamEntryPayload(manifest, payload, decompressedPayload));
		}

		return payloads;
	}

	private static void PopulateFirstTableMetadata(
		FamEntryManifest manifest,
		byte[] payload,
		bool retainDecompressedPayloads,
		out byte[]? decompressedPayload)
	{
		decompressedPayload = null;

		if (payload.Length < FirstChunkLzOffset + 4)
		{
			return;
		}

		manifest.LeadingStoredSize = ReadUInt32(payload, 0);
		manifest.EmbeddedName = DecodeName(payload.AsSpan(4, NameFieldSize).ToArray(), manifest.Index);
		manifest.Field10 = ReadUInt16(payload, 0x10);
		manifest.Field12 = ReadUInt16(payload, 0x12);
		manifest.Field14 = ReadUInt16(payload, 0x14);
		manifest.Field16 = ReadUInt16(payload, 0x16);
		manifest.HeaderTailHex = Convert.ToHexString(payload.AsSpan(0x18, 0x14));
		manifest.HeaderDword28 = ReadUInt32(payload, 0x28);
		manifest.LzStreamOffset = FirstChunkLzOffset;
		manifest.LeadingStoredSizeMatchesPayloadSpan = manifest.LeadingStoredSize == payload.Length - FirstChunkLzOffset;
		manifest.DecompressedSize = ReadUInt32(payload, FirstChunkLzOffset);

		if (manifest.Field10 is { } field10 && manifest.Field12 is { } field12)
		{
			var cellCount = (ulong)field10 * field12;
			if (cellCount <= uint.MaxValue)
			{
				manifest.CellCountCandidate = (uint)cellCount;
			}

			if (manifest.DecompressedSize is { } decompressedSize)
			{
				manifest.MatchesFiveBytesPerCell = cellCount * 5 == decompressedSize;
			}
		}

		PopulateLzMetadata(manifest, payload, FirstChunkLzOffset, retainDecompressedPayloads, out decompressedPayload);
	}

	private static void PopulateSecondTableMetadata(
		FamEntryManifest manifest,
		byte[] payload,
		bool retainDecompressedPayloads,
		out byte[]? decompressedPayload)
	{
		decompressedPayload = null;

		if (payload.Length < SecondChunkLzOffset + 4)
		{
			return;
		}

		manifest.LeadingStoredSize = ReadUInt32(payload, 0);
		manifest.LzStreamOffset = SecondChunkLzOffset;
		manifest.LeadingStoredSizeMatchesPayloadSpan = manifest.LeadingStoredSize == payload.Length - SecondChunkLzOffset;
		manifest.DecompressedSize = ReadUInt32(payload, SecondChunkLzOffset);
		PopulateLzMetadata(manifest, payload, SecondChunkLzOffset, retainDecompressedPayloads, out decompressedPayload);

		if (manifest.LzDecodeSucceeded == true && manifest.DecompressedSize is { } decompressedSize)
		{
			var tailTotalLength = SecondaryPaletteTailLength + SecondaryLookupTableLength;
			if (decompressedSize >= tailTotalLength)
			{
				manifest.RuntimePaletteOffset = (int)decompressedSize - tailTotalLength;
				manifest.RuntimePaletteLength = SecondaryPaletteTailLength;
				manifest.RuntimeLookupTableOffset = (int)decompressedSize - SecondaryLookupTableLength;
				manifest.RuntimeLookupTableLength = SecondaryLookupTableLength;
			}
		}
	}

	private static void PopulateLzMetadata(
		FamEntryManifest manifest,
		byte[] payload,
		int lzStreamOffset,
		bool retainDecompressedPayloads,
		out byte[]? decompressedPayload)
	{
		decompressedPayload = null;

		if (!TryDecompressLz(payload, lzStreamOffset, out var decoded, out var bytesConsumed, out var error))
		{
			manifest.LzDecodeSucceeded = false;
			manifest.LzDecodeError = error;
			return;
		}

		manifest.LzDecodeSucceeded = true;
		manifest.LzBytesConsumed = bytesConsumed;
		if (retainDecompressedPayloads)
		{
			decompressedPayload = decoded;
		}
	}

	private static bool TryDecompressLz(
		byte[] payload,
		int startOffset,
		out byte[] decoded,
		out int bytesConsumed,
		out string? error)
	{
		decoded = Array.Empty<byte>();
		bytesConsumed = 0;
		error = null;

		if (payload.Length < startOffset + 4)
		{
			error = "Payload is too small to contain the LZ size dword.";
			return false;
		}

		var expectedLength64 = ReadUInt32(payload, startOffset);
		if (expectedLength64 > int.MaxValue)
		{
			error = $"LZ output size 0x{expectedLength64:X8} is too large to decode in memory.";
			return false;
		}

		var expectedLength = checked((int)expectedLength64);
		decoded = new byte[expectedLength];
		var window = new byte[0x1000];
		var sourceOffset = startOffset + 4;
		var outputOffset = 0;
		var flags = 0;
		var mask = 0;

		while (outputOffset < expectedLength)
		{
			if (mask == 0)
			{
				if (sourceOffset >= payload.Length)
				{
					error = "LZ stream ended before all output bytes were produced.";
					decoded = Array.Empty<byte>();
					return false;
				}

				flags = payload[sourceOffset++];
				mask = 0x80;
			}

			if ((flags & mask) != 0)
			{
				if (sourceOffset >= payload.Length)
				{
					error = "LZ literal run overran the payload.";
					decoded = Array.Empty<byte>();
					return false;
				}

				var value = payload[sourceOffset++];
				window[outputOffset & 0x0FFF] = value;
				decoded[outputOffset++] = value;
			}
			else
			{
				if (sourceOffset + 1 >= payload.Length)
				{
					error = "LZ back-reference overran the payload.";
					decoded = Array.Empty<byte>();
					return false;
				}

				var pair = (payload[sourceOffset] << 8) | payload[sourceOffset + 1];
				sourceOffset += 2;
				var windowOffset = (pair >> 4) & 0x0FFF;
				var count = (pair & 0x0F) + 3;

				for (var index = 0; index < count && outputOffset < expectedLength; index++)
				{
					var value = window[windowOffset];
					window[outputOffset & 0x0FFF] = value;
					decoded[outputOffset++] = value;
					windowOffset = (windowOffset + 1) & 0x0FFF;
				}
			}

			mask >>= 1;
		}

		bytesConsumed = sourceOffset - startOffset;
		return true;
	}

	private static uint FindNextOffset(List<uint> sortedOffsets, uint currentOffset, uint fallback)
	{
		foreach (var offset in sortedOffsets)
		{
			if (offset > currentOffset)
			{
				return offset;
			}
		}

		return fallback;
	}

	private static string DecodeName(byte[] rawNameBytes, int index)
	{
		var zeroIndex = Array.IndexOf(rawNameBytes, (byte)0);
		var count = zeroIndex >= 0 ? zeroIndex : rawNameBytes.Length;
		var decoded = AsciiEncoding.GetString(rawNameBytes, 0, count).Trim();
		return string.IsNullOrEmpty(decoded) ? $"entry_{index:D3}" : decoded;
	}

	private static string BuildHexPreview(byte[] payload, int maxBytes)
	{
		var previewLength = Math.Min(maxBytes, payload.Length);
		if (previewLength == 0)
		{
			return string.Empty;
		}

		return Convert.ToHexString(payload.AsSpan(0, previewLength));
	}

	private static List<string> ExtractAsciiPreview(byte[] payload, int maxStrings, int maxBytes)
	{
		var preview = new List<string>(maxStrings);
		var limit = Math.Min(payload.Length, maxBytes);
		var start = -1;

		for (var index = 0; index < limit; index++)
		{
			var value = payload[index];
			var isPrintable = value >= 32 && value <= 126;

			if (isPrintable)
			{
				if (start < 0)
				{
					start = index;
				}
				continue;
			}

			TryAppendAsciiPreview(payload, start, index, preview, maxStrings);
			if (preview.Count == maxStrings)
			{
				return preview;
			}

			start = -1;
		}

		TryAppendAsciiPreview(payload, start, limit, preview, maxStrings);
		return preview;
	}

	private static void TryAppendAsciiPreview(
		byte[] payload,
		int start,
		int end,
		List<string> preview,
		int maxStrings)
	{
		if (start < 0 || preview.Count >= maxStrings)
		{
			return;
		}

		var length = end - start;
		if (length < 4)
		{
			return;
		}

		preview.Add(AsciiEncoding.GetString(payload, start, length));
	}

	private static ushort ReadUInt16(byte[] data, int offset)
	{
		return BitConverter.ToUInt16(data, offset);
	}

	private static uint ReadUInt32(byte[] data, int offset)
	{
		return BitConverter.ToUInt32(data, offset);
	}
}

sealed class AppOptions
{
	public required List<string> InputPaths { get; init; }
	public string? PalettePath { get; init; }
	public bool WriteBinaryOutputs { get; init; }
	public bool WriteStripOutputs { get; init; }
	public bool WriteSequenceRelativeOutputs { get; init; }
	public required List<string> FamProbeSceneNames { get; init; }
}

static class FamSceneProbeWriter
{
	private const int RuntimeHeightTrim = 0x28;
	private const int AttributePlaneLeadRows = 20;
	private const int TilePlaneLeadRows = 40;
	private const int TileSideLength = 8;
	private const int BytesPerTile = TileSideLength * TileSideLength;
	private const int LookupTableSideLength = 256;

	public static bool TryWriteSceneDebugProbe(
		string outputDir,
		FamEntryPayload firstEntry,
		FamEntryPayload secondEntry,
		out List<string> planeFiles,
		out string? palettePreviewFile,
		out string? lookupTablePreviewFile,
		out List<string> probeNotes,
		out string? error)
	{
		planeFiles = new List<string>();
		palettePreviewFile = null;
		lookupTablePreviewFile = null;
		probeNotes = new List<string>();
		error = null;

		if (firstEntry.DecompressedPayload is null)
		{
			error = $"{firstEntry.Manifest.DecodedName} does not have a retained decompressed primary payload.";
			return false;
		}

		if (secondEntry.DecompressedPayload is null)
		{
			error = $"{secondEntry.Manifest.DecodedName} does not have a retained decompressed secondary payload.";
			return false;
		}

		if (firstEntry.Manifest.Field10 is not { } width || firstEntry.Manifest.Field12 is not { } headerHeight)
		{
			error = $"{firstEntry.Manifest.DecodedName} is missing the first-table dimensions needed for a scene probe.";
			return false;
		}

		var firstPayloadLength64 = (long)width * headerHeight * 5;
		if (firstPayloadLength64 > int.MaxValue)
		{
			error = $"{firstEntry.Manifest.DecodedName} is too large to probe in memory.";
			return false;
		}

		if (firstEntry.DecompressedPayload.Length != firstPayloadLength64)
		{
			error = $"{firstEntry.Manifest.DecodedName} primary payload is {firstEntry.DecompressedPayload.Length} bytes; expected {firstPayloadLength64} from the loader-backed width * height * 5 rule.";
			return false;
		}

		var runtimeHeight = headerHeight - RuntimeHeightTrim;
		if (runtimeHeight <= 0)
		{
			error = $"{firstEntry.Manifest.DecodedName} runtime height becomes {runtimeHeight} after the loader's 0x28-row trim.";
			return false;
		}

		var runtimeCellCount64 = (long)width * runtimeHeight;
		if (runtimeCellCount64 > int.MaxValue)
		{
			error = $"{firstEntry.Manifest.DecodedName} runtime tilemap is too large to probe in memory.";
			return false;
		}

		var runtimeCellCount = (int)runtimeCellCount64;
		var attributeOffset = checked(width * AttributePlaneLeadRows);
		var baseTileOffset = checked((width * headerHeight) + (width * TilePlaneLeadRows));
		var overlayTileOffset = checked((width * headerHeight * 3) + (width * TilePlaneLeadRows));
		var baseTileByteCount = checked(runtimeCellCount * 2);
		var overlayTileByteCount = checked(runtimeCellCount * 2);

		if (attributeOffset + runtimeCellCount > firstEntry.DecompressedPayload.Length
			|| baseTileOffset + baseTileByteCount > firstEntry.DecompressedPayload.Length
			|| overlayTileOffset + overlayTileByteCount > firstEntry.DecompressedPayload.Length)
		{
			error = $"{firstEntry.Manifest.DecodedName} loader-derived scene slices extend beyond the decoded primary payload.";
			return false;
		}

		if (secondEntry.Manifest.RuntimePaletteOffset is not { } paletteOffset
			|| secondEntry.Manifest.RuntimePaletteLength is not { } paletteLength
			|| secondEntry.Manifest.RuntimeLookupTableOffset is not { } lookupTableOffset
			|| secondEntry.Manifest.RuntimeLookupTableLength is not { } lookupTableLength)
		{
			error = $"{secondEntry.Manifest.DecodedName} is missing the second-buffer palette/lookup-table tail offsets required for a scene probe.";
			return false;
		}

		if (paletteLength % 4 != 0)
		{
			error = $"{secondEntry.Manifest.DecodedName} palette tail length {paletteLength} is not a multiple of 4-byte RGBX entries.";
			return false;
		}

		if (lookupTableLength != LookupTableSideLength * LookupTableSideLength)
		{
			error = $"{secondEntry.Manifest.DecodedName} lookup-table tail length {lookupTableLength} does not match the expected {LookupTableSideLength * LookupTableSideLength} bytes.";
			return false;
		}

		var palette = IndexedPalette.FromHeaderRgbx(
			secondEntry.DecompressedPayload.AsSpan(paletteOffset, paletteLength).ToArray(),
			0,
			paletteLength / 4,
			$"FORGA.FAM probe palette from {secondEntry.Manifest.DecodedName}");
		var lookupTableBytes = secondEntry.DecompressedPayload.AsSpan(lookupTableOffset, lookupTableLength).ToArray();
		var tileBankLength = paletteOffset;
		if (tileBankLength <= 0 || tileBankLength % BytesPerTile != 0)
		{
			error = $"{secondEntry.Manifest.DecodedName} tile bank length {tileBankLength} is not a clean multiple of {BytesPerTile}-byte 8x8 tiles.";
			return false;
		}

		var attributeValues = firstEntry.DecompressedPayload.AsSpan(attributeOffset, runtimeCellCount).ToArray();
		var baseTileIndices = ReadUInt16Array(firstEntry.DecompressedPayload, baseTileOffset, runtimeCellCount);
		var overlayTileIndices = ReadUInt16Array(firstEntry.DecompressedPayload, overlayTileOffset, runtimeCellCount);
		var tileBankBytes = secondEntry.DecompressedPayload.AsSpan(0, tileBankLength).ToArray();
		var attributeLowNibble = attributeValues.Select(static value => (byte)(value & 0x0F)).ToArray();
		var attributeHighBitMask = attributeValues.Select(static value => (byte)((value & 0x80) != 0 ? 0xFF : 0x00)).ToArray();
		var translationCellCount = attributeValues.Count(static value => (value & 0x80) != 0);

		var attributeFileName = "attribute_low_nibble.png";
		ImageWriter.WriteFamPlaneValueMapPng(
			Path.Combine(outputDir, attributeFileName),
			attributeLowNibble,
			width,
			runtimeHeight);
		planeFiles.Add(attributeFileName);

		var translationMaskFileName = "attribute_high_bit_mask.png";
		ImageWriter.WriteFamPlaneValueMapPng(
			Path.Combine(outputDir, translationMaskFileName),
			attributeHighBitMask,
			width,
			runtimeHeight);
		planeFiles.Add(translationMaskFileName);

		var baseTileFileName = "base_tile_indices.png";
		ImageWriter.WriteFamU16ValueMapPng(
			Path.Combine(outputDir, baseTileFileName),
			baseTileIndices,
			width,
			runtimeHeight);
		planeFiles.Add(baseTileFileName);

		var overlayTileFileName = "overlay_tile_indices.png";
		ImageWriter.WriteFamU16ValueMapPng(
			Path.Combine(outputDir, overlayTileFileName),
			overlayTileIndices,
			width,
			runtimeHeight);
		planeFiles.Add(overlayTileFileName);

		var compositeFileName = translationCellCount == 0
			? "scene_composite.png"
			: "scene_composite_pretranslation.png";
		ImageWriter.WriteFamSceneCompositePng(
			Path.Combine(outputDir, compositeFileName),
			baseTileIndices,
			overlayTileIndices,
			width,
			runtimeHeight,
			tileBankBytes,
			palette);
		planeFiles.Add(compositeFileName);

		palettePreviewFile = "secondary_palette_preview.png";
		ImageWriter.WriteFamPalettePreviewPng(
			Path.Combine(outputDir, palettePreviewFile),
			palette,
			paletteLength / 4);

		lookupTablePreviewFile = "secondary_lookup_table.png";
		ImageWriter.WriteFamLookupTablePng(
			Path.Combine(outputDir, lookupTablePreviewFile),
			lookupTableBytes);

		probeNotes.Add($"Loader-backed first-buffer layout: attribute bytes @ +0x{attributeOffset:X}, base tile indices @ +0x{baseTileOffset:X}, overlay tile indices @ +0x{overlayTileOffset:X}; runtime map size is {width}x{runtimeHeight} after the loader trims 0x28 rows from the header height {headerHeight}.");
		probeNotes.Add($"Secondary resource begins with {tileBankLength / BytesPerTile} 8x8 tiles ({tileBankLength} bytes) before the RGBX palette tail.");
		probeNotes.Add($"The scene composite follows the runtime base-tile plus transparent-overlay draw path. Cells with attribute bit 0x80 would need an additional translation step through the engine's color table.");
		probeNotes.Add(translationCellCount == 0
			? "AREA02-style validation: no attribute byte sets bit 0x80, so the emitted scene composite does not need the unresolved translation-table step."
			: $"Translation-table follow-up still required: {translationCellCount} cells set attribute bit 0x80.");
		probeNotes.Add($"Secondary resource offset 0x{paletteOffset:X} contributes {paletteLength / 4} RGBX palette entries.");
		probeNotes.Add($"Secondary resource offset 0x{lookupTableOffset:X} contributes a 256x256 lookup table; runtime tracing shows the engine consumes it as a remap table, not a tile bank.");

		return true;
	}

	private static ushort[] ReadUInt16Array(byte[] data, int offset, int elementCount)
	{
		var values = new ushort[elementCount];

		for (var index = 0; index < elementCount; index++)
		{
			values[index] = BitConverter.ToUInt16(data, offset + (index * 2));
		}

		return values;
	}
}

static class SpbImageDecoder
{
	public static bool TryDecode(byte[] payload, out DecodedSpbImage decodedImage, out string? error)
	{
		decodedImage = default!;
		error = null;

		if (payload.Length < 12)
		{
			error = "Payload is too small to contain an SPB image header.";
			return false;
		}

		var width = BitConverter.ToUInt32(payload, 0);
		var height = BitConverter.ToUInt32(payload, 4);
		var dataStart = BitConverter.ToUInt32(payload, 8);

		if (width == 0 || height == 0)
		{
			error = "SPB dimensions must be non-zero.";
			return false;
		}

		var expectedDataStart = checked((uint)(12 + (height * 4)));
		if (dataStart != expectedDataStart)
		{
			error = $"SPB dataStart mismatch: expected {expectedDataStart}, found {dataStart}.";
			return false;
		}

		if (dataStart > payload.Length)
		{
			error = "SPB dataStart points beyond the payload.";
			return false;
		}

		var pixelCount64 = (ulong)width * height;
		if (pixelCount64 > int.MaxValue)
		{
			error = "SPB dimensions are too large to decode in memory.";
			return false;
		}

		var pixelCount = (int)pixelCount64;
		var decodedIndices = new byte[pixelCount];
		var alphaMask = new byte[pixelCount];
		var opaquePixelCount = 0;
		var widthInt = checked((int)width);
		var heightInt = checked((int)height);

		for (var row = 0; row < heightInt; row++)
		{
			var rowEntryOffset = 12 + (row * 4);
			var rowRelativeOffset = BitConverter.ToUInt32(payload, rowEntryOffset);
			var rowDataOffset64 = (ulong)rowEntryOffset + rowRelativeOffset;

			if (rowDataOffset64 < dataStart || rowDataOffset64 >= (ulong)payload.Length)
			{
				error = $"Row {row} points outside the SPB data stream.";
				return false;
			}

			var sourceOffset = (int)rowDataOffset64;
			var x = 0;

			while (true)
			{
				if ((uint)sourceOffset >= payload.Length)
				{
					error = $"Row {row} overran the SPB payload.";
					return false;
				}

				var opcode = payload[sourceOffset++];

				if ((opcode & 0x80) != 0)
				{
					var count = opcode & 0x3F;
					if (x + count > widthInt)
					{
						error = $"Row {row} literal run exceeds the decoded width.";
						return false;
					}

					if (sourceOffset + count > payload.Length)
					{
						error = $"Row {row} literal run exceeds the SPB payload.";
						return false;
					}

					var rowPixelOffset = (row * widthInt) + x;
					Buffer.BlockCopy(payload, sourceOffset, decodedIndices, rowPixelOffset, count);
					alphaMask.AsSpan(rowPixelOffset, count).Fill(0xFF);
					sourceOffset += count;
					x += count;
					opaquePixelCount += count;
					continue;
				}

				if ((opcode & 0x40) != 0)
				{
					var count = opcode & 0x3F;
					if (x + count > widthInt)
					{
						error = $"Row {row} transparent run exceeds the decoded width.";
						return false;
					}

					x += count;
					continue;
				}

				if (opcode == 0)
				{
					if (x != widthInt)
					{
						error = $"Row {row} ended after {x} pixels, expected {widthInt}.";
						return false;
					}

					break;
				}

				error = $"Row {row} encountered unknown opcode 0x{opcode:X2}.";
				return false;
			}
		}

		decodedImage = new DecodedSpbImage(widthInt, heightInt, decodedIndices, alphaMask, opaquePixelCount);
		return true;
	}
}

static class MobSpriteDecoder
{
	private const int RuntimeTailLengthOffset = 0xA0;
	private const int RuntimeWidthOffset = 0xA4;
	private const int RowCountOffset = 0xA8;
	private const int DataStartOffset = 0xAC;
	private const int RowTableOffset = 0xB0;

	public static bool TryInspectLayout(byte[] payload, out MobSpriteLayout layout)
	{
		layout = default;

		if (payload.Length < RowTableOffset)
		{
			return false;
		}

		var width = BitConverter.ToUInt16(payload, 4) + 1;
		var height = BitConverter.ToUInt16(payload, 6) + 1;

		if (width == 0 || height == 0 || width > 4096 || height > 4096)
		{
			return false;
		}

		var tailLength = BitConverter.ToUInt32(payload, RuntimeTailLengthOffset);
		var runtimeWidth = BitConverter.ToUInt32(payload, RuntimeWidthOffset);
		var rowCount = BitConverter.ToUInt32(payload, RowCountOffset);
		var dataStart = BitConverter.ToUInt32(payload, DataStartOffset);

		if (runtimeWidth != width || rowCount != height)
		{
			return false;
		}

		var expectedRuntimeLength = checked((uint)(payload.Length + 4));
		if (RowCountOffset + tailLength != expectedRuntimeLength)
		{
			return false;
		}

		var rowTableLength = checked((long)RowTableOffset + (rowCount * 4));
		if (rowTableLength > payload.Length)
		{
			return false;
		}

		layout = new MobSpriteLayout(width, height, tailLength, rowCount, dataStart);
		return true;
	}

	public static bool TryDecode(byte[] payload, out DecodedSpbImage decodedImage, out string? error)
	{
		decodedImage = default!;
		error = null;

		if (!TryInspectLayout(payload, out var layout))
		{
			error = "Payload does not match the currently supported MOB sprite layout.";
			return false;
		}

		var pixelCount64 = (ulong)layout.Width * (ulong)layout.Height;
		if (pixelCount64 > int.MaxValue)
		{
			error = "MOB sprite dimensions are too large to decode in memory.";
			return false;
		}

		var pixelCount = (int)pixelCount64;
		var decodedIndices = new byte[pixelCount];
		var alphaMask = new byte[pixelCount];
		var opaquePixelCount = 0;

		for (var row = 0; row < layout.Height; row++)
		{
			var rowEntryOffset = RowTableOffset + (row * 4);
			var rowRelativeOffset = BitConverter.ToUInt32(payload, rowEntryOffset);
			var rowDataOffset64 = (ulong)rowEntryOffset + rowRelativeOffset;

			if (rowDataOffset64 >= (ulong)payload.Length)
			{
				error = $"Row {row} points outside the MOB item payload.";
				return false;
			}

			var sourceOffset = (int)rowDataOffset64;
			var x = 0;

			while (true)
			{
				if ((uint)sourceOffset >= payload.Length)
				{
					error = $"Row {row} overran the MOB item payload.";
					return false;
				}

				var opcode = payload[sourceOffset++];

				if ((opcode & 0x80) != 0)
				{
					var count = opcode & 0x3F;
					if (x + count > layout.Width)
					{
						error = $"Row {row} literal run exceeds the decoded width.";
						return false;
					}

					if (sourceOffset + count > payload.Length)
					{
						error = $"Row {row} literal run exceeds the MOB item payload.";
						return false;
					}

					var rowPixelOffset = (row * layout.Width) + x;
					Buffer.BlockCopy(payload, sourceOffset, decodedIndices, rowPixelOffset, count);
					alphaMask.AsSpan(rowPixelOffset, count).Fill(0xFF);
					sourceOffset += count;
					x += count;
					opaquePixelCount += count;
					continue;
				}

				if ((opcode & 0x40) != 0)
				{
					var count = opcode & 0x3F;
					if (x + count > layout.Width)
					{
						error = $"Row {row} transparent run exceeds the decoded width.";
						return false;
					}

					x += count;
					continue;
				}

				if (opcode == 0)
				{
					if (x != layout.Width)
					{
						error = $"Row {row} ended after {x} pixels, expected {layout.Width}.";
						return false;
					}

					break;
				}

				error = $"Row {row} encountered unknown opcode 0x{opcode:X2}.";
				return false;
			}
		}

		decodedImage = new DecodedSpbImage(layout.Width, layout.Height, decodedIndices, alphaMask, opaquePixelCount);
		return true;
	}
}

sealed record DecodedSpbImage(
	int Width,
	int Height,
	byte[] Indices,
	byte[] AlphaMask,
	int OpaquePixelCount)
{
	public int TransparentPixelCount => (Width * Height) - OpaquePixelCount;
}

sealed class IndexedPalette
{
	private const int RawVgaPaletteLength = 256 * 3;
	private const ushort SpbTypeCode = 0x2711;
	private const ushort MobTypeCode = 0x2712;
	private const int EmbeddedHeaderPaletteOffset = 0x06;
	private const int EmbeddedHeaderPaletteEntryCount = 255;

	private IndexedPalette(Rgba32[] colors, string sourceDescription)
	{
		Colors = colors;
		SourceDescription = sourceDescription;
	}

	public Rgba32[] Colors { get; }
	public string SourceDescription { get; }

	public static IndexedPalette LoadFile(string path)
	{
		var bytes = File.ReadAllBytes(path);

		if (TryLoadRawVga(bytes, path, out var rawPalette))
		{
			return rawPalette;
		}

		if (TryLoadEmbeddedHeaderRgbx(bytes, path, out var headerPalette))
		{
			return headerPalette;
		}

		throw new InvalidDataException(
			$"{path} is {bytes.Length} bytes, expected either a 768-byte raw VGA palette or a 0x2711/0x2712 file with an embedded header RGBX palette.");
	}

	private static bool TryLoadRawVga(byte[] bytes, string path, out IndexedPalette palette)
	{
		if (bytes.Length != RawVgaPaletteLength)
		{
			palette = null!;
			return false;
		}

		var colors = new Rgba32[256];
		for (var index = 0; index < colors.Length; index++)
		{
			var red = bytes[(index * 3) + 0];
			var green = bytes[(index * 3) + 1];
			var blue = bytes[(index * 3) + 2];

			if (red > 63 || green > 63 || blue > 63)
			{
				throw new InvalidDataException($"{path} does not look like a 6-bit raw VGA palette.");
			}

			colors[index] = new Rgba32(ScaleVgaComponent(red), ScaleVgaComponent(green), ScaleVgaComponent(blue), 0xFF);
		}

		palette = new IndexedPalette(colors, $"raw VGA palette from {Path.GetFileName(path)}");
		return true;
	}

	private static bool TryLoadEmbeddedHeaderRgbx(byte[] bytes, string path, out IndexedPalette palette)
	{
		var requiredLength = EmbeddedHeaderPaletteOffset + (EmbeddedHeaderPaletteEntryCount * 4);
		if (bytes.Length < requiredLength)
		{
			palette = null!;
			return false;
		}

		var typeCode = BitConverter.ToUInt16(bytes, 0);
		if (typeCode is not (SpbTypeCode or MobTypeCode))
		{
			palette = null!;
			return false;
		}

		palette = FromHeaderRgbx(
			bytes.AsSpan(EmbeddedHeaderPaletteOffset, EmbeddedHeaderPaletteEntryCount * 4).ToArray(),
			EmbeddedHeaderPaletteOffset,
			EmbeddedHeaderPaletteEntryCount,
			$"embedded header RGBX palette from {Path.GetFileName(path)}");
		return true;
	}

	public static IndexedPalette FromHeaderRgbx(byte[] rgbxBytes, int offset, int entryCount, string? sourceDescription = null)
	{
		var colors = new Rgba32[256];

		for (var index = 0; index < entryCount; index++)
		{
			var sourceOffset = index * 4;
			colors[index] = new Rgba32(
				rgbxBytes[sourceOffset + 0],
				rgbxBytes[sourceOffset + 1],
				rgbxBytes[sourceOffset + 2],
				0xFF);
		}

		colors[255] = default;
		return new IndexedPalette(
			colors,
			sourceDescription ?? $"embedded header RGBX palette @ 0x{offset:X} ({entryCount} entries)");
	}

	private static byte ScaleVgaComponent(byte value)
	{
		return (byte)((value * 255 + 31) / 63);
	}
}

static class ImageWriter
{
	private const long MaxImageSharpBufferLength = 4L * 1024 * 1024 * 1024;
	private const int BytesPerRgba32Pixel = 4;
	private const long MaxStripPixelCount = 134_217_728;
	private const int FamProbeCellScale = 4;
	private const int FamPalettePreviewColumns = 16;
	private const int FamPalettePreviewSwatchSize = 24;
	private const int FamLookupTableSideLength = 256;
	private const int FamSceneTileSideLength = 8;
	private const int FamSceneBytesPerTile = FamSceneTileSideLength * FamSceneTileSideLength;

	public static void WritePng(string path, DecodedSpbImage image, IndexedPalette palette)
	{
		using var output = new Image<Rgba32>(image.Width, image.Height);

		for (var y = 0; y < image.Height; y++)
		{
			var rowOffset = y * image.Width;

			for (var x = 0; x < image.Width; x++)
			{
				var pixelOffset = rowOffset + x;
				if (image.AlphaMask[pixelOffset] == 0)
				{
					output[x, y] = default;
					continue;
				}

				output[x, y] = palette.Colors[image.Indices[pixelOffset]];
			}
		}

		output.Save(path);
	}

	public static void WritePlacedHorizontalStripPng(
		string path,
		IReadOnlyList<MobPlacedFrame> frames,
		MobFramePlacement placement,
		IndexedPalette palette)
	{
		if (frames.Count == 0)
		{
			throw new ArgumentException("At least one frame is required.", nameof(frames));
		}

		const int spacing = 1;
		var width = ((long)placement.CanvasWidth * frames.Count) + ((long)spacing * (frames.Count - 1));
		var height = placement.CanvasHeight;
		ValidateCanvasDimensions(width, height, path, enforceStripLimit: true);

		using var output = new Image<Rgba32>((int)width, height);

		for (var frameIndex = 0; frameIndex < frames.Count; frameIndex++)
		{
			var slotOffsetX = frameIndex * (placement.CanvasWidth + spacing);
			DrawFrame(
				output,
				frames[frameIndex].Image,
				slotOffsetX + (frames[frameIndex].PlacementX - placement.MinX),
				frames[frameIndex].PlacementY - placement.MinY,
				palette);
		}

		output.Save(path);
	}

	public static void WriteFamPlaneValueMapPng(
		string path,
		byte[] planeValues,
		int width,
		int height)
	{
		var outputWidth = (long)width * FamProbeCellScale;
		var outputHeight = (long)height * FamProbeCellScale;
		ValidateCanvasDimensions(outputWidth, outputHeight, path, enforceStripLimit: false);

		using var output = new Image<Rgba32>((int)outputWidth, (int)outputHeight);
		var (minValue, maxValue) = ComputeNonZeroRange(planeValues);

		for (var cellIndex = 0; cellIndex < planeValues.Length; cellIndex++)
		{
			var color = BuildFamPlaneDebugColor(planeValues[cellIndex], minValue, maxValue);
			var cellX = (cellIndex % width) * FamProbeCellScale;
			var cellY = (cellIndex / width) * FamProbeCellScale;
			FillBlock(output, cellX, cellY, FamProbeCellScale, color);
		}

		output.Save(path);
	}

	public static void WriteFamU16ValueMapPng(
		string path,
		ushort[] planeValues,
		int width,
		int height)
	{
		var outputWidth = (long)width * FamProbeCellScale;
		var outputHeight = (long)height * FamProbeCellScale;
		ValidateCanvasDimensions(outputWidth, outputHeight, path, enforceStripLimit: false);

		using var output = new Image<Rgba32>((int)outputWidth, (int)outputHeight);
		var (minValue, maxValue) = ComputeNonZeroRange(planeValues);

		for (var cellIndex = 0; cellIndex < planeValues.Length; cellIndex++)
		{
			var color = BuildFamPlaneDebugColor(planeValues[cellIndex], minValue, maxValue);
			var cellX = (cellIndex % width) * FamProbeCellScale;
			var cellY = (cellIndex / width) * FamProbeCellScale;
			FillBlock(output, cellX, cellY, FamProbeCellScale, color);
		}

		output.Save(path);
	}

	public static void WriteFamSceneCompositePng(
		string path,
		ushort[] baseTileIndices,
		ushort[] overlayTileIndices,
		int width,
		int height,
		byte[] tileBankBytes,
		IndexedPalette palette)
	{
		var outputWidth = (long)width * FamSceneTileSideLength;
		var outputHeight = (long)height * FamSceneTileSideLength;
		ValidateCanvasDimensions(outputWidth, outputHeight, path, enforceStripLimit: false);

		using var output = new Image<Rgba32>((int)outputWidth, (int)outputHeight);
		var tileCount = tileBankBytes.Length / FamSceneBytesPerTile;

		for (var cellIndex = 0; cellIndex < baseTileIndices.Length; cellIndex++)
		{
			var cellX = (cellIndex % width) * FamSceneTileSideLength;
			var cellY = (cellIndex / width) * FamSceneTileSideLength;
			DrawFamTile(output, cellX, cellY, baseTileIndices[cellIndex], tileBankBytes, tileCount, palette, treat0xFFAsTransparent: false);

			if (cellIndex < overlayTileIndices.Length)
			{
				DrawFamTile(output, cellX, cellY, overlayTileIndices[cellIndex], tileBankBytes, tileCount, palette, treat0xFFAsTransparent: true);
			}
		}

		output.Save(path);
	}

	public static void WriteFamPalettePreviewPng(string path, IndexedPalette palette, int entryCount)
	{
		var rows = Math.Max(1, (entryCount + FamPalettePreviewColumns - 1) / FamPalettePreviewColumns);
		var outputWidth = (long)FamPalettePreviewColumns * FamPalettePreviewSwatchSize;
		var outputHeight = (long)rows * FamPalettePreviewSwatchSize;
		ValidateCanvasDimensions(outputWidth, outputHeight, path, enforceStripLimit: false);

		using var output = new Image<Rgba32>((int)outputWidth, (int)outputHeight);

		for (var index = 0; index < entryCount; index++)
		{
			var swatchX = (index % FamPalettePreviewColumns) * FamPalettePreviewSwatchSize;
			var swatchY = (index / FamPalettePreviewColumns) * FamPalettePreviewSwatchSize;
			FillBlock(output, swatchX, swatchY, FamPalettePreviewSwatchSize, palette.Colors[index]);
		}

		output.Save(path);
	}

	public static void WriteFamLookupTablePng(string path, byte[] lookupTableBytes)
	{
		if (lookupTableBytes.Length != FamLookupTableSideLength * FamLookupTableSideLength)
		{
			throw new InvalidDataException($"Lookup table preview requires exactly {FamLookupTableSideLength * FamLookupTableSideLength} bytes.");
		}

		ValidateCanvasDimensions(FamLookupTableSideLength, FamLookupTableSideLength, path, enforceStripLimit: false);
		using var output = new Image<Rgba32>(FamLookupTableSideLength, FamLookupTableSideLength);

		for (var index = 0; index < lookupTableBytes.Length; index++)
		{
			var value = lookupTableBytes[index];
			output[index % FamLookupTableSideLength, index / FamLookupTableSideLength] = new Rgba32(value, value, value, 0xFF);
		}

		output.Save(path);
	}

	private static (byte MinValue, byte MaxValue) ComputeNonZeroRange(byte[] values)
	{
		var foundNonZero = false;
		byte minValue = byte.MaxValue;
		byte maxValue = byte.MinValue;

		foreach (var value in values)
		{
			if (value == 0)
			{
				continue;
			}

			foundNonZero = true;
			if (value < minValue)
			{
				minValue = value;
			}

			if (value > maxValue)
			{
				maxValue = value;
			}
		}

		return foundNonZero ? (minValue, maxValue) : ((byte)0, (byte)0);
	}

	private static (ushort MinValue, ushort MaxValue) ComputeNonZeroRange(ushort[] values)
	{
		var foundNonZero = false;
		ushort minValue = ushort.MaxValue;
		ushort maxValue = ushort.MinValue;

		foreach (var value in values)
		{
			if (value == 0)
			{
				continue;
			}

			foundNonZero = true;
			if (value < minValue)
			{
				minValue = value;
			}

			if (value > maxValue)
			{
				maxValue = value;
			}
		}

		return foundNonZero ? (minValue, maxValue) : ((ushort)0, (ushort)0);
	}

	private static Rgba32 BuildFamPlaneDebugColor(byte value, byte minValue, byte maxValue)
	{
		if (value == 0)
		{
			return new Rgba32(0, 0, 0, 0xFF);
		}

		if (minValue == maxValue)
		{
			return new Rgba32(0xFF, 0xFF, 0xFF, 0xFF);
		}

		var range = Math.Max(1, maxValue - minValue);
		var intensity = 64 + (((value - minValue) * 191) / range);
		var channel = (byte)intensity;
		return new Rgba32(channel, channel, channel, 0xFF);
	}

	private static Rgba32 BuildFamPlaneDebugColor(ushort value, ushort minValue, ushort maxValue)
	{
		if (value == 0)
		{
			return new Rgba32(0, 0, 0, 0xFF);
		}

		if (minValue == maxValue)
		{
			return new Rgba32(0xFF, 0xFF, 0xFF, 0xFF);
		}

		var range = Math.Max(1, maxValue - minValue);
		var intensity = 64 + (((value - minValue) * 191) / range);
		var channel = (byte)intensity;
		return new Rgba32(channel, channel, channel, 0xFF);
	}

	private static void DrawFamTile(
		Image<Rgba32> output,
		int cellX,
		int cellY,
		ushort tileIndex,
		byte[] tileBankBytes,
		int tileCount,
		IndexedPalette palette,
		bool treat0xFFAsTransparent)
	{
		if (tileIndex == 0 || tileIndex > tileCount)
		{
			return;
		}

		var tileOffset = (tileIndex - 1) * FamSceneBytesPerTile;
		for (var y = 0; y < FamSceneTileSideLength; y++)
		{
			for (var x = 0; x < FamSceneTileSideLength; x++)
			{
				var paletteIndex = tileBankBytes[tileOffset + (y * FamSceneTileSideLength) + x];
				if (treat0xFFAsTransparent && paletteIndex == 0xFF)
				{
					continue;
				}

				output[cellX + x, cellY + y] = paletteIndex == 0xFF
					? default
					: palette.Colors[paletteIndex];
			}
		}
	}

	private static void FillBlock(Image<Rgba32> output, int startX, int startY, int blockSize, Rgba32 color)
	{
		for (var y = 0; y < blockSize; y++)
		{
			for (var x = 0; x < blockSize; x++)
			{
				output[startX + x, startY + y] = color;
			}
		}
	}

	public static void WritePlacedFramePng(
		string path,
		MobPlacedFrame frame,
		MobFramePlacement placement,
		IndexedPalette palette)
	{
		ValidateCanvasDimensions(placement.CanvasWidth, placement.CanvasHeight, path, enforceStripLimit: false);
		using var output = new Image<Rgba32>(placement.CanvasWidth, placement.CanvasHeight);
		DrawFrame(
			output,
			frame.Image,
			frame.PlacementX - placement.MinX,
			frame.PlacementY - placement.MinY,
			palette);
		output.Save(path);
	}

	private static void DrawFrame(
		Image<Rgba32> output,
		DecodedSpbImage frame,
		int destinationX,
		int destinationY,
		IndexedPalette palette)
	{
		for (var y = 0; y < frame.Height; y++)
		{
			var rowOffset = y * frame.Width;

			for (var x = 0; x < frame.Width; x++)
			{
				var pixelOffset = rowOffset + x;
				if (frame.AlphaMask[pixelOffset] == 0)
				{
					continue;
				}

				output[destinationX + x, destinationY + y] = palette.Colors[frame.Indices[pixelOffset]];
			}
		}
	}

	private static void ValidateCanvasDimensions(long width, long height, string path, bool enforceStripLimit)
	{
		if (width <= 0 || height <= 0 || width > int.MaxValue || height > int.MaxValue)
		{
			throw new MobCanvasTooLargeException($"Canvas {width}x{height} for {Path.GetFileName(path)} is outside the supported size range.");
		}

		var pixelCount = width * height;
		if (pixelCount > MaxImageSharpBufferLength / BytesPerRgba32Pixel)
		{
			throw new MobCanvasTooLargeException($"Canvas {width}x{height} for {Path.GetFileName(path)} exceeds the ImageSharp allocation limit.");
		}

		if (enforceStripLimit && pixelCount > MaxStripPixelCount)
		{
			throw new MobCanvasTooLargeException($"Canvas {width}x{height} for {Path.GetFileName(path)} exceeds the strip export limit; use the per-frame outputs instead.");
		}
	}
}

sealed class MobCanvasTooLargeException : Exception
{
	public MobCanvasTooLargeException(string message)
		: base(message)
	{
	}
}

sealed record ArchiveInspection(
	ArchiveManifest Manifest,
	List<ArchiveEntryPayloads> Entries,
	byte[] HeaderBytes,
	IndexedPalette? HeaderPalette,
	byte[]? HeaderPaletteBytes);

sealed record FamInspection(
	FamManifest Manifest,
	List<FamEntryPayload> Entries,
	byte[] HeaderBytes,
	byte[] FirstTableBytes,
	byte[] SecondHeaderBytes,
	byte[] SecondTableBytes);

sealed record ArchiveEntryPayloads(
	ArchiveEntryManifest Manifest,
	List<ArchiveSubresourcePayload> Subresources,
	List<MobMetadataRecordPayload> MobMetadataRecords,
	List<MobDataItemPayload> MobDataItems,
	byte[]? MobMetadataRegion,
	byte[]? MobDataRegion);

sealed record ArchiveSubresourcePayload(
	ArchiveSubresourceManifest Manifest,
	byte[] Payload);

sealed record MobMetadataRecordPayload(
	MobMetadataRecordManifest Manifest,
	byte[] Payload);

sealed record MobDataItemPayload(
	MobDataItemManifest Manifest,
	byte[] Payload);

sealed record FamEntryPayload(
	FamEntryManifest Manifest,
	byte[] Payload,
	byte[]? DecompressedPayload);

sealed record MobResolvedFrame(
	MobMetadataElementManifest Element,
	DecodedSpbImage Image);

sealed record MobPlacedFrame(
	MobMetadataElementManifest Element,
	DecodedSpbImage Image,
	int PlacementX,
	int PlacementY);

readonly record struct MobSpriteLayout(
	int Width,
	int Height,
	uint TailLength,
	uint RowCount,
	uint DataStart);

readonly record struct MobFramePlacement(
	int MinX,
	int MinY,
	int CanvasWidth,
	int CanvasHeight)
{
	public static MobFramePlacement FromPlacedFrames(IReadOnlyList<MobPlacedFrame> frames)
	{
		var minX = frames.Min(frame => frame.PlacementX);
		var minY = frames.Min(frame => frame.PlacementY);
		var maxX = frames.Max(frame => frame.PlacementX + frame.Image.Width);
		var maxY = frames.Max(frame => frame.PlacementY + frame.Image.Height);
		return new MobFramePlacement(minX, minY, maxX - minX, maxY - minY);
	}
}

sealed class ArchiveManifest
{
	public required string FileName { get; init; }
	public required string FullPath { get; init; }
	public required ushort TypeCode { get; init; }
	public required ushort HeaderMarker { get; init; }
	public required ushort EntryCount { get; init; }
	public required int HeaderSize { get; init; }
	public required int EntrySize { get; init; }
	public int? HeaderPaletteOffset { get; init; }
	public int? HeaderPaletteEntryCount { get; init; }
	public string? HeaderPaletteFormat { get; init; }
	public required List<ArchiveEntryManifest> Entries { get; init; }
}

sealed class FamManifest
{
	public required string FileName { get; init; }
	public required string FullPath { get; init; }
	public required string Magic { get; init; }
	public required ushort FirstTableCount { get; init; }
	public required int FirstTableOffset { get; init; }
	public required int FirstTableEndOffset { get; init; }
	public required int SecondHeaderOffset { get; init; }
	public required uint SecondHeaderValue { get; init; }
	public required ushort SecondTableCount { get; init; }
	public required int SecondTableOffset { get; init; }
	public required int SecondTableEndOffset { get; init; }
	public required int HeaderSize { get; init; }
	public required int EntrySize { get; init; }
	public required List<FamEntryManifest> Entries { get; init; }
}

sealed class ArchiveEntryManifest
{
	public required int Index { get; init; }
	public required string DecodedName { get; init; }
	public required string RawNameHex { get; init; }
	public required uint DataOffset { get; init; }
	public uint? MobDataOffset { get; init; }
	public uint? OuterSize { get; set; }
	public ushort? SubresourceCount { get; set; }
	public uint? ExpectedBlockSpan { get; set; }
	public uint? ActualBlockSpan { get; set; }
	public uint? MobDataOuterSize { get; set; }
	public ushort? MobDataItemCount { get; set; }
	public uint? MobDataExpectedSpan { get; set; }
	public uint? MobDataActualSpan { get; set; }
	public List<ArchiveSubresourceManifest> Subresources { get; } = new();
	public List<MobMetadataRecordManifest> MobMetadataRecords { get; } = new();
	public List<MobDataItemManifest> MobDataItems { get; } = new();
}

sealed class FamEntryManifest
{
	public required string TableName { get; init; }
	public required int Index { get; init; }
	public required string DecodedName { get; init; }
	public required string RawNameHex { get; init; }
	public required uint DataOffset { get; init; }
	public uint DataSpan { get; set; }
	public string? FirstBytesHex { get; set; }
	public string? OutputFile { get; set; }
	public uint? LeadingStoredSize { get; set; }
	public bool? LeadingStoredSizeMatchesPayloadSpan { get; set; }
	public int? LzStreamOffset { get; set; }
	public uint? DecompressedSize { get; set; }
	public int? LzBytesConsumed { get; set; }
	public bool? LzDecodeSucceeded { get; set; }
	public string? LzDecodeError { get; set; }
	public string? DecompressedOutputFile { get; set; }
	public string? EmbeddedName { get; set; }
	public ushort? Field10 { get; set; }
	public ushort? Field12 { get; set; }
	public ushort? Field14 { get; set; }
	public ushort? Field16 { get; set; }
	public string? HeaderTailHex { get; set; }
	public uint? HeaderDword28 { get; set; }
	public uint? CellCountCandidate { get; set; }
	public bool? MatchesFiveBytesPerCell { get; set; }
	public int? RuntimePaletteOffset { get; set; }
	public int? RuntimePaletteLength { get; set; }
	public int? RuntimeLookupTableOffset { get; set; }
	public int? RuntimeLookupTableLength { get; set; }
	public string? RuntimePaletteOutputFile { get; set; }
	public string? RuntimeLookupTableOutputFile { get; set; }
	public string? ProbeMode { get; set; }
	public string? ProbeResourceName { get; set; }
	public int? ProbeResourceIndex { get; set; }
	public string? ProbeOutputDirectory { get; set; }
	public string? ProbePalettePreviewFile { get; set; }
	public string? ProbeLookupTablePreviewFile { get; set; }
	public string? ProbeError { get; set; }
	public List<string> ProbePlaneFiles { get; } = new();
	public List<string> ProbeNotes { get; } = new();
	public List<string> AsciiPreview { get; } = new();
}

sealed class MobMetadataRecordManifest
{
	public required int Index { get; init; }
	public required string DecodedName { get; init; }
	public required string RawNameHex { get; init; }
	public required uint RelativeOffset { get; init; }
	public required uint ActualSpan { get; init; }
	public ushort? ElementCount { get; set; }
	public ushort? UnknownWord { get; set; }
	public string? WordPreview16 { get; init; }
	public int? DecodedFrameCount { get; set; }
	public int? DecodedCanvasWidth { get; set; }
	public int? DecodedCanvasHeight { get; set; }
	public string? DecodedPlacementMode { get; set; }
	public string? DecodedPngFile { get; set; }
	public string? DecodedExportError { get; set; }
	public int? AlternateDecodedCanvasWidth { get; set; }
	public int? AlternateDecodedCanvasHeight { get; set; }
	public string? AlternateDecodedPlacementMode { get; set; }
	public string? AlternateDecodedPngFile { get; set; }
	public string? AlternateDecodedExportError { get; set; }
	public List<MobMetadataElementManifest> Elements { get; } = new();
}

sealed class MobMetadataElementManifest
{
	public required int Index { get; init; }
	public required ushort DataItemIndex { get; init; }
	public int? CandidatePlacementX { get; init; }
	public int? CandidatePlacementY { get; init; }
	public int? DecodedPlacementX { get; set; }
	public int? DecodedPlacementY { get; set; }
	public int? AlternateDecodedPlacementX { get; set; }
	public int? AlternateDecodedPlacementY { get; set; }
	public required List<ushort> Words16 { get; init; }
	public string? WordPreview16 { get; init; }
	public string? DecodedPngFile { get; set; }
	public string? AlternateDecodedPngFile { get; set; }
}

sealed class MobDataItemManifest
{
	public required int Index { get; init; }
	public required uint RelativeOffset { get; init; }
	public required uint PayloadSize { get; init; }
	public required uint ActualPayloadSpan { get; init; }
	public string? WordPreview16 { get; init; }
	public int? CandidateWidth { get; set; }
	public int? CandidateHeight { get; set; }
	public uint? CandidateTailLength { get; set; }
	public uint? CandidateRowCount { get; set; }
	public uint? CandidateDataStart { get; set; }
	public bool CandidateSpriteLayout { get; set; }
	public bool? SpriteDecodeSucceeded { get; set; }
	public string? SpriteDecodeError { get; set; }
	public int? DecodedOpaquePixels { get; set; }
	public int? DecodedTransparentPixels { get; set; }
	public string? DecodedIndexedFile { get; set; }
	public string? DecodedMaskFile { get; set; }
	public string? DecodedPngFile { get; set; }
	public string? DecodedPaletteSource { get; set; }
}

sealed class ArchiveSubresourceManifest
{
	public required int Index { get; init; }
	public required uint RelativeOffset { get; init; }
	public required uint PayloadSize { get; init; }
	public required uint ActualPayloadSpan { get; init; }
	public uint? CandidateWidth { get; init; }
	public uint? CandidateHeight { get; init; }
	public uint? CandidateDataOffset { get; init; }
	public bool CandidateRowTableMatch { get; init; }
	public bool? SpbDecodeSucceeded { get; set; }
	public string? SpbDecodeError { get; set; }
	public bool? SpbRowOffsetsAreSelfRelative { get; set; }
	public string? SpbCommandEncoding { get; set; }
	public int? DecodedOpaquePixels { get; set; }
	public int? DecodedTransparentPixels { get; set; }
	public string? DecodedIndexedFile { get; set; }
	public string? DecodedMaskFile { get; set; }
	public string? DecodedPngFile { get; set; }
	public string? DecodedPaletteSource { get; set; }
}
