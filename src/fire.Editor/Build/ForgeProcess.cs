using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using fire.Compiler;
using fire.Native;

namespace fire.Editor
{
    /// <summary>What a run of forge reported (<see cref="ForgeProcess.Run"/>): how it ended, its errors and warnings, what it wrote, the projects of `--all` and publish, its log.</summary>
    public sealed class ForgeOutcome
    {
        public int ExitCode { get; init; }
        /// <summary>The report ended with ok (and forge with exit code 0).</summary>
        public bool Ok { get; init; }
        public bool Cancelled { get; init; }
        /// <summary>forge wrote the end of its report (false: it crashed, was killed or is no forge that reports).</summary>
        public bool Ended { get; init; }
        public IReadOnlyList<Diagnostic> Diagnostics { get; init; } = Array.Empty<Diagnostic>();
        /// <summary>What it wrote: (kind - program, folder, package, publish -, path, project).</summary>
        public IReadOnlyList<(string Kind, string Path, string? Project)> Outputs { get; init; } = Array.Empty<(string, string, string?)>();
        /// <summary>The <c>project</c> events (`--all`, publish).</summary>
        public IReadOnlyList<ForgeEvent> Projects { get; init; } = Array.Empty<ForgeEvent>();
        public string? Summary { get; init; }
        /// <summary>The first error (for the build window), or why forge could not be started.</summary>
        public string? Error { get; init; }
        /// <summary>Every line of its log, in order.</summary>
        public string Log { get; init; } = "";

        /// <summary>The first output of a kind (null: none).</summary>
        public string? Output(params string[] kinds) => Outputs.FirstOrDefault(o => kinds.Contains(o.Kind)).Path;
    }

    /// <summary>Where the report of a running forge goes (any thread: the reader of its output).</summary>
    public sealed class ForgeSink
    {
        public Action<string>? Log { get; init; }
        public Action<BuildProgress>? Progress { get; init; }
        /// <summary>Work without a build window (the standard packages, the natives of a package): null when it is over.</summary>
        public Action<BackgroundWork.Item?>? Background { get; init; }
        public Action<Diagnostic>? Diagnostic { get; init; }
        public Action<Process>? Started { get; init; }
    }

    /// <summary>
    /// spark builds through forge (docs/ROADMAP_C_PORT.md, L4 - Phase A step 5): every build - Build, Build Solution, Build virtual, the build of a native run, Publish, Pack Library -
    /// is <c>forge build|publish ... --report json</c> as a child process, its report (<see cref="ForgeReport"/>) read line by line into the build log, the error list and the
    /// progress. Cancel writes <c>cancel</c> to forge's input (and on Linux and macOS sends SIGTERM): forge ends its tools, deletes what it had half written and its temporary
    /// folders and reports "cancelled"; a forge that has not ended after <see cref="CancelGrace"/> is killed with its process tree.
    /// No user interface here: tested with a fake forge (ForgeReportTests).
    /// </summary>
    public static class ForgeProcess
    {
        /// <summary>A forge of its own (a program, or a .dll run with dotnet): for tests, and to try another compiler.</summary>
        public const string ForgeVariable = "FIRE_FORGE";

        /// <summary>How long forge may take to end after it was asked to.</summary>
        public static TimeSpan CancelGrace { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>The forge that spark runs: <see cref="ForgeVariable"/>, else the one beside spark (the program, else forge.dll with dotnet). Null: none was found.</summary>
        public static (string Program, IReadOnlyList<string> Prefix)? Locate(string? baseDirectory = null)
        {
            static (string, IReadOnlyList<string>)? Of(string path)
            {
                if (!File.Exists(path)) return null;
                if (string.Equals(Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase))
                    return (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host) ? host : "dotnet", new[] { path });
                return (path, Array.Empty<string>());
            }
            if (Environment.GetEnvironmentVariable(ForgeVariable) is { Length: > 0 } own) return Of(Path.GetFullPath(own));
            string dir = baseDirectory ?? AppContext.BaseDirectory;
            return Of(Path.Combine(dir, OperatingSystem.IsWindows() ? "forge.exe" : "forge")) ?? Of(Path.Combine(dir, "forge.dll"));
        }

        /// <summary>
        /// Runs forge with <paramref name="args"/> (`--report json` is added) to its end (blocking: off the UI thread) and hands its report to <paramref name="sink"/> as it comes.
        /// A line that is no object of the report (a forge that crashed, its error output) is a line of the log. <paramref name="token"/> cancels it (see the class).
        /// </summary>
        public static ForgeOutcome Run(IReadOnlyList<string> args, ForgeSink sink, CancellationToken token, string? workingDirectory = null, (string Program, IReadOnlyList<string> Prefix)? forge = null)
        {
            forge ??= Locate();
            if (forge is not { } f) return new ForgeOutcome { ExitCode = -1, Error = $"forge was not found beside spark ({AppContext.BaseDirectory}); set {ForgeVariable} to the forge to use." };
            var info = new ProcessStartInfo(f.Program)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            };
            foreach (string a in f.Prefix) info.ArgumentList.Add(a);
            foreach (string a in args) info.ArgumentList.Add(a);
            if (!args.Any(a => a.StartsWith("--report", StringComparison.OrdinalIgnoreCase))) { info.ArgumentList.Add("--report"); info.ArgumentList.Add("json"); }
            info.Environment.Remove(BackgroundWork.LinesVariable);   // (the report says it)

