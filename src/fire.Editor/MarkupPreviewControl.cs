using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using fire.UI.Markup;

namespace fire.Editor
{
    /// <summary>The design view of a UI markup file: a picture of the interface as the library `ui` draws it (same sizes and layout, the light theme of the library, the 8x14 pixel
    /// characters approximated by a monospaced font). Only for looking - nothing in it reacts to the mouse. Elements whose text is a binding show the path in angle brackets.</summary>
    internal sealed class MarkupPreviewControl : Control
    {
        private const int CharWidth = 8, CharHeight = 14, Margin = 16;

        // the light theme of UI.Theme
        private static readonly Color Back = Color.FromRgb(240, 240, 240), PanelColor = Color.FromRgb(250, 250, 250), Face = Color.FromRgb(225, 225, 225),
            BorderColor = Color.FromRgb(120, 120, 120), TextColor = Color.FromRgb(0, 0, 0), TextDisabled = Color.FromRgb(131, 131, 131),
            InputBack = Color.FromRgb(255, 255, 255), Accent = Color.FromRgb(0, 120, 215);

        private static readonly Typeface Mono = new("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace");

        private sealed class Box
        {
            public required MarkupElement Element { get; init; }
            public double X, Y, Width, Height;
            public string Text = "";
            public bool Dim;
            public List<Box> Children { get; } = new();
        }

        private MarkupDocument? _document;
        private List<Box> _roots = new();
        private int _highlightLine;

        public void SetDocument(MarkupDocument? document)
        {
            _document = document;
            _roots = document == null ? new List<Box>() : document.Children.Select(c => Layout(c, null, 0, 0)).ToList();
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

        // ---- layout, like UI.Panel / UI.Stack and the Draw methods of the elements ----

        private static int IntOf(MarkupElement e, string name, int fallback) =>
            e.Find(name)?.Value is LiteralValue { Text: var t } && int.TryParse(t.Trim(), out int n) ? n : fallback;

        private static bool BoolOf(MarkupElement e, string name, bool fallback) =>
            e.Find(name)?.Value is LiteralValue { Text: var t } ? t.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) || (!t.Trim().Equals("false", StringComparison.OrdinalIgnoreCase) && fallback) : fallback;

        private static string TextOf(MarkupElement e) => e.Find("text")?.Value switch
        {
            LiteralValue l => l.Text,
            BindingValue b => "‹" + b.Path + "›",
            ExpressionValue x => "‹" + x.Code + "›",
            _ => "",
        };

        private static Box Layout(MarkupElement element, Box? parent, double x, double y)
        {
            var box = new Box { Element = element, X = x, Y = y, Text = TextOf(element) };
            box.Dim = !BoolOf(element, "visible", true) || !BoolOf(element, "enabled", true);
            switch (element.Tag)
            {
                case "Panel":
                case "Stack":
                    box.Width = IntOf(element, "width", 100);
                    box.Height = IntOf(element, "height", 100);
                    break;
                case "Label":
                    box.Width = CharWidth * box.Text.Length;
                    box.Height = CharHeight;
                    break;
                case "CheckBox":
                    box.Width = 14 + 6 + CharWidth * box.Text.Length;
                    box.Height = Math.Max(CharHeight, 14);
                    break;
                case "Button":
                    box.Width = IntOf(element, "width", 90);
                    box.Height = IntOf(element, "height", 26);
                    break;
                default: // TextBox
                    box.Width = IntOf(element, "width", 160);
                    box.Height = IntOf(element, "height", 24);
                    break;
            }

            bool stack = element.Tag == "Stack";
            bool horizontal = stack && (BoolOf(element, "horizontal", false) ||
                (element.Find("orientation")?.Value is LiteralValue { Text: var o } && o.Trim().Equals("Horizontal", StringComparison.OrdinalIgnoreCase)));
            int padding = IntOf(element, "padding", 4), spacing = IntOf(element, "spacing", 4);
            double position = padding;
            foreach (var child in element.Children)
            {
                Box childBox;
                if (stack)
                {
                    childBox = Layout(child, box, 0, 0);
                    if (BoolOf(child, "visible", true))
                    {
                        if (horizontal) { childBox.X = position; childBox.Y = padding; position += childBox.Width + spacing; }
                        else { childBox.X = padding; childBox.Y = position; position += childBox.Height + spacing; }
                    }
                }
                else childBox = Layout(child, box, IntOf(child, "x", 0), IntOf(child, "y", 0));
                box.Children.Add(childBox);
            }
            return box;
        }

        // ---- drawing ----

        private double WindowWidth => _document?.Width ?? 640;
        private double WindowHeight => _document?.Height ?? 480;

