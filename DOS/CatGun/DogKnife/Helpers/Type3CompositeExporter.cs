using DogKnife.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DogKnife.Helpers;

internal static class Type3CompositeExporter
{
	private const int RuntimeStride = 0x180;
	private const int OptionsBaseWidth = 122;
	private const int OptionsBaseHeight = 80;
	private const int OptionsOverlayWidth = 104;
	private const int OptionsOverlayHeight = 50;
	private const int Rx7BaseWidth = 26;
	private const int Rx7BaseHeight = 35;
	private const int Rx7OverlayWidth = 26;
	private const int Rx7OverlayHeight = 5;
	private const int BlobsCanvasWidth = 25;
	private const int BlobsCanvasHeight = 22;
	private const int BlobsBaseWidth = 23;
	private const int BlobsBaseHeight = 20;
	private const int BlobsOverlayWidth = 25;
	private const int BlobsOverlayHeight = 3;
	private const int BlobsBaseInsetX = 1;
	private const int BlobsBaseInsetY = 1;
	private const int BlobsBaseStartIndex = 8;
	private const int BlobsBaseCount = 160;
	private const int BlobsOverlayCount = 8;
	private const int SupportedBaseWidth = 44;
	private const int SupportedBaseHeight = 32;
	private const int CanvasTopPadding = 7;
	private const int FullCompositeHeight = 48;
	private const int OverlayWidth = 34;
	private const int OverlayHeight = 18;
	private const int OverlayBaseIndex = 0x10;
	private const int OverlayOffsetX = 5;
	private const int OverlayTopY = 0x11;
	private const int PilotOffsetX = 7;
	private const int PilotOffsetY = -7;
	private const int AccentOffsetX = 12;
	private const int AccentOffsetY = -6;
	private static readonly short[] PhaseYOffsetTable = [0, -1, -2, -4, -5, -6, -6, -5, -4, -2, -1, 0];
	private static readonly byte[] Type3SelectorTable = [0, 1, 2, 3, 4, 5, 5, 4, 3, 2, 1, 0];
	private static readonly byte[] PilotRowLookup = [0, 1];

	public static bool SupportsResource(string resourceName)
	{
		return resourceName is "PLAYER" or "RX7_FRAMES" or "OPTIONS" or "BLOBS";
	}

