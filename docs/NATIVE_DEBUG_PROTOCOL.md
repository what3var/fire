# Debug-Protokoll nativer Programme

Ein nativer Debug-Build (Modus `debug`, siehe docs/NATIVE_BACKEND.md "Debug-Builds") enthält einen Debug-Agenten (`fire_debug.h`, Teil der Runtime). Ist beim Start
die Umgebungsvariable `FIRE_DEBUG` gesetzt, verbindet sich das Programm mit dem Debugger (spark) und lässt sich von dort genauso steuern wie die VM
(`VM.StepLine`/`StepInto`/`StepOut`/`RunUntilBreakpoint`, `DebugScopeChain`, `DebugGlobals`, `DebugThisValue`, `PauseOnUnhandled`/`TryResumeUnhandled`). Dieses Dokument beschreibt
das Protokoll; die Semantik ist die der VM (unten, "Semantik").

Ohne `FIRE_DEBUG` läuft ein Debug-Build ohne Agent (die Haken im Code sind dann nur ein nicht genommener Sprung).

## Transport

* `FIRE_DEBUG=tcp:127.0.0.1:PORT;token=T` - der Debugger lauscht auf `PORT` (nur localhost), das **Programm verbindet sich** als Client. `token` ist ein
  beliebiger Text ohne `;`, den das Programm in `hello` zurückschickt (der Debugger erkennt daran seinen Prozess).
* Kommt keine Verbindung zustande, schreibt das Programm `fire debug: cannot connect to ...` nach stderr und läuft ohne Debugger.
* **Eine JSON-Nachricht pro Zeile** (UTF-8, Ende `\n`, keine Zeilenumbrüche in der Nachricht). Das Programm schreibt Zeichen außerhalb von ASCII als `\uXXXX`
  (UTF-16-Einheiten, auch einzelne Surrogate - ein fire-String ist UTF-16); der Debugger darf UTF-8 oder `\u` schicken.
* Die Ausgabe des Programms (stdout/stderr) bleibt die Ausgabe des Prozesses; sie geht nicht über die Verbindung.
* Später (nicht in dieser Phase): ESP32 über UART/USB-CDC mit Rahmen, TCP über WLAN.

## Nachrichten

Befehle (Debugger -> Programm) haben eine Nummer und einen Namen, die Antwort nennt die Nummer:

```json
{"id": 7, "cmd": "scopes", "thread": 1}
{"re": 7, "ok": true, "scopes": [...]}
{"re": 8, "ok": false, "error": "thread 3 is not paused"}
```

Ereignisse (Programm -> Debugger) haben `event` und keine Nummer. Antworten und Ereignisse kommen in der Reihenfolge, in der sie entstehen; die Antwort auf
`continue`/`step` kommt vor dem `stopped`, das der Lauf später auslöst. Unbekannte Felder werden ignoriert (beide Seiten), unbekannte Befehle beantwortet das
Programm mit `ok: false`.

### Ereignisse

**`hello`** - das erste, was das Programm schickt:

```json
{"event": "hello", "protocol": 1, "token": "T", "engine": "native", "pid": 4711, "target": "linux", "mode": "debug",
 "firstUserSource": 3, "mainThread": 1,
 "sources": [{"index": 0, "file": null, "hash": null}, {"index": 3, "file": "/home/me/p/main.fire", "hash": "9f86d08..."}]}
```

`sources`: alle Quellen des Programms in der Reihenfolge der Quellindizes (der Index von `Chunk.MarkLine`, einer Position, eines Haltepunkts): `file` wie
`LinkedProgram.SourceFiles` (null für den Prelude, die Preludes der Imports und eine Quelle ohne Datei), `hash` = SHA-256 (hex, klein) der UTF-8-Bytes des
Quelltexts, wie ihn der Compiler bekommen hat (`Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant()`), null für den Prelude
und die Libraries. Der Debugger vergleicht damit, ob seine Dateien zum Programm passen.

Direkt danach hält der Hauptthread an (`stopped`, Grund `entry`) und wartet auf den Debugger (wie der Hauptthread der VM in spark, der erst mit dem ersten
Befehl losläuft). Nichts vom Programm ist gelaufen, auch der Prelude nicht.

**`stopped`** - ein Thread hat angehalten:

```json
{"event": "stopped", "thread": 1, "reason": "breakpoint", "source": 3, "line": 12, "depth": 1}
{"event": "stopped", "thread": 2, "reason": "unhandled", "source": 3, "line": 7, "depth": 0,
 "exception": {"class": "ArgumentException", "message": "bad value"}}
```

