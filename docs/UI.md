# Oberflächen-Bibliothek (`#import "ui"`)

Eine minimale, komplett in fire geschriebene Oberfläche, die in einem Framebuffer läuft: Schaltflächen, Beschriftungen, Kontrollkästchen, Textfelder, Panels und ein Stapel-Layout. Sie ist
die Standardbibliothek `fire-ui` (Projekt `packages/ui`, nur fire-Quelltext, `namespace UI`; keine native Funktion) und baut auf den Klassen der Grafik-Brücke auf
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

ok.OnClick = func () => { out.Text = "Hallo " + name.Text }     // läuft im Hauptprogramm, sieht die echten Globals

while (ui.Tick()) {                      // zeichnen, Fenster-Zyklus, Ereignisse verarbeiten
    if (check.TakeChanged()) { print("Kontrollkaestchen: " + check.IsChecked) }
}
```

## Steuerelemente

**Symbole auf Knöpfen.** `button.Icon` ist eine Vektorform (`UI.Geometry`), die in der Textfarbe gezeichnet wird (ausgegraut, wenn der Knopf gesperrt ist):
fertige gibt es in `UI.Icons` (`Up`, `Down`, `Back`, `Forward`, `Home`, `Folder`, `File`, `Close`, `Check`, `Add`, Raster 16 x 16) oder eigene mit
`UI.Geometry.Parse("M 8 1 L 15 8 L 1 8 Z")` (geschlossene Figuren werden gefüllt, offene als Linie gezeichnet). `button.Image` ist ein Bild (`Framebuffer`), etwa
`Framebuffer.FromResource(new Resource("icons/home.png"))` - auch aus einem Paket (docs/RESOURCES.md). Beides steht vor dem Text (oder allein, ohne Text), `IconSize` Pixel
groß (16); ein Knopf ohne feste Größe wird entsprechend breiter.

Neben `Label`, `Button`, `CheckBox` und `TextBox` (siehe Aufbau) bringt die Bibliothek diese Elemente mit; alle erben von `UI.Element`, haben also Layout-Felder, `Style`, `Template` (wo es Sinn hat) und lassen sich stylen:

- **`ScrollViewer(x, y, w, h)`**: zeigt EIN Kind (`SetContent(element)`) in einem Ausschnitt; Bildlaufleisten erscheinen, wenn es nicht hineinpasst (`VerticalScrollMode`/`HorizontalScrollMode` = `UI.ScrollMode.Disabled/Auto/Visible`, senkrecht Auto, waagerecht Disabled: dann wird das Kind auf die Breite des Ausschnitts begrenzt, ein WrapPanel bricht um).
  Mausrad (mit waagerechtem Rad seitlich), Ziehen am Griff, Klick neben den Griff blättert eine Seite; `ScrollIntoView(x, y, w, h)`. Dahinter steckt `UI.Scroller` (Versatz, Inhalts- und Ausschnittsgröße, Griff), den die Listen mitbenutzen.
- **`ListBox(x, y, w, h)`**: `Add(item)` (Text, Zahl oder Objekt; ein Objekt gehört danach der Liste), `SetItems(liste)`, `Remove`, `Clear`; `DisplayMember = "name"` zeigt diese Eigenschaft der Objekte; `SelectedIndex`/`selectedItem`, `Select(i)`, `OnSelect`/`TakeChanged()`,
  Enter oder ein Doppelklick auf eine Zeile (zweiter Klick auf dieselbe Zeile innerhalb von `doubleClickCycles` = 24 Zyklen) meldet `OnActivate`/`TakeActivated()`; Pfeile, Pos1/Ende, Bild auf/ab, Mausrad, Klick. `ItemTemplate = new UI.DataTemplate(func (item) => element)` baut jede Zeile als eigenes Element (nur zum Anzeigen). Nach einer Änderung in den Objekten selbst: `Refresh()`.
- **`ListView`**: wie die ListBox, mit Spalten und Kopfzeile: `AddColumn("Name", "name", 100)` (Breite -1: der Rest); ein Klick auf den Titel sortiert (noch einmal: umgekehrt).
- **`CollectionView(liste)`**: eine Sicht auf eine Liste - `Filter` (Lambda mit einem Parameter), `SortBy("eigenschaft", absteigend)` oder `Comparer` (Lambda mit zwei Parametern), `current` mit `MoveCurrentTo.../CurrentItem()`;
  `listBox.SetView(sicht)` zeigt sie (die Auswahl folgt `current`). Die Sicht aktualisiert sich beim Zeichnen, wenn sich die Anzahl der Quelle ändert, sonst mit `Refresh()`/`Invalidate()`.
- **`TreeView`**: `AddNode(new UI.TreeNode("Text", wert))`, Kinder mit `node.Add(...)`, `Expanded`; Klick aufs Kästchen klappt auf/zu, Klick wählt (`selected`, `OnSelect`/`TakeChanged()`); Auf/Ab wandern, Rechts klappt auf bzw. geht zum ersten Kind, Links klappt zu bzw. geht zum Elternknoten.
- **`RadioButtons`**: `Add("Text")`, `Header`, `Horizontal`, `SelectedIndex`/`selectedItem`, `OnChange`/`TakeChanged()`; Pfeiltasten wechseln die Wahl.
- **`ComboBox(x, y, w, h)`**: eine Auswahl aus einer aufklappenden Liste: `Add(item)`, `SetItems(liste)`/`ItemsSource`, `Remove`, `Clear`, `DisplayMember`, `SelectedIndex`/`selectedItem`, `Text` (der Text der Wahl), `SelectText("x")`,
  `OnSelect`/`TakeChanged()`; ein Klick (oder Alt+Ab, F4, Enter, Leertaste) klappt die Liste auf (höchstens `MaxDropDownItems` Zeilen, mehr rollen), ein Klick auf eine Zeile wählt, Escape oder ein Klick daneben schließt;
  zugeklappt wählen Auf/Ab, Pos1/Ende direkt. Tool-Leisten mit Kombinationsfeldern, andockbare Toolfenster und eine Statusleiste bringt `#import "ui.docking"` (docs/DOCKING.md).
