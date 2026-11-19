using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace fire.Runtime
{
    /// <summary>
    /// The payload of a program packed with the VM in C (`forge build --engine vm`, docs/spec/VM_C.md "Gepackte Programme"): the executable `fire`
    /// (native/vm/fire.c) with the program file and the libraries of its packages behind it. Read by `fire` itself (vmPayloadOf in fire.c).
    ///
    ///   [fire] [entry 0] [entry 1] ... [index] [footer]
    ///   footer (24 bytes): uint64 offset of the index, uint32 its length, uint32 the version of the format (1), "FIREVMP1"
    ///   index: uint32 count; per entry: uint8 kind (0 the program file, 1 a library), uint16 length of the name, the name (UTF-8, a file name),
    ///          uint64 offset, uint64 length, uint64 FNV-1a (64 bit) of the bytes
    ///
    /// All numbers little-endian, nothing compressed: the C VM has no decompressor, and its program file is small next to the .NET runtime it replaces.
    /// </summary>
    public static class CVmPayload
    {
        public const byte ProgramKind = 0;
        public const byte LibraryKind = 1;
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("FIREVMP1");
        private const int FooterSize = 24;

        public sealed record Entry(byte Kind, string Name, long Offset, long Length, ulong Hash);

        /// <summary>FNV-1a, 64 bit (what fire.c checks).</summary>
        public static ulong Fnv(ReadOnlySpan<byte> data)
        {
            ulong h = 14695981039346656037UL;
            foreach (byte b in data) { h ^= b; h *= 1099511628211UL; }
            return h;
        }

        /// <summary>Appends the entries and the index behind the end of <paramref name="stream"/> (the executable `fire`).</summary>
        public static void Append(Stream stream, IEnumerable<(byte Kind, string Name, byte[] Data)> items)
        {
            stream.Seek(0, SeekOrigin.End);
            var entries = new List<Entry>();
            foreach (var (kind, name, data) in items)
            {
                if (name.Length == 0 || name.IndexOfAny(new[] { '/', '\\' }) >= 0 || name.Contains("..")) throw new ArgumentException($"'{name}' is no file name for the payload.");
                entries.Add(new Entry(kind, name, stream.Position, data.Length, Fnv(data)));
                stream.Write(data, 0, data.Length);
            }
            long indexOffset = stream.Position;
            var index = new MemoryStream();
            using (var w = new BinaryWriter(index, Encoding.UTF8, leaveOpen: true))
            {
                w.Write((uint)entries.Count);
                foreach (var e in entries)
                {
                    byte[] name = Encoding.UTF8.GetBytes(e.Name);
                    if (name.Length > 255) throw new ArgumentException($"The name '{e.Name}' is too long for the payload.");
                    w.Write(e.Kind);
                    w.Write((ushort)name.Length);
                    w.Write(name);
                    w.Write((ulong)e.Offset);
                    w.Write((ulong)e.Length);
                    w.Write(e.Hash);
                }
            }
            byte[] indexBytes = index.ToArray();
            stream.Write(indexBytes, 0, indexBytes.Length);
            using (var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                w.Write((ulong)indexOffset);
                w.Write((uint)indexBytes.Length);
                w.Write(1u);
                w.Write(Magic);
            }
        }

        /// <summary>The entries of the payload behind <paramref name="path"/>; null when the file carries none (or a damaged one).</summary>
        public static List<Entry>? Open(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < FooterSize) return null;
            fs.Seek(-FooterSize, SeekOrigin.End);
            var footer = new byte[FooterSize];
            fs.ReadExactly(footer, 0, FooterSize);
            if (!footer.AsSpan(16, 8).SequenceEqual(Magic) || BitConverter.ToUInt32(footer, 12) != 1) return null;
            long indexOffset = (long)BitConverter.ToUInt64(footer, 0);
            int indexLength = (int)BitConverter.ToUInt32(footer, 8);
            if (indexOffset < 0 || indexLength < 4 || indexOffset + indexLength > fs.Length - FooterSize) return null;
            fs.Seek(indexOffset, SeekOrigin.Begin);
            var index = new byte[indexLength];
            fs.ReadExactly(index, 0, indexLength);
            var r = new BinaryReader(new MemoryStream(index));
            try
            {
                uint count = r.ReadUInt32();
                var entries = new List<Entry>();
                for (uint i = 0; i < count; i++)
                {
                    byte kind = r.ReadByte();
                    int n = r.ReadUInt16();
                    string name = Encoding.UTF8.GetString(r.ReadBytes(n));
                    entries.Add(new Entry(kind, name, (long)r.ReadUInt64(), (long)r.ReadUInt64(), r.ReadUInt64()));
                }
                return entries;
            }
            catch (EndOfStreamException) { return null; }
        }

        /// <summary>The bytes of an entry, null when they are not those the index names.</summary>
        public static byte[]? Read(string path, Entry entry)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (entry.Offset < 0 || entry.Offset + entry.Length > fs.Length) return null;
            fs.Seek(entry.Offset, SeekOrigin.Begin);
            var data = new byte[entry.Length];
            fs.ReadExactly(data, 0, data.Length);
            return Fnv(data) == entry.Hash ? data : null;
        }

        /// <summary>The program file (.fbc) of a program packed with the C VM; null when <paramref name="path"/> is none.</summary>
        public static byte[]? ReadProgram(string path)
        {
            var entries = Open(path);
            var program = entries?.Find(e => e.Kind == ProgramKind);
            return program == null ? null : Read(path, program);
        }
    }
}
