static class SpbImageDecoder
{
	public static bool TryDecode(byte[] payload, out DecodedSpbImage decodedImage, out string? error)
	{
		decodedImage = default!;
		error = null;

		if (payload.Length < 12)
		{
			error = "Payload is too small to contain an SPB image header.";
			return false;
		}

		var width = BitConverter.ToUInt32(payload, 0);
		var height = BitConverter.ToUInt32(payload, 4);
		var dataStart = BitConverter.ToUInt32(payload, 8);

		if (width == 0 || height == 0)
		{
			error = "SPB dimensions must be non-zero.";
			return false;
		}

		var expectedDataStart = checked((uint)(12 + (height * 4)));
		if (dataStart != expectedDataStart)
		{
			error = $"SPB dataStart mismatch: expected {expectedDataStart}, found {dataStart}.";
			return false;
		}

		if (dataStart > payload.Length)
		{
			error = "SPB dataStart points beyond the payload.";
			return false;
		}

		var pixelCount64 = (ulong)width * height;
		if (pixelCount64 > int.MaxValue)
		{
			error = "SPB dimensions are too large to decode in memory.";
			return false;
		}

		var pixelCount = (int)pixelCount64;
		var decodedIndices = new byte[pixelCount];
		var alphaMask = new byte[pixelCount];
		var opaquePixelCount = 0;
		var widthInt = checked((int)width);
		var heightInt = checked((int)height);

		for (var row = 0; row < heightInt; row++)
		{
			var rowEntryOffset = 12 + (row * 4);
			var rowRelativeOffset = BitConverter.ToUInt32(payload, rowEntryOffset);
			var rowDataOffset64 = (ulong)rowEntryOffset + rowRelativeOffset;

			if (rowDataOffset64 < dataStart || rowDataOffset64 >= (ulong)payload.Length)
			{
				error = $"Row {row} points outside the SPB data stream.";
				return false;
			}

			var sourceOffset = (int)rowDataOffset64;
			var x = 0;

			while (true)
			{
				if ((uint)sourceOffset >= payload.Length)
				{
					error = $"Row {row} overran the SPB payload.";
					return false;
				}

				var opcode = payload[sourceOffset++];

				if ((opcode & 0x80) != 0)
				{
					var count = opcode & 0x3F;
					if (x + count > widthInt)
					{
						error = $"Row {row} literal run exceeds the decoded width.";
						return false;
					}

					if (sourceOffset + count > payload.Length)
					{
						error = $"Row {row} literal run exceeds the SPB payload.";
						return false;
					}

					var rowPixelOffset = (row * widthInt) + x;
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
					if (x + count > widthInt)
					{
						error = $"Row {row} transparent run exceeds the decoded width.";
						return false;
					}

					x += count;
					continue;
				}

				if (opcode == 0)
				{
					if (x != widthInt)
					{
						error = $"Row {row} ended after {x} pixels, expected {widthInt}.";
						return false;
					}

					break;
				}

				error = $"Row {row} encountered unknown opcode 0x{opcode:X2}.";
				return false;
			}
		}

		decodedImage = new DecodedSpbImage(widthInt, heightInt, decodedIndices, alphaMask, opaquePixelCount);
		return true;
	}
}
