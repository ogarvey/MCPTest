using DogKnife.Helpers;
using DogKnife.Models;

namespace DogKnife;

internal static class Program
{
	private static int Main(string[] args)
	{
		try
		{
			AppOptions options = AppOptions.Parse(args);

			if (options.ShowHelp)
			{
				PrintUsage();
				return 0;
			}

			if (!string.IsNullOrWhiteSpace(options.DatPath))
			{
				string datPath = ResolveExistingFilePath(options.DatPath, ".dat");
				CatGunDat dat = CatGunDat.Load(datPath);
				PrintDatSummary(dat);
				return 0;
			}

			if (!string.IsNullOrWhiteSpace(options.TextureExportPath))
			{
				string datPath = ResolveExistingFilePath(options.TextureExportPath, ".dat");
				CatGunDat dat = CatGunDat.Load(datPath);
				string exportOutputPath = ResolveDatExportPath(datPath, options.OutputPath);
				TextureExportResult result = TextureExporter.Export(dat, exportOutputPath);
				Console.WriteLine($"Exported TEXTURE probe assets to: {result.OutputDirectory}");
				Console.WriteLine($"Blocks: {result.BlockCount}");
				Console.WriteLine($"Frames: {result.FrameCount}");
				Console.WriteLine($"Palette banks: {result.PaletteBankCount}");
				Console.WriteLine($"Probe palette bank: {(result.ProbePaletteBank is null ? "<none>" : result.ProbePaletteBank.Value.ToString())}");
				Console.WriteLine($"Default palette: {result.DefaultPaletteSummary}");
				Console.WriteLine($"Exported palette variants: {(result.ExportedPaletteVariants.Count == 0 ? "<none>" : string.Join(", ", result.ExportedPaletteVariants))}");
				return 0;
			}

			if (!string.IsNullOrWhiteSpace(options.Type1RenderExportPath))
			{
				string datPath = ResolveExistingFilePath(options.Type1RenderExportPath, ".dat");
				if (string.IsNullOrWhiteSpace(options.ResourceName))
				{
					throw new ArgumentException("--export-type1-renders requires --resource <name>.");
				}

				CatGunDat dat = CatGunDat.Load(datPath);
				string exportOutputPath = ResolveDatExportPath(datPath, options.OutputPath);
				Type1RenderedExportResult result = Type1RenderedExporter.Export(dat, options.ResourceName, exportOutputPath);
				Console.WriteLine($"Exported {result.ResourceName} exact type-1 renders to: {result.OutputDirectory}");
				Console.WriteLine($"Blocks: {result.RenderedBlockCount}/{result.BlockCount}");
				Console.WriteLine($"Frames: {result.FrameCount}");
				Console.WriteLine($"Skipped frames: {result.SkippedFrameCount}");
				Console.WriteLine($"Default palette: {result.DefaultPaletteSummary}");
				Console.WriteLine($"Exported palette variants: {(result.ExportedPaletteVariants.Count == 0 ? "<none>" : string.Join(", ", result.ExportedPaletteVariants))}");
				return 0;
			}

			if (!string.IsNullOrWhiteSpace(options.Type3CompositeExportPath))
			{
				string datPath = ResolveExistingFilePath(options.Type3CompositeExportPath, ".dat");
				if (string.IsNullOrWhiteSpace(options.ResourceName))
				{
					throw new ArgumentException("--export-type3-composites requires --resource <name>.");
				}

				CatGunDat dat = CatGunDat.Load(datPath);
				string exportOutputPath = ResolveDatExportPath(datPath, options.OutputPath);
				Type3CompositeExportResult result = Type3CompositeExporter.Export(dat, options.ResourceName, exportOutputPath);
				Console.WriteLine($"Exported {result.ResourceName} exact type-3 composites to: {result.OutputDirectory}");
				Console.WriteLine($"Base blocks: {result.BaseBlockCount}");
				Console.WriteLine($"Phase entries: {result.PhaseCount}");
				Console.WriteLine($"Composites: {result.CompositeCount}");
				Console.WriteLine($"FUN_00038130 special variants: {result.SpecialVariantCount}");
				Console.WriteLine($"FUN_00038130 cycle variants: {result.CycleVariantCount}");
				Console.WriteLine($"Default palette: {result.DefaultPaletteSummary}");
				Console.WriteLine($"Exported palette variants: {(result.ExportedPaletteVariants.Count == 0 ? "<none>" : string.Join(", ", result.ExportedPaletteVariants))}");
				return 0;
			}

			if (!string.IsNullOrWhiteSpace(options.DisplayType3ExportPath))
			{
				string datPath = ResolveExistingFilePath(options.DisplayType3ExportPath, ".dat");
				CatGunDat dat = CatGunDat.Load(datPath);
				string exportOutputPath = ResolveDatExportPath(datPath, options.OutputPath);
				DisplayType3RuntimeExportResult result = DisplayType3RuntimeExporter.Export(dat, exportOutputPath);
				Console.WriteLine($"Exported {result.ResourceName} exact DISPLAY type-3 runtime variants to: {result.OutputDirectory}");
				Console.WriteLine($"Block-48 width variants: {result.GaugeWidthVariantCount}");
				Console.WriteLine($"Block-35 filled-slot prefix variants: {result.Block35PrefixVariantCount}");
				Console.WriteLine($"Documented-but-unrendered block count: {result.DocumentedOnlyBlockCount}");
				Console.WriteLine($"Default palette: {result.DefaultPaletteSummary}");
				Console.WriteLine($"Exported palette variants: {(result.ExportedPaletteVariants.Count == 0 ? "<none>" : string.Join(", ", result.ExportedPaletteVariants))}");
				return 0;
			}

			if (!string.IsNullOrWhiteSpace(options.KnownRenderExportPath))
			{
				string datPath = ResolveExistingFilePath(options.KnownRenderExportPath, ".dat");
				CatGunDat dat = CatGunDat.Load(datPath);
				string exportOutputPath = ResolveDatExportPath(datPath, options.OutputPath);
				KnownRenderExportResult result = KnownRenderExporter.Export(dat, exportOutputPath);
				Console.WriteLine($"Exported currently exact renderable resources for: {datPath}");
				Console.WriteLine($"Resources: {result.ResourceCount}");
				Console.WriteLine($"With exports: {result.ExportedResourceCount}");
				Console.WriteLine($"With unresolved loader families: {result.UnresolvedResourceCount}");
				Console.WriteLine($"Exporter failures: {result.FailureCount}");
				Console.WriteLine($"Summary: {result.SummaryPath}");
				return 0;
			}

			if (!string.IsNullOrWhiteSpace(options.ResourceCoverageReportPath))
			{
				string datPath = ResolveExistingFilePath(options.ResourceCoverageReportPath, ".dat");
				CatGunDat dat = CatGunDat.Load(datPath);
				string exportOutputPath = ResolveDatExportPath(datPath, options.OutputPath);
				ResourceCoverageReportResult result = ResourceCoverageReporter.Export(dat, exportOutputPath);
				Console.WriteLine($"Reported resource coverage for: {datPath}");
				Console.WriteLine($"Resources: {result.ResourceCount}");
				Console.WriteLine($"Runtime-referenced resources: {result.RuntimeReferencedResourceCount}");
				Console.WriteLine($"Runtime gaps: {result.PriorityGapCount}");
				Console.WriteLine($"Present loader types: {result.PresentLoaderTypes}");
				Console.WriteLine($"Unhandled loader types: {result.UnhandledLoaderTypes}");
				Console.WriteLine($"Report: {result.ReportPath}");
				return 0;
			}

			if (!string.IsNullOrWhiteSpace(options.Type1ProbeExportPath))
			{
				string datPath = ResolveExistingFilePath(options.Type1ProbeExportPath, ".dat");
				if (string.IsNullOrWhiteSpace(options.ResourceName))
				{
					throw new ArgumentException("--inspect-type1-probes requires --resource <name>.");
				}

				CatGunDat dat = CatGunDat.Load(datPath);
				string exportOutputPath = ResolveDatExportPath(datPath, options.OutputPath);
				Type1PayloadProbeExportResult result = Type1PayloadProbeExporter.Export(dat, options.ResourceName, exportOutputPath);
				Console.WriteLine($"Exported {result.ResourceName} type-1 probe metadata to: {result.OutputDirectory}");
				Console.WriteLine($"Blocks: {result.BlockCount}");
				Console.WriteLine($"Known families: {result.KnownFamilyCount}");
				return 0;
			}

			if (!string.IsNullOrWhiteSpace(options.Type3ProbeExportPath))
			{
				string datPath = ResolveExistingFilePath(options.Type3ProbeExportPath, ".dat");
				if (string.IsNullOrWhiteSpace(options.ResourceName))
				{
					throw new ArgumentException("--export-type3-probes requires --resource <name>.");
				}

				CatGunDat dat = CatGunDat.Load(datPath);
				string exportOutputPath = ResolveDatExportPath(datPath, options.OutputPath);
				Type3RemapProbeExportResult result = Type3RemapProbeExporter.Export(dat, options.ResourceName, exportOutputPath);
				Console.WriteLine($"Exported {result.ResourceName} type-3 probe assets to: {result.OutputDirectory}");
				Console.WriteLine($"Blocks: {result.BlockCount}");
				Console.WriteLine($"Lookup pages: {result.UniqueLookupPageCount}");
				return 0;
			}

			if (!string.IsNullOrWhiteSpace(options.Type7EffectExportPath))
			{
				string datPath = ResolveExistingFilePath(options.Type7EffectExportPath, ".dat");
				if (string.IsNullOrWhiteSpace(options.ResourceName))
				{
					throw new ArgumentException("--export-type7-effects requires --resource <name>.");
				}

				CatGunDat dat = CatGunDat.Load(datPath);
				string exportOutputPath = ResolveDatExportPath(datPath, options.OutputPath);
				Type7EffectExportResult result = Type7EffectExporter.Export(dat, options.ResourceName, exportOutputPath);
				Console.WriteLine($"Exported {result.ResourceName} exact type-7 effects to: {result.OutputDirectory}");
				Console.WriteLine($"Blocks: {result.BlockCount}");
				Console.WriteLine($"Frames: {result.FrameCount}");
				Console.WriteLine($"Default palette: {result.DefaultPaletteSummary}");
				Console.WriteLine($"Exported palette variants: {(result.ExportedPaletteVariants.Count == 0 ? "<none>" : string.Join(", ", result.ExportedPaletteVariants))}");
				return 0;
			}

			if (!string.IsNullOrWhiteSpace(options.RawPlaneExportPath))
			{
				string datPath = ResolveExistingFilePath(options.RawPlaneExportPath, ".dat");
				if (string.IsNullOrWhiteSpace(options.ResourceName))
				{
					throw new ArgumentException("--export-resource-planes requires --resource <name>.");
				}

				CatGunDat dat = CatGunDat.Load(datPath);
				string exportOutputPath = ResolveDatExportPath(datPath, options.OutputPath);
				RawPlaneResourceExportResult result = RawPlaneResourceExporter.Export(dat, options.ResourceName, exportOutputPath);
				Console.WriteLine($"Exported {result.ResourceName} exact type-4 planes to: {result.OutputDirectory}");
				Console.WriteLine($"Blocks: {result.BlockCount}");
				Console.WriteLine($"Frames: {result.FrameCount}");
				Console.WriteLine($"Skipped frames: {result.SkippedFrameCount}");
				Console.WriteLine($"Palette banks: {result.PaletteBankCount}");
				Console.WriteLine($"Probe palette bank: {(result.ProbePaletteBank is null ? "<none>" : result.ProbePaletteBank.Value.ToString())}");
				Console.WriteLine($"Default palette: {result.DefaultPaletteSummary}");
				Console.WriteLine($"Exported palette variants: {(result.ExportedPaletteVariants.Count == 0 ? "<none>" : string.Join(", ", result.ExportedPaletteVariants))}");
				return 0;
			}

			string inputPath = ResolveInputPath(options.InputPath);
			string outputPath = ResolveOutputPath(inputPath, options.OutputPath);

			BlkArchive archive = BlkArchive.Load(inputPath);

			Console.WriteLine($"Input : {inputPath}");
			Console.WriteLine($"Output: {outputPath}");
			Console.WriteLine($"Entries: {archive.EntryCount}");
			Console.WriteLine($"First data offset: 0x{archive.FirstDataOffset:X}");

			if (options.ListOnly)
			{
				foreach (var entry in archive.Entries)
				{
					Console.WriteLine($"{entry.ArchivePath} | offset=0x{entry.Offset:X} | size=0x{entry.Size:X}");
				}

				return 0;
			}

			archive.ExtractAll(outputPath);

			Console.WriteLine($"Extracted {archive.EntryCount} entries.");
			return 0;
		}
		catch (Exception exception)
		{
			Console.Error.WriteLine(exception.Message);
			Console.Error.WriteLine();
			PrintUsage();
			return 1;
		}
	}