	public static Type3CompositeExportResult Export(CatGunDat dat, string resourceName, string outputRoot)
	{
		if (string.Equals(resourceName, "OPTIONS", StringComparison.Ordinal))
		{
			return ExportOptions(dat, outputRoot);
		}

		if (string.Equals(resourceName, "RX7_FRAMES", StringComparison.Ordinal))
		{
			return ExportRx7Frames(dat, outputRoot);
		}

		if (string.Equals(resourceName, "BLOBS", StringComparison.Ordinal))
		{
			return ExportBlobs(dat, outputRoot);
		}

		if (!string.Equals(resourceName, "PLAYER", StringComparison.Ordinal))
		{
			throw new NotSupportedException("Exact type-3 composite export is currently grounded only for PLAYER via FUN_00038130 and the same-resource base-plus-remap matrices for RX7_FRAMES, OPTIONS, and BLOBS.");
		}

		DatResourceEntry resource = dat.Resources.SingleOrDefault(candidate =>
			string.Equals(candidate.Name, resourceName, StringComparison.Ordinal))
			?? throw new InvalidDataException($"{resourceName} resource was not found in the DAT resource table.");

		DatPayloadGroup payloadGroup = dat.PayloadGroups.SingleOrDefault(group => group.StartOffset == resource.Pointer04)
			?? throw new InvalidDataException($"{resourceName} payload group was not found for resource field +0x04.");

		List<DatPayloadBlock30> baseBlocks = payloadGroup.Blocks
			.Where(block =>
				block.LoaderType == 1 &&
				block.Index is >= 0 and <= 15 &&
				block.Value08 == SupportedBaseWidth &&
				block.Value0C == SupportedBaseHeight)
			.OrderBy(block => block.Index)
			.ToList();

		if (baseBlocks.Count != 16)
		{
			throw new InvalidDataException(
				$"Expected 16 direct PLAYER base blocks (0..15) at {SupportedBaseWidth}x{SupportedBaseHeight}, found {baseBlocks.Count}.");
		}

		Dictionary<int, DatPayloadBlock30> overlayBlocks = payloadGroup.Blocks
			.Where(block =>
				block.LoaderType == 3 &&
				block.Index is >= 16 and <= 21 &&
				block.Value08 == OverlayWidth &&
				block.Value0C == OverlayHeight)
			.ToDictionary(block => block.Index);

		if (overlayBlocks.Count != 6)
		{
			throw new InvalidDataException(
				$"Expected 6 shared PLAYER remap blocks (16..21) at {OverlayWidth}x{OverlayHeight}, found {overlayBlocks.Count}.");
		}

		ReadOnlySpan<byte> bytes = dat.RawBytes.Span;
		string familyRoot = Path.Combine(Path.GetFullPath(outputRoot), resourceName, "type3_composite");
		string defaultRoot = Path.Combine(familyRoot, DatPaletteHelper.DefaultDirectoryName);
		string baseDirectory = Path.Combine(defaultRoot, "base_blocks");
		string phaseDirectory = Path.Combine(defaultRoot, "phase_matrix");
		string specialDirectory = Path.Combine(defaultRoot, "fun38130_special");
		string cycleDirectory = Path.Combine(defaultRoot, "fun38130_cycle");
		Directory.CreateDirectory(baseDirectory);
		Directory.CreateDirectory(phaseDirectory);
		Directory.CreateDirectory(specialDirectory);
		Directory.CreateDirectory(cycleDirectory);
		DatPaletteHelper.TryCreateContext(dat, out DatPaletteContext? paletteContext, out string? paletteFailureReason);
		Rgba32[]? defaultPalette = paletteContext?.DefaultPalette;

		if (paletteContext is not null)
		{
			DatPaletteHelper.ExportPaletteBankImages(familyRoot, paletteContext);

			foreach (ExportPaletteVariant variant in paletteContext.Variants)
			{
				Directory.CreateDirectory(Path.Combine(familyRoot, variant.DirectoryName, "base_blocks"));
				Directory.CreateDirectory(Path.Combine(familyRoot, variant.DirectoryName, "phase_matrix"));
				Directory.CreateDirectory(Path.Combine(familyRoot, variant.DirectoryName, "fun38130_special"));
				Directory.CreateDirectory(Path.Combine(familyRoot, variant.DirectoryName, "fun38130_cycle"));
			}
		}

		Dictionary<int, RenderedType1Image> pilotBlocks = RenderDirectBlocks(dat, bytes, "PILOT", block => block.LoaderType == 1 && block.Index is >= 0 and <= 17);
		Dictionary<int, RenderedType1Image> accentBlocks = new();
		foreach (DatPayloadBlock30 block in payloadGroup.Blocks.Where(block => block.LoaderType == 1 && block.Index is >= 22 and <= 28))
		{
			accentBlocks.Add(block.Index, Type1RenderedExporter.RenderBlock(bytes, block));
		}

		List<Type3CompositeBaseSummary> baseSummaries = new(baseBlocks.Count);
		Dictionary<int, RenderedBaseBlock> renderedBases = new();

		foreach (DatPayloadBlock30 baseBlock in baseBlocks)
		{
			RenderedType1Image image = Type1RenderedExporter.RenderBlock(bytes, baseBlock);
			SurfaceBounds bounds = new(0, 0);
			ValidateBounds(image, bounds, baseBlock);

			string basePath = Path.Combine(baseDirectory, $"block_{baseBlock.Index:D2}_{baseBlock.Value08}x{baseBlock.Value0C}.png");
			SaveImage(basePath, image.Pixels, bounds.Left, bounds.Top, baseBlock.Value08, baseBlock.Value0C, defaultPalette);

			if (paletteContext is not null)
			{
				foreach (ExportPaletteVariant variant in paletteContext.Variants)
				{
					string variantBasePath = Path.Combine(
						familyRoot,
						variant.DirectoryName,
						"base_blocks",
						$"block_{baseBlock.Index:D2}_{baseBlock.Value08}x{baseBlock.Value0C}.png");
					SaveImage(variantBasePath, image.Pixels, bounds.Left, bounds.Top, baseBlock.Value08, baseBlock.Value0C, paletteContext.Banks[variant.BankIndex]);
				}
			}

			renderedBases.Add(baseBlock.Index, new RenderedBaseBlock(baseBlock, image, bounds));
			baseSummaries.Add(new Type3CompositeBaseSummary(baseBlock.Index, basePath, bounds.Left, bounds.Top));
		}

		List<Type3CompositePhaseSummary> phaseSummaries = new(renderedBases.Count * PhaseYOffsetTable.Length);
		int specialVariantCount = 0;
		int cycleVariantCount = 0;

		foreach ((int baseIndex, RenderedBaseBlock renderedBase) in renderedBases)
		{
			string basePhaseDirectory = Path.Combine(phaseDirectory, $"base_{baseIndex:D2}");
			string baseSpecialDirectory = Path.Combine(specialDirectory, $"base_{baseIndex:D2}");
			string baseCycleDirectory = Path.Combine(cycleDirectory, $"base_{baseIndex:D2}");
			Directory.CreateDirectory(basePhaseDirectory);
			Directory.CreateDirectory(baseSpecialDirectory);
			Directory.CreateDirectory(baseCycleDirectory);

			if (paletteContext is not null)
			{
				foreach (ExportPaletteVariant variant in paletteContext.Variants)
				{
					Directory.CreateDirectory(Path.Combine(familyRoot, variant.DirectoryName, "phase_matrix", $"base_{baseIndex:D2}"));
					Directory.CreateDirectory(Path.Combine(familyRoot, variant.DirectoryName, "fun38130_special", $"base_{baseIndex:D2}"));
					Directory.CreateDirectory(Path.Combine(familyRoot, variant.DirectoryName, "fun38130_cycle", $"base_{baseIndex:D2}"));
				}
			}

			int directionIndex = baseIndex / 8;
			byte pilotRow = PilotRowLookup[directionIndex];

			for (int phaseIndex = 0; phaseIndex < PhaseYOffsetTable.Length; phaseIndex++)
			{
				byte selector = Type3SelectorTable[phaseIndex];
				int overlayIndex = OverlayBaseIndex + selector;
				DatPayloadBlock30 overlayBlock = overlayBlocks[overlayIndex];
				int relativeYOffset = OverlayTopY - PhaseYOffsetTable[phaseIndex];
				Type3CompositeImage composite = ApplyOverlay(bytes, renderedBase, overlayBlock, OverlayOffsetX, relativeYOffset);

				int outputHeight = Math.Max(renderedBase.Block.Value0C, relativeYOffset + overlayBlock.Value0C);
				string compositePath = Path.Combine(
					basePhaseDirectory,
					$"phase_{phaseIndex:D2}_overlay_{overlayIndex:D2}.png");
				SaveImage(
					compositePath,
					composite.Pixels,
					renderedBase.Bounds.Left,
					renderedBase.Bounds.Top,
					renderedBase.Block.Value08,
					outputHeight,
					defaultPalette);

				if (paletteContext is not null)
				{
					foreach (ExportPaletteVariant variant in paletteContext.Variants)
					{
						string variantCompositePath = Path.Combine(
							familyRoot,
							variant.DirectoryName,
							"phase_matrix",
							$"base_{baseIndex:D2}",
							$"phase_{phaseIndex:D2}_overlay_{overlayIndex:D2}.png");
						SaveImage(
							variantCompositePath,
							composite.Pixels,
							renderedBase.Bounds.Left,
							renderedBase.Bounds.Top,
							renderedBase.Block.Value08,
							outputHeight,
							paletteContext.Banks[variant.BankIndex]);
					}
				}

				phaseSummaries.Add(new Type3CompositePhaseSummary(
					BaseBlockIndex: baseIndex,
					PhaseIndex: phaseIndex,
					Selector: selector,
					OverlayBlockIndex: overlayIndex,
					RelativeYOffset: relativeYOffset,
					ChangedPixelCount: composite.ChangedPixelCount,
					OutputPath: compositePath));

				for (int pilotVariant = 0; pilotVariant < 8; pilotVariant++)
				{
					int pilotBlockIndex = (pilotRow * 9) + pilotVariant;
					RenderedType1Image pilotImage = pilotBlocks[pilotBlockIndex];
					IReadOnlyDictionary<int, byte> finalPixels = ComposeFullFrame(composite.Pixels, pilotImage, PilotOffsetX, PilotOffsetY, null, 0, 0);
					string specialPath = Path.Combine(
						baseSpecialDirectory,
						$"phase_{phaseIndex:D2}_pilot_{pilotBlockIndex:D2}.png");
					SaveImage(specialPath, finalPixels, 0, 0, SupportedBaseWidth, FullCompositeHeight, defaultPalette);

					if (paletteContext is not null)
					{
						foreach (ExportPaletteVariant variant in paletteContext.Variants)
						{
							string variantSpecialPath = Path.Combine(
								familyRoot,
								variant.DirectoryName,
								"fun38130_special",
								$"base_{baseIndex:D2}",
								$"phase_{phaseIndex:D2}_pilot_{pilotBlockIndex:D2}.png");
							SaveImage(variantSpecialPath, finalPixels, 0, 0, SupportedBaseWidth, FullCompositeHeight, paletteContext.Banks[variant.BankIndex]);
						}
					}

					specialVariantCount++;
				}

				int fixedPilotBlockIndex = (pilotRow * 9) + 8;
				RenderedType1Image fixedPilotImage = pilotBlocks[fixedPilotBlockIndex];
				for (int accentState = 0; accentState <= 6; accentState++)
				{
					int accentBlockIndex = 0x16 + accentState;
					RenderedType1Image accentImage = accentBlocks[accentBlockIndex];
					IReadOnlyDictionary<int, byte> finalPixels = ComposeFullFrame(
						composite.Pixels,
						fixedPilotImage,
						PilotOffsetX,
						PilotOffsetY,
						accentImage,
						AccentOffsetX,
						AccentOffsetY);
					string cyclePath = Path.Combine(
						baseCycleDirectory,
						$"phase_{phaseIndex:D2}_state_{accentState:D2}_pilot_{fixedPilotBlockIndex:D2}_player_{accentBlockIndex:D2}.png");
					SaveImage(cyclePath, finalPixels, 0, 0, SupportedBaseWidth, FullCompositeHeight, defaultPalette);

					if (paletteContext is not null)
					{
						foreach (ExportPaletteVariant variant in paletteContext.Variants)
						{
							string variantCyclePath = Path.Combine(
								familyRoot,
								variant.DirectoryName,
								"fun38130_cycle",
								$"base_{baseIndex:D2}",
								$"phase_{phaseIndex:D2}_state_{accentState:D2}_pilot_{fixedPilotBlockIndex:D2}_player_{accentBlockIndex:D2}.png");
							SaveImage(variantCyclePath, finalPixels, 0, 0, SupportedBaseWidth, FullCompositeHeight, paletteContext.Banks[variant.BankIndex]);
						}
					}

					cycleVariantCount++;
				}
			}
		}

		WriteMetadata(
			Path.Combine(familyRoot, "metadata.txt"),
			dat,
			resource,
			payloadGroup,
			paletteContext,
			paletteFailureReason,
			baseSummaries,
			phaseSummaries,
			specialVariantCount,
			cycleVariantCount);

		return new Type3CompositeExportResult(
			ResourceName: resourceName,
			OutputDirectory: familyRoot,
			BaseBlockCount: baseSummaries.Count,
			PhaseCount: PhaseYOffsetTable.Length,
			CompositeCount: phaseSummaries.Count,
			SpecialVariantCount: specialVariantCount,
			CycleVariantCount: cycleVariantCount,
			DefaultPaletteSummary: paletteContext?.DefaultPaletteSummary ?? $"{DatPaletteHelper.DefaultDirectoryName}/ falls back to grayscale; palette data unavailable.",
			ExportedPaletteVariants: paletteContext?.Variants.Select(variant => $"{variant.DirectoryName}=bank{variant.BankIndex:D2}").ToArray() ?? []);
	}

