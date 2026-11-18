using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using fire.Projects;

namespace fire.Editor
{
    // Files that change under the editor (docs/tools/git.md, "Files that change on disk"): a git pull, merge, switch of the branch, reset or stash - in the editor or outside of it - or any other
    // program. The editor remembers what each file it holds (the solution, the projects, the open documents) was like when it read or wrote it (DiskBaselines); a change of the folder (a file watcher,
    // ChangeBatcher bundles its bursts) or the end of a git command compares them with the disk, and ReloadPlanner says what to do: load the solution or a project again, read the folders again,
    // read an unchanged document again, ask about a document with changes of its own, mark a deleted one. No user interface in here (tested in fire.Testing, GitSyncTests).

    /// <summary>What a file was like: whether it existed, its size and a hash of its content (the time of the last write alone is not enough: git may write the same second).</summary>
    public readonly record struct FileStamp(bool Exists, long Length, ulong Hash)
    {
        public static readonly FileStamp Missing = new(false, 0, 0);

        public static FileStamp Of(string path)
        {
            try
            {
                if (!File.Exists(path)) return Missing;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var hash = SHA256.HashData(stream);
                return new FileStamp(true, stream.Length, BitConverter.ToUInt64(hash, 0));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Missing; }
        }
    }

    /// <summary>How a file is now, compared with what the editor last read or wrote.</summary>
    public enum DiskChange { Unchanged, Changed, Deleted, Created, Unknown }

    /// <summary>The state of the files the editor holds, as it last read or wrote them.</summary>
    public sealed class DiskBaselines
    {
        private readonly Dictionary<string, FileStamp> _stamps = new(ProjectFiles.PathComparer);

        /// <summary>The file is as it is on disk now (it was just read or written).</summary>
        public void Remember(string path) { lock (_stamps) _stamps[Path.GetFullPath(path)] = FileStamp.Of(path); }

        public void Forget(string path) { lock (_stamps) _stamps.Remove(Path.GetFullPath(path)); }

        public bool Knows(string path) { lock (_stamps) return _stamps.ContainsKey(Path.GetFullPath(path)); }

        public IReadOnlyList<string> Paths { get { lock (_stamps) return _stamps.Keys.ToList(); } }

        /// <summary>The file on disk compared with what was remembered (Unknown: nothing was remembered of it).</summary>
        public DiskChange Check(string path)
        {
            string full = Path.GetFullPath(path);
            FileStamp before;
            lock (_stamps) if (!_stamps.TryGetValue(full, out before)) return DiskChange.Unknown;
            var now = FileStamp.Of(full);
            if (before.Exists && !now.Exists) return DiskChange.Deleted;
            if (!before.Exists && now.Exists) return DiskChange.Created;
            if (!before.Exists) return DiskChange.Unchanged;
            return before == now ? DiskChange.Unchanged : DiskChange.Changed;
        }
    }

    /// <summary>An open document, as the planner needs it: its file and whether it has changes that are not saved.</summary>
    public sealed record OpenFileInfo(string Path, bool IsModified, bool IsMarkedDeleted = false);

    public enum DocumentReload
    {
        /// <summary>Changed on disk, no changes of its own in the editor: read again without asking.</summary>
        Silently,
        /// <summary>Changed on disk and in the editor: the user decides (keep mine, take theirs, show the differences).</summary>
        Ask,
        /// <summary>The file was deleted: the tab stays, marked as deleted (saving makes the file again).</summary>
        MarkDeleted,
    }

    /// <summary>What to do after files changed on disk. <see cref="ReloadWorkspace"/> includes the projects and their folders. The caller remembers the new state of the files it handled (DiskBaselines.Remember).</summary>
    public sealed record ReloadPlan(bool ReloadWorkspace, bool CloseWorkspace, IReadOnlyList<string> ProjectsToReload, bool RefreshFolders, IReadOnlyList<(string Path, DocumentReload Action)> Documents)
    {
        public bool IsEmpty => !ReloadWorkspace && !CloseWorkspace && ProjectsToReload.Count == 0 && !RefreshFolders && Documents.Count == 0;
    }

