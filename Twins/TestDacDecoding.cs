using System;
using System.IO;
using Twins;

namespace Twins
{
    class Program
    {
        static void Main(string[] args)
        {
            try
            {
                // Hardcoded paths based on previous run output
                string uniPath = @"C:\Dev\Gaming\PC\Dos\Games\Twins_DOS_KR\UNI";
                string baseDir = Path.GetDirectoryName(uniPath);

                string[] dacFiles = { "TND.DAC", "TNK.DAC", "ANI.DAC", "CHICK.DAC" };
                
                foreach (var dacFile in dacFiles)
                {
                    string fullDacPath = Path.Combine(baseDir, dacFile);
                    if (!File.Exists(fullDacPath))
                    {
                        Console.WriteLine($"Skipping {dacFile} (not found)");
                        continue;
                    }

                    Console.WriteLine($"\n--- Processing {dacFile} ---");
                    var decompressor = new TwinsDecompressor(uniPath);
                    byte[] decompressedData;
                    try
                    {
                        decompressedData = decompressor.Load_Compressed_Resource(fullDacPath);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Decompression failed for {dacFile}: {ex.Message}");
                        continue;
                    }

                    // Dump output so we can inspect it in a hex editor.
                    string outPath = Path.Combine(Directory.GetCurrentDirectory(), $"{dacFile}.decompressed.bin");
                    File.WriteAllBytes(outPath, decompressedData);
                    Console.WriteLine($"Wrote: {outPath}");

                    Console.WriteLine($"Decompressed Data Size: {decompressedData.Length}");
                    
                    Console.WriteLine("Parsing decompressed data...");
                    var dac = new TwinsDacFile();
                    try
                    {
                        dac.Parse(decompressedData);
                        Console.WriteLine($"Successfully parsed {dacFile}!");
                        Console.WriteLine($"Detected DAC start offset: 0x{dac.LastDetectedOffset:X}");
                        Console.WriteLine($"Section 1 Entries: {dac.Section1Entries.Count}");
                        Console.WriteLine($"Frames: {dac.Frames.Count}");

                        for (int i = 0; i < Math.Min(6, dac.Frames.Count); i++)
                        {
                            var f = dac.Frames[i];
                            Console.WriteLine($"Frame {i}: {f.Width}x{f.Height} at ({f.X},{f.Y}), DataSize={f.Data.Length}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Detected DAC start offset: 0x{dac.LastDetectedOffset:X}");
                        Console.WriteLine($"Failed to parse {dacFile}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
        }
    }
}
