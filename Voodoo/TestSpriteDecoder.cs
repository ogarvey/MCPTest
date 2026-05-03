using System;
using System.IO;
using Voodoo.Assets;

namespace Voodoo
{
    public class TestSpriteDecoder
    {
        public static void Main(string[] args)
        {
            // Example usage
            string burpFilePath = "DATA_G.KID"; // Example file
            int assetIndex = 123; // Example asset index
            
            // 1. Load Burp File (Mocked)
            // var burpFile = new BurpFile(burpFilePath);
            // var entry = burpFile.Entries[assetIndex];
            
            // 2. Decompress Asset (Type 4)
            // byte[] compressedData = burpFile.ReadData(entry);
            // byte[] decompressedData = VoodooBitwiseDecompressor.Decompress(compressedData, entry.UncompressedSize);
            
            // Mock decompressed data for testing
            byte[] decompressedData = new byte[1024]; 
            // ... fill with test data ...

            // 3. Extract Sprite Frame
            var extractor = new VoodooSpriteExtractor();
            try
            {
                // Extract Frame 0
                var frame = extractor.ExtractFrame(decompressedData, 0);
                
                Console.WriteLine($"Frame Extracted: {frame.SubFrames.Count} sub-frames");
                
                foreach (var sub in frame.SubFrames)
                {
                    Console.WriteLine($"  SubFrame: {sub.Width}x{sub.Height}, Type: {sub.Type}");
                    if (sub.DecodedPixels != null)
                    {
                        Console.WriteLine($"  Pixels: {sub.DecodedPixels.Length} bytes");
                        // Save to bitmap or process
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Extraction failed: {ex.Message}");
            }
        }
    }
}
