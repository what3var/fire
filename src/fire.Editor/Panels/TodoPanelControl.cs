using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using fire.Projects;

namespace fire.Editor
{
    /// <summary>Whose todo list the Todo window shows: the project of the active document, or the solution.</summary>
    internal enum TodoScope { Project, Solution }

    /// <summary>
    /// The Todo window (docs/NOTES.md): the tasks of `notes/todo.md` of a project or the solution, grouped by the headings of the file. A click on the mark moves a task on
    /// (open → in progress → done → open), the context menu sets any state (blocked as well), a double click edits the text, the box at the bottom adds a task. Blocked tasks
    /// come first in their group. Every change goes to the file at once, but only to the lines it concerns (<see cref="TodoList"/>).
    /// The window knows only the text: which list it is and where it is written decides the main window (MainWindow.Notes.cs).
    /// </summary>
    internal sealed class TodoPanelControl : UserControl
    {
        private readonly ComboBox _scope = new() { MinWidth = 96, FontSize = 12 };
        private readonly TextBlock _target = new() { Foreground = EditorTheme.TextDim, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly StackPanel _rows = new() { Margin = new Thickness(0, 2, 0, 6) };
        private readonly TextBlock _empty = new() { Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap, Foreground = EditorTheme.TextDim };
        private readonly TextBlock _notice = new() { Margin = new Thickness(8, 4), TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = EditorTheme.Yellow, IsVisible = false };
        private readonly TextBox _add = new() { FontSize = 12, Watermark = "Add a task (Enter)", Margin = new Thickness(0, 0, 0, 2) };
        private readonly TextBlock _addTarget = new() { FontSize = 11, Foreground = EditorTheme.TextDim, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly Button _open;
        private readonly Dictionary<int, Border> _rowByLine = new();

        private TodoList _list = TodoList.Empty();
        private string? _path;
        private bool _readOnly;
        private int? _selectedLine;
        private int? _editingLine;
        private bool _settingScope;

        /// <summary>The user chose another scope.</summary>
        public event Action? ScopeChanged;
        /// <summary>`todo.md` should be opened as a document.</summary>
        public event Action<string>? OpenFileRequested;
        /// <summary>Writes the changed text of the list (file, open document); false: it could not be written (the list is shown as it was).</summary>
        public Func<string, string, bool>? Write { get; set; }

        public TodoScope Scope => _scope.SelectedIndex == 1 ? TodoScope.Solution : TodoScope.Project;

        /// <summary>The path of the list that is shown; null: none.</summary>
        public string? Path => _path;

        public TodoPanelControl()
        {
            MinWidth = 260;   // also when it is collapsed to the edge and shown as a flyout
            _scope.ItemsSource = new[] { "Project", "Solution" };
            _scope.SelectedIndex = 0;
            _scope.SelectionChanged += (_, _) => { if (!_settingScope) ScopeChanged?.Invoke(); };
            ToolTip.SetTip(_scope, "The todo list of the project of the active document, or of the solution");

            _open = new Button { Content = new TextBlock { Text = "Open todo.md", FontSize = 12 }, Padding = new Thickness(6, 2) };
            _open.Classes.Add("tool");
            ToolTip.SetTip(_open, "Open the todo list as a Markdown document");
            _open.Click += (_, _) => { if (_path != null) OpenFileRequested?.Invoke(_path); };

            var top = new DockPanel { Margin = new Thickness(4, 3) };
            DockPanel.SetDock(_scope, Avalonia.Controls.Dock.Left);
            DockPanel.SetDock(_open, Avalonia.Controls.Dock.Right);
            _target.Margin = new Thickness(8, 0, 4, 0);
            top.Children.Add(_scope);
            top.Children.Add(_open);
            top.Children.Add(_target);

            _add.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { AddFromBox(); e.Handled = true; }
                else if (e.Key == Key.Escape) { _add.Text = ""; e.Handled = true; }
            };
            var addArea = new StackPanel { Margin = new Thickness(4, 4, 4, 4), Children = { _add, _addTarget } };

            var scroll = new ScrollViewer { Content = _rows, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
            var listArea = new Grid { Children = { scroll, _empty } };
            listArea.ContextMenu = PanelMenu();
            KeyDown += OnKey;

            var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
            var topBorder = new Border { BorderBrush = EditorTheme.Border, BorderThickness = new Thickness(0, 0, 0, 1), Child = top };
            var addBorder = new Border { BorderBrush = EditorTheme.Border, BorderThickness = new Thickness(0, 1, 0, 0), Child = addArea };
            Grid.SetRow(topBorder, 0);
            Grid.SetRow(_notice, 1);
            Grid.SetRow(listArea, 2);
            Grid.SetRow(addBorder, 3);
            grid.Children.Add(topBorder);
            grid.Children.Add(_notice);
            grid.Children.Add(listArea);
            grid.Children.Add(addBorder);
            Content = grid;
            Show(null, null, null, null, null);
        }

        public void SetScope(TodoScope scope)
        {
            _settingScope = true;
            _scope.SelectedIndex = scope == TodoScope.Solution ? 1 : 0;
            _settingScope = false;
        }

        /// <summary>
        /// Shows a list: `title` names whose list it is, `path` is its file (null: there is no project or solution - nothing to show), `text` its text (null: the file does not
        /// exist yet), `readOnly` a reason why it cannot be changed now (null: it can), `empty` the text shown when there is no list.
        /// </summary>
        public void Show(string? title, string? path, string? text, string? readOnly, string? empty)
        {
            bool samePath = path != null && string.Equals(path, _path, StringComparison.OrdinalIgnoreCase);
            if (samePath && _editingLine != null && text == _list.ToString() && (readOnly != null) == _readOnly) return;   // nothing new: keep the edit box
            _path = path;
            _list = text == null ? TodoList.Empty() : TodoList.Parse(text);
            _readOnly = path == null || readOnly != null;
            if (!samePath) { _selectedLine = null; _editingLine = null; }
            _target.Text = title ?? "";
            _notice.Text = readOnly ?? "";
            _notice.IsVisible = readOnly != null && path != null;
            _add.IsEnabled = !_readOnly;
            _empty.Text = path == null ? (empty ?? "No project is open.") : "No tasks yet. Type one below: it is written to notes/todo.md.";
            Render();
        }

        // ---------------------------------------------------------------- showing

        private void Render()
        {
            _rows.Children.Clear();
            _rowByLine.Clear();
            var items = _list.Items;
            _empty.IsVisible = items.Count == 0;
            _open.IsEnabled = _path != null && _list.HasContent;
            if (_selectedLine != null && _list.ItemAt(_selectedLine.Value) == null) _selectedLine = null;

            // the groups in the order of the file; the tasks before the first group come first, without a heading
            var groups = new List<string?>();
            if (items.Any(i => i.Group == null)) groups.Add(null);
            groups.AddRange(_list.Groups.Where(g => items.Any(i => i.Group == g)));
            foreach (var group in groups)
            {
                if (group != null) _rows.Children.Add(GroupHeader(group, items.Where(i => i.Group == group).ToList()));
                foreach (var item in Ordered(items.Where(i => i.Group == group && i.Parent == null)))
                    AddRow(item);
            }
            UpdateAddTarget();
        }

        /// <summary>Blocked tasks first, the others in the order of the file.</summary>
        private static IEnumerable<TodoItem> Ordered(IEnumerable<TodoItem> items) =>
            items.Select((item, index) => (item, index)).OrderBy(x => x.item.State == TodoState.Blocked ? 0 : 1).ThenBy(x => x.index).Select(x => x.item);

        private void AddRow(TodoItem item)
        {
            _rows.Children.Add(Row(item));
            foreach (var child in Ordered(item.Children)) AddRow(child);
        }

        private static Control GroupHeader(string group, List<TodoItem> items)
        {
            int done = items.Count(i => i.State == TodoState.Done);
            var panel = new DockPanel { Margin = new Thickness(6, 8, 6, 2) };
            var count = new TextBlock { Text = $"{done}/{items.Count}", FontSize = 11, Foreground = EditorTheme.TextDim, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(count, Avalonia.Controls.Dock.Right);
            panel.Children.Add(count);
            panel.Children.Add(new TextBlock { Text = group, FontWeight = FontWeight.SemiBold, FontSize = 12, Foreground = EditorTheme.Text, TextTrimming = TextTrimming.CharacterEllipsis });
            return panel;
        }

        private static readonly IBrush BlockedBackground = new SolidColorBrush(Color.FromArgb(0x48, 0xE5, 0x56, 0x6F));
        private static readonly IBrush SelectedBackground = new SolidColorBrush(Color.FromArgb(0x50, 0xB7, 0x00, 0x52));
        private static readonly IBrush Accent = new SolidColorBrush(EditorTheme.AccentTextColor);

        private Control Row(TodoItem item)
        {
            int line = item.Line;
            var mark = Mark(item.State);
            ToolTip.SetTip(mark, item.State switch
            {
                TodoState.Open => "Open - click: in progress", TodoState.InProgress => "In progress - click: done",
                TodoState.Done => "Done - click: open", _ => "Blocked - click: open",
            });
            mark.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(mark).Properties.IsLeftButtonPressed) return;
                e.Handled = true;
                _selectedLine = line;
                Change(list => list.SetState(list.ItemAt(line)!, ProjectNotes.Next(item.State)));
            };

            Control content;
            if (_editingLine == line)
            {
                var box = new TextBox { Text = item.Text, FontSize = 12, Padding = new Thickness(4, 1), MinHeight = 0 };
                bool done = false;
                void Finish(bool keep)
                {
                    if (done) return;
                    done = true;
                    _editingLine = null;
                    if (keep && box.Text != item.Text) Change(list => list.SetText(list.ItemAt(line)!, box.Text ?? ""));
                    else Render();
                }
                box.KeyDown += (_, e) =>
                {
                    if (e.Key == Key.Enter) { Finish(true); e.Handled = true; }
                    else if (e.Key == Key.Escape) { Finish(false); e.Handled = true; }
                };
                box.LostFocus += (_, _) => Finish(true);
                box.AttachedToVisualTree += (_, _) => { box.Focus(); box.SelectAll(); };
                content = box;
            }
            else
            {
                var text = new TextBlock
                {
                    Text = item.Text.Length > 0 ? item.Text : "(no text)", FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
                    Foreground = item.State switch { TodoState.Done => EditorTheme.TextDim, TodoState.InProgress => Accent, _ => EditorTheme.Text },
                    FontWeight = item.State == TodoState.Blocked ? FontWeight.SemiBold : FontWeight.Normal,
                    TextDecorations = item.State == TodoState.Done ? TextDecorations.Strikethrough : null,
                    FontStyle = item.Text.Length > 0 ? FontStyle.Normal : FontStyle.Italic,
                };
                content = text;
            }

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(6 + 16 * item.Depth, 0, 6, 0) };
            Grid.SetColumn(content, 1);
            grid.Children.Add(mark);
            grid.Children.Add(content);

            var row = new Border
            {
                Child = grid, Padding = new Thickness(0, 3), Margin = new Thickness(2, 0), CornerRadius = new CornerRadius(3),
                Background = line == _selectedLine ? SelectedBackground : item.State == TodoState.Blocked ? BlockedBackground : Brushes.Transparent,
            };
            if (item.State == TodoState.Blocked) { row.BorderBrush = EditorTheme.WineRed; row.BorderThickness = new Thickness(3, 0, 0, 0); }
            row.PointerPressed += (_, e) =>
            {
                if (_editingLine == line) return;
                _selectedLine = line;
                UpdateSelection();
                Focus();
                if (e.ClickCount == 2 && !_readOnly && e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) { _editingLine = line; Render(); }
            };
            row.ContextMenu = ItemMenu(line);
            _rowByLine[line] = row;
            return row;
        }

