# Input: keyboard, mouse, touch, joystick, drag & drop

An interface can be used with all four inputs; they arrive at the same methods of the elements (`MouseDown`, `KeyDown`, ...), so an element need not
know which one the user has.

## Focus and keyboard

| Key | Effect |
| --- | --- |
| Tab / Shift+Tab | the next / previous focusable element (visible, enabled; in drawing order) |
| Arrow keys | the focus moves to the nearest element in that direction, by position on the screen; inside a `ScrollViewer` the element is scrolled into view, and when there is none left in that direction the arrow scrolls like the wheel |
| Enter / Space | clicks the focused button, toggles a check box |
| Escape | closes a menu or popup |

An element keeps the arrows it needs itself: the text field Left/Right/Home/End, the list and the tree Up/Down (at the first and last row the list
lets the key go, so the user can leave it), `RadioButtons` the arrows between their options, the `TextArea` all arrows and Page Up/Down. Own elements
override `bool WantsKey(int key)`; Tab goes to an element only when `bool TakesTab()` says so (a `TextArea` with `AcceptsTab`) - Shift+Tab and
Ctrl+Tab always move the focus. `ui.SetFocus(element)` sets the focus from code; `ui.focusElement` is the focused one.

**Keys before the elements:** `ui.onKeyPreview = func (int key, int mod) => { ... }` sees every key first; returning `true` uses it up. That is where
shortcuts go:

```fire
#import "ui"

var fb = new Framebuffer(300, 100)
var win = new Window(fb, "Shortcuts")
var ui = new UI.Root(fb, win)
var status = new UI.Label("Press Ctrl+S", 16, 16)
ui.Add(status)
ui.onKeyPreview = func (int key, int mod) => {
    if (UI.Keys.Shortcut(mod) && key == 115) {      // Ctrl+S (Cmd+S on the Mac); 115 = 's'
        status.Text = "saved"
        return true
    }
    return false
}
ui.Run()
```

`UI.Keys` names the key codes (`Enter()`, `Escape()`, `Tab()`, `Left()`, `Up()`, `Home()`, `PageDown()`, `Delete()`, `F4()`, letters `A()`, `C()`,
`V()`, ...); letters and digits are their character code. `UI.Keys.Shift(mod)` and `UI.Keys.Shortcut(mod)` (Ctrl, or Cmd on the Mac) test the
modifiers; `ui.modifiers` holds the keys held right now.

## Mouse

