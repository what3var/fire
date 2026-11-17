using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using fire.Git;
using fire.Projects;

namespace fire.Editor
{
    // Files that change under the editor (docs/tools/git.md, "Files that change on disk"): after a git command of the editor (pull, switch of the branch, discard) everything is compared; a file
    // watcher on the folders of the solution, the projects and the open documents reports what other programs do (git outside of the editor, another editor), bundled (ChangeBatcher).
    // What to do is decided by ReloadPlanner (Git/WorkingTreeSync.cs); here it is done: the solution or a project loaded again, the folders read again, documents read again or asked about.
    public partial class MainWindow
    {
        private readonly DiskBaselines _baselines = new();
        private readonly ChangeBatcher _diskChanges = new(TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(3));
        private readonly List<FileSystemWatcher> _watchers = new();
        private List<string> _watchedRoots = new();
        private DispatcherTimer? _diskTimer;
        private bool _syncing, _syncAgain;
        private DateTime _diskChecked = DateTime.MinValue;

        private void InitReload()
        {
            _workspace.Changed += () => Dispatcher.UIThread.Post(() => { RememberWorkspaceFiles(); UpdateWatchers(); });
            _diskTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) => TakeDiskChanges());
            _diskTimer.Start();
            // a folder on a network drive may not report its changes: they are compared again when the window gets the focus
            Activated += (_, _) =>
            {
                if ((DateTime.UtcNow - _diskChecked).TotalSeconds < 2) return;
                var all = new ChangeBatch();
                foreach (var path in _baselines.Paths) all.Paths.Add(path);
                _ = SyncWithDisk(all, refreshGit: false);   // (the git state is read on activation anyway, MainWindow.Git.cs)
            };
            Closed += (_, _) => { foreach (var w in _watchers) w.Dispose(); _watchers.Clear(); };
        }

        /// <summary>The solution and the project files are as on disk now (they were just loaded or saved by the editor).</summary>
        private void RememberWorkspaceFiles()
        {
            if (_workspace.FilePath is { } file) _baselines.Remember(file);
            foreach (var p in _workspace.AllProjects) _baselines.Remember(p.FilePath);
        }

        /// <summary>A document's file is as on disk now (it was just opened, saved or read again).</summary>
        private void RememberDocumentFile(string? path)
        {
            if (path != null) _baselines.Remember(path);
            UpdateWatchers();
        }

        // ---- the watchers -------------------------------------------------------------------------------------------------------------------

        /// <summary>Watches the folders of the solution, of the projects and of the open documents (as few watchers as possible), and the `.git` folder of the repository.</summary>
        private void UpdateWatchers()
        {
            var folders = new List<string>();
            if (_workspace.FilePath is { } ws) folders.Add(Path.GetDirectoryName(Path.GetFullPath(ws))!);
            folders.AddRange(_workspace.AllProjects.Select(p => p.Directory));
            folders.AddRange(_documents.Select(FullPathOf).Where(p => p != null).Select(p => Path.GetDirectoryName(p!)!));
            var roots = WatchFilter.Roots(folders.Where(Directory.Exists)).ToList();
            string? gitDir = _gitRoot != null && Directory.Exists(Path.Combine(_gitRoot, ".git")) ? Path.Combine(_gitRoot, ".git") : null;
            var wanted = roots.Select(r => "r|" + r).ToList();
            if (gitDir != null && !roots.Any(r => WatchFilter.IsInside(gitDir, r))) wanted.Add("g|" + gitDir);
            if (wanted.SequenceEqual(_watchedRoots)) return;
            foreach (var w in _watchers) w.Dispose();
            _watchers.Clear();
            _watchedRoots = wanted;
            foreach (var entry in wanted)
            {
                try
                {
                    bool gitOnly = entry.StartsWith("g|");
                    var watcher = new FileSystemWatcher(entry.Substring(2))
                    {
                        IncludeSubdirectories = !gitOnly,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                        InternalBufferSize = 64 * 1024,
                    };
                    watcher.Changed += (_, e) => OnDiskEvent(e.FullPath, false);
                    watcher.Created += (_, e) => OnDiskEvent(e.FullPath, true);
                    watcher.Deleted += (_, e) => OnDiskEvent(e.FullPath, true);
                    watcher.Renamed += (_, e) => { OnDiskEvent(e.OldFullPath, true); OnDiskEvent(e.FullPath, true); };
                    watcher.Error += (_, _) => _diskChanges.AddOverflow(DateTime.UtcNow);
                    watcher.EnableRaisingEvents = true;
                    _watchers.Add(watcher);
                }
                catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
                {
                    System.Diagnostics.Debug.WriteLine(ex);   // (no watcher: the focus of the window and the git commands still compare)
                }
            }
        }

        /// <summary>From the thread of a watcher.</summary>
        private void OnDiskEvent(string path, bool existenceChanged)
        {
            switch (WatchFilter.Classify(path))
            {
                case WatchFilter.Kind.WorkingTree: _diskChanges.Add(path, existenceChanged, DateTime.UtcNow); break;
                case WatchFilter.Kind.GitState: _diskChanges.AddGitState(DateTime.UtcNow); break;
            }
        }

        private void TakeDiskChanges()
        {
            if (_syncing || !_diskChanges.TryTake(DateTime.UtcNow, out var batch)) return;
            _ = SyncWithDisk(batch);
        }

        // ---- comparing and loading again ------------------------------------------------------------------------------------------------------

        /// <summary>
        /// Compares the files the editor holds with the disk and loads what changed. `batch` null: after a git command - everything, and the folders of the projects are read again;
        /// else what the watcher reported. The git state is read again afterwards (`refreshGit`), or when something had to be loaded again.
        /// </summary>
        private async Task SyncWithDisk(ChangeBatch? batch, bool refreshGit = true)
        {
            if (_syncing) { _syncAgain = true; return; }
            _syncing = true;
            _diskChecked = DateTime.UtcNow;
            try
            {
                if (batch == null) _diskChanges.Clear();   // (everything is compared now)
                var docs = _documents.Where(d => FullPathOf(d) != null).ToList();
                var plan = ReloadPlanner.Plan(_baselines, _workspace.FilePath, _workspace.AllProjects.Select(p => p.FilePath), _workspace.AllProjects.Select(p => p.Directory),
                    docs.Select(d => new OpenFileInfo(FullPathOf(d)!, d.View.IsModified, d.DeletedOnDisk)), batch);
                await ApplyReloadPlan(plan, docs);
                if (refreshGit || !plan.IsEmpty) RefreshGit();
            }
            finally
            {
                _syncing = false;
                if (_syncAgain) { _syncAgain = false; Dispatcher.UIThread.Post(() => _ = SyncWithDisk(new ChangeBatch { Overflow = true }), DispatcherPriority.Background); }
            }
        }

        private async Task ApplyReloadPlan(ReloadPlan plan, List<OpenDocument> docs)
        {
            if (plan.IsEmpty) return;
            var messages = new List<string>();
            if (plan.CloseWorkspace)
            {
                string name = _workspace.Name;
                _workspace.Close();
                messages.Add($"'{name}' is no longer on disk: it was closed");
            }
            else if (plan.ReloadWorkspace)
            {
                try { _workspace.Reload(); messages.Add($"{_workspace.Name} was loaded again"); }
                catch (Exception ex) when (ex is ProjectException or IOException or UnauthorizedAccessException)
                {
                    RememberWorkspaceFiles();   // (not asked again until it changes once more)
                    await Dialogs.Message(this, $"The solution changed on disk and could not be loaded again:\n{ex.Message}", "Reload");
                }
            }
            else
            {
                foreach (var project in plan.ProjectsToReload)
                {
                    try { if (_workspace.ReloadProject(project)) messages.Add($"{Path.GetFileNameWithoutExtension(project)} was loaded again"); }
                    catch (Exception ex) when (ex is ProjectException or IOException or UnauthorizedAccessException)
                    {
                        _baselines.Remember(project);
                        await Dialogs.Message(this, $"The project {Path.GetFileName(project)} changed on disk and could not be loaded again:\n{ex.Message}", "Reload");
                    }
                }
                if (plan.RefreshFolders && _workspace.IsOpen) _workspace.Refresh();   // (also the projects that were not loaded again: a file made or deleted in them)
            }

            int reloaded = 0;
            foreach (var (path, action) in plan.Documents)
            {
                var doc = docs.FirstOrDefault(d => FullPathOf(d) is { } p && ProjectFiles.PathComparer.Equals(p, path));
                if (doc == null || !_documents.Contains(doc)) continue;
                switch (action)
                {
                    case DocumentReload.Silently:
                        if (ReloadFromDisk(doc)) reloaded++;
                        break;
                    case DocumentReload.MarkDeleted:
                        doc.DeletedOnDisk = true;
                        _baselines.Remember(path);   // (as missing: when it comes back, it is read again)
                        UpdateTitle(doc);
                        messages.Add($"{doc.DisplayName} was deleted on disk");
                        break;
                    case DocumentReload.Ask:
                        await AskAboutChangedDocument(doc);
                        break;
                }
            }
            if (reloaded > 0) messages.Add(reloaded == 1 ? "1 document was read again" : $"{reloaded} documents were read again");
            if (messages.Count > 0) UpdateStatus("Changed on disk: " + string.Join("; ", messages) + ".");
        }

        /// <summary>Reads a document again from its file (the caret stays on its line). false: it cannot be read (it stays as it is).</summary>
        private bool ReloadFromDisk(OpenDocument doc)
        {
            if (doc.View.FilePath is not { } path) return false;
            try
            {
                if (!File.Exists(path)) return false;
                int line = doc.View.GetCaretLine();
                if (doc.Kind is DocumentKind.Image or DocumentKind.Pixel or DocumentKind.Hex) doc.View.ResetTo("", path);
                else
                {
                    string text = File.ReadAllText(path);
                    if (text != doc.View.GetText() || doc.View.IsModified) doc.View.ResetTo(text, path);
                    if (doc.Script is { } script && line > 1 && line <= script.LineCount)
                    {
                        script.SetCaretByLineColumn(line - 1, 0);
                        script.ScrollToLine(line);
                    }
                }
                doc.DeletedOnDisk = false;
                _baselines.Remember(path);
                UpdateTitle(doc);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }

        /// <summary>A document with changes of its own whose file changed on disk: keep mine, take theirs, or look at the differences first.</summary>
        private async Task AskAboutChangedDocument(OpenDocument doc)
        {
            if (doc.View.FilePath is not { } path) return;
            Activate(doc);
            while (true)
            {
                var answer = await Dialogs.Ask(this, $"'{doc.DisplayName}' was changed on disk, and it has changes in the editor that are not saved.\n\n{path}\n\nKeep yours (the next save overwrites the file), or take the file from disk (your changes are lost)?",
                    "Changed on Disk", ("Keep Mine", Dialogs.Answer.No), ("Take Theirs", Dialogs.Answer.Yes), ("Show Diff", Dialogs.Answer.Ok));
                if (answer == Dialogs.Answer.Yes) { ReloadFromDisk(doc); UpdateStatus($"{doc.DisplayName}: the file from disk was taken."); return; }
                if (answer == Dialogs.Answer.Ok)
                {
                    string disk;
                    try { disk = File.Exists(path) ? File.ReadAllText(path) : ""; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { disk = ""; }
                    string diff = TextDiff.Unified(disk, doc.View.GetText(), "on disk: " + Path.GetFileName(path), "in the editor: " + doc.DisplayName);
                    await new GitDiffDialog($"{doc.DisplayName}: on disk (-) and in the editor (+)", diff).ShowDialog(this);
                    continue;
                }
                // keep mine (also when the dialog was closed): the file's new state is known, the document stays modified
                _baselines.Remember(path);
                UpdateStatus($"{doc.DisplayName}: your changes were kept (the file on disk is overwritten when you save).");
                return;
            }
        }

        /// <summary>After a git command of the editor that may have changed the files (pull, switch, discard): everything is compared and loaded again.</summary>
        private Task SyncAfterGit() => SyncWithDisk(null);
    }
}
