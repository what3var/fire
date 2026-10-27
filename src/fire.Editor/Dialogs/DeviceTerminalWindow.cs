using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using fire.Device.Manager.DeviceManager;

namespace fire.Editor
{
    /// <summary>A terminal for one device of the device manager: it shows what the device sends and what is sent to it (also by a running program) - as text or as a
    /// hex dump (16 bytes per line, then the characters) - and sends text (with a line ending of choice) or hex bytes. Connect/Disconnect right in the window.</summary>
    internal sealed class DeviceTerminalWindow : Window
    {
        private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace");

        private readonly DeviceManager _manager;
        private readonly string _identifier;
        private readonly TerminalLog _log = new();
        private readonly object _lock = new();
        private bool _dirty;
        private readonly DispatcherTimer _timer;

        private readonly TextBox _output;
        private readonly ComboBox _display;
        private readonly CheckBox _showSent;
        private readonly CheckBox _follow;
        private readonly TextBox _input;
        private readonly ComboBox _sendMode;
        private readonly ComboBox _lineEnding;
        private readonly Button _connect;
        private readonly TextBlock _state;

        public string Identifier => _identifier;

        public DeviceTerminalWindow(DeviceManager manager, string identifier)
        {
            _manager = manager;
            _identifier = identifier;
            Title = "Terminal - " + identifier;
            Width = 760;
            Height = 520;
            MinWidth = 480;
            MinHeight = 260;

            _state = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            _connect = new Button { Content = "Connect" };
            _connect.Click += (_, _) => _ = ToggleConnectionAsync();
            _display = new ComboBox { ItemsSource = new[] { "ASCII", "Hex" }, SelectedIndex = 0, Width = 90 };
            _display.SelectionChanged += (_, _) => Render();
            _showSent = new CheckBox { Content = "Show sent", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
            _showSent.IsCheckedChanged += (_, _) => Render();
            _follow = new CheckBox { Content = "Follow", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
            var clear = new Button { Content = "Clear" };
            clear.Click += (_, _) => { lock (_lock) _log.Clear(); Render(); };

            var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(8, 6) };
            top.Children.Add(_state);
            top.Children.Add(_connect);
            top.Children.Add(new TextBlock { Text = "Display", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) });
            top.Children.Add(_display);
            top.Children.Add(_showSent);
            top.Children.Add(_follow);
            top.Children.Add(clear);

            _output = new TextBox
            {
                IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = Mono, FontSize = 13,
                BorderThickness = new Thickness(0), Margin = new Thickness(0),
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(_output, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);

            _input = new TextBox { FontFamily = Mono, Watermark = "text to send (\\r \\n \\t \\xNN work)", VerticalAlignment = VerticalAlignment.Center };
            _input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; _ = SendAsync(); } };
            _sendMode = new ComboBox { ItemsSource = new[] { "ASCII", "Hex" }, SelectedIndex = 0, Width = 90 };
            _sendMode.SelectionChanged += (_, _) =>
            {
                bool hex = _sendMode.SelectedIndex == 1;
                _lineEnding.IsEnabled = !hex;
                _input.Watermark = hex ? "bytes in hex (48 65 6C 6C 6F, 0x0D 0x0A)" : "text to send (\\r \\n \\t \\xNN work)";
            };
            _lineEnding = new ComboBox { ItemsSource = new[] { "No line ending", "LF", "CR", "CR+LF" }, SelectedIndex = 1, Width = 140 };
            var send = new Button { Content = "Send" };
            send.Click += (_, _) => _ = SendAsync();

            var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), ColumnSpacing = 6, Margin = new Thickness(8, 6) };
            bottom.Children.Add(_input);
            Grid.SetColumn(_sendMode, 1);
            Grid.SetColumn(_lineEnding, 2);
            Grid.SetColumn(send, 3);
            bottom.Children.Add(_sendMode);
            bottom.Children.Add(_lineEnding);
            bottom.Children.Add(send);

            var dock = new DockPanel();
            DockPanel.SetDock(top, Avalonia.Controls.Dock.Top);
            DockPanel.SetDock(bottom, Avalonia.Controls.Dock.Bottom);
            dock.Children.Add(top);
            dock.Children.Add(bottom);
            dock.Children.Add(new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(0x3C, 0x35, 0x44)), BorderThickness = new Thickness(0, 1), Child = _output });
            Content = dock;

            _manager.PacketCaptured += OnPacket;
            _manager.DeviceStateChanged += OnState;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            _timer.Tick += (_, _) => { if (_dirty) Render(); };
            _timer.Start();
            Closed += (_, _) =>
            {
                _timer.Stop();
                _manager.PacketCaptured -= OnPacket;
                _manager.DeviceStateChanged -= OnState;
            };
            Opened += (_, _) => { UpdateState(); _input.Focus(); };
            KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        }

        private DeviceSlot? Slot => _manager.GetSlotByIdentifier(_identifier);

        private void OnPacket(PacketRecord p)
        {
            if (p.DeviceIdentifier != _identifier) return;
            lock (_lock)
            {
                _log.Add(p.Direction, p.Data, p.Time.ToLocalTime());
                _dirty = true;
            }
        }

        private void OnState(DeviceSlot slot)
        {
            if (slot.Identifier == _identifier) Dispatcher.UIThread.Post(UpdateState);
        }

        private void UpdateState()
        {
            var slot = Slot;
            bool connected = slot?.Device.IsConnected == true;
            _state.Text = slot == null ? "not found" : connected ? "connected" : "not connected";
            _state.Foreground = connected ? new SolidColorBrush(Color.FromRgb(0x4C, 0xC0, 0x4C)) : new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
            _connect.Content = connected ? "Disconnect" : "Connect";
            _connect.IsEnabled = slot != null;
        }

        private void Render()
        {
            string text;
            lock (_lock)
            {
                _dirty = false;
                text = _log.Render(_display.SelectedIndex == 1, _showSent.IsChecked == true);
            }
            _output.Text = text;
            if (_follow.IsChecked == true) _output.CaretIndex = text.Length;
        }

        private async Task ToggleConnectionAsync()
        {
            var slot = Slot;
            if (slot == null) return;
            try
            {
                bool connected = slot.Device.IsConnected;
                await Task.Run(() => { if (connected) slot.Device.Disconnect(); else slot.Device.Connect(); });
            }
            catch (Exception ex) { Note("! " + ex.Message); }
            UpdateState();
        }

        private async Task SendAsync()
        {
            var slot = Slot;
            if (slot == null) return;
            string text = _input.Text ?? "";
            byte[] data;
            if (_sendMode.SelectedIndex == 1)
            {
                if (!TerminalLog.TryParseHex(text, out data, out string error)) { Note("! " + error); return; }
            }
            else
            {
                string ending = _lineEnding.SelectedIndex switch { 1 => "\n", 2 => "\r", 3 => "\r\n", _ => "" };
                data = TerminalLog.TextToBytes(text, ending);
            }
            if (data.Length == 0) return;
            try
            {
                if (!slot.Device.IsConnected) await Task.Run(() => slot.Device.Connect());
                UpdateState();
                bool ok = await Task.Run(() => slot.Device.Write(data));
                if (!ok) { Note("! not sent: the device is not connected"); return; }
                _input.Text = "";
            }
            catch (Exception ex) { Note("! " + ex.Message); }
        }

        // a line of the terminal itself (an error) - not part of the traffic
        private void Note(string text)
        {
            _output.Text += text + "\n";
            if (_follow.IsChecked == true) _output.CaretIndex = _output.Text.Length;
        }
    }
}
