using System.Text;

static class FamInspector
{
	private const int HeaderSize = 6;
	private const int DirectoryEntrySize = 16;
	private const int NameFieldSize = 12;
	private const int SecondaryHeaderSize = 6;
	private const int FirstChunkLzOffset = 0x2C;
	private const int SecondChunkLzOffset = 0x04;
	private const int SecondaryPaletteTailLength = 0x400;
	private const int SecondaryLookupTableLength = 0x10000;
	private static readonly Encoding AsciiEncoding = Encoding.ASCII;

	public static FamInspection Inspect(string path, bool retainDecompressedPayloads = false)
	{
		var data = File.ReadAllBytes(path);

		if (data.Length < HeaderSize)
		{
			throw new InvalidDataException($"{path} is smaller than the FAM header.");
		}

		var magic = AsciiEncoding.GetString(data, 0, 4).TrimEnd('\0');
		var firstTableCount = ReadUInt16(data, 4);
		var firstTableOffset = HeaderSize;
		var firstTableSize = firstTableCount * DirectoryEntrySize;
		var firstTableEndOffset = firstTableOffset + firstTableSize;

		if (firstTableEndOffset + SecondaryHeaderSize > data.Length)
		{
			throw new InvalidDataException($"{path} declares {firstTableCount} first-table FAM entries, but the first table overruns the file.");
		}

		var secondHeaderOffset = firstTableEndOffset;
		var secondHeaderValue = ReadUInt32(data, secondHeaderOffset);
		var secondTableCount = ReadUInt16(data, secondHeaderOffset + 4);
		var secondTableOffset = secondHeaderOffset + SecondaryHeaderSize;
		var secondTableSize = secondTableCount * DirectoryEntrySize;
		var secondTableEndOffset = secondTableOffset + secondTableSize;

		if (secondTableEndOffset > data.Length)
		{
			throw new InvalidDataException($"{path} declares {secondTableCount} second-table FAM entries, but the second table overruns the file.");
		}

		var entryManifests = new List<FamEntryManifest>(firstTableCount + secondTableCount);
		entryManifests.AddRange(ParseEntryManifests(data, firstTableOffset, firstTableCount, "first"));
		entryManifests.AddRange(ParseEntryManifests(data, secondTableOffset, secondTableCount, "second"));

		var sortedOffsets = entryManifests
			.Select(manifest => manifest.DataOffset)
			.Where(offset => offset >= secondTableEndOffset && offset < data.Length)
			.Distinct()
			.OrderBy(offset => offset)
			.ToList();

		var entryPayloads = BuildEntryPayloads(
			data,
			entryManifests,
			sortedOffsets,
			secondTableEndOffset,
			retainDecompressedPayloads);

		var manifestModel = new FamManifest
		{
			FileName = Path.GetFileName(path),
			FullPath = path,
			Magic = magic,
			FirstTableCount = firstTableCount,
			FirstTableOffset = firstTableOffset,
			FirstTableEndOffset = firstTableEndOffset,
			SecondHeaderOffset = secondHeaderOffset,
			SecondHeaderValue = secondHeaderValue,
			SecondTableCount = secondTableCount,
			SecondTableOffset = secondTableOffset,
			SecondTableEndOffset = secondTableEndOffset,
			HeaderSize = HeaderSize,
			EntrySize = DirectoryEntrySize,
			Entries = entryManifests
		};

		return new FamInspection(
			manifestModel,
			entryPayloads,
			data.AsSpan(0, HeaderSize).ToArray(),
			data.AsSpan(firstTableOffset, firstTableSize).ToArray(),
			data.AsSpan(secondHeaderOffset, SecondaryHeaderSize).ToArray(),
			data.AsSpan(secondTableOffset, secondTableSize).ToArray());
	}

	private static List<FamEntryManifest> ParseEntryManifests(
		byte[] data,
		int tableOffset,
		int entryCount,
		string tableName)
	{
		var manifests = new List<FamEntryManifest>(entryCount);

		for (var index = 0; index < entryCount; index++)
		{
			var entryOffset = tableOffset + (index * DirectoryEntrySize);
			var rawNameBytes = data.AsSpan(entryOffset, NameFieldSize).ToArray();
			var dataOffset = ReadUInt32(data, entryOffset + NameFieldSize);

			manifests.Add(new FamEntryManifest
			{
				TableName = tableName,
				Index = index,
				DecodedName = DecodeName(rawNameBytes, index),
				RawNameHex = Convert.ToHexString(rawNameBytes),
				DataOffset = dataOffset
			});
		}

		return manifests;
	}

