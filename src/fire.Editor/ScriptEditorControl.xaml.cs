using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace fire.Editor
{
    /// <summary>Ein eigenständiges, wiederverwendbares Editor-Control:
    /// RichTextBox + Syntax-Highlighting + Autovervollständigung + Strg+Klick-
    /// Navigation + Haltepunkt-Rand + Live-Diagnostik - GENAU EIN Quelltext
    /// pro Instanz. Sowohl vom alten Einzeldatei-Fenster (MainWindow, eine
    /// Instanz) als auch vom neuen Tabbed-Projekt-Fenster (ProjectWindow, eine
    /// Instanz PRO offenem Tab) genutzt, damit Editing-Logik nicht zweimal
    /// gepflegt werden muss.
    ///
    /// Bewusst KEINE eigene Kenntnis von DebugSession/Kompilieren/Ausführen -
    /// das bleibt Sache des jeweiligen Host-Fensters (siehe MainWindow/
    /// ProjectWindow), das diese Instanz orchestriert (Breakpoints abfragen,
    /// HighlightedLine setzen, GetText() beim Kompilieren aufrufen, ...).</summary>
    public partial class ScriptEditorControl : UserControl
    {
        /// <summary>Der Dateipfad dieses Editors, falls schon einmal
        /// gespeichert/geöffnet - `null` für ein neues, ungespeichertes
        /// Dokument. Nur für relative `#include`-Pfadauflösung bei der
        /// Strg+Klick-Navigation gebraucht (siehe TryResolveAcrossIncludes/
        /// OpenFileViewer) - das Control selbst liest/schreibt NIE
        /// eigenständig von/auf die Platte, das bleibt Sache des Host-
        /// Fensters (siehe GetText/SetText).</summary>
        public string? FilePath { get; set; }

        private readonly HashSet<int> _breakpoints = new();

        /// <summary>Die aktuellen Haltepunkt-Zeilen (1-basiert, wie im
        /// restlichen Editor) - nur LESEND; zum Ändern ToggleBreakpointAtCaret/
        /// ClearBreakpoints/SetBreakpoints nutzen (löst dabei automatisch
        /// BreakpointsChanged aus und aktualisiert den Rand).</summary>
        public IReadOnlySet<int> Breakpoints => _breakpoints;

        /// <summary>Feuert, wann immer sich die Haltepunkt-Menge geändert hat
        /// (Rand-Klick/ToggleBreakpointAtCaret/ClearBreakpoints/
        /// SetBreakpoints) - der Host muss darauf i.d.R. mit DebugSession.
        /// UpdateBreakpoints reagieren.</summary>
        public event Action? BreakpointsChanged;

        /// <summary>Die aktuell im Editor angezeigten Live-Diagnostik-Fehler
        /// (siehe LiveDiagnostics) - nur LESEND, wird intern debounced nach
        /// jeder Textänderung neu berechnet (siehe RunDiagnostics).</summary>
        public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

        /// <summary>Wie die Diagnostik berechnet wird (siehe RunDiagnostics) -
        /// Default: einfache Einzeldatei-Analyse (LiveDiagnostics.Analyze),
        /// passend für ein einzelnes, freistehendes Dokument (siehe
        /// MainWindow). Ein Host mit mehreren zusammengehörigen Dateien
        /// (siehe ProjectWindow) setzt hier stattdessen eine projektweite
        /// Variante (LiveDiagnostics.AnalyzeInProject), damit eine gültige
        /// Referenz auf eine Klasse aus einer ANDEREN Projektdatei nicht
        /// fälschlich als Fehler markiert wird.</summary>
        public Func<string, List<Diagnostic>> DiagnosticsProvider { get; set; } = LiveDiagnostics.Analyze;

        /// <summary>Feuert, wann immer sich Diagnostics geändert hat - der
        /// Host zeigt das i.d.R. in einer eigenen Fehlerliste an.</summary>
        public event Action? DiagnosticsChanged;

        /// <summary>Feuert bei JEDER Cursor-Bewegung mit der neuen 1-basierten
        /// Zeile - für eine Statusleisten-Anzeige im Host.</summary>
        public event Action<int>? CaretLineChanged;

        // Verhindert, dass ApplyHighlighting()s eigenes Neuaufbauen des
        // FlowDocument (das TextChanged auslöst) eine weitere Highlighting-
        // Runde anstößt - sonst Endlos-Rekursion.
        private bool _suppressTextChanged;

        // Highlighting läuft debounced (statt bei JEDEM Tastendruck) - sonst
        // würde das komplette Neuaufbauen des FlowDocument bei schnellem
        // Tippen spürbar ruckeln.
        private readonly DispatcherTimer _highlightTimer;

        /// <summary>Die aktuell per Debugger angehaltene Zeile (gelb
        /// hervorgehoben), `null` wenn keine - vom Host gesetzt (siehe
        /// MainWindow/ProjectWindow nach jedem Schritt/Stop).</summary>
        public int? HighlightedLine
        {
            get => _highlightedLine;
            set { _highlightedLine = value; ApplyHighlighting(); }
        }
        private int? _highlightedLine;

        // Live-Fehleranalyse (siehe LiveDiagnostics) - läuft debounced wie
        // das Highlighting, aber mit einer LÄNGEREN Verzögerung (Parser +
        // Resolver + Compiler sind spürbar teurer als reines Lexen) und
        // GETRENNT davon, damit schnelles Tippen nicht bei jedem Zwischen-
        // zustand einen vollständigen Kompilierversuch auslöst.
        private readonly DispatcherTimer _diagnosticsTimer;
        private List<Diagnostic> _diagnostics = new();

        // Die Vorschläge, die GERADE im CompletionPopup angezeigt werden -
        // Index in CompletionList.SelectedIndex zeigt hierauf.
        private List<CompletionItem> _completionItems = new();

        public ScriptEditorControl()
        {
            InitializeComponent();

            _highlightTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _highlightTimer.Tick += (_, _) =>
            {
                _highlightTimer.Stop();
                ApplyHighlighting();
            };

            _diagnosticsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _diagnosticsTimer.Tick += (_, _) =>
            {
                _diagnosticsTimer.Stop();
                RunDiagnostics();
            };

            // Highlighting wird während das Popup offen ist bewusst
            // ausgesetzt (siehe Editor_TextChanged) - hier nachholen, sobald
            // es schließt (egal ob durch Übernahme, Escape oder Fokusverlust).
            CompletionPopup.Closed += (_, _) =>
            {
                _highlightTimer.Stop();
                _highlightTimer.Start();
            };

            SetText(string.Empty);
        }

        // -----------------------------------------------------------
        // Text-Zugriff (RichTextBox <-> reiner String)
        //
        // Bewusste Design-Entscheidung: EIN Paragraph pro Quelltextzeile
        // (kein Zeilenumbruch, kein automatischer Wortumbruch innerhalb einer
        // Zeile) - dadurch entspricht "Zeile N im Editor" IMMER exakt
        // "Zeile N im Sinn von Chunk.MarkLine/GetLocation", ohne dass
        // Wortumbruch die Zuordnung durcheinanderbringen könnte.
        // -----------------------------------------------------------

        public string GetText()
        {
            // Bewusst direkt aus den Absätzen zusammengesetzt, statt über
            // TextRange(ContentStart, ContentEnd).Text + TrimEnd('\n') zu
            // raten, wie viele trailing Newlines WPF selbst hinzufügt - das
            // hatte einen echten Bug: TrimEnd('\n') entfernt ALLE trailing
            // Newlines, nicht nur ein synthetisches letztes. Ein frisch per
            // Enter angehängter LEERER Absatz am Dokumentende wurde dadurch
            // beim Zurückwandeln in Text komplett verschluckt - das Dokument
            // hatte beim nächsten Neuaufbau eine Zeile WENIGER als der
            // Nutzer gerade eingegeben hatte, und der Cursor (der auf die
            // inzwischen "verschwundene" Zeile zeigte) landete auf der Zeile
            // darüber. Da ein Paragraph hier IMMER genau einer Quelltextzeile
            // entspricht (siehe Kommentar oben), ist ein einfaches Join mit
            // '\n' exakt richtig, ohne jede Rate-Logik.
            var lines = Editor.Document.Blocks.OfType<Paragraph>()
                .Select(p => new TextRange(p.ContentStart, p.ContentEnd).Text.Replace("\r\n", "\n").TrimEnd('\n'));
            return string.Join("\n", lines);
        }

        public void SetText(string text)
        {
            _suppressTextChanged = true;
            var doc = new FlowDocument();
            foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
                doc.Blocks.Add(new Paragraph(new Run(line)) { Margin = new Thickness(0) });
            Editor.Document = doc;
            _suppressTextChanged = false;
            ApplyHighlighting();

            // TextChanged wird während des obigen Aufbaus unterdrückt (siehe
            // _suppressTextChanged) - die Diagnostik würde hier also sonst
            // NIE angestoßen, bis der Nutzer selbst das erste Mal tippt.
            _diagnosticsTimer.Stop();
            _diagnosticsTimer.Start();
        }

        /// <summary>Setzt den Editor-Text zurück und verwirft dabei auch
        /// Haltepunkte/Diagnostik/Hervorhebung - für "neue Datei"/"andere
        /// Datei geöffnet" im Host (anders als SetText, das z.B. auch bei
        /// der Autovervollständigung für ein und dasselbe Dokument genutzt
        /// wird und dort bewusst NICHTS von alldem verwirft).</summary>
        public void ResetTo(string text, string? filePath)
        {
            FilePath = filePath;
            _breakpoints.Clear();
            _diagnostics = new List<Diagnostic>();
            _highlightedLine = null;
            SetText(text);
            BreakpointsChanged?.Invoke();
            DiagnosticsChanged?.Invoke();
        }

        readonly IEnumerable<char> lastChars = new List<char>() { ' ', '.', '(', ';', ':', '!', '\t', '\r', '\n', '{' };

        private void Editor_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressTextChanged) return;

            // Diagnostik läuft UNABHÄNGIG vom Popup-Zustand debounced weiter
            // (löst selbst keinen Dokument-Neuaufbau aus, siehe
            // RunDiagnostics - erst das anschließende ApplyHighlighting tut
            // das, und das respektiert die Popup-Sperre bereits).
            _diagnosticsTimer.Stop();
            _diagnosticsTimer.Start();

            string source = GetText();
            int offset = GetOffsetOf(Editor.CaretPosition);
            bool popupRelevant = CompletionPopup.IsOpen
                || (offset >= 0 && offset <= source.Length && (offset == 0 || lastChars.Contains(source[offset - 1])));

            if (popupRelevant)
            {
                // Solange das Popup aktiv ist (oder gerade durch ein '.'
                // ausgelöst wird), KEINEN Highlighting-Neuaufbau des
                // Dokuments zulassen - der ersetzt Editor.Document komplett
                // und reißt damit ein gerade geöffnetes, an den Editor
                // "angehängtes" Popup sofort wieder ein. Highlighting holt
                // automatisch nach, sobald das Popup wieder schließt (siehe
                // CompletionPopup.Closed im Konstruktor). Die Positionierung
                // wird zusätzlich einen Dispatcher-Tick verzögert, damit das
                // Layout der GERADE getippten Änderung sicher fertig ist,
                // bevor die Caret-Rechteck-Position abgefragt wird.
                _highlightTimer.Stop();
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    string src = GetText();
                    int off = GetOffsetOf(Editor.CaretPosition);
                    ShowOrUpdateCompletion(src, off, closeIfEmpty: true);
                }), DispatcherPriority.Background);
            }
            else
            {
                _highlightTimer.Stop();
                _highlightTimer.Start();
            }
        }

        private void Editor_SelectionChanged(object sender, RoutedEventArgs e) =>
            CaretLineChanged?.Invoke(GetCaretLine());

        // -----------------------------------------------------------
        // Autovervollständigung (IntelliSense) - siehe ScriptSymbolIndex/
        // CompletionEngine für die eigentliche Logik, hier nur die UI-
        // Anbindung (Popup zeigen/filtern/positionieren, Tastatur-Steuerung,
        // Einfügen der Auswahl).
        // -----------------------------------------------------------

        private void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (CompletionPopup.IsOpen)
            {
                switch (e.Key)
                {
                    case Key.Down:
                        if (CompletionList.Items.Count > 0)
                            CompletionList.SelectedIndex = Math.Min(CompletionList.SelectedIndex + 1, CompletionList.Items.Count - 1);
                        e.Handled = true;
                        return;
                    case Key.Up:
                        if (CompletionList.Items.Count > 0)
                            CompletionList.SelectedIndex = Math.Max(CompletionList.SelectedIndex - 1, 0);
                        e.Handled = true;
                        return;
                    case Key.Enter:
                    case Key.Tab:
                        AcceptCompletion();
                        e.Handled = true;
                        return;
                    case Key.Escape:
                        CompletionPopup.IsOpen = false;
                        e.Handled = true;
                        return;
                }
                return;
            }

            // Strg+Leertaste: Vervollständigung manuell anstoßen, auch ohne
            // vorangehenden '.' (allgemeine Bezeichner-Vervollständigung).
            if (e.Key == Key.Space && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                string source = GetText();
                int offset = GetOffsetOf(Editor.CaretPosition);
                ShowOrUpdateCompletion(source, offset, closeIfEmpty: true);
                e.Handled = true;
            }
        }

        private void ShowOrUpdateCompletion(string source, int offset, bool closeIfEmpty)
        {
            var index = ScriptSymbolIndex.Build(source);
            var items = CompletionEngine.GetSuggestions(source, offset, index);

            if (items.Count == 0)
            {
                if (closeIfEmpty) CompletionPopup.IsOpen = false;
                return;
            }

            items.OrderByDescending(i => i.Score);

            Debug.WriteLine(items[0].Display + " (Score: " + items[0].Score + ")");
            _completionItems = items;
            CompletionList.ItemsSource = items.Select(i => i.Display).ToList();

            if (items[0].Score > 0.6f)
                CompletionList.SelectedIndex = 0;
            else
                CompletionList.SelectedIndex = -1;

            PositionCompletionPopup();
            CompletionPopup.IsOpen = true;
        }

        private void PositionCompletionPopup()
        {
            var caretRect = Editor.CaretPosition.GetCharacterRect(LogicalDirection.Forward);
            CompletionPopup.Placement = PlacementMode.RelativePoint;
            CompletionPopup.HorizontalOffset = caretRect.Left;
            CompletionPopup.VerticalOffset = caretRect.Bottom + 2;
        }

        private void CompletionList_MouseDoubleClick(object sender, MouseButtonEventArgs e) =>
            AcceptCompletion();

        /// <summary>Fügt den ausgewählten Vorschlag ein - ersetzt dabei das
        /// bereits getippte Präfix (Bezeichner-Zeichen unmittelbar vor dem
        /// Cursor) durch den vollen Vorschlagstext.</summary>
        private void AcceptCompletion()
        {
            if (_completionItems.Count == 0 || CompletionList.SelectedIndex < 0)
            {
                CompletionPopup.IsOpen = false;
                return;
            }

            var item = _completionItems[CompletionList.SelectedIndex];
            CompletionPopup.IsOpen = false;

            string source = GetText();
            int offset = GetOffsetOf(Editor.CaretPosition);

            int start = offset - 1;
            while (start >= 0 && (char.IsLetterOrDigit(source[start]) || source[start] == '_')) start--;
            start++;

            string newSource = source.Substring(0, start) + item.Text + source.Substring(Math.Min(offset, source.Length));
            int newCaretOffset = start + item.Text.Length;
            var (newLine, newColumn) = OffsetToLineColumn(newSource, newCaretOffset);

            SetText(newSource);
            SetCaretByLineColumn(newLine, newColumn);
            Editor.Focus();
        }

        /// <summary>Reine String-Umrechnung Offset -> (Zeile, Spalte), ohne
        /// WPF-Beteiligung - zuverlässig, im Gegensatz zur direkten
        /// TextPointer-Offset-Umrechnung (siehe GetOffsetOf-Kommentar).</summary>
        private static (int Line, int Column) OffsetToLineColumn(string text, int offset)
        {
            int line = 0, col = 0;
            int end = Math.Min(offset, text.Length);
            for (int i = 0; i < end; i++)
            {
                if (text[i] == '\n') { line++; col = 0; }
                else col++;
            }
            return (line, col);
        }

        public int GetCaretLine()
        {
            var para = Editor.CaretPosition.Paragraph;
            if (para == null) return 1;
            var blocks = Editor.Document.Blocks.OfType<Paragraph>().ToList();
            int index = blocks.IndexOf(para);
            return index < 0 ? 1 : index + 1;
        }

        // -----------------------------------------------------------
        // Syntax-Highlighting (debounced, Cursor-Position wird über Zeile+
        // Spalte statt Gesamt-Offset erhalten - siehe GetCaretLineColumn/
        // SetCaretByLineColumn weiter unten)
        // -----------------------------------------------------------

        private void ApplyHighlighting()
        {
            string text = GetText();
            var (caretLine, caretColumn) = GetCaretLineColumn();

            var spans = SyntaxHighlighter.Highlight(text);
            var errorLines = _diagnostics.Select(d => d.Line).ToHashSet();

            _suppressTextChanged = true;
            var doc = new FlowDocument();
            var lines = text.Split('\n');
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
                        para.Inlines.Add(new Run(text.Substring(pos, spanStart - pos)));
                    para.Inlines.Add(new Run(text.Substring(spanStart, spanEnd - spanStart))
                    {
                        Foreground = BrushFor(span.Category),
                    });
                    pos = spanEnd;
                }
                if (pos < lineEnd)
                    para.Inlines.Add(new Run(text.Substring(pos, lineEnd - pos)));
                if (para.Inlines.Count == 0)
                    para.Inlines.Add(new Run(string.Empty));

                if (_highlightedLine == lineNumber)
                    para.Background = new SolidColorBrush(Color.FromArgb(90, 255, 215, 0));
                else if (_breakpoints.Contains(lineNumber))
                    para.Background = new SolidColorBrush(Color.FromArgb(60, 220, 20, 20));

                // Live-Diagnostik (siehe LiveDiagnostics/RunDiagnostics) -
                // jede Zeile mit einem gemeldeten Fehler wird komplett
                // unterkringelt (keine Spalten-Information vorhanden, siehe
                // Diagnostic-Doku, deshalb die GANZE Zeile statt eines
                // genauen Bereichs).
                if (errorLines.Contains(lineNumber))
                    foreach (var run in para.Inlines.OfType<Run>())
                        run.TextDecorations = SquigglyDecorations;

                doc.Blocks.Add(para);
                lineStart = lineEnd + 1; // '+1' für den übersprungenen '\n'
            }

            Editor.Document = doc;
            SetCaretByLineColumn(caretLine, caretColumn);
            _suppressTextChanged = false;
        }

        /// <summary>Läuft debounced nach Textänderungen (siehe
        /// Editor_TextChanged/_diagnosticsTimer): Parser+Resolver+Compiler
        /// auf dem aktuellen Editor-Inhalt (siehe LiveDiagnostics.Analyze),
        /// aktualisiert Diagnostics (löst DiagnosticsChanged aus) und (falls
        /// das Vervollständigungs-Popup nicht gerade offen ist, siehe
        /// ApplyHighlighting-Aufrufe an anderer Stelle für die Begründung)
        /// die unterkringelten Zeilen im Editor selbst.</summary>
        private void RunDiagnostics()
        {
            string source = GetText();
            _diagnostics = DiagnosticsProvider(source);
            DiagnosticsChanged?.Invoke();
            if (!CompletionPopup.IsOpen)
                ApplyHighlighting();
        }

        /// <summary>Eine ECHTE wellenförmige Unterkringelung (nicht nur eine
        /// gerade rote Linie - WPFs eingebaute TextDecorations kennen von
        /// Haus aus nur gerade Linien): der Pen, der die Unterstreichung
        /// zeichnet, benutzt selbst einen kleinen, gekachelten Zickzack-
        /// Pinsel als seine "Farbe" statt einer schlichten SolidColorBrush -
        /// dadurch besteht die resultierende Linie optisch aus vielen
        /// kleinen Dreieckswellen hintereinander.</summary>
        private static readonly TextDecorationCollection SquigglyDecorations = CreateSquigglyDecorations();

        private static TextDecorationCollection CreateSquigglyDecorations()
        {
            var figure = new PathFigure { StartPoint = new Point(0, 2) };
            figure.Segments.Add(new LineSegment(new Point(1.5, 0), true));
            figure.Segments.Add(new LineSegment(new Point(3, 2), true));
            var geometry = new PathGeometry();
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

        /// <summary>Ungefährer Zeichen-Offset einer TextPointer-Position
        /// relativ zum GESAMTEN Dokument - nur für die Umwandlung IN einen
        /// Klartext-Offset gedacht (z.B. für die Autovervollständigung, die
        /// mit reinen String-Positionen arbeitet). NICHT für die
        /// Wiederherstellung einer Caret-Position nach einem Dokument-
        /// Neuaufbau verwenden - siehe GetCaretLineColumn/SetCaretByLineColumn
        /// dafür (WPFs TextPointer.GetPositionAtOffset zählt über
        /// Absatzgrenzen hinweg NICHT wie reine Zeichen, das würde sich mit
        /// jeder Zeile vor dem Cursor stärker aufschaukeln - genau das
        /// Cursor-"Springen", das diese Methode hier zwar lesen, aber nicht
        /// zuverlässig rückgängig machen kann).</summary>
        private int GetOffsetOf(TextPointer pointer) =>
            new TextRange(Editor.Document.ContentStart, pointer).Text.Replace("\r\n", "\n").Length;

        // -----------------------------------------------------------
        // Strg+Klick-Navigation zu Definitionen/Includes (siehe
        // NavigationEngine für die eigentliche Auflösung, FileViewerWindow
        // für die Anzeige einer ANDEREN Datei).
        // -----------------------------------------------------------

        private void Editor_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Nur Strg+Klick navigiert - ein normaler Klick muss weiterhin
            // ganz gewöhnlich den Cursor setzen/Text markieren können, ohne
            // versehentlich wegzuspringen.
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;

            var pos = Editor.GetPositionFromPoint(e.GetPosition(Editor), snapToText: true);
            if (pos == null) return;

            int offset = GetOffsetOf(pos);
            string source = GetText();
            var index = ScriptSymbolIndex.Build(source);

            var target = NavigationEngine.TryResolve(source, offset, index)
                ?? TryResolveAcrossIncludes(source, offset, index);
            if (target == null) return;

            e.Handled = true;

            if (target.IsPrelude)
            {
                ShowPreludeSource(target.Line);
                return;
            }

            if (target.FilePath == null)
            {
                SetCaretByLineColumn(target.Line - 1, 0);
                ScrollToLine(target.Line);
                Editor.Focus();
                return;
            }

            OpenFileViewer(target.FilePath, target.Line);
        }

        /// <summary>Zeigt die eingebaute Standardbibliothek (Prelude, siehe
        /// fire.Standard.Prelude/ScriptSymbolIndex.MergeInPrelude) in
        /// einem schreibgeschützten FileViewerWindow-Popup an, zu `line`
        /// gescrollt - für "zu Definition springen" auf `List`/
        /// `IEnumerable`/etc., die NICHT im aktuellen Dokument selbst
        /// stehen (siehe NavigationTarget.IsPrelude). Kein echter Dateipfad
        /// vorhanden (siehe FileViewerWindow.LoadSource), deshalb ein
        /// eigener Titel statt eines Dateinamens.</summary>
        private void ShowPreludeSource(int line)
        {
            var viewer = new FileViewerWindow();
            viewer.LoadSource("Standardbibliothek (Prelude)", fire.Standard.Prelude.Source, line);
            viewer.Show();
        }

        /// <summary>Fällt auf jede per `#include` in DIESEM Dokument
        /// eingebundene Datei zurück, wenn NavigationEngine.TryResolve im
        /// Dokument selbst nichts gefunden hat - braucht einen gespeicherten
        /// Dateipfad (siehe FilePath), um relative Include-Pfade überhaupt
        /// auflösen zu können (ein noch nie gespeichertes Dokument hat kein
        /// Verzeichnis, relativ zu dem das Sinn ergäbe).</summary>
        private NavigationTarget? TryResolveAcrossIncludes(string source, int offset, ScriptSymbolIndex index)
        {
            if (FilePath == null || index.IncludeDirectives.Count == 0) return null;

            var extracted = NavigationEngine.ExtractIdentifierAndReceiver(source, offset);
            if (extracted == null) return null;
            var (identifier, receiver) = extracted.Value;

            string? dir = Path.GetDirectoryName(FilePath);
            foreach (var inc in index.IncludeDirectives)
            {
                string resolved;
                try { resolved = Path.GetFullPath(Path.Combine(dir ?? ".", inc.RelativePath)); }
                catch { continue; }
                if (!File.Exists(resolved)) continue;

                string includedSource;
                try { includedSource = File.ReadAllText(resolved); }
                catch { continue; }

                var includedIndex = ScriptSymbolIndex.Build(includedSource);
                var line = NavigationEngine.TryResolveInOtherFile(identifier, receiver, includedIndex);
                if (line != null) return new NavigationTarget(resolved, line.Value);
            }
            return null;
        }

        /// <summary>Öffnet `pathFromTarget` (entweder schon absolut - vom
        /// includes-Rückfall oben - oder noch der ROHE relative Pfad direkt
        /// aus einer `#include`-Zeile, siehe NavigationTarget-Doku) in einem
        /// neuen FileViewerWindow-Popup, zu `line` gescrollt.</summary>
        private void OpenFileViewer(string pathFromTarget, int line)
        {
            string resolved = pathFromTarget;
            if (!Path.IsPathRooted(resolved))
            {
                if (FilePath == null)
                {
                    MessageBox.Show(
                        "Diese Datei muss erst gespeichert werden, bevor '#include'-Pfade aufgelöst werden können.",
                        "Springen nicht möglich", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                string? dir = Path.GetDirectoryName(FilePath);
                resolved = Path.GetFullPath(Path.Combine(dir ?? ".", pathFromTarget));
            }

            var viewer = new FileViewerWindow { Owner = Window.GetWindow(this) };
            viewer.LoadFile(resolved, line);
            viewer.Show();
        }

        /// <summary>Caret-Position als (0-basierter Absatz-Index, Zeichen-
        /// Offset INNERHALB dieses Absatzes) - zuverlässig, weil innerhalb
        /// EINES Absatzes (keine Absatzgrenze dazwischen) WPFs interne
        /// Zählung tatsächlich mit reinen Zeichen-Offsets übereinstimmt.</summary>
        private (int Line, int Column) GetCaretLineColumn()
        {
            var caret = Editor.CaretPosition;
            var para = caret.Paragraph;
            if (para == null) return (0, 0);

            var blocks = Editor.Document.Blocks.OfType<Paragraph>().ToList();
            int line = blocks.IndexOf(para);
            if (line < 0) line = 0;

            int column = new TextRange(para.ContentStart, caret).Text.Length;
            return (line, column);
        }

        public void SetCaretByLineColumn(int line, int column)
        {
            var blocks = Editor.Document.Blocks.OfType<Paragraph>().ToList();
            if (blocks.Count == 0) return;

            line = Math.Max(0, Math.Min(line, blocks.Count - 1));
            var para = blocks[line];

            // Bewusst NICHT GetPositionAtOffset(column, ...) - das zählt in
            // "Symbolen", was bei mehreren Runs INNERHALB desselben Absatzes
            // (durch das Highlighting entstehen pro Zeile oft mehrere
            // farbige Runs) offenbar nicht exakt mit der Zeichenanzahl
            // übereinstimmt (genau das beobachtete "springt um ein oder
            // mehrere Zeichen"). Stattdessen zeichenweise mit
            // GetNextInsertionPosition navigieren - dieselbe API, die WPF
            // auch für die Pfeiltasten-Navigation selbst verwendet, landet
            // also garantiert an denselben Stellen wie ein manuelles
            // Drücken von "Rechts" `column`-mal.
            var pointer = para.ContentStart;
            for (int i = 0; i <= column; i++)
            {
                var next = pointer.GetNextInsertionPosition(LogicalDirection.Forward);
                if (next == null || next.CompareTo(para.ContentEnd) >= 0)
                {
                    pointer = para.ContentEnd;
                    break;
                }
                pointer = next;
            }
            Editor.CaretPosition = pointer;
        }

        public void ScrollToLine(int line)
        {
            var blocks = Editor.Document.Blocks.OfType<Paragraph>().ToList();
            if (line >= 1 && line <= blocks.Count)
                blocks[line - 1].BringIntoView();
        }

        public new void Focus() => Editor.Focus();

        // -----------------------------------------------------------
        // Haltepunkte (Rand-Klick über F9 im Host, siehe ToggleBreakpointAtCaret)
        // -----------------------------------------------------------

        public void ToggleBreakpointAtCaret()
        {
            int line = GetCaretLine();
            if (!_breakpoints.Remove(line))
                _breakpoints.Add(line);
            ApplyHighlighting();
            BreakpointsChanged?.Invoke();
        }

        public void ClearBreakpoints()
        {
            if (_breakpoints.Count == 0) return;
            _breakpoints.Clear();
            ApplyHighlighting();
            BreakpointsChanged?.Invoke();
        }
    }
}
