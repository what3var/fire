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

        /// <summary>The natives of the imports of packages in the order they are registered (after those of the compiler's own imports).</summary>
        public static List<string> NativeNamesOf(IEnumerable<string> nativeImports)
        {
            var names = new List<string>();
            foreach (var key in nativeImports.Where(IsPackageKey).OrderBy(k => k, StringComparer.Ordinal))
                if (PackageStore.Default.FindKey(key) is { } import && import.Import.Native != null)
                    foreach (var function in import.Import.Native.Functions)
                        if (!names.Contains(function.Name)) names.Add(function.Name);
            return names;
        }

        /// <summary>Registers the natives of the imports of packages (names only: the calls need them while compiling; a call at run time fails, see <see cref="Unavailable"/>).</summary>
        public static void RegisterNames(NativeRegistry natives, InstalledImport import)
        {
            if (import.Import.Native == null) return;
            foreach (var function in import.Import.Native.Functions)
                if (!natives.Has(function.Name)) natives.Register(function.Name, Unavailable(function.Name));
        }

        /// <summary>For a run: the natives of packages as they were linked, each an error when called. The calls address natives by index, so the order has to be the one of linking.</summary>
        public static void RegisterForRun(NativeRegistry natives, IEnumerable<string>? packageNatives)
        {
            if (packageNatives == null) return;
            foreach (var name in packageNatives) if (!natives.Has(name)) natives.Register(name, Unavailable(name));
        }

        private static fire.Bytecode.NativeFunction Unavailable(string name) => _ =>
            throw new InvalidOperationException($"The native function '{name}' of a package is written in C++: it is only available in a native build (`fire native`), not in the virtual machine.");
    }
}
