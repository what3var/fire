using System.Diagnostics;

namespace fire.Native
{
    /// <summary>
    /// `FIRE_BUILD_TIMES=1`: the time of each phase of a build on the error output, `[build-times] phase: N ms` (the fire front end - preprocessing, parsing, names, types,
    /// bytecode -, the rounds of the C generator, putting the C together, writing the files, the C compiler and the linker), and what came out of it (lines of C, functions).
    /// A lap is the time since the previous one on the same thread (a build runs its phases on one thread; the parallel compiles of linkage "static" report their own times).
    /// Without the variable nothing is measured or printed.
    /// </summary>
    public static class BuildTimes
    {
        public static readonly bool Enabled = Environment.GetEnvironmentVariable("FIRE_BUILD_TIMES") is { Length: > 0 } v && v != "0";

        [ThreadStatic] private static long t_last;

        /// <summary>Starts the laps of this thread (the beginning of a build).</summary>
        public static void Begin()
        {
            if (Enabled) t_last = Stopwatch.GetTimestamp();
        }

        /// <summary>The phase that just ended: its time since the previous lap (or <see cref="Begin"/>).</summary>
        public static void Lap(string phase)
        {
            if (!Enabled) return;
            long now = Stopwatch.GetTimestamp();
            if (t_last != 0) Print($"{phase}: {Stopwatch.GetElapsedTime(t_last, now).TotalMilliseconds:F0} ms");
            t_last = Stopwatch.GetTimestamp();
        }

        /// <summary>A line of its own (a size, a time measured elsewhere).</summary>
        public static void Note(string text)
        {
            if (Enabled) Print(text);
        }

        private static void Print(string text)
        {
            lock (typeof(BuildTimes)) Console.Error.WriteLine("[build-times] " + text);
        }
    }
}
