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

### Wie Objekte übersetzt werden

* **Klasse = Klassen-Id + Feldlayout** (Basisfelder zuerst, wie in der VM). Ein Objekt ist ein `fire::Obj` (Kopf mit Klassen-Id, Besitzer,
  Geschwisterliste und der Liste der eigenen Objekte) mit den Feldern als `Value` dahinter. Der Zugriff läuft über kleine erzeugte
  Hilfsfunktionen `gf_<feld>`/`sf_<feld>`: haben alle Klassen das Feld am selben Index (der Normalfall), ist es ein fester Index, sonst
  entscheidet die Klassen-Id.
* **Methodenaufruf** über eine erzeugte Weiche je (Name, Argumentzahl): die Klassen des Programms sind bekannt (geschlossene Welt), die
  Weiche wählt nach Klassen-Id; gibt es nur eine Implementierung, wird sie direkt aufgerufen und der Compiler bettet sie ein.
* **Ownership**: Jeder Scope, der Objekte besitzen kann (er erzeugt welche oder ruft etwas auf, das eines zurückgibt), bekommt eine lokale
  `OwnList`. `new` hängt das Objekt in die Liste des innersten Scopes, `field = new X()` in die Liste des Objekts. Beim Verlassen des
  Scopes und bei `return` werden die Listen von innen nach außen zerstört (Reihenfolge der Erzeugung, erst `destruct()`, dann die
  eigenen Objekte); ein zurückgegebenes Objekt, das dem verlassenen Scope gehörte, wandert zum Aufrufer (`transferOut`/`adopt`). Scopes ohne
  Objekte kosten nichts.
* Objekte werden beim Zerstören **freigegeben**. Der Zugriff auf ein zerstörtes Objekt wird (anders als in der VM) nicht erkannt; mit
  `-DFIRE_KEEP_DESTROYED` bleiben sie im Speicher (Fehlersuche). Die Tests laufen unter AddressSanitizer/UBSan. `delete obj` und ein zweites `delete` auf
  dasselbe Objekt sind deshalb nur für Objekte erlaubt, die danach nicht mehr berührt werden.

### Speicher: Besitz für Objekte, Arrays und Puffer, Zähler für Strings und Lambdas

* **Objekte, Arrays und Puffer haben genau einen Besitzer** (SPEC 2, 2.5) und werden mit ihm zerstört: sie hängen in derselben `OwnList` (gemeinsamer
  Kopf `fire::Owned`: Vorgänger, Nachfolger, Besitzer, Art). `new int[n]`, Literale und `new byte[n]` kommen in die Liste des innersten Scopes, direkt einem
  Feld zugewiesen (`OwnValue`) in die des Objekts; `return` gibt sie an den Aufrufer weiter (`transferOut`/`adopt`), `x.Take()`, `TakeUpwards`, `TakeGlobal`,
  `TakeTo(obj)` und `delete x` sind kleine Laufzeitfunktionen (`ownMethod`, `deleteValue`). Die inneren Arrays von `new int[3][4]` und `[[1, 2], [3]]` hängen in
  der Teileliste (`parts`) des äußeren. Es gibt **keine Zähler** für diese Werte.
* **Zuweisung nach oben, Aufrufergebnisse** (SPEC 2.1): `HoistValue` wird zu `hoistFrom(v, &innerer_Scope, &Funktions_Scope)` für jeden inneren Scope der Funktion
  (nur was dem inneren Scope gehört, wandert). `OwnValue` ist bedingt: `ownValue(objekt, v, &Scope1, &Scope2, ...)` gibt den Wert nur dem Objekt, wenn er niemandem oder
  einem Scope dieser Funktion gehört. `f(g())`: der Rückgabewert von `g` reist in der Argumentliste `AL` des Aufrufs (`reownArg`), nach dem Aufruf zerstört `finishArgs`
  was die aufgerufene Funktion nicht behalten hat. **Abweichung von der VM:** dort stirbt der Wert mit dem Scope der aufgerufenen Funktion (vor deren übrigen
  Variablen, auch beim Auslösen einer Ausnahme), nativ erst nach dem Aufruf - die Reihenfolge von Destruktor-Ausgaben kann sich dabei unterscheiden.
* **Handle-Tabelle gegen hängende Referenzen.** Ein zerstörtes Array darf nicht mehr benutzt werden (SPEC 2.5). In den geprüften Modi trägt der `Value` eines
  Arrays/Puffers den Platz seines Kopfs in einer Tabelle von Generationen (`unit` = Platz, `reserved` = Generation); beim Zerstören bekommt der Platz eine neue
  Generation, jeder Zugriff vergleicht (`leafAlive`: eine Ladung und ein Vergleich) und meldet sonst die fangbare `DestroyedException`, bevor freigegebener
  Speicher berührt wird. Mit `#performance` definiert der Generator `FIRE_UNCHECKED`: keine Tabelle, keine Prüfung (undefiniert, wie in der VM-Doku).
  **Objekte** haben (noch) kein Handle: der Zugriff auf ein zerstörtes Objekt wird nicht erkannt (die VM lässt ihn zu).
