using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using fire.Git;
using fire.Projects;

namespace fire.Editor
{
    // Git (docs/tools/git.md): the state of the files in the solution explorer, the branch in the status bar, the Git menu - commit, history, differences, branches, pull and push.
    // The repository is opened for each command (nothing stays open, so nothing stays locked); what the explorer shows is a snapshot that is read again in the background.
    public partial class MainWindow
    {
        private string? _gitRoot;
        private string? _gitBranch;
        private int _gitAhead, _gitBehind;
        private Dictionary<string, GitFileState> _gitStates = new();
        private GitOverview? _gitOverview;
        private bool _gitBusy;
        private DateTime _gitRefreshed = DateTime.MinValue;
        private readonly Dictionary<string, GitCredentials> _gitCredentials = new(StringComparer.OrdinalIgnoreCase);

        private void InitGit()
        {
            Activated += (_, _) => { if ((DateTime.UtcNow - _gitRefreshed).TotalSeconds > 3) RefreshGit(); };   // git may have been used outside of the editor
        }

        /// <summary>The path that decides which repository is meant: the solution or project, else the file of the active document (the Repository view: the repository it shows).</summary>
        private string? GitProbePath() => _workspace.FilePath ?? FullPathOf(ActiveDocument) ?? (ActiveDocument?.View as GitRepositoryView)?.Root;

        // one refresh at a time: one that is asked for while another runs is done once after it (a burst of requests is one more refresh, not one each)
        private bool _gitRefreshRunning, _gitRefreshAgain;

        /// <summary>Reads the state of the repository again in the background and shows it (explorer marks, branch in the status bar, the Git panel). Never on the UI thread.</summary>
        private void RefreshGit()
        {
            _gitRefreshed = DateTime.UtcNow;
            if (_gitRefreshRunning) { _gitRefreshAgain = true; return; }
            _gitRefreshRunning = true;
            string? probe = GitProbePath();
            _ = Task.Run(() =>
            {
                long statusMs = 0;
                string? root = null, branch = null;
                var states = new Dictionary<string, GitFileState>();
                int ahead = 0, behind = 0;
                GitOverview? overview = null;
                try
                {
                    if (probe != null && GitRepository.Discover(probe) is { } found)
                    {
                        using var repo = GitRepository.Open(found);
                        root = repo.RootPath;
                        branch = repo.BranchName;
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        var changes = repo.Status(collapseUntrackedFolders: true);   // (a new folder is one entry, as in `git status`)
                        long tStatus = statusMs = sw.ElapsedMilliseconds;
                        states = GitRepository.StateMapOf(changes);
                        overview = repo.Overview(changes);
                        GitTiming($"status {tStatus} ms ({changes.Count} entries), overview {sw.ElapsedMilliseconds - tStatus} ms");
                        ahead = overview.Ahead; behind = overview.Behind;
                    }
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }   // (any error: the next refresh must still be possible)
                Dispatcher.UIThread.Post(() =>
                {
                    _gitRefreshRunning = false;
                    // a repository that is slow to read: the watcher waits longer before it asks again
                    _diskChanges.Slow = statusMs > 700 || states.Count > 5000;
                    bool rootChanged = root != _gitRoot;
                    _gitRoot = root; _gitBranch = branch; _gitStates = states; _gitAhead = ahead; _gitBehind = behind; _gitOverview = overview;
                    var ui = System.Diagnostics.Stopwatch.StartNew();
                    ShowGitState();
                    GitTiming($"UI: ShowGitState {ui.ElapsedMilliseconds} ms");
                    Dispatcher.UIThread.Post(() => GitTiming($"UI: shown (layout and render) after {ui.ElapsedMilliseconds} ms"), DispatcherPriority.Background);
                    if (rootChanged) UpdateWatchers();   // (the .git folder of the repository is watched too)
                    if (_gitRefreshAgain) { _gitRefreshAgain = false; RefreshGit(); }   // (asked for while this one ran: once more)
                });
            });
        }

