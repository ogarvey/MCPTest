using System.Buffers.Binary;
using System.Text;
using DogKnife.Models;

namespace DogKnife.Helpers;

internal sealed class CatGunDat
{
	private const int HeaderSize = 0x6C;
	private const int CellReferenceEntrySize = 7;
	private const int LayerCount = 3;
	private const int LayerDescriptorSize = 0x10;
	private const int ResourceEntrySize = 0x28;

	private CatGunDat(
		string filePath,
		CatGunDatHeader header,
		IReadOnlyList<DatCellReferenceEntry> cellReferences,
		int cellReferencePaddingByteCount,
		IReadOnlyList<DatLayer> layers,
		IReadOnlyList<DatSequenceGroup> sequenceGroups,
		IReadOnlyList<DatPayloadGroup> payloadGroups,
		IReadOnlyList<DatResourceEntry> resources)
	{
		FilePath = filePath;
		Header = header;
		CellReferences = cellReferences;
		CellReferencePaddingByteCount = cellReferencePaddingByteCount;
		Layers = layers;
		SequenceGroups = sequenceGroups;
		PayloadGroups = payloadGroups;
		Resources = resources;
	}

	public string FilePath { get; }

	public CatGunDatHeader Header { get; }

	public IReadOnlyList<DatCellReferenceEntry> CellReferences { get; }

	public int CellReferencePaddingByteCount { get; }

	public IReadOnlyList<DatLayer> Layers { get; }

	public IReadOnlyList<DatSequenceGroup> SequenceGroups { get; }

	public IReadOnlyList<DatPayloadGroup> PayloadGroups { get; }

	public IReadOnlyList<DatResourceEntry> Resources { get; }

	public static CatGunDat Load(string filePath)
	{
		byte[] bytes = File.ReadAllBytes(filePath);

		if (bytes.Length < HeaderSize)
		{
			throw new InvalidDataException($"DAT file is too small to contain the 0x{HeaderSize:X} header: {filePath}");
		}

		CatGunDatHeader header = new(
			Type: bytes[0x00],
			Variant: bytes[0x01],
			Value04: ReadInt32(bytes, 0x04),
			Byte08: bytes[0x08],
			Byte09: bytes[0x09],
			Byte0A: bytes[0x0A],
			Byte0B: bytes[0x0B],
			Byte0C: bytes[0x0C],
			Table40EntryCount: ReadInt32(bytes, 0x10),
			ResourceEntryCount: ReadInt32(bytes, 0x14),
			Table64EntryCount: ReadInt32(bytes, 0x18),
			CellReferenceTableOffset: ReadInt32(bytes, 0x1C),
			Offset20: ReadInt32(bytes, 0x20),
			Offset24: ReadInt32(bytes, 0x24),
			Offset28: ReadInt32(bytes, 0x28),
			RelativeOffset2C: ReadInt32(bytes, 0x2C),
			Offset30: ReadInt32(bytes, 0x30),
			Value34: ReadInt32(bytes, 0x34),
			Offset38: ReadInt32(bytes, 0x38),
			Offset3C: ReadInt32(bytes, 0x3C),
			Table40Offset: ReadInt32(bytes, 0x40),
			Offset44: ReadInt32(bytes, 0x44),
			Offset48: ReadInt32(bytes, 0x48),
			ResourceTableOffset: ReadInt32(bytes, 0x4C),
			PatchTableOffset: ReadInt32(bytes, 0x50),
			PaletteTableOffset: ReadInt32(bytes, 0x54),
			LayerTableOffset: ReadInt32(bytes, 0x58),
			Offset5C: ReadInt32(bytes, 0x5C),
			Offset60: ReadInt32(bytes, 0x60),
			Offset64: ReadInt32(bytes, 0x64),
			Offset68: ReadInt32(bytes, 0x68));

		ValidateResourceTableBounds(bytes.Length, header.ResourceTableOffset, header.ResourceEntryCount);
		IReadOnlyList<DatResourceEntry> resources = ParseResources(bytes, header.ResourceTableOffset, header.ResourceEntryCount);
		(int nextBlockOffset, int cellReferencePaddingByteCount) = FindCellReferenceTableBounds(header, bytes.Length);
		IReadOnlyList<DatCellReferenceEntry> cellReferences = ParseCellReferences(
			bytes,
			header.CellReferenceTableOffset,
			nextBlockOffset,
			resources);
		IReadOnlyList<DatLayer> layers = ParseLayers(bytes, header.LayerTableOffset);
		IReadOnlyList<DatSequenceGroup> sequenceGroups = ParseSequenceGroups(bytes, header, resources);
		IReadOnlyList<DatPayloadGroup> payloadGroups = ParsePayloadGroups(bytes, header, resources);

		return new CatGunDat(
			Path.GetFullPath(filePath),
			header,
			cellReferences,
			cellReferencePaddingByteCount,
			layers,
			sequenceGroups,
			payloadGroups,
			resources);
	}

