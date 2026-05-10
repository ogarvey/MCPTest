using DogKnife.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DogKnife.Helpers;

internal static class DatPaletteHelper
{
	private const int PaletteColorCount = 256;
	private const int PaletteBankSize = PaletteColorCount * 3;
	private const int PaletteGridSide = 16;
	private const int PaletteCellSize = 16;
	public const string DefaultDirectoryName = "default";
	private const string PaletteBanksDirectoryName = "palette_banks";
	private const string OriginalVga6BitDirectoryName = "original_vga6bit";
	private const string Converted8BitDirectoryName = "converted_8bit";

	public static bool TryCreateContext(
		CatGunDat dat,
		out DatPaletteContext? context,
		out string? failureReason,
		IEnumerable<ExportPaletteVariant>? additionalVariants = null)
	{
		int paletteRegionLength = dat.Header.LayerTableOffset - dat.Header.PaletteTableOffset;
		if (paletteRegionLength <= 0 || paletteRegionLength % PaletteBankSize != 0)
		{
			context = null;
			failureReason =
				$"Palette region length 0x{paletteRegionLength:X} is not divisible by 0x{PaletteBankSize:X}.";
			return false;
		}

		Rgba32[][] banks = ParsePaletteBanks(dat.RawBytes.Span, dat.Header.PaletteTableOffset, paletteRegionLength);
		byte[][] rawBanks = ParseRawPaletteBanks(dat.RawBytes.Span, dat.Header.PaletteTableOffset, paletteRegionLength);
		List<ExportPaletteVariant> variants = BuildVariants(dat, banks.Length, additionalVariants);
		SceneDefaultPalette defaultPalette = BuildDefaultPalette(dat, banks);
		context = new DatPaletteContext(banks, rawBanks, variants, defaultPalette.Palette, defaultPalette.Summary);
		failureReason = null;
		return true;
	}

	public static void ExportPaletteBankImages(string outputRoot, DatPaletteContext paletteContext)
	{
		string paletteBanksRoot = Path.Combine(outputRoot, PaletteBanksDirectoryName);
		string originalVgaDirectory = Path.Combine(paletteBanksRoot, OriginalVga6BitDirectoryName);
		string convertedDirectory = Path.Combine(paletteBanksRoot, Converted8BitDirectoryName);
		Directory.CreateDirectory(originalVgaDirectory);
		Directory.CreateDirectory(convertedDirectory);

		for (int bankIndex = 0; bankIndex < paletteContext.PaletteBankCount; bankIndex++)
		{
			string originalPath = Path.Combine(originalVgaDirectory, $"bank_{bankIndex:D2}.png");
			string convertedPath = Path.Combine(convertedDirectory, $"bank_{bankIndex:D2}.png");
			SaveRawPaletteSheet(originalPath, paletteContext.RawBanks[bankIndex]);
			SaveConvertedPaletteSheet(convertedPath, paletteContext.Banks[bankIndex]);
		}

		string metadataPath = Path.Combine(paletteBanksRoot, "metadata.txt");
		List<string> lines =
		[
			$"Palette bank count: {paletteContext.PaletteBankCount}",
			$"Original VGA 6-bit previews: {OriginalVga6BitDirectoryName}",
			$"Expanded 8-bit previews: {Converted8BitDirectoryName}",
			string.Empty,
			"Banks:",
		];

		for (int bankIndex = 0; bankIndex < paletteContext.PaletteBankCount; bankIndex++)
		{
			lines.Add(
				$"[{bankIndex:D2}] raw={OriginalVga6BitDirectoryName}/bank_{bankIndex:D2}.png expanded={Converted8BitDirectoryName}/bank_{bankIndex:D2}.png");
		}

		File.WriteAllLines(metadataPath, lines);
	}

