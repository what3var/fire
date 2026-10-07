using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using fire.Compiler;
using fire.Projects;

namespace fire.Editor
{
    /// <summary>
    /// "Pack as Package" (docs/PROJECTS.md): what goes into the manifest of the package - everything comes from the project's settings and is written back to them: the version (three numbers; it
    /// can be raised afterwards for the next one), description, author, license - and the folder the `.fpk` is written to. The import name is the project's (Project properties).
    /// </summary>
    internal sealed class PackDialog : Window
    {
        private readonly LoadedProject _project;
        private readonly TextBox _version = new(), _description = new(), _author = new(), _license = new(), _folder = new();
        private readonly CheckBox _bump = new() { Content = "Raise the patch number afterwards (ready for the next version)", IsChecked = true };
        private readonly TextBlock _info = new() { Foreground = EditorTheme.TextDim, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _error = new() { Foreground = EditorTheme.ErrorMark, TextWrapping = TextWrapping.Wrap, IsVisible = false };

        public string Version { get; private set; } = "";
        public string? Description { get; private set; }
        public string? Author { get; private set; }
        public string? License { get; private set; }
        public string Folder { get; private set; } = "";
        public bool BumpAfterwards { get; private set; }

        public PackDialog(LoadedProject project, ProjectSettings effective, string folder)
        {
            _project = project;
            Title = $"Pack {project.Name} as a package";
            Width = 520; SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            CanResize = false;
            ShowInTaskbar = false;

            _version.Text = ProjectBuilder.PackageVersion(effective.Version);
            _description.Text = effective.Description; _author.Text = effective.Author; _license.Text = effective.License;
            _folder.Text = folder;
            _version.TextChanged += (_, _) => UpdateInfo();
            _folder.TextChanged += (_, _) => UpdateInfo();

            var browse = new Button { Content = "Browse...", Margin = new Thickness(6, 0, 0, 0) };
            browse.Click += async (_, _) => await Browse();
            var folderRow = new DockPanel();
            DockPanel.SetDock(browse, Avalonia.Controls.Dock.Right);
            folderRow.Children.Add(browse);
            folderRow.Children.Add(_folder);

            var ok = new Button { Content = "Pack", MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
            ok.Classes.Add("accent");
            ok.Click += (_, _) => Accept();
            var cancel = new Button { Content = "Cancel", MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
            cancel.Click += (_, _) => Close(false);

            var panel = new StackPanel { Margin = new Thickness(16), Spacing = 8 };
            panel.Children.Add(Labeled("Version (major.minor.patch)", _version));
            panel.Children.Add(Labeled("Description", _description));
            panel.Children.Add(Labeled("Author", _author));
            panel.Children.Add(Labeled("License", _license));
            panel.Children.Add(Labeled("Folder of the package", folderRow));
            panel.Children.Add(_bump);
            panel.Children.Add(_info);
            panel.Children.Add(_error);
            panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { ok, cancel } });
            Content = panel;
            UpdateInfo();
        }

        private static Control Labeled(string label, Control control) =>
            new StackPanel { Spacing = 2, Children = { new TextBlock { Text = label, Foreground = EditorTheme.TextDim }, control } };

        private void UpdateInfo()
        {
            string version = (_version.Text ?? "").Trim();
            string folder = (_folder.Text ?? "").Trim();
            if (!ProjectBuilder.IsPackageVersion(version) || folder.Length == 0) { _info.Text = ""; return; }
            string file = Path.Combine(folder, $"{_project.Name}-{version}.fpk");
            _info.Text = $"{file}  -  programs write #import \"{_project.Project.ImportName}\"" + (File.Exists(file) ? "\nThis version exists already: it is replaced." : "");
        }

        private async Task Browse()
        {
            string start = (_folder.Text ?? "").Trim();
            var startFolder = Directory.Exists(start) ? await StorageProvider.TryGetFolderFromPathAsync(start) : await StorageProvider.TryGetFolderFromPathAsync(_project.Directory);
            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Folder for the package", SuggestedStartLocation = startFolder });
            if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } path) _folder.Text = path;
        }

        private static string? NullIfEmpty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

        private void Accept()
        {
            string version = (_version.Text ?? "").Trim();
            if (!ProjectBuilder.IsPackageVersion(version)) { _error.Text = "The version has three numbers: 1.2.3"; _error.IsVisible = true; return; }
            if (string.IsNullOrWhiteSpace(_folder.Text)) { _error.Text = "Choose a folder for the package."; _error.IsVisible = true; return; }
            Version = version; Description = NullIfEmpty(_description.Text); Author = NullIfEmpty(_author.Text); License = NullIfEmpty(_license.Text);
            Folder = _folder.Text!.Trim(); BumpAfterwards = _bump.IsChecked == true;
            Close(true);
        }
    }
}
