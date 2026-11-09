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
    /// The block editor of spark (`.fblocks`, docs/graphics/models.md - ROADMAP 16, milestone 3.1): a model is built from blocks of a palette, like a world of
    /// blocks. On the left the model in 3D - turn it (middle button or Ctrl + left button, the arrow keys), zoom (wheel); a click puts a block on the face under the
    /// pointer (or on the floor), the right button takes one away, Shift + click takes its colour. On the right one layer of the grid from above - draw with the
    /// left button, erase with the right one; Page Up / Page Down choose the layer. Mirror X puts every block on the other side as well. Blocks belong to a part
    /// (the parts form a tree and turn around their pivot: what animations move). The text of the file is the model (BlockModel.ToJson); forge makes an .f3d of
    /// it when a program uses it as a resource, Export writes one.
    /// </summary>
    public sealed class BlockEditorControl : UserControl, IDocumentView
    {
        // ---- the model in 3D ------------------------------------------------------------------------------------------------------------------
        private sealed class PreviewView : Control
        {
            public BlockModel Model = BlockModel.CreateDefault();
            public double Yaw = 35, Pitch = 30, Zoom = 18;
            /// <summary>The pose of the parts (an animation at its time: a matrix per part, cells to cells), or null: the rest pose.</summary>
            public double[][]? Pose;
            public int CurrentPart;
            public (int X, int Y, int Z, int Face)? Hover;
            public BlockModel.Box? Selection;
            public event Action<PointerPoint, KeyModifiers, Point>? Clicked;
            public event Action<Point>? Hovered;
            private Point? _drag;
            private static readonly double[] Light = Normalize(new[] { 0.45, 1.0, 0.65 });

            public PreviewView() { Focusable = true; ClipToBounds = true; }

            private static double[] Normalize(double[] v) { double l = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]); return new[] { v[0] / l, v[1] / l, v[2] / l }; }

            private double[] Center => new[] { Model.SizeX / 2.0, Model.SizeY / 3.0, Model.SizeZ / 2.0 };

            /// <summary>A point of the grid on the screen, and its depth (larger: nearer).</summary>
            public (double X, double Y, double Depth) Project(double x, double y, double z)
            {
                var c = Center;
                double qx = x - c[0], qy = y - c[1], qz = z - c[2];
                double cy = Math.Cos(Yaw * Math.PI / 180), sy = Math.Sin(Yaw * Math.PI / 180), cp = Math.Cos(Pitch * Math.PI / 180), sp = Math.Sin(Pitch * Math.PI / 180);
                double x1 = cy * qx + sy * qz, z1 = -sy * qx + cy * qz;
                double y2 = cp * qy - sp * z1, z2 = sp * qy + cp * z1;
                return (Bounds.Width / 2 + x1 * Zoom, Bounds.Height / 2 - y2 * Zoom, z2);
            }

            /// <summary>The ray through a point of the screen, in cells (from in front of the model into it).</summary>
            public (double[] Origin, double[] Dir) Ray(Point p)
            {
                double x1 = (p.X - Bounds.Width / 2) / Zoom, y2 = (Bounds.Height / 2 - p.Y) / Zoom, z2 = 1000;
                double cy = Math.Cos(Yaw * Math.PI / 180), sy = Math.Sin(Yaw * Math.PI / 180), cp = Math.Cos(Pitch * Math.PI / 180), sp = Math.Sin(Pitch * Math.PI / 180);
                double[] Back(double a, double b, double cz)
                {
                    double qy = cp * b + sp * cz, z1 = -sp * b + cp * cz;
                    return new[] { cy * a - sy * z1, qy, sy * a + cy * z1 };
                }
                var o = Back(x1, y2, z2); var d = Back(0, 0, -1); var c = Center;
                return (new[] { o[0] + c[0], o[1] + c[1], o[2] + c[2] }, d);
            }

            protected override void OnPointerPressed(PointerPressedEventArgs e)
            {
                base.OnPointerPressed(e);
                Focus();
                var point = e.GetCurrentPoint(this);
                if (point.Properties.IsMiddleButtonPressed || (point.Properties.IsLeftButtonPressed && (e.KeyModifiers & KeyModifiers.Control) != 0))
                {
                    _drag = point.Position;
                    e.Pointer.Capture(this);
                    return;
                }
                Clicked?.Invoke(point, e.KeyModifiers, point.Position);
            }

            protected override void OnPointerMoved(PointerEventArgs e)
            {
                base.OnPointerMoved(e);
                var p = e.GetPosition(this);
                if (_drag is { } d)
                {
                    Yaw += (p.X - d.X) * 0.5;
                    Pitch = Math.Clamp(Pitch + (p.Y - d.Y) * 0.4, -89, 89);
                    _drag = p;
                    InvalidateVisual();
                    return;
                }
                Hovered?.Invoke(p);
            }

            protected override void OnPointerReleased(PointerReleasedEventArgs e)
            {
                base.OnPointerReleased(e);
                if (_drag != null) { _drag = null; e.Pointer.Capture(null); }
            }

            protected override void OnPointerExited(PointerEventArgs e) { base.OnPointerExited(e); Hover = null; InvalidateVisual(); }

            protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
            {
                Zoom = Math.Clamp(Zoom * (e.Delta.Y > 0 ? 1.15 : 1 / 1.15), 2, 200);
                InvalidateVisual();
                e.Handled = true;
            }

            private static readonly int[][] FaceCorners =
            {
                // the corners of the faces of a cell (offsets 0/1), counter clockwise seen from outside: +x, -x, +y, -y, +z, -z
                new[] { 1,0,0, 1,1,0, 1,1,1, 1,0,1 }, new[] { 0,0,0, 0,0,1, 0,1,1, 0,1,0 },
                new[] { 0,1,0, 0,1,1, 1,1,1, 1,1,0 }, new[] { 0,0,0, 1,0,0, 1,0,1, 0,0,1 },
                new[] { 0,0,1, 1,0,1, 1,1,1, 0,1,1 }, new[] { 0,0,0, 0,1,0, 1,1,0, 1,0,0 },
            };

            private Point[] Corners(int x, int y, int z, int face, double[]? m = null)
            {
                var c = FaceCorners[face];
                var pts = new Point[4];
                for (int k = 0; k < 4; k++)
                {
                    var q = Posed(m, x + c[k * 3], y + c[k * 3 + 1], z + c[k * 3 + 2]);
                    var p = Project(q[0], q[1], q[2]);
                    pts[k] = new Point(p.X, p.Y);
                }
                return pts;
            }

            private static double[] Posed(double[]? m, double x, double y, double z) =>
                m == null ? new[] { x, y, z } : new[] { m[0] * x + m[1] * y + m[2] * z + m[3], m[4] * x + m[5] * y + m[6] * z + m[7], m[8] * x + m[9] * y + m[10] * z + m[11] };

            /// <summary>A direction turned by the pose (normalised; the normal of a face: the inverse transpose would be exact, a turn and a uniform scale need no more).</summary>
            private static double[] PosedDir(double[]? m, double x, double y, double z)
            {
                if (m == null) return new[] { x, y, z };
                double a = m[0] * x + m[1] * y + m[2] * z, b = m[4] * x + m[5] * y + m[6] * z, c = m[8] * x + m[9] * y + m[10] * z, l = Math.Sqrt(a * a + b * b + c * c);
                return l > 1e-12 ? new[] { a / l, b / l, c / l } : new[] { x, y, z };
            }

            private static StreamGeometry Polygon(Point[] pts)
            {
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    ctx.BeginFigure(pts[0], true);
                    for (int i = 1; i < pts.Length; i++) ctx.LineTo(pts[i]);
                    ctx.EndFigure(true);
                }
                return g;
            }

            public override void Render(DrawingContext context)
            {
                context.FillRectangle(EditorTheme.Solid(Color.FromRgb(0x1B, 0x18, 0x1E)), new Rect(Bounds.Size));
                // the floor: the grid at y = 0
                var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), 1);
                for (int x = 0; x <= Model.SizeX; x++) { var a = Project(x, 0, 0); var b = Project(x, 0, Model.SizeZ); context.DrawLine(gridPen, new Point(a.X, a.Y), new Point(b.X, b.Y)); }
                for (int z = 0; z <= Model.SizeZ; z++) { var a = Project(0, 0, z); var b = Project(Model.SizeX, 0, z); context.DrawLine(gridPen, new Point(a.X, a.Y), new Point(b.X, b.Y)); }
                // the faces from far to near (the back faces are not seen)
                double cy = Math.Cos(Yaw * Math.PI / 180), sy = Math.Sin(Yaw * Math.PI / 180), cp = Math.Cos(Pitch * Math.PI / 180), sp = Math.Sin(Pitch * Math.PI / 180);
                double ViewZ(double dx, double dy, double dz) { double z1 = -sy * dx + cy * dz; return sp * dy + cp * z1; }
                var faces = new List<(double Depth, int X, int Y, int Z, int Face, int Color, int Part, double[] N)>();
                foreach (var f in Model.VisibleFaces())
                {
                    var d = BlockModel.Directions[f.Dir];
                    var m = Pose != null && f.Part < Pose.Length ? Pose[f.Part] : null;
                    var n = PosedDir(m, d.X, d.Y, d.Z);
                    if (ViewZ(n[0], n[1], n[2]) <= 1e-9) continue;
                    var c = Posed(m, f.X + 0.5 + d.X * 0.5, f.Y + 0.5 + d.Y * 0.5, f.Z + 0.5 + d.Z * 0.5);
                    var center = Project(c[0], c[1], c[2]);
                    faces.Add((center.Depth, f.X, f.Y, f.Z, f.Dir, f.Color, f.Part, n));
                }
                faces.Sort((a, b) => a.Depth.CompareTo(b.Depth));
                var edge = new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)), 1);
                foreach (var f in faces)
                {
                    var d = f.N;
                    double shade = 0.5 + 0.5 * Math.Max(0, d[0] * Light[0] + d[1] * Light[1] + d[2] * Light[2]);
                    uint rgb = Model.Palette[Math.Clamp(f.Color, 0, Model.Palette.Count - 1)];
                    var color = Color.FromRgb((byte)(((rgb >> 16) & 255) * shade), (byte)(((rgb >> 8) & 255) * shade), (byte)((rgb & 255) * shade));
                    if (f.Part != CurrentPart) color = Color.FromRgb((byte)(color.R * 0.7 + 30), (byte)(color.G * 0.7 + 30), (byte)(color.B * 0.7 + 30));
                    context.DrawGeometry(new SolidColorBrush(color), edge, Polygon(Corners(f.X, f.Y, f.Z, f.Face, Pose != null && f.Part < Pose.Length ? Pose[f.Part] : null)));
                }
                // the pivot of the part that is drawn, and the face under the pointer
                if (CurrentPart >= 0 && CurrentPart < Model.Parts.Count)
                {
                    var pv = Model.Parts[CurrentPart].Pivot;
                    var q = Posed(Pose != null && CurrentPart < Pose.Length ? Pose[CurrentPart] : null, pv[0], pv[1], pv[2]);
                    var p = Project(q[0], q[1], q[2]);
                    context.DrawEllipse(null, new Pen(Brushes.Orange, 2), new Point(p.X, p.Y), 4, 4);
                }
                if (Selection is { } sel && Pose == null)
                {
                    // the box of the selection as wires
                    var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x60, 0xC0, 0xFF)), 1.5, new DashStyle(new double[] { 4, 3 }, 0));
                    double[] xs = { sel.X0, sel.X1 + 1 }, ys = { sel.Y0, sel.Y1 + 1 }, zs = { sel.Z0, sel.Z1 + 1 };
                    Point P(int i, int j, int k) { var q = Project(xs[i], ys[j], zs[k]); return new Point(q.X, q.Y); }
                    for (int a = 0; a < 2; a++) for (int b = 0; b < 2; b++)
                    {
                        context.DrawLine(pen, P(0, a, b), P(1, a, b));
                        context.DrawLine(pen, P(a, 0, b), P(a, 1, b));
                        context.DrawLine(pen, P(a, b, 0), P(a, b, 1));
                    }
                }
                if (Hover is { } h && Pose == null)
                {
                    var pts = h.Y < 0 ? Corners(h.X, 0, h.Z, 3) : Corners(h.X, h.Y, h.Z, h.Face);
                    context.DrawGeometry(null, new Pen(Brushes.White, 2), Polygon(pts));
                }
            }
        }

        // ---- one layer of the grid -------------------------------------------------------------------------------------------------------------
        private sealed class LayerView : Control
        {
            public BlockModel Model = BlockModel.CreateDefault();
            public int Layer, CurrentPart;
            public double Cell = 16;
            public (int X, int Z)? Hover;
            public BlockModel.Box? Selection;
            public event Action<int, int, PointerPoint, KeyModifiers, bool>? Painted;   // x, z, point, modifiers, first (pressed) or moved
            public event Action? StrokeEnded;
            private bool _down;
            private (int X, int Z) _last;

            public LayerView() { Focusable = true; }

            protected override Size MeasureOverride(Size availableSize) => new(Model.SizeX * Cell + 1, Model.SizeZ * Cell + 1);

            private (int, int) CellAt(Point p) => ((int)Math.Floor(p.X / Cell), (int)Math.Floor(p.Y / Cell));

            protected override void OnPointerPressed(PointerPressedEventArgs e)
            {
                base.OnPointerPressed(e);
                Focus();
                var point = e.GetCurrentPoint(this);
                var (x, z) = CellAt(point.Position);
                _down = true;
                _last = (x, z);
                e.Pointer.Capture(this);
                Painted?.Invoke(x, z, point, e.KeyModifiers, true);
            }

            protected override void OnPointerMoved(PointerEventArgs e)
            {
                base.OnPointerMoved(e);
                var point = e.GetCurrentPoint(this);
                var (x, z) = CellAt(point.Position);
                var hover = x >= 0 && z >= 0 && x < Model.SizeX && z < Model.SizeZ ? (x, z) : ((int, int)?)null;
                if (hover != Hover) { Hover = hover; InvalidateVisual(); }
                if (_down && (x, z) != _last)
                {
                    // every cell on the way from the last one (the pointer jumps when it is fast)
                    int x0 = _last.X, z0 = _last.Z, dx = Math.Abs(x - x0), dz = -Math.Abs(z - z0), sx = x0 < x ? 1 : -1, sz = z0 < z ? 1 : -1, err = dx + dz;
                    while (true)
                    {
                        if (x0 == x && z0 == z) break;
                        int e2 = 2 * err;
                        if (e2 >= dz) { err += dz; x0 += sx; }
                        if (e2 <= dx) { err += dx; z0 += sz; }
                        Painted?.Invoke(x0, z0, point, e.KeyModifiers, false);
                    }
                    _last = (x, z);
                }
            }

            protected override void OnPointerReleased(PointerReleasedEventArgs e)
            {
                base.OnPointerReleased(e);
                _down = false;
                e.Pointer.Capture(null);
                StrokeEnded?.Invoke();
            }

            public override void Render(DrawingContext context)
            {
                double w = Model.SizeX * Cell, h = Model.SizeZ * Cell;
                context.FillRectangle(EditorTheme.Solid(Color.FromRgb(0x24, 0x21, 0x28)), new Rect(0, 0, w, h));
                for (int z = 0; z < Model.SizeZ; z++)
                    for (int x = 0; x < Model.SizeX; x++)
                    {
                        var r = new Rect(x * Cell, z * Cell, Cell, Cell);
                        if (Model.TryGet(x, Layer, z, out int color, out int part))
                        {
                            uint rgb = Model.Palette[Math.Clamp(color, 0, Model.Palette.Count - 1)];
                            var c = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
                            context.FillRectangle(new SolidColorBrush(c), r);
                            if (part != CurrentPart) context.FillRectangle(new SolidColorBrush(Color.FromArgb(0x60, 0x20, 0x20, 0x20)), r);
                        }
                        else if (Layer > 0 && Model.TryGet(x, Layer - 1, z, out int below, out _))
                        {
                            // the layer below shines through
                            uint rgb = Model.Palette[Math.Clamp(below, 0, Model.Palette.Count - 1)];
                            context.FillRectangle(new SolidColorBrush(Color.FromArgb(0x50, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)), r.Deflate(Cell * 0.25));
                        }
                    }
                var pen = new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), 1);
                for (int x = 0; x <= Model.SizeX; x++) context.DrawLine(pen, new Point(x * Cell + 0.5, 0), new Point(x * Cell + 0.5, h));
                for (int z = 0; z <= Model.SizeZ; z++) context.DrawLine(pen, new Point(0, z * Cell + 0.5), new Point(w, z * Cell + 0.5));
                var mid = new Pen(new SolidColorBrush(Color.FromArgb(0x80, 0x80, 0xC0, 0xFF)), 1);
                context.DrawLine(mid, new Point(w / 2, 0), new Point(w / 2, h));
                if (Selection is { } sel)
                {
                    bool here = Layer >= sel.Y0 && Layer <= sel.Y1;
                    var selPen = new Pen(new SolidColorBrush(Color.FromArgb((byte)(here ? 0xFF : 0x70), 0x60, 0xC0, 0xFF)), 2, new DashStyle(new double[] { 3, 2 }, 0));
                    context.DrawRectangle(here ? new SolidColorBrush(Color.FromArgb(0x30, 0x60, 0xC0, 0xFF)) : null, selPen, new Rect(sel.X0 * Cell + 1, sel.Z0 * Cell + 1, sel.Width * Cell - 2, sel.Depth * Cell - 2));
                }
                if (Hover is { } hv) context.DrawRectangle(null, new Pen(Brushes.White, 2), new Rect(hv.X * Cell + 1, hv.Z * Cell + 1, Cell - 2, Cell - 2));
            }
        }

        // ---- the control -------------------------------------------------------------------------------------------------------------------
        private BlockModel _model = BlockModel.CreateDefault();
        private readonly PreviewView _preview = new();
        private readonly LayerView _layerView = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        private readonly ModelPaletteStrip _palette = new();
        private readonly TextBlock _layerText = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0), MinWidth = 64 };
        private readonly TextBlock _info = new() { Foreground = EditorTheme.TextDim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
        private readonly ToggleButton _mirror = new() { Content = "Mirror X" };
        private readonly ComboBox _parts = new() { MinWidth = 110 };
        private readonly List<string> _undo = new(), _redo = new();
        private string _savedText = "";
        private string _text = "";
        private int _color = 4, _layer;
        private bool _updatingParts;
        // the animation that is shown (the rest pose, where the blocks are built, or an animation at a time)
        private readonly ModelAnimationBar _animBar = new("cells", 0.5m);
        private enum LayerTool { Draw, Fill, Select }
        private LayerTool _tool = LayerTool.Draw;
        private readonly ToggleButton[] _toolButtons = new ToggleButton[3];
        private readonly ToggleButton _allLayers = new() { Content = "All layers" };
        private BlockModel.Box? _selection;
        private (int X, int Z) _selectStart;
        private static BlockModel.Clip? s_clipboard;   // (shared by the block models of spark)
        private bool _stroke;              // a stroke of the layer view: its changes are one step of undo
        private bool _strokeRecorded;

        public string? FilePath { get; set; }
        public bool IsReadOnly => false;
        public bool IsModified => _text != _savedText;
        public event Action? ModifiedChanged;
        public event Action<int>? CaretLineChanged { add { } remove { } }

        /// <summary>The model as it is now (for the tests and the host).</summary>
        public BlockModel Model => _model;

        public BlockEditorControl()
        {
            _mirror.Classes.Add("tool");
            ToolTip.SetTip(_mirror, "Every block is put (and taken away) on the other side of the middle as well");
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(4, 2) };
            bar.Children.Add(Button_("▼", "The layer below (Page Down)", () => SetLayer(_layer - 1)));
            bar.Children.Add(_layerText);
            bar.Children.Add(Button_("▲", "The layer above (Page Up)", () => SetLayer(_layer + 1)));
            bar.Children.Add(Separator());
            bar.Children.Add(_mirror);
            string[] toolNames = { "Draw", "Fill", "Select" };
            string[] toolTips = { "The layer view draws (left) and erases (right)", "A click in the layer view fills the area of the same blocks (or of empty cells) with the colour",
                                  "A drag in the layer view selects a box (All layers: through every layer); Ctrl+C / Ctrl+X / Ctrl+V (at the pointer) / Delete, Esc lets go" };
            for (int t = 0; t < 3; t++)
            {
                var tool = (LayerTool)t;
                var b = new ToggleButton { Content = toolNames[t], IsChecked = t == 0 };
                b.Classes.Add("tool");
                ToolTip.SetTip(b, toolTips[t]);
                b.Click += (_, _) => SetTool(tool);
                _toolButtons[t] = b;
                bar.Children.Add(b);
            }
            _allLayers.Classes.Add("tool");
            ToolTip.SetTip(_allLayers, "The selection reaches through every layer (else: the layer it was made in)");
            _allLayers.Click += (_, _) => { if (_selection is { } sel) SetSelection(BlockModel.Box.Of(sel.X0, _allLayers.IsChecked == true ? 0 : _layer, sel.Z0, sel.X1, _allLayers.IsChecked == true ? _model.SizeY - 1 : _layer, sel.Z1)); };
            bar.Children.Add(_allLayers);
            bar.Children.Add(Button_("Fill box", "Fills the selection with the colour", FillSelection));
            bar.Children.Add(Button_("⟲", "Turn the view", () => Turn(-45)));
            bar.Children.Add(Button_("⟳", "Turn the view", () => Turn(45)));
            bar.Children.Add(Separator());
            bar.Children.Add(new TextBlock { Text = "Part", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0) });
            bar.Children.Add(_parts);
            bar.Children.Add(Button_("+", "A new part (under the part chosen; its pivot: the middle of the layer)", async () => await AddPart()));
            bar.Children.Add(Button_("Pivot...", "Where the part turns (in cells, x, y, z)", async () => await EditPivot()));
            bar.Children.Add(Separator());
            bar.Children.Add(Button_("Size...", "The size of the grid and of a block", async () => await AskSize()));
            bar.Children.Add(Button_("Preview", "scene3d draws the model as a program shows it (with the animation chosen)", () =>
            {
                var window = new BlockPreviewWindow(BlockModel.Parse(_model.ToJson()), _animBar.Index);
                if (TopLevel.GetTopLevel(this) is Window owner) window.Show(owner); else window.Show();
            }));
            bar.Children.Add(Button_("Export .f3d...", "Writes the model file that scene3d reads (a program can also take the .fblocks as a resource: forge converts it)", async () => await Export()));
            bar.Children.Add(_info);

            var paletteBar = new DockPanel { Margin = new Thickness(6, 4) };
            var paletteButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Top };
            paletteButtons.Children.Add(Button_("+ Colour", "A new colour in the palette", async () => await AddColor()));
            paletteButtons.Children.Add(Button_("Edit...", "Change the chosen colour (double-click a colour does the same)", async () => await EditColor(_color)));
            paletteButtons.Children.Add(Button_("− Colour", "Take the chosen colour out (its blocks take the first colour)", RemoveColor));
            DockPanel.SetDock(paletteButtons, Avalonia.Controls.Dock.Right);
            paletteBar.Children.Add(paletteButtons);
            paletteBar.Children.Add(_palette);

            // the second bar: the animations
            _animBar.Animations = () => _model.Animations;
            _animBar.CurrentPart = () => _preview.CurrentPart;
            _animBar.Change = Change;
            _animBar.PoseChanged += RefreshViews;
            var animBar = _animBar;

            var layerScroll = new ScrollViewer
            {
                Content = new Border { Padding = new Thickness(10), Child = _layerView },
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            var help = new TextBlock
            {
                Text = "3D: click puts a block on a face, right button takes it away, Shift + click takes the colour; middle button or Ctrl + drag turns, wheel zooms.  " +
                       "Layer: left draws, right erases, Shift takes the colour; Page Up / Down change the layer.",
                Foreground = EditorTheme.TextDim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6, 2),
            };
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("3*,4,2*") };
            Grid.SetColumn(_preview, 0);
            var splitter = new GridSplitter { Background = EditorTheme.Border };
            Grid.SetColumn(splitter, 1);
            var right = new Border { Child = layerScroll, BorderBrush = EditorTheme.Border, BorderThickness = new Thickness(1, 0, 0, 0) };
            Grid.SetColumn(right, 2);
            grid.Children.Add(_preview); grid.Children.Add(splitter); grid.Children.Add(right);

            var dock = new DockPanel();
            DockPanel.SetDock(bar, Avalonia.Controls.Dock.Top);
            DockPanel.SetDock(animBar, Avalonia.Controls.Dock.Top);
            DockPanel.SetDock(help, Avalonia.Controls.Dock.Bottom);
            DockPanel.SetDock(paletteBar, Avalonia.Controls.Dock.Bottom);
            dock.Children.Add(bar); dock.Children.Add(animBar); dock.Children.Add(help); dock.Children.Add(paletteBar); dock.Children.Add(grid);
            Content = dock;
            Focusable = true;

            _preview.Clicked += OnPreviewClick;
            _preview.Hovered += OnPreviewHover;
            _layerView.Painted += (x, z, point, mods, first) => { if (first) { _stroke = true; _strokeRecorded = false; } OnLayerPaint(x, z, point, mods, first); };
            _layerView.StrokeEnded += () => { _stroke = false; _strokeRecorded = false; };
            // (before the scroll viewer of the layer takes Page Up / Page Down for itself)
            AddHandler(KeyDownEvent, (_, e) => OnEditorKey(e), Avalonia.Interactivity.RoutingStrategies.Tunnel);
            _palette.Picked += i => { _color = i; RefreshViews(); };
            _palette.EditRequested += async i => { _color = i; await EditColor(i); };
            _parts.SelectionChanged += (_, _) =>
            {
                if (_updatingParts || _parts.SelectedIndex < 0) return;
                _preview.CurrentPart = _layerView.CurrentPart = _parts.SelectedIndex;
                RefreshViews();
            };
            Attach(BlockModel.CreateDefault(), resetUndo: true);
        }

        private static Control Separator() => ModelEditorUi.Separator();

        private static Button Button_(string text, string tip, Action click) => ModelEditorUi.Button(text, tip, click);

        private void Attach(BlockModel model, bool resetUndo)
        {
            _model = model;
            _preview.Model = _layerView.Model = model;
            _palette.Palette = model.Palette;
            _layer = Math.Clamp(_layer, 0, model.SizeY - 1);
            _color = Math.Clamp(_color, 0, model.Palette.Count - 1);
            int part = Math.Clamp(_parts.SelectedIndex, 0, model.Parts.Count - 1);
            _preview.CurrentPart = _layerView.CurrentPart = part;
            if (resetUndo) { _undo.Clear(); _redo.Clear(); }
            _text = model.ToJson();
            _preview.Zoom = Math.Clamp(260.0 / Math.Max(model.SizeX, Math.Max(model.SizeY, model.SizeZ)), 4, 60);
            _layerView.Cell = Math.Clamp(320.0 / Math.Max(model.SizeX, model.SizeZ), 6, 28);
            RefreshParts();
            _animBar.RefreshList();
            RefreshViews();
        }

        private void RefreshParts()
        {
            _updatingParts = true;
            int selected = Math.Clamp(_preview.CurrentPart, 0, _model.Parts.Count - 1);
            _parts.ItemsSource = _model.Parts.Select((p, i) => (i == 0 ? "" : "  ") + p.Name).ToList();
            _parts.SelectedIndex = selected;
            _updatingParts = false;
        }

        private void RefreshViews()
        {
            _layerView.Layer = _layer;
            _palette.Current = _color;
            _layerText.Text = $"Layer {_layer} / {_model.SizeY - 1}";
            int triangles = _model.Quads().Count * 2;
            _info.Text = $"{_model.SizeX} x {_model.SizeY} x {_model.SizeZ}, {_model.BlockSize.ToString("0.###", CultureInfo.InvariantCulture)} m a block - {_model.Count} blocks, {triangles} triangles";
            _layerView.InvalidateMeasure(); _layerView.InvalidateVisual();
            _palette.InvalidateMeasure(); _palette.InvalidateVisual();
            // the animation: its pose in the 3D view, the timeline, the numbers of the part at this time
            var anim = _animBar.Current;
            _preview.Pose = anim != null ? _model.PoseMatrices(anim, _animBar.Time) : null;
            _animBar.Refresh();
            _preview.InvalidateVisual();
        }

        // ---- animations ----

        private BlockModel.Animation? CurrentAnimation => _animBar.Current;

        /// <summary>Shows an animation (-1: the rest pose, where blocks are built).</summary>
        public void ChooseAnimation(int index) => _animBar.Choose(index);
        public void SetTime(double time) => _animBar.SetTime(time);
        public double Time => _animBar.Time;
        public int AnimationIndex => _animBar.Index;

        /// <summary>A change of the model: undo keeps the text before it.</summary>
        private void Change(Action change)
        {
            string before = _model.ToJson();
            change();
            string after = _model.ToJson();
            if (after == before) return;
            if (!_stroke || !_strokeRecorded)
            {
                _undo.Add(before);
                if (_undo.Count > 200) _undo.RemoveAt(0);
                _strokeRecorded = _stroke;
            }
            _redo.Clear();
            _text = after;
            RefreshViews();
            ModifiedChanged?.Invoke();
        }

        // ---- editing ----

        /// <summary>Puts a block of the chosen colour and part (and its mirror image).</summary>
        public void PutBlock(int x, int y, int z) => Change(() =>
        {
            _model.Set(x, y, z, _color, _preview.CurrentPart);
            if (_mirror.IsChecked == true) _model.Set(_model.SizeX - 1 - x, y, z, _color, _preview.CurrentPart);
        });

        public void TakeBlock(int x, int y, int z) => Change(() =>
        {
            _model.Remove(x, y, z);
            if (_mirror.IsChecked == true) _model.Remove(_model.SizeX - 1 - x, y, z);
        });

        public bool Mirror { get => _mirror.IsChecked == true; set => _mirror.IsChecked = value; }
        public int CurrentColor { get => _color; set { _color = Math.Clamp(value, 0, _model.Palette.Count - 1); RefreshViews(); } }
        public int CurrentLayer => _layer;

        public void SetLayer(int layer) { _layer = Math.Clamp(layer, 0, _model.SizeY - 1); RefreshViews(); }

        private void Turn(double degrees) { _preview.Yaw += degrees; _preview.InvalidateVisual(); }

        private void OnPreviewHover(Point p)
        {
            if (_animBar.Index >= 0) { if (_preview.Hover != null) { _preview.Hover = null; _preview.InvalidateVisual(); } return; }
            var (o, d) = _preview.Ray(p);
            (int X, int Y, int Z, int Face)? hover = _model.Raycast(o, d, out var cell, out int face) ? (cell.X, cell.Y, cell.Z, face) : null;
            if (hover != _preview.Hover) { _preview.Hover = hover; _preview.InvalidateVisual(); }
        }

        private void OnPreviewClick(PointerPoint point, KeyModifiers mods, Point p)
        {
            if (_animBar.Index >= 0) { _info.Text = "Blocks are built in the rest pose (Animation: rest pose)."; return; }
            var (o, d) = _preview.Ray(p);
            if (!_model.Raycast(o, d, out var cell, out int face)) return;
            bool onFloor = cell.Y < 0;
            if ((mods & KeyModifiers.Shift) != 0)
            {
                if (!onFloor && _model.TryGet(cell.X, cell.Y, cell.Z, out int c, out _)) CurrentColor = c;
                return;
            }
            if (point.Properties.IsRightButtonPressed) { if (!onFloor) TakeBlock(cell.X, cell.Y, cell.Z); }
            else
            {
                var n = BlockModel.Directions[face];
                int x = cell.X + n.X, y = cell.Y + n.Y, z = cell.Z + n.Z;
                if (_model.Inside(x, y, z)) { PutBlock(x, y, z); _layer = y; RefreshViews(); }
            }
            OnPreviewHover(p);
        }

        private void OnLayerPaint(int x, int z, PointerPoint point, KeyModifiers mods, bool first)
        {
            if (_animBar.Index >= 0) { _info.Text = "Blocks are built in the rest pose (Animation: rest pose)."; return; }
            if (!_model.Inside(x, _layer, z)) return;
            if ((mods & KeyModifiers.Shift) != 0)
            {
                if (_model.TryGet(x, _layer, z, out int c, out _)) CurrentColor = c;
                return;
            }
            if (_tool == LayerTool.Fill)
            {
                if (!first) return;
                int c0 = _color, p0 = _preview.CurrentPart, y0 = _layer;
                Change(() => _model.FloodFillLayer(x, y0, z, c0, p0));
                return;
            }
            if (_tool == LayerTool.Select)
            {
                if (first) _selectStart = (x, z);
                bool all = _allLayers.IsChecked == true;
                int sx = Math.Clamp(_selectStart.X, 0, _model.SizeX - 1), sz = Math.Clamp(_selectStart.Z, 0, _model.SizeZ - 1);
                SetSelection(BlockModel.Box.Of(sx, all ? 0 : _layer, sz, Math.Clamp(x, 0, _model.SizeX - 1), all ? _model.SizeY - 1 : _layer, Math.Clamp(z, 0, _model.SizeZ - 1)));
                return;
            }
            bool erase = point.Properties.IsRightButtonPressed;
            if (erase) { if (_model.TryGet(x, _layer, z, out _, out _)) TakeBlock(x, _layer, z); }
            else if (!_model.TryGet(x, _layer, z, out int c, out int part) || c != _color || part != _preview.CurrentPart) PutBlock(x, _layer, z);
        }

        private void OnEditorKey(KeyEventArgs e)
        {
            if (e.Source is Visual v && (v is ComboBox or TextBox || v.FindAncestorOfType<ComboBox>() != null)) return;   // (their keys stay theirs)
            bool ctrl = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0, shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
            if (ctrl && e.Key == Key.Z && !shift) { Undo(); e.Handled = true; return; }
            if (ctrl && (e.Key == Key.Y || (e.Key == Key.Z && shift))) { Redo(); e.Handled = true; return; }
            if (ctrl && e.Key == Key.C) { Copy(); e.Handled = true; return; }
            if (ctrl && e.Key == Key.X) { Cut(); e.Handled = true; return; }
            if (ctrl && e.Key == Key.V) { Paste(); e.Handled = true; return; }
            if (ctrl && e.Key == Key.A) { SelectAll(); e.Handled = true; return; }
            if (e.Key == Key.Delete && _selection != null) { Delete(); e.Handled = true; return; }
            if (e.Key == Key.Escape && _selection != null) { SetSelection(null); e.Handled = true; return; }
            switch (e.Key)
            {
                case Key.PageUp: SetLayer(_layer + 1); e.Handled = true; break;
                case Key.PageDown: SetLayer(_layer - 1); e.Handled = true; break;
                case Key.Left: Turn(-15); e.Handled = true; break;
                case Key.Right: Turn(15); e.Handled = true; break;
                case Key.Up: _preview.Pitch = Math.Min(89, _preview.Pitch + 10); _preview.InvalidateVisual(); e.Handled = true; break;
                case Key.Down: _preview.Pitch = Math.Max(-89, _preview.Pitch - 10); _preview.InvalidateVisual(); e.Handled = true; break;
            }
        }

        // ---- palette, parts, size ----

        private async Task<string?> Input(string prompt, string title, string initial) => await Dialogs.Input(Dialogs.WindowOf(this), prompt, title, initial);

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
            string? name = await Input("The name of the new part (it hangs from the part chosen):", "New part", "part" + _model.Parts.Count);
            if (string.IsNullOrWhiteSpace(name)) return;
            int parent = Math.Max(0, _preview.CurrentPart);
            Change(() => _model.Parts.Add(new BlockModel.Part { Name = name.Trim(), Parent = parent, Pivot = new[] { _model.SizeX / 2.0, _layer, _model.SizeZ / 2.0 } }));
            _preview.CurrentPart = _layerView.CurrentPart = _model.Parts.Count - 1;
            RefreshParts();
            RefreshViews();
        }

        private async Task EditPivot()
        {
            int part = _preview.CurrentPart;
            if (part < 0 || part >= _model.Parts.Count) return;
            var pv = _model.Parts[part].Pivot;
            string? text = await Input($"Where '{_model.Parts[part].Name}' turns (cells: x, y, z; 0.5 is the middle of a cell):", "Pivot",
                string.Join(", ", pv.Select(v => v.ToString("0.###", CultureInfo.InvariantCulture))));
            if (text == null) return;
            var parts = text.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3 || !parts.All(s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _))) return;
            Change(() => _model.Parts[part].Pivot = parts.Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray());
        }

        private async Task AskSize()
        {
            string? text = await Input("The grid in cells (x, y, z - y is up) and the edge of a block in meters, e.g. 16, 16, 16, 0.1:", "Size",
                $"{_model.SizeX}, {_model.SizeY}, {_model.SizeZ}, {_model.BlockSize.ToString("0.###", CultureInfo.InvariantCulture)}");
            if (text == null) return;
            var p = text.Split(new[] { ',', ' ', ';', 'x' }, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 3 || !int.TryParse(p[0], out int sx) || !int.TryParse(p[1], out int sy) || !int.TryParse(p[2], out int sz)) return;
            double block = _model.BlockSize;
            if (p.Length > 3 && double.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double b) && b > 0) block = b;
            Change(() => { _model.Resize(sx, sy, sz); _model.BlockSize = block; });
            Attach(_model, resetUndo: false);
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
            BlockModel model;
            try { model = BlockModel.Parse(text); }
            catch (InvalidDataException ex) { model = BlockModel.CreateDefault(); _info.Text = ex.Message; }
            _animBar.Reset();
            Attach(model, resetUndo: true);
            _savedText = _text;   // (the text of the model in its own form: what is saved)
            ModifiedChanged?.Invoke();
        }

        public void MarkSaved() { _savedText = _text; ModifiedChanged?.Invoke(); }
        public int GetCaretLine() => 1;
        public void FocusEditor() => _preview.Focus();
        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;
        public bool HasSelection => _selection != null;

        public void Undo()
        {
            if (_undo.Count == 0) return;
            _redo.Add(_model.ToJson());
            string text = _undo[^1];
            _undo.RemoveAt(_undo.Count - 1);
            Attach(BlockModel.Parse(text), resetUndo: false);
            ModifiedChanged?.Invoke();
        }

        public void Redo()
        {
            if (_redo.Count == 0) return;
            _undo.Add(_model.ToJson());
            string text = _redo[^1];
            _redo.RemoveAt(_redo.Count - 1);
            Attach(BlockModel.Parse(text), resetUndo: false);
            ModifiedChanged?.Invoke();
        }

        // ---- tools and the selection ----

        private void SetTool(LayerTool tool)
        {
            _tool = tool;
            for (int t = 0; t < _toolButtons.Length; t++) _toolButtons[t].IsChecked = t == (int)tool;
        }

        public BlockModel.Box? Selection => _selection;

        public void SetSelection(BlockModel.Box? box)
        {
            _selection = box;
            _layerView.Selection = _preview.Selection = box;
            _layerView.InvalidateVisual(); _preview.InvalidateVisual();
            if (box is { } b) _info.Text = $"Selected: {b.Width} x {b.Height} x {b.Depth} cells - Ctrl+C, Ctrl+X, Ctrl+V at the pointer, Delete, Fill box, Esc";
        }

        private void FillSelection()
        {
            if (_selection is not { } box || _animBar.Index >= 0) return;
            int c = _color, p = _preview.CurrentPart;
            Change(() => _model.FillBox(box, c, p));
        }

        public void Cut()
        {
            if (_selection is not { } box || _animBar.Index >= 0) return;
            s_clipboard = _model.Copy(box);
            Change(() => _model.Delete(box));
        }

        public void Copy() { if (_selection is { } box) { s_clipboard = _model.Copy(box); _info.Text = $"Copied: {s_clipboard.Blocks.Count} blocks"; } }

        /// <summary>Puts the copied blocks with their corner at the cell under the pointer in the layer view (on the layer shown), else where the selection is.</summary>
        public void Paste()
        {
            if (s_clipboard is not { } clip || _animBar.Index >= 0) return;
            int x, y = _layer, z;
            if (_layerView.Hover is { } hv) { x = hv.X; z = hv.Z; }
            else if (_selection is { } sel) { x = sel.X0; y = sel.Y0; z = sel.Z0; }
            else { x = 0; z = 0; }
            int part = _preview.CurrentPart;
            Change(() => _model.Paste(clip, x, y, z, part));   // (the blocks join the part chosen)
            SetSelection(BlockModel.Box.Of(x, y, z, Math.Min(_model.SizeX - 1, x + clip.Width - 1), Math.Min(_model.SizeY - 1, y + clip.Height - 1), Math.Min(_model.SizeZ - 1, z + clip.Depth - 1)));
        }

        public void Delete() { if (_selection is { } box && _animBar.Index < 0) Change(() => _model.Delete(box)); }

        public void SelectAll() { SetTool(LayerTool.Select); SetSelection(BlockModel.Box.Of(0, 0, 0, _model.SizeX - 1, _model.SizeY - 1, _model.SizeZ - 1)); }
        public void Find() { }
        public void FindNext() { }
        public void FindPrevious() { }
        public int LineCount => 0;
        public void GoToLine(int line) { }
    }
}
