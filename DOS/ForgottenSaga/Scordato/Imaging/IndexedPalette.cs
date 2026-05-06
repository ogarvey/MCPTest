using SixLabors.ImageSharp.PixelFormats;

sealed class IndexedPalette
{
	private const int RawVgaPaletteLength = 256 * 3;
	private const int RawRgbxPaletteEntryCountWithTransparentDefault = 255;
	private const int RawRgbxPaletteEntryCountWithOpaqueIndex255 = 256;
	private const ushort SpbTypeCode = 0x2711;
	private const ushort MobTypeCode = 0x2712;
	private const int EmbeddedHeaderPaletteOffset = 0x06;
	private const int EmbeddedHeaderPaletteEntryCount = 255;

	private IndexedPalette(Rgba32[] colors, string sourceDescription)
	{
		Colors = colors;
		SourceDescription = sourceDescription;
	}

	public Rgba32[] Colors { get; }
	public string SourceDescription { get; }

	public static IndexedPalette LoadFile(string path)
	{
		var bytes = File.ReadAllBytes(path);

		if (TryLoadRawVga(bytes, path, out var rawPalette))
		{
			return rawPalette;
		}

		if (TryLoadRawRgbx(bytes, path, out var rawRgbxPalette))
		{
			return rawRgbxPalette;
		}

		if (TryLoadEmbeddedHeaderRgbx(bytes, path, out var headerPalette))
		{
			return headerPalette;
		}

		throw new InvalidDataException(
			$"{path} is {bytes.Length} bytes, expected either a 768-byte raw VGA palette, a 255/256-entry raw RGBX palette, or a 0x2711/0x2712 file with an embedded header RGBX palette.");
	}

	private static bool TryLoadRawVga(byte[] bytes, string path, out IndexedPalette palette)
	{
		if (bytes.Length != RawVgaPaletteLength)
		{
			palette = null!;
			return false;
		}

		var colors = new Rgba32[256];
		for (var index = 0; index < colors.Length; index++)
		{
			var red = bytes[(index * 3) + 0];
			var green = bytes[(index * 3) + 1];
			var blue = bytes[(index * 3) + 2];

			if (red > 63 || green > 63 || blue > 63)
			{
				throw new InvalidDataException($"{path} does not look like a 6-bit raw VGA palette.");
			}

			colors[index] = new Rgba32(ScaleVgaComponent(red), ScaleVgaComponent(green), ScaleVgaComponent(blue), 0xFF);
		}

		palette = new IndexedPalette(colors, $"raw VGA palette from {Path.GetFileName(path)}");
		return true;
	}

	private static bool TryLoadRawRgbx(byte[] bytes, string path, out IndexedPalette palette)
	{
		if (bytes.Length == RawRgbxPaletteEntryCountWithTransparentDefault * 4)
		{
			palette = FromRgbxEntries(
				bytes,
				RawRgbxPaletteEntryCountWithTransparentDefault,
				preserveIndex255: false,
				$"raw RGBX palette from {Path.GetFileName(path)}");
			return true;
		}

		if (bytes.Length == RawRgbxPaletteEntryCountWithOpaqueIndex255 * 4)
		{
			palette = FromRgbxEntries(
				bytes,
				RawRgbxPaletteEntryCountWithOpaqueIndex255,
				preserveIndex255: true,
				$"raw RGBX palette from {Path.GetFileName(path)}");
			return true;
		}

		palette = null!;
		return false;
	}

	private static bool TryLoadEmbeddedHeaderRgbx(byte[] bytes, string path, out IndexedPalette palette)
	{
		var requiredLength = EmbeddedHeaderPaletteOffset + (EmbeddedHeaderPaletteEntryCount * 4);
		if (bytes.Length < requiredLength)
		{
			palette = null!;
			return false;
		}

		var typeCode = BitConverter.ToUInt16(bytes, 0);
		if (typeCode is not (SpbTypeCode or MobTypeCode))
		{
			palette = null!;
			return false;
		}

		palette = FromHeaderRgbx(
			bytes.AsSpan(EmbeddedHeaderPaletteOffset, EmbeddedHeaderPaletteEntryCount * 4).ToArray(),
			EmbeddedHeaderPaletteOffset,
			EmbeddedHeaderPaletteEntryCount,
			$"embedded header RGBX palette from {Path.GetFileName(path)}");
		return true;
	}

	public static IndexedPalette FromRawRgbx(byte[] rgbxBytes, int entryCount, string? sourceDescription = null)
	{
		return FromRgbxEntries(
			rgbxBytes,
			entryCount,
			preserveIndex255: entryCount >= 256,
			sourceDescription ?? $"raw RGBX palette ({entryCount} entries)");
	}

	public static IndexedPalette FromHeaderRgbx(byte[] rgbxBytes, int offset, int entryCount, string? sourceDescription = null)
	{
		return FromRgbxEntries(
			rgbxBytes,
			entryCount,
			preserveIndex255: false,
			sourceDescription ?? $"embedded header RGBX palette @ 0x{offset:X} ({entryCount} entries)");
	}

	private static IndexedPalette FromRgbxEntries(byte[] rgbxBytes, int entryCount, bool preserveIndex255, string sourceDescription)
	{
		if (entryCount < 0 || entryCount > 256)
		{
			throw new ArgumentOutOfRangeException(nameof(entryCount), entryCount, "RGBX palette entry count must be between 0 and 256.");
		}

		if (rgbxBytes.Length < entryCount * 4)
		{
			throw new InvalidDataException($"RGBX palette is {rgbxBytes.Length} bytes, expected at least {entryCount * 4} bytes for {entryCount} entries.");
		}

		var colors = new Rgba32[256];

		for (var index = 0; index < entryCount; index++)
		{
			var sourceOffset = index * 4;
			colors[index] = new Rgba32(
				rgbxBytes[sourceOffset + 0],
				rgbxBytes[sourceOffset + 1],
				rgbxBytes[sourceOffset + 2],
				0xFF);
		}

		if (!preserveIndex255)
		{
			colors[255] = default;
		}

		return new IndexedPalette(colors, sourceDescription);
	}

	private static byte ScaleVgaComponent(byte value)
	{
		return (byte)((value * 255 + 31) / 63);
	}
}
