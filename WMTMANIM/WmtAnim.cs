using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ExtractCLUT.Games.PC.Mario.TMD
{
  public static class WmtmAnimHelper
  {
    public class AnxFile
    {
      public short FrameCount { get; set; }
      public byte CompressionType { get; set; }
      public ushort[] ParameterTable { get; set; } // 256 entries for type 0x03
      public AnxFrame[] Frames { get; set; }
    }

    public class AnxFrame
    {
      public byte CompressionType { get; set; }
      public short Width { get; set; }
      public short Height { get; set; }
      public byte[] CompressedData { get; set; }
    }

    public static AnxFile LoadAnxFile(byte[] fileData)
    {
      using var reader = new BinaryReader(new MemoryStream(fileData));

      var anx = new AnxFile();
      anx.FrameCount = reader.ReadInt16();
      if (anx.FrameCount < 0)
        anx.FrameCount = (short)Math.Abs(anx.FrameCount);

      anx.CompressionType = reader.ReadByte();
      byte constantByte = reader.ReadByte(); // Always 0x20

      uint unknown1 = reader.ReadUInt32();
      uint unknown2 = reader.ReadUInt32();

      // Read parameter table (256 ushorts = 0x200 bytes)
      if (anx.CompressionType == 0x03)
      {
        anx.ParameterTable = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
          anx.ParameterTable[i] = reader.ReadUInt16();
        }
      }

      // Read frame offsets and sizes
      uint[] frameOffsets = new uint[anx.FrameCount];
      uint[] frameSizes = new uint[anx.FrameCount];

      for (int i = 0; i < anx.FrameCount; i++)
        frameOffsets[i] = reader.ReadUInt32();

      for (int i = 0; i < anx.FrameCount; i++)
        frameSizes[i] = reader.ReadUInt32();

      // Read frames
      anx.Frames = new AnxFrame[anx.FrameCount];
      for (int i = 0; i < anx.FrameCount; i++)
      {
        reader.BaseStream.Position = frameOffsets[i];
        anx.Frames[i] = LoadAnxFrame(reader, (int)frameSizes[i]);
      }

      return anx;
    }

    public static AnxFrame LoadAnxFrame(BinaryReader reader, int size)
    {
      var frame = new AnxFrame();
      frame.CompressionType = reader.ReadByte();
      byte constantByte = reader.ReadByte(); // 0x20

      reader.BaseStream.Position += 0x0E; // Skip to width/height

      frame.Width = reader.ReadInt16();
      frame.Height = reader.ReadInt16();

      reader.BaseStream.Position += 0x10; // Skip padding

      short width2 = reader.ReadInt16();
      short height2 = reader.ReadInt16();

      // Read compressed data
      long dataStart = reader.BaseStream.Position;
      int dataSize = size - (int)(dataStart - (reader.BaseStream.Position - size));
      frame.CompressedData = reader.ReadBytes(dataSize);

      return frame;
    }

    public static byte[] DecompressAnxFrame(AnxFrame frame, ushort[] paramTable)
    {
      if (frame.CompressionType == 0x01)
      {
        return DecompressAnx(frame.CompressedData);
      }
      else if (frame.CompressionType == 0x03)
      {
        int paddedWidth = (frame.Width + 3) & ~3;
        uint decompressedSize = (uint)(paddedWidth * frame.Height);
        return DecompressAnxType3(frame.CompressedData, decompressedSize, paramTable);
      }

      throw new NotSupportedException($"Compression type {frame.CompressionType:X2} not supported");
    }
    public static byte[] DecompressAnxType3(byte[] compressedData, uint decompressedSize, ushort[] paramTable)
    {
      var output = new List<byte>((int)decompressedSize);

      // Build 1D lookup table (256 * 100 = 25600 bytes = 0x6400)
      var lookupTable = new byte[25600];
      ushort baseValue = paramTable[0];
      byte escapeByte = (byte)(baseValue & 0xFF);
      byte highEscapeByte = (byte)((baseValue >> 8) & 0xFF);

      // Initialize lookup table: lookupTable[lowByte * 100 + highByte] = index
      for (int i = 0; i < 256; i++)
      {
        if (paramTable[i] != baseValue)
        {
          byte lowByte = (byte)(paramTable[i] & 0xFF);
          byte highByte = (byte)((paramTable[i] >> 8) & 0xFF);
          int index = lowByte * 100 + highByte;
          if (index < lookupTable.Length)
          {
            lookupTable[index] = (byte)i;
          }
        }
      }

      int pos = 0;

      while (pos < compressedData.Length && output.Count < decompressedSize)
      {
        byte currentByte = compressedData[pos++];

        // Count consecutive identical bytes in the output stream
        // But wait - we're decompressing, not compressing!
        // The compressed stream doesn't have runs, it has CODES

        // Try to decode this byte as a code
        bool decoded = false;

        // Check all possible (byteValue, runLength) combinations
        for (int byteValue = 0; byteValue < 256 && !decoded; byteValue++)
        {
          for (int runLength = 3; runLength < 100 && !decoded; runLength++)
          {
            int tableIndex = byteValue * 100 + runLength;
            if (tableIndex < lookupTable.Length && lookupTable[tableIndex] == currentByte)
            {
              // Found a match! Output the run
              for (int i = 0; i < runLength; i++)
              {
                output.Add((byte)byteValue);
              }
              decoded = true;
            }
          }
        }

        if (!decoded && pos + 1 < compressedData.Length)
        {
          // Check for 2-byte encoding: code from lookupTable[escapeByte * 100 + runLength], then actual byte
          for (int runLength = 3; runLength < 100 && !decoded; runLength++)
          {
            int tableIndex = escapeByte * 100 + runLength;
            if (tableIndex < lookupTable.Length && lookupTable[tableIndex] == currentByte)
            {
              byte actualByte = compressedData[pos++];
              for (int i = 0; i < runLength; i++)
              {
                output.Add(actualByte);
              }
              decoded = true;
            }
          }
        }

        if (!decoded && pos < compressedData.Length)
        {
          // Check for 2-byte encoding: code from lookupTable[byteValue * 100 + highEscapeByte], then runLength
          for (int byteValue = 0; byteValue < 256 && !decoded; byteValue++)
          {
            int tableIndex = byteValue * 100 + highEscapeByte;
            if (tableIndex < lookupTable.Length && lookupTable[tableIndex] == currentByte)
            {
              byte runLength = compressedData[pos++];
              for (int i = 0; i < runLength; i++)
              {
                output.Add((byte)byteValue);
              }
              decoded = true;
            }
          }
        }

        if (!decoded && currentByte == escapeByte && pos + 1 < compressedData.Length)
        {
          // 3-byte escape: [escapeByte, runLength, byteValue]
          byte runLength = compressedData[pos];
          byte byteValue = compressedData[pos + 1];
          if (runLength >= 3 && runLength < 100)
          {
            pos += 2;
            for (int i = 0; i < runLength; i++)
            {
              output.Add(byteValue);
            }
            decoded = true;
          }
        }

        if (!decoded)
        {
          // Literal byte
          output.Add(currentByte);
        }
      }

      return output.ToArray();
    }
    
    private class DecodedEntry
    {
      public int Type { get; set; }  // 1=single, 2=twoByteValue, 3=twoByteRun
      public byte Value { get; set; }
      public int RunLength { get; set; }
    }

    private static Dictionary<byte, DecodedEntry> BuildDecompressionTable(ushort[] paramTable)
    {
      var table = new Dictionary<byte, DecodedEntry>();
      ushort baseValue = paramTable[0];
      byte escapeByte = (byte)(baseValue & 0xFF);
      byte highEscapeByte = (byte)((baseValue >> 8) & 0xFF);

      for (int i = 0; i < 256; i++)
      {
        if (paramTable[i] != baseValue)
        {
          byte lowByte = (byte)(paramTable[i] & 0xFF);
          byte highByte = (byte)((paramTable[i] >> 8) & 0xFF);
          byte code = (byte)i;

          // Type 1: table[value][runLength] = code
          // Decompression: code -> output 'lowByte' repeated 'highByte' times
          if (highByte >= 3 && highByte < 100)
          {
            table[code] = new DecodedEntry
            {
              Type = 1,
              Value = lowByte,
              RunLength = highByte
            };
          }
          // Type 2: table[escapeByte][runLength] = code
          // Decompression: code, nextByte -> output 'nextByte' repeated 'highByte' times
          else if (lowByte == escapeByte && highByte >= 3 && highByte < 100)
          {
            table[code] = new DecodedEntry
            {
              Type = 2,
              Value = 0,
              RunLength = highByte
            };
          }
          // Type 3: table[value][highEscapeByte] = code
          // Decompression: code, runLength -> output 'lowByte' repeated 'runLength' times
          else if (highByte == highEscapeByte)
          {
            table[code] = new DecodedEntry
            {
              Type = 3,
              Value = lowByte,
              RunLength = 0
            };
          }
        }
      }

      return table;
    }

    private static void InitializeReverseLookupTable(byte[,] table, ushort[] paramTable)
    {
      // Clear the table
      Array.Clear(table, 0, table.Length);

      ushort baseValue = paramTable[0];

      // Build the lookup table from the parameter table
      for (int i = 0; i < 256; i++)
      {
        if (paramTable[i] != baseValue)
        {
          byte lowByte = (byte)(paramTable[i] & 0xFF);
          byte highByte = (byte)((paramTable[i] >> 8) & 0xFF);

          table[lowByte, highByte] = (byte)i;
        }
      }
    }
    public static byte[] DecompressAnx(byte[] data)
    {
      var output = new List<byte>();

      using var dataReader = new BinaryReader(new MemoryStream(data));

      while (dataReader.BaseStream.Position < dataReader.BaseStream.Length)
      {
        var flag = dataReader.ReadByte();
        if (flag == 0x01)
        {
          // Literal run
          var value = dataReader.ReadByte();
          var runLength = dataReader.ReadByte();
          for (int i = 0; i < runLength; i++)
          {
            output.Add(value);
          }
        }
        else
        {
          output.Add(flag);
        }
      }
      return output.ToArray();
    }

    public static void ExtractResFile(string inputFile, string outputDir)
    {
      using var reader = new BinaryReader(File.OpenRead(inputFile));
      reader.BaseStream.Seek(0xA1, SeekOrigin.Begin);
      var fileCount = reader.ReadUInt16();
      var fileEntries = new List<(string Name, uint Offset, uint Size, ushort Flags)>();
      reader.BaseStream.Seek(0xB8, SeekOrigin.Begin);
      for (int i = 0; i < fileCount; i++)
      {
        var offset = reader.ReadUInt32();
        var size = reader.ReadUInt32();
        var nameBytes = reader.ReadBytes(0x11);
        var name = Encoding.ASCII.GetString(nameBytes).TrimEnd('\0');
        reader.ReadBytes(6);
        var flags = reader.ReadUInt16();
        fileEntries.Add((name, offset, size, flags));
      }

      foreach (var entry in fileEntries)
      {
        reader.BaseStream.Seek(entry.Offset, SeekOrigin.Begin);
        var fileData = reader.ReadBytes((int)entry.Size);
        var outputPath = Path.Combine(outputDir, entry.Name);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllBytes(outputPath, fileData);
      }
    }

    public static void ExtractV2LFile(string inputFile, string outputDir)
    {
      using var reader = new BinaryReader(File.OpenRead(inputFile));
      reader.BaseStream.Seek(0x1F, SeekOrigin.Begin);
      var fileCount = reader.ReadUInt16();
      var fileEntries = new List<(string Name, uint Offset, uint Size, ushort Flags)>();
      reader.BaseStream.Seek(0x36, SeekOrigin.Begin);
      for (int i = 0; i < fileCount; i++)
      {
        var offset = reader.ReadUInt32();
        var size = reader.ReadUInt32();
        var nameBytes = reader.ReadBytes(0x11);
        var name = Encoding.ASCII.GetString(nameBytes).TrimEnd('\0');
        var flags = reader.ReadUInt16();
        fileEntries.Add((name, offset, size, flags));
      }

      foreach (var entry in fileEntries)
      {
        reader.BaseStream.Seek(entry.Offset, SeekOrigin.Begin);
        var fileData = reader.ReadBytes((int)entry.Size);
        var outputPath = Path.Combine(outputDir, entry.Name);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllBytes(outputPath, fileData);
      }
    }
  }
}
