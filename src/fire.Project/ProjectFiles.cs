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

        public static bool IsPattern(string text) => text.IndexOfAny(new[] { '*', '?' }) >= 0;

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
            void Visit(string dir, string rel)
            {
                IEnumerable<string> files;
                try { files = System.IO.Directory.EnumerateFiles(dir); } catch (Exception) { return; }
                foreach (var f in files) found.Add(rel + Path.GetFileName(f));
                IEnumerable<string> subs;
                try { subs = System.IO.Directory.EnumerateDirectories(dir); } catch (Exception) { return; }
                foreach (var sub in subs)
                {
                    string name = Path.GetFileName(sub);
                    if (SkippedFolders.Contains(name, StringComparer.OrdinalIgnoreCase) || name.StartsWith('.')) continue;
                    bool nestedProject;
                    try { nestedProject = System.IO.Directory.EnumerateFiles(sub, "*" + FireProject.Extension).Any(); } catch (Exception) { nestedProject = false; }
                    if (nestedProject) continue;   // another project lives there: its files are its own
                    Visit(sub, rel + name + "/");
                }
            }
            Visit(directory, "");
            return found;
        }

        /// <summary>The files of the project as full paths in the order they are compiled; `problems` gets what is wrong (a file that is named but does not exist).</summary>
        public static List<string> Resolve(FireProject project, List<string>? problems = null)
        {
            string? dir = project.Directory;
            if (dir == null) return new List<string>();
            // no files named: one sweep over all fire files below, by path (the default pattern has all the extensions in one: `{script,fi,fic}`)
            var patterns = project.Files.Count > 0 ? project.Files : FireProject.DefaultFiles.ToList();
            var excludes = project.Exclude.Select(PatternToRegex).ToList();
            List<string>? walked = null;
            var result = new List<string>();
            var seen = new HashSet<string>(PathComparer);

            bool Excluded(string rel) => excludes.Any(r => r.IsMatch(rel));

            foreach (var entry in patterns)
            {
                if (IsPattern(entry))
                {
                    walked ??= Walk(dir);
                    var regex = PatternToRegex(entry);
                    // markup files first: the classes generated from them are there before the scripts use them
                    foreach (var rel in walked.Where(r => regex.IsMatch(r)).OrderBy(r => FireProject.IsMarkupFile(r) ? 0 : 1).ThenBy(r => r, StringComparer.OrdinalIgnoreCase).ThenBy(r => r, StringComparer.Ordinal))
                    {
                        if (Excluded(rel)) continue;
                        // the folder `templates/` holds what the project offers as templates (docs/TEMPLATES.md): files with placeholders, not code of the project - unless the files are named
                        if (project.Files.Count == 0 && rel.StartsWith("templates/", StringComparison.OrdinalIgnoreCase)) continue;
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
                    if (nestedProject) continue;
                    found.Add(rel + name);
                    if (found.Count > 2000) return;
                    Visit(sub, rel + name + "/");
                }
            }
            Visit(directory, "");
            return found;
        }

        /// <summary>Everything in the folder of the project that is not compiled and not a project or solution file (resources, C++ sources, notes): the content of the project, shown by the editor
        /// and used by `new Resource("...")` and by the natives. `limit` keeps a project in a huge folder from listing it all.</summary>
        public static List<string> ContentFiles(string directory, IReadOnlyCollection<string> compiled, int limit = 5000)
        {
            var compiledSet = new HashSet<string>(compiled, PathComparer);
            var result = new List<string>();
            foreach (var rel in Walk(directory).OrderBy(r => r, StringComparer.OrdinalIgnoreCase).ThenBy(r => r, StringComparer.Ordinal))
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

        /// <summary>The files that `patterns` (files and patterns relative to `directory`, in this order) name: the matches of one pattern by name; a named file that does not exist is a problem.</summary>
        public static List<string> Expand(string directory, IEnumerable<string> patterns, List<string>? problems = null, Func<string, int>? rank = null)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(PathComparer);
            List<string>? walked = null;
            foreach (var entry in patterns)
            {
                if (IsPattern(entry))
                {
                    walked ??= Walk(directory);
                    var regex = PatternToRegex(entry);
                    foreach (var rel in walked.Where(r => regex.IsMatch(r)).OrderBy(r => rank?.Invoke(r) ?? 0).ThenBy(r => r, StringComparer.OrdinalIgnoreCase).ThenBy(r => r, StringComparer.Ordinal))
                    {
                        string full = Path.GetFullPath(rel, directory);
                        if (seen.Add(full)) result.Add(full);
                    }
                }
                else
                {
                    string full = Path.GetFullPath(entry.Replace('\\', '/'), directory);
                    if (!File.Exists(full)) { problems?.Add($"The file '{entry}' does not exist."); continue; }
                    if (seen.Add(full)) result.Add(full);
                }
            }
            return result;
        }

        /// <summary>`path` relative to `directory` with `/` as the separator (a path outside of it stays `../x`).</summary>
        public static string Relative(string directory, string path) => Path.GetRelativePath(directory, path).Replace('\\', '/');
    }
}
