using System.Text;
using LibGit2Sharp;

namespace fire.Git
{
    /// <summary>What is wrong with a git command, in words for the user.</summary>
    public sealed class GitException : Exception
    {
        public GitException(string message, Exception? inner = null) : base(message, inner) { }
    }

    /// <summary>The state of a file as the editor shows it.</summary>
    public enum GitFileState { Unmodified, Untracked, Added, Modified, Deleted, Renamed, Conflicted, Ignored }

    /// <summary>One changed file: its state in the index (what the next commit has) and in the working folder (what is not staged yet).</summary>
    public sealed record GitChange(string Path, string FullPath, GitFileState Staged, GitFileState Unstaged)
    {
        public bool IsConflicted => Staged == GitFileState.Conflicted || Unstaged == GitFileState.Conflicted;
        /// <summary>The state to show for the file: a conflict first, then what is not staged, then what is staged.</summary>
        public GitFileState Combined => IsConflicted ? GitFileState.Conflicted : Unstaged != GitFileState.Unmodified ? Unstaged : Staged;
    }

    public sealed record GitCommitInfo(string Sha, string Message, string Author, DateTimeOffset When)
    {
        public string ShortSha => Sha.Length > 7 ? Sha.Substring(0, 7) : Sha;
        /// <summary>The first line of the message.</summary>
        public string Summary => Message.Split('\n')[0].Trim();
    }

    public sealed record GitBranchInfo(string Name, bool IsCurrent, bool IsRemote, int Ahead, int Behind, bool HasUpstream);

    /// <summary>A user name and a password - for GitHub and most servers a token - for a server that asks (https only).</summary>
    public sealed record GitCredentials(string Username, string Password);

    /// <summary>How a pull ended.</summary>
    public enum GitPullResult { UpToDate, FastForward, Merged, Conflicts }

    /// <summary>
    /// A git repository for the editor (docs/GIT.md), over LibGit2Sharp. `Discover` finds the repository a path is in, `Init` makes one. Paths that are given and returned are full paths in the working
    /// folder; <see cref="GitChange.Path"/> is relative to it (with `/`). A server that asks for a user name and a password (https) is served by the `credentials` function that the caller gives:
    /// it gets the address and returns the credentials (null: no).
    /// </summary>
    public sealed class GitRepository : IDisposable
    {
        private readonly Repository _repo;

        /// <summary>The text of the `.gitignore` of a new repository: what a build writes.</summary>
        public const string DefaultIgnore = "# build results and temporary files of fire\nbin/\nobj/\n*.fpk\n.vs/\n";

        public string RootPath { get; }

        /// <summary>null when git can be used; else why not (the native library of libgit2 could not be loaded - an operating system or processor that the package does not cover).</summary>
        public static string? UnavailableReason { get; } = Probe();

        private static string? Probe()
        {
            try { _ = Repository.IsValid(Path.GetTempPath()); return null; }
            catch (Exception ex) when (ex is TypeInitializationException or DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or PlatformNotSupportedException)
            {
                return "Git is not available on this machine: " + (ex.InnerException?.Message ?? ex.Message);
            }
        }

        private static void Require()
        {
            if (UnavailableReason != null) throw new GitException(UnavailableReason);
        }

