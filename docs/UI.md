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
  (`UI.Theme`: alle Farben als `Brush`-Felder - `back`, `panel`, `face`, `faceHover`, `facePressed`, `faceDisabled`, `inputBack`, `accent`, `text`, `textDisabled` - und `Pen`-Felder `border`, `borderFocus`, `caret`) und Fokus/Hover/gedrückt-Zustand. `Add(element)` hängt ein Element an, `Update()` bringt die Oberfläche auf den Stand und malt, was sich geändert hat (siehe *Zeichnen nur bei Änderungen*), `Draw()` zeichnet alles neu, `Tick()` ist ein Zyklus
  (`Update()`, `window.Tick()`, alle angekommenen Ereignisse verteilen; false, sobald das Fenster geschlossen ist), `Run()` ruft `Tick` bis zum Schließen - `Run(anderes)` hängt vorher ein weiteres Fenster an
  (siehe *Mehrere Fenster*). Eine Änderung an einem Feld (`label.text = ...`) ist beim nächsten Tick sichtbar.
- **Elemente** (`UI.Element` als Basis: `x`, `y`, `width`, `height` relativ zum Container, `visible`, `enabled`, dazu `hover`/`pressed`/`focused`):
  `Panel` (Container, `showBorder`, `background` = Brush, `filled`, `pen` = Stift des Rahmens), `Stack` (ordnet Kinder unter- oder nebeneinander an: `horizontal`, `spacing`, `padding`), `Label` (`brush`), `Button`, `CheckBox`, `TextBox`.
- **Reagieren:** jedes bedienbare Element kennt beides - ein Lambda-Feld (`onClick`, `onChange`, `onEnter`) und einen abfragbaren Merker (`TakeClicked()`, `TakeChanged()`, `TakeEntered()`; liefert
  true einmal und setzt zurück). Lambdas laufen im Hauptprogramm, weil `Root.Tick` die Ereignisse per `Window.NextEvent` abholt statt über einen Callback (dessen isolierte Kopie der Globals
  würde ein Lambda in einem Objekt gar nicht erst zulassen, siehe `docs/CONSOLE.md`).
- **Besitz:** `Add` ruft `child.TakeTo(this)` - ein Element gehört seinem Container und lebt, solange der Root lebt, auch wenn es in einer Hilfsfunktion angelegt wurde.
- **Bedienung:** Mausklick fokussiert, `Tab`/`Umschalt+Tab` wechselt den Fokus (nur sichtbare, aktive, fokussierbare Elemente), Leertaste/Enter löst Schaltfläche/Kontrollkästchen aus. `TextBox`:
  Zeichen (Texteingabe-Ereignisse), Pfeile, Pos1/Ende, Rücktaste/Entf, Klick setzt die Einfügemarke, der sichtbare Ausschnitt wandert mit, `maxLength`, Enter meldet `entered`.
- **Zeichnen:** flach, mit der eingebauten 8x14-Schrift (`root.cw`/`root.ch` sind Breite und Höhe eines Zeichens) oder - mit `font`/`fontSize` an einem Element, siehe *Schriften* unten - mit einer TrueType-Schrift; Gezeichnet wird mit Pinseln und Stiften (`new SolidBrush(UI.Color.Rgb(r, g, b))`, `new Pen(farbe, breite)`; `undefined` = Vorgabe des Themes), also auch mit halbdurchsichtigen Farben, solange `root.renderer.AlphaBlending` an ist. `UI.Color.Rgb(r, g, b)` baut die rohen Farbwerte, `UI.Keys` nennt die Tastencodes.

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
- **Bindung zwischen Elementen:** `element.Bind("text", quelle, "text")` (optional `twoWay`, `converter` = `UI.Converter`, `once` = nur einmal übertragen) gleicht bei jedem Durchlauf von `Update` ab; die Quelle ist
  ein beliebiges Objekt, der Pfad darf durch Objekte gehen (`"adresse.ort"`); ein leerer Pfad ist die Quelle selbst (bei einer Zeile mit `DataTemplate` das Datenelement, nur lesend). Eine Text-Eigenschaft zeigt jeden Wert als Text.
