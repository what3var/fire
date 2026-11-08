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
| 6 | [Clipboard, Drag & Drop](#10-clipboard-drag--drop) | 0 | Plattformschicht und VM-Seite |
| 6 | [Ungenutzten Code weglassen](#11-ungenutzten-code-weglassen) (A-C und die Stufen 0-3 umgesetzt) | 0; mit 5 abstimmen | Bauzeit und Größe nativer Programme (ein minimales `ui`-Programm: 7,85 MB C vorher, 2,29 MB nach Phase A-C, 2,76 MB heute in Stufe 3) |
| 6 | [Bau-Reihenfolge der Quellen von Paket-Natives](#12-bau-reihenfolge-der-quellen-von-paket-natives) | 9 | ein Paket mit Headern, die einander brauchen, hängt heute an der Reihenfolge seiner Liste |
| 6 | [UI-Markup: fehlende Eigenschaften](#14-ui-markup-fehlende-eigenschaften) | - | klein; `.fxml` ist der Hauptweg für Oberflächen, dort fehlen ToolTip, Cursor und Drag & Drop |
| 0 | [Arrays statt Byte-Puffer an Natives](#15-arrays-statt-byte-puffer-an-natives) | - | **Absturz** (Segfault) der VM bei einem häufigen Fehler; vor allem anderen |
| 7 | [Kleine und alte Ziele](#13-kleine-und-alte-ziele) (Zeigerarten, typisierte Arrays, `#intwidth 32` umgesetzt) | 0, 9 | Mikrocontroller, DOS, Win 3.x, Win9x, NT 3.51-XP: Speicher pro Wert, Breiten, neue Plattformen |

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
   `fire_init()` statt statischer Initialisierung ([Natives Backend (AOT): fire -> C](spec/NATIVE_BACKEND.md), "C statt C++").
2. ~~JSON und Reflection nach C~~ - `fire_json.h` (die Engine in C), die Reflection-Tabellen ohne C++.
3. ~~Package-Natives als eigene Übersetzungseinheit~~ - `fire_natives.cpp` mit `fire_rt_cpp.hpp`, Aufrufe über `fire_nat_N` (C-Linkage).
4. ~~VM-Seite~~ - die Bibliotheken der Packages für die VM bauen gegen `fire_rt.h` (als C++, `FIRE_LIBRARY`).
5. ~~Toolchain~~ - C-Compiler für das Programm (aus dem C++-Compiler abgeleitet, oder `cCompiler`/`cStd`), C++ nur mit Natives; ESP-IDF-Komponent mit
   beiden Dateien; der FreeRTOS-Simulator ist C.
6. ~~`fire_rt.hpp` entfernen~~.

Geprüft: die Testsuite unter Linux (gcc 13, AddressSanitizer/UBSan) - native Fälle wie vor dem Umbau, gcc und clang ohne Warnungen (`-Wall -Wextra`, clang
auch `-pedantic`), FreeRTOS-Fälle auf dem Simulator. Benchmarks vorher/nachher ([Natives Backend (AOT): fire -> C](spec/NATIVE_BACKEND.md), "Benchmarks vor und nach dem C-Port"): der Programmstart
ist als C gut 0,5 ms kürzer, die Rechenzeit gleich oder bis 20 % besser ; `method` war durch den Zugriffsschutz knapp 4x langsamer (`this.count`
über `gf_count`/`sf_count`) - behoben: ein privates Feld wird direkt gelesen und geschrieben, wenn der Zugriff schon beim Übersetzen erlaubt ist; ebenso private/geschützte Methoden und Properties. Die Zugriffsregeln entscheidet der Typprüfer, wo die Klasse des Objekts bekannt ist (`TC012`, auch lax ein Fehler); zur Laufzeit geprüft werden nur noch `any` und die Reflection. Aufrufe über ein Interface brauchen keine Prüfung: die Implementierung eines Interface-Mitglieds muss öffentlich sein (`E4063`), nativ gehen sie an einen eigenen Verteiler ohne Aufruferklasse. Interfaces haben Properties (`int ReadTimeout { get; set; }`, implementiert von einer öffentlichen Property, `E4064`/`E4065`/`E4066`, `TC016`) und erweitern andere Interfaces (`interface IConnection : IO.IStream`, Zyklen `E4068`) - VM und nativ gleich, auch über Module und Plugins (SPEC 8.5); `Net.IConnection` nutzt beides. Ein Mitglied, das ein Interface-Typ (samt seinen Basis-Interfaces) nicht deklariert, ist `TC008` wie bei einer bekannten Klasse (lax Warnung, strikt Fehler; die Besitz-Methoden `TakeTo`/`TakeLocal`/... gehen immer, der Cast `x!Klasse` erreicht den Rest); `Type.Interfaces` der Reflection listet alle Interfaces, die die Klasse erreicht (auch über Basisklassen und Interface-Vererbung, volle Namen), `Type.DeclaredInterfaces` nur die genannten - VM und nativ gleich, auch Module und Plugins. Offen dabei: der Typ einer implementierenden Property wird nur bei Werttypen und Einheiten mit dem des Interfaces verglichen. Emscripten (6.0): ein Programm unter node und ein Fenster im Browser (Testgruppe `packer`). Offen: MSVC und
macOS sind hier nicht installiert. ESP32: mit ESP-IDF 5.5 gebaut (ohne Warnung) und im Emulator von Espressif (QEMU) ausgeführt - Ausgabe wie in der VM;
auf echter Hardware noch nicht.

Danach: ~~die Natives der Standard-Packages in C~~ - **umgesetzt**: process, time, io, net, tls (OpenSSL, mbedTLS, SChannel), gpio, i2c, spi, usb, wifi,
devices, audio, tracker, graphics und windows sind C99 (`"language": "c"` im Manifest), ihre Plattformschichten ebenso (`native/platform/std/fire_<teil>_c.h`).
Ein Programm mit diesen Packages ist reines C und lässt sich mit TinyCC bauen (`--toolchain tcc`). Auch die 3D-Engine `scene3d` ist C99 (Branch `scene-c`:
derselbe Software-Renderer, bytegleiche Bilder wie vorher, dazu ein optionaler Hardware-Renderer über Vulkan, [3D scenes](graphics/3d.md) "Hardware"), und der Simulator
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

Stand: umgesetzt ([Processes:](stdlib/process.md)) - `Process.Run`/`Start`, `ProcessOptions` (Arbeitsordner, Umgebung, Umleitungen, `MergeError`), `ProcessStream` als
`IO.Stream`, `WaitForExit(timeout)`, `Kill`, `ExitCode`, `Id`, Zeilen als Ereignisse (`OnOutput`/`OnError`/`OnExit`, ausgeliefert von `Pump()`, ohne zu
blockieren). Die Natives sind - wie bei den anderen Standardpaketen - C99 (`fire_bridge_process.h`, `"language": "c"`; auch mit TinyCC), in der VM als Shared Library; POSIX mit `fork`/`execvp`
statt `posix_spawn` (der Arbeitsordner braucht sonst `posix_spawn_file_actions_addchdir_np`); Windows unter Wine geprüft.

## 6. SHA-256

Stand: **umgesetzt** ([Hash functions](stdlib/hash.md)) - Paket `hash`: MD5, SHA-1, SHA-256/384/512, CRC-32, Adler-32 und HMAC in reinem C (gegen die Testvektoren von
FIPS 180, RFC 1321/2202/4231 und gegen Python geprüft; VM, gcc und TinyCC gleich). Der Zustand eines Hashers ist ein Byte-Puffer des fire-Objekts (keine
Handle-Tabelle). Eine Anbindung an mbedTLS/OpenSSL gibt es nicht - die eigene Implementierung ist überall gleich und schnell genug.

Ursprünglicher Plan:

`Hash.Sha256(bytes | string | stream)` -> Bytes, `.ToHex()`; dazu `Crc32` (für Zip) und gleich `Sha1`/`Md5` für git und alte Formate.
Reines C ohne Abhängigkeiten (läuft auch auf dem ESP32); auf Zielen mit mbedTLS/OpenSSL optional deren Implementierung. Paket `hash`.

## 7. Zip / Deflate

Stand: **umgesetzt** ([Compression and Zip](stdlib/compression.md)) - Paket `compression`: Deflate/Inflate in eigenem C (streamfähig, gegen Python-zlib und mit 1500 kaputten
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

Stand: **Generator, Runtime und Agent umgesetzt** (Punkte 1, 3, 4, 5 für TCP, stdio und die UART des ESP32, 6, 7; [Natives Backend (AOT): fire -> C](spec/NATIVE_BACKEND.md) "Zeilen des Programms im C" und "Debug-Builds",
Protokoll: [NATIVE_DEBUG_PROTOCOL.md](spec/NATIVE_DEBUG_PROTOCOL.md)); getestet gegen die VM (`NativeDebugTests.cs`). spark-Seite (2, 8) ebenfalls umgesetzt (`IDebugTarget`, `NativeDebugTarget`, Engine-Box, natives Run/Debug; Tests `FIRE_TESTS_ONLY=debugger`).
**ESP32 umgesetzt** (8.6): Transport UART gerahmt (CRC-16), 3 s Wartezeit, Standardeingabe durch den Agenten, spark baut/flasht über `idf.py` und debuggt bzw. zeigt die Konsole
über die serielle Schnittstelle ([Natives Backend (AOT): fire -> C](spec/NATIVE_BACKEND.md) "Ziel `esp32`: Run und Debug auf dem Board"); getestet unter QEMU, nicht auf einem echten Board. Offen:
WLAN (TCP auf dem Board), andere Chips (USB-Serial-JTAG der C3/S3) als eigene Ziele, wasm. Die serielle Schnittstelle, die Baudrate und der Ordner von ESP-IDF sind in spark einzustellen (Projekteigenschaften, Toolchain Settings; nicht von Hand durchgeklickt). Nicht getestet: `set` eines Felds mit Probes (die gibt es nur mit Reflection; geprüft ist der Setter mit der Prüfung
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
   sprechen das Protokoll ([Debug-Protokoll nativer Programme](spec/NATIVE_DEBUG_PROTOCOL.md)); spark lauscht, übergibt `FIRE_DEBUG`, nimmt eine Verbindung mit dem Token an. Engine-Box *VM/Native* neben der
   Modus-Box (gespeichert in `settings.engine`), natives *Run* (Release/Performance: Ausgabe ins Output-Panel) und *Debug* (Build mit `-O0 -g` nach `bin/<ziel>-dbg`),
   Stack-Panel bei nativ ausgeblendet ([Natives Backend (AOT): fire -> C](spec/NATIVE_BACKEND.md) "Ausführen und Debuggen aus spark"). Getestet gegen einen Schein-Agenten; Ende-zu-Ende-Test gegen einen
   echten Debug-Build, sobald die Runtime den Agenten hat. ESP32 (8.6): Ziel `esp32` + Run/Debug baut das ESP-IDF-Projekt in `bin/esp32-<modus>`, `idf.py build flash`,
   öffnet die serielle Schnittstelle (`port`/`baud` aus Projekt oder Ziel, sonst erkannt) - Debug über `SerialFramedTransport`, Run mit der Konsole im Output-Panel
   und einer Eingabezeile darunter. Ein fire-Thread zeigt in der Scope-Ansicht seine eigenen Variablen der obersten Ebene (`globals` mit `thread`). Offen: die
   Oberfläche ist nicht von Hand durchgeklickt, kein echtes Board.
9. ~~**Build-Fenster**~~ (umgesetzt): jeder Bau aus spark (F5 mit der VM, nativ, ESP32, Build, Pack) zeigt nach 0,5 s ein Fenster mit Phase, Fortschritt (die Library-Module
   von linkage `static`/`shared` gezählt, sonst unbestimmt) und Log, *Hide* (Fortschritt in der Statusleiste) und *Cancel* (Prozessbaum der Compiler beendet, nichts
   Halbes bleibt) - `BuildMonitor` ([Natives Backend (AOT): fire -> C](spec/NATIVE_BACKEND.md) "Fortschritt und Abbrechen"). Offen: das Fenster ist nicht von Hand durchgeklickt; `idf.py` abbrechen nur
   ohne echtes ESP-IDF geprüft. Die Natives eines Packages, die für die VM gebaut werden, sind eine eigene Phase (*natives of packages*, je Paket gezählt) und abbrechbar.

Nicht in dieser Phase: Edit-and-Continue, Auswerten beliebiger Ausdrücke.

## 9. Binäre Libraries und dynamisches Laden

Zwei Teile: **getrennte Übersetzung** (Code teilen, schneller bauen) und **dynamisches Laden** (Plugins, Treiber, Natives in einer VM in fire).

Stand: **Phasen A0, A, B, B2 und C umgesetzt** (C ohne Windows) ([Natives Backend (AOT): fire -> C](spec/NATIVE_BACKEND.md) "Klassen-Deskriptoren" und "Getrennte Übersetzung: `linkage`"): Klassen-Deskriptoren statt
`switch` in den Hooks der Runtime (A0); Module mit eigener Registrierung der Zahlen über Tabellen des Programms, in einer Datei hinter `FIRE_MODULES=1` (A);
`"linkage": "static"` (Projekt, `fire.native.json`, `--linkage`) mit Schnittstellendatei (Formatversion + ABI-Hash), Cache des Benutzers nach Inhalts-Hash,
`ember cache clean`, Varianten je Menge von Schaltern, öffentliche Schnittstelle einer Library als Wurzel (§11), Debug-Tabellen je Modul (ein Haltepunkt in einer
statischen Library hält); Desktop (gcc, clang, TinyCC, MinGW), wasm (Emscripten, `emar`) und ESP32 (die Module als eigene Übersetzungseinheiten der Komponente) (B);
jedes Modul bedient seine eigenen Klassen (Verteiler, Feld-Helfer, `is of`, Operatoren, Typargumente), fremde über Tabellen der Member in den
Klassen-Deskriptoren (der Rückfall der Runtime, auch die Basis für Plugins), private Member einer Library nur, wenn sie erreicht werden, Reflection-Zeilen und
Reflect.New je Modul, Natives in C, die für sich stehen, und die JSON-Engine in der Bibliothek ihres Moduls, Sections + `--gc-sections` (B2: der zweite Bau mit
`static` ist schneller als `source`, siehe Messwerte).
`"linkage": "shared"` (C, Linux und macOS mit gcc/clang): die Libraries als Shared Libraries neben dem Programm, das den Zustand der Runtime exportiert; jede
nennt ihre Schnittstelle, das Programm prüft sie beim Start (eine lesbare Meldung statt eines Absturzes). Windows (MinGW, unter Wine getestet): die Runtime eine DLL
für sich, die thread-lokalen Variablen hinter `fire_tl()`, was eine DLL vom Programm braucht über Zeiger, die ihre `fire_link_*`-Funktion beim Start füllt.
Plugins (Phase D b, [Natives Backend (AOT): fire -> C](spec/NATIVE_BACKEND.md) "Plugins"): `--linkage plugin` baut die Klassen eines Programms als Modul für sich (`.so`/`.dll` und `.fireplugin` für die
VM), ein Programm mit `"shared"` lädt es mit `Library.Load` (Prüfung nur über Hashes: die Schnittstellen der Module, die Konfiguration der Runtime), die Runtime
registriert seine Klassen (Deskriptoren, Typen von `catch` und Enums nach Namen, Reflection, statische Initialisierer) und seine Einheiten (nach Dimensionen, Faktor
und Symbol, neue Dimensionen auf freien Plätzen); Natives in C++ des Plugins in seiner Bibliothek (eine eigene Kopie; für die VM eine Bibliothek mit der C-ABI neben
dem Bytecode), Natives in C von Packages über die `fire_cn_` des Programms (es legt sie für alle an; fehlt eines, eine lesbare Meldung); JSON der Objekte der
Plugins (ihre Zeilen `JsonClassInfo`, die Runtime fragt sie nach Id und Namen); `class extends` von Klassen der Libraries und von Basistypen im Plugin (Methoden und
Properties mit Rümpfen, keine Felder; die Runtime hängt ihre Tabellen an die Klassen, die VM die Methoden); das Programm ist dann eine offene Welt;
Linux, Windows (MinGW, unter Wine) und die VM (sie lädt den Bytecode, Entscheidung 9.8). Debuggen eines Plugins (Debug-Build von Programm und Plugin): seine
Quellen bekommen beim Laden die nächsten Indizes (Ereignis `sources` des Debug-Protokolls, die VM schreibt die Zeilentabellen um), Haltepunkte nach Datei, Step Into, Scopes.
Tests: `FIRE_TESTS_ONLY=linkage`. Offen: `"shared"` mit msvc (Exporte der Modul-DLLs, Tabellen des Programms, die auf Daten von DLLs zeigen - nicht zu testen), in Plugins
Felder in `class extends` fremder Klassen (der Platz in den Objekten des Programms), benannte Argumente im nativen Programm auf ihren Objekten (die Namen und Vorgaben der Parameter in den Tabellen der Member),
**die Größe** (vorerst bewusst zurückgestellt: schneller bauen geht vor): mit `static` ist ein Programm noch
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

* **umgesetzt** (Phase D a; [Native code](language/18-native-code.md)): `Library.Load(path)` / `library.Get(name, signature)` - `dlopen`/`LoadLibrary` zur Laufzeit, Funktionszeiger als aufrufbare Lambdas (ergänzt `extern`,
  das nur feste Namen kennt). `Get(..., release: "free")`: die Freigabefunktion der Bibliothek - ein zurückgegebener Zeiger wird ein `NativeHandle`, das dem Besitz folgt
  (`delete` oder das Ende des Besitzers gibt ihn einmal frei), ein zurückgegebener String wird kopiert und gleich freigegeben. `NativeHandle` ist `[NoCopy]` (SPEC 5.12,
  vererbt): `flat`/`copy` verdoppeln es nie (eine `ArgumentException`, auch als Feld oder Element einer tiefen Kopie). Ebenso die Kopien der Threads
  (VM und nativ gleich, vor jedem Kopieren): `fire taking x`/`fire M(args)` (der Thread startet nicht), die Objekt-Argumente von `fire global` (nativ wurde
  der Auftrag vorher trotzdem eingereiht), `sync`/`sync flat` (Fall A, beim vollen `sync` auch Fall C; nichts wird zurückgeschrieben). Kein `taking` verschiebt
  ein Objekt; **umgesetzt**: `fire handover x` übergibt es ohne Kopie (alles Erreichbare, die Variable wird `undefined`, VM und nativ gleich, THREADING_DESIGN.md
  Abschnitt 1). `taking` kopiert alles Erreichbare, egal wem es gehört (auch ein Array-Literal in einem Feld; ein Array auch in der VM), beim `fire` im
  startenden Thread; Lambdas/Pointer darin sind eine `ArgumentException`. **Umgesetzt:** wann `fire global`-Aufträge (und Sektionen, Host-Callbacks)
  automatisch laufen, ist eine Build-Einstellung (`#globaljobs safepoints|sleep|sync`, `#nosync` = `sync`, Projekt `globalJobs`, fire.native.json, `--global-jobs`)
  mit EINER Liste sicherer Punkte für VM und nativ (Rücksprung einer Schleife, `Sleep`/Wartefunktionen, `sync globals`, Programmende; THREADING_DESIGN.md
  Abschnitt 7) - der Rückgabewert von `sync globals` ist in beiden gleich. **Umgesetzt:** andere Verweise auf übergebene Objekte (von vor dem `fire handover`, in jedem Thread) sind tot - jede Benutzung ist die
  `DestroyedException` "... was handed over to a thread ('fire handover')." (VM und nativ gleich, ohne neue Prüfung: die Teile bekommen eine neue
  Identität, nativ die nächste Slot-Generation; `==` vergleicht das Objekt; THREADING_DESIGN.md Abschnitt 1).
  **Umgesetzt:** die isolierte Kopie der Globals für einen Host-Callback, wenn das Hauptprogramm nicht mehr läuft (VM; nativ läuft dann keiner mehr),
  folgt den `[NoCopy]`-Regeln (vor jedem Kopieren abgelehnt, der Callback läuft nicht, der Host bekommt die `ArgumentException` als Text). `is of`/`!`/`throw` eines zerstörten Objekts
  sind in VM und nativ eine `DestroyedException` (nativ las `isof_*` vorher den freigegebenen Speicher).
* **umgesetzt** (Phase D b): fire-Module als Plugins (`--linkage plugin`, `Library.Load`): ein Programm mit `"shared"` (und die VM) lädt sie zur Laufzeit, registriert
  ihre Klassen; `Reflect.New("Kit.MyDriver")` findet sie; Prüfung der Schnittstelle beim Laden (Hash), eine verständliche Fehlermeldung statt eines Absturzes.
  spark lädt so die Treiber installierter Packages (der Treiber-Agent: das Prelude jedes Packages mit Treibern einmal als Plugin für die VM übersetzt und im Cache,
  `Library.Load` + `Reflect.New`; passt ein Plugin nicht, der alte Weg über `#import`; [Devices](devices/README.md), Test `FIRE_TESTS_ONLY=drivers`).

## 10. Clipboard, Drag & Drop

Stand: **umgesetzt** ([fire.Terminal – Framebuffer, Zeichnen und Fenster](spec/GRAPHICS_LAYER.md) "Drag & Drop aus anderen Programmen, Zwischenablage", [User interfaces](ui/README.md) "Textfeld", "Mehrzeiliges Textfeld" und "Drag & Drop", [Natives Backend (AOT): fire -> C](spec/NATIVE_BACKEND.md) "Die eigene Anzeige"):
`Clipboard.GetText`/`SetText`/`HasText`, `GetImage`/`SetImage`/`HasImage` (PNG über `Framebuffer.ToPng`, unter Windows auch `CF_DIB`) und `GetFiles`/`SetFiles`/`HasFiles` (`text/uri-list`, unter
Windows `CF_HDROP`, nativ und in der VM) - VM und nativ über fire's eigene Anzeige, ohne Desktop eine eigene Zwischenablage des Programms, unter X11 auch mit dem, was vor dem Start kopiert wurde; die Ereignisse `DropFiles`/`DropText` mit Position
und `DragHover`/`DragLeave`, solange etwas aus einem anderen Programm über dem Fenster schwebt; im `ui` die Auswahl im Textfeld mit Kopieren/Ausschneiden/Einfügen, Doppelklick markiert ein Wort,
Dreifachklick alles bzw. die Zeile (`root.clickCount`), das mehrzeilige Textfeld `UI.TextArea`, Drag & Drop zwischen Elementen (`OnDragStart`, `AllowDrop`, `OnDragOver`, `OnDrop`, `OnDragLeave`,
`OnDragDone`, `UI.DropEffect`) mit einem Bild am Zeiger (`DragData.ShowImage`: `DragData.Image`, ein durchscheinender Schnappschuss des Quellelements oder ein Schild mit dem Text), Drops aus
anderen Programmen an das Element darunter und ihr Schweben (`UI.DragData.Pending`); Ziehen aus dem Fenster heraus in andere Programme (`Window.StartDrag`: Text und Dateien, die Antwort
Kopie/Verschieben) unter Windows (OLE: `IDataObject` mit `CF_UNICODETEXT`/`CF_HDROP`) und X11 (XDND), in der VM und nativ, unter Wayland (`wl_data_device.start_drag`) nativ - das `ui` übergibt ein Ziehen mit Text oder Dateien,
sobald der Zeiger das Fenster verlässt (`UI.DragData.AllowExternal`); wo die Plattform das nicht kann (`Window.CanStartDrag()`), bleibt das Ziehen im Fenster. Rückgängig/Wiederholen in `TextBox`
und `TextArea`, Ziehen der Markierung (verschieben, mit Ctrl kopieren, in andere Felder und Programme), Doppelklick in `ListBox`/`ListView`/`TreeView` über `root.clickCount` (`OnActivate`).

**Offen:**
- Ziehen aus dem Fenster heraus unter macOS (`NSDraggingSession`) und in der VM unter Wayland; dort bleibt ein Ziehen im `ui`
  im Fenster (`Window.CanStartDrag()` sagt `false`).
- Wayland-Ziehen nur mit weston (X11-Backend unter Xvfb) getestet, nicht mit GNOME/KDE.
- Das Bild am Zeiger gibt es nur im Fenster: draußen zeigt der Desktop sein eigenes Zeichen (ein Bild für OLE/XDND fehlt).
- Drag-out in der VM unter echtem Windows prüfen (OLE-Pfad nur unter Wine nativ getestet; die VM läuft unter Wine als `win-x64`-Build - die
  Zwischenablage mit `CF_HDROP` wird so geprüft -, ein Ziehen mit der "Hand" dort fehlt noch).

Ursprünglicher Plan:

* `Clipboard.GetText`/`SetText`, `GetImage`/`SetImage` (`graphics.Image`), `GetFiles`; im `ui`: Kopieren/Einfügen in `TextBox` und eigenen Elementen.
* Drag & Drop: Dateien aus dem Betriebssystem in ein Fenster (`windows`: Ereignis `Drop`), innerhalb des `ui` zwischen Elementen (`DragStart`,
  `DragOver`, `Drop` mit Daten und Effekt); Drag aus dem Programm heraus später (jetzt: Windows und X11).
* Nativ über die Anzeige der Plattform (Clipboard, Drop-Ereignisse), in der VM über die C#-Natives von `windows`/`ui`.

## 11. Ungenutzten Code weglassen

**Umgesetzt (Phasen A-C, Stand und Messung: [Natives Backend (AOT): fire -> C](spec/NATIVE_BACKEND.md) "Größe des Programms").** Anlass war, dass ein minimales Programm mit `#import "ui"` (ein Fenster, ein `UI.Root`,
ein `UI.Label`) 7,85 MB C ergab und `gcc -O2` dafür über zwei Minuten brauchte. Die Ursache: `ui` benutzt die Reflection (Stile, Trigger, Bindungen setzen Properties per Name), und ein
einziger Aufruf eines `__refl_*` erzeugte **jede** Klasse des Programms mit allen Konstruktoren und Methoden. Jetzt:

* **Reflection nach Stufen** (A): Mitglieder per Name halten die Felder und Property-Zugriffe der gemachten Klassen fest, `Reflect.Call` deren Instanzmethoden, `Reflect.New` die Konstruktoren
  der Klassen in den Tabellen; die Tabellen beschreiben die erreichten Klassen (gemacht, benutzt, Basisklassen, `[Keep]`).
* **JSON** (B): Schreiben zieht nichts nach, Lesen die deklarierten Typen mit Unterklassen und Feldtypen, ein Slot ohne Klasse alle. `[NotSerializable]` (vererbt) und `"serializable": false`
  im Package (`ui`) schließen Klassen aus: Schreiben oder Lesen einer solchen ist eine `ArgumentException`, in VM und nativ gleich.
* **Tabellen** (C): gleiche Zweige der Feldhelfer einmal, `fp_` nur bei genommener Adresse, Reflection- und Proben-Tabellen ohne die geerbten Mitglieder jeder Klasse, Namenslisten einmal.
* **`[Keep]`** vor Klassen und Mitgliedern (nicht vererbt; SPEC 5.12 erlaubt Attribute jetzt auch vor Klassen ausdrücklich).
* **Stufen des Weglassens** (`"stripping"` in `fire.native.json` und in den Einstellungen des Projekts, `--stripping` auf der Befehlszeile; Zahl oder Name; ersetzt `"trim"`,
  das als Alias bleibt: `"all"` = 0, `"reflection"` = 1, `"reached"` = 3, `--keep-all` = 0): **0** `none` (alles, wie die VM; ein Debug-Build immer), **1** `protected` (jede Klasse
  und jeder öffentliche Member; ein privater oder geschützter nur, wenn erreicht - die Regel einer vorgebauten Library), **2** `methods` (jede Klasse mit Konstruktoren, Feldern und
  Properties; eine Methode nur, wenn benutzt - mit ihrer Beschreibung in den Reflection-Tabellen), **3** `all` (der Standard: nur das Erreichte). In jeder Stufe bleiben
  **Namen, die das Programm als Literal an die Reflection gibt** (`Reflect.New("Dog")`, `Reflect.Call(o, "Bark")`, `Type.Named("X")`, `Reflect.Get(o, "f")` - jeder Text der
  Funktion, die die Reflection ruft), und ein Zugriff auf etwas Weggelassenes ist ein klarer Fehler: „Class 'X' was left out of the build (stripping 3). Add [Keep] to 'X' or use
  stripping <= 2.“ (ebenso für Member). Proben funktionieren in den Stufen 2 und 3 (sie halten die Felder und Properties der gemachten Klassen). Mit `"static"`/`"shared"`
  behält eine Library in jeder Stufe außer 0 ihre Schnittstelle (Stufe 1); das Programm lässt weg wie mit `"source"`.
* Statische Initialisierer aller Klassen laufen weiter (Semantik unverändert).

Ergebnis: das minimale `ui`-Programm hat jetzt 2,29 MB C statt 7,85 MB (-71 %), `gcc -O2` braucht 49 s statt 140 s (-65 %); Messung für `graphics`, `scene3d`, JSON und Reflection in NATIVE_BACKEND.md.

**VM und nativ:** Die VM behält alles. Im Standard (Stufe 3) unterscheiden sich nur Suchen mit einem Namen, der erst zur Laufzeit entsteht, nach einer Klasse, die das Programm
nie erreicht, oder einer Methode, die es nie benutzt (`Reflect.New(name)`, `Type.Named(name)`, `Reflect.Call(o, name)`; `Type.Names()` und `Type.Of(o).Methods()` listen, was da
ist; JSON-`"$type"` außerhalb der deklarierten Typen): nativ der klare Fehler, in der VM gefunden. `[Keep]` oder eine kleinere Stufe machen beides gleich; getestet in allen
vier Stufen gegen die VM ("Weglassen (stripping)" in der nativen Gruppe, mit `static` in `FIRE_TESTS_ONLY=linkage`).

**Offen:**

* **D - Rapid Type Analysis für den Verteiler** (eine Methode nur, wenn eine Aufrufstelle mit passendem statischen Typ sie erreicht): gemessen, nicht gebaut - im `ui`-Programm kennen 81 %
  der Aufrufstellen über den Verteiler die Klasse schon, der Filter spart höchstens 4 % des Funktionscodes.
* **E - Natives der Bridges in `#if`-Abschnitten** (`graphics`, `windows`, Zwischenablage, Drag & Drop, Bilder, Slicer): nicht gebaut; gcc verwirft nicht benutzte `static`-Funktionen schon,
  der Gewinn läge bei TinyCC und der Größe der Datei.
* **F - Weglassen in der VM** (kleinere Images, schnellerer Start, dann auch dieselben Klassen per Name wie nativ): offen.
* ~~Mit Abschnitt 9 abstimmen~~: eine Library, die als `"static"`/`"shared"` vorgebaut wird, behält ihre Schnittstelle (öffentliche und geschützte Member, Stufe 1) - umgesetzt
  mit B2 und den Stufen. Offen: eine Erreichbarkeit über die Modulgrenzen, damit auch eine vorgebaute Library weglässt, was das Programm nicht braucht (Abschnitt 9).
* Die Stufen 1 und 2 sind für ein `ui`-Programm fast so groß wie 0: jeder öffentliche Member bzw. jeder Konstruktor jeder Klasse des Packages zieht seinen Code nach.
* Die Erkennung der Literale ist grob (jeder Text der Funktion, die die Reflection ruft); ein Name, der über eine Variable einer anderen Funktion oder aus einer Datei
  kommt, braucht `[Keep]` oder eine kleinere Stufe.
* Was bleibt, ist beim `ui`-Programm echter Code (`UI.Root`: Ereignisse, Fokus, Popups, Ziehen und Ablegen); kleiner würde es nur durch weniger Code im Package oder ein `ui`, dessen
  Teile (Drag & Drop, Popups) erst beim Benutzen erreicht werden.

## 12. Bau-Reihenfolge der Quellen von Paket-Natives

**Problem.** Die Quellen der Natives eines Pakets (`native.sources` im `.fireproj`, davor `native.platformSources`) werden für die VM-Bibliothek und den nativen Build
**in eine Datei** gelegt, genau in der Reihenfolge der Liste (`PackageStore.ReadNativeSources`). Ein `#include "x.h"` auf eine andere Quelle desselben Pakets wird dabei
entfernt (`StripLocalIncludes`: "in this file already") - er wirkt also nicht. Was ein Header benutzt, muss in der Liste **vor** ihm stehen; sonst fehlen Typen und
Funktionen, obwohl der Header sie selbst einbindet. Gemerkt wird das erst beim Bauen, und in der VM fiel die Grafik dann still auf C# zurück (inzwischen meldet sie es,
`GraphicsLibrary.WarnFallback`).

**Wie es aufgetreten ist (Teil 4, GPU-Backend von `graphics`).** `fire_gfx_gpu.h` bindet `fire_gfx_vk.h` (der erzeugte Ausschnitt der Vulkan-Header) und
`fire_gfx_shaders.h` (die SPIR-V-Shader) ein. In `packages/graphics/graphics.fireproj` stand `fire_gfx_gpu.h` zuerst in der Liste; der Bau scheiterte mit
`unknown type name 'VkBuffer'`. Getauscht: die beiden Header, die es braucht, stehen jetzt davor -

```
"native/fire_gfx_fx.h",
"native/fire_gfx_vk.h",        (vorher nach fire_gfx_gpu.h)
"native/fire_gfx_shaders.h",   (vorher nach fire_gfx_gpu.h)
"native/fire_gfx_gpu.h",
"native/fire_bridge_graphics.h"
```

Innerhalb einer Datei gilt ohnehin C: definiert vor benutzt (der GPU-Helfer `fire_gfxb_gpuShape` steht in `fire_bridge_graphics.h` vor den Natives, die ihn rufen).

**Was es braucht (zum Vereinheitlichen).**
* Eine **feste Bau-Reihenfolge**, die das Paket ausdrücklich angibt statt sie aus der Lage in der Liste zu folgern - z. B. ein Index je Quelle
  (`{ "file": "native/fire_gfx_vk.h", "order": 10 }`) oder Gruppen (`"headers"` vor `"sources"`), gleich für `sources` und `platformSources`.
* Alternativ (oder zusätzlich) die lokalen `#include`s **auflösen** statt sie zu streichen: eine Quelle wird an der Stelle ihres ersten `#include` eingesetzt (einmal je
  Datei); dann ergibt sich die Reihenfolge aus den Abhängigkeiten selbst, und eine falsche Liste ist kein Fehler mehr.
* Eine **Prüfung beim Packen** (`ember pack`, Paket-Tests): jede Quelle, die eine andere des Pakets einbindet, steht nach ihr - mit einer Meldung, die Datei und Zeile nennt.
* Dieselben Regeln für alle Wege, die Quellen einsammeln: die VM-Bibliotheken (`PackageLibrary`, auch `EnsureGraphics`), der native Build (`CppGenerator`) und Plugins.

**Zweiter Fall: dieselben Deklarationen in zwei Paketen.** `graphics` (GPU-Backend) und `scene3d` brauchen beide einen Ausschnitt der Vulkan-Header. Im nativen Build liegen die
Quellen beider Pakete in einer Datei - zwei verschiedene Ausschnitte derselben Enums kollidierten (`redeclaration of 'enum VkResult'`). Gelöst ist es vorerst so: **ein**
Generator (`packages/graphics/native/tools/make_gfx_vk.py`; der von scene3d ruft ihn nur auf) liest beide Backends und schreibt dieselbe Datei als
`graphics/native/fire_gfx_vk.h` und `scene3d/native/s3d_vk.h`, unter einem gemeinsamen Include-Guard (`FIRE_VK_DECLS_H`); beide Backends binden sie nur ein, wenn der Guard
noch nicht gesetzt ist. In der VM baut jedes Paket seine Bibliothek allein und hat seine vollständige Kopie. Sauberer wäre, wenn ein Paket Header eines Pakets, von dem es
abhängt, **einbinden** könnte (scene3d nimmt den von graphics) - das gehört zur festen Bau-Reihenfolge oben: die Quellen der Abhängigkeiten zuerst, jede einmal.


**Umgesetzt (Branch `native-c`).**
* **Feste Bau-Reihenfolge je Quelle**: ein Eintrag von `sources`/`platformSources` (Paket und `.fireproj`) ist ein Pfad oder `{ "file": "native/x.h", "order": 10 }`
  (`NativeSourceList`, `src/fire.Package.Manager/NativeSources.cs`; im Projekt gilt die Zahl für jede Datei eines Musters). Alle Quellen eines Baus - erst die Plattformquellen, dann die
  gemeinsamen - werden **stabil** nach `order` sortiert (ohne Angabe 0: einfache Einträge behalten ihre Lage wie bisher). Eine einzige Stelle (`PackageNativeExtensions.SourcesFor`, gelesen von
  `InstalledImport.ReadNativeSources`) bedient alle Wege: die VM-Bibliotheken (`PackageLibrary`, der Fingerabdruck in `StandardSources`; `EnsureGraphics` von `ui-platform` liest dieselbe Methode), den nativen Build
  (`CppGenerator`), Plugins und die Bibliotheksmodule (`static`/`shared`, `StaticLinkage`). `ember pack`/Pack Library schreiben die Zahlen ins `package.json` des Pakets.
* **Prüfung statt Auflösung**: eine Quelle, die eine spätere Quelle desselben Imports einbindet, ist ein Fehler mit Datei und Zeile -
  `native/a.h:3: #include "b.h" is a source of the same package that is built after it - give 'native/b.h' a smaller build order ("order") than 'native/a.h', or list it before.` -
  beim Bauen (`ReadNativeSources`: VM, nativ, Plugins) und beim Packen (`Fpk.Forge`, für jeden Schlüssel von `platformSources`). Die lokalen `#include`s werden bewusst **nicht**
  automatisch aufgelöst: die Reihenfolge steht ausdrücklich da und ist im Editor sichtbar. Keines der Pakete von `native-c` und `ui-platform` verletzt die Regel.
* Die VM-Bibliothek eines Projekts wird neu gebaut, wenn die Projektdatei neuer ist (`InstalledPackage.DescriptionFile`) - eine neue Reihenfolge ändert keine Quelle.
* **Editor**: das Werkzeugfenster *Properties* zeigt die Eigenschaften der im Projektmappen-Explorer gewählten Datei (`FilePropertiesHost` mit `IFilePropertiesProvider`); eine C/C++-Datei
  eines Projekts hat *Part of the native build*, *Build order*, *Platforms*, *Position* und *Listed as*, geändert wird die `.fireproj` (`ProjectNativeFiles`).
* Tests: `FIRE_TESTS_ONLY=projects` (Lesen/Schreiben, Sortierung, Meldung, Rundreise, eine C-Bibliothek mit voneinander abhängigen Headern in der VM und nativ, Packen, die Dateioptionen).
* Offen: Header eines Pakets, von dem man abhängt, einbinden (scene3d nimmt den Vulkan-Ausschnitt von graphics) - die Quellen der Abhängigkeiten zuerst, jede einmal.

## 13. Kleine und alte Ziele

Ziel: fire auf Mikrocontrollern ab 32 KB RAM / 128 KB Flash (Cortex-M, ESP32, RISC-V, PIC32), auf DOS (Real Mode und DPMI), Win 3.x, Win9x und NT 3.51 bis XP,
und ohne Betriebssystem auf dem PC (BIOS, UEFI).
Grundlage ist die Analyse auf `native-c` (Größen gemessen mit arm-none-eabi-gcc und avr-gcc): ab 32 Bit sinnvoll, 8-Bit-PIC nie (Hardware-Stack, kein
Daten-Stack bei XC8), AVR nur mit einem eigenen kleinen Profil.

**Umgesetzt** (Branch `pointer-kinds`):

* Drei Zeigerarten (`K_PtrVar`, `K_PtrByte`, `K_PtrElem`) statt `K_Pointer` + `width`; das Byte ist frei.
* Typisierte Arrays (SPEC 8.4.2): `int`, `int[N]`, `float`, `float[N]`, `bool`, `char`, `byte` als Elementtyp - strikt (falscher Typ: `ArgumentException`,
  andere Einheit: `UnitMismatchException`), `undefined` erlaubt; nativ gepackt (eine C-Zahl pro Element, `undefined` als Merker, Bitmaske erst bei Bedarf).
* `int x[4]` legt das Array überall gleich an (Local, Global, Feld mit dem Objekt); `int x[]` bleibt `undefined`.
* `#intwidth 32` (SPEC 8.2.2; `-i`, `intWidth` in Projekt und Ziel, `#if int32`); Standard bleibt 64 Bit, auch auf dem ESP32. Ein Wert hat überall 16 Byte (auch `int[64]`, ohne Zeiger-Kästen);
  mit `#intwidth 32`/`#floatwidth 32` teilen sich zwei Felder `int`/`float`/`bool` einen Wert (schmale Felder, NATIVE_BACKEND.md).

**Reihenfolge des Rests** (nach dem Abschluss von Abschnitt 9 auf `native-c`):

1. ~~**Kompaktes Format ohne Grenze bei den Objekten.**~~ **Umgesetzt** (Branch `targets`; NATIVE_BACKEND "Breite von `int`"). Vorher: 16 Bit Slot, 12 Bit Generation (65535 lebende Objekte/Arrays/Puffer). Neu: ein Objekt, Array oder
   Puffer trägt Slot (32 Bit, Nutzlast) und Generation (28 Bit, Kopf); der Zeiger steht in der Slot-Tabelle (Zeiger und Generation nebeneinander: eine Ladung).
   Zeiger in Arrays und Puffer (`ref a[i]`, `p + n`) zeigen auf einen **Kasten** `{Adresse, Slot, Generation, Zähler}` (16 Byte pro lebendem Zeiger, nicht pro
   Array oder Element), gezählt wie Strings, wiederverwendet über eine Freiliste. Mit `#performance` (keine Tabelle) tragen alle direkt ihren Zeiger. Bleibt: 65535 Einheiten.
   Später, wenn eine Messung es zeigt: einen Kasten mit Zähler 1 bei `p + 1` weiterschieben statt einen neuen zu holen.
1b. **Köpfe der Werte getrennt und schmal speichern** (Vorschlag des Benutzers, nach Punkt 1). Ein Wert besteht aus Kopf (Art, Einheit bzw. Enum, Generation)
   und Nutzlast. Auf 64 Bit sind das heute 8 + 8 Byte, obwohl der Kopf mit 32 Bit (oder weniger) auskäme - die andere Hälfte ist Füllung für die Ausrichtung.
   * **Idee:** wo Werte im Speicher hintereinander stehen (Elemente eines Arrays ohne Elementtyp, Felder eines Objekts, Globals, `List`/`Dictionary`), stehen
     erst X Köpfe und dann X Nutzlasten, X so gewählt, dass die Gruppe auf die Ausrichtung aufgeht: 64 Bit mit 4-Byte-Köpfen in Zweiergruppen (8 + 16 Byte)
     = 12 statt 16 Byte pro Wert (-25 %), mit 2-Byte-Köpfen in Vierergruppen (8 + 32) = 10 Byte; das kompakte 32-Bit-Format mit 2-Byte-Köpfen = 6 statt 8 Byte.
     Die Breite des Kopfes richtet sich nach der Wortbreite (16-Bit-Ziele heute nach der Breite von `int`). Keine echte Kompression - nur die Füllung fällt weg.
   * **Voraussetzung:** die Generation wandert aus dem Kopf in die Nutzlast (Punkt 1: Slot und Generation der Objekte; auf x86-64/ARM64 hätten auch die
     oberen 16 Bit eines Zeigers Platz), dann bleiben im Kopf nur Art (4 Bit), ein paar Merker und Einheit/Enum (16 Bit).
   * **Gilt nur im Speicher:** ein Wert in einer C-Variablen, als Argument oder Rückgabe bleibt das `Value`-Struct (Register); Lesen/Schreiben eines Elements
     oder Feldes sind zwei Zugriffe, in der Gruppe meist in derselben Cache-Zeile.
   * **Zu klären:** Zeiger auf Elemente und Felder (`ref a[i]`, `K_PtrElem`) zeigen dann auf zwei Stellen (der Kasten aus Punkt 1 hilft); Natives, die heute
     `Value*` eines Arrays sehen (`arrItems`), brauchen Zugriffsfunktionen; der Debugger und JSON lesen über dieselben Funktionen; Messung vorher (ein `ui`-Programm,
     ein Programm mit vielen Objekten) - typisierte Arrays und Puffer sind schon ohne Köpfe gepackt, der Gewinn liegt bei Objekten und gemischten Arrays.
   * **Gemessen** (Branch `targets`, 218 native Testprogramme, 64 Bit, alle Allokationen gezählt): Heap 50,9 MB, davon Strings 25,7 MB, Felder der Objekte 4,6 MB,
     Elemente gemischter Arrays 8,0 MB (zusammen 25 %), **Köpfe der Objekte 9,6 MB** (108.759 Objekte zu je 88 Byte: `Owned` 32, `cls`/`nfields` 8, die eingebettete
     `OwnList` 40, `targs` 8). Die getrennten Köpfe sparten 6 % (12/16) bis 9 % (10/16) des Heaps, bei Programmen über 100 KB im Median 37 % Wertanteil, also 9-14 %.
     Ein schmalerer Objektkopf (die `OwnList` eines Objekts braucht nur `head`/`tail`; `mark`, `depth`, `parent` gehören den Scopes, `holder` ist das Objekt selbst;
     `targs` nur bei generischen Klassen) spart 32-40 Byte pro Objekt = 7-8 % des Heaps - bei viel kleinerem Umbau. Vorschlag: erst den Objektkopf, dann neu messen.
   * **Umgesetzt: schmaler Objektkopf** (Branch `targets`, Entscheidung des Benutzers: erst der Kopf, dann neu messen). Die Besitzliste eines Objekts entsteht erst,
     wenn es etwas besitzt (`objOwned`; `objOwns`/`objOwnedHead` lesen): der Kopf hat 56 statt 88 Byte. `moveReachable` legt die Liste erst an, wenn wirklich etwas
     umzieht. **Neu gemessen:** nur 3,6 % der Objekte bekommen eine Liste; die Köpfe der Objekte kosten 3,87 statt 5,93 MB - **7,9 % weniger Heap**. Die getrennten
     Köpfe brächten auf demselben Stand weitere 8,6 % (12/16) bis 12,9 % (10/16). **Entscheidung des Benutzers:** die getrennten Köpfe höchstens später.
1c. **Rohdaten ohne Einheit: `byte` und `word`** (Entscheidung des Benutzers). `byte` und `word` sind Datentypen für rohe Daten **ohne Einheit**; Einheiten
   bleiben bei `int`, `int[N]`, `float` und `float[N]` (dort ändert sich nichts). Ab hier unterscheiden sich die beiden Formate.
   * **Umgesetzt:** Byte-Argumente der Natives sicher - lesende Natives nehmen einen Puffer oder ein Array aus Zahlen 0..255 (auch ein typisiertes; kopiert, bis der
     aufrufende Scope endet), füllende verlangen einen Puffer, alles andere ist eine `ArgumentException` statt eines Absturzes (VM und nativ: `bufOf` prüfte die Art
     nicht) - `bytesArg`/`bufferArg` in fire_rt.h (docs/packages/natives.md), alle Bridges umgestellt. `byte x[] = [...]` und `byte x[n]` (auch Felder, statische
     Felder, Globals; mehrdimensional: die inneren) deklarieren einen **Puffer** (SPEC 8.4.2): ein zugewiesenes Array wird ein neuer Puffer (int auf 8 Bit
     gekürzt, `undefined` 0); ein Pufferelement ist nie `undefined`, ein Nicht-int dort ist eine `ArgumentException` (vorher ein Laufzeitfehler).
     Ein Objekt als Argument einer Native ist in der VM wie nativ eine `ArgumentException` (die Grenze der Paket-Bibliothek, PackageNativeBinding; der Text
     nennt die Native statt der Funktion des Preludes) - meist scheitert der Prelude-Code vorher (`data.length`).
     (Behoben: die Bibliothek eines Pakets für die VM wird jetzt auch neu gebaut, wenn sich nur die Runtime ändert - `PackageLibrary.NewestSource` zählt den Stand von fire.Native mit.)
   * **Umgesetzt: `byte` ohne Einheit** - eine Einheit an einem `byte` (ein Wert mit Einheit, `byte b : m`, Feld, Parameter, Array) ist `TC017` des Typprüfers (auch lax ein
     Fehler, docs/tools/errors.md); ein gespeicherter Wert verliert seine Einheit (auch `x!byte`), VM und nativ. In Packages, Beispielen und Vorlagen gab es keinen `byte` mit Einheit.
   * **Umgesetzt: `word`** = das Maschinenwort des **Ziels**, vorzeichenlos, ohne Einheit (SPEC 8.10a): eine eigene Art von Wert (`ValueKind.Word`/`K_Word`, die Bits
     oberhalb der Breite 0) - die int-Schnellwege bleiben unberührt, ein Wort nimmt immer den langsamen Weg der allgemeinen Operationen (Value.WordOp, fire_rt.h `wordOp`).
     `TargetProfile.WordWidth` (64 auf windows/linux/macos, 32 auf esp32/freertos/wasm), `wordWidth` (16/32/64) eines Ziels in fire.native.json, `#if word16|word32|word64`,
     `Word.Bits`, `FIRE_WORD_BITS`; die VM folgt dem Ziel des Baus (`Value.WordBits`, `forge run`: 64). Mit 64 Bit **echt vorzeichenlos** (Ausgabe, Vergleich, Division,
     Modulo, `>>`, nach float). Regel: ein Wort auf einer Seite macht eine Wort-Operation (das int als Wort, Ergebnis abgeschnitten), ein float eine float-Operation; `x!word`,
     `w!int` (dieselben Bits), `w!float`; `word w = i` wandelt, `int i = w` will `w!int` (TC001). Dezimal-Literale bis 2^64-1. `$"{w:X}"`, JSON, `is of word`,
     Dictionary-Schlüssel, die C-ABI der VM (`FIRE_WORD`).
   * **Umgesetzt: `word a[] = [...]`, `word a[n]`, `new word[n]`** sind Puffer aus Maschinenwörtern (`ByteBuffer.ElementSize`/`Buf.elem`, Byte-Reihenfolge der Maschine; die
     Größe in Bytes hängt vom Ziel ab, dokumentiert): Index, `length` in Wörtern, `foreach`, `copy`, `++`, JSON als Zahlen-Array; `bytesArg`/`bufferArg` lehnen einen
     Wortpuffer ab, `wordsArg` nimmt Wörter (ein Wortpuffer ohne Kopie, ein Array kopiert; C-ABI `FIRE_WORDS`). Ein Test-Paket prüft es in VM und nativ.
   * **Umgesetzt (zum Teil): schmal und schnell** - der Generator kennt Wörter und Puffer, die er selbst gemacht hat (`KindWord`, `KindBuf`): Wort-Arithmetik und
     -Vergleiche, die Speicherung in `byte`/`word`, Elemente und `length` bekannter Puffer und `& | # << >>` zweier ints stehen als C da. Prüfsummen-Schleife über einen
     Bytepuffer in einer Methode (2 x 200 x 1.000.000, gcc -O2): 4,45 s -> 0,41 s.
     Die Puffer-Methoden für Bytes (`ToString()`, `ToUnicode()`, `ToUnicodeChar()`) sehen bei einem Wortpuffer alle seine Bytes; `ToLittleEndian()`/`ToBigEndian()`
     geben einen Bytepuffer, jedes Wort in dieser Reihenfolge (VM und nativ gleich).
     Ein Wortpuffer der VM mit 16/32-Bit-Wörtern (ein Bau der VM für ein fremdes Ziel) geht an eine Paket-Bibliothek als Kopie in 64-Bit-Wörtern und kommt
     auf seine Breite gekürzt zurück.
     Überladung nach `word` (`f(int)`/`f(word)`/`f(float)`): ein Wort passt genau, ein int besser in `word` als in `float`, ein float nie (Typprüfer, VM, nativ).
     **Offen:** echte `uint8_t`/`uintN_t`-C-Variablen für `byte`/`word`-Locals und -Parameter (die Locals bleiben 16-Byte-Werte); ein Puffer als Parameter oder aus einem
     Feld ist dem Generator nicht bekannt (allgemeiner Zugriff);
     16 Bit über ein Ziel mit
     `wordWidth` 16 getestet (VM und nativ auf dem Rechner, kein echtes 16-Bit-Ziel); ein Wort an einen `int`-Parameter (auch der Methoden eines Preludes) will `w!int` (TC001, so gewollt).
     (Umgesetzt: ein Wort an `extern`-Funktionen und `Library.Get` geht als seine Bits, VM und nativ.)
   * PCM und andere vorzeichenbehaftete Daten bleiben bei `int[16]`-Arrays (gepackt, mit Einheit); kein weiterer Typ.
2. ~~**Inline-C**~~ **Umgesetzt** (Branch `targets`; SPEC 8.1.1a, docs/language/18-native-code.md) - eine Funktion mit C-Rumpf, der unverändert an den Compiler geht (auch Assembler im Dialekt des Compilers, `#if watcom` / `#if gcc`):

   ```fire
   inline c int Add(int a, int b) { return a + b; }
   inline c undefined Beep(int hz) { _asm { mov al, 0B6h
                                           out 43h, al } }
   ```

   Parameter und Ergebnis sind C-Typen (`int` -> `FireInt`, `float` -> `Real`, `byte[]` -> die Bytes, `any` -> `Value`); der Generator baut den Wrapper wie bei
   den Natives der Packages. In der VM ist ein Aufruf ein Fehler (oder eine Alternative mit `#if native`); der Editor hebt den Rumpf als C hervor.
2b. **Eigener Scheduler für Ziele ohne Threads** (Entscheidung des Benutzers, direkt nach Inline-C): fire-Threads auch dort, wo das System keine hat (DOS,
   BIOS, UEFI, Win16, kleine Boards ohne RTOS). Eine Plattformschicht `std/fire_sched.h` setzt die Schnittstelle der Threads (Mutex, Bedingungsvariable,
   Thread starten/warten, ein Zeiger je Thread) mit eigenen "grünen" Threads um; der Wechsel des Kontexts ist je Architektur eine kleine Datei (`ucontext` auf
   POSIX zum Testen am Rechner, 16- und 32-Bit-x86 für Watcom). Gewechselt wird nur an den Safepoints der Runtime (der GIL gibt ohnehin alle 4096 ab) und
   beim Warten (Mutex, Bedingung, `Sleep`, `join`); ein Timer-IRQ (DOS/BIOS: `INT 08h`/`1Ch`) setzt nur ein Flag, das den nächsten Safepoint früher abgeben lässt -
   im IRQ selbst wird nie gewechselt (DOS und die C-Bibliothek sind nicht reentrant). Läuft niemand, schläft die Plattform (`hlt`). Ein Mutex übergibt beim
   Freigeben an den ersten Wartenden (fair). Test: `posix` mit `FIRE_GREEN_THREADS` lässt die Thread-Fälle der Suite auf dem eigenen Scheduler laufen.
   **Umgesetzt** (Branch `targets`): `std/fire_sched.h`, `sched/fire_ctx_ucontext.h` (Test am Rechner: alle 52 Thread-Fälle der Suite mit `FIRE_GREEN_THREADS`
   wie die VM) und `sched/fire_ctx_x86_16.h` (dos16: `setjmp`/`longjmp` zwischen laufenden Aufgaben, ein paar Befehle Assembler für den Start auf einem neuen
   Stack, `INT 1Ch` setzt das Flag; übersetzt mit `-zu` und ohne Stackprüfung, 16 KB Stack je Thread). Alle 40 Thread-Fälle ohne Imports laufen in DOSBox-X wie
   die VM. Die Haken der Runtime: `FIRE_PLAT_YIELD` (ein Safepoint gibt an bereite Aufgaben ab) und `FIRE_PLAT_PREEMPTED`.
   **Offen:** der Wechsel für 32-Bit-x86 (dos32, bios32, win32-legacy ohne Threads), Win16 (kooperativ über die Nachrichtenschleife), Boards ohne RTOS (ARM).
3. **`dos16`** - DOS im Real Mode ab 8086: OpenWatcom `wcc`, Huge Model (keine 64-KB-Grenze für die Daten von Arrays und Puffern),
   keine fire-Threads, Grafik über BIOS/VGA/VESA, Package **`dos`** (`int86`, Ports `In`/`Out`, `Peek`/`Poke` auf `far`-Adressen, Interruptvektoren;
   paralleler und serieller Port als Geräte), optional Package **`xms`** (Blöcke anlegen, zwischen Block und Puffer kopieren).
   Dazu **`#intwidth 16`** (native Breite des 8086/286); `word` ist dort 16 Bit (Punkt 1c).
   **Begonnen** (Branch `targets`): Zielprofil `dos16` (TargetProfile.Pc.cs: `int`/`float` 32 Bit, `word` 16, keine Threads), Toolchain `watcom` (`owcc`,
   Treiber in `src/fire.Native/Toolchains/`), Plattformpaket `native/platform/dos16` (Uhr, Sleep, Ende, Umgebung über `std/fire_platform_dos.h` und
   `std/fire_env_dos.h`; alle anderen Teile `none`), der Dialekt des Compilers (`native/runtime/cc/fire_cc_watcom.h`). Der Generator schreibt C89-taugliche
   lokale Arrays (keine Compound-Literals, keine nicht-konstanten Initialisierer). Programme ohne Imports laufen in DOSBox-X wie in der VM (Test in der Suite;
   SKIP ohne Open Watcom/DOSBox-X; von 139 Testprogrammen ohne Imports alle außer Threads und `#performance`; die Arrays über 64 KB laufen inzwischen auch). Eigenheiten von Open Watcom
   im Dialekt (`fire_cc_watcom.h`): Rundung zur geraden Zahl (`nearbyint`, `printf("%.*f")`), NaN-Vergleiche der 8087-Emulation.
   **Dateien** (`io`, `filesystem`; `std/fire_fs_dos.h`, Entscheidung des Benutzers): Pfade wie gewohnt `X:\FOLDER\FILE.EXT` (auch `/`), 8.3 erwartet, lange Namen
   soweit das DOS darunter sie hat, Groß/Klein gleich; was DOS zurückgibt (volle Pfade, aktuelles Verzeichnis, Listen) ist groß. `FIRE_FS_DRIVES` schaltet die
   Pfadregeln mit Laufwerken für Windows und DOS. Die VM rechnet weiter mit den Pfaden des Rechners. Test in DOSBox-X mit fester erwarteter Ausgabe.
   (Nebenbei: `__declspec(aborts)` erzeugte bei Open Watcom falschen Code - eine ungefangene Ausnahme hing; der Dialekt setzt kein `FIRE_NORETURN` mehr.)
   **Große Daten** (Arrays und Puffer über 64 KB): das Huge Model (`-mcmodel=h`, jeder Zeiger rechnet über Segmentgrenzen), Blöcke über 64 KB von `halloc`
   (`dataAlloc`/`dataFree` der Runtime, `FIRE_CC_BIG_ALLOC`), Löschen und Kopieren in Stücken bis zum Segmentende (`FIRE_CC_ZERO`/`FIRE_CC_COPY` - `memset`/`memcpy`
   der C-Bibliothek rechnen nur mit dem Offset), ebenso `fread`/`fwrite` der io-Brücke (`FIRE_CC_IO_STEP`); Größen in 32 Bit statt `size_t`. Gebunden wird
   gegen die C-Bibliothek des Large Model: deren Code liegt in einem Segment `_TEXT` (höchstens 64 KB), die des Huge Model ist rund 30 % größer und passt
   nicht mehr; Aufrufkonvention und Daten sind gleich, die Runtime gibt ihr keinen Block über ein Segmentende. Ein `int` im gepackten Array ist mit
   `#intwidth 32` 4 Byte (nicht 8). `ReadAll` einer Datei liest den Rest auf einmal in einen Puffer seiner Größe. Test in DOSBox-X (100000 Bytes, `int[30000]`,
   `any[9000]`, Kopien, eine Datei mit 100000 Bytes). Grenze: der freie DOS-Speicher (rund 400 KB neben dem Programm).
   **Konsole und Tastatur:** neues Standardpaket **`console`** ([The text console](stdlib/console.md)) für alle Ziele: `Console.Write`/`WriteAt` in den 16 Farben des
   Textmodus (`ConsoleColor`), `Clear`, Cursor, `Width`/`Height`, einzelne Tasten (`ReadKey`, `KeyAvailable`, `ConsoleKey`: Zeichen, Pfeile, Pos1/Ende, Bild,
   Einfg/Entf, F1-F12, mit Shift/Strg/Alt). Ein neuer Teil der Plattformschicht (`fire_console.h`, Schnittstelle `std/fire_console_c.h`): DOS über das BIOS
   (INT 10h/16h, Codepage 437), POSIX-Terminals (ANSI, termios), Windows (Konsolen-API; übersetzt mit den NT-Headern von Open Watcom, noch nicht ausgeführt),
   sonst `none`. Ohne Konsole (umgeleitet, die VM) einfacher Text und die Zeichen der Standardeingabe - das Programm verhält sich gleich. Tests: VM und nativ
   umgeleitet, ein Pseudo-Terminal (`script`), DOS in DOSBox-X (Bildschirm aus dem Videospeicher gelesen, Tasten über den Tastaturpuffer des BIOS).
   **`machine`** (Hardware-Paket, siehe unten; [The hardware of the PC](stdlib/machine.md)): Ports 8/16/32 Bit, physischer Speicher, CPU-Stufe und CPUID,
   CLI/STI, IRQ 0..15 (gezählt, verkettet oder selbst quittiert; fire-Code läuft nie im Interrupt - `Irq.Wait` in einem fire-Thread), CMOS-Uhr, Lautsprecher, PCI;
   ein neuer Teil der Plattformschicht (`fire_machine.h`: `std/fire_machine_x86_16.h`, sonst `none` - dort eine `MachineException`). Test in DOSBox-X.
   **Grafik** (Entscheidung des Benutzers: die `graphics`-Bibliothek läuft auch unter DOS): die Bibliothek rechnet mit eigenen Typen - `fire_gfx_int` (mindestens
   32 Bit: auf dos16 `int32_t`, sonst `int`), `fire_gfx_size` (Pixel und Bytes; auf dos16 `uint32_t`, sonst `size_t`) und `fire_gfx_malloc/calloc/realloc/free/
   copy/set/move` (`fire_gfx_mem.h`: auf dos16 die Blöcke der Runtime, sonst die C-Bibliothek - auf den anderen Zielen ändert sich nichts). Dazu die Stellen, die
   im 16-Bit-`int` überliefen (Clip-Grenzen `0x7FFFFFFF`, `byte << 8` in den Bildformaten, Konstantenprodukte, `(unsigned)`-Grenzprüfungen). Blöcke ab 16 KB kommen
   unter Watcom/Huge direkt von DOS (`FIRE_CC_BIG_FROM`: ein Puffer von 64000 Bytes kostete im Heap der C-Bibliothek 118 KB), der PNG-Kodierer nimmt dort
   kleinere Tabellen (32 statt 256 KB). Das Plattformpaket setzt `FIRE_GFX_NO_GPU`. Vergleich mit gcc bei denselben Breiten (Prüfsummen der Pixel): Linien,
   Kreise, Ellipsen, Polygone, Füllen, Text, Blits (auch gespiegelt/skaliert), `Resize`, Schatten/Unschärfe, PNG hin und zurück, Masken - gleich.
   Grenze: ein Programm mit der Grafikbibliothek hat rund 450 KB Code (je ~115 KB Grafik-C und übersetztes Prelude), daneben bleiben ~150 KB Daten
   (ein Palettenpuffer 320x200 und kleinere; ein RGBA-Puffer 320x200 nicht). Mehr Platz erst mit Overlays oder dos32.
   **Anzeige** (`windows` auf dos16, `std/fire_display_dos.h`): ein Fenster ist der ganze Bildschirm - Mode 13h (320x200, 256 Farben) bis 320x200, VESA 101h
   (640x480, 256 Farben, Bänke) darüber, wenn das BIOS ihn hat; der Framebuffer wird gestreckt. Ein Paletten-Framebuffer geht direkt in den Videospeicher und seine
   Palette in die DAC-Register (`FIRE_DISPLAY_PRESENT_FB`: die Brücke reicht den Framebuffer, keine Kopie als RGBA - 320x200 wären 256 KB), ein RGBA-Framebuffer
   über den 6x6x6-Farbwürfel und 40 Graustufen. Tasten vom BIOS (Set-1-Scancodes wie unter Windows: `fire_key_fromWin32`; gedrückt und losgelassen im selben
   Takt), die Maus über INT 33h (ihr Zeiger ist beim Schreiben eines Bildes aus); am Ende der Textmodus. Kein Fenster-Thread (`FIRE_WINDOW_NO_THREADS`). Keycodes
   und Modifikatoren der Display-Schicht sind `int32_t` (ein Keycode ohne Zeichen hat Bit 30). Tests in DOSBox-X (Videospeicher, DAC, VESA-Modus, Tasten).
   **Offen:** Mode 12h (640x480, 16 Farben, Bitebenen) und die CGA/EGA-Modi, ein `PackedFramebuffer` (1/2/4 Bit), nur geänderte Bereiche schreiben, die UI.
   **`dos`** und **`xms`** ([DOS and XMS](stdlib/dos.md)): Interrupts mit den Registern des 8086 (`Dos.Interrupt`, `DosRegisters`), DOS-Speicher, Vektoren,
   die Adresse eines Puffers als Segment:Offset; Blöcke im Speicher über 1 MB über den XMS-Treiber (`XmsBlock`, Kopien beliebiger Länge, am Ende freigegeben).
   Ein Teil der Plattformschicht `fire_dos.h` (`std/fire_dos_real.h`; dos32 später über DPMI). Test in DOSBox-X.
   **Geräte:** `devices` auf dos16 - `COM1`..`COM4` (UART direkt, 115200 8N1, FIFO; Empfang über den IRQ in einen Ringpuffer, Vektor und Maske am Ende zurück)
   und `LPT1`..`LPT3` (nur Ausgabe, BUSY/STROBE) als serielle Geräte (`std/fire_dev_dos.h`). Test in DOSBox-X (Dateien für COM1/LPT1, Empfang über den
   Loopback des UART).
   **`#intwidth 16`** (SPEC 8.2.2): jedes int und jede int-Konstante auf 16 Bit geschnitten - die VM (`Value.Ints16`), nativ beim Erzeugen (`FIRE_INT16`,
   gespeichert in 32 Bit, demselben Feld wie ein `char`), Direktive, `-i 16`, Projekteinstellung, `#if int16`, Editor. Tests: VM == gcc, und in DOSBox-X.
   **Offen:** weitere Grafikmodi (s.o.).
3b. **Die Natives eines Packages gehören in das Package** (Entscheidung des Benutzers, nach der Grafik auf DOS): der C-Code der Natives - auch die
   plattformabhängigen Teile, die heute in `native/platform/<platform>/` und `native/platform/std/` liegen (`fire_console_*`, `fire_machine_*`, `fire_dos_*`,
   `fire_dev_*`, `fire_fs_*`, ...) - liegt im Package und wird mit dem Package aktualisiert (ein neues Package bringt seine Umsetzungen je Plattform mit, ohne
   neue Runtime). Die Auswahl je Plattform über die Quellen des Packages (`ReadNativeSources` mit den Schlüsseln der Plattform und des Ziels); in der Runtime
   bleibt nur, was sie selbst braucht (Uhr, Threads, Speicher, der Kern der Plattform).
   **Stufe 1 umgesetzt:** `console`, `machine`, `dos`, `xms` bringen ihre Plattformteile selbst (`packages/<name>/native/fire_<name>_<plattform>.h`, gewählt über
   `platformSources`); zwei neue Schlüssel: `default` (eine Plattform ohne eigenen Eintrag) und `vm` (die Bibliothek der VM bekommt nur diese Schicht - dort
   "none"). Die gemeinsame Codepage-437-Tabelle bleibt als Hilfe der Plattformschicht (`std/fire_cp437.h`, auch für die DOS-Anzeige). Ein `#include "x.h"` mit
   Kommentar dahinter gilt jetzt auch als Include einer Quelle des Packages (er blieb sonst stehen). Test: die Regeln von `SourcesFor` (Projekt-Tests).
   **Stufe 2 (Entscheidung: Referenzen):** ein Teil, den mehrere Packages brauchen, gehört dem Package, dessen Kern er ist; ein anderes nimmt ihn über
   `"native": { "uses": [ "io" ] }` mit (dessen `platformSources` für das Ziel kommen vor die eigenen; das benutzte Package muss installiert sein).
   Umgesetzt: `fs` -> io (`packages/io/native/fire_fs_{c,posix,win32,dos,board,none}.h`; die Boards: `fire_fs_board.h` mit posix oder - `FIRE_NO_FS` - none),
   graphics benutzt io; `audio` -> audio (circuits benutzt es nur auf posix, windows und in der VM: ein Eintrag von `uses` kann seine Plattformen nennen,
   `{ "import": "audio", "platforms": [ ... ] }`), `net`, `tls` (benutzt net), `dev` -> devices, `gpio`, `i2c`, `spi`, `usb`, `wifi`.
   **Stufe 3:** `display` -> windows; `native.headers`: die Dateien, die eine Quelle einbindet, ohne selbst Quelle des Builds zu sein, liegen in jedem Build als
   Dateien neben dem Programm (`pkg/<import>/`, NativeRuntimeFiles schreibt sie wie die Runtime), das `#include` nennt sie dort (auch in `#if`; zuerst eingesetzt -
   das machte das C eines minimalen ui-Programms 5,1 statt 2,8 MB groß, alle Anzeigen standen darin). Damit bleiben alle Teile so geschrieben wie vorher in `native/platform/std` - je Plattform eine Datei (die
   Auswahl, z. B. `fire_audio_desktop.h`, `fire_display_select.h`, oder gleich die Umsetzung), die übrigen sind Header; die Umbauten von Stufe 2
   (`FIRE_<TEIL>_USE_<X>`, Wächter in jeder Datei) sind zurückgenommen. `FIRE_PLATFORM_<TEIL>_C_HEADER` gibt es nicht mehr; in `native/platform` bleibt nur, was
   die Runtime selbst braucht (`fire_platform.h`, Scheduler, Umgebung) und `fire_cp437.h` (Konsole und DOS-Anzeige).
3c. **Package `x86`** (umgesetzt, Wunsch des Benutzers): ein dos16-Programm wechselt in den 32-Bit-Protected-Mode oder den 64-Bit-Long-Mode, führt dort eine
   Routine (Maschinencode als `byte[]`, Register hin und zurück) aus und kommt zurück; Speicher über 1 MB (`ReadLinear`/`WriteLinear`/`Move`), A20, Erkennung
   von V86 (EMM386/Windows: ohne VCPI/DPMI kein Wechsel) und 386/Long Mode (docs/stdlib/x86.md). Zwei kleine Stubs (NASM, als Bytes in `fire_x86_stubs.h`).
   Getestet: Protected Mode aus fire in DOSBox-X; beide Stubs unter QEMU ohne DOS (Bootsektor). Offen: Long Mode aus fire unter DOS auf einem 64-Bit-Rechner
   (QEMU mit FreeDOS - der Download ist in dieser Umgebung gesperrt), und **fire-Code im Protected Mode** - eine Funktion, für das 32-Bit-Ziel übersetzt
   (kommt mit `dos32`/`bios16`/`bios32`: dieselbe Runtime mit flachem Speicher), als Routine mitgegeben.
4. **`dos32`** - DOS mit DOS-Extender ab 386 (**umgesetzt**): Open Watcom 386, der Extender DOS/32A steckt im Programm (`-bdos32a`: eine .exe, kein
   DOS4GW.EXE daneben), flacher Speicher, `int`/`float` 64 Bit wie auf dem Desktop (dieselben Ergebnisse wie die VM), fire-Threads über den eigenen Scheduler
   (`std/sched/fire_ctx_x86_32.h`, Takt über IRQ 0). Die Packages mit Real-Mode-Code dienen dos16 und dos32 mit denselben Dateien über
   `platform/std/fire_dos_bios.h` (BIOS über `int386` - der Extender reicht Interrupts nur mit Registern hinunter -, das erste Megabyte an seiner linearen
   Adresse, DPMI 0300h für einen Interrupt mit Puffer, z. B. die VESA-Information): Konsole, Anzeige von windows (Mode 13h, VESA 101h), serielle Ports;
   machine hat `fire_machine_x86_32.h` (CPUID, 32-Bit-Ports, physischer Speicher über 1 MB per DPMI 0800h, IRQs gemeinsam mit dos16), dos hat
   `fire_dos_dpmi.h` (Interrupts im Real Mode über DPMI; ein Puffer des Programms liegt über 1 MB: DosException). xms und x86 gibt es dort nicht (der Speicher
   gehört dem Programm, es ist schon im Protected Mode). Getestet in DOSBox-X mit denselben Programmen wie dos16.
   Offen: ein Package **`dpmi`** (Interrupts mit Registersatz, Real-Mode-Aufrufe, Selektoren, DOS-Speicher als Puffer des Programms).
5. **`bios16` und `bios32`** - ohne Betriebssystem, vom BIOS gestartet (Entscheidung des Benutzers: `bios16`/`bios32` geteilt; zuerst die BIOS-Ziele, danach die Grafik):
   * **`bios16`**: Real Mode ab 8086, die Breiten und das Huge Model von dos16 (Open Watcom `wcc`); BIOS-Dienste direkt (`int 10h`, `13h`, `16h`, `1Ah`).
   * **`bios32`**: 32-Bit-Protected-Mode mit flachem Speicher ab 386 (Open Watcom `wcc386`), die Breiten von dos32; das Boot-Image schaltet selbst in den
     Protected Mode, BIOS-Dienste über einen kurzen Rücksprung in den Real Mode.
   * Start über ein eigenes Boot-Image (Bootsektor lädt das Programm von Diskette/Platte, in QEMU als `-drive`/`-fda`); Ende in QEMU über den Debug-Port.
   * **Zwei Stufen, wählbar** (Entscheidung des Benutzers): **minimale libc** (Grundlage; eigener Startcode, Heap aus der Speicherkarte - `int 12h`/E820 -,
     Strings, `snprintf` mit Gleitkomma, Mathe, `setjmp`; Ausgabe auf Bildschirm und seriellen Port, Tastatur, Zeit über das BIOS; keine Dateien) oder darauf
     aufbauend ein **Mini-DOS** (ein `int 21h`-Dienst über dieser libc: Speicher, Ausgabe, Tastatur, Zeit, Vektoren, Ende, Dateien nur lesend aus dem Boot-Image),
     mit dem die Packages laufen, die DOS aufrufen (`dos`, `xms`, ...). Auswahl über das Ziel bzw. eine Option des Ziels.
   * Kein Audio, kein Netz; fire-Threads über den eigenen Scheduler (Takt über IRQ 0) wie dos16/dos32. Nur Grafik, grober IO (Bildschirm/Konsole, Tastatur,
     serieller Port) und die Hardware-Packages.
   * Tests in QEMU (`qemu-system-i386`) ohne Fenster: Ausgabe über den seriellen Port, Ende über den Debug-Port.
   * **Stand `bios16`** (umgesetzt, beide Stufen): das Boot-Image `<programm>.img` ist eine FAT12-Diskette von 1,44 MB (`Fat12Image`): der Bootsektor mit BPB lädt
     `FIRE.EXE` (die erste Datei, am Stück), dazu kommen die Dateien und Ordner des Ordners `disk` neben dem Programm (8.3-Namen); `fsck.fat` und mtools lesen sie.
     Eigener Start (`fire_bios16_start.asm`), Heap bis zur Grenze aus `int 12h`, ein `int 21h` der Plattform für die C-Bibliothek von Watcom (Konsole auf
     Bildschirm und COM1, Tastatur, Zeit), fire-Threads über den eigenen Scheduler (Takt über `INT 1Ch`).
     **`bios16`** = die minimale libc; **`bios16-dos`** = dazu das Mini-DOS (`fire_bios16_dos.h`): Dateien nur lesend von der Diskette (öffnen, lesen, suchen,
     Muster, Ordner, aktueller Ordner, Zeit und Attribute; Schreiben wird mit „Zugriff verweigert“ abgelehnt) und der Speicher von DOS (48h/49h/4Ah) - damit
     laufen `io`, `filesystem` und `dos` wie auf dos16. Getestet in QEMU (`FIRE_TESTS_ONLY=bios16`).
     Gebunden mit `option nofarcalls`: QEMU 8.2 rechnet einen Near-Call, der unter IP 0 umläuft, nicht in 16 Bit (die Umwandlung der Far-Calls durch wlink
     erzeugt solche). Offen: `xms` (das Mini-DOS hat keinen XMS-Treiber), `bios32`, die Grafik.
6. **`efi-x64`** - eine UEFI-Anwendung (PE32+; clang + lld-link oder mingw-w64 x86_64 ohne CRT), bleibt in den Boot Services: Konsole über ConIn/ConOut, Grafik über
   den GOP-Framebuffer, Zeit und `Sleep` über Timer-Ereignisse, Speicher über `AllocatePool`. Keine fire-Threads; Audio, Dateien und Netz wie bei `bios16`/`bios32` nicht.
   EFI mit 32 Bit nicht (solche Rechner haben praktisch immer ein CSM: dort laufen `bios16`/`bios32`).

   Gemeinsam für `bios16`, `bios32` und `efi-x64`: eine kleine freistehende C-Bibliothek für die Runtime (Heap aus der Speicherkarte - E820 bzw. die der Firmware -,
   `snprintf`, Strings, Mathe, `setjmp`).
7. **`win16`** - Windows 3.x (**umgesetzt**, Grundstock): Open Watcom `wcc -bwindows`, das Huge Model wie dos16 (Arrays über 64 KB), die Breiten von dos16,
   fire-Threads als Aufgaben des eigenen Schedulers (`std/sched/fire_ctx_win16.h`: ein Stack-Segment je Aufgabe über `GlobalAlloc` mit dem Stack-Kopf von
   Windows, `-zu`, Fensterprozeduren mit `__loadds`, Zeitscheiben über `GetTickCount`; sie laufen, solange das Programm an der Reihe ist). `std/fire_platform_win16.h`: das Programm beginnt in `WinMain` (Argumente aus der Befehlszeile, dann der erzeugte Einstieg
   `fire_win16_main`), Uhr über `GetTickCount`, Schlafen hält die Nachrichten am Laufen (kooperatives Multitasking), die Ausgabe geht in die Datei aus
   `FIRE_STDOUT` oder wird am Ende in einem Fenster gezeigt. Das Paket `windows` hat `fire_display_win16.h`: ein eigenes Fenster über GDI (`StretchDIBits` in
   Bändern unter 64 KB, Palette als 8-Bit-DIB, RGBA als 24-Bit), Tasten aus `WM_KEYDOWN` (Scancode wie unter Win32), Text aus `WM_CHAR`, Maus, Größe, Schließen.
   Getestet unter Wine (sein 32-Bit-Teil führt 16-Bit-Programme aus) in Xvfb: dieselbe Ausgabe wie die VM, große Arrays, ein Fenster (Pixel mit `GetPixel`
   zurückgelesen, Tasten). Offen: die Zwischenablage von Windows, Ausgabe live in einem Fenster statt am Ende, Dateidialoge.
   **scene3d auf 16 Bit** (win16, dos16): die Engine zählt ihre Puffer mit `s3d_size` (32 Bit unter Watcom/16 Bit, sonst `size_t`) und holt sie über die Blöcke
   der Runtime (`S3D_MALLOC`/`S3D_FREE`/`S3D_COPY`/`S3D_ZERO` -> `dataAlloc`/`dataFree`/`dataCopy`/`dataZero`; außerhalb von fire die C-Bibliothek); `realloc`
   gibt es dafür nicht mehr (`s3d_grow` holt einen neuen Block und kopiert). Ein Tiefenpuffer, eine Schattenkarte oder eine Textur darf damit über 64 KB groß
   sein. Auf win16 läuft das Beispiel `Scene3D` (640x480); getestet mit 160x120 unter Wine (dieselben Zahlen wie die VM; einzelne Farben weichen um 1 ab - der
   x87 rundet anders als SSE). Auf dos16 reicht der konventionelle Speicher neben dem Programm (~500 KB mit scene3d) nur für kleine Bilder (getestet 64x48;
   Framebuffer, Tiefenpuffer und die Pixel der Kamera nehmen je 4 Byte pro Pixel). dos16 übersetzt dafür wie win16 mit `-zt=16` (DGROUP war mit scene3d zu
   voll). Dabei gefunden: `dataFree(NULL)` gab unter Watcom/Huge `hfree(0)` an DOS weiter (ein freier Platz der Tabelle großer Blöcke ist auch NULL).
8. **`win32-legacy`** - Win9x, NT 3.51 bis XP (OpenWatcom `wcc386`): Synchronisation ohne Vista-APIs (Critical Section und Events statt `SRWLOCK`/
   `CONDITION_VARIABLE`), `GetTickCount` statt `GetTickCount64`, ANSI/Unicode-Schicht für 9x, `inet_addr` statt `inet_pton` (NT 3.51: Winsock 1.1), TLS über
   mbedTLS statt Schannel, kein USB, GDI aus dem laufenden Umbau.
9. **Ebene 2 im Generator**: typisierte Locals und Parameter als C-Typen (`int8_t`, ...) - in `#strict` überall, sonst wo der Generator den Typ beweist; Felder
   gepackt pro Klasse später.
   **Begonnen:** Stufe 1 (typisierte Arrays im Generator, `KindArr`, auch Felder), Stufe 2a (Merkmal „schmal“, skalare Plätze und Variablen für Ints der
   Programmbreite, floats, bools aus Vergleichen und `word`; docs/spec/NATIVE_BACKEND.md "Skalare"). Offen: Zahlen mit Einheit als Skalare, Parameter und
   Rückgaben als C-Typen.
   **Messung (Frage des Benutzers: woher die 350 KB eines Grafikprogramms auf dos16 kommen).** Grafik-C ~85 KB (~23 Byte je Zeile, normal), Runtime 75 KB,
   C-Bibliothek 46 KB - und **109 KB aus fire übersetzter Code** für 725 Zeilen (Prelude der Grafik + Programm), ~150 Byte je Zeile: `DrawRoundRect` (4 Zeilen,
   reicht an ein Native weiter) wird 298 Zeilen C und 4,7 KB. gcc `-m32 -Os` erzeugt aus demselben C ebenso viel (108 KB) - es ist der allgemeine Code, nicht
   der 16-Bit-Modus. Wo er steckt (Versuch: jeweils eine Gruppe weggelassen, im DOS-Build): Tests auf eine laufende Ausnahme nach jedem Schritt ~11 KB,
   Gültigkeitslisten (`leave`/`adopt`/`transferTree`) ~8 KB, Prüfungen der Parameter ~8 KB, `retain`/`release` ~4 KB; der Rest (~70 KB) sind die 8-Byte-`Value`s
   selbst (Kopien, Aufrufe mit Values, Feldzugriffe) - das nimmt erst diese Ebene 2. Schon umgesetzt: kein `retain`/`release` für Parameter, die nach ihrer
   Prüfung `int`/`float`/`bool` sind (DOS 109 -> 103 KB, gcc 108 -> 79 KB), der Zustand des Threads im Datensegment unter Watcom/Huge (`FIRE_CC_NEAR_DATA`).
   Noch offen dort: Methoden mit erklärtem skalaren Rückgabetyp ohne `adopt` (braucht den Rückgabetyp im Bytecode und eine Regel, wann er gilt - der
   Typprüfer prüft ihn, die VM erzwingt ihn nicht).

Für alle neuen Ziele: Zielprofile, Toolchain OpenWatcom (`bios16`/`bios32` auch gcc `-m32 -ffreestanding`, `efi-x64` clang/mingw), eine Plattformschicht pro Familie;
Tests ohne Fenster über den seriellen Port bzw. die Konsole: DOSBox-X / dosemu2 (`dos16`, `dos32`, Win 3.11), QEMU (`bios16`/`bios32`; `efi-x64` mit der Firmware OVMF),
9x und NT über QEMU-Images.

**Grafik auf alter Hardware** (Render-Targets der `graphics`-Bibliothek, für `dos16`, `dos32` und `bios16`/`bios32`; `efi-x64` hat nur GOP):

* **MDA** (Text 80x25, monochrom mit Attributen): ein Render-Target für Zeichenzellen; Grafik auf der Monochromkarte über **Hercules** (720x348, 1 Bit).
* **CGA** (Text 40/80x25 in 16 Farben, 320x200 mit 4 Farben aus den beiden Paletten, 640x200 mit 2), **EGA** (320x200 und 640x200/350 mit 16 aus 64 Farben, Bitebenen),
  **VGA** (Mode 13h: 320x200 mit 256, Mode 12h: 640x480 mit 16, Bitebenen) und **VESA** (Framebuffer mit 8/15/16/24/32 Bit).
* Das Render-Target wandelt beim Present in das Layout der Hardware (die verschränkten Bänke der CGA, die Bitebenen der EGA/VGA über Plane-Masken, die Palette in die
  Register der Karte) - nur die geänderten Bereiche.
* Ein eigener **`PackedFramebuffer`** für 1, 2 und 4 Bit pro Pixel (Palette mit 2, 4 und 16 Einträgen) - der `Framebuffer` (RGBA, 8-Bit-Palette) bleibt
  unverändert:
  * gruppiert 8, 4 oder 2 Pixel in ein Byte, das linke Pixel in den oberen Bits (wie CGA, die Ebenen der EGA und Hercules: das Present ist eine Kopie);
  * Spans, Füllen und Blits arbeiten in ganzen Maschinenwörtern (16 bzw. 32 Bit: das Doppelte bzw. Vierfache an Pixeln pro Schritt, die Farbe als wiederholtes
    Muster, Masken nur an den Rändern); Blits mit Versatz tauschen die Bytes (x86 ist Little Endian) oder gehen in Bytes;
  * Zeilen auf die Wortbreite des Ziels gerundet;
  * bei 4 Bit wahlweise *chunky* (2 Pixel pro Byte) oder *planar* (4 Ebenen mit je 1 Bit: das Layout der EGA/VGA-16, jede Ebene bleibt unter 64 KB);
    *planar* ist der Standard auf `dos16` und `bios16`/`bios32` (auch im Bootblock, solange der Code dort noch im Real Mode läuft), sonst *chunky*;
  * dieselbe Zeichen-API wie der `Framebuffer` über eine gemeinsame Schnittstelle; ein Blit zwischen beiden bildet die Farben über die Palette ab.
  Wer auf eine CGA/EGA zeichnet, kann ebenso einen 8-Bit-`Framebuffer` nehmen - das Render-Target bildet dann auf die Farben der Karte ab.

**Hardware-Packages** (für `dos16`, `dos32`, `bios16`/`bios32`, teils `efi-x64`):

* **`machine`** - die Hardware des PCs, auf jedem dieser Ziele gleich: Ports (`In`/`Out` mit 8/16/32 Bit), physischer Speicher (`Peek`/`Poke`, Bereiche abbilden),
  PCI-Konfiguration, CPUID, PIT, CMOS/RTC, Interrupts (Handler eintragen, PIC/EOI, sperren/freigeben). Darunter je Ziel: DOS über die Vektoren von `int 21h`,
  DPMI über die DPMI-Funktionen, `bios16`/`bios32` über eine eigene IDT; EFI ohne Interrupts (Abfragen, Timer-Ereignisse) und PCI über die Protokolle der Firmware.
  Paralleler und serieller Port als Geräte des Gerätemanagers (`devices`).
* Darauf die Dienste der Umgebung: **`dos`** (`int 21h`, `int86`, Speicher und Vektoren von DOS), **`dpmi`** (siehe 4), **`bios`** (`int 10h`/`13h`/`16h`/`1Ah` über den
  Rücksprung in den Real Mode), **`efi`** (Protokolle der Firmware: GOP, Konsole, Variablen, Zeit), **`xms`** (siehe 3).

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

## 14. UI-Markup: fehlende Eigenschaften

Oberflächen werden in erster Linie mit `.fxml` gebaut ([UI markup (.fxml)](ui/markup.md)). Einige Eigenschaften, die jedes `UI.Element` hat, kennt das Schema des
Markups (`src/fire.UI.Markup/MarkupSchema.cs`, `Common`) noch nicht - sie gehen heute nur im Code-Behind:

* **`ToolTip`** (Text; `Element.ToolTip`).
* **`Cursor`** (`UI.Cursor`: `Arrow`, `IBeam`, `Hand`, `SizeWE`, ...; `Element.Cursor`) - als Enum-Eigenschaft wie `HorizontalAlign`.
* **Drag & Drop:** `AllowDrop` (bool) und die Ereignisse `OnDragStart(x, y)`, `OnDragOver(data, x, y)`, `OnDrop(data, x, y)`, `OnDragLeave()`,
  `OnDragDone(data, effect)` als Handler (mit Rückgabewert: `UI.DragData` bzw. `UI.DropEffect` - die Handler-Methoden des generierten Codes
  geben heute nichts zurück, das braucht eine Erweiterung von `EventDef`).

Dazu jeweils: Properties-Fenster und Completion in spark, die Entwurfsansicht (ToolTip/Cursor zeigen dort nichts), Tests im Generator.

Ebenfalls hier, weil es vor allem Markup-Fenster trifft: **der oberste Container füllt das Fenster nicht.** `Root.content` ist ein `Panel` mit
Canvas-Anordnung - ein Kind bekommt nur seine Wunschgröße, `HorizontalAlign`/`VerticalAlign = Stretch` wirken dort nicht. Ein `<DockPanel>` oder
`<Grid>` direkt im `<Window>` braucht deshalb `Width`/`Height` (gleich der Fenstergröße) und wächst beim Vergrößern des Fensters nicht mit; nur der
`DockManager` von `ui.docking` rechnet selbst mit `root.width`/`root.height`. Vorschlag: `Root.content` ordnet Kinder ohne feste Größe (und ohne
`Left`/`Top`) wie ein Grid mit einer Zelle an - gestreckt auf die Fenstergröße, mit `Margin` und Ausrichtung; Kinder mit Position bleiben wie heute.

## 15. Arrays statt Byte-Puffer an Natives

Gefunden beim Durchgehen der Doku (Branch `doc-2611-1`): ein Array an einem `byte[]`-Parameter eines Packages bringt die VM zum **Absturz**
(Segmentation fault) statt zu einer Exception:

```fire
#import "filesystem"
IO.File.WriteAllBytes("x.bin", [1, 2, 3])        // Segfault
byte data[] = [0x9F]                              // ein typisiertes byte-Array - auch kein Puffer
Spi.Sim.Reply(data)                               // Segfault (ebenso I2C bus.Write(0x10, [1, 2]))
```

`Net.UdpSocket.SendTo([1, 2, 3], ...)` meldet "Bad address". Ursache: Parameter typisieren nicht, was sie bekommen (SPEC 8.4.2), und die Natives
lesen ein Argument mit `bufOf(value)` als Byte-Puffer, ohne die Art zu prüfen (`fire_bridge_spi.h` `fire_spib_checkRange`, i2c, io, ...).

* Sofort: jede Stelle, die `bufOf` auf ein Argument anwendet, prüft die Art (`K_Buffer`) und meldet sonst eine `ArgumentException`
  ("expects a byte buffer (new byte[n]), not an array") - am besten einmal in der Package-ABI für Parameter, die als Puffer deklariert sind.
* Danach: Arrays aus Zahlen und typisierte `byte`-Arrays an einem `byte[]`-Parameter in einen Puffer wandeln (eine Kopie; Ergebnisse, die
  zurückgeschrieben werden - `Read(buffer, ...)` - brauchen dann einen echten Puffer), oder `byte a[] = [...]` legt gleich einen Puffer an. Für
  Nutzer sind beide heute nicht zu unterscheiden.
* Test: jedes Package mit `byte[]`-Parametern bekommt einen Fall mit einem Array (VM und nativ).