`reason`: `entry` (Start), `breakpoint`, `step` (Ende eines `step`), `pause` (auf `pause` hin), `unhandled` (eine Ausnahme, die nichts fängt; angehalten an der
Stelle des `throw`, bevor etwas abgerollt wird). `depth` wie `VM.DebugCallDepth` (0 = oberste Ebene des Programms oder des Threads). `message` ist der Text des
Felds `message` (kein `ToString()`), fehlt, wenn die Klasse keins hat.

**`thread`** - ein fire-Thread ist entstanden: `{"event": "thread", "thread": 2, "name": "Fire #1"}`. Er läuft sofort im Modus `continue` mit den aktuellen
Haltepunkten (wie `DebugThreadContext.ForFireThread`).

**`threadExited`** - ein fire-Thread ist zu Ende: `{"event": "threadExited", "thread": 2}`.

**`exited`** - das Programm endet: `{"event": "exited", "code": 0}` (danach schließt es die Verbindung). Auch nach `terminate`, nach einer unbehandelten Ausnahme
(`code` 1) und nach `leave`/`terminate(n)` des Programms.

### Befehle

`thread` ist die Nummer eines Threads (`hello.mainThread`, `thread`-Ereignisse). Befehle, die Werte lesen (`trace`, `scopes`, `this`, `expand`, `set`), brauchen
einen **angehaltenen** Thread; `globals` und `expand` auf `g:`-Pfaden gehen in einem Programm mit Threads immer (der Agent liest unter der GIL), in einem
Programm ohne Threads nur, solange der Hauptthread steht (sonst `ok: false`).

| Befehl | Felder | Antwort |
|---|---|---|
| `breakpoints` | `breakpoints`: `[[source, line], ...]` | ersetzt die Haltepunkte (für alle Threads, auch laufende) |
| `continue` | `thread` (fehlt: alle angehaltenen), `breakpoints` (bool, Standard true; false = bis zum Ende, wie `RunUntilEnd`) | sofort `ok`; später `stopped` |
| `step` | `thread`, `kind`: `line` (Step Over), `into`, `out` | sofort `ok`; später `stopped` (Grund `step`) - `out` auf Tiefe 0 ist `ok: false` (der Thread bleibt stehen) |
| `pause` | `thread` (fehlt: alle laufenden) | `ok`; ein laufender Thread hält an seiner nächsten Zeile an (`stopped`, Grund `pause`) |
| `threads` | - | `threads`: `[{"id", "name", "state": "paused"/"running", "reason", "source", "line", "depth"}]` (`reason`/`source`/`line` nur angehalten) |
| `trace` | `thread` | `frames`: `[{"function", "source", "line"}]`, innerster zuerst, `depth` + 1 Einträge (wie `VM.CurrentTrace`; ein Aufrufer steht auf der Zeile des Aufrufs) |
| `scopes` | `thread` | `scopes`: `[{"depth", "functionTop", "variables": [V...]}]` wie `VM.DebugScopeChain()` (innerster Block zuerst, bis zur Ebene der Funktion; auf der obersten Ebene leer) |
| `globals` | `thread` (optional) | `variables`: `[V...]` wie `VM.DebugGlobals()` (die bisher angelegten globalen Variablen); mit dem `thread` eines fire-Threads zuerst dessen eigene Variablen der obersten Ebene (in der VM: der globale Scope des Threads) |
| `this` | `thread` | `value`: V oder null (wie `VM.DebugThisValue`: kein `this` gebunden) |
| `expand` | `ref` | `children`: `[V...]` - Felder eines Objekts (in der Reihenfolge der Klasse, Basis zuerst) oder Elemente eines Arrays (`[0]`, `[1]`, ...) |
| `set` | `thread`, `ref`, `value` (Literal als Text) | `value`: V (der neue Wert) - siehe "Werte setzen" |
| `resume` | `thread` | nur angehalten mit Grund `unhandled`: die Ausnahme wird ignoriert, der `throw` ergibt `undefined` (wie `TryResumeUnhandled`); der Thread bleibt angehalten, direkt hinter dem `throw` - danach `continue`/`step` |
| `terminate` | `code` (Standard 1) | das Programm endet sofort (`exited`) |
| `detach` | - | alle Threads laufen weiter, die Haken sind aus, die Verbindung wird geschlossen |

`continue`/`step` auf einen Thread, der wegen `unhandled` steht (ohne `resume`): die Ausnahme geht ihren Weg wie ohne Debugger (Programm- bzw. Threadende,
`catch threads`).

### Werte (V)

