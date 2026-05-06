using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

static class ImageWriter
{
	private const long MaxImageSharpBufferLength = 4L * 1024 * 1024 * 1024;
	private const int BytesPerRgba32Pixel = 4;
	private const long MaxStripPixelCount = 134_217_728;
	private const int FamProbeCellScale = 4;
	private const int FamPalettePreviewColumns = 16;
	private const int FamPalettePreviewSwatchSize = 24;
	private const int FamLookupTableSideLength = 256;
	private const int FamSceneTileSideLength = 8;
	private const int FamSceneBytesPerTile = FamSceneTileSideLength * FamSceneTileSideLength;

	public static void WritePng(string path, DecodedSpbImage image, IndexedPalette palette)
	{
		using var output = new Image<Rgba32>(image.Width, image.Height);

		for (var y = 0; y < image.Height; y++)
		{
			var rowOffset = y * image.Width;

			for (var x = 0; x < image.Width; x++)
			{
				var pixelOffset = rowOffset + x;
				if (image.AlphaMask[pixelOffset] == 0)
				{
					output[x, y] = default;
					continue;
				}

				output[x, y] = palette.Colors[image.Indices[pixelOffset]];
			}
		}

		output.Save(path);
	}

	public static void WritePlacedHorizontalStripPng(
		string path,
		IReadOnlyList<MobPlacedFrame> frames,
		MobFramePlacement placement,
		IndexedPalette palette)
	{
		if (frames.Count == 0)
		{
			throw new ArgumentException("At least one frame is required.", nameof(frames));
		}

		const int spacing = 1;
		var width = ((long)placement.CanvasWidth * frames.Count) + ((long)spacing * (frames.Count - 1));
		var height = placement.CanvasHeight;
		ValidateCanvasDimensions(width, height, path, enforceStripLimit: true);

		using var output = new Image<Rgba32>((int)width, height);

		for (var frameIndex = 0; frameIndex < frames.Count; frameIndex++)
		{
			var slotOffsetX = frameIndex * (placement.CanvasWidth + spacing);
			DrawFrame(
				output,
				frames[frameIndex].Image,
				slotOffsetX + (frames[frameIndex].PlacementX - placement.MinX),
				frames[frameIndex].PlacementY - placement.MinY,
				palette);
		}

		output.Save(path);
	}

	public static void WriteFamPlaneValueMapPng(
		string path,
		byte[] planeValues,
		int width,
		int height)
	{
		var outputWidth = (long)width * FamProbeCellScale;
		var outputHeight = (long)height * FamProbeCellScale;
		ValidateCanvasDimensions(outputWidth, outputHeight, path, enforceStripLimit: false);

		using var output = new Image<Rgba32>((int)outputWidth, (int)outputHeight);
		var (minValue, maxValue) = ComputeNonZeroRange(planeValues);

		for (var cellIndex = 0; cellIndex < planeValues.Length; cellIndex++)
		{
			var color = BuildFamPlaneDebugColor(planeValues[cellIndex], minValue, maxValue);
			var cellX = (cellIndex % width) * FamProbeCellScale;
			var cellY = (cellIndex / width) * FamProbeCellScale;
			FillBlock(output, cellX, cellY, FamProbeCellScale, color);
		}

		output.Save(path);
	}

	public static void WriteFamU16ValueMapPng(
		string path,
		ushort[] planeValues,
		int width,
		int height)
	{
		var outputWidth = (long)width * FamProbeCellScale;
		var outputHeight = (long)height * FamProbeCellScale;
		ValidateCanvasDimensions(outputWidth, outputHeight, path, enforceStripLimit: false);

		using var output = new Image<Rgba32>((int)outputWidth, (int)outputHeight);
		var (minValue, maxValue) = ComputeNonZeroRange(planeValues);

		for (var cellIndex = 0; cellIndex < planeValues.Length; cellIndex++)
		{
			var color = BuildFamPlaneDebugColor(planeValues[cellIndex], minValue, maxValue);
			var cellX = (cellIndex % width) * FamProbeCellScale;
			var cellY = (cellIndex / width) * FamProbeCellScale;
			FillBlock(output, cellX, cellY, FamProbeCellScale, color);
		}

		output.Save(path);
	}

