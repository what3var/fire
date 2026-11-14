using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using fire.Compiler;
using fire.UI.Markup;

namespace fire.Editor
{
    /// <summary>
    /// The designer of data models (`.fdata`, docs/data/designer.md - ROADMAP_DATA step 12): the diagram in the middle (entities, enums and types as
    /// boxes with their fields, the relations as lines with their cardinality - drag a box to move it, click to select), the toolbox above (a new entity,
    /// enum or type; a relation: click the button, then the two entities), the properties of what is selected on the right (the fields of an entity as a
    /// grid), and below the problems the build would report (and names that are keywords of SQL) and the preview: the fire code forge makes, or the
    /// DDL for a database. With a database: import its tables as a model, make the tables of the model in it, or write the migration it lacks into the
    /// project. The text of the file is the model (DataModelDocument.ToJson).
    /// </summary>
    public sealed class DataModelEditorControl : UserControl, IDocumentView
    {
        // ---- the diagram ----------------------------------------------------------------------------------------------------------------------
        private sealed class DiagramView : Control
        {
            public DataModelDocument Model = new();
            public int Diagram;                   // the diagram shown (0: the main one - every box)
            public Dictionary<string, (double X, double Y)> Places => Model.PlacesOf(Diagram);
            public IEnumerable<string> Visible => Model.Names.Where(n => Model.Shows(Diagram, n));
            public string? Selected;              // the name of a box, or "#<index>" of a relation
            public string? Pending;               // the first entity of a relation being drawn
            public event Action<string?>? Picked;
            public event Action<string>? Moved;
            public event Action? DragEnded;
            private Point? _grab;
            private string? _dragging;
            private bool _moved;

            public const double BoxWidth = 230, Header = 26, Row = 18;
            private static readonly Typeface Font = new(FontFamily.Default);
            private static readonly Typeface Bold = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
            private static readonly IBrush EntityHead = EditorTheme.Solid(Color.FromRgb(0x5B, 0x21, 0x46));
            private static readonly IBrush EnumHead = EditorTheme.Solid(Color.FromRgb(0x1E, 0x4D, 0x5C));
            private static readonly IBrush ViewHead = EditorTheme.Solid(Color.FromRgb(0x4A, 0x3A, 0x6E));
            private static readonly IBrush TypeHead = EditorTheme.Solid(Color.FromRgb(0x3D, 0x4A, 0x1E));
            private static readonly IBrush Body = EditorTheme.Solid(Color.FromRgb(0x2B, 0x26, 0x31));
            private static readonly IBrush Line = EditorTheme.Solid(Color.FromRgb(0xC0, 0x6A, 0xDE));
            private static readonly IBrush SelectedLine = EditorTheme.Solid(Color.FromRgb(0xFF, 0x91, 0x42));

            public DiagramView() { Focusable = true; ClipToBounds = true; }

            public Rect BoxOf(string name)
            {
                var (x, y) = Places.TryGetValue(name, out var p) ? p : Model.Places.TryGetValue(name, out var m) ? m : (40, 40);
                int rows = 1;
                if (Model.Entity(name) is { } e) rows = Math.Max(1, e.Fields.Count + Navigations(name).Count);
                else if (Model.Enums.FirstOrDefault(n => n.Name == name) is { } en) rows = Math.Max(1, en.Values.Count);
                else if (Model.Types.FirstOrDefault(t => t.Name == name) is { } ty) rows = Math.Max(1, ty.Fields.Count);
                return new Rect(x, y, BoxWidth, Header + rows * Row + 8);
            }

            /// <summary>The navigation fields a relation gives an entity: (field, target, list).</summary>
            public List<(string Field, string Target, bool List)> Navigations(string entity)
            {
                var list = new List<(string, string, bool)>();
                foreach (var r in Model.Relations)
                {
                    if (r.From == entity && r.Navigation != null) list.Add((r.Navigation, r.To, r.Kind != "one-to-one"));
                    if (r.To == entity && r.Inverse != null) list.Add((r.Inverse, r.From, r.Kind == "many-to-many"));
                }
                return list;
            }

            public Size Extent()
            {
                double w = 800, h = 600;
                foreach (var n in Visible) { var b = BoxOf(n); w = Math.Max(w, b.Right + 200); h = Math.Max(h, b.Bottom + 200); }
                return new Size(w, h);
            }
            protected override Size MeasureOverride(Size availableSize) => Extent();

            private static FormattedText Text(string text, IBrush brush, double size = 12, bool bold = false) =>
                new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, bold ? Bold : Font, size, brush);

            // (where a line from the middle of a box towards a point leaves the box)
            private static Point Edge(Rect box, Point toward)
            {
                var c = box.Center;
                double dx = toward.X - c.X, dy = toward.Y - c.Y;
                if (Math.Abs(dx) < 0.01 && Math.Abs(dy) < 0.01) return c;
                double sx = Math.Abs(dx) > 0.01 ? box.Width / 2 / Math.Abs(dx) : double.MaxValue;
                double sy = Math.Abs(dy) > 0.01 ? box.Height / 2 / Math.Abs(dy) : double.MaxValue;
                double s = Math.Min(sx, sy);
                return new Point(c.X + dx * s, c.Y + dy * s);
            }

            public (Point A, Point B) LineOf(DmRelation r)
            {
                var a = BoxOf(r.From); var b = BoxOf(r.To);
                if (r.From == r.To) return (new Point(a.Right, a.Top + 10), new Point(a.Right, a.Top + 40));
                return (Edge(a, b.Center), Edge(b, a.Center));
            }

