namespace DogKnife.Models;

internal sealed record DatResourceEntry(
	int Index,
	int EntryOffset,
	int NameOffset,
	string Name,
	int Pointer04,
	int Pointer08,
	int Pointer0C,
	int Value10,
	int Pointer14,
	int Pointer18,
	int Value1C,
	int Value20,
	int Value24);
