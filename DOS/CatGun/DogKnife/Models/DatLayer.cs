namespace DogKnife.Models;

internal sealed record DatLayer(
	int Index,
	int DescriptorOffset,
	int Value00,
	int Value04,
	int Width,
	int Height,
	int CellDataOffset,
	IReadOnlyList<uint> Cells,
	int NonZeroCellCount,
	int MaxReferenceIndex);
