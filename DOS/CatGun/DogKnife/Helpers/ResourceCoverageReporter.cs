using DogKnife.Models;

namespace DogKnife.Helpers;

internal static class ResourceCoverageReporter
{
	public static ResourceCoverageReportResult Export(CatGunDat dat, string outputRoot)
	{
		string outputDirectory = Path.GetFullPath(outputRoot);
		Directory.CreateDirectory(outputDirectory);
		string reportPath = Path.Combine(outputDirectory, "resource_coverage_report.txt");

		Dictionary<string, Dictionary<int, int>> layerUsage = BuildLayerUsage(dat);
		IReadOnlyDictionary<string, string> exeLookups = GetExeNamedLookups(dat.FilePath);
		List<ResourceCoverageResourceSummary> summaries = new(dat.Resources.Count);

		foreach (DatResourceEntry resource in dat.Resources.OrderBy(resource => resource.Name, StringComparer.Ordinal))
		{
			DatPayloadGroup? payloadGroup = resource.Pointer04 == 0
				? null
				: dat.PayloadGroups.SingleOrDefault(group => group.StartOffset == resource.Pointer04);

			List<int> loaderTypes = payloadGroup is null
				? []
				: payloadGroup.Blocks
					.Select(block => block.LoaderType)
					.Distinct()
					.OrderBy(loaderType => loaderType)
					.ToList();

			string payloadLoaderTypes = payloadGroup is null
				? "<none>"
				: string.Join(", ", payloadGroup.Blocks
					.GroupBy(block => block.LoaderType)
					.OrderBy(group => group.Key)
					.Select(group => $"0x{group.Key:X2}:{group.Count()}"));

			string exactActions = FormatExactActions(resource.Name, payloadGroup);
			string unhandledLoaderTypes = loaderTypes.Count == 0
				? "<none>"
				: string.Join(", ", loaderTypes.Where(loaderType => !IsHandledLoaderType(resource.Name, loaderType)).Select(loaderType => $"0x{loaderType:X2}"));

			if (string.IsNullOrWhiteSpace(unhandledLoaderTypes))
			{
				unhandledLoaderTypes = "<none>";
			}

			string layerReferenceSummary = layerUsage.TryGetValue(resource.Name, out Dictionary<int, int>? perLayer)
				? string.Join(", ", perLayer.OrderBy(entry => entry.Key).Select(entry => $"L{entry.Key}:{entry.Value}"))
				: "<none>";

			exeLookups.TryGetValue(resource.Name, out string? exeLookupEvidence);

			summaries.Add(new ResourceCoverageResourceSummary(
				ResourceName: resource.Name,
				PayloadLoaderTypes: payloadLoaderTypes,
				ExactActions: exactActions,
				UnhandledLoaderTypes: unhandledLoaderTypes,
				LayerReferenceSummary: layerReferenceSummary,
				ExeLookupEvidence: exeLookupEvidence));
		}

		string presentLoaderTypes = string.Join(
			", ",
			dat.PayloadGroups
				.SelectMany(group => group.Blocks)
				.GroupBy(block => block.LoaderType)
				.OrderBy(group => group.Key)
				.Select(group => $"0x{group.Key:X2}:{group.Count()}"));

		string unhandledLoaderTypeSummary = string.Join(
			", ",
			dat.PayloadGroups
				.SelectMany(group => group.Blocks.Select(block => new
				{
					ResourceName = group.ResourceNames.FirstOrDefault() ?? string.Empty,
					block.LoaderType,
				}))
				.Where(block => !IsHandledLoaderType(block.ResourceName, block.LoaderType))
				.GroupBy(block => block.LoaderType)
				.OrderBy(group => group.Key)
				.Select(group => $"0x{group.Key:X2}:{group.Count()}"));

		if (string.IsNullOrWhiteSpace(unhandledLoaderTypeSummary))
		{
			unhandledLoaderTypeSummary = "<none>";
		}

		List<ResourceCoverageResourceSummary> priorityGaps = summaries
			.Where(summary => (summary.LayerReferenceSummary != "<none>" || summary.ExeLookupEvidence is not null) && summary.UnhandledLoaderTypes != "<none>")
			.ToList();

		List<string> lines =
		[
			$"DAT: {dat.FilePath}",
			$"Header type: 0x{dat.Header.Type:X2}",
			$"Resources: {summaries.Count}",
			$"Resources referenced by layer cells: {summaries.Count(summary => summary.LayerReferenceSummary != "<none>")}",
			$"Resources with EXE-proven named lookups: {summaries.Count(summary => summary.ExeLookupEvidence is not null)}",
			$"Resources with current exact export support: {summaries.Count(summary => summary.ExactActions != "<none>")}",
			$"Runtime-referenced resources with unresolved loader families: {priorityGaps.Count}",
			$"Present loader types: {(string.IsNullOrWhiteSpace(presentLoaderTypes) ? "<none>" : presentLoaderTypes)}",
			$"Unhandled loader types in this DAT: {unhandledLoaderTypeSummary}",
			string.Empty,
			"Likely runtime gaps:",
		];

		if (priorityGaps.Count == 0)
		{
			lines.Add("<none>");
		}
		else
		{
			foreach (ResourceCoverageResourceSummary summary in priorityGaps)
			{
				lines.Add($"{summary.ResourceName}: types={summary.PayloadLoaderTypes} exact={summary.ExactActions} unhandled={summary.UnhandledLoaderTypes} layers={summary.LayerReferenceSummary} exe={(summary.ExeLookupEvidence ?? "<none>")}");
			}
		}

		lines.Add(string.Empty);
		lines.Add("Resource coverage:");

		foreach (ResourceCoverageResourceSummary summary in summaries)
		{
			lines.Add($"{summary.ResourceName}: types={summary.PayloadLoaderTypes} exact={summary.ExactActions} unhandled={summary.UnhandledLoaderTypes} layers={summary.LayerReferenceSummary} exe={(summary.ExeLookupEvidence ?? "<none>")}");
		}

		File.WriteAllLines(reportPath, lines);

		return new ResourceCoverageReportResult(
			ReportPath: reportPath,
			ResourceCount: summaries.Count,
			RuntimeReferencedResourceCount: summaries.Count(summary => summary.LayerReferenceSummary != "<none>" || summary.ExeLookupEvidence is not null),
			PriorityGapCount: priorityGaps.Count,
			PresentLoaderTypes: string.IsNullOrWhiteSpace(presentLoaderTypes) ? "<none>" : presentLoaderTypes,
			UnhandledLoaderTypes: unhandledLoaderTypeSummary);
	}

