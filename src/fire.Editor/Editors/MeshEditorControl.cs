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
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using fire.Compiler;
using Pen = Avalonia.Media.Pen;

namespace fire.Editor
{
    /// <summary>
    /// The CAD editor of spark (`.fmesh`, docs/graphics/models.md - ROADMAP 16, milestone 3.1): a model of corners and faces (MeshModel). Four views - from
    /// above, from the front, from the side (drawn as wires on a grid) and in 3D (turned with the middle button) - or one of them large. What is chosen are
    /// corners, edges or faces (1, 2, 3): a click chooses, Shift adds, a drag over empty space chooses what lies in the box, a drag of what is chosen moves it
    /// (in a 2D view on the grid). Draw makes a face of points clicked in a 2D view (on the grid; Enter or a click on the first point closes it). Extrude pulls
    /// faces out (E), Mirror copies them to the other side of a plane through the origin, and the bodies to start from are a box, a cylinder, a sphere and
    /// a plane. Faces have a colour of the palette and belong to a part (a tree of parts that turn around their pivot - what animations move, as in the
    /// block editor). The text of the file is the model (MeshModel.ToJson); forge makes an .f3d of it when a program uses it as a resource, Export writes one.
    /// </summary>
    public sealed class MeshEditorControl : UserControl, IDocumentView
    {
        public enum SelectMode { Vertex, Edge, Face }
        public enum ViewKind { Top, Front, Side, Perspective }

        // ---- a view ---------------------------------------------------------------------------------------------------------------------------
        private sealed class MeshView : Control
        {
            private readonly MeshEditorControl _owner;
            public readonly ViewKind Kind;
            public double Yaw, Pitch, Zoom = 120;
            public double[] Target = { 0, 0.5, 0 };
            public bool NeedsFit = true;
            private Point? _orbit, _pan;
            private static readonly double[] Light = MeshModel.Normalize(new[] { 0.45, 1.0, 0.65 });

            public MeshView(MeshEditorControl owner, ViewKind kind)
            {
                _owner = owner; Kind = kind;
                Focusable = true; ClipToBounds = true;
                switch (kind)
                {
                    case ViewKind.Top: Pitch = 90; break;
                    case ViewKind.Front: break;
                    case ViewKind.Side: Yaw = -90; break;
                    default: Yaw = 35; Pitch = 30; break;
                }
            }

            public bool Is2D => Kind != ViewKind.Perspective;
            public string Title => Kind switch { ViewKind.Top => "Top (x, -z)", ViewKind.Front => "Front (x, y)", ViewKind.Side => "Side (-z, y)", _ => "3D" };

            /// <summary>A point of the model on the screen and its depth (larger: nearer).</summary>
            public (double X, double Y, double Depth) Project(double[] p)
            {
                double qx = p[0] - Target[0], qy = p[1] - Target[1], qz = p[2] - Target[2];
                double cy = Math.Cos(Yaw * Math.PI / 180), sy = Math.Sin(Yaw * Math.PI / 180), cp = Math.Cos(Pitch * Math.PI / 180), sp = Math.Sin(Pitch * Math.PI / 180);
                double x1 = cy * qx + sy * qz, z1 = -sy * qx + cy * qz;
                double y2 = cp * qy - sp * z1, z2 = sp * qy + cp * z1;
                return (Bounds.Width / 2 + x1 * Zoom, Bounds.Height / 2 - y2 * Zoom, z2);
            }

            public Point P(double[] p) { var q = Project(p); return new Point(q.X, q.Y); }

            /// <summary>A direction of the view (right, up, towards the eye) in the model.</summary>
            public double[] Back(double a, double b, double c)
            {
                double cy = Math.Cos(Yaw * Math.PI / 180), sy = Math.Sin(Yaw * Math.PI / 180), cp = Math.Cos(Pitch * Math.PI / 180), sp = Math.Sin(Pitch * Math.PI / 180);
                double qy = cp * b + sp * c, z1 = -sp * b + cp * c;
                return new[] { cy * a - sy * z1, qy, sy * a + cy * z1 };
            }

            /// <summary>The point of the plane of the view through the target under a point of the screen.</summary>
            public double[] PlanePoint(Point p) => MeshModel.Add(Target, Back((p.X - Bounds.Width / 2) / Zoom, (Bounds.Height / 2 - p.Y) / Zoom, 0));

            /// <summary>The ray through a point of the screen (from in front of the model into it).</summary>
            public (double[] Origin, double[] Dir) Ray(Point p) => (MeshModel.Add(PlanePoint(p), Back(0, 0, 1000)), Back(0, 0, -1));

            /// <summary>The axis of the model the view looks along (0 x, 1 y, 2 z) - the depth of a 2D view.</summary>
            public int DepthAxis => Kind switch { ViewKind.Top => 1, ViewKind.Side => 0, _ => 2 };

            protected override void OnPointerPressed(PointerPressedEventArgs e)
            {
                base.OnPointerPressed(e);
                Focus();
                _owner._active = this;
                var point = e.GetCurrentPoint(this);
                bool ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0, shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
                if (point.Properties.IsMiddleButtonPressed || (point.Properties.IsLeftButtonPressed && ctrl && !Is2D))
                {
                    if (Is2D || shift) _pan = point.Position; else _orbit = point.Position;
                    e.Pointer.Capture(this);
                    return;
                }
                if (point.Properties.IsLeftButtonPressed) { e.Pointer.Capture(this); _owner.ViewPressed(this, point.Position, e.KeyModifiers, e.ClickCount); }
                else if (point.Properties.IsRightButtonPressed) _owner.ViewRightClick(this);
            }

            protected override void OnPointerMoved(PointerEventArgs e)
            {
                base.OnPointerMoved(e);
                var p = e.GetPosition(this);
                if (_orbit is { } o)
                {
                    Yaw += (p.X - o.X) * 0.5;
                    Pitch = Math.Clamp(Pitch + (p.Y - o.Y) * 0.4, -89, 89);
                    _orbit = p;
                    InvalidateVisual();
                    return;
                }
                if (_pan is { } q)
                {
                    Target = MeshModel.Sub(Target, Back((p.X - q.X) / Zoom, -(p.Y - q.Y) / Zoom, 0));
                    _pan = p;
                    InvalidateVisual();
                    return;
                }
                _owner.ViewMoved(this, p);
            }

            protected override void OnPointerReleased(PointerReleasedEventArgs e)
            {
                base.OnPointerReleased(e);
                e.Pointer.Capture(null);
                if (_orbit != null || _pan != null) { _orbit = _pan = null; return; }
                _owner.ViewReleased(this, e.GetPosition(this));
            }

            protected override void OnPointerExited(PointerEventArgs e) { base.OnPointerExited(e); _owner.ViewLeft(this); }

            protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
            {
                // zoom around the point under the pointer
                var p = e.GetPosition(this);
                var before = PlanePoint(p);
                Zoom = Math.Clamp(Zoom * (e.Delta.Y > 0 ? 1.15 : 1 / 1.15), 5, 5000);
                var after = PlanePoint(p);
                Target = MeshModel.Add(Target, MeshModel.Sub(before, after));
                InvalidateVisual();
                e.Handled = true;
            }

            private static StreamGeometry Polygon(IReadOnlyList<Point> pts)
            {
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    ctx.BeginFigure(pts[0], true);
                    for (int i = 1; i < pts.Count; i++) ctx.LineTo(pts[i]);
                    ctx.EndFigure(true);
                }
                return g;
            }