- **`AutoSuggestBox(text, x, y, w, h)`**: ein TextBox, der beim Tippen eine Liste von Vorschlägen zeigt: `AddSuggestion(item)` (vorgeschlagen wird, was den Text enthält, ohne Groß-/Kleinschreibung) oder `provider = func (text) => liste`;
  Auf/Ab wählt, Enter oder Klick übernimmt (`OnChosen`/`TakeChosen()`, `chosenItem`), Escape schließt; `MaxSuggestions`.
- **Menüs:** `UI.MenuBar` mit Menüs der obersten Ebene (`bar.Add(new UI.MenuItem("File"))`), `item.Add(new UI.MenuItem("Open", func () => { ... }))`, Trenner `UI.MenuItem.Separator()`, Untermenüs (ein `MenuItem` mit eigenen Zeilen), `Checkable`/`IsChecked`, `Enabled`, `Shortcut` (nur ein Text).
  Ein Klick klappt auf, bei offenem Menü wechselt der Zeiger das Menü; Pfeile, Enter, Escape. **Kontextmenü:** `element.contextMenu = liste` (eine Liste von `MenuItem`) - ein Rechtsklick auf das Element oder einen Nachfahren zeigt es; allgemein `root.ShowMenu(liste, x, y)`.
- **Schwebende Elemente:** `root.ShowPopup(element, x, y, transient = true, owner = undefined)` legt ein Element über die Oberfläche (zuletzt gezeichnet, zuerst getroffen), `root.ClosePopup(element)`, `root.CloseAllPopups()`; ein Klick daneben ruft `element.PopupOutside(root, x, y)` (schließt und
  verbraucht den Klick, wenn er den `owner` traf); `grabsKeys` heißt: es bekommt zuerst die Tasten. Menüs und die Vorschlagsliste sind solche Elemente.
- **`ToolBar`**: ein waagerechtes StackPanel mit `AddButton("Text", func () => { ... })` und `AddSeparator()`.
- **`Image(framebuffer)`**: zeigt einen Framebuffer (`Framebuffer.FromResource(new Resource("logo.png"))`, ein geladenes oder selbst gemaltes Bild) mit Alpha; `Stretch = UI.Stretch.None/Fill/Uniform/UniformToFill`.
- **Formen:** `Rectangle`, `Ellipse` (Größe des Elements), `Line(x1, y1, x2, y2)`, `Path(geometry)` - alle mit `Fill` (Brush) und `Stroke` (Pen). Eine **`Geometry`** besteht aus Linienzügen: `MoveTo/LineTo/QuadTo/CubicTo/Close`, `AddRect/AddEllipse/AddArc` oder
  `UI.Geometry.Parse("M 10 10 L 50 10 C 60 20 60 40 25 50 Z")` (Befehle M L H V C Q Z, klein = relativ; Zahlen ganzzahlig); `path.SetData(text)`. Geschlossene Figuren werden gefüllt (Even-Odd).
