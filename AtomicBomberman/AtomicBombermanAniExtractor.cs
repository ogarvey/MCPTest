using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AtomicBomberman
{
    /// <summary>
    /// Reverse-engineered ANI decoder for Atomic Bomberman.
    ///
    /// Current scope:
    /// - Parses CHFILE container records as indexed by the game's ANI loader.
    /// - Decodes FRAM/CIMG image payloads for pixel format (flags & 7) == 4.
    /// - Supports CIMG encoding mode 0x00 (raw) and 0x11 (RLE16 variant).
    /// - Exports each decoded frame as 32-bit BMP.
    ///
    /// Notes:
    /// - The original game maps 16-bit pixels through a lookup table (DAT_00495390) into 8-bit indices.
    ///   This extractor instead converts 16-bit pixels directly to RGB (RGB565 by default).
    /// - State/sequence playback extraction from STAT/SEQ records can be added later.
    /// </summary>
    public static class AtomicBombermanAniExtractor
    {
        public sealed class AniFrame
        {
            public int Index { get; init; }
            public int Width { get; init; }
            public int Height { get; init; }
            public short OriginX { get; init; }
            public short OriginY { get; init; }
            public ushort FormatFlags { get; init; }
            public ushort TransparentKey16 { get; init; }
            public byte[] Rgba32 { get; init; } = Array.Empty<byte>();
        }

        private readonly struct ChunkRecord
        {
            public string Tag { get; init; }
            public uint Length { get; init; }
            public int DataOffset { get; init; }
            public int EndOffset { get; init; }
        }

        private readonly struct FramHeader
        {
            public ushort Field00 { get; init; }
            public ushort Field02 { get; init; }
            public uint CimgOffsetA { get; init; }
            public uint CimgOffsetB { get; init; }
            public short OriginX { get; init; }
            public short Width { get; init; }
            public short OriginY { get; init; }
            public short Height { get; init; }
            public uint HeaderExtSize { get; init; }
        }

        private readonly struct CimgHeader
        {
            public byte Encoding { get; init; }
            public byte Field01 { get; init; }
            public ushort FormatFlags { get; init; }
            public uint TransparentKey { get; init; }
            public uint UnpackedSize { get; init; }
            public uint PackedSize { get; init; }
        }

        public static IReadOnlyList<AniFrame> DecodeFrames(string aniPath, bool rgb565 = true, bool applyTransparencyKey = true)
        {
            using var fs = File.OpenRead(aniPath);
            using var br = new BinaryReader(fs, Encoding.ASCII, leaveOpen: true);

            var records = ReadChunkIndex(br);
            var framRecords = records.Where(r => r.Tag == "FRAM").ToList();
            var frames = new List<AniFrame>(framRecords.Count);

            for (int i = 0; i < framRecords.Count; i++)
            {
                var frame = TryDecodeFramRecord(br, framRecords[i], i, rgb565, applyTransparencyKey);
                if (frame != null)
                {
                    frames.Add(frame);
                }
            }

            return frames;
        }

        public static void ExportFramesAsBmp(string aniPath, string outputDirectory, string? prefix = null, bool rgb565 = true)
        {
            Directory.CreateDirectory(outputDirectory);
            prefix ??= Path.GetFileNameWithoutExtension(aniPath);

            var frames = DecodeFrames(aniPath, rgb565: rgb565, applyTransparencyKey: true);
            for (int i = 0; i < frames.Count; i++)
            {
                var frame = frames[i];
                string outPath = Path.Combine(outputDirectory, $"{prefix}_frame_{frame.Index:D4}_{frame.Width}x{frame.Height}.bmp");
                SaveRgbaAsBmp32(outPath, frame.Width, frame.Height, frame.Rgba32);
            }
        }

        private static AniFrame? TryDecodeFramRecord(BinaryReader br, ChunkRecord framRecord, int frameIndex, bool rgb565, bool applyTransparencyKey)
        {
            br.BaseStream.Seek(framRecord.DataOffset, SeekOrigin.Begin);

            var fh = new FramHeader
            {
                Field00 = br.ReadUInt16(),
                Field02 = br.ReadUInt16(),
                CimgOffsetA = br.ReadUInt32(),
                CimgOffsetB = br.ReadUInt32(),
                OriginX = br.ReadInt16(),
                Width = br.ReadInt16(),
                OriginY = br.ReadInt16(),
                Height = br.ReadInt16(),
                HeaderExtSize = br.ReadUInt32()
            };

            bool hasLegacyFramHeader = fh.Width > 0 && fh.Height > 0 && fh.Width < 4096 && fh.Height < 4096;

            if (!hasLegacyFramHeader)
            {
                // Observed files can store nested subchunks inside FRAM (e.g. HEAD/..../CIMG).
                // Fall back to scanning embedded CIMG by tag+len.
                return TryDecodeEmbeddedCimgChunk(br, framRecord, frameIndex, rgb565, applyTransparencyKey);
            }

            // Legacy FRAM path kept as fallback when header matches earlier assumptions.
            // Observed in game code: absolute seek target = CimgOffsetA - 0x18.
            if (fh.CimgOffsetA < 0x18)
            {
                return TryDecodeEmbeddedCimgChunk(br, framRecord, frameIndex, rgb565, applyTransparencyKey);
            }

            long cimgPos = fh.CimgOffsetA - 0x18;
            if (cimgPos < 0 || cimgPos >= br.BaseStream.Length)
            {
                return TryDecodeEmbeddedCimgChunk(br, framRecord, frameIndex, rgb565, applyTransparencyKey);
            }

            br.BaseStream.Seek(cimgPos, SeekOrigin.Begin);

            var ch = new CimgHeader
            {
                Encoding = br.ReadByte(),
                Field01 = br.ReadByte(),
                FormatFlags = br.ReadUInt16(),
                TransparentKey = br.ReadUInt32(),
                UnpackedSize = br.ReadUInt32(),
                PackedSize = br.ReadUInt32()
            };

            // Observed in game code: skip (HeaderExtSize - 0x0C) bytes before packed payload read.
            int extraSkip = Math.Max(0, unchecked((int)fh.HeaderExtSize) - 0x0C);
            if (extraSkip > 0)
            {
                br.BaseStream.Seek(extraSkip, SeekOrigin.Current);
            }

            if (ch.PackedSize > int.MaxValue || ch.UnpackedSize > int.MaxValue)
            {
                return null;
            }

            byte[] packed = br.ReadBytes((int)ch.PackedSize);
            if (packed.Length != (int)ch.PackedSize)
            {
                return null;
            }

            byte[] unpacked;
            switch (ch.Encoding)
            {
                case 0x00:
                    // In binary this path checks packed==unpacked and memcpys.
                    unpacked = packed;
                    break;
                case 0x11:
                    unpacked = DecodeMode11Rle16(packed, (int)ch.UnpackedSize, (int)ch.PackedSize);
                    break;
                default:
                    throw new NotSupportedException($"Unsupported CIMG encoding mode 0x{ch.Encoding:X2} in frame {frameIndex}.");
            }

            // In binary this is validated as: (formatFlags & 7) == 4.
            if ((ch.FormatFlags & 0x0007) != 0x0004)
            {
                throw new NotSupportedException($"Unsupported CIMG pixel format flags 0x{ch.FormatFlags:X4} in frame {frameIndex}.");
            }

            int w = fh.Width;
            int h = fh.Height;
            int pixelCount = w * h;
            if (unpacked.Length < pixelCount * 2)
            {
                return null;
            }

            ushort transparent16 = (ushort)(ch.TransparentKey & 0xFFFF);
            bool keyEnabled = applyTransparencyKey;

            byte[] rgba = new byte[pixelCount * 4];
            for (int i = 0; i < pixelCount; i++)
            {
                ushort p16 = BinaryPrimitives.ReadUInt16LittleEndian(unpacked.AsSpan(i * 2, 2));
                bool transparent = keyEnabled && (p16 == transparent16);

                byte r, g, b;
                if (rgb565)
                {
                    r = (byte)(((p16 >> 11) & 0x1F) * 255 / 31);
                    g = (byte)(((p16 >> 5) & 0x3F) * 255 / 63);
                    b = (byte)((p16 & 0x1F) * 255 / 31);
                }
                else
                {
                    // RGB555 fallback
                    r = (byte)(((p16 >> 10) & 0x1F) * 255 / 31);
                    g = (byte)(((p16 >> 5) & 0x1F) * 255 / 31);
                    b = (byte)((p16 & 0x1F) * 255 / 31);
                }

                int o = i * 4;
                rgba[o + 0] = r;
                rgba[o + 1] = g;
                rgba[o + 2] = b;
                rgba[o + 3] = transparent ? (byte)0 : (byte)255;
            }

            return new AniFrame
            {
                Index = frameIndex,
                Width = w,
                Height = h,
                OriginX = fh.OriginX,
                OriginY = fh.OriginY,
                FormatFlags = ch.FormatFlags,
                TransparentKey16 = transparent16,
                Rgba32 = rgba
            };
        }

        private static AniFrame? TryDecodeEmbeddedCimgChunk(BinaryReader br, ChunkRecord framRecord, int frameIndex, bool rgb565, bool applyTransparencyKey)
        {
            if (!TryFindChunkInRange(br, framRecord.DataOffset, framRecord.EndOffset, "CIMG", out var cimg))
            {
                return null;
            }

            br.BaseStream.Seek(cimg.DataOffset, SeekOrigin.Begin);
            byte[] payload = br.ReadBytes((int)cimg.Length);
            if (payload.Length < 0x18)
            {
                return null;
            }

            // Observed CIMG payload layout in attached samples:
            // +0x00 u16: encoding/variant (often 0x0001 in sample)
            // +0x04 u16: pixel format (4 in sample)
            // +0x06 u16: header size (0x18 in sample)
            // +0x0C u16: width
            // +0x0E u16: height
            // +0x10 s16: originX
            // +0x12 s16: originY
            // +0x14 u16: transparent key (low 16)
            ushort cimgType = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(0x00, 2));
            ushort formatFlags = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(0x04, 2));
            int headerSize = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(0x06, 2));
            if (headerSize < 0x18 || headerSize > payload.Length)
            {
                headerSize = 0x18;
            }

            int w = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(0x0C, 2));
            int h = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(0x0E, 2));
            short originX = (short)BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(0x10, 2));
            short originY = (short)BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(0x12, 2));
            ushort transparent16 = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(0x14, 2));

            if (w <= 0 || h <= 0 || w > 4096 || h > 4096)
            {
                return null;
            }

            byte[] packed = payload.AsSpan(headerSize).ToArray();
            int expectedBytes = checked(w * h * 2);

            byte[] unpacked;
            // Heuristic decoding for observed variants:
            // - If payload size matches expected pixels, treat as raw.
            // - Otherwise try mode 0x11 RLE16.
            if (packed.Length == expectedBytes)
            {
                unpacked = packed;
            }
            else
            {
                unpacked = DecodeMode11Rle16(packed, expectedBytes, packed.Length);
                if (unpacked.Length < expectedBytes)
                {
                    // fallback: raw truncate/pad guard
                    if (packed.Length >= expectedBytes)
                    {
                        unpacked = packed.AsSpan(0, expectedBytes).ToArray();
                    }
                    else
                    {
                        return null;
                    }
                }
            }

            if ((formatFlags & 0x0007) != 0x0004)
            {
                // Keep decoding anyway; some samples may carry variant flags.
            }

            bool keyEnabled = applyTransparencyKey;
            int pixelCount = w * h;
            byte[] rgba = new byte[pixelCount * 4];

            for (int i = 0; i < pixelCount; i++)
            {
                ushort p16 = BinaryPrimitives.ReadUInt16LittleEndian(unpacked.AsSpan(i * 2, 2));
                bool transparent = keyEnabled && (p16 == transparent16);

                byte r, g, b;
                if (rgb565)
                {
                    r = (byte)(((p16 >> 11) & 0x1F) * 255 / 31);
                    g = (byte)(((p16 >> 5) & 0x3F) * 255 / 63);
                    b = (byte)((p16 & 0x1F) * 255 / 31);
                }
                else
                {
                    r = (byte)(((p16 >> 10) & 0x1F) * 255 / 31);
                    g = (byte)(((p16 >> 5) & 0x1F) * 255 / 31);
                    b = (byte)((p16 & 0x1F) * 255 / 31);
                }

                int o = i * 4;
                rgba[o + 0] = r;
                rgba[o + 1] = g;
                rgba[o + 2] = b;
                rgba[o + 3] = transparent ? (byte)0 : (byte)255;
            }

            return new AniFrame
            {
                Index = frameIndex,
                Width = w,
                Height = h,
                OriginX = originX,
                OriginY = originY,
                FormatFlags = formatFlags,
                TransparentKey16 = transparent16,
                Rgba32 = rgba
            };
        }

        private static List<ChunkRecord> ReadChunkIndex(BinaryReader br)
        {
            var records = new List<ChunkRecord>();

            // The game validates "CHFILE" at start.
            br.BaseStream.Seek(0, SeekOrigin.Begin);
            var sig = br.ReadBytes(6);
            if (sig.Length < 6 || Encoding.ASCII.GetString(sig) != "CHFILE")
            {
                throw new InvalidDataException("Not a CHFILE container (expected CHFILE signature). ");
            }

            // Attached sample indicates chunk headers are TAG(4) + LEN(4), with payload immediately after.
            long cursor = 0x10; // CHFILEANI + header fields observed in sample
            if (cursor > br.BaseStream.Length)
            {
                cursor = 6;
            }

            while (cursor + 8 <= br.BaseStream.Length)
            {
                br.BaseStream.Seek(cursor, SeekOrigin.Begin);
                byte[] tagBytes = br.ReadBytes(4);
                if (tagBytes.Length < 4)
                {
                    break;
                }

                string tag = Encoding.ASCII.GetString(tagBytes);
                uint len = br.ReadUInt32();

                // If alignment/padding byte exists between chunks, resync by 1 byte.
                if (!IsLikelyTag(tag) || len > int.MaxValue)
                {
                    cursor += 1;
                    continue;
                }

                int dataOffset = checked((int)(cursor + 8));
                long end = cursor + 8 + len;
                if (end > br.BaseStream.Length)
                {
                    break;
                }

                records.Add(new ChunkRecord
                {
                    Tag = tag,
                    Length = len,
                    DataOffset = dataOffset,
                    EndOffset = checked((int)end)
                });

                cursor = end;
            }

            return records;
        }

        private static bool TryFindChunkInRange(BinaryReader br, int start, int end, string wantedTag, out ChunkRecord chunk)
        {
            long cursor = start;
            while (cursor + 8 <= end)
            {
                br.BaseStream.Seek(cursor, SeekOrigin.Begin);
                byte[] tagBytes = br.ReadBytes(4);
                if (tagBytes.Length < 4)
                {
                    break;
                }

                string tag = Encoding.ASCII.GetString(tagBytes);
                uint len = br.ReadUInt32();
                long dataOffset = cursor + 8;
                long next = dataOffset + len;

                if (!IsLikelyTag(tag) || next > end)
                {
                    cursor += 1;
                    continue;
                }

                if (tag == wantedTag)
                {
                    chunk = new ChunkRecord
                    {
                        Tag = tag,
                        Length = len,
                        DataOffset = (int)dataOffset,
                        EndOffset = (int)next
                    };
                    return true;
                }

                cursor = next;
            }

            chunk = default;
            return false;
        }

        private static bool IsLikelyTag(string tag)
        {
            if (tag.Length != 4)
            {
                return false;
            }

            for (int i = 0; i < 4; i++)
            {
                char c = tag[i];
                bool ok = (c >= 'A' && c <= 'Z') || c == ' ' || c == '.';
                if (!ok)
                {
                    return false;
                }
            }

            return true;
        }

        private static byte[] DecodeMode11Rle16(byte[] src, int expectedOutputBytes, int packedBytes)
        {
            // Based on FUN_0041c0ba.
            var dst = new byte[expectedOutputBytes];
            int si = 0;
            int di = 0;
            int remainingIn = Math.Min(packedBytes, src.Length);
            int remainingOut = expectedOutputBytes;

            while (remainingIn > 0 && remainingOut > 0)
            {
                byte control = src[si];
                if (control == 0xFF)
                {
                    break;
                }

                if ((control & 0x80) == 0)
                {
                    // Literal words: control+1 words, each as 2 raw bytes.
                    si += 1;
                    remainingIn -= 1;

                    int words = control + 1;
                    for (int i = 0; i < words && remainingIn >= 2 && remainingOut >= 2; i++)
                    {
                        dst[di++] = src[si++];
                        dst[di++] = src[si++];
                        remainingIn -= 2;
                        remainingOut -= 2;
                    }
                }
                else
                {
                    // Repeat word: repeat next 2-byte value (control&0x7F)+1 times.
                    int words = (control & 0x7F) + 1;
                    if (remainingIn < 3)
                    {
                        break;
                    }

                    byte b0 = src[si + 1];
                    byte b1 = src[si + 2];

                    for (int i = 0; i < words && remainingOut >= 2; i++)
                    {
                        dst[di++] = b0;
                        dst[di++] = b1;
                        remainingOut -= 2;
                    }

                    si += 3;
                    remainingIn -= 3;
                }
            }

            if (di < dst.Length)
            {
                Array.Resize(ref dst, di);
            }

            return dst;
        }

        private static void SaveRgbaAsBmp32(string filePath, int width, int height, byte[] rgba)
        {
            if (rgba.Length < width * height * 4)
            {
                throw new ArgumentException("RGBA buffer is smaller than width*height*4.");
            }

            int pixelBytes = width * height * 4;
            int fileSize = 14 + 40 + pixelBytes;

            using var fs = File.Create(filePath);
            using var bw = new BinaryWriter(fs);

            // BITMAPFILEHEADER
            bw.Write((byte)'B');
            bw.Write((byte)'M');
            bw.Write(fileSize);
            bw.Write((short)0);
            bw.Write((short)0);
            bw.Write(14 + 40);

            // BITMAPINFOHEADER
            bw.Write(40);
            bw.Write(width);
            bw.Write(height);
            bw.Write((short)1);
            bw.Write((short)32);
            bw.Write(0); // BI_RGB
            bw.Write(pixelBytes);
            bw.Write(2835);
            bw.Write(2835);
            bw.Write(0);
            bw.Write(0);

            // BMP pixel order: bottom-up, BGRA per pixel.
            for (int y = height - 1; y >= 0; y--)
            {
                int row = y * width * 4;
                for (int x = 0; x < width; x++)
                {
                    int o = row + x * 4;
                    byte r = rgba[o + 0];
                    byte g = rgba[o + 1];
                    byte b = rgba[o + 2];
                    byte a = rgba[o + 3];

                    bw.Write(b);
                    bw.Write(g);
                    bw.Write(r);
                    bw.Write(a);
                }
            }
        }
    }
}
