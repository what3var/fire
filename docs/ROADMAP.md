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
| 6 | [Ungenutzten Code weglassen](#11-ungenutzten-code-weglassen) (A-C umgesetzt) | 0; mit 5 abstimmen | Bauzeit und Größe nativer Programme (ein minimales `ui`-Programm: 7,85 MB C vorher, 2,29 MB jetzt) |
| 7 | [Kleine und alte Ziele](#12-kleine-und-alte-ziele) (Zeigerarten, typisierte Arrays, `#intwidth 32` umgesetzt) | 0, 9 | Mikrocontroller, DOS, Win 3.x, Win9x, NT 3.51-XP: Speicher pro Wert, Breiten, neue Plattformen |

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
ist als C gut 0,5 ms kürzer, die Rechenzeit gleich oder bis 20 % besser ; `method` war durch den Zugriffsschutz knapp 4x langsamer (`this.count`
über `gf_count`/`sf_count`) - behoben: ein privates Feld wird direkt gelesen und geschrieben, wenn der Zugriff schon beim Übersetzen erlaubt ist; ebenso private/geschützte Methoden und Properties. Die Zugriffsregeln entscheidet der Typprüfer, wo die Klasse des Objekts bekannt ist (`TC012`, auch lax ein Fehler); zur Laufzeit geprüft werden nur noch `any` und die Reflection. Aufrufe über ein Interface brauchen keine Prüfung: die Implementierung eines Interface-Mitglieds muss öffentlich sein (`E4063`), nativ gehen sie an einen eigenen Verteiler ohne Aufruferklasse. Emscripten (6.0): ein Programm unter node und ein Fenster im Browser (Testgruppe `packer`). Offen: MSVC und
macOS sind hier nicht installiert. ESP32: mit ESP-IDF 5.5 gebaut (ohne Warnung) und im Emulator von Espressif (QEMU) ausgeführt - Ausgabe wie in der VM;
auf echter Hardware noch nicht.

Danach: ~~die Natives der Standard-Packages in C~~ - **umgesetzt**: process, time, io, net, tls (OpenSSL, mbedTLS, SChannel), gpio, i2c, spi, usb, wifi,
devices, audio, tracker, graphics und windows sind C99 (`"language": "c"` im Manifest), ihre Plattformschichten ebenso (`native/platform/std/fire_<teil>_c.h`).
Ein Programm mit diesen Packages ist reines C und lässt sich mit TinyCC bauen (`--toolchain tcc`). Auch die 3D-Engine `scene3d` ist C99 (Branch `scene-c`:
derselbe Software-Renderer, bytegleiche Bilder wie vorher, dazu ein optionaler Hardware-Renderer über Vulkan, docs/SCENE3D.md "Hardware"), und der Simulator
`circuits` (Branch `circuits-c`: derselbe Kern, jede Rechnung in derselben Reihenfolge - die Ausgaben der Tests sind bytegleich, auch ungerundet). Kein
Standard-Package braucht mehr C++; `fire_rt_cpp.hpp` und `fire_platform.hpp` bleiben für Packages, die ihre Natives in C++ schreiben. Geprüft: die Testsuite (VM == nativ, ASan/UBSan),
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

Stand: **Generator, Runtime und Agent umgesetzt** (Punkte 1, 3, 4, 5 für TCP, stdio und die UART des ESP32, 6, 7; docs/NATIVE_BACKEND.md "Zeilen des Programms im C" und "Debug-Builds",
Protokoll: [NATIVE_DEBUG_PROTOCOL.md](NATIVE_DEBUG_PROTOCOL.md)); getestet gegen die VM (`NativeDebugTests.cs`). spark-Seite (2, 8) ebenfalls umgesetzt (`IDebugTarget`, `NativeDebugTarget`, Engine-Box, natives Run/Debug; Tests `FIRE_TESTS_ONLY=debugger`).
**ESP32 umgesetzt** (8.6): Transport UART gerahmt (CRC-16), 3 s Wartezeit, Standardeingabe durch den Agenten, spark baut/flasht über `idf.py` und debuggt bzw. zeigt die Konsole
über die serielle Schnittstelle (docs/NATIVE_BACKEND.md "Ziel `esp32`: Run und Debug auf dem Board"); getestet unter QEMU, nicht auf einem echten Board. Offen:
WLAN (TCP auf dem Board), andere Chips (USB-Serial-JTAG der C3/S3) als eigene Ziele, wasm; die serielle Schnittstelle im Einstellungsdialog von spark (heute `port`/`baud` im JSON). Nicht getestet: `set` eines Felds mit Probes (die gibt es nur mit Reflection; geprüft ist der Setter mit der Prüfung
der Art); Windows nur mit MinGW unter Wine (nicht mit msvc auf einem echten Windows).

1. ~~**`#line`** im erzeugten C (alle Modi)~~: gdb/lldb und Absturzberichte zeigen fire-Zeilen (`"lineDirectives": false` schaltet es ab).
2. ~~**`IDebugTarget`** in spark~~ (umgesetzt): die ~20 Mitglieder der VM, die `DebugSession`, `DebugThreadContext` und die Panels benutzten, stehen hinter
   `IDebugTarget`/`IDebugThread` mit dem Wertemodell `DebugValue` (Name, Text, Art, Id, Kinder auf Anfrage) in `fire.Debugging`
   (`src/fire.Compiler/Debugging`); `VmDebugTarget` reicht die VM ohne Änderung des Verhaltens durch, dazu `set` (Local, Global, Element, Feld
   über Setter und Probes). Die Panels kennen keine `Value`/`ObjectInstance` mehr; Scope-Ansicht: Kontextmenü *Set Value...*.
   Tests: `FIRE_TESTS_ONLY=debugger`.
3. ~~**Generator**~~ (nur Modus `debug`): beim Wechsel der Zeile `if (FIRE_UNLIKELY(g_dbgArmed)) fire_dbg_line(&frame, src, line, scope);`,
   ein Debug-Frame pro Funktion auf einem Schatten-Stack (Funktion, Zeile, `this`, Adressen der Locals), statische Tabellen Scope -> Namen/Eltern-Scope,
   eine Tabelle der Globals, Reflection-Tabellen immer.
4. ~~**Agent**~~ in der Runtime (`fire_debug.h`, von `fire_rt.h` eingebunden): ein eigener Thread außerhalb der GIL; Befehle `pause`, `continue` (Haltepunkte als Bitmap je Quelldatei), `step line/into/out`,
   `scopes`, `globals`, `this`, `expand` (Objekt/Array/Liste), `trace`, `threads`, `set` (Wert setzen), `resume` (nach unbehandelter Ausnahme).
   Semantik wie die VM: ein Haltepunkt zählt beim **Betreten** der Zeile.
5. **Transport** (umgesetzt bis auf WLAN und wasm): Desktop TCP auf localhost (`FIRE_DEBUG=tcp:...`) oder gerahmt über stdio (`FIRE_DEBUG=stdio`, für Tests und
   FreeRTOS); ESP32 über die Konsolen-UART (mit der Programmausgabe gemischt: Rahmen `\x1EFD:json*crc16`, die anderen Zeilen sind Konsole bzw. Standardeingabe,
   3 s Wartezeit beim Start); später TCP über WLAN, wasm über `postMessage`. Protokoll: eine JSON-Nachricht pro Zeile.
6. ~~**Threads**~~: Anhalten aller Threads über die GIL; ein angehaltener Thread wartet ohne GIL (wie `Sleep`), damit `ContinueThread` einzelne Threads laufen lässt.
7. ~~**Unbehandelte Ausnahmen**~~: anhalten vor dem Abrollen, wenn kein Handler da ist; `resume` wie `TryResumeUnhandled` der VM.
8. ~~`NativeDebugTarget` in spark; *Run > Debug* mit der Engine `native` (und dem Ziel aus dem Projekt)~~ (umgesetzt, spark-Seite): `NativeDebugTarget`/`NativeDebugThread`
   sprechen das Protokoll (docs/NATIVE_DEBUG_PROTOCOL.md); spark lauscht, übergibt `FIRE_DEBUG`, nimmt eine Verbindung mit dem Token an. Engine-Box *VM/Native* neben der
   Modus-Box (gespeichert in `settings.engine`), natives *Run* (Release/Performance: Ausgabe ins Output-Panel) und *Debug* (Build mit `-O0 -g` nach `bin/<ziel>-dbg`),
   Stack-Panel bei nativ ausgeblendet (docs/NATIVE_BACKEND.md "Ausführen und Debuggen aus spark"). Getestet gegen einen Schein-Agenten; Ende-zu-Ende-Test gegen einen
   echten Debug-Build, sobald die Runtime den Agenten hat. ESP32 (8.6): Ziel `esp32` + Run/Debug baut das ESP-IDF-Projekt in `bin/esp32-<modus>`, `idf.py build flash`,
   öffnet die serielle Schnittstelle (`port`/`baud` aus Projekt oder Ziel, sonst erkannt) - Debug über `SerialFramedTransport`, Run mit der Konsole im Output-Panel
   und einer Eingabezeile darunter. Ein fire-Thread zeigt in der Scope-Ansicht seine eigenen Variablen der obersten Ebene (`globals` mit `thread`). Offen: die
   Oberfläche ist nicht von Hand durchgeklickt, kein echtes Board.

Nicht in dieser Phase: Edit-and-Continue, Auswerten beliebiger Ausdrücke.

## 9. Binäre Libraries und dynamisches Laden

Zwei Teile: **getrennte Übersetzung** (Code teilen, schneller bauen) und **dynamisches Laden** (Plugins, Treiber, Natives in einer VM in fire).

Stand: **Phasen A0, A, B, B2 und C umgesetzt** (C ohne Windows) (docs/NATIVE_BACKEND.md "Klassen-Deskriptoren" und "Getrennte Übersetzung: `linkage`"): Klassen-Deskriptoren statt
`switch` in den Hooks der Runtime (A0); Module mit eigener Registrierung der Zahlen über Tabellen des Programms, in einer Datei hinter `FIRE_MODULES=1` (A);
`"linkage": "static"` (Projekt, `fire.native.json`, `--linkage`) mit Schnittstellendatei (Formatversion + ABI-Hash), Cache des Benutzers nach Inhalts-Hash,
`ember cache clean`, Varianten je Menge von Schaltern, öffentliche Schnittstelle einer Library als Wurzel (§11), Debug-Tabellen je Modul (ein Haltepunkt in einer
statischen Library hält); Desktop (gcc, clang, TinyCC, MinGW), wasm (Emscripten, `emar`) und ESP32 (die Module als eigene Übersetzungseinheiten der Komponente) (B);
jedes Modul bedient seine eigenen Klassen (Verteiler, Feld-Helfer, `is of`, Operatoren, Typargumente), fremde über Tabellen der Member in den
Klassen-Deskriptoren (der Rückfall der Runtime, auch die Basis für Plugins), private Member einer Library nur, wenn sie erreicht werden, Reflection-Zeilen und
Reflect.New je Modul, Natives in C, die für sich stehen, und die JSON-Engine in der Bibliothek ihres Moduls, Sections + `--gc-sections` (B2: der zweite Bau mit
`static` ist schneller als `source`, siehe Messwerte).
`"linkage": "shared"` (C, Linux und macOS mit gcc/clang): die Libraries als Shared Libraries neben dem Programm, das den Zustand der Runtime exportiert; jede
nennt ihre Schnittstelle, das Programm prüft sie beim Start (eine lesbare Meldung statt eines Absturzes).
Tests: `FIRE_TESTS_ONLY=linkage`. Offen: `"shared"` unter Windows (Runtime als DLL, Zugriffsfunktionen für thread-lokale Variablen), `Library.Load` und Plugins (Phase D); **die Größe** (vorerst bewusst zurückgestellt: schneller bauen geht vor): mit `static` ist ein Programm noch
deutlich größer als mit `source` - CLI 2,7 MB statt 226 KB, `ui`-Beispiel 6,5 MB statt 2,3 MB, `print("hi")` 395 KB statt 26 KB (eine Library behält ihre
Schnittstelle, das Programm legt die Deskriptoren aller Klassen an, die irgendein Code einer Library anlegt - eine Erreichbarkeit über die Modulgrenzen fehlt). Wege:
die Erreichbarkeit beim Linken über die Modulgrenzen (die Schnittstellendateien sagen, was eine Library von einer anderen braucht; Tabellen und Deskriptoren nur
für Erreichtes), LTO als Option, ungenutzte Libraries-Teile mit `--gc-sections` (schon an) vollständig wegfallen lassen; Schalter wie Reflection und Probes gelten noch für alle Module (eine Variante je Menge); LTO; ein fertiges `.a`
aus dem Cache für ESP-IDF; ein Programm, das eine Klasse einer Library neu deklariert, wird mit `source` gebaut.

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
Windows `CF_HDROP`, nativ und in der VM) - VM: SDL3, nativ: SDL3, wenn die Toolchain es hat (sonst SDL2: nur Text), ohne Desktop eine eigene Zwischenablage des Programms, unter X11 auch mit dem, was vor dem Start kopiert wurde (SDL 3.2 sähe es nicht); die Ereignisse `DropFiles`/`DropText` mit Position
und `DragHover`/`DragLeave`, solange etwas aus einem anderen Programm über dem Fenster schwebt (SDL3); im `ui` die Auswahl im Textfeld mit Kopieren/Ausschneiden/Einfügen, Doppelklick markiert ein Wort,
Dreifachklick alles bzw. die Zeile (`root.clickCount`), das mehrzeilige Textfeld `UI.TextArea`, Drag & Drop zwischen Elementen (`OnDragStart`, `AllowDrop`, `OnDragOver`, `OnDrop`, `OnDragLeave`,
`OnDragDone`, `UI.DropEffect`) mit einem Bild am Zeiger (`DragData.ShowImage`: `DragData.Image`, ein durchscheinender Schnappschuss des Quellelements oder ein Schild mit dem Text), Drops aus
anderen Programmen an das Element darunter und ihr Schweben (`UI.DragData.Pending`); Ziehen aus dem Fenster heraus in andere Programme (`Window.StartDrag`: Text und Dateien, die Antwort
Kopie/Verschieben) unter Windows (OLE: `IDataObject` mit `CF_UNICODETEXT`/`CF_HDROP`) und X11 (XDND), in der VM und nativ mit SDL3 und SDL2, unter Wayland (`wl_data_device.start_drag`) nativ mit SDL3 - das `ui` übergibt ein Ziehen mit Text oder Dateien,
sobald der Zeiger das Fenster verlässt (`UI.DragData.AllowExternal`); wo die Plattform das nicht kann (`Window.CanStartDrag()`), bleibt das Ziehen im Fenster. Rückgängig/Wiederholen in `TextBox`
und `TextArea`, Ziehen der Markierung (verschieben, mit Ctrl kopieren, in andere Felder und Programme), Doppelklick in `ListBox`/`ListView`/`TreeView` über `root.clickCount` (`OnActivate`).

**Offen:**
- Ziehen aus dem Fenster heraus unter macOS (`NSDraggingSession`) und in der VM unter Wayland (die VM bräuchte dasselbe wie nativ in `SdlDragOut.cs`); dort bleibt ein Ziehen im `ui`
  im Fenster (`Window.CanStartDrag()` sagt `false`).
- Wayland-Ziehen nur mit weston (X11-Backend unter Xvfb) getestet, nicht mit GNOME/KDE.
- Das Bild am Zeiger gibt es nur im Fenster: draußen zeigt der Desktop sein eigenes Zeichen (ein Bild für OLE/XDND fehlt).
- Drag-out in der VM unter echtem Windows prüfen (OLE-Pfad nur unter Wine nativ getestet; die VM läuft unter Wine als `win-x64`-Build - die
  Zwischenablage mit `CF_HDROP` wird so geprüft -, ein Ziehen mit der "Hand" dort fehlt noch).

Ursprünglicher Plan:

* `Clipboard.GetText`/`SetText`, `GetImage`/`SetImage` (`graphics.Image`), `GetFiles`; im `ui`: Kopieren/Einfügen in `TextBox` und eigenen Elementen.
* Drag & Drop: Dateien aus dem Betriebssystem in ein Fenster (`windows`: Ereignis `Drop`), innerhalb des `ui` zwischen Elementen (`DragStart`,
  `DragOver`, `Drop` mit Daten und Effekt); Drag aus dem Programm heraus später (jetzt: Windows und X11).
* Nativ über SDL (Clipboard, `SDL_EVENT_DROP_*`), in der VM über die C#-Natives von `windows`/`ui`.

## 11. Ungenutzten Code weglassen

**Umgesetzt (Phasen A-C, Stand und Messung: docs/NATIVE_BACKEND.md "Größe des Programms").** Anlass war, dass ein minimales Programm mit `#import "ui"` (ein Fenster, ein `UI.Root`,
ein `UI.Label`) 7,85 MB C ergab und `gcc -O2` dafür über zwei Minuten brauchte. Die Ursache: `ui` benutzt die Reflection (Stile, Trigger, Bindungen setzen Properties per Name), und ein
einziger Aufruf eines `__refl_*` erzeugte **jede** Klasse des Programms mit allen Konstruktoren und Methoden. Jetzt:

* **Reflection nach Stufen** (A): Mitglieder per Name halten die Felder und Property-Zugriffe der gemachten Klassen fest, `Reflect.Call` deren Instanzmethoden, `Reflect.New` die Konstruktoren
  der Klassen in den Tabellen; die Tabellen beschreiben die erreichten Klassen (gemacht, benutzt, Basisklassen, `[Keep]`).
* **JSON** (B): Schreiben zieht nichts nach, Lesen die deklarierten Typen mit Unterklassen und Feldtypen, ein Slot ohne Klasse alle. `[NotSerializable]` (vererbt) und `"serializable": false`
  im Package (`ui`) schließen Klassen aus: Schreiben oder Lesen einer solchen ist eine `ArgumentException`, in VM und nativ gleich.
* **Tabellen** (C): gleiche Zweige der Feldhelfer einmal, `fp_` nur bei genommener Adresse, Reflection- und Proben-Tabellen ohne die geerbten Mitglieder jeder Klasse, Namenslisten einmal.
* **`[Keep]`** vor Klassen und Mitgliedern (nicht vererbt; SPEC 5.12 erlaubt Attribute jetzt auch vor Klassen ausdrücklich), **`"trim"`** in `fire.native.json`: `"reached"` (Standard),
  `"reflection"` (jede Klasse, sobald eine per Name gesucht wird - wie die VM), `"all"` (`--keep-all`; ein Debug-Build behält immer alles, für Abschnitt 8).
* Statische Initialisierer aller Klassen laufen weiter (Semantik unverändert).

Ergebnis: das minimale `ui`-Programm hat jetzt 2,29 MB C statt 7,85 MB (-71 %), `gcc -O2` braucht 49 s statt 140 s (-65 %); Messung für `graphics`, `scene3d`, JSON und Reflection in NATIVE_BACKEND.md.

**VM und nativ:** Im Standard unterscheiden sich nur Suchen per Name nach einer Klasse, die das Programm nie erreicht (`Reflect.New("X")`, `Type.Named`, `Type.Of(text)`, `Reflect.Classes()`,
JSON-`"$type"` außerhalb der deklarierten Typen): nativ eine unbekannte Klasse, in der VM gefunden - die VM kennt die Erreichbarkeit nicht. `[Keep]`, `"trim": "reflection"` oder `"all"`
machen beides gleich; getestet in allen drei Stufen.

**Offen:**

* **D - Rapid Type Analysis für den Verteiler** (eine Methode nur, wenn eine Aufrufstelle mit passendem statischen Typ sie erreicht): gemessen, nicht gebaut - im `ui`-Programm kennen 81 %
  der Aufrufstellen über den Verteiler die Klasse schon, der Filter spart höchstens 4 % des Funktionscodes.
* **E - Natives der Bridges in `#if`-Abschnitten** (`graphics`, `windows`, Zwischenablage, Drag & Drop, Bilder, Slicer): nicht gebaut; gcc verwirft nicht benutzte `static`-Funktionen schon,
  der Gewinn läge bei TinyCC und der Größe der Datei.
* **F - Weglassen in der VM** (kleinere Images, schnellerer Start, dann auch dieselben Klassen per Name wie nativ): offen.
* Mit Abschnitt 9 abstimmen: eine Library, die als `"static"`/`"shared"` vorgebaut wird, behält ihre öffentliche Schnittstelle vollständig (sie ist dann Wurzel wie `[Keep]`); heute
  sind alle Libraries `"source"` und werden mit dem Programm zusammen weggelassen.
* Was bleibt, ist beim `ui`-Programm echter Code (`UI.Root`: Ereignisse, Fokus, Popups, Ziehen und Ablegen); kleiner würde es nur durch weniger Code im Package oder ein `ui`, dessen
  Teile (Drag & Drop, Popups) erst beim Benutzen erreicht werden.

## 12. Kleine und alte Ziele

Ziel: fire auf Mikrocontrollern ab 32 KB RAM / 128 KB Flash (Cortex-M, ESP32, RISC-V, PIC32), auf DOS (Real Mode und DPMI), Win 3.x, Win9x und NT 3.51 bis XP.
Grundlage ist die Analyse auf `native-c` (Größen gemessen mit arm-none-eabi-gcc und avr-gcc): ab 32 Bit sinnvoll, 8-Bit-PIC nie (Hardware-Stack, kein
Daten-Stack bei XC8), AVR nur mit einem eigenen kleinen Profil.

**Umgesetzt** (Branch `pointer-kinds`):

* Drei Zeigerarten (`K_PtrVar`, `K_PtrByte`, `K_PtrElem`) statt `K_Pointer` + `width`; das Byte ist frei.
* Typisierte Arrays (SPEC 8.4.2): `int`, `int[N]`, `float`, `float[N]`, `bool`, `char`, `byte` als Elementtyp - strikt (falscher Typ: `ArgumentException`,
  andere Einheit: `UnitMismatchException`), `undefined` erlaubt; nativ gepackt (eine C-Zahl pro Element, `undefined` als Merker, Bitmaske erst bei Bedarf).
* `int x[4]` legt das Array überall gleich an (Local, Global, Feld mit dem Objekt); `int x[]` bleibt `undefined`.
* `#intwidth 32` (SPEC 8.2.2; `-i`, `intWidth` in Projekt und Ziel, `#if int32`); Standard bleibt 64 Bit, auch auf dem ESP32. Mit `#floatwidth 32` und 32-Bit-Zeigern
  ist ein Wert 8 statt 16 Byte (`FIRE_COMPACT`).

**Reihenfolge des Rests** (nach dem Abschluss von Abschnitt 9 auf `native-c`):

1. **Kompaktes Format ohne Grenze bei den Objekten.** Heute: 16 Bit Slot, 12 Bit Generation (65535 lebende Objekte/Arrays/Puffer). Neu: ein Objekt, Array oder
   Puffer trägt Slot (32 Bit, Nutzlast) und Generation (28 Bit, Kopf); der Zeiger steht in der Slot-Tabelle (Zeiger und Generation nebeneinander: eine Ladung).
   Zeiger in Arrays und Puffer (`ref a[i]`, `p + n`) zeigen auf einen **Kasten** `{Adresse, Slot, Generation, Zähler}` (16 Byte pro lebendem Zeiger, nicht pro
   Array oder Element), gezählt wie Strings, wiederverwendet über eine Freiliste. Mit `#performance` (keine Tabelle) tragen alle direkt ihren Zeiger. Bleibt: 65535 Einheiten.
   Später, wenn eine Messung es zeigt: einen Kasten mit Zähler 1 bei `p + 1` weiterschieben statt einen neuen zu holen.
2. **Inline-C** - eine Funktion mit C-Rumpf, der unverändert an den Compiler geht (auch Assembler im Dialekt des Compilers, `#if watcom` / `#if gcc`):

   ```fire
   inline c int Add(int a, int b) { return a + b; }
   inline c undefined Beep(int hz) { _asm { mov al, 0B6h
                                           out 43h, al } }
   ```

   Parameter und Ergebnis sind C-Typen (`int` -> `FireInt`, `float` -> `Real`, `byte[]` -> die Bytes, `any` -> `Value`); der Generator baut den Wrapper wie bei
   den Natives der Packages. In der VM ist ein Aufruf ein Fehler (oder eine Alternative mit `#if native`); der Editor hebt den Rumpf als C hervor.
3. **`dos16`** - DOS im Real Mode ab 8086: OpenWatcom `wcc`, Large Model (`far`), `__huge` für die Daten von Arrays und Puffern (`FIRE_HUGE`: keine 64-KB-Grenze),
   keine fire-Threads, Grafik über BIOS/VGA/VESA, Package **`dos`** (`int86`, Ports `In`/`Out`, `Peek`/`Poke` auf `far`-Adressen, Interruptvektoren;
   paralleler und serieller Port als Geräte), optional Package **`xms`** (Blöcke anlegen, zwischen Block und Puffer kopieren).
   Dazu **`#intwidth 16`** (native Breite des 8086/286) und **`word`** (vorzeichenlos in Maschinenbreite, `word[8|16|32]`; die VM bekommt die Wortbreite des Ziels).
4. **`dos32`** - DOS mit DPMI ab 386: `wcc386` mit DOS/4GW oder HX, flacher Speicher, keine fire-Threads, VESA-Framebuffer, Package **`dpmi`** (Interrupts mit
   Registersatz, Aufrufe in den Real Mode, physischen Speicher abbilden, DOS-Speicher unter 1 MB, Selektoren).
5. **`win16`** - Win 3.x: `wcc`, Large Model, GDI, kooperative Nachrichtenschleife (`Sleep` gibt ab), keine fire-Threads.
6. **`win32-legacy`** - Win9x, NT 3.51 bis XP (OpenWatcom `wcc386`): Synchronisation ohne Vista-APIs (Critical Section und Events statt `SRWLOCK`/
   `CONDITION_VARIABLE`), `GetTickCount` statt `GetTickCount64`, ANSI/Unicode-Schicht für 9x, `inet_addr` statt `inet_pton` (NT 3.51: Winsock 1.1), TLS über
   mbedTLS statt Schannel, kein USB, GDI aus dem laufenden Umbau.
7. **Ebene 2 im Generator**: typisierte Locals und Parameter als C-Typen (`int8_t`, ...) - in `#strict` überall, sonst wo der Generator den Typ beweist; Felder
   gepackt pro Klasse später.

Für alle neuen Ziele: Zielprofile, Toolchain OpenWatcom, eine Plattformschicht pro Familie; Tests mit DOSBox-X / dosemu2 ohne Fenster (`dos16`, `dos32`, Win 3.11),
9x und NT über QEMU-Images.

**Dazwischen, klein:**

* **Breiten pro Programm statt pro Prozess.** `Value.Ints32` und `Value.SingleFloats` sind statisch: Übersetzt der Editor ein Programm, während ein anderes mit
  `#intwidth 32` läuft, schneidet er dessen Konstanten ab. Die Breiten gehören zum Programm und werden über ein Objekt weitergereicht (das Zahlenmodell des Programms:
  der Linker und der Compiler bekommen es ausdrücklich, die VM setzt es für die Threads, auf denen das Programm läuft).
* **Gepackte Arrays nicht entpacken, wo es nicht nötig ist - samt der Natives.** Heute entpackt `arrItems` ein gepacktes Array für immer (16 Byte pro Element).
  * Runtime: JSON, der Debugger und `print` lesen gepackt (`arrElemGet`); eine API für Natives, die gepackte Arrays kennt (`arrGet`/`arrSet` pro Element,
    `arrNumbers(a, E_F32)` für direkten Zugriff auf die Zahlen eines passend typisierten Arrays, sonst null); `ref a[i]` in ein gepacktes Array wird ein Zeiger,
    der den Elementtyp kennt (16-Byte-Wert: das freie Byte; kompakt: im Kasten).
  * Natives: alle Stellen unserer Packages, die ein übergebenes Array lesen oder beschreiben, auf diese API umstellen (rund 80 Aufrufe von `arrItems` in audio,
    circuits, gpio, graphics, i2c, io, net, process, scene3d, spi, time, usb, wifi, windows und der Package-ABI; die Stellen, die ein neues Array füllen, bleiben).
    Grafik und Audio bekommen bei `float[32]`/`int[16]`-Arrays direkt die Zahlen.
  * `arrItems` bleibt nur als Notweg für fremde Natives; ein Test prüft, dass unsere Packages ein gepacktes Array nicht entpacken.
* **Mehrdimensional und Literale gleich gepackt anlegen.** Nur `new int[n]` entsteht heute direkt gepackt; `int m[3][4]`, `new int[3][4]` und `int a[] = [1, 2, 3]`
  legen erst Values an (die ungenutzt im Block bleiben). Der Generator erkennt `NewJagged` / `MakeArrayLiteral` vor `TypeArray` und baut gleich gepackt.
* **Editor:** die Breite von `int` auch im Dialog des nativen Builds (die Projekteigenschaften haben sie schon).
* **Mikrocontroller, schnelle Gewinne:** der GPIO-Simulator (8,4 KB RAM) nicht in die Firmware; kein `double`/`strtod` bei `#floatwidth 32` (~10 KB Flash, auch der
  Faktor der Einheitentabelle als `Real`); AVR: `nearbyint`, `localtime_r` ohne Rückgabe.
* **Ziele:** ESP32-C3/C6 als Varianten von `esp32`; ein generisches Ziel Cortex-M/RISC-V mit FreeRTOS (Vorgaben für STM32F4/H7, RP2040, SAMD51, PIC32);
  AVR und PIC24 später mit einem eigenen kleinen Profil (Konstanten im Flash).
* **Strikte Arrays für Strings und Klassen** (`string a[]`, `Dog a[]`): die Art beim Schreiben prüfen (ohne zu packen).
