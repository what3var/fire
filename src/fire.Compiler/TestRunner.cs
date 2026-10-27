using System.Diagnostics;
using System.Text;
using fire.Bytecode;
using fire.Runtime;
using fire.Values;

namespace fire.Compiler
{
    public enum TestStatus { Passed, Failed, Error }

    /// <summary>One test that ran (a test with parameters: one per set of values).</summary>
    public sealed class TestResult
    {
        public required string Class { get; init; }
        public required string Method { get; init; }
        public required string Name { get; init; }
        public string Description { get; init; } = "";
        public TestStatus Status { get; set; } = TestStatus.Error;
        public string Message { get; set; } = "";
        public bool Finished { get; set; }
        /// <summary>What the test printed.</summary>
        public StringBuilder Output { get; } = new();
    }

    /// <summary>What a test run found: the tests and the problems that are not a test (an attribute in the wrong place, a class that could not be made, a program that stopped).</summary>
    public sealed class TestRun
    {
        public required string Title { get; init; }
        public List<TestResult> Results { get; } = new();
        public List<string> Problems { get; } = new();
        public TimeSpan Duration { get; set; }
        public int Passed => Results.Count(r => r.Status == TestStatus.Passed);
        public int Failed => Results.Count(r => r.Status == TestStatus.Failed);
        public int Errors => Results.Count(r => r.Status == TestStatus.Error);
        public bool Ok => Failed == 0 && Errors == 0 && Problems.Count == 0;
    }

    /// <summary>Reads the lines of the test driver (see <see cref="TestDriver"/>) into a <see cref="TestRun"/>; other lines are the output of the running test.</summary>
    public sealed class TestProtocol
    {
        private readonly TestRun _run;
        private TestResult? _current;
        public bool Done { get; private set; }
        /// <summary>A test started (for the live display).</summary>
        public event Action<TestResult>? Started;
        /// <summary>A test finished.</summary>
        public event Action<TestResult>? Finished;

        public TestProtocol(TestRun run) { _run = run; }

        public void Line(string line)
        {
            if (!line.StartsWith(TestDriver.Marker, StringComparison.Ordinal))
            {
                if (_current != null) _current.Output.AppendLine(line);
                return;
            }
            var parts = line.Substring(TestDriver.Marker.Length).Split('|');
            string Part(int i) => i < parts.Length ? parts[i].Replace("\\n", "\n") : "";
            switch (parts[0])
            {
                case "S":
                    Close();
                    _current = new TestResult { Class = Part(1), Method = Part(2), Name = Part(1) + "." + Part(3), Description = Part(4) };
                    _run.Results.Add(_current);
                    Started?.Invoke(_current);
                    break;
                case "P":
                    if (_current != null) { _current.Status = TestStatus.Passed; _current.Finished = true; }
                    break;
                case "F":
                case "E":
                    if (_current != null) { _current.Status = parts[0] == "F" ? TestStatus.Failed : TestStatus.Error; _current.Message = Rest(parts, 1); _current.Finished = true; }
                    break;
                case "C":
                    if (_current != null && _current.Status != TestStatus.Error)
                    {
                        _current.Message = (_current.Status == TestStatus.Failed ? _current.Message + "; " : "") + "[CleanTest] threw: " + Rest(parts, 1);
                        _current.Status = TestStatus.Error;
                    }
                    break;
                case "X":
                    Close();
                    _run.Problems.Add(Part(2) switch
                    {
                        "construct" => $"{Part(1)}: the test object could not be made (new {Part(1)}()): {Rest(parts, 3)}",
                        _ => $"{Part(1)}: [{Part(2)}] threw: {Rest(parts, 3)}",
                    });
                    break;
                case "D":
                    Close();
                    Done = true;
                    break;
            }
        }

        /// <summary>The program stopped (an exception nobody caught, a crash): the running test is an error with this message.</summary>
        public void Stopped(string message)
        {
            if (_current != null && !_current.Finished) { _current.Status = TestStatus.Error; _current.Message = message; _current.Finished = true; Close(); }
            else _run.Problems.Add("The test run stopped: " + message);
        }

        private void Close()
        {
            if (_current == null) return;
            if (!_current.Finished && _current.Message == "") _current.Message = "the test did not finish";
            Finished?.Invoke(_current);
            _current = null;
        }

        private static string Rest(string[] parts, int from) => string.Join("|", parts.Skip(from)).Replace("\\n", "\n");
    }

    /// <summary>
    /// `forge test` (docs/TESTING.md): compiles a program with its tests (the top level of its files does not run), runs the generated driver in the VM or as a native program and reports.
    /// While it runs, a console that is not redirected shows a spinner (`\ - / |`) with the running test; at the end a list of the tests and the sums.
    /// </summary>
    public sealed class TestRunner
    {
        private readonly TextWriter _out;
        private readonly bool _live;
        private readonly object _lock = new();
        private int _spin;
        private string _status = "";
        private int _shownWidth;

