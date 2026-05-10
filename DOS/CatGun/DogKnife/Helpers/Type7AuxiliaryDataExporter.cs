using System.Buffers.Binary;
using DogKnife.Models;

namespace DogKnife.Helpers;

internal static class Type7AuxiliaryDataExporter
{
	private static readonly Dictionary<string, IReadOnlyList<string>> ResourceNotes = new(StringComparer.Ordinal)
	{
		["INTROBCK"] =
		[
			"Exact companion note: FUN_00035000() performs the explicit INTROBCK lookup, and the traced companion path through FUN_00036A00() consumes the type-7 block as non-visual auxiliary data rather than a LEADER-style render script.",
		],
		["OPTIONS"] =
		[
			"Companion note: this type-7 block is not a LEADER-style script; its header fields do not decode as image dimensions and the payload begins with a long 0x7F-filled region rather than a recovered command table.",
		],
		["HALLBACK"] =
		[
			"Companion note: this type-7 block is not a LEADER-style script; the visible HALLBACK background is already recovered through the type-4 plane block, so this exporter writes the remaining companion bytes exactly as raw data rather than claiming a decoded full-screen render.",
		],
	};

	public static bool SupportsResource(string resourceName)
	{
		return ResourceNotes.ContainsKey(resourceName);
	}

	public static Type7AuxiliaryDataExportResult Export(CatGunDat dat, string resourceName, string outputRoot)
	{
		if (!SupportsResource(resourceName))
		{
			throw new NotSupportedException($"{resourceName} is not supported by the exact type-7 auxiliary-data exporter.");
		}

		DatResourceEntry resource = dat.Resources.SingleOrDefault(candidate =>
			string.Equals(candidate.Name, resourceName, StringComparison.Ordinal))
			?? throw new InvalidDataException($"{resourceName} resource was not found in the DAT resource table.");

		DatPayloadGroup payloadGroup = dat.PayloadGroups.SingleOrDefault(group => group.StartOffset == resource.Pointer04)
			?? throw new InvalidDataException($"{resourceName} payload group was not found for resource field +0x04.");

		List<DatPayloadBlock30> type7Blocks = payloadGroup.Blocks
			.Where(block => (block.Value20 & 0xFF) == 7)
			.ToList();

		if (type7Blocks.Count == 0)
		{
			throw new InvalidDataException($"{resourceName} does not expose any loader type-7 blocks in its payload group.");
		}

		ReadOnlySpan<byte> bytes = dat.RawBytes.Span;
		string familyRoot = Path.Combine(Path.GetFullPath(outputRoot), resourceName, "type7_auxiliary");
		Directory.CreateDirectory(familyRoot);

		List<Type7AuxiliaryBlockSummary> summaries = new(type7Blocks.Count);
		foreach (DatPayloadBlock30 block in type7Blocks)
		{
			int dataSpan = GetBlockDataSpan(dat, block.Value24);
			if (dataSpan <= 0)
			{
				throw new InvalidDataException($"{resourceName} type-7 block {block.Index} resolved to an invalid data span 0x{dataSpan:X} at 0x{block.Value24:X}.");
			}

			string outputPath = Path.Combine(familyRoot, $"block_{block.Index:D2}_data_{block.Value24:X}.bin");
			File.WriteAllBytes(outputPath, bytes.Slice(block.Value24, dataSpan).ToArray());

			string hexPreview = string.Join(' ', bytes.Slice(block.Value24, Math.Min(64, dataSpan)).ToArray().Select(value => value.ToString("X2")));
			string ushortPreview = BuildUInt16Preview(bytes, block.Value24, dataSpan);

			summaries.Add(new Type7AuxiliaryBlockSummary(
				BlockIndex: block.Index,
				DataOffset: block.Value24,
				DataSpan: dataSpan,
				OutputPath: outputPath,
				HexPreview: hexPreview,
				UInt16Preview: ushortPreview));
		}

		WriteMetadata(Path.Combine(familyRoot, "metadata.txt"), dat, resource, payloadGroup, summaries);

		return new Type7AuxiliaryDataExportResult(resource.Name, familyRoot, summaries.Count);
	}

	private static void WriteMetadata(
		string outputPath,
		CatGunDat dat,
		DatResourceEntry resource,
		DatPayloadGroup payloadGroup,
		IReadOnlyList<Type7AuxiliaryBlockSummary> summaries)
	{
		List<string> lines =
		[
			$"DAT: {dat.FilePath}",
			$"Resource: {resource.Name}",
			$"Payload group: 0x{payloadGroup.StartOffset:X}..0x{payloadGroup.EndOffset:X}",
			$"Payload loader types: {string.Join(", ", payloadGroup.Blocks.GroupBy(block => block.LoaderType).OrderBy(group => group.Key).Select(group => $"0x{group.Key:X2}:{group.Count()}"))}",
			"Exact companion-data note: this exporter writes the raw companion payload bytes and previews them, instead of claiming a final composited image family.",
			string.Empty,
			"Blocks:",
		];

		lines.InsertRange(5, ResourceNotes[resource.Name]);

		foreach (Type7AuxiliaryBlockSummary summary in summaries)
		{
			lines.Add($"[{summary.BlockIndex:D2}] data=0x{summary.DataOffset:X} span=0x{summary.DataSpan:X} file={Path.GetFileName(summary.OutputPath)}");
			lines.Add($"  first64={summary.HexPreview}");
			lines.Add($"  first16_u16={summary.UInt16Preview}");
		}

		File.WriteAllLines(outputPath, lines);
	}

	private static int GetBlockDataSpan(CatGunDat dat, int dataOffset)
	{
		int nextDataOffset = dat.Table40Blocks
			.SelectMany(GetCandidateDataOffsets)
			.Where(candidate => candidate > dataOffset)
			.DefaultIfEmpty(dat.RawBytes.Length)
			.Min();

		return nextDataOffset - dataOffset;
	}

	private static IEnumerable<int> GetCandidateDataOffsets(DatPayloadBlock30 block)
	{
		if (block.Value24 > 0)
		{
			yield return block.Value24;
		}

		if (block.Value28 > 0)
		{
			yield return block.Value28;
		}
	}

	private static string BuildUInt16Preview(ReadOnlySpan<byte> bytes, int dataOffset, int dataSpan)
	{
		if (dataSpan < 2)
		{
			return "<none>";
		}

		int valueCount = Math.Min(16, dataSpan / 2);
		List<string> values = new(valueCount);
		for (int index = 0; index < valueCount; index++)
		{
			values.Add(BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(dataOffset + (index * 2), 2)).ToString("X4"));
		}

		return string.Join(' ', values);
	}
}

internal sealed record Type7AuxiliaryDataExportResult(string ResourceName, string OutputDirectory, int BlockCount);

internal sealed record Type7AuxiliaryBlockSummary(
	int BlockIndex,
	int DataOffset,
	int DataSpan,
	string OutputPath,
	string HexPreview,
	string UInt16Preview);
