using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using fire.Native;

namespace fire.Editor
{
    /// <summary>
    /// The log of one build as spark keeps it (no user interface - tested headless): the build's threads hand in the lines of its monitor (<see cref="BuildMonitor"/>: the
    /// commands, the output of the compilers, the errors) and its phase changes; <see cref="Drain"/>, on the window's thread, hands every new line to the build log panel
    /// (where it stays after the build window is gone) and returns them for the build window, which only mirrors the panel. <see cref="Lines"/> keeps them all (a window
    /// that appears late shows the log so far).
    /// </summary>
    public sealed class BuildLogFeed
    {
        private readonly ConcurrentQueue<string> _pending = new();
        private readonly List<string> _lines = new();
        private readonly Action<string> _sink;
        private BuildPhase? _phase;
        private readonly object _phaseGate = new();

        /// <param name="sink">Where every line goes, in order, as it is drained (spark: the build log panel).</param>
        public BuildLogFeed(Action<string> sink) => _sink = sink ?? throw new ArgumentNullException(nameof(sink));

        /// <summary>Every line so far (the drained ones).</summary>
        public IReadOnlyList<string> Lines => _lines;

        /// <summary>A line of the build's log (from any thread).</summary>
        public void Line(string text) => _pending.Enqueue(text);

        /// <summary>The progress of the build (from any thread): a new phase is a line of the log ("--- Compiling the C code ---"); the counts within a phase are not.</summary>
        public void Progress(BuildProgress progress)
        {
            lock (_phaseGate)
            {
                if (_phase == progress.Phase) return;
                _phase = progress.Phase;
            }
            _pending.Enqueue($"--- {progress.Text} ---");
        }

        /// <summary>The lines of a build's whole log (its result, written at the end) that have not come through this feed already - so that the build log has each
        /// line once (the same line twice in the log counts twice).</summary>
        public List<string> Missing(string log)
        {
            if (string.IsNullOrWhiteSpace(log)) return new List<string>();
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string line in _lines) { string key = line.TrimEnd(); seen[key] = seen.TryGetValue(key, out int n) ? n + 1 : 1; }
            var missing = new List<string>();
            foreach (string raw in log.Replace("\r\n", "\n").TrimEnd('\n').Split('\n'))
            {
                string key = raw.TrimEnd();
                if (seen.TryGetValue(key, out int n) && n > 0) { seen[key] = n - 1; continue; }
                missing.Add(raw.TrimEnd('\r'));
            }
            return missing;
        }

        /// <summary>On the window's thread: the lines that came since the last call - each written to the sink first (in order), then returned.</summary>
        public List<string> Drain()
        {
            var lines = new List<string>();
            while (_pending.TryDequeue(out var line)) lines.Add(line);
            foreach (string line in lines) _sink(line);
            _lines.AddRange(lines);
            return lines;
        }
    }
}
