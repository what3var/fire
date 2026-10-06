using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using fire.UI.Markup;

namespace fire.Editor
{
    /// <summary>Editor of a UI markup file (.fxml, docs/UI_MARKUP.md): the XML text with colours, and next to it the design view - a picture of the interface (only to look at,
    /// see <see cref="MarkupPreviewControl"/>) that follows the text while it is typed; mistakes of the markup are listed under the picture and underlined in the text.</summary>
    public partial class MarkupEditorControl : UserControl, IDocumentView
    {
        public string? FilePath { get; set; }
        public bool IsModified { get; private set; }
        public bool IsReadOnly => false;
        public event Action? ModifiedChanged;
        public event Action<int>? CaretLineChanged;

        /// <summary>The user wants to see the script generated from the markup (the host opens it in a tab).</summary>
        public event Action? ShowScriptRequested;

        private readonly HighlightingColorizer _colorizer = new();
        private readonly ErrorSquiggleRenderer _squiggles = new();
        private readonly DispatcherTimer _timer;
        private AvaloniaEdit.Search.SearchPanel? _searchPanel;
        private bool _loading;
        private GridLength _previewWidth = new(1, GridUnitType.Star);

        public MarkupEditorControl()
        {
            InitializeComponent();

            Editor.Options.ConvertTabsToSpaces = true;
            Editor.Options.IndentationSize = 2;
            Editor.TextArea.TextView.LineTransformers.Add(_colorizer);
            Editor.TextArea.TextView.BackgroundRenderers.Add(_squiggles);
            Editor.TextChanged += (_, _) =>
            {
                if (!_loading) SetModified(true);
                _timer!.Stop();
                _timer.Start();
            };
            Editor.TextArea.Caret.PositionChanged += (_, _) =>
            {
                int line = GetCaretLine();
                Preview.HighlightLine = line;
                CaretLineChanged?.Invoke(line);
            };
            Editor.AddHandler(PointerPressedEvent, (object? _, PointerPressedEventArgs e) =>
            {
                if (e.GetCurrentPoint(Editor).Properties.IsRightButtonPressed) Editor.PlaceCaretForContextMenu(e);
            }, RoutingStrategies.Tunnel);

            _searchPanel = AvaloniaEdit.Search.SearchPanel.Install(Editor);
            EditorTheme.Apply(Editor, _searchPanel);
            Editor.ContextMenu = EditorCommands.BuildMenu(EditorCommands.StandardEntries(Editor, Find).ToList());

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _timer.Tick += (_, _) => { _timer.Stop(); Refresh(); };
            Refresh();
        }

        // -----------------------------------------------------------
        // Text, colours, design view
        // -----------------------------------------------------------

        private static readonly Regex CommentToken = new(@"<!--.*?-->", RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex TagToken = new(@"</?[A-Za-z_][\w.:-]*|/?>", RegexOptions.Compiled);
        private static readonly Regex AttributeToken = new(@"([A-Za-z_][\w.:-]*)\s*=\s*(""[^""]*""|'[^']*')", RegexOptions.Compiled);
        private static readonly Regex ExtensionToken = new(@"\{[^{}]*\}", RegexOptions.Compiled);

        /// <summary>The coloured parts of an XML text, sorted by start (a later part is drawn over an earlier one that it lies in).</summary>
        internal static List<HighlightSpan> Highlight(string text)
        {
            var spans = new List<HighlightSpan>();
            var comments = CommentToken.Matches(text).Select(m => (m.Index, m.Length)).ToList();
            bool InComment(int index) => comments.Any(c => index >= c.Index && index < c.Index + c.Length);

            foreach (Match m in TagToken.Matches(text))
                if (!InComment(m.Index)) spans.Add(new HighlightSpan(m.Index, m.Length, HighlightCategory.Keyword));
            foreach (Match m in AttributeToken.Matches(text))
            {
                if (InComment(m.Index)) continue;
                spans.Add(new HighlightSpan(m.Groups[1].Index, m.Groups[1].Length, HighlightCategory.Type));
                var value = m.Groups[2];
                spans.Add(new HighlightSpan(value.Index, value.Length, HighlightCategory.String));
                foreach (Match e in ExtensionToken.Matches(value.Value))
                    spans.Add(new HighlightSpan(value.Index + e.Index, e.Length, HighlightCategory.Number));
            }
            foreach (var (index, length) in comments) spans.Add(new HighlightSpan(index, length, HighlightCategory.Comment));
            return spans.OrderBy(s => s.Start).ToList();
        }

        private MarkupDocument? _document;

        private void Refresh()
        {
            string text = Editor.Text;
            _colorizer.Spans = Highlight(text);
            Editor.TextArea.TextView.Redraw();

            _document = MarkupParser.Parse(text);
            Preview.SetDocument(_document);
            Preview.HighlightLine = GetCaretLine();

            // the problems of the markup itself, and - when it reads well - the ones the generator finds (values that do not fit their property)
            var problems = _document.Diagnostics.ToList();
            if (problems.Count == 0)
            {
                try { FireUiGenerator.Generate(_document, FilePath ?? ""); }
                catch (MarkupException ex) { problems.AddRange(ex.Diagnostics); }
            }
            _problemLines = problems.Select(p => p.Line).ToList();
            Problems.ItemsSource = problems.Select(p => p.ToString()).ToList();
            Problems.IsVisible = problems.Count > 0;
            _squiggles.ErrorLines = problems.Where(p => p.Line > 0).Select(p => p.Line).ToHashSet();
            Editor.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Selection);
        }

