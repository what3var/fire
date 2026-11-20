# `forge serve` - das Protokoll der Sprachdienste

Status: **Phase C Schritt 6** aus [ROADMAP_C_PORT.md](../ROADMAP_C_PORT.md#phase-c---das-frontend-in-c) (L4: Werkzeuge sind Prozesse mit Protokoll).
Umgesetzt in C (`native/forge/serve*.c`, Programm `forge-c serve`) auf dem Frontend in C ([FORGE_C.md](FORGE_C.md#forge-serve)); der Client in
spark ist `src/fire.Editor/Language/ServeClient.cs`. Die Logik von `ScriptSymbolIndex`, `CompletionEngine`, `BuiltinMembers`,
`NavigationEngine`, `MarkupCompletion` und `LiveDiagnostics` ist von spark nach forge gezogen (Port 1:1, je C-Datei die C#-Datei und ihr
Commit im Kopf); spark behält die Oberfläche (Liste, Tooltip, Unterstreichung), das Lesen der `///`-Kommentare (`DocComments.Parse`) und das
Planen seiner Prüfungen (`BackgroundCheck`, `EditLog`, die Pläne der Projekte - `BuildPlan` kennt forge-c erst mit Phase D).

```
forge-c serve [--tables <ordner>] [--packages <ordner>] [--debounce <ms>] [--log <datei>]
```

`--tables`: der Ordner mit `prelude.fire` (sonst wie bei den anderen Befehlen: `FIRE_TABLES`, `<programm>/../tables`, `native/tables`);
`--packages`: die installierten Pakete (sonst `FIRE_PACKAGES`, `<programm>/Packages`); `--debounce`: die Ruhe vor einer Analyse (Standard
300 ms, auch per `initialize`); `--log`: ein Protokoll für Menschen (was scheiterte, am Ende die Zahl der Analysen).

| Datei | C# (commit 1c549476) | Inhalt |
|---|---|---|
| `serve.c` | - | das Protokoll: Leser, Hauptfaden, Analysefaden; Dokumente, Projekte, Texte mit Zählern |
| `serve_index.c` | `ScriptSymbolIndex.cs` | der Index aus den Token (Klassen, Mitglieder, Enums, Namespaces, `#using`, `#include`), Preludes, Importe, die anderen Dateien |
| `serve_types.c` | `ScriptSymbolIndex.Types.cs`, `BuiltinMembers.cs` | der Typ eines Ausdrucks (Ketten, `var`, Lambdas, Rückgaben, Felder) |
| `serve_complete.c` | `CompletionEngine.cs` | die Vorschläge, Sichtbarkeit und Reihenfolge |
| `serve_diag.c` | `LiveDiagnostics.cs`, `ProjectLibraries.cs` | die Analyse auf dem Frontend in C |
| `serve_nav.c` | `NavigationEngine.cs` | Hover, Signatur, Definition; neu: Referenzen, Gliederung, Faltung |
| `serve_markup.c` | `MarkupCompletion.cs` | die Vervollständigung von `.fxml` (auf `markupschema.c`, Phase C Schritt 5: aktiv, sobald das Schema Teil von forge-c ist) |

## Transport

* **stdio**, eine JSON-Nachricht pro Zeile (UTF-8, `\n`; kein Zeilenumbruch in einer Nachricht). Der Server schreibt Zeichen außerhalb von ASCII als
  `\uXXXX` (UTF-16-Einheiten): jede Zeile des Servers ist reines ASCII. stderr bleibt leer (Protokoll mit `--log`).
* **Positionen** sind Offsets in **UTF-16-Einheiten** im Text des Dokuments (wie die Offsets von AvaloniaEdit), Zeilen und Spalten 1-basiert.
* Drei Arten von Nachrichten (wie JSON-RPC, ohne dessen Hülle):

| Art | Form | Antwort |
|---|---|---|
| Anfrage (Client -> Server) | `{"id":7,"method":"completion","params":{...}}` | genau eine: `{"id":7,"result":...}` oder `{"id":7,"error":{"code":"...","message":"..."}}` |
| Mitteilung (Client -> Server) | `{"method":"change","params":{...}}` (ohne `id`) | keine |
| Mitteilung (Server -> Client) | `{"method":"diagnostics","params":{...}}`, `{"method":"resync",...}` | keine |

* Drei Fäden: der **Leser** (stdin -> Warteschlange; ein `cancel` markiert eine noch wartende Anfrage), der **Hauptfaden** (Dokumente,
  Projekte, alle Antworten - in der Reihenfolge der Nachrichten), der **Analysefaden** (die Diagnosen, eine Analyse zur Zeit). Eine Änderung
  vor einer Anfrage ist also immer angewandt, wenn die Anfrage beantwortet wird; keine Anfrage wartet auf eine Analyse im Hintergrund.
* Fehlercodes: `parse` (keine JSON-Zeile), `method` (unbekannte Methode; nach `shutdown` alles außer `exit`), `params` (Feld fehlt), `document`
  (Dokument nicht offen), `cancelled` (`cancel` kam, bevor die Anfrage dran war), `internal` (Ausnahme im Server - die Anfrage scheitert, der
  Server läuft weiter).

## Lebenslauf

| Methode | Art | Parameter | Ergebnis |
|---|---|---|---|
| `initialize` | Anfrage | `debounce?` (ms), `client?` | `{"server":"forge-c","protocol":1,"capabilities":[...]}` |
| `shutdown` | Anfrage | - | `null` |
| `exit` | Mitteilung | - | der Server endet (Code 0 nach `shutdown`, sonst 1); EOF auf stdin ebenso |
| `invalidate` | Mitteilung | - | die installierten Pakete haben sich geändert: die Indizes der Preludes, die Importe und die gemerkten Analysen werden verworfen (`ForgetImports`, `LiveDiagnostics.Invalidate`), alle offenen Dokumente neu geprüft |
| `cancel` | Mitteilung | `id` | eine noch wartende Anfrage wird mit `cancelled` beantwortet; eine laufende Analyse läuft zu Ende (siehe Diagnosen) |

`capabilities`: `diagnostics`, `completion`, `hover`, `signature`, `definition`, `references`, `symbols`, `folding`, `preludeSource`, und
`markupCompletion` nur, wenn forge-c das Markup-Schema enthält. Was fehlt, macht der Client im eigenen Prozess.

## Dokumente und Kontext

| Methode | Art | Parameter |
|---|---|---|
| `open` | Mitteilung | `path`, `version`, `text`, `kind?` (`fire`; `fxml`: nur Markup-Vervollständigung, keine Analyse), `project?` (Schlüssel), `base?` (Ordner für `#include`, Standard: der von `path`), `includes?` |
| `change` | Mitteilung | `path`, `version`, **entweder** `text` (der ganze Text) **oder** `edits`: `[{"offset":o,"removed":n,"text":"..."}]` (der Reihe nach, jeder auf den Text nach dem vorigen); `includes?`, `project?` |
| `close` | Mitteilung | `path` |
| `project` | Mitteilung | `key`, `files`, `libraries?`, `typeChecking?` (`lax`/`strict`), `library?` + `projectImport?` (das Projekt ist selbst eine Bibliothek: sein eigener Import) |
| `dropProject` | Mitteilung | `key` |

* Passt ein Edit nicht in den Text, verwirft der Server ihn und schickt `{"method":"resync","params":{"path":...}}` - der Client schickt den
  ganzen Text.
* **`includes`** eines Dokuments: was der **Symbolindex** außer dem Dokument kennt (`ScriptSymbolIndex.Build(..., projectFiles)` - die
  anderen Dateien des Projekts, das erzeugte Skript einer `#include`ten `.fxml`), `[{"path","text"?}]`. Ohne `includes` nimmt der Server die
  Dateien des Projekts des Dokuments und die der Bibliotheken, die das Programm importiert (`ProjectFilesFor` von spark).
* Ein **Projekt** ist der Kontext einer **Analyse** (was `BuildPlan` weiß): `files` (`[{"path","text"?}]`, ohne `text` liest der Server die
  Datei), `libraries` (`[{"names":[...],"import","name","namespace"|null,"requires":[...],"files":[...]}]`: unter welchen Namen `#import` sie
  findet, `ProjectLibraries.NamespaceOf`, die Bibliotheken, die sie braucht - `BuildPlan.LibraryOrder` -, ihre Dateien). Ist eine Datei des
  Projekts als Dokument offen, gilt dessen Text (**Overlay**), für den Index und die Analyse jedes anderen Dokuments; eine Änderung eines
  Dokuments plant die anderen offenen Dokumente seiner Projekte neu (nach ihm).
* Gemerkt nach Inhalt (der "inkrementelle" Teil): der Index einer anderen Datei hängt an Pfad und Text (FNV-1a über die UTF-16-Einheiten) und wird
  nur neu gebaut, wenn ihr Text sich ändert; die Indizes der Preludes und die Schlüssel der Importe einmal; der Index eines Dokuments, solange
  sein Text und der seines Kontexts gleich sind; die `#import`-Namen eines Textes einmal; je Dokument die **letzten vier Analysen** nach dem
  Schlüssel aus Text, Eingaben und Einstellungen (`LiveDiagnostics.Remembered`, dort nur die letzte) - ein Undo zurück auf einen geprüften Text,
  eine Änderung, die das Dokument nicht betrifft, kosten keine Übersetzung. Das Frontend selbst übersetzt eine Analyse ganz (Prelude, Bibliotheken,
  Dokument): ein Cache übersetzter Einheiten setzt voraus, dass Parser und Resolver Zustand außerhalb des Kontexts einer Übersetzung halten
  können - offen.

## Diagnosen

Nach `open`/`change` eines Dokuments plant der Server eine Analyse: nach `debounce` ms Ruhe läuft sie auf dem Analysefaden; eine neuere
Änderung während des Wartens ersetzt sie (**latest wins**: die ältere läuft nie). Warten mehrere Dokumente, kommt das zuletzt geänderte zuerst
(das, in dem getippt wird). Eine Analyse, die während ihres Laufs überholt wird, läuft zu Ende (das Frontend hat keine Abbruchpunkte); ihr
Ergebnis kommt noch, mit `"stale":true`, solange kein neueres geschickt wurde - Fehler des Textes von eben sind besser als keine
(`BackgroundCheck.DeliverSuperseded`); ein älteres Ergebnis überschreibt nie ein neueres.

```
{"method":"diagnostics","params":{"path":"/p/main.fire","version":12,"stale":false,"ms":38,"items":[
   {"line":3,"column":0,"severity":"error","code":"E4022","message":"Unknown identifier 'x'","file":null}]}}
```

* `items` wie `LiveDiagnostics.AnalyzeInProject` (ohne Projekt: `Analyze`): Präprozessor, Parser (der erste Fehler), Resolver und Compiler (alle
  ihre Fehler), Typprüfung (Warnungen lax, Fehler strikt) über Prelude, Preludes der Importe, Bibliotheken, die anderen Dateien, das Dokument als
  letzte Quelle; gemeldet wird, was im Dokument liegt (`file` `null`), und die Warnungen der Bibliotheken (`file`: ihr Pfad). Kann eine andere
  Datei nicht gelesen werden, wird das Dokument allein geprüft und "unknown class/type 'X'" fällt weg, wenn eine andere Datei X erklärt.
  `column` 0: unbekannt. Sortiert nach Datei, dann Zeile (stabil). `ms`: die Dauer der Analyse (0: gemerkt).
* **`diagnose`** (Anfrage, `path`): dieselbe Analyse sofort, auf dem Hauptfaden, als Ergebnis `{"version","items"}`; eine geplante Analyse
  bleibt geplant. spark benutzt sie aus seinen eigenen Hintergrundprüfungen (dort Entprellen, Tor, Prioritäten wie bisher).

## Anfragen an ein Dokument

Alle mit `path` (offenes Dokument), wo eine Stelle gemeint ist mit `offset`. `doc` ist der rohe Text der `///`-Zeilen über der Deklaration
(`DocComments.ExtractRaw`; der Client liest ihn mit `DocComments.Parse`), `null`: keiner.

| Methode | Ergebnis |
|---|---|
| `completion` | `{"replaceStart","items":[{"text","kind","score","detail"?,"doc"?}]}` - **das beste zuerst** (absteigend nach `score`; bei Gleichstand nach Text ohne Groß-/Kleinschreibung, wie spark sortiert). `kind`: `keyword`, `typeKeyword`, `class`, `enum`, `enumMember`, `namespace`, `field`, `method`, `property`, `variable`, `parameter` |
| `hover` | `{"header","kind","doc","path"|"prelude","line"}` oder `null` - das Symbol unter `offset` (`TryResolveSymbol`; `header` wie `ResolvedSymbol.Header`, `kind`: `class`, `interface`, `enum`, `method`, `constructor`, `property`, `field`, `lambdaParameter`) |
| `signature` | `{"paren","active","symbol":{wie hover},"parameters":[...]}` oder `null` - der Aufruf, in dessen Klammern `offset` steht (`FindOpenCall`, `TryResolveCall`: die dokumentierte Überladung, die zu den begonnenen Argumenten passt) |
| `definition` | `{"path"|"prelude","line"}` oder `null` - die Deklaration: im Dokument, in einer anderen Datei, in einer Prelude (`standard` oder ein Import; Text per `preludeSource`), bei einer `#include`-Zeile die Datei (Zeile 1) |
| `references` | `[{"path","offset","line","column","length"}]` - jedes Vorkommen des Namens unter `offset` im Dokument, das auf dieselbe Deklaration auflöst |
| `symbols` | `[{"name","kind","line","children":[{"name","kind","line","detail"}]}]` - die Gliederung: Namespaces, Klassen mit ihren Mitgliedern, Enums des Dokuments |
| `folding` | `[{"start","end","kind"}]` - Zeilenbereiche: `{}`-Blöcke über mehrere Zeilen (`block`), Folgen von `//`-Zeilen (`comment`), `#if`...`#endif` (`region`) |
| `preludeSource` | `{"title","text"}` für `name` (`standard` oder ein Import) |
| `markupCompletion` | `{"replaceStart","items":[{"text","kind","caretBack","detail"?,"display"?}]}` für ein Dokument mit `kind` `fxml` (`MarkupCompletion.Suggest`; `kind`: `element`, `attribute`, `value`, `extension`, `namespace`) |

Was angeboten wird und in welcher Reihenfolge, ist das von spark (die Regeln im Kopf von `serve_complete.c`): nur was der Code am Cursor
benutzen darf (`private` in der Klasse selbst, `protected` auch in Ableitungen), statische Mitglieder nur an der Klasse, Instanzmitglieder nur an
Objekten (an `this.` beide), der Typ des Empfängers auch aus `var`, einer globalen Variablen, einem Lambda; die eigenen Namen und genaue Präfixe
zuerst, die Interna der Bibliotheken (kleiner Anfangsbuchstabe) zuletzt, die Helfer des Markup-Generators (`fx...`) nie. Die Prüfung vergleicht
jede Liste (Text, Art, Punkte, Detail, Reihenfolge) mit der von spark im eigenen Prozess.

## Beispiel

```
-> {"id":1,"method":"initialize","params":{"debounce":250}}
<- {"id":1,"result":{"server":"forge-c","protocol":1,"capabilities":["diagnostics","completion",...]}}
-> {"method":"open","params":{"path":"/p/a.fire","version":1,"text":"class A { int n }\nvar a = new A()\na."}}
-> {"id":2,"method":"completion","params":{"path":"/p/a.fire","offset":36}}
<- {"id":2,"result":{"replaceStart":36,"items":[{"text":"n","kind":"field","score":0.3,"detail":"Field : int"}]}}
   ... 250 ms später:
<- {"method":"diagnostics","params":{"path":"/p/a.fire","version":1,"stale":false,"ms":21,"items":[...]}}
-> {"method":"change","params":{"path":"/p/a.fire","version":2,"edits":[{"offset":36,"removed":0,"text":"n"}]}}
-> {"id":3,"method":"shutdown"}
<- {"id":3,"result":null}
-> {"method":"exit"}
```

## spark

`ServeClient` startet `forge-c serve` (gefunden über `FIRE_FORGE_C`, neben spark, sonst aus `native/forge/forge.c` mit dem C-Compiler der
Maschine gebaut, einmal, in einen Cache nach den Quellen) mit der Prelude von spark (`--tables`) und dem Paketspeicher (`PackageStore.Default`).
`ServeDocuments` hält die Kopie jedes Dokuments auf dem Server im Gleichschritt (ein Edit je Abgleich: die geänderte Mitte zwischen gleichem
Anfang und Ende), schickt die Kontextdateien des Index (`includes`) und die Projekte der Analysen (die Dateien und Bibliotheken eines
`BuildPlan`, nur wenn sich einer ihrer Texte änderte). Über das Protokoll gehen: die Vervollständigung (`ScriptEditorControl`), die Diagnosen
(`DiagnosticsJob` des Editors und `AnalyzeDocumentJob` des Hauptfensters rufen `diagnose` aus ihren Hintergrundprüfungen), die
Markup-Vervollständigung (`MarkupEditorControl`, wenn der Server sie kann). Hover, Signatur und Sprung zur Definition rechnet spark noch selbst
(die Antworten des Servers gibt es; die Tooltips und das Fenster der Prelude hängen an `ResolvedSymbol`).

Der Schalter: **`FIRE_SPARK_LANG=inproc`** behält den Weg im eigenen Prozess (`ScriptSymbolIndex`, `CompletionEngine`, `LiveDiagnostics`) bis M2;
ohne ihn geht spark über `forge serve` und fällt auf den eigenen Prozess zurück, wenn der Server nicht startet oder endet.

Die Prüfungen ([FORGE_C.md](FORGE_C.md#forge-serve)): `FIRE_TESTS_ONLY=serve` (ServeTests: Entprellen, latest wins, `stale`, gemerkte
Ergebnisse, Edits, `resync`, Projekt mit Overlay, Fehler des Protokolls, die Diagnosen von zehn Fällen gleich `LiveDiagnostics`; dazu
CompletionTests über das Protokoll, jede Liste verglichen mit dem eigenen Prozess), `FIRE_TESTS_ONLY=editorchecks` (BackgroundCheckTests und der
Protokollteil von ServeTests), `FIRE_TESTS_ONLY=completion` (über das Protokoll; mit `FIRE_SPARK_LANG=inproc` im eigenen Prozess), die
Markup-IntelliSense-Prüfungen von `FIRE_TESTS_ONLY=ui` (über das Protokoll, wenn der Server das Schema hat, verglichen mit dem eigenen Prozess).
