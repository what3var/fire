using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace fire.Editor
{
    /// <summary>
    /// "New Raster Image": name, format (PNG or BMP), size in pixels and background. The result is in <see cref="FileName"/> (with the extension of the format), <see cref="ImageWidth"/>,
    /// <see cref="ImageHeight"/> and <see cref="Background"/> (packed R,G,B,A like PixelColor: 0 is transparent). A BMP has no transparency, so there transparent becomes white.
    /// </summary>
    internal sealed class NewImageDialog : Window
    {
        public const int MaxSide = 8192;

        private readonly TextBox _name = new() { MinWidth = 260 };
        private readonly ComboBox _format = new() { ItemsSource = new[] { "PNG", "BMP" }, SelectedIndex = 0 };
        private readonly NumericUpDown _width = new() { Minimum = 1, Maximum = MaxSide, Value = 64, FormatString = "0", Width = 130 };
        private readonly NumericUpDown _height = new() { Minimum = 1, Maximum = MaxSide, Value = 64, FormatString = "0", Width = 130 };
        private readonly ComboBox _background = new() { ItemsSource = new[] { "Transparent", "White", "Black" }, SelectedIndex = 0 };
        private readonly TextBlock _error = new() { Foreground = EditorTheme.ErrorMark, TextWrapping = Avalonia.Media.TextWrapping.Wrap, IsVisible = false, MaxWidth = 300 };

        public string FileName { get; private set; } = "";
        public int ImageWidth { get; private set; }
        public int ImageHeight { get; private set; }
        public uint Background { get; private set; }

        public NewImageDialog(string initialName = "image")
        {
            Title = "New Raster Image";
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            CanResize = false;
            ShowInTaskbar = false;
            _name.Text = initialName;

            var ok = new Button { Content = "Create", MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
            ok.Classes.Add("accent");
            ok.Click += (_, _) => Accept();
            var cancel = new Button { Content = "Cancel", MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
            cancel.Click += (_, _) => Close(false);

            static Control Labeled(string label, Control control) => new StackPanel { Spacing = 2, Children = { new TextBlock { Text = label, Foreground = EditorTheme.TextDim }, control } };
            var size = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Labeled("Width (pixels)", _width), Labeled("Height (pixels)", _height) } };
            var kind = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Labeled("Format", _format), Labeled("Background", _background) } };
            Content = new StackPanel
            {
                Margin = new Thickness(18),
                Spacing = 8,
                Children =
                {
                    Labeled("Name", _name), kind, size, _error,
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 6, 0, 0), Children = { ok, cancel } },
                },
            };
            Opened += (_, _) => { _name.Focus(); _name.SelectAll(); };
        }

        private void Accept()
        {
            string name = (_name.Text ?? "").Trim();
            string format = _format.SelectedItem as string ?? "PNG";
            string extension = "." + format.ToLowerInvariant();
            // the extension follows the format: a typed .png/.bmp is taken out first
            if (name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
            string? problem = fire.Projects.ProjectTemplates.CheckName(name);
            int w = (int)(_width.Value ?? 0), h = (int)(_height.Value ?? 0);
            if (problem == null && (w < 1 || h < 1 || w > MaxSide || h > MaxSide)) problem = $"The size must be 1 to {MaxSide} pixels per side.";
            if (problem != null) { _error.Text = problem; _error.IsVisible = true; return; }
            FileName = name + extension;
            ImageWidth = w;
            ImageHeight = h;
            Background = (_background.SelectedItem as string) switch { "White" => 0xFFFFFFFFu, "Black" => 0xFF000000u, _ => format == "BMP" ? 0xFFFFFFFFu : 0u };
            Close(true);
        }
    }
}