	private static IReadOnlyList<DatSequenceGroup> ParseSequenceGroups(
		byte[] bytes,
		CatGunDatHeader header,
		IReadOnlyList<DatResourceEntry> resources)
	{
		int[] globalBoundaryCandidates = GetGlobalBoundaryCandidates(header, bytes.Length);
		int[] resourcePointerCandidates = GetResourcePointerCandidates(resources);

		List<int> uniqueSequenceOffsets = resources
			.Select(resource => resource.Pointer08)
			.Where(offset => offset > 0 && offset < bytes.Length)
			.Distinct()
			.OrderBy(offset => offset)
			.ToList();

		List<DatSequenceGroup> sequenceGroups = new(uniqueSequenceOffsets.Count);

		foreach (int sequenceOffset in uniqueSequenceOffsets)
		{
			int nextBoundary = FindNextBoundary(sequenceOffset, resourcePointerCandidates, globalBoundaryCandidates, bytes.Length);
			int byteCount = nextBoundary - sequenceOffset;

			if (byteCount < 0)
			{
				throw new InvalidDataException(
					$"Invalid sequence bounds at 0x{sequenceOffset:X}: next boundary 0x{nextBoundary:X}");
			}

			byte[] rawBytes = bytes.AsSpan(sequenceOffset, byteCount).ToArray();
			int parsedByteCount = rawBytes.LastIndexOf((byte)0xFF) switch
			{
				>= 0 and int lastDelimiterIndex => lastDelimiterIndex + 1,
				_ => rawBytes.Length,
			};
			int trailingByteCount = rawBytes.Length - parsedByteCount;
			List<DatSequenceSegment> segments = new();
			int segmentStart = 0;

			for (int index = 0; index < parsedByteCount; index++)
			{
				if (rawBytes[index] != 0xFF)
				{
					continue;
				}

				if (index > segmentStart)
				{
					segments.Add(new DatSequenceSegment(
						Index: segments.Count,
						StartOffset: sequenceOffset + segmentStart,
						ByteCount: index - segmentStart,
						Bytes: rawBytes.AsSpan(segmentStart, index - segmentStart).ToArray()));
				}

				segmentStart = index + 1;
			}

			if (segmentStart < parsedByteCount)
			{
				segments.Add(new DatSequenceSegment(
					Index: segments.Count,
					StartOffset: sequenceOffset + segmentStart,
					ByteCount: parsedByteCount - segmentStart,
					Bytes: rawBytes.AsSpan(segmentStart, parsedByteCount - segmentStart).ToArray()));
			}

			List<string> resourceNames = resources
				.Where(resource => resource.Pointer08 == sequenceOffset)
				.Select(resource => resource.Name)
				.OrderBy(name => name, StringComparer.Ordinal)
				.ToList();

			sequenceGroups.Add(new DatSequenceGroup(
				StartOffset: sequenceOffset,
				EndOffset: nextBoundary,
				ByteCount: byteCount,
				TrailingByteCount: trailingByteCount,
				DelimiterCount: rawBytes.Count(value => value == 0xFF),
				ResourceNames: resourceNames,
				Segments: segments,
				RawBytes: rawBytes));
		}

		return sequenceGroups;
	}

