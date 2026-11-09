using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace fire.Editor
{
    /// <summary>The Profile tab after a profiled run (ProfileReport): which lines cost the most (instructions of the VM, how often they were entered), and for every
    /// own source file its code with the counts beside each line - hot lines in orange, lines with code that never ran in red. A click on a line opens it in the
    /// editor (<see cref="OpenLineRequested"/>).</summary>
    public sealed class ProfileViewerControl : UserControl, IDocumentView
    {
        private readonly StackPanel _body = new() { Spacing = 4, Margin = new Thickness(16, 10, 16, 24) };
        private readonly TextBlock _info = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Opacity = 0.75 };
        private readonly CheckBox _onlyFallow = new() { Content = "Only what never ran", Margin = new Thickness(8, 0, 0, 0) };
        private ProfileReport? _report;
        private string _text = "";

        private static readonly IBrush Hot = EditorTheme.Solid(Color.FromRgb(0xF9, 0x73, 0x16));
        private static readonly IBrush Bar = EditorTheme.Solid(Color.FromRgb(0x60, 0xA5, 0xFA));
        private static readonly IBrush Fallow = EditorTheme.Solid(Color.FromArgb(0x55, 0xEF, 0x44, 0x44));
        private static readonly IBrush Rule = EditorTheme.Solid(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
        private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, DejaVu Sans Mono, monospace");

        /// <summary>A line was clicked: the file (path, or null for the program's unsaved document) and the line.</summary>
        public event Action<string?, int>? OpenLineRequested;

        public string? FilePath { get; set; }
        public bool IsModified => false;
        public bool IsReadOnly => true;
        public event Action? ModifiedChanged { add { } remove { } }
        public event Action<int>? CaretLineChanged { add { } remove { } }

        public ProfileViewerControl()
        {
            _onlyFallow.IsCheckedChanged += (_, _) => Build();
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(4, 2), Children = { _onlyFallow, _info } };
            var scroll = new ScrollViewer
            {
                Content = _body,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            };
            var dock = new DockPanel();
            DockPanel.SetDock(bar, Avalonia.Controls.Dock.Top);
            dock.Children.Add(bar);
            dock.Children.Add(scroll);
            Content = dock;
            Focusable = true;
        }

        public void ResetTo(string text, string? filePath)
        {
            FilePath = filePath;
            if (string.IsNullOrWhiteSpace(text) && filePath != null)
            {
                try { text = File.ReadAllText(filePath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { text = ""; }
            }
            _text = text;
            _report = ProfileReport.FromJson(text);
            Build();
        }

        public string GetText() => _text;

        private void Build()
        {
            _body.Children.Clear();
            if (_report is not { } r)
            {
                _body.Children.Add(new TextBlock { Text = "This is no profile.", TextWrapping = TextWrapping.Wrap });
                return;
            }
            var all = r.Files.SelectMany(f => f.Lines.Select(l => (File: f, Line: l))).ToList();
            int code = all.Count(x => x.Line.Executable), ran = all.Count(x => x.Line.Executable && x.Line.Ran);
            _info.Text = $"{r.What}";
            _body.Children.Add(new TextBlock { Text = $"Profile: {r.What}", FontSize = 18, FontWeight = FontWeight.SemiBold });
            _body.Children.Add(new TextBlock
            {
                Text = $"{r.Seconds.ToString("0.0", CultureInfo.CurrentCulture)} s, {(Micros(r) ? $"{(r.Instructions / 1000.0).ToString("N1", CultureInfo.CurrentCulture)} ms in the lines of the program" : $"{r.Instructions.ToString("N0", CultureInfo.CurrentCulture)} instructions of the VM")}  ·  "
                     + $"{ran} of {code} lines with code ran, {code - ran} never ({Percent(code - ran, code)})",
                Opacity = 0.85, TextWrapping = TextWrapping.Wrap,
            });

            long max = Math.Max(1, all.Count == 0 ? 1 : all.Max(x => x.Line.Instructions));
            if (_onlyFallow.IsChecked != true)
            {
                Section(Micros(r) ? "Where the time goes: the lines that took the longest" : "Where the time goes: the lines with the most instructions");
                var top = all.Where(x => x.Line.Instructions > 0).OrderByDescending(x => x.Line.Instructions).Take(25).ToList();
                var grid = Grid(6);
                AddRow(grid, Header(Micros(r) ? "µs" : "Instructions"), Header(""), Header(""), Header("Entered"), Header("Line"), Header("Code"));
                foreach (var (file, line) in top)
                    AddRow(grid, Num(line.Instructions), Text(Percent(line.Instructions, r.Instructions), right: true), BarOf(line.Instructions, max, Hot), Num(line.Hits),
                        Link($"{Name(file)}:{line.Line}", file, line.Line), Code(line.Text.Trim()));
                _body.Children.Add(grid);
            }

            foreach (var file in r.Files)
            {
                var lines = _onlyFallow.IsChecked == true ? file.Lines.Where(l => l.Executable && !l.Ran).ToList() : file.Lines.ToList();
                int fileCode = file.Lines.Count(l => l.Executable), fileRan = file.Lines.Count(l => l.Executable && l.Ran);
                Section($"{Name(file)}: {fileRan} of {fileCode} lines with code ran");
                var fallow = Ranges(file.Lines.Where(l => l.Executable && !l.Ran).Select(l => l.Line).ToList());
                if (fallow.Length > 0) _body.Children.Add(new TextBlock { Text = "Never ran: lines " + fallow, Opacity = 0.85, TextWrapping = TextWrapping.Wrap, Foreground = EditorTheme.Solid(Color.FromRgb(0xF8, 0x71, 0x71)) });
                var grid = Grid(4);
                AddRow(grid, Header("Line"), Header("Entered"), Header(Micros(r) ? "µs" : "Instructions"), Header("Code"));
                foreach (var line in lines)
                {
                    var row = new Control[] { Link(line.Line.ToString(CultureInfo.CurrentCulture), file, line.Line), Num(line.Hits), Num(line.Instructions), Code(line.Text) };
                    int at = AddRow(grid, row);
                    // (the background of the row: red for code that never ran, orange as hot as the line is)
                    IBrush? back = !line.Ran ? Fallow : line.Instructions > 0 ? new SolidColorBrush(Color.FromRgb(0xF9, 0x73, 0x16), 0.08 + 0.45 * line.Instructions / max) : null;
                    if (back != null)
                    {
                        var shade = new Border { Background = back, IsHitTestVisible = false };
                        Avalonia.Controls.Grid.SetRow(shade, at);
                        Avalonia.Controls.Grid.SetColumnSpan(shade, 4);
                        grid.Children.Insert(0, shade);
                    }
                }
                _body.Children.Add(grid);
            }
        }

        private void Section(string title)
        {
            _body.Children.Add(new Border { Height = 1, Background = Rule, Margin = new Thickness(0, 14, 0, 2) });
            _body.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 2) });
        }

        private static Grid Grid(int columns)
        {
            var g = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
            for (int c = 0; c < columns; c++) g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            return g;
        }

        private static int AddRow(Grid grid, params Control[] cells)
        {
            int row = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (int c = 0; c < cells.Length; c++)
            {
                Avalonia.Controls.Grid.SetRow(cells[c], row);
                Avalonia.Controls.Grid.SetColumn(cells[c], c);
                cells[c].Margin = new Thickness(4, 0, 14, 0);
                cells[c].VerticalAlignment = VerticalAlignment.Center;
                grid.Children.Add(cells[c]);
            }
            return row;
        }

        private static Control Header(string text) => new TextBlock { Text = text, Opacity = 0.6, Margin = new Thickness(0, 0, 0, 3) };
        private static Control Text(string text, bool right = false) => new TextBlock { Text = text, HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left };
        private static Control Num(long n) => new TextBlock { Text = n == 0 ? "" : n.ToString("N0", CultureInfo.CurrentCulture), HorizontalAlignment = HorizontalAlignment.Right };
        private static Control Code(string text) => new TextBlock { Text = text.Replace("\t", "    "), FontFamily = Mono, FontSize = 12 };

        private Control Link(string text, ProfileFile file, int line)
        {
            var t = new TextBlock { Text = text, Foreground = Bar, Cursor = new Cursor(StandardCursorType.Hand), HorizontalAlignment = HorizontalAlignment.Right };
            t.PointerPressed += (_, e) => { OpenLineRequested?.Invoke(file.Path, line); e.Handled = true; };
            return t;
        }

        private static Control BarOf(long n, long max, IBrush brush) =>
            new Border { Width = Math.Max(1, 160.0 * n / Math.Max(1, max)), Height = 9, Background = brush, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left };

        private static bool Micros(ProfileReport r) => r.Measure == "µs";

        private static string Name(ProfileFile f) => f.Path != null ? Path.GetFileName(f.Path) : $"source {f.SourceIndex}";

        private static string Percent(long part, long whole) => whole <= 0 ? "" : (100.0 * part / whole).ToString("0.0", CultureInfo.CurrentCulture) + " %";

        /// <summary>`3, 4, 5, 9` as "3-5, 9".</summary>
        private static string Ranges(List<int> lines)
        {
            var parts = new List<string>();
            for (int i = 0; i < lines.Count;)
            {
                int j = i;
                while (j + 1 < lines.Count && lines[j + 1] == lines[j] + 1) j++;
                parts.Add(j > i ? $"{lines[i]}-{lines[j]}" : lines[i].ToString(CultureInfo.CurrentCulture));
                i = j + 1;
            }
            return string.Join(", ", parts);
        }

        public int GetCaretLine() => 1;
        public void FocusEditor() => Focus();
        public bool CanUndo => false;
        public bool CanRedo => false;
        public bool HasSelection => false;
        public int LineCount => 1;
        public void Undo() { }
        public void Redo() { }
        public void Cut() { }
        public void Copy() { }
        public void Paste() { }
        public void Delete() { }
        public void SelectAll() { }
        public void GoToLine(int line) { }
        public void Find() { }
        public void FindNext() { }
        public void FindPrevious() { }
        public void MarkSaved() { }
    }
}
