using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using fire.Compiler;
using fire.UI.Markup;

namespace fire.Editor
{
    /// <summary>The design view of a UI markup file: the picture of the interface as the library `ui` itself draws it (see <see cref="UiPreview"/>: the same code that runs in the program, so sizes, layout,
    /// styles and templates are exact). Only for looking - nothing in it reacts to the mouse. Elements whose text is a binding show the path in angle brackets.</summary>
    internal sealed class MarkupPreviewControl : Control
    {
        private const int Margin = 16;

        private MarkupDocument? _document;
        private UiPreviewResult? _result;
        private WriteableBitmap? _bitmap;
        private List<MarkupElement> _elements = new();
        private int _highlightLine;

        /// <summary>Shows the picture of `document`; null result: nothing is drawn yet.</summary>
        public void SetRender(MarkupDocument? document, UiPreviewResult? result)
        {
            _document = document;
            _result = result;
            _elements = document == null ? new List<MarkupElement>() : document.AllElements().Where(e => MarkupSchema.Find(e.Tag) is { IsPart: false }).ToList();
            _bitmap?.Dispose();
            _bitmap = null;
            if (result is { Ok: true, Width: > 0, Height: > 0 })
            {
                var bitmap = new WriteableBitmap(new PixelSize(result.Width, result.Height), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
                using (var frame = bitmap.Lock())
                {
                    var bytes = new byte[result.Pixels.Length * 4];
                    Buffer.BlockCopy(result.Pixels, 0, bytes, 0, bytes.Length);
                    for (int y = 0; y < result.Height; y++)
                        System.Runtime.InteropServices.Marshal.Copy(bytes, y * result.Width * 4, frame.Address + y * frame.RowBytes, result.Width * 4);
                }
                _bitmap = bitmap;
            }
            InvalidateMeasure();
            InvalidateVisual();
        }

        /// <summary>The element that starts at or before this line (1-based) is outlined (the caret of the editor); 0 = none.</summary>
        public int HighlightLine
        {
            get => _highlightLine;
            set
            {
                if (_highlightLine == value) return;
                _highlightLine = value;
                InvalidateVisual();
            }
        }

        private double PictureWidth => _result is { Ok: true } ? _result.Width : _document?.Width ?? 640;
        private double PictureHeight => _result is { Ok: true } ? _result.Height : _document?.Height ?? 480;

        protected override Size MeasureOverride(Size availableSize) => new(PictureWidth + 2 * Margin, PictureHeight + 2 * Margin);

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            if (_document == null) return;

            var origin = new Point(Margin, Margin);
            var frame = new Rect(origin, new Size(PictureWidth, PictureHeight));
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x10, 0x0E, 0x12)), null, frame.Inflate(1));
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(240, 240, 240)), null, frame);

            using (context.PushClip(frame))
            {
                if (_bitmap != null) context.DrawImage(_bitmap, new Rect(_bitmap.Size), frame);

                if (_highlightLine > 0 && FindHighlighted() is { } rect)
                    context.DrawRectangle(null, new Pen(new SolidColorBrush(EditorTheme.AccentTextColor), 2, DashStyle.Dash),
                        new Rect(origin.X + rect.X - 1, origin.Y + rect.Y - 1, rect.Width + 2, rect.Height + 2));
            }
        }

        /// <summary>The place of the element at the caret: the last element that starts at or before the line.</summary>
        private PreviewRect? FindHighlighted()
        {
            if (_result is not { Ok: true }) return null;
            int best = -1;
            for (int i = 0; i < _elements.Count; i++)
                if (_elements[i].Line <= _highlightLine && (best < 0 || _elements[i].Line >= _elements[best].Line)) best = i;
            return best < 0 ? null : _result.Rects.FirstOrDefault(r => r.Index == best);
        }
    }
}
