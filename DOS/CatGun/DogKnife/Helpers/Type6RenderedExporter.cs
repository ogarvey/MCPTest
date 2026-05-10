using DogKnife.Models;

namespace DogKnife.Helpers;

internal static class Type6RenderedExporter
{
	private const byte HighlightIndex = 0x8A;
	private const byte MidIndex = 0x87;
	private const byte ShadowIndex = 0x84;

	public static Type6RenderedExportResult Export(CatGunDat dat, string resourceName, string outputRoot)
	{
		DatResourceEntry resource = dat.Resources.SingleOrDefault(candidate =>
			string.Equals(candidate.Name, resourceName, StringComparison.Ordinal))
			?? throw new InvalidDataException($"{resourceName} resource was not found in the DAT resource table.");

		DatPayloadGroup payloadGroup = dat.PayloadGroups.SingleOrDefault(group => group.StartOffset == resource.Pointer04)
			?? throw new InvalidDataException($"{resourceName} payload group was not found for resource field +0x04.");

		DatSequenceGroup? sequenceGroup = resource.Pointer08 == 0
			? null
			: dat.SequenceGroups.SingleOrDefault(group => group.StartOffset == resource.Pointer08);

		List<DatPayloadBlock30> type6Blocks = payloadGroup.Blocks
			.Where(block => block.LoaderType == 6)
			.OrderBy(block => block.Index)
			.ToList();

		if (type6Blocks.Count == 0)
		{
			throw new InvalidDataException($"{resourceName} does not expose any loader type-6 blocks in its current payload group.");
		}

		if (!DatPaletteHelper.TryCreateContext(dat, out DatPaletteContext? paletteContext, out string? paletteFailureReason))
		{
			throw new InvalidDataException(
				$"{resourceName} type-6 export expects a valid palette region. {paletteFailureReason}");
		}

		string familyRoot = Path.Combine(Path.GetFullPath(outputRoot), resourceName, "type6_overlay");
		DatPaletteHelper.ExportPaletteBankImages(familyRoot, paletteContext!);
		string defaultBlocksDirectory = Path.Combine(familyRoot, DatPaletteHelper.DefaultDirectoryName, "blocks");
		string defaultFramesDirectory = Path.Combine(familyRoot, DatPaletteHelper.DefaultDirectoryName, "frames");
		string grayscaleBlocksDirectory = Path.Combine(familyRoot, "grayscale", "blocks");
		string grayscaleFramesDirectory = Path.Combine(familyRoot, "grayscale", "frames");
		Directory.CreateDirectory(defaultBlocksDirectory);
		Directory.CreateDirectory(defaultFramesDirectory);
		Directory.CreateDirectory(grayscaleBlocksDirectory);
		Directory.CreateDirectory(grayscaleFramesDirectory);

		foreach (ExportPaletteVariant paletteVariant in paletteContext!.Variants)
		{
			Directory.CreateDirectory(Path.Combine(familyRoot, paletteVariant.DirectoryName, "blocks"));
			Directory.CreateDirectory(Path.Combine(familyRoot, paletteVariant.DirectoryName, "frames"));
		}

		ReadOnlySpan<byte> bytes = dat.RawBytes.Span;
		Dictionary<int, RenderedType6Overlay> overlaysByIndex = new(type6Blocks.Count);
		List<Type6BlockSummary> blockSummaries = new(type6Blocks.Count);

		foreach (DatPayloadBlock30 block in type6Blocks)
		{
			RenderedType6Overlay overlay = RenderBlock(bytes, block);
			overlaysByIndex.Add(block.Index, overlay);

			string baseFileName = $"block_{block.Index:D2}_logical_{overlay.LogicalWidth}x{overlay.LogicalHeight}_overlay_{overlay.PhysicalWidth}x{overlay.PhysicalHeight}_data_{overlay.DataOffset:X}.png";
			DatPaletteHelper.SaveIndexedImage(
				Path.Combine(defaultBlocksDirectory, baseFileName),
				overlay.Pixels,
				0,
				0,
				overlay.PhysicalWidth,
				overlay.PhysicalHeight,
				overlay.RuntimeStride,
				paletteContext.DefaultPalette);
			DatPaletteHelper.SaveIndexedImage(
				Path.Combine(grayscaleBlocksDirectory, baseFileName),
				overlay.Pixels,
				0,
				0,
				overlay.PhysicalWidth,
				overlay.PhysicalHeight,
				overlay.RuntimeStride,
				palette: null);

			foreach (ExportPaletteVariant paletteVariant in paletteContext.Variants)
			{
				DatPaletteHelper.SaveIndexedImage(
					Path.Combine(familyRoot, paletteVariant.DirectoryName, "blocks", baseFileName),
					overlay.Pixels,
					0,
					0,
					overlay.PhysicalWidth,
					overlay.PhysicalHeight,
					overlay.RuntimeStride,
					paletteContext.Banks[paletteVariant.BankIndex]);
			}

			blockSummaries.Add(new Type6BlockSummary(
				BlockIndex: block.Index,
				LogicalWidth: overlay.LogicalWidth,
				LogicalHeight: overlay.LogicalHeight,
				PhysicalWidth: overlay.PhysicalWidth,
				PhysicalHeight: overlay.PhysicalHeight,
				DataOffset: overlay.DataOffset,
				DataLength: overlay.DataLength,
				Value00: block.Value00,
				Value04: block.Value04,
				Value10: block.Value10,
				Value14: block.Value14,
				Value18: block.Value18,
				Value1C: block.Value1C));
		}

		List<byte> frameOrder = sequenceGroup?.Segments.SelectMany(segment => segment.Bytes).ToList() ?? [];
		int frameCount = 0;
		int skippedFrameCount = 0;

		for (int frameIndex = 0; frameIndex < frameOrder.Count; frameIndex++)
		{
			byte blockIndex = frameOrder[frameIndex];
			if (!overlaysByIndex.TryGetValue(blockIndex, out RenderedType6Overlay? overlay))
			{
				skippedFrameCount++;
				continue;
			}

			string frameFileName = $"frame_{frameIndex:D2}_block_{blockIndex:D2}.png";
			DatPaletteHelper.SaveIndexedImage(
				Path.Combine(defaultFramesDirectory, frameFileName),
				overlay.Pixels,
				0,
				0,
				overlay.PhysicalWidth,
				overlay.PhysicalHeight,
				overlay.RuntimeStride,
				paletteContext.DefaultPalette);
			DatPaletteHelper.SaveIndexedImage(
				Path.Combine(grayscaleFramesDirectory, frameFileName),
				overlay.Pixels,
				0,
				0,
				overlay.PhysicalWidth,
				overlay.PhysicalHeight,
				overlay.RuntimeStride,
				palette: null);

			foreach (ExportPaletteVariant paletteVariant in paletteContext.Variants)
			{
				DatPaletteHelper.SaveIndexedImage(
					Path.Combine(familyRoot, paletteVariant.DirectoryName, "frames", frameFileName),
					overlay.Pixels,
					0,
					0,
					overlay.PhysicalWidth,
					overlay.PhysicalHeight,
					overlay.RuntimeStride,
					paletteContext.Banks[paletteVariant.BankIndex]);
			}

			frameCount++;
		}

		WriteMetadata(
			Path.Combine(familyRoot, "metadata.txt"),
			dat,
			resource,
			payloadGroup,
			sequenceGroup,
			paletteContext,
			paletteFailureReason,
			blockSummaries,
			frameOrder,
			frameCount,
			skippedFrameCount);

		return new Type6RenderedExportResult(
			ResourceName: resourceName,
			OutputDirectory: familyRoot,
			BlockCount: blockSummaries.Count,
			FrameCount: frameCount,
			SkippedFrameCount: skippedFrameCount,
			DefaultPaletteSummary: paletteContext.DefaultPaletteSummary,
			ExportedPaletteVariants: paletteContext.Variants.Select(variant => $"{variant.DirectoryName}=bank{variant.BankIndex:D2}").ToArray());
	}

