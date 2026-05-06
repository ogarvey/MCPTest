using System.Text;

static class ArchiveInspector
{
	private const int HeaderSize = 0x408;
	private const int SpbEntrySize = 36;
	private const int MobTopLevelEntrySize = 40;
	private const int MobMetadataRecordSize = 36;
	private const int MobMetadataElementSize = 20;
	private const int HeaderPaletteOffset = 0x06;
	private const int HeaderPaletteEntryCount = 255;
	private const string HeaderPaletteFormat = "RGBX";
	private static readonly Encoding KoreanEncoding = Encoding.GetEncoding(949);
	public const int LiveWorldSpriteDataItemIndex = 0x0B;

	public static ArchiveInspection Inspect(string path)
	{
		var data = File.ReadAllBytes(path);

		if (data.Length < HeaderSize)
		{
			throw new InvalidDataException($"{path} is smaller than the fixed archive header.");
		}

		var typeCode = ReadUInt16(data, 0x0000);
		var headerTail = ReadUInt32(data, 0x0404);
		var entryCount = (ushort)(headerTail >> 16);
		var headerMarker = (ushort)(headerTail & 0xFFFF);
		var headerPaletteBytes = TryReadHeaderPaletteBytes(data);
		var headerPalette = headerPaletteBytes is not null
			? IndexedPalette.FromHeaderRgbx(headerPaletteBytes, HeaderPaletteOffset, HeaderPaletteEntryCount)
			: null;
		var entrySize = typeCode == 0x2712 ? MobTopLevelEntrySize : SpbEntrySize;

		var manifests = new List<ArchiveEntryManifest>(entryCount);
		var entries = new List<ArchiveEntryPayloads>(entryCount);

		for (var index = 0; index < entryCount; index++)
		{
			var entryOffset = HeaderSize + (index * entrySize);
			if (entryOffset + entrySize > data.Length)
			{
				break;
			}

			var rawNameBytes = data.AsSpan(entryOffset, 32).ToArray();
			var dataOffset = ReadUInt32(data, entryOffset + 32);
			var mobDataOffset = typeCode == 0x2712
				? ReadUInt32(data, entryOffset + 36)
				: (uint?)null;

			manifests.Add(new ArchiveEntryManifest
			{
				Index = index,
				DecodedName = DecodeName(rawNameBytes),
				RawNameHex = Convert.ToHexString(rawNameBytes),
				DataOffset = dataOffset,
				MobDataOffset = mobDataOffset
			});
		}

		if (typeCode == 0x2712)
		{
			PopulateMobEntries(data, manifests, entries);
		}
		else
		{
			PopulateSpbEntries(data, manifests, entries);
		}

		var manifest = new ArchiveManifest
		{
			FileName = Path.GetFileName(path),
			FullPath = path,
			TypeCode = typeCode,
			HeaderMarker = headerMarker,
			EntryCount = (ushort)entries.Count,
			HeaderSize = HeaderSize,
			EntrySize = entrySize,
			HeaderPaletteOffset = headerPaletteBytes is not null ? HeaderPaletteOffset : null,
			HeaderPaletteEntryCount = headerPaletteBytes is not null ? HeaderPaletteEntryCount : null,
			HeaderPaletteFormat = headerPaletteBytes is not null ? HeaderPaletteFormat : null,
			Entries = entries.Select(entry => entry.Manifest).ToList()
		};

		return new ArchiveInspection(
			manifest,
			entries,
			data.AsSpan(0, HeaderSize).ToArray(),
			headerPalette,
			headerPaletteBytes);
	}

	private static byte[]? TryReadHeaderPaletteBytes(byte[] data)
	{
		var requiredLength = HeaderPaletteOffset + (HeaderPaletteEntryCount * 4);
		if (data.Length < requiredLength)
		{
			return null;
		}

		return data.AsSpan(HeaderPaletteOffset, HeaderPaletteEntryCount * 4).ToArray();
	}