            public override void Render(DrawingContext context)
            {
                context.FillRectangle(EditorTheme.DarkSurface, new Rect(Bounds.Size));
                // the relations under the boxes
                for (int i = 0; i < Model.Relations.Count; i++)
                {
                    var r = Model.Relations[i];
                    if (Model.Entity(r.From) == null || Model.Entity(r.To) == null || !Model.Shows(Diagram, r.From) || !Model.Shows(Diagram, r.To)) continue;
                    var (a, b) = LineOf(r);
                    var pen = new Pen(Selected == "#" + i ? SelectedLine : Line, Selected == "#" + i ? 2.5 : 1.5);
                    if (r.From == r.To)
                    {
                        var box = BoxOf(r.From);
                        var g = new StreamGeometry();
                        using (var c = g.Open()) { c.BeginFigure(a, false); c.CubicBezierTo(new Point(box.Right + 60, a.Y - 20), new Point(box.Right + 60, b.Y + 20), b); c.EndFigure(false); }
                        context.DrawGeometry(null, pen, g);
                    }
                    else context.DrawLine(pen, a, b);
                    string near = "1", far = "*";
                    if (r.Kind == "many-to-many") near = "*";
                    if (r.Kind == "one-to-one") far = "1";
                    context.DrawText(Text(near, Line, 12, true), new Point(a.X + 4, a.Y - 16));
                    context.DrawText(Text(far, Line, 12, true), new Point(b.X + 4, b.Y - 16));
                }
                foreach (var e in Model.Entities.Where(x => Model.Shows(Diagram, x.Name))) DrawBox(context, e.Name, e.View != null ? ViewHead : EntityHead, e.View != null ? "view " + Model.TableOf(e) : Model.TableOf(e),
                    e.Fields.Select(f => (Describe(f), f.Key ? EditorTheme.Yellow : EditorTheme.Text))
                        .Concat(Navigations(e.Name).Select(n => ((n.List ? "  " + n.Field + " : List<" + n.Target + ">" : "  " + n.Field + " : " + n.Target), EditorTheme.Purple))).ToList());
                foreach (var en in Model.Enums.Where(x => Model.Shows(Diagram, x.Name))) DrawBox(context, en.Name, EnumHead, "enum", en.Values.Select(v => ("  " + v, EditorTheme.Text)).ToList());
                foreach (var t in Model.Types.Where(x => Model.Shows(Diagram, x.Name))) DrawBox(context, t.Name, TypeHead, "record (JSON)", t.Fields.Select(f => (Describe(f), EditorTheme.Text)).ToList());
                if (Pending != null)
                    context.DrawText(Text("Relation: now click the second entity (Esc: cancel) - from " + Pending, SelectedLine, 12), new Point(10, 6));
            }

            private static string Describe(DmField f)
            {
                string mark = f.Key ? "🔑 " : f.Required ? "• " : "  ";
                string type = f.Type == "string" && f.Length > 0 ? $"string({f.Length})" : f.Type;
                var extra = new List<string>();
                if (f.Unique) extra.Add("unique");
                else if (f.Index) extra.Add("index");
                if (f.Version) extra.Add("version");
                if (f.Json) extra.Add("json");
                return mark + f.Name + " : " + type + (extra.Count > 0 ? "  [" + string.Join(", ", extra) + "]" : "");
            }

            private void DrawBox(DrawingContext context, string name, IBrush head, string sub, List<(string Text, IBrush Brush)> rows)
            {
                var box = BoxOf(name);
                bool selected = Selected == name || Pending == name;
                context.DrawRectangle(Body, new Pen(selected ? SelectedLine : EditorTheme.Border, selected ? 2 : 1), box, 6, 6);
                context.DrawRectangle(head, null, new Rect(box.X, box.Y, box.Width, Header), 6, 6);
                context.DrawText(Text(name, EditorTheme.Text, 13, true), new Point(box.X + 8, box.Y + 5));
                var subText = Text(sub, EditorTheme.TextDim, 10);
                context.DrawText(subText, new Point(box.Right - subText.Width - 8, box.Y + 7));
                double y = box.Y + Header + 4;
                foreach (var (text, brush) in rows)
                {
                    context.DrawText(Text(text, brush, 11.5), new Point(box.X + 8, y));
                    y += Row;
                }
            }

            /// <summary>What is under a point: a box (the last drawn first), a relation near its line, or null.</summary>
            public string? HitTest(Point p)
            {
                foreach (var n in Visible.Reverse()) if (BoxOf(n).Contains(p)) return n;
                for (int i = 0; i < Model.Relations.Count; i++)
                {
                    var r = Model.Relations[i];
                    if (Model.Entity(r.From) == null || Model.Entity(r.To) == null || !Model.Shows(Diagram, r.From) || !Model.Shows(Diagram, r.To)) continue;
                    var (a, b) = LineOf(r);
                    if (Distance(p, a, b) < 6) return "#" + i;
                }
                return null;
            }

            private static double Distance(Point p, Point a, Point b)
            {
                double dx = b.X - a.X, dy = b.Y - a.Y, len = dx * dx + dy * dy;
                double t = len == 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len, 0, 1);
                double x = a.X + t * dx - p.X, y = a.Y + t * dy - p.Y;
                return Math.Sqrt(x * x + y * y);
            }

            protected override void OnPointerPressed(PointerPressedEventArgs e)
            {
                base.OnPointerPressed(e);
                Focus();
                var p = e.GetPosition(this);
                string? hit = HitTest(p);
                Picked?.Invoke(hit);
                if (hit != null && !hit.StartsWith('#') && Pending == null)
                {
                    var box = BoxOf(hit);
                    _dragging = hit;
                    _grab = new Point(p.X - box.X, p.Y - box.Y);
                    _moved = false;
                    e.Pointer.Capture(this);
                }
            }

            protected override void OnPointerMoved(PointerEventArgs e)
            {
                base.OnPointerMoved(e);
                if (_dragging == null || _grab is not { } g) return;
                var p = e.GetPosition(this);
                double x = Math.Max(0, Math.Round((p.X - g.X) / 10) * 10), y = Math.Max(0, Math.Round((p.Y - g.Y) / 10) * 10);
                if (Places.TryGetValue(_dragging, out var old) && old.X == x && old.Y == y) return;
                Places[_dragging] = (x, y);
                _moved = true;
                Moved?.Invoke(_dragging);
                InvalidateMeasure();
                InvalidateVisual();
            }

