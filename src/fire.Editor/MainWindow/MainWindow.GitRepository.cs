using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using fire.Git;

namespace fire.Editor
{
    // The Repository view of git (docs/tools/git.md, GitRepositoryView) and the branch in the status bar: the operations ask first (in clear words, what is lost said before), run in the
    // background with the file watcher held, and afterwards the editor compares what it holds with the disk (SyncAfterGit) and reads the git state again (RefreshGit: explorer, Git panel, view).
    public partial class MainWindow
    {
        private OpenDocument? GitRepositoryDocument => _documents.FirstOrDefault(d => d.Kind == DocumentKind.GitRepository);

        private void GitRepository_Click(object? sender, RoutedEventArgs e) => OpenGitRepositoryView();

        /// <summary>Opens the Repository view (or brings it to the front) for the repository of the solution.</summary>
        private void OpenGitRepositoryView()
        {
            if (GitRepositoryDocument is { } open) { Activate(open); ((GitRepositoryView)open.View).ShowRepository(_gitRoot); return; }
            string name = _gitRoot != null ? Path.GetFileName(_gitRoot) : "git";
            var doc = CreateDocument(DocumentKind.GitRepository, "", null, untitledName: $"Repository: {name}");
            var view = (GitRepositoryView)doc.View;
            view.CommandRequested += request => _ = GitViewCommand(request);
            view.ShowRepository(_gitRoot);
        }

        /// <summary>After every refresh of the git state: the view follows (it reads the log again only when a commit, a branch, a tag or the head changed).</summary>
        private void ShowGitRepositoryView()
        {
            if (GitRepositoryDocument is { } doc && doc.View is GitRepositoryView view)
            {
                string? root = _gitRoot ?? view.Root;   // (no repository for the active document: the view stays with the one it shows)
                view.ShowRepository(root);
                doc.Layout.Title = $"Repository: {(root != null ? Path.GetFileName(root) : "git")}";
            }
        }

        private GitRepositoryView? RepositoryView => GitRepositoryDocument?.View as GitRepositoryView;

        /// <summary>A git operation in the background, its error as the result (null: it went well); then the files and the git state are read again.</summary>
        private async Task<string?> GitOperation(string status, Action<GitRepository> action, bool sync = true)
        {
            if (_gitRoot is not { } root) return "There is no git repository here.";
            if (_gitBusy) return "Another git command is still running.";
            UpdateStatus(status);
            string? error = null;
            await RunGit(() =>
            {
                try { using var repo = GitRepository.Open(root); action(repo); }
                catch (GitException ex) { error = ex.Message; }
            });
            if (sync) await SyncAfterGit();
            RefreshGit();
            RepositoryView?.Reload();
            return error;
        }

        private async Task<T?> GitRead<T>(Func<GitRepository, T> read) where T : class
        {
            if (_gitRoot is not { } root) return null;
            try { return await Task.Run(() => { using var repo = GitRepository.Open(root); return read(repo); }); }
            catch (GitException ex) { await Dialogs.Message(this, ex.Message, "Git"); return null; }
        }

        private async Task<bool> Confirm(string text, string title, string yes) =>
            await Dialogs.Ask(this, text, title, (yes, Dialogs.Answer.Yes), ("Cancel", Dialogs.Answer.Cancel)) == Dialogs.Answer.Yes;

        private async Task Report(string? error, string title, string done)
        {
            if (error != null) { UpdateStatus($"{title} failed."); RepositoryView?.ShowNotice(error, error: true); await Dialogs.Message(this, error, title); }
            else { UpdateStatus(done); RepositoryView?.ShowNotice(null); }
        }