- **Listen:** `ListBox`/`ListView` zeigen `items`; `list.itemsSource = liste` zeigt eine fremde Liste, ohne sie zu besitzen (Änderungen darin erscheinen von selbst, die Liste austauschen geht auch),
  `list.SetView(view, false)` eine `CollectionView` (`view.source` ist austauschbar; mit `own = false` gehört sie nicht der Liste), `list.itemTemplate = DataTemplate` baut die Zeilen. Wird `selectedIndex` von außen gesetzt
  (zum Beispiel durch eine Bindung), verhält sich das wie eine Auswahl (`onSelect`).
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

## Zeichnen nur bei Änderungen

`Root.Update()` (und damit `Tick()`) malt nur, wenn sich etwas geändert hat, und dann nur den Bereich, der sich geändert hat; ein ruhiges Fenster kostet fast nichts, `Update()` liefert dann `false`.
Dahinter steckt keine Buchführung des Programms: der Root beobachtet die Felder der Elemente mit `probe` (SPEC 8.14 - nur beobachtete Objekte nehmen den langsamen Schreibpfad, und nur ein Schreibzugriff, der den Wert
wirklich ändert, meldet sich), dazu fragt `Update()` bei jedem Durchlauf ab, was nicht über Felder läuft: Bindungen, Trigger, Styles, Vorlagen, Listen (Zeilenzahl, `CollectionView`) und die Zeiger-Zustände der Elemente.

- Ändert sich nur der Inhalt eines Elements (Text, Farbe, Hover), wird der Bereich dieses Elements neu gemalt (die Vereinigung der Bereiche aller geänderten Elemente, ein Pixel größer).
  Rutscht dabei ein Element woanders hin oder ändert sich seine Größe (zum Beispiel ein längerer Text in einem `StackPanel`), wird alles neu angeordnet und ganz neu gemalt.
- Änderungen am Theme (`root.theme.back = ...`) und an Fenstergröße/Schrift malen alles neu.
- **Nicht** beobachtet werden Änderungen *im Inneren* eines Brushes, Stifts, einer `Geometry` oder eines Bildes, das ein Element nutzt (ein `SolidBrush`, dessen Farbe man verändert): dann `element.Invalidate()`
  (oder `root.Invalidate()` für alles) aufrufen, oder den Brush austauschen. `Draw()` zeichnet immer alles. Ein `DrawingCanvas` mit `continuous = true` malt in jedem Zyklus neu.
- Eigene Elemente: `Draw` beginnt mit `if (!this.NeedsDraw(root, ax, ay)) { return }` und malt nur, was in den Bereich fällt; Änderungen, die keine Felder sind, meldet das Element mit `Invalidate()`
  oder in `Poll(root)`.

## Bedienung: Maus, Tastatur, Touchscreen, Joystick

Die Oberfläche lässt sich mit allen vier Eingaben bedienen; sie laufen in dieselben Elemente (`MouseDown`, `KeyDown`, ...), ein Element muss nichts davon wissen.

- **Maus**: Klicken, Ziehen, Mausrad, rechte Taste für das Kontextmenü.
- **Tastatur**: Tab / Umschalt-Tab wandert durch die fokussierbaren Elemente, die **Pfeiltasten** bewegen den Fokus zum nächsten Element in dieser Richtung (nach Lage auf dem Bildschirm; in einem `ScrollViewer` wird das Element
  sichtbar gerollt, und gibt es in der Richtung keins mehr, rollt der Pfeil den ScrollViewer wie das Mausrad). Enter/Leertaste löst aus. Ein Element behält die Pfeile, die es selbst braucht: das Textfeld Links/Rechts/Pos1/Ende,
  die Liste und der Baum Auf/Ab (am ersten und letzten Eintrag gibt sie die Taste frei, damit man aus ihr herauskommt), `RadioButtons` die Pfeile zwischen den Optionen. Eigene Elemente überschreiben `bool WantsKey(int key)`.
