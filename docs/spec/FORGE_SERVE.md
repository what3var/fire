# `forge serve` - das Protokoll der Sprachdienste

Status: **Phase C Schritt 6** aus [ROADMAP_C_PORT.md](../ROADMAP_C_PORT.md#phase-c---das-frontend-in-c) (L4: Werkzeuge sind Prozesse mit Protokoll).
Umgesetzt in C (`native/forge/serve*.c`, Programm `forge-c serve`) auf dem Frontend in C ([FORGE_C.md](FORGE_C.md)); der Client in spark ist
`src/fire.Editor/Language/ServeClient.cs`. Die Logik von `ScriptSymbolIndex`, `CompletionEngine`, `BuiltinMembers`, `DocComments`,
`NavigationEngine` und `LiveDiagnostics` ist von spark nach forge gezogen (Port 1:1, je C-Datei die C#-Datei im Kopf); spark behält die
Oberfläche (Liste, Tooltip, Unterstreichung) und das Planen auf seiner Seite (`BackgroundCheck`, `EditLog`).

```
forge-c serve [--tables <ordner>] [--packages <ordner>] [--debounce <ms>] [--log <datei>]
```

## Transport

* **stdio**, eine JSON-Nachricht pro Zeile (UTF-8, `\n`; kein Zeilenumbruch in einer Nachricht). Der Server schreibt Zeichen außerhalb von ASCII als
  `\uXXXX` (UTF-16-Einheiten, auch einzelne Surrogate): jede Zeile des Servers ist reines ASCII. stderr ist nur Protokoll für Menschen (`--log`).
* **Positionen** sind Offsets in **UTF-16-Einheiten** im Text des Dokuments (wie die Offsets von AvaloniaEdit und die Spalten der Diagnosen),
  Zeilen und Spalten 1-basiert.
* Drei Arten von Nachrichten (wie JSON-RPC, ohne dessen Hülle):

| Art | Form | Antwort |
|---|---|---|
| Anfrage (Client -> Server) | `{"id":7,"method":"completion","params":{...}}` | genau eine: `{"id":7,"result":...}` oder `{"id":7,"error":{"code":"...","message":"..."}}` |
| Mitteilung (Client -> Server) | `{"method":"change","params":{...}}` (ohne `id`) | keine |
| Mitteilung (Server -> Client) | `{"method":"diagnostics","params":{...}}` | keine |

* Anfragen werden **in der Reihenfolge ihres Eintreffens** beantwortet (auf dem Lesefaden des Servers; keine Anfrage wartet auf eine Analyse).
  Eine Änderung (`change`) vor einer Anfrage ist also immer schon angewandt, wenn die Anfrage beantwortet wird: der Client braucht keine Versionen
  abzugleichen. Die **Diagnosen** laufen auf einem eigenen Faden (eine Analyse zur Zeit, siehe unten) und kommen als Mitteilung.
* Fehlercodes: `parse` (keine JSON-Zeile), `method` (unbekannte Methode), `params` (Feld fehlt/falsch), `document` (Dokument nicht offen),
  `cancelled` (`cancel`), `internal` (Ausnahme im Server - die Anfrage scheitert, der Server läuft weiter).

## Lebenslauf

| Methode | Art | Parameter | Ergebnis |
|---|---|---|---|
| `initialize` | Anfrage | `tables?` (Ordner mit `prelude.fire`), `packages?` (die installierten Pakete), `debounce?` (ms, Standard 300), `client?` | `{"server":"forge-c","protocol":1,"capabilities":[...]}` |
| `shutdown` | Anfrage | - | `null`; danach beantwortet der Server nur noch `exit` |
| `exit` | Mitteilung | - | der Server endet (Code 0 nach `shutdown`, sonst 1). EOF auf stdin wirkt wie `exit`. |
| `invalidate` | Mitteilung | - | die installierten Pakete haben sich geändert: die Indizes der Preludes und die gemerkten Analysen werden verworfen (`ScriptSymbolIndex.ForgetImports`, `LiveDiagnostics.Invalidate`), die offenen Dokumente neu geprüft |
| `cancel` | Mitteilung | `id` | eine noch nicht beantwortete Anfrage wird mit `cancelled` beantwortet; eine laufende Analyse wird verworfen (siehe Diagnosen) |

`capabilities`: `diagnostics`, `completion`, `hover`, `signature`, `definition`, `references`, `symbols`, `folding`, `markupCompletion` - was dieser
Server kann (ein älterer Server lässt weg, was er nicht kann; der Client fällt dann für diese Dienste auf den Weg im eigenen Prozess zurück).

## Dokumente und Kontext

| Methode | Art | Parameter |
|---|---|---|
| `open` | Mitteilung | `path`, `version` (Zahl), `text`, `kind?` (`fire` Standard, `fxml`), `project?` (Schlüssel eines Projekts), `base?` (Ordner für `#include`, Standard: der Ordner von `path`) |
| `change` | Mitteilung | `path`, `version`, **entweder** `text` (der ganze Text) **oder** `edits`: `[{"offset":o,"removed":n,"text":"..."}]` - der Reihe nach angewandt, jeder auf den Text nach dem vorigen (`TextEdit` von spark) |
| `close` | Mitteilung | `path` |
| `project` | Mitteilung | `key`, `files`, `libraries`, `typeChecking?` (`lax`/`strict`), `projectImport?`, `library?` (bool: das Projekt ist selbst eine Bibliothek) |
| `dropProject` | Mitteilung | `key` |

* Ein **offenes Dokument** hat den Text des Clients (auch ungespeichert); seine Version steigt mit jeder Änderung. Ein `change` mit `edits`, dessen
  Offsets nicht in den Text passen, verwirft der Server und meldet `{"method":"resync","params":{"path":...}}` - der Client schickt dann den ganzen Text.
* Ein **Projekt** ist der Kontext einer Analyse (was `BuildPlan` in spark weiß; forge-c kennt noch keine Projekte - Phase D):
  * `files`: die Dateien des Projekts, `[{"path":...,"text"?:...}]`, in der Reihenfolge des Plans; ohne `text` liest der Server die Datei selbst.
    Ist die Datei als Dokument offen, gilt der Text des Dokuments (**Overlay**) - auch für die Analyse eines anderen Dokuments.
  * `libraries`: die Bibliotheken des Plans, `[{"names":[...],"import":"Raser.Core","name":"Raser.Core","namespace":"Raser.Core"|null,"requires":[...],"files":[...]}]`
    (`names`: unter welchen Namen `#import` sie findet, `plan.Libraries`; `namespace`: `ProjectLibraries.NamespaceOf`). Ein `#import` einer Bibliothek
    nimmt ihre Dateien (und die der Bibliotheken, die sie braucht, `BuildPlan.LibraryOrder`) in die Analyse wie `ProjectLibraries.Process`.
  * Was die Vervollständigung von den anderen Dateien weiß (`ScriptSymbolIndex.Build(..., projectFiles)`): die Dateien des Projekts und die der
    Bibliotheken.
* Indizes und Analysen sind nach Inhalt gemerkt (L4 "inkrementell"): der Index einer Datei des Projekts hängt an Pfad und Text (FNV-1a über die
  UTF-16-Einheiten) und wird nur neu gebaut, wenn der Text sich ändert; die Indizes der Preludes werden einmal gebaut; eine Analyse ist gemerkt nach
  Dokumenttext, den Texten aller Eingaben und den Einstellungen (`LiveDiagnostics.Remembered`) - ein Wechsel des Tabs, ein Undo zurück auf einen geprüften
  Text, eine Änderung in einer Datei, die das Dokument nicht sieht, kosten keine Übersetzung.

## Diagnosen

Nach jedem `open`/`change` eines Dokuments (und eines Projekts, das es benutzt) plant der Server eine Analyse: nach `debounce` ms Ruhe läuft sie
auf dem Analysefaden; eine neuere Änderung während des Wartens ersetzt sie (**latest wins**: die ältere läuft nie). Es läuft eine Analyse zur Zeit;
warten mehrere Dokumente, kommt das zuerst, das zuletzt geändert wurde (das, in dem getippt wird), dann die übrigen. Eine Analyse, die während ihres
Laufs überholt wird (eine neuere Version kam), läuft zu Ende (das Frontend hat keine Abbruchpunkte); ihr Ergebnis wird **noch geschickt, mit
`"stale":true`**, wenn noch kein neueres geschickt wurde - Fehler des Textes von eben sind besser als keine, bis die neue Analyse fertig ist
(`BackgroundCheck.DeliverSuperseded`). Ergebnisse kommen in der Reihenfolge der Versionen: ein älteres überschreibt nie ein neueres.

```
{"method":"diagnostics","params":{"path":"/p/main.fire","version":12,"stale":false,"ms":38,"items":[
   {"line":3,"column":5,"severity":"error","code":"E4016","message":"Unknown identifier 'x'","file":null}]}}
```

* `items` wie `LiveDiagnostics.AnalyzeInProject`: Präprozessor, Parser (der erste Fehler), Resolver und Compiler (alle ihre Fehler) und die Typprüfung
  (Warnungen lax, Fehler strikt) über die Prelude, die Preludes der `#import`s, die anderen Dateien des Projekts und die Bibliotheken, das Dokument als
  letzte Quelle; gemeldet wird, was im Dokument liegt (`file` `null`), und die Warnungen der Bibliotheken (`file`: ihr Pfad). Kann eine andere Datei des
  Projekts gerade nicht gelesen werden, wird das Dokument allein geprüft und "unknown class/type 'X'" fallen weg, wenn eine andere Datei X erklärt.
* `column` 0: unbekannt. `severity`: `error` oder `warning`. Sortiert nach Datei, dann Zeile (stabil).
* `diagnose` (Anfrage, `path`): dieselbe Analyse sofort, ohne Warten, als Ergebnis (`{"version":..,"items":[...]}`) - für Tests und "jetzt prüfen".

## Anfragen an ein Dokument

Alle mit `path` (offenes Dokument) und, wo eine Stelle gemeint ist, `offset`.

| Methode | Ergebnis |
|---|---|
| `completion` | `{"replaceStart":s,"items":[{"text","kind","score","detail"?,"doc"?}]}` - die Vorschläge an `offset`, **das beste zuerst** (absteigend nach `score`, bei Gleichstand wie `CompletionEngine`: nach Text ohne Groß-/Kleinschreibung). `kind`: `keyword`, `typeKeyword`, `class`, `enum`, `enumMember`, `namespace`, `field`, `method`, `property`, `variable`, `parameter`. `replaceStart`: der Anfang des Wortes am Cursor (was die Auswahl ersetzt). `doc`: siehe Hover. |
| `hover` | `{"symbol":"...","kind":"...","detail":"...","doc":{"summary","params":[{"name","text"}],"returns"}}` oder `null` - was am Wort unter `offset` steht (`NavigationEngine.TryResolveSymbol`) und sein `///`-Kommentar (`DocComments`) |
| `signature` | `{"name","owner","signature","parameters":[...],"active":n,"doc"}` oder `null` - der Aufruf, in dessen Klammern `offset` steht (`FindOpenCall`/`TryResolveCall`), `active`: das Argument am Cursor |
| `definition` | `{"path"|"prelude","line","column"}` oder `null` - die Deklaration (`NavigationEngine.TryResolve`): im Dokument (`path` = das Dokument), in einer anderen Datei des Projekts (`path`), in einer Prelude (`prelude`: `standard` oder der Name des Imports; Text mit `preludeSource`) |
| `references` | `[{"path","line","column","length"}]` - jedes Vorkommen des Namens unter `offset`, das auf dieselbe Deklaration zeigt (Token-genau: gleicher Name, gleiche aufgelöste Deklaration), im Dokument und in den Dateien des Projekts |
| `symbols` | `[{"name","kind","line","detail","children":[...]}]` - Gliederung: Namespaces, Klassen (mit ihren Mitgliedern), Enums, oberste Funktionen |
| `folding` | `[{"start","end","kind"}]` - Zeilenbereiche: `{...}`-Blöcke über mehrere Zeilen (`kind` `block`), zusammenhängende `///`/`//`-Kommentare (`comment`), `#if ... #endif` (`region`) |
| `preludeSource` | `{"title","text"}` für `name` (`standard` oder ein Import) - der Text einer Prelude, die `definition` nennt (`ScriptSymbolIndex.PreludeSourceOf`/`PreludeTitleOf`) |
| `markupCompletion` | `{"replaceStart","items":[{"text","kind","detail"?,"caretBack","display"?}]}` - für ein Dokument mit `kind` `fxml` (`MarkupCompletion.Suggest`; `kind`: `element`, `attribute`, `value`, `extension`, `namespace`) |

Die Ordnung der Vorschläge und was angeboten wird, ist die von spark (die Regeln stehen im Kopf von `serve_complete.c`): nur was der Code am Cursor
benutzen darf (`private` in der Klasse selbst, `protected` auch in Ableitungen), statische Mitglieder nur an der Klasse, Instanzmitglieder nur an
Objekten (an `this.` beide), der Typ des Empfängers auch aus `var`, einer globalen Variablen, einem Lambda; die eigenen Namen und genaue Präfixe zuerst,
die Interna der Bibliotheken (kleiner Anfangsbuchstabe) zuletzt, die Helfer des Markup-Generators (`fx...`) nie.

## Beispiel

```
-> {"id":1,"method":"initialize","params":{"packages":"/opt/fire/Packages","debounce":250}}
<- {"id":1,"result":{"server":"forge-c","protocol":1,"capabilities":["diagnostics","completion",...]}}
-> {"method":"open","params":{"path":"/p/a.fire","version":1,"text":"class A { int n }\nvar a = new A()\na."}}
-> {"id":2,"method":"completion","params":{"path":"/p/a.fire","offset":36}}
<- {"id":2,"result":{"replaceStart":36,"items":[{"text":"n","kind":"field","score":0.3,"detail":"Field : int"}]}}
   ... 250 ms später:
<- {"method":"diagnostics","params":{"path":"/p/a.fire","version":1,"stale":false,"ms":21,"items":[{"line":3,...}]}}
-> {"method":"change","params":{"path":"/p/a.fire","version":2,"edits":[{"offset":36,"removed":0,"text":"n"}]}}
-> {"id":3,"method":"shutdown"}
<- {"id":3,"result":null}
-> {"method":"exit"}
```

## spark

`ServeClient` startet `forge-c serve` (gefunden über `FIRE_FORGE_C`, neben spark, sonst aus `native/forge/forge.c` gebaut), hält die offenen
Dokumente mit `open`/`change` (die `edits` aus dem `EditLog` des Editors) und die Projekte mit `project` aktuell und beantwortet die Fragen der
Editoren über das Protokoll. Der Schalter: **`FIRE_SPARK_LANG=inproc`** behält den Weg im eigenen Prozess (`ScriptSymbolIndex`, `LiveDiagnostics`
in spark) bis M2; ohne ihn (oder mit `serve`) gehen Vervollständigung und Diagnosen über `forge serve`, fällt der Server aus, über den eigenen Prozess.
Die Prüfungen: `FIRE_TESTS_ONLY=serve` (die Fälle von `CompletionTests`, die Markup-Vervollständigung und die Diagnosen über das Protokoll, dazu
Entprellen, latest wins, Versionen, Overlay, Abbruch) - siehe [FORGE_C.md](FORGE_C.md#forge-serve).
