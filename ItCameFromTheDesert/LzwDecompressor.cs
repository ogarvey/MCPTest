using System;

namespace ItCameFromTheDesert
{
    public static class LzwDecompressor
    {
        // Decompresses a single resource payload that begins with optional marker ("LZ" or "SMS")
        // followed by: 1 byte (trailingByte), 4 bytes big-endian uncompressed size, then bitstream.
        public static byte[] Decompress(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (data.Length < 6) throw new ArgumentException("Data too small.", nameof(data));

            var offset = 0;
            if (data.Length >= 2 && data[0] == (byte)'L' && data[1] == (byte)'Z')
            {
                offset = 2;
            }
            else if (data.Length >= 3 && data[0] == (byte)'S' && data[1] == (byte)'M' && data[2] == (byte)'S')
            {
                offset = (data.Length >= 4 && data[3] == 0x00) ? 4 : 3;
            }

            if (data.Length < offset + 5)
            {
                throw new ArgumentException("Data too small for header.", nameof(data));
            }

            var trailingByte = data[offset];
            var decodedSize = ReadUInt32BigEndian(data, offset + 1);
            if (decodedSize == 0)
            {
                return Array.Empty<byte>();
            }

            var bitstreamStart = offset + 5;
            if (bitstreamStart > data.Length)
            {
                throw new ArgumentException("Invalid header offsets.", nameof(data));
            }

            var output = new byte[decodedSize];

            var prefix = new ushort[0x1000];
            var suffix = new byte[0x1000];

            var codeWidth = 9;
            var maxCode = (1 << codeWidth) - 1;       // corresponds to A4+0x2b1e
            var maxCodeMinus1 = maxCode - 1;          // corresponds to A4+0x2b22
            var nextCode = 0x100;                     // corresponds to A4+0x2b24

            var reader = new BitReader(data, bitstreamStart);

            int outPos = 0;
            int prevCode = reader.ReadBits(codeWidth, ref maxCode, ref maxCodeMinus1, ref codeWidth);
            if (prevCode < 0)
            {
                return output;
            }

            if (prevCode == maxCode)
            {
                WriteLastByte(output, trailingByte);
                return output;
            }

            byte prevChar = (byte)prevCode;
            output[outPos++] = prevChar;

            while (outPos < output.Length)
            {
                int code = reader.ReadBits(codeWidth, ref maxCode, ref maxCodeMinus1, ref codeWidth);
                if (code < 0)
                {
                    break;
                }

                if (code == maxCodeMinus1)
                {
                    IncreaseCodeWidth(ref codeWidth, ref maxCode, ref maxCodeMinus1);
                    continue;
                }

                if (code == maxCode)
                {
                    break;
                }

                var stack = new byte[0x1000];
                int stackLen = 0;

                int cur = code;
                if (cur >= nextCode)
                {
                    stack[stackLen++] = prevChar;
                    cur = prevCode;
                }

                while (cur > 0xFF)
                {
                    stack[stackLen++] = suffix[cur];
                    cur = prefix[cur];
                }

                byte firstChar = (byte)cur;
                stack[stackLen++] = firstChar;

                for (int i = stackLen - 1; i >= 0 && outPos < output.Length; i--)
                {
                    output[outPos++] = stack[i];
                }

                if (nextCode < 0x1000)
                {
                    prefix[nextCode] = (ushort)prevCode;
                    suffix[nextCode] = firstChar;
                    nextCode++;
                }

                prevCode = code;
                prevChar = firstChar;
            }

            WriteLastByte(output, trailingByte);
            return output;
        }

        private static void WriteLastByte(byte[] output, byte headerByte)
        {
            if (output.Length == 0) return;
            output[^1] = headerByte;
        }

        private static void IncreaseCodeWidth(ref int codeWidth, ref int maxCode, ref int maxCodeMinus1)
        {
            if (codeWidth < 12)
            {
                codeWidth++;
                maxCode = (1 << codeWidth) - 1;
                maxCodeMinus1 = maxCode - 1;
            }
        }

        private static uint ReadUInt32BigEndian(byte[] data, int offset)
        {
            if (offset + 4 > data.Length) return 0;
            return (uint)(data[offset] << 24 | data[offset + 1] << 16 | data[offset + 2] << 8 | data[offset + 3]);
        }

        private sealed class BitReader
        {
            private readonly byte[] _data;
            private int _pos;
            private uint _bitBuffer;
            private int _bitsInBuffer;

            public BitReader(byte[] data, int startOffset)
            {
                _data = data;
                _pos = startOffset;
                _bitBuffer = 0;
                _bitsInBuffer = 0;
            }

            public int ReadBits(int codeWidth, ref int maxCode, ref int maxCodeMinus1, ref int currentWidth)
            {
                // Mimic the 68k reader: ensure we have at least 25 bits cached.
                for (int s = _bitsInBuffer; s < 0x19; s += 8)
                {
                    if (_pos >= _data.Length)
                    {
                        return -1;
                    }

                    _bitBuffer |= (uint)_data[_pos++] << (0x18 - s);
                }

                _bitsInBuffer = _bitsInBuffer + 8 * ((25 - _bitsInBuffer + 7) / 8);
                _bitsInBuffer -= codeWidth;

                var value = (int)(_bitBuffer >> (0x20 - codeWidth));
                _bitBuffer <<= codeWidth;
                return value;
            }
        }
    }
}
