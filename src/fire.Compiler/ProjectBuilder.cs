using System.Text;
using System.Text.RegularExpressions;
using fire.Package.Manager;
using fire.Projects;
using fire.Runtime;

namespace fire.Compiler
{
    /// <summary>
    /// What a project build needs besides the linker (docs/PROJECTS.md): choosing the project of a solution, checking a library (it compiles, it has no entry point) and packing it as a package
    /// (`.fpk`) that `ember` installs and other programs `#import`.
    /// </summary>
    public static class ProjectBuilder
    {
        /// <summary>The project to build from a workspace: the one named, else the startup project (a single project: that one).</summary>
        public static LoadedProject SelectProject(Workspace workspace, string? name)
        {
            if (name != null)
                return workspace.FindByName(name) ?? workspace.AllProjects.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ProjectException($"There is no project '{name}' (projects: {string.Join(", ", workspace.Projects.Select(p => p.Name))}).");
            return workspace.Startup ?? throw new ProjectException("The workspace has no project.");
        }

        /// <summary>The plan of a project, or an exception that lists what is wrong with it.</summary>
        public static BuildPlan PlanOrThrow(Workspace workspace, LoadedProject project, Func<string, string?>? textOf = null)
        {
            var plan = BuildPlan.Create(workspace, project, textOf);
            if (!plan.IsValid) throw new ProjectException(string.Join(Environment.NewLine, plan.Errors));
            return plan;
        }

        /// <summary>Compiles the project (as a program or, for a library, as declarations only): the errors are the compiler's own exceptions.</summary>
        public static LinkedProgram Check(BuildPlan plan, TargetProfile? target = null)
        {
            var linker = new Linker { Plan = plan, SourcePaths = plan.SourcePaths.Cast<string?>().ToList() };
            return linker.CompileAndLink(plan.SourceTexts, null, null, null, null, target);
        }

