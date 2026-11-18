using LibGit2Sharp;

namespace fire.Git
{
    /// <summary>What git is in the middle of: a merge, a rebase, a cherry-pick or a revert that stopped at conflicts (or waits for its commit).</summary>
    public enum GitOperationKind { None, Merge, Rebase, CherryPick, Revert, Other }

    /// <summary>A local branch, a branch of a server (`Remote` is the server's name, `Name` e.g. "origin/main"), or a tag. `Upstream`: the server's branch a local branch follows.</summary>
    public sealed record GitRefInfo(string Name, string CanonicalName, string Sha, bool IsCurrent, string? Remote, string? Upstream, int Ahead, int Behind, string? Message = null)
    {
        /// <summary>The name without the server ("main" of "origin/main").</summary>
        public string ShortName => Remote != null && Name.StartsWith(Remote + "/", StringComparison.Ordinal) ? Name.Substring(Remote.Length + 1) : Name;
    }

    public sealed record GitStashInfo(int Index, string Message, string Sha, DateTimeOffset When);

    /// <summary>
    /// The branches, the servers' branches (by server), the tags and the stashes of a repository, what is checked out (`Head`: the branch, or the short id when the head is detached), and the
    /// operation that is not finished (with the step of a rebase: `RebaseStep` of `RebaseSteps`, the commit it stopped at).
    /// </summary>
    public sealed record GitRefs(string Head, string? HeadSha, bool IsDetached, IReadOnlyList<GitRefInfo> Branches, IReadOnlyDictionary<string, IReadOnlyList<GitRefInfo>> Remotes,
        IReadOnlyList<GitRefInfo> Tags, IReadOnlyList<GitStashInfo> Stashes, GitOperationKind Operation, int RebaseStep = 0, int RebaseSteps = 0, string? RebaseStopped = null);

    /// <summary>A commit in the log of the Repository view: the commit, its parents (first parent first), the author's address and the names of the branches and tags that point at it.</summary>
    public sealed record GitLogEntry(GitCommitInfo Commit, IReadOnlyList<string> Parents, string Email, IReadOnlyList<string> Refs);

    /// <summary>What the log shows: commits whose message or id contains `Text`, whose author (name or address) contains `Author`, that changed `Path` (a file or a folder, relative to the repository).</summary>
    public sealed record GitLogFilter(string? Text = null, string? Author = null, string? Path = null)
    {
        public bool IsEmpty => string.IsNullOrWhiteSpace(Text) && string.IsNullOrWhiteSpace(Author) && string.IsNullOrWhiteSpace(Path);
    }

    /// <summary>A page of the log: the entries, and where the next page starts (`Next`, the number of commits walked; null: there are no more).</summary>
    public sealed record GitLogPage(IReadOnlyList<GitLogEntry> Entries, int? Next);

    /// <summary>A file that a commit changed (against its first parent): its path, the path before a rename, the state (Added, Modified, Deleted, Renamed).</summary>
    public sealed record GitCommitFile(string Path, string? OldPath, GitFileState State);

    public sealed record GitCommitDetails(GitCommitInfo Commit, string AuthorEmail, string Committer, string CommitterEmail, DateTimeOffset CommitterWhen, IReadOnlyList<string> Parents,
        IReadOnlyList<GitCommitFile> Files, IReadOnlyList<string> Refs);

    public sealed partial class GitRepository
    {
        /// <summary>The operation that is not finished.</summary>
        public GitOperationKind Operation => _repo.Info.CurrentOperation switch
        {
            CurrentOperation.None => GitOperationKind.None,
            CurrentOperation.Merge => GitOperationKind.Merge,
            CurrentOperation.Rebase or CurrentOperation.RebaseInteractive or CurrentOperation.RebaseMerge or CurrentOperation.ApplyMailbox or CurrentOperation.ApplyMailboxOrRebase => GitOperationKind.Rebase,
            CurrentOperation.CherryPick or CurrentOperation.CherryPickSequence => GitOperationKind.CherryPick,
            CurrentOperation.Revert or CurrentOperation.RevertSequence => GitOperationKind.Revert,
            _ => GitOperationKind.Other,
        };

        public bool IsDetached => _repo.Info.IsHeadDetached;

