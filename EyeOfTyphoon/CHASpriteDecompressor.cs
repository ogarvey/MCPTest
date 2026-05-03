using System;
using System.IO;

namespace EyeOfTyphoon
{
    /// <summary>
    /// Decompresses sprite data from Eye of Typhoon CHA files.
    /// The sprites use RLE compression designed for VGA Mode X (planar 4-bit graphics).
    /// </summary>
    public class CHASpriteDecompressor
    {
        /// <summary>
        /// Decompresses a single sprite from CHA file data.
        /// </summary>
        /// <param name="compressedData">The compressed sprite data (starting with width/height header)</param>
        /// <returns>Decompressed 8-bit indexed pixel data (0 = transparent)</returns>
        public static byte[] DecompressSprite(byte[] compressedData)
        {
            if (compressedData == null || compressedData.Length < 4)
                throw new ArgumentException("Invalid sprite data");

            using (var reader = new BinaryReader(new MemoryStream(compressedData)))
            {
                return DecompressSprite(reader);
            }
        }

        /// <summary>
        /// Decompresses a single sprite from a BinaryReader.
        /// </summary>
        /// <param name="reader">BinaryReader positioned at sprite header</param>
        /// <returns>Decompressed 8-bit indexed pixel data (0 = transparent)</returns>
        public static byte[] DecompressSprite(BinaryReader reader)
        {
            // Read sprite dimensions from header
            ushort width = reader.ReadUInt16();   // Width in pixels (NOT divided by 4)
            ushort height = reader.ReadUInt16();
            
            // Skip sprite offset data (we'll read this in DecompressSpriteWithInfo instead)
            reader.ReadBytes(6);
            
            int totalPixels = width * height;
            
            byte[] outputBuffer = new byte[totalPixels];
            int pixelIndex = 0;

            // Decompress RLE data
            while (pixelIndex < totalPixels)
            {
                byte controlByte = reader.ReadByte();

                if (controlByte < 0x80)
                {
                    // Single literal pixel
                    // The entire byte is the pixel value (palette index in low nibble)
                    byte pixelValue = controlByte;
                    outputBuffer[pixelIndex++] = pixelValue;
                }
                else
                {
                    // Run of pixels
                    byte runLength = (byte)(controlByte & 0x7F);  // Bits 0-6 contain run length
                    byte pixelValue = reader.ReadByte();  // Full byte is the pixel value

                    // Write the run
                    for (int i = 0; i < runLength && pixelIndex < totalPixels; i++)
                    {
                        outputBuffer[pixelIndex++] = pixelValue;
                    }
                }
            }

            return outputBuffer;
        }

        /// <summary>
        /// Converts VGA Mode X planar data to linear pixel format.
        /// In Mode X, the sprite is divided into 4 horizontal planes/strips.
        /// Each plane contains height/4 rows, and they need to be interleaved.
        /// </summary>
        /// <param name="planarData">Decompressed planar pixel data</param>
        /// <param name="width">Sprite width</param>
        /// <param name="height">Sprite height (must be divisible by 4)</param>
        /// <returns>Linear pixel data where rows are in correct order</returns>
        public static byte[] ConvertPlanarToLinear(byte[] planarData, int width, int height)
        {
            if (height % 4 != 0)
                throw new ArgumentException("Height must be divisible by 4 for planar mode");

            int totalPixels = width * height;
            byte[] linearData = new byte[totalPixels];
            int rowsPerPlane = height / 4;  // Each plane contains height/4 rows
            int bytesPerRow = width;

            // Interleave the 4 planes
            for (int plane = 0; plane < 4; plane++)
            {
                int planeOffset = plane * rowsPerPlane * bytesPerRow;
                
                for (int row = 0; row < rowsPerPlane; row++)
                {
                    int srcOffset = planeOffset + (row * bytesPerRow);
                    int dstRow = (row * 4) + plane;  // Interleave: plane 0 row 0 -> output row 0, plane 1 row 0 -> output row 1, etc.
                    int dstOffset = dstRow * bytesPerRow;
                    
                    Array.Copy(planarData, srcOffset, linearData, dstOffset, bytesPerRow);
                }
            }

            return linearData;
        }

        /// <summary>
        /// Decompresses a sprite and converts from planar to linear format.
        /// </summary>
        /// <param name="compressedData">The compressed sprite data</param>
        /// <returns>Sprite with linear (non-planar) pixel data</returns>
        public static SpriteData DecompressSpriteLinear(byte[] compressedData)
        {
            var sprite = DecompressSpriteWithInfo(compressedData);
            sprite.Pixels = ConvertPlanarToLinear(sprite.Pixels, sprite.Width, sprite.Height);
            return sprite;
        }

