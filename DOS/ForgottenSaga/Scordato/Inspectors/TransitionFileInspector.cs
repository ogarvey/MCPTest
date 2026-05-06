using System.Text;
using System.Text.RegularExpressions;

static class TransitionFileInspector
{
	private const int CountHeaderOffset = 0;
	private const int LzStreamOffset = 4;
	private const int HeaderSize = 8;
	private const int ScpRootCountOffset = 0;
	private const int ScpRootTableOffset = 2;
	private const int ScpRootRecordSize = 0x24;
	private const int ScpBlobOffsetFieldOffset = 0x20;
	private const int ScpBlobOffsetBias = 4;
	private const int BinFirstSectionCountOffset = 0;
	private const int BinFirstSectionOffset = 2;
	private const int BinFirstSectionEntrySize = 0x80;
	private const int BinSecondSectionEntrySize = 0x44;
	private const int BinPublishedSelectorEntrySize = 0x24;
	private const int FirstKeyBlobSectionBaseOffset = 0x20;
	private const int SecondKeyBlobSectionBaseOffset = 0x10E;
	private const int SceneRecordNameLength = 0x20;
	private const int TopLevelDescriptorNameLength = 0x20;
	private const int TopLevelDescriptorField20Offset = 0x20;
	private const int TopLevelDescriptorField24Offset = 0x24;
	private const int SceneRecordSelectorOffset = 0x24;
	private const int SceneRecordFlagsOffset = 0x28;
	private const int SceneRecordPositionXOffset = 0x2E;
	private const int SceneRecordPositionYOffset = 0x30;
	private const int SceneRecordField32Offset = 0x32;
	private const int SceneRecordField34Offset = 0x34;
	private const int SceneRecordField36Offset = 0x36;
	private const int SceneRecordField38Offset = 0x38;
	private const int SceneRecordField3AOffset = 0x3A;
	private const int SceneRecordField3COffset = 0x3C;
	private const int SecondKeyFixedSceneBlockOffset = 0xC2;
	private const int SecondKeyFixedSceneNameLength = 0x20;
	private const int SecondKeySecondarySceneNameOffset = 0x20;
	private const int SecondKeyPrelude44SectionIndex = 0;
	private const int SecondKeyInteraction4CSectionIndex = 1;
	private const int SecondKeySceneRecordSectionIndex = 2;
	private const int SecondKeyAnchorSectionIndex = 3;
	private static readonly int[] FirstKeyBlobSectionEntrySizes = { 0x44, 0x24, 0x30, 0x08, 0x04 };
	private static readonly int[] SecondKeyBlobSectionEntrySizes = { 0x44, 0x4C, 0x44, 0x38 };
	private static readonly Encoding KoreanEncoding = Encoding.GetEncoding(949);

	public static TransitionFileInspection Inspect(
		string path,
		bool retainDecompressedPayload,
		ArchiveInspection? objectMobInspection = null)
	{
		var data = File.ReadAllBytes(path);
		if (data.Length < HeaderSize)
		{
			throw new InvalidDataException($"{path} is smaller than the transition-file decode header.");
		}

		var allocationDecodedSize = ReadUInt32(data, CountHeaderOffset);
		var lzDecodedSize = ReadUInt32(data, LzStreamOffset);
		if (!FamInspector.TryDecompressLz(data, LzStreamOffset, out var decoded, out var bytesConsumed, out var error))
		{
			throw new InvalidDataException($"Failed to decompress {Path.GetFileName(path)}: {error}");
		}

		var fileName = Path.GetFileName(path);
		if (fileName.Equals("SAGA.SCP", StringComparison.OrdinalIgnoreCase))
		{
			return InspectSagaScp(path, data, decoded, allocationDecodedSize, lzDecodedSize, bytesConsumed, retainDecompressedPayload);
		}

		if (fileName.Equals("SAGA.BIN", StringComparison.OrdinalIgnoreCase))
		{
			return InspectSagaBin(
				path,
				data,
				decoded,
				allocationDecodedSize,
				lzDecodedSize,
				bytesConsumed,
				retainDecompressedPayload,
				objectMobInspection);
		}

		throw new InvalidDataException($"Unsupported transition file: {path}");
	}

