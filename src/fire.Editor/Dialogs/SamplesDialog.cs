using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using fire.Projects;

namespace fire.Editor
{
    /// <summary>
    /// Help > Samples: the sample projects that ship with fire in a list (title and description). "Open" copies the chosen sample into a folder of the user's choice (proposed: `$HOME/spark/samples/&lt;name&gt;`)
    /// and the editor opens the copy - the shipped files stay as they are. The result is in <see cref="Sample"/> and <see cref="Location"/> (the folder of the copy).
    /// </summary>
    internal sealed class SamplesDialog : Window
    {
        private readonly ListBox _list = new() { MinHeight = 220, HorizontalAlignment = HorizontalAlignment.Stretch };
        private readonly TextBox _location = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
        private readonly TextBlock _error = new() { Foreground = EditorTheme.ErrorMark, TextWrapping = TextWrapping.Wrap, IsVisible = false };

        public FireSample? Sample { get; private set; }
        public string Location { get; private set; } = "";

        public SamplesDialog(IReadOnlyList<FireSample> samples)
        {
            Title = "Samples";
            Width = 560; Height = 480; MinWidth = 420; MinHeight = 340;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            _list.ItemsSource = samples;
            _list.ItemTemplate = new FuncDataTemplate<FireSample>((s, _) => s == null ? new TextBlock() : new StackPanel
            {
                Margin = new Thickness(4),
                Children =
                {
                    new TextBlock { Text = s.Title, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = s.Description, Foreground = EditorTheme.TextDim, FontSize = 11, TextWrapping = TextWrapping.Wrap },
                },
            });
            _list.SelectedItem = samples.FirstOrDefault();
            _list.SelectionChanged += (_, _) => ProposeLocation();
            _list.DoubleTapped += (_, _) => Accept();
            ProposeLocation();

            var browse = new Button { Content = "Browse..." };
            browse.Click += async (_, _) => await Browse();
            var locationRow = new DockPanel { Children = { browse, _location } };
            DockPanel.SetDock(browse, Avalonia.Controls.Dock.Right);
            browse.Margin = new Thickness(6, 0, 0, 0);

            var ok = new Button { Content = "Open", MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
            ok.Classes.Add("accent");
            ok.Click += (_, _) => Accept();
            var cancel = new Button { Content = "Cancel", MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
            cancel.Click += (_, _) => Close(false);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 10, 0, 0), Children = { ok, cancel } };

            var bottom = new StackPanel
            {
                Spacing = 4,
                Children = { new TextBlock { Text = "A copy of the sample is made in this folder and opened:", Foreground = EditorTheme.TextDim }, locationRow, _error, buttons },
            };
            var root = new DockPanel { Margin = new Thickness(12) };
            DockPanel.SetDock(bottom, Avalonia.Controls.Dock.Bottom);
            root.Children.Add(bottom);
            root.Children.Add(_list);
            Content = root;
        }

        private void ProposeLocation()
        {
            if (_list.SelectedItem is FireSample s)
                _location.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "spark", "samples", s.Name);
        }

        private async Task Browse()
        {
            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Folder for the copy of the sample" });
            if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } path || _list.SelectedItem is not FireSample s) return;
            _location.Text = Path.Combine(path, s.Name);
        }

        private void Accept()
        {
            string location = (_location.Text ?? "").Trim();
            if (_list.SelectedItem is not FireSample sample) { Fail("Choose a sample."); return; }
            if (location.Length == 0) { Fail("Enter the folder for the copy."); return; }
            Sample = sample;
            Location = location;
            Close(true);
        }

        private void Fail(string message) { _error.Text = message; _error.IsVisible = true; }
    }
}
