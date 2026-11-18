using System.Text.Json.Serialization;

namespace fire.Projects
{
    /// <summary>
    /// What a build and a publish do with a file of the project (docs/tools/projects.md, "Build actions"): `buildActions` in the project file maps a path or a pattern to one of them.
    /// A file that no entry names keeps what it always had: a fire source is compiled, everything else is content (a resource that `new Resource("...")` embeds, C++ of the natives, notes).
    /// </summary>
    public enum FileBuildAction
    {
        /// <summary>The default: a source is compiled, content stays content (a `new Resource("path")` still embeds it).</summary>
        Build,
        /// <summary>Left out: a source is not compiled; nothing is copied. (A resource that the code names is still embedded - the code asks for it.)</summary>
        Ignore,
        /// <summary>Copied next to the program (keeping its path relative to the project) by Build and by Publish when it is newer than the copy there; a source is then not compiled.</summary>
        Copy,
        /// <summary>Like <see cref="Copy"/>, but copied on every build and publish.</summary>
        CopyAlways,
    }

    /// <summary>The `buildActions` of a project: reading, resolving and changing them.</summary>
    public static class FileBuildActions
    {
        /// <summary>The names in the project file, by action (index = the value of the enum).</summary>
        public static readonly IReadOnlyList<string> Names = new[] { "build", "ignore", "copy", "copyAlways" };

        public static string Name(FileBuildAction action) => Names[(int)action];

        /// <summary>The action of a name of the file (any case; `copy-always` and `copyIfNewer` are read too); null: no action.</summary>
        public static FileBuildAction? Parse(string? text) => (text ?? "").Trim().Replace("-", "").Replace("_", "").ToLowerInvariant() switch
        {
            "build" or "compile" or "default" => FileBuildAction.Build,
            "ignore" or "none" or "exclude" => FileBuildAction.Ignore,
            "copy" or "copyifnewer" => FileBuildAction.Copy,
            "copyalways" => FileBuildAction.CopyAlways,
            _ => null,
        };

        public static bool IsCopy(FileBuildAction action) => action is FileBuildAction.Copy or FileBuildAction.CopyAlways;

        /// <summary>Is the source left out of the compile (ignored or copied)?</summary>
        public static bool DropsSource(FileBuildAction action) => action != FileBuildAction.Build;

        /// <summary>The entry of `buildActions` that decides for the file `rel` (relative to the project, `/`): the file itself, else the last pattern that matches it; null: none.</summary>
        public static string? EntryOf(FireProject project, string rel)
        {
            if (project.BuildActions is not { Count: > 0 } map) return null;
            rel = Normalize(rel);
            foreach (var key in map.Keys)
                if (!ProjectFiles.IsPattern(key) && ProjectFiles.PathComparer.Equals(Normalize(key), rel)) return key;
            string? found = null;
            foreach (var key in map.Keys)
                if (ProjectFiles.IsPattern(key) && ProjectFiles.CachedPattern(key).IsMatch(rel)) found = key;
            return found;
        }

        /// <summary>The action of the file `rel` (relative to the project, `/`), and whether an entry of the project file says so (false: the default, Build).</summary>
        public static FileBuildAction Of(FireProject project, string rel, out bool explicitly)
        {
            explicitly = false;
            if (EntryOf(project, rel) is not { } key) return FileBuildAction.Build;
            if (Parse(project.BuildActions![key]) is not { } action) return FileBuildAction.Build;   // (a name that is not known: Validate reports it)
            explicitly = true;
            return action;
        }

        public static FileBuildAction Of(FireProject project, string rel) => Of(project, rel, out _);

        /// <summary>Sets the action of one file (relative to the project). Build for a file that no pattern names takes its entry out (the default); otherwise the file gets an entry
        /// of its own (it goes before what a pattern says).</summary>
        public static void Set(FireProject project, string rel, FileBuildAction action)
        {
            rel = Normalize(rel);
            var map = project.BuildActions ?? new Dictionary<string, string>();
            foreach (var key in map.Keys.Where(k => !ProjectFiles.IsPattern(k) && ProjectFiles.PathComparer.Equals(Normalize(k), rel)).ToList()) map.Remove(key);
            var byPattern = EntryOf(new FireProject { BuildActions = map }, rel) is { } pattern ? Parse(map[pattern]) ?? FileBuildAction.Build : FileBuildAction.Build;
            if (action != byPattern) map[rel] = Name(action);
            project.BuildActions = map.Count > 0 ? map : null;
        }

