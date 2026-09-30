using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace fire.Runtime
{
    /// <summary>
    /// Baut aus dem apphost (der kleinen Start-.exe des .NET SDK) und ein paar Dateien ein .NET-"Single-File"-Bundle
    /// (Format Version 6, wie `dotnet publish -p:PublishSingleFile=true` es für framework-abhängige Apps erzeugt).
    /// Das Ergebnis ist EINE Datei, die der Host direkt startet - ohne daneben liegende fire.Runtime.dll.
    ///
    /// Gebaut wird hier nur das Minimum (fire.Runtime.dll + runtimeconfig.json); alles Übrige kommt als Payload dahinter
    /// (siehe PayloadFile). Der Host liest ausschließlich ab dem im apphost eingetragenen Header-Offset, Daten hinter dem
    /// Bundle stören ihn nicht.
    ///
    /// Format (siehe dotnet/runtime, Microsoft.NET.HostModel.Bundle):
    ///   [apphost][Datei 0][Datei 1]...[Header]
    ///   Im apphost steht eine 8-Byte-Lücke direkt vor einer festen 32-Byte-Kennung (SHA-256 von ".net core bundle"),
    ///   dort wird der Header-Offset eingetragen.
    ///   Header: uint32 Major(6), uint32 Minor(0), int32 Anzahl, string BundleId, int64 DepsJsonOffset/-Size,
    ///           int64 RuntimeConfigOffset/-Size, uint64 Flags, je Datei: int64 Offset, int64 Size, int64 CompressedSize,
    ///           byte Typ, string Pfad
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

        /// <summary>Schreibt das Bundle nach `outFile`; `apphost` wird dabei NICHT verändert (nur die Kopie im
        /// Ergebnis trägt den Header-Offset).</summary>
        public static void Write(byte[] apphost, IReadOnlyList<BundleFile> files, string outFile)
        {
            int sigPos = IndexOf(apphost, Signature);
            if (sigPos < 8)
                throw new InvalidOperationException("Der apphost enthält keine Bundle-Kennung (falsche Start-.exe?).");

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
                bw.Write(0UL); // Flags: nichts (keine Kompression - die gilt im Bundle nur für self-contained)

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

        /// <summary>true, wenn die Datei schon ein Bundle ist (Header-Offset im apphost ungleich 0).</summary>
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