        /// <summary>A command of the Repository view: asks first where something can be lost or changes a lot, runs it, reports.</summary>
        private async Task GitViewCommand(GitViewRequest r)
        {
            var refs = RepositoryView?.Refs;
            string current = refs == null ? "the current branch" : refs.IsDetached ? "the detached HEAD" : $"'{refs.Head}'";
            switch (r.Action)
            {
                case GitViewAction.Fetch:
                    await GitFetchAll();
                    break;
                case GitViewAction.Pull: await GitPull(); RepositoryView?.Reload(); break;
                case GitViewAction.Push: await GitPush(); RepositoryView?.Reload(); break;
                case GitViewAction.CopySha:
                    if (Clipboard is { } clip) await clip.SetTextAsync(r.Target);
                    UpdateStatus($"Copied {r.Target}.");
                    break;

                case GitViewAction.CheckoutBranch:
                    await GitSwitchBranch(r.Target);
                    break;
                case GitViewAction.CheckoutCommit:
                {
                    if (!await Confirm($"Check out {r.Label}?\n\nNo branch will be checked out (a \"detached HEAD\"): you can look around and build, but commits you make belong to no branch and are easily lost when you switch away - make a branch for them (Create Branch Here...).",
                        "Check Out Commit", "Check Out")) return;
                    if (!await HandleLocalChanges("check out the commit")) return;
                    await Report(await GitOperation($"Checking out {r.Label}...", repo => repo.CheckoutCommit(r.Target)), "Check Out", $"Checked out {r.Label} (detached HEAD).");
                    break;
                }
                case GitViewAction.CreateBranch:
                {
                    string? name = await Dialogs.Input(this, $"Name of the new branch at {r.Label}:", "Create Branch");
                    if (string.IsNullOrWhiteSpace(name)) return;
                    var answer = await Dialogs.Ask(this, $"Switch to the new branch '{name.Trim()}' now?", "Create Branch", ("Create and Switch", Dialogs.Answer.Yes), ("Only Create", Dialogs.Answer.No), ("Cancel", Dialogs.Answer.Cancel));
                    if (answer == Dialogs.Answer.Cancel) return;
                    bool checkout = answer == Dialogs.Answer.Yes;
                    if (checkout && !await HandleLocalChanges("switch")) return;
                    await Report(await GitOperation("Creating the branch...", repo => repo.CreateBranchAt(name.Trim(), r.Target, checkout), sync: checkout), "Create Branch", $"Branch '{name.Trim()}' created{(checkout ? " and checked out" : "")}.");
                    break;
                }
                case GitViewAction.CreateTag:
                {
                    string? name = await Dialogs.Input(this, $"Name of the new tag at {r.Label} (e.g. v1.2):", "Create Tag");
                    if (string.IsNullOrWhiteSpace(name)) return;
                    string? message = await Dialogs.Input(this, "A message makes it an annotated tag (with your name and the date). Leave it empty for a lightweight tag:", "Create Tag");
                    if (message == null) return;
                    await Report(await GitOperation("Creating the tag...", repo => repo.CreateTag(name.Trim(), r.Target, message), sync: false), "Create Tag", $"Tag '{name.Trim()}' created.");
                    break;
                }
                case GitViewAction.DeleteTag:
                    if (!await Confirm($"Delete the tag '{r.Target}' here?\n\nThe commit stays. A copy of the tag on a server is not deleted.", "Delete Tag", "Delete")) return;
                    await Report(await GitOperation("Deleting the tag...", repo => repo.DeleteTag(r.Target), sync: false), "Delete Tag", $"Tag '{r.Target}' deleted.");
                    break;
                case GitViewAction.PushTag:
                    await Report(await GitOperation($"Pushing the tag {r.Target}...", repo => repo.PushTag(r.Target, AskCredentials), sync: false), "Push Tag", $"Tag '{r.Target}' is on the server.");
                    break;
                case GitViewAction.DeleteBranch:
                {
                    bool merged = await GitRead(repo => (object)repo.IsMergedIntoHead(r.Target)) is true;
                    bool force = false;
                    if (merged)
                    {
                        if (!await Confirm($"Delete the branch '{r.Target}'?\n\nIts commits are all in {current}, nothing is lost. A branch of the same name on a server stays.", "Delete Branch", "Delete")) return;
                    }
                    else
                    {
                        if (await Dialogs.Ask(this, $"The branch '{r.Target}' has commits that are not in {current}.\n\nDeleting it loses them (unless another branch, a tag or a server has them). A branch of the same name on a server stays.",
                            "Delete Branch", ("Delete Anyway", Dialogs.Answer.Yes), ("Cancel", Dialogs.Answer.Cancel)) != Dialogs.Answer.Yes) return;
                        force = true;
                    }
                    await Report(await GitOperation("Deleting the branch...", repo => repo.DeleteBranch(r.Target, force), sync: false), "Delete Branch", $"Branch '{r.Target}' deleted.");
                    break;
                }
                case GitViewAction.DeleteRemoteBranch:
                {
                    string shortName = r.Target.Contains('/') ? r.Target.Substring(r.Target.IndexOf('/') + 1) : r.Target;
                    if (await Dialogs.Ask(this, $"Delete the branch '{shortName}' on the server ({r.Target})?\n\nIt is gone for everybody who uses that server. Commits that only this branch has are lost on the server. Your local branches stay.",
                        "Delete Branch on the Server", ("Delete on the Server", Dialogs.Answer.Yes), ("Cancel", Dialogs.Answer.Cancel)) != Dialogs.Answer.Yes) return;
                    await Report(await GitOperation($"Deleting {r.Target} on the server...", repo => repo.DeleteRemoteBranch(r.Target, AskCredentials), sync: false), "Delete Branch on the Server", $"'{shortName}' is deleted on the server.");
                    break;
                }

                case GitViewAction.Merge:
                {
                    if (!await Confirm($"Merge {r.Label} into {current}?\n\nWhen {current} has no commits of its own, it is simply moved forward; otherwise a merge commit joins them. Conflicts stop the merge: solve them, stage the files, then Continue (or Abort).",
                        "Merge", "Merge")) return;
                    await SaveAllModified();
                    GitApplyResult result = GitApplyResult.UpToDate;
                    string? error = await GitOperation($"Merging {r.Label}...", repo => result = repo.Merge(r.Target));
                    await Report(error, "Merge", result switch
                    {
                        GitApplyResult.UpToDate => $"{current} has everything of {r.Label} already.",
                        GitApplyResult.FastForward => $"{current} moved forward to {r.Label}.",
                        GitApplyResult.Committed => $"Merged {r.Label} into {current}.",
                        _ => "The merge stopped at conflicts: solve them, stage the files, then Continue.",
                    });
                    if (error == null && result == GitApplyResult.Conflicts) await ConflictsMessage("merge");
                    break;
                }
                case GitViewAction.Rebase:
                {
                    if (!await Confirm($"Rebase {current} onto {r.Label}?\n\nThe commits of {current} that {r.Label} does not have are made again on top of it, one after the other (new commits: if {current} was pushed already, a push needs to be forced afterwards - avoid that on shared branches).\nConflicts stop the rebase: solve them, stage the files, then Continue, Skip or Abort.",
                        "Rebase", "Rebase")) return;
                    await SaveAllModified();
                    GitRebaseResult? result = null;
                    string? error = await GitOperation($"Rebasing onto {r.Label}...", repo => result = repo.Rebase(r.Target));
                    await Report(error, "Rebase", RebaseText(result));
                    if (error == null && result is { Stopped: true }) await ConflictsMessage("rebase");
                    break;
                }
                case GitViewAction.CherryPick:
                {
                    if (!await Confirm($"Cherry-pick {r.Label} into {current}?\n\nA new commit on {current} gets the changes of that commit. Conflicts stop it: solve them, stage the files, then Continue (or Abort).", "Cherry-Pick", "Cherry-Pick")) return;
                    await SaveAllModified();
                    GitApplyResult result = GitApplyResult.UpToDate;
                    string? error = await GitOperation($"Cherry-picking {r.Label}...", repo => result = repo.CherryPick(r.Target));
                    await Report(error, "Cherry-Pick", result switch
                    {
                        GitApplyResult.Committed => $"Cherry-picked {r.Label}.",
                        GitApplyResult.UpToDate => $"{current} has these changes already: nothing to do.",
                        _ => "The cherry-pick stopped at conflicts: solve them, stage the files, then Continue.",
                    });
                    if (error == null && result == GitApplyResult.Conflicts) await ConflictsMessage("cherry-pick");
                    break;
                }
                case GitViewAction.Revert:
                {
                    if (!await Confirm($"Revert {r.Label}?\n\nA new commit on {current} takes the changes of that commit back (the commit itself stays in the history). Conflicts stop it: solve them, stage the files, then Continue (or Abort).", "Revert", "Revert")) return;
                    await SaveAllModified();
                    GitApplyResult result = GitApplyResult.UpToDate;
                    string? error = await GitOperation($"Reverting {r.Label}...", repo => result = repo.Revert(r.Target));
                    await Report(error, "Revert", result switch
                    {
                        GitApplyResult.Committed => $"Reverted {r.Label}.",
                        GitApplyResult.UpToDate => "Nothing to revert.",
                        _ => "The revert stopped at conflicts: solve them, stage the files, then Continue.",
                    });
                    if (error == null && result == GitApplyResult.Conflicts) await ConflictsMessage("revert");
                    break;
                }
                case GitViewAction.Reset:
                    await GitReset(r.Target, r.Label ?? r.Target, r.Mode, current);
                    break;
                case GitViewAction.ResetToUpstream:
                    await GitReset(r.Target, $"'{r.Target}' (the server's branch, as of the last fetch)", GitResetMode.Hard, current);
                    break;

                case GitViewAction.StashSave:
                {
                    await SaveAllModified();
                    string? message = await Dialogs.Input(this, "Put your changes (also new files) onto the stash: the files go back to the last commit, the changes wait on the stash until you apply them.\n\nA message for the stash (optional):", "Stash");
                    if (message == null) return;
                    bool saved = false;
                    string? error = await GitOperation("Stashing...", repo => saved = repo.StashSave(message));
                    await Report(error, "Stash", saved ? "Changes put onto the stash." : "There are no changes to stash.");
                    break;
                }
                case GitViewAction.StashApply:
                case GitViewAction.StashPop:
                {
                    bool pop = r.Action == GitViewAction.StashPop;
                    GitStashResult result = GitStashResult.Applied;
                    string? error = await GitOperation(pop ? "Popping the stash..." : "Applying the stash...", repo => result = pop ? repo.StashPop(r.Stash) : repo.StashApply(r.Stash));
                    await Report(error, pop ? "Pop Stash" : "Apply Stash", result == GitStashResult.Applied ? (pop ? "The stash is applied and removed." : "The stash is applied (it stays on the stash).")
                        : "The stash is applied with conflicts (it stays on the stash): solve them, then stage the files.");
                    break;
                }
                case GitViewAction.StashDrop:
                    if (await Dialogs.Ask(this, $"Delete stash@{{{r.Stash}}} ({r.Label})?\n\nIts changes are lost.", "Drop Stash", ("Delete", Dialogs.Answer.Yes), ("Cancel", Dialogs.Answer.Cancel)) != Dialogs.Answer.Yes) return;
                    await Report(await GitOperation("Dropping the stash...", repo => repo.StashDrop(r.Stash), sync: false), "Drop Stash", "The stash is deleted.");
                    break;

                case GitViewAction.Continue:
                {
                    var op = refs?.Operation ?? GitOperationKind.None;
                    await SaveAllModified();
                    if (op == GitOperationKind.Rebase)
                    {
                        GitRebaseResult? result = null;
                        string? error = await GitOperation("Continuing the rebase...", repo => result = repo.RebaseContinue());
                        await Report(error, "Continue Rebase", RebaseText(result));
                    }
                    else
                    {
                        GitCommitInfo? commit = null;
                        string? error = await GitOperation($"Finishing the {GitRepository.OperationName(op)}...", repo => commit = repo.FinishOperation());
                        await Report(error, "Continue", commit != null ? $"Committed {commit.ShortSha}: {commit.Summary}" : "Done.");
                    }
                    break;
                }
                case GitViewAction.Skip:
                {
                    if (!await Confirm($"Skip the commit {refs?.RebaseStopped ?? ""}?\n\nIts changes are left out of {current} (also what you have solved of it so far).", "Skip", "Skip")) return;
                    GitRebaseResult? result = null;
                    string? error = await GitOperation("Skipping the commit...", repo => result = repo.RebaseSkip());
                    await Report(error, "Skip", RebaseText(result));
                    break;
                }
                case GitViewAction.Abort:
                {
                    var op = refs?.Operation ?? GitOperationKind.None;
                    string what = GitRepository.OperationName(op);
                    if (await Dialogs.Ask(this, op == GitOperationKind.Rebase
                            ? "Abort the rebase?\n\nThe branch and the files go back to how they were before the rebase started; what was solved so far is lost."
                            : $"Abort the {what}?\n\nThe files go back to the last commit: what the {what} brought and what you solved so far is lost, and so are other uncommitted changes of tracked files.",
                        "Abort", ("Abort " + char.ToUpper(what[0]) + what.Substring(1), Dialogs.Answer.Yes), ("Cancel", Dialogs.Answer.Cancel)) != Dialogs.Answer.Yes) return;
                    await Report(await GitOperation($"Aborting the {what}...", repo => repo.AbortOperation()), "Abort", $"The {what} is aborted.");
                    break;
                }
            }
        }

