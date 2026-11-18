using System.Text;
using System.Text.RegularExpressions;

namespace fire.Projects
{
    /// <summary>What a project does with a folder below it (docs/tools/projects.md, "Folders: included, undecided, excluded").</summary>
    public enum FolderState
    {
        /// <summary>Part of the project: named in `includedDirectories` (or below such a folder), or it holds code; the project folder itself.</summary>
        Included,
        /// <summary>Neither named nor holding code (`assets/`, `docs/`): shown pale and italic, its files are no part of a build (a file's own build action still counts).</summary>
        Undecided,
        /// <summary>Named in `excludedDirectories` (or below such a folder), ignored by a `.gitignore` (with "Sync .gitignore") or a folder a project always leaves out (`bin`, `obj`,
        /// `node_modules`, names with a dot): not read, not built, not watched. Wins over everything.</summary>
        Excluded,
    }

    /// <summary>Why a folder has its <see cref="FolderState"/>.</summary>
    public enum FolderReason
    {
        /// <summary>The project folder.</summary>
        Root,
        /// <summary>Named in `includedDirectories` / `excludedDirectories` (the entry itself or a pattern; see <see cref="FolderInfo.Entry"/>).</summary>
        Listed,
        /// <summary>Below a folder that is named.</summary>
        InsideListed,
        /// <summary>It holds code (fire sources, markup; C/C++ of a project with natives) or an included folder, also further down.</summary>
        Code,
        /// <summary>The folder of the notes (docs/tools/notes.md): always part of the project.</summary>
        Notes,
        /// <summary>Undecided: nothing names it, no code.</summary>
        None,
        /// <summary>Excluded: ignored by a `.gitignore` of the repository (only with "Sync .gitignore").</summary>
        Gitignore,
        /// <summary>Excluded: a folder no project reads (`bin`, `obj`, `.git`, `.vs`, `node_modules`, a name with a dot).</summary>
        Hidden,
    }

    /// <summary>The state of one folder (relative to the project, with `/`); `Entry` is the entry of the project file (or the line of the `.gitignore`) that decided it, if any.</summary>
    public sealed record FolderInfo(string Path, FolderState State, FolderReason Reason, string? Entry = null)
    {
        /// <summary>What the explorer writes behind the row: "excluded", "ignored by .gitignore", "undecided" - empty for an included folder.</summary>
        public string Label => State switch
        {
            FolderState.Excluded => Reason == FolderReason.Gitignore ? "ignored by .gitignore" : "excluded",
            FolderState.Undecided => "undecided",
            _ => "",
        };

        /// <summary>The folder's own entry of `excludedDirectories` (not a pattern, not a parent): "Remove Exclusion" can take it out.</summary>
        public bool IsOwnExclusion => State == FolderState.Excluded && Reason == FolderReason.Listed && Entry != null && !ProjectFiles.IsPattern(Entry);
    }

    /// <summary>
    /// The folders of a project and what it does with each (<see cref="FolderState"/>), read in one walk over the project folder: <see cref="Files"/> are the files of the folders that are
    /// not excluded - what <see cref="ProjectFiles.Resolve"/> compiles from and what is content. Excluded folders are not entered (a `node_modules` with 100 000 files costs nothing).
    /// </summary>
    public sealed class ProjectFolderMap
    {
        private static readonly string[] SkippedFolders = { "bin", "obj", ".git", ".vs", "node_modules" };
        private static readonly string[] NativeExtensions = { ".h", ".hpp", ".hh", ".c", ".cc", ".cpp", ".cxx" };

        private readonly Dictionary<string, FolderInfo> _folders = new(ProjectFiles.PathComparer);
        private readonly List<string> _order = new();
        private readonly List<string> _files = new();

        public string Directory { get; }

        /// <summary>Every folder that was seen (not those inside an excluded one, not the always hidden ones, not the folders of other projects), in the order of the walk (by name).</summary>
        public IEnumerable<FolderInfo> Folders => _order.Select(f => _folders[f]);

        /// <summary>The files below the project folder that are not in an excluded folder (relative, with `/`), in the order of the walk.</summary>
        public IReadOnlyList<string> Files => _files;

        /// <summary>The excluded folders the walk met (their insides are not listed).</summary>
        public IEnumerable<FolderInfo> ExcludedFolders => Folders.Where(f => f.State == FolderState.Excluded);

        private ProjectFolderMap(string directory) { Directory = directory; }

        /// <summary>An empty map (a project without folder).</summary>
        public static ProjectFolderMap Empty(string directory) => new(directory);