	public static void AppendMetadata(
		List<string> lines,
		CatGunDat dat,
		DatPaletteContext? paletteContext,
		string? paletteFailureReason)
	{
		lines.Add($"Header byte 0x0B (DAT_000655BB): {dat.Header.Byte0B}");
		lines.Add($"Header byte 0x0C (DAT_000655BE): {dat.Header.Byte0C}");

		if (paletteContext is null)
		{
			lines.Add("Palette banks: <unavailable>");
			lines.Add($"Palette export note: {paletteFailureReason ?? "<none>"}");
			return;
		}

		lines.Add($"Palette banks: {paletteContext.PaletteBankCount}");
		lines.Add($"Palette bank previews: {PaletteBanksDirectoryName}/{OriginalVga6BitDirectoryName}, {PaletteBanksDirectoryName}/{Converted8BitDirectoryName}");
		lines.Add("Palette selection scope: generic draw paths use the current scene-global live palette; no per-resource palette-bank field has been found in the queued draw records.");
		if (dat.Header.Type == 0x02)
		{
			lines.Add("Palette selection note: The intro path later calls FUN_0003DD90() with DAT_0006557C + 0x600, which is palette bank 2, for a full-bank transition after the generic startup reset.");
		}
		if (dat.Header.Type == 0x03)
		{
			lines.Add("Palette selection note: The high-score path later calls FUN_0003DD90(DAT_0006557C + DAT_000655BE * 0x300, 0, 0x20000), so the live palette is a full-bank transition from header byte 0x0C rather than a grayscale fallback.");
		}
		if (dat.Header.Type == 0x06)
		{
			lines.Add("Palette selection note: The charsel state range includes a later FUN_0003E080(DAT_0006557C, 0, ...) baseline copy from palette bank 0, so type 0x06 is not a grayscale-only path.");
			lines.Add("Palette selection note: Another recovered charsel FUN_0003E080() call then copies 0x50 entries starting at live index 0x20 from DAT_0006557C + bank * 0x300, where the recovered runtime bit currently selects bank 0 or 1.");
			lines.Add("Palette selection note: Default output therefore uses the proven bank-0 baseline, while the later 0x20..0x6F slice swap remains documented but not yet exported as an exact hybrid runtime palette.");
		}
		if (dat.Header.Type == 0x0D)
		{
			lines.Add("Palette selection note: The dynamic LAFONT mode path later conditionally calls FUN_0003B1A0() and also reaches FUN_0003E080(), so type 0x0D uses scripted/runtime palette changes beyond generic startup.");
			lines.Add("Palette selection note: The exact live palette source for type 0x0D is still unresolved, so default output remains a fallback rather than a claimed exact runtime palette.");
		}
		if (dat.Header.Type == 0x0B)
		{
			lines.Add("Palette selection note: Type 0x0B scenes start from a full bank-0 live palette transition in FUN_000110A0().");
			lines.Add("Palette selection note: Later runtime handlers then replace selected slices; FUN_00049640() rewrites indices 0x00..0x7F from DAT_000655BB and separately rewrites only the last 0x20 entries (0xE0..0xFF) via DAT_000655BE-related data.");
			lines.Add("Palette selection note: FUN_00049640() is gated by DAT_000602AA and only runs from FUN_0003E760(); DAT_000602AA is armed by FUN_0003B1A0(1), while the normal lv01s1 startup trace currently reaches FUN_0003B1A0(0) instead.");
			lines.Add("Palette selection note: Bank 0 is the proven scene-start baseline; the remaining unresolved mismatch is in later slice-specific updates, not a proven full-bank switch to the final palette bank.");
			if (dat.Header.Byte0C >= paletteContext.PaletteBankCount)
			{
				lines.Add($"Palette selection note: Header byte 0x0C value {dat.Header.Byte0C} exceeds the {paletteContext.PaletteBankCount} exported static banks, so it is not acting as a direct bank index in this DAT.");
			}
		}
		lines.Add($"Default export palette: {paletteContext.DefaultPaletteSummary}");
		lines.Add($"Exported palette variants: {FormatVariantSummary(paletteContext.Variants)}");

		if (paletteContext.Variants.Count == 0)
		{
			return;
		}

		lines.Add("Palette variant rationale:");
		foreach (ExportPaletteVariant variant in paletteContext.Variants)
		{
			lines.Add($"- {variant.DirectoryName}: {variant.Description}");
		}
	}

