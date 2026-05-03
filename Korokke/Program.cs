using Korokke;

string outputRoot = Path.Combine(Environment.CurrentDirectory, "output");
Directory.CreateDirectory(outputRoot);

if (args.Length > 0)
{
	string assetPath = args[0];
	string assetOutputRoot = args.Length > 1
		? args[1]
		: Path.Combine(outputRoot, Path.GetFileNameWithoutExtension(assetPath));
	string? paletteTimPath = args.Length > 2 ? args[2] : null;
	int paletteGroupOffset = args.Length > 3 && int.TryParse(args[3], out int parsedPaletteGroupOffset)
		? parsedPaletteGroupOffset
		: 0;

	KorokkeExtractor.Extract(assetPath, assetOutputRoot, paletteTimPath, paletteGroupOffset);
	return;
}

var bSelectFile = @"C:\Dev\Gaming\Sony\PSX\Games\KOROKKE\BIND_output\BSELECT.ANM";
var continueFile = @"C:\Dev\Gaming\Sony\PSX\Games\KOROKKE\BIND_output\CONTINUE.ANN";
var iCharFile = @"C:\Dev\Gaming\Sony\PSX\Games\KOROKKE\BIND_output\ICHARA00.ANC";

string[] sampleFiles =
[
	bSelectFile,
	continueFile,
	iCharFile
];

foreach (string sampleFile in sampleFiles)
{
	string sampleOutputRoot = Path.Combine(outputRoot, Path.GetFileNameWithoutExtension(sampleFile));
	KorokkeExtractor.Extract(sampleFile, sampleOutputRoot);
}