	private static Type3CompositeExportResult ExportRx7Frames(CatGunDat dat, string outputRoot)
	{
		DatResourceEntry resource = dat.Resources.SingleOrDefault(candidate =>
			string.Equals(candidate.Name, "RX7_FRAMES", StringComparison.Ordinal))
			?? throw new InvalidDataException("RX7_FRAMES resource was not found in the DAT resource table.");

		DatPayloadGroup payloadGroup = dat.PayloadGroups.SingleOrDefault(group => group.StartOffset == resource.Pointer04)
			?? throw new InvalidDataException("RX7_FRAMES payload group was not found for resource field +0x04.");

		List<DatPayloadBlock30> baseBlocks = payloadGroup.Blocks
			.Where(block =>
				block.LoaderType == 1 &&
				block.Index is >= 0 and <= 35 &&
				block.Value08 == Rx7BaseWidth &&
				block.Value0C == Rx7BaseHeight)
			.OrderBy(block => block.Index)
			.ToList();

		if (baseBlocks.Count != 36)
		{
			throw new InvalidDataException(
				$"Expected 36 RX7_FRAMES direct base blocks (0..35) at {Rx7BaseWidth}x{Rx7BaseHeight}, found {baseBlocks.Count}.");
		}

		List<DatPayloadBlock30> overlayBlocks = payloadGroup.Blocks
			.Where(block =>
				block.LoaderType == 3 &&
				block.Index is >= 36 and <= 44 &&
				block.Value08 == Rx7OverlayWidth &&
				block.Value0C == Rx7OverlayHeight)
			.OrderBy(block => block.Index)
			.ToList();

		if (overlayBlocks.Count != 9)
		{
			throw new InvalidDataException(
				$"Expected 9 RX7_FRAMES remap blocks (36..44) at {Rx7OverlayWidth}x{Rx7OverlayHeight}, found {overlayBlocks.Count}.");
		}

		ReadOnlySpan<byte> bytes = dat.RawBytes.Span;
		string familyRoot = Path.Combine(Path.GetFullPath(outputRoot), resource.Name, "type3_composite");
		string defaultRoot = Path.Combine(familyRoot, DatPaletteHelper.DefaultDirectoryName);
		string baseDirectory = Path.Combine(defaultRoot, "base_blocks");
		string overlayDirectory = Path.Combine(defaultRoot, "overlay_matrix");
		Directory.CreateDirectory(baseDirectory);
		Directory.CreateDirectory(overlayDirectory);

		DatPaletteHelper.TryCreateContext(dat, out DatPaletteContext? paletteContext, out string? paletteFailureReason);
		Rgba32[]? defaultPalette = paletteContext?.DefaultPalette;

		if (paletteContext is not null)
		{
			DatPaletteHelper.ExportPaletteBankImages(familyRoot, paletteContext);

			foreach (ExportPaletteVariant variant in paletteContext.Variants)
			{
				Directory.CreateDirectory(Path.Combine(familyRoot, variant.DirectoryName, "base_blocks"));
				Directory.CreateDirectory(Path.Combine(familyRoot, variant.DirectoryName, "overlay_matrix"));
			}
		}

		List<Type3CompositeBaseSummary> baseSummaries = new(baseBlocks.Count);
		Dictionary<int, RenderedBaseBlock> renderedBases = new();

		foreach (DatPayloadBlock30 baseBlock in baseBlocks)
		{
			RenderedType1Image image = Type1RenderedExporter.RenderBlock(bytes, baseBlock);
			SurfaceBounds bounds = new(0, 0);
			ValidateBounds(image, bounds, baseBlock);

			string basePath = Path.Combine(baseDirectory, $"block_{baseBlock.Index:D2}_{baseBlock.Value08}x{baseBlock.Value0C}.png");
			SaveImage(basePath, image.Pixels, bounds.Left, bounds.Top, baseBlock.Value08, baseBlock.Value0C, defaultPalette);

			if (paletteContext is not null)
			{
				foreach (ExportPaletteVariant variant in paletteContext.Variants)
				{
					string variantBasePath = Path.Combine(
						familyRoot,
						variant.DirectoryName,
						"base_blocks",
						$"block_{baseBlock.Index:D2}_{baseBlock.Value08}x{baseBlock.Value0C}.png");
					SaveImage(variantBasePath, image.Pixels, bounds.Left, bounds.Top, baseBlock.Value08, baseBlock.Value0C, paletteContext.Banks[variant.BankIndex]);
				}
			}

			renderedBases.Add(baseBlock.Index, new RenderedBaseBlock(baseBlock, image, bounds));
			baseSummaries.Add(new Type3CompositeBaseSummary(baseBlock.Index, basePath, bounds.Left, bounds.Top));
		}

		List<Type3OverlayMatrixSummary> overlaySummaries = new(renderedBases.Count * overlayBlocks.Count);
		foreach ((int baseIndex, RenderedBaseBlock renderedBase) in renderedBases)
		{
			string baseOverlayDirectory = Path.Combine(overlayDirectory, $"base_{baseIndex:D2}");
			Directory.CreateDirectory(baseOverlayDirectory);

			if (paletteContext is not null)
			{
				foreach (ExportPaletteVariant variant in paletteContext.Variants)
				{
					Directory.CreateDirectory(Path.Combine(familyRoot, variant.DirectoryName, "overlay_matrix", $"base_{baseIndex:D2}"));
				}
			}

			foreach (DatPayloadBlock30 overlayBlock in overlayBlocks)
			{
				Type3CompositeImage composite = ApplyOverlay(bytes, renderedBase, overlayBlock, 0, 0);
				string compositePath = Path.Combine(baseOverlayDirectory, $"overlay_{overlayBlock.Index:D2}.png");
				SaveImage(compositePath, composite.Pixels, renderedBase.Bounds.Left, renderedBase.Bounds.Top, renderedBase.Block.Value08, renderedBase.Block.Value0C, defaultPalette);

				if (paletteContext is not null)
				{
					foreach (ExportPaletteVariant variant in paletteContext.Variants)
					{
						string variantCompositePath = Path.Combine(
							familyRoot,
							variant.DirectoryName,
							"overlay_matrix",
							$"base_{baseIndex:D2}",
							$"overlay_{overlayBlock.Index:D2}.png");
						SaveImage(variantCompositePath, composite.Pixels, renderedBase.Bounds.Left, renderedBase.Bounds.Top, renderedBase.Block.Value08, renderedBase.Block.Value0C, paletteContext.Banks[variant.BankIndex]);
					}
				}

				overlaySummaries.Add(new Type3OverlayMatrixSummary(
					BaseBlockIndex: baseIndex,
					OverlayBlockIndex: overlayBlock.Index,
					ChangedPixelCount: composite.ChangedPixelCount,
					OutputPath: compositePath));
			}
		}

		WriteRx7Metadata(
			Path.Combine(familyRoot, "metadata.txt"),
			dat,
			resource,
			payloadGroup,
			paletteContext,
			paletteFailureReason,
			baseSummaries,
			overlaySummaries);

		return new Type3CompositeExportResult(
			ResourceName: resource.Name,
			OutputDirectory: familyRoot,
			BaseBlockCount: baseSummaries.Count,
			PhaseCount: overlayBlocks.Count,
			CompositeCount: overlaySummaries.Count,
			SpecialVariantCount: 0,
			CycleVariantCount: 0,
			DefaultPaletteSummary: paletteContext?.DefaultPaletteSummary ?? $"{DatPaletteHelper.DefaultDirectoryName}/ falls back to grayscale; palette data unavailable.",
			ExportedPaletteVariants: paletteContext?.Variants.Select(variant => $"{variant.DirectoryName}=bank{variant.BankIndex:D2}").ToArray() ?? []);
	}

