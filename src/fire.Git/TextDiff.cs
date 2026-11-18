using System.Text;

namespace fire.Git
{
    /// <summary>
    /// The differences of two texts as a unified diff (the form the diff view of the editor shows): for a file that was changed on disk while the editor has other changes of it. The lines are
    /// compared with the algorithm of Myers (the shortest edit script); line ends do not count (\r\n and \n are the same).
    /// </summary>
    public static class TextDiff
    {
        private enum Op { Same, Delete, Insert }

        public static string Unified(string oldText, string newText, string oldName = "a", string newName = "b", int context = 3)
        {
            var a = Lines(oldText);
            var b = Lines(newText);
            var ops = Script(a, b);
            if (ops.All(o => o.Op == Op.Same)) return "";

            var sb = new StringBuilder();
            sb.Append("--- ").Append(oldName).Append('\n');
            sb.Append("+++ ").Append(newName).Append('\n');
            // the hunks: runs of changes with up to `context` same lines around them (two runs closer than 2 * context are one hunk)
            int i = 0;
            while (i < ops.Count)
            {
                if (ops[i].Op == Op.Same) { i++; continue; }
                int start = Math.Max(0, i - context);
                int end = i;   // exclusive
                int lastChange = i;
                while (end < ops.Count)
                {
                    if (ops[end].Op != Op.Same) lastChange = end;
                    else if (end - lastChange > 2 * context) break;
                    end++;
                }
                end = Math.Min(ops.Count, lastChange + 1 + context);
                int oldStart = ops[start].OldLine, newStart = ops[start].NewLine;
                int oldCount = 0, newCount = 0;
                for (int k = start; k < end; k++)
                {
                    if (ops[k].Op != Op.Insert) oldCount++;
                    if (ops[k].Op != Op.Delete) newCount++;
                }
                sb.Append($"@@ -{(oldCount == 0 ? oldStart : oldStart + 1)},{oldCount} +{(newCount == 0 ? newStart : newStart + 1)},{newCount} @@\n");
                for (int k = start; k < end; k++)
                {
                    var o = ops[k];
                    sb.Append(o.Op switch { Op.Same => ' ', Op.Delete => '-', _ => '+' }).Append(o.Op == Op.Insert ? b[o.NewLine] : a[o.OldLine]).Append('\n');
                }
                i = end;
            }
            return sb.ToString();
        }

        private static string[] Lines(string text)
        {
            string t = text.Replace("\r\n", "\n");
            if (t.Length == 0) return Array.Empty<string>();
            if (t.EndsWith('\n')) t = t.Substring(0, t.Length - 1);
            return t.Split('\n');
        }

        private readonly record struct Edit(Op Op, int OldLine, int NewLine);

        /// <summary>The edit script from `a` to `b` (each entry with the line in `a` and in `b` it stands at).</summary>
        private static List<Edit> Script(string[] a, string[] b)
        {
            // the same lines at the start and at the end are taken off first (most changes are small)
            int prefix = 0;
            while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix]) prefix++;
            int suffix = 0;
            while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[a.Length - 1 - suffix] == b[b.Length - 1 - suffix]) suffix++;

            var result = new List<Edit>();
            for (int k = 0; k < prefix; k++) result.Add(new Edit(Op.Same, k, k));
            int n = a.Length - prefix - suffix, m = b.Length - prefix - suffix;
            var middle = n == 0 || m == 0 ? null : Myers(a, b, prefix, n, m);
            if (middle == null)
            {
                // nothing in common in the middle (or too different to compare): all of the old out, all of the new in
                for (int k = 0; k < n; k++) result.Add(new Edit(Op.Delete, prefix + k, prefix));
                for (int k = 0; k < m; k++) result.Add(new Edit(Op.Insert, prefix + n, prefix + k));
            }
            else result.AddRange(middle);
            for (int k = 0; k < suffix; k++) result.Add(new Edit(Op.Same, prefix + n + k, prefix + m + k));
            return result;
        }

        /// <summary>null: the texts are too different to compare in a reasonable memory (the trace is kept for every step).</summary>
        private static List<Edit>? Myers(string[] a, string[] b, int offset, int n, int m)
        {
            int max = n + m;
            var v = new int[2 * max + 2];
            var trace = new List<int[]>();
            int found = -1;
            for (int d = 0; d <= max && found < 0; d++)
            {
                if ((long)(d + 1) * v.Length > 30_000_000L) return null;
                trace.Add((int[])v.Clone());
                for (int k = -d; k <= d; k += 2)
                {
                    int x = k == -d || (k != d && v[max + k - 1] < v[max + k + 1]) ? v[max + k + 1] : v[max + k - 1] + 1;
                    int y = x - k;
                    while (x < n && y < m && a[offset + x] == b[offset + y]) { x++; y++; }
                    v[max + k] = x;
                    if (x >= n && y >= m) { found = d; break; }
                }
            }
            // back along the trace: from the end to the start
            var edits = new List<Edit>();
            int cx = n, cy = m;
            for (int d = found; d > 0; d--)
            {
                var pv = trace[d];
                int k = cx - cy;
                int prevK = k == -d || (k != d && pv[max + k - 1] < pv[max + k + 1]) ? k + 1 : k - 1;
                int px = pv[max + prevK], py = px - prevK;
                while (cx > px && cy > py) { cx--; cy--; edits.Add(new Edit(Op.Same, offset + cx, offset + cy)); }
                if (cx == px) { cy--; edits.Add(new Edit(Op.Insert, offset + cx, offset + cy)); }
                else { cx--; edits.Add(new Edit(Op.Delete, offset + cx, offset + cy)); }
            }
            while (cx > 0 && cy > 0) { cx--; cy--; edits.Add(new Edit(Op.Same, offset + cx, offset + cy)); }
            edits.Reverse();
            return edits;
        }
    }
}
