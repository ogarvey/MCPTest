sealed record DecodedSpbImage(
	int Width,
	int Height,
	byte[] Indices,
	byte[] AlphaMask,
	int OpaquePixelCount)
{
	public int TransparentPixelCount => (Width * Height) - OpaquePixelCount;
}

sealed record ArchiveInspection(
	ArchiveManifest Manifest,
	List<ArchiveEntryPayloads> Entries,
	byte[] HeaderBytes,
	IndexedPalette? HeaderPalette,
	byte[]? HeaderPaletteBytes);

sealed record FamInspection(
	FamManifest Manifest,
	List<FamEntryPayload> Entries,
	byte[] HeaderBytes,
	byte[] FirstTableBytes,
	byte[] SecondHeaderBytes,
	byte[] SecondTableBytes);

sealed record CharDatInspection(
	CharDatManifest Manifest,
	List<CharDatRecordPayload> Entries,
	byte[] HeaderBytes,
	byte[]? DecompressedPayload);

sealed record TransitionFileInspection(
	TransitionFileManifest Manifest,
	List<TransitionSectionInspection> Sections,
	byte[] HeaderBytes,
	byte[]? DecompressedPayload,
	byte[]? PublishedSelectorRegion);

sealed record TransitionPaletteContext(
	IReadOnlyList<TransitionScenePaletteCandidate> SceneCandidates,
	IReadOnlyDictionary<int, IReadOnlyList<TransitionScenePaletteCandidate>> ObjectMobEntryPaletteCandidates)
{
	public static TransitionPaletteContext Empty { get; } = new(
		Array.Empty<TransitionScenePaletteCandidate>(),
		new Dictionary<int, IReadOnlyList<TransitionScenePaletteCandidate>>());
}

sealed record TransitionSectionInspection(
	TransitionSectionManifest Manifest,
	List<TransitionSectionEntryPayload> Entries,
	byte[] RawBytes);

sealed record TransitionSectionEntryPayload(
	TransitionSectionEntryManifest Manifest,
	byte[] Payload);

sealed record ArchiveEntryPayloads(
	ArchiveEntryManifest Manifest,
	List<ArchiveSubresourcePayload> Subresources,
	List<MobMetadataRecordPayload> MobMetadataRecords,
	List<MobDataItemPayload> MobDataItems,
	byte[]? MobMetadataRegion,
	byte[]? MobDataRegion);

sealed record ArchiveSubresourcePayload(
	ArchiveSubresourceManifest Manifest,
	byte[] Payload);

sealed record MobMetadataRecordPayload(
	MobMetadataRecordManifest Manifest,
	byte[] Payload);

sealed record MobDataItemPayload(
	MobDataItemManifest Manifest,
	byte[] Payload);

sealed record FamEntryPayload(
	FamEntryManifest Manifest,
	byte[] Payload,
	byte[]? DecompressedPayload);

sealed record CharDatRecordPayload(
	CharDatRecordManifest Manifest,
	byte[] Payload);

sealed record TransitionScenePaletteCandidate(
	string SceneName,
	string PalettePath,
	IndexedPalette Palette,
	IReadOnlyList<string> SourceFiles,
	IReadOnlyList<string> Evidence);

sealed record ExternalPaletteCandidate(
	string CandidateName,
	string DirectoryName,
	string PalettePath,
	IndexedPalette Palette,
	string Evidence);

sealed record MenuEventPaletteCandidate(
	string CandidateName,
	string PalettePath,
	IndexedPalette Palette,
	string Evidence);

sealed record MobResolvedFrame(
	MobMetadataElementManifest Element,
	DecodedSpbImage Image);

sealed record MobPlacedFrame(
	MobMetadataElementManifest Element,
	DecodedSpbImage Image,
	int PlacementX,
	int PlacementY);

readonly record struct MobSpriteLayout(
	int Width,
	int Height,
	uint TailLength,
	uint RowCount,
	uint DataStart);

readonly record struct MobFramePlacement(
	int MinX,
	int MinY,
	int CanvasWidth,
	int CanvasHeight)
{
	public static MobFramePlacement FromPlacedFrames(IReadOnlyList<MobPlacedFrame> frames)
	{
		var minX = frames.Min(frame => frame.PlacementX);
		var minY = frames.Min(frame => frame.PlacementY);
		var maxX = frames.Max(frame => frame.PlacementX + frame.Image.Width);
		var maxY = frames.Max(frame => frame.PlacementY + frame.Image.Height);
		return new MobFramePlacement(minX, minY, maxX - minX, maxY - minY);
	}
}