	private static string ResolveInputPath(string? requestedPath)
	{
		if (!string.IsNullOrWhiteSpace(requestedPath))
		{
			return ResolveExistingFilePath(requestedPath, ".blk");
		}

		foreach (string root in EnumerateSearchRoots())
		{
			string candidate = Path.Combine(root, "Samples", "CATGUN.BLK");
			if (File.Exists(candidate))
			{
				return Path.GetFullPath(candidate);
			}
		}

		throw new FileNotFoundException("Unable to locate Samples/CATGUN.BLK. Pass --input <path> explicitly.");
	}

	private static string ResolveExistingFilePath(string requestedPath, string expectedExtension)
	{
		string fullPath = Path.GetFullPath(requestedPath);

		if (!File.Exists(fullPath))
		{
			throw new FileNotFoundException($"Input file was not found: {fullPath}");
		}

		if (!string.Equals(Path.GetExtension(fullPath), expectedExtension, StringComparison.OrdinalIgnoreCase))
		{
			throw new ArgumentException($"Expected a {expectedExtension} file: {fullPath}");
		}

		return fullPath;
	}

	private static string ResolveOutputPath(string inputPath, string? requestedPath)
	{
		if (!string.IsNullOrWhiteSpace(requestedPath))
		{
			return Path.GetFullPath(requestedPath);
		}

		string samplesDirectory = Path.GetDirectoryName(inputPath)
			?? throw new InvalidOperationException("Input path does not have a parent directory.");
		string catGunRoot = Directory.GetParent(samplesDirectory)?.FullName
			?? throw new InvalidOperationException("Unable to determine the CatGun root directory.");

		return Path.GetFullPath(Path.Combine(catGunRoot, "TestOutput", "BLK_Raw"));
	}

