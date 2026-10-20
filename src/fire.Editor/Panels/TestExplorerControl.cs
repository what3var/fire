using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using fire.Compiler;

namespace fire.Editor
{
    /// <summary>Which tests the Test Explorer shows: those of the solution, of the project of the active document, or of the active file.</summary>
    internal enum TestScope { Solution, Project, File }

    /// <summary>Which tests a run takes: all that are shown, those below the selected row, or those that failed last time.</summary>
    internal enum TestRunKind { All, Selected, Failed }

    internal enum TestState { NotRun, Queued, Running, Passed, Failed, Error }

    internal enum TestNodeKind { Group, Class, Test, Case }

    /// <summary>A group of the explorer: the tests of one program (a project, or a file that belongs to no project) - one test run each.</summary>
    internal sealed record TestGroupInfo(string Id, string Title, IReadOnlyList<DiscoveredTest> Tests, IReadOnlyList<string> Problems);

    /// <summary>A row of the Test Explorer: a group (project), a class, a test, or one case of a test with [Parameters].</summary>
    internal sealed class TestNode : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public required TestNodeKind Kind { get; init; }
        public required string GroupId { get; init; }
        /// <summary>`Class.Method` of a test (and its cases), the class name of a class, the group id of a group.</summary>
        public required string Key { get; init; }
        public TestNode? Parent { get; init; }
        public string? File { get; set; }
        public int Line { get; set; }
        public string Description { get; set; } = "";
        public string Message { get; set; } = "";
        public string Output { get; set; } = "";
        public ObservableCollection<TestNode> Children { get; } = new();

        private string _text = "";
        public string Text { get => _text; set { if (_text != value) { _text = value; Notify(nameof(Text)); } } }

        private TestState _state;
        public TestState State
        {
            get => _state;
            set { if (_state != value) { _state = value; Notify(nameof(State)); Notify(nameof(Glyph)); Notify(nameof(GlyphBrush)); } }
        }

        private bool _expanded = true;
        public bool IsExpanded { get => _expanded; set { if (_expanded != value) { _expanded = value; Notify(nameof(IsExpanded)); } } }

        public string Glyph => State switch
        {
            TestState.Passed => "✔", TestState.Failed => "✖", TestState.Error => "⚠", TestState.Running => "▶", TestState.Queued => "◌", _ => "○",
        };

        public IBrush GlyphBrush => State switch
        {
            TestState.Passed => PassedBrush, TestState.Failed => EditorTheme.WineRed, TestState.Error => EditorTheme.Orange, TestState.Running => EditorTheme.Yellow, _ => EditorTheme.TextDim,
        };

        private static readonly IBrush PassedBrush = new SolidColorBrush(Color.FromRgb(0x7F, 0xD9, 0x7F));

        /// <summary>The tests below this row (itself for a test).</summary>
        public IEnumerable<TestNode> Tests() => Kind == TestNodeKind.Test ? new[] { this } : Kind == TestNodeKind.Case ? Array.Empty<TestNode>() : Children.SelectMany(c => c.Tests());

        private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// The Test Explorer (docs/TESTING.md): the tests (`[Test]` methods) of the solution, of the project of the active document or of the active file, grouped by project and class.
    /// It runs all of them, the selected ones or those that failed and shows the results live; a double click goes to the test. The window finds the tests and runs them
    /// (see MainWindow.Tests.cs) - the control only shows them and asks for the commands.
    /// </summary>
    internal sealed class TestExplorerControl : UserControl
    {
        private readonly TreeView _tree = new() { BorderThickness = new Thickness(0) };
        private readonly ObservableCollection<TestNode> _roots = new();
        private readonly ComboBox _scope = new() { MinWidth = 110, FontSize = 12 };
        private readonly TextBlock _scopeText = new() { Foreground = EditorTheme.TextDim, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _summary = new() { FontSize = 12, Margin = new Thickness(6, 3), TextWrapping = TextWrapping.Wrap };
        private readonly TextBox _details = new()
        {
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), FontSize = 12,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace"),
        };
        private readonly TextBlock _empty = new() { Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap, Foreground = EditorTheme.TextDim, VerticalAlignment = VerticalAlignment.Top };
        private readonly Button _runAll, _runSelected, _runFailed, _stop, _refresh;
        private readonly HashSet<string> _collapsed = new();
        private readonly Dictionary<string, List<string>> _problems = new();
        private bool _running;
        private bool _settingScope;

