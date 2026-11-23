using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Interactivity;
using Avalonia.Threading;
using fire.Tooling;
using fire.Projects;

namespace fire.Editor
{
    // The Test Explorer (docs/tools/testing.md): finds the tests of the solution, the project or the file (forge serve, `tests`) and runs them with `forge test --report json`
    // (in the VM; the results come as the tests run).
    public partial class MainWindow
    {
        private readonly TestExplorerControl _testExplorer = new();

        /// <summary>What a group of the explorer runs: a project (its plan with the tests), or a single file (its text).</summary>
        private sealed record TestTarget(string Id, string Title, LoadedProject? Project, string? File, string? Text, IReadOnlyList<string> Files);

        private readonly Dictionary<string, TestTarget> _testTargets = new();
        private TestRunner? _testRunner;
        private bool _testsStopped;
        private DispatcherTimer? _testDiscoveryTimer;
        private int _testDiscoveryVersion;

        private void InitTests()
        {
            _testExplorer.RunRequested += kind => _ = RunTests(kind);
            _testExplorer.StopRequested += StopTests;
            _testExplorer.RefreshRequested += () => _ = DiscoverTests();
            _testExplorer.LocationRequested += (file, line) => ShowLocation(Path.IsPathRooted(file) ? file : null, file, line);
            // a frame of a test's trace: next to the test's file first (the folder of the program's first source, as the trace names it), else as in the output
            _testExplorer.TraceLocationRequested += (file, line, testFile) =>
            {
                string? next = testFile != null && Path.IsPathRooted(testFile) && Path.GetDirectoryName(testFile) is { } dir ? Path.GetFullPath(Path.Combine(dir, file)) : null;
                if (next != null && File.Exists(next)) ShowLocation(next, null, line);
                else ShowTraceLocation(file, line);
            };
            _testDiscoveryTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => { _testDiscoveryTimer!.Stop(); _ = DiscoverTests(); });
        }

        /// <summary>The tests are looked for again a little later (the workspace, the active document or a file changed) - several changes in a row make one search.</summary>
        private void ScheduleTestDiscovery(bool activeDocumentChanged = false)
        {
            if (_testDiscoveryTimer == null || _testExplorer.IsRunning) return;
            if (activeDocumentChanged && _testExplorer.Scope == TestScope.Solution) return;   // the solution does not depend on the active document
            _testDiscoveryTimer.Stop();
            _testDiscoveryTimer.Start();
        }

        /// <summary>The project a file belongs to - also a file in testing/ (which belongs to the tests of the project only).</summary>
        private LoadedProject? ProjectOfTestFile(string path)
        {
            if (!_workspace.IsOpen) return null;
            return _workspace.FindProjectOf(path) ?? _workspace.Projects.FirstOrDefault(p => p.TestFiles.Contains(Path.GetFullPath(path), ProjectFiles.PathComparer));
        }

        private static IReadOnlyList<string> TestableFiles(LoadedProject project)
        {
            project.Refresh();
            return project.Files.Concat(project.TestFiles).Where(f => !FireProject.IsMarkupFile(f)).ToList();
        }

        /// <summary>The groups of the scope: the projects of the solution, the project of the active document (or the one selected in the explorer), or the active file.</summary>
        private List<TestTarget> TestTargets(out string scopeText)
        {
            var targets = new List<TestTarget>();
            var doc = ActiveDocument?.Script != null ? ActiveDocument : null;
            string? docPath = FullPathOf(doc);
            var scope = _testExplorer.Scope;
            if (scope == TestScope.Solution && !_workspace.IsOpen) scope = TestScope.File;   // no solution: the active file
            LoadedProject? project = null;
            if (scope == TestScope.Project)
            {
                project = (docPath != null ? ProjectOfTestFile(docPath) : null) ?? _solutionPanel.Selected?.Project ?? _workspace.Startup;
                if (project == null) scope = TestScope.File;   // no project: the active file
            }

            switch (scope)
            {
                case TestScope.Solution:
                    scopeText = _workspace.Solution != null ? $"Solution: {_workspace.Name}" : $"Project: {_workspace.Name}";
                    foreach (var p in _workspace.Projects) targets.Add(new TestTarget(p.FilePath, p.Name, p, null, null, TestableFiles(p)));
                    break;
                case TestScope.Project:
                    scopeText = $"Project: {project!.Name}";
                    targets.Add(new TestTarget(project.FilePath, project.Name, project, null, null, TestableFiles(project)));
                    break;
                default:
                    if (doc == null) { scopeText = "File: (no script is active)"; break; }
                    scopeText = $"File: {doc.DisplayName}";
                    var owner = docPath != null ? ProjectOfTestFile(docPath) : null;
                    if (owner != null) targets.Add(new TestTarget(owner.FilePath, $"{owner.Name} - {doc.DisplayName}", owner, docPath, null, new[] { docPath! }));
                    else targets.Add(new TestTarget(docPath ?? "doc:" + doc.DisplayName, doc.DisplayName, null, docPath ?? doc.DisplayName, doc.Script!.GetText(), new[] { docPath ?? doc.DisplayName }));
                    break;
            }
            return targets;
        }

