using DogKnife.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DogKnife.Helpers;

internal static class RawPlaneResourceExporter
{
	private const int PaletteColorCount = 256;
	private const int PaletteBankSize = PaletteColorCount * 3;

	public static RawPlaneResourceExportResult Export(CatGunDat dat, string resourceName, string outputRoot)
	{
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

		byte[] bytes = File.ReadAllBytes(dat.FilePath);
		int paletteRegionLength = dat.Header.LayerTableOffset - dat.Header.PaletteTableOffset;
		if (paletteRegionLength <= 0 || paletteRegionLength % PaletteBankSize != 0)
		{
			throw new InvalidDataException(
				$"{resourceName} export expects a palette region divisible by 0x{PaletteBankSize:X}. Actual length: 0x{paletteRegionLength:X}");
		}

		Rgba32[][] paletteBanks = ParsePaletteBanks(bytes, dat.Header.PaletteTableOffset, paletteRegionLength);
		int? probePaletteBank = TryGetSharedPaletteBank(payloadGroup.Blocks, paletteBanks.Length);

		string familyRoot = Path.Combine(Path.GetFullPath(outputRoot), resourceName);
		string grayscaleBlocksDirectory = Path.Combine(familyRoot, "grayscale", "blocks");
		string grayscaleFramesDirectory = Path.Combine(familyRoot, "grayscale", "frames");
		Directory.CreateDirectory(grayscaleBlocksDirectory);
		Directory.CreateDirectory(grayscaleFramesDirectory);

		string? paletteBlocksDirectory = null;
		string? paletteFramesDirectory = null;
		Rgba32[]? probePalette = null;

		if (probePaletteBank is int paletteBankIndex)
		{
			probePalette = paletteBanks[paletteBankIndex];
			paletteBlocksDirectory = Path.Combine(familyRoot, $"palette_bank_{paletteBankIndex:D2}", "blocks");
			paletteFramesDirectory = Path.Combine(familyRoot, $"palette_bank_{paletteBankIndex:D2}", "frames");
			Directory.CreateDirectory(paletteBlocksDirectory);
			Directory.CreateDirectory(paletteFramesDirectory);
		}

		foreach (DatPayloadBlock30 block in payloadGroup.Blocks)
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

			ReadOnlySpan<byte> indices = bytes.AsSpan(dataOffset, pixelCount);

			string grayscaleBlockPath = Path.Combine(
				grayscaleBlocksDirectory,
				$"block_{block.Index:D2}_{width}x{height}_data_{dataOffset:X}.png");
			SaveImage(grayscaleBlockPath, width, height, indices, palette: null);

			if (probePalette is not null && paletteBlocksDirectory is not null)
			{
				string paletteBlockPath = Path.Combine(
					paletteBlocksDirectory,
					$"block_{block.Index:D2}_{width}x{height}_data_{dataOffset:X}.png");
				SaveImage(paletteBlockPath, width, height, indices, probePalette);
			}
		}

		List<byte> frameOrder = sequenceGroup?.Segments.SelectMany(segment => segment.Bytes).ToList() ?? [];
		for (int frameIndex = 0; frameIndex < frameOrder.Count; frameIndex++)
		{
			byte blockIndex = frameOrder[frameIndex];
			if (blockIndex >= payloadGroup.Blocks.Count)
			{
				throw new InvalidDataException(
					$"{resourceName} sequence frame {frameIndex} references block {blockIndex}, but only {payloadGroup.Blocks.Count} blocks were parsed.");
			}

			DatPayloadBlock30 block = payloadGroup.Blocks[blockIndex];
			int width = block.Value08;
			int height = block.Value0C;
			int dataOffset = block.Value24;
			ReadOnlySpan<byte> indices = bytes.AsSpan(dataOffset, checked(width * height));

			string grayscaleFramePath = Path.Combine(
				grayscaleFramesDirectory,
				$"frame_{frameIndex:D2}_block_{blockIndex:D2}.png");
			SaveImage(grayscaleFramePath, width, height, indices, palette: null);

			if (probePalette is not null && paletteFramesDirectory is not null)
			{
				string paletteFramePath = Path.Combine(
					paletteFramesDirectory,
					$"frame_{frameIndex:D2}_block_{blockIndex:D2}.png");
				SaveImage(paletteFramePath, width, height, indices, probePalette);
			}
		}

		WriteMetadata(
			Path.Combine(familyRoot, "metadata.txt"),
			dat,
			resource,
			payloadGroup,
			sequenceGroup,
			paletteBanks.Length,
			probePaletteBank,
			frameOrder);

		return new RawPlaneResourceExportResult(
			ResourceName: resourceName,
			OutputDirectory: familyRoot,
			BlockCount: payloadGroup.Blocks.Count,
			FrameCount: frameOrder.Count,
			PaletteBankCount: paletteBanks.Length,
			ProbePaletteBank: probePaletteBank);
	}

	private static Rgba32[][] ParsePaletteBanks(byte[] bytes, int paletteTableOffset, int paletteRegionLength)
	{
		int paletteBankCount = paletteRegionLength / PaletteBankSize;
		Rgba32[][] banks = new Rgba32[paletteBankCount][];

		for (int bankIndex = 0; bankIndex < paletteBankCount; bankIndex++)
		{
			int bankOffset = paletteTableOffset + (bankIndex * PaletteBankSize);
			Rgba32[] colors = new Rgba32[PaletteColorCount];

			for (int colorIndex = 0; colorIndex < PaletteColorCount; colorIndex++)
			{
				int colorOffset = bankOffset + (colorIndex * 3);
				colors[colorIndex] = new Rgba32(
					ExpandVgaColor(bytes[colorOffset + 0]),
					ExpandVgaColor(bytes[colorOffset + 1]),
					ExpandVgaColor(bytes[colorOffset + 2]));
			}

			banks[bankIndex] = colors;
		}

		return banks;
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

	private static byte ExpandVgaColor(byte value)
	{
		return (byte)((value * 255) / 63);
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
		int paletteBankCount,
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
			$"Palette banks: {paletteBankCount}",
			$"Probe palette bank: {(probePaletteBank is null ? "<none>" : probePaletteBank.Value.ToString())}",
			string.Empty,
			"Blocks:",
		];

		foreach (DatPayloadBlock30 block in payloadGroup.Blocks)
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
	int? ProbePaletteBank);
