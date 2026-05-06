using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

var projectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
var sampleRoot = Path.GetFullPath(Path.Combine(projectRoot, "..", "Samples"));
var outputRoot = Path.GetFullPath(Path.Combine(projectRoot, "..", "TestOutput", "Scordato"));
var options = ParseArguments(args);
var jsonOptions = new JsonSerializerOptions
{
	WriteIndented = true,
	DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
};

var inputFiles = ResolveInputFiles(options.InputPaths.ToArray(), sampleRoot).ToArray();

if (inputFiles.Length == 0)
{
	Console.Error.WriteLine("No supported files were found to inspect.");
	Console.Error.WriteLine("Default scan includes .spb, .mob, and .fam files; explicit input also supports CHAR.DAT, SAGA.BIN, and SAGA.SCP.");
	Console.Error.WriteLine($"Checked: {sampleRoot}");
	return 1;
}

Directory.CreateDirectory(outputRoot);

string? resolvedPalettePath;
try
{
	resolvedPalettePath = ResolvePalettePath(options, sampleRoot, outputRoot, jsonOptions);
}
catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or DirectoryNotFoundException or InvalidDataException or JsonException)
{
	Console.Error.WriteLine(ex.Message);
	return 1;
}

var palette = resolvedPalettePath is { } palettePath
	? IndexedPalette.LoadFile(palettePath)
	: null;
var hasExplicitPaletteSelection = palette is not null;
var hasPreviewPaletteOverride = !string.IsNullOrWhiteSpace(options.PalettePath);
List<TransitionScenePaletteCandidate> transitionScenePaletteCandidates;
try
{
	transitionScenePaletteCandidates = !hasExplicitPaletteSelection
		? LoadTransitionScenePaletteCandidates(sampleRoot, outputRoot, jsonOptions)
		: new List<TransitionScenePaletteCandidate>();
}
catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or InvalidDataException or JsonException)
{
	Console.Error.WriteLine($"Transition-backed palette candidate scan skipped: {ex.Message}");
	transitionScenePaletteCandidates = new List<TransitionScenePaletteCandidate>();
}

MenuEventPaletteCandidate? menuEventPaletteCandidate;
try
{
	menuEventPaletteCandidate = !hasExplicitPaletteSelection
		? TryLoadDiskresMenuPaletteCandidate(sampleRoot, outputRoot)
		: null;
}
catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or InvalidDataException)
{
	Console.Error.WriteLine($"DISKRES menu/resource palette candidate skipped: {ex.Message}");
	menuEventPaletteCandidate = null;
}

var externalPaletteCandidates = !hasExplicitPaletteSelection
	? BuildExternalPaletteCandidates(transitionScenePaletteCandidates, menuEventPaletteCandidate)
	: new List<ExternalPaletteCandidate>();
var hasGenericPaletteCandidates = externalPaletteCandidates.Count > 0;

foreach (var inputFile in inputFiles)
{
	var archiveOutputDir = Path.Combine(
		outputRoot,
		BuildArchiveOutputDirectoryName(inputFile, hasExplicitPaletteSelection, transitionScenePaletteCandidates.Count > 0));
	ResetDirectory(archiveOutputDir);

	if (IsSupportedCharDat(inputFile))
	{
		WriteCharDatInspectionOutput(
			inputFile,
			archiveOutputDir,
			jsonOptions,
			options.WriteBinaryOutputs);
		continue;
	}

	if (IsSupportedTransitionFile(inputFile))
	{
		WriteTransitionFileInspectionOutput(
			inputFile,
			archiveOutputDir,
			sampleRoot,
			jsonOptions,
			options.WriteBinaryOutputs);
		continue;
	}

	if (Path.GetExtension(inputFile).Equals(".fam", StringComparison.OrdinalIgnoreCase))
	{
		WriteFamInspectionOutput(
			inputFile,
			archiveOutputDir,
			jsonOptions,
			options.WriteBinaryOutputs,
			options.FamProbeSceneNames,
			options.ProbeAllFamScenes);
		continue;
	}

	var archive = ArchiveInspector.Inspect(inputFile);
	if (transitionScenePaletteCandidates.Count > 0)
	{
		foreach (var candidate in transitionScenePaletteCandidates)
		{
			archive.Manifest.TransitionScenePaletteCandidates.Add(new TransitionScenePaletteCandidateManifest
			{
				SceneName = candidate.SceneName,
				PaletteOutputFile = Path.GetRelativePath(outputRoot, candidate.PalettePath),
				PaletteSourceDescription = candidate.Palette.SourceDescription,
				MatchedInFiles = candidate.SourceFiles.ToList()
			});
		}
	}

	if (archive.Manifest.TypeCode == 0x2712 && menuEventPaletteCandidate is not null)
	{
		archive.Manifest.MenuEventPaletteCandidates.Add(new MenuEventPaletteCandidateManifest
		{
			CandidateName = menuEventPaletteCandidate.CandidateName,
			PaletteOutputFile = Path.GetRelativePath(outputRoot, menuEventPaletteCandidate.PalettePath),
			PaletteSourceDescription = menuEventPaletteCandidate.Palette.SourceDescription,
			Evidence = menuEventPaletteCandidate.Evidence
		});
	}

	if (options.WriteBinaryOutputs)
	{
		File.WriteAllBytes(Path.Combine(archiveOutputDir, "header.bin"), archive.HeaderBytes);
		if (archive.HeaderPaletteBytes is not null)
		{
			File.WriteAllBytes(Path.Combine(archiveOutputDir, "header_palette.rgbx.bin"), archive.HeaderPaletteBytes);
		}
	}

	var previewPalette = hasPreviewPaletteOverride
		? palette
		: archive.HeaderPalette ?? palette;
	var liveWorldOutputPalette = palette ?? archive.HeaderPalette;

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

		if (entry.MobDataItems.Count > 0 || entry.MobMetadataRecords.Count > 0)
		{
			entry.Manifest.PreviewPaletteSource = previewPalette?.SourceDescription;
			entry.Manifest.PaletteAuthority = "external palette authority unresolved; preview uses archive/header palette when present and runtime-backed external palette candidates stay separate";
		}

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

					if (previewPalette is not null)
					{
						var pngFileName = $"{baseName}.png";
						ImageWriter.WritePng(Path.Combine(entryDir, pngFileName), decodedImage, previewPalette);
						subresource.Manifest.DecodedPngFile = pngFileName;
						subresource.Manifest.DecodedPaletteSource = previewPalette.SourceDescription;
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

						if (previewPalette is not null)
						{
							var pngFileName = $"{baseName}.png";
							ImageWriter.WritePng(Path.Combine(dataDir, pngFileName), decodedImage, previewPalette);
							item.Manifest.DecodedPngFile = pngFileName;
							item.Manifest.DecodedPaletteSource = previewPalette.SourceDescription;
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

		var liveWorldSpriteItem = entry.MobDataItems
			.FirstOrDefault(item => item.Manifest.Index == ArchiveInspector.LiveWorldSpriteDataItemIndex);
		if (liveWorldSpriteItem is not null)
		{
			entry.Manifest.LiveWorldSpriteDataItemIndex = liveWorldSpriteItem.Manifest.Index;
			entry.Manifest.LiveWorldBindingModel = "external-scene-record-table (0x1c058 room-level two-key/no-anchor; 0x1cc10 anchored three-key)";
			entry.Manifest.PaletteAuthority = "proven live-world sprite item 0x0B; rendered under externally owned scene/state palette";

			if (decodedMobSprites.TryGetValue(ArchiveInspector.LiveWorldSpriteDataItemIndex, out var liveWorldSpriteImage))
			{
				if (hasExplicitPaletteSelection && liveWorldOutputPalette is not null)
				{
					const string liveWorldSpriteFileName = "world_sprite.png";
					ImageWriter.WritePng(Path.Combine(entryDir, liveWorldSpriteFileName), liveWorldSpriteImage, liveWorldOutputPalette);
					entry.Manifest.LiveWorldSpriteDecodedPngFile = liveWorldSpriteFileName;
					entry.Manifest.LiveWorldSpriteDecodedPaletteSource = liveWorldOutputPalette.SourceDescription;
				}
				else if (transitionScenePaletteCandidates.Count > 0)
				{
					const string candidateDirName = "world_sprite_scene_candidates";
					var candidateDir = Path.Combine(entryDir, candidateDirName);
					Directory.CreateDirectory(candidateDir);

					for (var candidateIndex = 0; candidateIndex < transitionScenePaletteCandidates.Count; candidateIndex++)
					{
						var candidate = transitionScenePaletteCandidates[candidateIndex];
						var candidateFileName = $"scene_{candidateIndex:D3}_{SanitizeFileComponent(candidate.SceneName)}.png";
						ImageWriter.WritePng(Path.Combine(candidateDir, candidateFileName), liveWorldSpriteImage, candidate.Palette);
						entry.Manifest.LiveWorldSpritePaletteCandidates.Add(new LiveWorldSpritePaletteCandidateManifest
						{
							SceneName = candidate.SceneName,
							MatchedInFiles = candidate.SourceFiles.ToList(),
							PaletteOutputFile = Path.GetRelativePath(outputRoot, candidate.PalettePath),
							PaletteSourceDescription = candidate.Palette.SourceDescription,
							DecodedPngFile = Path.Combine(candidateDirName, candidateFileName)
						});
					}
				}
				else if (liveWorldOutputPalette is not null)
				{
					const string liveWorldSpriteFileName = "world_sprite.png";
					ImageWriter.WritePng(Path.Combine(entryDir, liveWorldSpriteFileName), liveWorldSpriteImage, liveWorldOutputPalette);
					entry.Manifest.LiveWorldSpriteDecodedPngFile = liveWorldSpriteFileName;
					entry.Manifest.LiveWorldSpriteDecodedPaletteSource = liveWorldOutputPalette.SourceDescription;
				}
			}
		}

		if (hasGenericPaletteCandidates && decodedMobSprites.Count > 0)
		{
			var paletteCandidatesRootDirName = "palette_candidates";
			var paletteCandidatesRootDir = Path.Combine(entryDir, paletteCandidatesRootDirName);
			Directory.CreateDirectory(paletteCandidatesRootDir);

			foreach (var paletteCandidate in externalPaletteCandidates)
			{
				var candidateRootDirName = Path.Combine(paletteCandidatesRootDirName, paletteCandidate.DirectoryName);
				var candidateRootDir = Path.Combine(entryDir, candidateRootDirName);
				Directory.CreateDirectory(candidateRootDir);

				foreach (var item in entry.MobDataItems)
				{
					if (!decodedMobSprites.TryGetValue(item.Manifest.Index, out var candidateImage))
					{
						continue;
					}

					var candidateFileName = $"item_{item.Manifest.Index:D3}.png";
					ImageWriter.WritePng(
						Path.Combine(candidateRootDir, candidateFileName),
						candidateImage,
						paletteCandidate.Palette);
					item.Manifest.PaletteCandidates.Add(new MobDataItemPaletteCandidateManifest
					{
						CandidateName = paletteCandidate.CandidateName,
						PaletteOutputFile = Path.GetRelativePath(outputRoot, paletteCandidate.PalettePath),
						PaletteSourceDescription = paletteCandidate.Palette.SourceDescription,
						Evidence = paletteCandidate.Evidence,
						DecodedPngFile = Path.Combine(candidateRootDirName, candidateFileName)
					});
				}
			}
		}

		if ((previewPalette is not null || hasGenericPaletteCandidates)
			&& decodedMobSprites.Count > 0
			&& entry.MobMetadataRecords.Count > 0)
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
					if (previewPalette is not null)
					{
						try
						{
							ImageWriter.WritePlacedHorizontalStripPng(
								Path.Combine(metadataDir, explicitPngFileName),
								explicitPlacedFrames,
								explicitPlacement,
								previewPalette);
							record.Manifest.DecodedPngFile = explicitPngFileName;
							record.Manifest.DecodedPaletteSource = previewPalette.SourceDescription;
						}
						catch (MobCanvasTooLargeException ex)
						{
							record.Manifest.DecodedExportError = ex.Message;
							Console.Error.WriteLine($"Skipping explicit strip for {Path.GetFileName(inputFile)} entry {entry.Manifest.Index:D3} record {record.Manifest.Index:D3} ({record.Manifest.DecodedName}): {ex.Message}");
						}
					}

					if (previewPalette is not null
						&& options.WriteSequenceRelativeOutputs
						&& placedFrames is not null
						&& sequencePlacement.HasValue)
					{
						try
						{
							ImageWriter.WritePlacedHorizontalStripPng(
								Path.Combine(metadataDir, sequencePngFileName),
								placedFrames,
								sequencePlacement.Value,
								previewPalette);
							record.Manifest.AlternateDecodedPngFile = sequencePngFileName;
						}
						catch (MobCanvasTooLargeException ex)
						{
							record.Manifest.AlternateDecodedExportError = ex.Message;
							Console.Error.WriteLine($"Skipping sequence strip for {Path.GetFileName(inputFile)} entry {entry.Manifest.Index:D3} record {record.Manifest.Index:D3} ({record.Manifest.DecodedName}): {ex.Message}");
						}
					}
				}

				if (hasGenericPaletteCandidates)
				{
					var paletteCandidatesRootDirName = Path.Combine("metadata", "palette_candidates");

					foreach (var paletteCandidate in externalPaletteCandidates)
					{
						var candidateDirName = Path.Combine(paletteCandidatesRootDirName, paletteCandidate.DirectoryName);
						var candidateDir = Path.Combine(entryDir, candidateDirName);
						Directory.CreateDirectory(candidateDir);

						try
						{
							ImageWriter.WritePlacedHorizontalStripPng(
								Path.Combine(candidateDir, explicitPngFileName),
								explicitPlacedFrames,
								explicitPlacement,
								paletteCandidate.Palette);
							record.Manifest.PaletteCandidates.Add(new MobDataItemPaletteCandidateManifest
							{
								CandidateName = paletteCandidate.CandidateName,
								PaletteOutputFile = Path.GetRelativePath(outputRoot, paletteCandidate.PalettePath),
								PaletteSourceDescription = paletteCandidate.Palette.SourceDescription,
								Evidence = paletteCandidate.Evidence,
								DecodedPngFile = Path.Combine(candidateDirName, explicitPngFileName)
							});
						}
						catch (MobCanvasTooLargeException ex)
						{
							Console.Error.WriteLine($"Skipping palette candidate strip for {Path.GetFileName(inputFile)} entry {entry.Manifest.Index:D3} record {record.Manifest.Index:D3} ({record.Manifest.DecodedName}) under {paletteCandidate.CandidateName}: {ex.Message}");
						}
					}
				}

				if (previewPalette is not null)
				{
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
								previewPalette);
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
				}

				if (previewPalette is not null
					&& options.WriteSequenceRelativeOutputs
					&& placedFrames is not null
					&& sequencePlacement.HasValue)
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
								previewPalette);
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
			resolved.AddRange(Directory.EnumerateFiles(fullPath).Where(IsSupportedExplicitInput));
			continue;
		}

		if (File.Exists(fullPath) && IsSupportedExplicitInput(fullPath))
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