        private static string RebaseText(GitRebaseResult? r) => r == null ? "" : r.Completed
            ? (r.Steps == 0 ? "Nothing to rebase: the branch has everything already." : $"Rebased: {r.Applied} commit{(r.Applied == 1 ? "" : "s")} made again on top.")
            : $"The rebase stopped at step {r.Step} of {r.Steps}{(r.StoppedAt != null ? $" ({r.StoppedAt})" : "")}: solve the conflicts, stage the files, then Continue, Skip or Abort.";

        private async Task ConflictsMessage(string what)
        {
            await Dialogs.Message(this, $"The {what} stopped: some files were changed on both sides. They are marked with ! (in the Git panel and the solution explorer) and contain <<<<<<< ======= >>>>>>> markers.\n\nEdit them, stage them in the Git panel (+), then press Continue in the banner of the Repository view - or Abort.", char.ToUpper(what[0]) + what.Substring(1));
        }

        private async Task GitFetchAll()
        {
            UpdateStatus("Fetching from all servers...");
            await Report(await GitOperation("Fetching...", repo => repo.FetchAll(AskCredentials), sync: false), "Fetch", "Fetched.");
        }

        /// <summary>A reset of the current branch: what leaves the branch (and whether it is lost) and, for hard, the changed files that are thrown away - said before.</summary>
        private async Task GitReset(string target, string label, GitResetMode mode, string current)
        {
            var preview = await GitRead(repo => repo.PreviewReset(target));
            if (preview == null) return;
            var text = new StringBuilder($"Reset {current} to {label} ({mode.ToString().ToLowerInvariant()})?\n\n");
            text.Append(mode switch
            {
                GitResetMode.Soft => "The branch moves to that commit. Your files stay as they are; the differences are staged (ready to commit again).",
                GitResetMode.Mixed => "The branch moves to that commit. Your files stay as they are; the differences are changes that are not staged.",
                _ => "The branch moves to that commit and ALL FILES are put back to it: every uncommitted change of a tracked file is lost (new, untracked files stay).",
            });
            if (preview.Leaving.Count > 0)
            {
                int lost = preview.Leaving.Count(l => !l.StillElsewhere);
                text.Append($"\n\n{preview.Leaving.Count} commit{(preview.Leaving.Count == 1 ? "" : "s")} leave{(preview.Leaving.Count == 1 ? "s" : "")} the branch");
                text.Append(lost == 0 ? " (another branch or tag still has them):" : lost == preview.Leaving.Count ? " - no other branch or tag has them, they are LOST (unless a server has them):" : $" - {lost} of them are on no other branch or tag and are LOST:");
                foreach (var (commit, elsewhere) in preview.Leaving.Take(12)) text.Append($"\n   {commit.ShortSha}  {(commit.Summary.Length > 60 ? commit.Summary.Substring(0, 59) + "…" : commit.Summary)}{(elsewhere ? "" : "   (lost)")}");
                if (preview.Leaving.Count > 12) text.Append($"\n   ... and {preview.Leaving.Count - 12} more");
            }
            if (preview.NewCommits > 0) text.Append($"\n\nThe branch gets {preview.NewCommits} commit{(preview.NewCommits == 1 ? "" : "s")} it does not have now.");
            if (mode == GitResetMode.Hard)
            {
                if (preview.ChangedFiles.Count > 0)
                {
                    text.Append($"\n\nUncommitted changes that are LOST ({preview.ChangedFiles.Count} file{(preview.ChangedFiles.Count == 1 ? "" : "s")}):");
                    foreach (var f in preview.ChangedFiles.Take(12)) text.Append("\n   ").Append(f);
                    if (preview.ChangedFiles.Count > 12) text.Append($"\n   ... and {preview.ChangedFiles.Count - 12} more");
                }
                else text.Append("\n\nThere are no uncommitted changes of tracked files.");
                text.Append("\n\nThis cannot be undone from the editor.");
            }
            string yes = mode == GitResetMode.Hard ? "Reset Hard - Lose Changes" : "Reset";
            if (await Dialogs.Ask(this, text.ToString(), $"Reset ({mode})", (mode == GitResetMode.Hard ? "Cancel" : yes, mode == GitResetMode.Hard ? Dialogs.Answer.Cancel : Dialogs.Answer.Yes),
                    (mode == GitResetMode.Hard ? yes : "Cancel", mode == GitResetMode.Hard ? Dialogs.Answer.Yes : Dialogs.Answer.Cancel)) != Dialogs.Answer.Yes) return;   // (hard: Cancel is the default button)
            if (mode == GitResetMode.Hard && _documents.Any(d => d.View.IsModified && !d.View.IsReadOnly && d.View.FilePath != null))
            {
                var answer = await Dialogs.Ask(this, "Some open documents have unsaved changes. A hard reset puts their files back as well: their unsaved changes are kept in the editor and you are asked about them afterwards.",
                    "Reset (Hard)", ("Go On", Dialogs.Answer.Yes), ("Cancel", Dialogs.Answer.Cancel));
                if (answer != Dialogs.Answer.Yes) return;
            }
            await Report(await GitOperation($"Resetting to {label}...", repo => repo.Reset(target, mode)), "Reset", $"{current} is at {label} now ({mode.ToString().ToLowerInvariant()}).");
        }

