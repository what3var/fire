using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace fire.Editor
{
    /// <summary>The version of spark (one place for the splash screen and the About window).</summary>
    internal static class AppInfo
    {
        /// <summary>The version as it is shown (the one of the installer, setup.iss).</summary>
        public const string Version = "alpha-2610";

        /// <summary>The copyright of fire (LICENSE).</summary>
        public const string Copyright = "Copyright (c) 2026 David Müller - MIT License";
    }

    /// <summary>
    /// ? > About spark: the logo, the version, the runtime and the attributions - the libraries, fonts and icons spark and fire are built with, each with
    /// its authors, its license and a link to it.
    /// </summary>
    internal sealed class AboutWindow : Window
    {
        /// <summary>A work spark uses: its name, what it is used for, its authors, its license and where it is.</summary>
        internal sealed record Credit(string Name, string Use, string Authors, string License, string Url);

        /// <summary>The attributions (keep them in step with the packages of the projects and the code taken over).</summary>
        internal static IReadOnlyList<Credit> Credits { get; } = new[]
        {
            new Credit("Avalonia", "the user interface of spark", "AvaloniaUI OÜ and contributors", "MIT", "https://avaloniaui.net"),
            new Credit("AvaloniaEdit", "the text editors", "AvaloniaUI contributors (after AvalonEdit by Daniel Grunwald)", "MIT", "https://github.com/AvaloniaUI/AvaloniaEdit"),
            new Credit("Dock", "the dockable tool windows", "Wiesław Šoltés", "MIT", "https://github.com/wieslawsoltes/Dock"),
            new Credit("Inter", "the font of the user interface", "Rasmus Andersson", "SIL Open Font License 1.1", "https://rsms.me/inter"),
            new Credit("LibGit2Sharp", "the Git integration", "LibGit2Sharp contributors", "MIT", "https://github.com/libgit2/libgit2sharp"),
            new Credit("libgit2", "the Git integration (native)", "libgit2 contributors", "GPL 2.0 with linking exception", "https://libgit2.org"),
            new Credit("MemoryPack", "binary serialization of compiled programs", "Cysharp, Inc.", "MIT", "https://github.com/Cysharp/MemoryPack"),
            new Credit("PropertyChanged.Fody", "property change notification", "Simon Cropp and contributors", "MIT", "https://github.com/Fody/PropertyChanged"),
            new Credit("SDL3-CS", "windows, input and sound of fire programs", "Eduard Gushchin", "zlib", "https://github.com/edwardgushchin/SDL3-CS"),
            new Credit("SDL", "windows, input and sound (SDL3 in the editor, SDL2 for native programs)", "Sam Lantinga and contributors", "zlib", "https://libsdl.org"),
            new Credit("System.IO.Ports", "serial ports of the device simulation", ".NET Foundation and contributors", "MIT", "https://dot.net"),
            new Credit("libschrift", "TrueType rendering (ported to C# and C++)", "Thomas Oltmann and contributors", "ISC", "https://github.com/tomolt/libschrift"),
            new Credit("pcface", "the 8x8 and 8x14 glyphs of the terminal font", "Susam Pal", "MIT", "https://github.com/susam/pcface"),
            new Credit("Material Design Icons", "icons of the tool strips", "Pictogrammers and Google", "Apache 2.0", "https://pictogrammers.com/library/mdi"),
            new Credit("Falstad's circuit simulator", "the model the circuit simulation follows (no code taken over); the import reads its text format", "Paul Falstad", "-", "https://www.falstad.com/circuit"),
        };

        public AboutWindow()
        {
            Title = "About spark";
            Width = 640; Height = 560; MinWidth = 480; MinHeight = 400;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            CanResize = true;
            Background = EditorTheme.Background;

            Control logo;
            try { logo = new Image { Source = new Bitmap(AssetLoader.Open(new Uri("avares://spark/Assets/Logo-64.png"))), Width = 64, Height = 64 }; }
            catch (Exception) { logo = new Border { Width = 64, Height = 64 }; }

            var title = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
            title.Children.Add(new TextBlock { Text = "spark", FontSize = 30, FontWeight = FontWeight.SemiBold, Foreground = EditorTheme.Magenta });
            title.Children.Add(new TextBlock { Text = "the IDE of the fire language - with forge (compiler) and ember (packages)", Foreground = EditorTheme.Text, TextWrapping = TextWrapping.Wrap });
            title.Children.Add(new TextBlock { Text = "Version " + AppInfo.Version, Foreground = EditorTheme.TextDim });

            var head = new DockPanel { Margin = new Thickness(20, 18, 20, 8) };
            DockPanel.SetDock(logo, Avalonia.Controls.Dock.Left);
            head.Children.Add(logo);
            head.Children.Add(title);

            var details = new TextBlock
            {
                Text = AppInfo.Copyright + "\n" + RuntimeText(),
                Foreground = EditorTheme.TextDim, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20, 0, 20, 10),
            };

            var list = new StackPanel { Spacing = 8, Margin = new Thickness(20, 4, 20, 12) };
            list.Children.Add(new TextBlock { Text = "Built with", FontWeight = FontWeight.SemiBold, Foreground = EditorTheme.Text });
            foreach (var credit in Credits) list.Children.Add(CreditView(credit));
            list.Children.Add(new TextBlock { Text = "Licenses", FontWeight = FontWeight.SemiBold, Foreground = EditorTheme.Text, Margin = new Thickness(0, 10, 0, 0) });
            foreach (var (header, file) in LicenseTexts)
            {
                var text = new SelectableTextBlock
                {
                    Text = ReadLicense(file), FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace"), FontSize = 11,
                    Foreground = EditorTheme.TextDim, TextWrapping = TextWrapping.Wrap,
                };
                list.Children.Add(new Expander { Header = header, Content = text, HorizontalAlignment = HorizontalAlignment.Stretch });
            }
            var scroll = new ScrollViewer { Content = list };
            var listBorder = new Border { Child = scroll, BorderBrush = EditorTheme.Border, BorderThickness = new Thickness(0, 1), Background = EditorTheme.DarkSurface };

            var copy = new Button { Content = "Copy Info", MinWidth = 90 };
            copy.Click += async (_, _) =>
            {
                var clipboard = Clipboard;
                if (clipboard != null) await clipboard.SetTextAsync("spark " + AppInfo.Version + "\n" + RuntimeText());
            };
            var ok = new Button { Content = "OK", MinWidth = 90, IsDefault = true, IsCancel = true };
            ok.Click += (_, _) => Close();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(20, 10) };
            buttons.Children.Add(copy);
            buttons.Children.Add(ok);

            var root = new DockPanel();
            DockPanel.SetDock(head, Avalonia.Controls.Dock.Top);
            DockPanel.SetDock(details, Avalonia.Controls.Dock.Top);
            DockPanel.SetDock(buttons, Avalonia.Controls.Dock.Bottom);
            root.Children.Add(head);
            root.Children.Add(details);
            root.Children.Add(buttons);
            root.Children.Add(listBorder);
            Content = root;
            KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        }

        /// <summary>The license texts shown in full: fire's own and those of the fonts and the code taken over (Assets/Licenses).</summary>
        internal static IReadOnlyList<(string Header, string File)> LicenseTexts { get; } = new[]
        {
            ("fire (MIT)", "LICENSE-fire.txt"),
            ("Inter - the font of the user interface (SIL Open Font License 1.1)", "LICENSE-Inter.txt"),
            ("pcface - the glyphs of the terminal font (MIT)", "LICENSE-pcface.txt"),
            ("libschrift - the TrueType renderer (ISC)", "LICENSE-libschrift.txt"),
        };

        /// <summary>The text of a license in Assets/Licenses (a note if it is missing).</summary>
        internal static string ReadLicense(string file)
        {
            try
            {
                using var stream = AssetLoader.Open(new Uri("avares://spark/Assets/Licenses/" + file));
                using var reader = new System.IO.StreamReader(stream);
                return reader.ReadToEnd().Trim();
            }
            catch (Exception) { return "(The text of the license is missing: " + file + ")"; }
        }

        /// <summary>The runtime spark runs on (for bug reports).</summary>
        internal static string RuntimeText() =>
            $".NET {Environment.Version} on {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})";

        private static Control CreditView(Credit c)
        {
            var name = new TextBlock { Text = c.Name, FontWeight = FontWeight.SemiBold, Foreground = EditorTheme.Text };
            var license = new TextBlock { Text = c.License == "-" ? "" : "  " + c.License, Foreground = EditorTheme.Yellow, FontSize = 12, VerticalAlignment = VerticalAlignment.Bottom };
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(name);
            line.Children.Add(license);
            var link = new TextBlock { Text = c.Url, Foreground = EditorTheme.Purple, FontSize = 12, Cursor = new Cursor(StandardCursorType.Hand) };
            ToolTip.SetTip(link, "Open " + c.Url);
            link.PointerPressed += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(c.Url) { UseShellExecute = true }); }
                catch (Exception) { }
            };
            var panel = new StackPanel { Spacing = 1 };
            panel.Children.Add(line);
            panel.Children.Add(new TextBlock { Text = c.Authors + " - " + c.Use, Foreground = EditorTheme.TextDim, FontSize = 12, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(link);
            return panel;
        }
    }
}
