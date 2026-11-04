using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using fire.Debugging;
using static fire.Editor.SettingsLayout;

namespace fire.Editor
{
    /// <summary>
    /// The fields of a board (ESP32: Run and Debug in spark, docs/NATIVE_BACKEND.md "Ziel esp32") that the project properties and the toolchain settings share: the serial
    /// port (a list of the ports of this machine, typed in also; empty or "auto": the only USB serial port, else the one esptool uses), the baud rate (the usual rates, typed
    /// in also; empty: the inherited one or 115200) and the ESP-IDF folder of spark with a folder picker and the check "idf.py found / not found".
    /// The fields only call `set`; the dialogs write the same keys as before (`port`, `baud`, `idfPath`).
    /// </summary>
    internal static class BoardFields
    {
        public const string Auto = Esp32Launch.AutoPort;

        public static string? PortOf(string? text) => Esp32Launch.PortChoice(text);

        public static (bool Ok, int? Baud) BaudOf(string? text) => Esp32Launch.BaudChoice(text);

        /// <summary>The text of the auto entry: which port auto takes now.</summary>
        private static string AutoText()
        {
            var usb = SerialFramedTransport.CandidatePorts();
            return usb.Count switch
            {
                0 => "auto - no USB serial port now (esptool picks one when flashing)",
                1 => $"auto - now {usb[0]}",
                _ => $"auto - several USB serial ports ({string.Join(", ", usb)}): choose one",
            };
        }

        /// <summary>The port: an editable list (auto, the ports of this machine) and a button that looks for the ports again.</summary>
        public static Control Port(string? value, string? inherited, Action<string?> set)
        {
            var box = new ComboBox { IsEditable = true, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Field, MinWidth = 220 };
            void Fill()
            {
                string? keep = PortOf(box.Text) ?? value;
                box.Items.Clear();
                box.Items.Add(Auto);
                foreach (var p in Esp32Launch.SerialPorts()) box.Items.Add(p);
                box.PlaceholderText = inherited != null ? $"{inherited}   (inherited)" : AutoText();
                ToolTip.SetTip(box, AutoText() + ". Type a port that is not in the list (COM7, /dev/ttyUSB1, /dev/serial/by-id/...).");
                box.Text = keep ?? "";
            }
            Fill();
            box.PropertyChanged += (_, e) => { if (e.Property == ComboBox.TextProperty) set(PortOf(box.Text)); };
            var refresh = new Button { Content = "Refresh", Margin = new Thickness(8, 0, 0, 0) };
            ToolTip.SetTip(refresh, "Looks for the serial ports of this machine again (after plugging in a board)");
            refresh.Click += (_, _) => Fill();
            var dock = new DockPanel();
            DockPanel.SetDock(refresh, Avalonia.Controls.Dock.Right);
            dock.Children.Add(refresh);
            dock.Children.Add(box);
            return dock;
        }

        /// <summary>The baud rate: an editable list of the usual rates; text that is no rate is marked and not taken.</summary>
        public static Control Baud(int? value, int? inherited, Action<int?> set)
        {
            var box = new ComboBox { IsEditable = true, HorizontalAlignment = HorizontalAlignment.Left, Background = Field, Width = 220 };
            foreach (var b in Esp32Launch.CommonBauds) box.Items.Add(b.ToString(CultureInfo.InvariantCulture));
            box.PlaceholderText = inherited is { } i ? $"{i}   (inherited)" : $"{Esp32Launch.DefaultBaud}   (default)";
            box.Text = value?.ToString(CultureInfo.InvariantCulture) ?? "";
            var problem = new TextBlock { Foreground = EditorTheme.ErrorMark, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            box.PropertyChanged += (_, e) =>
            {
                if (e.Property != ComboBox.TextProperty) return;
                var (ok, baud) = BaudOf(box.Text);
                problem.Text = ok ? "" : "not a baud rate - the field is not saved";
                if (ok) set(baud);
            };
            return new StackPanel { Orientation = Orientation.Horizontal, Children = { box, problem } };
        }

        /// <summary>The ESP-IDF folder of spark (empty: IDF_PATH) with "..." for a folder and the check below it.</summary>
        public static Control IdfFolder(string? value, Action<string?> set, Func<TopLevel?> owner)
        {
            var text = TextField("(empty: the environment variable IDF_PATH)", mono: true);
            text.Text = value ?? "";
            var status = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
            void Check()
            {
                var (ok, message) = Esp32Launch.CheckIdf(text.Text);
                status.Text = (ok ? "✓ " : "✗ ") + message;
                status.Foreground = ok ? AccentText : EditorTheme.ErrorMark;
            }
            text.TextChanged += (_, _) => { set(string.IsNullOrWhiteSpace(text.Text) ? null : text.Text!.Trim()); Check(); };
            Check();
            var browse = new Button { Content = "...", Width = 32, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            ToolTip.SetTip(browse, "Choose the ESP-IDF folder (the one with export.sh / export.bat and tools/idf.py)");
            browse.Click += async (_, _) =>
            {
                if (owner()?.StorageProvider is not { } storage) return;
                var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "ESP-IDF folder (with export.sh / export.bat)", AllowMultiple = false });
                if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path) text.Text = path;
            };
            var dock = new DockPanel();
            DockPanel.SetDock(browse, Avalonia.Controls.Dock.Right);
            dock.Children.Add(browse);
            dock.Children.Add(text);
            return new StackPanel { Children = { dock, status } };
        }

        /// <summary>One line on ESP-IDF for a page that does not edit it (the project properties): found or not, and where to set it.</summary>
        public static TextBlock IdfStatus(string? configured)
        {
            var (ok, message) = Esp32Launch.CheckIdf(configured);
            return new TextBlock
            {
                Text = "ESP-IDF: " + message + " - set its folder in Tools > Toolchain Settings > ESP32 Board.", FontSize = 12, TextWrapping = TextWrapping.Wrap,
                Foreground = ok ? EditorTheme.TextDim : EditorTheme.ErrorMark,
            };
        }
    }
}
