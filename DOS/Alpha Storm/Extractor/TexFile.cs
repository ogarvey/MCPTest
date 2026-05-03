public sealed class TexFile
{
	public const int TextureWidth = 128;
	public const int TextureHeight = 128;
	public const int TextureByteLength = TextureWidth * TextureHeight;

	private TexFile(IReadOnlyList<TexTexture> textures)
	{
		Textures = textures;
	}

	public IReadOnlyList<TexTexture> Textures { get; }

	public static TexFile Load(string path)
	{
		return Parse(File.ReadAllBytes(path));
	}

	public static TexFile Parse(ReadOnlySpan<byte> data)
	{
		if (data.Length == 0 || data.Length % TextureByteLength != 0)
		{
			throw new InvalidDataException($"TEX file length {data.Length} is not a whole number of {TextureWidth}x{TextureHeight} textures.");
		}

		var textureCount = data.Length / TextureByteLength;
		var textures = new List<TexTexture>(textureCount);

		for (var index = 0; index < textureCount; index++)
		{
			var encodedOffset = index * TextureByteLength;
			var pixels = data.Slice(encodedOffset, TextureByteLength).ToArray();
			var alpha = new byte[pixels.Length];
			Array.Fill(alpha, (byte)255);
			textures.Add(new TexTexture(
				index,
				TextureWidth,
				TextureHeight,
				encodedOffset,
				TextureByteLength,
				pixels,
				alpha,
				pixels.Length,
				new PixelBounds(0, 0, TextureWidth - 1, TextureHeight - 1)));
		}

		return new TexFile(textures);
	}
}

public sealed record TexTexture(
	int Index,
	int Width,
	int Height,
	int EncodedOffset,
	int EncodedLength,
	byte[] Pixels,
	byte[] Alpha,
	int OpaquePixelCount,
	PixelBounds Bounds);
