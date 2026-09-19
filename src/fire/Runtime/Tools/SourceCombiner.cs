using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace fire.Runtime.Tools
{
    public class SourceCombiner
    {
        public byte[] Combine(IEnumerable<string> sources)
        {
            var bytesList = sources.Select(s => PackFile(s)).ToList();

            var destinationLength = bytesList.Sum(b => b.Length);
            var destination = new byte[destinationLength];

            var offset = 0;
            foreach (var bytes in bytesList)
            {
                Array.Copy(bytes, 0, destination, offset, bytes.Length);
                offset += bytes.Length;
            }

            return destination;
        }

        public byte[] PackFile(string content)
        {
            var contentBytes = Encoding.UTF8.GetBytes(content);
            var hash = SHA256.HashData(contentBytes);

            var destinationLength = contentBytes.Length + hash.Length + 10;

            var pack = new byte[destinationLength];

            pack[0] = 0xF1;
            pack[1] = 0x2E;

            WriteIntToBytes(hash.Length, pack, 2);
            Array.Copy(hash, 0, pack, 6, hash.Length);

            WriteIntToBytes(contentBytes.Length, pack, 6 + hash.Length);
            Array.Copy(contentBytes,0,pack, 10 + hash.Length, contentBytes.Length);
            
            return pack;
        }

        public IEnumerable<string> UnpackFiles(byte[] packedData)
        {
            var offset = 0;
            
            while (offset < packedData.Length)
            {
                if (packedData[offset] != 0xF1 || packedData[offset + 1] != 0x2E)
                    throw new InvalidOperationException("Wrong pack format.");
            
                var hashLength = ReadIntFromBytes(packedData, offset + 2);
                var hash = new byte[hashLength];
                
                Array.Copy(packedData, offset + 6, hash, 0, hashLength);
                
                var contentLength = ReadIntFromBytes(packedData, offset + 6 + hashLength);
                var contentBytes = new byte[contentLength];
                
                Array.Copy(packedData, offset + 10 + hashLength, contentBytes, 0, contentLength);
                
                var computedHash = SHA256.HashData(contentBytes);
                if (!computedHash.SequenceEqual(hash))
                    throw new InvalidOperationException("Hash mismatch. The file may be corrupted.");
                
                yield return Encoding.UTF8.GetString(contentBytes);
                offset += 10 + hashLength + contentLength;
            }
        }

        private int ReadIntFromBytes(byte[] bytes, int offset)
        {
            if (bytes.Length < offset + 4)
                throw new ArgumentException("Not enough bytes to read an int from the specified offset.");
            return bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16) | (bytes[offset + 3] << 24);
        }

        private void WriteIntToBytes(int value, byte[] bytes, int offset)
        {
            if (bytes.Length < offset + 4)
                throw new ArgumentException("Not enough space in the byte array to write an int at the specified offset.");
            bytes[offset] = (byte)(value & 0xFF);
            bytes[offset + 1] = (byte)((value >> 8) & 0xFF);
            bytes[offset + 2] = (byte)((value >> 16) & 0xFF);
            bytes[offset + 3] = (byte)((value >> 24) & 0xFF);
        }
    }
}
