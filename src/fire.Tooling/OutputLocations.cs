using System.Text.RegularExpressions;

namespace fire.Compiler
{
    /// <summary>
    /// The lines of a stack trace (docs/spec/STACKTRACE.md section 2) in what a program writes: `   at Store.Find (main.fire:16)` names a place (the file relative to
    /// the folder of the program's first source) that spark's output panel and test explorer make clickable; a frame of the prelude (`(prelude)`) or one
    /// without a line (`#stacktrace calls`) names none. `forge test` leaves out the frames of its driver and of `Testing.Assert`: a failure starts at the line of
    /// the `Assert` in the test.
    /// </summary>
    public static class OutputLocations
    {
        private static readonly Regex Frame = new(@"^\s+at (?<fn>.+) \((?<file>[^():]+):(?<line>\d+)\)\s*$", RegexOptions.Compiled);
        private static readonly Regex AnyFrame = new(@"^\s+at (?<fn>.+) \((?<where>[^()]*)\)\s*$", RegexOptions.Compiled);

        /// <summary>The place a line of a trace names (file as written, line), null: none (not a frame, the prelude, no line).</summary>
        public static (string File, int Line)? TraceFrame(string line)
        {
            var m = Frame.Match(line);
            return m.Success && int.TryParse(m.Groups["line"].Value, out int n) && n > 0 ? (m.Groups["file"].Value, n) : null;
        }

        /// <summary>A trace as `forge test` shows it under a failed test: without the frames of `Testing.Assert` before the test (the innermost ones) and without
        /// the frames of the driver after it (`__Tests.*` and the driver's top level in the prelude); the other lines stay.</summary>
        public static string WithoutTestDriver(string trace)
        {
            var lines = trace.Replace("\r", "").Split('\n').ToList();
            bool trailingFeed = trace.EndsWith('\n');
            if (trailingFeed) lines.RemoveAt(lines.Count - 1);
            // the innermost frames of the assertion
            while (lines.Count > 0 && AnyFrame.Match(lines[0]) is { Success: true } m && IsAssert(m.Groups["fn"].Value)) lines.RemoveAt(0);
            // from the first frame of the driver on (the test's caller)
            int cut = lines.FindIndex(l => AnyFrame.Match(l) is { Success: true } m && (m.Groups["fn"].Value.StartsWith("__Tests.", StringComparison.Ordinal)
                || m.Groups["where"].Value == "prelude" && m.Groups["fn"].Value == "<main>"));
            if (cut >= 0) lines.RemoveRange(cut, lines.Count - cut);
            string result = string.Join("\n", lines);
            return lines.Count > 0 && trailingFeed ? result + "\n" : result;
        }

        private static bool IsAssert(string fn) => fn.StartsWith("Assert.", StringComparison.Ordinal) || fn.StartsWith("Testing.", StringComparison.Ordinal) || fn == "Assertion.construct";
    }
}
