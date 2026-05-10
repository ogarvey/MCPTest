using System.Buffers.Binary;
using DogKnife.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DogKnife.Helpers;

internal static class Type1RenderedExporter
{
	private const int RuntimeStride = 0x180;
	private const uint Signature4D100 = 0xE083F88B;
	private const uint Signature4D31C = 0x03E08350;
	private const uint Signature4D4DC = 0xE8905550;

	public static Type1RenderedExportResult Export(CatGunDat dat, string resourceName, string outputRoot)
	{
		DatResourceEntry resource = dat.Resources.SingleOrDefault(candidate =>
			string.Equals(candidate.Name, resourceName, StringComparison.Ordinal))
			?? throw new InvalidDataException($"{resourceName} resource was not found in the DAT resource table.");

		DatPayloadGroup payloadGroup = dat.PayloadGroups.SingleOrDefault(group => group.StartOffset == resource.Pointer04)
			?? throw new InvalidDataException($"{resourceName} payload group was not found for resource field +0x04.");

		DatSequenceGroup? sequenceGroup = resource.Pointer08 == 0
			? null
			: dat.SequenceGroups.SingleOrDefault(group => group.StartOffset == resource.Pointer08);

		List<DatPayloadBlock30> type1Blocks = payloadGroup.Blocks
			.Where(block => block.LoaderType == 1)
			.ToList();

		if (type1Blocks.Count == 0)
		{
			throw new NotSupportedException($"{resourceName} does not expose any loader type-1 blocks in its current payload group.");
		}

		ReadOnlySpan<byte> bytes = dat.RawBytes.Span;
		string familyRoot = Path.Combine(Path.GetFullPath(outputRoot), resourceName, "type1_render");
		string defaultBlocksDirectory = Path.Combine(familyRoot, DatPaletteHelper.DefaultDirectoryName, "blocks");
		string defaultFramesDirectory = Path.Combine(familyRoot, DatPaletteHelper.DefaultDirectoryName, "frames");
		string blocksDirectory = Path.Combine(familyRoot, "grayscale", "blocks");
		string framesDirectory = Path.Combine(familyRoot, "grayscale", "frames");
		Directory.CreateDirectory(defaultBlocksDirectory);
		Directory.CreateDirectory(defaultFramesDirectory);
		Directory.CreateDirectory(blocksDirectory);
		Directory.CreateDirectory(framesDirectory);
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

		List<Type1RenderedBlockSummary> blockSummaries = new(type1Blocks.Count);
		Dictionary<int, RenderedType1Image> renderedBlocks = new();

		foreach (DatPayloadBlock30 block in type1Blocks)
		{
			try
			{
				RenderedType1Image image = RenderBlock(bytes, block);
				string blockPath = Path.Combine(
					blocksDirectory,
					$"block_{block.Index:D2}_{image.Width}x{image.Height}.png");
				string defaultBlockPath = Path.Combine(
					defaultBlocksDirectory,
					$"block_{block.Index:D2}_{image.Width}x{image.Height}.png");
				SaveImage(defaultBlockPath, image, defaultPalette);
				SaveImage(blockPath, image, palette: null);

				if (paletteContext is not null)
				{
					foreach (ExportPaletteVariant variant in paletteContext.Variants)
					{
						string variantBlockPath = Path.Combine(
							familyRoot,
							variant.DirectoryName,
							"blocks",
							$"block_{block.Index:D2}_{image.Width}x{image.Height}.png");
						SaveImage(variantBlockPath, image, paletteContext.Banks[variant.BankIndex]);
					}
				}

				renderedBlocks.Add(block.Index, image);
				blockSummaries.Add(new Type1RenderedBlockSummary(
					BlockIndex: block.Index,
					Rendered: true,
					DeclaredWidth: block.Value08,
					DeclaredHeight: block.Value0C,
					OutputWidth: image.Width,
					OutputHeight: image.Height,
					TouchedPixelCount: image.TouchedPixelCount,
					FamilyChain: image.FamilyChain,
					OutputPath: blockPath,
					FailureReason: null));
			}
			catch (Exception exception)
			{
				blockSummaries.Add(new Type1RenderedBlockSummary(
					BlockIndex: block.Index,
					Rendered: false,
					DeclaredWidth: block.Value08,
					DeclaredHeight: block.Value0C,
					OutputWidth: null,
					OutputHeight: null,
					TouchedPixelCount: null,
					FamilyChain: null,
					OutputPath: null,
					FailureReason: exception.Message));
			}
		}

		List<byte> frameOrder = sequenceGroup?.Segments.SelectMany(segment => segment.Bytes).ToList() ?? [];
		int frameCount = 0;
		int skippedFrameCount = 0;

		for (int frameIndex = 0; frameIndex < frameOrder.Count; frameIndex++)
		{
			byte blockIndex = frameOrder[frameIndex];
			if (!renderedBlocks.TryGetValue(blockIndex, out RenderedType1Image? image))
			{
				skippedFrameCount++;
				continue;
			}

			string framePath = Path.Combine(
				framesDirectory,
				$"frame_{frameIndex:D2}_block_{blockIndex:D2}.png");
			string defaultFramePath = Path.Combine(
				defaultFramesDirectory,
				$"frame_{frameIndex:D2}_block_{blockIndex:D2}.png");
			SaveImage(defaultFramePath, image, defaultPalette);
			SaveImage(framePath, image, palette: null);

			if (paletteContext is not null)
			{
				foreach (ExportPaletteVariant variant in paletteContext.Variants)
				{
					string variantFramePath = Path.Combine(
						familyRoot,
						variant.DirectoryName,
						"frames",
						$"frame_{frameIndex:D2}_block_{blockIndex:D2}.png");
					SaveImage(variantFramePath, image, paletteContext.Banks[variant.BankIndex]);
				}
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
			frameCount,
			skippedFrameCount,
			frameOrder);

		return new Type1RenderedExportResult(
			ResourceName: resourceName,
			OutputDirectory: familyRoot,
			BlockCount: type1Blocks.Count,
			RenderedBlockCount: blockSummaries.Count(summary => summary.Rendered),
			FrameCount: frameCount,
			SkippedFrameCount: skippedFrameCount,
			DefaultPaletteSummary: paletteContext?.DefaultPaletteSummary ?? $"{DatPaletteHelper.DefaultDirectoryName}/ falls back to grayscale; palette data unavailable.",
			ExportedPaletteVariants: paletteContext?.Variants.Select(variant => $"{variant.DirectoryName}=bank{variant.BankIndex:D2}").ToArray() ?? []);
	}

	internal static RenderedType1Image RenderBlock(ReadOnlySpan<byte> bytes, DatPayloadBlock30 block)
	{
		SparseType1Surface surface = new();
		List<string> familyChain = [];
		ExecutePayload(bytes, block.Value24, surface, familyChain);

		if (surface.TouchedPixelCount == 0)
		{
			throw new InvalidDataException($"Type-1 payload block {block.Index} produced no pixel writes.");
		}

		return surface.ToImage(block.Value08, block.Value0C, string.Join(" -> ", familyChain));
	}

	internal static RenderedType1Image RenderPrimaryStage(ReadOnlySpan<byte> bytes, DatPayloadBlock30 block)
	{
		SparseType1Surface surface = new();
		List<string> familyChain = [];
		int payloadOffset = block.Value24;

		EnsureAvailable(bytes, payloadOffset, 1, "primary-stage opcode");
		uint signature = payloadOffset + sizeof(uint) <= bytes.Length
			? ReadUInt32(bytes, payloadOffset)
			: 0;

		if (signature == Signature4D31C)
		{
			familyChain.Add("VariableRowCopy");
			ExecuteVariableRowCopy(bytes, payloadOffset, surface);
		}
		else if (signature == Signature4D100)
		{
			throw new NotSupportedException(
				$"Aligned16x16Copy payload at 0x{payloadOffset:X} is not rendered yet.");
		}
		else if (signature == Signature4D4DC)
		{
			familyChain.Add("ScatterWriteHelper");
			ExecuteScatterWriteHelper(bytes, payloadOffset, surface);
		}
		else if (TryExecutePatternWriter(bytes, payloadOffset, surface, out _))
		{
			familyChain.Add("PatternWriter");
		}
		else
		{
			throw new NotSupportedException(
				$"Unsupported primary type-1 stage at 0x{payloadOffset:X}: signature starts with {FormatSignature(bytes.Slice(payloadOffset, Math.Min(8, bytes.Length - payloadOffset)).ToArray())}.");
		}

		if (surface.TouchedPixelCount == 0)
		{
			throw new InvalidDataException($"Type-1 payload block {block.Index} primary stage produced no pixel writes.");
		}

		return surface.ToImage(block.Value08, block.Value0C, string.Join(" -> ", familyChain));
	}

	private static void ExecutePayload(
		ReadOnlySpan<byte> bytes,
		int payloadOffset,
		SparseType1Surface surface,
		ICollection<string> familyChain)
	{
		int offset = payloadOffset;

		for (int step = 0; step < 16; step++)
		{
			EnsureAvailable(bytes, offset, 1, "payload opcode");

			uint signature = offset + sizeof(uint) <= bytes.Length
				? ReadUInt32(bytes, offset)
				: 0;

			if (signature == Signature4D31C)
			{
				familyChain.Add("VariableRowCopy");
				offset = ExecuteVariableRowCopy(bytes, offset, surface);
				continue;
			}

			if (signature == Signature4D100)
			{
				throw new NotSupportedException(
					$"Aligned16x16Copy payload at 0x{offset:X} is not rendered yet.");
			}

			if (signature == Signature4D4DC)
			{
				familyChain.Add("ScatterWriteHelper");
				offset = ExecuteScatterWriteHelper(bytes, offset, surface);
				continue;
			}

			if (TryExecutePatternWriter(bytes, offset, surface, out int nextOffset))
			{
				familyChain.Add("PatternWriter");
				if (nextOffset < bytes.Length && bytes[nextOffset] == 0xC3)
				{
					return;
				}

				return;
			}

			if (bytes[offset] == 0xC3)
			{
				return;
			}

			throw new NotSupportedException(
				$"Unsupported type-1 execution chain at 0x{offset:X}: signature starts with {FormatSignature(bytes.Slice(offset, Math.Min(8, bytes.Length - offset)).ToArray())}.");
		}

		throw new InvalidDataException($"Type-1 payload at 0x{payloadOffset:X} exceeded the execution-step guard.");
	}

	private static int ExecuteScatterWriteHelper(ReadOnlySpan<byte> bytes, int payloadOffset, SparseType1Surface surface)
	{
		int cursor = payloadOffset + 0x08;

		cursor = ExecuteDwordPhase(bytes, cursor, surface);
		cursor = ExecuteWordPhase(bytes, cursor, surface);
		cursor = ExecuteBytePhase(bytes, cursor, surface);

		return cursor;
	}

	private static int ExecuteVariableRowCopy(ReadOnlySpan<byte> bytes, int payloadOffset, SparseType1Surface surface)
	{
		int relativeNextOffset = ReadInt32(bytes, payloadOffset + 0x0C);
		if (relativeNextOffset < 0)
		{
			throw new InvalidDataException($"4D31C payload at 0x{payloadOffset:X} has a negative next-stage offset {relativeNextOffset}.");
		}

		int streamStartOffset = payloadOffset + 0x10;
		EnsureAvailable(bytes, streamStartOffset, relativeNextOffset, "4D31C outer stream");
		int streamEndOffset = checked(streamStartOffset + relativeNextOffset);
		int cursor = streamStartOffset;
		int rowCount = ReadInt32(bytes, cursor);
		cursor += sizeof(int);

		if (rowCount <= 0)
		{
			throw new InvalidDataException($"4D31C payload at 0x{payloadOffset:X} has invalid row count {rowCount}.");
		}

		int destinationOffset = 0;
		for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
		{
			int destinationSkip = ReadInt32(bytes, cursor);
			int encodedDwordCount = ReadInt32(bytes, cursor + sizeof(int));
			cursor += sizeof(int) * 2;

			if (encodedDwordCount < 0)
			{
				throw new InvalidDataException(
					$"4D31C payload at 0x{payloadOffset:X} row {rowIndex} has invalid encoded dword count {encodedDwordCount}.");
			}

			destinationOffset = checked(destinationOffset + destinationSkip);
			int byteCount = checked((encodedDwordCount + 1) * sizeof(uint));
			EnsureAvailable(bytes, cursor, byteCount, "4D31C row data");
			surface.WriteBytes(destinationOffset, bytes.Slice(cursor, byteCount));
			cursor += byteCount;
			destinationOffset = checked(destinationOffset + byteCount);
		}

		if (cursor != streamEndOffset)
		{
			throw new InvalidDataException(
				$"4D31C payload at 0x{payloadOffset:X} ended at 0x{cursor:X}, expected next stage at 0x{streamEndOffset:X}.");
		}

		return cursor;
	}

	private static int ExecuteDwordPhase(ReadOnlySpan<byte> bytes, int cursor, SparseType1Surface surface)
	{
		int groupCount = ReadInt32(bytes, cursor);
		cursor += sizeof(int);

		for (int groupIndex = 0; groupIndex < groupCount; groupIndex++)
		{
			int destinationOffset = ReadInt32(bytes, cursor);
			uint firstValue = ReadUInt32(bytes, cursor + sizeof(int));
			cursor += sizeof(int) * 2;
			surface.WriteUInt32(destinationOffset, firstValue);

			int pairCount = ReadInt32(bytes, cursor);
			int dataOffset = ReadInt32(bytes, cursor + sizeof(int));
			int controlCursor = cursor + (sizeof(int) * 2);
			int dataCursor = checked(cursor + dataOffset);
			cursor = dataCursor;

			for (int pairIndex = 0; pairIndex < pairCount; pairIndex++)
			{
				uint control = ReadUInt32(bytes, controlCursor);
				controlCursor += sizeof(int);

				destinationOffset += (ushort)control;
				surface.WriteUInt32(destinationOffset, ReadUInt32(bytes, cursor));
				cursor += sizeof(uint);

				destinationOffset += (ushort)(control >> 16);
				surface.WriteUInt32(destinationOffset, ReadUInt32(bytes, cursor));
				cursor += sizeof(uint);
			}
		}

		return cursor;
	}

	private static int ExecuteWordPhase(ReadOnlySpan<byte> bytes, int cursor, SparseType1Surface surface)
	{
		int groupCount = ReadInt32(bytes, cursor);
		cursor += sizeof(int);

		for (int groupIndex = 0; groupIndex < groupCount; groupIndex++)
		{
			int destinationOffset = ReadInt32(bytes, cursor);
			ushort firstValue = (ushort)ReadUInt32(bytes, cursor + sizeof(int));
			cursor += sizeof(int) * 2;
			surface.WriteUInt16(destinationOffset, firstValue);

			int pairCount = ReadInt32(bytes, cursor);
			int dataOffset = ReadInt32(bytes, cursor + sizeof(int));
			int controlCursor = cursor + (sizeof(int) * 2);
			int dataCursor = checked(cursor + dataOffset);
			cursor = dataCursor;

			for (int pairIndex = 0; pairIndex < pairCount; pairIndex++)
			{
				uint control = ReadUInt32(bytes, controlCursor);
				controlCursor += sizeof(int);
				uint packedWords = ReadUInt32(bytes, cursor);
				cursor += sizeof(uint);

				destinationOffset += (ushort)control;
				surface.WriteUInt16(destinationOffset, (ushort)packedWords);

				destinationOffset += (ushort)(control >> 16);
				surface.WriteUInt16(destinationOffset, (ushort)(packedWords >> 16));
			}
		}

		return cursor;
	}

	private static int ExecuteBytePhase(ReadOnlySpan<byte> bytes, int cursor, SparseType1Surface surface)
	{
		int groupCount = ReadInt32(bytes, cursor);
		cursor += sizeof(int);

		for (int groupIndex = 0; groupIndex < groupCount; groupIndex++)
		{
			int destinationOffset = ReadInt32(bytes, cursor);
			byte firstValue = (byte)ReadUInt32(bytes, cursor + sizeof(int));
			cursor += sizeof(int) * 2;
			surface.WriteByte(destinationOffset, firstValue);

			int quartetCount = ReadInt32(bytes, cursor);
			int dataOffset = ReadInt32(bytes, cursor + sizeof(int));
			int controlCursor = cursor + (sizeof(int) * 2);
			int dataCursor = checked(cursor + dataOffset);
			cursor = dataCursor;

			for (int quartetIndex = 0; quartetIndex < quartetCount; quartetIndex++)
			{
				uint controlA = ReadUInt32(bytes, controlCursor);
				controlCursor += sizeof(int);
				uint packedBytes = ReadUInt32(bytes, cursor);
				cursor += sizeof(uint);

				destinationOffset += (ushort)controlA;
				surface.WriteByte(destinationOffset, (byte)packedBytes);

				destinationOffset += (ushort)(controlA >> 16);
				surface.WriteByte(destinationOffset, (byte)(packedBytes >> 8));

				uint controlB = ReadUInt32(bytes, controlCursor);
				controlCursor += sizeof(int);

				destinationOffset += (ushort)controlB;
				surface.WriteByte(destinationOffset, (byte)(packedBytes >> 16));

				destinationOffset += (ushort)(controlB >> 16);
				surface.WriteByte(destinationOffset, (byte)(packedBytes >> 24));
			}
		}

		return cursor;
	}

	private static bool TryExecutePatternWriter(
		ReadOnlySpan<byte> bytes,
		int payloadOffset,
		SparseType1Surface surface,
		out int nextOffset)
	{
		int cursor = payloadOffset;
		uint ebx = 0;
		bool wroteAnyPixels = false;

		while (cursor < bytes.Length)
		{
			byte opcode = bytes[cursor];
			switch (opcode)
			{
				case 0xBB:
					EnsureAvailable(bytes, cursor, 5, "pattern-writer imm32 load");
					ebx = ReadUInt32(bytes, cursor + 1);
					cursor += 5;
					break;

				case 0xB3:
					EnsureAvailable(bytes, cursor, 2, "pattern-writer imm8 BL load");
					ebx = (ebx & 0xFFFFFF00U) | bytes[cursor + 1];
					cursor += 2;
					break;

				case 0x89 when ReadByte(bytes, cursor + 1) == 0x98:
					EnsureAvailable(bytes, cursor, 6, "pattern-writer dword write");
					surface.WriteUInt32(ReadInt32(bytes, cursor + 2), ebx);
					wroteAnyPixels = true;
					cursor += 6;
					break;

				case 0x89 when ReadByte(bytes, cursor + 1) == 0x18:
					EnsureAvailable(bytes, cursor, 2, "pattern-writer base dword write");
					surface.WriteUInt32(0, ebx);
					wroteAnyPixels = true;
					cursor += 2;
					break;

				case 0x89 when ReadByte(bytes, cursor + 1) == 0x58:
					EnsureAvailable(bytes, cursor, 3, "pattern-writer short dword write");
					surface.WriteUInt32((sbyte)ReadByte(bytes, cursor + 2), ebx);
					wroteAnyPixels = true;
					cursor += 3;
					break;

				case 0x88 when ReadByte(bytes, cursor + 1) == 0x98:
					EnsureAvailable(bytes, cursor, 6, "pattern-writer byte BL write");
					surface.WriteByte(ReadInt32(bytes, cursor + 2), (byte)ebx);
					wroteAnyPixels = true;
					cursor += 6;
					break;

				case 0x88 when ReadByte(bytes, cursor + 1) == 0x18:
					EnsureAvailable(bytes, cursor, 2, "pattern-writer base byte BL write");
					surface.WriteByte(0, (byte)ebx);
					wroteAnyPixels = true;
					cursor += 2;
					break;

				case 0x88 when ReadByte(bytes, cursor + 1) == 0xB8:
					EnsureAvailable(bytes, cursor, 6, "pattern-writer byte BH write");
					surface.WriteByte(ReadInt32(bytes, cursor + 2), (byte)(ebx >> 8));
					wroteAnyPixels = true;
					cursor += 6;
					break;

				case 0x88 when ReadByte(bytes, cursor + 1) == 0x58:
					EnsureAvailable(bytes, cursor, 3, "pattern-writer short byte BL write");
					surface.WriteByte((sbyte)ReadByte(bytes, cursor + 2), (byte)ebx);
					wroteAnyPixels = true;
					cursor += 3;
					break;

				case 0x88 when ReadByte(bytes, cursor + 1) == 0x78:
					EnsureAvailable(bytes, cursor, 3, "pattern-writer short byte BH write");
					surface.WriteByte((sbyte)ReadByte(bytes, cursor + 2), (byte)(ebx >> 8));
					wroteAnyPixels = true;
					cursor += 3;
					break;

				case 0x66 when ReadByte(bytes, cursor + 1) == 0x89 && ReadByte(bytes, cursor + 2) == 0x98:
					EnsureAvailable(bytes, cursor, 7, "pattern-writer word write");
					surface.WriteUInt16(ReadInt32(bytes, cursor + 3), (ushort)ebx);
					wroteAnyPixels = true;
					cursor += 7;
					break;

				case 0x66 when ReadByte(bytes, cursor + 1) == 0x89 && ReadByte(bytes, cursor + 2) == 0x18:
					EnsureAvailable(bytes, cursor, 3, "pattern-writer base word write");
					surface.WriteUInt16(0, (ushort)ebx);
					wroteAnyPixels = true;
					cursor += 3;
					break;

				case 0x66 when ReadByte(bytes, cursor + 1) == 0x89 && ReadByte(bytes, cursor + 2) == 0x58:
					EnsureAvailable(bytes, cursor, 4, "pattern-writer short word write");
					surface.WriteUInt16((sbyte)ReadByte(bytes, cursor + 3), (ushort)ebx);
					wroteAnyPixels = true;
					cursor += 4;
					break;

				case 0xC7 when ReadByte(bytes, cursor + 1) == 0x00:
					EnsureAvailable(bytes, cursor, 6, "pattern-writer base imm32 write");
					surface.WriteUInt32(0, ReadUInt32(bytes, cursor + 2));
					wroteAnyPixels = true;
					cursor += 6;
					break;

				case 0xC7 when ReadByte(bytes, cursor + 1) == 0x80:
					EnsureAvailable(bytes, cursor, 10, "pattern-writer imm32 write");
					surface.WriteUInt32(ReadInt32(bytes, cursor + 2), ReadUInt32(bytes, cursor + 6));
					wroteAnyPixels = true;
					cursor += 10;
					break;

				case 0xC7 when ReadByte(bytes, cursor + 1) == 0x40:
					EnsureAvailable(bytes, cursor, 7, "pattern-writer imm32 disp8 write");
					surface.WriteUInt32((sbyte)ReadByte(bytes, cursor + 2), ReadUInt32(bytes, cursor + 3));
					wroteAnyPixels = true;
					cursor += 7;
					break;

				case 0xC6 when ReadByte(bytes, cursor + 1) == 0x00:
					EnsureAvailable(bytes, cursor, 3, "pattern-writer base imm8 write");
					surface.WriteByte(0, ReadByte(bytes, cursor + 2));
					wroteAnyPixels = true;
					cursor += 3;
					break;

				case 0xC6 when ReadByte(bytes, cursor + 1) == 0x80:
					EnsureAvailable(bytes, cursor, 7, "pattern-writer imm8 disp32 write");
					surface.WriteByte(ReadInt32(bytes, cursor + 2), ReadByte(bytes, cursor + 6));
					wroteAnyPixels = true;
					cursor += 7;
					break;

				case 0xC6 when ReadByte(bytes, cursor + 1) == 0x40:
					EnsureAvailable(bytes, cursor, 4, "pattern-writer imm8 disp8 write");
					surface.WriteByte((sbyte)ReadByte(bytes, cursor + 2), ReadByte(bytes, cursor + 3));
					wroteAnyPixels = true;
					cursor += 4;
					break;

				case 0xC3:
					nextOffset = cursor + 1;
					return wroteAnyPixels;

				default:
					nextOffset = payloadOffset;
					return false;
			}
		}

		nextOffset = payloadOffset;
		return false;
	}

	private static int ReadInt32(ReadOnlySpan<byte> bytes, int offset)
	{
		EnsureAvailable(bytes, offset, sizeof(int), "int32 read");
		return BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(offset, sizeof(int)));
	}