	private static Type3CompositeExportResult ExportOptions(CatGunDat dat, string outputRoot)
	{
		DatResourceEntry resource = dat.Resources.SingleOrDefault(candidate =>
			string.Equals(candidate.Name, "OPTIONS", StringComparison.Ordinal))
			?? throw new InvalidDataException("OPTIONS resource was not found in the DAT resource table.");

		DatPayloadGroup payloadGroup = dat.PayloadGroups.SingleOrDefault(group => group.StartOffset == resource.Pointer04)
			?? throw new InvalidDataException("OPTIONS payload group was not found for resource field +0x04.");

		List<DatPayloadBlock30> baseBlocks = payloadGroup.Blocks
			.Where(block =>
				block.LoaderType == 1 &&
				block.Index == 13 &&
				block.Value08 == OptionsBaseWidth &&
				block.Value0C == OptionsBaseHeight)
			.OrderBy(block => block.Index)
			.ToList();

		if (baseBlocks.Count != 1)
		{
			throw new InvalidDataException(
				$"Expected OPTIONS direct base block 13 at {OptionsBaseWidth}x{OptionsBaseHeight}, found {baseBlocks.Count} matching blocks.");
		}

		List<DatPayloadBlock30> overlayBlocks = payloadGroup.Blocks
			.Where(block =>
				block.LoaderType == 3 &&
				block.Index == 0 &&
				block.Value08 == OptionsOverlayWidth &&
				block.Value0C == OptionsOverlayHeight)
			.OrderBy(block => block.Index)
			.ToList();

		if (overlayBlocks.Count != 1)
		{
			throw new InvalidDataException(
				$"Expected OPTIONS remap block 0 at {OptionsOverlayWidth}x{OptionsOverlayHeight}, found {overlayBlocks.Count} matching blocks.");
		}

		ReadOnlySpan<byte> bytes = dat.RawBytes.Span;
		string familyRoot = Path.Combine(Path.GetFullPath(outputRoot), resource.Name, "type3_composite");
		string defaultRoot = Path.Combine(familyRoot, DatPaletteHelper.DefaultDirectoryName);
		string baseDirectory = Path.Combine(defaultRoot, "base_blocks");
		string overlayDirectory = Path.Combine(defaultRoot, "overlay_matrix");
		Directory.CreateDirectory(baseDirectory);
		Directory.CreateDirectory(overlayDirectory);

		DatPaletteHelper.TryCreateContext(dat, out DatPaletteContext? paletteContext, out string? paletteFailureReason);
		Rgba32[]? defaultPalette = paletteContext?.DefaultPalette;

		if (paletteContext is not null)
		{
			DatPaletteHelper.ExportPaletteBankImages(familyRoot, paletteContext);

			foreach (ExportPaletteVariant variant in paletteContext.Variants)
			{
				Directory.CreateDirectory(Path.Combine(familyRoot, variant.DirectoryName, "base_blocks"));
				Directory.CreateDirectory(Path.Combine(familyRoot, variant.DirectoryName, "overlay_matrix"));
			}
		}

		List<Type3CompositeBaseSummary> baseSummaries = new(baseBlocks.Count);
		Dictionary<int, RenderedBaseBlock> renderedBases = new();

		foreach (DatPayloadBlock30 baseBlock in baseBlocks)
		{
			RenderedType1Image image = Type1RenderedExporter.RenderBlock(bytes, baseBlock);
			SurfaceBounds bounds = new(0, 0);
			ValidateBounds(image, bounds, baseBlock);

			string basePath = Path.Combine(baseDirectory, $"block_{baseBlock.Index:D2}_{baseBlock.Value08}x{baseBlock.Value0C}.png");
			SaveImage(basePath, image.Pixels, bounds.Left, bounds.Top, baseBlock.Value08, baseBlock.Value0C, defaultPalette);

			if (paletteContext is not null)
			{
				foreach (ExportPaletteVariant variant in paletteContext.Variants)
				{
					string variantBasePath = Path.Combine(
						familyRoot,
						variant.DirectoryName,
						"base_blocks",
						$"block_{baseBlock.Index:D2}_{baseBlock.Value08}x{baseBlock.Value0C}.png");
					SaveImage(variantBasePath, image.Pixels, bounds.Left, bounds.Top, baseBlock.Value08, baseBlock.Value0C, paletteContext.Banks[variant.BankIndex]);
				}
			}

			renderedBases.Add(baseBlock.Index, new RenderedBaseBlock(baseBlock, image, bounds));
			baseSummaries.Add(new Type3CompositeBaseSummary(baseBlock.Index, basePath, bounds.Left, bounds.Top));
		}

		List<Type3OverlayMatrixSummary> overlaySummaries = new(renderedBases.Count * overlayBlocks.Count);
		foreach ((int baseIndex, RenderedBaseBlock renderedBase) in renderedBases)
		{
			string baseOverlayDirectory = Path.Combine(overlayDirectory, $"base_{baseIndex:D2}");
			Directory.CreateDirectory(baseOverlayDirectory);

			if (paletteContext is not null)
			{
				foreach (ExportPaletteVariant variant in paletteContext.Variants)
				{
					Directory.CreateDirectory(Path.Combine(familyRoot, variant.DirectoryName, "overlay_matrix", $"base_{baseIndex:D2}"));
				}
			}

			foreach (DatPayloadBlock30 overlayBlock in overlayBlocks)
			{
				Type3CompositeImage composite = ApplyOverlay(bytes, renderedBase, overlayBlock, 0, 0);
				string compositePath = Path.Combine(baseOverlayDirectory, $"overlay_{overlayBlock.Index:D2}.png");
				SaveImage(compositePath, composite.Pixels, renderedBase.Bounds.Left, renderedBase.Bounds.Top, renderedBase.Block.Value08, renderedBase.Block.Value0C, defaultPalette);

				if (paletteContext is not null)
				{
					foreach (ExportPaletteVariant variant in paletteContext.Variants)
					{
						string variantCompositePath = Path.Combine(
							familyRoot,
							variant.DirectoryName,
							"overlay_matrix",
							$"base_{baseIndex:D2}",
							$"overlay_{overlayBlock.Index:D2}.png");
						SaveImage(variantCompositePath, composite.Pixels, renderedBase.Bounds.Left, renderedBase.Bounds.Top, renderedBase.Block.Value08, renderedBase.Block.Value0C, paletteContext.Banks[variant.BankIndex]);
					}
				}

				overlaySummaries.Add(new Type3OverlayMatrixSummary(
					BaseBlockIndex: baseIndex,
					OverlayBlockIndex: overlayBlock.Index,
					ChangedPixelCount: composite.ChangedPixelCount,
					OutputPath: compositePath));
			}
		}

		WriteSimpleMatrixMetadata(
			Path.Combine(familyRoot, "metadata.txt"),
			dat,
			resource,
			payloadGroup,
			paletteContext,
			paletteFailureReason,
			baseSummaries,
			overlaySummaries,
			"OPTIONS exposes one large direct menu block plus one same-resource type-3 remap block, so this export writes the exact base-plus-remap composite without claiming any higher-level menu state sequencing.",
			"Placement note: each overlay is applied at the same top-left origin as the direct base image.");

		return new Type3CompositeExportResult(
			ResourceName: resource.Name,
			OutputDirectory: familyRoot,
			BaseBlockCount: baseSummaries.Count,
			PhaseCount: overlayBlocks.Count,
			CompositeCount: overlaySummaries.Count,
			SpecialVariantCount: 0,
			CycleVariantCount: 0,
			DefaultPaletteSummary: paletteContext?.DefaultPaletteSummary ?? $"{DatPaletteHelper.DefaultDirectoryName}/ falls back to grayscale; palette data unavailable.",
			ExportedPaletteVariants: paletteContext?.Variants.Select(variant => $"{variant.DirectoryName}=bank{variant.BankIndex:D2}").ToArray() ?? []);
	}