	private static void PopulateSpbEntries(byte[] data, List<ArchiveEntryManifest> manifests, List<ArchiveEntryPayloads> entries)
	{
		var validOffsets = manifests
			.Select(manifest => manifest.DataOffset)
			.Where(offset => offset > 0 && offset < data.Length)
			.OrderBy(offset => offset)
			.ToList();

		foreach (var entryManifest in manifests)
		{
			var subresources = new List<ArchiveSubresourcePayload>();

			if (entryManifest.DataOffset > 0 && entryManifest.DataOffset + 6 <= data.Length)
			{
				var blockOffset = checked((int)entryManifest.DataOffset);
				var blockSize = ReadUInt32(data, blockOffset);
				var subresourceCount = ReadUInt16(data, blockOffset + 4);
				var actualBlockEnd = FindNextOffset(validOffsets, entryManifest.DataOffset, (uint)data.Length);
				var actualBlockSpan = actualBlockEnd - entryManifest.DataOffset;

				entryManifest.OuterSize = blockSize;
				entryManifest.SubresourceCount = subresourceCount;
				entryManifest.ExpectedBlockSpan = blockSize + 4;
				entryManifest.ActualBlockSpan = actualBlockSpan;

				var subOffsets = new List<uint>(subresourceCount);
				var minimumSubresourceOffset = (uint)(6 + (subresourceCount * 4));

				for (var subIndex = 0; subIndex < subresourceCount; subIndex++)
				{
					var subTableOffset = blockOffset + 6 + (subIndex * 4);
					if (subTableOffset + 4 > data.Length)
					{
						break;
					}

					subOffsets.Add(ReadUInt32(data, subTableOffset));
				}

				for (var subIndex = 0; subIndex < subOffsets.Count; subIndex++)
				{
					var subOffset = subOffsets[subIndex];

					if (subOffset < minimumSubresourceOffset || subOffset >= actualBlockSpan)
					{
						continue;
					}

					var subHeaderOffset64 = (ulong)entryManifest.DataOffset + subOffset;
					if (subHeaderOffset64 + 4 > (ulong)data.Length)
					{
						continue;
					}

					var subHeaderOffset = (int)subHeaderOffset64;

					if (subHeaderOffset + 4 > data.Length)
					{
						continue;
					}

					var payloadSize = ReadUInt32(data, subHeaderOffset);
					var payloadOffset = subHeaderOffset + 4;
					var nextRelativeOffset = subOffsets
						.Where(offset => offset > subOffset)
						.DefaultIfEmpty(actualBlockSpan)
						.Min();
					var actualPayloadSpan = nextRelativeOffset > subOffset
						? nextRelativeOffset - subOffset - 4
						: 0;
					var boundedPayloadSize = Math.Min(payloadSize, actualPayloadSpan);
					var availablePayload = Math.Max(0, Math.Min((int)boundedPayloadSize, data.Length - payloadOffset));
					var payload = data.AsSpan(payloadOffset, availablePayload).ToArray();
					var candidateWidth = TryReadReasonableUInt32(payload, 0);
					var candidateHeight = TryReadReasonableUInt32(payload, 4);
					var candidateDataOffset = TryReadPayloadOffset(payload, 8);

					var subresourceManifest = new ArchiveSubresourceManifest
					{
						Index = subIndex,
						RelativeOffset = subOffset,
						PayloadSize = payloadSize,
						ActualPayloadSpan = actualPayloadSpan,
						CandidateWidth = candidateWidth,
						CandidateHeight = candidateHeight,
						CandidateDataOffset = candidateDataOffset,
						CandidateRowTableMatch = candidateWidth.HasValue
							&& candidateHeight.HasValue
							&& candidateDataOffset.HasValue
							&& candidateDataOffset.Value == 12 + (candidateHeight.Value * 4)
					};

					entryManifest.Subresources.Add(subresourceManifest);
					subresources.Add(new ArchiveSubresourcePayload(subresourceManifest, payload));
				}
			}

			entries.Add(new ArchiveEntryPayloads(entryManifest, subresources, new(), new(), null, null));
		}
	}