	private static TransitionFileInspection InspectSagaScp(
		string path,
		byte[] encoded,
		byte[] decoded,
		uint allocationDecodedSize,
		uint lzDecodedSize,
		int bytesConsumed,
		bool retainDecompressedPayload)
	{
		var recordCount = ReadUInt16(decoded, ScpRootCountOffset);
		var tableLength = checked(recordCount * ScpRootRecordSize);
		var minimumBlobDataOffset = checked(ScpRootTableOffset + tableLength);
		var rootSection = BuildSection(
			decoded,
			"root_0x24_records",
			ScpRootTableOffset,
			recordCount,
			ScpRootRecordSize,
			blobOffsetFieldOffset: ScpBlobOffsetFieldOffset,
			blobOffsetBias: ScpBlobOffsetBias,
			minimumBlobDataOffset: minimumBlobDataOffset);

		var sections = new List<TransitionSectionInspection> { rootSection };
		var manifest = new TransitionFileManifest
		{
			FileName = Path.GetFileName(path),
			FullPath = path,
			Variant = "SAGA.SCP",
			CountHeaderOffset = CountHeaderOffset,
			AllocationDecodedSize = allocationDecodedSize,
			LzStreamOffset = LzStreamOffset,
			LzDecodedSize = lzDecodedSize,
			LzBytesConsumed = bytesConsumed,
			DecompressedLength = decoded.Length,
			RootRecordCount = recordCount,
			RootRecordOffset = ScpRootTableOffset,
			RootRecordSize = ScpRootRecordSize,
			Sections = sections.Select(section => section.Manifest).ToList()
		};

		return new TransitionFileInspection(
			manifest,
			sections,
			encoded.AsSpan(0, HeaderSize).ToArray(),
			retainDecompressedPayload ? decoded : null,
			null);
	}

	private static TransitionFileInspection InspectSagaBin(
		string path,
		byte[] encoded,
		byte[] decoded,
		uint allocationDecodedSize,
		uint lzDecodedSize,
		int bytesConsumed,
		bool retainDecompressedPayload,
		ArchiveInspection? objectMobInspection)
	{
		var firstSectionCount = ReadUInt16(decoded, BinFirstSectionCountOffset);
		var firstSectionLength = checked(firstSectionCount * BinFirstSectionEntrySize);
		var secondSectionCountOffset = checked(BinFirstSectionOffset + firstSectionLength);
		EnsureAvailable(decoded, secondSectionCountOffset, 2, path, "SAGA.BIN second-section count");

		var secondSectionCount = ReadUInt16(decoded, secondSectionCountOffset);
		var secondSectionOffset = secondSectionCountOffset + 2;
		var secondSectionLength = checked(secondSectionCount * BinSecondSectionEntrySize);
		var publishedSelectorCountOffset = checked(secondSectionOffset + secondSectionLength);
		EnsureAvailable(decoded, publishedSelectorCountOffset, 2, path, "SAGA.BIN published selector count");

		var publishedSelectorCount = ReadUInt16(decoded, publishedSelectorCountOffset);
		var publishedSelectorOffset = publishedSelectorCountOffset + 2;
		var publishedSelectorTableLength = checked(publishedSelectorCount * BinPublishedSelectorEntrySize);
		EnsureAvailable(decoded, publishedSelectorOffset, publishedSelectorTableLength, path, "SAGA.BIN published selector table");

		var firstSection = BuildSection(
			decoded,
			"first_0x80_records",
			BinFirstSectionOffset,
			firstSectionCount,
			BinFirstSectionEntrySize);
		var secondSection = BuildSection(
			decoded,
			"second_0x44_records",
			secondSectionOffset,
			secondSectionCount,
			BinSecondSectionEntrySize);
		var publishedSelectorSection = BuildSection(
			decoded,
			"published_0x24_records",
			publishedSelectorOffset,
			publishedSelectorCount,
			BinPublishedSelectorEntrySize,
			blobOffsetFieldOffset: ScpBlobOffsetFieldOffset,
			blobOffsetBias: 0,
			blobPointerBaseOffset: publishedSelectorOffset,
			minimumBlobDataOffset: checked(publishedSelectorOffset + publishedSelectorTableLength));
		AnalyzePublishedSelectorEntries(
			decoded,
			publishedSelectorSection,
			secondSection,
			objectMobInspection is null ? null : BuildObjectMobEntries(objectMobInspection));
		var sections = new List<TransitionSectionInspection> { firstSection, secondSection, publishedSelectorSection };
		var publishedSelectorRegion = decoded.AsSpan(publishedSelectorOffset).ToArray();

		var manifest = new TransitionFileManifest
		{
			FileName = Path.GetFileName(path),
			FullPath = path,
			Variant = "SAGA.BIN",
			CountHeaderOffset = CountHeaderOffset,
			AllocationDecodedSize = allocationDecodedSize,
			LzStreamOffset = LzStreamOffset,
			LzDecodedSize = lzDecodedSize,
			LzBytesConsumed = bytesConsumed,
			DecompressedLength = decoded.Length,
			FirstSectionCount = firstSectionCount,
			FirstSectionOffset = BinFirstSectionOffset,
			FirstSectionEntrySize = BinFirstSectionEntrySize,
			SecondSectionCount = secondSectionCount,
			SecondSectionOffset = secondSectionOffset,
			SecondSectionEntrySize = BinSecondSectionEntrySize,
			PublishedSelectorCount = publishedSelectorCount,
			PublishedSelectorOffset = publishedSelectorOffset,
			PublishedSelectorRegionLength = publishedSelectorRegion.Length,
			PublishedSelectorRegionFirstBytesHex = BuildHexPreview(publishedSelectorRegion, 24),
			Sections = sections.Select(section => section.Manifest).ToList()
		};

		return new TransitionFileInspection(
			manifest,
			sections,
			encoded.AsSpan(0, HeaderSize).ToArray(),
			retainDecompressedPayload ? decoded : null,
			publishedSelectorRegion);
	}