	private static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset)
	{
		EnsureAvailable(bytes, offset, sizeof(uint), "uint32 read");
		return BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, sizeof(uint)));
	}

	private static byte ReadByte(ReadOnlySpan<byte> bytes, int offset)
	{
		EnsureAvailable(bytes, offset, 1, "byte read");
		return bytes[offset];
	}

	private static void EnsureAvailable(ReadOnlySpan<byte> bytes, int offset, int byteCount, string operation)
	{
		if (offset < 0 || byteCount < 0 || offset + byteCount > bytes.Length)
		{
			throw new InvalidDataException($"Type-1 renderer {operation} exceeds DAT bounds at 0x{offset:X} (length 0x{byteCount:X}).");
		}
	}

	private static void SaveImage(string outputPath, RenderedType1Image image, Rgba32[]? palette)
	{
		DatPaletteHelper.SaveIndexedImage(outputPath, image.Pixels, 0, 0, image.Width, image.Height, RuntimeStride, palette);
	}

	private static void WriteMetadata(
		string outputPath,
		CatGunDat dat,
		DatResourceEntry resource,
		DatPayloadGroup payloadGroup,
		DatSequenceGroup? sequenceGroup,
		DatPaletteContext? paletteContext,
		string? paletteFailureReason,
		IReadOnlyList<Type1RenderedBlockSummary> blockSummaries,
		int frameCount,
		int skippedFrameCount,
		IReadOnlyList<byte> frameOrder)
	{
		List<string> lines =
		[
			$"DAT: {dat.FilePath}",
			$"Resource: {resource.Name}",
			$"Payload group: 0x{payloadGroup.StartOffset:X}..0x{payloadGroup.EndOffset:X}",
			$"Type-1 blocks inspected: {blockSummaries.Count}",
			$"Rendered blocks: {blockSummaries.Count(summary => summary.Rendered)}",
			$"Sequence group: {(sequenceGroup is null ? "<none>" : $"0x{sequenceGroup.StartOffset:X}..0x{sequenceGroup.EndOffset:X}")}",
			$"Frame order: {(frameOrder.Count == 0 ? "<none>" : string.Join(' ', frameOrder.Select(value => value.ToString("X2"))))}",
			$"Rendered frames: {frameCount}",
			$"Skipped frames: {skippedFrameCount}",
		];

		DatPaletteHelper.AppendMetadata(lines, dat, paletteContext, paletteFailureReason);
		lines.Add(string.Empty);
		lines.AddRange(
		[
			"Blocks:",
		]);

		foreach (Type1RenderedBlockSummary summary in blockSummaries)
		{
			if (summary.Rendered)
			{
				lines.Add(
					$"[{summary.BlockIndex:D2}] rendered family={summary.FamilyChain} declared={summary.DeclaredWidth}x{summary.DeclaredHeight} output={summary.OutputWidth}x{summary.OutputHeight} touched={summary.TouchedPixelCount} file={Path.GetFileName(summary.OutputPath!)}");
			}
			else
			{
				lines.Add(
					$"[{summary.BlockIndex:D2}] skipped declared={summary.DeclaredWidth}x{summary.DeclaredHeight} reason={summary.FailureReason}");
			}
		}

		File.WriteAllLines(outputPath, lines);
	}

	private static string FormatSignature(IEnumerable<byte> bytes)
	{
		return string.Join(" ", bytes.Select(value => value.ToString("X2")));
	}

	private sealed class SparseType1Surface
	{
		private readonly Dictionary<int, byte> _pixels = new();
		private int _maxX = -1;
		private int _maxY = -1;

		public int TouchedPixelCount => _pixels.Count;

		public void WriteByte(int destinationOffset, byte value)
		{
			if (destinationOffset < 0)
			{
				throw new InvalidDataException($"Type-1 renderer attempted to write a negative destination offset: {destinationOffset}.");
			}

			int x = destinationOffset % RuntimeStride;
			int y = destinationOffset / RuntimeStride;
			_pixels[destinationOffset] = value;
			_maxX = Math.Max(_maxX, x);
			_maxY = Math.Max(_maxY, y);
		}

		public void WriteUInt16(int destinationOffset, ushort value)
		{
			WriteByte(destinationOffset, (byte)value);
			WriteByte(destinationOffset + 1, (byte)(value >> 8));
		}

		public void WriteUInt32(int destinationOffset, uint value)
		{
			WriteByte(destinationOffset, (byte)value);
			WriteByte(destinationOffset + 1, (byte)(value >> 8));
			WriteByte(destinationOffset + 2, (byte)(value >> 16));
			WriteByte(destinationOffset + 3, (byte)(value >> 24));
		}

		public void WriteBytes(int destinationOffset, ReadOnlySpan<byte> values)
		{
			for (int index = 0; index < values.Length; index++)
			{
				WriteByte(destinationOffset + index, values[index]);
			}
		}

		public RenderedType1Image ToImage(int declaredWidth, int declaredHeight, string familyChain)
		{
			int width = Math.Max(1, declaredWidth);
			int height = Math.Max(1, declaredHeight);

			if (_pixels.Count > 0)
			{
				width = Math.Max(width, _maxX + 1);
				height = Math.Max(height, _maxY + 1);
			}

			return new RenderedType1Image(
				Width: width,
				Height: height,
				Pixels: new Dictionary<int, byte>(_pixels),
				TouchedPixelCount: _pixels.Count,
				FamilyChain: familyChain);
		}
	}
}

internal sealed record Type1RenderedExportResult(
	string ResourceName,
	string OutputDirectory,
	int BlockCount,
	int RenderedBlockCount,
	int FrameCount,
	int SkippedFrameCount,
	string DefaultPaletteSummary,
	IReadOnlyList<string> ExportedPaletteVariants);

internal sealed record Type1RenderedBlockSummary(
	int BlockIndex,
	bool Rendered,
	int DeclaredWidth,
	int DeclaredHeight,
	int? OutputWidth,
	int? OutputHeight,
	int? TouchedPixelCount,
	string? FamilyChain,
	string? OutputPath,
	string? FailureReason);

internal sealed record RenderedType1Image(
	int Width,
	int Height,
	IReadOnlyDictionary<int, byte> Pixels,
	int TouchedPixelCount,
	string FamilyChain);
