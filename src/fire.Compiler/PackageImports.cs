using fire.Runtime;
using fire.Bytecode;
using fire.Package.Manager;
using fire.Values;

namespace fire.Compiler
{
    /// <summary>The imports of installed packages (docs/packages/README.md) as far as the compiler and the virtual machine are concerned: their natives are C++ source for the native backend, so the
    /// virtual machine only knows their names (a call is an error that says so); a prelude without natives works in both.</summary>
    public static partial class PackageImports
    {
        public static bool IsPackageKey(string key) => key.StartsWith(PackageStore.KeyPrefix, StringComparison.Ordinal);

        /// <summary>The "library" of a native the host runs (a `host` function of a manifest: `fire` has them itself) - in the program file as PackageNativeLibraries says it
        /// (the same as fire.Runtime.PackageNativeBinding.HostLibrary and forge-c).</summary>
        public const string HostLibrary = "@host";

        /// <summary>The natives of the imports of packages in the order they are registered (after those of the compiler's own imports) and the file names of the libraries they live in.</summary>
        public static (List<string> Names, List<string> Libraries) NativesOf(IEnumerable<string> nativeImports)
        {
            var names = new List<string>();
            var libraries = new List<string>();
            foreach (var key in nativeImports.Where(IsPackageKey).OrderBy(k => k, StringComparer.Ordinal))
                if (PackageStore.Default.FindKey(key) is { } import && import.Import.Native != null)
                    foreach (var function in import.Import.Native.Functions)
                        if (!names.Contains(function.Name)) { names.Add(function.Name); libraries.Add(function.Host ? HostLibrary : PackageLibrary.FileNameFor(import)); }
            return (names, libraries);
        }

        /// <summary>What the natives of the packages among <paramref name="nativeImports"/> return (their `returns`): name -> type text.</summary>
        public static Dictionary<string, string> NativeReturnTypes(IEnumerable<string> nativeImports)
        {
            var types = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var key in nativeImports.Where(IsPackageKey))
                if (PackageStore.Default.FindKey(key) is { } import && import.Import.Native != null)
                    foreach (var function in import.Import.Native.Functions)
                        if (!string.IsNullOrWhiteSpace(function.Returns)) types.TryAdd(function.Name, function.Returns!.Trim());
            return types;
        }

        /// <summary>Builds (if needed) the libraries of the natives of the imports of packages that a program uses and returns their paths. A failure to build is a <see cref="PackageException"/>.</summary>
        public static List<string> EnsureLibraries(IEnumerable<string> nativeImports, Action<string>? log = null)
        {
            var imports = LibraryImports(nativeImports).ToList();
            var count = PackageLibrary.CountBuilds(imports);
            return imports.Select(import => PackageLibrary.Ensure(import, log, count)).ToList();
        }

        /// <summary>The imports of packages among <paramref name="nativeImports"/> that have natives in a library (not only host natives), in a fixed order.</summary>
        internal static IEnumerable<InstalledImport> LibraryImports(IEnumerable<string> nativeImports)
        {
            foreach (var key in nativeImports.Where(IsPackageKey).OrderBy(k => k, StringComparer.Ordinal))
                if (PackageStore.Default.FindKey(key) is { } import && import.Import.Native is { } native && native.Functions.Any(f => !f.Host))
                    yield return import;
        }

        /// <summary>Registers the natives of the imports of packages (names only: the calls need them while compiling).</summary>
        public static void RegisterNames(NativeRegistry natives, InstalledImport import)
        {
            if (import.Import.Native == null) return;
            foreach (var function in import.Import.Native.Functions)
                if (!natives.Has(function.Name)) natives.Register(function.Name, Unavailable(function.Name));   // (names only: `fire` runs them)
        }


        private static fire.Bytecode.NativeFunction Unavailable(string name) => _ =>
            throw new InvalidOperationException($"The native function '{name}' of a package is written in C++: it is only available in a native build (`fire native`), not in the virtual machine.");
    }
}
