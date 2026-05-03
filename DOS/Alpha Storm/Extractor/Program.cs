using System.Globalization;

var options = ParseArguments(args);
if (options.ShowHelp)
{
	PrintUsage();
	return args.Length == 0 ? 1 : 0;
}

if (options.ErrorMessage is not null)
{
	Console.Error.WriteLine(options.ErrorMessage);
	PrintUsage();
	return 1;
}

var inputPath = Path.GetFullPath(options.InputPath!);
if (!File.Exists(inputPath))
{
	Console.Error.WriteLine($"Input file not found: {inputPath}");
	return 1;
}

var inputExtension = Path.GetExtension(inputPath).ToLowerInvariant();
var outputDirectory = options.OutputDirectory is not null
	? Path.GetFullPath(options.OutputDirectory)
	: Path.Combine(Path.GetDirectoryName(inputPath) ?? Environment.CurrentDirectory,
		Path.GetFileNameWithoutExtension(inputPath) + GetDefaultOutputSuffix(inputExtension));

Directory.CreateDirectory(outputDirectory);

try
{
	string? paletteDescription;
	switch (inputExtension)
	{
		case ".lif":
		{
			var palette = LoadPalette(options.PalettePath, options.PaletteOffset, inputPath, out paletteDescription);
			ExtractLif(inputPath, outputDirectory, palette);
			break;
		}

		case ".bif":
		{
			var palette = LoadPalette(options.PalettePath, options.PaletteOffset, inputPath, out paletteDescription);
			ExtractBif(inputPath, outputDirectory, palette);
			break;
		}

		case ".wsp":
		{
			var palette = LoadPalette(options.PalettePath, options.PaletteOffset, inputPath, out paletteDescription);
			ExtractWsp(inputPath, outputDirectory, palette);
			break;
		}

		case ".wac":
		{
			var palette = LoadPalette(options.PalettePath, options.PaletteOffset, inputPath, out paletteDescription);
			ExtractWac(inputPath, outputDirectory, palette);
			break;
		}

		case ".pac":
		{
			var paletteOverride = LoadExplicitPalette(options.PalettePath, options.PaletteOffset, out paletteDescription);
			ExtractPac(inputPath, outputDirectory, paletteOverride);
			paletteDescription ??= $"embedded palette from {Path.GetFileName(inputPath)}";
			break;
		}

		case ".tex":
		{
			var palette = LoadPalette(options.PalettePath, options.PaletteOffset, inputPath, out paletteDescription);
			ExtractTex(inputPath, outputDirectory, palette);
			break;
		}

		default:
			throw new InvalidDataException($"Unsupported input format: {inputExtension}");
	}

	Console.WriteLine(paletteDescription is null ? "Palette: grayscale index preview" : $"Palette: {paletteDescription}");
	Console.WriteLine($"Output: {outputDirectory}");
	return 0;
}
catch (Exception exception)
{
	Console.Error.WriteLine(exception.Message);
	return 1;
}

static void PrintUsage()
{
	Console.WriteLine("Alpha Storm graphics extractor");
	Console.WriteLine();
	Console.WriteLine("Usage:");
	Console.WriteLine("  Extractor <input.lif|input.bif|input.wsp|input.wac|input.pac|input.tex> [output-directory] [--palette <file>] [--palette-offset <offset>]");
	Console.WriteLine();
	Console.WriteLine("If no palette is specified, the extractor looks for DUM_SINE.BIN next to the input and reads");
	Console.WriteLine($"a 256-color RGB888 palette at 0x{RgbPalette.DefaultDumSineOffset:X}. PAC files use their embedded CMAP unless an explicit override is provided.");
}

static void ExtractLif(string inputPath, string outputDirectory, RgbPalette? palette)
{
	var lif = LifFile.Load(inputPath);
	var stem = Path.GetFileNameWithoutExtension(inputPath);
	var manifestPath = Path.Combine(outputDirectory, "manifest.csv");

	using var manifest = new StreamWriter(manifestPath, false);
	manifest.WriteLine("frame,width,height,encoded_offset,encoded_length,opaque_pixels,bounds_left,bounds_top,bounds_right,bounds_bottom,png");

	foreach (var frame in lif.Frames)
	{
		var fileName = $"{stem}_{frame.Index:D3}.png";
		var pngPath = Path.Combine(outputDirectory, fileName);
		PngWriter.WriteIndexedPreview(pngPath, lif.Width, lif.Height, frame.Pixels, frame.Alpha, palette);
		WriteManifestRow(manifest, frame.Index, lif.Width, lif.Height, frame.EncodedOffset, frame.EncodedLength, frame.OpaquePixelCount, frame.Bounds, fileName);
	}

	Console.WriteLine($"Decoded {lif.Frames.Count} frame(s) from {Path.GetFileName(inputPath)}.");
	Console.WriteLine($"Canvas: {lif.Width}x{lif.Height}");
}