Click, drag, wheel (the horizontal wheel or Shift+wheel scrolls sideways), the right button for [context menus](menus.md#context-menus).
`ui.mouseX`/`ui.mouseY` are the last position. Every element has a mouse pointer: `element.Cursor = UI.Cursor.Hand` (`Arrow`, `IBeam`, `Wait`, `Cross`,
`Progress`, `SizeNWSE`, `SizeNESW`, `SizeWE`, `SizeNS`, `SizeAll`, `No`, `Hand`; `Inherit`: that of the element around it); text fields show the
I-beam by themselves. (In markup `Cursor` is planned.)

### Double clicks

`ui.clickCount` is, during the `MouseDown` of an element, 1 for a single click, 2 for a double click, 3 for a triple click and so on. A press counts
on when it is the same button as before, at most `ui.doubleClickTime` milliseconds (400) later and at most `ui.doubleClickDistance` pixels (4) away.
The time comes from `ui.clock` (a lambda returning milliseconds, for example `func () => { return DateTime.Now.Ticks / 10000 }` with `#import "time"`);
without one, the root counts cycles (60 per second). Lists report a double click on a row as `OnActivate`, a tree also opens or closes the node, a text
field selects a word (a line or everything with three clicks).

## Touch

A finger acts like the left mouse button: it presses on touch-down and clicks when lifted on the same element (lifted elsewhere: nothing). Moved more
than `ui.touchSlop` pixels (10) over a `ScrollViewer`, a list, a tree or a text area, it **swipes**: the content follows the finger and the element
under it does not click; a scroll bar thumb under the finger is dragged instead. Only one finger counts. No hover highlight stays after lifting. Own
swipeable elements override `CanPan()`, `Dragging()` and `PanBy(root, dx, dy)`.

The root switches off the window's mouse emulation of touches (`window.TouchMouse = false`), so a finger does not arrive twice.

## Joystick

The hat and the left stick (axes 0 and 1, with a dead zone: out at 0.6, back under 0.3) become the arrow keys; holding repeats them
(`ui.joyRepeatDelay` = 24 cycles, then every `ui.joyRepeatInterval` = 6 cycles). Button 0 = Enter, button 1 = Escape, buttons 4 / 5 = Shift+Tab / Tab
(on an Xbox pad: A, B, LB, RB). `ui.joyButtonActivate`, `joyButtonCancel`, `joyButtonPrevious`, `joyButtonNext`, `joyAxisX`, `joyAxisY` change the
mapping (-1 switches a button off). An interface can be used with a game pad alone.

## Clipboard

Text fields copy, cut and paste by themselves (Ctrl+C/X/V, also from their methods `Copy()`, `Cut()`, `Paste()`). Programs use `Clipboard` of
`#import "windows"`: `Clipboard.SetText(text)`, `Clipboard.GetText()`, `Clipboard.HasText` (a property) (see [graphics](../graphics/README.md#clipboard)).

## Drag & drop

An element becomes a **source** with `OnDragStart`: when the user holds the left button on it and moves 4 pixels, the root calls
`OnDragStart(x, y)` (where the button went down); a `UI.DragData` starts the drag, `undefined` asks the element around it. The pressed element then
gets no `MouseUp` (a button does not click).

An element becomes a **target** with `AllowDrop = true`:

| Lambda | Meaning |
| --- | --- |
| `OnDragOver(data, x, y)` | what a drop here would do: `UI.DropEffect.None`, `Copy`, `Move`, `Link` (nothing returned: `Copy`) |
| `OnDrop(data, x, y)` | takes the data |
| `OnDragLeave()` | the pointer left the target |
| `OnDragDone(data, effect)` (on the source) | how it ended: `None` (Escape, refused), `Copy`, `Move` (remove what you gave) |

```fire
#import "ui"

var fb = new Framebuffer(360, 200)
var win = new Window(fb, "Drag & drop")
var ui = new UI.Root(fb, win)
var list = new UI.ListBox(10, 10, 160, 150)
foreach (f in ["apple", "banana", "cherry"]) { list.Add(f) }
var basket = new UI.StackPanel(190, 10, 160, 150)
basket.ShowBorder = true
basket.Add(new UI.Label("Basket"))
list.OnDragStart = func (int x, int y) => {
    if (list.selectedItem == undefined) { return undefined }
    return new UI.DragData(list.selectedItem, list.selectedItem, "fruit")
}
basket.AllowDrop = true
basket.OnDragOver = func (UI.DragData d, int x, int y) => {
    if (d.Kind == "fruit") { return UI.DropEffect.Move }
    return UI.DropEffect.None
}
basket.OnDrop = func (UI.DragData d, int x, int y) => {
    basket.Add(new UI.Label(d.Text))
    return UI.DropEffect.Move
}
list.OnDragDone = func (UI.DragData d, UI.DropEffect e) => { if (e == UI.DropEffect.Move) { list.Remove(d.Value) } }
ui.Add(list)
ui.Add(basket)
ui.Run()
```

`UI.DragData(text, value, kind)` carries `Text`, `Files` (paths), `Value` (any value of the program), `Kind` (a word for targets to tell drags apart),
`Source` (the element it comes from), `HasText`, `HasFiles`, `External` (from another program). Own elements override `DragStart`, `AcceptsDrops`,
`DragOver`, `Drop`, `DragLeave` and `DragDone` instead of the lambdas. `ui.IsDragging` tells whether a drag is going on. (In markup the drag events
are planned.)

**From other programs:** files and text dropped on the window go to the same targets - the element under the point that accepts, else
`ui.onDrop(data, x, y)`. While something is dragged over the window, the target already gets `OnDragOver` with `External` true and `Pending` true
(the content is not known before the drop), so it can highlight itself. Text fields take text and files (their paths) by themselves.

**Into other programs:** a drag whose data has `Text` or `Files` leaves the window when the pointer does: a file manager, an editor or a browser
takes it, and the source learns with `OnDragDone` what happened (`Move`: a file manager moved the files). This works on Windows, X11 and Wayland;
on macOS and in the browser (`window.CanStartDrag()` is false) the drag stays in the window. `AllowExternal = false` keeps a drag inside; a drag with
only a `Value` stays inside anyway.

**The picture at the pointer:** `data.Image` (a framebuffer, with its corner at `ImageX`/`ImageY` from the pointer), else a see-through snapshot of the
source element (not for text fields, lists and trees - an element says so with `DragSnapshot()`; `data.Snapshot = false` switches it off), else a
small label with the text, the file name or the kind. `data.ShowImage = false`: no picture.