- **Touchscreen**: Ein Finger setzt auf wie eine gedrückte linke Maustaste und löst beim Abheben auf dem Element einen Klick aus; hebt er daneben ab, passiert nichts. Zieht er über 10 Pixel (`ui.touchSlop`) über einen
  `ScrollViewer`, eine Liste oder einen Baum, wird daraus **Wischen**: der Inhalt folgt dem Finger, ohne dass das Element unter dem Finger auslöst; ein Griff einer Bildlaufleiste wird dagegen gezogen. Nur ein Finger zählt,
  weitere werden ignoriert. Nach dem Abheben bleibt keine Hover-Hervorhebung. (Eigene Elemente, die sich wischen lassen, überschreiben `CanPan()`, `Dragging()` und `PanBy(root, dx, dy)`.)
- **Joystick**: das Kreuz (Hat) und der linke Stick (Achsen 0 und 1, mit Totzone: ab 0,6 ausgeschlagen, zurück unter 0,3) werden zu den Pfeiltasten; Halten wiederholt sie (`ui.joyRepeatDelay` = 24 Zyklen, dann alle
  `ui.joyRepeatInterval` = 6 Zyklen - ein Zyklus ist ein `Tick`, mit VSync etwa 1/60 s). Knopf 0 = Enter (auslösen), Knopf 1 = Escape (Menü schließen), Knopf 4 / 5 = Umschalt-Tab / Tab. Die Knopfnummern sind die des
  Geräts (Xbox-Pad: 0 = A, 1 = B, 4 = LB, 5 = RB); mit `ui.joyButtonActivate`, `joyButtonCancel`, `joyButtonPrevious`, `joyButtonNext` und `joyAxisX`/`joyAxisY` lassen sie sich anpassen (-1 schaltet einen Knopf aus).

Das `UI.Root` schaltet dafür `window.TouchMouse` aus (siehe docs/CONSOLE.md "Touchscreen und Joystick"), damit ein Finger nicht zweimal ankommt.

## Fenstergröße

Ein `UI.Root` schaltet `window.AutoResize` ein: zieht der Nutzer das Fenster auf eine andere Größe, bekommt der Framebuffer sie (sofern sie gültig ist, siehe docs/CONSOLE.md "Fenstergröße ändern") und die Oberfläche
ordnet sich in der neuen Größe neu an und malt neu - Elemente mit `halign`/`valign` = `Stretch` (und Panels, die ihre Kinder dehnen) füllen den neuen Platz, `Dock`-, `Grid`- und `Stack`-Layouts rechnen neu, schwebende
Elemente (Menüs) bleiben im Fenster. `ui.width` und `ui.height` sind immer die aktuelle Größe; `ui.onResize = func (int w, int h) => { ... }` wird danach gerufen (zum Beispiel für ein eigenes Bild in einem `Canvas`).
Wer das Strecken des festen Bildes vorzieht, setzt `window.AutoResize = false`.

## Mehrere Fenster

Ein Programm kann mehrere Fenster haben - jedes ist ein `UI.Root` mit eigenem Framebuffer und `Window`. `ui.Attach(anderes)` hängt den Root eines weiteren Fensters an: `ui.Tick()` (und `ui.Run()`) macht dann
in jedem Zyklus auch dessen Zyklus, bis sein Fenster geschlossen wird (das schließt es nur aus der Liste; das Ende von `Run` bestimmt das Fenster, an das angehängt wurde). `ui.Run(anderes)` hängt an und läuft.
`ui.Detach(anderes)` nimmt es wieder heraus; `ui.attached` ist die Liste. `ui.onTick` ist ein Lambda ohne Parameter, das der Zyklus dieses Roots am Ende ruft. Nur das Hauptfenster wartet auf den Bildaufbau (VSync),
die angehängten nicht, damit n Fenster nicht n-mal warten. Der angehängte Root (und was ihn besitzt) muss so lange leben wie sein Fenster - zum Beispiel als Feld des öffnenden Fensters.