static void ExtractBif(string inputPath, string outputDirectory, RgbPalette? palette)
{
	var bif = BifFile.Load(inputPath);
	var stem = Path.GetFileNameWithoutExtension(inputPath);
	var manifestPath = Path.Combine(outputDirectory, "manifest.csv");

	using var manifest = new StreamWriter(manifestPath, false);
	manifest.WriteLine("entry,width,height,encoded_offset,encoded_length,opaque_pixels,bounds_left,bounds_top,bounds_right,bounds_bottom,png");

	var firstImage = bif.Images[0];
	var uniformSize = true;
	for (var index = 0; index < bif.Images.Count; index++)
	{
		var image = bif.Images[index];
		var fileName = $"{stem}_{image.Index:D3}.png";
		var pngPath = Path.Combine(outputDirectory, fileName);
		PngWriter.WriteIndexedPreview(pngPath, image.Width, image.Height, image.Pixels, image.Alpha, palette);
		WriteManifestRow(manifest, image.Index, image.Width, image.Height, image.EncodedOffset, image.EncodedLength, image.OpaquePixelCount, image.Bounds, fileName);

		if (image.Width != firstImage.Width || image.Height != firstImage.Height)
		{
			uniformSize = false;
		}
	}

	Console.WriteLine($"Decoded {bif.Images.Count} sprite(s) from {Path.GetFileName(inputPath)}.");
	if (uniformSize)
	{
		Console.WriteLine($"Sprite size: {firstImage.Width}x{firstImage.Height}");
	}
	else
	{
		Console.WriteLine("Sprite sizes vary; see manifest.csv for per-entry dimensions.");
	}
}

static void ExtractWsp(string inputPath, string outputDirectory, RgbPalette? palette)
{
	var wsp = WspFile.Load(inputPath);
	var stem = Path.GetFileNameWithoutExtension(inputPath);
	var manifestPath = Path.Combine(outputDirectory, "manifest.csv");
	var totalFrames = 0;

	using var manifest = new StreamWriter(manifestPath, false);
	manifest.WriteLine("entry,frame,width,height,encoded_offset,encoded_length,opaque_pixels,bounds_left,bounds_top,bounds_right,bounds_bottom,png");

	foreach (var spriteSet in wsp.SpriteSets)
	{
		foreach (var frame in spriteSet.SpriteSet.Frames)
		{
			var fileName = $"{stem}_{spriteSet.Index:D3}_{frame.Index:D3}.png";
			var pngPath = Path.Combine(outputDirectory, fileName);
			PngWriter.WriteIndexedPreview(pngPath, spriteSet.SpriteSet.Width, spriteSet.SpriteSet.Height, frame.Pixels, frame.Alpha, palette);

			var bounds = frame.Bounds;
			manifest.Write(spriteSet.Index);
			manifest.Write(',');
			manifest.Write(frame.Index);
			manifest.Write(',');
			manifest.Write(spriteSet.SpriteSet.Width);
			manifest.Write(',');
			manifest.Write(spriteSet.SpriteSet.Height);
			manifest.Write(',');
			manifest.Write(spriteSet.EncodedOffset + frame.EncodedOffset);
			manifest.Write(',');
			manifest.Write(frame.EncodedLength);
			manifest.Write(',');
			manifest.Write(frame.OpaquePixelCount);
			manifest.Write(',');
			manifest.Write(bounds.IsEmpty ? string.Empty : bounds.Left.ToString());
			manifest.Write(',');
			manifest.Write(bounds.IsEmpty ? string.Empty : bounds.Top.ToString());
			manifest.Write(',');
			manifest.Write(bounds.IsEmpty ? string.Empty : bounds.Right.ToString());
			manifest.Write(',');
			manifest.Write(bounds.IsEmpty ? string.Empty : bounds.Bottom.ToString());
			manifest.Write(',');
			manifest.WriteLine(fileName);
			totalFrames++;
		}
	}

	Console.WriteLine($"Decoded {wsp.SpriteSets.Count} sprite set(s) and {totalFrames} frame(s) from {Path.GetFileName(inputPath)}.");
	Console.WriteLine($"Container entries: {wsp.EntryCount} ({wsp.NonSpriteEntryCount} non-sprite entries skipped)");
}