	private static void PopulateMobEntries(byte[] data, List<ArchiveEntryManifest> manifests, List<ArchiveEntryPayloads> entries)
	{
		foreach (var entryManifest in manifests)
		{
			var metadataRecords = new List<MobMetadataRecordPayload>();
			var dataItems = new List<MobDataItemPayload>();
			byte[]? metadataRegion = null;
			byte[]? dataRegion = null;

			if (entryManifest.DataOffset > 0 && entryManifest.DataOffset + 6 <= data.Length)
			{
				var metadataOffset = checked((int)entryManifest.DataOffset);
				var outerSize = ReadUInt32(data, metadataOffset);
				var recordCount = ReadUInt16(data, metadataOffset + 4);
				var actualSpan = ComputeBoundedSpan(data, entryManifest.DataOffset, outerSize + 4);

				entryManifest.OuterSize = outerSize;
				entryManifest.SubresourceCount = recordCount;
				entryManifest.ExpectedBlockSpan = outerSize + 4;
				entryManifest.ActualBlockSpan = actualSpan;
				metadataRegion = data.AsSpan(metadataOffset, (int)actualSpan).ToArray();

				if ((ulong)6 + ((ulong)recordCount * MobMetadataRecordSize) <= actualSpan)
				{
					var offsets = new List<uint>(recordCount);

					for (var recordIndex = 0; recordIndex < recordCount; recordIndex++)
					{
						var recordOffset = metadataOffset + 6 + (recordIndex * MobMetadataRecordSize);
						offsets.Add(ReadUInt32(data, recordOffset + 32));
					}

					for (var recordIndex = 0; recordIndex < recordCount; recordIndex++)
					{
						var recordOffset = metadataOffset + 6 + (recordIndex * MobMetadataRecordSize);
						var rawNameBytes = data.AsSpan(recordOffset, 32).ToArray();
						var relativeOffset = offsets[recordIndex];
						var minimumOffset = (uint)(6 + (recordCount * MobMetadataRecordSize));

						if (relativeOffset < minimumOffset || relativeOffset >= actualSpan)
						{
							continue;
						}

						var nextRelativeOffset = offsets
							.Where(offset => offset > relativeOffset)
							.DefaultIfEmpty(actualSpan)
							.Min();
						var actualRecordSpan = nextRelativeOffset > relativeOffset
							? nextRelativeOffset - relativeOffset
							: 0;
						var recordPayloadOffset = metadataOffset + (int)relativeOffset;
						var payload = data.AsSpan(recordPayloadOffset, (int)actualRecordSpan).ToArray();
						var metadataManifest = new MobMetadataRecordManifest
						{
							Index = recordIndex,
							DecodedName = DecodeName(rawNameBytes),
							RawNameHex = Convert.ToHexString(rawNameBytes),
							RelativeOffset = relativeOffset,
							ActualSpan = actualRecordSpan,
							WordPreview16 = BuildU16Preview(payload)
						};

						if (TryParseMobMetadataElements(payload, out var elementCount, out var unknownWord, out var elements))
						{
							metadataManifest.ElementCount = elementCount;
							metadataManifest.UnknownWord = unknownWord;
							metadataManifest.Elements.AddRange(elements);
						}

						entryManifest.MobMetadataRecords.Add(metadataManifest);
						metadataRecords.Add(new MobMetadataRecordPayload(metadataManifest, payload));
					}
				}
			}

			if (entryManifest.MobDataOffset is { } mobDataOffset && mobDataOffset > 0 && mobDataOffset + 6 <= data.Length)
			{
				var dataOffset = checked((int)mobDataOffset);
				var outerSize = ReadUInt32(data, dataOffset);
				var itemCount = ReadUInt16(data, dataOffset + 4);
				var actualSpan = ComputeBoundedSpan(data, mobDataOffset, outerSize + 4);

				entryManifest.MobDataOuterSize = outerSize;
				entryManifest.MobDataItemCount = itemCount;
				entryManifest.MobDataExpectedSpan = outerSize + 4;
				entryManifest.MobDataActualSpan = actualSpan;
				dataRegion = data.AsSpan(dataOffset, (int)actualSpan).ToArray();

				if ((ulong)6 + ((ulong)itemCount * 4) <= actualSpan)
				{
					var offsets = new List<uint>(itemCount);

					for (var itemIndex = 0; itemIndex < itemCount; itemIndex++)
					{
						offsets.Add(ReadUInt32(data, dataOffset + 6 + (itemIndex * 4)));
					}

					for (var itemIndex = 0; itemIndex < itemCount; itemIndex++)
					{
						var relativeOffset = offsets[itemIndex];
						var minimumOffset = (uint)(6 + (itemCount * 4));

						if (relativeOffset < minimumOffset || relativeOffset >= actualSpan)
						{
							continue;
						}

						var itemHeaderOffset = dataOffset + (int)relativeOffset;
						if (itemHeaderOffset + 4 > data.Length)
						{
							continue;
						}

						var payloadSize = ReadUInt32(data, itemHeaderOffset);
						var nextRelativeOffset = offsets
							.Where(offset => offset > relativeOffset)
							.DefaultIfEmpty(actualSpan)
							.Min();
						var actualPayloadSpan = nextRelativeOffset > relativeOffset
							? nextRelativeOffset - relativeOffset - 4
							: 0;
						var payloadOffset = itemHeaderOffset + 4;
						var boundedPayloadSize = Math.Min(payloadSize, actualPayloadSpan);
						var availablePayload = Math.Max(0, Math.Min((int)boundedPayloadSize, data.Length - payloadOffset));
						var payload = data.AsSpan(payloadOffset, availablePayload).ToArray();
						var dataManifest = new MobDataItemManifest
						{
							Index = itemIndex,
							RelativeOffset = relativeOffset,
							PayloadSize = payloadSize,
							ActualPayloadSpan = actualPayloadSpan,
							WordPreview16 = BuildU16Preview(payload),
							IsLikelyLiveWorldSprite = itemIndex == LiveWorldSpriteDataItemIndex
						};

						if (MobSpriteDecoder.TryInspectLayout(payload, out var layout))
						{
							dataManifest.CandidateWidth = layout.Width;
							dataManifest.CandidateHeight = layout.Height;
							dataManifest.CandidateTailLength = layout.TailLength;
							dataManifest.CandidateRowCount = layout.RowCount;
							dataManifest.CandidateDataStart = layout.DataStart;
							dataManifest.CandidateSpriteLayout = true;
						}

						entryManifest.MobDataItems.Add(dataManifest);
						dataItems.Add(new MobDataItemPayload(dataManifest, payload));
					}
				}
			}

			entries.Add(new ArchiveEntryPayloads(entryManifest, new(), metadataRecords, dataItems, metadataRegion, dataRegion));
		}
	}