```
var main = new UI.Root(fb1, win1)
var tools = new UI.Root(fb2, win2)
main.Run(tools)           // beide Fenster laufen in einer Schleife; main schließen beendet das Programm
```

## Oberfläche im Markup

Die Oberfläche lässt sich auch in einer Markup-Datei (`.fxml`, XML wie XAML) entwerfen; daraus entsteht eine Basisklasse, von der der eigene Code erbt - mit Handlern, benannten Elementen und Datenbindung
(mit `probe`) samt Convertern, auch `DataTemplate`, `CollectionView` und Listen, die ihre Daten aus dem Datenkontext holen. Siehe `docs/UI_MARKUP.md`; der Editor zeigt dazu eine Entwurfsansicht.

## Grenzen

- Keine Mehrzeilen-Textfelder, kein Drag & Drop, keine Animationen; Listenzeilen mit `itemTemplate` sind nur zum Anzeigen (sie nehmen keine Eingaben an).
- Text ist dicktengleich (eine Schrift, eine Größe).
- Touch kennt nur einen Finger (kein Zoomen, kein Zwei-Finger-Rollen, kein langes Drücken für das Kontextmenü, kein Schwung nach dem Wischen); die Menüleiste ist nur mit Maus und Finger zu öffnen. Der Joystick-Wiederholtakt zählt Zyklen, nicht Zeit.
- Die Elemente zeichnen sich in den Framebuffer des Roots; mehrere Roots auf demselben Framebuffer übermalen einander.
- Das Verhalten der Fenster-Ereignisse mit echtem SDL (Mausposition bei skaliertem Fenster, Texteingabe, echte Finger und Joysticks) ist nur aus dem Code begründet, nicht unter SDL getestet; die Bibliothek selbst ist headless
  getestet (Suite-Block "UI-Bibliothek": echter `WindowManager` mit Renderer-Attrappe).

## Schriften

Jedes `UI.Element` hat `font` (ein Name für `Fonts.Get`, `""` = vom umgebenden Element erben) und `fontSize` (Pixel, `0` = erben). Eine Schrift an einem `Panel` gilt für alles darin, ein Menü nimmt die Schrift des Elements, von dem es geöffnet wurde,
und `root.theme.font` / `root.theme.fontSize` (Vorgabe `""` und `14`) sind der letzte Rückgriff. Ohne alles bleibt es bei der Konsolenschrift des Renderers - Größen und Bild sind dann wie vorher. Namen: `"8x14"`, `"8x8"`, eine mit
`Fonts.Add(new Resource("fonts/X.ttf"))` eingebettete Schrift (nach Dateiname, Familie, vollem Namen oder Alias) oder eine auf dem System installierte; unbekannte Namen ergeben die Konsolenschrift (docs/FONTS.md).

```
Fonts.Add(new Resource("fonts/Roboto-Regular.ttf"))
ui.theme.font = "Roboto-Regular"
ui.theme.fontSize = 16
var title = new UI.Label("Settings", 8, 8)
title.fontSize = 24                       // dieselbe Schrift, größer
var hint = new UI.Label("Esc closes", 8, 40)
hint.font = "8x8"                         // eine Bitmap-Schrift
```

`Label`, `Button`, `CheckBox`, `TextBox`, `ListBox`/`ListView`, `TreeView`, `RadioButtons` und Menüs messen und zeichnen ihren Text mit der Schrift des Elements (`element.TextWidth(root, text)`, `element.TextHeight(root)` und
`element.DrawString(root, x, y, text, brush)` sind die Hilfen dafür, auch für eigene Elemente; `root.cw`/`root.ch` bleiben die Zellgröße der Konsolenschrift). Ein `TextBox` mit Proportionalschrift misst den Text bis zur Einfügemarke
und unter der Maus.