        /// <summary>Packs a library as a package: checks it, writes its files as one prelude and forges `name-version.fpk` into `outputDirectory` (default: the `output` setting, else `bin` next to the
        /// project). The package imports as `#import "Name"` and requires the libraries and packages the project references. Returns the path of the `.fpk`.</summary>
        public static string PackLibrary(BuildPlan plan, string? outputDirectory = null)
        {
            if (plan.Type != OutputType.Library) throw new ProjectException($"'{plan.Name}' is a program: only a library is packed.");
            Check(plan);   // it has to compile (and have no entry point) before it is shipped

            var project = plan.Project;
            string import = project.Project.ImportName;
            string version = PackageVersion(plan.Settings.Version);
            string outDir = Path.GetFullPath(outputDirectory ?? plan.Settings.Output ?? Path.Combine(project.Directory, "bin"));
            string workDir = Path.Combine(outDir, "obj-" + project.Name);
            Directory.CreateDirectory(workDir);
            try
            {
                string preludePath = Path.Combine(workDir, import + ".fire");
                var imports = new List<string>();
                var resources = new Dictionary<string, string>();
                File.WriteAllText(preludePath, Flatten(plan.Sources, imports, resources, project.Directory));

                var manifest = new PackageManifest
                {
                    Name = project.Name, Version = version, Author = plan.Settings.Author ?? "", Description = plan.Settings.Description ?? "", License = plan.Settings.License ?? "",
                };
                var lib = plan.Libraries.Values.Where(l => plan.Project.Project.References.Any(r => r.IsProject && Path.GetFullPath(r.Project!.Replace('\\', '/'), project.Directory) == l.Project.FilePath)).ToList();
                var packageImport = new PackageImport { Name = import, Prelude = preludePath, Native = plan.Native?.Native, Standard = project.Project.Standard };   // the C++ goes into the package (copied by the forge)
                // the files of `new Resource("...")` go into the package, next to the prelude, at the paths the prelude now writes (relative to the project)
                if (resources.Count > 0) packageImport.Resources = resources;
                foreach (var l in lib) { packageImport.Requires.Add(l.ImportName); manifest.Dependencies.Add(l.Name); }
                // what the files import (`#import "time"`) is required by the package: whoever imports it gets those too (the lines are not in the prelude)
                foreach (var name in imports)
                {
                    if (string.Equals(name, import, StringComparison.OrdinalIgnoreCase) || packageImport.Requires.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                    packageImport.Requires.Add(name);
                    string? provider = StandardPackages.Bridges.Contains(name.ToLowerInvariant()) ? StandardPackages.PackageNameOf(name.ToLowerInvariant())
                        : lib.Any(l => string.Equals(l.ImportName, name, StringComparison.OrdinalIgnoreCase)) ? null : PackageStore.Default.FindImport(name)?.Package.Name;
                    if (provider != null && !manifest.Dependencies.Contains(provider)) manifest.Dependencies.Add(provider);
                }
                foreach (var pkg in plan.Packages) if (!manifest.Dependencies.Contains(pkg.Package!)) manifest.Dependencies.Add(pkg.Package!);
                manifest.Imports.Add(packageImport);
                // the folder `templates/` of the project goes into the package: the templates it offers to whoever installs it
                string templates = Path.Combine(project.Directory, Fpk.TemplatesEntry);
                if (Directory.Exists(templates)) manifest.Templates = templates;

                string forge = Path.Combine(workDir, "package.json");
                manifest.Save(forge);
                var result = Fpk.Forge(forge, outDir);
                return result.PackagePath;
            }
            finally
            {
                try { Directory.Delete(workDir, true); } catch (IOException) { }
            }
        }

        /// <summary>The next version: `part` is `major`, `minor` or `patch` (the lower numbers become 0). `1.2.3` -> `1.2.4`; a version with four numbers keeps the fourth out (a package has three).</summary>
        public static string BumpVersion(string? version, string part = "patch")
        {
            var v = PackageVersion(version).Split('.').Select(int.Parse).ToArray();
            switch (part.ToLowerInvariant())
            {
                case "major": v[0]++; v[1] = 0; v[2] = 0; break;
                case "minor": v[1]++; v[2] = 0; break;
                case "patch": v[2]++; break;
                default: throw new ProjectException($"'{part}' is not major, minor or patch.");
            }
            return string.Join('.', v);
        }

        /// <summary>Is `text` a version a package can have (`1.2.3`: three numbers)?</summary>
        public static bool IsPackageVersion(string? text)
        {
            var parts = (text ?? "").Split('.');
            return parts.Length == 3 && parts.All(p => p.Length > 0 && p.All(char.IsDigit));
        }

        /// <summary>The file the package of a library gets in `outputDirectory` (see <see cref="PackLibrary"/>).</summary>
        public static string PackagePathFor(BuildPlan plan, string? outputDirectory = null) =>
            Path.Combine(Path.GetFullPath(outputDirectory ?? plan.Settings.Output ?? Path.Combine(plan.Project.Directory, "bin")), $"{plan.Project.Name}-{PackageVersion(plan.Settings.Version)}.fpk");

        /// <summary>`1.2.3.0` or `1.2` as a package version (three numbers); anything else: 1.0.0.</summary>
        public static string PackageVersion(string? version)
        {
            if (version == null) return "1.0.0";
            var parts = version.Split('.');
            if (parts.Length >= 3 && parts.Take(3).All(p => p.Length > 0 && p.All(char.IsDigit))) return string.Join('.', parts.Take(3));
            if (parts.Length == 2 && parts.All(p => p.Length > 0 && p.All(char.IsDigit))) return version + ".0";
            if (parts.Length == 1 && parts[0].Length > 0 && parts[0].All(char.IsDigit)) return version + ".0.0";
            return "1.0.0";
        }

        private static readonly Regex Include = new("^[ \\t]*#include[ \\t]+\"([^\"\\r\\n]+)\"", RegexOptions.Compiled);
        private static readonly Regex Import = new("^[ \\t]*#import[ \\t]+\"([^\"\\r\\n]+)\"", RegexOptions.Compiled);

        /// <summary>The files of a library as one text: an `#include` is replaced by the file it names (once), because the prelude of a package is read where no folder of the project is known.</summary>
        /// <param name="imports">Gets the names of the `#import` lines, which are left out (a package requires its imports instead).</param>
        /// <param name="resources">Gets the files of `new Resource("path")`: the path in the text becomes the file's path relative to <paramref name="baseDirectory"/> (a file outside of
        /// it: `external/name`), the key of the map; the value is the file.</param>
        public static string Flatten(IReadOnlyList<SourceFile> files, List<string>? imports = null, Dictionary<string, string>? resources = null, string? baseDirectory = null)
        {
            var sb = new StringBuilder();
            var done = new HashSet<string>(ProjectFiles.PathComparer);
            void Add(string path, string text, List<string> chain)
            {
                if (!done.Add(Path.GetFullPath(path))) return;
                sb.Append("// ---- ").AppendLine(Path.GetFileName(path));
                foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
                {
                    if (imports != null && Import.Match(line) is { Success: true } im)
                    {
                        if (!imports.Contains(im.Groups[1].Value, StringComparer.OrdinalIgnoreCase)) imports.Add(im.Groups[1].Value);
                        sb.AppendLine("// " + line.Trim());
                        continue;
                    }
                    var m = Include.Match(line);
                    if (!m.Success) { sb.AppendLine(resources != null ? Preprocessor.MapResourcePaths(line, p => ResourceKey(p, path)) : line); continue; }
                    string target = Path.GetFullPath(m.Groups[1].Value, Path.GetDirectoryName(Path.GetFullPath(path))!);
                    if (chain.Contains(target, ProjectFiles.PathComparer)) throw new ProjectException($"Circular include of '{target}'.");
                    string included;
                    try { included = File.ReadAllText(target); if (FireProject.IsMarkupFile(target)) included = BuildPlan.GenerateMarkupScript(target, included); }
                    catch (IOException ex) { throw new ProjectException($"{Path.GetFileName(path)}: '{m.Groups[1].Value}' could not be read: {ex.Message}"); }
                    catch (fire.UI.Markup.MarkupException ex) { throw new ProjectException($"{Path.GetFileName(target)}: {ex.Message}"); }
                    Add(target, included, chain.Append(target).ToList());
                }
            }
            string ResourceKey(string written, string file)
            {
                string full = Path.GetFullPath(written, Path.GetDirectoryName(Path.GetFullPath(file))!);
                if (!File.Exists(full)) throw new ProjectException($"{Path.GetFileName(file)}: the resource '{written}' ('{full}') does not exist.");
                string root = Path.GetFullPath(baseDirectory ?? Path.GetDirectoryName(Path.GetFullPath(file))!);
                string rel = Path.GetRelativePath(root, full).Replace('\\', '/');
                if (rel.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(rel)) rel = "external/" + Path.GetFileName(full);
                string key = rel;
                for (int k = 2; resources!.TryGetValue(key, out var other) && !ProjectFiles.PathComparer.Equals(other, full); k++)
                    key = rel.Substring(0, rel.LastIndexOf('/') + 1) + k + "_" + Path.GetFileName(full);
                resources[key] = full;
                return key;
            }
            foreach (var f in files) Add(f.Path, f.Text, new List<string> { Path.GetFullPath(f.Path) });
            return sb.ToString();
        }
    }
}
