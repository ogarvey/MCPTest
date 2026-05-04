using System.Buffers.Binary;

namespace ZyCleaver;

internal static class RawChunkDecoder
{
  public static DecodedRawChunk Decode(ReadOnlySpan<byte> chunkData)
  {
    if (chunkData.Length < 4)
    {
      throw new InvalidDataException("Raw chunk is shorter than the 4-byte width/height header.");
    }

    var width = BinaryPrimitives.ReadUInt16LittleEndian(chunkData.Slice(0, sizeof(ushort)));
    var height = BinaryPrimitives.ReadUInt16LittleEndian(chunkData.Slice(sizeof(ushort), sizeof(ushort)));

    if (width == 0 || height == 0)
    {
      throw new InvalidDataException($"Invalid raw chunk dimensions {width}x{height}.");
    }

    var pixels = new byte[width * height];
    var writtenMask = new bool[width * height];
    var offset = 4;

    for (var row = 0; row < height; row++)
    {
      var x = 0;
      var rowOffset = row * width;

      while (true)
      {
        EnsureReadable(chunkData, offset, 1, "opcode");
        var opcode = chunkData[offset++];

        if (opcode == 0xff)
        {
          break;
        }

        if (opcode == 0x00)
        {
          EnsureReadable(chunkData, offset, 1, "skip count");
          x += chunkData[offset++];

          if (x > width)
          {
            throw new InvalidDataException($"Row {row} skip extends beyond width {width}.");
          }

          continue;
        }

        if ((opcode & 0x80) == 0)
        {
          var literalCount = opcode;
          EnsureReadable(chunkData, offset, literalCount, "literal packet");
          EnsureWritable(width, row, x, literalCount);
          chunkData.Slice(offset, literalCount).CopyTo(pixels.AsSpan(rowOffset + x, literalCount));

          for (var index = 0; index < literalCount; index++)
          {
            writtenMask[rowOffset + x + index] = true;
          }

          offset += literalCount;
          x += literalCount;
          continue;
        }

        var fillCount = opcode & 0x7f;
        EnsureReadable(chunkData, offset, 4, "fill packet pattern");
        EnsureWritable(width, row, x, fillCount);

        var pattern0 = chunkData[offset + 0];
        var pattern1 = chunkData[offset + 1];
        var pattern2 = chunkData[offset + 2];
        var pattern3 = chunkData[offset + 3];
        offset += 4;

        var remainder = fillCount & 3;

        for (var index = 0; index < remainder; index++)
        {
          pixels[rowOffset + x + index] = pattern0;
          writtenMask[rowOffset + x + index] = true;
        }

        x += remainder;

        var dwordCopies = fillCount >> 2;

        for (var index = 0; index < dwordCopies; index++)
        {
          pixels[rowOffset + x + 0] = pattern0;
          pixels[rowOffset + x + 1] = pattern1;
          pixels[rowOffset + x + 2] = pattern2;
          pixels[rowOffset + x + 3] = pattern3;
          writtenMask[rowOffset + x + 0] = true;
          writtenMask[rowOffset + x + 1] = true;
          writtenMask[rowOffset + x + 2] = true;
          writtenMask[rowOffset + x + 3] = true;
          x += 4;
        }
      }
    }

    return new DecodedRawChunk(width, height, pixels, writtenMask, offset);
  }

  private static void EnsureReadable(ReadOnlySpan<byte> chunkData, int offset, int count, string description)
  {
    if (offset < 0 || count < 0 || offset + count > chunkData.Length)
    {
      throw new InvalidDataException($"Raw chunk ended while reading {description} at offset 0x{offset:X}.");
    }
  }

  private static void EnsureWritable(int width, int row, int x, int count)
  {
    if (x + count > width)
    {
      throw new InvalidDataException($"Row {row} writes past width {width}: x={x}, count={count}.");
    }
  }
}

internal sealed record DecodedRawChunk(ushort Width, ushort Height, byte[] Pixels, bool[] WrittenMask, int BytesConsumed);