	private static IReadOnlyList<DatPayloadGroup> ParsePayloadGroups(
		byte[] bytes,
		CatGunDatHeader header,
		IReadOnlyList<DatResourceEntry> resources)
	{
		int[] globalBoundaryCandidates = GetGlobalBoundaryCandidates(header, bytes.Length);
		int[] resourcePointerCandidates = GetResourcePointerCandidates(resources);

		List<int> uniquePayloadOffsets = resources
			.Select(resource => resource.Pointer04)
			.Where(offset => offset > 0 && offset < bytes.Length)
			.Distinct()
			.OrderBy(offset => offset)
			.ToList();

		List<DatPayloadGroup> payloadGroups = new(uniquePayloadOffsets.Count);

		foreach (int payloadOffset in uniquePayloadOffsets)
		{
			int nextBoundary = FindNextBoundary(payloadOffset, resourcePointerCandidates, globalBoundaryCandidates, bytes.Length);

			int byteCount = nextBoundary - payloadOffset;
			if (byteCount < 0)
			{
				throw new InvalidDataException(
					$"Invalid payload bounds at 0x{payloadOffset:X}: next boundary 0x{nextBoundary:X}");
			}

			List<DatPayloadBlock30> blocks = new(byteCount / 0x30);
			for (int blockIndex = 0; blockIndex < byteCount / 0x30; blockIndex++)
			{
				int blockOffset = payloadOffset + (blockIndex * 0x30);
				blocks.Add(new DatPayloadBlock30(
					Index: blockIndex,
					Offset: blockOffset,
					Value00: ReadInt32(bytes, blockOffset + 0x00),
					Value04: ReadInt32(bytes, blockOffset + 0x04),
					Value08: ReadInt32(bytes, blockOffset + 0x08),
					Value0C: ReadInt32(bytes, blockOffset + 0x0C),
					Value10: ReadInt32(bytes, blockOffset + 0x10),
					Value14: ReadInt32(bytes, blockOffset + 0x14),
					Value18: ReadInt32(bytes, blockOffset + 0x18),
					Value1C: ReadInt32(bytes, blockOffset + 0x1C),
					Value20: ReadInt32(bytes, blockOffset + 0x20),
					Value24: ReadInt32(bytes, blockOffset + 0x24),
					Value28: ReadInt32(bytes, blockOffset + 0x28),
					Value2C: ReadInt32(bytes, blockOffset + 0x2C)));
			}

			List<string> resourceNames = resources
				.Where(resource => resource.Pointer04 == payloadOffset)
				.Select(resource => resource.Name)
				.OrderBy(name => name, StringComparer.Ordinal)
				.ToList();

			payloadGroups.Add(new DatPayloadGroup(
				StartOffset: payloadOffset,
				EndOffset: nextBoundary,
				ByteCount: byteCount,
				TrailingByteCount: byteCount % 0x30,
				ResourceNames: resourceNames,
				Blocks: blocks));
		}

		return payloadGroups;
	}

	private static int[] GetGlobalBoundaryCandidates(CatGunDatHeader header, int fileLength)
	{
		return
		[
			header.CellReferenceTableOffset,
			header.Offset20,
			header.Offset24,
			header.Offset28,
			header.Offset30,
			header.Offset38,
			header.Offset3C,
			header.Table40Offset,
			header.Offset44,
			header.Offset48,
			header.ResourceTableOffset,
			header.PatchTableOffset,
			header.PaletteTableOffset,
			header.LayerTableOffset,
			header.Offset5C,
			header.Offset60,
			header.Offset64,
			header.Offset68,
			fileLength,
		];
	}

	private static int[] GetResourcePointerCandidates(IReadOnlyList<DatResourceEntry> resources)
	{
		return resources
			.SelectMany(resource => new[] { resource.Pointer04, resource.Pointer08, resource.Pointer0C, resource.Pointer14, resource.Pointer18 })
			.Where(offset => offset > 0)
			.ToArray();
	}

	private static int FindNextBoundary(
		int startOffset,
		IReadOnlyList<int> resourcePointerCandidates,
		IReadOnlyList<int> globalBoundaryCandidates,
		int fileLength)
	{
		return resourcePointerCandidates
			.Concat(globalBoundaryCandidates)
			.Where(offset => offset > startOffset && offset <= fileLength)
			.DefaultIfEmpty(fileLength)
			.Min();
	}

