using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;

namespace fire.Editor
{
    /// <summary>Färbt Syntax-Hervorhebung (aus SyntaxHighlighter.Highlight)
    /// und Fehler-Unterkringelung (aus ScriptEditorControl.Diagnostics) beim
    /// Zeichnen ein - OHNE das Dokument selbst anzufassen. Das ist der
    /// zentrale Unterschied zur alten RichTextBox-Fassung (siehe deren
    /// ApplyHighlighting): dort musste bei JEDER Änderung das komplette
    /// FlowDocument neu gebaut und ausgetauscht werden (Quelle so ziemlich
    /// aller Bugs der letzten Runden - Selektion, Cursor, Scroll-Position,
    /// Wettlauf mit dem Tippen). AvalonEdits DocumentColorizingTransformer
    /// wird pro SICHTBARER Zeile beim Zeichnen aufgerufen und ändert nur die
    /// Darstellung (Vordergrundfarbe, Textdekoration) einzelner Zeichen-
    /// bereiche - das Dokument/der Text bleibt währenddessen unangetastet,
    /// Cursor/Auswahl/Scroll-Position sind davon architekturbedingt gar nicht
    /// erst betroffen.
    ///
    /// `Spans`/`ErrorLines` werden von ScriptEditorControl nach jedem
    /// (debounced) Neu-Lexen/-Analysieren ausgetauscht, gefolgt von einem
    /// TextView.Redraw() - DAS löst den nächsten Zeichen-Durchlauf aus, der
    /// wiederum ColorizeLine für die dann sichtbaren Zeilen aufruft.</summary>
    internal sealed class HighlightingColorizer : DocumentColorizingTransformer
    {
        /// <summary>Aktuelle Hervorhebungs-Spans, nach Start aufsteigend
        /// sortiert (so liefert sie SyntaxHighlighter.Highlight bereits,
        /// Tokens werden in Quelltext-Reihenfolge verarbeitet) - das erlaubt
        /// den frühen Abbruch unten (`span.Start >= lineEnd`), ohne für jede
        /// Zeile die komplette Liste durchsuchen zu müssen.</summary>
        public IReadOnlyList<HighlightSpan> Spans { get; set; } = Array.Empty<HighlightSpan>();

        /// <summary>1-basierte Zeilennummern mit mindestens einem gemeldeten
        /// Diagnostik-Fehler - komplett unterkringelt (keine Spalten-
        /// Information in Diagnostic vorhanden, siehe dortige Doku), analog
        /// zur alten RichTextBox-Fassung.</summary>
        public IReadOnlySet<int> ErrorLines { get; set; } = new HashSet<int>();

        protected override void ColorizeLine(ICSharpCode.AvalonEdit.Document.DocumentLine line)
        {
            int lineStart = line.Offset;
            int lineEnd = line.EndOffset;

            if (ErrorLines.Contains(line.LineNumber))
                ChangeLinePart(lineStart, lineEnd, el => el.TextRunProperties.SetTextDecorations(SquigglyDecorations));

            foreach (var span in Spans)
            {
                if (span.Start >= lineEnd) break; // sortiert - alles Weitere liegt noch später
                int spanEnd = span.Start + span.Length;
                if (spanEnd <= lineStart) continue;

                int start = Math.Max(span.Start, lineStart);
                int end = Math.Min(spanEnd, lineEnd);
                if (start >= end) continue;

                var brush = BrushFor(span.Category);
                ChangeLinePart(start, end, el => el.TextRunProperties.SetForegroundBrush(brush));
            }
        }

        internal static Brush BrushFor(HighlightCategory category) => category switch
        {
            HighlightCategory.Keyword => Brushes.MediumBlue,
            HighlightCategory.Type => Brushes.Teal,
            HighlightCategory.String => Brushes.DarkGreen,
            HighlightCategory.Char => Brushes.DarkGreen,
            HighlightCategory.Number => Brushes.DarkOrange,
            HighlightCategory.Comment => Brushes.Gray,
            HighlightCategory.Identifier => Brushes.Black,
            _ => Brushes.Black,
        };

        /// <summary>Wie die alte RichTextBox-Fassung (SquigglyDecorations
        /// dort) - eine ECHTE wellenförmige Unterkringelung statt einer
        /// geraden Linie: der Stift, der unterstreicht, nutzt selbst einen
        /// kleinen gekachelten Zickzack-Pinsel als "Farbe".</summary>
        private static readonly TextDecorationCollection SquigglyDecorations = CreateSquigglyDecorations();

        private static TextDecorationCollection CreateSquigglyDecorations()
        {
            var figure = new System.Windows.Media.PathFigure { StartPoint = new Point(0, 2) };
            figure.Segments.Add(new System.Windows.Media.LineSegment(new Point(1.5, 0), true));
            figure.Segments.Add(new System.Windows.Media.LineSegment(new Point(3, 2), true));
            var geometry = new System.Windows.Media.PathGeometry();
            geometry.Figures.Add(figure);

            var tile = new DrawingBrush(new GeometryDrawing(null, new Pen(Brushes.Red, 1), geometry))
            {
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, 3, 4),
                ViewportUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.None,
            };
            tile.Freeze();