        /// <summary>FIRE_GIT_TIMING=1: the times of reading and showing the git state go to stderr (to measure big repositories).</summary>
        private static readonly bool GitTimingOn = Environment.GetEnvironmentVariable("FIRE_GIT_TIMING") == "1";
        private static void GitTiming(string text) { if (GitTimingOn) Console.Error.WriteLine($"[git {DateTime.Now:HH:mm:ss.fff}] {text}"); }

        private void ShowGitState()
        {
            GitText.Text = _gitRoot == null ? "" : $"git: {_gitBranch}" + (_gitAhead > 0 ? $" ↑{_gitAhead}" : "") + (_gitBehind > 0 ? $" ↓{_gitBehind}" : "") + (_gitStates.Count > 0 ? $"  {_gitStates.Count} changed" : "");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _solutionPanel.UpdateGit(_workspace, FullPathOf(ActiveDocument), _gitRoot == null ? null : _gitStates, _gitBranch);   // (only the marks: the tree follows the workspace in RefreshProjectUi)
            long explorer = sw.ElapsedMilliseconds;
            ShowGitPanel(_gitOverview);
            ShowGitRepositoryView();
            GitTiming($"UI: solution explorer {explorer} ms, Git panel {sw.ElapsedMilliseconds - explorer} ms");
        }

        // ---- the menu ---------------------------------------------------------------------------------------------------------------------

        private void GitMenu_SubmenuOpened(object? sender, RoutedEventArgs e)
        {
            bool repo = _gitRoot != null;
            bool file = repo && FullPathOf(ActiveDocument) is { } p && _gitStates.ContainsKey(Path.GetFullPath(p));
            mnuGitInit.IsEnabled = !repo && GitProbePath() != null;
            mnuGitCommit.IsEnabled = mnuGitPull.IsEnabled = mnuGitPush.IsEnabled = mnuGitFetch.IsEnabled = mnuGitServer.IsEnabled = mnuGitBranches.IsEnabled = mnuGitHistory.IsEnabled = mnuGitRepository.IsEnabled = repo && !_gitBusy;
            mnuGitFileHistory.IsEnabled = repo && FullPathOf(ActiveDocument) != null;
            mnuGitFileChanges.IsEnabled = mnuGitFileDiscard.IsEnabled = file;
        }

        private async void GitInit_Click(object? sender, RoutedEventArgs e) => await GitInit();
        private async void GitClone_Click(object? sender, RoutedEventArgs e) => await GitClone();
        private async void GitCommit_Click(object? sender, RoutedEventArgs e) => await GitCommit(null);
        private async void GitPull_Click(object? sender, RoutedEventArgs e) => await GitPull();
        private async void GitPush_Click(object? sender, RoutedEventArgs e) => await GitPush();
        private async void GitFetch_Click(object? sender, RoutedEventArgs e) => await GitFetch();
        private async void GitServer_Click(object? sender, RoutedEventArgs e) => await GitAddServer();
        private async void GitHistory_Click(object? sender, RoutedEventArgs e) => await GitHistory(null);
        private async void GitFileHistory_Click(object? sender, RoutedEventArgs e) => await GitHistory(FullPathOf(ActiveDocument));
        private async void GitFileChanges_Click(object? sender, RoutedEventArgs e) => await GitShowChanges(FullPathOf(ActiveDocument));
        private async void GitFileDiscard_Click(object? sender, RoutedEventArgs e) => await GitDiscard(FullPathOf(ActiveDocument));
        private async void GitBranches_Click(object? sender, RoutedEventArgs e) => await GitBranches();
        private void GitRefresh_Click(object? sender, RoutedEventArgs e) => RefreshGit();
        private void GitShowPanel_Click(object? sender, RoutedEventArgs e) { RevealTool("git"); RefreshGit(); }

