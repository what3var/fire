using System.Text;
using System.Text.RegularExpressions;

namespace fire.Projects
{
    /// <summary>Which files a project consists of, in which order (see FireProject): the patterns of `files` and `exclude`, the entry file last.</summary>
    public static class ProjectFiles
    {
        /// <summary>The comparison of paths of this system (Windows and macOS do not tell `A.script` from `a.script`).</summary>
        public static StringComparer PathComparer { get; } = OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        public static StringComparison PathComparison { get; } = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        private static readonly string[] SkippedFolders = { "bin", "obj", ".git", ".vs", "node_modules" };



        /// <summary>The file Publish leaves in each folder it copies into (docs/tools/projects.md, "Publish"): a folder that has it is no part of a project (the copies there are not its files).</summary>
        public const string PublishMarker = ".firepublish";

        public static bool IsPublishFolder(string folder)
        {
            try { return File.Exists(Path.Combine(folder, PublishMarker)); } catch (Exception) { return false; }
        }

        public static bool IsPattern(string text) => text.IndexOfAny(new[] { '*', '?' }) >= 0;

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Regex> Patterns = new(StringComparer.Ordinal);

        /// <summary><see cref="PatternToRegex"/>, made once per pattern (the build actions ask it for every file).</summary>
        public static Regex CachedPattern(string pattern)
        {
            if (Patterns.Count > 512) Patterns.Clear();
            return Patterns.GetOrAdd(pattern, PatternToRegex);
        }

