# Das C des Programms in mehreren Dateien (Konzept)

Status: **Konzept, nicht umgesetzt.** Ein natives Programm mit linkage `source` (der Standard) ist heute **eine** C-Datei, `fire_program.c`
([Natives Backend (AOT): fire -> C](NATIVE_BACKEND.md)). Dieses Dokument beschreibt, wie der Generator sie in mehrere Übersetzungseinheiten teilen könnte, damit
der C-Compiler sie auf mehreren Kernen und, bei kleinen Änderungen, nur zum Teil neu übersetzt. Die Messungen unten sind ein Versuch mit dem C des Assistenten,
der dafür von Hand (mit einem Skript) geteilt wurde; der Generator kann es nicht.

## 1. Ziel und was es bringt

### Ausgangslage

Der Assistent (`src/fire.Editor/Assistant/assistant.fire`, mit ai, ui, windows, http, mcp) ist das größte Programm, das wir nativ bauen
(NATIVE_BACKEND.md "Bauzeiten (`FIRE_BUILD_TIMES`) und der Cache der Programme"):

| | |
|---|---|
| C des Programms | 417 655 Zeilen, 15,8 MB, 1708 fire-Funktionen, ~29 000 C-Funktionen (heute: 423 176 Zeilen, 16,0 MB, 1722 Funktionen) |
| Frontend / C-Generator | 2,3 s / ~19-25 s (13 Runden) |
| gcc -O2, eine Einheit, ein Kern | 225-270 s (in diesem Versuch 218 s) |
| gcc -O0 / -O1 / TinyCC | ~36-39 s / 142 s / 1,0 s |
| gcc -O2 `-flto=4` auf die eine Datei | 146 s |

gcc verteilt seine Zeit über alle Funktionen (die 40 größten sind 24 %): es ist die **Menge** des C, keine einzelne Funktion. Eine Einheit läuft auf einem Kern;
die anderen drei warten. Daran ändert der Cache der Programme (`NativeBuilder.ProgramCache`) nichts - er hilft nur, wenn sich *nichts* geändert hat.

### Ziele

1. **Nebeneinander übersetzen**: N Einheiten auf N Kernen. Erwartung aus den Messungen (siehe Versuch): mit 4 Kernen statt ~220-270 s etwa 60-70 s bei -O2,
   bei -O0 statt ~37 s etwa 10-15 s. Der grobe Versuch (ohne die Arbeit am Generator) kam schon auf 89 s bzw. 23 s.
2. **Nur übersetzen, was sich geändert hat**: ein Cache der **Objektdateien**, Schlüssel = SHA-256 über den Text der Einheit, den Text des gemeinsamen Kopfs,
   die Runtime-Dateien, den Befehl (die Ordner neutral, wie `ProgramCache.Key`) und den Compiler (Pfad, Größe, Zeit: `ProgramCache.Stamp`). Eine Änderung an einer
   Methode übersetzt dann eine oder wenige Einheiten - wenn die Nummern des Generators stabil bleiben (siehe 5., das ist der eigentliche Aufwand).
3. **Nichts an der Bedeutung ändern**: dasselbe Programm, dieselbe Ausgabe, dieselben Fehler; Byte für Byte derselbe Text je Einheit für dieselbe Eingabe.

### Was nacheinander bleibt

* **Frontend und C-Generator** (~22-27 s beim Assistenten): die Runden des Generators sind ein Fixpunkt über das ganze Programm (`CppGenerator.Run`: Funktionen,
  Helfer, Strings, Typargumente registrieren sich gegenseitig). Das Teilen geschieht *nach* dem Generator; ihn zu parallelisieren ist ein eigenes Thema.
  Bei -O2 und 4 Kernen ist der Generator danach ein Viertel bis ein Drittel des ganzen Baus.
* **Das Binden**: alle Objektdateien zu einem Programm, ein Aufruf (`ld`/`lld`/`mold`, `link.exe`); für ~10-20 MB Objekte eine bis wenige Sekunden.
* **Die größte Einheit**: der kritische Pfad. Eine Einheit sollte nicht größer werden als ein Bruchteil des Ganzen (die 40 größten Funktionen sind zusammen 24 %
  der Zeit - einzeln klein genug).
* **Der feste Preis je Einheit**: jede Einheit liest `fire_rt.h` (14 400 Zeilen, alle Funktionen `FIRE_FN` = `static inline`) und den gemeinsamen Kopf.

### Der Versuch (C des Assistenten, von Hand geteilt)

Gemessen auf dem Rechner dieser Sitzung: 4 vCPUs, gcc 13.3, `-std=c11 -pthread -w -c` (nur übersetzen, nicht binden), mit wenig Last von außen.
Das C ist das des Assistenten von heute (423 176 Zeilen, 16,0 MB), erzeugt mit `forge native`.

**Wie geteilt** (ein Python-Skript, nicht im Repository): die Datei hat drei Teile -
die Natives in C der Packages und Bridges (Zeilen 1-20 845), die Tabellen, Strings, Prototypen und Helfer (bis Zeile 102 724) und die Rümpfe der 2138 C-Funktionen
der fire-Funktionen (`fN`, `fN_c`; bis Zeile 423 160), dann `fire_main`, `fire_init`, `main`. Die ersten beiden Teile wurden der **Kopf** (`fire_split.h`), den jede
Einheit einbindet; die Rümpfe wurden an den Zeilen `#line N "fire_program.c"` hinter jeder Funktion in N gleich große Stücke (nach Bytes) geschnitten - die Messung lief so mit 4 und mit 8 Einheiten; Einheit 0
bekommt den Rest der Datei und als einzige `FIRE_RT_IMPLEMENTATION`/`FIRE_JSON_IMPLEMENTATION`. Bei den Funktionen `fN` (Prototyp und Rumpf) wurde das `static`
gestrichen, sonst nichts. Grob ist das, weil der Kopf auch alle Helfer (Verteiler `call_`/`direct_`, Feld-Helfer, Reflection, `isof_`, ...) als `static`
**Definitionen** enthält: jede Einheit übersetzt die Helfer, die ihre Funktionen rufen, selbst noch einmal. Das ist eine **obere Grenze** für die Zeit je Einheit;
gebunden wurde nicht (mehrfach definierte Symbole des Kopfs wie `g_dimNames`, `fire_libThunk`, die Hooks `make…Error`).