    /// <summary>What changed in a folder, as the file watcher reported it: the paths, and of which of them the existence changed (made, deleted, renamed).</summary>
    public sealed class ChangeBatch
    {
        public HashSet<string> Paths { get; } = new(ProjectFiles.PathComparer);
        public HashSet<string> Appeared { get; } = new(ProjectFiles.PathComparer);
        /// <summary>The index or the current branch of a git repository changed (a commit or `git add` outside of the editor): the Git panel is read again.</summary>
        public bool GitStateChanged { get; set; }
        /// <summary>The watcher lost events (too many at once): everything has to be compared.</summary>
        public bool Overflow { get; set; }
        public bool IsEmpty => Paths.Count == 0 && !GitStateChanged && !Overflow;
    }

    /// <summary>
    /// Bundles the bursts of a file watcher (a checkout writes hundreds of files): a batch is ready when nothing came for <c>quiet</c>, at the latest <c>maxDelay</c> after its first change. While
    /// it is held (a git command of the editor runs, which compares everything afterwards) nothing is handed out. A big burst (<see cref="LargeBatch"/> paths or more: a checkout, a build output
    /// that is not ignored) or a repository whose state is slow to read (<see cref="Slow"/>) waits longer: 5 times the quiet time, at most twice maxDelay. The time is passed in (tests).
    /// </summary>
    public sealed class ChangeBatcher
    {
        private readonly TimeSpan _quiet, _maxDelay;
        private readonly object _lock = new();
        private ChangeBatch _batch = new();
        private DateTime _first, _last;
        private int _held;

        public ChangeBatcher(TimeSpan quiet, TimeSpan maxDelay) { _quiet = quiet; _maxDelay = maxDelay; }

        public bool HasPending { get { lock (_lock) return !_batch.IsEmpty; } }

        /// <summary>From this many paths on, a batch waits longer.</summary>
        public int LargeBatch { get; init; } = 500;

        /// <summary>Reading the state of the repository is slow (a huge tree): every batch waits as long as a large one.</summary>
        public bool Slow { get; set; }

        public void Add(string path, bool existenceChanged, DateTime now)
        {
            lock (_lock)
            {
                Touch(now);
                string full = Path.GetFullPath(path);
                _batch.Paths.Add(full);
                if (existenceChanged) _batch.Appeared.Add(full);
            }
        }

        public void AddGitState(DateTime now) { lock (_lock) { Touch(now); _batch.GitStateChanged = true; } }

        public void AddOverflow(DateTime now) { lock (_lock) { Touch(now); _batch.Overflow = true; } }

        private void Touch(DateTime now)
        {
            if (_batch.IsEmpty) _first = now;
            _last = now;
        }

        public void Hold() { lock (_lock) _held++; }
        public void Release() { lock (_lock) _held = Math.Max(0, _held - 1); }

        /// <summary>Drops what was collected (everything was just compared anyway).</summary>
        public void Clear() { lock (_lock) _batch = new ChangeBatch(); }

        /// <summary>The batch, when it is ready (and takes it out).</summary>
        public bool TryTake(DateTime now, out ChangeBatch batch)
        {
            lock (_lock)
            {
                batch = _batch;
                bool large = Slow || _batch.Paths.Count >= LargeBatch || _batch.Overflow;
                var quiet = large ? _quiet * 5 : _quiet;
                var maxDelay = large ? _maxDelay * 2 : _maxDelay;
                if (_held > 0 || _batch.IsEmpty || (now - _last < quiet && now - _first < maxDelay)) return false;
                _batch = new ChangeBatch();
                return true;
            }
        }
    }

    /// <summary>Which events of a file watcher matter: the files of the working folder (not what a build writes, not the inside of `.git` - only its index and HEAD, which tell of a commit or a switch).</summary>
    public static class WatchFilter
    {
        public enum Kind { Ignore, WorkingTree, GitState }

        private static readonly HashSet<string> IgnoredFolders = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".vs", "node_modules" };

        public static Kind Classify(string path)
        {
            var parts = Path.GetFullPath(path).Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            int git = Array.FindIndex(parts, p => p == ".git");
            if (git >= 0)
                return git == parts.Length - 2 && parts[^1] is "index" or "HEAD" ? Kind.GitState : Kind.Ignore;
            for (int i = 0; i < parts.Length - 1; i++) if (IgnoredFolders.Contains(parts[i])) return Kind.Ignore;
            string name = parts.Length > 0 ? parts[^1] : "";
            // the temporary files of editors and of git (`index.lock`, `foo.swp`, `foo~`)
            if (name.EndsWith('~') || name.EndsWith(".swp", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)) return Kind.Ignore;
            return Kind.WorkingTree;
        }

