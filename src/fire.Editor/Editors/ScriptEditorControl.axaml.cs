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
    /// <summary>A self-contained, reusable editor control:
    /// AvalonEdit TextEditor + syntax highlighting + auto-completion +
    /// Ctrl+click navigation + clickable breakpoint margin + live diagnostics -
    /// EXACTLY ONE source text per instance. Used both by the old single-file window
    /// (MainWindow, one instance) and by the tabbed project window
    /// (ProjectWindow, one instance PER open tab), so that editing
    /// logic does not have to be maintained twice.
    ///
    /// On AvaloniaEdit (the Avalonia version of AvalonEdit; see EditorRendering.cs for
    /// the new rendering building blocks): the old approach (highlighting = rebuilding and swapping
    /// the COMPLETE FlowDocument debounced while
    /// typing) was the root of almost all bugs of several debug rounds - selection/
    /// cursor collapsed on the swap, race between typing and
    /// background highlighting, a genuine feedback loop, scroll
    /// position jumped for long files. AvalonEdit has its own
    /// TextDocument model with REAL character offsets (GetOffset/GetLocation)
    /// instead of WPF's TextPointer/Paragraph class hierarchy, and highlighting
    /// runs purely when DRAWING (DocumentColorizingTransformer) - the
    /// document itself is never touched for it, which eliminates this whole
    /// class of bugs structurally, instead of patching it case by case.
    ///
    /// Deliberately NO knowledge of its own about DebugSession/compiling/running -
    /// that remains the business of the respective host window (see MainWindow/
    /// ProjectWindow), which orchestrates this instance (query breakpoints,
    /// set HighlightedLine, call GetText() when compiling, ...).
    ///
    /// LINE COUNTING: outwardly (public interface) UNCHANGED as
    /// with the old RichTextBox version - SetCaretByLineColumn takes a
    /// 0-based line, ScrollToLine/HighlightedLine/Breakpoints/
    /// GetCaretLine are 1-based (matching Chunk.MarkLine/
    /// GetLocation) - so MainWindow/ProjectWindow remain usable
    /// unchanged. AvalonEdit itself counts INTERNALLY everywhere 1-based
    /// (Caret.Line, DocumentLine.LineNumber, ...) - the conversion at the
    /// 0-based SetCaretByLineColumn boundary is the only place that
    /// has to take that into account.</summary>
    public partial class ScriptEditorControl : UserControl, IDocumentView
    {
        /// <summary>The file path of this editor, if it has been
        /// saved/opened once - `null` for a new, unsaved
        /// document. Only needed for relative `#include` path resolution in
        /// Ctrl+click navigation (see TryResolveAcrossIncludes/
        /// OpenFileViewer) - the control itself NEVER reads/writes
        /// from/to the disk on its own, that remains the business of the host
        /// window (see GetText/SetText).</summary>
        private string? _filePath;
        public string? FilePath { get => _filePath; set { _filePath = value; _conditionalSymbols = null; } }

        private readonly HashSet<int> _breakpoints = new();

        /// <summary>The current breakpoint lines (1-based, as in the
        /// rest of the editor) - READ-ONLY; to change use ToggleBreakpointAtCaret/
        /// ClearBreakpoints (which automatically raises BreakpointsChanged
        /// and updates margin + line background).</summary>
        public IReadOnlySet<int> Breakpoints => _breakpoints;

        /// <summary>Fires whenever the breakpoint set has changed
        /// (margin click/ToggleBreakpointAtCaret/ClearBreakpoints) - the host
        /// usually has to react to it with DebugSession.UpdateBreakpoints.</summary>
        public event Action? BreakpointsChanged;

        /// <summary>The live diagnostics errors currently shown in the editor
        /// (see LiveDiagnostics) - READ-ONLY, recomputed internally, debounced, after
        /// every text change (see RunDiagnostics).</summary>
        public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

        /// <summary>How the diagnostics are computed (see RunDiagnostics) -
        /// default: simple single-file analysis (LiveDiagnostics.Analyze),
        /// suitable for a single, free-standing document (see
        /// MainWindow). A host with several related files
        /// (see ProjectWindow) sets a project-wide variant
        /// (LiveDiagnostics.AnalyzeInProject) here instead, so that a valid
        /// reference to a class from ANOTHER project file is not
        /// wrongly marked as an error.</summary>
        public Func<string, List<Diagnostic>> DiagnosticsProvider { get; set; }

        /// <summary>Directory of this document (for relative `#include`
        /// paths) - `null` as long as it has never been saved/opened (then the
        /// working directory applies).</summary>
        public string? BaseDirectory => FilePath == null ? null : Path.GetDirectoryName(Path.GetFullPath(FilePath));

        /// <summary>True as soon as the text has been changed since loading/the last save
        /// (for the asterisk in the tab title and the prompt when
        /// closing).</summary>
        public bool IsModified { get; private set; }
        public bool IsReadOnly => false;

        /// <summary>Fires when IsModified has changed.</summary>
        public event Action? ModifiedChanged;

        /// <summary>The host has saved the current text.</summary>
        public void MarkSaved() => SetModified(false);

        private void SetModified(bool value)
        {
            if (IsModified == value) return;
            IsModified = value;
            ModifiedChanged?.Invoke();
        }

        private bool _loading;

        /// <summary>Fires whenever Diagnostics has changed - the
        /// host usually shows that in a list of its own.</summary>
        public event Action? DiagnosticsChanged;

        /// <summary>Fires on EVERY cursor movement with the new 1-based
        /// line - for a status bar display in the host.</summary>
        public event Action<int>? CaretLineChanged;

        /// <summary>The line currently halted by the debugger (highlighted
        /// yellow), `null` if none - set by the host (see
        /// MainWindow/ProjectWindow after every step/stop). Unlike
        /// before, ONLY sets the background renderer and triggers
        /// a repaint (TextView.Redraw) - NO document rebuild
        /// needed any more, see the class documentation.</summary>
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

        // Highlighting runs debounced (instead of lexing anew on every
        // keystroke) - pure lexing is fast, but with very fast
        // typing it should still not be lexed anew for EVERY
        // intermediate state.
        private readonly DispatcherTimer _highlightTimer;

        // Live error analysis (see LiveDiagnostics) - runs debounced like
        // the highlighting, but with a LONGER delay (parser +
        // resolver + compiler are noticeably more expensive than pure lexing) and
        // SEPARATE from it, so that fast typing does not trigger a complete
        // compile attempt on every intermediate state.
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
            // Index 0 = far left, thus before the line-number column
            // (inserted automatically by ShowLineNumbers="True") - breakpoint dot,
            // then line number, then text, as is common in most IDEs.
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
        private int _indexedVersion;
        private ScriptSymbolIndex? _index;

        /// <summary>Counts the changes of any script (and of the project): the symbols that come from other files of the project are looked up again after one.</summary>
        public static int TextVersion { get; private set; }
        public static void NoteProjectChanged() => TextVersion++;

        /// <summary>The other files of the project the document belongs to (path and text), and the libraries it imports: their classes, enums and namespaces are known here without
        /// `#include`, as they are to the build. null: the document stands alone.</summary>
        public Func<IReadOnlyList<(string Path, string Text)>>? ProjectFilesProvider { get; set; }

        /// <summary>The symbol index of `source`, reused as long as the text (and the project) does not change.</summary>
        private ScriptSymbolIndex IndexFor(string source)
        {
            if (_index == null || _indexedVersion != TextVersion || !string.Equals(_indexedSource, source, StringComparison.Ordinal))
            {
                _index = ScriptSymbolIndex.Build(source, Array.Empty<string>(), ProjectFilesProvider?.Invoke() ?? Array.Empty<(string, string)>());
                _indexedSource = source;
                _indexedVersion = TextVersion;
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

        /// <summary>Resets the editor text and also discards
        /// breakpoints/diagnostics/highlighting - for "new file"/"another
        /// file opened" in the host (unlike SetText, which deliberately discards NOTHING
        /// of that).</summary>
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
            TextVersion++;
            if (_docTipKind == DocTipKind.Hover) CloseDocTip();
            OnCaretOrTextChanged();
            if (!_loading) SetModified(true);
            _diagnosticsTimer.Stop();
            _diagnosticsTimer.Start();
            _highlightTimer.Stop();
            _highlightTimer.Start();

            // Runs on EVERY text change, also deletion (Backspace/Del) -
            // TextEntered (see below) fires ONLY for text
            // actually inserted, so it does not see deletions at all. Recompute an already
            // open popup here EXPLICITLY and close it if necessary,
            // instead of relying on AvalonEdit's own internal "filter while
            // typing on" logic - exactly THAT was
            // presumably the cause of the list often neither opening
            // reliably nor closing reliably again.
            if (_completionWindow != null)
                ShowOrUpdateCompletion(closeIfEmpty: true);
        }

        // -----------------------------------------------------------
        // Auto-completion (IntelliSense) - see ScriptSymbolIndex/
        // CompletionEngine for the actual logic, here only the UI
        // connection to AvalonEdit's CompletionWindow. Keyboard control
        // (arrow keys/Enter/Tab/Escape) in the open window is handled
        // entirely by AvalonEdit itself - the continuous narrowing of the
        // list while typing on, however, NOT reliably enough (see
        // Editor_TextChanged), which is why everything is explicitly
        // recomputed on every change: Editor_TextEntered opens (only at the start of an
        // identifier or after '.'), Editor_TextChanged keeps an already
        // open popup in sync with the current text and closes it as soon as
        // nothing matches any more.
        // -----------------------------------------------------------

        private void Editor_KeyDown(object? sender, KeyEventArgs e)
        {
            // Ctrl+Space: trigger completion manually, also without a
            // preceding '.' (general identifier completion).
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
            else if (e.Key == Key.C && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift)) // layout-independent (Ctrl+/ does not exist on German keyboards)
            {
                ToggleComment();
                e.Handled = true;
            }
        }

        private void Editor_TextEntered(object? sender, TextInputEventArgs e)
        {
            // An already open popup is updated by Editor_TextChanged
            // (which fires for EVERY change, also this
            // insertion here - computing twice for the same character is
            // thereby avoided).
            if (_completionWindow != null) return;
            if (string.IsNullOrEmpty(e.Text)) return;

            char c = e.Text[^1];
            bool isIdentifierChar = char.IsLetter(c) || c == '_';
            if (c != '.' && !isIdentifierChar) return;

            if (isIdentifierChar)
            {
                // Trigger only at the START of an identifier (the character before it
                // is itself not an identifier character) - otherwise every
                // further letter in the middle of an already fully typed
                // word would tear open a popup again.
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
            var index = IndexFor(source);
            var items = CompletionEngine.GetSuggestions(source, offset, index);

            if (items.Count == 0)
            {
                if (closeIfEmpty) _completionWindow?.Close();
                return;
            }

            items = items.OrderByDescending(i => i.Score).ToList();

            // Already typed prefix (identifier characters immediately before
            // the cursor) - AvalonEdit is to REPLACE that, not only
            // insert behind it (the same prefix logic as before in AcceptCompletion).
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
        // Syntax highlighting/error squiggles - see
        // EditorRendering.HighlightingColorizer for the actual
        // character logic, here only trigger recomputing + redrawing.
        // -----------------------------------------------------------

        private ISet<string>? _conditionalSymbols;

        /// <summary>More symbols of `#if` (those that the settings of the project add), asked whenever the symbols are computed; null: none.</summary>
        public Func<IReadOnlyList<string>?>? ExtraDefines { get; set; }

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
            if (ExtraDefines?.Invoke() is { Count: > 0 } extra)
            {
                var all = new HashSet<string>(_conditionalSymbols);
                foreach (var d in extra) all.Add(d);
                _conditionalSymbols = all;
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

        /// <summary>Runs debounced after text changes (see
        /// Editor_TextChanged/_diagnosticsTimer): parser+resolver+compiler
        /// on the current editor content (see LiveDiagnostics.Analyze),
        /// updates Diagnostics (raises DiagnosticsChanged) and the
        /// squiggled lines in the editor itself.</summary>
        private void RunDiagnostics()
        {
            string source = Editor.Text;
            _diagnostics = DiagnosticsProvider(source);
            DiagnosticsChanged?.Invoke();
            RecomputeHighlighting();
        }

        // -----------------------------------------------------------
        // Ctrl+click navigation to definitions/includes (see
        // NavigationEngine for the actual resolution, FileViewerWindow
        // for showing ANOTHER file) - unchanged compared to the
        // old version, only the offset determination now uses AvalonEdit's
        // own, reliable GetPositionFromPoint/GetOffset instead of
        // TextPointer contortions.
        // -----------------------------------------------------------

        private void Editor_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            var point = e.GetCurrentPoint(Editor);
            // a right click puts the caret under the mouse for the context menu
            if (point.Properties.IsRightButtonPressed) { _contextOffset = Editor.PlaceCaretForContextMenu(e); return; }

            // Only Ctrl+click navigates - a normal click must still be able
            // to set the cursor/select text quite normally, without
            // accidentally jumping away.
            if (!point.Properties.IsLeftButtonPressed || (e.KeyModifiers & KeyModifiers.Control) == 0) return;

            var pos = Editor.GetPositionFromPoint(e.GetPosition(Editor));
            if (pos == null) return;

            int offset = Editor.Document.GetOffset(pos.Value.Location);
            if (GoToDefinitionAt(offset)) e.Handled = true;
        }

        /// <summary>Where a jump to the definition at `offset` would lead, null = nowhere.</summary>
        private NavigationTarget? FindDefinitionAt(int offset)
        {
            string source = Editor.Text;
            var index = IndexFor(source);
            return NavigationEngine.TryResolve(source, offset, index)
                ?? TryResolveAcrossIncludes(source, offset, index);
        }

        /// <summary>Jumps to the definition of the symbol under the cursor (F12, menu "Go to definition").</summary>
        public void GoToDefinition() => GoToDefinitionAt(Editor.CaretOffset);

        /// <summary>Is there a definition for the symbol under the cursor? (for the menu)</summary>
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
        // Context menu (right click)
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

        /// <summary>Toggles `//` in front of the lines of the selection (or the cursor line) on or off.</summary>
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

        /// <summary>Shows a built-in prelude (standard library or that of an extension switched on via
        /// `#import`, see ScriptSymbolIndex.PreludeSourceOf) in a
        /// read-only window, scrolled to `line` - for "go to definition" on
        /// `List`/`Framebuffer`/etc., which are NOT in the current document itself. There is
        /// only ONE window per prelude, further jumps reuse it.</summary>
        private void ShowPreludeSource(string preludeName, int line) =>
            FileViewerWindow.ShowPrelude(preludeName, line, Dialogs.WindowOf(this));

        /// <summary>Falls back to every file included via `#include` in THIS document
        /// if NavigationEngine.TryResolve found nothing in the
        /// document itself - needs a saved
        /// file path (see FilePath) to be able to resolve relative include paths at all
        /// (a document never saved has no
        /// directory relative to which that would make sense).</summary>
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
                if (resolved.EndsWith(".fxml", StringComparison.OrdinalIgnoreCase)) continue; // the markup of an interface: no script text to look into

                string includedSource;
                try { includedSource = File.ReadAllText(resolved); }
                catch { continue; }

                var includedIndex = ScriptSymbolIndex.Build(includedSource);
                var line = NavigationEngine.TryResolveInOtherFile(identifier, receiver, includedIndex);
                if (line != null) return new NavigationTarget(resolved, line.Value);
            }
            return null;
        }

        /// <summary>Opens `pathFromTarget` (either already absolute - from the
        /// includes fallback above - or still the RAW relative path directly
        /// from an `#include` line, see NavigationTarget documentation) in a
        /// new FileViewerWindow popup, scrolled to `line`.</summary>
        /// <summary>A jump leads into ANOTHER file (e.g. via `#include`): the host opens it
        /// in a tab and jumps to the line (parameters: full path, 1-based line).
        /// Without a subscriber a read-only window shows the file.</summary>
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
        // Cursor/scroll - AvalonEdit counts internally everywhere 1-based
        // (Caret.Line, DocumentLine.LineNumber); the 0-based line at
        // SetCaretByLineColumn is the only conversion point (see
        // the class documentation at the very top).
        // -----------------------------------------------------------

        public int GetCaretLine() => Editor.TextArea.Caret.Line;

        public void SetCaretByLineColumn(int line, int column)
        {
            if (Editor.Document.LineCount == 0) return;
            int docLine = Math.Max(1, Math.Min(line + 1, Editor.Document.LineCount));
            var lineObj = Editor.Document.GetLineByNumber(docLine);
            int col = Math.Max(0, Math.Min(column, lineObj.Length));

            Editor.TextArea.Caret.Line = docLine;
            Editor.TextArea.Caret.Column = col + 1; // AvalonEdit columns are 1-based
            Editor.TextArea.Caret.BringCaretToView();
        }

        public void ScrollToLine(int line) => Editor.ScrollToLine(line);

        public void FocusEditor() => Editor.Focus();

        // -----------------------------------------------------------
        // Edit (menu of the main window, context menu)
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
        // Breakpoints (margin click or F9 in the host, see
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
