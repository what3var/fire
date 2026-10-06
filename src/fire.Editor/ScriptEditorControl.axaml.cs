using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace fire.Editor
{
    /// <summary>Ein eigenständiges, wiederverwendbares Editor-Control:
    /// AvalonEdit-TextEditor + Syntax-Highlighting + Autovervollständigung +
    /// Strg+Klick-Navigation + klickbarer Haltepunkt-Rand + Live-Diagnostik -
    /// GENAU EIN Quelltext pro Instanz. Sowohl vom alten Einzeldatei-Fenster
    /// (MainWindow, eine Instanz) als auch vom Tabbed-Projekt-Fenster
    /// (ProjectWindow, eine Instanz PRO offenem Tab) genutzt, damit Editing-
    /// Logik nicht zweimal gepflegt werden muss.
    ///
    /// Auf AvaloniaEdit (die Avalonia-Fassung von AvalonEdit; siehe EditorRendering.cs für
    /// die neuen Render-Bausteine): der alte Ansatz (Highlighting = beim
    /// Tippen debounced das KOMPLETTE FlowDocument neu bauen und austauschen)
    /// war die Wurzel so gut wie aller Bugs mehrerer Debug-Runden - Auswahl/
    /// Cursor kollabierte beim Austausch, Wettlauf zwischen Tippen und
    /// Hintergrund-Highlighting, eine echte Rückkopplungsschleife, Scroll-
    /// Position sprang bei langen Dateien. AvalonEdit hat ein eigenes
    /// TextDocument-Modell mit ECHTEN Zeichen-Offsets (GetOffset/GetLocation)
    /// statt WPFs TextPointer/Paragraph-Klassenhierarchie, und Highlighting
    /// läuft rein beim ZEICHNEN (DocumentColorizingTransformer) - das
    /// Dokument selbst wird dafür nie angefasst, das eliminiert diese ganze
    /// Bug-Klasse strukturell, statt sie Fall für Fall zu flicken.
    ///
    /// Bewusst KEINE eigene Kenntnis von DebugSession/Kompilieren/Ausführen -
    /// das bleibt Sache des jeweiligen Host-Fensters (siehe MainWindow/
    /// ProjectWindow), das diese Instanz orchestriert (Breakpoints abfragen,
    /// HighlightedLine setzen, GetText() beim Kompilieren aufrufen, ...).
    ///
    /// ZEILENZÄHLUNG: nach außen (öffentliche Schnittstelle) UNVERÄNDERT wie
    /// bei der alten RichTextBox-Fassung - SetCaretByLineColumn nimmt eine
    /// 0-basierte Zeile, ScrollToLine/HighlightedLine/Breakpoints/
    /// GetCaretLine sind 1-basiert (deckungsgleich mit Chunk.MarkLine/
    /// GetLocation) - damit bleibt MainWindow/ProjectWindow unverändert
    /// benutzbar. AvalonEdit selbst zählt INTERN überall 1-basiert
    /// (Caret.Line, DocumentLine.LineNumber, ...) - die Umrechnung an der
    /// 0-basierten SetCaretByLineColumn-Grenze ist die einzige Stelle, die
    /// das berücksichtigen muss.</summary>
    public partial class ScriptEditorControl : UserControl, IDocumentView
    {
        /// <summary>Der Dateipfad dieses Editors, falls schon einmal
        /// gespeichert/geöffnet - `null` für ein neues, ungespeichertes
        /// Dokument. Nur für relative `#include`-Pfadauflösung bei der
        /// Strg+Klick-Navigation gebraucht (siehe TryResolveAcrossIncludes/
        /// OpenFileViewer) - das Control selbst liest/schreibt NIE
        /// eigenständig von/auf die Platte, das bleibt Sache des Host-
        /// Fensters (siehe GetText/SetText).</summary>
        private string? _filePath;
        public string? FilePath { get => _filePath; set { _filePath = value; _conditionalSymbols = null; } }

        private readonly HashSet<int> _breakpoints = new();

        /// <summary>Die aktuellen Haltepunkt-Zeilen (1-basiert, wie im
        /// restlichen Editor) - nur LESEND; zum Ändern ToggleBreakpointAtCaret/
        /// ClearBreakpoints nutzen (löst dabei automatisch BreakpointsChanged
        /// aus und aktualisiert Rand + Zeilen-Hintergrund).</summary>
        public IReadOnlySet<int> Breakpoints => _breakpoints;

        /// <summary>Feuert, wann immer sich die Haltepunkt-Menge geändert hat
        /// (Rand-Klick/ToggleBreakpointAtCaret/ClearBreakpoints) - der Host
        /// muss darauf i.d.R. mit DebugSession.UpdateBreakpoints reagieren.</summary>
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
        public Func<string, List<Diagnostic>> DiagnosticsProvider { get; set; }

        /// <summary>Verzeichnis dieses Dokuments (für relative `#include`-
        /// Pfade) - `null` solange noch nie gespeichert/geöffnet (dann gilt
        /// das Arbeitsverzeichnis).</summary>
        public string? BaseDirectory => FilePath == null ? null : Path.GetDirectoryName(Path.GetFullPath(FilePath));

        /// <summary>Wahr, sobald der Text seit dem Laden/letzten Speichern
        /// geändert wurde (für den Stern im Tab-Titel und die Rückfrage beim
        /// Schließen).</summary>
        public bool IsModified { get; private set; }
        public bool IsReadOnly => false;

        /// <summary>Feuert, wenn sich IsModified geändert hat.</summary>
        public event Action? ModifiedChanged;

        /// <summary>Der Host hat den aktuellen Text gespeichert.</summary>
        public void MarkSaved() => SetModified(false);

        private void SetModified(bool value)
        {
            if (IsModified == value) return;
            IsModified = value;
            ModifiedChanged?.Invoke();
        }

        private bool _loading;

        /// <summary>Feuert, wann immer sich Diagnostics geändert hat - der
        /// Host zeigt das i.d.R. in einer eigenen Fehlerliste an.</summary>
        public event Action? DiagnosticsChanged;

        /// <summary>Feuert bei JEDER Cursor-Bewegung mit der neuen 1-basierten
        /// Zeile - für eine Statusleisten-Anzeige im Host.</summary>
        public event Action<int>? CaretLineChanged;

        /// <summary>Die aktuell per Debugger angehaltene Zeile (gelb
        /// hervorgehoben), `null` wenn keine - vom Host gesetzt (siehe
        /// MainWindow/ProjectWindow nach jedem Schritt/Stop). Setzt anders
        /// als früher NUR NOCH den Hintergrund-Renderer und löst ein
        /// Neuzeichnen aus (TextView.Redraw) - KEIN Dokument-Neuaufbau mehr
        /// nötig, siehe Klassendoku.</summary>
        public int? HighlightedLine
        {
            get => _highlightedLine;
            set
            {
                _highlightedLine = value;
                _lineBackground.HighlightedLine = value;
                Editor.TextArea.TextView.Redraw();
            }
        }
        private int? _highlightedLine;

        // Highlighting läuft debounced (statt bei jedem Tastendruck neu
        // gelext) - reines Lexen ist zwar schnell, aber bei sehr schnellem
        // Tippen soll trotzdem nicht bei JEDEM Zwischenzustand neu gelext
        // werden.
        private readonly DispatcherTimer _highlightTimer;

        // Live-Fehleranalyse (siehe LiveDiagnostics) - läuft debounced wie
        // das Highlighting, aber mit einer LÄNGEREN Verzögerung (Parser +
        // Resolver + Compiler sind spürbar teurer als reines Lexen) und
        // GETRENNT davon, damit schnelles Tippen nicht bei jedem Zwischen-
        // zustand einen vollständigen Kompilierversuch auslöst.
        private readonly DispatcherTimer _diagnosticsTimer;
        private List<Diagnostic> _diagnostics = new();

        private readonly HighlightingColorizer _colorizer = new();
        private readonly ErrorSquiggleRenderer _errorSquiggles = new();
        private readonly LineBackgroundRenderer _lineBackground = new();
        private readonly BreakpointMargin _breakpointMargin = new();

        private CompletionWindow? _completionWindow;

        public ScriptEditorControl()
        {
            InitializeComponent();

            DiagnosticsProvider = source => LiveDiagnostics.Analyze(source, BaseDirectory);

            Editor.TextArea.TextView.LineTransformers.Add(_colorizer);
            Editor.TextArea.TextView.BackgroundRenderers.Add(_lineBackground);
            Editor.TextArea.TextView.BackgroundRenderers.Add(_errorSquiggles);
            // Index 0 = ganz links, also vor der (von ShowLineNumbers="True"
            // automatisch eingefügten) Zeilennummer-Spalte - Haltepunkt-Punkt,
            // dann Zeilennummer, dann Text, wie in den meisten IDEs üblich.
            Editor.TextArea.LeftMargins.Insert(0, _breakpointMargin);
            _breakpointMargin.LineClicked += ToggleBreakpoint;

            Editor.TextChanged += Editor_TextChanged;
            Editor.TextArea.Caret.PositionChanged += (_, _) => CaretLineChanged?.Invoke(GetCaretLine());
            Editor.TextArea.TextEntered += Editor_TextEntered;
            // tunnelling handlers: they see the event before the editor does (and may mark it as handled)
            Editor.AddHandler(PointerPressedEvent, Editor_PointerPressed, RoutingStrategies.Tunnel);
            Editor.AddHandler(KeyDownEvent, Editor_KeyDown, RoutingStrategies.Tunnel);

            _searchPanel = AvaloniaEdit.Search.SearchPanel.Install(Editor);
            EditorTheme.Apply(Editor, _searchPanel);
            Editor.ContextMenu = BuildContextMenu();

            _highlightTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _highlightTimer.Tick += (_, _) =>
            {
                _highlightTimer.Stop();
                RecomputeHighlighting();
            };

            _diagnosticsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _diagnosticsTimer.Tick += (_, _) =>
            {
                _diagnosticsTimer.Stop();
                RunDiagnostics();
            };

            // Documentation tooltips (`///` comments): a short pause with the caret on a symbol, or the mouse resting on one.
            _caretTipTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _caretTipTimer.Tick += (_, _) =>
            {
                _caretTipTimer.Stop();
                ShowCaretDocTip();
            };
            Editor.TextArea.Caret.PositionChanged += (_, _) => OnCaretOrTextChanged();
            Editor.TextArea.TextView.PointerHover += TextView_PointerHover;
            Editor.TextArea.TextView.PointerHoverStopped += (_, _) =>
            {
                if (_docTipKind == DocTipKind.Hover) CloseDocTip();
            };
            // Scrolling moves the text under a tooltip: a call tooltip is placed again, the others are closed.
            Editor.TextArea.TextView.ScrollOffsetChanged += (_, _) =>
            {
                if (_docTipKind == DocTipKind.Call) { _callTipKey = null; UpdateCallTip(); }
                else CloseDocTip();
            };
            Editor.LostFocus += (_, _) =>
            {
                _caretTipTimer.Stop();
                CloseDocTip();
            };
            DetachedFromVisualTree += (_, _) =>
            {
                _caretTipTimer.Stop();
                CloseDocTip();
            };

            SetText(string.Empty);
            IsModified = false;
        }

        // -----------------------------------------------------------
        // Documentation tooltips: the `///` comment above a class, field, property or method is shown as a tooltip
        // when the caret rests on its name, when the mouse hovers over it, and next to the completion list
        // (see FireCompletionData). Where a symbol is declared and what it is comes from NavigationEngine.
        // -----------------------------------------------------------

        private readonly DispatcherTimer _caretTipTimer;
        private Popup? _docTip;

        /// <summary>Why the tooltip is open: while the arguments of a call are typed (stays until the call is closed),
        /// because the caret rested on a symbol, or because the mouse hovers over one.</summary>
        private enum DocTipKind { None, Call, Rest, Hover }
        private DocTipKind _docTipKind;
        private (int ParenOffset, string Header)? _callTipKey;
        private string? _indexedSource;
        private ScriptSymbolIndex? _index;

        /// <summary>The symbol index of `source`, reused as long as the text does not change.</summary>
        private ScriptSymbolIndex IndexFor(string source)
        {
            if (_index == null || !string.Equals(_indexedSource, source, StringComparison.Ordinal))
            {
                _index = ScriptSymbolIndex.Build(source);
                _indexedSource = source;
            }
            return _index;
        }

        private bool TryGetDoc(int offset, out string header, out DocComment doc)
        {
            string source = Editor.Text;
            var symbol = NavigationEngine.TryResolveSymbol(source, offset, IndexFor(source));
            if (symbol?.Documentation is { } found)
            {
                header = symbol.Header;
                doc = found;
                return true;
            }
            header = string.Empty;
            doc = null!;
            return false;
        }

        private void ShowDocTip(DocTipKind kind, string header, DocComment doc, double x, double y)
        {
            CloseDocTip();
            var tip = DocToolTip.Create(header, doc);
            tip.PlacementTarget = Editor.TextArea.TextView;
            tip.Placement = PlacementMode.BottomEdgeAlignedLeft;
            tip.PlacementRect = new Rect(x, y, 1, 1);
            tip.IsOpen = true;
            _docTip = tip;
            _docTipKind = kind;
        }

        private void CloseDocTip()
        {
            _callTipKey = null;
            _docTipKind = DocTipKind.None;
            if (_docTip == null) return;
            _docTip.IsOpen = false;
            _docTip = null;
        }

        /// <summary>Caret moved or text changed: keep (or update) the tooltip of an open call, otherwise close a resting-caret tooltip
        /// and start waiting for the caret to rest on a symbol.</summary>
        private void OnCaretOrTextChanged()
        {
            bool inCall = UpdateCallTip();
            if (_docTipKind == DocTipKind.Rest) CloseDocTip();
            _caretTipTimer.Stop();
            if (!inCall) _caretTipTimer.Start();
        }

        /// <summary>While the arguments of `Foo(` / `new Foo(` are typed the documentation of the called method or constructor stays open (above the line, so it does
        /// not cover the completion list) until the call is closed with `)`. Returns whether the caret is inside a call at all.</summary>
        private bool UpdateCallTip()
        {
            if (!Editor.IsKeyboardFocusWithin && _docTipKind != DocTipKind.Call) return false;
            string source = Editor.Text;
            var call = NavigationEngine.FindOpenCall(source, Editor.CaretOffset);
            if (call == null)
            {
                if (_docTipKind == DocTipKind.Call) CloseDocTip();
                return false;
            }

            var symbol = NavigationEngine.TryResolveCall(source, call, IndexFor(source));
            if (symbol?.Documentation is not { } doc)
            {
                if (_docTipKind == DocTipKind.Call) CloseDocTip();
                return true;
            }

            var key = (call.ParenOffset, symbol.Header);
            if (_docTipKind == DocTipKind.Call && _callTipKey == key) return true; // same call, same overload: leave it alone

            var textView = Editor.TextArea.TextView;
            var line = Editor.Document.GetLineByOffset(call.ParenOffset);
            var nameLocation = Editor.Document.GetLocation(Math.Max(line.Offset, call.ParenOffset - 1));
            var lineTop = textView.GetVisualPosition(new TextViewPosition(nameLocation), VisualYPosition.LineTop) - textView.ScrollOffset;
            var lineBottom = textView.GetVisualPosition(new TextViewPosition(nameLocation), VisualYPosition.LineBottom) - textView.ScrollOffset;

            ShowDocTip(DocTipKind.Call, symbol.Header, doc, lineTop.X, lineTop.Y);
            if (_docTip != null)
            {
                _docTip.Placement = PlacementMode.TopEdgeAlignedLeft;
                _docTip.PlacementRect = new Rect(lineTop.X, lineTop.Y, 1, Math.Max(1, lineBottom.Y - lineTop.Y));
            }
            _callTipKey = key;
            return true;
        }

        private void ShowCaretDocTip()
        {
            if (!Editor.IsKeyboardFocusWithin || _completionWindow != null || Editor.SelectionLength > 0) return;
            if (!TryGetDoc(Editor.CaretOffset, out string header, out DocComment doc)) return;

            var textView = Editor.TextArea.TextView;
            var below = textView.GetVisualPosition(Editor.TextArea.Caret.Position, VisualYPosition.LineBottom) - textView.ScrollOffset;
            ShowDocTip(DocTipKind.Rest, header, doc, below.X, below.Y + 2);
        }

        private void TextView_PointerHover(object? sender, PointerEventArgs e)
        {
            var textView = Editor.TextArea.TextView;
            var position = Editor.GetPositionFromPoint(e.GetPosition(Editor));
            if (position == null) return;

            // The identifier under the mouse, and only if the mouse really is over its text (not in the empty space behind the line).
            int offset = Editor.Document.GetOffset(position.Value.Location);
            var (start, end) = IdentifierAround(offset);
            if (start >= end) return;
            var startPos = new TextViewPosition(Editor.Document.GetLocation(start));
            var endPos = new TextViewPosition(Editor.Document.GetLocation(end));
            var left = textView.GetVisualPosition(startPos, VisualYPosition.LineTop) - textView.ScrollOffset;
            var right = textView.GetVisualPosition(endPos, VisualYPosition.LineTop) - textView.ScrollOffset;
            var bottom = textView.GetVisualPosition(startPos, VisualYPosition.LineBottom) - textView.ScrollOffset;
            var mouse = e.GetPosition(textView);
            if (mouse.X < left.X || mouse.X > right.X || mouse.Y < left.Y || mouse.Y > bottom.Y) return;

            if (!TryGetDoc(start, out string header, out DocComment doc)) return;
            ShowDocTip(DocTipKind.Hover, header, doc, mouse.X + 8, mouse.Y + 18);
        }

        /// <summary>Start and end offsets of the identifier that contains or touches `offset` (start == end: none).</summary>
        private (int Start, int End) IdentifierAround(int offset)
        {
            var document = Editor.Document;
            bool IsIdent(char c) => char.IsLetterOrDigit(c) || c == '_';
            int start = offset, end = offset;
            while (start > 0 && IsIdent(document.GetCharAt(start - 1))) start--;
            while (end < document.TextLength && IsIdent(document.GetCharAt(end))) end++;
            return (start, end);
        }

        // -----------------------------------------------------------
        // Text-Zugriff
        // -----------------------------------------------------------

        public string GetText() => Editor.Text;

        public void SetText(string text)
        {
            Editor.Text = text;
            RecomputeHighlighting();
            _diagnosticsTimer.Stop();
            _diagnosticsTimer.Start();
        }

        /// <summary>Setzt den Editor-Text zurück und verwirft dabei auch
        /// Haltepunkte/Diagnostik/Hervorhebung - für "neue Datei"/"andere
        /// Datei geöffnet" im Host (anders als SetText, das bewusst NICHTS
        /// von alldem verwirft).</summary>
        public void ResetTo(string text, string? filePath)
        {
            FilePath = filePath;
            _breakpoints.Clear();
            _diagnostics = new List<Diagnostic>();
            _highlightedLine = null;
            _lineBackground.HighlightedLine = null;
            RefreshBreakpointDisplay();
            _loading = true;
            try { SetText(text); }
            finally { _loading = false; }
            SetModified(false);
            BreakpointsChanged?.Invoke();
            DiagnosticsChanged?.Invoke();
        }

        private void Editor_TextChanged(object? sender, EventArgs e)
        {
            if (_docTipKind == DocTipKind.Hover) CloseDocTip();
            OnCaretOrTextChanged();
            if (!_loading) SetModified(true);
            _diagnosticsTimer.Stop();
            _diagnosticsTimer.Start();
            _highlightTimer.Stop();
            _highlightTimer.Start();

            // Läuft bei JEDER Textänderung, auch Löschen (Backspace/Entf) -
            // TextEntered (siehe unten) feuert NUR bei tatsächlich
            // eingefügtem Text, sieht Löschungen also gar nicht. Ein bereits
            // offenes Popup hier EXPLIZIT neu berechnen und bei Bedarf
            // schließen, statt uns auf AvalonEdits eigene interne "beim
            // Weitertippen filtern"-Logik zu verlassen - genau DAS war
            // vermutlich die Ursache dafür, dass die Liste oft weder
            // zuverlässig aufging noch zuverlässig wieder zuging.
            if (_completionWindow != null)
                ShowOrUpdateCompletion(closeIfEmpty: true);
        }

        // -----------------------------------------------------------
        // Autovervollständigung (IntelliSense) - siehe ScriptSymbolIndex/
        // CompletionEngine für die eigentliche Logik, hier nur die UI-
        // Anbindung an AvalonEdits CompletionWindow. Tastatursteuerung
        // (Pfeiltasten/Enter/Tab/Escape) im offenen Fenster übernimmt
        // AvalonEdit vollständig selbst - das fortlaufende Eingrenzen der
        // Liste beim Weitertippen dagegen NICHT verlässlich genug (siehe
        // Editor_TextChanged), deshalb wird bei jeder Änderung explizit neu
        // gerechnet: Editor_TextEntered öffnet (nur am Anfang eines
        // Bezeichners bzw. nach '.'), Editor_TextChanged hält ein bereits
        // offenes Popup synchron zum aktuellen Text und schließt es, sobald
        // nichts mehr passt.
        // -----------------------------------------------------------

        private void Editor_KeyDown(object? sender, KeyEventArgs e)
        {
            // Strg+Leertaste: Vervollständigung manuell anstoßen, auch ohne
            // vorangehenden '.' (allgemeine Bezeichner-Vervollständigung).
            if (e.Key == Key.Escape) CloseDocTip();
            if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.Control)
            {
                ShowOrUpdateCompletion(closeIfEmpty: true);
                e.Handled = true;
            }
            else if (e.Key == Key.F12 && e.KeyModifiers == KeyModifiers.None)
            {
                GoToDefinition();
                e.Handled = true;
            }
            else if (e.Key == Key.C && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift)) // layoutunabhängig (Strg+/ gibt es auf deutschen Tastaturen nicht)
            {
                ToggleComment();
                e.Handled = true;
            }
        }

        private void Editor_TextEntered(object? sender, TextInputEventArgs e)
        {
            // Ein bereits offenes Popup wird von Editor_TextChanged
            // aktualisiert (das feuert für JEDE Änderung, auch diese
            // Einfügung hier - doppeltes Berechnen für dasselbe Zeichen wird
            // dadurch vermieden).
            if (_completionWindow != null) return;
            if (string.IsNullOrEmpty(e.Text)) return;

            char c = e.Text[^1];
            bool isIdentifierChar = char.IsLetter(c) || c == '_';
            if (c != '.' && !isIdentifierChar) return;

            if (isIdentifierChar)
            {
                // Nur am ANFANG eines Bezeichners auslösen (das Zeichen davor
                // ist selbst kein Bezeichner-Zeichen) - sonst würde jeder
                // weitere Buchstabe mitten in einem bereits fertig getippten
                // Wort erneut ein Popup aufreißen.
                int before = Editor.CaretOffset - 2;
                if (before >= 0)
                {
                    char prev = Editor.Document.GetCharAt(before);
                    if (char.IsLetterOrDigit(prev) || prev == '_') return;

                }
            }

            ShowOrUpdateCompletion(closeIfEmpty: true);
        }

        private void ShowOrUpdateCompletion(bool closeIfEmpty)
        {
            string source = Editor.Text;
            int offset = Editor.CaretOffset;
            var index = ScriptSymbolIndex.Build(source);
            var items = CompletionEngine.GetSuggestions(source, offset, index);

            if (items.Count == 0)
            {
                if (closeIfEmpty) _completionWindow?.Close();
                return;
            }

            items = items.OrderByDescending(i => i.Score).ToList();

            // Bereits getipptes Präfix (Bezeichner-Zeichen unmittelbar vor
            // dem Cursor) - AvalonEdit soll das ERSETZEN, nicht nur dahinter
            // einfügen (dieselbe Präfix-Logik wie vorher in AcceptCompletion).
            int start = offset - 1;
            while (start >= 0 && (char.IsLetterOrDigit(source[start]) || source[start] == '_')) start--;
            start++;

            _completionWindow?.Close();
            var window = new CompletionWindow(Editor.TextArea) { StartOffset = start, EndOffset = offset };
            window.CompletionList.ListBox.Background = EditorTheme.DarkSurface;
            window.CompletionList.ListBox.Foreground = EditorTheme.Text;
            foreach (var item in items)
                window.CompletionList.CompletionData.Add(new FireCompletionData(item));

            if (items[0].Score > 0.6f)
                window.CompletionList.SelectedItem = window.CompletionList.CompletionData[0];

            window.Closed += (_, _) =>
            {
                if (_completionWindow == window) _completionWindow = null;
            };
            _completionWindow = window;
            window.Show();
        }

        // -----------------------------------------------------------
        // Syntax-Highlighting/Fehler-Unterkringelung - siehe
        // EditorRendering.HighlightingColorizer für die eigentliche
        // Zeichen-Logik, hier nur Neuberechnen + Neuzeichnen anstoßen.
        // -----------------------------------------------------------

        private ISet<string>? _conditionalSymbols;

        /// <summary>The symbols of `#if` for the configuration this script is built with (the nearest fire.native.json: engine, target, defines; without one the VM on this machine) -
        /// the branches that are not taken are greyed out. Cached; <see cref="InvalidateConditionalSymbols"/> after the settings changed.</summary>
        private ISet<string> ConditionalSymbolsForView()
        {
            if (_conditionalSymbols != null) return _conditionalSymbols;
            try
            {
                var config = FilePath != null ? fire.Native.NativeConfig.FindFor(FilePath) : new fire.Native.NativeConfig();
                bool native = string.Equals(config.Engine, "native", StringComparison.OrdinalIgnoreCase);
                var target = config.ResolveTarget();
                _conditionalSymbols = native
                    ? fire.Compiler.ConditionalSymbols.For(target, "native", target.FloatWidth, target.Native.Defines)
                    : fire.Compiler.ConditionalSymbols.For(null, fire.Compiler.ConditionalSymbols.DefaultEngine, null, target.Native.Defines);
            }
            catch (Exception)
            {
                _conditionalSymbols = fire.Compiler.ConditionalSymbols.For(null);   // an unreadable configuration: the build reports it, the editor assumes the defaults
            }
            return _conditionalSymbols;
        }

        /// <summary>Checks the script again now (after the installed packages changed).</summary>
        public void Revalidate() => RunDiagnostics();

        public void InvalidateConditionalSymbols()
        {
            _conditionalSymbols = null;
            RecomputeHighlighting();
        }

        private void RecomputeHighlighting()
        {
            string text = Editor.Text;
            _colorizer.Spans = SyntaxHighlighter.Highlight(text, ConditionalSymbolsForView());
            _errorSquiggles.ErrorLines = _diagnostics.Select(d => d.Line).ToHashSet();
            Editor.TextArea.TextView.Redraw();
        }

        /// <summary>Läuft debounced nach Textänderungen (siehe
        /// Editor_TextChanged/_diagnosticsTimer): Parser+Resolver+Compiler
        /// auf dem aktuellen Editor-Inhalt (siehe LiveDiagnostics.Analyze),
        /// aktualisiert Diagnostics (löst DiagnosticsChanged aus) und die
        /// unterkringelten Zeilen im Editor selbst.</summary>
        private void RunDiagnostics()
        {
            string source = Editor.Text;
            _diagnostics = DiagnosticsProvider(source);
            DiagnosticsChanged?.Invoke();
            RecomputeHighlighting();
        }

        // -----------------------------------------------------------
        // Strg+Klick-Navigation zu Definitionen/Includes (siehe
        // NavigationEngine für die eigentliche Auflösung, FileViewerWindow
        // für die Anzeige einer ANDEREN Datei) - unverändert gegenüber der
        // alten Fassung, nur die Offset-Ermittlung nutzt jetzt AvalonEdits
        // eigene, zuverlässige GetPositionFromPoint/GetOffset statt
        // TextPointer-Klimmzüge.
        // -----------------------------------------------------------

        private void Editor_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            var point = e.GetCurrentPoint(Editor);
            // a right click puts the caret under the mouse for the context menu
            if (point.Properties.IsRightButtonPressed) { _contextOffset = Editor.PlaceCaretForContextMenu(e); return; }

            // Nur Strg+Klick navigiert - ein normaler Klick muss weiterhin
            // ganz gewöhnlich den Cursor setzen/Text markieren können, ohne
            // versehentlich wegzuspringen.
            if (!point.Properties.IsLeftButtonPressed || (e.KeyModifiers & KeyModifiers.Control) == 0) return;

            var pos = Editor.GetPositionFromPoint(e.GetPosition(Editor));
            if (pos == null) return;

            int offset = Editor.Document.GetOffset(pos.Value.Location);
            if (GoToDefinitionAt(offset)) e.Handled = true;
        }

        /// <summary>Wohin ein Sprung zur Definition an `offset` führen würde, null = nirgends.</summary>
        private NavigationTarget? FindDefinitionAt(int offset)
        {
            string source = Editor.Text;
            var index = ScriptSymbolIndex.Build(source);
            return NavigationEngine.TryResolve(source, offset, index)
                ?? TryResolveAcrossIncludes(source, offset, index);
        }

        /// <summary>Springt zur Definition des Symbols unter dem Cursor (F12, Menü "Zu Definition springen").</summary>
        public void GoToDefinition() => GoToDefinitionAt(Editor.CaretOffset);

        /// <summary>Gibt es zum Symbol unter dem Cursor eine Definition? (für das Menü)</summary>
        public bool CanGoToDefinition() => FindDefinitionAt(Editor.CaretOffset) != null;

        private bool GoToDefinitionAt(int offset)
        {
            var target = FindDefinitionAt(offset);
            if (target == null) return false;

            if (target.PreludeName != null)
            {
                ShowPreludeSource(target.PreludeName, target.Line);
                return true;
            }

            if (target.FilePath == null)
            {
                SetCaretByLineColumn(target.Line - 1, 0);
                ScrollToLine(target.Line);
                Editor.Focus();
                return true;
            }

            OpenFileViewer(target.FilePath, target.Line);
            return true;
        }

        // -----------------------------------------------------------
        // Kontextmenü (Rechtsklick)
        // -----------------------------------------------------------

        private int _contextOffset;

        private ContextMenu BuildContextMenu()
        {
            var entries = new List<EditorCommands.Entry?>
            {
                new() { Header = "Go to _Definition", Gesture = "F12 / Ctrl+Click", Execute = () => GoToDefinitionAt(_contextOffset), Enabled = () => FindDefinitionAt(_contextOffset) != null },
                null,
            };
            entries.AddRange(EditorCommands.StandardEntries(Editor, Find));
            entries.Add(null);
            entries.Add(new() { Header = "Toggle _Comment", Gesture = "Ctrl+Shift+C", Execute = ToggleComment });
            entries.Add(new() { Header = "Toggle _Breakpoint", Gesture = "F9", Execute = ToggleBreakpointAtCaret });
            return EditorCommands.BuildMenu(entries);
        }

        /// <summary>Schaltet `//` vor den Zeilen der Auswahl (bzw. der Cursor-Zeile) ein oder aus.</summary>
        public void ToggleComment()
        {
            var doc = Editor.Document;
            var lines = Editor.SelectedLines();
            var texts = lines.Select(l => doc.GetText(l.Offset, l.Length)).ToList();
            var filled = Enumerable.Range(0, lines.Count).Where(k => texts[k].Trim().Length > 0).ToList();
            if (filled.Count == 0) return;

            bool allCommented = filled.All(k => texts[k].TrimStart().StartsWith("//"));
            int indent = filled.Min(k => texts[k].Length - texts[k].TrimStart().Length);

            doc.BeginUpdate();
            try
            {
                for (int k = lines.Count - 1; k >= 0; k--)
                {
                    if (!filled.Contains(k)) continue;
                    int lead = texts[k].Length - texts[k].TrimStart().Length;
                    if (allCommented)
                    {
                        int len = texts[k].Substring(lead).StartsWith("// ") ? 3 : 2;
                        doc.Remove(lines[k].Offset + lead, len);
                    }
                    else
                    {
                        doc.Insert(lines[k].Offset + indent, "// ");
                    }
                }
            }
            finally { doc.EndUpdate(); }
            Editor.Focus();
        }

        /// <summary>Zeigt eine eingebaute Prelude (Standardbibliothek oder die einer per
        /// `#import` zugeschalteten Erweiterung, siehe ScriptSymbolIndex.PreludeSourceOf) in einem
        /// schreibgeschützten Fenster, zu `line` gescrollt - für "zu Definition springen" auf
        /// `List`/`Framebuffer`/etc., die NICHT im aktuellen Dokument selbst stehen. Pro Prelude
        /// gibt es nur EIN Fenster, weitere Sprünge benutzen es wieder.</summary>
        private void ShowPreludeSource(string preludeName, int line) =>
            FileViewerWindow.ShowPrelude(preludeName, line, Dialogs.WindowOf(this));

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
        /// <summary>Ein Sprung führt in eine ANDERE Datei (z.B. per `#include`): der Host öffnet sie
        /// in einem Tab und springt zur Zeile (Parameter: vollständiger Pfad, 1-basierte Zeile).
        /// Ohne Abonnent zeigt ein schreibgeschütztes Fenster die Datei.</summary>
        public event Action<string, int>? OpenFileRequested;

        private void OpenFileViewer(string pathFromTarget, int line)
        {
            string resolved = pathFromTarget;
            if (!Path.IsPathRooted(resolved))
            {
                if (FilePath == null)
                {
                    _ = Dialogs.Message(Dialogs.WindowOf(this), "This file has to be saved before '#include' paths can be resolved.", "Cannot jump");
                    return;
                }
                string? dir = Path.GetDirectoryName(FilePath);
                resolved = Path.GetFullPath(Path.Combine(dir ?? ".", pathFromTarget));
            }

            if (OpenFileRequested != null)
            {
                OpenFileRequested.Invoke(resolved, line);
                return;
            }

            var viewer = new FileViewerWindow();
            viewer.LoadFile(resolved, line);
            if (Dialogs.WindowOf(this) is { } owner) viewer.Show(owner); else viewer.Show();
        }

        // -----------------------------------------------------------
        // Cursor/Scroll - AvalonEdit zählt intern überall 1-basiert
        // (Caret.Line, DocumentLine.LineNumber); die 0-basierte Zeile bei
        // SetCaretByLineColumn ist die einzige Umrechnungsstelle (siehe
        // Klassendoku ganz oben).
        // -----------------------------------------------------------

        public int GetCaretLine() => Editor.TextArea.Caret.Line;

        public void SetCaretByLineColumn(int line, int column)
        {
            if (Editor.Document.LineCount == 0) return;
            int docLine = Math.Max(1, Math.Min(line + 1, Editor.Document.LineCount));
            var lineObj = Editor.Document.GetLineByNumber(docLine);
            int col = Math.Max(0, Math.Min(column, lineObj.Length));

            Editor.TextArea.Caret.Line = docLine;
            Editor.TextArea.Caret.Column = col + 1; // AvalonEdit-Spalten sind 1-basiert
            Editor.TextArea.Caret.BringCaretToView();
        }

        public void ScrollToLine(int line) => Editor.ScrollToLine(line);

        public void FocusEditor() => Editor.Focus();

        // -----------------------------------------------------------
        // Bearbeiten (Menü des Hauptfensters, Kontextmenü)
        // -----------------------------------------------------------

        private AvaloniaEdit.Search.SearchPanel? _searchPanel;

        public bool CanUndo => Editor.Document.UndoStack.CanUndo;
        public bool CanRedo => Editor.Document.UndoStack.CanRedo;
        public bool HasSelection => Editor.SelectionLength > 0;
        public int LineCount => Editor.Document.LineCount;

        public void Undo() { Editor.Undo(); Editor.Focus(); }
        public void Redo() { Editor.Redo(); Editor.Focus(); }
        public void Cut() { Editor.Cut(); Editor.Focus(); }
        public void Copy() { Editor.Copy(); Editor.Focus(); }
        public void Paste() { Editor.Paste(); Editor.Focus(); }
        public void Delete() { Editor.Delete(); Editor.Focus(); }
        public void SelectAll() { Editor.SelectAll(); Editor.Focus(); }
        public void GoToLine(int line) => Editor.GoToLine(line);

        public void Find()
        {
            Editor.Focus();
            _searchPanel?.Open();
        }

        public void FindNext() => _searchPanel?.FindNext();
        public void FindPrevious() => _searchPanel?.FindPrevious();

        // -----------------------------------------------------------
        // Haltepunkte (Rand-Klick oder F9 im Host, siehe
        // ToggleBreakpointAtCaret)
        // -----------------------------------------------------------

        public void ToggleBreakpointAtCaret() => ToggleBreakpoint(GetCaretLine());

        private void ToggleBreakpoint(int line)
        {
            if (!_breakpoints.Remove(line))
                _breakpoints.Add(line);
            RefreshBreakpointDisplay();
            BreakpointsChanged?.Invoke();
        }

        public void ClearBreakpoints()
        {
            if (_breakpoints.Count == 0) return;
            _breakpoints.Clear();
            RefreshBreakpointDisplay();
            BreakpointsChanged?.Invoke();
        }

        private void RefreshBreakpointDisplay()
        {
            _lineBackground.Breakpoints = _breakpoints;
            _breakpointMargin.Breakpoints = _breakpoints;
            _breakpointMargin.RedrawMargin();
            Editor.TextArea.TextView.Redraw();
        }
    }
}