	private static uint ComputeBoundedSpan(byte[] data, uint offset, uint expectedSpan)
	{
		if (offset >= data.Length)
		{
			return 0;
		}

		var available = (uint)(data.Length - offset);
		return Math.Min(expectedSpan, available);
	}

	private static string BuildU16Preview(byte[] payload, int maxWords = 12)
	{
		if (payload.Length < 2)
		{
			return string.Empty;
		}

		var wordCount = Math.Min(maxWords, payload.Length / 2);
		var words = new List<string>(wordCount);

		for (var index = 0; index < wordCount; index++)
		{
			words.Add(ReadUInt16(payload, index * 2).ToString());
		}

		return string.Join(", ", words);
	}

	private static bool TryParseMobMetadataElements(
		byte[] payload,
		out ushort elementCount,
		out ushort unknownWord,
		out List<MobMetadataElementManifest> elements)
	{
		elementCount = 0;
		unknownWord = 0;
		elements = new List<MobMetadataElementManifest>();

		if (payload.Length < 4)
		{
			return false;
		}

		elementCount = ReadUInt16(payload, 0);
		unknownWord = ReadUInt16(payload, 2);
		var expectedLength = 4 + (elementCount * MobMetadataElementSize);

		if (payload.Length < expectedLength)
		{
			return false;
		}

		elements = new List<MobMetadataElementManifest>(elementCount);

		for (var elementIndex = 0; elementIndex < elementCount; elementIndex++)
		{
			var elementOffset = 4 + (elementIndex * MobMetadataElementSize);
			var words = new ushort[MobMetadataElementSize / 2];

			for (var wordIndex = 0; wordIndex < words.Length; wordIndex++)
			{
				words[wordIndex] = ReadUInt16(payload, elementOffset + (wordIndex * 2));
			}

			elements.Add(new MobMetadataElementManifest
			{
				Index = elementIndex,
				DataItemIndex = words[3],
				CandidatePlacementX = unchecked((short)words[1]),
				CandidatePlacementY = unchecked((short)words[2]),
				Words16 = words.ToList(),
				WordPreview16 = string.Join(", ", words.Select(static value => value.ToString()))
			});
		}

		return true;
	}

