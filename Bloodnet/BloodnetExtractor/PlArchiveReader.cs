using System.Buffers.Binary;
using System.Text;

namespace BloodnetExtractor;

internal static class PlArchiveReader
{
    public static PlArchive ReadArchive(ReadOnlySpan<byte> data, string name)
    {
        if (!TryReadArchive(data, name, out var archive, out var failureReason))
        {
            throw new InvalidDataException(failureReason);
        }

        return archive;
    }

    public static bool TryReadArchive(ReadOnlySpan<byte> data, string name, out PlArchive archive, out string failureReason)
    {
        archive = null!;
        failureReason = string.Empty;

        if (data.Length < 6)
        {
            failureReason = "Entry is too small to be a PL archive.";
            return false;
        }

        var entryCount = BinaryPrimitives.ReadUInt16LittleEndian(data[..2]);
        var tableOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(2, 4));

        if (entryCount == 0)
        {
            failureReason = "PL archive entry count is zero.";
            return false;
        }

        var tableLength = entryCount * 12u;
        if (tableOffset < 6 || tableOffset + tableLength > data.Length)
        {
            failureReason = "PL archive table points outside the entry data.";
            return false;
        }

        var entries = new List<PlEntry>(entryCount);
        var offsets = new uint[entryCount];
        var names = new string[entryCount];

        for (var index = 0; index < entryCount; index++)
        {
            var entryOffset = tableOffset + (index * 12u);
            offsets[index] = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice((int)entryOffset, 4));
            names[index] = ReadName(data.Slice((int)entryOffset + 4, 8));
        }

        if (offsets[0] != 6)
        {
            failureReason = "First PL entry does not begin at offset 0x06.";
            return false;
        }

        for (var index = 0; index < entryCount; index++)
        {
            if (offsets[index] >= tableOffset)
            {
                failureReason = $"PL entry {index} points into or beyond the file table.";
                return false;
            }

            if (!LooksLikeArchiveName(names[index]))
            {
                failureReason = $"PL entry {index} has an invalid archive name: {names[index]}";
                return false;
            }

            var nextOffset = index + 1 < entryCount ? offsets[index + 1] : tableOffset;
            if (nextOffset < offsets[index])
            {
                failureReason = $"PL entry {index} is not ordered by offset.";
                return false;
            }

            var size = (int)(nextOffset - offsets[index]);
            var entryData = data.Slice((int)offsets[index], size).ToArray();
            entries.Add(new PlEntry(index, names[index], offsets[index], size, entryData));
        }

        archive = new PlArchive(name, entryCount, tableOffset, entries);
        return true;
    }

    private static string ReadName(ReadOnlySpan<byte> nameBytes)
    {
        var terminatorIndex = nameBytes.IndexOf((byte)0);
        var slice = terminatorIndex >= 0 ? nameBytes[..terminatorIndex] : nameBytes;
        return Encoding.ASCII.GetString(slice);
    }

    private static bool LooksLikeArchiveName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 8)
        {
            return false;
        }

        return value.All(character => character is >= '!' and <= '~' && character is not '/' and not '\\');
    }
}

internal sealed record PlArchive(string Name, int EntryCount, uint TableOffset, IReadOnlyList<PlEntry> Entries);

internal sealed record PlEntry(int Index, string Name, uint Offset, int Size, byte[] Data);