	private static TransitionSectionInspection BuildSection(
		byte[] decoded,
		string sectionName,
		int tableOffset,
		int entryCount,
		int entrySize,
		int? blobOffsetFieldOffset = null,
		int blobOffsetBias = 0,
		int blobPointerBaseOffset = 0,
		int? minimumBlobDataOffset = null)
	{
		var tableLength = checked(entryCount * entrySize);
		EnsureAvailable(decoded, tableOffset, tableLength, sectionName, "table bytes");

		var manifests = new List<TransitionSectionEntryManifest>(entryCount);
		var entries = new List<TransitionSectionEntryPayload>(entryCount);

		for (var index = 0; index < entryCount; index++)
		{
			var entryOffset = checked(tableOffset + (index * entrySize));
			var payload = decoded.AsSpan(entryOffset, entrySize).ToArray();

			uint? blobFieldValue = null;
			int? blobDataOffsetCandidate = null;
			if (blobOffsetFieldOffset is { } fieldOffset && fieldOffset + 4 <= payload.Length)
			{
				var candidate = ReadUInt32(payload, fieldOffset);
				blobFieldValue = candidate;
				var candidateOffset = (long)blobPointerBaseOffset + candidate + blobOffsetBias;
				if (candidateOffset >= 0
					&& candidateOffset < decoded.Length
					&& (!minimumBlobDataOffset.HasValue || candidateOffset >= minimumBlobDataOffset.Value))
				{
					blobDataOffsetCandidate = checked((int)candidateOffset);
				}
			}

			var manifest = new TransitionSectionEntryManifest
			{
				SectionName = sectionName,
				Index = index,
				EntryOffset = entryOffset,
				FirstBytesHex = BuildHexPreview(payload, 24),
				TextPreview = BuildTextPreview(payload),
				BlobOffsetField = blobFieldValue,
				BlobDataOffsetCandidate = blobDataOffsetCandidate
			};

			manifests.Add(manifest);
			entries.Add(new TransitionSectionEntryPayload(manifest, payload));
		}

		return new TransitionSectionInspection(
			new TransitionSectionManifest
			{
				SectionName = sectionName,
				TableOffset = tableOffset,
				EntryCount = entryCount,
				EntrySize = entrySize,
				Entries = manifests
			},
			entries,
			decoded.AsSpan(tableOffset, tableLength).ToArray());
	}

