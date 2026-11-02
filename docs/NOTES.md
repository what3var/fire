# Notes: todo lists and project overviews

IDEs know projects and solutions, but not the notes that belong to them. spark keeps two kinds of notes next to a project (or a solution), as plain Markdown files that
live in the repository with the code:

- a **todo list** (`notes/todo.md`), shown and edited in the tool window **Todo**,
- a **project overview** (`notes/overview.md`), a surface with text, containers, drawings and lines, opened in its own editor.

Both are Markdown that any other program shows sensibly: what spark needs on top of Markdown is written so that other renderers (GitHub, other editors) hide it.

Status: the todo list is built (format, reading and writing back, the tool window). The overview is specified here and built in the steps listed at the end.

## The folder `notes/`

The notes of a project are in the folder **`notes/`** next to its `.fireproj`; the notes of a solution in `notes/` next to its `.firesln`. The folder does not exist at first: it is made
when the first entry is written (the first task in the Todo window, the first overview).

```
App/
  App.fireproj
  main.fire
  notes/
    todo.md
    overview.md
All.firesln
notes/
  todo.md            the todo list of the solution
```

`"notes": "docs/notes"` in the `.fireproj` (or the `.firesln`) puts the folder somewhere else; the path is relative to that file. Markdown files are not code, so the notes are
content of the project (docs/PROJECTS.md): they are not compiled and change nothing in a build.

## Todo lists

### The format

A todo list is a Markdown task list. Headings group the tasks, indented tasks are subtasks:

```markdown
# Todo

## Circuits
- [ ] Show the probe values in a tooltip
- [/] Falstad import: group linked pots
  - [x] Parser
  - [ ] Dialog
- [#] Tube models: waiting for real measurements
- [x] RC sample

## Docs
- [ ] CIRCUITS.md in English
```

| Mark | State | In spark |
|---|---|---|
| `[ ]` | open | normal |
| `[/]` | in progress | accent colour |
| `[#]` | blocked (a stopper) | red background and a stop sign; first in its group |
| `[x]` | done | dimmed, struck through (`[X]` is read as done, too) |

`[ ]` and `[x]` are the task lists of GitHub; `[/]` and `[#]` are shown there as text in front of the task, which stays readable.

A task is a list item (`-`, `*`, `+` or `1.`, `1)`) that starts with one of the marks. A heading of level 2 or deeper starts a group; the heading of level 1 is the title of the
list. Everything else in the file - paragraphs, notes below a task, list items without a mark, code blocks - belongs to the file and is kept as it is; the Todo window does not
show it.

### Writing back

The Todo window changes the file **only where something changed**, so the file can be edited by hand, too:

- a new state changes the character between the brackets, nothing else;
- a changed text replaces the text of that one line;
- a new task goes below the last task of its group (a group that does not exist yet is added at the end of the file, a list without groups gets the task at its end);
- deleting a task deletes its line and the lines of its subtasks and notes (the lines that are indented below it).

The line endings of the file are kept (`\n` or `\r\n`). When the first task is written, the file is made with a title: `# Todo` followed by the task.

spark watches the files: a change from outside (a `git pull`, another editor) is read again at once.

### The Todo window

**View → Todo** (and **Project → Todo List**) shows the todo list of the **project** of the active document (or of the project selected in the Solution Explorer); the box at
the top switches to the list of the **solution**. Project lists are the usual case, a solution list is for what concerns all projects.

- A click on the mark of a task moves it on: open → in progress → done → open (a blocked task becomes open). **Blocked** is set in the context menu (so that it is not set by
  mistake), as are the other states.