- **`DrawingCanvas(x, y, w, h)`**: eine Zeichenfläche mit eigenem Framebuffer (`canvas.framebuffer`, gezeichnet wird mit `canvas.renderer`): `OnPaint = func (c) => { ... }` läuft beim Anlegen und bei Größenänderung, nach `Invalidate()` und - mit `Continuous` - in jedem Bild;
  `SetSource(framebuffer)` zeigt stattdessen einen fremden Framebuffer; `onMouseDown/onMouseMove/onMouseUp` melden Mausereignisse (x, y relativ zur Fläche).
- **Mausrad und Hover:** `Element.MouseWheel(root, dx, dy)` (true = verbraucht, sonst fragt der Root den Container) und `Element.Hover(root, x, y)`; `Root.PushClip/PopClip` beschneiden das Zeichnen (siehe `docs/CONSOLE.md`, `SetClip`).

## Aufbau

- **`UI.Root(Framebuffer, Window)`**: legt einen `Renderer` zum Zeichnen an (`root.renderer`), schaltet die Ereignis-Warteschlange des Fensters ein und hält die oberste Ebene (`content`, ein `Panel`), das `theme`
  (`UI.Theme`: alle Farben als `Brush`-Felder - `back`, `panel`, `face`, `faceHover`, `facePressed`, `faceDisabled`, `inputBack`, `accent`, `text`, `textDisabled` - und `Pen`-Felder `border`, `borderFocus`, `caret`) und Fokus/Hover/gedrückt-Zustand. `Add(element)` hängt ein Element an, `Update()` bringt die Oberfläche auf den Stand und malt, was sich geändert hat (siehe *Zeichnen nur bei Änderungen*), `Draw()` zeichnet alles neu, `Tick()` ist ein Zyklus
  (`Update()`, `window.Tick()`, alle angekommenen Ereignisse verteilen; false, sobald das Fenster geschlossen ist), `Run()` ruft `Tick` bis zum Schließen - `Run(anderes)` hängt vorher ein weiteres Fenster an
  (siehe *Mehrere Fenster*). Eine Änderung an einem Feld (`label.Text = ...`) ist beim nächsten Tick sichtbar.
- **Elemente** (`UI.Element` als Basis: `Left`, `Top`, `Width`, `Height` relativ zum Container, `Visible`, `Enabled`, dazu `Hover`/`Pressed`/`Focused`):
  `Panel` (Container, `ShowBorder`, `Background` = Brush, `Filled`, `Pen` = Stift des Rahmens), `Stack` (ordnet Kinder unter- oder nebeneinander an: `Horizontal`, `Spacing`, `Padding`), `Label` (`Color`), `Button`, `CheckBox`, `TextBox`.
- **Reagieren:** jedes bedienbare Element kennt beides - ein Lambda-Feld (`OnClick`, `OnChange`, `OnEnter`) und einen abfragbaren Merker (`TakeClicked()`, `TakeChanged()`, `TakeEntered()`; liefert
  true einmal und setzt zurück). Lambdas laufen im Hauptprogramm, weil `Root.Tick` die Ereignisse per `Window.NextEvent` abholt statt über einen Callback (dessen isolierte Kopie der Globals
  würde ein Lambda in einem Objekt gar nicht erst zulassen, siehe `docs/CONSOLE.md`).
- **Besitz:** `Add` ruft `child.TakeTo(this)` - ein Element gehört seinem Container und lebt, solange der Root lebt, auch wenn es in einer Hilfsfunktion angelegt wurde.
- **Tasten vorab:** `root.onKeyPreview = func (int key, int mod) => { ... }` sieht jede Taste vor den Elementen; gibt es `true` zurück, ist die Taste verbraucht (so fängt ein Dialog Escape ab, egal wo der Fokus ist). `root.cycles` zählt die Zyklen des Roots.
- **Bedienung:** Mausklick fokussiert, `Tab`/`Umschalt+Tab` wechselt den Fokus (nur sichtbare, aktive, fokussierbare Elemente), Leertaste/Enter löst Schaltfläche/Kontrollkästchen aus. `TextBox`:
  Zeichen (Texteingabe-Ereignisse), Pfeile, Pos1/Ende, Rücktaste/Entf, Klick setzt die Einfügemarke, der sichtbare Ausschnitt wandert mit, `MaxLength`, Enter meldet `entered`.
