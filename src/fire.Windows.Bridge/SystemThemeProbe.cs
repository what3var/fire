using System.Diagnostics;
using System.Runtime.InteropServices;
using fire.Values;

namespace fire.Windows.Bridge
{
    /// <summary>
    /// `SystemTheme.Current()` in the virtual machine: what the desktop says about the look of programs - the same queries as the native runtime
    /// (native/platform/std/fire_systheme.hpp): Windows reads the registry and the DPI, Linux GNOME's gsettings or KDE's kdeglobals, macOS `defaults`.
    /// What cannot be found keeps its default (scale 1, light, the accent 0x0078D7, no font); FIRE_SYSTEM_THEME=off switches the query off.
    /// </summary>
    public sealed class SystemThemeProbe
    {
        public bool Embedded;
        public double Scale = 1.0;
        public bool Dark;
        public int Accent = 0x0078D7;
        public string Fonts = "";
        public int FontSize;

        private static SystemThemeProbe? s_cached;

        /// <summary>The theme of the desktop (asked once per process: the commands it runs take a moment).</summary>
        public static SystemThemeProbe Current() => s_cached ??= Query();

        /// <summary>[embedded, scale, dark, accent, font candidates, font size] - the array of `__GRPHWinSystemTheme`.</summary>
        public Value ToValue()
        {
            var array = new ScriptArray(6);
            array.Items[0] = Value.MakeBool(Embedded);
            array.Items[1] = Value.MakeFloat(Scale);
            array.Items[2] = Value.MakeBool(Dark);
            array.Items[3] = Value.MakeInt(Accent);
            array.Items[4] = Value.MakeString(Fonts);
            array.Items[5] = Value.MakeInt(FontSize);
            return Value.MakeArray(array);
        }

        private static bool Off()
        {
            string? v = Environment.GetEnvironmentVariable("FIRE_SYSTEM_THEME");
            return v is "off" or "0" or "false";
        }

        private static SystemThemeProbe Query()
        {
            var t = new SystemThemeProbe();
            if (Off()) return t;
            try
            {
                if (OperatingSystem.IsWindows()) QueryWindows(t);
                else if (OperatingSystem.IsMacOS()) QueryMac(t);
                else QueryUnix(t);
            }
            catch (Exception)
            {
                // (whatever fails keeps its default)
            }
            if (t.Scale is < 0.5 or > 8) t.Scale = 1.0;
            return t;
        }

