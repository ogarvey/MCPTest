using System;
using System.IO;

namespace Arcturus
{
    internal static class TestExtractor
    {
        // Example usage:
        //   TestExtractor.exe "C:\Games\Arcturus\data.pak" "C:\Temp\arcturus_out"
        private static int Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: TestExtractor <pakPath> <outputDir>");
                return 1;
            }

            string pakPath = args[0];
            string outDir = args[1];

            if (!File.Exists(pakPath))
            {
                Console.WriteLine($"PAK not found: {pakPath}");
                return 2;
            }

            try
            {
                var pak = ArcturusPakExtractor.PakArchive.Load(pakPath);
                Console.WriteLine($"Loaded {Path.GetFileName(pakPath)} with {pak.EntryCount} entries.");

                Directory.CreateDirectory(outDir);
                string reportPath = Path.Combine(outDir, "index_report.txt");
                pak.WriteIndexReport(reportPath);

                pak.ExtractAll(outDir);
                Console.WriteLine($"Extraction complete: {outDir}");
                Console.WriteLine($"Index report: {reportPath}");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Extraction failed:");
                Console.WriteLine(ex);
                return 3;
            }
        }
    }
}