	private static (int NextBlockOffset, int PaddingByteCount) FindCellReferenceTableBounds(CatGunDatHeader header, int fileLength)
	{
		int[] candidates =
		[
			header.Offset20,
			header.Offset24,
			header.Offset28,
			header.Offset30,
			header.Offset38,
			header.Offset3C,
			header.Table40Offset,
			header.Offset44,
			header.Offset48,
			header.ResourceTableOffset,
			header.PatchTableOffset,
			header.PaletteTableOffset,
			header.LayerTableOffset,
			header.Offset5C,
			header.Offset60,
			header.Offset64,
			header.Offset68,
			fileLength,
		];

		int nextBlockOffset = candidates
			.Where(offset => offset > header.CellReferenceTableOffset && offset <= fileLength)
			.DefaultIfEmpty(fileLength)
			.Min();

		int byteCount = nextBlockOffset - header.CellReferenceTableOffset;
		if (byteCount < 0)
		{
			throw new InvalidDataException(
				$"Invalid DAT cell-reference table bounds: start=0x{header.CellReferenceTableOffset:X}, next=0x{nextBlockOffset:X}");
		}

		return (nextBlockOffset, byteCount % CellReferenceEntrySize);
	}

	private static IReadOnlyList<DatCellReferenceEntry> ParseCellReferences(
		byte[] bytes,
		int cellReferenceTableOffset,
		int nextBlockOffset,
		IReadOnlyList<DatResourceEntry> resources)
	{
		int byteCount = nextBlockOffset - cellReferenceTableOffset;
		int entryCount = byteCount / CellReferenceEntrySize;
		List<DatCellReferenceEntry> cellReferences = new(entryCount);

		for (int index = 0; index < entryCount; index++)
		{
			int entryOffset = cellReferenceTableOffset + (index * CellReferenceEntrySize);
			byte resourceIndex = bytes[entryOffset + 5];
			string? resourceName = resourceIndex < resources.Count
				? resources[resourceIndex].Name
				: null;

			cellReferences.Add(new DatCellReferenceEntry(
				Index: index,
				EntryOffset: entryOffset,
				Value00: ReadUInt16(bytes, entryOffset + 0x00),
				Byte02: bytes[entryOffset + 0x02],
				Byte03: bytes[entryOffset + 0x03],
				Byte04: bytes[entryOffset + 0x04],
				ResourceIndex: resourceIndex,
				Byte06: bytes[entryOffset + 0x06],
				ResourceName: resourceName));
		}

		return cellReferences;
	}

	private static IReadOnlyList<DatLayer> ParseLayers(byte[] bytes, int layerTableOffset)
	{
		if (layerTableOffset < 0 || layerTableOffset >= bytes.Length)
		{
			throw new InvalidDataException($"Invalid DAT layer table offset: 0x{layerTableOffset:X}");
		}

		List<DatLayer> layers = new(LayerCount);
		int cursor = layerTableOffset;

		for (int index = 0; index < LayerCount; index++)
		{
			if (cursor + LayerDescriptorSize > bytes.Length)
			{
				throw new InvalidDataException($"Layer descriptor {index} exceeds file bounds at 0x{cursor:X}");
			}

			int value00 = ReadInt32(bytes, cursor + 0x00);
			int value04 = ReadInt32(bytes, cursor + 0x04);
			int width = ReadInt32(bytes, cursor + 0x08);
			int height = ReadInt32(bytes, cursor + 0x0C);

			if (width < 0 || height < 0)
			{
				throw new InvalidDataException($"Invalid DAT layer dimensions at 0x{cursor:X}: {width}x{height}");
			}

			int cellDataOffset = cursor + LayerDescriptorSize;
			int cellCount = checked(width * height);
			long cellDataEnd = cellDataOffset + ((long)cellCount * sizeof(uint));

			if (cellDataEnd > bytes.Length)
			{
				throw new InvalidDataException(
					$"Layer {index} cell data exceeds file bounds: offset=0x{cellDataOffset:X}, count={cellCount}, end=0x{cellDataEnd:X}");
			}

			uint[] cells = new uint[cellCount];
			int nonZeroCellCount = 0;
			int maxReferenceIndex = 0;

			for (int cellIndex = 0; cellIndex < cellCount; cellIndex++)
			{
				uint value = ReadUInt32(bytes, cellDataOffset + (cellIndex * sizeof(uint)));
				cells[cellIndex] = value;

				int referenceIndex = (int)(value & 0xFFFF);
				if (referenceIndex != 0)
				{
					nonZeroCellCount++;
					maxReferenceIndex = Math.Max(maxReferenceIndex, referenceIndex);
				}
			}

			layers.Add(new DatLayer(
				Index: index,
				DescriptorOffset: cursor,
				Value00: value00,
				Value04: value04,
				Width: width,
				Height: height,
				CellDataOffset: cellDataOffset,
				Cells: cells,
				NonZeroCellCount: nonZeroCellCount,
				MaxReferenceIndex: maxReferenceIndex));

			cursor = checked((int)cellDataEnd);
		}

		return layers;
	}

