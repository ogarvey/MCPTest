using System;
using System.Collections.Generic;
using System.IO;

namespace Twins
{
    public class TwinsDacFile
    {
        public List<DacSection1Entry> Section1Entries { get; set; } = new List<DacSection1Entry>();
        public List<DacFrame> Frames { get; set; } = new List<DacFrame>();

        public int LastDetectedOffset { get; private set; }

        /// <summary>
        /// Parses a decompressed DAC file stream.
        /// </summary>
        /// <param name="data">The decompressed byte array of the DAC file.</param>
        public void Parse(byte[] data) => Parse(data, baseFrameIndex: 0, enforceGlobalFrameCap: false);

        /// <summary>
        /// Parses a decompressed DAC stream using the same rules as TWINS.LE (FUN_000187b7).
        /// </summary>
        /// <param name="data">Decompressed bytes for a single DAC resource.</param>
        /// <param name="baseFrameIndex">The current global frame index (DAT_00031c6c) before parsing this DAC.</param>
        /// <param name="enforceGlobalFrameCap">If true, enforces the engine's global 100-frame cap.</param>
        /// <returns>The new global frame index after parsing this DAC.</returns>
        public int Parse(byte[] data, int baseFrameIndex, bool enforceGlobalFrameCap)
        {
            int offset = DetectOffset(data);
            LastDetectedOffset = offset;
            int globalFrameIndex = baseFrameIndex;
            
            using (var stream = new MemoryStream(data, offset, data.Length - offset))
            using (var reader = new BinaryReader(stream))
            {
                byte ReadByteChecked(string field)
                {
                    if (stream.Position + 1 > stream.Length)
                        throw new Exception($"DAC parse overrun reading {field} at 0x{stream.Position:X}");
                    return reader.ReadByte();
                }

                ushort ReadUInt16Checked(string field)
                {
                    if (stream.Position + 2 > stream.Length)
                        throw new Exception($"DAC parse overrun reading {field} at 0x{stream.Position:X}");
                    return reader.ReadUInt16();
                }

                byte[] ReadBytesChecked(int count, string field)
                {
                    if (count < 0)
                        throw new Exception($"DAC parse invalid size {count} for {field}");

                    if (stream.Position + count > stream.Length)
                    {
                        long remaining = stream.Length - stream.Position;
                        throw new Exception($"DAC parse overrun reading {field} ({count} bytes) at 0x{stream.Position:X}; remaining={remaining}");
                    }
                    return reader.ReadBytes(count);
                }

                // --- Section 1: Configuration/Header Data ---
                // Reads a count of entries (max 4).
                ushort section1Count = ReadUInt16Checked("Section1Count");
                if (section1Count > 4)
                    throw new Exception($"Invalid DAC file: Section 1 count {section1Count} > 4");

                for (int i = 0; i < section1Count; i++)
                {
                    // Each entry starts with a byte count (max 20).
                    byte valueCount = ReadByteChecked($"Section1[{i}].ValueCount");
                    if (valueCount > 0x14)
                        throw new Exception($"Invalid DAC file: Section 1 value count {valueCount} > 0x14");

                    var entry = new DacSection1Entry();
                    for (int j = 0; j < valueCount; j++)
                    {
                        // TWINS.LE FUN_000187b7 reads word values here and stores
                        // (globalFrameIndex + value) into its tables.
                        entry.Values.Add((ushort)(baseFrameIndex + ReadUInt16Checked($"Section1[{i}].Values[{j}]")));
                    }
                    Section1Entries.Add(entry);
                }

                // --- Section 2: Frame Data ---
                // Reads the number of frames in this file (word).
                // In TWINS.LE this is then used to loop and read each frame record.
                ushort frameCount = ReadUInt16Checked("FrameCount");

                for (int i = 0; i < frameCount; i++)
                {
                    if (enforceGlobalFrameCap && globalFrameIndex > 99)
                        throw new Exception($"Invalid DAC file: global frame index {globalFrameIndex} exceeds 99");

                    long frameStart = stream.Position;
                    int frameStartAbs = offset + (int)frameStart;

                    // TWINS.LE FUN_000187b7 frame layout:
                    //   word  unk0
                    //   word  x
                    //   word  y
                    //   word  w
                    //   word  h
                    //   byte  unk1 (read and ignored by the engine)
                    //   word  dataSize
                    //   bytes data[dataSize]
                    if (stream.Position + 2 + 2 + 2 + 2 + 2 + 1 + 2 > stream.Length)
                        throw new Exception($"DAC parse overrun reading Frame[{i}] header at 0x{stream.Position:X}");

                    var frame = new DacFrame();
                    frame.Unknown0 = ReadUInt16Checked($"Frame[{i}].Unknown0");
                    frame.X = ReadUInt16Checked($"Frame[{i}].X");
                    frame.Y = ReadUInt16Checked($"Frame[{i}].Y");
                    frame.Width = ReadUInt16Checked($"Frame[{i}].Width");
                    frame.Height = ReadUInt16Checked($"Frame[{i}].Height");
                    frame.Unknown1 = ReadByteChecked($"Frame[{i}].Unknown1");

                    ushort dataSize = ReadUInt16Checked($"Frame[{i}].DataSize");

                    long dataStart = stream.Position;
                    long remaining = stream.Length - dataStart;
                    if (dataSize > remaining)
                    {
                        int dumpLen = Math.Min(32, (int)(stream.Length - frameStart));
                        string dump = dumpLen > 0
                            ? BitConverter.ToString(data, frameStartAbs, dumpLen)
                            : string.Empty;
                        throw new Exception(
                            $"DAC parse overrun reading Frame[{i}].Data: dataSize={dataSize}, remaining={remaining}, " +
                            $"frameStart=0x{frameStartAbs:X}, dataStart=0x{(offset + (int)dataStart):X}, " +
                            $"headerBytes[{dumpLen}]=[{dump}]"
                        );
                    }

                    frame.Data = ReadBytesChecked(dataSize, $"Frame[{i}].Data");

                    Frames.Add(frame);
                    globalFrameIndex++;
                }
            }

            return globalFrameIndex;
        }