- **Zeichnen:** flach, mit der eingebauten 8x14-Schrift (`root.cw`/`root.ch` sind Breite und Höhe eines Zeichens) oder - mit `Font`/`FontSize` an einem Element, siehe *Schriften* unten - mit einer TrueType-Schrift; Gezeichnet wird mit Pinseln und Stiften (`new SolidBrush(UI.Color.Rgb(r, g, b))`, `new Pen(farbe, breite)`; `undefined` = Vorgabe des Themes), also auch mit halbdurchsichtigen Farben, solange `root.renderer.AlphaBlending` an ist. `UI.Color.Rgb(r, g, b)` baut die rohen Farbwerte, `UI.Keys` nennt die Tastencodes.

## Layout

Wie in WPF läuft das Layout in zwei Durchgängen je Zeichnen: `Measure` (was möchte das Element, bei begrenztem oder unbegrenztem Platz) und `Arrange` (Position und Größe im
Container). Ganze Pixel, `-1` heißt "automatisch/unbegrenzt".

- **Gemeinsame Felder** jedes `UI.Element`: `Width`/`Height` (`-1` = automatisch), `MinWidth`/`MinHeight`, `MaxWidth`/`MaxHeight`, `Margin` (`new UI.Thickness(a)` / `(waagerecht, senkrecht)` /
  `(links, oben, rechts, unten)`), `HorizontalAlign` (`UI.HAlign.Stretch/Left/Center/Right`), `VerticalAlign` (`UI.VAlign....`). Nach dem Layout liefern `actualWidth`/`actualHeight` die Größe.
  Abweichung von WPF: ein `Stretch`-Element mit ausdrücklicher Breite/Höhe wird links bzw. oben ausgerichtet.
- **`Panel`** / **`Canvas`**: absolute Positionen (`x`, `y`) wie bisher.
- **`StackPanel`** (Alias `Stack`): `Horizontal`, `Spacing`, `Padding`; Kinder quer gestreckt, unsichtbare zählen nicht.
- **`DockPanel`**: Kind-Feld `Dock` (`UI.Dock.Left/Top/Right/Bottom`) in Reihenfolge; das letzte Kind füllt den Rest.
- **`WrapPanel`**: bricht in die nächste Zeile bzw. Spalte um.
- **`Grid`**: `SetRows("24, *, auto")`, `SetColumns("60, 2*, auto")` (Pixel, Stern mit Gewicht, auto); Kinder mit `AddAt(kind, zeile, spalte, zeilen, spalten)` oder den Feldern `GridRow`, `GridColumn`, `GridRowSpan`, `GridColumnSpan`.
- **`Border`**: ein Kind mit Rand, Innenabstand und Hintergrund.

## Aussehen: Eigenschaften, Styles, Trigger, Vorlagen, Ressourcen

Die **Eigenschaften** der Elemente sind ihre Felder; Styles und Trigger setzen sie über den Namen (`Reflect`, deshalb bringt `#import "ui"` die Bibliothek `reflection` mit).
Allen Elementen gemeinsam: `Background` und `Foreground` (Brush), `Pen` (Stift des Rahmens), `name`; `undefined` heißt: das Steuerelement nimmt die Farben des Themes (und
färbt Hover/Druck selbst; ein gesetztes `Background` gilt in jedem Zustand, bis ein Trigger es ändert). Bei `Label` gilt `Color`, dann `Foreground`.

- **`UI.Style(target)`** sammelt `Set("eigenschaft", wert)` und Trigger. `target` ist der Stil-Schlüssel der Art Element (`element.StyleKey()`: `"Button"`, `"Label"`, `"CheckBox"`, `"TextBox"`,
  `"Panel"`, `"StackPanel"`, `"DockPanel"`, `"WrapPanel"`, `"Grid"`, `"Border"`, `"Canvas"`, `"Element"`; eine abgeleitete Klasse erbt den Schlüssel oder überschreibt `StyleKey`).
  `style.basedOn = anderer` baut auf einem Style auf.
