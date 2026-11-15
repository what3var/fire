using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace fire.Projects.Mcp
{
    /// <summary>
    /// The documentation as sections (a Markdown file cut at its headings), found by the words of a question (BM25: a word that is rare counts more, a section
    /// that is long a little less; the words of the heading count twice). Small and without a model: the agent asks it, then reads what it found.
    /// </summary>
    public sealed class DocIndex
    {
        public sealed record Section(string File, string Heading, string Text, int Line);

        private readonly List<Section> _sections = new();
        private readonly List<Dictionary<string, int>> _terms = new();
        private readonly List<int> _lengths = new();
        private readonly Dictionary<string, int> _documentFrequency = new();
        private double _averageLength;

        /// <summary>The folder the files are in (paths are relative to it, with `/`).</summary>
        public string Root { get; }
        public int Count => _sections.Count;

        private DocIndex(string root) { Root = root; }

        /// <summary>All Markdown files below `root`.</summary>
        public static DocIndex Load(string root)
        {
            var index = new DocIndex(root);
            if (!Directory.Exists(root)) return index;
            foreach (string file in Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                string[] lines;
                try { lines = File.ReadAllLines(file); } catch (IOException) { continue; }
                string heading = Path.GetFileNameWithoutExtension(file);
                var text = new StringBuilder();
                int start = 1;
                bool inCode = false;
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (line.TrimStart().StartsWith("```")) inCode = !inCode;
                    if (!inCode && line.StartsWith('#'))
                    {
                        index.Add(rel, heading, text.ToString(), start);
                        heading = line.TrimStart('#').Trim();
                        text.Clear();
                        start = i + 1;
                        continue;
                    }
                    text.Append(line).Append('\n');
                }
                index.Add(rel, heading, text.ToString(), start);
            }
            index._averageLength = index._lengths.Count == 0 ? 1 : index._lengths.Average();
            return index;
        }

        private void Add(string file, string heading, string text, int line)
        {
            if (text.Trim().Length == 0 && heading.Length == 0) return;
            var terms = new Dictionary<string, int>();
            int length = 0;
            foreach (var w in Words(text)) { terms[w] = terms.GetValueOrDefault(w) + 1; length++; }
            foreach (var w in Words(heading)) { terms[w] = terms.GetValueOrDefault(w) + 2; length += 2; }
            foreach (var w in Words(file.Replace('/', ' ').Replace(".md", ""))) terms[w] = terms.GetValueOrDefault(w) + 1;
            foreach (var w in terms.Keys) _documentFrequency[w] = _documentFrequency.GetValueOrDefault(w) + 1;
            _sections.Add(new Section(file, heading, text, line));
            _terms.Add(terms);
            _lengths.Add(Math.Max(1, length));
        }

        /// <summary>The words of a text: letters and digits, lower case, at least two; a name with dots or capitals also gives its parts (Data.Entity: data, entity,
        /// data.entity; AddTool: addtool, add, tool).</summary>
        public static IEnumerable<string> Words(string text)
        {
            var word = new StringBuilder();
            foreach (char c in text + " ")
            {
                if (char.IsLetterOrDigit(c) || c == '_' || c == '.') { word.Append(c); continue; }
                if (word.Length > 0)
                {
                    string w = word.ToString().Trim('.');
                    word.Clear();
                    if (w.Length < 2) continue;
                    yield return w.ToLowerInvariant();
                    if (w.Contains('.')) foreach (var part in w.Split('.')) if (part.Length >= 2) yield return part.ToLowerInvariant();
                    // camelCase / PascalCase parts
                    int from = 0;
                    for (int i = 1; i < w.Length; i++)
                        if (char.IsUpper(w[i]) && char.IsLower(w[i - 1]))
                        {
                            if (i - from >= 2 && from > 0 || (from == 0 && i >= 2 && i < w.Length)) yield return w.Substring(from, i - from).ToLowerInvariant();
                            from = i;
                        }
                    if (from > 0 && w.Length - from >= 2) yield return w.Substring(from).ToLowerInvariant();
                }
            }
        }

        /// <summary>The sections that fit the words of `query` best (at most `max`), with their scores.</summary>
        public IReadOnlyList<(Section Section, double Score)> Search(string query, int max = 5)
        {
            var words = Words(query).Distinct().Where(w => !Stop.Contains(w)).ToList();
            if (words.Count == 0) return Array.Empty<(Section, double)>();
            const double k1 = 1.2, b = 0.75;
            int n = _sections.Count;
            var scored = new List<(Section, double)>();
            for (int i = 0; i < n; i++)
            {
                double score = 0;
                foreach (var w in words)
                {
                    if (!_terms[i].TryGetValue(w, out int tf)) continue;
                    int df = _documentFrequency.GetValueOrDefault(w);
                    double idf = Math.Log(1 + (n - df + 0.5) / (df + 0.5));
                    score += idf * tf * (k1 + 1) / (tf + k1 * (1 - b + b * _lengths[i] / _averageLength));
                }
                if (score > 0) scored.Add((_sections[i], score));
            }
            return scored.OrderByDescending(s => s.Item2).Take(max).ToList();
        }

        private static readonly HashSet<string> Stop = new()
        {
            "the", "a", "an", "of", "in", "on", "to", "is", "are", "and", "or", "for", "with", "how", "what", "do", "does", "can", "it", "this", "that", "be", "by",
            "as", "at", "from", "my", "me", "use", "used", "using", "der", "die", "das", "und", "wie", "ein", "eine", "ist", "mit", "von", "zu", "den", "im", "ich",
        };

        /// <summary>A file (path relative to Root), or only its section with the heading; null when there is none.</summary>
        public string? Read(string path, string? heading = null)
        {
            path = path.Replace('\\', '/').TrimStart('/');
            if (heading != null && heading.Trim() != "")
            {
                string h = heading.Trim().TrimStart('#').Trim();
                var section = _sections.FirstOrDefault(s => string.Equals(s.File, path, StringComparison.OrdinalIgnoreCase) && string.Equals(s.Heading, h, StringComparison.OrdinalIgnoreCase))
                              ?? _sections.FirstOrDefault(s => string.Equals(s.File, path, StringComparison.OrdinalIgnoreCase) && s.Heading.Contains(h, StringComparison.OrdinalIgnoreCase));
                if (section != null) return "## " + section.Heading + "\n" + section.Text;
            }
            string full = Path.GetFullPath(Path.Combine(Root, path));
            if (!full.StartsWith(Path.GetFullPath(Root), StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) return null;
            return File.ReadAllText(full);
        }

        /// <summary>The headings of a file (its table of contents).</summary>
        public IEnumerable<string> Headings(string path) => _sections.Where(s => string.Equals(s.File, path, StringComparison.OrdinalIgnoreCase)).Select(s => s.Heading);

        public IEnumerable<string> Files => _sections.Select(s => s.File).Distinct();
    }

    /// <summary>The tools of the documentation and the samples (spark's MCP server, docs/tools/spark-mcp.md): search_docs, read_doc, list_samples, list_sample_files,
    /// read_sample_file. Each gives back little (a few hits, one section, one file): a small model has a small context.</summary>
    public static class KnowledgeTools
    {
        private static string Snippet(string text, int max = 220)
        {
            string flat = string.Join(" ", text.Split(new[] { '\n', '\r', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries));
            return flat.Length <= max ? flat : flat.Substring(0, max) + " ...";
        }

        private static string? Str(JsonElement args, string name) =>
            args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()) : null;

        /// <summary>Registers the tools; the index of `docsRoot` is made at the first search (in the background of the call).</summary>
        public static void Register(McpServer server, string docsRoot, string samplesRoot)
        {
            DocIndex? index = null;
            DocIndex Index() => index ??= DocIndex.Load(docsRoot);

            server.AddTool("search_docs", "Searches the documentation of fire (the language, the libraries, the tools) for the words of a question; the sections that fit " +
                                          "best, each with its file and heading - read one with read_doc.",
                "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\",\"description\":\"the words to look for, e.g. 'list sort' or 'http server route'\"}," +
                "\"max\":{\"type\":\"integer\",\"description\":\"how many sections (3)\"}},\"required\":[\"query\"]}",
                args => System.Threading.Tasks.Task.Run(() =>
                {
                    string query = Str(args, "query") ?? "";
                    int max = int.TryParse(Str(args, "max"), out int m) && m > 0 ? Math.Min(m, 10) : 3;
                    var found = Index().Search(query, max);
                    if (found.Count == 0) return McpToolResult.Ok("Nothing found for: " + query);
                    var sb = new StringBuilder();
                    foreach (var (s, score) in found)
                        sb.Append("path: ").Append(s.File).Append(" heading: ").Append(s.Heading).Append(" - ").Append(Snippet(s.Text, 160)).Append('\n');
                    return McpToolResult.Ok(sb.ToString());
                }));

            server.AddTool("read_doc", "Reads one section of a page of the documentation (the path and heading that search_docs named). Without a heading: the headings of the page.",
                "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\",\"description\":\"the file, e.g. 'language/07-arrays-and-lists.md'\"}," +
                "\"heading\":{\"type\":\"string\",\"description\":\"a heading of the file\"}},\"required\":[\"path\"]}",
                args => System.Threading.Tasks.Task.Run(() =>
                {
                    string path = (Str(args, "path") ?? "").Replace('\\', '/').TrimStart('/');
                    string? heading = Str(args, "heading");
                    var headings = Index().Headings(path).ToList();
                    if (headings.Count == 0)
                    {
                        var like = Index().Files.Where(f => f.Contains(Path.GetFileNameWithoutExtension(path), StringComparison.OrdinalIgnoreCase)).Take(5).ToList();
                        return McpToolResult.Error($"There is no page '{path}'." + (like.Count > 0 ? " Did you mean: " + string.Join(", ", like) : ""));
                    }
                    if (string.IsNullOrWhiteSpace(heading))
                        return McpToolResult.Ok($"The headings of {path} (read one with read_doc and its heading):\n- " + string.Join("\n- ", headings));
                    string? text = Index().Read(path, heading);
                    if (text == null || !text.StartsWith("## "))
                        return McpToolResult.Error($"{path} has no heading '{heading}'. Its headings: " + string.Join(" | ", headings));
                    const int limit = 4000;
                    if (text.Length > limit) text = text.Substring(0, limit) + "\n... (the section goes on)";
                    return McpToolResult.Ok(text);
                }));

            server.AddTool("list_samples", "The sample programs that come with fire: name and what each shows. list_sample_files names the files of one.", "",
                _ => System.Threading.Tasks.Task.Run(() =>
                {
                    var samples = SampleCatalog.Load(samplesRoot);
                    if (samples.Count == 0) return McpToolResult.Ok("There are no samples.");
                    return McpToolResult.Ok(string.Join("\n", samples.Select(s => $"{s.Name}: {s.Description}")));
                }));

            FireSample? Find(string name)
            {
                var samples = SampleCatalog.Load(samplesRoot);
                return samples.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                       ?? samples.FirstOrDefault(s => string.Equals(s.Title, name, StringComparison.OrdinalIgnoreCase))
                       ?? samples.FirstOrDefault(s => name.Length > 2 && (s.Name.Contains(name, StringComparison.OrdinalIgnoreCase) || s.Title.Contains(name, StringComparison.OrdinalIgnoreCase)));
            }
            static List<string> FilesOf(FireSample sample) =>
                Directory.EnumerateFiles(sample.Folder, "*", SearchOption.AllDirectories)
                    .Select(f => Path.GetRelativePath(sample.Folder, f).Replace('\\', '/'))
                    .Where(f => !f.EndsWith("sample.json") && !f.EndsWith(".fireproj")).OrderBy(f => f).ToList();

            server.AddTool("list_sample_files", "The files of a sample program (read one with read_sample_file).",
                "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\",\"description\":\"the name of the sample (list_samples)\"}},\"required\":[\"name\"]}",
                args => System.Threading.Tasks.Task.Run(() =>
                {
                    string name = Str(args, "name") ?? "";
                    var sample = Find(name);
                    if (sample == null) return McpToolResult.Error($"There is no sample '{name}' (list_samples names them).");
                    var files = FilesOf(sample);
                    return McpToolResult.Ok($"{sample.Name} ({sample.Title}): " + string.Join(", ", files));
                }));

            server.AddTool("read_sample_file", "One file of a sample program.",
                "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\",\"description\":\"the name of the sample\"}," +
                "\"file\":{\"type\":\"string\",\"description\":\"the file (list_sample_files); empty: its main file\"}},\"required\":[\"name\"]}",
                args => System.Threading.Tasks.Task.Run(() =>
                {
                    string name = Str(args, "name") ?? "";
                    var sample = Find(name);
                    if (sample == null) return McpToolResult.Error($"There is no sample '{name}' (list_samples names them).");
                    var files = FilesOf(sample);
                    string? file = Str(args, "file");
                    if (string.IsNullOrWhiteSpace(file)) file = sample.Open.FirstOrDefault(f => f.EndsWith(".fire")) ?? files.FirstOrDefault(f => f.EndsWith(".fire"));
                    if (file == null) return McpToolResult.Error($"The sample {sample.Name} has no fire file. Its files: {string.Join(", ", files)}");
                    string full = Path.GetFullPath(Path.Combine(sample.Folder, file));
                    if (!full.StartsWith(Path.GetFullPath(sample.Folder), StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
                        return McpToolResult.Error($"The sample {sample.Name} has no file '{file}'. Its files: {string.Join(", ", files)}");
                    string text = File.ReadAllText(full);
                    const int limit = 4000;
                    if (text.Length > limit) text = text.Substring(0, limit) + "\n... (the file goes on)";
                    return McpToolResult.Ok(text);
                }));
        }
    }
}
