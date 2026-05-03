using System;
using System.IO;

namespace JurassicParkTools
{
    /// <summary>
    /// Decompresses asset files from the DOS game Jurassic Park.
    /// The algorithm is a variation of LZSS.
    /// </summary>
    public class JurassicParkDecompressor
    {
        private const ushort XorKey = 0x7c2b;

        /// <summary>
        /// Helper class to read bits from a byte array.
        /// </summary>
        private class BitReader
        {
            private readonly byte[] _data;
            private int _bytePosition;
            private byte _bitPosition;

            public BitReader(byte[] data)
            {
                _data = data;
                _bytePosition = 0;
                _bitPosition = 0;
            }

            public ushort ReadWord()
            {
                if (_bytePosition + 1 >= _data.Length)
                {
                    // Not enough data for a full word, might happen at the end
                    return 0;
                }
                ushort value = BitConverter.ToUInt16(_data, _bytePosition);
                _bytePosition += 2;
                return value;
            }

            public uint ReadBits(int count)
            {
                uint value = 0;
                for (int i = 0; i < count; i++)
                {
                    if (_bytePosition >= _data.Length)
                    {
                        break; // End of stream
                    }

                    uint bit = (uint)(_data[_bytePosition] >> (7 - _bitPosition)) & 1;
                    value = (value << 1) | bit;

                    _bitPosition++;
                    if (_bitPosition == 8)
                    {
                        _bitPosition = 0;
                        _bytePosition++;
                    }
                }
                return value;
            }
        }

        /// <summary>
        /// Decompresses a Jurassic Park asset file.
        /// </summary>
        /// <param name="filePath">The path to the compressed file.</param>
        public void Decompress(string filePath)
        {
            if (!File.Exists(filePath))
            {
                Console.WriteLine($"Error: File not found at {filePath}");
                return;
            }

            byte[] compressedData = File.ReadAllBytes(filePath);
            byte[] decompressedData = DecompressData(compressedData);

            string outputFilePath = filePath + ".decompressed";
            File.WriteAllBytes(outputFilePath, decompressedData);

            Console.WriteLine($"Decompression successful. Output written to {outputFilePath}");
        }

        private byte[] DecompressData(byte[] compressedData)
        {
            var reader = new BitReader(compressedData);

            // The first word is an offset/magic, not the size. We skip it as per the assembly analysis.
            reader.ReadWord(); 
            
            // The second word is also skipped/unused according to the disassembly.
            reader.ReadWord(); 

            var outputList = new List<byte>();
            
            uint bitStream = (uint)(reader.ReadWord() ^ XorKey);

            // A failsafe to prevent infinite loops with malformed data.
            int safetyBreak = compressedData.Length * 8 * 2; 
            int operations = 0;

            while (operations < safetyBreak)
            {
                operations++;

                if (reader.IsFinished())
                {
                    break;
                }

                uint controlBit = bitStream & 1;
                bitStream >>= 1;
                if (bitStream == 0)
                {
                    bitStream = (uint)(reader.ReadWord() ^ XorKey);
                    if (bitStream == 0) // End of stream marker
                    {
                        // Check if the next word is also 0, which seems to be a reliable EOF marker
                        if (reader.PeekWord() == 0) break;
                    }
                    bitStream |= 0x8000;
                }

                if (controlBit == 0) // Literal copy
                {
                    uint type = ReadNextBits(ref bitStream, reader, 1);
                    int count;
                    if (type == 1)
                    {
                        count = (int)ReadNextBits(ref bitStream, reader, 3) + 1;
                    }
                    else
                    {
                        count = (int)ReadNextBits(ref bitStream, reader, 8) + 1;
                    }

                    for (int i = 0; i < count; i++)
                    {
                        outputList.Add((byte)ReadNextBits(ref bitStream, reader, 8));
                    }
                }
                else // Copy from dictionary (previously decompressed data)
                {
                    uint type = ReadNextBits(ref bitStream, reader, 2);
                    int length;
                    int offset;

                    if (type == 3)
                    {
                        length = (int)ReadNextBits(ref bitStream, reader, 8);
                        offset = (int)ReadNextBits(ref bitStream, reader, 12) + 1;
                    }
                    else if (type < 2)
                    {
                        length = (int)type + 2;
                        offset = (int)ReadNextBits(ref bitStream, reader, (int)type + 9) + 1;
                    }
                    else // type == 2
                    {
                        length = (int)ReadNextBits(ref bitStream, reader, 8) + 1;
                        offset = (int)ReadNextBits(ref bitStream, reader, 8) + 1;
                    }

                    for (int i = 0; i < length; i++)
                    {
                        int readPos = outputList.Count - offset;
                        if (readPos >= 0 && readPos < outputList.Count)
                        {
                            outputList.Add(outputList[readPos]);
                        }
                        else
                        {
                            // This can happen with some data, implies a 0-byte.
                            outputList.Add(0);
                        }
                    }
                }
            }

            outputList.Reverse();
            return outputList.ToArray();
        }

        private uint ReadNextBits(ref uint bitStream, BitReader reader, int count)
        {
            uint value = 0;
            for (int i = 0; i < count; i++)
            {
                value |= (bitStream & 1) << i;
                bitStream >>= 1;
                if (bitStream == 0)
                {
                    bitStream = (uint)(reader.ReadWord() ^ XorKey) | 0x8000;
                }
            }
            return value;
        }
    }
}
