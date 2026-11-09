using System.Text;

namespace fire.Native
{
    /// <summary>A FAT12 floppy of 1.44 MB as the boot image of the targets without an operating system (ROADMAP 13.5): the boot sector (with its BIOS parameter
    /// block, <see cref="BiosBootSectors"/>), two FATs, the root directory, then the files - FIRE.EXE first and in one piece (the boot sector loads it as a run of
    /// sectors), then the files and folders of the program's `disk` folder. Any system reads and changes the image (mtools, a loop mount, a real floppy).</summary>
    public static class Fat12Image
    {
        public const int SectorBytes = 512, Sectors = 2880, FatSectors = 9, RootEntries = 224;
        private const int RootSector = 1 + 2 * FatSectors, DataSector = RootSector + RootEntries * 32 / SectorBytes, Clusters = Sectors - DataSector;

        /// <summary>A file or folder on the disk: a folder has <see cref="Children"/>, a file <see cref="Data"/>.</summary>
        public sealed class Entry
        {
            public string Name = "";
            public byte[]? Data;
            public List<Entry>? Children;
            public DateTime Time = new(2026, 1, 1);
            internal int Cluster;
        }

        /// <summary>The image: <paramref name="bootSector"/> (512 bytes), FIRE.EXE (<paramref name="program"/>) and the entries of <paramref name="files"/>.
        /// Throws an <see cref="InvalidOperationException"/> naming the problem (a name that is not 8.3, more than fits on the floppy).</summary>
        public static byte[] Create(byte[] bootSector, byte[] program, IReadOnlyList<Entry> files)
        {
            if (bootSector.Length != SectorBytes) throw new InvalidOperationException("The boot sector must be 512 bytes.");
            var image = new byte[Sectors * SectorBytes];
            Array.Copy(bootSector, image, SectorBytes);
            var fat = new int[Clusters + 2];
            fat[0] = 0xFF0; fat[1] = 0xFFF;
            int next = 2;
            int Allocate(int bytes, string what)
            {
                int count = Math.Max(1, (bytes + SectorBytes - 1) / SectorBytes);
                if (next + count > Clusters + 2) throw new InvalidOperationException($"The floppy (1.44 MB) is full: {what} does not fit any more.");
                int first = next;
                for (int i = 0; i < count; i++) fat[next + i] = i == count - 1 ? 0xFFF : next + i + 1;
                next += count;
                return first;
            }
            void WriteCluster(int cluster, byte[] data, int offset = 0)
            {
                Array.Copy(data, 0, image, (DataSector + cluster - 2) * SectorBytes + offset, data.Length);
            }
            var program83 = new Entry { Name = "FIRE.EXE", Data = program, Time = DateTime.Now };
            var root = new List<Entry> { program83 };
            root.AddRange(files);
            CheckNames(root, "");
            if (root.Count > RootEntries) throw new InvalidOperationException($"The root directory of the floppy holds {RootEntries} entries, not {root.Count}.");
            // FIRE.EXE first, in one piece; then the rest, a folder before its contents
            void Place(Entry e)
            {
                if (e.Children != null)
                {
                    e.Cluster = Allocate((e.Children.Count + 2) * 32, "the folder " + e.Name);
                    foreach (var c in e.Children) Place(c);
                }
                else if (e.Data!.Length > 0) e.Cluster = Allocate(e.Data.Length, e.Name);
            }
            foreach (var e in root) Place(e);
            // the contents: files, then the directories (their entries name the clusters of their contents)
            void Fill(Entry e, int parentCluster)
            {
                if (e.Children == null) { if (e.Data!.Length > 0) WriteCluster(e.Cluster, e.Data); return; }
                var dir = new byte[(e.Children.Count + 2) * 32];
                WriteEntry(dir, 0, ".          ", 0x10, e.Cluster, 0, e.Time);
                WriteEntry(dir, 32, "..         ", 0x10, parentCluster, 0, e.Time);
                for (int i = 0; i < e.Children.Count; i++) WriteEntry(dir, (i + 2) * 32, e.Children[i]);
                WriteCluster(e.Cluster, dir);
                foreach (var c in e.Children) Fill(c, e.Cluster);
            }
            foreach (var e in root) Fill(e, 0);
            for (int i = 0; i < root.Count; i++) WriteEntry(image, RootSector * SectorBytes + i * 32, root[i]);
            // the two FATs: 12 bits per cluster, two entries in three bytes
            var fatBytes = new byte[FatSectors * SectorBytes];
            for (int i = 0; i < fat.Length; i++)
            {
                int at = i * 3 / 2;
                if (at + 1 >= fatBytes.Length) break;
                if ((i & 1) == 0) { fatBytes[at] = (byte)fat[i]; fatBytes[at + 1] = (byte)((fatBytes[at + 1] & 0xF0) | ((fat[i] >> 8) & 0x0F)); }
                else { fatBytes[at] = (byte)((fatBytes[at] & 0x0F) | ((fat[i] << 4) & 0xF0)); fatBytes[at + 1] = (byte)(fat[i] >> 4); }
            }
            Array.Copy(fatBytes, 0, image, SectorBytes, fatBytes.Length);
            Array.Copy(fatBytes, 0, image, (1 + FatSectors) * SectorBytes, fatBytes.Length);
            return image;
        }

