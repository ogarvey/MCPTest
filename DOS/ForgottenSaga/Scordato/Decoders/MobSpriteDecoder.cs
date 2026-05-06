static class MobSpriteDecoder
{
	private const int RuntimeTailLengthOffset = 0xA0;
	private const int RuntimeWidthOffset = 0xA4;
	private const int RowCountOffset = 0xA8;
	private const int DataStartOffset = 0xAC;
	private const int RowTableOffset = 0xB0;

	public static bool TryInspectLayout(byte[] payload, out MobSpriteLayout layout)
	{
		layout = default;

		if (payload.Length < RowTableOffset)
		{
			return false;
		}

		var width = BitConverter.ToUInt16(payload, 4) + 1;
		var height = BitConverter.ToUInt16(payload, 6) + 1;

		if (width == 0 || height == 0 || width > 4096 || height > 4096)
		{
			return false;
		}

		var tailLength = BitConverter.ToUInt32(payload, RuntimeTailLengthOffset);
		var runtimeWidth = BitConverter.ToUInt32(payload, RuntimeWidthOffset);
		var rowCount = BitConverter.ToUInt32(payload, RowCountOffset);
		var dataStart = BitConverter.ToUInt32(payload, DataStartOffset);

		if (runtimeWidth != width || rowCount != height)
		{
			return false;
		}

		var expectedRuntimeLength = checked((uint)(payload.Length + 4));
		if (RowCountOffset + tailLength != expectedRuntimeLength)
		{
			return false;
		}

		var rowTableLength = checked((long)RowTableOffset + (rowCount * 4));
		if (rowTableLength > payload.Length)
		{
			return false;
		}

		layout = new MobSpriteLayout(width, height, tailLength, rowCount, dataStart);
		return true;
	}

	public static bool TryDecode(byte[] payload, out DecodedSpbImage decodedImage, out string? error)
	{
		decodedImage = default!;
		error = null;

		if (!TryInspectLayout(payload, out var layout))
		{
			error = "Payload does not match the currently supported MOB sprite layout.";
			return false;
		}

		var pixelCount64 = (ulong)layout.Width * (ulong)layout.Height;
		if (pixelCount64 > int.MaxValue)
		{
			error = "MOB sprite dimensions are too large to decode in memory.";
			return false;
		}

		var pixelCount = (int)pixelCount64;
		var decodedIndices = new byte[pixelCount];
		var alphaMask = new byte[pixelCount];
		var opaquePixelCount = 0;

		for (var row = 0; row < layout.Height; row++)
		{
			var rowEntryOffset = RowTableOffset + (row * 4);
			var rowRelativeOffset = BitConverter.ToUInt32(payload, rowEntryOffset);
			var rowDataOffset64 = (ulong)rowEntryOffset + rowRelativeOffset;

			if (rowDataOffset64 >= (ulong)payload.Length)
			{
				error = $"Row {row} points outside the MOB item payload.";
				return false;
			}

			var sourceOffset = (int)rowDataOffset64;
			var x = 0;

			while (true)
			{
				if ((uint)sourceOffset >= payload.Length)
				{
					error = $"Row {row} overran the MOB item payload.";
					return false;
				}

				var opcode = payload[sourceOffset++];

				if ((opcode & 0x80) != 0)
				{
					var count = opcode & 0x3F;
					if (x + count > layout.Width)
					{
						error = $"Row {row} literal run exceeds the decoded width.";
						return false;
					}

					if (sourceOffset + count > payload.Length)
					{
						error = $"Row {row} literal run exceeds the MOB item payload.";
						return false;
					}

					var rowPixelOffset = (row * layout.Width) + x;
					Buffer.BlockCopy(payload, sourceOffset, decodedIndices, rowPixelOffset, count);
					alphaMask.AsSpan(rowPixelOffset, count).Fill(0xFF);
					sourceOffset += count;
					x += count;
					opaquePixelCount += count;
					continue;
				}

				if ((opcode & 0x40) != 0)
				{
					var count = opcode & 0x3F;
					if (x + count > layout.Width)
					{
						error = $"Row {row} transparent run exceeds the decoded width.";
						return false;
					}

					x += count;
					continue;
				}

				if (opcode == 0)
				{
					if (x != layout.Width)
					{
						error = $"Row {row} ended after {x} pixels, expected {layout.Width}.";
						return false;
					}

					break;
				}

				error = $"Row {row} encountered unknown opcode 0x{opcode:X2}.";
				return false;
			}
		}

		decodedImage = new DecodedSpbImage(layout.Width, layout.Height, decodedIndices, alphaMask, opaquePixelCount);
		return true;
	}
}