        /// <summary>Opens the repository for a command; null (and a message) when there is none.</summary>
        private async Task<GitRepository?> OpenGit(string title)
        {
            if (_gitRoot == null) { await Dialogs.Message(this, "There is no git repository here: Git > Create Repository makes one.", title); return null; }
            try { return GitRepository.Open(_gitRoot); }
            catch (GitException ex) { await Dialogs.Message(this, ex.Message, title); return null; }
        }

        // ---- commands ---------------------------------------------------------------------------------------------------------------------

        private async Task GitInit()
        {
            string? probe = GitProbePath();
            if (probe == null) { await Dialogs.Message(this, "Open a solution or project first (or a file that is saved).", "Create Repository"); return; }
            string folder = _workspace.FilePath != null ? Path.GetDirectoryName(_workspace.FilePath)! : Path.GetDirectoryName(probe)!;
            if (await Dialogs.Ask(this, $"Make a git repository in\n{folder}\n\nA .gitignore for the files that a build writes (bin, obj, *.fpk) is added.", "Create Repository", ("Create", Dialogs.Answer.Yes), ("Cancel", Dialogs.Answer.Cancel)) != Dialogs.Answer.Yes) return;
            try
            {
                using var repo = GitRepository.Init(folder);
                UpdateStatus($"Git repository created in {folder}.");
            }
            catch (GitException ex) { await Dialogs.Message(this, ex.Message, "Create Repository"); return; }
            RefreshGit();
            if (await Dialogs.Ask(this, "Make the first commit with everything that is there now?", "Create Repository", ("Commit...", Dialogs.Answer.Yes), ("Later", Dialogs.Answer.No)) == Dialogs.Answer.Yes)
            {
                _gitRoot = folder;   // the refresh has not come back yet
                await GitCommit(null);
            }
        }

