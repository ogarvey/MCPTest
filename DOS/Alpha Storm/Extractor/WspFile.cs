using System.Buffers.Binary;

public sealed class WspFile
{
    private const int HeaderEntrySize = 4;

    private WspFile(int entryCount, IReadOnlyList<WspSpriteSet> spriteSets)
    {
        EntryCount = entryCount;
        SpriteSets = spriteSets;
    }

    public int EntryCount { get; }

    public int NonSpriteEntryCount => EntryCount - SpriteSets.Count;

    public IReadOnlyList<WspSpriteSet> SpriteSets { get; }

    public static WspFile Load(string path)
    {
        return Parse(File.ReadAllBytes(path));
    }

    public static WspFile Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderEntrySize)
        {
            throw new InvalidDataException("WSP file is too small to contain an entry table.");
        }

        var firstOffset = BinaryPrimitives.ReadUInt32LittleEndian(data[..4]);
        if (firstOffset == 0 || firstOffset > data.Length || firstOffset % HeaderEntrySize != 0)
        {
            throw new InvalidDataException($"Invalid WSP first entry offset: 0x{firstOffset:X}.");
        }

        var entryCount = checked((int)firstOffset / HeaderEntrySize);
        var spriteSets = new List<WspSpriteSet>();

        for (var index = 0; index < entryCount; index++)
        {
            var start = ReadOffset(data.Slice(index * HeaderEntrySize, HeaderEntrySize), index, firstOffset, data.Length);
            var end = index + 1 < entryCount
                ? ReadOffset(data.Slice((index + 1) * HeaderEntrySize, HeaderEntrySize), index + 1, firstOffset, data.Length)
                : data.Length;

            if (end <= start)
            {
                throw new InvalidDataException($"WSP entry {index} ends before it starts.");
            }

            var entryData = data.Slice(start, end - start);
            if (TryParseSpriteSet(entryData, out var spriteSet))
            {
                spriteSets.Add(new WspSpriteSet(index, start, end - start, spriteSet!));
            }
        }

        return new WspFile(entryCount, spriteSets);
    }

    private static int ReadOffset(ReadOnlySpan<byte> source, int index, uint directoryLength, int dataLength)
    {
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(source);
        if (offset < directoryLength || offset >= dataLength)
        {
            throw new InvalidDataException($"WSP entry {index} offset 0x{offset:X} points outside the file.");
        }

        return (int)offset;
    }

    private static bool TryParseSpriteSet(ReadOnlySpan<byte> data, out LifFile? spriteSet)
    {
        spriteSet = null;
        if (data.Length < 12)
        {
            return false;
        }

        try
        {
            spriteSet = LifFile.Parse(data);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }
}

public sealed record WspSpriteSet(int Index, int EncodedOffset, int EncodedLength, LifFile SpriteSet);
