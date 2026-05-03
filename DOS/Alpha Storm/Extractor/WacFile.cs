using System.Buffers.Binary;

public sealed class WacFile
{
	public const int Width = 640;
	public const int Height = 480;

	private const int HeaderEntrySize = 4;
	private const int LookupTableSize = 256;
	private const int ExpectedDecodedLength = Width * Height;

	private WacFile(int entryCount, IReadOnlyList<WacImage> images, IReadOnlyList<WacSpriteSet> spriteSets)
	{
		EntryCount = entryCount;
		Images = images;
		SpriteSets = spriteSets;
	}

	public int EntryCount { get; }

	public IReadOnlyList<WacImage> Images { get; }

	public IReadOnlyList<WacSpriteSet> SpriteSets { get; }

	public static WacFile Load(string path)
	{
		return Parse(File.ReadAllBytes(path));
	}

	public static WacFile Parse(ReadOnlySpan<byte> data)
	{
		if (data.Length < HeaderEntrySize)
		{
			throw new InvalidDataException("WAC file is too small to contain an entry table.");
		}

		var firstOffset = BinaryPrimitives.ReadUInt32LittleEndian(data[..4]);
		if (firstOffset == 0 || firstOffset > data.Length || firstOffset % HeaderEntrySize != 0)
		{
			throw new InvalidDataException($"Invalid WAC first entry offset: 0x{firstOffset:X}.");
		}

		var entryCount = checked((int)firstOffset / HeaderEntrySize);
		var entryRanges = new (int Start, int End)[entryCount];

		for (var index = 0; index < entryCount; index++)
		{
			var start = ReadOffset(data.Slice(index * HeaderEntrySize, HeaderEntrySize), index, firstOffset, data.Length);
			var end = index + 1 < entryCount
				? ReadOffset(data.Slice((index + 1) * HeaderEntrySize, HeaderEntrySize), index + 1, firstOffset, data.Length)
				: data.Length;

			if (end <= start)
			{
				throw new InvalidDataException($"WAC entry {index} ends before it starts.");
			}

			entryRanges[index] = (start, end);
		}

		var spriteSets = TryParseSpriteSets(entryRanges, data);
		if (spriteSets is not null)
		{
			return new WacFile(entryCount, Array.Empty<WacImage>(), spriteSets);
		}

		var images = new List<WacImage>(entryCount);

		for (var index = 0; index < entryCount; index++)
		{
			var (start, end) = entryRanges[index];
			images.Add(DecodeImage(index, start, end, data));
		}

		return new WacFile(entryCount, images, Array.Empty<WacSpriteSet>());
	}

	private static IReadOnlyList<WacSpriteSet>? TryParseSpriteSets((int Start, int End)[] entryRanges, ReadOnlySpan<byte> data)
	{
		var spriteSets = new List<WacSpriteSet>(entryRanges.Length);

		for (var index = 0; index < entryRanges.Length; index++)
		{
			var (start, end) = entryRanges[index];
			try
			{
				var spriteSet = LifFile.Parse(data.Slice(start, end - start));
				spriteSets.Add(new WacSpriteSet(index, start, end - start, spriteSet));
			}
			catch (Exception exception) when (exception is InvalidDataException or OverflowException or ArgumentOutOfRangeException)
			{
				return null;
			}
		}

		return spriteSets;
	}

	private static WacImage DecodeImage(int index, int start, int end, ReadOnlySpan<byte> data)
	{
		var entry = data.Slice(start, end - start);
		var pixels = new byte[ExpectedDecodedLength];
		var alpha = new byte[pixels.Length];
		Array.Fill(alpha, (byte)255);
		var lookup = new byte[LookupTableSize];
		var pending = new Stack<WacPendingSymbol>();
		var sourceOffset = 0;
		var outputOffset = 0;

		while (true)
		{
			if (entry.Length - sourceOffset < 4)
			{
				throw new InvalidDataException($"WAC entry {index} is truncated at block header 0x{start + sourceOffset:X}.");
			}

			var headerHigh = entry[sourceOffset++];
			var headerLow = entry[sourceOffset++];
			var symbolCount = ((headerHigh & 0x7f) << 8) | headerLow;
			if (symbolCount > 0xff)
			{
				throw new InvalidDataException($"WAC entry {index} block at 0x{start + sourceOffset - 2:X} has an unsupported symbol table size of {symbolCount + 1}.");
			}

			var topLevelCount = BinaryPrimitives.ReadUInt16BigEndian(entry.Slice(sourceOffset, 2)) + 1;
			sourceOffset += 2;

			var symbolTableLength = symbolCount + 1;
			var tableBytes = checked(symbolTableLength * 3);
			var requiredBytes = checked(tableBytes + topLevelCount);
			if (entry.Length - sourceOffset < requiredBytes)
			{
				throw new InvalidDataException($"WAC entry {index} block at 0x{start + sourceOffset - 4:X} extends past the end of the entry.");
			}

			var sourceSymbols = entry.Slice(sourceOffset, symbolTableLength);
			sourceOffset += symbolTableLength;
			var rightSymbols = entry.Slice(sourceOffset, symbolTableLength);
			sourceOffset += symbolTableLength;
			var leftSymbols = entry.Slice(sourceOffset, symbolTableLength);
			sourceOffset += symbolTableLength;

			BuildLookupTable(lookup, sourceSymbols, symbolCount);

			for (var rootIndex = 0; rootIndex < topLevelCount; rootIndex++)
			{
				ExpandSymbol(entry[sourceOffset++], symbolCount, sourceSymbols, rightSymbols, leftSymbols, lookup, pending, pixels, ref outputOffset, index);
			}

			if ((headerHigh & 0x80) == 0)
			{
				break;
			}
		}

		if (outputOffset != pixels.Length)
		{
			throw new InvalidDataException($"WAC entry {index} decodes to {outputOffset} byte(s); expected {ExpectedDecodedLength}.");
		}

		var trailerLength = entry.Length - sourceOffset;
		return new WacImage(
			index,
			Width,
			Height,
			start,
			entry.Length,
			sourceOffset,
			trailerLength,
			pixels,
			alpha,
			pixels.Length,
			new PixelBounds(0, 0, Width - 1, Height - 1));
	}

