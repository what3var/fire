using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace fire.Editor
{
    /// <summary>How the property window edits a value.</summary>
    public enum PropertyEditKind
    {
        /// <summary>A line of text (numbers too); <see cref="PropertyEntry.Unit"/> is shown at its end.</summary>
        Text,
        /// <summary>A check box (true/false).</summary>
        Bool,
        /// <summary>A list of <see cref="PropertyEntry.Choices"/>.</summary>
        Choice,
        /// <summary>A colour (#RRGGBB) as text with a swatch.</summary>
        Color,
        /// <summary>A slider from <see cref="PropertyEntry.Minimum"/> to <see cref="PropertyEntry.Maximum"/> (the position of a potentiometer).</summary>
        Slider,
        /// <summary>A button that is "true" while it is held down and "false" when it is let go (a push button).</summary>
        Momentary,
    }

    /// <summary>One row of the property window: the name, how it is edited and its value. `IsSet` false: the value is not given (the markup has no attribute of this name) -
    /// the row shows `Value` as the default in grey; an empty `Choices` entry or an emptied text box removes the value again (when `CanClear`).</summary>
    public sealed record PropertyEntry(string Name, PropertyEditKind Kind, string Value)
    {
        public bool IsSet { get; init; } = true;
        public bool CanClear { get; init; }
        public string? Unit { get; init; }
        public IReadOnlyList<string>? Choices { get; init; }
        public string? Tip { get; init; }
        /// <summary>The group the row is listed under ("" = at the top, without a heading).</summary>
        public string Category { get; init; } = "";
        /// <summary>The range of a <see cref="PropertyEditKind.Slider"/>.</summary>
        public double Minimum { get; init; }
        public double Maximum { get; init; } = 1;
    }

    /// <summary>What the property window shows: the selected thing of a document. Built anew by the document whenever its selection or the values change.</summary>
    public sealed class PropertySet
    {
        public string Title { get; init; } = "";
        /// <summary>A problem of the selected thing (shown in red under the title).</summary>
        public string? Error { get; init; }
        /// <summary>Shown when there are no rows (nothing selected): what to do.</summary>
        public string? Hint { get; init; }
        public IReadOnlyList<PropertyEntry> Entries { get; init; } = Array.Empty<PropertyEntry>();
        /// <summary>Called with the name and the new text of a row (an empty text: remove, when the row can be cleared).</summary>
        public Action<string, string>? Set { get; init; }
    }

    /// <summary>A document whose selection has properties (the circuit editor, the UI markup editor): the property window follows the active one.</summary>
    public interface IPropertyHost
    {
        PropertySet? Properties { get; }
        /// <summary>The properties changed; `focusFirst`: the user asked to edit them (the first text field gets the focus).</summary>
        event Action<bool>? PropertiesChanged;
        /// <summary>After Enter in a field: the focus goes back to the document.</summary>
        void FocusEditor();
    }

    /// <summary>The tool window "Properties": the properties of the selection of the active document, shared by all editors that have one (see <see cref="IPropertyHost"/>).
    /// It looks like the property grid of .NET: a table of names and values with lines between the cells, categories that fold, a column border that can be
    /// dragged, sorting by category or by name, and a description of the selected row at the bottom.</summary>
    public sealed class PropertiesPanelControl : UserControl
    {
        private const double RowHeight = 24;
        private static readonly IBrush GridLine = new SolidColorBrush(Color.FromRgb(0x36, 0x30, 0x3D));
        private static readonly IBrush CategoryBack = new SolidColorBrush(Color.FromRgb(0x2E, 0x29, 0x34));
        private static readonly IBrush SelectedBack = new SolidColorBrush(Color.FromArgb(0xC0, EditorTheme.AccentColor.R, EditorTheme.AccentColor.G, EditorTheme.AccentColor.B));

        private readonly TextBlock _title = new() { FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _error = new() { Foreground = EditorTheme.ErrorMark, TextWrapping = TextWrapping.Wrap, IsVisible = false, Margin = new Thickness(6, 0, 6, 4) };
        private readonly ToggleButton _byCategory = new() { Content = "▤", IsChecked = true, Padding = new Thickness(6, 1), MinHeight = 0 };
        private readonly ToggleButton _byName = new() { Content = "A-Z", Padding = new Thickness(6, 1), MinHeight = 0 };
        private readonly Grid _table = new();
        private readonly TextBlock _hint = new() { Foreground = EditorTheme.TextDim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8) };
        private readonly TextBlock _descName = new() { FontWeight = FontWeight.SemiBold };
        private readonly TextBlock _descText = new() { Foreground = EditorTheme.TextDim, TextWrapping = TextWrapping.Wrap };
        private GridLength _nameWidth = new(120);
        private readonly HashSet<string> _collapsed = new();
        private readonly Dictionary<string, (Border Name, Control Field, PropertyEntry Entry)> _cells = new();
        private IPropertyHost? _host;
        private PropertySet? _shown;
        private string? _selected;

        public PropertiesPanelControl()
        {
            ToolTip.SetTip(_byCategory, "Categorized");
            ToolTip.SetTip(_byName, "Alphabetical");
            _byCategory.Click += (_, _) => { _byCategory.IsChecked = true; _byName.IsChecked = false; Rebuild(false); };
            _byName.Click += (_, _) => { _byName.IsChecked = true; _byCategory.IsChecked = false; Rebuild(false); };

            var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            tools.Children.Add(_byCategory);
            tools.Children.Add(_byName);
            var head = new DockPanel { Margin = new Thickness(6, 4) };
            DockPanel.SetDock(tools, Avalonia.Controls.Dock.Right);
            head.Children.Add(tools);
            head.Children.Add(_title);
            var top = new StackPanel();
            top.Children.Add(head);
            top.Children.Add(_error);

            var description = new Border
            {
                Background = EditorTheme.DarkSurface, BorderBrush = GridLine, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(8, 6), MinHeight = 64,
                Child = new StackPanel { Spacing = 2, Children = { _descName, _descText } },
            };
            var body = new StackPanel { Children = { _hint, new Border { BorderBrush = GridLine, BorderThickness = new Thickness(0, 1, 0, 0), Child = _table } } };
            var scroll = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };

            var dock = new DockPanel();
            DockPanel.SetDock(top, Avalonia.Controls.Dock.Top);
            DockPanel.SetDock(description, Avalonia.Controls.Dock.Bottom);
            dock.Children.Add(top);
            dock.Children.Add(description);
            dock.Children.Add(scroll);
            Content = dock;
            Show(null, false);
        }

        /// <summary>Follows another document (null: none that has properties).</summary>
        public void Attach(IPropertyHost? host)
        {
            if (ReferenceEquals(host, _host)) return;
            if (_host != null) _host.PropertiesChanged -= OnChanged;
            _host = host;
            if (_host != null) _host.PropertiesChanged += OnChanged;
            Show(_host?.Properties, false);
        }

        private void OnChanged(bool focusFirst) => Show(_host?.Properties, focusFirst);

        private void Show(PropertySet? set, bool focusFirst)
        {
            if (!ReferenceEquals(set, _shown) && set != null && _shown != null && set.Title != _shown.Title) _selected = null;
            // the same rows with other values (a slider is dragged, the simulation reports a new position): the fields are kept and only show the new values -
            // a rebuild would take the slider away from under the mouse
            if (!focusFirst && set != null && _shown != null && SameRows(set, _shown))
            {
                _shown = set;
                foreach (var entry in set.Entries) if (_cells.TryGetValue(entry.Name, out var cell)) { _cells[entry.Name] = (cell.Name, cell.Field, entry); ShowValue(cell.Field, entry); }
                _error.Text = set.Error ?? "";
                _error.IsVisible = !string.IsNullOrEmpty(set.Error);
                return;
            }
            _shown = set;
            Rebuild(focusFirst);
        }

        private bool SameRows(PropertySet a, PropertySet b)
        {
            if (a.Title != b.Title || a.Entries.Count != b.Entries.Count) return false;
            for (int i = 0; i < a.Entries.Count; i++)
            {
                var x = a.Entries[i];
                var y = b.Entries[i];
                if (x.Name != y.Name || x.Kind != y.Kind || x.Category != y.Category || x.IsSet != y.IsSet || x.Unit != y.Unit) return false;
                if (!(x.Choices ?? Array.Empty<string>()).SequenceEqual(y.Choices ?? Array.Empty<string>())) return false;
            }
            // folded categories have no fields: a rebuild is cheap and right then
            return a.Entries.All(e => _cells.ContainsKey(e.Name));
        }

        /// <summary>Puts a new value into a field that is already there (not into the one being edited).</summary>
        private static void ShowValue(Control field, PropertyEntry entry)
        {
            switch (field)
            {
                case TextBox box when !box.IsKeyboardFocusWithin: box.Text = entry.IsSet ? entry.Value : ""; break;
                case CheckBox check: check.IsChecked = entry.Value == "true"; break;
                case ComboBox combo when !combo.IsDropDownOpen: if (combo.Items.Contains(entry.Value)) combo.SelectedItem = entry.Value; break;
                case Slider slider when slider.Tag == null:
                    if (double.TryParse(entry.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v)) slider.Value = v;
                    break;
            }
        }

        private void Rebuild(bool focusFirst)
        {
            var set = _shown;
            // the field that has the focus is found again after the rebuild (Tab from one field to the next sets the first one, which rebuilds the list)
            string? focused = _cells.FirstOrDefault(c => c.Value.Field.IsKeyboardFocusWithin).Key;
            _cells.Clear();
            _table.Children.Clear();
            _table.RowDefinitions.Clear();
            _table.ColumnDefinitions = new ColumnDefinitions { new(14, GridUnitType.Pixel), new(_nameWidth.Value, GridUnitType.Pixel), new(5, GridUnitType.Pixel), new(1, GridUnitType.Star) };
            _error.IsVisible = false;
            if (set == null)
            {
                _title.Text = "";
                _hint.Text = "Select something in a schematic or put the caret into an element of a UI markup file to see its properties here.";
                _hint.IsVisible = true;
                Describe(null);
                return;
            }
            _title.Text = set.Title;
            if (!string.IsNullOrEmpty(set.Error)) { _error.Text = set.Error; _error.IsVisible = true; }
            _hint.Text = set.Hint ?? "";
            _hint.IsVisible = set.Entries.Count == 0 && set.Hint != null;

            bool categorized = _byCategory.IsChecked == true;
            var entries = categorized ? set.Entries.ToList() : set.Entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
            string? category = null;
            int row = 0;
            Control? first = null, again = null;
            foreach (var entry in entries)
            {
                if (categorized && entry.Category != "" && entry.Category != category)
                {
                    category = entry.Category;
                    AddCategory(row++, category);
                }
                if (categorized && entry.Category != "" && _collapsed.Contains(entry.Category)) continue;
                var field = Field(set, entry);
                AddRow(row++, entry, field);
                if (first == null && field is TextBox) first = field;
                if (focused != null && entry.Name == focused) again = field;
            }
            if (row > 0)
            {
                var splitter = new GridSplitter { Background = Brushes.Transparent, ResizeDirection = GridResizeDirection.Columns, Width = 5 };
                Grid.SetColumn(splitter, 2);
                Grid.SetRowSpan(splitter, row);
                splitter.DragCompleted += (_, _) => _nameWidth = new GridLength(Math.Max(40, _table.ColumnDefinitions[1].ActualWidth));
                _table.Children.Add(splitter);
            }
            if (_selected != null && !_cells.ContainsKey(_selected)) _selected = null;
            Highlight();
            var target = focusFirst ? first : again;
            if (target != null) Avalonia.Threading.Dispatcher.UIThread.Post(() => { target.Focus(); if (target is TextBox t) t.SelectAll(); });
        }

        private void AddCategory(int row, string name)
        {
            _table.RowDefinitions.Add(new RowDefinition(RowHeight, GridUnitType.Pixel));
            bool open = !_collapsed.Contains(name);
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0) };
            line.Children.Add(new TextBlock { Text = open ? "▾" : "▸", Foreground = EditorTheme.TextDim, Width = 10 });
            line.Children.Add(new TextBlock { Text = name, FontWeight = FontWeight.SemiBold });
            var cell = new Border { Background = CategoryBack, BorderBrush = GridLine, BorderThickness = new Thickness(0, 0, 0, 1), Child = line, Cursor = new Cursor(StandardCursorType.Hand) };
            cell.PointerPressed += (_, e) =>
            {
                if (!_collapsed.Remove(name)) _collapsed.Add(name);
                e.Handled = true;
                Rebuild(false);
            };
            Grid.SetRow(cell, row);
            Grid.SetColumnSpan(cell, 4);
            _table.Children.Add(cell);
        }

        private void AddRow(int row, PropertyEntry entry, Control field)
        {
            _table.RowDefinitions.Add(new RowDefinition(RowHeight, GridUnitType.Pixel));
            var gutter = new Border { Background = CategoryBack };
            Grid.SetRow(gutter, row);
            _table.Children.Add(gutter);

            var text = new TextBlock
            {
                Text = entry.Name, Margin = new Thickness(4, 0), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
                Foreground = entry.IsSet ? EditorTheme.Text : EditorTheme.TextDim,
            };
            var name = new Border { BorderBrush = GridLine, BorderThickness = new Thickness(0, 0, 1, 1), Background = Brushes.Transparent, Child = text };
            name.PointerPressed += (_, e) => { Select(entry.Name); field.Focus(); e.Handled = true; };
            Grid.SetRow(name, row);
            Grid.SetColumn(name, 1);
            _table.Children.Add(name);

            var value = new Border { BorderBrush = GridLine, BorderThickness = new Thickness(0, 0, 0, 1), Child = field };
            Grid.SetRow(value, row);
            Grid.SetColumn(value, 2);
            Grid.SetColumnSpan(value, 2);
            value.Margin = new Thickness(5, 0, 0, 0);
            _table.Children.Add(value);

            if (entry.Tip != null) ToolTip.SetTip(text, entry.Tip);
            field.GotFocus += (_, _) => Select(entry.Name);
            _cells[entry.Name] = (name, field, entry);
        }

        private void Select(string name)
        {
            _selected = name;
            Highlight();
        }

        private void Highlight()
        {
            foreach (var (key, cell) in _cells)
            {
                bool on = key == _selected;
                cell.Name.Background = on ? SelectedBack : Brushes.Transparent;
                if (cell.Name.Child is TextBlock t) t.Foreground = on ? Brushes.White : cell.Entry.IsSet ? EditorTheme.Text : EditorTheme.TextDim;
            }
            Describe(_selected != null && _cells.TryGetValue(_selected, out var c) ? c.Entry : null);
        }

        private void Describe(PropertyEntry? entry)
        {
            if (entry == null)
            {
                _descName.Text = _shown?.Title ?? "";
                _descText.Text = _shown == null ? "" : "Select a row to see what it means.";
                return;
            }
            _descName.Text = entry.Name + (entry.Unit is { Length: > 0 } u ? " (" + u + ")" : "");
            var text = entry.Tip ?? "";
            if (!entry.IsSet && entry.Value != "") text = (text == "" ? "" : text + "\n") + "Not set; the default is " + entry.Value + ".";
            _descText.Text = text;
        }

        /// <summary>A field was edited: the row's value (as it is shown now) is compared, and the document gets the new one.</summary>
        private void Commit(Control field, string name, string value)
        {
            var set = _shown;
            // a field of an earlier list (it lost the focus while the list was built anew) changes nothing
            if (set == null || !_cells.TryGetValue(name, out var cell) || !ReferenceEquals(cell.Field, field)) return;
            var entry = cell.Entry;
            if (entry.Kind != PropertyEditKind.Momentary && (entry.IsSet ? value == entry.Value : value == "")) return;
            set.Set?.Invoke(entry.Name, value);
        }

        private Control Field(PropertySet set, PropertyEntry entry)
        {
            switch (entry.Kind)
            {
                case PropertyEditKind.Slider:
                {
                    double.TryParse(entry.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v);
                    var slider = new Slider
                    {
                        Minimum = entry.Minimum, Maximum = entry.Maximum, Value = v, Margin = new Thickness(6, 0), MinHeight = 0, VerticalAlignment = VerticalAlignment.Center,
                        SmallChange = (entry.Maximum - entry.Minimum) / 100, LargeChange = (entry.Maximum - entry.Minimum) / 10,
                    };
                    // while the thumb is dragged, the values the document reports back do not move it
                    slider.AddHandler(InputElement.PointerPressedEvent, (_, _) => slider.Tag = "drag", Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
                    slider.AddHandler(InputElement.PointerReleasedEvent, (_, _) => slider.Tag = null, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
                    slider.ValueChanged += (_, e) => Commit(slider, entry.Name, Math.Round(e.NewValue, 4).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    return slider;
                }
                case PropertyEditKind.Momentary:
                {
                    var button = new Button { Content = "Press", Padding = new Thickness(10, 0), Margin = new Thickness(4, 2), MinHeight = 0, VerticalAlignment = VerticalAlignment.Stretch };
                    button.AddHandler(InputElement.PointerPressedEvent, (_, _) => Commit(button, entry.Name, "true"), Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
                    button.AddHandler(InputElement.PointerReleasedEvent, (_, _) => Commit(button, entry.Name, "false"), Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
                    button.AddHandler(InputElement.PointerCaptureLostEvent, (_, _) => Commit(button, entry.Name, "false"), Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
                    return button;
                }
                case PropertyEditKind.Bool:
                {
                    var check = new CheckBox { IsChecked = entry.Value == "true", Margin = new Thickness(4, 0), MinHeight = 0, VerticalAlignment = VerticalAlignment.Center };
                    check.Click += (_, _) => Commit(check, entry.Name, check.IsChecked == true ? "true" : "false");
                    return check;
                }
                case PropertyEditKind.Choice:
                {
                    const string unset = "(default)";
                    var options = (entry.Choices ?? Array.Empty<string>()).ToList();
                    if (entry.IsSet && !options.Contains(entry.Value)) options.Insert(0, entry.Value);   // a value the list does not know (a binding, an old name)
                    if (entry.CanClear) options.Insert(0, unset);
                    var combo = new ComboBox
                    {
                        HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, MinHeight = 0, Padding = new Thickness(4, 0),
                        BorderThickness = new Thickness(0), Background = Brushes.Transparent, CornerRadius = new CornerRadius(0), VerticalContentAlignment = VerticalAlignment.Center,
                    };
                    foreach (var o in options) combo.Items.Add(o);
                    combo.SelectedItem = entry.IsSet ? entry.Value : entry.CanClear ? unset : entry.Value;
                    combo.SelectionChanged += (_, _) =>
                    {
                        if (combo.SelectedItem is string s) Commit(combo, entry.Name, s == unset ? "" : s);
                    };
                    return combo;
                }
                default:
                {
                    var box = new TextBox
                    {
                        Text = entry.IsSet ? entry.Value : "", Watermark = entry.IsSet || entry.Value == "" ? null : entry.Value,
                        BorderThickness = new Thickness(0), Background = Brushes.Transparent, MinHeight = 0, Padding = new Thickness(4, 0), CornerRadius = new CornerRadius(0),
                        VerticalContentAlignment = VerticalAlignment.Center, VerticalAlignment = VerticalAlignment.Stretch,
                    };
                    if (entry.Unit is { Length: > 0 } unit)
                        box.InnerRightContent = new TextBlock { Text = unit, Foreground = EditorTheme.TextDim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0) };
                    if (entry.Kind == PropertyEditKind.Color)
                    {
                        var swatch = new Rectangle { Width = 12, Height = 12, Margin = new Thickness(4, 0, 0, 0), Stroke = EditorTheme.Border, StrokeThickness = 1, VerticalAlignment = VerticalAlignment.Center };
                        void Paint() => swatch.Fill = Color.TryParse(box.Text ?? "", out var c) ? new SolidColorBrush(c) : Brushes.Transparent;
                        Paint();
                        box.TextChanged += (_, _) => Paint();
                        box.InnerLeftContent = swatch;
                    }
                    box.LostFocus += (_, _) => Commit(box, entry.Name, box.Text ?? "");
                    box.KeyDown += (_, e) =>
                    {
                        if (e.Key == Key.Enter) { Commit(box, entry.Name, box.Text ?? ""); _host?.FocusEditor(); e.Handled = true; }
                        else if (e.Key == Key.Escape) { box.Text = entry.IsSet ? entry.Value : ""; _host?.FocusEditor(); e.Handled = true; }
                    };
                    return box;
                }
            }
        }
    }
}
