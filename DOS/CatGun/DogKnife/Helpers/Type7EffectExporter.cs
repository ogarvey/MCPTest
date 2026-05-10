using System.Buffers.Binary;
using DogKnife.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DogKnife.Helpers;

internal static class Type7EffectExporter
{
	private const int LeaderRuntimeStride = 0x180;
	private const int LeaderHeaderSize = 0x80;
	private const int RuntimeWidth = 320;
	private const int RuntimeHeight = 200;
	private const int RuntimeStride = 320;
	private const int RecordCount = 0x1000;
	private const int RecordSize = 10;
	private const int ExactCycleFrameCount = 256;
	private static readonly HashSet<string> SupportedResourceNames = new(StringComparer.Ordinal)
	{
		"LEADER",
		"REACTOR",
	};

	public static bool SupportsResource(string resourceName)
	{
		return SupportedResourceNames.Contains(resourceName);
	}

	public static Type7EffectExportResult Export(CatGunDat dat, string resourceName, string outputRoot)
	{
		if (string.Equals(resourceName, "LEADER", StringComparison.Ordinal))
		{
			return ExportLeaderScript(dat, outputRoot);
		}

		if (!SupportsResource(resourceName))
		{
			throw new NotSupportedException(
				$"{resourceName} is not supported by --export-type7-effects. The exact type-7 path is currently limited to REACTOR's block104 -> 14F90 -> FUN_0002A0CA() effect family and LEADER's FUN_00054FA0()/FUN_00054EB0() script render.");
		}

		DatResourceEntry resource = dat.Resources.SingleOrDefault(candidate =>
			string.Equals(candidate.Name, resourceName, StringComparison.Ordinal))
			?? throw new InvalidDataException($"{resourceName} resource was not found in the DAT resource table.");

		DatPayloadGroup payloadGroup = dat.PayloadGroups.SingleOrDefault(group => group.StartOffset == resource.Pointer04)
			?? throw new InvalidDataException($"{resourceName} payload group was not found for resource field +0x04.");

		List<DatPayloadBlock30> type7Blocks = payloadGroup.Blocks
			.Where(block => block.LoaderType == 7)
			.ToList();

		if (type7Blocks.Count == 0)
		{
			throw new InvalidDataException($"{resourceName} does not expose any loader type-7 blocks in its current payload group.");
		}

		ReadOnlySpan<byte> bytes = dat.RawBytes.Span;
		string familyRoot = Path.Combine(Path.GetFullPath(outputRoot), resourceName, "type7_effect");
		string defaultBlocksDirectory = Path.Combine(familyRoot, DatPaletteHelper.DefaultDirectoryName, "blocks");
		string defaultFramesDirectory = Path.Combine(familyRoot, DatPaletteHelper.DefaultDirectoryName, "frames");
		string grayscaleBlocksDirectory = Path.Combine(familyRoot, "grayscale", "blocks");
		string grayscaleFramesDirectory = Path.Combine(familyRoot, "grayscale", "frames");
		Directory.CreateDirectory(defaultBlocksDirectory);
		Directory.CreateDirectory(defaultFramesDirectory);
		Directory.CreateDirectory(grayscaleBlocksDirectory);
		Directory.CreateDirectory(grayscaleFramesDirectory);

		DatPaletteHelper.TryCreateContext(dat, out DatPaletteContext? paletteContext, out string? paletteFailureReason);
		Rgba32[]? defaultPalette = paletteContext?.DefaultPalette;

		if (paletteContext is not null)
		{
			DatPaletteHelper.ExportPaletteBankImages(familyRoot, paletteContext);

			foreach (ExportPaletteVariant variant in paletteContext.Variants)
			{
				Directory.CreateDirectory(Path.Combine(familyRoot, variant.DirectoryName, "blocks"));
				Directory.CreateDirectory(Path.Combine(familyRoot, variant.DirectoryName, "frames"));
			}
		}

		List<Type7EffectBlockSummary> blockSummaries = new(type7Blocks.Count);

		foreach (DatPayloadBlock30 block in type7Blocks)
		{
			int dataSpan = GetBlockDataSpan(dat, block.Value24);
			int requiredByteCount = checked(RecordCount * RecordSize);

			if (dataSpan < requiredByteCount)
			{
				throw new InvalidDataException(
					$"Type-7 block {block.Index} only has 0x{dataSpan:X} bytes available at 0x{block.Value24:X}; FUN_0002A0CA() requires the exact 0x{requiredByteCount:X}-byte 4096-record region.");
			}

			ParticleRecordState[] records = ParseRecords(bytes.Slice(block.Value24, requiredByteCount));
			Type7CycleRender render = RenderExactCycle(records);

			string defaultCoveragePath = Path.Combine(
				defaultBlocksDirectory,
				$"block_{block.Index:D2}_cycle_coverage.png");
			string grayscaleCoveragePath = Path.Combine(
				grayscaleBlocksDirectory,
				$"block_{block.Index:D2}_cycle_coverage.png");
			DatPaletteHelper.SaveIndexedImage(defaultCoveragePath, render.CoveragePixels, render.Bounds.Left, render.Bounds.Top, render.Bounds.Width, render.Bounds.Height, RuntimeStride, defaultPalette);
			DatPaletteHelper.SaveIndexedImage(grayscaleCoveragePath, render.CoveragePixels, render.Bounds.Left, render.Bounds.Top, render.Bounds.Width, render.Bounds.Height, RuntimeStride, palette: null);

			if (paletteContext is not null)
			{
				foreach (ExportPaletteVariant variant in paletteContext.Variants)
				{
					string variantCoveragePath = Path.Combine(
						familyRoot,
						variant.DirectoryName,
						"blocks",
						$"block_{block.Index:D2}_cycle_coverage.png");
					DatPaletteHelper.SaveIndexedImage(variantCoveragePath, render.CoveragePixels, render.Bounds.Left, render.Bounds.Top, render.Bounds.Width, render.Bounds.Height, RuntimeStride, paletteContext.Banks[variant.BankIndex]);
				}
			}

			for (int frameIndex = 0; frameIndex < render.Frames.Count; frameIndex++)
			{
				IReadOnlyDictionary<int, byte> framePixels = render.Frames[frameIndex];
				string defaultFramePath = Path.Combine(
					defaultFramesDirectory,
					$"block_{block.Index:D2}_frame_{frameIndex:D3}.png");
				string grayscaleFramePath = Path.Combine(
					grayscaleFramesDirectory,
					$"block_{block.Index:D2}_frame_{frameIndex:D3}.png");
				DatPaletteHelper.SaveIndexedImage(defaultFramePath, framePixels, render.Bounds.Left, render.Bounds.Top, render.Bounds.Width, render.Bounds.Height, RuntimeStride, defaultPalette);
				DatPaletteHelper.SaveIndexedImage(grayscaleFramePath, framePixels, render.Bounds.Left, render.Bounds.Top, render.Bounds.Width, render.Bounds.Height, RuntimeStride, palette: null);

				if (paletteContext is not null)
				{
					foreach (ExportPaletteVariant variant in paletteContext.Variants)
					{
						string variantFramePath = Path.Combine(
							familyRoot,
							variant.DirectoryName,
							"frames",
							$"block_{block.Index:D2}_frame_{frameIndex:D3}.png");
						DatPaletteHelper.SaveIndexedImage(variantFramePath, framePixels, render.Bounds.Left, render.Bounds.Top, render.Bounds.Width, render.Bounds.Height, RuntimeStride, paletteContext.Banks[variant.BankIndex]);
					}
				}
			}

			blockSummaries.Add(new Type7EffectBlockSummary(
				BlockIndex: block.Index,
				DataOffset: block.Value24,
				DataSpan: dataSpan,
				DeclaredWidth: block.Value08,
				DeclaredHeight: block.Value0C,
				FrameCount: render.Frames.Count,
				PixelWritesPerFrame: render.MaxFramePixelCount,
				Bounds: render.Bounds));
		}

		WriteMetadata(
			Path.Combine(familyRoot, "metadata.txt"),
			dat,
			resource,
			payloadGroup,
			paletteContext,
			paletteFailureReason,
			blockSummaries);

		return new Type7EffectExportResult(
			ResourceName: resourceName,
			OutputDirectory: familyRoot,
			BlockCount: blockSummaries.Count,
			FrameCount: ExactCycleFrameCount,
			DefaultPaletteSummary: paletteContext?.DefaultPaletteSummary ?? $"{DatPaletteHelper.DefaultDirectoryName}/ falls back to grayscale; palette data unavailable.",
			ExportedPaletteVariants: paletteContext?.Variants.Select(variant => $"{variant.DirectoryName}=bank{variant.BankIndex:D2}").ToArray() ?? []);
	}

