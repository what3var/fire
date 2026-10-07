namespace fire.Projects
{
    /// <summary>A source file of a build: where it is, what is in it right now (the editor passes the text of an open document that was not saved), and which project it belongs to.</summary>
    public sealed class SourceFile
    {
        public string Path { get; }
        public string Text { get; }
        public string ProjectName { get; }
        public SourceFile(string path, string text, string projectName) { Path = path; Text = text; ProjectName = projectName; }
        public string FileName => System.IO.Path.GetFileName(Path);
        public string Directory => System.IO.Path.GetDirectoryName(Path) ?? "";
    }

    /// <summary>A library project that a build can import: its files, the libraries it needs itself, and its own settings.</summary>
    public sealed class LibraryPlan
    {
        public string ImportName { get; init; } = "";
        public LoadedProject Project { get; init; } = null!;
        public IReadOnlyList<SourceFile> Sources { get; init; } = Array.Empty<SourceFile>();
        /// <summary>The import names of the libraries it references.</summary>
        public IReadOnlyList<string> Requires { get; init; } = Array.Empty<string>();
        /// <summary>The names of the packages it references.</summary>
        public IReadOnlyList<ProjectReference> Packages { get; init; } = Array.Empty<ProjectReference>();
        public ProjectSettings Settings { get; init; } = new();
        /// <summary>The C++ natives of the library (null: none).</summary>
        public NativePart? Native { get; init; }
        public string Name => Project.Name;
    }

    /// <summary>
    /// Everything the compiler needs to build one project: its files (with their text), the libraries it can import (`#import "Name"`: a reference to a library project makes the library available, the
    /// import turns it on - exactly like a package), the packages it references, and the settings that apply (the project's before the solution's; the tags in the source are taken into
    /// account by the compiler after these). Making the plan reads the files and finds out what is wrong with the project (<see cref="Errors"/>); nothing is compiled yet.
    /// </summary>
    public sealed class BuildPlan
    {
        public LoadedProject Project { get; private init; } = null!;
        public OutputType Type => Project.Project.Type;
        public IReadOnlyList<SourceFile> Sources { get; private init; } = Array.Empty<SourceFile>();
        /// <summary>All libraries that are reachable through the references, by import name (any case).</summary>
        public IReadOnlyDictionary<string, LibraryPlan> Libraries { get; private init; } = new Dictionary<string, LibraryPlan>(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyList<ProjectReference> Packages { get; private init; } = Array.Empty<ProjectReference>();
        /// <summary>The settings that apply: the project's over the solution's, with paths made absolute.</summary>
        public ProjectSettings Settings { get; private init; } = new();
        public IReadOnlyList<string> Errors { get; private init; } = Array.Empty<string>();
        /// <summary>The C++ natives of the project itself (null: none).</summary>
        public NativePart? Native { get; private init; }
        /// <summary>The native parts of the project and of all libraries that are reachable (they are known to the build whether a library is imported or not; an import turns one on).</summary>
        public IEnumerable<NativePart> NativeParts => (Native != null ? new[] { Native } : Array.Empty<NativePart>()).Concat(Libraries.Values.Where(l => l.Native != null).Select(l => l.Native!));
        public bool IsValid => Errors.Count == 0;

        public IReadOnlyList<string> SourceTexts => Sources.Select(s => s.Text).ToList();
        public IReadOnlyList<string> SourcePaths => Sources.Select(s => s.Path).ToList();
        public string Name => Project.Name;

        /// <summary>The libraries named by `names` (import names) and everything they need, the ones that others need first.</summary>
        public IReadOnlyList<LibraryPlan> LibraryOrder(IEnumerable<string> names)
        {
            var ordered = new List<LibraryPlan>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Visit(string name)
            {
                if (!Libraries.TryGetValue(name, out var lib) || !visited.Add(lib.ImportName)) return;
                foreach (var need in lib.Requires) Visit(need);
                ordered.Add(lib);
            }
            foreach (var n in names) Visit(n);
            return ordered;
        }

        /// <summary>The text of a file: what `textOf` says (an unsaved document), else the file.</summary>
        private static string ReadText(string path, Func<string, string?>? textOf)
        {
            if (textOf?.Invoke(path) is { } open) return open;
            return File.ReadAllText(path);
        }

        public static BuildPlan Create(Workspace workspace, LoadedProject project, Func<string, string?>? textOf = null)
        {
            var errors = new List<string>();
            project.Refresh();
            foreach (var p in project.Problems) errors.Add(p);

            var libraries = new Dictionary<string, LibraryPlan>(StringComparer.OrdinalIgnoreCase);
            var byProjectPath = new Dictionary<string, LibraryPlan>(ProjectFiles.PathComparer);
            var packages = new List<ProjectReference>();
            var stack = new List<LoadedProject> { project };

            // returns the plan of a library (made once); null when there is none (an error is noted)
            LibraryPlan? PlanOf(LoadedProject lib)
            {
                if (byProjectPath.TryGetValue(lib.FilePath, out var done)) return done;
                lib.Refresh();
                foreach (var p in lib.Problems) errors.Add($"{lib.Name}: {p}");
                var requires = new List<string>();
                var libPackages = new List<ProjectReference>();
                stack.Add(lib);
                foreach (var r in lib.Project.References)
                {
                    if (!r.IsProject) { libPackages.Add(r); continue; }
                    if (Referenced(lib, r) is { } sub && PlanOf(sub) is { } subPlan) requires.Add(subPlan.ImportName);
                }
                stack.RemoveAt(stack.Count - 1);
                var files = new List<SourceFile>();
                foreach (var f in lib.Files)
                {
                    if (TryReadSource(f, textOf, lib.Name, errors, lib.Name + ": ") is { } source) files.Add(source);
                }
                var plan = new LibraryPlan
                {
                    ImportName = lib.Project.ImportName, Project = lib, Sources = files, Requires = requires, Packages = libPackages,
                    Settings = Merge(lib, workspace), Native = ProjectNatives.Resolve(lib, errors),
                };
                byProjectPath[lib.FilePath] = plan;
                if (libraries.TryGetValue(plan.ImportName, out var clash) && clash.Project != lib)
                    errors.Add($"The libraries '{clash.Name}' and '{lib.Name}' have the same import name '{plan.ImportName}': set 'import' in one of them.");
                else libraries[plan.ImportName] = plan;
                return plan;
            }

            LoadedProject? Referenced(LoadedProject from, ProjectReference reference)
            {
                string path = Path.GetFullPath(reference.Project!.Replace('\\', '/'), from.Directory);
                if (!File.Exists(path)) { errors.Add($"{from.Name}: the referenced project '{reference.Project}' does not exist."); return null; }
                LoadedProject target;
                try { target = workspace.LoadProject(path); }
                catch (ProjectException ex) { errors.Add($"{from.Name}: {ex.Message}"); return null; }
                if (stack.Any(s => ProjectFiles.PathComparer.Equals(s.FilePath, target.FilePath)))
                {
                    errors.Add($"The projects reference each other in a circle: {string.Join(" -> ", stack.Select(s => s.Name).Append(target.Name))}.");
                    return null;
                }
                if (target.Project.Type != OutputType.Library)
                {
                    errors.Add($"{from.Name}: '{target.Name}' is a program, only a library can be referenced (set \"type\": \"library\" in it).");
                    return null;
                }
                return target;
            }

            foreach (var r in project.Project.References)
            {
                if (!r.IsProject) { packages.Add(r); continue; }
                if (Referenced(project, r) is { } lib) PlanOf(lib);
            }

            var sources = new List<SourceFile>();
            foreach (var f in project.Files)
            {
                if (TryReadSource(f, textOf, project.Name, errors, "") is { } source) sources.Add(source);
            }
            if (sources.Count == 0 && project.Project.Type == OutputType.Exe && errors.Count == 0) errors.Add($"The project '{project.Name}' has no files.");

            return new BuildPlan
            {
                Project = project, Sources = sources, Libraries = libraries, Packages = packages,
                Settings = Merge(project, workspace), Errors = errors, Native = ProjectNatives.Resolve(project, errors),
            };
        }

        /// <summary>Reads a source file of a project; the markup of a user interface (`.fxml`) is read as the script that is generated from it - the same text that `#include "x.fxml"` would insert.
        /// Null (and an error) when the file cannot be read or is no valid markup.</summary>
        private static SourceFile? TryReadSource(string path, Func<string, string?>? textOf, string projectName, List<string> errors, string prefix)
        {
            try
            {
                string text = ReadText(path, textOf);
                if (FireProject.IsMarkupFile(path)) text = GenerateMarkupScript(path, text);
                return new SourceFile(path, text, projectName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors.Add($"{prefix}Cannot read '{path}': {ex.Message}"); }
            catch (fire.UI.Markup.MarkupException ex) { errors.Add($"{prefix}{System.IO.Path.GetFileName(path)}: {ex.Message}"); }
            return null;
        }

        /// <summary>The script generated from the markup of a user interface (docs/UI_MARKUP.md); throws MarkupException when the markup is not valid.</summary>
        public static string GenerateMarkupScript(string path, string markup) =>
            fire.UI.Markup.FireUiGenerator.Generate(fire.UI.Markup.MarkupParser.Parse(markup), path);

        private static ProjectSettings Merge(LoadedProject p, Workspace workspace)
        {
            var own = (p.Project.Settings ?? new ProjectSettings()).WithAbsolutePaths(p.Directory);
            ProjectSettings? shared = workspace.Solution is { } s && s.Settings != null ? s.Settings.WithAbsolutePaths(s.Directory!) : null;
            return ProjectSettings.Merge(own, shared);
        }
    }
}