	private static int ReadOffset(ReadOnlySpan<byte> source, int index, uint directoryLength, int dataLength)
	{
		var offset = BinaryPrimitives.ReadUInt32LittleEndian(source);
		if (offset < directoryLength || offset >= dataLength)
		{
			throw new InvalidDataException($"WAC entry {index} offset 0x{offset:X} points outside the file.");
		}

		return (int)offset;
	}

	private static void BuildLookupTable(byte[] lookup, ReadOnlySpan<byte> sourceSymbols, int symbolCount)
	{
		Array.Clear(lookup);

		for (var symbolIndex = symbolCount; symbolIndex >= 0; symbolIndex--)
		{
			var symbol = sourceSymbols[symbolIndex];
			if (lookup[symbol] == 0 && (byte)symbolIndex < 0x80)
			{
				lookup[symbol] = (byte)(symbolIndex | 0x80);
			}
			else
			{
				lookup[symbol] = 0xff;
			}
		}
	}

	private static void ExpandSymbol(byte rootSymbol, int limit, ReadOnlySpan<byte> sourceSymbols,
		ReadOnlySpan<byte> rightSymbols, ReadOnlySpan<byte> leftSymbols, byte[] lookup, Stack<WacPendingSymbol> pending,
		byte[] output, ref int outputOffset, int imageIndex)
	{
		pending.Push(new WacPendingSymbol(rootSymbol, limit));

		while (pending.Count > 0)
		{
			var state = pending.Pop();
			var symbol = state.Symbol;
			var symbolLimit = state.Limit;

			while (true)
			{
				var lookupEntry = lookup[symbol];
				int symbolIndex;

				if (lookupEntry is >= 0x80 and <= 0xfe)
				{
					symbolIndex = lookupEntry - 0x80;
					if (symbolLimit < symbolIndex)
					{
						WriteOutputByte(output, ref outputOffset, symbol, imageIndex);
						break;
					}
				}
				else if (lookupEntry == 0xff)
				{
					symbolIndex = symbolLimit;
					while (symbolIndex >= 0 && sourceSymbols[symbolIndex] != symbol)
					{
						symbolIndex--;
					}

					if (symbolIndex < 0)
					{
						WriteOutputByte(output, ref outputOffset, symbol, imageIndex);
						break;
					}
				}
				else
				{
					WriteOutputByte(output, ref outputOffset, symbol, imageIndex);
					break;
				}

				var nextLimit = symbolIndex - 1;
				pending.Push(new WacPendingSymbol(rightSymbols[symbolIndex], nextLimit));
				symbol = leftSymbols[symbolIndex];
				symbolLimit = nextLimit;
			}
		}
	}

	private static void WriteOutputByte(byte[] output, ref int outputOffset, byte value, int imageIndex)
	{
		if (outputOffset >= output.Length)
		{
			throw new InvalidDataException($"WAC entry {imageIndex} expands past the expected {ExpectedDecodedLength}-byte VGA frame.");
		}

		output[outputOffset++] = value;
	}

	private readonly record struct WacPendingSymbol(byte Symbol, int Limit);
}

public sealed record WacImage(
	int Index,
	int Width,
	int Height,
	int EncodedOffset,
	int EncodedLength,
	int ConsumedLength,
	int TrailerLength,
	byte[] Pixels,
	byte[] Alpha,
	int OpaquePixelCount,
	PixelBounds Bounds);

public sealed record WacSpriteSet(int Index, int EncodedOffset, int EncodedLength, LifFile SpriteSet);
