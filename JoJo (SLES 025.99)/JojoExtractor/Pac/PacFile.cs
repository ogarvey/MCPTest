using System.Buffers.Binary;

namespace JojoExtractor.Pac;

/// <summary>
/// Parser for the .PAC container format used by JoJo's Bizarre Adventure (SLES_025.99).
///
/// Layout (little-endian):
///   0x00  uint32  entry_count
///   0x04  uint32  total_size              (size of the whole PAC file)
///   0x08  entry_count * { uint32 flags; uint32 data_length; }
///
/// Each entry's payload starts at the next 0x800-aligned offset after the previous
/// entry ends. The first entry's payload starts at 0x800.
/// </summary>
public sealed class PacFile
{
    public const int SectorSize = 0x800;

    public uint EntryCount { get; }
    public uint TotalSize { get; }
    public IReadOnlyList<PacEntry> Entries { get; }
    public byte[] RawBytes { get; }

    private PacFile(uint entryCount, uint totalSize, IReadOnlyList<PacEntry> entries, byte[] rawBytes)
    {
        EntryCount = entryCount;
        TotalSize = totalSize;
        Entries = entries;
        RawBytes = rawBytes;
    }

    public static PacFile Load(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        return Parse(bytes);
    }

    public static PacFile Parse(byte[] bytes)
    {
        if (bytes.Length < 8)
            throw new InvalidDataException("File too small to be a PAC.");

        ReadOnlySpan<byte> span = bytes;
        uint entryCount = BinaryPrimitives.ReadUInt32LittleEndian(span[..4]);
        uint totalSize = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(4, 4));

        if (entryCount == 0 || entryCount > 4096)
            throw new InvalidDataException($"Implausible entry count: {entryCount}.");

        int dirEnd = 8 + checked((int)entryCount) * 8;
        if (dirEnd > bytes.Length)
            throw new InvalidDataException("Entry directory extends past end of file.");

        var entries = new List<PacEntry>((int)entryCount);
        long dataOffset = SectorSize;

        for (int i = 0; i < entryCount; i++)
        {
            int header = 8 + i * 8;
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(header, 4));
            uint dataLength = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(header + 4, 4));

            entries.Add(new PacEntry(i, flags, dataLength, dataOffset));

            // Advance to next 0x800 boundary after this payload.
            long end = dataOffset + dataLength;
            long aligned = (end + (SectorSize - 1)) & ~(long)(SectorSize - 1);
            dataOffset = aligned;
        }

        return new PacFile(entryCount, totalSize, entries, bytes);
    }

    /// <summary>
    /// Returns a view over the payload bytes of the given entry.
    /// </summary>
    public ReadOnlySpan<byte> GetEntryData(PacEntry entry)
    {
        long end = entry.DataOffset + entry.DataLength;
        if (end > RawBytes.LongLength)
            throw new InvalidDataException(
                $"Entry {entry.Index} payload extends past end of file " +
                $"(offset=0x{entry.DataOffset:X}, length=0x{entry.DataLength:X}, file=0x{RawBytes.LongLength:X}).");

        return RawBytes.AsSpan((int)entry.DataOffset, (int)entry.DataLength);
    }
}