            private void DrawGrid(DrawingContext context)
            {
                double step = _owner.Snap;
                while (step * Zoom < 8) step *= 10;
                double w = Bounds.Width, h = Bounds.Height;
                var minor = new Pen(new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)), 1);
                var major = new Pen(new SolidColorBrush(Color.FromArgb(0x48, 0xFF, 0xFF, 0xFF)), 1);
                if (Is2D)
                {
                    var u = Back(1, 0, 0); var v = Back(0, 1, 0);
                    double u0 = MeshModel.Dot(Target, u), v0 = MeshModel.Dot(Target, v);
                    Pen PenOf(double c, double[] along)
                    {
                        if (Math.Abs(c) < 1e-9)
                        {
                            int axis = Math.Abs(along[0]) > 0.5 ? 0 : Math.Abs(along[1]) > 0.5 ? 1 : 2;
                            return new Pen(new SolidColorBrush(axis == 0 ? Color.FromArgb(0xB0, 0xE0, 0x50, 0x50) : axis == 1 ? Color.FromArgb(0xB0, 0x60, 0xC8, 0x60) : Color.FromArgb(0xB0, 0x50, 0x80, 0xE8)), 1.5);
                        }
                        return Math.Abs(c / (step * 10) - Math.Round(c / (step * 10))) < 1e-6 ? major : minor;
                    }
                    for (double k = Math.Floor((u0 - w / 2 / Zoom) / step); k <= Math.Ceiling((u0 + w / 2 / Zoom) / step); k++)
                    {
                        double x = w / 2 + (k * step - u0) * Zoom;
                        context.DrawLine(PenOf(k * step, v), new Point(x, 0), new Point(x, h));
                    }
                    for (double k = Math.Floor((v0 - h / 2 / Zoom) / step); k <= Math.Ceiling((v0 + h / 2 / Zoom) / step); k++)
                    {
                        double y = h / 2 - (k * step - v0) * Zoom;
                        context.DrawLine(PenOf(k * step, u), new Point(0, y), new Point(w, y));
                    }
                    return;
                }
                // 3D: the floor (y = 0) around the model
                var (min, max) = _owner._model.Bounds();
                double reach = Math.Ceiling(Math.Max(1, Math.Max(Math.Max(Math.Abs(min[0]), Math.Abs(max[0])), Math.Max(Math.Abs(min[2]), Math.Abs(max[2]))) + 0.5) / step) * step;
                int n = (int)Math.Round(reach / step);
                if (n > 200) { step = reach / 100; n = 100; }
                for (int i = -n; i <= n; i++)
                {
                    var pen = i == 0 ? major : minor;
                    context.DrawLine(pen, P(new[] { i * step, 0, -reach }), P(new[] { i * step, 0, reach }));
                    context.DrawLine(pen, P(new[] { -reach, 0, i * step }), P(new[] { reach, 0, i * step }));
                }
            }

            public override void Render(DrawingContext context)
            {
                var o = _owner;
                var m = o._model;
                if (NeedsFit && Bounds.Width > 20 && Bounds.Height > 20) o.Fit(this);
                context.FillRectangle(EditorTheme.Solid(Is2D ? Color.FromRgb(0x1E, 0x1C, 0x22) : Color.FromRgb(0x1B, 0x18, 0x1E)), new Rect(Bounds.Size));
                DrawGrid(context);
                var pose = o._pose;
                double[] Pt(int v, int part)
                {
                    var p = m.Vertices[v];
                    if (pose == null || part >= pose.Length) return p;
                    var a = pose[part];
                    return new[] { a[0] * p[0] + a[1] * p[1] + a[2] * p[2] + a[3], a[4] * p[0] + a[5] * p[1] + a[6] * p[2] + a[7], a[8] * p[0] + a[9] * p[1] + a[10] * p[2] + a[11] };
                }
                var toEye = Back(0, 0, 1);
                var selF = o._selFaces; var selE = o._selEdges; var selV = o._selVerts;
                var orange = Color.FromRgb(0xFF, 0x9A, 0x30);
                if (!Is2D)
                {
                    // shaded faces, far to near; the back of a face is grey
                    var faces = new List<(double Depth, int Face, Point[] Pts, double[] N)>();
                    for (int f = 0; f < m.Faces.Count; f++)
                    {
                        var face = m.Faces[f];
                        var pts = face.V.Select(v => Pt(v, face.Part)).ToList();
                        var n = new double[3];
                        for (int i = 0; i < pts.Count; i++)
                        {
                            var a = pts[i]; var b = pts[(i + 1) % pts.Count];
                            n[0] += (a[1] - b[1]) * (a[2] + b[2]); n[1] += (a[2] - b[2]) * (a[0] + b[0]); n[2] += (a[0] - b[0]) * (a[1] + b[1]);
                        }
                        n = MeshModel.Normalize(n);
                        double depth = pts.Average(p => Project(p).Depth);
                        faces.Add((depth, f, pts.Select(P).ToArray(), n));
                    }
                    faces.Sort((a, b) => a.Depth.CompareTo(b.Depth));
                    var edge = new Pen(new SolidColorBrush(Color.FromArgb(0x50, 0, 0, 0)), 1);
                    foreach (var (_, f, pts, n) in faces)
                    {
                        var face = m.Faces[f];
                        Color color;
                        if (MeshModel.Dot(n, toEye) < 0) color = Color.FromRgb(0x48, 0x44, 0x4C);
                        else
                        {
                            double shade = 0.5 + 0.5 * Math.Max(0, MeshModel.Dot(n, Light));
                            uint rgb = m.Palette[Math.Clamp(face.Color, 0, m.Palette.Count - 1)];
                            color = Color.FromRgb((byte)(((rgb >> 16) & 255) * shade), (byte)(((rgb >> 8) & 255) * shade), (byte)((rgb & 255) * shade));
                            if (face.Part != o.CurrentPart) color = Color.FromRgb((byte)(color.R * 0.7 + 30), (byte)(color.G * 0.7 + 30), (byte)(color.B * 0.7 + 30));
                        }
                        context.DrawGeometry(new SolidColorBrush(color), edge, Polygon(pts));
                        if (selF.Contains(f)) context.DrawGeometry(new SolidColorBrush(Color.FromArgb(0x70, orange.R, orange.G, orange.B)), new Pen(new SolidColorBrush(orange), 1.5), Polygon(pts));
                    }
                }
                else
                {
                    // wires: every edge once, the edges of other parts dimmer; the faces chosen filled
                    for (int f = 0; f < m.Faces.Count; f++)
                        if (selF.Contains(f)) context.DrawGeometry(new SolidColorBrush(Color.FromArgb(0x50, orange.R, orange.G, orange.B)), null, Polygon(m.Faces[f].V.Select(v => P(Pt(v, m.Faces[f].Part))).ToArray()));
                    var mine = new Pen(new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xD0)), 1);
                    var others = new Pen(new SolidColorBrush(Color.FromArgb(0x80, 0xA0, 0xA0, 0xA8)), 1);
                    var seen = new HashSet<(int, int)>();
                    foreach (var face in m.Faces)
                        for (int i = 0; i < face.V.Count; i++)
                        {
                            int a = face.V[i], b = face.V[(i + 1) % face.V.Count];
                            if (!seen.Add(MeshModel.Edge(a, b))) continue;
                            context.DrawLine(face.Part == o.CurrentPart ? mine : others, P(Pt(a, face.Part)), P(Pt(b, face.Part)));
                        }
                }
                // what is chosen and what is under the pointer
                var selPen = new Pen(new SolidColorBrush(orange), 2.5);
                foreach (var (a, b) in selE) if (a < m.Vertices.Count && b < m.Vertices.Count) context.DrawLine(selPen, P(m.Vertices[a]), P(m.Vertices[b]));
                if (o._mode == SelectMode.Vertex || selV.Count > 0)
                {
                    var dot = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE8));
                    if (o._mode == SelectMode.Vertex && o._pose == null) foreach (var v in m.Vertices) { var q = P(v); context.FillRectangle(dot, new Rect(q.X - 2, q.Y - 2, 4, 4)); }
                    foreach (int v in selV) if (v < m.Vertices.Count) { var q = P(m.Vertices[v]); context.FillRectangle(new SolidColorBrush(orange), new Rect(q.X - 3.5, q.Y - 3.5, 7, 7)); }
                }
                if (o._hover is { } hv && o._hoverView == this && o._pose == null)
                {
                    var white = new Pen(Brushes.White, 2);
                    if (hv.Kind == SelectMode.Vertex && hv.A < m.Vertices.Count) { var q = P(m.Vertices[hv.A]); context.DrawRectangle(null, white, new Rect(q.X - 5, q.Y - 5, 10, 10)); }
                    else if (hv.Kind == SelectMode.Edge && hv.B < m.Vertices.Count) context.DrawLine(white, P(m.Vertices[hv.A]), P(m.Vertices[hv.B]));
                    else if (hv.Kind == SelectMode.Face && hv.A < m.Faces.Count) context.DrawGeometry(null, white, Polygon(m.Faces[hv.A].V.Select(v => P(m.Vertices[v])).ToArray()));
                }
                // the pivot of the part chosen
                if (o.CurrentPart >= 0 && o.CurrentPart < m.Parts.Count)
                {
                    var pv = m.Parts[o.CurrentPart].Pivot;
                    var pp = P(pose != null && o.CurrentPart < pose.Length ? Pt2(pose[o.CurrentPart], pv) : pv);
                    context.DrawEllipse(null, new Pen(Brushes.Orange, 2), pp, 4, 4);
                }
                // the face being drawn
                if (o._drawing.Count > 0 && o._drawView == this)
                {
                    var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x60, 0xC0, 0xFF)), 2);
                    var pts = o._drawing.Select(P).ToList();
                    if (o._drawHover is { } dh) pts.Add(P(dh));
                    for (int i = 0; i + 1 < pts.Count; i++) context.DrawLine(pen, pts[i], pts[i + 1]);
                    foreach (var q in pts) context.DrawEllipse(Brushes.White, null, q, 3, 3);
                }
                // the box being dragged
                if (o._boxStart is { } bs && o._boxView == this && o._boxEnd is { } be)
                    context.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x20, 0x60, 0xC0, 0xFF)), new Pen(new SolidColorBrush(Color.FromRgb(0x60, 0xC0, 0xFF)), 1, new DashStyle(new double[] { 4, 3 }, 0)), new Rect(bs, be).Normalize());
                var title = new FormattedText(Title, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default), 11, new SolidColorBrush(Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF)));
                context.DrawText(title, new Point(6, 4));
                if (o._active == this) context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(0x60, 0x60, 0xC0, 0xFF)), 1), new Rect(Bounds.Size).Deflate(0.5));
            }

            private static double[] Pt2(double[] a, double[] p) =>
                new[] { a[0] * p[0] + a[1] * p[1] + a[2] * p[2] + a[3], a[4] * p[0] + a[5] * p[1] + a[6] * p[2] + a[7], a[8] * p[0] + a[9] * p[1] + a[10] * p[2] + a[11] };
        }

        // ---- the state ------------------------------------------------------------------------------------------------------------------------
        private MeshModel _model = MeshModel.CreateDefault();
        private readonly MeshView[] _views;
        private MeshView _active;
        private readonly Grid _viewGrid = new() { ColumnDefinitions = new ColumnDefinitions("*,2,*"), RowDefinitions = new RowDefinitions("*,2,*") };
        private readonly ModelPaletteStrip _palette = new();
        private readonly ModelAnimationBar _animBar = new("meters", 0.1m);
        private readonly TextBlock _info = new() { Foreground = EditorTheme.TextDim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
        private readonly ComboBox _parts = new() { MinWidth = 110 };
        private readonly ComboBox _layout = new() { ItemsSource = new[] { "4 views", "Top", "Front", "Side", "3D" }, SelectedIndex = 0, MinWidth = 80 };
        private readonly NumericUpDown _snap = new() { Minimum = 0.001m, Maximum = 10, Increment = 0.05m, Value = 0.1m, FormatString = "0.###", Width = 64, ShowButtonSpinner = false };
        private readonly ToggleButton[] _modeButtons = new ToggleButton[3];
        private readonly ToggleButton _drawButton;
        private readonly ToggleButton _mirrorCopy = ModelEditorUi.Toggle("Copy", "Mirror makes a copy (the halves share the corners on the plane); off: what is chosen itself goes to the other side", on: true);
        private readonly List<string> _undo = new(), _redo = new();
        private string _savedText = "", _text = "";
        private int _color = 9, _part;
        private bool _updatingParts;
        private SelectMode _mode = SelectMode.Face;
        private readonly HashSet<int> _selVerts = new(), _selFaces = new();
        private readonly HashSet<(int, int)> _selEdges = new();
        private readonly List<int> _vertOrder = new();          // the corners in the order they were chosen (a face of them)
        private double[][]? _pose;
        // the pointer: what is under it, a drag of what is chosen, a box being dragged, a face being drawn
        private (SelectMode Kind, int A, int B)? _hover;
        private MeshView? _hoverView;
        private MeshView? _dragView;
        private Point _dragStart;
        private string? _dragBefore;
        private Dictionary<int, double[]>? _dragFrom;
        private bool _dragMoved;
        private Point? _boxStart, _boxEnd;
        private MeshView? _boxView;
        private bool _boxAdd;
        private readonly List<double[]> _drawing = new();
        private double[]? _drawHover;
        private MeshView? _drawView;
        private double _extrudeDistance = 0.5;
        private static (List<double[]> Vertices, List<MeshModel.Face> Faces)? s_clipboard;   // (shared by the mesh models of spark)

        public string? FilePath { get; set; }
        public bool IsReadOnly => false;
        public bool IsModified => _text != _savedText;
        public event Action? ModifiedChanged;
        public event Action<int>? CaretLineChanged { add { } remove { } }

        /// <summary>The model as it is now (for the tests and the host).</summary>
        public MeshModel Model => _model;
        public SelectMode Mode { get => _mode; set => SetMode(value); }
        public IReadOnlyCollection<int> SelectedFaces => _selFaces;
        public IReadOnlyCollection<int> SelectedVertices => _selVerts;
        public double Snap => (double)(_snap.Value ?? 0.1m) > 0 ? (double)(_snap.Value ?? 0.1m) : 0.1;
        public int CurrentPart => _part;
        public int CurrentColor { get => _color; set { _color = Math.Clamp(value, 0, _model.Palette.Count - 1); RefreshViews(); } }

        public MeshEditorControl()
        {
            _views = new[] { new MeshView(this, ViewKind.Top), new MeshView(this, ViewKind.Perspective), new MeshView(this, ViewKind.Front), new MeshView(this, ViewKind.Side) };
            _active = _views[1];
            var bar = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 2) };
            string[] modeNames = { "Vertex", "Edge", "Face" };
            string[] modeTips = { "Choose corners (1)", "Choose edges (2)", "Choose faces (3)" };
            for (int i = 0; i < 3; i++)
            {
                var mode = (SelectMode)i;
                _modeButtons[i] = ModelEditorUi.Toggle(modeNames[i], modeTips[i], mode == _mode);
                _modeButtons[i].Click += (_, _) => SetMode(mode);
                bar.Children.Add(_modeButtons[i]);
            }
            _drawButton = ModelEditorUi.Toggle("Draw", "Draw a face in a 2D view: click its corners (on the grid), Enter or a click on the first corner closes it, Esc lets go (D)");
            _drawButton.Click += (_, _) => { if (_drawButton.IsChecked != true) CancelDrawing(); };
            bar.Children.Add(_drawButton);
            bar.Children.Add(ModelEditorUi.Separator());
            bar.Children.Add(ModelEditorUi.Label("Add"));
            bar.Children.Add(ModelEditorUi.Button("Box", "A box of a meter standing on the origin", () => AddBody("box")));
            bar.Children.Add(ModelEditorUi.Button("Cylinder", "A cylinder (12 sides) standing on the origin", () => AddBody("cylinder")));
            bar.Children.Add(ModelEditorUi.Button("Sphere", "A sphere (12 x 8) on the origin", () => AddBody("sphere")));
            bar.Children.Add(ModelEditorUi.Button("Plane", "A square of a meter on the floor (to extrude)", () => AddBody("plane")));
            bar.Children.Add(ModelEditorUi.Separator());
            bar.Children.Add(ModelEditorUi.Button("Extrude...", "Pulls the faces chosen out (a negative distance pushes them in); E: by the last distance", async () => await AskExtrude()));
            bar.Children.Add(ModelEditorUi.Button("Mirror X", "Mirrors what is chosen to the other side of x = 0 (a copy, or itself when Copy is off)", () => MirrorSelection(0)));
            bar.Children.Add(ModelEditorUi.Button("Y", "Mirrors what is chosen to the other side of y = 0", () => MirrorSelection(1)));
            bar.Children.Add(ModelEditorUi.Button("Z", "Mirrors what is chosen to the other side of z = 0", () => MirrorSelection(2)));
            bar.Children.Add(_mirrorCopy);
            bar.Children.Add(ModelEditorUi.Button("Flip", "Turns the faces chosen round (the other side is seen)", FlipSelection));
            bar.Children.Add(ModelEditorUi.Button("Merge", "Puts the corners chosen together at their middle (M)", MergeSelection));
            bar.Children.Add(ModelEditorUi.Button("Weld", "Joins corners closer than a millimeter (the corners chosen, or all)", WeldSelection));
            bar.Children.Add(ModelEditorUi.Button("Split", "A corner in the middle of each edge chosen", SplitSelection));
            bar.Children.Add(ModelEditorUi.Button("Face", "A face through the corners chosen; two corners of a face: cuts it along them (F)", MakeFace));
            bar.Children.Add(ModelEditorUi.Button("Move...", "Moves, turns or scales what is chosen by numbers", async () => await AskTransform()));
            bar.Children.Add(ModelEditorUi.Separator());
            bar.Children.Add(ModelEditorUi.Label("Grid"));
            ToolTip.SetTip(_snap, "The step of the grid in meters: drawing and moving in a 2D view snap to it, the arrow keys move by it");
            _snap.ValueChanged += (_, _) => RefreshViews();
            bar.Children.Add(_snap);
            bar.Children.Add(_layout);
            _layout.SelectionChanged += (_, _) => ArrangeViews();
            bar.Children.Add(ModelEditorUi.Separator());
            bar.Children.Add(ModelEditorUi.Label("Part"));
            bar.Children.Add(_parts);
            bar.Children.Add(ModelEditorUi.Button("+", "A new part under the part chosen (its pivot: the middle of what is chosen)", async () => await AddPart()));
            bar.Children.Add(ModelEditorUi.Button("Assign", "The faces chosen belong to the part chosen", AssignPart));
            bar.Children.Add(ModelEditorUi.Button("Pivot...", "Where the part turns (meters: x, y, z)", async () => await EditPivot()));
            bar.Children.Add(ModelEditorUi.Separator());
            bar.Children.Add(ModelEditorUi.Button("Preview", "scene3d draws the model as a program shows it (with the animation chosen)", () =>
            {
                var window = new BlockPreviewWindow(MeshModel.Parse(_model.ToJson()), _animBar.Index);
                if (TopLevel.GetTopLevel(this) is Window owner) window.Show(owner); else window.Show();
            }));
            bar.Children.Add(ModelEditorUi.Button("Export .f3d...", "Writes the model file that scene3d reads (a program can also take the .fmesh as a resource: forge converts it)", async () => await Export()));

            _animBar.Animations = () => _model.Animations;
            _animBar.CurrentPart = () => _part;
            _animBar.Change = Change;
            _animBar.PoseChanged += RefreshViews;

            var paletteBar = new DockPanel { Margin = new Thickness(6, 4) };
            var paletteButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Top };
            paletteButtons.Children.Add(ModelEditorUi.Button("+ Colour", "A new colour in the palette", async () => await AddColor()));
            paletteButtons.Children.Add(ModelEditorUi.Button("Edit...", "Change the chosen colour (double-click a colour does the same)", async () => await EditColor(_color)));
            paletteButtons.Children.Add(ModelEditorUi.Button("− Colour", "Take the chosen colour out (its faces take the first colour)", RemoveColor));
            DockPanel.SetDock(paletteButtons, Avalonia.Controls.Dock.Right);
            paletteBar.Children.Add(paletteButtons);
            paletteBar.Children.Add(_palette);
            var help = new TextBlock
            {
                Text = "Click chooses, Shift adds, drag on empty space chooses a box, drag what is chosen moves it; middle button pans (3D: turns, Shift pans), wheel zooms. " +
                       "1/2/3 corners/edges/faces, D draw, E extrude, F face, M merge, Delete, arrows move by the grid (Page Up/Down: in depth), a colour paints the faces chosen.",
                Foreground = EditorTheme.TextDim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 2),
            };
            var status = new DockPanel();
            status.Children.Add(_info);

            ArrangeViews();
            var dock = new DockPanel();
            DockPanel.SetDock(bar, Avalonia.Controls.Dock.Top);
            DockPanel.SetDock(_animBar, Avalonia.Controls.Dock.Top);
            DockPanel.SetDock(help, Avalonia.Controls.Dock.Bottom);
            DockPanel.SetDock(status, Avalonia.Controls.Dock.Bottom);
            DockPanel.SetDock(paletteBar, Avalonia.Controls.Dock.Bottom);
            dock.Children.Add(bar); dock.Children.Add(_animBar); dock.Children.Add(help); dock.Children.Add(status); dock.Children.Add(paletteBar); dock.Children.Add(_viewGrid);
            Content = dock;
            Focusable = true;

            AddHandler(KeyDownEvent, (_, e) => OnEditorKey(e), Avalonia.Interactivity.RoutingStrategies.Tunnel);
            _palette.Picked += i => { _color = i; PaintSelection(); RefreshViews(); };
            _palette.EditRequested += async i => { _color = i; await EditColor(i); };
            _parts.SelectionChanged += (_, _) =>
            {
                if (_updatingParts || _parts.SelectedIndex < 0) return;
                _part = _parts.SelectedIndex;
                RefreshViews();
            };
            Attach(MeshModel.CreateDefault(), resetUndo: true);
        }

        private void ArrangeViews()
        {
            _viewGrid.Children.Clear();
            int single = _layout.SelectedIndex - 1;   // -1: four views
            if (single < 0)
            {
                _viewGrid.ColumnDefinitions = new ColumnDefinitions("*,2,*");
                _viewGrid.RowDefinitions = new RowDefinitions("*,2,*");
                (int Row, int Col)[] at = { (0, 0), (0, 2), (2, 0), (2, 2) };
                for (int i = 0; i < 4; i++)
                {
                    Grid.SetRow(_views[i], at[i].Row); Grid.SetColumn(_views[i], at[i].Col);
                    _viewGrid.Children.Add(_views[i]);
                }
                var h = new Border { Background = EditorTheme.Border }; Grid.SetRow(h, 1); Grid.SetColumnSpan(h, 3);
                var v = new Border { Background = EditorTheme.Border }; Grid.SetColumn(v, 1); Grid.SetRowSpan(v, 3);
                _viewGrid.Children.Add(h); _viewGrid.Children.Add(v);
            }
            else
            {
                _viewGrid.ColumnDefinitions = new ColumnDefinitions("*");
                _viewGrid.RowDefinitions = new RowDefinitions("*");
                var view = _views.First(x => x.Kind == (ViewKind)single);
                Grid.SetRow(view, 0); Grid.SetColumn(view, 0);
                _viewGrid.Children.Add(view);
                _active = view;
            }
        }

        private void Attach(MeshModel model, bool resetUndo)
        {
            _model = model;
            _palette.Palette = model.Palette;
            _color = Math.Clamp(_color, 0, model.Palette.Count - 1);
            _part = Math.Clamp(_part, 0, model.Parts.Count - 1);
            if (resetUndo) { _undo.Clear(); _redo.Clear(); }
            _text = model.ToJson();
            ClearSelection(refresh: false);
            RefreshParts();
            _animBar.RefreshList();
            RefreshViews();
        }

        /// <summary>The views look at the whole model (a zoom that fits it; a view not laid out yet does it when it is first drawn).</summary>
        public void FitViews() { foreach (var v in _views) { v.NeedsFit = true; v.InvalidateVisual(); } }

        private void Fit(MeshView view)
        {
            view.NeedsFit = false;
            var (min, max) = _model.Bounds();
            var center = MeshModel.Mul(MeshModel.Add(min, max), 0.5);
            // the extent of the model on the screen of the view (its box seen from there)
            double w = 0.5, h = 0.5;
            var right = view.Back(1, 0, 0); var up = view.Back(0, 1, 0);
            for (int i = 0; i < 8; i++)
            {
                var c = new[] { (i & 1) != 0 ? max[0] : min[0], (i & 2) != 0 ? max[1] : min[1], (i & 4) != 0 ? max[2] : min[2] };
                var d = MeshModel.Sub(c, center);
                w = Math.Max(w, 2 * Math.Abs(MeshModel.Dot(d, right))); h = Math.Max(h, 2 * Math.Abs(MeshModel.Dot(d, up)));
            }
            view.Zoom = Math.Clamp(Math.Min(view.Bounds.Width / w, view.Bounds.Height / h) * 0.7, 5, 5000);
            view.Target = center;
        }
        private void RefreshParts()
        {
            _updatingParts = true;
            _parts.ItemsSource = _model.Parts.Select((p, i) => (i == 0 ? "" : "  ") + p.Name).ToList();
            _parts.SelectedIndex = Math.Clamp(_part, 0, _model.Parts.Count - 1);
            _updatingParts = false;
        }

        private void RefreshViews()
        {
            _palette.Current = _color;
            _palette.InvalidateMeasure(); _palette.InvalidateVisual();
            var anim = _animBar.Current;
            _pose = anim != null ? BlockModel.PoseMatricesOf(_model.Parts, anim, _animBar.Time) : null;
            _animBar.Refresh();
            string chosen = _mode switch { SelectMode.Vertex => $"{_selVerts.Count} corners", SelectMode.Edge => $"{_selEdges.Count} edges", _ => $"{_selFaces.Count} faces" };
            _info.Text = $"{_model.Vertices.Count} corners, {_model.Faces.Count} faces, {_model.TriangleCount} triangles - chosen: {chosen}";
            foreach (var v in _views) v.InvalidateVisual();
        }

        /// <summary>A change of the model: undo keeps the text before it.</summary>
        private void Change(Action change)
        {
            string before = _model.ToJson();
            change();
            Commit(before);
        }

        private void Commit(string before)
        {
            string after = _model.ToJson();
            if (after == before) { RefreshViews(); return; }
            _undo.Add(before);
            if (_undo.Count > 200) _undo.RemoveAt(0);
            _redo.Clear();
            _text = after;
            PruneSelection();
            RefreshViews();
            ModifiedChanged?.Invoke();
        }

        private bool Editable()
        {
            if (_animBar.Index < 0) return true;
            _info.Text = "The model is built in the rest pose (Animation: rest pose).";
            return false;
        }

        // ---- choosing ----

        public void SetMode(SelectMode mode)
        {
            if (mode == _mode) { RefreshModeButtons(); return; }
            // what is chosen goes along: faces -> their corners/edges, corners -> the faces/edges between them
            var verts = SelectionVertices();
            _mode = mode;
            ClearSelection(refresh: false);
            if (verts.Count > 0)
            {
                if (mode == SelectMode.Vertex) { _selVerts.UnionWith(verts); _vertOrder.AddRange(verts); }
                else if (mode == SelectMode.Face) _selFaces.UnionWith(_model.FacesWithin(verts));
                else foreach (var e in _model.Edges()) if (verts.Contains(e.A) && verts.Contains(e.B)) _selEdges.Add(e);
            }
            RefreshModeButtons();
            RefreshViews();
        }

        private void RefreshModeButtons() { for (int i = 0; i < 3; i++) _modeButtons[i].IsChecked = i == (int)_mode; }

        public void ClearSelection(bool refresh = true)
        {
            _selVerts.Clear(); _selEdges.Clear(); _selFaces.Clear(); _vertOrder.Clear();
            if (refresh) RefreshViews();
        }

        private void PruneSelection()
        {
            _selVerts.RemoveWhere(v => v >= _model.Vertices.Count);
            _vertOrder.RemoveAll(v => v >= _model.Vertices.Count);
            _selFaces.RemoveWhere(f => f >= _model.Faces.Count);
            var edges = new HashSet<(int, int)>(_model.Edges());
            _selEdges.RemoveWhere(e => !edges.Contains(e));
        }

        /// <summary>The corners of what is chosen.</summary>
        public HashSet<int> SelectionVertices()
        {
            var set = new HashSet<int>(_selVerts);
            foreach (var (a, b) in _selEdges) { set.Add(a); set.Add(b); }
            foreach (int f in _selFaces) if (f < _model.Faces.Count) set.UnionWith(_model.Faces[f].V);
            return set;
        }

        /// <summary>The faces of what is chosen (the faces chosen, or those whose corners are all chosen).</summary>
        public List<int> SelectionFaces() => _mode == SelectMode.Face ? _selFaces.OrderBy(f => f).ToList() : _model.FacesWithin(SelectionVertices());

        public void SelectFaces(IEnumerable<int> faces)
        {
            ClearSelection(refresh: false);
            _mode = SelectMode.Face;
            _selFaces.UnionWith(faces);
            RefreshModeButtons();
            RefreshViews();
        }

        public void SelectVertices(IEnumerable<int> vertices)
        {
            ClearSelection(refresh: false);
            _mode = SelectMode.Vertex;
            foreach (int v in vertices) if (_selVerts.Add(v)) _vertOrder.Add(v);
            RefreshModeButtons();
            RefreshViews();
        }

        public void SelectAll()
        {
            ClearSelection(refresh: false);
            if (_mode == SelectMode.Vertex) { _selVerts.UnionWith(Enumerable.Range(0, _model.Vertices.Count)); _vertOrder.AddRange(_selVerts); }
            else if (_mode == SelectMode.Edge) _selEdges.UnionWith(_model.Edges());
            else _selFaces.UnionWith(Enumerable.Range(0, _model.Faces.Count));
            RefreshViews();
        }

        /// <summary>What is under a point of a view (in the mode chosen): a corner within 8 pixels, an edge within 6, a face the ray hits.</summary>
        private (SelectMode Kind, int A, int B)? Pick(MeshView view, Point p)
        {
            if (_mode == SelectMode.Vertex)
            {
                int best = -1; double bestD = 8, bestDepth = double.MinValue;
                for (int v = 0; v < _model.Vertices.Count; v++)
                {
                    var q = view.Project(_model.Vertices[v]);
                    double d = Math.Sqrt((q.X - p.X) * (q.X - p.X) + (q.Y - p.Y) * (q.Y - p.Y));
                    if (d < bestD - 0.5 || (Math.Abs(d - bestD) <= 0.5 && q.Depth > bestDepth)) { best = v; bestD = d; bestDepth = q.Depth; }
                }
                return best >= 0 ? (SelectMode.Vertex, best, -1) : null;
            }
            if (_mode == SelectMode.Edge)
            {
                (int, int)? best = null; double bestD = 6;
                foreach (var (a, b) in _model.Edges())
                {
                    var pa = view.P(_model.Vertices[a]); var pb = view.P(_model.Vertices[b]);
                    double d = SegmentDistance(p, pa, pb);
                    if (d < bestD) { bestD = d; best = (a, b); }
                }
                return best is { } e ? (SelectMode.Edge, e.Item1, e.Item2) : null;
            }
            var (o, dir) = view.Ray(p);
            return _model.Raycast(o, dir, out int face, out _) ? (SelectMode.Face, face, -1) : null;
        }

        private static double SegmentDistance(Point p, Point a, Point b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y, l = dx * dx + dy * dy;
            double t = l < 1e-9 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / l, 0, 1);
            double x = a.X + dx * t - p.X, y = a.Y + dy * t - p.Y;
            return Math.Sqrt(x * x + y * y);
        }

        private bool IsSelected((SelectMode Kind, int A, int B) h) =>
            h.Kind == SelectMode.Vertex ? _selVerts.Contains(h.A) : h.Kind == SelectMode.Edge ? _selEdges.Contains((h.A, h.B)) : _selFaces.Contains(h.A);

        private void Toggle((SelectMode Kind, int A, int B) h, bool add)
        {
            if (!add) ClearSelection(refresh: false);
            switch (h.Kind)
            {
                case SelectMode.Vertex:
                    if (add && _selVerts.Remove(h.A)) _vertOrder.Remove(h.A);
                    else if (_selVerts.Add(h.A)) _vertOrder.Add(h.A);
                    break;
                case SelectMode.Edge: if (!(add && _selEdges.Remove((h.A, h.B)))) _selEdges.Add((h.A, h.B)); break;
                default: if (!(add && _selFaces.Remove(h.A))) _selFaces.Add(h.A); break;
            }
        }

        // ---- the pointer in a view ----

        private double SnapValue(double v) => Math.Round(v / Snap) * Snap;

        private double[] SnapPoint(MeshView view, double[] p)
        {
            var q = (double[])p.Clone();
            for (int a = 0; a < 3; a++) if (a != view.DepthAxis) q[a] = SnapValue(q[a]);
            q[view.DepthAxis] = 0;
            return q;
        }

        private void ViewPressed(MeshView view, Point p, KeyModifiers mods, int clicks)
        {
            if (_pose != null) { Editable(); return; }
            if (_drawButton.IsChecked == true)
            {
                if (!view.Is2D) { _info.Text = "A face is drawn in a 2D view (Top, Front, Side)."; return; }
                if (_drawView != view) { _drawing.Clear(); _drawView = view; }
                var q = SnapPoint(view, view.PlanePoint(p));
                if (_drawing.Count >= 3 && Distance(view.P(_drawing[0]), view.P(q)) < 8) { FinishDrawing(); return; }
                if (_drawing.Count == 0 || MeshModel.Length(MeshModel.Sub(_drawing[^1], q)) > 1e-9) _drawing.Add(q);
                view.InvalidateVisual();
                return;
            }
            bool add = (mods & KeyModifiers.Shift) != 0;
            var hit = Pick(view, p);
            if (hit is { } h)
            {
                if (!IsSelected(h) || add) Toggle(h, add);
                if (IsSelected(h) && !add) BeginDrag(view, p);
                RefreshViews();
                return;
            }
            // empty space: a box to choose
            _boxView = view; _boxStart = p; _boxEnd = p; _boxAdd = add;
        }

        private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

        private void BeginDrag(MeshView view, Point p)
        {
            _dragView = view; _dragStart = p; _dragMoved = false;
            _dragBefore = _model.ToJson();
            _dragFrom = SelectionVertices().ToDictionary(v => v, v => (double[])_model.Vertices[v].Clone());
        }

        private void ViewMoved(MeshView view, Point p)
        {
            if (_dragView == view && _dragFrom != null)
            {
                if (!_dragMoved && Distance(p, _dragStart) < 3) return;
                _dragMoved = true;
                var by = view.Back((p.X - _dragStart.X) / view.Zoom, -(p.Y - _dragStart.Y) / view.Zoom, 0);
                if (view.Is2D) for (int a = 0; a < 3; a++) by[a] = a == view.DepthAxis ? 0 : SnapValue(by[a]);
                foreach (var (v, from) in _dragFrom) _model.Vertices[v] = MeshModel.Add(from, by);
                foreach (var w in _views) w.InvalidateVisual();
                _info.Text = $"Moved by {Fmt(by[0])}, {Fmt(by[1])}, {Fmt(by[2])} m";
                return;
            }
            if (_boxView == view && _boxStart != null) { _boxEnd = p; view.InvalidateVisual(); return; }
            if (_drawButton.IsChecked == true && view.Is2D && _drawView == view)
            {
                _drawHover = SnapPoint(view, view.PlanePoint(p));
                view.InvalidateVisual();
                return;
            }
            var hover = _pose == null ? Pick(view, p) : null;
            if (hover != _hover || _hoverView != view)
            {
                var old = _hoverView;
                _hover = hover; _hoverView = view;
                old?.InvalidateVisual();
                view.InvalidateVisual();
            }
        }

        private void ViewReleased(MeshView view, Point p)
        {
            if (_dragView == view && _dragBefore != null)
            {
                string before = _dragBefore;
                _dragView = null; _dragBefore = null; _dragFrom = null;
                if (_dragMoved) Commit(before);
                return;
            }
            if (_boxView == view && _boxStart is { } start)
            {
                var rect = new Rect(start, p).Normalize();
                _boxStart = _boxEnd = null; _boxView = null;
                if (!_boxAdd) ClearSelection(refresh: false);
                if (rect.Width < 3 && rect.Height < 3) { RefreshViews(); return; }
                bool Inside(double[] q) => rect.Contains(view.P(q));
                if (_mode == SelectMode.Vertex) { for (int v = 0; v < _model.Vertices.Count; v++) if (Inside(_model.Vertices[v]) && _selVerts.Add(v)) _vertOrder.Add(v); }
                else if (_mode == SelectMode.Edge) { foreach (var e in _model.Edges()) if (Inside(_model.Vertices[e.A]) && Inside(_model.Vertices[e.B])) _selEdges.Add(e); }
                else for (int f = 0; f < _model.Faces.Count; f++) if (_model.Faces[f].V.All(v => Inside(_model.Vertices[v]))) _selFaces.Add(f);
                RefreshViews();
            }
        }

        private void ViewLeft(MeshView view)
        {
            if (_hoverView == view) { _hover = null; view.InvalidateVisual(); }
        }

        private void ViewRightClick(MeshView view)
        {
            if (_drawing.Count > 0) { CancelDrawing(); return; }
            ClearSelection();
        }

        private void FinishDrawing()
        {
            var view = _drawView;
            var points = _drawing.ToList();
            CancelDrawing();
            if (view == null || points.Count < 3 || !Editable()) return;
            int face = -1;
            Change(() =>
            {
                int first = _model.Vertices.Count;
                foreach (var q in points) _model.Vertices.Add(q);
                var loop = Enumerable.Range(first, points.Count).ToList();
                if (MeshModel.Dot(_model.Normal(loop), view.Back(0, 0, 1)) < 0) loop.Reverse();   // (seen from the view)
                face = _model.AddFace(loop, _color, _part);
            });
            if (face >= 0) SelectFaces(new[] { face });
            _drawButton.IsChecked = true;
        }

        private void CancelDrawing()
        {
            _drawing.Clear(); _drawHover = null;
            var v = _drawView; _drawView = null;
            v?.InvalidateVisual();
        }

        // ---- the operations ----

        public void AddBody(string kind)
        {
            if (!Editable()) return;
            List<int> faces = new();
            Change(() => faces = kind switch
            {
                "cylinder" => _model.AddCylinder(new[] { 0.0, 0, 0 }, 0.5, 1, 12, _color, _part),
                "sphere" => _model.AddSphere(new[] { 0.0, 0.5, 0 }, 0.5, 12, 8, _color, _part),
                "plane" => _model.AddPlane(new[] { 0.0, 0, 0 }, 1, 1, _color, _part),
                _ => _model.AddBox(new[] { -0.5, 0, -0.5 }, new[] { 0.5, 1, 0.5 }, _color, _part),
            });
            SelectFaces(faces);
        }

        public void Extrude(double distance)
        {
            if (!Editable()) return;
            var faces = SelectionFaces();
            if (faces.Count == 0) { _info.Text = "Choose faces to extrude."; return; }
            _extrudeDistance = distance;
            List<int> result = new();
            Change(() => result = _model.Extrude(faces, distance));
            SelectFaces(result.Take(faces.Count));
        }

        private async Task AskExtrude()
        {
            string? text = await Input("How far the faces go out, in meters (negative: in):", "Extrude", Fmt(_extrudeDistance));
            if (text != null && double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) Extrude(d);
        }

        /// <summary>Mirrors what is chosen at the plane axis = 0: a copy (the halves share the corners on the plane), or (Copy off) the faces themselves.</summary>
        public void MirrorSelection(int axis, bool? copy = null)
        {
            if (!Editable()) return;
            bool makeCopy = copy ?? _mirrorCopy.IsChecked == true;
            var faces = SelectionFaces();
            if (faces.Count == 0) { _info.Text = "Choose faces (or the corners of faces) to mirror."; return; }
            List<int> result = new();
            Change(() => result = _model.Mirror(faces, axis, 0, makeCopy));
            SelectFaces(result);
        }

        public void FlipSelection()
        {
            if (!Editable()) return;
            var faces = SelectionFaces();
            Change(() => _model.Flip(faces));
        }

        public void MergeSelection()
        {
            if (!Editable()) return;
            var verts = SelectionVertices();
            if (verts.Count < 2) { _info.Text = "Choose two or more corners to merge."; return; }
            Change(() => _model.Merge(verts));
            ClearSelection();
        }

        public void WeldSelection()
        {
            if (!Editable()) return;
            var verts = SelectionVertices();
            int joined = 0;
            Change(() => joined = _model.Weld(verts.Count > 0 ? verts : null, 0.001));
            ClearSelection();
            _info.Text = $"Welded: {joined} corners";
        }

        public void SplitSelection()
        {
            if (!Editable()) return;
            var edges = _selEdges.ToList();
            if (edges.Count == 0) { _info.Text = "Choose edges to split (Edge)."; return; }
            var made = new List<int>();
            Change(() => { foreach (var (a, b) in edges) made.Add(_model.SplitEdge(a, b)); });
            SelectVertices(made);
        }

        /// <summary>A face through the corners chosen (in the order chosen, turned to face out); two corners of one face: the face is cut along them.</summary>
        public void MakeFace()
        {
            if (!Editable()) return;
            var order = _vertOrder.Where(_selVerts.Contains).ToList();
            if (order.Count == 2)
            {
                int face = _model.Faces.FindIndex(f => f.V.Contains(order[0]) && f.V.Contains(order[1]));
                if (face < 0) { _info.Text = "The two corners are not corners of one face."; return; }
                int made = -1;
                Change(() => made = _model.SplitFace(face, order[0], order[1]));
                if (made < 0) _info.Text = "Neighbouring corners: the edge is there already.";
                return;
            }
            if (order.Count < 3) { _info.Text = "Choose three or more corners (Vertex) for a face, or two corners of a face to cut it."; return; }
            // corners chosen by a box come in any order: around their middle in their plane
            var center = _model.Center(order);
            var n = _model.Normal(order);
            if (MeshModel.Length(n) < 0.5) n = _active.Back(0, 0, 1);
            var u = MeshModel.Normalize(MeshModel.Sub(_model.Vertices[order[0]], center));
            if (MeshModel.Length(u) < 0.5) u = MeshModel.Normalize(MeshModel.Cross(n, new[] { 0.0, 1, 0 }));
            var w = MeshModel.Cross(n, u);
            var sorted = order.OrderBy(v => { var d = MeshModel.Sub(_model.Vertices[v], center); return Math.Atan2(MeshModel.Dot(d, w), MeshModel.Dot(d, u)); }).ToList();
            int f2 = -1;
            Change(() => f2 = _model.FaceFromVertices(sorted, _color, _part));
            SelectFaces(new[] { f2 });
        }

        public void Delete()
        {
            if (!Editable()) return;
            if (_mode == SelectMode.Vertex && _selVerts.Count > 0) { var v = _selVerts.ToList(); Change(() => _model.DeleteVertices(v)); }
            else if (_mode == SelectMode.Edge && _selEdges.Count > 0) { var e = _selEdges.ToList(); Change(() => _model.DeleteEdges(e)); }
            else if (_selFaces.Count > 0) { var f = _selFaces.ToList(); Change(() => _model.DeleteFaces(f)); }
            ClearSelection();
        }

        /// <summary>Moves what is chosen.</summary>
        public void MoveSelection(double[] by)
        {
            if (!Editable()) return;
            var verts = SelectionVertices();
            if (verts.Count == 0) return;
            Change(() => _model.Move(verts, by));
        }

        private async Task AskTransform()
        {
            if (!Editable() || SelectionVertices().Count == 0) { _info.Text = "Choose something to move."; return; }
            string? text = await Input("move x, y, z  -  turn x|y|z degrees  -  scale f  or  scale fx, fy, fz (around the middle of what is chosen):", "Move", "move 0, 0.1, 0");
            if (text == null) return;
            var words = text.Trim().Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 2) return;
            var nums = words.Skip(1).Select(s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double x) ? (double?)x : null).ToList();
            var verts = SelectionVertices();
            var center = _model.Center(verts);
            switch (words[0].ToLowerInvariant())
            {
                case "move" when nums.Count == 3 && nums.All(x => x != null):
                    Change(() => _model.Move(verts, nums.Select(x => x!.Value).ToArray()));
                    break;
                case "turn" or "rotate" when words.Length == 3 && "xyz".Contains(words[1].ToLowerInvariant()) && nums[1] != null:
                    Change(() => _model.Rotate(verts, center, "xyz".IndexOf(words[1].ToLowerInvariant(), StringComparison.Ordinal), nums[1]!.Value));
                    break;
                case "scale" when nums.All(x => x != null) && (nums.Count == 1 || nums.Count == 3):
                    var f = nums.Count == 1 ? new[] { nums[0]!.Value, nums[0]!.Value, nums[0]!.Value } : nums.Select(x => x!.Value).ToArray();
                    Change(() => _model.Scale(verts, center, f));
                    break;
                default: _info.Text = "Not understood: " + text; break;
            }
        }

        private void PaintSelection()
        {
            if (_pose != null || _selFaces.Count == 0 || _mode != SelectMode.Face) return;
            var faces = _selFaces.ToList();
            int c = _color;
            Change(() => { foreach (int f in faces) _model.Faces[f].Color = c; });
        }

        private void AssignPart()
        {
            if (!Editable()) return;
            var faces = SelectionFaces();
            int part = _part;
            Change(() => { foreach (int f in faces) _model.Faces[f].Part = part; });
        }

        // ---- keys ----

        private void OnEditorKey(KeyEventArgs e)
        {
            if (e.Source is Visual v && (v is ComboBox or TextBox || v.FindAncestorOfType<ComboBox>() != null || v.FindAncestorOfType<NumericUpDown>() != null)) return;   // (their keys stay theirs)
            bool ctrl = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0, shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
            if (ctrl && e.Key == Key.Z && !shift) { Undo(); e.Handled = true; return; }
            if (ctrl && (e.Key == Key.Y || (e.Key == Key.Z && shift))) { Redo(); e.Handled = true; return; }
            if (ctrl && e.Key == Key.C) { Copy(); e.Handled = true; return; }
            if (ctrl && e.Key == Key.X) { Cut(); e.Handled = true; return; }
            if (ctrl && e.Key == Key.V) { Paste(); e.Handled = true; return; }
            if (ctrl && e.Key == Key.D) { Copy(); Paste(); e.Handled = true; return; }
            if (ctrl && e.Key == Key.A) { SelectAll(); e.Handled = true; return; }
            if (ctrl) return;
            // the arrows move by the grid in the plane of the view chosen (Page Up / Down: along its depth)
            double s = Snap;
            double[]? by = e.Key switch
            {
                Key.Left => _active.Back(-s, 0, 0), Key.Right => _active.Back(s, 0, 0), Key.Up => _active.Back(0, s, 0), Key.Down => _active.Back(0, -s, 0),
                Key.PageUp => _active.Back(0, 0, s), Key.PageDown => _active.Back(0, 0, -s), _ => null,
            };
            if (by != null)
            {
                if (_active.Is2D) for (int a = 0; a < 3; a++) by[a] = Math.Round(by[a] / s) * s;
                if (SelectionVertices().Count > 0) MoveSelection(by);
                else if (!_active.Is2D && e.Key is Key.Left or Key.Right) { _active.Yaw += e.Key == Key.Left ? -15 : 15; _active.InvalidateVisual(); }
                e.Handled = true;
                return;
            }
            switch (e.Key)
            {
                case Key.D1: SetMode(SelectMode.Vertex); break;
                case Key.D2: SetMode(SelectMode.Edge); break;
                case Key.D3: SetMode(SelectMode.Face); break;
                case Key.D: _drawButton.IsChecked = _drawButton.IsChecked != true; if (_drawButton.IsChecked != true) CancelDrawing(); break;
                case Key.E: Extrude(shift ? -_extrudeDistance : _extrudeDistance); break;
                case Key.F: MakeFace(); break;
                case Key.M: MergeSelection(); break;
                case Key.Home: FitViews(); break;
                case Key.Delete or Key.Back: Delete(); break;
                case Key.Enter when _drawing.Count > 0: FinishDrawing(); break;
                case Key.Escape: if (_drawing.Count > 0) CancelDrawing(); else ClearSelection(); break;
                default: return;
            }
            e.Handled = true;
        }

        // ---- palette, parts ----

        private async Task<string?> Input(string prompt, string title, string initial) => await Dialogs.Input(Dialogs.WindowOf(this), prompt, title, initial);

        private static string Fmt(double v) => Math.Round(v, 4).ToString("0.####", CultureInfo.InvariantCulture);

        private async Task AddColor()
        {
            string? text = await Input("The new colour (#RRGGBB):", "Colour", BlockModel.ColorText(_model.Palette[_color]));
            if (PixelEditorControl.ParseColor(text) is not { } packed) return;
            Change(() => _model.Palette.Add(((packed & 255) << 16) | (packed & 0xFF00) | ((packed >> 16) & 255)));
            CurrentColor = _model.Palette.Count - 1;
        }

        private async Task EditColor(int index)
        {
            if (index < 0 || index >= _model.Palette.Count) return;
            string? text = await Input($"Colour {index} (#RRGGBB):", "Colour", BlockModel.ColorText(_model.Palette[index]));
            if (PixelEditorControl.ParseColor(text) is not { } packed) return;
            Change(() => _model.Palette[index] = ((packed & 255) << 16) | (packed & 0xFF00) | ((packed >> 16) & 255));
        }

        private void RemoveColor()
        {
            if (_model.Palette.Count <= 1) return;
            Change(() => _model.RemoveColor(_color));
            CurrentColor = Math.Min(_color, _model.Palette.Count - 1);
        }

        private async Task AddPart()
        {
            string? name = await Input("The name of the new part (it hangs from the part chosen; the faces chosen join it):", "New part", "part" + _model.Parts.Count);
            if (string.IsNullOrWhiteSpace(name)) return;
            int parent = Math.Max(0, _part);
            var verts = SelectionVertices();
            var pivot = verts.Count > 0 ? _model.Center(verts).Select(x => Math.Round(x, 4)).ToArray() : new double[3];
            var faces = SelectionFaces();
            Change(() =>
            {
                _model.Parts.Add(new BlockModel.Part { Name = name.Trim(), Parent = parent, Pivot = pivot });
                foreach (int f in faces) _model.Faces[f].Part = _model.Parts.Count - 1;
            });
            _part = _model.Parts.Count - 1;
            RefreshParts();
            RefreshViews();
        }

        private async Task EditPivot()
        {
            int part = _part;
            if (part < 0 || part >= _model.Parts.Count) return;
            var pv = _model.Parts[part].Pivot;
            string? text = await Input($"Where '{_model.Parts[part].Name}' turns (meters: x, y, z):", "Pivot", string.Join(", ", pv.Select(Fmt)));
            if (text == null) return;
            var parts = text.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3 || !parts.All(s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _))) return;
            Change(() => _model.Parts[part].Pivot = parts.Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray());
        }

        private async Task Export()
        {
            var top = TopLevel.GetTopLevel(this);
            if (top == null) return;
            string suggested = Path.GetFileNameWithoutExtension(FilePath ?? "model") + ".f3d";
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export the model file (.f3d)", SuggestedFileName = suggested, DefaultExtension = "f3d",
                FileTypeChoices = new[] { new FilePickerFileType("fire model") { Patterns = new[] { "*.f3d" } } },
            });
            if (file?.TryGetLocalPath() is not { } path) return;
            try { File.WriteAllBytes(path, _model.ToF3d()); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { await Dialogs.Message(Dialogs.WindowOf(this), ex.Message, "Export"); }
        }

        // ---- IDocumentView ----

        public string GetText() => _text;

        public void ResetTo(string text, string? filePath)
        {
            FilePath = filePath;
            MeshModel model;
            try { model = MeshModel.Parse(text); }
            catch (InvalidDataException ex) { model = MeshModel.CreateDefault(); _info.Text = ex.Message; }
            _animBar.Reset();
            Attach(model, resetUndo: true);
            _savedText = _text;   // (the text of the model in its own form: what is saved)
            ModifiedChanged?.Invoke();
            FitViews();
        }

        public void MarkSaved() { _savedText = _text; ModifiedChanged?.Invoke(); }
        public int GetCaretLine() => 1;
        public void FocusEditor() => _active.Focus();
        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;
        public bool HasSelection => _selVerts.Count + _selEdges.Count + _selFaces.Count > 0;

        public void Undo()
        {
            if (_undo.Count == 0) return;
            _redo.Add(_model.ToJson());
            string text = _undo[^1];
            _undo.RemoveAt(_undo.Count - 1);
            Attach(MeshModel.Parse(text), resetUndo: false);
            ModifiedChanged?.Invoke();
        }

        public void Redo()
        {
            if (_redo.Count == 0) return;
            _undo.Add(_model.ToJson());
            string text = _redo[^1];
            _redo.RemoveAt(_redo.Count - 1);
            Attach(MeshModel.Parse(text), resetUndo: false);
            ModifiedChanged?.Invoke();
        }

        /// <summary>The faces chosen (with their corners) to the clipboard of the mesh models.</summary>
        public void Copy()
        {
            var faces = SelectionFaces();
            if (faces.Count == 0) return;
            var verts = _model.VerticesOf(faces).ToList();
            var index = verts.Select((v, i) => (v, i)).ToDictionary(p => p.v, p => p.i);
            s_clipboard = (verts.Select(v => (double[])_model.Vertices[v].Clone()).ToList(),
                           faces.Select(f => new MeshModel.Face { V = _model.Faces[f].V.Select(v => index[v]).ToList(), Color = _model.Faces[f].Color, Part = 0 }).ToList());
            _info.Text = $"Copied: {faces.Count} faces";
        }

        public void Cut()
        {
            if (!Editable()) return;
            Copy();
            var faces = SelectionFaces();
            Change(() => _model.DeleteFaces(faces));
            ClearSelection();
        }

        /// <summary>The copied faces where they were (they join the part chosen; move them with the arrows or a drag).</summary>
        public void Paste()
        {
            if (s_clipboard is not { } clip || !Editable()) return;
            var made = new List<int>();
            Change(() =>
            {
                int first = _model.Vertices.Count;
                foreach (var v in clip.Vertices) _model.Vertices.Add((double[])v.Clone());
                foreach (var f in clip.Faces) made.Add(_model.AddFace(f.V.Select(v => first + v), Math.Min(f.Color, _model.Palette.Count - 1), _part));
            });
            SelectFaces(made);
        }

        public void Find() { }
        public void FindNext() { }
        public void FindPrevious() { }
        public int LineCount => 0;
        public void GoToLine(int line) { }
    }
}