        private GitRepository(Repository repo)
        {
            _repo = repo;
            RootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repo.Info.WorkingDirectory ?? throw new GitException("A repository without a working folder (a bare one) cannot be used here.")));
        }

        public void Dispose() => _repo.Dispose();

        // ---- opening --------------------------------------------------------------------------------------------------------------------------

        /// <summary>The working folder of the repository that `path` (a file or folder) is in, null if there is none.</summary>
        public static string? Discover(string path)
        {
            if (UnavailableReason != null) return null;
            try
            {
                string full = Path.GetFullPath(path);
                string? start = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
                while (start != null && !Directory.Exists(start)) start = Path.GetDirectoryName(start);   // a file that is not there yet (a new document)
                if (start == null) return null;
                string? git = Repository.Discover(start);
                if (git == null) return null;
                using var repo = new Repository(git);
                return repo.Info.WorkingDirectory is { } wd ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(wd)) : null;
            }
            catch (Exception ex) when (ex is LibGit2SharpException or ArgumentException or IOException or UnauthorizedAccessException) { return null; }
        }

        public static GitRepository Open(string path)
        {
            Require();
            string? root = Discover(path);
            if (root == null) throw new GitException($"'{path}' is not in a git repository.");
            try { return new GitRepository(new Repository(root)); }
            catch (LibGit2SharpException ex) { throw new GitException(ex.Message, ex); }
        }

        /// <summary>Makes a repository in `folder` (it is created when it does not exist), with a `.gitignore` for the files that a build writes (when there is none).</summary>
        public static GitRepository Init(string folder, bool writeIgnore = true)
        {
            Require();
            try
            {
                Directory.CreateDirectory(folder);
                string path = Repository.Init(folder);
                var repo = new Repository(path);
                var result = new GitRepository(repo);
                string ignore = Path.Combine(result.RootPath, ".gitignore");
                if (writeIgnore && !File.Exists(ignore)) File.WriteAllText(ignore, DefaultIgnore);
                return result;
            }
            catch (Exception ex) when (ex is LibGit2SharpException or IOException or UnauthorizedAccessException) { throw new GitException(ex.Message, ex); }
        }

        /// <summary>Copies a repository from a server (https or a local folder) into `folder`.</summary>
        public static GitRepository Clone(string url, string folder, Func<string, GitCredentials?>? credentials = null)
        {
            Require();
            CheckUrl(url);
            try
            {
                var options = new CloneOptions();
                options.FetchOptions.CredentialsProvider = Provider(credentials);
                string path = Repository.Clone(url, folder, options);
                return new GitRepository(new Repository(path));
            }
            catch (Exception ex) when (ex is LibGit2SharpException or IOException or UnauthorizedAccessException) { throw new GitException(Describe(ex), ex); }
        }

        // ---- state ----------------------------------------------------------------------------------------------------------------------------

        public bool HasCommits => _repo.Head.Tip != null;

        /// <summary>The name of the current branch; in a detached state the short id of the commit; "(no commits yet)" for a new repository.</summary>
        public string BranchName =>
            _repo.Info.IsHeadDetached ? (_repo.Head.Tip?.Sha.Substring(0, 7) ?? "(detached)") : _repo.Head.FriendlyName;

        /// <summary>true: a merge is not finished (conflicts to solve, or a commit to make).</summary>
        public bool IsMerging => _repo.Info.CurrentOperation == CurrentOperation.Merge;

        private static GitFileState StateOf(FileStatus s, bool index)
        {
            if (s.HasFlag(FileStatus.Conflicted)) return GitFileState.Conflicted;
            if (index)
            {
                if (s.HasFlag(FileStatus.NewInIndex)) return GitFileState.Added;
                if (s.HasFlag(FileStatus.ModifiedInIndex) || s.HasFlag(FileStatus.TypeChangeInIndex)) return GitFileState.Modified;
                if (s.HasFlag(FileStatus.DeletedFromIndex)) return GitFileState.Deleted;
                if (s.HasFlag(FileStatus.RenamedInIndex)) return GitFileState.Renamed;
            }
            else
            {
                if (s.HasFlag(FileStatus.NewInWorkdir)) return GitFileState.Untracked;
                if (s.HasFlag(FileStatus.ModifiedInWorkdir) || s.HasFlag(FileStatus.TypeChangeInWorkdir)) return GitFileState.Modified;
                if (s.HasFlag(FileStatus.DeletedFromWorkdir)) return GitFileState.Deleted;
                if (s.HasFlag(FileStatus.RenamedInWorkdir)) return GitFileState.Renamed;
            }
            return GitFileState.Unmodified;
        }

        /// <summary>The files that are not as in the last commit (untracked ones included, ignored ones not).</summary>
        public IReadOnlyList<GitChange> Status()
        {
            try
            {
                var result = new List<GitChange>();
                var status = _repo.RetrieveStatus(new StatusOptions { IncludeUntracked = true, RecurseUntrackedDirs = true, IncludeIgnored = false });
                foreach (var entry in status)
                {
                    if (entry.State is FileStatus.Unaltered or FileStatus.Ignored) continue;
                    var change = new GitChange(entry.FilePath.Replace('\\', '/'), Path.GetFullPath(Path.Combine(RootPath, entry.FilePath)), StateOf(entry.State, true), StateOf(entry.State, false));
                    if (change.Staged != GitFileState.Unmodified || change.Unstaged != GitFileState.Unmodified) result.Add(change);
                }
                return result.OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (LibGit2SharpException ex) { throw new GitException(ex.Message, ex); }
        }

        /// <summary>The state of every changed file by full path (for marking files and folders in a tree).</summary>
        public Dictionary<string, GitFileState> StateMap()
        {
            var map = new Dictionary<string, GitFileState>(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
            foreach (var c in Status()) map[c.FullPath] = c.Combined;
            return map;
        }

        public bool IsIgnored(string path) => _repo.Ignore.IsPathIgnored(RelativeOf(path));

        private string RelativeOf(string path) => Path.GetRelativePath(RootPath, Path.GetFullPath(path)).Replace('\\', '/');

        // ---- staging and committing ---------------------------------------------------------------------------------------------------------

        public void Stage(IEnumerable<string> paths)
        {
            try { Commands.Stage(_repo, paths.Select(RelativeOf).ToList()); }
            catch (LibGit2SharpException ex) { throw new GitException(ex.Message, ex); }
        }

        public void StageAll()
        {
            try { Commands.Stage(_repo, "*"); }
            catch (LibGit2SharpException ex) { throw new GitException(ex.Message, ex); }
        }

        public void Unstage(IEnumerable<string> paths)
        {
            try { Commands.Unstage(_repo, paths.Select(RelativeOf).ToList()); }
            catch (LibGit2SharpException ex) { throw new GitException(ex.Message, ex); }
        }

        /// <summary>The name and the e-mail address for commits from the configuration of git (the repository's, then the user's); null parts if there are none.</summary>
        public (string? Name, string? Email) Identity()
        {
            try { return (_repo.Config.Get<string>("user.name")?.Value, _repo.Config.Get<string>("user.email")?.Value); }
            catch (LibGit2SharpException) { return (null, null); }
        }

        /// <summary>Remembers the name and the address in the configuration of this repository.</summary>
        public void SetIdentity(string name, string email)
        {
            try
            {
                _repo.Config.Set("user.name", name, ConfigurationLevel.Local);
                _repo.Config.Set("user.email", email, ConfigurationLevel.Local);
            }
            catch (LibGit2SharpException ex) { throw new GitException(ex.Message, ex); }
        }

        /// <summary>Commits what is staged. Without a name and an address (here and in the configuration) it is an error that says so.</summary>
        public GitCommitInfo Commit(string message, string? name = null, string? email = null)
        {
            if (string.IsNullOrWhiteSpace(message)) throw new GitException("The message of the commit is empty.");
            var (configName, configEmail) = Identity();
            name = string.IsNullOrWhiteSpace(name) ? configName : name;
            email = string.IsNullOrWhiteSpace(email) ? configEmail : email;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(email)) throw new GitException("Git needs a name and an e-mail address for the commit.");
            try
            {
                var signature = new Signature(name, email, DateTimeOffset.Now);
                var commit = _repo.Commit(message.Trim(), signature, signature);
                return Info(commit);
            }
            catch (EmptyCommitException) { throw new GitException("Nothing is staged: there is nothing to commit."); }
            catch (LibGit2SharpException ex) { throw new GitException(ex.Message, ex); }
        }

        private static GitCommitInfo Info(Commit c) => new(c.Sha, c.Message.TrimEnd(), c.Author.Name, c.Author.When);

        /// <summary>Puts the files back to the last commit (what is staged and what was changed); an untracked file is deleted. This cannot be undone.</summary>
        public void Discard(IEnumerable<string> paths)
        {
            try
            {
                foreach (var path in paths)
                {
                    string relative = RelativeOf(path);
                    var status = _repo.RetrieveStatus(relative);
                    if (status.HasFlag(FileStatus.NewInWorkdir)) { File.Delete(Path.Combine(RootPath, relative)); continue; }
                    if (status.HasFlag(FileStatus.NewInIndex))
                    {
                        Commands.Unstage(_repo, relative);   // a file that was only added: it is untracked again (the file stays)
                        continue;
                    }
                    if (!HasCommits) continue;
                    Commands.Unstage(_repo, relative);
                    _repo.CheckoutPaths("HEAD", new[] { relative }, new CheckoutOptions { CheckoutModifiers = CheckoutModifiers.Force });
                }
            }
            catch (Exception ex) when (ex is LibGit2SharpException or IOException or UnauthorizedAccessException) { throw new GitException(ex.Message, ex); }
        }

        // ---- history and differences --------------------------------------------------------------------------------------------------------

        /// <summary>The last commits of the current branch, newest first (of one file when `path` is given).</summary>
        public IReadOnlyList<GitCommitInfo> Log(int max = 200, string? path = null)
        {
            if (!HasCommits) return Array.Empty<GitCommitInfo>();
            try
            {
                IEnumerable<Commit> commits = path == null
                    ? _repo.Commits.QueryBy(new CommitFilter { SortBy = CommitSortStrategies.Time })
                    : _repo.Commits.QueryBy(RelativeOf(path)).Select(e => e.Commit);
                return commits.Take(max).Select(Info).ToList();
            }
            catch (KeyNotFoundException) { return Array.Empty<GitCommitInfo>(); }   // LibGit2Sharp: a file that is in no commit
            catch (LibGit2SharpException ex) { throw new GitException(ex.Message, ex); }
        }

        /// <summary>The changes of a file as a unified diff: `staged` what the next commit has over the last one, else what is changed in the folder over what is staged. An untracked file is shown as new.</summary>
        public string Diff(string path, bool staged)
        {
            try
            {
                string relative = RelativeOf(path);
                string full = Path.Combine(RootPath, relative);
                if (!staged && _repo.RetrieveStatus(relative).HasFlag(FileStatus.NewInWorkdir) && File.Exists(full))
                {
                    var sb = new StringBuilder($"diff --git a/{relative} b/{relative}\nnew file\n--- /dev/null\n+++ b/{relative}\n");
                    foreach (var line in File.ReadAllText(full).Replace("\r\n", "\n").Split('\n')) sb.Append('+').Append(line).Append('\n');
                    return sb.ToString();
                }
                var patch = staged
                    ? _repo.Diff.Compare<Patch>(_repo.Head.Tip?.Tree, DiffTargets.Index, new[] { relative })
                    : _repo.Diff.Compare<Patch>(new[] { relative });
                return patch.Content;
            }
            catch (Exception ex) when (ex is LibGit2SharpException or IOException) { throw new GitException(ex.Message, ex); }
        }

        /// <summary>What a commit changed, as a unified diff of all its files (against its first parent).</summary>
        public string DiffCommit(string sha)
        {
            try
            {
                var commit = _repo.Lookup<Commit>(sha) ?? throw new GitException($"There is no commit {sha}.");
                var parent = commit.Parents.FirstOrDefault();
                return _repo.Diff.Compare<Patch>(parent?.Tree, commit.Tree).Content;
            }
            catch (LibGit2SharpException ex) { throw new GitException(ex.Message, ex); }
        }

        // ---- branches ------------------------------------------------------------------------------------------------------------------------

        public IReadOnlyList<GitBranchInfo> Branches()
        {
            return _repo.Branches
                .Where(b => !b.FriendlyName.EndsWith("/HEAD", StringComparison.Ordinal))
                .Select(b => new GitBranchInfo(b.FriendlyName, b.IsCurrentRepositoryHead, b.IsRemote, b.TrackingDetails.AheadBy ?? 0, b.TrackingDetails.BehindBy ?? 0, b.TrackedBranch != null))
                .OrderBy(b => b.IsRemote).ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>Makes a branch at the current commit (and switches to it).</summary>
        public void CreateBranch(string name, bool checkout = true)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsWhiteSpace) || name.Contains("..")) throw new GitException($"'{name}' is not a name for a branch.");
            if (!HasCommits) throw new GitException("A branch needs a first commit.");
            try
            {
                var branch = _repo.CreateBranch(name);
                if (checkout) Commands.Checkout(_repo, branch);
            }
            catch (LibGit2SharpException ex) { throw new GitException(ex.Message, ex); }
        }

        /// <summary>Switches to a branch (a branch of the server that has no local one yet gets one). Changes that would be lost are an error.</summary>
        public void Checkout(string name)
        {
            try
            {
                var branch = _repo.Branches[name] ?? throw new GitException($"There is no branch '{name}'.");
                if (branch.IsRemote)
                {
                    string local = name.Contains('/') ? name.Substring(name.IndexOf('/') + 1) : name;
                    var existing = _repo.Branches[local];
                    if (existing == null)
                    {
                        existing = _repo.CreateBranch(local, branch.Tip);
                        _repo.Branches.Update(existing, b => b.TrackedBranch = branch.CanonicalName);
                    }
                    branch = existing;
                }
                Commands.Checkout(_repo, branch);
            }
            catch (CheckoutConflictException) { throw new GitException("Changes in your files would be overwritten: commit them first, or discard them."); }
            catch (LibGit2SharpException ex) { throw new GitException(ex.Message, ex); }
        }

        // ---- the server ----------------------------------------------------------------------------------------------------------------------

        public IReadOnlyList<(string Name, string Url)> Remotes() => _repo.Network.Remotes.Select(r => (r.Name, r.Url)).ToList();

        public void AddRemote(string name, string url)
        {
            CheckUrl(url);
            try { _repo.Network.Remotes.Add(name, url); }
            catch (LibGit2SharpException ex) { throw new GitException(ex.Message, ex); }
        }

        private static void CheckUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) throw new GitException("The address is empty.");
            if (url.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("git@", StringComparison.OrdinalIgnoreCase) || System.Text.RegularExpressions.Regex.IsMatch(url, @"^[\w.-]+@[\w.-]+:"))
                throw new GitException("An address with ssh is not supported here: use the https:// address of the repository.");
        }

        private static LibGit2Sharp.Handlers.CredentialsHandler? Provider(Func<string, GitCredentials?>? credentials)
        {
            if (credentials == null) return null;
            int asked = 0;
            return (url, user, types) =>
            {
                if (asked++ > 0) return null;   // the server said no to what was given: stop instead of asking again and again
                var c = credentials(url);
                return c == null ? null : new UsernamePasswordCredentials { Username = c.Username, Password = c.Password };
            };
        }

        private static string Describe(Exception ex) => ex switch
        {
            NonFastForwardException => "The server has changes that you do not have: pull first.",
            LibGit2SharpException l when l.Message.Contains("authentication", StringComparison.OrdinalIgnoreCase) || l.Message.Contains("401") || l.Message.Contains("credentials", StringComparison.OrdinalIgnoreCase) => "The server did not accept the user name and password (for GitHub: a token, not the password).",
            _ => ex.Message,
        };

        private string RemoteNameOfCurrent()
        {
            var tracked = _repo.Head.TrackedBranch;
            if (tracked != null) return _repo.Head.RemoteName ?? "origin";
            var remote = _repo.Network.Remotes.FirstOrDefault() ?? throw new GitException("This repository has no server yet: add one first (its https:// address).");
            return remote.Name;
        }

        public void Fetch(Func<string, GitCredentials?>? credentials = null)
        {
            try
            {
                var remote = _repo.Network.Remotes[RemoteNameOfCurrent()];
                Commands.Fetch(_repo, remote.Name, Array.Empty<string>(), new FetchOptions { CredentialsProvider = Provider(credentials) }, null);
            }
            catch (Exception ex) when (ex is LibGit2SharpException) { throw new GitException(Describe(ex), ex); }
        }

        /// <summary>Fetches and joins the server's changes into the current branch. Conflicts leave the files with markers and <see cref="IsMerging"/> true: solve them, stage the files and commit.</summary>
        public GitPullResult Pull(Func<string, GitCredentials?>? credentials = null, string? name = null, string? email = null)
        {
            var (configName, configEmail) = Identity();
            name = string.IsNullOrWhiteSpace(name) ? configName : name;
            email = string.IsNullOrWhiteSpace(email) ? configEmail : email;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(email)) throw new GitException("Git needs a name and an e-mail address (for the commit that joins the changes).");
            try
            {
                var remote = _repo.Network.Remotes[RemoteNameOfCurrent()];
                if (_repo.Head.TrackedBranch == null)
                {
                    // a branch that has no server branch yet: join the one with the same name, if the server has it
                    Commands.Fetch(_repo, remote.Name, Array.Empty<string>(), new FetchOptions { CredentialsProvider = Provider(credentials) }, null);
                    var same = _repo.Branches[$"{remote.Name}/{_repo.Head.FriendlyName}"];
                    if (same == null) return GitPullResult.UpToDate;
                    _repo.Branches.Update(_repo.Head, b => b.TrackedBranch = same.CanonicalName);
                }
                var result = Commands.Pull(_repo, new Signature(name, email, DateTimeOffset.Now), new PullOptions { FetchOptions = new FetchOptions { CredentialsProvider = Provider(credentials) } });
                return result.Status switch
                {
                    MergeStatus.UpToDate => GitPullResult.UpToDate,
                    MergeStatus.FastForward => GitPullResult.FastForward,
                    MergeStatus.NonFastForward => GitPullResult.Merged,
                    _ => GitPullResult.Conflicts,
                };
            }
            catch (CheckoutConflictException) { throw new GitException("Changes in your files would be overwritten: commit them first, or discard them."); }
            catch (MergeFetchHeadNotFoundException) { return GitPullResult.UpToDate; }
            catch (LibGit2SharpException ex) { throw new GitException(Describe(ex), ex); }
        }

        /// <summary>Sends the commits of the current branch to the server (the first time it also makes the branch follow the server's).</summary>
        public void Push(Func<string, GitCredentials?>? credentials = null)
        {
            if (!HasCommits) throw new GitException("There is nothing to push yet: make a commit first.");
            try
            {
                var remote = _repo.Network.Remotes[RemoteNameOfCurrent()];
                var branch = _repo.Head;
                _repo.Network.Push(remote, branch.CanonicalName, new PushOptions { CredentialsProvider = Provider(credentials) });
                if (branch.TrackedBranch == null)
                    _repo.Branches.Update(branch, b => { b.Remote = remote.Name; b.UpstreamBranch = branch.CanonicalName; });
            }
            catch (LibGit2SharpException ex) { throw new GitException(Describe(ex), ex); }
        }
    }
}
