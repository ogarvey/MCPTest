sealed class ArchiveManifest
{
	public required string FileName { get; init; }
	public required string FullPath { get; init; }
	public required ushort TypeCode { get; init; }
	public required ushort HeaderMarker { get; init; }
	public required ushort EntryCount { get; init; }
	public required int HeaderSize { get; init; }
	public required int EntrySize { get; init; }
	public int? HeaderPaletteOffset { get; init; }
	public int? HeaderPaletteEntryCount { get; init; }
	public string? HeaderPaletteFormat { get; init; }
	public List<TransitionScenePaletteCandidateManifest> TransitionScenePaletteCandidates { get; } = new();
	public List<MenuEventPaletteCandidateManifest> MenuEventPaletteCandidates { get; } = new();
	public required List<ArchiveEntryManifest> Entries { get; init; }
}

sealed class FamManifest
{
	public required string FileName { get; init; }
	public required string FullPath { get; init; }
	public required string Magic { get; init; }
	public required ushort FirstTableCount { get; init; }
	public required int FirstTableOffset { get; init; }
	public required int FirstTableEndOffset { get; init; }
	public required int SecondHeaderOffset { get; init; }
	public required uint SecondHeaderValue { get; init; }
	public required ushort SecondTableCount { get; init; }
	public required int SecondTableOffset { get; init; }
	public required int SecondTableEndOffset { get; init; }
	public required int HeaderSize { get; init; }
	public required int EntrySize { get; init; }
	public required List<FamEntryManifest> Entries { get; init; }
}

sealed class CharDatManifest
{
	public required string FileName { get; init; }
	public required string FullPath { get; init; }
	public required uint RecordCount { get; init; }
	public required int RecordSize { get; init; }
	public required int CountHeaderOffset { get; init; }
	public required int LzStreamOffset { get; init; }
	public required uint LzDecodedSize { get; init; }
	public required uint ExpectedDecodedSize { get; init; }
	public required int LzBytesConsumed { get; init; }
	public required int NameFieldLength { get; init; }
	public required int NodeTemplateOffset { get; init; }
	public required int NodeTemplateLength { get; init; }
	public required int SlotRecordSize { get; init; }
	public required int PrimarySlotGroupOffset { get; init; }
	public required int PrimarySlotGroupCount { get; init; }
	public required int SecondarySlotGroupCount { get; init; }
	public List<int> SecondarySlotGroupOffsets { get; } = new();
	public required List<CharDatRecordManifest> Entries { get; init; }
}

sealed class TransitionFileManifest
{
	public required string FileName { get; init; }
	public required string FullPath { get; init; }
	public required string Variant { get; init; }
	public required int CountHeaderOffset { get; init; }
	public required uint AllocationDecodedSize { get; init; }
	public required int LzStreamOffset { get; init; }
	public required uint LzDecodedSize { get; init; }
	public required int LzBytesConsumed { get; init; }
	public required int DecompressedLength { get; init; }
	public int? RootRecordCount { get; init; }
	public int? RootRecordOffset { get; init; }
	public int? RootRecordSize { get; init; }
	public int? FirstSectionCount { get; init; }
	public int? FirstSectionOffset { get; init; }
	public int? FirstSectionEntrySize { get; init; }
	public int? SecondSectionCount { get; init; }
	public int? SecondSectionOffset { get; init; }
	public int? SecondSectionEntrySize { get; init; }
	public int? PublishedSelectorCount { get; init; }
	public int? PublishedSelectorOffset { get; init; }
	public int? PublishedSelectorRegionLength { get; init; }
	public string? PublishedSelectorRegionFirstBytesHex { get; init; }
	public string? PublishedSelectorRegionOutputFile { get; set; }
	public required List<TransitionSectionManifest> Sections { get; init; }
}

sealed class TransitionSectionManifest
{
	public required string SectionName { get; init; }
	public required int TableOffset { get; init; }
	public required int EntryCount { get; init; }
	public required int EntrySize { get; init; }
	public string? OutputFile { get; set; }
	public required List<TransitionSectionEntryManifest> Entries { get; init; }
}