	private static string ResolveDatExportPath(string datPath, string? requestedPath)
	{
		if (!string.IsNullOrWhiteSpace(requestedPath))
		{
			return Path.GetFullPath(requestedPath);
		}

		DirectoryInfo? current = new(Path.GetDirectoryName(datPath)
			?? throw new InvalidOperationException("DAT path does not have a parent directory."));

		while (current is not null)
		{
			if (string.Equals(current.Name, "CatGun", StringComparison.OrdinalIgnoreCase))
			{
				return Path.Combine(
					current.FullName,
					"TestOutput",
					"DatExports",
					Path.GetFileNameWithoutExtension(datPath));
			}

			current = current.Parent;
		}

		return Path.Combine(
			Path.GetDirectoryName(datPath)!,
			$"{Path.GetFileNameWithoutExtension(datPath)}_Export");
	}

	private static IEnumerable<string> EnumerateSearchRoots()
	{
		HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

		foreach (string start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
		{
			string? current = Path.GetFullPath(start);

			while (!string.IsNullOrWhiteSpace(current) && seen.Add(current))
			{
				yield return current;
				current = Directory.GetParent(current)?.FullName;
			}
		}
	}

	private static void PrintUsage()
	{
		Console.WriteLine("DogKnife - CatGun BLK extractor");
		Console.WriteLine("Usage:");
		Console.WriteLine("  dotnet run -- [input.blk] [outputDir]");
		Console.WriteLine("  dotnet run -- --input <path> --output <dir>");
		Console.WriteLine("  dotnet run -- --list [--input <path>]");
		Console.WriteLine("  dotnet run -- --dump-dat <path-to-raw-dat>");
		Console.WriteLine("  dotnet run -- --export-textures <path-to-raw-dat> [--output <dir>]");
		Console.WriteLine("  dotnet run -- --export-type1-renders <path-to-raw-dat> --resource <name> [--output <dir>] (renders exact PatternWriter and 0x4D4DC helper families to PNGs)");
		Console.WriteLine("  dotnet run -- --export-type3-composites <path-to-raw-dat> --resource <name> [--output <dir>] (exports exact type-3 composite matrices for PLAYER via FUN_00038130 and RX7_FRAMES via its same-resource base-plus-remap family)");
		Console.WriteLine("  dotnet run -- --export-display-type3 <path-to-raw-dat> [--output <dir>] (exports exact DISPLAY block-48 runtime gauge widths plus block-35's exact filled-slot prefix, and documents the remaining substrate-dependent remap)");
		Console.WriteLine("  dotnet run -- --export-known-renders <path-to-raw-dat> [--output <dir>] (runs the currently exact resource renderers across the whole DAT and writes a coverage summary)");
		Console.WriteLine("  dotnet run -- --report-resource-coverage <path-to-raw-dat> [--output <dir>] (reports per-resource loader types, DAT layer references, EXE-proven named lookups, and current exact exporter coverage)");
		Console.WriteLine("  dotnet run -- --inspect-type1-probes <path-to-raw-dat> --resource <name> [--output <dir>] (classifies loader-patched type-1 payload families and parses outer streams)");
		Console.WriteLine("  dotnet run -- --export-type3-probes <path-to-raw-dat> --resource <name> [--output <dir>] (exports coverage masks and lookup pages for the shared type-3 remap family)");
		Console.WriteLine("  dotnet run -- --export-type7-effects <path-to-raw-dat> --resource <name> [--output <dir>] (exports REACTOR's exact first 256-frame FUN_0002A0CA cycle or LEADER's exact FUN_00054FA0()/FUN_00054EB0() script render)");
		Console.WriteLine("  dotnet run -- --export-resource-planes <path-to-raw-dat> --resource <name> [--output <dir>] (exports exact loader type-4 indexed planes for validated resources such as DISPLAY and LEVINFO0/1/2)");
	}

	private static void PrintDatSummary(CatGunDat dat)
	{
		Console.WriteLine($"DAT: {dat.FilePath}");
		Console.WriteLine($"Type: 0x{dat.Header.Type:X2}");
		Console.WriteLine($"Variant: 0x{dat.Header.Variant:X2}");
		Console.WriteLine($"Header dword @0x04: 0x{dat.Header.Value04:X8}");
		Console.WriteLine($"Header bytes @0x08-0x0C: {dat.Header.Byte08}, {dat.Header.Byte09}, {dat.Header.Byte0A}, {dat.Header.Byte0B}, {dat.Header.Byte0C}");
		Console.WriteLine($"Table40 entry count : {dat.Header.Table40EntryCount}");
		Console.WriteLine($"Table40 table    @0x40: 0x{dat.Header.Table40Offset:X} ({dat.Table40Blocks.Count} blocks)");
		Console.WriteLine($"Resource entry count: {dat.Header.ResourceEntryCount}");
		Console.WriteLine($"Table64 entry count : {dat.Header.Table64EntryCount}");
		Console.WriteLine($"Cell ref entries    : {dat.CellReferences.Count} (+{dat.CellReferencePaddingByteCount} trailing bytes)");
		Console.WriteLine($"Cell ref table  @0x1C: 0x{dat.Header.CellReferenceTableOffset:X}");
		Console.WriteLine($"Resource table  @0x4C: 0x{dat.Header.ResourceTableOffset:X}");
		Console.WriteLine($"Patch table     @0x50: 0x{dat.Header.PatchTableOffset:X}");
		Console.WriteLine($"Palette table   @0x54: 0x{dat.Header.PaletteTableOffset:X}");
		Console.WriteLine($"Layer table     @0x58: 0x{dat.Header.LayerTableOffset:X}");
		Console.WriteLine();
		Console.WriteLine("Layers:");

		foreach (DatLayer layer in dat.Layers)
		{
			Console.WriteLine(
				$"Layer[{layer.Index}] desc=0x{layer.DescriptorOffset:X} cells=0x{layer.CellDataOffset:X} size={layer.Width}x{layer.Height} nonZero={layer.NonZeroCellCount} maxRef={layer.MaxReferenceIndex}");
		}

		Console.WriteLine();
		Console.WriteLine("First cell references:");

		foreach (DatCellReferenceEntry cellReference in dat.CellReferences.Take(12))
		{
			string resourceName = cellReference.ResourceName ?? "<out-of-range>";
			Console.WriteLine(
				$"[{cellReference.Index:D3}] @0x{cellReference.EntryOffset:X} raw={cellReference.Value00:X4}-{cellReference.Byte02:X2}-{cellReference.Byte03:X2}-{cellReference.Byte04:X2}-{cellReference.ResourceIndex:X2}-{cellReference.Byte06:X2} resource={resourceName}");
		}

		Console.WriteLine();
		Console.WriteLine("Resource entries:");

		foreach (DatResourceEntry resource in dat.Resources)
		{
			Console.WriteLine(
				$"[{resource.Index:D3}] {resource.Name} | entry=0x{resource.EntryOffset:X} | p04=0x{resource.Pointer04:X} | p08=0x{resource.Pointer08:X} | p0C=0x{resource.Pointer0C:X} | p14=0x{resource.Pointer14:X} | p18=0x{resource.Pointer18:X}");
		}

		Console.WriteLine();
		Console.WriteLine("Sequence groups from resource p08:");

		foreach (DatSequenceGroup sequenceGroup in dat.SequenceGroups)
		{
			string names = string.Join(", ", sequenceGroup.ResourceNames);
			Console.WriteLine(
				$"@0x{sequenceGroup.StartOffset:X}..0x{sequenceGroup.EndOffset:X} bytes=0x{sequenceGroup.ByteCount:X} trailing={sequenceGroup.TrailingByteCount} delimiters={sequenceGroup.DelimiterCount} segments={sequenceGroup.Segments.Count} resources={names} raw={FormatByteSequence(sequenceGroup.RawBytes)}");

			foreach (DatSequenceSegment segment in sequenceGroup.Segments)
			{
				Console.WriteLine(
					$"  seg[{segment.Index}] @0x{segment.StartOffset:X} len=0x{segment.ByteCount:X} bytes={FormatByteSequence(segment.Bytes)}");
			}
		}

		Console.WriteLine();
		Console.WriteLine("Payload groups from resource p04:");

		foreach (DatPayloadGroup payloadGroup in dat.PayloadGroups)
		{
			string names = string.Join(", ", payloadGroup.ResourceNames);
			string loaderTypes = FormatLoaderTypeDistribution(payloadGroup.Blocks);
			Console.WriteLine(
				$"@0x{payloadGroup.StartOffset:X}..0x{payloadGroup.EndOffset:X} bytes=0x{payloadGroup.ByteCount:X} blocks={payloadGroup.Blocks.Count} trailing={payloadGroup.TrailingByteCount} types={loaderTypes} resources={names}");

			if (payloadGroup.Blocks.Count > 0)
			{
				DatPayloadBlock30 firstBlock = payloadGroup.Blocks[0];
				Console.WriteLine(
					$"  block0: type=0x{firstBlock.LoaderType:X2} 00={firstBlock.Value00:X8} 04={firstBlock.Value04:X8} 08={firstBlock.Value08:X8} 0C={firstBlock.Value0C:X8} 10={firstBlock.Value10:X8} 14={firstBlock.Value14:X8} 18={firstBlock.Value18:X8} 1C={firstBlock.Value1C:X8} 20={firstBlock.Value20:X8} 24={firstBlock.Value24:X8} 28={firstBlock.Value28:X8} 2C={firstBlock.Value2C:X8}");
			}
		}
	}

	private static string FormatLoaderTypeDistribution(IEnumerable<DatPayloadBlock30> blocks)
	{
		return string.Join(
			", ",
			blocks
				.GroupBy(block => block.LoaderType)
				.OrderBy(group => group.Key)
				.Select(group => $"0x{group.Key:X2}:{group.Count()}"));
	}

	private static string FormatByteSequence(IEnumerable<byte> bytes)
	{
		return string.Join(" ", bytes.Select(value => value.ToString("X2")));
	}

	private sealed record AppOptions(string? InputPath, string? OutputPath, string? DatPath, string? TextureExportPath, string? Type1RenderExportPath, string? Type3CompositeExportPath, string? DisplayType3ExportPath, string? KnownRenderExportPath, string? ResourceCoverageReportPath, string? Type1ProbeExportPath, string? Type3ProbeExportPath, string? Type7EffectExportPath, string? RawPlaneExportPath, string? ResourceName, bool ListOnly, bool ShowHelp)
	{
		public static AppOptions Parse(string[] args)
		{
			string? inputPath = null;
			string? outputPath = null;
			string? datPath = null;
			string? textureExportPath = null;
			string? type1RenderExportPath = null;
			string? type3CompositeExportPath = null;
			string? displayType3ExportPath = null;
			string? knownRenderExportPath = null;
			string? resourceCoverageReportPath = null;
			string? type1ProbeExportPath = null;
			string? type3ProbeExportPath = null;
			string? type7EffectExportPath = null;
			string? rawPlaneExportPath = null;
			string? resourceName = null;
			bool listOnly = false;
			bool showHelp = false;

			for (int index = 0; index < args.Length; index++)
			{
				string argument = args[index];

				switch (argument)
				{
					case "--help":
					case "-h":
					case "/?":
						showHelp = true;
						break;

					case "--list":
						listOnly = true;
						break;

					case "--dump-dat":
						datPath = ReadValue(args, ref index, argument);
						break;

					case "--export-textures":
						textureExportPath = ReadValue(args, ref index, argument);
						break;

					case "--export-type1-renders":
						type1RenderExportPath = ReadValue(args, ref index, argument);
						break;

					case "--export-type3-composites":
						type3CompositeExportPath = ReadValue(args, ref index, argument);
						break;

					case "--export-display-type3":
						displayType3ExportPath = ReadValue(args, ref index, argument);
						break;

					case "--export-known-renders":
						knownRenderExportPath = ReadValue(args, ref index, argument);
						break;

					case "--report-resource-coverage":
						resourceCoverageReportPath = ReadValue(args, ref index, argument);
						break;

					case "--inspect-type1-probes":
						type1ProbeExportPath = ReadValue(args, ref index, argument);
						break;

					case "--export-type3-probes":
						type3ProbeExportPath = ReadValue(args, ref index, argument);
						break;

					case "--export-type7-effects":
						type7EffectExportPath = ReadValue(args, ref index, argument);
						break;

					case "--export-resource-planes":
						rawPlaneExportPath = ReadValue(args, ref index, argument);
						break;

					case "--resource":
						resourceName = ReadValue(args, ref index, argument);
						break;

					case "--input":
					case "-i":
						inputPath = ReadValue(args, ref index, argument);
						break;

					case "--output":
					case "-o":
						outputPath = ReadValue(args, ref index, argument);
						break;

					default:
						if (argument.StartsWith("-", StringComparison.Ordinal))
						{
							throw new ArgumentException($"Unknown argument: {argument}");
						}

						if (string.IsNullOrWhiteSpace(inputPath))
						{
							inputPath = argument;
						}
						else if (string.IsNullOrWhiteSpace(outputPath))
						{
							outputPath = argument;
						}
						else
						{
							throw new ArgumentException($"Unexpected argument: {argument}");
						}

						break;
				}
			}

			int activeDatActions = 0;
			if (!string.IsNullOrWhiteSpace(datPath))
			{
				activeDatActions++;
			}

			if (!string.IsNullOrWhiteSpace(textureExportPath))
			{
				activeDatActions++;
			}

			if (!string.IsNullOrWhiteSpace(type1RenderExportPath))
			{
				activeDatActions++;
			}

			if (!string.IsNullOrWhiteSpace(type3CompositeExportPath))
			{
				activeDatActions++;
			}

			if (!string.IsNullOrWhiteSpace(displayType3ExportPath))
			{
				activeDatActions++;
			}

			if (!string.IsNullOrWhiteSpace(knownRenderExportPath))
			{
				activeDatActions++;
			}

			if (!string.IsNullOrWhiteSpace(resourceCoverageReportPath))
			{
				activeDatActions++;
			}

			if (!string.IsNullOrWhiteSpace(type1ProbeExportPath))
			{
				activeDatActions++;
			}

			if (!string.IsNullOrWhiteSpace(type3ProbeExportPath))
			{
				activeDatActions++;
			}

			if (!string.IsNullOrWhiteSpace(type7EffectExportPath))
			{
				activeDatActions++;
			}

			if (!string.IsNullOrWhiteSpace(rawPlaneExportPath))
			{
				activeDatActions++;
			}

			if (1 < activeDatActions)
			{
				throw new ArgumentException("Use only one of --dump-dat, --export-textures, --export-type1-renders, --export-type3-composites, --export-display-type3, --export-known-renders, --report-resource-coverage, --inspect-type1-probes, --export-type3-probes, --export-type7-effects, or --export-resource-planes at a time.");
			}

			if (string.IsNullOrWhiteSpace(rawPlaneExportPath) && string.IsNullOrWhiteSpace(type1RenderExportPath) && string.IsNullOrWhiteSpace(type3CompositeExportPath) && string.IsNullOrWhiteSpace(type1ProbeExportPath) && string.IsNullOrWhiteSpace(type3ProbeExportPath) && string.IsNullOrWhiteSpace(type7EffectExportPath) && !string.IsNullOrWhiteSpace(resourceName))
			{
				throw new ArgumentException("--resource is only valid with --export-type1-renders, --export-type3-composites, --inspect-type1-probes, --export-type3-probes, --export-type7-effects, or --export-resource-planes.");
			}

			return new AppOptions(inputPath, outputPath, datPath, textureExportPath, type1RenderExportPath, type3CompositeExportPath, displayType3ExportPath, knownRenderExportPath, resourceCoverageReportPath, type1ProbeExportPath, type3ProbeExportPath, type7EffectExportPath, rawPlaneExportPath, resourceName, listOnly, showHelp);
		}

		private static string ReadValue(string[] args, ref int index, string optionName)
		{
			if (index + 1 >= args.Length)
			{
				throw new ArgumentException($"Missing value for {optionName}");
			}

			index++;
			return args[index];
		}
	}
}
