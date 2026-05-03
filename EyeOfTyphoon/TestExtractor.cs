using System;
using System.IO;

namespace EyeOfTyphoon
{
    /// <summary>
    /// Example program demonstrating how to extract and decompress sprites from Eye of Typhoon.
    /// </summary>
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("Eye of Typhoon - CHA Sprite & Animation Extractor");
            Console.WriteLine("==================================================\n");

            if (args.Length < 2)
            {
                Console.WriteLine("Usage:");
                Console.WriteLine("  Sprites only: EyeOfTyphoon.exe <chaFile> <pidFile> [outputDir]");
                Console.WriteLine("  With animations: EyeOfTyphoon.exe <chaFile> <pidFile> <idxFile> <actFile> [outputDir]");
                Console.WriteLine();
                Console.WriteLine("Example:");
                Console.WriteLine("  EyeOfTyphoon.exe maindata\\player.cha maindata\\player.pid sprites\\");
                Console.WriteLine("  EyeOfTyphoon.exe maindata\\player.cha maindata\\player.pid maindata\\player.idx maindata\\player.act animations\\");
                return;
            }

            string chaFile = args[0];
            string pidFile = args[1];
            string idxFile = args.Length > 2 ? args[2] : null;
            string actFile = args.Length > 3 ? args[3] : null;
            string outputDir = args.Length > 4 ? args[4] : "extracted";
            
            bool exportAnimations = !string.IsNullOrEmpty(idxFile) && !string.IsNullOrEmpty(actFile);

            try
            {
                // Load the CHA and PID files
                Console.WriteLine($"Loading CHA file: {chaFile}");
                byte[] chaData = File.ReadAllBytes(chaFile);
                Console.WriteLine($"  Size: {chaData.Length} bytes");

                Console.WriteLine($"Loading PID file: {pidFile}");
                byte[] pidData = File.ReadAllBytes(pidFile);
                Console.WriteLine($"  Size: {pidData.Length} bytes");

                // Extract sprites (in linear format, ready for palette application)
                Console.WriteLine("\nExtracting sprites...");
                var sprites = CHASpriteDecompressor.ExtractAllSpritesLinear(chaData, pidData);
                Console.WriteLine($"  Found {sprites.Length} sprites");

                // Create output directory
                Directory.CreateDirectory(outputDir);

                // Save individual sprites
                string spritesDir = Path.Combine(outputDir, "sprites");
                Directory.CreateDirectory(spritesDir);
                
                for (int i = 0; i < sprites.Length; i++)
                {
                    if (sprites[i] == null)
                        continue;

                    var sprite = sprites[i];
                    Console.WriteLine($"\nSprite {i}:");
                    Console.WriteLine($"  Size: {sprite.Width}x{sprite.Height}");
                    
                    if (sprite.UnknownHeader != null && sprite.UnknownHeader.Length > 0)
                    {
                        Console.Write($"  Header: ");
                        foreach (byte b in sprite.UnknownHeader)
                            Console.Write($"{b:X2} ");
                        Console.WriteLine();
                    }

                    // Save as raw indexed data
                    string rawFile = Path.Combine(spritesDir, $"sprite_{i:D3}_{sprite.Width}x{sprite.Height}.raw");
                    sprite.SaveAsRaw(rawFile);
                    Console.WriteLine($"  Saved: {rawFile}");
                }

                // Export animations if IDX and ACT files provided
                if (exportAnimations)
                {
                    Console.WriteLine($"\nLoading IDX file: {idxFile}");
                    byte[] idxData = File.ReadAllBytes(idxFile);
                    Console.WriteLine($"  Size: {idxData.Length} bytes");

                    Console.WriteLine($"Loading ACT file: {actFile}");
                    byte[] actData = File.ReadAllBytes(actFile);
                    Console.WriteLine($"  Size: {actData.Length} bytes");

                    Console.WriteLine("\nExporting animations...");
                    string animationsDir = Path.Combine(outputDir, "animations");
                    Directory.CreateDirectory(animationsDir);

                    var animations = AnimationExporter.LoadAllAnimations(idxData, actData, sprites);
                    
                    int exportedCount = 0;
                    for (int i = 0; i < animations.Length; i++)
                    {
                        var anim = animations[i];
                        if (anim == null || anim.Frames == null || anim.Frames.Length == 0)
                            continue;

                        Console.WriteLine($"\nAnimation {i}: {anim.Frames.Length} frames");
                        
                        // Export as sprite sheet
                        string sheetFile = Path.Combine(animationsDir, $"anim_{i:D3}_sheet.raw");
                        var (width, height) = AnimationExporter.ExportAnimationSpriteSheet(anim, sheetFile);
                        Console.WriteLine($"  Sprite sheet: {width}x{height} -> {Path.GetFileName(sheetFile)}");

                        // Also export individual frames
                        string animFramesDir = Path.Combine(animationsDir, $"anim_{i:D3}_frames");
                        var (frameW, frameH) = AnimationExporter.ExportAnimationFrames(anim, animFramesDir, $"anim{i:D3}");
                        Console.WriteLine($"  Frames: {anim.Frames.Length} x {frameW}x{frameH} -> {Path.GetFileName(animFramesDir)}/");

                        exportedCount++;
                    }

                    Console.WriteLine($"\n✓ Exported {exportedCount} animations");
                }

                Console.WriteLine("\n✓ Extraction complete!");
                Console.WriteLine($"\nOutput saved to: {Path.GetFullPath(outputDir)}");
                Console.WriteLine("\nNOTE: RAW files are 8-bit indexed. You'll need to apply");
                Console.WriteLine("      the palette from the .PAL file to view true colors.");
            }
            catch (FileNotFoundException ex)
            {
                Console.WriteLine($"\n✗ Error: File not found - {ex.FileName}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n✗ Error: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
        }
    }
}