        /// <summary>
        /// Before a switch of the branch with uncommitted changes: Stash them (the new branch starts clean; the changes wait on the stash), carry them over (git keeps them when they do not
        /// clash with the other branch), or cancel. False: cancelled (or the stash failed).
        /// </summary>
        private async Task<bool> HandleLocalChanges(string what)
        {
            await SaveAllModified();
            bool dirty = await GitRead(repo => (object)repo.HasUncommittedChanges()) is true;
            if (!dirty) return true;
            var answer = await Dialogs.Ask(this, $"You have uncommitted changes. What happens to them when you {what}?\n\n" +
                "Stash: they are put onto the stash (Repository view, Stashes) and the files are as in the last commit; apply the stash later to get them back.\n" +
                "Carry over: they stay in your files and come along - this only works when the other commit does not change the same files (else nothing happens and you are told).",
                "Uncommitted Changes", ("Stash", Dialogs.Answer.Yes), ("Carry Over", Dialogs.Answer.No), ("Cancel", Dialogs.Answer.Cancel));
            if (answer == Dialogs.Answer.Cancel) return false;
            if (answer == Dialogs.Answer.No) return true;
            bool saved = false;
            string? error = await GitOperation("Stashing your changes...", repo => saved = repo.StashSave($"before: {what}"));
            if (error != null) { await Dialogs.Message(this, error, "Stash"); return false; }
            if (saved) UpdateStatus("Your changes are on the stash.");
            return true;
        }

