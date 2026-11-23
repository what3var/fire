# fire einbetten: `fire_embed.h`, libforge, libfirevm (Phase D Schritt 7)

Status: **umgesetzt** (23.11.2026) für [ROADMAP_C_PORT.md](../ROADMAP_C_PORT.md#phase-d---forge-in-c) Phase D Schritt 7 und Entscheidung
[L6](../ROADMAP_C_PORT.md#l6-compiler-und-vm-sind-bibliotheken-und-packages) ("Einbetten: C-Schnittstelle"). Die Schnittstelle steht in
`native/abi/fire_embed.h`; die Packages darauf: [forge](../packages/forge.md), [script](../packages/script.md).

```
cc -O2 -DFIRE_FORGE_LIBRARY -c native/forge/forge.c -o libforge.o -lm -lpthread     libforge (statisch: ar rcs libforge.a libforge.o)
cc -O2 -DFIRE_VM_LIBRARY -c native/vm/fire.c -o libfirevm.o                           libfirevm (dazu -lm -lpthread -ldl)
forge-c build-embed <ordner>          beide als Shared Libraries (libfireforge.so, libfirevm.so; Windows fireforge.dll, firevm.dll; macOS .dylib) mit -fvisibility=hidden
cc -O2 native/forge/forge.c -o forge-c -lm -lpthread   und   cc -O2 native/vm/fire.c -o fire -lm -lpthread -ldl   (die Programme wie bisher)
```

Die Programme sind dünn über den Bibliotheken: `forge-c` ist `main` (die Ordner aus dem eigenen Pfad, `FIRE_TABLES`, `FIRE_PACKAGES`,
`FIRE_NATIVE_ROOT`) und `fcCliMain` - dieselbe Funktion, die die Bibliothek als `fire_forge_main` anbietet; `fire` ist `main` (Befehlszeile,
gepackte Programme, Varianten der Breiten) über `vmRunProgram`, das auch `fire_vm_run` nimmt. Ohne `FIRE_FORGE_LIBRARY`/`FIRE_VM_LIBRARY` ist jede
Datei, was sie war (dieselbe Ausgabe, dieselben Warnungen); mit dem Define fehlt `main`, und die Schnittstelle ist da.

## Die Schnittstelle

Reines C, undurchsichtige Handles und Getter (keine Strukturen, die Wirt und Bibliothek gleich halten müssten): ein Wirt kann die Bibliotheken
linken oder mit `dlopen`/`LoadLibrary` laden und die Funktionen nach Namen suchen (die Packages tun das). Texte sind UTF-8 mit NUL; was ein Getter
gibt, gehört dem Handle. `fire_embed_version()` ist 1.

| Funktion | |
|---|---|
| `fire_forge_new(tables, packages, native)` | ein Compiler: der Ordner mit `prelude.fire`, der Store der Pakete, `native/` (für die Bibliotheken der Pakete, die er baut); `NULL`: `FIRE_TABLES`, `FIRE_PACKAGES`, `FIRE_NATIVE_ROOT`, sonst neben der Bibliothek (`<ordner>/../tables` oder `<ordner>/native/tables` einer Distribution, `<ordner>/Packages`, `<tables>/..`) |
| `fire_forge_compile(f, path, source, length, mode)` | übersetzt wie `forge run` vor dem Start: eine Datei oder ein Projekt (`source` NULL) oder `source` als Text einer Datei `path`; die Bibliotheken der Pakete werden für die VM gebaut. `mode`: `FIRE_MODE_DEFAULT`/`DEBUG`/`RELEASE`/`PERFORMANCE` |
| `fire_compilation_ok`, `_program`, `_package_path` | ob es ein Programm gibt, die Programmdatei (die Bytes von `forge bytecode`), die Ordner ihrer Paketbibliotheken (wie `FIRE_PKG_PATH`) |
| `fire_compilation_diagnostic_count`, `_diagnostic(c, i, FIRE_DIAG_*)`, `_line`, `_column` | die Warnungen, wie sie kamen, dann die Fehler: Schwere, Code (`E4022`, `TC005`), Datei, Meldung und die Zeile, wie forge sie druckt (`Diagnostic.ToLogLine`). Was kein Übersetzungsfehler ist (ein Projekt, das nicht zu lesen ist, ein Paket, das nicht baut), ist ein Fehler ohne Code |
| `fire_forge_main(f, argc, argv)` | die Befehlszeile von forge im Prozess (für D8): schreibt auf stdout/stderr des Prozesses, gibt den Exitcode; `serve` lehnt sie ab |
| `fire_vm_new`, `fire_vm_set_package_path`, `fire_vm_set_output(vm, write, user, passThrough)` | eine VM: die Ordner der Paketbibliotheken (vor `FIRE_PKG_PATH`), die Ausgabe an einen Rückruf (`write(user, stream, data, n)`, Strom 1 stdout, 2 stderr), sonst gesammelt im Lauf, mit `passThrough` die des Prozesses |
| `fire_vm_run(vm, name, program, length, argc, argv)` | führt eine Programmdatei bis zum Ende aus (`argv` sind `Environment.Args`) |
| `fire_run_status`, `_exit_code`, `_text(r, FIRE_RUN_*)` | wie es endete (`FIRE_RUN_ENDED`, `_EXCEPTION`, `_ERROR`, `_TERMINATED`, `_NOT_LOADED`), der Exitcode wie bei `fire` (0, 1, der Code von `terminate`, 2), die Ausgabe, die Fehlerausgabe, Klasse, Meldung und Stack-Trace der Ausnahme, die niemand fing (bei einem Fehler der Runtime dessen Text als Meldung), warum das Programm nicht geladen wurde |

Was dazu in der VM nötig war (strukturell, ohne die Semantik zu ändern): `vm_embed.c` (neu); in `fire.c` das Define, die Umleitung von `stdout`/`stderr`
nur im Bau der Bibliothek (jedes `stdout`/`stderr` der Übersetzungseinheit - VM, Runtime, die Konsole der Paketbibliotheken über `fire_host` - nennt
die Ströme des Laufs: unter Linux `fopencookie`, BSD/macOS `funopen`, sonst eine temporäre Datei, die nach dem Lauf übergeben wird) und
`g_vmOutcome` (wie `vmRunProgram` endete); in `vm_core.c` der Aufruf `vmEmbedUnhandled` in `vmUnhandledFn`; in `vm_pkg.c` `g_vmPkgPathFirst` (die Ordner
des Wirts vor `FIRE_PKG_PATH`, statt `setenv`); in `fire_rt.h` hinter `FIRE_VM` `fire_vmNoteError` in `fatal` (der Text eines Fehlers der Runtime als
Daten, bevor er gedruckt wird). In forge: `embed.c` (neu), `main` geteilt in `main` und `fcCliMain`, `build/embedlib.c` (neu, `build-embed`), ein
Aufruf in `pjLink` (unten).

## Mehrere Instanzen, globaler Zustand, `exit`

* **forge**: Frontend, Compiler und Linker hängen am Kontext (`FcCtx`, L6 - so schon seit Phase C); jede Übersetzung hat ihren eigenen. Jeder
  `FireForge` ist unabhängig (seine Ordner). Was der Builder für den Prozess hält, wird für die Zeit einer Übersetzung aus dem `FireForge` gesetzt,
  unter einer Sperre (`feLock`): Übersetzungen laufen im Prozess nacheinander (die Bibliotheken der Pakete gehen in einen gemeinsamen Store).
* **VM**: die VM läuft auf der Runtime von fire (`fire_rt.h`, L1: dieselbe wie ein natives Programm), deren Zustand der des Prozesses ist (die
  Handle-Tabelle, die Klassen, die GIL, die Handler, ...). Ein `FireVm` ist unabhängig in seinen Einstellungen, aber es läuft ein Programm zur Zeit -
  ein zweites `fire_vm_run` (aus jedem Thread) wartet (`g_vmEmbedLock`). Nach jedem Lauf ist nichts mehr da (wie bei `fire a.fbc b.fbc`, VM_C.md
  "Ende"; geprüft mit LeakSanitizer in `cvmrun`). Die Shared Library `libfirevm` trägt eine eigene Runtime mit verborgenen Symbolen: in einem
  nativen Wirt (der seine eigene Runtime hat) und in der VM (deren Runtime die von `fire` ist) begegnen sich die beiden nie - darum kann ein
  fire-Programm in beiden Engines Skripte im eigenen Prozess laufen lassen.
* **`exit`**: forge ruft es nicht (L6). `_exit(127)` steht nur in den Kindern nach `fork`, `_exit` am Ende von `forge-c serve` (Threads, die nicht
  zurückkommen - `fire_forge_main` lehnt `serve` darum ab); `abort` bei einer Ausnahme ohne Handler (ein Fehler in forge) und ohne Speicher. Die VM
  beendet ein Skript über `exitJump` (`fatal`, `terminate`, eine Ausnahme), nicht den Prozess; `vmExecVariant` (`execv` der Variante anderer Breiten) gibt
  es nur im Programm `fire`, die Bibliothek sagt dann, welche VM das Programm braucht (`FIRE_RUN_NOT_LOADED`).

**Was im Prozess geteilt bleibt** (Bestand, 23.11.2026, `nm` über `forge.o`):

| Wo | Zustand | Warum er bleibt |
|---|---|---|
| `diag.c` | `fcOnReport`, `fcMonitorStepHook`, `fcBgItems`/`fcBgCount`/`fcBgNext`/`fcBgChanged` | Haken des Berichts der Befehlszeile (`--report json`, `FIRE_PROGRESS_LINES`); nur `tools/report.c` und `tools/commands.c` setzen sie, eine Übersetzung der Bibliothek lässt sie leer |
| `tools/report.c` | `frLock`, `frEnded`, `frProject`, `frLogged`, `frSeen`, `frCancel`, `frCancelJump`, `frPending` | der Bericht und sein Abbrechen (ein Signal) - der eine Befehl des Prozesses |
| `linker.c` | `fcProcessStackTrace` | `--stacktrace` der Befehlszeile, prozessweit wie `Linker.ProcessStackTrace` in C# |
| `typechecker4.c` | `fcLinkClassMeta` | während des Links eines Plugins gesetzt (`gen/native.c`, `build/plugin.c`); Kandidat für `FcCtx`, im Bereich des Generators nicht angefasst |
| `build/` | `nbBaseDirectory`, `nbNativeRoot`, `nvStoreRoot` | die Ordner (Toolchain, `native/`, Store) - in der Bibliothek je Übersetzung aus dem `FireForge` unter `feLock` |
| `build/` | `nbLinkageNote`, `nbMonitorLine`, `nbPlanNote`, `nbProjectBuildPlan`, `nbUnitOrderNatives`, `nlGraphicsWork`, `trPkgPath` | Haken und Zustand eines Builds/Tests der Befehlszeile |
| `build/cvm.c` | `nvKey` | Cache des Hashes der Quellen der VM (für den Prozess gültig: die Quellen ändern sich nicht) |
| `project/` | `pjExpandCtx`, `pjExpandRank` | Kontext eines `qsort`-Vergleichs (ohne `qsort_r`) - kurz und unter `feLock` |
| `project/` | `pjMonitorOut`, `pjOnWarning`, `pjProcessIntWidth`, `pjPublishPlanHook`, `pjPublishHookState`, `pjPublishSummary` | Haken und Optionen von build/publish der Befehlszeile |
| `native/vm`, `fire_rt.h` | `g_vmCur`, `g_slots`, `g_handlers`, die Klassen, ... | die Runtime des Prozesses (L1); ein Lauf zur Zeit |
| `vm_embed.c`, `fire.c`, `vm_pkg.c` | `g_vmEmbedNote`, `g_vmOut`/`g_vmErr`, `g_vmOutcome`, `g_vmPkgPathFirst` | der eine laufende Lauf, unter `g_vmEmbedLock` |

Die übrigen Daten in `forge.o` sind Tabellen, die nie geschrieben werden (Schlüsselwörter, Namen, Ziele, Schemata des Markups).

## libforge und libfirevm für die Packages (`build-embed`)

`forge-c build-embed <ordner>` baut beide mit dem optimierenden C-Compiler der Maschine (wie `fire`, `build/cvm.c`): `forge.c` mit
`FIRE_FORGE_LIBRARY` (gnu11), `fire.c` mit `FIRE_VM_LIBRARY` und der Runtime dieser Plattform (`nvWriteSources`), beide mit `-fvisibility=hidden` -
nur `fire_embed.h` verlässt die Bibliothek. Jede bekommt einen Stempel `.key` (SHA-256 der Dateien unter `native/forge`, `vm`, `base`, `tables`,
`runtime`, `abi`, `platform`); ein Ordner mit aktuellen gilt. Ein Programm, das `forge` oder `script` importiert, bekommt sie von selbst: `pjLink`
ruft `neEnsureFor` - neben forge-c, wenn sie dort aktuell sind, sonst im Cache der VM (`<LocalApplicationData>/fire/vm/<rid>/embed-<schlüssel>/`, beim
ersten Mal gebaut, rund 75 s) -, und forge-c gibt den Ordner seinen Programmen als `FIRE_EMBED_PATH` (in der VM wie nativ). Das C#-forge baut sie nicht,
reicht aber einen Ordner neben sich als `FIRE_EMBED_PATH` weiter. Die Brücken suchen in `FIRE_EMBED_PATH`, den Ordnern von `FIRE_PKG_PATH` und neben
dem laufenden Programm.

## Warum `script` im Prozess läuft und nicht als Kind `fire`

Beides ging; gewählt ist der Prozess, weil er die Schnittstelle von L6 selbst prüft (ein Wirt in C, der fire einbettet, ist genau `fire_vm_run`) und
die Ausnahme ohne Umweg als Daten ankommt (Klasse, Meldung, Trace aus `vmUnhandledFn`, nicht aus dem Text von stderr). Dass es in beiden Engines geht,
liegt an der eigenen Runtime der Shared Library. Die Grenzen: ein Skript zur Zeit; ein Skript, das abstürzt oder nicht endet, hält den Wirt (kein
Abbruch von außen); Werte gehen als Text und Bytes hinüber, nicht als Objekte. Ein Skript, das die Runtime des Wirts teilt (L6 "Im nativen Wirt
teilt das Skript dessen Runtime") und Objekte ohne Kopie übergibt, bleibt der eigene Entwurf, den der Plan vorsieht. Ein Kindprozess `fire` wäre
der Weg für Abbruch und Zeitlimit.

**Die Natives der Packages sind Brücken, nicht die Quellen von forge.** Die Quellen von `native/forge` (107 Dateien in Unterordnern, drei Namen
doppelt: `command.c`, `json.c`, `stats.c`; Tabellen als X-Makros, `natives.def` zweimal eingebunden) passen nicht in den Mechanismus der Paket-Natives:
die Header eines Pakets werden nach dem Dateinamen flach abgelegt (`pkg/<import>/<name>`) bzw. eingesetzt, und ein eingesetzter Header kommt nur
einmal. Darum lädt die Brücke libforge bzw. libfirevm zur Laufzeit; im nativen Build ist das auch nötig, weil die Runtime der VM eine zweite,
anders konfigurierte Runtime ist, die nicht in dieselbe C-Datei wie das Programm passt.

## Prüfung

* `packages/forge/testing` (5 Tests) und `packages/script/testing` (6 Tests: ein Programm übersetzt und startet ein Skript, zwei Skripte nebeneinander
  ohne gemeinsamen Zustand, ein Übersetzungsfehler als Daten, eine Ausnahme des Skripts vom Wirt gefangen, `terminate` und ein Fehler der Runtime,
  ein Skript mit `#import "io"`) - `forge test` in der VM und `--engine native` (auch mit TinyCC), mit forge-c und dem C#-forge: alle richtig.
* Ein Wirt in C (beide Bibliotheken gelinkt): Übersetzen, Lauf mit Argumenten, Ausnahme mit Trace, `terminate(7)`, `#import "io"`, Ausgabe an
  einen Rückruf.
* forge-c gleich wie vorher: `tests/compare-frontend.sh ... fbc` 1305 von 1305 je Modus, `tests/compare-commands.sh` 113 von 115 (die zwei
  Dateien nativer Builds, die schon vorher abwichen), `FIRE_TESTS_ONLY=cvmrun` 26 von 26; `cc -Wall` 19 Warnungen wie vorher.

## Offen

* **D8** (forge mit einem Frontend in fire): `Forge.Compiler.Main(args)` gibt es (`fire_forge_main`), aber ein so gebautes forge braucht libforge
  als Shared Library neben sich. Für "baut sich selbst für andere Ziele" müssen die Quellen von forge als Natives in den Build - dafür braucht der
  Mechanismus der Paket-Natives Header mit Unterordnern (oder forge eine Amalgamation als eine Datei), und `fire_forge_main` schreibt noch auf die
  Ströme des Prozesses (kein Rückruf). Dazu die Toolchains und die Standard-Packages als Quellen im Ziel (siehe Plan).
* Eine Distribution (`forge-c package-dist`, Schritt 6) trägt `native/forge` nicht: dort kann forge libforge nicht selbst bauen - `package-dist` sollte
  `build-embed <ordner>` ausführen (die Bibliotheken neben forge) oder `native/forge` mitnehmen.
* Ein gepacktes Programm (`forge build --engine vm`) trägt libforge/libfirevm nicht im Payload; es findet sie über `FIRE_EMBED_PATH` oder daneben.
* `fcLinkClassMeta` in den Kontext; Übersetzungen nebeneinander (heute nacheinander unter `feLock`).
* Abbruch eines laufenden Skripts, Zeitlimit, stdin für ein Skript; Windows und macOS ungeprüft (`fopencookie` nur unter Linux; die Brücken laden mit
  `LoadLibraryExW`/`dlopen`).
* Der .NET-Wrapper über `fire_embed.h` (nach M3).
