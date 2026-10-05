# Natives Backend (AOT): fire -> C++

Status: **experimentell**, Branch `aot`. Dieses Dokument beschreibt Ziel, Architektur, den aktuellen Stand und die Messwerte.

## Ziel

fire-Programme sollen sich auf Wunsch zu **richtig schnellen, eigenständigen Binaries** übersetzen lassen - auf dem Desktop
und auf einem ESP32 (FreeRTOS), wo die Sprache nur dann sinnvoll ist, wenn die Leistung stimmt.

## Entscheidung: den Code direkt als C++ einbetten

Der Bytecode wird **nicht** als Datenblock neben einem Interpreter ausgeliefert, sondern in **C++-Anweisungen übersetzt**, die der
C++-Compiler zusammen mit der Runtime sieht und optimiert. Dafür sprechen:

* Der C++-Compiler erledigt die Typinferenz mit: `var i = 0` ist ein `Int(0)`, die Art des Werts steht zur Übersetzungszeit fest,
  und der generische `Value`-Pfad (Art prüfen, Einheit prüfen, rechnen) faltet sich zu nacktem `int64`-Code zusammen.
  Das haben wir gemessen (siehe unten): schon die *generische* Übersetzung ist mehr als 20x schneller als die VM.
* Kein Programmformat, kein Lader, kein Dispatch. Auf dem ESP32 liegt der Code als normaler Code im Flash (Firmware), nicht als
  Daten, die ein Interpreter erst lesen müsste.
* Jede fire-Funktion (statische Methode, später auch Methode, Konstruktor, Lambda) wird eine C++-Funktion; kleine werden vom
  Compiler eingebettet, der Rest bleibt ein Aufruf - genau so, wie man es erwartet.

Ein portables Binärformat braucht nur noch der **Interpreter** - und den braucht man nur, wenn Skripte auf dem Gerät *zur Laufzeit*
nachgeladen werden sollen (ohne neu zu flashen). Das ist ein eigener, späterer Schritt (siehe `PORTING.md`); die
AOT-Übersetzung hängt nicht davon ab.

## Architektur

```
Skript -> Lexer -> Parser -> Resolver -> Compiler -> Bytecode (LinkedProgram)
                                                        |
                          +-----------------------------+------------------------+
                          v                                                      v
                  C#-VM (Editor, Debugger)                       fire.Native (CppGenerator)
                                                                                 |
                                                                  erzeugtes C++ + native/runtime/fire_rt.hpp
                                                                                 |
                                                                  clang/gcc/MSVC/xtensa-gcc -> Binary
```

* `src/fire.Native` - der Generator (`CppGenerator`), liest `LinkedProgram` und schreibt eine `.cpp`-Datei. Die Runtime
  (`fire_rt.hpp`) steckt als Ressource in der DLL und wird neben die `.cpp` geschrieben.
* `native/runtime/fire_rt.hpp` - die C++-Runtime: `Value` (16 Byte), Arithmetik/Vergleiche mit schnellem Pfad und kaltem Fehlerpfad,
  Ausgabeformat identisch zu `Value.ToString()`.
* `fire.Compiler native <dateien> [-o out.cpp]` - Befehlszeile.

### Wie der Bytecode übersetzt wird

* **Der Operandenstack verschwindet.** Seine Tiefe ist an jeder Instruktion bekannt (der Generator verfolgt sie über alle Wege und
  verlangt, dass sie sich an Zusammenflüssen deckt). Stackplatz `k` ist die C++-Variable `s{k}`; der Compiler macht daraus Register.
* **Scopes verschwinden, solange sie nur Werte halten.** Die Scope-Kette ist statisch bekannt. Eine lokale Variable ist eine
  C++-Variable (`B{scope}_{slot}`, Parameter `P{slot}`, Globale `G{slot}`).
* **Sprünge werden `goto`**, jede Funktion eine C++-Funktion.
* **Der Generator lehnt ab, was er nicht kann** (`NativeNotSupportedException` mit dem Namen des Opcodes), er übersetzt nie
  mit anderer Semantik.

### Regeln für die Runtime (gelernt)

* **Alle Funktionen nehmen `Value` per Wert**, nie per Referenz, und die Fehlerpfade nur die Operandenarten. Eine Referenz auf eine
  Variable, die an einen nicht eingebetteten Aufruf geht, lässt die Variable "entkommen": der Compiler hält sie dann - und weil der
  Generator die Stack-Variablen wiederverwendet, *alle* - im Speicher statt in Registern. Das allein kostete Faktor 5-10.
