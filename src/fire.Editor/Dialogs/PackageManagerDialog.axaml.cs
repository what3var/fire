using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using fire.Projects;
using fire.Tooling;
namespace fire.Editor
{
    /// <summary>
    /// The package manager (ember) in the editor: search the package sources, install and remove packages (docs/packages/README.md). It runs the program `ember` (tools/ember,
    /// M3: `ember find --json`, `ember list --json`, `ember install`, `ember remove` - <see cref="Ember"/>): the same global store and sources as on the command line; what is
    /// installed here can be imported (`#import "name"`) by every script.
    /// </summary>
    public partial class PackageManagerDialog : Window
    {
        public sealed class Row
        {
            public string Name { get; init; } = "";
            public string Latest { get; init; } = "";
            public string InstalledVersion { get; init; } = "";
            public string Author { get; init; } = "";
            public string Description { get; init; } = "";
            public string Details { get; init; } = "";
            /// <summary>What Install installs: the name, or a spec straight from GitHub (`github:owner/repo[@ref]`).</summary>
            public string Spec { get; init; } = "";
            public bool Installed => InstalledVersion.Length > 0;
        }

        private bool _busy;

        /// <summary>True when something was installed or removed: the open scripts should be checked again.</summary>
        public bool Changed { get; private set; }

        public PackageManagerDialog()
        {
            InitializeComponent();
            Opened += async (_, _) => await RefreshAsync();
        }

        private async void Search_Click(object? sender, RoutedEventArgs e) => await RefreshAsync();

        private async Task RefreshAsync()
        {
            if (_busy) return;
            SetBusy(true, "Searching...");
            string query = txtSearch.Text ?? "";
            var warnings = new List<string>();
            List<Row> rows;
            try
            {
                rows = await Task.Run(() =>
                {
                    var installed = Ember.Installed();
                    // `github:owner/repo[@ref]` in the search field: a row that installs it straight from GitHub (docs/packages/README.md "Straight from GitHub")
                    if (GitHubSpecText(query.Trim()) is { } gh)
                    {
                        var have = installed.FirstOrDefault(p => p.Origin != null && p.Origin.StartsWith("github:" + gh.Repository, StringComparison.OrdinalIgnoreCase));
                        return new List<Row> { new Row { Name = "github:" + gh.Repository, Spec = query.Trim(), Latest = gh.Ref.Length > 0 ? gh.Ref : "newest release", InstalledVersion = have?.Installed ?? "",
                            Description = "Straight from https://github.com/" + gh.Repository, Details = $"Installs github:{gh.Repository} from https://github.com/{gh.Repository}: the .fpk of the release (checked against its checksum), or the package built from the source." } };
                    }
                    var (offered, problems) = Ember.Find(query);
                    warnings.AddRange(problems);
                    var found = offered.Select(l => ToRow(l, installed)).ToList();
                    // installed packages that no source offers (installed from a file) are listed too
                    foreach (var p in installed)
                        if (!found.Any(r => string.Equals(r.Name, p.Name, StringComparison.OrdinalIgnoreCase)) && (query.Length == 0 || p.Name.Contains(query, StringComparison.OrdinalIgnoreCase)))
                            found.Add(new Row { Name = p.Name, Latest = "", InstalledVersion = p.Installed, Author = p.Author, Description = p.Description, Details = DetailsOf(p) });
                    return found.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
                });
            }
            catch (Exception ex)
            {
                SetBusy(false, "");
                await Dialogs.Message(this, ex.Message, "Package Manager");
                return;
            }
            lvPackages.ItemsSource = rows;
            SetBusy(false, warnings.Count > 0 ? "Some sources could not be read: " + warnings[0] : $"{rows.Count} package(s)");
            UpdateButtons();
        }

        private static Row ToRow(EmberPackage l, List<EmberPackage> installed)
        {
            var have = installed.FirstOrDefault(p => string.Equals(p.Name, l.Name, StringComparison.OrdinalIgnoreCase));
            return new Row
            {
                Name = l.Name, Latest = l.Latest, InstalledVersion = have?.Installed ?? "", Author = l.Author, Description = l.Description,
                Details = $"{l.Name}: versions {string.Join(", ", l.Versions)}; from {l.Source}" + (have != null ? $"; imports: {string.Join(", ", have.Imports)}" : ""),
            };
        }