	private static List<FamEntryPayload> BuildEntryPayloads(
		byte[] data,
		List<FamEntryManifest> manifests,
		List<uint> sortedOffsets,
		int minimumDataOffset,
		bool retainDecompressedPayloads)
	{
		var payloads = new List<FamEntryPayload>(manifests.Count);

		foreach (var manifest in manifests)
		{
			var dataOffset = manifest.DataOffset;
			var nextOffset = FindNextOffset(sortedOffsets, dataOffset, (uint)data.Length);

			if (dataOffset < minimumDataOffset || dataOffset > data.Length)
			{
				throw new InvalidDataException($"FAM {manifest.TableName} entry {manifest.Index} points outside the data region: 0x{dataOffset:X8}.");
			}

			if (nextOffset < dataOffset || nextOffset > data.Length)
			{
				throw new InvalidDataException($"FAM {manifest.TableName} entry {manifest.Index} has an invalid next offset: 0x{nextOffset:X8}.");
			}

			var payloadLength = checked((int)(nextOffset - dataOffset));
			var payload = data.AsSpan((int)dataOffset, payloadLength).ToArray();
			manifest.DataSpan = (uint)payloadLength;
			manifest.FirstBytesHex = BuildHexPreview(payload, 24);
			manifest.AsciiPreview.AddRange(ExtractAsciiPreview(payload, 8, 256));

			byte[]? decompressedPayload = null;
			if (manifest.TableName == "first")
			{
				PopulateFirstTableMetadata(manifest, payload, retainDecompressedPayloads, out decompressedPayload);
			}
			else
			{
				PopulateSecondTableMetadata(manifest, payload, retainDecompressedPayloads, out decompressedPayload);
			}

			payloads.Add(new FamEntryPayload(manifest, payload, decompressedPayload));
		}

		return payloads;
	}

	private static void PopulateFirstTableMetadata(
		FamEntryManifest manifest,
		byte[] payload,
		bool retainDecompressedPayloads,
		out byte[]? decompressedPayload)
	{
		decompressedPayload = null;

		if (payload.Length < FirstChunkLzOffset + 4)
		{
			return;
		}

		manifest.LeadingStoredSize = ReadUInt32(payload, 0);
		manifest.EmbeddedName = DecodeName(payload.AsSpan(4, NameFieldSize).ToArray(), manifest.Index);
		manifest.Field10 = ReadUInt16(payload, 0x10);
		manifest.Field12 = ReadUInt16(payload, 0x12);
		manifest.Field14 = ReadUInt16(payload, 0x14);
		manifest.Field16 = ReadUInt16(payload, 0x16);
		manifest.HeaderTailHex = Convert.ToHexString(payload.AsSpan(0x18, 0x14));
		manifest.HeaderDword28 = ReadUInt32(payload, 0x28);
		manifest.LzStreamOffset = FirstChunkLzOffset;
		manifest.LeadingStoredSizeMatchesPayloadSpan = manifest.LeadingStoredSize == payload.Length - FirstChunkLzOffset;
		manifest.DecompressedSize = ReadUInt32(payload, FirstChunkLzOffset);

		if (manifest.Field10 is { } field10 && manifest.Field12 is { } field12)
		{
			var cellCount = (ulong)field10 * field12;
			if (cellCount <= uint.MaxValue)
			{
				manifest.CellCountCandidate = (uint)cellCount;
			}

			if (manifest.DecompressedSize is { } decompressedSize)
			{
				manifest.MatchesFiveBytesPerCell = cellCount * 5 == decompressedSize;
			}
		}

		PopulateLzMetadata(manifest, payload, FirstChunkLzOffset, retainDecompressedPayloads, out decompressedPayload);
	}

	private static void PopulateSecondTableMetadata(
		FamEntryManifest manifest,
		byte[] payload,
		bool retainDecompressedPayloads,
		out byte[]? decompressedPayload)
	{
		decompressedPayload = null;

		if (payload.Length < SecondChunkLzOffset + 4)
		{
			return;
		}

		manifest.LeadingStoredSize = ReadUInt32(payload, 0);
		manifest.LzStreamOffset = SecondChunkLzOffset;
		manifest.LeadingStoredSizeMatchesPayloadSpan = manifest.LeadingStoredSize == payload.Length - SecondChunkLzOffset;
		manifest.DecompressedSize = ReadUInt32(payload, SecondChunkLzOffset);
		PopulateLzMetadata(manifest, payload, SecondChunkLzOffset, retainDecompressedPayloads, out decompressedPayload);

		if (manifest.LzDecodeSucceeded == true && manifest.DecompressedSize is { } decompressedSize)
		{
			var tailTotalLength = SecondaryPaletteTailLength + SecondaryLookupTableLength;
			if (decompressedSize >= tailTotalLength)
			{
				manifest.RuntimePaletteOffset = (int)decompressedSize - tailTotalLength;
				manifest.RuntimePaletteLength = SecondaryPaletteTailLength;
				manifest.RuntimeLookupTableOffset = (int)decompressedSize - SecondaryLookupTableLength;
				manifest.RuntimeLookupTableLength = SecondaryLookupTableLength;
			}
		}
	}

	private static void PopulateLzMetadata(
		FamEntryManifest manifest,
		byte[] payload,
		int lzStreamOffset,
		bool retainDecompressedPayloads,
		out byte[]? decompressedPayload)
	{
		decompressedPayload = null;

		if (!TryDecompressLz(payload, lzStreamOffset, out var decoded, out var bytesConsumed, out var error))
		{
			manifest.LzDecodeSucceeded = false;
			manifest.LzDecodeError = error;
			return;
		}

		manifest.LzDecodeSucceeded = true;
		manifest.LzBytesConsumed = bytesConsumed;
		if (retainDecompressedPayloads)
		{
			decompressedPayload = decoded;
		}
	}

