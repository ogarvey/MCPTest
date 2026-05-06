using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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
var hasPreviewPaletteOverride = hasExplicitPaletteSelection;
TransitionPaletteContext transitionPaletteContext;
IReadOnlyList<TransitionScenePaletteCandidate> transitionScenePaletteCandidates;
try
{
	transitionPaletteContext = !hasExplicitPaletteSelection
		? LoadTransitionPaletteContext(sampleRoot, outputRoot, jsonOptions)
		: TransitionPaletteContext.Empty;
	transitionScenePaletteCandidates = transitionPaletteContext.SceneCandidates;
}
catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or InvalidDataException or JsonException)
{
	Console.Error.WriteLine($"Transition-backed palette candidate scan skipped: {ex.Message}");
	transitionPaletteContext = TransitionPaletteContext.Empty;
	transitionScenePaletteCandidates = transitionPaletteContext.SceneCandidates;
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

		var entryScopedTransitionPaletteCandidates = !hasExplicitPaletteSelection
			&& transitionPaletteContext.ObjectMobEntryPaletteCandidates.TryGetValue(entry.Manifest.Index, out var scopedTransitionPaletteCandidates)
				? scopedTransitionPaletteCandidates
				: Array.Empty<TransitionScenePaletteCandidate>();
		var entryRuntimeTransitionPaletteCandidates = entryScopedTransitionPaletteCandidates.Count > 0
			? entryScopedTransitionPaletteCandidates
			: transitionScenePaletteCandidates;
		var entryExternalPaletteCandidates = !hasExplicitPaletteSelection
			? BuildExternalPaletteCandidates(entryRuntimeTransitionPaletteCandidates, menuEventPaletteCandidate)
			: new List<ExternalPaletteCandidate>();
		var entryHasExternalPaletteCandidates = entryExternalPaletteCandidates.Count > 0;
		var entryAuthoritativeTransitionPalette = !hasExplicitPaletteSelection && entryScopedTransitionPaletteCandidates.Count == 1
			? entryScopedTransitionPaletteCandidates[0]
			: null;
		var entryPreviewPalette = hasPreviewPaletteOverride
			? palette
			: entryAuthoritativeTransitionPalette?.Palette
				?? (entryScopedTransitionPaletteCandidates.Count > 0 ? null : archive.HeaderPalette ?? palette);
		var entryLiveWorldOutputPalette = palette
			?? entryAuthoritativeTransitionPalette?.Palette
			?? (entryScopedTransitionPaletteCandidates.Count > 0 ? null : archive.HeaderPalette);

		if (entry.MobDataItems.Count > 0 || entry.MobMetadataRecords.Count > 0)
		{
			entry.Manifest.PreviewPaletteSource = entryPreviewPalette?.SourceDescription;
			entry.Manifest.PaletteAuthority = hasExplicitPaletteSelection && entryPreviewPalette is not null
				? "explicit palette override"
				: entryAuthoritativeTransitionPalette is not null
					? $"runtime fixed-block FORGA scene palette '{entryAuthoritativeTransitionPalette.SceneName}'"
					: entryScopedTransitionPaletteCandidates.Count > 0
						? "runtime fixed-block FORGA scene palette narrows to scoped candidates; no single authority selected"
						: "external palette authority unresolved; preview uses archive/header palette when present and runtime-backed external palette candidates stay separate";
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

					if (entryPreviewPalette is not null)
					{
						var pngFileName = $"{baseName}.png";
						ImageWriter.WritePng(Path.Combine(entryDir, pngFileName), decodedImage, entryPreviewPalette);
						subresource.Manifest.DecodedPngFile = pngFileName;
						subresource.Manifest.DecodedPaletteSource = entryPreviewPalette.SourceDescription;
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

						if (entryPreviewPalette is not null)
						{
							var pngFileName = $"{baseName}.png";
							ImageWriter.WritePng(Path.Combine(dataDir, pngFileName), decodedImage, entryPreviewPalette);
							item.Manifest.DecodedPngFile = pngFileName;
							item.Manifest.DecodedPaletteSource = entryPreviewPalette.SourceDescription;
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
				if (entryLiveWorldOutputPalette is not null)
				{
					const string liveWorldSpriteFileName = "world_sprite.png";
					ImageWriter.WritePng(Path.Combine(entryDir, liveWorldSpriteFileName), liveWorldSpriteImage, entryLiveWorldOutputPalette);
					entry.Manifest.LiveWorldSpriteDecodedPngFile = liveWorldSpriteFileName;
					entry.Manifest.LiveWorldSpriteDecodedPaletteSource = entryLiveWorldOutputPalette.SourceDescription;
				}
				else if (entryRuntimeTransitionPaletteCandidates.Count > 0)
				{
					const string candidateDirName = "world_sprite_scene_candidates";
					var candidateDir = Path.Combine(entryDir, candidateDirName);
					Directory.CreateDirectory(candidateDir);

					for (var candidateIndex = 0; candidateIndex < entryRuntimeTransitionPaletteCandidates.Count; candidateIndex++)
					{
						var candidate = entryRuntimeTransitionPaletteCandidates[candidateIndex];
						var candidateFileName = $"scene_{candidateIndex:D3}_{SanitizeFileComponent(candidate.SceneName)}.png";
						ImageWriter.WritePng(Path.Combine(candidateDir, candidateFileName), liveWorldSpriteImage, candidate.Palette);
						entry.Manifest.LiveWorldSpritePaletteCandidates.Add(new LiveWorldSpritePaletteCandidateManifest
						{
							SceneName = candidate.SceneName,
							MatchedInFiles = candidate.Evidence.Count > 0 ? candidate.Evidence.ToList() : candidate.SourceFiles.ToList(),
							PaletteOutputFile = Path.GetRelativePath(outputRoot, candidate.PalettePath),
							PaletteSourceDescription = candidate.Palette.SourceDescription,
							DecodedPngFile = Path.Combine(candidateDirName, candidateFileName)
						});
					}
				}
			}
		}

		if (entryHasExternalPaletteCandidates && decodedMobSprites.Count > 0)
		{
			var paletteCandidatesRootDirName = "palette_candidates";
			var paletteCandidatesRootDir = Path.Combine(entryDir, paletteCandidatesRootDirName);
			Directory.CreateDirectory(paletteCandidatesRootDir);

			foreach (var paletteCandidate in entryExternalPaletteCandidates)
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

		if ((entryPreviewPalette is not null || entryHasExternalPaletteCandidates)
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
					if (entryPreviewPalette is not null)
					{
						try
						{
							ImageWriter.WritePlacedHorizontalStripPng(
								Path.Combine(metadataDir, explicitPngFileName),
								explicitPlacedFrames,
								explicitPlacement,
								entryPreviewPalette);
							record.Manifest.DecodedPngFile = explicitPngFileName;
							record.Manifest.DecodedPaletteSource = entryPreviewPalette.SourceDescription;
						}
						catch (MobCanvasTooLargeException ex)
						{
							record.Manifest.DecodedExportError = ex.Message;
							Console.Error.WriteLine($"Skipping explicit strip for {Path.GetFileName(inputFile)} entry {entry.Manifest.Index:D3} record {record.Manifest.Index:D3} ({record.Manifest.DecodedName}): {ex.Message}");
						}
					}

					if (entryPreviewPalette is not null
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
								entryPreviewPalette);
							record.Manifest.AlternateDecodedPngFile = sequencePngFileName;
						}
						catch (MobCanvasTooLargeException ex)
						{
							record.Manifest.AlternateDecodedExportError = ex.Message;
							Console.Error.WriteLine($"Skipping sequence strip for {Path.GetFileName(inputFile)} entry {entry.Manifest.Index:D3} record {record.Manifest.Index:D3} ({record.Manifest.DecodedName}): {ex.Message}");
						}
					}
				}

				if (entryHasExternalPaletteCandidates)
				{
					var paletteCandidatesRootDirName = Path.Combine("metadata", "palette_candidates");

					foreach (var paletteCandidate in entryExternalPaletteCandidates)
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

				if (entryPreviewPalette is not null)
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
								entryPreviewPalette);
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

				if (entryPreviewPalette is not null
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
								entryPreviewPalette);
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

static string ResolveForgaScenePalettePathFromManifest(
	string sceneOrResourceName,
	FamManifest manifest,
	string forgaOutputDir)
{
	if (TryResolveForgaScenePalettePath(
		manifest,
		forgaOutputDir,
		sceneOrResourceName,
		out var palettePath,
		out _,
		out var error))
	{
		return palettePath;
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

static TransitionPaletteContext LoadTransitionPaletteContext(
	string sampleRoot,
	string outputRoot,
	JsonSerializerOptions jsonOptions)
{
	var sagaBinPath = Path.Combine(sampleRoot, "SAGA.BIN");
	if (!File.Exists(sagaBinPath))
	{
		return TransitionPaletteContext.Empty;
	}

	ArchiveInspection? objectMobInspection = null;
	var objectMobPath = Path.Combine(sampleRoot, "OBJECT.MOB");
	if (File.Exists(objectMobPath))
	{
		objectMobInspection = ArchiveInspector.Inspect(objectMobPath);
	}

	var inspection = TransitionFileInspector.Inspect(
		sagaBinPath,
		retainDecompressedPayload: false,
		objectMobInspection);
	var publishedSelectorSection = inspection.Sections.FirstOrDefault(section =>
		section.Manifest.SectionName.Equals("published_0x24_records", StringComparison.OrdinalIgnoreCase));
	if (publishedSelectorSection is null)
	{
		return TransitionPaletteContext.Empty;
	}

	var forgaOutputDir = Path.Combine(outputRoot, "FORGA");
	var forgaManifest = LoadOrGenerateForgaManifest(sampleRoot, outputRoot, jsonOptions, ensurePaletteOutputs: true);
	var sceneCandidatesByName = new Dictionary<string, TransitionScenePaletteCandidate>(StringComparer.OrdinalIgnoreCase);
	var matchedSceneSources = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
	var objectEntrySceneEvidence = new Dictionary<int, Dictionary<string, HashSet<string>>>();
	var transitionSourceFile = Path.GetFileName(sagaBinPath);

	foreach (var selectorEntry in publishedSelectorSection.Entries)
	{
		var selectorName = string.IsNullOrWhiteSpace(selectorEntry.Manifest.TextPreview)
			? $"published selector {selectorEntry.Manifest.Index:D3}"
			: selectorEntry.Manifest.TextPreview;
		var analysis = selectorEntry.Manifest.PublishedSelectorAnalysis;
		if (analysis is null)
		{
			continue;
		}

		foreach (var subscene in analysis.Subscenes)
		{
			if (string.IsNullOrWhiteSpace(subscene.PrimaryForgaSceneName))
			{
				continue;
			}

			var forgaSceneName = subscene.PrimaryForgaSceneName;
			if (!sceneCandidatesByName.ContainsKey(forgaSceneName))
			{
				var palettePath = ResolveForgaScenePalettePathFromManifest(forgaSceneName, forgaManifest, forgaOutputDir);
				sceneCandidatesByName.Add(
					forgaSceneName,
					new TransitionScenePaletteCandidate(
						forgaSceneName,
						palettePath,
						IndexedPalette.LoadFile(palettePath),
						new[] { transitionSourceFile },
						Array.Empty<string>()));
			}

			AddTransitionSceneSource(matchedSceneSources, forgaSceneName, $"{selectorName} -> {subscene.Name}");

			foreach (var sceneRecord in subscene.SceneRecords)
			{
				if (sceneRecord.ObjectMobEntry?.Index is not int objectMobEntryIndex)
				{
					continue;
				}

				if (!objectEntrySceneEvidence.TryGetValue(objectMobEntryIndex, out var sceneEvidenceByName))
				{
					sceneEvidenceByName = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
					objectEntrySceneEvidence.Add(objectMobEntryIndex, sceneEvidenceByName);
				}

				if (!sceneEvidenceByName.TryGetValue(forgaSceneName, out var evidence))
				{
					evidence = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
					sceneEvidenceByName.Add(forgaSceneName, evidence);
				}

				evidence.Add($"{selectorName} -> {subscene.Name}");
			}
		}
	}

	var objectMobEntryPaletteCandidates = new Dictionary<int, IReadOnlyList<TransitionScenePaletteCandidate>>();
	foreach (var sceneEvidenceByEntry in objectEntrySceneEvidence)
	{
		var candidates = sceneEvidenceByEntry.Value
			.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
			.Select(pair => sceneCandidatesByName[pair.Key] with
			{
				Evidence = pair.Value.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray()
			})
			.ToArray();
		objectMobEntryPaletteCandidates.Add(sceneEvidenceByEntry.Key, candidates);
	}

	return new TransitionPaletteContext(
		matchedSceneSources
			.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
			.Select(pair => sceneCandidatesByName[pair.Key] with
			{
				Evidence = pair.Value.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray()
			})
			.ToArray(),
		objectMobEntryPaletteCandidates);
}

static void AddTransitionSceneSource(
	IDictionary<string, HashSet<string>> sceneSources,
	string sceneName,
	string source)
{
	if (!sceneSources.TryGetValue(sceneName, out var sources))
	{
		sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		sceneSources.Add(sceneName, sources);
	}

	sources.Add(source);
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
	var collapsedTransitionPaletteCandidates = CollapseTransitionPaletteCandidates(transitionScenePaletteCandidates);
	var candidates = new List<ExternalPaletteCandidate>(
		collapsedTransitionPaletteCandidates.Count + (menuEventPaletteCandidate is null ? 0 : 1));

	for (var candidateIndex = 0; candidateIndex < collapsedTransitionPaletteCandidates.Count; candidateIndex++)
	{
		var candidate = collapsedTransitionPaletteCandidates[candidateIndex];
		var matchedFiles = candidate.SourceFiles.Count > 0
			? string.Join(", ", candidate.SourceFiles)
			: "transition analysis";
		var evidence = candidate.Evidence.Count > 0
			? $"Runtime fixed-block FORGA scene palette candidate from {string.Join("; ", candidate.Evidence)}."
			: $"Transition-backed scene palette candidate matched in {matchedFiles}.";
		candidates.Add(new ExternalPaletteCandidate(
			candidate.SceneName,
			$"scene_{candidateIndex:D3}_{SanitizeFileComponent(candidate.SceneName)}",
			candidate.PalettePath,
			candidate.Palette,
			evidence));
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

static IReadOnlyList<TransitionScenePaletteCandidate> CollapseTransitionPaletteCandidates(
	IReadOnlyList<TransitionScenePaletteCandidate> transitionScenePaletteCandidates)
{
	if (transitionScenePaletteCandidates.Count <= 1)
	{
		return transitionScenePaletteCandidates;
	}

	return transitionScenePaletteCandidates
		.GroupBy(candidate => candidate.PalettePath, StringComparer.OrdinalIgnoreCase)
		.OrderBy(group => group.Min(candidate => candidate.SceneName), StringComparer.OrdinalIgnoreCase)
		.Select(group =>
		{
			var orderedCandidates = group
				.OrderBy(candidate => candidate.SceneName, StringComparer.OrdinalIgnoreCase)
				.ToArray();
			var representative = orderedCandidates[0];
			var sceneNames = orderedCandidates
				.Select(candidate => candidate.SceneName)
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.OrderBy(sceneName => sceneName, StringComparer.OrdinalIgnoreCase)
				.ToArray();
			var mergedSourceFiles = orderedCandidates
				.SelectMany(candidate => candidate.SourceFiles)
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.OrderBy(sourceFile => sourceFile, StringComparer.OrdinalIgnoreCase)
				.ToArray();
			var mergedEvidence = orderedCandidates
				.SelectMany(candidate => candidate.Evidence)
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.OrderBy(evidence => evidence, StringComparer.OrdinalIgnoreCase)
				.ToArray();
			var mergedSceneName = sceneNames.Length <= 1
				? representative.SceneName
				: $"{sceneNames[0]} (+{sceneNames.Length - 1} scenes)";

			return representative with
			{
				SceneName = mergedSceneName,
				SourceFiles = mergedSourceFiles,
				Evidence = mergedEvidence
			};
		})
		.ToArray();
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