        /// <summary>The branches, the servers' branches, the tags, the stashes and the state (the left side of the Repository view).</summary>
        public GitRefs Refs()
        {
            try
            {
                var locals = new List<GitRefInfo>();
                var remotes = new Dictionary<string, List<GitRefInfo>>(StringComparer.Ordinal);
                foreach (var r in _repo.Network.Remotes) remotes[r.Name] = new List<GitRefInfo>();
                foreach (var b in _repo.Branches)
                {
                    if (b.Tip == null) continue;
                    if (b.IsRemote)
                    {
                        if (b.FriendlyName.EndsWith("/HEAD", StringComparison.Ordinal)) continue;
                        string remote = b.RemoteName ?? b.FriendlyName.Split('/')[0];
                        if (!remotes.TryGetValue(remote, out var list)) remotes[remote] = list = new List<GitRefInfo>();
                        list.Add(new GitRefInfo(b.FriendlyName, b.CanonicalName, b.Tip.Sha, false, remote, null, 0, 0));
                    }
                    else
                    {
                        var tracked = b.TrackedBranch;
                        bool hasUpstream = tracked != null && tracked.Tip != null;
                        locals.Add(new GitRefInfo(b.FriendlyName, b.CanonicalName, b.Tip.Sha, b.IsCurrentRepositoryHead, null, tracked?.FriendlyName,
                            hasUpstream ? b.TrackingDetails.AheadBy ?? 0 : 0, hasUpstream ? b.TrackingDetails.BehindBy ?? 0 : 0));
                    }
                }
                var tags = new List<GitRefInfo>();
                foreach (var t in _repo.Tags)
                {
                    var target = t.PeeledTarget as Commit;
                    if (target == null) continue;
                    tags.Add(new GitRefInfo(t.FriendlyName, t.CanonicalName, target.Sha, false, null, null, 0, 0, t.IsAnnotated ? t.Annotation.Message.TrimEnd() : null));
                }
                var stashes = _repo.Stashes.Select((s, i) => new GitStashInfo(i, s.Message, s.WorkTree.Sha, s.WorkTree.Author.When)).ToList();
                var cmp = StringComparer.OrdinalIgnoreCase;
                int step = 0, steps = 0;
                string? stopped = null;
                var op = Operation;
                if (op == GitOperationKind.Rebase)
                {
                    try
                    {
                        steps = (int)_repo.Rebase.GetTotalStepCount();
                        step = (int)_repo.Rebase.GetCurrentStepIndex() + 1;
                        var info = _repo.Rebase.GetCurrentStepInfo();
                        if (info?.Commit != null) stopped = $"{info.Commit.Sha.Substring(0, 7)} {info.Commit.MessageShort}";
                    }
                    catch (LibGit2SharpException) { }   // (a rebase that git itself started in another way: no steps to show)
                }
                return new GitRefs(BranchName, HeadSha, IsDetached,
                    locals.OrderBy(b => b.Name, cmp).ToList(),
                    remotes.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<GitRefInfo>)kv.Value.OrderBy(b => b.Name, cmp).ToList()),
                    tags.OrderBy(t => t.Name, cmp).ToList(), stashes, op, step, steps, stopped);
            }
            catch (LibGit2SharpException ex) { throw new GitException(ex.Message, ex); }
        }

        /// <summary>The names to show at each commit: the branches, the servers' branches and the tags that point at it ("HEAD" when the head is detached there).</summary>
        private Dictionary<string, List<string>> RefNames()
        {
            var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            void Add(string sha, string name) { if (!map.TryGetValue(sha, out var l)) map[sha] = l = new List<string>(); l.Add(name); }
            if (IsDetached && _repo.Head.Tip != null) Add(_repo.Head.Tip.Sha, "HEAD");
            foreach (var b in _repo.Branches)
                if (b.Tip != null && !b.FriendlyName.EndsWith("/HEAD", StringComparison.Ordinal)) Add(b.Tip.Sha, b.FriendlyName);
            foreach (var t in _repo.Tags)
                if (t.PeeledTarget is Commit c) Add(c.Sha, "tag: " + t.FriendlyName);
            return map;
        }

        /// <summary>
        /// A page of the log of the whole repository (all branches, the servers' branches, the tags and the head), newest first and each commit before its parents (for the graph), from the
        /// commit `start` of the walk on: `take` entries that pass the filter. A path filter compares the file or folder in the commit with its first parent (no diff, so it is quick).
        /// </summary>
        public GitLogPage History(GitLogFilter? filter = null, int start = 0, int take = 300)
        {
            if (!HasCommits && !_repo.Branches.Any(b => b.Tip != null)) return new GitLogPage(Array.Empty<GitLogEntry>(), null);
            try
            {
                var tips = new List<object>();
                if (_repo.Head.Tip != null) tips.Add(_repo.Head.Tip);
                foreach (var b in _repo.Branches) if (b.Tip != null) tips.Add(b.Tip);
                foreach (var t in _repo.Tags) if (t.PeeledTarget is Commit c) tips.Add(c);
                var query = _repo.Commits.QueryBy(new CommitFilter { IncludeReachableFrom = tips, SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time });
                var refs = RefNames();
                string text = filter?.Text?.Trim() ?? "", author = filter?.Author?.Trim() ?? "", path = filter?.Path?.Trim() ?? "";
                if (path.Length > 0 && System.IO.Path.IsPathRooted(path)) path = RelativeOf(path);
                path = path.Replace('\\', '/').Trim('/');
                var entries = new List<GitLogEntry>();
                int index = 0;
                foreach (var c in query)
                {
                    if (index++ < start) continue;
                    if (entries.Count >= take) return new GitLogPage(entries, index - 1);
                    if (text.Length > 0 && !c.Message.Contains(text, StringComparison.OrdinalIgnoreCase) && !c.Sha.StartsWith(text, StringComparison.OrdinalIgnoreCase)) continue;
                    if (author.Length > 0 && !c.Author.Name.Contains(author, StringComparison.OrdinalIgnoreCase) && !c.Author.Email.Contains(author, StringComparison.OrdinalIgnoreCase)) continue;
                    if (path.Length > 0 && !Touches(c, path)) continue;
                    entries.Add(new GitLogEntry(Info(c), c.Parents.Select(p => p.Sha).ToList(), c.Author.Email, refs.TryGetValue(c.Sha, out var names) ? names : (IReadOnlyList<string>)Array.Empty<string>()));
                }
                return new GitLogPage(entries, null);
            }
            catch (LibGit2SharpException ex) { throw new GitException(ex.Message, ex); }
        }

        private static bool Touches(Commit c, string path)
        {
            string? Id(Commit commit) => commit[path]?.Target.Sha;
            string? mine = Id(c);
            var parent = c.Parents.FirstOrDefault();
            return parent == null ? mine != null : mine != Id(parent);
        }

        /// <summary>The commit with its message, author and committer, parents, the files it changed (against its first parent) and the names that point at it.</summary>
        public GitCommitDetails CommitDetails(string sha)
        {
            try
            {
                var c = _repo.Lookup<Commit>(sha) ?? throw new GitException($"There is no commit {sha}.");
                var parent = c.Parents.FirstOrDefault();
                var files = new List<GitCommitFile>();
                foreach (var change in _repo.Diff.Compare<TreeChanges>(parent?.Tree, c.Tree))
                {
                    var state = change.Status switch
                    {
                        ChangeKind.Added or ChangeKind.Copied => GitFileState.Added,
                        ChangeKind.Deleted => GitFileState.Deleted,
                        ChangeKind.Renamed => GitFileState.Renamed,
                        _ => GitFileState.Modified,
                    };
                    files.Add(new GitCommitFile(change.Path.Replace('\\', '/'), change.Status == ChangeKind.Renamed ? change.OldPath.Replace('\\', '/') : null, state));
                }
                files.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path));
                var refs = RefNames();
                return new GitCommitDetails(Info(c), c.Author.Email, c.Committer.Name, c.Committer.Email, c.Committer.When, c.Parents.Select(p => p.Sha).ToList(), files,
                    refs.TryGetValue(c.Sha, out var names) ? names : Array.Empty<string>());
            }
            catch (LibGit2SharpException ex) { throw new GitException(ex.Message, ex); }
        }

        /// <summary>What a commit changed in one file (against its first parent), as a unified diff.</summary>
        public string DiffCommitFile(string sha, string path, string? oldPath = null)
        {
            try
            {
                var c = _repo.Lookup<Commit>(sha) ?? throw new GitException($"There is no commit {sha}.");
                var paths = oldPath == null ? new[] { path } : new[] { path, oldPath };
                return _repo.Diff.Compare<Patch>(c.Parents.FirstOrDefault()?.Tree, c.Tree, paths).Content;
            }
            catch (LibGit2SharpException ex) { throw new GitException(ex.Message, ex); }
        }

        /// <summary>The full id of a commit, a branch, a tag or a short id; null when there is none.</summary>
        public string? Resolve(string what)
        {
            try { return (_repo.Lookup(what) as Commit ?? (_repo.Lookup(what)?.Peel<Commit>()))?.Sha ?? _repo.Branches[what]?.Tip?.Sha; }
            catch (LibGit2SharpException) { return null; }
        }
    }
}
