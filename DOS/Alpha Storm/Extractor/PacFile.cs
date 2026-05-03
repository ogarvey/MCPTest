using System.Buffers.Binary;
using System.Text;

public sealed class PacFile
{
	private const string FormChunkId = "FORM";
	private const string PbmFormType = "PBM ";
	private const string BitmapHeaderChunkId = "BMHD";
	private const string ColorMapChunkId = "CMAP";
	private const string ColorRangeChunkId = "CRNG";
	private const string BodyChunkId = "BODY";
	private const byte CompressionNone = 0;
	private const byte CompressionByteRun1 = 1;

	private PacFile(
		int width,
		int height,
		byte compression,
		int bodyOffset,
		int bodyLength,
		int colorMapLength,
		int colorRangeCount,
		RgbPalette palette,
		byte[] pixels,
		byte[] alpha)
	{
		Width = width;
		Height = height;
		Compression = compression;
		BodyOffset = bodyOffset;
		BodyLength = bodyLength;
		ColorMapLength = colorMapLength;
		ColorRangeCount = colorRangeCount;
		Palette = palette;
		Pixels = pixels;
		Alpha = alpha;
		OpaquePixelCount = pixels.Length;
		Bounds = new PixelBounds(0, 0, width - 1, height - 1);
	}

	public int Width { get; }

	public int Height { get; }

	public byte Compression { get; }

	public int BodyOffset { get; }

	public int BodyLength { get; }

	public int ColorMapLength { get; }

	public int ColorRangeCount { get; }

	public RgbPalette Palette { get; }

	public byte[] Pixels { get; }

	public byte[] Alpha { get; }

	public int OpaquePixelCount { get; }

	public PixelBounds Bounds { get; }

	public static PacFile Load(string path)
	{
		return Parse(File.ReadAllBytes(path), Path.GetFileName(path));
	}

	public static PacFile Parse(ReadOnlySpan<byte> data, string sourceName = "PAC file")
	{
		if (data.Length < 12)
		{
			throw new InvalidDataException($"{sourceName} is too small to contain an IFF header.");
		}

		if (ReadAscii(data[..4]) != FormChunkId)
		{
			throw new InvalidDataException($"{sourceName} is not an IFF FORM.");
		}

		var declaredFormSize = ReadCheckedUInt32BigEndian(data.Slice(4, 4), $"{sourceName} FORM size");
		if (declaredFormSize + 8 > data.Length)
		{
			throw new InvalidDataException($"{sourceName} FORM size extends beyond the file.");
		}

		if (ReadAscii(data.Slice(8, 4)) != PbmFormType)
		{
			throw new InvalidDataException($"{sourceName} FORM type is not PBM.");
		}

		var width = 0;
		var height = 0;
		byte compression = 0;
		RgbPalette? palette = null;
		var colorMapLength = 0;
		var colorRangeCount = 0;
		var bodyOffset = -1;
		ReadOnlySpan<byte> bodyData = default;

		var offset = 12;
		var formEnd = declaredFormSize + 8;
		while (offset + 8 <= formEnd)
		{
			var chunkId = ReadAscii(data.Slice(offset, 4));
			var chunkLength = ReadCheckedUInt32BigEndian(data.Slice(offset + 4, 4), $"{sourceName} chunk size");
			var chunkDataOffset = offset + 8;
			if (chunkDataOffset > data.Length || data.Length - chunkDataOffset < chunkLength)
			{
				throw new InvalidDataException($"{sourceName} chunk {chunkId} extends beyond the file.");
			}

			var chunkData = data.Slice(chunkDataOffset, chunkLength);
			switch (chunkId)
			{
				case BitmapHeaderChunkId:
					(width, height, compression) = ParseBitmapHeader(chunkData, sourceName);
					break;

				case ColorMapChunkId:
					palette = RgbPalette.ParseColorMap(chunkData, sourceName);
					colorMapLength = chunkLength;
					break;

				case ColorRangeChunkId:
					colorRangeCount++;
					break;

				case BodyChunkId:
					bodyOffset = chunkDataOffset;
					bodyData = chunkData;
					break;
			}

			offset = chunkDataOffset + chunkLength;
			if ((chunkLength & 1) != 0)
			{
				offset++;
			}

			if (chunkId == BodyChunkId)
			{
				break;
			}
		}

		if (width <= 0 || height <= 0)
		{
			throw new InvalidDataException($"{sourceName} does not contain a valid BMHD chunk.");
		}

		if (palette is null)
		{
			throw new InvalidDataException($"{sourceName} does not contain a CMAP chunk.");
		}

		if (bodyOffset < 0)
		{
			throw new InvalidDataException($"{sourceName} does not contain a BODY chunk.");
		}

		var pixels = DecodeBody(bodyData, width, height, compression, sourceName);
		var alpha = new byte[pixels.Length];
		Array.Fill(alpha, (byte)255);

		return new PacFile(width, height, compression, bodyOffset, bodyData.Length, colorMapLength, colorRangeCount, palette, pixels, alpha);
	}

