using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using PropertyChanged;

// PropertyChanged.Fody weaves only the view models (Avalonia's controls carry INotifyPropertyChanged of their own)
[assembly: FilterType("fire.Editor.ViewModels.")]

namespace fire.Editor.ViewModels
{
    /// <summary>An input or output of the circuit as the dialog shows it (PropertyChanged.Fody raises the events): the schematic reports it, the row's controls are
    /// bound to it and follow every report without being built again.</summary>
    [AddINotifyPropertyChangedInterface]
    internal sealed class CircuitPortRow
    {
        public int Index { get; set; }
        public string Name { get; set; } = "";
        public string Direction { get; set; } = "input";
        public string Pin { get; set; } = "";
        public string Color { get; set; } = "";
        public string Error { get; set; } = "";

        public IBrush ColorBrush => fire.Editor.CircuitDialog.BrushOf(Color);
        public bool HasError => Error != "";
        public string ColorTip => $"The color of {Name} ({Color}): click to choose another one";
        public string RemoveTip => $"Remove {Name}";
    }

    /// <summary>What the dialog "Circuit" shows (PropertyChanged.Fody raises the events of the properties; NoPorts follows the list by hand): the class, its namespace, the time step, the inputs and outputs and the
    /// pins they can be bound to. <see cref="Update"/> takes over what the schematic reports, changing only what changed.</summary>
    internal sealed class CircuitDialogModel : INotifyPropertyChanged
    {
        public string ClassName { get; set; } = "";
        public string Namespace { get; set; } = "";
        public string TimeStep { get; set; } = "";
        public ObservableCollection<CircuitPortRow> Ports { get; } = new();
        public ObservableCollection<string> Pins { get; } = new();
        public bool NoPorts => Ports.Count == 0;

        public CircuitDialogModel() => Ports.CollectionChanged += (_, _) => OnPropertyChanged(nameof(NoPorts));

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public void Update(string[] circuit, IReadOnlyList<string[]> ports, IReadOnlyList<string> pins)
        {
            ClassName = circuit[0];
            Namespace = circuit[1];
            TimeStep = circuit[2];
            for (int i = 0; i < ports.Count; i++)
            {
                var p = ports[i];
                if (i == Ports.Count) Ports.Add(new CircuitPortRow());
                var row = Ports[i];
                row.Index = int.Parse(p[0]);
                row.Name = p[1];
                row.Direction = p[2];
                row.Pin = p[3];
                row.Color = p[4];
                row.Error = p[5];
            }
            while (Ports.Count > ports.Count) Ports.RemoveAt(Ports.Count - 1);
            if (!Pins.SequenceEqual(pins))
            {
                Pins.Clear();
                foreach (var pin in pins) Pins.Add(pin);
            }
        }
    }

}

namespace fire.Editor
{
    using fire.Editor.ViewModels;

    /// <summary>
    /// The dialog "Circuit" of the circuit editor: the name of the class generated from the file and its namespace, the time step of the simulation and the
    /// inputs and outputs of the circuit - ports bound to pins of its elements ("R1.2"), each in a color that marks the pin in the drawing and the member in the
    /// source. Every change goes to the schematic at once (with undo); the controls are bound to <see cref="CircuitDialogModel"/>, which follows what the
    /// schematic reports back.
    /// </summary>
    internal sealed class CircuitDialog : Window
    {
        /// <summary>The colors offered for a port (the schematic gives a new one the first that is free).</summary>
        public static readonly string[] Palette = { "#E03030", "#9040E0", "#E08020", "#3070E0", "#20A050", "#D030A0", "#B09010", "#10A0B0" };

        private readonly CircuitDialogModel _model = new();
        private readonly Action<string, string> _setCircuit;
        private readonly Action<int, string, string> _setPort;
        private readonly Action<string> _addPort;
        private readonly Action<int> _removePort;

        public CircuitDialog(string[] circuit, IReadOnlyList<string[]> ports, IReadOnlyList<string> pins, Action<string, string> setCircuit,
            Action<int, string, string> setPort, Action<string> addPort, Action<int> removePort)
        {
            _setCircuit = setCircuit;
            _setPort = setPort;
            _addPort = addPort;
            _removePort = removePort;
            Title = "Circuit";
            Width = 640;
            Height = 520;
            MinWidth = 480;
            MinHeight = 360;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            DataContext = _model;

            var general = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), RowSpacing = 6 };
            AddField(general, 0, "Class", nameof(CircuitDialogModel.ClassName), "the name of the file",
                "The class generated from the file (new MyCircuit()); empty: the name of the file", text => _setCircuit("class", text));
            AddField(general, 1, "Namespace", nameof(CircuitDialogModel.Namespace), "none", "The namespace of the generated class; empty: none", text => _setCircuit("namespace", text));
            AddField(general, 2, "Time step", nameof(CircuitDialogModel.TimeStep), "", "The step of the simulation, like 10us: smaller is more exact and slower",
                text => _setCircuit("timeStep", text));

