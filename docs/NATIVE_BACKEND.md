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
7. **ESP32**: siehe unten.
8. **Editor**: `Run -> Build Native`, Toolchain-Erkennung.

## ESP32 / FreeRTOS - Besonderheiten, die früh entschieden werden müssen

* **`double`**: Der klassische ESP32 und der S3 haben nur eine Single-Precision-FPU, `double` ist Software. Typinferenz sollte für
  `float` mit Bitbreite 32 echtes `float` erzeugen und für Genauigkeits-Default (W64) ein Übersetzungsschalter (`--float32`) erlauben.
* **Stack**: Rekursion läuft jetzt auf dem C++-Stack (die VM nutzt einen Heap-Stack). FreeRTOS-Tasks haben feste Stacks - die Größe
  pro `fire`-Thread muss wählbar sein, und eine Rekursionstiefenprüfung gehört in den Funktionsprolog.
* **Speicher**: `Value` hat 16 Byte. Auf 32-Bit-Zielen reicht das für Zeiger und `int64`; Objektlayouts pro Klasse halten den
  Heap klein. Kein GC - die Ownership-Kaskade ist deterministisch.
* **Code im Flash**: erzeugtes C++ landet als normaler Code im Flash; String-Konstanten sind `static const` (Flash/DROM).
* `#extern "lib"` (dynamisches Laden) entfällt dort; `extern` wird zum statischen Bindungspunkt.