            protected override void OnPointerReleased(PointerReleasedEventArgs e)
            {
                base.OnPointerReleased(e);
                if (_dragging != null && _moved) DragEnded?.Invoke();
                _dragging = null;
                _grab = null;
                e.Pointer.Capture(null);
            }
        }

        // ---- the control ------------------------------------------------------------------------------------------------------------------------
        private DataModelDocument _model = new();
        private string _saved = "";
        private string? _rawText;                 // a file that is no model: kept as it is
        private readonly DiagramView _diagram = new();
        private readonly StackPanel _properties = new() { Spacing = 6, Margin = new Thickness(10) };
        private readonly ListBox _problems = new();
        private readonly TextBox _preview = new() { IsReadOnly = true, AcceptsReturn = true, FontFamily = new FontFamily("Cascadia Mono,Consolas,DejaVu Sans Mono,monospace"), FontSize = 12 };
        private readonly ComboBox _previewKind = new() { MinWidth = 170 };
        private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), Opacity = 0.8 };
        private readonly Stack<string> _undo = new(), _redo = new();
        private string? _pendingKind;            // the kind of a relation being drawn
        private DmField? _selectedField;
        private DispatcherTimer? _previewTimer;
        private int _previewVersion;

        public string? FilePath { get; set; }
        public bool IsReadOnly => false;
        public bool IsModified => GetText() != _saved;
        public event Action? ModifiedChanged;
        public event Action<int>? CaretLineChanged { add { } remove { } }

        /// <summary>The model being edited (tests, the main window).</summary>
        public DataModelDocument Model => _model;

        public DataModelEditorControl()
        {
            var bar = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 2) };
            Button Tool(string text, string tip, Action click)
            {
                var b = new Button { Content = text, Margin = new Thickness(1, 0) };
                b.Classes.Add("tool");
                ToolTip.SetTip(b, tip);
                b.Click += (_, _) => click();
                bar.Children.Add(b);
                return b;
            }
            Tool("+ Entity", "A new entity (a table) with a key id", () => Change(() => { var at = FreePlace(); var e = _model.AddEntity(null, at.X, at.Y); Placed(e.Name); Select(e.Name); }));
            Tool("+ Enum", "A new enum (stored by the name of its value)", () => Change(() => { var at = FreePlace(); var e = _model.AddEnum(null, at.X, at.Y); Placed(e.Name); Select(e.Name); }));
            Tool("+ Type", "A new record type, stored as JSON in a column", () => Change(() => { var at = FreePlace(); var t = _model.AddType(null, at.X, at.Y); Placed(t.Name); Select(t.Name); }));
            Tool("1 : 1", "A one-to-one relation: click the entity that has the other, then the other (it gets the unique foreign key)", () => StartRelation("one-to-one"));
            Tool("1 : n", "A one-to-many relation: click the 'one' entity, then the 'many' entity", () => StartRelation("one-to-many"));
            Tool("n : m", "A many-to-many relation (with a join table): click the two entities", () => StartRelation("many-to-many"));
            Tool("Delete", "Delete what is selected (Del)", DeleteSelected);
            bar.Children.Add(new Border { Width = 12 });
            _diagramChoice.SelectionChanged += (_, _) =>
            {
                if (_refreshingDiagrams || _diagramChoice.SelectedIndex < 0) return;
                _diagram.Diagram = _diagramChoice.SelectedIndex;
                Select(null);
                _diagram.InvalidateMeasure();
                _diagram.InvalidateVisual();
            };
            ToolTip.SetTip(_diagramChoice, "The diagram shown: Main has every box, the others the boxes added to them");
            bar.Children.Add(_diagramChoice);
            Tool("+ Diagram", "A new diagram (empty: add boxes with 'Show box…')", () => Change(() => { _model.AddDiagram(); _diagram.Diagram = _model.Diagrams.Count - 1; }));
            Tool("Show box…", "Show a box of the model in this diagram", () => _ = ShowBoxAsync());
            Tool("Hide box", "Take the selected box out of this diagram (it stays in the model)", HideSelected);
            Tool("Rename diagram…", "Rename this diagram", () => _ = RenameDiagramAsync());
            Tool("Delete diagram", "Delete this diagram (not the main one; the boxes stay in the model)", () =>
            {
                int d = _diagram.Diagram;
                if (d <= 0) { _status.Text = "The main diagram stays."; return; }
                Change(() => { _model.Diagrams.RemoveAt(d); _diagram.Diagram = 0; });
            });
            bar.Children.Add(new Border { Width = 12 });
            Tool("Import from database…", "Read the tables of a database into the model (it replaces the model; Undo brings it back)", () => _ = ImportAsync());
            Tool("Generate database…", "Make the tables of the model in a database (what is missing - nothing is dropped)", () => _ = CreateDatabaseAsync());
            Tool("Generate migration…", "Write the migration that a database lacks of the model into the project (forge data diff)", () => _ = MigrationAsync());
            bar.Children.Add(_status);

            _previewKind.ItemsSource = new[] { "fire code" }.Concat(DataModelTools.Dialects.Select(d => "DDL: " + d.Name)).ToList();
            _previewKind.SelectedIndex = 0;
            _previewKind.SelectionChanged += (_, _) => SchedulePreview();
            _problems.DoubleTapped += (_, _) => { };

            _diagram.Picked += OnPicked;
            _diagram.Moved += _ => ModifiedChanged?.Invoke();
            _diagram.DragEnded += () => { PushUndoOf(_beforeDrag); _beforeDrag = null; };
            _diagram.PointerPressed += (_, _) => { _beforeDrag ??= _model.ToJson(); };
            _diagram.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Delete) { DeleteSelected(); e.Handled = true; }
                else if (e.Key == Key.Escape) { CancelRelation(); e.Handled = true; }
            };

            var diagramScroll = new ScrollViewer { Content = _diagram, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var propertiesScroll = new ScrollViewer { Content = _properties, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = EditorTheme.Background };
            var previewDock = new DockPanel();
            var previewBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(4), Children = { new TextBlock { Text = "Show:", VerticalAlignment = VerticalAlignment.Center }, _previewKind } };
            DockPanel.SetDock(previewBar, Avalonia.Controls.Dock.Top);
            previewDock.Children.Add(previewBar);
            previewDock.Children.Add(_preview);
            var tabs = new TabControl
            {
                ItemsSource = new[]
                {
                    new TabItem { Header = "Problems", Content = _problems },
                    new TabItem { Header = "Preview", Content = previewDock },
                },
            };
            tabs.SelectionChanged += (_, _) => SchedulePreview();

            var upper = new Grid { ColumnDefinitions = new ColumnDefinitions("*,4,360") };
            upper.Children.Add(diagramScroll);
            var split1 = new GridSplitter { ResizeDirection = GridResizeDirection.Columns, Background = EditorTheme.Border };
            Grid.SetColumn(split1, 1); upper.Children.Add(split1);
            Grid.SetColumn(propertiesScroll, 2); upper.Children.Add(propertiesScroll);
            var main = new Grid { RowDefinitions = new RowDefinitions("*,4,220") };
            main.Children.Add(upper);
            var split2 = new GridSplitter { ResizeDirection = GridResizeDirection.Rows, Background = EditorTheme.Border };
            Grid.SetRow(split2, 1); main.Children.Add(split2);
            Grid.SetRow(tabs, 2); main.Children.Add(tabs);
            var dock = new DockPanel();
            DockPanel.SetDock(bar, Avalonia.Controls.Dock.Top);
            dock.Children.Add(bar);
            dock.Children.Add(main);
            Content = dock;
            Focusable = true;
            Refresh();
        }

        private string? _beforeDrag;
        private readonly ComboBox _diagramChoice = new() { MinWidth = 120 };
        private bool _refreshingDiagrams;

        // ---- IDocumentView ----
        public string GetText() => _rawText ?? _model.ToJson();
        public int GetCaretLine() => 1;
        public int LineCount => GetText().Split('\n').Length;

        public void ResetTo(string text, string? filePath)
        {
            FilePath = filePath;
            _undo.Clear(); _redo.Clear();
            try
            {
                _model = DataModelDocument.Parse(text);
                _rawText = null;
                if (string.IsNullOrWhiteSpace(text)) { _model.Namespace = filePath != null ? Path.GetFileNameWithoutExtension(filePath) : "Model"; }
            }
            catch (MarkupException ex)
            {
                // (no model: the text stays as it is, the designer says why)
                _model = new DataModelDocument();
                _rawText = text;
                _status.Text = "This file is no data model: " + ex.Message;
            }
            _diagram.Model = _model;
            _saved = GetText();
            Select(null);
            Refresh();
            ModifiedChanged?.Invoke();
        }

        public void MarkSaved() { _saved = GetText(); ModifiedChanged?.Invoke(); }
        public void FocusEditor() => _diagram.Focus();
        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;
        public bool HasSelection => _diagram.Selected != null && !_diagram.Selected.StartsWith('#');
        public void Undo() { if (_undo.Count == 0) return; _redo.Push(_model.ToJson()); Load(_undo.Pop()); }
        public void Redo() { if (_redo.Count == 0) return; _undo.Push(_model.ToJson()); Load(_redo.Pop()); }
        public void Cut()
        {
            if (CopySelected()) DeleteSelected();
        }
        public void Copy() => CopySelected();
        public void Paste() => _ = PasteAsync();
        // (the selected box as text on the clipboard - a model of its own, so it can go into another model)
        private bool CopySelected()
        {
            string? s = _diagram.Selected;
            if (s == null || s.StartsWith('#') || _model.CopyText(s) is not { } text) return false;
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) _ = clipboard.SetTextAsync(text);
            _status.Text = "Copied: " + s;
            return true;
        }
        private async Task PasteAsync()
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
            string? text = null;
            try { text = await clipboard.TryGetTextAsync(); } catch (Exception) { }
            if (string.IsNullOrWhiteSpace(text)) return;
            var at = FreePlace();
            List<string> added = new();
            Change(() => added = _model.Paste(text, at.X, at.Y, _diagram.Diagram));
            if (added.Count == 0) { _status.Text = "The clipboard holds no part of a data model."; return; }
            Select(added[0]);
            _status.Text = "Pasted: " + string.Join(", ", added);
        }
        public void Delete() => DeleteSelected();
        public void SelectAll() { }
        public void Find() { }
        public void FindNext() { }
        public void FindPrevious() { }
        public void GoToLine(int line) { }

        private void Load(string json)
        {
            string? selected = _diagram.Selected;
            _model = DataModelDocument.Parse(json);
            _diagram.Model = _model;
            _rawText = null;
            Select(selected != null && (_model.Names.Contains(selected) || selected.StartsWith('#')) ? selected : null);
            Refresh();
            ModifiedChanged?.Invoke();
        }

        // ---- changes ----
        /// <summary>A change of the model (one step of Undo).</summary>
        private void Change(Action change)
        {
            string before = _model.ToJson();
            _rawText = null;
            change();
            if (_model.ToJson() == before) return;
            PushUndoOf(before);
            Refresh();
            ModifiedChanged?.Invoke();
        }
        private void PushUndoOf(string? before)
        {
            if (before == null || before == _model.ToJson()) return;
            _undo.Push(before);
            _redo.Clear();
            RefreshProblems();
            SchedulePreview();
        }

        private (double X, double Y) FreePlace()
        {
            // (the first free place of a grid, to the right of and below the boxes that are there)
            for (int row = 0; row < 50; row++)
                for (int col = 0; col < 4; col++)
                {
                    var r = new Rect(40 + col * 280, 40 + row * 220, DiagramView.BoxWidth, 180);
                    if (!_diagram.Visible.Any(n => _diagram.BoxOf(n).Intersects(r))) return (r.X, r.Y);
                }
            return (40, 40);
        }

        // (a new box is also in the diagram that is shown)
        private void Placed(string name)
        {
            int d = _diagram.Diagram;
            if (d > 0 && d < _model.Diagrams.Count) _model.Diagrams[d].Shapes[name] = _model.Places[name];
        }

        private void HideSelected()
        {
            string? s = _diagram.Selected;
            int d = _diagram.Diagram;
            if (d <= 0) { _status.Text = "The main diagram shows every box - delete it, or hide it in another diagram."; return; }
            if (s == null || s.StartsWith('#')) { _status.Text = "Select a box first."; return; }
            Change(() => _model.Diagrams[d].Shapes.Remove(s));
            Select(null);
        }

        private async Task ShowBoxAsync()
        {
            int d = _diagram.Diagram;
            if (d <= 0) { _status.Text = "The main diagram shows every box already."; return; }
            var hidden = _model.Names.Where(n => !_model.Shows(d, n)).ToList();
            if (hidden.Count == 0) { _status.Text = "This diagram shows every box of the model."; return; }
            if (await AskChoice("Show a box", "The box to show in '" + _model.Diagrams[d].Name + "'", hidden) is not { } name) return;
            var at = FreePlace();
            Change(() => _model.Diagrams[d].Shapes[name] = at);
            Select(name);
        }

        private async Task RenameDiagramAsync()
        {
            int d = Math.Clamp(_diagram.Diagram, 0, _model.Diagrams.Count - 1);
            if (await AskText("Rename the diagram", "Name", _model.Diagrams[d].Name) is not { } name || name.Trim() == "") return;
            if (_model.Diagrams.Any(x => x.Name == name.Trim())) { _status.Text = "There is a diagram of that name."; return; }
            Change(() => _model.Diagrams[d].Name = name.Trim());
        }

        // (a small dialog: a text, or one of a list; null: cancelled)
        private async Task<string?> AskText(string title, string label, string value) => await Ask(title, label, value, null);
        private async Task<string?> AskChoice(string title, string label, List<string> items) => await Ask(title, label, "", items);
        private async Task<string?> Ask(string title, string label, string value, List<string>? items)
        {
            if (TopLevel.GetTopLevel(this) is not Window owner) return null;
            var box = new TextBox { Text = value };
            var list = new ComboBox { ItemsSource = items, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
            var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80 };
            var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
            var panel = new StackPanel { Spacing = 6, Margin = new Thickness(14) };
            panel.Children.Add(new TextBlock { Text = label });
            panel.Children.Add(items == null ? box : list);
            panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0), Children = { ok, cancel } });
            var dialog = new Window { Title = title, Width = 380, SizeToContent = SizeToContent.Height, Content = panel, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = EditorTheme.Background };
            bool accepted = false;
            ok.Click += (_, _) => { accepted = true; dialog.Close(); };
            cancel.Click += (_, _) => dialog.Close();
            await dialog.ShowDialog(owner);
            if (!accepted) return null;
            return items == null ? box.Text ?? "" : list.SelectedItem as string;
        }

        private void StartRelation(string kind)
        {
            if (_model.Entities.Count == 0) { _status.Text = "A relation needs entities."; return; }
            _pendingKind = kind;
            _diagram.Pending = null;
            _status.Text = "Relation " + kind + ": click the first entity.";
            _diagram.Focus();
            _diagram.InvalidateVisual();
        }
        private void CancelRelation()
        {
            _pendingKind = null;
            _diagram.Pending = null;
            _status.Text = "";
            _diagram.InvalidateVisual();
        }

        private void OnPicked(string? hit)
        {
            if (_pendingKind != null)
            {
                if (hit == null || _model.Entity(hit) == null) { _status.Text = "Click an entity (Esc: cancel)."; return; }
                if (_diagram.Pending == null)
                {
                    _diagram.Pending = hit;
                    _status.Text = "Relation " + _pendingKind + " from " + hit + ": click the second entity.";
                    _diagram.InvalidateVisual();
                    return;
                }
                string first = _diagram.Pending, kind = _pendingKind;
                CancelRelation();
                Change(() => { _model.AddRelation(kind, first, hit); Select("#" + (_model.Relations.Count - 1)); });
                return;
            }
            Select(hit);
        }

        private void DeleteSelected()
        {
            string? s = _diagram.Selected;
            if (s == null) return;
            Change(() =>
            {
                if (s.StartsWith('#') && int.TryParse(s.AsSpan(1), out int i) && i < _model.Relations.Count) _model.Relations.RemoveAt(i);
                else _model.Remove(s);
            });
            Select(null);
        }

        private void Select(string? what)
        {
            _diagram.Selected = what;
            _selectedField = null;
            BuildProperties();
            _diagram.InvalidateVisual();
        }

        private void Refresh()
        {
            // (the diagrams of the model in the choice; the one shown stays when it is there)
            _refreshingDiagrams = true;
            _diagram.Diagram = Math.Clamp(_diagram.Diagram, 0, _model.Diagrams.Count - 1);
            _diagramChoice.ItemsSource = _model.Diagrams.Select(d => d.Name).ToList();
            _diagramChoice.SelectedIndex = _diagram.Diagram;
            _refreshingDiagrams = false;
            _diagram.InvalidateMeasure();
            _diagram.InvalidateVisual();
            BuildProperties();
            RefreshProblems();
            SchedulePreview();
        }

        // ---- the problems ----
        private static readonly HashSet<string> SqlWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "order", "user", "group", "select", "table", "index", "key", "from", "where", "join", "check", "column", "default", "primary", "references", "values",
            "date", "time", "timestamp", "limit", "offset", "desc", "asc", "by", "case", "end", "level", "plan", "rows", "session", "range", "option",
        };

        /// <summary>The problems of the model: those of the build, then names that are words of SQL (quoted by the dialects - legal, but worth a look).</summary>
        public List<string> ProblemTexts()
        {
            var list = _model.Problems(FilePath ?? "Model.fdata").Select(d => "error: " + d).ToList();
            foreach (var e in _model.Entities)
            {
                string table = _model.TableOf(e);
                if (SqlWords.Contains(table)) list.Add($"note: the table '{table}' of '{e.Name}' is a word of SQL - it is quoted, but hand-written SQL must quote it too.");
                foreach (var f in e.Fields)
                    if (SqlWords.Contains(f.Column ?? f.Name)) list.Add($"note: the column '{f.Column ?? f.Name}' of '{e.Name}' is a word of SQL - it is quoted, but hand-written SQL must quote it too.");
            }
            return list;
        }

        private void RefreshProblems()
        {
            var list = ProblemTexts();
            _problems.ItemsSource = list.Count == 0 ? new List<string> { "No problems: a build makes the code of this model." } : list;
        }

        // ---- the preview ----
        private void SchedulePreview()
        {
            _previewTimer?.Stop();
            _previewTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _previewTimer.Tick -= PreviewTick;
            _previewTimer.Tick += PreviewTick;
            _previewTimer.Start();
        }
        private void PreviewTick(object? sender, EventArgs e)
        {
            _previewTimer?.Stop();
            int kind = _previewKind.SelectedIndex;
            string text = _model.ToJson(), path = FilePath ?? "Model.fdata";
            int version = ++_previewVersion;
            if (kind <= 0)
            {
                _preview.Text = _model.GeneratedCode(path) ?? "// The model has problems (see Problems): no code.";
                return;
            }
            _preview.Text = "-- making the DDL ...";
            Task.Run(() => DataModelTools.Ddl(text, path, kind - 1)).ContinueWith(t => Dispatcher.UIThread.Post(() =>
            {
                if (version != _previewVersion) return;
                var (ddl, error) = t.Result;
                _preview.Text = error != null ? "-- " + error.Replace("\n", "\n-- ") : ddl;
            }));
        }

        // ---- the properties ----
        private static TextBlock Label(string text) => new() { Text = text, Opacity = 0.75, Margin = new Thickness(0, 4, 0, 0) };
        private static TextBlock Heading(string text) => new() { Text = text, FontSize = 15, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 4) };

        private TextBox Field(string label, string? value, Action<string> commit, string? watermark = null)
        {
            _properties.Children.Add(Label(label));
            var box = new TextBox { Text = value ?? "", PlaceholderText = watermark };
            void Commit() { if ((box.Text ?? "") != (value ?? "")) { string v = box.Text ?? ""; value = v; commit(v); } }
            box.LostFocus += (_, _) => Commit();
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
            _properties.Children.Add(box);
            return box;
        }
        private ComboBox Choice(string label, IReadOnlyList<string> items, string value, Action<string> commit)
        {
            _properties.Children.Add(Label(label));
            var combo = new ComboBox { ItemsSource = items, SelectedItem = items.Contains(value) ? value : null, HorizontalAlignment = HorizontalAlignment.Stretch };
            combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is string s && s != value) { value = s; commit(s); } };
            _properties.Children.Add(combo);
            return combo;
        }
        private CheckBox Flag(string label, bool value, Action<bool> commit, Panel? into = null)
        {
            var box = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 0, 10, 0) };
            box.IsCheckedChanged += (_, _) => commit(box.IsChecked == true);
            (into ?? _properties).Children.Add(box);
            return box;
        }
        private static string? Null(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        private void BuildProperties()
        {
            _properties.Children.Clear();
            string? s = _diagram.Selected;
            if (_rawText != null)
            {
                _properties.Children.Add(new TextBlock { Text = "This file is no data model - open it as text to fix it.", TextWrapping = TextWrapping.Wrap });
                return;
            }
            if (s == null)
            {
                _properties.Children.Add(Heading("Data model"));
                Field("Namespace", _model.Namespace, v => Change(() => _model.Namespace = v.Trim()));
                Choice("Table names", new[] { "name", "plural", "snake", "snake_plural" }, _model.TableNaming, v => Change(() => _model.TableNaming = v));
                Choice("Column names", new[] { "field", "snake" }, _model.ColumnNaming, v => Change(() => _model.ColumnNaming = v));
                _properties.Children.Add(new TextBlock { Text = $"{_model.Entities.Count} entities, {_model.Relations.Count} relations, {_model.Enums.Count} enums, {_model.Types.Count} types.", Opacity = 0.7, Margin = new Thickness(0, 10, 0, 0) });
                return;
            }
            if (s.StartsWith('#') && int.TryParse(s.AsSpan(1), out int ri) && ri < _model.Relations.Count) { RelationProperties(_model.Relations[ri]); return; }
            if (_model.Entity(s) is { } e) { EntityProperties(e); return; }
            if (_model.Enums.FirstOrDefault(n => n.Name == s) is { } en) { EnumProperties(en); return; }
            if (_model.Types.FirstOrDefault(t => t.Name == s) is { } ty) TypeProperties(ty);
        }

        private void Rename(string old, string name)
        {
            name = name.Trim();
            bool ok = true;
            Change(() => ok = _model.Rename(old, name));
            if (!ok) { _status.Text = $"'{name}' is no free name."; BuildProperties(); return; }
            Select(name);
        }

        private void EntityProperties(DmEntity e)
        {
            _properties.Children.Add(Heading("Entity " + e.Name));
            Field("Name", e.Name, v => Rename(e.Name, v));
            Field("Table", e.Table, v => Change(() => e.Table = Null(v)), _model.TableOf(new DmEntity { Name = e.Name }));
            Field("Query in the Context", e.Set, v => Change(() => e.Set = Null(v)), DataModelGenerator.Plural(e.Name));
            Field("Comment", e.Comment, v => Change(() => e.Comment = Null(v)));
            Flag("a record (not a class)", e.Record, v => Change(() => e.Record = v));
            _properties.Children.Add(Label("View: its query (empty: a table)"));
            var view = new TextBox { Text = e.View ?? "", AcceptsReturn = true, MinHeight = 50, TextWrapping = TextWrapping.Wrap, PlaceholderText = "SELECT ... FROM ..." };
            view.LostFocus += (_, _) => { string? v = Null(view.Text ?? ""); if (v != e.View) Change(() => e.View = v); };
            _properties.Children.Add(view);
            if (e.View == null)
            {
                _properties.Children.Add(Label("Checks of the table (one condition per line)"));
                var checks = new TextBox { Text = string.Join("\n", e.Checks), AcceptsReturn = true, MinHeight = 40, PlaceholderText = "endsAt > startsAt" };
                checks.LostFocus += (_, _) =>
                {
                    var list = (checks.Text ?? "").Split('\n').Select(c => c.Trim()).Where(c => c != "").ToList();
                    if (!list.SequenceEqual(e.Checks)) Change(() => { e.Checks.Clear(); e.Checks.AddRange(list); });
                };
                _properties.Children.Add(checks);
            }
            FieldGrid(e.Fields, true);
            var nav = _diagram.Navigations(e.Name);
            if (nav.Count > 0)
            {
                _properties.Children.Add(Label("Navigation fields (from the relations)"));
                foreach (var n in nav) _properties.Children.Add(new TextBlock { Text = "  " + n.Field + " : " + (n.List ? "List<" + n.Target + ">" : n.Target), Foreground = EditorTheme.Purple });
            }
        }

        private void TypeProperties(DmType t)
        {
            _properties.Children.Add(Heading("Type " + t.Name + " (a record stored as JSON)"));
            Field("Name", t.Name, v => Rename(t.Name, v));
            Field("Comment", t.Comment, v => Change(() => t.Comment = Null(v)));
            FieldGrid(t.Fields, false);
        }

        private void EnumProperties(DmEnum en)
        {
            _properties.Children.Add(Heading("Enum " + en.Name));
            Field("Name", en.Name, v => Rename(en.Name, v));
            Field("Comment", en.Comment, v => Change(() => en.Comment = Null(v)));
            _properties.Children.Add(Label("Values (one per line)"));
            var values = new TextBox { Text = string.Join("\n", en.Values), AcceptsReturn = true, MinHeight = 90 };
            values.LostFocus += (_, _) =>
            {
                var list = (values.Text ?? "").Split('\n').Select(v => v.Trim()).Where(v => v != "").ToList();
                if (!list.SequenceEqual(en.Values)) Change(() => { en.Values.Clear(); en.Values.AddRange(list); });
            };
            _properties.Children.Add(values);
        }

        private void RelationProperties(DmRelation r)
        {
            _properties.Children.Add(Heading("Relation " + r.From + " → " + r.To));
            var entities = _model.Entities.Select(x => x.Name).ToList();
            Choice("Kind", new[] { "one-to-one", "one-to-many", "many-to-many" }, r.Kind, v => Change(() => r.Kind = v));
            Choice(r.Kind == "one-to-many" ? "From (the one)" : "From", entities, r.From, v => Change(() => r.From = v));
            Choice(r.Kind == "one-to-many" ? "To (the many)" : r.Kind == "one-to-one" ? "To (has the foreign key)" : "To", entities, r.To, v => Change(() => r.To = v));
            Field(r.Kind == "one-to-one" ? "Navigation (the object in " + r.From + ")" : "Navigation (the list in " + r.From + ")", r.Navigation, v => Change(() => r.Navigation = Null(v)));
            Field(r.Kind == "many-to-many" ? "Inverse (the list in " + r.To + ")" : "Inverse (the object in " + r.To + ")", r.Inverse, v => Change(() => r.Inverse = Null(v)));
            if (r.Kind != "many-to-many")
                Field("Foreign key (a field of " + r.To + (r.Kind == "one-to-one" ? ", unique" : "") + ")", r.ForeignKey, v => Change(() => r.ForeignKey = Null(v)), LowerFirst(r.From) + "Id");
            else
            {
                Field("Join table", r.JoinTable, v => Change(() => r.JoinTable = Null(v)), LowerFirst(r.From) + "_" + LowerFirst(r.To));
                Field("Column of " + r.From, r.FromColumn, v => Change(() => r.FromColumn = Null(v)), LowerFirst(r.From) + "Id");
                Field("Column of " + r.To, r.ToColumn, v => Change(() => r.ToColumn = Null(v)), LowerFirst(r.To) + "Id");
            }
        }
        private static string LowerFirst(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s.Substring(1);

        /// <summary>The fields as a grid: name, type, key, required, length, unique, index; below the rest of the field that is selected.</summary>
        private void FieldGrid(List<DmField> fields, bool entity)
        {
            _properties.Children.Add(Label("Fields"));
            var types = DataModelDocument.FieldTypes.Concat(_model.Enums.Select(x => x.Name)).Concat(_model.Types.Select(x => x.Name)).ToList();
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(entity ? "*,96,28,28,48,28,28,56" : "*,96,28,48,56"), RowSpacing = 2 };
            int row = 0;
            void Header(params string[] names)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                for (int c = 0; c < names.Length; c++)
                {
                    var t = new TextBlock { Text = names[c], FontSize = 10, Opacity = 0.7, HorizontalAlignment = c == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Center };
                    ToolTip.SetTip(t, names[c] switch { "K" => "Key", "R" => "Required (NOT NULL)", "U" => "Unique", "I" => "Index", "Len" => "Length (0: none)", _ => null });
                    Grid.SetColumn(t, c); Grid.SetRow(t, row); grid.Children.Add(t);
                }
                row++;
            }
            if (entity) Header("Name", "Type", "K", "R", "Len", "U", "I", ""); else Header("Name", "Type", "R", "Len", "");
            foreach (var f in fields)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                int col = 0;
                void Put(Control c) { Grid.SetColumn(c, col++); Grid.SetRow(c, row); grid.Children.Add(c); }
                var name = new TextBox { Text = f.Name, Padding = new Thickness(4, 2), FontWeight = _selectedField == f ? FontWeight.SemiBold : FontWeight.Normal };
                name.GotFocus += (_, _) => { if (_selectedField != f) { _selectedField = f; Dispatcher.UIThread.Post(() => { BuildProperties(); }); } };
                name.LostFocus += (_, _) => { if (name.Text != f.Name && !string.IsNullOrWhiteSpace(name.Text)) Change(() => f.Name = name.Text!.Trim()); };
                Put(name);
                var type = new ComboBox { ItemsSource = types, SelectedItem = types.Contains(f.Type) ? f.Type : null, HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(4, 2) };
                type.SelectionChanged += (_, _) => { if (type.SelectedItem is string t && t != f.Type) Change(() => { f.Type = t; if (_model.Types.Any(x => x.Name == t)) f.Json = true; }); };
                Put(type);
                CheckBox Check(bool v, Action<bool> set) { var c = new CheckBox { IsChecked = v, HorizontalAlignment = HorizontalAlignment.Center, MinWidth = 0 }; c.IsCheckedChanged += (_, _) => Change(() => set(c.IsChecked == true)); Put(c); return c; }
                if (entity) Check(f.Key, v => f.Key = v);
                Check(f.Required, v => f.Required = v);
                var length = new TextBox { Text = f.Length > 0 ? f.Length.ToString(CultureInfo.InvariantCulture) : "", Padding = new Thickness(4, 2) };
                length.LostFocus += (_, _) => { int.TryParse(length.Text, out int n); if (n != f.Length) Change(() => f.Length = Math.Max(0, n)); };
                Put(length);
                if (entity) { Check(f.Unique, v => f.Unique = v); Check(f.Index, v => f.Index = v); }
                var moves = new StackPanel { Orientation = Orientation.Horizontal };
                Button Small(string text, string tip, Action a) { var b = new Button { Content = text, Padding = new Thickness(4, 0), MinWidth = 0 }; b.Classes.Add("tool"); ToolTip.SetTip(b, tip); b.Click += (_, _) => a(); moves.Children.Add(b); return b; }
                Small("↑", "Up", () => Change(() => DataModelDocument.MoveField(fields, f, -1)));
                Small("↓", "Down", () => Change(() => DataModelDocument.MoveField(fields, f, 1)));
                Small("×", "Delete the field", () => Change(() => fields.Remove(f)));
                Put(moves);
                row++;
            }
            _properties.Children.Add(grid);
            var add = new Button { Content = "+ Field", Margin = new Thickness(0, 4, 0, 0) };
            add.Click += (_, _) => Change(() => _selectedField = _model.AddField(fields));
            _properties.Children.Add(add);
            if (_selectedField is { } sel && fields.Contains(sel))
            {
                _properties.Children.Add(Heading("Field " + sel.Name));
                if (entity) Field("Column", sel.Column, v => Change(() => sel.Column = Null(v)), "(by the naming of the model)");
                if (entity) Field("Check (a condition of the column)", sel.Check, v => Change(() => sel.Check = Null(v)), "age >= 0");
                if (entity) Field("Type in the database (in place of the dialect's)", sel.DbType, v => Change(() => sel.DbType = Null(v)), "postgres: jsonb; mysql: JSON; *: TEXT");
                Field("Comment", sel.Comment, v => Change(() => sel.Comment = Null(v)));
                var flags = new WrapPanel();
                if (entity)
                {
                    Flag("generated key", sel.Generated, v => Change(() => sel.Generated = v), flags);
                    Flag("version", sel.Version, v => Change(() => sel.Version = v), flags);
                }
                Flag("as JSON", sel.Json, v => Change(() => sel.Json = v), flags);
                _properties.Children.Add(flags);
            }
        }

        // ---- with a database ----
        private async Task<(string Url, string User, string Password, string Extra)?> AskDatabase(string title, string? extraLabel = null, string extraValue = "")
        {
            var owner = TopLevel.GetTopLevel(this) as Window;
            var url = new TextBox { Text = _lastUrl, PlaceholderText = "sqlite:app.db, mysql://host/db, sqlserver://host/db?trust=true" };
            var user = new TextBox { Text = _lastUser };
            var password = new TextBox { PasswordChar = '•' };
            var extra = new TextBox { Text = extraValue };
            var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80 };
            var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
            var panel = new StackPanel { Spacing = 4, Margin = new Thickness(14) };
            panel.Children.Add(new TextBlock { Text = "Database (URL)" }); panel.Children.Add(url);
            panel.Children.Add(new TextBlock { Text = "User (for a server)" }); panel.Children.Add(user);
            panel.Children.Add(new TextBlock { Text = "Password" }); panel.Children.Add(password);
            if (extraLabel != null) { panel.Children.Add(new TextBlock { Text = extraLabel }); panel.Children.Add(extra); }
            panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0), Children = { ok, cancel } });
            var dialog = new Window { Title = title, Width = 460, SizeToContent = SizeToContent.Height, Content = panel, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = EditorTheme.Background };
            bool accepted = false;
            ok.Click += (_, _) => { accepted = true; dialog.Close(); };
            cancel.Click += (_, _) => dialog.Close();
            if (owner != null) await dialog.ShowDialog(owner); else return null;
            if (!accepted || string.IsNullOrWhiteSpace(url.Text)) return null;
            _lastUrl = url.Text!.Trim(); _lastUser = user.Text ?? "";
            return (_lastUrl, _lastUser, password.Text ?? "", extra.Text ?? "");
        }
        private string _lastUrl = "", _lastUser = "";

        private async Task ImportAsync()
        {
            if (await AskDatabase("Import from a database", "Namespace", _model.Namespace) is not { } db) return;
            _status.Text = "Reading the tables of the database ...";
            var (text, error) = await Task.Run(() => DataModelTools.Import(db.Url, db.User, db.Password, db.Extra.Trim() == "" ? _model.Namespace : db.Extra.Trim()));
            if (error != null) { _status.Text = "Import failed: " + error; return; }
            Change(() =>
            {
                var imported = DataModelDocument.Parse(text);
                _model = imported;
                _diagram.Model = _model;
            });
            Select(null);
            _status.Text = $"Imported: {_model.Entities.Count} entities, {_model.Relations.Count} relations (Undo brings the model before it back).";
        }

        private async Task CreateDatabaseAsync()
        {
            if (_model.Problems().Count > 0) { _status.Text = "The model has problems (see Problems)."; return; }
            if (await AskDatabase("Generate the database") is not { } db) return;
            _status.Text = "Making the tables ...";
            var (text, error) = await Task.Run(() => DataModelTools.CreateDatabase(_model.ToJson(), FilePath ?? "Model.fdata", db.Url, db.User, db.Password));
            _status.Text = error != null ? "Failed: " + error : "Done - " + text.Trim();
        }

        private async Task MigrationAsync()
        {
            if (FilePath == null) { _status.Text = "Save the model first (in the folder of a project)."; return; }
            if (IsModified) { _status.Text = "Save the model first: the migration is made from the project's files."; return; }
            string? project = DataModelTools.ProjectOf(FilePath);
            if (await AskDatabase("Generate a migration", "Title of the migration", "from the designer") is not { } db) return;
            _status.Text = "Comparing the model with the database ...";
            var (text, error, written) = await Task.Run(() => DataModelTools.WriteMigration(project, FilePath!, db.Url, db.User, db.Password, db.Extra));
            _status.Text = error ?? (written != null ? "Written: " + written : text.Trim());
        }

    }
}
