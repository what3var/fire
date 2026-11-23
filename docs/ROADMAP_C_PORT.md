# Roadmap: VM und Compiler von .NET nach C

Status: **Plan, mit dem Benutzer abgestimmt** (siehe [Entscheidungen](#entscheidungen)); **M1 erreicht** (23.11.2026: die C#-VM ist entfernt); **M3 erreicht**
(23.11.2026: das C#-forge, `src/fire`, `fire.Native` und der C#-ember sind entfernt, spark hat keinen Compiler mehr - [M3](#m3-schritt-1-bestandsaufnahme-23112026-vor-dem-entfernen)); Phase B bis Schritt 8 umgesetzt, Phase C Schritte 1 bis 6, Phase D Schritt 1, Schritt 2 teilweise, Schritt 3 bis auf Windows, Schritt 4 bis auf `tables`/`prebuild-standard`, Schritt 6 bis auf den Bau des Installers (siehe dort), Schritt 7 (`fire_embed.h`, Packages `forge`/`script`). Offen ist nur der Zuschnitt von ember.

Ziel: **forge** (Compiler, Generator, Linker, Projektwerkzeuge) und die **VM** laufen ohne .NET - geschrieben in C11, gebaut mit dem
C-Compiler, den fire ohnehin voraussetzt. Beide sind **Bibliotheken**, die sich auch als Package in ein fire-Programm einbinden lassen
(fire als Skriptsprache in eigenen fire-Programmen, [L6](#l6-compiler-und-vm-sind-bibliotheken-und-packages)). .NET bleibt danach nur noch in
**spark**, bis die IDE nach fire portiert ist ([Phase F](#phase-f---die-ide-und-ember-nach-fire-ausblick)).

Das ändert das Ziel aus [ROADMAP.md](ROADMAP.md) ("ember, forge und spark in fire neu schreiben"): der Compiler braucht ohnehin ein Frontend
in C (Henne und Ei), also entsteht er gleich ganz dort; die VM ebenso. spark wird fire, ember voraussichtlich auch.
Die Leitlinien von dort gelten weiter (Packages bleiben Code-only; neuer nativer Code in C gegen `fire_rt.h`; VM und nativ verhalten sich gleich).

**Harte Bedingung: der native Build wird nicht langsamer und nicht größer.** Was die VM von der Runtime braucht, darf ein natives Programm
nichts kosten ([L1](#l1-eine-runtime-die-vm-ist-ein-interpreter-auf-fire_rth), [Prüfungen](#prüfungen-für-den-nativen-build)).

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
| `fire.Package.Manager`, `fire.Ember` | 1 700 | Manifest, Store, `.fpk`, Quellen | was forge zum Bauen braucht (Manifest, Store lesen, `.fpk` packen/entpacken) -> C; ember selbst (Quellen, Installieren, Befehlszeile) voraussichtlich -> fire, mit der IDE |
| `fire.Terminal*`, `*.Bridge`, `fire.Device.Manager` | 9 500 | Grafik, Fenster und Geräte der VM in C# | **entfallen**: die C-VM benutzt die C-Natives der Packages wie ein natives Programm (Geräte-Manager von spark: Phase B Schritt 9, dann Phase F) |
| `fire.Testing` | 51 900 | Testprogramm (beide Engines, Editor, Debugger) | Fälle werden Daten, der Läufer C; Editor-Tests bleiben bis Phase F |
| `fire.Editor`, `fire.Git` | 34 100 | spark | bleibt C# bis Phase F |

Zu portieren sind damit rund **75 000 Zeilen** C#; rund 20 000 fallen weg, weil die Runtime in C sie schon enthält. Schon in C vorhanden und
wiederverwendbar: `native/runtime` (17 000 Zeilen: Werte, Objekte, Besitz, Strings, Arrays, JSON, Reflection, Threads/GIL, Tasks, Aktoren,
Globals, Probes, Module/Plugins, Debug-Agent), die Plattformschicht, Deflate/Zip (`packages/compression`), SHA-256 (`packages/hash`), Prozesse (`packages/process`).

## Leitentscheidungen

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

**Der native Build zahlt nichts dafür.** Regeln:

* Was nur die VM braucht (Aufruf nach Namen, Tabellen zur Laufzeit, dynamische Scopes, Laden und Entladen eines Programms), steht in `native/vm`
  oder in der Runtime hinter `FIRE_VM` - ohne den Schalter ist `fire_rt.h` für den Präprozessor dasselbe wie vorher.
* Keine neuen Felder in `Value`, im Objektkopf, in Klassen-Deskriptoren oder Frames, keine neuen Prüfungen auf den schnellen Pfaden. Braucht die
  VM mehr Daten je Objekt oder Klasse, liegen sie in Seitentabellen der VM.
* Wo das nicht reicht, verliert die VM: sie nimmt den langsameren Weg oder eine eigene Fassung der Funktion, die Runtime bleibt.

Risiko: `fire_rt.h` ist auf erzeugten Code zugeschnitten (statische Tabellen, `fatal` beendet den Prozess). Darum beginnt Phase B mit einem
Spike, der genau das - unter diesen Regeln - prüft, bevor irgendetwas anderes portiert wird. **Geprüft** ([VM_SPIKE.md](spec/VM_SPIKE.md)): L1 trägt, mit einem
Eingriff hinter `FIRE_VM` (`exitNow` kehrt zur VM zurück); der native Build ist byte-gleich.

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
  und Eingaben über einen gemeinsamen Speicherbereich bzw. das Protokoll. Das ist der heikelste Teil des Umschaltens (Phase B Schritt 9).

Ein abgestürztes Programm reißt die IDE dann nicht mehr mit.

Der Umbau von spark soll kurz sein und früh kommen, damit in der Zwischenzeit wenig doppelt zu pflegen ist. Er ist deshalb keine eigene Phase am
Ende, sondern hängt an den Meilensteinen: mit M1 gehen Ausführen, Debuggen und die eingebetteten Ansichten auf die C-VM, mit M2 die Sprachdienste
auf `forge serve`. Bauen läuft von Anfang an über die Befehlszeile von forge - ob dahinter C# oder C steckt, sieht spark nicht.

### L5. Tests werden Daten

`fire.Testing` hält die Fälle als C#-Text (`Check(...)`, `CheckNat(...)`, ... - rund 800 Aufrufe allein in `Program.cs`). Sie werden einmal
exportiert nach `tests/cases/<gruppe>/<name>.fire` mit `.expected` und einer kleinen Kopfzeile (Engine, Modus, Optionen); das C#-Testprogramm
liest sie von dort, solange es lebt, der Läufer in C (`forge conformance`) später dieselben Dateien. Tests in fire selbst (`[Test]`,
`packages/*/testing`) laufen unverändert über `forge test`.

### L6. Compiler und VM sind Bibliotheken und Packages

`forge` und `fire` (die VM) sind dünne Programme um zwei C-Bibliotheken, `libforge` und `libfirevm`, mit einer **C-Schnittstelle**
(`fire_embed.h`: Quelltexte zu einem Programm übersetzen, Diagnosen, ausführen, Ausgabe umleiten, Regeln für Dateien und Netz, eigene Funktionen
des Wirts, anhalten). Diese Schnittstelle ersetzt das Einbetten in .NET (`Help/Embedding.md`); ein dünner **.NET-Wrapper** darüber steht auf der
Roadmap, nach M3.

Dieselben Bibliotheken kommen als **Packages** (`#import "forge"`, `#import "script"` - Namen offen): ihre Natives sind die C-Quellen von
`native/forge` und `native/vm`, wie bei jedem Package im Build des Programms mitübersetzt. Ein fire-Programm kann damit fire-Quelltext zur
Laufzeit übersetzen und ausführen - und spark (in fire) bekommt seine Sprachdienste notfalls auch ohne Prozess.

Das stellt Anforderungen an den C-Code, die von Anfang an gelten:

* **Kein globaler Zustand** in Compiler und VM: alles hängt an einem Kontext, mehrere Übersetzungen und Programme nebeneinander.
* **Kein `exit`, kein `fatal`**: Fehler kommen als Ergebnis bzw. als Ausnahme beim Wirt an; ein Skript endet, der Wirt läuft weiter und räumt ab
  (alles, was dem Skript gehörte, wird zerstört - Klassen, Globals, Threads).
* **Im nativen Wirt teilt das Skript dessen Runtime** (L1): Werte gehen ohne Kopie hinüber. Was Objekte des Wirts im Skript dürfen (Besitz,
  Klassen des Wirts sichtbar machen, was das Weglassen ungenutzten Codes dafür behalten muss), ist ein eigener Entwurf vor dem Package - der Port
  muss es nur nicht verbauen.
* Der Compiler braucht nichts vom Wirt außer Speicher und Dateien (über Rückrufe), damit er auch auf kleinen Zielen übersetzbar bleibt.

Der Spike in Phase B prüft deshalb auch: zwei Skripte nacheinander in einem Prozess, das erste mit einer unbehandelten Ausnahme beendet.

## Aufbau im Repository (Vorschlag)

```
native/runtime/    (wie heute) die Runtime, gemeinsam für native Programme und die VM
native/base/       Arena, Strings, geordnete Maps, Pfade/Dateien, Prozesse, JSON-Leser, Zip, SHA-256 für die Werkzeuge
native/vm/         libfirevm: Lader (.fbc), Interpreter, Natives-Tabelle, Host der Paket-Natives, Debug-Agent der VM; Programm `fire`
native/forge/      libforge: lexer, preprocessor, parser, resolver, typecheck, compiler, linker, markup/, gen/ (Generator), build/ (Toolchains),
                   project/, pkg/, serve/; Programm `forge` (cli)
native/embed/      fire_embed.h: die C-Schnittstelle über beiden
packages/forge, packages/script   die Packages dazu (Preludes; ihre Natives sind die Quellen oben)
tests/cases/       die Testfälle als Dateien
```

Gebaut wird ohne Build-System: je Programm eine Datei, die alles einbindet (`cc -O2 native/forge/forge.c -o forge`), dazu `build.sh`/`build.bat`.
Übersetzen müssen die Werkzeuge mit gcc, clang und MSVC für Windows, Linux und macOS (nicht mit Open Watcom - das bleibt ein Ziel, kein Wirt).

## Phasen

Gearbeitet wird **phasenweise nach `master`**: jeder Schritt einer Phase ist ein eigener, kleiner Merge, der `master` lauffähig lässt (neuer
C-Code liegt daneben, bis er umgeschaltet wird). Das C#-Gegenstück wird erst entfernt, wenn der Vergleich über den ganzen Korpus grün ist.
Der **Korpus**: alle Testfälle, `packages/**/*.fire` mit ihren Tests, die Beispiele und Vorlagen aus `fire.Project`.

### Prüfungen für den nativen Build

Gelten für jeden Merge, der `native/runtime` oder den Generator berührt, und sind ab Phase A Teil der Tests:

* `fire_program.c` über den Korpus **byte-gleich** zum Stand davor (der Port ändert den erzeugten Code nicht);
* **Größe**: `forge build --stats` der Referenzprogramme (Hello World, minimales `ui`-Programm, ein DOS-Ziel) - Code und Daten nicht größer;
* **Geschwindigkeit**: die Benchmarks aus NATIVE_BACKEND ("Messwerte") nicht langsamer, über die Messschwankung hinaus;
* **Bauzeit**: nicht länger (forge in C sollte sie verkürzen);
* **geteilt** ([NATIVE_SPLIT.md](spec/NATIVE_SPLIT.md), Phase A Schritt 7): die native Testgruppe auch mit `FIRE_SPLIT=1` - dieselben Ausgaben; jede
  Einheit byte-gleich, die eine Datei (`FIRE_SPLIT=0`) ohnehin.

Größe und Geschwindigkeit misst `tests/native-baseline.py measure <forge> <stand.tsv>`: die neun Programme aus `tests/bench` (dieselben, die
`fire.Benchmarks` in der VM misst) und ein leeres, nativ gebaut - Bytes, Code, Daten, Bauzeit, kürzester von 30 Läufen, Ausgabe;
`compare <vorher.tsv> <nachher.tsv>` meldet, was größer oder (über die Toleranz hinaus) langsamer wurde. Stand heute (gcc 13, Linux x64): ein leeres
Programm 22,7 KB, `list` 104,7 KB; zwei Messungen hintereinander geben dieselben Größen und Zeiten innerhalb weniger Prozent. Noch nicht dabei: ein
`ui`-Programm und ein DOS-Ziel (braucht Open Watcom).

### Phase A - Vorbereitung im C#-Bestand

Noch kein C; alles hier ist für sich nützlich und macht den Rest prüfbar.

1. ~~**`.fbc`** festlegen und in C# schreiben/lesen~~ (umgesetzt): `ProgramFile` schreibt und liest ein gelinktes Programm
   ([BYTECODE.md, Abschnitt 48](spec/BYTECODE.md#48-das-dateiformat-fbc)); Packer, Payload, Plugins (`FIREPLG2`) und die Schnittstellen der Module
   benutzen es, **MemoryPack ist entfernt**. `forge bytecode` schreibt eine Datei, `forge dump` gibt ein Programm als Text aus (`ProgramDump`).
   Tests: `FIRE_TESTS_ONLY=programfile`.
2. **Tests als Daten** (L5), in Schritten nach Familien:
   * ~~Die Fälle "nativ == VM"~~ (umgesetzt): 436 der 448 Fälle sind Dateien in `tests/cases/native` (`.fire` mit Titelzeile, `.expected`, bei
     einem Abbruch `.unhandled`; [testing.md](tools/testing.md#the-cases-as-files-testscases)), `Program.cs` ist dadurch von 38 000 auf 21 000 Zeilen
     geschrumpft. Das Testprogramm liest sie (`TestCases.cs`) und prüft zusätzlich die VM gegen die festgehaltene Ausgabe; `FIRE_TESTS_UPDATE=1`
     schreibt sie neu. Die 12 übrigen Fälle entstehen zur Laufzeit (Zertifikat, Ordner des Laufs, Datenbankserver) und bleiben im Code.
   * **Was auf `master` dazukommt**, solange dort noch Text in `Program.cs` steht: `tests/tools/merge-master.sh` holt neue und geänderte Fälle beim
     Merge in die Dateien (zuerst gebraucht für den Merge bis PR 49: ein neuer Fall).
   * ~~Die Familien, die nur in der VM laufen~~ (umgesetzt): 193 der 197 Prüfungen von `CheckPerf`, `CheckLife`, `CheckShutdown`, `CheckGl`, `CheckLq`,
     `CheckRf`, `CheckSc`, `CheckScChecked`, `CheckExt`, `CheckArr`, `CheckClone` und ihren Fehler-Varianten sind Dateien in `tests/cases/vm-*`
     (mit Modi, `packed`, `.error`); das Testprogramm ruft je Ordner dieselbe Prüfung auf wie vorher. `Program.cs`: noch 18 700 Zeilen.
   * ~~Der Läufer ohne das Testprogramm~~ (umgesetzt): `tests/run-cases.sh <forge>` führt jeden Fall mit `forge run` aus und vergleicht - das Maß für
     forge und die VM in C. Stand mit dem C#-forge (nach dem Merge bis PR 56): 1061 von 1064 richtig. Neun Reflection-Fälle bekamen dafür ihr `#import "reflection"` (das
     Testprogramm hatte die Bibliothek stillschweigend dazugelegt), ein Fall ist als nur im Testprogramm sinnvoll markiert (`//# skip: forge run`).
   * **Offen - zwei Fälle weichen unter `forge run` ab** (im Testprogramm, das die VM im eigenen Prozess startet, laufen sie): `native/4180` und
     `native/4190` (http.pages: richtige Ausgabe, danach `fire runtime error: Index was outside the bounds of the array.`, Exitcode 1). Ursache
     klären, bevor diese Fälle als Maß für die C-VM gelten. (`native/0030` und `native/2340` sind auf `master` angepasst, PR 54.)
   * ~~Die Einzeltests am Anfang von `Program.cs`~~ (zum Teil umgesetzt): die rund 130 Abschnitte dort druckten Ausgabe und einen Satz, was zu erwarten
     sei - **geprüft wurde nichts**. 58 davon (ein Skript, läuft mit `forge run` sauber durch) sind jetzt Fälle in `tests/cases/smoke` mit
     festgehaltener Ausgabe; der alte Satz steht als `//# note:` im Fall. Zwei dieser Sätze waren überholt (Enums in Namespaces drucken Namen statt
     Zahlen; ein nicht fertig konstruiertes Objekt ruft kein `destruct()` mehr) - festgehalten ist, was die VM heute tut.
   * ~~Diagnosen~~ (umgesetzt, zusätzlich zu den Prüfungen in C#): jede Quelle, die `DiagnosticTests`, `TypeTests` und `SyntaxTests` übersetzen, ist
     ein Fall in `tests/cases/diag`, `types`, `syntax` (375) mit dem, was forge dazu sagt (`.diagnostics`: Warnungen, Fehler, Abbruch) und ausgibt -
     das Maß "Diagnosen gleich" für das Frontend in C. `tests/tools/cases.py record <forge> <ordner>` schreibt sie neu.
   * **Offen:** die übrigen Abschnitte am Anfang von `Program.cs` (eigene Natives, mehrere Dateien, erwartete Fehler, Aufbau von Hand); Completion
     (`CheckCompletion`) mit `forge serve`. Was am Wirt hängt (`CheckUi`, `CheckDev`, `CheckCb`, `CheckIo`, `CheckNet`, `CheckGf`), an Fenstern oder
     an Toolchains, bleibt Code.
3. ~~**Determinismus**~~ (geprüft, es war nichts zu beseitigen): zweimal übersetzt ergibt byte-gleiche `.fbc` und denselben Text von
   `fire_program.c` - für ein Skript mit einem Stück von allem und für jedes Beispiel von spark (dieselben Tests), von Hand auch über zwei Prozesse.
4. ~~**Referenzstände**~~ (umgesetzt): `tests/reference.sh <forge> <ordner>` schreibt für jeden Fall aus `tests/cases` und jedes Beispiel von spark
   den Text des Programms (`forge dump`), die Programmdatei, das C des nativen Backends und das Protokoll von forge, dazu `manifest.sha256`; zwei
   Stände vergleicht `diff -r`. Der Stand des C#-Compilers: 460 Programme ohne Fehler, rund 1 GB, auf 4 Kernen eine halbe Stunde (nicht eingecheckt -
   er ändert sich mit jedem Commit am Compiler). Größe und Geschwindigkeit nativer Programme misst `tests/native-baseline.py`
   ([Prüfungen für den nativen Build](#prüfungen-für-den-nativen-build)), die Diagnosen stehen bei den Fällen (`.diagnostics`). ~~Der AST als Text~~ (umgesetzt mit Phase C 1/2): `forge tokens`/`forge ast`
   ([FORGE_C.md](spec/FORGE_C.md)).
5. ~~**spark baut nur noch über die Befehlszeile von forge**~~ (umgesetzt; statt `NativeBuilder`/`ProjectBuilder` im eigenen Prozess) - der erste Teil von L4,
   noch ganz in C#. Build, Build virtual, Build Solution (`forge build <sln> --all`), der Build eines nativen Laufs und Debuggens (`--debug-info`, auch
   ESP32), Publish, Pack Library und Install laufen als `forge build|publish ... --report json` im Kindprozess (`ForgeProcess` in spark): der
   [Bericht](tools/forge.md#the-report-for-tools) - eine JSON-Zeile je Ereignis: Phasen, Hintergrundarbeit, Log, Diagnosen mit Datei/Zeile/Spalte/Code,
   Ausgaben, Projekte, Ende - geht in Build-Log, Build-Fenster, Statusleiste und Fehlerliste. Abbrechen: `cancel` auf stdin (überall) bzw. SIGTERM,
   forge beendet seine Werkzeuge und löscht seine temporären Ordner, nach zehn Sekunden tötet spark den Prozessbaum. **Ungespeicherter Text:** spark
   speichert nicht vor dem Bauen, sondern gibt geänderte Dokumente als `--overlay <datei>=<kopie>` mit (wie bisher baut spark, was der Editor zeigt).
   Was spark selbst entscheidet (Ziel der Werkzeugleiste, Modus, Ausgabeort, Statistik), geht als `-t`/`-m`/`-o`/`--stats` mit; die Frage nach einem
   fehlenden Compiler stellt spark vor dem Start von forge. Im eigenen Prozess bleiben: das Übersetzen für einen Lauf in der VM (die VM läuft noch in
   spark, Phase B 9), die Sprachdienste (Phase C 6), Clean (baut nichts), der Test einer Toolchain im Dialog. Prüfung: `FIRE_TESTS_ONLY=forgereport`.
6. ~~Tabellen, die beide Seiten brauchen, aus **einer** Quelle~~ (umgesetzt): `native/tables` hält `opcodes.def` (Name, Nummer, Operandengrößen),
   `errors.def` (Code, Bereich, Text), `natives.def` (die Natives jedes Programms mit ihrem Index) und `prelude.fire` (das Prelude als ein Text).
   Die Quelle bleibt der C#-Code, solange er lebt: `forge tables native/tables` schreibt die Dateien, die Tests (`FIRE_TESTS_ONLY=tables`) schlagen
   fehl, wenn sie nicht dem Code entsprechen. Die `.def`-Dateien sind Listen von Makroaufrufen, die C direkt einbindet (mit gcc und clang geprüft).
   **Offen:** die Tabellen hinter den Natives der Basistypen (`__StringCall` mit einer Methodennummer: `StringMethods` usw.) - die Nummern stehen im
   Prelude, die Bedeutung in C# und in `fire_rt.h`; die Codes der Typprüfung (`TC...`), die keinen Katalog haben.
7. **Das C in Einheiten aufteilen** ([NATIVE_SPLIT.md](spec/NATIVE_SPLIT.md), Phase 1 und 2) - noch im C#-Generator, **vor Phase D**: der Port übernimmt
   dann den aufgeteilten Generator 1:1 (L3), statt ihn danach ein zweites Mal umzubauen. Die Teilung ist ein letzter Schritt über den fertigen Text
   (`ProgramSplit`), die Runden, das Weglassen und die Durchgänge über den Text bleiben, wie sie sind. Prüfung wie für die eine Datei: jede Einheit
   **byte-gleich** zum C#-Generator; die eine Datei (`"split": "off"`, kleine Programme, TinyCC) byte-gleich zum Stand davor. In die
   [Prüfungen für den nativen Build](#prüfungen-für-den-nativen-build) kommt `FIRE_SPLIT=1` als Testschalter (die native Testgruppe damit, wie
   `FIRE_MODULES=1`). Phase 1 ist umgesetzt (Stand und Messungen in NATIVE_SPLIT.md); offen sind Phase 2 (Cache der Objekte, Namen aus dem Inhalt)
   und die Größe des geteilten Programms (siehe dort).

### Phase B - die VM in C

0. **Spike** (entscheidet L1): Interpreter für rund 30 Opcodes auf `fire_rt.h` - Rechnen, Locals/Scopes, Aufrufe, eine Klasse mit Feldern und
   Destruktor, `try`/`catch`/`resume`, eine Klasse zur Laufzeit registriert, zwei Skripte nacheinander in einem Prozess (L6). Ergebnis: geht es
   ohne Eingriffe in die Runtime, mit Eingriffen hinter `FIRE_VM`, oder gar nicht - gemessen an den Prüfungen für den nativen Build.
   **Umgesetzt** (`native/vm`, [VM_SPIKE.md](spec/VM_SPIKE.md)): Lader für das ganze `.fbc`-Format, Interpreter mit 69 Opcodes auf den Funktionen der
   Runtime (Scopes als `OwnList`-Paare, Ausnahmen über ihren Handler-Stapel, Klassen-Deskriptoren und Catch-Typen beim Laden registriert), gebaut mit
   `cc -O2 native/vm/fire.c -o fire -lm` (gcc, clang). Die Skripte in `native/vm/tests` geben dasselbe aus wie `forge run`; 16 Programme nacheinander in
   einem Prozess, darunter zwei mit unbehandelter Ausnahme bzw. `fatal`, lassen jeweils nichts zurück. **Ergebnis: mit Eingriffen hinter `FIRE_VM` - genau
   einem** (`exitNow` ruft `fire_vmExit`, 8 Zeilen); ohne den Schalter ist `fire_rt.h` für den Präprozessor dasselbe, `fire_program.c` und die
   Executables sind byte-gleich, `native-baseline.py compare`: nichts größer oder langsamer. Die C-VM ist dabei 2,4- bis 6-mal so schnell wie die
   C#-VM. Was in Phase B einen eigenen Weg der VM braucht (Rückrufe ohne Kontext wie `TArg.holds`, die feste Konfiguration der Runtime für
   `#intwidth`/`#floatwidth`, Klassen-Ids bei gleichzeitigen Programmen), steht dort unter "Was nicht passt".
1. `native/base` und der Lader. **Umgesetzt** ([VM_C.md](spec/VM_C.md)): `native/base/fire_base.h` (Arena, internierte Namen, geordnete Map,
   Dateien - ohne globalen Zustand); der Lader behält das ganze Programm bis auf Reflection-Meta, Externs und Debug-Namen, die Einheiten und Enums
   der Datei werden beim Start die Tabellen der Runtime.
2. Interpreter vollständig: alle Opcodes, die drei Modi (Debug/Release/Performance), Lambdas und Captures, Properties, Überladungen, Generics,
   typisierte Arrays, Zeiger, Records, Besitz (`take`, `TakeTo`, Kopien), Shutdown an sicheren Punkten.
   **Zum größten Teil umgesetzt** ([VM_C.md](spec/VM_C.md)): alle Opcodes außer denen der Threads/Aktoren (Schritt 3) und der Probes (Schritt 5);
   Überladung nach Typ, Properties, Operatoren, Erweiterungen der Basistypen, Generics mit Typargumenten je Objekt und Aufruf, Spezialisierungen,
   `DeclaredType`, Arrays einer Klasse und nach Typargument, Records, `finally`, Einheiten des Programms mit Umrechnungsfunktionen, Zugriffsprüfung
   (nicht in Performance). Die Rückrufe ohne Kontext (VM_SPIKE Punkt 1) sind eigene Fassungen der VM bzw. ein Vorrat an Trampolinen; die Runtime ist
   unverändert (der native Build ebenso). Maß: `tests/run-cases-vm.sh <forge>` übersetzt jeden Fall mit dem C#-forge und führt ihn mit der C-VM aus -
   **751 von 1126** richtig (Spike: 306); von den übrigen brauchten rund 320 die Schritte 3 bis 5. **Abgeschlossen** (mit den Schritten 3 bis 5
   zusammen): **1126 von 1128**, ASan ohne Befund über den ganzen Korpus. `#intwidth`/`#floatwidth`: eine VM je Breite aus denselben Quellen
   (`fire-i32`, `fire-f32`, `fire-i32-f32`, `fire-i16`, ...; der Mechanismus von Schritt 7, [VM_C.md](spec/VM_C.md#breiten)), die VM rechnet in ihnen
   richtig (Deklarationen `int` schneiden breite ints, ints aus Natives behalten 64 Bit, Zeiger der VM als 64-Bit-ints); dazu `try take`/`Takes.Children`, `defined`-Arrays und typisierte Felder, Typargumente mit Einheiten, die
   Reihenfolge der Destruktoren nach einer Ausnahme (Wurfort vor catch), Zeiger auf Lokale, die Meldungstexte, die UI-Fälle (Probes und Besitz), Natives
   ohne GIL mit Sektionen der Threads, während das Hauptprogramm weg ist (zweiter Eingriff hinter `FIRE_VM`). Offen nur `native/3210`, `4110`:
   Textmetriken von `libfire_gfx` gegen den nativen Build - die C#-VM gibt dasselbe aus wie die C-VM.
3. Threads, Tasks, Aktoren, `sync globals`, Abbruch, `on`-Lambdas - über die Abschnitte der Runtime.
   **Umgesetzt** ([VM_C.md](spec/VM_C.md#threads-schritt-3-vm_threadsc)): das Threadmodell der Runtime mit GIL (ein Thread des Systems je fire-Thread,
   ein `Vm` je Strang, die Globals des Hauptprogramms direkt und in Sektionen), sichere Punkte wie im erzeugten Code, `leave`/`terminate` mit `finally`
   und Destruktoren der Globals, `sync globals`, `#globaljobs`, `fire global`/`await on`, Aktoren (`process`, `await a.M()`), Regel 10, `catch threads`/
   `catch terminate`, Kanäle, `Completion`, Abbruch, `await [...]` (nacheinander), `handover`, Lambdas mit einem Ziel in einem anderen Thread, `Sleep`.
   Rückrufe ohne Kontext: die Funktion steht in den Argumenten (Thread, Nachricht), `catch`-Handler über Trampoline. Die Runtime ist unverändert.
   Maß: **910 von 1128** (vorher 751); `vm-gl`, `vm-shutdown`, `vm-life` ganz, die Thread-Fälle von `native` bis auf die, die Natives der Schritte 4/5
   brauchen; unter ASan/TSan ohne Befund. **Offen**: Tasks (`FIRE_TASKS` gibt es nur ohne GIL - dafür bräuchte die VM Sperren um ihre Tabellen und
   ihren Zustand am Task, ein Eingriff in die Runtime); `await [...]` läuft deshalb nacheinander.
4. Natives: eine Tabelle Name -> Funktion für die Kern-Natives (in der Runtime vorhanden, heute vom Generator direkt aufgerufen); Paket-Natives
   zunächst unverändert als Shared Library über die [C-ABI v1](../native/abi/fire_pkg_abi.h), den Host (`fire_host`: IO-/Netz-Regeln, Lambdas, Dienste) in C.
   **Umgesetzt** ([VM_C.md](spec/VM_C.md#natives)): die Kern-Natives als Tabelle nach Namen; die Natives der Importe nach Index (graphics, windows,
   reflection: `native/tables/bridges.def`, von `forge tables`; dann die der Pakete), beim Laden nach Namen gebunden - an eine Funktion der VM (was ein
   Paket seinem Host lässt: `Sleep`, `__time_unit_ticks`, die Wartefunktionen der Geräte) oder an ihre Bibliothek: dieselben Shared Libraries, die
   `PackageLibrary` für die C#-VM baut, auch `libfire_gfx` für Fenster (`FIRE_DISPLAY=headless` ohne Bildschirm), gesucht in `FIRE_PKG_PATH` und neben
   der Programmdatei (`Packages/`, `Prebuilt/` eines forge). `fire_host` in C mit allem, was die Kopfdatei hat (Konsole, IO-/Netz-Regeln - alles
   erlaubt wie in der gepackten C#-Runtime -, Lambdas mit dem Aufschub ihrer Ausnahmen bis zum Ende des Natives, Aufrufe von anderen Threads am
   nächsten sicheren Punkt, Dienste, Leerlauf, Halte auf dem Ende des Programms). Dazu `Library` und `extern` über einen Aufruf nach der Signatur zur
   Laufzeit (System V x86-64, AArch64). Offen: Regeln eines Hosts und seine Geräte (spark, Schritt 9), `Library`/`extern` unter Windows x64 (`inline c` läuft wie in der C#-VM nur nativ:
   sein Aufruf ist eine abfangbare `ArgumentException`).
5. Reflection, JSON, Probes, Module und Plugins in der VM.
   **Zum größten Teil umgesetzt** ([VM_C.md](spec/VM_C.md#reflection-und-probes)): Reflection auf den Meta-Daten der Datei, die der Lader jetzt
   behält (`vm-rf` 18 von 18), Lesen/Schreiben/Aufrufen/Erzeugen nach Namen auf den Wegen des Interpreters; JSON mit der Engine des nativen Builds
   (`fire_json.h`) auf Tabellen, die die VM aus dem Programm baut (`$id`/`$ref`, Einheiten, Enums, Generics); Probes mit dem Probe-Speicher der Runtime
   (`FIRE_REFLECTION` in der VM) und einer eigenen Fassung von `probedSet` nach Namen statt der Rückrufe ohne Kontext; `linq` (bis auf einen Fall von
   Besitz aus Schritt 2). Fire-Plugins (das Laden der Klassen eines zweiten Programms in ein laufendes): seit dem 22.11.2026 ([VM_C.md](spec/VM_C.md#plugins)).
   Maß: **869 von 1128** (vorher 751); was fehlt, ist Schritt 3 (rund 185) und Schritt 2 (rund 75).
6. Debug-Agent (L4): Haltepunkte, Schritte, Variablen über die Scope-Ebenen, `set`, Pause, Zeilenprofil.
   **Umgesetzt** ([VM_C.md](spec/VM_C.md#debugger-schritt-6-vm_debugc)): `fire x.fbc` mit `FIRE_DEBUG`/`--debug` spricht das Protokoll nativer Programme über
   den Agenten der Runtime (tcp, stdio), `NativeDebugTarget` debuggt die VM wie ein natives Programm; die Halte entscheidet ein Haken vor jeder Instruktion
   wie die Schrittschleifen der C#-VM (verschachtelte Frames unsichtbar), die Frames und Scopes baut die VM für den Agenten, wenn ein Thread anhält.
   Die `.fbc` eines Debug-Programms trägt seine Quellen (Datei, SHA-256). Tests: `FIRE_TESTS_FILTER=VM-Debugger` - die Fälle des nativen Debuggers gegen
   die C#-VM und seine Protokoll-Prüfungen. Offen: Eingabe über den gerahmten Transport für Paket-Natives. Plugins: seit dem 22.11. (ihre Quellen, `sources`).
7. Programm `fire` (`fire run x.fbc`); der Korpus, vom C#-Compiler übersetzt, läuft mit den erwarteten Ausgaben; Benchmarks gegen die C#-VM.
   **Umgesetzt** ([VM_C.md](spec/VM_C.md#das-programm-fire-schritt-7)): `fire run x.fbc [--] args`, `fire a.fbc b.fbc -- args`, `help`, `version`,
   `--debug`, `--stats`, `--time`; Exitcodes 0, 1 (unbehandelt), der von `terminate`, 2 (nicht ladbar, Befehlszeile). Eine VM je Paar von Breiten
   (`fire-i32`, `fire-i32-f32`, ...: dieselben Quellen mit `FIRE_INT32`/`FIRE_FLOAT32`); `fire` startet für ein Programm anderer Breiten die Variante neben
   sich. Gebaut mit forge: `forge build-vm` nach jedem Build von forge/spark/Tests (`src/FireVm.targets`) neben forge, ein Publish nimmt es mit
   (`setup.iss`); entschieden: auf der Maschine gebaut (forge trägt die Quellen), nicht vorgebaut je RID. Maß: **1073 von 1128** (mit den Varianten der Breiten; vorher 1065), mit den Korrekturen von Schritt 2 zusammen **1126 von 1128**, am 22.11. **1149 von 1149**; Benchmarks (`tests/bench`, Performance): die C-VM 1,7- bis 9-mal so schnell wie die C#-VM (zusammen 255 ms gegen 697 ms), 13,7 MB RSS gegen 77-129 MB des C#-Prozesses.
8. `forge build --engine vm` bleibt: die C-VM mit angehängtem `.fbc` (und den Bibliotheken der Packages) statt der .NET-Einzeldatei.
   **Umgesetzt** ([VM_C.md](spec/VM_C.md#gepackte-programme-schritt-8)): eine Datei - `fire` der Breiten des Programms, dahinter die `.fbc` und die
   Bibliotheken der Pakete (Payload `FIREVMP1`); die Bibliotheken werden beim ersten Start in den Cache des Benutzers ausgepackt (ein Ordner je
   Prüfsumme; entschieden: ausgepackt statt aus dem Speicher, wegen Windows). `fire` neben forge, sonst mit dem C-Compiler der Maschine in den Cache
   gebaut. Die .NET-Einzeldatei (`FIRE_VM_ENGINE=dotnet`) ist mit M1 (6) weg. hello world: 748 KB (die .NET-Einzeldatei 729 KB plus das installierte
   .NET). Windows (MinGW) übersetzt, nicht gelaufen. Tests: `FIRE_TESTS_ONLY=packer`.
9. **spark auf die C-VM** (L4): Ausführen als Kindprozess, Debuggen über `NativeDebugTarget`, `LiveSession` und `UiPreview` über Kindprozess und
   gemeinsamen Speicher, Treiber-Agent und Geräte.
   **Zum größten Teil umgesetzt** ([VM_C.md](spec/VM_C.md#spark-auf-der-c-vm-schritt-9)): F5/F10/F11/Ctrl+F5 mit der Engine `vm` übersetzen über
   `forge bytecode ... --report json` (mit den Overlays; der Bericht nennt die `.fbc`, die Bibliotheken der Pakete und das `fire` der Breiten) und starten
   `fire run x.fbc` als Kindprozess - Ausgabe ins Output-Panel (zeilenweise: `FIRE_STDOUT_LINES`), Eingabe über die Pipe, Exitcode, Stop; Debug über
   `NativeDebugTarget` (TCP, `"engine": "vm"`), dieselben Panels wie nativ (`FIRE_SPARK_VM=dotnet`, die C#-VM in spark, ist seit M1 (6) weg). `LiveSession`
   (Schaltplan-Editor) läuft in `fire`: Bilder, Zeiger und Ereignisse über eine Datei, die beide abbilden (`FIRE_DISPLAY_SHM`, der Host-Display der
   Bibliothek von graphics/windows), Nachrichten über stdout (`FIRE_PRINT_RECORDS`), Befehle über stdin (`__HostCommand`); ein Programm, das in `fire`
   scheitert, endet mit dem Grund (der Rückfall in die C#-VM ist seit 22.11. weg). Behoben in der VM: der Hauptthread wartete am Einstieg mit der GIL (Scopes am Einstieg hingen).
   `UiPreview` (Entwurfsansicht) und die Vorschau der Blöcke laufen als Kindprozess `fire` (`CVmScript`; die Entwurfsansicht über `FIRE_DISPLAY_SHM`,
   Pixel und Orte wie in der C#-VM; Latenz nach einer Änderung 604 gegen 520 ms, davon der Prozess `fire` 16 ms - der Rest ist das Übersetzen in spark).
   Das geteilte Gerät: ein Programm in `fire` nimmt die Geräte des `DeviceManager` von spark über `FIRE_DEVICE_HOST` (die `dev_*` von `fire_host` über
   eine lokale Verbindung, `native/vm/vm_devhost.c`, `DeviceHostServer`).
   Tests: `FIRE_TESTS_ONLY=cvmrun`, die Live-Sitzung in `circuiteditor`; unter Xvfb mit spark: Konsolen- und Fensterprogramm, Haltepunkt, Schaltplan.
   Der Schaltplan-Editor braucht den Rückfall nicht mehr (22.11.: die `DestroyedException` in `UI.CircuitView.DrawBackground` war das `SetField` auf ein
   Objekt mit Sonden, behoben mit `40e81bcb`; `vm-life/0110`, die Live-Sitzung mit jedem Beispiel-Schaltplan ohne Rückfall).
   **Offen**: das Debuggen der Plugins ist da, der Treiber-Agent läuft seit dem 22.11. in `fire` (fire-Plugins in der C-VM);
   ~~`FIRE_DISPLAY_SHM` und `__HostCommand` unter Windows~~ (unter Wine geprüft, `cvmwin`, siehe M1 (7)); "Build virtual" und `check` (MCP) übersetzen noch in spark (M2).

**M1:** forge (noch C#) und spark benutzen nur noch die C-VM. `fire.Runtime`, `fire.Terminal*`, die Bridges, die C#-Natives und `VmDebugTarget`
werden entfernt. **Erreicht** (23.11.2026) - was entfernt ist und was dabei wegfiel, steht unter "Entfernt" am Ende dieses Abschnitts; der Stand davor
bleibt als Geschichte stehen.
**Stand** (22.11.2026): Ausführen und Debuggen in spark und `forge build --engine vm` gehen über die C-VM. Was das Entfernen noch aufhält:
(1) ~~die Fälle des Korpus, die die C-VM noch nicht richtig macht, und der Fehler im Schaltplan-Editor~~ - **erledigt** (22.11.2026): der Korpus
**1149 von 1149** in der C-VM (vorher 1127 richtig, 17 falsch, 1 nicht gelaufen von 1145: veraltete `.diagnostics` seit den Stack-Traces, die GIL weckte
bei jeder Übergabe alle Wartenden - `native/3510` 0,87 s statt 0,15 s -, die Läufer ließen die Schrift des Desktops zu - 3210/4110, nicht die
Umgebung der Bibliothek); alle Fälle zusätzlich in Release, Debug und Performance gegen die C#-VM: gleich bis auf die Fälle, deren Ausgabe von Lauf zu
Lauf wechselt, und Performance, wo die C#-VM ungeprüft liest (undefiniert; sie stürzt dabei teils mit einer .NET-Ausnahme ab); Debug hat jetzt die
Warnung für Verweise auf zerstörte Objekte (`vm_dangle.c`); der Schaltplan-Editor läuft in `fire` ohne den Rückfall (die Ursache war schon mit
`40e81bcb` behoben). Der Rückfall der Live-Sitzung ist **weg** (22.11.2026: ein Programm, das in `fire` scheitert, beendet seine Ansicht mit dem Grund);
`FIRE_SPARK_VM=dotnet` ist seit dem 22.11.2026 weg (siehe (6)) ([VM_C.md](spec/VM_C.md#stand-gegen-den-korpus));
(2) ~~fire-Plugins in der C-VM (Treiber-Agent, `Library.Load` von `.fireplugin`)~~ - **erledigt** (22.11.2026,
[VM_C.md](spec/VM_C.md#plugins)): `vm_plugin.c` lädt den Bytecode eines Plugins wie die C#-VM (Prüfungen und Meldungen, Basisklassen, Natives nach Namen und in
C++, `class extends`, Einheiten, Enums, statische Initialisierer, Debuggen mit dem Ereignis `sources`); die Programmdatei trägt dafür die Schnittstellen ihrer
Module (Bit 3, C#-forge und forge-c); der Treiber-Agent läuft in `fire` (`FIRE_DRIVER_AGENT=dotnet` ist seit (6) weg); alle Plugin-Fälle von
`FIRE_TESTS_FILTER=plugin` auch in der C-VM; offen: die C-VM unter Windows; (3) ~~das geteilte Gerät zwischen spark und einem Programm
(`fire_host.dev_*`, `DeviceManager`)~~ - **erledigt** (22.11.2026): ein Protokoll statt Phase F - die 16 `dev_*` von `fire_host` gehen über eine lokale
Verbindung zum `DeviceManager` von spark (`FIRE_DEVICE_HOST=tcp:127.0.0.1:PORT;token=T`, `native/vm/vm_devhost.c`, `DeviceHostServer`; Treiber, Teilen,
Mitschnitt, Standardgerät wie in der C#-VM; ohne die Variable die Geräte der Maschine; [VM_C.md](spec/VM_C.md#spark-auf-der-c-vm-schritt-9), "Das geteilte
Gerät"); (4) ~~`UiPreview`/Blockvorschau in `fire`~~ - **erledigt** (22.11.2026): beide als Kindprozess `fire` (`CVmScript`), die Entwurfsansicht über
`FIRE_DISPLAY_SHM` (`Window.Idle` nach dem Dump legt das Bild in die Datei - keine Aufnahme in `fire_gfx_host.h` nötig), Pixel, Orte und PNG-Bytes wie in
der C#-VM; Latenz nach einer Änderung 604 ms gegen 520 ms im Prozess (der Prozess `fire` 16 ms, der Rest Übersetzen in spark); Test `FIRE_TESTS_ONLY=previews`;
(5) ~~`forge run`, `forge test` und die Tests (`fire.Testing`: RuntimeSession, VmDebugTarget, die C#-VM als Vergleich) hängen an `fire.Runtime`~~ - **erledigt**
(22.11.2026): `forge run`, `forge test` und `forge data` des C#-forge übersetzen wie bisher und lassen das Programm in `fire` laufen, wie forge-c
(`CVmRun`: `Linker { PackageLibraries }`, die `.fbc` mit Zeilentabellen nach `<temp>/fire-run-|fire-test-|fire-data-<guid>`, `fire run x.fbc -- <args>` mit der VM
der Breiten, `FIRE_PKG_PATH` mit den Ordnern der Bibliotheken; `run` mit der Konsole von forge - `CommandLineRunner.WritersAreConsole`, sonst über die Writer
des Aufrufers -, `test` liest die Zeilen des Treibers, während sie kommen (`FIRE_STDOUT_LINES`; der Test-Explorer von spark und `Cancel` beenden den Prozess),
eine unbehandelte Ausnahme ist die Zeile `Unhandled exception of type ...` von `fire`). Ausgabe und Exitcodes wie vorher (`tests/compare-commands.sh` gegen
forge-c: siehe "Geprüft" unten). Der Übersetzer braucht die C#-Bridges nicht mehr: die Namen der Natives von graphics, windows und reflection kommen aus
`native/tables/bridges.def` (`BridgeNatives`, wie forge-c), die Host-Natives der Pakete sind nur Namen. `fire.Testing`: jede Prüfung, die die C#-VM als Engine
oder als Vergleich nahm, läuft in `fire` (`CVmTest`: übersetzt hier, `fire run` mit `FIRE_PRINT_RECORDS`; `CVmTest.Debug`: unter dem Debugger wie spark) oder
gegen die `.expected` der Fälle; was nur die C#-VM selbst prüft, ist als solches markiert und läuft nur mit `FIRE_TESTS_DOTNET_VM=1` (`DotnetVm.Skip`) - die
Standardrunde braucht `fire.Runtime` nicht (Liste der Gruppen unten);
(6) ~~`forge build --engine vm` mit `FIRE_VM_ENGINE=dotnet`, der .NET-Packer~~ - **erledigt** (22.11.2026): `CVm.UseDotnet`/`EngineOverride` und der Weg über
`fire.Runtime.Packer` sind aus forge entfernt, `forge build --engine vm` packt nur die VM in C; `CVmPayload` (das Format, das `fire` liest) liegt jetzt in
`src/fire` (der Kern), der Stempel von `publish` nennt die Quellen der VM statt der Assembly der C#-VM. spark: `FIRE_SPARK_VM=dotnet` mit allem, was es
offen hielt, ist weg (`CVmLaunch.UseDotnet`, `RunsAsChild`, `CompileAndPrepareAsync`/`DebugSession.CompileAsync`/`VmDebugTarget` in spark - `Compile`/
`CompileAsync` leben nur noch im Testprogramm, `DebugSessionVm.cs` -, `LiveSession` im Prozess, `UiPreview.RenderHere`, der Rückweg der Blockvorschau,
`FIRE_DRIVER_AGENT=dotnet`, die Zeilenzählung `LineProfile` der C#-VM im Profiler); das Werkzeug `check` des MCP-Servers übersetzt mit forge wie Start.
spark referenziert `fire.Runtime` nicht mehr (nur noch `fire.Terminal` für die Bilder des Pixel-Editors);
(7) ~~Windows: `fire` ungeprüft gelaufen, `FIRE_DISPLAY_SHM`, `__HostCommand`,
`Environment.Args`~~ - **erledigt unter Wine** (22.11.2026, [VM_C.md](spec/VM_C.md#windows-m1-7)): `FIRE_DISPLAY_SHM` über `CreateFileMapping`/`MapViewOfFile`
(`HostDisplayFile` mit `DeleteOnClose`), `__HostCommand` über `PeekNamedPipe` oder einen Lese-Thread, `FIRE_STDOUT_LINES` ungepuffert, `Environment.Args` aus der
UTF-16-Befehlszeile (nur die des Programms), UTF-8-Pfade (`_wfopen`, `LoadLibraryW`, ...), die Variante über `CreateProcessW`, die Namen der Bibliotheken einer
`.fbc` eines anderen Systems; Test `FIRE_TESTS_ONLY=cvmwin` (`fire.exe` mit MinGW aus den Quellen von forge, unter Wine: Befehlszeile, gepackte Programme, die
Ansicht des Hosts mit dem Leser von spark, Befehle, das geteilte Gerät, der Korpus `native` 501 von 501 und 4 mit einer Ausgabe von Windows); offen: ein echtes
Windows, Plugins und Debuggen der C-VM unter Windows, spark selbst unter Windows.

**Was nach (5) und (6) noch auf `fire.Runtime`, `fire.Terminal*`, die Bridges, die C#-Natives und `VmDebugTarget` verwies** (22.11.2026, der Plan für das
Entfernen - umgesetzt am 23.11.2026, siehe "Entfernt" unten; geprüft mit einem
Build von `fire.Compiler` ohne die Referenz und ohne die Dateien unten - es übersetzt -, und von spark ohne die Referenz und ohne transitive Referenzen - es
übersetzt mit `fire.Terminal`). Das Entfernen ist danach mechanisch:

* **`src/fire.Compiler`** (Programm `forge`): die Referenz auf `fire.Runtime` in `fire.Compiler.csproj` hält nur noch Code, den forge und spark nicht mehr
  ausführen - zu löschen mit der Referenz: `RuntimeSession.cs` (die Sitzung der C#-VM; ihr `CreateProjectDirectiveRegistry` leitet nur noch an
  `ProjectDirectives.CreateRegistry` weiter), `Debugging/VmDebugTarget.cs`, `HostRenderer.cs` (fire.Terminal), `GraphicsLibrary.InProcess.cs` (die Bibliothek
  von graphics/windows im Prozess: `Register`, `Pixels`, `Instance`, `WarnFallback` - `PackageNativeBinding`, `PackageHost`), `PackageImports.InProcess.cs`
  (`RegisterForRun` - `PackageNativeBinding`), `Tables.Bridges.cs` (`forge tables` schreibt `native/tables/bridges.def` aus `GraphicsBridge.RegisterStubs`,
  `WindowsBridge.RegisterStubs`, `ReflectionNatives.Register`: danach ist `bridges.def` die Quelle - in `Tables.cs` der Eintrag `["bridges.def"] = Bridges()`
  und in `TablesTests` dessen Vergleich weg), in `GraphicsLibrary.cs` der Record `HostWindows` (nur `RuntimeSession`). `fire.Device.Manager` referenziert
  forge jetzt direkt (kam vorher über `fire.Runtime`). Die vielen `using fire.Runtime;`/`fire.Runtime.X` in forge sind der **Namensraum** des Kerns
  (`src/fire/Runtime`: `LinkedProgram`, `VmExecutionMode`, `TargetProfile`, `CVmPayload`, ...), nicht das Projekt.
* **`src/fire.Editor`** (spark): keine Referenz auf `fire.Runtime` mehr (sie kommt nur noch transitiv über `fire.Compiler`); direkt referenziert ist
  `fire.Terminal` - für Bilder, nicht für die VM: `PixelCanvas`, `ImageDecoder`/`ImageEncoder`/`ImageData` (`Editors/PixelEditorControl.cs`,
  `MainWindow/MainWindow.Projects.cs`). Wer `fire.Terminal` ganz entfernen will, zieht diese Codecs vorher in ein eigenes Projekt (oder nach spark).
  `Help/Embedding.md` beschreibt noch das Einbetten der C#-VM (`RuntimeSession`) - mit dem Projekt zu streichen (L6: `fire_embed.h`).
* **`src/fire.Testing`**: die Referenz auf `fire.Runtime` hält nur noch, was die C#-VM selbst prüft; die Standardrunde führt es nicht aus
  (`DotnetVm.Skip`, eine Zeile "(C#-VM: ... goes away with fire.Runtime ...)"; `FIRE_TESTS_DOTNET_VM=1` führt es aus, solange es das Projekt gibt):
  in `Program.cs` die Abschnitte zwischen `goto dotnetVm1` ... `dotnetVm22` und `dotnetVmUi` (Bytecode-Tests mit `new VM`, Multithreading/Actors über
  `FireRuntime`, Operatoren/Format/Strings/IO mit eigenen Natives, VmExecutionMode, Namespaces, ParseMultiple, die Familien vm-* in ihren C#-Helfern
  `CheckExt`/`CheckArr`/`CheckPerf`/`CheckClone`/`CheckLife`/`CheckShutdown`/`CheckGl`/`CheckLq`/`CheckRf`/`CheckSc` - die Fälle selbst laufen in `fire`
  über `TestCasesVm` -, "Grafik aus fire", "Netzwerk: Host-Richtlinie", die Callbacks, "Globals und Fire-Threads", LINQ/Reflection/Scope, Embedding.md, die
  UI-Bibliothek mit Test-Anzeige und den Test-Natives `__TestEvent`/`__TestClose`/`__TestMillis`/`__TestDragAnswer`/`__TestCanDrag`), im Packer-Abschnitt
  die Teile 1-4 (PackagePlan, PayloadFile, PayloadLoader, `Packer`) und die Single-File-Runtime (`BundleWriter`, `Packer`), die Helfer `UseIoPackage`,
  `FakeRenderer`, `TestHostDisplay`; `DebuggerTests.cs` (`VmRun`, `TargetRun`), `DebuggerPluginTests.cs` (`PluginRun`), `DebugSessionVm.cs`
  (`DebugSession.Compile`/`CompileAsync` über `VmDebugTarget`), `NativeDebugTests.cs` (`RunCsVm`/`RunVmLocked`), `NativeDebugTests.Vm.cs` (die Fälle C-VM
  gegen C#-VM), `DialogTests.cs`, `DockingTests.cs`, `WindowManagerTests.cs` (ganz: die Fenster von fire.Terminal.Windows mit Schein-Renderern),
  `ProgramFileTests.cs` (die Klasse `Session`, unbenutzt). Alles andere führt seine Programme in `fire` aus (`CVmTest`, `CVmTest.Debug`, `TestCasesVm`) oder
  vergleicht mit den `.expected` der Fälle.
* **`src/fire.Benchmarks`**: misst die C#-VM gegen die C-VM (`VM.Run()`), mit `fire.Runtime` zu entfernen oder auf `fire` allein umzustellen.
* **Die Projekte selbst**: `src/fire.Runtime` (mit `Packer`, `BundleWriter`, `PayloadLoader`, `PackagePlan`, `PackageNativeBinding`, `PackageHost`,
  `HostNatives`/`TimeNatives`, `ReflectionNatives`, `LineProfile`, dem Programm `runtime`, das der Build neben forge legt), `src/fire.Terminal.Bridge`,
  `src/fire.Windows.Bridge`, `src/fire.Terminal.Windows`, `src/fire.Terminal` (bis auf die Bild-Codecs von spark, oben), ihre Einträge in `fire.sln`.
* **Verhalten, das sich mit (5) geändert hat** (offen, nicht im Bereich dieses Schritts): `Environment.ProgramPath` ist bei `forge run` jetzt der Pfad von
  `fire` (die C-VM nimmt den Pfad ihres Programms, `fire_rt.h`), nicht mehr die letzte Quelle wie in der C#-VM - forge-c tut dasselbe; die Ausgabe eines
  Threads in spark trägt im Output-Panel nicht mehr den Namen des Threads (`fire` schreibt sie auf stdout, nicht je Thread); die Prüfungen der
  UI-Bibliothek, der Dialoge, des Dockings und des WindowManagers laufen nur noch mit der C#-VM, bis die C-VM eine Test-Anzeige mit Test-Natives hat
  (die UI-Pakete sind sonst über die Fälle "native == VM" und die Live-Sitzung geprüft).
* **Geprüft** (23.11.2026, Linux x64): `tests/compare-commands.sh` C#-forge gegen forge-c **100 von 103** gleich (alle `run`, `test`, `data` gleich; die drei
  sind Dateien nativer Builds und die gepackte Datei von `build -m debug --types strict` - das C#-forge vor dieser Änderung schreibt Byte für Byte dieselben,
  die gepackte Datei ist schon von Lauf zu Lauf verschieden); `tests/run-cases.sh` mit dem C#-forge **1152 von 1152**, `tests/run-cases-vm.sh` **1152 von
  1152**. Die Standardrunde von `fire.Testing` ohne `native` (`FIRE_TEST_NO_NATIVE=1`, 9 min): 2204 Prüfungen, danach behoben: die Fälle `.error` zur
  Laufzeit in `TestCasesVm`, die Prüfung "Eigener Manager" der Geräte (die C#-VM baute einen eigenen Manager ab) - übrig ein Fehler, der schon vorher da war
  (die Sprachdoku verlinkt `../spec/PLUGINS.md`, die Prüfung will Links im selben Ordner); einzeln danach grün: `debugger` 43, `cvmrun` 26, `previews` 10,
  `trace` 27, `syntax` 58, `diagnostics` 76, `types` 268, `programfile` 138, `testing` 32, `drivers` 10, `circuiteditor` 53, `packer` 40, `completion` 62
  (`windows`, `dialogs`, `docking` melden sich als Prüfungen der C#-VM ab; `linkage` 107 von 120 in 29 min, der Rest MinGW/Wine; `native`: die 505 Fälle
  "VM: die erwartete Ausgabe" alle grün, die nativen Builds mit ASan brauchen länger als 30 min je Prozess - unberührt von diesem Schritt). Unter Xvfb
  mit spark: ein Konsolenprogramm (Ausgabe, Eingabe über die Zeile des Output-Panels), ein Haltepunkt (Halt, Scope-Panel, weiter bis zum Ende), ein
  Fensterprogramm (sein Fenster aus `fire`), der Schaltplan-Editor (Live-Sitzung in `fire`, Simulation läuft), das Werkzeug `check` des MCP-Servers über forge.
* **Gefunden** und danach behoben (`native/vm/vm_lib.c`): `inline c` in der C-VM beendete das Programm mit `fire runtime error: the VM in C does not support
  the inline C function M_Add yet`; jetzt wirft der Aufruf wie in der C#-VM eine abfangbare `ArgumentException` `'M.Add' is an inline C function: it runs
  only in a native build (...)` (der Name, wie das Programm ihn ruft: `DisplayName` der Signatur), ein `extern` ohne Bibliothek ebenso mit dem Text der
  C#-VM. Den C-Rumpf in der VM auszuführen (zur Laufzeit übersetzen und laden) ist nicht vorgesehen: SPEC und Tests sagen "nur nativ".

**Entfernt** (23.11.2026, nach der Liste oben):

* **Die Projekte** `src/fire.Runtime` (die C#-VM mit `Packer`, `BundleWriter`, `PayloadLoader`, `PackagePlan`, `PackageNativeBinding`, `PackageHost`,
  `HostNatives`/`TimeNatives`, `ReflectionNatives`, `LineProfile`, dem Programm `runtime`), `src/fire.Terminal.Bridge`, `src/fire.Windows.Bridge`,
  `src/fire.Terminal.Windows`, `src/fire.Terminal` und ihre Einträge in `fire.sln`.
* **Die Bilder von spark**: `PixelCanvas`, `ImageDecoder`/`ImageEncoder` (mit `PngDecoder`, `BmpDecoder`, `GifDecoder`), `ImageData` und was sie brauchen
  (`Palette`, `PixelColor`) liegen jetzt in **`src/fire.Imaging`** (Namensraum `fire.Imaging`, ohne `ImageData.ToFramebuffer` - das war die Brücke zur Grafik der
  C#-VM). Ein eigenes Projekt statt spark selbst, weil das Testprogramm die Codecs prüft (`ImageTests`, "Bilder: PNG, BMP, GIF") und spark nicht referenziert.
  Die Lizenz von pcface (die Glyphen der eingebauten Schriften, `fire_gfx_font.h`) liegt jetzt neben der von libschrift in `packages/graphics/native`.
* **In `src/fire.Compiler`**: `RuntimeSession.cs`, `Debugging/VmDebugTarget.cs`, `HostRenderer.cs`, `GraphicsLibrary.InProcess.cs`, `PackageImports.InProcess.cs`,
  `Tables.Bridges.cs` und der Eintrag `bridges.def` in `Tables.cs` (**`native/tables/bridges.def` ist jetzt die Quelle**, von Hand gepflegt; `forge tables`
  schreibt noch `opcodes.def`, `errors.def`, `natives.def` und `prelude.fire`), der Record `HostWindows`, `GraphicsLibrary.Wanted` (`FIRE_VM_GRAPHICS`) und die
  Referenz auf `fire.Runtime` (`fire.Device.Manager` referenziert forge direkt).
* **In spark**: die Referenz auf `fire.Terminal` (jetzt `fire.Imaging`), `Help/Embedding.md` (das Einbetten der C#-VM) mit dem Menüpunkt *Embedding fire in
  your Application* und den Links darauf (First Steps, README, [docs/README.md](README.md)); das Einbetten der VM in C kommt mit L6 (`fire_embed.h`).
* **In `src/fire.Testing`** alles, was nur mit `FIRE_TESTS_DOTNET_VM=1` lief (`DotnetVm` ist weg): die Abschnitte `dotnetVm1` ... `dotnetVm22` und
  `dotnetVmUi` in `Program.cs`, im Packer die Teile 1-4 und die Single-File-Runtime (es bleiben die Importe, die VM in C, das Subsystem und die nativen
  Ziele), `UseIoPackage`, `FakeRenderer`, `TestHostDisplay`, `PixelOnlyFont`, `ArrayTarget`, `Shp`; `DebuggerTests` `VmRun`/`TargetRun`,
  `DebuggerPluginTests` `PluginRun` (der Plan `PluginPlan` bleibt: LinkageTests "plugindebug" in `fire` und nativ), `DebugSessionVm.cs`,
  `NativeDebugTests` `RunCsVm`/`RunVmLocked` und die Fälle "C-VM gegen C#-VM" (`NativeDebugTests.Vm.cs` behält `fire` mit den Protokoll-Prüfungen und stdio),
  `DialogTests.cs`, `DockingTests.cs`, `WindowManagerTests.cs` (die Gruppen `windows`, `dialogs`, `docking`), dazu die Prüfungen der C#-Grafik selbst
  (`fire.Terminal`: "Font-Rendering", "Grafik: Farbmodi, Zeichenfunktionen, Blit", der Slicer in C#, der C#-Port von libschrift und der `FontManager`,
  `ToFramebuffer`) - die Gruppen `graphics` und `callbacks` waren danach leer und sind weg. Die Prüfungen der Breiten eines Ziels (dos16, dos32, win16,
  bios16), die die C#-VM als Vergleich nahmen, nehmen `fire` (`CVmTest.TargetOutput`); "word: 16/32 Bit" vergleicht das native Programm mit der Ausgabe, die
  die C#-VM hatte.
* **`src/fire.Benchmarks`**: misst nur noch `fire` (übersetzt mit dem C#-forge, `fire run --time`: "ran in"); der Vergleich mit der C#-VM ist weg.

**Weggefallen ohne Gegenstück in der C-VM** (die Prüfungen waren die der C#-VM; was sie sonst prüften, läuft über den Korpus `tests/cases` in `fire`):
die UI-Bibliothek mit Test-Anzeige und den Test-Natives `__TestEvent`/`__TestClose`/`__TestMillis`/`__TestDragAnswer`/`__TestCanDrag` (Textfelder,
Zwischenablage, Drag & Drop, Touch, Joystick, Markup - es bleibt die Zeichnung "UI nativ == VM" und die Live-Sitzung), die Dialoge, das Docking und der
WindowManager (die Fenster von `fire.Terminal.Windows`), die Rückrufe des Hosts auf dem VM-Thread, die IO- und Netz-Regeln eines Hosts (`IoPolicy`,
`NetPolicy`: die C-VM erlaubt alles wie die gepackte C#-Runtime), `extern` über P/Invoke mit eigenen Natives, die Beispiele von `Embedding.md`, ein
Wortpuffer eines 16-Bit-Ziels an eine Bibliothek. **Offen** dabei: die C-VM rechnet `word` immer mit 64 Bit (`FIRE_WORD_BITS`; der Lader liest `wordWidth`,
nimmt es aber nicht) - die C#-VM nahm die Breite des Ziels; die Vergleiche dos16/bios16/win16 mit Open Watcom, DOSBox-X, QEMU und Wine (auf dieser Maschine
nicht vorhanden, ungeprüft) weichen deshalb in der Zeile eines `word` ab, sobald die Werkzeuge da sind.

**Geprüft** (23.11.2026, Linux x64, nach dem Entfernen): Builds von `fire.Compiler`, spark, `fire.Testing` und `fire.Benchmarks` (Release) ohne Fehler,
`cc -O2 native/forge/forge.c` übersetzt; einzeln grün: `tables` 8, `cvmrun` 26, `drivers` 10, `previews` 10, `trace` 27, `debugger` 43, `testing` 32,
`packer` 46, `programfile` 138, `types` 268, `diagnostics` 76, `circuiteditor` 63, `linkage` 124 (36 min); die Standardrunde ohne `native`
(`FIRE_TEST_NO_NATIVE=1`, 13 min): 2206 Prüfungen, rot nur der Fehler, der schon vorher da war (die Sprachdoku verlinkt `../spec/PLUGINS.md`). Fünf
Laufzeitfehler aus den Prüfungen der C#-VM sind jetzt Fälle in `tests/cases/smoke` (0590-0630). Unter Xvfb mit spark (über das Werkzeug `run` des
MCP-Servers): ein Konsolenprogramm (Ausgabe im Output-Panel) und ein Fensterprogramm (sein Fenster aus `fire`).
**Gefunden** (nicht behoben): der Trace einer Ausnahme, die beim Eintritt in eine Methode geworfen wird (die Signatur eines Lambda-Arguments passt
nicht), nennt für die Methode `(prelude)` statt ihrer Quelle und Zeile (`at Runner2.Execute (prelude)`, `smoke/0610` - der Fall prüft den Trace nicht).

Später, nicht Teil des Ports: ABI v2 für Paket-Natives (die Bibliothek benutzt die `Value` der VM direkt, keine Kopien, Objekte dürfen hinüber).

### Phase C - das Frontend in C

1. Lexer, Präprozessor, `#if`, Diagnosen. Prüfung: Token- und Diagnose-Ausgabe gleich.
   **Umgesetzt** ([FORGE_C.md](spec/FORGE_C.md)): `native/forge` (`lexer.c`, `preprocessor.c`, `conditional.c`, `diag.c`; je Datei der C#-Commit, dem
   sie entspricht), gebaut mit `cc -O2 native/forge/forge.c -o forge-c -lm` (gcc, clang, `-Wall -Wextra` ohne Warnung). Das Maß: `forge tokens <datei>`
   (neu im C#-forge, `FrontendDump.cs`: ein Token je Zeile mit Art, Text, Zeile, Spalte, Länge, Wert, dazu die Diagnosen wie forge sie druckt) und
   dasselbe von `forge-c tokens`; `tests/compare-frontend.sh` vergleicht über den Korpus: **1255 von 1256 Dateien byte-gleich** (die eine bindet eine
   `.fxml` ein - Schritt 5), dazu 2512 beschädigte Kopien (`FRONTEND_MUTANTS=2`, die Fehlerwege) alle gleich. Quelltext intern in UTF-16-Einheiten
   (Spalten), Texte sonst als UTF-8 je Einheit; Ausnahmen als `longjmp` auf dem Kontext, kein globaler Zustand (L6).
2. AST und Parser. Prüfung: AST-Ausgabe gleich (`SyntaxTests`).
   **Umgesetzt** ([FORGE_C.md](spec/FORGE_C.md)): `ast.h`/`ast.c` (dieselben Knoten und Felder wie `src/fire/Ast`, getaggte Structs aus der Arena),
   `parser.c` (`Parser.cs` Methode für Methode, mit `ParseMultiple`: eigene Einheiten, Namespaces, Erweiterungen, generische Namen, Enum-Hilfsklassen),
   `units.c` (was der Parser von `Unit.cs` braucht). Das Maß: `forge ast` (der Baum als Text mit allen Feldern, die Warnungen, die Diagnosen) - über den
   Korpus **1255 von 1256 gleich**, die 2512 beschädigten Kopien (75 verschiedene Fehlercodes) alle gleich. Jede Diagnose der Fälle (`.diagnostics`),
   die aus Lexer, Präprozessor oder Parser kommt, gibt `forge-c ast` wörtlich so aus (`syntax`: 25 von 29, der Rest ist Resolver - Schritt 3).
   `forge-c ast` ist rund 4-mal so schnell wie `forge ast`. **Offen:** die Markup-Generatoren hinter `#include` (Schritt 5), was erst der Linker setzt
   (`ProcessedSource.Qualify`/`Module`/`ImportNamespaces`, Schritt 4), die Namen zusammengesetzter Einheiten (`RenderTerms`, mit dem Resolver).
3. Resolver, Typprüfer, `SetterClaims`. Prüfung: Diagnosen gleich (`DiagnosticTests`, `TypeTests`).
   **Umgesetzt** ([FORGE_C.md](spec/FORGE_C.md)): `resolver.c` (`Resolver.cs` mit `StaticInits`, `AstNames`), `typechecker*.c` (`TypeChecker.cs` mit
   `TakeLocal`, alle TC0xx und E4052), `setterclaims.c`, `unitv.c` (`Unit.cs` als Wert, mit den Namen zusammengesetzter Einheiten), `linker.c`
   (`CompileAndLink` bis zur Typprüfung: Prelude, Direktiven mit Wirkung, `#import` mit den Preludes und Natives der installierten Pakete,
   `new Resource`), `dict.c` (Dictionary/HashSet mit der Reihenfolge von .NET). Das Maß: `forge diagnostics <datei>` (neu, `FrontendDump.cs`: was
   `forge run` beim Übersetzen auf stderr schreibt, bis zur Typprüfung) und `forge-c diagnostics`; `tests/compare-frontend.sh ... diag`: über den Korpus
   **1255 von 1256 gleich** (wieder die `.fxml`), die 2512 beschädigten Kopien alle gleich, der ganze Korpus strikt 1255 von 1256; gegen die
   festgehaltenen `.diagnostics` von `diag`/`types`/`syntax` alle Zeilen des Frontends gleich (38/275/62). Rund 3-mal so schnell wie C#.
   **Offen** (für Schritt 4, meldet nichts): was die Typprüfung dem Compiler hinterlässt (UnitChecks, LocalGuards, FixedCoercions, OverloadHints,
   MethodTypeArgs, die Herleitung von `defined`, ...), Projekte und Ziele im Linker.
4. Compiler, Linker, Importe und Preludes. Prüfung: `.fbc` byte-gleich über den Korpus.
   **Umgesetzt** ([FORGE_C.md](spec/FORGE_C.md)): `compiler.c`/`compiler2.c` (`Compiler.cs` Methode für Methode), `compilertrees.c`
   (`Compiler.Trees.cs`), `bytecode.c` (Chunk, FunctionProto, RuntimeClass, ClassMeta, DeclaredType/DeclaredKind, EnumType, die Konstanten),
   `programfile.c` (der Schreiber von `ProgramFile`, mit SHA-256 für die Tabelle der Quellen), im Typprüfer alles, was er dem Compiler hinterlässt
   (UnitChecks, LocalGuards, FixedCoercions, OverloadHints, MethodTypeArgs, ReceiverTypeArgs, Ausdrucksbäume, BitsOf, die Herleitung von
   `defined`), `linker.c` bis zum gelinkten Programm (Natives in der Reihenfolge der NativeRegistry mit `bridges.def` und den Paketen,
   `NativesOf`, `FloatNarrowing`, Ressourcen, eigene Einheiten, Quellen). Das Maß: `forge fbc <modus> <datei>` (neu, `FrontendDump.cs`: die Bytes
   von `forge bytecode -m <modus>`) und `forge-c fbc`; `tests/compare-frontend.sh ... fbc`: **1254 von 1256 byte-gleich** in Debug (mit der
   Tabelle der Quellen), Release und Performance (die zwei: die `.fxml` - Schritt 5 - und Modelle als Ressourcen, die der C#-Linker umwandelt -
   `ModelConverter`, Phase D), dazu 1256 beschädigte Kopien alle gleich. Ende zu Ende: `forge-c bytecode` + die VM in C über
   `tests/run-cases-vm.sh` **1126 von 1128** - wie mit dem C#-forge. **Offen** (für Phase D): Projekte, Ziele, `-D`, Plugins (`ClassMeta`,
   `OwnImports`), das Packen eines Programms, `.fpk`, `ModelConverter`.
5. Markup-Generatoren (`.fxml`, `.biscuit`, `.fdata`, Falstad). Prüfung: erzeugter fire-Text gleich.
   **Umgesetzt** ([FORGE_C.md](spec/FORGE_C.md#schritt-5)): `markupmodel.c`, `markupschema.c`, `markupparser.c`, `fireuigenerator.c` (mit den
   `UIRoot.*`-Attributen, Bindungen samt dem `undefined` eines gebrochenen Pfads, Slider/Stepper, typisierten Lambdas), `biscuitgenerator.c`
   (mit `MarkupFiles`), `datamodelgenerator.c`, `falstadimport.c` - je C#-Datei eine C-Datei, Stand 7a594769; dazu `xml.c` und `json.c`: was das
   Markup von System.Xml und System.Text.Json braucht, mit den Fehlermeldungen von .NET samt Zeile und Position. `#include "x.fxml"` (und
   `.biscuit`, `.fdata`) geht durch die Generatoren. Das Maß: `forge ui|biscuit|fdata|falstad` (gab es schon) gegen `forge-c` mit denselben
   Befehlen, `tests/compare-frontend.sh ... markup`: die 26 Markup-Dateien des Repositorys, 130 Stücke Markup, die `src/fire.Testing` inline
   schreibt (`tests/extract-markup.py`), 468 beschädigte Kopien (`FRONTEND_MUTANTS=3`) - **alle byte-gleich**; `FxmlBinding` ist jetzt in
   `tokens`/`ast`/`diagnostics`/`fbc` gleich (der Korpus: tokens/ast/diagnostics 1256 von 1256, `fbc` 1255 von 1256 - offen nur die Modelle als
   Ressourcen, Phase D). Nicht portiert (Editor): `DataModelDocument`, `MarkupText`, `MarkupCompletion` (mit Schritt 6), `CircuitSamples`.

6. `forge serve` (L4) auf dem C-Frontend; spark stellt `Language/` (4 300 Zeilen) darauf um, die Completion-Tests wandern auf Protokollebene.
   **Umgesetzt** ([FORGE_SERVE.md](spec/FORGE_SERVE.md), [FORGE_C.md](spec/FORGE_C.md#forge-serve)): `forge-c serve`, JSON-Zeilen über stdio -
   Dokumente (ganzer Text oder Edits, Versionen, Overlay), Projekte (Dateien, Bibliotheken mit Namespaces), Diagnosen (entprellt, latest wins,
   `stale`, gemerkt), Vervollständigung, Hover, Signatur, Definition, Referenzen, Gliederung, Faltung, Markup-Vervollständigung, Abbruch. Die
   Logik von `ScriptSymbolIndex`, `CompletionEngine`, `NavigationEngine`, `MarkupCompletion` und `LiveDiagnostics` 1:1 in C
   (`native/forge/serve*.c`); spark (`ServeClient`, `ServeDocuments`) geht für Vervollständigung, Diagnosen, Markup-Vervollständigung, Hover, Signatur und Definition über
   das Protokoll, `FIRE_SPARK_LANG=inproc` behält den eigenen Prozess bis M2. Die Completion-Tests laufen über das Protokoll (jede Liste gleich der
   des eigenen Prozesses), dazu ServeTests (Planung der Diagnosen, Projekte; die Diagnosen gleich `LiveDiagnostics`, auch an ruhrpott-raser).
   Ein Tastendruck 3-8 ms, eine Analyse rund 3-mal so schnell wie im eigenen Prozess. **Offen**: Vergleichsprüfungen für Hover/Signatur/Definition, die Prüfung ganzer Projekte (`AnalyzeProjectFiles`), Natives von Bibliotheksprojekten, übersetzte Einheiten über Analysen merken.

**M2:** `forge run`, `forge test`, `forge ui|biscuit|fdata|falstad` für die VM laufen ganz ohne .NET; spark enthält keinen Compiler mehr.

### Phase D - Backend und Werkzeuge in C

1. Generator (`CppGenerator*`, 15 600 Zeilen - der größte Einzelposten). Prüfung: `fire_program.c`/`fire_natives.cpp` byte-gleich über den
   Korpus und die Optionen (Ziele, `linkage`, `stripping`, `#intwidth`, Debug, `optimize`).
   **Umgesetzt** ([FORGE_C.md](spec/FORGE_C.md#phase-d-schritt-1-der-generator); der Generator eines Plugins seit 22.11.2026): `native/forge/gen/` (Stand 808e9788) -
   `CppGenerator.cs` ganz (Run, Kopf, Dispatcher, Feldhelfer, Klassendeskriptoren, der Fluss, `StepCore` mit allen Opcodes), `Packed`,
   `CAbi`, `CFields`, `Overloads` (Selektoren, Hinweise), die Dispatcher der Interfaces, die Natives der Pakete (Manifest, Plattformquellen,
   Header, C und C++, typisierte Natives, Sperren), die Brücken von graphics/windows, JSON, `Library.Get`, die Zeigerkonvention der
   Open-Watcom-Ziele, die Reflection (`__refl_*`, Tabellen, Selektoren der Lambdas, Probes), Debug-Builds, die Teilung in Einheiten (`ProgramSplit`) und
   `--stats`. Maß: `forge cgen <optionen> <datei>` gegen `forge-c cgen`, `tests/compare-frontend.sh ... native` mit `NATIVE_STRICT=1`: Standardoptionen
   **1129 von 1129** Fällen, **93 von 93** Paketen, **33 von 34** Beispielen byte-gleich (die eine Abweichung, `Scene3D Models`, sind die Modelle als
   Ressourcen - der Linker, Schritt 4), ebenso mit `debug` und mit `split`; die Reflection auch mit `strip0`-`strip2`. Dazu (20.11.2026) die **Module**
   ([FORGE_C.md](spec/FORGE_C.md#die-module-linkage-staticshared-fire_modules1)): `CppGenerator.Modules.cs`, `ModuleDispatch.cs`, `StaticLinkage.cs` und die Seite des
   Programms von `Plugins.cs` (`gen/modules.c`, `moddispatch.c`, `staticlinkage.c`, `plugins.c`: ein Generator je Bibliotheksmodul mit seiner Sicht des Programms, die
   Runden über die gemeinsamen Schalter, die Exporte `fx_`, die Tabellen der Laufzeit, die Schnittstellen im Cache, die offene Welt von linkage shared), `FIRE_MODULES=1`;
   gemessen mit `cgen modules|static|shared` (FrontendDump) - `modules`/`static`/`shared` 179 von 179 Quellen der Stichprobe, alle 93 Pakete, die Optionssätze `debug`, `strip0`, `i32`, `t:esp32`, `performance` gleich. Dazu (22.11.2026)
   der Generator eines Plugins (`gen/plugins.c`: `GeneratePlugin`, `PluginDeclarations`, `RegisterPluginTargets`, `PluginTrailer`, `PluginUnits`, `PluginJson`,
   `PluginNativesCpp`), gemessen mit `cgen plugin|pluginhost`: 375 von 375 Quellen mit Klassen (jeder 3. Fall, alle Pakete und Beispiele) gleich.
2. NativeBuilder, Toolchains (gcc, clang, MSVC, tcc, Emscripten, ESP-IDF, Open Watcom), Linkage, `PackageLibrary`, Stats, Boot-Images, PE-Ressourcen.
   **Teilweise umgesetzt** ([FORGE_C.md](spec/FORGE_C.md#phase-d-schritt-2-der-builder)): `native/forge/build/` (Stand 6f21d1a4) - `forge-c build` mit linkage
   `source`: `ToolchainDef`, `ToolchainDetector`, `MachineToolchains`, `NativeConfig` (fire.native.json, eigene Ziele), die Befehlszeilen aller Arten (gcc,
   clang, tcc, Emscripten, MSVC, `custom`, `files`/ESP-IDF, Open Watcom ohne Disk-Image), der C++-Ersatz für TinyCC, die geteilten Einheiten nebeneinander,
   der Schnellbuild `--quick[=0|1]`, der Programm-Cache (derselbe Schlüssel wie in C#) und `OptimizingToolchain`; dazu im Generator die Teilung in Einheiten
   (`ProgramSplit`). Maß: `forge buildplan` gegen `forge-c buildplan` (Befehle, Dateien, Cache-Schlüssel) über den Korpus und echte Builds einer Stichprobe mit
   gcc und tcc (`tests/compare-frontend.sh ... buildplan|build`). Dazu (Stand de45e7e4) `PackageLibrary` (`forge-c pkglib`: die Bibliotheken der Pakete für
   die VM mit ihren Wrappern in C und C++ und `libfire_gfx`, `Locate`/`Ensure` mit denselben Schlüsseln - 27 von 27 Plänen gleich, die 25 gebauten
   Bibliotheken byte-gleich mit denen des C#-forge, beide VMs laden sie), das Boot-Image von bios16 (Fat12, nach dem Link; gemessen mit `bootimage`), die
   PE-Ressourcen (Version, Icon, Subsystem: der Inhalt wie in C#; geschrieben werden sie wie dort nur unter Windows) mit `#noconsole` (`-mwindows`),
   `ToolchainProvider`/`TestToolchain`, `"globalJobs"` und Kommentare in fire.native.json. Dazu (20.11.2026) linkage `static` und `shared` (`build/linkage.c`:
   `BuildStatic`, der Cache der Bibliotheken, `LinkDlls`, `WriteStaticFiles`; gemessen mit `buildplan static|shared` und echten Builds - `buildplan` 40 von 40 Quellen je Satz, echte Builds mit gcc, tcc und MinGW: Protokoll und Programm gleich; zwei Fehler des C#-Generators mit Modulen und einer mit TinyCC gefunden, siehe FORGE_C.md).
   Dazu (22.11.2026) linkage `plugin` ([FORGE_C.md](spec/FORGE_C.md#linkage-plugin-der-generator-eines-plugins-und-buildplugin): `build/plugin.c` - `BuildPlugin`
   mit `--host`, die DLL für Windows, `PluginVmNatives`, `PluginBytecode.Write`; `buildplan plugin` 125 von 125 gleich, der `.fireplugin` byte-gleich; LinkageTests
   `FIRE_TESTS_FILTER=plugin` mit `FIRE_TESTS_FORGE=<forge-c>` 29 von 29, auch unter Wine). Seither
   portiert: `--stats` (NativeStats: die Map, der Bericht als Text und JSON). Dazu (23.11.2026) **Windows als Host**
   ([FORGE_C.md](spec/FORGE_C.md#windows-als-host-phase-d-schritte-2-und-3): `native/forge/win32.h` - Ordner, Dateien, Threads, `CreateProcess` für die Werkzeuge
   des Builds, die Pfade mit Laufwerk, `FindProgram` mit `.exe` und den bekannten Orten; die PE-Ressourcen mit `UpdateResource` wie in C#): forge-c baut mit MinGW
   (`x86_64-w64-mingw32-gcc -O2 native/forge/forge.c -o forge-c.exe -static`, nichts nur von GNU - MSVC nicht geprüft) und läuft unter Wine: `bytecode` byte-gleich
   mit Linux, `run` in `fire.exe`, `build` mit der VM, `test`, ein nativer Build mit `files` (mit MinGW übersetzt, läuft). Offen: ein nativer Build mit einem
   Compiler unter Windows (unter Wine fehlt er: kein w64devkit ohne Netz), Pfade außerhalb der Codepage, das Beenden der Werkzeuge beim Abbrechen unter Windows.
3. Projekt, Solution, Bauplan, Vorlagen, Beispiele; von der Paketverwaltung, was forge braucht (Manifest, Store, `.fpk`). ember bleibt bis Phase F das C#-Programm.
   **Umgesetzt** ([FORGE_C.md](spec/FORGE_C.md#phase-d-schritt-3-projekte-solutions-pakete); Windows als Host seit 23.11.2026, wie in Schritt 2): `native/forge/project/` (Stand de45e7e4) - das Laden von
   `.fireproj`/`.firesln`, die Dateien, Ordner (mit `.gitignore`) und Build-Aktionen eines Projekts, Bauordnung und Startprojekt, der `BuildPlan` mit Bibliotheken und
   Natives, der Linker mit Plan, `forge-c build` eines Projekts oder einer Solution (eine Bibliothek wird gepackt, ein Programm nativ gebaut, die Copy-Dateien daneben;
   `--check`, `--all`), `forge-c publish`, das Paketformat `.fpk` (Manifest, Zip schreiben und lesen). Maß: `forge projectplan`/`forge fpk` gegen `forge-c`
   (`tests/compare-frontend.sh ... project|projectbuild`): das Modell **109 von 109** Projekt- und Solutiondateien gleich (`src/fire.Project`, `packages`,
   ruhrpott-raser, Grenzfälle), **70 von 70** Bibliotheken gleich gepackt (Ausgabe, Manifest, Einträge, Zip-Struktur; jeder forge liest das Paket des anderen -
   verschieden nur die Zeiten und die komprimierten Bytes: zlib-ng in .NET, der Deflater von `packages/compression` in forge-c), die Programme der Projekte wie bei
   einzelnen Dateien (`buildplan` 26 von 36, die übrigen: Reflection und die Modelle als Ressourcen aus Schritt 1/4, eine Abweichung des Generators an Raser.Viewer/
   Raser.Game bei gleichem gelinkten Programm), `build --all` und `publish` an Kopien gleich bis auf die Programme mit Reflection. Dazu (20.11.2026) die Engine `vm`
   (`build/cvm.c`: `fire` gefunden oder gebaut wie in C# - dieselbe VM, derselbe Schlüssel -, das Programm und die Bibliotheken der Pakete dahinter; 188 von 188 Fällen
   und 43 von 43 Projekten gleich, die Programme byte-gleich bis auf `Scene3D Models`), die C++-Natives eines Projekts (im nativen Programm und als Bibliothek für die VM),
   `settings.globalJobs`, Paketreferenzen `github:...` (über `ember-lock.json`), das Installieren der Standardpakete in den Store (`StandardSources.EnsureInstalled`:
   derselbe Store, dieselben Stempel). Vorlagen und Beispiele nutzt forge nicht (nur spark). Offen: `FIRE_VM_ENGINE=dotnet`; `ember install` bleibt C#.
4. Rest der Befehlszeile: Testläufer, `forge data`, Modellwandler (`.fmesh`, `.fblocks`, glTF), Packer.
   **Umgesetzt** ([FORGE_C.md](spec/FORGE_C.md#phase-d-schritt-4-der-rest-der-befehlszeile)): der **Modellwandler** in `native/forge/model/` (Stand 6c1b0f0e:
   `ModelConverter`, `BlockModel`, `MeshModel` - glTF/`.glb`, `.fblocks`, `.fmesh` -, eine Modellressource wird beim Einfügen zur Modelldatei, `forge-c model`):
   `tests/compare-frontend.sh ... model` **576 von 576** Dateien des Korpus (`tests/model-corpus.py`: die Modelle des Repositorys, zufällige Modelle aller Formate,
   ihre Fehlerpfade, beschädigte Kopien) gleich - Ausgabe, Fehler, `.f3d` Byte für Byte, auch als Ressource eines Programms; damit ist `Scene3D Models` in `cgen`,
   `fbc`, `buildplan` und `projectbuild` gleich (die eine bekannte Abweichung der Schritte 1 bis 3). **`forge test`** in `native/forge/tools/` (Stand b833e71b: der
   Treiber, der Filter, der Teil des Linkers, das Protokoll, die Zusammenfassung; in der VM in C oder `--engine native`; mehrere Dateien, `-D`, `-f`, `-i`, die
   Bibliotheken der eigenen Natives eines Projekts für die VM): **54 von 54** Paketprojekten in der VM gleich, **52 von 54** nativ (`tls`: das C#-forge findet mit tcc
   einen Header nicht - gemeldet; `windows`: beide scheitern an tcc), die Quellen von `TestingTests` und weitere Fälle in beiden Engines gleich. Dazu (20.11.2026,
   `tools/commands.c`, `tools/report.c`, `tools/dump.c`, Stand b833e71b) **`build|publish|bytecode --report json`** - der Bericht, mit dem spark baut (Phase A):
   Fortschritt, Protokoll, Diagnosen, Ausgaben, Projekte, Ende, Abbrechen -, `run` (in der VM in C), `native`, `data` (der Treiber des Schemas in der VM),
   `dump`, `stats`, `bytecode` von Projekten, der Link mehrerer Quellen (`build`/`bytecode`/`run`/`test` mit mehreren Dateien, `-D`, `-i`, `--overlay`,
   `--debug-info`, `--global-jobs`), `CommandLineParser.Parse` mit seinen Meldungen und `-o` von `ui|biscuit|fdata|falstad`: `tests/compare-commands.sh` 95 von 95
   Befehlen gleich (Ausgabe, Fehler, Exitcode, geschriebene Dateien), `dump` 1222 von 1222 Quellen des Korpus. `install-standard` und `build-vm` sind aus Schritt 3.
   Dazu (23.11.2026) der Rest: `tables` (aus den Tabellen, die forge-c trägt - über `native/tables` "up to date"), `prebuild-standard` (die 25 Bibliotheken byte-gleich
   mit denen des C#-forge, `.hash` und `.runtime` gleich, auch frisch gebaut), `dump` einer Programmdatei (`programread.c`: `ProgramFile.Read`; 72 von 72 Programmen
   aus `tests/cases` und `packages` gleich, die Fehler beschädigter Dateien gleich), die Ereignisse `background` des Berichts und `FIRE_PROGRESS_LINES`
   (`BackgroundWork`), `publish` mit `--overlay`/`-D`/`-i` (wie in C#: die Overlays gehen in die Pläne, `-D` und `-i` nutzt publish nicht); `tests/compare-commands.sh`
   mit diesen Befehlen. `--host` nimmt publish auch in C# nicht. Der Packer: `fire.Runtime.Packer` packt nur
   noch die .NET-Einzeldatei (`FIRE_VM_ENGINE=dotnet`) und entfällt in C; das Packen mit der VM in C ist die Engine `vm` (Schritt 3).
5. `ToolchainSetup` (lädt unter Windows einen Compiler): braucht HTTPS und Entpacken im Werkzeug - über das System (WinHTTP) statt eigener TLS.
   **Umgesetzt** ([FORGE_C.md](spec/FORGE_C.md#phase-d-schritt-5-toolchains-herunterladen), 23.11.2026): `native/forge/tools/setup.c` - `ToolchainProvider.Require`
   fragt wie `ConsoleToolchainPrompt` und lädt w64devkit (Windows x64) oder Open Watcom (Windows, Linux x64) mit denselben Quellen in dieselben Ordner
   (`Toolchain/` neben forge-c, `toolchain.json`); TinyCC mit seinem SHA-256 über `forge-c toolchain-setup tcc` (was das Setup von spark tut). Der Download über das
   System: WinHTTP unter Windows (zur Laufzeit geladen), unter POSIX `http://` über einen Socket und `https://` mit `curl`/`wget`; die Zips entpackt forge-c selbst.
   Gemessen mit einem lokalen Server als Proxy (hier kein Netz): Open Watcom unter Linux und unter Wine, die Frage eines nativen Builds für `dos32` bis zum Build mit dem
   geladenen Compiler, w64devkit (ein falsches 7z) und die Prüfung des SHA-256 von TinyCC mit `--archive`. Offen: ein echter Download (https) - nicht geprüft.
6. `forge conformance` ersetzt den Kern von `fire.Testing`; Installer (`setup.iss`) und Paketierung ohne .NET-Werkzeuge.
   **Umgesetzt** ([FORGE_C.md](spec/FORGE_C.md#phase-d-schritt-6-conformance-und-die-distribution-ohne-net), 23.11.2026; den Installer selbst baut hier
   niemand - kein Windows, kein Inno Setup): `forge-c conformance` (`native/forge/tools/conformance.c`) führt die Fälle von `tests/cases` ohne .NET aus - jeder Fall
   in eigenen Prozessen (`forge-c bytecode`, dann `fire`; mit `--engine native` die Gruppe native als native Programme), nach den Regeln von `run-cases.sh`,
   `run-cases-vm.sh`, `TestCasesVm` und `NatVerdict`; Filter nach Gruppe, Fall und Titel, `-j` (Vorgabe: die Hälfte der Kerne, höchstens 4), ein falscher Fall noch
   einmal allein, Zusammenfassung je Gruppe, Exitcode. Maß: **1173 von 1173** Fällen in der VM, dieselben Urteile wie `tests/run-cases-vm.sh` mit forge-c (auch an
   drei absichtlich verdorbenen Fällen); nativ (tcc) 515 von 517 - `native/1370` ist tcc (mit gcc richtig), `native/4440` erwartet die Meldung der VM, wo ein natives
   Programm keine fire-Plugins laden kann (seither `vm-rf/0190`). `forge-c package-dist <ordner>` (`tools/dist.c`, `native/forge/package-dist.sh`): forge-c als `forge`, die
   VM mit ihren fünf Varianten, `native/`, `Source/` und `Prebuilt/` - nur mit einem C-Compiler und forge-c; `setup.iss` nimmt den Ordner mit `/DForgeDist=<ordner>`
   (spark und ember dann mit `-p:PublishForge=false`, ohne das C#-forge). Was in `fire.Testing` C# bleibt und was Daten werden kann: die Tabelle in FORGE_C.md.
   **Offen:** der Installer mit `/DForgeDist` unter Windows gebaut und installiert; eine Distribution für Windows (unter Windows mit w64devkit zu bauen - hier nur
   der Aufbau als Probelauf); der native Teil des Testprogramms, den `conformance` nicht hat (Sanitizer, Warnungen als Fehler, die Sonderläufe); ein Timeout der
   Prozesse unter Windows; `fire.Testing` liest die Fälle weiter selbst (`TestCasesVm` kann gehen, sobald M3 die C#-VM-Seite nicht mehr braucht).
7. `fire_embed.h` und die Packages `forge`/`script` (L6), mit Tests in fire (ein Programm übersetzt und startet ein Skript, VM und nativ).
   **Umgesetzt** ([EMBED.md](spec/EMBED.md), 23.11.2026): `native/abi/fire_embed.h` - Übersetzen (Datei, Projekt oder Text) mit den Diagnosen als Daten,
   die Befehlszeile von forge im Prozess (`fire_forge_main`), ein Programm in der VM im Prozess mit Argumenten, Ausgabe an einen Rückruf oder gesammelt,
   Exitcode und der Ausnahme, die niemand fing (Klasse, Meldung, Trace), als Daten. libforge ist `forge.c` mit `FIRE_FORGE_LIBRARY` (`embed.c`), libfirevm
   `fire.c` mit `FIRE_VM_LIBRARY` (`vm_embed.c`); `forge-c` und `fire` sind dünne Programme darüber, `forge-c build-embed <ordner>` baut beide als Shared
   Libraries (eigene Runtime, verborgene Symbole), für ein Programm, das `forge` oder `script` importiert, von selbst (`FIRE_EMBED_PATH`). Die Packages
   `forge` (`Forge.Compiler`) und `script` (`Scripting.Script`: im eigenen Prozess, nicht als Kind `fire` - Begründung in EMBED.md) sind Standard-Packages,
   ihre Natives laden die Bibliotheken; Tests in fire (`packages/forge/testing`, `packages/script/testing`: übersetzen und starten, zwei Skripte
   nebeneinander, ein Übersetzungsfehler als Daten, eine Ausnahme des Skripts vom Wirt gefangen) in der VM und nativ richtig. Globaler Zustand: das Frontend
   hing schon am Kontext; was der Builder und die Befehlszeile für den Prozess halten, ist in EMBED.md aufgelistet (die Ordner setzt die Bibliothek je
   Übersetzung unter einer Sperre); die Runtime der VM bleibt die des Prozesses (L1) - ein Skript zur Zeit. Offen (für Schritt 8): die Quellen von forge
   als Natives eines Packages (der Mechanismus legt Header flach nach Dateinamen ab), `fire_forge_main` mit eigenen Strömen; Abbruch und Zeitlimit eines Skripts.
8. **forge mit einem Frontend in fire** (Wunsch vom 19.11.2026): ein kleines fire-Programm (`tools/forge.fire`: `#import "forge"`, die
   Befehlszeile unverändert an `Forge.Main(args)` durchgereicht, Ausgabe und Exitcode zurück), das mit forge selbst für jedes Ziel gebaut
   wird - fire baut sich damit selbst für andere Plattformen (`forge build tools/forge.fire --engine native -t <ziel>`; libforge und die VM sind C
   und gehen als Natives des Packages mit). Voraussetzungen: Schritt 7 (Package `forge`, ohne globalen Zustand, ohne `exit` - L6), die
   Toolchains im Ziel (Prozesse starten, Dateien), die Standard-Packages als Quellen im Ziel. Prüfung: das so gebaute forge übersetzt den Korpus
   byte-gleich wie das C-forge und baut sich selbst noch einmal (Fixpunkt).
   **Umgesetzt** ([EMBED.md](spec/EMBED.md#phase-d-schritt-8-libforge-als-eine-datei-im-package-forge), 23.11.2026): `tools/forge.fire` (`#import "forge"`,
   `terminate(Forge.Compiler.Main(Environment.Args))`). libforge geht als **eine C-Datei** mit dem Package (`packages/forge/native/libforge.c`, geschrieben von
   `forge-c amalgamate`, `--check` prüft, ob sie aktuell ist - Weg B; Begründung in EMBED.md: forge ist schon eine Übersetzungseinheit, die Regeln der Header
   und das Packen der Pakete bleiben, nichts in C#) und wird über die neue Liste `native.units` für sich übersetzt und dazugelinkt - nativ für jedes Ziel, in die
   Bibliothek des Pakets für die VM; die Brücke ruft `fire_embed.h` dann direkt (`FIRE_PKG_UNITS_FORGE`), ohne den Define (C#-forge) lädt sie libforge wie
   bisher. `fire_forge_main` schreibt auf eigene Ströme (`fire_forge_set_output`; das Package: `Forge.Compiler.Run` mit gesammelter Ausgabe), `package-dist`
   baut libforge/libfirevm in den Ordner, ein gepacktes Programm der VM mit `script` trägt libfirevm im Payload. Prüfung (Linux x64): `tools/forge.fire` nativ
   mit gcc und tcc und als Programm der VM - `bytecode` über jeden 10. Fall des Korpus, alle Projekte und Tests der Pakete (191) byte-gleich mit forge-c,
   `tests/compare-commands.sh <forge-c> <forge aus fire>`, Fixpunkt (das gebaute forge baut sich wieder, mit `SOURCE_DATE_EPOCH` byte-gleich), für Windows mit
   MinGW gebaut und unter Wine geprüft. Offen: libfirevm (Paket `script`) für andere Ziele - ihre Runtime passt nicht neben die des Programms, sie bleibt eine
   Shared Library der Maschine; `units` mit linkage static/shared/plugin; `libforge.c` muss nach jeder Änderung an forge neu geschrieben werden.

**M3:** forge und die VM sind C-Programme und C-Bibliotheken; wer nur sie braucht, braucht kein .NET. Der Bau von fire selbst braucht nur einen
C-Compiler. `fire`, `fire.Compiler`, `fire.Native` und der Kern von `fire.Testing` verlassen die Solution; .NET steckt nur noch in spark und ember.

#### M3 Schritt 1: Bestandsaufnahme (23.11.2026, vor dem Entfernen)

Was spark (`fire.Editor`), `fire.Testing`, `fire.Benchmarks` und die übrigen Projekte von `fire.Compiler`, `src/fire`, `fire.Native`,
`fire.Package.Manager` und `fire.Ember` benutzen (gesucht nach den Typen der fünf Projekte, dann Stelle für Stelle gelesen). `fire.Project`,
`fire.UI.Markup`, `fire.Git`, `fire.Imaging`, `fire.Device.Manager` hängen an keinem der fünf außer `fire.Project` an `fire.Package.Manager` (Manifest,
Natives der Pakete, Store, Versionen); `fire.Compiler` zieht `fire.Device.Manager` für `PackageDriverAgent` hinein.

**(a) forge-c kann es schon** (Befehl oder Protokoll) - spark ruft künftig nur den Prozess:

| spark (Stelle) | heute in C# | künftig |
| --- | --- | --- |
| Build, Build Solution, Build virtual, Publish, Pack Library, der Build eines nativen Laufs (`ForgeProcess`) | Prozess `forge` (C#) mit `--report json` | derselbe Prozess, `forge` ist forge-c |
| Run/Debug in der VM (`MainWindow.CVm`) | `forge bytecode --report json`, `CVm.Locate` als Rückfall | ebenso; der Rückfall ist `fire` neben spark |
| Sprachdienste: Vervollständigung, Diagnosen, Hover, Signatur, Definition, Markup-Vervollständigung (`ServeClient`) | `forge-c serve`, mit `FIRE_SPARK_LANG=inproc` im eigenen Prozess (`LiveDiagnostics`, `ScriptSymbolIndex`, `CompletionEngine`, `NavigationEngine`) | nur `forge serve`; der Weg im eigenen Prozess entfällt |
| Hintergrundprüfungen (`BackgroundCheck`, `AnalyzeDocumentJob`) | `diagnose` über serve, sonst `LiveDiagnostics` | nur `diagnose` |
| Entwurfsansicht des Markups, Vorschau der Blockmodelle, Schaltplan-Editor, Gerätetreiber (`UiPreview`, `BlockPreview`, `LiveSession`, `PackageDriverAgent`) | Linker in spark, dann `fire` als Kind | `forge bytecode --report json` (Programm, Bibliotheken der Pakete, VM), dann `fire` |
| Werkzeuge des Datenmodells (`DataModelTools`: DDL, Import, Datenbank anlegen, Migration) | `DataCommand` in spark (Linker + VM) | `forge run` eines erzeugten Skripts, `forge data import|diff` |
| Standardpakete installieren (Start, Einrichtung) | `StandardSources.EnsureInstalled` | `forge install-standard` |
| TinyCC/Open Watcom laden (Einrichtung) | `TinyccSetup`, `WatcomSetup` | `forge toolchain-setup tcc|watcom` (D5) |
| eine Toolchain prüfen (Dialog der nativen Builds) | `NativeBuilder.TestToolchain` | `forge toolchaintest <name>` |
| ein `.fpk` lesen (Projekte, Package Manager) | `Fpk.ReadManifest` | blieb C# (c): ein Zip und ein JSON, `fire.Project/PackageModel/Fpk.cs` |

**(b) forge-c (oder ember in fire) braucht einen neuen Befehl oder Teil des Protokolls:**

| spark (Stelle) | heute in C# | neu |
| --- | --- | --- |
| Test-Explorer: Tests finden (`TestDiscovery`, nur Parsen) | Präprozessor und Parser in spark | `forge serve`: Anfrage `tests` (Text einer Datei -> Klassen, Methoden, Titel, Zeilen) |
| Test-Explorer: Tests laufen lassen (`TestRunner.RunInVm`, live) | Linker + Treiber in spark, `fire` als Kind | `forge test ... --report json`: ein JSON-Objekt je Ereignis (Lauf, Test begonnen, Test fertig mit Status/Meldung/Ausgabe, Problem, Diagnose, Ende), live; `--overlay` für ungesicherte Texte |
| Package Manager: suchen, Liste der installierten | `PackageManagerService` (C#-ember als Bibliothek) | `ember find --json`, `ember list --json` (ember in fire); Installieren/Entfernen über `ember install|remove` |

**(c) reine C#-Helfer ohne Compiler** - bleiben als kleine Bibliothek `src/fire.Tooling` (ohne Parser, Typprüfer, Generator, Builder):

* der Weg zu forge und `fire` (neben spark, `FIRE_FORGE`, sonst forge-c aus `native/forge` gebaut), `ChildProcesses`;
* die Daten des Berichts (`ForgeReport`, `Diagnostic`, `BuildProgress`/`BuildPhase`, `BackgroundWork`), `OutputLocations` (klickbare Zeilen), das Lesen des Testberichts (`TestResult`, `TestFilter`);
* die Debugger-Clients (`NativeDebugTarget`, `NativeDebugTransport`, `NativeLaunch`, `CVmLaunch`, `Esp32Launch`, `DebugJson`, `DebugTarget`) - sie sprechen das Protokoll mit einem Prozess;
* die Kindprozesse der Ansichten (`HostDisplayFile`, `LiveSession`, `CVmScript`, `UiPreview`, `BlockPreview`, `PackageDriverAgent`) - jetzt ohne Linker;
* die Modelle der Editoren: `BlockModel`, `MeshModel` (mit ihrem `.f3d` für die Vorschau), `AssemblyInfo` (Dialog der Build-Einstellungen; die Direktiven aus dem Text gelesen statt mit dem Präprozessor), `NativeStats` (Ansicht von `.stats.json`);
* die Daten der Toolchains und Ziele für die Dialoge: `ToolchainDef`/`ToolchainDetector`, `MachineToolchains`, `NativeConfig` (fire.native.json), `TargetProfile`, `VmExecutionMode`, `OptimizeFor`, `NativeRuntimeFiles.AllPackages`;
* für die Syntaxfarben: `Lexer` mit `Token` und `ConditionalSymbols` (ein Tastendruck darf nicht auf einen Prozess warten);
* von `fire.Package.Manager`, was `fire.Project` und spark lesen: `Manifest`, `NativeSources`, `PackageStore` (der Store als Ordner), `PackageVersion` - nach `fire.Project`.

**Was entfällt:** `fire.Compiler` (bis auf das Obige), `fire.Native` (bis auf die Daten oben), `src/fire` (bis auf `Token`, `TargetProfile`, `VmExecutionMode`,
`OptimizeFor`, `ExceptionTrace.Relative` und die Typen von `PeResourceEditor`, die der Dialog zeigt), `fire.Package.Manager` (bis auf das Obige),
`fire.Ember`; in spark `LiveDiagnostics`, `ScriptSymbolIndex*`, `CompletionEngine`, `NavigationEngine`, `BuiltinMembers` (die Logik lebt in
`serve_*.c`). In `fire.Testing` alles, was das C#-forge prüft oder `forge-c conformance` abdeckt (Tabelle in [FORGE_C.md](spec/FORGE_C.md#phase-d-schritt-6-conformance-und-die-distribution-ohne-net)).
`fire.Benchmarks` übersetzt mit forge-c statt mit dem Linker.

#### M3 Schritte 2 bis 5: umgesetzt (23.11.2026)

**forge-c und ember (b):** `forge serve` beantwortet `tests` (die `[Test]`-Methoden einer Datei, nur geparst; [FORGE_SERVE.md](spec/FORGE_SERVE.md)),
`forge test --report json [--overlay ...]` meldet einen Lauf live (`run`, `start`, `result`, `problem`, `error`, `done`, `end`; `tools/testrunner.c`), ember
in fire kann `find --json` und `list --json` und nimmt den Store aus `FIRE_PACKAGES`. Beim Umstellen gefunden und behoben: forge-c mit `--report json`
endete nie, solange seine Eingabe offen blieb (der Leser von `cancel` hielt in `fgets` die Sperre von stdin, `exit()` wartete darauf - spark hält die Eingabe
offen); `package-dist` legte `native/forge/` und den Deflater von `packages/compression` nicht in den Ordner, ohne die forge einer Distribution die Pakete
`forge` und `script` (libforge) nicht installieren konnte.

**Was spark jetzt benutzt:**

| Funktion | Weg |
| --- | --- |
| Bauen, Build Solution, Publish, Pack Library, nativer Lauf | `forge build|publish --report json` (wie zuvor, jetzt forge-c) |
| Run/Debug in der VM | `forge bytecode --report json`, dann `fire` (Debugger: das Protokoll, `NativeDebugTarget`) |
| Vervollständigung, Diagnosen, Hover, Signatur, Definition, Markup, Prelude-Ansicht | nur `forge serve` (der eigene Prozess ist entfernt) |
| Prüfung eines ganzen Projekts (Fehlerliste) | `diagnose` von `forge serve` für jede Datei im Kontext des Plans (die Fehler des Resolvers in einem Projekt kennen im Bericht von `forge bytecode` ihre Datei nicht) |
| eine Bibliothek prüfen, ein Programm übersetzen ohne Lauf | `forge build --check` bzw. `forge bytecode` (`Forge.CheckProject`) |
| Test-Explorer | `tests` von `forge serve`, `forge test --report json` |
| Entwurfsansicht, Blockvorschau, Schaltplan-Editor, Gerätetreiber | `forge bytecode --report json`, dann `fire` (der Treiber-Agent ohne Bytecode-Plugins: `#import`) |
| Datenmodell (DDL, Datenbank anlegen, Import, Migration) | `forge run` eines Skripts, `forge data import|diff` |
| Standardpakete, Toolchains laden, Toolchain prüfen | `forge install-standard`, `forge toolchain-setup`, `forge toolchaintest` (mit einer fire.native.json in einem eigenen Ordner) |
| Package Manager, eine Bibliothek installieren | `ember find|list --json`, `ember install|remove` |
| Syntaxfarben, Build-Einstellungen eines Skripts | C# in `fire.Tooling`: der Lexer; die Direktiven aus dem Text (`AssemblyInfo.FromSource`) |

forge, `fire`, `native/`, `Source/`, `Prebuilt/` und ember liegen neben spark: der Build und das Publish von spark (und von `fire.Testing`) übersetzen
forge-c mit dem C-Compiler (`obj/forge-c`, nur wenn sich seine Quellen ändern) und rufen `forge package-dist` und `forge build tools/ember/ember.fireproj`
(`src/ForgeDist.targets`, statt `FireVm.targets`/`StandardSources.targets`); `setup.iss` packt den Ordner des Publish, `/DForgeDist` bleibt als zweiter Weg.
Ohne forge daneben (ein Build mit `-p:BuildForgeDist=false`) baut spark forge-c aus `native/forge` des Repositorys in einen Cache (`fire.Tooling.Forge`).

**Entfernt:** `src/fire.Compiler`, `src/fire.Native`, `src/fire`, `src/fire.Package.Manager`, `src/fire.Ember`, `src/FireVm.targets`,
`src/StandardSources.targets`; in spark `LiveDiagnostics` (bis auf Tor und Cache), `ScriptSymbolIndex*`, `CompletionEngine`, `NavigationEngine`,
`BuiltinMembers`; in `fire.Testing` die Prüfungen des C#-forge (Tabelle in [FORGE_C.md](spec/FORGE_C.md#phase-d-schritt-6-conformance-und-die-distribution-ohne-net));
die Vergleichsskripte `tests/compare-frontend.sh`, `compare-commands.sh`, `compare-ember.sh` mit `extract-markup.py`, `model-corpus.py`, `fpk-compare.py`,
`ember-fake-github.py` (sie verglichen mit dem C#-forge bzw. -ember, den es nicht mehr gibt; neu ausrichten lohnte nicht: forge-c ist jetzt das Maß).

**Was in C# bleibt und warum:** spark (`fire.Editor`, Avalonia - bis Phase F), `fire.Tooling` (die Liste (c) oben: Wege, Berichte, Debugger-Clients,
Kindprozesse, Modelle der Editoren, Daten der Toolchains, Lexer), `fire.Project` (das Projektmodell, das spark bearbeitet, mit dem Paketmodell, das es liest),
`fire.UI.Markup` (das Modell der Markup-Editoren und `GeneratePreview` der Entwurfsansicht - der Generator selbst ist in C, die Editoren brauchen das
Modell), `fire.Git`, `fire.Imaging`, `fire.Device.Manager` (die Geräte von spark), `fire.Testing` (die Prüfungen von spark) und `fire.Benchmarks`
(misst `fire`, übersetzt mit forge). Damit gilt M3: wer nur forge und die VM braucht, braucht kein .NET, und der Bau von fire selbst (forge, `fire`, die
Pakete) braucht nur einen C-Compiler.

**Geprüft** (23.11.2026, Linux): spark gebaut (mit `ForgeDist.targets`) und unter Xvfb bedient - eine Solution geöffnet, ein Konsolen- und ein
Fensterprogramm gebaut und gestartet, Vervollständigung und Diagnosen beim Tippen, ein Haltepunkt mit Fortsetzen, der Test-Explorer (2 Tests gefunden und
gelaufen), der Package Manager (Liste, Suche, Installieren aus einem lokalen `PackageSource/`); `forge conformance` 1176 von 1176 Fällen (mit `Prebuilt/`);
`fire.Testing` 1168 Prüfungen, offen danach 2 (unten); ember in fire allein mit seinen Hauptbefehlen.

**Offen:** eine Lücke von forge-c, die die Prüfungen von spark zeigen: die Meldung einer falschen Bau-Reihenfolge der Natives im nativen Build ist noch
`unported: PackageException` ohne die Zeile des `#include` (`gen/packages.c`). (Behoben: `forge serve` kannte die Natives von Bibliotheksprojekten nicht -
in `Packages.firesln` zeigte die Analyse `Unknown identifier '__GRPH...'`; jetzt liest der Server sie aus `projectFile` wie `build`, mit den Brücken
einer Standardbibliothek, [FORGE_SERVE.md](spec/FORGE_SERVE.md#dokumente-und-kontext).) Die Lücken der Tests in [FORGE_C.md](spec/FORGE_C.md#phase-d-schritt-6-conformance-und-die-distribution-ohne-net) (Sanitizer und Sonderläufe in
`conformance`, die Fälle mit Servern, die Linkages und Debug-Protokolle als Daten); `forge serve` kennt noch keine Prüfung ganzer Projekte im Server (spark
fragt `diagnose` je Datei; die Fehler eines Projekts im Bericht von `forge bytecode`/`build` nennen ihre Datei nicht, wenn sie aus Resolver oder Compiler kommen); ein Build unter Windows mit `ForgeDist.targets` (gcc von w64devkit) und der Installer sind nicht geprüft; spark ohne forge (kein
C-Compiler) hat keine Sprachdienste mehr.

Danach: der **.NET-Wrapper** über `fire_embed.h` (NuGet-Paket mit P/Invoke, ersetzt `RuntimeSession` für Einbetter) und ABI v2 der Paket-Natives.

### Phase F - die IDE und ember nach fire (Ausblick)

Eigener Plan, sobald M3 steht. Was dafür in den Packages fehlt bzw. zu prüfen ist:

* ein **Code-Editor-Control** (`ui` hat `38_textarea.fire`; es fehlen Syntaxfarben, große Dateien, Faltung, Completion-Popup, Markierungen des Debuggers);
* Docking (`ui.docking` gibt es), Dialoge, Menüs, Baum, Markdown-Ansicht;
* Git über den `git`-Prozess statt libgit2; MCP-Server über `packages/mcp`; der Assistent ist schon ein Package.

**ember** ist der erste Härtetest (wie in ROADMAP.md vorgesehen): klein, Befehlszeile, braucht Prozesse, SHA-256, Zip und HTTP - alles als
Package vorhanden. Den Teil, den auch forge braucht, bekommt es über das Package `forge`. Ob ember wirklich fire wird oder doch mit nach C geht, ist noch offen.

**ember in fire - umgesetzt** (23.11.2026, `tools/ember/`, 3 650 Zeilen fire in 9 Dateien, ein Projekt `ember.fireproj`): die ganze Befehlszeile des
C#-ember (`src/fire.Ember` mit `src/fire.Package.Manager`) - `find`, `install` (Name, `Name@Version`, `.fpk`, `github:owner/repo[@ref][#name]`, `--name`,
`--force`, Abhängigkeiten), `update` (Quellen und GitHub: Releases, Zweige, Tags/Commits gepinnt), `remove` (auch `github:`), `list`, `sources`, `create`,
`blank`, `forge` (Prüfung der Beschreibung und der Bauordnung der Natives, Dokumente, Vorlagen, die Kopie mit absoluten Pfaden), `index`, `cache [clean]`;
dazu die Quellen (Ordner, Index im Netz, `file:`, `ember.json`), Prüfsumme und Größe der Downloads, nur https (http nur von dieser Maschine), die
GitHub-API (Releases, Commits, Assets mit `digest`/`.sha256`/`SHA256SUMS`, trust on first use, Umleitungen von Hand, das Token nur für den Host der
API, Ratenlimit), der Quellbau aus dem Tarball (ein Tar-Leser in fire, `package.json` im Wurzelordner oder `forge build` eines Bibliotheksprojekts),
`ember-lock.json` und der Aufruf von `forge install-standard` (die Stempel schreibt weiter forge). Gleiche Ablage, gleiches JSON (das Format von
System.Text.Json bis auf die Rohwerte von `{ "file": ..., "order": n }` und die beiden Escaper: alle 53 `package.json` des Stores liest und schreibt
ember in fire Byte für Byte gleich), gleiche Meldungen und Exitcodes - die beiden sind austauschbar. Der Teil, den forge zum Bauen braucht, bleibt in
forge-c (`native/forge/project`) und wird nicht doppelt gehalten; ember braucht kein Package `forge` (Schritt 7): es ruft forge als Prozess.
Gebaut mit `forge build tools/ember/ember.fireproj -o ember` (VM, 1,9 MB), mit `--engine native` (tcc) oder mit forge-c. **Geprüft**
(`tests/compare-ember.sh <C#-Bin> <ember in fire>`: beide in einem eigenen Store am selben Ort, ein Index und ein GitHub auf dieser Maschine -
`tests/ember-fake-github.py` -, 45 Befehle mit Ausgabe, Fehlerausgabe, Exitcode und vier Abzügen des Stores und der geschriebenen Dateien): **139 von
139 gleich**, mit dem Programm der VM (C#-forge und forge-c) und nativ. Verschieden und vom Skript ausgeblendet: die Bytes eines selbst gepackten `.fpk`
(Deflater von `packages/compression` gegen zlib-ng, wie bei forge-c) und damit dessen Prüfsumme. Die C#-Fassung bleibt, bis die fire-Fassung im
Alltag läuft.

Was der Härtetest an der Sprache und den Packages gefunden hat (nichts davon geändert, die Semantik bleibt):

* **Kein `??`** (nur `??.`): `a ?? b` ist ein Syntaxfehler; ember hat eine Hilfsfunktion `Ci.Or(a, b)`.
* **Schlüsselwörter als Namen**: `from`, `where`, `is`, `word`, `in`, `of`, ... sind auch als lokale Variablen verboten (`string from = ...` ist
  E3057) - beim Portieren aus C# stolpert man oft darüber; die Meldung ist klar.
* **Besitz**: `obj.feld = lokaleListe` macht nur eine Referenz; die Liste stirbt mit dem Block, und erst die nächste Benutzung ist eine
  `DestroyedException` (zur Laufzeit, ohne Warnung des Typprüfers - TC031 gibt es nur für Argumente von Konstruktoren und Methoden). Richtig ist
  `obj.feld = take lokaleListe`. Das war der einzige Laufzeitfehler des Ports.
* **Lambdas fangen Kopien**: eine Sortierung nach einer Liste von Schlüsseln (`index.Sort((a, b) => orders[a] - orders[b])`) kopiert die Liste
  (Warnung TC023); ember sortiert stabil von Hand.
* **`json`**: `Json.Parse` liest keine Kommentare und kein Komma am Ende (beides nimmt der C#-ember), und es gibt kein Schreiben mit festem Format.
  ember hat einen eigenen Leser und Schreiber in fire (`tools/ember/json.fire`, Format und Escaper von System.Text.Json) - ein Kandidat für das Package.
* **`compression`** hat Zip und gzip, aber kein **Tar**: der Leser für die Tarballs von GitHub (ustar, pax `path`, GNU-Langnamen) steht in ember
  (`Tar` in `github.fire`) - ein Kandidat für das Package.
* **`http`**: `Http.Client` hält den Körper im Speicher (eine Antwort von GitHub bis 100 MB auch); ein Download mit eigenen Kopfzeilen je Sprung
  (das Token nur für den Host der API) geht nur mit `followRedirects = false` und dem Client. Eine zu große Antwort ist `HttpException` Code 4 ohne die
  angekündigte Länge - ember meldet dann "is bigger than", wo der C#-ember bei einem zu großen `Content-Length` "has N bytes" sagt. `Http.Url.Resolve`
  löst `../` nicht auf (ein relativer `url` eines Index mit `..`).
* **Keine regulären Ausdrücke** (die Namensmuster, `#include`, SHA-256 in einem Text sind von Hand), kein `Guid`, kein Vergleich ohne Groß- und
  Kleinschreibung (`ToUpper` auf beiden Seiten statt OrdinalIgnoreCase).
* `Process.Run` nimmt die Argumente nur als `string[]`, eine `List<string>` ist eine `ArgumentException` zur Laufzeit.
* `Environment.ProgramPath` ist unter `forge run` der Pfad von `fire`, nicht die Quelldatei (anders als in 19-standard-library.md beschrieben).

**Geschlossen** (23.11.2026, alles in beiden Frontends, VM und nativ; ember benutzt es, `tests/compare-ember.sh` weiter 139 von 139 gleich):

* `a ?? b` und `a ??= b` (docs/language/03-operators.md; Vorrang und Typ wie in C#) - ember schreibt `??` statt `Ci.Or`.
* `is`, `of`, `from`, `under`, `where` sind kontextuelle Wörter: Namen von Variablen, Parametern, Feldern (docs/language/01-basics.md). Schlüsselwörter
  bleiben `in` (`foreach (in in xs)`, `foreach (Dog d in dogs)` - nicht von einer Deklaration zu unterscheiden), `on` (`Dog on` am Anfang einer
  Anweisung: Deklaration oder Umbinden), `word` und die anderen Typnamen (`word * p`, `word[n]`) und `and`/`or`.
* Besitz: die Warnung **TC036**, wenn ein neuer Wert eines Blocks (`var x = new ...`) ohne `take` in einem Feld landet, das den Block überlebt (nicht
  bei `take x`, `x.TakeTo(...)`, `return x`, x als Argument eines Aufrufs, einer Property, einem Halter aus demselben Block, auf oberster Ebene des
  Programms). Sie fand einen echten Fehler in `packages/net.channel` (der vergrößerte Puffer von `Feed`).
* Lambdas fangen Kopien: das ist die dokumentierte Regel (12-lambdas.md); dazu jetzt die Redewendung für eine Sortierung nach einer Liste
  (`func (int a, int b) on orders => this[a] - this[b]`, keine Kopie) und was die Kopie kostet.
* `json`: `JsonNode` (strikt, oder mit `JsonReadOptions(true)` Kommentare und Komma am Ende, auch für `Json.Parse`/`Json.Deserialize`), Schreiben
  kompakt oder eingerückt mit den beiden Escapern von System.Text.Json - ember liest und schreibt damit (sein Leser/Schreiber ist weg).
* `compression`: `Tar.Archive` (ustar, pax, GNU-Langnamen, gzip) - ember entpackt die Tarballs damit.
* `http`: `Client.Send` liefert den Körper als Strom (`Http.BodyStream`, `SaveTo`, `ReadBody`), `GetFile`; `HttpException.length` nennt die
  angekündigte Länge; `Url.Resolve` nach RFC 3986 (`../`, `./`). ember lädt Pakete und GitHub-Dateien jetzt in die Datei statt in den Speicher und
  meldet ein zu großes `Content-Length` wie der C#-ember ("has N bytes - more than ember takes").
* Neues Standard-Package `regex` (Backtracking in fire, dokumentierte Teilmenge von .NET), `Guid` in `random` (ember nimmt es für seine
  Zufallsnamen), `string.EqualsIgnoreCase`/`CompareIgnoreCase` (ordinal ohne Groß-/Kleinschreibung; `Ci` von ember benutzt sie).
* `Process.Run`/`Start` nehmen `string[]` oder `List<string>` (jedes `IEnumerable`).
* 19-standard-library.md: `Environment.ProgramPath` ist unter `forge run` der Pfad von `fire`.

Offen: die Meldungen eines kaputten JSON sind eigene Texte im Stil von System.Text.Json (Zeile/Byte können abweichen); der Quellbau eines
Bibliotheksprojekts (`forge build` als Prozess) und echtes https gegen api.github.com sind nicht im Vergleich; Windows ungeprüft. Danach: ember in
spark (das Fenster des Package Managers ruft heute die C#-Bibliothek) und das Entfernen von `src/fire.Ember`.

Reihenfolge der IDE: Rahmen mit Projektmappe und Texteditor -> Bauen/Ausgabe -> Debugger -> Designer (fxml, Schaltplan, Datenmodell, Pixel, Hex,
Mesh, Blöcke) -> Notizen, Git, Pakete, Geräte -> Installer. Die Protokolle aus L4 sind dann schon da und erprobt; wo ein Prozess zu schwer
ist, nimmt die IDE die Packages aus L6.

## Risiken

* **`master` bewegt sich schnell.** Jeder Commit am C#-Compiler oder an der VM muss in den Port nachgezogen werden. Gegenmittel: L3, kurze Phasen,
  und während eine Komponente portiert wird, möglichst keine neuen Features in genau dieser Komponente.
* **L1 trägt nicht** (Runtime zu sehr auf erzeugten Code gebaut, oder nur um den Preis eines größeren/langsameren nativen Builds): dann eine
  eigenständige VM mit eigenem Wertmodell - deutlich mehr Arbeit, wieder zwei Implementierungen, und ein eingebettetes Skript teilt keine Werte
  mit seinem Wirt. Der Spike klärt das vor allem anderen.
* **Skripte im nativen Wirt** (L6): Entladen eines Programms aus einer Runtime, die dafür nicht gebaut ist; das Weglassen ungenutzten Codes
  gegen ein Skript, das alles erreichen könnte.
* **Geschwindigkeit der VM** auf einer Runtime, deren schnelle Pfade für den C-Compiler gedacht sind (Inlining mit bekannten Arten). Messen in B7.
* **`LiveSession`/`UiPreview`** über Prozessgrenzen (Bildrate, Fokus, Eingaben) - der Teil, an dem "der Umbau soll kurz sein" hängen kann;
  notfalls bleiben diese beiden Ansichten bis Phase F eingeschränkt.
* **Geräte in der VM**: heute nutzt sie den Geräte-Manager und die Treiber von spark (`fire_host.dev_*`); die C-VM nimmt die C-Treiber des Packages.
  Das geteilte Gerät zwischen IDE und laufendem Skript braucht einen neuen Weg (Protokoll) oder entfällt in der Übergangszeit.
* **MSVC und macOS** sind für Teile der Runtime ausdrücklich ungeprüft (NATIVE_BACKEND, "Natives von Packages in der VM").
* **Umfang der Tests**: der Export von 38 000 Zeilen `Program.cs` ist Handarbeit an vielen Sonderfällen (Fake-Renderer, Xvfb, QEMU, DOSBox).

## Entscheidungen

Vom Benutzer entschieden (15.11.2026):

1. **Compiler und VM in C**, nicht in fire: ein Frontend in C braucht es ohnehin (Henne und Ei). Beide müssen sich **per Package einbinden**
   lassen - fire als Skriptsprache in eigenen fire-Programmen (L6).
2. **Einbetten: C-Schnittstelle.** Ein .NET-Wrapper kommt auf die Roadmap (nach M3).
3. **Der native Build darf nicht langsamer oder größer werden** - das ist wichtig und geht vor (L1, Prüfungen für den nativen Build).
4. **spark wird auf Prozesse und Protokolle umgestellt**, nicht auf einem eingefrorenen C#-Compiler gelassen: je weniger in der Zwischenzeit
   zu pflegen ist, desto besser. Der Umbau soll nicht lange dauern (darum an M1/M2 gehängt statt als eigene Phase).
5. **`forge build --engine vm` bleibt** (C-VM mit angehängtem `.fbc`).
6. **Phasenweise nach `master`**, damit der Port nicht unmergebar wird.

Offen:

* **ember**: in C mit forge, oder wie die IDE in fire (Tendenz des Benutzers: fire). Der Plan nimmt fire an und hält nur den Teil in C, den
  forge selbst zum Bauen braucht; die Entscheidung muss erst vor Phase D Schritt 3 fallen. Stand 23.11.2026: ember gibt es in fire (`tools/ember`,
  [Phase F](#phase-f---die-ide-und-ember-nach-fire-ausblick)), gleich zum C#-ember; das C#-Programm bleibt, bis der Benutzer es entfernt.
* ~~**L1** (VM auf `fire_rt.h`): vom Spike abhängig, unter Bedingung 3.~~ Entschieden durch den Spike (19.11.2026, [VM_SPIKE.md](spec/VM_SPIKE.md)):
  L1 hält, Bedingung 3 ist erfüllt.
