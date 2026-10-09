using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Search;

namespace fire.Editor
{
    /// <summary>
    /// A plain text editor that is tied to nothing: no project, no completion, no diagnostics - so it takes what is not valid code on its own: the C++ of the natives, notes, data, and the files of
    /// a template (docs/TEMPLATES.md; `$name$` is no fire). The file gets colours by its extension: C and C++, fire (.script) or markup (.fxml). Used for every text file that no other editor takes,
    /// and for "Open as Text".
    /// </summary>
    public sealed class TextFileEditorControl : UserControl, IDocumentView
    {
        private readonly TextEditor _editor = new()
        {
            FontFamily = new Avalonia.Media.FontFamily("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace"), FontSize = 14,
            ShowLineNumbers = true, WordWrap = false, Padding = new Thickness(6, 4),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        private readonly HighlightingColorizer _colorizer = new();
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
        private readonly SearchPanel _searchPanel;
        private bool _loading;
        private enum Colours { None, Cpp, Fire, Markup }
        private Colours _colours;
        private bool _highlighted => _colours != Colours.None;

        public string? FilePath { get; set; }
        public bool IsModified { get; private set; }
        public bool IsReadOnly => false;
        public event Action? ModifiedChanged;
        public event Action<int>? CaretLineChanged;

        public TextFileEditorControl()
        {
            _editor.Options.ConvertTabsToSpaces = true;
            _editor.Options.IndentationSize = 4;
            _editor.TextArea.TextView.LineTransformers.Add(_colorizer);
            _editor.TextChanged += (_, _) =>
            {
                if (!_loading) SetModified(true);
                if (_highlighted) { _timer.Stop(); _timer.Start(); }
            };
            _editor.TextArea.Caret.PositionChanged += (_, _) => CaretLineChanged?.Invoke(GetCaretLine());
            _editor.AddHandler(PointerPressedEvent, (object? _, PointerPressedEventArgs e) =>
            {
                if (e.GetCurrentPoint(_editor).Properties.IsRightButtonPressed) _editor.PlaceCaretForContextMenu(e);
            }, RoutingStrategies.Tunnel);
            _searchPanel = SearchPanel.Install(_editor);
            EditorTheme.Apply(_editor, _searchPanel);
            _editor.ContextMenu = EditorCommands.BuildMenu(EditorCommands.StandardEntries(_editor, Find).ToList());
            _timer.Tick += (_, _) => { _timer.Stop(); Recolor(); };
            Content = _editor;
        }

        private void Recolor()
        {
            string text = _editor.Text;
            System.Collections.Generic.IReadOnlyList<HighlightSpan> spans;
            try
            {
                spans = _colours switch
                {
                    Colours.Cpp => CppHighlighter.Highlight(text),
                    Colours.Fire => SyntaxHighlighter.Highlight(System.Text.RegularExpressions.Regex.Replace(text, @"\$(\w+)\$", "_$1_")),   // a placeholder as a name of the same length
                    Colours.Markup => MarkupEditorControl.Highlight(text),
                    _ => Array.Empty<HighlightSpan>(),
                };
            }
            catch (Exception) { spans = Array.Empty<HighlightSpan>(); }   // text that is no code of its kind (a template with placeholders) just has fewer colours
            _colorizer.Spans = spans;
            _editor.TextArea.TextView.Redraw();
        }

        private void SetModified(bool value)
        {
            if (IsModified == value) return;
            IsModified = value;
            ModifiedChanged?.Invoke();
        }

        public string GetText() => _editor.Text;
        public int GetCaretLine() => _editor.TextArea.Caret.Line;
        public void FocusEditor() => _editor.Focus();
        public bool CanUndo => _editor.Document.UndoStack.CanUndo;
        public bool CanRedo => _editor.Document.UndoStack.CanRedo;
        public bool HasSelection => _editor.SelectionLength > 0;
        public int LineCount => _editor.Document.LineCount;
        public void Undo() { _editor.Undo(); _editor.Focus(); }
        public void Redo() { _editor.Redo(); _editor.Focus(); }
        public void Cut() { _editor.Cut(); _editor.Focus(); }
        public void Copy() { _editor.Copy(); _editor.Focus(); }
        public void Paste() { _editor.Paste(); _editor.Focus(); }
        public void Delete() { _editor.Delete(); _editor.Focus(); }
        public void SelectAll() { _editor.SelectAll(); _editor.Focus(); }
        public void GoToLine(int line) => _editor.GoToLine(line);
        public void Find() { _editor.Focus(); _searchPanel.Open(); }
        public void FindNext() => _searchPanel.FindNext();
        public void FindPrevious() => _searchPanel.FindPrevious();
        public void MarkSaved() => SetModified(false);

        public void ResetTo(string text, string? filePath)
        {
            FilePath = filePath;
            string ext = filePath == null ? "" : System.IO.Path.GetExtension(filePath).ToLowerInvariant();
            _colours = filePath == null ? Colours.None : CppHighlighter.IsCppFile(filePath) ? Colours.Cpp : ext is ".script" or ".fi" or ".fic" ? Colours.Fire : ext == ".fxml" ? Colours.Markup : Colours.None;
            _loading = true;
            try { _editor.Text = text; }
            finally { _loading = false; }
            _editor.Document.UndoStack.ClearAll();
            Recolor();
            SetModified(false);
        }
    }
}