Gemessen (Sekunden, Wand-Zeit; "parallel" mit `xargs -P4`, 4 Kerne):

| | eine Datei | Kopf allein | 4 Einheiten: je Einheit | 4 Einheiten parallel | 8 Einheiten: je Einheit | 8 Einheiten parallel (4 Kerne) |
|---|---:|---:|---:|---:|---:|---:|
| gcc -O2 | **218** | 2,9 | 71 / 73 / 77 / 89 (Summe 309) | **89** | 33-47, eine 62 (Summe 325) | 109 |
| gcc -O0 | **37** | 15,4 | 20 / 21 / 22 / 23 | **23** | 18-20 (Summe 150) | 39 |
| `.text` (-O2) | 9,1 MB | | Summe 13,2 MB (+45 %) | | Summe 15,1 MB (+65 %) | |

Was die Zahlen sagen:

* **-O2, 4 Einheiten: 218 s -> 89 s** (2,5-mal so schnell), obwohl die Einheiten zusammen 309 s statt 218 s rechnen. Die 91 s mehr sind die Kopien: eine Einheit
  (`unit1`) hat 1,3 MB Code ihrer fire-Funktionen, 0,7 MB Helfer (Verteiler, Reflection, Feld-Helfer) und 0,5 MB Runtime und Natives (darunter die Engine von `ai`,
  `fai_feed`, die Bridges von graphics und windows) - die letzten beiden hat jede Einheit, die sie ruft, noch einmal übersetzt. Der Kopf selbst kostet bei -O2 nur
  2,9 s (gcc verwirft `static` Funktionen, die nichts ruft).
* **8 Einheiten auf 4 Kernen waren langsamer (109 s)**: die Einheiten waren nach Bytes gleich groß, nicht nach Zeit (die letzte, mit großen Funktionen der Bridges
  und der UI, brauchte 62 s und begann zuletzt), und jede weitere Einheit bringt ihre Kopien mit. Eine Verteilung nach geschätzter Zeit und die größten zuerst
  (LPT) bringen 8 Einheiten auf etwa 325/4 ≈ 82 s.
* **-O0: 37 s -> 23 s**, aber der Kopf allein kostet 15,4 s: ohne Optimierung behält gcc alle `static` Funktionen und Tabellen (`-fno-toplevel-reorder`), also
  übersetzt jede Einheit **alle** Helfer und Natives des Kopfs (3,5 MB `.text`). Mit einem Kopf aus Deklarationen fällt das weg.

**Hochgerechnet auf das Konzept** (Kopf nur aus Deklarationen, Helfer und Natives je einmal, siehe 2.): bei -O2 rechnen die Einheiten zusammen etwa
218 s + N × 3-5 s (Kopf, Kopien der nicht eingebetteten Runtime-Funktionen), mit 4 Kernen also **~60-70 s**, mit 8 Kernen ~35-40 s (dann begrenzt die größte
Einheit). Bei -O0 sind die Rümpfe der fire-Funktionen 37 - 15 ≈ 21 s, die Helfer und Natives ~15 s: mit 4 Kernen **~10-15 s**, wenn auch die Einheit der Helfer
und Natives geteilt wird (sonst ist sie mit ~15 s der kritische Pfad). Dazu kommt, was nacheinander bleibt: Frontend und Generator (~22-27 s) und das Binden.
Der ganze Bau des Assistenten mit -O2 würde so von ~4 min auf ~1,5 min fallen, mit -O0 (heute der Bau des Assistenten in spark, `AssistantNative.BuildToolchain`)
von ~1 min auf ~40 s; den Rest hat dann der Generator.

## 2. Was geteilt wird und was jede Einheit für sich hat

```
fire_program.h        gemeinsamer Kopf: Konfiguration der Runtime, #include "fire_rt.h", Deklarationen des Programms
fire_program.c        Einheit 0: FIRE_RT_IMPLEMENTATION (Zustand der Runtime), die Daten des Programms, die Hooks der Runtime, fire_init, main
fire_program_h.c      die Helfer: Verteiler, Selektoren, Feld-Helfer (die nicht inline sind), Operatoren, `is of`, Typargumente, Reflection, JSON-Tabellen
fire_program_1.c ..   die Rümpfe der fire-Funktionen, je Einheit eine Gruppe
fire_natives_<imp>.c  die Natives in C eines Imports, der für sich steht (wie bei linkage "static")
fire_natives.cpp      die Natives in C++ (wie heute schon eine eigene Einheit)
```

**Der gemeinsame Kopf** (`fire_program.h`) ist, was heute vor dem ersten Rumpf steht, aber nur als **Deklaration**:

* `HeadDefines()` und `HeadTail()` (`CppGenerator.cs`): die Schalter der Runtime (`FIRE_EXCEPTIONS`, `FIRE_THREADS`, `FIRE_NDIMS`, `#define FIRE_DEBUG 1`, ...)
  - in jeder Einheit **dieselben**, sonst sähen die Einheiten verschiedene Layouts von `Value`, `Obj` und dem Zustand. Danach `#include "fire_rt.h"` (ohne
  `FIRE_RT_IMPLEMENTATION`; die Runtime ist dafür schon gebaut: eine Library von linkage `static` bindet sie genau so ein).
