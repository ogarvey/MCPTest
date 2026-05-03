using System.Buffers.Binary;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace BloodnetExtractor;

internal static class BloodnetImageDecoder
{
    public static bool TryProbe(ReadOnlySpan<byte> entryData, out BloodnetImageProbe probe, out string failureReason)
    {
        probe = null!;

        if (!TryReadContainer(entryData, out var container, out failureReason))
        {
            return false;
        }

        probe = new BloodnetImageProbe(
            container.Header,
            container.InlinePalette,
            GetEffectiveTransparentPixelIndex(container.Header, container.SymbolTable),
            container.HasInlineSymbolTable,
            container.AuxiliaryTable);

        failureReason = string.Empty;
        return true;
    }

    public static bool TryDecode(ReadOnlySpan<byte> entryData, out BloodnetDecodedImage image, out string failureReason)
    {
        image = null!;

        if (!TryReadContainer(entryData, out var container, out failureReason))
        {
            return false;
        }

        byte[] packedPixels;
        try
        {
            packedPixels = container.Header.Format switch
            {
                0x10 => DecodeRaw(container.Header, container.Payload),
                0x01 or 0x02 => DecodeIndexedImageStream(container.Header, container.Payload, container.SymbolTable),
                _ => throw new InvalidDataException($"Unsupported image format 0x{container.Header.Format:X2}."),
            };
        }
        catch (Exception exception)
        {
            failureReason = exception.Message;
            return false;
        }

        int? expandedWidthEstimate = null;

        var layoutNote = "Decoded pixels are emitted in the stored entry dimensions. Any later game-side layout expansion still depends on caller-side metadata that is not stored in the 0x1C entry header.";

        image = new BloodnetDecodedImage(
            container.Header,
            packedPixels,
            container.InlinePalette,
            container.Header.Format is 0x01 or 0x02,
            container.InlinePalette is not null,
            GetEffectiveTransparentPixelIndex(container.Header, container.SymbolTable),
            container.HasInlineSymbolTable,
            container.AuxiliaryTable,
            expandedWidthEstimate,
            layoutNote);

        failureReason = string.Empty;
        return true;
    }

    private static byte GetEffectiveTransparentPixelIndex(BloodnetImageHeader header, byte[] symbolTable)
    {
        if (header.Format is 0x01 or 0x02)
        {
            return symbolTable[header.TransparentIndex];
        }

        return header.TransparentIndex;
    }

    private static bool TryReadContainer(ReadOnlySpan<byte> entryData, out BloodnetImageContainer container, out string failureReason)
    {
        container = null!;

        if (!BloodnetImageHeader.TryParse(entryData, out var header, out failureReason))
        {
            return false;
        }

        var offset = BloodnetImageHeader.Size;
        var payload = entryData.Slice(offset, header.PayloadLength).ToArray();
        offset += header.PayloadLength;

        byte[]? inlinePalette = null;
        if ((header.Flags & 0x01) != 0)
        {
            var paletteByteCount = header.PaletteColorCount * 3;
            inlinePalette = entryData.Slice(offset, paletteByteCount).ToArray();
            offset += paletteByteCount;
        }

        var symbolTable = Enumerable.Range(0, 256).Select(index => (byte)index).ToArray();
        var hasInlineSymbolTable = (header.Flags & 0x02) != 0;
        if (hasInlineSymbolTable)
        {
            entryData.Slice(offset, header.SymbolCount).CopyTo(symbolTable);
            offset += header.SymbolCount;
        }

        byte[]? auxiliaryTable = null;
        if (header.AuxiliaryTableLength > 0)
        {
            auxiliaryTable = entryData.Slice(offset, header.AuxiliaryTableLength).ToArray();
            offset += header.AuxiliaryTableLength;
        }

        container = new BloodnetImageContainer(
            header,
            payload,
            inlinePalette,
            symbolTable,
            hasInlineSymbolTable,
            auxiliaryTable);

        failureReason = string.Empty;
        return true;
    }

    private static byte[] DecodeRaw(BloodnetImageHeader header, byte[] payload)
    {
        var expectedSize = header.Width * header.Height;
        if (payload.Length != expectedSize)
        {
            throw new InvalidDataException($"Raw payload length {payload.Length} does not match packed image size {expectedSize}.");
        }

        return payload;
    }

