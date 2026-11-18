using System.Text;
using LibGit2Sharp;

namespace fire.Git
{
    /// <summary>How a merge, a cherry-pick or a revert ended: nothing to do, the branch was moved on (fast-forward), a commit was made, or it stopped at conflicts (solve, stage, commit).</summary>
    public enum GitApplyResult { UpToDate, FastForward, Committed, Conflicts }

    /// <summary>How a rebase ended (or stopped): `Stopped` at conflicts in step `Step` of `Steps` (the commit `StoppedAt`): solve them, stage the files, then Continue, Skip or Abort.</summary>
    public sealed record GitRebaseResult(bool Completed, int Step, int Steps, string? StoppedAt, int Applied)
    {
        public bool Stopped => !Completed;
    }

    /// <summary>What git reset does: Soft keeps the files and what is staged, Mixed keeps the files (nothing staged), Hard puts the files back to the commit (changes are lost).</summary>
    public enum GitResetMode { Soft, Mixed, Hard }

    /// <summary>What a reset of the current branch to a commit leaves behind: the commits that leave the branch (`StillElsewhere`: another branch or tag still has it), the changed files that a hard reset throws away.</summary>
    public sealed record GitResetPreview(string Branch, string Target, IReadOnlyList<(GitCommitInfo Commit, bool StillElsewhere)> Leaving, IReadOnlyList<string> ChangedFiles, int NewCommits);

    /// <summary>How applying a stash ended.</summary>
    public enum GitStashResult { Applied, Conflicts }

    /// <summary>
    /// The operations of the Repository view (docs/tools/git.md): checkout of a commit or a server's branch, branches and tags at a commit, delete, merge, rebase (not interactive), cherry-pick,
    /// revert, reset, stash. All with LibGit2Sharp (libgit2) - git itself is not needed. A rebase that stops at conflicts is kept on disk as git keeps it (.git/rebase-merge), so it can be
    /// continued (or aborted) later, also with git outside of the editor.
    /// </summary>
    public sealed partial class GitRepository
    {
        private Signature Me()
        {
            var (name, email) = Identity();
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(email)) throw new GitException("Git needs a name and an e-mail address for the commits it makes.");
            return new Signature(name, email, DateTimeOffset.Now);
        }

        private Commit CommitOf(string what)
        {
            var obj = _repo.Branches[what]?.Tip as GitObject ?? _repo.Lookup(what);
            return obj switch
            {
                Commit c => c,
                TagAnnotation a => a.Target.Peel<Commit>(),
                GitObject o => o.Peel<Commit>(),
                _ => throw new GitException($"There is no commit '{what}'."),
            } ?? throw new GitException($"'{what}' is not a commit.");
        }

