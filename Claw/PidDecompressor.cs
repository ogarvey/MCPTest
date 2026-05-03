using System;
using System.IO;

namespace MCPTest.Claw
{
    public class PidDecompressor
    {
        public static byte[] Decompress(byte[] fileData)
        {
            if (fileData == null || fileData.Length < 32)
                throw new ArgumentException("Invalid file data");

            using (var reader = new BinaryReader(new MemoryStream(fileData)))
            {
                // Header parsing based on ParsePID (004c0300)
                // Offset 0: Signature (skipped in ParsePID, usually 0x0A000000)
                reader.ReadUInt32(); 
                
                // Offset 4: Flags
                uint flags = reader.ReadUInt32();
                
                // Offset 8: Width
                int width = reader.ReadInt32();
                
                // Offset 12: Height
                int height = reader.ReadInt32();

                // Data starts at offset 32
                reader.BaseStream.Seek(32, SeekOrigin.Begin);
                
                byte[] output = new byte[width * height];
                
                // Try to detect compression type based on flags or data
                // For now, we implement the "Skip and Copy" logic found by manual analysis
                // as it fits the provided data samples better than the PCX RLE found in the binary.
                
                int outputIndex = 0;
                
                while (reader.BaseStream.Position < reader.BaseStream.Length && outputIndex < output.Length)
                {
                    byte b = reader.ReadByte();
                    
                    if ((b & 0x80) == 0x80)
                    {
                        // Skip command (Transparency)
                        int skipCount = b & 0x7F;
                        
                        // Fill with 0 (transparent)
                        for (int i = 0; i < skipCount; i++)
                        {
                            if (outputIndex < output.Length)
                                output[outputIndex++] = 0; // Assuming 0 is transparent index
                        }
                    }
                    else
                    {
                        // Copy command
                        int copyCount = b; // The byte itself is the count (since top bit is 0)
                        
                        for (int i = 0; i < copyCount; i++)
                        {
                            if (reader.BaseStream.Position >= reader.BaseStream.Length)
                                break;
                                
                            byte val = reader.ReadByte();
                            if (outputIndex < output.Length)
                                output[outputIndex++] = val;
                        }
                    }
                }
                
                return output;
            }
        }
    }
}