- **Impliziter Style:** `root.resources.AddStyle(style)` (oder `panel.Resources().AddStyle(style)`) - gilt für alle Elemente dieser Art darunter; der nächste Style nach oben gewinnt, zuletzt der des Roots.
  **Expliziter Style:** `element.Style = style` (auch später; er ersetzt den impliziten). Ein Style gilt beim **ersten Layout** des Elements und überschreibt dort, was der Konstruktor oder der Code vorher
  gesetzt haben; wer einen eigenen Wert behalten will, setzt ihn nach dem ersten Zeichnen oder weist `element.Style = new UI.Style()` zu. Eigenschaften, die es beim Element nicht gibt, werden übergangen.
- **`UI.Trigger(eigenschaft, wert)`**: solange die Eigenschaft des Elements den Wert hat (`new UI.Trigger("Hover", true)`), setzen die `Set(...)`-Aufrufe ihre Werte; danach kommen die alten wieder.
  Weitere Bedingungen: `trigger.And("IsChecked", true)`. `style.AddTrigger(trigger)`. Die Trigger werden bei jedem Layout geprüft (ohne Ereignisse).
- **Vorlagen:** `UI.ControlTemplate(func (owner) => { ...; return wurzel })` ersetzt das Aussehen von `Label`, `Button` und `CheckBox` (`element.Template = vorlage`, oder per Style:
  `style.Set("Template", vorlage)`): das Lambda baut aus Elementen (`Border`, `StackPanel`, `Label`, ...) einen Teilbaum und bekommt das Steuerelement übergeben. Teile mit `name` lassen sich
  ansprechen: `vorlage.Bind("teil", "Text", "Text")` hält `teil.Text` gleich `owner.Text`; `vorlage.AddTrigger(trigger)` mit Settern, deren drittes Argument der Name des Teils ist
  (`trigger.Set("Background", brush, "teil")`), und Bedingungen auf das Steuerelement (`Hover`, `Pressed`, `Focused`, `IsChecked`, `Enabled`). Größe, Klicks und Fokus bleiben beim Steuerelement.
  `element.FindPart("name")` liefert einen Teil. `TextBox` zeichnet sich immer selbst (Einfügemarke und Auswahl).
- **`UI.DataTemplate(func (item) => element)`**: baut aus einem Datenelement das Element, das es zeigt (`template.Build(item)`; die Listen-Steuerelemente nutzen es).
- **Bindung zwischen Elementen:** `element.Bind("Text", quelle, "text")` (optional `twoWay`, `converter` = `UI.Converter`, `once` = nur einmal übertragen) gleicht bei jedem Durchlauf von `Update` ab; die Quelle ist
  ein beliebiges Objekt, der Pfad darf durch Objekte gehen (`"adresse.ort"`); ein leerer Pfad ist die Quelle selbst (bei einer Zeile mit `DataTemplate` das Datenelement, nur lesend). Eine Text-Eigenschaft zeigt jeden Wert als Text.
- **Listen:** `ListBox`/`ListView` zeigen `items`; `list.ItemsSource = liste` zeigt eine fremde Liste, ohne sie zu besitzen (Änderungen darin erscheinen von selbst, die Liste austauschen geht auch),
  `list.SetView(view, false)` eine `CollectionView` (`view.Source` ist austauschbar; mit `own = false` gehört sie nicht der Liste), `list.ItemTemplate = DataTemplate` baut die Zeilen. Wird `SelectedIndex` von außen gesetzt
  (zum Beispiel durch eine Bindung), verhält sich das wie eine Auswahl (`OnSelect`).
- **Ressourcen:** `element.Resources().Set("schluessel", wert)`, `root.resources.Set(...)`; `element.FindResource("schluessel")` sucht vom Element nach oben, zuletzt im Root (vor dem ersten Layout nur
  bei den Vorfahren); `undefined`, wenn es sie nicht gibt. Ein `ResourceDictionary` besitzt seine Werte (und Styles).

