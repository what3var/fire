using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using fire.Terminal;
using Pen = Avalonia.Media.Pen;

namespace fire.Editor
{
    /// <summary>A document whose content is bytes, not text (a picture, a binary file): the host saves <see cref="GetBytes"/>.</summary>
    public interface IBinaryDocument
    {
        byte[] GetBytes();
    }

    /// <summary>
    /// A small pixel editor for the pictures of a project (docs/PROJECTS.md): a palette of 256 colours with the transparent colour at its start (the palette of fire; entry 0), or true colour;
    /// pencil, eraser, line, rectangle, fill and colour picker; zoom and a grid of pixels. Left button draws with the first colour, right button with the second (transparent at the start).
    /// PNG and BMP are saved (an indexed picture stays indexed; BMP cannot say which colour is transparent). The logic is <see cref="PixelCanvas"/>.
    /// </summary>
    public sealed class PixelEditorControl : UserControl, IDocumentView, IBinaryDocument
    {
        private enum Tool { Pencil, Eraser, Line, Rectangle, Fill, Picker }

        // ---- the picture ------------------------------------------------------------------------------------------------------------------
        private sealed class CanvasView : Control
        {
            public PixelCanvas Canvas { get; set; } = PixelCanvas.Create(1, 1, true);
            public double Zoom { get; set; } = 8;
            public bool Grid { get; set; } = true;
            public (int X, int Y)? Hover { get; set; }
            private WriteableBitmap? _bitmap;
            private static readonly IBrush Light = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x5B));
            private static readonly IBrush Dark = new SolidColorBrush(Color.FromRgb(0x3E, 0x3E, 0x44));

            public event Action<int, int, PointerPoint>? Pressed;
            public event Action<int, int, PointerPoint>? Moved;
            public event Action<int, int, PointerPoint>? Released;
            public event Action? WheelZoom;
            public int WheelDirection { get; private set; }

            /// <summary>Draws the picture again (after its pixels changed).</summary>
            public void Refresh()
            {
                _bitmap?.Dispose();
                _bitmap = null;
                InvalidateMeasure();
                InvalidateVisual();
            }

            protected override Size MeasureOverride(Size availableSize) => new(Math.Ceiling(Canvas.Width * Zoom), Math.Ceiling(Canvas.Height * Zoom));

            private (int, int) Cell(Point p) => ((int)Math.Floor(p.X / Zoom), (int)Math.Floor(p.Y / Zoom));

            protected override void OnPointerPressed(PointerPressedEventArgs e)
            {
                base.OnPointerPressed(e);
                var point = e.GetCurrentPoint(this);
                var (x, y) = Cell(point.Position);
                e.Pointer.Capture(this);
                Pressed?.Invoke(x, y, point);
            }

            protected override void OnPointerMoved(PointerEventArgs e)
            {
                base.OnPointerMoved(e);
                var point = e.GetCurrentPoint(this);
                var (x, y) = Cell(point.Position);
                Hover = Canvas.InBounds(x, y) ? (x, y) : null;
                Moved?.Invoke(x, y, point);
            }

            protected override void OnPointerReleased(PointerReleasedEventArgs e)
            {
                base.OnPointerReleased(e);
                var point = e.GetCurrentPoint(this);
                var (x, y) = Cell(point.Position);
                e.Pointer.Capture(null);
                Released?.Invoke(x, y, point);
            }

            protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
            {
                if ((e.KeyModifiers & KeyModifiers.Control) == 0) { base.OnPointerWheelChanged(e); return; }
                WheelDirection = e.Delta.Y > 0 ? 1 : -1;
                WheelZoom?.Invoke();
                e.Handled = true;
            }