	public static bool TryDecompressLz(
		byte[] payload,
		int startOffset,
		out byte[] decoded,
		out int bytesConsumed,
		out string? error)
	{
		decoded = Array.Empty<byte>();
		bytesConsumed = 0;
		error = null;

		if (payload.Length < startOffset + 4)
		{
			error = "Payload is too small to contain the LZ size dword.";
			return false;
		}

		var expectedLength64 = ReadUInt32(payload, startOffset);
		if (expectedLength64 > int.MaxValue)
		{
			error = $"LZ output size 0x{expectedLength64:X8} is too large to decode in memory.";
			return false;
		}

		var expectedLength = checked((int)expectedLength64);
		decoded = new byte[expectedLength];
		var window = new byte[0x1000];
		var sourceOffset = startOffset + 4;
		var outputOffset = 0;
		var flags = 0;
		var mask = 0;

		while (outputOffset < expectedLength)
		{
			if (mask == 0)
			{
				if (sourceOffset >= payload.Length)
				{
					error = "LZ stream ended before all output bytes were produced.";
					decoded = Array.Empty<byte>();
					return false;
				}

				flags = payload[sourceOffset++];
				mask = 0x80;
			}

			if ((flags & mask) != 0)
			{
				if (sourceOffset >= payload.Length)
				{
					error = "LZ literal run overran the payload.";
					decoded = Array.Empty<byte>();
					return false;
				}

				var value = payload[sourceOffset++];
				window[outputOffset & 0x0FFF] = value;
				decoded[outputOffset++] = value;
			}
			else
			{
				if (sourceOffset + 1 >= payload.Length)
				{
					error = "LZ back-reference overran the payload.";
					decoded = Array.Empty<byte>();
					return false;
				}

				var pair = (payload[sourceOffset] << 8) | payload[sourceOffset + 1];
				sourceOffset += 2;
				var windowOffset = (pair >> 4) & 0x0FFF;
				var count = (pair & 0x0F) + 3;

				for (var index = 0; index < count && outputOffset < expectedLength; index++)
				{
					var value = window[windowOffset];
					window[outputOffset & 0x0FFF] = value;
					decoded[outputOffset++] = value;
					windowOffset = (windowOffset + 1) & 0x0FFF;
				}
			}

			mask >>= 1;
		}

		bytesConsumed = sourceOffset - startOffset;
		return true;
	}

	private static uint FindNextOffset(List<uint> sortedOffsets, uint currentOffset, uint fallback)
	{
		foreach (var offset in sortedOffsets)
		{
			if (offset > currentOffset)
			{
				return offset;
			}
		}

		return fallback;
	}

	private static string DecodeName(byte[] rawNameBytes, int index)
	{
		var zeroIndex = Array.IndexOf(rawNameBytes, (byte)0);
		var count = zeroIndex >= 0 ? zeroIndex : rawNameBytes.Length;
		var decoded = AsciiEncoding.GetString(rawNameBytes, 0, count);
		return string.IsNullOrEmpty(decoded) ? $"entry_{index:D3}" : decoded;
	}

	private static string BuildHexPreview(byte[] payload, int maxBytes)
	{
		var previewLength = Math.Min(maxBytes, payload.Length);
		if (previewLength == 0)
		{
			return string.Empty;
		}

		return Convert.ToHexString(payload.AsSpan(0, previewLength));
	}

	private static List<string> ExtractAsciiPreview(byte[] payload, int maxStrings, int maxBytes)
	{
		var preview = new List<string>(maxStrings);
		var limit = Math.Min(payload.Length, maxBytes);
		var start = -1;

		for (var index = 0; index < limit; index++)
		{
			var value = payload[index];
			var isPrintable = value >= 32 && value <= 126;

			if (isPrintable)
			{
				if (start < 0)
				{
					start = index;
				}
				continue;
			}

			TryAppendAsciiPreview(payload, start, index, preview, maxStrings);
			if (preview.Count == maxStrings)
			{
				return preview;
			}

			start = -1;
		}

		TryAppendAsciiPreview(payload, start, limit, preview, maxStrings);
		return preview;
	}

	private static void TryAppendAsciiPreview(
		byte[] payload,
		int start,
		int end,
		List<string> preview,
		int maxStrings)
	{
		if (start < 0 || preview.Count >= maxStrings)
		{
			return;
		}

		var length = end - start;
		if (length < 4)
		{
			return;
		}

		preview.Add(AsciiEncoding.GetString(payload, start, length));
	}

	private static ushort ReadUInt16(byte[] data, int offset)
	{
		return BitConverter.ToUInt16(data, offset);
	}

	private static uint ReadUInt32(byte[] data, int offset)
	{
		return BitConverter.ToUInt32(data, offset);
	}
}
