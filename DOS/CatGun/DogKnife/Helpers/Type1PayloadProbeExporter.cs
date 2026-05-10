using System.Buffers.Binary;
using DogKnife.Models;

namespace DogKnife.Helpers;

internal static class Type1PayloadProbeExporter
{
	private const int RuntimeStride = 0x180;
	private const uint Signature4D100 = 0xE083F88B;
	private const uint Signature4D31C = 0x03E08350;
	private const uint Signature4D4DC = 0xE8905550;

	public static Type1PayloadProbeExportResult Export(CatGunDat dat, string resourceName, string outputRoot)
	{
		DatResourceEntry resource = dat.Resources.SingleOrDefault(candidate =>
			string.Equals(candidate.Name, resourceName, StringComparison.Ordinal))
			?? throw new InvalidDataException($"{resourceName} resource was not found in the DAT resource table.");

		DatPayloadGroup payloadGroup = dat.PayloadGroups.SingleOrDefault(group => group.StartOffset == resource.Pointer04)
			?? throw new InvalidDataException($"{resourceName} payload group was not found for resource field +0x04.");

		List<DatPayloadBlock30> type1Blocks = payloadGroup.Blocks
			.Where(block => block.LoaderType == 1)
			.ToList();

		if (type1Blocks.Count == 0)
		{
			string loaderTypes = FormatLoaderTypeDistribution(payloadGroup.Blocks);
			throw new NotSupportedException(
				$"{resourceName} does not expose any loader type-1 blocks in its current payload group. Parsed loader types: {loaderTypes}.");
		}

		ReadOnlySpan<byte> bytes = dat.RawBytes.Span;
		string familyRoot = Path.Combine(Path.GetFullPath(outputRoot), resourceName, "type1_probe");
		Directory.CreateDirectory(familyRoot);

		List<Type1PayloadBlockSummary> blockSummaries = new(type1Blocks.Count);
		foreach (DatPayloadBlock30 block in type1Blocks)
		{
			blockSummaries.Add(InspectBlock(bytes, block));
		}

		WriteMetadata(Path.Combine(familyRoot, "metadata.txt"), dat, resource, payloadGroup, blockSummaries);

		return new Type1PayloadProbeExportResult(
			ResourceName: resourceName,
			OutputDirectory: familyRoot,
			BlockCount: blockSummaries.Count,
			KnownFamilyCount: blockSummaries.Count(summary => summary.Family != Type1PayloadFamily.Unknown));
	}

	private static Type1PayloadBlockSummary InspectBlock(ReadOnlySpan<byte> bytes, DatPayloadBlock30 block)
	{
		if (block.Value24 <= 0 || block.Value24 + sizeof(uint) > bytes.Length)
		{
			throw new InvalidDataException($"Type-1 payload block {block.Index} has an invalid data offset: 0x{block.Value24:X}");
		}

		uint signature = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(block.Value24, sizeof(uint)));
		byte[] signatureBytes = bytes.Slice(block.Value24, 8).ToArray();

		if (signature == Signature4D100)
		{
			int streamStartOffset = block.Value24 + 0x0C;
			int streamByteCount = 16 * 16;
			ValidateBounds(bytes, streamStartOffset, streamByteCount, block.Index, "4D100 inline copy data");

			return new Type1PayloadBlockSummary(
				BlockIndex: block.Index,
				Family: Type1PayloadFamily.Aligned16x16Copy,
				Signature: FormatSignature(signatureBytes),
				PayloadOffset: block.Value24,
				PrimaryStreamOffset: streamStartOffset,
				PrimaryStreamEndOffset: streamStartOffset + streamByteCount,
				SecondaryStreamOffset: null,
				DeclaredWidth: 16,
				DeclaredHeight: 16,
				OuterSegmentCount: 16,
				InnerWriteCount: 64,
				Notes: "Patched to 0x4D100. Alignment-dispatched 16x16 opaque copy; inline source bytes start at payload+0x0C.");
		}

