namespace fire.Projects
{
    /// <summary>What the filter box of the solution explorer (docs/tools/spark.md, "The tool bar of the solution explorer") leaves of a tree: the rows whose name matches, their parents
    /// (opened, so that the matches can be seen) and what lies below a match (closed, as it was). Independent of the interface: a row is anything with children and a name.</summary>
    public sealed class ExplorerFilterResult<T> where T : class
    {
        /// <summary>The rows that stay visible.</summary>
        public HashSet<T> Shown { get; } = new(ReferenceEqualityComparer.Instance);
        /// <summary>The rows that are opened: those with a match below them.</summary>
        public HashSet<T> Expanded { get; } = new(ReferenceEqualityComparer.Instance);
        /// <summary>The rows whose own name matches, in the order of the tree (depth first).</summary>
        public List<T> Matches { get; } = new();
    }

    public static class ExplorerTree
    {
        /// <summary>Whether `name` matches the text of the filter box: every word of it occurs in the name, without regard to case ("main fire" finds "main.fire"). An empty query matches all.</summary>
        public static bool Matches(string name, string? query)
        {
            if (string.IsNullOrWhiteSpace(query)) return true;
            foreach (var word in query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                if (!name.Contains(word, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        /// <summary>Filters the tree by name (<see cref="Matches"/>); null for an empty query (everything stays as it is). A row whose `name` is empty never matches by itself (the
        /// solution row: else every letter of "Solution" would show the whole tree).</summary>
        public static ExplorerFilterResult<T>? Filter<T>(IEnumerable<T> roots, Func<T, IEnumerable<T>> children, Func<T, string> name, string? query) where T : class
        {
            if (string.IsNullOrWhiteSpace(query)) return null;
            var result = new ExplorerFilterResult<T>();
            void ShowAll(T node)
            {
                result.Shown.Add(node);
                foreach (var c in children(node)) ShowAll(c);
            }
            // true: the row stays (it matches or something below it does)
            bool Visit(T node)
            {
                string text = name(node);
                if (text.Length > 0 && Matches(text, query))
                {
                    result.Matches.Add(node);
                    result.Shown.Add(node);
                    // below a match everything stays; a match further down opens its parents there too
                    bool below = false;
                    foreach (var c in children(node)) below |= Visit(c);
                    ShowAll(node);
                    if (below) result.Expanded.Add(node);
                    return true;
                }
                bool any = false;
                foreach (var c in children(node)) any |= Visit(c);
                if (any) { result.Shown.Add(node); result.Expanded.Add(node); }
                return any;
            }
            foreach (var r in roots) Visit(r);
            return result;
        }

        /// <summary>The rows from a root down to the first row (depth first) that `isTarget` accepts, both included; null when there is none. What "Sync with Active Document" opens.</summary>
        public static List<T>? PathTo<T>(IEnumerable<T> roots, Func<T, IEnumerable<T>> children, Func<T, bool> isTarget) where T : class
        {
            var path = new List<T>();
            bool Visit(T node)
            {
                path.Add(node);
                if (isTarget(node)) return true;
                foreach (var c in children(node)) if (Visit(c)) return true;
                path.RemoveAt(path.Count - 1);
                return false;
            }
            foreach (var r in roots) if (Visit(r)) return path;
            return null;
        }
    }
}
