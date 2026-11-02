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
| 0 | ~~[Native-C-Port (Kern)](#0-native-c-port-kern)~~ (umgesetzt) | - | Debugging, binäre Libraries und alle neuen Natives bauen auf der Runtime auf; sonst doppelt |
| 1 | [Echte Enums, Flag-Enums als Attribute](#1-echte-enums) | 0 | Compiler, Typprüfer, VM, Generator; `Owns` für die Container braucht sie |
| 1 | [Benannte Argumente](#2-benannte-argumente) | - | `new Dictionary<K, V>(Owns.All)` liest sich besser mit Namen; allgemein nützlich |
| 1 | [Dictionary / HashSet, `Owns`](#3-dictionary--hashset-besitz-mit-owns) | 1 (Enums) | Prelude in fire, ein Hash-Native |
| 2 | [Argumente und Umgebung](#4-kommandozeilenargumente-und-umgebungsvariablen) | 0 | klein; Voraussetzung für jede CLI (ember, forge) |
| 2 | [Prozesse](#5-prozesse-starten-pipes-exitcodes) | 0 | forge ruft den C-Compiler, spark ruft forge und git |
| 3 | [SHA-256](#6-sha-256) | 0 | ember prüft Downloads |
| 3 | [Zip / Deflate](#7-zip--deflate) | 0, 6 (CRC/Hash-Infrastruktur) | `.fpk` ist ein Zip |
| 4 | [Debugging nativer Builds](#8-debugging-nativer-builds-variante-b) | 0 | Runtime-Agent in C, Haken im Generator |
| 5 | [Binäre Libraries und dynamisches Laden](#9-binäre-libraries-und-dynamisches-laden) | 0, 8 (Debug-Tabellen pro Modul) | größter Umbau des Generators (getrennte Übersetzung) |
| 6 | [Clipboard, Drag & Drop](#10-clipboard-drag--drop) | 0 | Plattformschicht (SDL) und VM-Seite |
| 6 | [Ungenutzten Code weglassen](#11-ungenutzten-code-weglassen) | 0; mit 5 abstimmen | Bauzeit und Größe nativer Programme (ein leeres `ui`-Programm: ~7,4 MB C) |

Nach Phase 3 wäre **ember** in fire schreibbar (der erste Härtetest), nach Phase 5 **forge**, nach Phase 6 (plus VM/Debugger in fire, Code-Editor-Control) **spark**.

### Warum der C-Port zuerst

Doppelt geschrieben würde ohne ihn:

* der **Debug-Agent** (Runtime) und seine Tabellen (Generator) - einmal gegen `fire_rt.hpp`, dann noch einmal gegen `fire_rt.h`;
* die **getrennte Übersetzung** für binäre Libraries - sie legt fest, was in welcher Übersetzungseinheit steht, und die Grenze ist eine C-ABI;
* jeder **neue Native** (Prozesse, Argumente, SHA-256, Zip, Clipboard) - er würde in C++ gegen die alte Runtime entstehen und müsste portiert werden.

Unabhängig davon sind Enums und Dictionary/HashSet (Compiler und Prelude); sie können parallel laufen.

Nicht Teil von Phase 0 ist die Portierung der bestehenden C++-Natives der Packages (~25k Zeilen in `packages/*/native` und `native/platform/std`): sie kompilieren
weiter als C++ gegen `fire_rt.h` (das als C++ in `extern "C"` + `namespace fire` übersetzt) und werden nach und nach portiert.

## 0. Native-C-Port (Kern)

Stand (Branch `native-c`): **umgesetzt**. Der Generator erzeugt C11 (`fire_program.c`) gegen `fire_rt.h`; `fire_rt.hpp` ist entfernt.

1. ~~Der Generator erzeugt C gegen `fire_rt.h`~~ - Catch-Blöcke sind eigene Funktionen mit dem Frame ihrer Funktion, Sections und Aufruftiefe explizit,
   `fire_init()` statt statischer Initialisierung (docs/NATIVE_BACKEND.md, "C statt C++").
2. ~~JSON und Reflection nach C~~ - `fire_json.h` (die Engine in C), die Reflection-Tabellen ohne C++.
3. ~~Package-Natives als eigene Übersetzungseinheit~~ - `fire_natives.cpp` mit `fire_rt_cpp.hpp`, Aufrufe über `fire_nat_N` (C-Linkage).
4. ~~VM-Seite~~ - die Bibliotheken der Packages für die VM bauen gegen `fire_rt.h` (als C++, `FIRE_LIBRARY`).
5. ~~Toolchain~~ - C-Compiler für das Programm (aus dem C++-Compiler abgeleitet, oder `cCompiler`/`cStd`), C++ nur mit Natives; ESP-IDF-Komponent mit
   beiden Dateien; der FreeRTOS-Simulator ist C.
6. ~~`fire_rt.hpp` entfernen~~.

Geprüft: die Testsuite unter Linux (gcc 13, AddressSanitizer/UBSan) - native Fälle wie vor dem Umbau, gcc und clang ohne Warnungen (`-Wall -Wextra`, clang
auch `-pedantic`), FreeRTOS-Fälle auf dem Simulator. Benchmarks vorher/nachher (docs/NATIVE_BACKEND.md, "Benchmarks vor und nach dem C-Port"): der Programmstart
ist als C gut 0,5 ms kürzer, die Rechenzeit gleich oder bis 20 % besser - bis auf `method`, das ein späterer Generatorstand (Zugriffsschutz: `this.count` geht
über `gf_count`/`sf_count`, weil `List` ein privates Feld `count` hat) knapp 4x langsamer macht. Offen: ein echter ESP32-Build (ESP-IDF), MSVC, macOS und
Emscripten sind hier nicht installiert; der direkte Feldzugriff in diesem Fall (Typ des Objekts bekannt).

Danach: ~~die Natives der Standard-Packages in C~~ - **umgesetzt**: process, time, io, net, tls (OpenSSL, mbedTLS, SChannel), gpio, i2c, spi, usb, wifi,
devices, audio, tracker, graphics und windows sind C99 (`"language": "c"` im Manifest), ihre Plattformschichten ebenso (`native/platform/std/fire_<teil>_c.h`).
Ein Programm mit diesen Packages ist reines C und lässt sich mit TinyCC bauen (`--toolchain tcc`). Auch die 3D-Engine `scene3d` ist C99 (Branch `scene-c`:
derselbe Software-Renderer, bytegleiche Bilder wie vorher, dazu ein optionaler Hardware-Renderer über Vulkan, docs/SCENE3D.md "Hardware"). C++ bleibt bewusst der
Simulator `circuits`; `fire_rt_cpp.hpp` und `fire_platform.hpp` gibt es nur noch für ihn. Geprüft: die Testsuite (VM == nativ, ASan/UBSan),
die Fälle der Packages zusätzlich mit TinyCC und - wo möglich - für Windows (MinGW) unter Wine; ESP32/FreeRTOS nur mit Ersatz-Headern übersetzt.

## 1. Echte Enums

Ein Enum-Wert trägt zur Laufzeit seinen Enum-Typ (VM: der Wert ist ein `Int`, der statt einer Einheit seinen Typ hält; nativ: die Typ-Id in `Value.reserved`).
Wie in C# ist ein Enum **kein `int`**:

* `print(c)`, `c.ToString()` und `"" + c` zeigen den Namen (ein Wert ohne Mitglied: die Zahl); `Color c` ist ein deklarierter Typ, `c is of Color` geht,
  `c is of int` ist `False`.
* Umwandeln mit `!`: `c!int` gibt die Zahl, `5!Color` den Enum-Wert. `Color c = 5` und `int i = c` sind Warnungen des Typprüfers (lax) bzw. Fehler (strict).
* Rechnen (`c + 1`) ergibt ein gewöhnliches `int`; Vergleiche (`==`, `<`) gehen mit Werten desselben Enums und mit Zahlen.
* `switch` über ein Enum ohne `default` warnt bei fehlenden Mitgliedern.
* Die Helfer `Color.Name(v)`, `Color.Parse(text)`, `Color.IsDefined(v)` bleiben; `Color.Values()` gibt alle Mitglieder.

### Flag-Enums als Attribute

`flag enum Test { a = 1, b = 2 }` deklariert ein Flag-Enum: seine Werte lassen sich verknüpfen (`Test.a | Test.b` ist wieder ein `Test`, `ToString()` zeigt
`a, b`), `value.HasFlag(Test.a)` prüft ein Flag. Vor allem ist ein Flag-Enum ein **Attribut** - in der Schreibweise der bestehenden Attribute:

```fire
flag enum Test { a = 1, b = 2 }

[Test a]
[Test b]            // oder [Test a, b]: die Flags sammeln sich
class Sensor { ... }
```

An Klassen (neu), Methoden, Feldern und Properties. Reflection prüft die Flags: `member.HasFlag(Test.a)`, `Type.Of(obj).HasFlag(Test.a)`, der ganze Wert mit
`member.Flags(Test.a)` oder `member.Flags("Test")` (das Enum über ein Mitglied - das Ergebnis ist ein Wert des Enums - oder über seinen Namen - dann eine Zahl; `0`, wenn das Attribut fehlt).

Stand: umgesetzt (VM und nativ), dokumentiert in [Enums](language/10-enums-and-namespaces.md).

## 2. Benannte Argumente

`f(1, name: "x", ownsValues: true)` - für Methoden, Konstruktoren, Erweiterungen und Lambdas mit bekannter Signatur. Positionale Argumente zuerst, dann benannte in
beliebiger Reihenfolge; ausgelassene Parameter nehmen ihren Standardwert (Standardwerte gibt es schon). Die Überladungswahl berücksichtigt die Namen; ein Name, den
kein Parameter hat, oder ein doppelt gesetzter ist ein Fehler beim Übersetzen. VM und nativ; das Editor-Completion schlägt die Namen vor.

## 3. Dictionary / HashSet, Besitz mit `Owns`

* `Dictionary<K, V>` (`Add`, `Get`/`this[k]`, `TryGet`, `ContainsKey`, `Remove`, `Keys`, `Values`, `Count`, `foreach` über Paare) und `HashSet<T>` im Prelude.
* Gleichheit und Hash: `string`, Zahlen (mit Einheit: nach Wert, wie `==`), `char`, `bool`, Enums; Objekte nach Identität, außer die Klasse hat
  `bool Equals(other)` und `int GetHashCode()`. Ein Native für den Hash (VM und C-Runtime), die Tabelle in fire.
* Besitz über ein Enum im Konstruktor: `new Dictionary<string, Node>(Owns.All)`, `new List<Node>(Owns.Values)`.
  * `Owns.Explicit` (Standard, das bisherige Verhalten von `List`): der Container behält nur, was ihm mit `take` übergeben wird (`dict.Add(key, take node)`).
  * `Owns.Keys`, `Owns.Values`, `Owns.All`: der Container übernimmt jeden Schlüssel bzw. Wert, der hineinkommt, und zerstört ihn, wenn er herausfällt (`Remove`,
    Überschreiben, `Clear`) - außer er wird vorher herausgenommen (`var n = take dict.Take(key)`).
  * Bei `List` und `HashSet` gelten `Owns.Values`/`Owns.All` für die Elemente.

Stand: umgesetzt (VM und nativ), dokumentiert in [Arrays und Listen](language/07-arrays-and-lists.md#dictionaries-and-sets-dictionaryk-v-hashsett). Abweichungen
vom Entwurf: Schlüssel mit eigener Gleichheit implementieren das Interface `IEquatable` (`Equals`, `GetHashCode`); die Anzahl ist wie bei `List` die
read-only Property `Count` (auf ein privates Feld); die Tabellen behalten die Reihenfolge des Einfügens (`foreach` ist so in VM und nativ gleich). `List` hat `TakeAt(index)`.

## 4. Kommandozeilenargumente und Umgebungsvariablen

* `Environment.Args` (string[] ohne den Programmnamen), `Environment.ProgramPath`, `Environment.Get(name)`/`Set`/`Variables`, `Environment.Exit(code)`
  (heute `terminate(n)`), `Environment.NewLine`, `Environment.Platform`.
* Nativ: `int main(int argc, char** argv)` im Generator, an die Runtime weitergereicht; auf Zielen ohne Prozess (ESP32) leere Argumente.
* VM: `forge run x.fire -- a b c` und ein Feld für Argumente in den Run-Einstellungen von spark (und im Projekt: `settings.arguments`).

Stand: umgesetzt - `Environment` im Prelude (VM und nativ; das Betriebssystem in der Plattformschicht: `fire_plat_eachArg`, `fire_plat_programPath`,
`fire_plat_getenv`/`setenv`/`envEach`, `fire_plat_name`), `forge run ... -- a b`, `settings.arguments`, *Run > Program Arguments...* in spark. In der VM
wirkt `Set` nur auf die Sicht des Programms (nicht auf spark/forge). Unter Windows kommen die Argumente aus `GetCommandLineW` (UTF-8, nicht die ANSI-Codepage).

## 5. Prozesse starten, Pipes, Exitcodes

* `Process.Start(file, args[], options)` mit `WorkingDirectory`, `Environment`, Umleitung von stdin/stdout/stderr (als `IO`-Streams), `WaitForExit()`
  (optional mit Timeout), `ExitCode`, `Kill()`, `Id`; `Process.Run(file, args)` als Kurzform, die die Ausgabe und den Exitcode liefert.
* Ein Ereignis pro Ausgabezeile (für die Ausgabe des Compilers in spark), ohne den Thread zu blockieren.
* POSIX: `posix_spawn` + `pipe`/`poll`; Windows: `CreateProcessW` + Pipes; kein Prozess auf Embedded-Zielen (`NotSupportedException`).
* Ein Paket `process` (Import `"process"`), Natives in C.

Stand: umgesetzt (docs/PROCESS.md) - `Process.Run`/`Start`, `ProcessOptions` (Arbeitsordner, Umgebung, Umleitungen, `MergeError`), `ProcessStream` als
`IO.Stream`, `WaitForExit(timeout)`, `Kill`, `ExitCode`, `Id`, Zeilen als Ereignisse (`OnOutput`/`OnError`/`OnExit`, ausgeliefert von `Pump()`, ohne zu
blockieren). Die Natives sind - wie bei den anderen Standardpaketen - C99 (`fire_bridge_process.h`, `"language": "c"`; auch mit TinyCC), in der VM als Shared Library; POSIX mit `fork`/`execvp`
statt `posix_spawn` (der Arbeitsordner braucht sonst `posix_spawn_file_actions_addchdir_np`); Windows unter Wine geprüft.

## 6. SHA-256

Stand: **umgesetzt** (docs/HASH.md) - Paket `hash`: MD5, SHA-1, SHA-256/384/512, CRC-32, Adler-32 und HMAC in reinem C (gegen die Testvektoren von
FIPS 180, RFC 1321/2202/4231 und gegen Python geprüft; VM, gcc und TinyCC gleich). Der Zustand eines Hashers ist ein Byte-Puffer des fire-Objekts (keine
Handle-Tabelle). Eine Anbindung an mbedTLS/OpenSSL gibt es nicht - die eigene Implementierung ist überall gleich und schnell genug.

Ursprünglicher Plan:

`Hash.Sha256(bytes | string | stream)` -> Bytes, `.ToHex()`; dazu `Crc32` (für Zip) und gleich `Sha1`/`Md5` für git und alte Formate.
Reines C ohne Abhängigkeiten (läuft auch auf dem ESP32); auf Zielen mit mbedTLS/OpenSSL optional deren Implementierung. Paket `hash`.

## 7. Zip / Deflate

Stand: **umgesetzt** (docs/COMPRESSION.md) - Paket `compression`: Deflate/Inflate in eigenem C (streamfähig, gegen Python-zlib und mit 1500 kaputten
Strömen unter ASan geprüft; Größe wie zlib), `DeflateStream`/`GZipStream`/`ZLibStream`, Einmal-Funktionen, `Zip.Archive` lesen (Zip64, Datendeskriptoren,
CP437/UTF-8) und schreiben (auch streamend), Entpacken mit Schutz vor Pfaden außerhalb des Ziels. Zip64 auch beim Schreiben: Einträge über 4 GB (Zip64-Extra-Feld
im zentralen Verzeichnis, im lokalen Header bei bekannter Größe bzw. mit dem Parameter `zip64` von `CreateEntry` und bei `AddFile` großer Dateien, Datendeskriptoren mit
8-Byte-Größen), mehr als 65534 Einträge und ein zentrales Verzeichnis jenseits von 4 GB (Zip64-Endsatz mit Locator). Nur wo eine Zahl nicht passt - Archive
unter 4 GB bleiben bytegleich. Geprüft mit gesenkten Grenzen (Tests, VM == nativ) und einmal echt mit 4,3 GB (VM und nativ; `zipfile`, `unzip -t`).

Ursprünglicher Plan:

* `Compression.Deflate`/`Inflate` (Bytes und Streams), `GZip`.
* `Zip.Archive`: lesen (Einträge, `Open(name)` als Stream, `ExtractTo(folder)`), schreiben (`Add(name, bytes|stream|file)`, `CreateFromDirectory`),
  Zip64 lesen.
* Eigenes C (Inflate klein und robust gegen kaputte Archive, Deflate mit fester und dynamischer Huffman-Tabelle); keine externe Bibliothek, damit es
  überall gleich ist. Paket `compression`.

## 8. Debugging nativer Builds (Variante B)

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

## 9. Binäre Libraries und dynamisches Laden

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
* Debug-Tabellen (Abschnitt 8) pro Modul.

Dynamisches Laden:

* `Library.Load(path)` / `library.Get(name, signature)` - `dlopen`/`LoadLibrary` zur Laufzeit, Funktionszeiger als aufrufbare Lambdas (ergänzt `extern`,
  das nur feste Namen kennt).
* fire-Module als Plugins: eine mit `"shared"` gebaute Library lädt zur Laufzeit, registriert ihre Klassen; `Reflect.New("Kit.MyDriver")` findet sie
  (spark lädt so die Treiber installierter Packages).
* Prüfung der Schnittstelle beim Laden (Hash), eine verständliche Fehlermeldung statt eines Absturzes.

## 10. Clipboard, Drag & Drop

Stand: **umgesetzt** (docs/CONSOLE.md "Drag & Drop aus anderen Programmen, Zwischenablage", docs/UI.md "Textfeld", "Mehrzeiliges Textfeld" und "Drag & Drop", docs/NATIVE_BACKEND.md "SDL3 oder SDL2"):
`Clipboard.GetText`/`SetText`/`HasText`, `GetImage`/`SetImage`/`HasImage` (PNG über `Framebuffer.ToPng`, unter Windows auch `CF_DIB`) und `GetFiles`/`SetFiles`/`HasFiles` (`text/uri-list`, unter
Windows `CF_HDROP` nativ) - VM: SDL3, nativ: SDL3, wenn die Toolchain es hat (sonst SDL2: nur Text), ohne Desktop eine eigene Zwischenablage des Programms; die Ereignisse `DropFiles`/`DropText` mit Position
und `DragHover`/`DragLeave`, solange etwas aus einem anderen Programm über dem Fenster schwebt (SDL3); im `ui` die Auswahl im Textfeld mit Kopieren/Ausschneiden/Einfügen, Doppelklick markiert ein Wort,
Dreifachklick alles bzw. die Zeile (`root.clickCount`), das mehrzeilige Textfeld `UI.TextArea`, Drag & Drop zwischen Elementen (`OnDragStart`, `AllowDrop`, `OnDragOver`, `OnDrop`, `OnDragLeave`,
`OnDragDone`, `UI.DropEffect`) mit einem Schild am Zeiger (`DragData.ShowImage`), Drops aus anderen Programmen an das Element darunter und ihr Schweben (`UI.DragData.Pending`);
Ziehen aus dem Fenster heraus in andere Programme (`Window.StartDrag`: Text und Dateien, die Antwort Kopie/Verschieben) unter Windows (OLE: `IDataObject` mit `CF_UNICODETEXT`/`CF_HDROP`) und X11
(XDND), in der VM und nativ mit SDL3 und SDL2 - das `ui` übergibt ein Ziehen mit Text oder Dateien, sobald der Zeiger das Fenster verlässt (`UI.DragData.AllowExternal`).

**Offen:**
- Ziehen aus dem Fenster heraus unter Wayland (`wl_data_source`/`start_drag`; SDL gibt die Verbindung nicht so heraus, dass das neben SDL ginge) und macOS (`NSDraggingSession`); bis dahin endet dort
  ein Ziehen im `ui` beim Verlassen des Fensters mit `None` - besser wäre, es dann im Fenster weiterlaufen zu lassen (die Plattform müsste sagen, ob sie Drag-out kann).
- Ein echtes Bild des gezogenen Inhalts am Zeiger (bisher ein Schild mit Text, und nur im Fenster).
- Drag-out in der VM unter echtem Windows prüfen (OLE-Pfad nur unter Wine nativ getestet; die VM läuft dort nicht).
- SDL 3.2 unter X11 sieht nicht, was ein anderes Programm vor seinem Start kopiert hat (es fragt nach den Arten nur, wenn der Besitzer wechselt); Dateien in der Zwischenablage unter Windows
  in der VM (SDL3 hat kein `CF_HDROP`, die VM bleibt dort bei ihrer eigenen).
- `TextArea`: Rückgängig/Wiederholen, Markierung im Text ziehen (Drag & Drop aus dem Feld heraus).

Ursprünglicher Plan:

* `Clipboard.GetText`/`SetText`, `GetImage`/`SetImage` (`graphics.Image`), `GetFiles`; im `ui`: Kopieren/Einfügen in `TextBox` und eigenen Elementen.
* Drag & Drop: Dateien aus dem Betriebssystem in ein Fenster (`windows`: Ereignis `Drop`), innerhalb des `ui` zwischen Elementen (`DragStart`,
  `DragOver`, `Drop` mit Daten und Effekt); Drag aus dem Programm heraus später (jetzt: Windows und X11).
* Nativ über SDL (Clipboard, `SDL_EVENT_DROP_*`), in der VM über die C#-Natives von `windows`/`ui`.

## 11. Ungenutzten Code weglassen

Anlass: Der Generator übersetzt heute alles, was importiert ist - ein minimales Programm mit `#import "ui"` ergibt rund 207.000 Zeilen bzw. 7,4 MB C, und
`gcc -O2` braucht dafür über eine Minute (TinyCC ist schnell, das Programm wird aber trotzdem groß). Ziel: nur übersetzen, was das Programm erreichen kann -
ungenutzte Methoden, Funktionen, Felder-Initialisierer und **ganze Klassen** fallen weg.

* **Erreichbarkeit** vom Einstieg (`Main`, Tests, statische Initialisierer der erreichten Klassen) über Aufrufe, `new`, Basisklassen, Interfaces und Lambdas.
  Virtuelle Aufrufe: eine Überschreibung bleibt, wenn die Methode der Basis erreicht **und** die Klasse instanziiert wird (Rapid Type Analysis).
* **Dynamische Zugriffe** halten Code fest: `Reflect.New`/`Reflect.*`, JSON (`Json.Read<T>`), Markup des `ui` (Elemente nach Namen), Treiber und Plugins
  (Abschnitt 9), Debugger (Abschnitt 8, Ausdrücke im Haltepunkt). Dafür ein Attribut zum Behalten (z. B. `[Keep]`) und ein Schalter, der alles behält
  (Debug-Builds, Live-Sitzungen).
* **Natives**: nur die Natives erreichter Methoden einbinden; die C-Bridges der Packages so schneiden, dass ungenutzte Teile (z. B. Zwischenablage,
  Drag & Drop) gar nicht erst mitkompiliert werden.
* Tabellen (Klassen-IDs, Methoden-Verteiler, Ausnahmen, Reflection, internierte Namen) nur für das, was bleibt.
* Mit Abschnitt 9 abstimmen: Eine Library, die als `"static"`/`"shared"` vorgebaut wird, behält ihre öffentliche Schnittstelle vollständig; das Weglassen
  wirkt dort nur auf Internes bzw. beim Linken des Programms (`"source"`).
* Optional auch für die VM (kleinere Images, schnellerer Start).
* Prüfen: die Testsuite mit und ohne Weglassen gleich; Messung von Codegröße und Bauzeit (`ui`, `graphics`, `scene3d`) vorher/nachher.
