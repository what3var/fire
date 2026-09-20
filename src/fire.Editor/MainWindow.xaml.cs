using System;
using System.Collections.Concurrent;
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
using Microsoft.Win32;
using fire.Runtime;
using fire.Values;

namespace fire.Editor
{
    public partial class MainWindow : Window
    {
        private readonly DebugSession _session = new();
        private readonly HashSet<int> _breakpoints = new();
        private string? _currentFilePath;

        // Verhindert, dass ApplyHighlighting()s eigenes Neuaufbauen des
        // FlowDocument (das TextChanged auslöst) eine weitere Highlighting-
        // Runde anstößt - sonst Endlos-Rekursion.
        private bool _suppressTextChanged;

        // Highlighting läuft debounced (statt bei JEDEM Tastendruck) - sonst
        // würde das komplette Neuaufbauen des FlowDocument bei schnellem
        // Tippen spürbar ruckeln.
        private readonly DispatcherTimer _highlightTimer;

        // Die aktuell per Debugger angehaltene Zeile (gelb hervorgehoben),
        // falls vorhanden.
        private int? _highlightedLine;

        // Live-Fehleranalyse (siehe LiveDiagnostics) - läuft debounced wie
        // das Highlighting, aber mit einer LÄNGEREN Verzögerung (Parser +
        // Resolver + Compiler sind spürbar teurer als reines Lexen) und
        // GETRENNT davon, damit schnelles Tippen nicht bei jedem Zwischen-
        // zustand einen vollständigen Kompilierversuch auslöst.
        private readonly DispatcherTimer _diagnosticsTimer;
        private List<Diagnostic> _diagnostics = new();

        // Ausgabe-Warteschlange (siehe OnScriptOutput-Doku) - thread-sicher,
        // da JEDER Thread (Main oder ein Fire-Thread) gleichzeitig
        // hineinschreiben kann; _outputFlushTimer holt sie regelmäßig,
        // GEBÜNDELT auf dem UI-Thread ab, statt pro print() einzeln zu
        // aktualisieren.
        private readonly ConcurrentQueue<string> _pendingOutput = new();
        private readonly DispatcherTimer _outputFlushTimer;

        // Die Vorschläge, die GERADE im CompletionPopup angezeigt werden -
        // Index in CompletionList.SelectedIndex zeigt hierauf.
        private List<CompletionItem> _completionItems = new();

