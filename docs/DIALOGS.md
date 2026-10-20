# Dialogs (`#import "dialogs"`)

The standard library `dialogs` (package `fire-dialogs`, project `packages/dialogs`, fire only) has the dialogs a program needs now and then: choosing a file to open,
a file to save, a folder, and a message with buttons - like the common dialogs of Windows. They are built with the user interface library (`ui`) and use
`filesystem`, so they look and work the same everywhere a window can open (Windows, Linux, macOS, in the VM and in a native program); they are no dialogs of the
operating system.

```fire
#import "dialogs"

var path = Dialogs.OpenFileDialog.Show("Text files|*.txt|All files|*.*")     // undefined when cancelled
if (path != undefined) { print(IO.File.ReadAllText(path)) }

var target = Dialogs.SaveFileDialog.Show("Text files|*.txt", "notes.txt")
var folder = Dialogs.FolderBrowserDialog.Show("Where should the pictures go?")

if (Dialogs.MessageBox.Show("Save the changes?", "Editor", Dialogs.MessageBoxButtons.YesNoCancel) == Dialogs.DialogResult.Yes) { ... }
```

With more settings, like in .NET:

```fire
var d = new Dialogs.OpenFileDialog()
d.Title = "Open a picture"
d.Filter = "Pictures|*.png;*.bmp;*.gif|All files|*.*"
d.FilterIndex = 1
d.InitialDirectory = IO.Directory.GetHomeDirectory()
if (d.ShowDialog() == Dialogs.DialogResult.OK) { print(d.FileName) }
```

A dialog is **modal for the code that opens it**: `ShowDialog()` (and `Show`) open a window of their own and cycle it on the calling thread until the user is done;
then the code goes on. Called from a click handler of an interface that runs with `Run()`, the interface waits meanwhile. Closing the window with its frame or
Escape cancels.

## The file dialogs

On top the folder (`Up`, `Home`, the path - type one and press Enter), on the left the places (home and its Desktop, Documents, Downloads, ... folders, the current
folder, the temporary folder, the drives), in the middle the folders and files with size and date, below the name and the file type.

- A double click or Enter on a folder opens it, on a file chooses it; Backspace in the list goes up a folder; Tab moves between the parts.
- The name box: a file name (or a path) and Enter chooses it; a folder name opens the folder; a pattern like `*.log` filters the list.
- `Filter`: pairs of a text and patterns, `"Text|*.txt|Pictures|*.png;*.bmp"` (empty: all files); `FilterIndex` counts from 1 and tells at the end which one was chosen.
- `FileName`: the result (a full path) - set before, it is the proposed name (and its folder the start folder); `InitialDirectory` (else the current directory).
- `ShowHiddenFiles` also shows names that start with `.`.
- `OpenFileDialog`: `CheckFileExists` (default true) - a file that is not there is not taken (a line below says why).
- `SaveFileDialog`: `OverwritePrompt` (default true) asks before an existing file is taken; `CheckPathExists`; a name without an extension gets `DefaultExt`
  (`"txt"` or `".txt"`), else the extension of the chosen file type if it has exactly one (`*.txt`).
- `FolderBrowserDialog`: shows only folders; `SelectedPath` (start and result), `Description` (the caption); a name chooses that folder in the open one, an empty name the open folder.

Static shortcuts: `OpenFileDialog.Show(filter = "", title = "", initialDirectory = "")`, `SaveFileDialog.Show(filter = "", fileName = "", title = "", initialDirectory = "")`,
`FolderBrowserDialog.Show(description = "", selectedPath = "")` - each gives the path or `undefined`.

## Message boxes

`Dialogs.MessageBox.Show(text, caption = "", buttons = Dialogs.MessageBoxButtons.OK)` gives a `Dialogs.DialogResult`: `OK`, `Cancel`, `Yes`, `No`.
`MessageBoxButtons`: `OK`, `OKCancel`, `YesNo`, `YesNoCancel`. The text may have several lines (`\n`). The first button has the focus (Enter), Tab moves to the next,
Escape gives `Cancel` (with `YesNo`: `No`, with `OK`: `OK`).

## Limits

- One file at a time (no multiple selection); no new folder from inside the dialog; no preview.
- The windows of the interface that opened the dialog go on drawing when they are shown by a `WindowManager` thread (`Show()`), and their input is handled at the
  program's safe points - only an interface run with `Run()` waits.
- The look is that of the `ui` library (its theme, its font).

Tests: `packages/dialogs/testing` (the parts without a window) and the suite block "Dialoge" (`src/fire.Testing/DialogTests.cs`: the dialogs with fake windows that play
keyboard input; `FIRE_DIALOG_SHOTS=<folder>` saves the last picture of every window).
