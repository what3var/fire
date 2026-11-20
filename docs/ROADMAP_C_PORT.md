# Roadmap: VM und Compiler von .NET nach C

Status: **Plan, mit dem Benutzer abgestimmt** (siehe [Entscheidungen](#entscheidungen)); Phase B bis Schritt 8 umgesetzt, Phase C Schritte 1 bis 4 und 6 (siehe dort). Offen ist nur der Zuschnitt von ember.

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
   Laufzeit (System V x86-64, AArch64). Offen: Regeln eines Hosts und seine Geräte (spark, Schritt 9), `inline c`, `Library`/`extern` unter Windows x64.
5. Reflection, JSON, Probes, Module und Plugins in der VM.
   **Zum größten Teil umgesetzt** ([VM_C.md](spec/VM_C.md#reflection-und-probes)): Reflection auf den Meta-Daten der Datei, die der Lader jetzt
   behält (`vm-rf` 18 von 18), Lesen/Schreiben/Aufrufen/Erzeugen nach Namen auf den Wegen des Interpreters; JSON mit der Engine des nativen Builds
   (`fire_json.h`) auf Tabellen, die die VM aus dem Programm baut (`$id`/`$ref`, Einheiten, Enums, Generics); Probes mit dem Probe-Speicher der Runtime
   (`FIRE_REFLECTION` in der VM) und einer eigenen Fassung von `probedSet` nach Namen statt der Rückrufe ohne Kontext; `linq` (bis auf einen Fall von
   Besitz aus Schritt 2). Offen: Fire-Plugins und Module (das Laden der Klassen eines zweiten Programms in ein laufendes; kein Fall des Korpus).
   Maß: **869 von 1128** (vorher 751); was fehlt, ist Schritt 3 (rund 185) und Schritt 2 (rund 75).
6. Debug-Agent (L4): Haltepunkte, Schritte, Variablen über die Scope-Ebenen, `set`, Pause, Zeilenprofil.
   **Umgesetzt** ([VM_C.md](spec/VM_C.md#debugger-schritt-6-vm_debugc)): `fire x.fbc` mit `FIRE_DEBUG`/`--debug` spricht das Protokoll nativer Programme über
   den Agenten der Runtime (tcp, stdio), `NativeDebugTarget` debuggt die VM wie ein natives Programm; die Halte entscheidet ein Haken vor jeder Instruktion
   wie die Schrittschleifen der C#-VM (verschachtelte Frames unsichtbar), die Frames und Scopes baut die VM für den Agenten, wenn ein Thread anhält.
   Die `.fbc` eines Debug-Programms trägt seine Quellen (Datei, SHA-256). Tests: `FIRE_TESTS_FILTER=VM-Debugger` - die Fälle des nativen Debuggers gegen
   die C#-VM und seine Protokoll-Prüfungen. Offen: Eingabe über den gerahmten Transport für Paket-Natives, Plugins.
7. Programm `fire` (`fire run x.fbc`); der Korpus, vom C#-Compiler übersetzt, läuft mit den erwarteten Ausgaben; Benchmarks gegen die C#-VM.
   **Umgesetzt** ([VM_C.md](spec/VM_C.md#das-programm-fire-schritt-7)): `fire run x.fbc [--] args`, `fire a.fbc b.fbc -- args`, `help`, `version`,
   `--debug`, `--stats`, `--time`; Exitcodes 0, 1 (unbehandelt), der von `terminate`, 2 (nicht ladbar, Befehlszeile). Eine VM je Paar von Breiten
   (`fire-i32`, `fire-i32-f32`, ...: dieselben Quellen mit `FIRE_INT32`/`FIRE_FLOAT32`); `fire` startet für ein Programm anderer Breiten die Variante neben
   sich. Gebaut mit forge: `forge build-vm` nach jedem Build von forge/spark/Tests (`src/FireVm.targets`) neben forge, ein Publish nimmt es mit
   (`setup.iss`); entschieden: auf der Maschine gebaut (forge trägt die Quellen), nicht vorgebaut je RID. Maß: **1073 von 1128** (mit den Varianten der Breiten; vorher 1065), mit den Korrekturen von Schritt 2 zusammen **1126 von 1128**; Benchmarks (`tests/bench`, Performance): die C-VM 1,7- bis 9-mal so schnell wie die C#-VM (zusammen 255 ms gegen 697 ms), 13,7 MB RSS gegen 77-129 MB des C#-Prozesses.
8. `forge build --engine vm` bleibt: die C-VM mit angehängtem `.fbc` (und den Bibliotheken der Packages) statt der .NET-Einzeldatei.
   **Umgesetzt** ([VM_C.md](spec/VM_C.md#gepackte-programme-schritt-8)): eine Datei - `fire` der Breiten des Programms, dahinter die `.fbc` und die
   Bibliotheken der Pakete (Payload `FIREVMP1`); die Bibliotheken werden beim ersten Start in den Cache des Benutzers ausgepackt (ein Ordner je
   Prüfsumme; entschieden: ausgepackt statt aus dem Speicher, wegen Windows). `fire` neben forge, sonst mit dem C-Compiler der Maschine in den Cache
   gebaut. Die .NET-Einzeldatei bleibt bis M1 wählbar: `FIRE_VM_ENGINE=dotnet`. hello world: 748 KB (die .NET-Einzeldatei 729 KB plus das installierte
   .NET). Windows (MinGW) übersetzt, nicht gelaufen. Tests: `FIRE_TESTS_ONLY=packer`.
9. **spark auf die C-VM** (L4): Ausführen als Kindprozess, Debuggen über `NativeDebugTarget`, `LiveSession` und `UiPreview` über Kindprozess und
   gemeinsamen Speicher, Treiber-Agent und Geräte.
   **Zum größten Teil umgesetzt** ([VM_C.md](spec/VM_C.md#spark-auf-der-c-vm-schritt-9)): F5/F10/F11/Ctrl+F5 mit der Engine `vm` übersetzen über
   `forge bytecode ... --report json` (mit den Overlays; der Bericht nennt die `.fbc`, die Bibliotheken der Pakete und das `fire` der Breiten) und starten
   `fire run x.fbc` als Kindprozess - Ausgabe ins Output-Panel (zeilenweise: `FIRE_STDOUT_LINES`), Eingabe über die Pipe, Exitcode, Stop; Debug über
   `NativeDebugTarget` (TCP, `"engine": "vm"`), dieselben Panels wie nativ. `FIRE_SPARK_VM=dotnet` behält die C#-VM in spark bis M1. `LiveSession`
   (Schaltplan-Editor) läuft in `fire`: Bilder, Zeiger und Ereignisse über eine Datei, die beide abbilden (`FIRE_DISPLAY_SHM`, der Host-Display der
   Bibliothek von graphics/windows), Nachrichten über stdout (`FIRE_PRINT_RECORDS`), Befehle über stdin (`__HostCommand`); scheitert ein Programm in `fire`
   vor seinem ersten Bild, läuft es in der C#-VM weiter. Behoben in der VM: der Hauptthread wartete am Einstieg mit der GIL (Scopes am Einstieg hingen).
   Tests: `FIRE_TESTS_ONLY=cvmrun`, die Live-Sitzung in `circuiteditor`; unter Xvfb mit spark: Konsolen- und Fensterprogramm, Haltepunkt, Schaltplan.
   **Offen**: der Schaltplan-Editor selbst läuft wegen eines Fehlers der C-VM noch über den Rückfall (`DestroyedException` in `UI.CircuitView.DrawBackground`);
   `UiPreview` und die Vorschau der Blöcke laufen noch im Prozess (Plan in VM_C.md); der Treiber-Agent bleibt in der C#-VM (fire-Plugins, das geteilte Gerät);
   `FIRE_DISPLAY_SHM` und `__HostCommand` unter Windows; "Build virtual" und `check` (MCP) übersetzen noch in spark (M2).

**M1:** forge (noch C#) und spark benutzen nur noch die C-VM. `fire.Runtime`, `fire.Terminal*`, die Bridges, die C#-Natives und `VmDebugTarget`
werden entfernt.
**Stand** (19.11.2026): Ausführen und Debuggen in spark und `forge build --engine vm` gehen über die C-VM. Was das Entfernen noch aufhält:
(1) die Fälle des Korpus, die die C-VM noch nicht richtig macht (rund 55 von 1128, dazu der Fehler im Schaltplan-Editor) - solange gibt es den Rückfall der
Live-Sitzung und `FIRE_SPARK_VM=dotnet`; (2) fire-Plugins in der C-VM (Treiber-Agent, `Library.Load` von `.fireplugin`); (3) das geteilte Gerät zwischen
spark und einem Programm (`fire_host.dev_*`, `DeviceManager`) - ein Protokoll oder bis Phase F ohne; (4) `UiPreview`/Blockvorschau in `fire`;
(5) `forge run`, `forge test` und die Tests (`fire.Testing`: RuntimeSession, VmDebugTarget, die C#-VM als Vergleich) hängen an `fire.Runtime`;
(6) `forge build --engine vm` mit `FIRE_VM_ENGINE=dotnet`, der .NET-Packer; (7) Windows: `fire` ungeprüft gelaufen, `FIRE_DISPLAY_SHM`, `__HostCommand`,
`Environment.Args`.

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
2. NativeBuilder, Toolchains (gcc, clang, MSVC, tcc, Emscripten, ESP-IDF, Open Watcom), Linkage, `PackageLibrary`, Stats, Boot-Images, PE-Ressourcen.
3. Projekt, Solution, Bauplan, Vorlagen, Beispiele; von der Paketverwaltung, was forge braucht (Manifest, Store, `.fpk`). ember bleibt bis Phase F das C#-Programm.
4. Rest der Befehlszeile: Testläufer, `forge data`, Modellwandler (`.fmesh`, `.fblocks`, glTF), Packer.
5. `ToolchainSetup` (lädt unter Windows einen Compiler): braucht HTTPS und Entpacken im Werkzeug - über das System (WinHTTP) statt eigener TLS.
6. `forge conformance` ersetzt den Kern von `fire.Testing`; Installer (`setup.iss`) und Paketierung ohne .NET-Werkzeuge.
7. `fire_embed.h` und die Packages `forge`/`script` (L6), mit Tests in fire (ein Programm übersetzt und startet ein Skript, VM und nativ).
8. **forge mit einem Frontend in fire** (Wunsch vom 19.11.2026): ein kleines fire-Programm (`tools/forge.fire`: `#import "forge"`, die
   Befehlszeile unverändert an `Forge.Main(args)` durchgereicht, Ausgabe und Exitcode zurück), das mit forge selbst für jedes Ziel gebaut
   wird - fire baut sich damit selbst für andere Plattformen (`forge build tools/forge.fire --engine native -t <ziel>`; libforge und die VM sind C
   und gehen als Natives des Packages mit). Voraussetzungen: Schritt 7 (Package `forge`, ohne globalen Zustand, ohne `exit` - L6), die
   Toolchains im Ziel (Prozesse starten, Dateien), die Standard-Packages als Quellen im Ziel. Prüfung: das so gebaute forge übersetzt den Korpus
   byte-gleich wie das C-forge und baut sich selbst noch einmal (Fixpunkt).

**M3:** forge und die VM sind C-Programme und C-Bibliotheken; wer nur sie braucht, braucht kein .NET. Der Bau von fire selbst braucht nur einen
C-Compiler. `fire`, `fire.Compiler`, `fire.Native` und der Kern von `fire.Testing` verlassen die Solution; .NET steckt nur noch in spark und ember.

Danach: der **.NET-Wrapper** über `fire_embed.h` (NuGet-Paket mit P/Invoke, ersetzt `RuntimeSession` für Einbetter) und ABI v2 der Paket-Natives.

### Phase F - die IDE und ember nach fire (Ausblick)

Eigener Plan, sobald M3 steht. Was dafür in den Packages fehlt bzw. zu prüfen ist:

* ein **Code-Editor-Control** (`ui` hat `38_textarea.fire`; es fehlen Syntaxfarben, große Dateien, Faltung, Completion-Popup, Markierungen des Debuggers);
* Docking (`ui.docking` gibt es), Dialoge, Menüs, Baum, Markdown-Ansicht;
* Git über den `git`-Prozess statt libgit2; MCP-Server über `packages/mcp`; der Assistent ist schon ein Package.

**ember** ist der erste Härtetest (wie in ROADMAP.md vorgesehen): klein, Befehlszeile, braucht Prozesse, SHA-256, Zip und HTTP - alles als
Package vorhanden. Den Teil, den auch forge braucht, bekommt es über das Package `forge`. Ob ember wirklich fire wird oder doch mit nach C geht, ist noch offen.

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
  forge selbst zum Bauen braucht; die Entscheidung muss erst vor Phase D Schritt 3 fallen.
* ~~**L1** (VM auf `fire_rt.h`): vom Spike abhängig, unter Bedingung 3.~~ Entschieden durch den Spike (19.11.2026, [VM_SPIKE.md](spec/VM_SPIKE.md)):
  L1 hält, Bedingung 3 ist erfüllt.
