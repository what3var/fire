using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using AvaloniaEdit.Document;

namespace fire.Editor
{
    /// <summary>How a Markdown document is opened: editable, read-only (editor and preview visible, no changes possible),
    /// or as a pure viewer (only the rendered page, the editor is hidden).</summary>
    public enum MarkdownViewMode { Edit, ReadOnly, Viewer }

    /// <summary>Editor for Markdown documents: AvalonEdit source text with
    /// Markdown highlighting (see MarkdownColorizer; ```fire blocks are coloured
    /// as in the script editor), format buttons/shortcuts, automatic
    /// list continuation with Enter and a live preview next to it (see
    /// MarkdownParser/MarkdownRenderer).
    ///
    /// Like ScriptEditorControl, it knows nothing about compiling/running - the
    /// main window treats both the same via IDocumentView.</summary>
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
        private double _overviewZoom = 1;
        private OverviewHost? _overviewHost;

        /// <summary>What the surface of an overview changes goes into this document: as one undo step, so Ctrl+Z takes it back and Ctrl+S saves it.</summary>
        private sealed class OverviewHost : IOverviewHost
        {
            private readonly MarkdownEditorControl _owner;
            public OverviewHost(MarkdownEditorControl owner) => _owner = owner;

            public string[] Lines => _owner.Editor.Text.Replace("\r\n", "\n").Split('\n');
            public int? SelectedLine { get; set; }
            public int? PendingEditLine { get; set; }
            public OverviewTool Tool => _owner._tool;
            public string? Color => _owner._color;
            public string? Fill => _owner._fill;
            public void ToolDone() { if (_owner._tool != OverviewTool.Pen) _owner.SetTool(OverviewTool.Select); }
            public void Batch(Action changes)
            {
                var doc = _owner.Editor.Document;
                doc.UndoStack.StartUndoGroup();
                try { changes(); }
                finally { doc.UndoStack.EndUndoGroup(); }
            }
            public void Undo() { if (_owner.Editor.Document.UndoStack.CanUndo) _owner.Editor.Undo(); }
            public void Redo() { if (_owner.Editor.Document.UndoStack.CanRedo) _owner.Editor.Redo(); }

            public void Replace(int first, int count, IReadOnlyList<string> lines)
            {
                var doc = _owner.Editor.Document;
                string newline = doc.Text.Contains("\r\n") ? "\r\n" : "\n";
                int start = first < doc.LineCount ? doc.GetLineByNumber(first + 1).Offset : doc.TextLength;
                string text = string.Join(newline, lines);
                if (count == 0)
                {
                    if (lines.Count == 0) return;
                    if (first < doc.LineCount) doc.Insert(start, text + newline);
                    else doc.Insert(doc.TextLength, (doc.TextLength > 0 && !doc.Text.EndsWith('\n') ? newline : "") + text);
                    return;
                }
                int last = Math.Min(first + count, doc.LineCount);
                if (lines.Count == 0)
                {
                    // removing lines takes their line breaks with them (no empty line is left)
                    if (last < doc.LineCount) doc.Remove(start, doc.GetLineByNumber(last + 1).Offset - start);
                    else if (first > 0) { int from = doc.GetLineByNumber(first).EndOffset; doc.Remove(from, doc.TextLength - from); }
                    else doc.Remove(0, doc.TextLength);
                    return;
                }
                int end = doc.GetLineByNumber(last).EndOffset;
                doc.Replace(start, end - start, text);
            }
        }
        private readonly DispatcherTimer _previewTimer;
        private readonly DispatcherTimer _highlightTimer;
        private List<(int Start, int End)> _fireBlocks = new();
        private bool _loading;
        private GridLength _previewWidth = new(1, GridUnitType.Star);

        // the columns of ContentGrid: editor, splitter, preview
        private ColumnDefinition EditorColumn => ContentGrid.ColumnDefinitions[0];
        private ColumnDefinition SplitterColumn => ContentGrid.ColumnDefinitions[1];
        private ColumnDefinition PreviewColumn => ContentGrid.ColumnDefinitions[2];

