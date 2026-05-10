using DogKnife.Models;
using SixLabors.ImageSharp.PixelFormats;

namespace DogKnife.Helpers;

internal static class DisplayType3RuntimeExporter
{
	private const string SupportedResourceName = "DISPLAY";
	private const int RuntimeStride = 0x180;
	private const int Block35OverlayBlockIndex = 35;
	private const int Block35SlotBlockStartIndex = 27;
	private const int Block35SlotBlockCount = 8;
	private const int Block35SlotWidth = 5;
	private const int Block35SlotHeight = 8;
	private const int Block35ValueCap = 0x40;
	private const int GaugeBaseBlockIndex = 47;
	private const int GaugeOverlayBlockIndex = 48;
	private const int GaugeWidth = 32;
	private const int GaugeHeight = 6;
	private const int ExpectedOverlaySegmentCount = 6;

	public static bool SupportsResource(string resourceName)
	{
		return string.Equals(resourceName, SupportedResourceName, StringComparison.Ordinal);
	}

	public static DisplayType3RuntimeExportResult Export(CatGunDat dat, string outputRoot)
	{
		DatResourceEntry resource = dat.Resources.SingleOrDefault(candidate =>
			string.Equals(candidate.Name, SupportedResourceName, StringComparison.Ordinal))
			?? throw new InvalidDataException($"{SupportedResourceName} resource was not found in the DAT resource table.");

		DatPayloadGroup payloadGroup = dat.PayloadGroups.SingleOrDefault(group => group.StartOffset == resource.Pointer04)
			?? throw new InvalidDataException($"{SupportedResourceName} payload group was not found for resource field +0x04.");

		DatPayloadBlock30 baseBlock = payloadGroup.Blocks.SingleOrDefault(block =>
			block.Index == GaugeBaseBlockIndex &&
			block.LoaderType == 4 &&
			block.Value08 == GaugeWidth &&
			block.Value0C == GaugeHeight)
			?? throw new InvalidDataException(
				$"{SupportedResourceName} exact type-4 gauge substrate block {GaugeBaseBlockIndex} was not found at {GaugeWidth}x{GaugeHeight}.");

		DatPayloadBlock30 overlayBlock = payloadGroup.Blocks.SingleOrDefault(block =>
			block.Index == GaugeOverlayBlockIndex &&
			block.LoaderType == 3 &&
			block.Value08 == GaugeWidth &&
			block.Value0C == GaugeHeight)
			?? throw new InvalidDataException(
				$"{SupportedResourceName} exact type-3 gauge overlay block {GaugeOverlayBlockIndex} was not found at {GaugeWidth}x{GaugeHeight}.");

		DatPayloadBlock30? block35OverlayBlock = payloadGroup.Blocks.SingleOrDefault(block =>
			block.Index == Block35OverlayBlockIndex &&
			block.LoaderType == 3);

		List<DatPayloadBlock30> block35SlotBlocks = payloadGroup.Blocks
			.Where(block =>
				block.LoaderType == 1 &&
				block.Index is >= Block35SlotBlockStartIndex and < Block35SlotBlockStartIndex + Block35SlotBlockCount &&
				block.Value08 == Block35SlotWidth &&
				block.Value0C == Block35SlotHeight)
			.OrderBy(block => block.Index)
			.ToList();

		ReadOnlySpan<byte> bytes = dat.RawBytes.Span;
		ReadOnlySpan<byte> baseIndices = GetType4Plane(bytes, baseBlock);
		Type3RemapStream originalOverlayStream = Type3RemapProbeExporter.ParseStream(bytes, overlayBlock.Value24);
		if (originalOverlayStream.SegmentCount != ExpectedOverlaySegmentCount)
		{
			throw new InvalidDataException(
				$"Expected DISPLAY block {GaugeOverlayBlockIndex} to expose {ExpectedOverlaySegmentCount} remap segments, found {originalOverlayStream.SegmentCount}.");
		}

		ReadOnlySpan<byte> lookupPage = Type3RemapProbeExporter.GetLookupPage(bytes, overlayBlock.Value28 & ~0xFF);
		Dictionary<int, byte> basePixels = BuildDensePixelMap(baseIndices, GaugeWidth, GaugeHeight);

		string familyRoot = Path.Combine(Path.GetFullPath(outputRoot), SupportedResourceName, "type3_runtime");
		string defaultBaseDirectory = Path.Combine(familyRoot, DatPaletteHelper.DefaultDirectoryName, "base_blocks");
		string defaultBlock35PrefixDirectory = Path.Combine(familyRoot, DatPaletteHelper.DefaultDirectoryName, "block35_filled_slot_counts");
		string defaultVariantDirectory = Path.Combine(familyRoot, DatPaletteHelper.DefaultDirectoryName, "block48_width_variants");
		string grayscaleBaseDirectory = Path.Combine(familyRoot, "grayscale", "base_blocks");
		string grayscaleBlock35PrefixDirectory = Path.Combine(familyRoot, "grayscale", "block35_filled_slot_counts");
		string grayscaleVariantDirectory = Path.Combine(familyRoot, "grayscale", "block48_width_variants");
		Directory.CreateDirectory(defaultBaseDirectory);
		Directory.CreateDirectory(defaultBlock35PrefixDirectory);
		Directory.CreateDirectory(defaultVariantDirectory);
		Directory.CreateDirectory(grayscaleBaseDirectory);
		Directory.CreateDirectory(grayscaleBlock35PrefixDirectory);
		Directory.CreateDirectory(grayscaleVariantDirectory);

		DatPaletteHelper.TryCreateContext(dat, out DatPaletteContext? paletteContext, out string? paletteFailureReason);
		Rgba32[]? defaultPalette = paletteContext?.DefaultPalette;

		if (paletteContext is not null)
		{
			DatPaletteHelper.ExportPaletteBankImages(familyRoot, paletteContext);

			foreach (ExportPaletteVariant variant in paletteContext.Variants)
			{
				Directory.CreateDirectory(Path.Combine(familyRoot, variant.DirectoryName, "base_blocks"));
				Directory.CreateDirectory(Path.Combine(familyRoot, variant.DirectoryName, "block35_filled_slot_counts"));
				Directory.CreateDirectory(Path.Combine(familyRoot, variant.DirectoryName, "block48_width_variants"));
			}
		}

		string defaultBasePath = Path.Combine(
			defaultBaseDirectory,
			$"block_{baseBlock.Index:D2}_{baseBlock.Value08}x{baseBlock.Value0C}_data_{baseBlock.Value24:X}.png");
		string grayscaleBasePath = Path.Combine(
			grayscaleBaseDirectory,
			$"block_{baseBlock.Index:D2}_{baseBlock.Value08}x{baseBlock.Value0C}_data_{baseBlock.Value24:X}.png");
		SaveImage(defaultBasePath, basePixels, 0, 0, GaugeWidth, GaugeHeight, defaultPalette);
		SaveImage(grayscaleBasePath, basePixels, 0, 0, GaugeWidth, GaugeHeight, palette: null);

		if (paletteContext is not null)
		{
			foreach (ExportPaletteVariant variant in paletteContext.Variants)
			{
				string variantBasePath = Path.Combine(
					familyRoot,
					variant.DirectoryName,
					"base_blocks",
					$"block_{baseBlock.Index:D2}_{baseBlock.Value08}x{baseBlock.Value0C}_data_{baseBlock.Value24:X}.png");
				SaveImage(variantBasePath, basePixels, 0, 0, GaugeWidth, GaugeHeight, paletteContext.Banks[variant.BankIndex]);
			}
		}

		List<DisplayType3Block35PrefixSummary> block35PrefixVariants = [];
		if (block35OverlayBlock is not null && block35SlotBlocks.Count == Block35SlotBlockCount)
		{
			List<RenderedType1Image> slotImages = new(block35SlotBlocks.Count);
			foreach (DatPayloadBlock30 block in block35SlotBlocks)
			{
				slotImages.Add(Type1RenderedExporter.RenderBlock(bytes, block));
			}

			for (int filledSlotCount = 0; filledSlotCount <= Block35SlotBlockCount; filledSlotCount++)
			{
				IReadOnlyDictionary<int, byte> pixels = RenderBlock35FilledSlotPrefix(slotImages, filledSlotCount);
				string defaultPrefixPath = Path.Combine(
					defaultBlock35PrefixDirectory,
					$"filled_slots_{filledSlotCount:D2}.png");
				string grayscalePrefixPath = Path.Combine(
					grayscaleBlock35PrefixDirectory,
					$"filled_slots_{filledSlotCount:D2}.png");
				SaveImage(defaultPrefixPath, pixels, 0, 0, Block35SlotBlockCount * Block35SlotWidth, Block35SlotHeight, defaultPalette);
				SaveImage(grayscalePrefixPath, pixels, 0, 0, Block35SlotBlockCount * Block35SlotWidth, Block35SlotHeight, palette: null);

				if (paletteContext is not null)
				{
					foreach (ExportPaletteVariant variant in paletteContext.Variants)
					{
						string variantPrefixPath = Path.Combine(
							familyRoot,
							variant.DirectoryName,
							"block35_filled_slot_counts",
							$"filled_slots_{filledSlotCount:D2}.png");
						SaveImage(variantPrefixPath, pixels, 0, 0, Block35SlotBlockCount * Block35SlotWidth, Block35SlotHeight, paletteContext.Banks[variant.BankIndex]);
					}
				}

				block35PrefixVariants.Add(new DisplayType3Block35PrefixSummary(
					FilledSlotCount: filledSlotCount,
					OutputPath: defaultPrefixPath));
			}
		}

		List<DisplayType3GaugeVariantSummary> gaugeVariants = new(GaugeWidth + 1);
		for (int filledWidth = 0; filledWidth <= GaugeWidth; filledWidth++)
		{
			Type3CompositeImage image = RenderGaugeVariant(baseIndices, filledWidth, originalOverlayStream, lookupPage);
			int remainingWidth = GaugeWidth - filledWidth;

			string defaultVariantPath = Path.Combine(
				defaultVariantDirectory,
				$"block_{GaugeOverlayBlockIndex:D2}_width_{filledWidth:D2}.png");
			string grayscaleVariantPath = Path.Combine(
				grayscaleVariantDirectory,
				$"block_{GaugeOverlayBlockIndex:D2}_width_{filledWidth:D2}.png");
			SaveImage(defaultVariantPath, image.Pixels, 0, 0, GaugeWidth, GaugeHeight, defaultPalette);
			SaveImage(grayscaleVariantPath, image.Pixels, 0, 0, GaugeWidth, GaugeHeight, palette: null);

			if (paletteContext is not null)
			{
				foreach (ExportPaletteVariant variant in paletteContext.Variants)
				{
					string variantPath = Path.Combine(
						familyRoot,
						variant.DirectoryName,
						"block48_width_variants",
						$"block_{GaugeOverlayBlockIndex:D2}_width_{filledWidth:D2}.png");
					SaveImage(variantPath, image.Pixels, 0, 0, GaugeWidth, GaugeHeight, paletteContext.Banks[variant.BankIndex]);
				}
			}

			gaugeVariants.Add(new DisplayType3GaugeVariantSummary(
				FilledWidth: filledWidth,
				RemainingWidth: remainingWidth,
				PixelCount: image.Pixels.Count,
				RemappedPixelCount: image.ChangedPixelCount,
				OutputPath: defaultVariantPath));
		}

		WriteMetadata(
			Path.Combine(familyRoot, "metadata.txt"),
			dat,
			resource,
			payloadGroup,
			baseBlock,
			overlayBlock,
			block35OverlayBlock,
			block35SlotBlocks,
			originalOverlayStream,
			block35PrefixVariants,
			gaugeVariants,
			paletteContext,
			paletteFailureReason);

		return new DisplayType3RuntimeExportResult(
			ResourceName: resource.Name,
			OutputDirectory: familyRoot,
			GaugeWidthVariantCount: gaugeVariants.Count,
			Block35PrefixVariantCount: block35PrefixVariants.Count,
			DocumentedOnlyBlockCount: block35OverlayBlock is null ? 0 : 1,
			DefaultPaletteSummary: paletteContext?.DefaultPaletteSummary ?? $"{DatPaletteHelper.DefaultDirectoryName}/ falls back to grayscale; palette data unavailable.",
			ExportedPaletteVariants: paletteContext?.Variants.Select(variant => $"{variant.DirectoryName}=bank{variant.BankIndex:D2}").ToArray() ?? []);
	}

