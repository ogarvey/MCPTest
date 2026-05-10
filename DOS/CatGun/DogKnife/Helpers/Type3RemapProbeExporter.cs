using System.Buffers.Binary;
using DogKnife.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DogKnife.Helpers;

internal static class Type3RemapProbeExporter
{
	private const int LookupPageSize = 0x100;
	private const int RuntimeStride = 0x180;

	public static Type3RemapProbeExportResult Export(CatGunDat dat, string resourceName, string outputRoot)
	{
		DatResourceEntry resource = dat.Resources.SingleOrDefault(candidate =>
			string.Equals(candidate.Name, resourceName, StringComparison.Ordinal))
			?? throw new InvalidDataException($"{resourceName} resource was not found in the DAT resource table.");

		DatPayloadGroup payloadGroup = dat.PayloadGroups.SingleOrDefault(group => group.StartOffset == resource.Pointer04)
			?? throw new InvalidDataException($"{resourceName} payload group was not found for resource field +0x04.");

		DatSequenceGroup? sequenceGroup = resource.Pointer08 == 0
			? null
			: dat.SequenceGroups.SingleOrDefault(group => group.StartOffset == resource.Pointer08);

		List<DatPayloadBlock30> type3Blocks = payloadGroup.Blocks
			.Where(block => block.LoaderType == 3)
			.ToList();

		if (type3Blocks.Count == 0)
		{
			string loaderTypes = FormatLoaderTypeDistribution(payloadGroup.Blocks);
			throw new NotSupportedException(
				$"{resourceName} does not expose any loader type-3 blocks in its current payload group. Parsed loader types: {loaderTypes}.");
		}

		ReadOnlySpan<byte> bytes = dat.RawBytes.Span;
		string familyRoot = Path.Combine(Path.GetFullPath(outputRoot), resourceName, "type3_probe");
		string masksDirectory = Path.Combine(familyRoot, "coverage_masks");
		string pagesDirectory = Path.Combine(familyRoot, "lookup_pages");
		Directory.CreateDirectory(masksDirectory);
		Directory.CreateDirectory(pagesDirectory);

		Dictionary<int, string> lookupPagePaths = new();
		List<Type3RemapBlockSummary> blockSummaries = new(type3Blocks.Count);
		List<int> skippedBlockIndices = payloadGroup.Blocks
			.Where(block => block.LoaderType != 3)
			.Select(block => block.Index)
			.ToList();

		foreach (DatPayloadBlock30 block in type3Blocks)
		{
			Type3RemapStream stream = ParseStream(bytes, block.Value24);
			Type3CoverageMask coverageMask = BuildCoverageMask(stream);

			string maskPath = Path.Combine(masksDirectory, $"block_{block.Index:D2}_mask.png");
			SaveCoverageMask(maskPath, coverageMask);

			int lookupPageOffset = block.Value28 & ~0xFF;
			if (!lookupPagePaths.ContainsKey(lookupPageOffset))
			{
				ReadOnlySpan<byte> lookupPage = GetLookupPage(bytes, lookupPageOffset);
				string lookupPageBasePath = Path.Combine(pagesDirectory, $"lookup_page_{lookupPageOffset:X}");
				File.WriteAllBytes($"{lookupPageBasePath}.bin", lookupPage.ToArray());
				SaveLookupPageImage($"{lookupPageBasePath}.png", lookupPage);
				lookupPagePaths.Add(lookupPageOffset, $"lookup_page_{lookupPageOffset:X}");
			}

			blockSummaries.Add(new Type3RemapBlockSummary(
				BlockIndex: block.Index,
				DeclaredWidth: block.Value08,
				DeclaredHeight: block.Value0C,
				StreamOffset: block.Value24,
				LookupPageOffset: lookupPageOffset,
				SegmentCount: stream.SegmentCount,
				CoveredPixelCount: stream.CoveredPixelCount,
				RelativeEndOffset: stream.RelativeEndOffset,
				MinX: coverageMask.MinX,
				MinY: coverageMask.MinY,
				MaxX: coverageMask.MaxX,
				MaxY: coverageMask.MaxY,
				MaskWidth: coverageMask.Width,
				MaskHeight: coverageMask.Height,
				MaskPath: maskPath));
		}

		WriteMetadata(
			Path.Combine(familyRoot, "metadata.txt"),
			dat,
			resource,
			payloadGroup,
			sequenceGroup,
			blockSummaries,
			skippedBlockIndices);

		return new Type3RemapProbeExportResult(
			ResourceName: resourceName,
			OutputDirectory: familyRoot,
			BlockCount: blockSummaries.Count,
			UniqueLookupPageCount: lookupPagePaths.Count);
	}

