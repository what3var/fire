namespace fire.UI.Bridge
{
    public static partial class UiBridge
    {
        /// <summary>
        /// Der fire-Quelltext der Oberflächen-Bibliothek (`namespace UI`), VOR das Nutzer-Skript zu setzen, wenn es `#import "ui"` gibt.
        ///
        /// Aufbau: `UI.Root` ist das oberste Element. Sein Konstruktor nimmt den Framebuffer, in den gezeichnet wird, und das
        /// Fenster, von dem die Ereignisse (Maus, Tastatur, Text) kommen; er legt selbst die Konsole zum Zeichnen an und meldet
        /// schaltet dort die Ereignis-Warteschlange ein (`Window.EnableEvents`). `Root.Tick()` zeichnet die gesamte Oberfläche neu, lässt
        /// das Fenster einen Zyklus laufen (Ereignisse abholen, Framebuffer anzeigen) und verarbeitet die angekommenen Ereignisse -
        /// jede Schleife ruft es einmal auf.
        ///
        /// Elemente liegen in Containern (`Panel`, `Stack`) und haben Position und Größe relativ zu ihrem Container. Ein Element
        /// gehört seinem Container (`Add` ruft `TakeTo`): die Oberfläche lebt so, solange ihr Root lebt, auch wenn sie in einer
        /// Hilfsfunktion aufgebaut wurde.
        ///
        /// Auf Klicks reagiert man auf zwei Wegen: mit einem Lambda (`button.onClick = func () => { ... }`; es läuft im
        /// Hauptprogramm und sieht die echten globalen Variablen) oder durch Abfragen in der eigenen Schleife
        /// (`if (button.TakeClicked()) { ... }`). Die Ereignisse holt `Root.Tick` per `Window.NextEvent` ab, nicht per Callback.
        ///
        /// Zeichnen geht über die Methoden von `Console` (FillRect/DrawRect/DrawLine/DrawText mit rohen Farbwerten, siehe
        /// `UI.Color.Rgb`); Text ist die eingebaute Schrift (dicktengleich, 8x14 Pixel pro Zeichen).
        /// </summary>
        public const string PreludeSource = """
            namespace UI {
                // Farben sind rohe 32-Bit-Werte: r + g*256 + b*65536 + a*16777216 (a = 255: deckend).
                class Color {
                    static int Rgb(int r, int g, int b) { return r + g * 256 + b * 65536 + 0xFF000000 }
                    static int Transparent() { return 0 }
                }

                // Tastencodes, wie sie KeyDown liefert (SDL3-Keycodes).
                class Keys {
                    static int Backspace() { return 8 }
                    static int Tab() { return 9 }
                    static int Enter() { return 13 }
                    static int Escape() { return 27 }
                    static int Space() { return 32 }
                    static int Delete() { return 127 }
                    static int Right() { return 1073741903 }
                    static int Left() { return 1073741904 }
                    static int Down() { return 1073741905 }
                    static int Up() { return 1073741906 }
                    static int Home() { return 1073741898 }
                    static int End() { return 1073741901 }
                }

                // Converters of the data bindings of the markup (docs/UI_MARKUP.md): Convert goes from the source to the property of the element, ConvertBack the other
                // way (TwoWay). Derive from Converter for your own and override what you need.
                class Converter {
                    Convert(value) { return value }
                    ConvertBack(value) { return value }
                }

                class NotConverter : Converter {
                    Convert(value) { return !value }
                    ConvertBack(value) { return !value }
                }

                class IsEmptyConverter : Converter {
                    Convert(value) {
                        if (value == undefined) { return true }
                        var text = "" + value
                        return text == ""
                    }
                }

                class NotEmptyConverter : Converter {
                    Convert(value) {
                        if (value == undefined) { return false }
                        var text = "" + value
                        return text != ""
                    }
                }

                class TextConverter : Converter {
                    Convert(value) {
                        if (value == undefined) { return "" }
                        return "" + value
                    }
                }

                // Die Farben der Oberfläche (Root.theme) - einzelne Felder lassen sich nach dem Anlegen des Roots ändern.
                class Theme {
                    int back
                    int panel
                    int face
                    int faceHover
                    int facePressed
                    int faceDisabled
                    int border
                    int borderFocus
                    int text
                    int textDisabled
                    int inputBack
                    int accent

                    construct() {
                        this.back = UI.Color.Rgb(240, 240, 240)
                        this.panel = UI.Color.Rgb(250, 250, 250)
                        this.face = UI.Color.Rgb(225, 225, 225)
                        this.faceHover = UI.Color.Rgb(229, 241, 251)
                        this.facePressed = UI.Color.Rgb(204, 228, 247)
                        this.faceDisabled = UI.Color.Rgb(204, 204, 204)
                        this.border = UI.Color.Rgb(120, 120, 120)
                        this.borderFocus = UI.Color.Rgb(0, 120, 215)
                        this.text = UI.Color.Rgb(0, 0, 0)
                        this.textDisabled = UI.Color.Rgb(131, 131, 131)
                        this.inputBack = UI.Color.Rgb(255, 255, 255)
                        this.accent = UI.Color.Rgb(0, 120, 215)
                    }
                }

                // Basis aller Elemente. x/y/width/height gelten relativ zum Container; ax/ay ist die Position auf dem Bildschirm beim
                // letzten Zeichnen (damit Treffertest und Zeichnen immer dasselbe meinen).
                class Element {
                    int x
                    int y
                    int width
                    int height
                    int ax
                    int ay
                    bool visible = true
                    bool enabled = true
                    bool hover = false
                    bool pressed = false
                    bool focused = false

                    construct(int x, int y, int width, int height) {
                        this.x = x
                        this.y = y
                        this.width = width
                        this.height = height
                    }

                    // Kann das Element den Tastaturfokus bekommen (Tab, Klick)?
                    bool Focusable() { return false }

                    Draw(root, int ax, int ay) {
                        this.ax = ax
                        this.ay = ay
                    }

                    // Das Element (oder ein Nachfahre) unter der Bildschirmposition (px, py), sonst undefined.
                    HitTest(int px, int py) {
                        if (!this.visible) { return undefined }
                        if (px >= this.ax && px < this.ax + this.width && py >= this.ay && py < this.ay + this.height) { return this }
                        return undefined
                    }

                    // Sammelt alle fokussierbaren Elemente in Zeichenreihenfolge (für Tab).
                    Collect(list) {
                        if (this.visible && this.enabled && this.Focusable()) { list.Add(this) }
                    }

                    bool Contains(int px, int py) {
                        return px >= this.ax && px < this.ax + this.width && py >= this.ay && py < this.ay + this.height
                    }

                    MouseDown(root, int button, int px, int py) { }
                    MouseUp(root, int button, int px, int py) { }
                    MouseMove(root, int px, int py) { }
                    KeyDown(root, int key, int mod) { }
                    TextInput(root, string text) { }
                }

                // Container mit absolut positionierten Kindern.
                class Panel : Element {
                    List children
                    bool showBorder = false
                    // Hintergrund: 0 = Farbe des Themes, 1 = keiner (durchsichtig), sonst ein Farbwert
                    int background = 0

                    construct(int x, int y, int width, int height) : base(x, y, width, height) {
                        this.children = new List()
                    }

                    // Das Kind gehört ab jetzt diesem Panel (es lebt so lange wie dieses).
                    Add(child) {
                        child.TakeTo(this)
                        this.children.Add(child)
                    }

                    Layout() { }

                    Draw(root, int ax, int ay) {
                        base.Draw(root, ax, ay)
                        if (this.background != 1) {
                            var color = this.background
                            if (color == 0) { color = root.theme.panel }
                            root.console.FillRect(ax, ay, this.width, this.height, color)
                        }
                        if (this.showBorder) {
                            root.console.DrawRect(ax, ay, this.width, this.height, root.theme.border)
                        }
                        this.Layout()
                        for (var i = 0; i < this.children.count; i = i + 1) {
                            var child = this.children[i]
                            if (child.visible) { child.Draw(root, ax + child.x, ay + child.y) }
                        }
                    }

                    HitTest(int px, int py) {
                        if (!this.visible) { return undefined }
                        if (!this.Contains(px, py)) { return undefined }
                        var i = this.children.count - 1
                        while (i >= 0) {
                            var hit = this.children[i].HitTest(px, py)
                            if (hit != undefined) { return hit }
                            i = i - 1
                        }
                        return this
                    }

                    Collect(list) {
                        if (!this.visible || !this.enabled) { return }
                        for (var i = 0; i < this.children.count; i = i + 1) {
                            this.children[i].Collect(list)
                        }
                    }
                }

                // Panel, das seine Kinder selbst nacheinander anordnet (untereinander oder nebeneinander); x/y der Kinder
                // werden dabei überschrieben, ihre Größe bleibt.
                class Stack : Panel {
                    bool horizontal
                    int spacing
                    int padding

                    construct(int x, int y, int width, int height, bool horizontal = false, int spacing = 4, int padding = 4) : base(x, y, width, height) {
                        this.horizontal = horizontal
                        this.spacing = spacing
                        this.padding = padding
                    }

                    Layout() {
                        var pos = this.padding
                        for (var i = 0; i < this.children.count; i = i + 1) {
                            var child = this.children[i]
                            if (!child.visible) { continue }
                            if (this.horizontal) {
                                child.x = pos
                                child.y = this.padding
                                pos = pos + child.width + this.spacing
                            } else {
                                child.x = this.padding
                                child.y = pos
                                pos = pos + child.height + this.spacing
                            }
                        }
                    }
                }

                class Label : Element {
                    string text
                    // 0 = Textfarbe des Themes
                    int color = 0

                    construct(string text, int x, int y) : base(x, y, 0, 0) {
                        this.text = text
                    }

                    Draw(root, int ax, int ay) {
                        this.width = root.cw * this.text.Length
                        this.height = root.ch
                        base.Draw(root, ax, ay)
                        var c = this.color
                        if (c == 0) { c = root.theme.text }
                        if (!this.enabled) { c = root.theme.textDisabled }
                        root.console.DrawText(ax, ay, this.text, c, 0)
                    }
                }

                class Button : Element {
                    string text
                    lambda onClick
                    bool clicked = false

                    construct(string text, int x, int y, int width = 90, int height = 26) : base(x, y, width, height) {
                        this.text = text
                    }

                    bool Focusable() { return true }

                    // Wurde die Schaltfläche seit dem letzten Abfragen geklickt? (setzt den Merker zurück)
                    bool TakeClicked() {
                        var was = this.clicked
                        this.clicked = false
                        return was
                    }

                    Click() {
                        this.clicked = true
                        var callback = this.onClick
                        if (callback != undefined) { callback() }
                    }

                    Draw(root, int ax, int ay) {
                        base.Draw(root, ax, ay)
                        var theme = root.theme
                        var face = theme.face
                        if (!this.enabled) { face = theme.faceDisabled }
                        else if (this.pressed && this.hover) { face = theme.facePressed }
                        else if (this.hover) { face = theme.faceHover }
                        root.console.FillRect(ax, ay, this.width, this.height, face)
                        var border = theme.border
                        if (this.focused) { border = theme.borderFocus }
                        root.console.DrawRect(ax, ay, this.width, this.height, border)

                        var color = theme.text
                        if (!this.enabled) { color = theme.textDisabled }
                        var tx = ax + (this.width - root.cw * this.text.Length) / 2
                        var ty = ay + (this.height - root.ch) / 2
                        root.console.DrawText(tx, ty, this.text, color, 0)
                    }

                    MouseDown(root, int button, int px, int py) {
                        if (button == 1) { this.pressed = true }
                    }

                    MouseUp(root, int button, int px, int py) {
                        var was = this.pressed
                        this.pressed = false
                        if (was && button == 1 && this.Contains(px, py)) { this.Click() }
                    }

                    KeyDown(root, int key, int mod) {
                        if (key == UI.Keys.Enter() || key == UI.Keys.Space()) { this.Click() }
                    }
                }

                class CheckBox : Element {
                    string text
                    bool isChecked = false
                    lambda onChange
                    bool changed = false

                    construct(string text, int x, int y, bool isChecked = false) : base(x, y, 0, 0) {
                        this.text = text
                        this.isChecked = isChecked
                    }

                    bool Focusable() { return true }

                    // Wurde der Zustand seit dem letzten Abfragen geändert? (setzt den Merker zurück)
                    bool TakeChanged() {
                        var was = this.changed
                        this.changed = false
                        return was
                    }

                    Toggle() {
                        this.isChecked = !this.isChecked
                        this.changed = true
                        var callback = this.onChange
                        if (callback != undefined) { callback() }
                    }

                    Draw(root, int ax, int ay) {
                        var box = 14
                        this.width = box + 6 + root.cw * this.text.Length
                        this.height = root.ch
                        if (this.height < box) { this.height = box }
                        base.Draw(root, ax, ay)

                        var theme = root.theme
                        var by = ay + (this.height - box) / 2
                        var back = theme.inputBack
                        if (!this.enabled) { back = theme.faceDisabled }
                        else if (this.hover) { back = theme.faceHover }
                        root.console.FillRect(ax, by, box, box, back)
                        var border = theme.border
                        if (this.focused) { border = theme.borderFocus }
                        root.console.DrawRect(ax, by, box, box, border)
                        if (this.isChecked) {
                            var mark = theme.accent
                            if (!this.enabled) { mark = theme.textDisabled }
                            root.console.FillRect(ax + 3, by + 3, box - 6, box - 6, mark)
                        }
                        var color = theme.text
                        if (!this.enabled) { color = theme.textDisabled }
                        root.console.DrawText(ax + box + 6, ay + (this.height - root.ch) / 2, this.text, color, 0)
                    }

                    MouseDown(root, int button, int px, int py) {
                        if (button == 1) { this.pressed = true }
                    }

                    MouseUp(root, int button, int px, int py) {
                        var was = this.pressed
                        this.pressed = false
                        if (was && button == 1 && this.Contains(px, py)) { this.Toggle() }
                    }

                    KeyDown(root, int key, int mod) {
                        if (key == UI.Keys.Space() || key == UI.Keys.Enter()) { this.Toggle() }
                    }
                }

                // Einzeiliges Textfeld mit Einfügemarke (Pfeiltasten, Pos1/Ende, Rücktaste/Entf, Mausklick). Enter löst `onEnter`
                // bzw. `TakeEntered()` aus, jede Änderung des Textes `onChange`/`TakeChanged()`.
                class TextBox : Element {
                    string text
                    int caret = 0
                    int scroll = 0
                    int maxLength = 0
                    lambda onChange
                    lambda onEnter
                    bool changed = false
                    bool entered = false

                    construct(string text, int x, int y, int width = 160, int height = 24) : base(x, y, width, height) {
                        this.text = text
                        this.caret = text.Length
                    }

                    bool Focusable() { return true }

                    bool TakeChanged() {
                        var was = this.changed
                        this.changed = false
                        return was
                    }

                    bool TakeEntered() {
                        var was = this.entered
                        this.entered = false
                        return was
                    }

                    SetText(string value) {
                        this.text = value
                        this.caret = value.Length
                        this.scroll = 0
                    }

                    Changed() {
                        this.changed = true
                        var callback = this.onChange
                        if (callback != undefined) { callback() }
                    }

                    // Wie viele Zeichen passen in das Feld?
                    int Fit(root) { return (this.width - 8) / root.cw }

                    Draw(root, int ax, int ay) {
                        base.Draw(root, ax, ay)
                        var theme = root.theme
                        var back = theme.inputBack
                        if (!this.enabled) { back = theme.faceDisabled }
                        root.console.FillRect(ax, ay, this.width, this.height, back)
                        var border = theme.border
                        if (this.focused) { border = theme.borderFocus }
                        root.console.DrawRect(ax, ay, this.width, this.height, border)

                        // die Einfügemarke bleibt sichtbar: der sichtbare Ausschnitt wandert mit
                        var fit = this.Fit(root)
                        if (this.caret < this.scroll) { this.scroll = this.caret }
                        if (this.caret > this.scroll + fit) { this.scroll = this.caret - fit }
                        if (this.scroll < 0) { this.scroll = 0 }

                        var count = this.text.Length - this.scroll
                        if (count > fit) { count = fit }
                        if (count < 0) { count = 0 }
                        var color = theme.text
                        if (!this.enabled) { color = theme.textDisabled }
                        var ty = ay + (this.height - root.ch) / 2
                        if (count > 0) {
                            root.console.DrawText(ax + 4, ty, this.text.Substring(this.scroll, count), color, 0)
                        }
                        if (this.focused) {
                            var cx = ax + 4 + (this.caret - this.scroll) * root.cw
                            root.console.DrawLine(cx, ay + 3, cx, ay + this.height - 4, color)
                        }
                    }

                    MouseDown(root, int button, int px, int py) {
                        if (button != 1) { return }
                        // auf die nächste Zeichengrenze runden (ganzzahlig: halbe Zellbreite dazu, dann teilen)
                        var column = (px - (this.ax + 4) + root.cw / 2) / root.cw
                        if (px < this.ax + 4) { column = 0 }
                        this.caret = this.scroll + column
                        if (this.caret < 0) { this.caret = 0 }
                        if (this.caret > this.text.Length) { this.caret = this.text.Length }
                    }

                    KeyDown(root, int key, int mod) {
                        var length = this.text.Length
                        if (key == UI.Keys.Left()) {
                            if (this.caret > 0) { this.caret = this.caret - 1 }
                        } else if (key == UI.Keys.Right()) {
                            if (this.caret < length) { this.caret = this.caret + 1 }
                        } else if (key == UI.Keys.Home()) {
                            this.caret = 0
                        } else if (key == UI.Keys.End()) {
                            this.caret = length
                        } else if (key == UI.Keys.Backspace()) {
                            if (this.caret > 0) {
                                this.text = this.text.Substring(0, this.caret - 1) + this.text.Substring(this.caret, length - this.caret)
                                this.caret = this.caret - 1
                                this.Changed()
                            }
                        } else if (key == UI.Keys.Delete()) {
                            if (this.caret < length) {
                                this.text = this.text.Substring(0, this.caret) + this.text.Substring(this.caret + 1, length - this.caret - 1)
                                this.Changed()
                            }
                        } else if (key == UI.Keys.Enter()) {
                            this.entered = true
                            var callback = this.onEnter
                            if (callback != undefined) { callback() }
                        }
                    }

                    TextInput(root, string value) {
                        var length = this.text.Length
                        if (this.maxLength > 0 && length + value.Length > this.maxLength) { return }
                        this.text = this.text.Substring(0, this.caret) + value + this.text.Substring(this.caret, length - this.caret)
                        this.caret = this.caret + value.Length
                        this.Changed()
                    }
                }

                // Das oberste Element: zeichnet in `framebuffer` und bekommt die Ereignisse von `window` (Warteschlange, siehe Tick).
                class Root {
                    Framebuffer framebuffer
                    Window window
                    Console console
                    Theme theme
                    Panel content
                    int width
                    int height
                    // Zellgröße der Schrift in Pixeln (Breite und Höhe eines Zeichens)
                    int cw
                    int ch
                    bool closed = false
                    int mouseX = 0
                    int mouseY = 0
                    Element hoverElement
                    Element pressedElement
                    Element focusElement

                    construct(Framebuffer framebuffer, Window window) {
                        this.framebuffer = framebuffer
                        this.window = window
                        this.console = new Console(framebuffer)
                        this.theme = new Theme()
                        this.cw = this.console.CellWidth()
                        this.ch = this.console.CellHeight()
                        this.width = framebuffer.Width()
                        this.height = framebuffer.Height()
                        this.content = new Panel(0, 0, this.width, this.height)
                        this.content.background = 1

                        // Ereignisse werden abgefragt, nicht per Callback geliefert: so laufen sie im Hauptprogramm, und ein
                        // onClick-Lambda sieht die echten globalen Variablen (ein Callback arbeitet dagegen auf einer Kopie, SPEC 8.1.4)
                        window.EnableEvents()
                    }

                    // Fügt ein Element (oder Panel) der obersten Ebene hinzu; es gehört ab jetzt diesem Root.
                    Add(element) { this.content.Add(element) }

                    // Zeichnet die gesamte Oberfläche neu.
                    Draw() {
                        this.console.FillRect(0, 0, this.width, this.height, this.theme.back)
                        this.content.Draw(this, 0, 0)
                    }

                    // Ein Zyklus: Oberfläche zeichnen, dann das Fenster einen Zyklus laufen lassen (Ereignisse abholen, anzeigen).
                    // Liefert false, sobald das Fenster geschlossen wurde.
                    bool Tick() {
                        this.Draw()
                        var open = this.window.Tick()
                        while (true) {
                            var e = this.window.NextEvent()   // (declared in the loop: the event array belongs to this iteration)
                            if (e == undefined) { break }
                            this.Dispatch(e)
                        }
                        if (!open) { this.closed = true }
                        return open
                    }

                    // Verteilt ein Ereignis des Fensters (Aufbau siehe Window.NextEvent) an das Element darunter bzw. das mit Fokus.
                    Dispatch(e) {
                        var type = e[0]
                        if (type == 8) { this.MouseDown(e[1], e[2], e[3]) }
                        else if (type == 11) { this.MouseUp(e[1], e[2], e[3]) }
                        else if (type == 9) { this.MouseMove(e[1], e[2]) }
                        else if (type == 24) { this.KeyDown(e[1], e[3]) }
                        else if (type == 3) { this.TextInput(e[1]) }
                        else if (type == 1) { this.closed = true }
                    }

                    // Läuft, bis das Fenster geschlossen wird.
                    Run() {
                        while (this.Tick()) { }
                    }

                    SetFocus(element) {
                        var old = this.focusElement
                        if (old != undefined) { old.focused = false }
                        this.focusElement = element
                        if (element != undefined) { element.focused = true }
                    }

                    // Tab: der Fokus wandert zum nächsten (mit Umschalt: zum vorigen) fokussierbaren Element.
                    FocusStep(bool backwards) {
                        var list = new List()
                        this.content.Collect(list)
                        if (list.count == 0) { return }
                        var index = -1
                        for (var i = 0; i < list.count; i = i + 1) {
                            if (list[i] == this.focusElement) { index = i }
                        }
                        if (backwards) {
                            index = index - 1
                            if (index < 0) { index = list.count - 1 }
                        } else {
                            index = index + 1
                            if (index >= list.count) { index = 0 }
                        }
                        this.SetFocus(list[index])
                    }

                    MouseDown(int button, int px, int py) {
                        this.mouseX = px
                        this.mouseY = py
                        var hit = this.content.HitTest(px, py)
                        if (hit != undefined && hit.enabled) {
                            if (hit.Focusable()) { this.SetFocus(hit) } else { this.SetFocus(undefined) }
                            this.pressedElement = hit
                            hit.MouseDown(this, button, px, py)
                        } else {
                            this.SetFocus(undefined)
                        }
                    }

                    MouseUp(int button, int px, int py) {
                        this.mouseX = px
                        this.mouseY = py
                        var target = this.pressedElement
                        this.pressedElement = undefined
                        if (target != undefined) { target.MouseUp(this, button, px, py) }
                    }

                    MouseMove(int px, int py) {
                        this.mouseX = px
                        this.mouseY = py
                        var hit = this.content.HitTest(px, py)
                        var old = this.hoverElement
                        if (hit != old) {
                            if (old != undefined) { old.hover = false }
                            this.hoverElement = hit
                            if (hit != undefined) { hit.hover = true }
                        }
                        var target = this.pressedElement
                        if (target != undefined) { target.MouseMove(this, px, py) }
                    }

                    KeyDown(int key, int mod) {
                        if (key == UI.Keys.Tab()) {
                            // die Umschalttasten sind die Bits 1 und 2 von mod
                            this.FocusStep(mod % 4 != 0)
                            return
                        }
                        var target = this.focusElement
                        if (target != undefined && target.enabled) { target.KeyDown(this, key, mod) }
                    }

                    TextInput(string text) {
                        var target = this.focusElement
                        if (target != undefined && target.enabled) { target.TextInput(this, text) }
                    }
                }
            }
            """;
    }
}
