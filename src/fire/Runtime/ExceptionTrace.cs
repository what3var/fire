using System.Collections.Generic;
using System.Text;

namespace fire.Runtime
{
    /// <summary>
    /// The stack an exception was thrown on (docs/spec/STACKTRACE.md section 3): the frames innermost first, taken at its first `throw` and never changed
    /// after (a rethrow, a `finally` and `resume` keep it). It hangs on the object (<see cref="ObjectInstance.Trace"/>), not in a field: reflection, JSON, `==`
    /// and `print` do not see it; a copy of the exception (`taking`, the end of a thread, `Completion.Fail`) shares it.
    ///
    /// The text is the one every engine writes (the VM in C formats with fire_rt.h `fire_traceFormat`, the same rules): a line `   at Function (file:line)` per
    /// frame, `(file)` without lines or with an unknown line, `(prelude)` for the code of the prelude; at most <see cref="MaxFrames"/> frames - with more, the
    /// innermost <see cref="KeepInner"/>, a line `   ... N more frames ...` and the outermost <see cref="KeepOuter"/>. Sections follow with
    /// `   --- awaited at ---` (an exception of a task that the waiting one throws again, at most <see cref="MaxSections"/>); a thread's section ends with
    /// `   in thread 'Fire #1', started at main.fire:40`.
    /// </summary>
    public sealed class ExceptionTrace
    {
        public const int MaxFrames = 64, KeepInner = 48, KeepOuter = 16, MaxSections = 4;

        /// <summary>The file of the frame of a native that calls back into fire (`   at __GRPHWinTick (native)`; fire_rt.h FIRE_TRACE_NATIVE_FILE); `e.Frames`
        /// shows it as the file "(native)", line 0.</summary>
        public const string NativeFile = "\u0001native";

        public static bool IsNative(string? file) => file != null && file.Length > 0 && file[0] == '\u0001';

        /// <summary>One frame: the function (`Store.Find`, `&lt;main&gt;`, `&lt;lambda&gt;`), its file as the trace shows it (relative to the folder of the program's
        /// first source; null: the prelude) and its line (0: unknown).</summary>
        public readonly record struct Frame(string Function, string? File, int Line);

        public IReadOnlyList<Frame> Frames { get; }
        /// <summary>The frames left out in the middle (a stack deeper than <see cref="MaxFrames"/>): they stand between Frames[KeepInner - 1] and Frames[KeepInner].</summary>
        public int Omitted { get; }
        /// <summary>The line that ends this section (a thread: `in thread 'Fire #1', started at main.fire:40`), without the indentation; null: none.</summary>
        public string? Trailer { get; }
        /// <summary>The section behind this one: where the exception of a task was awaited (`--- awaited at ---`); null: none.</summary>
        public ExceptionTrace? Next { get; }

        public ExceptionTrace(IReadOnlyList<Frame> frames, int omitted = 0, string? trailer = null, ExceptionTrace? next = null)
        {
            Frames = frames; Omitted = omitted; Trailer = trailer; Next = next;
        }

        /// <summary>The trace of a stack of <paramref name="total"/> frames, innermost first (<paramref name="frameAt"/>(0) the throw site), limited as above.</summary>
        public static ExceptionTrace Of(int total, System.Func<int, Frame> frameAt, string? trailer = null)
        {
            var frames = new List<Frame>(System.Math.Min(total, MaxFrames));
            if (total <= MaxFrames) { for (int i = 0; i < total; i++) frames.Add(frameAt(i)); return new ExceptionTrace(frames, 0, trailer); }
            for (int i = 0; i < KeepInner; i++) frames.Add(frameAt(i));
            for (int i = total - KeepOuter; i < total; i++) frames.Add(frameAt(i));
            return new ExceptionTrace(frames, total - MaxFrames, trailer);
        }

