# Docking (`#import "ui.docking"`)

The standard library `ui.docking` (package `fire-ui.docking`, project `packages/ui.docking`, fire only, namespace `UI.Docking`) adds what the windows of IDEs and editors
are made of to the user interface library (docs/UI.md):

* a **dock manager** that fills the window and arranges **tool windows** at its four edges - in panes with a caption, as tabs on top of each other, with splitters - or lets them
  float above the window; the user drags them by their caption or tab, guides show where they can go;
* **toolstrips** - bars with buttons, labels, separators, text fields, combo boxes and split buttons - in rows at all four edges, movable by their grip, or floating; what does not fit
  goes into an **overflow menu**;
* a **status bar**.

```fire
#import "ui.docking"

var fb = new Framebuffer(800, 600)
var window = new Window(fb, "Editor")
var ui = new UI.Root(fb, window)
var dock = new UI.Docking.DockManager()
ui.Add(dock)

// a toolstrip at the top
var tools = new UI.Docking.ToolStrip("Standard")
tools.AddButton("New", func () => { print("new") }, UI.Icons.File())
tools.AddButton("Open", func () => { print("open") }, UI.Icons.Folder())
tools.AddSeparator()
var run = tools.AddSplitButton("Run", func () => { print("run") })
run.AddItem("Run without debugging", func () => { print("run only") })
tools.AddLabel("Target:")
var target = tools.AddComboBox(90)
target.Add("Debug")
target.Add("Release")
target.SelectedIndex = 0
tools.AddTextBox("", 120)
dock.AddToolStrip(tools)

// tool windows
var files = new UI.ListBox()
files.Add("main.fire")
dock.Add(new UI.Docking.ToolWindow("Explorer", files), UI.Dock.Left)
var output = dock.Add(new UI.Docking.ToolWindow("Output", new UI.TextBox("")), UI.Dock.Bottom)
dock.AddTabbed(new UI.Docking.ToolWindow("Errors", new UI.ListBox()), output)

// the centre and the status bar
dock.SetContent(new UI.TextBox("the document"))
var status = new UI.Docking.StatusBar()
status.AddLabel("Ready", true)
status.AddLabel("Ln 1, Col 1")
dock.SetStatusBar(status)

ui.Run()
```

## The dock manager

`new UI.Docking.DockManager()` - added to the root (or to a panel), it fills the space it gets; without one of its own (`width`/`height` -1, in the root) the whole window.
From the outside in it holds: the status bar (bottom), the four **toolstrip panels** (`topStrips`, `bottomStrips`, `leftStrips`, `rightStrips`), the **edges** with the docked panes
(left and right over the whole height, top and bottom between them), the **centre** (`SetContent(element)`), and the floating frames above everything.

| | |
|---|---|
| `SetContent(element)` | the element in the centre; it belongs to the manager (the previous one is destroyed). With `fillContent` (default) it gets no fixed size and fills the centre |
| `SetStatusBar(bar)` | the status bar at the bottom |
| `AddToolStrip(strip, side = UI.Dock.Top, row = 0, offset = -1)` | docks a strip at an edge, in row `row` (-1: a new row), at `offset` pixels (-1: after the strips of the row) |
| `FloatToolStrip(strip, x, y)` | lets a strip float |
| `Add(window, side = UI.Dock.Left)` | docks a tool window at an edge in a pane of its own (at the end of the edge) |
| `AddTabbed(window, other)` | adds a tool window as a tab to the pane of `other` |
| `AddFloating(window, x, y, width, height)` | lets a tool window float |
| `SetSideSize(side, size)`, `leftSize` ... | the width (left, right) or height (top, bottom) of an edge |
| `ToolWindows()`, `FindToolWindow(title)`, `ToolStrips()` | all tool windows (docked, floating, hidden), all strips |
| `leftPanes` ..., `floats`, `hidden` | the panes of the edges, the floating frames, the hidden tool windows |

The **splitters** - between an edge and the centre, and between two panes of an edge - are dragged with the mouse; an edge keeps at least `minCenter` pixels for the centre.

## Tool windows

`new UI.Docking.ToolWindow(title, content)` - a title and one element. The content belongs to the window (`SetContent` replaces it) and fills it (`fillContent`, default true:
it loses a fixed width and height).

