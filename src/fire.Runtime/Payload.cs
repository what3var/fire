using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace fire.Runtime
{
    /// <summary>Was ein Eintrag im Payload der gepackten Datei ist.</summary>
    public enum PayloadKind : byte
    {
        /// <summary>Das serialisierte LinkedProgram (genau ein Eintrag, Name "program").</summary>
        Program = 0,
        /// <summary>Verwaltete DLL, wird bei Bedarf per AssemblyLoadContext.Resolving geladen. Name = Assembly-Name.</summary>
        Assembly = 1,
        /// <summary>Native Bibliothek (z.B. SDL3.dll), wird beim ersten Zugriff auf die Platte entpackt und geladen.
        /// Name = Dateiname.</summary>
        Native = 2,
    }

    /// <summary>Ein Eintrag der Inhaltsübersicht am Ende der Datei.</summary>
    public sealed class PayloadEntry
    {
        public PayloadKind Kind { get; init; }
        public string Name { get; init; } = "";
        public long Offset { get; init; }
        public int StoredLength { get; init; }
        public int RawLength { get; init; }
        public bool Compressed { get; init; }
        /// <summary>SHA-256 der ENTPACKTEN Daten (Integritätsprüfung beim Lesen).</summary>
        public byte[] Sha256 { get; init; } = Array.Empty<byte>();
    }

    /// <summary>
    /// Format der Nutzdaten, die der Packer HINTER die ausführbare Datei hängt (Programm, benötigte Bridge-DLLs, native
    /// Bibliotheken). Ersetzt die frühere Marker-Suche (`DA 1D`): Lesen geschieht über einen festen Fuß am Dateiende, es
    /// gibt also nichts zu durchsuchen, und die Nutzdaten dürfen beliebige Bytes enthalten.
    ///
    ///   [ausführbare Datei: apphost + .NET-Bundle] [Eintrag 0][Eintrag 1]... [Index] [Fuß]
    ///   Fuß (20 Bytes): int64 Index-Offset, int32 Index-Länge, 8 Byte Kennung "FIREPAK1"
    ///   Index: int32 Anzahl, je Eintrag: byte Kind, string Name, int64 Offset, int32 StoredLength, int32 RawLength,
    ///          byte Compressed, 32 Byte SHA-256
    ///
    /// Die Einträge sind einzeln mit Brotli gepackt (nur wenn es etwas bringt), damit die Datei so klein wird wie möglich
    /// und trotzdem jede DLL einzeln und erst bei Bedarf entpackt wird.
    ///
    /// Diese Klasse (und alles, was die Runtime vor dem Installieren des Laders berührt) darf NICHT auf fire.dll oder
    /// MemoryPack zugreifen - die kommen ja selbst erst aus diesem Payload.
    /// </summary>
    public static class PayloadFile
    {
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("FIREPAK1");
        private const int FooterSize = 8 + 4 + 8;

        // ------------------------------------------------------------
        // Schreiben (Packer)
        // ------------------------------------------------------------

        /// <summary>Hängt die Einträge an das Ende von `stream` an (Position = Ende der ausführbaren Datei) und
        /// schreibt Index und Fuß.</summary>
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

        /// <summary>Brotli-Packen. `Optimal` braucht für eine DLL wenige Millisekunden, `SmallestSize` (Qualität 11) dagegen
        /// Sekunden (SDL3-CS.dll: ~3 s statt ~40 ms) für nur etwa 15-20 % weniger Größe - deshalb Vorgabe `Optimal`; der
        /// Packer nutzt `SmallestSize` nur zusammen mit einem Cache (siehe Packer.CompressCached).</summary>
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

        /// <summary>Liest die Inhaltsübersicht der Datei; null, wenn die Datei keinen Payload trägt.</summary>
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

    /// <summary>Lesezugriff auf die Einträge einer gepackten Datei. Jeder Zugriff öffnet die Datei kurz selbst (teilbar,
    /// threadsicher, und die laufende .exe bleibt nicht länger als nötig geöffnet).</summary>
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

        /// <summary>Liest und entpackt einen Eintrag; null, wenn die Prüfsumme nicht stimmt (beschädigte Datei).</summary>
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