        public MainWindow()
        {
            InitializeComponent();

            // Bewusst ein explizit registrierter Handler (siehe unten,
            // Window_PreviewKeyDown) statt eines OnPreviewKeyDown-Overrides -
            // UND mit handledEventsToo:true, damit er auch dann noch feuert,
            // wenn irgendein Kind-Element (oder eine spätere WPF-interne
            // Verarbeitung, siehe Window_PreviewKeyDown-Doku zu F10) das
            // Ereignis bereits als Handled markiert hat. Ein einfacher
            // Override/normal registrierter Handler hätte das NICHT
            // garantiert.
            AddHandler(PreviewKeyDownEvent, new KeyEventHandler(Window_PreviewKeyDown), handledEventsToo: true);

            _session.OutputWritten += OnScriptOutput;
            // WICHTIG: InvokeAsync (nicht-blockierend), NICHT Invoke -
            // dieser Handler kann vom EIGENEN Hintergrund-Thread eines
            // gerade entstehenden/pausierenden Fire-Threads aus feuern,
            // während der UI-Thread SELBST synchron in einem "Bis Ende
            // durchlaufen" des Main-Threads steckt (z.B. RunToCompletion),
            // das seinerseits auf eine Nachricht von GENAU DIESEM
            // Fire-Thread wartet (etwa über `process`) - ein blockierendes
            // Dispatcher.Invoke hier würde in diesem Fall zu einem echten
            // Deadlock führen (Fire-Thread wartet auf den UI-Thread, der
            // UI-Thread wartet transitiv auf den Fire-Thread).
            _session.ThreadAdded += ctx => Dispatcher.InvokeAsync(() => RefreshThreadsList());
            _session.ThreadPaused += ctx => Dispatcher.InvokeAsync(() =>
            {
                RefreshThreadsList();
                // Nur wenn der GERADE ANGEZEIGTE (aktive) Thread betroffen
                // ist, muss die Detailanzeige (Hervorhebung/Scope/Stack)
                // neu aufgebaut werden - ein anderer, automatisch
                // weiterlaufender Fire-Thread, der gerade z.B. einen
                // Haltepunkt erreicht, aktualisiert erstmal nur seinen
                // eigenen Eintrag in der Threads-Liste (siehe dort für die
                // Statusanzeige).
                if (ReferenceEquals(ctx, _session.ActiveThread))
                {
                    _isBusy = false;
                    AfterStep(!ctx.IsFinished);
                }
            });

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

            // Läuft DURCHGEHEND (nicht debounced wie die beiden obigen -
            // Ausgabe kann jederzeit, auch mitten in einem langen Lauf,
            // anfallen und soll zeitnah sichtbar werden, nur eben gebündelt
            // statt einzeln, siehe OnScriptOutput-Doku).
            _outputFlushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            _outputFlushTimer.Tick += (_, _) => FlushPendingOutput();
            _outputFlushTimer.Start();

            // Highlighting wird während das Popup offen ist bewusst
            // ausgesetzt (siehe Editor_TextChanged) - hier nachholen, sobald
            // es schließt (egal ob durch Übernahme, Escape oder Fokusverlust).
            CompletionPopup.Closed += (_, _) =>
            {
                _highlightTimer.Stop();
                _highlightTimer.Start();
            };

            SetEditorText("// Willkommen im fire-Editor\nprint(\"Hallo, Welt!\")\n");
            UpdateStatus("Bereit.");
        }

        // -----------------------------------------------------------
        // Text-Zugriff (RichTextBox <-> reiner String)
        //
        // Bewusste Design-Entscheidung: EIN Paragraph pro Quelltextzeile
        // (kein Zeilenumbruch, kein automatischer Wortumbruch innerhalb einer
        // Zeile) - dadurch entspricht "Zeile N im Editor" IMMER exakt
        // "Zeile N im Sinn von Chunk.MarkLine/GetLine", ohne dass Wortumbruch
        // die Zuordnung durcheinanderbringen könnte.
        // -----------------------------------------------------------

        private string GetEditorText()
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

        private void SetEditorText(string text)
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

        readonly IEnumerable<Char> lastChars = new List<Char>() { ' ', '.', '(', ';', ':', '!', '\t', '\r', '\n', '{' };

        private void Editor_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressTextChanged) return;

            // Diagnostik läuft UNABHÄNGIG vom Popup-Zustand debounced weiter
            // (löst selbst keinen Dokument-Neuaufbau aus, siehe
            // RunDiagnostics - erst das anschließende ApplyHighlighting tut
            // das, und das respektiert die Popup-Sperre bereits).
            _diagnosticsTimer.Stop();
            _diagnosticsTimer.Start();

            string source = GetEditorText();
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
                    string src = GetEditorText();
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

        private void Editor_SelectionChanged(object sender, RoutedEventArgs e)
        {
            CaretText.Text = $"Zeile {GetCaretLine()}";
        }

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
                string source = GetEditorText();
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

            string source = GetEditorText();
            int offset = GetOffsetOf(Editor.CaretPosition);

            int start = offset - 1;
            while (start >= 0 && (char.IsLetterOrDigit(source[start]) || source[start] == '_')) start--;
            start++;

            string newSource = source.Substring(0, start) + item.Text + source.Substring(Math.Min(offset, source.Length));
            int newCaretOffset = start + item.Text.Length;
            var (newLine, newColumn) = OffsetToLineColumn(newSource, newCaretOffset);