        /// <summary>The files and folders of a folder of this machine as entries (names in upper case; a name that is not 8.3 is an error when the image is made).</summary>
        public static List<Entry> FromFolder(string folder)
        {
            var list = new List<Entry>();
            foreach (var d in Directory.GetDirectories(folder).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                list.Add(new Entry { Name = Path.GetFileName(d).ToUpperInvariant(), Children = FromFolder(d), Time = Directory.GetLastWriteTime(d) });
            foreach (var f in Directory.GetFiles(folder).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                list.Add(new Entry { Name = Path.GetFileName(f).ToUpperInvariant(), Data = File.ReadAllBytes(f), Time = File.GetLastWriteTime(f) });
            return list;
        }

        private static void CheckNames(IReadOnlyList<Entry> entries, string folder)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in entries)
            {
                if (ShortName(e.Name) == null)
                    throw new InvalidOperationException($"'{folder}{e.Name}' is no name of DOS (8.3: up to 8 letters, a dot, up to 3 letters; no spaces).");
                if (!seen.Add(e.Name)) throw new InvalidOperationException($"'{folder}{e.Name}' is on the disk twice (FIRE.EXE is the program).");
                if (e.Children != null) CheckNames(e.Children, folder + e.Name + "\\");
            }
        }

        /// <summary>The name as the 11 characters of a directory entry ("FIRE    EXE"), null when it is no 8.3 name.</summary>
        public static string? ShortName(string name)
        {
            const string allowed = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!#$%&'()-@^_`{}~";
            int dot = name.LastIndexOf('.');
            string stem = dot < 0 ? name : name[..dot], ext = dot < 0 ? "" : name[(dot + 1)..];
            if (stem.Length is 0 or > 8 || ext.Length > 3 || (dot >= 0 && ext.Length == 0)) return null;
            if (!(stem + ext).All(c => allowed.Contains(c))) return null;
            return stem.PadRight(8) + ext.PadRight(3);
        }

        private static void WriteEntry(byte[] dir, int at, Entry e) =>
            WriteEntry(dir, at, ShortName(e.Name)!, (byte)(e.Children != null ? 0x10 : 0x01), e.Cluster, e.Children != null ? 0 : e.Data!.Length, e.Time);

        private static void WriteEntry(byte[] dir, int at, string name11, byte attributes, int cluster, int size, DateTime time)
        {
            Encoding.ASCII.GetBytes(name11, 0, 11, dir, at);
            dir[at + 11] = attributes;   // (files: read-only - the mini-DOS does not write)
            int year = Math.Clamp(time.Year, 1980, 2107);
            int t = (time.Hour << 11) | (time.Minute << 5) | (time.Second / 2), d = ((year - 1980) << 9) | (time.Month << 5) | time.Day;
            BitConverter.TryWriteBytes(dir.AsSpan(at + 14), (ushort)t);
            BitConverter.TryWriteBytes(dir.AsSpan(at + 16), (ushort)d);
            BitConverter.TryWriteBytes(dir.AsSpan(at + 18), (ushort)d);
            BitConverter.TryWriteBytes(dir.AsSpan(at + 22), (ushort)t);
            BitConverter.TryWriteBytes(dir.AsSpan(at + 24), (ushort)d);
            BitConverter.TryWriteBytes(dir.AsSpan(at + 26), (ushort)cluster);
            BitConverter.TryWriteBytes(dir.AsSpan(at + 28), size);
        }
    }
}
