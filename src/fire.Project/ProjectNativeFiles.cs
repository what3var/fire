using fire.Package.Manager;

namespace fire.Projects
{
    /// <summary>Where a C/C++ file of a project stands in its native build (see <see cref="ProjectNativeFiles.Describe"/>).</summary>
    public sealed record NativeFileInfo
    {
        /// <summary>The path relative to the project file, with `/`.</summary>
        public required string Relative { get; init; }
        /// <summary>The file is one of the sources of the native part.</summary>
        public bool InBuild { get; init; }
        /// <summary>The lists it is in: null for `native.sources`, else the key of `native.platformSources` (empty when not in the build).</summary>
        public IReadOnlyList<string?> Groups { get; init; } = Array.Empty<string?>();
        /// <summary>The entry of the project file that names it: the file itself, or a pattern (null: not in the build; `native.sources` not set: the default pattern).</summary>
        public string? Entry { get; init; }
        /// <summary>The entry is the file itself (not a pattern): it can be moved and taken out.</summary>
        public bool Explicit { get; init; }
        /// <summary>The build order (0 when none is given) and whether one is given.</summary>
        public int Order { get; init; }
        public bool HasOrder { get; init; }
        /// <summary>The place in the build of its group (1 = first; the common sources alone for `native.sources`, with the platform's for a platform group) and how many there are.</summary>
        public int Position { get; init; }
        public int Count { get; init; }
        /// <summary>The problems of the order of this build that name the file (an `#include` of a source that is built after it).</summary>
        public IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();
    }

    /// <summary>
    /// The native build options of one file of a project, read and changed in the project file (`native.sources`, `native.platformSources`): whether it is built, for which
    /// platform and its build order (`{ "file": "native/x.h", "order": 10 }`, see <see cref="NativeSourceList"/>). The changes are made to the <see cref="FireProject"/>; the caller saves it.
    /// </summary>
    public static class ProjectNativeFiles
    {
        public static readonly string[] Extensions = { ".h", ".hpp", ".hh", ".hxx", ".c", ".cc", ".cpp", ".cxx" };

        /// <summary>The default sources written out (when an option is set on a file of a project that does not list its sources): the headers first, as the default does.</summary>
        private static readonly string[] DefaultSourcesWritten = { "native/**/*.{h,hpp,hh}", "native/**/*.{c,cc,cpp,cxx}" };

        /// <summary>The platform groups an editor offers (more can be written in the project file).</summary>
        public static readonly string[] KnownGroups = { "posix", "windows", "freertos", "linux", "macos", "esp32" };

        public static bool IsNativeFile(string path) => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

        private static IEnumerable<(string? Group, NativeSourceList List)> Lists(ProjectNative n)
        {
            yield return (null, n.Sources ?? new NativeSourceList(ProjectNative.DefaultSources));
            foreach (var (key, list) in n.PlatformSources ?? new()) yield return (key, list);
        }

        /// <summary>The entry of one list that brings the file in (its index), -1: none.</summary>
        private static int EntryOf(string dir, NativeSourceList list, string full)
        {
            var from = new List<int>();
            var files = ProjectFiles.Expand(dir, list, null, null, from);
            for (int i = 0; i < files.Count; i++) if (ProjectFiles.PathComparer.Equals(files[i], full)) return from[i];
            return -1;
        }

        private static bool Names(FireProject project, string entry, string full) =>
            !ProjectFiles.IsPattern(entry) && ProjectFiles.PathComparer.Equals(Path.GetFullPath(entry.Replace('\\', '/'), project.Directory!), full);

        /// <summary>The pattern of a list that would still bring the file in when its own entries were taken out (null: none).</summary>
        private static string? PatternWithout(FireProject project, NativeSourceList list, string full)
        {
            var rest = new NativeSourceList(list.Where(e => !Names(project, e, full)));
            int at = EntryOf(project.Directory!, rest, full);
            return at < 0 ? null : rest[at];
        }

