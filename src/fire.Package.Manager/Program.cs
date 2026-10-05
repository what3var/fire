namespace fire.Package.Manager
{
    /// <summary>ember: the command line of the package manager (docs/PACKAGES.md).</summary>
    internal static class Program
    {
        private const string Usage = """
            ember - the package manager of fire

              ember find [Name]              search the package sources (part of a name or description; no name: everything)
              ember install Name[@Version]   install a package (with what it depends on); also: ember install path\to\package.fpk
              ember remove Name              remove an installed package
              ember list                     the installed packages
              ember create File.json         write a package description with example values (and the example files next to it)
              ember blank File.json          write a package description with all fields empty
              ember forge File.json [-o Dir] build the package (.fpk) from the description; the description with absolute paths is kept in Dir\json\
              ember index Folder [BaseUrl]   write Folder\index.json for the .fpk files in Folder (to publish them on a web page)

            Packages are installed for the machine, below Packages\ in the folder of the compiler. The sources are listed in ember.json next to the compiler
            (default: the folder PackageSource and the index of the packages on the web).
            """;

        public static int Main(string[] args)
        {
            if (args.Length == 0 || args[0] is "help" or "-h" or "--help" or "/?") { Console.WriteLine(Usage); return args.Length == 0 ? 2 : 0; }
            try
            {
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
                        Console.WriteLine($"    by {(l.Author.Length > 0 ? l.Author : "?")}; versions: {string.Join(", ", l.Versions.Select(v => v.Version))}");
                    }
                    return 0;
                }
                case "install":
                    if (a.Length != 1) return Usage1("ember install Name[@Version]");
                    new PackageManagerService().Install(a[0], Console.WriteLine);
                    return 0;
                case "remove":
                    if (a.Length != 1) return Usage1("ember remove Name");
                    new PackageManagerService().Remove(a[0]);
                    Console.WriteLine($"Removed {a[0]}.");
                    return 0;
                case "list":
                {
                    var installed = PackageStore.Default.Installed();
                    if (installed.Count == 0) Console.WriteLine("No packages installed.");
                    foreach (var p in installed) Console.WriteLine($"{p.Name} {p.Version}  (imports: {string.Join(", ", p.Manifest.Imports.Select(i => i.Name))})");
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
