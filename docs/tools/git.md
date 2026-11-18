# Git in the editor

The editor (spark) has git built in: the state of the files, commit, history, differences, branches, pull and push. It uses **LibGit2Sharp** (MIT) - libgit2 comes with the editor for Windows, Linux
and macOS (x64 and arm64), so **nothing has to be installed**: git itself is not needed. The logic is in `src/fire.Git` (no user interface, `GitRepository`), the interface in the editor.

## What you see

* **Solution Explorer**: a file that is not as in the last commit has a letter - `M` changed, `A` added (staged), `U` new (untracked), `D` deleted, `R` renamed, `!` conflict. A folder, a project and
  the solution have a dot when something below them is changed. The solution shows the branch (`git: main`).
* **Status bar**: `git: main ↑1 ↓2  3 changed` - the branch, commits to push (↑) and to pull (↓), the number of changed files. **A click** on it switches the branch (below).
* **Git panel**: the branch, the changes and the commit, as a tab beside the explorer (below).
* **Repository view**: the branches, tags and stashes, the commit graph and what each commit changed, with checkout, merge, rebase, cherry-pick, revert and reset (Git > Repository, below).
* The state is read again when something is saved, when the solution changes, when files change on disk, when the window gets the focus (git may have been used outside of the editor) and after
  every git command.

## The Git panel

A tab beside the Solution Explorer (View > Git, or Git > Show Git Panel; it comes out with the explorer when a solution is opened):

* **The branch** and how far it is from the server's branch: `↑2` commits to push, `↓1` to pull (as of the last fetch), "up to date", "not on the server yet" or "no server".
* **Fetch, Pull, Push** and **⟳** (read the state again).
* **The message** and **Commit** (Ctrl+Enter in the message) or **Commit & Push**. Commit takes what is staged; when nothing is staged it asks whether all changes go in. Unsaved documents are
  saved first. Git asks for your name and e-mail address once (kept in this repository).
* **The changed files** in three lists, each with its letter (`M` changed, `A` added, `D` deleted, `R` renamed, `U` new, `!` conflict) and its folder:
  *Staged Changes* (what the next commit has; **−** unstages), *Changes* (changed, not staged; **+** stages, **↶** throws the changes away) and *Untracked* (new files; **+** stages, **↶**
  deletes the file). The header of a list does the same for all of its files. Throwing away asks first. A file that is staged and then changed again is in both lists.
* **A click** on a file shows its changes (staged ones for a row under *Staged Changes*), **a double click** opens it.
* **A new folder** (everything in it untracked) is one row `name/`, as `git status` shows it; a click opens it and lists its files (the first 2000, then "and N more"). + stages all of its
  files, ↶ deletes them (ignored files in it stay).
* **The filter** above the lists shows only the files whose path contains the text; the buttons of a list's header act on what is shown.
* **The context menu** of a row: Open, Show Changes, Stage / Unstage, Discard, and for a new file or folder **Add to .gitignore** (a line `/path` or `/path/` in the `.gitignore` at the top of
  the repository; for a file also its folder).
* While a merge is not finished (conflicts after a pull) a line says so: solve the conflicts, stage the files (+), commit. The same for a cherry-pick or a revert that stopped at conflicts; a rebase
  that stopped is continued in the Repository view (Commit in the panel refuses then and says so).
* **⎇ Repository** opens the Repository view.

The panel is read again after every git command, when a file is saved, when files change on disk (also `git add` or a commit outside of the editor) and when the window gets the focus.

### Very many changed files

A build output that is not in the `.gitignore` yet can be tens of thousands of new files. The editor stays responsive:

* the state is read in the background, never on the user interface's thread, with new folders collapsed (20000 untracked files in two folders: 3 ms instead of 270 ms, 306 entries instead of 20305);
  one refresh at a time - what is asked for while one runs is done once afterwards;
* the lists are virtualized: only the rows on screen have controls (20000 rows: 5-20 ms to show, before 7-8 s);
* the solution explorer only gets the new marks of git, it is not built anew;
* stage, unstage and discard of many files are one call each (20000 files staged: about 3 s, before 10 s), in the background;
* the file watcher waits longer for bursts of 500 or more changes, and while the repository is slow to read (5 times the quiet time);
* a change on disk only compares the files the editor holds (the solution, the projects, the open documents), never the whole tree.

`FIRE_GIT_TIMING=1` writes the times of reading and showing the state to stderr (to measure a repository).

## The Repository view

Git > Repository (or **⎇ Repository** in the Git panel, or *Repository View* in the popup of the status bar) opens a tab with the whole repository:

