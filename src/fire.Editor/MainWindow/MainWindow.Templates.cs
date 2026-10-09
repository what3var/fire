using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Interactivity;
using fire.Projects;

namespace fire.Editor
{
    public partial class MainWindow
    {
        /// <summary>File > Templates > Open Template...: choose a template (the flat list of all) and edit its files - in the plain text editor, tied to no project, so that the placeholders are no errors.</summary>
        private async void OpenTemplate_Click(object? sender, RoutedEventArgs e)
        {
            var dialog = new TemplateDialog(TemplateDialogMode.Pick, TemplateCatalog.Load(), title: "Open Template", button: "Open");
            if (await dialog.ShowDialog<bool?>(this) != true || dialog.Template == null) return;
            await OpenTemplate(dialog.Template);
        }

        /// <summary>File > Templates > Copy Template to My Templates...: a copy of any template (one that ships with fire, or from a package) in the folder of the user, to change it.</summary>
        private async void CopyTemplate_Click(object? sender, RoutedEventArgs e)
        {
            var dialog = new TemplateDialog(TemplateDialogMode.Pick, TemplateCatalog.Load(), title: "Copy Template to My Templates", button: "Copy and Open");
            if (await dialog.ShowDialog<bool?>(this) != true || dialog.Template == null) return;
            var copy = await CopyToUserTemplates(dialog.Template);
            if (copy != null) await OpenTemplate(copy, askToCopy: false);
        }

        /// <summary>File > Templates > Open My Templates Folder: `~/spark/templates` with `Code` and `Project` in it (made if it is not there).</summary>
        private void OpenTemplatesFolder_Click(object? sender, RoutedEventArgs e)
        {
            string root = TemplateCatalog.UserRoot;
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "Code"));
                Directory.CreateDirectory(Path.Combine(root, "Project"));
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(root) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                _ = Dialogs.Message(this, ex.Message, "Templates");
            }
        }

        /// <summary>Opens the files of a template (and its `template.json`) in plain text editors. A template of the program folder or of a package is not the user's to change there: it is offered a copy first.</summary>
        private async Task OpenTemplate(FireTemplate template, bool askToCopy = true)
        {
            if (askToCopy && !IsUserTemplate(template))
            {
                var answer = await Dialogs.Ask(this,
                    $"'{template.Title}' is a template of {(template.IsFromPackage ? "the package " + template.PackageName : "the program")}: it is in\n{template.Directory}\nand your changes there may not be allowed or may be lost with the next update.\n\nMake a copy in your own templates folder first?",
                    "Open Template", ("Copy and Open", Dialogs.Answer.Yes), ("Open Here", Dialogs.Answer.No), ("Cancel", Dialogs.Answer.Cancel));
                if (answer == Dialogs.Answer.Cancel) return;
                if (answer == Dialogs.Answer.Yes)
                {
                    var copy = await CopyToUserTemplates(template);
                    if (copy == null) return;
                    template = copy;
                }
            }
            var files = template.Files.Select(f => Path.Combine(template.Directory, f.Replace('/', Path.DirectorySeparatorChar))).ToList();
            string description = Path.Combine(template.Directory, TemplateDescription.FileName);
            if (File.Exists(description)) files.Add(description);
            foreach (var file in files) OpenFile(file, forceKind: DocumentKind.Text);
            UpdateStatus($"Template {template.Title}: {files.Count} file{(files.Count == 1 ? "" : "s")} open as text (placeholders like $name$ are replaced when a file is made from it).");
        }

        private static bool IsUserTemplate(FireTemplate template) =>
            Path.GetFullPath(template.Directory).StartsWith(Path.GetFullPath(TemplateCatalog.UserRoot) + Path.DirectorySeparatorChar, ProjectFiles.PathComparison) && !template.IsFromPackage;

        /// <summary>Copies the folder of a template to `~/spark/templates/{Code|Project}/{title}` (a template of a package becomes a template of its own, "(copy)" is added to the title so that the two can be told apart).
        /// Returns the copy as it is found there, or null when it could not be made.</summary>
        private async Task<FireTemplate?> CopyToUserTemplates(FireTemplate template)
        {
            string scopeFolder = Path.Combine(TemplateCatalog.UserRoot, template.Scope == TemplateScope.Code ? "Code" : "Project");
            string name = string.Concat(template.Title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c));
            string target = Path.Combine(scopeFolder, name);
            if (Directory.Exists(target))
            {
                await Dialogs.Message(this, $"'{target}' exists already: open it from the list of templates, or take it away first.", "Copy Template");
                return null;
            }
            try
            {
                CopyFolder(template.Directory, target);
                var description = TemplateDescription.Load(target);
                description.Title = template.Title + " (copy)";
                description.Description ??= template.Description;
                description.Save(target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Dialogs.Message(this, ex.Message, "Copy Template");
                return null;
            }
            return TemplateCatalog.Load(builtinRoot: Path.Combine(Path.GetTempPath(), "fire-no-builtin-" + Guid.NewGuid().ToString("N")), includeInstalledPackages: false)
                .All.FirstOrDefault(t => string.Equals(Path.GetFullPath(t.Directory), Path.GetFullPath(target), ProjectFiles.PathComparison));
        }

        private static void CopyFolder(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
            foreach (var dir in Directory.GetDirectories(from)) CopyFolder(dir, Path.Combine(to, Path.GetFileName(dir)));
        }
    }
}
