using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace fire.Utilities
{
    /// <summary>
    /// Ändert Icon und Versions-Metadaten (Produktname, Firma, Version, ...)
    /// einer bereits gebauten Windows-PE-Datei (.exe/.dll) NACHTRÄGLICH.
    ///
    /// Nutzt dieselben Win32-Resource-Update-APIs (BeginUpdateResource /
    /// UpdateResource / EndUpdateResource), die auch Resource Hacker oder
    /// rcedit verwenden, statt den PE-Header selbst zu parsen - deutlich
    /// robuster als eigenes Byte-Patchen des Dateiformats.
    ///
    /// Läuft NUR unter Windows (reines P/Invoke, keine Cross-Platform-
    /// Alternative dafür). Die Zieldatei darf währenddessen nicht von einem
    /// anderen Prozess geöffnet/gesperrt sein (bei einer laufenden .exe geht
    /// das i.d.R. nicht - dann vorher schließen oder eine Kopie bearbeiten).
    /// </summary>
    public static class PeResourceEditor
    {
        // ------------------------------------------------------------
        // Win32 P/Invoke
        // ------------------------------------------------------------

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr BeginUpdateResource(string pFileName,
            [MarshalAs(UnmanagedType.Bool)] bool bDeleteExistingResources);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateResource(IntPtr hUpdate, IntPtr lpType, IntPtr lpName,
            ushort wLanguage, byte[]? lpData, uint cbData);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool EndUpdateResource(IntPtr hUpdate,
            [MarshalAs(UnmanagedType.Bool)] bool fDiscard);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr hModule);

        private delegate bool EnumResNameProc(IntPtr hModule, IntPtr lpType, IntPtr lpName, IntPtr lParam);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool EnumResourceNames(IntPtr hModule, IntPtr lpType,
            EnumResNameProc lpEnumFunc, IntPtr lParam);

        private const uint LOAD_LIBRARY_AS_DATAFILE = 0x00000002;

        private static readonly IntPtr RT_ICON = (IntPtr)3;
        private static readonly IntPtr RT_GROUP_ICON = (IntPtr)14;
        private static readonly IntPtr RT_VERSION = (IntPtr)16;

        private const ushort LANG_NEUTRAL = 0;

        // Sprache/Codepage für die StringTable der Versionsinfo - "Englisch
        // (USA) / Unicode" ist die weitaus gebräuchlichste Kombination und
        // wird von jedem Anzeigeprogramm (Explorer-Eigenschaften, etc.)
        // sicher erkannt, unabhängig von der Systemsprache.
        private const ushort LANG_EN_US = 0x0409;
        private const ushort CODEPAGE_UNICODE = 0x04B0;

        // ------------------------------------------------------------
        // Icon ersetzen
        // ------------------------------------------------------------

        /// <summary>
        /// Ersetzt das/die Icon(s) von <paramref name="exePath"/> durch den
        /// Inhalt von <paramref name="icoPath"/> (eine gewöhnliche .ico-Datei,
        /// kann mehrere Auflösungen/Farbtiefen enthalten - alle werden
        /// übernommen). Ersetzt dabei GEZIELT die Icon-Gruppe(n), die die Datei
        /// schon hat (per Enumeration herausgefunden, üblicherweise genau
        /// eine) - legt nur dann eine neue mit ID 1 an, wenn die Datei bisher
        /// gar kein Icon hatte.
        /// </summary>
        public static void SetIcon(string exePath, string icoPath)
        {
            var images = ReadIconImages(icoPath);
            var existingGroupIds = FindResourceNames(exePath, RT_GROUP_ICON);
            var existingIconIds = FindResourceNames(exePath, RT_ICON);

            IntPtr handle = BeginUpdateResource(exePath, bDeleteExistingResources: false);
            if (handle == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"BeginUpdateResource for '{exePath}' failed.");

            try
            {
                // Alte einzelne Icon-Bilder zuerst entfernen - sonst blieben
                // Bilder eines vorherigen, GRÖSSEREN Icon-Satzes (z.B. mehr
                // Auflösungen) als Datenleichen in der Datei liegen, auch wenn
                // keine Gruppe mehr darauf zeigt.
                foreach (var id in existingIconIds)
                    Update(handle, RT_ICON, id, null);

                // Neue Bilder unter frischen IDs 1..N schreiben - die alten
                // Bild-IDs sind irrelevant, nur die Gruppe unten muss auf die
                // NEUEN IDs zeigen.
                var newImageIds = new List<ushort>();
                for (int i = 0; i < images.Count; i++)
                {
                    ushort id = (ushort)(i + 1);
                    Update(handle, RT_ICON, (IntPtr)id, images[i].Data);
                    newImageIds.Add(id);
                }

                byte[] groupData = BuildGroupIconResource(images, newImageIds);
                var groupIds = existingGroupIds.Count > 0 ? existingGroupIds : new List<IntPtr> { (IntPtr)1 };
                foreach (var groupId in groupIds)
                    Update(handle, RT_GROUP_ICON, groupId, groupData);

                if (!EndUpdateResource(handle, fDiscard: false))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "EndUpdateResource failed.");
            }
            catch
            {
                EndUpdateResource(handle, fDiscard: true); // Änderungen verwerfen, Datei bleibt unangetastet
                throw;
            }
        }

        // ------------------------------------------------------------
        // Versions-Metadaten setzen
        // ------------------------------------------------------------

        /// <summary>
        /// Setzt/ersetzt die Versions-Metadaten (Dateiversion, Produktname,
        /// Firma, Copyright, Beschreibung, ...) von <paramref name="exePath"/> -
        /// überschreibt eine vorhandene VERSIONINFO-Ressource komplett, legt bei
        /// Bedarf eine neue an.
        /// </summary>
        public static void SetVersionInfo(string exePath, PeVersionInfo info)
        {
            byte[] data = BuildVersionInfoResource(info);

            IntPtr handle = BeginUpdateResource(exePath, bDeleteExistingResources: false);
            if (handle == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"BeginUpdateResource for '{exePath}' failed.");

            try
            {
                Update(handle, RT_VERSION, (IntPtr)1, data);
                if (!EndUpdateResource(handle, fDiscard: false))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "EndUpdateResource failed.");
            }
            catch
            {
                EndUpdateResource(handle, fDiscard: true);
                throw;
            }
        }

        // ------------------------------------------------------------
        // Gemeinsame Hilfsfunktionen
        // ------------------------------------------------------------

        private static void Update(IntPtr handle, IntPtr type, IntPtr name, byte[]? data)
        {
            uint length = data != null ? (uint)data.Length : 0;
            if (!UpdateResource(handle, type, name, LANG_NEUTRAL, data, length))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateResource failed.");
        }

        /// <summary>Listet alle Ressourcen-IDs eines bestimmten Typs, die
        /// <paramref name="exePath"/> JETZT schon hat (z.B. vorhandene
        /// Icon-Gruppen) - lädt die Datei dafür separat, NUR zum Lesen, als
        /// Daten-Modul (LOAD_LIBRARY_AS_DATAFILE führt keinen Code aus und
        /// bindet keine Imports).</summary>
        private static List<IntPtr> FindResourceNames(string exePath, IntPtr resourceType)
        {
            var names = new List<IntPtr>();
            IntPtr hModule = LoadLibraryEx(exePath, IntPtr.Zero, LOAD_LIBRARY_AS_DATAFILE);
            if (hModule == IntPtr.Zero) return names; // Datei evtl. noch ganz ohne Ressourcen - kein Fehler

            try
            {
                EnumResourceNames(hModule, resourceType, (h, type, name, param) =>
                {
                    names.Add(name);
                    return true;
                }, IntPtr.Zero);
            }
            finally
            {
                FreeLibrary(hModule);
            }
            return names;
        }

        // ------------------------------------------------------------
        // .ico-Datei einlesen und ins PE-Ressourcenformat umwandeln
        //
        // Der Unterschied zum rohen .ico-Dateiformat: dort verweist jeder
        // ICONDIRENTRY per Byte-OFFSET auf sein Bild INNERHALB derselben
        // Datei. In einer PE-Ressource gibt es das nicht - stattdessen
        // liegt jedes Bild als EIGENE RT_ICON-Ressource vor, und der
        // "GRPICONDIRENTRY" verweist per Ressourcen-ID darauf.
        // ------------------------------------------------------------

        private sealed record IconImage(byte Width, byte Height, byte ColorCount, ushort Planes, ushort BitCount, byte[] Data);

        private static List<IconImage> ReadIconImages(string icoPath)
        {
            using var stream = File.OpenRead(icoPath);
            using var reader = new BinaryReader(stream);

            reader.ReadUInt16(); // reserved, immer 0
            ushort type = reader.ReadUInt16();
            if (type != 1)
                throw new InvalidDataException($"'{icoPath}' is not a valid .ico file (type {type}, expected 1).");
            ushort count = reader.ReadUInt16();

            var entries = new List<(byte Width, byte Height, byte ColorCount, ushort Planes, ushort BitCount, uint BytesInRes, uint ImageOffset)>();
            for (int i = 0; i < count; i++)
            {
                byte width = reader.ReadByte();
                byte height = reader.ReadByte();
                byte colorCount = reader.ReadByte();
                reader.ReadByte(); // reserved
                ushort planes = reader.ReadUInt16();
                ushort bitCount = reader.ReadUInt16();
                uint bytesInRes = reader.ReadUInt32();
                uint imageOffset = reader.ReadUInt32();
                entries.Add((width, height, colorCount, planes, bitCount, bytesInRes, imageOffset));
            }

            var images = new List<IconImage>();
            foreach (var e in entries)
            {
                stream.Seek(e.ImageOffset, SeekOrigin.Begin);
                byte[] data = reader.ReadBytes((int)e.BytesInRes);
                images.Add(new IconImage(e.Width, e.Height, e.ColorCount, e.Planes, e.BitCount, data));
            }
            return images;
        }

        private static byte[] BuildGroupIconResource(List<IconImage> images, List<ushort> imageIds)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);

            writer.Write((ushort)0); // reserved
            writer.Write((ushort)1); // type = icon
            writer.Write((ushort)images.Count);

            for (int i = 0; i < images.Count; i++)
            {
                var img = images[i];
                writer.Write(img.Width);
                writer.Write(img.Height);
                writer.Write(img.ColorCount);
                writer.Write((byte)0); // reserved
                writer.Write(img.Planes);
                writer.Write(img.BitCount);
                writer.Write((uint)img.Data.Length);
                writer.Write(imageIds[i]); // ID statt Datei-Offset - der Unterschied zum .ico-Format
            }

            return stream.ToArray();
        }

        // ------------------------------------------------------------
        // VS_VERSIONINFO-Ressource bauen (RT_VERSION)
        //
        // Verschachteltes Binärformat: VS_VERSIONINFO enthält die feste
        // VS_FIXEDFILEINFO-Struktur (die binäre Versionsnummer, die Windows
        // z.B. im "Details"-Dialog oben zeigt) plus zwei Kind-Blöcke,
        // StringFileInfo (freier Text wie Produktname/Firma) und
        // VarFileInfo (welche Sprache/Codepage die StringTable hat).
        // Jeder Block trägt seine eigene Gesamtlänge VORNE im Header -
        // die steht erst fest, NACHDEM alle Kind-Elemente geschrieben
        // sind, deshalb BeginBlock/EndBlock: erst ein Platzhalter, nach
        // dem Schreiben der Kinder wird die echte Länge nachgetragen
        // (Seek zurück, schreiben, wieder vor).
        // ------------------------------------------------------------

        private static byte[] BuildVersionInfoResource(PeVersionInfo info)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);

            long rootLengthPos = BeginBlock(writer, "VS_VERSION_INFO", valueIsText: false);

            // VS_FIXEDFILEINFO - BeginBlock hat direkt davor schon per
            // Align4() auf eine 4er-Grenze aufgefüllt (der Name
            // "VS_VERSION_INFO" selbst reicht dafür NICHT aus: 15 Zeichen +
            // Nullterminator = 32 Byte, plus 6 Byte für die drei
            // vorangehenden WORDs = 38 Byte, erst das Align4() rundet auf
            // 40 auf) - ab hier ist also garantiert 4-Byte-ausgerichtet.
            writer.Write(0xFEEF04BDu);                        // dwSignature
            writer.Write(0x00010000u);                        // dwStrucVersion (1.0)
            writer.Write(PackVersion(info.FileVersion, high: true));
            writer.Write(PackVersion(info.FileVersion, high: false));
            writer.Write(PackVersion(info.ProductVersion, high: true));
            writer.Write(PackVersion(info.ProductVersion, high: false));
            writer.Write(0x0u);                                // dwFileFlagsMask
            writer.Write(0x0u);                                // dwFileFlags
            writer.Write(0x00000004u);                         // dwFileOS = VOS_NT_WINDOWS32
            writer.Write(0x00000001u);                         // dwFileType = VFT_APP
            writer.Write((uint) info.Subsystem);                                // dwFileSubtype
            writer.Write(0x0u);                                // dwFileDateMS
            writer.Write(0x0u);                                // dwFileDateLS
            // wValueLength von VS_VERSIONINFO selbst (Größe von VS_FIXEDFILEINFO
            // in Byte) wird erst hier, nachträglich, ins schon geschriebene
            // Feld eingetragen (siehe BeginBlock/EndBlock-Kommentar).
            PatchValueLength(writer, rootLengthPos, 13 * 4);
            Align4(writer);

            // StringFileInfo -> genau EINE StringTable (Sprache/Codepage als
            // 8-stelliger Hex-Schlüssel) -> beliebig viele String-Einträge.
            long stringFileInfoPos = BeginBlock(writer, "StringFileInfo", valueIsText: true);
            string langKey = $"{LANG_EN_US:X4}{CODEPAGE_UNICODE:X4}";
            long stringTablePos = BeginBlock(writer, langKey, valueIsText: true);
            foreach (var (key, value) in info.EnumerateStrings())
                WriteStringEntry(writer, key, value ?? "");
            EndBlock(writer, stringTablePos);
            EndBlock(writer, stringFileInfoPos);

            // VarFileInfo/Translation - sagt Anzeigeprogrammen, unter
            // welcher Sprache/Codepage sie die StringTable oben finden.
            long varFileInfoPos = BeginBlock(writer, "VarFileInfo", valueIsText: false);
            long translationPos = BeginBlock(writer, "Translation", valueIsText: false);
            writer.Write(LANG_EN_US);
            writer.Write(CODEPAGE_UNICODE);
            PatchValueLength(writer, translationPos, 4);
            EndBlock(writer, translationPos);
            EndBlock(writer, varFileInfoPos);

            EndBlock(writer, rootLengthPos);
            return stream.ToArray();
        }

        /// <summary>Schreibt Länge(Platzhalter)/wValueLength(0)/wType/Namen
        /// eines verschachtelten VERSIONINFO-Blocks und richtet danach auf
        /// 4 Byte aus. Liefert die Position des Längenfelds für EndBlock/
        /// PatchValueLength zurück.</summary>
        private static long BeginBlock(BinaryWriter writer, string key, bool valueIsText)
        {
            long lengthPos = writer.BaseStream.Position;
            writer.Write((ushort)0); // wLength - Platzhalter, siehe EndBlock
            writer.Write((ushort)0); // wValueLength - meist 0 (reiner Container), siehe PatchValueLength für Ausnahmen
            writer.Write((ushort)(valueIsText ? 1 : 0)); // wType
            WriteUnicodeZ(writer, key);
            Align4(writer);
            return lengthPos;
        }

        private static void PatchValueLength(BinaryWriter writer, long blockStartPos, int valueLengthBytes)
        {
            long current = writer.BaseStream.Position;
            writer.Seek((int)blockStartPos + 2, SeekOrigin.Begin); // wValueLength liegt direkt nach wLength
            writer.Write((ushort)valueLengthBytes);
            writer.Seek((int)current, SeekOrigin.Begin);
        }

        private static void EndBlock(BinaryWriter writer, long lengthPos)
        {
            long end = writer.BaseStream.Position;
            ushort totalLength = (ushort)(end - lengthPos);
            writer.Seek((int)lengthPos, SeekOrigin.Begin);
            writer.Write(totalLength);
            writer.Seek((int)end, SeekOrigin.Begin);
            Align4(writer);
        }

        private static void WriteStringEntry(BinaryWriter writer, string key, string value)
        {
            long start = writer.BaseStream.Position;
            writer.Write((ushort)0); // wLength - Platzhalter
            writer.Write((ushort)(value.Length + 1)); // wValueLength zählt in WCHARs inkl. Nullterminator
            writer.Write((ushort)1); // wType = Text
            WriteUnicodeZ(writer, key);
            Align4(writer);
            WriteUnicodeZ(writer, value);
            Align4(writer);

            long end = writer.BaseStream.Position;
            ushort totalLength = (ushort)(end - start);
            writer.Seek((int)start, SeekOrigin.Begin);
            writer.Write(totalLength);
            writer.Seek((int)end, SeekOrigin.Begin);
        }

        private static void WriteUnicodeZ(BinaryWriter writer, string text)
        {
            writer.Write(Encoding.Unicode.GetBytes(text));
            writer.Write((ushort)0); // Nullterminator
        }

        private static void Align4(BinaryWriter writer)
        {
            while (writer.BaseStream.Position % 4 != 0)
                writer.Write((byte)0);
        }

        private static uint PackVersion(Version version, bool high) =>
            high
                ? ((uint)(ushort)Math.Max(version.Major, 0) << 16) | (ushort)Math.Max(version.Minor, 0)
                : ((uint)(ushort)Math.Max(version.Build, 0) << 16) | (ushort)Math.Max(version.Revision, 0);
    }

    public enum SubsystemType
    {
        GUI = 2,
        Console = 3
    }

    /// <summary>
    /// Die gebräuchlichen VS_VERSION_INFO-Felder. FileVersion/ProductVersion
    /// sind die BINÄREN 4x16-Bit-Werte, die Windows-Dialoge (Explorer-
    /// Eigenschaften -&gt; Details) strukturiert anzeigen; die restlichen
    /// Felder landen als freier Text in der StringTable (ebenfalls im
    /// "Details"-Reiter sichtbar).
    /// </summary>
    public sealed class PeVersionInfo
    {
        public Version FileVersion { get; set; } = new(1, 0, 0, 0);
        public Version ProductVersion { get; set; } = new(1, 0, 0, 0);
        
        public SubsystemType Subsystem { get; set; }
        public string? CompanyName { get; set; }
        public string? FileDescription { get; set; }
        public string? ProductName { get; set; }
        public string? LegalCopyright { get; set; }
        public string? OriginalFilename { get; set; }
        public string? InternalName { get; set; }
        public string? Comments { get; set; }

        internal IEnumerable<(string Key, string? Value)> EnumerateStrings()
        {
            yield return ("CompanyName", CompanyName);
            yield return ("FileDescription", FileDescription);
            yield return ("FileVersion", FileVersion.ToString());
            yield return ("InternalName", InternalName);
            yield return ("LegalCopyright", LegalCopyright);
            yield return ("OriginalFilename", OriginalFilename);
            yield return ("ProductName", ProductName);
            yield return ("ProductVersion", ProductVersion.ToString());
            if (!string.IsNullOrEmpty(Comments))
                yield return ("Comments", Comments);
        }
    }
}
