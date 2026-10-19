using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using fire.Package.Manager;
using fire.Projects;

namespace fire.Compiler
{
    /// <summary>
    /// The standard libraries as projects (docs/PACKAGES.md): the folder <c>Source</c> next to the programs holds <c>Packages.firesln</c> with one library project per import (`time`, `io`,
    /// `graphics`, ...): its prelude as fire source, its C++ in <c>native/</c>, its tests in <c>testing/</c>. When spark, forge or ember start, <see cref="EnsureInstalled"/> packs the projects
    /// that are not installed yet, or that changed since they were installed, and installs them (without their tests). A library that was built for this machine with the programs
    /// (<c>Prebuilt/</c>, see <see cref="Prebuild"/>) goes along when its C++ is still the same, so that the virtual machine needs no C++ compiler for it.
    /// </summary>
    public static class StandardSources
    {
        public const string SolutionFile = "Packages.firesln";

        /// <summary>The folder of the projects: <c>Source</c> next to the program.</summary>
        public static string DefaultFolder => Path.Combine(AppContext.BaseDirectory, "Source");

        /// <summary>The libraries built with the programs: <c>Prebuilt/&lt;package&gt;/&lt;rid&gt;/</c> next to the program.</summary>
        public static string DefaultPrebuiltFolder => Path.Combine(AppContext.BaseDirectory, "Prebuilt");

        private static string StampFile(PackageStore store) => Path.Combine(store.Root, ".source-stamps.json");

        /// <summary>The projects of the solution in <paramref name="folder"/> in the order they are packed: a project after the ones whose imports it uses.</summary>
        public static List<LoadedProject> Projects(Workspace workspace)
        {
            var byImport = workspace.Projects.ToDictionary(p => p.Project.ImportName, StringComparer.OrdinalIgnoreCase);
            var result = new List<LoadedProject>();
            var visiting = new HashSet<LoadedProject>();
            void Visit(LoadedProject p)
            {
                if (result.Contains(p) || !visiting.Add(p)) return;
                foreach (var name in ImportsOf(p)) if (byImport.TryGetValue(name, out var dep) && dep != p) Visit(dep);
                result.Add(p);
            }
            foreach (var p in workspace.Projects) Visit(p);
            return result;
        }

        /// <summary>The names the files of a project import (`#import "name"`), not counting its tests.</summary>
        public static IEnumerable<string> ImportsOf(LoadedProject project) =>
            project.Files.SelectMany(f => { try { return ImportedPreludes.FindImportNames(File.ReadAllText(f)); } catch (IOException) { return Enumerable.Empty<string>(); } })
                .Distinct(StringComparer.OrdinalIgnoreCase);

        /// <summary>A fingerprint of what goes into the package of a project: every file of its folder but the tests (and `bin`/`obj`).</summary>
        public static string HashOf(LoadedProject project)
        {
            using var sha = SHA256.Create();
            var files = Directory.EnumerateFiles(project.Directory, "*", SearchOption.AllDirectories)
                .Select(f => (Full: f, Rel: Path.GetRelativePath(project.Directory, f).Replace('\\', '/')))
                .Where(f => !f.Rel.StartsWith(ProjectFiles.TestingFolder + "/", StringComparison.OrdinalIgnoreCase) && !f.Rel.StartsWith("bin/") && !f.Rel.StartsWith("obj/"))
                .OrderBy(f => f.Rel, StringComparer.Ordinal);
            foreach (var (full, rel) in files)
            {
                byte[] name = Encoding.UTF8.GetBytes(rel + "\n");
                sha.TransformBlock(name, 0, name.Length, null, 0);
                byte[] content = File.ReadAllBytes(full);
                sha.TransformBlock(content, 0, content.Length, null, 0);
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return Convert.ToHexString(sha.Hash!);
        }

        /// <summary>A fingerprint of the C++ of an installed import for this machine: a prebuilt library belongs to exactly these sources.</summary>
        public static string NativeHashOf(InstalledImport import)
        {
            using var sha = SHA256.Create();
            foreach (var (name, text) in import.ReadNativeSources(PackageLibrary.HostPlatformKeys))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(name + "\n" + text + "\n");
                sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
            }
            byte[] functions = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(import.Import.Native?.Functions));
            sha.TransformFinalBlock(functions, 0, functions.Length);
            return Convert.ToHexString(sha.Hash!);
        }