* Typen des Programms, wo es welche gibt: die C-Structs der gepackten Klassen und der C-Felder (`CppGenerator.Packed.cs`, `CppGenerator.CFields.cs`),
  die Signaturen der Thunks. Die Frames (`FR_fN`) und `HandlerInfo`/`HT_fN_k` einer Funktion bleiben bei ihrer Funktion.
* Die Prototypen der Funktionen (`FunctionPrototypes`, `CppGenerator.Modules.cs`): `Func.Signature`, die C-Rümpfe (`CSig`), die `_b`-Funktionen hinter den
  Wrappern eines Debug-Builds, die Thunks `fN_t` der Lambdas und ihre Standardwerte `fN_d`.
* `extern`-Deklarationen der Daten, die mehrere Einheiten brauchen: die Strings `K<i>` (`Str`), die Typargumente `ta_N`/`tl_N`/`tio_N`/`tah_N`
  (`TArgTables`), `kArrClasses`, die Globals `G<n>` und statischen Felder `SF<n>`, die Faktor-Caches `kUF_a_b`, die Locks `fire_pkgM`, die Tabellen der Einheiten,
  Enums, Ressourcen, Klassen-Deskriptoren, Debug-Tabellen (`kDbgF_n`, `kDbgProgram`, `CppGenerator.Debug.cs`).
* Die kleinen Helfer, die eingebettet werden sollen, als `static inline` **Definitionen** (`HelperInline`: `gf_`/`sf_`/`fp_`, `aget_g`/`aset_g`) - mit den
  Prototypen der Verteiler, die sie rufen, davor.

Ein **Kopf je Einheit** ist besser als ein gemeinsamer: der Generator weiß, welche Funktionen, Strings und Tabellen eine Einheit benutzt (er schreibt die Aufrufe),
und kann jeder Einheit genau deren Deklarationen voranstellen. Dann hängt der Text einer Einheit nur an dem, was sie benutzt - ein neuer String in einer anderen
Einheit ändert ihn nicht (für den Objekt-Cache, Phase 2, wichtig). Phase 1 kann mit einem gemeinsamen Kopf beginnen.

**Je Einheit**: die Rümpfe einer Gruppe von fire-Funktionen, mit allem, was nur zu ihnen gehört (Frames, Handler-Tabellen, Catch-Callbacks `C_fN_k`, die Zeilen-
und Variablentabellen eines Debug-Builds, wenn nur die Funktion sie nennt), und ihren spezialisierten Kopien (`SpecFunc`) und Lambdas.

**Wie gruppiert wird** (deterministisch, stabil bei kleinen Änderungen, ausgeglichen nach Größe):

1. Ein stabiler **Schlüssel je Funktion**: Modul (`LinkedProgram.SourceModules`: `core`, ein Import, eine Library des Projekts, das Programm), voller Name der
   Klasse, Member, Zahl der Argumente; eine Lambda oder Kopie hinter dem Schlüssel ihrer Funktion. Nicht die Nummer `fN` (sie hängt von der Reihenfolge der Runden
   ab, siehe 5.).
2. Sortiert nach dem Schlüssel, **inhaltsdefinierte Grenzen** (wie beim Chunking von rsync/restic): eine Einheit endet hinter einer Funktion, deren Hash des
   Schlüssels `mod M == 0` ist, sobald die Einheit eine Mindestgröße hat, spätestens bei einer Höchstgröße. Eine neue Funktion verschiebt so nur die Grenzen ihrer
   Einheit, nicht alle folgenden (bei Grenzen "nach je X KB" würde jede Einfügung alle späteren Einheiten ändern).
3. Ein Modul beginnt immer eine neue Einheit (die Grenze von `core`, `ui`, `ai`, ... ist ohnehin stabil und deckt sich mit linkage `static`, siehe 4.).
4. Die Zielgröße folgt aus der Zahl der Kerne *nicht*: sonst bekäme dasselbe Programm auf einem anderen Rechner andere Einheiten (und der Cache passte nicht).
   Eine feste Größe (etwa 1-2 MB C, beim Assistenten 8-16 Einheiten) gibt genug Einheiten für 4-8 Kerne; mehr Einheiten kosten mehr (jede liest die Runtime
   und den Kopf und übersetzt ihre Kopien der Runtime-Funktionen: ~3-5 s bei -O2). Gemessen wird nicht in Bytes, sondern in geschätzter Zeit (die Zahl der
   Instruktionen und Aufrufe einer Funktion; der Versuch hatte eine Einheit, die bei gleicher Größe doppelt so lange brauchte). Der Builder verteilt sie mit
   `Parallel.For` wie die Module bei `BuildStatic`, die größten zuerst.

**Die Natives der Packages** (heute am Anfang der Datei, `AppendStandardNatives` und die Schleife über `_packageImports` in `CppGenerator.Run`): ein Import, dessen
C für sich steht (`StaticLinkage.NativesStandAlone`: hash, compression, io, time, ...), bekommt eine **eigene Einheit**; graphics und windows (die Bridge von
windows benutzt die Framebuffer von graphics) und scene3d zusammen eine. Der Code des Programms ruft sie über die `fire_cn_<funktion>` (`SharedNativeThunks`,
`StaticLinkage.cs`), genau wie die Module von linkage `static` es tun - dieselbe Entscheidung, derselbe Code. Eine solche Einheit hängt nicht vom Programm ab,
nur von den Schaltern und dem Compiler: sie ist der beste Kandidat für den Cache (ein Programm mit `data.sqlite` hat 270 000 Zeilen SQLite-Amalgamation in
seinem C, die sich nie ändern). Die Natives in C++ sind schon heute eine eigene Einheit (`fire_natives.cpp`, `NativeBuilder.BuildCommands`).

