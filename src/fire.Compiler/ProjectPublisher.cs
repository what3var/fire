using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using fire.Native;
using fire.Projects;
using fire.Runtime;

namespace fire.Compiler
{
    /// <summary>What a publish is asked to do (see <see cref="ProjectPublisher.Publish"/>).</summary>
    public sealed class PublishOptions
    {
        /// <summary>The text of a file that is open and not saved (the editor); null: the files.</summary>
        public Func<string, string?>? TextOf { get; init; }
        /// <summary>The progress, the log lines and Cancel (null: <see cref="BuildMonitor.Current"/>).</summary>
        public BuildMonitor? Monitor { get; init; }
        /// <summary>Build every project again, even when nothing changed.</summary>
        public bool Rebuild { get; init; }
        /// <summary>The toolchain for every native build (`forge publish --toolchain`); null: the project's, else the compiler the machine chose for publishing.</summary>
        public string? Toolchain { get; init; }
        /// <summary>Publish projects that have publishing off as well (a test, `forge publish --all`); the spark commands never do.</summary>
        public bool IncludeDisabled { get; init; }
        /// <summary>`forge publish --quick[=level]` (spark's hare switch): the native programs as quick builds for tests (NativeBuilder.QuickBuild: -O0 or -O1, in units where the
        /// toolchain splits) - marked in the log, the summary and the publish folder; its own up-to-date state. Null: a normal publish.</summary>
        public int? Quick { get; init; }
    }

    /// <summary>How the publish of one project went.</summary>
    public sealed class ProjectPublishResult
    {
        public required LoadedProject Project { get; init; }
        public bool Ok { get; init; }
        /// <summary>Not tried: publishing is off, or a library it references failed (<see cref="Error"/> says which).</summary>
        public bool Skipped { get; init; }
        /// <summary>The program was up to date: it was not built again.</summary>
        public bool UpToDate { get; init; }
        /// <summary>The program (a folder for a toolchain that only writes files, the `.fpk` of a library).</summary>
        public string? Program { get; init; }
        public string Engine { get; init; } = "";
        public string Mode { get; init; } = "";
        public string? TargetName { get; init; }
        /// <summary>The folders it was copied into.</summary>
        public IReadOnlyList<string> Folders { get; init; } = Array.Empty<string>();
        /// <summary>The files written into the folders (those that were up to date there are not counted).</summary>
        public int FilesCopied { get; init; }
        public string? Error { get; init; }
        public IReadOnlyList<Diagnostic> Diagnostics { get; init; } = Array.Empty<Diagnostic>();
    }

    /// <summary>How a publish went: every project that was asked for, in the order they were published.</summary>
    public sealed class PublishResult
    {
        public List<ProjectPublishResult> Projects { get; } = new();
        public bool Cancelled { get; set; }
        /// <summary>A quick publish (<see cref="PublishOptions.Quick"/>): its level; the summary says so ("Schnellbuild (-O0, geteilt)"), so it is not taken for a release.</summary>
        public int? Quick { get; set; }
        public int Published => Projects.Count(p => p.Ok);
        public int Failed => Projects.Count(p => !p.Ok && !p.Skipped);
        public bool Ok => !Cancelled && Failed == 0 && Projects.All(p => p.Ok || p.Skipped && p.Error == null);
        public IEnumerable<Diagnostic> Diagnostics => Projects.SelectMany(p => p.Diagnostics);

        /// <summary>"2 published to /x/publish/App, /x/publish/Tool (1 up to date), 1 failed".</summary>
        public string Summary
        {
            get
            {
                if (Cancelled) return "Publish cancelled.";
                var folders = Projects.Where(p => p.Ok).SelectMany(p => p.Folders).Distinct(ProjectFiles.PathComparer).ToList();
                var sb = new StringBuilder($"{Published} project{(Published == 1 ? "" : "s")} published");
                if (folders.Count > 0) sb.Append(" to ").Append(string.Join(", ", folders));
                int upToDate = Projects.Count(p => p.Ok && p.UpToDate);
                if (upToDate > 0) sb.Append($" ({upToDate} up to date, not built again)");
                if (Failed > 0) sb.Append($", {Failed} failed");
                int skipped = Projects.Count(p => p.Skipped);
                if (skipped > 0) sb.Append($", {skipped} skipped");
                if (Quick is { } level && Projects.Any(p => p.Engine == "native")) sb.Append($" - {NativeBuilder.QuickBuild.Label(level)}, not for release");
                return sb.Append('.').ToString();
            }
        }
    }