        private List<int> _problemLines = new();

        private void Problems_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            int index = Problems.SelectedIndex;
            if (index >= 0 && index < _problemLines.Count && _problemLines[index] > 0) Editor.GoToLine(_problemLines[index]);
        }

        /// <summary>The script generated from the current text; false (with the reason) if the markup has mistakes.</summary>
        public bool TryGenerate(out string script)
        {
            try
            {
                script = FireUiGenerator.Generate(MarkupParser.Parse(Editor.Text), FilePath ?? "");
                return true;
            }
            catch (MarkupException ex)
            {
                script = ex.Message;
                return false;
            }
        }

        private void Script_Click(object? sender, RoutedEventArgs e) => ShowScriptRequested?.Invoke();

        // -----------------------------------------------------------
        // Insert elements
        // -----------------------------------------------------------

        private static readonly (string Title, string Snippet)[] Snippets =
        {
            ("Label", "<Label text=\"Text\"/>"),
            ("Button", "<Button name=\"ok\" text=\"OK\" onClick=\"Ok\"/>"),
            ("CheckBox", "<CheckBox name=\"flag\" text=\"Option\" isChecked=\"{Binding Flag, Mode=TwoWay}\"/>"),
            ("TextBox", "<TextBox name=\"name\" width=\"200\" text=\"{Binding Name, Mode=TwoWay}\"/>"),
            ("Stack", "<Stack width=\"200\" height=\"100\" orientation=\"Vertical\" spacing=\"6\">\n  \n</Stack>"),
            ("Panel", "<Panel x=\"0\" y=\"0\" width=\"200\" height=\"100\" showBorder=\"true\">\n  \n</Panel>"),
            ("Converter", "<Resources>\n  <Converter key=\"Upper\" type=\"UpperConverter\"/>\n</Resources>"),
        };

        private void Template_Click(object? sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu();
            foreach (var (title, snippet) in Snippets)
            {
                var item = new MenuItem { Header = title };
                string captured = snippet;
                item.Click += (_, _) =>
                {
                    Editor.Document.Insert(Editor.CaretOffset, captured);
                    Editor.Focus();
                };
                menu.Items.Add(item);
            }
            menu.Open((Control)sender!);
        }

        // -----------------------------------------------------------
        // IDocumentView
        // -----------------------------------------------------------

        public string GetText() => Editor.Text;
        public int GetCaretLine() => Editor.TextArea.Caret.Line;
        public void FocusEditor() => Editor.Focus();

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
        public void Find() { Editor.Focus(); _searchPanel?.Open(); }
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
            Refresh();
            SetModified(false);
        }

        public void SetText(string text) => Editor.Text = text;

        private void SetModified(bool value)
        {
            if (IsModified == value) return;
            IsModified = value;
            ModifiedChanged?.Invoke();
        }

        // -----------------------------------------------------------
        // Design view on/off
        // -----------------------------------------------------------

        public void TogglePreview()
        {
            PreviewToggle.IsChecked = PreviewToggle.IsChecked != true;
            ApplyPreviewVisibility();
        }

        private void PreviewToggle_Click(object? sender, RoutedEventArgs e) => ApplyPreviewVisibility();

        private void ApplyPreviewVisibility()
        {
            var columns = ContentGrid.ColumnDefinitions;
            if (PreviewToggle.IsChecked == true)
            {
                Splitter.IsVisible = true;
                PreviewPanel.IsVisible = true;
                columns[1].Width = GridLength.Auto;
                columns[2].Width = _previewWidth;
                Refresh();
            }
            else
            {
                if (columns[2].Width.Value > 0) _previewWidth = columns[2].Width;
                Splitter.IsVisible = false;
                PreviewPanel.IsVisible = false;
                columns[1].Width = new GridLength(0);
                columns[2].Width = new GridLength(0);
            }
        }
    }
}