            SetEditorText(newSource);
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

        private int GetCaretLine()
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
            string text = GetEditorText();
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
        /// aktualisiert die Fehleransicht und (falls das Vervollständigungs-
        /// Popup nicht gerade offen ist, siehe ApplyHighlighting-Aufrufe an
        /// anderer Stelle für die Begründung) die unterkringelten Zeilen im
        /// Editor selbst.</summary>
        private void RunDiagnostics()
        {
            string source = GetEditorText();
            _diagnostics = LiveDiagnostics.Analyze(source);
            UpdateErrorPanel();
            if (!CompletionPopup.IsOpen)
                ApplyHighlighting();
        }

        private void UpdateErrorPanel()
        {
            ErrorList.ItemsSource = _diagnostics.Select(d => d.ToString()).ToList();
            ErrorPanelHeader.Text = _diagnostics.Count == 0
                ? "Fehler (keine)"
                : $"Fehler ({_diagnostics.Count})";
        }

        private void ErrorList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ErrorList.SelectedIndex < 0 || ErrorList.SelectedIndex >= _diagnostics.Count) return;
            int line = _diagnostics[ErrorList.SelectedIndex].Line;
            ScrollToLine(line);
            SetCaretByLineColumn(Math.Max(0, line - 1), 0);
            Editor.Focus();
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
            string source = GetEditorText();
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
        /// Dateipfad, um relative Include-Pfade überhaupt auflösen zu
        /// können (ein noch nie gespeichertes Dokument hat kein Verzeichnis,
        /// relativ zu dem das Sinn ergäbe).</summary>
        private NavigationTarget? TryResolveAcrossIncludes(string source, int offset, ScriptSymbolIndex index)
        {
            if (_currentFilePath == null || index.IncludeDirectives.Count == 0) return null;

            var extracted = NavigationEngine.ExtractIdentifierAndReceiver(source, offset);
            if (extracted == null) return null;
            var (identifier, receiver) = extracted.Value;

            string? dir = Path.GetDirectoryName(_currentFilePath);
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
                if (_currentFilePath == null)
                {
                    MessageBox.Show(
                        "Diese Datei muss erst gespeichert werden, bevor '#include'-Pfade aufgelöst werden können.",
                        "Springen nicht möglich", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                string? dir = Path.GetDirectoryName(_currentFilePath);
                resolved = Path.GetFullPath(Path.Combine(dir ?? ".", pathFromTarget));
            }

            var viewer = new FileViewerWindow { Owner = this };
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

        private void SetCaretByLineColumn(int line, int column)
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

        // -----------------------------------------------------------
        // Ausführen / Debuggen
        // -----------------------------------------------------------

        private void OnScriptOutput(string text)
        {
            // Bewusst NUR einreihen, KEINE UI-Interaktion hier - diese
            // Methode kann sehr oft und sehr schnell hintereinander aus
            // einem Fire-Thread heraus feuern (z.B. in einer Schleife mit
            // hunderten print()-Aufrufen). Ein Dispatcher-Aufruf PRO
            // einzelnem print() (frühere Implementierung) erzeugt hunderte
            // einzelne AppendText/ScrollToEnd-Operationen auf dem UI-Thread
            // hintereinander - jede davon billig für sich, aber in Summe
            // spürbar reaktionsträge ("hängt"), weil WPF dabei kaum zum
            // Verarbeiten anderer Ereignisse (Maus/Tastatur/Neuzeichnen)
            // kommt. _outputFlushTimer (siehe Konstruktor) holt die ganze
            // Warteschlange stattdessen gebündelt, in festen Abständen, in
            // EINEM AppendText-Aufruf ab.
            _pendingOutput.Enqueue(text);
        }

        /// <summary>Holt ALLE aktuell wartenden Ausgabe-Zeilen aus der
        /// Warteschlange (siehe OnScriptOutput-Doku) und hängt sie in
        /// GENAU EINEM AppendText/ScrollToEnd-Aufruf an - läuft auf dem
        /// UI-Thread (Aufrufer ist der _outputFlushTimer im Konstruktor).
        /// Tut nichts, wenn gerade nichts Neues wartet.</summary>
        private void FlushPendingOutput()
        {
            if (_pendingOutput.IsEmpty) return;

            var batch = new System.Text.StringBuilder();
            while (_pendingOutput.TryDequeue(out var line))
                batch.Append(line).Append(Environment.NewLine);

            OutputBox.AppendText(batch.ToString());
            OutputBox.ScrollToEnd();
        }

        private void Run_Click(object sender, RoutedEventArgs e) => CompileAndPrepare();

        private void CompileAndPrepare()
        {
            OutputBox.Clear();
            while (_pendingOutput.TryDequeue(out _)) { } // Reste eines evtl. noch nicht abgeflossenen vorigen Laufs verwerfen
            _highlightedLine = null;
            _isBusy = false;
            string source = GetEditorText();
            _session.UpdateBreakpoints(_breakpoints);

            if (!_session.Compile(new string[]{source}))
            {
                ApplyHighlighting();
                UpdateStatus($"Kompilierfehler: {_session.CompileError}");
                MessageBox.Show(_session.CompileError, "Kompilierfehler",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            ApplyHighlighting();
            UpdateStatus("Kompiliert - bereit für Einzelschritt/Weiter/Bis Ende.");
            RefreshDebugPanels();
        }

        // Verhindert überlappende Schritt-Anfragen auf demselben Thread
        // (siehe DebugThreadContext.RequestStep-Doku: fire-and-forget, ein
        // zweiter Aufruf während der erste noch läuft könnte sonst dessen
        // Ergebnis überschreiben) - gesetzt beim Anfordern, zurückgesetzt
        // sobald ThreadPaused für den betroffenen Thread feuert.
        private bool _isBusy;

        private void Step_Click(object sender, RoutedEventArgs e)
        {
            if (_session.Vm == null) CompileAndPrepare();
            if (_session.Vm == null || _isBusy) return;
            BeginStep();
            _session.StepLine();
        }

        private void StepInto_Click(object sender, RoutedEventArgs e)
        {
            if (_session.Vm == null) CompileAndPrepare();
            if (_session.Vm == null || _isBusy) return;
            BeginStep();
            _session.StepInto();
        }

        private void StepOut_Click(object sender, RoutedEventArgs e)
        {
            if (_session.Vm == null || _isBusy) return; // "Funktion verlassen" ohne laufende Funktion ergibt keinen Sinn
            BeginStep();
            _session.StepOut();
        }

        private void Continue_Click(object sender, RoutedEventArgs e)
        {
            if (_session.Vm == null) CompileAndPrepare();
            if (_session.Vm == null || _isBusy) return;
            BeginStep();
            _session.Continue(_breakpoints);
        }

        private void RunToEnd_Click(object sender, RoutedEventArgs e)
        {
            if (_session.Vm == null) CompileAndPrepare();
            if (_session.Vm == null || _isBusy) return;
            BeginStep();
            _session.RunToCompletion();
        }

        /// <summary>"Anhalten"-Knopf für einen gerade laufenden ("Weiter"/
        /// "Bis Ende durchlaufen") aktiven Thread - siehe DebugSession.
        /// PauseActiveThread/DebugThreadContext.RequestPause für die
        /// Einschränkung (wirkt nur bei Continue/RunToCompletion, nicht
        /// mitten in einem einzelnen Step Line/Into/Out).</summary>
        private void PauseThread_Click(object sender, RoutedEventArgs e) => _session.PauseActiveThread();

        /// <summary>Alle Schritt-Anfragen sind FIRE-AND-FORGET (siehe
        /// DebugThreadContext - jede VM läuft auf ihrem EIGENEN Hintergrund-
        /// Thread, damit die UI während der Ausführung reaktionsfähig
        /// bleibt) - hier nur die sofortige Rückmeldung "es läuft", die
        /// eigentliche Aktualisierung kommt asynchron über das ThreadPaused-
        /// Event (siehe Konstruktor).</summary>
        private void BeginStep()
        {
            _isBusy = true;
            UpdateStatus("Läuft...");
        }

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            _session.Reset();
            _isBusy = false;
            _highlightedLine = null;
            ApplyHighlighting();
            RefreshDebugPanels();
            UpdateStatus("Gestoppt.");
        }

        private void AfterStep(bool more)
        {
            FlushPendingOutput(); // sofort sichtbar, nicht erst beim nächsten Timer-Tick
            if (!more)
            {
                _highlightedLine = null;
                ApplyHighlighting();
                UpdateStatus(_session.RuntimeError != null
                    ? $"Laufzeitfehler: {_session.RuntimeError}"
                    : "Programm beendet.");
            }
            else
            {
                _highlightedLine = _session.Vm!.CurrentLine;
                ApplyHighlighting();
                ScrollToLine(_highlightedLine.Value);
                UpdateStatus($"Angehalten in Zeile {_highlightedLine}.");
            }
            RefreshDebugPanels();
        }

        private void ScrollToLine(int line)
        {
            var blocks = Editor.Document.Blocks.OfType<Paragraph>().ToList();
            if (line >= 1 && line <= blocks.Count)
                blocks[line - 1].BringIntoView();
        }

        private void RefreshDebugPanels()
        {
            RefreshThreadsList();

            var vm = _session.Vm;
            CallDepthText.Text = $"Aufruftiefe: {(vm?.DebugCallDepth.ToString() ?? "-")}";
            CurrentLineText.Text = $"Aktuelle Zeile: {(vm != null ? vm.CurrentLine.ToString() : "-")}";
            BreakpointsText.Text = _breakpoints.Count == 0
                ? "Haltepunkte: (keine - F9 auf der Cursor-Zeile)"
                : "Haltepunkte: " + string.Join(", ", _breakpoints.OrderBy(x => x));

            ScopeTree.Items.Clear();
            if (vm == null)
            {
                StackView.ItemsSource = null;
                return;
            }

            // "this" ganz oben, falls an dieser Stelle gebunden - jetzt als
            // echter (aufklappbarer) Wert statt nur als Textzeile, siehe
            // BuildVariableTreeItem.
            if (vm.DebugThisValue is Value thisValue)
                ScopeTree.Items.Add(BuildVariableTreeItem("this", thisValue, expanded: true));

            // Scope-Kette der aktuellen Funktion - der ERSTE Eintrag (Depth 0)
            // ist der GERADE AKTIVE (innerste) Block, danach umschließende
            // Ebenen bis zur Funktions-/Methoden-/Lambda-Grenze.
            foreach (var level in vm.DebugScopeChain())
            {
                string label = level.Depth == 0
                    ? (level.IsFunctionTopLevel ? "Aktiver Scope (Funktionsebene)" : "Aktiver Scope")
                    : $"umschließender Scope (Tiefe {level.Depth})" + (level.IsFunctionTopLevel ? " - Parameter" : "");

                var node = new TreeViewItem { Header = label, IsExpanded = level.Depth == 0 };
                foreach (var (name, value) in level.Variables)
                    node.Items.Add(BuildVariableTreeItem(name, value));
                if (level.Variables.Count == 0)
                    node.Items.Add(new TreeViewItem { Header = "(leer)" });
                ScopeTree.Items.Add(node);
            }

            // Global ganz unten, eingeklappt (meist nicht der primäre Fokus
            // beim Debuggen einer bestimmten Funktion).
            var globals = vm.DebugGlobals().ToList();
            var globalNode = new TreeViewItem { Header = "Global", IsExpanded = false };
            foreach (var (name, value) in globals)
                globalNode.Items.Add(BuildVariableTreeItem(name, value));
            if (globals.Count == 0)
                globalNode.Items.Add(new TreeViewItem { Header = "(leer)" });
            ScopeTree.Items.Add(globalNode);

            StackView.ItemsSource = vm.DebugStackSnapshot
                .Reverse()
                .Select(v => v.ToString())
                .ToList();
        }

        // -----------------------------------------------------------
        // Feld-/Element-Anzeige für Objekte und Arrays im Scope-Baum
        // (siehe RefreshDebugPanels) - baut Kind-Knoten LAZY erst beim
        // tatsächlichen Aufklappen auf (TreeViewItem.Expanded), statt den
        // kompletten (potenziell riesigen oder zyklischen) Objektgraphen
        // sofort komplett zu durchlaufen. Zyklenschutz über die Menge der
        // bereits auf dem Pfad von der Wurzel besuchten Objekte/Arrays
        // (Referenzidentität, nicht Wert-Gleichheit) - eine ganz normale
        // MEHRFACHE Referenz auf dasselbe Objekt an verschiedenen Stellen
        // ist dagegen kein Zyklus und bleibt aufklappbar (nur der Pfad
        // WURZEL->...->SELBES OBJEKT NOCHMAL wird abgeschnitten).
        // -----------------------------------------------------------

        private TreeViewItem BuildVariableTreeItem(string name, Value value, HashSet<object>? ancestors = null, bool expanded = false)
        {
            var item = new TreeViewItem { Header = $"{name} = {DescribeForTree(value)}", IsExpanded = expanded };
            AttachChildrenIfExpandable(item, value, ancestors ?? new HashSet<object>());
            return item;
        }

        /// <summary>Wie Value.ToString(), aber für Objekte/Arrays mit einer
        /// für den Debugger nützlicheren Kurzbeschreibung (Klassenname +
        /// Erzeugungs-ID statt des rohen .NET-Typnamens, Elementanzahl statt
        /// nur "&lt;array&gt;") - reine Anzeige-Bequemlichkeit, ändert nichts an
        /// Value.ToString() selbst (das wird u.a. für Skript-seitige
        /// String-Konkatenation gebraucht und soll dafür unverändert
        /// bleiben).</summary>
        private static string DescribeForTree(Value value) => value.Kind switch
        {
            ValueKind.Class => $"{((ObjectInstance)value.AsObjectRef()).ClassDef.Name} (#{((ObjectInstance)value.AsObjectRef()).Id})",
            ValueKind.Array => $"Array[{value.AsArray().Length}]",
            _ => value.ToString(),
        };

        private void AttachChildrenIfExpandable(TreeViewItem item, Value value, HashSet<object> ancestors)
        {
            if (value.Kind == ValueKind.Class)
            {
                var obj = (ObjectInstance)value.AsObjectRef();
                if (ancestors.Contains(obj))
                {
                    item.Items.Add(new TreeViewItem { Header = "(Zyklus - Objekt liegt weiter oben im Baum)" });
                    return;
                }
                if (!obj.Fields.Any()) return; // keine Felder - kein Aufklapp-Pfeil nötig

                item.Items.Add(new TreeViewItem { Header = "…" }); // Platzhalter, bis tatsächlich aufgeklappt
                bool loaded = false;
                item.Expanded += (_, _) =>
                {
                    if (loaded) return;
                    loaded = true;
                    item.Items.Clear();
                    var childAncestors = new HashSet<object>(ancestors) { obj };
                    foreach (var (fieldName, fieldValue) in obj.Fields)
                        item.Items.Add(BuildVariableTreeItem(fieldName, fieldValue, childAncestors));
                };
            }
            else if (value.Kind == ValueKind.Array)
            {
                var arr = value.AsArray();
                if (arr.Length == 0) return;
                if (ancestors.Contains(arr))
                {
                    item.Items.Add(new TreeViewItem { Header = "(Zyklus - Array liegt weiter oben im Baum)" });
                    return;
                }

                item.Items.Add(new TreeViewItem { Header = "…" });
                bool loaded = false;
                item.Expanded += (_, _) =>
                {
                    if (loaded) return;
                    loaded = true;
                    item.Items.Clear();
                    var childAncestors = new HashSet<object>(ancestors) { arr };
                    for (int i = 0; i < arr.Length; i++)
                        item.Items.Add(BuildVariableTreeItem($"[{i}]", arr.Items[i], childAncestors));
                };
            }
        }

        // Index-parallel zu ThreadsList.ItemsSource (siehe RefreshThreadsList)
        // - derselbe Aufbau wie _completionItems/_diagnostics an anderer
        // Stelle in dieser Datei: die Liste selbst zeigt nur formatierten
        // Text an, die Auswahl wird über den Index auf dieses Parallel-Array
        // zurückgemappt.
        private List<DebugThreadContext> _threadListItems = new();

        private void RefreshThreadsList()
        {
            _threadListItems = _session.Threads.ToList();
            var active = _session.ActiveThread;

            ThreadsList.ItemsSource = _threadListItems.Select(t =>
            {
                string status = t.RuntimeError != null ? $"Fehler: {t.RuntimeError}"
                    : t.IsFinished ? "beendet"
                    : $"Zeile {t.Vm.CurrentLine}";
                string marker = ReferenceEquals(t, active) ? "-> " : "   ";
                return $"{marker}{t.Name} ({status})";
            }).ToList();

            int activeIndex = active != null ? _threadListItems.FindIndex(t => ReferenceEquals(t, active)) : -1;
            if (activeIndex >= 0) ThreadsList.SelectedIndex = activeIndex;
        }

        private void ThreadsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            int index = ThreadsList.SelectedIndex;
            if (index < 0 || index >= _threadListItems.Count) return;

            var chosen = _threadListItems[index];
            if (ReferenceEquals(chosen, _session.ActiveThread)) return;

            // Ein evtl. noch ausstehender Schritt des BISHERIGEN aktiven
            // Threads würde _isBusy sonst für immer gesetzt lassen (sein
            // ThreadPaused-Ereignis feuert später mit einem ctx, der nicht
            // mehr der aktive ist, siehe Konstruktor) - beim Wechsel des
            // Threads deshalb immer zurücksetzen, damit die Schritt-Knöpfe
            // nicht dauerhaft gesperrt bleiben.
            _isBusy = false;
            _session.SelectThread(chosen);
            _highlightedLine = chosen.IsFinished ? null : chosen.Vm.CurrentLine;
            ApplyHighlighting();
            if (_highlightedLine != null) ScrollToLine(_highlightedLine.Value);
            RefreshDebugPanels();
            UpdateStatus(chosen.IsFinished
                ? $"{chosen.Name}: beendet."
                : $"{chosen.Name}: angehalten in Zeile {chosen.Vm.CurrentLine}.");
        }

        private void ToggleBreakpoint_Click(object sender, RoutedEventArgs e)
        {
            int line = GetCaretLine();
            if (!_breakpoints.Remove(line))
                _breakpoints.Add(line);
            _session.UpdateBreakpoints(_breakpoints);
            ApplyHighlighting();
            RefreshDebugPanels();
        }

        private void UpdateStatus(string text) => StatusText.Text = text;

        // -----------------------------------------------------------
        // Datei-Menü
        // -----------------------------------------------------------

        private void New_Click(object sender, RoutedEventArgs e)
        {
            _diagnostics = new List<Diagnostic>();
            UpdateErrorPanel();
            SetEditorText(string.Empty);
            _currentFilePath = null;
            _breakpoints.Clear();
            Stop_Click(sender, e);
            UpdateStatus("Neue Datei.");
        }

        private void Open_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "fire-Dateien (*.script)|*.script|Alle Dateien (*.*)|*.*" };
            if (dlg.ShowDialog() != true) return;

            _diagnostics = new List<Diagnostic>();
            UpdateErrorPanel();
            SetEditorText(File.ReadAllText(dlg.FileName));
            _currentFilePath = dlg.FileName;
            _breakpoints.Clear();
            Stop_Click(sender, e);
            UpdateStatus($"Geöffnet: {dlg.FileName}");
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (_currentFilePath == null)
            {
                SaveAs_Click(sender, e);
                return;
            }
            File.WriteAllText(_currentFilePath, GetEditorText());
            UpdateStatus($"Gespeichert: {_currentFilePath}");
        }

        private void SaveAs_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog { Filter = "fire-Dateien (*.script)|*.script|Alle Dateien (*.*)|*.*" };
            if (dlg.ShowDialog() != true) return;

            _currentFilePath = dlg.FileName;
            File.WriteAllText(_currentFilePath, GetEditorText());
            UpdateStatus($"Gespeichert: {_currentFilePath}");
        }

        private void Exit_Click(object sender, RoutedEventArgs e) => Close();

        /// <summary>Bewusst ein explizit registrierter Handler (siehe
        /// Konstruktor: `AddHandler(..., handledEventsToo: true)`) statt
        /// eines `OnPreviewKeyDown`-Overrides UND bewusst PreviewKeyDown
        /// (Tunneling, von der Wurzel zum fokussierten Element) statt
        /// KeyDown (Bubbling) - zwei unabhängige Gründe, warum die
        /// Hotkeys vorher nicht ankamen:
        ///
        /// 1. WPF behandelt F10 bei vorhandenem `Menu`-Element speziell
        ///    (aktiviert die Tastatur-Navigation des Menüs, klassisches
        ///    Windows-Verhalten) - Preview statt Bubbling läuft VOR dieser
        ///    eingebauten Behandlung.
        /// 2. WICHTIGER, der eigentliche Grund: F10 ist (wie Alt) unter
        ///    Windows eine "System-Taste" (WM_SYSKEYDOWN) - WPF liefert
        ///    dafür `e.Key == Key.System`, die TATSÄCHLICHE Taste steht in
        ///    `e.SystemKey`, NICHT in `e.Key` selbst! Ein Vergleich gegen
        ///    `e.Key == Key.F10` (wie vorher) matcht deshalb NIE, unabhängig
        ///    von Tunneling/Bubbling oder Handled-Status - das war der
        ///    eigentliche Bug, nicht (nur) die Event-Phase.
        ///
        /// `handledEventsToo: true` beim Registrieren macht das zusätzlich
        /// robust gegen JEDEN Fall, in dem irgendein Kind-Element das
        /// Ereignis bereits als Handled markiert hätte.</summary>
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Bei System-Tasten (F10, Alt+...) steht die eigentliche Taste in
            // SystemKey, e.Key ist dann nur Key.System (siehe Doku oben).
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;

            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

            switch (key)
            {
                case Key.F5 when ctrl:
                    RunToEnd_Click(this, e); e.Handled = true; break;
                case Key.F5 when shift:
                    Stop_Click(this, e); e.Handled = true; break;
                case Key.F5:
                    Run_Click(this, e); e.Handled = true; break;
                case Key.F10:
                    Step_Click(this, e); e.Handled = true; break;
                case Key.F11 when shift:
                    StepOut_Click(this, e); e.Handled = true; break;
                case Key.F11:
                    StepInto_Click(this, e); e.Handled = true; break;
                case Key.F8:
                    Continue_Click(this, e); e.Handled = true; break;
                case Key.F9:
                    ToggleBreakpoint_Click(this, e); e.Handled = true; break;
                case Key.N when ctrl:
                    New_Click(this, e); e.Handled = true; break;
                case Key.O when ctrl:
                    Open_Click(this, e); e.Handled = true; break;
                case Key.S when ctrl:
                    Save_Click(this, e); e.Handled = true; break;
            }
        }
    }
}