## 3. Die Hindernisse und wie sie gelöst werden

### `static` überall

Heute ist alles, was der Generator schreibt, `static` - der Compiler sieht eine geschlossene Welt und darf jede Funktion einbetten, klonen, ihre Signatur ändern
(IPA-SRA) oder weglassen. Die Stellen:

| Was | Wo |
|---|---|
| fire-Funktionen, `fire_main`, `FireBody` | `Func.Signature`/`SignatureOf` (`CppGenerator.cs`, `static` außer `Export`), `ThunkSignature` |
| Thunks, Standardwerte, Catch-Callbacks | `FunctionPrototypes` (`CppGenerator.Modules.cs`) |
| Verteiler, Selektoren, `is of`, Operatoren | `HelperStorage` (`CppGenerator.Modules.cs`, immer `"static "`), `DispatcherSignature`, `msg_` (`CppGenerator.cs`) |
| Feld-Helfer, `aget_g`/`aset_g` | `HelperInline` (`"static inline "`) |
| Typargumente | `TArgTables`: `taa_`, `tah_`, `ta_`, `tl_`, `tio_` |
| Strings | `static const fire_char16 K<i>_d[]`, `static const Str K<i>` (in `Run`) |
| Globals, statische Felder, Caches | `static Value G<n>`, `SF<n>`, `kUF_`, `fire_pkgM`, `fire_shareGlobals` |
| Tabellen | `kUnitInit`, `kUnitTo`/`kUnitFrom`, `kTextUnits`, `kRes<n>`/`kResources`, `kEnumM<i>`/`kEnums`, `ArrClassTable` (`acf_`/`acz_`/`kArrClasses`) |
| Debug | `kDbgF_n`, `kDbgLv_n`, `kDbgProgram`, ... (`CppGenerator.Debug.cs`) |
| `Library.Get`, `inline c` | `fire_lt_k`, `fire_inl_<name>` (`InlineCFunctions`) |

**Lösung**: ein Makro statt des Worts, das der Kopf festlegt -

```c
#if FIRE_SPLIT
#  if (defined(__GNUC__) || defined(__clang__)) && !defined(_WIN32)
#    define FIRE_PS __attribute__((visibility("hidden")))   /* sichtbar im Programm, nicht exportiert (auch nicht mit -rdynamic) */
#  else
#    define FIRE_PS                                          /* msvc, MinGW, TinyCC, Watcom: ein Programm exportiert ohnehin nichts */
#  endif
#else
#  define FIRE_PS static                                     /* eine Datei: wie heute */
#endif
```

Der Generator schreibt `FIRE_PS` (Funktionen) bzw. `FIRE_PS extern`/`FIRE_PS` (Daten: Deklaration im Kopf, Definition in einer Einheit) nur bei Symbolen,
die **wirklich über Einheiten gehen**: er kennt den Aufrufgraphen. Eine Funktion, die nur in ihrer Einheit gerufen wird, bleibt `static` (der Compiler darf mit
ihr weiter alles). Darum gruppiert 2. nach Klasse: die meisten Aufrufe einer Methode sind in ihrer Klasse. Auf Windows ist `hidden` nicht nötig (ein `.exe`
exportiert nur `__declspec(dllexport)`); bei linkage `shared` auf Linux, wo das Programm mit `-rdynamic` exportiert, was die Libraries brauchen, hält `hidden`
alles andere draußen - das, was die Libraries brauchen (Hooks, Tabellen, Thunks), bleibt ohne `FIRE_PS`.

**Namen**: die Namen des Generators sind im Programm eindeutig (`f12`, `K3`, `G1`), aber kurz; solange sie `static` waren, konnten sie mit nichts kollidieren.
Über Einheiten hinweg teilen sie sich den Namensraum mit dem C der Packages, dem `inline c` des Programms und den Systembibliotheken. Darum bekommen die Symbole,
die über Einheiten gehen, einen Präfix (wie `Px` der Module, `CppGenerator.Modules.cs`): `fire__f12`, `fire__K3` (`fire__` mit zwei Unterstrichen ist für den
Generator reserviert; docs/packages/natives.md sagt es den Packages). Im Text bleibt es lesbar, wenn der Präfix ein Makro ist (`#define f12 fire__f12` im Kopf).

### Konstanten, die über ihre Adresse verglichen werden

Eine Konstante, die in zwei Einheiten je eine eigene Kopie hätte, hätte zwei Adressen. Wo die Adresse zählt:

* **Typargumente `ta_N`** (`TArgTables`, `TArgRef` in `CppGenerator.cs`): die Runtime interniert Listen von Typargumenten nach den Zeigern ihrer Elemente
  (`findTArgs`/`internTArgsN` in fire_rt.h: `e->a[i] == items[i]`); `List<int>` aus zwei Einheiten muss dieselbe Liste ergeben. Die JSON-Tabellen fragen
  `if (t == &ta_i)` (`CppGenerator.Json.cs`, `jsonTArgText`) und `kJsonTArgs` nennt `&ta_N`.
* **Listen `tl_N`** sind `TArgs` und werden in Objekten gespeichert (`objSetTArgs`); ein Vergleich über die Adresse ist heute nicht zu finden, aber sie sind
  dafür gemacht, als Zeiger weitergegeben zu werden - ebenfalls eine Definition.