	private static IReadOnlyDictionary<int, byte> RenderBlock35FilledSlotPrefix(IReadOnlyList<RenderedType1Image> slotImages, int filledSlotCount)
	{
		Dictionary<int, byte> pixels = new(Block35SlotBlockCount * Block35SlotWidth * Block35SlotHeight);
		for (int slotIndex = 0; slotIndex < filledSlotCount; slotIndex++)
		{
			BlitImage(pixels, slotImages[slotIndex].Pixels, slotIndex * Block35SlotWidth, 0, Block35SlotBlockCount * Block35SlotWidth, Block35SlotHeight);
		}

		return pixels;
	}

	private static ReadOnlySpan<byte> GetType4Plane(ReadOnlySpan<byte> bytes, DatPayloadBlock30 block)
	{
		int pixelCount = checked(block.Value08 * block.Value0C);
		if (block.Value24 < 0 || block.Value24 + pixelCount > bytes.Length)
		{
			throw new InvalidDataException(
				$"DISPLAY type-4 block {block.Index} exceeds file bounds: offset=0x{block.Value24:X}, size=0x{pixelCount:X}");
		}

		return bytes.Slice(block.Value24, pixelCount);
	}

	private static Dictionary<int, byte> BuildDensePixelMap(ReadOnlySpan<byte> indices, int width, int height)
	{
		Dictionary<int, byte> pixels = new(width * height);
		for (int y = 0; y < height; y++)
		{
			int rowOffset = y * width;
			for (int x = 0; x < width; x++)
			{
				pixels[(y * RuntimeStride) + x] = indices[rowOffset + x];
			}
		}

		return pixels;
	}

