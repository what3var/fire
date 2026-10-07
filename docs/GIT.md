# Git in the editor

The editor (spark) has git built in: the state of the files, commit, history, differences, branches, pull and push. It uses **LibGit2Sharp** (MIT) - libgit2 comes with the editor for Windows, Linux
and macOS (x64 and arm64), so **nothing has to be installed**: git itself is not needed. The logic is in `src/fire.Git` (no user interface, `GitRepository`), the interface in the editor.

## What you see

* **Solution Explorer**: a file that is not as in the last commit has a letter - `M` changed, `A` added (staged), `U` new (untracked), `D` deleted, `R` renamed, `!` conflict. A folder, a project and
  the solution have a dot when something below them is changed. The solution shows the branch (`git: main`).
* **Status bar**: `git: main ↑1 ↓2  3 changed` - the branch, commits to push (↑) and to pull (↓), the number of changed files.
* The state is read again when something is saved, when the solution changes, when the window gets the focus (git may have been used outside of the editor) and after every git command.

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

The context menus of the explorer have the same for the solution (commit, pull, push) and for files (show changes, commit this file, discard, history of this file). Open documents without unsaved
changes are read again after a pull, a switch of the branch or a discard.

## The server

Only **https** addresses are supported (LibGit2Sharp has no ssh): `https://github.com/you/project.git`. When the server asks, a dialog asks for a user name and a password - for GitHub, GitLab and most
servers a **personal access token**, not your account password. It is kept until the editor is closed (not on disk), and asked for again when the server did not accept it. Public repositories can be cloned
without credentials.

## Not (yet) there

Marks of changed lines in the editor margin, staging single lines, rebase, stashing, tags, ssh addresses, storing credentials on disk. Everything git can do beyond that works as usual outside of the editor
(the editor reads the state again when it gets the focus).