            private void EnsureBitmap()
            {
                if (_bitmap != null) return;
                var bitmap = new WriteableBitmap(new PixelSize(Canvas.Width, Canvas.Height), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
                var colors = new uint[Canvas.Width * Canvas.Height];
                Canvas.CopyColors(colors);
                using (var frame = bitmap.Lock())
                {
                    var bytes = new byte[colors.Length * 4];
                    Buffer.BlockCopy(colors, 0, bytes, 0, bytes.Length);   // R, G, B, A in memory: the packed colour is little endian
                    for (int y = 0; y < Canvas.Height; y++)
                        System.Runtime.InteropServices.Marshal.Copy(bytes, y * Canvas.Width * 4, frame.Address + y * frame.RowBytes, Canvas.Width * 4);
                }
                _bitmap = bitmap;
            }

            public override void Render(DrawingContext context)
            {
                EnsureBitmap();
                double w = Canvas.Width * Zoom, h = Canvas.Height * Zoom;
                var area = new Rect(0, 0, w, h);
                const double square = 8;
                using (context.PushClip(area))
                {
                    context.FillRectangle(Light, area);
                    for (double y = 0; y < h; y += square)
                        for (double x = ((int)(y / square) % 2) * square; x < w; x += 2 * square)
                            context.FillRectangle(Dark, new Rect(x, y, square, square));
                    using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None }))
                        context.DrawImage(_bitmap!, new Rect(0, 0, Canvas.Width, Canvas.Height), area);
                    if (Grid && Zoom >= 6)
                    {
                        var pen = new Pen(new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)), 1);
                        for (int x = 1; x < Canvas.Width; x++) context.DrawLine(pen, new Point(x * Zoom, 0), new Point(x * Zoom, h));
                        for (int y = 1; y < Canvas.Height; y++) context.DrawLine(pen, new Point(0, y * Zoom), new Point(w, y * Zoom));
                    }
                    if (Hover is { } hv)
                        context.DrawRectangle(null, new Pen(Brushes.White, 1), new Rect(hv.X * Zoom + 0.5, hv.Y * Zoom + 0.5, Math.Max(1, Zoom - 1), Math.Max(1, Zoom - 1)));
                }
            }
        }

        // ---- the palette -------------------------------------------------------------------------------------------------------------------
        private sealed class PaletteView : Control
        {
            public const int Cell = 12;
            public PixelCanvas Canvas { get; set; } = PixelCanvas.Create(1, 1, true);
            public int Primary { get; set; } = 1;
            public int Secondary { get; set; }
            public event Action<int, bool>? Picked;   // index, true = right button
            public event Action<int>? EditRequested;

            protected override Size MeasureOverride(Size availableSize) => new(16 * Cell + 1, 16 * Cell + 1);

            private static int IndexAt(Point p) => (int)(p.Y / Cell) * 16 + (int)(p.X / Cell);

            protected override void OnPointerPressed(PointerPressedEventArgs e)
            {
                base.OnPointerPressed(e);
                var point = e.GetCurrentPoint(this);
                int index = IndexAt(point.Position);
                if (index < 0 || index > 255) return;
                if (e.ClickCount == 2) { EditRequested?.Invoke(index); return; }
                Picked?.Invoke(index, point.Properties.IsRightButtonPressed);
            }

            public override void Render(DrawingContext context)
            {
                for (int i = 0; i < 256; i++)
                {
                    var cell = new Rect((i % 16) * Cell, (i / 16) * Cell, Cell, Cell);
                    // a chequerboard under entries that are not opaque
                    uint color = Canvas.EntryColor(i);
                    if ((color >> 24) != 255)
                    {
                        context.FillRectangle(new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x5B)), cell);
                        context.FillRectangle(new SolidColorBrush(Color.FromRgb(0x3E, 0x3E, 0x44)), new Rect(cell.X, cell.Y, Cell / 2, Cell / 2));
                        context.FillRectangle(new SolidColorBrush(Color.FromRgb(0x3E, 0x3E, 0x44)), new Rect(cell.X + Cell / 2, cell.Y + Cell / 2, Cell / 2, Cell / 2));
                    }
                    context.FillRectangle(new SolidColorBrush(ToColor(color)), cell);
                }
                if (Canvas.IsIndexed)
                {
                    Mark(context, Primary, Brushes.White);
                    Mark(context, Secondary, Brushes.Black);
                }
            }

            private static void Mark(DrawingContext context, int index, IBrush brush) =>
                context.DrawRectangle(null, new Pen(brush, 2), new Rect((index % 16) * Cell + 1, (index / 16) * Cell + 1, Cell - 2, Cell - 2));
        }

        private static Color ToColor(uint packed) => Color.FromArgb((byte)(packed >> 24), (byte)packed, (byte)(packed >> 8), (byte)(packed >> 16));

        /// <summary>`#RRGGBB` or `#RRGGBBAA` (the `#` is optional) as a packed colour (R in the lowest byte); null if it is not one.</summary>
        public static uint? ParseColor(string? text)
        {
            if (text == null) return null;
            text = text.Trim().TrimStart('#');
            if (text.Length is not (6 or 8) || !uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v)) return null;
            uint r, g, b, a = 255;
            if (text.Length == 6) { r = v >> 16; g = (v >> 8) & 255; b = v & 255; }
            else { r = v >> 24; g = (v >> 16) & 255; b = (v >> 8) & 255; a = v & 255; }
            return (a << 24) | (b << 16) | (g << 8) | r;
        }

        public static string FormatColor(uint packed) => $"#{(byte)packed:X2}{(byte)(packed >> 8):X2}{(byte)(packed >> 16):X2}{(byte)(packed >> 24):X2}";

        // ---- the control -------------------------------------------------------------------------------------------------------------------
        private static readonly double[] ZoomSteps = { 1, 2, 3, 4, 6, 8, 12, 16, 24, 32, 48 };
        private readonly CanvasView _view = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        private readonly PaletteView _palette = new();
        private readonly ScrollViewer _scroll;
        private readonly TextBlock _info = new() { Foreground = EditorTheme.TextDim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
        private readonly TextBlock _colorInfo = new() { Foreground = EditorTheme.TextDim, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        private readonly Border _primarySwatch = new() { Width = 28, Height = 28, BorderBrush = Brushes.White, BorderThickness = new Thickness(2) };
        private readonly Border _secondarySwatch = new() { Width = 28, Height = 28, BorderBrush = Brushes.Black, BorderThickness = new Thickness(2) };
        private readonly ToggleButton[] _toolButtons;
        private readonly CheckBox _filled = new() { Content = "Filled", Margin = new Thickness(6, 0) };
        private readonly Button _modeButton = new();
        private PixelCanvas _canvas = PixelCanvas.Create(16, 16, true);
        private Tool _tool = Tool.Pencil;
        private uint _primary = 1, _secondary;   // values: palette indices in an indexed picture, packed colours in a truecolor one
        private (int X, int Y)? _start, _last;
        private bool _drawing, _rightButton;

        public string? FilePath { get; set; }
        public bool IsModified => _canvas.IsModified;
        public bool IsReadOnly => false;
        public event Action? ModifiedChanged;
        public event Action<int>? CaretLineChanged { add { } remove { } }

        public PixelEditorControl()
        {
            _toolButtons = new[]
            {
                Tool_("Pencil", Tool.Pencil, "Draw (left: first colour, right: second colour)"), Tool_("Eraser", Tool.Eraser, "Make pixels transparent"),
                Tool_("Line", Tool.Line, "Draw a line"), Tool_("Rectangle", Tool.Rectangle, "Draw a rectangle (Filled: solid)"),
                Tool_("Fill", Tool.Fill, "Fill an area of one colour"), Tool_("Picker", Tool.Picker, "Take the colour of a pixel"),
            };
            _toolButtons[0].IsChecked = true;

            var zoomOut = Button_("−", () => Step(-1)); var zoomIn = Button_("+", () => Step(+1));
            var fit = Button_("Fit", Fit);
            var grid = new ToggleButton { Content = "Grid", IsChecked = true }; grid.Classes.Add("tool");
            grid.Click += (_, _) => { _view.Grid = grid.IsChecked == true; _view.InvalidateVisual(); };
            var size = Button_("Size...", async () => await AskSize());
            _modeButton.Classes.Add("tool");
            _modeButton.Click += (_, _) => SwitchMode();

            var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(4, 2) };
            foreach (var b in _toolButtons) bar.Children.Add(b);
            bar.Children.Add(_filled);
            bar.Children.Add(new Border { Width = 1, Background = EditorTheme.Border, Margin = new Thickness(6, 2) });
            foreach (var c in new Control[] { zoomOut, zoomIn, fit, grid, size, _modeButton, _info }) bar.Children.Add(c);

            _scroll = new ScrollViewer
            {
                Content = new Border { Padding = new Thickness(12), Child = _view },
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = EditorTheme.Solid(Color.FromRgb(0x1B, 0x18, 0x1E)),
            };

            var swatches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _primarySwatch, _secondarySwatch } };
            var edit = Button_("Edit color...", async () => await EditPrimaryColor());
            var side = new StackPanel { Margin = new Thickness(8), Spacing = 8, Width = 16 * PaletteView.Cell + 17 };
            side.Children.Add(new TextBlock { Text = "Palette", Foreground = EditorTheme.TextDim });
            side.Children.Add(_palette);
            side.Children.Add(swatches);
            side.Children.Add(edit);
            side.Children.Add(_colorInfo);
            side.Children.Add(new TextBlock { Text = "Left button: first colour, right button: second. Double-click a palette entry to change its colour. Entry 0 is transparent.", Foreground = EditorTheme.TextDim, FontSize = 11, TextWrapping = TextWrapping.Wrap });
            var sidePanel = new Border { Child = new ScrollViewer { Content = side }, BorderBrush = EditorTheme.Border, BorderThickness = new Thickness(1, 0, 0, 0) };

            var dock = new DockPanel();
            DockPanel.SetDock(bar, Avalonia.Controls.Dock.Top);
            DockPanel.SetDock(sidePanel, Avalonia.Controls.Dock.Right);
            dock.Children.Add(bar);
            dock.Children.Add(sidePanel);
            dock.Children.Add(_scroll);
            Content = dock;
            Focusable = true;

            _view.Pressed += OnPressed; _view.Moved += OnMoved; _view.Released += OnReleased;
            _view.WheelZoom += () => Step(_view.WheelDirection);
            _palette.Picked += (index, right) => PickEntry(index, right);
            _palette.EditRequested += async index => { PickEntry(index, false); await EditPrimaryColor(); };
            Attach(PixelCanvas.Create(16, 16, true));
        }

        private ToggleButton Tool_(string text, Tool tool, string tip)
        {
            var b = new ToggleButton { Content = text };
            b.Classes.Add("tool");
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) =>
            {
                _tool = tool;
                foreach (var other in _toolButtons) other.IsChecked = ReferenceEquals(other, b);
            };
            return b;
        }

        private static Button Button_(string text, Action click)
        {
            var b = new Button { Content = text };
            b.Classes.Add("tool");
            b.Click += (_, _) => click();
            return b;
        }

        private void Attach(PixelCanvas canvas)
        {
            _canvas = canvas;
            _canvas.Changed += OnCanvasChanged;
            _view.Canvas = canvas;
            _palette.Canvas = canvas;
            _primary = canvas.IsIndexed ? 1u : 0xFF000000u;
            _secondary = canvas.IsIndexed ? (uint)Math.Max(canvas.TransparentIndex, 0) : 0u;
            if (canvas.IsIndexed && canvas.TransparentIndex < 0) _secondary = 0;
            double zoom = Math.Max(1, Math.Min(16, 384 / Math.Max(canvas.Width, canvas.Height)));
            _view.Zoom = ZoomSteps.LastOrDefault(z => z <= zoom, 1);
            RefreshAll();
        }

        private void OnCanvasChanged()
        {
            RefreshAll();
            ModifiedChanged?.Invoke();
        }

        private void RefreshAll()
        {
            _palette.Primary = _canvas.IsIndexed ? (int)_primary : -1;
            _palette.Secondary = _canvas.IsIndexed ? (int)_secondary : -1;
            _palette.Canvas = _canvas;
            _palette.InvalidateVisual();
            _view.Refresh();
            _modeButton.Content = _canvas.IsIndexed ? "To true color" : "To 256 colors";
            ToolTip.SetTip(_modeButton, _canvas.IsIndexed ? "Change this picture to true colour (every pixel its own colour)" : "Change this picture to the palette of 256 colours (nearest colours)");
            _primarySwatch.Background = SwatchBrush(_primary);
            _secondarySwatch.Background = SwatchBrush(_secondary);
            UpdateInfo();
        }

        private IBrush SwatchBrush(uint value) => new SolidColorBrush(ToColor(_canvas.IsIndexed ? _canvas.EntryColor((int)value) : value));

        private void UpdateInfo(int? x = null, int? y = null)
        {
            string mode = _canvas.IsIndexed ? "256 colours" : "true colour";
            string at = x is { } px && y is { } py && _canvas.InBounds(px, py) ? $"   ({px}, {py})  {FormatColor(_canvas.ColorAt(px, py))}" + (_canvas.IsIndexed ? $"  entry {_canvas.ValueAt(px, py)}" : "") : "";
            _info.Text = $"{_canvas.Width} × {_canvas.Height}, {mode}   {_view.Zoom * 100:0}%{at}";
            _colorInfo.Text = _canvas.IsIndexed ? $"First: entry {_primary} {FormatColor(_canvas.EntryColor((int)_primary))}\nSecond: entry {_secondary} {FormatColor(_canvas.EntryColor((int)_secondary))}" : $"First: {FormatColor(_primary)}\nSecond: {FormatColor(_secondary)}";
        }

        // ---- zoom, size, mode --------------------------------------------------------------------------------------------------------------
        private void Step(int direction)
        {
            int at = 0;
            for (int i = 0; i < ZoomSteps.Length; i++) if (ZoomSteps[i] <= _view.Zoom + 0.001) at = i;
            SetZoom(ZoomSteps[Math.Clamp(at + direction, 0, ZoomSteps.Length - 1)]);
        }

        private void SetZoom(double zoom) { _view.Zoom = zoom; _view.Refresh(); UpdateInfo(); }

        private void Fit()
        {
            double w = Math.Max(1, _scroll.Bounds.Width - 40), h = Math.Max(1, _scroll.Bounds.Height - 40);
            double zoom = Math.Min(w / _canvas.Width, h / _canvas.Height);
            SetZoom(ZoomSteps.LastOrDefault(z => z <= zoom, 1));
        }

        private async System.Threading.Tasks.Task AskSize()
        {
            string? text = await Dialogs.Input(Dialogs.WindowOf(this), "New size in pixels (width x height). The content stays at the top left:", "Size", $"{_canvas.Width} x {_canvas.Height}");
            if (text == null) return;
            var parts = text.Split(new[] { 'x', 'X', '×', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !int.TryParse(parts[0], out int w) || !int.TryParse(parts[1], out int h) || w < 1 || h < 1 || (long)w * h > 16_000_000)
            {
                await Dialogs.Message(Dialogs.WindowOf(this), "Write the size like 32 x 32 (at most 16 million pixels).", "Size");
                return;
            }
            _canvas.Resize(w, h);
        }

        private void SwitchMode()
        {
            if (_canvas.IsIndexed) _canvas.ConvertToTruecolor(); else _canvas.ConvertToIndexed();
            _primary = _canvas.IsIndexed ? 1u : 0xFF000000u;
            _secondary = _canvas.IsIndexed ? (uint)Math.Max(_canvas.TransparentIndex, 0) : 0u;
            RefreshAll();
        }

        // ---- colours -----------------------------------------------------------------------------------------------------------------------
        private void PickEntry(int index, bool right)
        {
            uint value = _canvas.IsIndexed ? (uint)index : _canvas.Palette[index];
            if (!_canvas.IsIndexed && index == 0) value = 0;   // the start of the palette is transparent
            if (right) _secondary = value; else _primary = value;
            RefreshAll();
        }

        private async System.Threading.Tasks.Task EditPrimaryColor()
        {
            uint current = _canvas.IsIndexed ? _canvas.Palette[(int)_primary] : _primary;
            string? text = await Dialogs.Input(Dialogs.WindowOf(this), _canvas.IsIndexed ? $"Colour of palette entry {_primary} (#RRGGBB or #RRGGBBAA):" : "The first colour (#RRGGBB or #RRGGBBAA):", "Colour", FormatColor(current));
            if (text == null) return;
            if (ParseColor(text) is not { } color) { await Dialogs.Message(Dialogs.WindowOf(this), "Write the colour like #FF8800 or #FF880080.", "Colour"); return; }
            if (_canvas.IsIndexed) _canvas.SetPaletteColor((int)_primary, color);
            else { _primary = color; RefreshAll(); }
        }

        // ---- drawing -----------------------------------------------------------------------------------------------------------------------
        private uint TransparentValue => _canvas.IsIndexed ? (uint)Math.Max(_canvas.TransparentIndex, 0) : 0u;

        private void OnPressed(int x, int y, PointerPoint point)
        {
            if (!point.Properties.IsLeftButtonPressed && !point.Properties.IsRightButtonPressed) return;
            _rightButton = point.Properties.IsRightButtonPressed;
            uint value = _tool == Tool.Eraser ? TransparentValue : _rightButton ? _secondary : _primary;
            if (_tool == Tool.Picker)
            {
                if (_canvas.InBounds(x, y))
                {
                    uint picked = _canvas.ValueAt(x, y);
                    if (_rightButton) _secondary = picked; else _primary = picked;
                    RefreshAll();
                }
                return;
            }
            if (!_canvas.InBounds(x, y) && _tool is Tool.Pencil or Tool.Eraser or Tool.Fill) return;
            _drawing = true;
            _start = _last = (x, y);
            _canvas.BeginEdit();
            switch (_tool)
            {
                case Tool.Pencil or Tool.Eraser: _canvas.Set(x, y, value); break;
                case Tool.Fill: _canvas.Fill(x, y, value); break;
                case Tool.Line: _canvas.Set(x, y, value); break;
                case Tool.Rectangle: _canvas.Rectangle(x, y, x, y, value, true); break;
            }
            _view.Refresh();
        }

        private void OnMoved(int x, int y, PointerPoint point)
        {
            UpdateInfo(x, y);
            if (!_drawing)
            {
                _view.InvalidateVisual();
                return;
            }
            uint value = _tool == Tool.Eraser ? TransparentValue : _rightButton ? _secondary : _primary;
            switch (_tool)
            {
                case Tool.Pencil or Tool.Eraser:
                    _canvas.Line(_last!.Value.X, _last.Value.Y, x, y, value);
                    break;
                case Tool.Line:
                    _canvas.RestoreStroke();
                    _canvas.Line(_start!.Value.X, _start.Value.Y, x, y, value);
                    break;
                case Tool.Rectangle:
                    _canvas.RestoreStroke();
                    _canvas.Rectangle(_start!.Value.X, _start.Value.Y, x, y, value, _filled.IsChecked == true);
                    break;
                default: return;
            }
            _last = (x, y);
            _view.Refresh();
        }

        private void OnReleased(int x, int y, PointerPoint point)
        {
            if (!_drawing) return;
            _drawing = false;
            _canvas.EndEdit();   // announces the change (the picture was already drawn)
            RefreshAll();
        }

        // ---- IDocumentView -----------------------------------------------------------------------------------------------------------------
        public string GetText() => "";
        public int GetCaretLine() => 1;
        public void FocusEditor() => Focus();
        public bool CanUndo => _canvas.CanUndo;
        public bool CanRedo => _canvas.CanRedo;
        public bool HasSelection => false;
        public int LineCount => 1;
        public void Undo() { _canvas.Undo(); }
        public void Redo() { _canvas.Redo(); }
        public void Cut() { }
        public void Copy() { }
        public void Paste() { }
        public void Delete() { }
        public void SelectAll() { }
        public void GoToLine(int line) { }
        public void Find() { }
        public void FindNext() { }
        public void FindPrevious() { }

        public void MarkSaved()
        {
            _canvas.MarkSaved();
            ModifiedChanged?.Invoke();
        }

        /// <summary>The picture as the file format of the path (PNG or BMP; a new picture: PNG).</summary>
        public byte[] GetBytes() => ImageEncoder.Encode(_canvas.ToImage(), FilePath != null ? ImageEncoder.FormatOf(FilePath) ?? "PNG" : "PNG");

        /// <summary>Loads the picture from the file (`text` is not used); without a path: a new empty picture of 16 x 16 pixels with the palette of fire.</summary>
        public void ResetTo(string text, string? filePath)
        {
            FilePath = filePath;
            if (filePath != null && File.Exists(filePath))
            {
                try { Attach(PixelCanvas.FromImage(ImageDecoder.Decode(File.ReadAllBytes(filePath)))); return; }
                catch (ImageFormatException ex) { _info.Text = "This picture cannot be edited: " + ex.Message; }
                catch (IOException ex) { _info.Text = ex.Message; }
            }
            Attach(PixelCanvas.Create(16, 16, true));
        }

        /// <summary>A new picture of the given size (indexed: the palette of fire with entry 0 transparent).</summary>
        public void NewPicture(int width, int height, bool indexed)
        {
            FilePath = null;
            Attach(PixelCanvas.Create(width, height, indexed));
        }
    }
}
