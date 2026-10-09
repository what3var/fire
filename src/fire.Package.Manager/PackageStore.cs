using System.Text;

namespace fire.Package.Manager
{
    /// <summary>A package that is installed: its description and the folder it was unpacked to.</summary>
    public sealed class InstalledPackage
    {
        public PackageManifest Manifest { get; }
        public string Directory { get; }
        public InstalledPackage(PackageManifest manifest, string directory) { Manifest = manifest; Directory = directory; }
        /// <summary>Where the libraries that are built from the natives of the package are kept (instead of `lib/` in <see cref="Directory"/>): a package that stands for a project.</summary>
        public string? BuildDirectory { get; init; }
        public string Name => Manifest.Name;
        public string Version => Manifest.Version;
    }

    /// <summary>One import of an installed package, with access to its files.</summary>
    public sealed class InstalledImport
    {
        public InstalledPackage Package { get; }
        public PackageImport Import { get; }
        public InstalledImport(InstalledPackage package, PackageImport import) { Package = package; Import = import; }

        public string Name => Import.Name;
        /// <summary>The key the compiler uses for this import in its set of imports (`pkg:name`).</summary>
        public string Key => PackageStore.KeyPrefix + Import.Name.ToLowerInvariant();

        private string PathOf(string relative) => System.IO.Path.GetFullPath(System.IO.Path.Combine(Package.Directory, relative));

        /// <summary>The prelude (fire source), null if the import has none.</summary>
        public string? ReadPrelude() => string.IsNullOrWhiteSpace(Import.Prelude) ? null : File.ReadAllText(PathOf(Import.Prelude));

        /// <summary>The C++ files of the natives for a build for <paramref name="platformKeys"/> (the platform package and the name of the target), in the order of the description.</summary>
        public IEnumerable<(string Name, string Text)> ReadNativeSources(IEnumerable<string> platformKeys)
        {
            if (Import.Native == null) yield break;
            foreach (var src in Import.Native.SourcesFor(platformKeys)) yield return (src, StripPragmaOnce(File.ReadAllText(PathOf(src))));
        }

        /// <summary>The sources are put into one generated file: `#pragma once` in a header would only be a warning there.</summary>
        private static string StripPragmaOnce(string text) =>
            System.Text.RegularExpressions.Regex.Replace(text, @"^[ \t]*#pragma[ \t]+once[ \t]*\r?$\n?", "", System.Text.RegularExpressions.RegexOptions.Multiline);

        /// <summary>The full path of a file of the package.</summary>
        public string FullPath(string relative) => PathOf(relative);
    }

    /// <summary>The installed packages: below `Packages` in the folder of the compiler, one folder per package with the unpacked files and the `package.json`. Global for the machine -
    /// every project sees the same packages.</summary>
    public sealed class PackageStore
    {
        public const string KeyPrefix = "pkg:";

        public static string DefaultRoot => Path.Combine(AppContext.BaseDirectory, "Packages");

        private static PackageStore? _default;
        /// <summary>The store the compiler and the editor use (tests set their own).</summary>
        public static PackageStore Default { get => _default ??= new PackageStore(DefaultRoot); set => _default = value; }

        public string Root { get; }
        private List<InstalledPackage>? _cache;
        private DateTime _cacheStamp;

        public PackageStore(string root) { Root = Path.GetFullPath(root); }

        private DateTime Stamp()
        {
            if (!Directory.Exists(Root)) return DateTime.MinValue;
            var stamp = Directory.GetLastWriteTimeUtc(Root);
            foreach (var dir in Directory.GetDirectories(Root))
            {
                string json = Path.Combine(dir, Fpk.ManifestEntry);
                if (File.Exists(json)) { var t = File.GetLastWriteTimeUtc(json); if (t > stamp) stamp = t; }
            }
            return stamp;
        }

        private readonly List<InstalledPackage> _overlay = new();