    /// <summary>
    /// Publish (docs/tools/projects.md, "Publish"): every project that has `publish.enabled`, in the order of its references (<see cref="Workspace.BuildOrder"/>), is built - again only when
    /// something changed since the last publish build (the sources and their text, the content of the project and of its libraries, the settings, the target, the compiler) - with the engine
    /// and mode of `publish` (default: the project's engine, else native; release) into its usual build folder, and then copied with what it needs (the shared libraries of linkage "shared",
    /// the files of the program next to it, the files with the build action Copy, keeping their paths) into every target folder. Nothing else in a target folder is touched, unless
    /// `publish.clean` empties it first. The same for spark and `forge publish`.
    /// </summary>
    public static class ProjectPublisher
    {
        /// <summary>The folder below `obj/` where a publish remembers what it built (Clean removes it with `obj/`).</summary>
        public const string StampFolder = "publish";

        /// <summary>The projects Publish Solution takes: the members of the workspace that have publishing on, a library before whoever references it.</summary>
        public static IReadOnlyList<LoadedProject> EnabledProjects(Workspace workspace) => workspace.BuildOrder().Where(p => p.Project.Publish?.Enabled == true).ToList();

        /// <summary>Publishes `projects` (null: <see cref="EnabledProjects"/>) - see the class. Runs on the caller's thread; cancelled (the monitor), it stops after the step that runs.</summary>
        public static PublishResult Publish(Workspace workspace, IReadOnlyList<LoadedProject>? projects = null, PublishOptions? options = null)
        {
            options ??= new PublishOptions();
            var monitor = options.Monitor ?? BuildMonitor.Current;
            using var watching = BuildMonitor.Use(monitor);
            var token = monitor?.Token ?? CancellationToken.None;
            var result = new PublishResult { Quick = options.Quick };
            // (the order of the references: a library first; a project that is no member of the workspace keeps its place at the end)
            var order = workspace.BuildOrder();
            var asked = (projects ?? EnabledProjects(workspace)).Distinct().ToList();
            var ordered = order.Where(asked.Contains).Concat(asked.Where(p => !order.Contains(p))).ToList();
            var failed = new HashSet<LoadedProject>();
            void Log(string line) { if (monitor != null) monitor.Line(line); }
            if (options.Quick is { } quickLevel) Log($"{NativeBuilder.QuickBuild.Label(quickLevel)}: a quick publish for tests, not a release - the native programs are built without full optimisation.");
            try
            {
                for (int i = 0; i < ordered.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var project = ordered[i];
                    Log($"------ Publish {i + 1}/{ordered.Count}: {project.Name} ------");
                    if (project.Project.Publish?.Enabled != true && !options.IncludeDisabled)
                    {
                        Log($"{project.Name}: skipped - publishing is off (Project Properties > Publish).");
                        result.Projects.Add(new ProjectPublishResult { Project = project, Skipped = true });
                        continue;
                    }
                    if (workspace.ReferencedProjects(project).FirstOrDefault(failed.Contains) is { } broken)
                    {
                        failed.Add(project);
                        string why = $"the library {broken.Name} it references did not publish";
                        Log($"{project.Name}: skipped - {why}.");
                        result.Projects.Add(new ProjectPublishResult { Project = project, Skipped = true, Error = why });
                        continue;
                    }
                    var one = PublishOne(workspace, project, options, Log, token);
                    if (!one.Ok) failed.Add(project);
                    result.Projects.Add(one);
                }
            }
            catch (OperationCanceledException) { result.Cancelled = true; Log("Publish cancelled."); }
            if (!result.Cancelled) Log($"========== Publish: {result.Summary} ==========");
            return result;
        }