	public static string FormatVariantSummary(IReadOnlyList<ExportPaletteVariant> variants)
	{
		return variants.Count == 0
			? "<none>"
			: string.Join(", ", variants.Select(variant => $"{variant.DirectoryName}=bank{variant.BankIndex:D2}"));
	}

	public static void SaveIndexedImage(
		string outputPath,
		IReadOnlyDictionary<int, byte> pixels,
		int left,
		int top,
		int width,
		int height,
		int runtimeStride,
		Rgba32[]? palette)
	{
		using Image<Rgba32> bitmap = new(width, height);

		bitmap.ProcessPixelRows(accessor =>
		{
			for (int y = 0; y < height; y++)
			{
				Span<Rgba32> row = accessor.GetRowSpan(y);
				int sourceY = top + y;
				for (int x = 0; x < width; x++)
				{
					int sourceOffset = (sourceY * runtimeStride) + left + x;
					if (pixels.TryGetValue(sourceOffset, out byte value))
					{
						row[x] = palette is null
							? new Rgba32(value, value, value, 255)
							: palette[value];
					}
					else
					{
						row[x] = new Rgba32(0, 0, 0, 0);
					}
				}
			}
		});

		bitmap.SaveAsPng(outputPath);
	}

	private static Rgba32[][] ParsePaletteBanks(ReadOnlySpan<byte> bytes, int paletteTableOffset, int paletteRegionLength)
	{
		int paletteBankCount = paletteRegionLength / PaletteBankSize;
		Rgba32[][] banks = new Rgba32[paletteBankCount][];

		for (int bankIndex = 0; bankIndex < paletteBankCount; bankIndex++)
		{
			int bankOffset = paletteTableOffset + (bankIndex * PaletteBankSize);
			Rgba32[] colors = new Rgba32[PaletteColorCount];

			for (int colorIndex = 0; colorIndex < PaletteColorCount; colorIndex++)
			{
				int colorOffset = bankOffset + (colorIndex * 3);
				colors[colorIndex] = new Rgba32(
					ExpandVgaColor(bytes[colorOffset + 0]),
					ExpandVgaColor(bytes[colorOffset + 1]),
					ExpandVgaColor(bytes[colorOffset + 2]));
			}

			banks[bankIndex] = colors;
		}

		return banks;
	}

	private static byte[][] ParseRawPaletteBanks(ReadOnlySpan<byte> bytes, int paletteTableOffset, int paletteRegionLength)
	{
		int paletteBankCount = paletteRegionLength / PaletteBankSize;
		byte[][] banks = new byte[paletteBankCount][];

		for (int bankIndex = 0; bankIndex < paletteBankCount; bankIndex++)
		{
			int bankOffset = paletteTableOffset + (bankIndex * PaletteBankSize);
			banks[bankIndex] = bytes.Slice(bankOffset, PaletteBankSize).ToArray();
		}

		return banks;
	}