| | |
|---|---|
| `title`, `Icon` (a `UI.Geometry`, shown in the caption) | |
| `canClose`, `canFloat` | may the user close it, let it float |
| `onClose` | a lambda, called when it is closed (the close button, or `Close()`) |
| `Show()` | shows a hidden window where it was: as a tab of its last pane if that still exists, otherwise in a pane of its own at its place along the edge, or floating; a visible one becomes the visible tab |
| `Hide()`, `Close()` | hides it (Close calls `onClose` first) |
| `Float(x = -1, y = -1)`, `DockTo(side)` | lets it float (-1: where it floated last), docks it at an edge |
| `Activate()` | makes it the visible tab of its pane |
| `isDocked`, `isFloating`, `isHidden`, `side`, `Pane()` | where it is |

A **pane** (`UI.Docking.DockPane`) shows one or more tool windows: the caption with the title of the visible one (in the accent colour while the focus is inside), a button to
float/dock and the close button; with several windows tabs at the bottom. A click on the caption gives the focus to the first element of the window that takes it.

**With the mouse:**

* **Drag the caption** (the whole pane) or **a tab** (only that window) a few pixels: it floats and follows the pointer. **Guides** appear: one at each edge of the dock area, and
  over a docked pane a small compass - its centre adds the window as a tab, the two arrows put it before or after that pane along the edge. A shaded area shows where it would go.
  Releasing over a guide docks it there, elsewhere it stays floating.
* **Double click** on the caption, or the float button: a docked pane floats, a floating one goes back to the edge it came from.
* A floating pane is **resized** at its edges and corners.

## Toolstrips

`new UI.Docking.ToolStrip(text)` - `Text` (the argument `text`) is the title while it floats. Items are added with:

| | |
|---|---|
| `AddButton(text, onClick, icon)` | a `ToolStripButton`: text and/or symbol (`Icon` a UI.Geometry such as `UI.Icons.Folder()`, or `Image` a Framebuffer, `IconSize`), flat until the pointer is over it; `Checkable`/`IsChecked`; `showText = false` shows only the symbol; `OnClick`/`TakeClicked()` |
| `AddSplitButton(text, onClick, icon)` | a `ToolStripSplitButton`: the button runs `OnClick`, the arrow opens a menu: `AddItem(header, onClick)`, `Add(menuItem)` (MenuItem as in menus, also with submenus) |
| `AddLabel(text, spring = false)` | a `ToolStripLabel`; with `spring` it takes the room that is left (several springs share it) |
| `AddSeparator()` | a `ToolStripSeparator`: a line, across a horizontal strip, along a vertical one |
| `AddTextBox(text, width)` | a `ToolStripTextBox` (a `UI.TextBox`) |
| `AddComboBox(width)` | a `ToolStripComboBox` (a `UI.ComboBox`, see docs/UI.md) |
| `AddProgressBar(width)` | a `ToolStripProgressBar`: `value` of `maximum` |
| `Add(element)` | any other element |

**Overflow:** when the strip is shorter than its items, the ones that do not fit go into the menu of the **»** button at its end (in it, buttons show their text, split buttons
open their menu to the side, text fields and combo boxes work as in the strip). An item's `overflow` decides: `UI.Docking.Overflow.AsNeeded` (default), `Never` (always stays in
the strip), `Always` (only in the menu). `canOverflow = false` switches the menu off.

**Moving:** a strip is dragged by its **grip** (the dots at its start): along its row, into another row (or a new one at the inner side of the panel), to another edge (at the left and
right edge it stands upright), or out of the panels - then it floats in a small frame with its title and a close button (which hides it: `strip.Visible = true` shows it again).
Dragged by the title back onto a panel, it docks there. In a row the strips sit at their `offset` as far as there is room; otherwise they are pushed together and finally shortened -
their items go into the overflow menu. `showGrip = false` fixes a strip in place.

`row`, `offset`, `Horizontal`, `floating` tell where a strip is; `ToolStripPanel` (`topStrips` ...) has `strips`, `RowCount()`, `RowStrips(row)`.

## The status bar

`new UI.Docking.StatusBar()` - a strip without grip and overflow at the bottom of the manager (`SetStatusBar`), with the same items: `AddLabel("Ready", true)` (spring: fills the
room), further labels, buttons, a `ToolStripProgressBar`.

## Notes

* Everything is drawn by the UI library and works in the VM and in native programs alike.
* The manager owns its panes, tool windows and strips; moving them between edges, tabs and floating frames never destroys them. Destroying the manager destroys all of them.
* The dragged pane floats while it is dragged; a drop that docks it puts the same pane (with all its tabs) at the new place.
* Tests: the logic in `packages/ui.docking/testing/` (`forge test`), dragging, drawing and the overflow menu with a fake window in `src/fire.Testing/DockingTests.cs`.
