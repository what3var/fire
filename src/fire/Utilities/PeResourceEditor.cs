using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace fire.Utilities
{
    /// <summary>
    /// Changes icon and version metadata (product name, company, version, ...)
    /// of an already built Windows PE file (.exe/.dll) AFTERWARDS.
    ///
    /// Uses the same Win32 resource update APIs (BeginUpdateResource /
    /// UpdateResource / EndUpdateResource) that Resource Hacker or
    /// rcedit also use, instead of parsing the PE header itself - considerably
    /// more robust than byte-patching the file format yourself.
    ///
    /// Runs ONLY on Windows (pure P/Invoke, no cross-platform
    /// alternative for it). The target file must not be opened/locked by
    /// another process meanwhile (for a running .exe this usually
    /// does not work - close it beforehand or edit a copy).
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

        // Language/codepage for the StringTable of the version info - "English
        // (USA) / Unicode" is by far the most common combination and
        // is reliably recognised by every display program (Explorer properties, etc.),
        // regardless of the system language.
        private const ushort LANG_EN_US = 0x0409;
        private const ushort CODEPAGE_UNICODE = 0x04B0;

        // ------------------------------------------------------------
        // Icon ersetzen
        // ------------------------------------------------------------

        /// <summary>
        /// Replaces the icon(s) of <paramref name="exePath"/> with the
        /// content of <paramref name="icoPath"/> (an ordinary .ico file,
        /// may contain several resolutions/colour depths - all are
        /// taken over). In doing so it replaces SPECIFICALLY the icon group(s) that the file
        /// already has (found out by enumeration, usually exactly
        /// one) - creates a new one with ID 1 only if the file previously
        /// had no icon at all.
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
                // Remove old individual icon images first - otherwise images
                // of a previous, LARGER icon set (e.g. more
                // resolutions) would remain in the file as data corpses,
                // even if no group points to them any more.
                foreach (var id in existingIconIds)
                    Update(handle, RT_ICON, id, null);

                // Write new images under fresh IDs 1..N - the old
                // image IDs are irrelevant, only the group below must point to the
                // NEW IDs.
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
                EndUpdateResource(handle, fDiscard: true); // Discard changes, file stays untouched
                throw;
            }
        }

        // ------------------------------------------------------------
        // Versions-Metadaten setzen
        // ------------------------------------------------------------

        /// <summary>
        /// Sets/replaces the version metadata (file version, product name,
        /// company, copyright, description, ...) of <paramref name="exePath"/> -
        /// overwrites an existing VERSIONINFO resource completely, creates a new one
        /// if needed.
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

        /// <summary>Lists all resource IDs of a certain type that
        /// <paramref name="exePath"/> ALREADY has (e.g. existing
        /// icon groups) - loads the file separately for that, for READING ONLY, as a
        /// data module (LOAD_LIBRARY_AS_DATAFILE executes no code
        /// and binds no imports).</summary>
        private static List<IntPtr> FindResourceNames(string exePath, IntPtr resourceType)
        {
            var names = new List<IntPtr>();
            IntPtr hModule = LoadLibraryEx(exePath, IntPtr.Zero, LOAD_LIBRARY_AS_DATAFILE);
            if (hModule == IntPtr.Zero) return names; // File possibly without any resources at all - no error

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
        // Read the .ico file and convert it to PE resource format
        //
        // The difference from the raw .ico file format: there every
        // ICONDIRENTRY refers to its image by byte OFFSET WITHIN the same
        // file. In a PE resource that does not exist - instead
        // every image is present as its OWN RT_ICON resource, and the
        // "GRPICONDIRENTRY" refers to it by resource ID.
        // ------------------------------------------------------------

        private sealed record IconImage(byte Width, byte Height, byte ColorCount, ushort Planes, ushort BitCount, byte[] Data);

        private static List<IconImage> ReadIconImages(string icoPath)
        {
            using var stream = File.OpenRead(icoPath);
            using var reader = new BinaryReader(stream);

            reader.ReadUInt16(); // reserved, always 0
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
                writer.Write(imageIds[i]); // ID instead of file offset - the difference from the .ico format
            }

            return stream.ToArray();
        }

        // ------------------------------------------------------------
        // VS_VERSIONINFO-Ressource bauen (RT_VERSION)
        //
        // Nested binary format: VS_VERSIONINFO contains the fixed
        // VS_FIXEDFILEINFO structure (the binary version number that Windows
        // shows e.g. at the top of the "Details" dialog) plus two child blocks,
        // StringFileInfo (free text such as product name/company) and
        // VarFileInfo (which language/codepage the StringTable has).
        // Every block carries its own total length at the FRONT of the header -
        // it is only fixed AFTER all child elements have been written,
        // hence BeginBlock/EndBlock: first a placeholder, after
        // writing the children the real length is filled in
        // (seek back, write, forward again).
        // ------------------------------------------------------------

        private static byte[] BuildVersionInfoResource(PeVersionInfo info)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);

            long rootLengthPos = BeginBlock(writer, "VS_VERSION_INFO", valueIsText: false);

            // VS_FIXEDFILEINFO - BeginBlock has already padded directly before via
            // Align4() to a 4-byte boundary (the name
            // "VS_VERSION_INFO" itself is NOT enough for that: 15 characters +
            // null terminator = 32 bytes, plus 6 bytes for the three
            // preceding WORDs = 38 bytes, only Align4() rounds up to
            // 40) - so from here on it is guaranteed 4-byte aligned.
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
            // wValueLength of VS_VERSIONINFO itself (size of VS_FIXEDFILEINFO
            // in bytes) is entered only here, afterwards, into the already written
            // field (see BeginBlock/EndBlock comment).
            PatchValueLength(writer, rootLengthPos, 13 * 4);
            Align4(writer);

            // StringFileInfo -> exactly ONE StringTable (language/codepage as an
            // 8-digit hex key) -> any number of string entries.
            long stringFileInfoPos = BeginBlock(writer, "StringFileInfo", valueIsText: true);
            string langKey = $"{LANG_EN_US:X4}{CODEPAGE_UNICODE:X4}";
            long stringTablePos = BeginBlock(writer, langKey, valueIsText: true);
            foreach (var (key, value) in info.EnumerateStrings())
                WriteStringEntry(writer, key, value ?? "");
            EndBlock(writer, stringTablePos);
            EndBlock(writer, stringFileInfoPos);

            // VarFileInfo/Translation - tells display programs
            // under which language/codepage they find the StringTable above.
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

        /// <summary>Writes length (placeholder)/wValueLength(0)/wType/name
        /// of a nested VERSIONINFO block and then aligns to
        /// 4 bytes. Returns the position of the length field for EndBlock/
        /// PatchValueLength.</summary>
        private static long BeginBlock(BinaryWriter writer, string key, bool valueIsText)
        {
            long lengthPos = writer.BaseStream.Position;
            writer.Write((ushort)0); // wLength - placeholder, see EndBlock
            writer.Write((ushort)0); // wValueLength - usually 0 (pure container), see PatchValueLength for exceptions
            writer.Write((ushort)(valueIsText ? 1 : 0)); // wType
            WriteUnicodeZ(writer, key);
            Align4(writer);
            return lengthPos;
        }

        private static void PatchValueLength(BinaryWriter writer, long blockStartPos, int valueLengthBytes)
        {
            long current = writer.BaseStream.Position;
            writer.Seek((int)blockStartPos + 2, SeekOrigin.Begin); // wValueLength sits directly after wLength
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
            writer.Write((ushort)0); // wLength - placeholder
            writer.Write((ushort)(value.Length + 1)); // wValueLength counts in WCHARs including the null terminator
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
    /// The common VS_VERSION_INFO fields. FileVersion/ProductVersion
    /// are the BINARY 4x16-bit values that Windows dialogs (Explorer
    /// properties -&gt; Details) display in structured form; the remaining
    /// fields end up as free text in the StringTable (likewise
    /// visible in the "Details" tab).
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
