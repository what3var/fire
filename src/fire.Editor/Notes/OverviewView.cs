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
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using fire.Projects;

namespace fire.Editor
{
    /// <summary>What a shown overview needs from its host: where it lives (relative paths), how links are followed, how a changed todo list is written.</summary>
    internal sealed class OverviewContext
    {
        public string? BaseDirectory { get; init; }
        public Action<string, KeyModifiers>? LinkClicked { get; init; }
        /// <summary>The overviews on the way to this one (full paths): an overview that shows itself is shown as a link.</summary>
        public HashSet<string> Visited { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Writes a todo list that was changed in an overview (set by the main window: it keeps the Todo window and open documents in step); null: the file is written directly.</summary>
        public static Func<string, string, bool>? WriteTodo { get; set; }
        /// <summary>Opens a file in the editor (set by the main window).</summary>
        public static Action<string>? OpenFile { get; set; }

        public string Resolve(string path) => Path.GetFullPath(Path.Combine(BaseDirectory ?? Directory.GetCurrentDirectory(), path.Replace('/', Path.DirectorySeparatorChar)));
    }

    /// <summary>What the surface changes in the text of the overview (the open document): its lines, replacing some of them (one undo step), and which container is selected.</summary>
    internal interface IOverviewHost
    {
        string[] Lines { get; }
        void Replace(int first, int count, IReadOnlyList<string> lines);
        /// <summary>The line of the definition of the selected container (kept when the surface is built again after a change); null: none.</summary>
        int? SelectedLine { get; set; }
        /// <summary>Undo and redo of the document (the surface has the keyboard, not the text editor).</summary>
        void Undo();
        void Redo();
        /// <summary>The line of a container whose text is edited as soon as the surface is built again (a new container); null: none.</summary>
        int? PendingEditLine { get; set; }
        /// <summary>The tool of the tool bar.</summary>
        OverviewTool Tool { get; }
        /// <summary>The tool has made what it makes (the host goes back to Select).</summary>
        void ToolDone();
        /// <summary>Several changes as one undo step.</summary>
        void Batch(Action changes);
        /// <summary>The colour of the tool bar for what is drawn next (`#rrggbb`); null: the default colours.</summary>
        string? Color { get; }
    }

    /// <summary>The tools of a canvas (docs/NOTES.md).</summary>
    internal enum OverviewTool { Select, Container, Rect, Ellipse, Arrow, Pen, Text, Connect }

    /// <summary>
    /// A project overview (docs/NOTES.md), shown: the text at the top left, the containers at their places (their text, sketches and containers in them, or the document they show),
    /// the lines between containers on top. Ctrl+mouse wheel zooms. A todo list in a container is a working list: its tasks are ticked off there.
    /// With a host (an editable document) the surface edits the overview: a click selects a container, dragging moves it, its handles resize it (on a raster of 10, Alt: free);
    /// a double click edits its text in place - the containers in it stay visible at half opacity. Every change goes into the text of the document, only to the lines concerned.
    /// </summary>
    internal sealed class OverviewView : UserControl
    {
        private static readonly IBrush Surface = new SolidColorBrush(Color.FromRgb(0x2A, 0x25, 0x31));
        private static readonly IBrush ContainerBorder = new SolidColorBrush(Color.FromRgb(0x4A, 0x42, 0x55));
        private static readonly IBrush SelectedBorder = new SolidColorBrush(EditorTheme.AccentTextColor);
        private static readonly IBrush Page = new SolidColorBrush(Color.FromRgb(0x24, 0x20, 0x28));
        private static readonly IBrush EditorBack = EditorTheme.CodeBlockBackground;
        public const double TextWidth = 680;
        /// <summary>The space inside a container: at the sides, and above and below its text.</summary>
        private const double SidePadding = 10;
        private const double Padding = 8;
        private const double Raster = 10;

        private readonly LayoutTransformControl _zoomHost = new();
        private readonly OverviewDocument _doc;
        private readonly OverviewContext _context;
        private readonly IOverviewHost? _host;
        private readonly AbsolutePanel _root = new() { Margin = new Thickness(24), Background = Brushes.Transparent };
        private readonly Dictionary<string, Control> _named = new(StringComparer.Ordinal);
        private readonly List<ContainerCell> _cells = new();
        private Control _rootText;
        private double _zoom = 1;

        private ContainerCell? _selected;
        private readonly List<SketchView> _sketches = new();
        private ConnectorLayer _connectors = null!;
        /// <summary>The selected shape (in its sketch) or line; at most one thing is selected.</summary>
        private (SketchView View, OvShape Shape)? _shape;
        private OvLine? _line;
        private Edit? _edit;
        private Drag? _drag;

        /// <summary>A container on the surface: its model, its box (with the text or document), the panel of the elements in it, its handles.</summary>
        private sealed class ContainerCell : Grid
        {
            public required OvContainer Model { get; init; }
            public required Border Box { get; init; }
            public required AbsolutePanel Inner { get; init; }
            public required Control Body { get; set; }
            public Border? Corner { get; set; }
            public Border? Edge { get; set; }
        }

        private enum DragKind { Move, Resize, ResizeWidth, MoveShape, ResizeShape }

        private sealed class Drag
        {
            public ContainerCell? Cell { get; init; }
            public (SketchView View, OvShape Shape)? Shape { get; init; }
            public required DragKind Kind { get; init; }
            public required Point Start { get; init; }
            public required Point Origin { get; init; }
            public required Size Size { get; init; }
            public bool Moving { get; set; }
        }

        /// <summary>The text of a container (Cell) or of the overview (Cell null) being edited in place.</summary>
        private sealed class Edit
        {
            public ContainerCell? Cell { get; init; }
            public required AvaloniaEdit.TextEditor Editor { get; init; }
            public required string Original { get; init; }
            public required Control Shown { get; init; }
            public bool Done { get; set; }
        }