            var pen = new Pen(tile, 3);
            pen.Freeze();

            var decoration = new TextDecoration
            {
                Location = TextDecorationLocation.Underline,
                Pen = pen,
                PenThicknessUnit = TextDecorationUnit.Pixel,
                PenOffset = 1,
                PenOffsetUnit = TextDecorationUnit.Pixel,
            };

            var collection = new TextDecorationCollection { decoration };
            collection.Freeze();
            return collection;
        }
    }

    /// <summary>Zeichnet den vollflächigen Zeilen-Hintergrund für die aktuell
    /// vom Debugger angehaltene Zeile (gelb) und für Haltepunkt-Zeilen (rot,
    /// blasser) - analog zu para.Background in der alten RichTextBox-
    /// Fassung, hier aber als eigene Zeichen-Ebene UNTER dem Text
    /// (KnownLayer.Background), statt Teil des Dokuments selbst zu sein.</summary>
    internal sealed class LineBackgroundRenderer : IBackgroundRenderer
    {
        private static readonly Brush CurrentLineBrush = new SolidColorBrush(Color.FromArgb(90, 255, 215, 0)).AsFrozen();
        private static readonly Brush BreakpointBrush = new SolidColorBrush(Color.FromArgb(60, 220, 20, 20)).AsFrozen();

        public int? HighlightedLine { get; set; }
        public IReadOnlySet<int> Breakpoints { get; set; } = new HashSet<int>();

        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (HighlightedLine == null && Breakpoints.Count == 0) return;
            if (!textView.VisualLinesValid) return;

            //double width = Math.Max(textView.ActualWidth, textView.);

            double width = textView.ActualWidth;

            foreach (var visualLine in textView.VisualLines)
            {
                int lineNumber = visualLine.FirstDocumentLine.LineNumber;
                Brush? brush = HighlightedLine == lineNumber ? CurrentLineBrush
                    : Breakpoints.Contains(lineNumber) ? BreakpointBrush
                    : null;
                if (brush == null) continue;

                double top = visualLine.VisualTop - textView.VerticalOffset;
                drawingContext.DrawRectangle(brush, null, new Rect(0, top, width, visualLine.Height));
            }
        }
    }

    /// <summary>Klickbarer Haltepunkt-Rand links vom Text (NEU gegenüber der
    /// alten RichTextBox-Fassung, die Haltepunkte nur über F9 im Host-Fenster
    /// setzen ließ, ganz ohne eigene Rand-Anzeige - mit AvalonEdit ist ein
    /// echter, anklickbarer Rand quasi kostenlos, das ist in praktisch jedem
    /// Code-Editor die erwartete Bedienung).</summary>
    internal sealed class BreakpointMargin : AbstractMargin
    {
        private const double MarginWidth = 18;

        public IReadOnlySet<int> Breakpoints { get; set; } = new HashSet<int>();

        /// <summary>Feuert mit der 1-basierten Zeilennummer, auf die geklickt
        /// wurde - der Host (ScriptEditorControl) entscheidet, was das
        /// bedeutet (Haltepunkt umschalten).</summary>
        public event Action<int>? LineClicked;

        protected override Size MeasureOverride(Size availableSize) => new(MarginWidth, 0);

        protected override void OnRender(DrawingContext drawingContext)
        {
            var textView = TextView;
            drawingContext.DrawRectangle(Brushes.WhiteSmoke, null, new Rect(0, 0, MarginWidth, RenderSize.Height));

            if (textView == null || !textView.VisualLinesValid) return;

            foreach (var visualLine in textView.VisualLines)
            {
                int lineNumber = visualLine.FirstDocumentLine.LineNumber;
                if (!Breakpoints.Contains(lineNumber)) continue;

                double y = visualLine.VisualTop - textView.VerticalOffset + visualLine.Height / 2;
                drawingContext.DrawEllipse(Brushes.Firebrick, null, new Point(MarginWidth / 2, y), 5, 5);
            }
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            var textView = TextView;
            if (textView != null)
            {
                var pos = e.GetPosition(this);
                var visualLine = textView.GetVisualLineFromVisualTop(pos.Y + textView.VerticalOffset);
                if (visualLine != null)
                {
                    LineClicked?.Invoke(visualLine.FirstDocumentLine.LineNumber);
                    e.Handled = true;
                }
            }
            base.OnMouseLeftButtonDown(e);
        }

        /// <summary>Vom Host nach jeder Haltepunkt-/Layout-Änderung
        /// aufzurufen - bewusst explizit statt über TextView-Ereignisse
        /// automatisch verdrahtet, um die Abhängigkeit von genauen Margin-
        /// Lifecycle-Ereignisnamen zu vermeiden (die sich zwischen AvalonEdit-
        /// Versionen schon mal verschoben haben).</summary>
        public void RedrawMargin() => InvalidateVisual();
    }

    internal static class BrushFreezeExtensions
    {
        public static Brush AsFrozen(this SolidColorBrush brush)
        {
            brush.Freeze();
            return brush;
        }
    }
}