	public static void WriteFamSceneCompositePng(
		string path,
		ushort[] baseTileIndices,
		ushort[] overlayTileIndices,
		int width,
		int height,
		byte[] tileBankBytes,
		IndexedPalette palette)
	{
		var outputWidth = (long)width * FamSceneTileSideLength;
		var outputHeight = (long)height * FamSceneTileSideLength;
		ValidateCanvasDimensions(outputWidth, outputHeight, path, enforceStripLimit: false);

		using var output = new Image<Rgba32>((int)outputWidth, (int)outputHeight);
		var tileCount = tileBankBytes.Length / FamSceneBytesPerTile;

		for (var cellIndex = 0; cellIndex < baseTileIndices.Length; cellIndex++)
		{
			var cellX = (cellIndex % width) * FamSceneTileSideLength;
			var cellY = (cellIndex / width) * FamSceneTileSideLength;
			DrawFamTile(output, cellX, cellY, baseTileIndices[cellIndex], tileBankBytes, tileCount, palette, treat0xFFAsTransparent: false);

			if (cellIndex < overlayTileIndices.Length)
			{
				DrawFamTile(output, cellX, cellY, overlayTileIndices[cellIndex], tileBankBytes, tileCount, palette, treat0xFFAsTransparent: true);
			}
		}

		output.Save(path);
	}

	public static void WriteFamPalettePreviewPng(string path, IndexedPalette palette, int entryCount)
	{
		var rows = Math.Max(1, (entryCount + FamPalettePreviewColumns - 1) / FamPalettePreviewColumns);
		var outputWidth = (long)FamPalettePreviewColumns * FamPalettePreviewSwatchSize;
		var outputHeight = (long)rows * FamPalettePreviewSwatchSize;
		ValidateCanvasDimensions(outputWidth, outputHeight, path, enforceStripLimit: false);

		using var output = new Image<Rgba32>((int)outputWidth, (int)outputHeight);

		for (var index = 0; index < entryCount; index++)
		{
			var swatchX = (index % FamPalettePreviewColumns) * FamPalettePreviewSwatchSize;
			var swatchY = (index / FamPalettePreviewColumns) * FamPalettePreviewSwatchSize;
			FillBlock(output, swatchX, swatchY, FamPalettePreviewSwatchSize, palette.Colors[index]);
		}

		output.Save(path);
	}

	public static void WriteFamLookupTablePng(string path, byte[] lookupTableBytes)
	{
		if (lookupTableBytes.Length != FamLookupTableSideLength * FamLookupTableSideLength)
		{
			throw new InvalidDataException($"Lookup table preview requires exactly {FamLookupTableSideLength * FamLookupTableSideLength} bytes.");
		}

		ValidateCanvasDimensions(FamLookupTableSideLength, FamLookupTableSideLength, path, enforceStripLimit: false);
		using var output = new Image<Rgba32>(FamLookupTableSideLength, FamLookupTableSideLength);

		for (var index = 0; index < lookupTableBytes.Length; index++)
		{
			var value = lookupTableBytes[index];
			output[index % FamLookupTableSideLength, index / FamLookupTableSideLength] = new Rgba32(value, value, value, 0xFF);
		}

		output.Save(path);
	}

	private static (byte MinValue, byte MaxValue) ComputeNonZeroRange(byte[] values)
	{
		var foundNonZero = false;
		byte minValue = byte.MaxValue;
		byte maxValue = byte.MinValue;

		foreach (var value in values)
		{
			if (value == 0)
			{
				continue;
			}

			foundNonZero = true;
			if (value < minValue)
			{
				minValue = value;
			}

			if (value > maxValue)
			{
				maxValue = value;
			}
		}

		return foundNonZero ? (minValue, maxValue) : ((byte)0, (byte)0);
	}

	private static (ushort MinValue, ushort MaxValue) ComputeNonZeroRange(ushort[] values)
	{
		var foundNonZero = false;
		ushort minValue = ushort.MaxValue;
		ushort maxValue = ushort.MinValue;

		foreach (var value in values)
		{
			if (value == 0)
			{
				continue;
			}

			foundNonZero = true;
			if (value < minValue)
			{
				minValue = value;
			}

			if (value > maxValue)
			{
				maxValue = value;
			}
		}

		return foundNonZero ? (minValue, maxValue) : ((ushort)0, (ushort)0);
	}

