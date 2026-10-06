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

## Steuerelemente

Neben `Label`, `Button`, `CheckBox` und `TextBox` (siehe Aufbau) bringt die Bibliothek diese Elemente mit; alle erben von `UI.Element`, haben also Layout-Felder, `style`, `template` (wo es Sinn hat) und lassen sich stylen:

- **`ScrollViewer(x, y, w, h)`**: zeigt EIN Kind (`SetContent(element)`) in einem Ausschnitt; Bildlaufleisten erscheinen, wenn es nicht hineinpasst (`vmode`/`hmode` = `UI.ScrollMode.Disabled/Auto/Visible`, senkrecht Auto, waagerecht Disabled: dann wird das Kind auf die Breite des Ausschnitts begrenzt, ein WrapPanel bricht um).
  Mausrad (mit waagerechtem Rad seitlich), Ziehen am Griff, Klick neben den Griff blättert eine Seite; `ScrollIntoView(x, y, w, h)`. Dahinter steckt `UI.Scroller` (Versatz, Inhalts- und Ausschnittsgröße, Griff), den die Listen mitbenutzen.
- **`ListBox(x, y, w, h)`**: `Add(item)` (Text, Zahl oder Objekt; ein Objekt gehört danach der Liste), `SetItems(liste)`, `Remove`, `Clear`; `displayMember = "name"` zeigt diese Eigenschaft der Objekte; `selectedIndex`/`selectedItem`, `Select(i)`, `onSelect`/`TakeChanged()`,
  Enter meldet `onActivate`/`TakeActivated()`; Pfeile, Pos1/Ende, Bild auf/ab, Mausrad, Klick. `itemTemplate = new UI.DataTemplate(func (item) => element)` baut jede Zeile als eigenes Element (nur zum Anzeigen). Nach einer Änderung in den Objekten selbst: `Refresh()`.
- **`ListView`**: wie die ListBox, mit Spalten und Kopfzeile: `AddColumn("Name", "name", 100)` (Breite -1: der Rest); ein Klick auf den Titel sortiert (noch einmal: umgekehrt).
- **`CollectionView(liste)`**: eine Sicht auf eine Liste - `filter` (Lambda mit einem Parameter), `SortBy("eigenschaft", absteigend)` oder `comparer` (Lambda mit zwei Parametern), `current` mit `MoveCurrentTo.../CurrentItem()`;
  `listBox.SetView(sicht)` zeigt sie (die Auswahl folgt `current`). Die Sicht aktualisiert sich beim Zeichnen, wenn sich die Anzahl der Quelle ändert, sonst mit `Refresh()`/`Invalidate()`.
- **`TreeView`**: `AddNode(new UI.TreeNode("Text", wert))`, Kinder mit `node.Add(...)`, `expanded`; Klick aufs Kästchen klappt auf/zu, Klick wählt (`selected`, `onSelect`/`TakeChanged()`); Auf/Ab wandern, Rechts klappt auf bzw. geht zum ersten Kind, Links klappt zu bzw. geht zum Elternknoten.
- **`RadioButtons`**: `Add("Text")`, `header`, `horizontal`, `selectedIndex`/`selectedItem`, `onChange`/`TakeChanged()`; Pfeiltasten wechseln die Wahl.
- **`AutoSuggestBox(text, x, y, w, h)`**: ein TextBox, der beim Tippen eine Liste von Vorschlägen zeigt: `AddSuggestion(item)` (vorgeschlagen wird, was den Text enthält, ohne Groß-/Kleinschreibung) oder `provider = func (text) => liste`;
  Auf/Ab wählt, Enter oder Klick übernimmt (`onChosen`/`TakeChosen()`, `chosenItem`), Escape schließt; `maxSuggestions`.
- **Menüs:** `UI.MenuBar` mit Menüs der obersten Ebene (`bar.Add(new UI.MenuItem("File"))`), `item.Add(new UI.MenuItem("Open", func () => { ... }))`, Trenner `UI.MenuItem.Separator()`, Untermenüs (ein `MenuItem` mit eigenen Zeilen), `checkable`/`isChecked`, `enabled`, `shortcut` (nur ein Text).
  Ein Klick klappt auf, bei offenem Menü wechselt der Zeiger das Menü; Pfeile, Enter, Escape. **Kontextmenü:** `element.contextMenu = liste` (eine Liste von `MenuItem`) - ein Rechtsklick auf das Element oder einen Nachfahren zeigt es; allgemein `root.ShowMenu(liste, x, y)`.
- **Schwebende Elemente:** `root.ShowPopup(element, x, y, transient = true, owner = undefined)` legt ein Element über die Oberfläche (zuletzt gezeichnet, zuerst getroffen), `root.ClosePopup(element)`, `root.CloseAllPopups()`; ein Klick daneben ruft `element.PopupOutside(root, x, y)` (schließt und
  verbraucht den Klick, wenn er den `owner` traf); `grabsKeys` heißt: es bekommt zuerst die Tasten. Menüs und die Vorschlagsliste sind solche Elemente.