	private static RenderedType6Overlay RenderBlock(ReadOnlySpan<byte> bytes, DatPayloadBlock30 block)
	{
		int logicalWidth = block.Value08;
		int logicalHeight = block.Value0C;
		if (logicalWidth <= 0 || logicalHeight <= 0)
		{
			throw new InvalidDataException(
				$"Type-6 block {block.Index} has invalid logical dimensions {logicalWidth}x{logicalHeight}.");
		}

		int bytesPerRow = (logicalWidth + 7) >> 3;
		int dataLength = checked(bytesPerRow * logicalHeight);
		int dataOffset = block.Value24;
		if (dataOffset < 0 || dataOffset + dataLength > bytes.Length)
		{
			throw new InvalidDataException(
				$"Type-6 block {block.Index} stream exceeds file bounds: offset=0x{dataOffset:X}, size=0x{dataLength:X}.");
		}

		int runtimeStride = checked(bytesPerRow * 16);
		int physicalWidth = checked(logicalWidth * 2);
		int physicalHeight = checked(logicalHeight * 2);
		Dictionary<int, byte> pixels = new();

		for (int logicalY = 0; logicalY < logicalHeight; logicalY++)
		{
			int streamOffset = dataOffset + (logicalY * bytesPerRow);
			int rowBase = logicalY * 2 * runtimeStride;

			for (int byteIndex = 0; byteIndex < bytesPerRow; byteIndex++)
			{
				byte packed = bytes[streamOffset + byteIndex];
				int cellBase = rowBase + (byteIndex * 16);
				WriteNibble(pixels, runtimeStride, cellBase, packed >> 4);
				WriteNibble(pixels, runtimeStride, cellBase + 8, packed & 0x0F);
			}
		}

		return new RenderedType6Overlay(
			BlockIndex: block.Index,
			LogicalWidth: logicalWidth,
			LogicalHeight: logicalHeight,
			PhysicalWidth: physicalWidth,
			PhysicalHeight: physicalHeight,
			RuntimeStride: runtimeStride,
			DataOffset: dataOffset,
			DataLength: dataLength,
			Pixels: pixels);
	}

