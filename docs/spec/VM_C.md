# Die VM in C (`native/vm`)

Status: **Phase B, Schritte 1 bis 8, Schritt 9 zum größten Teil** (spark auf der C-VM, [unten](#spark-auf-der-c-vm-schritt-9)) aus [ROADMAP_C_PORT.md](../ROADMAP_C_PORT.md#phase-b---die-vm-in-c) (19.11.2026). Der Spike davor:
[VM_SPIKE.md](VM_SPIKE.md). Die VM ist ein Interpreter auf `fire_rt.h` (L1): dieselben `Value`s, Objekte, Besitzlisten, Ausnahmen, Texte und
Threads wie ein natives Programm; was nur die VM braucht, liegt in ihren Seitentabellen. Die Runtime ist **nicht** geändert bis auf Eingriffe hinter
`FIRE_VM`: `exitNow` aus dem Spike, für den Debugger `"engine": "vm"` im `hello`, und `fire_vmGrantAway` für die Sektionen, während das Hauptprogramm
in einem Native ist (siehe [Natives](#natives)); der native Build ist damit unverändert.

```
cc -O2 native/vm/fire.c -o fire -lm -lpthread -ldl    (gcc 13, clang 18, tcc; MinGW: -lws2_32; eine Datei, die alles einbindet)
forge bytecode x.fire -o x.fbc && ./fire run x.fbc [args]   (fire neben forge findet dessen Bibliotheken; sonst FIRE_PKG_PATH=<ordner von forge>)
forge build x.fire -o x && ./x [args]                     (die VM mit dem Programm dahinter, siehe "Gepackte Programme")
./fire --debug 'tcp:127.0.0.1:PORT;token=T' x.fbc        (oder FIRE_DEBUG=...; der Debugger, siehe "Debugger")
tests/run-cases-vm.sh <forge> [<gruppe>...] [--fire <fire>] [--cache <ordner>] [--list]
```

`tests/run-cases-vm.sh` baut `fire` und seine Varianten (`fire-i32`, `fire-i16`, `fire-f32`, `fire-i32-f32`, `fire-i16-f32`, parallel) in den
Cache, übersetzt jeden Fall aus `tests/cases` mit dem C#-forge zu einer `.fbc` (`forge bytecode`, in jedem Modus aus `//# modes:`),
führt sie mit der C-VM aus und vergleicht nach denselben Regeln wie `tests/run-cases.sh` (`.expected`, `.unhandled`, `UNBEHANDELT:`, `.error`,
`.diagnostics` = Fehlerausgabe von forge und VM zusammen). Mit `--cache` bleiben die Programmdateien liegen und werden nur neu geschrieben, wenn
der Fall oder forge neuer ist - der zweite Lauf über den ganzen Korpus dauert dann rund 20 s (4 Kerne). Am Ende: Zählung je Ordner. Die Bibliotheken
der Paket-Natives sucht die VM im Ordner von forge (der Runner setzt `FIRE_PKG_PATH`), siehe [Natives](#natives).

## Dateien

Die Spalte "Gegenstück in C#" nennt, woher der Port kam: die C#-VM (`src/fire.Runtime`) mit ihrer Grafik (`src/fire.Terminal*`, den Bridges) ist seit
dem 23.11.2026 entfernt ([ROADMAP_C_PORT.md](../ROADMAP_C_PORT.md), M1); was davon noch lebt, steht im Kern (`src/fire`) oder in forge.

| Datei | Inhalt | Gegenstück in C# |
|---|---|---|
| `native/base/fire_base.h` | Arena, internierte Namen, geordnete Map (Einfügereihenfolge), Datei lesen - ohne globalen Zustand, für VM und später forge | - |
| `vm.h` | Programm (`VmProgram`, `VmClass`, `VmProto`, `VmChunk`), Interpreter (`Vm`, `VmFrame`, `VmScope`, `VmHandlerRec`) | `RuntimeClass`, `FunctionProto`, `Chunk`, `VM` |
| `vm_load.c` | der Lader: das ganze `.fbc`-Format in der Reihenfolge von `ProgramFile.Read`, behalten wird alles, was die Ausführung braucht | `ProgramFile.Read`, `CompiledProgram` (OwnerClass) |
| `vm_core.c` | Scopes, Klassen bei der Runtime registrieren, Layout/Felder/Statics, Wahl der Methode (`Candidates`, `Overloads.Select`), Zugriff, Aufrufe, Spezialisierungen, Rückrufe der Runtime, Einheiten, Typargumente | `RuntimeClass`, `Overloads`, `VM.EnterCall`, `VM.IsMemberAccessAllowed` |
| `vm_natives.c` | die Kern-Natives (`natives.def`) als Tabelle nach Namen auf den Funktionen der Runtime; die Natives der Importe nach Index (graphics, windows, reflection, Pakete - `bridges.def`), gebunden beim Start; die Natives, die ein Paket dem Host lässt (`Sleep`, `__time_unit_ticks`, die Wartefunktionen der Geräte) | `NativeRegistry.CreateDefault`, `StringMethods`, `HostNatives`, `TimeNatives`, `DeviceHostNatives` |
| `vm_pkg.c` | die Natives der Pakete als Shared Library über die C-ABI v1 (`native/abi/fire_pkg_abi.h`), der Host `fire_host` in C (Konsole, IO-/Netz-Regeln, Lambdas, Dienste, Halte), auch die Bibliothek von graphics und windows (`libfire_gfx`) | `PackageNativeBinding`, `PackageHost` |
| `vm_lib.c` | `Library` (`__LibCall`) und `extern`: Aufrufe nach einer C-Signatur, die erst zur Laufzeit feststeht | `VM.Library.cs`, `VM.ResolveDynamicExtern` |
| `vm_reflect.c` | Reflection (`__refl_*`) und Probes (`probe`, `silence`, `Reflect.Probe`) | `ReflectionNatives`, `VM.Reflection.cs`, `VM.Probe.cs` |
| `vm_json.c` | JSON: die Engine des nativen Builds (`fire_json.h`) auf Tabellen, die die VM aus dem Programm baut | `JsonMethods`, `VM.Json.cs` (`CppGenerator.Json.cs` für die Tabellen) |
| `vm_threads.c` | Threads, Tasks, Aktoren, Sektionen, Jobs, `catch threads`/`catch terminate`, die Natives der Threads (Kanäle, `Completion`, Abbruch, `await [...]`, `handover`, `Sleep`), Anfang und Ende des Programms mit Threads | `FireRuntime`, `GlobalsBroker`, `VM.Tasks`, `Channels`, `Completions`, `Cancellation` |
| `vm_run.c` | die Schleife (ein `switch` über `opcodes.def`), `DeclaredType`, Arrays einer Klasse, `finally`, Abwicklung | `VM.Execute`, `DeclaredType`, `VM.ThrowException` |
| `vm_dangle.c` | Debug: die Warnung für Verweise, die ihr Objekt überleben (ein Objekt eines Aufrufs in einem Feld oder Array, das beim Ende des Aufrufs stirbt) | `DanglingWatch`, `VM.NoteStore`/`ReportDangling` |
| `vm_plugin.c` | fire-Plugins: `Library.Load` einer `.fireplugin` (oder einer Bibliothek mit einer daneben), das Verlinken ihrer Klassen in das laufende Programm (siehe [Plugins](#plugins)) | `VM.Plugins.cs`, `PluginBytecode` |
| `vm_debug.c` | der Debugger: das Protokoll nativer Programme über den Agenten der Runtime (`fire_debug.h`), die Halte vor jeder Instruktion, die Frames für den Agenten | `VM.StepLine`/`StepInto`/`StepOut`/`RunUntilBreakpoint`, `DebugScopeChain`, `DebugGlobals`, `PauseOnUnhandled` |
| `vm_embed.c` | libfirevm (`fire.c` mit `FIRE_VM_LIBRARY`, ohne `main`): `fire_embed.h` - ein Programm im Prozess mit seinen Strömen, die Ausnahme als Daten ([EMBED.md](EMBED.md)) | `RuntimeSession` (Einbetten in .NET) |
| `fire.c` | das Programm `fire`: Befehlszeile, Programme nacheinander in einem Prozess, Abbau nach jedem (L6), die Varianten der Breiten, das Payload eines gepackten Programms | `fire.Runtime/Program.cs`, `Runtime.cs`, `PayloadLoader` |
| `src/fire.Compiler/CVm.cs`, `src/fire/Runtime/CVmPayload.cs` | `forge build --engine vm`: `fire` finden oder bauen, das Programm dahinter packen; das Format des Payloads in C# | `Packer`, `PayloadFile` |
| `src/fire.Compiler/Debugging/CVmLaunch.cs`, `src/fire.Editor/MainWindow/MainWindow.CVm.cs` | spark: Ausführen/Debuggen eines Programms der VM als Kindprozess `fire run x.fbc` | `RuntimeSession`, `VmDebugTarget` in spark |
| `src/fire.Compiler/HostDisplayFile.cs`, `native/abi/fire_gfx_host.h` (`FIRE_DISPLAY_SHM`) | die Fenster eines Programms in `fire` als Bilder in der IDE: eine Datei, die beide abbilden | `GraphicsLibrary.Instance` (Host-Display im Prozess) |

## Das Programm fire (Schritt 7)

```
fire run [--stats] [--time] [--debug <spec>] <programm.fbc> [--] [<argumente>]   ein Programm; was ihm folgt, ist Environment.Args
fire [--stats] [--time] [--debug <spec>] a.fbc [b.fbc ...] [-- <argumente>]      Programme nacheinander in einem Prozess
fire help | fire version
```

* **Exitcode**: 0; 1 für eine Ausnahme, die niemand fängt, oder einen Fehler der Runtime (L6); der Code von `terminate(code)`; 2, wenn ein Programm
  nicht gelesen oder geladen werden kann oder die Befehlszeile falsch ist (eine unbekannte Option, `--debug` ohne Transport, kein Programm). Mehrere
  Programme: der Code des letzten, das scheiterte.
* `--debug <spec>` ist `FIRE_DEBUG` ([Debugger](#debugger-schritt-6-vm_debugc)), `--stats` die Bilanz nach jedem Programm, `--time` die Zeiten von Laden,
  Lauf und Abbau (für die Benchmarks, siehe unten).
* **Bibliotheken der Pakete**: `FIRE_PKG_PATH`, neben dem Programm, dann der Ordner von `fire` selbst (main hängt ihn an `FIRE_PKG_PATH`): `fire`
  neben forge findet dessen `Packages/` und `Prebuilt/`.
* **Breiten** (`#intwidth`/`#floatwidth`): eine VM je Paar von Breiten - dieselben Quellen mit `FIRE_INT32`/`FIRE_INT16`/`FIRE_FLOAT32` (fire_rt.h),
  `fire-i32`, `fire-f32`, `fire-i32-f32`, `fire-i16`, `fire-i16-f32`. Die VM prüft die Breiten der Datei; ein Programm anderer Breiten startet bei
  einem Programm auf der Befehlszeile die Variante neben `fire` mit denselben Argumenten (`execv`; Windows: `_spawnv` und ihr Code), sonst sagt sie,
  welche sie braucht (Code 2). `forge build-vm <ordner> --variant i32-f32` (oder `--all`) baut sie. Alle Fälle mit `#intwidth`/`#floatwidth`
  laufen in ihrer Variante richtig (siehe [Breiten](#breiten)).
* **Gebaut und ausgeliefert** mit forge: nach jedem Build von fire.Compiler, fire.Editor und fire.Testing kompiliert `forge build-vm` (`src/FireVm.targets`)
  `fire` mit dem optimierenden C-Compiler der Maschine (wie die Bibliotheken der Pakete: `PackageLibrary.OptimizingToolchain`, gcc vor TinyCC) neben
  forge, nur wenn seine Quellen sich geändert haben (`fire.key`: SHA-256 der Quellen der VM und der Runtime samt Plattform-Paket). Ein Publish nimmt
  `fire` in seinen Ordner mit (spark: `FolderProfile`, den `setup.iss` packt); einer für eine andere Maschine (`-r`) baut nichts. Entschieden: **gebaut
  auf der Maschine, nicht vorgebaut je RID** - forge trägt die Quellen (eingebettet: `native/vm`, `native/base`, `native/tables`, die Runtime aus
  fire.Native) und baut `fire` sonst beim ersten Packen eines Programms in den Cache des Benutzers (siehe unten); ein vorgebautes `fire` je RID wie
  `Prebuilt/` wäre nur für Maschinen ohne C-Compiler nötig, die ohnehin keine Paket-Natives bauen können (spark bietet dort TinyCC an).
* Übersetzt mit gcc 13, clang 18, tcc und MinGW-w64 (x86_64, `-lws2_32`); unter Windows geprüft unter Wine (`FIRE_TESTS_ONLY=cvmwin`, siehe [Windows](#windows-m1-7)).

### Benchmarks gegen die C#-VM

Gemessen vor M1; seit M1 misst `fire.Benchmarks` nur noch `fire` (übersetzt mit dem C#-forge, gemessen `fire run --time`: "ran in", Minimum aus 5 nach
einem Aufwärmlauf, mit `--save`/`--check` der Ergebnisse wie bisher). Die 9 Programme aus `tests/bench` (die von `fire.Benchmarks`), Modus Performance, Linux x64, gcc -O2, 4 Kerne unter Last (load ~8, andere Builds
liefen mit): **C-VM** `fire --time x.fbc` - der Lauf (ohne Laden und Abbau; das Minimum aus 5 nach einem Aufwärmlauf), der ganze Prozess, max RSS;
**C#-VM** `fire.Benchmarks --filter x --runs 5` - `VM.Run()` (Minimum aus 5 nach dem Aufwärmen des JIT), max RSS des Prozesses (darin auch der
Compiler und der JIT: die C#-VM allein hat keinen eigenen Prozess). Die Ausgaben sind gleich.

| Programm | C-VM Lauf | C-VM Prozess | C-VM RSS | C#-VM Lauf | C#-Prozess RSS | C# / C |
|---|---:|---:|---:|---:|---:|---:|
| alloc | 13,9 ms | 15,9 ms | 13,7 MB | 47,3 ms | 101,0 MB | 3,4 |
| array | 68,7 ms | 72,1 ms | 13,7 MB | 122,6 ms | 83,3 MB | 1,8 |
| fib | 10,8 ms | 14,1 ms | 13,7 MB | 18,0 ms | 76,9 MB | 1,7 |
| float | 27,4 ms | 32,4 ms | 13,7 MB | 93,6 ms | 77,5 MB | 3,4 |
| lambda | 16,1 ms | 18,3 ms | 13,7 MB | 48,6 ms | 77,1 MB | 3,0 |
| list | 23,7 ms | 27,6 ms | 13,7 MB | 71,8 ms | 86,8 MB | 3,0 |
| loop | 50,7 ms | 52,8 ms | 13,7 MB | 139,5 ms | 77,4 MB | 2,8 |
| method | 35,4 ms | 43,0 ms | 13,7 MB | 80,2 ms | 77,8 MB | 2,3 |
| string | 8,4 ms | 13,8 ms | 13,7 MB | 75,5 ms | 129,0 MB | 9,0 |
| **Summe** | **255,1 ms** | **290,0 ms** | | **697,1 ms** | | **2,7** |

Die C-VM ist 1,7- bis 9-mal so schnell (zusammen 2,7-mal); ein ganzer Prozess der C-VM (Laden der `.fbc` mit dem Prelude ~0,7 ms, Lauf, Abbau)
ist kürzer als der Lauf allein in der C#-VM. Der Speicher der C-VM ist bei allen derselbe (13,7 MB, die Tabellen der Runtime und des Programms).

## Gepackte Programme (Schritt 8)

`forge build --engine vm` (der Standard von `build`) schreibt **eine ausführbare Datei**: `fire` mit dem Programm dahinter
(`CVm.Pack`, `src/fire.Compiler/CVm.cs`; die .NET-Einzeldatei mit `fire.Runtime` und `FIRE_VM_ENGINE=dotnet` sind mit M1 weg, 22.11.2026):

```
[fire] [Eintrag 0] [Eintrag 1] ... [Index] [Fuß]
Fuß (24 Bytes): uint64 Offset des Index, uint32 seine Länge, uint32 Version des Formats (1), "FIREVMP1"
Index: uint32 Anzahl; je Eintrag: uint8 Art (0 die Programmdatei, 1 eine Bibliothek), uint16 Länge des Namens, der Name (UTF-8, ein Dateiname),
       uint64 Offset, uint64 Länge, uint64 FNV-1a (64 Bit) der Bytes
```

Alles little-endian, nichts komprimiert (die VM hat keinen Entpacker; das Programm ist die `.fbc` mit ihren Zeilentabellen für die Stack-Traces - ohne nur bei `#stacktrace off`, [STACKTRACE.md](STACKTRACE.md)). Beim Start
liest `fire` den Fuß seiner eigenen Datei (`fire_plat_programPath`): ist ein Payload da, gehört jedes Argument dem Programm.

* **Bibliotheken der Pakete** (`LinkedProgram.PackageLibraryFiles`: `libfire_pkg_*`, für graphics/windows `libfire_gfx`, das die C-VM immer braucht):
  entschieden - **ausgepackt in den Cache des Benutzers**, nicht aus dem Speicher geladen (Windows kann keine DLL aus dem Speicher laden; ein Weg für alle
  Systeme). Ordner: `FIRE_VM_CACHE`, sonst `$XDG_CACHE_HOME/fire/native`, `~/.cache/fire/native`, `%LOCALAPPDATA%\fire\native` (ohne Heimatordner
  `$TMPDIR/fire-native-<uid>`), darin ein Ordner je Satz von Bibliotheken (FNV ihrer Namen und Prüfsummen). Eine Datei dort gilt, wenn Länge und
  Prüfsumme stimmen, sonst wird sie neu geschrieben (daneben, dann umbenannt: zwei Starts zugleich sehen nie eine halbe). Die VM sucht sie dort wie
  neben einer Programmdatei; `FIRE_PKG_PATH` geht vor. Nicht im gemeinsamen `/tmp` (eine fremde Bibliothek dort würde geladen).
* **Breiten**: gepackt wird die VM der Breiten des Programms (`CVm.Locate(intWidth, floatWidth)`).
* **Woher `fire` kommt**: `FIRE_VM_EXE` (eine Datei; ihre Varianten daneben), sonst neben forge, wenn sein Stempel `fire.key` die Quellen dieses forge
  nennt (ein veraltetes aus einem älteren Build gilt nicht), sonst gebaut mit dem C-Compiler der Maschine nach `<LocalApplicationData>/fire/vm/<rid>/<schlüssel>/`
  (gibt es keinen: die Meldung nennt den Compiler, `FIRE_VM_EXE` und `--engine native`).
* **Windows**: `#noconsole` (Subsystem), Icon und Versionsinfo gehen in die Kopie von `fire.exe`, bevor das Payload dahinter kommt (wie beim Apphost).
* **Größe** (Linux x64, gcc -O2, gestrippt): `fire` 642 KB; hello world gepackt 748 KB (die `.fbc` 107 KB, fast ganz das Prelude); mit io und time
  936 KB; mit graphics (libfire_gfx) 1,29 MB. Die .NET-Einzeldatei von hello world: 729 KB, dazu das installierte .NET (rund 70 MB).
* **Tests**: `FIRE_TESTS_ONLY=packer` (Abschnitt "die VM in C"): das Format (Rundlauf, beschädigter Eintrag, fremde Namen), hello world allein in einem
  anderen Ordner, die Argumente und `terminate`, eine unbehandelte Ausnahme, io/time aus den Bibliotheken der Datei, der Cache (zweiter Start schreibt
  nichts, eine beschädigte Bibliothek wird ersetzt), `#intwidth 32` mit `fire-i32`, `fire run`/`help`/`version`/Exitcodes der Befehlszeile; die Prüfungen
  der .NET-Einzeldatei (`Packer` direkt) sind mit M1 weg.

## forge run, test und data in der C-VM (M1)

Seit dem 22.11.2026 führt auch das C#-forge Programme nur noch in `fire` aus ([ROADMAP_C_PORT.md](../ROADMAP_C_PORT.md), M1 (5)), so wie forge-c
(`native/forge/tools/commands.c` `tcRunInVm`, `testrunner.c` `trRunInVm`, `tcRunDriver`): `CVmRun` (`src/fire.Compiler/CVmRun.cs`) linkt mit
`Linker { PackageLibraries }` (die Bibliotheken der Pakete werden gebaut), schreibt die `.fbc` mit Zeilentabellen nach `<temp>/fire-run-<guid>`
(`fire-test-`, `fire-data-`) und startet `fire run <datei> [-- <argumente>]` - die VM der Breiten des Programms (`CVm.Locate`), `FIRE_PKG_PATH` mit den
Ordnern der Bibliotheken; der Ordner geht, wenn das Programm endet.

* **`forge run`**: das Programm erbt die Konsole von forge (stdin, stdout, stderr; `CommandLineRunner.WritersAreConsole`, gesetzt von `Program.cs`); wer
  `CommandLineRunner.Run` mit eigenen Writern ruft (die Tests), bekommt die Ausgabe dort hinein (`CVmRun.RunToWriters`). Exitcode: der von `fire` - 0, 1
  bei einer unbehandelten Ausnahme (ihr Bericht mit Stack-Trace auf stderr), `terminate(n)`: n.
* **`forge test`** (`TestRunner.RunInVm`, auch der Test-Explorer von spark): die Zeilen des Treibers kommen, während sie geschrieben werden
  (`FIRE_STDOUT_LINES`); `Cancel` beendet den Prozess; endet das Programm vor dem Ende der Tests, ist die Zeile `Unhandled exception of type ...` von
  stderr die Meldung des laufenden Tests.
* **`forge data`** (`DataCommand.RunDriver`, auch der Designer von spark): der Treiber mit den Klassen der Quellen, seine Ausgabe, eine unbehandelte
  Ausnahme als Fehler.
* Die Natives von graphics, windows und reflection kennt der Übersetzer jetzt aus `native/tables/bridges.def` (`BridgeNatives`, die Ressource
  `cvm/tables/bridges.def`), nicht mehr aus den C#-Bridges; die Host-Natives der Pakete sind beim Übersetzen nur Namen.
* `FIRE_VM_ENGINE=dotnet` (die .NET-Einzeldatei, `fire.Runtime.Packer`) ist weg: `forge build --engine vm` packt nur noch `fire`.
* **Geändert**: `Environment.ProgramPath` ist bei `forge run` der Pfad von `fire` (wie bei forge-c), in der C#-VM war es die letzte Quelle.
* **Behoben** (gefunden dabei): der Aufruf einer `inline c`-Funktion beendete ein Programm in `fire`; jetzt ist er wie in der C#-VM eine abfangbare
  `ArgumentException` (`'M.Add' is an inline C function: it runs only in a native build ...`).
* **Tests**: `tests/compare-commands.sh` gegen forge-c 100 von 103 gleich (die drei: Dateien nativer Builds und die gepackte Datei von `-m debug`, die
  schon vorher abwichen - das C#-forge vor dieser Änderung schreibt dieselben, die gepackte Datei ist von Lauf zu Lauf verschieden); im Testprogramm
  laufen die Programme der Prüfungen in `fire` (`CVmTest`: übersetzt im Testprogramm, `fire run` mit `FIRE_PRINT_RECORDS`; `CVmTest.Debug` unter dem
  Debugger wie in spark; `TestCasesVm`: smoke und die Familien vm-* nach den Regeln von `tests/run-cases.sh`); was nur die C#-VM prüfte, ist seit dem 23.11.2026 mit
  ihr entfernt ([testing.md](../tools/testing.md), [ROADMAP_C_PORT.md](../ROADMAP_C_PORT.md) M1).

## Windows (M1 (7))

`fire.exe` baut mit MinGW-w64 aus denselben Quellen (`CVm.WriteSources(..., target: TargetProfile.Windows)` schreibt das Plattform-Paket von Windows, auch auf
Linux) und läuft unter Wine wie unter Linux. Was dafür in der VM und der Runtime geändert ist (22.11.2026):

* **Argumente**: `main` nimmt die Argumente aus der UTF-16-Befehlszeile (`fire_plat_eachCommandLineArg`, die Regeln der C-Runtime) als UTF-8 statt aus `argv`
  (ANSI-Codepage); mit `FIRE_ARGS_UTF8` (fire.c) gibt `fire_plat_eachArg` der Runtime die übergebenen weiter - `fire run x.fbc a` gibt dem Programm nur `a`
  (vorher `run x.fbc a`). Leerzeichen, Umlaute, CJK, Emoji, Anführungszeichen, Backslashes, leere Argumente.
* **Pfade**: UTF-8 überall (die Argumente, `fire_plat_programPath`): `vmFopen` (`_wfopen`, auch für `fbReadFile` über `FB_FOPEN` in `fire_base.h`), `_wmkdir`,
  `_wremove`, `MoveFileExW`, `FindFirstFileW`, `fire_plat_dlopen` (`LoadLibraryW`; Backslashes; ein voller Pfad mit `LOAD_WITH_ALTERED_SEARCH_PATH`, damit die DLLs
  neben einer DLL gefunden werden). Variablen mit Pfaden (`FIRE_PKG_PATH`, `FIRE_VM_CACHE`, `LOCALAPPDATA`, `TEMP`, `FIRE_DISPLAY_SHM`) wide gelesen, `FIRE_PKG_PATH`
  wide gesetzt (`_wputenv_s`).
* **Die Variante der Breiten**: `CreateProcessW` mit der Befehlszeile dieses Prozesses (nur das Programm vorn ausgetauscht) statt `_spawnv` - das setzte die
  Argumente ohne Anführungszeichen zusammen (`"a b"` kam als zwei an).
* **Bibliotheken der Pakete**: eine Programmdatei nennt sie, wie die Maschine sie nennt, die sie übersetzte (`PackageLibrary.FileNameFor`: `libfire_pkg_x.so`,
  `.dylib`, `fire_pkg_x.dll`); die VM sucht zuerst den Namen, dann den dieses Systems (`vmPkgLocalName`) - eine `.fbc` von Linux läuft unter Windows mit den
  DLLs. Das Auspacken eines gepackten Programms nach `%LOCALAPPDATA%\fire\native` (oder `FIRE_VM_CACHE`) geht wie unter Linux.
* **`FIRE_STDOUT_LINES=1`**: die C-Runtime von Windows kennt kein Zeilenpuffern (`_IOLBF` ist dort voll gepuffert) - stdout ist dann ungepuffert.
  Zeilenenden: stdout ist im Textmodus (`\r\n`, wie ein natives Programm unter Windows; spark liest Zeilen).
* **`FIRE_DISPLAY_SHM`** und **`__HostCommand`**: siehe [Programme im Fenster der IDE](#spark-auf-der-c-vm-schritt-9). `FIRE_PRINT_RECORDS` braucht nichts
  Eigenes. Das geteilte Gerät (`FIRE_DEVICE_HOST`) hatte `WSAStartup` schon.
* Eine `.fbc` ist zwischen den Systemen dieselbe; ein gepacktes Programm für Windows packt forge **auf** Windows (`--engine vm -t windows` lehnt forge auf
  Linux ab: es packt die VM und die Bibliotheken dieser Maschine) - oder auf Linux mit `FIRE_VM_EXE=fire.exe` für ein Programm ohne Natives von Paketen.

**Test** (`FIRE_TESTS_ONLY=cvmwin`, `src/fire.Testing/CVmWindowsTests.cs`; Wine, MinGW, Xvfb für die Fenster des Korpus; ein eigener `WINEPREFIX`): baut `fire.exe`
und die 5 Varianten aus den Quellen, die forge trägt, die DLLs der Pakete (`GenerateCWrapper`/`GenerateWrapper` für Windows) und `fire_gfx.dll`
(`GenerateGraphicsWrapper`) mit MinGW, und prüft unter Wine: die Befehlszeile (Exitcodes, `Environment.Args`, Pfade mit Umlauten und CJK, zwei Programme in
einem Prozess, `#intwidth 32` über `fire-i32.exe`, `FIRE_STDOUT_LINES` durch eine Pipe); gepackte Programme (`forge build` mit `FIRE_VM_EXE=fire.exe`; ein Payload
mit den DLLs von io und time: der Cache, der zweite Start schreibt nichts, eine beschädigte DLL wird ersetzt, `FIRE_VM_CACHE` mit Umlaut; mit
`FIRE_TEST_WIN_FORGE` packt auch `forge.exe` für win-x64 unter Wine); die Ansicht des Hosts (`HostDisplayFile` von spark unter Linux liest das Bild, das
`fire.exe` in die von Wine abgebildete Datei schreibt, und gibt ihm einen Klick über den Ring; `FIRE_PRINT_RECORDS`; Befehle über stdin bis `quit`); das geteilte
Gerät (`DeviceHostServer` mit dem Loopback-Treiber, ein falsches Token); und den Korpus (`tests/cases/native`, jeder Fall von forge unter Linux zur `.fbc`
übersetzt, verglichen wie `tests/run-cases-vm.sh`). Stand 22.11.2026: alle Prüfungen richtig; der Korpus **501 von 501** wie `.expected`, dazu 4 mit einer
Ausgabe von Windows (`2160` `Path.Combine` mit `\`, `2630` startet `sh`, `3470` das ALSA-Gerät von Linux, `3380` die Simulation eines Schaltkreises über 20 ms:
die Mathematik der C-Runtime von Windows rundet anders - 27 statt 28 Zündungen); 23 Prüfungen, 17-20 Minuten mit dem ersten Bau aller DLLs (Korpus 9-13 min,
4 Kerne). Dazu die Prüfung mit `FIRE_TEST_WIN_FORGE` in `FIRE_TESTS_ONLY=linkage FIRE_TESTS_FILTER=vmgraphics` (`forge.exe run` mit `fire_gfx.dll` öffnet ein Fenster):
richtig, seit sie den Stempel `.runtime` schreibt, den `forge.exe pkglibplan` nennt (der unter Linux für Windows berechnete ist ein anderer). Wine selbst: ein
`forge.exe` mit `WINEDLLOVERRIDES=mscoree=` stürzt ab (und `winedbg` wartete dann ewig: der Test schaltet ihn ab).
**Nicht geprüft**: ein echtes Windows (Wine ist nicht Windows: Pipes, Konsole, `DeleteOnClose` mit Abbildung, Defender, Pfade über 260 Zeichen), MSVC und
TinyCC für Windows (`fire_gfx_host.h` nimmt dort die Interlocked-Funktionen), Plugins, der Debugger der C-VM unter Windows (der Agent selbst ist unter Wine
geprüft: `NativeDebugTests`), die Fenster des Desktops (nur ohne Prüfung des Bildes unter Xvfb), spark selbst unter Windows (`LiveSession` startet `fire.exe`).

## spark auf der C-VM (Schritt 9)

spark führt Programme der Engine `vm` nicht mehr in seinem Prozess aus (L4), sondern als Kindprozess `fire`. Den Schalter `FIRE_SPARK_VM=dotnet` (die C#-VM in
spark: `DebugSession.CompileAsync`, `VmDebugTarget`, `LiveSession` und die Vorschauen im Prozess, `FIRE_DRIVER_AGENT=dotnet`) gibt es seit M1 nicht mehr
(22.11.2026): `CVmLaunch.UseDotnet`, `CVmScript.EngineOverride`, `LiveSession.InProcessOnly`, `UiPreview.RenderHere` und der Rückweg der Blockvorschau sind
entfernt, `DebugSession.Compile`/`CompileAsync` (über `VmDebugTarget`) lebten danach nur noch im Testprogramm und sind seit dem 23.11.2026 mit der C#-VM weg;
das Werkzeug `check` des
MCP-Servers übersetzt mit forge (`forge bytecode`, wie Start), nicht mehr mit der C#-VM in spark; der Profiler zählt nur noch native Läufe
(`fire.Runtime.LineProfile` war der C#-VM).

**Ausführen und Debuggen** (`MainWindow.CVm.cs`, `CVmLaunch`): F5/F10/F11/Ctrl+F5 mit der Engine `vm` übersetzen über forge -
`forge bytecode <datei|projekt> -m <modus> -o <bin/vm-<modus>/name.fbc> --report json` mit den Overlays des Editors (der Bericht von `build`; neu für
`bytecode`: `--report`, `--overlay`, `-p`). Der Bericht nennt die Programmdatei (`program`), die Bibliotheken der Pakete, die forge dafür jetzt baut
(`library`, `Linker.PackageLibraries` - sonst nur für ein gepacktes Programm), und das `fire` der Breiten des Programms (`vm`, `CVm.Locate`). Dann:

* **Release/Performance**: `fire run x.fbc [-- args]`, `FIRE_PKG_PATH` = die Ordner der Bibliotheken; stdout und stderr zeilenweise ins Output-Panel, das
  Eingabefeld darunter schreibt in stdin (`IO.Stdio.ReadLine`), der Exitcode in die Statuszeile, Stop (Shift+F5) beendet den Prozessbaum.
* **Debug**: derselbe Aufruf mit `FIRE_DEBUG=tcp:127.0.0.1:PORT;token=T` (`NativeLaunch.LaunchDebug`, stdin bleibt offen: die Eingabe geht auch beim Debuggen über
  die Pipe), `NativeDebugTarget` wie bei einem nativen Programm; das `hello` sagt `"engine": "vm"` (`IDebugTarget.Engine`). Haltepunkte, Schritte, Scopes, Globals,
  Threads, `set`, Profil sind die des Agenten (Schritt 6); das Stack-Panel (Operandenstapel) ist wie bei nativ ausgeblendet. **Nicht** gerahmt über stdio
  (`NativeLaunch.LaunchDebugStdio` gibt es, `framed: true`): die Natives der Pakete (io) lesen stdin selbst und nähmen die Rahmen mit.
* **`FIRE_STDOUT_LINES=1`** (setzt spark): `fire` schreibt stdout auch in eine Pipe zeilenweise (`setvbuf`), sonst käme die Ausgabe erst am Ende.
* **Das Ende mit spark** (`fire.Debugging.ChildProcesses`): was spark startet (`fire` mit Run, Debug, Live-Sitzungen, Skripten; forge, `forge serve`, native
  Programme, der Test-Runner) endet mit spark - bei `ProcessExit` und SIGTERM/SIGHUP tötet spark sie samt Baum. Linux, auch wenn spark getötet wird:
  `FIRE_END_WITH_PARENT=<pid von spark>`; ist das der Elternprozess von `fire`, verlangt `fire` beim Start `prctl(PR_SET_PDEATHSIG)` mit einem Echtzeitsignal
  und endet (Code 143), wenn der Elternprozess weg ist - der Kernel schickt es schon beim Ende des Threads, der `fire` startete (die Threads des .NET-Pools
  enden), der Handler sieht darum nach `getppid()`. Ein Enkel, der die Variable erbt, nimmt sie nicht. Windows: jedes Kind kommt in ein Job-Objekt mit
  `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, das mit spark endet. Geprüft: `FIRE_TESTS_ONLY=cvmrun` ("Ende mit spark").
* **Behoben**: der Hauptthread wartete am Einstieg mit der GIL (der Agent parkt ihn als einen ohne - ein natives Programm startet so); `scopes`/`globals`/`this`
  am Einstieg - die Panels von spark fragen sofort - hingen im Agenten für immer (`vmDbgBegin` gibt die GIL ab, solange er wartet).

**Programme im Fenster der IDE** (`LiveSession`: der Schaltplan-Editor; L4 "der heikelste Teil"). Was die Ansicht braucht: Bilder in ihrer Größe mit etwa 60/s
(ein unverändertes Bild wird nicht noch einmal übergeben), Maus, Tasten, Text, Rad, Größe, Schließen; den Mauszeiger des Programms; Nachrichten des Programms
(`print`, eine Nachricht je `print`, auch mehrzeilig) und Befehle des Hosts (`extern string __HostCommand()`, "" wenn keiner da ist, ohne zu warten). Der Fokus
bleibt in spark (die Ansicht reicht die Tasten weiter, das Programm hat kein Fenster des Systems). Das Protokoll:

* **Bilder, Zeiger, Ereignisse: eine Datei, die beide abbilden** (`HostDisplayFile` in C#, `FIRE_DISPLAY_SHM=<datei>` für `fire`; unter Linux in `/dev/shm`,
  dünn besetzt). Kopf (4 KB): `FIRESHM1`, Version, Größen; `eventWrite`/`eventRead` eines Rings (1024 Ereignisse zu 128 Bytes, die Felder von
  `FireDisplayHostEvent` ohne `data`); `frameSeq` (ungerade, während das Programm schreibt - der Host kopiert, wenn sie gerade und vorher wie nachher dieselbe
  ist), Breite, Höhe, Fenster, Zeiger, pid; dahinter das Bild (RGBA, Platz für 3840 x 2160). Kein Pipe, keine Sperre zwischen den Prozessen: spark schaut
  etwa alle 8 ms nach; ein Bild mit vsync wartet im Programm 1/60 s. Im Programm ist das der Host-Display von `fire_display_headless.h` (`fire_display_host`) -
  `fire_gfx_host_fromEnvironment` in `fire_gfx_host.h`, gerufen von `fire_pkg_set_host` der Bibliothek von graphics und windows, also **nur in der Bibliothek der
  VM**, nicht im nativen Build. Windows: `CreateFileMappingW`/`MapViewOfFile` derselben Datei (die Ansichten einer Datei sind zwischen Prozessen kohärent);
  `HostDisplayFile` legt sie dort mit `FileShare.Delete` und `DeleteOnClose` an, damit sie auch geht, solange das Programm sie noch abbildet.
* **Nachrichten**: stdout, `FIRE_PRINT_RECORDS=1` - ein `print` ist eine Zeile, ein Zeilenumbruch in seinem Text kommt als `\x1F` (ein Haken an `print` der VM).
* **Befehle**: stdin, eine Zeile je Befehl; `__HostCommand()` ohne `#extern` liest sie ohne zu warten (ein Haken in `vmCallExtern`, POSIX: `poll`; Windows: `PeekNamedPipe`, wo eine Pipe das nicht kann -
  Wines Pipes des Wirtssystems - oder stdin eine Konsole ist, ein eigener Lese-Thread ab dem ersten Aufruf).
* **Übersetzt** wird das Programm der Ansicht in spark (`Linker` mit `PackageLibraries`, `ProgramFile.Write`) - wie die übrigen Sprachdienste bis M2 (`forge serve`).
* **Kein Rückfall mehr** (22.11.2026): bis dahin startete `LiveSession` ein Programm, das in `fire` vor seinem ersten Bild scheiterte, in der C#-VM von spark
  neu. Der Schaltplan-Editor brauchte ihn zuletzt nicht mehr: seine `DestroyedException` in `UI.CircuitView.DrawBackground` (nur über `UI.Root.Update`)
  war das `SetField` der C-VM auf ein Objekt mit Sonden - die Elemente der UI bekommen beim ersten Anordnen welche (`Root.Watch`) -, das die Besitzregeln
  des Speicherns überging: `this.gridLayer = new Framebuffer(w, h)` starb mit der Anweisung. Behoben mit `40e81bcb`; `vm-life/0110` und die Live-Sitzung mit
  jedem Beispiel-Schaltplan halten es fest. Jetzt endet die Ansicht eines Programms, das in `fire` scheitert, mit dem Grund (`Ended`, stderr von `fire`);
  `ChildFailure` und `NoFallback` sind weg. Ohne `fire` (kein C-Compiler) sagt `Start` das, statt still in die C#-VM zu gehen. In der C#-VM bleibt nur
  `FIRE_SPARK_VM=dotnet`; der Treiber-Agent läuft seit dem 22.11. in `fire` (siehe unten).
* **Das geteilte Gerät** (M1 (3), 22.11.2026; `native/vm/vm_devhost.c`, `DeviceHostServer` in fire.Device.Manager): ein Programm, das spark in `fire` startet
  (F5/Ctrl+F5/Debug), nimmt die Geräte des `DeviceManager` von spark - dessen Treiber (auch die, die der Treiber-Agent einhängt), das Teilen eines Geräts
  zwischen IDE und Programm, den Paketmitschnitt, das Standardgerät -, wie die C#-VM im Prozess über `fire_host.dev_*`. **Entschieden: ein Protokoll, nicht
  Phase F**: die Schnittstelle gibt es schon - die 16 `dev_*` von `fire_host` (`fire_pkg_abi.h`), die die Bibliothek von devices für "ein Gerät des Hosts"
  fragt (`fire_bridge_devices.h`); sie gehen nur über eine Verbindung statt über C#-Delegaten. Ohne das säße ein Programm in `fire` auf den seriellen
  Ports der Maschine, die spark (Terminal, Mitschnitt, Teilen) gerade offen hält, und sähe die Geräte der Treiber der Pakete nicht.
  * **Weg**: `FIRE_DEVICE_HOST=tcp:127.0.0.1:PORT;token=T` (setzt spark, `EditorDeviceService.ProgramHost`, ein Server je spark, gestartet mit dem ersten
    Programm). `fire` verbindet sich, wenn die Bibliotheken den Host zum ersten Mal bekommen (`vmPkgHost`), sagt hello mit dem Token und füllt dann die
    `dev_*`; ohne die Variable, ohne Antwort oder mit falschem Token bleiben sie NULL - die Bibliothek nimmt die Geräte der Maschine wie bisher.
  * **Protokoll** (binär, little-endian, eine Anfrage und ihre Antwort, eine Sperre im Programm): Anfrage `u32 n, u8 op, i32 a, Nutzlast`, Antwort
    `u32 n, i32 Ergebnis, Nutzlast`; op 0 hello (Token), 1 refresh .. 16 poll in der Reihenfolge der `dev_*` (Texte und Pakete in der Nutzlast; poll
    nennt seinen Platz, ein zu großes Paket bleibt liegen). Empfangenes wird nicht geschoben: die Bibliothek fragt mit poll (sie pollt ohnehin), der
    Server hält eine Warteschlange je Gerät und Verbindung (wie `PackageHost.Devices.cs` je Programm); endet die Verbindung, gehen ihre Haken vom Gerät,
    das Gerät bleibt beim Manager von spark (geteilt). TCP und nicht die Datei des Host-Displays: Geräte sind Anfrage/Antwort mit Texten und Paketen
    beliebiger Länge, und TCP geht auf Windows wie POSIX (die Sockets des Debug-Agenten, `fire_debug.h`).
  * **Kosten**: ein Aufruf ist ein Rundweg über localhost; das Testprogramm (Start von `fire`, Liste, Verbinden, zwei Echos) braucht 32-40 ms insgesamt.
  * **Offen**: die Live-Sitzung und die Vorschauen bekommen `FIRE_DEVICE_HOST` nicht (sie brauchen keine Geräte); `forge run` außerhalb von spark hat
    keinen Host (die Geräte der Maschine). Der Treiber-Agent läuft in `fire`; seine Geräte gehen weiter über seine Zeilen (siehe unten).
* **Treiber-Agent und Geräte**: der Treiber-Agent von spark (`PackageDriverAgent`, eine `LiveSession` ohne Fenster) läuft in `fire`: seine Treiber kommen als
  fire-Plugins, die die C-VM jetzt lädt ([Plugins](#plugins)); die Befehle über stdin (`__HostCommand`), die Antworten und der Verkehr der Geräte über stdout
  (`FIRE_PRINT_RECORDS`) - dieselben Zeilen wie vorher im Prozess. Die Geräte, die er macht, sieht der `DeviceManager` von spark über diese Zeilen (`AgentDriver`,
  `AgentDevice`). (`FIRE_DRIVER_AGENT=dotnet` ist mit M1 weg.) Scheitert er in `fire`, endet er mit dem Grund (`EndedWith`; einen Rückfall in die C#-VM gibt es nicht mehr). Ein Programm
  in `fire`, das spark startet, nimmt die Geräte von spark über das geteilte Gerät (oben).
* **Die Vorschauen in `fire`** (M1 (4), 22.11.2026): `UiPreview` (die Entwurfsansicht von FXML) und die Vorschau der Blockmodelle (`BlockPreviewWindow`,
  jetzt über `BlockPreview` in fire.Compiler) laufen als Kindprozess `fire` (`CVmScript`: übersetzt in spark mit `Linker { PackageLibraries }` wie die
  Live-Sitzung, die `.fbc` in einen Ordner unter `/dev/shm`, `fire run` mit `FIRE_PRINT_RECORDS`, stdin zu, eine Zeitgrenze, Abbruch beendet den Prozess).
  Ohne `fire` (kein C-Compiler) sagen beide, warum es kein Bild gibt (seit M1 kein Rückweg in die C#-VM).
  * **Entwurfsansicht**: das Fenster des Skripts ist der Host-Display einer `HostDisplayFile` (`FIRE_DISPLAY_SHM`); das Bild ist, was das Fenster dort
    zuletzt zeigte, die Orte der Elemente kommen als `print` (`@N X Y W H`). **Abweichung vom Plan** (eine Aufnahme in `fire_gfx_host.h` am Ende des
    Programms oder auf `@fb ID` hin): nicht nötig - `UiPreview` hängt an das erzeugte Skript `fxP.window.Idle()` an (ein halber Zyklus des Fensters:
    seine Ereignisse - es gibt keine -, sein Bild; kein onCycle, kein Handler), und der Weg der Bibliothek, ein Bild zu zeigen, legt es in die Datei. Kein
    neuer Code in der Bibliothek, kein Haken am `print` der VM, kein Raten der Nummer des Framebuffers am Ende. Das Bild gleicht dem der C#-VM Pixel für
    Pixel, die Orte sind dieselben (die Auswahl per Klick trifft dasselbe Element); `FireUiGenerator.GeneratePreview` ist unverändert.
  * **Blockvorschau**: das Skript schreibt die PNGs selbst (`frame.ToPng()`, `IO.File.WriteAllBytes`), es braucht keinen Bildschirm; die Bytes gleichen
    denen der C#-VM. Eine neuere Wahl (Animation, Renderer, Seite) beendet den Prozess der älteren (vorher lief die ältere in der C#-VM zu Ende).
  * **Latenz** (dieser Rechner, Debug-Build von spark, Median von 5 nach einem Aufwärmen; `FIRE_TESTS_ONLY=previews`): Entwurfsansicht nach einer
    Änderung **fire 604 ms** (übersetzen 588, davon die Programmdatei 16; der Prozess `fire` - Start, Laden, Zeichnen, Ende - **16 ms**) gegen **520 ms** in
    der C#-VM im Prozess (übersetzen 511, laufen 8). Fast alles ist das Übersetzen des Skripts mit der Bibliothek `ui` in spark (beide Wege; das ändert
    erst `forge serve`/M2 oder ein Zwischenstand der Bibliotheken); `fire` kostet rund 80 ms mehr, davon die Hälfte im Linker (`PackageLibraries`).
    Blockvorschau: ein Bild **241 ms** (übersetzen 214, Prozess 26) gegen 178 ms, 6 Bilder einer Animation 317 gegen 255 ms; der allererste Start auf
    einer Maschine baut einmal die Bibliothek von scene3d in den Cache (rund 12 s, danach nie wieder). Die geplanten "15 ms je Prozess" stimmen.
  * **Unter Xvfb mit spark** geprüft: eine FXML-Datei und ein Blockmodell offen; Entwurfsansicht nach Eingaben neu gezeichnet (`fire run
    /dev/shm/fire-script-...` als Kind von spark gesehen), Klick auf einen Button im Bild wählt ihn (Eigenschaften, Zeile im Text); Vorschau der Blöcke
    mit Ruhepose, anderer Seite und der Animation "hop" (16 Bilder). Gefunden (alt, nicht durch `fire`): öffnet spark eine FXML-Datei beim allerersten
    Start, bevor die Standardpakete installiert sind, zeigt die Entwurfsansicht "fire-graphics is not installed" bis zur nächsten Änderung.

**Tests**: `FIRE_TESTS_ONLY=cvmrun` (`CVmRunTests.cs`: ein falsches `fire` - Befehlszeile, Umgebung, Eingabe, beide Ausgaben, Exitcode, Stop, ein Programm, das
sich nicht verbindet -, das echte forge und `fire`: der Bericht von `bytecode`, Overlay, Fehler, ein Konsolenprogramm, ein Fensterprogramm ohne Bildschirm,
das geteilte Gerät - Liste, Standardgerät, Verbinden, Echo und Befehl über `FIRE_DEVICE_HOST`, der Mitschnitt von spark, das Ende der Verbindung, ein falsches
Token und ohne Variable die Geräte der Maschine -, Debuggen mit engine vm, Scopes am Einstieg, Haltepunkt, Variablen, Schritt, Ende mit dem Code von
`terminate`); `FIRE_TESTS_ONLY=circuiteditor` (die Live-Sitzung läuft in `fire`, Bilder, Maus, Befehle, ein mehrzeiliges `print`, ein Programm, das in `fire`
scheitert, endet mit dem Grund - kein Rückfall -, die zehn Beispiel-Schaltpläne im Programm des Editors; dazu die Vorschauen);
`FIRE_TESTS_ONLY=previews` (`PreviewTests.cs`: Entwurfsansicht in `fire` - Größe, Orte, Style, ein zweites Bild gleich, Fehler, Latenz -, Blockvorschau -
ein Bild, eine Animation, ein zweites Bild dieselben PNG-Bytes, Abbruch, danach wieder -; seit M1 ohne den Vergleich mit der C#-VM); die Prüfungen der Entwurfsansicht unter `FIRE_TESTS_ONLY=ui`
(`FIRE_TESTS_FILTER=Entwurfsansicht`) laufen jetzt in `fire`; `drivers` und `packer` nach diesen Änderungen grün, `debugger`, `forgereport` und `native` mit
`FIRE_TESTS_FILTER=VM-Debugger` grün vor ihnen (nicht berührt). Unter Xvfb mit spark selbst: ein Konsolenprogramm (Ausgabe, Eingabe), ein Fensterprogramm (sein Fenster
aus `fire`, Stop), Debuggen mit einem Haltepunkt (Halt, Scope-Panel, F10), der Schaltplan-Editor (damals über den Rückfall; seit 22.11. ohne ihn - geprüft mit
der Live-Sitzung in `circuiteditor`), die Entwurfsansicht und die Blockvorschau (22.11., siehe oben).

## Lader

Gelesen wird alles, behalten: Namen (interniert: ein Name vergleicht sich über den Zeiger), Einheiten (die Tabelle der Datei wird beim Start die
Tabelle der Runtime, `unitsInit`; Zeile 0 ist "ohne Einheit"), Enums (`g_enums`), die eigenen Einheiten (`customUnits`: Texte, die eine Einheit
nennen, `g_textUnits`), Ressourcen (`g_resources`), die Package-Natives mit den Dateinamen ihrer Bibliotheken, die Externs (als C-Signatur),
Klassen mit Feldern, `fieldInfo` (Einheit, Art, Klasse, typisiertes Array, `arrayOfParam`, Zugriff, Attribute), die Meta-Daten der Reflection (`ClassMeta`:
Mitglieder mit Art, Typ, Zugriff, Parametern und Attributen; Basisnamen, Attribute und eigene Interfaces der Klasse), `thisReads` (was ein Getter von
`this` liest: Probes auf berechneten Properties), Statics, Methoden, Konstruktoren nach Arität und die zusätzlichen der Überladung nach Typ,
Destruktor; Funktionen mit Defaults, Zugriff, Masken (`refMask`, `captureCopyMask`), Überladungs-Deskriptoren (`paramTypes`), `paramNames`,
Typparametern und Spezialisierungen (`specArgs`, `specParamKinds`), der Selektor-Pfad eines Lambdas (`c => c.radius`); Chunks mit Code, Konstanten,
Einheiten, Funktionen, Handlern, Zeilentabellen.
Nach dem Lesen bekommt jede Funktion einer Klasse ihren `OwnerClass` (rekursiv: Lambdas, Defaults, Spezialisierungen).
Übersprungen: `receiverTypeArgs`, `keptMembers` (Stripping des nativen Backends), `graphicsNatives` (die VM hat ihre Reihenfolge selbst). Für den
Debugger behalten: die Namen der Locals (`DebugLocalNames`: Tiefe -1 nach dem Offset des `DeclareLocal`, Tiefe 0 die Parameter), `firstUserSource`,
die Quellen (Bit 1 der Datei, [BYTECODE.md](BYTECODE.md#48-das-dateiformat-fbc)), die Liste aller Chunks (die Zeilen mit Code).

## Interpreter

**Werte und Besitz**: die Funktionen, die der Generator für jeden Opcode ruft (`CppGenerator.Step` ist die Liste), mit den schnellen Pfaden inline.
Ein Scope hat dynamische Slots (`n` deklariert, `hi` benutzt - ein Global, das vor seiner Deklaration geschrieben wird, bekommt seinen Wert bei der
Deklaration) und zwei `OwnList`s (`own` mit der Marke des Pools, `temp` für die Anweisung); `own.parent` ist der Scope außen (TakeUpwards). Ein String
oder Lambda, das aus einem Feld, einem Global, einem Static oder einem Element gelesen wird, bekommt einen Zähler im Pool der Anweisung (`holdTemp`,
wie der Generator für "geborgte" Slots), damit Code dazwischen es nicht freigibt.

**Aufrufe**: Frames auf einem eigenen Stapel, kein C-Rekursion für Aufrufe aus dem Bytecode. `vmPushCall` übernimmt die Argumente als Slots, wendet
`CopyArgs` an (Kopie, `ref`, Ergebnis eines Aufrufs, `take`, `try take`), wählt die Spezialisierung (`Add<int>`, `VM.SpecializationFor`) und füllt
Defaults (verschachtelt). Was ein Frame dem Aufrufer gibt, sagt `retMode`: das Ergebnis, einen anderen Wert (Setter, `SetIndex`: der zugewiesene Wert)
oder nichts. Methoden werden über den Namen gesucht (`Candidates` wie `RuntimeClass`: Basisklassen, überschriebene ausgelassen, exakte Arität vor
Defaults; bei Überladung nach Typ entscheiden die Werte, `Overloads.Score/Select`), mit einem Inline-Cache je Codeposition, wenn die Wahl nicht von
den Werten abhängt. `OverloadHint` wird übergangen (die Wertung findet dieselbe Überladung). Properties: ein fehlendes Feld ist `get_`/`set_`.
Operatoren einer Klasse (`operator+`, ...) für den linken Operanden, auch in den verschmolzenen `JumpIfNot*`. Methoden auf Basistypen: die
Erweiterungsklassen `$string`/`$char`/... des Preludes, dann die eingebauten (`ToString`, `ToBytes`, die Puffer-Methoden, die Besitz-Methoden).
`GetEnumerator` auf Array/Puffer/String baut den `ListEnumerator` des Preludes.

**Zugriff** (Debug/Release, nicht Performance): private/protected von Methoden, Feldern, Statics, Konstruktoren und Erweiterungen, mit den Texten
der C#-VM.

**Ausnahmen**: wie im Spike über den Handler-Stapel der Runtime (catch auf der Wurfstelle). Jede Klasse kann geworfen werden (Catch-Typen für alle
Klassen). Neu: `finally` wie in der C#-VM über den Operandenstapel (Nutzlast und Art der Vollendung: normal, Wurf, `return`, Sprung;
`EndFinally` macht weiter) - eine Ausnahme, die ein `try` mit `finally` passiert, landet dort (`UW_RETHROW`), ein `return` im `try` oder im `catch`
läuft durch das `finally`, was ein beendeter `catch` für sein `finally` auf den Stapel legt, reist mit seinem Sprung.

**Typen**: `GuardLocal`/`GuardTop` (`guardSlot`), `DeclaredType` (Klassen, Interfaces, Alternativen, Einheiten-Bedingungen, Lambdas, Typparameter der
Klasse über die Typargumente des Objekts und die `baseTypeArgs` der Kette, Typparameter des Aufrufs) mit `FitsOrConvert` (eine Zahl für ein `T`, das
für eine Zahl steht) und Text für ein `T`, das für `string` steht. Typargumente: interniert über die Runtime (`internTArgsN`), die `TArg`s sind die
der VM (Text, keine Rückrufe), ihre Länge merkt sich die VM.

**Arrays**: Literale, `new`, Puffer, verschachtelte Arrays, typisierte Arrays (`typeArray`, `newTypedArray`), Arrays einer Klasse, eines Strings oder
eines Enums über `g_arrClasses` (siehe unten), Arrays nach einem Typargument (`T items[]`), Index-Zugriffe auf Objekte (`GetIndex`/`SetIndex`),
Zeiger (`PtrV`, `addressOfIndex`, mit `FIRE_PTRBOUNDS`).

**Records**: `==` und Hash nach Inhalt, unveränderlich nach der Konstruktion (`g_recInit` während des Konstruktors, `InitNext` für `with`), die
Prüfung am Ende des Konstruktors.

**Einheiten**: die Tabelle der Datei, die Einheiten des Programms mit Offset und Umrechnungsfunktionen, Einheiten-Forderungen als Text (auch die
"§"-kodierten Einheiten des Programms), `adaptUnit`, `coerceUnit`, `relabelUnit`, `stepOf`.

**Ende**: `terminate(code)` beendet das Skript mit dem Code; eine unbehandelte Ausnahme und ein `fatal` der Runtime beenden nur das Skript (L6).
Nach jedem Programm ist nichts mehr da (geprüft mit `--stats` für 60 Fälle in einem Prozess: 0 Handles, 0 Strings, 0 im Pool, 0 Klassen) - auch kein
Speicher der VM: die Tabelle der Einheiten der Runtime (`unitsInit` legte sie an; `vmUninstallUnits` gibt sie frei mit den Namen und den Texten der
Einheiten, die die Runtime selbst gemacht hat - `unitMul`, `unitDiv`, `unitInternText`; die Texte des Programms und seiner Plugins sind geliehen,
`g_vmUnitLent`), die Dimensionen, die ein Plugin hinzufügte (`g_dimNames`, `g_dimsAdded`), die Einheiten, die ein Text nennen darf. Vorher blieb die
Tabelle jedes Programms liegen (ein Leck je Programm, das nächste legte eine neue an), und `g_dimsAdded` eines Plugins galt für die folgenden Programme
weiter. **Geprüft mit LeakSanitizer**: die 582 Fälle von `native`, `smoke`, `vm-life` und `vm-shutdown` je zweimal in einem Prozess von `fire` mit ASan
gebaut - danach keine Meldung (vorher: 25 Fälle mit Lecks der Einheiten, 2 mit einem Lesen freigegebenen Speichers, siehe Trampoline); die Tests:
`FIRE_TESTS_ONLY=cvmrun` (`CVmRunTests.Memory`: acht Fälle - Einheiten, Arrays einer Klasse, Threads, JSON, data.memory, eine unbehandelte Ausnahme
in einem Thread - zweimal in einem Prozess, `CVmSanitized` baut `fire` mit `-fsanitize=address` aus den Quellen, die forge trägt) und
`FIRE_TESTS_ONLY=linkage FIRE_TESTS_FILTER=plugin` (das Programm mit Kit und Meter und das mit einem Plugin in einem Thread je zweimal in einem Prozess).

### Rückrufe ohne Kontext (VM_SPIKE, Punkt 1)

Gelöst mit den beiden Wegen, die der Spike vorschlug:

* **eigene Fassungen der VM**: `TArg.holds/adapt` bleiben leer - die VM prüft Typargumente mit `vmFitsDeclared`/`vmTypeArgOf` statt mit `fitsTArg`.
  `FireClassDesc.recEq/recHash/recCheck` sind generisch (die Klasse steht im Objekt).
* **ein fester Vorrat an Trampolinen**: `UnitConvFn` (16 Paare `vmUnitTo_k`/`vmUnitFrom_k`, je mit dem Index der Klasse der Umrechnung) und
  `ArrClass.fits/zero` (64 Paare, je mit dem `DeclaredType`-Text des Arrays). Reicht der Vorrat nicht, lehnt der Lader das Programm ab bzw. bleibt das
  Array ungetypt. (Die Namen der 64 Paare sind oktal - `vmAcf10` ist der Eintrag 8; bis zum 22.11. riefen die Einträge ab 8 den Text eines anderen
  Eintrags auf - ab 64 hinter der Tabelle: ein Array der neunten Klasse nahm Objekte jeder Klasse, nach einem Programm mit mehr Einträgen las das nächste
  einen freigegebenen Text. Korpus: `native/4450`.)

Aus Schritt 5 dazu: `ProbeRawSet/Get` (die Probes) haben eine **eigene Fassung der VM** - `vmProbedSet` schreibt und liest das Mitglied über seinen
Namen statt über die Rückrufe, die der Generator je Feld erzeugt; die Einträge (`ProbeNode`, `ProbeEntry`) und `probeRun` sind die der Runtime.
`RfClass`/`RfMember` (mit `make` und `attrs`) braucht die VM gar nicht: ihre Reflection beschreibt die Klassen aus den Meta-Daten der Datei
(`vm_reflect.c`), nicht aus den Tabellen eines nativen Builds. `FireLibThunk` (eine Funktion je Signatur, `Library.Get`) ersetzt ein Aufruf nach der
Signatur zur Laufzeit (`vm_lib.c`).

Noch offen von Punkt 1: `MsgFn` (Aktoren), `ThreadsCatch.fn` (B3).

## Breiten

`#intwidth 32|16`, `#floatwidth 32` und die Breite von `word` sind in der Runtime eine Konfiguration zur Übersetzungszeit (`FIRE_INT32`,
`FIRE_INT16`, `FIRE_FLOAT32`, `FIRE_WORD_BITS`) wie in einem nativen Build - `Value`, `Real`, `FIRE_INT_CUT` und die gepackten Arrays hängen daran.
Entschieden: **eine VM je Konfiguration aus denselben Quellen**, keine Laufzeit-Schalter in der Runtime (L1: der schnelle Pfad bleibt, wie er ist)
und keine Varianten unter Präfixen in einem Binary (die Runtime hat Daten mit externer Bindung, jede Variante bräuchte eigene Namen für alles). Die
Varianten heißen `fire-i32`, `fire-i16`, `fire-f32`, `fire-i32-f32`, `fire-i16-f32`; `fire` liest den Kopf des Programms und startet bei einem
Programm anderer Breiten die Variante neben sich (siehe [Das Programm fire](#das-programm-fire-schritt-7); `forge build-vm --variant`/`--all` baut sie,
`tests/run-cases-vm.sh` baut alle in seinen Cache). Die Breite von `word` ist in der VM **eine Einstellung zur Laufzeit** (`FIRE_WORD_DYNAMIC`: `FIRE_WORD_BITS`/`FIRE_WORD_MASK` sind `g_fireWordBits`/`g_fireWordMask`, `fireSetWordBits` beim Start eines Programms aus `wordWidth` seiner Datei: 16, 32 oder 64 - wie `Value.WordBits` der C#-VM; vorher rechnete die VM immer mit 64 Bit). Ein Wort liegt in 64 Bit, die Bits über der Breite sind 0; ein Puffer von Wörtern (`word a[]`) hat Elemente in der Breite des Programms - 2, 4 oder 8 Bytes (`FIRE_WORD_ELEM`, `bufWordGet`/`bufWordSet`), wie der native Build für dieses Ziel und die C#-VM (Breite/8); bis zum 23.11.2026 waren es immer 8 Bytes, sichtbar an `ToLittleEndian`/`ToBigEndian`, `ToString`, `ToUnicode` eines Wortpuffers (seine Bytes), und ein Element aus einem Array behielt Bits über der Breite (Json). Zu einer Bibliothek eines Pakets geht ein schmaler Wortpuffer als Kopie in 64-Bit-Wörtern (die ABI kennt nur `FIRE_WORDS` zu 64 Bit), die nach dem Aufruf zurückgeschrieben wird. Sichtbar ist das nur mit einer Programmdatei, die eine Breite von 16 oder 32 trägt - forge-c schreibt Programme für die VM immer mit 64 (die VM läuft nur auf dieser Maschine); `tests/vm-word-width.sh` setzt die Breite in der Datei von Hand und vergleicht mit dem nativen Build für Ziele mit `"wordWidth": 32` und `16` (Fall `native/2735`). Die Operationen auf Wörtern gehen ohnehin über `wordOp`/`WordV`, die schneiden; geprüft in der Gruppe `native` ("word in der Breite des Ziels": 16 und 32 Bit in `fire` wie nativ). Dazu in der VM: eine Deklaration
`int` (ohne `int[64]`/`int[32]`) und `x!int` schneiden ein breites int auf die Breite des Programms (`DeclaredKind.CutsWide`, `intProgram`), was ein Native
eines Pakets zurückgibt, behält seine 64 Bit (`IntNative`, wie `FIRE_INT_U`), Zeiger der VM in einem `Value` (der Proto eines Lambdas, die Argumente
eines Threads) sind ints mit 64 Bit (`VM_PTRV`).

## Natives

**Kern-Natives** (`natives.def`, vom Generator heute direkt gerufen): eine Tabelle Name -> Funktion (`g_vmCoreNatives`: `print`, `__StringCall`,
`__CharCall`, `__ResourceCall`, `__MathCall`, `__JsonCall`, `__EnvCall`, `__LibCall`, `__BitsCall`, `__array_copy`, `__type_arg`, `__SbCall`), beim Start
an die Indizes von `natives.def` gebunden; dieselben Funktionen der Runtime wie im erzeugten C (`stringCall`, `mathCall`, `envCall`, ...). Die Natives der
Threads, Tasks, Kanäle und Completions kommen mit Schritt 3 in dieselbe Tabelle.

**Natives der Importe**: hinter `natives.def` in der Reihenfolge, in der der Compiler sie registriert (`ImportedPreludes.Insert`): die von `graphics`,
dann `windows`, dann `reflection` (die Datei nennt sie nicht - ein Aufruf adressiert sie über den Index: `native/tables/bridges.def`, bis M1
von `forge tables` aus den C#-Bridges geschrieben, seitdem selbst die Quelle), dann die Natives der Pakete, die die
Datei mit ihren Bibliotheken nennt (`packageNatives`, `packageNativeLibraries`). Jeder wird beim Laden nach Namen gebunden: an eine Funktion der VM
(`g_vmHostNatives`: die Reflection und was ein Paket seinem Host lässt, `"host": true` - `Sleep`, `__time_unit_ticks`, `__DEVWaitFor`,
`__DEVWaitForString`) oder an seine Bibliothek, die beim ersten Aufruf geladen wird.

**Pakete über die C-ABI v1** (`vm_pkg.c`): wie die C#-VM - dieselben Bibliotheken, die `PackageLibrary` baut (`libfire_pkg_<paket>_<import>.so`, für
graphics und windows `libfire_gfx.so`). Gesucht werden sie in den Ordnern von `FIRE_PKG_PATH` (`:`, unter Windows `;`), dann neben der Programmdatei:
im Ordner selbst, in `Packages/<paket>/lib/<rid>/` (der Paketspeicher eines forge, dort baut `forge run` sie) und in `Prebuilt/<paket>/<rid>/` (was ein
Build von forge mitbringt). Eine Bibliothek bleibt für die folgenden Programme geladen; am Ende jedes Programms `fire_pkg_reset`. Werte gehen als
Daten hinüber (Zahlen, Text, Arrays kopiert - ein Text ohne Kopie, die UTF-16 der Runtime sind die der ABI -, Byte- und Wortpuffer per Referenz),
ein Objekt oder Zeiger ist die `ArgumentException` der C#-VM. Ein Fehler der Bibliothek (1) beendet das Programm mit dem Text, eine Ausnahme (2)
wird mit `fireError` geworfen (die Klasse des Programms, sonst das Ende mit ihrem Text). Über die ABI kommen nur die Klasse und der Text: `makeClassError`
(vm_core.c) baut die Ausnahme mit dem Konstruktor eines Arguments, eine Klasse ohne einen solchen (`IndexOutOfBoundsException`: Text, Index, Länge;
`UnitMismatchException`) mit dem Konstruktor der wenigsten Parameter, der Text zuerst, der Rest `undefined` - wie die C#-VM (`NativeFail`). Index und Länge,
die `indexError` der Runtime in den Text schreibt (`Array index 7 out of range (length 3).`), werden daraus zurückgelesen, wie sie der native Build hat
(`makeIndexError`). Vorher bekam eine solche Klasse gar keinen Konstruktor, ihr `message` blieb `undefined` (`caught: undefined`). Prüfung: `tests/package-native-errors.sh <forge>` - ein Paket
im eigenen Speicher, dessen Natives scheitern (`indexError`, `fireError` mit `UnitMismatchException` und mit `IndexOutOfBoundsException`), in der VM und
nativ - beide drucken dasselbe. Der native Build erzeugt `makeClassError` nach derselben Regel (`gen/cppgenerator2.c`, `fgErrorConstructor`: der Konstruktor
eines Parameters, sonst der mit den wenigsten, 2 bis 16; Index und Länge aus dem Text); bis zum 23.11.2026 ließ er eine Klasse ohne Konstruktor eines
Parameters aus, ein `fireError("UnitMismatchException", ...)` einer Native beendete das native Programm, statt eine fangbare Ausnahme zu werfen.

**Der Host** (`fire_host`): Konsole = die des Prozesses (`std_write` geht durch `stdout` der Runtime: die Reihenfolge mit `print` bleibt), Pfade und
Hosts sind alle erlaubt (wie die gepackte Runtime der C#-VM: sie hat keinen anderen Host), Geräte des Hosts keine (das devices-Paket nimmt seine
eigenen Treiber), `win_attach`/`win_detach` gehen an `libfire_gfx`. **Lambdas** (`FIRE_LAMBDA`): ein Handle je Lambda, das bis zum Ende des Programms
lebt; `call_lambda` auf dem Thread der VM läuft sofort, verschachtelt (`fire_cbCall`) - eine Ausnahme darin, während das Programm auf den Native wartet,
behält die VM und wirft sie am Aufruf des Natives, wenn er zurück ist (2, SPEC 8.1.4); wo niemand wartet (ein Dienst, ein Leerlauf), meldet sie sie.
Von einem anderen Thread wird der Aufruf (mit kopierten Argumenten) gemerkt und am nächsten sicheren Punkt ausgeführt - jeder Aufruf eines Paket-Natives,
jede Runde eines Wartens (`Sleep`, das Ende des Programms). **Dienste** (`request_service`): an denselben Punkten `fire_pkg_service` aller
Bibliotheken, die es haben; **Leerlauf** (`fire_pkg_idle`) in jeder Runde eines Wartens; **Halte** (`hold`): das Ende des Programms wartet, solange
eine Bibliothek es hält (offene Fenster), und bedient dabei, was ankommt. Ein Native, der nichts aus der gemeinsamen Domäne bekommt (keine Lambdas, keine Puffer der Globals) und dessen
Bibliothek keine Dienste/keinen Leerlauf hat, läuft ohne die GIL (wie die Threads der C#-VM ihre Natives gleichzeitig rufen); das Hauptprogramm ist
dann "weg": ein Thread, der eine Sektion will, nimmt sie sich selbst, einer nach dem anderen (`fire_vmGrantAway`, der einzige zweite Eingriff in die
Runtime hinter `FIRE_VM`, nur mit der GIL), und das Hauptprogramm wartet nach dem Native, bis diese Sektionen fertig sind. Ein Native, der die GIL behält
und lange lief (2 ms), gibt danach den wartenden Strängen ihre Runde (`fire_natEnd` eines nativen Builds). Die GIL ist eine Ticket-Sperre, deren Wartende
je auf der Bedingung des Platzes ihres Tickets warten (64 Plätze); eine Freigabe weckt nur den Platz des nächsten Tickets - mit einer Bedingung für alle
weckte jede Übergabe jeden wartenden Thread, bei Dutzenden fire-Threads ging die GIL in Futex-Aufrufen reihum (`native/3510`, 30 parallele Anfragen an
`Http.AppServer`: 0,87 s statt 0,15 s; die C#-VM 0,11 s). Das gilt auch für den Debug-Build eines nativen Programms (dieselbe GIL, `fire_rt.h`). Fenster laufen so wie in der C#-VM: `Window.Show()` startet den Manager
mit seinem eigenen Thread in der Bibliothek, dessen Zyklen kommen als Dienste (mit `FIRE_DISPLAY=headless` ohne Bildschirm).

**ABI v2**: die Kopfdatei ist Version 1, die Lambdas, `request_service` und `hold` sind Felder von `fire_host` (an `size` erkannt) - die VM bietet alle.
Eine ABI, in der die Bibliothek die `Value`s der VM direkt benutzt (Roadmap: "später"), gibt es noch nicht; die Pakete benutzen sie also nicht.

**Library und extern** (`vm_lib.c`): ein natives Programm erzeugt einen Thunk je Signatur, die VM ruft nach der Signatur, die sie zur Laufzeit liest:
ganze Zahlen (Zeiger, bool, char, int jeder Breite - auf 64 Bit erweitert) und Floats (ein `float[32]` als die Bits eines float in der unteren Hälfte des
Registers) werden getrennt gezählt, die Funktion wird als eine mit 8 ganzzahligen und 8 Float-Parametern gerufen. Das gilt für die Aufrufkonventionen,
in denen beide eigene Register der Reihe nach haben (System V x86-64, AArch64) - unter Windows x64 ist eine Signatur "nicht bekannt" (Get sagt es).
`extern` mit `#extern "lib"` geht denselben Weg (CallExtern); `inline c` läuft nur nativ - sein Aufruf ist wie in der C#-VM eine abfangbare
`ArgumentException` (ebenso ein `extern` ohne Bibliothek). Fire-Plugins
(`.fireplugin`, `__LibCall(1)`/`__LibCall(5)`): siehe [Plugins](#plugins).

## Plugins

`vm_plugin.c`, wie `VM.Plugins.cs` der C#-VM ([NATIVE_BACKEND.md](NATIVE_BACKEND.md#plugins-phase-d-b), `FIRE_PLUGIN_FORMAT` 7 nativ). `Library.Load` einer
`.fireplugin`-Datei - oder einer Bibliothek (`kit.so`, `.dll`, `.dylib`), neben der eine liegt - liest die Datei (`FIREPLG2`, Länge, der JSON-Kopf von
`PluginBytecode`, dahinter eine Programmdatei) und gibt einen Handle unter -1000 (`__LibCall(2)` gibt darauf 0: keine C-Funktionen); eine Bibliothek ohne
`.fireplugin` daneben ist eine C-Bibliothek wie bisher (die C-VM registriert keine nativen Plugins: ihr Code ist der eines nativen Programms mit `"shared"`;
dass `kit.so` geht, liegt an seinem Bytecode daneben - wie in der C#-VM). `__LibCall(5, handle)` verlinkt:

* **Prüfungen** in der Reihenfolge der C#-VM und mit ihren Texten (`The plugin 'Kit' cannot be loaded: ...`): ein Plugin dieses Namens (dieselben Klassen:
  dieselben Namen zurück - ein zweites `Load`), das Format, **die Schnittstellen der Module** (`Deps` gegen die Programmdatei: sie trägt seit diesem Schritt die
  Schnittstellen ihrer Library-Module, Bit 3 der Flags, [BYTECODE.md](BYTECODE.md#48-das-dateiformat-fbc), geschrieben von `ProgramFile.Write` und forge-c;
  eine Datei ohne sie wird nicht geprüft, wie ein gepacktes Programm der C#-VM), Klassen, die das Programm schon hat, Basisklassen, die es nicht hat.
* **Das Programm des Plugins** wird mit den Namen des Programms gelesen (`vmLoadWith`: dieselbe Intern-Tabelle - ein Name vergleicht sich über den Zeiger), seine
  Basisklassen sind die des Programms, seine Natives in C++ kommen aus den Bibliotheken neben der Datei (oder dem vollen Pfad, den ein Treiber-Plugin nennt;
  `vmPkgFind` nimmt einen vollen Pfad), die `CallNative` seines Codes bekommen die Nummern des Programms nach Namen (Platz für 256 weitere Natives, die Tabelle
  wandert nicht); Top-Level-Variablen, `extern`/versuchbare Natives: die Meldungen der C#-VM. `class extends`: die Methoden kommen an die Klassen des Programms
  (eine, die die Klasse schon hat: die Meldung), ein Basistyp, den das Programm nicht erweitert (`$int`), wird die Klasse des Plugins; danach vergisst die VM die
  gefundenen Kandidaten und Inline-Caches.
* **Einheiten und Enums** werden die des Programms: Dimensionen nach Namen in `g_dimNames` (neue in freie Plätze, `g_dimsAdded`), eine Einheit die des Programms
  mit denselben Dimensionen, Faktor, Offset, Symbol und denselben Klassen der Umrechnungsfunktionen (eine eigene des Plugins ist immer neu; ihre Klasse bekommt
  einen der 16 Plätze der Trampoline), Enums nach Namen (neue hinten an); die Konstanten seines Codes werden umgeschrieben; die Einheiten, die ein Text nennen
  darf, kommen zu `g_textUnits`. Die Texte seiner Einheiten sind die des Plugins (`vmUnitsLent`: die Tabelle gibt sie am Ende nicht frei).
* **Klassen**: die nächsten Ids (`vmRegisterMore`: die Tabellen wachsen als Kopien, die alten bleiben bis zum Ende des Programms - ein Thread liest
  `byId` jetzt über `vm->top`), dann die **statischen Initialisierer** in der Reihenfolge ihrer Abhängigkeiten (die `SetStaticFieldOnInit` des Top-Levels des
  Plugins, `StaticInitSequence`), mit einem eigenen Handler: eine Ausnahme ist `the initialiser of 'C.f' threw X.`; was sie anlegen, gehört dem **eigenen
  Scope des Plugins** ([PLUGINS.md](PLUGINS.md): `VmLinked.scope`, ein `OwnList`; `VmClass.plOwn` seiner Klassen zeigt darauf).
* **Der Scope des Plugins**: das Ergebnis eines Initialisierers und ein unbeanspruchter Wert, der in ein statisches Feld einer Klasse des Plugins geht (er
  liegt im globalen Scope, wohin sein `ClaimToScope 65535` ihn legte, oder hat keinen Besitzer), kommen hinein (`vmPlClaim`; aus einem Thread in einer
  Sektion wie `x.TakeGlobal()`, im `SetStaticField` eines Threads `toMainGlobals` mit dem Scope als Ziel); mit Threads gehört er zur Domäne der Globals
  (`markOwnedShared`; beim ersten `fire` `vmPluginsShare`). `lib.Unload()` (`__LibCall(8)`, `vmPluginUnload`) zerstört ihn (`destroyList`, die
  Destruktoren laufen) und setzt die statischen Felder seiner Klassen auf `undefined`, in einer Sektion; der Code bleibt; ein `Load` danach lässt die
  Initialisierer noch einmal laufen. Am Ende des Programms: nach dem, was der globale Scope besitzt, die Scopes der Plugins in der Reihenfolge ihres Ladens
  (`vmPluginsReleaseStatics`, solange der globale Scope noch da ist - ihre Destruktoren laufen als verschachtelte Aufrufe), dann die Globals.
* **Geladen in einem fire-Thread**: bis zum 22.11. gehörte, was die Initialisierer anlegten, dem Thread - **in allen drei Engines**: die C-VM adoptierte das
  Ergebnis in den globalen Scope, aber es hatte schon einen Besitzer (den globalen Scope des Threads, wohin sein `ClaimToScope` es legte), die C#-VM nahm
  `TakeGlobal(_globalScope)` mit dem globalen Scope des Threads, der native Build `adopt(r, g_globalOwn)` mit dem des Threads (thread-lokal); das Ende des
  Threads zerstörte die Objekte der statischen Felder, das Hauptprogramm und die anderen Threads bekamen die `DestroyedException`. Jetzt gehört es in allen
  drei dem Scope des Plugins (`vmPlStatics`, `VM.RunPluginStatics`, `fire_pl_static`/`fire_pl_claim` beider Generatoren). **Gleichzeitig geladen**: ein
  zweites `Library.Load` desselben Plugins, während ein anderer Thread seine Initialisierer laufen lässt, wartet auf sie (`VmLinked.initing`;
  `blockUntil`/`blockUntilServing`: das Hauptprogramm gewährt währenddessen die Sektionen und Jobs der Threads) - vorher gab es die Klassen mit statischen
  Feldern, die noch `undefined` waren (C-VM und nativ; nativ jetzt `g_pluginIniting` in `fireRegisterPlugin`); die C#-VM wartete schon (`lock`), aber ein
  Hauptprogramm, das dort wartet, ist jetzt „away" (`NativeAwayBegin`), sonst hätte der Thread auf eine Sektion gewartet, die es nie gewährt. Ein
  Initialisierer, der sein eigenes Plugin noch einmal lädt, wartet nicht auf sich.
* **Debuggen**: die eigenen Quellen des Plugins (`Sources`) bekommen die nächsten Indizes nach denen des Programms (ein Debug-Programm mit Quellen), seine
  Zeilentabellen werden umgeschrieben, die Quellen seiner Libraries 0; der Agent schickt `sources` und wartet auf die Haltepunkte (`fire_dbgVmSources` in
  `fire_debug.h` hinter `FIRE_VM`, dasselbe Ereignis wie `fire_dbgPlugin`); `fire_dbgClass` wächst mit den Klassen.
* **Ende**: der Code eines Plugins bleibt, solange sein Programm läuft (auch nach `lib.Unload()`, wie in der C#-VM und nativ); am Ende gehen sein Scope,
  sein Programm und die ersetzten Tabellen (`vmPluginsEnd`) - das nächste Programm im selben Prozess fängt ohne an.

Tests: `FIRE_TESTS_ONLY=linkage FIRE_TESTS_FILTER=plugin` hat zu jedem Fall der C#-VM den der C-VM (das Programm als `.fbc` in `fire run`, `CVmRun`): Kit
(Interface, Basisklasse, Reflection, Ausnahmen, Enum, statische Felder, `class extends`), `kit.so` mit dem Bytecode daneben, Meter (Einheiten, Natives in C++),
eine andere Schnittstelle von `Api`, zwei Plugins mit derselben Methode, JSON, Natives in C, gepackte Objekte über Module, Debuggen (`fire --debug`,
`NativeDebugTarget`, der Plan `DebuggerTests.PluginPlan`) - am 22.11. alle 43 Prüfungen richtig (14 davon die der C-VM; mit MinGW/Wine). Der Treiber-Agent
(`FIRE_TESTS_ONLY=drivers`, 10 von 10) läuft in `fire` (Plugin und `#import`, ohne Rückfall; ein Plugin gegen eine andere Schnittstelle von `devices` wird
gemeldet, der Agent startet mit `#import` neu). Korpus: `vm-rf/0190` (eine `.fireplugin`, die keine ist: dieselben Meldungen in beiden VMs). Die
Programmdateien beider forges mit den Schnittstellen der Module sind byte-gleich (Stichprobe des Korpus und ein Projekt mit Library).
Windows: `fire.exe` (MinGW) unter Wine lädt Kit und Kit2 wie unter Linux (von Hand geprüft; `cvmwin` prüft die VM unter Windows, aber keine Plugins).
Ein Plugin in einem fire-Thread: `PluginsInThreads` (TKit mit statischen Feldern - ein Objekt mit Destruktor, Listen, ein Text -, in einem Thread geladen,
nach dessen Ende vom Hauptprogramm und einem zweiten Thread benutzt, frische Objekte eines Threads in einer statischen Liste; TKit2 mit Initialisierern,
die warten, von einem Thread und dem Hauptprogramm gleichzeitig geladen; UKit mit `lib.Unload()`, einem zweiten `Unload`, einem neuen `Load`, dem `Unload`
einer C-Bibliothek und der Reihenfolge am Ende) - nativ, VM, C-VM und die C-VM zweimal in einem Prozess mit ASan dieselbe Ausgabe.
Ein Fall des Korpus geht dafür nicht: ein Plugin ist ein eigenes Projekt, das gebaut werden muss (die Fälle sind eine Datei).
Offen: Plugins in einem Test der C-VM unter Windows (`cvmwin` hat die übrige VM, [Windows](#windows-m1-7)).

## JSON

`#import "json"`: die Engine des nativen Builds (`fire_json.h`, dieselben Texte und Fehler wie `JsonMethods`) auf Tabellen, die die VM beim ersten
Aufruf aus dem Programm baut, so wie `CppGenerator.Json.cs` sie schreibt: je Klasse ihre Felder, wie JSON sie sieht (`JsonFieldsOf`: Basisklasse zuerst,
`[NonSerialized]` weggelassen, `[JsonName]`), dahinter die `defined`-Felder, die nicht nach JSON gehen (ein gelesenes Objekt hat ihre Nullen), Einheit,
typisiertes Array, Klasse der Elemente; die Flags (Aktor, `OnDeserialized`, `List`, `JsonObject`, nicht serialisierbar); die Plätze von `List` und
`JsonObject`; die Texte der Typargumente (die der VM sind Texte). Jede Klasse steht in der Tabelle (`"$type"` kann jede nennen). `$id`/`$ref`, Zyklen,
Einheiten, Enums, generische Klassen kommen damit von selbst.

## Reflection und Probes

`#import "reflection"` (`vm_reflect.c`): die Beschreibungen (`Type`, `Member`, Attribute) aus den Meta-Daten der Datei; Lesen, Schreiben, Aufrufen und
Erzeugen nach Namen auf den Wegen des Interpreters - Felder mit ihren Prüfungen (Zugriff, Einheit, Art, Typ, Record), Properties und Methoden als
verschachtelte Aufrufe (Überladung nach den Werten, Defaults, Spezialisierungen), `Reflect.New` mit dem Konstruktor (ein Record gilt währenddessen als im
Bau). Die Zugriffsprüfung sieht den Code, der die Bibliothek gerufen hat (der erste Frame, dessen Klasse keine der Bibliothek ist, `VC_REFLHELPER`).

Probes: die Runtime hält sie (`FIRE_REFLECTION` in der VM: `ProbeNode` je Objekt, Flag 2 am Objekt, freigegeben mit dem Objekt); ein `SetField` auf ein
Objekt mit Flag 2 geht durch `vmProbedSet` - `changing`-Handler (einer, der `false` gibt, bricht ab), das Schreiben, `changed`-Handler bei einem anderen
Wert, dann die Probes auf berechneten Properties, die das Mitglied lesen (`ComputedSources` aus `thisReads`). Die Opcodes `Probe`, `SilenceMember`,
`SilenceValue` und `Reflect.Probe`/`Silence` mit den Fehlern der C#-VM.

## Debugger (Schritt 6, `vm_debug.c`)

`fire x.fbc` mit `FIRE_DEBUG` (oder `fire --debug <spec> x.fbc`, dasselbe) spricht das [Debug-Protokoll nativer Programme](NATIVE_DEBUG_PROTOCOL.md):
`NativeDebugTarget` in spark debuggt ein Programm der VM wie ein natives (L4). Das Protokoll selbst ist **der Agent der Runtime** (`fire_debug.h`,
`FIRE_DEBUG`/`FIRE_DEBUG_AGENT` im Build von `fire`): Transporte tcp und stdio (gerahmt, mit der Eingabe des Programms), sein Thread, alle Befehle
(`breakpoints`, `continue`, `step`, `pause`, `threads`, `trace`, `scopes`, `globals`, `this`, `expand`, `set`, `resume`, `terminate`, `profile`, `detach`),
die Werte (V, `ref`-Pfade), `set` (durch den angehaltenen Thread selbst, ein Feld über `vmReflWrite` mit Einheit, Art, Typ, Record, Probes), der Profiler,
die Threads (`thread`/`threadExited` über `threadEntry` der Runtime). Was ein natives Programm dafür erzeugt, liefert die VM: das `DbgProgram` (Quellen aus
der Datei, `firstUserSource`, Einstieg, Zeilen mit Code, die Globals), `fire_dbgClass` (die Felder nach `layout`) und `fire_dbgSetField`. `hello` sagt
`"engine": "vm"` (der einzige Eingriff in `fire_debug.h`, hinter `FIRE_VM`). Ohne `FIRE_DEBUG` kostet der Debugger eine Abfrage von `g_dbgArmed` je Instruktion.

**Halte**: ein Haken vor jeder Instruktion (`vmRun`, nur mit angeschlossenem Debugger) entscheidet wie die Schrittschleifen der C#-VM nach jeder
Instruktion: Position = `Chunk.GetLocation` der Instruktion (mit dem Bereich der Zeile gemerkt), Tiefe = die Frames der C#-VM; Haltepunkt beim Betreten
einer Zeile, `step line`/`into`/`out`, `pause`. **Verschachtelt** (unsichtbar wie in der C#-VM) ist ein Frame, den keine Aufruf-Instruktion des Frames
darunter gemacht hat (`Call`, `CallMethod`, `CallBaseMethod`, `CallStaticMethod`, `CallProtoWithThis`, `NewObject*`, `ConstructBase`) oder der
verschachtelt läuft (`FR_NESTED`): Getter, Setter, Operatoren, `GetIndex`/`SetIndex`, Destruktoren, `ToString`, Standardwerte, Lambdas eines Natives, die
Ausnahme, die die Runtime macht. Ein `catch`-Block (`FR_CATCH`, auf der Wurfstelle) ist der Frame seiner Funktion; die Frames der Wurfstelle darunter sind
für den Debugger weg. **Unbehandelt**: ein `throw`, den kein `catch` nähme (der Boden des Skripts zählt nicht, ein `finally` dazwischen auch nicht), hält
vor dem Wurf an; `resume` macht den `throw` zu `undefined`. Eine Ausnahme der Runtime (Index, Division, ...) hält im Hauptprogramm am Boden des Skripts an
(`vmUnhandledFn`, auf der Wurfstelle), in einem fire-Thread im Agenten der Runtime (`fire_dbgUnhandled`, mit den Frames, die `vmMakeException` vorher
festhält).

**Frames für den Agenten**: der Agent liest den Schattenstapel eines nativen Debug-Builds (`DbgFrame`). Die VM baut ihn, wenn ein Thread anhält: die
logischen Frames (innerster zuerst, Name wie `CppGenerator.DebugFunctionName`: `<main>`, `<fire>`, `<lambda>`, `Klasse.Methode`, `Klasse.construct`; ein
Aufrufer auf der Zeile des Aufrufs), die Scope-Kette des innersten als Ebenen (je Scope eine, `functionTop`, wo der Scope darüber der globale ist), ein
fire-Thread mit der Ebene seiner eigenen Globals (`T:`), `this`. Die Namen der Slots schreibt der Haken beim `DeclareLocal` in den Scope
(`VmScope.names`, wie `Scope.SetDebugName`), sonst die Parameter des Chunks, sonst `(local N)` bzw. `(global N)` - wie `VM.DebugScopeChain`/`DebugGlobals`.

**Tests**: `FIRE_TESTS_ONLY=native FIRE_TESTS_FILTER=VM-Debugger` (`NativeDebugTests.Vm.cs`): `fire` aus `native/vm/fire.c` gebaut, jeder Fall des nativen
Debuggers als `.fbc` (Modus Debug), über TCP gegen die C#-VM verglichen - Halte, Tiefen, Aufrufstapel, Scopes, `this`, Globals an jedem Halt, jeder
Step Into/Over bis zum Ende (auch durch den Prelude) -, dazu die Protokoll-Prüfungen der nativen Builds, die nicht das C betreffen (expand/set, gepackte
Felder, hello mit Quellen und SHA-256, detach, Profiler, pause/terminate, Threads), und ein Fall über stdio. Stand: alle 74 Prüfungen richtig (22 Fälle je Plan, Step Into und Step Over; 7 des Protokolls; stdio); die des nativen Debuggers unverändert. Ohne Debugger kostet der Haken in der Schleife im Rahmen der Messschwankung.

**Offen**: die Eingabe des Programms über den gerahmten Transport erreicht nur, was `fire_dbgStdinRead` liest (die Natives der Pakete lesen stdin
selbst); Aufgaben mit `await [...]` auf eigenen Threads melden sich als fire-Threads; ein Frame, der an
derselben Stelle des Stapels mit derselben Funktion und demselben (wiederverwendeten) Scope einen verschachtelten ersetzt, behält dessen Einordnung.

## Stand gegen den Korpus

`tests/run-cases-vm.sh` mit dem C#-forge, alle Ordner, mit den Varianten der Breiten (der Spike: 306; nach den Schritten 1 und 2: 751; Schritte 4/5
allein 869, Schritt 3 allein 910; nach dem Zusammenführen 1065; Schritt 7 allein 1073; 19.11.: 1126 von 1128). Stand 22.11.2026, vorher 1127 richtig,
17 falsch, 1 nicht gelaufen von 1145 - jetzt **alle 1149 richtig, keiner ausgelassen**:

| Ordner | richtig | falsch | | Ordner | richtig | falsch |
|---|---|---|---|---|---|---|
| native | 503 | 0 | | vm-gl | 27 | 0 |
| types | 275 | 0 | | vm-lq | 32 | 0 |
| syntax | 62 | 0 | | vm-perf | 20 | 0 |
| smoke | 58 | 0 | | vm-rf | 18 | 0 |
| diag | 38 | 0 | | vm-sc | 30 | 0 |
| trace | 16 | 0 | | vm-sc-checked | 6 | 0 |
| vm-arr, vm-arr-error | 9 | 0 | | vm-shutdown | 6 | 0 |
| vm-clone, vm-clone-error | 15 | 0 | | vm-life | 14 | 0 |
| vm-ext, vm-ext-error | 20 | 0 | | **zusammen** | **1149** | **0** |

Was die 17 waren: `types` 14 - die `.diagnostics` stammten von vor den Stack-Traces (Release druckt seitdem `   at <main> (datei:zeile)`, beide VMs
gleich; die Erwartungen nachgezogen); `native/3510` - die GIL (oben): 30 langsame Anfragen parallel brauchten 0,87 s statt unter 0,7 s;
`native/3210`, `4110` - **nicht** die Schrift von `libfire_gfx`, wie hier stand: die Läufer ließen das Aussehen des Desktops zu (`SystemTheme`: unter
GNOME die Schrift der Oberfläche, Cantarell über gsettings), das Testprogramm schaltet es ab (`FIRE_SYSTEM_THEME=off`, Program.cs); beide Läufer tun
das jetzt auch, dann geben C-VM, C#-VM und der native Build die `.expected` aus. Der eine nicht gelaufene (`vm-perf/0120`, die Warnungen eines
Debug-Laufs) läuft jetzt: `forge run` druckt sie auf stdout, und die C-VM hat sie (unten).

**Alle drei Modi**: zusätzlich jeder Fall (ohne `.error`) in Release, Debug und Performance gegen die C#-VM verglichen (`forge run -m` gegen `fire` auf
dem `.fbc` desselben Modus: Ausgabe, Exitcode, Fehlerausgabe ohne Warnungen; mit den Varianten der Breiten; Skript im Arbeitsordner, nicht
eingecheckt). Release: gleich, bis auf Fälle, deren Ausgabe sich von Lauf zu Lauf ändert (`native/4237`: zufällige Kennungen in der Fehlerausgabe).
Debug: vorher 6 Abweichungen (die Warnung für Verweise auf zerstörte Objekte fehlte - `native/0140`, `3800`, `4130`, `4238`, `vm-perf/0130`,
`diag/0300`), jetzt gleich. Performance: rund 40 Abweichungen, alle in Fällen, die ihre Modi ohne Performance angeben, und alle dort, wo
Performance die Prüfungen weglässt (Index, zerstörte Objekte, übergebene Objekte): die C-VM prüft weiter und wirft die Ausnahme, die C#-VM liest
ungeprüft - darunter ein Absturz des ganzen Prozesses mit `System.IndexOutOfRangeException` (`ScriptArray.GetUnchecked`, `ByteBuffer.SetUnchecked`),
statt einer Ausnahme von fire (z. B. `smoke/0140`, `trace/0020`, `native/0630`). Undefiniertes Verhalten nach SPEC, kein Fehler der C-VM; der
Absturz der C#-VM ist ihrer - **behoben** (22.11.2026): die C#-VM wirft in Performance bei einem Index außerhalb eines Arrays oder Puffers die
`IndexOutOfBoundsException` von Release wie die C-VM (ein Vergleich vor den `*Unchecked`-Zugriffen; `VmExecutionMode.Performance`), die drei Fälle
geben in beiden VMs dasselbe aus.

**Debug: Verweise, die ihr Objekt überleben** (`vm_dangle.c`, wie `DanglingWatch` der C#-VM): ein Objekt, das einem Aufruf gehört (ein Lokal, ein
mit `take` übergebenes Argument), stirbt mit ihm - auch wenn ein Feld oder ein Array nur einen Verweis darauf bekam. Im Modus Debug wird jedes
Speichern eines solchen Objekts in ein Feld (`SetField`) oder ein Element eines Arrays (`ArraySet`) vermerkt; stirbt es, werden die Halter, die noch
darauf zeigen und weiterleben, einmal je Stelle und Strang gemeldet (`warning (line N): an Item is destroyed at the end of the call it belonged to, but
the array in the field 'slots' of a Shelf still points to it (...)`, als Zeile der Ausgabe). Wem ein Objekt gehört: die Kette der Besitzer
(`OwnList.holder`) bis zu einer Liste ohne Halter - die eigene Liste eines Scopes (von `scNew` in `OwnList.depth` markiert, das die Runtime nicht
benutzt) oder dessen Temporäre (`home`); der globale Scope zählt nicht. Ein Halter "stirbt mit", wenn er schon zerstört ist oder einem Scope gehört, der
dieser oder einer darin ist (`OwnList.parent`). Die Zeile ist die der nächsten Instruktion (`VM.CurrentLocation`); was mit dem Wurfort nach einem
catch-Block stirbt, nennt die Stelle, an der der catch-Block verlassen wurde (die C-VM führt ihn auf den Frames des Wurforts aus, die C#-VM ohne sie).
Die Runtime ruft `fire_vmDangling` aus `destroy`/`destroyList`, solange etwas vermerkt ist (`g_vmDangling`, hinter `FIRE_VM`). Fälle `vm-life/0120`,
`0130` (je Thread), `0140` (nach catch), `vm-perf/0120`.

Mit ASan (`-fsanitize=address,undefined`): am 19.11. der ganze Korpus ohne Befund; am 22.11. die neuen Fälle und die Debug-Fälle mit Warnungen
(`native/0140`, `3800`, `4130`, `4238`, `vm-perf/0120`) ohne Befund.

Was die Fälle davor fehlen ließ (zum Nachlesen in den Commits): der Scope eines catch-Blocks stirbt nach den Scopes des Wurforts (wie
`DeferCatchRelease` des Generators); Zeiger auf Lokale halten die Slots am Leben (ein Scope mit `&x` gibt sie erst am Ende des Programms frei);
`Takes.Children` eines `IEnumerable` (`g_enumerateItems`); `TakeCheck`; `f(try take x)` und `try take` nur für das, was der Aufrufer besitzt (auf
oberster Ebene zählt der globale Scope); ein Feld mit typisiertem Array typisiert, was gespeichert wird, auch statische Felder und `Reflect.Set` prüfen
Art, Typ und Array (ein Helfer `vmFieldStore`); `&obj.n` eines `defined`-Felds ist ein `K_PtrField`; catch nach einem Interface; `resume` nach einem
gescheiterten Aufruf/Feld-Speichern; `SetIndex` übernimmt Schlüssel und Wert; `T items[]` für eine Klasse/einen String/ein Enum; `M<T>(x)` wählt eine
generische Überladung; Typargumente mit Einheit (`DeclaredType.UnitFits`); ein `SetField` auf ein Objekt mit Probes hält die Besitzregeln (die
Panels der UI); Kopien eingefangener Lambdas je Aufruf; ein eingebauter Methodenaufruf verbraucht ein `CopyArgs` (sonst gehörte das Array eines
`foreach` dem nächsten Konstruktor); `Sleep` endet mit `terminate`; die Breiten (siehe oben); Natives ohne GIL und Sektionen der Threads, während das
Hauptprogramm weg ist; `types/1880`, `1890`: die Diagnose des heutigen forge.