static void ExtractWac(string inputPath, string outputDirectory, RgbPalette? palette)
{
	var wac = WacFile.Load(inputPath);
	var stem = Path.GetFileNameWithoutExtension(inputPath);

	if (wac.SpriteSets.Count != 0)
	{
		var spriteManifestPath = Path.Combine(outputDirectory, "manifest.csv");
		var totalFrames = 0;

		using var spriteManifest = new StreamWriter(spriteManifestPath, false);
		spriteManifest.WriteLine("entry,frame,width,height,encoded_offset,encoded_length,opaque_pixels,bounds_left,bounds_top,bounds_right,bounds_bottom,png");

		foreach (var spriteSet in wac.SpriteSets)
		{
			foreach (var frame in spriteSet.SpriteSet.Frames)
			{
				var fileName = $"{stem}_{spriteSet.Index:D3}_{frame.Index:D3}.png";
				var pngPath = Path.Combine(outputDirectory, fileName);
				PngWriter.WriteIndexedPreview(pngPath, spriteSet.SpriteSet.Width, spriteSet.SpriteSet.Height, frame.Pixels, frame.Alpha, palette);

				var bounds = frame.Bounds;
				spriteManifest.Write(spriteSet.Index);
				spriteManifest.Write(',');
				spriteManifest.Write(frame.Index);
				spriteManifest.Write(',');
				spriteManifest.Write(spriteSet.SpriteSet.Width);
				spriteManifest.Write(',');
				spriteManifest.Write(spriteSet.SpriteSet.Height);
				spriteManifest.Write(',');
				spriteManifest.Write(spriteSet.EncodedOffset + frame.EncodedOffset);
				spriteManifest.Write(',');
				spriteManifest.Write(frame.EncodedLength);
				spriteManifest.Write(',');
				spriteManifest.Write(frame.OpaquePixelCount);
				spriteManifest.Write(',');
				spriteManifest.Write(bounds.IsEmpty ? string.Empty : bounds.Left.ToString());
				spriteManifest.Write(',');
				spriteManifest.Write(bounds.IsEmpty ? string.Empty : bounds.Top.ToString());
				spriteManifest.Write(',');
				spriteManifest.Write(bounds.IsEmpty ? string.Empty : bounds.Right.ToString());
				spriteManifest.Write(',');
				spriteManifest.Write(bounds.IsEmpty ? string.Empty : bounds.Bottom.ToString());
				spriteManifest.Write(',');
				spriteManifest.WriteLine(fileName);
				totalFrames++;
			}
		}

		Console.WriteLine($"Decoded {wac.SpriteSets.Count} sprite set(s) and {totalFrames} frame(s) from {Path.GetFileName(inputPath)}.");
		Console.WriteLine($"Container entries: {wac.EntryCount}");
		return;
	}

	var manifestPath = Path.Combine(outputDirectory, "manifest.csv");
	var hasTrailerBytes = false;

	using var manifest = new StreamWriter(manifestPath, false);
	manifest.WriteLine("entry,width,height,encoded_offset,encoded_length,consumed_length,trailing_length,opaque_pixels,bounds_left,bounds_top,bounds_right,bounds_bottom,png");

	foreach (var image in wac.Images)
	{
		var fileName = $"{stem}_{image.Index:D3}.png";
		var pngPath = Path.Combine(outputDirectory, fileName);
		PngWriter.WriteIndexedPreview(pngPath, image.Width, image.Height, image.Pixels, image.Alpha, palette);

		var bounds = image.Bounds;
		manifest.Write(image.Index);
		manifest.Write(',');
		manifest.Write(image.Width);
		manifest.Write(',');
		manifest.Write(image.Height);
		manifest.Write(',');
		manifest.Write(image.EncodedOffset);
		manifest.Write(',');
		manifest.Write(image.EncodedLength);
		manifest.Write(',');
		manifest.Write(image.ConsumedLength);
		manifest.Write(',');
		manifest.Write(image.TrailerLength);
		manifest.Write(',');
		manifest.Write(image.OpaquePixelCount);
		manifest.Write(',');
		manifest.Write(bounds.IsEmpty ? string.Empty : bounds.Left.ToString());
		manifest.Write(',');
		manifest.Write(bounds.IsEmpty ? string.Empty : bounds.Top.ToString());
		manifest.Write(',');
		manifest.Write(bounds.IsEmpty ? string.Empty : bounds.Right.ToString());
		manifest.Write(',');
		manifest.Write(bounds.IsEmpty ? string.Empty : bounds.Bottom.ToString());
		manifest.Write(',');
		manifest.WriteLine(fileName);

		hasTrailerBytes |= image.TrailerLength != 0;
	}

	Console.WriteLine($"Decoded {wac.Images.Count} screen(s) from {Path.GetFileName(inputPath)}.");
	Console.WriteLine($"Canvas: {WacFile.Width}x{WacFile.Height}");
	if (hasTrailerBytes)
	{
		Console.WriteLine("Some entries contain trailing footer bytes; see manifest.csv for per-entry lengths.");
	}
}

