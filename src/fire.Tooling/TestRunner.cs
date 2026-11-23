using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace fire.Tooling
{
    public enum TestStatus { Passed, Failed, Error }

    /// <summary>A test found in a source file without running anything (the Test Explorer of the editor; forge serve, `tests`): its class, method, title, file and line.</summary>
    public sealed record DiscoveredTest(string Class, string Method, string Title, string Description, string File, int Line, bool HasParameters)
    {
        /// <summary>The key of the test for <see cref="TestFilter"/>: `Class.Method`.</summary>
        public string Key => Class + "." + Method;
    }

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
        /// <summary>The run could not be compiled: forge's errors (null: it ran).</summary>
        public string? CompileErrors { get; set; }
        public TimeSpan Duration { get; set; }
        public int Passed => Results.Count(r => r.Status == TestStatus.Passed);
        public int Failed => Results.Count(r => r.Status == TestStatus.Failed);
        public int Errors => Results.Count(r => r.Status == TestStatus.Error);
        public bool Ok => Failed == 0 && Errors == 0 && Problems.Count == 0 && CompileErrors == null;
    }

    /// <summary>
    /// The tests of a file or a project, run by forge (docs/tools/testing.md; M3: forge compiles the driver and runs it in `fire`): `forge test &lt;file|project&gt; --report json
    /// [--filter ...] [--overlay source=file]...` as a child process, its events (native/forge/tools/testrunner.c: `run`, `start`, `result`, `problem`, `error`, `done`, `end`)
    /// read as they come - <see cref="TestStarted"/> and <see cref="TestFinished"/> on the reader's thread. <see cref="Cancel"/> ends forge with its children.
    /// </summary>
    public sealed class TestRunner
    {
        private readonly object _lock = new();
        private Process? _process;
        private volatile bool _cancelled;

        /// <summary>Only these tests run (null: all).</summary>
        public TestFilter? Filter { get; set; }

        /// <summary>A test started / finished (for the live display).</summary>
        public event Action<TestResult>? TestStarted;
        public event Action<TestResult>? TestFinished;

        /// <summary>Ends the running tests (forge and the program it runs).</summary>
        public void Cancel()
        {
            _cancelled = true;
            lock (_lock)
            {
                try { if (_process is { HasExited: false } p) p.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }

        /// <summary>Runs the tests of <paramref name="file"/> (a source, a project or a solution) to their end. <paramref name="overlays"/>: texts of sources that are not
        /// saved (path -> text; written to temporary files for `--overlay`). <paramref name="engine"/>: vm or native.</summary>
        public TestRun Run(string title, string file, IReadOnlyDictionary<string, string>? overlays = null, string? workingDirectory = null, string engine = "vm",
            IEnumerable<string>? extraArgs = null)
        {
            var run = new TestRun { Title = title };
            var watch = Stopwatch.StartNew();
            var temps = new List<string>();
            try
            {
                var args = new List<string> { "test", file, "--report", "json" };
                if (engine != "vm") { args.Add("--engine"); args.Add(engine); }
                if (Filter is { IsEmpty: false } f) { args.Add("--filter"); args.Add(f.Text); }
                foreach (var (path, text) in overlays ?? new Dictionary<string, string>())
                {
                    string temp = Path.Combine(Path.GetTempPath(), $"fire-test-overlay-{Environment.ProcessId}-{Guid.NewGuid():N}.fire");
                    File.WriteAllText(temp, text, new UTF8Encoding(false));
                    temps.Add(temp);
                    args.Add("--overlay");
                    args.Add(Path.GetFullPath(path) + "=" + temp);
                }
                if (extraArgs != null) args.AddRange(extraArgs);
                var info = Forge.StartInfo(args, workingDirectory ?? Path.GetDirectoryName(Path.GetFullPath(file)));
                var errors = new StringBuilder();
                TestResult? current = null;
                bool ended = false;
                using var process = new Process { StartInfo = info };
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errors) errors.AppendLine(e.Data); };
                lock (_lock)
                {
                    if (_cancelled) { run.Problems.Add("cancelled"); return run; }
                    process.Start();
                    _process = process;
                }
                fire.Debugging.ChildProcesses.Add(process);
                process.StandardInput.Close();
                process.BeginErrorReadLine();
                string? line;
                while ((line = process.StandardOutput.ReadLine()) != null)
                {
                    JsonObject? ev;
                    try { ev = JsonNode.Parse(line) as JsonObject; } catch (System.Text.Json.JsonException) { ev = null; }
                    if (ev == null) continue;
                    string S(string key) => ev[key]?.GetValue<string>() ?? "";
                    switch (S("type"))
                    {
                        case "start":
                            current = new TestResult { Class = S("class"), Method = S("method"), Name = S("name"), Description = S("description") };
                            run.Results.Add(current);
                            TestStarted?.Invoke(current);
                            break;
                        case "result":
                        {
                            var r = current != null && current.Name == S("name") && !current.Finished ? current
                                : new TestResult { Class = S("class"), Method = S("method"), Name = S("name"), Description = S("description") };
                            if (!run.Results.Contains(r)) run.Results.Add(r);
                            r.Status = S("status") switch { "passed" => TestStatus.Passed, "failed" => TestStatus.Failed, _ => TestStatus.Error };
                            r.Message = S("message");
                            r.Output.Append(S("output"));
                            r.Finished = S("message") != "the test did not finish";
                            current = null;
                            TestFinished?.Invoke(r);
                            break;
                        }
                        case "problem": run.Problems.Add(S("text")); break;
                        case "error": run.CompileErrors = (run.CompileErrors == null ? "" : run.CompileErrors + Environment.NewLine) + S("text"); break;
                        case "end": ended = true; break;
                    }
                }
                process.WaitForExit();
                lock (_lock) _process = null;
                if (_cancelled) { if (current != null) { current.Status = TestStatus.Error; current.Message = "cancelled"; TestFinished?.Invoke(current); } else run.Problems.Add("The test run was cancelled."); }
                else if (!ended && run.CompileErrors == null)
                {
                    string text;
                    lock (errors) text = errors.ToString().Trim();
                    run.CompileErrors = text.Length > 0 ? text : $"forge ended with exit code {process.ExitCode}.";
                }
            }
            finally
            {
                foreach (var t in temps) try { File.Delete(t); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                run.Duration = watch.Elapsed;
            }
            return run;
        }
    }
}