* Kein `throw`, kein RTTI (ESP32-Toolchains schalten beides oft ab). Laufzeitfehler beenden das Programm mit Exitcode 1; `try`/
  `catch` (wiederaufnehmbar) kommt mit dem Ausnahme-Mechanismus, siehe Roadmap.
* Reines C++17, `std::to_chars` für die Zahlenausgabe (kürzeste Darstellung wie .NET).

## Messwerte

Rechner dieser Sitzung, g++ 13 `-O2`, VM im Modus *Performance*, Zeiten ohne Prozessstart (1,3 ms):

| Benchmark | VM | handgeschrieben, generisch | erzeugt (g++) | Faktor (erzeugt) |
|---|---:|---:|---:|---:|
| `loop` (1,5 Mio. Iterationen, Int) | 158 ms | 1,9 ms | ~2,8 ms | ~55x |
| `float` (600 000 Iterationen) | 99 ms | 3,4 ms | ~4,8 ms | ~20x |
| `fib` (rekursiv, `Fib(23)`) | 29 ms | 0,27 ms | ~1,3 ms | ~22x |

*Handgeschrieben generisch* heißt: alles bleibt ein getaggter `Value`, so wie es der Generator erzeugt; mit nackten `int64`/`double`
(Typinferenz) wird `fib` weitere ~4x schneller (`native/spike/spike.cpp`). Die Zahlen sind Momentaufnahmen von einer
einzigen Maschine, keine Garantie.

## Stand

Übersetzt wird eine Teilmenge der ISA:

* Konstanten (Int/Float/Bool/Char/String/Undefined, mit Einheit), `Pop`/`Dup`/`Swap`
* Lokale und globale Variablen, Blockscopes, verschmolzene Instruktionen (`StoreLocalPop`, `JumpIfNotLt`, `ArithLocalConstPop` ...)
* `+ - * / %`, Bit-Operationen, Vergleiche, `&&`, `||`, `!`, `if`/`while`/`for`/`do`, `break`/`continue`
* Statische Methoden (inkl. Rekursion), `print`
* Einheiten: Rechnen und Vergleichen **gleicher** Einheiten
* Float-Genauigkeit 32 oder 64 Bit (`#floatwidth`, `-f`), identisch zur VM

Noch nicht (der Generator meldet es mit Namen): Objekte, Felder, Methoden, Konstruktoren, Arrays, Strings über `print` hinaus,
Lambdas/Closures, Zeiger, Ausnahmen, Threads, Reflection, Einheiten-Algebra und implizites Einheiten-Coercing, `extern`, die Bridges.

Getestet wird per **Differential-Test** (`fire.Testing`, Block "Native-Backend"): jeder Fall läuft in der VM und als erzeugtes
C++ (g++/clang++, mit `-Wall -Wextra`, ohne Warnung), die Ausgabe muss gleich sein.

## Roadmap

1. **Sprachumfang**: Objekte/Felder/Methoden (Layout pro Klasse statt Dictionary), Strings, Arrays, Lambdas (Closure-Conversion),
   Einheiten-Algebra (Tabelle zur Übersetzungszeit, Konvertierungsfaktoren als Konstanten).
2. **Ownership**: Scopes, die besitzende Objekte halten, behalten eine Laufzeit-Scope-Kette (für die Destruktor-Kaskade); alle anderen
   bleiben aufgelöst.
3. **Ausnahmen mit Resume**: `throw` ruft den Handler über einen dynamischen Handler-Stack *auf* (kein Abrollen); nur "abbrechen"
   rollt ab, über ein Statusflag nach Aufrufen.
4. **Threads, `sync`, Safe-Points** (`leave`/`terminate`) über `std::thread` bzw. FreeRTOS-Tasks.
5. **Bridges** (Variante A): eine C++-Implementierung mit C-ABI, die auch der C#-Editor per P/Invoke nutzt - IO und Time zuerst,
   dann Graphics (SDL3 ist ohnehin C), zuletzt Devices.
6. **Optimierungen**: Typinferenz und Einheiten-Folding, Devirtualisierung (geschlossene Welt), Inlining, Scope-Elision.
7. **Zielprofil und ESP32**: Ziele (`--target`), `#if`, Stack-Analyse, Plattformpakete, ESP-IDF-Komponente.
8. **Editor**: `Run -> Build Native`, Toolchain-Erkennung.

## Umschalter und Ziele