	private static void AnalyzePublishedSelectorEntries(
		byte[] decoded,
		TransitionSectionInspection publishedSelectorSection,
		TransitionSectionInspection secondSection,
		IReadOnlyDictionary<int, ObjectMobArchiveEntry>? objectMobEntries)
	{
		var visibleTopLevelDescriptors = BuildVisibleTopLevelDescriptors(secondSection);
		foreach (var entry in publishedSelectorSection.Entries)
		{
			if (entry.Manifest.BlobDataOffsetCandidate is not int blobOffset)
			{
				continue;
			}

			entry.Manifest.PublishedSelectorAnalysis = AnalyzePublishedSelectorEntry(
				decoded,
				blobOffset,
				visibleTopLevelDescriptors,
				secondSection.Manifest.EntryCount,
				objectMobEntries);
		}
	}

	private static TransitionPublishedSelectorAnalysisManifest AnalyzePublishedSelectorEntry(
		byte[] decoded,
		int blobOffset,
		IReadOnlyDictionary<int, VisibleTopLevelDescriptor> visibleTopLevelDescriptors,
		int visibleTopLevelDescriptorCount,
		IReadOnlyDictionary<int, ObjectMobArchiveEntry>? objectMobEntries)
	{
		var firstKeySections = ParseCountedSections(
			decoded,
			checked(blobOffset + FirstKeyBlobSectionBaseOffset),
			FirstKeyBlobSectionEntrySizes,
			$"published selector blob 0x{blobOffset:X}");
		var aliasSection = firstKeySections[^1];
		var aliasValues = new List<int>(aliasSection.EntryCount);
		for (var index = 0; index < aliasSection.EntryCount; index++)
		{
			aliasValues.Add(checked((int)ReadUInt32(decoded, checked(aliasSection.DataOffset + (index * aliasSection.EntrySize)))));
		}

		var aliasesBeyondVisibleTransitionDescriptors = aliasValues
			.Where(value => value < 0 || value >= visibleTopLevelDescriptorCount)
			.Distinct()
			.OrderBy(value => value)
			.ToList();

		var aliasesOutsideObjectMobEntryTable = new List<int>();
		var distinctObjectMobEntries = new List<ObjectMobArchiveEntryManifest>();
		if (objectMobEntries is not null)
		{
			foreach (var aliasValue in aliasValues.Distinct().OrderBy(value => value))
			{
				if (objectMobEntries.TryGetValue(aliasValue, out var objectMobEntry))
				{
					distinctObjectMobEntries.Add(BuildObjectMobEntryManifest(objectMobEntry));
				}
				else
				{
					aliasesOutsideObjectMobEntryTable.Add(aliasValue);
				}
			}
		}

		var subsceneCountOffset = aliasSection.EndOffset;
		EnsureAvailable(decoded, subsceneCountOffset, 2, "published selector blob", "subscene count");
		var subsceneCount = ReadUInt16(decoded, subsceneCountOffset);
		var subsceneTableOffset = subsceneCountOffset + 2;
		EnsureAvailable(
			decoded,
			subsceneTableOffset,
			checked(subsceneCount * BinPublishedSelectorEntrySize),
			"published selector blob",
			"subscene table");

		var subscenes = new List<TransitionPublishedSubsceneManifest>(subsceneCount);
		for (var index = 0; index < subsceneCount; index++)
		{
			var entryOffset = checked(subsceneTableOffset + (index * BinPublishedSelectorEntrySize));
			var subsceneName = DecodeName(decoded, entryOffset, SceneRecordNameLength);
			var subsceneBlobOffset = checked(subsceneTableOffset + (int)ReadUInt32(decoded, entryOffset + ScpBlobOffsetFieldOffset));

			try
			{
				subscenes.Add(AnalyzePublishedSubscene(
					decoded,
					index,
					subsceneName,
					subsceneBlobOffset,
					aliasValues,
					visibleTopLevelDescriptors,
					objectMobEntries));
			}
			catch (Exception ex)
			{
				subscenes.Add(new TransitionPublishedSubsceneManifest
					{
						Index = index,
						Name = subsceneName,
						BlobOffset = subsceneBlobOffset,
						ParseError = ex.Message,
						SceneRecords = new List<TransitionSceneRecordAnalysisManifest>()
					});
			}
		}

		return new TransitionPublishedSelectorAnalysisManifest
		{
			BlobOffset = blobOffset,
			AliasCount = aliasValues.Count,
			VisibleTopLevelDescriptorCount = visibleTopLevelDescriptorCount,
			AliasValues = aliasValues,
			AliasesBeyondVisibleTransitionDescriptors = aliasesBeyondVisibleTransitionDescriptors,
			ObjectMobEntryCount = objectMobEntries?.Count,
			AliasesOutsideObjectMobEntryTable = aliasesOutsideObjectMobEntryTable,
			DistinctObjectMobEntries = distinctObjectMobEntries,
			SubsceneCount = subsceneCount,
			Subscenes = subscenes
		};
	}