        /// <summary>The tests of files (forge serve, `tests`: parsing only); a file the server could not look at is a problem of the group.</summary>
        private static List<DiscoveredTest> DiscoverInFiles(IEnumerable<string> files, Func<string, string?> textOf, List<string> problems)
        {
            var found = new List<DiscoveredTest>();
            foreach (string f in files)
            {
                if (FireProject.IsMarkupFile(f)) continue;
                string? text = textOf(f);
                if (text == null && !Path.IsPathRooted(f)) continue;
                if (text != null && !text.Contains("[Test", StringComparison.Ordinal)) continue;   // (the cheap answer for most files)
                if (text == null && File.Exists(f))
                {
                    try { if (!File.ReadAllText(f).Contains("[Test", StringComparison.Ordinal)) continue; } catch (IOException) { continue; }
                }
                if (ServeDocuments.Tests(f, text) is not { } r) { problems.Add($"{Path.GetFileName(f)}: the tests cannot be found without forge serve."); continue; }
                found.AddRange(r.Tests);
                problems.AddRange(r.Problems);
            }
            return found;
        }

        /// <summary>Looks for the tests of the scope (parsing only, on a background thread) and shows them.</summary>
        private async Task DiscoverTests()
        {
            if (_testExplorer.IsRunning) return;
            int version = ++_testDiscoveryVersion;
            var targets = TestTargets(out string scopeText);
            _testExplorer.SetScopeText(scopeText);
            // the texts of the open documents (also unsaved ones) are taken here, on the UI thread
            var texts = new Dictionary<string, string?>(ProjectFiles.PathComparer);
            foreach (var t in targets)
                foreach (var f in t.Files)
                    texts[f] = t.Text != null && t.File == f ? t.Text : Path.IsPathRooted(f) ? OpenText(f) : null;
            var groups = await Task.Run(() => targets.Select(t =>
            {
                var problems = new List<string>();
                var tests = DiscoverInFiles(t.Files, f => texts.TryGetValue(f, out var text) ? text : null, problems);
                return new TestGroupInfo(t.Id, t.Title, tests, problems);
            }).ToList());
            if (version != _testDiscoveryVersion || _testExplorer.IsRunning) return;   // a newer search, or a run started meanwhile
            _testTargets.Clear();
            foreach (var t in targets) _testTargets[t.Id] = t;
            _testExplorer.SetGroups(groups);
            if (targets.Count == 0) _testExplorer.ShowEmpty(_workspace.IsOpen ? "No tests." : "Open a solution, a project or a script to see its tests.");
        }

