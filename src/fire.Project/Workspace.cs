namespace fire.Projects
{
    /// <summary>A project of the workspace: the model and the files it consists of right now.</summary>
    public sealed class LoadedProject
    {
        public FireProject Project { get; }
        public string FilePath => Project.FilePath!;
        public string Directory => Project.Directory!;
        /// <summary>false: a project that a solution project references but that is not part of the solution.</summary>
        public bool InSolution { get; internal set; }
        public string Name => Project.Name;

        /// <summary>The files in compile order (see ProjectFiles); <see cref="Refresh"/> reads the folder again.</summary>
        public IReadOnlyList<string> Files { get; private set; } = Array.Empty<string>();
        public IReadOnlyList<string> Problems { get; private set; } = Array.Empty<string>();
        /// <summary>The other files in the project folder: resources, C++ sources, notes (not compiled).</summary>
        public IReadOnlyList<string> ContentFiles { get; private set; } = Array.Empty<string>();
        /// <summary>The folders below the project folder (relative paths with `/`), also the empty ones.</summary>
        public IReadOnlyList<string> Folders { get; private set; } = Array.Empty<string>();
        private HashSet<string> _fileSet = new(ProjectFiles.PathComparer);

        public LoadedProject(FireProject project, bool inSolution)
        {
            Project = project;
            InSolution = inSolution;
            Refresh();
        }

        public void Refresh()
        {
            var problems = new List<string>(Project.Validate());
            Files = ProjectFiles.Resolve(Project, problems);
            Problems = problems;
            ContentFiles = ProjectFiles.ContentFiles(Project.Directory!, Files);
            Folders = ProjectFiles.Folders(Project.Directory!);
            _fileSet = new HashSet<string>(Files, ProjectFiles.PathComparer);
        }

        public bool Contains(string path) => _fileSet.Contains(Path.GetFullPath(path));
    }

    /// <summary>
    /// What the editor (or the command line) has open of projects: a solution with its projects, or a single project, or nothing - single files that belong to no project are always possible next
    /// to it. The workspace answers the question the interface asks all the time: which project does this file belong to (so that build and run take the project and not the single file)?
    /// Projects that a solution project references but that are not in the solution are loaded too (they are built as libraries of it).
    /// </summary>
    public sealed class Workspace
    {
        private readonly Dictionary<string, LoadedProject> _byPath = new(ProjectFiles.PathComparer);
        private readonly List<LoadedProject> _members = new();

        /// <summary>The solution, null when a single project was opened (or nothing).</summary>
        public FireSolution? Solution { get; private set; }

        /// <summary>The projects of the solution (or the single project), in the order they are shown.</summary>
        public IReadOnlyList<LoadedProject> Projects => _members;

        /// <summary>Everything loaded, with the referenced projects outside of the solution.</summary>
        public IEnumerable<LoadedProject> AllProjects => _byPath.Values;

        public bool IsOpen => _members.Count > 0 || Solution != null;

        /// <summary>The name to show: the solution's, or the project's.</summary>
        public string Name => Solution?.Name ?? (_members.Count == 1 ? _members[0].Name : "");

        /// <summary>The file that was opened (the solution file, else the project file).</summary>
        public string? FilePath => Solution?.FilePath ?? (_members.Count == 1 ? _members[0].FilePath : null);

        public event Action? Changed;
        private void RaiseChanged() => Changed?.Invoke();

        /// <summary>Opens a `.firesln` or a `.fireproj`.</summary>
        public static Workspace Open(string path)
        {
            var w = new Workspace();
            w.Load(path);
            return w;
        }

        public void Load(string path)
        {
            string full = Path.GetFullPath(path);
            string ext = Path.GetExtension(full);
            Close(raise: false);
            if (string.Equals(ext, FireSolution.Extension, StringComparison.OrdinalIgnoreCase))
            {
                Solution = FireSolution.Load(full);
                foreach (var rel in Solution.Projects)
                {
                    string projectPath = Path.GetFullPath(rel.Replace('\\', '/'), Solution.Directory!);
                    var p = LoadProject(projectPath, inSolution: true);
                    if (!_members.Contains(p)) _members.Add(p);
                }
            }
            else if (string.Equals(ext, FireProject.Extension, StringComparison.OrdinalIgnoreCase))
            {
                _members.Add(LoadProject(full, inSolution: true));
            }
            else throw new ProjectException($"'{Path.GetFileName(full)}' is neither a solution ({FireSolution.Extension}) nor a project ({FireProject.Extension}).");
            RaiseChanged();
        }

        public void Close() => Close(raise: true);

        private void Close(bool raise)
        {
            Solution = null;
            _members.Clear();
            _byPath.Clear();
            if (raise) RaiseChanged();
        }

        /// <summary>Loads a project (once: the same path gives the same object).</summary>
        public LoadedProject LoadProject(string path, bool inSolution = false)
        {
            string full = Path.GetFullPath(path);
            if (_byPath.TryGetValue(full, out var existing))
            {
                if (inSolution) existing.InSolution = true;
                return existing;
            }
            var loaded = new LoadedProject(FireProject.Load(full), inSolution);
            _byPath[full] = loaded;
            return loaded;
        }

        /// <summary>The project of a solution by name (null: none).</summary>
        public LoadedProject? FindByName(string name) => _members.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>The project that runs when the solution is run: the one named by `startup`, else the first program.</summary>
        public LoadedProject? Startup
        {
            get
            {
                if (Solution?.Startup != null && FindByName(Solution.Startup) is { } named) return named;
                return _members.FirstOrDefault(p => p.Project.Type == OutputType.Exe) ?? _members.FirstOrDefault();
            }
        }

        /// <summary>The project a file belongs to, null for a file outside of every project. A file that two projects hold counts for the startup project, else the first one. Projects that are only
        /// referenced (not in the solution) count too: their files are part of a library of the solution.</summary>
        public LoadedProject? FindProjectOf(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return null;
            string full = Path.GetFullPath(filePath);
            var startup = Startup;
            if (startup != null && startup.Contains(full)) return startup;
            return _members.FirstOrDefault(p => p.Contains(full)) ?? _byPath.Values.FirstOrDefault(p => !p.InSolution && p.Contains(full));
        }

        /// <summary>Reads the folders of all projects again (a file was added or deleted outside of the editor).</summary>
        public void Refresh()
        {
            foreach (var p in _byPath.Values) p.Refresh();
            RaiseChanged();
        }

        // -------------------------------------------------------------------------------------------------------------
        // Changes (written to the files)
        // -------------------------------------------------------------------------------------------------------------
        /// <summary>A new solution file with no project (saved).</summary>
        public static Workspace CreateSolution(string path, string? name = null)
        {
            string full = Path.GetFullPath(path);
            var solution = new FireSolution { Name = name ?? Path.GetFileNameWithoutExtension(full) };
            solution.Save(full);
            return Open(full);
        }

        /// <summary>Makes a solution from a template (see <see cref="ProjectTemplates"/>): the solution file in `folder`, and for a template that makes a project the project `name` in `folder/name`. Opens it.</summary>
        public static Workspace CreateSolution(ProjectTemplate template, string folder, string name)
        {
            string path = ProjectTemplates.CreateSolution(template, folder, name);
            return Open(path);
        }

        /// <summary>Makes a project from a template in the folder `parentFolder/name` (saved) and adds it to the solution, or - without a solution - opens it on its own.</summary>
        public LoadedProject CreateProject(ProjectTemplate template, string parentFolder, string name)
        {
            string path = ProjectTemplates.CreateProject(template, parentFolder, name);
            if (Solution == null) { Load(path); return _members[0]; }
            var loaded = LoadProject(path, inSolution: true);
            AddToSolution(loaded);
            RaiseChanged();
            return loaded;
        }

        /// <summary>Makes a folder in the solution (a project made there lives in a subfolder of it); the empty ones are kept in the solution file.</summary>
        public string AddFolder(string fullPath)
        {
            if (Solution == null) throw new ProjectException("There is no solution: open or create one first.");
            string full = Path.GetFullPath(fullPath);
            string rel = ProjectFiles.Relative(Solution.Directory!, full);
            if (rel.StartsWith("..")) throw new ProjectException("A folder of the solution has to be inside the solution folder.");
            System.IO.Directory.CreateDirectory(full);
            if (!Solution.Folders.Any(f => ProjectFiles.PathComparer.Equals(Path.GetFullPath(f.Replace('\\', '/'), Solution.Directory!), full))) Solution.Folders.Add(rel);
            Solution.Save();
            RaiseChanged();
            return full;
        }

        /// <summary>Takes a folder out of the solution file (the folder and what is in it stay on disk).</summary>
        public void RemoveFolder(string fullPath)
        {
            if (Solution == null) return;
            string full = Path.GetFullPath(fullPath);
            Solution.Folders.RemoveAll(f => ProjectFiles.PathComparer.Equals(Path.GetFullPath(f.Replace('\\', '/'), Solution.Directory!), full));
            Solution.Save();
            RaiseChanged();
        }

        /// <summary>Makes a new project (saved; a program with one file `main.script` when `withMain`) and adds it to the solution if one is open.</summary>
        public LoadedProject CreateProject(string path, OutputType type, string? name = null, bool withMain = true)
        {
            string full = Path.GetFullPath(path);
            var project = new FireProject { Name = name ?? Path.GetFileNameWithoutExtension(full), Type = type, FilePath = full };
            if (withMain)
            {
                string dir = project.Directory!;
                System.IO.Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, type == OutputType.Library ? project.ImportName.ToLowerInvariant() + ".script" : "main.script");
                if (!File.Exists(file))
                    File.WriteAllText(file, type == OutputType.Library
                        ? $"// The library {project.Name}: classes and functions, no statements at the top level.\nclass Greeter {{\n    static string Hello(string name) {{ return \"Hello, \" + name }}\n}}\n"
                        : "print(\"Hello from " + project.Name + "\")\n");
            }
            project.Save(full);
            var loaded = LoadProject(full, inSolution: Solution != null || _members.Count == 0);
            if (Solution != null) AddToSolution(loaded);
            else if (!_members.Contains(loaded)) _members.Add(loaded);
            RaiseChanged();
            return loaded;
        }

        /// <summary>Adds an existing project file to the solution (saved).</summary>
        public LoadedProject AddProject(string projectPath)
        {
            if (Solution == null) throw new ProjectException("There is no solution: open or create one first.");
            var loaded = LoadProject(projectPath, inSolution: true);
            AddToSolution(loaded);
            RaiseChanged();
            return loaded;
        }

        private void AddToSolution(LoadedProject loaded)
        {
            if (!_members.Contains(loaded)) _members.Add(loaded);
            string rel = ProjectFiles.Relative(Solution!.Directory!, loaded.FilePath);
            if (!Solution.Projects.Any(p => ProjectFiles.PathComparer.Equals(Path.GetFullPath(p, Solution.Directory!), loaded.FilePath))) Solution.Projects.Add(rel);
            Solution.Save();
        }

        /// <summary>Takes a project out of the solution (its files stay where they are).</summary>
        public void RemoveProject(LoadedProject project)
        {
            if (Solution == null) return;
            _members.Remove(project);
            Solution.Projects.RemoveAll(p => ProjectFiles.PathComparer.Equals(Path.GetFullPath(p, Solution.Directory!), project.FilePath));
            if (string.Equals(Solution.Startup, project.Name, StringComparison.OrdinalIgnoreCase)) Solution.Startup = null;
            Solution.Save();
            project.InSolution = false;
            if (!_members.Any(m => ReferencesProject(m, project))) _byPath.Remove(project.FilePath);
            RaiseChanged();
        }

        private bool ReferencesProject(LoadedProject from, LoadedProject target) =>
            from.Project.References.Any(r => r.IsProject && ProjectFiles.PathComparer.Equals(Path.GetFullPath(r.Project!.Replace('\\', '/'), from.Directory), target.FilePath));

        public void SetStartup(LoadedProject project)
        {
            if (Solution == null) return;
            Solution.Startup = project.Name;
            Solution.Save();
            RaiseChanged();
        }

        /// <summary>Adds a file to a project: nothing to do when the project's patterns take it already (it is in its folder); else it is named in `files` (saved).</summary>
        public void AddFile(LoadedProject project, string filePath)
        {
            string full = Path.GetFullPath(filePath);
            if (project.Contains(full)) return;
            if (!FireProject.IsSourceFile(full)) { project.Refresh(); RaiseChanged(); return; }   // a file that is no source (a resource, a header, ...) is not compiled: it is content of the project folder
            if (project.Project.Files.Count == 0) project.Project.Files.AddRange(FireProject.DefaultFiles);   // naming a file must not drop the files that the default patterns take
            project.Project.Files.Add(ProjectFiles.Relative(project.Directory, full));
            project.Project.Save();
            project.Refresh();
            RaiseChanged();
        }

        /// <summary>Copies a file into a folder of the project (a resource, a picture, data: nothing is compiled from it until the code says `new Resource("...")`). Returns the new path; an existing file is
        /// not overwritten (a ProjectException).</summary>
        public string AddContentFile(LoadedProject project, string sourcePath, string destinationFolder)
        {
            string folder = Path.GetFullPath(destinationFolder);
            if (!ProjectFiles.Relative(project.Directory, folder).Equals(".") && ProjectFiles.Relative(project.Directory, folder).StartsWith("..")) throw new ProjectException("The folder is not inside the project.");
            string target = Path.Combine(folder, Path.GetFileName(sourcePath));
            if (ProjectFiles.PathComparer.Equals(Path.GetFullPath(sourcePath), target)) { project.Refresh(); RaiseChanged(); return target; }   // it is there already
            if (File.Exists(target)) throw new ProjectException($"'{Path.GetFileName(sourcePath)}' exists already in {ProjectFiles.Relative(project.Directory, folder)}.");
            System.IO.Directory.CreateDirectory(folder);
            File.Copy(sourcePath, target);
            project.Refresh();
            RaiseChanged();
            return target;
        }

        /// <summary>Gives the project a native part (C++ in the folder `native/`) if it has none, with a starting header that has one example function. Returns the path of the header.</summary>
        public string AddNative(LoadedProject project)
        {
            string ident = project.Project.ImportName.ToLowerInvariant();
            string header = Path.Combine(project.Directory, "native", ident + ".hpp");
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(header)!);
            if (!File.Exists(header)) File.WriteAllText(header, ProjectTemplates.NativeHeader(project.Name, project.Project.ImportName));
            if (project.Project.Native == null) { project.Project.Native = new ProjectNative(); project.Project.Save(); }
            project.Refresh();
            RaiseChanged();
            return header;
        }

        /// <summary>Takes a file out of a project (saved; the file itself stays): an explicit entry is removed, a file that a pattern takes is excluded.</summary>
        public void RemoveFile(LoadedProject project, string filePath)
        {
            string full = Path.GetFullPath(filePath);
            string rel = ProjectFiles.Relative(project.Directory, full);
            int removed = project.Project.Files.RemoveAll(f => string.Equals(f.Replace('\\', '/'), rel, ProjectFiles.PathComparison));
            project.Refresh();
            if (project.Contains(full)) project.Project.Exclude.Add(rel);
            if (project.Project.Entry != null && string.Equals(project.Project.Entry.Replace('\\', '/'), rel, ProjectFiles.PathComparison)) project.Project.Entry = null;
            project.Project.Save();
            project.Refresh();
            RaiseChanged();
        }

        /// <summary>Adds a reference to a library project (saved).</summary>
        public void AddReference(LoadedProject project, LoadedProject library)
        {
            string rel = ProjectFiles.Relative(project.Directory, library.FilePath);
            if (project.Project.References.Any(r => r.IsProject && ProjectFiles.PathComparer.Equals(Path.GetFullPath(r.Project!.Replace('\\', '/'), project.Directory), library.FilePath))) return;
            project.Project.References.Add(new ProjectReference { Project = rel });
            project.Project.Save();
            RaiseChanged();
        }

        public void AddPackageReference(LoadedProject project, string package, string? version = null)
        {
            if (project.Project.References.Any(r => string.Equals(r.Package, package, StringComparison.OrdinalIgnoreCase))) return;
            project.Project.References.Add(new ProjectReference { Package = package, Version = version });
            project.Project.Save();
            RaiseChanged();
        }

        public void RemoveReference(LoadedProject project, ProjectReference reference)
        {
            project.Project.References.Remove(reference);
            project.Project.Save();
            RaiseChanged();
        }

        /// <summary>The settings of the project that was changed (or of the solution) were edited: write them.</summary>
        public void SaveProject(LoadedProject project)
        {
            project.Project.Save();
            project.Refresh();
            RaiseChanged();
        }

        public void SaveSolution() { Solution?.Save(); RaiseChanged(); }
    }
}
