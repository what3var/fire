using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace fire.Runtime
{
    public class Packer
    {
        string runtimeAssembly = "fire.Runtime.exe";
        int copyBlockSize = 4096;
        byte[] markerBytes = [0xDA, 0x1D];

        int markerSearchStart = 0;

        public byte[]? Unpack(string inFile)
        {
            using var fsIn = new FileStream(inFile, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite);

            var markerPos = FindMarkerIndex(fsIn, markerBytes);

            if (markerPos == null)
                return null;

            Debug.Print($"Found marker at {markerPos:X}");

            fsIn.Seek(markerPos.Value + 2, SeekOrigin.Begin);

            var hashLengthHeader = new byte[4];

            fsIn.ReadExactly(hashLengthHeader, 0, 4);

            var hashLength = bytetoint(hashLengthHeader);

            var hash = new byte[hashLength];

            fsIn.ReadExactly(hash, 0, hashLength);

            var binLengthHeader = new byte[4];

            fsIn.ReadExactly(binLengthHeader, 0, 4);

            var binLength = bytetoint(binLengthHeader);

            var bin = new byte[binLength];

            var index = 0;

            while (fsIn.Position < fsIn.Length && index < binLength)
            {
                var remaining = binLength - index;

                var remainingFileLen = fsIn.Length - fsIn.Position;

                if (remainingFileLen < remaining)
                    remaining = (int)remainingFileLen;

                index += fsIn.Read(bin, index, remaining);
            }

            using var sha256Hash = SHA256.Create();

            var newHash = sha256Hash.ComputeHash(bin);

            fsIn.Close();

            if (!hash.SequenceEqual(newHash))
                return null;

            return bin;
        }

        public void Pack(byte[] bin, string outFile)
        {
            using var fsIn = new FileStream(Path.Combine(Path.GetDirectoryName(Environment.ProcessPath), runtimeAssembly), 
                FileMode.Open, 
                FileAccess.Read,
                FileShare.ReadWrite);
            
            var runtime = new byte[fsIn.Length];
            
            var index = 0;

            while (index < fsIn.Length)
            {
                index += fsIn.Read(runtime, index, copyBlockSize);
            }

            using var fsOut = new FileStream(outFile, FileMode.Create, FileAccess.Write);
            using var sha256Hash = SHA256.Create();

            var hash = sha256Hash.ComputeHash(bin);
            var hashLengthHeader = inttobyte(hash.Length);
            var binLengthHeader = inttobyte(bin.Length);

            fsOut.Write(runtime, 0, runtime.Length);
            fsOut.Write(markerBytes, 0, markerBytes.Length);
            fsOut.Write(hashLengthHeader, 0, hashLengthHeader.Length);
            fsOut.Write(hash, 0, hash.Length);
            fsOut.Write(binLengthHeader, 0, binLengthHeader.Length);
            fsOut.Write(bin, 0, bin.Length);

            fsOut.Flush();

            fsOut.Close();
            fsIn.Close();
        }

        public static byte[] inttobyte(int value)
        {
            var result = new byte[4];
            result = [(byte)(value), (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24)];
            return result;
        }

        public static int bytetoint(byte[] invalue)
        {
            return (invalue[0]) | (invalue[1] << 8) | (invalue[2] << 16) | (invalue[3] << 24);
        }


        public static void PackProgram(LinkedProgram program, string outName)
        {
            try
            {
                var packer = new Packer();

                Debug.WriteLine(string.Join(",", program.Program.TopLevel.Code));
                var bin = MemoryPack.MemoryPackSerializer.Serialize(program);

                Debug.WriteLine($"Serialized {bin.Length} bytes.");

                packer.Pack(bin, outName);

                var decompressedBin = MemoryPack.MemoryPackSerializer.Deserialize<LinkedProgram>(bin);
                if (decompressedBin != null)
                Debug.WriteLine(string.Join(",", decompressedBin.Program.TopLevel.Code));
                Debug.WriteLine($"Wrote {outName} to disk");
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }

        public static LinkedProgram? UnpackProgram(string fileName)
        {
            var packer = new Packer();

            var bin = packer.Unpack(fileName);

            if (bin == null)
                return null;

            var program = MemoryPack.MemoryPackSerializer.Deserialize<LinkedProgram>(bin);

            if (program == null)
                return null;

            program.Program.RelinkAfterDeserialize();

            return program;
        }

        private long? FindMarkerIndex(Stream fileStream, byte[] marker)
        {
            //using var reader = new StreamReader(fileStream);

            fileStream.Seek(markerSearchStart, SeekOrigin.Begin);
            
            while (fileStream.Position < fileStream.Length)
            {
                long? matchPosition = null;

                for (int j = 0; j < marker.Length; j++)
                {
                    if (fileStream.ReadByte() != marker[j])
                    {
                        matchPosition = null;
                        break;
                    }
                    else if (j == 0)
                    {
                        matchPosition = fileStream.Position - 1;
                    }
                }
                if (matchPosition != null) return matchPosition;
            }

            return null;
        }
    }
}
