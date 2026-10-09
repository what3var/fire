using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Avalonia.VisualTree;
using fire.Device.Manager.DeviceManager;
using fire.Device.Manager.Drivers;

namespace fire.Editor
{
    /// <summary>The device overview: a tree of the devices found (one node per driver), with a status icon on the device
    /// (connected / available / unchecked / unavailable) and a star on the default device. Above it a toolbar
    /// for searching, connecting/disconnecting and opening the packet tracking; the same actions in the context menu.
    ///
    /// Works on the shared DeviceManager of the editor (see EditorDeviceService); events of the manager arrive
    /// on arbitrary threads and are brought here, bundled, to the UI thread. Connecting/searching run on a
    /// background thread (opening a port can take a while).</summary>
    public partial class DevicesPanelControl : UserControl
    {
        private static readonly IBrush ConnectedBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10));
        private static readonly IBrush AvailableBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x4C, 0x9A, 0x4C));
        private static readonly IBrush UncheckedBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        private static readonly IBrush UnavailableBrush = new ImmutableSolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C));
        private static readonly IBrush StarBrush = new ImmutableSolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x00));

        private EditorDeviceService? _service;
        private int _refreshPending;
        private readonly HashSet<string> _collapsedDrivers = new();
        private bool _searching;

        /// <summary>The user wants to open the packet tracking of the device with this identifier.</summary>
        public event Action<string>? OpenTraceRequested;

        /// <summary>A message for the host's status bar.</summary>
        public event Action<string>? StatusMessage;

        public DevicesPanelControl()
        {
            InitializeComponent();
            DeviceTree.SelectionChanged += (_, _) => UpdateButtons();
            DeviceTree.DoubleTapped += DeviceTree_DoubleTapped;
            DeviceTree.AddHandler(PointerPressedEvent, DeviceTree_PointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            DeviceTree.ContextMenu = EditorCommands.BuildMenu(new List<EditorCommands.Entry?>
            {
                new() { Header = "_Connect", Execute = () => _ = ConnectSelectedAsync(), Enabled = () => SelectedSlot is { Device.IsConnected: false } },
                new() { Header = "_Disconnect", Execute = () => _ = DisconnectSelectedAsync(), Enabled = () => SelectedSlot is { Device.IsConnected: true } },
                new() { Header = "Check _Availability", Execute = () => _ = TestSelectedAsync(), Enabled = () => SelectedSlot != null },
                null,
                new() { Header = "Set as Default _Device", Execute = SetSelectedAsDefault, Enabled = () => SelectedSlot != null && SelectedSlot.Identifier != _service?.Manager.DefaultIdentifier },
                new() { Header = "Clear Default D_evice", Execute = ClearDefault, Enabled = () => _service?.Manager.DefaultIdentifier != null },
                null,
                new() { Header = "Open Packet _Trace", Execute = OpenTraceForSelected, Enabled = () => SelectedSlot != null },
            });
        }

        /// <summary>Connects the panel to the editor's shared manager.</summary>
        public void Attach(EditorDeviceService service)
        {
            _service = service;
            service.Manager.DevicesChanged += ScheduleRefresh;
            service.Manager.DeviceStateChanged += _ => ScheduleRefresh();
            service.Manager.DefaultChanged += ScheduleRefresh;
            Refresh();
        }

        /// <summary>The device chosen in the tree, null if none (or a driver node) is chosen.</summary>
        public DeviceSlot? SelectedSlot =>
            DeviceTree.SelectedItem is TreeViewItem { Tag: string identifier } ? _service?.Manager.GetSlotByIdentifier(identifier) : null;

        // -----------------------------------------------------------
        // Anzeige
        // -----------------------------------------------------------

        private void ScheduleRefresh()
        {
            if (Interlocked.Exchange(ref _refreshPending, 1) == 1) return;
            Dispatcher.UIThread.Post(() =>
            {
                Interlocked.Exchange(ref _refreshPending, 0);
                Refresh();
            }, DispatcherPriority.Background);
        }

        /// <summary>Rebuilds the tree (selection and expanded drivers are kept).</summary>
        public void Refresh()
        {
            if (_service == null) return;
            string? selected = (DeviceTree.SelectedItem as TreeViewItem)?.Tag as string;
            string? defaultId = _service.Manager.DefaultIdentifier;

            DeviceTree.Items.Clear();
            foreach (var group in _service.Manager.GetSlots().GroupBy(s => s.DriverIdentifier))
            {
                var driverNode = new TreeViewItem
                {
                    Header = new TextBlock { Text = DriverTitle(group.Key), FontWeight = FontWeight.SemiBold },
                    IsExpanded = !_collapsedDrivers.Contains(group.Key),
                };
                string driver = group.Key;
                driverNode.Expanded += (_, _) => _collapsedDrivers.Remove(driver);
                driverNode.Collapsed += (_, _) => _collapsedDrivers.Add(driver);

                foreach (var slot in group)
                {
                    var node = new TreeViewItem { Header = MakeHeader(slot, slot.Identifier == defaultId), Tag = slot.Identifier };
                    driverNode.Items.Add(node);
                    if (slot.Identifier == selected) node.IsSelected = true;
                }
                DeviceTree.Items.Add(driverNode);
            }

            if (DeviceTree.Items.Count == 0)
                DeviceTree.Items.Add(new TreeViewItem { Header = new TextBlock { Text = "(no devices - click \"Search\")", Foreground = UncheckedBrush } });

            UpdateButtons();
        }

        private static string DriverTitle(string driver) => driver switch
        {
            "serial" => "Serial (serial)",
            "loopback" => "Simulation (loopback)",
            _ => driver,
        };

        private static StackPanel MakeHeader(DeviceSlot slot, bool isDefault)
        {
            var (icon, state) = StateOf(slot);

            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            ToolTip.SetTip(panel, $"{slot.Identifier} - {state}");
            panel.Children.Add(new Image
            {
                Source = Icons.Load(icon),
                Width = 18,
                Height = 18,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });
            panel.Children.Add(new TextBlock
            {
                Text = slot.Device.PortName ?? slot.Identifier,
                FontWeight = isDefault ? FontWeight.Bold : FontWeight.Normal,
                VerticalAlignment = VerticalAlignment.Center,
            });
            panel.Children.Add(new TextBlock
            {
                Text = state,
                Foreground = UncheckedBrush,
                FontSize = 11,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            if (isDefault)
            {
                var star = new TextBlock
                {
                    Text = "★",
                    Foreground = StarBrush,
                    Margin = new Thickness(6, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                ToolTip.SetTip(star, "Default device (Device.Default in scripts)");
                panel.Children.Add(star);
            }
            return panel;
        }

        private static (string Icon, string Text) StateOf(DeviceSlot slot)
        {
            if (slot.Device.IsConnected) return ("Material-ToyBrickOnline", "connected");
            return slot.Device.Availability switch
            {
                DeviceAvailability.Available => ("Material-ToyBrickOffline", "available"),
                DeviceAvailability.Unavailable => ("Material-ToyBrickRemoveOutline", "unavailable"),
                _ => ("Material-ToyBrickOutline", "unchecked"),
            };
        }

        private void UpdateButtons()
        {
            var slot = SelectedSlot;
            ConnectButton.IsEnabled = slot is { Device.IsConnected: false };
            DisconnectButton.IsEnabled = slot is { Device.IsConnected: true };
            TraceButton.IsEnabled = slot != null;
            SearchButton.IsEnabled = !_searching;
        }

        private void DeviceTree_DoubleTapped(object? sender, TappedEventArgs e)
        {
            // Double click on a device: packet tracking (the double click on a driver only expands/collapses it).
            if (SelectedSlot != null && (e.Source as Visual)?.FindAncestorOfType<TreeViewItem>(true) is { Tag: string })
                OpenTraceForSelected();
        }

        private void DeviceTree_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            // A right click selects the element beneath it (TreeView does not do that by itself).
            if (e.GetCurrentPoint(DeviceTree).Properties.IsRightButtonPressed && (e.Source as Visual)?.FindAncestorOfType<TreeViewItem>(true) is { } item)
            {
                item.IsSelected = true;
                item.Focus();
            }
        }

        // -----------------------------------------------------------
        // Actions (also called from the host's menu)
        // -----------------------------------------------------------

        private void Search_Click(object? sender, RoutedEventArgs e) => _ = SearchAsync();
        private void Connect_Click(object? sender, RoutedEventArgs e) => _ = ConnectSelectedAsync();
        private void Disconnect_Click(object? sender, RoutedEventArgs e) => _ = DisconnectSelectedAsync();
        private void Trace_Click(object? sender, RoutedEventArgs e) => OpenTraceForSelected();

        /// <summary>Searches for devices and checks their availability (background thread).</summary>
        public async Task SearchAsync()
        {
            if (_service == null || _searching) return;
            _searching = true;
            UpdateButtons();
            StatusMessage?.Invoke("Searching for devices...");
            try
            {
                await _service.RefreshAsync(fastScan: false);
                StatusMessage?.Invoke($"{_service.Manager.DeviceCount} device(s) found.");
            }
            catch (Exception ex)
            {
                StatusMessage?.Invoke($"Device search failed: {ex.Message}");
            }
            finally
            {
                _searching = false;
                Refresh();
            }
        }

        public async Task ConnectSelectedAsync()
        {
            if (SelectedSlot is not { } slot) return;
            StatusMessage?.Invoke($"Connecting {slot.Identifier}...");
            try
            {
                await Task.Run(() => slot.Device.Connect());
                StatusMessage?.Invoke($"Connected: {slot.Identifier}");
            }
            catch (Exception ex)
            {
                StatusMessage?.Invoke($"Connecting to {slot.Identifier} failed: {ex.Message}");
            }
            Refresh();
        }

        public async Task DisconnectSelectedAsync()
        {
            if (SelectedSlot is not { } slot) return;
            try
            {
                await Task.Run(() => slot.Device.Disconnect());
                StatusMessage?.Invoke($"Disconnected: {slot.Identifier}");
            }
            catch (Exception ex)
            {
                StatusMessage?.Invoke($"Disconnecting {slot.Identifier} failed: {ex.Message}");
            }
            Refresh();
        }

        public async Task TestSelectedAsync()
        {
            if (SelectedSlot is not { } slot) return;
            try
            {
                var result = await Task.Run(() => slot.Device.TestAvailability());
                StatusMessage?.Invoke($"{slot.Identifier}: {(result == DeviceAvailability.Available ? "available" : "unavailable")}");
            }
            catch (Exception ex)
            {
                StatusMessage?.Invoke($"Checking {slot.Identifier} failed: {ex.Message}");
            }
            Refresh();
        }

        public void SetSelectedAsDefault()
        {
            if (_service == null || SelectedSlot is not { } slot) return;
            _service.Manager.DefaultIdentifier = slot.Identifier;
            StatusMessage?.Invoke($"Default device: {slot.Identifier}");
        }

        public void ClearDefault()
        {
            if (_service == null) return;
            _service.Manager.DefaultIdentifier = null;
            StatusMessage?.Invoke("No default device.");
        }

        public void OpenTraceForSelected()
        {
            if (SelectedSlot is { } slot) OpenTraceRequested?.Invoke(slot.Identifier);
        }
    }
}