```
var rot = new SolidBrush(UI.Color.Rgb(200, 60, 60))
var stil = new UI.Style("Button")
stil.Set("Margin", new UI.Thickness(4))
stil.Set("Background", new SolidBrush(UI.Color.Rgb(60, 120, 200)))
var hover = new UI.Trigger("Hover", true)
hover.Set("Background", rot)
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
  (oder `root.Invalidate()` für alles) aufrufen, oder den Brush austauschen. `Draw()` zeichnet immer alles. Ein `DrawingCanvas` mit `Continuous = true` malt in jedem Zyklus neu.
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
ordnet sich in der neuen Größe neu an und malt neu - Elemente mit `HorizontalAlign`/`VerticalAlign` = `Stretch` (und Panels, die ihre Kinder dehnen) füllen den neuen Platz, `Dock`-, `Grid`- und `Stack`-Layouts rechnen neu, schwebende
Elemente (Menüs) bleiben im Fenster. `ui.width` und `ui.height` sind immer die aktuelle Größe; `ui.onResize = func (int w, int h) => { ... }` wird danach gerufen (zum Beispiel für ein eigenes Bild in einem `Canvas`).
Wer das Strecken des festen Bildes vorzieht, setzt `window.AutoResize = false`.

## Systemaussehen: Skalierung, Dark/Light, Akzentfarbe, Systemschrift

Jedes `UI.Root` sieht von sich aus aus wie die anderen Programme auf dem Rechner: Es fragt beim Start `SystemTheme.Current()` (`#import "windows"`) und übernimmt

- die **Skalierung** des Bildschirms (Windows: DPI, GNOME: `scaling-factor` × `text-scaling-factor`, KDE/Qt/GDK über `QT_SCALE_FACTOR`/`GDK_SCALE`; macOS skaliert das Fenster selbst),
- **Dark oder Light Mode** (Windows: `AppsUseLightTheme`, GNOME: `color-scheme`/GTK-Theme, KDE: Farbschema, macOS: `AppleInterfaceStyle`, Browser: `prefers-color-scheme`),
- die **Akzentfarbe** (Fokus, Auswahl, überfahrene Knöpfe) und
- die **Schrift der Oberfläche** samt Größe (z. B. Segoe UI, Cantarell, Noto Sans, SF Pro - die erste, die `Fonts.Has` findet; sonst bleibt die Konsolenschrift).

Überschreiben lässt sich das am Root mit drei Properties:

```
ui.Scaling = 1.0        // 1.0 = 100 %: ohne Systemskalierung (1.5 = 150 %)
ui.Dark = true          // dunkles Aussehen
ui.Accent = 0x8854D0    // Akzentfarbe als 0xRRGGBB
```

Die Elemente rechnen weiter in **logischen Pixeln**: `Left`, `Top`, Breiten, Abstände, `ui.width`/`ui.height` und die Mauspositionen der Elemente bleiben gleich, der `Renderer` rechnet beim Zeichnen
in Bildschirmpixel um (`root.renderer.scale`). TrueType-Text wird dabei in der größeren Größe gezeichnet und bleibt scharf; die Bitmap-Schriften behalten ihre Größe. Fenster und Framebuffer
wachsen mit (`Window.SetSize`), ein 400×300-Framebuffer wird bei 150 % ein 600×450-Fenster. `Theme.SetColors(dark, accent)` setzt alle Farben eines Themes auf einmal - eigene Pinsel, die ein
Programm ins Theme gesetzt hat, bleiben, solange sie kein `SolidBrush` sind.

Auf einem **Board ohne Desktop** (ESP32, FreeRTOS: `SystemTheme.embedded`) gilt dagegen: dunkel mit violettem Akzent, die schnelle eingebaute Schrift und nur ganzzahlige Skalierung.
Was nicht ermittelt werden kann, behält den Standard (100 %, hell, Akzent `0x0078D7`, Konsolenschrift); `FIRE_SYSTEM_THEME=off` in der Umgebung schaltet die Abfrage ganz ab (so laufen die Tests).

## Show, Run und der Fenster-Manager