	private static List<ExportPaletteVariant> BuildVariants(
		CatGunDat dat,
		int paletteBankCount,
		IEnumerable<ExportPaletteVariant>? additionalVariants)
	{
		List<ExportPaletteVariant> variants = [];
		HashSet<int> seenBanks = [];

		void AddVariant(int bankIndex, string directoryName, string description)
		{
			if ((uint)bankIndex >= (uint)paletteBankCount || !seenBanks.Add(bankIndex))
			{
				return;
			}

			variants.Add(new ExportPaletteVariant(bankIndex, directoryName, description));
		}

		if (dat.Header.Type == 8)
		{
			AddVariant(
				0,
				"palette_bank_00_state8_start",
				"State 8 init calls FUN_0003DD90(DAT_0006557C, 0, 0x20000), which targets palette bank 0.");
		}

		if (dat.Header.Type == 0x02)
		{
			AddVariant(
				2,
				"palette_bank_02_intro_state",
				"The intro path later calls FUN_0003DD90() with DAT_0006557C + 0x600, which is a full-bank transition from palette bank 2.");
		}

		if (dat.Header.Type == 0x03)
		{
			AddVariant(
				dat.Header.Byte0C,
				$"palette_bank_{dat.Header.Byte0C:D2}_state03_header_0C",
				"The high-score path later calls FUN_0003DD90(DAT_0006557C + DAT_000655BE * 0x300, 0, 0x20000), so header byte 0x0C is the full-bank live-palette source.");
		}

		if (dat.Header.Type == 0x06)
		{
			AddVariant(
				0,
				"palette_bank_00_state06_start",
				"The charsel state range later calls FUN_0003E080(DAT_0006557C, 0, ...), which proves a bank-0 live-palette baseline before the later 0x20..0x6F slice swap.");
		}

		if (dat.Header.Type == 0x0B)
		{
			AddVariant(
				0,
				"palette_bank_00_state0B_start",
				"FUN_000110A0() uses lookup byte 0x01 for type 0x0B, which triggers the scene-start full live-palette transition from palette bank 0.");
			AddVariant(
				dat.Header.Byte0B,
				$"palette_bank_{dat.Header.Byte0B:D2}_header_0B_low_slice_reference",
				"DAT header byte 0x0B is loaded into DAT_000655BB by FUN_0002A630(), and FUN_00049640() uses it for the 0x00..0x7F live-palette slice only.");
			AddVariant(
				dat.Header.Byte0C,
				$"palette_bank_{dat.Header.Byte0C:D2}_header_0C_upper_input_reference",
				"DAT header byte 0x0C is loaded into DAT_000655BE by FUN_0002A630(), and FUN_00049640() uses it only as the source input for the final 0x20 live-palette entries (0xE0..0xFF), not as a proven full-bank runtime selection.");
		}
		else
		{
			AddVariant(
				dat.Header.Byte0B,
				$"palette_bank_{dat.Header.Byte0B:D2}_header_0B",
				"DAT header byte 0x0B is loaded into DAT_000655BB by FUN_0002A630().");
			AddVariant(
				dat.Header.Byte0C,
				$"palette_bank_{dat.Header.Byte0C:D2}_header_0C",
				"DAT header byte 0x0C is loaded into DAT_000655BE by FUN_0002A630().");
		}

		if (additionalVariants is not null)
		{
			foreach (ExportPaletteVariant variant in additionalVariants)
			{
				AddVariant(variant.BankIndex, variant.DirectoryName, variant.Description);
			}
		}

		return variants;
	}