            var addInput = new Button { Content = "Add input" };
            addInput.Click += (_, _) => _addPort("input");
            var addOutput = new Button { Content = "Add output" };
            addOutput.Click += (_, _) => _addPort("output");

            var noPorts = new TextBlock
            {
                Text = "No inputs or outputs yet: add one here, or right-click a pin in the drawing.",
                Foreground = EditorTheme.TextDim, TextWrapping = TextWrapping.Wrap,
            };
            noPorts.Bind(IsVisibleProperty, new Binding(nameof(CircuitDialogModel.NoPorts)));
            var rows = new ItemsControl { ItemTemplate = new FuncDataTemplate<CircuitPortRow>((row, _) => RowView()) };
            rows.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(CircuitDialogModel.Ports)));

            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(Heading("Class and simulation"));
            content.Children.Add(general);
            content.Children.Add(Heading("Inputs and outputs"));
            content.Children.Add(new TextBlock
            {
                Text = "The ports of the circuit, each bound to a pin of an element (R1.2: the second pin of R1). The generated class gets a member of the port's "
                     + "name; the pin in the drawing and the member in the source show the port's color.",
                Foreground = EditorTheme.TextDim, TextWrapping = TextWrapping.Wrap,
            });
            content.Children.Add(noPorts);
            content.Children.Add(rows);
            content.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { addInput, addOutput } });

            var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true, IsCancel = true, Margin = new Thickness(0, 10, 0, 0) };
            close.Click += (_, _) => Close();
            var root = new DockPanel { Margin = new Thickness(16) };
            DockPanel.SetDock(close, Avalonia.Controls.Dock.Bottom);
            root.Children.Add(close);
            root.Children.Add(new ScrollViewer { Content = content, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
            Content = root;
            KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
            Refresh(circuit, ports, pins);
        }

        /// <summary>The model the controls are bound to (for tests).</summary>
        internal CircuitDialogModel Model => _model;

        private static TextBlock Heading(string text) => new() { Text = text, FontSize = 15, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) };

        // a text box bound to a property of the model; Enter or leaving it sends the text to the schematic
        private static TextBox Committing(string property, string watermark, string tip, Func<object?, Action<string>> commit)
        {
            var box = new TextBox { Watermark = watermark };
            box.Bind(TextBox.TextProperty, new Binding(property) { Mode = BindingMode.OneWay });
            ToolTip.SetTip(box, tip);
            void Commit()
            {
                // only what the user changed: the model holds what the schematic reported
                var current = box.DataContext?.GetType().GetProperty(property)?.GetValue(box.DataContext) as string ?? "";
                var text = (box.Text ?? "").Trim();
                if (text != current) commit(box.DataContext)(text);
            }
            box.LostFocus += (_, _) => Commit();
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
            return box;
        }

        private void AddField(Grid grid, int row, string label, string property, string watermark, string tip, Action<string> commit)
        {
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            var box = Committing(property, watermark, tip, _ => commit);
            Grid.SetRow(text, row);
            Grid.SetRow(box, row);
            Grid.SetColumn(box, 1);
            grid.Children.Add(text);
            grid.Children.Add(box);
        }

        /// <summary>The brush of "#RRGGBB" (gray if the text is no color).</summary>
        public static IBrush BrushOf(string color) => Color.TryParse(color, out var c) ? new SolidColorBrush(c) : Brushes.Gray;

        /// <summary>Takes over what the schematic reports now: the bound controls change with the model (a field being edited keeps its text until it is committed).</summary>
        public void Refresh(string[] circuit, IReadOnlyList<string[]> ports, IReadOnlyList<string> pins) => _model.Update(circuit, ports, pins);

        // the controls of a port (their DataContext is its CircuitPortRow): its color, its name, input/output, the pin, a button to remove it; what is wrong below
        private Control RowView()
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("30,*,100,*,30"), RowDefinitions = new RowDefinitions("Auto,Auto"), ColumnSpacing = 6 };
            static CircuitPortRow? RowOf(object? context) => context as CircuitPortRow;

            var swatch = new Button
            {
                Width = 24, Height = 24, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center,
                BorderBrush = EditorTheme.TextDim, BorderThickness = new Thickness(1),
            };
            swatch.Bind(BackgroundProperty, new Binding(nameof(CircuitPortRow.ColorBrush)));
            swatch.Bind(ToolTip.TipProperty, new Binding(nameof(CircuitPortRow.ColorTip)));
            var flyout = new Flyout();
            flyout.Opening += (_, _) => { if (RowOf(swatch.DataContext) is { } r) flyout.Content = ColorChooser(flyout, r); };
            swatch.Flyout = flyout;
            Place(grid, swatch, 0, 0);

            var name = Committing(nameof(CircuitPortRow.Name), "name", "The name of the port: the generated class gets a member of this name",
                context => text => { if (RowOf(context) is { } r) _setPort(r.Index, "name", text); });
            name.Margin = new Thickness(0, 2);
            name.Bind(ForegroundProperty, new Binding(nameof(CircuitPortRow.ColorBrush)));
            Place(grid, name, 0, 1);

            var direction = new ComboBox { ItemsSource = new[] { "input", "output" }, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
            direction.Bind(Avalonia.Controls.Primitives.SelectingItemsControl.SelectedItemProperty, new Binding(nameof(CircuitPortRow.Direction)) { Mode = BindingMode.OneWay });
            direction.SelectionChanged += (_, _) =>
            {
                if (RowOf(direction.DataContext) is { } r && direction.SelectedItem is string d && d != r.Direction) _setPort(r.Index, "direction", d);
            };
            Place(grid, direction, 0, 2);

            var pin = new AutoCompleteBox
            {
                ItemsSource = _model.Pins, Watermark = "R1.2", FilterMode = AutoCompleteFilterMode.StartsWithOrdinal, MinimumPrefixLength = 0,
                Margin = new Thickness(0, 2), VerticalAlignment = VerticalAlignment.Center,
            };
            pin.Bind(AutoCompleteBox.TextProperty, new Binding(nameof(CircuitPortRow.Pin)) { Mode = BindingMode.OneWay });
            ToolTip.SetTip(pin, "The pin the port is bound to: the element and the number of its pin (R1.2), or the name of a port of its part (Q1.B)");
            void CommitPin()
            {
                if (RowOf(pin.DataContext) is { } r && (pin.Text ?? "").Trim() != r.Pin) _setPort(r.Index, "pin", (pin.Text ?? "").Trim());
            }
            pin.LostFocus += (_, _) => CommitPin();
            pin.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitPin(); e.Handled = true; } };
            pin.DropDownClosed += (_, _) => CommitPin();
            Place(grid, pin, 0, 3);

            var remove = new Button { Content = "✕", Width = 26, Height = 26, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            remove.Bind(ToolTip.TipProperty, new Binding(nameof(CircuitPortRow.RemoveTip)));
            remove.Click += (_, _) => { if (RowOf(remove.DataContext) is { } r) _removePort(r.Index); };
            Place(grid, remove, 0, 4);

            var error = new TextBlock { Foreground = new SolidColorBrush(Avalonia.Media.Color.FromRgb(230, 110, 90)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
            error.Bind(TextBlock.TextProperty, new Binding(nameof(CircuitPortRow.Error)));
            error.Bind(IsVisibleProperty, new Binding(nameof(CircuitPortRow.HasError)));
            Grid.SetColumnSpan(error, 3);
            Place(grid, error, 1, 1);
            return grid;
        }

        private static void Place(Grid grid, Control c, int row, int column)
        {
            Grid.SetRow(c, row);
            Grid.SetColumn(c, column);
            grid.Children.Add(c);
        }

        // the colors of the palette and a field for any other "#RRGGBB"
        private Control ColorChooser(Flyout flyout, CircuitPortRow row)
        {
            var panel = new StackPanel { Spacing = 6 };
            var swatches = new WrapPanel { MaxWidth = 4 * 30 };
            foreach (var c in Palette)
            {
                var b = new Button
                {
                    Width = 26, Height = 26, Margin = new Thickness(2), Background = BrushOf(c), Padding = new Thickness(0),
                    BorderBrush = string.Equals(c, row.Color, StringComparison.OrdinalIgnoreCase) ? Brushes.White : Brushes.Transparent, BorderThickness = new Thickness(2),
                };
                ToolTip.SetTip(b, c);
                b.Click += (_, _) => { flyout.Hide(); if (!string.Equals(c, row.Color, StringComparison.OrdinalIgnoreCase)) _setPort(row.Index, "color", c); };
                swatches.Children.Add(b);
            }
            var hex = new TextBox { Text = row.Color, Watermark = "#RRGGBB", Width = 120 };
            hex.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                var t = (hex.Text ?? "").Trim();
                if (!t.StartsWith('#')) t = "#" + t;
                if (t.Length == 7 && Avalonia.Media.Color.TryParse(t, out Avalonia.Media.Color _)) { flyout.Hide(); _setPort(row.Index, "color", t.ToUpperInvariant()); }
                e.Handled = true;
            };
            panel.Children.Add(swatches);
            panel.Children.Add(hex);
            return panel;
        }
    }
}