        /// <summary>Packages that stand for the projects of the open solution (their natives, see ProjectNatives): they are not installed on the machine, but a build sees them like installed ones -
        /// and before them. A package of the same name replaces the one that was there.</summary>
        public void AddOverlay(InstalledPackage package)
        {
            lock (_overlay)
            {
                _overlay.RemoveAll(p => string.Equals(p.Name, package.Name, StringComparison.OrdinalIgnoreCase));
                _overlay.Add(package);
            }
        }

        public void ClearOverlay() { lock (_overlay) _overlay.Clear(); }

        /// <summary>All installed packages (packages that cannot be read are left out), after the overlay.</summary>
        public IReadOnlyList<InstalledPackage> Installed()
        {
            InstalledPackage[] overlay;
            lock (_overlay) overlay = _overlay.ToArray();
            var disk = InstalledOnDisk();
            return overlay.Length == 0 ? disk : overlay.Concat(disk.Where(d => !overlay.Any(o => string.Equals(o.Name, d.Name, StringComparison.OrdinalIgnoreCase)))).ToList();
        }

        private IReadOnlyList<InstalledPackage> InstalledOnDisk()
        {
            var stamp = Stamp();
            if (_cache != null && stamp == _cacheStamp) return _cache;
            var list = new List<InstalledPackage>();
            if (Directory.Exists(Root))
                foreach (var dir in Directory.GetDirectories(Root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    string json = Path.Combine(dir, Fpk.ManifestEntry);
                    if (!File.Exists(json)) continue;
                    try { list.Add(new InstalledPackage(PackageManifest.Load(json), dir)); }
                    catch (PackageException) { /* a broken folder: not a package */ }
                }
            _cache = list;
            _cacheStamp = stamp;
            return list;
        }

        public InstalledPackage? Find(string name) => Installed().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>The import `name` (as in `#import "name"`, not case sensitive) of an installed package, or null.</summary>
        public InstalledImport? FindImport(string importName)
        {
            foreach (var package in Installed())
                foreach (var import in package.Manifest.Imports)
                    if (string.Equals(import.Name, importName, StringComparison.OrdinalIgnoreCase)) return new InstalledImport(package, import);
            return null;
        }

        /// <summary>The import for a key of the compiler (`pkg:name`), or null.</summary>
        public InstalledImport? FindKey(string key) =>
            key.StartsWith(KeyPrefix, StringComparison.Ordinal) ? FindImport(key.Substring(KeyPrefix.Length)) : null;

        /// <summary>Installs a package file; an installed package of the same name is replaced. Another package that has one of the imports is an error.</summary>
        public InstalledPackage Install(string fpkPath)
        {
            var manifest = Fpk.ReadManifest(fpkPath);
            foreach (var import in manifest.Imports)
                if (FindImport(import.Name) is { } other && !string.Equals(other.Package.Name, manifest.Name, StringComparison.OrdinalIgnoreCase))
                    throw new PackageException($"The import '{import.Name}' already belongs to the package '{other.Package.Name}'.");

            Directory.CreateDirectory(Root);
            string temp = Path.Combine(Root, ".tmp-" + Guid.NewGuid().ToString("N"));
            try
            {
                Fpk.Extract(fpkPath, temp);
                string target = Path.Combine(Root, manifest.Name);
                if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                Directory.Move(temp, target);
                _cache = null;
                return new InstalledPackage(manifest, target);
            }
            finally
            {
                if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
            }
        }

        /// <summary>Removes a package; false if it is not installed.</summary>
        public bool Remove(string name)
        {
            var package = Find(name);
            if (package == null) return false;
            Directory.Delete(package.Directory, recursive: true);
            _cache = null;
            return true;
        }

        /// <summary>Installed packages that list <paramref name="name"/> as a dependency.</summary>
        public IReadOnlyList<InstalledPackage> DependentsOf(string name) =>
            Installed().Where(p => p.Manifest.Dependencies.Any(d => string.Equals(d, name, StringComparison.OrdinalIgnoreCase))).ToList();
    }
}
