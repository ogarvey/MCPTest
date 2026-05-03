using System;
using System.Collections.Generic;

namespace VoodooAssetSystem
{
    /// <summary>
    /// Accurate C# implementation of Voodoo's BitwiseDecompressor
    /// Ghidra analysis confirmed this is standard DEFLATE.
    /// This class now wraps the correct implementation in BitwiseDecompressor.
    /// </summary>
    public static class VoodooBitwiseDecompressor
    {
        /// <summary>
        /// Decompresses bitwise-compressed data using the Voodoo algorithm (DEFLATE)
        /// </summary>
        /// <param name="compressedData">Input compressed data</param>
        /// <param name="expectedOutputSize">Expected output size for buffer allocation</param>
        /// <returns>Decompressed data or null on error</returns>
        public static byte[] Decompress(byte[] compressedData, int expectedOutputSize = 0)
        {
            return BitwiseDecompressor.Decompress(compressedData, expectedOutputSize);
        }
    }
}
