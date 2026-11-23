using System.Text;

namespace fire.Projects
{
    /// <summary>A package that is installed: its description and the folder it was unpacked to.</summary>
    public sealed class InstalledPackage
    {
        public PackageManifest Manifest { get; }
        public string Directory { get; }
        public InstalledPackage(PackageManifest manifest, string directory) { Manifest = manifest; Directory = directory; }
        /// <summary>Where the libraries that are built from the natives of the package are kept (instead of `lib/` in <see cref="Directory"/>): a package that stands for a project.</summary>
        public string? BuildDirectory { get; init; }
        /// <summary>The file that describes the natives besides the sources (the project file of a project): a library built before it changed (a new build order) is built again.</summary>
        public string? DescriptionFile { get; init; }
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

        /// <summary>The C/C++ files of the natives for a build for <paramref name="platformKeys"/> (the platform package and the name of the target), in build order (the platform
        /// sources first, then the common ones, sorted by their `order`; see <see cref="NativeSourceOrder"/>). A source that includes another source of the import that comes after it
        /// is a <see cref="PackageException"/> naming the file and the line - the include is taken out in the one file the sources are put into, so it would not work.</summary>
        public IEnumerable<(string Name, string Text)> ReadNativeSources(IEnumerable<string> platformKeys)
        {
            if (Import.Native == null) return Array.Empty<(string, string)>();
            var keys = platformKeys.ToList();
            // the platform layers of the imports this one uses (`native.uses`): before its own sources, each file once, named as the other package names it
            var used = new List<(string Name, string Text)>();
            foreach (var other in UsedImports(keys))
                foreach (var src in other.Import.Native!.PlatformLayerFor(keys))
                    if (!used.Any(u => u.Name == other.Name + "/" + src)) used.Add((other.Name + "/" + src, File.ReadAllText(other.PathOf(src))));
            var sources = Import.Native.SourcesFor(keys);
            var texts = sources.Select(src => (Name: src, Text: File.ReadAllText(PathOf(src)))).ToList();
            // (shown relative to the package - or the project - when the path is in its folder)
            string Display(string name)
            {
                if (!Path.IsPathRooted(name)) return name;
                string rel = Path.GetRelativePath(Package.Directory, name);
                return rel.StartsWith("..") || Path.IsPathRooted(rel) ? name : rel.Replace('\\', '/');
            }
            var problems = NativeSourceOrder.CheckIncludes(texts, Display);
            if (problems.Count > 0) throw new PackageException($"The build order of the natives of '{Import.Name}' is wrong:\n  " + string.Join("\n  ", problems));
            var names = new HashSet<string>(sources.Select(NativeSourceOrder.FileNameOf).Concat(used.Select(u => NativeSourceOrder.FileNameOf(u.Name))), StringComparer.Ordinal);
            // the headers (own ones first, then those of the used imports), unless they are sources of this build: an installed package's are files next to the
            // runtime of the build (`pkg/<import>/`, NativeRuntimeFiles writes them), the include names that file; a project's are put in where they are included
            var inStore = new Dictionary<string, string>(StringComparer.Ordinal);
            var inline = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var imp in new[] { this }.Concat(UsedImports(keys)))
                foreach (var h in imp.Import.Native!.Headers)
                {
                    string name = NativeSourceOrder.FileNameOf(h);
                    if (inStore.ContainsKey(name) || inline.ContainsKey(name)) continue;
                    if (imp.IsInStore) inStore[name] = NativeHeaders.BuildPath(imp.Name, name);
                    else inline[name] = imp.PathOf(h);
                }
            var placed = new HashSet<string>(StringComparer.Ordinal);
            // (a header put in may include one of an installed import - a project's `tls` the `fire_net_c.h` of `net`, which it uses: that include is pointed too)
            string ReadHeader(string path) => NativeHeaders.Point(File.ReadAllText(path), inStore, names);
            return used.Concat(texts)
                .Select(t => (t.Name, StripLocalIncludes(StripPragmaOnce(NativeHeaders.Inline(NativeHeaders.Point(t.Text, inStore, names), inline, names, ReadHeader, placed)), names))).ToList();
        }

