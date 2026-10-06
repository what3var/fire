using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace fire.Editor
{
    // File menu: new, open, save, close, Markdown links, help.
    public partial class MainWindow
    {
        private static readonly FilePickerFileType ScriptFiles = new("fire files") { Patterns = new[] { "*.script", "*.fi", "*.fic" } };
        private static readonly FilePickerFileType MarkdownFiles = new("Markdown") { Patterns = new[] { "*.md", "*.markdown" } };
        private static readonly FilePickerFileType PacketLogFiles = new("Packet logs") { Patterns = new[] { "*.fplog" } };
        private static readonly FilePickerFileType UiMarkupFiles = new("UI markup") { Patterns = new[] { "*.fxml" } };
        private static readonly FilePickerFileType AllDocuments = new("All documents") { Patterns = new[] { "*.script", "*.fi", "*.fic", "*.md", "*.markdown", "*.fplog", "*.fxml" } };

        private static string SafeFileName(string name) =>
            string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '-' : c));

        private async Task<string[]> PickFiles(string title, params FilePickerFileType[] types)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = true,
                FileTypeFilter = types.Append(FilePickerFileTypes.All).ToList(),
            });
            return files.Select(f => f.TryGetLocalPath()).Where(p => p != null).Select(p => p!).ToArray();
        }

        /// <summary>Asks for a file name to write; null if the dialog was cancelled. The default extension is added when the name has none.</summary>
        private async Task<string?> PickSavePath(string title, string suggested, string defaultExtension, params FilePickerFileType[] types)
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = title,
                SuggestedFileName = suggested,
                DefaultExtension = defaultExtension,
                FileTypeChoices = types.Append(FilePickerFileTypes.All).ToList(),
                ShowOverwritePrompt = true,
            });
            string? path = file?.TryGetLocalPath();
            if (path != null && Path.GetExtension(path).Length == 0 && defaultExtension.Length > 0) path += "." + defaultExtension.TrimStart('.');
            return path;
        }

        private void New_Click(object? sender, RoutedEventArgs e) => NewScript("");

        private void NewMarkdown_Click(object? sender, RoutedEventArgs e) => NewMarkdown("");

        private const string UiMarkupTemplate = """
            <Window class="MainWindow" title="My window" width="400" height="300">
              <Stack x="10" y="10" width="380" height="280" spacing="6">
                <Label text="Hello"/>
                <TextBox name="nameBox" width="200" text="{Binding Name, Mode=TwoWay}"/>
                <Button name="ok" text="OK" onClick="Ok"/>
              </Stack>
            </Window>

            """;

        private void NewUiMarkup_Click(object? sender, RoutedEventArgs e) => CreateDocument(DocumentKind.UiMarkup, UiMarkupTemplate, null);

        private void OpenReadOnly_Click(object? sender, RoutedEventArgs e) => OpenMarkdownDialog(MarkdownViewMode.ReadOnly);
        private void OpenViewer_Click(object? sender, RoutedEventArgs e) => OpenMarkdownDialog(MarkdownViewMode.Viewer);

        private async void OpenMarkdownDialog(MarkdownViewMode mode)
        {
            var files = await PickFiles(mode == MarkdownViewMode.Viewer ? "Open Markdown in Viewer" : "Open Markdown Read-Only", MarkdownFiles);
            foreach (var file in files) OpenFile(file, mode);
        }

        private void HelpFirstSteps_Click(object? sender, RoutedEventArgs e) => OpenHelp("First Steps.md");

        private void HelpEmbedding_Click(object? sender, RoutedEventArgs e) => OpenHelp("Embedding.md");

        /// <summary>Opens a page of the Help folder (next to the executable) as a viewer.</summary>
        private void OpenHelp(string fileName)
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Help", fileName);
            if (!File.Exists(path))
            {
                _ = Dialogs.Message(this, $"The help file was not found:\n{path}", "Help");
                return;
            }
            OpenFile(path, MarkdownViewMode.Viewer);
        }

        // -----------------------------------------------------------
        // Links in Markdown documents
        // -----------------------------------------------------------

        /// <summary>A link to a local file was clicked in a Markdown preview. In a read-only document or viewer a plain click loads a Markdown target into the same tab (again
        /// read-only/viewer, with back/forward history); Ctrl+click opens it in a new tab with the same mode. In an editable document the file always opens in a new tab (the
        /// text there may be unsaved). Other file types (scripts) open normally in a new tab.</summary>
        private void HandleMarkdownLink(OpenDocument source, string path, string? anchor, bool newTab)
        {
            if (source.Markdown is not { } md) return;
            bool targetIsMarkdown = KindOfPath(path) == DocumentKind.Markdown;

            if (md.IsReadOnly && targetIsMarkdown && !newTab)
            {
                NavigateMarkdown(source, path, anchor, recordHistory: true);
                return;
            }

            var target = OpenFile(path, targetIsMarkdown ? md.Mode : MarkdownViewMode.Edit);
            if (target?.Markdown is { } targetView) targetView.ScrollToAnchor(anchor);
        }

        /// <summary>Loads `path` into the (read-only) Markdown tab `doc`, replacing what it showed.</summary>
        private void NavigateMarkdown(OpenDocument doc, string path, string? anchor, bool recordHistory)
        {
            if (doc.Markdown is not { } md) return;
            string full = Path.GetFullPath(path);
            string text;
            try { text = File.ReadAllText(full); }
            catch (Exception ex)
            {
                _ = Dialogs.Message(this, ex.Message, "Open failed");
                return;
            }

            if (recordHistory)
            {
                if (doc.History.Count == 0 && md.FilePath != null) doc.History.Add(md.FilePath);
                if (doc.History.Count > 0) doc.History.RemoveRange(doc.HistoryIndex + 1, doc.History.Count - doc.HistoryIndex - 1);
                doc.History.Add(full);
                doc.HistoryIndex = doc.History.Count - 1;
            }

            md.ResetTo(text, full);
            md.ScrollToAnchor(anchor);
            UpdateTitle(doc);
            UpdateStatus($"Opened: {full}");
        }

        /// <summary>Back (-1) or forward (+1) through the files a read-only Markdown tab has shown.</summary>
        private void NavigateHistory(OpenDocument doc, int delta)
        {
            int index = doc.HistoryIndex + delta;
            if (index < 0 || index >= doc.History.Count) return;
            doc.HistoryIndex = index;
            NavigateMarkdown(doc, doc.History[index], null, recordHistory: false);
        }

        private async void Open_Click(object? sender, RoutedEventArgs e)
        {
            var files = await PickFiles("Open", AllDocuments, ScriptFiles, MarkdownFiles, UiMarkupFiles, PacketLogFiles);
            foreach (var file in files) OpenFile(file);
        }

        private async void Save_Click(object? sender, RoutedEventArgs e)
        {
            if (ActiveDocument is { } doc) await Save(doc);
        }

        private async void SaveAs_Click(object? sender, RoutedEventArgs e)
        {
            if (ActiveDocument is { } doc) await SaveAs(doc);
        }

        private async void SaveAll_Click(object? sender, RoutedEventArgs e)
        {
            foreach (var doc in _documents.Where(d => d.View.IsModified && !d.View.IsReadOnly).ToList())
                if (!await Save(doc)) return;
        }

        private void CloseDocument_Click(object? sender, RoutedEventArgs e)
        {
            if (ActiveDocument is { } doc) _factory.CloseDockable(doc.Layout);
        }

        /// <summary>Opens a file in a new tab (script or Markdown by extension) - if it is open already, only switches there.</summary>
        private OpenDocument? OpenFile(string path, MarkdownViewMode mode = MarkdownViewMode.Edit)
        {
            string full = Path.GetFullPath(path);
            var existing = _documents.FirstOrDefault(d => d.View.FilePath != null &&
                string.Equals(Path.GetFullPath(d.View.FilePath), full, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                Activate(existing);
                return existing;
            }

            string text;
            try { text = File.ReadAllText(full); }
            catch (Exception ex)
            {
                _ = Dialogs.Message(this, ex.Message, "Open failed");
                return null;
            }

            // An untouched, empty "Untitled" document (e.g. the welcome script) is replaced by it.
            var pristine = _documents.Count == 1 && _documents[0].Kind != DocumentKind.PacketLog
                && _documents[0].View.FilePath == null && !_documents[0].View.IsModified
                ? _documents[0] : null;

            var doc = CreateDocument(KindOfPath(full), text, full, mode: mode);
            if (pristine != null) _factory.CloseDockable(pristine.Layout);
            UpdateStatus($"Opened: {full}");
            return doc;
        }

        /// <summary>Asks about unsaved changes (save/discard/cancel). false = cancel the closing.</summary>
        private async Task<bool> ConfirmClose(OpenDocument doc)
        {
            if (!doc.View.IsModified) return true;
            Activate(doc);
            var answer = await Dialogs.Ask(this, $"Save changes to \"{doc.DisplayName}\"?", "fire Editor",
                ("Yes", Dialogs.Answer.Yes), ("No", Dialogs.Answer.No), ("Cancel", Dialogs.Answer.Cancel));
            return answer switch
            {
                Dialogs.Answer.Yes => await Save(doc),
                Dialogs.Answer.No => true,
                _ => false,
            };
        }

        /// <summary>Saves a document (without a path: "Save as"). false = not saved (cancelled/error).</summary>
        private Task<bool> Save(OpenDocument doc)
        {
            if (doc.View.IsReadOnly) return Task.FromResult(false);
            return doc.View.FilePath == null ? SaveAs(doc) : WriteDocument(doc, doc.View.FilePath);
        }

        private async Task<bool> SaveAs(OpenDocument doc)
        {
            if (doc.View.IsReadOnly) return false;
            string suggested = doc.View.FilePath != null ? Path.GetFileName(doc.View.FilePath) : doc.Kind == DocumentKind.PacketLog ? SafeFileName(doc.DisplayName) : "";
            string? path = doc.Kind switch
            {
                DocumentKind.Markdown => await PickSavePath("Save", suggested, "md", MarkdownFiles),
                DocumentKind.UiMarkup => await PickSavePath("Save", suggested, "fxml", UiMarkupFiles),
                DocumentKind.PacketLog => await PickSavePath("Save", suggested, fire.Device.Manager.DeviceManager.PacketLog.FileExtension.TrimStart('.'), PacketLogFiles),
                _ => await PickSavePath("Save", suggested, "script", ScriptFiles),
            };
            return path != null && await WriteDocument(doc, path);
        }

        private async Task<bool> WriteDocument(OpenDocument doc, string path)
        {
            try { File.WriteAllText(path, doc.View.GetText()); }
            catch (Exception ex)
            {
                await Dialogs.Message(this, ex.Message, "Save failed");
                return false;
            }
            doc.View.FilePath = path;
            doc.View.MarkSaved();
            UpdateTitle(doc);
            UpdateStatus($"Saved: {path}");
            return true;
        }
    }
}