sealed class TransitionSectionEntryManifest
{
	public required string SectionName { get; init; }
	public required int Index { get; init; }
	public required int EntryOffset { get; init; }
	public required string FirstBytesHex { get; init; }
	public string? TextPreview { get; init; }
	public uint? BlobOffsetField { get; init; }
	public int? BlobDataOffsetCandidate { get; init; }
	public TransitionPublishedSelectorAnalysisManifest? PublishedSelectorAnalysis { get; set; }
	public string? OutputFile { get; set; }
}

sealed class TransitionPublishedSelectorAnalysisManifest
{
	public required int BlobOffset { get; init; }
	public required int AliasCount { get; init; }
	public required int VisibleTopLevelDescriptorCount { get; init; }
	public required List<int> AliasValues { get; init; }
	public required List<int> AliasesBeyondVisibleTransitionDescriptors { get; init; }
	public int? ObjectMobEntryCount { get; init; }
	public required List<int> AliasesOutsideObjectMobEntryTable { get; init; }
	public required List<ObjectMobArchiveEntryManifest> DistinctObjectMobEntries { get; init; }
	public required int SubsceneCount { get; init; }
	public required List<TransitionPublishedSubsceneManifest> Subscenes { get; init; }
}

sealed class TransitionPublishedSubsceneManifest
{
	public required int Index { get; init; }
	public required string Name { get; init; }
	public required int BlobOffset { get; init; }
	public int? FixedSceneBlockOffset { get; init; }
	public string? PrimaryForgaSceneName { get; init; }
	public string? SecondaryForgaSceneName { get; init; }
	public int? Prelude44Count { get; init; }
	public int? Interaction4CCount { get; init; }
	public int? SceneRecordCount { get; init; }
	public int? AnchorCount { get; init; }
	public string? ParseError { get; init; }
	public required List<TransitionSceneRecordAnalysisManifest> SceneRecords { get; init; }
}

sealed class TransitionSceneRecordAnalysisManifest
{
	public required int Index { get; init; }
	public required int RecordOffset { get; init; }
	public required string Name { get; init; }
	public required int Selector { get; init; }
	public int? AliasValue { get; init; }
	public int? VisibleTopLevelDescriptorIndex { get; init; }
	public string? VisibleTopLevelDescriptorName { get; init; }
	public uint? VisibleTopLevelDescriptorField20 { get; init; }
	public uint? VisibleTopLevelDescriptorField24 { get; init; }
	public ObjectMobArchiveEntryManifest? ObjectMobEntry { get; init; }
	public required byte Flags28 { get; init; }
	public required ushort PositionX { get; init; }
	public required ushort PositionY { get; init; }
	public required ushort Field32 { get; init; }
	public required ushort Field34 { get; init; }
	public required ushort Field36 { get; init; }
	public required ushort Field38 { get; init; }
	public required ushort Field3A { get; init; }
	public required uint Field3C { get; init; }
}

sealed class ObjectMobArchiveEntryManifest
{
	public required int Index { get; init; }
	public required string Name { get; init; }
	public required uint MetadataBlockOffset { get; init; }
	public uint? DataBlockOffset { get; init; }
	public required int MetadataRecordCount { get; init; }
	public required int DataItemCount { get; init; }
	public int? LikelyLiveWorldSpriteDataItemIndex { get; init; }
}

sealed class ArchiveEntryManifest
{
	public required int Index { get; init; }
	public required string DecodedName { get; init; }
	public required string RawNameHex { get; init; }
	public required uint DataOffset { get; init; }
	public uint? MobDataOffset { get; init; }
	public uint? OuterSize { get; set; }
	public ushort? SubresourceCount { get; set; }
	public uint? ExpectedBlockSpan { get; set; }
	public uint? ActualBlockSpan { get; set; }
	public uint? MobDataOuterSize { get; set; }
	public ushort? MobDataItemCount { get; set; }
	public uint? MobDataExpectedSpan { get; set; }
	public uint? MobDataActualSpan { get; set; }
	public string? PreviewPaletteSource { get; set; }
	public string? PaletteAuthority { get; set; }
	public int? LiveWorldSpriteDataItemIndex { get; set; }
	public string? LiveWorldBindingModel { get; set; }
	public string? LiveWorldSpriteDecodedPngFile { get; set; }
	public string? LiveWorldSpriteDecodedPaletteSource { get; set; }
	public List<LiveWorldSpritePaletteCandidateManifest> LiveWorldSpritePaletteCandidates { get; } = new();
	public List<ArchiveSubresourceManifest> Subresources { get; } = new();
	public List<MobMetadataRecordManifest> MobMetadataRecords { get; } = new();
	public List<MobDataItemManifest> MobDataItems { get; } = new();
}

