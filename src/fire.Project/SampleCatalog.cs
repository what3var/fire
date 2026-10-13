using System.Text.Json;

namespace fire.Projects
{
    /// <summary>One sample project (a folder under Samples\ next to the program): its title and description from `sample.json`, the project file, the files to open afterwards.</summary>
    public sealed class FireSample
    {
        public string Folder { get; init; } = "";
        /// <summary>The category: the folders between `Samples\` and the sample, joined with " / " (empty: the sample lies directly in `Samples\`).</summary>
        public string Category { get; init; } = "";
        public string Title { get; init; } = "";
        public string Description { get; init; } = "";
        public int Order { get; init; } = 100;
        /// <summary>The files (relative to the sample) that the editor opens afterwards.</summary>
        public IReadOnlyList<string> Open { get; init; } = Array.Empty<string>();
        /// <summary>The name of the project file in the folder.</summary>
        public string ProjectFile { get; init; } = "";
        /// <summary>The folder name, also the proposed name of the copy.</summary>
        public string Name => Path.GetFileName(Folder);
    }

    /// <summary>The sample projects that ship with fire (Help > Samples): found in `Samples\` (a folder with a project file is a sample, any other folder is a category that holds samples),
    /// each opened as a copy in a folder of the user's choice.</summary>
    public static class SampleCatalog
    {
        public static string BuiltinRoot => Path.Combine(AppContext.BaseDirectory, "Samples");

        public static IReadOnlyList<FireSample> Load(string? root = null)
        {
            root ??= BuiltinRoot;
            var list = new List<FireSample>();
            if (!Directory.Exists(root)) return list;
            Collect(root, "", list);
            return list.OrderBy(s => s.Order).ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void Collect(string folder, string category, List<FireSample> list)
        {
            foreach (string sub in Directory.GetDirectories(folder))
            {
                string project = Directory.GetFiles(sub, "*.fireproj").FirstOrDefault() ?? "";
                if (project.Length == 0)
                {
                    // no project file: a category folder, its samples are below
                    Collect(sub, category.Length == 0 ? Path.GetFileName(sub) : category + " / " + Path.GetFileName(sub), list);
                    continue;
                }
                string title = Path.GetFileName(sub), description = "";
                int order = 100;
                var open = new List<string>();
                string json = Path.Combine(sub, "sample.json");
                if (File.Exists(json))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(json), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                        var e = doc.RootElement;
                        if (e.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String) title = t.GetString()!;
                        if (e.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String) description = d.GetString()!;
                        if (e.TryGetProperty("order", out var o) && o.TryGetInt32(out int n)) order = n;
                        if (e.TryGetProperty("open", out var op) && op.ValueKind == JsonValueKind.Array)
                            foreach (var item in op.EnumerateArray()) if (item.ValueKind == JsonValueKind.String) open.Add(item.GetString()!);
                    }
                    catch (JsonException) { }
                }
                list.Add(new FireSample { Folder = sub, Category = category, Title = title, Description = description, Order = order, Open = open, ProjectFile = Path.GetFileName(project) });
            }
        }

        /// <summary>The categories of the samples in the order of their first sample (empty when no sample has one).</summary>
        public static IReadOnlyList<string> Categories(IEnumerable<FireSample> samples) =>
            samples.Where(s => s.Category.Length > 0).OrderBy(s => s.Order).ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase).Select(s => s.Category).Distinct().ToList();

        /// <summary>Copies the sample (without `sample.json`) into `target`, which must not exist or be empty; returns the path of the project file.</summary>
        public static string CopyTo(FireSample sample, string target)
        {
            target = Path.GetFullPath(target);
            if (File.Exists(target) || (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any()))
                throw new ProjectException($"The folder '{target}' exists and is not empty.");
            CopyFolder(sample.Folder, target);
            return Path.Combine(target, sample.ProjectFile);
        }

        private static void CopyFolder(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (string file in Directory.GetFiles(from))
            {
                if (Path.GetFileName(file) == "sample.json") continue;
                File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
            }
            foreach (string dir in Directory.GetDirectories(from)) CopyFolder(dir, Path.Combine(to, Path.GetFileName(dir)));
        }
    }
}