        /// <summary>Switches to a branch (a server's branch: to the local branch that follows it, made when there is none), with the question about uncommitted changes.</summary>
        private async Task GitSwitchBranch(string name)
        {
            if (!await HandleLocalChanges($"switch to '{name}'")) return;
            string? local = null;
            string? error = await GitOperation($"Switching to {name}...", repo => local = repo.CheckoutBranch(name));
            await Report(error, "Switch Branch", local != null && local != name ? $"Switched to '{local}' (follows {name})." : $"Switched to '{name}'.");
        }

        private async Task GitNewBranch()
        {
            if (_gitRoot == null) return;
            string? name = await Dialogs.Input(this, $"Name of the new branch (made at the current commit{(_gitBranch != null ? $" of '{_gitBranch}'" : "")}, and switched to; your uncommitted changes come along):", "New Branch");
            if (string.IsNullOrWhiteSpace(name)) return;
            await Report(await GitOperation("Creating the branch...", repo => repo.CreateBranchAt(name.Trim(), "HEAD", checkout: true), sync: false), "New Branch", $"Switched to the new branch '{name.Trim()}'.");
        }

        // ---- the branch in the status bar ---------------------------------------------------------------------------------------------------

        private void InitGitStatus()
        {
            GitText.Cursor = new Cursor(StandardCursorType.Hand);
            GitText.PointerPressed += (_, e) =>
            {
                if (_gitRoot != null && e.GetCurrentPoint(GitText).Properties.IsLeftButtonPressed) { e.Handled = true; _ = ShowBranchPopup(); }
            };
        }

