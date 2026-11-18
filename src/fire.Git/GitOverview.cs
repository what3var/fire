namespace fire.Git
{
    /// <summary>One row of the Git panel: a file in one of the lists (staged, changes, untracked), with the letter of its state.</summary>
    /// <remarks>`IsDirectory`: a folder that is untracked as a whole (one row; what is in it is listed when it is opened).</remarks>
    public sealed record GitOverviewEntry(string Path, string FullPath, GitFileState State, bool IsStaged, bool IsDirectory = false)
    {
        public string Letter => GitOverview.Letter(State);
        /// <summary>The file name (the last part of <see cref="Path"/>).</summary>
        public string Name => Path.Contains('/') ? Path.Substring(Path.LastIndexOf('/') + 1) : Path;
        /// <summary>The folder of the file relative to the repository ("" at the top).</summary>
        public string Folder => Path.Contains('/') ? Path.Substring(0, Path.LastIndexOf('/')) : "";
    }

    /// <summary>
    /// What the Git panel of the editor shows (docs/tools/git.md): the branch, commits to push (ahead) and to pull (behind), and the changed files in three lists - staged (what the next commit
    /// has), changes (changed in the folder, not staged; conflicts too) and untracked (new files). A file that is staged and changed again is in both of the first two lists.
    /// </summary>
    public sealed record GitOverview(string Branch, int Ahead, int Behind, bool HasUpstream, bool HasRemote, bool IsMerging,
        IReadOnlyList<GitOverviewEntry> Staged, IReadOnlyList<GitOverviewEntry> Changes, IReadOnlyList<GitOverviewEntry> Untracked)
    {
        /// <summary>The letter of a state: M changed, A added, D deleted, R renamed, U untracked, ! conflict (the same as the solution explorer).</summary>
        public static string Letter(GitFileState state) => state switch
        {
            GitFileState.Modified => "M", GitFileState.Added => "A", GitFileState.Untracked => "U", GitFileState.Deleted => "D", GitFileState.Renamed => "R", GitFileState.Conflicted => "!", _ => " ",
        };

        /// <summary>What git is in the middle of (a merge, a rebase, a cherry-pick, a revert that stopped at conflicts); the panel says so.</summary>
        public GitOperationKind Operation { get; init; } = IsMerging ? GitOperationKind.Merge : GitOperationKind.None;

        public int Count => Staged.Count + Changes.Count + Untracked.Count;
        public bool HasConflicts => Changes.Any(e => e.State == GitFileState.Conflicted);

        /// <summary>"↑2 ↓1" (empty when the branch is even with the server's); "not on the server" for a branch that has no server branch yet.</summary>
        public string AheadBehindText =>
            !HasRemote ? "no server" : !HasUpstream ? "not on the server yet" : Ahead == 0 && Behind == 0 ? "up to date" : string.Join(" ", new[] { Ahead > 0 ? $"↑{Ahead}" : "", Behind > 0 ? $"↓{Behind}" : "" }.Where(s => s.Length > 0));

        public static GitOverview Build(string branch, int ahead, int behind, bool hasUpstream, bool hasRemote, bool isMerging, IEnumerable<GitChange> changes)
        {
            var staged = new List<GitOverviewEntry>();
            var changed = new List<GitOverviewEntry>();
            var untracked = new List<GitOverviewEntry>();
            var sorted = changes.ToList();
            sorted.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path));
            foreach (var c in sorted)
            {
                if (c.IsConflicted) { changed.Add(new GitOverviewEntry(c.Path, c.FullPath, GitFileState.Conflicted, false)); continue; }
                if (c.Staged != GitFileState.Unmodified) staged.Add(new GitOverviewEntry(c.Path, c.FullPath, c.Staged, true));
                if (c.Unstaged == GitFileState.Untracked) untracked.Add(new GitOverviewEntry(c.Path, c.FullPath, GitFileState.Untracked, false, c.IsDirectory));
                else if (c.Unstaged != GitFileState.Unmodified) changed.Add(new GitOverviewEntry(c.Path, c.FullPath, c.Unstaged, false));
            }
            return new GitOverview(branch, ahead, behind, hasUpstream, hasRemote, isMerging, staged, changed, untracked);
        }
    }
}

namespace fire.Git
{
    public enum GitRowKind { Banner, Header, Entry, More, Empty }

    /// <summary>
    /// One row of the Git panel's list (a flat list, so that the panel can virtualize it: only the rows that are on screen get controls). `Section` of a header: its entries as shown (the
    /// filter applied) - what its buttons act on. `Depth` 1: a file in an opened untracked folder.
    /// </summary>
    public sealed record GitRow(GitRowKind Kind, string Text, GitOverviewEntry? Entry = null, IReadOnlyList<GitOverviewEntry>? Section = null, bool StagedSection = false, int Depth = 0, bool IsExpanded = false)
    {
        /// <summary>The identity of the row across refreshes (selection).</summary>
        public string Key => Kind switch
        {
            GitRowKind.Entry => (Entry!.IsStaged ? "s|" : "w|") + Entry.FullPath,
            _ => Kind + "|" + Text,
        };
    }