static void ExtractPac(string inputPath, string outputDirectory, RgbPalette? paletteOverride)
{
	var pac = PacFile.Load(inputPath);
	var stem = Path.GetFileNameWithoutExtension(inputPath);
	var fileName = $"{stem}.png";
	var pngPath = Path.Combine(outputDirectory, fileName);
	var manifestPath = Path.Combine(outputDirectory, "manifest.csv");
	var palette = paletteOverride ?? pac.Palette;

	PngWriter.WriteIndexedPreview(pngPath, pac.Width, pac.Height, pac.Pixels, pac.Alpha, palette);

	using var manifest = new StreamWriter(manifestPath, false);
	manifest.WriteLine("image,width,height,body_offset,body_length,compression,cmap_length,color_ranges,opaque_pixels,bounds_left,bounds_top,bounds_right,bounds_bottom,png");
	manifest.Write("screen");
	manifest.Write(',');
	manifest.Write(pac.Width);
	manifest.Write(',');
	manifest.Write(pac.Height);
	manifest.Write(',');
	manifest.Write(pac.BodyOffset);
	manifest.Write(',');
	manifest.Write(pac.BodyLength);
	manifest.Write(',');
	manifest.Write(pac.Compression);
	manifest.Write(',');
	manifest.Write(pac.ColorMapLength);
	manifest.Write(',');
	manifest.Write(pac.ColorRangeCount);
	manifest.Write(',');
	manifest.Write(pac.OpaquePixelCount);
	manifest.Write(',');
	manifest.Write(pac.Bounds.Left);
	manifest.Write(',');
	manifest.Write(pac.Bounds.Top);
	manifest.Write(',');
	manifest.Write(pac.Bounds.Right);
	manifest.Write(',');
	manifest.Write(pac.Bounds.Bottom);
	manifest.Write(',');
	manifest.WriteLine(fileName);

	Console.WriteLine($"Decoded 1 screen from {Path.GetFileName(inputPath)}.");
	Console.WriteLine($"Canvas: {pac.Width}x{pac.Height}");
}

static void ExtractTex(string inputPath, string outputDirectory, RgbPalette? palette)
{
	var tex = TexFile.Load(inputPath);
	var stem = Path.GetFileNameWithoutExtension(inputPath);
	var manifestPath = Path.Combine(outputDirectory, "manifest.csv");

	using var manifest = new StreamWriter(manifestPath, false);
	manifest.WriteLine("entry,width,height,encoded_offset,encoded_length,opaque_pixels,bounds_left,bounds_top,bounds_right,bounds_bottom,png");

	foreach (var texture in tex.Textures)
	{
		var fileName = $"{stem}_{texture.Index:D3}.png";
		var pngPath = Path.Combine(outputDirectory, fileName);
		PngWriter.WriteIndexedPreview(pngPath, texture.Width, texture.Height, texture.Pixels, texture.Alpha, palette);
		WriteManifestRow(manifest, texture.Index, texture.Width, texture.Height, texture.EncodedOffset, texture.EncodedLength, texture.OpaquePixelCount, texture.Bounds, fileName);
	}

	Console.WriteLine($"Decoded {tex.Textures.Count} texture(s) from {Path.GetFileName(inputPath)}.");
	Console.WriteLine($"Texture size: {TexFile.TextureWidth}x{TexFile.TextureHeight}");
}

static void WriteManifestRow(StreamWriter manifest, int index, int width, int height, int encodedOffset, int encodedLength,
	int opaquePixelCount, PixelBounds bounds, string fileName)
{
	manifest.Write(index);
	manifest.Write(',');
	manifest.Write(width);
	manifest.Write(',');
	manifest.Write(height);
	manifest.Write(',');
	manifest.Write(encodedOffset);
	manifest.Write(',');
	manifest.Write(encodedLength);
	manifest.Write(',');
	manifest.Write(opaquePixelCount);
	manifest.Write(',');
	manifest.Write(bounds.IsEmpty ? string.Empty : bounds.Left.ToString());
	manifest.Write(',');
	manifest.Write(bounds.IsEmpty ? string.Empty : bounds.Top.ToString());
	manifest.Write(',');
	manifest.Write(bounds.IsEmpty ? string.Empty : bounds.Right.ToString());
	manifest.Write(',');
	manifest.Write(bounds.IsEmpty ? string.Empty : bounds.Bottom.ToString());
	manifest.Write(',');
	manifest.WriteLine(fileName);
}

