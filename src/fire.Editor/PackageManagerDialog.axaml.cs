using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using fire.Package.Manager;

namespace fire.Editor
{
    /// <summary>
    /// The package manager (ember) in the editor: search the package sources, install and remove packages (docs/PACKAGES.md). It works on the same global store and sources as the
    /// command line tool `ember`: what is installed here can be imported (`#import "name"`) by every script.
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
            public bool Installed => InstalledVersion.Length > 0;
        }

        private readonly PackageManagerService _service;
        private bool _busy;

        /// <summary>True when something was installed or removed: the open scripts should be checked again.</summary>
        public bool Changed { get; private set; }

        public PackageManagerDialog()
        {
            InitializeComponent();
            PackageManagerService service;
            string? startError = null;
            try { service = new PackageManagerService(); }
            catch (PackageException ex) { startError = ex.Message; service = new PackageManagerService(PackageStore.Default, new List<IPackageSource>()); }
            _service = service;
            Opened += async (_, _) =>
            {
                if (startError != null) await Dialogs.Message(this, startError, "Package Manager");
                await RefreshAsync();
            };
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
                    var found = _service.Find(query, warnings).Select(l => ToRow(l)).ToList();
                    // installed packages that no source offers (installed from a file) are listed too
                    foreach (var p in _service.Store.Installed())
                        if (!found.Any(r => string.Equals(r.Name, p.Name, StringComparison.OrdinalIgnoreCase)) && (query.Length == 0 || p.Name.Contains(query, StringComparison.OrdinalIgnoreCase)))
                            found.Add(new Row { Name = p.Name, Latest = "", InstalledVersion = p.Version, Author = p.Manifest.Author, Description = p.Manifest.Description, Details = DetailsOf(p) });
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

        private Row ToRow(PackageListing l)
        {
            var installed = _service.Store.Find(l.Name);
            return new Row
            {
                Name = l.Name, Latest = l.Latest?.Version ?? "", InstalledVersion = installed?.Version ?? "", Author = l.Author, Description = l.Description,
                Details = $"{l.Name}: versions {string.Join(", ", l.Versions.Select(v => v.Version))}; from {l.Source}" + (installed != null ? $"; imports: {string.Join(", ", installed.Manifest.Imports.Select(i => i.Name))}" : ""),
            };
        }

        private static string DetailsOf(InstalledPackage p) => $"{p.Name} {p.Version} (installed from a file); imports: {string.Join(", ", p.Manifest.Imports.Select(i => i.Name))}";

        private Row? Selected => lvPackages.SelectedItem as Row;

        private void Packages_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            txtDetails.Text = Selected?.Details ?? "";
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            var row = Selected;
            btnInstall.IsEnabled = !_busy && row != null && row.Latest.Length > 0 && row.InstalledVersion != row.Latest;
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
            await RunAsync($"Installing {row.Name}...", () => _service.Install(row.Name));
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
            await RunAsync($"Installing {System.IO.Path.GetFileName(file)}...", () => _service.Install(file));
        }

        private void Close_Click(object? sender, RoutedEventArgs e) => Close();

        private async void Remove_Click(object? sender, RoutedEventArgs e)
        {
            if (Selected is not { } row) return;
            if (await Dialogs.Ask(this, $"Remove the package '{row.Name}'?", "Package Manager", ("Yes", Dialogs.Answer.Yes), ("No", Dialogs.Answer.No)) != Dialogs.Answer.Yes) return;
            await RunAsync($"Removing {row.Name}...", () => _service.Remove(row.Name));
        }

        private async Task RunAsync(string status, Action action)
        {
            if (_busy) return;
            SetBusy(true, status);
            string? error = null;
            try { await Task.Run(action); Changed = true; }
            catch (PackageException ex) { error = ex.Message; }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { error = ex.Message; }
            SetBusy(false, error == null ? "Done." : "Failed.");
            if (error != null) await Dialogs.Message(this, error, "Package Manager");
            await RefreshAsync();
        }
    }
}
