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
    /// The dialog "Circuit" of the circuit editor: the name of the class generated from the file and its namespace, the time step of the simulation, the ports of
    /// the circuit (its Pin elements) and the parts bound to the devices of a program (a GPIO line, an I2C address, SPI). Every change goes to the schematic at
    /// once (with undo); the lists follow what the schematic reports back.
    /// </summary>
    internal sealed class CircuitDialog : Window
    {
        private readonly Action<string, string> _setCircuit;
        private readonly Action<string, string, string> _setElement;
        private readonly TextBox _class = new() { Watermark = "the name of the file" };
        private readonly TextBox _namespace = new() { Watermark = "none" };
        private readonly TextBox _timeStep = new();
        private readonly Grid _ports = new() { ColumnDefinitions = new ColumnDefinitions("110,*,90,*") };
        private readonly Grid _devices = new() { ColumnDefinitions = new ColumnDefinitions("110,*,90,*") };
        private readonly TextBlock _noPorts = new() { Text = "No ports: place a Pin (Display) and give it a port name.", Foreground = EditorTheme.TextDim, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _noDevices = new() { Text = "No parts bound to devices (GpioLine, I2cAdc, I2cDac, SpiShiftOut, SpiShiftIn).", Foreground = EditorTheme.TextDim, TextWrapping = TextWrapping.Wrap };
        private string _shownPorts = "";

        public CircuitDialog(string[] circuit, IReadOnlyList<string[]> ports, Action<string, string> setCircuit, Action<string, string, string> setElement)
        {
            _setCircuit = setCircuit;
            _setElement = setElement;
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

            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(Heading("Class and simulation"));
            content.Children.Add(general);
            content.Children.Add(Heading("Ports"));
            content.Children.Add(new TextBlock
            {
                Text = "The connections of the circuit to the outside: a schematic with ports can be used as a part of another one.",
                Foreground = EditorTheme.TextDim, TextWrapping = TextWrapping.Wrap,
            });
            content.Children.Add(_noPorts);
            content.Children.Add(_ports);
            content.Children.Add(Heading("Bound to devices"));
            content.Children.Add(new TextBlock
            {
                Text = "Parts that connect the circuit to the devices of a program (docs/CIRCUITS.md, Attached): which line or address they use.",
                Foreground = EditorTheme.TextDim, TextWrapping = TextWrapping.Wrap,
            });
            content.Children.Add(_noDevices);
            content.Children.Add(_devices);

            var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true, IsCancel = true, Margin = new Thickness(0, 10, 0, 0) };
            close.Click += (_, _) => Close();
            var root = new DockPanel { Margin = new Thickness(16) };
            DockPanel.SetDock(close, Avalonia.Controls.Dock.Bottom);
            root.Children.Add(close);
            root.Children.Add(new ScrollViewer { Content = content, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
            Content = root;
            KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
            Refresh(circuit, ports);
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

        /// <summary>Shows what the schematic reports now (fields that are being edited keep their text).</summary>
        public void Refresh(string[] circuit, IReadOnlyList<string[]> ports)
        {
            if (!_class.IsKeyboardFocusWithin) _class.Text = circuit[0];
            if (!_namespace.IsKeyboardFocusWithin) _namespace.Text = circuit[1];
            if (!_timeStep.IsKeyboardFocusWithin) _timeStep.Text = circuit[2];
            // the rows are built anew only when the list changed and nothing in it is being edited
            string key = string.Join("\n", ports.Select(p => string.Join("|", p)));
            if (key == _shownPorts || _ports.IsKeyboardFocusWithin || _devices.IsKeyboardFocusWithin) return;
            _shownPorts = key;
            Fill(_ports, ports.Where(p => p[1] == "Pin").ToList(), "port");
            Fill(_devices, ports.Where(p => p[1] != "Pin").ToList(), null);
            _noPorts.IsVisible = _ports.Children.Count == 0;
            _noDevices.IsVisible = _devices.Children.Count == 0;
        }

        // a row per element: its type, its name (editable), the key that names the port or binds the device and its value (editable)
        private void Fill(Grid grid, List<string[]> rows, string? fixedKey)
        {
            grid.Children.Clear();
            grid.RowDefinitions.Clear();
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                string index = r[0], type = r[1], name = r[2], prop = r[3], value = r[4];
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var typeText = new TextBlock { Text = type, VerticalAlignment = VerticalAlignment.Center, Foreground = EditorTheme.TextDim };
                var nameBox = new TextBox { Text = name, Watermark = "name", Margin = new Thickness(0, 2, 6, 2) };
                ToolTip.SetTip(nameBox, "The name of the element: the generated class gets a field of this name");
                void CommitName() { if ((nameBox.Text ?? "") != name) _setElement(index, "name", (nameBox.Text ?? "").Trim()); }
                nameBox.LostFocus += (_, _) => CommitName();
                nameBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitName(); e.Handled = true; } };
                Grid.SetRow(typeText, i);
                Grid.SetRow(nameBox, i);
                Grid.SetColumn(nameBox, 1);
                grid.Children.Add(typeText);
                grid.Children.Add(nameBox);
                if (prop != "")
                {
                    var propText = new TextBlock { Text = fixedKey ?? prop, VerticalAlignment = VerticalAlignment.Center, Foreground = EditorTheme.TextDim, Margin = new Thickness(6, 0) };
                    var valueBox = new TextBox { Text = value, Margin = new Thickness(0, 2) };
                    ToolTip.SetTip(valueBox, prop switch
                    {
                        "port" => "The name of the port: the connection of the circuit to the outside",
                        "line" => "The GPIO line of the device the part is bound to",
                        "address" => "The I2C address of the device (0x48)",
                        _ => prop,
                    });
                    void CommitValue() { if ((valueBox.Text ?? "") != value) _setElement(index, prop, (valueBox.Text ?? "").Trim()); }
                    valueBox.LostFocus += (_, _) => CommitValue();
                    valueBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitValue(); e.Handled = true; } };
                    Grid.SetRow(propText, i);
                    Grid.SetColumn(propText, 2);
                    Grid.SetRow(valueBox, i);
                    Grid.SetColumn(valueBox, 3);
                    grid.Children.Add(propText);
                    grid.Children.Add(valueBox);
                }
            }
        }
    }
}
