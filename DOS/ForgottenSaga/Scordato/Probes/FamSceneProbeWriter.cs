static class FamSceneProbeWriter
{
	private const int RuntimeHeightTrim = 0x28;
	private const int AttributePlaneLeadRows = 20;
	private const int TilePlaneLeadRows = 40;
	private const int TileSideLength = 8;
	private const int BytesPerTile = TileSideLength * TileSideLength;
	private const int LookupTableSideLength = 256;

	public static bool TryWriteSceneDebugProbe(
		string outputDir,
		FamEntryPayload firstEntry,
		FamEntryPayload secondEntry,
		out List<string> planeFiles,
		out string? palettePreviewFile,
		out string? lookupTablePreviewFile,
		out List<string> probeNotes,
		out string? error)
	{
		planeFiles = new List<string>();
		palettePreviewFile = null;
		lookupTablePreviewFile = null;
		probeNotes = new List<string>();
		error = null;

		if (firstEntry.DecompressedPayload is null)
		{
			error = $"{firstEntry.Manifest.DecodedName} does not have a retained decompressed primary payload.";
			return false;
		}

		if (secondEntry.DecompressedPayload is null)
		{
			error = $"{secondEntry.Manifest.DecodedName} does not have a retained decompressed secondary payload.";
			return false;
		}

		if (firstEntry.Manifest.Field10 is not { } width || firstEntry.Manifest.Field12 is not { } headerHeight)
		{
			error = $"{firstEntry.Manifest.DecodedName} is missing the first-table dimensions needed for a scene probe.";
			return false;
		}

		var firstPayloadLength64 = (long)width * headerHeight * 5;
		if (firstPayloadLength64 > int.MaxValue)
		{
			error = $"{firstEntry.Manifest.DecodedName} is too large to probe in memory.";
			return false;
		}

		if (firstEntry.DecompressedPayload.Length != firstPayloadLength64)
		{
			error = $"{firstEntry.Manifest.DecodedName} primary payload is {firstEntry.DecompressedPayload.Length} bytes; expected {firstPayloadLength64} from the loader-backed width * height * 5 rule.";
			return false;
		}

		var runtimeHeight = headerHeight - RuntimeHeightTrim;
		if (runtimeHeight <= 0)
		{
			error = $"{firstEntry.Manifest.DecodedName} runtime height becomes {runtimeHeight} after the loader's 0x28-row trim.";
			return false;
		}

		var runtimeCellCount64 = (long)width * runtimeHeight;
		if (runtimeCellCount64 > int.MaxValue)
		{
			error = $"{firstEntry.Manifest.DecodedName} runtime tilemap is too large to probe in memory.";
			return false;
		}

		var runtimeCellCount = (int)runtimeCellCount64;
		var attributeOffset = checked(width * AttributePlaneLeadRows);
		var baseTileOffset = checked((width * headerHeight) + (width * TilePlaneLeadRows));
		var overlayTileOffset = checked((width * headerHeight * 3) + (width * TilePlaneLeadRows));
		var baseTileByteCount = checked(runtimeCellCount * 2);
		var overlayTileByteCount = checked(runtimeCellCount * 2);

		if (attributeOffset + runtimeCellCount > firstEntry.DecompressedPayload.Length
			|| baseTileOffset + baseTileByteCount > firstEntry.DecompressedPayload.Length
			|| overlayTileOffset + overlayTileByteCount > firstEntry.DecompressedPayload.Length)
		{
			error = $"{firstEntry.Manifest.DecodedName} loader-derived scene slices extend beyond the decoded primary payload.";
			return false;
		}

		if (secondEntry.Manifest.RuntimePaletteOffset is not { } paletteOffset
			|| secondEntry.Manifest.RuntimePaletteLength is not { } paletteLength
			|| secondEntry.Manifest.RuntimeLookupTableOffset is not { } lookupTableOffset
			|| secondEntry.Manifest.RuntimeLookupTableLength is not { } lookupTableLength)
		{
			error = $"{secondEntry.Manifest.DecodedName} is missing the second-buffer palette/lookup-table tail offsets required for a scene probe.";
			return false;
		}

		if (paletteLength % 4 != 0)
		{
			error = $"{secondEntry.Manifest.DecodedName} palette tail length {paletteLength} is not a multiple of 4-byte RGBX entries.";
			return false;
		}

		if (lookupTableLength != LookupTableSideLength * LookupTableSideLength)
		{
			error = $"{secondEntry.Manifest.DecodedName} lookup-table tail length {lookupTableLength} does not match the expected {LookupTableSideLength * LookupTableSideLength} bytes.";
			return false;
		}

		var palette = IndexedPalette.FromRawRgbx(
			secondEntry.DecompressedPayload.AsSpan(paletteOffset, paletteLength).ToArray(),
			paletteLength / 4,
			$"FORGA.FAM probe palette from {secondEntry.Manifest.DecodedName}");
		var lookupTableBytes = secondEntry.DecompressedPayload.AsSpan(lookupTableOffset, lookupTableLength).ToArray();
		var tileBankLength = secondEntry.DecompressedPayload.Length;
		if (tileBankLength <= 0 || tileBankLength % BytesPerTile != 0)
		{
			error = $"{secondEntry.Manifest.DecodedName} tile bank length {tileBankLength} is not a clean multiple of {BytesPerTile}-byte 8x8 tiles.";
			return false;
		}

		var attributeValues = firstEntry.DecompressedPayload.AsSpan(attributeOffset, runtimeCellCount).ToArray();
		var baseTileIndices = ReadUInt16Array(firstEntry.DecompressedPayload, baseTileOffset, runtimeCellCount);
		var overlayTileIndices = ReadUInt16Array(firstEntry.DecompressedPayload, overlayTileOffset, runtimeCellCount);
		var tileBankBytes = secondEntry.DecompressedPayload.AsSpan(0, tileBankLength).ToArray();
		var attributeLowNibble = attributeValues.Select(static value => (byte)(value & 0x0F)).ToArray();
		var attributeHighBitMask = attributeValues.Select(static value => (byte)((value & 0x80) != 0 ? 0xFF : 0x00)).ToArray();
		var translationCellCount = attributeValues.Count(static value => (value & 0x80) != 0);

		var attributeFileName = "attribute_low_nibble.png";
		ImageWriter.WriteFamPlaneValueMapPng(
			Path.Combine(outputDir, attributeFileName),
			attributeLowNibble,
			width,
			runtimeHeight);
		planeFiles.Add(attributeFileName);

		var translationMaskFileName = "attribute_high_bit_mask.png";
		ImageWriter.WriteFamPlaneValueMapPng(
			Path.Combine(outputDir, translationMaskFileName),
			attributeHighBitMask,
			width,
			runtimeHeight);
		planeFiles.Add(translationMaskFileName);

		var baseTileFileName = "base_tile_indices.png";
		ImageWriter.WriteFamU16ValueMapPng(
			Path.Combine(outputDir, baseTileFileName),
			baseTileIndices,
			width,
			runtimeHeight);
		planeFiles.Add(baseTileFileName);

		var overlayTileFileName = "overlay_tile_indices.png";
		ImageWriter.WriteFamU16ValueMapPng(
			Path.Combine(outputDir, overlayTileFileName),
			overlayTileIndices,
			width,
			runtimeHeight);
		planeFiles.Add(overlayTileFileName);

		var compositeFileName = translationCellCount == 0
			? "scene_composite.png"
			: "scene_composite_pretranslation.png";
		ImageWriter.WriteFamSceneCompositePng(
			Path.Combine(outputDir, compositeFileName),
			baseTileIndices,
			overlayTileIndices,
			width,
			runtimeHeight,
			tileBankBytes,
			palette);
		planeFiles.Add(compositeFileName);

		palettePreviewFile = "secondary_palette_preview.png";
		ImageWriter.WriteFamPalettePreviewPng(
			Path.Combine(outputDir, palettePreviewFile),
			palette,
			paletteLength / 4);

		lookupTablePreviewFile = "secondary_lookup_table.png";
		ImageWriter.WriteFamLookupTablePng(
			Path.Combine(outputDir, lookupTablePreviewFile),
			lookupTableBytes);

		probeNotes.Add($"Loader-backed first-buffer layout: attribute bytes @ +0x{attributeOffset:X}, base tile indices @ +0x{baseTileOffset:X}, overlay tile indices @ +0x{overlayTileOffset:X}; runtime map size is {width}x{runtimeHeight} after the loader trims 0x28 rows from the header height {headerHeight}.");
		probeNotes.Add($"Runtime draw path uses the full second decoded buffer as the 8x8 tile source ({tileBankLength / BytesPerTile} tiles / {tileBankLength} bytes) while also exposing palette and lookup-table views into its tail.");
		probeNotes.Add($"The scene composite follows the runtime base-tile plus transparent-overlay draw path. Cells with attribute bit 0x80 would need an additional translation step through the engine's color table.");
		probeNotes.Add(translationCellCount == 0
			? "AREA02-style validation: no attribute byte sets bit 0x80, so the emitted scene composite does not need the unresolved translation-table step."
			: $"Translation-table follow-up still required: {translationCellCount} cells set attribute bit 0x80.");
		probeNotes.Add($"Secondary resource offset 0x{paletteOffset:X} contributes {paletteLength / 4} RGBX palette entries.");
		probeNotes.Add($"Secondary resource offset 0x{lookupTableOffset:X} contributes a 256x256 lookup table; runtime tracing shows the engine consumes it as a remap table, not a tile bank.");

		return true;
	}

	private static ushort[] ReadUInt16Array(byte[] data, int offset, int elementCount)
	{
		var values = new ushort[elementCount];

		for (var index = 0; index < elementCount; index++)
		{
			values[index] = BitConverter.ToUInt16(data, offset + (index * 2));
		}

		return values;
	}
}