	private static TransitionPublishedSubsceneManifest AnalyzePublishedSubscene(
		byte[] decoded,
		int subsceneIndex,
		string subsceneName,
		int blobOffset,
		IReadOnlyList<int> aliasValues,
		IReadOnlyDictionary<int, VisibleTopLevelDescriptor> visibleTopLevelDescriptors,
		IReadOnlyDictionary<int, ObjectMobArchiveEntry>? objectMobEntries)
	{
		var secondKeySections = ParseCountedSections(
			decoded,
			checked(blobOffset + SecondKeyBlobSectionBaseOffset),
			SecondKeyBlobSectionEntrySizes,
			$"subscene blob 0x{blobOffset:X}");
		var (fixedSceneBlockOffset, primaryForgaSceneName, secondaryForgaSceneName) =
			ReadFixedSceneNames(decoded, blobOffset);
		var sceneRecordSection = secondKeySections[SecondKeySceneRecordSectionIndex];
		var sceneRecords = new List<TransitionSceneRecordAnalysisManifest>(sceneRecordSection.EntryCount);

		for (var index = 0; index < sceneRecordSection.EntryCount; index++)
		{
			var recordOffset = checked(sceneRecordSection.DataOffset + (index * sceneRecordSection.EntrySize));
			EnsureAvailable(decoded, recordOffset, sceneRecordSection.EntrySize, "subscene blob", "scene record");
			var selector = checked((int)ReadUInt32(decoded, recordOffset + SceneRecordSelectorOffset));
			int? aliasValue = selector >= 0 && selector < aliasValues.Count
				? aliasValues[selector]
				: null;
			VisibleTopLevelDescriptor? visibleDescriptor = null;
			if (aliasValue is int descriptorIndex)
			{
				visibleTopLevelDescriptors.TryGetValue(descriptorIndex, out visibleDescriptor);
			}

			ObjectMobArchiveEntryManifest? objectMobEntry = null;
			if (aliasValue is int objectMobEntryIndex
				&& objectMobEntries is not null
				&& objectMobEntries.TryGetValue(objectMobEntryIndex, out var resolvedObjectMobEntry))
			{
				objectMobEntry = BuildObjectMobEntryManifest(resolvedObjectMobEntry);
			}

			sceneRecords.Add(new TransitionSceneRecordAnalysisManifest
			{
				Index = index,
				RecordOffset = recordOffset,
				Name = DecodeName(decoded, recordOffset, SceneRecordNameLength),
				Selector = selector,
				AliasValue = aliasValue,
				VisibleTopLevelDescriptorIndex = visibleDescriptor?.Index,
				VisibleTopLevelDescriptorName = visibleDescriptor?.Name,
				VisibleTopLevelDescriptorField20 = visibleDescriptor?.Field20,
				VisibleTopLevelDescriptorField24 = visibleDescriptor?.Field24,
				ObjectMobEntry = objectMobEntry,
				Flags28 = decoded[recordOffset + SceneRecordFlagsOffset],
				PositionX = ReadUInt16(decoded, recordOffset + SceneRecordPositionXOffset),
				PositionY = ReadUInt16(decoded, recordOffset + SceneRecordPositionYOffset),
				Field32 = ReadUInt16(decoded, recordOffset + SceneRecordField32Offset),
				Field34 = ReadUInt16(decoded, recordOffset + SceneRecordField34Offset),
				Field36 = ReadUInt16(decoded, recordOffset + SceneRecordField36Offset),
				Field38 = ReadUInt16(decoded, recordOffset + SceneRecordField38Offset),
				Field3A = ReadUInt16(decoded, recordOffset + SceneRecordField3AOffset),
				Field3C = ReadUInt32(decoded, recordOffset + SceneRecordField3COffset)
			});
		}

		return new TransitionPublishedSubsceneManifest
		{
			Index = subsceneIndex,
			Name = subsceneName,
			BlobOffset = blobOffset,
			FixedSceneBlockOffset = fixedSceneBlockOffset,
			PrimaryForgaSceneName = primaryForgaSceneName,
			SecondaryForgaSceneName = secondaryForgaSceneName,
			Prelude44Count = secondKeySections[SecondKeyPrelude44SectionIndex].EntryCount,
			Interaction4CCount = secondKeySections[SecondKeyInteraction4CSectionIndex].EntryCount,
			SceneRecordCount = sceneRecordSection.EntryCount,
			AnchorCount = secondKeySections[SecondKeyAnchorSectionIndex].EntryCount,
			SceneRecords = sceneRecords
		};
	}

