using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using fire.Native;

namespace fire.Editor
{
    /// <summary>The statistics of a native build (`forge build --stats`, the `.stats.json` next to the program; the Statistics switch of the build tool strip opens it
    /// after the build): the size of the program, its code by origin, its data, the typing of the generated fire functions by module, the biggest functions, why
    /// functions have no C interface, and the generated helpers. Read only.</summary>
    public sealed class BuildStatsViewerControl : UserControl, IDocumentView
    {
        private readonly StackPanel _body = new() { Spacing = 6, Margin = new Thickness(16, 10, 16, 24) };
        private readonly TextBlock _info = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Opacity = 0.75 };
        private NativeStats.Report? _report;

        private static readonly IBrush Bar = EditorTheme.Solid(Color.FromRgb(0x60, 0xA5, 0xFA));
        private static readonly IBrush BarFire = EditorTheme.Solid(Color.FromRgb(0xF9, 0x73, 0x16));
        private static readonly IBrush Rule = EditorTheme.Solid(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));

        public string? FilePath { get; set; }
        public bool IsModified => false;
        public bool IsReadOnly => true;
        public event Action? ModifiedChanged { add { } remove { } }
        public event Action<int>? CaretLineChanged { add { } remove { } }

        public BuildStatsViewerControl()
        {
            var reload = new Button { Content = "Reload" }; reload.Classes.Add("tool");
            reload.Click += (_, _) => { if (FilePath != null) ResetTo("", FilePath); };
            var copy = new Button { Content = "Copy as text" }; copy.Classes.Add("tool");
            copy.Click += async (_, _) =>
            {
                if (_report != null && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(NativeStats.Text(_report));
            };
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(4, 2), Children = { reload, copy, _info } };
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

        /// <summary>Shows the report of `text` (the JSON), or of the file when `text` is empty.</summary>
        public void ResetTo(string text, string? filePath)
        {
            FilePath = filePath;
            if (string.IsNullOrWhiteSpace(text) && filePath != null)
            {
                try { text = File.ReadAllText(filePath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { text = ""; }
            }
            _report = NativeStats.FromJson(text);
            _info.Text = filePath != null ? $"{Path.GetFileName(filePath)}   {(File.Exists(filePath) ? File.GetLastWriteTime(filePath).ToString("g", CultureInfo.CurrentCulture) : "")}" : "";
            Build();
        }

        private void Build()
        {
            _body.Children.Clear();
            if (_report is not { } r)
            {
                _body.Children.Add(new TextBlock { Text = "These are no build statistics (a .stats.json written by a native build with statistics).", TextWrapping = TextWrapping.Wrap });
                return;
            }
            _body.Children.Add(new TextBlock { Text = $"Build statistics: {r.Target}", FontSize = 18, FontWeight = FontWeight.SemiBold });
            long code = r.Code.Sum(o => o.Bytes), fire = r.Code.Where(o => o.Name.StartsWith("fire:", StringComparison.Ordinal)).Sum(o => o.Bytes);
            var summary = new List<string> { $"Program {Bytes(r.FileBytes)}" };
            if (r.LoadBytes is { } load) summary.Add($"loaded {Bytes(load)}");
            if (code > 0) summary.Add($"code {Bytes(code)}, of it generated fire code {Bytes(fire)} ({Percent(fire, code)})");
            _body.Children.Add(new TextBlock { Text = string.Join("  ·  ", summary), Opacity = 0.85, TextWrapping = TextWrapping.Wrap });

            if (r.Code.Count > 0)
            {
                Section("Code by origin");
                long max = r.Code.Max(o => o.Bytes);
                _body.Children.Add(Table(new[] { "Origin", "Bytes", "", "", "Functions" }, new[] { false, true, true, false, true },
                    r.Code.Where(o => o.Bytes > 0).Select(o => new Cell[]
                    {
                        o.Name, o.Bytes.ToString("N0", CultureInfo.CurrentCulture), Percent(o.Bytes, code),
                        new Cell(BarOf(o.Bytes, max, o.Name.StartsWith("fire:", StringComparison.Ordinal) ? BarFire : Bar)), o.Functions.ToString(CultureInfo.CurrentCulture),
                    })));
            }
            if (r.Data.Count > 0)
            {
                Section("Data");
                _body.Children.Add(Table(new[] { "Part", "Bytes" }, new[] { false, true },
                    r.Data.Select(o => new Cell[] { o.Name, o.Bytes.ToString("N0", CultureInfo.CurrentCulture) })));
            }
            if (r.Typing.Count > 0)
            {
                Section("fire functions by module: what is a C number");
                _body.Children.Add(Table(new[] { "Module", "Functions", "Bytes", "C interface", "Parameters", "Variables", "Stack slots" }, new[] { false, true, true, true, true, true, true },
                    r.Typing.Select(m => new Cell[]
                    {
                        m.Module, m.Functions.ToString(CultureInfo.CurrentCulture), m.Bytes.ToString("N0", CultureInfo.CurrentCulture), $"{m.WithCAbi} / {m.Functions}",
                        $"{m.CParams} / {m.Params}", $"{m.CLocals} / {m.CLocals + m.ValueLocals}", $"{m.StackSlots} (+{m.Shadows} C)",
                    })));
            }
            if (r.Biggest.Count > 0)
            {
                Section("The biggest functions");
                long max = r.Biggest.Max(b => b.Bytes);
                _body.Children.Add(Table(new[] { "Bytes", "", "Function", "Origin", "C" }, new[] { true, false, false, false, false },
                    r.Biggest.Select(b => new Cell[] { b.Bytes.ToString("N0", CultureInfo.CurrentCulture), new Cell(BarOf(b.Bytes, max, Bar)), b.What, b.Origin, b.CName })));
            }
            if (r.NoCAbi.Count > 0)
            {
                Section("Why functions have no C interface");
                _body.Children.Add(Table(new[] { "Reason", "Functions" }, new[] { false, true },
                    r.NoCAbi.OrderByDescending(kv => kv.Value).Select(kv => new Cell[] { kv.Key, kv.Value.ToString(CultureInfo.CurrentCulture) })));
            }
            if (r.Generated.Count > 0)
            {
                Section("Generated helpers");
                _body.Children.Add(Table(new[] { "Helpers", "Bytes" }, new[] { false, true },
                    r.Generated.OrderByDescending(kv => kv.Value).Select(kv => new Cell[] { kv.Key, kv.Value.ToString("N0", CultureInfo.CurrentCulture) })));
            }
        }

        /// <summary>A cell of a table: a text, or a control (a bar).</summary>
        private readonly record struct Cell(object Content)
        {
            public static implicit operator Cell(string text) => new(text);
        }

        private void Section(string title)
        {
            _body.Children.Add(new Border { Height = 1, Background = Rule, Margin = new Thickness(0, 14, 0, 2) });
            _body.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 2) });
        }

        private static Grid Table(string[] headers, bool[] right, IEnumerable<Cell[]> rows)
        {
            var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
            for (int c = 0; c < headers.Length; c++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            void Add(int row, int col, Control control)
            {
                Grid.SetRow(control, row);
                Grid.SetColumn(control, col);
                grid.Children.Add(control);
            }
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (int c = 0; c < headers.Length; c++)
                Add(0, c, new TextBlock { Text = headers[c], Opacity = 0.6, Margin = new Thickness(0, 0, 18, 3), HorizontalAlignment = right[c] ? HorizontalAlignment.Right : HorizontalAlignment.Left });
            int r = 1;
            foreach (var row in rows)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                for (int c = 0; c < row.Length && c < headers.Length; c++)
                {
                    Control control = row[c].Content as Control ?? new TextBlock { Text = row[c].Content as string ?? "" };
                    control.Margin = new Thickness(0, 1, 18, 1);
                    control.HorizontalAlignment = right[c] ? HorizontalAlignment.Right : HorizontalAlignment.Left;
                    control.VerticalAlignment = VerticalAlignment.Center;
                    Add(r, c, control);
                }
                r++;
            }
            return grid;
        }

        private static Control BarOf(long bytes, long max, IBrush brush) =>
            new Border { Width = Math.Max(1, 220.0 * bytes / Math.Max(1, max)), Height = 9, Background = brush, CornerRadius = new CornerRadius(2) };

        private static string Percent(long part, long whole) => whole <= 0 ? "" : (100.0 * part / whole).ToString("0.0", CultureInfo.CurrentCulture) + " %";

        private static string Bytes(long n) => n >= 10 * 1024
            ? $"{(n / 1024.0).ToString("0", CultureInfo.CurrentCulture)} KB ({n.ToString("N0", CultureInfo.CurrentCulture)} bytes)"
            : $"{n.ToString("N0", CultureInfo.CurrentCulture)} bytes";

        public string GetText() => "";
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