        /// <summary>Runs the tests (all of the scope, the selected or the failed ones): one run per group, in the VM, on a background thread; the results come live.</summary>
        private async Task RunTests(TestRunKind kind)
        {
            if (_testExplorer.IsRunning) return;
            if (_session.IsStarted && !_session.IsFinished) { UpdateStatus("Stop the running program first (Shift+F5) - the tests run in the same VM host."); return; }
            if (kind == TestRunKind.All) await DiscoverTests();   // new tests are part of "all"
            var selection = _testExplorer.Selection(kind);
            if (selection.Count == 0) { UpdateStatus(kind == TestRunKind.Failed ? "No failed tests." : kind == TestRunKind.Selected ? "Select a test, a class or a project." : "No tests to run."); return; }
            RevealTool("tests");

            // what each run compiles is taken on the UI thread: the project (or the file) and the texts of the open documents that are not saved (forge reads them as overlays)
            var unsaved = OpenTexts();
            var jobs = new List<(string Id, string Title, string File, Dictionary<string, string> Overlays, TestFilter? Filter, string? Error, string? Temp)>();
            foreach (var id in _testExplorer.GroupIds)
            {
                if (!selection.TryGetValue(id, out var keys) || !_testTargets.TryGetValue(id, out var target)) continue;
                // all tests of a project: no filter (also the tests the explorer could not see, e.g. from an #include); else exactly the chosen ones
                var filter = kind == TestRunKind.All && target.File == null ? null : TestFilter.Of(keys);
                _testExplorer.MarkQueued(id, keys);
                var overlays = new Dictionary<string, string>(ProjectFiles.PathComparer);
                foreach (var (path, text) in unsaved)
                {
                    try { if (!File.Exists(path) || File.ReadAllText(path) != text) overlays[path] = text; }
                    catch (IOException) { }
                }
                if (target.Project != null)
                    jobs.Add((id, target.Project.Name, target.Project.FilePath, overlays, filter, null, null));
                else if (target.File != null && Path.IsPathRooted(target.File) && File.Exists(target.File))
                    jobs.Add((id, target.Title, target.File, overlays, filter, null, null));
                else
                {
                    // (a document that was never saved: its text as a file of its own)
                    string temp = Path.Combine(Path.GetTempPath(), $"fire-tests-{Environment.ProcessId}-{Guid.NewGuid():N}.fire");
                    try { File.WriteAllText(temp, target.Text ?? ""); jobs.Add((id, target.Title, temp, overlays, filter, null, temp)); }
                    catch (IOException ex) { jobs.Add((id, target.Title, "", overlays, filter, ex.Message, null)); }
                }
            }
            if (jobs.Count == 0) return;

            _testsStopped = false;
            _testExplorer.SetRunning(true);
            UpdateStatus("Running tests ...");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                foreach (var job in jobs)
                {
                    if (_testsStopped) break;
                    if (job.Error != null) { _testExplorer.GroupDone(job.Id, new[] { job.Error }); continue; }
                    var runner = new TestRunner { Filter = job.Filter };
                    runner.TestStarted += r => Dispatcher.UIThread.Post(() => _testExplorer.TestStarted(job.Id, r));
                    runner.TestFinished += r => Dispatcher.UIThread.Post(() => _testExplorer.TestFinished(job.Id, r));
                    _testRunner = runner;
                    var (run, error) = await Task.Run(() =>
                    {
                        try { return (runner.Run(job.Title, job.File, job.Overlays), (string?)null); }
                        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { return ((TestRun?)null, ex.Message); }
                        finally { if (job.Temp != null) try { File.Delete(job.Temp); } catch (IOException) { } }
                    });
                    _testRunner = null;
                    // the events were posted: the results are shown before the problems of the run
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                    if (run is { CompileErrors: null }) _testExplorer.GroupDone(job.Id, run.Problems);
                    else _testExplorer.GroupDone(job.Id, new[] { "The tests could not be compiled:" + Environment.NewLine + (run?.CompileErrors ?? error) });
                }
            }
            finally
            {
                _testRunner = null;
                _testExplorer.SetRunning(false);
            }
            UpdateStatus((_testsStopped ? "Test run stopped - " : "Tests - ") + _testExplorer.SummaryText + $" ({watch.Elapsed.TotalSeconds:0.00} s).");
        }

        private void StopTests()
        {
            _testsStopped = true;
            _testRunner?.Cancel();
        }

        private void TestsRunAll_Click(object? sender, RoutedEventArgs e) => _ = RunTests(TestRunKind.All);
        private void TestsRunFailed_Click(object? sender, RoutedEventArgs e) => _ = RunTests(TestRunKind.Failed);
        private void TestsStop_Click(object? sender, RoutedEventArgs e) => StopTests();

        private void TestsScope_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Avalonia.Controls.MenuItem { Tag: string tag }) return;
            _testExplorer.Scope = Enum.Parse<TestScope>(tag);
            RevealTool("tests");
            _ = DiscoverTests();
        }

        /// <summary>The tests of the active file: Test > Run Tests of This File.</summary>
        private void TestsRunFile_Click(object? sender, RoutedEventArgs e)
        {
            _testExplorer.Scope = TestScope.File;
            _ = RunTests(TestRunKind.All);
        }

        private void TestsOpen_Click(object? sender, RoutedEventArgs e)
        {
            RevealTool("tests");
            _ = DiscoverTests();
        }
    }
}
