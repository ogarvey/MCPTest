namespace DogKnife.Models;

internal sealed record DatCellReferenceEntry(
	int Index,
	int EntryOffset,
	ushort Value00,
	byte Byte02,
	byte Byte03,
	byte Byte04,
	byte ResourceIndex,
	byte Byte06,
	string? ResourceName);
