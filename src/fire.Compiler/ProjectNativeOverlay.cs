using fire.Package.Manager;
using fire.Projects;

namespace fire.Compiler
{
    /// <summary>
    /// The C++ natives of projects (docs/PROJECTS.md) go the way of the natives of packages: a project with a `native` part stands for a package that is not installed - it is put into the
    /// overlay of the package store (<see cref="PackageStore.AddOverlay"/>), the build finds its natives there (the virtual machine builds a shared library from them, a native build puts the
    /// C++ into the generated file) and its import key switches them on. The package has no prelude: the fire code of the project comes from the project's files.
    /// </summary>
    public static class ProjectNativeOverlay
    {
        public static string KeyOf(string importName) => PackageStore.KeyPrefix + importName.ToLowerInvariant();

        /// <summary>Puts the native parts of the plan (the project's and its libraries') into the package store.</summary>
        public static void Register(BuildPlan? plan)
        {
            if (plan == null) return;
            foreach (var part in plan.NativeParts)
            {
                var manifest = new PackageManifest { Name = "project:" + part.ProjectName, Version = ProjectBuilder.PackageVersion(plan.Settings.Version) };
                manifest.Imports.Add(new PackageImport { Name = part.ImportName, Native = part.Native });
                PackageStore.Default.AddOverlay(new InstalledPackage(manifest, part.Directory) { BuildDirectory = Path.Combine(part.Directory, "obj", "native") });
            }
        }

        /// <summary>The key of the natives of the project itself: they are on whatever the sources import.</summary>
        public static string? OwnKey(BuildPlan? plan) => plan?.Native != null ? KeyOf(plan.Native.ImportName) : null;

        /// <summary>The key of the natives of the library that `#import "name"` names (null: the library has none, or `name` is no library of the plan).</summary>
        public static string? KeyOfLibrary(BuildPlan? plan, string name) =>
            plan != null && plan.Libraries.TryGetValue(name, out var library) && library.Native != null ? KeyOf(library.ImportName) : null;
    }
}