        [DllImport("user32.dll")] private static extern uint GetDpiForSystem();

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private static void QueryWindows(SystemThemeProbe t)
        {
            try { uint dpi = GetDpiForSystem(); if (dpi >= 48) t.Scale = dpi / 96.0; } catch (Exception) { }
            using (var personalize = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                if (personalize?.GetValue("AppsUseLightTheme") is int light) t.Dark = light == 0;
            using (var dwm = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM"))
                if (dwm?.GetValue("AccentColor") is int abgr)
                {
                    uint v = unchecked((uint)abgr);
                    t.Accent = (int)(((v & 0xFF) << 16) | (v & 0xFF00) | ((v >> 16) & 0xFF));
                }
            // the font of message boxes: a LOGFONTW (lfHeight at 0, lfFaceName at 28)
            using (var metrics = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop\WindowMetrics"))
                if (metrics?.GetValue("MessageFont") is byte[] font && font.Length >= 92)
                {
                    int height = Math.Abs(BitConverter.ToInt32(font, 0));
                    string face = System.Text.Encoding.Unicode.GetString(font, 28, 64);
                    int nul = face.IndexOf('\0');
                    if (nul >= 0) face = face.Substring(0, nul);
                    if (face.Length > 0) t.Fonts = face + ";Segoe UI;Tahoma;Arial";
                    if (height > 0) t.FontSize = height;
                }
        }

        private static void QueryMac(SystemThemeProbe t)
        {
            if (Run("defaults", "read -g AppleInterfaceStyle") == "Dark") t.Dark = true;
            string accent = Run("defaults", "read -g AppleAccentColor");
            int[] colors = { 0x8C8C8C, 0xFF5257, 0xF7821B, 0xFFC600, 0x62BA46, 0x007AFF, 0xA550A7, 0xF74F9E };
            t.Accent = int.TryParse(accent, out int i) && i + 1 is >= 0 and < 8 ? colors[i + 1] : 0x007AFF;
            t.Fonts = "SF Pro Text;SF Pro;SFNS;Helvetica Neue;Helvetica;Arial";
            t.FontSize = 13;
        }

        private static readonly Dictionary<string, int> GnomeAccents = new()
        {
            ["blue"] = 0x3584E4, ["teal"] = 0x2190A4, ["green"] = 0x3A944A, ["yellow"] = 0xC88800, ["orange"] = 0xED5B00,
            ["red"] = 0xE62D42, ["pink"] = 0xD56199, ["purple"] = 0x9141AC, ["slate"] = 0x6F8396,
        };

        private static void QueryUnix(SystemThemeProbe t)
        {
            string scheme = Gsettings("color-scheme"), gtkTheme = Gsettings("gtk-theme");
            string? home = Environment.GetEnvironmentVariable("HOME");
            string kde = home != null ? Path.Combine(home, ".config", "kdeglobals") : "";
            if (scheme.Length > 0 || gtkTheme.Length > 0)
            {
                if (scheme == "prefer-dark" || gtkTheme.Contains("dark", StringComparison.OrdinalIgnoreCase)) t.Dark = true;
                t.Accent = GnomeAccents.TryGetValue(Gsettings("accent-color"), out int accent) ? accent : 0x3584E4;
                var (family, points) = FontSpec(Gsettings("font-name"));
                if (family.Length > 0) t.Fonts = family + ";Cantarell;Noto Sans;DejaVu Sans;Liberation Sans";
                if (points > 0) t.FontSize = (int)(points * 96.0 / 72.0 + 0.5);
                string factorText = Gsettings("scaling-factor");
                if (double.TryParse(factorText.Split(' ').Last(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double factor) && factor >= 1) t.Scale = factor;
                if (double.TryParse(Gsettings("text-scaling-factor"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double text) && text is > 0.5 and < 4) t.Scale *= text;
            }
            else if (kde.Length > 0 && File.Exists(kde))
            {
                string colors = Ini(kde, "General", "ColorScheme");
                if (colors.Contains("dark", StringComparison.OrdinalIgnoreCase)) t.Dark = true;
                var rgb = Ini(kde, "General", "AccentColor").Split(',');
                if (rgb.Length == 3 && int.TryParse(rgb[0], out int r) && int.TryParse(rgb[1], out int g) && int.TryParse(rgb[2], out int b)) t.Accent = (r << 16) | (g << 8) | b;
                var (family, points) = FontSpec(Ini(kde, "General", "font"));
                if (family.Length > 0) t.Fonts = family + ";Noto Sans;DejaVu Sans;Liberation Sans";
                if (points > 0) t.FontSize = (int)(points * 96.0 / 72.0 + 0.5);
            }
            if (double.TryParse(Environment.GetEnvironmentVariable("GDK_SCALE"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double gdk) && gdk >= 1) t.Scale = gdk;
            if (double.TryParse(Environment.GetEnvironmentVariable("QT_SCALE_FACTOR"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double qt) && qt >= 0.5) t.Scale = qt;
        }

        private static string Gsettings(string key) => Run("gsettings", "get org.gnome.desktop.interface " + key);

        /// <summary>"Cantarell 11" or KDE's "Noto Sans,10,-1,5,50,..." -> (family, points).</summary>
        private static (string Family, double Points) FontSpec(string spec)
        {
            spec = Trim(spec);
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            int comma = spec.IndexOf(',');
            if (comma >= 0)
            {
                string rest = spec.Substring(comma + 1).Split(',')[0];
                return (spec.Substring(0, comma), double.TryParse(rest, System.Globalization.NumberStyles.Float, inv, out double p) ? p : 0);
            }
            int space = spec.LastIndexOf(' ');
            if (space > 0 && double.TryParse(spec.Substring(space + 1), System.Globalization.NumberStyles.Float, inv, out double pts) && pts > 0) return (spec.Substring(0, space), pts);
            return (spec, 0);
        }

        private static string Ini(string path, string section, string key)
        {
            string current = "";
            foreach (string raw in File.ReadLines(path))
            {
                string line = raw.Trim();
                if (line.StartsWith('[')) { current = line; continue; }
                if (current == "[" + section + "]" && line.StartsWith(key + "=", StringComparison.Ordinal)) return line.Substring(key.Length + 1);
            }
            return "";
        }

        private static string Trim(string s) => s.Trim().Trim('\'', '"').Trim();

        /// <summary>The first line of a command's output, "" when it is not there or fails.</summary>
        private static string Run(string file, string arguments)
        {
            try
            {
                var info = new ProcessStartInfo(file, arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                using var process = Process.Start(info);
                if (process == null) return "";
                string output = process.StandardOutput.ReadToEnd();
                if (!process.WaitForExit(2000)) { try { process.Kill(); } catch (Exception) { } return ""; }
                if (process.ExitCode != 0) return "";
                int nl = output.IndexOf('\n');
                return Trim(nl >= 0 ? output.Substring(0, nl) : output);
            }
            catch (Exception)
            {
                return "";
            }
        }
    }
}