    private static byte[] DecodeIndexedImageStream(BloodnetImageHeader header, ReadOnlySpan<byte> payload, byte[] symbolTable)
    {
        if (payload.Length == 0)
        {
            throw new InvalidDataException("Compressed payload is empty.");
        }

        if (header.TailLiteralCount > payload.Length)
        {
            throw new InvalidDataException("Tail literal count exceeds the payload size.");
        }

        var outputLength = header.Width * header.Height;
        var output = new byte[outputLength];
        var writeIndex = 0;

        var currentSymbol = payload[0];
        output[writeIndex++] = symbolTable[currentSymbol];

        var mainPayloadLength = payload.Length - header.TailLiteralCount;
        var remainingPixels = outputLength - header.TailLiteralCount - 1;
        var cursor = new PackedNibbleCursor(payload.Slice(1, mainPayloadLength - 1));

        while (remainingPixels > 0)
        {
            var opcode = cursor.ReadNibble();

            if (opcode == 0x0F)
            {
                currentSymbol = cursor.ReadByte();
                output[writeIndex++] = symbolTable[currentSymbol];
                remainingPixels--;
                continue;
            }

            if (header.Format == 0x02 && opcode == 0x0E)
            {
                var runLength = (int)cursor.ReadByte();
                if (runLength == 0xFF)
                {
                    // The DOS decoder loads the first follow-up byte into CH and the second into CL.
                    var highByte = cursor.ReadByte();
                    var lowByte = cursor.ReadByte();
                    runLength = (highByte << 8) | lowByte;
                }

                runLength += 2;
                if (runLength > remainingPixels)
                {
                    throw new InvalidDataException("Decoded run length exceeds the remaining output pixels.");
                }

                var repeatedValue = output[writeIndex - 1];
                output.AsSpan(writeIndex, runLength).Fill(repeatedValue);
                writeIndex += runLength;
                remainingPixels -= runLength;
                continue;
            }

            currentSymbol = (byte)((currentSymbol + header.DeltaTable[opcode]) % header.SymbolCount);
            output[writeIndex++] = symbolTable[currentSymbol];
            remainingPixels--;
        }

        if (header.TailLiteralCount > 0)
        {
            var tailLiterals = payload[^header.TailLiteralCount..];
            foreach (var symbol in tailLiterals)
            {
                output[writeIndex++] = symbolTable[symbol];
            }
        }

        if (writeIndex != output.Length)
        {
            throw new InvalidDataException($"Decoded output length {writeIndex} does not match the expected image size {output.Length}.");
        }

        return output;
    }

    private ref struct PackedNibbleCursor(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> data = data;
        private int byteIndex;
        private bool readHighNibble;

        public byte ReadNibble()
        {
            EnsureAvailableBytes(1);

            if (!readHighNibble)
            {
                readHighNibble = true;
                return (byte)(data[byteIndex] & 0x0F);
            }

            var value = (byte)(data[byteIndex] >> 4);
            readHighNibble = false;
            byteIndex++;
            return value;
        }

        public byte ReadByte()
        {
            if (!readHighNibble)
            {
                EnsureAvailableBytes(1);
                return data[byteIndex++];
            }

            EnsureAvailableBytes(2);
            var value = (byte)((data[byteIndex] >> 4) | ((data[byteIndex + 1] & 0x0F) << 4));
            byteIndex++;
            return value;
        }
        private void EnsureAvailableBytes(int requiredBytes)
        {
            if (byteIndex + requiredBytes > data.Length)
            {
                throw new InvalidDataException("Compressed payload ended unexpectedly.");
            }
        }
    }
}

internal sealed record BloodnetDecodedImage(
    BloodnetImageHeader Header,
    byte[] PackedPixels,
    byte[]? InlinePalette,
    bool DecodedUsing2106_002cModel,
    bool HasInlinePalette,
    byte EffectiveTransparentPixelIndex,
    bool HasInlineSymbolTable,
    byte[]? AuxiliaryTable,
    int? EstimatedExpandedWidth,
    string LayoutNote)
{
    public bool HasAuxiliaryTable => AuxiliaryTable is not null;

    public int AuxiliaryTableLength => AuxiliaryTable?.Length ?? 0;
}

internal sealed record BloodnetImageProbe(
    BloodnetImageHeader Header,
    byte[]? InlinePalette,
    byte EffectiveTransparentPixelIndex,
    bool HasInlineSymbolTable,
    byte[]? AuxiliaryTable)
{
    public bool HasInlinePalette => InlinePalette is not null;

    public bool HasAuxiliaryTable => AuxiliaryTable is not null;

    public int AuxiliaryTableLength => AuxiliaryTable?.Length ?? 0;
}

