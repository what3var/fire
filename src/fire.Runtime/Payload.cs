using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace fire.Runtime
{
    /// <summary>What an entry in the payload of the packed file is.</summary>
    public enum PayloadKind : byte
    {
        /// <summary>The serialised LinkedProgram (exactly one entry, name "program").</summary>
        Program = 0,
        /// <summary>Managed DLL, loaded on demand via AssemblyLoadContext.Resolving. Name = assembly name.</summary>
        Assembly = 1,
        /// <summary>Native library (e.g. SDL3.dll), unpacked to disk and loaded on first access.
        /// Name = file name.</summary>
        Native = 2,
    }

    /// <summary>An entry of the table of contents at the end of the file.</summary>
    public sealed class PayloadEntry
    {
        public PayloadKind Kind { get; init; }
        public string Name { get; init; } = "";
        public long Offset { get; init; }
        public int StoredLength { get; init; }
        public int RawLength { get; init; }
        public bool Compressed { get; init; }
        /// <summary>SHA-256 of the UNPACKED data (integrity check when reading).</summary>
        public byte[] Sha256 { get; init; } = Array.Empty<byte>();
    }

    /// <summary>
    /// Format of the payload that the packer appends BEHIND the executable file (program, required bridge DLLs, native
    /// libraries). Replaces the former marker search (`DA 1D`): reading happens via a fixed footer at the end of the file, so there
    /// is nothing to search, and the payload may contain arbitrary bytes.
    ///
    ///   [executable file: apphost + .NET bundle] [entry 0][entry 1]... [index] [footer]
    ///   Footer (20 bytes): int64 index offset, int32 index length, 8-byte identifier "FIREPAK1"
    ///   Index: int32 count, per entry: byte kind, string name, int64 offset, int32 StoredLength, int32 RawLength,
    ///          byte compressed, 32-byte SHA-256
    ///
    /// The entries are individually Brotli-packed (only if it helps), so that the file becomes as small as possible
    /// and yet every DLL is unpacked individually and only when needed.
    ///
    /// This class (and everything the runtime touches before installing the loader) must NOT access fire.dll or
    /// MemoryPack - after all, those themselves only come from this payload.
    /// </summary>
    public static class PayloadFile
    {
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("FIREPAK1");
        private const int FooterSize = 8 + 4 + 8;

        // ------------------------------------------------------------
        // Schreiben (Packer)
        // ------------------------------------------------------------

        /// <summary>Appends the entries to the end of `stream` (position = end of the executable file) and
        /// writes index and footer.</summary>
        public static void Append(Stream stream, IEnumerable<(PayloadKind Kind, string Name, byte[] Data)> items,
            Func<PayloadKind, byte[], byte[]>? compress = null)
        {
            stream.Seek(0, SeekOrigin.End);
            var entries = new List<PayloadEntry>();
            foreach (var (kind, name, data) in items)
            {
                var packed = compress != null ? compress(kind, data) : Compress(data, CompressionLevel.Optimal);
                bool compressed = packed.Length < data.Length;
                var stored = compressed ? packed : data;
                entries.Add(new PayloadEntry
                {
                    Kind = kind,
                    Name = name,
                    Offset = stream.Position,
                    StoredLength = stored.Length,
                    RawLength = data.Length,
                    Compressed = compressed,
                    Sha256 = SHA256.HashData(data),
                });
                stream.Write(stored, 0, stored.Length);
            }

            long indexOffset = stream.Position;
            using (var bw = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                bw.Write(entries.Count);
                foreach (var e in entries)
                {
                    bw.Write((byte)e.Kind);
                    bw.Write(e.Name);
                    bw.Write(e.Offset);
                    bw.Write(e.StoredLength);
                    bw.Write(e.RawLength);
                    bw.Write(e.Compressed);
                    bw.Write(e.Sha256);
                }
                bw.Flush();
                long indexLength = stream.Position - indexOffset;
                bw.Write(indexOffset);
                bw.Write((int)indexLength);
                bw.Write(Magic);
            }
        }

        /// <summary>Brotli packing. `Optimal` needs a few milliseconds for a DLL, `SmallestSize` (quality 11), by contrast,
        /// seconds (SDL3-CS.dll: ~3 s instead of ~40 ms) for only about 15-20 % less size - hence the default `Optimal`; the
        /// packer uses `SmallestSize` only together with a cache (see Packer.CompressCached).</summary>
        public static byte[] Compress(byte[] data, CompressionLevel level)
        {
            using var ms = new MemoryStream();
            using (var br = new BrotliStream(ms, level, leaveOpen: true))
                br.Write(data, 0, data.Length);
            return ms.ToArray();
        }

        // ------------------------------------------------------------
        // Lesen (Runtime)
        // ------------------------------------------------------------

        /// <summary>Reads the table of contents of the file; null if the file carries no payload.</summary>
        public static PayloadReader? Open(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < FooterSize) return null;

            fs.Seek(-FooterSize, SeekOrigin.End);
            var footer = new byte[FooterSize];
            fs.ReadExactly(footer, 0, FooterSize);
            for (int i = 0; i < Magic.Length; i++)
                if (footer[12 + i] != Magic[i]) return null;

            long indexOffset = BitConverter.ToInt64(footer, 0);
            int indexLength = BitConverter.ToInt32(footer, 8);
            if (indexOffset < 0 || indexLength <= 0 || indexOffset + indexLength > fs.Length - FooterSize)
                return null;

            fs.Seek(indexOffset, SeekOrigin.Begin);
            var index = new byte[indexLength];
            fs.ReadExactly(index, 0, indexLength);

            var entries = new List<PayloadEntry>();
            using (var br = new BinaryReader(new MemoryStream(index), Encoding.UTF8))
            {
                int count = br.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    var kind = (PayloadKind)br.ReadByte();
                    var name = br.ReadString();
                    var offset = br.ReadInt64();
                    var stored = br.ReadInt32();
                    var raw = br.ReadInt32();
                    var compressed = br.ReadBoolean();
                    var hash = br.ReadBytes(32);
                    if (offset < 0 || stored < 0 || raw < 0 || offset + stored > indexOffset)
                        return null;
                    entries.Add(new PayloadEntry { Kind = kind, Name = name, Offset = offset, StoredLength = stored, RawLength = raw, Compressed = compressed, Sha256 = hash });
                }
            }
            return new PayloadReader(path, entries);
        }
    }

    /// <summary>Read access to the entries of a packed file. Every access briefly opens the file itself (shareable,
    /// thread-safe, and the running .exe is not kept open longer than necessary).</summary>
    public sealed class PayloadReader
    {
        private readonly string _path;
        public IReadOnlyList<PayloadEntry> Entries { get; }

        internal PayloadReader(string path, List<PayloadEntry> entries)
        {
            _path = path;
            Entries = entries;
        }

        public PayloadEntry? Find(PayloadKind kind, string name, StringComparison comparison = StringComparison.OrdinalIgnoreCase)
        {
            foreach (var e in Entries)
                if (e.Kind == kind && string.Equals(e.Name, name, comparison))
                    return e;
            return null;
        }

        /// <summary>Reads and unpacks an entry; null if the checksum does not match (damaged file).</summary>
        public byte[]? Read(PayloadEntry entry)
        {
            var stored = new byte[entry.StoredLength];
            using (var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                fs.Seek(entry.Offset, SeekOrigin.Begin);
                fs.ReadExactly(stored, 0, stored.Length);
            }

            byte[] raw;
            if (entry.Compressed)
            {
                raw = new byte[entry.RawLength];
                using var br = new BrotliStream(new MemoryStream(stored), CompressionMode.Decompress);
                br.ReadExactly(raw, 0, raw.Length);
            }
            else raw = stored;

            return SHA256.HashData(raw).AsSpan().SequenceEqual(entry.Sha256) ? raw : null;
        }
    }
}
