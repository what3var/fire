using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace fire.Native
{
    /// <summary>The phases of a build, in the order they come (docs/NATIVE_BACKEND.md "Fortschritt und Abbrechen"): a phase that a build does not have is left out.</summary>
    public enum BuildPhase
    {
        /// <summary>The fire compiler: parsing, names, types, bytecode (the VM's program is ready after it).</summary>
        CompileFire,
        /// <summary>The natives of the packages a program in the VM uses (PackageLibrary: the wrapper and the package's C/C++ as a shared library), one step per package that is not
        /// built yet - before the program runs, or while a library is packed.</summary>
        PackageNatives,
        /// <summary>The C (and the natives in C++) of the program and of its library modules.</summary>
        GenerateC,
        /// <summary>The C compiler: with linkage "static"/"shared" one step per library module that is not in the cache (side by side), with linkage "source" the program.</summary>
        CompileC,
        /// <summary>The program is linked (with linkage "source" compiling and linking are one call).</summary>
        Link,
        /// <summary>The build system of a board (idf.py: its own compile steps), then flashing.</summary>
        Flash,
        /// <summary>A library becomes a package (`.fpk`).</summary>
        Pack,
    }

    /// <summary>Where a build is: its phase, what it does, and - when the phase has steps - how many of them are done (<see cref="Total"/> 0: no count, the progress is indeterminate).</summary>
    public readonly record struct BuildProgress(BuildPhase Phase, string Text, int Done = 0, int Total = 0)
    {
        public bool Indeterminate => Total <= 0;
    }

    /// <summary>
    /// What watches a build (spark's build window, a test): its progress (<see cref="Report"/>, in order, from the thread that does the step - the parallel library builds report
    /// from their threads, one after the other), every command and its output as it happens (<see cref="Log"/>), and the request to stop (<see cref="Token"/>): a compiler that
    /// runs is ended with its whole process tree, the in-process parts stop at their next checkpoint with <see cref="OperationCanceledException"/>.
    /// The builders find the monitor of the build they run in as <see cref="Current"/> (an ambient value that follows the build into its parallel steps), so that the deep
    /// helpers (the process runner, the linker's phases) need no extra parameter.
    /// </summary>
    public sealed class BuildMonitor : IProgress<BuildProgress>
    {
        private static readonly AsyncLocal<BuildMonitor?> s_current = new();
        private readonly Action<BuildProgress>? _progress;
        private readonly Action<string>? _log;
        private readonly object _lock = new();
        private int _done;

        public BuildMonitor(CancellationToken token = default, Action<BuildProgress>? progress = null, Action<string>? log = null)
        {
            Token = token;
            _progress = progress;
            _log = log;
        }

        public CancellationToken Token { get; }

        /// <summary>The last progress reported (null: none yet).</summary>
        public BuildProgress? Last { get; private set; }

        /// <summary>The monitor of the build that runs on this flow (null: nobody watches).</summary>
        public static BuildMonitor? Current => s_current.Value;

        /// <summary>Makes <paramref name="monitor"/> the current one until the result is disposed (null: leaves the current one as it is - a build inside a build keeps the outer monitor).</summary>
        public static IDisposable Use(BuildMonitor? monitor)
        {
            if (monitor == null || ReferenceEquals(monitor, s_current.Value)) return NoScope.Instance;
            var before = s_current.Value;
            s_current.Value = monitor;
            return new Scope(before);
        }

        /// <summary>The current build was asked to stop: <see cref="OperationCanceledException"/>. Nothing happens without a monitor.</summary>
        public static void Checkpoint() => s_current.Value?.Token.ThrowIfCancellationRequested();

        /// <summary>Reports a phase of the current build (and checks whether it should stop). Nothing happens without a monitor.</summary>
        public static void Step(BuildPhase phase, string text, int done = 0, int total = 0)
        {
            var monitor = s_current.Value;
            if (monitor == null) return;
            monitor.Token.ThrowIfCancellationRequested();
            monitor.Report(new BuildProgress(phase, text, done, total));
        }

        /// <summary>A line for the log of the current build (a command, what it said). Nothing happens without a monitor.</summary>
        public static void LogLine(string text) => s_current.Value?.Line(text);

        public bool IsCancellationRequested => Token.IsCancellationRequested;

        public void Report(BuildProgress value)
        {
            lock (_lock)
            {
                Last = value;
                _progress?.Invoke(value);
            }
        }

        public void Line(string text)
        {
            if (_log == null) return;
            lock (_lock) _log(text.TrimEnd('\r', '\n'));
        }

        /// <summary>Steps that run side by side (the library modules): <see cref="BeginCount"/> reports 0 of <paramref name="total"/>, every <see cref="Count"/> one more, in order.</summary>
        public void BeginCount(BuildPhase phase, string text, int total)
        {
            lock (_lock)
            {
                _done = 0;
                Report(new BuildProgress(phase, text, 0, total));
            }
        }

        public void Count(BuildPhase phase, int total, Func<int, string> text)
        {
            lock (_lock)
            {
                _done++;
                int done = Math.Min(_done, total);
                Report(new BuildProgress(phase, text(done), done, total));
            }
        }

        public int Counted { get { lock (_lock) return _done; } }

        private sealed class Scope(BuildMonitor? before) : IDisposable
        {
            private bool _disposed;
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                s_current.Value = before;
            }
        }

        private sealed class NoScope : IDisposable
        {
            public static readonly NoScope Instance = new();
            public void Dispose() { }
        }
    }

    /// <summary>
    /// The tools of a build as processes that can be stopped: <see cref="Run"/> runs one (its output line by line to a callback), and when the build is cancelled the whole
    /// tree of processes it started (gcc with cc1, as, collect2, ld; idf.py with cmake, ninja and the compilers) ends: first asked to (SIGTERM - gcc and ninja then delete the
    /// output they were writing), after a short grace killed.
    /// </summary>
    public static class BuildProcess
    {
        /// <summary>How long a tool may take to end after it was asked to.</summary>
        public static TimeSpan Grace { get; set; } = TimeSpan.FromSeconds(2);

        /// <summary>Runs the process of <paramref name="psi"/> (its output is redirected here) to its end. Every line of its output goes to <paramref name="line"/> as it comes
        /// (stdout and stderr each in order). A cancelled <paramref name="token"/> ends the process tree and throws <see cref="OperationCanceledException"/> once it is gone.
        /// Throws what Process.Start throws.</summary>
        public static (int ExitCode, string Output, string Error) Run(ProcessStartInfo psi, Action<string>? line, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.UseShellExecute = false;
            var output = new StringBuilder();
            var error = new StringBuilder();
            using var process = new Process { StartInfo = psi };
            process.OutputDataReceived += (_, e) => { if (e.Data != null) { lock (output) output.Append(e.Data).Append('\n'); line?.Invoke(e.Data); } };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) { lock (error) error.Append(e.Data).Append('\n'); line?.Invoke(e.Data); } };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            Task? ending = null;
            using (token.Register(() => ending = Task.Run(() => Terminate(process, Grace))))
                process.WaitForExit();   // (without a time-out: also waits for the end of the output)
            if (token.IsCancellationRequested)
            {
                try { ending?.Wait(Grace + TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
                throw new OperationCanceledException(token);
            }
            string o, e2;
            lock (output) o = output.ToString();
            lock (error) e2 = error.ToString();
            return (process.ExitCode, o, e2);
        }

        /// <summary>Ends a process with everything it started: SIGTERM to the whole tree (Linux, macOS), then - after <paramref name="grace"/> - kills what is left, also the
        /// children that outlived their parent. On Windows the tree is killed at once.</summary>
        public static void Terminate(Process process, TimeSpan grace)
        {
            try { if (process.HasExited) return; }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { return; }
            int root;
            try { root = process.Id; }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { return; }
            var tree = OperatingSystem.IsLinux() ? Descendants(root) : new List<(int Pid, string Start)>();
            if (!OperatingSystem.IsWindows())
            {
                Signal(root, SigTerm);
                foreach (var (pid, _) in tree) Signal(pid, SigTerm);
                var until = DateTime.UtcNow + grace;
                bool exited = false;
                try { exited = process.WaitForExit((int)Math.Max(1, grace.TotalMilliseconds)); }
                catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or SystemException) { exited = true; }
                while (exited && tree.Any(t => IsAlive(t.Pid, t.Start)) && DateTime.UtcNow < until) Thread.Sleep(50);
            }
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or ObjectDisposedException) { }
            foreach (var (pid, start) in tree)
                if (IsAlive(pid, start)) Signal(pid, SigKill);
        }

        private const int SigTerm = 15, SigKill = 9;

        [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
        private static extern int SysKill(int pid, int signal);

        private static void Signal(int pid, int signal)
        {
            try { SysKill(pid, signal); }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException) { }
        }

        /// <summary>Linux: the processes below <paramref name="root"/> (from /proc), each with its start time (a pid that is used again later is not the same process).</summary>
        private static List<(int Pid, string Start)> Descendants(int root)
        {
            var children = new Dictionary<int, List<(int, string)>>();
            try
            {
                foreach (var dir in Directory.EnumerateDirectories("/proc"))
                {
                    if (!int.TryParse(Path.GetFileName(dir), out int pid)) continue;
                    if (ReadStat(pid) is not { } stat) continue;
                    if (!children.TryGetValue(stat.Parent, out var list)) children[stat.Parent] = list = new();
                    list.Add((pid, stat.Start));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            var result = new List<(int, string)>();
            var queue = new Queue<int>();
            queue.Enqueue(root);
            while (queue.Count > 0)
                if (children.TryGetValue(queue.Dequeue(), out var list))
                    foreach (var child in list)
                    {
                        result.Add(child);
                        queue.Enqueue(child.Item1);
                    }
            return result;
        }

        /// <summary>The parent and the start time of a process (fields 4 and 22 of /proc/PID/stat; the name in parentheses may contain spaces).</summary>
        private static (int Parent, string Start)? ReadStat(int pid)
        {
            try
            {
                string text = File.ReadAllText($"/proc/{pid}/stat");
                int close = text.LastIndexOf(')');
                if (close < 0) return null;
                var fields = text.Substring(close + 2).Split(' ');
                // (after the name: state, ppid, ..., starttime is field 22 of the whole line = index 19 here)
                return fields.Length > 19 && int.TryParse(fields[1], out int parent) ? (parent, fields[19]) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }

        private static bool IsAlive(int pid, string start)
        {
            if (!OperatingSystem.IsLinux()) return false;
            if (ReadStat(pid) is not { } stat || stat.Start != start) return false;
            try { return !File.ReadAllText($"/proc/{pid}/stat").Contains(") Z ", StringComparison.Ordinal); }   // (a zombie is gone, only not reaped yet)
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }
    }
}