static bool IsSupportedExplicitInput(string path)
{
	return IsSupportedArchive(path) || IsSupportedCharDat(path) || IsSupportedTransitionFile(path);
}

static bool IsSupportedCharDat(string path)
{
	return Path.GetFileName(path).Equals("CHAR.DAT", StringComparison.OrdinalIgnoreCase);
}

static bool IsSupportedTransitionFile(string path)
{
	var fileName = Path.GetFileName(path);
	return fileName.Equals("SAGA.BIN", StringComparison.OrdinalIgnoreCase)
		|| fileName.Equals("SAGA.SCP", StringComparison.OrdinalIgnoreCase);
}

static AppOptions ParseArguments(string[] args)
{
	var inputPaths = new List<string>();
	var famProbeSceneNames = new List<string>();
	var probeAllFamScenes = false;
	string? palettePath = null;
	string? forgaSceneName = null;
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

		if (arg.Equals("--forga-scene", StringComparison.OrdinalIgnoreCase)
			|| arg.Equals("--palette-scene", StringComparison.OrdinalIgnoreCase)
			|| arg.Equals("--scene-palette", StringComparison.OrdinalIgnoreCase))
		{
			if (index + 1 >= args.Length)
			{
				throw new ArgumentException("Expected a FORGA scene or resource name after --forga-scene.");
			}

			forgaSceneName = args[++index];
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

		if (arg.Equals("--probe-all-scenes", StringComparison.OrdinalIgnoreCase)
			|| arg.Equals("--fam-probe-all-scenes", StringComparison.OrdinalIgnoreCase))
		{
			probeAllFamScenes = true;
			continue;
		}

		inputPaths.Add(arg);
	}

	return new AppOptions
	{
		InputPaths = inputPaths,
		PalettePath = palettePath,
		ForgaSceneName = forgaSceneName,
		WriteBinaryOutputs = writeBinaryOutputs,
		WriteStripOutputs = writeStripOutputs,
		WriteSequenceRelativeOutputs = writeSequenceRelativeOutputs,
		FamProbeSceneNames = famProbeSceneNames,
		ProbeAllFamScenes = probeAllFamScenes
	};
}

static string? ResolvePalettePath(
	AppOptions options,
	string sampleRoot,
	string outputRoot,
	JsonSerializerOptions jsonOptions)
{
	if (options.PalettePath is { } explicitPalettePath)
	{
		if (!File.Exists(explicitPalettePath))
		{
			throw new FileNotFoundException($"Palette file not found: {explicitPalettePath}");
		}

		return explicitPalettePath;
	}

	if (string.IsNullOrWhiteSpace(options.ForgaSceneName))
	{
		return null;
	}

	return ResolveForgaScenePalettePath(options.ForgaSceneName, sampleRoot, outputRoot, jsonOptions);
}

static string ResolveForgaScenePalettePath(
	string sceneOrResourceName,
	string sampleRoot,
	string outputRoot,
	JsonSerializerOptions jsonOptions)
{
	var forgaOutputDir = Path.Combine(outputRoot, "FORGA");
	var manifest = LoadOrGenerateForgaManifest(sampleRoot, outputRoot, jsonOptions, ensurePaletteOutputs: false);

	if (TryResolveForgaScenePalettePath(
		manifest,
		forgaOutputDir,
		sceneOrResourceName,
		out var palettePath,
		out var requiresPaletteRefresh,
		out var error))
	{
		return palettePath;
	}

	if (requiresPaletteRefresh)
	{
		manifest = LoadOrGenerateForgaManifest(sampleRoot, outputRoot, jsonOptions, ensurePaletteOutputs: true);
		if (TryResolveForgaScenePalettePath(
			manifest,
			forgaOutputDir,
			sceneOrResourceName,
			out palettePath,
			out _,
			out error))
		{
			return palettePath;
		}
	}

	throw new InvalidDataException(error);
}

static FamManifest LoadOrGenerateForgaManifest(
	string sampleRoot,
	string outputRoot,
	JsonSerializerOptions jsonOptions,
	bool ensurePaletteOutputs)
{
	var forgaOutputDir = Path.Combine(outputRoot, "FORGA");
	var manifestPath = Path.Combine(forgaOutputDir, "manifest.json");

	if (File.Exists(manifestPath))
	{
		var manifest = LoadForgaManifest(manifestPath);
		if (!ensurePaletteOutputs || HasForgaRuntimePaletteOutputs(manifest, forgaOutputDir))
		{
			return manifest;
		}
	}

	var forgaInputPath = Path.Combine(sampleRoot, "MAP", "FORGA.FAM");
	if (!File.Exists(forgaInputPath))
	{
		throw new FileNotFoundException(
			$"FORGA palette resolution needs either an existing extraction at {manifestPath} or the source archive at {forgaInputPath}.");
	}

	ResetDirectory(forgaOutputDir);
	WriteFamInspectionOutput(
		forgaInputPath,
		forgaOutputDir,
		jsonOptions,
		writeBinaryOutputs: true,
		Array.Empty<string>(),
		probeAllFamScenes: false);

	return LoadForgaManifest(manifestPath);
}

static FamManifest LoadForgaManifest(string manifestPath)
{
	var manifest = JsonSerializer.Deserialize<FamManifest>(File.ReadAllText(manifestPath));
	if (manifest is null)
	{
		throw new InvalidDataException($"Failed to deserialize FORGA manifest at {manifestPath}.");
	}

	return manifest;
}