        public OverviewView(OverviewDocument doc, OverviewContext context, IOverviewHost? host = null)
        {
            _doc = doc;
            _context = context;
            _host = host;
            Focusable = host != null;
            _root.Children.Add(new DotLayer());
            _rootText = MarkdownText(doc.Markdown, context, TextWidth);
            if (host != null && _rootText is Panel { Width: 0 }) _rootText = EmptyText(TextWidth, "Double click to write the text of the overview.");
            _root.Children.Add(_rootText);
            foreach (var element in doc.Children) _root.Children.Add(Build(element, _context));
            _connectors = new ConnectorLayer(doc.Lines, _named, _root) { IsHitTestVisible = host != null, Editable = host != null };
            _root.Children.Add(_connectors);

            _zoomHost.Child = _root;
            var stack = new StackPanel();
            if (doc.Problems.Count > 0)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = "Problems in the file:\n" + string.Join("\n", doc.Problems.Select(p => "  line " + p)), FontSize = 12, Foreground = EditorTheme.Yellow,
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24, 12, 24, 0),
                });
            }
            stack.Children.Add(_zoomHost);
            Content = stack;
            Background = Page;

            AddHandler(PointerWheelChangedEvent, (_, e) =>
            {
                if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
                SetZoom(_zoom * (e.Delta.Y > 0 ? 1.1 : 1 / 1.1));
                e.Handled = true;
            }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

            if (host != null)
            {
                _rootText.AddHandler(PointerPressedEvent, (_, e) =>
                {
                    if (!e.GetCurrentPoint(_rootText).Properties.IsLeftButtonPressed || _edit?.Cell == null && _edit != null) return;
                    if (e.ClickCount == 2) { Commit(); Select(null); BeginEdit(null); e.Handled = true; }
                }, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
                // a click on the empty surface (also below and beside what is on it) clears the selection
                AddHandler(PointerPressedEvent, (_, e) =>
                {
                    if (CellOf(e.Source) == null && !IsInside(e.Source, _rootText) && !IsInside(e.Source, _edit?.Editor) && !IsInside(e.Source, typeof(SketchView)) && !IsInside(e.Source, _connectors))
                    { Commit(); Select(null); }
                }, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
                AddHandler(PointerMovedEvent, OnDragMove, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
                AddHandler(PointerReleasedEvent, OnDragEnd, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
                KeyDown += (_, e) =>
                {
                    if (_edit != null) return;   // the editor in a container has its own undo
                    bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
                    bool any = _selected != null || _shape != null || _line != null;
                    if (e.Key == Key.Escape && any) { Select(null); e.Handled = true; }
                    else if (e.Key == Key.Delete && any) { DeleteSelected(); e.Handled = true; }
                    else if (ctrl && e.Key == Key.Z && !e.KeyModifiers.HasFlag(KeyModifiers.Shift)) { host.Undo(); e.Handled = true; }
                    else if (ctrl && (e.Key == Key.Y || e.Key == Key.Z)) { host.Redo(); e.Handled = true; }
                };
                if (host.SelectedLine is { } line)
                {
                    if (_cells.FirstOrDefault(c => c.Model.Line == line) is { } keep) Select(keep);
                    else if (_sketches.SelectMany(v => v.Sketch.Shapes.Select(sh => (v, sh))).FirstOrDefault(x => x.sh.Line == line) is { v: not null } shape) SelectShape(shape.v, shape.sh);
                    else if (doc.Lines.FirstOrDefault(l => l.Line == line) is { } kept) SelectLine(kept);
                }
                _connectors.AddHandler(PointerPressedEvent, (_, e) =>
                {
                    if (host.Tool != OverviewTool.Select || !e.GetCurrentPoint(_connectors).Properties.IsLeftButtonPressed) return;
                    if (_connectors.LineAt(e.GetPosition(_connectors)) is { } hit) { Commit(); SelectLine(hit); e.Handled = true; }
                });
                if (host.PendingEditLine is { } edit)
                {
                    host.PendingEditLine = null;
                    if (_cells.FirstOrDefault(c => c.Model.Line == edit) is { } fresh) Dispatcher.UIThread.Post(() => { Select(fresh); BeginEdit(fresh); }, DispatcherPriority.Loaded);
                }
                // the drawing tools take the mouse before the containers do
                AddHandler(PointerPressedEvent, OnToolPress, Avalonia.Interactivity.RoutingStrategies.Tunnel);
                AddHandler(PointerMovedEvent, OnToolMove, Avalonia.Interactivity.RoutingStrategies.Tunnel);
                AddHandler(PointerReleasedEvent, OnToolRelease, Avalonia.Interactivity.RoutingStrategies.Tunnel);
                _root.Children.Add(_rubber);
                Cursor = host.Tool == OverviewTool.Select ? null : new Cursor(StandardCursorType.Cross);
            }
        }

        /// <summary>Ends editing in place (the text goes into the document) and gives the keys to the surface.</summary>
        public void EndEditing()
        {
            Commit();
            Focus();
        }

        /// <summary>Zooms so that the whole canvas fits into `viewport`.</summary>
        public void FitTo(Size viewport)
        {
            var content = _root.DesiredSize;
            double w = content.Width + _root.Margin.Left + _root.Margin.Right, h = content.Height + _root.Margin.Top + _root.Margin.Bottom + 30;
            if (w <= 0 || h <= 0 || viewport.Width <= 0 || viewport.Height <= 0) return;
            SetZoom(Math.Min(2, Math.Min(viewport.Width / w, viewport.Height / h)));
        }

        public double Zoom => _zoom;

        public void SetZoom(double zoom)
        {
            _zoom = Math.Clamp(zoom, 0.25, 4);
            _zoomHost.LayoutTransform = new ScaleTransform(_zoom, _zoom);
        }

        // ---------------------------------------------------------------- building

        /// <summary>Markdown as a page of the given width; `compact` (in a container): no space above the first block and less below the last one.</summary>
        private static Control MarkdownText(string markdown, OverviewContext context, double width, bool compact = false)
        {
            if (markdown.Trim().Length == 0) return new Panel { Width = 0, Height = 0 };
            var renderer = new MarkdownRenderer { BaseDirectory = context.BaseDirectory, LinkClicked = context.LinkClicked };
            var page = renderer.Render(markdown);
            if (page is StackPanel sp)
            {
                sp.Margin = new Thickness(0);
                if (compact && sp.Children.Count > 0)
                {
                    var first = sp.Children[0];
                    first.Margin = new Thickness(first.Margin.Left, 0, first.Margin.Right, first.Margin.Bottom);
                    var last = sp.Children[^1];
                    last.Margin = new Thickness(last.Margin.Left, last.Margin.Top, last.Margin.Right, last.Margin.Bottom / 2);
                }
            }
            page.Width = width;
            return page;
        }

        private static Control EmptyText(double width, string hint) =>
            new TextBlock { Text = hint, Width = width, FontSize = 12, FontStyle = FontStyle.Italic, Foreground = EditorTheme.TextDim, Background = Brushes.Transparent };

        private Control Build(OvElement element, OverviewContext context) => element switch
        {
            OvContainer c => Container(c, context),
            OvSketch s => Place(Sketch(s), 0, 0),
            _ => new Panel(),
        };

        private static Control Place(Control control, double x, double y)
        {
            AbsolutePanel.SetPosition(control, new Point(x, y));
            return control;
        }

        private Control Container(OvContainer c, OverviewContext context)
        {
            Control body = c.Src != null ? Document(c.Src, context, c.W - 2 * SidePadding) : MarkdownText(c.Markdown, context, c.W - 2 * SidePadding, compact: true);
            if (_host != null && c.Src == null && body is Panel { Width: 0 }) body = EmptyText(c.W - 2 * SidePadding, "Double click to write.");

            var box = new Border
            {
                Background = Surface, BorderBrush = ContainerBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
                Padding = new Thickness(SidePadding, Padding + (c.Name != null ? 4 : 0), SidePadding, Padding), Child = body,
                Width = c.W, MinHeight = 24,
            };
            if (c.H is { } h) { box.Height = h; box.ClipToBounds = true; }

            var inner = new AbsolutePanel();
            foreach (var child in c.Children) inner.Children.Add(Build(child, context));

            var cell = new ContainerCell { Model = c, Box = box, Inner = inner, Body = body, Width = c.W };
            cell.Children.Add(box);
            cell.Children.Add(inner);
            if (c.Name != null)
            {
                cell.Children.Add(new Border
                {
                    Background = Page, CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 0), Margin = new Thickness(10, -9, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false,
                    Child = new TextBlock { Text = c.Name, FontSize = 11, Foreground = EditorTheme.TextDim },
                });
                _named.TryAdd(c.Name, box);
            }
            _cells.Add(cell);
            if (_host != null) Editable(cell);
            return Place(cell, c.X, c.Y);
        }

        // ---------------------------------------------------------------- editing

        private void Editable(ContainerCell cell)
        {
            // the handles of a selected container: the corner sizes it, the right edge only its width
            cell.Corner = new Border
            {
                Width = 10, Height = 10, Background = SelectedBorder, CornerRadius = new CornerRadius(2), IsVisible = false,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -5, -5),
                Cursor = new Cursor(StandardCursorType.BottomRightCorner),
            };
            cell.Edge = new Border
            {
                Width = 8, Background = Brushes.Transparent, IsVisible = false, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, -4, 12),
                Cursor = new Cursor(StandardCursorType.SizeWestEast),
            };
            cell.Children.Add(cell.Edge);
            cell.Children.Add(cell.Corner);
            cell.Corner.PointerPressed += (_, e) => { if (_host!.Tool != OverviewTool.Select) return; StartDrag(cell, DragKind.Resize, e); e.Handled = true; };
            cell.Edge.PointerPressed += (_, e) => { if (_host!.Tool != OverviewTool.Select) return; StartDrag(cell, DragKind.ResizeWidth, e); e.Handled = true; };

            cell.AddHandler(PointerPressedEvent, (_, e) =>
            {
                if (_host!.Tool != OverviewTool.Select || _stroke != null) return;   // a drawing tool has the mouse
                if (CellOf(e.Source) != cell || IsInside(e.Source, _edit?.Editor)) return;   // a container inside it, or its own editor
                if (IsInside(e.Source, typeof(SketchView)) || IsInside(e.Source, _connectors)) return;   // a shape or a line in it
                if (!e.GetCurrentPoint(cell).Properties.IsLeftButtonPressed) return;
                if (IsInside(e.Source, cell.Corner) || IsInside(e.Source, cell.Edge)) return;
                if (e.ClickCount == 2)
                {
                    if (cell.Model.Src != null) OverviewContext.OpenFile?.Invoke(_context.Resolve(cell.Model.Src));
                    else { Commit(); BeginEdit(cell); }
                    e.Handled = true;
                    return;
                }
                if (_edit != null && _edit.Cell != cell) Commit();
                Select(cell);
                // a click that a mark of a task or a link took does not move the container
                bool taken = e.Handled && !IsInside(e.Source, typeof(SelectableTextBlock));
                if (!taken) StartDrag(cell, DragKind.Move, e);
            }, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        }

        /// <summary>A sketch on the surface; on an editable one its shapes are selected, moved and sized with the mouse.</summary>
        private SketchView Sketch(OvSketch sketch)
        {
            var view = new SketchView(sketch, _host != null);
            _sketches.Add(view);
            if (_host == null) return view;
            view.AddHandler(PointerPressedEvent, (_, e) =>
            {
                if (_host.Tool != OverviewTool.Select || !e.GetCurrentPoint(view).Properties.IsLeftButtonPressed) return;
                var p = e.GetPosition(view);
                Commit();
                if (_shape is { } current && current.View == view && view.Handle is { } handle && handle.Inflate(2).Contains(p))
                {
                    _drag = new Drag { Shape = current, Kind = DragKind.ResizeShape, Start = e.GetPosition(_root), Origin = default, Size = SketchView.BoundsOf(current.Shape).Size };
                    e.Handled = true;
                    return;
                }
                if (view.ShapeAt(p) is not { } shape) return;
                SelectShape(view, shape);
                _drag = new Drag { Shape = (view, shape), Kind = DragKind.MoveShape, Start = e.GetPosition(_root), Origin = default, Size = SketchView.BoundsOf(shape).Size };
                e.Handled = true;
            });
            return view;
        }

        private void SelectShape(SketchView view, OvShape shape)
        {
            Select(null);
            _shape = (view, shape);
            view.Select(shape);
            if (_host != null) _host.SelectedLine = shape.Line;
        }

        private void SelectLine(OvLine line)
        {
            Select(null);
            _line = line;
            _connectors.Selected = line;
            _connectors.InvalidateVisual();
            if (_host != null) _host.SelectedLine = line.Line;
        }

        /// <summary>Gives the selected shape or line a colour (null: its default colour): `stroke`, for a text `color`.</summary>
        public void ApplyColor(string? color)
        {
            if (_host == null) return;
            int line;
            string key;
            if (_shape is { } shape) { line = shape.Shape.Line; key = shape.Shape.Kind == "text" ? "color" : "stroke"; }
            else if (_line != null) { line = _line.Line; key = "stroke"; }
            else return;
            var lines = _host.Lines;
            if (line >= lines.Length) return;
            string changed = color == null ? OverviewSource.RemoveAttribute(lines[line], key) : OverviewSource.SetAttributes(lines[line], new[] { (key, color) });
            if (changed != lines[line]) _host.Replace(line, 1, new[] { changed });
        }

        /// <summary>The colour of the tool bar for a new shape: ` stroke=…` (a text: ` color=…`), or nothing.</summary>
        private string ColorOf(string kind) => _host?.Color is { } c ? (kind == "text" ? " color=" : " stroke=") + c : "";

        private static bool IsInside(object? source, Control? control)
        {
            if (control == null) return false;
            for (var v = source as Visual; v != null; v = v.GetVisualParent()) if (ReferenceEquals(v, control)) return true;
            return false;
        }

        private static bool IsInside(object? source, Type type)
        {
            for (var v = source as Visual; v != null; v = v.GetVisualParent()) if (type.IsInstanceOfType(v)) return true;
            return false;
        }

        /// <summary>The innermost container a visual is in.</summary>
        private static ContainerCell? CellOf(object? source)
        {
            for (var v = source as Visual; v != null; v = v.GetVisualParent()) if (v is ContainerCell c) return c;
            return null;
        }

        private void Select(ContainerCell? cell)
        {
            if (_shape is { } shape) { shape.View.Select(null); _shape = null; }
            if (_line != null) { _line = null; _connectors.Selected = null; _connectors.InvalidateVisual(); }
            if (_selected != null)
            {
                _selected.Box.BorderBrush = ContainerBorder;
                _selected.Box.BorderThickness = new Thickness(1);
                if (_selected.Corner != null) _selected.Corner.IsVisible = false;
                if (_selected.Edge != null) _selected.Edge.IsVisible = false;
            }
            _selected = cell;
            if (_host != null) _host.SelectedLine = cell?.Model.Line;
            Focus();   // the keys (Esc, Ctrl+Z) go to the surface
            if (cell == null) return;
            cell.Box.BorderBrush = SelectedBorder;
            cell.Box.BorderThickness = new Thickness(1.5);
            if (cell.Corner != null) cell.Corner.IsVisible = true;
            if (cell.Edge != null) cell.Edge.IsVisible = true;
        }

        private void StartDrag(ContainerCell cell, DragKind kind, PointerPressedEventArgs e)
        {
            if (_edit?.Cell == cell) return;
            Select(cell);
            _drag = new Drag
            {
                Cell = cell, Kind = kind, Start = e.GetPosition(_root), Origin = AbsolutePanel.GetPosition(cell),
                Size = new Size(cell.Box.Bounds.Width > 0 ? cell.Box.Bounds.Width : cell.Model.W, cell.Box.Bounds.Height),
            };
        }

        private static double Snap(double value, bool free) => free ? Math.Round(value) : Math.Round(value / Raster) * Raster;

        private void OnDragMove(object? sender, PointerEventArgs e)
        {
            if (_drag is not { } drag) return;
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { _drag = null; return; }
            var p = e.GetPosition(_root);
            double dx = p.X - drag.Start.X, dy = p.Y - drag.Start.Y;
            if (!drag.Moving && Math.Abs(dx) + Math.Abs(dy) < 4) return;
            if (!drag.Moving) { drag.Moving = true; e.Pointer.Capture(this); }
            bool free = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
            if (drag.Shape is { } dragged)
            {
                if (drag.Kind == DragKind.MoveShape) dragged.View.Offset = new Vector(Snap(dx, free), Snap(dy, free));
                else dragged.View.NewSize = new Size(Math.Max(Raster, Snap(drag.Size.Width + dx, free)), Math.Max(Raster, Snap(drag.Size.Height + dy, free)));
                dragged.View.InvalidateVisual();
                e.Handled = true;
                return;
            }
            var cell = drag.Cell!;
            switch (drag.Kind)
            {
                case DragKind.Move:
                    AbsolutePanel.SetPosition(cell, new Point(Math.Max(0, Snap(drag.Origin.X + dx, free)), Math.Max(0, Snap(drag.Origin.Y + dy, free))));
                    (cell.Parent as Control)?.InvalidateMeasure();
                    break;
                case DragKind.Resize:
                case DragKind.ResizeWidth:
                    double w = Math.Max(60, Snap(drag.Size.Width + dx, free));
                    cell.Width = w;
                    cell.Box.Width = w;
                    if (cell.Body is Control body && body.Width > 0) body.Width = w - 2 * SidePadding;
                    if (drag.Kind == DragKind.Resize)
                    {
                        cell.Box.Height = Math.Max(30, Snap(drag.Size.Height + dy, free));
                        cell.Box.ClipToBounds = true;
                    }
                    break;
            }
            e.Handled = true;
        }

        private void OnDragEnd(object? sender, PointerReleasedEventArgs e)
        {
            if (_drag is not { } drag) return;
            _drag = null;
            if (!drag.Moving) return;
            e.Pointer.Capture(null);
            if (drag.Shape is { } dragged)
            {
                var lines0 = _host!.Lines;
                var (view, shape) = dragged;
                if (shape.Line >= lines0.Length) return;
                string changed = drag.Kind == DragKind.MoveShape
                    ? OverviewSource.MoveShape(lines0[shape.Line], shape.Kind, view.Offset.X, view.Offset.Y)
                    : OverviewSource.ResizeShape(lines0[shape.Line], view.NewSize?.Width ?? drag.Size.Width, view.NewSize?.Height ?? drag.Size.Height);
                _host.SelectedLine = shape.Line;
                if (changed != lines0[shape.Line]) _host.Replace(shape.Line, 1, new[] { changed });
                else view.Select(shape);
                e.Handled = true;
                return;
            }
            var cell = drag.Cell!;
            var values = drag.Kind switch
            {
                DragKind.Move => new[] { ("x", OverviewSource.Number(AbsolutePanel.GetPosition(cell).X)), ("y", OverviewSource.Number(AbsolutePanel.GetPosition(cell).Y)) },
                DragKind.ResizeWidth => new[] { ("w", OverviewSource.Number(cell.Box.Width)) },
                _ => new[] { ("w", OverviewSource.Number(cell.Box.Width)), ("h", OverviewSource.Number(cell.Box.Height)) },
            };
            var lines = _host!.Lines;
            int line = cell.Model.Line;
            if (line < lines.Length) _host.Replace(line, 1, new[] { OverviewSource.SetAttributes(lines[line], values) });
            e.Handled = true;
        }

        /// <summary>Edits the own text of a container (null: of the overview) in place; the containers in it stay visible at half opacity.</summary>
        private void BeginEdit(ContainerCell? cell)
        {
            if (_host == null) return;
            string original = OverviewSource.OwnText(_host.Lines, _doc, cell?.Model);
            double width = cell == null ? TextWidth : cell.Box.Width - 2 * SidePadding;
            var shown = cell?.Body ?? _rootText;
            var editor = new AvaloniaEdit.TextEditor
            {
                Text = original, WordWrap = true, ShowLineNumbers = false, FontSize = 13, Width = width, Background = EditorBack, Foreground = EditorTheme.Text,
                FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace"),
                MinHeight = Math.Max(60, shown.Bounds.Height), Padding = new Thickness(4),
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            };
            editor.TextArea.TextView.LineTransformers.Add(new MarkdownColorizer());
            var edit = new Edit { Cell = cell, Editor = editor, Original = original, Shown = shown };
            _edit = edit;
            if (cell != null)
            {
                cell.Box.Child = editor;
                cell.Box.Height = double.NaN;   // the editor may need more room than the container had
                cell.Box.ClipToBounds = false;
                cell.Inner.Opacity = 0.5;
            }
            else
            {
                int index = _root.Children.IndexOf(_rootText);
                AbsolutePanel.SetPosition(editor, new Point(0, 0));
                _root.Children[index] = editor;
                foreach (var c in _root.Children.Where(c => c is ContainerCell or SketchView)) c.Opacity = 0.5;
            }
            editor.KeyDown += (_, e) => { if (e.Key == Key.Escape) { Commit(); e.Handled = true; } };
            editor.LostFocus += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                // the focus went somewhere else (not into the editor's own parts): the text is done
                if (_edit == edit && !editor.IsKeyboardFocusWithin) Commit();
            }, DispatcherPriority.Background);
            Dispatcher.UIThread.Post(() => { editor.Focus(); editor.TextArea.Focus(); }, DispatcherPriority.Loaded);
        }

        /// <summary>Ends editing in place: a changed text goes into the document (the surface is built again from it), else the text is shown as it was.</summary>
        private void Commit()
        {
            if (_edit is not { Done: false } edit || _host == null) return;
            edit.Done = true;
            _edit = null;
            string text = edit.Editor.Text;
            if (edit.Cell != null)
            {
                edit.Cell.Box.Child = edit.Shown;
                edit.Cell.Inner.Opacity = 1;
                if (edit.Cell.Model.H is { } h) { edit.Cell.Box.Height = h; edit.Cell.Box.ClipToBounds = true; }
            }
            else
            {
                int index = _root.Children.IndexOf(edit.Editor);
                if (index >= 0) _root.Children[index] = edit.Shown;
                foreach (var c in _root.Children.Where(c => c is ContainerCell or SketchView)) c.Opacity = 1;
            }
            if (text.Replace("\r\n", "\n").TrimEnd() == edit.Original.TrimEnd()) return;
            var (first, count, lines) = OverviewSource.ReplaceOwnText(_host.Lines, _doc, edit.Cell?.Model, text);
            _host.Replace(first, count, lines);
        }

        // ---------------------------------------------------------------- tools

        private readonly RubberLayer _rubber = new() { IsHitTestVisible = false };
        private ToolStroke? _stroke;

        /// <summary>What a tool is drawing: in which container (null: the overview), its points in the coordinates of that container.</summary>
        private sealed class ToolStroke
        {
            public required OverviewTool Tool { get; init; }
            public ContainerCell? Scope { get; init; }
            public required List<Point> Points { get; init; }
        }

        /// <summary>The coordinates of the container a tool draws in (its top left corner), or of the overview.</summary>
        private Point Local(PointerEventArgs e, ContainerCell? scope) => e.GetPosition(scope ?? (Visual)_root);

        private Point ToRoot(ContainerCell? scope, Point p) => scope == null ? p : scope.TranslatePoint(p, _root) ?? p;

        private void OnToolPress(object? sender, PointerPressedEventArgs e)
        {
            var tool = _host!.Tool;
            if (tool == OverviewTool.Select || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            if (_edit != null && IsInside(e.Source, _edit.Editor)) return;
            Commit();
            // a shape or container goes into the container it starts in (not into one that shows a document)
            var scope = CellOf(e.Source);
            while (scope != null && scope.Model.Src != null) scope = CellOf(scope.GetVisualParent());
            if (tool == OverviewTool.Connect) scope = CellOf(e.Source);
            if (tool == OverviewTool.Connect && scope == null) { e.Handled = true; return; }
            var p = Local(e, tool == OverviewTool.Connect ? null : scope);
            if (tool == OverviewTool.Text) { e.Handled = true; AskText(scope, p); return; }
            _stroke = new ToolStroke { Tool = tool, Scope = scope, Points = new List<Point> { p } };
            e.Pointer.Capture(this);
            e.Handled = true;
            UpdateRubber();
        }

        private void OnToolMove(object? sender, PointerEventArgs e)
        {
            if (_stroke is not { } stroke) return;
            var p = Local(e, stroke.Tool == OverviewTool.Connect ? null : stroke.Scope);
            if (stroke.Tool == OverviewTool.Pen) { if (Distance(stroke.Points[^1], p) >= 3) stroke.Points.Add(p); }
            else if (stroke.Points.Count == 1) stroke.Points.Add(p);
            else stroke.Points[^1] = p;
            UpdateRubber();
            e.Handled = true;
        }

        private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

        private void UpdateRubber()
        {
            if (_stroke is not { } stroke) { _rubber.Points = null; _rubber.InvalidateVisual(); return; }
            _rubber.Tool = stroke.Tool;
            _rubber.Points = stroke.Points.Select(p => stroke.Tool == OverviewTool.Connect ? p : ToRoot(stroke.Scope, p)).ToList();
            _rubber.InvalidateVisual();
        }

        private void OnToolRelease(object? sender, PointerReleasedEventArgs e)
        {
            if (_stroke is not { } stroke) return;
            _stroke = null;
            UpdateRubber();
            e.Pointer.Capture(null);
            e.Handled = true;
            bool free = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
            var start = stroke.Points[0];
            var end = stroke.Points[^1];
            var scope = stroke.Scope?.Model;
            string N(double v) => OverviewSource.Number(v);
            switch (stroke.Tool)
            {
                case OverviewTool.Container:
                {
                    double x = Snap(Math.Min(start.X, end.X), free), y = Snap(Math.Min(start.Y, end.Y), free);
                    // the drag gives the place and the width; the height follows the text (the corner handle fixes it, if wanted)
                    double w = Math.Abs(end.X - start.X);
                    w = w >= 20 ? Math.Max(60, Snap(w, free)) : 300;
                    var (change, line) = OverviewSource.Insert(_host!.Lines, scope, new[] { $"[container]: <x={N(x)} y={N(y)} w={N(w)}>", "[/]: <>" });
                    _host.PendingEditLine = line;
                    _host.SelectedLine = line;
                    _host.Replace(change.First, change.Count, change.Lines);
                    break;
                }
                case OverviewTool.Rect or OverviewTool.Ellipse:
                {
                    double x = Math.Min(start.X, end.X), y = Math.Min(start.Y, end.Y), w = Math.Abs(end.X - start.X), h = Math.Abs(end.Y - start.Y);
                    if (w < 3 && h < 3) break;
                    string kind = stroke.Tool == OverviewTool.Rect ? "rect" : "ellipse";
                    AddShape(scope, $"[{kind}]: <{N(Snap(x, free))} {N(Snap(y, free))} {N(Math.Max(Raster, Snap(w, free)))} {N(Math.Max(Raster, Snap(h, free)))}{ColorOf(kind)}>");
                    break;
                }
                case OverviewTool.Arrow:
                    if (Distance(start, end) < 5) break;
                    AddShape(scope, $"[path]: <M {N(Math.Round(start.X))} {N(Math.Round(start.Y))} L {N(Math.Round(end.X))} {N(Math.Round(end.Y))} arrow=end{ColorOf("path")}>");
                    break;
                case OverviewTool.Pen:
                    if (stroke.Points.Count < 2) break;
                    AddShape(scope, "[path]: <M " + string.Join(" L ", stroke.Points.Select(p => $"{N(Math.Round(p.X))} {N(Math.Round(p.Y))}")) + ColorOf("path") + ">");
                    break;
                case OverviewTool.Connect:
                    Connect(stroke.Scope, e);
                    break;
            }
            _host!.ToolDone();
        }

        private void AddShape(OvContainer? scope, string shape)
        {
            var (change, _) = OverviewSource.AddShape(_host!.Lines, _doc, scope, shape);
            _host.Replace(change.First, change.Count, change.Lines);
        }

        /// <summary>A line from the container the drag started in to the one it ends in; containers without a name get one.</summary>
        private void Connect(ContainerCell? from, PointerEventArgs e)
        {
            var to = CellOf(_root.InputHitTest(e.GetPosition(_root)) as Visual);
            if (from == null || to == null || to == from) return;
            var lines = _host!.Lines;
            string a = from.Model.Name ?? OverviewSource.UniqueName(_doc);
            string b = to.Model.Name ?? OverviewSource.UniqueName(_doc, taken: new[] { a });
            string? fromLine = from.Model.Name == null ? OverviewSource.SetTitle(lines[from.Model.Line], a) : null;
            string? toLine = to.Model.Name == null ? OverviewSource.SetTitle(lines[to.Model.Line], b) : null;
            _host.Batch(() =>
            {
                if (fromLine != null) _host.Replace(from.Model.Line, 1, new[] { fromLine });
                if (toLine != null) _host.Replace(to.Model.Line, 1, new[] { toLine });
                var (change, _) = OverviewSource.Insert(_host.Lines, null, new[] { $"[line]: <from={a} to={b} arrow=end{ColorOf("line")}>" });
                _host.Replace(change.First, change.Count, change.Lines);
            });
        }

        /// <summary>A text on the surface: a box at the point; Enter writes it into the sketch of the container, Esc drops it.</summary>
        private void AskText(ContainerCell? scope, Point at)
        {
            var box = new TextBox { MinWidth = 160, FontSize = 13, Watermark = "Text (Enter)" };
            AbsolutePanel.SetPosition(box, ToRoot(scope, at));
            _root.Children.Add(box);
            bool done = false;
            void Finish(bool keep)
            {
                if (done) return;
                done = true;
                _root.Children.Remove(box);
                string text = (box.Text ?? "").Trim();
                if (keep && text.Length > 0)
                    AddShape(scope?.Model, $"[text]: <{OverviewSource.Number(Math.Round(at.X))} {OverviewSource.Number(Math.Round(at.Y))}{ColorOf("text")}> \"{text.Replace("\"", "\\\"")}\"");
                _host!.ToolDone();
            }
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { Finish(true); e.Handled = true; }
                else if (e.Key == Key.Escape) { Finish(false); e.Handled = true; }
            };
            box.LostFocus += (_, _) => Finish(true);
            Dispatcher.UIThread.Post(() => box.Focus(), DispatcherPriority.Loaded);
        }

        /// <summary>Deletes the selected container with everything in it.</summary>
        private void DeleteSelected()
        {
            if (_host == null) return;
            if (_shape is { } shape)
            {
                var removed = OverviewSource.RemoveShape(_host.Lines, _doc, shape.Shape);
                _host.SelectedLine = null;
                _host.Replace(removed.First, removed.Count, removed.Lines);
                return;
            }
            if (_line != null)
            {
                int at = _line.Line;
                _host.SelectedLine = null;
                _host.Replace(at, 1, Array.Empty<string>());
                return;
            }
            if (_selected == null) return;
            var change = OverviewSource.Remove(_host.Lines, _selected.Model);
            _host.SelectedLine = null;
            _host.Replace(change.First, change.Count, change.Lines);
        }

        // ---------------------------------------------------------------- documents in containers

        /// <summary>The document a container shows: a todo list (to tick off), another overview, Markdown, a picture - or a link to open it.</summary>
        private static Control Document(string src, OverviewContext context, double width)
        {
            string path = context.Resolve(src);
            if (!File.Exists(path))
                return new TextBlock { Text = $"{src} does not exist.", Foreground = EditorTheme.Yellow, FontSize = 12, TextWrapping = TextWrapping.Wrap };
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp")
            {
                try { return new Image { Source = new Bitmap(path), Stretch = Stretch.Uniform, Width = width }; }
                catch (Exception) { return FileLink(src, path); }
            }
            if (ext is not (".md" or ".xmd")) return FileLink(src, path);

            string text;
            try { text = File.ReadAllText(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new TextBlock { Text = ex.Message, Foreground = EditorTheme.Yellow }; }

            if (ext == OverviewParser.CanvasExtension || OverviewParser.IsOverview(text))
            {
                if (context.Visited.Contains(path)) return FileLink(src, path);
                var visited = new HashSet<string>(context.Visited, StringComparer.OrdinalIgnoreCase) { path };
                var nested = new OverviewView(OverviewParser.Parse(text), new OverviewContext { BaseDirectory = Path.GetDirectoryName(path), LinkClicked = context.LinkClicked, Visited = visited });
                return new Border { ClipToBounds = true, Width = width, Child = nested };
            }
            if (TodoList.Parse(text).Items.Count > 0) return new TodoListView(path, width);
            return MarkdownText(text, new OverviewContext { BaseDirectory = Path.GetDirectoryName(path), LinkClicked = context.LinkClicked }, width, compact: true);
        }

        private static Control FileLink(string src, string path)
        {
            var link = new TextBlock { Text = "Open " + src, Foreground = new SolidColorBrush(EditorTheme.AccentTextColor), Cursor = new Cursor(StandardCursorType.Hand), FontSize = 13 };
            link.PointerPressed += (_, e) => { OverviewContext.OpenFile?.Invoke(path); e.Handled = true; };
            return link;
        }
    }

    /// <summary>What a tool draws while the mouse is down: the frame of a container or shape, the arrow, the stroke of the pen, the line between containers.</summary>
    internal sealed class RubberLayer : Control, IFillLayer
    {
        private static readonly IPen Pen = new Pen(new SolidColorBrush(EditorTheme.AccentTextColor), 1.5) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) };
        private static readonly IBrush Brush = new SolidColorBrush(EditorTheme.AccentTextColor);
        public OverviewTool Tool { get; set; }
        public List<Point>? Points { get; set; }

        public override void Render(DrawingContext context)
        {
            if (Points is not { Count: >= 2 } p) return;
            var a = p[0];
            var b = p[^1];
            var rect = new Rect(new Point(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y)), new Point(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)));
            switch (Tool)
            {
                case OverviewTool.Container or OverviewTool.Rect: context.DrawRectangle(null, Pen, rect, 4, 4); break;
                case OverviewTool.Ellipse: context.DrawEllipse(null, Pen, rect); break;
                case OverviewTool.Arrow or OverviewTool.Connect:
                    context.DrawLine(Pen, a, b);
                    ArrowHead.Draw(context, b, new Point(b.X - a.X, b.Y - a.Y), Brush);
                    break;
                case OverviewTool.Pen:
                    for (int i = 1; i < p.Count; i++) context.DrawLine(Pen, p[i - 1], p[i]);
                    break;
            }
        }
    }

    /// <summary>A panel that puts its children at their positions (<see cref="SetPosition"/>) and is as large as what they cover; layers (<see cref="IFillLayer"/>) cover all of it.</summary>
    internal sealed class AbsolutePanel : Panel
    {
        public static readonly AttachedProperty<Point> PositionProperty = AvaloniaProperty.RegisterAttached<AbsolutePanel, Control, Point>("Position");
        public static void SetPosition(Control control, Point point) => control.SetValue(PositionProperty, point);
        public static Point GetPosition(Control control) => control.GetValue(PositionProperty);


        protected override Size MeasureOverride(Size availableSize)
        {
            double w = 0, h = 0;
            foreach (var child in Children)
            {
                child.Measure(Size.Infinity);
                if (child is IFillLayer) continue;
                var p = GetPosition(child);
                w = Math.Max(w, p.X + child.DesiredSize.Width);
                h = Math.Max(h, p.Y + child.DesiredSize.Height);
            }
            return new Size(w, h);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            foreach (var child in Children)
            {
                if (child is IFillLayer) { child.Arrange(new Rect(finalSize)); continue; }
                child.Arrange(new Rect(GetPosition(child), child.DesiredSize));
            }
            return finalSize;
        }

    }

    /// <summary>A layer that covers the whole <see cref="AbsolutePanel"/> it is in (and does not make it larger).</summary>
    internal interface IFillLayer { }

    /// <summary>The dots of a drawing surface, behind everything.</summary>
    internal sealed class DotLayer : Control, IFillLayer
    {
        private static readonly IBrush Dot = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));

        public DotLayer() => IsHitTestVisible = false;

        public override void Render(DrawingContext context)
        {
            const double step = 20;
            for (double x = 0; x <= Bounds.Width; x += step)
                for (double y = 0; y <= Bounds.Height; y += step)
                    context.FillRectangle(Dot, new Rect(x - 0.75, y - 0.75, 1.5, 1.5));
        }
    }

    /// <summary>
    /// The shapes of a sketch, relative to the container it is in. On an editable surface a shape is hit where it is drawn (the outline of a shape without filling, near
    /// a path); the selected shape gets a dashed frame (and a handle for its size, for a rect or an ellipse), and while it is dragged it is drawn where it will be.
    /// </summary>
    internal sealed class SketchView : Control, Avalonia.Rendering.ICustomHitTest
    {
        private readonly OvSketch _sketch;
        private readonly bool _editable;
        private static readonly IBrush DefaultStroke = new SolidColorBrush(Color.FromRgb(0xA0, 0x94, 0xA8));
        private static readonly IPen SelectionPen = new Pen(new SolidColorBrush(EditorTheme.AccentTextColor), 1) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) };
        private static readonly IBrush HandleBrush = new SolidColorBrush(EditorTheme.AccentTextColor);
        public const double HandleSize = 9;

        public OvSketch Sketch => _sketch;
        /// <summary>The selected shape; null: none.</summary>
        public OvShape? Selected { get; private set; }
        /// <summary>While dragging: how far the selected shape moves, and its new size (rect, ellipse).</summary>
        public Vector Offset { get; set; }
        public Size? NewSize { get; set; }

        public SketchView(OvSketch sketch, bool editable = false)
        {
            _sketch = sketch;
            _editable = editable;
            IsHitTestVisible = editable;
        }

        public void Select(OvShape? shape)
        {
            Selected = shape;
            Offset = default;
            NewSize = null;
            InvalidateVisual();
        }

        private static IBrush? Brush(string? color)
        {
            if (string.IsNullOrWhiteSpace(color) || color == "none") return null;
            return Color.TryParse(color, out var c) ? new SolidColorBrush(c) : null;
        }

        private static (double X, double Y, double W, double H) Box(OvShape s) =>
            (s.Attributes.Number("x", 0) ?? 0, s.Attributes.Number("y", 1) ?? 0, s.Attributes.Number("w", 2) ?? 0, s.Attributes.Number("h", 3) ?? 0);

        private static Geometry? PathOf(OvShape s)
        {
            string data = s.Attributes.Get("d") ?? string.Join(" ", s.Attributes.Positional);
            try { return Geometry.Parse(data); } catch (Exception) { return null; }
        }

        private static FormattedText TextOf(OvShape s, IBrush? fill) =>
            new(s.Name ?? "", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, s.Attributes.Number("size") ?? 13,
                Brush(s.Attributes.Get("color")) ?? fill ?? EditorTheme.Text);

        /// <summary>Where a shape is (without the stroke).</summary>
        public static Rect BoundsOf(OvShape s) => s.Kind switch
        {
            "path" => PathOf(s)?.Bounds ?? default,
            "text" => new Rect(Box(s).X, Box(s).Y, TextOf(s, null).Width, TextOf(s, null).Height),
            _ => new Rect(Box(s).X, Box(s).Y, Box(s).W, Box(s).H),
        };

        public static bool CanResize(OvShape s) => s.Kind is "rect" or "ellipse";

        /// <summary>The handle of the size of the selected shape, where it is drawn now.</summary>
        public Rect? Handle => Selected is { } s && CanResize(s) ? HandleAt(ShownBounds(s)) : null;

        private static Rect HandleAt(Rect bounds) => new(bounds.Right - HandleSize / 2, bounds.Bottom - HandleSize / 2, HandleSize, HandleSize);

        private Rect ShownBounds(OvShape s)
        {
            var r = BoundsOf(s);
            if (s != Selected) return r;
            if (NewSize is { } size) r = new Rect(r.Position, size);
            return r.Translate(Offset);
        }

        /// <summary>The shape at a point (the topmost one); null: none.</summary>
        public OvShape? ShapeAt(Point p)
        {
            for (int i = _sketch.Shapes.Count - 1; i >= 0; i--)
            {
                var s = _sketch.Shapes[i];
                bool filled = Brush(s.Attributes.Get("fill")) != null;
                var r = BoundsOf(s);
                switch (s.Kind)
                {
                    case "path":
                        if (PathOf(s) is { } g && (g.StrokeContains(new Pen(Brushes.Black, Math.Max(8, (s.Attributes.Number("width") ?? 1.5) + 6)), p) || filled && g.FillContains(p))) return s;
                        break;
                    case "text":
                        if (r.Inflate(3).Contains(p)) return s;
                        break;
                    case "ellipse":
                    {
                        double rx = r.Width / 2, ry = r.Height / 2;
                        if (rx <= 0 || ry <= 0) break;
                        double d = Math.Sqrt(Math.Pow((p.X - r.Center.X) / rx, 2) + Math.Pow((p.Y - r.Center.Y) / ry, 2));
                        if (filled ? d <= 1.05 : Math.Abs(d - 1) * Math.Min(rx, ry) <= 5) return s;
                        break;
                    }
                    default:
                        if (filled ? r.Inflate(3).Contains(p) : r.Inflate(5).Contains(p) && !r.Deflate(5).Contains(p)) return s;
                        break;
                }
            }
            return null;
        }

        public bool HitTest(Point point) => _editable && (ShapeAt(point) != null || Handle is { } h && h.Inflate(2).Contains(point));

        private Rect Extent()
        {
            Rect? all = null;
            foreach (var s in _sketch.Shapes) all = all == null ? BoundsOf(s) : all.Value.Union(BoundsOf(s));
            return (all ?? default).Inflate(6);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var e = Extent();
            return new Size(Math.Max(0, e.Right), Math.Max(0, e.Bottom));
        }

        public override void Render(DrawingContext context)
        {
            foreach (var s in _sketch.Shapes)
            {
                var stroke = Brush(s.Attributes.Get("stroke")) ?? (s.Kind == "text" ? null : DefaultStroke);
                var fill = Brush(s.Attributes.Get("fill"));
                double width = s.Attributes.Number("width") ?? 1.5;
                var pen = stroke == null ? null : new Pen(stroke, width, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round)
                {
                    DashStyle = s.Attributes.Get("dash") is "true" or "yes" ? new DashStyle(new double[] { 4, 3 }, 0) : null,
                };
                bool moving = s == Selected && Offset != default;
                using var _ = moving ? context.PushTransform(Matrix.CreateTranslation(Offset.X, Offset.Y)) : default(DrawingContext.PushedState?);
                var (x, y, w, h) = Box(s);
                if (s == Selected && NewSize is { } size) { w = size.Width; h = size.Height; }
                switch (s.Kind)
                {
                    case "rect":
                        double r = s.Attributes.Number("radius") ?? 4;
                        context.DrawRectangle(fill, pen, new Rect(x, y, w, h), r, r);
                        break;
                    case "ellipse":
                        context.DrawEllipse(fill, pen, new Rect(x, y, w, h));
                        break;
                    case "path":
                        if (PathOf(s) is { } geometry)
                        {
                            context.DrawGeometry(fill, pen, geometry);
                            if (pen != null) Arrows(context, geometry, stroke!, s.Attributes.Get("arrow"));
                        }
                        break;
                    case "text":
                        context.DrawText(TextOf(s, fill), new Point(x, y));
                        break;
                }
            }
            if (Selected is { } selected)
            {
                var bounds = ShownBounds(selected);
                context.DrawRectangle(null, SelectionPen, bounds.Inflate(4));
                if (CanResize(selected)) context.DrawRectangle(HandleBrush, null, HandleAt(bounds), 2, 2);
            }
        }

        private static void Arrows(DrawingContext context, Geometry geometry, IBrush brush, string? arrow)
        {
            if (arrow is not ("end" or "start" or "both")) return;
            double length = geometry.ContourLength;
            if (length <= 0) return;
            if (arrow is "end" or "both" && geometry.TryGetPointAndTangentAtDistance(length, out var p, out var t)) ArrowHead.Draw(context, p, t, brush);
            if (arrow is "start" or "both" && geometry.TryGetPointAndTangentAtDistance(0, out var p0, out var t0)) ArrowHead.Draw(context, p0, new Point(-t0.X, -t0.Y), brush);
        }
    }

    internal static class ArrowHead
    {
        /// <summary>A filled arrow head at `tip`, pointing along `direction`.</summary>
        public static void Draw(DrawingContext context, Point tip, Point direction, IBrush brush, double size = 9)
        {
            double len = Math.Sqrt(direction.X * direction.X + direction.Y * direction.Y);
            if (len < 1e-9) return;
            var d = new Point(direction.X / len, direction.Y / len);
            var n = new Point(-d.Y, d.X);
            var back = new Point(tip.X - d.X * size, tip.Y - d.Y * size);
            var head = new StreamGeometry();
            using (var g = head.Open())
            {
                g.BeginFigure(tip, true);
                g.LineTo(new Point(back.X + n.X * size * 0.5, back.Y + n.Y * size * 0.5));
                g.LineTo(new Point(back.X - n.X * size * 0.5, back.Y - n.Y * size * 0.5));
                g.EndFigure(true);
            }
            context.DrawGeometry(brush, null, head);
        }
    }

    /// <summary>The lines between named containers, drawn over everything; they are drawn again whenever the layout changes, so they follow the containers.</summary>
    internal sealed class ConnectorLayer : Control, IFillLayer, Avalonia.Rendering.ICustomHitTest
    {
        /// <summary>The selected line; null: none. On an editable surface a line is hit near it.</summary>
        public OvLine? Selected { get; set; }
        public bool Editable { get; set; }
        private readonly Dictionary<OvLine, (Point Start, Point End)> _drawn = new();
        private static readonly IBrush SelectedBrush = new SolidColorBrush(EditorTheme.AccentTextColor);

        public bool HitTest(Point point) => Editable && LineAt(point) != null;

        /// <summary>The line near a point; null: none.</summary>
        public OvLine? LineAt(Point p)
        {
            foreach (var (line, (a, b)) in _drawn)
            {
                double dx = b.X - a.X, dy = b.Y - a.Y, len2 = dx * dx + dy * dy;
                double t = len2 == 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0, 1);
                double qx = a.X + t * dx - p.X, qy = a.Y + t * dy - p.Y;
                if (qx * qx + qy * qy <= 36) return line;
            }
            return null;
        }

        private readonly List<OvLine> _lines;
        private readonly Dictionary<string, Control> _named;
        private readonly Visual _root;
        private static readonly IBrush Stroke = new SolidColorBrush(Color.FromRgb(0xC0, 0x6A, 0xDE));
        private static readonly IBrush LabelBack = new SolidColorBrush(Color.FromRgb(0x24, 0x20, 0x28));

        public ConnectorLayer(List<OvLine> lines, Dictionary<string, Control> named, Visual root)
        {
            _lines = lines;
            _named = named;
            _root = root;
            LayoutUpdated += (_, _) => InvalidateVisual();
        }

        private Rect? BoundsOf(string name)
        {
            if (!_named.TryGetValue(name, out var c) || !c.IsAttachedToVisualTree()) return null;
            var p = c.TranslatePoint(new Point(0, 0), this);
            return p == null ? null : new Rect(p.Value, c.Bounds.Size);
        }

        /// <summary>Where the straight line from the centre of `r` towards `toward` leaves `r`.</summary>
        private static Point Edge(Rect r, Point toward)
        {
            var c = r.Center;
            double dx = toward.X - c.X, dy = toward.Y - c.Y;
            if (Math.Abs(dx) < 1e-9 && Math.Abs(dy) < 1e-9) return c;
            double sx = Math.Abs(dx) < 1e-9 ? double.MaxValue : r.Width / 2 / Math.Abs(dx);
            double sy = Math.Abs(dy) < 1e-9 ? double.MaxValue : r.Height / 2 / Math.Abs(dy);
            double s = Math.Min(sx, sy);
            return new Point(c.X + dx * s, c.Y + dy * s);
        }

        public override void Render(DrawingContext context)
        {
            _drawn.Clear();
            foreach (var line in _lines)
            {
                if (BoundsOf(line.From) is not { } a || BoundsOf(line.To) is not { } b) continue;
                var start = Edge(a.Inflate(4), b.Center);
                var end = Edge(b.Inflate(4), a.Center);
                _drawn[line] = (start, end);
                IBrush brush = line == Selected ? SelectedBrush : line.Attributes.Get("stroke") is { } s && Color.TryParse(s, out var color) ? new SolidColorBrush(color) : Stroke;
                var pen = new Pen(brush, (line.Attributes.Number("width") ?? 1.5) + (line == Selected ? 1.5 : 0), lineCap: PenLineCap.Round)
                {
                    DashStyle = line.Attributes.Get("dash") is "true" or "yes" ? new DashStyle(new double[] { 4, 3 }, 0) : null,
                };
                context.DrawLine(pen, start, end);
                var dir = new Point(end.X - start.X, end.Y - start.Y);
                if (line.Arrow is "end" or "both") ArrowHead.Draw(context, end, dir, brush);
                if (line.Arrow is "start" or "both") ArrowHead.Draw(context, start, new Point(-dir.X, -dir.Y), brush);
                if (line.Name is { Length: > 0 } label)
                {
                    var text = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 11, EditorTheme.TextDim);
                    var mid = new Point((start.X + end.X) / 2, (start.Y + end.Y) / 2);
                    var rect = new Rect(mid.X - text.Width / 2 - 4, mid.Y - text.Height / 2 - 1, text.Width + 8, text.Height + 2);
                    context.DrawRectangle(LabelBack, null, rect, 3, 3);
                    context.DrawText(text, new Point(rect.X + 4, rect.Y + 1));
                }
            }
        }
    }

    /// <summary>
    /// A todo list in an overview (docs/NOTES.md): its groups and tasks with their marks; a click on a mark moves the task on, like in the Todo window, and the file is written
    /// (through <see cref="OverviewContext.WriteTodo"/>, which keeps the Todo window and an open document in step). Changes of the file from outside are read again.
    /// </summary>
    internal sealed class TodoListView : UserControl
    {
        private readonly string _path;
        private readonly double _width;
        private readonly DispatcherTimer _timer;
        private string _text = "";

        public TodoListView(string path, double width)
        {
            _path = path;
            _width = width;
            _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Reload());
            AttachedToVisualTree += (_, _) => { Reload(); _timer.Start(); };
            DetachedFromVisualTree += (_, _) => _timer.Stop();
            Reload();
        }

        private void Reload()
        {
            string text;
            try { text = File.Exists(_path) ? File.ReadAllText(_path) : ""; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
            if (text == _text && Content != null) return;
            _text = text;
            Show(TodoList.Parse(text));
        }

        private void Show(TodoList list)
        {
            var stack = new StackPanel { Width = _width, Spacing = 1 };
            var groups = new List<string?>();
            if (list.Items.Any(i => i.Group == null)) groups.Add(null);
            groups.AddRange(list.Groups.Where(g => list.Items.Any(i => i.Group == g)));
            foreach (var group in groups)
            {
                if (group != null)
                    stack.Children.Add(new TextBlock { Text = group, FontWeight = FontWeight.SemiBold, FontSize = 13, Margin = new Thickness(0, stack.Children.Count > 0 ? 8 : 0, 0, 2), Foreground = EditorTheme.Text });
                foreach (var item in list.Items.Where(i => i.Group == group))
                    stack.Children.Add(Row(list, item));
            }
            Content = stack;
        }

        private Control Row(TodoList list, TodoItem item)
        {
            int line = item.Line;
            var mark = new TaskMark
            {
                State = item.State switch { TodoState.InProgress => MdTaskState.InProgress, TodoState.Blocked => MdTaskState.Blocked, TodoState.Done => MdTaskState.Done, _ => MdTaskState.Open },
                Margin = new Thickness(0, 2, 8, 0), VerticalAlignment = VerticalAlignment.Top, Cursor = new Cursor(StandardCursorType.Hand),
            };
            mark.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                var current = TodoList.Parse(_text);
                if (current.ItemAt(line) is not { } now || now.Text != item.Text) { Reload(); return; }
                current.SetState(now, ProjectNotes.Next(now.State));
                string text = current.ToString();
                bool ok = OverviewContext.WriteTodo?.Invoke(_path, text) ?? TryWrite(text);
                if (ok) { _text = text; Show(current); }
            };
            var label = new TextBlock
            {
                Text = item.Text, FontSize = 13, TextWrapping = TextWrapping.Wrap,
                Foreground = item.State switch { TodoState.Done => EditorTheme.TextDim, TodoState.InProgress => new SolidColorBrush(EditorTheme.AccentTextColor), _ => EditorTheme.Text },
                TextDecorations = item.State == TodoState.Done ? TextDecorations.Strikethrough : null,
                FontWeight = item.State == TodoState.Blocked ? FontWeight.SemiBold : FontWeight.Normal,
            };
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(16 * item.Depth, 1, 0, 1) };
            Grid.SetColumn(label, 1);
            grid.Children.Add(mark);
            grid.Children.Add(label);
            return grid;
        }

        private bool TryWrite(string text)
        {
            try { File.WriteAllText(_path, text); return true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }
    }
}