Alles, was sich je nach Ziel ändert, hängt an **einer** Beschreibung des Ziels (Betriebssystem/Board, Float-Genauigkeit,
Stackgröße, verfügbare Bridges), die Übersetzer, VM und Editor teilen.

### Zielprofil - umgesetzt (Basis)

`TargetProfile` (`src/fire/Runtime/TargetProfile.cs`) beschreibt ein Ziel an einer Stelle; Linker, Generator und später der
Präprozessor lesen dasselbe:

| Feld | Bedeutung | `esp32` |
|---|---|---|
| `Name` | `--target`/`-t` auf der Befehlszeile | `esp32` |
| `Symbols` | Symbole für `#if` (noch nicht gebaut) | `esp32`, `freertos` |
| `FloatWidth` | Standard-Genauigkeit von `float` | 32 |
| `DefaultStackBytes` | Standard-Stack eines `fire`-Threads | 4096 |
| `Imports` | welche `#import`-Bibliotheken es dort gibt | print, io, devices, time, reflection, linq (kein `graphics`/`ui`) |
| `HalPackage` | Plattformpaket der C++-Runtime | `esp32` |
| `IsEmbedded` | Einstieg ist `app_main` statt `main` | ja |

Eingebaut: `windows`, `linux`, `macos`, `esp32`; `TargetProfile.Host` ist das Ziel der VM im Editor.

* **Rangfolge der Float-Genauigkeit:** `-f` > `#floatwidth` im Programm > Standard des Ziels > 64.
* `Linker.CompileAndLink(..., target:)` lehnt `#import` einer Bibliothek ab, die es auf dem Ziel nicht gibt (Fehler beim Übersetzen, nicht
  erst auf dem Gerät).
* Der Generator schreibt `FIRE_TARGET`, `FIRE_TARGET_<NAME>`, `FIRE_HAL_<PAKET>` und `FIRE_DEFAULT_STACK_BYTES` in die Datei; für
  eingebettete Ziele den Einstieg `extern "C" void app_main(void)`. Die Plattformschicht der Runtime wählt später über diese Defines.
* Befehlszeile: `fire.Compiler native skript.script -t esp32 -o main.cpp`.

Zusätzliche Ziele (eigene Boards) sind ein Eintrag in dieser Tabelle; Profile aus einer Datei sind ein späterer Schritt.

### Float-Genauigkeit - umgesetzt

`#floatwidth 32|64` (SPEC 8.2.1), überschreibbar mit `-f 32|64`. **Die VM nutzt den Schalter genauso**: sie rundet jedes Float-Ergebnis,
jede Float-Konstante und jede Ganzzahl-nach-Float-Umwandlung auf 32 Bit und druckt die kürzeste 32-Bit-Darstellung. Für `+ - * /` ist
"in `double` rechnen und auf `float` runden" bit-identisch zur echten `float`-Rechnung (ein `double` hat mehr als 2p+2 Bit), die
Ergebnisse auf dem Desktop und auf dem ESP32 sind also gleich. Nativ ist `Real` dann `float` (`FIRE_FLOAT32`), auf dem ESP32 die
Hardware-FPU. Der Differential-Test deckt beide Genauigkeiten ab. Für das Ziel ESP32 ist 32 der Vorgabewert (kommt mit dem Zielprofil).

### Stackgröße der Threads

**Vorab ermitteln geht, solange das Programm nicht rekursiv ist** - sonst nicht:

* Der Generator kennt den Aufrufgraphen (geschlossene Welt: alle Funktionen, Methoden, Lambdas stehen fest). Für jeden Thread-Einstieg
  (`main`, jeder `fire`-Block) ist der Stackbedarf der **längste Pfad** durch diesen Graphen plus die Summe der Rahmengrößen darauf.
* Rahmengrößen: als sichere Obergrenze aus den Variablen der Funktion (16 Byte je `Value` plus Aufrufrahmen); genauer aus den
  Compiler-Angaben (`-fstack-usage` bei gcc/clang, auch im ESP-IDF), dann ist ein zweiter Durchlauf nötig.
* Aufrufe über Lambdas und virtuelle Methoden zählen konservativ alle in Frage kommenden Ziele (gleiche Parameterzahl bzw. Name).
* **Rekursion** (ein Zyklus im Graphen) hat keine statische Grenze. Dann gilt die vom Nutzer angegebene Tiefe oder der Standardwert.

Vorschlag für die Einstellung (Standard `auto`):

