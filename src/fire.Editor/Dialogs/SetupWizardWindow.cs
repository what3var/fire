using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using fire.Editor.Setup;

namespace fire.Editor
{
    /// <summary>
    /// The setup wizard of spark (its first start, and ? > Setup Wizard): the pages of <see cref="SetupWizardModel"/> - welcome, toolchains, the model of the assistant (when it is on),
    /// summary - with the steps on the left and Back / Next / Start below. Closed with Start, <see cref="Plan"/> holds what the progress window then does; Not now (or the close box)
    /// leaves it null.
    /// </summary>
    internal sealed class SetupWizardWindow : Window
    {
        private readonly SetupWizardModel _model;
        private readonly StackPanel _steps = new() { Spacing = 4 };
        private readonly TextBlock _title = new() { FontSize = 22, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        private readonly TextBlock _stepText = new() { Foreground = EditorTheme.TextDim, FontSize = 12, Margin = new Thickness(0, 0, 0, 14) };
        private readonly ContentControl _page = new();
        private readonly Button _back = new() { Content = "_Back", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        private readonly Button _next = new() { Content = "_Next", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center, IsDefault = true };
        private readonly Button _skip = new() { Content = "Not now", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };

        /// <summary>What to do (Start), or null (Not now).</summary>
        public IReadOnlyList<SetupPlanItem>? Plan { get; private set; }

        public SetupWizardWindow(SetupWizardModel model)
        {
            _model = model;
            Title = "spark setup";
            Width = 820;
            Height = 580;
            MinWidth = 640;
            MinHeight = 460;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            Background = EditorTheme.Background;

            _back.Click += (_, _) => { _model.Back(); ShowPage(); };
            _next.Click += (_, _) =>
            {
                if (_model.IsLast) { Plan = _model.Plan(); Close(true); return; }
                _model.Next();
                ShowPage();
            };
            _skip.Click += (_, _) => Close(false);
            ToolTip.SetTip(_skip, "Nothing is installed now. The wizard is in ? > Setup Wizard; the toolchain asks again when a build needs one.");
            KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(false); } };

            // left: the steps
            var side = new Border
            {
                Background = EditorTheme.DarkSurface, Padding = new Thickness(18, 20), Width = 210,
                Child = new DockPanel
                {
                    Children =
                    {
                        Docked(new TextBlock { Text = "spark", FontSize = 26, FontWeight = FontWeight.SemiBold, Foreground = EditorTheme.Magenta, Margin = new Thickness(0, 0, 0, 2) }, Avalonia.Controls.Dock.Top),
                        Docked(new TextBlock { Text = "first-start setup", Foreground = EditorTheme.TextDim, Margin = new Thickness(0, 0, 0, 24) }, Avalonia.Controls.Dock.Top),
                        _steps,
                    },
                },
            };

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { _back, _next } };
            var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
            DockPanel.SetDock(buttons, Avalonia.Controls.Dock.Right);
            footer.Children.Add(buttons);
            footer.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { _skip } });

            var main = new DockPanel { Margin = new Thickness(24, 20, 24, 18) };
            DockPanel.SetDock(_title, Avalonia.Controls.Dock.Top);
            DockPanel.SetDock(_stepText, Avalonia.Controls.Dock.Top);
            DockPanel.SetDock(footer, Avalonia.Controls.Dock.Bottom);
            main.Children.Add(_title);
            main.Children.Add(_stepText);
            main.Children.Add(footer);
            main.Children.Add(new ScrollViewer { Content = _page, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });

            var root = new DockPanel();
            DockPanel.SetDock(side, Avalonia.Controls.Dock.Left);
            root.Children.Add(side);
            root.Children.Add(main);
            Content = root;
            ShowPage();
        }

        private static Control Docked(Control c, Avalonia.Controls.Dock dock) { DockPanel.SetDock(c, dock); return c; }

        private void ShowPage()
        {
            _title.Text = _model.PageTitle;
            _stepText.Text = _model.StepText;
            _back.IsEnabled = _model.CanBack;
            _next.Content = _model.IsLast ? "_Start" : "_Next";
            ToolTip.SetTip(_next, _model.IsLast ? "Installs what is listed; a window shows the progress (it can go to the background)" : null);
            _page.Content = _model.Page switch
            {
                SetupPage.Welcome => WelcomePage(),
                SetupPage.Toolchains => ToolchainPage(),
                SetupPage.Model => ModelPage(),
                _ => SummaryPage(),
            };
            _steps.Children.Clear();
            for (int i = 0; i < _model.Pages.Count; i++)
            {
                bool current = i == _model.PageIndex;
                string name = _model.Pages[i] switch { SetupPage.Welcome => "Welcome", SetupPage.Toolchains => "Toolchains", SetupPage.Model => "Assistant model", _ => "Summary" };
                _steps.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(4), Padding = new Thickness(10, 6),
                    Background = current ? new SolidColorBrush(EditorTheme.AccentColor, 0.35) : Brushes.Transparent,
                    Child = new TextBlock
                    {
                        Text = $"{i + 1}  {name}", FontWeight = current ? FontWeight.SemiBold : FontWeight.Normal,
                        Foreground = i < _model.PageIndex ? EditorTheme.TextDim : EditorTheme.Text,
                    },
                });
            }
        }

        // ---- the pages ----

        private static TextBlock Para(string text, IBrush? brush = null, double size = 14) =>
            new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = brush ?? EditorTheme.Text, FontSize = size, Margin = new Thickness(0, 0, 0, 10) };

        private static TextBlock Heading(string text) =>
            new() { Text = text, FontWeight = FontWeight.SemiBold, FontSize = 15, Margin = new Thickness(0, 8, 0, 8) };

        private static Control Badge(string text, IBrush brush) => new Border
        {
            BorderBrush = brush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 0), Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = text, FontSize = 11, Foreground = brush },
        };

        private Control WelcomePage()
        {
            var env = _model.Environment;
            var p = new StackPanel();
            p.Children.Add(Para("spark asks a few things once and then sets them up for you: a C/C++ toolchain for native builds" +
                (_model.Pages.Contains(SetupPage.Model) ? ", the AI model of the assistant" : "") + ", and the first build of the standard packages."));
            p.Children.Add(Para("Everything is optional and can be changed later: Tools > Toolchain Settings, Tools > Assistant, and this wizard again in ? > Setup Wizard."));
            string os = env.IsWindows ? "Windows" : env.IsMacOS ? "macOS" : "Linux";
            string memory = env.Machine.MemoryMB > 0 ? $"{env.Machine.MemoryMB / 1024.0:0.#} GB memory" : "memory unknown";
            p.Children.Add(Heading("This computer"));
            p.Children.Add(Para($"{os} ({(env.IsX64 ? "x64" : System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant())}), {memory}, {env.Machine.Cores} threads" +
                (env.Machine.Simd ? ", SIMD" : ""), EditorTheme.TextDim));
            if (env.AssistantAvailable && !env.AssistantEnabled)
                p.Children.Add(Para("The assistant is off. Switch it on in Tools > Assistant; it asks for its model then (or open this wizard again).", EditorTheme.TextDim));
            return p;
        }

        private Control ToolchainPage()
        {
            var env = _model.Environment;
            var p = new StackPanel();
            p.Children.Add(Para("Native builds (Build, a run as a native program) and packages with C or C++ natives need a C/C++ compiler. spark uses the one it finds, in this order: " +
                "the one chosen in the toolchain settings, the portable ones next to spark, the PATH."));
            p.Children.Add(Heading("Found on this computer"));
            if (env.Detected.Count == 0 && !env.W64devkitThere) p.Children.Add(Para("None.", EditorTheme.TextDim));
            foreach (var d in env.Detected)
                p.Children.Add(new DockPanel
                {
                    Margin = new Thickness(0, 0, 0, 6),
                    Children =
                    {
                        Docked(new TextBlock { Text = "✓", Foreground = EditorTheme.Magenta, Width = 22, FontWeight = FontWeight.Bold }, Avalonia.Controls.Dock.Left),
                        Docked(new TextBlock { Text = d.Name, Width = 200, FontWeight = FontWeight.SemiBold }, Avalonia.Controls.Dock.Left),
                        new TextBlock { Text = d.Path, Foreground = EditorTheme.TextDim, TextTrimming = TextTrimming.CharacterEllipsis },
                    },
                });

            if (_model.OffersToolchains || _model.Toolchains.Any(t => t.There))
            {
                p.Children.Add(Heading(_model.OffersToolchains ? "Download (portable, nothing is installed on the system)" : "Portable toolchains"));
                foreach (var t in _model.Toolchains.Where(t => t.Installable || t.There))
                {
                    var head = new StackPanel { Orientation = Orientation.Horizontal };
                    head.Children.Add(new TextBlock { Text = t.Name, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
                    head.Children.Add(new TextBlock { Text = "  " + (t.There ? "installed" : t.SizeText), Foreground = EditorTheme.TextDim, VerticalAlignment = VerticalAlignment.Center });
                    if (t.Recommended && !t.There) head.Children.Add(Badge("recommended", EditorTheme.Magenta));
                    var body = new StackPanel { Children = { head, new TextBlock { Text = t.Description, TextWrapping = TextWrapping.Wrap, Foreground = EditorTheme.TextDim, FontSize = 12 } } };
                    var option = t;
                    var box = new CheckBox { Content = body, IsChecked = t.Selected || t.There, IsEnabled = t.Installable, Margin = new Thickness(0, 0, 0, 10) };
                    box.IsCheckedChanged += (_, _) => option.Selected = box.IsChecked == true;
                    ToolTip.SetTip(box, "Kept in " + t.Folder);
                    p.Children.Add(box);
                }
            }
            p.Children.Add(Heading(_model.OffersToolchains ? "Good to know" : "How to get one"));
            foreach (string hint in _model.ToolchainHints)
                p.Children.Add(new DockPanel
                {
                    Margin = new Thickness(0, 0, 0, 6),
                    Children =
                    {
                        Docked(new TextBlock { Text = "•", Width = 16, Foreground = EditorTheme.TextDim }, Avalonia.Controls.Dock.Left),
                        new SelectableTextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, Foreground = EditorTheme.Text },
                    },
                });
            return p;
        }

        private Control ModelPage()
        {
            var p = new StackPanel();
            p.Children.Add(Para("The assistant answers questions about fire and explains errors with a small AI model that runs on this computer - nothing leaves it. " +
                "The model is downloaded once from Hugging Face and checked (SHA-256)."));
            const string group = "model";
            foreach (var m in _model.Models)
            {
                var head = new StackPanel { Orientation = Orientation.Horizontal };
                head.Children.Add(new TextBlock { Text = m.Info.Name, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
                head.Children.Add(new TextBlock { Text = $"  {m.Info.SizeText}, {m.MemoryText}", Foreground = EditorTheme.TextDim, VerticalAlignment = VerticalAlignment.Center });
                if (m.Recommended) head.Children.Add(Badge("recommended", EditorTheme.Magenta));
                if (m.Stronger) head.Children.Add(Badge("stronger", EditorTheme.Purple));
                if (m.Installed) head.Children.Add(Badge("installed", EditorTheme.Yellow));
                if (!m.Fits) head.Children.Add(Badge("too big", EditorTheme.WineRed));
                var body = new StackPanel { Children = { head } };
                body.Children.Add(new TextBlock { Text = m.Info.Description, TextWrapping = TextWrapping.Wrap, Foreground = EditorTheme.TextDim, FontSize = 12 });
                body.Children.Add(new TextBlock { Text = "License: " + m.Info.License, TextWrapping = TextWrapping.Wrap, Foreground = EditorTheme.Comment, FontSize = 11 });
                var id = m.Info.Id;
                var radio = new RadioButton { GroupName = group, Content = body, IsChecked = _model.SelectedModelId == id, IsEnabled = m.Fits, Margin = new Thickness(0, 0, 0, 10) };
                radio.IsCheckedChanged += (_, _) => { if (radio.IsChecked == true) _model.SelectedModelId = id; };
                ToolTip.SetTip(radio, m.Info.LicenseUrl);
                p.Children.Add(radio);
            }
            var none = new RadioButton
            {
                GroupName = group, IsChecked = _model.SelectedModelId == "",
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = "None for now", FontWeight = FontWeight.SemiBold },
                        new TextBlock { Text = "The assistant gives tips without a model and offers the download when it is poked.", Foreground = EditorTheme.TextDim, FontSize = 12, TextWrapping = TextWrapping.Wrap },
                    },
                },
            };
            none.IsCheckedChanged += (_, _) => { if (none.IsChecked == true) _model.SelectedModelId = ""; };
            p.Children.Add(none);
            return p;
        }

        private Control SummaryPage()
        {
            var p = new StackPanel();
            p.Children.Add(Para("Start does this (a window shows the progress; it can go to the background, the status bar goes on):"));
            foreach (string line in _model.SummaryLines)
                p.Children.Add(new DockPanel
                {
                    Margin = new Thickness(0, 0, 0, 8),
                    Children =
                    {
                        Docked(new TextBlock { Text = "•", Width = 16, Foreground = EditorTheme.Magenta }, Avalonia.Controls.Dock.Left),
                        new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap },
                    },
                });
            return p;
        }

        /// <summary>Saves every page as a PNG into <paramref name="folder"/> ("setup-1-welcome.png", ...) - to look at the layout without a screen (FIRE_SETUP_WIZARD_SHOTS).</summary>
        public void SaveShots(string folder)
        {
            Directory.CreateDirectory(folder);
            for (int i = 0; i < _model.Pages.Count; i++)
            {
                _model.GoTo(_model.Pages[i]);
                ShowPage();
                SaveShot(this, Path.Combine(folder, $"setup-{i + 1}-{_model.Pages[i].ToString().ToLowerInvariant()}.png"));
            }
            _model.GoTo(SetupPage.Welcome);
            ShowPage();
        }

        /// <summary>The window as it is drawn now into a PNG.</summary>
        public static void SaveShot(Window window, string file)
        {
            var size = new Size(window.Bounds.Width > 0 ? window.Bounds.Width : window.Width, window.Bounds.Height > 0 ? window.Bounds.Height : window.Height);
            window.Measure(size);
            window.Arrange(new Rect(size));
            window.UpdateLayout();
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)size.Width, (int)size.Height));
            bitmap.Render(window);
            using (var stream = File.Create(file)) bitmap.Save(stream);
        }
    }
}
