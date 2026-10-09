using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace fire.Editor
{
    /// <summary>
    /// Shows a picture of a project (a resource, docs/RESOURCES.md): any size and colour depth the platform can decode (PNG, BMP, GIF, JPEG), zoomed with sharp pixels, on a chequerboard that
    /// shows what is transparent, with an optional grid of pixels. Ctrl+wheel zooms. To look at only; the pixel editor changes pictures.
    /// </summary>
    public sealed class ImageViewerControl : UserControl, IDocumentView
    {
        /// <summary>The picture itself, drawn at the zoom; the grid is drawn over it from a zoom of 6 on.</summary>
        private sealed class Canvas : Control
        {
            public Bitmap? Bitmap { get; set; }
            public double Zoom { get; set; } = 1;
            public bool Grid { get; set; }

            private static readonly IBrush Light = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x5B));
            private static readonly IBrush Dark = new SolidColorBrush(Color.FromRgb(0x3E, 0x3E, 0x44));

            protected override Size MeasureOverride(Size availableSize) =>
                Bitmap == null ? new Size(0, 0) : new Size(Math.Ceiling(Bitmap.PixelSize.Width * Zoom), Math.Ceiling(Bitmap.PixelSize.Height * Zoom));

            public override void Render(DrawingContext context)
            {
                if (Bitmap == null) return;
                double w = Bitmap.PixelSize.Width * Zoom, h = Bitmap.PixelSize.Height * Zoom;
                var area = new Rect(0, 0, w, h);
                // the chequerboard (8 x 8 device pixels per square)
                const double square = 8;
                using (context.PushClip(area))
                {
                    context.FillRectangle(Light, area);
                    for (double y = 0; y < h; y += square)
                        for (double x = ((int)(y / square) % 2) * square; x < w; x += 2 * square)
                            context.FillRectangle(Dark, new Rect(x, y, square, square));
                    using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None }))
                        context.DrawImage(Bitmap, new Rect(0, 0, Bitmap.PixelSize.Width, Bitmap.PixelSize.Height), area);
                    if (Grid && Zoom >= 6)
                    {
                        var pen = new Pen(new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)), 1);
                        for (int x = 1; x < Bitmap.PixelSize.Width; x++) context.DrawLine(pen, new Point(x * Zoom, 0), new Point(x * Zoom, h));
                        for (int y = 1; y < Bitmap.PixelSize.Height; y++) context.DrawLine(pen, new Point(0, y * Zoom), new Point(w, y * Zoom));
                    }
                }
            }
        }

        private static readonly double[] ZoomSteps = { 0.25, 0.5, 1, 2, 3, 4, 6, 8, 12, 16, 24, 32 };
        private readonly Canvas _canvas = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        private readonly ScrollViewer _scroll;
        private readonly TextBlock _info = new() { Foreground = EditorTheme.TextDim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
        private Bitmap? _bitmap;

        public string? FilePath { get; set; }
        public bool IsModified => false;
        public bool IsReadOnly => true;
        public event Action? ModifiedChanged { add { } remove { } }
        public event Action<int>? CaretLineChanged { add { } remove { } }

        /// <summary>The user wants to change the picture: the host opens it in the pixel editor.</summary>
        public event Action? EditRequested;

        public ImageViewerControl()
        {
            var zoomOut = new Button { Content = "−" }; zoomOut.Classes.Add("tool"); zoomOut.Click += (_, _) => Step(-1);
            var zoomIn = new Button { Content = "+" }; zoomIn.Classes.Add("tool"); zoomIn.Click += (_, _) => Step(+1);
            var actual = new Button { Content = "1:1" }; actual.Classes.Add("tool"); actual.Click += (_, _) => SetZoom(1);
            var fit = new Button { Content = "Fit" }; fit.Classes.Add("tool"); fit.Click += (_, _) => Fit();
            var grid = new ToggleButton { Content = "Grid" }; grid.Classes.Add("tool");
            grid.Click += (_, _) => { _canvas.Grid = grid.IsChecked == true; _canvas.InvalidateVisual(); };
            var edit = new Button { Content = "Edit pixels" }; edit.Classes.Add("tool");
            edit.Click += (_, _) => EditRequested?.Invoke();
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(4, 2), Children = { zoomOut, zoomIn, actual, fit, grid, edit, _info } };
            _scroll = new ScrollViewer
            {
                Content = new Border { Padding = new Thickness(12), Child = _canvas },
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Background = EditorTheme.Solid(Color.FromRgb(0x1B, 0x18, 0x1E)),
            };
            _scroll.AddHandler(PointerWheelChangedEvent, (object? _, PointerWheelEventArgs e) =>
            {
                if ((e.KeyModifiers & KeyModifiers.Control) == 0) return;
                Step(e.Delta.Y > 0 ? +1 : -1);
                e.Handled = true;
            }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            var dock = new DockPanel();
            DockPanel.SetDock(bar, Avalonia.Controls.Dock.Top);
            dock.Children.Add(bar);
            dock.Children.Add(_scroll);
            Content = dock;
            Focusable = true;
        }

        private void Step(int direction)
        {
            int at = 0;
            for (int i = 0; i < ZoomSteps.Length; i++) if (ZoomSteps[i] <= _canvas.Zoom + 0.001) at = i;
            if (direction > 0 && ZoomSteps[at] < _canvas.Zoom - 0.001) at--;   // between two steps: the next one up
            SetZoom(ZoomSteps[Math.Clamp(at + direction, 0, ZoomSteps.Length - 1)]);
        }

        private void SetZoom(double zoom)
        {
            _canvas.Zoom = zoom;
            _canvas.InvalidateMeasure();
            _canvas.InvalidateVisual();
            UpdateInfo();
        }

        private void Fit()
        {
            if (_bitmap == null) return;
            double w = Math.Max(1, _scroll.Bounds.Width - 40), h = Math.Max(1, _scroll.Bounds.Height - 40);
            double zoom = Math.Min(w / _bitmap.PixelSize.Width, h / _bitmap.PixelSize.Height);
            SetZoom(Math.Max(0.1, Math.Floor(zoom * 100) / 100));
        }

        private void UpdateInfo() =>
            _info.Text = _bitmap == null ? "" : $"{_bitmap.PixelSize.Width} × {_bitmap.PixelSize.Height} pixels   {_canvas.Zoom * 100:0}%";

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

        /// <summary>Loads the picture from the file (`text` is not used: a picture is not text).</summary>
        public void ResetTo(string text, string? filePath)
        {
            FilePath = filePath;
            _bitmap?.Dispose();
            _bitmap = null;
            if (filePath != null)
            {
                try { _bitmap = new Bitmap(filePath); }
                catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or NotSupportedException) { _info.Text = "This picture cannot be shown: " + ex.Message; }
            }
            _canvas.Bitmap = _bitmap;
            int zoom = _bitmap == null ? 1 : Math.Max(1, Math.Min(8, 256 / Math.Max(1, Math.Max(_bitmap.PixelSize.Width, _bitmap.PixelSize.Height))));   // small pictures start magnified
            SetZoom(zoom);
        }
    }
}