        /// <summary>Is the import one of an installed package (its headers are written next to the runtime of every build), not the natives of a project?</summary>
        public bool IsInStore
        {
            get
            {
                string root = PackageStore.Default.Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                return Path.GetFullPath(Package.Directory).StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            }
        }

        /// <summary>The installed imports named by `native.uses` for a build with these keys (an import that is not installed is a <see cref="PackageException"/>).</summary>
        public IEnumerable<InstalledImport> UsedImports(IEnumerable<string> platformKeys)
        {
            if (Import.Native == null || Import.Native.Uses.Count == 0) yield break;
            var installed = PackageStore.Default.Installed().SelectMany(p => p.Manifest.Imports.Select(i => new InstalledImport(p, i))).ToList();
            foreach (var use in Import.Native.Uses.Where(u => u.AppliesTo(platformKeys)))
                yield return installed.FirstOrDefault(i => string.Equals(i.Name, use.Import, StringComparison.OrdinalIgnoreCase) && i.Import.Native != null)
                    ?? throw new PackageException($"The natives of '{Import.Name}' use the platform layer of '{use.Import}', which is not installed.");
        }

        /// <summary>The sources go into one file in the order of the package (what a file includes comes before it): an `#include "x.hpp"` of another source of the package is
        /// already there and is left out.</summary>
        private static string StripLocalIncludes(string text, HashSet<string> names) =>
            System.Text.RegularExpressions.Regex.Replace(text, @"^([ \t]*)#include[ \t]+""([^""\r\n]+)""[ \t]*(?:/\*.*\*/[ \t]*|//.*)?\r?$",   // (a comment may follow on the line)
                m => names.Contains(Path.GetFileName(m.Groups[2].Value)) ? m.Groups[1].Value + "// (in this file already) #include \"" + m.Groups[2].Value + "\"" : m.Value,
                System.Text.RegularExpressions.RegexOptions.Multiline);

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

        /// <summary>How long the packages read from the disk are taken as they are before the folders are looked at again (another program may install one): every `#import`
        /// of every file asks the store, the live analysis of the editor after every pause in typing - a look at all the package folders each time (a stat per package, hundreds per
        /// analysis) was a fifth of its time. Install and Remove of this store forget the list at once. Zero (the default; the editor sets a second): look every time.</summary>
        public static TimeSpan RecheckInterval { get; set; } = TimeSpan.Zero;

        private long _stampCheckedAt;

        private IReadOnlyList<InstalledPackage> InstalledOnDisk()
        {
            var cached = _cache;
            long now = Environment.TickCount64;
            if (cached != null && now - Interlocked.Read(ref _stampCheckedAt) < RecheckInterval.TotalMilliseconds) return cached;
            var stamp = Stamp();
            Interlocked.Exchange(ref _stampCheckedAt, now);
            if (cached != null && stamp == _cacheStamp) return cached;
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

        /// <summary>The installed package a dependency or a reference names: a name or `name@1.2.0` (the installed one, whatever its version); a package from GitHub
        /// (`github:...`) is ember's (its lock file) - null here.</summary>
        public InstalledPackage? FindBySpec(string spec)
        {
            if (spec.StartsWith("github:", StringComparison.OrdinalIgnoreCase)) return null;
            int at = spec.IndexOf('@');
            return Find(at > 0 ? spec.Substring(0, at) : spec);
        }

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

        /// <summary>Installed packages that list <paramref name="name"/> as a dependency.</summary>
        public IReadOnlyList<InstalledPackage> DependentsOf(string name) =>
            Installed().Where(p => p.Manifest.Dependencies.Any(d => string.Equals(d, name, StringComparison.OrdinalIgnoreCase)
                || d.Contains('@') && string.Equals(FindBySpec(d)?.Name, name, StringComparison.OrdinalIgnoreCase))).ToList();
    }
}