Die Fenster laufen über einen `WindowManager` (docs/CONSOLE.md, *Fenster-Manager*): der Root hängt sich an den Zyklus seines Fensters (`window.onCycle`) und verteilt dort die
Ereignisse, zeichnet und ruft `onTick`. `ui.Show()` zeigt das Fenster und lässt das Programm weiterlaufen (läuft der Manager noch nicht, zeigt es sein Thread; die Ereignisse
behandelt das Programm an seinen sicheren Punkten und am Ende wartet es, bis das Fenster zu ist); `ui.Show(true)` ist **modal**: es kehrt erst zurück, wenn das Fenster zu ist,
und die anderen Fenster des Managers bekommen so lange keine Eingaben - so öffnet ein Klick einen Dialog (docs/DIALOGS.md). `ui.Run()` ist der Startpunkt: es lässt den Manager
blockierend auf dem aufrufenden Thread laufen, bis die Fenster zu sind - läuft er schon, wird das Fenster dort nur gezeigt (kein zweiter Loop). `while (ui.Tick()) { ... }` geht
wie bisher: ein Zyklus dieses Roots (und seiner angehängten).

## Mehrere Fenster

Ein Programm kann mehrere Fenster haben - jedes ist ein `UI.Root` mit eigenem Framebuffer und `Window`. `ui.Attach(anderes)` hängt den Root eines weiteren Fensters an - auch an den Fenster-Manager des eigenen
Fensters: `ui.Run()`/`ui.Show()` bedienen dann beide, `ui.Tick()` macht in jedem Zyklus auch dessen Zyklus, bis sein Fenster geschlossen wird. `ui.Run(anderes)` hängt an und läuft, bis beide zu sind.
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

- Keine Mehrzeilen-Textfelder, kein Drag & Drop, keine Animationen; Listenzeilen mit `ItemTemplate` sind nur zum Anzeigen (sie nehmen keine Eingaben an).
- Text ist dicktengleich (eine Schrift, eine Größe).
- Touch kennt nur einen Finger (kein Zoomen, kein Zwei-Finger-Rollen, kein langes Drücken für das Kontextmenü, kein Schwung nach dem Wischen); die Menüleiste ist nur mit Maus und Finger zu öffnen. Der Joystick-Wiederholtakt zählt Zyklen, nicht Zeit.
- Die Elemente zeichnen sich in den Framebuffer des Roots; mehrere Roots auf demselben Framebuffer übermalen einander.
- Das Verhalten der Fenster-Ereignisse mit echtem SDL (Mausposition bei skaliertem Fenster, Texteingabe, echte Finger und Joysticks) ist nur aus dem Code begründet, nicht unter SDL getestet; die Bibliothek selbst ist headless
  getestet (Suite-Block "UI-Bibliothek": echter `WindowManager` mit Renderer-Attrappe).

## Schriften

Jedes `UI.Element` hat `Font` (ein Name für `Fonts.Get`, `""` = vom umgebenden Element erben) und `FontSize` (Pixel, `0` = erben). Eine Schrift an einem `Panel` gilt für alles darin, ein Menü nimmt die Schrift des Elements, von dem es geöffnet wurde,
und `root.theme.font` / `root.theme.fontSize` (Vorgabe `""` und `14`) sind der letzte Rückgriff. Ohne alles bleibt es bei der Konsolenschrift des Renderers - Größen und Bild sind dann wie vorher. Namen: `"8x14"`, `"8x8"`, eine mit
`Fonts.Add(new Resource("fonts/X.ttf"))` eingebettete Schrift (nach Dateiname, Familie, vollem Namen oder Alias) oder eine auf dem System installierte; unbekannte Namen ergeben die Konsolenschrift (docs/FONTS.md).

```
Fonts.Add(new Resource("fonts/Roboto-Regular.ttf"))
ui.theme.font = "Roboto-Regular"
ui.theme.fontSize = 16
var title = new UI.Label("Settings", 8, 8)
title.FontSize = 24                       // dieselbe Schrift, größer
var hint = new UI.Label("Esc closes", 8, 40)
hint.Font = "8x8"                         // eine Bitmap-Schrift
```

`Label`, `Button`, `CheckBox`, `TextBox`, `ListBox`/`ListView`, `TreeView`, `RadioButtons` und Menüs messen und zeichnen ihren Text mit der Schrift des Elements (`element.TextWidth(root, text)`, `element.TextHeight(root)` und
`element.DrawString(root, x, y, text, brush)` sind die Hilfen dafür, auch für eigene Elemente; `root.cw`/`root.ch` bleiben die Zellgröße der Konsolenschrift). Ein `TextBox` mit Proportionalschrift misst den Text bis zur Einfügemarke
und unter der Maus.