	private static Type7EffectExportResult ExportLeaderScript(CatGunDat dat, string outputRoot)
	{
		DatResourceEntry resource = dat.Resources.SingleOrDefault(candidate =>
			string.Equals(candidate.Name, "LEADER", StringComparison.Ordinal))
			?? throw new InvalidDataException("LEADER resource was not found in the DAT resource table.");

		DatPayloadGroup payloadGroup = dat.PayloadGroups.SingleOrDefault(group => group.StartOffset == resource.Pointer04)
			?? throw new InvalidDataException("LEADER payload group was not found for resource field +0x04.");

		DatPayloadBlock30 scriptBlock = payloadGroup.Blocks.SingleOrDefault(block => block.LoaderType == 7)
			?? throw new InvalidDataException("LEADER does not expose a loader type-7 block in its payload group.");

		ReadOnlySpan<byte> bytes = dat.RawBytes.Span;
		if ((uint)scriptBlock.Value24 >= (uint)bytes.Length)
		{
			throw new InvalidDataException($"LEADER type-7 data offset 0x{scriptBlock.Value24:X} is outside the DAT bounds.");
		}

		int width = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(scriptBlock.Value24 + 0x08, 2));
		int height = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(scriptBlock.Value24 + 0x0A, 2));
		int commandCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(scriptBlock.Value24 + 0x86, 2));

		if (width <= 0 || height <= 0)
		{
			throw new InvalidDataException($"LEADER script header reported invalid dimensions {width}x{height}.");
		}

		if (width > LeaderRuntimeStride)
		{
			throw new InvalidDataException($"LEADER script width {width} exceeds the recovered runtime stride 0x{LeaderRuntimeStride:X}.");
		}

		int cursor = scriptBlock.Value24 + 0x90;
		byte[] frameBuffer = new byte[LeaderRuntimeStride * height];
		Rgba32[]? scriptPalette = null;
		List<string> commandSummaries = new(commandCount);

		for (int commandIndex = 0; commandIndex < commandCount; commandIndex++)
		{
			if (bytes.Length - cursor < 6)
			{
				throw new InvalidDataException($"LEADER script command {commandIndex} header overruns the DAT bounds at 0x{cursor:X}.");
			}

			int commandSize = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(cursor + 0x00, 4));
			ushort opcode = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(cursor + 0x04, 2));
			if (commandSize < 6)
			{
				throw new InvalidDataException($"LEADER script command {commandIndex} has invalid size 0x{commandSize:X}.");
			}

			int payloadOffset = cursor + 0x06;
			int payloadLength = commandSize - 6;
			if (payloadLength < 0 || bytes.Length - payloadOffset < payloadLength)
			{
				throw new InvalidDataException($"LEADER script command {commandIndex} payload overruns the DAT bounds at 0x{payloadOffset:X}.");
			}

			ReadOnlySpan<byte> payload = bytes.Slice(payloadOffset, payloadLength);

			switch (opcode)
			{
				case 0x0B:
					scriptPalette = ParseLeaderPalette(payload);
					commandSummaries.Add($"[{commandIndex:D2}] offset=0x{cursor:X} size=0x{commandSize:X} opcode=0x0B full/segment palette upload");
					break;

				case 0x0F:
					DecodeLeaderRleFrame(payload, frameBuffer, width, height, LeaderRuntimeStride);
					commandSummaries.Add($"[{commandIndex:D2}] offset=0x{cursor:X} size=0x{commandSize:X} opcode=0x0F row-RLE image decode");
					break;

				case 0x10:
					DecodeLeaderRawFrame(payload, frameBuffer, width, height, LeaderRuntimeStride);
					commandSummaries.Add($"[{commandIndex:D2}] offset=0x{cursor:X} size=0x{commandSize:X} opcode=0x10 raw image copy");
					break;

				default:
					throw new NotSupportedException($"LEADER script command {commandIndex} uses unsupported opcode 0x{opcode:X2} at 0x{cursor:X}.");
			}

			cursor += commandSize;
		}

		string familyRoot = Path.Combine(Path.GetFullPath(outputRoot), resource.Name, "type7_script");
		string defaultDirectory = Path.Combine(familyRoot, DatPaletteHelper.DefaultDirectoryName);
		string grayscaleDirectory = Path.Combine(familyRoot, "grayscale");
		Directory.CreateDirectory(defaultDirectory);
		Directory.CreateDirectory(grayscaleDirectory);

		string defaultImagePath = Path.Combine(defaultDirectory, $"block_{scriptBlock.Index:D2}_{width}x{height}.png");
		string grayscaleImagePath = Path.Combine(grayscaleDirectory, $"block_{scriptBlock.Index:D2}_{width}x{height}.png");
		SaveLeaderImage(defaultImagePath, frameBuffer, width, height, LeaderRuntimeStride, scriptPalette);
		SaveLeaderImage(grayscaleImagePath, frameBuffer, width, height, LeaderRuntimeStride, palette: null);

		WriteLeaderMetadata(
			Path.Combine(familyRoot, "metadata.txt"),
			dat,
			resource,
			payloadGroup,
			scriptBlock,
			width,
			height,
			commandCount,
			scriptPalette is null
				? $"{DatPaletteHelper.DefaultDirectoryName}/ falls back to grayscale; no opcode 0x0B palette upload was encountered."
				: $"{DatPaletteHelper.DefaultDirectoryName}/ uses the exact LEADER script palette from opcode 0x0B.",
			commandSummaries);

		return new Type7EffectExportResult(
			ResourceName: resource.Name,
			OutputDirectory: familyRoot,
			BlockCount: 1,
			FrameCount: 1,
			DefaultPaletteSummary: scriptPalette is null
				? $"{DatPaletteHelper.DefaultDirectoryName}/ falls back to grayscale; no opcode 0x0B palette upload was encountered."
				: $"{DatPaletteHelper.DefaultDirectoryName}/ uses the exact LEADER script palette from opcode 0x0B.",
			ExportedPaletteVariants: []);
	}

	private static int GetBlockDataSpan(CatGunDat dat, int dataOffset)
	{
		int nextDataOffset = dat.Table40Blocks
			.SelectMany(GetCandidateDataOffsets)
			.Where(candidate => candidate > dataOffset)
			.DefaultIfEmpty(dat.RawBytes.Length)
			.Min();

		return nextDataOffset - dataOffset;
	}

	private static Rgba32[] ParseLeaderPalette(ReadOnlySpan<byte> payload)
	{
		if (payload.Length < 2)
		{
			throw new InvalidDataException("LEADER opcode 0x0B palette payload is truncated before the segment count.");
		}

		byte[] rawPalette = new byte[256 * 3];
		int segmentCount = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(0, 2));
		int payloadOffset = 2;
		int colorIndex = 0;

		for (int segmentIndex = 0; segmentIndex < segmentCount; segmentIndex++)
		{
			if (payload.Length - payloadOffset < 2)
			{
				throw new InvalidDataException($"LEADER opcode 0x0B palette segment {segmentIndex} is truncated before its skip/count bytes.");
			}

			colorIndex += payload[payloadOffset + 0];
			int colorCount = payload[payloadOffset + 1];
			if (colorCount == 0)
			{
				colorCount = 0x100;
			}
			payloadOffset += 2;

			if (colorIndex < 0 || colorIndex + colorCount > 0x100)
			{
				throw new InvalidDataException($"LEADER opcode 0x0B palette segment {segmentIndex} writes outside the 256-color palette.");
			}

			int byteCount = colorCount * 3;
			if (payload.Length - payloadOffset < byteCount)
			{
				throw new InvalidDataException($"LEADER opcode 0x0B palette segment {segmentIndex} is truncated before its {byteCount} color bytes.");
			}

			payload.Slice(payloadOffset, byteCount).CopyTo(rawPalette.AsSpan(colorIndex * 3, byteCount));
			payloadOffset += byteCount;
			colorIndex += colorCount;
		}

		Rgba32[] palette = new Rgba32[256];
		for (int index = 0; index < palette.Length; index++)
		{
			int rawOffset = index * 3;
			palette[index] = new Rgba32(
				ExpandVgaColor(rawPalette[rawOffset + 0]),
				ExpandVgaColor(rawPalette[rawOffset + 1]),
				ExpandVgaColor(rawPalette[rawOffset + 2]));
		}

		return palette;
	}

	private static void DecodeLeaderRleFrame(ReadOnlySpan<byte> payload, byte[] frameBuffer, int width, int height, int stride)
	{
		int payloadOffset = 0;

		for (int row = 0; row < height; row++)
		{
			if (payloadOffset >= payload.Length)
			{
				throw new InvalidDataException($"LEADER opcode 0x0F payload ended before row {row}.");
			}

			int segmentCount = payload[payloadOffset++];
			int rowOffset = row * stride;
			int written = 0;

			for (int segment = 0; segment < segmentCount; segment++)
			{
				if (payloadOffset >= payload.Length)
				{
					throw new InvalidDataException($"LEADER opcode 0x0F payload ended before row {row} segment {segment} control byte.");
				}

				sbyte control = unchecked((sbyte)payload[payloadOffset++]);
				if (control <= 0)
				{
					int copyCount = -control;
					if (payload.Length - payloadOffset < copyCount)
					{
						throw new InvalidDataException($"LEADER opcode 0x0F row {row} segment {segment} overruns the payload while copying {copyCount} literal bytes.");
					}

					if (written + copyCount > width)
					{
						throw new InvalidDataException($"LEADER opcode 0x0F row {row} literal segment exceeds the recovered width {width}.");
					}

					payload.Slice(payloadOffset, copyCount).CopyTo(frameBuffer.AsSpan(rowOffset + written, copyCount));
					payloadOffset += copyCount;
					written += copyCount;
				}
				else
				{
					if (payloadOffset >= payload.Length)
					{
						throw new InvalidDataException($"LEADER opcode 0x0F row {row} segment {segment} is missing its repeated byte.");
					}

					if (written + control > width)
					{
						throw new InvalidDataException($"LEADER opcode 0x0F row {row} repeat segment exceeds the recovered width {width}.");
					}

					frameBuffer.AsSpan(rowOffset + written, control).Fill(payload[payloadOffset++]);
					written += control;
				}
			}

			if (written != width)
			{
				throw new InvalidDataException($"LEADER opcode 0x0F row {row} wrote {written} bytes; expected exactly {width}.");
			}
		}
	}

	private static void DecodeLeaderRawFrame(ReadOnlySpan<byte> payload, byte[] frameBuffer, int width, int height, int stride)
	{
		int requiredByteCount = checked(width * height);
		if (payload.Length < requiredByteCount)
		{
			throw new InvalidDataException($"LEADER opcode 0x10 payload is only 0x{payload.Length:X} bytes; expected 0x{requiredByteCount:X} raw image bytes.");
		}

		int payloadOffset = 0;
		for (int row = 0; row < height; row++)
		{
			payload.Slice(payloadOffset, width).CopyTo(frameBuffer.AsSpan(row * stride, width));
			payloadOffset += width;
		}
	}

	private static void SaveLeaderImage(string outputPath, byte[] frameBuffer, int width, int height, int stride, Rgba32[]? palette)
	{
		using Image<Rgba32> image = new(width, height);

		image.ProcessPixelRows(accessor =>
		{
			for (int y = 0; y < height; y++)
			{
				Span<Rgba32> row = accessor.GetRowSpan(y);
				int sourceOffset = y * stride;

				for (int x = 0; x < width; x++)
				{
					byte index = frameBuffer[sourceOffset + x];
					row[x] = palette is null
						? new Rgba32(index, index, index, 255)
						: palette[index];
				}
			}
		});

		image.SaveAsPng(outputPath);
	}

	private static void WriteLeaderMetadata(
		string outputPath,
		CatGunDat dat,
		DatResourceEntry resource,
		DatPayloadGroup payloadGroup,
		DatPayloadBlock30 scriptBlock,
		int width,
		int height,
		int commandCount,
		string defaultPaletteSummary,
		IReadOnlyList<string> commandSummaries)
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
			$"Type-7 block index: {scriptBlock.Index}",
			$"Type-7 data offset: 0x{scriptBlock.Value24:X}",
			$"Recovered script image size: {width}x{height}",
			$"Recovered runtime stride: 0x{LeaderRuntimeStride:X}",
			$"Recovered command count: {commandCount}",
			$"Default export palette: {defaultPaletteSummary}",
			"Type-7 script note: FUN_00036F40() loads LEADER block.Value24 into FUN_00054FA0(), which copies an 0x80-byte header and then drives FUN_00054EB0() over the command table at data+0x90 with stride 0x180.",
			"Type-7 script note: the recovered LEADER block uses opcode 0x0B for a full 256-color VGA palette upload and opcode 0x0F for a 320x200 row-RLE decode, so the exported PNG is the exact static script result under the current known path.",
			string.Empty,
			"Commands:",
		];

		lines.AddRange(commandSummaries);
		File.WriteAllLines(outputPath, lines);
	}

	private static byte ExpandVgaColor(byte value)
	{
		return (byte)((value * 255) / 63);
	}

	private static IEnumerable<int> GetCandidateDataOffsets(DatPayloadBlock30 block)
	{
		if (block.Value24 > 0)
		{
			yield return block.Value24;
		}

		if (block.Value28 > 0)
		{
			yield return block.Value28;
		}
	}

	private static ParticleRecordState[] ParseRecords(ReadOnlySpan<byte> bytes)
	{
		ParticleRecordState[] records = new ParticleRecordState[RecordCount];

		for (int recordIndex = 0; recordIndex < RecordCount; recordIndex++)
		{
			int offset = recordIndex * RecordSize;
			records[recordIndex] = new ParticleRecordState(
				X: BitConverter.ToUInt16(bytes.Slice(offset + 0, 2)),
				Y: BitConverter.ToUInt16(bytes.Slice(offset + 2, 2)),
				DeltaX: BitConverter.ToInt16(bytes.Slice(offset + 4, 2)),
				DeltaY: BitConverter.ToInt16(bytes.Slice(offset + 6, 2)),
				ColorIndex: bytes[offset + 8],
				DelayCounter: bytes[offset + 9]);
		}

		return records;
	}

	private static Type7CycleRender RenderExactCycle(ParticleRecordState[] initialRecords)
	{
		ParticleRecordState[] records = initialRecords.Select(record => record with { }).ToArray();
		List<IReadOnlyDictionary<int, byte>> frames = new(ExactCycleFrameCount);
		Dictionary<int, byte> coveragePixels = [];
		Rect bounds = Rect.Empty;
		int maxFramePixelCount = 0;

		for (int frameIndex = 0; frameIndex < ExactCycleFrameCount; frameIndex++)
		{
			Dictionary<int, byte> framePixels = [];

			for (int recordIndex = 0; recordIndex < records.Length; recordIndex++)
			{
				ParticleRecordState record = records[recordIndex];
				byte decrementedDelay = unchecked((byte)(record.DelayCounter - 1));

				if (decrementedDelay > 0)
				{
					records[recordIndex] = record with { DelayCounter = decrementedDelay };
					continue;
				}

				if (record.X >= 0xA000 || record.Y >= 0x6400)
				{
					throw new InvalidDataException(
						$"Type-7 exact first-cycle export hit the RNG reset path unexpectedly at frame {frameIndex} with record {recordIndex}. The current exporter is limited to the deterministic pre-reset window.");
				}

				int pixelX = record.X >> 7;
				int pixelY = record.Y >> 7;
				if ((uint)pixelX >= RuntimeWidth || (uint)pixelY >= RuntimeHeight)
				{
					throw new InvalidDataException(
						$"Type-7 record {recordIndex} resolved to out-of-bounds pixel ({pixelX}, {pixelY}) during the deterministic first cycle.");
				}

				int offset = (pixelY * RuntimeStride) + pixelX;
				framePixels[offset] = record.ColorIndex;
				coveragePixels[offset] = record.ColorIndex;
				bounds = bounds.Include(pixelX, pixelY);

				ushort nextX = unchecked((ushort)(record.X + record.DeltaX));
				ushort nextY = unchecked((ushort)(record.Y + record.DeltaY));
				short nextDeltaY = unchecked((short)(record.DeltaY + 5));
				records[recordIndex] = record with
				{
					X = nextX,
					Y = nextY,
					DeltaY = nextDeltaY,
					DelayCounter = decrementedDelay,
				};
			}

			maxFramePixelCount = Math.Max(maxFramePixelCount, framePixels.Count);
			frames.Add(framePixels);
		}

		if (bounds.IsEmpty)
		{
			throw new InvalidDataException("Type-7 effect export produced no visible pixels during the deterministic first cycle.");
		}

		return new Type7CycleRender(frames, coveragePixels, bounds, maxFramePixelCount);
	}

	private static void WriteMetadata(
		string outputPath,
		CatGunDat dat,
		DatResourceEntry resource,
		DatPayloadGroup payloadGroup,
		DatPaletteContext? paletteContext,
		string? paletteFailureReason,
		IReadOnlyList<Type7EffectBlockSummary> blockSummaries)
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
			$"Exact type-7 block count: {blockSummaries.Count}",
			$"Exact deterministic cycle frames: {ExactCycleFrameCount}",
			"Type-7 effect note: REACTOR state 0x14CA0 reaches 0x14F24, which loads block104.Value24 into a child object handled by 0x14F90; 0x14F90 then calls FUN_0002A0CA() on that pointer.",
			"Type-7 effect note: FUN_0002A0CA() consumes 4096 fixed-size 10-byte records from the 0xA000-byte region at block.Value24, so the deterministic first 256-frame cycle covers each record exactly once before any record can fire a second time.",
			"Type-7 effect note: the exported 256-frame window does not hit FUN_0002BD08()'s RNG reset path for lv01s1.dat REACTOR block104, so this cycle is exact without needing external RNG state.",
			"Type-7 record format: u16 x_9_7, u16 y_9_7, s16 dx, s16 dy, u8 colorIndex, u8 delayCounter; active records plot one pixel, then update x += dx, y += dy, dy += 5.",
		];

		DatPaletteHelper.AppendMetadata(lines, dat, paletteContext, paletteFailureReason);
		lines.AddRange(
		[
			string.Empty,
			"Type-7 blocks:",
		]);

		foreach (Type7EffectBlockSummary summary in blockSummaries)
		{
			lines.Add(
				$"[{summary.BlockIndex:D2}] data=0x{summary.DataOffset:X} span=0x{summary.DataSpan:X} declared={summary.DeclaredWidth}x{summary.DeclaredHeight} frames={summary.FrameCount} maxFramePixels={summary.PixelWritesPerFrame} bounds={summary.Bounds.Left},{summary.Bounds.Top} {summary.Bounds.Width}x{summary.Bounds.Height}");
		}

		File.WriteAllLines(outputPath, lines);
	}
}

