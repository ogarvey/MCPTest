using DogKnife.Models;

namespace DogKnife.Helpers;

internal static class KnownRenderExporter
{
	public static KnownRenderExportResult Export(CatGunDat dat, string outputRoot)
	{
		string outputDirectory = Path.GetFullPath(outputRoot);
		Directory.CreateDirectory(outputDirectory);
		string summaryPath = Path.Combine(outputDirectory, "known_render_summary.txt");
		List<KnownRenderResourceSummary> summaries = new(dat.Resources.Count);

		foreach (DatResourceEntry resource in dat.Resources.OrderBy(resource => resource.Name, StringComparer.Ordinal))
		{
			List<string> exportedActions = [];
			string? failure = null;
			string unresolvedLoaderTypes = "<none>";

			try
			{
				DatPayloadGroup? payloadGroup = resource.Pointer04 == 0
					? null
					: dat.PayloadGroups.SingleOrDefault(group => group.StartOffset == resource.Pointer04);

				HashSet<int> unresolvedTypes = payloadGroup is null
					? []
					: payloadGroup.Blocks
						.Select(block => block.LoaderType)
						.Where(loaderType => !IsHandledLoaderType(resource.Name, loaderType))
						.ToHashSet();

				unresolvedLoaderTypes = unresolvedTypes.Count == 0
					? "<none>"
					: string.Join(", ", unresolvedTypes.OrderBy(value => value).Select(value => $"0x{value:X2}"));

				if (string.Equals(resource.Name, "TEXTURE", StringComparison.Ordinal))
				{
					TextureExporter.Export(dat, outputRoot);
					exportedActions.Add("TEXTURE exporter");
				}

				if (payloadGroup is not null &&
					payloadGroup.Blocks.Any(block => block.LoaderType == 2) &&
					Type2CropResourceExporter.SupportsResource(resource.Name))
				{
					Type2CropResourceExporter.Export(dat, resource.Name, outputRoot);
					exportedActions.Add("type2 crops");
				}

				if (payloadGroup is not null &&
					payloadGroup.Blocks.Any(block => block.LoaderType == 6))
				{
					Type6RenderedExporter.Export(dat, resource.Name, outputRoot);
					exportedActions.Add("type6 overlays");
				}

				if (payloadGroup is not null &&
					payloadGroup.Blocks.Any(block => block.LoaderType == 4) &&
					RawPlaneResourceExporter.SupportsResource(resource.Name))
				{
					RawPlaneResourceExporter.Export(dat, resource.Name, outputRoot);
					exportedActions.Add("type4 planes");
				}

				if (payloadGroup is not null &&
					payloadGroup.Blocks.Any(block => block.LoaderType == 7) &&
					Type7AuxiliaryDataExporter.SupportsResource(resource.Name))
				{
					Type7AuxiliaryDataExporter.Export(dat, resource.Name, outputRoot);
					exportedActions.Add("type7 companion data");
				}

				if (payloadGroup is not null &&
					payloadGroup.Blocks.Any(block => block.LoaderType == 7) &&
					Type7EffectExporter.SupportsResource(resource.Name))
				{
					Type7EffectExporter.Export(dat, resource.Name, outputRoot);
					exportedActions.Add("type7 effects");
				}

				if (payloadGroup is not null && payloadGroup.Blocks.Any(block => block.LoaderType == 1))
				{
					Type1RenderedExporter.Export(dat, resource.Name, outputRoot);
					exportedActions.Add("type1 renders");
				}

				if (payloadGroup is not null &&
					payloadGroup.Blocks.Any(block => block.LoaderType == 3) &&
					DisplayType3RuntimeExporter.SupportsResource(resource.Name))
				{
					DisplayType3RuntimeExporter.Export(dat, outputRoot);
					exportedActions.Add("DISPLAY type3 runtime");
				}

				if (payloadGroup is not null &&
					payloadGroup.Blocks.Any(block => block.LoaderType == 3) &&
					Type3RemapProbeExporter.SupportsResource(resource.Name))
				{
					Type3RemapProbeExporter.Export(dat, resource.Name, outputRoot);
					exportedActions.Add("type3 remap data");
				}

				if (payloadGroup is not null &&
					Type3CompositeExporter.SupportsResource(resource.Name) &&
					payloadGroup is not null &&
					payloadGroup.Blocks.Any(block => block.LoaderType == 3))
				{
					Type3CompositeExporter.Export(dat, resource.Name, outputRoot);
					exportedActions.Add("type3 composites");
				}
			}
			catch (Exception exception)
			{
				failure = exception.Message;
			}

			summaries.Add(new KnownRenderResourceSummary(
				ResourceName: resource.Name,
				ExportedActions: exportedActions.Count == 0 ? "<none>" : string.Join(", ", exportedActions),
				UnresolvedLoaderTypes: unresolvedLoaderTypes,
				Failure: failure));
		}

		List<string> lines =
		[
			$"DAT: {dat.FilePath}",
			$"Resources: {summaries.Count}",
			$"Resources with exports: {summaries.Count(summary => summary.ExportedActions != "<none>")}",
			$"Resources with unresolved loader families: {summaries.Count(summary => summary.UnresolvedLoaderTypes != "<none>")}",
			$"Resources with exporter failures: {summaries.Count(summary => summary.Failure is not null)}",
			string.Empty,
			"Resources:",
		];

		foreach (KnownRenderResourceSummary summary in summaries)
		{
			lines.Add($"{summary.ResourceName}: exported={summary.ExportedActions} unresolved={summary.UnresolvedLoaderTypes} failure={(summary.Failure ?? "<none>")}");
		}

		File.WriteAllLines(summaryPath, lines);

		return new KnownRenderExportResult(
			SummaryPath: summaryPath,
			ResourceCount: summaries.Count,
			ExportedResourceCount: summaries.Count(summary => summary.ExportedActions != "<none>"),
			UnresolvedResourceCount: summaries.Count(summary => summary.UnresolvedLoaderTypes != "<none>"),
			FailureCount: summaries.Count(summary => summary.Failure is not null));
	}

	private static bool IsHandledLoaderType(string resourceName, int loaderType)
	{
		return loaderType switch
		{
			1 => true,
			2 when Type2CropResourceExporter.SupportsResource(resourceName) => true,
			6 => true,
			3 when Type3CompositeExporter.SupportsResource(resourceName) => true,
			3 when DisplayType3RuntimeExporter.SupportsResource(resourceName) => true,
			3 when Type3RemapProbeExporter.SupportsResource(resourceName) => true,
			4 when string.Equals(resourceName, "TEXTURE", StringComparison.Ordinal) || RawPlaneResourceExporter.SupportsResource(resourceName) => true,
			7 when Type7AuxiliaryDataExporter.SupportsResource(resourceName) => true,
			7 when Type7EffectExporter.SupportsResource(resourceName) => true,
			_ => false,
		};
	}
}

internal sealed record KnownRenderExportResult(
	string SummaryPath,
	int ResourceCount,
	int ExportedResourceCount,
	int UnresolvedResourceCount,
	int FailureCount);

internal sealed record KnownRenderResourceSummary(
	string ResourceName,
	string ExportedActions,
	string UnresolvedLoaderTypes,
	string? Failure);