* **Strings `K<i>`**: `sameRef` (fire_rt.h) vergleicht bei Strings die Adresse ("derselbe String, nicht derselbe Text"), `valueEquals` nimmt `x == y` als
  schnellen Weg. Zwei Kopien desselben Literals änderten `sameRef` (heute nur intern benutzt: `g_awaitReplyTo`, das Zurückschreiben eines Slots in
  `CppGenerator.cs`) und verdoppelten die Daten.
* Klassen-Deskriptoren, `kArrClasses`, die Debug-Tabellen: die Runtime hält Zeiger auf sie.

**Lösung**: jede dieser Konstanten hat **eine Definition** (in Einheit 0 bzw. der Daten-Einheit, `FIRE_PS const Str fire__K3 = ...`) und in den anderen eine
`extern`-Deklaration. Kein Interning zur Laufzeit, kein `__attribute__((weak))`/COMDAT (TinyCC und Watcom können das nicht verlässlich). Die Handler-Marken
`&H<t>` (`g_unwind.target == &H<t>`, `CppGenerator.cs`) sind lokale Variablen ihrer Funktion und davon nicht betroffen.

### Inline-Helfer, Runtime, Initialisierung

* Die Helfer mit `HelperInline` stehen als `static inline` im Kopf; was sie rufen (die Verteiler), als Prototyp davor. Die großen Helfer (Verteiler, Reflection
  `rf_*`, `targOf_`; beim Assistenten zusammen ~2 MB des C) stehen einmal in der Helfer-Einheit.
* Die Runtime ist schon mehrfach einbindbar: `FIRE_FN` ist `static inline`, ihr Zustand liegt in der Einheit mit `FIRE_RT_IMPLEMENTATION` (fire_debug.h und
  fire_json.h ebenso). Jede Einheit übersetzt die Runtime-Funktionen, die sie benutzt und nicht einbettet, noch einmal (siehe "Größe" in 4.).
* Die Hooks, die die Runtime vom Programm braucht (`RuntimeHooks`: `makeClassError`, `makeDivideError`, ...; die JSON-Tabellen `jsonClassAt`), sind schon heute
  nicht `static`; sie stehen in Einheit 0.
* **Initialisierung**: C hat keine dynamischen statischen Initialisierer; alle Tabellen sind konstante Initialisierer (auch über Einheiten: Adressen von `extern`-
  Symbolen löst der Linker auf). Was zur Laufzeit gesetzt wird (`_initCode`: `unitsInit`, `g_textUnits`, `g_resources`, `g_enums`, `g_arrClasses`, ...), bleibt
  in **einem** `fire_init` in Einheit 0 in der Reihenfolge von heute. Braucht später eine Einheit eigene Schritte, ruft `fire_init` ihre `fire_init_<k>()` in der
  Reihenfolge der Einheiten - nie der Linker oder Konstruktoren.

### Durchgänge über den ganzen Text

Einige Schritte des Generators arbeiten auf dem fertigen Text und setzen eine Datei voraus: `PointerCallsPass` (`CppGenerator.PointerCalls.cs`: Aufrufe und
Prototypen von `fN` zu `fN_p`), `HostTable(sb.ToString())` (linkage `shared` unter Windows: sucht Symbole im Text), `InsertAfterRuntime` (Plugins),
`ResolveLineResets` (die Zeilennummern der Datei). Der einfachste Weg: der Generator schreibt wie heute einen Text, mit **Marken** zwischen den Teilen
(`/*@unit k*/`); die Durchgänge laufen über den ganzen Text, *danach* verteilt ein letzter Schritt die Teile in die Dateien und schreibt die Deklarationen aus
einer Tabelle der Symbole. `ResolveLineResets` läuft zuletzt je Datei, mit ihrem Namen.

Auch `NativeBuilder` liest die Datei: `CompilerCommand` sucht `// fire-link: ...` (die Bibliotheken) und `#define FIRE_DEBUG 1` in den ersten 60 Zeilen
(Debug-Build) in `programFile` - das bleibt Einheit 0 (dort stehen beide).

### Zeilen, Debugger, Sanitizer

* **`#line`**: jede Einheit nennt sich selbst (`#line N "fire_program_3.c"`); die fire-Zeilen bleiben, wie sie sind. gdb/lldb finden `break main.fire:7` über
  alle Compile-Units (DWARF je Einheit). Der Test in `NativeDebugTests.cs`, der die Rücksprünge auf `"fire_program.c"` prüft, muss jede Einheit prüfen.
* **Der native Debugger** (NATIVE_DEBUG_PROTOCOL.md): der Agent und `kDbgProgram` (Quellen, Globals, Klassen, Zeilentabelle) stehen in Einheit 0; die Tabellen
  einer Funktion (`kDbgF_n`, Ebenen der Scopes) bei ihr, `extern`, wo `kDbgProgram` sie nennt. Der Schatten-Stack und die Haken sind Code in den Funktionen -
  nichts ändert sich. Ein Debug-Build (`-O0 -g`) ist der Fall, in dem der Objekt-Cache am meisten bringt (der Zyklus Ändern - F5).
  Debug-Informationen nennen die Ordner des Baus; für den Cache mit `-fdebug-prefix-map=<bauordner>=.` übersetzen (sonst den Ordner in den Schlüssel, wie
  `ProgramCache` es für Debug-Builds tut).
* **AddressSanitizer/UBSan**: dieselben Schalter für alle Einheiten und das Binden; nichts Besonderes. Die Tests "unter AddressSanitizer/UBSan" laufen mit
  `FIRE_SPLIT=1` mit.

### `inline c` und die C-Dateien der Packages