        /// <summary>The state of a folder (relative to the project, `/`): one that the walk met, else from its parents - inside an excluded folder excluded, inside an included one
        /// undecided (nothing there holds code: else the walk would have met it), the hidden ones by name.</summary>
        public FolderInfo StateOf(string relFolder)
        {
            string rel = Normalize(relFolder);
            if (rel.Length == 0) return new FolderInfo("", FolderState.Included, FolderReason.Root);
            if (rel == ".." || rel.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(rel)) return new FolderInfo(rel, FolderState.Included, FolderReason.Listed);   // (outside: a file the project names)
            if (_folders.TryGetValue(rel, out var known)) return known;
            var parts = rel.Split('/');
            string path = "";
            for (int i = 0; i < parts.Length; i++)
            {
                path = i == 0 ? parts[0] : path + "/" + parts[i];
                if (IsHiddenName(parts[i])) return new FolderInfo(rel, FolderState.Excluded, path == rel ? FolderReason.Hidden : FolderReason.InsideListed, path);
                if (_folders.TryGetValue(path, out var f) && f.State == FolderState.Excluded) return path == rel ? f : new FolderInfo(rel, FolderState.Excluded, FolderReason.InsideListed, f.Entry ?? path);
            }
            return new FolderInfo(rel, FolderState.Undecided, FolderReason.None);
        }

        /// <summary>The state of the folder a file is in (`relFile` relative to the project).</summary>
        public FolderState StateOfFile(string relFile)
        {
            string rel = Normalize(relFile);
            int slash = rel.LastIndexOf('/');
            return slash < 0 ? FolderState.Included : StateOf(rel.Substring(0, slash)).State;
        }

        public bool IsExcludedFile(string relFile) => StateOfFile(relFile) == FolderState.Excluded;
        public bool IsUndecidedFile(string relFile) => StateOfFile(relFile) == FolderState.Undecided;

        internal static bool IsHiddenName(string name) => SkippedFolders.Contains(name, StringComparer.OrdinalIgnoreCase) || name.StartsWith('.');

        /// <summary>A path or pattern of `includedDirectories`/`excludedDirectories` as it is compared: `/`, no `./` or `/` at the start, no `/` at the end.</summary>
        public static string Normalize(string entry)
        {
            string p = entry.Trim().Replace('\\', '/');
            while (p.StartsWith("./", StringComparison.Ordinal)) p = p.Substring(2);
            p = p.Trim('/');
            return p == "." ? "" : p;
        }

        /// <summary>The entry of `entries` that names the folder `rel` (itself or as a pattern); null: none.</summary>
        public static string? Matching(IEnumerable<string>? entries, string rel)
        {
            if (entries == null) return null;
            foreach (var e in entries)
            {
                string n = Normalize(e);
                if (n.Length == 0) continue;
                if (ProjectFiles.IsPattern(n) ? ProjectFiles.CachedPattern(n).IsMatch(rel) : ProjectFiles.PathComparer.Equals(n, rel)) return e;
            }
            return null;
        }

        private static bool IsCode(FireProject project, string name) =>
            FireProject.IsSourceFile(name) || project.Native != null && NativeExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);

