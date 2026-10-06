using System.IO.Compression;
using System.Text;

namespace fire.Package.Manager
{
    /// <summary>The package file (.fpk): a zip with the `package.json` in its root and the files it names.</summary>
    public static class Fpk
    {
        public const string ManifestEntry = "package.json";
        public const string Extension = ".fpk";

        /// <summary>The result of <see cref="Forge"/>.</summary>
        public sealed record ForgeResult(string PackagePath, string JsonCopyPath, PackageManifest Manifest);

        /// <summary>Builds the package of a forge file (the description with absolute paths, or paths relative to the file): `outputDirectory/name-version.fpk`, and a copy of the forge file
        /// with absolute paths in `outputDirectory/json/name-version.json` - open that one later to forge the package again.</summary>
        public static ForgeResult Forge(string forgeJsonPath, string? outputDirectory = null)
        {
            string forgeFile = Path.GetFullPath(forgeJsonPath);
            if (!File.Exists(forgeFile)) throw new PackageException($"'{forgeJsonPath}' does not exist.");
            string baseDir = Path.GetDirectoryName(forgeFile)!;
            var manifest = PackageManifest.Load(forgeFile);

            string Absolute(string path) => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(baseDir, path));
            var problems = manifest.Validate(path => File.Exists(Absolute(path)));
            if (problems.Count > 0) throw new PackageException("The package description has problems:\n  " + string.Join("\n  ", problems));

            // the forge copy: the same file with every path absolute
            var forgeCopy = PackageManifest.Parse(manifest.ToJson());
            foreach (var import in forgeCopy.Imports)
            {
                if (!string.IsNullOrWhiteSpace(import.Prelude)) import.Prelude = Absolute(import.Prelude);
                if (import.Native != null)
                {
                    import.Native.Sources = import.Native.Sources.Select(Absolute).ToList();
                    import.Native.PlatformSources = import.Native.PlatformSources.ToDictionary(kv => kv.Key, kv => kv.Value.Select(Absolute).ToList());
                    import.Native.Libraries = import.Native.Libraries.ToDictionary(kv => kv.Key, kv => Absolute(kv.Value));
                }
            }

            // the package: every file below a folder of its import; the package.json names them relative to the root
            var packaged = PackageManifest.Parse(manifest.ToJson());
            var entries = new List<(string Source, string Entry)>();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ManifestEntry };
            var byFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string Place(string importName, string source)
            {
                string abs = Absolute(source);
                if (byFile.TryGetValue(abs, out var done)) return done;
                string file = Path.GetFileName(abs);
                string entry = $"{importName}/{file}";
                for (int k = 2; !used.Add(entry); k++) entry = $"{importName}/{k}_{file}";
                byFile[abs] = entry;
                entries.Add((abs, entry));
                return entry;
            }
            foreach (var import in packaged.Imports)
            {
                if (!string.IsNullOrWhiteSpace(import.Prelude)) import.Prelude = Place(import.Name, import.Prelude);
                if (import.Native != null)
                {
                    import.Native.Sources = import.Native.Sources.Select(s => Place(import.Name, s)).ToList();
                    import.Native.PlatformSources = import.Native.PlatformSources.ToDictionary(kv => kv.Key, kv => kv.Value.Select(s => Place(import.Name, s)).ToList());
                    import.Native.Libraries = import.Native.Libraries.ToDictionary(kv => kv.Key, kv => Place(import.Name, kv.Value));
                }
            }

            string outDir = Path.GetFullPath(outputDirectory ?? Path.Combine(baseDir, "build"));
            Directory.CreateDirectory(outDir);
            string baseName = $"{manifest.Name}-{manifest.Version}";
            string fpk = Path.Combine(outDir, baseName + Extension);
            if (File.Exists(fpk)) File.Delete(fpk);
            using (var zip = ZipFile.Open(fpk, ZipArchiveMode.Create))
            {
                var json = zip.CreateEntry(ManifestEntry, CompressionLevel.Optimal);
                using (var w = new StreamWriter(json.Open(), new UTF8Encoding(false))) w.Write(packaged.ToJson());
                foreach (var (source, entry) in entries) zip.CreateEntryFromFile(source, entry, CompressionLevel.Optimal);
            }
            string jsonDir = Path.Combine(outDir, "json");
            Directory.CreateDirectory(jsonDir);
            string jsonCopy = Path.Combine(jsonDir, baseName + ".json");
            forgeCopy.Save(jsonCopy);
            return new ForgeResult(fpk, jsonCopy, packaged);
        }

        /// <summary>The `package.json` of a package file; the problems of a package that cannot be used are an exception.</summary>
        public static PackageManifest ReadManifest(string fpkPath)
        {
            try
            {
                using var zip = ZipFile.OpenRead(fpkPath);
                return ReadManifest(zip, fpkPath);
            }
            catch (InvalidDataException) { throw new PackageException($"'{fpkPath}' is not a package file (.fpk)."); }
            catch (IOException ex) { throw new PackageException($"Cannot read '{fpkPath}': {ex.Message}"); }
        }

        private static PackageManifest ReadManifest(ZipArchive zip, string fpkPath)
        {
            var entry = zip.GetEntry(ManifestEntry) ?? throw new PackageException($"'{fpkPath}' has no {ManifestEntry} in its root.");
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            var manifest = PackageManifest.Parse(reader.ReadToEnd());
            var problems = manifest.Validate(path => zip.GetEntry(path.Replace('\\', '/')) != null);
            if (problems.Count > 0) throw new PackageException($"The package '{fpkPath}' has problems:\n  " + string.Join("\n  ", problems));
            return manifest;
        }

        /// <summary>Unpacks a package into <paramref name="directory"/> (which must not exist) and returns its manifest.</summary>
        public static PackageManifest Extract(string fpkPath, string directory)
        {
            PackageManifest manifest;
            try
            {
                using var zip = ZipFile.OpenRead(fpkPath);
                manifest = ReadManifest(zip, fpkPath);
                string root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
                foreach (var entry in zip.Entries)
                {
                    if (entry.FullName.EndsWith('/')) continue;
                    string target = Path.GetFullPath(Path.Combine(directory, entry.FullName));
                    if (!target.StartsWith(root, StringComparison.Ordinal)) throw new PackageException($"'{fpkPath}' contains a path that leaves the package folder: {entry.FullName}");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: true);
                }
            }
            catch (InvalidDataException) { throw new PackageException($"'{fpkPath}' is not a package file (.fpk)."); }
            catch (IOException ex) { throw new PackageException($"Cannot unpack '{fpkPath}': {ex.Message}"); }
            return manifest;
        }
    }
}