- A double click on the text edits it in place; Enter keeps the change, Esc throws it away.
- The box at the bottom adds a task to the group of the selected task (or to the end of the list).
- The context menu deletes a task and opens `todo.md` as a document.
- Keys on the selected task: **Space** moves it on like a click, **F2** edits it, **Delete** deletes it, **Up**/**Down** select another one.

When `todo.md` is open as a document, the window works on its text: a change in the window shows up in the document at once. While the document has unsaved changes, the
window only shows the list (it says why) - save the document to change the list in the window again.

Blocked tasks are shown first in their group, so that they stand out; the order in the file stays as it is.

## Project overviews

### What it is

An overview is a surface on which you write and draw. Its text is anchored at the top left. On top of it lie **containers** at fixed places: each holds text, drawings and
further containers - or, instead, another document (the todo list, another overview, a `.biscuit`, a picture), shown live. **Lines** connect containers and follow them when
they move.

### The format

An overview is a Markdown file. What only spark understands is written as Markdown **link reference definitions** - `[label]: <destination> "title"` - which Markdown never
shows. The label says what it is, the destination (between `<` and `>`) holds its attributes, the title (optional) is its name:

```markdown
# Fuzz pedal

Goal: a distortion that runs on the ESP32.

[container]: <x=420 y=180 w=300 src="todo.md"> "todos"

[container]: <x=40 y=180 w=340> "signal"
## Signal path
Input → Clipper → Tone → Out

[container]: <x=16 y=110 w=260> "inner"
Nested text

[/]: <>

[sketch]: <>
[rect]: <0 90 300 60 stroke=#147AD0>
[path]: <M 20 120 L 280 120 stroke=#ff4070 width=2 arrow=end>
[/]: <>
[/]: <>

[line]: <from=signal to=todos arrow=end> "open"
```

| Element | Meaning |
|---|---|
| text outside of containers | the text of the overview, anchored at the top left, flowing in a default width |
| `[container]: <x= y= w= [h=]> "name"` … `[/]: <>` | a container at x/y, w wide; without `h` it grows with its content. Inside: Markdown, sketches, containers |
| `[container]: <x= y= w= src="file"> "name"` | a container that shows another document; it has no content and no end |
| `[sketch]: <>` … `[/]: <>` | drawings: one shape per line (`rect`, `ellipse`, `path` in SVG path syntax, `text`), coordinates relative to the sketch's container |
| `[line]: <from=name to=name arrow=end> "label"` | a line between two named containers; it follows them when they move |
| `[/]: <>` or `[/container]: <>` | ends the innermost open container or sketch; with a label spark checks that it ends what it says |

The name (the title in `"…"`) is optional; it may also be written in `'…'` or `(…)`. Lines need names to refer to. Attributes are `key=value`, a value with spaces in `"…"`.

**What other programs show.** A container's content is ordinary Markdown, so GitHub shows its text and headings in the order of the file. Content that should appear as code
elsewhere is put into a fenced code block (```` ``` ````); a sketch consists only of definitions, so it is not shown at all.

**Rules that keep it invisible elsewhere** (spark writes files that keep them, and warns when a hand-written file breaks them):

- a definition stands on a line of its own, after an empty line - or directly after a heading or another definition (otherwise it belongs to the paragraph or list item above it
  and is shown as a link);
- at most 3 spaces in front of it (4 make it a code block): nested containers are not indented;
- no `<` or `>` inside `<…>` (`\<` and `\>` if needed).

### Positions

Positions are **absolute**, in pixels at a zoom of 100 %, measured from the top left corner of the **container the element is in** (the overview itself for the outermost ones).
Moving a container changes its own line only - not the lines of what it holds - so diffs stay small and merges rarely conflict. What is written later lies on top. Zoom and
*Fit to View* only change the view, never the file.

### Editing

- A click selects a container; it is moved by dragging and resized by its handles.
- A **double click** edits the container in place: it turns into a Markdown editor of the same place and size, with the colours of the Markdown editor. Its own text is edited
  there; the definitions of position and size are kept by the surface. The containers it holds stay visible above the editor at **half opacity** - clearly visible, but not
  in the way - and are edited themselves by a double click. A click outside or Esc shows the text rendered again.
- The text of the overview itself is edited the same way.
- A todo list shown in a container is a working list: its tasks are ticked off there, too. The overview and the Todo window share one document per file, so a change in one is
  seen in the other at once (and written to the file only where it changed, see above).
- A document that shows itself (directly or through others) is shown once and then as a link, not again.

## Steps

1. **Todo** (built): the folder `notes/`, the format with four states, writing back only what changed, the Todo window for projects and solutions.
2. **Overview, viewing**: reading containers, sketches and lines; showing them; containers with documents, todo lists with ticking off.
3. **Overview, editing**: moving and resizing, the editor in a container (with the inner containers at half opacity).
4. **Drawing**: shapes, pen, lines between containers.
5. **Fit to View** and finishing.