        protected override Size MeasureOverride(Size availableSize) => new(WindowWidth + 2 * Margin, WindowHeight + 2 * Margin);

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            if (_document == null) return;

            var origin = new Point(Margin, Margin);
            var frame = new Rect(origin, new Size(WindowWidth, WindowHeight));
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x10, 0x0E, 0x12)), null, frame.Inflate(1));
            context.DrawRectangle(new SolidColorBrush(Back), null, frame);

            using (context.PushClip(frame))
            {
                foreach (var root in _roots) Draw(context, root, origin.X, origin.Y);

                var highlighted = _highlightLine > 0 ? FindByLine(_roots) : null;
                if (highlighted != null)
                {
                    var (hx, hy) = AbsolutePosition(highlighted);
                    context.DrawRectangle(null, new Pen(new SolidColorBrush(EditorTheme.AccentTextColor), 2, DashStyle.Dash),
                        new Rect(origin.X + hx - 1, origin.Y + hy - 1, highlighted.Width + 2, highlighted.Height + 2));
                }
            }
        }

        private Box? FindByLine(IEnumerable<Box> boxes)
        {
            Box? best = null;
            void Visit(Box box)
            {
                if (box.Element.Line <= _highlightLine && (best == null || box.Element.Line >= best.Element.Line)) best = box;
                foreach (var child in box.Children) Visit(child);
            }
            foreach (var box in boxes) Visit(box);
            return best;
        }

        private (double X, double Y) AbsolutePosition(Box target)
        {
            (double, double)? Find(IEnumerable<Box> boxes, double ox, double oy)
            {
                foreach (var box in boxes)
                {
                    if (ReferenceEquals(box, target)) return (ox + box.X, oy + box.Y);
                    if (Find(box.Children, ox + box.X, oy + box.Y) is { } found) return found;
                }
                return null;
            }
            return Find(_roots, 0, 0) ?? (0, 0);
        }

        private static IBrush Brush(Color color, bool dim) => new SolidColorBrush(color, dim ? 0.45 : 1);

        private static Color ColorOf(MarkupElement e, string name, Color fallback) =>
            e.Find(name)?.Value is LiteralValue { Text: var t } && FireUiGenerator.TryParseColor(t.Trim(), out int r, out int g, out int b)
                ? Color.FromRgb((byte)r, (byte)g, (byte)b) : fallback;

        private static void DrawText(DrawingContext context, string text, double x, double y, Color color, bool dim)
        {
            if (text.Length == 0) return;
            var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Mono, 13, Brush(color, dim));
            // the characters of the library are 8 pixels wide: scale the approximation to that
            double scale = formatted.Width > 0 ? CharWidth * text.Length / formatted.Width : 1;
            using (context.PushTransform(Matrix.CreateScale(scale, 1) * Matrix.CreateTranslation(x, y + (CharHeight - formatted.Height) / 2)))
                context.DrawText(formatted, new Point(0, 0));
        }

        private void Draw(DrawingContext context, Box box, double ox, double oy)
        {
            double x = ox + box.X, y = oy + box.Y;
            var rect = new Rect(x, y, box.Width, box.Height);
            var element = box.Element;
            bool dim = box.Dim;
            var border = new Pen(Brush(BorderColor, dim), 1);

            switch (element.Tag)
            {
                case "Panel":
                case "Stack":
                    context.DrawRectangle(BoolOf(element, "filled", true) ? Brush(ColorOf(element, "background", PanelColor), dim) : null, BoolOf(element, "showBorder", false) ? border : null, rect);
                    foreach (var child in box.Children) Draw(context, child, x, y);
                    break;
                case "Label":
                    DrawText(context, box.Text, x, y, ColorOf(element, "color", TextColor), dim);
                    break;
                case "Button":
                    context.DrawRectangle(Brush(Face, dim), border, rect);
                    DrawText(context, box.Text, x + (box.Width - CharWidth * box.Text.Length) / 2, y + (box.Height - CharHeight) / 2, TextColor, dim);
                    break;
                case "CheckBox":
                    var check = new Rect(x, y + (box.Height - 14) / 2, 14, 14);
                    context.DrawRectangle(Brush(InputBack, dim), border, check);
                    if (BoolOf(element, "isChecked", false)) context.DrawRectangle(Brush(Accent, dim), null, check.Deflate(3));
                    DrawText(context, box.Text, x + 14 + 6, y + (box.Height - CharHeight) / 2, TextColor, dim);
                    break;
                default: // TextBox
                    context.DrawRectangle(Brush(InputBack, dim), border, rect);
                    DrawText(context, box.Text, x + 4, y + (box.Height - CharHeight) / 2, box.Text.StartsWith('‹') ? TextDisabled : TextColor, dim);
                    break;
            }
        }
    }
}