- **`ToolBar`**: ein waagerechtes StackPanel mit `AddButton("Text", func () => { ... })` und `AddSeparator()`.
- **`Image(framebuffer)`**: zeigt einen Framebuffer (`Framebuffer.FromResource(new Resource("logo.png"))`, ein geladenes oder selbst gemaltes Bild) mit Alpha; `stretch = UI.Stretch.None/Fill/Uniform/UniformToFill`.
- **Formen:** `Rectangle`, `Ellipse` (Größe des Elements), `Line(x1, y1, x2, y2)`, `Path(geometry)` - alle mit `fill` (Brush) und `stroke` (Pen). Eine **`Geometry`** besteht aus Linienzügen: `MoveTo/LineTo/QuadTo/CubicTo/Close`, `AddRect/AddEllipse/AddArc` oder
  `UI.Geometry.Parse("M 10 10 L 50 10 C 60 20 60 40 25 50 Z")` (Befehle M L H V C Q Z, klein = relativ; Zahlen ganzzahlig); `path.SetData(text)`. Geschlossene Figuren werden gefüllt (Even-Odd).
- **`DrawingCanvas(x, y, w, h)`**: eine Zeichenfläche mit eigenem Framebuffer (`canvas.framebuffer`, gezeichnet wird mit `canvas.renderer`): `onPaint = func (c) => { ... }` läuft beim Anlegen und bei Größenänderung, nach `Invalidate()` und - mit `continuous` - in jedem Bild;
  `SetSource(framebuffer)` zeigt stattdessen einen fremden Framebuffer; `onMouseDown/onMouseMove/onMouseUp` melden Mausereignisse (x, y relativ zur Fläche).
- **Mausrad und Hover:** `Element.MouseWheel(root, dx, dy)` (true = verbraucht, sonst fragt der Root den Container) und `Element.Hover(root, x, y)`; `Root.PushClip/PopClip` beschneiden das Zeichnen (siehe `docs/CONSOLE.md`, `SetClip`).

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

## Layout

Wie in WPF läuft das Layout in zwei Durchgängen je Zeichnen: `Measure` (was möchte das Element, bei begrenztem oder unbegrenztem Platz) und `Arrange` (Position und Größe im
Container). Ganze Pixel, `-1` heißt "automatisch/unbegrenzt".

- **Gemeinsame Felder** jedes `UI.Element`: `width`/`height` (`-1` = automatisch), `minWidth`/`minHeight`, `maxWidth`/`maxHeight`, `margin` (`new UI.Thickness(a)` / `(waagerecht, senkrecht)` /
  `(links, oben, rechts, unten)`), `halign` (`UI.HAlign.Stretch/Left/Center/Right`), `valign` (`UI.VAlign....`). Nach dem Layout liefern `actualWidth`/`actualHeight` die Größe.
  Abweichung von WPF: ein `Stretch`-Element mit ausdrücklicher Breite/Höhe wird links bzw. oben ausgerichtet.
- **`Panel`** / **`Canvas`**: absolute Positionen (`x`, `y`) wie bisher.
- **`StackPanel`** (Alias `Stack`): `horizontal`, `spacing`, `padding`; Kinder quer gestreckt, unsichtbare zählen nicht.
- **`DockPanel`**: Kind-Feld `dock` (`UI.Dock.Left/Top/Right/Bottom`) in Reihenfolge; das letzte Kind füllt den Rest.
- **`WrapPanel`**: bricht in die nächste Zeile bzw. Spalte um.
- **`Grid`**: `SetRows("24, *, auto")`, `SetColumns("60, 2*, auto")` (Pixel, Stern mit Gewicht, auto); Kinder mit `AddAt(kind, zeile, spalte, zeilen, spalten)` oder den Feldern `gridRow`, `gridColumn`, `gridRowSpan`, `gridColumnSpan`.
- **`Border`**: ein Kind mit Rand, Innenabstand und Hintergrund.

## Aussehen: Eigenschaften, Styles, Trigger, Vorlagen, Ressourcen

Die **Eigenschaften** der Elemente sind ihre Felder; Styles und Trigger setzen sie über den Namen (`Reflect`, deshalb bringt `#import "ui"` die Bibliothek `reflection` mit).
Allen Elementen gemeinsam: `background` und `foreground` (Brush), `pen` (Stift des Rahmens), `name`; `undefined` heißt: das Steuerelement nimmt die Farben des Themes (und
färbt Hover/Druck selbst; ein gesetztes `background` gilt in jedem Zustand, bis ein Trigger es ändert). Bei `Label` gilt `brush`, dann `foreground`.

