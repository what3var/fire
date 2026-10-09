using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using fire.Device.Manager.DeviceManager;

namespace fire.Editor
{
    /// <summary>A row of the packet list.</summary>
    public sealed class PacketRow
    {
        private readonly Func<bool> _textMode;
        private string? _hex, _text;

        public PacketRow(int number, PacketRecord record, Func<bool> textMode)
        {
            Number = number;
            Record = record;
            _textMode = textMode;
        }

        public int Number { get; }
        public PacketRecord Record { get; }

        public string TimeText => Record.Time.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        public bool IsOutgoing => Record.Direction == PacketDirection.HostToDevice;
        public string DirectionText => IsOutgoing ? "Host → Device" : "Device → Host";
        public string DeviceId => Record.DeviceIdentifier;
        public int Length => Record.Data.Length;

        /// <summary>The content in the currently chosen representation (hex or text).</summary>
        public string Content => _textMode() ? (_text ??= ToText(Record.Data)) : (_hex ??= PacketLog.ToHex(Record.Data));

        /// <summary>The bytes as UTF-8 text; control characters visible (\r, \n, \t, otherwise \xNN), invalid sequences as U+FFFD.</summary>
        public static string ToText(byte[] data)
        {
            var sb = new StringBuilder();
            foreach (char c in Encoding.UTF8.GetString(data))
            {
                switch (c)
                {
                    case '\r': sb.Append("\\r"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    case < ' ':
                    case '\u007F':
                        sb.Append("\\x").Append(((int)c).ToString("X2", CultureInfo.InvariantCulture));
                        break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }
    }

    /// <summary>The packet tracking of a device (like Wireshark): shows all sent and received packets with
    /// direction (host→device / device→host), device identifier and content - optionally as hex bytes or as Unicode text.
    ///
    /// Two operating modes, both as a document tab: LIVE (<see cref="Attach"/>: records the traffic of the device as long as
    /// the tab is open and the recording runs) or a LOADED file (<see cref="ResetTo"/>, format see
    /// PacketLog). As an <see cref="IDocumentView"/>, saving/opening goes through the same paths as for scripts.</summary>
    public partial class PacketTraceControl : UserControl, IDocumentView
    {
        private readonly ObservableCollection<PacketRow> _rows = new();
        private readonly ConcurrentQueue<PacketRecord> _incoming = new();
        private int _flushPending;
        private DeviceManager? _manager;
        private volatile bool _recording = true;
        private bool _loading;

        public string? FilePath { get; set; }
        public bool IsReadOnly => false;
        public bool IsModified { get; private set; }
        public event Action? ModifiedChanged;
        public event Action<int>? CaretLineChanged;

        /// <summary>The tracked device for a live tracking, otherwise null (loaded file).</summary>
        public string? DeviceIdentifier { get; private set; }

        public PacketTraceControl()
        {
            InitializeComponent();
            PacketList.ItemsSource = _rows;
            UpdateInfo();
        }

        private bool TextMode => TextRadio.IsChecked == true;

        // -----------------------------------------------------------
        // Live-Verfolgung
        // -----------------------------------------------------------

        /// <summary>Starts recording the traffic of the device `deviceIdentifier` via `manager`.</summary>
        public void Attach(DeviceManager manager, string deviceIdentifier)
        {
            Detach();
            _manager = manager;
            DeviceIdentifier = deviceIdentifier;
            manager.PacketCaptured += OnPacket;
            RecordToggle.IsEnabled = true;
            UpdateInfo();
        }

        /// <summary>Ends the live tracking (when the tab is closed); the packets already captured stay.</summary>
        public void Detach()
        {
            if (_manager != null) _manager.PacketCaptured -= OnPacket;
            _manager = null;
            UpdateInfo();
        }

        private void OnPacket(PacketRecord packet)
        {
            // Runs on the thread that sends or on the device's - only enqueue, the display fetches them in bundles.
            if (!_recording || packet.DeviceIdentifier != DeviceIdentifier) return;
            _incoming.Enqueue(packet);
            if (Interlocked.Exchange(ref _flushPending, 1) == 0)
                Dispatcher.UIThread.Post(Flush, DispatcherPriority.Background);
        }

        private void Flush()
        {
            Interlocked.Exchange(ref _flushPending, 0);
            bool any = false;
            while (_incoming.TryDequeue(out var packet))
            {
                _rows.Add(new PacketRow(_rows.Count + 1, packet, () => TextMode));
                any = true;
            }
            if (!any) return;

            SetModified(true);
            UpdateInfo();
            if (AutoScrollBox.IsChecked == true && _rows.Count > 0)
                PacketList.ScrollIntoView(_rows[^1], null);
        }

        private void RecordToggle_Click(object? sender, RoutedEventArgs e) => _recording = RecordToggle.IsChecked == true;

        private void Clear_Click(object? sender, RoutedEventArgs e)
        {
            while (_incoming.TryDequeue(out _)) { }
            if (_rows.Count == 0) return;
            _rows.Clear();
            SetModified(DeviceIdentifier == null || FilePath != null); // a cleared live tracking has nothing unsaved any more
            UpdateInfo();
        }

        private void ViewMode_Changed(object? sender, RoutedEventArgs e)
        {
            // When the template is loaded, Checked already fires before the list exists.
            if (PacketList == null) return;
            PacketList.ItemsSource = null;   // the rows ask for their content again
            PacketList.ItemsSource = _rows;
        }

        /// <summary>The rows are tinted by direction: green for what comes from the device, blue for what the host sends.</summary>
        private static readonly IBrush IncomingBrush = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.FromArgb(0x38, 0x4C, 0x9A, 0x4C));
        private static readonly IBrush OutgoingBrush = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.FromArgb(0x38, 0x3C, 0x78, 0xD8));

        private void PacketList_LoadingRow(object? sender, DataGridRowEventArgs e) =>
            e.Row.Background = e.Row.DataContext is PacketRow { IsOutgoing: true } ? OutgoingBrush : IncomingBrush;

        private void UpdateInfo()
        {
            string target = DeviceIdentifier ?? (FilePath != null ? System.IO.Path.GetFileName(FilePath) : "Log");
            string live = _manager != null ? "" : " (not recording)";
            InfoText.Text = $"{target}{(DeviceIdentifier != null ? live : "")} – {_rows.Count} packet{(_rows.Count == 1 ? "" : "s")}";
        }

        private void SetModified(bool value)
        {
            if (IsModified == value) return;
            IsModified = value;
            ModifiedChanged?.Invoke();
        }

        private void PacketList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (PacketList.SelectedItem is PacketRow row) CaretLineChanged?.Invoke(row.Number);
        }