internal sealed record BloodnetImageContainer(
    BloodnetImageHeader Header,
    byte[] Payload,
    byte[]? InlinePalette,
    byte[] SymbolTable,
    bool HasInlineSymbolTable,
    byte[]? AuxiliaryTable);

internal readonly record struct BloodnetImageHeader(
    byte Flags,
    int SymbolCount,
    int PaletteColorCount,
    byte TransparentIndex,
    byte Format,
    byte[] DeltaTable,
    ushort TailLiteralCount,
    ushort Width,
    ushort Height,
    ushort PayloadLength,
    int AuxiliaryTableLength)
{
    public const int Size = 0x1C;

    public static bool TryParse(ReadOnlySpan<byte> data, out BloodnetImageHeader header, out string failureReason)
    {
        header = default;
        failureReason = string.Empty;

        if (data.Length < Size)
        {
            failureReason = "Entry is smaller than the 0x1C Bloodnet image header.";
            return false;
        }

        var flags = data[0];
        var symbolCount = data[1] + 1;
        var paletteColorCount = data[2] + 1;
        var transparentIndex = data[3];
        var format = data[4];
        var deltaTable = data.Slice(5, 15).ToArray();
        var tailLiteralCount = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(0x14, 2));
        var width = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(0x18, 2));
        var height = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(0x16, 2));
        var payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(0x1A, 2));

        if (width == 0 || height == 0 || payloadLength == 0)
        {
            failureReason = "Image header has zero dimensions or payload length.";
            return false;
        }

        if (format is not 0x01 and not 0x02 and not 0x10)
        {
            failureReason = $"Unsupported Bloodnet image format 0x{format:X2}.";
            return false;
        }

        var expectedLength = Size + payloadLength;
        if ((flags & 0x01) != 0)
        {
            expectedLength += paletteColorCount * 3;
        }

        if ((flags & 0x02) != 0)
        {
            expectedLength += symbolCount;
        }

        var auxiliaryTableLength = 0;
        if ((flags & 0x04) != 0)
        {
            auxiliaryTableLength = symbolCount;
            expectedLength += auxiliaryTableLength;
        }

        if (expectedLength != data.Length)
        {
            failureReason = $"Entry length 0x{data.Length:X} does not match the Bloodnet image header expectation 0x{expectedLength:X}.";
            return false;
        }

        header = new BloodnetImageHeader(
            flags,
            symbolCount,
            paletteColorCount,
            transparentIndex,
            format,
            deltaTable,
            tailLiteralCount,
            width,
            height,
            payloadLength,
            auxiliaryTableLength);
        return true;
    }
}

internal static class ImagePreviewWriter
{
    public static void WritePreviewPng(
        string filePath,
        ReadOnlySpan<byte> pixels,
        int width,
        int height,
        byte[]? inlinePalette,
        byte? transparentPixelIndex = null)
    {
        using var image = new Image<Rgba32>(width, height);
        var palette = BuildPalette(inlinePalette);

        var pixelIndex = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var paletteIndex = pixels[pixelIndex++];
                var color = palette[paletteIndex];
                if (transparentPixelIndex is not null && paletteIndex == transparentPixelIndex.Value)
                {
                    color = new Rgba32(color.R, color.G, color.B, 0);
                }

                image[x, y] = color;
            }
        }

        image.SaveAsPng(filePath);
    }

    private static Rgba32[] BuildPalette(byte[]? inlinePalette)
    {
        var palette = new Rgba32[256];
        for (var index = 0; index < palette.Length; index++)
        {
            palette[index] = new Rgba32((byte)index, (byte)index, (byte)index, 255);
        }

        if (inlinePalette is null)
        {
            return palette;
        }

        var colorCount = Math.Min(256, inlinePalette.Length / 3);
        var scaleFromSixBit = inlinePalette.Take(colorCount * 3).All(value => value <= 63);
        for (var colorIndex = 0; colorIndex < colorCount; colorIndex++)
        {
            var red = inlinePalette[colorIndex * 3];
            var green = inlinePalette[colorIndex * 3 + 1];
            var blue = inlinePalette[colorIndex * 3 + 2];

            if (scaleFromSixBit)
            {
                red = ScaleVgaColor(red);
                green = ScaleVgaColor(green);
                blue = ScaleVgaColor(blue);
            }

            palette[colorIndex] = new Rgba32(red, green, blue, 255);
        }

        return palette;
    }

    private static byte ScaleVgaColor(byte value)
    {
        return (byte)Math.Clamp((value * 255) / 63, 0, 255);
    }
}