static bool HasForgaRuntimePaletteOutputs(FamManifest manifest, string forgaOutputDir)
{
	return manifest.Entries.Any(entry =>
		entry.TableName == "second"
		&& entry.RuntimePaletteOutputFile is { Length: > 0 } paletteRelativePath
		&& File.Exists(Path.Combine(forgaOutputDir, paletteRelativePath)));
}

static bool TryResolveForgaScenePalettePath(
	FamManifest manifest,
	string forgaOutputDir,
	string sceneOrResourceName,
	out string palettePath,
	out bool requiresPaletteRefresh,
	out string error)
{
	palettePath = null!;
	requiresPaletteRefresh = false;
	error = string.Empty;

	var firstTableEntries = manifest.Entries.Where(entry => entry.TableName == "first").ToArray();
	var secondTableEntries = manifest.Entries.Where(entry => entry.TableName == "second").ToArray();
	FamEntryManifest? secondEntry = null;

	if (TryFindUniqueFamEntryByName(firstTableEntries, sceneOrResourceName, out var firstEntry, out var firstEntryError))
	{
		if (firstEntry is null)
		{
			error = $"FORGA scene/resource '{sceneOrResourceName}' resolved to a null first-table entry.";
			return false;
		}

		var resourceName = firstEntry.ProbeResourceName ?? firstEntry.EmbeddedName ?? firstEntry.DecodedName;
		if (!TryFindUniqueFamEntryByName(secondTableEntries, resourceName, out secondEntry, out var secondEntryError))
		{
			error = secondEntryError
				?? $"FORGA scene '{sceneOrResourceName}' resolved to resource '{resourceName}', but no matching second-table entry was found.";
			return false;
		}
	}
	else if (!TryFindUniqueFamEntryByName(secondTableEntries, sceneOrResourceName, out secondEntry, out var secondEntryError))
	{
		error = firstEntryError
			?? secondEntryError
			?? $"FORGA scene/resource '{sceneOrResourceName}' was not found in {Path.Combine(forgaOutputDir, "manifest.json")}.";
		return false;
	}

	if (secondEntry is null)
	{
		error = $"FORGA scene/resource '{sceneOrResourceName}' resolved to a null second-table entry.";
		return false;
	}

	if (string.IsNullOrWhiteSpace(secondEntry.RuntimePaletteOutputFile))
	{
		requiresPaletteRefresh = true;
		error = $"FORGA resource '{secondEntry.DecodedName}' is present but does not yet expose a runtime palette output file.";
		return false;
	}

	var candidatePalettePath = Path.Combine(forgaOutputDir, secondEntry.RuntimePaletteOutputFile);
	if (!File.Exists(candidatePalettePath))
	{
		requiresPaletteRefresh = true;
		error = $"FORGA runtime palette file not found: {candidatePalettePath}";
		return false;
	}

	palettePath = candidatePalettePath;
	return true;
}

