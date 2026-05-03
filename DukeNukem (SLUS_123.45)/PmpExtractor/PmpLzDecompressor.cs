internal static class PmpLzDecompressor
{
    public static byte[] Decompress(ReadOnlySpan<byte> compressedStream, int expectedSize)
    {
        List<byte> output = new(Math.Max(expectedSize, 0x100));
        int sourceOffset = 0;
        uint flags = 0;

        while (true)
        {
            while (true)
            {
                flags >>= 1;
                if ((flags & 0xff00) == 0)
                {
                    EnsureReadable(compressedStream, sourceOffset, 1);
                    flags = (uint)(compressedStream[sourceOffset++] | 0xff00);
                }

                if ((flags & 1) != 0)
                {
                    break;
                }

                EnsureReadable(compressedStream, sourceOffset, 1);
                output.Add(compressedStream[sourceOffset++]);
            }

            EnsureReadable(compressedStream, sourceOffset, 1);
            byte token = compressedStream[sourceOffset++];

            int distance;
            int length;

            if (token < 0x60)
            {
                EnsureReadable(compressedStream, sourceOffset, 1);
                distance = ((token & 0x0f) << 8) | compressedStream[sourceOffset++];
                if (distance == 0)
                {
                    break;
                }

                length = (token >> 4) + 3;
                if ((token >> 4) == 5)
                {
                    EnsureReadable(compressedStream, sourceOffset, 1);
                    length = compressedStream[sourceOffset++] + 8;
                }
            }
            else
            {
                distance = 0x100 - token;
                length = 2;
            }

            if (distance <= 0 || distance > output.Count)
            {
                throw new InvalidDataException($"Invalid back-reference distance {distance} at compressed offset 0x{sourceOffset:X}.");
            }

            int copyIndex = output.Count - distance;
            for (int remaining = 0; remaining < length; remaining++)
            {
                output.Add(output[copyIndex++]);
            }
        }

        if (expectedSize >= 0 && output.Count != expectedSize)
        {
            throw new InvalidDataException($"Decompressed size mismatch. Expected 0x{expectedSize:X}, got 0x{output.Count:X}.");
        }

        return output.ToArray();
    }

    private static void EnsureReadable(ReadOnlySpan<byte> data, int offset, int byteCount)
    {
        if ((uint)offset > data.Length || byteCount < 0 || offset + byteCount > data.Length)
        {
            throw new InvalidDataException("Compressed stream ended unexpectedly.");
        }
    }
}
