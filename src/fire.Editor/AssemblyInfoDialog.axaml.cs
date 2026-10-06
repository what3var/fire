using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using fire.Compiler.Assembly;
using fire.Runtime;
using fire.Utilities;

namespace fire.Editor
{
    /// <summary>The build settings (execution mode, subsystem, version, icon and the texts of the assembly info) of a script; the dialog edits a copy of the settings:
    /// <see cref="Window.ShowDialog{TResult}(Window)"/> returns true when the user saved.</summary>
    public partial class AssemblyInfoDialog : Window
    {
        public AssemblyInfoDialog()
        {
            InitializeComponent();
            // only digits in the version boxes
            foreach (var box in this.GetVisualDescendants<TextBox>().Where(b => b.Text == null || b.Text.All(char.IsDigit)))
                box.AddHandler(TextInputEvent, (object? _, TextInputEventArgs e) => { if (e.Text != null && e.Text.Any(c => !char.IsDigit(c))) e.Handled = true; }, RoutingStrategies.Tunnel);
            DataContextChanged += (_, _) => ShowSettings();
        }

        private AssemblyInfo? Info => DataContext as AssemblyInfo;

        /// <summary>Radio buttons and the icon from the settings.</summary>
        private void ShowSettings()
        {
            if (Info is not { } info) return;
            ModeDebug.IsChecked = info.ExecutionMode == VmExecutionMode.Debug;
            ModeRelease.IsChecked = info.ExecutionMode == VmExecutionMode.Release;
            ModePerformance.IsChecked = info.ExecutionMode == VmExecutionMode.Performance;
            SubsystemGui.IsChecked = info.Subsystem == SubsystemType.GUI;
            SubsystemConsole.IsChecked = info.Subsystem == SubsystemType.Console;
            ShowIcon(info.IconPath);
        }

        private void ShowIcon(string? path)
        {
            IconPathText.Text = path ?? "";
            try { IconImage.Source = path != null && File.Exists(path) ? new Bitmap(path) : null; }
            catch (Exception) { IconImage.Source = null; }   // an .ico that Avalonia cannot read is still used for the program: only the preview is missing
        }

        private void Save_Click(object? sender, RoutedEventArgs e)
        {
            if (Info is { } info)
            {
                info.ExecutionMode = ModeDebug.IsChecked == true ? VmExecutionMode.Debug : ModePerformance.IsChecked == true ? VmExecutionMode.Performance : VmExecutionMode.Release;
                info.Subsystem = SubsystemGui.IsChecked == true ? SubsystemType.GUI : SubsystemType.Console;
            }
            Close(true);
        }

        private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);

        private async void Load_Click(object? sender, RoutedEventArgs e)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Icon",
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("Icons") { Patterns = new[] { "*.ico" } }, FilePickerFileTypes.All },
            });
            if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
            if (Info is { } info) info.IconPath = path;
            ShowIcon(path);
        }
    }

    internal static class VisualExtras
    {
        /// <summary>All descendants of a type in the logical tree.</summary>
        public static System.Collections.Generic.IEnumerable<T> GetVisualDescendants<T>(this Avalonia.Controls.Control root) where T : Avalonia.Controls.Control
        {
            foreach (var child in Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(root))
                if (child is T match) yield return match;
        }
    }
}
