namespace Arcturus.ModelExportTool;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        string inputPath = args[0];
        if (!File.Exists(inputPath))
        {
            Console.WriteLine($"Input file not found: {inputPath}");
            return 2;
        }

        string baseName = Path.GetFileNameWithoutExtension(inputPath);
        string outDir = Path.GetDirectoryName(inputPath) ?? Environment.CurrentDirectory;
        string objPath = Path.Combine(outDir, baseName + ".obj");
        string castPath = Path.Combine(outDir, baseName + ".cast");

        bool exportObj = true;
        bool exportCast = true;
        bool flipV = true;

        for (int i = 1; i < args.Length; i++)
        {
            string arg = args[i];

            switch (arg)
            {
                case "--obj":
                    exportObj = true;
                    exportCast = false;
                    break;

                case "--cast":
                    exportObj = false;
                    exportCast = true;
                    break;

                case "--both":
                    exportObj = true;
                    exportCast = true;
                    break;

                case "--out-dir" when i + 1 < args.Length:
                    outDir = args[++i];
                    objPath = Path.Combine(outDir, baseName + ".obj");
                    castPath = Path.Combine(outDir, baseName + ".cast");
                    break;

                case "--obj-path" when i + 1 < args.Length:
                    objPath = args[++i];
                    exportObj = true;
                    break;

                case "--cast-path" when i + 1 < args.Length:
                    castPath = args[++i];
                    exportCast = true;
                    break;

                case "--no-flip-v":
                    flipV = false;
                    break;

                default:
                    Console.WriteLine($"Unknown option: {arg}");
                    PrintUsage();
                    return 1;
            }
        }

        try
        {
            var model = ArcturusModelParser.Load(inputPath);
            Console.WriteLine($"Loaded model '{model.Name}' with {model.Meshes.Count} mesh(es).");

            if (exportObj)
            {
                ModelExporters.ExportObj(model, objPath, flipV);
                Console.WriteLine($"OBJ exported: {objPath}");
            }

            if (exportCast)
            {
                ModelExporters.ExportCast(model, castPath, flipV);
                Console.WriteLine($"CAST exported: {castPath}");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Export failed:");
            Console.WriteLine(ex.Message);
            return 3;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Arcturus Model Export Tool");
        Console.WriteLine("Usage:");
        Console.WriteLine("  Arcturus.ModelExportTool <model.rsm|model.rsx> [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --obj              Export OBJ only");
        Console.WriteLine("  --cast             Export CAST only");
        Console.WriteLine("  --both             Export OBJ and CAST (default)");
        Console.WriteLine("  --out-dir <path>   Output folder for generated files");
        Console.WriteLine("  --obj-path <path>  Explicit OBJ output path");
        Console.WriteLine("  --cast-path <path> Explicit CAST output path");
        Console.WriteLine("  --no-flip-v        Keep V coordinate as-is (default is flipped)");
    }
}