		if (signature == Signature4D31C)
		{
			int relativeNextOffset = ReadInt32(bytes, block.Value24 + 0x0C);
			int streamStartOffset = block.Value24 + 0x10;
			int streamEndOffset = checked(streamStartOffset + relativeNextOffset);
			ValidateBounds(bytes, streamStartOffset, Math.Max(0, relativeNextOffset), block.Index, "4D31C outer stream");

			Type1OuterStreamSummary outerSummary = Parse4D31COuterStream(bytes, streamStartOffset, streamEndOffset);
			int? nestedHelperOffset = null;
			string notes = "Patched to 0x4D31C. Alignment-dispatched variable-row dword blit; stream starts at payload+0x10.";

			if (streamEndOffset + sizeof(uint) <= bytes.Length)
			{
				uint nestedSignature = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(streamEndOffset, sizeof(uint)));
				if (nestedSignature == Signature4D4DC)
				{
					nestedHelperOffset = streamEndOffset;
					notes += " Nested 0x4D4DC helper stub is present at the end of the outer stream.";
				}
			}

			return new Type1PayloadBlockSummary(
				BlockIndex: block.Index,
				Family: Type1PayloadFamily.VariableRowCopy,
				Signature: FormatSignature(signatureBytes),
				PayloadOffset: block.Value24,
				PrimaryStreamOffset: streamStartOffset,
				PrimaryStreamEndOffset: streamEndOffset,
				SecondaryStreamOffset: nestedHelperOffset,
				DeclaredWidth: block.Value08,
				DeclaredHeight: block.Value0C,
				OuterSegmentCount: outerSummary.RowGroupCount,
				InnerWriteCount: outerSummary.RowCount,
				Notes: notes + $" Parsed rows={outerSummary.RowCount}, copied dwords={outerSummary.TotalDwordCount}, max row dwords={outerSummary.MaxDwordCount}."
			);
		}

		if (signature == Signature4D4DC)
		{
			return new Type1PayloadBlockSummary(
				BlockIndex: block.Index,
				Family: Type1PayloadFamily.ScatterWriteHelper,
				Signature: FormatSignature(signatureBytes),
				PayloadOffset: block.Value24,
				PrimaryStreamOffset: block.Value24 + 0x08,
				PrimaryStreamEndOffset: null,
				SecondaryStreamOffset: null,
				DeclaredWidth: block.Value08,
				DeclaredHeight: block.Value0C,
				OuterSegmentCount: null,
				InnerWriteCount: null,
				Notes: "Patched to 0x4D4DC via the inline CALL at payload+0x04. This is the nested scatter-write helper family; stream starts at payload+0x08.");
		}

		if (TryParsePatternWriter(bytes, block.Value24, out Type1PatternWriterSummary patternWriter))
		{
			return new Type1PayloadBlockSummary(
				BlockIndex: block.Index,
				Family: Type1PayloadFamily.PatternWriter,
				Signature: FormatSignature(signatureBytes),
				PayloadOffset: block.Value24,
				PrimaryStreamOffset: block.Value24,
				PrimaryStreamEndOffset: patternWriter.EndOffset,
				SecondaryStreamOffset: null,
				DeclaredWidth: block.Value08,
				DeclaredHeight: block.Value0C,
				OuterSegmentCount: null,
				InnerWriteCount: patternWriter.WriteCount,
				Notes: $"Direct straight-line pattern writer stub ending at 0x{patternWriter.EndOffset:X}. Loads: ebx32={patternWriter.ImmediateEbx32LoadCount}, bl8={patternWriter.ImmediateBl8LoadCount}. Writes: dword={patternWriter.DwordWriteCount}, word={patternWriter.WordWriteCount}, byte={patternWriter.ByteWriteCount}, imm32={patternWriter.ImmediateDwordWriteCount}, imm8={patternWriter.ImmediateByteWriteCount}. Bounds: offsets 0x{patternWriter.MinOffset:X}..0x{patternWriter.MaxOffset:X}, crop {patternWriter.Width}x{patternWriter.Height} at ({patternWriter.MinX},{patternWriter.MinY})..({patternWriter.MaxX},{patternWriter.MaxY}).");
		}

		return new Type1PayloadBlockSummary(
			BlockIndex: block.Index,
			Family: Type1PayloadFamily.Unknown,
			Signature: FormatSignature(signatureBytes),
			PayloadOffset: block.Value24,
			PrimaryStreamOffset: null,
			PrimaryStreamEndOffset: null,
			SecondaryStreamOffset: null,
			DeclaredWidth: block.Value08,
			DeclaredHeight: block.Value0C,
			OuterSegmentCount: null,
			InnerWriteCount: null,
			Notes: "Raw block does not match the 0x4D100, 0x4D31C, or 0x4D4DC loader-patched signatures recovered so far.");
	}

	private static Type1OuterStreamSummary Parse4D31COuterStream(ReadOnlySpan<byte> bytes, int streamStartOffset, int streamEndOffset)
	{
		int cursor = streamStartOffset;
		int rowGroupCount = 0;
		int rowCount = 0;
		int totalDwordCount = 0;
		int maxDwordCount = 0;

		while (cursor < streamEndOffset)
		{
			if (streamEndOffset - cursor < sizeof(int))
			{
				throw new InvalidDataException($"4D31C outer stream truncated at 0x{cursor:X}.");
			}

			int rowsInGroup = ReadInt32(bytes, cursor);
			cursor += sizeof(int);
			if (rowsInGroup <= 0)
			{
				throw new InvalidDataException($"4D31C outer stream has invalid row-group count {rowsInGroup} at 0x{cursor - 4:X}.");
			}

			rowGroupCount++;
			for (int rowIndex = 0; rowIndex < rowsInGroup; rowIndex++)
			{
				if (streamEndOffset - cursor < (sizeof(int) * 2))
				{
					throw new InvalidDataException($"4D31C outer stream row header truncated at 0x{cursor:X}.");
				}

				cursor += sizeof(int);
				int encodedDwordCount = ReadInt32(bytes, cursor);
				cursor += sizeof(int);

				if (encodedDwordCount < 0)
				{
					throw new InvalidDataException($"4D31C outer stream row has invalid encoded dword count {encodedDwordCount} at 0x{cursor - 4:X}.");
				}

				int dwordCount = checked(encodedDwordCount + 1);
				int dataByteCount = checked(dwordCount * sizeof(int));
				if (streamEndOffset - cursor < dataByteCount)
				{
					throw new InvalidDataException($"4D31C outer stream row data exceeds stream bounds at 0x{cursor:X}.");
				}

				cursor += dataByteCount;
				rowCount++;
				totalDwordCount += dwordCount;
				maxDwordCount = Math.Max(maxDwordCount, dwordCount);
			}
		}

		if (cursor != streamEndOffset)
		{
			throw new InvalidDataException(
				$"4D31C outer stream parsing ended at 0x{cursor:X}, expected 0x{streamEndOffset:X}.");
		}

		return new Type1OuterStreamSummary(rowGroupCount, rowCount, totalDwordCount, maxDwordCount);
	}

	private static bool TryParsePatternWriter(ReadOnlySpan<byte> bytes, int payloadOffset, out Type1PatternWriterSummary summary)
	{
		int cursor = payloadOffset;
		uint ebx = 0;
		int writeCount = 0;
		int dwordWriteCount = 0;
		int wordWriteCount = 0;
		int byteWriteCount = 0;
		int immediateDwordWriteCount = 0;
		int immediateByteWriteCount = 0;
		int immediateEbx32LoadCount = 0;
		int immediateBl8LoadCount = 0;
		int minOffset = int.MaxValue;
		int maxOffset = 0;

		while (cursor < bytes.Length)
		{
			byte opcode = bytes[cursor];
			switch (opcode)
			{
				case 0xBB:
					ebx = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(cursor + 1, sizeof(uint)));
					immediateEbx32LoadCount++;
					cursor += 5;
					break;

				case 0xB3:
					ebx = (ebx & 0xFFFFFF00U) | bytes[cursor + 1];
					immediateBl8LoadCount++;
					cursor += 2;
					break;

				case 0x89 when bytes[cursor + 1] == 0x98:
					RegisterWrite(ReadInt32(bytes, cursor + 2), sizeof(uint));
					dwordWriteCount++;
					writeCount++;
					cursor += 6;
					break;

				case 0x89 when bytes[cursor + 1] == 0x18:
					RegisterWrite(0, sizeof(uint));
					dwordWriteCount++;
					writeCount++;
					cursor += 2;
					break;

				case 0x89 when bytes[cursor + 1] == 0x58:
					RegisterWrite((sbyte)bytes[cursor + 2], sizeof(uint));
					dwordWriteCount++;
					writeCount++;
					cursor += 3;
					break;

				case 0x88 when bytes[cursor + 1] == 0x98:
					RegisterWrite(ReadInt32(bytes, cursor + 2), sizeof(byte));
					byteWriteCount++;
					writeCount++;
					cursor += 6;
					break;

				case 0x88 when bytes[cursor + 1] == 0x18:
					RegisterWrite(0, sizeof(byte));
					byteWriteCount++;
					writeCount++;
					cursor += 2;
					break;

				case 0x88 when bytes[cursor + 1] == 0xB8:
					RegisterWrite(ReadInt32(bytes, cursor + 2), sizeof(byte));
					byteWriteCount++;
					writeCount++;
					cursor += 6;
					break;

				case 0x88 when bytes[cursor + 1] == 0x58:
					RegisterWrite((sbyte)bytes[cursor + 2], sizeof(byte));
					byteWriteCount++;
					writeCount++;
					cursor += 3;
					break;

				case 0x88 when bytes[cursor + 1] == 0x78:
					RegisterWrite((sbyte)bytes[cursor + 2], sizeof(byte));
					byteWriteCount++;
					writeCount++;
					cursor += 3;
					break;

				case 0x66 when bytes[cursor + 1] == 0x89 && bytes[cursor + 2] == 0x98:
					RegisterWrite(ReadInt32(bytes, cursor + 3), sizeof(ushort));
					wordWriteCount++;
					writeCount++;
					cursor += 7;
					break;

				case 0x66 when bytes[cursor + 1] == 0x89 && bytes[cursor + 2] == 0x18:
					RegisterWrite(0, sizeof(ushort));
					wordWriteCount++;
					writeCount++;
					cursor += 3;
					break;

				case 0x66 when bytes[cursor + 1] == 0x89 && bytes[cursor + 2] == 0x58:
					RegisterWrite((sbyte)bytes[cursor + 3], sizeof(ushort));
					wordWriteCount++;
					writeCount++;
					cursor += 4;
					break;

				case 0xC7 when bytes[cursor + 1] == 0x00:
					RegisterWrite(0, sizeof(uint));
					immediateDwordWriteCount++;
					writeCount++;
					cursor += 6;
					break;

				case 0xC7 when bytes[cursor + 1] == 0x80:
					RegisterWrite(ReadInt32(bytes, cursor + 2), sizeof(uint));
					immediateDwordWriteCount++;
					writeCount++;
					cursor += 10;
					break;

				case 0xC7 when bytes[cursor + 1] == 0x40:
					RegisterWrite((sbyte)bytes[cursor + 2], sizeof(uint));
					immediateDwordWriteCount++;
					writeCount++;
					cursor += 7;
					break;

				case 0xC6 when bytes[cursor + 1] == 0x00:
					RegisterWrite(0, sizeof(byte));
					immediateByteWriteCount++;
					writeCount++;
					cursor += 3;
					break;

				case 0xC6 when bytes[cursor + 1] == 0x80:
					RegisterWrite(ReadInt32(bytes, cursor + 2), sizeof(byte));
					immediateByteWriteCount++;
					writeCount++;
					cursor += 7;
					break;

				case 0xC6 when bytes[cursor + 1] == 0x40:
					RegisterWrite((sbyte)bytes[cursor + 2], sizeof(byte));
					immediateByteWriteCount++;
					writeCount++;
					cursor += 4;
					break;

				case 0xC3:
					if (writeCount == 0)
					{
						summary = default!;
						return false;
					}

					summary = new Type1PatternWriterSummary(
						EndOffset: cursor + 1,
						WriteCount: writeCount,
						ImmediateEbx32LoadCount: immediateEbx32LoadCount,
						ImmediateBl8LoadCount: immediateBl8LoadCount,
						DwordWriteCount: dwordWriteCount,
						WordWriteCount: wordWriteCount,
						ByteWriteCount: byteWriteCount,
						ImmediateDwordWriteCount: immediateDwordWriteCount,
						ImmediateByteWriteCount: immediateByteWriteCount,
						MinOffset: minOffset,
						MaxOffset: maxOffset,
						MinX: minOffset % RuntimeStride,
						MinY: minOffset / RuntimeStride,
						MaxX: maxOffset % RuntimeStride,
						MaxY: maxOffset / RuntimeStride,
						Width: (maxOffset % RuntimeStride) - (minOffset % RuntimeStride) + 1,
						Height: (maxOffset / RuntimeStride) - (minOffset / RuntimeStride) + 1);
					return true;

				default:
					summary = default!;
					return false;
			}
		}

		summary = default!;
		return false;

		void RegisterWrite(int destinationOffset, int byteCount)
		{
			if (destinationOffset < 0)
			{
				throw new InvalidDataException($"Pattern-writer payload at 0x{payloadOffset:X} writes to a negative destination offset {destinationOffset}.");
			}

			minOffset = Math.Min(minOffset, destinationOffset);
			maxOffset = Math.Max(maxOffset, destinationOffset + byteCount - 1);
		}
	}

	private static void ValidateBounds(ReadOnlySpan<byte> bytes, int startOffset, int byteCount, int blockIndex, string surfaceName)
	{
		if (startOffset < 0 || byteCount < 0 || startOffset + byteCount > bytes.Length)
		{
			throw new InvalidDataException($"Type-1 payload block {blockIndex} {surfaceName} exceeds file bounds: start=0x{startOffset:X}, length=0x{byteCount:X}.");
		}
	}

	private static int ReadInt32(ReadOnlySpan<byte> bytes, int offset)
	{
		return BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(offset, sizeof(int)));
	}

	private static string FormatSignature(IEnumerable<byte> bytes)
	{
		return string.Join(" ", bytes.Select(value => value.ToString("X2")));
	}

	private static void WriteMetadata(
		string outputPath,
		CatGunDat dat,
		DatResourceEntry resource,
		DatPayloadGroup payloadGroup,
		IReadOnlyList<Type1PayloadBlockSummary> blockSummaries)
	{
		List<string> lines =
		[
			$"DAT: {dat.FilePath}",
			$"Resource: {resource.Name}",
			$"Payload group: 0x{payloadGroup.StartOffset:X}..0x{payloadGroup.EndOffset:X}",
			$"Payload loader types: {FormatLoaderTypeDistribution(payloadGroup.Blocks)}",
			$"Type-1 blocks inspected: {blockSummaries.Count}",
			$"Known families: {blockSummaries.Count(summary => summary.Family != Type1PayloadFamily.Unknown)}",
			string.Empty,
			"Blocks:",
		];

		foreach (Type1PayloadBlockSummary summary in blockSummaries)
		{
			lines.Add(
				$"[{summary.BlockIndex:D2}] family={summary.Family} sig={summary.Signature} payload=0x{summary.PayloadOffset:X} primary={(summary.PrimaryStreamOffset is null ? "<none>" : $"0x{summary.PrimaryStreamOffset:X}")}..{(summary.PrimaryStreamEndOffset is null ? "<unknown>" : $"0x{summary.PrimaryStreamEndOffset:X}")} secondary={(summary.SecondaryStreamOffset is null ? "<none>" : $"0x{summary.SecondaryStreamOffset:X}")} declared={summary.DeclaredWidth}x{summary.DeclaredHeight} groups={(summary.OuterSegmentCount is null ? "<n/a>" : summary.OuterSegmentCount.Value.ToString())} rows={(summary.InnerWriteCount is null ? "<n/a>" : summary.InnerWriteCount.Value.ToString())}");
			lines.Add($"  notes: {summary.Notes}");
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

internal sealed record Type1PayloadProbeExportResult(
	string ResourceName,
	string OutputDirectory,
	int BlockCount,
	int KnownFamilyCount);

internal sealed record Type1OuterStreamSummary(
	int RowGroupCount,
	int RowCount,
	int TotalDwordCount,
	int MaxDwordCount);

internal sealed record Type1PatternWriterSummary(
	int EndOffset,
	int WriteCount,
	int ImmediateEbx32LoadCount,
	int ImmediateBl8LoadCount,
	int DwordWriteCount,
	int WordWriteCount,
	int ByteWriteCount,
	int ImmediateDwordWriteCount,
	int ImmediateByteWriteCount,
	int MinOffset,
	int MaxOffset,
	int MinX,
	int MinY,
	int MaxX,
	int MaxY,
	int Width,
	int Height);

internal sealed record Type1PayloadBlockSummary(
	int BlockIndex,
	Type1PayloadFamily Family,
	string Signature,
	int PayloadOffset,
	int? PrimaryStreamOffset,
	int? PrimaryStreamEndOffset,
	int? SecondaryStreamOffset,
	int DeclaredWidth,
	int DeclaredHeight,
	int? OuterSegmentCount,
	int? InnerWriteCount,
	string Notes);

internal enum Type1PayloadFamily
{
	Unknown,
	Aligned16x16Copy,
	VariableRowCopy,
	ScatterWriteHelper,
	PatternWriter,
}
