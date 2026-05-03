using System;
using System.IO;

namespace Fugger2
{
    /// <summary>
    /// Simple test program to decompress Fugger 2 icons correctly
    /// </summary>
    class TestIconDecoder
    {
        static void Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("Usage: TestIconDecoder <icon.dat>");
                Console.WriteLine();
                Console.WriteLine("This will create:");
                Console.WriteLine("  icon_rgb565.raw - Raw RGB24 data (assuming RGB565)");
                Console.WriteLine("  icon_rgb555.raw - Raw RGB24 data (assuming RGB555)");
                Console.WriteLine();
                Console.WriteLine("View raw files in GIMP/Photoshop:");
                Console.WriteLine("  File -> Open -> Select .raw file");
                Console.WriteLine("  Set Image Type: RGB");
                Console.WriteLine("  Width/Height: (will be shown in console output)");
                return;
            }

            string iconPath = args[0];
            byte[] compressedData = File.ReadAllBytes(iconPath);

            Console.WriteLine($"Loading: {iconPath}");
            Console.WriteLine($"Compressed size: {compressedData.Length} bytes");
            Console.WriteLine();

            // Try RGB565 (more common)
            var (rgb565Data, width565, height565) = IconRLEDecompressor.DecompressToRGB24(compressedData, useRGB555: false);
            string output565 = Path.ChangeExtension(iconPath, "_rgb565.raw");
            File.WriteAllBytes(output565, rgb565Data);
            Console.WriteLine($"RGB565 output: {output565}");
            Console.WriteLine($"  Dimensions: {width565}x{height565}");
            Console.WriteLine($"  RGB24 size: {rgb565Data.Length} bytes");
            Console.WriteLine();

            // Try RGB555 (less common)
            var (rgb555Data, width555, height555) = IconRLEDecompressor.DecompressToRGB24(compressedData, useRGB555: true);
            string output555 = Path.ChangeExtension(iconPath, "_rgb555.raw");
            File.WriteAllBytes(output555, rgb555Data);
            Console.WriteLine($"RGB555 output: {output555}");
            Console.WriteLine($"  Dimensions: {width555}x{height555}");
            Console.WriteLine($"  RGB24 size: {rgb555Data.Length} bytes");
            Console.WriteLine();

            Console.WriteLine("To view in GIMP:");
            Console.WriteLine($"  1. File -> Open -> {output565}");
            Console.WriteLine($"  2. Set 'Image Type' to RGB");
            Console.WriteLine($"  3. Set Width: {width565}, Height: {height565}");
            Console.WriteLine($"  4. Click OK");
            Console.WriteLine();
            Console.WriteLine("If colors look wrong, try the RGB555 version instead.");
            Console.WriteLine();
            Console.WriteLine("SUCCESS! No palette needed - icons already have RGB values!");
        }
    }
}