	private static Rgba32 BuildFamPlaneDebugColor(byte value, byte minValue, byte maxValue)
	{
		if (value == 0)
		{
			return new Rgba32(0, 0, 0, 0xFF);
		}

		if (minValue == maxValue)
		{
			return new Rgba32(0xFF, 0xFF, 0xFF, 0xFF);
		}

		var range = Math.Max(1, maxValue - minValue);
		var intensity = 64 + (((value - minValue) * 191) / range);
		var channel = (byte)intensity;
		return new Rgba32(channel, channel, channel, 0xFF);
	}

	private static Rgba32 BuildFamPlaneDebugColor(ushort value, ushort minValue, ushort maxValue)
	{
		if (value == 0)
		{
			return new Rgba32(0, 0, 0, 0xFF);
		}

		if (minValue == maxValue)
		{
			return new Rgba32(0xFF, 0xFF, 0xFF, 0xFF);
		}

		var range = Math.Max(1, maxValue - minValue);
		var intensity = 64 + (((value - minValue) * 191) / range);
		var channel = (byte)intensity;
		return new Rgba32(channel, channel, channel, 0xFF);
	}

	private static void DrawFamTile(
		Image<Rgba32> output,
		int cellX,
		int cellY,
		ushort tileIndex,
		byte[] tileBankBytes,
		int tileCount,
		IndexedPalette palette,
		bool treat0xFFAsTransparent)
	{
		if (tileIndex == 0 || tileIndex > tileCount)
		{
			return;
		}

		var tileOffset = (tileIndex - 1) * FamSceneBytesPerTile;
		for (var y = 0; y < FamSceneTileSideLength; y++)
		{
			for (var x = 0; x < FamSceneTileSideLength; x++)
			{
				var paletteIndex = tileBankBytes[tileOffset + (y * FamSceneTileSideLength) + x];
				if (treat0xFFAsTransparent && paletteIndex == 0xFF)
				{
					continue;
				}

				output[cellX + x, cellY + y] = palette.Colors[paletteIndex];
			}
		}
	}

	private static void FillBlock(Image<Rgba32> output, int startX, int startY, int blockSize, Rgba32 color)
	{
		for (var y = 0; y < blockSize; y++)
		{
			for (var x = 0; x < blockSize; x++)
			{
				output[startX + x, startY + y] = color;
			}
		}
	}

	public static void WritePlacedFramePng(
		string path,
		MobPlacedFrame frame,
		MobFramePlacement placement,
		IndexedPalette palette)
	{
		ValidateCanvasDimensions(placement.CanvasWidth, placement.CanvasHeight, path, enforceStripLimit: false);
		using var output = new Image<Rgba32>(placement.CanvasWidth, placement.CanvasHeight);
		DrawFrame(
			output,
			frame.Image,
			frame.PlacementX - placement.MinX,
			frame.PlacementY - placement.MinY,
			palette);
		output.Save(path);
	}

	private static void DrawFrame(
		Image<Rgba32> output,
		DecodedSpbImage frame,
		int destinationX,
		int destinationY,
		IndexedPalette palette)
	{
		for (var y = 0; y < frame.Height; y++)
		{
			var rowOffset = y * frame.Width;

			for (var x = 0; x < frame.Width; x++)
			{
				var pixelOffset = rowOffset + x;
				if (frame.AlphaMask[pixelOffset] == 0)
				{
					continue;
				}

				output[destinationX + x, destinationY + y] = palette.Colors[frame.Indices[pixelOffset]];
			}
		}
	}

	private static void ValidateCanvasDimensions(long width, long height, string path, bool enforceStripLimit)
	{
		if (width <= 0 || height <= 0 || width > int.MaxValue || height > int.MaxValue)
		{
			throw new MobCanvasTooLargeException($"Canvas {width}x{height} for {Path.GetFileName(path)} is outside the supported size range.");
		}

		var pixelCount = width * height;
		if (pixelCount > MaxImageSharpBufferLength / BytesPerRgba32Pixel)
		{
			throw new MobCanvasTooLargeException($"Canvas {width}x{height} for {Path.GetFileName(path)} exceeds the ImageSharp allocation limit.");
		}

		if (enforceStripLimit && pixelCount > MaxStripPixelCount)
		{
			throw new MobCanvasTooLargeException($"Canvas {width}x{height} for {Path.GetFileName(path)} exceeds the strip export limit; use the per-frame outputs instead.");
		}
	}
}
