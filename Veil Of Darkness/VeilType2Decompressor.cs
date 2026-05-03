using System;
using System.Collections.Generic;

namespace VeilOfDarkness
{
    /// <summary>
    /// Decompressor for Type 2 resources.
    /// Based on the hypothesis that Type 2 resources use the same "Columnar" format
    /// as the images found in RES1, as they are likely handled by the same graphics driver (INT 60h).
    /// </summary>
    public class VeilType2Decompressor
    {
        public struct DecompressedImage
        {
            public byte[] Data;
            public int Width;
            public int Height;
            public int StartX;
        }

        public static DecompressedImage Decompress(byte[] fileData)
        {
            // The format appears to be identical to the Columnar format.
            // We reuse the logic from VeilColumnarDecompressor.
            return VeilColumnarDecompressor.Decompress(fileData);
        }
    }
}