sealed class TransitionScenePaletteCandidateManifest
{
	public required string SceneName { get; init; }
	public required string PaletteOutputFile { get; init; }
	public required string PaletteSourceDescription { get; init; }
	public required List<string> MatchedInFiles { get; init; }
}

sealed class MenuEventPaletteCandidateManifest
{
	public required string CandidateName { get; init; }
	public required string PaletteOutputFile { get; init; }
	public required string PaletteSourceDescription { get; init; }
	public required string Evidence { get; init; }
}

sealed class LiveWorldSpritePaletteCandidateManifest
{
	public required string SceneName { get; init; }
	public required List<string> MatchedInFiles { get; init; }
	public required string PaletteOutputFile { get; init; }
	public required string PaletteSourceDescription { get; init; }
	public required string DecodedPngFile { get; init; }
}

sealed class FamEntryManifest
{
	public required string TableName { get; init; }
	public required int Index { get; init; }
	public required string DecodedName { get; init; }
	public required string RawNameHex { get; init; }
	public required uint DataOffset { get; init; }
	public uint DataSpan { get; set; }
	public string? FirstBytesHex { get; set; }
	public string? OutputFile { get; set; }
	public uint? LeadingStoredSize { get; set; }
	public bool? LeadingStoredSizeMatchesPayloadSpan { get; set; }
	public int? LzStreamOffset { get; set; }
	public uint? DecompressedSize { get; set; }
	public int? LzBytesConsumed { get; set; }
	public bool? LzDecodeSucceeded { get; set; }
	public string? LzDecodeError { get; set; }
	public string? DecompressedOutputFile { get; set; }
	public string? EmbeddedName { get; set; }
	public ushort? Field10 { get; set; }
	public ushort? Field12 { get; set; }
	public ushort? Field14 { get; set; }
	public ushort? Field16 { get; set; }
	public string? HeaderTailHex { get; set; }
	public uint? HeaderDword28 { get; set; }
	public uint? CellCountCandidate { get; set; }
	public bool? MatchesFiveBytesPerCell { get; set; }
	public int? RuntimePaletteOffset { get; set; }
	public int? RuntimePaletteLength { get; set; }
	public int? RuntimeLookupTableOffset { get; set; }
	public int? RuntimeLookupTableLength { get; set; }
	public string? RuntimePaletteOutputFile { get; set; }
	public string? RuntimeLookupTableOutputFile { get; set; }
	public string? ProbeMode { get; set; }
	public string? ProbeResourceName { get; set; }
	public int? ProbeResourceIndex { get; set; }
	public string? ProbeOutputDirectory { get; set; }
	public string? ProbePalettePreviewFile { get; set; }
	public string? ProbeLookupTablePreviewFile { get; set; }
	public string? ProbeError { get; set; }
	public List<string> ProbePlaneFiles { get; } = new();
	public List<string> ProbeNotes { get; } = new();
	public List<string> AsciiPreview { get; } = new();
}

sealed class CharDatRecordManifest
{
	public required int Index { get; init; }
	public required int RecordOffset { get; init; }
	public required string DecodedName { get; init; }
	public required string RawNameHex { get; init; }
	public string? OutputFile { get; set; }
}

