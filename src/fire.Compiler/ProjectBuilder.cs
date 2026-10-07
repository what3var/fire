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
                File.WriteAllText(preludePath, Flatten(plan.Sources));

                var manifest = new PackageManifest
                {
                    Name = project.Name, Version = version, Author = plan.Settings.Author ?? "", Description = plan.Settings.Description ?? "", License = "",
                };
                var lib = plan.Libraries.Values.Where(l => plan.Project.Project.References.Any(r => r.IsProject && Path.GetFullPath(r.Project!.Replace('\\', '/'), project.Directory) == l.Project.FilePath)).ToList();
                var packageImport = new PackageImport { Name = import, Prelude = preludePath };
                foreach (var l in lib) { packageImport.Requires.Add(l.ImportName); manifest.Dependencies.Add(l.Name); }
                foreach (var pkg in plan.Packages) if (!manifest.Dependencies.Contains(pkg.Package!)) manifest.Dependencies.Add(pkg.Package!);
                manifest.Imports.Add(packageImport);

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

        /// <summary>The files of a library as one text: an `#include` is replaced by the file it names (once), because the prelude of a package is read where no folder of the project is known.</summary>
        public static string Flatten(IReadOnlyList<SourceFile> files)
        {
            var sb = new StringBuilder();
            var done = new HashSet<string>(ProjectFiles.PathComparer);
            void Add(string path, string text, List<string> chain)
            {
                if (!done.Add(Path.GetFullPath(path))) return;
                sb.Append("// ---- ").AppendLine(Path.GetFileName(path));
                foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
                {
                    var m = Include.Match(line);
                    if (!m.Success) { sb.AppendLine(line); continue; }
                    string target = Path.GetFullPath(m.Groups[1].Value, Path.GetDirectoryName(Path.GetFullPath(path))!);
                    if (string.Equals(Path.GetExtension(target), ".fxml", StringComparison.OrdinalIgnoreCase)) throw new ProjectException($"{Path.GetFileName(path)}: a markup file ({m.Groups[1].Value}) cannot be included in a library that is packed.");
                    if (chain.Contains(target, ProjectFiles.PathComparer)) throw new ProjectException($"Circular include of '{target}'.");
                    string included;
                    try { included = File.ReadAllText(target); }
                    catch (IOException ex) { throw new ProjectException($"{Path.GetFileName(path)}: '{m.Groups[1].Value}' could not be read: {ex.Message}"); }
                    Add(target, included, chain.Append(target).ToList());
                }
            }
            foreach (var f in files) Add(f.Path, f.Text, new List<string> { Path.GetFullPath(f.Path) });
            return sb.ToString();
        }
    }
}
