using DogKnife.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DogKnife.Helpers;

internal static class TextureExporter
{
	public static TextureExportResult Export(CatGunDat dat, string outputRoot)
	{
		DatResourceEntry resource = dat.Resources.SingleOrDefault(resource =>
			string.Equals(resource.Name, "TEXTURE", StringComparison.Ordinal))
			?? throw new InvalidDataException("TEXTURE resource was not found in the DAT resource table.");

		DatPayloadGroup payloadGroup = dat.PayloadGroups.SingleOrDefault(group => group.StartOffset == resource.Pointer04)
			?? throw new InvalidDataException("TEXTURE payload group was not found for resource field +0x04.");

		DatSequenceGroup? sequenceGroup = resource.Pointer08 == 0
			? null
			: dat.SequenceGroups.SingleOrDefault(group => group.StartOffset == resource.Pointer08);

		if (payloadGroup.Blocks.Count == 0)
		{
			throw new InvalidDataException("TEXTURE payload group does not contain any 0x30-byte blocks.");
		}

		byte[] bytes = File.ReadAllBytes(dat.FilePath);
		int? probePaletteBank = TryGetSharedPaletteBank(payloadGroup.Blocks, GetPaletteBankCount(dat));
		IEnumerable<ExportPaletteVariant>? additionalVariants = probePaletteBank is int paletteBankIndex
			? [new ExportPaletteVariant(paletteBankIndex, $"palette_bank_{paletteBankIndex:D2}_block_value20", "Shared high byte of block value20 across TEXTURE blocks (legacy heuristic).")]
			: null;

		if (!DatPaletteHelper.TryCreateContext(dat, out DatPaletteContext? paletteContext, out string? paletteFailureReason, additionalVariants))
		{
			throw new InvalidDataException(
				$"TEXTURE export expects a valid palette region. {paletteFailureReason}");
		}

		DatPaletteHelper.ExportPaletteBankImages(Path.Combine(Path.GetFullPath(outputRoot), "TEXTURE"), paletteContext!);

		string familyRoot = Path.Combine(Path.GetFullPath(outputRoot), "TEXTURE");
		string defaultBlocksDirectory = Path.Combine(familyRoot, DatPaletteHelper.DefaultDirectoryName, "blocks");
		string defaultFramesDirectory = Path.Combine(familyRoot, DatPaletteHelper.DefaultDirectoryName, "frames");
		string grayscaleBlocksDirectory = Path.Combine(familyRoot, "grayscale", "blocks");
		string grayscaleFramesDirectory = Path.Combine(familyRoot, "grayscale", "frames");
		Directory.CreateDirectory(defaultBlocksDirectory);
		Directory.CreateDirectory(defaultFramesDirectory);
		Directory.CreateDirectory(grayscaleBlocksDirectory);
		Directory.CreateDirectory(grayscaleFramesDirectory);
		Rgba32[]? defaultPalette = paletteContext!.DefaultPalette;

		foreach (ExportPaletteVariant paletteVariant in paletteContext!.Variants)
		{
			Directory.CreateDirectory(Path.Combine(familyRoot, paletteVariant.DirectoryName, "blocks"));
			Directory.CreateDirectory(Path.Combine(familyRoot, paletteVariant.DirectoryName, "frames"));
		}

		List<string> grayscaleBlockPaths = new(payloadGroup.Blocks.Count);

		foreach (DatPayloadBlock30 block in payloadGroup.Blocks)
		{
			int width = block.Value08;
			int height = block.Value0C;
			int dataOffset = block.Value24;

			if (width <= 0 || height <= 0)
			{
				throw new InvalidDataException(
					$"TEXTURE block {block.Index} has invalid dimensions {width}x{height}.");
			}

			int pixelCount = checked(width * height);
			if (dataOffset < 0 || dataOffset + pixelCount > bytes.Length)
			{
				throw new InvalidDataException(
					$"TEXTURE block {block.Index} pixel data exceeds file bounds: offset=0x{dataOffset:X}, size=0x{pixelCount:X}");
			}

			ReadOnlySpan<byte> indices = bytes.AsSpan(dataOffset, pixelCount);

			string grayscaleBlockPath = Path.Combine(
				grayscaleBlocksDirectory,
				$"block_{block.Index:D2}_{width}x{height}_data_{dataOffset:X}.png");
			string defaultBlockPath = Path.Combine(
				defaultBlocksDirectory,
				$"block_{block.Index:D2}_{width}x{height}_data_{dataOffset:X}.png");
			SaveImage(defaultBlockPath, width, height, indices, defaultPalette);
			SaveImage(grayscaleBlockPath, width, height, indices, palette: null);
			grayscaleBlockPaths.Add(grayscaleBlockPath);

			foreach (ExportPaletteVariant paletteVariant in paletteContext!.Variants)
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
		for (int frameIndex = 0; frameIndex < frameOrder.Count; frameIndex++)
		{
			byte blockIndex = frameOrder[frameIndex];
			if (blockIndex >= payloadGroup.Blocks.Count)
			{
				throw new InvalidDataException(
					$"TEXTURE sequence frame {frameIndex} references block {blockIndex}, but only {payloadGroup.Blocks.Count} blocks were parsed.");
			}

			DatPayloadBlock30 block = payloadGroup.Blocks[blockIndex];
			int width = block.Value08;
			int height = block.Value0C;
			int dataOffset = block.Value24;
			ReadOnlySpan<byte> indices = bytes.AsSpan(dataOffset, checked(width * height));

			string grayscaleFramePath = Path.Combine(
				grayscaleFramesDirectory,
				$"frame_{frameIndex:D2}_block_{blockIndex:D2}.png");
			string defaultFramePath = Path.Combine(
				defaultFramesDirectory,
				$"frame_{frameIndex:D2}_block_{blockIndex:D2}.png");
			SaveImage(defaultFramePath, width, height, indices, defaultPalette);
			SaveImage(grayscaleFramePath, width, height, indices, palette: null);

			foreach (ExportPaletteVariant paletteVariant in paletteContext!.Variants)
			{
				string paletteFramePath = Path.Combine(
					familyRoot,
					paletteVariant.DirectoryName,
					"frames",
					$"frame_{frameIndex:D2}_block_{blockIndex:D2}.png");
				SaveImage(paletteFramePath, width, height, indices, paletteContext.Banks[paletteVariant.BankIndex]);
			}
		}

		WriteMetadata(
			Path.Combine(familyRoot, "metadata.txt"),
			dat,
			resource,
			payloadGroup,
			sequenceGroup,
			paletteContext,
			probePaletteBank,
			frameOrder);

		return new TextureExportResult(
			OutputDirectory: familyRoot,
			BlockCount: payloadGroup.Blocks.Count,
			FrameCount: frameOrder.Count,
			PaletteBankCount: paletteContext.PaletteBankCount,
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
		DatSequenceGroup? sequenceGroup,
		DatPaletteContext paletteContext,
		int? probePaletteBank,
		IReadOnlyList<byte> frameOrder)
	{
		List<string> lines =
		[
			$"DAT: {dat.FilePath}",
			$"Resource: {resource.Name}",
			$"Payload group: 0x{payloadGroup.StartOffset:X}..0x{payloadGroup.EndOffset:X}",
			$"Block count: {payloadGroup.Blocks.Count}",
			$"Sequence group: {(sequenceGroup is null ? "<none>" : $"0x{sequenceGroup.StartOffset:X}..0x{sequenceGroup.EndOffset:X}")}",
			$"Frame order: {(frameOrder.Count == 0 ? "<none>" : string.Join(' ', frameOrder.Select(value => value.ToString("X2"))))}",
			$"Probe palette bank: {(probePaletteBank is null ? "<none>" : probePaletteBank.Value.ToString())}",
		];

		DatPaletteHelper.AppendMetadata(lines, dat, paletteContext, paletteFailureReason: null);
		lines.AddRange(
		[
			string.Empty,
			"Blocks:",
		]);

		foreach (DatPayloadBlock30 block in payloadGroup.Blocks)
		{
			lines.Add(
				$"[{block.Index:D2}] size={block.Value08}x{block.Value0C} data=0x{block.Value24:X} value20=0x{block.Value20:X8} value00=0x{block.Value00:X8} value04=0x{block.Value04:X8}");
		}

		File.WriteAllLines(outputPath, lines);
	}
}

internal sealed record TextureExportResult(
	string OutputDirectory,
	int BlockCount,
	int FrameCount,
	int PaletteBankCount,
	int? ProbePaletteBank,
	string DefaultPaletteSummary,
	IReadOnlyList<string> ExportedPaletteVariants);
