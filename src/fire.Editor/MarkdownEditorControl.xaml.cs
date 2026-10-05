using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;

namespace fire.Editor
{
    /// <summary>How a Markdown document is opened: editable, read-only (editor and preview visible, no changes possible),
    /// or as a pure viewer (only the rendered page, the editor is hidden).</summary>
    public enum MarkdownViewMode { Edit, ReadOnly, Viewer }

    /// <summary>Editor für Markdown-Dokumente: AvalonEdit-Quelltext mit
    /// Markdown-Hervorhebung (siehe MarkdownColorizer; ```fire-Blöcke werden
    /// wie im Skript-Editor eingefärbt), Format-Knöpfe/-Kürzel, automatische
    /// Listenfortsetzung mit Enter und einer Live-Vorschau daneben (siehe
    /// MarkdownParser/MarkdownRenderer).
    ///
    /// Kennt wie ScriptEditorControl nichts vom Kompilieren/Ausführen - das
    /// Hauptfenster behandelt beide über IDocumentView gleich.</summary>
    public partial class MarkdownEditorControl : UserControl, IDocumentView
    {
        public string? FilePath { get; set; }
        public bool IsModified { get; private set; }
        public event Action? ModifiedChanged;
        public event Action<int>? CaretLineChanged;

        /// <summary>A link to a local file was clicked in the preview. Parameters: full path, anchor (heading, or null) and whether
        /// the file should open in a new tab (Ctrl+click). The host decides what happens (see MainWindow.HandleMarkdownLink).</summary>
        public event Action<string, string?, bool>? OpenFileRequested;

        private MarkdownViewMode _mode = MarkdownViewMode.Edit;

        /// <summary>Edit / read-only / viewer only. Set once after creation (and before showing the document).</summary>
        public MarkdownViewMode Mode
        {
            get => _mode;
            set
            {
                _mode = value;
                ApplyMode();
            }
        }

        public bool IsReadOnly => _mode != MarkdownViewMode.Edit;
        private bool ViewerOnly => _mode == MarkdownViewMode.Viewer;

        public string? BaseDirectory => FilePath == null ? null : Path.GetDirectoryName(Path.GetFullPath(FilePath));

        private readonly MarkdownColorizer _colorizer = new();
        private readonly HighlightingColorizer _fireColorizer = new();
        private readonly MarkdownRenderer _renderer = new();
        private readonly DispatcherTimer _previewTimer;
        private readonly DispatcherTimer _highlightTimer;
        private List<(int Start, int End)> _fireBlocks = new();
        private bool _loading;
        private GridLength _previewWidth = new(1, GridUnitType.Star);

        public MarkdownEditorControl()
        {
            InitializeComponent();

            Editor.Options.ConvertTabsToSpaces = true;
            Editor.Options.IndentationSize = 2;
            Editor.TextArea.TextView.LineTransformers.Add(_colorizer);
            Editor.TextArea.TextView.LineTransformers.Add(_fireColorizer);

            Editor.TextChanged += Editor_TextChanged;
            Editor.TextArea.Caret.PositionChanged += (_, _) => CaretLineChanged?.Invoke(GetCaretLine());
            Editor.PreviewKeyDown += Editor_PreviewKeyDown;
            Editor.PreviewMouseRightButtonDown += (_, e) => Editor.PlaceCaretForContextMenu(e);

            _searchPanel = ICSharpCode.AvalonEdit.Search.SearchPanel.Install(Editor);
            EditorTheme.Apply(Editor, _searchPanel);
            Editor.ContextMenu = EditorCommands.BuildMenu(new List<EditorCommands.Entry?>
            {
                new() { Header = "_Fett", Gesture = "Strg+B", Execute = () => Wrap("**"), Enabled = CanFormat },
                new() { Header = "_Kursiv", Gesture = "Strg+I", Execute = () => Wrap("*"), Enabled = CanFormat },
                new() { Header = "_Code", Gesture = "Strg+E", Execute = () => Wrap("`"), Enabled = CanFormat },
                new() { Header = "_Link", Gesture = "Strg+K", Execute = InsertLink, Enabled = CanFormat },
                new() { Header = "_Überschrift (Ebene wechseln)", Gesture = "Strg+H", Execute = CycleHeading, Enabled = CanFormat },
                null,
            }.Concat(EditorCommands.StandardEntries(Editor, Find)).ToList());

            _renderer.LinkClicked = HandleLink;

            _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); UpdatePreview(); };