* **`inline c`** (docs/language/18-native-code.md): die Rümpfe werden `static` Funktionen `fire_inl_<name>`, die einander rufen dürfen (`fire_inl_Add(a, 1)`).
  Alle `inline c` des Programms bleiben **zusammen in einer Einheit** (die Helfer-Einheit oder eine eigene), mit `FIRE_PS`-Hüllen für die Aufrufer: der Code des
  Benutzers sieht eine Übersetzungseinheit wie heute.
* **Die C-Dateien der Packages** gehen von einer Datei aus: `PackageNativeText` stellt ihnen Makros voran (`#undef FIRE_INT_U`, `#define callLam fire_cbCall`),
  gemeinsame Header haben Include-Guards (`fire_fs_posix.h` kommt beim Assistenten für ai *und* io, die zweite Kopie ist durch `FIRE_FS_POSIX_H` leer), graphics
  und windows teilen Zustand. Darum entscheidet dieselbe Regel wie bei linkage `static` (`NativesStandAlone`): ein Import, der für sich steht, eine Einheit; alle
  anderen zusammen in einer. Steht ein gemeinsamer Header so in zwei Einheiten, sind seine Funktionen dort je `static` (sonst wäre er schon bei `static` falsch).

### Toolchains

| Toolchain | mehrere Einheiten | Bemerkung |
|---|---|---|
| gcc, clang (Linux, macOS) | ja | `-c` je Einheit nebeneinander, dann binden; lange Befehle über `@datei` |
| MinGW (auch von Linux aus) | ja | Windows begrenzt die Befehlszeile auf 32 KB: Antwortdatei (`@datei`) für das Binden |
| msvc | ja | `cl /c` je Einheit (oder `/MP`), `link`; Antwortdateien. Hier nicht zu testen |
| Emscripten (wasm) | ja | `emcc -c` je Einheit, dann `emcc` bindet (wie die Module von `static`) |
| ESP-IDF (`files`, Komponente) | ja | die Komponente listet alle Einheiten in `SRCS`; ninja von `idf.py` übersetzt nebeneinander - von selbst |
| FreeRTOS-Simulator, eigene Boards (`files`) | ja, wenn ihr Bausystem Dateien listet | |
| TinyCC | ja (kann es), aber **nicht nötig** | 1 s für das ganze Programm: bleibt eine Datei |
| Open Watcom (DOS, Windows 3.x) | grundsätzlich ja (`owcc` nimmt mehrere Dateien) | bleibt eine Datei: kleine Programme, Treiber mit eigener Befehlszeile (`WatcomToolchain`), ungetestet |
| `custom` | nein | die Vorlage kennt nur `{c}`; später ein `{units}` |

**Eine Datei bleibt der Rückfall** und für kleine Programme die Wahl: `"split": "auto"` (Standard, sobald umgesetzt) teilt nur mit gcc/clang/MinGW/msvc/emcc/
ESP-IDF und erst ab einer Größe (etwa 50 000 Zeilen C oder geschätzt > 10 s Übersetzen), `"split": false` nie, `"split": true` immer (in `fire.native.json`, im
Ziel oder der Toolchain; `FIRE_SPLIT=1` für die Tests).

## 4. Zusammenspiel mit dem Vorhandenen

### Der Cache der Programme

`NativeBuilder.ProgramCache` bleibt, wie er ist: sein Schlüssel ist der SHA-256 über jede Datei im Bauordner und die Befehle - mit Einheiten eben über mehr
Dateien und Befehle. Ein Bau ohne Änderung nimmt weiter das Programm aus dem Cache (31 s beim Assistenten, der Generator ist dann fast alles). Neu ist der
**Cache der Objekte** (Phase 2) unter `objects/` neben `programs/` (`FIRE_NATIVE_CACHE`, `ember cache clean`), mit einer Grenze der Größe (die ältesten gehen
zuerst); eine Objektdatei des Assistenten mit -O2 ist ~0,5-1 MB, alle zusammen ~15-20 MB je Variante. `FIRE_PROGRAM_CACHE=0` schaltet beide ab.
ccache wäre dasselbe von außen; wir können uns nicht darauf verlassen (Windows, ESP-IDF), es profitiert aber genauso von stabilen Einheiten.

### linkage `static` und die Module

Die Module von ROADMAP 9 **sind** schon eine Teilung - in mehrere Dateien (`<modul>.c`), nebeneinander übersetzt (`Parallel.For` in `NativeBuilder.BuildStatic`),
nach Inhalt im Cache, sogar über Programme hinweg. Aber sie teilen **nach Bedeutung**: jede Library sieht nur ihre Klassen, ruft fremde über die Tabellen der
Klassen-Deskriptoren, kein Inlining über Modulgrenzen, eine Library behält ihre ganze Schnittstelle. Das kostet Größe und etwas Tempo (der Assistent mit `static`:
24 s mit allen Modulen im Cache, aber 22 MB statt 10 MB). Die Teilung hier ist **rein physisch**: dieselbe geschlossene Welt, dasselbe C, nur in Stücken; der
Cache gilt nur für dieses Programm (seine Nummern sind seine).

Beides passt zusammen:

* Die **Grenzen** der Einheiten beginnen bei den Modulen (`SourceModules`, 2.3), die **Natives**, die für sich stehen, teilen dieselbe Entscheidung und dieselben
  Thunks (`NativesStandAlone`, `fire_cn_`), der **Builder** dieselbe Schleife (`Parallel.For`, `BuildMonitor.BeginCount`/`Count` für `CompileC`, die temporären
  Dateien eines abgebrochenen Baus).
* Mit linkage `static` bleibt vom Programm vor allem sein eigener Code und die Bridges von graphics/windows; ist das groß, teilt sich auch das Programm
  ("Module für die Libraries + geteiltes Programm").