        /// <summary>The popup of the branch in the status bar: the local branches and the servers' branches, filtered by what is typed; New Branch..., the Repository view.</summary>
        private async Task ShowBranchPopup()
        {
            var refs = await GitRead(repo => repo.Refs());
            if (refs == null) return;
            var filter = new TextBox { PlaceholderText = "Switch to a branch (type to filter)", FontSize = 12, MinHeight = 24, Padding = new Thickness(6, 2) };
            var list = new ListBox { MaxHeight = 360, MinWidth = 340, Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            var flyout = new Flyout { Placement = PlacementMode.TopEdgeAlignedRight, ShowMode = FlyoutShowMode.Standard };
            var entries = new List<(string Name, string Text, string Detail, bool Remote, bool Current)>();
            foreach (var b in refs.Branches)
            {
                string sync = b.Upstream == null ? "no server branch" : b.Ahead == 0 && b.Behind == 0 ? b.Upstream : $"{b.Upstream}  " + string.Join(" ", new[] { b.Ahead > 0 ? $"↑{b.Ahead}" : "", b.Behind > 0 ? $"↓{b.Behind}" : "" }.Where(s => s.Length > 0));
                entries.Add((b.Name, (b.IsCurrent ? "● " : "   ") + b.Name, sync, false, b.IsCurrent));
            }
            var followed = refs.Branches.Where(b => b.Upstream != null).Select(b => b.Upstream!).ToHashSet(StringComparer.Ordinal);
            foreach (var b in refs.Remotes.SelectMany(kv => kv.Value))
                entries.Add((b.Name, "   " + b.Name, followed.Contains(b.Name) ? "server (has a local branch)" : "server: check out as a new local branch", true, false));

            void Fill()
            {
                string f = filter.Text?.Trim() ?? "";
                list.Items.Clear();
                bool header = false;
                foreach (var e in entries.Where(e => f.Length == 0 || e.Name.Contains(f, StringComparison.OrdinalIgnoreCase)))
                {
                    if (e.Remote && !header) { header = true; list.Items.Add(new ListBoxItem { IsEnabled = false, Content = new TextBlock { Text = "SERVER BRANCHES", FontSize = 11, FontWeight = FontWeight.Bold, Foreground = EditorTheme.TextDim } }); }
                    var row = new DockPanel { Tag = e.Name };
                    var detail = new TextBlock { Text = e.Detail, FontSize = 11, Foreground = EditorTheme.TextDim, Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                    DockPanel.SetDock(detail, Avalonia.Controls.Dock.Right);
                    row.Children.Add(detail);
                    row.Children.Add(new TextBlock { Text = e.Text, FontSize = 12, FontWeight = e.Current ? FontWeight.Bold : FontWeight.Normal, Foreground = e.Current ? EditorTheme.Magenta : e.Remote ? EditorTheme.Orange : EditorTheme.Text });
                    list.Items.Add(new ListBoxItem { Content = row, Tag = e.Name, IsEnabled = !e.Current, Padding = new Thickness(6, 3) });
                }
                if (list.Items.Count == 0) list.Items.Add(new ListBoxItem { IsEnabled = false, Content = new TextBlock { Text = $"No branch matches '{f}'.", FontSize = 12, Foreground = EditorTheme.TextDim } });
                var firstEnabled = list.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.IsEnabled);
                list.SelectedItem = firstEnabled;
            }
            void Choose()
            {
                if (list.SelectedItem is not ListBoxItem { Tag: string name, IsEnabled: true }) return;
                flyout.Hide();
                _ = GitSwitchBranch(name);
            }
            filter.TextChanged += (_, _) => Fill();
            filter.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { Choose(); e.Handled = true; }
                else if (e.Key is Key.Down or Key.Up)
                {
                    var enabled = list.Items.OfType<ListBoxItem>().Where(i => i.IsEnabled).ToList();
                    int i = enabled.IndexOf(list.SelectedItem as ListBoxItem ?? enabled.FirstOrDefault()!);
                    if (enabled.Count > 0) { list.SelectedItem = enabled[Math.Clamp(i + (e.Key == Key.Down ? 1 : -1), 0, enabled.Count - 1)]; list.ScrollIntoView(list.SelectedItem!); }
                    e.Handled = true;
                }
            };
            list.Tapped += (_, _) => Choose();
            list.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Choose(); e.Handled = true; } };
            Fill();

