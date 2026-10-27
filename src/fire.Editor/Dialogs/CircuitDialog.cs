using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace fire.Editor
{
    /// <summary>
    /// The dialog "Circuit" of the circuit editor: the name of the class generated from the file and its namespace, the time step of the simulation and the
    /// inputs and outputs of the circuit - ports bound to pins of its elements ("R1.2"), each in a color that marks the pin in the drawing and the member in the
    /// source. Every change goes to the schematic at once (with undo); the lists follow what the schematic reports back.
    /// </summary>
    internal sealed class CircuitDialog : Window
    {
        /// <summary>The colors offered for a port (the schematic gives a new one the first that is free).</summary>
        public static readonly string[] Palette = { "#E03030", "#9040E0", "#E08020", "#3070E0", "#20A050", "#D030A0", "#B09010", "#10A0B0" };

        private readonly Action<string, string> _setCircuit;
        private readonly Action<int, string, string> _setPort;
        private readonly Action<string> _addPort;
        private readonly Action<int> _removePort;
        private readonly TextBox _class = new() { Watermark = "the name of the file" };
        private readonly TextBox _namespace = new() { Watermark = "none" };
        private readonly TextBox _timeStep = new();
        private readonly Grid _ports = new() { ColumnDefinitions = new ColumnDefinitions("30,*,100,*,30"), ColumnSpacing = 6 };
        private readonly TextBlock _noPorts = new()
        {
            Text = "No inputs or outputs yet: add one here, or right-click a pin in the drawing.",
            Foreground = EditorTheme.TextDim, TextWrapping = TextWrapping.Wrap,
        };
        private string _shown = "\u0001";
        private IReadOnlyList<string> _pins = Array.Empty<string>();

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

            var general = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), RowSpacing = 6 };
            AddField(general, 0, "Class", _class, "The class generated from the file (new MyCircuit()); empty: the name of the file", () => _setCircuit("class", _class.Text ?? ""));
            AddField(general, 1, "Namespace", _namespace, "The namespace of the generated class; empty: none", () => _setCircuit("namespace", _namespace.Text ?? ""));
            AddField(general, 2, "Time step", _timeStep, "The step of the simulation, like 10us: smaller is more exact and slower", () => _setCircuit("timeStep", _timeStep.Text ?? ""));

            var addInput = new Button { Content = "Add input" };
            addInput.Click += (_, _) => _addPort("input");
            var addOutput = new Button { Content = "Add output" };
            addOutput.Click += (_, _) => _addPort("output");

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
            content.Children.Add(_noPorts);
            content.Children.Add(_ports);
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

        private static TextBlock Heading(string text) => new() { Text = text, FontSize = 15, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) };

        private static void AddField(Grid grid, int row, string label, TextBox box, string tip, Action commit)
        {
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(box, tip);
            box.LostFocus += (_, _) => commit();
            box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { commit(); e.Handled = true; } };
            Grid.SetRow(text, row);
            Grid.SetRow(box, row);
            Grid.SetColumn(box, 1);
            grid.Children.Add(text);
            grid.Children.Add(box);
        }

        /// <summary>The brush of "#RRGGBB" (gray if the text is no color).</summary>
        public static IBrush BrushOf(string color) => Color.TryParse(color, out var c) ? new SolidColorBrush(c) : Brushes.Gray;

        /// <summary>Shows what the schematic reports now (fields that are being edited keep their text).</summary>
        public void Refresh(string[] circuit, IReadOnlyList<string[]> ports, IReadOnlyList<string> pins)
        {
            if (!_class.IsKeyboardFocusWithin) _class.Text = circuit[0];
            if (!_namespace.IsKeyboardFocusWithin) _namespace.Text = circuit[1];
            if (!_timeStep.IsKeyboardFocusWithin) _timeStep.Text = circuit[2];
            _pins = pins.ToList();
            // the rows are built anew only when the list changed and nothing in it is being edited
            string key = string.Join("\n", ports.Select(p => string.Join("|", p))) + "\n" + string.Join(",", pins);
            if (key == _shown || _ports.IsKeyboardFocusWithin) return;
            _shown = key;
            Fill(ports);
            _noPorts.IsVisible = ports.Count == 0;
        }

        // a row per port: its color, its name, input/output, the pin (with the pins of the schematic to choose from), a button to remove it; what is wrong below
        private void Fill(IReadOnlyList<string[]> ports)
        {
            _ports.Children.Clear();
            _ports.RowDefinitions.Clear();
            int row = 0;
            foreach (var p in ports)
            {
                int index = int.Parse(p[0]);
                string name = p[1], direction = p[2], pin = p[3], color = p[4], error = p[5];
                _ports.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

                var swatch = new Button
                {
                    Width = 24, Height = 24, Padding = new Thickness(0), Background = BrushOf(color), VerticalAlignment = VerticalAlignment.Center,
                    BorderBrush = EditorTheme.TextDim, BorderThickness = new Thickness(1),
                };
                ToolTip.SetTip(swatch, $"The color of {name} ({color}): click to choose another one");
                swatch.Flyout = ColorFlyout(index, color);
                Place(swatch, row, 0);

                var nameBox = new TextBox { Text = name, Watermark = "name", Margin = new Thickness(0, 2), Foreground = BrushOf(color) };
                ToolTip.SetTip(nameBox, "The name of the port: the generated class gets a member of this name");
                void CommitName() { if ((nameBox.Text ?? "") != name) _setPort(index, "name", (nameBox.Text ?? "").Trim()); }
                nameBox.LostFocus += (_, _) => CommitName();
                nameBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitName(); e.Handled = true; } };
                Place(nameBox, row, 1);

                var dir = new ComboBox { ItemsSource = new[] { "input", "output" }, SelectedItem = direction, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
                dir.SelectionChanged += (_, _) => { if (dir.SelectedItem is string d && d != direction) _setPort(index, "direction", d); };
                Place(dir, row, 2);

                var pinBox = new AutoCompleteBox
                {
                    Text = pin, ItemsSource = _pins, Watermark = "R1.2", FilterMode = AutoCompleteFilterMode.StartsWithOrdinal, MinimumPrefixLength = 0,
                    Margin = new Thickness(0, 2), VerticalAlignment = VerticalAlignment.Center,
                };
                ToolTip.SetTip(pinBox, "The pin the port is bound to: the element and the number of its pin (R1.2), or the name of a port of its part (Q1.B)");
                void CommitPin() { if ((pinBox.Text ?? "") != pin) _setPort(index, "pin", (pinBox.Text ?? "").Trim()); }
                pinBox.LostFocus += (_, _) => CommitPin();
                pinBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitPin(); e.Handled = true; } };
                pinBox.DropDownClosed += (_, _) => CommitPin();
                Place(pinBox, row, 3);

                var remove = new Button { Content = "✕", Width = 26, Height = 26, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                ToolTip.SetTip(remove, $"Remove {name}");
                remove.Click += (_, _) => _removePort(index);
                Place(remove, row, 4);
                row++;

                if (error != "")
                {
                    _ports.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                    var text = new TextBlock { Text = error, Foreground = new SolidColorBrush(Color.FromRgb(230, 110, 90)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
                    Grid.SetColumnSpan(text, 4);
                    Place(text, row, 1);
                    row++;
                }
            }
        }

        private void Place(Control c, int row, int column)
        {
            Grid.SetRow(c, row);
            Grid.SetColumn(c, column);
            _ports.Children.Add(c);
        }

        // the colors of the palette and a field for any other "#RRGGBB"
        private Flyout ColorFlyout(int index, string current)
        {
            var panel = new StackPanel { Spacing = 6 };
            var swatches = new WrapPanel { MaxWidth = 4 * 30 };
            var flyout = new Flyout { Content = panel };
            foreach (var c in Palette)
            {
                var b = new Button
                {
                    Width = 26, Height = 26, Margin = new Thickness(2), Background = BrushOf(c), Padding = new Thickness(0),
                    BorderBrush = string.Equals(c, current, StringComparison.OrdinalIgnoreCase) ? Brushes.White : Brushes.Transparent, BorderThickness = new Thickness(2),
                };
                ToolTip.SetTip(b, c);
                b.Click += (_, _) => { flyout.Hide(); if (!string.Equals(c, current, StringComparison.OrdinalIgnoreCase)) _setPort(index, "color", c); };
                swatches.Children.Add(b);
            }
            var hex = new TextBox { Text = current, Watermark = "#RRGGBB", Width = 120 };
            hex.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                var t = (hex.Text ?? "").Trim();
                if (!t.StartsWith('#')) t = "#" + t;
                if (t.Length == 7 && Color.TryParse(t, out Color _)) { flyout.Hide(); _setPort(index, "color", t.ToUpperInvariant()); }
                e.Handled = true;
            };
            panel.Children.Add(swatches);
            panel.Children.Add(hex);
            return flyout;
        }
    }
}