* `FIRE_MODULES=1` (das Programm aus Modulen, in einer Datei) und `FIRE_SPLIT=1` (das Programm in Einheiten, ohne Module) sind die beiden Schalter, mit denen die
  native Testgruppe dieselben Ausgaben zeigen muss.

### LTO für Publish

`-flto=4` auf die eine Datei gab 146 s statt ~225 s: gcc teilt dabei selbst (WPA, dann LTRANS-Partitionen nebeneinander), aber Parsen und die frühen Optimierungen
bleiben auf einem Kern. Mit Einheiten laufen auch diese nebeneinander, und die LTO gibt zurück, was `hidden` statt `static` kostet: mit `-flto` und
`visibility("hidden")` sieht gcc wieder alle Aufrufer einer Funktion (wie bei `static`), clang mit `-flto=thin` ebenso, mit wenig Speicher. Vorschlag: der Bau für
den Alltag ohne LTO (Einheiten, -O2 oder für Debug -O0), **Publish** mit `-flto=auto` (gcc) bzw. `-flto=thin` (clang) und `-fuse-linker-plugin`, msvc mit `/GL` und
`/LTCG`. Die Zeit dafür ist zu messen; Erwartung: unter den 146 s der einen Datei.

### Größe

Das Weglassen (`EffectiveStripping`, Stufen 0-3, ROADMAP 11) geschieht im Generator, **bevor** geteilt wird: was kein Code erreicht, steht in keiner Einheit.
Was mehr wird:

* Runtime-Funktionen, die nicht eingebettet werden, und die `static inline` Helfer stehen in jeder Einheit, die sie benutzt, einmal (je Einheit eine Kopie).
  Gegenmittel: `-ffunction-sections -fdata-sections` beim Übersetzen und `--gc-sections` beim Binden (wie bei den Libraries von `static`,
  `CompilerCommand(sections: true)`), dazu `--icf=all` (lld, gold) bzw. `/OPT:ICF` (msvc), die gleiche Funktionen zusammenlegen; LTO legt sie ohnehin zusammen.
* Funktionen, die über Einheiten gerufen werden, kann gcc nicht mehr ganz einbetten und weglassen: eine Kopie bleibt.

Die Größe ist mit dem Versuch nur grob zu sehen (Summe des `.text` der Einheiten gegen die eine Datei, siehe oben); genau erst mit dem gebundenen Programm.

## 5. Determinismus und Tests

**Dieselbe Eingabe, dieselben Einheiten**: der Generator schreibt schon heute Byte für Byte dasselbe C (darauf beruht `ProgramCache`). Die Teilung muss das
erhalten: Gruppierung über den stabilen Schlüssel (2.), Hash mit SHA-256 oder FNV - **nicht** `string.GetHashCode()` (in .NET je Prozess zufällig); keine
Reihenfolge eines `Dictionary`/`HashSet`; die Zahl der Kerne geht nicht ein.

**Stabil bei kleinen Änderungen** ist schwerer: die Namen und Nummern des Generators hängen vom ganzen Programm ab - `fN` nach der Reihenfolge, in der die Runden
Funktionen finden, `K<i>` nach der ersten Benutzung, `ta_N`, Klassen-Ids, Ids der Einheiten. Ändert eine Bearbeitung eine dieser Reihenfolgen, ändert sich der
Text vieler Einheiten, und der Objekt-Cache hilft nicht. Ein Versuch dazu (siehe unten, "eine kleine Änderung") zeigt, wie groß das heute ist. Für Phase 2
heißt das: Namen aus dem Inhalt statt aus der Reihenfolge (`fN` aus dem Schlüssel der Funktion, wie die exportierten Namen der Module
`fx_<modul>_<Klasse>__<Member>_<i>`; `K_<hash des Texts>`; `ta_<hash des Typs>`) und ein Kopf je Einheit (2.).

**Versuch: eine kleine Änderung** (das C des Assistenten zweimal erzeugt, verglichen je fire-Funktion ohne die `#line`-Rücksprünge der Datei; die Einheiten
wie oben in der Reihenfolge der Funktionen nach Größe):

| Änderung in `packages/assistant/25_tips.fire` | Funktionen mit anderem Text (von 1721) | Einheiten betroffen (8 / 16 / 32) |
|---|---:|---|
| ein String-Literal geändert (gleiche Zeile) | 0 - nur die Definition von `K295` | nur, wo `K295` definiert ist |
| eine Zeile eingefügt, mit einem neuen String | 232 | 8 / 16 / 31 |
| dieselbe, die Namen `K<i>` aus dem Vergleich genommen | 72 | 4 / 8 / 14 |

Ein neuer String verschiebt die Nummern aller späteren (`K296` wird `K297`, ...): 160 Funktionen ändern sich nur dadurch. Die übrigen 72 sind die Funktionen
hinter der eingefügten Zeile **in derselben Quelle** - ihre `#line`-Zeilen verschieben sich (die Dateien eines Packages sind *eine* Quelle des Preludes,
`fire:prelude/13`). Das ist echt (die Debug-Informationen müssen stimmen) und begrenzt sich auf die Einheiten dieses Packages, wenn nach Quelle gruppiert wird.
Daraus: Strings mit Namen aus ihrem Inhalt und Gruppen nach Quelle - dann trifft eine solche Änderung die Einheiten eines Packages, nicht alle.

**Tests**:

* Die native Testgruppe mit `FIRE_SPLIT=1` (wie `FIRE_MODULES=1`, docs/tools/testing.md): jede Ausgabe gleich; verschieden dürfen nur Prüfungen sein, die die
  Form des C ansehen. Zusätzlich `FIRE_SPLIT_UNIT_KB=<klein>` (Einheiten von wenigen KB), damit auch kleine Testprogramme in viele Einheiten zerfallen und jede
  Grenze (Lambda in einer anderen Einheit als ihre Funktion, ein String in vielen Einheiten, ein Typargument über Einheiten) einmal getroffen wird.
