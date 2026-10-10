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
