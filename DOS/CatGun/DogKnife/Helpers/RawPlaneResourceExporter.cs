using DogKnife.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DogKnife.Helpers;

internal static class RawPlaneResourceExporter
{
	private static readonly HashSet<string> SupportedResourceNames = new(StringComparer.Ordinal)
	{
		"DISPLAY",
		"HALLBACK",
		"INTROBCK",
		"LETTERS",
		"PAWBACK",
		"LEVINFO0",
		"LEVINFO1",
		"LEVINFO2",
		"TEXTURE_FRAMES",
	};

	public static bool SupportsResource(string resourceName)
	{
		return SupportedResourceNames.Contains(resourceName);
	}

	public static RawPlaneResourceExportResult Export(CatGunDat dat, string resourceName, string outputRoot)
	{
		if (string.Equals(resourceName, "TEXTURE", StringComparison.Ordinal))
		{
			throw new InvalidOperationException(
				"TEXTURE has a dedicated exporter. Use --export-textures instead of --export-resource-planes.");
		}

		if (!SupportedResourceNames.Contains(resourceName))
		{
			throw new NotSupportedException(
				$"{resourceName} is not supported by --export-resource-planes. The exact type-4 plane path is currently limited to validated direct indexed-plane families such as DISPLAY, INTROBCK, HALLBACK, PAWBACK, TEXTURE_FRAMES, LETTERS, and LEVINFO0/1/2. Other families like PAW still stage payloads through queue helpers rather than a proven raw width*height indexed-plane path.");
		}

		DatResourceEntry resource = dat.Resources.SingleOrDefault(resource =>
			string.Equals(resource.Name, resourceName, StringComparison.Ordinal))
			?? throw new InvalidDataException($"{resourceName} resource was not found in the DAT resource table.");

		DatPayloadGroup payloadGroup = dat.PayloadGroups.SingleOrDefault(group => group.StartOffset == resource.Pointer04)
			?? throw new InvalidDataException($"{resourceName} payload group was not found for resource field +0x04.");

		DatSequenceGroup? sequenceGroup = resource.Pointer08 == 0
			? null
			: dat.SequenceGroups.SingleOrDefault(group => group.StartOffset == resource.Pointer08);

		if (payloadGroup.Blocks.Count == 0)
		{
			throw new InvalidDataException($"{resourceName} payload group does not contain any 0x30-byte blocks.");
		}

		List<DatPayloadBlock30> type4Blocks = payloadGroup.Blocks
			.Where(block => block.LoaderType == 4)
			.ToList();

		if (type4Blocks.Count == 0)
		{
			throw new InvalidDataException($"{resourceName} does not expose any loader type-4 blocks in its current payload group.");
		}

		ReadOnlySpan<byte> bytes = dat.RawBytes.Span;
		int? probePaletteBank = TryGetSharedPaletteBank(type4Blocks, GetPaletteBankCount(dat));
		IEnumerable<ExportPaletteVariant>? additionalVariants = probePaletteBank is int probePaletteBankIndex
			? [new ExportPaletteVariant(probePaletteBankIndex, $"palette_bank_{probePaletteBankIndex:D2}_block_value20", "Shared high byte of block value20 across exact type-4 plane blocks.")]
			: null;

		if (!DatPaletteHelper.TryCreateContext(dat, out DatPaletteContext? paletteContext, out string? paletteFailureReason, additionalVariants))
		{
			throw new InvalidDataException(
				$"{resourceName} export expects a valid palette region. {paletteFailureReason}");
		}

		string familyRoot = Path.Combine(Path.GetFullPath(outputRoot), resourceName, "type4_plane");
		DatPaletteHelper.ExportPaletteBankImages(familyRoot, paletteContext!);

		string defaultBlocksDirectory = Path.Combine(familyRoot, DatPaletteHelper.DefaultDirectoryName, "blocks");
		string defaultFramesDirectory = Path.Combine(familyRoot, DatPaletteHelper.DefaultDirectoryName, "frames");
		string grayscaleBlocksDirectory = Path.Combine(familyRoot, "grayscale", "blocks");
		string grayscaleFramesDirectory = Path.Combine(familyRoot, "grayscale", "frames");
		Directory.CreateDirectory(defaultBlocksDirectory);
		Directory.CreateDirectory(defaultFramesDirectory);
		Directory.CreateDirectory(grayscaleBlocksDirectory);
		Directory.CreateDirectory(grayscaleFramesDirectory);
		Rgba32[]? defaultPalette = paletteContext!.DefaultPalette;

		foreach (ExportPaletteVariant paletteVariant in paletteContext.Variants)
		{
			Directory.CreateDirectory(Path.Combine(familyRoot, paletteVariant.DirectoryName, "blocks"));
			Directory.CreateDirectory(Path.Combine(familyRoot, paletteVariant.DirectoryName, "frames"));
		}

		Dictionary<int, IndexedPlaneBlock> renderedBlocks = new(type4Blocks.Count);

		foreach (DatPayloadBlock30 block in type4Blocks)
		{
			int width = block.Value08;
			int height = block.Value0C;
			int dataOffset = block.Value24;

			if (width <= 0 || height <= 0)
			{
				throw new InvalidDataException(
					$"{resourceName} block {block.Index} has invalid dimensions {width}x{height}.");
			}

			int pixelCount = checked(width * height);
			if (dataOffset < 0 || dataOffset + pixelCount > bytes.Length)
			{
				throw new InvalidDataException(
					$"{resourceName} block {block.Index} pixel data exceeds file bounds: offset=0x{dataOffset:X}, size=0x{pixelCount:X}");
			}

			ReadOnlySpan<byte> indices = bytes.Slice(dataOffset, pixelCount);
			renderedBlocks.Add(block.Index, new IndexedPlaneBlock(block.Index, width, height, dataOffset));

			string defaultBlockPath = Path.Combine(
				defaultBlocksDirectory,
				$"block_{block.Index:D2}_{width}x{height}_data_{dataOffset:X}.png");
			string grayscaleBlockPath = Path.Combine(
				grayscaleBlocksDirectory,
				$"block_{block.Index:D2}_{width}x{height}_data_{dataOffset:X}.png");
			SaveImage(defaultBlockPath, width, height, indices, defaultPalette);
			SaveImage(grayscaleBlockPath, width, height, indices, palette: null);

			foreach (ExportPaletteVariant paletteVariant in paletteContext.Variants)
			{
				string paletteBlockPath = Path.Combine(
					familyRoot,
					paletteVariant.DirectoryName,
					"blocks",
					$"block_{block.Index:D2}_{width}x{height}_data_{dataOffset:X}.png");
				SaveImage(paletteBlockPath, width, height, indices, paletteContext.Banks[paletteVariant.BankIndex]);
			}
		}

		List<byte> frameOrder = sequenceGroup?.Segments.SelectMany(segment => segment.Bytes).ToList() ?? [];
		int frameCount = 0;
		int skippedFrameCount = 0;

		for (int frameIndex = 0; frameIndex < frameOrder.Count; frameIndex++)
		{
			byte blockIndex = frameOrder[frameIndex];
			if (!renderedBlocks.TryGetValue(blockIndex, out IndexedPlaneBlock? block))
			{
				skippedFrameCount++;
				continue;
			}

			if (blockIndex >= payloadGroup.Blocks.Count)
			{
				throw new InvalidDataException(
					$"{resourceName} sequence frame {frameIndex} references block {blockIndex}, but only {payloadGroup.Blocks.Count} blocks were parsed.");
			}

			ReadOnlySpan<byte> indices = bytes.Slice(block.DataOffset, checked(block.Width * block.Height));

			string defaultFramePath = Path.Combine(
				defaultFramesDirectory,
				$"frame_{frameIndex:D2}_block_{blockIndex:D2}.png");
			string grayscaleFramePath = Path.Combine(
				grayscaleFramesDirectory,
				$"frame_{frameIndex:D2}_block_{blockIndex:D2}.png");
			SaveImage(defaultFramePath, block.Width, block.Height, indices, defaultPalette);
			SaveImage(grayscaleFramePath, block.Width, block.Height, indices, palette: null);

			foreach (ExportPaletteVariant paletteVariant in paletteContext.Variants)
			{
				string paletteFramePath = Path.Combine(
					familyRoot,
					paletteVariant.DirectoryName,
					"frames",
					$"frame_{frameIndex:D2}_block_{blockIndex:D2}.png");
				SaveImage(paletteFramePath, block.Width, block.Height, indices, paletteContext.Banks[paletteVariant.BankIndex]);
			}

			frameCount++;
		}

		WriteMetadata(
			Path.Combine(familyRoot, "metadata.txt"),
			dat,
			resource,
			payloadGroup,
			type4Blocks,
			sequenceGroup,
			paletteContext!,
			probePaletteBank,
			frameCount,
			skippedFrameCount,
			frameOrder);

		return new RawPlaneResourceExportResult(
			ResourceName: resourceName,
			OutputDirectory: familyRoot,
			BlockCount: type4Blocks.Count,
			FrameCount: frameCount,
			SkippedFrameCount: skippedFrameCount,
			PaletteBankCount: paletteContext!.PaletteBankCount,
			ProbePaletteBank: probePaletteBank,
			DefaultPaletteSummary: paletteContext.DefaultPaletteSummary,
			ExportedPaletteVariants: paletteContext.Variants.Select(variant => $"{variant.DirectoryName}=bank{variant.BankIndex:D2}").ToArray());
	}

