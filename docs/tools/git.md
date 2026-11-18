# Git in the editor

The editor (spark) has git built in: the state of the files, commit, history, differences, branches, pull and push. It uses **LibGit2Sharp** (MIT) - libgit2 comes with the editor for Windows, Linux
and macOS (x64 and arm64), so **nothing has to be installed**: git itself is not needed. The logic is in `src/fire.Git` (no user interface, `GitRepository`), the interface in the editor.

## What you see

* **Solution Explorer**: a file that is not as in the last commit has a letter - `M` changed, `A` added (staged), `U` new (untracked), `D` deleted, `R` renamed, `!` conflict. A folder, a project and
  the solution have a dot when something below them is changed. The solution shows the branch (`git: main`).
* **Status bar**: `git: main ↑1 ↓2  3 changed` - the branch, commits to push (↑) and to pull (↓), the number of changed files.
* **Git panel**: the branch, the changes and the commit, as a tab beside the explorer (below).
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
* While a merge is not finished (conflicts after a pull) a line says so: solve the conflicts, stage the files (+), commit.

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
tested by `GitSyncTests`, alone with `FIRE_TESTS_ONLY=git`).

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
| Branches | the branches of the repository and of the server: switch (also to a server branch, which gets a local one), make a new one at the current commit |

The context menus of the explorer have the same for the solution (commit, pull, push) and for files (show changes, commit this file, discard, history of this file). What a pull, a switch of the
branch or a discard changes is loaded again (see *Files that change on disk*).

## The server

Only **https** addresses are supported (LibGit2Sharp has no ssh): `https://github.com/you/project.git`. When the server asks, a dialog asks for a user name and a password - for GitHub, GitLab and most
servers a **personal access token**, not your account password. It is kept until the editor is closed (not on disk), and asked for again when the server did not accept it. Public repositories can be cloned
without credentials.

## Not (yet) there

Marks of changed lines in the editor margin, staging single lines, rebase, stashing, tags, ssh addresses, storing credentials on disk. Everything git can do beyond that works as usual outside of the editor
(the editor reads the state again when it gets the focus).