        public MarkdownEditorControl()
        {
            InitializeComponent();

            Editor.Options.ConvertTabsToSpaces = true;
            Editor.Options.IndentationSize = 2;
            Editor.TextArea.TextView.LineTransformers.Add(_colorizer);
            Editor.TextArea.TextView.LineTransformers.Add(_fireColorizer);

            Editor.TextChanged += Editor_TextChanged;
            Editor.TextArea.Caret.PositionChanged += (_, _) => CaretLineChanged?.Invoke(GetCaretLine());
            Editor.AddHandler(KeyDownEvent, Editor_KeyDown, RoutingStrategies.Tunnel);
            Editor.AddHandler(PointerPressedEvent, (object? _, PointerPressedEventArgs e) =>
            {
                if (e.GetCurrentPoint(Editor).Properties.IsRightButtonPressed) Editor.PlaceCaretForContextMenu(e);
            }, RoutingStrategies.Tunnel);

            _searchPanel = AvaloniaEdit.Search.SearchPanel.Install(Editor);
            EditorTheme.Apply(Editor, _searchPanel);
            Editor.ContextMenu = EditorCommands.BuildMenu(new List<EditorCommands.Entry?>
            {
                new() { Header = "_Bold", Gesture = "Ctrl+B", Execute = () => Wrap("**"), Enabled = CanFormat },
                new() { Header = "_Italic", Gesture = "Ctrl+I", Execute = () => Wrap("*"), Enabled = CanFormat },
                new() { Header = "_Code", Gesture = "Ctrl+E", Execute = () => Wrap("`"), Enabled = CanFormat },
                new() { Header = "_Link", Gesture = "Ctrl+K", Execute = InsertLink, Enabled = CanFormat },
                new() { Header = "_Heading (change level)", Gesture = "Ctrl+H", Execute = CycleHeading, Enabled = CanFormat },
                null,
            }.Concat(EditorCommands.StandardEntries(Editor, Find)).ToList());

            _renderer.LinkClicked = HandleLink;

            _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); UpdatePreview(); };

            _highlightTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _highlightTimer.Tick += (_, _) => { _highlightTimer.Stop(); RecomputeFireHighlighting(); };

            BuildCanvasTools();
            RecomputeFences();
            _settingLayout = true;
            LayoutBox.SelectedIndex = (int)Layout;
            _settingLayout = false;
            ApplyPreviewVisibility();
        }

        // -----------------------------------------------------------
        // IDocumentView
        // -----------------------------------------------------------

        public string GetText() => Editor.Text;
        public int GetCaretLine() => Editor.TextArea.Caret.Line;
        public void FocusEditor()
        {
            if (Layout is Layouts.Surface or Layouts.View) Preview.Focus();
            else Editor.Focus();
        }

        // -----------------------------------------------------------
        // Edit (menu of the main window, context menu)
        // -----------------------------------------------------------

        private AvaloniaEdit.Search.SearchPanel? _searchPanel;

        public bool CanUndo => !IsReadOnly && Editor.Document.UndoStack.CanUndo;
        public bool CanRedo => !IsReadOnly && Editor.Document.UndoStack.CanRedo;
        /// <summary>The selectable text of the preview that has the focus (viewer mode: copying from the page).</summary>
        private SelectableTextBlock? FocusedPreviewText => TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as SelectableTextBlock;

        public bool HasSelection => ViewerOnly ? FocusedPreviewText is { SelectionStart: var a, SelectionEnd: var b } && a != b : Editor.SelectionLength > 0;
        public int LineCount => Editor.Document.LineCount;

        public void Undo() { Editor.Undo(); Editor.Focus(); }
        public void Redo() { Editor.Redo(); Editor.Focus(); }
        public void Cut() { Editor.Cut(); Editor.Focus(); }
        public void Copy()
        {
            if (ViewerOnly)
            {
                FocusedPreviewText?.Copy();
                return;
            }
            Editor.Copy();
            Editor.Focus();
        }
        public void Paste() { Editor.Paste(); Editor.Focus(); }
        public void Delete() { Editor.Delete(); Editor.Focus(); }
        public void SelectAll()
        {
            if (ViewerOnly)
            {
                // the text blocks of the page are selected one by one: the one with the focus
                Preview.Focus();
                FocusedPreviewText?.SelectAll();
                return;
            }
            Editor.SelectAll();
            Editor.Focus();
        }
        public void GoToLine(int line) => Editor.GoToLine(line);

        public void FindReplace()

        {

            if (_searchPanel != null) _searchPanel.IsReplaceMode = true;

            Find();

        }


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
            if (PreviewVisible && !_loading) { _previewTimer.Stop(); _previewTimer.Start(); }   // (loading renders the page itself)
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

        /// <summary>How a Markdown document is shown: the surface (edited on it), text and view side by side, the text alone, the view alone.</summary>
        public enum Layouts { Surface, Split, Code, View }

        private Layouts _layout = Layouts.Surface;

        /// <summary>The layout; a read-only document is shown as a view (the surface edits).</summary>
        public Layouts Layout
        {
            get => ViewerOnly ? Layouts.View : IsReadOnly && _layout == Layouts.Surface ? Layouts.View : _layout;
            set
            {
                _layout = value;
                _settingLayout = true;
                LayoutBox.SelectedIndex = (int)Layout;
                _settingLayout = false;
                ApplyPreviewVisibility();
            }
        }
        private bool _settingLayout;

        public bool PreviewVisible
        {
            get => Layout != Layouts.Code;
            set => Layout = value ? (Layout == Layouts.Code ? Layouts.View : Layout) : Layouts.Code;
        }

        /// <summary>The preview button (Ctrl+Shift+V): Surface - leave the editor of a container; Code and View - switch between the two; Split - only the view.</summary>
        public void TogglePreview()
        {
            switch (Layout)
            {
                case Layouts.Surface:
                    if (Preview.Content is OverviewView view) view.EndEditing();
                    break;
                case Layouts.View when !ViewerOnly: Layout = Layouts.Code; break;
                case Layouts.Code or Layouts.Split: Layout = Layouts.View; break;
            }
        }

        private void PreviewButton_Click(object? sender, RoutedEventArgs e) => TogglePreview();

        private void LayoutBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_settingLayout || LayoutBox.SelectedIndex < 0) return;
            _layout = (Layouts)LayoutBox.SelectedIndex;
            ApplyPreviewVisibility();
        }

        /// <summary>Applies <see cref="Mode"/>: editor read-only, formatting buttons disabled, or (viewer) the editor hidden altogether.</summary>
        private void ApplyMode()
        {
            Editor.IsReadOnly = IsReadOnly;
            foreach (var item in TextTools.Children.OfType<Control>()) item.IsEnabled = !IsReadOnly;
            FormatBar.IsVisible = !ViewerOnly;
            if (IsReadOnly && _layout == Layouts.Surface) _layout = Layouts.View;
            _settingLayout = true;
            LayoutBox.SelectedIndex = (int)Layout;
            _settingLayout = false;
            ApplyPreviewVisibility();
        }

        private void ApplyPreviewVisibility()
        {
            var layout = Layout;
            bool editor = layout is Layouts.Split or Layouts.Code;
            bool preview = layout != Layouts.Code;
            Editor.IsVisible = editor;
            EditorColumn.Width = editor ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            if (layout == Layouts.Split)
            {
                Splitter.IsVisible = true;
                SplitterColumn.Width = GridLength.Auto;
                PreviewColumn.Width = _previewWidth;
            }
            else
            {
                if (Splitter.IsVisible && PreviewColumn.Width.Value > 0 && PreviewColumn.Width.IsStar) _previewWidth = PreviewColumn.Width;
                Splitter.IsVisible = false;
                SplitterColumn.Width = new GridLength(0);
                PreviewColumn.Width = preview ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            }
            Preview.IsVisible = preview;
            TextTools.IsVisible = editor;
            CanvasTools.IsVisible = !IsReadOnly && layout is Layouts.Surface or Layouts.Split;
            PreviewButtonText.Text = layout == Layouts.View ? "Code" : "Preview";
            ToolTip.SetTip(PreviewButton, layout switch
            {
                Layouts.Surface => "Leave the editor of a container (Ctrl+Shift+V)",
                Layouts.View => "Show the text (Ctrl+Shift+V)",
                _ => "Show the view (Ctrl+Shift+V)",
            });
            if (preview) UpdatePreview();
        }

        /// <summary>The document is a canvas: a `.xmd` file, or Markdown that already has containers, sketches or lines (or was made a canvas before it was saved).</summary>
        public bool IsCanvas => _madeCanvas || (FilePath != null && Path.GetExtension(FilePath).Equals(OverviewParser.CanvasExtension, StringComparison.OrdinalIgnoreCase))
            || OverviewParser.IsOverview(Editor.Text);
        private bool _madeCanvas;

        /// <summary>Makes a Markdown document a canvas (`.md` → `.xmd`), after asking (set by the main window); true: it is one now.</summary>
        public static Func<MarkdownEditorControl, System.Threading.Tasks.Task<bool>>? ConvertToCanvas { get; set; }

        private void UpdatePreview(bool keepScroll = true)
        {
            if (!PreviewVisible) return;
            _renderer.BaseDirectory = BaseDirectory;

            // Keep the scroll position across rebuilding the page.
            var offset = keepScroll ? Preview.Offset : default;

            string text = Editor.Text;
            var layout = Layout;
            bool surface = layout == Layouts.Surface || (IsCanvas && layout == Layouts.Split) || (IsCanvas && layout == Layouts.View);
            if (surface)
            {
                // the surface (docs/NOTES.md): the text at the top left, containers at their places; it scrolls both ways and keeps its zoom. It edits in Surface and Split.
                bool hadFocus = false;
                if (Preview.Content is OverviewView old) { _overviewZoom = old.Zoom; hadFocus = old.IsKeyboardFocusWithin; }
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (FilePath != null) visited.Add(Path.GetFullPath(FilePath));
                bool edits = !IsReadOnly && layout != Layouts.View;
                var view = new OverviewView(OverviewParser.Parse(text), new OverviewContext { BaseDirectory = BaseDirectory, LinkClicked = HandleLink, Visited = visited },
                    edits ? _overviewHost ??= new OverviewHost(this) : null);
                view.SetZoom(_overviewZoom);
                Preview.HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
                Preview.Content = view;
                if (hadFocus) Dispatcher.UIThread.Post(() => view.Focus(), DispatcherPriority.Loaded);   // the keys (Ctrl+Z, Esc) stay with the surface
            }
            else
            {
                Preview.HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
                Preview.Content = _renderer.Render(text);
            }

            Dispatcher.UIThread.Post(() => Preview.Offset = offset, DispatcherPriority.Loaded);
        }

        // -----------------------------------------------------------
        // The tools of a canvas
        // -----------------------------------------------------------

        private readonly Dictionary<OverviewTool, Avalonia.Controls.Primitives.ToggleButton> _toolButtons = new();
        private OverviewTool _tool = OverviewTool.Select;

        private void BuildCanvasTools()
        {
            void Tool(OverviewTool tool, string label, string tip)
            {
                var button = new Avalonia.Controls.Primitives.ToggleButton { Content = new TextBlock { Text = label }, IsChecked = tool == OverviewTool.Select };
                button.Classes.Add("tool");
                ToolTip.SetTip(button, tip);
                button.Click += async (_, _) => await ChooseTool(tool);
                _toolButtons[tool] = button;
                CanvasTools.Children.Add(button);
            }
            void Command(string label, string tip, Action action)
            {
                var button = new Button { Content = new TextBlock { Text = label } };
                button.Classes.Add("tool");
                ToolTip.SetTip(button, tip);
                button.Click += (_, _) => action();
                CanvasTools.Children.Add(button);
            }
            Tool(OverviewTool.Select, "Select", "Select, move and size containers; a double click edits the text");
            Tool(OverviewTool.Container, "Container", "Draw a container (a click: 300 wide)");
            Tool(OverviewTool.Rect, "Rect", "Draw a rectangle");
            Tool(OverviewTool.Ellipse, "Ellipse", "Draw an ellipse");
            Tool(OverviewTool.Arrow, "Arrow", "Draw an arrow");
            Tool(OverviewTool.Pen, "Pen", "Draw freely");
            Tool(OverviewTool.Text, "Text", "Write a text on the surface");
            Tool(OverviewTool.Connect, "Connect", "Draw a line from one container to another");
            Command("Embed...", "A container that shows a file: a todo list, another canvas, Markdown, a picture", () => _ = Embed());
            CanvasTools.Children.Add(ColorButton("Color", "The colour of what is drawn next - and of the selected shape or line", _colorSwatch,
                value => { _color = value; if (Preview.Content is OverviewView v) v.ApplyColor(value); }));
            CanvasTools.Children.Add(ColorButton("Fill", "The filling of the next rect or ellipse - and of the selected shape", _fillSwatch,
                value => { _fill = value; if (Preview.Content is OverviewView v) v.ApplyFill(value); }));
            Command("Fit", "Fit the whole canvas into the window", FitToView);
            CanvasTools.Children.Add(new Border { Width = 1, Background = EditorTheme.Border, Margin = new Thickness(4, 2) });
        }

        private string? _color;
        private readonly Border _colorSwatch = new() { Width = 12, Height = 12, CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1), BorderBrush = EditorTheme.TextDim };

        /// <summary>The colours of the tool bar: for what is drawn next, and for the selected shape or line.</summary>
        private static readonly (string Name, string? Value)[] Colors =
        {
            ("Default", null), ("Pink", "#FF479A"), ("Red", "#FF523B"), ("Orange", "#FF9142"), ("Yellow", "#F9CB5C"),
            ("Green", "#5CCB7A"), ("Blue", "#147AD0"), ("Violet", "#C06ADE"), ("White", "#ECE8F3"),
        };

        private string? _fill;
        private readonly Border _fillSwatch = new() { Width = 12, Height = 12, CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1), BorderBrush = EditorTheme.TextDim };

        /// <summary>A button with a swatch and a flyout of the colours; `chosen` gets the colour (null: Default).</summary>
        private Control ColorButton(string label, string tip, Border swatchOfButton, Action<string?> chosen)
        {
            swatchOfButton.Background = Brushes.Transparent;
            var button = new Button { Content = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 5, Children = { swatchOfButton, new TextBlock { Text = label } } } };
            button.Classes.Add("tool");
            ToolTip.SetTip(button, tip);
            var panel = new WrapPanel { MaxWidth = 170, Margin = new Thickness(4) };
            var flyout = new Flyout { Content = panel, Placement = PlacementMode.BottomEdgeAlignedLeft };
            foreach (var (name, value) in Colors)
            {
                var swatch = new Button
                {
                    Width = 30, Height = 30, Margin = new Thickness(2), Padding = new Thickness(0),
                    Content = value == null
                        ? new TextBlock { Text = "∅", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center }
                        : new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Color.Parse(value)) },
                };
                ToolTip.SetTip(swatch, name);
                swatch.Click += (_, _) =>
                {
                    swatchOfButton.Background = value == null ? Brushes.Transparent : new SolidColorBrush(Color.Parse(value));
                    flyout.Hide();
                    chosen(value);
                };
                panel.Children.Add(swatch);
            }
            button.Flyout = flyout;
            return button;
        }

        /// <summary>Chooses a tool; the first tool of a canvas makes a Markdown document a canvas (after asking).</summary>
        private async System.Threading.Tasks.Task ChooseTool(OverviewTool tool)
        {
            if (tool != OverviewTool.Select && !await EnsureCanvas()) tool = OverviewTool.Select;
            SetTool(tool);
        }

        private void SetTool(OverviewTool tool)
        {
            _tool = tool;
            foreach (var (t, button) in _toolButtons) button.IsChecked = t == tool;
            if (Layout is Layouts.Code or Layouts.View && tool != OverviewTool.Select) Layout = Layouts.Surface;
            if (Preview.Content is OverviewView view) { view.EndEditing(); view.Focus(); }
        }

        private async System.Threading.Tasks.Task<bool> EnsureCanvas()
        {
            if (IsCanvas) return true;
            if (ConvertToCanvas == null || !await ConvertToCanvas(this)) return false;
            if (!IsCanvas) _madeCanvas = true;
            return true;
        }

        /// <summary>A container that shows a file, in the visible part of the surface.</summary>
        private async System.Threading.Tasks.Task Embed()
        {
            if (IsReadOnly || !await EnsureCanvas()) return;
            var top = TopLevel.GetTopLevel(this);
            if (top == null) return;
            var files = await top.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions { Title = "Embed a file", AllowMultiple = false });
            if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
            string src = BaseDirectory != null ? Path.GetRelativePath(BaseDirectory, path).Replace('\\', '/') : path;
            double zoom = Preview.Content is OverviewView v ? v.Zoom : 1;
            double x = Math.Round((Preview.Offset.X / zoom + 40) / 10) * 10, y = Math.Round((Preview.Offset.Y / zoom + 40) / 10) * 10;
            var host = _overviewHost ??= new OverviewHost(this);
            string name = OverviewSource.UniqueName(OverviewParser.Parse(Editor.Text), Path.GetFileNameWithoutExtension(path).Replace(' ', '_'));
            var (change, line) = OverviewSource.Insert(host.Lines, null,
                new[] { $"[container]: <x={OverviewSource.Number(x)} y={OverviewSource.Number(y)} w=300 src=\"{src}\"> \"{name}\"" });
            host.Replace(change.First, change.Count, change.Lines);
            host.SelectedLine = line;
            if (Layout is Layouts.Code or Layouts.View) Layout = Layouts.Surface;
        }

        private void FitToView()
        {
            if (Layout is Layouts.Code) Layout = Layouts.Surface;
            if (Preview.Content is OverviewView view)
            {
                view.FitTo(Preview.Viewport);
                _overviewZoom = view.Zoom;
                Dispatcher.UIThread.Post(() => Preview.Offset = default, DispatcherPriority.Loaded);
            }
        }

        /// <summary>Scrolls the preview to the heading with the given anchor (GitHub style, see <see cref="MdAnchors"/>).</summary>
        public bool ScrollToAnchor(string? anchor)
        {
            if (string.IsNullOrEmpty(anchor)) return false;
            string key = Uri.UnescapeDataString(anchor).ToLowerInvariant();
            if (!_renderer.Anchors.ContainsKey(key)) return false;
            // The heading is looked up again on every attempt: right after loading, the preview is rendered once more (the page and its headings are
            // replaced), and the position of a heading is known only when it is in the visual tree and laid out. Holding on to the block of the first
            // page (BringIntoView on it) is why the jump was lost.
            int attempts = 0;
            void Jump()
            {
                Preview.UpdateLayout();
                if (_renderer.Anchors.TryGetValue(key, out var block) && Preview.Content is Visual page && block.IsAttachedToVisualTree()
                    && block.TranslatePoint(new Point(0, 0), page) is { } position)
                    Preview.Offset = new Vector(Preview.Offset.X, Math.Max(0, position.Y - 6));
                else if (++attempts < 50)
                    DispatcherTimer.RunOnce(Jump, TimeSpan.FromMilliseconds(20));
            }
            Dispatcher.UIThread.Post(Jump, DispatcherPriority.Background);
            return true;
        }

        private void HandleLink(string url, KeyModifiers modifiers)
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
                bool newTab = (modifiers & KeyModifiers.Control) != 0;
                if (File.Exists(path)) OpenFileRequested?.Invoke(path, anchor, newTab);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex); // a broken link must not disturb the editor
            }
        }

        // -----------------------------------------------------------
        // Formatieren
        // -----------------------------------------------------------

        private void Bold_Click(object? sender, RoutedEventArgs e) => Wrap("**");
        private void Italic_Click(object? sender, RoutedEventArgs e) => Wrap("*");
        private void Strike_Click(object? sender, RoutedEventArgs e) => Wrap("~~");
        private void InlineCode_Click(object? sender, RoutedEventArgs e) => Wrap("`");
        private void Heading_Click(object? sender, RoutedEventArgs e) => CycleHeading();
        private void BulletList_Click(object? sender, RoutedEventArgs e) => TogglePrefix(_ => "- ", BulletPrefix);
        private void NumberedList_Click(object? sender, RoutedEventArgs e) => TogglePrefix(n => $"{n + 1}. ", NumberPrefix);
        private void Quote_Click(object? sender, RoutedEventArgs e) => TogglePrefix(_ => "> ", QuotePrefix);
        private void CodeBlock_Click(object? sender, RoutedEventArgs e) => InsertCodeBlock();
        private void Link_Click(object? sender, RoutedEventArgs e) => InsertLink();
        private void Table_Click(object? sender, RoutedEventArgs e) => InsertTable();

        private static readonly Regex BulletPrefix = new(@"^(\s*)[-*+]\s+", RegexOptions.Compiled);
        private static readonly Regex NumberPrefix = new(@"^(\s*)\d+[.)]\s+", RegexOptions.Compiled);
        private static readonly Regex QuotePrefix = new(@"^(\s*)>\s?", RegexOptions.Compiled);
        private static readonly Regex HeadingPrefix = new(@"^(#{1,6})\s+", RegexOptions.Compiled);

        private bool CanFormat() => !IsReadOnly;

        private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        /// <summary>Marks the selection (or the word at the cursor) with `marker`; if it is already marked like that, it is removed again.</summary>
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
                // From back to front, so that the offsets of the lines still to follow stay valid.
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
                        // Replace another list/quote kind first, do not stack.
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
                // Cursor into the first line of the block (behind the opening fences, to specify the language)
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
            const string table = "| Column 1 | Column 2 |\n|----------|----------|\n|          |          |\n";
            var doc = Editor.Document;
            var line = doc.GetLineByOffset(Editor.CaretOffset);
            string pre = Editor.CaretOffset == line.Offset ? "" : "\n";
            doc.Insert(Editor.CaretOffset, pre + table);
            Editor.Focus();
        }

        // -----------------------------------------------------------
        // Keyboard: shortcuts and list continuation
        // -----------------------------------------------------------

        private static readonly Regex ListLine = new(@"^(?<indent>\s*)(?<marker>[-*+]|\d+[.)])\s+(?<task>\[[ xX]\]\s+)?(?<rest>.*)$", RegexOptions.Compiled);

        private void Editor_KeyDown(object? sender, KeyEventArgs e)
        {
            bool ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
            bool shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
            bool alt = (e.KeyModifiers & KeyModifiers.Alt) != 0;

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

            if (e.Key == Key.Enter && !ctrl && !shift && !alt && Editor.SelectionLength == 0 && ContinueList())
                e.Handled = true;
        }

        /// <summary>Enter at the end of a list item: create the next item; an empty item ends the list.</summary>
        private bool ContinueList()
        {
            var doc = Editor.Document;
            int caret = Editor.CaretOffset;
            var line = doc.GetLineByOffset(caret);
            if (caret != line.EndOffset) return false;
            var m = ListLine.Match(doc.GetText(line.Offset, line.Length));
            if (!m.Success) return false;

            // Do not continue anything in a code block.
            if (_colorizer.FencedLines.Contains(line.LineNumber)) return false;

            if (m.Groups["rest"].Length == 0)
            {
                // Empty item: end the list (clear the line, leave it indented)
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