	private static Dictionary<int, VisibleTopLevelDescriptor> BuildVisibleTopLevelDescriptors(TransitionSectionInspection secondSection)
	{
		var descriptors = new Dictionary<int, VisibleTopLevelDescriptor>(secondSection.Entries.Count);
		foreach (var entry in secondSection.Entries)
		{
			var payload = entry.Payload;
			if (payload.Length < BinSecondSectionEntrySize)
			{
				continue;
			}

			descriptors[entry.Manifest.Index] = new VisibleTopLevelDescriptor(
				entry.Manifest.Index,
				DecodeName(payload, 0, TopLevelDescriptorNameLength),
				ReadUInt32(payload, TopLevelDescriptorField20Offset),
				ReadUInt32(payload, TopLevelDescriptorField24Offset));
		}

		return descriptors;
	}

	private static Dictionary<int, ObjectMobArchiveEntry> BuildObjectMobEntries(ArchiveInspection objectMobInspection)
	{
		var entries = new Dictionary<int, ObjectMobArchiveEntry>(objectMobInspection.Entries.Count);
		foreach (var entry in objectMobInspection.Entries)
		{
			entries[entry.Manifest.Index] = new ObjectMobArchiveEntry(
				entry.Manifest.Index,
				entry.Manifest.DecodedName,
				entry.Manifest.DataOffset,
				entry.Manifest.MobDataOffset,
				entry.MobMetadataRecords.Count,
				entry.MobDataItems.Count,
				entry.MobDataItems.Any(item => item.Manifest.IsLikelyLiveWorldSprite)
					? ArchiveInspector.LiveWorldSpriteDataItemIndex
					: null);
		}

		return entries;
	}

	private static ObjectMobArchiveEntryManifest BuildObjectMobEntryManifest(ObjectMobArchiveEntry entry)
	{
		return new ObjectMobArchiveEntryManifest
		{
			Index = entry.Index,
			Name = entry.Name,
			MetadataBlockOffset = entry.MetadataBlockOffset,
			DataBlockOffset = entry.DataBlockOffset,
			MetadataRecordCount = entry.MetadataRecordCount,
			DataItemCount = entry.DataItemCount,
			LikelyLiveWorldSpriteDataItemIndex = entry.LikelyLiveWorldSpriteDataItemIndex
		};
	}

