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

    /// <summary>
    /// A project overview (docs/NOTES.md), shown: the text at the top left, the containers at their places (their text, sketches and containers in them, or the document they show),
    /// the lines between containers on top. Ctrl+mouse wheel zooms. A todo list in a container is a working list: its tasks are ticked off there.
    /// </summary>
    internal sealed class OverviewView : UserControl
    {
        private static readonly IBrush Surface = new SolidColorBrush(Color.FromRgb(0x2A, 0x25, 0x31));
        private static readonly IBrush ContainerBorder = new SolidColorBrush(Color.FromRgb(0x4A, 0x42, 0x55));
        private static readonly IBrush Page = new SolidColorBrush(Color.FromRgb(0x24, 0x20, 0x28));
        public const double TextWidth = 680;

        private readonly LayoutTransformControl _zoomHost = new();
        private double _zoom = 1;

        public OverviewView(OverviewDocument doc, OverviewContext context)
        {
            var root = new AbsolutePanel { Margin = new Thickness(24) };
            root.Children.Add(new DotLayer());
            var named = new Dictionary<string, Control>(StringComparer.Ordinal);

            var text = MarkdownText(doc.Markdown, context, TextWidth);
            root.Children.Add(text);
            foreach (var element in doc.Children) root.Children.Add(Build(element, context, named));
            var lines = new ConnectorLayer(doc.Lines, named, root) { IsHitTestVisible = false };
            root.Children.Add(lines);

            _zoomHost.Child = root;
            var stack = new StackPanel();
            if (doc.Problems.Count > 0)
            {
                var problems = new TextBlock
                {
                    Text = "Problems in the file:\n" + string.Join("\n", doc.Problems.Select(p => "  line " + p)), FontSize = 12, Foreground = EditorTheme.Yellow,
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24, 12, 24, 0),
                };
                stack.Children.Add(problems);
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
        }

        public double Zoom => _zoom;

        public void SetZoom(double zoom)
        {
            _zoom = Math.Clamp(zoom, 0.25, 4);
            _zoomHost.LayoutTransform = new ScaleTransform(_zoom, _zoom);
        }

        // ---------------------------------------------------------------- elements

        private static Control MarkdownText(string markdown, OverviewContext context, double width)
        {
            if (markdown.Trim().Length == 0) return new Panel { Width = 0, Height = 0 };
            var renderer = new MarkdownRenderer { BaseDirectory = context.BaseDirectory, LinkClicked = context.LinkClicked };
            var page = renderer.Render(markdown);
            if (page is StackPanel sp) sp.Margin = new Thickness(0);
            page.Width = width;
            return page;
        }

        private static Control Build(OvElement element, OverviewContext context, Dictionary<string, Control> named) => element switch
        {
            OvContainer c => Container(c, context, named),
            OvSketch s => Place(new SketchView(s), 0, 0),
            _ => new Panel(),
        };

        private static Control Place(Control control, double x, double y)
        {
            AbsolutePanel.SetPosition(control, new Point(x, y));
            return control;
        }

        private static Control Container(OvContainer c, OverviewContext context, Dictionary<string, Control> named)
        {
            const double padding = 12;
            Control body;
            if (c.Src != null) body = Document(c.Src, context, c.W - 2 * padding);
            else body = MarkdownText(c.Markdown, context, c.W - 2 * padding);

            var box = new Border
            {
                Background = Surface, BorderBrush = ContainerBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
                Padding = new Thickness(padding, padding + (c.Name != null ? 4 : 0), padding, padding), Child = body,
                Width = c.W, MinHeight = 24,
            };
            if (c.H is { } h) { box.Height = h; box.ClipToBounds = true; }

            // the elements in the container: positioned from its top left corner
            var inner = new AbsolutePanel();
            foreach (var child in c.Children) inner.Children.Add(Build(child, context, named));

            var cell = new Grid { Width = c.W };
            cell.Children.Add(box);
            cell.Children.Add(inner);
            if (c.Name != null)
            {
                cell.Children.Add(new Border
                {
                    Background = Page, CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 0), Margin = new Thickness(10, -9, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                    Child = new TextBlock { Text = c.Name, FontSize = 11, Foreground = EditorTheme.TextDim },
                });
                named.TryAdd(c.Name, box);
            }
            return Place(cell, c.X, c.Y);
        }

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
            if (ext != ".md") return FileLink(src, path);

            string text;
            try { text = File.ReadAllText(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new TextBlock { Text = ex.Message, Foreground = EditorTheme.Yellow }; }

            if (OverviewParser.IsOverview(text))
            {
                if (context.Visited.Contains(path)) return FileLink(src, path);
                var visited = new HashSet<string>(context.Visited, StringComparer.OrdinalIgnoreCase) { path };
                var nested = new OverviewView(OverviewParser.Parse(text), new OverviewContext { BaseDirectory = Path.GetDirectoryName(path), LinkClicked = context.LinkClicked, Visited = visited });
                return new Border { ClipToBounds = true, Width = width, Child = nested };
            }
            if (TodoList.Parse(text).Items.Count > 0) return new TodoListView(path, width);
            return MarkdownText(text, new OverviewContext { BaseDirectory = Path.GetDirectoryName(path), LinkClicked = context.LinkClicked }, width);
        }

        private static Control FileLink(string src, string path)
        {
            var link = new TextBlock { Text = "Open " + src, Foreground = new SolidColorBrush(EditorTheme.AccentTextColor), Cursor = new Cursor(StandardCursorType.Hand), FontSize = 13 };
            link.PointerPressed += (_, e) => { OverviewContext.OpenFile?.Invoke(path); e.Handled = true; };
            return link;
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

    /// <summary>The shapes of a sketch, relative to the container it is in.</summary>
    internal sealed class SketchView : Control
    {
        private readonly OvSketch _sketch;
        private static readonly IBrush DefaultStroke = new SolidColorBrush(Color.FromRgb(0xA0, 0x94, 0xA8));

        public SketchView(OvSketch sketch)
        {
            _sketch = sketch;
            IsHitTestVisible = false;
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

        private Rect Extent()
        {
            Rect? all = null;
            foreach (var s in _sketch.Shapes)
            {
                Rect r = s.Kind switch
                {
                    "path" => PathOf(s)?.Bounds ?? default,
                    "text" => new Rect(Box(s).X, Box(s).Y, 8 * (s.Name?.Length ?? 0), 20),
                    _ => new Rect(Box(s).X, Box(s).Y, Box(s).W, Box(s).H),
                };
                all = all == null ? r : all.Value.Union(r);
            }
            return (all ?? default).Inflate(4);
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
                var (x, y, w, h) = Box(s);
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
                            if (pen != null) Arrows(context, geometry, pen, stroke!, s.Attributes.Get("arrow"));
                        }
                        break;
                    case "text":
                        var formatted = new FormattedText(s.Name ?? "", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default,
                            s.Attributes.Number("size") ?? 13, Brush(s.Attributes.Get("color")) ?? fill ?? EditorTheme.Text);
                        context.DrawText(formatted, new Point(x, y));
                        break;
                }
            }
        }

        private static void Arrows(DrawingContext context, Geometry geometry, IPen pen, IBrush brush, string? arrow)
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
    internal sealed class ConnectorLayer : Control, IFillLayer
    {
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
            foreach (var line in _lines)
            {
                if (BoundsOf(line.From) is not { } a || BoundsOf(line.To) is not { } b) continue;
                var start = Edge(a.Inflate(4), b.Center);
                var end = Edge(b.Inflate(4), a.Center);
                var brush = line.Attributes.Get("stroke") is { } s && Color.TryParse(s, out var color) ? new SolidColorBrush(color) : Stroke;
                var pen = new Pen(brush, line.Attributes.Number("width") ?? 1.5, lineCap: PenLineCap.Round)
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
