namespace DogKnife.Models;

internal sealed record DatSequenceSegment(
	int Index,
	int StartOffset,
	int ByteCount,
	IReadOnlyList<byte> Bytes);