	private static Type3CompositeImage RenderGaugeVariant(
		ReadOnlySpan<byte> baseIndices,
		int filledWidth,
		Type3RemapStream originalOverlayStream,
		ReadOnlySpan<byte> lookupPage)
	{
		Dictionary<int, byte> pixels = new(GaugeWidth * GaugeHeight);
		for (int y = 0; y < GaugeHeight; y++)
		{
			int rowOffset = y * GaugeWidth;
			for (int x = 0; x < filledWidth; x++)
			{
				pixels[(y * RuntimeStride) + x] = baseIndices[rowOffset + x];
			}
		}

		int remappedPixelCount = 0;
		if (filledWidth > 0 && filledWidth < GaugeWidth)
		{
			int remainingWidth = GaugeWidth - filledWidth;
			Type3RemapStream patchedStream = CreatePatchedGaugeOverlayStream(originalOverlayStream, remainingWidth);
			ApplyOverlay(pixels, patchedStream, lookupPage, filledWidth, 0, ref remappedPixelCount);
		}

		return new Type3CompositeImage(pixels, remappedPixelCount);
	}

	private static Type3RemapStream CreatePatchedGaugeOverlayStream(Type3RemapStream originalStream, int remainingWidth)
	{
		List<Type3RemapSegment> segments = new(originalStream.SegmentCount);
		int relativeOffset = 0;

		for (int segmentIndex = 0; segmentIndex < originalStream.SegmentCount; segmentIndex++)
		{
			ushort destinationSkip = segmentIndex == 0
				? checked((ushort)originalStream.Segments[0].DestinationSkip)
				: checked((ushort)(RuntimeStride - remainingWidth));
			ushort spanLength = checked((ushort)remainingWidth);
			relativeOffset += destinationSkip;
			int startOffset = relativeOffset;
			int endOffset = checked(startOffset + spanLength);
			segments.Add(new Type3RemapSegment(
				Index: segmentIndex,
				DestinationSkip: destinationSkip,
				SpanLength: spanLength,
				RelativeStartOffset: startOffset,
				RelativeEndOffset: endOffset));
			relativeOffset = endOffset;
		}

		return new Type3RemapStream(
			StreamOffset: originalStream.StreamOffset,
			SegmentCount: originalStream.SegmentCount,
			CoveredPixelCount: remainingWidth * originalStream.SegmentCount,
			RelativeEndOffset: relativeOffset,
			Segments: segments);
	}