            _highlightTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _highlightTimer.Tick += (_, _) => { _highlightTimer.Stop(); RecomputeFireHighlighting(); };

            RecomputeFences();
            UpdatePreview();
        }

        // -----------------------------------------------------------
        // IDocumentView
        // -----------------------------------------------------------

        public string GetText() => Editor.Text;
        public int GetCaretLine() => Editor.TextArea.Caret.Line;
        public void FocusEditor()
        {
            if (ViewerOnly) Preview.Focus();
            else Editor.Focus();
        }

        // -----------------------------------------------------------
        // Bearbeiten (Menü des Hauptfensters, Kontextmenü)
        // -----------------------------------------------------------

        private ICSharpCode.AvalonEdit.Search.SearchPanel? _searchPanel;

        public bool CanUndo => !IsReadOnly && Editor.Document.UndoStack.CanUndo;
        public bool CanRedo => !IsReadOnly && Editor.Document.UndoStack.CanRedo;
        public bool HasSelection => ViewerOnly ? !Preview.Selection.IsEmpty : Editor.SelectionLength > 0;
        public int LineCount => Editor.Document.LineCount;

        public void Undo() { Editor.Undo(); Editor.Focus(); }
        public void Redo() { Editor.Redo(); Editor.Focus(); }
        public void Cut() { Editor.Cut(); Editor.Focus(); }
        public void Copy()
        {
            if (ViewerOnly)
            {
                try { if (!Preview.Selection.IsEmpty) Clipboard.SetText(Preview.Selection.Text); }
                catch (System.Runtime.InteropServices.COMException) { /* clipboard locked by another program */ }
                return;
            }
            Editor.Copy();
            Editor.Focus();
        }
        public void Paste() { Editor.Paste(); Editor.Focus(); }
        public void Delete() { Editor.Delete(); Editor.Focus(); }
        public void SelectAll()
        {
            //if (ViewerOnly) { Preview.Focus(); Preview.SelectAll(); return; }
            Editor.SelectAll();
            Editor.Focus();
        }
        public void GoToLine(int line) => Editor.GoToLine(line);

        public void Find()
        {
            if (ViewerOnly) return;
            Editor.Focus();
            _searchPanel?.Open();
        }

        public void FindNext() => _searchPanel?.FindNext();
        public void FindPrevious() => _searchPanel?.FindPrevious();
        public void MarkSaved() => SetModified(false);

        public void ResetTo(string text, string? filePath)
        {
            FilePath = filePath;
            _loading = true;
            try { Editor.Text = text; }
            finally { _loading = false; }
            Editor.CaretOffset = 0;
            Editor.ScrollToHome();
            RecomputeFences();
            RecomputeFireHighlighting();
            UpdatePreview(keepScroll: false);
            SetModified(false);
        }

        public void SetText(string text) => Editor.Text = text;

        private void SetModified(bool value)
        {
            if (IsModified == value) return;
            IsModified = value;
            ModifiedChanged?.Invoke();
        }

        private void Editor_TextChanged(object? sender, EventArgs e)
        {
            if (!_loading) SetModified(true);
            RecomputeFences();
            Editor.TextArea.TextView.Redraw();
            _highlightTimer.Stop(); _highlightTimer.Start();
            if (PreviewVisible) { _previewTimer.Stop(); _previewTimer.Start(); }
        }

        // -----------------------------------------------------------
        // Hervorhebung
        // -----------------------------------------------------------

        private void RecomputeFences()
        {
            var blocks = new List<(int, int)>();
            _colorizer.FencedLines = MarkdownColorizer.ComputeFencedLines(Editor.Document, blocks);
            _fireBlocks = blocks;
        }

        private void RecomputeFireHighlighting()
        {
            var spans = new List<HighlightSpan>();
            var doc = Editor.Document;
            foreach (var (start, end) in _fireBlocks)
            {
                if (start < 0 || end > doc.TextLength || end <= start) continue;
                foreach (var span in SyntaxHighlighter.Highlight(doc.GetText(start, end - start)))
                    spans.Add(new HighlightSpan(start + span.Start, span.Length, span.Category));
            }
            _fireColorizer.Spans = spans;
            Editor.TextArea.TextView.Redraw();
        }

        // -----------------------------------------------------------
        // Vorschau
        // -----------------------------------------------------------

        public bool PreviewVisible
        {
            get => ViewerOnly || PreviewToggle.IsChecked == true;
            set
            {
                PreviewToggle.IsChecked = value;
                ApplyPreviewVisibility();
            }
        }

        public void TogglePreview()
        {
            if (!ViewerOnly) PreviewVisible = !PreviewVisible;
        }

        /// <summary>Applies <see cref="Mode"/>: editor read-only, formatting buttons disabled, or (viewer) the editor hidden altogether.</summary>
        private void ApplyMode()
        {
            Editor.IsReadOnly = IsReadOnly;
            foreach (var item in FormatBar.Items.OfType<Control>())
                if (!ReferenceEquals(item, PreviewToggle)) item.IsEnabled = !IsReadOnly;
            FormatBar.Visibility = ViewerOnly ? Visibility.Collapsed : Visibility.Visible;
            ApplyPreviewVisibility();
        }

        private void PreviewToggle_Click(object sender, RoutedEventArgs e) => ApplyPreviewVisibility();

        private void ApplyPreviewVisibility()
        {
            if (ViewerOnly)
            {
                // only the rendered page: no editor, no splitter
                Editor.Visibility = Visibility.Collapsed;
                EditorColumn.Width = new GridLength(0);
                Splitter.Visibility = Visibility.Collapsed;
                SplitterColumn.Width = new GridLength(0);
                Preview.Visibility = Visibility.Visible;
                PreviewColumn.Width = new GridLength(1, GridUnitType.Star);
                UpdatePreview();
                return;
            }
            Editor.Visibility = Visibility.Visible;
            EditorColumn.Width = new GridLength(1, GridUnitType.Star);

            if (PreviewVisible)
            {
                Splitter.Visibility = Visibility.Visible;
                Preview.Visibility = Visibility.Visible;
                SplitterColumn.Width = GridLength.Auto;
                PreviewColumn.Width = _previewWidth;
                UpdatePreview();
            }
            else
            {
                if (PreviewColumn.Width.Value > 0) _previewWidth = PreviewColumn.Width;
                Splitter.Visibility = Visibility.Collapsed;
                Preview.Visibility = Visibility.Collapsed;
                SplitterColumn.Width = new GridLength(0);
                PreviewColumn.Width = new GridLength(0);
            }
        }

        private void UpdatePreview(bool keepScroll = true)
        {
            if (!PreviewVisible) return;
            _renderer.BaseDirectory = BaseDirectory;

            // Scroll-Position über das Neuaufbauen des Dokuments hinweg halten.
            ScrollViewer? viewer = null;
            try
            {
                Preview.ApplyTemplate(); // sonst ist die Vorlage beim allerersten Aufruf noch nicht angewandt
                viewer = Preview.Template?.FindName("PART_ContentHost", Preview) as ScrollViewer;
            }
            catch (InvalidOperationException)
            {
                // ohne Scroll-Erhalt weitermachen
            }
            double offset = keepScroll ? viewer?.VerticalOffset ?? 0 : 0;

            Preview.Document = _renderer.Render(Editor.Text);

            if (viewer != null && offset > 0)
                Dispatcher.BeginInvoke(new Action(() => viewer.ScrollToVerticalOffset(offset)), DispatcherPriority.Loaded);
            else if (viewer != null && !keepScroll)
                Dispatcher.BeginInvoke(new Action(() => viewer.ScrollToHome()), DispatcherPriority.Loaded);
        }

        /// <summary>Scrolls the preview to the heading with the given anchor (GitHub style, see <see cref="MdAnchors"/>).</summary>
        public bool ScrollToAnchor(string? anchor)
        {
            if (string.IsNullOrEmpty(anchor)) return false;
            if (!_renderer.Anchors.TryGetValue(Uri.UnescapeDataString(anchor).ToLowerInvariant(), out var block)) return false;
            Dispatcher.BeginInvoke(new Action(() => block.BringIntoView()), DispatcherPriority.Loaded);
            return true;
        }

        private void HandleLink(string url)
        {
            try
            {
                if (url.StartsWith("#"))
                {
                    ScrollToAnchor(url.Substring(1));
                    return;
                }
                if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                    url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                {
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                    return;
                }

                int hash = url.IndexOf('#');
                string? anchor = hash >= 0 ? url.Substring(hash + 1) : null;
                string path = Uri.UnescapeDataString(hash >= 0 ? url.Substring(0, hash) : url);
                if (path.Length == 0) return;
                if (!Path.IsPathRooted(path) && BaseDirectory != null) path = Path.Combine(BaseDirectory, path);
                path = Path.GetFullPath(path);
                bool newTab = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
                if (File.Exists(path)) OpenFileRequested?.Invoke(path, anchor, newTab);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex); // ein kaputter Link darf den Editor nicht stören
            }
        }

        // -----------------------------------------------------------
        // Formatieren
        // -----------------------------------------------------------

        private void ToolBar_HideOverflow(object sender, RoutedEventArgs e)
        {
            if (sender is not ToolBar toolBar) return;
            if (toolBar.Template.FindName("OverflowGrid", toolBar) is FrameworkElement overflow)
                overflow.Visibility = Visibility.Collapsed;
            if (toolBar.Template.FindName("MainPanelBorder", toolBar) is FrameworkElement border)
                border.Margin = new Thickness(0);
        }

        private void Bold_Click(object sender, RoutedEventArgs e) => Wrap("**");
        private void Italic_Click(object sender, RoutedEventArgs e) => Wrap("*");
        private void Strike_Click(object sender, RoutedEventArgs e) => Wrap("~~");
        private void InlineCode_Click(object sender, RoutedEventArgs e) => Wrap("`");
        private void Heading_Click(object sender, RoutedEventArgs e) => CycleHeading();
        private void BulletList_Click(object sender, RoutedEventArgs e) => TogglePrefix(_ => "- ", BulletPrefix);
        private void NumberedList_Click(object sender, RoutedEventArgs e) => TogglePrefix(n => $"{n + 1}. ", NumberPrefix);
        private void Quote_Click(object sender, RoutedEventArgs e) => TogglePrefix(_ => "> ", QuotePrefix);
        private void CodeBlock_Click(object sender, RoutedEventArgs e) => InsertCodeBlock();
        private void Link_Click(object sender, RoutedEventArgs e) => InsertLink();
        private void Table_Click(object sender, RoutedEventArgs e) => InsertTable();

        private static readonly Regex BulletPrefix = new(@"^(\s*)[-*+]\s+", RegexOptions.Compiled);
        private static readonly Regex NumberPrefix = new(@"^(\s*)\d+[.)]\s+", RegexOptions.Compiled);
        private static readonly Regex QuotePrefix = new(@"^(\s*)>\s?", RegexOptions.Compiled);
        private static readonly Regex HeadingPrefix = new(@"^(#{1,6})\s+", RegexOptions.Compiled);

        private bool CanFormat() => !IsReadOnly;

        private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        /// <summary>Markiert Auswahl (oder das Wort am Cursor) mit `marker`; ist sie schon so markiert, wird es wieder entfernt.</summary>
        private void Wrap(string marker)
        {
            var doc = Editor.Document;
            int start = Editor.SelectionStart;
            int length = Editor.SelectionLength;
            int m = marker.Length;

            if (length == 0)
            {
                int caret = Editor.CaretOffset;
                int s = caret, e = caret;
                while (s > 0 && IsWordChar(doc.GetCharAt(s - 1))) s--;
                while (e < doc.TextLength && IsWordChar(doc.GetCharAt(e))) e++;
                start = s; length = e - s;
            }

            doc.BeginUpdate();
            try
            {
                string sel = doc.GetText(start, length);
                bool wrappedAround = start >= m && start + length + m <= doc.TextLength
                    && doc.GetText(start - m, m) == marker && doc.GetText(start + length, m) == marker;
                if (wrappedAround)
                {
                    doc.Remove(start + length, m);
                    doc.Remove(start - m, m);
                    Editor.Select(start - m, length);
                }
                else if (length >= 2 * m && sel.StartsWith(marker) && sel.EndsWith(marker))
                {
                    doc.Replace(start, length, sel.Substring(m, length - 2 * m));
                    Editor.Select(start, length - 2 * m);
                }
                else if (length == 0)
                {
                    doc.Insert(start, marker + marker);
                    Editor.CaretOffset = start + m;
                }
                else
                {
                    doc.Replace(start, length, marker + sel + marker);
                    Editor.Select(start + m, length);
                }
            }
            finally { doc.EndUpdate(); }
            Editor.Focus();
        }

        private void TogglePrefix(Func<int, string> prefixFor, Regex existing)
        {
            var doc = Editor.Document;
            var lines = Editor.SelectedLines();
            bool allHave = lines.All(l => existing.IsMatch(doc.GetText(l.Offset, l.Length)) || doc.GetText(l.Offset, l.Length).Trim().Length == 0);
            bool anyText = lines.Any(l => doc.GetText(l.Offset, l.Length).Trim().Length > 0);
            doc.BeginUpdate();
            try
            {
                // Von hinten nach vorn, damit die Offsets der noch folgenden Zeilen gültig bleiben.
                for (int k = lines.Count - 1; k >= 0; k--)
                {
                    var line = lines[k];
                    string text = doc.GetText(line.Offset, line.Length);
                    if (text.Trim().Length == 0 && lines.Count > 1) continue;
                    var m = existing.Match(text);
                    if (allHave && anyText)
                    {
                        if (m.Success) doc.Remove(line.Offset + m.Groups[1].Length, m.Length - m.Groups[1].Length);
                    }
                    else
                    {
                        // Eine andere Listen-/Zitat-Art zuerst ersetzen, nicht stapeln.
                        var other = new[] { BulletPrefix, NumberPrefix, QuotePrefix }.Select(r => r.Match(text)).FirstOrDefault(r => r.Success);
                        if (other != null && !m.Success)
                            doc.Replace(line.Offset + other.Groups[1].Length, other.Length - other.Groups[1].Length, prefixFor(k));
                        else if (!m.Success)
                            doc.Insert(line.Offset + (text.Length - text.TrimStart().Length), prefixFor(k));
                    }
                }
            }
            finally { doc.EndUpdate(); }
            Editor.Focus();
        }

        private void CycleHeading()
        {
            var doc = Editor.Document;
            var line = doc.GetLineByOffset(Editor.CaretOffset);
            string text = doc.GetText(line.Offset, line.Length);
            var m = HeadingPrefix.Match(text);
            if (!m.Success) doc.Insert(line.Offset, "# ");
            else if (m.Groups[1].Length < 3) doc.Replace(line.Offset, m.Groups[1].Length, new string('#', m.Groups[1].Length + 1));
            else doc.Remove(line.Offset, m.Length);
            Editor.Focus();
        }

        private void InsertCodeBlock()
        {
            var doc = Editor.Document;
            int start = Editor.SelectionStart, length = Editor.SelectionLength;
            string sel = doc.GetText(start, length);
            doc.BeginUpdate();
            try
            {
                var line = doc.GetLineByOffset(start);
                bool atLineStart = start == line.Offset;
                string pre = atLineStart ? "" : "\n";
                string body = length > 0 ? sel : "";
                string block = pre + "```\n" + body + "\n```\n";
                doc.Replace(start, length, block);
                // Cursor in die erste Zeile des Blocks (hinter die öffnenden Zäune, um die Sprache anzugeben)
                Editor.CaretOffset = start + pre.Length + 3;
            }
            finally { doc.EndUpdate(); }
            Editor.Focus();
        }

        private void InsertLink()
        {
            var doc = Editor.Document;
            int start = Editor.SelectionStart, length = Editor.SelectionLength;
            string sel = doc.GetText(start, length);
            string label = length > 0 ? sel : "Text";
            string link = $"[{label}](url)";
            doc.Replace(start, length, link);
            Editor.Select(start + label.Length + 3, 3); // "url" markieren
            Editor.Focus();
        }

        private void InsertTable()
        {
            const string table = "| Spalte 1 | Spalte 2 |\n|----------|----------|\n|          |          |\n";
            var doc = Editor.Document;
            var line = doc.GetLineByOffset(Editor.CaretOffset);
            string pre = Editor.CaretOffset == line.Offset ? "" : "\n";
            doc.Insert(Editor.CaretOffset, pre + table);
            Editor.Focus();
        }

        // -----------------------------------------------------------
        // Tastatur: Kürzel und Listenfortsetzung
        // -----------------------------------------------------------

        private static readonly Regex ListLine = new(@"^(?<indent>\s*)(?<marker>[-*+]|\d+[.)])\s+(?<task>\[[ xX]\]\s+)?(?<rest>.*)$", RegexOptions.Compiled);

        private void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;

            if (IsReadOnly)
            {
                if (ctrl && shift && e.Key == Key.V) { TogglePreview(); e.Handled = true; }
                return; // no formatting shortcuts or list continuation in a read-only document
            }

            if (ctrl && !alt)
            {
                switch (e.Key)
                {
                    case Key.B when !shift: Wrap("**"); e.Handled = true; return;
                    case Key.I when !shift: Wrap("*"); e.Handled = true; return;
                    case Key.E when !shift: Wrap("`"); e.Handled = true; return;
                    case Key.K when !shift: InsertLink(); e.Handled = true; return;
                    case Key.H when !shift: CycleHeading(); e.Handled = true; return;
                    case Key.V when shift: TogglePreview(); e.Handled = true; return;
                }
            }

            if (e.Key == Key.Return && !ctrl && !shift && !alt && Editor.SelectionLength == 0 && ContinueList())
                e.Handled = true;
        }

        /// <summary>Enter am Ende eines Listenpunkts: nächsten Punkt anlegen; ein leerer Punkt beendet die Liste.</summary>
        private bool ContinueList()
        {
            var doc = Editor.Document;
            int caret = Editor.CaretOffset;
            var line = doc.GetLineByOffset(caret);
            if (caret != line.EndOffset) return false;
            var m = ListLine.Match(doc.GetText(line.Offset, line.Length));
            if (!m.Success) return false;

            // In einem Code-Block nichts fortsetzen.
            if (_colorizer.FencedLines.Contains(line.LineNumber)) return false;

            if (m.Groups["rest"].Length == 0)
            {
                // Leerer Punkt: Liste beenden (Zeile leeren, eingerückt lassen)
                doc.Replace(line.Offset, line.Length, "");
                return true;
            }

            string marker = m.Groups["marker"].Value;
            if (char.IsDigit(marker[0]))
            {
                string digits = new string(marker.TakeWhile(char.IsDigit).ToArray());
                marker = (long.Parse(digits) + 1) + marker.Substring(digits.Length);
            }
            string next = "\n" + m.Groups["indent"].Value + marker + " " + (m.Groups["task"].Success ? "[ ] " : "");
            doc.Insert(caret, next);
            Editor.CaretOffset = caret + next.Length;
            Editor.TextArea.Caret.BringCaretToView();
            return true;
        }
    }
}