        private volatile bool _cancelled;
        private VM? _vm;
        private Process? _process;

        public TestRunner(TextWriter output, bool live)
        {
            _out = output;
            _live = live;
        }

        /// <summary>Only these tests run (null: all).</summary>
        public TestFilter? Filter { get; set; }

        /// <summary>A test started (on the thread of the run - the editor posts it to its UI thread).</summary>
        public event Action<TestResult>? TestStarted;

        /// <summary>A test finished (its status, message and output are complete).</summary>
        public event Action<TestResult>? TestFinished;

        /// <summary>Was the run stopped with <see cref="Cancel"/>?</summary>
        public bool Cancelled => _cancelled;

        /// <summary>Stops the run from another thread: the VM leaves at its next check point, a native test program is killed. The running test ends as an error "cancelled".</summary>
        public void Cancel()
        {
            _cancelled = true;
            lock (_lock)
            {
                _vm?.RequestLeave();
                try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }

        /// <summary>Runs the tests of <paramref name="sources"/> in the VM.</summary>
        /// <param name="files">The names of the files of <paramref name="sources"/> (for the messages), when there is no plan.</param>
        /// <param name="basePath">The folder `#include` of a file without a plan starts from (else the current folder).</param>
        public TestRun RunInVm(string title, IReadOnlyList<string> sources, VmExecutionMode? mode = null, int? floatWidth = null, IReadOnlyList<string>? defines = null, fire.Projects.BuildPlan? plan = null,
            IReadOnlyList<string>? files = null, string? basePath = null)
        {
            var run = new TestRun { Title = title };
            var protocol = Attach(run);
            var watch = Stopwatch.StartNew();
            using var spinner = StartSpinner();
            RuntimeSession session;
            int tests = 0;
            Linker.TestDriver = (program, first) => Driver(program, first, plan, files, run, out tests);
            try
            {
                session = RuntimeSession.Build(sources, mode, args =>
                {
                    if (args.Length > 0) protocol.Line(args[0].ToString());
                    return Value.MakeUndefined();
                }, basePath: basePath, floatWidth: floatWidth, defines: defines, plan: plan);
            }
            finally { Linker.TestDriver = null; }
            if (tests > 0 && !_cancelled)
            {
                lock (_lock) _vm = session.VirtualMachine;
                try { session.Run(); }
                finally { lock (_lock) _vm = null; }
                if (_cancelled && !protocol.Done) protocol.Stopped("cancelled");
                else if (session.VirtualMachine!.UnhandledException is { } ex) protocol.Stopped(new UncaughtScriptException(ex).Message);
                else if (!protocol.Done) protocol.Stopped("the program ended before the tests were done (`leave`/`terminate`?)");
            }
            run.Duration = watch.Elapsed;
            return run;
        }

        /// <summary>Runs the tests of <paramref name="sources"/> as a native program for this machine (built with the toolchain of the configuration).</summary>
        public TestRun RunNative(string title, IReadOnlyList<string> sources, fire.Native.NativeConfig config, string? toolchainName, VmExecutionMode? mode = null, int? floatWidth = null,
            IReadOnlyList<string>? defines = null, fire.Projects.BuildPlan? plan = null, IReadOnlyList<string>? files = null)
        {
            var run = new TestRun { Title = title };
            var protocol = Attach(run);
            var watch = Stopwatch.StartNew();
            using var spinner = StartSpinner();
            Status("building the native test program ...");
            var target = config.ResolveTarget(null);
            var toolchain = config.ResolveToolchain(target, toolchainName);
            string dir = Path.Combine(Path.GetTempPath(), "fire-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string exe = Path.Combine(dir, OperatingSystem.IsWindows() ? "tests.exe" : "tests");
                int tests = 0;
                Linker.TestDriver = (program, first) => Driver(program, first, plan, files, run, out tests);
                NativeBuildResult result;
                try { result = NativeBuilder.Build(sources, config, target, toolchain, exe, mode, floatWidth, defines: defines, plan: plan); }
                finally { Linker.TestDriver = null; }
                if (!result.Ok) { run.Problems.Add("The native build of the tests failed:" + Environment.NewLine + result.Log.TrimEnd()); return run; }
                if (tests == 0 || _cancelled) return run;

                var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Directory.GetCurrentDirectory() };
                using var process = Process.Start(psi)!;
                lock (_lock) _process = process;
                var errors = new StringBuilder();
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errors) errors.AppendLine(e.Data); };
                process.BeginErrorReadLine();
                string? line;
                while ((line = process.StandardOutput.ReadLine()) != null) lock (_lock) protocol.Line(line);
                process.WaitForExit();
                lock (_lock) _process = null;
                if (_cancelled && !protocol.Done) protocol.Stopped("cancelled");
                else if (!protocol.Done) protocol.Stopped($"the program ended with code {process.ExitCode} before the tests were done" + (errors.Length > 0 ? ": " + errors.ToString().Trim() : ""));
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                run.Duration = watch.Elapsed;
            }
            return run;
        }

        private string Driver(IReadOnlyList<Ast.Stmt> program, int first, fire.Projects.BuildPlan? plan, IReadOnlyList<string>? files, TestRun run, out int tests)
        {
            string? FileOf(int source)
            {
                int i = source - first;
                if (plan != null) return i >= 0 && i < plan.Sources.Count ? Path.GetRelativePath(plan.Project.Directory, plan.Sources[i].Path).Replace('\\', '/') : null;
                return files != null && i >= 0 && i < files.Count ? files[i] : null;
            }
            return TestDriver.Generate(program, first, FileOf, run.Problems, out tests, Filter);
        }

        private TestProtocol Attach(TestRun run)
        {
            var protocol = new TestProtocol(run);
            protocol.Started += r => { Status($"{run.Passed} ok, {run.Failed + run.Errors} failed - {r.Name}"); TestStarted?.Invoke(r); };
            protocol.Finished += r => TestFinished?.Invoke(r);
            return protocol;
        }

        // ---- live display ----------------------------------------------------------------------------------------------

        private static readonly char[] Frames = { '\\', '-', '/', '|' };

        private IDisposable StartSpinner()
        {
            if (!_live) return new Stop(null, this);
            var timer = new Timer(_ => Draw(advance: true), null, 100, 100);
            return new Stop(timer, this);
        }

        private void Status(string text)
        {
            if (!_live) return;
            lock (_lock) { _status = text; }
            Draw(advance: true);
        }

        private void Draw(bool advance)
        {
            lock (_lock)
            {
                if (advance) _spin = (_spin + 1) % Frames.Length;
                int width = 79;
                try { if (Console.WindowWidth > 20) width = Console.WindowWidth - 1; } catch (IOException) { } catch (PlatformNotSupportedException) { }
                string text = $"[{Frames[_spin]}] {_status}";
                if (text.Length > width) text = text.Substring(0, width);
                _out.Write("\r" + text.PadRight(Math.Max(_shownWidth, text.Length)));
                _shownWidth = text.Length;
                _out.Flush();
            }
        }

        private void Clear()
        {
            lock (_lock)
            {
                if (_shownWidth > 0) _out.Write("\r" + new string(' ', _shownWidth) + "\r");
                _shownWidth = 0;
                _out.Flush();
            }
        }

        private sealed class Stop : IDisposable
        {
            private readonly Timer? _timer;
            private readonly TestRunner _owner;
            public Stop(Timer? timer, TestRunner owner) { _timer = timer; _owner = owner; }
            public void Dispose()
            {
                if (_timer == null) return;
                using var done = new ManualResetEvent(false);
                _timer.Dispose(done);
                done.WaitOne(1000);
                _owner.Clear();
            }
        }

        // ---- summary ---------------------------------------------------------------------------------------------------

        /// <summary>The list of the tests and the sums.</summary>
        public static void WriteSummary(TestRun run, TextWriter output)
        {
            output.WriteLine($"Tests of {run.Title}:");
            foreach (var r in run.Results)
            {
                string tag = r.Status switch { TestStatus.Passed => "  ok    ", TestStatus.Failed => "  FAILED", _ => "  ERROR " };
                output.WriteLine($"{tag}  {r.Name}");
                if (r.Status == TestStatus.Passed) continue;
                if (r.Description != "") output.WriteLine($"          ({r.Description})");
                foreach (var line in r.Message.Split('\n')) output.WriteLine($"          {line}");
                if (r.Output.Length > 0)
                {
                    output.WriteLine("          output:");
                    foreach (var line in r.Output.ToString().TrimEnd().Split('\n')) output.WriteLine($"            {line.TrimEnd('\r')}");
                }
            }
            foreach (var p in run.Problems)
            {
                var lines = p.Split('\n');
                output.WriteLine($"  PROBLEM {lines[0].TrimEnd('\r')}");
                foreach (var line in lines.Skip(1)) output.WriteLine($"          {line.TrimEnd('\r')}");
            }
            if (run.Results.Count == 0 && run.Problems.Count == 0) output.WriteLine("  no tests (a test is a method with [Test], see docs/TESTING.md)");
            output.WriteLine($"{run.Results.Count} test{(run.Results.Count == 1 ? "" : "s")}: {run.Passed} passed, {run.Failed} failed, {run.Errors} error{(run.Errors == 1 ? "" : "s")}"
                + (run.Problems.Count > 0 ? $", {run.Problems.Count} problem{(run.Problems.Count == 1 ? "" : "s")}" : "") + $" ({run.Duration.TotalSeconds:0.00} s)");
        }
    }
}