sealed class MobMetadataRecordManifest
{
	public required int Index { get; init; }
	public required string DecodedName { get; init; }
	public required string RawNameHex { get; init; }
	public required uint RelativeOffset { get; init; }
	public required uint ActualSpan { get; init; }
	public ushort? ElementCount { get; set; }
	public ushort? UnknownWord { get; set; }
	public string? WordPreview16 { get; init; }
	public int? DecodedFrameCount { get; set; }
	public int? DecodedCanvasWidth { get; set; }
	public int? DecodedCanvasHeight { get; set; }
	public string? DecodedPlacementMode { get; set; }
	public string? DecodedPngFile { get; set; }
	public string? DecodedPaletteSource { get; set; }
	public string? DecodedExportError { get; set; }
	public int? AlternateDecodedCanvasWidth { get; set; }
	public int? AlternateDecodedCanvasHeight { get; set; }
	public string? AlternateDecodedPlacementMode { get; set; }
	public string? AlternateDecodedPngFile { get; set; }
	public string? AlternateDecodedExportError { get; set; }
	public List<MobDataItemPaletteCandidateManifest> PaletteCandidates { get; } = new();
	public List<MobMetadataElementManifest> Elements { get; } = new();
}

sealed class MobMetadataElementManifest
{
	public required int Index { get; init; }
	public required ushort DataItemIndex { get; init; }
	public int? CandidatePlacementX { get; init; }
	public int? CandidatePlacementY { get; init; }
	public int? DecodedPlacementX { get; set; }
	public int? DecodedPlacementY { get; set; }
	public int? AlternateDecodedPlacementX { get; set; }
	public int? AlternateDecodedPlacementY { get; set; }
	public required List<ushort> Words16 { get; init; }
	public string? WordPreview16 { get; init; }
	public string? DecodedPngFile { get; set; }
	public string? AlternateDecodedPngFile { get; set; }
}

sealed class MobDataItemManifest
{
	public required int Index { get; init; }
	public required uint RelativeOffset { get; init; }
	public required uint PayloadSize { get; init; }
	public required uint ActualPayloadSpan { get; init; }
	public string? WordPreview16 { get; init; }
	public bool IsLikelyLiveWorldSprite { get; init; }
	public int? CandidateWidth { get; set; }
	public int? CandidateHeight { get; set; }
	public uint? CandidateTailLength { get; set; }
	public uint? CandidateRowCount { get; set; }
	public uint? CandidateDataStart { get; set; }
	public bool CandidateSpriteLayout { get; set; }
	public bool? SpriteDecodeSucceeded { get; set; }
	public string? SpriteDecodeError { get; set; }
	public int? DecodedOpaquePixels { get; set; }
	public int? DecodedTransparentPixels { get; set; }
	public string? DecodedIndexedFile { get; set; }
	public string? DecodedMaskFile { get; set; }
	public string? DecodedPngFile { get; set; }
	public string? DecodedPaletteSource { get; set; }
	public List<MobDataItemPaletteCandidateManifest> PaletteCandidates { get; } = new();
}

sealed class MobDataItemPaletteCandidateManifest
{
	public required string CandidateName { get; init; }
	public required string PaletteOutputFile { get; init; }
	public required string PaletteSourceDescription { get; init; }
	public required string Evidence { get; init; }
	public required string DecodedPngFile { get; init; }
}

sealed class ArchiveSubresourceManifest
{
	public required int Index { get; init; }
	public required uint RelativeOffset { get; init; }
	public required uint PayloadSize { get; init; }
	public required uint ActualPayloadSpan { get; init; }
	public uint? CandidateWidth { get; init; }
	public uint? CandidateHeight { get; init; }
	public uint? CandidateDataOffset { get; init; }
	public bool CandidateRowTableMatch { get; init; }
	public bool? SpbDecodeSucceeded { get; set; }
	public string? SpbDecodeError { get; set; }
	public bool? SpbRowOffsetsAreSelfRelative { get; set; }
	public string? SpbCommandEncoding { get; set; }
	public int? DecodedOpaquePixels { get; set; }
	public int? DecodedTransparentPixels { get; set; }
	public string? DecodedIndexedFile { get; set; }
	public string? DecodedMaskFile { get; set; }
	public string? DecodedPngFile { get; set; }
	public string? DecodedPaletteSource { get; set; }
	public List<MobDataItemPaletteCandidateManifest> PaletteCandidates { get; } = new();
}
