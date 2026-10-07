using fire.Projects;

namespace fire.Compiler
{
    /// <summary>The libraries of a project that a program imports (`#import "Name"`): their files are processed like the prelude of a package. One place for the linker and for the live
    /// diagnostics of the editor, so that both always see the same program.</summary>
    public static class ProjectLibraries
    {
        /// <summary>The files of the libraries named in `imports` (and everything they need, the ones that others need first), preprocessed by `preprocess` and with the path of each file.
        /// A file may import more libraries itself: `imports` grows while the files are processed (the import handler of `preprocess` adds to it), and the new ones are processed too.</summary>
        public static List<(ProcessedSource Source, string Path)> Process(BuildPlan plan, List<string> imports, Func<SourceFile, ProcessedSource> preprocess)
        {
            var processed = new Dictionary<string, List<(ProcessedSource, string)>>(StringComparer.OrdinalIgnoreCase);
            for (bool more = true; more;)
            {
                more = false;
                foreach (var library in plan.LibraryOrder(imports.ToList()))
                {
                    if (processed.ContainsKey(library.ImportName)) continue;
                    more = true;
                    var files = new List<(ProcessedSource, string)>();
                    foreach (var file in library.Sources)
                        files.Add((preprocess(file) with { Name = library.Name + "/" + file.FileName }, file.Path));
                    processed[library.ImportName] = files;
                }
            }
            var result = new List<(ProcessedSource Source, string Path)>();
            foreach (var library in plan.LibraryOrder(imports.ToList()))
                if (processed.TryGetValue(library.ImportName, out var files)) result.AddRange(files);
            return result;
        }

        /// <summary>Is `name` the import name of a library of the plan? Adds the (canonical) name to `imports` when it is.</summary>
        public static bool TryImport(BuildPlan? plan, string name, List<string> imports)
        {
            if (plan == null || !plan.Libraries.TryGetValue(name, out var library)) return false;
            if (!imports.Contains(library.ImportName, StringComparer.OrdinalIgnoreCase)) imports.Add(library.ImportName);
            return true;
        }
    }
}
