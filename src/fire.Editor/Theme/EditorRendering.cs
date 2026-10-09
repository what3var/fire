using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace fire.Editor
{
    /// <summary>Colours syntax highlighting (from SyntaxHighlighter.Highlight) when drawing - WITHOUT touching the document itself: the colorizer is called per VISIBLE line when drawing
    /// and changes only the presentation (foreground colour) of individual character ranges; cursor, selection and scroll position are not affected at all.
    ///
    /// `Spans` are swapped by ScriptEditorControl after every (debounced) re-lexing, followed by a TextView.Redraw() - THAT triggers the next drawing pass,
    /// which in turn calls ColorizeLine for the lines then visible. The error squiggles are drawn by <see cref="ErrorSquiggleRenderer"/>.</summary>
    internal sealed class HighlightingColorizer : DocumentColorizingTransformer
    {
        /// <summary>Current highlighting spans, sorted ascending by start (as SyntaxHighlighter.Highlight already delivers them) - this allows the early exit below.</summary>
        public IReadOnlyList<HighlightSpan> Spans { get; set; } = Array.Empty<HighlightSpan>();

        protected override void ColorizeLine(DocumentLine line)
        {
            int lineStart = line.Offset;
            int lineEnd = line.EndOffset;

            foreach (var span in Spans)
            {
                if (span.Start >= lineEnd) break; // sorted - everything further lies even later
                int spanEnd = span.Start + span.Length;
                if (spanEnd <= lineStart) continue;

                int start = Math.Max(span.Start, lineStart);
                int end = Math.Min(spanEnd, lineEnd);
                if (start >= end) continue;

                var brush = BrushFor(span.Category);
                ChangeLinePart(start, end, el => el.TextRunProperties.SetForegroundBrush(brush));
            }
        }

        internal static IBrush BrushFor(HighlightCategory category) => category switch
        {
            HighlightCategory.Keyword => EditorTheme.Magenta,
            HighlightCategory.Type => EditorTheme.Purple,
            HighlightCategory.String => EditorTheme.Yellow,
            HighlightCategory.Char => EditorTheme.WineRed,
            HighlightCategory.Number => EditorTheme.Orange,
            HighlightCategory.Comment => EditorTheme.Comment,
            HighlightCategory.Inactive => EditorTheme.Comment,
            HighlightCategory.Identifier => EditorTheme.Text,
            _ => EditorTheme.Text,
        };
    }

    /// <summary>Squiggles the lines for which live diagnostics reports an error (whole line: Diagnostic knows no column): a red wavy line under the text of the line.</summary>
    internal sealed class ErrorSquiggleRenderer : IBackgroundRenderer
    {
        private static readonly IPen Pen = new Pen(EditorTheme.ErrorMark, 1);

        /// <summary>1-based line numbers with at least one reported diagnostics error.</summary>
        public IReadOnlySet<int> ErrorLines { get; set; } = new HashSet<int>();

        public KnownLayer Layer => KnownLayer.Selection;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (ErrorLines.Count == 0 || textView.Document == null || !textView.VisualLinesValid) return;
            
            foreach (var visualLine in textView.VisualLines)
            {
                var documentLine = visualLine.FirstDocumentLine;
                if (!ErrorLines.Contains(documentLine.LineNumber)) continue;

                // from the first character that is not white space to the end of the line
                string text = textView.Document.GetText(documentLine.Offset, documentLine.Length);
                int first = 0;
                while (first < text.Length && char.IsWhiteSpace(text[first])) first++;
                if (first >= text.Length) continue;
                var segment = new TextSegment { StartOffset = documentLine.Offset + first, EndOffset = documentLine.EndOffset };
                foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
                    DrawWave(drawingContext, rect.Left, rect.Right, rect.Bottom - 1);
            }
        }

        /// <summary>A zig-zag line (period 4 pixels, height 2) from x1 to x2 at the height y.</summary>
        private static void DrawWave(DrawingContext context, double x1, double x2, double y)
        {
            if (x2 <= x1) return;
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(new Point(x1, y), false);
                bool up = true;
                for (double x = x1 + 2; x < x2 + 2; x += 2)
                {
                    g.LineTo(new Point(Math.Min(x, x2), up ? y - 2 : y));
                    up = !up;
                }
                g.EndFigure(false);
            }
            context.DrawGeometry(null, Pen, geometry);
        }
    }

    /// <summary>Draws the full-width line background for the line currently halted by the debugger (yellow) and for breakpoint lines (red, paler) as a drawing layer of its own
    /// BELOW the text (KnownLayer.Background).</summary>
    internal sealed class LineBackgroundRenderer : IBackgroundRenderer
    {
        private static readonly IBrush CurrentLineBrush = EditorTheme.CurrentDebugLine;
        private static readonly IBrush BreakpointBrush = EditorTheme.BreakpointLine;

        public int? HighlightedLine { get; set; }
        public IReadOnlySet<int> Breakpoints { get; set; } = new HashSet<int>();

        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (HighlightedLine == null && Breakpoints.Count == 0) return;
            if (!textView.VisualLinesValid) return;

            double width = textView.Bounds.Width;

            foreach (var visualLine in textView.VisualLines)
            {
                int lineNumber = visualLine.FirstDocumentLine.LineNumber;
                IBrush? brush = HighlightedLine == lineNumber ? CurrentLineBrush
                    : Breakpoints.Contains(lineNumber) ? BreakpointBrush
                    : null;
                if (brush == null) continue;

                double top = visualLine.VisualTop - textView.VerticalOffset;
                drawingContext.DrawRectangle(brush, null, new Rect(0, top, width, visualLine.Height));
            }
        }
    }

    /// <summary>Clickable breakpoint margin to the left of the text: a click on a line toggles its breakpoint.</summary>
    internal sealed class BreakpointMargin : AbstractMargin
    {
        private const double MarginWidth = 18;

        public IReadOnlySet<int> Breakpoints { get; set; } = new HashSet<int>();

        /// <summary>Fires with the 1-based line number that was clicked - the host (ScriptEditorControl) decides what that means (toggle breakpoint).</summary>
        public event Action<int>? LineClicked;

        protected override Size MeasureOverride(Size availableSize) => new(MarginWidth, 0);

        public override void Render(DrawingContext drawingContext)
        {
            var textView = TextView;
            drawingContext.DrawRectangle(EditorTheme.Background, null, new Rect(0, 0, MarginWidth, Bounds.Height));

            if (textView == null || !textView.VisualLinesValid) return;

            foreach (var visualLine in textView.VisualLines)
            {
                int lineNumber = visualLine.FirstDocumentLine.LineNumber;
                if (!Breakpoints.Contains(lineNumber)) continue;

                double y = visualLine.VisualTop - textView.VerticalOffset + visualLine.Height / 2;
                drawingContext.DrawEllipse(EditorTheme.BreakpointDot, null, new Point(MarginWidth / 2, y), 5, 5);
            }
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            var textView = TextView;
            if (textView != null && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                var pos = e.GetPosition(this);
                var visualLine = textView.GetVisualLineFromVisualTop(pos.Y + textView.VerticalOffset);
                if (visualLine != null)
                {
                    LineClicked?.Invoke(visualLine.FirstDocumentLine.LineNumber);
                    e.Handled = true;
                }
            }
            base.OnPointerPressed(e);
        }

        /// <summary>To be called by the host after every breakpoint/layout change.</summary>
        public void RedrawMargin() => InvalidateVisual();
    }
}
