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
                    foreach (var rel in walked.Where(r => regex.IsMatch(r)).OrderBy(r => r, StringComparer.OrdinalIgnoreCase).ThenBy(r => r, StringComparer.Ordinal))
                    {
                        if (Excluded(rel)) continue;
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

        /// <summary>`path` relative to `directory` with `/` as the separator (a path outside of it stays `../x`).</summary>
        public static string Relative(string directory, string path) => Path.GetRelativePath(directory, path).Replace('\\', '/');
    }
}