        private int DetectOffset(byte[] data)
        {
            // 1. Try offset 0
            if (IsValidHeader(data, 0)) return 0;

            // 2. Scan for first non-zero byte
            int firstNonZero = -1;
            for (int i = 0; i < data.Length; i++)
            {
                if (data[i] != 0)
                {
                    firstNonZero = i;
                    break;
                }
            }

            if (firstNonZero != -1)
            {
                // Check if this non-zero byte is the start of Frame Count (implying Section 1 Count is 0 at idx-2)
                if (firstNonZero >= 2 && IsValidHeader(data, firstNonZero - 2))
                    return firstNonZero - 2;
                
                // Check if this non-zero byte is the start of Section 1 Count
                if (IsValidHeader(data, firstNonZero))
                    return firstNonZero;
            }
            
            // Fallback: Scan all positions (expensive but safe)
            for (int i = 0; i < Math.Min(data.Length, 1000); i++) // Limit scan to first 1000 bytes
            {
                 if (IsValidHeader(data, i)) return i;
            }

            return 0; // Default
        }

        private bool IsValidHeader(byte[] data, int offset)
        {
            if (offset < 0 || offset + 4 > data.Length) return false;
            
            ushort sec1Count = (ushort)(data[offset] | (data[offset + 1] << 8));
            if (sec1Count > 4) return false;

            if (sec1Count == 0)
            {
                ushort frameCount = (ushort)(data[offset + 2] | (data[offset + 3] << 8));
                if (frameCount == 0) return false; // Assume valid files have frames
                if (frameCount > 2000) return false; // Sanity check
            }
            
            return true;
        }
    }

    public class DacSection1Entry
    {
        public List<ushort> Values { get; set; } = new List<ushort>();
    }

    public class DacFrame
    {
        public ushort Unknown0 { get; set; }
        public ushort X { get; set; }
        public ushort Y { get; set; }
        public ushort Width { get; set; }
        public ushort Height { get; set; }
        public byte Unknown1 { get; set; }
        public byte[] Data { get; set; }
    }
}