	private static Type3CompositeExportResult ExportBlobs(CatGunDat dat, string outputRoot)
	{
		DatResourceEntry resource = dat.Resources.SingleOrDefault(candidate =>
			string.Equals(candidate.Name, "BLOBS", StringComparison.Ordinal))
			?? throw new InvalidDataException("BLOBS resource was not found in the DAT resource table.");

		DatPayloadGroup payloadGroup = dat.PayloadGroups.SingleOrDefault(group => group.StartOffset == resource.Pointer04)
			?? throw new InvalidDataException("BLOBS payload group was not found for resource field +0x04.");

		List<DatPayloadBlock30> baseBlocks = payloadGroup.Blocks
			.Where(block =>
				block.LoaderType == 1 &&
				block.Index is >= BlobsBaseStartIndex and < BlobsBaseStartIndex + BlobsBaseCount &&
				block.Value08 == BlobsBaseWidth &&
				block.Value0C == BlobsBaseHeight)
			.OrderBy(block => block.Index)
			.ToList();

		if (baseBlocks.Count != BlobsBaseCount)
		{
			throw new InvalidDataException(
				$"Expected {BlobsBaseCount} BLOBS direct base blocks ({BlobsBaseStartIndex}..{BlobsBaseStartIndex + BlobsBaseCount - 1}) at {BlobsBaseWidth}x{BlobsBaseHeight}, found {baseBlocks.Count}.");
		}

		List<DatPayloadBlock30> overlayBlocks = payloadGroup.Blocks
			.Where(block =>
				block.LoaderType == 3 &&
				block.Index is >= 0 and < BlobsOverlayCount &&
				block.Value08 == BlobsOverlayWidth &&
				block.Value0C == BlobsOverlayHeight)
			.OrderBy(block => block.Index)
			.ToList();

		if (overlayBlocks.Count != BlobsOverlayCount)
		{
			throw new InvalidDataException(
				$"Expected {BlobsOverlayCount} BLOBS remap blocks (0..{BlobsOverlayCount - 1}) at {BlobsOverlayWidth}x{BlobsOverlayHeight}, found {overlayBlocks.Count}.");
		}

		ReadOnlySpan<byte> bytes = dat.RawBytes.Span;
		string familyRoot = Path.Combine(Path.GetFullPath(outputRoot), resource.Name, "type3_composite");
		string defaultRoot = Path.Combine(familyRoot, DatPaletteHelper.DefaultDirectoryName);
		string baseDirectory = Path.Combine(defaultRoot, "base_blocks");
		string overlayDirectory = Path.Combine(defaultRoot, "overlay_matrix");
		Directory.CreateDirectory(baseDirectory);
		Directory.CreateDirectory(overlayDirectory);

		DatPaletteHelper.TryCreateContext(dat, out DatPaletteContext? paletteContext, out string? paletteFailureReason);
		Rgba32[]? defaultPalette = paletteContext?.DefaultPalette;

		if (paletteContext is not null)
		{
			DatPaletteHelper.ExportPaletteBankImages(familyRoot, paletteContext);

			foreach (ExportPaletteVariant variant in paletteContext.Variants)
			{
				Directory.CreateDirectory(Path.Combine(familyRoot, variant.DirectoryName, "base_blocks"));
				Directory.CreateDirectory(Path.Combine(familyRoot, variant.DirectoryName, "overlay_matrix"));
			}
		}

		List<Type3CompositeBaseSummary> baseSummaries = new(baseBlocks.Count);
		Dictionary<int, RenderedBaseBlock> renderedBases = new();

		foreach (DatPayloadBlock30 baseBlock in baseBlocks)
		{
			RenderedType1Image image = Type1RenderedExporter.RenderBlock(bytes, baseBlock);
			ValidateBounds(image, new SurfaceBounds(0, 0), baseBlock);

			string basePath = Path.Combine(baseDirectory, $"block_{baseBlock.Index:D2}_{baseBlock.Value08}x{baseBlock.Value0C}.png");
			SaveImage(basePath, image.Pixels, 0, 0, baseBlock.Value08, baseBlock.Value0C, defaultPalette);

			if (paletteContext is not null)
			{
				foreach (ExportPaletteVariant variant in paletteContext.Variants)
				{
					string variantBasePath = Path.Combine(
						familyRoot,
						variant.DirectoryName,
						"base_blocks",
						$"block_{baseBlock.Index:D2}_{baseBlock.Value08}x{baseBlock.Value0C}.png");
					SaveImage(variantBasePath, image.Pixels, 0, 0, baseBlock.Value08, baseBlock.Value0C, paletteContext.Banks[variant.BankIndex]);
				}
			}

			renderedBases.Add(baseBlock.Index, new RenderedBaseBlock(baseBlock, image, new SurfaceBounds(BlobsBaseInsetX, BlobsBaseInsetY)));
			baseSummaries.Add(new Type3CompositeBaseSummary(baseBlock.Index, basePath, BlobsBaseInsetX, BlobsBaseInsetY));
		}

		List<Type3OverlayMatrixSummary> overlaySummaries = new(renderedBases.Count * overlayBlocks.Count);
		foreach ((int baseIndex, RenderedBaseBlock renderedBase) in renderedBases)
		{
			string baseOverlayDirectory = Path.Combine(overlayDirectory, $"base_{baseIndex:D2}");
			Directory.CreateDirectory(baseOverlayDirectory);

			if (paletteContext is not null)
			{
				foreach (ExportPaletteVariant variant in paletteContext.Variants)
				{
					Directory.CreateDirectory(Path.Combine(familyRoot, variant.DirectoryName, "overlay_matrix", $"base_{baseIndex:D2}"));
				}
			}

			IReadOnlyDictionary<int, byte> baseCanvas = TranslateImage(renderedBase.Image.Pixels, BlobsBaseInsetX, BlobsBaseInsetY);
			foreach (DatPayloadBlock30 overlayBlock in overlayBlocks)
			{
				Type3CompositeImage composite = ApplyOverlayToPixels(bytes, baseCanvas, overlayBlock, 0, 0);
				string compositePath = Path.Combine(baseOverlayDirectory, $"overlay_{overlayBlock.Index:D2}.png");
				SaveImage(compositePath, composite.Pixels, 0, 0, BlobsCanvasWidth, BlobsCanvasHeight, defaultPalette);

				if (paletteContext is not null)
				{
					foreach (ExportPaletteVariant variant in paletteContext.Variants)
					{
						string variantCompositePath = Path.Combine(
							familyRoot,
							variant.DirectoryName,
							"overlay_matrix",
							$"base_{baseIndex:D2}",
							$"overlay_{overlayBlock.Index:D2}.png");
						SaveImage(variantCompositePath, composite.Pixels, 0, 0, BlobsCanvasWidth, BlobsCanvasHeight, paletteContext.Banks[variant.BankIndex]);
					}
				}

				overlaySummaries.Add(new Type3OverlayMatrixSummary(
					BaseBlockIndex: baseIndex,
					OverlayBlockIndex: overlayBlock.Index,
					ChangedPixelCount: composite.ChangedPixelCount,
					OutputPath: compositePath));
			}
		}

		WriteSimpleMatrixMetadata(
			Path.Combine(familyRoot, "metadata.txt"),
			dat,
			resource,
			payloadGroup,
			paletteContext,
			paletteFailureReason,
			baseSummaries,
			overlaySummaries,
			"BLOBS state 0x0B reaches the composite renderer at 0x3A2F0, which queues one same-resource type-1 block and one same-resource type-3 remap block per visible sprite. The recovered visibility helper FUN_0003F300() clips BLOBS against a fixed 25x22 canvas, so this export writes the exact base-plus-remap matrix on that per-sprite canvas without claiming higher-level slot/state sequencing.",
			$"Placement note: each 23x20 direct BLOBS base is inset at ({BlobsBaseInsetX},{BlobsBaseInsetY}) inside the proven 25x22 visibility canvas, and each type-3 remap is applied at the runtime canvas origin using its recovered stream offsets.");

		return new Type3CompositeExportResult(
			ResourceName: resource.Name,
			OutputDirectory: familyRoot,
			BaseBlockCount: baseSummaries.Count,
			PhaseCount: overlayBlocks.Count,
			CompositeCount: overlaySummaries.Count,
			SpecialVariantCount: 0,
			CycleVariantCount: 0,
			DefaultPaletteSummary: paletteContext?.DefaultPaletteSummary ?? $"{DatPaletteHelper.DefaultDirectoryName}/ falls back to grayscale; palette data unavailable.",
			ExportedPaletteVariants: paletteContext?.Variants.Select(variant => $"{variant.DirectoryName}=bank{variant.BankIndex:D2}").ToArray() ?? []);
	}