static bool TryFindUniqueFamEntryByName(
	IEnumerable<FamEntryManifest> entries,
	string name,
	out FamEntryManifest? match,
	out string? error)
{
	var exactMatches = entries
		.Where(entry => entry.DecodedName.Equals(name, StringComparison.OrdinalIgnoreCase))
		.ToList();
	if (exactMatches.Count == 1)
	{
		match = exactMatches[0];
		error = null;
		return true;
	}

	if (exactMatches.Count > 1)
	{
		match = null;
		error = $"FORGA name '{name}' is ambiguous. Use the exact scene name or an explicit palette path.";
		return false;
	}

	var trimmedName = name.TrimEnd();
	var trimmedMatches = entries
		.Where(entry => entry.DecodedName.TrimEnd().Equals(trimmedName, StringComparison.OrdinalIgnoreCase))
		.ToList();
	if (trimmedMatches.Count == 1)
	{
		match = trimmedMatches[0];
		error = null;
		return true;
	}

	if (trimmedMatches.Count > 1)
	{
		match = null;
		error = $"FORGA name '{name}' is ambiguous after trimming trailing spaces. Use the first-table scene name or an explicit palette path.";
		return false;
	}

	match = null;
	error = null;
	return false;
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

static string BuildArchiveOutputDirectoryName(
	string inputFile,
	bool hasExplicitPaletteSelection,
	bool hasTransitionScenePaletteCandidates)
{
	if (IsSupportedTransitionFile(inputFile))
	{
		return Path.GetFileName(inputFile);
	}

	var baseName = Path.GetFileNameWithoutExtension(inputFile);
	if (hasExplicitPaletteSelection
		|| !hasTransitionScenePaletteCandidates
		|| !Path.GetExtension(inputFile).Equals(".mob", StringComparison.OrdinalIgnoreCase))
	{
		return baseName;
	}

	return $"{baseName}_transition_candidates";
}

static List<TransitionScenePaletteCandidate> LoadTransitionScenePaletteCandidates(
	string sampleRoot,
	string outputRoot,
	JsonSerializerOptions jsonOptions)
{
	var transitionFilePaths = new[]
	{
		Path.Combine(sampleRoot, "SAGA.BIN"),
		Path.Combine(sampleRoot, "SAGA.SCP")
	}.Where(File.Exists).ToArray();

	if (transitionFilePaths.Length == 0)
	{
		return new List<TransitionScenePaletteCandidate>();
	}

	var manifest = LoadOrGenerateForgaManifest(sampleRoot, outputRoot, jsonOptions, ensurePaletteOutputs: true);
	var knownSceneNames = manifest.Entries
		.Where(entry => entry.TableName == "first")
		.Select(entry => entry.DecodedName)
		.ToHashSet(StringComparer.OrdinalIgnoreCase);
	var matchedSceneSources = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

	foreach (var transitionFilePath in transitionFilePaths)
	{
		foreach (var token in ExtractTransitionSceneNameTokens(transitionFilePath))
		{
			if (!knownSceneNames.Contains(token))
			{
				continue;
			}

			if (!matchedSceneSources.TryGetValue(token, out var sources))
			{
				sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				matchedSceneSources.Add(token, sources);
			}

			sources.Add(Path.GetFileName(transitionFilePath));
		}
	}

	var candidates = new List<TransitionScenePaletteCandidate>(matchedSceneSources.Count);
	foreach (var matchedScene in matchedSceneSources.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
	{
		var resolvedPalettePath = ResolveForgaScenePalettePath(matchedScene.Key, sampleRoot, outputRoot, jsonOptions);
		candidates.Add(new TransitionScenePaletteCandidate(
			matchedScene.Key,
			resolvedPalettePath,
			IndexedPalette.LoadFile(resolvedPalettePath),
			matchedScene.Value.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray()));
	}

	return candidates;
}

static IEnumerable<string> ExtractTransitionSceneNameTokens(string path)
{
	var content = Encoding.GetEncoding(949).GetString(File.ReadAllBytes(path));
	var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

	foreach (Match match in Regex.Matches(content, @"(?<![A-Z0-9-])[A-Z][A-Z0-9-]{2,10}(?![A-Z0-9-])"))
	{
		var token = match.Value.Trim();
		if (seen.Add(token))
		{
			yield return token;
		}
	}
}

static MenuEventPaletteCandidate? TryLoadDiskresMenuPaletteCandidate(string sampleRoot, string outputRoot)
{
	var diskresPath = Path.Combine(sampleRoot, "DISKRES.SPB");
	if (!File.Exists(diskresPath))
	{
		return null;
	}

	var archive = ArchiveInspector.Inspect(diskresPath);
	if (archive.HeaderPaletteBytes is null)
	{
		return null;
	}

	var diskresOutputDir = Path.Combine(outputRoot, "DISKRES");
	Directory.CreateDirectory(diskresOutputDir);
	var palettePath = Path.Combine(diskresOutputDir, "header_palette.rgbx.bin");
	File.WriteAllBytes(palettePath, archive.HeaderPaletteBytes);

	return new MenuEventPaletteCandidate(
		"DISKRES outer menu resource header",
		palettePath,
		IndexedPalette.FromRawRgbx(
			archive.HeaderPaletteBytes,
			255,
			"DISKRES.SPB header RGBX palette (traced outer menu/resource path via 0x17f96 -> 0x33512(1,0) -> 0x14ac1)"),
		"Selector 1/0 resolves DISKRES.SPB entry 1 (GAME_OVER) subresource 0 before the outer full-palette upload at 0x18010.");
}

static List<ExternalPaletteCandidate> BuildExternalPaletteCandidates(
	IReadOnlyList<TransitionScenePaletteCandidate> transitionScenePaletteCandidates,
	MenuEventPaletteCandidate? menuEventPaletteCandidate)
{
	var candidates = new List<ExternalPaletteCandidate>(
		transitionScenePaletteCandidates.Count + (menuEventPaletteCandidate is null ? 0 : 1));

	for (var candidateIndex = 0; candidateIndex < transitionScenePaletteCandidates.Count; candidateIndex++)
	{
		var candidate = transitionScenePaletteCandidates[candidateIndex];
		var matchedFiles = candidate.SourceFiles.Count > 0
			? string.Join(", ", candidate.SourceFiles)
			: "transition analysis";
		candidates.Add(new ExternalPaletteCandidate(
			candidate.SceneName,
			$"scene_{candidateIndex:D3}_{SanitizeFileComponent(candidate.SceneName)}",
			candidate.PalettePath,
			candidate.Palette,
			$"Transition-backed scene palette candidate matched in {matchedFiles}."));
	}

	if (menuEventPaletteCandidate is not null)
	{
		candidates.Add(new ExternalPaletteCandidate(
			menuEventPaletteCandidate.CandidateName,
			$"menu_{SanitizeFileComponent(menuEventPaletteCandidate.CandidateName)}",
			menuEventPaletteCandidate.PalettePath,
			menuEventPaletteCandidate.Palette,
			menuEventPaletteCandidate.Evidence));
	}

	return candidates;
}

static void ResetDirectory(string path)
{
	if (Directory.Exists(path))
	{
		Directory.Delete(path, recursive: true);
	}

	Directory.CreateDirectory(path);
}

static void WriteCharDatInspectionOutput(
	string inputFile,
	string outputDir,
	JsonSerializerOptions jsonOptions,
	bool writeBinaryOutputs)
{
	var inspection = CharDatInspector.Inspect(inputFile, retainDecompressedPayload: writeBinaryOutputs);

	if (writeBinaryOutputs)
	{
		File.WriteAllBytes(Path.Combine(outputDir, "header.bin"), inspection.HeaderBytes);
		if (inspection.DecompressedPayload is not null)
		{
			File.WriteAllBytes(Path.Combine(outputDir, "decoded.bin"), inspection.DecompressedPayload);
		}

		var recordsDir = Path.Combine(outputDir, "records");
		Directory.CreateDirectory(recordsDir);

		foreach (var entry in inspection.Entries)
		{
			var fileName = $"record_{entry.Manifest.Index:D3}_{SanitizeFileComponent(entry.Manifest.DecodedName)}.bin";
			File.WriteAllBytes(Path.Combine(recordsDir, fileName), entry.Payload);
			entry.Manifest.OutputFile = Path.Combine("records", fileName);
		}
	}

	File.WriteAllText(
		Path.Combine(outputDir, "manifest.json"),
		JsonSerializer.Serialize(inspection.Manifest, jsonOptions));

	Console.WriteLine(
		$"{Path.GetFileName(inputFile)}: type=CHAR.DAT, records={inspection.Manifest.RecordCount}, output={outputDir}");
}

static void WriteTransitionFileInspectionOutput(
	string inputFile,
	string outputDir,
	string sampleRoot,
	JsonSerializerOptions jsonOptions,
	bool writeBinaryOutputs)
{
	ArchiveInspection? objectMobInspection = null;
	if (Path.GetFileName(inputFile).Equals("SAGA.BIN", StringComparison.OrdinalIgnoreCase))
	{
		var objectMobPath = Path.Combine(sampleRoot, "OBJECT.MOB");
		if (File.Exists(objectMobPath))
		{
			try
			{
				objectMobInspection = ArchiveInspector.Inspect(objectMobPath);
			}
			catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
			{
				Console.Error.WriteLine($"OBJECT.MOB alias enrichment skipped: {ex.Message}");
			}
		}
	}

	var inspection = TransitionFileInspector.Inspect(
		inputFile,
		retainDecompressedPayload: writeBinaryOutputs,
		objectMobInspection);

	foreach (var section in inspection.Sections)
	{
		var sectionDirectoryName = SanitizeFileComponent(section.Manifest.SectionName);
		var sectionDirectoryPath = Path.Combine(outputDir, sectionDirectoryName);
		Directory.CreateDirectory(sectionDirectoryPath);

		var sectionFileName = $"{sectionDirectoryName}.bin";
		File.WriteAllBytes(Path.Combine(outputDir, sectionFileName), section.RawBytes);
		section.Manifest.OutputFile = sectionFileName;

		foreach (var entry in section.Entries)
		{
			var entryFileName = $"{sectionDirectoryName}_{entry.Manifest.Index:D3}.bin";
			File.WriteAllBytes(Path.Combine(sectionDirectoryPath, entryFileName), entry.Payload);
			entry.Manifest.OutputFile = Path.Combine(sectionDirectoryName, entryFileName);
		}
	}

	if (writeBinaryOutputs)
	{
		File.WriteAllBytes(Path.Combine(outputDir, "header.bin"), inspection.HeaderBytes);
		if (inspection.DecompressedPayload is not null)
		{
			File.WriteAllBytes(Path.Combine(outputDir, "decoded.bin"), inspection.DecompressedPayload);
		}

		if (inspection.PublishedSelectorRegion is { Length: > 0 })
		{
			const string publishedSelectorRegionFileName = "published_selector_region.bin";
			File.WriteAllBytes(
				Path.Combine(outputDir, publishedSelectorRegionFileName),
				inspection.PublishedSelectorRegion);
			inspection.Manifest.PublishedSelectorRegionOutputFile = publishedSelectorRegionFileName;
		}
	}

	File.WriteAllText(
		Path.Combine(outputDir, "manifest.json"),
		JsonSerializer.Serialize(inspection.Manifest, jsonOptions));

	Console.WriteLine(
		$"{Path.GetFileName(inputFile)}: type={inspection.Manifest.Variant}, sections={inspection.Sections.Count}, output={outputDir}");
}

static void WriteFamInspectionOutput(
	string inputFile,
	string outputDir,
	JsonSerializerOptions jsonOptions,
	bool writeBinaryOutputs,
	IReadOnlyList<string> famProbeSceneNames,
	bool probeAllFamScenes)
{
	var shouldProbeScenes = probeAllFamScenes || famProbeSceneNames.Count > 0;
	var retainDecompressedPayloads = writeBinaryOutputs || shouldProbeScenes;
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

		if (retainDecompressedPayloads && entry.DecompressedPayload is not null)
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

	if (shouldProbeScenes)
	{
		WriteFamSceneProbeOutputs(outputDir, inspection, famProbeSceneNames, probeAllFamScenes);
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
	IReadOnlyList<string> requestedSceneNames,
	bool probeAllScenes)
{
	var requested = new HashSet<string>(requestedSceneNames, StringComparer.OrdinalIgnoreCase);
	var probesRoot = Path.Combine(outputDir, "scene_probes");
	Directory.CreateDirectory(probesRoot);

	var secondTableByName = inspection.Entries
		.Where(entry => entry.Manifest.TableName == "second")
		.GroupBy(entry => entry.Manifest.DecodedName, StringComparer.OrdinalIgnoreCase)
		.ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

	foreach (var firstEntry in inspection.Entries.Where(entry =>
		entry.Manifest.TableName == "first"
		&& (probeAllScenes || requested.Contains(entry.Manifest.DecodedName))))
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
	public const int LiveWorldSpriteDataItemIndex = 0x0B;

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
							WordPreview16 = BuildU16Preview(payload),
							IsLikelyLiveWorldSprite = itemIndex == LiveWorldSpriteDataItemIndex
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

	public static bool TryDecompressLz(
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
		var decoded = AsciiEncoding.GetString(rawNameBytes, 0, count);
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
	public string? ForgaSceneName { get; init; }
	public bool WriteBinaryOutputs { get; init; }
	public bool WriteStripOutputs { get; init; }
	public bool WriteSequenceRelativeOutputs { get; init; }
	public required List<string> FamProbeSceneNames { get; init; }
	public bool ProbeAllFamScenes { get; init; }
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

		var palette = IndexedPalette.FromRawRgbx(
			secondEntry.DecompressedPayload.AsSpan(paletteOffset, paletteLength).ToArray(),
			paletteLength / 4,
			$"FORGA.FAM probe palette from {secondEntry.Manifest.DecodedName}");
		var lookupTableBytes = secondEntry.DecompressedPayload.AsSpan(lookupTableOffset, lookupTableLength).ToArray();
		var tileBankLength = secondEntry.DecompressedPayload.Length;
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
		probeNotes.Add($"Runtime draw path uses the full second decoded buffer as the 8x8 tile source ({tileBankLength / BytesPerTile} tiles / {tileBankLength} bytes) while also exposing palette and lookup-table views into its tail.");
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
	private const int RawRgbxPaletteEntryCountWithTransparentDefault = 255;
	private const int RawRgbxPaletteEntryCountWithOpaqueIndex255 = 256;
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

		if (TryLoadRawRgbx(bytes, path, out var rawRgbxPalette))
		{
			return rawRgbxPalette;
		}

		if (TryLoadEmbeddedHeaderRgbx(bytes, path, out var headerPalette))
		{
			return headerPalette;
		}

		throw new InvalidDataException(
			$"{path} is {bytes.Length} bytes, expected either a 768-byte raw VGA palette, a 255/256-entry raw RGBX palette, or a 0x2711/0x2712 file with an embedded header RGBX palette.");
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

	private static bool TryLoadRawRgbx(byte[] bytes, string path, out IndexedPalette palette)
	{
		if (bytes.Length == RawRgbxPaletteEntryCountWithTransparentDefault * 4)
		{
			palette = FromRgbxEntries(
				bytes,
				RawRgbxPaletteEntryCountWithTransparentDefault,
				preserveIndex255: false,
				$"raw RGBX palette from {Path.GetFileName(path)}");
			return true;
		}

		if (bytes.Length == RawRgbxPaletteEntryCountWithOpaqueIndex255 * 4)
		{
			palette = FromRgbxEntries(
				bytes,
				RawRgbxPaletteEntryCountWithOpaqueIndex255,
				preserveIndex255: true,
				$"raw RGBX palette from {Path.GetFileName(path)}");
			return true;
		}

		palette = null!;
		return false;
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

	public static IndexedPalette FromRawRgbx(byte[] rgbxBytes, int entryCount, string? sourceDescription = null)
	{
		return FromRgbxEntries(
			rgbxBytes,
			entryCount,
			preserveIndex255: entryCount >= 256,
			sourceDescription ?? $"raw RGBX palette ({entryCount} entries)");
	}

	public static IndexedPalette FromHeaderRgbx(byte[] rgbxBytes, int offset, int entryCount, string? sourceDescription = null)
	{
		return FromRgbxEntries(
			rgbxBytes,
			entryCount,
			preserveIndex255: false,
			sourceDescription ?? $"embedded header RGBX palette @ 0x{offset:X} ({entryCount} entries)");
	}

	private static IndexedPalette FromRgbxEntries(byte[] rgbxBytes, int entryCount, bool preserveIndex255, string sourceDescription)
	{
		if (entryCount < 0 || entryCount > 256)
		{
			throw new ArgumentOutOfRangeException(nameof(entryCount), entryCount, "RGBX palette entry count must be between 0 and 256.");
		}

		if (rgbxBytes.Length < entryCount * 4)
		{
			throw new InvalidDataException($"RGBX palette is {rgbxBytes.Length} bytes, expected at least {entryCount * 4} bytes for {entryCount} entries.");
		}

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

		if (!preserveIndex255)
		{
			colors[255] = default;
		}

		return new IndexedPalette(colors, sourceDescription);
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

				output[cellX + x, cellY + y] = palette.Colors[paletteIndex];
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

static class CharDatInspector
{
	private const int CountHeaderOffset = 0;
	private const int LzStreamOffset = 4;
	private const int RecordNameLength = 32;
	private const int RecordSize = 0x3BC;
	private const int NodeTemplateOffset = 0x20;
	private const int NodeTemplateLength = 0xB8;
	private const int SlotRecordSize = 0x20;
	private const int PrimarySlotGroupOffset = 0xDC;
	private const int PrimarySlotGroupCount = 8;
	private const int SecondarySlotGroupCount = 5;
	private static readonly int[] SecondarySlotGroupOffsets = { 0x1DC, 0x27C, 0x31C };
	private static readonly Encoding KoreanEncoding = Encoding.GetEncoding(949);

	public static CharDatInspection Inspect(string path, bool retainDecompressedPayload)
	{
		var data = File.ReadAllBytes(path);
		if (data.Length < 8)
		{
			throw new InvalidDataException($"{path} is smaller than the CHAR.DAT count + LZ-size header.");
		}

		var recordCount = ReadUInt32(data, CountHeaderOffset);
		var expectedDecodedSize64 = (ulong)recordCount * RecordSize;
		if (expectedDecodedSize64 > int.MaxValue)
		{
			throw new InvalidDataException($"{path} expands to 0x{expectedDecodedSize64:X} bytes, which is too large to inspect in memory.");
		}

		var expectedDecodedSize = checked((int)expectedDecodedSize64);
		var lzDecodedSize = ReadUInt32(data, LzStreamOffset);

		if (!FamInspector.TryDecompressLz(data, LzStreamOffset, out var decoded, out var bytesConsumed, out var error))
		{
			throw new InvalidDataException($"Failed to decompress CHAR.DAT: {error}");
		}

		if (decoded.Length != expectedDecodedSize)
		{
			throw new InvalidDataException(
				$"CHAR.DAT decompressed to {decoded.Length} bytes, but the runtime-derived record count requires {expectedDecodedSize} bytes ({recordCount} * 0x{RecordSize:X}).");
		}

		if (lzDecodedSize != expectedDecodedSize)
		{
			throw new InvalidDataException(
				$"CHAR.DAT LZ header advertises 0x{lzDecodedSize:X} bytes, but the runtime-derived record table requires 0x{expectedDecodedSize:X} bytes.");
		}

		var manifests = new List<CharDatRecordManifest>(checked((int)recordCount));
		var entries = new List<CharDatRecordPayload>(checked((int)recordCount));

		for (var index = 0; index < recordCount; index++)
		{
			var recordOffset = checked((int)index * RecordSize);
			var payload = decoded.AsSpan(recordOffset, RecordSize).ToArray();
			var rawNameBytes = payload.AsSpan(0, RecordNameLength).ToArray();

			var manifest = new CharDatRecordManifest
			{
				Index = checked((int)index),
				RecordOffset = recordOffset,
				DecodedName = DecodeName(rawNameBytes),
				RawNameHex = Convert.ToHexString(rawNameBytes)
			};

			manifests.Add(manifest);
			entries.Add(new CharDatRecordPayload(manifest, payload));
		}

		var manifestModel = new CharDatManifest
		{
			FileName = Path.GetFileName(path),
			FullPath = path,
			RecordCount = recordCount,
			RecordSize = RecordSize,
			CountHeaderOffset = CountHeaderOffset,
			LzStreamOffset = LzStreamOffset,
			LzDecodedSize = lzDecodedSize,
			ExpectedDecodedSize = checked((uint)expectedDecodedSize),
			LzBytesConsumed = bytesConsumed,
			NameFieldLength = RecordNameLength,
			NodeTemplateOffset = NodeTemplateOffset,
			NodeTemplateLength = NodeTemplateLength,
			SlotRecordSize = SlotRecordSize,
			PrimarySlotGroupOffset = PrimarySlotGroupOffset,
			PrimarySlotGroupCount = PrimarySlotGroupCount,
			SecondarySlotGroupCount = SecondarySlotGroupCount,
			Entries = manifests
		};
		manifestModel.SecondarySlotGroupOffsets.AddRange(SecondarySlotGroupOffsets);

		return new CharDatInspection(
			manifestModel,
			entries,
			data.AsSpan(0, 8).ToArray(),
			retainDecompressedPayload ? decoded : null);
	}

	private static string DecodeName(byte[] rawNameBytes)
	{
		var zeroIndex = Array.IndexOf(rawNameBytes, (byte)0);
		var count = zeroIndex >= 0 ? zeroIndex : rawNameBytes.Length;
		if (count == 0)
		{
			return string.Empty;
		}

		return KoreanEncoding.GetString(rawNameBytes, 0, count).Trim();
	}

	private static uint ReadUInt32(byte[] data, int offset)
	{
		return BitConverter.ToUInt32(data, offset);
	}
}

static class TransitionFileInspector
{
	private const int CountHeaderOffset = 0;
	private const int LzStreamOffset = 4;
	private const int HeaderSize = 8;
	private const int ScpRootCountOffset = 0;
	private const int ScpRootTableOffset = 2;
	private const int ScpRootRecordSize = 0x24;
	private const int ScpBlobOffsetFieldOffset = 0x20;
	private const int ScpBlobOffsetBias = 4;
	private const int BinFirstSectionCountOffset = 0;
	private const int BinFirstSectionOffset = 2;
	private const int BinFirstSectionEntrySize = 0x80;
	private const int BinSecondSectionEntrySize = 0x44;
	private const int BinPublishedSelectorEntrySize = 0x24;
	private const int FirstKeyBlobSectionBaseOffset = 0x20;
	private const int SecondKeyBlobSectionBaseOffset = 0x10E;
	private const int SceneRecordNameLength = 0x20;
	private const int TopLevelDescriptorNameLength = 0x20;
	private const int TopLevelDescriptorField20Offset = 0x20;
	private const int TopLevelDescriptorField24Offset = 0x24;
	private const int SceneRecordSelectorOffset = 0x24;
	private const int SceneRecordFlagsOffset = 0x28;
	private const int SceneRecordPositionXOffset = 0x2E;
	private const int SceneRecordPositionYOffset = 0x30;
	private const int SceneRecordField32Offset = 0x32;
	private const int SceneRecordField34Offset = 0x34;
	private const int SceneRecordField36Offset = 0x36;
	private const int SceneRecordField38Offset = 0x38;
	private const int SceneRecordField3AOffset = 0x3A;
	private const int SceneRecordField3COffset = 0x3C;
	private const int SecondKeyPrelude44SectionIndex = 0;
	private const int SecondKeyInteraction4CSectionIndex = 1;
	private const int SecondKeySceneRecordSectionIndex = 2;
	private const int SecondKeyAnchorSectionIndex = 3;
	private static readonly int[] FirstKeyBlobSectionEntrySizes = { 0x44, 0x24, 0x30, 0x08, 0x04 };
	private static readonly int[] SecondKeyBlobSectionEntrySizes = { 0x44, 0x4C, 0x44, 0x38 };
	private static readonly Encoding KoreanEncoding = Encoding.GetEncoding(949);

	public static TransitionFileInspection Inspect(
		string path,
		bool retainDecompressedPayload,
		ArchiveInspection? objectMobInspection = null)
	{
		var data = File.ReadAllBytes(path);
		if (data.Length < HeaderSize)
		{
			throw new InvalidDataException($"{path} is smaller than the transition-file decode header.");
		}

		var allocationDecodedSize = ReadUInt32(data, CountHeaderOffset);
		var lzDecodedSize = ReadUInt32(data, LzStreamOffset);
		if (!FamInspector.TryDecompressLz(data, LzStreamOffset, out var decoded, out var bytesConsumed, out var error))
		{
			throw new InvalidDataException($"Failed to decompress {Path.GetFileName(path)}: {error}");
		}

		var fileName = Path.GetFileName(path);
		if (fileName.Equals("SAGA.SCP", StringComparison.OrdinalIgnoreCase))
		{
			return InspectSagaScp(path, data, decoded, allocationDecodedSize, lzDecodedSize, bytesConsumed, retainDecompressedPayload);
		}

		if (fileName.Equals("SAGA.BIN", StringComparison.OrdinalIgnoreCase))
		{
			return InspectSagaBin(
				path,
				data,
				decoded,
				allocationDecodedSize,
				lzDecodedSize,
				bytesConsumed,
				retainDecompressedPayload,
				objectMobInspection);
		}

		throw new InvalidDataException($"Unsupported transition file: {path}");
	}

	private static TransitionFileInspection InspectSagaScp(
		string path,
		byte[] encoded,
		byte[] decoded,
		uint allocationDecodedSize,
		uint lzDecodedSize,
		int bytesConsumed,
		bool retainDecompressedPayload)
	{
		var recordCount = ReadUInt16(decoded, ScpRootCountOffset);
		var tableLength = checked(recordCount * ScpRootRecordSize);
		var minimumBlobDataOffset = checked(ScpRootTableOffset + tableLength);
		var rootSection = BuildSection(
			decoded,
			"root_0x24_records",
			ScpRootTableOffset,
			recordCount,
			ScpRootRecordSize,
			blobOffsetFieldOffset: ScpBlobOffsetFieldOffset,
			blobOffsetBias: ScpBlobOffsetBias,
			minimumBlobDataOffset: minimumBlobDataOffset);

		var sections = new List<TransitionSectionInspection> { rootSection };
		var manifest = new TransitionFileManifest
		{
			FileName = Path.GetFileName(path),
			FullPath = path,
			Variant = "SAGA.SCP",
			CountHeaderOffset = CountHeaderOffset,
			AllocationDecodedSize = allocationDecodedSize,
			LzStreamOffset = LzStreamOffset,
			LzDecodedSize = lzDecodedSize,
			LzBytesConsumed = bytesConsumed,
			DecompressedLength = decoded.Length,
			RootRecordCount = recordCount,
			RootRecordOffset = ScpRootTableOffset,
			RootRecordSize = ScpRootRecordSize,
			Sections = sections.Select(section => section.Manifest).ToList()
		};

		return new TransitionFileInspection(
			manifest,
			sections,
			encoded.AsSpan(0, HeaderSize).ToArray(),
			retainDecompressedPayload ? decoded : null,
			null);
	}

	private static TransitionFileInspection InspectSagaBin(
		string path,
		byte[] encoded,
		byte[] decoded,
		uint allocationDecodedSize,
		uint lzDecodedSize,
		int bytesConsumed,
		bool retainDecompressedPayload,
		ArchiveInspection? objectMobInspection)
	{
		var firstSectionCount = ReadUInt16(decoded, BinFirstSectionCountOffset);
		var firstSectionLength = checked(firstSectionCount * BinFirstSectionEntrySize);
		var secondSectionCountOffset = checked(BinFirstSectionOffset + firstSectionLength);
		EnsureAvailable(decoded, secondSectionCountOffset, 2, path, "SAGA.BIN second-section count");

		var secondSectionCount = ReadUInt16(decoded, secondSectionCountOffset);
		var secondSectionOffset = secondSectionCountOffset + 2;
		var secondSectionLength = checked(secondSectionCount * BinSecondSectionEntrySize);
		var publishedSelectorCountOffset = checked(secondSectionOffset + secondSectionLength);
		EnsureAvailable(decoded, publishedSelectorCountOffset, 2, path, "SAGA.BIN published selector count");

		var publishedSelectorCount = ReadUInt16(decoded, publishedSelectorCountOffset);
		var publishedSelectorOffset = publishedSelectorCountOffset + 2;
		var publishedSelectorTableLength = checked(publishedSelectorCount * BinPublishedSelectorEntrySize);
		EnsureAvailable(decoded, publishedSelectorOffset, publishedSelectorTableLength, path, "SAGA.BIN published selector table");

		var firstSection = BuildSection(
			decoded,
			"first_0x80_records",
			BinFirstSectionOffset,
			firstSectionCount,
			BinFirstSectionEntrySize);
		var secondSection = BuildSection(
			decoded,
			"second_0x44_records",
			secondSectionOffset,
			secondSectionCount,
			BinSecondSectionEntrySize);
		var publishedSelectorSection = BuildSection(
			decoded,
			"published_0x24_records",
			publishedSelectorOffset,
			publishedSelectorCount,
			BinPublishedSelectorEntrySize,
			blobOffsetFieldOffset: ScpBlobOffsetFieldOffset,
			blobOffsetBias: 0,
			blobPointerBaseOffset: publishedSelectorOffset,
			minimumBlobDataOffset: checked(publishedSelectorOffset + publishedSelectorTableLength));
		AnalyzePublishedSelectorEntries(
			decoded,
			publishedSelectorSection,
			secondSection,
			objectMobInspection is null ? null : BuildObjectMobEntries(objectMobInspection));
		var sections = new List<TransitionSectionInspection> { firstSection, secondSection, publishedSelectorSection };
		var publishedSelectorRegion = decoded.AsSpan(publishedSelectorOffset).ToArray();

		var manifest = new TransitionFileManifest
		{
			FileName = Path.GetFileName(path),
			FullPath = path,
			Variant = "SAGA.BIN",
			CountHeaderOffset = CountHeaderOffset,
			AllocationDecodedSize = allocationDecodedSize,
			LzStreamOffset = LzStreamOffset,
			LzDecodedSize = lzDecodedSize,
			LzBytesConsumed = bytesConsumed,
			DecompressedLength = decoded.Length,
			FirstSectionCount = firstSectionCount,
			FirstSectionOffset = BinFirstSectionOffset,
			FirstSectionEntrySize = BinFirstSectionEntrySize,
			SecondSectionCount = secondSectionCount,
			SecondSectionOffset = secondSectionOffset,
			SecondSectionEntrySize = BinSecondSectionEntrySize,
			PublishedSelectorCount = publishedSelectorCount,
			PublishedSelectorOffset = publishedSelectorOffset,
			PublishedSelectorRegionLength = publishedSelectorRegion.Length,
			PublishedSelectorRegionFirstBytesHex = BuildHexPreview(publishedSelectorRegion, 24),
			Sections = sections.Select(section => section.Manifest).ToList()
		};

		return new TransitionFileInspection(
			manifest,
			sections,
			encoded.AsSpan(0, HeaderSize).ToArray(),
			retainDecompressedPayload ? decoded : null,
			publishedSelectorRegion);
	}

	private static TransitionSectionInspection BuildSection(
		byte[] decoded,
		string sectionName,
		int tableOffset,
		int entryCount,
		int entrySize,
		int? blobOffsetFieldOffset = null,
		int blobOffsetBias = 0,
		int blobPointerBaseOffset = 0,
		int? minimumBlobDataOffset = null)
	{
		var tableLength = checked(entryCount * entrySize);
		EnsureAvailable(decoded, tableOffset, tableLength, sectionName, "table bytes");

		var manifests = new List<TransitionSectionEntryManifest>(entryCount);
		var entries = new List<TransitionSectionEntryPayload>(entryCount);

		for (var index = 0; index < entryCount; index++)
		{
			var entryOffset = checked(tableOffset + (index * entrySize));
			var payload = decoded.AsSpan(entryOffset, entrySize).ToArray();

			uint? blobFieldValue = null;
			int? blobDataOffsetCandidate = null;
			if (blobOffsetFieldOffset is { } fieldOffset && fieldOffset + 4 <= payload.Length)
			{
				var candidate = ReadUInt32(payload, fieldOffset);
				blobFieldValue = candidate;
				var candidateOffset = (long)blobPointerBaseOffset + candidate + blobOffsetBias;
				if (candidateOffset >= 0
					&& candidateOffset < decoded.Length
					&& (!minimumBlobDataOffset.HasValue || candidateOffset >= minimumBlobDataOffset.Value))
				{
					blobDataOffsetCandidate = checked((int)candidateOffset);
				}
			}

			var manifest = new TransitionSectionEntryManifest
			{
				SectionName = sectionName,
				Index = index,
				EntryOffset = entryOffset,
				FirstBytesHex = BuildHexPreview(payload, 24),
				TextPreview = BuildTextPreview(payload),
				BlobOffsetField = blobFieldValue,
				BlobDataOffsetCandidate = blobDataOffsetCandidate
			};

			manifests.Add(manifest);
			entries.Add(new TransitionSectionEntryPayload(manifest, payload));
		}

		return new TransitionSectionInspection(
			new TransitionSectionManifest
			{
				SectionName = sectionName,
				TableOffset = tableOffset,
				EntryCount = entryCount,
				EntrySize = entrySize,
				Entries = manifests
			},
			entries,
			decoded.AsSpan(tableOffset, tableLength).ToArray());
	}

	private static void AnalyzePublishedSelectorEntries(
		byte[] decoded,
		TransitionSectionInspection publishedSelectorSection,
		TransitionSectionInspection secondSection,
		IReadOnlyDictionary<int, ObjectMobArchiveEntry>? objectMobEntries)
	{
		var visibleTopLevelDescriptors = BuildVisibleTopLevelDescriptors(secondSection);
		foreach (var entry in publishedSelectorSection.Entries)
		{
			if (entry.Manifest.BlobDataOffsetCandidate is not int blobOffset)
			{
				continue;
			}

			entry.Manifest.PublishedSelectorAnalysis = AnalyzePublishedSelectorEntry(
				decoded,
				blobOffset,
				visibleTopLevelDescriptors,
				secondSection.Manifest.EntryCount,
				objectMobEntries);
		}
	}

	private static TransitionPublishedSelectorAnalysisManifest AnalyzePublishedSelectorEntry(
		byte[] decoded,
		int blobOffset,
		IReadOnlyDictionary<int, VisibleTopLevelDescriptor> visibleTopLevelDescriptors,
		int visibleTopLevelDescriptorCount,
		IReadOnlyDictionary<int, ObjectMobArchiveEntry>? objectMobEntries)
	{
		var firstKeySections = ParseCountedSections(
			decoded,
			checked(blobOffset + FirstKeyBlobSectionBaseOffset),
			FirstKeyBlobSectionEntrySizes,
			$"published selector blob 0x{blobOffset:X}");
		var aliasSection = firstKeySections[^1];
		var aliasValues = new List<int>(aliasSection.EntryCount);
		for (var index = 0; index < aliasSection.EntryCount; index++)
		{
			aliasValues.Add(checked((int)ReadUInt32(decoded, checked(aliasSection.DataOffset + (index * aliasSection.EntrySize)))));
		}

		var aliasesBeyondVisibleTransitionDescriptors = aliasValues
			.Where(value => value < 0 || value >= visibleTopLevelDescriptorCount)
			.Distinct()
			.OrderBy(value => value)
			.ToList();

		var aliasesOutsideObjectMobEntryTable = new List<int>();
		var distinctObjectMobEntries = new List<ObjectMobArchiveEntryManifest>();
		if (objectMobEntries is not null)
		{
			foreach (var aliasValue in aliasValues.Distinct().OrderBy(value => value))
			{
				if (objectMobEntries.TryGetValue(aliasValue, out var objectMobEntry))
				{
					distinctObjectMobEntries.Add(BuildObjectMobEntryManifest(objectMobEntry));
				}
				else
				{
					aliasesOutsideObjectMobEntryTable.Add(aliasValue);
				}
			}
		}

		var subsceneCountOffset = aliasSection.EndOffset;
		EnsureAvailable(decoded, subsceneCountOffset, 2, "published selector blob", "subscene count");
		var subsceneCount = ReadUInt16(decoded, subsceneCountOffset);
		var subsceneTableOffset = subsceneCountOffset + 2;
		EnsureAvailable(
			decoded,
			subsceneTableOffset,
			checked(subsceneCount * BinPublishedSelectorEntrySize),
			"published selector blob",
			"subscene table");

		var subscenes = new List<TransitionPublishedSubsceneManifest>(subsceneCount);
		for (var index = 0; index < subsceneCount; index++)
		{
			var entryOffset = checked(subsceneTableOffset + (index * BinPublishedSelectorEntrySize));
			var subsceneName = DecodeName(decoded, entryOffset, SceneRecordNameLength);
			var subsceneBlobOffset = checked(subsceneTableOffset + (int)ReadUInt32(decoded, entryOffset + ScpBlobOffsetFieldOffset));

			try
			{
				subscenes.Add(AnalyzePublishedSubscene(
					decoded,
					index,
					subsceneName,
					subsceneBlobOffset,
					aliasValues,
					visibleTopLevelDescriptors,
					objectMobEntries));
			}
			catch (Exception ex)
			{
				subscenes.Add(new TransitionPublishedSubsceneManifest
					{
						Index = index,
						Name = subsceneName,
						BlobOffset = subsceneBlobOffset,
						ParseError = ex.Message,
						SceneRecords = new List<TransitionSceneRecordAnalysisManifest>()
					});
			}
		}

		return new TransitionPublishedSelectorAnalysisManifest
		{
			BlobOffset = blobOffset,
			AliasCount = aliasValues.Count,
			VisibleTopLevelDescriptorCount = visibleTopLevelDescriptorCount,
			AliasValues = aliasValues,
			AliasesBeyondVisibleTransitionDescriptors = aliasesBeyondVisibleTransitionDescriptors,
			ObjectMobEntryCount = objectMobEntries?.Count,
			AliasesOutsideObjectMobEntryTable = aliasesOutsideObjectMobEntryTable,
			DistinctObjectMobEntries = distinctObjectMobEntries,
			SubsceneCount = subsceneCount,
			Subscenes = subscenes
		};
	}

	private static TransitionPublishedSubsceneManifest AnalyzePublishedSubscene(
		byte[] decoded,
		int subsceneIndex,
		string subsceneName,
		int blobOffset,
		IReadOnlyList<int> aliasValues,
		IReadOnlyDictionary<int, VisibleTopLevelDescriptor> visibleTopLevelDescriptors,
		IReadOnlyDictionary<int, ObjectMobArchiveEntry>? objectMobEntries)
	{
		var secondKeySections = ParseCountedSections(
			decoded,
			checked(blobOffset + SecondKeyBlobSectionBaseOffset),
			SecondKeyBlobSectionEntrySizes,
			$"subscene blob 0x{blobOffset:X}");
		var sceneRecordSection = secondKeySections[SecondKeySceneRecordSectionIndex];
		var sceneRecords = new List<TransitionSceneRecordAnalysisManifest>(sceneRecordSection.EntryCount);

		for (var index = 0; index < sceneRecordSection.EntryCount; index++)
		{
			var recordOffset = checked(sceneRecordSection.DataOffset + (index * sceneRecordSection.EntrySize));
			EnsureAvailable(decoded, recordOffset, sceneRecordSection.EntrySize, "subscene blob", "scene record");
			var selector = checked((int)ReadUInt32(decoded, recordOffset + SceneRecordSelectorOffset));
			int? aliasValue = selector >= 0 && selector < aliasValues.Count
				? aliasValues[selector]
				: null;
			VisibleTopLevelDescriptor? visibleDescriptor = null;
			if (aliasValue is int descriptorIndex)
			{
				visibleTopLevelDescriptors.TryGetValue(descriptorIndex, out visibleDescriptor);
			}

			ObjectMobArchiveEntryManifest? objectMobEntry = null;
			if (aliasValue is int objectMobEntryIndex
				&& objectMobEntries is not null
				&& objectMobEntries.TryGetValue(objectMobEntryIndex, out var resolvedObjectMobEntry))
			{
				objectMobEntry = BuildObjectMobEntryManifest(resolvedObjectMobEntry);
			}

			sceneRecords.Add(new TransitionSceneRecordAnalysisManifest
			{
				Index = index,
				RecordOffset = recordOffset,
				Name = DecodeName(decoded, recordOffset, SceneRecordNameLength),
				Selector = selector,
				AliasValue = aliasValue,
				VisibleTopLevelDescriptorIndex = visibleDescriptor?.Index,
				VisibleTopLevelDescriptorName = visibleDescriptor?.Name,
				VisibleTopLevelDescriptorField20 = visibleDescriptor?.Field20,
				VisibleTopLevelDescriptorField24 = visibleDescriptor?.Field24,
				ObjectMobEntry = objectMobEntry,
				Flags28 = decoded[recordOffset + SceneRecordFlagsOffset],
				PositionX = ReadUInt16(decoded, recordOffset + SceneRecordPositionXOffset),
				PositionY = ReadUInt16(decoded, recordOffset + SceneRecordPositionYOffset),
				Field32 = ReadUInt16(decoded, recordOffset + SceneRecordField32Offset),
				Field34 = ReadUInt16(decoded, recordOffset + SceneRecordField34Offset),
				Field36 = ReadUInt16(decoded, recordOffset + SceneRecordField36Offset),
				Field38 = ReadUInt16(decoded, recordOffset + SceneRecordField38Offset),
				Field3A = ReadUInt16(decoded, recordOffset + SceneRecordField3AOffset),
				Field3C = ReadUInt32(decoded, recordOffset + SceneRecordField3COffset)
			});
		}

		return new TransitionPublishedSubsceneManifest
		{
			Index = subsceneIndex,
			Name = subsceneName,
			BlobOffset = blobOffset,
			Prelude44Count = secondKeySections[SecondKeyPrelude44SectionIndex].EntryCount,
			Interaction4CCount = secondKeySections[SecondKeyInteraction4CSectionIndex].EntryCount,
			SceneRecordCount = sceneRecordSection.EntryCount,
			AnchorCount = secondKeySections[SecondKeyAnchorSectionIndex].EntryCount,
			SceneRecords = sceneRecords
		};
	}

	private static Dictionary<int, VisibleTopLevelDescriptor> BuildVisibleTopLevelDescriptors(TransitionSectionInspection secondSection)
	{
		var descriptors = new Dictionary<int, VisibleTopLevelDescriptor>(secondSection.Entries.Count);
		foreach (var entry in secondSection.Entries)
		{
			var payload = entry.Payload;
			if (payload.Length < BinSecondSectionEntrySize)
			{
				continue;
			}

			descriptors[entry.Manifest.Index] = new VisibleTopLevelDescriptor(
				entry.Manifest.Index,
				DecodeName(payload, 0, TopLevelDescriptorNameLength),
				ReadUInt32(payload, TopLevelDescriptorField20Offset),
				ReadUInt32(payload, TopLevelDescriptorField24Offset));
		}

		return descriptors;
	}

	private static Dictionary<int, ObjectMobArchiveEntry> BuildObjectMobEntries(ArchiveInspection objectMobInspection)
	{
		var entries = new Dictionary<int, ObjectMobArchiveEntry>(objectMobInspection.Entries.Count);
		foreach (var entry in objectMobInspection.Entries)
		{
			entries[entry.Manifest.Index] = new ObjectMobArchiveEntry(
				entry.Manifest.Index,
				entry.Manifest.DecodedName,
				entry.Manifest.DataOffset,
				entry.Manifest.MobDataOffset,
				entry.MobMetadataRecords.Count,
				entry.MobDataItems.Count,
				entry.MobDataItems.Any(item => item.Manifest.IsLikelyLiveWorldSprite)
					? ArchiveInspector.LiveWorldSpriteDataItemIndex
					: null);
		}

		return entries;
	}

	private static ObjectMobArchiveEntryManifest BuildObjectMobEntryManifest(ObjectMobArchiveEntry entry)
	{
		return new ObjectMobArchiveEntryManifest
		{
			Index = entry.Index,
			Name = entry.Name,
			MetadataBlockOffset = entry.MetadataBlockOffset,
			DataBlockOffset = entry.DataBlockOffset,
			MetadataRecordCount = entry.MetadataRecordCount,
			DataItemCount = entry.DataItemCount,
			LikelyLiveWorldSpriteDataItemIndex = entry.LikelyLiveWorldSpriteDataItemIndex
		};
	}

	private static List<CountedSectionParse> ParseCountedSections(
		byte[] decoded,
		int startOffset,
		IReadOnlyList<int> entrySizes,
		string description)
	{
		var sections = new List<CountedSectionParse>(entrySizes.Count);
		var cursor = startOffset;
		for (var index = 0; index < entrySizes.Count; index++)
		{
			EnsureAvailable(decoded, cursor, 2, description, $"section {index} count");
			var entryCount = ReadUInt16(decoded, cursor);
			var dataOffset = cursor + 2;
			var byteLength = checked(entryCount * entrySizes[index]);
			EnsureAvailable(decoded, dataOffset, byteLength, description, $"section {index} data");
			sections.Add(new CountedSectionParse(entrySizes[index], entryCount, dataOffset));
			cursor = checked(dataOffset + byteLength);
		}

		return sections;
	}

	private static string DecodeName(byte[] data, int offset, int maxLength)
	{
		EnsureAvailable(data, offset, maxLength, nameof(TransitionFileInspector), "name decode");
		var zeroIndex = Array.IndexOf(data, (byte)0, offset, maxLength);
		var count = zeroIndex >= 0 ? zeroIndex - offset : maxLength;
		if (count <= 0)
		{
			return string.Empty;
		}

		return KoreanEncoding.GetString(data, offset, count).Trim();
	}

	private static string? BuildTextPreview(byte[] payload)
	{
		var previewLength = Math.Min(payload.Length, 32);
		if (previewLength == 0)
		{
			return null;
		}

		var decoded = KoreanEncoding.GetString(payload, 0, previewLength);
		var builder = new StringBuilder(decoded.Length);
		foreach (var character in decoded)
		{
			if (character == '\0')
			{
				continue;
			}

			builder.Append(char.IsControl(character) ? ' ' : character);
		}

		var normalized = Regex.Replace(builder.ToString(), @"\s+", " ").Trim();
		return string.IsNullOrEmpty(normalized) ? null : normalized;
	}

	private static string BuildHexPreview(byte[] payload, int maxBytes)
	{
		var previewLength = Math.Min(payload.Length, maxBytes);
		return previewLength == 0
			? string.Empty
			: Convert.ToHexString(payload.AsSpan(0, previewLength));
	}

	private static void EnsureAvailable(byte[] data, int offset, int length, string path, string description)
	{
		if (offset < 0 || length < 0 || offset + length > data.Length)
		{
			throw new InvalidDataException($"{path} does not contain a complete {description} at 0x{offset:X} (length 0x{length:X}).");
		}
	}

	private static ushort ReadUInt16(byte[] data, int offset)
	{
		return BitConverter.ToUInt16(data, offset);
	}

	private static uint ReadUInt32(byte[] data, int offset)
	{
		return BitConverter.ToUInt32(data, offset);
	}

	private sealed record CountedSectionParse(int EntrySize, int EntryCount, int DataOffset)
	{
		public int EndOffset => checked(DataOffset + (EntryCount * EntrySize));
	}

	private sealed record VisibleTopLevelDescriptor(int Index, string Name, uint Field20, uint Field24);
	private sealed record ObjectMobArchiveEntry(
		int Index,
		string Name,
		uint MetadataBlockOffset,
		uint? DataBlockOffset,
		int MetadataRecordCount,
		int DataItemCount,
		int? LikelyLiveWorldSpriteDataItemIndex);
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

sealed record CharDatInspection(
	CharDatManifest Manifest,
	List<CharDatRecordPayload> Entries,
	byte[] HeaderBytes,
	byte[]? DecompressedPayload);

sealed record TransitionFileInspection(
	TransitionFileManifest Manifest,
	List<TransitionSectionInspection> Sections,
	byte[] HeaderBytes,
	byte[]? DecompressedPayload,
	byte[]? PublishedSelectorRegion);

sealed record TransitionSectionInspection(
	TransitionSectionManifest Manifest,
	List<TransitionSectionEntryPayload> Entries,
	byte[] RawBytes);

sealed record TransitionSectionEntryPayload(
	TransitionSectionEntryManifest Manifest,
	byte[] Payload);

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

sealed record CharDatRecordPayload(
	CharDatRecordManifest Manifest,
	byte[] Payload);

sealed record TransitionScenePaletteCandidate(
	string SceneName,
	string PalettePath,
	IndexedPalette Palette,
	IReadOnlyList<string> SourceFiles);

sealed record ExternalPaletteCandidate(
	string CandidateName,
	string DirectoryName,
	string PalettePath,
	IndexedPalette Palette,
	string Evidence);

sealed record MenuEventPaletteCandidate(
	string CandidateName,
	string PalettePath,
	IndexedPalette Palette,
	string Evidence);

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
	public List<TransitionScenePaletteCandidateManifest> TransitionScenePaletteCandidates { get; } = new();
	public List<MenuEventPaletteCandidateManifest> MenuEventPaletteCandidates { get; } = new();
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

sealed class CharDatManifest
{
	public required string FileName { get; init; }
	public required string FullPath { get; init; }
	public required uint RecordCount { get; init; }
	public required int RecordSize { get; init; }
	public required int CountHeaderOffset { get; init; }
	public required int LzStreamOffset { get; init; }
	public required uint LzDecodedSize { get; init; }
	public required uint ExpectedDecodedSize { get; init; }
	public required int LzBytesConsumed { get; init; }
	public required int NameFieldLength { get; init; }
	public required int NodeTemplateOffset { get; init; }
	public required int NodeTemplateLength { get; init; }
	public required int SlotRecordSize { get; init; }
	public required int PrimarySlotGroupOffset { get; init; }
	public required int PrimarySlotGroupCount { get; init; }
	public required int SecondarySlotGroupCount { get; init; }
	public List<int> SecondarySlotGroupOffsets { get; } = new();
	public required List<CharDatRecordManifest> Entries { get; init; }
}

sealed class TransitionFileManifest
{
	public required string FileName { get; init; }
	public required string FullPath { get; init; }
	public required string Variant { get; init; }
	public required int CountHeaderOffset { get; init; }
	public required uint AllocationDecodedSize { get; init; }
	public required int LzStreamOffset { get; init; }
	public required uint LzDecodedSize { get; init; }
	public required int LzBytesConsumed { get; init; }
	public required int DecompressedLength { get; init; }
	public int? RootRecordCount { get; init; }
	public int? RootRecordOffset { get; init; }
	public int? RootRecordSize { get; init; }
	public int? FirstSectionCount { get; init; }
	public int? FirstSectionOffset { get; init; }
	public int? FirstSectionEntrySize { get; init; }
	public int? SecondSectionCount { get; init; }
	public int? SecondSectionOffset { get; init; }
	public int? SecondSectionEntrySize { get; init; }
	public int? PublishedSelectorCount { get; init; }
	public int? PublishedSelectorOffset { get; init; }
	public int? PublishedSelectorRegionLength { get; init; }
	public string? PublishedSelectorRegionFirstBytesHex { get; init; }
	public string? PublishedSelectorRegionOutputFile { get; set; }
	public required List<TransitionSectionManifest> Sections { get; init; }
}

sealed class TransitionSectionManifest
{
	public required string SectionName { get; init; }
	public required int TableOffset { get; init; }
	public required int EntryCount { get; init; }
	public required int EntrySize { get; init; }
	public string? OutputFile { get; set; }
	public required List<TransitionSectionEntryManifest> Entries { get; init; }
}

sealed class TransitionSectionEntryManifest
{
	public required string SectionName { get; init; }
	public required int Index { get; init; }
	public required int EntryOffset { get; init; }
	public required string FirstBytesHex { get; init; }
	public string? TextPreview { get; init; }
	public uint? BlobOffsetField { get; init; }
	public int? BlobDataOffsetCandidate { get; init; }
	public TransitionPublishedSelectorAnalysisManifest? PublishedSelectorAnalysis { get; set; }
	public string? OutputFile { get; set; }
}

sealed class TransitionPublishedSelectorAnalysisManifest
{
	public required int BlobOffset { get; init; }
	public required int AliasCount { get; init; }
	public required int VisibleTopLevelDescriptorCount { get; init; }
	public required List<int> AliasValues { get; init; }
	public required List<int> AliasesBeyondVisibleTransitionDescriptors { get; init; }
	public int? ObjectMobEntryCount { get; init; }
	public required List<int> AliasesOutsideObjectMobEntryTable { get; init; }
	public required List<ObjectMobArchiveEntryManifest> DistinctObjectMobEntries { get; init; }
	public required int SubsceneCount { get; init; }
	public required List<TransitionPublishedSubsceneManifest> Subscenes { get; init; }
}

sealed class TransitionPublishedSubsceneManifest
{
	public required int Index { get; init; }
	public required string Name { get; init; }
	public required int BlobOffset { get; init; }
	public int? Prelude44Count { get; init; }
	public int? Interaction4CCount { get; init; }
	public int? SceneRecordCount { get; init; }
	public int? AnchorCount { get; init; }
	public string? ParseError { get; init; }
	public required List<TransitionSceneRecordAnalysisManifest> SceneRecords { get; init; }
}

sealed class TransitionSceneRecordAnalysisManifest
{
	public required int Index { get; init; }
	public required int RecordOffset { get; init; }
	public required string Name { get; init; }
	public required int Selector { get; init; }
	public int? AliasValue { get; init; }
	public int? VisibleTopLevelDescriptorIndex { get; init; }
	public string? VisibleTopLevelDescriptorName { get; init; }
	public uint? VisibleTopLevelDescriptorField20 { get; init; }
	public uint? VisibleTopLevelDescriptorField24 { get; init; }
	public ObjectMobArchiveEntryManifest? ObjectMobEntry { get; init; }
	public required byte Flags28 { get; init; }
	public required ushort PositionX { get; init; }
	public required ushort PositionY { get; init; }
	public required ushort Field32 { get; init; }
	public required ushort Field34 { get; init; }
	public required ushort Field36 { get; init; }
	public required ushort Field38 { get; init; }
	public required ushort Field3A { get; init; }
	public required uint Field3C { get; init; }
}

sealed class ObjectMobArchiveEntryManifest
{
	public required int Index { get; init; }
	public required string Name { get; init; }
	public required uint MetadataBlockOffset { get; init; }
	public uint? DataBlockOffset { get; init; }
	public required int MetadataRecordCount { get; init; }
	public required int DataItemCount { get; init; }
	public int? LikelyLiveWorldSpriteDataItemIndex { get; init; }
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
	public string? PreviewPaletteSource { get; set; }
	public string? PaletteAuthority { get; set; }
	public int? LiveWorldSpriteDataItemIndex { get; set; }
	public string? LiveWorldBindingModel { get; set; }
	public string? LiveWorldSpriteDecodedPngFile { get; set; }
	public string? LiveWorldSpriteDecodedPaletteSource { get; set; }
	public List<LiveWorldSpritePaletteCandidateManifest> LiveWorldSpritePaletteCandidates { get; } = new();
	public List<ArchiveSubresourceManifest> Subresources { get; } = new();
	public List<MobMetadataRecordManifest> MobMetadataRecords { get; } = new();
	public List<MobDataItemManifest> MobDataItems { get; } = new();
}

sealed class TransitionScenePaletteCandidateManifest
{
	public required string SceneName { get; init; }
	public required string PaletteOutputFile { get; init; }
	public required string PaletteSourceDescription { get; init; }
	public required List<string> MatchedInFiles { get; init; }
}

sealed class MenuEventPaletteCandidateManifest
{
	public required string CandidateName { get; init; }
	public required string PaletteOutputFile { get; init; }
	public required string PaletteSourceDescription { get; init; }
	public required string Evidence { get; init; }
}

sealed class LiveWorldSpritePaletteCandidateManifest
{
	public required string SceneName { get; init; }
	public required List<string> MatchedInFiles { get; init; }
	public required string PaletteOutputFile { get; init; }
	public required string PaletteSourceDescription { get; init; }
	public required string DecodedPngFile { get; init; }
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

sealed class CharDatRecordManifest
{
	public required int Index { get; init; }
	public required int RecordOffset { get; init; }
	public required string DecodedName { get; init; }
	public required string RawNameHex { get; init; }
	public string? OutputFile { get; set; }
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
	public string? DecodedPaletteSource { get; set; }
	public string? DecodedExportError { get; set; }
	public int? AlternateDecodedCanvasWidth { get; set; }
	public int? AlternateDecodedCanvasHeight { get; set; }
	public string? AlternateDecodedPlacementMode { get; set; }
	public string? AlternateDecodedPngFile { get; set; }
	public string? AlternateDecodedExportError { get; set; }
	public List<MobDataItemPaletteCandidateManifest> PaletteCandidates { get; } = new();
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
	public bool IsLikelyLiveWorldSprite { get; init; }
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
	public List<MobDataItemPaletteCandidateManifest> PaletteCandidates { get; } = new();
}

sealed class MobDataItemPaletteCandidateManifest
{
	public required string CandidateName { get; init; }
	public required string PaletteOutputFile { get; init; }
	public required string PaletteSourceDescription { get; init; }
	public required string Evidence { get; init; }
	public required string DecodedPngFile { get; init; }
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
