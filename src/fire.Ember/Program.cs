using fire.Package.Manager;

namespace fire.Ember
{
    /// <summary>ember: the command line of the package manager (docs/packages/README.md).</summary>
    internal static class Program
    {
        private const string Usage = """
            ember - the package manager of fire

              ember find [Name]              search the package sources (part of a name or description; no name: everything)
              ember install Name[@Version]   install a package (with what it depends on); also: ember install path\to\package.fpk
              ember install github:owner/repo[@ref] [--name N] [--force]
                                             straight from a GitHub repository: the newest release, a tag (@v1.2.0), a branch or a commit (built from the source);
                                             --name accepts a package not named like the repository, --force replaces a package of the same name from elsewhere
              ember update [Name|github:owner/repo]
                                             move installed packages to their newest version (from GitHub: the newest release; pinned tags and commits stay unless named)
              ember remove Name              remove an installed package (also: ember remove github:owner/repo)
              ember list                     the installed packages (and where those from GitHub came from)
              ember sources                  the package sources in use (ember.json next to the compiler, or the defaults) and the GitHub API
              ember create File.json         write a package description with example values (and the example files next to it)
              ember blank File.json          write a package description with all fields empty
              ember forge File.json [-o Dir] build the package (.fpk) from the description; the description with absolute paths is kept in Dir\json\
              ember index Folder [BaseUrl]   write Folder\index.json for the .fpk files in Folder (to publish them on a web page)
              ember cache [clean]            the user-wide cache of the static libraries of native builds (linkage "static"): where it is and how big; clean empties it

            Packages are installed for the machine, below Packages\ in the folder of the compiler. The sources are listed in ember.json next to the compiler
            (default: the folder PackageSource and the index of the packages on the web).
            """;

        public static int Main(string[] args)
        {
            if (args.Length == 0 || args[0] is "help" or "-h" or "--help" or "/?") { Console.WriteLine(Usage); return args.Length == 0 ? 2 : 0; }
            try
            {
                // the standard packages are packed and installed from Source (by forge next to ember) when they are missing or changed - quick when nothing changed
                if (args[0].ToLowerInvariant() is not ("create" or "blank" or "forge" or "index" or "cache" or "sources"))
                    StandardPackages.EnsureInstalled(Console.WriteLine);
                return Run(args[0].ToLowerInvariant(), args.Skip(1).ToArray());
            }
            catch (PackageException ex)
            {
                Console.Error.WriteLine("ember: " + ex.Message);
                return 1;
            }
        }

