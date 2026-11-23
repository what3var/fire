# fire einbetten: `fire_embed.h`, libforge, libfirevm (Phase D Schritte 7 und 8)

Status: **umgesetzt** (23.11.2026) für [ROADMAP_C_PORT.md](../ROADMAP_C_PORT.md#phase-d---forge-in-c) Phase D Schritt 7 und Entscheidung
[L6](../ROADMAP_C_PORT.md#l6-compiler-und-vm-sind-bibliotheken-und-packages) ("Einbetten: C-Schnittstelle"). Die Schnittstelle steht in
`native/abi/fire_embed.h`; die Packages darauf: [forge](../packages/forge.md), [script](../packages/script.md). Schritt 8 (23.11.2026, [unten](#phase-d-schritt-8-libforge-als-eine-datei-im-package-forge)):
libforge als eine C-Datei im Package `forge`, gelinkt statt geladen; `fire_forge_main` mit eigenen Strömen; `tools/forge.fire`.

```
cc -O2 -DFIRE_FORGE_LIBRARY -c native/forge/forge.c -o libforge.o -lm -lpthread     libforge (statisch: ar rcs libforge.a libforge.o)
cc -O2 -DFIRE_VM_LIBRARY -c native/vm/fire.c -o libfirevm.o                           libfirevm (dazu -lm -lpthread -ldl)
forge-c build-embed <ordner>          beide als Shared Libraries (libfireforge.so, libfirevm.so; Windows fireforge.dll, firevm.dll; macOS .dylib) mit -fvisibility=hidden
forge-c amalgamate <datei.c> [--check] libforge als eine C-Datei (packages/forge/native/libforge.c, Schritt 8)
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
| `fire_forge_main(f, argc, argv)` | die Befehlszeile von forge im Prozess (D8): schreibt auf die Ausgabe des `FireForge`, gibt den Exitcode; `serve` lehnt sie ab |
| `fire_forge_set_output(f, write, user)` | wohin `fire_forge_main` schreibt: an den Rückruf (`write(user, stream, data, n)`, Strom 1 stdout, 2 stderr - auch was die Programme von `forge run` schreiben), `NULL` (Vorgabe): stdout/stderr des Prozesses |
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
`runtime`, `abi`, `platform`); ein Ordner mit aktuellen gilt. Ein Programm, das `script` importiert (seit Schritt 8 nicht mehr `forge`, außer sein
Paket wurde ohne `units` installiert), bekommt sie von selbst: `pjLink`
ruft `neEnsureFor` - neben forge-c, wenn sie dort aktuell sind, sonst im Cache der VM (`<LocalApplicationData>/fire/vm/<rid>/embed-<schlüssel>/`, beim
ersten Mal gebaut, rund 75 s) -, und forge-c gibt den Ordner seinen Programmen als `FIRE_EMBED_PATH` (in der VM wie nativ). Das C#-forge baut sie nicht,
reicht aber einen Ordner neben sich als `FIRE_EMBED_PATH` weiter. Die Brücken suchen in `FIRE_EMBED_PATH`, den Ordnern von `FIRE_PKG_PATH`, neben ihrem
eigenen Modul (der Bibliothek des Pakets; seit Schritt 8) und neben dem laufenden Programm.

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
einmal. Darum lud die Brücke in Schritt 7 libforge bzw. libfirevm zur Laufzeit; im nativen Build ist das für libfirevm auch nötig, weil die Runtime der
VM eine zweite, anders konfigurierte Runtime ist, die nicht in dieselbe C-Datei wie das Programm passt. Seit Schritt 8 trägt das Package `forge` libforge
als eine Datei und linkt es (unten); `script` lädt libfirevm weiter.

## Prüfung

* `packages/forge/testing` (5 Tests) und `packages/script/testing` (6 Tests: ein Programm übersetzt und startet ein Skript, zwei Skripte nebeneinander
  ohne gemeinsamen Zustand, ein Übersetzungsfehler als Daten, eine Ausnahme des Skripts vom Wirt gefangen, `terminate` und ein Fehler der Runtime,
  ein Skript mit `#import "io"`) - `forge test` in der VM und `--engine native` (auch mit TinyCC), mit forge-c und dem C#-forge: alle richtig.
* Ein Wirt in C (beide Bibliotheken gelinkt): Übersetzen, Lauf mit Argumenten, Ausnahme mit Trace, `terminate(7)`, `#import "io"`, Ausgabe an
  einen Rückruf.
* forge-c gleich wie vorher: `tests/compare-frontend.sh ... fbc` 1305 von 1305 je Modus, `tests/compare-commands.sh` 113 von 115 (die zwei
  Dateien nativer Builds, die schon vorher abwichen), `FIRE_TESTS_ONLY=cvmrun` 26 von 26; `cc -Wall` 19 Warnungen wie vorher.

## Phase D Schritt 8: libforge als eine Datei im Package `forge`

Ein forge, das für ein anderes Ziel gebaut ist (`forge build tools/forge.fire --engine native -t <ziel>`), braucht libforge für dieses Ziel - im Programm.
Zwei Wege standen offen:

* **A** - der Mechanismus der Paket-Natives nimmt die Quellen von forge, wie sie sind: Header in Unterordnern, doppelte Dateinamen, `.def`-Dateien mehrfach
  eingesetzt, im nativen Build eine eigene Einheit mit verborgenen Symbolen. Das ändert das Ablegen und Einsetzen der Header (`pkg/<import>/<name>`,
  "ein eingesetzter Header kommt nur einmal" - eine Regel, auf die sich die anderen Pakete verlassen), in C# (`src/fire.Native`, `PackageLibrary`) und in C
  gleich (byte-gleich, `tests/compare-frontend.sh native`), dazu das Packen der Pakete (`.fpk` legt flach ab) und ember.
* **B** - eine Amalgamation: `forge-c amalgamate` setzt `native/forge/forge.c` mit allem, was es einbindet, zu **einer** C-Datei zusammen
  (`packages/forge/native/libforge.c`, 4,3 MB); das Package trägt sie als eine Datei.

**Gewählt ist B**, mit einer kleinen Erweiterung des Mechanismus: `native.units` - C-Dateien eines Pakets, die nicht in die Datei des Programms eingesetzt,
sondern **für sich übersetzt und dazugelinkt** werden (nativ für jedes Ziel, und in die Bibliothek des Pakets für die VM). Gründe:

* forge ist schon eine einzige Übersetzungseinheit (`forge.c` bindet alles ein); die Amalgamation ist genau das, was der Präprozessor sieht - kein
  zweiter Weg, die Quellen zu lesen, und nichts, was nur für forge gilt (Unterordner, doppelte Namen, X-Makros sind in der Datei aufgelöst: jedes
  `#include "..."` relativ zur einbindenden Datei, jedes Mal eingesetzt, wie es der Präprozessor tut). `#include <...>` bleibt.
* Eine eigene Einheit: die Makros, `static`-Funktionen und Systemheader von forge (unter Windows `win32.h`) treffen die des Programms nicht; nach außen
  sichtbar sind nur `fire_embed.h` und Namen mit `fc`/`fb` (keine in der Runtime). Mit A hätte es dafür ohnehin eine eigene Einheit gebraucht.
* Die Regeln der Header und das Ablegen der Pakete bleiben, wie sie sind: keine Änderung an C# (die M3 ohnehin entfernt), keine an `.fpk`, keine an
  den anderen 90 Paketen; `units` ist eine neue Liste neben `headers` (forge-c liest sie aus `.fireproj` und `package.json`, schreibt sie in das Manifest
  nur, wenn es welche gibt - jedes andere Manifest bleibt byte-gleich).
* Die Datei hängt nur von den Quellen ab (keine Zeiten, keine absoluten Pfade); `forge-c amalgamate <datei> --check` sagt, ob sie aktuell ist.

Der Preis: eine erzeugte Datei im Repository, die nach jeder Änderung unter `native/forge` (und `native/base`, `abi`, `tables`, dem Deflater von
`packages/compression`) neu geschrieben werden muss - `--check` findet das. **libfirevm** bleibt eine Shared Library: ihre Runtime hat dieselben
externen Namen wie die Runtime des Programms (`g_slots`, `g_enums`, ...), zwei davon in einem Programm verbieten sich ohne Werkzeuge, die nicht jeder
Compiler hat (`objcopy --localize-hidden`; tcc, MSVC nicht); ihr Weg bleibt `-fvisibility=hidden` in einer eigenen Bibliothek.

**Wie es gebaut wird** (`build/builder.c`, `build/pkglib.c`): für jedes Paket des Programms mit `units` (in der Ordnung der Schlüssel) wird die Datei
neben das Programm kopiert (`fire_unit_<import>.c`), mit dem C-Compiler und den Optionen des Programms ohne Warnungen (`-w`) zu einem Objekt übersetzt -
mit `--quick` mit der Optimierung der Toolchain, wie die Natives der Pakete - und dazugelinkt; jede Einheit des Programms bekommt
`-DFIRE_PKG_UNITS_<IMPORT>`. Die Bibliothek des Pakets für die VM bekommt das Objekt (`-fPIC`, `-fvisibility=hidden`) und den Define ebenso; ihr Stand
(`Locate`) nimmt die Zeit der Einheit, der Hash von `Prebuilt/` ihre Bytes dazu. Das C#-forge kennt `units` nicht: es lässt den Define weg, und die
Brücke lädt libforge dann wie in Schritt 7 (`build-embed`). Die Brücke von `forge` ruft mit dem Define die Funktionen von `fire_embed.h` direkt.
Mit Open Watcom und `custom` wird ein Paket mit `units` abgelehnt; linkage `static`/`shared`/`plugin` nehmen `units` nicht (offen).

**Die Ströme von `fire_forge_main`** (`fire_forge_set_output`): in der Bibliothek nennt jedes `stdout`/`stderr` von forge die Ströme des Aufrufs
(`g_feOut`, `g_feErr` in `forge.c`; `printf`, `puts`, `putchar` schreiben auf `stdout`) - unter Linux mit `fopencookie`, BSD/macOS `funopen`, sonst eine
temporäre Datei, die am Ende übergeben wird (wie bei der VM). Ein Programm, das `forge run` startet, schreibt dann über Pipes dorthin (`tcRunInVm`;
unter Windows am Ende). Das Package: `Forge.Compiler.Main(args)` schreibt wie bisher auf die Ausgabe des Programms, `Forge.Compiler.Run(args)` sammelt
(`Forge.CommandResult`: `ExitCode`, `Output`, `Errors`).

**Was mitgeht:** ein gepacktes Programm der VM (`forge build --engine vm`), das `script` importiert, trägt libfirevm im Payload (mit den Bibliotheken der
Pakete, `nvEnsureLibraries`); die Brücken suchen auch im Ordner ihres eigenen Moduls (der Bibliothek des Pakets - dort schreibt `fire` den Payload hin).
libforge braucht nichts daneben: es steckt in der Bibliothek des Pakets `forge`. `forge-c package-dist` baut libforge und libfirevm in den Ordner (mit den
Quellen des forge-c, das ihn macht - `native/forge` liegt nicht in der Distribution, und `forge.c` bindet den Deflater aus `packages/` ein), gestempelt mit
dem Schlüssel, den das forge des Ordners rechnet (`--no-embed`: nicht).

**`tools/forge.fire`**: `#import "forge"` und `terminate(Forge.Compiler.Main(Environment.Args))`. Prüfung (23.11.2026, Linux x64): nativ mit gcc und tcc
und als Programm der VM gebaut; `bytecode` über jeden 10. Fall von `tests/cases`, alle Projekte von `packages/` und ihre Tests (191 Dateien) byte-gleich
mit forge-c (Programmdatei, Ausgabe, Fehler, Exitcode) - mit allen drei; `tests/compare-commands.sh <forge-c> <forge aus fire>` (gcc): 112 von 113 Befehlen gleich - der eine baut beim ersten Mal die Variante der VM in den Cache, das Protokoll davon hat nur der erste;
Fixpunkt: das mit gcc gebaute forge baut `tools/forge.fire` wieder, mit `SOURCE_DATE_EPOCH` byte-gleich (ohne: nur `__DATE__`/`__TIME__` in
`fgGeneratorIdentity` und die Build-ID verschieden); für Windows vom gebauten forge mit MinGW (`x86_64-w64-mingw32-gcc`, nur KERNEL32 und msvcrt) gebaut: unter Wine `bytecode` über jeden 30. Fall und jede 3. Testdatei der Pakete (46) gleich wie `forge-c.exe` unter Wine, die Programmdateien gleich wie unter Linux. Dafür setzt `fire_forge_main` unter Windows stdout/stderr für die Zeit des Aufrufs binär (wie `main` von forge-c), sonst kämen CR vor LF.
`packages/forge/testing` (6 Tests, neu: die Befehlszeile mit gesammelter Ausgabe) und `packages/script/testing` in der VM und nativ richtig.

## Offen

* `units` mit linkage `static`/`shared`/`plugin`, Open Watcom und `custom`; libfirevm für ein anderes Ziel (ein natives Programm, das `script` importiert,
  findet libfirevm nur auf der Maschine, für die forge sie baut: `FIRE_EMBED_PATH`, daneben); `script` für andere Ziele braucht libfirevm für das Ziel.
* `libforge.c` nach jeder Änderung an forge neu schreiben (`forge-c amalgamate packages/forge/native/libforge.c`); ein Test, der `--check` ausführt.
* `fcLinkClassMeta` in den Kontext; Übersetzungen nebeneinander (heute nacheinander unter `feLock`).
* Abbruch eines laufenden Skripts, Zeitlimit, stdin für ein Skript; `script` unter Windows und macOS ungeprüft (`fopencookie` nur unter Linux; die
  Brücke lädt mit `LoadLibraryExW`/`dlopen`).
* Der .NET-Wrapper über `fire_embed.h` (nach M3).
