using fire.Runtime;
using fire.Bytecode;
using fire.Package.Manager;
using fire.Values;

namespace fire.Compiler
{
    /// <summary>The imports of installed packages (docs/PACKAGES.md) as far as the compiler and the virtual machine are concerned: their natives are C++ source for the native backend, so the
    /// virtual machine only knows their names (a call is an error that says so); a prelude without natives works in both.</summary>
    public static class PackageImports
    {
        public static bool IsPackageKey(string key) => key.StartsWith(PackageStore.KeyPrefix, StringComparison.Ordinal);

        /// <summary>The natives of the imports of packages in the order they are registered (after those of the compiler's own imports) and the file names of the libraries they live in.</summary>
        public static (List<string> Names, List<string> Libraries) NativesOf(IEnumerable<string> nativeImports)
        {
            var names = new List<string>();
            var libraries = new List<string>();
            foreach (var key in nativeImports.Where(IsPackageKey).OrderBy(k => k, StringComparer.Ordinal))
                if (PackageStore.Default.FindKey(key) is { } import && import.Import.Native != null)
                    foreach (var function in import.Import.Native.Functions)
                        if (!names.Contains(function.Name)) { names.Add(function.Name); libraries.Add(function.Host ? PackageNativeBinding.HostLibrary : PackageLibrary.FileNameFor(import)); }
            return (names, libraries);
        }

        /// <summary>Builds (if needed) the libraries of the natives of the imports of packages that a program uses and returns their paths. A failure to build is a <see cref="PackageException"/>.</summary>
        public static List<string> EnsureLibraries(IEnumerable<string> nativeImports, Action<string>? log = null)
        {
            var files = new List<string>();
            foreach (var key in nativeImports.Where(IsPackageKey).OrderBy(k => k, StringComparer.Ordinal))
                if (PackageStore.Default.FindKey(key) is { } import && import.Import.Native is { } native && native.Functions.Any(f => !f.Host))
                    files.Add(PackageLibrary.Ensure(import, log));
            return files;
        }

        /// <summary>Registers the natives of the imports of packages (names only: the calls need them while compiling).</summary>
        public static void RegisterNames(NativeRegistry natives, InstalledImport import)
        {
            if (import.Import.Native == null) return;
            foreach (var function in import.Import.Native.Functions)
                if (!natives.Has(function.Name)) natives.Register(function.Name, function.Host && HostNatives.Find(function.Name) is { } host ? host : Unavailable(function.Name));
        }

        /// <summary>For a run in the virtual machine: the natives of packages bound to their libraries (built now when they are not there yet; a library that cannot be built or loaded makes
        /// a call fail with the reason). The calls address natives by index, so the order is the one of linking.</summary>
        public static void RegisterForRun(NativeRegistry natives, LinkedProgram program, Action<string>? log = null)
        {
            if (program.PackageNatives == null) return;
            // build what is missing now (a failure shows when a native is called, not before)
            var located = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in program.NativeImports.Where(IsPackageKey))
                if (PackageStore.Default.FindKey(key) is { } import && import.Import.Native is { } native && native.Functions.Any(f => !f.Host))
                {
                    try { located[PackageLibrary.FileNameFor(import)] = PackageLibrary.Ensure(import, log ?? (m => (fire.Native.ToolchainProvider.Log ?? Console.Error.WriteLine)(m))); }
                    catch (PackageException ex) { FailedLibraries[PackageLibrary.FileNameFor(import)] = ex.Message; }
                }
            PackageNativeBinding.Register(natives, program.PackageNatives, program.PackageNativeLibraries,
                file => located.TryGetValue(file, out var path) ? path : FailedLibraries.TryGetValue(file, out var reason) ? throw new DllNotFoundException(reason) : null);
        }

        private static readonly Dictionary<string, string> FailedLibraries = new(StringComparer.OrdinalIgnoreCase);

        private static fire.Bytecode.NativeFunction Unavailable(string name) => _ =>
            throw new InvalidOperationException($"The native function '{name}' of a package is written in C++: it is only available in a native build (`fire native`), not in the virtual machine.");
    }
}