- **`UI.Style(target)`** sammelt `Set("eigenschaft", wert)` und Trigger. `target` ist der Stil-Schlüssel der Art Element (`element.StyleKey()`: `"Button"`, `"Label"`, `"CheckBox"`, `"TextBox"`,
  `"Panel"`, `"StackPanel"`, `"DockPanel"`, `"WrapPanel"`, `"Grid"`, `"Border"`, `"Canvas"`, `"Element"`; eine abgeleitete Klasse erbt den Schlüssel oder überschreibt `StyleKey`).
  `style.basedOn = anderer` baut auf einem Style auf.
- **Impliziter Style:** `root.resources.AddStyle(style)` (oder `panel.Resources().AddStyle(style)`) - gilt für alle Elemente dieser Art darunter; der nächste Style nach oben gewinnt, zuletzt der des Roots.
  **Expliziter Style:** `element.style = style` (auch später; er ersetzt den impliziten). Ein Style gilt beim **ersten Layout** des Elements und überschreibt dort, was der Konstruktor oder der Code vorher
  gesetzt haben; wer einen eigenen Wert behalten will, setzt ihn nach dem ersten Zeichnen oder weist `element.style = new UI.Style()` zu. Eigenschaften, die es beim Element nicht gibt, werden übergangen.
- **`UI.Trigger(eigenschaft, wert)`**: solange die Eigenschaft des Elements den Wert hat (`new UI.Trigger("hover", true)`), setzen die `Set(...)`-Aufrufe ihre Werte; danach kommen die alten wieder.
  Weitere Bedingungen: `trigger.And("isChecked", true)`. `style.AddTrigger(trigger)`. Die Trigger werden bei jedem Layout geprüft (ohne Ereignisse).
- **Vorlagen:** `UI.ControlTemplate(func (owner) => { ...; return wurzel })` ersetzt das Aussehen von `Label`, `Button` und `CheckBox` (`element.template = vorlage`, oder per Style:
  `style.Set("template", vorlage)`): das Lambda baut aus Elementen (`Border`, `StackPanel`, `Label`, ...) einen Teilbaum und bekommt das Steuerelement übergeben. Teile mit `name` lassen sich
  ansprechen: `vorlage.Bind("teil", "text", "text")` hält `teil.text` gleich `owner.text`; `vorlage.AddTrigger(trigger)` mit Settern, deren drittes Argument der Name des Teils ist
  (`trigger.Set("background", brush, "teil")`), und Bedingungen auf das Steuerelement (`hover`, `pressed`, `focused`, `isChecked`, `enabled`). Größe, Klicks und Fokus bleiben beim Steuerelement.
  `element.FindPart("name")` liefert einen Teil. `TextBox` zeichnet sich immer selbst (Einfügemarke und Auswahl).
- **`UI.DataTemplate(func (item) => element)`**: baut aus einem Datenelement das Element, das es zeigt (`template.Build(item)`; die Listen-Steuerelemente nutzen es).
- **Bindung zwischen Elementen:** `element.Bind("text", quelle, "text")` (optional `twoWay`, `converter` = `UI.Converter`) gleicht bei jedem Layout ab; die Quelle ist ein beliebiges Objekt.
- **Ressourcen:** `element.Resources().Set("schluessel", wert)`, `root.resources.Set(...)`; `element.FindResource("schluessel")` sucht vom Element nach oben, zuletzt im Root (vor dem ersten Layout nur
  bei den Vorfahren); `undefined`, wenn es sie nicht gibt. Ein `ResourceDictionary` besitzt seine Werte (und Styles).

```
var rot = new SolidBrush(UI.Color.Rgb(200, 60, 60))
var stil = new UI.Style("Button")
stil.Set("margin", new UI.Thickness(4))
stil.Set("background", new SolidBrush(UI.Color.Rgb(60, 120, 200)))
var hover = new UI.Trigger("hover", true)
hover.Set("background", rot)
stil.AddTrigger(hover)
ui.resources.AddStyle(stil)               // alle Button
```

## Oberfläche im Markup

Die Oberfläche lässt sich auch in einer Markup-Datei (`.fxml`, XML wie XAML) entwerfen; daraus entsteht eine Basisklasse, von der der eigene Code erbt - mit Handlern, benannten Elementen und Datenbindung
(mit `probe`) samt Convertern. Siehe `docs/UI_MARKUP.md`; der Editor zeigt dazu eine Entwurfsansicht.

## Grenzen

- Keine Mehrzeilen-Textfelder, kein Drag & Drop, keine Animationen; Listenzeilen mit `itemTemplate` sind nur zum Anzeigen (sie nehmen keine Eingaben an).
- Text ist dicktengleich (eine Schrift, eine Größe).
- Die Elemente zeichnen sich in den Framebuffer des Roots; mehrere Roots auf demselben Framebuffer übermalen einander.
- Das Verhalten der Fenster-Ereignisse mit echtem SDL (Mausposition bei skaliertem Fenster, Texteingabe) ist nur aus dem Code begründet, nicht unter SDL getestet; die Bibliothek selbst ist headless
  getestet (Suite-Block "UI-Bibliothek": echter `WindowManager` mit Renderer-Attrappe).
