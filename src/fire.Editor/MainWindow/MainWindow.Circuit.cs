using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Dock.Model.Controls;
using Dock.Model.Core;
using fire.UI.Markup;
using DockTool = Dock.Model.Avalonia.Controls.Tool;

namespace fire.Editor
{
    /// <summary>The circuit editor in the main window: the tool strips "Edit" (for every document) and "Simulation" (on while a circuit is the active document),
    /// and the tool window "Toolbox" with the parts of the active circuit - shown while a circuit is active, hidden otherwise.</summary>
    public partial class MainWindow
    {
        // the content of the tool window "Toolbox": the palette of the active circuit (each circuit document has its own)
        private readonly ContentControl _toolboxPanel = new()
        {
            Content = new TextBlock { Text = "The parts of a circuit appear here while a circuit (.biscuit) is the active document.", TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Avalonia.Thickness(8), Foreground = EditorTheme.TextDim },
        };
        private CircuitEditorControl? _shownCircuit;
        private readonly HashSet<CircuitEditorControl> _hookedCircuits = new();
        private DispatcherTimer? _editStripTimer;
        private bool _fillingSteps;
        private bool _toolboxSynced;
        private IImage? _pauseIcon, _playIcon;

        private CircuitEditorControl? ActiveCircuit => ActiveDocument?.View as CircuitEditorControl;

        private void InitCircuitUi()
        {
            _fillingSteps = true;
            // the pace of real time: faster, real time, slow motion (Tag: simulated seconds per second)
            foreach (double speed in CircuitEditorControl.SpeedChoices)
                cmbSimSteps.Items.Add(new ComboBoxItem { Content = CircuitEditorControl.SpeedLabel(speed), Tag = speed });
            _fillingSteps = false;
            if (tbSimSamples.Menu is { } samples)
                foreach (var sample in CircuitSamples.All)
                {
                    var item = new MenuItem { Header = sample.Title };
                    ToolTip.SetTip(item, sample.Description);
                    item.Click += async (_, _) => { if (ActiveCircuit is { } c) await c.LoadSampleAsync(sample); };
                    samples.Items.Add(item);
                }
            _pauseIcon = LoadIcon("Circuit-Pause.png");
            _playIcon = LoadIcon("Material-Play.png");
            // the buttons follow the active document (undo, selection, ...): a light poll instead of an event per kind of document
            _editStripTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => UpdateEditStrip());
            _editStripTimer.Start();
            UpdateEditStrip();
        }

        private static IImage? LoadIcon(string name)
        {
            try { return new Bitmap(AssetLoader.Open(new Uri("avares://spark/Assets/icons/" + name))); }
            catch (Exception) { return null; }
        }

        /// <summary>The buttons of the tool strips "Edit" and "Simulation" for the active document.</summary>
        private void UpdateEditStrip()
        {
            var view = ActiveDocument?.View;
            bool editable = view is { IsReadOnly: false };
            tbUndo.IsEnabled = view?.CanUndo == true;
            tbRedo.IsEnabled = view?.CanRedo == true;
            tbCopy.IsEnabled = view?.HasSelection == true;
            tbCut.IsEnabled = tbDelete.IsEnabled = view?.HasSelection == true && editable;
            tbPaste.IsEnabled = editable;
            tbSelectAll.IsEnabled = view != null;
            tbFindReplace.IsEnabled = view != null;
            // only scripts know definitions and comments
            tbGoToDefinition.IsEnabled = tbToggleComment.IsEnabled = ActiveScript != null;

            var circuit = ActiveCircuit;
            stripSimulation.IsEnabled = circuit != null;
            if (circuit != null)
            {
                var icon = circuit.Running ? _pauseIcon : _playIcon;
                if (!ReferenceEquals(tbSimPause.IconSource, icon)) tbSimPause.IconSource = icon;
                ToolTip.SetTip(tbSimPause, circuit.Running ? "Pause the simulation (Space in the view)" : "Run the simulation (Space in the view)");
                double wanted = circuit.Speed;
                if (cmbSimSteps.SelectedItem is not ComboBoxItem { Tag: double shown } || Math.Abs(shown - wanted) > wanted * 1e-6) SelectSpeed(wanted);
            }
        }