        private static string Normalize(string rel)
        {
            string p = rel.Replace('\\', '/');
            while (p.StartsWith("./", StringComparison.Ordinal)) p = p.Substring(2);
            return p;
        }

        /// <summary>The problems of the entries (an action that is not known).</summary>
        public static IEnumerable<string> Validate(FireProject project)
        {
            foreach (var (key, value) in project.BuildActions ?? new Dictionary<string, string>())
                if (Parse(value) == null) yield return $"buildActions: '{value}' ({key}) is not one of: {string.Join(", ", Names)}.";
        }
    }

    /// <summary>
    /// How a project is published (docs/tools/projects.md, "Publish"): `publish` in the project file. Publish (spark: *Run > Publish*, the Build tool strip, the context menus) builds every
    /// project that has it on - again only when something changed - and copies the program, what it needs next to it and the files with the build action Copy into the target folders.
    /// </summary>
    public sealed class PublishSettings
    {
        /// <summary>Publish this project (Publish Solution takes only the projects that have it on).</summary>
        public bool Enabled { get; set; }
        /// <summary>The folders the program goes into: absolute, or relative to the solution file (without a solution: to the project file). None: `publish/&lt;project&gt;` next to the solution.</summary>
        public List<string>? Targets { get; set; }
        /// <summary>`vm` or `native`; null: the engine of the project's settings, else native.</summary>
        public string? Engine { get; set; }
        /// <summary>`debug`, `release` or `performance`; null: release.</summary>
        public string? Mode { get; set; }
        /// <summary>The target of a native build; null: the project's target, else the one of fire.native.json (this machine).</summary>
        public string? Target { get; set; }
        /// <summary>The toolchain of a native build; null: the project's.</summary>
        public string? Toolchain { get; set; }
        /// <summary>Empty each target folder before the files are copied (off by default: files of others in the folder stay). A folder that holds a project, a solution or a repository is never emptied.</summary>
        public bool? Clean { get; set; }

        public const string DefaultFolder = "publish";

        [JsonIgnore]
        public bool IsEmpty => !Enabled && (Targets == null || Targets.Count == 0) && Engine == null && Mode == null && Target == null && Toolchain == null && Clean == null;

        public PublishSettings Clone() => new()
        {
            Enabled = Enabled, Targets = Targets?.ToList(), Engine = Engine, Mode = Mode, Target = Target, Toolchain = Toolchain, Clean = Clean,
        };

        public IEnumerable<string> Validate()
        {
            if (Engine != null && !ProjectSettings.Engines.Contains(Engine)) yield return $"publish.engine '{Engine}' is not one of: {string.Join(", ", ProjectSettings.Engines)}.";
            if (Mode != null && !ProjectSettings.Modes.Contains(Mode)) yield return $"publish.mode '{Mode}' is not one of: {string.Join(", ", ProjectSettings.Modes)}.";
            foreach (var t in Targets ?? new List<string>()) if (string.IsNullOrWhiteSpace(t)) yield return "publish.targets: an entry is empty.";
        }

        /// <summary>The target folders of `project` as full paths: relative ones from the folder of the solution (`solutionDirectory`; null: the project's folder).</summary>
        public static IReadOnlyList<string> TargetFolders(LoadedProject project, string? solutionDirectory)
        {
            string root = solutionDirectory ?? project.Directory;
            var publish = project.Project.Publish;
            var targets = publish?.Targets?.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            if (targets == null || targets.Count == 0) return new[] { DefaultTarget(project, solutionDirectory) };
            return targets.Select(t => Path.GetFullPath(Environment.ExpandEnvironmentVariables(t.Trim()), root)).Distinct(ProjectFiles.PathComparer).ToList();
        }