	private static (int Width, int Height, byte Compression) ParseBitmapHeader(ReadOnlySpan<byte> data, string sourceName)
	{
		if (data.Length < 20)
		{
			throw new InvalidDataException($"{sourceName} BMHD chunk is truncated.");
		}

		var width = BinaryPrimitives.ReadUInt16BigEndian(data[..2]);
		var height = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(2, 2));
		var planes = data[8];
		var masking = data[9];
		var compression = data[10];

		if (width == 0 || height == 0)
		{
			throw new InvalidDataException($"{sourceName} BMHD chunk has invalid dimensions {width}x{height}.");
		}

		if (planes != 8)
		{
			throw new InvalidDataException($"{sourceName} uses {planes} bitplane(s); only 8-bit PBM images are supported.");
		}

		if (masking != 0)
		{
			throw new InvalidDataException($"{sourceName} uses unsupported masking mode {masking}.");
		}

		if (compression is not (CompressionNone or CompressionByteRun1))
		{
			throw new InvalidDataException($"{sourceName} uses unsupported PBM compression mode {compression}.");
		}

		return (width, height, compression);
	}

	private static byte[] DecodeBody(ReadOnlySpan<byte> bodyData, int width, int height, byte compression, string sourceName)
	{
		var paddedRowWidth = (width + 1) & ~1;
		var decodedLength = checked(paddedRowWidth * height);
		var decoded = compression switch
		{
			CompressionNone => DecodeUncompressedBody(bodyData, decodedLength, sourceName),
			CompressionByteRun1 => DecodeByteRun1Body(bodyData, decodedLength, sourceName),
			_ => throw new InvalidDataException($"{sourceName} uses unsupported PBM compression mode {compression}.")
		};

		if (paddedRowWidth == width)
		{
			return decoded;
		}

		var pixels = new byte[checked(width * height)];
		for (var y = 0; y < height; y++)
		{
			decoded.AsSpan(y * paddedRowWidth, width).CopyTo(pixels.AsSpan(y * width, width));
		}

		return pixels;
	}

	private static byte[] DecodeUncompressedBody(ReadOnlySpan<byte> bodyData, int decodedLength, string sourceName)
	{
		if (bodyData.Length != decodedLength)
		{
			throw new InvalidDataException($"{sourceName} BODY chunk contains {bodyData.Length} byte(s); expected {decodedLength} for an uncompressed PBM image.");
		}

		return bodyData.ToArray();
	}

	private static byte[] DecodeByteRun1Body(ReadOnlySpan<byte> bodyData, int decodedLength, string sourceName)
	{
		var decoded = new byte[decodedLength];
		var sourceOffset = 0;
		var targetOffset = 0;

		while (targetOffset < decoded.Length)
		{
			if (sourceOffset >= bodyData.Length)
			{
				throw new InvalidDataException($"{sourceName} BODY chunk ended after decoding {targetOffset} byte(s); expected {decodedLength}.");
			}

			var control = unchecked((sbyte)bodyData[sourceOffset++]);
			if (control >= 0)
			{
				var literalCount = control + 1;
				if (bodyData.Length - sourceOffset < literalCount)
				{
					throw new InvalidDataException($"{sourceName} BODY chunk has a truncated ByteRun1 literal run.");
				}

				if (decoded.Length - targetOffset < literalCount)
				{
					throw new InvalidDataException($"{sourceName} BODY chunk expands beyond the expected pixel buffer.");
				}

				bodyData.Slice(sourceOffset, literalCount).CopyTo(decoded.AsSpan(targetOffset, literalCount));
				sourceOffset += literalCount;
				targetOffset += literalCount;
				continue;
			}

			if (control == -128)
			{
				continue;
			}

			if (sourceOffset >= bodyData.Length)
			{
				throw new InvalidDataException($"{sourceName} BODY chunk has a truncated ByteRun1 repeat run.");
			}

			var repeatCount = 1 - control;
			if (decoded.Length - targetOffset < repeatCount)
			{
				throw new InvalidDataException($"{sourceName} BODY chunk expands beyond the expected pixel buffer.");
			}

			decoded.AsSpan(targetOffset, repeatCount).Fill(bodyData[sourceOffset++]);
			targetOffset += repeatCount;
		}

		if (sourceOffset != bodyData.Length)
		{
			throw new InvalidDataException($"{sourceName} BODY chunk has {bodyData.Length - sourceOffset} trailing compressed byte(s) after decoding the image.");
		}

		return decoded;
	}

	private static int ReadCheckedUInt32BigEndian(ReadOnlySpan<byte> source, string fieldName)
	{
		var value = BinaryPrimitives.ReadUInt32BigEndian(source);
		if (value > int.MaxValue)
		{
			throw new InvalidDataException($"{fieldName} is too large.");
		}

		return (int)value;
	}

	private static string ReadAscii(ReadOnlySpan<byte> source)
	{
		return Encoding.ASCII.GetString(source);
	}
}