        private static int Run(string command, string[] a)
        {
            switch (command)
            {
                case "find":
                {
                    var warnings = new List<string>();
                    var service = new PackageManagerService();
                    var found = service.Find(string.Join(" ", a), warnings);
                    foreach (var w in warnings) Console.Error.WriteLine("ember: " + w);
                    if (found.Count == 0) { Console.WriteLine("Nothing found."); return 1; }
                    foreach (var l in found)
                    {
                        string installed = service.Store.Find(l.Name) is { } p ? $" [installed: {p.Version}]" : "";
                        Console.WriteLine($"{l.Name} {l.Latest?.Version}{installed}");
                        if (l.Description.Length > 0) Console.WriteLine("    " + l.Description);
                        Console.WriteLine($"    by {(l.Author.Length > 0 ? l.Author : "?")}{(l.License.Length > 0 ? "; license: " + l.License : "")}; versions: {string.Join(", ", l.Versions.Select(v => v.Version + (v.Yanked ? " (yanked)" : "")))}");
                    }
                    return 0;
                }
                case "install":
                {
                    string? spec = null, name = null;
                    bool force = false;
                    for (int i = 0; i < a.Length; i++)
                    {
                        if (a[i] is "--force" or "-f") force = true;
                        else if (a[i] == "--name" && i + 1 < a.Length) name = a[++i];
                        else if (spec == null) spec = a[i];
                        else return Usage1("ember install Name[@Version] | github:owner/repo[@ref] [--name Name] [--force]");
                    }
                    if (spec == null) return Usage1("ember install Name[@Version] | github:owner/repo[@ref] [--name Name] [--force]");
                    if (name != null)
                    {
                        if (!GitHubSpec.IsGitHub(spec)) return Usage1("--name is for a package from GitHub: ember install github:owner/repo --name Name");
                        spec = (spec.Contains('#') ? spec.Substring(0, spec.IndexOf('#')) : spec) + "#" + name;
                    }
                    new PackageManagerService().Install(spec, Console.WriteLine, force);
                    return 0;
                }
                case "update":
                {
                    if (a.Length > 1) return Usage1("ember update [Name | github:owner/repo[@ref]]");
                    var results = new PackageManagerService().Update(a.Length == 1 ? a[0] : null, Console.WriteLine);
                    if (results.Count == 0) Console.WriteLine("Nothing to update.");
                    foreach (var r in results)
                        Console.WriteLine(r.OldVersion == r.NewVersion ? $"{r.Name} {r.NewVersion} is up to date ({r.Origin})." : $"{r.Name}: {r.OldVersion} -> {r.NewVersion} ({r.Origin})");
                    return 0;
                }
                case "sources":
                {
                    string file = Path.Combine(AppContext.BaseDirectory, EmberSettings.FileName);
                    Console.WriteLine(File.Exists(file) ? $"The package sources ({file}):" : $"The package sources (default; {file} lists others):");
                    foreach (var source in EmberSettings.LoadSources()) Console.WriteLine($"  {(source is FolderSource ? "folder" : "index ")}  {source.Name}");
                    var gh = EmberSettings.LoadGitHub();
                    Console.WriteLine($"  github  github:owner/repo[@ref] through {gh.ApiBase} ({(gh.Token != null ? "token from " + gh.TokenSource : "no token")})");
                    foreach (var e in PackageLock.Load(PackageStore.Default).GitHub) Console.WriteLine($"          {e.Package} {e.Version}: {e.Origin}, checked: {e.Verified}");
                    return 0;
                }
                case "remove":
                    if (a.Length != 1) return Usage1("ember remove Name");
                    new PackageManagerService(sources: Array.Empty<IPackageSource>()).Remove(a[0]);
                    Console.WriteLine($"Removed {a[0]}.");
                    return 0;
                case "list":
                {
                    var installed = PackageStore.Default.Installed();
                    if (installed.Count == 0) Console.WriteLine("No packages installed.");
                    var locked = PackageLock.Load(PackageStore.Default);
                    foreach (var p in installed)
                        Console.WriteLine($"{p.Name} {p.Version}  (imports: {string.Join(", ", p.Manifest.Imports.Select(i => i.Name))})" + (locked.ForPackage(p.Name) is { } e ? $"  from {e.Origin}" : ""));
                    return 0;
                }
                case "create":
                case "blank":
                {
                    if (a.Length != 1) return Usage1($"ember {command} File.json");
                    string path = a[0];
                    if (File.Exists(path)) throw new PackageException($"'{path}' exists already.");
                    (command == "create" ? Templates.Example(path, writeFiles: true) : Templates.Blank()).Save(path);
                    Console.WriteLine($"Wrote {Path.GetFullPath(path)}.");
                    return 0;
                }
                case "forge":
                {
                    string? output = null;
                    var rest = new List<string>();
                    for (int i = 0; i < a.Length; i++)
                    {
                        if (a[i] is "-o" or "--output" && i + 1 < a.Length) output = a[++i];
                        else rest.Add(a[i]);
                    }
                    if (rest.Count != 1) return Usage1("ember forge File.json [-o Directory]");
                    var result = Fpk.Forge(rest[0], output);
                    Console.WriteLine($"Package: {result.PackagePath}");
                    Console.WriteLine($"Description (absolute paths): {result.JsonCopyPath}");
                    return 0;
                }
                case "index":
                {
                    if (a.Length is < 1 or > 2) return Usage1("ember index Folder [BaseUrl]");
                    string json = PackageIndex.Build(a[0], a.Length > 1 ? a[1] : null);
                    string file = Path.Combine(a[0], "index.json");
                    File.WriteAllText(file, json);
                    Console.WriteLine($"Wrote {Path.GetFullPath(file)}.");
                    return 0;
                }
                case "cache":
                {
                    if (a.Length > 1 || a.Length == 1 && a[0].ToLowerInvariant() != "clean") return Usage1("ember cache [clean]");
                    if (a.Length == 1)
                    {
                        var (count, bytes) = NativeLibraryCache.Clean();
                        Console.WriteLine($"Removed {count} libraries ({bytes / 1024} KB) from {NativeLibraryCache.Root}.");
                        return 0;
                    }
                    var (libraries, size) = NativeLibraryCache.Size();
                    Console.WriteLine($"{NativeLibraryCache.Root}: {libraries} libraries, {size / 1024} KB");
                    return 0;
                }
                default:
                    Console.Error.WriteLine($"ember: unknown command '{command}'.\n");
                    Console.Error.WriteLine(Usage);
                    return 2;
            }
        }

        private static int Usage1(string text)
        {
            Console.Error.WriteLine("Usage: " + text);
            return 2;
        }
    }
}
