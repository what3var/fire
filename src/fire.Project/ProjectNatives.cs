using System.Text.RegularExpressions;
using fire.Package.Manager;

namespace fire.Projects
{
    /// <summary>The C++ natives of a project (or of a library project), ready to build: what the project's `native` part and the folder `native/` give (docs/PROJECTS.md).</summary>
    public sealed record NativePart(string ImportName, string ProjectName, string Directory, PackageNative Native);

    /// <summary>
    /// Finds the natives of a project: its C++ files (default: everything in the folder `native/`, the headers first) and the functions in them. Every `inline Value name(Value a, ...)` is a native
    /// `__name` for fire code; a last parameter `OwnList* list` says that the function allocates its result (and returns it). `native.functions` of the project replaces the finding for a C++ name and says more
    /// (`returnsReference`, a different name for fire).
    /// </summary>
    public static class ProjectNatives
    {
        private static readonly Regex FunctionPattern = new(@"^[ \t]*(?:static[ \t]+)?inline[ \t]+Value[ \t]+(\w+)[ \t]*\(([^)]*)\)", RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex CommentPattern = new(@"//[^\n]*|/\*.*?\*/", RegexOptions.Singleline | RegexOptions.Compiled);

        private static int Rank(string path) => new[] { ".h", ".hpp", ".hh", ".hxx" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) ? 0 : 1;

        /// <summary>The functions that the text of C++ files declares (`inline Value name(Value a, Value b)`), without the ones in comments.</summary>
        public static List<PackageNativeFunction> Discover(string cppText)
        {
            var result = new List<PackageNativeFunction>();
            string text = CommentPattern.Replace(cppText, m => new string(' ', 0));
            foreach (Match m in FunctionPattern.Matches(text))
            {
                var parameters = m.Groups[2].Value.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).Where(p => p.Length > 0 && p != "void").ToList();
                bool needsList = parameters.Count > 0 && Regex.IsMatch(parameters[^1], @"\bOwnList\s*\*");
                int arguments = parameters.Count - (needsList ? 1 : 0);
                if (parameters.Take(arguments).Any(p => !Regex.IsMatch(p, @"^Value\b"))) continue;   // not a native: its parameters are not all Value
                result.Add(new PackageNativeFunction { Name = "__" + m.Groups[1].Value, Arguments = arguments, Cpp = m.Groups[1].Value, NeedsList = needsList, ReturnsReference = needsList });
            }
            return result;
        }

        /// <summary>The native part of a project as a package has it (absolute paths), null if the project has none. `problems` gets what is wrong.</summary>
        public static NativePart? Resolve(LoadedProject project, List<string>? problems = null)
        {
            var n = project.Project.Native;
            if (n == null) return null;
            string dir = project.Directory;
            var found = new List<string>();
            var sources = ProjectFiles.Expand(dir, n.Sources ?? ProjectNative.DefaultSources.ToList(), found, n.Sources == null ? Rank : null);
            var native = new PackageNative { Reset = n.Reset };
            native.Sources.AddRange(sources);
            if (n.PlatformSources != null)
                foreach (var (key, patterns) in n.PlatformSources) native.PlatformSources[key] = ProjectFiles.Expand(dir, patterns, found);
            if (n.Platforms != null) native.Platforms.AddRange(n.Platforms);
            if (n.LinkLibraries != null) foreach (var (key, libs) in n.LinkLibraries) native.LinkLibraries[key] = libs.ToList();
            if (n.Exceptions != null) native.Exceptions.AddRange(n.Exceptions);
            foreach (var p in found) problems?.Add($"{project.Name}: native: {p}");
            if (sources.Count == 0 && native.PlatformSources.Count == 0) problems?.Add($"{project.Name}: the project has a native part but no C++ files (put them into the folder 'native').");

            // the functions: the ones named in the project, then the ones found in the files
            var explicitCpp = new HashSet<string>(n.Functions?.Select(f => f.Cpp) ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            if (n.Functions != null) native.Functions.AddRange(n.Functions);
            foreach (var file in sources.Concat(native.PlatformSources.Values.SelectMany(v => v)))
            {
                string text;
                try { text = File.ReadAllText(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { problems?.Add($"{project.Name}: cannot read '{file}': {ex.Message}"); continue; }
                foreach (var f in Discover(text))
                    if (!explicitCpp.Contains(f.Cpp) && !native.Functions.Any(x => x.Name == f.Name)) native.Functions.Add(f);
            }
            return new NativePart(project.Project.ImportName, project.Name, dir, native);
        }
    }
}