	private static Dictionary<string, Dictionary<int, int>> BuildLayerUsage(CatGunDat dat)
	{
		Dictionary<string, Dictionary<int, int>> usage = new(StringComparer.Ordinal);

		foreach (DatLayer layer in dat.Layers)
		{
			foreach (uint cell in layer.Cells)
			{
				if (cell == 0)
				{
					continue;
				}

				int referenceIndex = (int)(cell & 0xFFFF);
				if ((uint)referenceIndex >= (uint)dat.CellReferences.Count)
				{
					continue;
				}

				string? resourceName = dat.CellReferences[referenceIndex].ResourceName;
				if (string.IsNullOrWhiteSpace(resourceName))
				{
					continue;
				}

				if (!usage.TryGetValue(resourceName, out Dictionary<int, int>? perLayer))
				{
					perLayer = [];
					usage.Add(resourceName, perLayer);
				}

				perLayer.TryGetValue(layer.Index, out int count);
				perLayer[layer.Index] = count + 1;
			}
		}

		return usage;
	}

	private static IReadOnlyDictionary<string, string> GetExeNamedLookups(string datPath)
	{
		return Path.GetFileName(datPath) switch
		{
			"intro.dat" => new Dictionary<string, string>(StringComparer.Ordinal)
			{
				["INTROBCK"] = "FUN_00035000() explicit lookup",
				["LEVELS"] = "FUN_00035000() explicit lookup",
				["FONT"] = "FUN_00035000() explicit lookup",
			},
			"highscor.dat" => new Dictionary<string, string>(StringComparer.Ordinal)
			{
				["DIFONT1"] = "FUN_0004AB90() explicit lookup",
				["RBFONT1"] = "FUN_0004AB90() explicit lookup",
			},
			"leader.dat" => new Dictionary<string, string>(StringComparer.Ordinal)
			{
				["LEADER"] = "FUN_00036F40() explicit lookup",
			},
			"evilbase.dat" => new Dictionary<string, string>(StringComparer.Ordinal)
			{
				["LAFONT"] = "FUN_00043EF0() explicit lookup on selector-5 dynamic path",
			},
			_ => new Dictionary<string, string>(StringComparer.Ordinal),
		};
	}

