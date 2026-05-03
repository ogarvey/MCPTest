using System;

namespace VolUnpacker
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: VolUnpacker <vol_file> <output_dir>");
                return;
            }

            string volFile = args[0];
            string outputDir = args[1];

            try
            {
                VolFile.Unpack(volFile, outputDir);
                Console.WriteLine("Unpacking complete.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
        }
    }
}