        private void SelectSpeed(double speed)
        {
            _fillingSteps = true;
            var item = cmbSimSteps.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is double d && Math.Abs(d - speed) <= speed * 1e-6);
            if (item == null)
            {
                // a pace from the file that the list does not offer: it gets an entry of its own, in order
                item = new ComboBoxItem { Content = CircuitEditorControl.SpeedLabel(speed), Tag = speed };
                int at = cmbSimSteps.Items.OfType<ComboBoxItem>().TakeWhile(i => i.Tag is double d && d > speed).Count();
                cmbSimSteps.Items.Insert(at, item);
            }
            cmbSimSteps.SelectedItem = item;
            _fillingSteps = false;
        }

        /// <summary>The active document changed: the toolbox shows the parts of the active circuit (and is there only while one is active), the simulation strip
        /// follows it.</summary>
        private void RefreshCircuitUi()
        {
            var circuit = ActiveCircuit;
            if (circuit != null && _hookedCircuits.Add(circuit))
            {
                circuit.StatusChanged += text => { if (ReferenceEquals(ActiveCircuit, circuit) && text != "") UpdateStatus(text); };
                circuit.StateChanged += () => { if (ReferenceEquals(ActiveCircuit, circuit)) UpdateEditStrip(); };
            }
            // the toolbox follows the active document only when that changes: closed while a circuit is active, it stays closed (View > Toolbox brings it back)
            bool changed = !ReferenceEquals(_shownCircuit, circuit) || !_toolboxSynced;
            if (!ReferenceEquals(_shownCircuit, circuit))
            {
                _shownCircuit = circuit;
                if (circuit != null) _toolboxPanel.Content = circuit.Toolbox;
                if (circuit != null && circuit.StatusText != "") UpdateStatus(circuit.StatusText);
            }
            if (changed && _tools.TryGetValue("toolbox", out var tool))
            {
                _toolboxSynced = true;
                try
                {
                    if (circuit != null && !IsToolVisible(tool)) _factory.RestoreDockable(tool);
                    else if (circuit == null && IsToolVisible(tool) && (RootDock == null || !_factory.IsDockablePinned(tool, RootDock))) _factory.HideDockable(tool);
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
            }
            UpdateEditStrip();
        }

        /// <summary>A saved layout from before the toolbox: a tool dock at the left edge with it.</summary>
        private void EnsureLeftTool(IRootDock layout, string id, Control content)
        {
            if (AllTools(layout).Any(t => t.Id == id)) return;
            var tool = SparkFactory.NewTool(id, PanelTitles[id], content);
            var main = _factory.Find(layout, d => d.Id == "main").OfType<IDock>().FirstOrDefault();
            if (main?.VisibleDockables == null)
            {
                EnsureTool(layout, id, content, "solution");
                return;
            }
            var dock = _factory.NewLeftDock(tool);
            main.VisibleDockables.Insert(0, new Dock.Model.Avalonia.Controls.ProportionalDockSplitter { Id = "split-left" });
            main.VisibleDockables.Insert(0, dock);
        }

        // ---- the tool strip "Simulation" ----
        private void SimPause_Click(object? sender, RoutedEventArgs e) { if (ActiveCircuit is { } c) { c.Running = !c.Running; UpdateEditStrip(); } }
        private void SimReset_Click(object? sender, RoutedEventArgs e) => ActiveCircuit?.ResetSimulation();
        private void SimRotateLeft_Click(object? sender, RoutedEventArgs e) => ActiveCircuit?.RotateCounterclockwise();
        private void SimRotateRight_Click(object? sender, RoutedEventArgs e) => ActiveCircuit?.RotateClockwise();
        private void SimFlipHorizontal_Click(object? sender, RoutedEventArgs e) => ActiveCircuit?.FlipHorizontal();
        private void SimFlipVertical_Click(object? sender, RoutedEventArgs e) => ActiveCircuit?.FlipVertical();
        private void SimFit_Click(object? sender, RoutedEventArgs e) => ActiveCircuit?.FitToView();
        private async void SimImportFile_Click(object? sender, RoutedEventArgs e) { if (ActiveCircuit is { } c) await c.ImportFalstadFileAsync(); }
        private async void SimImportClipboard_Click(object? sender, RoutedEventArgs e) { if (ActiveCircuit is { } c) await c.ImportFalstadClipboardAsync(); }
        private async void SimProperties_Click(object? sender, RoutedEventArgs e) { if (ActiveCircuit is { } c) await c.ShowCircuitPropertiesAsync(); }

        // a button whose work is its menu (Import, Samples): a click anywhere on it opens the menu
        private void SimOpenMenu_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is ToolStripButton { Menu: { } menu } button) menu.ShowAt(button);
        }

        private void cmbSimSteps_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_fillingSteps || cmbSimSteps.SelectedItem is not ComboBoxItem { Tag: double speed } || ActiveCircuit is not { } c) return;
            c.Speed = speed;
            c.FocusEditor();
        }
    }
}
