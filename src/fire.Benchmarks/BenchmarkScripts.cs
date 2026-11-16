namespace fire.Benchmarks
{
    /// <summary>A benchmark: a fire program that prints exactly ONE line (its result).
    /// The result serves as a regression check: an optimization must never change it (see
    /// `--check`/`--save` in Program.cs).</summary>
    public sealed record BenchmarkScript(string Name, string Description, string Source);

    public static class BenchmarkScripts
    {
        /// <summary>The benchmarks are files: `tests/bench/&lt;name&gt;.fire` of the repository - the source, then a line `//# name: &lt;description&gt;`. The same files are
        /// built natively and measured by tests/native-baseline.py (docs/ROADMAP_C_PORT.md, "Pruefungen fuer den nativen Build").</summary>
        public static readonly BenchmarkScript[] All = Load();

        private static BenchmarkScript[] Load([System.Runtime.CompilerServices.CallerFilePath] string here = "")
        {
            string folder = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "tests", "bench"));
            // (the order they always had, then whatever is new)
            string[] order = { "loop", "float", "fib", "method", "array", "string", "alloc", "lambda", "list" };
            return Directory.GetFiles(folder, "*.fire")
                .Select(file =>
                {
                    string text = File.ReadAllText(file).Replace("\r\n", "\n");
                    const string tag = "\n//# name: ";
                    int at = text.LastIndexOf(tag, StringComparison.Ordinal);
                    return at < 0 ? new BenchmarkScript(Path.GetFileNameWithoutExtension(file), "", text) : new BenchmarkScript(Path.GetFileNameWithoutExtension(file), text.Substring(at + tag.Length).Trim(), text.Substring(0, at));
                })
                .OrderBy(s => Array.IndexOf(order, s.Name) is var i && i >= 0 ? i : int.MaxValue).ThenBy(s => s.Name, StringComparer.Ordinal)
                .ToArray();
        }
    }
}