        /// <summary>A pattern (`/` as the separator; `*` any characters but `/`, `**` any folders, `?` one character, `{a,b}` one of the alternatives) as a regular expression over relative paths.</summary>
        public static Regex PatternToRegex(string pattern)
        {
            string p = pattern.Replace('\\', '/');
            while (p.StartsWith("./", StringComparison.Ordinal)) p = p.Substring(2);
            var sb = new StringBuilder("^");
            int braces = 0;
            for (int i = 0; i < p.Length; i++)
            {
                char c = p[i];
                if (c == '*')
                {
                    if (i + 1 < p.Length && p[i + 1] == '*')
                    {
                        i++;
                        if (i + 1 < p.Length && p[i + 1] == '/') { i++; sb.Append("(?:.*/)?"); }
                        else sb.Append(".*");
                    }
                    else sb.Append("[^/]*");
                }
                else if (c == '?') sb.Append("[^/]");
                else if (c == '{') { sb.Append("(?:"); braces++; }
                else if (c == '}' && braces > 0) { sb.Append(')'); braces--; }
                else if (c == ',' && braces > 0) sb.Append('|');
                else sb.Append(Regex.Escape(c.ToString()));
            }
            sb.Append('$');
            return new Regex(sb.ToString(), OperatingSystem.IsLinux() ? RegexOptions.CultureInvariant : RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        }

        /// <summary>All files below `directory` as paths relative to it (with `/`), without the folders of builds and of other projects.</summary>
        private static List<string> Walk(string directory)
        {
            var found = new List<string>();

            void VisitRecusively(string dir, string rel)
            {
                IEnumerable<string> files;
                try { files = System.IO.Directory.EnumerateFiles(dir); } catch (Exception) { return; }
                
                foreach (var f in files) 
                    found.Add(rel + Path.GetFileName(f));
                
                IEnumerable<string> folders;
                
                try { folders = System.IO.Directory.EnumerateDirectories(dir); } catch (Exception) { return; }
                
                foreach (var folder in folders)
                {
                    var name = Path.GetFileName(folder);
                    if (SkippedFolders.Contains(name, StringComparer.OrdinalIgnoreCase) || name.StartsWith('.')) 
                        continue;

                    bool nestedProject;
                    try { nestedProject = System.IO.Directory.EnumerateFiles(folder, "*" + FireProject.Extension).Any(); } catch (Exception) { nestedProject = false; }
                    
                    if (nestedProject) 
                        continue;   // another project lives there: its files are its own
                    if (IsPublishFolder(folder))
                        continue;   // a folder Publish copies into (inside the project): what is there is a copy, not the project's
                    
                    VisitRecusively(folder, rel + name + "/");
                }
            }
            VisitRecusively(directory, "");
            return found;
        }

        /// <summary>The folder of the tests of a project (by convention; a test may stand in any file).</summary>
        public const string TestingFolder = "testing";

        /// <summary>The files of the project as full paths in the order they are compiled; `problems` gets what is wrong (a file that is named but does not exist).
        /// `folders`: the folders of the project as read already (null: read now) - nothing in an excluded folder is compiled, also a file that is named.</summary>
        public static List<string> Resolve(FireProject project, List<string>? problems = null, bool tests = false, ProjectFolderMap? folders = null)
        {
            string? dir = project.Directory;
            if (dir == null) return new List<string>();
            ProjectFolderMap? map = folders;
            // no files named: one sweep over all fire files below, by path (the default pattern has all the extensions in one: `{fire,script,fi,fic}`)
            var patterns = project.Files.Count > 0 ? project.Files : FireProject.DefaultFiles.ToList();
            var excludes = project.Exclude.Select(PatternToRegex).ToList();
            List<string>? walked = null;
            var result = new List<string>();
            var seen = new HashSet<string>(PathComparer);

            // (the build action Ignore or Copy takes a source out of the compile: docs/tools/projects.md, "Build actions")
            // (an excluded folder: docs/tools/projects.md, "Folders")
            bool Excluded(string rel) => excludes.Any(r => r.IsMatch(rel)) || project.BuildActions is { Count: > 0 } && FileBuildActions.DropsSource(FileBuildActions.Of(project, rel))
                || (map ??= ProjectFolderMap.Scan(project)).IsExcludedFile(rel);

            foreach (var entry in patterns)
            {
                if (IsPattern(entry))
                {
                    walked ??= (List<string>)[.. (map ??= ProjectFolderMap.Scan(project)).Files];
                    var regex = PatternToRegex(entry);
                    // markup files first: the classes generated from them are there before the scripts use them
                    foreach (var rel in walked.Where(r => regex.IsMatch(r)).OrderBy(r => FireProject.IsMarkupFile(r) ? 0 : 1).ThenBy(r => r, StringComparer.OrdinalIgnoreCase).ThenBy(r => r, StringComparer.Ordinal))
                    {
                        if (Excluded(rel)) continue;
                        // the folder `templates/` holds what the project offers as templates (docs/tools/templates.md): files with placeholders, not code of the project - unless the files are named
                        if (project.Files.Count == 0 && rel.StartsWith("templates/", StringComparison.OrdinalIgnoreCase)) continue;
                        // the folder `testing/` holds the tests of the project (docs/tools/testing.md): only `forge test` compiles them
                        if (project.Files.Count == 0 && !tests && rel.StartsWith(TestingFolder + "/", StringComparison.OrdinalIgnoreCase)) continue;
                        string full = Path.GetFullPath(rel, dir);
                        if (seen.Add(full)) result.Add(full);
                    }
                }
                else
                {
                    string rel = entry.Replace('\\', '/');
                    string full = Path.GetFullPath(rel, dir);
                    if (!File.Exists(full)) { problems?.Add($"The file '{entry}' of the project '{project.Name}' does not exist."); continue; }
                    if (Excluded(rel)) continue;
                    if (seen.Add(full)) result.Add(full);
                }
            }

            // the entry file runs last: what the other files declare is known by then
            if (project.Type == OutputType.Exe)
            {
                string? entryFull = project.Entry != null ? Path.GetFullPath(project.Entry.Replace('\\', '/'), dir) : null;
                if (entryFull != null)
                {
                    int at = result.FindIndex(f => PathComparer.Equals(f, entryFull));
                    if (at < 0) problems?.Add($"The entry file '{project.Entry}' of the project '{project.Name}' is not one of its files.");
                    else { result.RemoveAt(at); result.Add(entryFull); }
                }
            }
            return result;
        }

        /// <summary>All folders below `directory` (relative, with `/`), also the empty ones: the editor shows them so that a folder that was just made is there.</summary>
        public static List<string> Folders(string directory)
        {
            var found = new List<string>();
            void Visit(string dir, string rel)
            {
                IEnumerable<string> subs;
                try { subs = System.IO.Directory.EnumerateDirectories(dir); } catch (Exception) { return; }
                foreach (var sub in subs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    string name = Path.GetFileName(sub);
                    if (SkippedFolders.Contains(name, StringComparer.OrdinalIgnoreCase) || name.StartsWith('.')) continue;
                    bool nestedProject;
                    try { nestedProject = System.IO.Directory.EnumerateFiles(sub, "*" + FireProject.Extension).Any(); } catch (Exception) { nestedProject = false; }
                    if (nestedProject || IsPublishFolder(sub)) continue;
                    found.Add(rel + name);
                    if (found.Count > 2000) return;
                    Visit(sub, rel + name + "/");
                }
            }
            Visit(directory, "");
            return found;
        }

        /// <summary>The folders of the project that are not excluded (relative, with `/`; at most `limit`), as <see cref="Folders(string)"/> - read by <see cref="ProjectFolderMap"/>.</summary>
        public static List<string> Folders(ProjectFolderMap map, int limit = 2000) => map.Folders.Where(f => f.State != FolderState.Excluded).Select(f => f.Path).Take(limit).ToList();

        /// <summary><see cref="ContentFiles(string, IReadOnlyCollection{string}, int)"/> from the files of the folders that are not excluded.</summary>
        public static List<string> ContentFiles(ProjectFolderMap map, IReadOnlyCollection<string> compiled, int limit = 5000) => ContentFiles(map.Directory, compiled, limit, map.Files);

        /// <summary>What <see cref="Folders"/> and <see cref="ContentFiles"/> leave out of the folder of a project - the folders of builds (`bin`, `obj`), `node_modules`, folders and files whose
        /// name starts with a dot - as paths relative to it (with `/`): "Show All Files" of the solution explorer shows them pale. The contents of `.git` are not listed (the folder is), nor
        /// the folders of other projects; at most `limit` entries.</summary>
        /// `excluded`: the folders a project excludes besides (relative, `excludedDirectories`, a `.gitignore`): listed like the hidden ones, with what is in them.</summary>
        public static (List<string> Folders, List<string> Files) HiddenEntries(string directory, int limit = 2000, IReadOnlyCollection<string>? excluded = null)
        {
            var excludedSet = excluded is { Count: > 0 } ? new HashSet<string>(excluded, PathComparer) : null;
            var folders = new List<string>();
            var files = new List<string>();
            bool Full() => folders.Count + files.Count >= limit;
            void Visit(string dir, string rel, bool hidden)
            {
                IEnumerable<string> entries;
                try { entries = System.IO.Directory.EnumerateFiles(dir).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList(); } catch (Exception) { return; }
                foreach (var f in entries)
                {
                    if (Full()) return;
                    string name = Path.GetFileName(f);
                    if (hidden || name.StartsWith('.')) files.Add(rel + name);
                }
                try { entries = System.IO.Directory.EnumerateDirectories(dir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList(); } catch (Exception) { return; }
                foreach (var sub in entries)
                {
                    if (Full()) return;
                    string name = Path.GetFileName(sub);
                    bool skipped = SkippedFolders.Contains(name, StringComparer.OrdinalIgnoreCase) || name.StartsWith('.') || excludedSet?.Contains(rel + name) == true;
                    if (!hidden && !skipped)
                    {
                        bool nestedProject;
                        try { nestedProject = System.IO.Directory.EnumerateFiles(sub, "*" + FireProject.Extension).Any(); } catch (Exception) { nestedProject = false; }
                        if (!nestedProject) Visit(sub, rel + name + "/", false);
                        continue;
                    }
                    folders.Add(rel + name);
                    if (!name.Equals(".git", StringComparison.OrdinalIgnoreCase)) Visit(sub, rel + name + "/", true);
                }
            }
            Visit(directory, "", false);
            return (folders, files);
        }

        /// <summary>Everything in the folder of the project that is not compiled and not a project or solution file (resources, C++ sources, notes): the content of the project, shown by the editor
        /// and used by `new Resource("...")` and by the natives. `limit` keeps a project in a huge folder from listing it all.</summary>
        public static List<string> ContentFiles(string directory, IReadOnlyCollection<string> compiled, int limit = 5000) => ContentFiles(directory, compiled, limit, null);

        private static List<string> ContentFiles(string directory, IReadOnlyCollection<string> compiled, int limit, IReadOnlyList<string>? walked)
        {
            var compiledSet = new HashSet<string>(compiled, PathComparer);
            var result = new List<string>();
            foreach (var rel in (walked ?? Walk(directory)).OrderBy(r => r, StringComparer.OrdinalIgnoreCase).ThenBy(r => r, StringComparer.Ordinal))
            {
                string name = Path.GetFileName(rel);
                if (name.StartsWith('.')) continue;
                string ext = Path.GetExtension(name);
                if (ext.Equals(FireProject.Extension, StringComparison.OrdinalIgnoreCase) || ext.Equals(FireSolution.Extension, StringComparison.OrdinalIgnoreCase)) continue;
                string full = Path.GetFullPath(rel, directory);
                if (compiledSet.Contains(full)) continue;
                result.Add(full);
                if (result.Count >= limit) break;
            }
            return result;
        }

        /// <summary>The files that `patterns` (files and patterns relative to `directory`, in this order) name: the matches of one pattern by name; a named file that does not exist is a problem.
        /// `entryOf` gets, for each file, the index of the entry it came from.</summary>
        public static List<string> Expand(string directory, IEnumerable<string> patterns, List<string>? problems = null, Func<string, int>? rank = null, List<int>? entryOf = null)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(PathComparer);
            List<string>? walked = null;
            int index = -1;
            foreach (var entry in patterns)
            {
                index++;
                if (IsPattern(entry))
                {
                    walked ??= Walk(directory);
                    var regex = PatternToRegex(entry);
                    foreach (var rel in walked.Where(r => regex.IsMatch(r)).OrderBy(r => rank?.Invoke(r) ?? 0).ThenBy(r => r, StringComparer.OrdinalIgnoreCase).ThenBy(r => r, StringComparer.Ordinal))
                    {
                        string full = Path.GetFullPath(rel, directory);
                        if (seen.Add(full)) { result.Add(full); entryOf?.Add(index); }
                    }
                }
                else
                {
                    string full = Path.GetFullPath(entry.Replace('\\', '/'), directory);
                    if (!File.Exists(full)) { problems?.Add($"The file '{entry}' does not exist."); continue; }
                    if (seen.Add(full)) { result.Add(full); entryOf?.Add(index); }
                }
            }
            return result;
        }

        /// <summary>`path` relative to `directory` with `/` as the separator (a path outside of it stays `../x`).</summary>
        public static string Relative(string directory, string path) => Path.GetRelativePath(directory, path).Replace('\\', '/');
    }
}