	private static void ApplyOverlay(
		Dictionary<int, byte> pixels,
		Type3RemapStream stream,
		ReadOnlySpan<byte> lookupPage,
		int left,
		int top,
		ref int remappedPixelCount)
	{
		int overlayBaseOffset = (top * RuntimeStride) + left;
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
					remappedPixelCount++;
				}
				pixels[destinationOffset] = remappedValue;
			}
		}
	}

	private static void BlitImage(
		Dictionary<int, byte> destinationPixels,
		IReadOnlyDictionary<int, byte> sourcePixels,
		int destinationX,
		int destinationY,
		int outputWidth,
		int outputHeight)
	{
		foreach ((int sourceOffset, byte value) in sourcePixels)
		{
			int sourceX = sourceOffset % RuntimeStride;
			int sourceY = sourceOffset / RuntimeStride;
			int targetX = destinationX + sourceX;
			int targetY = destinationY + sourceY;

			if (targetX < 0 || targetX >= outputWidth || targetY < 0 || targetY >= outputHeight)
			{
				continue;
			}

			destinationPixels[(targetY * RuntimeStride) + targetX] = value;
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
		DatPayloadBlock30 baseBlock,
		DatPayloadBlock30 overlayBlock,
		DatPayloadBlock30? block35OverlayBlock,
		IReadOnlyList<DatPayloadBlock30> block35SlotBlocks,
		Type3RemapStream originalOverlayStream,
		IReadOnlyList<DisplayType3Block35PrefixSummary> block35PrefixVariants,
		IReadOnlyList<DisplayType3GaugeVariantSummary> gaugeVariants,
		DatPaletteContext? paletteContext,
		string? paletteFailureReason)
	{
		string loaderTypes = string.Join(
			", ",
			payloadGroup.Blocks
				.GroupBy(block => block.LoaderType)
				.OrderBy(group => group.Key)
				.Select(group => $"0x{group.Key:X2}:{group.Count()}"));

		List<string> lines =
		[
			$"DAT: {dat.FilePath}",
			$"Resource: {resource.Name}",
			$"Payload group: 0x{payloadGroup.StartOffset:X}..0x{payloadGroup.EndOffset:X}",
			$"Payload block count: {payloadGroup.Blocks.Count}",
			$"Loader types: {loaderTypes}",
			"Grounded states: FUN_0002EFE0() case 1/2 -> FUN_0002F5C0() -> FUN_0002F6A0() / FUN_00028A00(); case 3 -> FUN_0002EDA0() direct prefix over blocks 27..34, then FUN_00028A00() over the remaining unresolved substrate.",
			$"Exact type-4 substrate block: [{baseBlock.Index:D2}] size={baseBlock.Value08}x{baseBlock.Value0C} data=0x{baseBlock.Value24:X}",
			$"Exact type-3 overlay block: [{overlayBlock.Index:D2}] declared={overlayBlock.Value08}x{overlayBlock.Value0C} stream=0x{overlayBlock.Value24:X} page=0x{(overlayBlock.Value28 & ~0xFF):X} segments={originalOverlayStream.SegmentCount}",
			block35OverlayBlock is null
				? "Block-35 remap block: <none>"
				: $"Block-35 remap block: [{block35OverlayBlock.Index:D2}] declared={block35OverlayBlock.Value08}x{block35OverlayBlock.Value0C} stream=0x{block35OverlayBlock.Value24:X} page=0x{(block35OverlayBlock.Value28 & ~0xFF):X}",
			"Runtime width formula: filledWidth = (value + 0x1FFFFF) >> 21, which yields 0..32 in the recovered path.",
			$"Block-35 slot-count formula: valueHi = min(0x{Block35ValueCap:X}, value >> 16), slotCount = (valueHi + 7) >> 3, which yields 0..{Block35SlotBlockCount}.",
			"Recovered runtime layout: FUN_0002FA80() first calls FUN_0002F8B0(), which draws block 16 at (26,2) and block 17 at (224,2), then calls FUN_0002EFE0() twice through the static layout tables at 0x2ECE8 and 0x2ED14.",
			"Primary strip layout proof: table 0x2ECE8 places the block-35 strip at (26,12), while the same-state DISPLAY draws in that path land at x=4 (21x18 family), x=46 (block 00, 7x9), and x=76 (FUN_0002ED40 digits). Those do not overlap the strip, so the primary block-35 remap is operating on preexisting framebuffer bytes outside the current DISPLAY-owned draw sequence.",
			"Secondary strip layout proof: table 0x2ED14 places the block-35 strip at (224,12). The helper pre-draw block 17 at (224,2) overlaps only the leftmost 21 pixels of that 40-pixel strip, leaving the rightmost 19 pixels still dependent on framebuffer state outside the current DISPLAY path.",
			"Exact block-48 export behavior:",
			"- filledWidth 0: FUN_0002F5C0() draws nothing.",
			"- filledWidth 1..31: copy that many columns from block 47, then patch block 48's six-segment stream in place and apply FUN_00028A00() at x=filledWidth.",
			"- filledWidth 32: copy the full 32-column block 47 and skip block 48.",
			$"Exported exact block-48 width variants: {gaugeVariants.Count}",
			block35PrefixVariants.Count == 0
				? "Exact block-35 filled-slot prefix export: <none>"
				: $"Exact block-35 filled-slot prefix export: {block35PrefixVariants.Count} variants from the proven direct prefix over blocks {block35SlotBlocks.First().Index:D2}..{block35SlotBlocks.Last().Index:D2} at +{Block35SlotWidth} x-strides.",
			block35OverlayBlock is null
				? "Documented-only DISPLAY type-3 remainder: block 35 was not present in this DAT."
				: $"Documented-only DISPLAY type-3 remainder: after drawing blocks {string.Join(", ", block35SlotBlocks.Select(block => block.Index.ToString("D2")))} for the first slotCount positions, FUN_0002EDA0() applies block {block35OverlayBlock.Index:D2} through FUN_00028A00() over the remaining positions using preexisting framebuffer bytes as substrate. That final substrate is still unresolved, so the exporter stops at the proven direct prefix family rather than claiming a final rendered empty-slot strip.",
		];

		DatPaletteHelper.AppendMetadata(lines, dat, paletteContext, paletteFailureReason);
		lines.Add(string.Empty);
		lines.Add("Block-35 filled-slot prefix variants:");
		foreach (DisplayType3Block35PrefixSummary summary in block35PrefixVariants)
		{
			lines.Add($"filledSlots={summary.FilledSlotCount:D2} file={Path.GetFileName(summary.OutputPath)}");
		}

		lines.Add(string.Empty);
		lines.Add("Block-48 width variants:");
		foreach (DisplayType3GaugeVariantSummary summary in gaugeVariants)
		{
			lines.Add(
				$"width={summary.FilledWidth:D2} remaining={summary.RemainingWidth:D2} pixels={summary.PixelCount} remapped={summary.RemappedPixelCount} file={Path.GetFileName(summary.OutputPath)}");
		}

		File.WriteAllLines(outputPath, lines);
	}
}

internal sealed record DisplayType3RuntimeExportResult(
	string ResourceName,
	string OutputDirectory,
	int GaugeWidthVariantCount,
	int Block35PrefixVariantCount,
	int DocumentedOnlyBlockCount,
	string DefaultPaletteSummary,
	IReadOnlyList<string> ExportedPaletteVariants);

internal sealed record DisplayType3Block35PrefixSummary(
	int FilledSlotCount,
	string OutputPath);

internal sealed record DisplayType3GaugeVariantSummary(
	int FilledWidth,
	int RemainingWidth,
	int PixelCount,
	int RemappedPixelCount,
	string OutputPath);