	public static List<MobPlacedFrame> ResolveMobPlacedFrames(
		IReadOnlyList<MobResolvedFrame> resolvedFrames)
	{
		var firstFrame = resolvedFrames[0];
		var firstX = firstFrame.Element.CandidatePlacementX ?? 0;
		var firstY = firstFrame.Element.CandidatePlacementY ?? 0;

		var placedFrames = new List<MobPlacedFrame>(resolvedFrames.Count);

		foreach (var frame in resolvedFrames)
		{
			var placementX = (frame.Element.CandidatePlacementX ?? 0) - firstX;
			var placementY = (frame.Element.CandidatePlacementY ?? 0) - firstY;

			placedFrames.Add(new MobPlacedFrame(frame.Element, frame.Image, placementX, placementY));
		}

		return placedFrames;
	}

	public static List<MobPlacedFrame> ResolveMobExplicitPlacedFrames(
		IReadOnlyList<MobResolvedFrame> resolvedFrames)
	{
		var firstFrame = resolvedFrames[0];
		var firstX = firstFrame.Element.CandidatePlacementX ?? 0;
		var firstY = firstFrame.Element.CandidatePlacementY ?? 0;
		var placedFrames = new List<MobPlacedFrame>(resolvedFrames.Count);

		foreach (var frame in resolvedFrames)
		{
			var placementX = (frame.Element.CandidatePlacementX ?? 0) - firstX;
			var placementY = (frame.Element.CandidatePlacementY ?? 0) - firstY - frame.Image.Height;
			placedFrames.Add(new MobPlacedFrame(frame.Element, frame.Image, placementX, placementY));
		}

		return placedFrames;
	}

	private static string DecodeName(byte[] rawNameBytes)
	{
		var zeroIndex = Array.IndexOf(rawNameBytes, (byte)0);
		var count = zeroIndex >= 0 ? zeroIndex : rawNameBytes.Length;
		if (count == 0)
		{
			return string.Empty;
		}

		var decoded = KoreanEncoding.GetString(rawNameBytes, 0, count).Trim();
		return decoded;
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

	private static ushort ReadUInt16(byte[] data, int offset)
	{
		return BitConverter.ToUInt16(data, offset);
	}

	private static uint ReadUInt32(byte[] data, int offset)
	{
		return BitConverter.ToUInt32(data, offset);
	}

	private static uint? TryReadReasonableUInt32(byte[] payload, int offset)
	{
		if (payload.Length < offset + 4)
		{
			return null;
		}

		var value = BitConverter.ToUInt32(payload, offset);
		if (value == 0 || value > 4096)
		{
			return null;
		}

		return value;
	}

	private static uint? TryReadPayloadOffset(byte[] payload, int offset)
	{
		if (payload.Length < offset + 4)
		{
			return null;
		}

		var value = BitConverter.ToUInt32(payload, offset);
		if (value == 0 || value > payload.Length)
		{
			return null;
		}

		return value;
	}
}