        /// <summary>The mark of a state: an empty box (open), a box with a slash in the accent colour (in progress), a red stop sign (blocked), a tick (done).</summary>
        private static Border Mark(TodoState state)
        {
            var (glyph, foreground, border, background) = state switch
            {
                TodoState.InProgress => ("/", Accent, Accent, (IBrush)Brushes.Transparent),
                TodoState.Blocked => ("!", Brushes.White, EditorTheme.WineRed, EditorTheme.WineRed),
                TodoState.Done => ("✓", EditorTheme.TextDim, EditorTheme.TextDim, Brushes.Transparent),
                _ => ("", EditorTheme.Text, EditorTheme.TextDim, Brushes.Transparent),
            };
            return new Border
            {
                Width = 15, Height = 15, Margin = new Thickness(0, 1, 8, 0), VerticalAlignment = VerticalAlignment.Top, Cursor = new Cursor(StandardCursorType.Hand),
                BorderBrush = border, BorderThickness = new Thickness(1.5), Background = background,
                CornerRadius = new CornerRadius(state == TodoState.Blocked ? 7.5 : 3),
                Child = new TextBlock
                {
                    Text = glyph, Foreground = foreground, FontSize = 11, FontWeight = FontWeight.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
        }

        private void UpdateSelection()
        {
            foreach (var (line, row) in _rowByLine)
            {
                var item = _list.ItemAt(line);
                row.Background = line == _selectedLine ? SelectedBackground : item?.State == TodoState.Blocked ? BlockedBackground : Brushes.Transparent;
            }
            UpdateAddTarget();
        }

        private string? SelectedGroup => _selectedLine is { } line ? _list.ItemAt(line)?.Group : null;

        private void UpdateAddTarget()
        {
            string? group = SelectedGroup;
            _addTarget.Text = _readOnly || _path == null ? "" : group != null ? $"to: {group}" : _list.Groups.Count > 0 ? "to: the tasks without a group" : "";
            _addTarget.IsVisible = _addTarget.Text.Length > 0;
        }

        // ---------------------------------------------------------------- changing

        /// <summary>Changes the list and writes it; if it cannot be written, the list stays as it was.</summary>
        private void Change(Action<TodoList> change)
        {
            if (_readOnly || _path == null) { Render(); return; }
            string before = _list.ToString();
            try { change(_list); }
            catch (InvalidOperationException) { Render(); return; }   // the task is gone (the file changed meanwhile)
            if (Write?.Invoke(_path, _list.ToString()) != true) _list = TodoList.Parse(before);
            Render();
        }

        private void AddFromBox()
        {
            string text = (_add.Text ?? "").Trim();
            if (text.Length == 0 || _readOnly || _path == null) return;
            string? group = SelectedGroup;
            Change(list => _selectedLine = list.Add(text, group).Line);
            _add.Text = "";
        }

        private void OnKey(object? sender, KeyEventArgs e)
        {
            if (_editingLine != null || _selectedLine is not { } line || _list.ItemAt(line) is not { } item || e.Source is TextBox) return;
            switch (e.Key)
            {
                case Key.Space: Change(list => list.SetState(list.ItemAt(line)!, ProjectNotes.Next(item.State))); e.Handled = true; break;
                case Key.F2: if (!_readOnly) { _editingLine = line; Render(); } e.Handled = true; break;
                case Key.Delete: Change(list => { list.Remove(list.ItemAt(line)!); _selectedLine = null; }); e.Handled = true; break;
                case Key.Up or Key.Down:
                    var lines = _rowByLine.Keys.ToList();   // the order of the rows
                    int at = lines.IndexOf(line) + (e.Key == Key.Up ? -1 : 1);
                    if (at >= 0 && at < lines.Count) { _selectedLine = lines[at]; UpdateSelection(); }
                    e.Handled = true;
                    break;
            }
        }

        private ContextMenu ItemMenu(int line)
        {
            var menu = new ContextMenu();
            void State(string header, TodoState state)
            {
                var item = new MenuItem { Header = header, IsEnabled = !_readOnly };
                item.Click += (_, _) => Change(list => list.SetState(list.ItemAt(line)!, state));
                menu.Items.Add(item);
            }
            State("_Open", TodoState.Open);
            State("In _Progress", TodoState.InProgress);
            State("_Blocked", TodoState.Blocked);
            State("_Done", TodoState.Done);
            menu.Items.Add(new Separator());
            var edit = new MenuItem { Header = "_Edit Text", InputGesture = new KeyGesture(Key.F2), IsEnabled = !_readOnly };
            edit.Click += (_, _) => { _selectedLine = line; _editingLine = line; Render(); };
            menu.Items.Add(edit);
            var delete = new MenuItem { Header = "De_lete", InputGesture = new KeyGesture(Key.Delete), IsEnabled = !_readOnly };
            delete.Click += (_, _) => Change(list => { list.Remove(list.ItemAt(line)!); if (_selectedLine == line) _selectedLine = null; });
            menu.Items.Add(delete);
            menu.Items.Add(new Separator());
            var open = new MenuItem { Header = "Open todo.md" };
            open.Click += (_, _) => { if (_path != null) OpenFileRequested?.Invoke(_path); };
            menu.Items.Add(open);
            return menu;
        }

        private ContextMenu PanelMenu()
        {
            var menu = new ContextMenu();
            var open = new MenuItem { Header = "Open todo.md" };
            open.Click += (_, _) => { if (_path != null) OpenFileRequested?.Invoke(_path); };
            menu.Opening += (_, _) => open.IsEnabled = _path != null && _list.HasContent;
            menu.Items.Add(open);
            return menu;
        }
    }
}