        private static void CheckName(string name, string what)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsWhiteSpace) || name.Contains("..") || name.StartsWith('-') || name.EndsWith('/') || name.EndsWith(".lock", StringComparison.Ordinal)
                || name.IndexOfAny(new[] { '~', '^', ':', '?', '*', '[', '\\' }) >= 0)
                throw new GitException($"'{name}' is not a name for a {what}.");
        }

        /// <summary>true: tracked files are changed or something is staged (untracked files do not count) - what a rebase or a hard reset would touch.</summary>
        public bool HasUncommittedChanges()
        {
            try { return _repo.RetrieveStatus(new StatusOptions { IncludeUntracked = false }).IsDirty; }
            catch (LibGit2SharpException ex) { throw new GitException(ex.Message, ex); }
        }

        private void RequireNoOperation(string what)
        {
            var op = Operation;
            if (op != GitOperationKind.None) throw new GitException($"A {OperationName(op)} is not finished: finish or abort it before you {what}.");
        }

        public static string OperationName(GitOperationKind op) => op switch
        {
            GitOperationKind.Merge => "merge", GitOperationKind.Rebase => "rebase", GitOperationKind.CherryPick => "cherry-pick", GitOperationKind.Revert => "revert", _ => "git operation",
        };

        private static GitException Wrap(LibGit2SharpException ex) => ex is CheckoutConflictException
            ? new GitException("Changes in your files would be overwritten: commit them, stash them or discard them first.", ex)
            : new GitException(ex.Message, ex);

        // ---- checkout ------------------------------------------------------------------------------------------------------------------------

        /// <summary>Checks a commit out without a branch (the head is detached: new commits belong to no branch until one is made).</summary>
        public void CheckoutCommit(string sha)
        {
            try { Commands.Checkout(_repo, CommitOf(sha)); }
            catch (LibGit2SharpException ex) { throw Wrap(ex); }
        }

        /// <summary>
        /// Switches to the local branch for a server's branch (e.g. "origin/feature"): the one that follows it already, else a new one (`localName`, by default the name without the server) that
        /// follows it. A local branch of that name that follows something else is an error that says so. Returns the name of the local branch.
        /// </summary>
        public string CheckoutRemoteBranch(string remoteBranch, string? localName = null)
        {
            try
            {
                var remote = _repo.Branches[remoteBranch];
                if (remote == null || !remote.IsRemote) throw new GitException($"There is no branch '{remoteBranch}' of a server.");
                var following = _repo.Branches.FirstOrDefault(b => !b.IsRemote && b.TrackedBranch?.CanonicalName == remote.CanonicalName);
                if (following != null && localName == null) { Commands.Checkout(_repo, following); return following.FriendlyName; }
                string name = localName ?? (remote.RemoteName is { } r && remoteBranch.StartsWith(r + "/", StringComparison.Ordinal) ? remoteBranch.Substring(r.Length + 1) : remoteBranch.Substring(remoteBranch.IndexOf('/') + 1));
                CheckName(name, "branch");
                var existing = _repo.Branches[name];
                if (existing != null)
                {
                    if (existing.TrackedBranch?.CanonicalName == remote.CanonicalName) { Commands.Checkout(_repo, existing); return name; }
                    throw new GitException($"There is a local branch '{name}' already, and it does not follow '{remoteBranch}'. Switch to it, or give the new branch another name.");
                }
                var local = _repo.CreateBranch(name, remote.Tip);
                _repo.Branches.Update(local, b => b.TrackedBranch = remote.CanonicalName);
                try { Commands.Checkout(_repo, local); }
                catch (LibGit2SharpException) { _repo.Branches.Remove(local); throw; }   // (it could not be switched to: no branch is left behind)
                return name;
            }
            catch (LibGit2SharpException ex) { throw Wrap(ex); }
        }

        /// <summary>Switches to a branch, local or of a server (see <see cref="CheckoutRemoteBranch"/>). Returns the name of the local branch.</summary>
        public string CheckoutBranch(string name)
        {
            var branch = _repo.Branches[name] ?? throw new GitException($"There is no branch '{name}'.");
            if (branch.IsRemote) return CheckoutRemoteBranch(name);
            try { Commands.Checkout(_repo, branch); return branch.FriendlyName; }
            catch (LibGit2SharpException ex) { throw Wrap(ex); }
        }

        // ---- branches and tags ---------------------------------------------------------------------------------------------------------------

        /// <summary>Makes a branch at a commit (a branch, a tag or an id), and switches to it when `checkout`.</summary>
        public void CreateBranchAt(string name, string at, bool checkout)
        {
            CheckName(name, "branch");
            if (_repo.Branches[name] != null) throw new GitException($"There is a branch '{name}' already.");
            try
            {
                var branch = _repo.CreateBranch(name, CommitOf(at));
                if (checkout)
                {
                    try { Commands.Checkout(_repo, branch); }
                    catch (LibGit2SharpException) { _repo.Branches.Remove(branch); throw; }
                }
            }
            catch (LibGit2SharpException ex) { throw Wrap(ex); }
        }

        /// <summary>true: everything of the branch is in the current commit (it can be deleted without losing commits).</summary>
        public bool IsMergedIntoHead(string branch)
        {
            var b = _repo.Branches[branch] ?? throw new GitException($"There is no branch '{branch}'.");
            var head = _repo.Head.Tip;
            if (b.Tip == null || head == null) return true;
            return b.Tip.Sha == head.Sha || _repo.ObjectDatabase.FindMergeBase(b.Tip, head)?.Sha == b.Tip.Sha;
        }

        /// <summary>Deletes a local branch. Not the current one; one with commits that are nowhere else only with `force`.</summary>
        public void DeleteBranch(string name, bool force = false)
        {
            var b = _repo.Branches[name] ?? throw new GitException($"There is no branch '{name}'.");
            if (b.IsRemote) throw new GitException($"'{name}' is a branch of a server: delete it on the server.");
            if (b.IsCurrentRepositoryHead) throw new GitException($"'{name}' is the current branch: switch to another one first.");
            if (!force && !IsMergedIntoHead(name) && !(b.TrackedBranch?.Tip is { } up && (up.Sha == b.Tip.Sha || _repo.ObjectDatabase.FindMergeBase(b.Tip, up)?.Sha == b.Tip.Sha)))
                throw new GitException($"'{name}' has commits that are not in the current branch: they would be lost.");
            try { _repo.Branches.Remove(b); }
            catch (LibGit2SharpException ex) { throw Wrap(ex); }
        }

        /// <summary>Deletes a branch on its server (e.g. "origin/feature"), and the copy of it here.</summary>
        public void DeleteRemoteBranch(string remoteBranch, Func<string, GitCredentials?>? credentials = null)
        {
            var b = _repo.Branches[remoteBranch];
            if (b == null || !b.IsRemote) throw new GitException($"There is no branch '{remoteBranch}' of a server.");
            string remoteName = b.RemoteName ?? remoteBranch.Split('/')[0];
            string shortName = remoteBranch.Substring(remoteName.Length + 1);
            try
            {
                var remote = _repo.Network.Remotes[remoteName] ?? throw new GitException($"There is no server '{remoteName}'.");
                _repo.Network.Push(remote, ":refs/heads/" + shortName, new PushOptions { CredentialsProvider = Provider(credentials) });
                if (_repo.Branches[remoteBranch] is { } still) _repo.Branches.Remove(still);
            }
            catch (LibGit2SharpException ex) { throw new GitException(Describe(ex), ex); }
        }

        /// <summary>Makes a tag at a commit: with a message an annotated one (with your name), else a lightweight one.</summary>
        public void CreateTag(string name, string at, string? message = null)
        {
            CheckName(name, "tag");
            if (_repo.Tags[name] != null) throw new GitException($"There is a tag '{name}' already.");
            try
            {
                var commit = CommitOf(at);
                if (string.IsNullOrWhiteSpace(message)) _repo.Tags.Add(name, commit);
                else _repo.Tags.Add(name, commit, Me(), message.Trim() + "\n");
            }
            catch (LibGit2SharpException ex) { throw Wrap(ex); }
        }

        public void DeleteTag(string name)
        {
            if (_repo.Tags[name] == null) throw new GitException($"There is no tag '{name}'.");
            try { _repo.Tags.Remove(name); }
            catch (LibGit2SharpException ex) { throw Wrap(ex); }
        }

        /// <summary>Sends a tag to the server of the current branch (or the first one).</summary>
        public void PushTag(string name, Func<string, GitCredentials?>? credentials = null)
        {
            var tag = _repo.Tags[name] ?? throw new GitException($"There is no tag '{name}'.");
            try
            {
                var remote = _repo.Network.Remotes[RemoteNameOfCurrent()];
                _repo.Network.Push(remote, tag.CanonicalName + ":" + tag.CanonicalName, new PushOptions { CredentialsProvider = Provider(credentials) });
            }
            catch (LibGit2SharpException ex) { throw new GitException(Describe(ex), ex); }
        }

        // ---- merge, cherry-pick, revert ------------------------------------------------------------------------------------------------------

        /// <summary>Joins a branch (or a commit) into the current branch: fast-forward when it can, else a commit that joins them. Conflicts stop it: solve them, stage the files, commit.</summary>
        public GitApplyResult Merge(string what)
        {
            RequireNoOperation("merge");
            if (!HasCommits) throw new GitException("The current branch has no commit yet.");
            try
            {
                var commit = CommitOf(what);
                string label = _repo.Branches[what] is { } b ? (b.IsRemote ? $"remote-tracking branch '{b.FriendlyName}'" : $"branch '{b.FriendlyName}'") : $"commit '{commit.Sha.Substring(0, 7)}'";
                var me = Me();
                var options = new MergeOptions { CommitOnSuccess = false, FastForwardStrategy = FastForwardStrategy.Default };
                var result = _repo.Branches[what] is { } branch ? _repo.Merge(branch, me, options) : _repo.Merge(commit, me, options);   // (a branch: git's message names it, also in .git/MERGE_MSG)
                if (result.Status == MergeStatus.NonFastForward)
                {
                    // joined without conflicts: the commit with git's usual message
                    string message = $"Merge {label}" + (IsDetached ? "" : $" into {_repo.Head.FriendlyName}");
                    _repo.Commit(message, me, me);
                }
                return result.Status switch
                {
                    MergeStatus.UpToDate => GitApplyResult.UpToDate,
                    MergeStatus.FastForward => GitApplyResult.FastForward,
                    MergeStatus.NonFastForward => GitApplyResult.Committed,
                    _ => GitApplyResult.Conflicts,
                };
            }
            catch (LibGit2SharpException ex) { throw Wrap(ex); }
        }

        /// <summary>Makes a new commit on the current branch with the changes of `sha` (of a merge: against its first parent). Conflicts stop it: solve them, stage the files, commit.</summary>
        public GitApplyResult CherryPick(string sha)
        {
            RequireNoOperation("cherry-pick");
            if (!HasCommits) throw new GitException("The current branch has no commit yet.");
            try
            {
                var commit = CommitOf(sha);
                var options = new CherryPickOptions { CommitOnSuccess = true };
                if (commit.Parents.Count() > 1) options.Mainline = 1;
                var result = _repo.CherryPick(commit, Me(), options);
                return result.Status == CherryPickStatus.Conflicts ? GitApplyResult.Conflicts : GitApplyResult.Committed;
            }
            catch (EmptyCommitException) { return GitApplyResult.UpToDate; }
            catch (LibGit2SharpException ex) { throw Wrap(ex); }
        }

        /// <summary>Makes a new commit that undoes the changes of `sha`. Conflicts stop it: solve them, stage the files, commit.</summary>
        public GitApplyResult Revert(string sha)
        {
            RequireNoOperation("revert");
            if (!HasCommits) throw new GitException("The current branch has no commit yet.");
            try
            {
                var commit = CommitOf(sha);
                var options = new RevertOptions { CommitOnSuccess = true };
                if (commit.Parents.Count() > 1) options.Mainline = 1;
                var result = _repo.Revert(commit, Me(), options);
                return result.Status == RevertStatus.Conflicts ? GitApplyResult.Conflicts : result.Status == RevertStatus.NothingToRevert ? GitApplyResult.UpToDate : GitApplyResult.Committed;
            }
            catch (EmptyCommitException) { return GitApplyResult.UpToDate; }
            catch (LibGit2SharpException ex) { throw Wrap(ex); }
        }

        /// <summary>The message git prepared for the commit that finishes a merge, a cherry-pick or a revert (.git/MERGE_MSG), null when there is none.</summary>
        public string? PreparedMessage()
        {
            string file = Path.Combine(_repo.Info.Path, "MERGE_MSG");
            try { return File.Exists(file) ? string.Join("\n", File.ReadAllText(file).Replace("\r\n", "\n").Split('\n').Where(l => !l.StartsWith('#'))).Trim() : null; }
            catch (IOException) { return null; }
        }

        /// <summary>Finishes a merge, a cherry-pick or a revert whose conflicts are solved and staged: the commit with the prepared message (or `message`).</summary>
        public GitCommitInfo FinishOperation(string? message = null)
        {
            var op = Operation;
            if (op is GitOperationKind.None or GitOperationKind.Rebase) throw new GitException(op == GitOperationKind.None ? "Nothing is to be finished." : "A rebase is continued with Continue.");
            if (!_repo.Index.IsFullyMerged) throw new GitException("Some files still have conflicts: solve them and stage them first.");
            string text = !string.IsNullOrWhiteSpace(message) ? message : PreparedMessage() ?? "";
            if (string.IsNullOrWhiteSpace(text)) text = op == GitOperationKind.Merge ? "Merge" : OperationName(op);
            try
            {
                var me = Me();
                return Info(_repo.Commit(text.Trim(), me, me, new CommitOptions { AllowEmptyCommit = true }));
            }
            catch (LibGit2SharpException ex) { throw Wrap(ex); }
        }

        /// <summary>Stops a merge, a cherry-pick or a revert (or a rebase, see <see cref="RebaseAbort"/>): the files go back to the current commit, what it changed is thrown away.</summary>
        public void AbortOperation()
        {
            var op = Operation;
            if (op == GitOperationKind.Rebase) { RebaseAbort(); return; }
            if (op == GitOperationKind.None) return;
            try { _repo.Reset(ResetMode.Hard, _repo.Head.Tip); }   // (a hard reset also ends the state of the operation: MERGE_HEAD, CHERRY_PICK_HEAD, ...)
            catch (LibGit2SharpException ex) { throw Wrap(ex); }
        }

        // ---- rebase --------------------------------------------------------------------------------------------------------------------------

        private Identity MyIdentity() { var s = Me(); return new Identity(s.Name, s.Email); }

        private GitRebaseResult ResultOf(RebaseResult r)
        {
            if (r.Status == RebaseStatus.Complete) return new GitRebaseResult(true, (int)r.TotalStepCount, (int)r.TotalStepCount, null, (int)r.CompletedStepCount);
            var step = r.CurrentStepInfo ?? _repo.Rebase.GetCurrentStepInfo();
            return new GitRebaseResult(false, (int)r.CompletedStepCount + 1, (int)r.TotalStepCount, step?.Commit is { } c ? $"{c.Sha.Substring(0, 7)} {c.MessageShort}" : null, (int)r.CompletedStepCount);
        }

        /// <summary>
        /// Puts the commits of the current branch that `onto` (a branch, a server's branch, a tag or a commit) does not have on top of it, one after the other (not interactive). The files must not
        /// have changes (untracked files are fine). Conflicts stop it at that commit: solve them, stage the files, then <see cref="RebaseContinue"/>, <see cref="RebaseSkip"/> or <see cref="RebaseAbort"/>.
        /// </summary>
        public GitRebaseResult Rebase(string onto)
        {
            RequireNoOperation("rebase");
            if (!HasCommits) throw new GitException("The current branch has no commit yet.");
            if (HasUncommittedChanges()) throw new GitException("A rebase needs a clean working folder: commit, stash or discard your changes first.");
            var me = MyIdentity();
            Branch? temporary = null;
            try
            {
                var upstream = _repo.Branches[onto];
                if (upstream == null)
                {
                    // a tag or a commit: libgit2 takes a branch, so a branch of the editor's points at it while the rebase starts (it remembers the commit, not the branch)
                    var target = CommitOf(onto);
                    string name = "fire-rebase-onto-" + target.Sha.Substring(0, 12);
                    temporary = _repo.Branches[name] ?? _repo.CreateBranch(name, target);
                    upstream = temporary;
                }
                var head = _repo.Head.Tip;
                if (head.Sha == upstream.Tip.Sha || _repo.ObjectDatabase.FindMergeBase(head, upstream.Tip)?.Sha == upstream.Tip.Sha)
                    return new GitRebaseResult(true, 0, 0, null, 0);   // (it has everything of `onto` already)
                var result = _repo.Rebase.Start(IsDetached ? null : _repo.Head, upstream, null, me, new RebaseOptions());
                return ResultOf(result);
            }
            catch (LibGit2SharpException ex) { throw Wrap(ex); }
            finally
            {
                if (temporary != null) { try { _repo.Branches.Remove(temporary); } catch (LibGit2SharpException) { } }
            }
        }

        /// <summary>Goes on with a rebase that stopped: the solved and staged files become the commit of this step, then the next steps.</summary>
        public GitRebaseResult RebaseContinue()
        {
            if (Operation != GitOperationKind.Rebase) throw new GitException("No rebase is going on.");
            if (!_repo.Index.IsFullyMerged) throw new GitException("Some files still have conflicts (!): solve them and stage them (+) first.");
            try { return ResultOf(_repo.Rebase.Continue(MyIdentity(), new RebaseOptions())); }
            catch (LibGit2SharpException ex) { throw Wrap(ex); }
        }

        /// <summary>Leaves the commit of the step out (its changes are thrown away) and goes on with the next steps.</summary>
        public GitRebaseResult RebaseSkip()
        {
            if (Operation != GitOperationKind.Rebase) throw new GitException("No rebase is going on.");
            try
            {
                // back to the commit that the rebase made last - not with a reset, which would also end the rebase (git_repository_state_cleanup removes .git/rebase-merge)
                var head = _repo.Head.Tip;
                var states = _repo.RetrieveStatus(new StatusOptions { IncludeUntracked = false }).Where(e => e.State != FileStatus.Unaltered).Select(e => (Path: e.FilePath, e.State)).ToList();
                _repo.Index.Replace(head.Tree);
                _repo.Index.Write();
                var restore = new List<string>();
                foreach (var (path, state) in states)
                {
                    if (head[path] == null)
                    {
                        // a file that only the step brought (it is not in the commit): it goes
                        string full = Path.Combine(RootPath, path);
                        if (File.Exists(full)) File.Delete(full);
                    }
                    else restore.Add(path);
                }
                if (restore.Count > 0) _repo.CheckoutPaths(head.Sha, restore, new CheckoutOptions { CheckoutModifiers = CheckoutModifiers.Force });
                return ResultOf(_repo.Rebase.Continue(MyIdentity(), new RebaseOptions()));
            }
            catch (LibGit2SharpException ex) { throw Wrap(ex); }
            catch (IOException ex) { throw new GitException(ex.Message, ex); }
        }

        /// <summary>Stops the rebase: the branch and the files are as before it started.</summary>
        public void RebaseAbort()
        {
            if (Operation != GitOperationKind.Rebase) throw new GitException("No rebase is going on.");
            try { _repo.Rebase.Abort(); }
            catch (LibGit2SharpException ex) { throw Wrap(ex); }
        }

        // ---- reset ---------------------------------------------------------------------------------------------------------------------------

        /// <summary>What a reset of the current branch to `target` would do: the commits that leave the branch (and whether another branch or tag still has them), the changed files.</summary>
        public GitResetPreview PreviewReset(string target)
        {
            try
            {
                var to = CommitOf(target);
                var head = _repo.Head.Tip ?? throw new GitException("The current branch has no commit yet.");
                var leaving = _repo.Commits.QueryBy(new CommitFilter { IncludeReachableFrom = head, ExcludeReachableFrom = to, SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time }).Take(1000).ToList();
                var others = new List<Commit>();
                foreach (var b in _repo.Branches) if (b.Tip != null && !b.IsCurrentRepositoryHead && !b.FriendlyName.EndsWith("/HEAD", StringComparison.Ordinal)) others.Add(b.Tip);
                foreach (var t in _repo.Tags) if (t.PeeledTarget is Commit c) others.Add(c);
                var elsewhere = new HashSet<string>(StringComparer.Ordinal);
                if (leaving.Count > 0 && others.Count > 0)
                    foreach (var c in _repo.Commits.QueryBy(new CommitFilter { IncludeReachableFrom = others, ExcludeReachableFrom = to })) elsewhere.Add(c.Sha);
                int newCommits = _repo.Commits.QueryBy(new CommitFilter { IncludeReachableFrom = to, ExcludeReachableFrom = head }).Take(10000).Count();
                var changed = _repo.RetrieveStatus(new StatusOptions { IncludeUntracked = false }).Where(e => e.State != FileStatus.Unaltered).Select(e => e.FilePath.Replace('\\', '/')).ToList();
                return new GitResetPreview(BranchName, to.Sha, leaving.Select(c => (Info(c), elsewhere.Contains(c.Sha))).ToList(), changed, newCommits);
            }
            catch (LibGit2SharpException ex) { throw Wrap(ex); }
        }

        /// <summary>Moves the current branch to `target` (git reset --soft/--mixed/--hard). Hard: the files are as in the commit, all changes of tracked files are lost (untracked files stay).</summary>
        public void Reset(string target, GitResetMode mode)
        {
            var op = Operation;
            if (op == GitOperationKind.Rebase) throw new GitException("A rebase is not finished: continue or abort it first.");
            if (mode == GitResetMode.Soft && op != GitOperationKind.None) throw new GitException($"A {OperationName(op)} is not finished: a soft reset cannot be done now (abort it first).");
            try
            {
                var to = CommitOf(target);
                _repo.Reset(mode switch { GitResetMode.Soft => ResetMode.Soft, GitResetMode.Mixed => ResetMode.Mixed, _ => ResetMode.Hard }, to);
            }
            catch (LibGit2SharpException ex) { throw Wrap(ex); }
        }

        /// <summary>The server's branch that the current branch follows ("origin/main"), null when it follows none.</summary>
        public string? Upstream => _repo.Head.TrackedBranch?.Tip != null ? _repo.Head.TrackedBranch.FriendlyName : null;

        // ---- stash ---------------------------------------------------------------------------------------------------------------------------

        public IReadOnlyList<GitStashInfo> Stashes() => _repo.Stashes.Select((s, i) => new GitStashInfo(i, s.Message, s.WorkTree.Sha, s.WorkTree.Author.When)).ToList();

        /// <summary>Puts the changes (with `includeUntracked` the new files too) on the stash; the files are as in the last commit afterwards. False: there was nothing to put away.</summary>
        public bool StashSave(string? message = null, bool includeUntracked = true)
        {
            if (!HasCommits) throw new GitException("A stash needs a first commit.");
            try
            {
                var stash = _repo.Stashes.Add(Me(), string.IsNullOrWhiteSpace(message) ? null : message.Trim(), includeUntracked ? StashModifiers.IncludeUntracked : StashModifiers.Default);
                return stash != null;
            }
            catch (LibGit2SharpException ex) { throw Wrap(ex); }
        }

        private GitStashResult ApplyStash(int index, bool pop)
        {
            if (index < 0 || index >= _repo.Stashes.Count()) throw new GitException($"There is no stash {index}.");
            try
            {
                var options = new StashApplyOptions { ApplyModifiers = StashApplyModifiers.Default };
                var status = pop ? _repo.Stashes.Pop(index, options) : _repo.Stashes.Apply(index, options);
                return status switch
                {
                    StashApplyStatus.Applied => GitStashResult.Applied,
                    StashApplyStatus.Conflicts => GitStashResult.Conflicts,
                    StashApplyStatus.UncommittedChanges => throw new GitException("Changes in your files would be overwritten by the stash: commit or discard them first."),
                    _ => throw new GitException($"The stash {index} could not be applied ({status})."),
                };
            }
            catch (LibGit2SharpException ex) { throw Wrap(ex); }
        }

        /// <summary>Brings the changes of a stash back into the files; the stash stays.</summary>
        public GitStashResult StashApply(int index) => ApplyStash(index, pop: false);

        /// <summary>Brings the changes of a stash back and removes it from the stash (with conflicts it stays, as with git).</summary>
        public GitStashResult StashPop(int index) => ApplyStash(index, pop: true);

        /// <summary>Removes a stash (its changes are lost).</summary>
        public void StashDrop(int index)
        {
            if (index < 0 || index >= _repo.Stashes.Count()) throw new GitException($"There is no stash {index}.");
            try { _repo.Stashes.Remove(index); }
            catch (LibGit2SharpException ex) { throw Wrap(ex); }
        }

        /// <summary>What a stash changed, as a unified diff (against the commit it was made on).</summary>
        public string DiffStash(int index)
        {
            var stash = _repo.Stashes.ElementAtOrDefault(index) ?? throw new GitException($"There is no stash {index}.");
            try
            {
                var sb = new StringBuilder(_repo.Diff.Compare<Patch>(stash.Base.Tree, stash.WorkTree.Tree).Content);
                if (stash.Untracked != null) sb.Append(_repo.Diff.Compare<Patch>(null, stash.Untracked.Tree).Content);
                return sb.ToString();
            }
            catch (LibGit2SharpException ex) { throw Wrap(ex); }
        }

        // ---- servers -------------------------------------------------------------------------------------------------------------------------

        /// <summary>Fetches from every server of the repository (the branches that are gone there are removed here).</summary>
        public void FetchAll(Func<string, GitCredentials?>? credentials = null)
        {
            if (!_repo.Network.Remotes.Any()) throw new GitException("This repository has no server yet: add one first (its https:// address).");
            try
            {
                foreach (var remote in _repo.Network.Remotes.ToList())
                    Commands.Fetch(_repo, remote.Name, Array.Empty<string>(), new FetchOptions { CredentialsProvider = Provider(credentials), Prune = true }, null);
            }
            catch (LibGit2SharpException ex) { throw new GitException(Describe(ex), ex); }
        }
    }
}