	private static int GetPaletteBankCount(CatGunDat dat)
	{
		int paletteRegionLength = dat.Header.LayerTableOffset - dat.Header.PaletteTableOffset;
		return paletteRegionLength <= 0 ? 0 : paletteRegionLength / (256 * 3);
	}

	private static int? TryGetSharedPaletteBank(IReadOnlyList<DatPayloadBlock30> blocks, int paletteBankCount)
	{
		int? paletteBank = null;

		foreach (DatPayloadBlock30 block in blocks)
		{
			int candidate = (block.Value20 >> 16) & 0xFF;
			if (candidate < 0 || candidate >= paletteBankCount)
			{
				return null;
			}

			if (paletteBank is null)
			{
				paletteBank = candidate;
				continue;
			}

			if (paletteBank.Value != candidate)
			{
				return null;
			}
		}

		return paletteBank;
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

		image.SaveAsPng(outputPath);
	}

	private static void WriteMetadata(
		string outputPath,
		CatGunDat dat,
		DatResourceEntry resource,
		DatPayloadGroup payloadGroup,
		IReadOnlyList<DatPayloadBlock30> type4Blocks,
		DatSequenceGroup? sequenceGroup,
		DatPaletteContext paletteContext,
		int? probePaletteBank,
		int frameCount,
		int skippedFrameCount,
		IReadOnlyList<byte> frameOrder)
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
			$"Exact type-4 block count: {type4Blocks.Count}",
			$"Sequence group: {(sequenceGroup is null ? "<none>" : $"0x{sequenceGroup.StartOffset:X}..0x{sequenceGroup.EndOffset:X}")}",
			$"Frame order: {(frameOrder.Count == 0 ? "<none>" : string.Join(' ', frameOrder.Select(value => value.ToString("X2"))))}",
			$"Exported type-4 frames: {frameCount}",
			$"Skipped non-type-4 frames: {skippedFrameCount}",
			$"Probe palette bank: {(probePaletteBank is null ? "<none>" : probePaletteBank.Value.ToString())}",
			"Exact plane note: this path exports only loader type-4 width*height indexed planes proven by the raw DAT payloads; sequence entries that point at non-type-4 blocks are skipped rather than guessed.",
		];

		DatPaletteHelper.AppendMetadata(lines, dat, paletteContext, paletteFailureReason: null);
		lines.AddRange(
		[
			string.Empty,
			"Type-4 blocks:",
		]);

		foreach (DatPayloadBlock30 block in type4Blocks)
		{
			lines.Add(
				$"[{block.Index:D2}] size={block.Value08}x{block.Value0C} data=0x{block.Value24:X} value20=0x{block.Value20:X8} value00=0x{block.Value00:X8} value04=0x{block.Value04:X8}");
		}

		File.WriteAllLines(outputPath, lines);
	}
}

internal sealed record RawPlaneResourceExportResult(
	string ResourceName,
	string OutputDirectory,
	int BlockCount,
	int FrameCount,
	int PaletteBankCount,
	int SkippedFrameCount,
	int? ProbePaletteBank,
	string DefaultPaletteSummary,
	IReadOnlyList<string> ExportedPaletteVariants);

internal sealed record IndexedPlaneBlock(
	int BlockIndex,
	int Width,
	int Height,
	int DataOffset);