* **Left: the references.** The current branch (or `HEAD (detached at 1a2b3c4)`), the local **branches** (the current one with ●, ↑ commits to push and ↓ to pull against the server's branch it
  follows), the **servers' branches**, one group per server, the **tags** and the **stashes**. A click selects the commit it points at in the log; a double click on a branch switches to it
  (a server's branch: to the local branch that follows it, made when there is none).
* **Middle: the commits** of all branches, tags and the head, newest first and each commit before its parents, as a **graph**: a lane per line of development, a ring for a merge, the current
  commit with a white circle and a `HEAD` mark, the names of the branches (blue, the current one with ●), the servers' branches (orange) and the tags (yellow) at their commits. Author, date and
  the short id beside it. The list is virtualized (only the rows on screen have controls) and read in **pages of 300**: the next page is read when the end is scrolled to. **The filter** (message
  or id, author name or address, a path of a file or folder) shows only the commits that match (then without the lines of the graph, which would be wrong between the commits that are left).
* **Right: the selected commit** - the whole message, the id, author and committer, date, parents and names, the **changed files** (A, M, D, R) and, for the file clicked, **its changes**.
* **The bar**: Fetch (all servers; branches gone there are removed here), Pull, Push, **Stash...** (puts your changes, also new files, onto the stash with a message) and ⟳.

What the view reads, it reads in the background. It reads the references again after every refresh of the git state (every git command, a save, the focus, files changing on disk) and the log only
when something changed (a commit, a branch, a tag, the head, a stash, an operation).

### The context menus

Every command says before what it does, and what is lost when something is:

| on | commands |
|---|---|
| a commit | Check Out This Commit (asks first: a *detached HEAD* - commits made there belong to no branch), Switch to a branch that points there, Create Branch Here, Create Tag Here (with a message: annotated), Cherry-Pick into the current branch, Revert (a new commit that undoes it), Merge into the current branch, Rebase the current branch onto it, **Reset the current branch to here** - Soft (the files stay, the differences staged), Mixed (the files stay, nothing staged), Hard (the files too), Copy Commit Id |
| a branch | Switch to it, Merge into the current one, Rebase the current one onto it, Reset the current one to it, (the current one:) Reset to the server's branch it follows, Create Branch / Tag Here, Delete (when its commits are not all in the current branch, it says they are lost and asks again) |
| a server's branch | Check Out (a local branch that follows it, or the one that does already), Merge, Rebase, Reset onto it, Create Branch Here, **Delete on the Server** (asks: it is gone for everybody) |
| a tag | Check Out (detached HEAD), Create Branch Here, Push the Tag to the Server, Delete (here) |
| a stash | Apply (it stays), Pop (applied and removed), Drop (asks: its changes are lost) |

**A reset** lists first the commits that leave the branch and says which of them no other branch or tag has (they are *lost*), and the commits the branch gets; **hard** also lists the uncommitted
changes that are thrown away, has *Cancel* as its first button and *Reset Hard - Lose Changes* as the other. Untracked files stay with every reset.

**A switch** (a branch, a commit) with uncommitted changes asks: **Stash** (they go onto the stash, the files are as in the last commit - apply the stash later), **Carry Over** (they stay in the files
and come along; when the other commit changes the same files nothing happens and you are told) or Cancel.

### Rebase, merge, cherry-pick, revert - and conflicts

* **Rebase** (not interactive): the commits of the current branch that the other one does not have are made again on top of it, one after the other. It needs a clean working folder (commit,
  stash or discard first; untracked files are fine). The question says that the commits are new (a branch that was pushed needs a forced push afterwards). Onto a branch, a server's branch, a tag
  or a commit.
* **Merge**: fast-forward when the current branch has nothing of its own, else a merge commit (`Merge branch 'x' into main`).
* **Cherry-pick**: a new commit with the changes of that commit (of a merge: against its first parent). **Revert**: a new commit that takes them back.
* **Conflicts** stop all of them. The files have the `<<<<<<<` `=======` `>>>>>>>` markers and a `!`, and a **banner** over the Repository view says what stopped, and where (*step 3 of 5, the
  commit 1a2b3c4 ...* for a rebase), with **Continue**, **Skip** (rebase only: the commit is left out) and **Abort** (the branch and the files are as before; asks first). Solve the files, stage them
  in the Git panel (+), press Continue: a rebase goes on with the next commits, a merge, cherry-pick or revert is committed with the message git prepared.

Everything is done with LibGit2Sharp - **git itself is not needed** (and not used when it is there): rebase, cherry-pick, revert, merge, reset and stash are in libgit2. A rebase that stopped is kept
on disk as git keeps it (`.git/rebase-merge`), so it can also be continued or aborted with git outside of the editor, and the editor shows one that git started. *Skip* is made of two steps that
libgit2 has: the files and the index go back to the last commit of the rebase (not with a reset, which would end the rebase), then *continue* finds nothing to commit and goes on.

A repository with missing objects (a shallow clone, which libgit2 does not see as one in a linked worktree) is shown as far as its history goes (*history incomplete* beside the number of commits).

## The branch in the status bar

**A click** on `git: main ...` opens a popup: a filter (type a part of the name, ↑ ↓ and Enter), the **local branches** (with the server's branch each follows and ↑ ↓) and the **servers'
branches**. Choosing a local branch switches to it; choosing a server's branch switches to the local branch that follows it - or makes one (named like it, without the server) that follows it.
With uncommitted changes you are asked first (Stash / Carry Over / Cancel, as above). **New Branch...** makes a branch at the current commit and switches to it (your changes come along),
**Fetch** gets the servers' branches, **Repository View** opens the view.

## Files that change on disk

A pull, a switch of the branch, a discard - in the editor or with git outside of it - and any other program can change the files the editor holds. The editor remembers what each of them was like when it
read or wrote it (the content, not only the time), watches the folders of the solution, the projects and the open documents (bursts of changes are bundled: it acts when nothing changed for 0.4 s),
and compares everything after each of its own git commands:

| what changed | what happens |
|---|---|
| the solution file | the solution is loaded again, with its projects (deleted: the solution is closed) |
| a project file | that project is loaded again (deleted: the solution is loaded again and shows it as missing) |
| files made or deleted in a project folder | the folders are read again (the explorer shows the new files) |
| an open document without unsaved changes | it is read again silently (the caret stays on its line) |
| an open document with unsaved changes | you decide: **Keep Mine** (the next save overwrites the file), **Take Theirs** (your changes are lost), **Show Diff** (the differences of the file on disk and the editor, then the question again) |
| the file of an open document was deleted | the tab says `(deleted)`; saving makes the file again, and when it comes back (a switch back) it is read again |

What a build writes (`bin/`, `obj/`) and the inside of `.git` (but its index and HEAD, for the Git panel) are not watched. The logic is in `src/fire.Editor/Git/WorkingTreeSync.cs` (no user interface;
tested by `GitSyncTests`, alone with `FIRE_TESTS_ONLY=git`). Every operation of the Repository view and of the status bar is followed by the same comparison and a refresh of the git state.

## The code and its tests

`src/fire.Git`: `GitRepository` (status, commit, diff, branches, servers), `GitRepository.History.cs` (references, the log in pages with its filter, the details of a commit),
`GitRepository.Operations.cs` (checkout, branches and tags, merge, rebase, cherry-pick, revert, reset, stash), `GitGraph.cs` (the lanes of the graph, page by page). The editor:
`src/fire.Editor/Git/GitRepositoryView.cs` (the view), `MainWindow.GitRepository.cs` (the questions, the operations, the popup of the status bar). `FIRE_TESTS_ONLY=git` runs `GitTests`,
`GitSyncTests` and `GitRepositoryViewTests` (the lanes of merges, branch tips and pages; rebase without and with a conflict - continue, skip, abort, onto a commit; reset soft, mixed and hard
with its preview; cherry-pick, revert and merge with and without conflicts; the checkout of a server's branch that makes a local branch following it; stash, tags, deleting branches; the log
with its filters and pages), in temporary repositories with a folder as the server.

## The Git menu

| command | what it does |
|---|---|
| Create Repository | makes a repository in the folder of the solution (or of the file), with a `.gitignore` for what a build writes (`bin/`, `obj/`, `*.fpk`, `.vs/`); offers the first commit |
| Clone Repository | copies a repository from a server (https) into a folder of its own and opens its solution or project |
| Commit | the commit dialog: the changed files with a check box each (checked = in this commit), the changes of the selected file (green added, red removed), the message; asks for your name and e-mail address once (kept in this repository). *Commit and push* sends it to the server at once. Unsaved documents are saved first |
| Pull | fetches and joins the server's changes (fast-forward, or a commit that joins them). Files with conflicts get the usual `<<<<<<<` `=======` `>>>>>>>` markers and a `!`: edit them, then commit |
| Push | sends the commits of the current branch (the first time the branch follows the server's); a server that has changes you do not have says so: pull first |
| Fetch | gets the server's commits without joining them (the arrows in the status bar show how far apart you are) |
| Server Address | the https address of the repository on the server (GitHub, GitLab, ...); the repository has to exist there (an empty one is fine) |
| History / History of This File | the commits (newest first) and what the selected one changed |
| Show Changes / Discard Changes of This File | the differences of the active file; back to the last commit (asks first; an untracked file is deleted) |
| Repository | the Repository view: branches, tags, stashes, the commit graph; checkout, merge, rebase, cherry-pick, revert, reset, stash (above) |
| Branches | the branches of the repository and of the server: switch (also to a server branch, which gets a local one), make a new one at the current commit |

The context menus of the explorer have the same for the solution (commit, pull, push) and for files (show changes, commit this file, discard, history of this file). What a pull, a switch of the
branch or a discard changes is loaded again (see *Files that change on disk*).

## The server

Only **https** addresses are supported (LibGit2Sharp has no ssh): `https://github.com/you/project.git`. When the server asks, a dialog asks for a user name and a password - for GitHub, GitLab and most
servers a **personal access token**, not your account password. It is kept until the editor is closed (not on disk), and asked for again when the server did not accept it. Public repositories can be cloned
without credentials.

## Not (yet) there

Marks of changed lines in the editor margin, staging single lines, an interactive rebase (reorder, squash, edit), a forced push, ssh addresses, storing credentials on disk. Everything git can do beyond that works as usual outside of the editor
(the editor reads the state again when it gets the focus).