	private static Dictionary<int, RenderedType1Image> RenderDirectBlocks(
		CatGunDat dat,
		ReadOnlySpan<byte> bytes,
		string resourceName,
		Func<DatPayloadBlock30, bool> predicate)
	{
		DatResourceEntry resource = dat.Resources.Single(candidate =>
			string.Equals(candidate.Name, resourceName, StringComparison.Ordinal));

		DatPayloadGroup payloadGroup = dat.PayloadGroups.Single(group => group.StartOffset == resource.Pointer04);
		Dictionary<int, RenderedType1Image> renderedBlocks = new();
		foreach (DatPayloadBlock30 block in payloadGroup.Blocks.Where(predicate))
		{
			renderedBlocks.Add(block.Index, Type1RenderedExporter.RenderBlock(bytes, block));
		}

		return renderedBlocks;
	}

	private static Type3CompositeImage ApplyOverlay(
		ReadOnlySpan<byte> bytes,
		RenderedBaseBlock renderedBase,
		DatPayloadBlock30 overlayBlock,
		int relativeXOffset,
		int relativeYOffset)
	{
		Type3RemapStream stream = Type3RemapProbeExporter.ParseStream(bytes, overlayBlock.Value24);
		ReadOnlySpan<byte> lookupPage = Type3RemapProbeExporter.GetLookupPage(bytes, overlayBlock.Value28 & ~0xFF);
		Dictionary<int, byte> pixels = new(renderedBase.Image.Pixels);
		int overlayBaseOffset = ((renderedBase.Bounds.Top + relativeYOffset) * RuntimeStride) + renderedBase.Bounds.Left + relativeXOffset;
		int changedPixelCount = 0;

		foreach (Type3RemapSegment segment in stream.Segments)
		{
			int segmentOffset = overlayBaseOffset + segment.RelativeStartOffset;
			for (int position = 0; position < segment.SpanLength; position++)
			{
				int destinationOffset = segmentOffset + position;
				byte currentValue = pixels.TryGetValue(destinationOffset, out byte pixelValue) ? pixelValue : (byte)0;
				byte remappedValue = lookupPage[currentValue];
				if (currentValue != remappedValue)
				{
					changedPixelCount++;
				}

				pixels[destinationOffset] = remappedValue;
			}
		}

		return new Type3CompositeImage(pixels, changedPixelCount);
	}