* Binden mit `-Wl,--no-undefined` und ohne doppelte Symbole; `nm` des Programms: kein Symbol des Programms exportiert (alle `hidden`), mit `shared` genau die
  Liste der Hooks und Tabellen.
* Determinismus: zweimal erzeugen, alle Dateien gleich; eine Änderung in einer Methode - nur die erwarteten Einheiten anders (Phase 2).
* Debugger: die Fälle "Native-Debugger" (`NativeDebugTests.cs`) mit `FIRE_SPLIT=1`, gdb mit einem Haltepunkt in einer Funktion einer anderen Einheit;
  AddressSanitizer/UBSan.
* Toolchains: gcc, clang, TinyCC (bleibt eine Datei), MinGW unter Wine, Emscripten (Gruppe `packer`), ESP-IDF als `files` (QEMU). msvc nicht prüfbar.
* **Messungen** mit `FIRE_BUILD_TIMES=1`: je Einheit `gcc unit k/N: ms`, die Summe, die Wand-Zeit, der kritische Pfad; ein Vergleich (Assistent, Docking,
  CLI) für kalt, ohne Änderung und mit einer Änderung in einer Methode. Dazu die neun Benchmarks von `fire.Benchmarks` gegen die eine Datei: was `hidden`
  statt `static` kostet.

## 6. Phasen, Aufwand, Risiken

| Phase | Inhalt | Aufwand | Risiken |
|---|---|---|---|
| 1 | **Teilen und nebeneinander übersetzen**: Marken im Text, ein letzter Schritt verteilt (Kopf, Einheit 0, Helfer, Rümpfe nach geschätzter Zeit, Natives, die für sich stehen); `FIRE_PS` statt `static` an den Stellen der Tabelle in 3.; eine Definition für `ta_`/`tl_`/`K`/Tabellen; `NativeBuilder`: Einheiten mit `Parallel.For`, dann binden, `BuildMonitor`, `FIRE_BUILD_TIMES`; `"split"` in `fire.native.json`, `FIRE_SPLIT=1`; die Testgruppe damit | 1-2 Wochen | das Programm wird langsamer (weniger Einbetten über Einheiten: messen, gruppieren nach Klasse, sonst LTO); Durchgänge über den ganzen Text, die Einheiten übersehen; Toolchains mit eigenen Befehlszeilen |
| 2 | **Cache der Objekte** und stabile Einheiten: Namen aus dem Inhalt (`fN`, `K`, `ta_`), Kopf je Einheit, inhaltsdefinierte Grenzen; Cache unter `objects/` | 1-2 Wochen | jede Nummer des Generators, die noch von der Reihenfolge abhängt, macht den Cache wirkungslos (der Versuch zeigt, wie viele es sind); Debug-Informationen mit Pfaden |
| 3 | **Natives und Module zusammen**: eine Einheit je Import, der für sich steht, im Cache über Programme hinweg (nur Schalter + Compiler im Schlüssel, wie eine Library von `static`); die Grenzen der Einheiten an den Modulen; gemeinsamer Code mit `BuildStatic` | ~1 Woche | Packages, deren C stillschweigend eine Datei annimmt (Makros, gemeinsame Header) |
| 4 | **LTO für Publish**: `-flto=auto`/`-flto=thin`/`/GL`, Messung gegen 146 s | einige Tage | Zeit und Speicher des Bindens auf 4 Kernen; Toolchains ohne LTO (TinyCC, Watcom, ESP-IDF eigener Weg) |

### Empfehlung

* **Phase 1 lohnt sich** für große Programme mit gcc/clang/MinGW/emcc: beim Assistenten -O2 von ~220-270 s auf ~60-70 s, -O0 von ~37 s auf ~10-15 s, ohne
  dass sich am Programm etwas ändert. Der Weg mit den Marken (der Generator schreibt einen Text, ein letzter Schritt verteilt ihn) hält den Eingriff klein:
  die Runden, das Weglassen und die Durchgänge über den Text bleiben, wie sie sind. Wichtig sind dabei drei Dinge, die der Versuch gezeigt hat: der Kopf nur aus
  **Deklarationen** (sonst übersetzt bei -O0 jede Einheit alle Helfer), die **Helfer und Natives je einmal** (sonst +45 % Rechenzeit) und eine Verteilung nach
  **geschätzter Zeit** mit den größten Einheiten zuerst. Vorher die Benchmarks: kostet `hidden` statt `static` messbar Tempo, bleibt Publish bei einer Datei oder
  bekommt LTO (Phase 4) früher.
* **Phase 2 erst nach Phase 1** und nur mit stabilen Namen: heute ändert ein neuer String den Text fast aller Einheiten (siehe Versuch); ohne Namen aus dem
  Inhalt wäre der Objekt-Cache kaum mehr als der Cache der Programme.
* **Phase 3** früh für Packages mit viel C (SQLite, die Engine von `ai`): eine Einheit, die sich nie ändert und über Programme hinweg im Cache liegt.
* **TinyCC** bleibt der Weg für den schnellsten Bau (1 s, eine Datei); die Teilung ist für die optimierten Bauten und für Ziele, die kein TinyCC haben.
* Unabhängig davon bleibt der größte Hebel auf lange Sicht **weniger C** (die Helfer sind beim Assistenten ein Viertel der Datei) und ein schnellerer Generator,
  der nach der Teilung ein Drittel des Baus ist.
