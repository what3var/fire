# Oberflächen-Bibliothek (`#import "ui"`)

Eine minimale, komplett in fire geschriebene Oberfläche, die in einem Framebuffer läuft: Schaltflächen, Beschriftungen, Kontrollkästchen, Textfelder, Panels und ein Stapel-Layout. Sie liegt als
eigene Brücke in `src/fire.UI.Bridge` (nur fire-Quelltext, `UiBridge.PreludeSource`, `namespace UI`; keine native Funktion, keine DLL) und baut auf den Klassen der Grafik-Brücke auf
(`Framebuffer`, `Renderer`, `Window`, siehe `docs/CONSOLE.md`). `#import "ui"` schaltet `graphics` und `windows` (das Fenster, siehe `docs/CONSOLE.md`) automatisch mit zu (`ImportedPreludes.WithDependencies`).

```
#import "ui"

var fb = new Framebuffer(640, 480)
var win = new Window(fb, "Demo")
var ui = new UI.Root(fb, win)          // oberstes Element: Framebuffer zum Zeichnen, Fenster für die Ereignisse

var name = new UI.TextBox("", 20, 40, 200)
var ok = new UI.Button("OK", 20, 80)
var check = new UI.CheckBox("Gross schreiben", 20, 120)
var out = new UI.Label("", 20, 160)
ui.Add(new UI.Label("Name:", 20, 20))
ui.Add(name)
ui.Add(ok)
ui.Add(check)
ui.Add(out)

ok.onClick = func () => { out.text = "Hallo " + name.text }     // läuft im Hauptprogramm, sieht die echten Globals

while (ui.Tick()) {                      // zeichnen, Fenster-Zyklus, Ereignisse verarbeiten
    if (check.TakeChanged()) { print("Kontrollkaestchen: " + check.isChecked) }
}
```

## Aufbau

- **`UI.Root(Framebuffer, Window)`**: legt einen `Renderer` zum Zeichnen an (`root.renderer`), schaltet die Ereignis-Warteschlange des Fensters ein und hält die oberste Ebene (`content`, ein `Panel`), das `theme`
  (`UI.Theme`: alle Farben als `Brush`-Felder - `back`, `panel`, `face`, `faceHover`, `facePressed`, `faceDisabled`, `inputBack`, `accent`, `text`, `textDisabled` - und `Pen`-Felder `border`, `borderFocus`, `caret`) und Fokus/Hover/gedrückt-Zustand. `Add(element)` hängt ein Element an, `Draw()` zeichnet alles neu, `Tick()` ist ein Zyklus (zeichnen, `window.Tick()`, alle
  angekommenen Ereignisse verteilen; false, sobald das Fenster geschlossen ist), `Run()` ruft `Tick` bis zum Schließen. `Tick` zeichnet jedes Mal die ganze Oberfläche - es gibt kein
  "schmutzig"-Bookkeeping, eine Änderung an einem Feld (`label.text = ...`) ist deshalb beim nächsten Tick sichtbar.
- **Elemente** (`UI.Element` als Basis: `x`, `y`, `width`, `height` relativ zum Container, `visible`, `enabled`, dazu `hover`/`pressed`/`focused`):
  `Panel` (Container, `showBorder`, `background` = Brush, `filled`, `pen` = Stift des Rahmens), `Stack` (ordnet Kinder unter- oder nebeneinander an: `horizontal`, `spacing`, `padding`), `Label` (`brush`), `Button`, `CheckBox`, `TextBox`.
- **Reagieren:** jedes bedienbare Element kennt beides - ein Lambda-Feld (`onClick`, `onChange`, `onEnter`) und einen abfragbaren Merker (`TakeClicked()`, `TakeChanged()`, `TakeEntered()`; liefert
  true einmal und setzt zurück). Lambdas laufen im Hauptprogramm, weil `Root.Tick` die Ereignisse per `Window.NextEvent` abholt statt über einen Callback (dessen isolierte Kopie der Globals
  würde ein Lambda in einem Objekt gar nicht erst zulassen, siehe `docs/CONSOLE.md`).
- **Besitz:** `Add` ruft `child.TakeTo(this)` - ein Element gehört seinem Container und lebt, solange der Root lebt, auch wenn es in einer Hilfsfunktion angelegt wurde.
- **Bedienung:** Mausklick fokussiert, `Tab`/`Umschalt+Tab` wechselt den Fokus (nur sichtbare, aktive, fokussierbare Elemente), Leertaste/Enter löst Schaltfläche/Kontrollkästchen aus. `TextBox`:
  Zeichen (Texteingabe-Ereignisse), Pfeile, Pos1/Ende, Rücktaste/Entf, Klick setzt die Einfügemarke, der sichtbare Ausschnitt wandert mit, `maxLength`, Enter meldet `entered`.
- **Zeichnen:** flach, mit der eingebauten 8x14-Schrift (`root.cw`/`root.ch` sind Breite und Höhe eines Zeichens); Gezeichnet wird mit Pinseln und Stiften (`new SolidBrush(UI.Color.Rgb(r, g, b))`, `new Pen(farbe, breite)`; `undefined` = Vorgabe des Themes), also auch mit halbdurchsichtigen Farben, solange `root.renderer.AlphaBlending` an ist. `UI.Color.Rgb(r, g, b)` baut die rohen Farbwerte, `UI.Keys` nennt die Tastencodes.

## Oberfläche im Markup

Die Oberfläche lässt sich auch in einer Markup-Datei (`.fxml`, XML wie XAML) entwerfen; daraus entsteht eine Basisklasse, von der der eigene Code erbt - mit Handlern, benannten Elementen und Datenbindung
(mit `probe`) samt Convertern. Siehe `docs/UI_MARKUP.md`; der Editor zeigt dazu eine Entwurfsansicht.

## Grenzen

- Nur absolute Positionierung und das einfache `Stack`-Layout; keine Scroll-Container, kein Clipping der Kinder am Container, keine Mehrzeilen-Textfelder, keine Auswahllisten.
- Text ist dicktengleich (eine Schrift, eine Größe).
- Die Elemente zeichnen sich in den Framebuffer des Roots; mehrere Roots auf demselben Framebuffer übermalen einander.
- Das Verhalten der Fenster-Ereignisse mit echtem SDL (Mausposition bei skaliertem Fenster, Texteingabe) ist nur aus dem Code begründet, nicht unter SDL getestet; die Bibliothek selbst ist headless
  getestet (Suite-Block "UI-Bibliothek": echter `WindowManager` mit Renderer-Attrappe).