internal sealed record Type7EffectExportResult(
	string ResourceName,
	string OutputDirectory,
	int BlockCount,
	int FrameCount,
	string DefaultPaletteSummary,
	IReadOnlyList<string> ExportedPaletteVariants);

internal sealed record Type7EffectBlockSummary(
	int BlockIndex,
	int DataOffset,
	int DataSpan,
	int DeclaredWidth,
	int DeclaredHeight,
	int FrameCount,
	int PixelWritesPerFrame,
	Rect Bounds);

internal sealed record Type7CycleRender(
	IReadOnlyList<IReadOnlyDictionary<int, byte>> Frames,
	IReadOnlyDictionary<int, byte> CoveragePixels,
	Rect Bounds,
	int MaxFramePixelCount);

internal sealed record ParticleRecordState(
	ushort X,
	ushort Y,
	short DeltaX,
	short DeltaY,
	byte ColorIndex,
	byte DelayCounter);

internal readonly record struct Rect(int Left, int Top, int Right, int Bottom)
{
	public static Rect Empty => new(int.MaxValue, int.MaxValue, int.MinValue, int.MinValue);

	public bool IsEmpty => Right < Left || Bottom < Top;

	public int Width => IsEmpty ? 0 : Right - Left + 1;

	public int Height => IsEmpty ? 0 : Bottom - Top + 1;

	public Rect Include(int x, int y)
	{
		if (IsEmpty)
		{
			return new Rect(x, y, x, y);
		}

		return new Rect(
			Math.Min(Left, x),
			Math.Min(Top, y),
			Math.Max(Right, x),
			Math.Max(Bottom, y));
	}
}
