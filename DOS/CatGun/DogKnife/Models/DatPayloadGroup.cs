namespace DogKnife.Models;

internal sealed record DatPayloadGroup(
	int StartOffset,
	int EndOffset,
	int ByteCount,
	int TrailingByteCount,
	IReadOnlyList<string> ResourceNames,
	IReadOnlyList<DatPayloadBlock30> Blocks);
