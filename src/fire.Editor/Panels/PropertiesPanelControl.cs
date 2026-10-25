using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
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

    /// <summary>The tool window "Properties": the properties of the selection of the active document, shared by all editors that have one (see <see cref="IPropertyHost"/>).</summary>
    public sealed class PropertiesPanelControl : UserControl
    {
        private readonly StackPanel _rows = new() { Spacing = 4, Margin = new Thickness(8) };
        private IPropertyHost? _host;
        private PropertySet? _shown;

        public PropertiesPanelControl()
        {
            Content = new ScrollViewer { Content = _rows, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
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
            // the field that has the focus is found again after the rebuild (Tab from one field to the next sets the first one, which rebuilds the list)
            string? focused = _rows.Children.OfType<Grid>().FirstOrDefault(g => g.Children.Count > 1 && g.Children[1].IsKeyboardFocusWithin)?.Tag as string;
            _shown = set;
            _rows.Children.Clear();
            if (set == null)
            {
                _rows.Children.Add(new TextBlock { Text = "Select something in a schematic or put the caret into an element of a UI markup file to see its properties here.", Foreground = EditorTheme.TextDim, TextWrapping = TextWrapping.Wrap });
                return;
            }
            if (set.Title != "") _rows.Children.Add(new TextBlock { Text = set.Title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
            if (!string.IsNullOrEmpty(set.Error)) _rows.Children.Add(new TextBlock { Text = set.Error, Foreground = EditorTheme.ErrorMark, TextWrapping = TextWrapping.Wrap });
            if (set.Entries.Count == 0 && set.Hint != null)
                _rows.Children.Add(new TextBlock { Text = set.Hint, Foreground = EditorTheme.TextDim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });

            string? category = null;
            Control? first = null, again = null;
            foreach (var entry in set.Entries)
            {
                if (entry.Category != (category ?? ""))
                {
                    category = entry.Category;
                    _rows.Children.Add(new TextBlock { Text = category, Foreground = EditorTheme.TextDim, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
                }
                category ??= entry.Category;
                var field = Field(set, entry);
                var row = Row(entry, field);
                _rows.Children.Add(row);
                if (first == null && field is TextBox) first = field;
                if (focused != null && entry.Name == focused) again = field;
            }
            var target = focusFirst ? first : again;
            if (target != null) Avalonia.Threading.Dispatcher.UIThread.Post(() => { target.Focus(); if (target is TextBox t) t.SelectAll(); });
        }

        private void Commit(PropertySet set, PropertyEntry entry, string value)
        {
            if (!ReferenceEquals(set, _shown)) return;   // an old list (the document reported new values meanwhile)
            if (entry.IsSet ? value == entry.Value : value == "") return;
            set.Set?.Invoke(entry.Name, value);
        }

        private Control Field(PropertySet set, PropertyEntry entry)
        {
            switch (entry.Kind)
            {
                case PropertyEditKind.Bool:
                {
                    var check = new CheckBox { IsChecked = entry.Value == "true" };
                    check.Click += (_, _) => Commit(set, entry, check.IsChecked == true ? "true" : "false");
                    return check;
                }
                case PropertyEditKind.Choice:
                {
                    const string unset = "(default)";
                    var options = (entry.Choices ?? Array.Empty<string>()).ToList();
                    if (entry.IsSet && !options.Contains(entry.Value)) options.Insert(0, entry.Value);   // a value the list does not know (a binding, an old name)
                    if (entry.CanClear) options.Insert(0, unset);
                    var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
                    foreach (var o in options) combo.Items.Add(o);
                    combo.SelectedItem = entry.IsSet ? entry.Value : entry.CanClear ? unset : entry.Value;
                    combo.SelectionChanged += (_, _) =>
                    {
                        if (combo.SelectedItem is string s) Commit(set, entry, s == unset ? "" : s);
                    };
                    return combo;
                }
                default:
                {
                    var box = new TextBox { Text = entry.IsSet ? entry.Value : "", Watermark = entry.IsSet || entry.Value == "" ? null : entry.Value };
                    if (entry.Unit is { Length: > 0 } unit)
                        box.InnerRightContent = new TextBlock { Text = unit, Foreground = EditorTheme.TextDim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0) };
                    if (entry.Kind == PropertyEditKind.Color)
                    {
                        var swatch = new Rectangle { Width = 12, Height = 12, Margin = new Thickness(6, 0, 0, 0), Stroke = EditorTheme.Border, StrokeThickness = 1, VerticalAlignment = VerticalAlignment.Center };
                        void Paint() => swatch.Fill = Color.TryParse(box.Text ?? "", out var c) ? new SolidColorBrush(c) : Brushes.Transparent;
                        Paint();
                        box.TextChanged += (_, _) => Paint();
                        box.InnerLeftContent = swatch;
                    }
                    box.LostFocus += (_, _) => Commit(set, entry, box.Text ?? "");
                    box.KeyDown += (_, e) =>
                    {
                        if (e.Key == Key.Enter) { Commit(set, entry, box.Text ?? ""); _host?.FocusEditor(); e.Handled = true; }
                        else if (e.Key == Key.Escape) { box.Text = entry.IsSet ? entry.Value : ""; _host?.FocusEditor(); e.Handled = true; }
                    };
                    return box;
                }
            }
        }

        private static Control Row(PropertyEntry entry, Control field)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Tag = entry.Name };
            var text = new TextBlock
            {
                Text = entry.Name, MinWidth = 90, MaxWidth = 140, Margin = new Thickness(0, 0, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = entry.IsSet ? EditorTheme.Text : EditorTheme.TextDim, VerticalAlignment = VerticalAlignment.Center,
            };
            if (entry.Tip != null) { ToolTip.SetTip(text, entry.Tip); ToolTip.SetTip(field, entry.Tip); }
            Grid.SetColumn(field, 1);
            grid.Children.Add(text);
            grid.Children.Add(field);
            return grid;
        }
    }
}