* **Strings und Lambdas werden gezählt** (sie werden frei weitergegeben: `var t = s`, in Feldern und Arrays gespeichert, zurückgegeben):
  * **Speicherorte halten einen Zähler**: Variablen, Parameter, Felder, Array-Elemente und statische Felder (`retain` beim Speichern, `release` beim
    Überschreiben, beim Verlassen des Scopes und beim Zerstören des Objekts/Arrays). Stack-Zwischenwerte halten nichts.
  * **Frische Werte** (Verkettung, Methoden von `string`, ...) kommen mit Zähler 1 in den **temporären Pool**; jeder Scope merkt sich dessen
    Höhe beim Eintritt und gibt beim Verlassen alles darüber frei. Ein Zwischenwert lebt also bis zum Ende seines Scopes.
  * **`return`** hält den Rückgabewert mit einem Zähler fest, der Aufrufer übernimmt ihn in seinen Pool (`adopt`).
  * **Konstanten** liegen statisch und sind unsterblich (nie gezählt).
  * Der Pool ist ein einfaches Array statt einer verketteten Liste: derselbe Wert darf mehrfach darin stehen (ein Fehler dieser Art wurde
    durch AddressSanitizer gefunden).
* **"Kann eine Referenz sein"** leitet der Generator für jede Variable und jeden Stack-Platz ab (Konstanten, Zahlenrechnung und Vergleiche sind
  es nie; Parameter, Aufrufergebnisse, Feld- und Array-Lesungen schon). Nur dann entstehen `retain`/`release` (für Strings und Lambdas) und das stringfähige
  `addR` statt `add`. Reine Zahlenschleifen sind deshalb so schnell wie zuvor.
* `TakeUpwards` braucht den Elternscope: nutzt ein Programm die Methode, bekommt jeder Scope eine Liste und kennt seinen Eltern (`OwnList::parent`; ein
  Funktions-Scope hat den globalen Scope als Eltern, wie in der VM).

Bekannte Abweichungen zur VM: Unicode-Klassifizierung und Groß-/Kleinschreibung nur für Basic Latin, Latin-1, Griechisch und Kyrillisch
(`char.IsDigit` nur ASCII-Ziffern); `print(objekt)` ohne `ToString()` schreibt `<object>`; das Zahlenformat `E` fehlt.

### Regeln für die Runtime (gelernt)

* **Alle Funktionen nehmen `Value` per Wert**, nie per Referenz, und die Fehlerpfade nur die Operandenarten. Eine Referenz auf eine
  Variable, die an einen nicht eingebetteten Aufruf geht, lässt die Variable "entkommen": der Compiler hält sie dann - und weil der
  Generator die Stack-Variablen wiederverwendet, *alle* - im Speicher statt in Registern. Das allein kostete Faktor 5-10.
* Kein C++-`throw`, kein RTTI (ESP32-Toolchains schalten beides oft ab). Ausnahmen der Sprache laufen über einen eigenen Mechanismus
  (siehe "Ausnahmen"); Fehler der Runtime, die die VM nicht als Ausnahme meldet, beenden das Programm mit Exitcode 1.
* Reines C++17, `std::to_chars` für die Zahlenausgabe (kürzeste Darstellung wie .NET).

## Ausnahmen

`throw` rollt den Stack nicht selbst ab. Der Handler-Stack (`g_handlers`, eine verkettete Liste von `Handler`-Objekten in den Frames der
Funktionen mit `try`) wird von oben durchsucht:

* **Passender `catch`**: sein Block läuft *auf* dem Stack der Wurfstelle (ein Aufruf in die Funktion, die den `try` besitzt). Die Frames der
  Wurfstelle leben also noch, und `e.resume(wert)` kann dort weitermachen: das `throw` liefert `wert`. Dafür ist jeder `try` mit `catch` ein
  C++-Lambda (`C{n}`), das die Variablen der Funktion per Referenz sieht. Seine eigenen Operanden-Slots heißen `c{n}_{k}`, damit sie die der
  Wurfstelle nicht überschreiben.
* **Kein passender `catch`** (oder nur ein `finally`): die Frames oberhalb werden über ein Statusflag abgerollt (`g_unwind`): jede erzeugte
  Funktion prüft es nach einem Aufruf, der werfen kann (`if (g_unwind.active) goto U..`), verlässt ihre Scopes (Destruktoren laufen) und kehrt zurück.
  In der Funktion des `try` führt der Weg zur **Landestelle** (`LAND{n}`), die das `finally` ausführt und weiterwirft.