        /// <summary>The fewest folders that hold all of `folders` (a folder inside another is watched by that one).</summary>
        public static IReadOnlyList<string> Roots(IEnumerable<string> folders)
        {
            var sorted = folders.Where(f => !string.IsNullOrEmpty(f)).Select(f => Path.TrimEndingDirectorySeparator(Path.GetFullPath(f))).Distinct(ProjectFiles.PathComparer)
                .OrderBy(f => f.Length).ToList();
            var roots = new List<string>();
            foreach (var f in sorted)
                if (!roots.Any(r => IsInside(f, r))) roots.Add(f);
            return roots;
        }

        public static bool IsInside(string path, string folder)
        {
            string p = Path.GetFullPath(path), d = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            return string.Equals(p, d, comparison) || p.StartsWith(d + Path.DirectorySeparatorChar, comparison);
        }
    }

    /// <summary>Decides what has to be loaded again after files changed on disk (see the top of this file).</summary>
    public static class ReloadPlanner
    {
        /// <param name="workspaceFile">The solution file (or the file of the single project) that is open; null: none.</param>
        /// <param name="projectFiles">The project files that are loaded.</param>
        /// <param name="projectFolders">The folders of the projects (a file made or deleted in them changes what the explorer shows).</param>
        /// <param name="batch">What the watcher reported; null: everything is compared (after a git command), and the folders are read again.</param>
        public static ReloadPlan Plan(DiskBaselines baselines, string? workspaceFile, IEnumerable<string> projectFiles, IEnumerable<string> projectFolders, IEnumerable<OpenFileInfo> documents, ChangeBatch? batch)
        {
            bool all = batch == null || batch.Overflow;
            bool Concerns(string path) => all || batch!.Paths.Contains(Path.GetFullPath(path));

            bool reloadWorkspace = false, closeWorkspace = false;
            if (workspaceFile != null && Concerns(workspaceFile))
            {
                var change = baselines.Check(workspaceFile);
                if (change == DiskChange.Deleted) closeWorkspace = true;
                else if (change is DiskChange.Changed or DiskChange.Created) reloadWorkspace = true;
            }

            var projects = new List<string>();
            if (!reloadWorkspace && !closeWorkspace)
            {
                foreach (var project in projectFiles.Distinct(ProjectFiles.PathComparer))
                {
                    if (ProjectFiles.PathComparer.Equals(Path.GetFullPath(project), workspaceFile == null ? "" : Path.GetFullPath(workspaceFile)) || !Concerns(project)) continue;
                    var change = baselines.Check(project);
                    // a project file that is gone: the solution is loaded again (it shows the project as missing)
                    if (change == DiskChange.Deleted) { reloadWorkspace = true; projects.Clear(); break; }
                    if (change is DiskChange.Changed or DiskChange.Created) projects.Add(Path.GetFullPath(project));
                }
            }

            bool refreshFolders = batch == null || batch.Overflow;
            if (!refreshFolders && batch!.Appeared.Count > 0)
            {
                var folders = projectFolders.ToList();
                refreshFolders = batch.Appeared.Any(p => folders.Any(f => WatchFilter.IsInside(p, f)));
            }
            if (reloadWorkspace || closeWorkspace) refreshFolders = false;   // (loading again reads the folders, too)

            var docs = new List<(string, DocumentReload)>();
            foreach (var doc in documents)
            {
                if (!Concerns(doc.Path)) continue;
                switch (baselines.Check(doc.Path))
                {
                    case DiskChange.Changed:
                    case DiskChange.Created:   // back again (a switch to a branch that has it): read it again like a changed one
                        docs.Add((doc.Path, doc.IsModified ? DocumentReload.Ask : DocumentReload.Silently));
                        break;
                    case DiskChange.Deleted:
                        if (!doc.IsMarkedDeleted) docs.Add((doc.Path, DocumentReload.MarkDeleted));
                        break;
                }
            }
            return new ReloadPlan(reloadWorkspace, closeWorkspace, projects, refreshFolders, docs);
        }
    }
}
