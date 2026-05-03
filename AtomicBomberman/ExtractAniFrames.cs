using System;
using System.IO;

namespace AtomicBomberman
{
    public static class ExtractAniFrames
    {
        // Example:
        //   dotnet run --project <your-csproj> -- "C:\path\file.ani" "C:\out\frames"
        public static void Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: ExtractAniFrames <input.ani> <outputDir>");
                return;
            }

            string inputAni = args[0];
            string outputDir = args[1];

            if (!File.Exists(inputAni))
            {
                Console.WriteLine($"Input not found: {inputAni}");
                return;
            }

            Directory.CreateDirectory(outputDir);
            AtomicBombermanAniExtractor.ExportFramesAsBmp(inputAni, outputDir);

            Console.WriteLine($"Done. Frames exported to: {outputDir}");
        }
    }
}