        // -----------------------------------------------------------
        // IDocumentView: Speichern/Laden
        // -----------------------------------------------------------

        public string GetText() => PacketLog.Serialize(_rows.Select(r => r.Record));

        /// <summary>Loads a log (see PacketLog) and shows it; for a faulty file the message appears
        /// above the (then empty) list. Ends a live tracking of this tab.</summary>
        public void ResetTo(string text, string? filePath)
        {
            Detach();
            DeviceIdentifier = null;
            RecordToggle.IsEnabled = false;
            FilePath = filePath;
            _rows.Clear();
            while (_incoming.TryDequeue(out _)) { }
            ErrorText.IsVisible = false;

            _loading = true;
            try
            {
                if (text.Trim().Length > 0)
                {
                    int n = 0;
                    foreach (var packet in PacketLog.Parse(text))
                        _rows.Add(new PacketRow(++n, packet, () => TextMode));
                }
            }
            catch (FormatException ex)
            {
                _rows.Clear();
                ErrorText.Text = "The log could not be read: " + ex.Message;
                ErrorText.IsVisible = true;
            }
            finally { _loading = false; }

            UpdateInfo();
            SetModified(false);
        }

        public void MarkSaved()
        {
            SetModified(false);
            UpdateInfo();
        }

        public int GetCaretLine() => PacketList.SelectedItem is PacketRow row ? row.Number : 1;
        public int LineCount => _rows.Count;
        public void FocusEditor() => PacketList.Focus();

        public void GoToLine(int line)
        {
            if (_rows.Count == 0) return;
            var row = _rows[Math.Max(1, Math.Min(line, _rows.Count)) - 1];
            PacketList.SelectedItem = row;
            PacketList.ScrollIntoView(row, null);
            PacketList.Focus();
        }

        // -----------------------------------------------------------
        // Edit menu: only Copy and Select all make sense for a packet list.
        // -----------------------------------------------------------

        public bool CanUndo => false;
        public bool CanRedo => false;
        public bool HasSelection => PacketList.SelectedItems.Count > 0;
        public void Undo() { }
        public void Redo() { }
        public void Cut() { }
        public void Paste() { }
        public void Delete() { }
        public void Find() => PacketList.Focus();
        public void FindNext() { }
        public void FindPrevious() { }

        /// <summary>Copies the chosen packets in the log format (time, direction, device, hex bytes) to the clipboard.</summary>
        public void Copy()
        {
            var selected = PacketList.SelectedItems.OfType<PacketRow>().OrderBy(r => r.Number).Select(r => r.Record).ToList();
            if (selected.Count == 0) return;
            _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(PacketLog.Serialize(selected));
        }

        public void SelectAll()
        {
            PacketList.SelectedItems.Clear();
            foreach (var row in _rows) PacketList.SelectedItems.Add(row);
            PacketList.Focus();
        }
    }
}