	private static Type3CompositeImage ApplyOverlayToPixels(
		ReadOnlySpan<byte> bytes,
		IReadOnlyDictionary<int, byte> basePixels,
		DatPayloadBlock30 overlayBlock,
		int overlayXOffset,
		int overlayYOffset)
	{
		Type3RemapStream stream = Type3RemapProbeExporter.ParseStream(bytes, overlayBlock.Value24);
		ReadOnlySpan<byte> lookupPage = Type3RemapProbeExporter.GetLookupPage(bytes, overlayBlock.Value28 & ~0xFF);
		Dictionary<int, byte> pixels = new(basePixels);
		int overlayBaseOffset = (overlayYOffset * RuntimeStride) + overlayXOffset;
		int changedPixelCount = 0;

		foreach (Type3RemapSegment segment in stream.Segments)
		{
			int segmentOffset = overlayBaseOffset + segment.RelativeStartOffset;
			for (int position = 0; position < segment.SpanLength; position++)
			{
				int destinationOffset = segmentOffset + position;
				byte currentValue = pixels.TryGetValue(destinationOffset, out byte pixelValue) ? pixelValue : (byte)0;
				byte remappedValue = lookupPage[currentValue];
				if (currentValue != remappedValue)
				{
					changedPixelCount++;
				}

				pixels[destinationOffset] = remappedValue;
			}
		}

		return new Type3CompositeImage(pixels, changedPixelCount);
	}

	private static IReadOnlyDictionary<int, byte> TranslateImage(IReadOnlyDictionary<int, byte> sourcePixels, int offsetX, int offsetY)
	{
		Dictionary<int, byte> pixels = new(sourcePixels.Count);
		foreach ((int sourceOffset, byte value) in sourcePixels)
		{
			int sourceX = sourceOffset % RuntimeStride;
			int sourceY = sourceOffset / RuntimeStride;
			int destinationOffset = ((sourceY + offsetY) * RuntimeStride) + sourceX + offsetX;
			pixels[destinationOffset] = value;
		}

		return pixels;
	}

	private static IReadOnlyDictionary<int, byte> ComposeFullFrame(
		IReadOnlyDictionary<int, byte> basePixels,
		RenderedType1Image pilotImage,
		int pilotOffsetX,
		int pilotOffsetY,
		RenderedType1Image? accentImage,
		int accentOffsetX,
		int accentOffsetY)
	{
		Dictionary<int, byte> pixels = new();
		BlitImage(pixels, basePixels, 0, 0);
		BlitImage(pixels, pilotImage.Pixels, pilotOffsetX, pilotOffsetY);

		if (accentImage is not null)
		{
			BlitImage(pixels, accentImage.Pixels, accentOffsetX, accentOffsetY);
		}

		return pixels;
	}

	private static void BlitImage(Dictionary<int, byte> destinationPixels, IReadOnlyDictionary<int, byte> sourcePixels, int destinationX, int destinationY)
	{
		foreach ((int sourceOffset, byte value) in sourcePixels)
		{
			int sourceX = sourceOffset % RuntimeStride;
			int sourceY = sourceOffset / RuntimeStride;
			int targetX = destinationX + sourceX;
			int targetY = CanvasTopPadding + destinationY + sourceY;

			if (targetX < 0 || targetX >= SupportedBaseWidth || targetY < 0 || targetY >= FullCompositeHeight)
			{
				continue;
			}

			int targetOffset = (targetY * RuntimeStride) + targetX;
			destinationPixels[targetOffset] = value;
		}
	}

	private static void ValidateBounds(RenderedType1Image image, SurfaceBounds bounds, DatPayloadBlock30 block)
	{
		int left = bounds.Left;
		int top = bounds.Top;
		int right = left + block.Value08 - 1;
		int bottom = top + block.Value0C - 1;

		foreach (int offset in image.Pixels.Keys)
		{
			int x = offset % RuntimeStride;
			int y = offset / RuntimeStride;
			if (x < left || x > right || y < top || y > bottom)
			{
				throw new InvalidDataException(
					$"Base block {block.Index} writes outside its inferred {block.Value08}x{block.Value0C} box: pixel=({x},{y}) bounds=({left},{top})..({right},{bottom}).");
			}
		}
	}

	private static void SaveImage(
		string outputPath,
		IReadOnlyDictionary<int, byte> pixels,
		int left,
		int top,
		int width,
		int height,
		Rgba32[]? palette)
	{
		DatPaletteHelper.SaveIndexedImage(outputPath, pixels, left, top, width, height, RuntimeStride, palette);
	}