	private static string FormatExactActions(string resourceName, DatPayloadGroup? payloadGroup)
	{
		List<string> actions = [];

		if (string.Equals(resourceName, "TEXTURE", StringComparison.Ordinal))
		{
			actions.Add("TEXTURE exporter");
		}

		if (payloadGroup is null)
		{
			return actions.Count == 0 ? "<none>" : string.Join(", ", actions);
		}

		if (payloadGroup.Blocks.Any(block => block.LoaderType == 4) &&
			RawPlaneResourceExporter.SupportsResource(resourceName))
		{
			actions.Add("type4 planes");
		}

		if (payloadGroup.Blocks.Any(block => block.LoaderType == 2) &&
			Type2CropResourceExporter.SupportsResource(resourceName))
		{
			actions.Add("type2 crops");
		}

		if (payloadGroup.Blocks.Any(block => block.LoaderType == 6))
		{
			actions.Add("type6 overlays");
		}

		if (payloadGroup.Blocks.Any(block => block.LoaderType == 7) &&
			Type7AuxiliaryDataExporter.SupportsResource(resourceName))
		{
			actions.Add("type7 companion data");
		}

		if (payloadGroup.Blocks.Any(block => block.LoaderType == 7) &&
			Type7EffectExporter.SupportsResource(resourceName))
		{
			actions.Add("type7 effects");
		}

		if (payloadGroup.Blocks.Any(block => block.LoaderType == 1))
		{
			actions.Add("type1 renders");
		}

		if (payloadGroup.Blocks.Any(block => block.LoaderType == 3) &&
			DisplayType3RuntimeExporter.SupportsResource(resourceName))
		{
			actions.Add("DISPLAY type3 runtime");
		}

		if (payloadGroup.Blocks.Any(block => block.LoaderType == 3) &&
			Type3RemapProbeExporter.SupportsResource(resourceName))
		{
			actions.Add("type3 remap data");
		}

		if (Type3CompositeExporter.SupportsResource(resourceName) &&
			payloadGroup.Blocks.Any(block => block.LoaderType == 3))
		{
			actions.Add("type3 composites");
		}

		return actions.Count == 0 ? "<none>" : string.Join(", ", actions);
	}

	private static bool IsHandledLoaderType(string resourceName, int loaderType)
	{
		return loaderType switch
		{
			1 => true,
			2 when Type2CropResourceExporter.SupportsResource(resourceName) => true,
			6 => true,
			3 when Type3CompositeExporter.SupportsResource(resourceName) => true,
			3 when Type3RemapProbeExporter.SupportsResource(resourceName) => true,
			3 when DisplayType3RuntimeExporter.SupportsResource(resourceName) => true,
			4 when string.Equals(resourceName, "TEXTURE", StringComparison.Ordinal) || RawPlaneResourceExporter.SupportsResource(resourceName) => true,
			7 when Type7AuxiliaryDataExporter.SupportsResource(resourceName) => true,
			7 when Type7EffectExporter.SupportsResource(resourceName) => true,
			_ => false,
		};
	}
}

internal sealed record ResourceCoverageReportResult(
	string ReportPath,
	int ResourceCount,
	int RuntimeReferencedResourceCount,
	int PriorityGapCount,
	string PresentLoaderTypes,
	string UnhandledLoaderTypes);

internal sealed record ResourceCoverageResourceSummary(
	string ResourceName,
	string PayloadLoaderTypes,
	string ExactActions,
	string UnhandledLoaderTypes,
	string LayerReferenceSummary,
	string? ExeLookupEvidence);
