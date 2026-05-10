using DogKnife.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DogKnife.Helpers;

internal static class Type2CropResourceExporter
{
	private static readonly HashSet<string> SupportedResourceNames = new(StringComparer.Ordinal)
	{
		"LEVINFO0",
		"LEVINFO1",
		"LEVINFO2",
	};

	public static bool SupportsResource(string resourceName)
	{
		return SupportedResourceNames.Contains(resourceName);
	}

	public static Type2CropResourceExportResult Export(CatGunDat dat, string resourceName, string outputRoot)
	{
		if (!SupportsResource(resourceName))
		{
			throw new NotSupportedException(
				$"{resourceName} is not supported by the current type-2 crop exporter. The proven descriptor-crop path is currently limited to LEVINFO0/1/2.");
		}

		DatResourceEntry resource = dat.Resources.SingleOrDefault(candidate =>
			string.Equals(candidate.Name, resourceName, StringComparison.Ordinal))
			?? throw new InvalidDataException($"{resourceName} resource was not found in the DAT resource table.");

		DatPayloadGroup payloadGroup = dat.PayloadGroups.SingleOrDefault(group => group.StartOffset == resource.Pointer04)
			?? throw new InvalidDataException($"{resourceName} payload group was not found for resource field +0x04.");

		DatSequenceGroup? sequenceGroup = resource.Pointer08 == 0
			? null
			: dat.SequenceGroups.SingleOrDefault(group => group.StartOffset == resource.Pointer08);

		List<DatPayloadBlock30> type4Blocks = payloadGroup.Blocks
			.Where(block => block.LoaderType == 4)
			.OrderBy(block => block.Index)
			.ToList();

		if (type4Blocks.Count != 1)
		{
			throw new InvalidDataException(
				$"{resourceName} crop export expects exactly one paired type-4 plane block, found {type4Blocks.Count}.");
		}

		List<DatPayloadBlock30> type2Blocks = payloadGroup.Blocks
			.Where(block => block.LoaderType == 2)
			.OrderBy(block => block.Index)
			.ToList();

		if (type2Blocks.Count == 0)
		{
			throw new InvalidDataException($"{resourceName} does not expose any loader type-2 blocks in its current payload group.");
		}

		ReadOnlySpan<byte> bytes = dat.RawBytes.Span;
		DatPayloadBlock30 planeBlock = type4Blocks[0];
		int planeWidth = planeBlock.Value08;
		int planeHeight = planeBlock.Value0C;
		int planePixelCount = checked(planeWidth * planeHeight);
		int planeDataOffset = planeBlock.Value24;

		if (planeDataOffset < 0 || planeDataOffset + planePixelCount > bytes.Length)
		{
			throw new InvalidDataException(
				$"{resourceName} paired type-4 plane exceeds file bounds: offset=0x{planeDataOffset:X}, size=0x{planePixelCount:X}.");
		}

		byte[] planeIndices = bytes.Slice(planeDataOffset, planePixelCount).ToArray();

		if (!DatPaletteHelper.TryCreateContext(dat, out DatPaletteContext? paletteContext, out string? paletteFailureReason))
		{
			throw new InvalidDataException(
				$"{resourceName} crop export expects a valid palette region. {paletteFailureReason}");
		}

		string familyRoot = Path.Combine(Path.GetFullPath(outputRoot), resourceName, "type2_crop");
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

		Dictionary<int, Type2CropDescriptor> descriptorsByIndex = new(type2Blocks.Count);
		List<Type2CropBlockSummary> blockSummaries = new(type2Blocks.Count);

		foreach (DatPayloadBlock30 block in type2Blocks)
		{
			Type2CropDescriptor descriptor = CreateDescriptor(block, planeWidth, planeHeight);
			byte[] cropIndices = CropPlane(planeIndices, planeWidth, descriptor.Left, descriptor.Top, descriptor.Width, descriptor.Height);
			descriptorsByIndex.Add(block.Index, descriptor);

			string defaultBlockPath = Path.Combine(
				defaultBlocksDirectory,
				$"block_{block.Index:D2}_x{descriptor.Left:D3}_y{descriptor.Top:D3}_{descriptor.Width}x{descriptor.Height}.png");
			string grayscaleBlockPath = Path.Combine(
				grayscaleBlocksDirectory,
				$"block_{block.Index:D2}_x{descriptor.Left:D3}_y{descriptor.Top:D3}_{descriptor.Width}x{descriptor.Height}.png");
			SaveImage(defaultBlockPath, descriptor.Width, descriptor.Height, cropIndices, paletteContext.DefaultPalette);
			SaveImage(grayscaleBlockPath, descriptor.Width, descriptor.Height, cropIndices, palette: null);

			foreach (ExportPaletteVariant paletteVariant in paletteContext.Variants)
			{
				string paletteBlockPath = Path.Combine(
					familyRoot,
					paletteVariant.DirectoryName,
					"blocks",
					$"block_{block.Index:D2}_x{descriptor.Left:D3}_y{descriptor.Top:D3}_{descriptor.Width}x{descriptor.Height}.png");
				SaveImage(paletteBlockPath, descriptor.Width, descriptor.Height, cropIndices, paletteContext.Banks[paletteVariant.BankIndex]);
			}

			blockSummaries.Add(new Type2CropBlockSummary(block.Index, descriptor.Left, descriptor.Top, descriptor.Width, descriptor.Height, block.Value24, defaultBlockPath));
		}

		List<byte> frameOrder = sequenceGroup?.Segments.SelectMany(segment => segment.Bytes).ToList() ?? [];
		int frameCount = 0;
		int skippedFrameCount = 0;

		for (int frameIndex = 0; frameIndex < frameOrder.Count; frameIndex++)
		{
			byte blockIndex = frameOrder[frameIndex];
			if (!descriptorsByIndex.TryGetValue(blockIndex, out Type2CropDescriptor? descriptor))
			{
				skippedFrameCount++;
				continue;
			}

			byte[] cropIndices = CropPlane(planeIndices, planeWidth, descriptor.Left, descriptor.Top, descriptor.Width, descriptor.Height);
			string defaultFramePath = Path.Combine(defaultFramesDirectory, $"frame_{frameIndex:D2}_block_{blockIndex:D2}.png");
			string grayscaleFramePath = Path.Combine(grayscaleFramesDirectory, $"frame_{frameIndex:D2}_block_{blockIndex:D2}.png");
			SaveImage(defaultFramePath, descriptor.Width, descriptor.Height, cropIndices, paletteContext.DefaultPalette);
			SaveImage(grayscaleFramePath, descriptor.Width, descriptor.Height, cropIndices, palette: null);

			foreach (ExportPaletteVariant paletteVariant in paletteContext.Variants)
			{
				string paletteFramePath = Path.Combine(
					familyRoot,
					paletteVariant.DirectoryName,
					"frames",
					$"frame_{frameIndex:D2}_block_{blockIndex:D2}.png");
				SaveImage(paletteFramePath, descriptor.Width, descriptor.Height, cropIndices, paletteContext.Banks[paletteVariant.BankIndex]);
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
			planeBlock,
			blockSummaries,
			frameOrder,
			frameCount,
			skippedFrameCount);

		return new Type2CropResourceExportResult(
			ResourceName: resourceName,
			OutputDirectory: familyRoot,
			BlockCount: blockSummaries.Count,
			FrameCount: frameCount,
			SkippedFrameCount: skippedFrameCount,
			DefaultPaletteSummary: paletteContext.DefaultPaletteSummary,
			ExportedPaletteVariants: paletteContext.Variants.Select(variant => $"{variant.DirectoryName}=bank{variant.BankIndex:D2}").ToArray());
	}

	private static Type2CropDescriptor CreateDescriptor(DatPayloadBlock30 block, int planeWidth, int planeHeight)
	{
		bool hasRepeatedSize = block.Value18 == block.Value08 && block.Value1C == block.Value0C;
		bool hasZeroSecondarySize = block.Value18 == 0 && block.Value1C == 0;
		if (!hasRepeatedSize && !hasZeroSecondarySize)
		{
			throw new InvalidDataException(
				$"Type-2 block {block.Index} does not preserve a compatible secondary size pair: 08/0C={block.Value08}x{block.Value0C}, 18/1C={block.Value18}x{block.Value1C}.");
		}

		int left = block.Value00;
		int top = block.Value04;
		int width = block.Value08;
		int height = block.Value0C;

		if (left < 0 || top < 0 || width <= 0 || height <= 0)
		{
			throw new InvalidDataException(
				$"Type-2 block {block.Index} has invalid crop rectangle ({left},{top}) {width}x{height}.");
		}

		if (left + width > planeWidth || top + height > planeHeight)
		{
			throw new InvalidDataException(
				$"Type-2 block {block.Index} crop rectangle ({left},{top}) {width}x{height} exceeds paired plane {planeWidth}x{planeHeight}.");
		}

		return new Type2CropDescriptor(left, top, width, height);
	}

	private static byte[] CropPlane(byte[] planeIndices, int planeWidth, int left, int top, int width, int height)
	{
		byte[] crop = new byte[checked(width * height)];
		for (int y = 0; y < height; y++)
		{
			int sourceOffset = (top + y) * planeWidth + left;
			int destinationOffset = y * width;
			Buffer.BlockCopy(planeIndices, sourceOffset, crop, destinationOffset, width);
		}

		return crop;
	}

	private static void SaveImage(string outputPath, int width, int height, ReadOnlySpan<byte> indices, Rgba32[]? palette)
	{
		using Image<Rgba32> image = new(width, height);
		byte[] pixelIndices = indices.ToArray();

		image.ProcessPixelRows(accessor =>
		{
			for (int y = 0; y < height; y++)
			{
				Span<Rgba32> row = accessor.GetRowSpan(y);
				int rowOffset = y * width;

				for (int x = 0; x < width; x++)
				{
					byte index = pixelIndices[rowOffset + x];
					row[x] = palette is null
						? new Rgba32(index, index, index)
						: palette[index];
				}
			}
		});

		Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
		image.SaveAsPng(outputPath);
	}

	private static void WriteMetadata(
		string outputPath,
		CatGunDat dat,
		DatResourceEntry resource,
		DatPayloadGroup payloadGroup,
		DatSequenceGroup? sequenceGroup,
		DatPaletteContext paletteContext,
		string? paletteFailureReason,
		DatPayloadBlock30 planeBlock,
		IReadOnlyList<Type2CropBlockSummary> blockSummaries,
		IReadOnlyList<byte> frameOrder,
		int frameCount,
		int skippedFrameCount)
	{
		List<string> lines =
		[
			$"DAT: {dat.FilePath}",
			$"Resource: {resource.Name}",
			$"Payload group: 0x{payloadGroup.StartOffset:X}..0x{payloadGroup.EndOffset:X}",
			$"Paired type-4 plane block: {planeBlock.Index:D2} -> {planeBlock.Value08}x{planeBlock.Value0C} @0x{planeBlock.Value24:X}",
			$"Type-2 crop blocks: {string.Join(", ", blockSummaries.Select(summary => summary.BlockIndex.ToString("D2")))}",
			$"Exported crop blocks: {blockSummaries.Count}",
			$"Sequence group: {(sequenceGroup is null ? "<none>" : $"0x{sequenceGroup.StartOffset:X}..0x{sequenceGroup.EndOffset:X} ({sequenceGroup.Segments.Count} segments)")}",
			$"Sequence bytes: {(frameOrder.Count == 0 ? "<none>" : string.Join(' ', frameOrder.Select(value => value.ToString("D2"))))}",
			$"Rendered frames: {frameCount}",
			$"Skipped frames: {skippedFrameCount}",
			"Descriptor note: loader type-2 blocks are explicitly skipped by the DAT loader fixup switch in FUN_0002A630(), so this export treats the block records themselves as rectangle descriptors rather than rebased pointer-backed payloads.",
			"Descriptor note: the current LEVINFO proof is 00/04 = crop origin and 08/0C = crop size. In most blocks 18/1C repeat the same size; a small LEVINFO1 tail block leaves 18/1C as zero, so this exporter treats that secondary pair as optional rather than authoritative.",
			"Scope note: this path is currently limited to LEVINFO0/1/2. It does not yet claim that every loader type-2 family in the game uses the same descriptor contract.",
		];

		DatPaletteHelper.AppendMetadata(lines, dat, paletteContext, paletteFailureReason);
		lines.Add(string.Empty);
		lines.Add("Crop blocks:");
		foreach (Type2CropBlockSummary summary in blockSummaries)
		{
			lines.Add($"[{summary.BlockIndex:D2}] x={summary.Left} y={summary.Top} size={summary.Width}x{summary.Height} raw24=0x{summary.Value24:X8} file={Path.GetFileName(summary.OutputPath)}");
		}

		File.WriteAllLines(outputPath, lines);
	}

	private sealed record Type2CropDescriptor(int Left, int Top, int Width, int Height);
}

internal sealed record Type2CropResourceExportResult(
	string ResourceName,
	string OutputDirectory,
	int BlockCount,
	int FrameCount,
	int SkippedFrameCount,
	string DefaultPaletteSummary,
	IReadOnlyList<string> ExportedPaletteVariants);

internal sealed record Type2CropBlockSummary(
	int BlockIndex,
	int Left,
	int Top,
	int Width,
	int Height,
	int Value24,
	string OutputPath);
