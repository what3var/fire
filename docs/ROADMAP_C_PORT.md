# Roadmap: VM und Compiler von .NET nach C

Status: **Entwurf zur Abstimmung** (Branch `claude/wizardly-clarke-28ot2e`). Noch nichts umgesetzt; die Punkte unter
[Offene Entscheidungen](#offene-entscheidungen) bestimmen den Zuschnitt und sind vor Phase A zu klären.

Ziel: **forge** (Compiler, Generator, Linker, Projekt- und Paketwerkzeuge samt **ember**) und die **VM** laufen ohne .NET - geschrieben in C11,
gebaut mit dem C-Compiler, den fire ohnehin voraussetzt. .NET bleibt danach nur noch in **spark**, bis die IDE nach fire portiert ist
([Phase F](#phase-f---die-ide-nach-fire-ausblick)).

Das ändert das Ziel aus [ROADMAP.md](ROADMAP.md) ("ember, forge und spark in fire neu schreiben"): forge, ember und die VM werden C, nur spark wird fire.
Die Leitlinien von dort gelten weiter (Packages bleiben Code-only; neuer nativer Code in C gegen `fire_rt.h`; VM und nativ verhalten sich gleich).

## Bestand

Zeilen C# (ohne Leerdateien, gerundet), und was aus jedem Projekt wird:

| Projekt | Zeilen | Inhalt | Verbleib |
|---|---|---|---|
| `fire` | 16 300 | Werte, Einheiten, Bytecode, AST, Prelude, Standard-Natives (`StringMethods`, `JsonMethods`, ...), Besitz, Scopes | AST/Bytecode/Prelude -> C (forge); Werte, Besitz und Natives **entfallen** zugunsten von `fire_rt.h` (dort gibt es alles schon ein zweites Mal) |
| `fire.Compiler` | 29 200 | Lexer, Präprozessor, Parser, Resolver, Typprüfer, Compiler, Linker, CLI, NativeBuilder, Debug-Ziele, Testläufer, Modellwandler | -> C (forge) |
| `fire.Native` | 15 600 | Generator Bytecode -> C (`CppGenerator*`), Toolchains, Linkage, Stats | -> C (forge) |
| `fire.Runtime` | 13 800 | VM (154 Opcodes, 225 Fälle mit den verschmolzenen), Threads, Tasks, Plugins, Packer, Host der Paket-Natives | -> C (VM), auf `fire_rt.h` |
| `fire.UI.Markup` | 5 000 | `.fxml`, `.biscuit`, `.fdata`, Falstad -> fire-Quelltext; Completion des Markups | -> C (forge) |
| `fire.Project` | 3 400 | Projekt, Solution, Bauplan, Vorlagen, Beispiele, Notizen, MCP-Server | Projekt/Bauplan/Vorlagen -> C (forge); Notizen und MCP gehören zur IDE (Phase F) |
| `fire.Package.Manager`, `fire.Ember` | 1 700 | Manifest, Store, `.fpk`, Quellen | -> C (forge/ember) |
| `fire.Terminal*`, `*.Bridge`, `fire.Device.Manager` | 9 500 | Grafik, Fenster und Geräte der VM in C# | **entfallen**: die C-VM benutzt die C-Natives der Packages wie ein natives Programm (Geräte-Manager von spark: Phase E/F) |
| `fire.Testing` | 51 900 | Testprogramm (beide Engines, Editor, Debugger) | Fälle werden Daten, der Läufer C; Editor-Tests bleiben bis Phase F |
| `fire.Editor`, `fire.Git` | 34 100 | spark | bleibt C# bis Phase F |

Zu portieren sind damit rund **75 000 Zeilen** C#; rund 20 000 fallen weg, weil die Runtime in C sie schon enthält. Schon in C vorhanden und
wiederverwendbar: `native/runtime` (17 000 Zeilen: Werte, Objekte, Besitz, Strings, Arrays, JSON, Reflection, Threads/GIL, Tasks, Aktoren,
Globals, Probes, Module/Plugins, Debug-Agent), die Plattformschicht, Deflate/Zip (`packages/compression`), SHA-256 (`packages/hash`), Prozesse (`packages/process`).

## Leitentscheidungen (Vorschlag)

### L1. Eine Runtime: die VM ist ein Interpreter auf `fire_rt.h`

Die C-VM bekommt kein eigenes Wertmodell. Sie lädt Bytecode und führt ihn über dieselben Funktionen aus, die der Generator in `fire_program.c`
aufruft: `Value` (16 Byte), Objekte mit Klassen-Deskriptoren, Besitzlisten, Ausnahmen mit `resume`, GIL-Threads, Scheduler, Aktoren, die
gemeinsame Domäne der Globals.

* "VM und nativ verhalten sich gleich" gilt dann für Werte, Formatierung, Besitz und Threads durch denselben Code statt durch doppelte Tests.
* Die Tabellen, die der Generator fest ins Programm schreibt (Klassen, Reflection, JSON, Debug), baut die VM beim Laden aus dem Bytecode. Dass
  die Runtime Klassen zur Laufzeit annimmt, zeigen die Plugins (`fire_rt.h`, "fire plugins": Klassen, Catch-Typen, Enums, Reflection-Zeilen).
* Neu zu schreiben sind nur: Lader, Frames und Scopes mit dynamischen Slots (BYTECODE 3), Dispatch, Aufruf nach Namen, die Schrittschleifen des Debuggers.
* Das Threadmodell wechselt: heute eine C#-VM je fire-Thread mit `GlobalsBroker`, künftig das Modell der Runtime (GIL, Sektionen).
  Programme sehen keinen Unterschied (dafür gibt es die Tests beider Engines), `FireRuntime`/`GlobalsBroker`/`SyncEngine` entfallen.

Risiko: `fire_rt.h` ist auf erzeugten Code zugeschnitten (statische Tabellen, `fatal` beendet den Prozess). Darum beginnt Phase B mit einem
Spike, der genau das prüft, bevor irgendetwas anderes portiert wird.

### L2. Die Naht ist das Bytecode-Format, die Reihenfolge VM -> Frontend -> Backend

Heute geht `LinkedProgram` als C#-Objekt von Compiler zu VM und Generator; serialisiert wird mit MemoryPack (Packer, Plugin-Cache). Zuerst
bekommt der Bytecode ein **festgelegtes Dateiformat** (`.fbc`, in BYTECODE.md beschrieben), das C# schreibt und liest. Danach lässt sich jede
Seite einzeln tauschen und gegen die andere prüfen:

1. **VM** zuerst: kleinster Teil, größter Wegfall (Bridges, doppelte Natives), und sie läuft sofort mit dem Bytecode des C#-Compilers.
2. **Frontend** (Lexer bis Linker): geprüft gegen den C#-Compiler, `.fbc` Byte für Byte gleich.
3. **Backend** (Generator, Toolchains): geprüft gegen den C#-Generator, `fire_program.c` Byte für Byte gleich.

### L3. Mechanischer Port 1:1, keine Umbauten unterwegs

Datei für Datei, dieselben Namen, dieselbe Reihenfolge der Ausgabe. Nur so taugen die byte-gleichen Vergleiche aus L2 als Prüfung, und nur so
lassen sich Änderungen, die während des Ports auf `master` am C#-Code passieren, nachziehen (je portierter Datei steht der C#-Commit, dem sie
entspricht). Verbesserungen kommen danach. Folgen für den C-Code:

* Maps mit **Einfügereihenfolge** (die Ausgabe des Compilers und des Generators hängt an über 100 Stellen an `Dictionary`/`HashSet`).
* Speicher im Compiler aus **Arenen** je Übersetzung (kein einzelnes `free`); AST-Knoten als getaggte Strukturen.
* Quelltext intern wie bisher in UTF-16-Einheiten gezählt (Spalten der Diagnosen, `fire_char16` der Runtime), Dateien in UTF-8.

### L4. Werkzeuge sind Prozesse mit Protokoll, keine eingebundene Bibliothek

spark ruft heute Parser, Resolver, Typprüfer und die VM im eigenen Prozess (42 Stellen `VM`, dazu `RuntimeSession`, `LiveSession`, `UiPreview`).
Künftig spricht jede IDE - das C#-spark der Übergangszeit und das spätere spark in fire - mit forge und der VM über Prozesse:

* **Bauen, Ausführen, Testen**: die Befehlszeile (gibt es).
* **Debuggen**: die VM spricht das [Debug-Protokoll nativer Programme](spec/NATIVE_DEBUG_PROTOCOL.md). `NativeDebugTarget` in spark gibt es schon;
  `VmDebugTarget` entfällt, VM und nativ debuggen gleich.
* **Sprachdienste** (Symbole, Completion, Diagnosen, Markup-Completion, Doku): `forge serve`, eine JSON-Nachricht pro Zeile über stdio.
  Die Logik von `ScriptSymbolIndex` und der Completion zieht dabei von spark nach forge und wird von der fire-IDE weiterbenutzt.
* **Programme im Fenster der IDE** (`LiveSession`: Schaltplan-Editor; `UiPreview`: Entwurfsansicht): das Programm läuft als Kindprozess, Bilder
  und Eingaben über einen gemeinsamen Speicherbereich bzw. das Protokoll. Das ist der heikelste Teil des Umschaltens (Phase E).

Ein abgestürztes Programm reißt die IDE dann nicht mehr mit, und `fire_rt.h` muss nicht lernen, mehrere Programme nacheinander in einem Prozess zu überleben.

### L5. Tests werden Daten

`fire.Testing` hält die Fälle als C#-Text (`Check(...)`, `CheckNat(...)`, ... - rund 800 Aufrufe allein in `Program.cs`). Sie werden einmal
exportiert nach `tests/cases/<gruppe>/<name>.fire` mit `.expected` und einer kleinen Kopfzeile (Engine, Modus, Optionen); das C#-Testprogramm
liest sie von dort, solange es lebt, der Läufer in C (`forge conformance`) später dieselben Dateien. Tests in fire selbst (`[Test]`,
`packages/*/testing`) laufen unverändert über `forge test`.

## Aufbau im Repository (Vorschlag)

```
native/runtime/    (wie heute) die Runtime, gemeinsam für native Programme und die VM
native/base/       Arena, Strings, geordnete Maps, Pfade/Dateien, Prozesse, JSON-Leser, Zip, SHA-256 für die Werkzeuge
native/vm/         Lader (.fbc), Interpreter, Natives-Tabelle, Host der Paket-Natives, Debug-Agent der VM; Programm `fire`
native/forge/      lexer, preprocessor, parser, resolver, typecheck, compiler, linker, markup/, gen/ (Generator), build/ (Toolchains),
                   project/, pkg/, serve/, cli; Programme `forge`, `ember`
tests/cases/       die Testfälle als Dateien
```

Gebaut wird ohne Build-System: je Programm eine Datei, die alles einbindet (`cc -O2 native/forge/forge.c -o forge`), dazu `build.sh`/`build.bat`.
Übersetzen müssen die Werkzeuge mit gcc, clang und MSVC für Windows, Linux und macOS (nicht mit Open Watcom - das bleibt ein Ziel, kein Wirt).

## Phasen

Jede Phase endet mit einem Stand, der auf `master` gehen kann; das C#-Gegenstück wird erst entfernt, wenn der Vergleich über den ganzen Korpus grün ist.
Der **Korpus**: alle Testfälle, `packages/**/*.fire` mit ihren Tests, die Beispiele und Vorlagen aus `fire.Project`.

### Phase A - Vorbereitung im C#-Bestand

Noch kein C; alles hier ist für sich nützlich und macht den Rest prüfbar.

1. **`.fbc`** festlegen und in C# schreiben/lesen (Konstanten, Einheiten, Funktionen, Klassen-Metadaten, Enums, Ressourcen, Zeilentabellen,
   Modulschnittstellen); Packer, Payload und Plugin-Cache steigen von MemoryPack um. `forge dump` gibt ein Programm als Text aus (zum Vergleichen).
2. **Tests als Daten** (L5): Export, das C#-Testprogramm liest die Dateien.
3. **Determinismus**: zweimal bauen ergibt byte-gleiche `.fbc` und `fire_program.c`; Stellen mit zufälliger Reihenfolge (Hash-Iteration, Zeitstempel, GUIDs) beseitigen.
4. **Referenzstände**: ein Skript erzeugt für den Korpus `.fbc`, AST-Ausgabe, Diagnosen und `fire_program.c` (je Ziel/Option) als Vergleichsbasis.
5. Tabellen, die beide Seiten brauchen, aus **einer** Quelle: Opcodes mit Operandengrößen, Fehlerkatalog, Methodentabellen des Preludes
   (`StringMethods` usw.) als Datendateien, aus denen C# und C erzeugt werden; das Prelude selbst als `.fire`-Dateien statt C#-Strings.

### Phase B - die VM in C

0. **Spike** (entscheidet L1): Interpreter für rund 30 Opcodes auf `fire_rt.h` - Rechnen, Locals/Scopes, Aufrufe, eine Klasse mit Feldern und
   Destruktor, `try`/`catch`/`resume`, eine Klasse zur Laufzeit registriert. Ergebnis: geht es ohne Eingriffe in die Runtime, mit kleinen, oder gar nicht.
1. `native/base` und der Lader.
2. Interpreter vollständig: alle Opcodes, die drei Modi (Debug/Release/Performance), Lambdas und Captures, Properties, Überladungen, Generics,
   typisierte Arrays, Zeiger, Records, Besitz (`take`, `TakeTo`, Kopien), Shutdown an sicheren Punkten.
3. Threads, Tasks, Aktoren, `sync globals`, Abbruch, `on`-Lambdas - über die Abschnitte der Runtime.
4. Natives: eine Tabelle Name -> Funktion für die Kern-Natives (in der Runtime vorhanden, heute vom Generator direkt aufgerufen); Paket-Natives
   zunächst unverändert als Shared Library über die [C-ABI v1](../native/abi/fire_pkg_abi.h), den Host (`fire_host`: IO-/Netz-Regeln, Lambdas, Dienste) in C.
5. Reflection, JSON, Probes, Module und Plugins in der VM.
6. Debug-Agent (L4): Haltepunkte, Schritte, Variablen über die Scope-Ebenen, `set`, Pause, Zeilenprofil.
7. Programm `fire` (`fire run x.fbc`); der Korpus, vom C#-Compiler übersetzt, läuft mit den erwarteten Ausgaben; Benchmarks gegen die C#-VM.

**M1:** `forge run`/`forge test` (noch C#) starten die C-VM; die C#-VM bleibt bis M2 als Schalter. `fire.Terminal*`, die Bridges und die
C#-Natives sind danach tot.

Später, nicht Teil des Ports: ABI v2 für Paket-Natives (die Bibliothek benutzt die `Value` der VM direkt, keine Kopien, Objekte dürfen hinüber).

### Phase C - das Frontend in C

1. Lexer, Präprozessor, `#if`, Diagnosen. Prüfung: Token- und Diagnose-Ausgabe gleich.
2. AST und Parser. Prüfung: AST-Ausgabe gleich (`SyntaxTests`).
3. Resolver, Typprüfer, `SetterClaims`. Prüfung: Diagnosen gleich (`DiagnosticTests`, `TypeTests`).
4. Compiler, Linker, Importe und Preludes. Prüfung: `.fbc` byte-gleich über den Korpus.
5. Markup-Generatoren (`.fxml`, `.biscuit`, `.fdata`, Falstad). Prüfung: erzeugter fire-Text gleich.

**M2:** `forge run`, `forge test`, `forge ui|biscuit|fdata|falstad` für die VM laufen ganz ohne .NET.

### Phase D - Backend und Werkzeuge in C

1. Generator (`CppGenerator*`, 15 600 Zeilen - der größte Einzelposten). Prüfung: `fire_program.c`/`fire_natives.cpp` byte-gleich über den
   Korpus und die Optionen (Ziele, `linkage`, `stripping`, `#intwidth`, Debug, `optimize`).
2. NativeBuilder, Toolchains (gcc, clang, MSVC, tcc, Emscripten, ESP-IDF, Open Watcom), Linkage, `PackageLibrary`, Stats, Boot-Images, PE-Ressourcen.
3. Projekt, Solution, Bauplan, Vorlagen, Beispiele; Paketverwaltung und `ember`.
4. Rest der Befehlszeile: Testläufer, `forge data`, Modellwandler (`.fmesh`, `.fblocks`, glTF), Packer.
5. `ToolchainSetup` (lädt unter Windows einen Compiler): braucht HTTPS und Entpacken im Werkzeug - über das System (WinHTTP) statt eigener TLS.
6. `forge conformance` ersetzt den Kern von `fire.Testing`; Installer (`setup.iss`) und Paketierung ohne .NET-Werkzeuge.

**M3:** forge, ember und die VM sind C-Programme; wer nur sie braucht, braucht kein .NET. Der Bau von fire selbst braucht nur einen C-Compiler.

### Phase E - spark (C#) auf die C-Werkzeuge umstellen

1. `forge serve` (L4) und in spark `Language/` (4 300 Zeilen) darauf; die Completion-Tests wandern auf Protokollebene.
2. Debuggen der VM über `NativeDebugTarget`; Bauen/Ausführen nur noch über Prozesse; Treiber-Agent (`PackageDriverAgent`) und Geräte-Manager auf die C-VM.
3. `LiveSession` und `UiPreview` über Kindprozess und gemeinsamen Speicher.
4. `fire`, `fire.Compiler`, `fire.Runtime`, `fire.Native`, die Bridges und der Kern von `fire.Testing` verlassen die Solution.

**M4:** .NET steckt nur noch in spark.

### Phase F - die IDE nach fire (Ausblick)

Eigener Plan, sobald M3 steht. Was dafür in den Packages fehlt bzw. zu prüfen ist:

* ein **Code-Editor-Control** (`ui` hat `38_textarea.fire`; es fehlen Syntaxfarben, große Dateien, Faltung, Completion-Popup, Markierungen des Debuggers);
* Docking (`ui.docking` gibt es), Dialoge, Menüs, Baum, Markdown-Ansicht;
* Git über den `git`-Prozess statt libgit2; MCP-Server über `packages/mcp`; der Assistent ist schon ein Package.

Reihenfolge: Rahmen mit Projektmappe und Texteditor -> Bauen/Ausgabe -> Debugger -> Designer (fxml, Schaltplan, Datenmodell, Pixel, Hex,
Mesh, Blöcke) -> Notizen, Git, Pakete, Geräte -> Installer. Die Protokolle aus L4 sind dann schon da und erprobt.

## Risiken

* **`master` bewegt sich schnell.** Jeder Commit am C#-Compiler oder an der VM muss in den Port nachgezogen werden. Gegenmittel: L3, kurze Phasen,
  und während eine Komponente portiert wird, möglichst keine neuen Features in genau dieser Komponente.
* **L1 trägt nicht** (Runtime zu sehr auf erzeugten Code gebaut): dann eine eigenständige VM mit eigenem Wertmodell - deutlich mehr Arbeit und
  wieder zwei Implementierungen. Der Spike klärt das vor allem anderen.
* **Geschwindigkeit der VM** auf einer Runtime, deren schnelle Pfade für den C-Compiler gedacht sind (Inlining mit bekannten Arten). Messen in B7.
* **`LiveSession`/`UiPreview`** über Prozessgrenzen (Bildrate, Fokus, Eingaben) - notfalls bleiben diese beiden Ansichten bis Phase F eingeschränkt.
* **Geräte in der VM**: heute nutzt sie den Geräte-Manager und die Treiber von spark (`fire_host.dev_*`); die C-VM nimmt die C-Treiber des Packages.
  Das geteilte Gerät zwischen IDE und laufendem Skript braucht einen neuen Weg (Protokoll) oder entfällt in der Übergangszeit.
* **MSVC und macOS** sind für Teile der Runtime ausdrücklich ungeprüft (NATIVE_BACKEND, "Natives von Packages in der VM").
* **Umfang der Tests**: der Export von 38 000 Zeilen `Program.cs` ist Handarbeit an vielen Sonderfällen (Fake-Renderer, Xvfb, QEMU, DOSBox).

## Offene Entscheidungen

1. **forge in C statt in fire** - so verstanden und geplant; ROADMAP.md nennt bisher fire. Gilt das auch für **ember**? (Vorschlag: ja, es hängt an denselben Bibliotheken.)
2. **Einbetten in .NET** (`Help/Embedding.md`: fire als Bibliothek in eigenen C#-Programmen) fällt mit dem Port weg. Ersatz: eine C-Schnittstelle
   (`libfire`: bauen, ausführen, Ausgabe, Regeln, eigene Funktionen); ein dünner .NET-Wrapper darüber nur bei Bedarf. (Vorschlag: C-Schnittstelle, Wrapper nicht.)
3. **L1**: VM auf `fire_rt.h` (Vorschlag) oder eigenständig.
4. **L4**: spark in der Übergangszeit über Prozesse und Protokolle umstellen (Vorschlag), oder das C#-spark bis Phase F auf dem eingefrorenen
   C#-Compiler lassen (weniger Arbeit jetzt, dafür zwei Compiler, die auseinanderlaufen).
5. **`forge build --engine vm`** (eigenständiges VM-Programm): als C-VM mit angehängtem `.fbc` behalten (Vorschlag) oder streichen, weil es nativ gibt.
6. **Wo weitergearbeitet wird**: alles auf einem langen Branch, oder phasenweise nach `master` (Vorschlag: phasenweise, Phase A sofort).