	private static List<CountedSectionParse> ParseCountedSections(
		byte[] decoded,
		int startOffset,
		IReadOnlyList<int> entrySizes,
		string description)
	{
		var sections = new List<CountedSectionParse>(entrySizes.Count);
		var cursor = startOffset;
		for (var index = 0; index < entrySizes.Count; index++)
		{
			EnsureAvailable(decoded, cursor, 2, description, $"section {index} count");
			var entryCount = ReadUInt16(decoded, cursor);
			var dataOffset = cursor + 2;
			var byteLength = checked(entryCount * entrySizes[index]);
			EnsureAvailable(decoded, dataOffset, byteLength, description, $"section {index} data");
			sections.Add(new CountedSectionParse(entrySizes[index], entryCount, dataOffset));
			cursor = checked(dataOffset + byteLength);
		}

		return sections;
	}

	private static (int? FixedSceneBlockOffset, string? PrimaryForgaSceneName, string? SecondaryForgaSceneName) ReadFixedSceneNames(
		byte[] decoded,
		int blobOffset)
	{
		var fixedSceneBlockOffset = checked(blobOffset + SecondKeyFixedSceneBlockOffset);
		if (fixedSceneBlockOffset < 0
			|| fixedSceneBlockOffset + (SecondKeyFixedSceneNameLength * 2) > decoded.Length)
		{
			return (null, null, null);
		}

		var primaryForgaSceneName = DecodeName(decoded, fixedSceneBlockOffset, SecondKeyFixedSceneNameLength);
		var secondaryForgaSceneName = DecodeName(
			decoded,
			fixedSceneBlockOffset + SecondKeySecondarySceneNameOffset,
			SecondKeyFixedSceneNameLength);

		return (
			fixedSceneBlockOffset,
			string.IsNullOrWhiteSpace(primaryForgaSceneName) ? null : primaryForgaSceneName,
			string.IsNullOrWhiteSpace(secondaryForgaSceneName) ? null : secondaryForgaSceneName);
	}

	private static string DecodeName(byte[] data, int offset, int maxLength)
	{
		EnsureAvailable(data, offset, maxLength, nameof(TransitionFileInspector), "name decode");
		var zeroIndex = Array.IndexOf(data, (byte)0, offset, maxLength);
		var count = zeroIndex >= 0 ? zeroIndex - offset : maxLength;
		if (count <= 0)
		{
			return string.Empty;
		}

		return KoreanEncoding.GetString(data, offset, count).Trim();
	}

	private static string? BuildTextPreview(byte[] payload)
	{
		var previewLength = Math.Min(payload.Length, 32);
		if (previewLength == 0)
		{
			return null;
		}

		var decoded = KoreanEncoding.GetString(payload, 0, previewLength);
		var builder = new StringBuilder(decoded.Length);
		foreach (var character in decoded)
		{
			if (character == '\0')
			{
				continue;
			}

			builder.Append(char.IsControl(character) ? ' ' : character);
		}

		var normalized = Regex.Replace(builder.ToString(), @"\s+", " ").Trim();
		return string.IsNullOrEmpty(normalized) ? null : normalized;
	}

	private static string BuildHexPreview(byte[] payload, int maxBytes)
	{
		var previewLength = Math.Min(payload.Length, maxBytes);
		return previewLength == 0
			? string.Empty
			: Convert.ToHexString(payload.AsSpan(0, previewLength));
	}

	private static void EnsureAvailable(byte[] data, int offset, int length, string path, string description)
	{
		if (offset < 0 || length < 0 || offset + length > data.Length)
		{
			throw new InvalidDataException($"{path} does not contain a complete {description} at 0x{offset:X} (length 0x{length:X}).");
		}
	}

	private static ushort ReadUInt16(byte[] data, int offset)
	{
		return BitConverter.ToUInt16(data, offset);
	}

	private static uint ReadUInt32(byte[] data, int offset)
	{
		return BitConverter.ToUInt32(data, offset);
	}

	private sealed record CountedSectionParse(int EntrySize, int EntryCount, int DataOffset)
	{
		public int EndOffset => checked(DataOffset + (EntryCount * EntrySize));
	}

	private sealed record VisibleTopLevelDescriptor(int Index, string Name, uint Field20, uint Field24);
 	private sealed record ObjectMobArchiveEntry(
		int Index,
		string Name,
		uint MetadataBlockOffset,
		uint? DataBlockOffset,
		int MetadataRecordCount,
		int DataItemCount,
		int? LikelyLiveWorldSpriteDataItemIndex);
}
