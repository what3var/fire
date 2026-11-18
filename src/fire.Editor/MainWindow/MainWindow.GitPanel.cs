using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using fire.Git;

namespace fire.Editor
{
    // The Git panel (docs/tools/git.md), a tab next to the solution explorer: the commands of its buttons and rows. What it shows comes with every RefreshGit (MainWindow.Git.cs).
    public partial class MainWindow
    {
        private readonly GitPanelControl _gitPanel = new();

        private void InitGitPanel()
        {
            _gitPanel.RefreshRequested += () => { _gitPanel.ShowNotice(null); RefreshGit(); };
            _gitPanel.InitRequested += () => _ = GitInit();
            _gitPanel.FetchRequested += () => _ = GitPanelCommand(GitFetch);
            _gitPanel.PullRequested += () => _ = GitPanelCommand(GitPull);
            _gitPanel.PushRequested += () => _ = GitPanelCommand(GitPush);
            _gitPanel.StageRequested += entries => _ = GitPanelStage(entries, stage: true);
            _gitPanel.UnstageRequested += entries => _ = GitPanelStage(entries, stage: false);
            _gitPanel.DiscardRequested += entries => _ = GitPanelDiscard(entries);
            _gitPanel.DiffRequested += entry => _ = GitPanelDiff(entry);
            _gitPanel.OpenRequested += entry => { if (File.Exists(entry.FullPath)) OpenFile(entry.FullPath); };
            _gitPanel.CommitRequested += (message, push) => _ = GitPanelCommit(message, push);
            _gitPanel.FolderFilesRequested += entry => _ = GitPanelFolderFiles(entry);
            _gitPanel.IgnoreRequested += entry => _ = GitPanelIgnore(entry);
            _gitPanel.RepositoryRequested += OpenGitRepositoryView;
        }

        /// <summary>The files of an untracked folder that was opened in the panel (read in the background, at most GitRows.FolderLimit).</summary>
        private async Task GitPanelFolderFiles(GitOverviewEntry folder)
        {
            if (_gitRoot is not { } root) return;
            try
            {
                var (files, total) = await Task.Run(() =>
                {
                    using var repo = GitRepository.Open(root);
                    var (paths, all) = repo.UntrackedFilesIn(folder.FullPath, GitRows.FolderLimit);
                    return (paths.Select(p => new GitOverviewEntry(Path.GetRelativePath(repo.RootPath, p).Replace('\\', '/'), p, GitFileState.Untracked, false)).ToList(), all);
                });
                _gitPanel.SetFolderFiles(folder.FullPath, files, total);
            }
            catch (GitException ex) { _gitPanel.ShowNotice(ex.Message, error: true); }
        }

        /// <summary>"Add to .gitignore": a line for the file or folder in the .gitignore at the top of the repository.</summary>
        private async Task GitPanelIgnore(GitOverviewEntry entry)
        {
            if (_gitRoot is not { } root) return;
            try
            {
                string line = await Task.Run(() => { using var repo = GitRepository.Open(root); return repo.AddToIgnore(entry.FullPath); });
                _gitPanel.ShowNotice($"'{line}' is in the .gitignore now.");
                UpdateStatus($"Added '{line}' to {Path.Combine(root, ".gitignore")}.");
            }
            catch (GitException ex) { _gitPanel.ShowNotice(ex.Message, error: true); }
            RefreshGit();
        }

        /// <summary>A git command in the background (the user interface does not wait: a repository with very many files can take seconds). Its error is the result, null when it went well.</summary>
        private Task<string?> InGit(Action<GitRepository> action) => _gitRoot is not { } root ? Task.FromResult<string?>("There is no git repository here.") : Task.Run<string?>(() =>
        {
            try { using var repo = GitRepository.Open(root); action(repo); return null; }
            catch (GitException ex) { return ex.Message; }
        });

        /// <summary>Shows the state of the repository in the panel (called with every refresh).</summary>
        private void ShowGitPanel(GitOverview? overview)
        {
            if (GitRepository.UnavailableReason is { } why) { _gitPanel.ShowNone(why, canInit: false); return; }
            if (_gitRoot == null || overview == null)
            {
                bool canInit = GitProbePath() != null;
                _gitPanel.ShowNone(canInit ? "The solution (or the file) is not in a git repository." : "Open a solution, a project or a file in a git repository to see its changes here.", canInit);
                return;
            }
            _gitPanel.Show(overview);
        }

        private async Task GitPanelCommand(Func<Task> command)
        {
            _gitPanel.SetBusy(true);
            _gitPanel.ShowNotice(null);
            try { await command(); }
            finally { _gitPanel.SetBusy(false); }
        }

        private async Task GitPanelStage(IReadOnlyList<GitOverviewEntry> entries, bool stage)
        {
            if (entries.Count == 0) return;
            // a document with unsaved changes: what is staged is the file, so it is saved first
            if (stage)
            {
                var paths = entries.Select(e => e.FullPath).ToHashSet(fire.Projects.ProjectFiles.PathComparer);
                foreach (var doc in _documents.Where(d => d.View.IsModified && !d.View.IsReadOnly && FullPathOf(d) is { } p && (paths.Contains(p) || entries.Any(e => e.IsDirectory && fire.Editor.WatchFilter.IsInside(p, e.FullPath)))).ToList()) await Save(doc);
            }
            var list = entries.Select(e => e.FullPath).ToList();
            _gitPanel.SetBusy(true);
            string? error;
            try { error = await InGit(repo => { if (stage) repo.Stage(list); else repo.Unstage(list); }); }
            finally { _gitPanel.SetBusy(false); }
            _gitPanel.ShowNotice(error, error: error != null);
            RefreshGit();
        }