        /// <summary>Reads the folders of the project (see the class). A project that is not saved yet has none.</summary>
        public static ProjectFolderMap Scan(FireProject project)
        {
            string? dir = project.Directory;
            if (dir == null) return new ProjectFolderMap("");
            var map = new ProjectFolderMap(dir);
            var gitignore = project.SyncsGitignore ? GitIgnore.ForProject(dir) : null;
            string notes = ProjectFiles.Relative(dir, ProjectNotes.Folder(project));

            // true: the folder holds code (or an included folder), so its parents are included too
            bool Visit(string full, string rel, GitIgnore? rules, bool insideIncluded)
            {
                bool code = false;
                IEnumerable<string> files;
                try { files = System.IO.Directory.EnumerateFiles(full).ToList(); } catch (Exception) { return false; }
                foreach (var f in files)
                {
                    string name = Path.GetFileName(f);
                    map._files.Add(rel.Length == 0 ? name : rel + "/" + name);
                    code |= IsCode(project, name);
                }
                // a .gitignore in this folder adds its rules for what is below
                if (rules != null && rel.Length > 0) rules = rules.WithFolder(full);

                IEnumerable<string> subs;
                try { subs = System.IO.Directory.EnumerateDirectories(full).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList(); } catch (Exception) { return code; }
                foreach (var sub in subs)
                {
                    string name = Path.GetFileName(sub);
                    if (IsHiddenName(name)) continue;   // (Show All Files lists them: ProjectFiles.HiddenEntries)
                    bool nestedProject;
                    try { nestedProject = System.IO.Directory.EnumerateFiles(sub, "*" + FireProject.Extension).Any(); } catch (Exception) { nestedProject = false; }
                    if (nestedProject || ProjectFiles.IsPublishFolder(sub)) continue;   // the folder of another project, a folder Publish copies into: not this project's
                    string subRel = rel.Length == 0 ? name : rel + "/" + name;
                    map._order.Add(subRel);
                    if (Matching(project.ExcludedDirectories, subRel) is { } excluded)
                    {
                        map._folders[subRel] = new FolderInfo(subRel, FolderState.Excluded, FolderReason.Listed, excluded);
                        continue;
                    }
                    if (rules?.Match(sub, isDirectory: true) is { } line)
                    {
                        map._folders[subRel] = new FolderInfo(subRel, FolderState.Excluded, FolderReason.Gitignore, line);
                        continue;
                    }
                    string? listed = Matching(project.IncludedDirectories, subRel);
                    map._folders[subRel] = null!;   // (the place in the order; set below, after its contents)
                    bool below = Visit(sub, subRel, rules, insideIncluded || listed != null);
                    FolderInfo info;
                    if (listed != null) info = new FolderInfo(subRel, FolderState.Included, FolderReason.Listed, listed);
                    else if (insideIncluded) info = new FolderInfo(subRel, FolderState.Included, FolderReason.InsideListed);
                    else if (below) info = new FolderInfo(subRel, FolderState.Included, FolderReason.Code);
                    else if (ProjectFiles.PathComparer.Equals(subRel, notes)) info = new FolderInfo(subRel, FolderState.Included, FolderReason.Notes);
                    else info = new FolderInfo(subRel, FolderState.Undecided, FolderReason.None);
                    map._folders[subRel] = info;
                    code |= below || listed != null;
                }
                return code;
            }
            Visit(dir, "", gitignore, false);
            return map;
        }
    }

    /// <summary>
    /// The rules of `.gitignore` files, as far as a project needs them: whether a folder is ignored. The files of the repository from its top down to the project folder, then those of the
    /// folders below as the walk enters them; the last rule that matches decides (`!` takes a path back in), `/` at the end only for folders, a `/` at the start or in the middle ties the
    /// pattern to the folder of its `.gitignore`, `*`, `?`, `**` and `[...]` as in git. Not read: `.git/info/exclude` and the global excludes of git.
    /// </summary>
    public sealed class GitIgnore
    {
        private sealed record Rule(string BaseDir, Regex Regex, bool Negate, bool DirectoryOnly, string Line);
        private readonly List<Rule> _rules;

        private GitIgnore(List<Rule> rules) { _rules = rules; }

        /// <summary>The top of the git repository `path` is in (the folder that holds `.git`); null: none.</summary>
        public static string? RepositoryRoot(string path)
        {
            for (string? d = Path.GetFullPath(path); d != null; d = Path.GetDirectoryName(d))
            {
                try { if (System.IO.Directory.Exists(Path.Combine(d, ".git")) || File.Exists(Path.Combine(d, ".git"))) return d; } catch (Exception) { return null; }
            }
            return null;
        }

        /// <summary>The folders whose `.gitignore` counts for the project folder: from the top of the repository (without one: the project folder alone) down to the project folder.</summary>
        public static List<string> FoldersAbove(string projectDir)
        {
            string dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectDir));
            string? root = RepositoryRoot(dir);
            var chain = new List<string>();
            for (string? d = dir; d != null; d = Path.GetDirectoryName(d))
            {
                chain.Add(d);
                if (root == null || ProjectFiles.PathComparer.Equals(d, root)) break;
            }
            chain.Reverse();
            return chain;
        }

        /// <summary>The rules that apply in the project folder.</summary>
        public static GitIgnore ForProject(string projectDir)
        {
            var g = new GitIgnore(new List<Rule>());
            foreach (var d in FoldersAbove(projectDir)) g = g.WithFolder(d);
            return g;
        }

        /// <summary>From the text of a `.gitignore` in `baseDir` (for tests and for <see cref="WithFolder"/>).</summary>
        public static GitIgnore Parse(string baseDir, string text) => new GitIgnore(new List<Rule>()).With(baseDir, text);

        /// <summary>These rules and those of the `.gitignore` in `folder` (the same object when it has none).</summary>
        public GitIgnore WithFolder(string folder)
        {
            string file = Path.Combine(folder, ".gitignore");
            string text;
            try { if (!File.Exists(file)) return this; text = File.ReadAllText(file); } catch (Exception) { return this; }
            return With(folder, text);
        }

        private GitIgnore With(string baseDir, string text)
        {
            var rules = new List<Rule>(_rules);
            foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.TrimEnd();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                string p = line;
                bool negate = p.StartsWith('!');
                if (negate) p = p.Substring(1);
                if (p.StartsWith("\\#") || p.StartsWith("\\!")) p = p.Substring(1);
                bool dirOnly = p.EndsWith('/');
                p = p.TrimEnd('/');
                if (p.Length == 0) continue;
                bool anchored = p.Contains('/');
                p = p.TrimStart('/');
                if (p.Length == 0) continue;
                rules.Add(new Rule(Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDir)), ToRegex(p, anchored), negate, dirOnly, line));
            }
            return new GitIgnore(rules);
        }

        private static Regex ToRegex(string pattern, bool anchored)
        {
            var sb = new StringBuilder("^");
            if (!anchored) sb.Append("(?:.*/)?");
            for (int i = 0; i < pattern.Length; i++)
            {
                char c = pattern[i];
                if (c == '*')
                {
                    if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                    {
                        i++;
                        if (i + 1 < pattern.Length && pattern[i + 1] == '/') { i++; sb.Append("(?:.*/)?"); }
                        else sb.Append(".*");
                    }
                    else sb.Append("[^/]*");
                }
                else if (c == '?') sb.Append("[^/]");
                else if (c == '[')
                {
                    int end = pattern.IndexOf(']', i + 2);
                    if (end < 0) { sb.Append("\\["); continue; }
                    string set = pattern.Substring(i + 1, end - i - 1);
                    if (set.StartsWith('!')) set = "^" + set.Substring(1);
                    sb.Append('[').Append(set.Replace("\\", "\\\\")).Append(']');
                    i = end;
                }
                else if (c == '\\' && i + 1 < pattern.Length) { sb.Append(Regex.Escape(pattern[++i].ToString())); }
                else sb.Append(Regex.Escape(c.ToString()));
            }
            sb.Append('$');
            return new Regex(sb.ToString(), OperatingSystem.IsLinux() ? RegexOptions.CultureInvariant : RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        }

        /// <summary>The line that ignores `fullPath` (null: it is not ignored; a `!` line that takes it back in also gives null).</summary>
        public string? Match(string fullPath, bool isDirectory)
        {
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fullPath));
            string? decided = null;
            foreach (var r in _rules)
            {
                if (r.DirectoryOnly && !isDirectory) continue;
                if (!full.StartsWith(r.BaseDir + Path.DirectorySeparatorChar, ProjectFiles.PathComparison)) continue;
                string rel = full.Substring(r.BaseDir.Length + 1).Replace('\\', '/');
                if (r.Regex.IsMatch(rel)) decided = r.Negate ? null : r.Line;
            }
            return decided;
        }

        public bool IsIgnored(string fullPath, bool isDirectory) => Match(fullPath, isDirectory) != null;
    }

    /// <summary>
    /// "Sync .gitignore" (docs/tools/projects.md, "Folders"): excluding a folder in spark also puts it into a `.gitignore`, taking the exclusion back takes the line out again - only a line spark
    /// put there itself, which it marks with the comment line <see cref="Marker"/> right above it. The file is the `.gitignore` nearest to the project: the project folder's, else the first one
    /// on the way up to the top of the repository; there is none: a new one at the top of the repository (without a repository: in the project folder). The line is the folder relative to that
    /// file, `/` at the start and at the end (`/assets/raw/`); a line that says the same is there already: nothing is added.
    /// </summary>
    public static class GitignoreSync
    {
        /// <summary>The comment above every line spark adds; the project file's name follows (`# spark: excluded from App.fireproj`).</summary>
        public const string Marker = "# spark: excluded from ";

        public static string MarkerFor(FireProject project) => Marker + Path.GetFileName(project.FilePath ?? project.Name + FireProject.Extension);

        /// <summary>The `.gitignore` excluding writes into (see the class); it need not exist yet.</summary>
        public static string FileFor(string projectDir)
        {
            var chain = GitIgnore.FoldersAbove(projectDir);
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                string f = Path.Combine(chain[i], ".gitignore");
                if (File.Exists(f)) return f;
            }
            return Path.Combine(GitIgnore.RepositoryRoot(projectDir) ?? Path.GetFullPath(projectDir), ".gitignore");
        }

        /// <summary>The line for `folder` in the `.gitignore` of the folder `gitignoreDir`.</summary>
        public static string LineFor(string gitignoreDir, string folder) => "/" + ProjectFiles.Relative(gitignoreDir, Path.GetFullPath(folder)).Trim('/') + "/";

        private static List<string> ReadLines(string file) => File.Exists(file) ? File.ReadAllText(file).Replace("\r\n", "\n").TrimEnd('\n').Split('\n').ToList() : new List<string>();

        private static void WriteLines(string file, List<string> lines)
        {
            if (lines.Count == 0 || lines.All(l => l.Length == 0)) { if (File.Exists(file)) File.WriteAllText(file, ""); return; }
            File.WriteAllText(file, string.Join("\n", lines) + "\n");
        }

        private static bool SameLine(string a, string b)
        {
            string Norm(string s) => s.Trim().TrimStart('/').TrimEnd('/');
            return a.Trim().Length > 0 && !a.TrimStart().StartsWith('#') && Norm(a) == Norm(b);
        }

        /// <summary>Puts the folder (full path) into the `.gitignore` (see the class); returns the file and the line, `Added` false when a line for it was there already.</summary>
        public static (string File, string Line, bool Added) Add(FireProject project, string folder)
        {
            string dir = project.Directory ?? throw new ProjectException("The project has no folder.");
            string file = FileFor(dir);
            string line = LineFor(Path.GetDirectoryName(file)!, folder);
            var lines = ReadLines(file);
            if (lines.Any(l => SameLine(l, line))) return (file, line, false);
            lines.Add(MarkerFor(project));
            lines.Add(line);
            WriteLines(file, lines);
            return (file, line, true);
        }

        /// <summary>Takes the line for the folder out of the `.gitignore` files from the project folder up to the top of the repository - only a line with spark's mark above it (and the
        /// mark). Returns the file it was taken out of; null: there was none (`foreign` names a line that says the same but was not written by spark: it stays).</summary>
        public static (string? File, string? Foreign) Remove(FireProject project, string folder)
        {
            string dir = project.Directory ?? throw new ProjectException("The project has no folder.");
            string? foreign = null;
            var chain = GitIgnore.FoldersAbove(dir);
            for (int c = chain.Count - 1; c >= 0; c--)
            {
                string file = Path.Combine(chain[c], ".gitignore");
                if (!File.Exists(file)) continue;
                string line = LineFor(chain[c], folder);
                var lines = ReadLines(file);
                bool removed = false;
                for (int i = lines.Count - 1; i >= 0; i--)
                {
                    if (!SameLine(lines[i], line)) continue;
                    if (i > 0 && lines[i - 1].Trim().StartsWith(Marker, StringComparison.Ordinal)) { lines.RemoveAt(i); lines.RemoveAt(i - 1); i--; removed = true; }
                    else foreign ??= lines[i].Trim();
                }
                if (removed) { WriteLines(file, lines); return (file, null); }
            }
            return (null, foreign);
        }
    }

    /// <summary>Changes of `includedDirectories` and `excludedDirectories` (the context menu of a folder in spark's explorer); the caller saves the project.</summary>
    public static class ProjectFolderEdits
    {
        private static bool Same(string a, string b) => ProjectFiles.PathComparer.Equals(ProjectFolderMap.Normalize(a), ProjectFolderMap.Normalize(b));

        /// <summary>Excludes the folder (relative): its own entry in `excludedDirectories` (an entry for it in `includedDirectories` goes). false: it was excluded already by its own entry.</summary>
        public static bool Exclude(FireProject project, string relFolder)
        {
            string rel = ProjectFolderMap.Normalize(relFolder);
            project.IncludedDirectories?.RemoveAll(e => Same(e, rel));
            if (project.IncludedDirectories is { Count: 0 }) project.IncludedDirectories = null;
            project.ExcludedDirectories ??= new List<string>();
            if (project.ExcludedDirectories.Any(e => Same(e, rel))) return false;
            project.ExcludedDirectories.Add(rel);
            return true;
        }

        /// <summary>Takes the folder's own entry out of `excludedDirectories`; false: it had none.</summary>
        public static bool RemoveExclusion(FireProject project, string relFolder)
        {
            string rel = ProjectFolderMap.Normalize(relFolder);
            int removed = project.ExcludedDirectories?.RemoveAll(e => Same(e, rel)) ?? 0;
            if (project.ExcludedDirectories is { Count: 0 }) project.ExcludedDirectories = null;
            return removed > 0;
        }

        /// <summary>Includes the folder: its own exclusion goes and it gets an entry in `includedDirectories`. false: it was listed already.</summary>
        public static bool Include(FireProject project, string relFolder)
        {
            string rel = ProjectFolderMap.Normalize(relFolder);
            RemoveExclusion(project, rel);
            project.IncludedDirectories ??= new List<string>();
            if (project.IncludedDirectories.Any(e => Same(e, rel))) return false;
            project.IncludedDirectories.Add(rel);
            return true;
        }
    }
}