        private async Task GitClone()
        {
            string? url = await Dialogs.Input(this, "Address of the repository (https://...):", "Clone Repository");
            if (string.IsNullOrWhiteSpace(url)) return;
            url = url.Trim();
            string name = Path.GetFileNameWithoutExtension(url.TrimEnd('/', '\\').Split('/', '\\').Last());
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Folder that gets the repository (a folder of its own is made inside)" });
            if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } parent) return;
            string target = Path.Combine(parent, string.IsNullOrEmpty(name) ? "repository" : name);
            if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any()) { await Dialogs.Message(this, $"'{target}' exists and is not empty.", "Clone Repository"); return; }
            UpdateStatus($"Cloning {url}...");
            string? error = null;
            await RunGit(() =>
            {
                try { using var r = GitRepository.Clone(url, target, AskCredentials); }
                catch (GitException ex) { error = ex.Message; }
            });
            if (error != null) { UpdateStatus("Clone failed."); await Dialogs.Message(this, error, "Clone Repository"); return; }
            UpdateStatus($"Cloned into {target}.");
            // a solution is opened, else a project
            string? open = Directory.EnumerateFiles(target, "*" + FireSolution.Extension, SearchOption.AllDirectories).FirstOrDefault()
                ?? Directory.EnumerateFiles(target, "*" + FireProject.Extension, SearchOption.AllDirectories).FirstOrDefault();
            if (open != null) OpenWorkspace(open);
            RefreshGit();
        }

        private async Task GitCommit(IEnumerable<string>? preselected)
        {
            using var repo = await OpenGit("Commit");
            if (repo == null) return;
            await SaveAllModified();
            if ((await Task.Run(() => repo.Status(collapseUntrackedFolders: true))).Count == 0 && !repo.IsMerging) { await Dialogs.Message(this, "Nothing has changed since the last commit.", "Commit"); return; }
            var dialog = new GitCommitDialog(repo, preselected);
            await dialog.ShowDialog(this);
            if (dialog.Committed is { } commit) UpdateStatus($"Committed {commit.ShortSha}: {commit.Summary}");
            RefreshGit();
            if (dialog.PushAfter && dialog.Committed != null) await GitPush();
        }

        private async Task SaveAllModified()
        {
            foreach (var doc in _documents.Where(d => d.View.IsModified && !d.View.IsReadOnly && d.View.FilePath != null).ToList())
                await Save(doc);
        }

        /// <summary>The credentials for a server: those used before, else the user is asked (this is called from the thread that talks to the server).</summary>
        private GitCredentials? AskCredentials(string url)
        {
            string host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
            lock (_gitCredentials) if (_gitCredentials.TryGetValue(host, out var known)) return known;
            var answer = Dispatcher.UIThread.InvokeAsync(() => GitCredentialsDialog.Ask(this, url)).GetAwaiter().GetResult();
            if (answer.Credentials != null && answer.Remember) lock (_gitCredentials) _gitCredentials[host] = answer.Credentials;
            return answer.Credentials;
        }

        private async Task RunGit(Action action)
        {
            _gitBusy = true;
            _diskChanges.Hold();   // (what the command changes is compared afterwards, all at once)
            try { await Task.Run(action); }
            finally { _gitBusy = false; _diskChanges.Release(); }
        }

        private async Task GitFetch()
        {
            using var repo = await OpenGit("Fetch");
            if (repo == null) return;
            UpdateStatus("Fetching...");
            string? error = null;
            await RunGit(() => { try { repo.Fetch(AskCredentials); } catch (GitException ex) { error = ex.Message; } });
            if (error != null) { ForgetCredentialsAfter(error); UpdateStatus("Fetch failed."); await Dialogs.Message(this, error, "Fetch"); return; }
            UpdateStatus("Fetched.");
            RefreshGit();
        }

        private async Task GitPull()
        {
            using var repo = await OpenGit("Pull");
            if (repo == null) return;
            await SaveAllModified();
            UpdateStatus("Pulling...");
            string? error = null;
            GitPullResult result = GitPullResult.UpToDate;
            await RunGit(() => { try { result = repo.Pull(AskCredentials); } catch (GitException ex) { error = ex.Message; } });
            if (error != null) { ForgetCredentialsAfter(error); UpdateStatus("Pull failed."); await Dialogs.Message(this, error, "Pull"); return; }
            // the files that were changed by the pull: the solution, the projects and the open documents are loaded again when they changed
            await SyncAfterGit();
            switch (result)
            {
                case GitPullResult.UpToDate: UpdateStatus("Already up to date."); break;
                case GitPullResult.FastForward: UpdateStatus("Pulled: the changes of the server are in."); break;
                case GitPullResult.Merged: UpdateStatus("Pulled and joined with your commits."); break;
                case GitPullResult.Conflicts:
                    UpdateStatus("Pulled: some files have conflicts.");
                    await Dialogs.Message(this, "Some files were changed on both sides. They are marked in the solution explorer (!) and contain <<<<<<< ======= >>>>>>> markers: edit them, then commit (Git > Commit) to finish.", "Pull");
                    break;
            }
            RefreshGit();
        }

        private async Task GitPush()
        {
            using var repo = await OpenGit("Push");
            if (repo == null) return;
            if (repo.Remotes().Count == 0 && !await AddServerFor(repo)) return;
            UpdateStatus("Pushing...");
            string? error = null;
            await RunGit(() => { try { repo.Push(AskCredentials); } catch (GitException ex) { error = ex.Message; } });
            if (error != null) { ForgetCredentialsAfter(error); UpdateStatus("Push failed."); await Dialogs.Message(this, error, "Push"); return; }
            UpdateStatus("Pushed.");
            RefreshGit();
        }

        private void ForgetCredentialsAfter(string error)
        {
            if (error.Contains("did not accept", StringComparison.OrdinalIgnoreCase)) lock (_gitCredentials) _gitCredentials.Clear();   // a wrong password is asked for again
        }

        private async Task GitAddServer()
        {
            using var repo = await OpenGit("Server");
            if (repo != null) await AddServerFor(repo);
        }

        private async Task<bool> AddServerFor(GitRepository repo)
        {
            string current = repo.Remotes().FirstOrDefault().Url ?? "";
            string? url = await Dialogs.Input(this, "Address of the server's repository (https://...). The repository has to exist there already (an empty one is fine).", "Server", current);
            if (string.IsNullOrWhiteSpace(url)) return false;
            try
            {
                if (repo.Remotes().Count > 0) { await Dialogs.Message(this, "This repository has a server already (" + current + "). Change it with git outside of the editor.", "Server"); return false; }
                repo.AddRemote("origin", url.Trim());
                UpdateStatus("Server added: " + url.Trim());
                return true;
            }
            catch (GitException ex) { await Dialogs.Message(this, ex.Message, "Server"); return false; }
        }

        private async Task GitHistory(string? path)
        {
            using var repo = await OpenGit("History");
            if (repo == null) return;
            await new GitHistoryDialog(repo, path).ShowDialog(this);
        }

        private async Task GitShowChanges(string? path)
        {
            if (path == null) return;
            using var repo = await OpenGit("Changes");
            if (repo == null) return;
            try
            {
                string staged = repo.Diff(path, staged: true), unstaged = repo.Diff(path, staged: false);
                await new GitDiffDialog($"Changes of {Path.GetFileName(path)}", staged + (staged.Length > 0 && unstaged.Length > 0 ? "\n" : "") + unstaged).ShowDialog(this);
            }
            catch (GitException ex) { await Dialogs.Message(this, ex.Message, "Changes"); }
        }

        private async Task GitDiscard(string? path)
        {
            if (path == null) return;
            using var repo = await OpenGit("Discard");
            if (repo == null) return;
            if (await Dialogs.Ask(this, $"Throw away the changes of '{Path.GetFileName(path)}'? The file goes back to the last commit. This cannot be undone.", "Discard Changes", ("Discard", Dialogs.Answer.Yes), ("Cancel", Dialogs.Answer.Cancel)) != Dialogs.Answer.Yes) return;
            try
            {
                var open = _documents.FirstOrDefault(d => d.View.FilePath != null && Path.GetFullPath(d.View.FilePath) == Path.GetFullPath(path));
                repo.Discard(new[] { path });
                if (open != null && File.Exists(path)) ReloadFromDisk(open);
                else if (open != null) _factory.CloseDockable(open.Layout);
                UpdateStatus($"{Path.GetFileName(path)} is as in the last commit.");
            }
            catch (GitException ex) { await Dialogs.Message(this, ex.Message, "Discard Changes"); }
            await SyncAfterGit();
        }

        private async Task GitBranches()
        {
            using var repo = await OpenGit("Branches");
            if (repo == null) return;
            var dialog = new GitBranchesDialog(repo);
            _diskChanges.Hold();
            try { await dialog.ShowDialog(this); }
            finally { _diskChanges.Release(); }
            if (dialog.Changed) await SyncAfterGit();   // a switch of the branch: the solution, the projects and the documents of the other branch
        }

        /// <summary>A command of the solution explorer for git.</summary>
        private async Task OnGitExplorerCommand(string command, ExplorerNode? node)
        {
            switch (command)
            {
                case "git-commit": await GitCommit(node?.Path != null && node.Kind is ExplorerKind.File or ExplorerKind.Content ? new[] { node.Path } : null); break;
                case "git-pull": await GitPull(); break;
                case "git-push": await GitPush(); break;
                case "git-diff": await GitShowChanges(node?.Path); break;
                case "git-discard": await GitDiscard(node?.Path); break;
                case "git-history-file": await GitHistory(node?.Path); break;
                case "git-init": await GitInit(); break;
            }
        }
    }
}
