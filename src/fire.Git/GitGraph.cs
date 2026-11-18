namespace fire.Git
{
    /// <summary>A line of the commit graph from the middle of one row to the middle of the next: from lane `From` (this row) to lane `To` (the next row), in the color of `Color`.</summary>
    public readonly record struct GitGraphEdge(int From, int To, int Color);

    /// <summary>
    /// One row of the commit graph: the lane (column) of the commit's dot, the lines that come in from the row above (<see cref="Up"/>, the same as the row above's <see cref="Down"/>) and the
    /// lines that go on to the row below (<see cref="Down"/>). A row draws the lower half of the lines above it and the upper half of its own. `Width`: the lanes the row needs.
    /// </summary>
    public sealed class GitGraphRow
    {
        public required string Sha { get; init; }
        public int Lane { get; internal set; }
        public int Color { get; internal set; }
        public IReadOnlyList<GitGraphEdge> Up { get; internal set; } = Array.Empty<GitGraphEdge>();
        public IReadOnlyList<GitGraphEdge> Down { get; internal set; } = Array.Empty<GitGraphEdge>();
        /// <summary>A merge (two parents or more): the dot is drawn as a ring.</summary>
        public bool IsMerge { get; init; }
        public int Width => Math.Max(Lane + 1, Math.Max(Up.Count == 0 ? 0 : Up.Max(e => Math.Max(e.From, e.To)) + 1, Down.Count == 0 ? 0 : Down.Max(e => Math.Max(e.From, e.To)) + 1));
    }

    /// <summary>
    /// The lanes of a commit graph (the Repository view, docs/tools/git.md), laid out row by row: the commits come newest first (each one before its parents, as a topological walk gives them), in
    /// pages - <see cref="Add"/> can be called again with the next page and goes on where it stopped (the last row of the page before gets its lines to the first new one). Each lane waits for
    /// one commit (the parent that the line in it leads to); a commit takes the first lane that waits for it - other lanes that wait for it end in it (a branch that was merged), its first parent
    /// goes on in its lane, further parents (a merge) get a lane of their own (or join the lane that waits for them already). A lane that is free again is used by the next new line.
    /// </summary>
    public sealed class GitGraphLayout
    {
        private readonly List<string?> _lanes = new();
        private readonly List<int> _colors = new();
        private readonly List<GitGraphRow> _rows = new();
        private int _nextColor;

        public IReadOnlyList<GitGraphRow> Rows => _rows;

        /// <summary>The number of colors the lines cycle through (a renderer has a brush for each).</summary>
        public const int Colors = 8;

        private int FreeLane(int except = -1)
        {
            for (int i = 0; i < _lanes.Count; i++) if (_lanes[i] == null && i != except) return i;
            _lanes.Add(null); _colors.Add(0);
            return _lanes.Count - 1;
        }

        /// <summary>Lays out the next commits (sha and the shas of the parents, first parent first) and returns their rows. The row before them (of an earlier call) can get new lines too.</summary>
        public IReadOnlyList<GitGraphRow> Add(IEnumerable<(string Sha, IReadOnlyList<string> Parents)> commits)
        {
            int first = _rows.Count;
            foreach (var (sha, parents) in commits)
            {
                var before = _lanes.ToArray();
                int lane = -1;
                var ending = new List<int>();
                for (int i = 0; i < _lanes.Count; i++)
                    if (_lanes[i] == sha) { if (lane < 0) lane = i; else ending.Add(i); }
                int color;
                if (lane < 0) { lane = FreeLane(); color = _nextColor++ % Colors; }   // a commit that no line waits for: a branch tip
                else color = _colors[lane];
                foreach (int i in ending) _lanes[i] = null;
                _lanes[lane] = null;

                var down = new List<GitGraphEdge>();
                if (parents.Count > 0)
                {
                    _lanes[lane] = parents[0];
                    _colors[lane] = color;
                }
                var mergeTargets = new List<int>();
                for (int p = 1; p < parents.Count; p++)
                {
                    int existing = _lanes.IndexOf(parents[p]);
                    if (existing >= 0) { mergeTargets.Add(existing); continue; }
                    int free = FreeLane(except: lane);
                    _lanes[free] = parents[p];
                    _colors[free] = _nextColor++ % Colors;
                    mergeTargets.Add(free);
                }
                // the lines to the next row: lanes that go on (also the ones that waited for this commit and end here go nowhere), the first parent from the dot, the other parents from the dot
                for (int i = 0; i < _lanes.Count; i++)
                {
                    if (_lanes[i] == null || i == lane) continue;
                    bool passing = i < before.Length && before[i] == _lanes[i] && before[i] != sha;
                    if (passing) down.Add(new GitGraphEdge(i, i, _colors[i]));
                }
                if (parents.Count > 0) down.Add(new GitGraphEdge(lane, lane, color));
                foreach (int t in mergeTargets) down.Add(new GitGraphEdge(lane, t, _colors[t]));
                while (_lanes.Count > 0 && _lanes[^1] == null) { _lanes.RemoveAt(_lanes.Count - 1); _colors.RemoveAt(_colors.Count - 1); }

                var row = new GitGraphRow { Sha = sha, Lane = lane, Color = color, Down = down, IsMerge = parents.Count > 1 };
                if (_rows.Count > 0)
                {
                    // the lines of the row above end in this one: a line that waited for this commit ends at its dot, not in the lane it was in
                    var above = _rows[^1];
                    var fixedDown = above.Down.Select(e => before.Length > e.To && before[e.To] == sha ? e with { To = lane } : e).ToList();
                    above.Down = fixedDown;
                    row.Up = fixedDown;
                }
                _rows.Add(row);
            }
            return _rows.Skip(Math.Max(0, first - 1)).ToList();
        }
    }
}