* **Ein `catch`-Block endet** mit `exitJump` (Sprung hinter den `try`, `break`/`continue` aus dem Block, Sprung ins `finally`) oder `exitReturn`
  (`return` im `catch`): das Lambda kehrt zurück, die Frames der Wurfstelle rollen sich ab (erst jetzt laufen ihre Destruktoren, wie in der VM
  nach `ClearPendingResume`), und die Landestelle der Funktion macht dort weiter. Operanden, die der Sprung mitnimmt (der Abschluss eines `finally`),
  reisen in `g_unwind.regs`.
* **`finally`**: ein Block pro `try`, mit dem Abschluss (Nutzlast, Art) in zwei Operanden-Slots wie im Bytecode (`EnterFinallyNormal`, `PushJump`,
  `EndFinally`). `return` durch ein `finally` läuft statisch: der Generator kennt die offenen Handler an der Stelle (`Flow.Handlers`) und springt
  ins `finally`; `EndFinally` setzt die Rückkehr fort.

Der Generator fügt die Prüfungen nur ein, wenn das Programm überhaupt `throw` oder `try` enthält (`FIRE_EXCEPTIONS`); sonst ändert sich der
erzeugte Code nicht. Typisiertes `catch` ordnet die Klasse über eine erzeugte Tabelle zu (`excMatches`: Klasse, Basisklassen, Interfaces).

Bekannte Abweichungen zur VM:

* `resume` geht nur an die Wurfstelle selbst. Läuft die Ausnahme an einem `try` ohne passenden `catch` vorbei (oder durch ein `finally`), ist die
  Wurfstelle schon abgerollt; `resume` meldet dann einen Fehler (die VM setzt dort an einer verschobenen Stelle fort).
* Ein Objekt, das erst im `catch` mit `new` entsteht, kann nicht an `resume` übergeben werden (es gehört dem `catch`-Scope, der endet).
* Die Destruktoren des `catch`-Scopes laufen vor denen der Wurfstelle (die VM umgekehrt).
* `resume` nach einer `UnitMismatchException` setzt hinter dem `CheckUnit` fort; die VM schiebt dort einen zusätzlichen Wert auf den Stack.
* Eine Ausnahme, die ein Destruktor wirft, wird beim Abrollen ignoriert.

## Messwerte

Rechner dieser Sitzung, g++ 13 `-O2`, VM im Modus *Performance*, Zeiten ohne Prozessstart (1,3 ms):

| Benchmark | VM | erzeugt (g++ -O2) | Faktor |
|---|---:|---:|---:|
| `loop` (1,5 Mio. Iterationen, Int) | 158 ms | 2,3 ms | ~68x |
| `float` (600 000 Iterationen) | 99 ms | 5,3 ms | ~19x |
| `fib` (rekursiv, `Fib(23)`) | 29 ms | 1,3 ms | ~21x |
| `method` (250 000 Methodenaufrufe, Felder) | 59 ms | 2,6 ms | ~23x |
| `array` (Array füllen und summieren) | 126 ms | 4,6 ms | ~28x |
| `string` (Verkettung, Zeichenkettenmethoden) | 39 ms | 1,9 ms | ~21x |
| `alloc` (60 000 Objekte erzeugen und freigeben) | 38 ms | 1,9 ms | ~20x |
| `list` (`List` aus dem Prelude, `foreach`) | 44 ms | 3,4 ms | ~13x |

Damit laufen alle neun Benchmarks nativ (`lambda` siehe unten). Mit clang++ sind die Werte ähnlich.

*Handgeschrieben generisch* heißt: alles bleibt ein getaggter `Value`, so wie es der Generator erzeugt; mit nackten `int64`/`double`
(Typinferenz) wird `fib` weitere ~4x schneller (`native/spike/spike.cpp`). Die Zahlen sind Momentaufnahmen von einer
einzigen Maschine, keine Garantie.

## Stand

Übersetzt wird eine Teilmenge der ISA:

* Konstanten (Int/Float/Bool/Char/String/Undefined, mit Einheit), `Pop`/`Dup`/`Swap`
* Lokale und globale Variablen, Blockscopes, verschmolzene Instruktionen (`StoreLocalPop`, `JumpIfNotLt`, `ArithLocalConstPop` ...)
* `+ - * / %`, Bit-Operationen, Vergleiche, `&&`, `||`, `!`, `if`/`while`/`for`/`do`, `break`/`continue`
* Statische Methoden (inkl. Rekursion), `print`
* **Objekte**: Klassen mit Feldern und Feld-Initialisierern, Konstruktoren (inkl. `base(...)`), Methoden mit virtuellem Aufruf,
  `base.Methode()`, statische Felder, Vererbung, Destruktoren (abgeleitete Klasse zuerst), **Ownership** (SPEC 2): Besitz durch Scopes und
  Objekte, Kaskadenlöschung beim Verlassen eines Scopes und bei `return`, Übergabe eines zurückgegebenen Objekts an den Aufrufer