	private static Type3RemapStream ParseStream(ReadOnlySpan<byte> bytes, int streamOffset)
	{
		if (streamOffset <= 0 || streamOffset + sizeof(ushort) > bytes.Length)
		{
			throw new InvalidDataException($"Type-3 remap stream offset is out of bounds: 0x{streamOffset:X}");
		}

		ushort segmentCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(streamOffset, sizeof(ushort)));
		int cursor = streamOffset + sizeof(ushort);
		int relativeOffset = 0;
		int coveredPixelCount = 0;
		List<Type3RemapSegment> segments = new(segmentCount);

		for (int segmentIndex = 0; segmentIndex < segmentCount; segmentIndex++)
		{
			if (cursor + (sizeof(ushort) * 2) > bytes.Length)
			{
				throw new InvalidDataException(
					$"Type-3 remap stream at 0x{streamOffset:X} ends before segment {segmentIndex}.");
			}

			ushort destinationSkip = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(cursor, sizeof(ushort)));
			ushort spanLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(cursor + sizeof(ushort), sizeof(ushort)));
			cursor += sizeof(ushort) * 2;

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
			coveredPixelCount += spanLength;
		}

		return new Type3RemapStream(
			StreamOffset: streamOffset,
			SegmentCount: segmentCount,
			CoveredPixelCount: coveredPixelCount,
			RelativeEndOffset: relativeOffset,
			Segments: segments);
	}

	private static ReadOnlySpan<byte> GetLookupPage(ReadOnlySpan<byte> bytes, int lookupPageOffset)
	{
		if (lookupPageOffset <= 0 || lookupPageOffset + LookupPageSize > bytes.Length)
		{
			throw new InvalidDataException($"Type-3 lookup page offset is out of bounds: 0x{lookupPageOffset:X}");
		}

		return bytes.Slice(lookupPageOffset, LookupPageSize);
	}

	private static Type3CoverageMask BuildCoverageMask(Type3RemapStream stream)
	{
		if (stream.CoveredPixelCount == 0)
		{
			return new Type3CoverageMask(
				Pixels: [0],
				MinX: 0,
				MinY: 0,
				MaxX: 0,
				MaxY: 0,
				Width: 1,
				Height: 1);
		}

		int surfaceHeight = Math.Max(1, (stream.RelativeEndOffset + RuntimeStride - 1) / RuntimeStride);
		byte[] pixels = new byte[RuntimeStride * surfaceHeight];
		int minX = RuntimeStride;
		int minY = int.MaxValue;
		int maxX = 0;
		int maxY = 0;

		foreach (Type3RemapSegment segment in stream.Segments)
		{
			for (int position = segment.RelativeStartOffset; position < segment.RelativeEndOffset; position++)
			{
				int x = position % RuntimeStride;
				int y = position / RuntimeStride;
				pixels[(y * RuntimeStride) + x] = 0xFF;
				minX = Math.Min(minX, x);
				minY = Math.Min(minY, y);
				maxX = Math.Max(maxX, x);
				maxY = Math.Max(maxY, y);
			}
		}

		return new Type3CoverageMask(
			Pixels: pixels,
			MinX: minX,
			MinY: minY,
			MaxX: maxX,
			MaxY: maxY,
			Width: (maxX - minX) + 1,
			Height: (maxY - minY) + 1);
	}

	private static void SaveCoverageMask(string outputPath, Type3CoverageMask mask)
	{
		using Image<Rgba32> image = new(mask.Width, mask.Height);

		image.ProcessPixelRows(accessor =>
		{
			for (int y = 0; y < mask.Height; y++)
			{
				Span<Rgba32> row = accessor.GetRowSpan(y);
				int sourceY = mask.MinY + y;

				for (int x = 0; x < mask.Width; x++)
				{
					int sourceX = mask.MinX + x;
					byte value = mask.Pixels[(sourceY * RuntimeStride) + sourceX];
					row[x] = value == 0
						? new Rgba32(0, 0, 0)
						: new Rgba32(255, 255, 255);
				}
			}
		});

		image.SaveAsPng(outputPath);
	}

	private static void SaveLookupPageImage(string outputPath, ReadOnlySpan<byte> lookupPage)
	{
		using Image<Rgba32> image = new(16, 16);
		byte[] values = lookupPage.ToArray();

		image.ProcessPixelRows(accessor =>
		{
			for (int y = 0; y < 16; y++)
			{
				Span<Rgba32> row = accessor.GetRowSpan(y);

				for (int x = 0; x < 16; x++)
				{
					byte value = values[(y * 16) + x];
					row[x] = new Rgba32(value, value, value);
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
		IReadOnlyList<Type3RemapBlockSummary> blockSummaries,
		IReadOnlyList<int> skippedBlockIndices)
	{
		List<byte> frameOrder = sequenceGroup?.Segments.SelectMany(segment => segment.Bytes).ToList() ?? [];
		List<string> lines =
		[
			$"DAT: {dat.FilePath}",
			$"Resource: {resource.Name}",
			$"Payload group: 0x{payloadGroup.StartOffset:X}..0x{payloadGroup.EndOffset:X}",
			$"Payload loader types: {FormatLoaderTypeDistribution(payloadGroup.Blocks)}",
			$"Exported type-3 blocks: {string.Join(", ", blockSummaries.Select(summary => summary.BlockIndex.ToString("D2")))}",
			$"Skipped non-type3 blocks: {(skippedBlockIndices.Count == 0 ? "<none>" : string.Join(", ", skippedBlockIndices.Select(index => index.ToString("D2"))))}",
			$"Sequence group: {(sequenceGroup is null ? "<none>" : $"0x{sequenceGroup.StartOffset:X}..0x{sequenceGroup.EndOffset:X}")}",
			$"Sequence bytes: {(frameOrder.Count == 0 ? "<none>" : string.Join(' ', frameOrder.Select(value => value.ToString("X2"))))}",
			$"Runtime stride assumption: 0x{RuntimeStride:X} bytes (from FUN_00013100/FUN_00013430 destination setup)",
			string.Empty,
			"Blocks:",
		];

		foreach (Type3RemapBlockSummary summary in blockSummaries)
		{
			lines.Add(
				$"[{summary.BlockIndex:D2}] declared={summary.DeclaredWidth}x{summary.DeclaredHeight} stream=0x{summary.StreamOffset:X} page=0x{summary.LookupPageOffset:X} segments={summary.SegmentCount} touched={summary.CoveredPixelCount} end=0x{summary.RelativeEndOffset:X} crop={summary.MaskWidth}x{summary.MaskHeight} bounds=({summary.MinX},{summary.MinY})..({summary.MaxX},{summary.MaxY}) mask={Path.GetFileName(summary.MaskPath)}");
		}

		File.WriteAllLines(outputPath, lines);
	}

	private static string FormatLoaderTypeDistribution(IEnumerable<DatPayloadBlock30> blocks)
	{
		return string.Join(
			", ",
			blocks
				.GroupBy(block => block.LoaderType)
				.OrderBy(group => group.Key)
				.Select(group => $"0x{group.Key:X2}:{group.Count()}"));
	}
}

internal sealed record Type3RemapProbeExportResult(
	string ResourceName,
	string OutputDirectory,
	int BlockCount,
	int UniqueLookupPageCount);

internal sealed record Type3RemapStream(
	int StreamOffset,
	int SegmentCount,
	int CoveredPixelCount,
	int RelativeEndOffset,
	IReadOnlyList<Type3RemapSegment> Segments);

internal sealed record Type3RemapSegment(
	int Index,
	int DestinationSkip,
	int SpanLength,
	int RelativeStartOffset,
	int RelativeEndOffset);

internal sealed record Type3CoverageMask(
	byte[] Pixels,
	int MinX,
	int MinY,
	int MaxX,
	int MaxY,
	int Width,
	int Height);

internal sealed record Type3RemapBlockSummary(
	int BlockIndex,
	int DeclaredWidth,
	int DeclaredHeight,
	int StreamOffset,
	int LookupPageOffset,
	int SegmentCount,
	int CoveredPixelCount,
	int RelativeEndOffset,
	int MinX,
	int MinY,
	int MaxX,
	int MaxY,
	int MaskWidth,
	int MaskHeight,
	string MaskPath);
