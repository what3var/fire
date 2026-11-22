using fire.Runtime;
using fire.Bytecode;
using fire.Package.Manager;
using fire.Values;

namespace fire.Compiler
{
    // The natives of packages bound in this process for the C# VM (fire.Runtime: RuntimeSession.Build) - it goes with fire.Runtime (docs/ROADMAP_C_PORT.md, M1).
    // `fire` binds them itself (FIRE_PKG_PATH).
    public static partial class PackageImports
    {
        /// <summary>For a run in the virtual machine: the natives of packages bound to their libraries (built now when they are not there yet; a library that cannot be built or loaded makes
        /// a call fail with the reason). The calls address natives by index, so the order is the one of linking.</summary>
        public static void RegisterForRun(NativeRegistry natives, LinkedProgram program, Action<string>? log = null)
        {
            if (program.PackageNatives == null) return;
            // build what is missing now (a failure shows when a native is called, not before)
            // (with a BuildMonitor: the phase PackageNatives, counted per package; cancelled, OperationCanceledException comes out and no half library stays)
            var located = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var imports = LibraryImports(program.NativeImports).ToList();
            var count = PackageLibrary.CountBuilds(imports);
            foreach (var import in imports)
            {
                try { located[PackageLibrary.FileNameFor(import)] = PackageLibrary.Ensure(import, log ?? (m => (fire.Native.ToolchainProvider.Log ?? Console.Error.WriteLine)(m)), count); }
                catch (PackageException ex) { FailedLibraries[PackageLibrary.FileNameFor(import)] = ex.Message; }
            }
            PackageNativeBinding.Register(natives, program.PackageNatives, program.PackageNativeLibraries,
                file => located.TryGetValue(file, out var path) ? path : FailedLibraries.TryGetValue(file, out var reason) ? throw new DllNotFoundException(reason) : null);
        }

        private static readonly Dictionary<string, string> FailedLibraries = new(StringComparer.OrdinalIgnoreCase);
    }
}
