using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace fire.Editor
{
    /// <summary>The result of a profiled run (spark's Profile switch, fire.Runtime.LineProfile): for each own source file every line with code - how often a thread
    /// came into it and how many instructions of the VM ran in it; a line with code that never ran is "fallow". Kept as JSON in the Profile tab.</summary>
    public sealed record ProfileReport(string What, double Seconds, long Instructions, IReadOnlyList<ProfileFile> Files)
    {
        public string ToJson() => JsonSerializer.Serialize(this);
        public static ProfileReport? FromJson(string json)
        {
            try { return JsonSerializer.Deserialize<ProfileReport>(json); }
            catch (JsonException) { return null; }
        }

        /// <summary>The report of `counts` (line -> hits, instructions) over the `executable` lines, for the sources from `firstUser` on (the program's own; the
        /// libraries before them are left out); `source(i)` gives the path and the text of source i (null: it has none to show).</summary>
        public static ProfileReport Build(string what, double seconds, IReadOnlyDictionary<(int SourceIndex, int Line), (long Hits, long Instructions)> counts,
            ISet<(int SourceIndex, int Line)> executable, int firstUser, Func<int, (string? Path, string? Text)> source)
        {
            var files = new List<ProfileFile>();
            foreach (var group in executable.Concat(counts.Keys).Where(l => l.SourceIndex >= firstUser && l.Line > 0).Distinct().GroupBy(l => l.SourceIndex).OrderBy(g => g.Key))
            {
                var (path, text) = source(group.Key);
                var lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
                var rows = group.OrderBy(l => l.Line).Select(l =>
                {
                    var (hits, instructions) = counts.TryGetValue(l, out var c) ? c : (0, 0);
                    return new ProfileLine(l.Line, l.Line - 1 < lines.Length ? lines[l.Line - 1] : "", hits, instructions, executable.Contains(l));
                }).ToList();
                files.Add(new ProfileFile(group.Key, path, rows));
            }
            return new ProfileReport(what, seconds, files.Sum(f => f.Lines.Sum(l => l.Instructions)), files);
        }
    }

    public sealed record ProfileFile(int SourceIndex, string? Path, IReadOnlyList<ProfileLine> Lines);

    /// <summary>A line with code: entered `Hits` times, `Instructions` instructions ran in it; it ran when either is not 0.</summary>
    public sealed record ProfileLine(int Line, string Text, long Hits, long Instructions, bool Executable)
    {
        public bool Ran => Hits > 0 || Instructions > 0;
    }
}