    /// <summary>The rows of the Git panel for an overview: the sections with their entries, filtered by a text, with the files of the opened untracked folders.</summary>
    public static class GitRows
    {
        /// <summary>The files shown of an opened folder (more: a row "and N more").</summary>
        public const int FolderLimit = 2000;

        public static List<GitRow> Build(GitOverview overview, string? filter, IReadOnlySet<string> expanded, IReadOnlyDictionary<string, (IReadOnlyList<GitOverviewEntry> Files, int Total)> children)
        {
            var rows = new List<GitRow>(overview.Count + 8);
            string f = filter?.Trim() ?? "";
            bool Match(GitOverviewEntry e) => f.Length == 0 || e.Path.Contains(f, StringComparison.OrdinalIgnoreCase);
            switch (overview.Operation)
            {
                case GitOperationKind.Merge:
                    rows.Add(new GitRow(GitRowKind.Banner, overview.HasConflicts ? "A merge is not finished: solve the conflicts (!), stage the files and commit." : "A merge is not finished: commit to finish it."));
                    break;
                case GitOperationKind.Rebase:
                    rows.Add(new GitRow(GitRowKind.Banner, overview.HasConflicts ? "A rebase stopped at conflicts: solve them (!), stage the files (+), then Continue in the Repository view (Git > Repository)." : "A rebase is not finished: Continue, Skip or Abort it in the Repository view (Git > Repository)."));
                    break;
                case GitOperationKind.CherryPick or GitOperationKind.Revert:
                    string what = overview.Operation == GitOperationKind.CherryPick ? "cherry-pick" : "revert";
                    rows.Add(new GitRow(GitRowKind.Banner, overview.HasConflicts ? $"A {what} stopped at conflicts: solve them (!), stage the files and commit (or abort it in the Repository view)." : $"A {what} is not finished: commit to finish it."));
                    break;
            }
            void Section(string title, IReadOnlyList<GitOverviewEntry> entries, bool staged)
            {
                if (entries.Count == 0) return;
                var shown = new List<GitOverviewEntry>();
                var childRows = new Dictionary<GitOverviewEntry, List<GitRow>>();
                foreach (var e in entries)
                {
                    bool match = Match(e);
                    List<GitRow>? inner = null;
                    if (e.IsDirectory && expanded.Contains(e.FullPath))
                    {
                        inner = new List<GitRow>();
                        if (children.TryGetValue(e.FullPath, out var list))
                        {
                            string prefix = e.Path + "/";
                            foreach (var c in list.Files)
                                if (match || Match(c)) inner.Add(new GitRow(GitRowKind.Entry, c.Path.StartsWith(prefix, StringComparison.Ordinal) ? c.Path.Substring(prefix.Length) : c.Path, c, Depth: 1));
                            if (list.Total > list.Files.Count) inner.Add(new GitRow(GitRowKind.More, $"and {list.Total - list.Files.Count} more files", Depth: 1));
                            if (!match && inner.Count == 0) inner = null;
                        }
                        else inner.Add(new GitRow(GitRowKind.More, "reading the folder...", Depth: 1));
                    }
                    if (match || inner != null) { shown.Add(e); if (inner != null) childRows[e] = inner; }
                }
                if (shown.Count == 0) return;   // (the filter leaves nothing of it)
                string count = shown.Count == entries.Count ? $"{entries.Count}" : $"{shown.Count} of {entries.Count}";
                rows.Add(new GitRow(GitRowKind.Header, $"{title.ToUpperInvariant()}  {count}", Section: shown, StagedSection: staged));
                foreach (var e in shown)
                {
                    rows.Add(new GitRow(GitRowKind.Entry, e.Path, e, IsExpanded: childRows.ContainsKey(e)));
                    if (childRows.TryGetValue(e, out var inner)) rows.AddRange(inner);
                }
            }
            Section("Staged Changes", overview.Staged, true);
            Section("Changes", overview.Changes, false);
            Section("Untracked", overview.Untracked, false);
            if (overview.Count == 0) rows.Add(new GitRow(GitRowKind.Empty, "No changes since the last commit."));
            else if (f.Length > 0 && !rows.Any(r => r.Kind == GitRowKind.Entry)) rows.Add(new GitRow(GitRowKind.Empty, $"No changed file matches '{f}'."));
            return rows;
        }
    }
}