            var diagnostics = new List<Diagnostic>();
            var outputs = new List<(string, string, string?)>();
            var projects = new List<ForgeEvent>();
            var log = new StringBuilder();
            ForgeEvent? end = null;
            var gate = new object();
            void Line(string text)
            {
                lock (gate) log.Append(text).Append('\n');
                sink.Log?.Invoke(text);
            }
            void Take(string? text)
            {
                if (text == null) return;
                if (!ForgeReport.TryParse(text, out var ev)) { Line(text); return; }
                switch (ev.Type)
                {
                    case "log": Line(ev.Text ?? ""); break;
                    case "progress": if (ev.Progress is { } p) sink.Progress?.Invoke(p); break;
                    case "background": sink.Background?.Invoke(ev.Text != null ? new BackgroundWork.Item(0, ev.Text, ev.Done, ev.Total) : null); break;
                    case "diagnostic":
                        if (ev.Diagnostic is { } d) { lock (gate) diagnostics.Add(d); sink.Diagnostic?.Invoke(d); }
                        break;
                    case "output": if (ev.Path != null) lock (gate) outputs.Add((ev.Kind ?? "program", ev.Path, ev.Project)); break;
                    case "project": lock (gate) projects.Add(ev); break;
                    case "end": lock (gate) end = ev; break;
                }
            }

            using var process = new Process { StartInfo = info };
            try { process.Start(); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                return new ForgeOutcome { ExitCode = -1, Error = $"forge could not be started ({f.Program}): {ex.Message}" };
            }
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception) { }   // (the editor stays quick while the compilers work)
            sink.Started?.Invoke(process);
            var stderrDone = Task.Run(() =>
            {
                string? l;
                while ((l = process.StandardError.ReadLine()) != null) Line(l);
            });
            Task? stopping = null;
            using (token.Register(() => stopping = Task.Run(() => Stop(process))))
            {
                string? l;
                while ((l = process.StandardOutput.ReadLine()) != null) Take(l);
                process.WaitForExit();
                stderrDone.Wait(TimeSpan.FromSeconds(5));
            }
            try { stopping?.Wait(CancelGrace + TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
            bool cancelled = token.IsCancellationRequested || end?.Cancelled == true;
            lock (gate)
                return new ForgeOutcome
                {
                    ExitCode = process.ExitCode,
                    Ok = !cancelled && end?.Ok == true && process.ExitCode == 0,
                    Cancelled = cancelled,
                    Ended = end != null,
                    Diagnostics = diagnostics.ToList(),
                    Outputs = outputs.ToList(),
                    Projects = projects.ToList(),
                    Summary = end?.Summary,
                    Error = end?.Error ?? (end == null && !cancelled ? $"forge ended with exit code {process.ExitCode} without finishing its report." : null),
                    Log = log.ToString(),
                };
        }

        /// <summary>Cancels a forge: "cancel" on its input (and SIGTERM on Linux and macOS) - it ends its tools and cleans up -, after <see cref="CancelGrace"/> it is killed with everything it started.</summary>
        public static void Stop(Process forge)
        {
            try
            {
                if (forge.HasExited) return;
                try { forge.StandardInput.WriteLine(ForgeReport.CancelLine); forge.StandardInput.Flush(); }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
                if (!OperatingSystem.IsWindows())
                    try { SysKill(forge.Id, 15); } catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
                if (forge.WaitForExit((int)CancelGrace.TotalMilliseconds)) return;
                BuildProcess.Terminate(forge, TimeSpan.FromSeconds(1));
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ObjectDisposedException) { }
        }

        [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
        private static extern int SysKill(int pid, int signal);
    }

    /// <summary>
    /// The text of the editor that is not saved, for forge (docs/tools/spark.md, "Builds"): spark builds what the editor shows - as before, when it built in its own process. Each
    /// changed document is written into a temporary folder and named with <c>--overlay &lt;file&gt;=&lt;copy&gt;</c>; a script that was never saved is written there as the
    /// source itself. <see cref="Dispose"/> removes the folder (after the build).
    /// </summary>
    public sealed class ForgeOverlays : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "spark-overlay-" + Guid.NewGuid().ToString("N"));
        private readonly List<string> _args = new();
        private int _count;

        /// <summary>The arguments for forge (<c>--overlay a=b</c> for each).</summary>
        public IReadOnlyList<string> Args => _args;

        /// <summary>The untitled sources written here (their full paths): errors in them belong to the document.</summary>
        public HashSet<string> Untitled { get; } = new(fire.Projects.ProjectFiles.PathComparer);

        private string Write(string name, string text)
        {
            Directory.CreateDirectory(_folder);
            string file = Path.Combine(_folder, (++_count).ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + name);
            File.WriteAllText(file, text);
            return file;
        }

        /// <summary>The text of <paramref name="path"/> is <paramref name="text"/> for this build.</summary>
        public void Add(string path, string text)
        {
            string copy = Write(Path.GetFileName(path), text);
            _args.Add("--overlay");
            _args.Add(Path.GetFullPath(path) + "=" + copy);
        }

        /// <summary>A script without a file: written here; the path is the source to build.</summary>
        public string AddUntitled(string name, string text)
        {
            string file = Write(string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)), text);
            Untitled.Add(file);
            return file;
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_folder)) Directory.Delete(_folder, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