        /// <summary>The user asked for a run.</summary>
        public event Action<TestRunKind>? RunRequested;
        /// <summary>The Stop button.</summary>
        public event Action? StopRequested;
        /// <summary>The tests should be looked for again (Refresh, or another scope).</summary>
        public event Action? RefreshRequested;
        /// <summary>A test was double clicked: its file and line.</summary>
        public event Action<string, int>? LocationRequested;

        public TestExplorerControl()
        {
            _scope.ItemsSource = new[] { "Solution", "Project", "Current File" };
            _scope.SelectedIndex = 0;
            _scope.SelectionChanged += (_, _) => { if (!_settingScope) RefreshRequested?.Invoke(); };

            _runAll = ToolButton("▶▶", "Run all tests of the scope", () => RunRequested?.Invoke(TestRunKind.All));
            _runSelected = ToolButton("▶", "Run the selected tests (the tests of a selected class or project)", () => RunRequested?.Invoke(TestRunKind.Selected));
            _runFailed = ToolButton("✖▶", "Run the tests that failed", () => RunRequested?.Invoke(TestRunKind.Failed));
            _stop = ToolButton("■", "Stop the test run", () => StopRequested?.Invoke());
            _refresh = ToolButton("⟳", "Look for the tests again", () => RefreshRequested?.Invoke());
            _stop.IsEnabled = false;

            // the buttons, below them the scope (the panel is often narrow)
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            foreach (var b in new[] { _runAll, _runSelected, _runFailed, _stop, _refresh }) bar.Children.Add(b);
            var scopeRow = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
            DockPanel.SetDock(_scope, Avalonia.Controls.Dock.Left);
            scopeRow.Children.Add(_scope);
            _scopeText.Margin = new Thickness(8, 0, 4, 0);
            scopeRow.Children.Add(_scopeText);
            var top = new StackPanel { Margin = new Thickness(4, 2) };
            top.Children.Add(bar);
            top.Children.Add(scopeRow);

            _tree.ItemsSource = _roots;
            _tree.ItemTemplate = new FuncTreeDataTemplate<TestNode>((node, _) => Row(node), node => node.Children);
            _tree.Styles.Add(new Style(x => x.OfType<TreeViewItem>())
            {
                Setters = { new Setter(TreeViewItem.IsExpandedProperty, new Binding("IsExpanded") { Mode = BindingMode.TwoWay }) },
            });
            _tree.SelectionChanged += (_, _) => ShowDetails();
            _tree.DoubleTapped += (_, e) => { if (GoToSelected()) e.Handled = true; };
            _tree.KeyDown += (_, e) => { if (e.Key == Key.Enter && GoToSelected()) e.Handled = true; };

            var menu = new ContextMenu();
            menu.Items.Add(MenuItem("Run", () => RunRequested?.Invoke(TestRunKind.Selected)));
            menu.Items.Add(MenuItem("Go to Test", () => GoToSelected()));
            _tree.ContextMenu = menu;

            var detailsBorder = new Border { BorderBrush = EditorTheme.Border, BorderThickness = new Thickness(0, 1, 0, 0), Child = _details };
            var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,4,120") };
            var topBorder = new Border { BorderBrush = EditorTheme.Border, BorderThickness = new Thickness(0, 0, 0, 1), Child = top };
            Grid.SetRow(topBorder, 0);
            Grid.SetRow(_summary, 1);
            var treeArea = new Grid { Children = { _tree, _empty } };
            Grid.SetRow(treeArea, 2);
            var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Rows, Background = Brushes.Transparent };
            Grid.SetRow(splitter, 3);
            Grid.SetRow(detailsBorder, 4);
            grid.Children.Add(topBorder);
            grid.Children.Add(_summary);
            grid.Children.Add(treeArea);
            grid.Children.Add(splitter);
            grid.Children.Add(detailsBorder);
            Content = grid;
            ShowEmpty("No tests. A test is a method with [Test] (docs/TESTING.md).");
        }

