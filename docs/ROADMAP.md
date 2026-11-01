# Roadmap: fire in fire

Ziel: ember, forge und spark in fire neu schreiben und nativ bauen. Diese Liste sammelt, was dafür in der Sprache, der Standardbibliothek und
dem nativen Backend fehlt, und legt die Reihenfolge fest. Die kleineren Punkte der Sprache stehen weiter in [language/roadmap.md](language/roadmap.md).

Leitlinien:

* **Packages bleiben Code-only.** Binäres entsteht nur beim Bau auf dem eigenen Rechner (Cache), nie im Package.
* **Neuer nativer Code wird in C geschrieben** (gegen `native/runtime/fire_rt.h`), nicht mehr in C++.
* VM und nativ verhalten sich gleich; jedes Feature bekommt Tests in beiden Engines (`fire.Testing`).

## Reihenfolge

| Phase | Punkt | Hängt ab von | Warum an dieser Stelle |
|---|---|---|---|
| 0 | [Native-C-Port (Kern)](#0-native-c-port-kern) | - | Debugging, binäre Libraries und alle neuen Natives bauen auf der Runtime auf; sonst doppelt |
| 1 | [Echte Enums](#1-echte-enums) | - | nur Compiler/Typprüfer, kann parallel zu 0 laufen |
| 1 | [Dictionary / HashSet](#2-dictionary--hashset) | - | Prelude in fire, ein Hash-Native; parallel zu 0 möglich |
| 2 | [Argumente und Umgebung](#3-kommandozeilenargumente-und-umgebungsvariablen) | 0 | klein; Voraussetzung für jede CLI (ember, forge) |
| 2 | [Prozesse](#4-prozesse-starten-pipes-exitcodes) | 0 | forge ruft den C-Compiler, spark ruft forge und git |
| 3 | [SHA-256](#5-sha-256) | 0 | ember prüft Downloads |
| 3 | [Zip / Deflate](#6-zip--deflate) | 0, 5 (CRC/Hash-Infrastruktur) | `.fpk` ist ein Zip |
| 4 | [Debugging nativer Builds](#7-debugging-nativer-builds-variante-b) | 0 | Runtime-Agent in C, Haken im Generator |
| 5 | [Binäre Libraries und dynamisches Laden](#8-binäre-libraries-und-dynamisches-laden) | 0, 7 (Debug-Tabellen pro Modul) | größter Umbau des Generators (getrennte Übersetzung) |
| 6 | [Clipboard, Drag & Drop](#9-clipboard-drag--drop) | 0 | Plattformschicht (SDL) und VM-Seite |

Nach Phase 3 ist **ember** in fire schreibbar (der erste Härtetest), nach Phase 5 **forge**, nach Phase 6 (plus VM/Debugger in fire, Code-Editor-Control) **spark**.

### Warum der C-Port zuerst

Doppelt geschrieben würde ohne ihn:

* der **Debug-Agent** (Runtime) und seine Tabellen (Generator) - einmal gegen `fire_rt.hpp`, dann noch einmal gegen `fire_rt.h`;
* die **getrennte Übersetzung** für binäre Libraries - sie legt fest, was in welcher Übersetzungseinheit steht, und die Grenze ist eine C-ABI;
* jeder **neue Native** (Prozesse, Argumente, SHA-256, Zip, Clipboard) - er würde in C++ gegen die alte Runtime entstehen und müsste portiert werden.

Unabhängig davon sind Enums und Dictionary/HashSet (Compiler und Prelude); sie können parallel laufen.

Nicht Teil von Phase 0 ist die Portierung der bestehenden C++-Natives der Packages (~25k Zeilen in `packages/*/native` und `native/platform/std`): sie kompilieren
weiter als C++ gegen `fire_rt.h` (das als C++ in `extern "C"` + `namespace fire` übersetzt) und werden nach und nach portiert.

## 0. Native-C-Port (Kern)

Stand: `native/runtime/fire_rt.h` (C11) und die C-Plattformschicht (`native/platform/*/fire_platform.h`, `std/fire_platform_*.h`) gibt es; der Generator
benutzt sie noch nicht.

1. Der Generator (`src/fire.Native/CppGenerator*.cs`) erzeugt C gegen `fire_rt.h`: keine Lambdas/Templates/`std::` im erzeugten Code, explizites
   begin/end statt RAII, `FIRE_RT_IMPLEMENTATION` in genau einer Einheit.
2. `fire_json.hpp` und die Reflection-Tabellen (`CppGenerator.Reflection.cs`, `CppGenerator.Json.cs`) nach C.
3. Package-Natives: das Programm ist C, die Natives der Packages bleiben C++ und werden als **eigene Übersetzungseinheit** gebaut und gelinkt
   (Aufrufe über die C-Funktionen von `fire_rt.h`). Das ist zugleich der erste Schritt zu Phase 5.
4. VM-Seite: `native/abi/fire_pkg_wrapper.hpp` gegen `fire_rt.h` (Modus `FIRE_LIBRARY`).
5. Toolchain (`Toolchain.cs`, `NativeBuilder`): C-Compiler für das Programm, C++ nur, wenn Natives in C++ dabei sind; ESP-IDF/FreeRTOS/wasm-Ziele.
6. `fire_rt.hpp` entfernen, wenn alle Konformitätstests nativ grün sind.

Fertig, wenn: alle Tests von `fire.Testing` für die native Engine grün sind, die Benchmarks nicht langsamer sind (`docs/NATIVE_BACKEND.md`, Messwerte) und
ein ESP32-Build durchläuft.

## 1. Echte Enums

`Color c` als Typ, `c.ToString()` gibt den Namen, `Color c = 5` ist eine Warnung (language/roadmap.md 2.2, 3.2). Laufzeitwert bleibt eine Zahl mit
Typkennung (VM und nativ), `switch` über Enums prüft auf fehlende Fälle (Warnung). Optional `[Flags]`.

## 2. Dictionary / HashSet

* `Dictionary<K, V>` (`Add`, `Get`/`this[k]`, `TryGet`, `ContainsKey`, `Remove`, `Keys`, `Values`, `Count`, `foreach` über Paare) und `HashSet<T>`
  im Prelude.
* Gleichheit und Hash: `string`, Zahlen (mit Einheit: nach Wert, wie `==`), `char`, `bool`, Enums; Objekte nach Identität, außer die Klasse hat
  `bool Equals(other)` und `int GetHashCode()`.
* Besitz: **keine implizite Übernahme**, wie bei `List`: der Container behält nur, was ihm mit `take` übergeben wird (`dict.Add(key, take item)`,
  intern `try value.TakeTo(this)`); sonst hält er nur einen Verweis.
* Besitzende Container auf Wunsch: Konstruktor-Optionen `ownsKeys` und `ownsValues` (`new Dictionary<string, Node>(ownsValues: true)`). Dann übernimmt
  der Container jedes Objekt, das hineinkommt (`TakeTo(this)` ohne `try`), und zerstört es, wenn es herausfällt (`Remove`, Überschreiben, `Clear`) -
  außer es wird vorher mit `take` herausgenommen (`var n = take dict.Take(key)`). `ownsValues` bekommt auch `List<T>` (`new List<Node>(ownsValues: true)`);
  ohne die Option bleibt `List` wie heute.
* Ein Native `__hash(value)` für die Geschwindigkeit; die Tabelle selbst in fire.

## 3. Kommandozeilenargumente und Umgebungsvariablen

* `Environment.Args` (string[] ohne den Programmnamen), `Environment.ProgramPath`, `Environment.Get(name)`/`Set`/`Variables`, `Environment.Exit(code)`
  (heute `terminate(n)`), `Environment.NewLine`, `Environment.Platform`.
* Nativ: `int main(int argc, char** argv)` im Generator, an die Runtime weitergereicht; auf Zielen ohne Prozess (ESP32) leere Argumente.
* VM: `forge run x.fire -- a b c` und ein Feld für Argumente in den Run-Einstellungen von spark (und im Projekt: `settings.arguments`).

## 4. Prozesse starten, Pipes, Exitcodes

* `Process.Start(file, args[], options)` mit `WorkingDirectory`, `Environment`, Umleitung von stdin/stdout/stderr (als `IO`-Streams), `WaitForExit()`
  (optional mit Timeout), `ExitCode`, `Kill()`, `Id`; `Process.Run(file, args)` als Kurzform, die die Ausgabe und den Exitcode liefert.
* Ein Ereignis pro Ausgabezeile (für die Ausgabe des Compilers in spark), ohne den Thread zu blockieren.
* POSIX: `posix_spawn` + `pipe`/`poll`; Windows: `CreateProcessW` + Pipes; kein Prozess auf Embedded-Zielen (`NotSupportedException`).
* Ein Paket `process` (Import `"process"`), Natives in C.

## 5. SHA-256

`Hash.Sha256(bytes | string | stream)` -> Bytes, `.ToHex()`; dazu `Crc32` (für Zip) und gleich `Sha1`/`Md5` für git und alte Formate.
Reines C ohne Abhängigkeiten (läuft auch auf dem ESP32); auf Zielen mit mbedTLS/OpenSSL optional deren Implementierung. Paket `hash`.

## 6. Zip / Deflate

* `Compression.Deflate`/`Inflate` (Bytes und Streams), `GZip`.
* `Zip.Archive`: lesen (Einträge, `Open(name)` als Stream, `ExtractTo(folder)`), schreiben (`Add(name, bytes|stream|file)`, `CreateFromDirectory`),
  Zip64 lesen.
* Eigenes C (Inflate klein und robust gegen kaputte Archive, Deflate mit fester und dynamischer Huffman-Tabelle); keine externe Bibliothek, damit es
  überall gleich ist. Paket `compression`.

## 7. Debugging nativer Builds (Variante B)

Ein Debug-Agent im nativen Debug-Build, damit spark native Programme genauso debuggt wie die VM - auf jedem Ziel, ohne gdb/lldb.

1. **`#line`** im erzeugten C (alle Modi): gdb/lldb und Absturzberichte zeigen fire-Zeilen.
2. **`IDebugTarget`** in spark: die ~20 Mitglieder der VM, die `DebugSession`, `DebugThreadContext` und die Panels benutzen (`StepLine`, `StepInto`,
   `StepOut`, `RunUntilBreakpoint`, `CurrentLocation`, `CurrentTrace`, `DebugScopeChain`, `DebugGlobals`, `DebugThisValue`, `PauseOnUnhandled`, ...)
   hinter ein Interface; `VmDebugTarget` zuerst, ohne Änderung des Verhaltens.
3. **Generator** (nur Modus `debug`): beim Wechsel der Zeile `if (FIRE_UNLIKELY(g_dbgArmed)) fire_dbg_line(&frame, src, line, scope);`,
   ein Debug-Frame pro Funktion auf einem Schatten-Stack (Funktion, Zeile, `this`, Adressen der Locals), statische Tabellen Scope -> Namen/Eltern-Scope,
   eine Tabelle der Globals, Reflection-Tabellen immer.
4. **Agent** in `fire_rt.h`: ein eigener Thread außerhalb der GIL; Befehle `pause`, `continue` (Haltepunkte als Bitmap je Quelldatei), `step line/into/out`,
   `scopes`, `globals`, `this`, `expand` (Objekt/Array/Liste), `trace`, `threads`, `set` (Wert setzen), `resume` (nach unbehandelter Ausnahme).
   Semantik wie die VM: ein Haltepunkt zählt beim **Betreten** der Zeile.
5. **Transport**: Desktop TCP auf localhost oder eine Pipe, die spark beim Start übergibt (`FIRE_DEBUG=...`); ESP32 über UART/USB-CDC (mit der
   Programmausgabe gemischt, Rahmen mit Kennung) oder TCP über WLAN; wasm später über `postMessage`. Protokoll: eine JSON-Nachricht pro Zeile.
6. **Threads**: Anhalten aller Threads über die GIL; ein angehaltener Thread wartet ohne GIL (wie `Sleep`), damit `ContinueThread` einzelne Threads laufen lässt.
7. **Unbehandelte Ausnahmen**: anhalten vor dem Abrollen, wenn kein Handler da ist; `resume` wie `TryResumeUnhandled` der VM.
8. `NativeDebugTarget` in spark; *Run > Debug* mit der Engine `native` (und dem Ziel aus dem Projekt).

Nicht in dieser Phase: Edit-and-Continue, Auswerten beliebiger Ausdrücke.

## 8. Binäre Libraries und dynamisches Laden

Zwei Teile: **getrennte Übersetzung** (Code teilen, schneller bauen) und **dynamisches Laden** (Plugins, Treiber, Natives in einer VM in fire).

Getrennte Übersetzung (optional, Schalter `settings.linkage`: `"source"` = heute, Standard; `"static"`; `"shared"`):

* forge übersetzt jede Library/jedes Package zu einer eigenen Bibliothek (`.a`/`.lib` bzw. `.so`/`.dll`/`.dylib`) im Cache
  (`Packages/<name>/bin/<ziel>/<hash>/`); der Hash umfasst die Quellen, die Schnittstellen der Abhängigkeiten, Modus, Ziel und Compiler. Programme,
  die dieselbe Library benutzen (ember, forge, spark), teilen sich die gebaute Bibliothek. Das Package selbst bleibt Code-only.
* Die **Schnittstelle eines Moduls** (Klassen, Felder, Methoden, Signaturen, Einheiten, Enums) als Datei neben der Bibliothek; der Generator des
  Benutzers erzeugt gegen sie.
* Was heute "geschlossene Welt" ist, wird pro Modul: Klassen-IDs, Methoden-Verteiler, Ausnahme-Tabellen (`excMatches`), Reflection- und JSON-Tabellen,
  internierte Namen - Module **registrieren** sich beim Start in der Runtime, statt dass der Generator globale Konstanten vergibt. Devirtualisierung und
  Inlining nur innerhalb eines Moduls (oder mit `"static"` über eine Link-Zeit-Optimierung).
* Debug-Tabellen (Phase 7) pro Modul.

Dynamisches Laden:

* `Library.Load(path)` / `library.Get(name, signature)` - `dlopen`/`LoadLibrary` zur Laufzeit, Funktionszeiger als aufrufbare Lambdas (ergänzt `extern`,
  das nur feste Namen kennt).
* fire-Module als Plugins: eine mit `"shared"` gebaute Library lädt zur Laufzeit, registriert ihre Klassen; `Reflect.New("Kit.MyDriver")` findet sie
  (spark lädt so die Treiber installierter Packages).
* Prüfung der Schnittstelle beim Laden (Hash), eine verständliche Fehlermeldung statt eines Absturzes.

## 9. Clipboard, Drag & Drop

* `Clipboard.GetText`/`SetText`, `GetImage`/`SetImage` (`graphics.Image`), `GetFiles`; im `ui`: Kopieren/Einfügen in `TextBox` und eigenen Elementen.
* Drag & Drop: Dateien aus dem Betriebssystem in ein Fenster (`windows`: Ereignis `Drop`), innerhalb des `ui` zwischen Elementen (`DragStart`,
  `DragOver`, `Drop` mit Daten und Effekt); Drag aus dem Programm heraus später.
* Nativ über SDL (Clipboard, `SDL_EVENT_DROP_*`), in der VM über die C#-Natives von `windows`/`ui`.