        /// <summary>
        /// Extracts and converts all sprites to linear format.
        /// </summary>
        public static SpriteData[] ExtractAllSpritesLinear(byte[] chaData, byte[] pidData)
        {
            var sprites = ExtractAllSprites(chaData, pidData);
            
            for (int i = 0; i < sprites.Length; i++)
            {
                if (sprites[i] != null)
                {
                    sprites[i].Pixels = ConvertPlanarToLinear(
                        sprites[i].Pixels, 
                        sprites[i].Width, 
                        sprites[i].Height);
                }
            }
            
            return sprites;
        }

        /// <summary>
        /// Decompresses a sprite and returns it with dimension information.
        /// </summary>
        public static SpriteData DecompressSpriteWithInfo(byte[] compressedData)
        {
            if (compressedData == null || compressedData.Length < 10)
                throw new ArgumentException("Invalid sprite data");

            using (var ms = new MemoryStream(compressedData))
            using (var reader = new BinaryReader(ms))
            {
                ushort width = reader.ReadUInt16();   // Width in pixels (NOT divided by 4)
                ushort height = reader.ReadUInt16();
                
                // Skip 6 bytes of unknown header data 
                // (Not alignment data - positioning is done by calling code)
                byte[] unknownHeader = reader.ReadBytes(6);

                // Now read the pixel data
                byte[] pixels = new byte[width * height];
                int pixelIndex = 0;

                // Decompress RLE data
                while (pixelIndex < pixels.Length && ms.Position < ms.Length)
                {
                    byte controlByte = reader.ReadByte();

                    if (controlByte < 0x80)
                    {
                        pixels[pixelIndex++] = controlByte;
                    }
                    else
                    {
                        byte runLength = (byte)(controlByte & 0x7F);
                        byte pixelValue = reader.ReadByte();

                        for (int i = 0; i < runLength && pixelIndex < pixels.Length; i++)
                        {
                            pixels[pixelIndex++] = pixelValue;
                        }
                    }
                }

                return new SpriteData
                {
                    Width = width,
                    Height = height,
                    UnknownHeader = unknownHeader,
                    Pixels = pixels
                };
            }
        }

        /// <summary>
        /// Extracts individual sprites from a CHA file using a PID (Picture Index Data) file.
        /// </summary>
        /// <param name="chaData">Complete CHA file data</param>
        /// <param name="pidData">Complete PID file data (array of DWORDs indicating sprite sizes)</param>
        /// <returns>Array of decompressed sprites</returns>
        public static SpriteData[] ExtractAllSprites(byte[] chaData, byte[] pidData)
        {
            if (chaData == null || pidData == null)
                throw new ArgumentException("Invalid file data");

            using (var pidReader = new BinaryReader(new MemoryStream(pidData)))
            {
                // First DWORD in PID is often special/header, read and skip it
                uint firstEntry = pidReader.ReadUInt32();
                
                // Calculate number of sprites (remaining bytes / 4)
                int spriteCount = (int)((pidData.Length - 4) / 4);
                var sprites = new SpriteData[spriteCount];

                int currentOffset = 0;

                for (int i = 0; i < spriteCount; i++)
                {
                    uint spriteSize = pidReader.ReadUInt32();
                    
                    if (currentOffset + spriteSize > chaData.Length)
                        break;

                    // Extract sprite data
                    byte[] spriteData = new byte[spriteSize];
                    Array.Copy(chaData, currentOffset, spriteData, 0, spriteSize);

                    // Decompress
                    sprites[i] = DecompressSpriteWithInfo(spriteData);
                    
                    currentOffset += (int)spriteSize;
                }

                return sprites;
            }
        }
    }

    /// <summary>
    /// Represents a decompressed sprite with dimension information.
    /// </summary>
    public class SpriteData
    {
        /// <summary>Width in pixels</summary>
        public int Width { get; set; }
        
        /// <summary>Height in pixels</summary>
        public int Height { get; set; }
        
        /// <summary>6 bytes of unknown header data from sprite</summary>
        public byte[] UnknownHeader { get; set; }
        
        /// <summary>8-bit indexed pixel data (0 = transparent, full byte is palette index)</summary>
        public byte[] Pixels { get; set; }

        /// <summary>
        /// Converts the sprite to a simple text representation for debugging.
        /// </summary>
        public string ToDebugString()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Sprite: {Width}x{Height}");
            if (UnknownHeader != null && UnknownHeader.Length > 0)
            {
                sb.Append("Header: ");
                foreach (byte b in UnknownHeader)
                    sb.Append($"{b:X2} ");
                sb.AppendLine();
            }
            
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    byte pixel = Pixels[y * Width + x];
                    sb.Append(pixel == 0 ? "." : pixel.ToString("X"));
                }
                sb.AppendLine();
            }
            
            return sb.ToString();
        }

        /// <summary>
        /// Saves the sprite as a raw 8-bit indexed bitmap.
        /// </summary>
        public void SaveAsRaw(string filename)
        {
            File.WriteAllBytes(filename, Pixels);
        }
    }
}