static string GetDefaultOutputSuffix(string inputExtension)
{
	return inputExtension switch
	{
		".lif" => "_lif",
		".bif" => "_bif",
		".wsp" => "_wsp",
		".wac" => "_wac",
		".pac" => "_pac",
		".tex" => "_tex",
		_ => "_out"
	};
}

static RgbPalette? LoadPalette(string? explicitPalettePath, int paletteOffset, string inputPath, out string? description)
{
	description = null;
	var palettePath = explicitPalettePath;
	var autoDetected = false;

	if (palettePath is null)
	{
		var inputDirectory = Path.GetDirectoryName(inputPath) ?? Environment.CurrentDirectory;
		var candidatePath = Path.Combine(inputDirectory, "DUM_SINE.BIN");
		if (!File.Exists(candidatePath))
		{
			return null;
		}

		palettePath = candidatePath;
		autoDetected = true;
	}

	var fullPalettePath = Path.GetFullPath(palettePath);
	if (!File.Exists(fullPalettePath))
	{
		throw new FileNotFoundException($"Palette file not found: {fullPalettePath}");
	}

	description = $"{Path.GetFileName(fullPalettePath)} at 0x{paletteOffset:X}" + (autoDetected ? " (auto-detected)" : string.Empty);
	return RgbPalette.LoadRgb888(fullPalettePath, paletteOffset);
}

static RgbPalette? LoadExplicitPalette(string? explicitPalettePath, int paletteOffset, out string? description)
{
	description = null;
	if (explicitPalettePath is null)
	{
		return null;
	}

	var fullPalettePath = Path.GetFullPath(explicitPalettePath);
	if (!File.Exists(fullPalettePath))
	{
		throw new FileNotFoundException($"Palette file not found: {fullPalettePath}");
	}

	description = $"{Path.GetFileName(fullPalettePath)} at 0x{paletteOffset:X}";
	return RgbPalette.LoadRgb888(fullPalettePath, paletteOffset);
}

static CommandLineOptions ParseArguments(string[] args)
{
	if (args.Length == 0 || args[0] is "-h" or "--help" or "/?")
	{
		return CommandLineOptions.Help;
	}

	var positional = new List<string>();
	string? palettePath = null;
	var paletteOffset = RgbPalette.DefaultDumSineOffset;

	for (var index = 0; index < args.Length; index++)
	{
		var argument = args[index];
		switch (argument)
		{
			case "--palette" or "-p":
				if (++index >= args.Length)
				{
					return CommandLineOptions.Error("Missing value for --palette.");
				}

				palettePath = args[index];
				break;

			case "--palette-offset":
				if (++index >= args.Length)
				{
					return CommandLineOptions.Error("Missing value for --palette-offset.");
				}

				if (!TryParseOffset(args[index], out paletteOffset))
				{
					return CommandLineOptions.Error($"Invalid palette offset: {args[index]}");
				}

				break;

			default:
				if (argument.StartsWith('-'))
				{
					return CommandLineOptions.Error($"Unknown option: {argument}");
				}

				positional.Add(argument);
				break;
		}
	}

	return positional.Count switch
	{
		1 => new CommandLineOptions(positional[0], null, palettePath, paletteOffset, false, null),
		2 => new CommandLineOptions(positional[0], positional[1], palettePath, paletteOffset, false, null),
		_ => CommandLineOptions.Error("Expected one input file and at most one output directory.")
	};
}

static bool TryParseOffset(string value, out int offset)
{
	var style = NumberStyles.Integer;
	var digits = value;
	if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
	{
		style = NumberStyles.HexNumber;
		digits = value[2..];
	}

	return int.TryParse(digits, style, CultureInfo.InvariantCulture, out offset) && offset >= 0;
}

internal sealed record CommandLineOptions(
	string? InputPath,
	string? OutputDirectory,
	string? PalettePath,
	int PaletteOffset,
	bool ShowHelp,
	string? ErrorMessage)
{
	public static CommandLineOptions Help { get; } = new(null, null, null, RgbPalette.DefaultDumSineOffset, true, null);

	public static CommandLineOptions Error(string message)
	{
		return new CommandLineOptions(null, null, null, RgbPalette.DefaultDumSineOffset, false, message);
	}
}