	private static SceneDefaultPalette BuildDefaultPalette(CatGunDat dat, Rgba32[][] banks)
	{
		if (banks.Length == 0)
		{
			return new(
				Palette: null,
				Summary: $"{DefaultDirectoryName}/ falls back to grayscale; no palette banks were parsed.");
		}

		if (dat.Header.Type == 0x0B)
		{
			Rgba32[] palette = (Rgba32[])banks[0].Clone();
			List<string> parts = ["bank00 baseline"];

			if ((uint)dat.Header.Byte0B < (uint)banks.Length)
			{
				Array.Copy(banks[dat.Header.Byte0B], 0, palette, 0, 0x80);
				parts.Add($"0x00..0x7F from bank{dat.Header.Byte0B:D2}");
			}

			if ((uint)dat.Header.Byte0C < (uint)banks.Length)
			{
				Array.Copy(banks[dat.Header.Byte0C], 0xE0, palette, 0xE0, 0x20);
				parts.Add($"0xE0..0xFF from bank{dat.Header.Byte0C:D2}");
			}
			else
			{
				parts.Add($"0xE0..0xFF kept from bank00 because header byte 0x0C ({dat.Header.Byte0C}) is not a direct static bank index here");
			}

			return new(
				Palette: palette,
				Summary: $"{DefaultDirectoryName}/ uses best-effort type 0x0B live palette reconstruction: {string.Join(", ", parts)}.");
		}

		if (dat.Header.Type == 0x02)
		{
			if (2 < banks.Length)
			{
				return new(
					Palette: banks[2],
					Summary: $"{DefaultDirectoryName}/ uses proven intro-state bank02 from the later FUN_0003DD90(DAT_0006557C + 0x600, ...) full-bank transition.");
			}

			return new(
				Palette: null,
				Summary: $"{DefaultDirectoryName}/ falls back to grayscale; the intro-state path expects palette bank02, but this DAT only exposes {banks.Length} banks.");
		}

		if (dat.Header.Type == 0x03 && (uint)dat.Header.Byte0C < (uint)banks.Length)
		{
			return new(
				Palette: banks[dat.Header.Byte0C],
				Summary: $"{DefaultDirectoryName}/ uses the proven high-score full-bank transition source from header byte 0x0C (bank{dat.Header.Byte0C:D2}).");
		}

		if (dat.Header.Type == 0x06)
		{
			return new(
				Palette: banks[0],
				Summary: $"{DefaultDirectoryName}/ uses the proven charsel bank00 baseline; later runtime code can still replace live indices 0x20..0x6F from bank00 or bank01.");
		}

		if (UsesProvenBank0Default(dat.Header.Type))
		{
			return new(
				Palette: banks[0],
				Summary: $"{DefaultDirectoryName}/ uses proven scene-start bank00 for DAT type 0x{dat.Header.Type:X2}.");
		}

		if (dat.Header.Type == 0x0D)
		{
			return new(
				Palette: null,
				Summary: $"{DefaultDirectoryName}/ falls back to grayscale; type 0x0D has proven scripted/runtime palette changes through FUN_0003B1A0()/FUN_0003E080(), but the full live palette source is still unresolved.");
		}

		return new(
			Palette: null,
			Summary: $"{DefaultDirectoryName}/ falls back to grayscale; no scene-level palette strategy is proven yet for DAT type 0x{dat.Header.Type:X2}.");
	}

	private static bool UsesProvenBank0Default(byte datType)
	{
		return datType is 0x00 or 0x01 or 0x04 or 0x05 or 0x08 or 0x09 or 0x0C or 0x10;
	}

	private static byte ExpandVgaColor(byte value)
	{
		return (byte)((value * 255) / 63);
	}

	private static void SaveRawPaletteSheet(string outputPath, byte[] rawBank)
	{
		SavePaletteSheet(
			outputPath,
			colorIndex =>
			{
				int colorOffset = colorIndex * 3;
				return new Rgba32(rawBank[colorOffset + 0], rawBank[colorOffset + 1], rawBank[colorOffset + 2], 255);
			});
	}

	private static void SaveConvertedPaletteSheet(string outputPath, Rgba32[] palette)
	{
		SavePaletteSheet(outputPath, colorIndex => palette[colorIndex]);
	}

	private static void SavePaletteSheet(string outputPath, Func<int, Rgba32> colorSelector)
	{
		int width = PaletteGridSide * PaletteCellSize;
		int height = PaletteGridSide * PaletteCellSize;
		using Image<Rgba32> bitmap = new(width, height);

		bitmap.ProcessPixelRows(accessor =>
		{
			for (int colorIndex = 0; colorIndex < PaletteColorCount; colorIndex++)
			{
				int cellX = (colorIndex % PaletteGridSide) * PaletteCellSize;
				int cellY = (colorIndex / PaletteGridSide) * PaletteCellSize;
				Rgba32 color = colorSelector(colorIndex);

				for (int y = 0; y < PaletteCellSize; y++)
				{
					Span<Rgba32> row = accessor.GetRowSpan(cellY + y);
					row.Slice(cellX, PaletteCellSize).Fill(color);
				}
			}
		});

		bitmap.SaveAsPng(outputPath);
	}
}

internal sealed record DatPaletteContext(
	Rgba32[][] Banks,
	byte[][] RawBanks,
	IReadOnlyList<ExportPaletteVariant> Variants,
	Rgba32[]? DefaultPalette,
	string DefaultPaletteSummary)
{
	public int PaletteBankCount => Banks.Length;
}

internal sealed record ExportPaletteVariant(
	int BankIndex,
	string DirectoryName,
	string Description);

internal sealed record SceneDefaultPalette(
	Rgba32[]? Palette,
	string Summary);
