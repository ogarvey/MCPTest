using System.Text;

static class CharDatInspector
{
	private const int CountHeaderOffset = 0;
	private const int LzStreamOffset = 4;
	private const int RecordNameLength = 32;
	private const int RecordSize = 0x3BC;
	private const int NodeTemplateOffset = 0x20;
	private const int NodeTemplateLength = 0xB8;
	private const int SlotRecordSize = 0x20;
	private const int PrimarySlotGroupOffset = 0xDC;
	private const int PrimarySlotGroupCount = 8;
	private const int SecondarySlotGroupCount = 5;
	private static readonly int[] SecondarySlotGroupOffsets = { 0x1DC, 0x27C, 0x31C };
	private static readonly Encoding KoreanEncoding = Encoding.GetEncoding(949);

	public static CharDatInspection Inspect(string path, bool retainDecompressedPayload)
	{
		var data = File.ReadAllBytes(path);
		if (data.Length < 8)
		{
			throw new InvalidDataException($"{path} is smaller than the CHAR.DAT count + LZ-size header.");
		}

		var recordCount = ReadUInt32(data, CountHeaderOffset);
		var expectedDecodedSize64 = (ulong)recordCount * RecordSize;
		if (expectedDecodedSize64 > int.MaxValue)
		{
			throw new InvalidDataException($"{path} expands to 0x{expectedDecodedSize64:X} bytes, which is too large to inspect in memory.");
		}

		var expectedDecodedSize = checked((int)expectedDecodedSize64);
		var lzDecodedSize = ReadUInt32(data, LzStreamOffset);

		if (!FamInspector.TryDecompressLz(data, LzStreamOffset, out var decoded, out var bytesConsumed, out var error))
		{
			throw new InvalidDataException($"Failed to decompress CHAR.DAT: {error}");
		}

		if (decoded.Length != expectedDecodedSize)
		{
			throw new InvalidDataException(
				$"CHAR.DAT decompressed to {decoded.Length} bytes, but the runtime-derived record count requires {expectedDecodedSize} bytes ({recordCount} * 0x{RecordSize:X}).");
		}

		if (lzDecodedSize != expectedDecodedSize)
		{
			throw new InvalidDataException(
				$"CHAR.DAT LZ header advertises 0x{lzDecodedSize:X} bytes, but the runtime-derived record table requires 0x{expectedDecodedSize:X} bytes.");
		}

		var manifests = new List<CharDatRecordManifest>(checked((int)recordCount));
		var entries = new List<CharDatRecordPayload>(checked((int)recordCount));

		for (var index = 0; index < recordCount; index++)
		{
			var recordOffset = checked((int)index * RecordSize);
			var payload = decoded.AsSpan(recordOffset, RecordSize).ToArray();
			var rawNameBytes = payload.AsSpan(0, RecordNameLength).ToArray();

			var manifest = new CharDatRecordManifest
			{
				Index = checked((int)index),
				RecordOffset = recordOffset,
				DecodedName = DecodeName(rawNameBytes),
				RawNameHex = Convert.ToHexString(rawNameBytes)
			};

			manifests.Add(manifest);
			entries.Add(new CharDatRecordPayload(manifest, payload));
		}

		var manifestModel = new CharDatManifest
		{
			FileName = Path.GetFileName(path),
			FullPath = path,
			RecordCount = recordCount,
			RecordSize = RecordSize,
			CountHeaderOffset = CountHeaderOffset,
			LzStreamOffset = LzStreamOffset,
			LzDecodedSize = lzDecodedSize,
			ExpectedDecodedSize = checked((uint)expectedDecodedSize),
			LzBytesConsumed = bytesConsumed,
			NameFieldLength = RecordNameLength,
			NodeTemplateOffset = NodeTemplateOffset,
			NodeTemplateLength = NodeTemplateLength,
			SlotRecordSize = SlotRecordSize,
			PrimarySlotGroupOffset = PrimarySlotGroupOffset,
			PrimarySlotGroupCount = PrimarySlotGroupCount,
			SecondarySlotGroupCount = SecondarySlotGroupCount,
			Entries = manifests
		};
		manifestModel.SecondarySlotGroupOffsets.AddRange(SecondarySlotGroupOffsets);

		return new CharDatInspection(
			manifestModel,
			entries,
			data.AsSpan(0, 8).ToArray(),
			retainDecompressedPayload ? decoded : null);
	}

	private static string DecodeName(byte[] rawNameBytes)
	{
		var zeroIndex = Array.IndexOf(rawNameBytes, (byte)0);
		var count = zeroIndex >= 0 ? zeroIndex : rawNameBytes.Length;
		if (count == 0)
		{
			return string.Empty;
		}

		return KoreanEncoding.GetString(rawNameBytes, 0, count).Trim();
	}

	private static uint ReadUInt32(byte[] data, int offset)
	{
		return BitConverter.ToUInt32(data, offset);
	}
}