* Einheiten: Rechnen und Vergleichen **gleicher** Einheiten
* Float-Genauigkeit 32 oder 64 Bit (`#floatwidth`, `-f`), identisch zur VM

* **Strings** (UTF-16 wie in der VM): Konstanten, `+` mit allen Wertarten (inkl. `ToString()` von Klassen), `$"{x:F2}"` (Formate `X`, `D`, `B`, `F`),
  `.Length`, Indexierung, alle Methoden von `string` (`IndexOf`, `LastIndexOf`, `Substring`, `CharAt`, `Contains`, `StartsWith`, `EndsWith`,
  `ToUpper`, `ToLower`, `Trim*`, `Replace`, `Split`, `PadLeft`, `PadRight`) und von `char`
* **Arrays und Puffer**: `new T[n]`, Literale, Zugriff und Zuweisung, `++` auf Elementen, `length`, verschachtelte (gezackte) Arrays,
  `byte[]`, `foreach` über Arrays, Index-Methoden von Klassen (`GetIndex`/`SetIndex`) und damit `List` aus dem Prelude; als Teil des Besitzmodells
  (SPEC 2.5): `Take()`, `TakeUpwards()`, `TakeGlobal()`, `TakeTo(obj)`, `delete x`, `DestroyedException`

* **Lambdas**: `func (x) => ...`, Kurzschreibweisen, Captures als Kopie (SPEC 4.2.1), `on`-Ziel, verschachtelte Lambdas, Signaturprüfung
  (`lambda<...>`), Einheitenprüfung (`CheckUnit`). Ein Lambda ist ein referenzgezählter Wert (Funktionszeiger, kopierte Captures, `on`-Ziel); die
  Captures sind Variablen hinter den Parametern. Standard-Parameterwerte bei Lambdas sind noch nicht übersetzt.

* **Ausnahmen** (SPEC 7): `throw` (auch als Ausdruck), `try`/`catch` (nach Typ, `catch (e)`, mehrere Klauseln, `catch` ohne `try`), `finally` auf
  jedem Weg (normal, `break`, `continue`, `return`, Ausnahme, aus dem `catch` heraus), Weiterwerfen, `e.resume(wert)` an der Wurfstelle, auch aus
  verschachtelten Aufrufen heraus. Die Fehler der Sprache sind fangbar: `IndexOutOfBoundsException` (Array, Puffer, Zeichenkette, Methoden von `string`)
  und `UnitMismatchException` (`CheckUnit`, Felder mit Einheit). Eine nicht gefangene Ausnahme meldet die Klasse auf stderr und beendet
  das Programm mit Exitcode 1, ohne Abwickeln - wie die VM.

* **`ref`-Parameter** (SPEC 5.4.2): der Aufrufer übergibt einen Zeiger auf die Variable (`PtrV(&B3_0)`), das Feld (`fp_name`) oder das
  Array-/Puffer-Element (`addressOfIndex`); der Parameter liest und schreibt durch ihn (`ptrRead`/`ptrWrite`, der Speicher zählt mit). Eine Variable,
  deren Adresse genommen wird, ist ab dann im Speicher (nur diese Funktion wird langsamer). Eine Methode ohne `ref` an derselben Stelle bekommt den
  Wert: bei bekanntem Ziel setzt der Aufrufer `ptrRead`, beim virtuellen Aufruf der Dispatcher (`derefArg`).

Noch nicht (der Generator meldet es mit Namen): Zeiger (`unsafe`), `flat`/`copy` als Argument, Threads, Reflection, Properties,
Operator-Überladung, `copy`/`flat`, die eingebauten Objektmethoden (`TakeTo`, `TakeUpwards`, `TakeGlobal`), Standardargumente,
Einheiten-Algebra und implizites Einheiten-Coercing, das Zahlenformat `E`, `extern`, die Bridges.

Getestet wird per **Differential-Test** (`fire.Testing`, Block "Native-Backend"): jeder Fall läuft in der VM und als erzeugtes
C++ (g++/clang++, mit `-Wall -Wextra`, ohne Warnung), die Ausgabe muss gleich sein.

## Roadmap

1. **Sprachumfang**: Objekte/Felder/Methoden (Layout pro Klasse statt Dictionary), Strings, Arrays, Lambdas (Closure-Conversion),
   Einheiten-Algebra (Tabelle zur Übersetzungszeit, Konvertierungsfaktoren als Konstanten).
2. **Ownership**: Scopes, die besitzende Objekte halten, behalten eine Laufzeit-Scope-Kette (für die Destruktor-Kaskade); alle anderen
   bleiben aufgelöst.
3. ~~**Ausnahmen mit Resume**~~ - umgesetzt, siehe "Ausnahmen".
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