        private static Dictionary<string, string> LoadStamps(PackageStore store)
        {
            try { return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(StampFile(store))) ?? new(); }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
        }

        private static void SaveStamps(PackageStore store, Dictionary<string, string> stamps)
        {
            Directory.CreateDirectory(store.Root);
            File.WriteAllText(StampFile(store), JsonSerializer.Serialize(stamps, new JsonSerializerOptions { WriteIndented = true }));
        }

        /// <summary>Packs and installs the projects of <paramref name="folder"/> that are missing in <paramref name="store"/> or changed since they were installed. Quick when nothing changed
        /// (it compares fingerprints of the folders). Never throws: what goes wrong is reported to <paramref name="log"/>. Returns the names of the packages it installed.</summary>
        public static IReadOnlyList<string> EnsureInstalled(Action<string>? log = null, string? folder = null, PackageStore? store = null, string? prebuiltFolder = null)
        {
            var installed = new List<string>();
            folder ??= DefaultFolder;
            store ??= PackageStore.Default;
            prebuiltFolder ??= DefaultPrebuiltFolder;
            string solution = Path.Combine(folder, SolutionFile);
            if (!File.Exists(solution)) return installed;
            try
            {
                Directory.CreateDirectory(store.Root);
                using var exclusive = Lock(store);   // spark and forge may start at the same time
                var stamps = LoadStamps(store);
                var workspace = Workspace.Open(solution);
                var previous = PackageStore.Default;
                PackageStore.Default = store;   // packing compiles the project: its imports are the packages installed just before
                try
                {
                    foreach (var project in Projects(workspace))
                    {
                        string hash = HashOf(project);
                        if (store.Find(project.Name) != null && stamps.TryGetValue(project.Name, out var known) && known == hash) continue;
                        try
                        {
                            Install(workspace, project, store, prebuiltFolder, log);
                            stamps[project.Name] = hash;
                            installed.Add(project.Name);
                            SaveStamps(store, stamps);
                        }
                        catch (Exception ex) when (ex is ProjectException or PackageException or IOException || CommandLineRunner.IsCompileError(ex))
                        {
                            log?.Invoke($"The standard package {project.Name} could not be installed: {(CommandLineRunner.IsCompileError(ex) ? CompileErrors.Describe(ex) : ex.Message)}");
                        }
                    }
                }
                finally
                {
                    store.ClearOverlay();   // (packing put the natives of the projects into the store's overlay: the installed packages count now)
                    PackageStore.Default = previous;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ProjectException or PackageException)
            {
                log?.Invoke("The standard packages could not be checked: " + ex.Message);
            }
            return installed;
        }

        /// <summary>Packs one project of the solution (without its tests) and installs the package, replacing an installed one of the same version.</summary>
        public static string Install(Workspace workspace, LoadedProject project, PackageStore store, string? prebuiltFolder, Action<string>? log)
        {
            var plan = BuildPlan.Create(workspace, project);
            if (!plan.IsValid) throw new ProjectException(string.Join(Environment.NewLine, plan.Errors));
            string work = Path.Combine(Path.GetTempPath(), "fire-source-" + Guid.NewGuid().ToString("N"));
            try
            {
                string fpk;
                try { fpk = ProjectBuilder.PackLibrary(plan, work); }
                finally { store.ClearOverlay(); }   // (the check of the project put its natives into the overlay; the installed package takes their place)
                log?.Invoke($"Installing the standard package {project.Name} {ProjectBuilder.PackageVersion(plan.Settings.Version)} from {Path.GetFileName(project.Directory)}...");
                store.Install(fpk);   // (replaces the installed package of the same name)
                if (prebuiltFolder != null) UsePrebuilt(store, project.Name, prebuiltFolder);
                return fpk;
            }
            finally
            {
                try { Directory.Delete(work, true); } catch (IOException) { }
            }
        }

        /// <summary>Puts the library that was built for this machine with the programs next to the installed package, when it belongs to the C++ the package has now.</summary>
        private static void UsePrebuilt(PackageStore store, string packageName, string prebuiltFolder)
        {
            var package = store.Find(packageName);
            if (package == null) return;
            foreach (var import in AllImports(store).Where(i => i.Package.Name == packageName && i.Import.Native != null))
            {
                string dir = Path.Combine(prebuiltFolder, packageName, PackageLibrary.Rid);
                string lib = Path.Combine(dir, PackageLibrary.FileNameFor(import));
                string hashFile = lib + ".hash";
                if (!File.Exists(lib) || !File.Exists(hashFile) || File.ReadAllText(hashFile).Trim() != NativeHashOf(import)) continue;
                string target = Path.Combine(package.Directory, "lib", PackageLibrary.Rid, Path.GetFileName(lib));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(lib, target, overwrite: true);
                File.SetLastWriteTimeUtc(target, DateTime.UtcNow);   // newer than the sources: PackageLibrary.Locate takes it
            }
        }

        /// <summary>Builds the libraries of the standard packages for this machine into <paramref name="prebuiltFolder"/> (with the build of the programs, `forge prebuild-standard`): packs the
        /// projects into a store of its own, builds every native part and keeps the library with the fingerprint of its C++.</summary>
        public static IReadOnlyList<string> Prebuild(string folder, string prebuiltFolder, Action<string>? log = null)
        {
            var result = new List<string>();
            string storeDir = Path.Combine(Path.GetTempPath(), "fire-prebuild-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new PackageStore(storeDir);
                EnsureInstalled(log, folder, store, prebuiltFolder: null);
                foreach (var import in AllImports(store).Where(i => i.Import.Native != null && i.Import.Native.Functions.Any(f => !f.Host)))
                {
                    try
                    {
                        string built = PackageLibrary.Ensure(import, log);
                        string dir = Path.Combine(prebuiltFolder, import.Package.Name, PackageLibrary.Rid);
                        Directory.CreateDirectory(dir);
                        string lib = Path.Combine(dir, PackageLibrary.FileNameFor(import));
                        File.Copy(built, lib, overwrite: true);
                        File.WriteAllText(lib + ".hash", NativeHashOf(import));
                        result.Add(lib);
                    }
                    catch (PackageException ex) { log?.Invoke($"{import.Package.Name}: no prebuilt library for {PackageLibrary.Rid} ({string.Join('\n', ex.Message.Split('\n').Take(20))})"); }
                }
            }
            finally
            {
                try { Directory.Delete(storeDir, true); } catch (IOException) { }
            }
            return result;
        }

        private static IEnumerable<InstalledImport> AllImports(PackageStore store) =>
            store.Installed().SelectMany(p => p.Manifest.Imports.Select(i => new InstalledImport(p, i))).ToList();

        private static IDisposable Lock(PackageStore store)
        {
            string file = Path.Combine(store.Root, ".source-lock");
            var deadline = DateTime.UtcNow.AddMinutes(2);
            while (true)
            {
                try { return new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
                catch (IOException) when (DateTime.UtcNow < deadline) { Thread.Sleep(200); }
            }
        }
    }
}
