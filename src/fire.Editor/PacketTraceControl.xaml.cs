using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using fire.Device.Manager.DeviceManager;

namespace fire.Editor
{
    /// <summary>Eine Zeile der Paketliste.</summary>
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
        public string DirectionText => IsOutgoing ? "Host → Gerät" : "Gerät → Host";
        public string DeviceId => Record.DeviceIdentifier;
        public int Length => Record.Data.Length;

        /// <summary>Der Inhalt in der gerade gewählten Darstellung (Hex oder Text).</summary>
        public string Content => _textMode() ? (_text ??= ToText(Record.Data)) : (_hex ??= PacketLog.ToHex(Record.Data));

        /// <summary>Die Bytes als UTF-8-Text; Steuerzeichen sichtbar (\r, \n, \t, sonst \xNN), ungültige Folgen als U+FFFD.</summary>
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

    /// <summary>Die Paketverfolgung eines Geräts (wie Wireshark): zeigt alle gesendeten und empfangenen Pakete mit
    /// Richtung (Host→Gerät / Gerät→Host), Geräte-Kennung und Inhalt - wahlweise als Hexbytes oder als Unicode-Text.
    ///
    /// Zwei Betriebsarten, beide als Dokument-Tab: LIVE (<see cref="Attach"/>: schneidet den Verkehr des Geräts mit, solange
    /// der Tab offen ist und die Aufzeichnung läuft) oder eine GELADENE Datei (<see cref="ResetTo"/>, Format siehe
    /// PacketLog). Als <see cref="IDocumentView"/> läuft Speichern/Öffnen über dieselben Wege wie bei Skripten.</summary>
    public partial class PacketTraceControl : UserControl, IDocumentView
    {
        private readonly ObservableCollection<PacketRow> _rows = new();
        private readonly ConcurrentQueue<PacketRecord> _incoming = new();
        private int _flushPending;
        private DeviceManager? _manager;
        private volatile bool _recording = true;
        private bool _loading;

        public string? FilePath { get; set; }
        public bool IsModified { get; private set; }
        public event Action? ModifiedChanged;
        public event Action<int>? CaretLineChanged;

        /// <summary>Das verfolgte Gerät bei einer Live-Verfolgung, sonst null (geladene Datei).</summary>
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

        /// <summary>Beginnt, den Verkehr des Geräts `deviceIdentifier` über `manager` mitzuschneiden.</summary>
        public void Attach(DeviceManager manager, string deviceIdentifier)
        {
            Detach();
            _manager = manager;
            DeviceIdentifier = deviceIdentifier;
            manager.PacketCaptured += OnPacket;
            RecordToggle.IsEnabled = true;
            UpdateInfo();
        }

        /// <summary>Beendet die Live-Verfolgung (beim Schließen des Tabs); die schon erfassten Pakete bleiben.</summary>
        public void Detach()
        {
            if (_manager != null) _manager.PacketCaptured -= OnPacket;
            _manager = null;
            UpdateInfo();
        }

        private void OnPacket(PacketRecord packet)
        {
            // Läuft auf dem Thread, der sendet bzw. des Geräts - nur einreihen, die Anzeige holt sie gebündelt ab.
            if (!_recording || packet.DeviceIdentifier != DeviceIdentifier) return;
            _incoming.Enqueue(packet);
            if (Interlocked.Exchange(ref _flushPending, 1) == 0)
                Dispatcher.BeginInvoke(new Action(Flush), DispatcherPriority.Background);
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
                PacketList.ScrollIntoView(_rows[^1]);
        }

        private void RecordToggle_Click(object sender, RoutedEventArgs e) => _recording = RecordToggle.IsChecked == true;

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            while (_incoming.TryDequeue(out _)) { }
            if (_rows.Count == 0) return;
            _rows.Clear();
            SetModified(DeviceIdentifier == null || FilePath != null); // eine geleerte Live-Verfolgung hat nichts Ungespeichertes mehr
            UpdateInfo();
        }

        private void ViewMode_Changed(object sender, RoutedEventArgs e)
        {
            // Beim Laden der Vorlage feuert Checked schon, bevor die Liste existiert.
            if (PacketList == null) return;
            PacketList.Items.Refresh();
        }

        private void UpdateInfo()
        {
            string target = DeviceIdentifier ?? (FilePath != null ? System.IO.Path.GetFileName(FilePath) : "Protokoll");
            string live = _manager != null ? "" : " (nicht aufzeichnend)";
            InfoText.Text = $"{target}{(DeviceIdentifier != null ? live : "")} – {_rows.Count} Paket{(_rows.Count == 1 ? "" : "e")}";
        }

        private void SetModified(bool value)
        {
            if (IsModified == value) return;
            IsModified = value;
            ModifiedChanged?.Invoke();
        }

        private void PacketList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PacketList.SelectedItem is PacketRow row) CaretLineChanged?.Invoke(row.Number);
        }

        private void ToolBar_HideOverflow(object sender, RoutedEventArgs e)
        {
            if (sender is not ToolBar toolBar) return;
            if (toolBar.Template.FindName("OverflowGrid", toolBar) is FrameworkElement overflow)
                overflow.Visibility = Visibility.Collapsed;
            if (toolBar.Template.FindName("MainPanelBorder", toolBar) is FrameworkElement border)
                border.Margin = new Thickness(0);
        }

        // -----------------------------------------------------------
        // IDocumentView: Speichern/Laden
        // -----------------------------------------------------------

        public string GetText() => PacketLog.Serialize(_rows.Select(r => r.Record));

        /// <summary>Lädt ein Protokoll (siehe PacketLog) und zeigt es an; bei einer fehlerhaften Datei erscheint die Meldung
        /// über der (dann leeren) Liste. Beendet eine Live-Verfolgung dieses Tabs.</summary>
        public void ResetTo(string text, string? filePath)
        {
            Detach();
            DeviceIdentifier = null;
            RecordToggle.IsEnabled = false;
            FilePath = filePath;
            _rows.Clear();
            while (_incoming.TryDequeue(out _)) { }
            ErrorText.Visibility = Visibility.Collapsed;

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
                ErrorText.Text = "Das Protokoll konnte nicht gelesen werden: " + ex.Message;
                ErrorText.Visibility = Visibility.Visible;
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
            PacketList.ScrollIntoView(row);
            PacketList.Focus();
        }

        // -----------------------------------------------------------
        // Bearbeiten-Menü: nur Kopieren und Alles auswählen ergeben bei einer Paketliste Sinn.
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

        /// <summary>Kopiert die gewählten Pakete im Protokollformat (Zeit, Richtung, Gerät, Hexbytes) in die Zwischenablage.</summary>
        public void Copy()
        {
            var selected = PacketList.SelectedItems.OfType<PacketRow>().OrderBy(r => r.Number).Select(r => r.Record).ToList();
            if (selected.Count == 0) return;
            try { Clipboard.SetText(PacketLog.Serialize(selected)); }
            catch (System.Runtime.InteropServices.COMException) { /* Zwischenablage gerade von einem anderen Programm gesperrt */ }
        }

        public void SelectAll()
        {
            PacketList.SelectAll();
            PacketList.Focus();
        }
    }
}