        /// <summary>The native build options of a C/C++ file of the project (null: it is no C/C++ file).</summary>
        public static NativeFileInfo? Describe(FireProject project, string file)
        {
            if (!IsNativeFile(file) || project.Directory == null) return null;
            string dir = project.Directory;
            string full = Path.GetFullPath(file);
            string rel = ProjectFiles.Relative(dir, full);
            var n = project.Native;
            if (n == null) return new NativeFileInfo { Relative = rel };
            var groups = new List<string?>();
            string? entry = null;
            bool isExplicit = false;
            int? order = null;
            foreach (var (group, list) in Lists(n))
            {
                int at = EntryOf(dir, list, full);
                if (at < 0) continue;
                groups.Add(group);
                if (entry == null || !isExplicit && !ProjectFiles.IsPattern(list[at]))
                {
                    entry = list[at];
                    isExplicit = !ProjectFiles.IsPattern(list[at]);
                    order = list.OrderOf(list[at]);
                }
            }
            if (groups.Count == 0) return new NativeFileInfo { Relative = rel };

            // the place in the build of its first group, and the problems of that build that name the file
            var native = ResolveFiles(project);
            var keys = groups[0] == null ? Array.Empty<string>() : new[] { groups[0]! };
            var build = native.SourcesFor(keys);
            int position = build.ToList().FindIndex(f => ProjectFiles.PathComparer.Equals(f, full)) + 1;
            var texts = new List<(string, string)>();
            foreach (var f in build) { try { texts.Add((f, File.ReadAllText(f))); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
            var problems = NativeSourceOrder.CheckIncludes(texts, f => ProjectFiles.Relative(dir, f)).Where(p => p.Contains("'" + rel + "'")).ToList();
            return new NativeFileInfo
            {
                Relative = rel, InBuild = true, Groups = groups, Entry = entry, Explicit = isExplicit, Order = order ?? 0, HasOrder = order != null,
                Position = position, Count = build.Count, Problems = problems,
            };
        }

        /// <summary>The sources of the project's native part as files (patterns expanded, orders kept), as the build has them.</summary>
        private static PackageNative ResolveFiles(FireProject project)
        {
            var n = project.Native!;
            string dir = project.Directory!;
            var native = new PackageNative { Sources = ProjectNatives.ExpandOrdered(dir, n.Sources ?? new NativeSourceList(ProjectNative.DefaultSources), null, n.Sources == null ? ProjectNativeRank : null) };
            foreach (var (key, list) in n.PlatformSources ?? new()) native.PlatformSources[key] = ProjectNatives.ExpandOrdered(dir, list, null);
            return native;
        }

        private static int ProjectNativeRank(string path) => new[] { ".h", ".hpp", ".hh", ".hxx" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) ? 0 : 1;

        /// <summary>The list `native.sources`, written out when the project relies on the default.</summary>
        private static NativeSourceList OwnSources(ProjectNative n) => n.Sources ??= new NativeSourceList(DefaultSourcesWritten);

        /// <summary>Gives the file a build order (null: takes it away). A file that comes from a pattern gets an entry of its own just before the pattern. Returns a problem (nothing changed) or null.</summary>
        public static string? SetOrder(FireProject project, string file, int? order)
        {
            var info = Describe(project, file);
            if (info == null) return "Only C/C++ files have a build order.";
            if (!info.InBuild) return $"'{info.Relative}' is not part of the native build.";
            var n = project.Native!;
            string full = Path.GetFullPath(file);
            bool done = false;
            foreach (var (group, listed) in Lists(n).ToList())
            {
                var list = listed;
                int at = EntryOf(project.Directory!, list, full);
                if (at < 0) continue;
                done = true;
                if (!ProjectFiles.IsPattern(list[at])) { list.SetOrder(list[at], order); continue; }
                if (order == null) continue;   // (from a pattern: it has none)
                if (group == null && n.Sources == null) { list = OwnSources(n); at = EntryOf(project.Directory!, list, full); }
                list.Insert(at, info.Relative);
                list.SetOrder(info.Relative, order);
            }
            return done ? null : $"'{info.Relative}' is not part of the native build.";
        }

        /// <summary>Puts the file into the native build (into `native.sources`; a project without a native part gets one) or takes it out (only a file the project file names itself;
        /// one that comes from a pattern cannot be). Returns a problem (nothing changed) or null.</summary>
        public static string? SetInBuild(FireProject project, string file, bool inBuild)
        {
            var info = Describe(project, file);
            if (info == null) return "Only C/C++ files are part of the native build.";
            if (info.InBuild == inBuild) return null;
            if (inBuild)
            {
                project.Native ??= new ProjectNative { Sources = new NativeSourceList() };
                OwnSources(project.Native).Add(info.Relative);
                return null;
            }
            var n = project.Native!;
            string full = Path.GetFullPath(file);
            foreach (var (_, list) in Lists(n))
                if (PatternWithout(project, list, full) is { } pattern)
                    return $"'{info.Relative}' comes from the pattern '{pattern}': to leave it out, list the files of the native part one by one in the project file (native.sources).";
            foreach (var (_, list) in Lists(n).ToList()) RemoveEntries(project, list, full);
            return null;
        }

        /// <summary>Takes the entries out that name the file itself; returns the build order of the first.</summary>
        private static int? RemoveEntries(FireProject project, NativeSourceList list, string full)
        {
            int? order = null;
            for (int i = list.Count - 1; i >= 0; i--)
                if (Names(project, list[i], full))
                {
                    order = list.OrderOf(list[i]) ?? order;
                    list.SetOrder(list[i], null);
                    list.RemoveAt(i);
                }
            return order;
        }

        /// <summary>Moves the file to another list: null = `native.sources` (every platform), else a key of `native.platformSources`. Only a file the project file names itself, in one list.
        /// The build order goes with it. Returns a problem (nothing changed) or null.</summary>
        public static string? SetGroup(FireProject project, string file, string? group)
        {
            group = string.IsNullOrWhiteSpace(group) ? null : group.Trim();
            var info = Describe(project, file);
            if (info == null) return "Only C/C++ files have a platform.";
            if (!info.InBuild) return $"'{info.Relative}' is not part of the native build.";
            if (info.Groups.Count == 1 && string.Equals(info.Groups[0], group, StringComparison.OrdinalIgnoreCase)) return null;
            if (!info.Explicit || info.Groups.Count != 1) return $"'{info.Relative}' comes from a pattern or is in several lists: change its platform in the project file (native.platformSources).";
            var n = project.Native!;
            string full = Path.GetFullPath(file);
            foreach (var (_, list) in Lists(n))
                if (PatternWithout(project, list, full) is { } pattern)
                    return $"'{info.Relative}' also comes from the pattern '{pattern}': change its platform in the project file (native.platformSources).";
            int? order = null;
            foreach (var (_, list) in Lists(n).ToList()) order ??= RemoveEntries(project, list, full);
            NativeSourceList target;
            if (group == null) target = OwnSources(n);
            else
            {
                n.PlatformSources ??= new Dictionary<string, NativeSourceList>();
                string? key = n.PlatformSources.Keys.FirstOrDefault(k => string.Equals(k, group, StringComparison.OrdinalIgnoreCase));
                if (key == null) n.PlatformSources[key = group] = new NativeSourceList();
                target = n.PlatformSources[key];
            }
            target.Add(info.Relative);
            if (order != null) target.SetOrder(info.Relative, order);
            return null;
        }
    }
}