            var newBranch = new Button { Content = "New Branch...", FontSize = 12 };
            newBranch.Click += (_, _) => { flyout.Hide(); _ = GitNewBranch(); };
            var repository = new Button { Content = "Repository View", FontSize = 12 };
            repository.Click += (_, _) => { flyout.Hide(); OpenGitRepositoryView(); };
            var fetch = new Button { Content = "Fetch", FontSize = 12 };
            ToolTip.SetTip(fetch, "Get the servers' branches and commits (the list is read again afterwards)");
            fetch.Click += async (_, _) => { flyout.Hide(); await GitFetchAll(); };
            var head = new TextBlock
            {
                Text = refs.IsDetached ? $"Detached HEAD at {refs.HeadSha?.Substring(0, 7)}" : $"On '{refs.Head}'" + (refs.Operation != GitOperationKind.None ? $" - a {GitRepository.OperationName(refs.Operation)} is not finished" : ""),
                FontSize = 12, Foreground = EditorTheme.TextDim, Margin = new Thickness(2, 0, 0, 4),
            };
            flyout.Content = new StackPanel
            {
                Spacing = 6, Width = 440,
                Children =
                {
                    head, filter, list,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, Children = { newBranch, fetch, repository } },
                },
            };
            flyout.Opened += (_, _) => Dispatcher.UIThread.Post(() => filter.Focus(), DispatcherPriority.Loaded);
            flyout.ShowAt(GitText);
            _branchPopup = flyout;
        }

        private Flyout? _branchPopup;
    }
}