        private static bool SamePath(string a, string b) => fire.Projects.ProjectFiles.PathComparer.Equals(Path.GetFullPath(a), Path.GetFullPath(b));

        private async Task GitPanelDiscard(IReadOnlyList<GitOverviewEntry> entries)
        {
            if (entries.Count == 0) return;
            int untracked = entries.Count(e => e.State == GitFileState.Untracked);
            string what = entries.Count == 1
                ? (untracked == 1 ? $"Delete the new file '{entries[0].Path}'?" : $"Throw away the changes of '{entries[0].Path}'? The file goes back to the last commit.")
                : $"Throw away the changes of {entries.Count} files?" + (untracked > 0 ? $" {untracked} new file{(untracked == 1 ? " is" : "s are")} deleted." : "") + " The files go back to the last commit.";
            if (await Dialogs.Ask(this, what + "\n\nThis cannot be undone.", "Discard Changes", ("Discard", Dialogs.Answer.Yes), ("Cancel", Dialogs.Answer.Cancel)) != Dialogs.Answer.Yes) return;
            using var repo = await OpenGit("Discard Changes");
            if (repo == null) return;
            try
            {
                // (the open documents of these files: what is in the editor is thrown away too - that is what was asked)
                var paths = entries.Select(e => e.FullPath).ToHashSet(fire.Projects.ProjectFiles.PathComparer);
                var open = _documents.Where(d => FullPathOf(d) is { } p && (paths.Contains(p) || entries.Any(e => e.IsDirectory && WatchFilter.IsInside(p, e.FullPath)))).ToList();
                _gitPanel.SetBusy(true);
                _diskChanges.Hold();
                try { await Task.Run(() => repo.Discard(paths)); }
                finally { _gitPanel.SetBusy(false); _diskChanges.Release(); }
                foreach (var doc in open)
                {
                    if (File.Exists(doc.View.FilePath)) ReloadFromDisk(doc);
                    else _factory.CloseDockable(doc.Layout);
                }
                UpdateStatus(entries.Count == 1 ? $"{entries[0].Name} is as in the last commit." : $"{entries.Count} files are as in the last commit.");
            }
            catch (GitException ex) { _gitPanel.ShowNotice(ex.Message, error: true); }
            await SyncAfterGit();
        }

        private async Task GitPanelDiff(GitOverviewEntry entry)
        {
            using var repo = await OpenGit("Changes");
            if (repo == null) return;
            try
            {
                string diff = await Task.Run(() => repo.Diff(entry.FullPath, staged: entry.IsStaged));
                string where = entry.IsStaged ? "staged" : entry.IsDirectory ? "new folder" : entry.State == GitFileState.Untracked ? "new file" : "not staged";
                await new GitDiffDialog($"Changes of {entry.Path} ({where})", diff).ShowDialog(this);
            }
            catch (GitException ex) { _gitPanel.ShowNotice(ex.Message, error: true); }
        }

        private async Task GitPanelCommit(string message, bool push)
        {
            if (string.IsNullOrWhiteSpace(message)) { _gitPanel.ShowNotice("Write a message for the commit.", error: true); return; }
            using var repo = await OpenGit("Commit");
            if (repo == null) return;
            await SaveAllModified();
            try
            {
                var overview = await Task.Run(() => repo.Overview());
                if (overview.HasConflicts) { _gitPanel.ShowNotice("Some files still have conflicts (!): solve them and stage them (+) first.", error: true); return; }
                if (overview.Operation == GitOperationKind.Rebase) { _gitPanel.ShowNotice("A rebase is going on: stage the solved files (+), then press Continue in the Repository view (Git > Repository).", error: true); return; }
                if (overview.Staged.Count == 0 && !overview.IsMerging)
                {
                    if (overview.Count == 0) { _gitPanel.ShowNotice("Nothing has changed since the last commit."); return; }
                    if (await Dialogs.Ask(this, $"Nothing is staged. Stage all {overview.Count} changed file{(overview.Count == 1 ? "" : "s")} and commit them?", "Commit",
                        ("Stage All and Commit", Dialogs.Answer.Yes), ("Cancel", Dialogs.Answer.Cancel)) != Dialogs.Answer.Yes) return;
                    _gitPanel.SetBusy(true);
                    try { await Task.Run(() => repo.StageAll()); }
                    finally { _gitPanel.SetBusy(false); }
                }
                var (name, email) = repo.Identity();
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(email))
                {
                    name = await Dialogs.Input(this, "Git does not know who you are yet. Your name (kept for this repository):", "Commit", name ?? "");
                    if (string.IsNullOrWhiteSpace(name)) return;
                    email = await Dialogs.Input(this, "Your e-mail address:", "Commit", email ?? "");
                    if (string.IsNullOrWhiteSpace(email)) return;
                    repo.SetIdentity(name.Trim(), email.Trim());
                }
                var commit = await Task.Run(() => repo.Commit(message));
                _gitPanel.Message = "";
                _gitPanel.ShowNotice($"Committed {commit.ShortSha}: {commit.Summary}");
                UpdateStatus($"Committed {commit.ShortSha}: {commit.Summary}");
            }
            catch (GitException ex) { _gitPanel.ShowNotice(ex.Message, error: true); RefreshGit(); return; }
            RefreshGit();
            if (push) await GitPanelCommand(GitPush);
        }
    }
}
