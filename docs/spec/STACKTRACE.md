# Stack-Traces für Ausnahmen

Status: **Konzept** (20.11.2026), nichts davon ist gebaut. Heute hat eine Ausnahme keinen Stack-Trace: `print(e)` gibt `<object NotFound>`, eine
unbehandelte Ausnahme schreibt eine Zeile (`Unhandled exception of type 'NotFound': not found: b`, [Exceptions](../language/14-exceptions.md)). Dieses
Dokument legt fest, was ein Trace ist, wo er entsteht, was die Sprache davon zeigt, was er in jedem Ausführungsweg kostet (gemessen, [unten](#5-kosten-im-nativen-build-gemessen))
und in welchen Schritten er kommt.

## 1. Was es heute gibt

| Weg | Frames zur Laufzeit | Zeile je Frame | Trace heute |
|---|---|---|---|
| **C#-VM** (`src/fire.Runtime/VM.cs`) | `_frames` (`CallFrame`: Rücksprung-Chunk und -IP, Scope, `this`) | `Chunk.GetLocation(ip)` über die Zeilentabelle | nur unter dem Debugger von spark: `CaptureErrorTrace` (`VmDebugTarget`), `RecordThrowTrace` bei jedem `throw`, `UnhandledTrace` (Quellindex, Zeile; ohne Funktionsnamen) |
| **C-VM** (`native/vm`) | `VmFrame` auf dem Stapel der VM (Chunk, IP, `proto`) | die Zeilentabellen des Chunks (`VmChunk.lines`) - **nur wenn die `.fbc` sie hat**: `forge bytecode` ja, ein gepacktes Programm (`forge build --engine vm`) nein | nur im Debugger (`vm_debug.c`, Frames für den Agenten) |
| **nativ Release/Performance** (`CppGenerator`, `fire_rt.h`) | keine; nur der C-Stack | `#line` vor jeder Instruktion (für gdb und die Meldungen des C-Compilers, [NATIVE_BACKEND.md](NATIVE_BACKEND.md)) | keiner |
| **nativ Debug** (`CppGenerator.Debug.cs`, `fire_debug.h`; in forge-c `native/forge/gen/debug.c`) | Schatten-Stack `DbgFrame` je fire-Funktion (Wrapper `fN` → `fN_b`) | `FIRE_DBG_L(quelle, zeile, zustand)` am Anfang jedes Bereichs der Zeilentabelle und an jedem Sprungziel | im Debugger (`NativeDebugTarget.UnhandledTrace` fragt den angehaltenen Thread) |

spark zeigt den Trace des Debuggers schon an: im Dialog einer unbehandelten Ausnahme (`ReportRuntimeErrorAsync`: "Stack (innermost first): at <datei>line N")
und im Output-Panel (`AfterStep`). Ohne Debugger - `fire run`, ein gebautes Programm, `forge test`, ein Absturz beim Kunden - gibt es nichts.

**Was die Sache einfach macht.** In allen vier Wegen läuft ein `catch` **auf der Wurfstelle** (für `resume`): nativ ruft `throwValueR` den Catch-Block als
Funktion oben auf dem Stack der Wurfstelle auf, die C-VM über den Handler-Stapel der Runtime ebenso, die C#-VM friert die Frames der Wurfstelle ein
(`CaptureContinuation`). Und eine Ausnahme, die nichts fängt, beendet das Programm **an der Wurfstelle**, ohne abzurollen (`reportUnhandled`, `exitNow(1)`).
Beim `throw` ist der ganze Stack also noch da - **dort** wird der Trace genommen, an genau einer Stelle je Weg: `throwValueR` (nativ und C-VM),
`VM.ThrowException` (C#-VM).

**Was nicht geht**: den Trace erst beim Abrollen sammeln (jede Funktion hängt ihre Zeile an, wenn sie nach einem Aufruf `g_unwind.active` sieht - das
kostete auf dem heißen Weg nichts). Ein passender `catch` läuft, bevor irgendetwas abgerollt ist, und eine unbehandelte Ausnahme rollt gar nicht ab; ein
Programm ohne `try` hat außerdem keine Prüfungen (`FIRE_EXCEPTIONS`).

## 2. Was der Benutzer sieht

### Das Format

```
Unhandled exception of type 'NotFound': not found: b
   at Store.Find (main.fire:16)
   at Store.Lookup (main.fire:21)
   at <main> (main.fire:30)
```

* Die **erste Zeile bleibt**, wie sie ist (der Text von `UncaughtScriptException` und `reportUnhandledIn`); darunter ein Frame je Zeile, der innerste zuerst,
  eingerückt mit drei Leerzeichen und `at`. Englisch wie alle Meldungen der Runtime.
* **Funktion**: die Namen des Debug-Builds (`CppGenerator.Debug.cs`, `DebugFuncName`): `Klasse.Methode`, `Klasse.construct`, `Klasse.destruct`, `<main>`,
  `<fire>` (der Rumpf eines `fire`-Blocks), `<lambda>`, `Klasse.<getter>` usw. Generische Methoden ohne Typargumente (`List.Add`, nicht `List<int>.Add`) - die
  C#-VM kennt die Spezialisierung nicht in jedem Fall, und der Trace muss in allen Wegen gleich sein.
* **Ort**: `(datei:zeile)`. Die Datei ist der Name **relativ zum Ordner der ersten Quelle** (des Projekts), so wie er im Trace jeder Maschine gleich aussieht
  (`main.fire`, `lib/util.fire`); eine Quelle ohne Datei `fire:source/N`. Code des Preludes und der Preludes der Imports: `(prelude)` ohne Zeile - der Benutzer
  kann mit der Zeile nichts anfangen, und sie ändert sich mit jeder Version.
* **Tiefe**: höchstens 64 Frames; bei mehr die innersten 48, eine Zeile `   ... 1234 more frames ...` und die äußersten 16 (eine Endlosrekursion soll nicht
  tausende Zeilen schreiben und beim Wurf nicht tausende Frames kopieren). Dieselbe Regel in allen Wegen.
* **Ohne Zeilen** (ein Build mit `#stacktrace calls`, [unten](#7-empfehlung-je-modus-und-die-direktive)): `   at Store.Find (main.fire)`.
* **Ohne Trace** (`#stacktrace off`, oder ein Wurf, dessen Frames nicht bekannt sind): nur die erste Zeile, wie heute.

### Die Sprache

```fire
try {
    Store.Lookup("b")
} catch (NotFound e) {
    print(e)                         // NotFound: not found: b
    print(e.StackTrace)              // die Zeilen "   at ..." wie oben, ohne die erste
    foreach (f in e.Frames) {        // StackFrame: function, file, line
        print(f.function + " " + f.file + ":" + f.line)
    }
}
```

| Mitglied | Typ | Bedeutung |
|---|---|---|
| `e.StackTrace` | `string` (Property, nur lesen) | der Trace als Text im Format oben, ohne die Kopfzeile; `""` für eine Ausnahme, die noch nie geworfen wurde oder ohne Trace gebaut ist |
| `e.Frames` | `StackFrame[]` (Property, nur lesen) | dieselben Frames als Records; `[]` wie oben |
| `StackFrame` | `record` im Prelude: `string function`, `string file` (voller Pfad wie `LinkedProgram.SourceFiles`, `""` für den Prelude), `int line` (0: unbekannt) | |
| `e.ToString()` | `string` | neu: `"Klasse: message"` (heute `<object Klasse>`). Ohne Trace - eine Ausgabe, die ein Test vergleicht, ändert sich nicht mit der Zeile |

`StackTrace` und `Frames` sind **keine Felder**: der Trace hängt in der Runtime am Objekt (siehe [3](#3-der-trace-in-der-runtime)); Reflection
(`Reflect.Fields`), JSON, `==` von Records und `print` sehen ihn nicht. Eine eigene Ausnahmeklasse muss nichts tun.

### Wo er erscheint

| Ort | Was |
|---|---|
| **unbehandelt, Konsole** (VM, nativ, `fire`, gepacktes Programm) | stderr: Kopfzeile und Trace; Exitcode 1 wie heute |
| **unbehandelt in einem Thread** ohne `catch threads` | dasselbe, mit dem Thread-Abschnitt ([4](#4-wo-der-trace-entsteht)) |
| **`print(e)`, `"" + e`** | nur `Klasse: message`; den Trace druckt man ausdrücklich (`print(e.StackTrace)`) |
| **spark, Output-Panel** | die Zeilen `at ... (datei:zeile)` sind **klickbar** (wie die Meldungen im Build-Log, `BuildLogPanelControl.Located`): ein neues Muster `\((?<file>[^():]+):(?<line>\d+)\)\s*$`, die Datei relativ zum Ordner der ersten Quelle des Laufs; Frames des Preludes nicht |
| **spark, Dialog einer unbehandelten Ausnahme** | wie heute, aber mit Funktionsnamen (heute nur `at <datei>line N`); Doppelklick auf eine Zeile springt dorthin. Unter dem Debugger bleibt das Stack-Panel der lebenden Frames, wie es ist |
| **`forge test`** | unter `ERROR`/`FAILED` (die Nachricht wird schon zeilenweise eingerückt, `TestRunner`) der Trace bis zur Testmethode: die Frames des Test-Treibers (`TestDriver`) und von `Testing.Assert` fallen weg - bei `FAILED` steht oben die Zeile des `Assert` im Test. Der Test-Explorer von spark zeigt ihn im Detail des Tests, klickbar |
| **der Debugger** | `stopped` mit Grund `unhandled` bekommt kein neues Feld: spark fragt die lebenden Frames wie heute. Neu ist nur, dass auch ein Release-Build ohne Debugger einen Trace druckt |

## 3. Der Trace in der Runtime

**Inhalt.** Ein Trace ist eine unveränderliche Liste von Frames `(Funktion, Zeile)` - die Funktion als Zeiger auf einen statischen Deskriptor
`FireFnInfo { const char* name; const char* file; }` (nativ: erzeugt je Funktion, in dem Modul, das sie enthält; C-VM: aus `VmProto` und der Quelle des
Chunks; C#-VM: aus `FunctionProto`/`Chunk`), die Zeile als `int32_t`. Formatiert wird erst, wenn jemand `StackTrace`, `Frames` liest oder die Ausnahme
unbehandelt endet - der Wurf selbst kopiert nur die Paare.

**Am Objekt.** Ein Block `FireTrace { refcount (atomar); n; omitted; frames[n]; next }` (`next`: der nächste Abschnitt, siehe `await` unten), auf den ein
verborgener Slot des Ausnahme-Objekts zeigt - nativ ein Wort hinter den Feldern von `Exception`, das der Generator in das Layout schreibt, aber nicht in
`fieldInfo`/die Reflection; C#-VM eine Eigenschaft von `ObjectInstance` für Objekte einer Ausnahmeklasse. Er ist unveränderlich: eine Kopie der Ausnahme
(`taking`, die Grenze eines Threads, `Completion.Fail`, ein Wert in einem Kanal) teilt ihn und zählt hoch; das Zerstören der Ausnahme zählt herunter. Der
Kopierer der Objekte (`ObjectCopier`, nativ `transferTree`/die Kopie über Threads) kennt die Ausnahmeklassen über ein Bit im Deskriptor der Klasse.

**Wann er gesetzt wird.** Beim **ersten** `throw` eines Objekts, in `throwValueR` (nativ, C-VM) und `VM.ThrowException` (C#-VM), bevor ein Handler gesucht wird
- also auch für die Ausnahmen der Runtime (`IndexOutOfBoundsException` aus `arrayGet`, `DivideByZeroException`, `DestroyedException`, ...), die alle dort
durchgehen. Hat das Objekt schon einen Trace, bleibt er (siehe Rethrow).

## 4. Wo der Trace entsteht

| Fall | Was der Trace zeigt |
|---|---|
| `throw new X()` | die Wurfstelle bis zum Anfang des Threads (des Programms: `<main>`) |
| ein Fehler der Runtime in einer Instruktion | dasselbe; die Zeile des obersten Frames ist die Zeile der Instruktion, nicht die einer Funktion der Runtime (die Runtime hat keine Frames) |
| **Rethrow** `catch (e) { ...; throw e }` | der **ursprüngliche** Trace (wie `throw;` in C#, nicht wie `throw e`): eine Ausnahme bekommt ihren Trace nur beim ersten Wurf. Wer einen neuen will, wirft eine neue Ausnahme |
| eine Ausnahme passiert ein `finally` (`UW_RETHROW`, `FinallyThrow`) | unverändert - das ist intern derselbe Wurf |
| `resume` | der Trace bleibt am Objekt; das Programm geht an der Wurfstelle weiter |
| ein `throw` **in** einem Catch-Block | der Stack ab der Funktion, der der `catch` gehört - **nicht** die eingefrorenen Frames der ersten Wurfstelle, über denen der Catch-Block läuft. Nativ: der Frame des Catch-Blocks hat als Vorgänger den Frame der Funktion mit dem `try` (wie im Debug-Build: `catchName` setzt `g_dbgTop = own_`), C-VM: das Laufen über `VmFrame` springt vom Catch-Frame (`rec`) zur Tiefe des Handlers, C#-VM: `_frames` ist schon so |
| ein Destruktor, ein Getter, ein Operator, `ToString()` (was die VM "verschachtelt" ausführt) | sie sind gewöhnliche Frames (`Klasse.destruct`, `Klasse.<getter>`); ein Destruktor beim Abrollen wirft ohnehin ins Leere (NATIVE_BACKEND "Bekannte Abweichungen") |
| **ein Native, das ein Lambda zurückruft** (Ereignis eines Fensters während `win.Tick()`, ein Lambda an ein Paket) | die Frames des Lambdas, darunter **ein Frame für das Native** (`at Window.Tick (native)`), darunter der fire-Code, der das Native rief. Die Ausnahme wird beim Aufruf des Natives geworfen (SPEC 8.1.4) - ein zweiter Wurf desselben Objekts, der Trace bleibt der aus dem Lambda. Den Frame des Natives legt der Weg der Rückrufe in der Runtime (`callLam` aus einem Native, `fire_host` der Pakete) auf; ein Native, das nicht zurückruft, hat keinen |
| ein Rückruf aus einem **fremden Thread** eines Pakets (`fire_strandAttach`), an einem sicheren Punkt | der Trace beginnt am Lambda: `at <lambda> (ui.fire:12)` / `   (called from native code)` |
| **`fire { ... }`, `fire M()`**, ein Job von `fire global`, unbehandelt | die Frames des Threads bis `<fire>` bzw. `M`; darunter eine Zeile `   in thread 'Fire #1', started at main.fire:40` - der Ort des `fire` ist eine Konstante, die der Generator dem Start des Threads mitgibt (kein Trace des Erzeugers: das kostete bei jedem Start). `catch threads(e)` sieht denselben Trace an seiner Kopie |
| **`await M()`**, `await [ ... ]`, `await actor.M()`, `Completion.Wait()` | der Trace der Ausnahme aus dem Task, dann ein zweiter Abschnitt: `   --- awaited at ---` und der Stack des Wartenden an der Stelle des `await`. Der Wurf beim Wartenden ist ein Wurf eines Objekts mit Trace: statt ihn zu ersetzen, hängt die Runtime **an diesen Stellen** einen Abschnitt an (`FireTrace.next`, höchstens 4 Abschnitte). `Completion.Fail(e)` mit einer nie geworfenen Ausnahme: ihr Trace entsteht erst beim `Wait()` |
| `until`/Abbruch (`CancelledException`, `TimeoutException`) | die Stelle des Wartens, an der der Task den Abbruch bemerkt (`Sleep`, `Receive`, ...) |
| ein Kanal | Ausnahmen gehen nicht durch Kanäle, nur Werte; eine Ausnahme als Wert (`Channel<Exception>`) behält ihren Trace wie bei jeder Kopie |
| **Module** (Linkage `static`/`shared`, Plugins, Pakete in fire) | die Frames einer Bibliothek wie die des Programms, mit ihren Dateien (relativ zum Paket: `(http:server.fire:88)` - der Name des Pakets vor der Datei). `FireFnInfo` liegt im Modul; der Zeiger gilt, solange das Modul geladen ist. Ein Plugin wird nicht entladen; käme das, müsste es die Namen seiner Frames in die Traces kopieren, die noch leben |
| ein Modul, das **ohne** Frames gebaut ist (älterer Cache, `#stacktrace off` in der Bibliothek) | seine Funktionen fehlen im Trace, der Rest stimmt (der Stapel der Frames gehört der Runtime; ein Modul ohne legt nur keine auf). Die Einstellung gehört deshalb zum Schlüssel des Caches der statischen Bibliotheken |

## 5. Kosten im nativen Build, gemessen

### Die Kandidaten

| | Mechanismus | Was er beim Aufruf kostet | Zeilen |
|---|---|---|---|
| **(a1)** | Schatten-Frame je Aufruf (`prev`, Funktion, Zeile), Zeile am Anfang jeder Zeile (wo der Debug-Build `FIRE_DBG_L` hat) | Frame auflegen/abnehmen (3 Speicherzugriffe und der Zeiger auf den obersten Frame) + ein Speichern je Zeile | genau die der VM (dieselben Stellen wie die Zeilentabelle) |
| **(a2)** | wie (a1), die Zeile nur vor Zeilen mit einem Aufruf | wie (a1), weniger Speicherungen | ungenau: ein Fehler der Runtime in einer Zeile ohne Aufruf hätte die Zeile davor - nur in Verbindung mit einer Zeile an jeder Stelle, die werfen kann, richtig, und das sind fast alle |
| **(b)** | nur der Frame (Funktion), keine Zeile | Frame auflegen/abnehmen | keine - Zeilen nur über (c) |
| **(c)** | Abwickeln der Plattform (`backtrace()`/libgcc, libunwind, DbgHelp) über `.eh_frame`, Zeilen aus den Debug-Infos (`#line` steht schon in jedem Modus im C) | nichts | aus DWARF (`-g1`), nur wo es die gibt |
| **(d)** | nur im Debug-Build (der Schatten-Stack ist schon da) | Release nichts | im Debug wie (a1) |

### Methode

Die 9 Programme aus `tests/bench` (die von `fire.Benchmarks`), mit vergrößerten Zählern (je rund 50-mal: `Fib(31)`, 3 Mio. Objekte, ...), damit der Lauf
nicht im Start des Prozesses untergeht; generiert mit dem C#-forge (`forge native x.fire -m release|performance`). Die Mechanismen (a1), (a2), (b) sind
**in das erzeugte C eingesetzt** (ein Skript: Frame am Anfang jeder fire-Funktion `fN`/`fN_c`/`fire_main`, Abnehmen vor jedem `return`, die Zeile nach den
`#line`-Direktiven), nicht im Generator gebaut - die Zahlen sind also die des Mechanismus ohne die Optimierungen von unten. Der Zeiger auf den obersten Frame
ist eine globale Variable (Programme ohne Threads; mit Threads siehe unten). Die Ausgaben aller Varianten sind gleich.

Gezählt sind **Instruktionen** (`valgrind --tool=callgrind`, deterministisch) und die **Zeit** (Minimum von 7-9 Läufen, Benutzer- + Systemzeit, verschränkt). Die
Maschine war belastet (4 Kerne, andere Builds, load 2-3): die Zeiten schwanken um ±8 %, die Instruktionen nicht. gcc 13.3 `-O2`, tcc 0.9.27, Linux x64.

### Release, gcc -O2

| Programm | Instr. ohne (Mio.) | (b) Frame | (a2) + Zeile an Aufrufen | (a1) + Zeile je Zeile | Zeit ohne | (b) | (a2) | (a1) |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| alloc | 921 | ±0 % | +0,7 % | +0,7 % | 51,5 ms | 50,1 | 49,9 | 49,8 |
| array | 1841 | ±0 % | +1,1 % | ±0 % | 132,6 ms | 132,3 | 131,7 | 128,5 |
| fib | 38 | **+197 %** | +197 % | +197 % | 3,0 ms | 8,9 | 8,9 | 9,0 |
| float | 330 | ±0 % | ±0 % | ±0 % | 141,3 ms | 154,7 | 159,5 | 149,3 |
| lambda | 2240 | +3,1 % | +4,0 % | +4,0 % | 189,0 ms | 189,5 | 188,7 | 192,2 |
| list | 1689 | +4,6 % | +6,6 % | +6,4 % | 203,4 ms | 202,4 | 211,7 | 205,2 |
| loop | 1050 | ±0 % | ±0 % | ±0 % | 53,3 ms | 52,1 | 52,1 | 51,6 |
| method | 87 | ±0 % | ±0 % | ±0 % | 5,5 ms | 5,5 | 5,5 | 5,5 |
| string | 877 | +3,4 % | +4,1 % | +4,1 % | 45,6 ms | 46,8 | 48,4 | 47,9 |
| **ohne fib** | **9035** | **+2,0 %** | **+2,8 %** | **+2,6 %** | | | | |

* Wo die Schleife in `<main>` steht oder gcc die Methode einbettet (`float`, `loop`, `method`: `Counter.Inc` wird eingebettet, der Frame fällt mit dem Einbetten weg),
  kostet es nichts. Wo fire-Funktionen aufgerufen werden, die etwas tun (`lambda`, `list`, `string`: Aufrufe über die Runtime, der Prelude), 3-7 % Instruktionen.
* **`fib` ist der Ausreißer**: `M.Fib` ist eine reine C-Funktion (`f3_c`, Ebene 2, nur `int`-Arithmetik), die gcc ohne Frame zu einer engen Rekursion umbaut;
  der Frame, dessen Adresse eine globale Variable bekommt, verhindert das (dreimal so viele Instruktionen und dreimal die Zeit). Abhilfe unten: eine
  Funktion, die **nicht werfen kann** und nur solche aufruft, braucht keinen Frame - aus ihr kommt nie ein Trace. Dann ist `fib` wieder bei 0 %.
* (a1) kostet kaum mehr als (a2): die Speicherungen der Zeile gehen in einen Frame auf dem Stack, gcc fasst aufeinanderfolgende zusammen. (a2) ist **nicht** billiger
  genug, um seine falschen Zeilen zu rechtfertigen.
* `float` braucht in allen Varianten genau dieselben Instruktionen; die 6-13 % mehr Zeit sind die Schwankung (ein erster Durchgang gab -2 % bis +7 %) oder die
  Ausrichtung des Codes - die Zeiten einzelner Programme sind hier nur auf etwa ±8 % genau, die Instruktionen sind die Grundlage.

### Release, tcc

| Programm | Instr. ohne (Mio.) | (b) | (a2) | (a1) | Zeit ohne | (b) | (a2) | (a1) |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| alloc | 6810 | +0,7 % | +0,9 % | +1,0 % | 673,5 ms | 669,8 | 670,1 | 725,1 |
| array | 15743 | ±0 % | +0,3 % | ±0 % | 1807 ms | 1860 | 1791 | 1710 |
| fib | 192 | **+34,9 %** | +37,0 % | +41,7 % | 24,3 ms | 20,9 | 18,3 | 22,7 |
| float | 6450 | ±0 % | ±0 % | +1,9 % | 1268 ms | 1268 | 1251 | 1209 |
| lambda | 16460 | +0,9 % | +1,3 % | +1,3 % | 1470 ms | 1385 | 1365 | 1393 |
| list | 15628 | +0,8 % | +1,1 % | +1,0 % | 1658 ms | 1733 | 1660 | 1768 |
| loop | 10200 | ±0 % | +1,5 % | +2,9 % | 1464 ms | 1329 | 1441 | 1374 |
| method | 6437 | +3,1 % | +3,9 % | +4,3 % | 637 ms | 680 | 677 | 612 |
| string | 8148 | +0,7 % | +1,0 % | +0,9 % | 901 ms | 879 | 916 | 866 |
| **ohne fib** | **85876** | **+0,7 %** | **+1,1 %** | **+1,4 %** | | | | |

tcc erzeugt 5- bis 10-mal so viele Instruktionen wie gcc; der Frame fällt darin kaum auf (die Zeiten liegen in der Schwankung). Auch hier ist `fib`
(+35-42 % Instruktionen) der Fall, den die Optimierung "wirft nicht" abfängt; `loop` und `float` zeigen in (a1) die Speicherung der Zeile in `<main>`, die
tcc nicht zusammenfasst (+2-3 %).

### Performance, gcc -O2

| Programm | Instr. ohne (Mio.) | (b) | (a1) | Zeit ohne | (b) | (a1) |
|---|---:|---:|---:|---:|---:|---:|
| alloc | 810 | ±0 % | +0,7 % | 48,7 ms | 48,8 | 48,8 |
| array | 1651 | ±0 % | ±0 % | 114,4 ms | 114,8 | 112,2 |
| fib | 38 | +197 % | +197 % | 2,6 ms | 7,9 | 7,9 |
| float | 330 | ±0 % | ±0 % | 140,8 ms | 140,9 | 149,0 |
| lambda | 2170 | +3,2 % | +4,1 % | 152,8 ms | 157,7 | 161,2 |
| list | 1511 | +4,5 % | +5,7 % | 203,0 ms | 207,2 | 209,9 |
| loop | 1050 | ±0 % | ±0 % | 65,9 ms | 58,5 | 60,3 |
| method | < 1 | ±0 % | ±0 % | 1,4 ms | 1,5 | 1,4 |
| string | 889 | +4,7 % | +5,4 % | 49,7 ms | 51,8 | 52,2 |
| **ohne fib** | **8411** | **+2,1 %** | **+2,7 %** | | | |

Dasselbe Bild wie Release. Die Zeilen kosten über (b) hinaus **0,6 Prozentpunkte** - zu wenig, um Performance einen Trace ohne Zeilen zu geben.

### Mit Threads (der Frame-Zeiger im Zustand des Tasks)

Ein Programm mit Threads hält seine Thread-Werte im Zustand des Tasks und liest ihn über eine Funktion, die nie eingebettet wird (`fire_tlGet`, `const`:
einmal je Funktion). Nachgebildet mit derselben Art Funktion für den Frame-Zeiger (Release, gcc -O2, Instruktionen gegen das Release ohne Frames oben):

| | alloc | array | fib | float | lambda | list | loop | method | string | ohne fib |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| (b) | +0,7 % | ±0 % | +279 % | ±0 % | +6,7 % | +3,9 % | ±0 % | **+29 %** | +3,0 % | **+3,0 %** |
| (a1) | +1,3 % | ±0 % | +279 % | ±0 % | +7,6 % | +5,4 % | ±0 % | +29 % | +3,6 % | **+3,7 %** |

Der Aufruf von `fire_tlGet` verhindert, dass gcc den Frame mit einer eingebetteten Methode wegfallen lässt (`method`: +29 %). In einem echten Programm mit
Threads ruft eine Funktion, die `g_unwind`, den Pool oder die Handler braucht, `fire_tl()` ohnehin schon - der Frame teilt sich den Aufruf; die Zahlen
sind die obere Grenze. Bei tcc mit Threads (`FIRE_TLS_EMULATED`, `pthread_getspecific`) wird es teurer; der Generator soll den Zeiger dort einmal je
Funktion holen, wie die übrigen Thread-Werte.

### Zum Vergleich: der Debug-Build

Der Debug-Build (Schatten-Stack mit allem, was der Debugger braucht, Haken je Zeile) ist kein Kandidat für Release - gemessen mit denselben Programmen,
gcc, Zeit gegen Release `-O2`:

| | alloc | array | fib | float | lambda | list | loop | method | string |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Release -O2 | 54,9 ms | 132,2 | 2,8 | 162,6 | 220,8 | 208,7 | 63,4 | 5,5 | 48,2 |
| Debug -O0 -g (wie gebaut) | 697,9 | 1222,9 | 213,1 | 2685,9 | 1714,2 | 1602,1 | 4372,4 | 1730,7 | 697,9 |
| Debug-C mit -O2 | 141,1 | 307,4 | 37,0 | 201,3 | 317,3 | 324,5 | 162,4 | 279,7 | 155,1 |

Selbst mit `-O2` ist der Debug-Code 1,2- bis 50-mal so langsam (die Adressen aller Variablen im Frame, keine typisierten Rümpfe). Der Frame für Traces muss
also ein eigener, schlanker sein, nicht der `DbgFrame`.

### Code-Größe

Gemessen an `list` mit `--keep-all` (der ganze Prelude bleibt: 442 fire-Funktionen), das Objekt des Programms samt Runtime (`size`, Text + Daten):

| | ohne | (b) | (a2) | (a1) |
|---|---:|---:|---:|---:|
| gcc -O2 | 436 370 B | +6,3 % | +7,6 % | +7,4 % |
| tcc | 1 407 677 B | +3,7 % | +4,5 % | +4,3 % |

Dazu kämen die Deskriptoren (`FireFnInfo`: zwei Zeiger je Funktion, die Namen; die Namen hat ein Release-Build für Reflection schon zum Teil) - bei 442
Funktionen rund 10 KB. Zum Vergleich (c): `-g1` (nur Zeilentabellen) legt bei demselben Programm 245 KB `.debug_line` dazu (nicht geladen, abtrennbar),
`.eh_frame` ist mit 38 KB schon da.

### Was ein Wurf kostet

Ein Mikro-Benchmark (`throw` in 20 Ebenen Rekursion, `catch` außen, 200 000-mal; Release, gcc -O2):

| | je Wurf |
|---|---:|
| heute (kein Trace) | 229 ns |
| mit Frames (a1), ohne Aufnahme | 275 ns |
| mit Frames (a1) und Aufnahme (Frames zählen, `malloc`, 21 Paare kopieren) | 300 ns |
| (c) `backtrace()` der glibc statt der Frames (nur die Adressen, ohne Auflösen) | 3 990 ns |

Allein gemessen (`cap.c`, ein C-Programm): das Laufen über 20 Schatten-Frames 67 ns (100 Frames: 408 ns); `backtrace()` 2,8 µs (100 Frames: 10,9 µs);
`backtrace_symbols` dazu 16,9 µs; libbacktrace (`backtrace_full` mit Datei und Zeile aus DWARF) 3,4 µs je Aufruf, der erste liest die Debug-Infos und kostete
rund 70 ms (belastete Maschine). **Unter tcc findet `backtrace()` einen einzigen Frame** - tcc 0.9.27 schreibt kein `.eh_frame`, der Abwickler kommt über den ersten Frame von
tcc-Code nicht hinaus.

### Bewertung

| | Laufzeit Release | Wurf | Zeilen richtig | geht überall | Threads/Tasks | Urteil |
|---|---|---|---|---|---|---|
| (a1) | 0-7 % Instruktionen (gcc; tcc 0-4 %; mit der Optimierung für Funktionen ohne Wurf), im Mittel 2,6 % (tcc 1,4 %); mit Threads im Mittel 3,7 %, einzelne bis 29 %; Code +4-8 % | +30 % | ja, wie die VM | ja (reines C) | ja: der Zeiger gehört zum Zustand des Tasks | **der Standard** |
| (a2) | etwas weniger als (a1) | wie (a1) | nein | ja | ja | verworfen |
| (b) | wie (a1) minus die Zeilen (0,6 Punkte gcc, 0,7 tcc) | wie (a1) | nein | ja | ja | nur als `#stacktrace calls` |
| (c) | 0 | ×15 | über DWARF; inlining-abhängig, Zeilen von gcc nicht immer die der VM | **nein**: nicht tcc, nicht Open Watcom (dos16, win16, dos32, bios16), nicht wasm (der Stack ist nicht lesbar), auf dem ESP32 nur Adressen (Xtensa, ohne Datei auf dem Gerät); auf Windows DbgHelp + PDB, MinGW: libgcc ja, Zeilen nur mit DWARF | Coroutinen der Tasks: der Abwickler läuft über den eigenen Stack eines Tasks bis zu seinem Einstieg, danach Unsinn | verworfen als Standard; höchstens eine spätere Ergänzung für Abstürze (Signal), nicht für Ausnahmen |
| (d) | 0 | - | ja | ja | ja | zu wenig: der Fall, für den man den Trace braucht (der Fehler beim Benutzer, im Test, im Release), hat keinen |

## 6. Was welches Ziel kann

| Ziel (Plattform, Toolchain) | (a) Schatten-Frames | (c) Abwickeln | Anmerkung |
|---|---|---|---|
| linux, macos (gcc, clang) | ja | ja (`backtrace`, libgcc/libunwind) | |
| linux (tcc) | ja | **nein** (kein `.eh_frame`) | tcc ist die Toolchain ohne Installation |
| windows (MinGW, clang) | ja | libgcc; Zeilen nur mit DWARF | |
| windows (msvc) | ja | DbgHelp + PDB | |
| windows tcc | ja | nein | |
| dos16, win16, dos32, bios16 (Open Watcom) | ja; der Frame sind 6-8 Byte bei 16 Bit (`near`-Zeiger auf den Vorgänger, der Deskriptor `far` oder `near`, die Zeile; der Zeiger auf den obersten Frame `near`-Daten wie `g_unwind`), Deskriptoren `far`/im Code-Segment | nein | Speicher knapp: `#stacktrace calls` oder `off` als Standard? siehe Offen |
| esp32 (ESP-IDF, gcc Xtensa) | ja | nur Adressen (`esp_backtrace`), keine Zeilen auf dem Gerät | Tasks von FreeRTOS: der Zeiger im Zustand der Task |
| freertos, sim | ja | nein | |
| wasm (Emscripten) | ja | nein (der Wasm-Stack ist nicht lesbar; `emscripten_get_callstack` gibt JS-Text) | |
| C-VM, C#-VM | (die VM hat ihre Frames) | - | Zeilen aus den Zeilentabellen, die das gepackte Programm dafür behalten muss |

(a) ist reines C ohne Hilfe der Plattform und geht überall, wo das Programm geht - deshalb ist es der Mechanismus.

## 7. Empfehlung je Modus und die Direktive

| Modus | Standard | Begründung |
|---|---|---|
| **Debug** | Trace mit Zeilen über den vorhandenen Schatten-Stack (`DbgFrame` hat `fn`, `src`, `line` schon) | kostet nichts zusätzlich |
| **Release** | **(a1)**: Frames mit Zeile | der Trace soll genau dort da sein, wo der Fehler beim Benutzer passiert; gemessen 0-7 % |
| **Performance** | **(a1)** wie Release | gemessen: ohne Zeilen spart es 0,6 Prozentpunkte (2,1 % statt 2,7 % Instruktionen) - das ist einen schlechteren Trace nicht wert |
| die kleinen Ziele (Open Watcom: dos16, win16, dos32, bios16) | (a1); `calls` ist der Weg, wenn der Code nicht passt | die Speicherungen der Zeilen kosten dort Code (gcc: +1,1 Punkte über (b)); zu messen |
| C-VM, C#-VM | immer mit Zeilen | der Trace entsteht nur beim Wurf; die Frames hat die VM ohnehin |

**Die Direktive** `#stacktrace off | calls | lines` (wie `#globaljobs`: für das ganze Programm, im Projekt `"stackTrace"`, auf der Befehlszeile `--stacktrace`,
in `fire.native.json` je Ziel; die Quelle geht vor dem Projekt und der Konfiguration, wie bei den anderen Einstellungen des Programms):

* `lines` - Frames mit Zeilen (der Standard in jedem Modus; Debug immer);
* `calls` - Frames ohne Zeilen (für Code, der knapp ist);
* `off` - keine Frames, die Ausgabe wie heute (für die kleinsten Ziele, für eine Schleife, die jedes Prozent zählt).

Die VMs halten sich daran **in der Ausgabe** (dieselben Traces wie der native Build derselben Einstellung: `calls` druckt keine Zeilen, `off` keinen Trace),
damit der Korpus in beiden Wegen gleich bleibt; ihre Kosten hängen nicht davon ab. Der Debug-Build hat immer `lines` (die Direktive gilt dort nicht).

**Optimierungen im Generator** (gehören zum Schritt, nicht später):

1. **Kein Frame in einer Funktion, die nicht werfen kann** und nur solche aufruft (ein Fixpunkt über den Aufrufgraphen, wie die Analyse, die heute die
   C-Rümpfe der Ebene 2 macht: nur Arithmetik ohne `/` und `%` durch eine Variable, keine Aufrufe der Runtime, die werfen). `M.Fib` ist so eine.
2. Die Zeile nur, wo eine Zeile **beginnt** (die Stellen von `FIRE_DBG_L`), nicht je Instruktion; zwischen zwei Aufrufen fasst der C-Compiler zusammen.
3. Ein Frame, dessen Funktion von gcc eingebettet wird, verschwindet mit (gemessen: `method`).
4. Der Zeiger auf den obersten Frame ist ein Thread-Wert (`FIRE_THREAD_VARS`): ohne Threads eine globale Variable, mit Tasks im Zustand des Tasks
   (`fire_tl()` einmal je Funktion, die schon da ist, wenn die Funktion andere Thread-Werte braucht).

## 8. Die VMs

**C#-VM**: `CaptureErrorTrace` wird zum Standard (an, außer bei `#stacktrace off`); `CurrentTrace` bekommt die Funktion je Frame (der Chunk des Frames →
`FunctionProto` → Name nach denselben Regeln wie `DebugFuncName` - eine gemeinsame Funktion in `fire.Runtime`, die auch der Generator nimmt), die
Tiefe begrenzt, und das Ergebnis geht an das Objekt (`ObjectInstance.Trace`) statt in `_lastThrowTrace`. Die verschachtelten Läufe (`CallLambdaInline`,
`_callbackBoundaries`) legen ihre Frames auf denselben `_frames`; an einer Grenze eines Rückrufs aus einem Native kommt der Frame des Natives dazu. Der Thread
einer VM (`IsFireThreadVm`) kennt seinen Startort (neu: vom `fire` mitgegeben).

**C-VM**: die Instruktion `Throw` (`vm_run.c`) und jeder Fehler der Runtime (`arrayGet`, `divide`, ...) gehen durch `throwValueR` der Runtime. Dort nimmt
ein Haken den Trace auf: nativ das Laufen über die Schatten-Frames, in der C-VM (hinter `FIRE_VM`, wie `exitNow`) eine Funktion der VM, die die `VmFrame`s
von oben nach unten liest (Catch-Frames überspringen bis zur Tiefe ihres Handlers), die Zeile aus `VmChunk.lines`, die Funktion aus `VmProto`. Dieselbe
`FireTrace`-Struktur und dasselbe Formatieren wie der native Build (es ist die Runtime: `fire_rt.h` formatiert, VM und nativ liefern nur die Paare); der native
Build ist ohne `FIRE_VM` unverändert. **Das gepackte Programm muss seine Zeilentabellen behalten** (heute schreibt `CVm.Pack` die `.fbc` ohne): bei
hello world sind das rund 5 KB von 112 KB (fast alles Prelude); bei `#stacktrace off` oder `calls` bleiben sie weg. Die Namen der Funktionen hat die
`.fbc` (Reflection, Debugger).

## 9. Schritte

1. **Format und Runtime** (`fire_rt.h`): `FireFnInfo`, `FireTrace` (Aufnehmen, Teilen, Freigeben, Formatieren, Begrenzen), `reportUnhandledIn` druckt den
   Trace; das verborgene Wort am Ausnahme-Objekt, das Bit der Ausnahmeklassen im Deskriptor, Kopie über Threads. Tests in C für das Formatieren.
2. **C#-VM** (der Maßstab, wie bei allem): Trace an jedem Wurf, `StackTrace`/`Frames`/`StackFrame`/`ToString` im Prelude (Natives `__exc_trace`, `__exc_frames`),
   unbehandelt auf stderr, Threads (`in thread ..., started at`), `await` (Abschnitt), Rückrufe. `#stacktrace`. Die Erwartungen des Korpus anpassen.
3. **C-VM** (`vm_run.c`, `vm_threads.c`, `vm_load.c`): dieselben Traces; Zeilentabellen im gepackten Programm (`CVm.Pack`, `vm_load.c`). Korpus mit
   `tests/run-cases-vm.sh` gleich der C#-VM.
4. **nativ** (`CppGenerator`): Frame je fire-Funktion außer den nicht werfenden, Zeile an `FIRE_DBG_L`-Stellen, Deskriptoren, Catch-Block mit dem Frame seiner
   Funktion, Startort der Threads, Frame der Natives bei Rückrufen; Debug-Build nimmt den `DbgFrame` (kein zweiter Frame). Messen mit
   `tests/native-baseline.py` (die Schwelle: der Mittelwert über die 9 Programme ≤ 5 % Zeit, keines über 10 %; sonst wird der Standard des Releases neu entschieden).
5. **Module**: Deskriptoren in den Bibliotheken (static, shared, Plugins), `#stacktrace` im Schlüssel des Caches, Datei mit Paketnamen.
6. **forge-c** (`native/forge/gen`): dasselbe wie 4 und 5, byte-gleich (siehe [11](#11-der-c-port)).
7. **spark und `forge test`**: klickbare Zeilen im Output-Panel und im Test-Explorer, Funktionsnamen im Dialog, die Frames des Test-Treibers ausblenden.
8. **Doku**: [Exceptions](../language/14-exceptions.md) (Abschnitt "Stack traces"), [Threads](../language/15-threads-and-actors.md#errors-in-threads), die
   Direktive in [Directives](../language/17-preprocessor.md), `docs/tools/testing.md`.

VM zuerst: sie ist der Maßstab des Korpus, der Trace kostet dort nur beim Wurf, und das Format ist danach festgelegt, bevor der Generator es nachbaut.

## 10. Tests

* **Der Korpus** (`tests/cases`): die 16 `.unhandled` und die 4 Erwartungen mit `UNBEHANDELT:` bekommen ihren Trace - das ist der Vergleich: VM (C#), C-VM
  und nativ drucken **dieselben Zeilen**. Neue Fälle in einer Gruppe `trace` (in allen drei Wegen, Release und Performance): `throw` in der Tiefe, jeder Fehler der
  Runtime aus der Tabelle in [Exceptions](../language/14-exceptions.md#errors-of-the-runtime), Rethrow, `throw` im `catch`, `finally` dazwischen, `resume`
  und danach ein zweiter Wurf, Konstruktor/Destruktor/Getter/Operator/Lambda in der Mitte, Rekursion über 64 Frames (das Kürzen), `e.StackTrace` und `e.Frames`
  im `catch`, `print(e)`, `fire`/`catch threads`, `fire global`, `await`/`await [...]`/Actor/`Completion.Fail`, `until`, ein Lambda aus einem Native
  (`win.Tick()` ohne Bildschirm, ein Lambda an ein Paket), ein Paket in fire, `#stacktrace calls|off`.
* `tests/run-cases.sh` und `run-cases-vm.sh` vergleichen die ganze Fehlerausgabe schon; die Dateinamen sind relativ (gleich auf jeder Maschine).
* **Module**: dieselben Fälle mit `--linkage static` und `shared` (der Trace geht durch die Bibliothek, mit Paketnamen).
* **`fire.Testing`**: `forge test` mit einem fehlschlagenden `Assert` (die Zeile im Test oben, keine Frames des Treibers), der Dialog von spark
  (`ReportRuntimeErrorAsync`), das klickbare Muster des Output-Panels, `NativeDebugTarget` unverändert.
* **Kosten**: `tests/native-baseline.py compare` vor und nach Schritt 4 (Größe und Zeit), dazu der Wurf-Benchmark von oben als neuer Eintrag in `tests/bench`.
* **Ziele ohne Desktop**: ein Fall auf dos16 (Open Watcom; Speicher), esp32 im Simulator `native/sim`, wasm - der Trace gleich dem des Desktops.

## 11. Der C-Port

forge-c (`native/forge`) muss **byte-gleich** dasselbe C (`fire_program.c`) und dieselbe `.fbc` schreiben wie der C#-forge (ROADMAP_C_PORT, `tests/compare-*.sh`).
Jede Änderung geht deshalb in beide:

| C# | C (forge-c) |
|---|---|
| `CppGenerator.cs` (Prolog/Epilog der Funktionen, `return`, Zeilen, Catch-Blöcke, Start der Threads), `CppGenerator.Debug.cs` (Namen), die Analyse "wirft nicht" | `native/forge/gen/cppgenerator*.c`, `gen/debug.c` |
| `CppGenerator.Modules.cs`, `StaticLinkage.cs`, `CppGenerator.Plugins.cs` (Deskriptoren der Module, Cache-Schlüssel) | `gen/modules.c`, `gen/staticlinkage.c`, `gen/plugins.c`, `build/linkage.c` |
| `ProgramFile.Write` (Zeilentabellen im gepackten Programm), `CVm.Pack` | `programfile.c`, `build/cvm.c` |
| Prelude (`Prelude.cs`: `StackFrame`, `StackTrace`, `Frames`, `ToString`) | `native/tables/prelude.fire` (schreibt `forge tables` aus `Prelude.cs`: neu erzeugen, nicht von Hand) |
| die Natives `__exc_trace`, `__exc_frames` (`natives.def`, `NativeRegistry`) | `native/tables` (die Tabelle der Natives), `vm_natives.c` |
| Direktive `#stacktrace`, Projekt `"stackTrace"`, `--stacktrace` | `preprocessor.c`, `project/`, `forge.c` |

Die Runtime (`fire_rt.h`) und die C-VM gibt es nur einmal. Die Prüfung: der Korpus durch beide forge (`compare-frontend.sh`, das C byte-gleich) und die
Traces in allen drei Wegen gleich.

## 12. Offen

* **Die kleinen Ziele**: Standard auf dos16/win16/bios16 `lines` wie überall (6 Byte je Frame auf dem Stack, die Zeilen-Speicher kosten Code) oder `calls`?
  Zu messen mit Open Watcom am Physik-Beispiel (`--stats`), wenn Schritt 4 steht.
* Die Messungen hier sind am eingesetzten C gemacht, ohne die Optimierungen aus [7](#7-empfehlung-je-modus-und-die-direktive); nach Schritt 4 neu mit
  `tests/native-baseline.py` und mit einem Programm mit Threads (der wirkliche Weg über `fire_tl()`).
* `e.StackTrace` als `string` reicht für die meisten; `e.Frames` als Records braucht ein Record im Prelude - Name `StackFrame` frei?
* Sollen Frames des Preludes ganz wegfallen (wie "Just My Code")? Vorschlag: nein, ohne Zeile; spark kann sie grau zeigen.
* Ein Absturz (Signal, `fatal`) ist keine Ausnahme: dort könnte später (c) einen Trace der C-Ebene drucken, wo es geht.
