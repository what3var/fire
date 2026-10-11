using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace fire.Runtime
{
    /// <summary>
    /// Builds from the apphost (the small start .exe of the .NET SDK) and a few files a .NET "single-file" bundle
    /// (format version 6, as `dotnet publish -p:PublishSingleFile=true` produces it for framework-dependent apps).
    /// The result is ONE file that the host starts directly - without a fire.Runtime.dll lying next to it.
    ///
    /// Only the minimum is built here (runtime.dll + runtimeconfig.json); everything else comes as a payload behind it
    /// (see PayloadFile). The host reads only from the header offset entered in the apphost, data behind the
    /// bundle does not disturb it.
    ///
    /// Format (see dotnet/runtime, Microsoft.NET.HostModel.Bundle):
    ///   [apphost][file 0][file 1]...[header]
    ///   In the apphost there is an 8-byte gap directly in front of a fixed 32-byte identifier (SHA-256 of ".net core bundle"),
    ///   where the header offset is entered.
    ///   Header: uint32 major(6), uint32 minor(0), int32 count, string BundleId, int64 DepsJsonOffset/-Size,
    ///           int64 RuntimeConfigOffset/-Size, uint64 flags, per file: int64 offset, int64 size, int64 compressed size,
    ///           byte type, string path
    /// </summary>
    public static class BundleWriter
    {
        private static readonly byte[] Signature =
        {
            0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38, 0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
            0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18, 0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae,
        };

        /// <summary>The identifier the apphost carries in front of which the header offset stands (for tests and tools).</summary>
        public static ReadOnlySpan<byte> BundleSignature => Signature;

        /// <summary>The files of a finished bundle: path, absolute offset and size.</summary>
        public static List<(string Path, long Offset, long Size)> ReadEntries(byte[] file)
        {
            int sigPos = IndexOf(file, Signature);
            if (sigPos < 8) throw new InvalidOperationException("The file does not contain a bundle signature.");
            using var ms = new MemoryStream(file, writable: false);
            using var br = new BinaryReader(ms, Encoding.UTF8);
            ms.Position = BitConverter.ToInt64(file, sigPos - 8);
            uint major = br.ReadUInt32();
            br.ReadUInt32();
            int count = br.ReadInt32();
            br.ReadString();
            if (major >= 2) { br.ReadInt64(); br.ReadInt64(); br.ReadInt64(); br.ReadInt64(); br.ReadUInt64(); }
            var entries = new List<(string, long, long)>();
            for (int i = 0; i < count; i++)
            {
                long offset = br.ReadInt64(), size = br.ReadInt64();
                if (major >= 6) br.ReadInt64();
                br.ReadByte();
                entries.Add((br.ReadString(), offset, size));
            }
            return entries;
        }

        public enum FileType : byte { Unknown = 0, Assembly = 1, NativeBinary = 2, DepsJson = 3, RuntimeConfigJson = 4, Symbols = 5 }

        public sealed record BundleFile(string RelativePath, FileType Type, byte[] Data);

        /// <summary>Writes the bundle to `outFile`; `apphost` is NOT modified in the process (only the copy in the
        /// result carries the header offset).</summary>
        public static void Write(byte[] apphost, IReadOnlyList<BundleFile> files, string outFile)
        {
            int sigPos = IndexOf(apphost, Signature);
            if (sigPos < 8)
                throw new InvalidOperationException("The apphost does not contain a bundle signature (wrong start .exe?).");

            using var fs = new FileStream(outFile, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            fs.Write(apphost, 0, apphost.Length);

            var offsets = new long[files.Count];
            for (int i = 0; i < files.Count; i++)
            {
                offsets[i] = fs.Position;
                fs.Write(files[i].Data, 0, files[i].Data.Length);
            }

            long headerOffset = fs.Position;
            using (var bw = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true))
            {
                bw.Write(6u);
                bw.Write(0u);
                bw.Write(files.Count);
                bw.Write(BundleId(files));

                (long off, long size) deps = (0, 0), config = (0, 0);
                for (int i = 0; i < files.Count; i++)
                {
                    if (files[i].Type == FileType.DepsJson) deps = (offsets[i], files[i].Data.Length);
                    if (files[i].Type == FileType.RuntimeConfigJson) config = (offsets[i], files[i].Data.Length);
                }
                bw.Write(deps.off);
                bw.Write(deps.size);
                bw.Write(config.off);
                bw.Write(config.size);
                bw.Write(0UL); // Flags: nothing (no compression - in the bundle it applies only to self-contained)

                for (int i = 0; i < files.Count; i++)
                {
                    bw.Write(offsets[i]);
                    bw.Write((long)files[i].Data.Length);
                    bw.Write(0L);
                    bw.Write((byte)files[i].Type);
                    bw.Write(files[i].RelativePath);
                }
                bw.Flush();
            }

            fs.Seek(sigPos - 8, SeekOrigin.Begin);
            fs.Write(BitConverter.GetBytes(headerOffset), 0, 8);
            fs.Flush();
        }

        /// <summary>true if the file is already a bundle (header offset in the apphost not equal to 0).</summary>
        public static bool IsBundle(byte[] apphost)
        {
            int sigPos = IndexOf(apphost, Signature);
            return sigPos >= 8 && BitConverter.ToInt64(apphost, sigPos - 8) != 0;
        }

        /// <summary>The size of the PE part of a Windows executable (end of the last section); everything behind it is appended data (a bundle).
        /// -1 if the file is not a PE file.</summary>
        public static long PeEnd(byte[] file)
        {
            if (file.Length < 0x40 || file[0] != 'M' || file[1] != 'Z') return -1;
            int pe = BitConverter.ToInt32(file, 0x3C);
            if (pe < 0 || pe + 24 > file.Length || file[pe] != 'P' || file[pe + 1] != 'E') return -1;
            int sections = BitConverter.ToUInt16(file, pe + 6);
            int optionalSize = BitConverter.ToUInt16(file, pe + 20);
            int table = pe + 24 + optionalSize;
            long end = 0;
            for (int i = 0; i < sections; i++)
            {
                int at = table + i * 40;
                if (at + 40 > file.Length) return -1;
                end = Math.Max(end, (long)BitConverter.ToUInt32(file, at + 20) + BitConverter.ToUInt32(file, at + 16));
            }
            return end;
        }

        /// <summary>Moves a finished bundle (apphost with the bundle behind it) by `delta` bytes after the PE part has grown or shrunk: the header offset in the apphost and
        /// every offset in the bundle header are absolute file positions. `file` is the NEW file (the changed PE part plus the unchanged rest), `oldHeaderOffset` the header
        /// offset from before the change.</summary>
        public static void ShiftBundle(byte[] file, long oldHeaderOffset, long delta)
        {
            int sigPos = IndexOf(file, Signature);
            if (sigPos < 8) throw new InvalidOperationException("The file does not contain a bundle signature.");
            long header = oldHeaderOffset + delta;
            BitConverter.GetBytes(header).CopyTo(file, sigPos - 8);

            using var ms = new MemoryStream(file, writable: false);
            using var br = new BinaryReader(ms, Encoding.UTF8);
            ms.Position = header;
            uint major = br.ReadUInt32();
            br.ReadUInt32();
            int count = br.ReadInt32();
            br.ReadString();
            void Shift()
            {
                long at = ms.Position;
                long value = br.ReadInt64();
                if (value != 0) BitConverter.GetBytes(value + delta).CopyTo(file, at);
            }
            if (major >= 2)
            {
                Shift(); br.ReadInt64();   // deps.json: offset, size
                Shift(); br.ReadInt64();   // runtimeconfig.json: offset, size
                br.ReadUInt64();           // flags
            }
            for (int i = 0; i < count; i++)
            {
                Shift();                   // offset
                br.ReadInt64();            // size
                if (major >= 6) br.ReadInt64();   // compressed size
                br.ReadByte();             // type
                br.ReadString();           // path
            }
        }

        /// <summary>Runs `edit` (icon, version info: it rewrites the file) on a bundle that is already finished, e.g. the single-file `runtime.exe` of a self-contained publish.
        /// Editing the PE resources drops what is appended to the file, so the PE part is cut off, edited, and the bundle put back behind it with all offsets moved.
        /// A file that is no bundle is simply edited.</summary>
        public static void EditBundled(string path, Action<string> edit)
        {
            var original = File.ReadAllBytes(path);
            long peEnd = PeEnd(original);
            if (peEnd <= 0 || peEnd >= original.Length || !IsBundle(original)) { edit(path); return; }

            int sigPos = IndexOf(original, Signature);
            long headerOffset = BitConverter.ToInt64(original, sigPos - 8);
            var overlay = original.AsSpan((int)peEnd).ToArray();
            using (var fs = new FileStream(path, FileMode.Truncate, FileAccess.Write)) fs.Write(original, 0, (int)peEnd);
            edit(path);

            var pe = File.ReadAllBytes(path);
            long delta = pe.Length - peEnd;
            var result = new byte[pe.Length + overlay.Length];
            pe.CopyTo(result, 0);
            overlay.CopyTo(result, pe.Length);
            if (delta != 0) ShiftBundle(result, headerOffset, delta);
            else BitConverter.GetBytes(headerOffset).CopyTo(result, IndexOf(result, Signature) - 8);
            File.WriteAllBytes(path, result);
        }

        private static string BundleId(IReadOnlyList<BundleFile> files)
        {
            using var sha = SHA256.Create();
            foreach (var f in files)
            {
                var name = Encoding.UTF8.GetBytes(f.RelativePath);
                sha.TransformBlock(name, 0, name.Length, null, 0);
                sha.TransformBlock(f.Data, 0, f.Data.Length, null, 0);
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return Convert.ToBase64String(sha.Hash!, 0, 12);
        }

        private static int IndexOf(byte[] data, byte[] pattern) => data.AsSpan().IndexOf(pattern);
    }
}