```
#stacksize auto          // Analyse; wo sie nicht geht (Rekursion): Standardwert, mit Warnung
#stacksize 6144          // feste Größe in Byte für alle Threads
#recursion 200           // geschätzte maximale Rekursionstiefe, damit "auto" auch rekursive Programme abdeckt
```

und `--stack auto|<Byte>`, `--default-stack <Byte>` auf der Befehlszeile (wie `-f` überschreibt die Befehlszeile die Direktive). Ein
Prolog-Test (`sp < Grenze` -> fire-Fehler statt stillem Überschreiben) macht eine zu knappe Größe diagnostizierbar; FreeRTOS liefert
mit `uxTaskGetStackHighWaterMark` die tatsächliche Nutzung, die ein Debug-Build ausgibt, damit man die Größe nachjustieren kann.

### Code abhängig vom Ziel

Zwei Ebenen, die zusammenarbeiten:

1. **In fire (Quelltext):** bedingte Übersetzung im Präprozessor,

   ```
   #if windows
       var port = "serial:COM3"
   #elif esp32
       var port = "uart:1"
   #else
       var port = "serial:/dev/ttyUSB0"
   #endif
   ```

   Die Symbole setzt das Ziel: Betriebssystem/Board (`windows`, `linux`, `macos`, `esp32`), Engine (`vm`, `native`), `float32`;
   dazu `#define NAME` und `--define NAME`. Weil das Textersetzung vor dem Parser ist, werden nicht gewählte Zweige nie gelesen - sie
   dürfen auch Bridges benutzen, die es auf dem Ziel gar nicht gibt. Die VM setzt die Symbole für das Host-System selbst, damit
   dasselbe Skript im Editor und als Binary dasselbe tut; die Live-Diagnose des Editors graut nicht gewählte Zweige aus.
2. **In C++ (Plattformschicht):** siehe nächster Abschnitt.

*Noch nicht umgesetzt* (`#if` ist ein Präprozessor-Thema für sich).

### Bridges: ein C++-Interface, plattformabhängig angesteckt (Variante A)

Schichten, von oben nach unten:

```
fire-Programm  ->  Natives der Bridges (IO, Time, Devices, Graphics, später Hardware)
                     |  reine C++-Logik: Handles, IoPolicy, Paketprotokoll, Zeichenfunktionen
                     v
                   HAL-Schnittstellen  fire::hal::*   (abstrakte Klassen/Funktionstabellen, kein OS-Header)
                     |
        +------------+-------------+-----------------+
        v            v             v                 v
   platform/posix  platform/windows  platform/esp32   platform/mock (Tests)
                                  (SDL3 für Fenster und Eingabe auf dem Desktop)
```

* Die **Bridges** enthalten die Sprachanbindung und alles Plattformunabhängige; sie sehen nur die HAL-Schnittstellen.
* **HAL-Schnittstellen** (je eine kleine, ausnahmefreie Klasse, Fehler über Statuswerte): `Clock`/`Sleep`, `FileSystem`, `Console`
  (stdin/stdout/stderr), `SerialPort` und `SerialEnumerator` (Portnamen sind Sache der Plattform), `Thread`/`Mutex`/`Signal`
  (`std::thread` oder FreeRTOS-Tasks), `Window`/`Input`/`Framebuffer`, auf dem ESP32 zusätzlich `Gpio`/`I2c`/`Spi`.
* **Plattformpakete** implementieren sie und werden beim Bauen ausgewählt (CMake-Option bzw. `--target`); dadurch kostet die Schicht auf
  dem ESP32 nichts zur Laufzeit. Das heutige `loopback`-Gerät wird zur Mock-Plattform, mit der sich die Bridges ohne Hardware testen
  lassen.
* **C-ABI-Hülle** (`fire_bridge_*.h`) über den Bridges: der C#-Editor bindet dieselben Bibliotheken per P/Invoke an. Es gibt eine
  Implementierung, die C#-Bridges werden dünne Wrapper.

Reihenfolge: IO und Time (Dateisystem und Uhr), dann Devices, dann Graphics.

## ESP32 / FreeRTOS - weitere Besonderheiten

* **Speicher**: `Value` hat 16 Byte. Objektlayouts pro Klasse halten den Heap klein. Kein GC - die Ownership-Kaskade ist deterministisch.
* **Code im Flash**: erzeugtes C++ landet als normaler Code im Flash; String-Konstanten sind `static const` (Flash/DROM).
* `#extern "lib"` (dynamisches Laden) entfällt dort; `extern` wird zum statischen Bindungspunkt.