```json
{"name": "count", "kind": "int", "display": "3", "ref": "l:1:0:2"}
{"name": "p", "kind": "object", "class": "Point", "display": "Point", "ref": "l:1:0:3", "expandable": true}
{"name": "xs", "kind": "array", "length": 3, "display": "Array[3]", "ref": "g:1", "expandable": true}
```

* `kind`: `int`, `float`, `bool`, `string`, `char`, `undefined`, `object`, `array`, `buffer`, `lambda`, `pointer`.
* `display`: für alles außer `object`/`array` der Text wie `Value.ToString()` der VM (Zahlen mit Einheit, `True`/`False`, der Name eines Enum-Werts,
  `undefined`, `<lambda>`, ...); ein String ist sein Text (ohne Anführungszeichen, höchstens 4000 Zeichen, dann `...` und `"truncated": true`).
  `object`: der Klassenname (ein zerstörtes Objekt: `<destroyed>`), `array`: `Array[n]`. Der Agent ruft **nie fire-Code** zum Anzeigen auf (kein `ToString()`).
* `expandable`: ein Objekt mit Feldern oder ein nicht leeres Array.
* `ref`: ein **Pfad** zum Wert (für `expand` und `set`), den der Debugger nicht deutet: `l:THREAD:SCOPE:INDEX` (Variable INDEX der Ebene SCOPE von `scopes`),
  `t:THREAD` (`this`), `g:SLOT` (globale Variable), `T:THREAD:INDEX` (eigene Variable eines fire-Threads), dahinter je Stufe `/N` (Feld N bzw. Element N). Der Pfad wird bei jedem `expand`/`set` neu vom Anfang
  gelesen - ein Wert, der inzwischen verschwunden ist, ergibt `ok: false`, nie einen Absturz. Ein `l:`/`t:`-Pfad gilt nur, solange der Thread an derselben
  Stelle steht.

### Werte setzen

`value` ist ein Literal: eine ganze Zahl (`42`, `-1`), eine Fließkommazahl (`2.5`, `1e3`), `true`/`false`, `undefined`, ein String in Anführungszeichen
(`"text"`, mit `\"`, `\\`, `\n`, `\t`). Eine Zahl behält die Einheit des alten Werts. Der angehaltene Thread führt das Setzen selbst aus (Referenzzählung,
Besitz wie eine Zuweisung im Programm): eine Variable und ein Array-Element werden direkt gesetzt, ein Feld über den Setter des Felds, wie die Klasse selbst
es zuweisen würde (die deklarierte Art wird geprüft, Probes hören die Änderung). Eine Ausnahme dabei ist `ok: false` mit ihrer Meldung; das Programm sieht sie
nicht und steht weiter an derselben Stelle. Die Antwort `value` ist der Wert danach.

## Semantik (wie die VM)

* **Haltepunkte** zählen beim **Betreten** einer Zeile: wenn sich die Position (Quelle, Zeile) des Threads ändert, auch wenn ein Aufruf in die Zeile
  zurückkehrt, aus der er kam - nicht auf der Zeile, auf der `continue` beginnt. Jeder Thread merkt sich seine letzte Position selbst (`RunUntilBreakpoint`
  der VM beginnt mit `last = CurrentLocation`).
* **`step line`** hält an, sobald die Zeile eine andere ist und die Aufruftiefe nicht größer als am Anfang (`VM.StepLine`, verglichen wird nur die Zeile);
  **`into`** bei jeder anderen Zeile oder Tiefe (`VM.StepInto`); **`out`**, sobald die Tiefe kleiner ist (`VM.StepOut`).
* Was die VM **verschachtelt** ausführt (Getter und Setter von Properties, Destruktoren, Operatoren, `GetIndex`/`SetIndex`, `ToString()` bei der Umwandlung in
  Text, Standardwerte von Parametern, die Umrechnungen von Einheiten, Lambdas, die ein Native aufruft), ist für Haltepunkte und Schritte unsichtbar: es läuft
  als Teil der Zeile, die es auslöst.
* Ein angehaltener Thread hält **nur sich** an; die anderen laufen weiter (wie die Threads der VM in spark). `pause` ohne `thread` hält alle an,
  `continue` ohne `thread` lässt alle weiterlaufen.
* `pause` greift an der nächsten Zeile, die der Thread betritt (die VM: nach höchstens 256 Instruktionen); ein Thread in einem langen Native (`Sleep`, Lesen)
  hält erst danach an.
* **Unbehandelte Ausnahmen**: eine Ausnahme, die kein `catch` des Threads nehmen würde, hält den Thread an der Stelle des `throw` an, bevor etwas
  abgerollt wird (wie `PauseOnUnhandled`).
