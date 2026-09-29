using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace fire.Editor
{
    /// <summary>
    /// Schlankes, schreibgeschütztes Popup-Fenster zum Anzeigen EINER Datei -
    /// für "zu Definition/Include springen" (siehe MainWindow.NavigateAt),
    /// wenn das Ziel NICHT im Haupt-Editor-Dokument selbst liegt. Bewusst
    /// eine eigene, EINFACHERE Fassung von Syntax-Highlighting/Navigation
    /// statt eine Wiederverwendung der MainWindow-Logik (die eng an das
    /// EINE, bearbeitbare Hauptdokument samt Debugger/Haltepunkten gekoppelt
    /// ist) - ein Popup braucht davon nichts, nur Lesen + Weiterspringen.
    /// Springt der Nutzer von HIER aus per Strg+Klick weiter (z.B. eine
    /// includierte Datei, die selbst wieder etwas includiert), öffnet das
    /// ein WEITERES Popup - keine Rückwärtsnavigation/Verlauf in dieser
    /// Ausbaustufe (bewusste Vereinfachung).
    /// </summary>
    public partial class FileViewerWindow : Window
    {
        private string _filePath = "";
        private string _source = "";
        private int? _highlightedLine;

        public FileViewerWindow()
        {
            InitializeComponent();
        }

        /// <summary>Lädt `filePath` und zeigt es an, optional direkt zu
        /// `jumpToLine` (1-basiert) gescrollt und dezent hervorgehoben.
        /// Kann NICHT gelesen werden (Datei fehlt, kein Zugriff, ...) -
        /// zeigt eine Fehlermeldung als Inhalt an, statt das Fenster gar
        /// nicht erst zu öffnen (der Nutzer soll sehen, WAS schiefging, statt
        /// sich zu fragen, warum der Klick nichts getan hat).</summary>
        public void LoadFile(string filePath, int? jumpToLine = null)
        {
            _filePath = filePath;
            Title = $"Datei ansehen - {Path.GetFileName(filePath)}";
            PathText.Text = filePath;

            string source;
            try
            {
                source = File.ReadAllText(filePath);
            }
            catch (Exception ex)
            {
                source = $"// Datei konnte nicht geöffnet werden:\n// {filePath}\n// {ex.Message}";
            }

            DisplaySource(source, jumpToLine);
        }

        /// <summary>Wie LoadFile, aber für Quelltext OHNE echten Dateipfad
        /// (z.B. die eingebaute Standardbibliothek, siehe MainWindow.
        /// ShowPreludeSource) - `title` steht direkt im Fenstertitel/der
        /// Pfad-Zeile statt eines Dateinamens. Strg+Klick-Navigation
        /// INNERHALB dieser Ansicht funktioniert identisch zu LoadFile;
        /// ein etwaiges `#include` darin würde (mangels echtem Verzeichnis)
        /// relativ zum aktuellen Arbeitsverzeichnis aufgelöst - genau wie
        /// beim ECHTEN Kompilieren der Prelude selbst (siehe Runtime.
        /// RuntimeSession.Build), also konsistent zum tatsächlichen Verhalten.</summary>
        public void LoadSource(string title, string source, int? jumpToLine = null)
        {
            _filePath = "";
            Title = title;
            PathText.Text = title;
            DisplaySource(source, jumpToLine);
        }

        private void DisplaySource(string source, int? jumpToLine)
        {
            _source = source;
            _highlightedLine = jumpToLine;
            ApplyHighlighting();
            if (jumpToLine.HasValue)
                Dispatcher.BeginInvoke(new Action(() => ScrollToLine(jumpToLine.Value)),
                    System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void ApplyHighlighting()
        {
            var spans = SyntaxHighlighter.Highlight(_source);
            var doc = new FlowDocument();
            var lines = _source.Split('\n');
            int lineStart = 0;
            int lineNumber = 0;

            foreach (var line in lines)
            {
                lineNumber++;
                int lineEnd = lineStart + line.Length;
                var para = new Paragraph { Margin = new Thickness(0) };

                int pos = lineStart;
                foreach (var span in spans.Where(s => s.Start < lineEnd && s.Start + s.Length > lineStart)
                                           .OrderBy(s => s.Start))
                {
                    int spanStart = Math.Max(span.Start, lineStart);
                    int spanEnd = Math.Min(span.Start + span.Length, lineEnd);
                    if (spanStart > pos)
                        para.Inlines.Add(new Run(_source.Substring(pos, spanStart - pos)));
                    para.Inlines.Add(new Run(_source.Substring(spanStart, spanEnd - spanStart))
                    {
                        Foreground = BrushFor(span.Category),
                    });
                    pos = spanEnd;
                }
                if (pos < lineEnd)
                    para.Inlines.Add(new Run(_source.Substring(pos, lineEnd - pos)));
                if (para.Inlines.Count == 0)
                    para.Inlines.Add(new Run(string.Empty));

                if (_highlightedLine == lineNumber)
                    para.Background = new SolidColorBrush(Color.FromArgb(90, 255, 215, 0));

                doc.Blocks.Add(para);
                lineStart = lineEnd + 1;
            }

            Viewer.Document = doc;
        }

        private static Brush BrushFor(HighlightCategory category) => category switch
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

        private void ScrollToLine(int line)
        {
            var blocks = Viewer.Document.Blocks.OfType<Paragraph>().ToList();
            if (line >= 1 && line <= blocks.Count)
                blocks[line - 1].BringIntoView();
        }

        private int GetOffsetOf(TextPointer pointer) =>
            new TextRange(Viewer.Document.ContentStart, pointer).Text.Replace("\r\n", "\n").Length;

        private void Viewer_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Nur Strg+Klick navigiert (siehe MainWindow.Editor_PreviewMouseLeftButtonDown
            // für dieselbe Begründung) - ein normaler Klick muss weiterhin
            // ganz gewöhnlich den Cursor setzen/Text markieren können.
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;

            var pos = Viewer.GetPositionFromPoint(e.GetPosition(Viewer), snapToText: true);
            if (pos == null) return;

            int offset = GetOffsetOf(pos);
            var index = ScriptSymbolIndex.Build(_source);
            var target = NavigationEngine.TryResolve(_source, offset, index);
            if (target == null) return;

            e.Handled = true;

            if (target.IsPrelude)
            {
                var preludeViewer = new FileViewerWindow();
                preludeViewer.LoadSource("Standardbibliothek (Prelude)", fire.Standard.Prelude.Source, target.Line);
                preludeViewer.Show();
                return;
            }

            if (target.FilePath == null)
            {
                _highlightedLine = target.Line;
                ApplyHighlighting();
                ScrollToLine(target.Line);
                return;
            }

            string? dir = Path.GetDirectoryName(_filePath);
            string resolved = Path.GetFullPath(Path.Combine(dir ?? ".", target.FilePath));
            var viewer = new FileViewerWindow();
            viewer.LoadFile(resolved, target.Line);
            viewer.Show();
        }
    }
}