        private static Button ToolButton(string text, string tip, Action click)
        {
            var b = new Button { Content = new TextBlock { Text = text, FontSize = 12 }, Padding = new Thickness(6, 2), MinWidth = 26 };
            b.Classes.Add("tool");
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => click();
            return b;
        }

        private static MenuItem MenuItem(string header, Action click)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => click();
            return item;
        }

        private static Control Row(TestNode node)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var glyph = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Width = 14, TextAlignment = TextAlignment.Center };
            glyph.Bind(TextBlock.TextProperty, new Binding(nameof(TestNode.Glyph)));
            glyph.Bind(TextBlock.ForegroundProperty, new Binding(nameof(TestNode.GlyphBrush)));
            panel.Children.Add(glyph);
            var text = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = node.Kind == TestNodeKind.Group ? FontWeight.Bold : FontWeight.Normal,
                Foreground = node.Kind == TestNodeKind.Class ? EditorTheme.TextDim : EditorTheme.Text,
            };
            text.Bind(TextBlock.TextProperty, new Binding(nameof(TestNode.Text)));
            panel.Children.Add(text);
            return panel;
        }

        // ---- the scope ----------------------------------------------------------------------------------------------------------------------

        public TestScope Scope
        {
            get => (TestScope)Math.Max(0, _scope.SelectedIndex);
            set { _settingScope = true; _scope.SelectedIndex = (int)value; _settingScope = false; }
        }

        /// <summary>What the scope is right now ("Project: Demo", "File: Math.fire").</summary>
        public void SetScopeText(string text) => _scopeText.Text = text;

        // ---- the tests ----------------------------------------------------------------------------------------------------------------------

        public bool IsRunning => _running;

        /// <summary>The sums of the tests shown ("5 tests: 3 passed, ...").</summary>
        public string SummaryText => _summary.Text ?? "";

        /// <summary>Shows these groups; what a test shown before had as result stays (by group and key).</summary>
        public void SetGroups(IReadOnlyList<TestGroupInfo> groups)
        {
            var old = AllNodes(_roots).Where(n => n.Kind == TestNodeKind.Test).ToDictionary(n => n.GroupId + "|" + n.Key);
            RememberCollapsed();
            _roots.Clear();
            _problems.Clear();
            foreach (var g in groups)
            {
                if (g.Tests.Count == 0 && g.Problems.Count == 0) continue;
                var group = new TestNode { Kind = TestNodeKind.Group, GroupId = g.Id, Key = g.Id, Text = g.Title };
                foreach (var t in g.Tests)
                {
                    var cls = ClassNode(group, t.Class);
                    var test = new TestNode { Kind = TestNodeKind.Test, GroupId = g.Id, Key = t.Key, Parent = cls, Text = t.Title, File = t.File, Line = t.Line, Description = t.Description };
                    if (old.TryGetValue(g.Id + "|" + t.Key, out var before))
                    {
                        test.State = before.State is TestState.Running or TestState.Queued ? TestState.NotRun : before.State;
                        test.Message = before.Message;
                        test.Output = before.Output;
                        foreach (var c in before.Children)
                            test.Children.Add(new TestNode { Kind = TestNodeKind.Case, GroupId = g.Id, Key = c.Key, Parent = test, Text = c.Text, State = c.State, Message = c.Message, Output = c.Output, File = t.File, Line = t.Line, Description = c.Description });
                        test.IsExpanded = false;
                    }
                    cls.Children.Add(test);
                }
                if (g.Problems.Count > 0) _problems[g.Id] = g.Problems.ToList();
                _roots.Add(group);
                Aggregate(group);
            }
            ApplyCollapsed();
            if (_roots.Count == 0) ShowEmpty("No tests. A test is a method with [Test] (docs/TESTING.md)."); else HideEmpty();
            UpdateSummary();
            ShowDetails();
        }

        /// <summary>Shows a text instead of the tree (nothing open, no tests).</summary>
        public void ShowEmpty(string text)
        {
            _empty.Text = text;
            _empty.IsVisible = true;
            _tree.IsVisible = false;
        }

        private void HideEmpty()
        {
            _empty.IsVisible = false;
            _tree.IsVisible = true;
        }

        private static TestNode ClassNode(TestNode group, string name)
        {
            var cls = group.Children.FirstOrDefault(c => c.Key == name);
            if (cls != null) return cls;
            cls = new TestNode { Kind = TestNodeKind.Class, GroupId = group.GroupId, Key = name, Parent = group, Text = name };
            group.Children.Add(cls);
            return cls;
        }

        /// <summary>The tests a run of this kind takes, by group (the keys `Class.Method`).</summary>
        public Dictionary<string, List<string>> Selection(TestRunKind kind)
        {
            IEnumerable<TestNode> tests = kind switch
            {
                TestRunKind.Selected => (_tree.SelectedItem as TestNode) is { } n ? (n.Kind == TestNodeKind.Case ? new[] { n.Parent! } : n.Tests()) : Enumerable.Empty<TestNode>(),
                TestRunKind.Failed => _roots.SelectMany(r => r.Tests()).Where(t => t.State is TestState.Failed or TestState.Error),
                _ => _roots.SelectMany(r => r.Tests()),
            };
            return tests.GroupBy(t => t.GroupId).ToDictionary(g => g.Key, g => g.Select(t => t.Key).Distinct().ToList());
        }

        /// <summary>The ids of the groups shown, in their order.</summary>
        public IReadOnlyList<string> GroupIds => _roots.Select(r => r.GroupId).ToList();

        // ---- a run ------------------------------------------------------------------------------------------------------------------------

        /// <summary>A run starts / ended: the buttons follow.</summary>
        public void SetRunning(bool running)
        {
            _running = running;
            _runAll.IsEnabled = _runSelected.IsEnabled = _runFailed.IsEnabled = _refresh.IsEnabled = _scope.IsEnabled = !running;
            _stop.IsEnabled = running;
            if (!running)
                foreach (var t in AllNodes(_roots).Where(n => n.State is TestState.Queued or TestState.Running).ToList())
                {
                    t.State = TestState.NotRun;
                    if (t.Parent != null) Aggregate(t.Parent);
                }
            UpdateSummary();
        }

        /// <summary>These tests of the group will run: they wait (their old result is gone).</summary>
        public void MarkQueued(string groupId, IEnumerable<string> keys)
        {
            var set = new HashSet<string>(keys);
            var group = _roots.FirstOrDefault(r => r.GroupId == groupId);
            if (group == null) return;
            _problems.Remove(groupId);
            foreach (var t in group.Tests().Where(t => set.Contains(t.Key)))
            {
                t.State = TestState.Queued;
                t.Message = t.Output = "";
                t.Children.Clear();
                if (t.Parent != null) Aggregate(t.Parent);
            }
            UpdateSummary();
            ShowDetails();
        }

        /// <summary>A test of the group started (a test with [Parameters]: one of its cases).</summary>
        public void TestStarted(string groupId, TestResult result)
        {
            var test = TestFor(groupId, result);
            if (test == null) return;
            if (IsCase(test, result))
            {
                var c = new TestNode { Kind = TestNodeKind.Case, GroupId = groupId, Key = test.Key, Parent = test, Text = CaseText(result), State = TestState.Running, File = test.File, Line = test.Line, Description = result.Description };
                test.Children.Add(c);
            }
            else test.State = TestState.Running;
            Aggregate(test);
            UpdateSummary();
        }

        /// <summary>A test of the group finished.</summary>
        public void TestFinished(string groupId, TestResult result)
        {
            var test = TestFor(groupId, result);
            if (test == null) return;
            var node = IsCase(test, result) ? test.Children.LastOrDefault(c => c.Text == CaseText(result)) ?? test : test;
            node.State = !result.Finished && result.Message == "" ? TestState.Error : result.Status switch { TestStatus.Passed => TestState.Passed, TestStatus.Failed => TestState.Failed, _ => TestState.Error };
            node.Message = result.Message;
            node.Output = result.Output.ToString();
            if (!ReferenceEquals(node, test) && node.State != TestState.Passed) test.IsExpanded = true;
            Aggregate(test);
            UpdateSummary();
            if (ReferenceEquals(_tree.SelectedItem, node) || ReferenceEquals(_tree.SelectedItem, test)) ShowDetails();
        }

        /// <summary>The run of the group is over: its problems (an attribute in the wrong place, a program that did not compile).</summary>
        public void GroupDone(string groupId, IEnumerable<string> problems)
        {
            var list = problems.ToList();
            if (list.Count > 0) _problems[groupId] = list; else _problems.Remove(groupId);
            UpdateSummary();
            ShowDetails();
        }

        private TestNode? TestFor(string groupId, TestResult result)
        {
            var group = _roots.FirstOrDefault(r => r.GroupId == groupId);
            if (group == null) return null;
            string key = result.Class + "." + result.Method;
            var test = group.Tests().FirstOrDefault(t => t.Key == key);
            if (test != null) return test;
            // a test the explorer did not find (in a file brought in with #include): it is added
            var cls = ClassNode(group, result.Class);
            test = new TestNode { Kind = TestNodeKind.Test, GroupId = groupId, Key = key, Parent = cls, Text = TitleOf(result), Description = result.Description };
            cls.Children.Add(test);
            HideEmpty();
            return test;
        }

        /// <summary>The name of a result without its class: `Title` or `Title(1, 2)`.</summary>
        private static string TitleOf(TestResult r) => r.Name.StartsWith(r.Class + ".", StringComparison.Ordinal) ? r.Name.Substring(r.Class.Length + 1) : r.Name;

        private static bool IsCase(TestNode test, TestResult r) => TitleOf(r) is var t && t.EndsWith(")", StringComparison.Ordinal) && t != test.Text && t.StartsWith(test.Text + "(", StringComparison.Ordinal);

        private static string CaseText(TestResult r) => TitleOf(r);

        /// <summary>A class or a group (and a test with cases) has the state of its children: failed before error before running before passed.</summary>
        private static void Aggregate(TestNode node)
        {
            for (var n = node; n != null; n = n.Parent)
            {
                if (n.Children.Count == 0) continue;
                var states = n.Children.Select(c => c.State).ToList();
                if (n.Kind == TestNodeKind.Test && n.Children.Any(c => c.Kind != TestNodeKind.Case)) continue;
                n.State = states.Contains(TestState.Failed) ? TestState.Failed
                    : states.Contains(TestState.Error) ? TestState.Error
                    : states.Contains(TestState.Running) ? TestState.Running
                    : states.Contains(TestState.Queued) ? TestState.Queued
                    : states.All(s => s == TestState.Passed) ? TestState.Passed
                    : TestState.NotRun;
            }
        }

        private void UpdateSummary()
        {
            var tests = _roots.SelectMany(r => r.Tests()).ToList();
            int passed = tests.Count(t => t.State == TestState.Passed), failed = tests.Count(t => t.State == TestState.Failed), errors = tests.Count(t => t.State == TestState.Error);
            int notRun = tests.Count(t => t.State is TestState.NotRun or TestState.Queued or TestState.Running);
            int problems = _problems.Values.Sum(p => p.Count);
            var sb = new StringBuilder($"{tests.Count} test{(tests.Count == 1 ? "" : "s")}: {passed} passed, {failed} failed, {errors} error{(errors == 1 ? "" : "s")}, {notRun} not run");
            if (problems > 0) sb.Append($", {problems} problem{(problems == 1 ? "" : "s")}");
            if (_running) sb.Append(" - running ...");
            _summary.Text = sb.ToString();
            _summary.Foreground = failed + errors + problems > 0 ? EditorTheme.WineRed : passed > 0 && notRun == 0 ? new SolidColorBrush(Color.FromRgb(0x7F, 0xD9, 0x7F)) : EditorTheme.Text;
        }

        // ---- details and going to the source ---------------------------------------------------------------------------------------------

        private void ShowDetails()
        {
            var sb = new StringBuilder();
            if (_tree.SelectedItem is TestNode node)
            {
                switch (node.Kind)
                {
                    case TestNodeKind.Test or TestNodeKind.Case:
                        sb.AppendLine($"{node.Key}{(node.Kind == TestNodeKind.Case ? "  " + node.Text : "")}");
                        if (node.Description != "") sb.AppendLine(node.Description);
                        sb.AppendLine(node.State switch
                        {
                            TestState.Passed => "Passed", TestState.Failed => "Failed", TestState.Error => "Error", TestState.Running => "Running ...", TestState.Queued => "Waiting to run", _ => "Not run",
                        });
                        if (node.Message != "") sb.AppendLine(node.Message);
                        if (node.Output.Trim() != "") { sb.AppendLine(); sb.AppendLine("Output:"); sb.AppendLine(node.Output.TrimEnd()); }
                        if (node.File != null) { sb.AppendLine(); sb.AppendLine($"{node.File}:{node.Line}"); }
                        break;
                    default:
                        var tests = node.Tests().ToList();
                        sb.AppendLine($"{node.Text}: {tests.Count} test{(tests.Count == 1 ? "" : "s")}, {tests.Count(t => t.State == TestState.Passed)} passed, {tests.Count(t => t.State is TestState.Failed or TestState.Error)} failed");
                        foreach (var t in tests.Where(t => t.State is TestState.Failed or TestState.Error))
                            sb.AppendLine($"  {t.Key}: {FirstLine(t.Message != "" ? t.Message : string.Join("; ", t.Children.Where(c => c.Message != "").Select(c => c.Text + " " + FirstLine(c.Message))))}");
                        break;
                }
                if (_problems.TryGetValue(node.GroupId, out var problems))
                {
                    sb.AppendLine();
                    sb.AppendLine("Problems:");
                    foreach (var p in problems) sb.AppendLine("  " + p);
                }
            }
            else if (_problems.Count > 0)
            {
                sb.AppendLine("Problems:");
                foreach (var p in _problems.Values.SelectMany(p => p)) sb.AppendLine("  " + p);
            }
            _details.Text = sb.ToString().TrimEnd();
        }

        private static string FirstLine(string text) => text.Split('\n')[0].TrimEnd('\r');

        private bool GoToSelected()
        {
            if (_tree.SelectedItem is not TestNode { File: { } file } node) return false;
            LocationRequested?.Invoke(file, node.Line);
            return true;
        }

        // ---- the expanded state across refreshes ------------------------------------------------------------------------------------------

        private static IEnumerable<TestNode> AllNodes(IEnumerable<TestNode> nodes) => nodes.SelectMany(n => new[] { n }.Concat(AllNodes(n.Children)));

        private void RememberCollapsed()
        {
            foreach (var n in AllNodes(_roots).Where(n => n.Kind is TestNodeKind.Group or TestNodeKind.Class))
            {
                string id = n.GroupId + "|" + n.Kind + "|" + n.Key;
                if (n.IsExpanded) _collapsed.Remove(id); else _collapsed.Add(id);
            }
        }

        private void ApplyCollapsed()
        {
            foreach (var n in AllNodes(_roots).Where(n => n.Kind is TestNodeKind.Group or TestNodeKind.Class))
                n.IsExpanded = !_collapsed.Contains(n.GroupId + "|" + n.Kind + "|" + n.Key);
        }
    }
}
