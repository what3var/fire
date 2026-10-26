using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using fire.Compiler;
using fire.UI.Markup;

namespace fire.Editor
{
    /// <summary>Editor of a UI markup file (.fxml, docs/UI_MARKUP.md): the XML text with colours, and next to it the design view - a picture of the interface (only to look at,
    /// see <see cref="MarkupPreviewControl"/>) that follows the text while it is typed; mistakes of the markup are listed under the picture and underlined in the text.</summary>
    public partial class MarkupEditorControl : UserControl, IDocumentView, IPropertyHost
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
                // an open list follows the text (also Backspace, which TextEntered does not see)
                if (_completionWindow != null) ShowOrUpdateCompletion(closeIfEmpty: true);
            };
            Editor.TextArea.TextEntered += Editor_TextEntered;
            Editor.AddHandler(KeyDownEvent, (object? _, KeyEventArgs e) =>
            {
                if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.Control)
                {
                    ShowOrUpdateCompletion(closeIfEmpty: true);
                    e.Handled = true;
                }
            }, RoutingStrategies.Tunnel);
            Editor.TextArea.Caret.PositionChanged += (_, _) =>
            {
                int line = GetCaretLine();
                Preview.HighlightLine = line;
                CaretLineChanged?.Invoke(line);
                UpdateProperties();
            };
            Preview.ElementClicked += SelectElementAtLine;
            Editor.AddHandler(PointerPressedEvent, (object? _, PointerPressedEventArgs e) =>
            {
                if (e.GetCurrentPoint(Editor).Properties.IsRightButtonPressed) Editor.PlaceCaretForContextMenu(e);
            }, RoutingStrategies.Tunnel);

            _searchPanel = AvaloniaEdit.Search.SearchPanel.Install(Editor);
            EditorTheme.Apply(Editor, _searchPanel);
            Editor.ContextMenu = EditorCommands.BuildMenu(EditorCommands.StandardEntries(Editor, Find).ToList());

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _timer.Tick += (_, _) => { _timer.Stop(); Refresh(); UpdateProperties(); };
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
            Preview.HighlightLine = GetCaretLine();

            // the problems of the markup itself, and - when it reads well - the ones the generator finds (values that do not fit their property)
            var problems = _document.Diagnostics.ToList();
            if (problems.Count == 0)
            {
                try { FireUiGenerator.Generate(_document, FilePath ?? ""); }
                catch (MarkupException ex) { problems.AddRange(ex.Diagnostics); }
            }
            // the picture: only a markup that reads well is drawn (the last picture stays while there are mistakes)
            if (problems.Count == 0) DrawPreview(_document);
            _problemLines = problems.Select(p => p.Line).ToList();
            Problems.ItemsSource = problems.Select(p => p.ToString()).ToList();
            Problems.IsVisible = problems.Count > 0;
            _squiggles.ErrorLines = problems.Where(p => p.Line > 0).Select(p => p.Line).ToHashSet();
            Editor.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Selection);
        }

        private List<int> _problemLines = new();

        private int _previewVersion;
        private static readonly object PreviewLock = new();

        /// <summary>Draws the interface with the library (in the background, one picture at a time; a result that a newer text has outdated is dropped).</summary>
        private void DrawPreview(MarkupDocument document)
        {
            int version = ++_previewVersion;
            string? path = FilePath;
            System.Threading.Tasks.Task.Run(() =>
            {
                UiPreviewResult result;
                lock (PreviewLock) result = UiPreview.Render(document, path);
                Dispatcher.UIThread.Post(() =>
                {
                    if (version != _previewVersion) return;
                    if (result.Ok)
                    {
                        Preview.SetRender(document, result);
                        PreviewError.IsVisible = false;
                    }
                    else
                    {
                        PreviewError.Text = result.Error ?? "The design view could not draw the interface.";
                        PreviewError.IsVisible = true;
                    }
                });
            });
        }

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
        // Properties: the element at the caret in the tool window "Properties"; changing a value changes its attribute in the text
        // -----------------------------------------------------------

        public PropertySet? Properties { get; private set; }
        public event Action<bool>? PropertiesChanged;
        // what the shown properties were built from (the window is only rebuilt when it changes)
        private string _propertiesKey = "\0";

        private void UpdateProperties()
        {
            string text = Editor.Text;
            var element = MarkupText.ElementAt(text, Editor.CaretOffset);
            string key = element == null ? "" : element.Tag + "\u0001" + element.Start + "\u0001" + string.Join("\u0001", element.Ancestors) + "\u0001" +
                string.Join("\u0001", element.Attributes.Select(a => a.Name + "=" + a.Value));
            if (key == _propertiesKey) return;
            _propertiesKey = key;
            Properties = element == null ? new PropertySet
            {
                Title = "No element",
                Hint = "Put the caret into an element of the markup, or click an element in the design view, to see and change its attributes.",
            } : BuildProperties(element);
            PropertiesChanged?.Invoke(false);
        }

        private PropertySet BuildProperties(MarkupTextElement element)
        {
            var entries = new List<PropertyEntry>();
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? Present(string name) => element.Attributes.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;
            foreach (var info in MarkupCompletion.AttributesFor(element.Tag, element.Ancestors))
            {
                known.Add(info.Name);
                string? value = Present(info.Name);
                var (kind, choices) = info.Property?.Kind switch
                {
                    PropertyKind.Bool => (PropertyEditKind.Choice, new[] { "true", "false" }),
                    PropertyKind.Orientation => (PropertyEditKind.Choice, new[] { "Horizontal", "Vertical" }),
                    PropertyKind.Enum => (PropertyEditKind.Choice, info.Property.Choices ?? Array.Empty<string>()),
                    PropertyKind.Color => (PropertyEditKind.Color, null),
                    _ => (PropertyEditKind.Text, (string[]?)null),
                };
                entries.Add(new PropertyEntry(info.Name, kind, value ?? "")
                {
                    IsSet = value != null, CanClear = true, Choices = choices, Tip = info.Detail,
                    Category = info.IsEvent ? "Events" : info.Name.Contains('.') ? "Layout in the container" : "",
                });
            }
            // attributes the schema does not know (namespaces, misspelt names): they can be changed or removed too
            foreach (var a in element.Attributes.Where(a => !known.Contains(a.Name)))
                entries.Add(new PropertyEntry(a.Name, PropertyEditKind.Text, a.Value) { CanClear = true, Category = "Other" });
            // the groups after each other (the order of the schema inside a group)
            string[] order = { "", "Layout in the container", "Events", "Other" };
            entries = entries.OrderBy(e => Array.IndexOf(order, e.Category)).ToList();

            string? name = Present("name") ?? Present("key");
            int start = element.Start;
            string tag = element.Tag;
            return new PropertySet
            {
                Title = "<" + tag + ">" + (name != null ? "  " + name : ""),
                Error = known.Count == 0 && !IsSpecialTag(tag) ? $"'{tag}' is no element of the library ui." : null,
                Entries = entries,
                Set = (attribute, value) => SetAttribute(start, tag, attribute, value),
            };
        }

        private static bool IsSpecialTag(string tag) =>
            new[] { "Window", "View", "Resources", "Converter", "Style", "Setter", "Trigger", "Condition", "ControlTemplate", "DataTemplate", "CollectionView", "Item", "Suggestion", "Column", "TreeNode" }
                .Contains(tag, StringComparer.OrdinalIgnoreCase);

        /// <summary>Sets (an empty value: removes) an attribute of the element that starts at `start`, as an edit of the text (one undo step).</summary>
        private void SetAttribute(int start, string tag, string attribute, string value)
        {
            string text = Editor.Text;
            var element = MarkupText.Elements(text).FirstOrDefault(e => e.Start == start && e.Tag == tag);
            if (element == null) return;
            var change = MarkupText.SetAttribute(text, element, attribute, value == "" ? null : value);
            if (change is not { } c) return;
            int caret = Editor.CaretOffset;
            Editor.Document.Replace(c.Start, c.End - c.Start, c.Text);
            // the caret stays in the element (a caret behind the change moves with it)
            if (caret > c.Start) Editor.CaretOffset = Math.Max(c.Start, Math.Min(Editor.Document.TextLength, caret + c.Text.Length - (c.End - c.Start)));
            UpdateProperties();
        }

        /// <summary>A click on an element in the design view: the caret goes into its start tag.</summary>
        private void SelectElementAtLine(int line)
        {
            if (line < 1 || line > Editor.Document.LineCount) return;
            var l = Editor.Document.GetLineByNumber(line);
            string text = Editor.Document.GetText(l);
            int lt = text.IndexOf('<');
            Editor.CaretOffset = l.Offset + (lt < 0 ? 0 : lt + 1);
            Editor.TextArea.Caret.BringCaretToView();
            UpdateProperties();
        }

        // -----------------------------------------------------------
        // IntelliSense: the logic is MarkupCompletion (fire.UI.Markup), here only the list of AvaloniaEdit
        // -----------------------------------------------------------

        private CompletionWindow? _completionWindow;

        private void Editor_TextEntered(object? sender, TextInputEventArgs e)
        {
            if (_completionWindow != null || string.IsNullOrEmpty(e.Text)) return;
            char c = e.Text[^1];
            bool opens = c is '<' or ' ' or '"' or '\'' or '{' or ',' or '=' or ':' or '[' or '/';
            if (!opens && (char.IsLetter(c) || c == '_'))
            {
                // only at the start of a word, not in the middle of one
                int before = Editor.CaretOffset - 2;
                opens = before < 0 || !(char.IsLetterOrDigit(Editor.Document.GetCharAt(before)) || Editor.Document.GetCharAt(before) is '_' or '.' or ':');
            }
            if (opens) ShowOrUpdateCompletion(closeIfEmpty: true);
        }

        private void ShowOrUpdateCompletion(bool closeIfEmpty)
        {
            int offset = Editor.CaretOffset;
            var result = MarkupCompletion.Suggest(Editor.Text, offset);
            if (result.Items.Count == 0)
            {
                if (closeIfEmpty) _completionWindow?.Close();
                return;
            }
            _completionWindow?.Close();
            var window = new CompletionWindow(Editor.TextArea) { StartOffset = result.ReplaceStart, EndOffset = offset };
            window.CompletionList.ListBox.Background = EditorTheme.DarkSurface;
            window.CompletionList.ListBox.Foreground = EditorTheme.Text;
            foreach (var item in result.Items) window.CompletionList.CompletionData.Add(new MarkupCompletionData(item));
            window.CompletionList.SelectedItem = window.CompletionList.CompletionData[0];
            window.Closed += (_, _) => { if (_completionWindow == window) _completionWindow = null; };
            _completionWindow = window;
            window.Show();
        }

        /// <summary>A suggestion of <see cref="MarkupCompletion"/> in the list of AvaloniaEdit: inserting it puts the caret into the quotes of `name=""`.</summary>
        private sealed class MarkupCompletionData : ICompletionData
        {
            private readonly MarkupCompletionItem _item;
            public MarkupCompletionData(MarkupCompletionItem item) => _item = item;
            public Avalonia.Media.IImage? Image => null;
            public string Text => _item.Label;
            public object Content => _item.Label;
            public object Description => _item.Detail ?? _item.Kind.ToString();
            public double Priority => 0;

            public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
            {
                textArea.Document.Replace(completionSegment, _item.Text);
                if (_item.CaretBack > 0) textArea.Caret.Offset -= _item.CaretBack;
            }
        }

        // -----------------------------------------------------------
        // Insert elements
        // -----------------------------------------------------------

        private static readonly (string Title, string Snippet)[] Snippets =
        {
            ("Label", "<Label Text=\"Text\"/>"),
            ("Button", "<Button Name=\"ok\" Text=\"OK\" OnClick=\"Ok\"/>"),
            ("CheckBox", "<CheckBox Name=\"flag\" Text=\"Option\" IsChecked=\"{Binding Flag, Mode=TwoWay}\"/>"),
            ("TextBox", "<TextBox Name=\"name\" Width=\"200\" Text=\"{Binding Name, Mode=TwoWay}\"/>"),
            ("Stack", "<Stack Width=\"200\" Height=\"100\" Orientation=\"Vertical\" Spacing=\"6\">\n  \n</Stack>"),
            ("Panel", "<Panel Left=\"0\" Top=\"0\" Width=\"200\" Height=\"100\" ShowBorder=\"true\">\n  \n</Panel>"),
            ("StackPanel", "<StackPanel Spacing=\"6\" Padding=\"4\">\n  \n</StackPanel>"),
            ("Grid", "<Grid Rows=\"auto, *\" Columns=\"100, *\">\n  <Label Grid.Row=\"0\" Grid.Column=\"0\" Text=\"Name:\"/>\n  <TextBox Grid.Row=\"0\" Grid.Column=\"1\"/>\n</Grid>"),
            ("DockPanel", "<DockPanel Width=\"400\" Height=\"300\">\n  <ToolBar DockPanel.Dock=\"Top\">\n    <Button Text=\"Open\"/>\n  </ToolBar>\n  \n</DockPanel>"),
            ("ScrollViewer", "<ScrollViewer Width=\"200\" Height=\"100\">\n  <StackPanel>\n    \n  </StackPanel>\n</ScrollViewer>"),
            ("ListBox", "<ListBox Name=\"list\" Width=\"160\" Height=\"100\" OnSelect=\"Picked\">\n  <Item>One</Item>\n  <Item>Two</Item>\n</ListBox>"),
            ("ComboBox", "<ComboBox Name=\"choice\" Width=\"140\" SelectedIndex=\"0\">\n  <Item>One</Item>\n  <Item>Two</Item>\n</ComboBox>"),
            ("ListView", "<ListView Width=\"240\" Height=\"120\">\n  <Column Header=\"Name\" Member=\"name\" Width=\"120\"/>\n  <Column Header=\"Age\" Member=\"age\"/>\n</ListView>"),
            ("TreeView", "<TreeView Width=\"200\" Height=\"120\">\n  <TreeNode Text=\"root\" Expanded=\"true\">\n    <TreeNode Text=\"child\"/>\n  </TreeNode>\n</TreeView>"),
            ("RadioButtons", "<RadioButtons Header=\"Size\" SelectedIndex=\"0\">\n  <Item>Small</Item>\n  <Item>Large</Item>\n</RadioButtons>"),
            ("AutoSuggestBox", "<AutoSuggestBox Width=\"160\">\n  <Suggestion>apple</Suggestion>\n  <Suggestion>banana</Suggestion>\n</AutoSuggestBox>"),
            ("MenuBar", "<MenuBar>\n  <Menu Header=\"File\">\n    <MenuItem Header=\"Open\" OnClick=\"OpenFile\"/>\n    <MenuSeparator/>\n    <MenuItem Header=\"Quit\" OnClick=\"Quit\"/>\n  </Menu>\n</MenuBar>"),
            ("ToolBar", "<ToolBar>\n  <Button Text=\"One\" OnClick=\"One\"/>\n  <Separator/>\n  <Button Text=\"Two\"/>\n</ToolBar>"),
            ("Image", "<Image Source=\"logo.png\" Width=\"64\" Height=\"64\" Stretch=\"Uniform\"/>"),
            ("Rectangle", "<Rectangle Width=\"60\" Height=\"30\" Fill=\"#FFCC00\" Stroke=\"#000000\"/>"),
            ("Path", "<Path Data=\"M 0 0 L 40 0 L 20 30 Z\" Fill=\"#66AAFF\" Stroke=\"#003366\"/>"),
            ("DrawingCanvas", "<DrawingCanvas Name=\"canvas\" Width=\"200\" Height=\"120\" OnPaint=\"Paint\"/>"),
            ("Converter", "<Resources>\n  <Converter Key=\"Upper\" Type=\"UpperConverter\"/>\n</Resources>"),
            ("Style", "<Resources>\n  <Style Key=\"Primary\" Target=\"Button\">\n    <Setter Property=\"Background\" Value=\"#3366AA\"/>\n    <Setter Property=\"Foreground\" Value=\"#FFFFFF\"/>\n    <Trigger Property=\"Hover\" Value=\"true\">\n      <Setter Property=\"Background\" Value=\"#4477BB\"/>\n    </Trigger>\n  </Style>\n</Resources>"),
            ("DataTemplate", "<Resources>\n  <DataTemplate Key=\"Person\">\n    <StackPanel Horizontal=\"true\" Spacing=\"6\">\n      <Label Text=\"{Binding name}\"/>\n      <Label Text=\"{Binding age}\"/>\n    </StackPanel>\n  </DataTemplate>\n</Resources>"),
            ("CollectionView", "<Resources>\n  <CollectionView Key=\"ByName\" Source=\"{Binding people}\" SortBy=\"name\"/>\n</Resources>"),
            ("List from data", "<ListBox View=\"ByName\" ItemTemplate=\"Person\" Width=\"200\" Height=\"120\"/>"),
            ("ControlTemplate", "<Resources>\n  <ControlTemplate Key=\"Fancy\" Target=\"Button\">\n    <Border Name=\"bd\" Background=\"#33AA66\" Padding=\"4\">\n      <Label Text=\"{TemplateBinding Text}\"/>\n    </Border>\n    <Trigger Property=\"Pressed\" Value=\"true\">\n      <Setter Target=\"bd\" Property=\"Background\" Value=\"#AA3333\"/>\n    </Trigger>\n  </ControlTemplate>\n</Resources>"),
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
            UpdateProperties();
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