        /// <summary>The folder of a project without `targets`: `publish/&lt;name&gt;` next to the solution file; without a solution next to the project's folder (not inside it).</summary>
        public static string DefaultTarget(LoadedProject project, string? solutionDirectory)
        {
            string root = solutionDirectory ?? Path.GetDirectoryName(project.Directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? project.Directory;
            return Path.GetFullPath(Path.Combine(root, DefaultFolder, SafeName(project.Name)));
        }

        /// <summary>The engine, the mode and the target of a publish of `project` (settings: those that apply to it - the project's over the solution's).</summary>
        public static (string Engine, string Mode, string? Target, string? Toolchain) Choices(PublishSettings? publish, ProjectSettings settings) =>
            (publish?.Engine ?? settings.Engine ?? "native", publish?.Mode ?? "release", publish?.Target ?? settings.Target, publish?.Toolchain ?? settings.Toolchain);

        private static string SafeName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }
    }

    /// <summary>A file a build copies next to the program: where it is, where it goes (relative to the program's folder) and whether it is copied every time.</summary>
    public sealed record CopyItem(string Source, string RelativePath, bool Always);

    /// <summary>What <see cref="ProjectContent.CopyTo"/> did: the files it copied and those that were up to date.</summary>
    public sealed record CopyResult(IReadOnlyList<string> Copied, int UpToDate, IReadOnlyList<string> Problems);

    /// <summary>The files with the build action Copy, copied next to a program (Build, Build Solution, `forge build`) and into a publish folder.</summary>
    public static class ProjectContent
    {
        /// <summary>The Copy files of a build: those of the project and of the libraries it uses (a library's data travels with the programs that use it; the project's own go last and win).</summary>
        public static IReadOnlyList<CopyItem> ItemsOf(BuildPlan plan)
        {
            var items = new Dictionary<string, CopyItem>(ProjectFiles.PathComparer);
            foreach (var lib in plan.Libraries.Values) foreach (var c in lib.Project.CopyFiles) items[c.RelativePath] = c;
            foreach (var c in plan.Project.CopyFiles) items[c.RelativePath] = c;
            return items.Values.OrderBy(c => c.RelativePath, StringComparer.Ordinal).ToList();
        }

        /// <summary>Copies `items` into `folder` (their relative paths kept): a Copy file when the copy there is missing, older or of another size, a CopyAlways file every time.
        /// The copy gets the time of the original (so that "newer" means changed). The token stops between files.</summary>
        public static CopyResult CopyTo(IEnumerable<CopyItem> items, string folder, CancellationToken token = default)
        {
            var copied = new List<string>();
            var problems = new List<string>();
            int upToDate = 0;
            foreach (var item in items)
            {
                token.ThrowIfCancellationRequested();
                string destination = Path.GetFullPath(item.RelativePath, folder);
                try
                {
                    if (!item.Always && IsSame(item.Source, destination)) { upToDate++; continue; }
                    CopyFile(item.Source, destination);
                    copied.Add(destination);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { problems.Add($"{item.RelativePath}: {ex.Message}"); }
            }
            return new CopyResult(copied, upToDate, problems);
        }

        /// <summary>Is `destination` a copy of `source` that is up to date: there, the same size, not older?</summary>
        public static bool IsSame(string source, string destination)
        {
            var from = new FileInfo(source);
            var to = new FileInfo(destination);
            return to.Exists && to.Length == from.Length && to.LastWriteTimeUtc >= from.LastWriteTimeUtc;
        }

        /// <summary>Copies a file (the folder is made), with its time and - on Unix - its permissions (a program stays executable). Written next to the place first, then moved there.</summary>
        public static void CopyFile(string source, string destination)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            string temp = destination + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            try
            {
                File.Copy(source, temp, true);
                File.SetLastWriteTimeUtc(temp, File.GetLastWriteTimeUtc(source));
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, File.GetUnixFileMode(source));
                File.Move(temp, destination, true);
            }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } }
        }
    }
}