        private static string DetailsOf(EmberPackage p)
        {
            string origin = p.Origin != null ? $"from {p.Origin}, checked: {p.Verified}" : "installed from a file";
            return $"{p.Name} {p.Installed} ({origin}); imports: {string.Join(", ", p.Imports)}";
        }

        /// <summary>`github:owner/repo[@ref][#name]`: the repository and the reference (null: no such spec).</summary>
        private static (string Repository, string Ref)? GitHubSpecText(string text)
        {
            if (!text.StartsWith("github:", StringComparison.OrdinalIgnoreCase)) return null;
            string rest = text.Substring(7);
            int hash = rest.IndexOf('#');
            if (hash >= 0) rest = rest.Substring(0, hash);
            int at = rest.IndexOf('@');
            string repo = at >= 0 ? rest.Substring(0, at) : rest, reference = at >= 0 ? rest.Substring(at + 1) : "";
            var parts = repo.Split('/');
            if (parts.Length != 2 || parts.Any(p => p.Length == 0)) return null;
            return (repo, reference);
        }

        private Row? Selected => lvPackages.SelectedItem as Row;

        private void Packages_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            txtDetails.Text = Selected?.Details ?? "";
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            var row = Selected;
            btnInstall.IsEnabled = !_busy && row != null && row.Latest.Length > 0 && (row.Spec.Length > 0 || row.InstalledVersion != row.Latest);
            btnInstall.Content = row != null && row.Installed ? "Update" : "Install";
            btnRemove.IsEnabled = !_busy && row != null && row.Installed;
        }

        private void SetBusy(bool busy, string status)
        {
            _busy = busy;
            txtStatus.Text = status;
            Cursor = busy ? new Cursor(StandardCursorType.Wait) : null;
            UpdateButtons();
        }

        private async void Install_Click(object? sender, RoutedEventArgs e)
        {
            if (Selected is not { } row) return;
            await RunAsync($"Installing {row.Name}...", () => Ember.Install(row.Spec.Length > 0 ? row.Spec : row.Name));
        }

        private async void InstallFile_Click(object? sender, RoutedEventArgs e)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Install a package file",
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("Package files") { Patterns = new[] { "*.fpk" } }, FilePickerFileTypes.All },
            });
            if (files.Count == 0 || files[0].TryGetLocalPath() is not { } file) return;
            try
            {
                var manifest = Fpk.ReadManifest(file);
                if (PackageStore.Default.Find(manifest.Name) is { } installed && installed.Version == manifest.Version
                    && await Dialogs.Ask(this, $"{manifest.Name} {manifest.Version} is installed already.\n\nReplace it with this package?", "Package Manager",
                        ("Replace", Dialogs.Answer.Yes), ("Cancel", Dialogs.Answer.No)) != Dialogs.Answer.Yes)
                    return;
            }
            catch (PackageException ex) { await Dialogs.Message(this, ex.Message, "Package Manager"); return; }
            await RunAsync($"Installing {System.IO.Path.GetFileName(file)}...", () => Ember.Install(file));
        }

        private void Close_Click(object? sender, RoutedEventArgs e) => Close();

        private async void Remove_Click(object? sender, RoutedEventArgs e)
        {
            if (Selected is not { } row) return;
            if (await Dialogs.Ask(this, $"Remove the package '{row.Name}'?", "Package Manager", ("Yes", Dialogs.Answer.Yes), ("No", Dialogs.Answer.No)) != Dialogs.Answer.Yes) return;
            await RunAsync($"Removing {row.Name}...", () => Ember.Remove(row.Name));
        }

        private async Task RunAsync(string status, Action action)
        {
            if (_busy) return;
            SetBusy(true, status);
            string? error = null;
            try { await Task.Run(action); Changed = true; }
            catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { error = ex.Message; }
            SetBusy(false, error == null ? "Done." : "Failed.");
            if (error != null) await Dialogs.Message(this, error, "Package Manager");
            await RefreshAsync();
        }
    }
}