	private static IReadOnlyList<DatResourceEntry> ParseResources(byte[] bytes, int resourceTableOffset, int resourceCount)
	{
		List<DatResourceEntry> resources = new(resourceCount);

		for (int index = 0; index < resourceCount; index++)
		{
			int entryOffset = resourceTableOffset + (index * ResourceEntrySize);
			int nameOffset = ReadInt32(bytes, entryOffset + 0x00);
			string name = ReadNullTerminatedAscii(bytes, nameOffset);

			resources.Add(new DatResourceEntry(
				Index: index,
				EntryOffset: entryOffset,
				NameOffset: nameOffset,
				Name: name,
				Pointer04: ReadInt32(bytes, entryOffset + 0x04),
				Pointer08: ReadInt32(bytes, entryOffset + 0x08),
				Pointer0C: ReadInt32(bytes, entryOffset + 0x0C),
				Value10: ReadInt32(bytes, entryOffset + 0x10),
				Pointer14: ReadInt32(bytes, entryOffset + 0x14),
				Pointer18: ReadInt32(bytes, entryOffset + 0x18),
				Value1C: ReadInt32(bytes, entryOffset + 0x1C),
				Value20: ReadInt32(bytes, entryOffset + 0x20),
				Value24: ReadInt32(bytes, entryOffset + 0x24)));
		}

		return resources;
	}

	private static void ValidateResourceTableBounds(int fileLength, int resourceTableOffset, int resourceCount)
	{
		if (resourceCount < 0)
		{
			throw new InvalidDataException($"Invalid DAT resource count: {resourceCount}");
		}

		if (resourceTableOffset < 0)
		{
			throw new InvalidDataException($"Invalid DAT resource table offset: 0x{resourceTableOffset:X}");
		}

		long endOffset = resourceTableOffset + ((long)resourceCount * ResourceEntrySize);
		if (endOffset > fileLength)
		{
			throw new InvalidDataException(
				$"DAT resource table exceeds file bounds: offset=0x{resourceTableOffset:X}, count={resourceCount}, end=0x{endOffset:X}, fileSize=0x{fileLength:X}");
		}
	}

	private static int ReadInt32(byte[] bytes, int offset)
	{
		return BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, sizeof(int)));
	}

	private static ushort ReadUInt16(byte[] bytes, int offset)
	{
		return BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, sizeof(ushort)));
	}

	private static uint ReadUInt32(byte[] bytes, int offset)
	{
		return BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, sizeof(uint)));
	}

	private static string ReadNullTerminatedAscii(byte[] bytes, int offset)
	{
		if (offset <= 0 || offset >= bytes.Length)
		{
			throw new InvalidDataException($"Invalid DAT string offset: 0x{offset:X}");
		}

		int end = offset;
		while (end < bytes.Length && bytes[end] != 0)
		{
			end++;
		}

		if (end == bytes.Length)
		{
			throw new InvalidDataException($"Unterminated DAT string at offset 0x{offset:X}");
		}

		return Encoding.ASCII.GetString(bytes, offset, end - offset);
	}
}