	private static void WriteNibble(Dictionary<int, byte> pixels, int stride, int baseOffset, int nibble)
	{
		WriteColumnPair(pixels, stride, baseOffset + 0, nibble, 0x8);
		WriteColumnPair(pixels, stride, baseOffset + 2, nibble, 0x4);
		WriteColumnPair(pixels, stride, baseOffset + 4, nibble, 0x2);
		WriteColumnPair(pixels, stride, baseOffset + 6, nibble, 0x1);
	}

	private static void WriteColumnPair(Dictionary<int, byte> pixels, int stride, int offset, int nibble, int mask)
	{
		if ((nibble & mask) == 0)
		{
			return;
		}

		pixels[offset + 0] = HighlightIndex;
		pixels[offset + 1] = MidIndex;
		pixels[offset + stride + 0] = MidIndex;
		pixels[offset + stride + 1] = ShadowIndex;
	}

	private static void WriteMetadata(
		string outputPath,
		CatGunDat dat,
		DatResourceEntry resource,
		DatPayloadGroup payloadGroup,
		DatSequenceGroup? sequenceGroup,
		DatPaletteContext paletteContext,
		string? paletteFailureReason,
		IReadOnlyList<Type6BlockSummary> blockSummaries,
		IReadOnlyList<byte> frameOrder,
		int frameCount,
		int skippedFrameCount)
	{
		List<string> lines =
		[
			$"DAT: {dat.FilePath}",
			$"Resource: {resource.Name}",
			$"Payload group: 0x{payloadGroup.StartOffset:X}..0x{payloadGroup.EndOffset:X}",
			$"Type-6 blocks: {string.Join(", ", blockSummaries.Select(summary => summary.BlockIndex.ToString("D2")))}",
			$"Exported overlay blocks: {blockSummaries.Count}",
			$"Sequence group: {(sequenceGroup is null ? "<none>" : $"0x{sequenceGroup.StartOffset:X}..0x{sequenceGroup.EndOffset:X} ({sequenceGroup.Segments.Count} segments)")}",
			$"Sequence bytes: {(frameOrder.Count == 0 ? "<none>" : string.Join(' ', frameOrder.Select(value => value.ToString("D2"))))}",
			$"Rendered frames: {frameCount}",
			$"Skipped frames: {skippedFrameCount}",
			"Runtime note: FUN_00012EA0 routes loader type-6 blocks into FUN_00028C64(width=block+0x08, height=block+0x0C, stream=block+0x24, dest=queue destination).",
			"Runtime note: FUN_00028C64 consumes ((logicalWidth + 7) >> 3) * logicalHeight bytes, and each nibble expands one logical bit group into a fixed 2x2 lit-pixel kernel using palette indices 0x8A, 0x87, and 0x84.",
			"Export note: untouched background pixels are preserved as transparency rather than guessed, because the type-6 decoder only writes lit cells and leaves the runtime substrate unchanged.",
		];

		DatPaletteHelper.AppendMetadata(lines, dat, paletteContext, paletteFailureReason);
		lines.Add(string.Empty);
		lines.Add("Overlay blocks:");
		foreach (Type6BlockSummary summary in blockSummaries)
		{
			lines.Add($"[{summary.BlockIndex:D2}] logical={summary.LogicalWidth}x{summary.LogicalHeight} physical={summary.PhysicalWidth}x{summary.PhysicalHeight} data=0x{summary.DataOffset:X}+0x{summary.DataLength:X} fields00/04={summary.Value00}/{summary.Value04} fields10/14={summary.Value10}/{summary.Value14} fields18/1C={summary.Value18}/{summary.Value1C}");
		}

		File.WriteAllLines(outputPath, lines);
	}

	private sealed record RenderedType6Overlay(
		int BlockIndex,
		int LogicalWidth,
		int LogicalHeight,
		int PhysicalWidth,
		int PhysicalHeight,
		int RuntimeStride,
		int DataOffset,
		int DataLength,
		IReadOnlyDictionary<int, byte> Pixels);
}

internal sealed record Type6RenderedExportResult(
	string ResourceName,
	string OutputDirectory,
	int BlockCount,
	int FrameCount,
	int SkippedFrameCount,
	string DefaultPaletteSummary,
	IReadOnlyList<string> ExportedPaletteVariants);

internal sealed record Type6BlockSummary(
	int BlockIndex,
	int LogicalWidth,
	int LogicalHeight,
	int PhysicalWidth,
	int PhysicalHeight,
	int DataOffset,
	int DataLength,
	int Value00,
	int Value04,
	int Value10,
	int Value14,
	int Value18,
	int Value1C);
