using System;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace fire.Editor
{
    /// <summary>The window that is up while the editor starts: the logo, the name, the version and what is going on. No border, no taskbar entry; it closes when the main window is there.</summary>
    internal sealed class SplashWindow : Window
    {
        private readonly TextBlock _status = new() { Text = "Starting...", Foreground = EditorTheme.TextDim, FontSize = 12 };

        public SplashWindow()
        {
            Width = 560;
            Height = 300;
            CanResize = false;
            WindowDecorations = Avalonia.Controls.WindowDecorations.None;
            ShowInTaskbar = false;
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = EditorTheme.DarkSurface;
            Title = "spark";

            Control logo;
            try { logo = new Image { Source = new Bitmap(AssetLoader.Open(new Uri("avares://spark/Assets/Logo-64.png"))), Width = 96, Height = 96 }; }
            catch (Exception) { logo = TemplateIcon.Create("script", 96); }

            var version = typeof(SplashWindow).Assembly.GetName().Version;
            string versionText = version == null ? "" : $"Version {version.Major}.{version.Minor}";

            var title = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24, 0, 0, 0) };
            title.Children.Add(new TextBlock { Text = "spark", FontSize = 52, FontWeight = FontWeight.SemiBold, Foreground = EditorTheme.Magenta });
            title.Children.Add(new TextBlock { Text = "the editor of the fire language", FontSize = 15, Foreground = EditorTheme.Text });

            var top = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Children = { logo, title } };

            var bar = new ProgressBar { IsIndeterminate = true, Height = 3, MinHeight = 3, Foreground = EditorTheme.Magenta, Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Bottom };
            var footer = new DockPanel { Margin = new Thickness(20, 0, 20, 14) };
            if (versionText.Length > 0) { var v = new TextBlock { Text = versionText, Foreground = EditorTheme.TextDim, FontSize = 12 }; DockPanel.SetDock(v, Avalonia.Controls.Dock.Right); footer.Children.Add(v); }
            footer.Children.Add(_status);

            var grid = new Grid { RowDefinitions = new RowDefinitions("*,Auto,Auto") };
            grid.Children.Add(top);
            Grid.SetRow(footer, 1);
            grid.Children.Add(footer);
            Grid.SetRow(bar, 2);
            grid.Children.Add(bar);
            Content = new Border { BorderBrush = EditorTheme.Border, BorderThickness = new Thickness(1), Child = grid };
        }

        public void SetStatus(string text) => _status.Text = text;
    }
}