        /// <summary>This trace with <paramref name="next"/> behind its last section (an exception awaited again: at most <see cref="MaxSections"/> sections; more are
        /// dropped).</summary>
        public ExceptionTrace Append(ExceptionTrace next)
        {
            int depth = 0;
            for (var t = this; t != null; t = t.Next) depth++;
            if (depth >= MaxSections) return this;
            return new ExceptionTrace(Frames, Omitted, Trailer, Next == null ? next : Next.Append(next));
        }

        /// <summary>This section with <paramref name="trailer"/> at its end (the thread it ran in), if it has none yet.</summary>
        public ExceptionTrace WithTrailer(string trailer) => Trailer != null ? this : new ExceptionTrace(Frames, Omitted, trailer, Next);

        /// <summary>The lines of the trace (each ended by a line feed), as `e.StackTrace` and the report of an unhandled exception show them; "" with
        /// <see cref="StackTraceMode.Off"/>.</summary>
        public string Format(StackTraceMode mode)
        {
            if (mode == StackTraceMode.Off) return "";
            var sb = new StringBuilder();
            for (var t = this; t != null; t = t.Next)
            {
                if (t != this) sb.Append("   --- awaited at ---\n");
                for (int i = 0; i < t.Frames.Count; i++)
                {
                    if (t.Omitted > 0 && i == KeepInner) sb.Append("   ... ").Append(t.Omitted).Append(" more frames ...\n");
                    var f = t.Frames[i];
                    sb.Append("   at ").Append(f.Function).Append(f.Line < 0 ? " (entry)" : "").Append(" (").Append(Location(f, mode)).Append(")\n");   // (a negative line: on entry, before the first line - fire_rt.h)
                }
                if (t.Trailer != null) sb.Append("   ").Append(t.Trailer).Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>What stands in the brackets of a frame: `main.fire:16`, `main.fire` (no lines), `prelude`; a frame on entry (a negative line) the first line of its function.</summary>
        public static string Location(Frame f, StackTraceMode mode) =>
            f.File == null ? "prelude" : IsNative(f.File) ? f.File.Substring(1) : mode == StackTraceMode.Lines && f.Line != 0 ? f.File + ":" + System.Math.Abs(f.Line) : f.File;

        /// <summary>The display names of the sources (by source index) for a trace: a source of the program relative to the folder of its first source (with `/`;
        /// `fire:source/N` for a source without a file), a library by the name of its module (its import) and its file name (`lib:util.fire`; without a module
        /// its file name), the prelude, the preludes of the imports and the sources of a test driver null. <paramref name="files"/>: the full path of each
        /// source (LinkedProgram.SourceFiles); the program's are <paramref name="userCount"/> from <paramref name="firstUser"/>; <paramref name="modules"/>: the
        /// module of each source (ProcessedSource.Module; null: none).</summary>
        public static List<string?> DisplayFiles(IReadOnlyList<string?> files, int firstUser, int userCount, IReadOnlyList<string?>? modules = null)
        {
            var names = new List<string?>(files.Count);
            string? baseDir = firstUser >= 0 && firstUser < files.Count && files[firstUser] is { } first ? System.IO.Path.GetDirectoryName(first) : null;
            for (int i = 0; i < files.Count; i++)
            {
                string? file = files[i];
                if (i >= firstUser && i < firstUser + userCount)
                    names.Add(file == null ? "fire:source/" + (i - firstUser) : baseDir != null ? Relative(baseDir, file) : System.IO.Path.GetFileName(file));
                else names.Add(file != null && i < firstUser ? (modules != null && i < modules.Count && modules[i] is { } module ? module + ":" : "") + System.IO.Path.GetFileName(file) : null);
            }
            return names;
        }

        /// <summary>The path of <paramref name="file"/> from the folder <paramref name="dir"/> with `/` (Path.GetRelativePath; the C port: fcRelativePath).</summary>
        public static string Relative(string dir, string file) => System.IO.Path.GetRelativePath(dir, file).Replace('\\', '/');
    }
}
