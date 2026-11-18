namespace fire.Git
{
    /// <summary>One row of the Git panel: a file in one of the lists (staged, changes, untracked), with the letter of its state.</summary>
    public sealed record GitOverviewEntry(string Path, string FullPath, GitFileState State, bool IsStaged)
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
            foreach (var c in changes.OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase))
            {
                if (c.IsConflicted) { changed.Add(new GitOverviewEntry(c.Path, c.FullPath, GitFileState.Conflicted, false)); continue; }
                if (c.Staged != GitFileState.Unmodified) staged.Add(new GitOverviewEntry(c.Path, c.FullPath, c.Staged, true));
                if (c.Unstaged == GitFileState.Untracked) untracked.Add(new GitOverviewEntry(c.Path, c.FullPath, GitFileState.Untracked, false));
                else if (c.Unstaged != GitFileState.Unmodified) changed.Add(new GitOverviewEntry(c.Path, c.FullPath, c.Unstaged, false));
            }
            return new GitOverview(branch, ahead, behind, hasUpstream, hasRemote, isMerging, staged, changed, untracked);
        }
    }
}