        private static ProjectPublishResult PublishOne(Workspace workspace, LoadedProject project, PublishOptions options, Action<string> log, CancellationToken token)
        {
            ProjectPublishResult Fail(string error, IReadOnlyList<Diagnostic>? diagnostics = null, string engine = "", string mode = "")
            {
                log($"{project.Name}: error: {error.TrimEnd()}");
                return new ProjectPublishResult { Project = project, Error = error, Diagnostics = diagnostics ?? Array.Empty<Diagnostic>(), Engine = engine, Mode = mode };
            }

            BuildPlan plan;
            try { plan = BuildPlan.Create(workspace, project, options.TextOf); }
            catch (Exception ex) when (ex is ProjectException or IOException or UnauthorizedAccessException) { return Fail(ex.Message); }
            if (!plan.IsValid) return Fail(string.Join(Environment.NewLine, plan.Errors));

            var (engine, modeName, targetName, toolchainName) = PublishSettings.Choices(project.Project.Publish, plan.Settings);
            if (!Enum.TryParse<VmExecutionMode>(modeName, true, out var mode)) return Fail($"'{modeName}' is no mode (debug, release, performance).");
            var folders = PublishSettings.TargetFolders(project, workspace.Solution?.Directory);

            // ---- the program: built when it is not up to date ----
            string output;
            bool upToDate;
            string? key;
            NativeConfig? config = null;
            TargetProfile? target = null;
            var diagnostics = new List<Diagnostic>();
            try
            {
                if (plan.Type == OutputType.Library)
                {
                    engine = "package";
                    output = ProjectBuilder.PackagePathFor(plan);
                    key = StampKey(plan, "package", modeName, null, null, output);
                    upToDate = !options.Rebuild && IsUpToDate(project, "package", key, output);
                    if (!upToDate)
                    {
                        log($"{project.Name}: packing the library (a library is published as its package)");
                        output = ProjectBuilder.PackLibrary(plan);
                    }
                }
                else if (engine == "native")
                {
                    config = NativeConfig.FindFor(project.FilePath);
                    if (plan.Settings.Linkage != null) config.Linkage = plan.Settings.Linkage;
                    // (publish: one file unless fire.native.json asks for units - the units build faster, the one file is smaller and lets the compiler see the whole
                    // program; docs/spec/NATIVE_SPLIT.md)
                    config.Split ??= "off";
                    config.Quick = options.Quick;   // (a quick publish: -O0 and the units where the toolchain splits - before the "off" above)
                    target = config.ResolveTarget(targetName);
                    var toolchain = config.ResolveToolchain(target, options.Toolchain ?? toolchainName, fire.Native.ToolchainPurpose.Publish);
                    bool files = toolchain.EffectiveKind == "files";
                    output = plan.Settings.Output is { } fixedOutput && !files ? fixedOutput
                        : files ? ProjectOutput.Folder(project.Directory, target.Name, modeName)
                        : ProjectOutput.ProgramPath(project.Directory, project.Name, target.Name, modeName, target.ProgramExtension);
                    key = StampKey(plan, engine, modeName, target, toolchain, output, config, options.Quick);
                    upToDate = !options.Rebuild && IsUpToDate(project, StampName(engine, target.Name, modeName), key, output);
                    if (!upToDate)
                    {
                        log($"{project.Name}: building natively for {target.Name} ({modeName}, {toolchain.EffectiveKind}{(options.Quick is { } q ? ", " + NativeBuilder.QuickBuild.Label(q, toolchain.EffectiveKind) : "")})");
                        Directory.CreateDirectory(files ? output : Path.GetDirectoryName(Path.GetFullPath(output))!);
                        var built = NativeBuilder.BuildSafe(plan.SourceTexts, config, target, toolchain, output, mode, null, false, null, null, plan);
                        if (built.Cancelled) throw new OperationCanceledException(token);
                        diagnostics.AddRange(built.Diagnostics ?? Array.Empty<Diagnostic>());
                        if (!built.Ok)
                        {
                            foreach (var line in built.Log.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').TakeLast(30)) log(line);
                            return Fail(diagnostics.FirstOrDefault(d => d.IsError)?.Message ?? "the native build failed (see the build log)", diagnostics, engine, modeName);
                        }
                        output = built.Output;
                    }
                }
                else if (engine == "vm")
                {
                    output = plan.Settings.Output ?? ProjectOutput.ProgramPath(project.Directory, project.Name, ProjectOutput.VmTarget, modeName, OperatingSystem.IsWindows());
                    key = StampKey(plan, engine, modeName, null, null, output);
                    upToDate = !options.Rebuild && IsUpToDate(project, StampName(engine, ProjectOutput.VmTarget, modeName), key, output);
                    if (!upToDate)
                    {
                        log($"{project.Name}: building a program with the virtual machine ({modeName})");
                        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
                        var warnings = new DiagnosticCollector();
                        using (warnings.Begin())
                        {
                            try { new Linker { Plan = plan, SourcePaths = plan.SourcePaths.Cast<string?>().ToList() }.CompileAndLink(plan.SourceTexts, null, output, mode); }
                            catch (Exception ex) when (CommandLineRunner.IsCompileErrorForEditor(ex))
                            {
                                diagnostics.AddRange(CompileDiagnostics.Of(ex));
                                diagnostics.AddRange(warnings.Items);
                                return Fail(diagnostics.FirstOrDefault(d => d.IsError)?.Message ?? ex.Message, diagnostics, engine, modeName);
                            }
                        }
                        diagnostics.AddRange(warnings.Items);
                    }
                }
                else return Fail($"'{engine}' is no engine (vm, native).");
            }
            catch (OperationCanceledException) { throw; }
            catch (NativeConfigException ex) { return Fail(ex.Message, null, engine, modeName); }
            catch (Exception ex) when (ex is ProjectException or fire.Package.Manager.PackageException or IOException or UnauthorizedAccessException or InvalidOperationException
                                       || CommandLineRunner.IsCompileErrorForEditor(ex))
            {
                return Fail(ex.Message, CommandLineRunner.IsCompileErrorForEditor(ex) ? CompileDiagnostics.Of(ex) : null, engine, modeName);
            }
            token.ThrowIfCancellationRequested();
            if (upToDate) log($"{project.Name}: {ProjectFiles.Relative(project.Directory, output)} is up to date (not built again)");
            else
            {
                WriteStamp(project, plan.Type == OutputType.Library ? "package" : StampName(engine, target?.Name ?? ProjectOutput.VmTarget, modeName), key, output);
                log($"{project.Name}: built {output}");
            }

            // ---- what goes into the target folders ----
            bool isFolder = Directory.Exists(output);
            var contentItems = plan.Type == OutputType.Library ? new List<CopyItem>() : ProjectContent.ItemsOf(plan).ToList();
            // the Copy files also go next to the program in its build folder (as a Build does)
            var copiedNext = ProjectContent.CopyTo(contentItems, isFolder ? output : Path.GetDirectoryName(Path.GetFullPath(output))!, token);
            foreach (var p in copiedNext.Problems) log($"{project.Name}: warning: {p}");
            var items = new Dictionary<string, CopyItem>(ProjectFiles.PathComparer);
            if (isFolder)
            {
                foreach (var file in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories))
                    items[ProjectFiles.Relative(output, file)] = new CopyItem(file, ProjectFiles.Relative(output, file), false);
            }
            else foreach (var f in Companions(output, config?.Linkage)) items[Path.GetFileName(f)] = new CopyItem(f, Path.GetFileName(f), false);
            foreach (var c in contentItems) items[c.RelativePath] = c;

            int copied = 0;
            var problems = new List<string>();
            foreach (var folder in folders)
            {
                token.ThrowIfCancellationRequested();
                if (project.Project.Publish?.Clean == true)
                {
                    if (CleanProblem(workspace, folder) is { } refused) log($"{project.Name}: {folder} is not emptied: {refused}");
                    else if (Directory.Exists(folder))
                    {
                        int removed = EmptyFolder(folder);
                        log($"{project.Name}: emptied {folder} ({removed} entr{(removed == 1 ? "y" : "ies")})");
                    }
                }
                try { Directory.CreateDirectory(folder); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { problems.Add($"{folder}: {ex.Message}"); continue; }
                var r = ProjectContent.CopyTo(items.Values, folder, token);
                // (the mark of a publish folder: a project whose folder holds it does not take the copies there for its own files)
                string quickNote = options.Quick is { } ql && engine == "native" ? $" {NativeBuilder.QuickBuild.Label(ql)}: a quick build for tests, not a release." : "";
                try { File.WriteAllText(Path.Combine(folder, ProjectFiles.PublishMarker), $"Published by fire: {project.Name} ({DateTime.Now:yyyy-MM-dd HH:mm}). The files here are copies; Publish overwrites them.{quickNote}\n"); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                copied += r.Copied.Count;
                problems.AddRange(r.Problems.Select(p => $"{folder}: {p}"));
                log($"{project.Name}: {folder}: {r.Copied.Count} file{(r.Copied.Count == 1 ? "" : "s")} copied{(r.UpToDate > 0 ? $", {r.UpToDate} up to date" : "")}");
            }
            if (problems.Count > 0) return Fail(string.Join(Environment.NewLine, problems), diagnostics, engine, modeName);
            return new ProjectPublishResult
            {
                Project = project, Ok = true, UpToDate = upToDate, Program = output, Engine = engine, Mode = modeName, TargetName = target?.Name ?? (engine == "vm" ? ProjectOutput.VmTarget : null),
                Folders = folders, FilesCopied = copied, Diagnostics = diagnostics,
            };
        }

        /// <summary>The program and the files that belong to it in its folder: those of the same name with another extension (`App.wasm` next to `App.html`, `App.firehost`) - not the
        /// statistics, not the C sources - and with linkage "shared" the shared libraries there.</summary>
        public static IReadOnlyList<string> Companions(string program, string? linkage)
        {
            var files = new List<string> { Path.GetFullPath(program) };
            string folder = Path.GetDirectoryName(Path.GetFullPath(program))!;
            string stem = Path.GetFileNameWithoutExtension(program);
            string name = Path.GetFileName(program);
            bool shared = string.Equals(linkage, "shared", StringComparison.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                string n = Path.GetFileName(file);
                if (ProjectFiles.PathComparer.Equals(n, name)) continue;
                if (n.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".stub", StringComparison.OrdinalIgnoreCase) || n.Contains(".stats.", StringComparison.OrdinalIgnoreCase)) continue;
                string ext = Path.GetExtension(n);
                bool sameName = ProjectFiles.PathComparer.Equals(Path.GetFileNameWithoutExtension(n), stem) && ext.Length > 0;
                bool library = shared && ext.ToLowerInvariant() is ".so" or ".dll" or ".dylib";
                if (sameName || library) files.Add(file);
            }
            return files;
        }

        // ---------------------------------------------------------------- up to date

        private static string StampName(string engine, string target, string mode) => $"{engine}-{target}-{ProjectOutput.ModeTag(mode)}";

        private static string StampPath(LoadedProject project, string name) => Path.Combine(project.Directory, "obj", StampFolder, name + ".json");

        private sealed class Stamp
        {
            public string Key { get; set; } = "";
            public string Output { get; set; } = "";
            public long Length { get; set; }
            public DateTime Time { get; set; }
        }

        /// <summary>Is the program of the last publish build still there, untouched, and was it built from exactly these inputs?</summary>
        private static bool IsUpToDate(LoadedProject project, string name, string? key, string output)
        {
            if (key == null) return false;
            try
            {
                string path = StampPath(project, name);
                if (!File.Exists(path)) return false;
                var stamp = JsonSerializer.Deserialize<Stamp>(File.ReadAllText(path));
                if (stamp == null || stamp.Key != key || !ProjectFiles.PathComparer.Equals(stamp.Output, Path.GetFullPath(output))) return false;
                if (Directory.Exists(output)) return true;   // (the folder of a toolchain that writes files)
                var info = new FileInfo(output);
                return info.Exists && info.Length == stamp.Length && info.LastWriteTimeUtc == stamp.Time;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return false; }
        }

        private static void WriteStamp(LoadedProject project, string name, string? key, string output)
        {
            if (key == null) return;
            try
            {
                string path = StampPath(project, name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var info = new FileInfo(output);
                var stamp = new Stamp { Key = key, Output = Path.GetFullPath(output), Length = info.Exists ? info.Length : 0, Time = info.Exists ? info.LastWriteTimeUtc : default };
                File.WriteAllText(path, JsonSerializer.Serialize(stamp));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        /// <summary>The inputs of a build as one hash: the compiler, the engine, mode, target and toolchain, a quick build (its level), the settings, the text of every source of the project and its libraries, and the
        /// size and time of every other file of theirs (resources, natives; not the Copy files, which are copied anyway), the project files and the native configuration.</summary>
        internal static string StampKey(BuildPlan plan, string engine, string mode, TargetProfile? target, ToolchainDef? toolchain, string output, NativeConfig? config = null, int? quick = null)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            void Add(string? text) { var bytes = Encoding.UTF8.GetBytes(text ?? "\0"); hash.AppendData(BitConverter.GetBytes(bytes.Length)); hash.AppendData(bytes); }
            void AddFile(string path)
            {
                Add(path);
                var info = new FileInfo(path);
                Add(info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : "missing");
            }
            Add("fire publish 1");
            AddFile(typeof(ProjectPublisher).Assembly.Location);
            Add("vm " + CVm.SourceKey());   // (the VM in C a program of the VM is packed with)
            Add(engine); Add(mode); Add(target?.Name); Add(Path.GetFullPath(output));
            if (toolchain != null) { Add(toolchain.EffectiveKind); Add(toolchain.Compiler); Add(toolchain.Optimization); Add(string.Join(" ", toolchain.Args ?? new List<string>())); }
            if (config != null) { Add(config.Linkage); if (config.Path != null) AddFile(config.Path); }
            if (quick is { } level) Add($"quick {level}");   // (a quick publish is not up to date for a normal one, nor the other way round)
            Add(JsonSerializer.Serialize(plan.Settings));
            foreach (var r in plan.Packages) Add($"{r.Package}@{r.Version}");
            void AddProject(LoadedProject p, IEnumerable<SourceFile> sources)
            {
                AddFile(p.FilePath);
                foreach (var s in sources) { Add(s.Path); Add(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s.Text)))); }
                var copies = new HashSet<string>(p.CopyFiles.Select(c => c.Source), ProjectFiles.PathComparer);
                foreach (var f in p.BuildContentFiles) if (!copies.Contains(f)) AddFile(f);   // (not the files of undecided folders: docs/tools/projects.md, "Folders")
            }
            AddProject(plan.Project, plan.Sources);
            foreach (var lib in plan.Libraries.Values.OrderBy(l => l.ImportName, StringComparer.Ordinal)) { Add(lib.ImportName); AddProject(lib.Project, lib.Sources); }
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }

        // ---------------------------------------------------------------- clean target

        /// <summary>Why `folder` must not be emptied (null: it may): a root, the home folder, a folder that holds (or is) a project, a solution, the folder of a project of the
        /// workspace or a repository.</summary>
        public static string? CleanProblem(Workspace workspace, string folder)
        {
            string full = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (full.Length == 0 || Path.GetPathRoot(full) is { } root && ProjectFiles.PathComparer.Equals(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), full)) return "it is the root of a drive";
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd(Path.DirectorySeparatorChar);
            if (home.Length > 0 && ProjectFiles.PathComparer.Equals(home, full)) return "it is the home folder";
            string prefix = full + Path.DirectorySeparatorChar;
            bool InsideOrSame(string? dir) => dir != null && (ProjectFiles.PathComparer.Equals(Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar), full) || Path.GetFullPath(dir).StartsWith(prefix, ProjectFiles.PathComparison));
            if (InsideOrSame(workspace.Solution?.Directory)) return "the solution is in it";
            if (workspace.AllProjects.FirstOrDefault(p => InsideOrSame(p.Directory)) is { } inside) return $"the project {inside.Name} is in it";
            if (!Directory.Exists(full)) return null;
            try
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint, MatchCasing = MatchCasing.CaseInsensitive };
                if (Directory.EnumerateDirectories(full, ".git", options).Any() || Directory.EnumerateFiles(full, ".git", options).Any()) return "it holds a git repository";
                if (Directory.EnumerateFiles(full, "*" + FireProject.Extension, options).FirstOrDefault() is { } p) return $"it holds the project file {ProjectFiles.Relative(full, p)}";
                if (Directory.EnumerateFiles(full, "*" + FireSolution.Extension, options).FirstOrDefault() is { } s) return $"it holds the solution file {ProjectFiles.Relative(full, s)}";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ex.Message; }
            return null;
        }

        /// <summary>Deletes everything in `folder` (a link is removed, not what it points to); the number of entries removed.</summary>
        private static int EmptyFolder(string folder)
        {
            int n = 0;
            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
            {
                try
                {
                    if (entry is DirectoryInfo d && d.LinkTarget == null) d.Delete(true);
                    else entry.Delete();
                    n++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            return n;
        }
    }
}
