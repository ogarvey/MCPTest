namespace DogKnife.Models;

internal sealed record DatSequenceGroup(
	int StartOffset,
	int EndOffset,
	int ByteCount,
	int TrailingByteCount,
	int DelimiterCount,
	IReadOnlyList<string> ResourceNames,
	IReadOnlyList<DatSequenceSegment> Segments,
	IReadOnlyList<byte> RawBytes);
