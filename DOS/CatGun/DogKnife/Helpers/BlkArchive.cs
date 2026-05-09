using System.Buffers.Binary;
using System.Text;
using DogKnife.Models;

namespace DogKnife.Helpers;

internal sealed class BlkArchive
{
    private const int HeaderSize = 8;
    private const int EntrySize = 0x30;
    private const int NameLength = 0x28;

    private readonly string archivePath;

    private BlkArchive(string archivePath, int entryCount, int firstDataOffset, IReadOnlyList<BlkEntry> entries)
    {
        this.archivePath = archivePath;
        EntryCount = entryCount;
        FirstDataOffset = firstDataOffset;
        Entries = entries;
    }

    public int EntryCount { get; }

    public int FirstDataOffset { get; }

    public IReadOnlyList<BlkEntry> Entries { get; }

    public static BlkArchive Load(string archivePath)
    {
        using FileStream stream = File.OpenRead(archivePath);

        if (stream.Length < HeaderSize)
        {
            throw new InvalidDataException("BLK file is too small to contain a header.");
        }

        byte[] header = ReadExact(stream, HeaderSize);
        int entryCount = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0, 4));
        int firstDataOffset = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));

        if (entryCount < 0)
        {
            throw new InvalidDataException($"Invalid BLK entry count: {entryCount}");
        }

        long minimumDataOffset = HeaderSize + ((long)entryCount * EntrySize);
        if (firstDataOffset < minimumDataOffset)
        {
            throw new InvalidDataException(
                $"Invalid BLK first data offset 0x{firstDataOffset:X}; expected at least 0x{minimumDataOffset:X}.");
        }

        List<BlkEntry> entries = new(entryCount);

        for (int index = 0; index < entryCount; index++)
        {
            byte[] rawEntry = ReadExact(stream, EntrySize);
            string archiveEntryPath = ReadName(rawEntry.AsSpan(0, NameLength));
            int offset = BinaryPrimitives.ReadInt32LittleEndian(rawEntry.AsSpan(NameLength, 4));
            int size = BinaryPrimitives.ReadInt32LittleEndian(rawEntry.AsSpan(NameLength + 4, 4));

            if (string.IsNullOrWhiteSpace(archiveEntryPath))
            {
                throw new InvalidDataException($"BLK entry {index} has an empty path.");
            }

            if (offset < 0 || size < 0 || (long)offset + size > stream.Length)
            {
                throw new InvalidDataException(
                    $"BLK entry {archiveEntryPath} has invalid bounds: offset=0x{offset:X}, size=0x{size:X}.");
            }

            entries.Add(new BlkEntry(archiveEntryPath, offset, size));
        }

        return new BlkArchive(Path.GetFullPath(archivePath), entryCount, firstDataOffset, entries);
    }

    public void ExtractAll(string outputRoot)
    {
        string fullOutputRoot = Path.GetFullPath(outputRoot);
        Directory.CreateDirectory(fullOutputRoot);

        using FileStream stream = File.OpenRead(archivePath);
        byte[] buffer = new byte[1024 * 1024];

        foreach (var entry in Entries)
        {
            string outputPath = entry.GetOutputPath(fullOutputRoot);
            string? outputDirectory = Path.GetDirectoryName(outputPath);

            if (!string.IsNullOrWhiteSpace(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            stream.Position = entry.Offset;

            using FileStream outputStream = File.Create(outputPath);
            CopyExact(stream, outputStream, entry.Size, buffer);
        }
    }

    private static void CopyExact(Stream input, Stream output, int byteCount, byte[] buffer)
    {
        int remaining = byteCount;

        while (remaining > 0)
        {
            int bytesToRead = Math.Min(buffer.Length, remaining);
            int bytesRead = input.Read(buffer, 0, bytesToRead);

            if (bytesRead == 0)
            {
                throw new EndOfStreamException("Unexpected end of BLK file while extracting an entry.");
            }

            output.Write(buffer, 0, bytesRead);
            remaining -= bytesRead;
        }
    }

    private static byte[] ReadExact(Stream stream, int byteCount)
    {
        byte[] buffer = new byte[byteCount];
        int totalRead = 0;

        while (totalRead < byteCount)
        {
            int bytesRead = stream.Read(buffer, totalRead, byteCount - totalRead);
            if (bytesRead == 0)
            {
                throw new EndOfStreamException("Unexpected end of BLK file while reading the directory.");
            }

            totalRead += bytesRead;
        }

        return buffer;
    }

    private static string ReadName(ReadOnlySpan<byte> rawName)
    {
        int nullIndex = rawName.IndexOf((byte)0);
        if (nullIndex < 0)
        {
            nullIndex = rawName.Length;
        }

        return Encoding.ASCII.GetString(rawName[..nullIndex]);
    }
}