	private static void WriteMetadata(
		string outputPath,
		CatGunDat dat,
		DatResourceEntry resource,
		DatPayloadGroup payloadGroup,
		DatPaletteContext? paletteContext,
		string? paletteFailureReason,
		IReadOnlyList<Type3CompositeBaseSummary> baseSummaries,
		IReadOnlyList<Type3CompositePhaseSummary> phaseSummaries,
		int specialVariantCount,
		int cycleVariantCount)
	{
		List<string> lines =
		[
			$"DAT: {dat.FilePath}",
			$"Resource: {resource.Name}",
			$"Payload group: 0x{payloadGroup.StartOffset:X}..0x{payloadGroup.EndOffset:X}",
			"Grounded state: FUN_00038130 (+0x79 branch selector, +0x78 cycle countdown)",
			$"Direct base blocks: {string.Join(", ", baseSummaries.Select(summary => summary.BlockIndex.ToString("D2")))}",
			$"Type-3 selector table: {string.Join(' ', Type3SelectorTable.Select(value => value.ToString()))}",
			$"Y-offset table: {string.Join(' ', PhaseYOffsetTable.Select(value => value.ToString()))}",
			$"PILOT row lookup by PLAYER facing row: {string.Join(' ', PilotRowLookup.Select(value => value.ToString()))}",
			$"Overlay origin relative to base: x={OverlayOffsetX}, y=(0x{OverlayTopY:X} - phaseYOffset)",
			$"Exported composites: {phaseSummaries.Count}",
			$"FUN_00038130 special-branch variants: {specialVariantCount}",
			$"FUN_00038130 cycle-branch variants: {cycleVariantCount}",
			"Counter cadence: +0x370 += DAT_00093924 with wrap 0x80000, +0x374 += DAT_00093924 with wrap 0xC0000, and +0x378 += DAT_0009393C with wrap 0xC0000.",
			"Counter roles: base-block selection uses (+0x370 >> 16), while remap phase / Y-offset selection uses (+0x374 >> 16).",
			"Cadence-source status: DAT_00093924 still shows only READ xrefs in the current analysis surface, and the nearby DAT_0009393C stores at 0x35AC7 / 0x35B4D only preserve the current delta after decrement logic rather than introducing a newly identified source value.",
			"Local branch control: +0x79 == 7 takes the special branch; otherwise the cycle branch uses +0x78 as a countdown, resets +0x78 to 1 on underflow, and then increments +0x79.",
			"Note: base-block selection and remap phase still come from different FUN_00038130 counters, so this export writes the exact remap result for the recovered base-family x phase-table matrix rather than claiming one animation order.",
			"Additional exact branch exports are also written:",
			$"  special branch (+0x79 == 7): PILOT block = (pilotRow * 9) + variant, variant=0..7, dest=(x+7, baseTop-7)",
			$"  cycle branch (+0x79 = 0..6): PILOT block = (pilotRow * 9) + 8, PLAYER accent block = 0x16 + state, state=0..6, dests=(x+7, baseTop-7) and (x+12, baseTop-6)",
		];

		DatPaletteHelper.AppendMetadata(lines, dat, paletteContext, paletteFailureReason);
		lines.Add(string.Empty);
		lines.AddRange(
		[
			"Base blocks:",
		]);

		foreach (Type3CompositeBaseSummary summary in baseSummaries)
		{
			lines.Add(
				$"[{summary.BlockIndex:D2}] origin=({summary.Left},{summary.Top}) file={Path.GetFileName(summary.OutputPath)}");
		}

		lines.Add(string.Empty);
		lines.Add("Phase composites:");
		foreach (Type3CompositePhaseSummary summary in phaseSummaries)
		{
			lines.Add(
				$"base={summary.BaseBlockIndex:D2} phase={summary.PhaseIndex:D2} selector={summary.Selector} overlay={summary.OverlayBlockIndex:D2} yOffset={summary.RelativeYOffset} changed={summary.ChangedPixelCount} file={Path.GetFileName(summary.OutputPath)}");
		}

		File.WriteAllLines(outputPath, lines);
	}

	private static void WriteRx7Metadata(
		string outputPath,
		CatGunDat dat,
		DatResourceEntry resource,
		DatPayloadGroup payloadGroup,
		DatPaletteContext? paletteContext,
		string? paletteFailureReason,
		IReadOnlyList<Type3CompositeBaseSummary> baseSummaries,
		IReadOnlyList<Type3OverlayMatrixSummary> overlaySummaries)
	{
		WriteSimpleMatrixMetadata(
			outputPath,
			dat,
			resource,
			payloadGroup,
			paletteContext,
			paletteFailureReason,
			baseSummaries,
			overlaySummaries,
			"RX7_FRAMES exposes 36 direct type-1 base blocks plus 9 same-resource type-3 remap blocks sharing one lookup page, so this export writes the exact base-plus-remap matrix without claiming a final runtime order.",
			"Placement note: each overlay is applied at the same top-left origin as the direct base image.");
	}

	private static void WriteSimpleMatrixMetadata(
		string outputPath,
		CatGunDat dat,
		DatResourceEntry resource,
		DatPayloadGroup payloadGroup,
		DatPaletteContext? paletteContext,
		string? paletteFailureReason,
		IReadOnlyList<Type3CompositeBaseSummary> baseSummaries,
		IReadOnlyList<Type3OverlayMatrixSummary> overlaySummaries,
		string primaryNote,
		string placementNote)
	{
		List<string> lines =
		[
			$"DAT: {dat.FilePath}",
			$"Resource: {resource.Name}",
			$"Payload group: 0x{payloadGroup.StartOffset:X}..0x{payloadGroup.EndOffset:X}",
			$"Direct base blocks: {string.Join(", ", baseSummaries.Select(summary => summary.BlockIndex.ToString("D2")))}",
			$"Type-3 overlay blocks: {string.Join(", ", overlaySummaries.Select(summary => summary.OverlayBlockIndex).Distinct().OrderBy(index => index).Select(index => index.ToString("D2")))}",
			$"Exported composites: {overlaySummaries.Count}",
			$"Note: {primaryNote}",
			$"Note: {placementNote}",
		];

		DatPaletteHelper.AppendMetadata(lines, dat, paletteContext, paletteFailureReason);
		lines.Add(string.Empty);
		lines.Add("Base blocks:");
		foreach (Type3CompositeBaseSummary summary in baseSummaries)
		{
			lines.Add($"[{summary.BlockIndex:D2}] origin=({summary.Left},{summary.Top}) file={Path.GetFileName(summary.OutputPath)}");
		}

		lines.Add(string.Empty);
		lines.Add("Overlay composites:");
		foreach (Type3OverlayMatrixSummary summary in overlaySummaries)
		{
			lines.Add($"base={summary.BaseBlockIndex:D2} overlay={summary.OverlayBlockIndex:D2} changed={summary.ChangedPixelCount} file={Path.GetFileName(summary.OutputPath)}");
		}

		File.WriteAllLines(outputPath, lines);
	}

	private sealed record RenderedBaseBlock(DatPayloadBlock30 Block, RenderedType1Image Image, SurfaceBounds Bounds);
}

internal sealed record Type3CompositeExportResult(
	string ResourceName,
	string OutputDirectory,
	int BaseBlockCount,
	int PhaseCount,
	int CompositeCount,
	int SpecialVariantCount,
	int CycleVariantCount,
	string DefaultPaletteSummary,
	IReadOnlyList<string> ExportedPaletteVariants);

internal sealed record Type3CompositeBaseSummary(int BlockIndex, string OutputPath, int Left, int Top);

internal sealed record Type3CompositePhaseSummary(
	int BaseBlockIndex,
	int PhaseIndex,
	byte Selector,
	int OverlayBlockIndex,
	int RelativeYOffset,
	int ChangedPixelCount,
	string OutputPath);

internal sealed record Type3CompositeImage(IReadOnlyDictionary<int, byte> Pixels, int ChangedPixelCount);

internal sealed record Type3OverlayMatrixSummary(int BaseBlockIndex, int OverlayBlockIndex, int ChangedPixelCount, string OutputPath);

internal sealed record SurfaceBounds(int Left, int Top);
