# forge in C (`native/forge`)

Status: **Phase C, Schritte 1 bis 5** aus [ROADMAP_C_PORT.md](../ROADMAP_C_PORT.md#phase-c---das-frontend-in-c) (20.11.2026), dazu
**Phase D Schritt 1**, der Generator Bytecode -> C ([unten](#phase-d-schritt-1-der-generator); mit Reflection, Probes, Debug-Builds, `--stats`, den Modulen für
linkage static/shared, `FIRE_MODULES=1` und dem Generator eines Plugins), der größte Teil von **Schritt 2**, dem Builder ([unten](#phase-d-schritt-2-der-builder):
`forge-c build` mit linkage `source`, `static`, `shared`, `plugin`, `--stats`), und **Schritt 3**, Projekte, Solutions und das Paketformat `.fpk`
([unten](#phase-d-schritt-3-projekte-solutions-pakete): `forge-c build`/`publish` eines Projekts oder einer Solution, die Engine `vm`, die Standardpakete), und
**Schritt 4**, der Rest der Befehlszeile ([unten](#phase-d-schritt-4-der-rest-der-befehlszeile): der Modellwandler, `forge test`, `build|publish|bytecode
--report json` - der Bericht, den spark liest -, `run`, `native`, `data`, `dump`, `stats`, `bytecode` von Projekten, mehrere Dateien, `-D`, `-i`, `--overlay`,
`--debug-info`; offen `tables`, `prebuild-standard`, `dump` einer Programmdatei, die sagen, was ihnen fehlt): Lexer,
Präprozessor mit `#if`, Diagnosen, AST und Parser (1, 2); Resolver, Typprüfer und `SetterClaims` mit dem, was sie vom Linker brauchen - die
Direktiven eines Builds mit ihrer Wirkung, `#import` mit den Preludes und den Natives der installierten Pakete (3); der Bytecode-Compiler, was die
Typprüfung ihm hinterlässt (mit der Herleitung von `defined`), der Rest des Linkers und der Schreiber der Programmdatei `.fbc` (4); die
Markup-Generatoren - `.fxml` (Oberflächen), `.biscuit` (Schaltpläne), `.fdata` (Datenmodelle), der Import von Falstads Schaltungen - hinter
`#include` und als eigene Befehle (5); dazu **Schritt 6**, die Sprachdienste als Prozess: `forge-c serve` ([FORGE_SERVE.md](FORGE_SERVE.md), [unten](#forge-serve)). Mechanischer Port 1:1 (L3): je C#-Datei eine C-Datei mit denselben Namen in derselben
Reihenfolge, im Kopf jeder Datei die C#-Datei und der Commit, dem sie entspricht. Kein globaler Zustand und kein `exit` (L6): alles hängt an
`FcCtx`, Fehler kommen als Ausnahme auf dem Kontext beim Aufrufer an.

```
cc -O2 native/forge/forge.c -o forge-c -lm          (gcc 13, clang; -Wall -Wextra ohne Warnung; eine Datei, die alles einbindet)
forge-c tokens x.fire                                (die Token der vorverarbeiteten Quelle, dann die Diagnosen)
forge-c ast x.fire                                   (der Syntaxbaum, dann die Diagnosen)
forge-c diagnostics x.fire                           (was `forge run` beim Übersetzen auf stderr schreibt, bis einschließlich der Typprüfung)
forge-c fbc debug|release|performance|default x.fire   (die Programmdatei, die `forge bytecode x.fire -m <modus>` schreibt; bei Fehlern `error` und die Diagnosen)
forge-c bytecode x.fire -o x.fbc [-m DEBUG|RELEASE|PERFORMANCE]   (wie `forge bytecode`: die Warnungen und Fehler auf stderr; für tests/run-cases-vm.sh)
forge-c tokens|ast|diagnostics --list <dateien.txt> --out <ordner>   (eine Ausgabe je Zeile der Liste: <ordner>/<n>.txt; ebenso `fbc <modus> --list ...`)
forge-c ui|biscuit|fdata x.fxml|x.biscuit|x.fdata    (das erzeugte Skript auf stdout, die Fehler des Markups auf stderr - wie `forge ui|biscuit|fdata`)
forge-c falstad schaltung.txt                        (der Schaltplan auf stdout, die Warnungen auf stderr - wie `forge falstad`)
forge-c markup --list <dateien.txt> --out <ordner>   (je Datei beide Ströme, der Befehl nach der Endung: .fxml, .biscuit, .fdata, sonst falstad)
forge-c serve [--tables <ordner>] [--packages <ordner>] [--debounce <ms>] [--log <datei>]   (die Sprachdienste, JSON-Zeilen über stdio: FORGE_SERVE.md;
                                                     -lpthread auf POSIX)
tests/compare-frontend.sh <forge> <forge-c> [tokens|ast|both|diag|fbc|markup|all] [<ordner>]   (FRONTEND_MUTANTS=n: dazu n beschädigte Kopien je Quelle;
                                                     FORGE_CS_CACHE=1: Ausgaben des C#-forge aus dem Ordner wiederverwenden;
                                                     FBC_MODES="release": nur diese Modi bei `fbc`)
```

`forge-c diagnostics` liest die Prelude aus `native/tables/prelude.fire` (`FIRE_TABLES=<ordner>`, sonst `<programm>/../tables`, sonst
`native/tables` des Arbeitsordners) und die installierten Pakete aus `FIRE_PACKAGES=<ordner>` (sonst `<programm>/Packages` - wie forge, das
sie neben sich hat; `forge install-standard` legt sie an). Für den Vergleich zeigt `FIRE_PACKAGES` auf den Ordner `Packages` des C#-forge.

```
```

`libm` braucht es für `Math.Pow` (eine Umrechnung `coerce ... => value ^ 2` wird beim Übersetzen gerechnet) - wie bei der VM.

## Das Maß: `forge tokens` / `forge ast`

Der C#-Compiler hat dafür zwei Befehle bekommen (`src/fire.Compiler/FrontendDump.cs`); `forge-c` gibt **byte-gleich** dasselbe aus:

* **Vorverarbeitung**: die Datei wie `File.ReadAllText` (BOM, UTF-8), `Preprocessor.Process` mit `DirectiveRegistry.CreateDefault()` und den
  Direktiven, die der Linker registriert (`#import`, `#name`, `#intwidth`, ... mit ihrer Zahl an Parametern, die geprüft wird - ohne ihre Wirkung:
  ein `#import` holt kein Paket), Symbole von `#if` wie für einen Lauf in der VM auf dieser Maschine (`ConditionalSymbols.For(null, "vm")`:
  `linux`/`posix`, `vm`, `word64`).
* **`tokens`**: die `#using`-Namen (`using "Name"`), dann ein Token je Zeile: `Zeile:Spalte Art "Lexem" len=Länge [nl] [unit="..."]` und der Wert
  eines Literals (`int=`, `float=0x<Bits>`, `value="..."`, bei `$"..."` die Segmente `T"Text"` / `E"Ausdruck"[:"Format"]`).
* **`ast`**: `Parser.ParseMultiple` der einen Quelle (benannt nach dem Dateinamen) unter einem `DiagnosticCollector`. Der Baum: jeder Knoten mit
  seinem C#-Typ und seinen Feldern in der Reihenfolge des primären Konstruktors, dann die später gesetzten (`BinaryExpr.FloatPower`,
  `AssignExpr.Replace`); Listen als `[n]` mit Elementen `-`, `own[n]` für eine `OwnNamespaceList`, `null`; Werte als `int 5 unit="mm"`,
  `float 0x...`, `string "..."`. Was der Parser neben dem Baum festhält, steht beim Knoten: `Parenthesized: true` (`Parser.ParenthesizedAssignments`),
  `SwitchValues` (`Parser.Switches`). Danach die Warnungen (`warn`) im Format von forge und die erklärten Warnungen (`declared ...`).
* **Texte** in Anführungszeichen: druckbares ASCII, sonst `\uXXXX` je UTF-16-Einheit - unabhängig von der Kodierung der Ausgabe.
* **Diagnosen** genau wie forge sie druckt (`Diagnostic.ToLogLine`, Codes aus `native/tables/errors.def`): bei einer Ausnahme des Frontends nur die
  Zeilen von `CompileDiagnostics.Of` (nach dem, was bis dahin geschrieben war: bei `tokens` die `using`-Zeilen).

`tests/compare-frontend.sh` übersetzt den Korpus (`tests/cases/**/*.fire`, `packages/**/*.fire`, `src/fire.Project/**/*.fire` ohne `bin`/`obj`)
mit beiden und vergleicht je Datei.

## Das Maß von Schritt 3: `forge diagnostics`

`forge diagnostics <datei>` (neu in `FrontendDump.cs`) übersetzt die Datei wie `forge run <datei>` - `Linker.CompileAndLink` mit der Datei als
Quelle, ihrem Ordner als Basis, den Symbolen der VM auf dieser Maschine -, hält aber nach der Typprüfung an (`Linker.FrontendOnly`: der
Bytecode-Compiler läuft nicht). Ausgegeben wird, was forge dabei auf stderr schreibt: die Warnungen, wie sie kommen (`OnReport` des
`DiagnosticCollector`: `warn` des Parsers, erklärte Warnungen an der Stelle ihrer Verwendung, die Warnungen der Typprüfung), dann die Fehler
einer gescheiterten Übersetzung (`CompileDiagnostics.Of`: alle Fehler des Resolvers, die der Typprüfung, eines Linkers). Steht `//# types: strict`
unter den letzten 12 Zeilen, läuft die Prüfung strikt (wie `tests/run-cases.sh` mit `--types strict`). Eine Ausnahme, die kein Übersetzungsfehler
ist, ist `internal: <Typ>: <Text>` (C#) bzw. `error E9999: internal: ...` (C) - sie kommt im Korpus nicht vor.

## Das Maß von Schritt 4: `forge fbc`

`forge fbc <modus> <datei>` (neu in `FrontendDump.cs`) gibt die Bytes aus, die `forge bytecode <datei> -m <modus>` schreibt
(`ProgramFile.Write` mit den Zeilentabellen, im Modus Debug mit der Tabelle der Quellen: Datei und SHA-256 je Quellindex) - übersetzt wie dort
(`Linker.CompileAndLink` mit der Datei als Quelle, `//# types: strict` wie bei `diagnostics`); `default` lässt `-m` weg (dann entscheiden `#debug`
/`#performance`). Eine gescheiterte Übersetzung gibt `error` und die Fehler, wie forge sie druckt. `forge-c fbc` gibt **byte-gleich** dasselbe aus;
verglichen werden die Bytes (`forge dump x.fbc` zeigt eine Programmdatei als Text, auch eine von `forge-c` - so lässt sich ein Unterschied finden).

Dazu der Lauf über die VM in C: `tests/run-cases-vm.sh forge-c` (mit `FIRE_TABLES`, `FIRE_PACKAGES` und `FIRE_PKG_PATH` auf den Ordner des
C#-forge, der die Bibliotheken der Pakete gebaut hat) übersetzt jeden Fall mit `forge-c bytecode` und lässt ihn von `fire` laufen.

## Das Maß von Schritt 5: `forge ui|biscuit|fdata|falstad`

Die Befehle gab es im C#-forge schon (`Program.cs`): `forge ui|biscuit|fdata <datei>` schreibt das erzeugte Skript
(`MarkupFiles.GenerateScript`) auf stdout, bei Fehlern des Markups je Diagnose `<datei>: line n: ...` auf stderr; `forge falstad <datei>` die
Warnungen auf stderr, den Schaltplan auf stdout. `forge-c` hat dieselben Befehle; `forge-c markup --list` schreibt je Datei beide Ströme in eine
Ausgabe (der Befehl nach der Endung), `tests/compare-frontend.sh ... markup` ruft den C#-forge je Datei auf (`2>&1`) und vergleicht **byte-gleich**:

* die Markup-Dateien des Repositorys (`docs/images/src`, Beispiele und Vorlagen in `src/fire.Project`, `src/fire.UI.Markup/CircuitSamples`): 12
  `.fxml`, 14 `.biscuit` - `.fdata` und Falstads Schaltungen gibt es dort keine;
* was `src/fire.Testing` inline schreibt (`tests/extract-markup.py`: die String-Literale - auch `"""`-Rohtexte und Verkettungen mit Konstanten -,
  die nach Markup aussehen): 111 `.fxml` (Diagnosen, halb getipptes Markup der Completion-Tests, Generator), 7 `.biscuit`, 4 `.fdata`, 8 Falstad;
* mit `FRONTEND_MUTANTS=n` n beschädigte Kopien jeder dieser Dateien - die Fehlerwege der Leser von XML und JSON (deren Meldungen samt Zeile und
  Spalte .NET schreibt: `XmlException`, `JsonReaderException`).

Und hinter `#include`: `FxmlBinding` (die letzte Abweichung von Schritt 1 bis 4) ist in `tokens`, `ast`, `diagnostics` und `fbc` (alle drei Modi,
auch strikt) gleich.

## Stand gegen den Korpus

| Teil | Dateien | `tokens` gleich | `ast` gleich | `diagnostics` gleich | `fbc` Debug | `fbc` Release | `fbc` Performance |
|---|---|---|---|---|---|---|---|
| `tests/cases` | 1129 | 1129 | 1129 | 1129 | 1129 | 1129 | 1129 |
| `packages` | 93 | 93 | 93 | 93 | 93 | 93 | 93 |
| `src/fire.Project` (Beispiele, Vorlagen) | 34 | 34 | 34 | 34 | 33 | 33 | 33 |
| beschädigte Kopien (`FRONTEND_MUTANTS=2`; `fbc`: `=1`) | 2512 | 2512 | 2512 | 2512 | - | 1256 von 1256 | - |
| der Korpus strikt (jede Quelle mit `//# types: strict`; seit Schritt 5 nur `FxmlBinding` neu geprüft) | 1256 | - | - | 1256 | - | 1255 | - |

| Markup (Schritt 5, `markup`) | Dateien | gleich |
|---|---|---|
| Dateien des Repositorys (`.fxml`, `.biscuit`) | 26 | 26 |
| inline in `src/fire.Testing` (`.fxml`, `.biscuit`, `.fdata`, Falstad) | 130 | 130 |
| beschädigte Kopien (`FRONTEND_MUTANTS=3`) | 468 | 468 |
| eigene Fälle der Fehlerwege und Umwandlungen (nicht im Skript) | 385 | 382 |

(Die drei: `.fdata`, bei denen C# mit einer Ausnahme von System.Text.Json abbricht - `"values": [1]`, `"length": "x"`, `"format": "x"`; `forge-c`
schreibt dieselbe erste Zeile `Unhandled exception. System.InvalidOperationException: ...`, nicht den Stack.)

`fbc` über die Fälle: 957 der 1129 übersetzen (zu Programmdateien von 112 KB bis 1,8 MB - jede mit der ganzen Prelude und den Preludes ihrer
Importe), 172 sind Fehler des Frontends oder des Compilers; von den beschädigten Kopien übersetzen 361. `tests/run-cases-vm.sh` mit `forge-c`:
**1126 von 1128** richtig (1 nicht gelaufen) - dieselbe Zahl wie mit dem C#-forge ([VM_C.md](VM_C.md#stand-gegen-den-korpus)), die
Programmdateien sind dieselben, also auch die zwei falschen (`native/3210`, `native/4110`: Textumbruch der UI). Die eine Abweichung von `fbc`
(`FxmlBinding` mit seiner `.fxml` ist seit Schritt 5 gleich): `Scene3D Models`, das Modelle als Ressourcen einbindet (`new Resource("robot.gltf")`, `.fblocks`, `.fmesh`) - die macht der C#-Linker beim Einbinden
zu fire's Modelldatei (`ModelConverter`, Phase D Schritt 4), `forge-c` nimmt die Datei, wie sie ist.

`diagnostics` über die Fälle: 472 der 1129 Ausgaben sind nicht leer (1333 Zeilen: TC005 320, TC020 301, TC004 145, TC019 111, TC001 90,
Fehler des Resolvers E4xxx, ...); die beschädigten Kopien bringen 405 Ausgaben mit Fehlern des Resolvers (E4022, E4016, E4044, ... - 2600
Zeilen) und 266 mit Meldungen der Typprüfung. Gegen die festgehaltenen `.diagnostics` (was `forge run` auf stderr schrieb):

| Ordner | Fälle mit `.diagnostics` | wörtlich gleich | die Zeilen des Frontends gleich |
|---|---|---|---|
| `tests/cases/diag` | 38 | 37 | 38 |
| `tests/cases/types` | 275 | 260 | 275 |
| `tests/cases/syntax` | 62 | 62 | 62 |

(Die übrigen enthalten, was erst die Laufzeit schreibt - `Unhandled exception ...` -, oder Fehler des Bytecode-Compilers, E5xxx.)

Geschwindigkeit: `diagnostics` über `tests/cases` und `packages` (1222 Dateien, je mit der ganzen Prelude und den Preludes ihrer Importe, ein
Prozess) 17,7 s gegen 51,6 s des C#-forge. gcc und clang mit `-Wall -Wextra` ohne Warnung; ASan/UBSan ohne Befund über den Korpus, die
beschädigten Kopien und den strikten Korpus.

`Samples/Graphics and UI/FxmlBinding/main.fire`, das mit `#include` eine `.fxml` einbindet, war bis Schritt 4 die eine Abweichung - seit
Schritt 5 ist es gleich. Die beschädigten Kopien (abgeschnitten, ein Zeichen weg, ein
Stück wiederholt) laufen durch die Fehlerwege: 75 verschiedene Codes von Lexer, Präprozessor und Parser, alle gleich; ASan/UBSan ohne Befund.

Die Diagnosen der Fälle (`.diagnostics`, von `forge run` festgehalten): jede, die aus Lexer, Präprozessor oder Parser kommt, gibt `forge-c ast`
wörtlich so aus (`syntax` 25 von 29 Fällen mit Diagnosen, `diag` 12 von 26, `types` 7 von 160 - der Rest sind Codes des Resolvers und der
Typprüfung, Schritt 3).

Geschwindigkeit: `ast` über `tests/cases` und `packages` (1222 Dateien, ein Prozess) 0,55 s gegen 2,2 s des C#-forge.

Geschwindigkeit von `fbc` (Release) über `tests/cases` und `packages` (1222 Dateien, ein Prozess, mit dem Schreiben der Programmdateien): 34,8 s
gegen 87,1 s des C#-forge. gcc und clang mit `-Wall -Wextra` ohne Warnung; ASan/UBSan über den Korpus und die beschädigten Kopien ohne Befund (die eine Meldung, ein
Linksschieben einer negativen Zahl im Zickzack von `ProgramFile`, ist behoben).

In beiden korrigiert (22.11.2026, `Compiler.cs` / `compiler.c`): ein Name, den irgendeine Klasse oder Methode des Programms (auch der Prelude: `A`, `B`, `K`,
`T`, ...) als Typparameter benutzt, war überall "kein Typ" - eine Klasse gleichen Namens bekam weder `TypeArray` (`A arr[] = new A[2]` nahm jedes Objekt) noch die
Prüfung eines Parameters, einer Variable, eines Felds (`DeclaredTypeOfCore`, `TypeArgText`). Die Typparameter, die gelten (der Methode, der Klasse), findet der
Compiler vorher; der Name allein heißt jetzt nur noch für einen Namen ohne Klasse, Interface oder Enum "Typparameter" (`RefParamTable.IsTypeParam`). Fälle
`native/4460`, `native/4470`, `types/2760`; kein anderer Fall des Korpus ändert seine Ausgabe, `fbc`/`ast`/`diag`/`native` beider forges gleich.

## forge serve

Phase C Schritt 6 (L4): die Sprachdienste von spark als Prozess, eine JSON-Nachricht je Zeile über stdio - das Protokoll steht in
[FORGE_SERVE.md](FORGE_SERVE.md). Die Logik zog von spark nach forge, 1:1 wie der Rest (je C-Datei die C#-Datei und ihr Commit, 1c549476):
`serve_index.c` (`ScriptSymbolIndex.cs`: der Index aus den Token, die Preludes, die Importe, die anderen Dateien), `serve_types.c`
(`ScriptSymbolIndex.Types.cs`, `BuiltinMembers.cs`: der Typ eines Ausdrucks), `serve_complete.c` (`CompletionEngine.cs`), `serve_diag.c`
(`LiveDiagnostics.cs`, `ProjectLibraries.cs`: die Analyse auf dem Frontend in C - Prelude, Bibliotheken mit ihren Namespaces, die anderen
Dateien, das Dokument; Resolver, Typprüfung, Compiler), `serve_nav.c` (`NavigationEngine.cs`: Hover, Signatur, Definition; dazu Referenzen,
Gliederung, Faltung), `serve_markup.c` (`MarkupCompletion.cs` auf `markupschema.c` von Schritt 5 - eingebunden, sobald das Schema Teil von
forge-c ist), `serve.c` (das Protokoll: ein Lesefaden, der Hauptfaden für Dokumente, Projekte und alle Antworten, ein Analysefaden für die
Diagnosen mit Entprellen, latest wins, `stale`, den letzten vier Ergebnissen je Dokument; Texte mit Zählern zwischen den Fäden, POSIX-Threads
bzw. Win32). Der Frontend-Teil ist unverändert: eine Analyse übersetzt in einem Kontext für sich (L6: kein globaler Zustand - darum laufen
Analysefaden und Hauptfaden nebeneinander).

**Die Prüfungen** (`src/fire.Testing`, gegen `forge-c serve`, das `ServeClient` aus `native/forge/forge.c` baut, oder `FIRE_FORGE_C`):

| Gruppe | was | Stand |
|---|---|---|
| `FIRE_TESTS_ONLY=completion` | die 61 Fälle von CompletionTests über das Protokoll; **jede Liste** (Text, Art, Punkte, Detail, Reihenfolge) verglichen mit `CompletionEngine` im eigenen Prozess | alle gleich |
| `FIRE_TESTS_ONLY=serve` | ServeTests: Entprellen (fünf schnelle Änderungen - eine Analyse, die neueste), danach nichts mehr, eine spätere Version, ein gemerktes Ergebnis, eine überholte Analyse mit `stale`, Edits, `resync`, ein Projekt mit Overlay einer geöffneten anderen Datei, die Fehler des Protokolls; die Diagnosen von 10 Fällen allein und 19 Dokumenten von Projekten (Bibliotheken mit Namespace, kaputte Bibliothek, kaputte andere Datei, unbekannter Import mit Hinweis, der eigene Import einer Bibliothek, je ein Dokument der 14 Projekte von ruhrpott-raser) gleich `LiveDiagnostics`; die Vervollständigung an 27 Stellen der Dokumente von ruhrpott-raser gleich `CompletionEngine`; dazu die CompletionTests | 122 von 122 |
| `FIRE_TESTS_ONLY=editorchecks` | BackgroundCheckTests (spark plant seine Prüfungen weiter selbst) und der Protokollteil von ServeTests | 62 von 62 |
| `FIRE_TESTS_ONLY=ui` (Markup) | die 22 Markup-IntelliSense-Prüfungen über das Protokoll, jede der 23 Antworten verglichen mit `MarkupCompletion` im eigenen Prozess | alle gleich - mit `markupschema.c` (gebaut mit dem Stand von Schritt 5 aus dessen Zweig); ohne Schema im eigenen Prozess |

ASan/UBSan (gcc) über ServeTests und die Dokumente von ruhrpott-raser: ohne Befund; gcc, clang mit `-Wall -Wextra` ohne Warnung, tcc baut es.

**Zahlen** (ruhrpott-raser, 104 Dateien, 1,9 MB; 4 Kerne unter Last eines parallelen Testlaufs): die Analyse eines Dokuments mit seinem
Projekt und allen Bibliotheken 60-590 ms über das Protokoll gegen 160-1850 ms von `LiveDiagnostics` im eigenen Prozess (rund 3-mal so schnell;
Raser.Game: 64 Dateien, 1,4 MB - 450-800 ms); ein Tastendruck (ein Edit, dann `completion`) 2,6-7,5 ms, die erste Vervollständigung nach dem
Öffnen 10-150 ms (die Indizes der anderen Dateien, danach gemerkt); eine Antwort auf denselben Text 1,6-4 ms; zurück auf einen geprüften Text 0 ms
(gemerkt). Die vollständige Analyse beim Tippen: ein Syntaxfehler 40-100 ms (der Parser hält an), sonst die volle Übersetzung.

**Offen**: Hover, Signatur und Definition gehen in spark über den Server, haben aber noch keine Vergleichsprüfung gegen `NavigationEngine`;
die Prüfung aller Dateien eines Projekts (`AnalyzeProjectFiles`) läuft noch in spark; die C++-Natives eines Bibliotheksprojekts
(`ProjectNativeOverlay`) kennt die Analyse nicht; eine laufende Analyse lässt sich nicht abbrechen (sie läuft zu Ende, ihr Ergebnis ist
`stale`); übersetzte Einheiten (Prelude, Bibliotheken) werden nicht über Analysen hinweg gemerkt - jede Analyse übersetzt alles (dafür müssten
Parser und Resolver ihren Zustand vom Kontext einer Übersetzung lösen); Highlighting (`SyntaxHighlighter`) bleibt in spark.

## Dateien

| Datei | Inhalt | Gegenstück in C# |
|---|---|---|
| `forge.h` | der Kontext `FcCtx`, Texte (`FcBuf`, UTF-16-Puffer `FcU16`), Listen, Ausnahmen (`FC_TRY`/`FC_CATCH`, `fcThrow`), Diagnosen | `string`, `List`, Exceptions |
| `util.c` | dazu die Umsetzung; `char.IsLetter`/`IsDigit`/`IsWhiteSpace` für UTF-16-Einheiten (Tabellen `unicode.inc`), `File.ReadAllText` | .NET |
| `unicode.inc` | Buchstaben und Ziffern der BMP als Bereiche (erzeugt, siehe unten) | `char.IsLetter`, `char.IsDigit` |
| `diag.c` | `Diagnostic.ToLogLine`, `DiagnosticCollector` (Warnungen, erklärte Warnungen), `ErrorCatalog.CodeOf`, `CompileDiagnostics.Of` | `Diagnostics.cs` |
| `lexer.h`, `lexer.c` | `TokenType`, `Token`, `InterpolationSegment`; der Lexer | `Lexing/Token.cs`, `Lexer.cs` |
| `conditional.c` | `#if`/`#elif`/`#else`/`#endif`/`#ifdef`/`#ifndef`/`#define`/`#undef`/`#error`, die Ausdrücke von `#if`, `InlineCScan` | `Conditional.cs` |
| `preprocessor.c` | Zeilen, Direktiven, `#using`, `#include` (einmal je Übersetzung, Kreise), die Parameter einer Direktive | `Preprocessor.cs` |
| `units.c` | was der Parser von den Einheiten braucht: `Unit.Parse` (Dimensionen, Faktor, Offset, Umrechnungsfunktionen), `CaretExponents`, `NiceFactor`, die Einheiten des Programms | `Values/Unit.cs` (Teil) |
| `ast.h`, `ast.c` | der Baum als getaggte Structs aus der Arena (dieselben Knotenarten und Felder wie in C#), seine Ausgabe | `Ast/Expr.cs`, `Ast/Stmt.cs`, `Ast/TypeRef.cs` |
| `parser.c` | der Parser, Methode für Methode in der Reihenfolge der C#-Datei; `ParseMultiple` mit den Einheiten des Programms, Namespaces, Erweiterungen, generischen Namen | `Parser.cs` |
| `dict.c` | `Dictionary`/`HashSet` mit der Reihenfolge von .NET (Einfügereihenfolge; ein entferntes Element ist ein Loch, das das nächste `Add` füllt), Schlüssel Text, Zeiger oder mit eigener Gleichheit | .NET |
| `unitv.c` | die Einheit als Wert: `Unit.Parse` mit dem Namen, die Namen zusammengesetzter Einheiten (`TermsOfText`, `MergeTerms`, `SimplifyTerms`, `RenderTerms`), `Multiply`, `Divide`, `Equals`, `ToString`, `DemandText` | `Values/Unit.cs` |
| `typeref.c` | was Resolver und Typprüfer von einem Typ fragen: `ResolveBaseName`, `AmbiguousCandidates`, `ToString`, `WidthCode`; `GenericClassNames`; `ArrayElem.Of`, `DeclaredKind.Of` | `Ast/TypeRef.cs`, `Ast/GenericClassNames.cs`, Teile von `Values/ArrayElem.cs`, `Bytecode/DeclaredKind.cs` |
| `resolver.c` | der Resolver, Methode für Methode; die Reihenfolge der statischen Initialisierer (E4073/E4078/E4079); `ResolvedRef`; `AstNames` (der Gang über die Eigenschaften der Records als `fcWalkExpr`/`fcWalkStmt` in der Reihenfolge ihrer Felder) | `Resolver.cs`, `Resolver.StaticInits.cs`, `Resolving/ResolvedRef.cs` |
| `typechecker.c` ... `typechecker4.c` | der Typprüfer (`StaticType` als unveränderliches `FcST`), in vier Teilen in der Reihenfolge der C#-Datei; `Compiler.AnalyzeCoercion`, `BuiltinMembers.Has`, `BufferMethods.Has`; TC035 | `TypeChecker.cs`, `TypeChecker.TakeLocal.cs` |
| `setterclaims.c` | die Wege durch einen Setter (TC018) | `SetterClaims.cs` |
| `linker.c` | `CompileAndLink`: die Prelude, die Direktiven eines Builds mit ihrer Wirkung (`#import`, `#intwidth`, `#debug`, ...), `ImportedPreludes` (`ParseImportName`, `WithDependencies`, `Insert`), `PackageImports` (`RegisterNames`, `NativeReturnTypes`, `NativesOf`), die installierten Pakete (`package.json`, ein kleiner JSON-Leser), `new Resource("...")`; nach der Typprüfung der Compiler, `FloatNarrowing` (`#floatwidth 32`, `#intwidth 32/16`), das gelinkte Programm (`fcLinkFile`) | `Linker.cs`, `ImportedPreludes.cs`, `PackageImports.cs`, `PackageLibrary.cs` (`FileNameFor`), `ProjectLibraries.cs` (Teil), `Package.Manager/PackageStore.cs` (Teil), `Runtime/FloatNarrowing.cs` |
| `bytecode.c` | die Opcodes (aus `native/tables/opcodes.def`), `Chunk` (Emit, Zeilentabelle, `DebugLocalNames`, `EndsWithOp`), `FunctionProto`, `HandlerTemplate`, `ClassMeta`, `RuntimeClass` (`AddMethod`, `AddConstructor`, `FieldInfo`), `ExternSignature`, die Texte von `DeclaredType`, `DeclaredKind`, `EnumType`, eine Konstante (`Value`: Art, Breite, `Wide`, Einheit, Enum), die Indizes der `NativeRegistry`, `CompiledProgram`/`LinkedProgram` | `Bytecode/*.cs` (Teile), `Values/EnumType.cs`, `Values/Value.cs` (Teil), `Runtime/CompiledProgram.cs`, `Runtime/LinkedProgram.cs` |
| `compiler.c`, `compiler2.c` | der Bytecode-Compiler, Methode für Methode in der Reihenfolge der C#-Datei: `RefParamTable`, die erklärten Typen, `Compile`, `StaticInitOrder`, die Klassen (`ClassMeta`, `ThisReads`, Spezialisierungen), die Prologe der Funktionen, die Anweisungen (1); die Ausdrücke, Lambdas, Argumente, Aufrufe, Zuweisungen, die Ankerregel (2); `ReadingMethods` | `Compiler.cs`, `ReadingMethods.cs` |
| `compilertrees.c` | die Ausdrucksbäume (`lambda expression<T>`): der Bau-Lambda und `__Attach` | `Compiler.Trees.cs` |
| `programfile.c` | der Schreiber von `.fbc` (Namen, Einheiten, Enums als Tabellen vor dem Programm; Funktionen und Chunks einmal, danach über ihre Nummer), SHA-256 der Quellen | `Bytecode/ProgramFile.cs` (`Writer`) |
| `markupmodel.c` | `MarkupDiagnostic`, `MarkupException` (`FC_ERR_MARKUP`), die Werte der Attribute, `MarkupElement`, die Ressourcen, `MarkupDocument`; was .NET dem Markup tut (`OrdinalIgnoreCase`, `Trim`, `ToLowerInvariant`, `$` eines Regex) | `fire.UI.Markup/MarkupModel.cs` |
| `markupschema.c` | die Elemente der Bibliothek `ui` mit Eigenschaften und Ereignissen (Tabellen; die Liste je Kontext: `FcCtx.markupSchema`), Konverter, `UIRoot.*`, reservierte Namen, die Muster | `MarkupSchema.cs` |
| `xml.c` | was das Markup von System.Xml braucht: `XDocument.Load` mit Zeileninfo (Namespaces, Entities, CDATA, Kommentare, Normalisierung), die Meldungen von `XmlException` mit Zeile und Position, `XElement.ToString` | `System.Xml.Linq` |
| `json.c` | was das Markup von System.Text.Json braucht: `JsonDocument.Parse` (Kommentare, Kommas am Ende, die Meldungen von `JsonReaderException`), die Anfänge der Token (`Utf8JsonReader`), `TryGetProperty` (die letzte gewinnt), `TryGetInt32`, `JsonSerializer.Serialize` (`UnsafeRelaxedJsonEscaping`) | `System.Text.Json` |
| `markupparser.c` | der Leser des Markups, Validierung, Bindungen, Objektwerte `[Type ...]`, Namespaces (`NamespaceScope` als `FmScope` statt `[ThreadStatic]`) | `MarkupParser.cs` |
| `fireuigenerator.c` | das Skript einer Oberfläche (Felder, Konstruktor, Ressourcen, Handler, Bindungen mit `undefined`, Run/Attach; die Vorschau mit `FmGen.preview` statt `[ThreadStatic]`); die Zahlenformate von .NET (`int.TryParse`, `double.TryParse`, `"0.0###..."`) | `FireUiGenerator.cs` |
| `biscuitgenerator.c` | `MarkupFiles` (welcher Generator), der Leser und Generator der `.biscuit` | `BiscuitGenerator.cs` |
| `datamodelgenerator.c` | der Generator der `.fdata` (mit `LineIndex`, `Plural`, `Snake`) | `DataModelGenerator.cs` |
| `falstadimport.c` | Falstads Schaltungen (Text und `<cir>`) als `.biscuit` | `FalstadImport.cs` |
| `forge.c` | das Programm `forge-c` (`tokens`, `ast`, `diagnostics`, `fbc`, `bytecode`, `ui`, `biscuit`, `fdata`, `falstad`, `markup`, `serve`, `cgen`, `buildplan`, `build`) | `FrontendDump.cs`, `CommandLine.cs` (`Bytecode`) |
| `serve.c` ... `serve_markup.c` | `forge serve` (siehe [oben](#forge-serve)) | `Language/` von spark, `MarkupCompletion.cs` |

`native/base/fire_base.h` (Arena, geordnete Map) wird unverändert mitbenutzt.

## Entscheidungen

* **Texte**: die Quelle wird einmal in UTF-16-Einheiten dekodiert (Spalten, Längen, `char.IsLetter` arbeiten auf Einheiten wie in C#); jeder andere
  Text ist "fc8": jede UTF-16-Einheit als ihre UTF-8-Folge (ein Surrogat einzeln, 3 Byte), die Einheit 0 als `C0 80`. So sind Lexeme
  nullterminiert und mit `strcmp` vergleichbar, ohne Verlust; erst die Ausgabe macht echtes UTF-8 daraus (ein einzelnes Surrogat wird U+FFFD, wie
  beim Encoder von .NET).
* **Ausnahmen**: `throw` ist `fcThrow` auf dem Kontext (`longjmp` zum innersten `FC_TRY`). Gefangen wird nur, wo C# fängt (`AttributesBeforeClass`,
  die Ausdrücke eines Format-Strings, der Dateiname in `ParseMultiple`, die Parameter einer Direktive). Die `finally`-Blöcke des Parsers sind ein
  Stapel von Aufräumschritten am Parser: auf dem normalen Weg tut der Code ihre Arbeit selbst, nach einer gefangenen Ausnahme laufen sie bis zur
  Tiefe des `try` - so stimmt auch der Zustand nach dem Zurücksetzen in `AttributesBeforeClass` (wie in C# bleibt `_bracketDepth` dabei stehen).
* **Knoten**: ein Struct je C#-Record mit einem Kopf (`FcExpr`: Art, Zeile; `FcStmt`: Art, Quelle, Zeile), angelegt mit `FC_NEW(ctx, Typ, .feld = ...)`
  aus der Arena. `with`-Kopien sind Kopien des Structs. Die Seitentabellen des Parsers (`ParenthesizedAssignments`, `Switches`) sind Felder des
  Knotens; eine Kopie (`replace` macht aus der Zuweisung eine neue) trägt die Markierung wie in C# nicht mit.
* **Einheiten**: die Ausgabe eines Literals zeigt den Text seiner Einheit (`Unit.ToString()`); dafür reicht der Name (ein Symbol der eigenen
  Einheiten, `m^1` ist `m`). Für `DefineUnits` (Fehler, Umrechnungsklassen) ist `Unit.Parse` mit Dimensionen und Faktoren portiert, die Namen
  zusammengesetzter Einheiten (`RenderTerms`, `SimplifyTerms`) nicht - sie kommen mit dem Resolver.
* **Was das Frontend nicht braucht, fehlt**: `ProcessedSource.Qualify`/`Module`/`ImportNamespaces` der Bibliotheken (der Linker setzt sie, Schritt 4),
  `Parser.SourceImportNamespaces` (für den Resolver), die Ressourcen von `new Resource("...")` (nur mit einer `ResourceTable`).
  Wo C# an einer Stelle mit einer .NET-Ausnahme abbräche (eine Zahl mit nicht-ASCII-Ziffern, ein Hex-Literal über 64 Bit), meldet `forge-c`
  `internal: ...` (Code E9999).
* **`unicode.inc`**: aus Pythons `unicodedata` (Unicode 15.1) - .NET 10 kennt eine neuere Version; Unterschiede gäbe es nur bei Zeichen, die seitdem
  dazukamen. Erzeugt mit: Kategorien `Lu Ll Lt Lm Lo` (Buchstaben) und `Nd` (Ziffern) der BMP ohne Surrogate als Bereiche.

### Schritt 3

* **Ein Programm aus Linker-Sicht**: `fcCompileFrontend` baut die Quellen wie `CompileAndLink`: die Prelude (`native/tables/prelude.fire`, von
  `forge tables` aus `Prelude.Source` geschrieben), die Datei, dann vor beiden die Preludes der Importe (je Einfügung an Stelle 1, wie
  `ImportedPreludes.Insert`); die Natives sind Namen (`natives.def`, `bridges.def`, die `functions` der Pakete), die Typprüfung bekommt die
  `returns` der Pakete. Die Pakete liest `forge-c` aus dem Ordner der installierten Pakete wie `PackageStore` (Ordner nach Namen ohne
  Groß/Klein sortiert, `imports` der `package.json`); `ProcessedSource.Qualify`/`Module`/`NotSerializable` setzt jetzt der Linker, der Parser
  qualifiziert mit `Qualify` und hält `SourceImportNamespaces` für den Resolver. Nicht portiert: Projekte (`BuildPlan`, Bibliotheken eines
  Projekts, `CheckLibraryHasNoEntryPoint`), Ziele (`target`), `-D`, `OwnImports` - die Befehlszeile der Dumps hat sie nicht.
* **Ausnahmen**: eine `ResolverException` ist `FC_ERR_RESOLVE` (Text, Zeile, Quelle), `Resolve` wirft am Ende eine mit allen Fehlern in
  `err.list`; `Guard`/`GuardWithState` sind Funktionen mit eigenem `FC_TRY` (kein lokaler Wert des Aufrufers lebt über ein `setjmp` hinweg). Eine
  `TypeCheckException` ist `FC_ERR_TYPECHECK` mit der Liste der Fehler, ein Fehler eines Direktiven-Handlers `FC_ERR_LINKER`.
* **Gleichheit der Records**: wo C# Records als Schlüssel nimmt, ohne `ReferenceEqualityComparer` (`HashSet<Expr> _takeAllowed` des Resolvers,
  `Distinct()` der Ereignisse in `SetterClaims`), vergleicht der C-Code nach Wert (`fcExprRecordEquals`, `scEventEq`), sonst nach Zeiger;
  `StaticType.Equals` ist `stEq` (Art, Klasse, Element, Signatur, Typargumente, Einheit).
* **Was die Typprüfung für den Compiler hinterlässt** kam mit Schritt 4 (siehe dort). Nicht portiert bleiben die Werkzeuge `MemberLog`, `VarLog`,
  `FIRE_TYPECHECK_SUGGEST`. Ein `EnumType` des Resolvers ist der Name der Enum (die Mitglieder kommen aus ihrer `EnumDecl`).
* **Die Namen zusammengesetzter Einheiten** (in Schritt 2 offen) kommen mit `unitv.c`: ein Literal trägt `Unit.Parse` seines Suffixes
  (`FcValue.unitv`), sein Text in der Ausgabe ist `ToString` davon. `OrderBy` der Dimensionen (die Kultur von .NET) ist ein Vergleich ohne
  Groß/Klein, bei Gleichstand der Kleinbuchstabe zuerst.

### Schritt 4

* **Ein Compiler je Funktionsrumpf** wie in C#: `FcComp` (Chunk, Tiefe der Scopes, Schleifen, `try`, Residenten, umgebende Klasse, ...), alle
  eines Programms teilen `FcCompShared` (die Referenzen des Resolvers, die Natives, die Fehler, die `RefParamTable`, die Enums, die bekannten Klassen
  und was C# in `[ThreadStatic]`-Feldern hält: `s_memberSource`, die Tabellen der Typprüfung, die rohen Bäume). Eine `NotSupportedException`/
  `InvalidOperationException` ist `FC_ERR_NOTSUPPORTED` (ohne Zeile: `CompileExpr` gibt ihr die des Ausdrucks), eine `CompilerException`
  `FC_ERR_COMPILE`; `CompileStmt` und die Mitglieder einer Klasse sammeln sie (je ein `FC_TRY`), `Compile` wirft am Ende eine mit allen.
* **Synthetische Knoten** (`new MemberExpr(..., "Compiled")`, das `undefined` von `?.`, `with`, der Bau-Lambda eines Baums) sind neue Knoten aus der
  Arena; die Tabellen der Typprüfung sind nach Zeiger geschlüsselt wie in C# (`ReferenceEqualityComparer`), sie finden sie also nicht.
* **Konstanten**: `FcConst` trägt, was `ProgramFile` von einem `Value` schreibt (Art, Zahl, Breite, `Wide`, Einheit - `Unit.Unitless` statt null -,
  Enum); `EnumType.Intern` ist eine Tabelle nach dem Schlüssel je Übersetzung. Strings bleiben fc8 und werden beim Schreiben UTF-16.
* **Was die Typprüfung hinterlässt** (`ResolveResult`): `UnitChecks`, `LocalGuards` (mit `DeclareLocalType`/`LocalType`/`GuardLocalValue`/
  `GuardValue` und der Liste der lokalen Typen je Scope), `FixedCoercions` (der Plan von `AutoTypeResult`/`AutoCoercionResult`), `OverloadHints`
  (`OverloadHintFor`, `ConstructorHint`, `Assumptions`, `DerivedKeepOverloads`), `MethodTypeArgs` (`TypeArgText` eines Typs), `ReceiverTypeArgs`,
  die Ausdrucksbäume (`Lambdas`, `Types`, `ToCompiled`), `BitsOf`, die bewiesenen Mitglieder von `??.`; dazu die Herleitung von `defined`
  (`NoteCall` mit allen Argumenten, `NoteReturn`, `PlainParams`, `DeriveReturns`, `DerivationCandidates`, die Felder mit `SetBeforeRead`,
  `BaseConstructionSafe`, `FieldsOf`, `FieldNeed`, `NoteFieldWrite`, `DisqualifyFields`), `SetterClaims.Sites`. `FIRE_NO_DERIVE=1`,
  `FIRE_INFER_UNINITIALIZED_DEFINED=0`, `FIRE_NO_OVERLOAD_HINTS=1` und `FIRE_ANCHOR_AT_RUNTIME=1` wirken wie in C#.
* **Der Linker** (`fcLinkFile`): die Natives in der Reihenfolge der `NativeRegistry` (`natives.def` = `CreateDefault`, dann die Brücken von
  `bridges.def` und die Funktionen der Pakete beim Einfügen der Preludes - ein Name, der schon da ist, behält seinen Index nicht: wie
  `Register`), `NativeImports` in der Reihenfolge der `#import`, `PackageNatives`/`PackageNativeLibraries` (`NativesOf`: die Schlüssel ordinal
  sortiert, `@host` oder `libfire_pkg_<paket>_<import>.so`), `GraphicsNatives` null (ohne `-o` eines gepackten Programms), die Ressourcen mit
  ihren Bytes, die eigenen Einheiten, die Quellen (nur die Datei hat Pfad und SHA-256). Nicht portiert: Projekte (`BuildPlan`, Bibliotheken,
  `CheckPackageReferences`), Ziele (`target`), `-D`, `OwnImports`, `ClassMeta` für Plugins, das Packen (`-o` eines Programms mit der VM),
  `ModelConverter` (eine Ressource `.gltf`/`.fblocks`/`.fmesh` wird nicht umgewandelt).
* **Die Paketverwaltung**: gelesen wird der Ordner der installierten Pakete (`package.json` je Paket, die Preludes als Dateien); `.fpk` (packen,
  entpacken) braucht `forge bytecode` nicht - das ist Phase D Schritt 3.

### Schritt 5

* **Was portiert ist**: `MarkupModel.cs`, `MarkupSchema.cs`, `MarkupParser.cs`, `FireUiGenerator.cs` (mit `GeneratePreview`, das kein Befehl
  zeigt), `BiscuitGenerator.cs` (mit `MarkupFiles`), `DataModelGenerator.cs`, `FalstadImport.cs` - Stand 7a594769. Nicht portiert, weil der
  Generator sie nicht braucht: `DataModelDocument.cs` (das Modell des Designers in spark), `MarkupText.cs` und `MarkupCompletion.cs` (Editor,
  Completion: mit `forge serve`, Schritt 6), `CircuitSamples.cs` (Beispiele des Editors). Die Dateien liegen flach in `native/forge` (nicht in
  `markup/`), wie die übrigen.
* **System.Xml und System.Text.Json** sind kein C# des Repositorys, also kein Port, sondern ihr Verhalten: `xml.c` und `json.c` lesen, was das Markup
  braucht, und schreiben bei Fehlern die Meldungen von .NET samt Zeile und Position - abgeglichen an den beschädigten Kopien und eigenen Fällen
  (abgeschnitten in Name, Attribut, Wert, Kommentar, CDATA, Endtag; falsche Token, Entities, Zeichenreferenzen; JSON: Literale - .NET zeigt den ganzen
  Rest -, Zahlen, Kommas, Kommentare, Ende). Nicht nachgebaut: eine DTD mit eigenen Entities, Kodierungen außer UTF-8, die volle Klasse der
  Namenszeichen von XML (Buchstaben, Ziffern, `_ - . :` und ab U+00C0).
* **Texte**: das Markup wird als UTF-16 (`fcReadText`) gelesen, der XML-Leser arbeitet auf Einheiten (Spalten wie .NET), JSON auf UTF-8 (wie
  `JsonDocument.Parse(string)`: `BytePositionInLine`), alle übrigen Texte sind fc8. Groß/klein (`OrdinalIgnoreCase`, `ToLowerInvariant`,
  `char.IsUpper` von `Snake`) für ASCII und Latin-1; `char.IsLetter`/`IsDigit` wie bisher aus `unicode.inc`.
* **Zahlen**: `double.ToString("0.0###############")` und `"0.######"` über 15 signifikante Stellen (wie .NET bei eigenen Formaten), aufgerundet
  ab 5; `Math.Round(v, 6)` wie .NET (mal 10^6, gerade runden, geteilt); `(int)` eines double sättigend (.NET 9).
* **Ausnahmen**: `MarkupException` ist `FC_ERR_MARKUP` mit den Diagnosen in `err.list`; `#include` macht daraus wie C# einen Fehler des
  Präprozessors (`'x.fxml': line n: ...`). Was C# nicht fängt (`TryGetInt32`/`GetString` auf einem Wert anderer Art in `.fdata`) beendet
  `forge-c` mit der ersten Zeile, die .NET schreibt (`Unhandled exception. System.InvalidOperationException: ...`), ohne Stack.
* **Kein globaler Zustand**: `[ThreadStatic] s_scope` (die Namespaces beim Lesen) und `s_preview` (die Vorschau) gehen als Parameter durch.

## Phase D Schritt 1: der Generator

Der Generator Bytecode -> C ([ROADMAP_C_PORT.md](../ROADMAP_C_PORT.md#phase-d---backend-und-werkzeuge-in-c), Schritt 1) in `native/forge/gen/`, Stand
808e9788 von `src/fire.Native`: dasselbe gelinkte Programm (`FcLinked`, wie `forge-c fbc` es schreibt) geht durch denselben Generator wie in C#,
1:1 (L3) - die Runden bis zum Fixpunkt (`_version`), die zwei Durchgänge je Funktion, der Fluss (`Flow`) mit seinen Kanten, die Schatten der
Slots als C-Zahlen, das Weglassen, die Tabellen. In derselben Übersetzungseinheit wie das Frontend (`forge.c` bindet `gen/*.c` nach `linker.c` ein).

```
forge cgen <optionen> x.fire                         (C#: das C von `forge native` - fire_program.c, dann `//==== fire_natives.cpp` und je Einheit `//==== name`)
forge-c cgen <optionen> x.fire                       (dasselbe aus dem Generator in C; `--list <dateien.txt> --out <ordner>` wie bei `fbc`)
tests/compare-frontend.sh <forge> <forge-c> native [<ordner>]   (NATIVE_OPTIONS="default size i32 t:esp32+size ...": die Optionssätze;
                                                     NATIVE_STRICT=1: `unported` zählt als Unterschied)
```

Die Optionen sind Wörter, mit `+` verbunden: `default`, `debug`/`release`/`performance` (der Modus), `split` (`"split": "on"`), `speed`/`size`
(`optimize`), `i16`/`i32` (`#intwidth`), `f32` (`#floatwidth`), `strip0` ... `strip3`, `t:<ziel>`. Beide Seiten bauen mit dem Werkzeugsatz
`files` und einer frischen `NativeConfig`. Fehler: `error` und die Diagnosen; `unsupported: ...` (C#: `NativeNotSupportedException`, in C
`FC_ERR_NATIVE`); `unported: ...` nur in C - ein Teil, der noch nicht portiert ist (das Skript zählt ihn getrennt).

### Stand

| Optionen | Quellen | gleich | noch nicht portiert | verschieden |
|---|---|---|---|---|
| `default`, `tests/cases` | 1129 | 1056 | 73 | 0 |
| `default`, `packages` | 93 | 92 | 1 | 0 |
| `default`, `src/fire.Project` | 34 | 30 | 3 | 1 (`Scene3D Models`: die Modelle als Ressourcen - der Linker, siehe oben) |
| Stichprobe, `default` / `performance` / `size` / `i32` / `f32` (je) | 504 | 473 | 30 | 1 (`Scene3D Models`) |
| Stichprobe, `strip0` | 504 | 455 | 48 | 1 (`Scene3D Models`) |
| Stichprobe, `t:esp32` | 504 | 488 | 16 | 0 |
| Stichprobe, `t:dos16` (Zeigerkonvention) | 504 | 487 | 16 | 1 (`Scene3D Models`) |
| Stichprobe, `t:win16` (Zeigerkonvention) | 504 | 475 | 28 | 1 (`Scene3D Models`) |
| Stichprobe, `strip2`, `t:windows`, `t:wasm`, `i16` (Stand vor JSON und `Library.Get`) | 504 | 455 / 473 / 475 / 473 | 48 / 30 / 28 / 30 | 1 (`Scene3D Models`) |
| Stichprobe, `debug`, `split` (Stand vor Reflection und Debug-Builds) | 504 | 131 (die Fehler des Frontends) | 373 | 0 |
| `default` mit `NATIVE_STRICT=1` (Reflection, Probes, Selektoren portiert), `tests/cases` / `packages` / `src/fire.Project` | 1129 / 93 / 34 | 1129 / 93 / 33 | 0 | 0 / 0 / 1 (`Scene3D Models`) |
| `debug` und `split` mit `NATIVE_STRICT=1`, dieselben (je) | 1129 / 93 / 34 | 1129 / 93 / 33 | 0 | 0 / 0 / 1 (`Scene3D Models`) |
| die 73 Fälle der Reflection und `fire-reflection` mit `strip0` / `strip1` / `strip2` | 74 | 74 / 74 / 74 | 0 | 0 |

(Die Stichprobe: jede dritte Quelle von `tests/cases` und alle von `packages` und `src/fire.Project`, 504 Quellen.)

Portiert seit dem Stand oben: die Reflection (`CppGenerator.Reflection.cs` in `gen/reflection.c`: die `__refl_*`-Natives, die Tabellen `RfClass`/`RfMember`
mit Attributen und den weggelassenen Membern, `Reflect.Get/Set/Call/New`, die Selektoren der Lambdas, `probe`/`silence` mit den Settern, die Probes suchen,
`RuntimeClass.ComputedSources`), Debug-Builds (`CppGenerator.Debug.cs` in `gen/debug.c`: Schattenstapel, `FIRE_DBG_L/J`, Ebenen der Gültigkeitsbereiche,
die Tabellen des Programms), `--stats` (`CppGenerator.Stats.cs` in `gen/stats.c`, [unten](#phase-d-schritt-2-der-builder) der Bericht). Dabei gefunden: `RegisterJsonTargets`
las in C den Code einer Funktion, die in dieser Runde erst angelegt, noch nicht erzeugt war (C#: `Func.Decoded` ist bis dahin leer) - in ruhrpott-raser
(`Raser.Viewer`, `Raser.Game`) vertauschte das die Nummern zweier Klassen.

### Die Module (linkage static/shared, `FIRE_MODULES=1`)

Portiert (Stand 661e55b3): `CppGenerator.Modules.cs` (`gen/modules.c`: `ModulesOf`, `LibraryView`, `GenerateModular`, `GenerateLibrary`, `SetModules`,
`ReachLibraryClasses`, `CollectNeeds`/`AddLibraryNeeds`, `ModuleTables`, `LibraryDeclarations`, `KeepOwnModule`, `ShareReflection`, `LibraryBody`,
`UnsupportedStub`), `CppGenerator.ModuleDispatch.cs` (`gen/moddispatch.c`: die Dispatcher `disp_`/`miss_`/`call_`/`mt_` je Modul, die Feldhelfer
`fget_`/`fset_`/`fptr_`, `is of`, Operatoren, Klassenpunkte, Typargumente, die Tabellen `FireMembers`, die Spalten der Deskriptoren, `ModularHelpers`; dazu
`TableSlotOf` aus `Packed.cs`), `StaticLinkage.cs` im Generator (`gen/staticlinkage.c`: die Natives in C im File ihres Moduls, `fire_cn_`, `LibraryKey`,
`AbiHash` = `ModuleInterface.Hash`, die Schnittstellendateien `.fireiface` - geschrieben wie `System.Text.Json`, gelesen beim Treffer im Cache -,
`GenerateStatic`, die DLLs von Windows: `HostDecl`, `HostArray`, die Link-Funktionen, `HostTable`, `RuntimeDllC`) und von `Plugins.cs` die Seite des
Programms (`gen/plugins.c`: `PluginHostCode`, `HostInfo`/`.firehost`). Die Rolle (`Modular`, `LibraryRole`, `Px`) ist jetzt die des Generators, die Zweige
der Module in den Schritt-Dateien, der Reflection (`rf_classes`, `rf_new`, `rf_find` mit `kRfClassList` und den Klassen der Plugins der offenen Welt),
`Packed.cs` (`RawTypeAt` nach der Deklaration), `Overloads.cs`, `Json.cs` (die Engine im File eines Moduls) und `Debug.cs` (`DbgSrc`) wie in C#. Der Linker
gibt dem Programm `ModuleDeps` (ohne und mit Plan) und `SourceTextHashes` mit. Seit 22.11.2026 auch der Generator eines Plugins, siehe
[linkage plugin](#linkage-plugin-der-generator-eines-plugins-und-buildplugin).

```
forge cgen modules|static|shared <optionen> x.fire  (FrontendDump.DumpModules: `modules` - GenerateModular, die eine Datei; `static`/`shared` - GenerateStatic
                                                     in einen leeren Cache: das Programm, seine Natives, je Bibliotheksmodul `//==== library <modul> <name> <schlüssel>`
                                                     mit seinem C und `//==== interface` mit seiner Schnittstelle, `//==== log`, bei shared `//==== host`)
forge-c cgen modules|static|shared <optionen> x.fire (dasselbe aus dem Generator in C)
tests/compare-frontend.sh ... native                 (NATIVE_OPTIONS="modules static shared debug+static ..."; FRONTEND_LIST=<liste>: nur diese Quellen)
```

Die Schlüssel des Caches nennen den Generator (C#: `GeneratorIdentity`, die Version seiner Assembly; forge-c: die Zeit, zu der es übersetzt wurde): bei den Messungen
ist es der feste Text `cgen` auf beiden Seiten, sonst hat jedes forge seine eigenen Bibliotheken im Cache (wie zwei Versionen des C#-forge).

| Messung (mit `NATIVE_STRICT=1`) | Quellen | gleich | verschieden |
|---|---|---|---|
| `cgen modules`, `static`, `shared`: die Stichprobe (jeder 10. Fall von `tests/cases`, die ersten 32 von `packages`, alle von `src/fire.Project`) | 179 je Wort | 179 / 179 / 179 | 0 |
| `cgen modules`, `static`: `packages` (vor zwei Korrekturen: `CollectPackages` jedes Generators, die JSON-Engine im File eines Moduls) | 93 | 93 | 0 |
| `tests/cases/smoke` mit `debug+static`, `strip0+static`, `i32+modules`, `t:esp32+static`, `performance+shared` | 58 je Satz | 58 | 0 |
| `buildplan static`, `shared`, `tc:gcc+static`, `tc:gcc+shared`, `tc:gcc+quick+static` (jede zweite Quelle der Stichprobe bis 58 und jedes dritte Paket) | 40 je Satz | 40 | 0 |

Echte Builds (beide forges, eigener Cache je forge über `FIRE_NATIVE_CACHE`; Protokoll nach dem Normalisieren von Zeiten, Schlüsseln und Pfaden, Ausgabe der Programme): das
CLI-Programm von LinkageTests (json, linq, hash, compression) mit gcc static (zweimal - der zweite Bau nimmt jede Bibliothek aus dem Cache) und shared (die `.so` und
`.firehost` neben dem Programm), mit tcc shared (Rückfall auf static mit der Zeile im Protokoll), die Solution von LinkageTests (zwei Bibliotheken, eine Klasse des Programms
erbt von einer der Bibliothek; dann eine Änderung des Codes und der Schnittstelle von `Lib`) - die Programme byte-gleich -, der Rückfall eines Programms mit eigener Klasse
`Exception` auf source, das kleine JSON-Programm mit tcc static, `--stats`, `-m DEBUG`, `--quick`, `--toolchain files` (der Ordner gleich), MinGW für Windows static und
shared (die DLLs der Laufzeit und der Module; die `.exe` bis auf den Zeitstempel des PE-Kopfs gleich - Wine startet hier auch die des C#-forge nicht): Protokoll und Ausgabe
gleich. Dazu jeder 40. Fall von `tests/cases` mit gcc static (28): Protokoll und Ausgabe gleich, 21 drucken die `.expected`, 4 übersetzen in beiden nicht (wie verlangt),
3 weichen in beiden forges gleich von der `.expected` ab (zwei Fehler des C#-Generators mit Modulen, unten - korrigiert; `vm-sc/0280-einheiten-...` erwartet das Präfix `UNBEHANDELT:` des Läufers der VM - auch mit linkage source).

Gefunden und in beiden forges korrigiert (mit den Modulen - static, shared, `FIRE_MODULES=1` -; mit linkage source waren sie richtig):
* `g_arrClasses`: eine Bibliothek nummerierte ihre Arrays einer Klasse selbst, die Laufzeit las die Nummern in der Tabelle des Programms -
  `.../3360-circuits-potentiometer-transformator-jk-flipflop.fire` (`An element of an array of Expression must be an Expression (or undefined), not String`: das
  `string[]` von `Split` in core), `.../4140-data-sqlite-execute-query-zeilen-nacheinander-ro.fire` (`An index must be an int, not String`: die Elemente der
  `List<Data.Row>` von data wurden zu Texten). Jetzt entscheidet das Programm die Einträge (`ModuleNeeds.ArrClasses`, `fire_arrc_M[k]`; in `TArg` und `JsonFieldInfo`
  einer Bibliothek bleibt ihre Nummer, `arrMap` nennt die Tabelle). Danach in 4140: `QueryList("Person", ...)` - was data als JSON liest, sagt ihre Schnittstelle
  (`JsonReads`), das Programm behält die Klassen wie mit einer Datei (sonst `Class 'Person' was left out of the build`). Schnittstellenformat 3.
* Das CLI-Programm von LinkageTests stürzte mit TinyCC und linkage static ab: ohne Thread-lokalen Speicher hängt der Zustand eines Threads an einem Schlüssel von
  pthreads, `FIRE_ONCE` war `static` - jedes Modul hatte seinen eigenen. Die Module in Dateien für sich (nicht DLL) bekommen `FIRE_SPLIT` (der Schlüssel lebt in
  der Datei des Programms).
* Beim Lauf von `tests/cases` mit gcc static (unten): `Reflect.Set` auf ein `byte`/`word`-Feld machte keinen Puffer (1650); die Typargumente einer Deklaration
  verglich der Guard nach der Adresse des TArg, die je Modul eine andere ist (2630, `Dictionary<string, string> Environment` von process); der alte Wert einer
  Probe auf einem Objekt einer Klasse eines anderen Moduls war `undefined` (3212: eine Klasse des Programms erbt von `UI.Button`, `UI.Root.Update` malte immer);
  ein fire-Thread im Code einer Bibliothek (http, net.channel, data Pool) wurde zu einem Stub `uses a global variable of the program` (seine Slots sind
  Variablen des Threads).
* Plugins (damals nur C#; der Generator eines Plugins ist seit 22.11.2026 auch in C): ihre Arrays einer Klasse bekommen beim Registrieren die Einträge des Programms gleichen Namens,
  neue werden angehängt (`FirePlugin.narr`/`arrClasses`/`arrMap`, `FIRE_PLUGIN_FORMAT` 6; das Programm, das Plugins lädt, setzt `g_arrClassCount`).
  LinkageTests (`FIRE_TESTS_ONLY=linkage`, neu `FIRE_TESTS_FILTER=boundaries`): alles grün bis auf die Prüfungen unter Wine (Wine bricht hier bei jedem Programm mit
  `free(): invalid pointer` ab).

`tests/cases` mit gcc static (1109 Fälle mit `.expected` ohne `.error`, ohne `skip`/`output: varies`; ein Fall gilt wie bei `tests/run-cases.sh`, ohne `--types
strict`): 951 drucken die `.expected`, 135 übersetzen in beiden Linkages nicht (134 mit `.diagnostics`, `vm-sc/0050` mit eigener Klasse `Exception`), 8 sind
Fälle von `//# types: strict` (ohne die Option gelaufen), 12 weichen auch mit linkage source ab (Meldungen der VM in `vm-*`, Schriften in 3210/4110). Nur mit den
Modulen wichen 3 ab - die Module behielten die GIL (fire_rt.h, `FIRE_NO_GIL` nicht mit `FIRE_MODULES`): `native/1810` (600 fire-Threads, richtig, aber 5 Minuten),
`native/3540` und `native/4424` (ein Thread läuft nicht, während das Hauptprogramm in Natives ist).

**Die Module ohne die GIL.** Ausgeschlossen waren sie, weil zwei Stücke nur das Programm aus einer Datei hatte (ROADMAP_TASKS 1g): die Sperren der Pakete
(`fire_pkgM[i]`, vom Programm nummeriert - `PkgLock` gab mit Modulen -1) und `fire_shareGlobals` (das Programm teilte beim ersten `fire` nur seine eigenen Globals).
Eine Bibliothek ist für sich gebaut und über Programme hinweg im Cache: sie kann die Pakete nicht für das Programm nummerieren. Jetzt (beide Generatoren gleich):
* die Sperre eines Pakets ist die der Laufzeit nach seinem Import-Schlüssel - `FIRE_PKG_ENTER(FIRE_PKG_NAMED(<modul>fire_pkgL[i], "pkg:io"))`; jedes Modul (Programm,
  Bibliothek, Plugin) hat `static fire_atomic32 <Px>fire_pkgL[n]` (nur mit einem Native unter einer Sperre), der erste Aufruf holt die Nummer aus `fire_pkgLockIndex`
  (Tabelle `g_pkgLocks`, unter `g_registryM`); die Bibliotheken brauchen dafür nichts vom Programm (keine neue Spalte der Schnittstelle, kein Symbol des Programms);
* `fire_shareGlobals` des Programms aus Modulen teilt seine Globals und die statischen Felder aller Module (`SFx_...`, aus den Bedürfnissen der Module), `main` setzt
  `g_domShareGlobals` wie mit einer Datei;
* TinyCC: die Slots von `FIRE_TLS_EMULATED` auch mit Modulen (der Schlüssel einmal: `FIRE_ONCE` mit `FIRE_SPLIT`, wie schon für die GIL);
* in der Laufzeit, was ohne die GIL ungeschützt war (auch mit einer Datei): `fieldPtrId` (die Deskriptoren der Feldzeiger: `realloc` der Tabelle, während ein anderer
  Thread sie liest) unter `g_registryM`, die alte Tabelle bleibt; `fireRegisterPlugin` unter `g_pluginM` (das `init` des Plugins danach).
Mit der GIL bleiben Debug-Builds (die DLLs von Windows seit unten nicht mehr).

| Messung | Quellen | gleich |
|---|---|---|
| `cgen default`, `modules`, `static`, `shared` (`NATIVE_STRICT=1`): die ersten 60 Thread-Fälle (`fire`, `Channel<`, Actors) | 60 je Wort | 60 |
| `cgen modules`, `static`, `shared`: die übrigen Thread-Fälle mit 1810, 3540, 4424 (die ersten 60 davon; `modules` auch die nächsten 8 und 15) | 60 je Wort (`modules` 83) | alle |
| `buildplan static`, `shared`, `tc:gcc+static`, `tc:tcc+static`: 1810, 3540, 4424 und jeder 12. Thread-Fall | 18 je Satz | 18 |

Echte Builds (198 Fälle: alle mit `fire`/Threads/Actors/Channels und die mit `#import "http"`/`"net..."`; 145 aus `native`, 53 aus smoke, syntax, trace, types,
vm-*): 1810 0,1 s (vorher 5 Minuten), 3540 0,01 s, 4424 0,6 s mit gcc static und shared, mit TinyCC static 0,1 / 0,04 / 3,4 s.

| Linkage | `native` (145) | der Rest (53) |
|---|---|---|
| gcc static | 145 | 42 - dieselben 11 wie mit source (die Meldungen der VM, `types: strict`, `.diagnostics`, ohne `.expected`) |
| gcc shared | 144 (`native/3840`) | 42 - dieselben, statt `vm-shutdown/0040` `vm-life/0100` |
| TinyCC static | 144 (`native/3840`) | 43 |
| gcc source (das Programm in einer Datei, gegen Rückschritte) | 122 von 122 gelaufenen | 42 |

`vm-shutdown/0040` und `vm-life/0100` sind Wettläufe der Fälle selbst (ein Thread gegen eine Schleife des Hauptprogramms), mit source genauso (0100 unter Last
0 von 40, wie shared). `native/3840` (`IChannel<T>` mit `Net.Channel`: `DestroyedException` nach `net.Close()`) ist ein Fehler der Module, den es schon vorher gab:
auch mit der GIL und auf dem Stand vor dieser Änderung (forge-c von `clippy`, gcc static: 0 von 10 Läufen richtig; mit source 10 von 10) - behoben, siehe unten.
LinkageTests (`FIRE_TESTS_FILTER=boundaries`, 11 Prüfungen grün): `ModuleThreads` - ein Thread läuft, während das Hauptprogramm im Native von compression steckt
(mit `-DFIRE_GIL` gebaut nicht: 5 statt 2000 Schritte), und 400 schlafende fire-Threads in 0,06 s, je mit gcc static, shared und TinyCC static; `FIRE_TESTS_FILTER=plugin`: 25 von 29, die 4 anderen die unter Wine (`free(): invalid pointer`, wie vorher).

**`fire with` in einer Bibliothek; die DLLs von Windows ohne die GIL.** `native/3840`: ein Thread, den der Code einer Bibliothek mit `fire with ch` startet, bekam
nicht sein eigenes Ende des Kanals (`__End`), sondern die Referenz des Kanals selbst - `RegisterTargets` einer Bibliothek registriert nur ihre bedienten Ziele (der
Verteiler `__End` fehlte), und `ChannelLikeTest` kannte nur Klassen, die das Modul selbst registriert (`Channel` von core ist in net.channel eine fremde Klasse). Die
Lese- und Schreib-Threads von `Net.Channel` hatten so `inbox`/`writerDone` des Objekts, die mit ihm zerstört wurden. Jetzt (beide Generatoren gleich): ein `Fire` oder
ein Job mit `with` in einer Bibliothek holt den Verteiler (`NoteFireWith`), `ChannelClass` nennt die Klassen von core (`ForeignClass`) - auch wenn die Bibliothek
selbst keinen Kanal macht (ein Kanal des Programms). Dieselbe Art Fehler (ein Ziel aus `RegisterTargets`, das eine Bibliothek nicht bekommt) gab es sonst nicht:
`GetIndex`/`SetIndex`/`GetEnumerator` holt `RegisterServedTargets` selbst, die Klassen der Fehler registriert das Programm. Die DLLs von Windows (`FIRE_DLL`) laufen
ohne die GIL, aber ohne Tasks (fire_rt.h; NATIVE_BACKEND.md "Threads"). Wine: `free(): invalid pointer` kam von Wine selbst (Ubuntu 9.0~repack gibt
`getenv("TMPDIR")` frei) - die Tests nehmen `TMPDIR` aus der Umgebung von Wine; dabei gefunden: `new Net.TcpListener("", 0)` als erster Gebrauch des Netzes
startete Winsock nicht (`native/4251` unter Windows in jeder Linkage: `Winsock error 10093`; jetzt `fire_net_init` in `fire_net_anyAddress`).

| Messung | Quellen | gleich |
|---|---|---|
| `cgen default`, `modules`, `static`, `shared` (`NATIVE_STRICT=1`): die Fälle mit `fire with`, `Channel<`, `net.channel`, `http` | 45 je Wort | 45 |
| `buildplan t:windows+shared+tc:gcc` (die DLLs), `t:windows+static+tc:gcc`: 1810, 3540, 3840, 4424, die zwei Programme von `ModuleThreads` | 6 je Satz | 6 |

Echte Builds unter Wine (MinGW, linkage shared, DLLs ohne die GIL, ein frisches `WINEPREFIX`): 41 Thread-Fälle aus `native` (mit 1810, 3540, 3840, 4424): 41 drucken die `.expected` (4251 nach
der Korrektur von net). `native/3840` gcc static 20 von 20, DLL 10 von 10. LinkageTests: `FIRE_TESTS_FILTER=boundaries` 18 von 18 (neu `ModuleChannelEnds`: eine
Bibliothek ohne eigenen Kanal leitet Kanäle des Programms weiter, deren Bereich vorher endet, und Net.Channel - gcc static, shared, MinGW-DLLs; `ModuleThreads`
auch mit den DLLs unter Wine: der Thread läuft, während das Hauptprogramm im Native steckt, 400 Threads in 0,7 s), `FIRE_TESTS_FILTER=plugin` 29 von 29, `FIRE_TESTS_ONLY=linkage` ganz 97 von 97 (übersprungen nur die VM von Windows mit Fenster: kein `FIRE_TEST_WIN_FORGE`).

### Dateien

| Datei | Inhalt | Gegenstück in C# |
|---|---|---|
| `gen/gen.h` | die Typen: das Ziel (`TargetProfile`, `NativeTarget`), die dekodierte Instruktion, `Func`, `ClassInfo`, `Flow`, `Out`, `Region`, `HEntry`, `StepResult`, `CAbi`, der Generator `FgGen` (die Felder aller Teile der partiellen Klasse) | die geschachtelten Klassen von `CppGenerator`, `OpInfo.Instr` |
| `gen/support.c` | Texte (`Split`, `Join`, die Kultur-Ordnung der `SortedSet<string>`), Mengen von Zahlen, `OpInfo.Decode`, `DeclaredType`, die Fragen an `RuntimeClass` (Felder, Kandidaten, Konstruktoren), `ArrayElem` | `OpInfo.cs`, `Bytecode/DeclaredType.cs`, `Bytecode/RuntimeClass.cs`, `Values/ArrayElem.cs` (Teile) |
| `gen/packages.c` | das Manifest der Natives eines Pakets, `SourcesFor`, `PlatformLayerFor`, `LinkLibrariesFor`, `SupportsAny`, `ReadNativeSources` mit den Headern (`Point`, `Inline`), `StripLocalIncludes`, `StripPragmaOnce`, `UsedImports`; `NativeCTypes` | `Package.Manager/Manifest.cs`, `NativeSources.cs`, `PackageStore.cs` (Teile), `fire.Native/NativeCTypes.cs` |
| `gen/cppgenerator.c` | `CppGenerator.cs` bis zur Zeile 1970: Namen, Funktionen, Spezialisierung, Klassen, Felder, Zugriff, Erweiterungen, Natives der Pakete (`CollectPackages`, `PkgLock`, `BridgeOf`, `AppendStandardNatives`), Einheiten der Anforderungen, die Dispatcher der Interfaces | `CppGenerator.cs` |
| `gen/cppgenerator2.c` | `Run` (die Runden, der Kopf, die Tabellen, die Helfer, `fire_init`, die Einstiegspunkte), Dispatcher, Feldhelfer, Eigenschaften, Klassendeskriptoren, Records | `CppGenerator.cs` 1971-3512 |
| `gen/cppgenerator3.c` | der Fluss, `GenerateChunkCore` (beide Durchgänge, Prolog, Rahmen, Catch-Funktionen), Regionen, Ausgänge, Variablen, die Schatten der Slots | `CppGenerator.cs` 3513-4679 |
| `gen/packed.c`, `gen/cabi.c`, `gen/overloads.c`, `gen/selectors.c` | gepackte Felder; die C-Schnittstelle der Methoden; Überladungen nach Typ, die Selektoren, `OverloadHint` | `CppGenerator.Packed.cs`, `CppGenerator.CAbi.cs`, `CppGenerator.Overloads.cs`, `Bytecode/Overloads.cs` (`OverloadHint`) |
| `gen/misc.c` | Konstanten (`Constant`, `DoubleLiteral` wie `"R"` von .NET, `CString`), `UnitId`, Prototypen, die Thunks von `Library.Get` (mit `LibraryMethods.Parse`), die Zeigerkonvention der Open-Watcom-Ziele (`PointerFunction`, `PointerCallsPass`), `Nested` | `CppGenerator.cs`, `PointerCalls.cs`, `Modules.cs`, `Debug.cs`, `Split.cs` (Teile), `Standard/LibraryMethods.cs` |
| `gen/reflection.c` | die Reflection: die Stufen (`UseReflection`, `UseReflectionLevel`, `UseProbes`), was sie erreicht (`RegisterReflectionTargets`, `RegisterKeptTargets`, `ReachedClasses`, `ReflectionTableClasses`), die Tabellen (`ReflectionRows` mit Attributen aus `ArgsJson`, `StrippedMethod`, `NewCases`), `ReflectionCode` (die `rf_*`-Funktionen), `WrapProbeSetter`, `ProbeCode`; `ComputedSources` | `CppGenerator.Reflection.cs`, `Bytecode/RuntimeClass.cs` (`ComputedSources`) |
| `gen/debug.c` | Debug-Builds: `FuncDebug`, `DebugBegin`, `DebugLevelOf`, `DebugBeforeInstruction`/`AfterInstruction`, Prolog, Tabellen, Wrapper (auch der Catch-Blöcke), `DebugFunctionName`, `IsHiddenFunc`, `DebugProgramTables`; `PlaceOf` | `CppGenerator.Debug.cs`, `CppGenerator.Packed.cs` (`PlaceOf`) |
| `gen/trace.c` | Stack-Traces ([STACKTRACE.md](STACKTRACE.md) 13): `TraceNames` (die Namen der Funktionen), die Deskriptoren `kFI_fN`, die Zeilen (`TraceBeforeInstruction`), `TraceCannotThrow`/`TraceNoteFrameless` (kein Frame für eine Funktion, die nicht werfen kann), `TraceReturns` (jedes `return` nimmt den Frame ab), `TraceStartAt` (die Zeile eines Threads) | `CppGenerator.Trace.cs`, `Bytecode/TraceNames.cs` |
| `gen/stats.c` | `--stats`: `FuncStat`, `StatOf`, `NoCAbiReason`, `FuncStats` | `CppGenerator.Stats.cs`, `NativeStats.cs` (`FuncStat`) |
| `gen/json.c` | JSON: die Ziele des Lesens, die Tabellen der Klassen und Felder, die Typargumente | `CppGenerator.Json.cs`, `Standard/JsonMethods.cs` (`JsonFieldsOf`) |
| `gen/cppgenerator4.c` ... `gen/cppgenerator8.c` | `StepCore`: seine lokalen Funktionen über dem Zustand eines Schritts (`FgSC`), `CFields`, dann der `switch` in der Reihenfolge der C#-Datei (Stapel, Variablen, Natives; Rechnen, Einheiten, Vergleiche, Sprünge; Aufrufe, Felder, `new`; Lambdas, Wächter, Arrays, Besitz, Zeiger, Ausnahmen, Threads) | `CppGenerator.cs` 4680-8555, `CppGenerator.CFields.cs` |
| `gen/native.c` | die Ziele, `NativeBuilder.GenerateLinked` mit `files` (das Linken mit den Symbolen des Ziels, `ModuleDependencies`, `SourceTextHashes`), `forge-c cgen` (auch `modules`/`static`/`shared`) | `NativeConfig.cs`, `Runtime/TargetProfile.cs`, `NativeBuilder` (Teil), `Linker.cs` (Teil), `FrontendDump.cs` (`DumpNative`, `DumpModules`) |
| `gen/modules.c` | die Module: Rolle, `ModuleOf`, `ProtoKey`, `ExportName`, `ForeignClass`, `DbgSrc`, `CurrentFlags`/`ApplyForcedFlags`, `ModulesOf`, `LibraryView`, `GenerateModular`, `GenerateLibrary`, `SetModules`, `ReachLibraryClasses`, `CollectNeeds`, `AddLibraryNeeds`, `ModuleTables`, `LibraryDeclarations`, `KeepOwnModule`, `KeepsPrivate`, `ShareReflection`, `LibraryBody`, `UnsupportedStub` | `CppGenerator.Modules.cs` |
| `gen/moddispatch.c` | jedes Modul bedient seine Klassen: `Served`, `ExtServed`, die Tabellen, `RegisterServedTargets`, `ModDispatcher`, `ModFieldHelpers`, `ModIsOf`, `ModOperator`, `ModClassScore`, `ModTArgOf`, `MemberTables`, `ModularDescColumns`, `LibraryClassTable`, `ModularHelpers`; `TableSlotOf` | `CppGenerator.ModuleDispatch.cs`, `CppGenerator.Packed.cs` (`TableSlotOf`) |
| `gen/staticlinkage.c` | linkage static/shared im Generator: `NativesOwner`, `LibraryNativesC`, `CnFunction`, `SharedNative(Thunks)`, `AbiHash`, `NativeSourcesHash`, `LibraryKey`, die Schnittstellendatei (schreiben, lesen, `ProgramFile.WriteUnit`/`ReadUnit`), `GenerateStatic`, die Prüfung der Bibliotheken beim Start, DLLs | `StaticLinkage.cs`, `src/fire/Runtime/ModuleInterface.cs` (`Hash`), `NativeCTypes.cs` (`ValueWrapper`) |
| `gen/plugins.c` | die Seite des Programms: `PluginHeadKey`, `PluginHostCode`, `HostInfo` (`.firehost`, eingerückt wie `System.Text.Json`, gelesen: `fgHostInfoRead`); der Generator eines Plugins: `PluginDeclarations`, `RegisterPluginTargets` (mit `StaticInitSequence`), `PluginTrailer`, `PluginDebugTables`, `PluginUnits`, `PluginJson`, `PluginExtensionProblem`, `PluginNativesCpp`, `GeneratePlugin` | `CppGenerator.Plugins.cs`, `CppGenerator.Json.cs` (`PluginJson`) |

### Entscheidungen

* **Die Reihenfolge der Auswertung**: in C ist sie bei den Argumenten eines Aufrufs offen, in C# von links nach rechts. Jede Interpolation, in der
  mehr als ein Teil etwas tut - `S`/`SR`/`T` (ein Schatten wird zum Value, eine Zeile wird geschrieben), `Constant` (die Reihenfolge der
  Stringtabelle), `UnitId`, `GetFunc` (die Nummern der Funktionen), `RegisterClass`, `TArgRef`, `ExitLabel`, `OwnerList` -, geht in C über lokale
  Variablen in der Reihenfolge der C#-Interpolation.
* **Die Schritt-Dateien** schreiben den Zustand eines Schritts wie C# (`d`, `f`, `st`, `ins`, `sb`, `locals`, `g` sind Makros über `x`; `E(...)`,
  `S(k)`, `Need(n)`, `Next()` wie die lokalen Funktionen); am Ende von `cppgenerator8.c` werden sie aufgehoben.
* **Was eine Bedingung in einem Kommentar hat**, zählt nicht: in `StepCore` steht `&& !(ins.Op == OpCode.CallMethod && CAbiMethodCallee(...) != null)`
  hinter `//` - der C-Code lässt es weg (mit einem Kommentar an der Stelle).
* **Ordnungen von .NET**: `Dictionary`/`HashSet` in Einfügereihenfolge (`dict.c`), `SortedSet<string>` und Tupel mit Texten in der Ordnung der
  Kultur (`fgCultureCmp`), `StringComparer.Ordinal` mit `strcmp`.
* **Die Pakete**: das Manifest wird für den Generator noch einmal gelesen (`linker.c` behält nur, was der Linker braucht); die regulären Ausdrücke von
  `NativeHeaders` und `PackageStore` sind Zeile für Zeile ausgeschrieben. Jedes Paket des Stores liegt im Store (`IsInStore`): seine Header werden
  nach `pkg/<import>/` gezeigt, nicht eingesetzt.
* **Warnungen**: einige Helfer sind schon da und noch unbenutzt (`-Wunused-function`).
* **Die Identität des Generators** in den Schlüsseln des Caches (`LibraryKey`, `RuntimeDllKey`, `PluginHeadKey`): in C# `GeneratorIdentity` (die Version der Assembly,
  für die Messung ein fester Text), in forge-c `fgGeneratorIdentity` (`"forge-c " __DATE__ " " __TIME__`). Ein forge nimmt so nie eine Bibliothek des anderen
  aus dem Cache - ihr C ist gleich, aber die Schnittstellendatei (mit dem Schlüssel) gehört zu ihrem Generator.
* **Die Mengen der Module** (`SortedSet` mit Tupeln und dem Standardvergleich) sortieren Texte in der Ordnung der Kultur, die mit `StringComparer.Ordinal` ordinal
  (`fgSortedNameArgc`, `fgSortedStaticFieldKeys`, `fgSortedKeys`); `MadeBy` (ein `SortedDictionary`) wird nach dem Besitzer geordnet in die Schnittstelle geschrieben.
* **Die Natives der Reflection** werden in der Reihenfolge der C#-`switch`-Arme ausgewertet: `S(...)` nur im passenden Arm, die Liste (`OwnerList`) davor.

## Phase D Schritt 2: der Builder

Der native Builder ([ROADMAP_C_PORT.md](../ROADMAP_C_PORT.md#phase-d---backend-und-werkzeuge-in-c), Schritt 2) in `native/forge/build/`, Stand 6f21d1a4 von
`src/fire.Compiler/NativeBuilder*.cs` und `src/fire.Native`: was `forge build <datei> --engine native` mit linkage `source` tut, 1:1 (L3) - die Toolchain der
Maschine oder der Konfiguration, die Richtung (`WithDirection`), das Programm für die Art der Toolchain (ihr Symbol für `#if`), der C++-Ersatz für TinyCC, die
Dateien des Builds (Laufzeit, Plattformpaket mit dem, was es einbindet, die Header der Pakete unter `pkg/`), die Befehlszeilen, die geteilten Einheiten
nebeneinander (pthreads, `FIRE_BUILD_JOBS`), der Schnellbuild (`--quick[=0|1]`: das Programm mit -O0/-O1, die Natives der Pakete in eigenen Einheiten mit
dem -O2 der Toolchain) und der Programm-Cache (`ProgramCache`: derselbe Schlüssel wie in C#, ein Programm, das das C#-forge gebaut hat, nimmt forge-c aus dem
Cache und umgekehrt). Dazu im Generator die Teilung in Einheiten (`ProgramSplit.cs`, `CppGenerator.Split.cs`, `gen/split.c`) - ohne sie gibt es keinen
Schnellbuild und keinen großen Build mit gcc/clang (`auto` teilt ab 2 MB C).

```
forge-c build x.fire [--engine native] [-o <programm>] [-t <ziel>] [--toolchain <name>] [--quick[=0|1]] [-m <modus>] [--split auto|on|off]
        [--optimize speed|size] [--stripping 0-3] [--keep-all] [--keep] [--config <fire.native.json>] [-f 32]
                                                     (wie `forge build ... --engine native`: das Protokoll auf stdout, die Warnungen und Fehler auf stderr)
forge buildplan <optionen> x.fire                    (C#, FrontendDump.DumpBuildPlan: was der Build ausführen würde)
forge-c buildplan <optionen> x.fire                  (dasselbe aus dem Builder in C; `--list <dateien.txt> --out <ordner>` wie bei `cgen`)
tests/compare-frontend.sh <forge> <forge-c> buildplan [<ordner>]   (BUILDPLAN_OPTIONS="default tc:gcc+quick ...")
tests/compare-frontend.sh <forge> <forge-c> build [<ordner>]       (echte Builds einer Stichprobe: BUILD_EVERY=25, BUILD_TOOLCHAINS="gcc tcc", BUILD_FLAGS)
forge-c pkglib <import>... | --all | graphics+windows                (die Bibliotheken der Pakete für die VM: gefunden oder jetzt gebaut, ein Pfad je Zeile)
forge pkglibplan <import> / forge-c pkglibplan <import>              (was PackageLibrary bauen würde: Datei, Schlüssel, Toolchain, Befehl, Dateien des Ordners)
tests/compare-frontend.sh <forge> <forge-c> pkglib [<ordner>]       (`pkglibplan` jedes Imports mit Natives im Store und von graphics+windows)
forge bootimage <exe> / forge-c bootimage <exe>                      (WatcomToolchain.AfterLink von bios16 auf einer eigenen Datei: <exe>.img daneben)
forge peres <x.fire> / forge-c peres <x.fire>                        (die PE-Ressourcen eines nativen Builds für Windows: Subsystem, VS_VERSIONINFO, Icon)
forge toolchaintest <toolchain> / forge-c toolchaintest <toolchain>  (NativeBuilder.TestToolchain; eine fire.native.json im Ordner gilt)
```

Die Werkzeuge des Builders (Stand de45e7e4): **`PackageLibrary`** - die Bibliotheken der Natives der Pakete für die VM (C und C++, typisierte Funktionen, `reset`,
`host`-Funktionen ausgelassen, die gemeinsame Bibliothek von graphics und windows `libfire_gfx`), `Locate` (vorgebaut im Paket, gebaut in `lib/<rid>/` oder im
Temp-Ordner und nicht älter als die Quellen, unter `Prebuilt/` neben forge-c mit `.hash` = `StandardSources.NativeHashOf`), der Schlüssel der Laufzeit
(`.runtime`: `NativeRuntimeFiles.Fingerprint` und der Wrapper), `Ensure` mit dem gemerkten Fehlschlag (`.failed`, 5 Minuten), `FIRE_PKGBUILD_KEEP`. Dieselben
Dateien wie die des C#-forge: eine Bibliothek, die das eine gebaut hat, nimmt das andere. **Das Boot-Image** von bios16 (`Fat12Image`, `BiosBootSectors`,
`WatcomToolchain.AfterLink`: nach dem Link, wie in C#). **Die PE-Ressourcen** (`PeResourceEditor`: VS_VERSIONINFO, die Bilder eines Icons und ihre Gruppe,
`SetSubsystem`; `AssemblyInfo.ToVersionInfo`; die Direktiven `#noconsole`, `#name`, `#codename`, `#description`, `#author`, `#comments`, `#icon`, `#version`,
`#fileversion` - der Linker in C behält sie jetzt, `FcAsmInfo`): `#noconsole` gibt einem Programm für Windows `-mwindows` (vorher fehlte es).
**`ToolchainProvider`** (`Require`: die gewünschte, sonst die gefundene Toolchain, sonst die Frage der Befehlszeile) und `TestToolchain`. **fire.native.json** mit
Kommentaren und Kommas am Ende und mit `"globalJobs"` (der Vorgabewert für ein Programm, das nichts sagt: `Compiler.Compile` mit `globalJobsDefault`).

`buildplan` druckt die Toolchain (nach dem C++-Ersatz), die Zeilen des Protokolls vor den Befehlen (Ersatz, Schnellbuild), jeden Befehl (der Ordner des Builds
als `<build>`, das Programm als `<programm>`), den Schlüssel des Programm-Caches (`none`: nicht gecacht) und jede Datei des Ordners mit ihrem SHA-256 - also
Befehlszeilen, Dateimenge und Cache-Schlüssel. Die Optionen sind die von `cgen` und `tc:<toolchain>` (sonst der Build-Compiler der Maschine), `quick`, `quick1`,
`nosplit`. forge-c findet die Dateien der Laufzeit, die das C#-forge als Ressourcen trägt, in `native/` (`FIRE_NATIVE_ROOT`, sonst neben dem Ordner der
Tabellen), `Toolchain/` (toolchain.json, portable Compiler) neben sich wie forge.

### Stand

| Messung | Quellen | gleich | noch nicht portiert | verschieden |
|---|---|---|---|---|
| `cgen split`, `tests/cases` / `packages` / `src/fire.Project` | 1129 / 93 / 34 | 1056 / 92 / 30 | 73 / 1 / 3 | 0 / 0 / 1 (`Scene3D Models`, wie oben) |
| `buildplan default` (Linux, Build-Compiler der Maschine: tcc), `tests/cases` / `packages` / `src/fire.Project` | 1129 / 93 / 34 | 1056 / 92 / 30 | 73 / 1 / 3 | 0 / 0 / 1 (`Scene3D Models`) |
| `buildplan tc:gcc+quick` (geteilt, -O0), dieselben | 1129 / 93 / 34 | 1056 / 92 / 30 | 73 / 1 / 3 | 0 / 0 / 1 (`Scene3D Models`) |
| `build`, Stichprobe (jeder 10. Fall, 72 mit `.expected`), gcc / tcc / gcc `--quick` / clang `--quick` | 72 je Toolchain | 63 (Protokoll und Ausgabe) | 9 (Reflection) | 0 |
| `build` nach der Reflection: dieselbe Stichprobe mit tcc (darunter die 9 Fälle der Reflection) / jeder 20. Fall mit tcc und `-m DEBUG` / gcc mit `--stats` (die ersten 46 der Stichprobe: Protokoll mit dem Bericht) | 72 / 35 / 46 | 72 / 35 (eines baut keiner) / 46 | 0 | 0 |

| `pkglib` (`pkglibplan`: Datei, `NativeHashOf`, Fingerprint, Schlüssel, Toolchain, Befehl, Dateien), die Importe mit Natives des Stores und graphics+windows | 27 | 27 | 0 | 0 |
| `forge-c pkglib --all graphics+windows` in einem Store ohne `lib/`: die gebauten Bibliotheken gegen die des C#-forge (`Prebuilt/`) | 25 | 25 (byte-gleich, `.runtime` gleich) | 0 | 0 |
| die C-VM mit nur diesen Bibliotheken (`FIRE_PKG_PATH`, die Programme von `forge-c bytecode`): Fälle mit compression, time, hash, websocket, http, sqlite, graphics, scene3d; dazu `forge run` (C-VM und C#-VM) mit dem `libfire_gfx` von forge-c | 9 | 9 (`.expected`) | 0 | 0 |
| `bootimage` (eine Datei als FIRE.EXE; `disk` mit Ordnern, Datum vor 1980, leerer Datei; Fehler: kein 8.3-Name, voll, doppelt, mehr als 224 Einträge, ein Unterordner zu groß) | 7 | 7 (Bild ohne die Uhrzeit von FIRE.EXE - `DateTime.Now` - byte-gleich, Meldung gleich) | 0 | 0 |
| `peres` (ohne Direktiven, alle mit Unicode und `<&>+`, `#noconsole` mit Versionen mit Leerzeichen und Zahlen über 65535, ein Icon mit 3 Bildern, keins, eins vom Typ 2, Versionen mit Vorzeichen und ungültige) | 5 | 5 | 0 | 0 |
| `buildplan t:windows+tc:gcc/tcc/clang(+quick)` eines Programms mit `#noconsole` | 5 | 5 | 0 | 0 |
| `buildplan default` und `t:windows+tc:gcc` über `tests/cases` / `packages` / `src/fire.Project` nach diesen Änderungen | 1129 / 93 / 34 | 1056 / 92 / 30 | 73 / 1 / 3 | 0 / 0 / 1 (`Scene3D Models`, wie oben) |
| `build` mit fire.native.json mit Kommentaren, Kommas am Ende, `"globalJobs": " Sleep "` (Linux, gcc) und für Windows mit MinGW (`x86_64-w64-mingw32-g++`, `#noconsole`, Versionsinfo, Icon): Protokoll, Fehlerausgabe, behaltene Quellen (`--keep`) | 2 | 2 (die `.exe` bis auf den Zeitstempel des PE-Kopfs gleich) | 0 | 0 |
| `toolchaintest` gcc, clang, tcc, files, eine fehlende, eine eigene; `build` mit einem Compiler, den es nicht gibt (`Require` nimmt den gefundenen) | 7 | 7 | 0 | 0 |

(`build`: das Protokoll beider forges ist nach dem Normalisieren der Pfade gleich, die beiden Programme geben dasselbe aus; 61 davon genau die
`.expected` - die zwei anderen enden, wie verlangt, mit einer unbehandelten Ausnahme. `buildplan tc:gcc+quick` teilt jedes Programm: das ist auch das Maß der
Teilung über den Korpus. Ein Programm, das das C#-forge gebaut hat, nimmt forge-c aus dem Cache - gleicher Schlüssel, geprüft mit tcc, gcc und `--quick`.)

### Dateien

| Datei | Inhalt | Gegenstück in C# |
|---|---|---|
| `gen/split.c` | die Teilung in Einheiten: Scan der C-Teile, Gruppen `#if`, Namen und Benutzungen, die Deklarationen im Header, die Einheiten (Laufzeit, Helfer, Funktionen nach Modul und Kosten, Natives in C), `SplitKeyOf`, `SplitNatives`, `NativesStandAlone` | `ProgramSplit.cs`, `CppGenerator.Split.cs`, `CppGenerator.Stats.cs` (`ModuleOfStat`), `StaticLinkage.cs` (Teil) |
| `build/toolchain.c` | `ToolchainDef` (die eingebauten, `WithBase`, `CCompilerFor`), `ToolchainDetector`, `ToolchainSetup` (`FindProgram`, `Detect`), `MachineToolchains` (toolchain.json), Open Watcom (Befehl, Ort, Umgebung, Startcode), `NativeConfig` (fire.native.json mit `TargetDef.Apply`, `ResolveTarget`, `ResolveToolchain`), `OptimizingToolchain` | `Toolchain.cs`, `ToolchainSetup.cs`, `MachineToolchains.cs`, `Toolchains/*.cs`, `NativeConfig.cs`, `PackageLibrary.cs` (Teil) |
| `build/builder.c` | `QuickBuild`, `WithDirection`, `NativeRuntimeFiles.WriteTo`, `PackageHeaders.WriteTo`, `WriteFiles`, `Run` (fork/exec, die Argumente wie .NET sie teilt), `CompilerCommand`, `BuildCommands`, `SplitCommands`, `CompileUnits`, `ToolchainStamp`, `ArchiveCommand`, `CppFallback`, `ProgramCache`, `Build` (linkage source) | `NativeBuilder.cs`, `NativeBuilder.Quick.cs`, `NativeBuilder.ProgramCache.cs`, `NativeRuntimeFiles.cs`, `Package.Manager/PackageStore.cs` (`PackageHeaders`), `NativeLibraryCache.cs` (`Root`) |
| `build/linkage.c` | linkage static/shared: die Wahl in `Build` (shared, wo gcc/clang es kann, sonst static; static mit `files`), `QuickNotApplied`, `BuildStatic`, `LinkDlls`, `WriteStaticFiles`; der Plan von `buildplan static|shared` | `NativeBuilder.cs`, `FrontendDump.cs` (`DumpStaticPlan`) |
| `build/plugin.c` | linkage plugin: `BuildPlugin` (die Bibliothek `.so`/`.dll` mit `--host`, die Natives in C++ des Plugins, die DLL gegen die DLLs des Programms), `PluginVmNatives`, `HostFileOf`, `ModuleInterface.Modules`/`Text` und `PluginBytecode.Write` (`.fireplugin`: `FIREPLG2`, der Kopf als JSON wie `System.Text.Json`, die Programmdatei mit Zeilentabellen); der Plan von `buildplan plugin` | `NativeBuilder.Plugins.cs`, `src/fire/Runtime/ModuleInterface.cs` |
| `build/command.c` | `forge-c build`, `forge-c buildplan` | `CommandLine.Report.cs` (`ReportBuildFiles`, `NativeOutcome`), `FrontendDump.cs` (`DumpBuildPlan`) |
| `build/pkglib.c` | die Bibliotheken der Pakete für die VM: `Rid`, `HostPlatformKeys`, `FileNameFor`, `Prebuilt`, `BuiltDirectories`, `NewestSource`, `Fingerprint`, `RuntimeKey`, `NativeHashOf` (die Funktionen als JSON wie `System.Text.Json`), die Wrapper in C, C++ und der von graphics und windows, `Locate`, `Ensure`, `EnsureGraphics`; `forge-c pkglib`, `pkglibplan` | `PackageLibrary.cs`, `NativeRuntimeFiles.cs` (`Fingerprint`), `StandardSources.cs` (`NativeHashOf`), `CppGenerator.cs` (`DisplayLibraries`) |
| `build/provider.c` | `ToolchainProvider.Require`, `ToolchainRequest.Message`, die Frage der Befehlszeile, `TestToolchain`; `toolchaintest` | `ToolchainSetup.cs` (`ToolchainProvider`, `ToolchainRequest`), `ConsoleToolchainPrompt.cs`, `NativeBuilder.cs` (`TestToolchain`) |
| `build/peres.c` | `PeVersionInfo`, `ToVersionInfo`, VS_VERSIONINFO, die Bilder eines Icons und ihre Gruppe, `SetSubsystem`, `ApplyPeMetadata`; `peres` | `src/fire/Utilities/PeResourceEditor.cs`, `Assembly/AssemblyInfo.cs`, `NativeBuilder.cs` (`ApplyPeMetadata`) |
| `build/bootimage.c` | `Fat12Image` (`Create`, `FromFolder`, `ShortName`), der Bootsektor von bios16, `AfterLink`; `bootimage` | `Toolchains/Fat12Image.cs`, `Toolchains/BiosBootSectors.cs`, `Toolchains/WatcomToolchain.cs` (`AfterLink`) |
| `build/tools.c` | die Befehle der Werkzeuge (`pkglib` und die Messungen) | `BuilderToolsDump.cs` (die Messungen auf der Seite von C#) |
| `win32.h` | Windows als Host: `opendir`, `stat`/`lstat`, `mkdir`, `realpath`, `rename`, `flock`, `utimes`, `sysconf`, die Zeit, `setenv`, `pthread_*`, `fwRun` (`CreateProcess` mit zwei Pipes), `fwResolveExe`, `fwQuoteArg`, `fwSelfPath` | - (in C# die Laufzeit von .NET) |
| `tools/setup.c` | Schritt 5: `DownloadTransfer.Fetch` (WinHTTP; ein Socket für http, curl/wget für https), `InstallW64devkit`, `FindDownloadUrl`, `SaveMachineToolchain`, `WatcomSetup.Install`/`Unpack`/`FindRoot`, `TinyccSetup.Install`/`Verify`/`Unpack`, `toolchain-setup` | `src/fire.Native/ToolchainSetup.cs`, `TinyccSetup.cs`, `DownloadTransfer.cs` |
| `build/stats.c` | `--stats`: `MapOptions`, die Map von GNU ld und Open Watcom (`ReadMap`), woher jede Funktion kommt (`OriginsOf` mit der `DefinitionRegex` ausgeschrieben), `Analyze`, `Text`, `Json` (wie System.Text.Json eingerückt mit dem Standard-Encoder), `DosLoadBytes`, `WriteStats` | `NativeStats.cs`, `NativeBuilder.cs` (`WriteStats`) |

### Entscheidungen

* **POSIX und Windows**: `Run` startet die Werkzeuge unter Linux und macOS mit `fork`/`execve`, `CompileUnits` mit pthreads; unter Windows (seit 23.11.2026,
  [Windows als Host](#windows-als-host-phase-d-schritte-2-und-3)) mit `CreateProcess` und den Threads von `win32.h`.
* **Die Argumente** gehen als eine Zeile in `ProcessStartInfo`; forge-c teilt sie wie .NET (Anführungszeichen, Backslashes) - die Zeilen im Protokoll sind dieselben.
* **Zeitstempel**: `FileInfo` einer symbolischen Verknüpfung (`/usr/bin/gcc -> /etc/alternatives/gcc`) hat Größe und Zeit der Verknüpfung selbst - die
  Stempel von Toolchain und Cache nehmen darum `lstat`; sonst wäre der Schlüssel für gcc ein anderer als in C#.
* **Was fehlt, sagt es** (`unported: ...`). Windows als Host und das Herunterladen von Toolchains sind seit 23.11.2026 da ([Windows als Host](#windows-als-host-phase-d-schritte-2-und-3),
  [Schritt 5](#phase-d-schritt-5-toolchains-herunterladen)).
* **Linkage static/shared** (`build/linkage.c`: die Wahl in `Build`, `BuildStatic`, `LinkDlls`, `WriteStaticFiles`): die Bibliotheken, die nicht im Cache sind, übersetzt
  forge-c nacheinander (C#: nebeneinander mit `Parallel.For`) - das Protokoll ist dasselbe, die Zeilen jeder Bibliothek beisammen in der Reihenfolge des Plans.
  `buildplan static|shared` (FrontendDump.DumpStaticPlan) zeigt, was `BuildStatic` ausführen würde: der Plan in einen leeren Cache (`<cache>`), die Befehle jeder
  Bibliothek (die temporäre Datei heißt dort `TEMP.a`/`TEMP.so`) und des Programms, die Dateien des Builds, das C jeder Bibliothek mit seinem Hash und bei shared
  die Beschreibung für Plugins. Ein Rückfall auf linkage source (kein Compiler, `files`, ein Programm, das sich nicht teilen lässt) ist dort der Plan von source.
* **`ToolchainProvider`**: der Host von forge-c ist die Befehlszeile - gefragt wird nur, wenn jemand da ist (Ein- und Ausgabe ein Terminal), und dann wie das C#-forge
  (`ConsoleToolchainPrompt`): der Text, und wo fire die Toolchain laden kann (w64devkit unter Windows x64, Open Watcom unter Windows und Linux x64) "Fix automatically?
  [F]ix / [C]ancel (f): " - die Antwort `f` lädt sie ([Schritt 5](#phase-d-schritt-5-toolchains-herunterladen)), sonst steht der Text da und die Antwort ist "abbrechen".
* **PE-Ressourcen**: das C#-forge schreibt sie mit der Ressourcen-API von Win32 (`UpdateResource`) und nur unter Windows - forge-c ebenso (seit 23.11.2026:
  `SetVersionInfo`, `SetIcon` mit den alten Bildern entfernt und der Gruppe unter ihren Namen, die Zeilen des Protokolls); unter POSIX lässt `ApplyPeMetadata` das
  Programm wie das C#-forge in Ruhe (auch beim Cross-Build mit MinGW). Der Inhalt der Ressourcen ist gemessen mit `peres`, dazu das Subsystem (`-mwindows`). Ein Fehler des Icons (`ReadIconImages`) ist in C ein Text statt einer Ausnahme; die Meldungen von .NET für eine zu kurze Datei sind
  nachgeschrieben.
* **Boot-Image**: Open Watcom ist hier nicht installiert - `bootimage` prüft `AfterLink` auf einer beliebigen Datei als FIRE.EXE; ein echter Build für bios16 ruft
  dieselbe Funktion nach dem Link. Die Zeit von FIRE.EXE ist `DateTime.Now` (die Messung lässt diese 10 Bytes aus), die der Dateien von `disk` ihre Änderungszeit,
  beide in der Ortszeit. Großbuchstaben (`ToUpperInvariant`) nur für ASCII - ein Name mit anderen Zeichen ist ohnehin kein 8.3-Name, nur die Meldung schreibt ihn
  dann klein.
* **`NativeHashOf`**: `JsonSerializer.Serialize` der Funktionen ist nachgeschrieben (die Eigenschaften in ihrer Reihenfolge, `null`, der Standard-Encoder: `"`, `<`,
  `>`, `&`, `'`, `+`, `` ` `` und alles außer druckbarem ASCII als `\uXXXX`).
* **fire.native.json**: Kommentare (`//`, `/* */`) und ein Komma vor `]`/`}` werden vor dem Lesen entfernt (`ReadCommentHandling.Skip`, `AllowTrailingCommas`); die
  Meldung einer kaputten Datei ist kürzer als die von `System.Text.Json`.

### linkage plugin: der Generator eines Plugins und BuildPlugin

Stand 22.11.2026. Portiert: von `CppGenerator.Plugins.cs` der Rest (`gen/plugins.c`: `PluginDeclarations`, `RegisterPluginTargets`, `PluginTrailer` mit
`PluginDebugTables`, `PluginUnits` und `PluginJson` aus `CppGenerator.Json.cs`, `PluginExtensionProblem`, `PluginNativesCpp`, `GeneratePlugin` mit den Runden über die
Schalter, dem Lesen von `.firehost` und den Schaltern der Module aus ihren Schnittstellen im Cache), von `NativeBuilder.Plugins.cs` `BuildPlugin` (`build/plugin.c`: die
Bibliothek `.so` - Linux, gcc/clang - oder die DLL für Windows gegen die DLLs der Runtime und der Module des Programms, die Natives in C++ des Plugins als eigene
Datei, `-fvisibility=hidden`), `PluginVmNatives` (die Bibliotheken der Natives für die VM neben dem Bytecode, `build/pkglib.c`) und von `ModuleInterface.cs`
`Modules`/`Text` und `PluginBytecode.Write` (`.fireplugin`: `FIREPLG2`, die Länge, der Kopf als JSON wie `System.Text.Json` mit dem Standard-Encoder, dann die
Programmdatei mit Zeilentabellen). `Linker.ClassMeta` ist `fcLinkClassMeta` (die Metadaten aller Klassen, kein abgeleitetes `defined`); der Linker einer Datei gibt
dem Programm jetzt auch `SourceModules` mit. `--host` geht in `build` einer Datei, eines Projekts und mit `--report json`; ein Fehler der Konfiguration eines Plugins
ist E7003 wie in C# (`NbResult.config`).

Maß (`tests/compare-frontend.sh`, neu `cgen plugin|pluginhost` - `GeneratePlugin` in einen eigenen leeren Cache, das Plugin nach der Datei benannt; `pluginhost`
erst das Programm mit `"shared"`, dann das Plugin mit dessen `.firehost` - und `buildplan plugin` - `NativeBuilder.PlanPlugin`, `BuildPlugin` als Trockenlauf:
Befehle, Protokoll, die Dateien des Baus mit SHA-256, darunter der `.fireplugin`):

| Messung | Quellen | gleich |
|---|---|---|
| `cgen plugin`, jeder 3. Fall von `tests/cases` mit Klassen (228 erzeugt, 36 Übersetzungsfehler, 1 ohne Klassen) | 265 | 265 |
| `cgen plugin` und `cgen pluginhost`, `packages` und `src/fire.Project` mit Klassen | 110 / 110 | 110 / 110 |
| `buildplan plugin+tc:gcc` (Stichprobe aus beiden: der C-Code, die Befehle, der `.fireplugin` byte-gleich) | 125 | 125 |
| `cgen plugin`, `pluginhost`, mit `debug`, `strip0`, `t:windows`; `buildplan plugin` mit gcc, clang, `debug` (ein Plugin mit Enum, Basisklasse, statischen Feldern) | 9 | 9 |
| ohne Rückschritt: `fbc debug/release/performance` ganzer Korpus; `cgen default` (`NATIVE_STRICT=1`) jeder 3. Quelle; `buildplan default` jeder 6.; `cgen shared` (105, Programme mit `fire` und mit Klassen); `cgen default`/`static` der 25 Fälle mit JSON | 1272 / 424 / 212 / 105 / 25 | alle |
| LinkageTests `FIRE_TESTS_FILTER=plugin` mit `FIRE_TESTS_FORGE=<forge-c>` (Programme mit `"shared"` und Plugins der Projekte von forge-c gebaut, darunter die DLLs unter Wine; die VM von C# lädt die `.fireplugin` von forge-c) | 29 | 29 (in C# ebenso 29) |

Echte Builds: das Plugin `Meter` (Einheiten, Natives in C++ des Projekts, JSON) für ein Programm mit JSON - der C-Code beider forges gleich bis auf den Schlüssel der
Runtime (er nennt den Generator: `GeneratorIdentity`), der `.fireplugin` byte-gleich; die `.so` unterscheiden sich wie zwei Builds desselben forge (der Name des
temporären Ordners in der Symboltabelle).

Gefunden und behoben:
* (beide Generatoren) Die statischen Initialisierer eines Plugins liefen in der Reihenfolge der Deklarationen statt der Abhängigkeiten: `RegisterPluginTargets`
  fragte die Sicht des Moduls nach `StaticInitSequence`, die keinen Top-Level hat. Jetzt die des ganzen Programms (`static int x = Beta.y + 1` vor `Beta`).
* (beide Generatoren, Laufzeit) Was ein Plugin vor dem ersten `fire` in seine statischen Felder legt, teilte `fire_shareGlobals` nicht: `fire_pl_share`,
  `FirePlugin.shareGlobals`, `firePluginShareGlobals` (`FIRE_PLUGIN_FORMAT` 7, [NATIVE_BACKEND.md](NATIVE_BACKEND.md#plugins-phase-d-b)).
* (forge-c) Die Einheit eines Felds für JSON mit einer Einheit des Programms (`§C§K:1§1§273.15§§`) las `JsonFieldsOf` als Symbol statt `DecodeDemand` - eine
  falsche Einheit mehr in den Tabellen (kein Fall des Korpus hatte JSON mit Einheiten des Programms; die 25 Fälle mit JSON sind mit `default` und `static`
  weiter gleich).

Offen: msvc (wie in C#: ein Plugin braucht gcc/clang/MinGW); ein Plugin mit Windows als Host ist nicht gemessen (unter Wine fehlt ein Compiler, siehe
[Windows als Host](#windows-als-host-phase-d-schritte-2-und-3)).

## Phase D Schritt 3: Projekte, Solutions, Pakete

Projekte und Solutions ([ROADMAP_C_PORT.md](../ROADMAP_C_PORT.md#phase-d---backend-und-werkzeuge-in-c), Schritt 3) in `native/forge/project/`, Stand de45e7e4 von
`src/fire.Project`, `src/fire.Package.Manager` und den Projektteilen von `src/fire.Compiler`: das Laden von `.fireproj`/`.firesln` (System.Text.Json mit den Optionen
von `FireProject`/`FireSolution`: Namen in jeder Schreibung, Kommentare, Kommas am Ende, die Meldungen des Deserialisierers samt Pfad und Position), die Dateien eines
Projekts (Muster, `exclude`, die Einstiegsdatei zuletzt, Markup zuerst, `testing/`, `templates/`), die Ordner (eingeschlossen, unentschieden, ausgeschlossen - mit
`.gitignore`), die Build-Aktionen (`buildActions`: Copy, CopyAlways, Ignore), die Bauordnung (`BuildOrder`), das Startprojekt, der `BuildPlan` mit den Bibliotheken
und den Natives der Projekte, der Linker mit einem Plan (die Einstellungen vor den Tags, `#import` der Bibliotheken, ihre Namensräume, die Natives der Projekte im
Overlay des Stores, eine Bibliothek ohne Einstiegspunkt), `ProjectBuilder.Check`/`PackLibrary`, das Paketformat `.fpk` (schreiben und lesen) und Publish. Dazu
(20.11.2026) die **Engine `vm`** (`build/cvm.c`: `CVm.cs` 1:1 und `CVmPayload.Append` - ein Programm, das die VM in C trägt, für eine Datei und für Projekte, mit
`--all` und Publish), die **C++-Natives eines Projekts** in einem nativen Programm und in den Bibliotheken für die VM (`ProjectNativeOverlay` auch im Store des
Generators), **`settings.globalJobs`** (`Linker.ForcedGlobalJobs`), **Paketreferenzen `github:owner/repo`** (`PackageStore.FindBySpec` mit der Lock-Datei
`ember-lock.json`, `GitHubSpec.Parse`) und das **Installieren der Standardpakete** in den Store (`StandardSources.EnsureInstalled`, `project/standard.c`).

```
forge-c build x.fire [--engine vm] [-o <programm>] [-m <modus>] [--types lax|strict]
                                                     (CommandLine.Build mit der Engine vm, der Vorgabe: `fire` mit dem Programm und den Bibliotheken seiner Pakete
                                                     dahinter - Payload FIREVMP1 -, ohne -o `out.exe`; die Ausgabe `<pfad> (<n> bytes)`)
forge-c build-vm <ordner> [--variant <v>]... [--all] [--rid <rid>]   (CVm.BuildCommand: `fire` und seine Varianten der Breiten mit `fire.key` in einen Ordner)
forge-c install-standard                             (StandardSources.EnsureInstalled: die Projekte von Source/Packages.firesln neben forge-c, die fehlen oder sich
                                                     geändert haben, gepackt und in den Store installiert - wie forge läuft das auch vor build, publish, bytecode)
forge-c build <projekt.fireproj | solution.firesln> [-p <projekt>] [--engine vm|native] [-o <aus>] [-t <ziel>] [--toolchain <name>] [--quick[=0|1]] [-m <modus>] ...
                                                     (CommandLine.RunProject/BuildPlanned: eine Bibliothek wird gepackt - `name-version.fpk` im Ordner von -o, `output`
                                                     oder `bin/` -, ein Programm mit der VM oder nativ gebaut, ohne -o nach `bin/<ziel>-<modus>/<name>` - `vm-rel/` usw.
                                                     für die VM; die Copy-Dateien daneben)
forge-c build <...> --check                          (eine Bibliothek nur übersetzen)
forge-c build <...> --all [--engine vm|native]       (Build Solution: jedes Projekt in der Bauordnung, wie `forge build --all` mit dem Text-Reporter)
forge-c publish <projekt | solution> [-p <projekt>] [--toolchain <name>] [--quick[=0|1]]   (ProjectPublisher: Programme nativ oder mit der VM - `publish.engine` -,
                                                     Bibliotheken als Paket, in die Zielordner)
forge projectplan <datei>   /   forge-c projectplan <datei>   (das Maß: Workspace, Bauordnung, je Projekt Dateien, Ordner, Build-Aktionen, Publish-Ordner und der BuildPlan
                                                     mit Quellen (SHA-256), Bibliotheken, Einstellungen, Natives, Flatten einer Bibliothek)
forge fpk <datei.fpk>       /   forge-c fpk <datei.fpk>        (ein Paket, wie Fpk.ReadManifest es liest, und jeder Eintrag: Name, Größe, CRC, Attribute, SHA-256)
forge buildplan <optionen> <projekt>  /  forge fbc <modus|native> <projekt>   (wie für eine Datei, mit dem Programm des Startprojekts; `fbc native`: gelinkt wie für
                                                     einen nativen Build für diese Maschine - die Eingabe des Generators)
tests/compare-frontend.sh <forge> <forge-c> project [<ordner>]       (PROJECT_EXTRA="<ordner oder dateien>": weitere Solutions, nur gelesen)
tests/compare-frontend.sh <forge> <forge-c> projectbuild [<ordner>]  (echte Builds der Programmprojekte; PROJECT_COPY="<ordner>": `build --all` und `publish` an Kopien)
tests/compare-frontend.sh <forge> <forge-c> vmbuild [<ordner>]       (Programme mit der VM: jeder VMBUILD_EVERY-te Fall - Vorgabe 6 - und die Beispiele als Projekte:
                                                     Ausgabe, Fehlerausgabe, Exitcode, die Dateien Byte für Byte, was die Programme ausgeben)
```

**Die Engine `vm`** ist wie im C#-forge die Vorgabe von `build` (Publish: `native`). `fire` kommt von dort, wo das C#-forge es sucht (`CVm.Locate`): `FIRE_VM_EXE`, neben
forge-c mit einem passenden Stempel `fire.key`, im Cache `<LocalApplicationData>/fire/vm/<rid>/<schlüssel>/` (unter Linux `$XDG_DATA_HOME` bzw. `~/.local/share` wie in .NET),
sonst jetzt gebaut (`WriteSources`: `native/vm`, `native/base`, `native/tables` und die Laufzeit mit `platform/`, `abi/`; der Compiler der Maschine wie in C#). Der
Schlüssel (`SourceKey`: die Quellen der VM und `NativeRuntimeFiles.Fingerprint`) ist derselbe - eine VM, die das eine forge gebaut hat, nimmt das andere, und eine von
forge-c gebaute VM ist byte-gleich mit der des C#-forge. Dahinter das Programm (`ProgramFile.Write` ohne Zeilentabellen), die Bibliotheken der Pakete
(`PackageImports.EnsureLibraries`: `nlEnsure`) und von graphics/windows (`libfire_gfx`, mit `GraphicsNatives` im Programm), Name für Name, mit Index und Fuß (`FIREVMP1`).

### Stand

| Messung | Dateien | gleich | noch nicht portiert | verschieden |
|---|---|---|---|---|
| `projectplan`: alle `.fireproj`/`.firesln` von `src/fire.Project` und `packages`, ruhrpott-raser (Solution und 14 Projekte) und eine Sammlung von Grenzfällen (Fehler aller Art, Zyklen, ein referenziertes Programm, `.gitignore` mit `!`, aus- und eingeschlossene Ordner, Build-Aktionen, Muster mit `{a,b}` und `?`, Markup, Natives in C mit gefundenen Funktionen, kaputtes JSON, falsche Typen) | 109 | 109 | - | 0 |
| `buildplan default` der Programmprojekte (`src/fire.Project`, `packages`, ruhrpott-raser, die Grenzfälle) | 36 | 26 | 7 (Reflection, Schritt 1) | 3 (siehe unten) |
| `fbc release` / `fbc native` aller 14 Projekte von ruhrpott-raser (der Linker mit Plan, für die VM und für ein natives Ziel) | 28 | 28 | - | 0 |
| Pakete: jede Bibliothek (`packages` 54, ruhrpott-raser 8, die Vorlage `Library`, die Grenzfälle) gepackt von beiden: Ausgabe, die vier Lesungen (jeder liest beide), Zip-Struktur, Forge-Kopie (`json/`) | 70 | 70 | - | 0 |
| echte Builds der 25 Programmprojekte von `src/fire.Project`, gcc / tcc: Protokoll gleich, Programm byte-gleich | 25 je Toolchain | 22 / 21 | 3 (Reflection) | 1 Programm (`Scene3D Models`) |
| `build --all --engine native` an einer Kopie von ruhrpott-raser | 14 Projekte | 11 (Protokoll, Warnungen gleich) | 3 (Raser.Tool, Raser.Server, Raser.Launcher: Reflection) | 0 |
| `publish` an Kopien: die Grenzfälle (ein Projekt mit Fehlern, eines mit Copy-Dateien, `clean`, zwei Zielen; zweimal, der zweite Lauf "up to date"), ruhrpott-raser (nichts eingeschaltet), `-p Raser.Core` zweimal | 6 Läufe | 6 (Ausgabe, geschriebene Dateien) | - | 0 |
| **Engine `vm`** (20.11.2026): `forge-c build-vm` gegen das `fire` des C#-forge (Stempel `fire.key`, Bytes) | 1 | 1 (byte-gleich, derselbe Schlüssel) | - | 0 |
| `build <fall> -o` ohne `--engine`, jeder 6. Fall von `tests/cases` (mit io, time, graphics, `#intwidth`, Fehlern): Ausgabe, Fehlerausgabe, Exitcode, Programm, was es ausgibt | 188 | 188 (die 167 gebauten Programme byte-gleich, sie geben dasselbe aus; die übrigen: Übersetzungsfehler, gleich gemeldet) | - | 0 |
| `build <projekt>` (VM) an Kopien: `src/fire.Project/Samples` (24), `Templates` (4), ruhrpott-raser (15 Projekt- und Solutiondateien) | 43 | 43 (Ausgabe); Programme 28 von 29 byte-gleich | - | 1 Programm (`Scene3D Models`: die Modelle als Ressourcen, Schritt 4) |
| `build --all` (VM) und `publish` an einer Kopie von ruhrpott-raser: 14 Projekte, die Programme mit Reflection laufen in der VM | 2 Läufe | 2 (Ausgabe; 5 Programme byte-gleich) | - | 0 |
| **C++-Natives von Projekten**: eine Solution mit einer Bibliothek in C++, einer in C, einem Programm, das beide nutzt, einem Programm mit eigenen Natives - `buildplan` default (tcc: der C++-Ersatz gcc), tc:gcc, tc:gcc+quick, tc:clang, tc:tcc+quick | 10 | 10 | - | 0 |
| dieselbe Solution gebaut: VM (die Bibliotheken unter `obj/native/<rid>/` mitgepackt), nativ mit gcc, clang, tcc (Fehler: C++), `build --all`, `publish` (VM, zweimal) | 6 Läufe | 6 (Ausgabe; alle Programme byte-gleich, sie rechnen richtig) | - | 0 |
| **`settings.globalJobs`** (`sync` gegen `#globaljobs sleep`, ungültige Werte, keiner): `fbc release`/`debug`/`native`, `buildplan`, Builds mit der VM | 24 | 24 | - | 0 |
| **`github:owner/repo`** über `ember-lock.json` (Spec mit Ref, `#name`, `.git`, Großschreibung, Version zu neu/alt, fehlend, kaputte Spec, kaputte Lock-Datei, Format 2): `fbc release`, `build` | 12 | 12 (Meldung, Programm byte-gleich) | - | Art des Fehlers (siehe unten) |
| **Standardpakete installieren** (`install-standard`): leerer Store (53 Pakete), danach ein geändertes Paket und eines mit Fehlern (Parser, Resolver); gegen den Store des C#-forge | 4 Läufe | 4 (Ausgabe; der Store Datei für Datei gleich, samt `.source-stamps.json` und den Bibliotheken aus `Prebuilt/`) | - | 0 |
| nach diesen Änderungen: `project` (projectplan, buildplan, Pakete) über `src/fire.Project`, `packages`, ruhrpott-raser und die Natives-Solution | 103 / 33 / 67 | 103 / 23 / 67 | - / 7 (Reflection) | - / 3 (wie oben) / - |

Die Messungen der Engine `vm` liefen mit forge-c neben dem C#-forge (derselbe Ordner: dasselbe `fire`, `Prebuilt/`, derselbe Store) - an einem anderen Ort baut
forge-c `libfire_gfx` und `fire` selbst, wenn sie im Cache fehlen (dann steht die Zeile "Building ..." nur bei ihm, die Dateien sind trotzdem gleich).

Die drei Unterschiede bei `buildplan` liegen nicht im Projektteil: `Scene3D Models` sind die Modelle als Ressourcen (`ModelConverter`, Schritt 4; schon in Schritt 1 so),
`Raser.Viewer` und `Raser.Game` vertauschten im erzeugten C die Nummern zweier Klassen (`Raser.Chunk`, `Raser.ChunkManifest`: in C registrierte `RegisterJsonTargets`
die zweite vor der ersten) - eine Abweichung des Generators (Schritt 1), inzwischen behoben (siehe dort): das C beider ist jetzt byte-gleich (`build --toolchain files`),
die Reflection-Projekte (Raser.Tool, Raser.Server, Raser.Launcher und die 3 von `src/fire.Project`) übersetzt der Generator jetzt auch.

**Die Pakete.** Ein `.fpk` ist ein Zip, wie `ZipArchive` es schreibt: dieselben Einträge in derselben Reihenfolge, dieselben Kopffelder (Version 2.0, von Unix, keine
Datenbeschreiber, UTF-8-Bit nur bei Nicht-ASCII-Namen, die Rechte der Datei in den externen Attributen, `package.json` als 0644, eine leere Datei gespeichert statt
komprimiert, mit Version 2.0 wie in .NET), dieselben CRCs und Größen, dasselbe `package.json` (Utf8JsonWriter eingerückt, Byte für Byte, samt der rohen Einträge mit Bauordnung). **Nicht** gleich,
beabsichtigt: die Zeiten (DOS-Zeit der Dateien bzw. von jetzt, wie in .NET) und die komprimierten Bytes - .NET 10 komprimiert mit zlib-ng, forge-c mit dem Deflater von
`packages/compression` (Stufe 6); beide sind gültiges Deflate, jeder liest das Paket des anderen (`fpk`). `tests/fpk-compare.py` vergleicht alles
außer Zeiten, komprimierter Größe und komprimierten Bytes.

### Dateien

| Datei | Inhalt | Gegenstück in C# |
|---|---|---|
| `project/projectmodel.c` | das Modell (`PjProject`, `PjSolution`, `PjSettings`, `PjNative`, `PjPublish`), das Lesen über `json.c` mit den Meldungen des Deserialisierers, `ImportName`, `Validate`, `Merge`, `WithAbsolutePaths`, `FileBuildActions`, `ProjectOutput` | `FireProject.cs`, `FireSolution.cs`, `ProjectSettings.cs`, `ProjectPublish.cs` (Teile), `ProjectOutput.cs` |
| `project/projectfiles.c` | die Muster als reguläre Ausdrücke (ein kleiner Matcher über demselben Baum), `Walk`, `Resolve`, `ContentFiles`, `Expand`, `ProjectFolderMap`, `GitIgnore`, `ProjectNotes.Folder` | `ProjectFiles.cs`, `ProjectFolders.cs`, `Notes.cs` (Teil) |
| `project/manifest.c` | `PackageManifest`: Modell, `ToJson` (Utf8JsonWriter eingerückt), `Parse`, `Validate`, `SemanticVersion`, `IsDocumentName`; `NativeSourceOrder.CheckIncludes`/`CheckAllPlatforms` | `Manifest.cs`, `NativeSources.cs` (Teile) |
| `project/workspace.c` | `LoadedProject`, `Workspace` (Laden, Startprojekt, Bauordnung), `BuildPlan.Create`, `ProjectNatives`, `ProjectContent`, `PublishSettings.TargetFolders`, `ProjectBuilder` ohne Linker (`SelectProject`, `PlanOrThrow`, `PackageVersion`, `Flatten` mit `MapResourcePaths`) | `Workspace.cs`, `BuildPlan.cs`, `ProjectNatives.cs`, `ProjectPublish.cs`, `ProjectBuilder.cs`, `Preprocessor.cs` (Teil) |
| `project/linkplan.c` | `Linker.CompileAndLink` mit einem Plan, für die VM (`Check`) und für ein Ziel (`NativeBuilder.Build` über `NbBuild.link`); `FindBySpec` mit `github:` (`GitHubSpec.Parse`, `PackageLock.Load`/`ForRepository`) | `Linker.cs`, `ProjectLibraries.cs`, `ProjectNativeOverlay.cs`, `ImportedPreludes.Insert` (mit `own`), `Package.Manager/PackageStore.cs` (`FindBySpec`), `PackageLock.cs`, `GitHubSource.cs` (`GitHubSpec`) |
| `project/fpk.c` | Zip schreiben und lesen (Deflate von `packages/compression`), `Fpk.Forge`, `ReadManifest`, `Extract` | `Fpk.cs` |
| `project/command.c` | `build` eines Projekts (`RunProject`, `BuildPlanned`, `--all` mit dem Text-Reporter), `Check`, `PackLibrary`, `ChooseEngine`, der Sammler der Warnungen | `CommandLine.cs`, `CommandLine.Report.cs`, `ProjectBuilder.cs` |
| `project/publisher.c` | Publish (Stempel, `Companions`, `CleanProblem`, Zusammenfassung) | `ProjectPublisher.cs` |
| `project/cli.c` | die Optionen von `build`/`publish` mit Projekt, `publish`, `projectplan`, `fpk`, `buildplan`/`fbc` eines Projekts | `CommandLine.cs` (Teile), `ProjectDump.cs`, `FrontendDump.cs` |
| `build/cvm.c` | die Engine `vm`: `VariantName`, `ParseVariant`, `SourceKey`, `Current`, `CacheFolder`, `Locate`, `WriteSources`, `Build`, `BuildCommand` (`build-vm`), `Pack`, `CVmPayload.Append`, `EnsureLibraries` mit graphics/windows, `CommandLine.Build` mit der Engine vm | `CVm.cs`, `src/fire.Runtime/CVmPayload.cs`, `Linker.cs` (der Teil mit `outname`), `PackageImports.cs`, `GraphicsLibrary.cs` (`Locate`) |
| `project/standard.c` | `Projects`, `ImportsOf`, `HashOf`, die Stempel, `EnsureInstalled`, `Install`, `UsePrebuilt`, `PackageStore.Install` | `StandardSources.cs`, `Package.Manager/PackageStore.cs` (`Install`) |

Messwerkzeuge auf der C#-Seite (ohne Wirkung auf einen Build): `src/fire.Compiler/ProjectDump.cs` (`forge projectplan`, `forge fpk`), in `FrontendDump.cs` `buildplan`
und `fbc` (mit dem Modus `native`) für Projekte.

### Entscheidungen

* **Der Linker mit Plan ist eine eigene Funktion** (`pjLink`) neben `fcLinkFile`/`fgLinkNativeKind`: dieselben Schritte wie `Linker.CompileAndLink`, mit dem Plan und
  wahlweise einem Ziel. Die Wege einer einzelnen Datei bleiben unverändert (ihre Maße auch); `nbBuild` nimmt den Plan über `NbBuild.link`.
* **Ein Schritt, ein Kontext**: jede Übersetzung (Check, der Modus des Programms, der Build) läuft in einem eigenen `FcCtx`; die Warnungen gehen in einen Sammler, der
  sie wie der `DiagnosticCollector` der Befehlszeile einmal ausgibt.
* **Zeiten**: `ProjectContent.CopyFile` setzt die Zeit der letzten Änderung in Ticks (100 ns, wie .NET), nicht die Zugriffszeit; Stempel von Publish und Vergleich
  "neuer" in Ticks.
* **Der Stempel von Publish** hasht forge-c selbst, wo C# seine zwei Assemblies hasht: was der eine forge veröffentlicht hat, baut der andere neu.
* **Was fehlt, sagt es** (`unported: ...`): `FIRE_VM_ENGINE=dotnet` (ein Programm mit der .NET-Runtime statt `fire`; das C#-forge kennt es seit M1 nicht mehr) und `-f` mit der Engine vm für eine einzelne Datei
  (der Linker einer Datei kennt die Breite der Befehlszeile nicht). Die Meldungen des Deserialisierers geben den Pfad im JSON nur an der Wurzel genau an (`Path: $` bei
  Lesefehlern).
* **Die Quellen der VM**: das C#-forge trägt sie als Ressourcen, forge-c liest sie aus `native/` (`FIRE_NATIVE_ROOT`, sonst neben den Tabellen) - der Schlüssel ist gleich,
  solange beide aus demselben Stand gebaut sind.
* **Die Natives eines Projekts** stehen im Overlay des Stores auch für den Generator (`NbBuild.overlay`); `FcPkgImport` trägt dafür ihr Manifest, `obj/native` und die
  Projektdatei (`InstalledPackage.BuildDirectory`/`DescriptionFile`); ihre Quellen haben absolute Pfade (`fgPkgFile`, wie `InstalledImport.FullPath`).
* **`github:`**: forge liest nur die Lock-Datei (`ember install github:...` schreibt sie, mit dem Netz - das bleibt ember). Gemessen mit einer von Hand geschriebenen
  Lock-Datei und einem lokal gepackten und installierten Paket, ohne Netz.
* **Fehler einer Paketreferenz, einer Bibliothek, der VM** (früher C#-Fehler, behoben): eine Paketreferenz, die fehlt oder zu alt ist, ist in
  `Linker.CheckPackageReferences` eine `PackageReferenceException` (ein Fehler des Programms, `IsCompileError`) - beide forges melden `error E6000: ...` mit Exitcode 1,
  `fbc` ebenso (vorher warf das C#-forge eine `System.Exception`: "Unhandled exception", 134, in `fbc` `internal: Exception: ...`). Eine Bibliothek eines Pakets, die sich
  nicht bauen lässt (`PackageException`), und eine VM, die sich nicht haben lässt (`VmUnavailableException` von `CVm.Locate`: FIRE_VM_EXE nennt nichts, kein
  C-Compiler), sind beim Build einer Datei und eines Projekts die Meldung mit Exitcode 1 (das C#-forge stürzte beim Build einer Datei ab). `tests/compare-commands.sh`
  misst die Paketreferenz (`build`, `build --report json`, `bytecode` eines Projekts).
* **`--types`**: `forge-c build` nahm die Markierung `//# types: strict` der Testfälle (die des Testläufers, nicht von forge) - jetzt `--types lax|strict` wie forge.
* **Standardpakete**: dieselben Fingerabdrücke und Stempel wie in C# (was das eine installiert hat, installiert das andere nicht noch einmal). Gesperrt wird mit einer
  Sperrdatei (`flock` auf `<store>/.source-lock`); das C#-forge nimmt einen benannten Mutex - ein C#-forge und ein forge-c, die zur selben Sekunde zum ersten Mal starten,
  sehen einander nicht. Ein Fehler beim Übersetzen wird wie `CompileErrors.Describe` beschrieben.
* **Vorlagen und Beispiele**: forge selbst nutzt `TemplateInstaller`, `TemplateCatalog` und `SampleCatalog` nicht (das tun spark - die Dialoge "Neues Projekt",
  "Beispiele" - und der MCP-Teil von `fire.Project`); was forge davon braucht, ist portiert: der Ordner `templates/` einer Bibliothek geht ins Paket, die Dateilisten eines
  Projekts lassen ihn aus. Die Vorlagen selbst sind als Projekte gemessen (`Templates`, oben).
* **POSIX und Windows**, wie der Builder (der Lock der Installation: `flock`, unter Windows `LockFileEx`).

## Phase D Schritt 4: der Rest der Befehlszeile

Der Rest der Befehlszeile ([ROADMAP_C_PORT.md](../ROADMAP_C_PORT.md#phase-d---backend-und-werkzeuge-in-c), Schritt 4): der **Modellwandler** in `native/forge/model/`
(Stand 6c1b0f0e von `src/fire.Compiler/ModelConverter.cs`, `BlockModel.cs`, `MeshModel.cs`), **`forge test`** in `native/forge/tools/` (Stand b833e71b von
`TestDriver.cs`, `TestFilter.cs`, `TestRunner.cs`, `CommandLine.Test`) und die übrigen Befehle (Stand b833e71b von `CommandLine.cs`, `CommandLine.Report.cs`,
`ForgeReport.cs`, `DataCommand.cs`, `Program.cs`; Stand a3e43117 von `src/fire/Bytecode/ProgramDump.cs`, 808e9788 von `src/fire.Native/NativeStats.cs`):
**der Bericht für Werkzeuge** (`--report json`), `run`, `native`, `data`, `dump`, `stats`, `bytecode` von Projekten, die Befehlszeile selbst
(`CommandLineParser.Parse` mit ihrer Hilfe) - 1:1 (L3) wie die Schritte davor.

```
forge-c model <datei.gltf|.glb|.fblocks|.fmesh> [-o <datei.f3d>] [--fps n]   (wie `forge model`: die Modelldatei, "<aus> (<n> bytes)"; Fehler: "<datei>: <meldung>", 1)
forge-c test [<datei>... | <projekt.fireproj> | <solution.firesln>] [-p <projekt>] [--engine vm|native] [-m <modus>] [--filter A.B,C] [--toolchain <name>] [--config <datei>]
             [-D name] [-f 32|64] [-i 16|32|64] [--types lax|strict]
                                                     (wie `forge test`: die Tests in der VM in C oder als natives Programm, die Liste und die Summen; Exitcode 0/1/2)
forge-c build|publish|bytecode ... --report json [--overlay <quelle>=<datei>]...   (der Bericht für Werkzeuge, docs/tools/forge.md: JSON-Zeilen start, progress,
                                                     log, diagnostic, output, project, end; "cancel" auf der Eingabe oder SIGTERM brechen ab, Exitcode 3)
forge-c run <datei>... | <projekt|solution> [-p <projekt>] [-m ...] [-f ...] [-i ...] [-D name] [-- <argumente>]   (übersetzen, in der VM in C ausführen)
forge-c native <datei>... [-o out.c] [-t <ziel>] [--config <datei>] ...      (das C neben fire_rt.h und wie man es übersetzt)
forge-c bytecode <datei>... | <projekt|solution> [-p <projekt>] [-o <out.fbc>] [-m ...] [-D name] ...
forge-c build <datei>... | <projekt|solution> ... [-D name] [-i 32] [--overlay ...] [--debug-info] [--global-jobs ...]
forge-c data migrate|status|diff|import [<datei>... | <projekt|solution>] --url <url> [...]   (DataCommand: der Treiber des Schemas in der VM in C)
forge-c dump <datei>... | <projekt|solution> | <programm.fbc> [-o <datei>] [-m ...] [-D name] ...   (ProgramDump: das gelinkte Programm als Text; eine Programmdatei
                                                     liest ProgramFile.Read, ein Fehler ist "<datei>: <meldung>" und 1)
forge-c tables <ordner>                              (Tables.Write: opcodes.def, errors.def, natives.def, prelude.fire - geschrieben, was sich geändert hat)
forge-c prebuild-standard <Source> <Prebuilt>        (StandardSources.Prebuild: die Bibliotheken der Standardpakete für diese Maschine nach Prebuilt/, mit .hash und .runtime)
forge-c publish <projekt | solution> ... [--overlay <quelle>=<datei>]... [-D name] [-i 32] [--types ...] [--global-jobs ...]   (die Overlays als TextOf; -D, -i,
                                                     --types, --global-jobs nimmt publish an und nutzt sie nicht - wie das C#-forge)
forge-c stats <vorher.stats.json> <nachher.stats.json>   (NativeStats.Compare: zwei Berichte von `build --stats` nebeneinander)
forge-c ui|biscuit|fdata|falstad <datei> [-o <datei>]
tests/compare-commands.sh <forge> <forge-c> [<ordner>]   (jeder dieser Befehle mit beiden forges in einer Kopie eines kleinen Beispiels: Ausgabe, Fehler,
                                                     Exitcode, geschriebene Dateien; COMMANDS_PACKAGES="vm native": dazu `test` jedes Paketprojekts;
                                                     COMMANDS_PREBUILD=1: dazu `prebuild-standard`)
tests/compare-frontend.sh <forge> <forge-c> model [<ordner>]   (tests/model-corpus.py: jede Datei mit `model` beider forges - Exitcode, Ausgabe, erste Zeile der
                                                     Fehler, die .f3d Byte für Byte - und als Ressource eines Programms, `fbc release`)
```

**Der Modellwandler.** `ModelConverter` (glTF 2.0: `.gltf` mit Puffern als data-URI oder Datei daneben, `.glb`; die Knoten Eltern vor Kindern, die Meshes, die
Materialfarbe, die Clips mit 30 Bildern je Sekunde, LINEAR/STEP/CUBICSPLINE), `BlockModel` (Raster, die sichtbaren Flächen gierig zu Rechtecken, Teile, Pivots,
Animationen) und `MeshModel` (Polygone als Fächer, Newells Normale) - von den beiden Modellen der Teil, den ein Build braucht (`Parse`, `ToF3d`, `PoseOf`,
`Rotation`; das Bearbeiten ist spark). Eine Ressource `new Resource("x.gltf")` (auch `.glb`, `.fblocks`, `.fmesh`) wird beim Einfügen (`ResourceTable.Add`) zur
Modelldatei, ein Fehler ist der des Präprozessors ("The model '...' cannot be converted: ..."); der Linker liest jede Ressource über `fcResourceRead`. Damit ist die
eine bekannte Abweichung der Schritte 1 bis 3, `Scene3D Models`, weg.

**`forge test`.** Der Treiber (`TestDriver.Generate`: je Klasse mit `[Test]` ein Objekt, `[PrepareTesting]`, je Test und Satz von `[Parameters]` mit Bereichen
`[PrepareTest]`/Test/`[CleanTest]`, `[CleanTesting]`, die Probleme der Attribute mit Datei und Zeile), der Filter, der Teil des Linkers (`Linker.TestDriver`:
die Anweisungen der obersten Ebene der Dateien fallen weg, der Treiber ist die letzte Quelle "<tests>" - in den drei Wegen des Linkers in C über
`fcTestDriverApply`), `TestProtocol` (die Zeilen `##fire-test##S|P|F|E|C|X|D`), `WriteSummary` und `CommandLine.Test` (Datei, Projekt, Solution mit allen
Projekten, `-p`, `BuildPlan` mit `testing/`). Die Tests laufen in der VM in C (`fire run <tests.fbc>` aus einem Ordner `fire-test-<guid>` des Temp-Ordners; das
C#-forge nimmt seine VM im Prozess - die VM in C gibt dasselbe aus, eine unbehandelte Ausnahme als dieselbe Zeile "Unhandled exception of type ..."), mit
`--engine native` als natives Programm für diese Maschine (`nbBuild` mit dem Treiber, dann gestartet). Die Bibliotheken der Pakete des Programms und der eigenen
Natives eines Projekts (die Überlagerung des Stores) baut `forge-c test` wie die Engine `vm` und gibt ihre Ordner der VM in FIRE_PKG_PATH; im nativen Build kommen
die C++-Natives eines Projekts über die Überlagerung in den Store des Generators. Dateien werden wie bei `forge build <datei>` als Dateien übersetzt (der Linker
und `NativeBuilder.Build` mit ihren Pfaden, `tcTestFilesPlan`): `#include` vom Ordner der Datei, eine Meldung nennt die Datei.

**Die Befehlszeile.** `CommandLineParser.Parse` (`tools/commands.c`) liest die Befehlszeile von `build`, `publish`, `test`, `run`, `native`, `bytecode`, `dump` wie
das C#-forge, ein Fehler ist seine Meldung mit der Hilfe (`Usage`, `tools/usage.inc`) und 2. Was die Builds der Schritte 2 und 3 schon nehmen, geht weiter an sie
(ihre Messungen bleiben); mehrere Dateien, `-D`, `-i`, `--overlay`, `--debug-info`, `--global-jobs` gehen über **den Link mehrerer Quellen**: einen Plan ohne
Projekt (`PjPlan.files`: 1 die Dateien, jede mit ihrem Dateinamen aus ihrem Ordner - `Linker.SourcePaths` -, 2 Texte ohne Namen aus `BasePath`, sonst dem
aktuellen Ordner), den der Linker eines Plans (`project/linkplan.c`) linkt; `-D` und `--types` kommen in seine Einstellungen (bei einem Projekt zu dessen eigenen),
`-i` ist `PjLinkIn.intWidthOverride`. **`run`** übersetzt wie `bytecode` und startet die Programmdatei mit `fire run <datei> -- <argumente>` (Ein- und Ausgabe
sind die von forge-c, der Exitcode der der VM). **`native`** ist `GenerateLinked` mit der Toolchain `files` und `WriteFiles`. **`data`** linkt die Quellen, eine
Quelle `#import "data"` mit dem Paket des Treibers und den Treiber `Data.Tools.Run(...)` als Testtreiber (`Linker.TestDriver`) und führt ihn in der VM in C aus.

**Der Bericht** (`tools/report.c`, `--report json`): `ForgeReportWriter` mit `RunReported`, `ReportBuild` (Dateien, Projekt, `--all`, `--check`, Bibliothek),
`ReportBytecode` (die Programmdatei, die Bibliotheken ihrer Pakete, die VM ihrer Breiten) und `ReportPublish`. Die Schritte (`BuildMonitor.Step`) melden die
Linker, der Builder und der Packer über `fcMonitorStep` (`diag.c`), die Warnungen des Codes kommen, wie sie gesammelt werden (`fcOnReport`,
`DiagnosticCollector.OnReport`); ein nativer Build sammelt seine eigenen und meldet sie an seinem Ende (`BuildSafe`), ein Publish die jedes Projekts mit seinem
Ergebnis. Die Zeilen der Werkzeuge kommen, wenn sie gelaufen sind, das Protokoll eines Schritts danach ohne die schon gemeldeten Zeilen (`LogBlock`).

### Stand

| Messung | Dateien | gleich | noch nicht portiert | verschieden |
|---|---|---|---|---|
| `model` nach den Posen mit zu wenigen oder zu vielen Zahlen (22.11.2026: `translation`/`rotation`/`scale`/`matrix` eines Knotens und die Schlüssel einer Animation müssen genau 3/4/3/16 bzw. 3/4/3 Zahlen haben - eine `InvalidDataException` mit dem Knoten oder der Animation statt eines IndexOutOfRangeException-Absturzes beim Abspielen und einer kaputten `.f3d` bei zu langen; in C# und C, 11 Fälle mehr im Korpus) | 587 | 587 | 0 | 0 |
| `model`: das Korpus von `tests/model-corpus.py` (die Modelle des Repositorys, der Roboter als `.glb` und mit externem Puffer, 60 zufällige glTF mit jeder Komponentenart, `normalized`, Strides, Offsets, Matrizen, Bäumen, Szenen, Materialien, Animationen; 50 `.fblocks`, 40 `.fmesh`; 70 Fehlerpfade - fehlende Schlüssel, falsche Arten, Indizes außerhalb, kaputtes Base64, Fehler des JSON-Lesers, abgeschnittene `.glb` -; 270 beschädigte Kopien): Exitcode, Ausgabe, Fehler, `.f3d` | 576 | 576 (252 Modelle, 324 Fehler) | 0 | 0 |
| dasselbe Korpus als Ressource eines Programms (`fbc release`: die Modelldatei im Programm oder der Fehler des Präprozessors) | 576 | 576 | 0 | 0 |
| `fbc release` / `cgen default` / `buildplan default` und `tc:gcc+quick` über `tests/cases` / `packages` / `src/fire.Project` nach dem Modellwandler | 1129 / 93 / 34 | fbc 1129 / 93 / 34, cgen und buildplan 1119 / 93 / 34 | 10 (Reflection, Schritt 1) / 0 / 0 | 0 (`Scene3D Models` gleich) |
| `projectbuild` (gcc): die 25 Programmprojekte von `src/fire.Project` | 25 | 22 Programme byte-gleich, darunter `Scene3D Models` (Protokoll gleich) | 3 | 0 (ein Protokoll mit den Zeilen "Installing the standard package ..." des ersten Starts des C#-forge) |
| `test` (VM): jedes Paketprojekt von `packages` | 54 | 54 (`graphics`, `windows` ohne die Zeile "Building the native part of graphics and windows ..." des C#-forge, die vom Cache abhängt) | 0 | 0 |
| `test --engine native`: jedes Paketprojekt von `packages` | 54 | 53 (`tls` - das C#-forge fand mit tcc `fire_net_c.h` nicht, behoben, unten - nach der Behebung einzeln nachgemessen) | 0 | 1: `windows` (beide scheitern an tcc, in verschiedenen Zeilen des C) |
| `dump` jeder Quelle von `tests/cases` und `packages` (aus ihrem Ordner) | 1222 | 1222 (vor dem Wechsel zu `fgRoundTrip`: 7 nur in der Schreibweise von Zahlen) | 0 | 0 |
| `tests/compare-commands.sh`: `build|publish|bytecode --report json` (Dateien, VM und nativ, Fehler, `--overlay`, `--config`, `-t`, `--debug-info`, Projekt, Bibliothek, `--check`, `--all`, Publish), `run`, `native`, `bytecode` von Projekten und mehreren Dateien, `build` mit `-D`/`-i`/`--global-jobs`, `test` mit mehreren Dateien, `-D`, `-f`, `-i`, `#include` vom Ordner der Datei und Meldungen mit ihrem Namen, eine Paketreferenz, die fehlt, `data` (migrate, status, diff, import mit memory: und sqlite:), `dump`, `stats` (zwei Berichte eines C#-Builds mit `--stats`, kaputte Berichte), Fehler der Befehlszeile | 103 | 103 | 0 | 0 |
| `tests/compare-commands.sh` nach dem Rest (23.11.2026, `COMMANDS_PREBUILD=1`): dazu `dump` von Programmdateien des C#-forge (Release, Debug, abgeschnitten, eine andere Version), `tables` (in einen neuen Ordner, über eine Kopie der Tabellen), `publish` mit `--overlay`, `-D`, `-i`, `--types`, `--global-jobs` (auch mit `--report json`), `prebuild-standard`, die Ereignisse `background` und `FIRE_PROGRESS_LINES` mit einem leeren Store (53 Pakete installiert) | 116 | 114 | 0 | 2 (unten) |
| `dump <programm.fbc>`: die Programmdateien jedes 30. Quelltexts von `tests/cases` und `packages` (forge-c `bytecode`, Debug und Release) mit beiden forges; dazu 8 beschädigte (abgeschnitten, Bytes dahinter, Version, ungültiges UTF-8, gekippte Bytes) | 72 + 8 | 80 | 0 | 0 |
| `prebuild-standard Source <ordner>`: mit `Prebuilt/` neben den forges und ohne (alle frisch gebaut) | 25 + 25 | 50 (Ausgabe, Fehlerausgabe, die Bibliotheken byte-gleich, `.hash`, `.runtime`) | 0 | 0 |
| `test` (VM und `--engine native`): die Quellen von `src/fire.Testing/TestingTests.cs` und weitere (Parameter mit Bereichen und Texten, Phasen, `[CleanTest]` wirft, `[PrepareTesting]` wirft, generische Klasse, Ausgaben mit Zeilenumbrüchen und Markern, `leave`, eine Ausnahme in einem Thread, keine Tests, ein Übersetzungsfehler, eine Solution aus Bibliothek und Programm, `-p`, `--filter` mit `*`, `-m debug`, eine fehlende Datei, ein Projekt mit fehlender Referenz) | 15 (VM) / 9 (native) | 15 / 9 | 0 | 0 |

(Verglichen werden stdout, stderr und Exitcode; die Sekunden der Summenzeile sind ausgenommen, ebenso auf der Seite von C# die Zeilen "Building the native part of
the package ..." - sie hängen vom Cache der Bibliotheken ab.)

Die zwei Abweichungen von `compare-commands` sind älter als der Rest (das forge-c von 044bb27b weicht ebenso ab, die Programme beider forge-c sind byte-gleich):
`build a.fire --engine native --report json` und dasselbe mit `-t wasm` - das C#-forge übersetzt die Dateien im Bericht ohne ihre Pfade (`ReportBuildFiles` gibt
`NativeBuilder.BuildSafe` keine `sourcePaths`; das Programm nennt seine Quelle `fire:source/0`), forge-c mit ihnen (`a.fire`, wie `build` ohne Bericht in beiden) -
gemeldet, nicht geändert (für wasm nicht im Einzelnen nachgesehen). Eine dritte (`build ... -m debug` im Bericht) war keine: ein Debug-Programm nennt die Pfade seiner
Quellen, und die Kopien der beiden forges liegen in verschiedenen Ordnern (jetzt `nofiles`; im selben Ordner byte-gleich).

### Dateien

| Datei | Inhalt | Gegenstück in C# |
|---|---|---|
| `model/modelconverter.c` | was die C#-Dateien von .NET nehmen (die Operationen von `JsonElement` mit ihren Ausnahmen, `Math.Round` gerade, die sättigenden Umwandlungen, `Math.Min/Max` mit NaN und -0, `Enumerable.Min/Max`, `Array.BinarySearch`, `Convert.FromBase64String`, `Uri.UnescapeDataString`, `File.ReadAllBytes`), `Part`, `Write`, `QuatOf`, `PoseMatrix`, `Decompose`, `Read`, `Convert`, `ConvertFile` | `ModelConverter.cs` |
| `model/blockmodel.c` | `Parse` (mit `ParseParts`, `ParseAnimations`, `ParseColor`), `Set`, `FaceVisible`, `Quads`, `ToF3d`, `Rotation`, `PoseOf` | `BlockModel.cs` (Teile) |
| `model/meshmodel.c` | `CreateDefault`, `Parse`, `Normal`, `ToF3d` | `MeshModel.cs` (Teile) |
| `model/command.c` | `ResourceTable.Add` für Modelle, `fcResourceRead`, `forge-c model` | `Preprocessor.cs` (`ResourceTable`), `Program.cs` (`model`) |
| `tools/testdriver.c` | `TestFilter`, `TestDriver.Generate`, `fcTestDriverApply` (der Teil des Linkers) | `TestDriver.cs`, `TestFilter.cs`, `Linker.cs` (`TestDriver`) |
| `tools/testrunner.c` | `TestProtocol`, `RunInVm`, `RunNative`, `Driver`, `WriteSummary`, `forge-c test` | `TestRunner.cs`, `CommandLine.cs` (`Test`) |
| `tools/commands.c` | `CommandLineParser.Parse`, `Run` (der Verteiler), `Execute`/`RunProject`/`ExecutePlan` (`run`), `Bytecode`, `Native`, `Build`/`NativeBuild` mit mehreren Dateien, `TextOf`, `ReadSource`, der Plan der Dateien, `DataCommand` | `CommandLine.cs`, `CommandLine.Report.cs` (`TextOf`), `DataCommand.cs`, `NativeStats.cs` (`FromJson`, `Compare`), `Program.cs` (`stats`, `tables` mit `Tables.cs`, `prebuild-standard` mit `StandardSources.Prebuild`/`StorePrebuilt`), `CommandLine.Publish` (die Optionen) |
| `diag.c` (Teil) | `BackgroundWork` (`fcBgBegin`, `fcBgEnd`, `Latest`, `WriteLines`) | `src/fire.Native/BackgroundWork.cs` |
| `tools/usage.inc` | `CommandLineParser.Usage` | `CommandLine.cs` |
| `tools/dump.c` | `ProgramDump.Text` (Q, L, F, UnitText, ConstantText, TypeText, Class, ProtoRef, ChunkRef, Code) | `src/fire/Bytecode/ProgramDump.cs` |
| `programread.c` | `ProgramFile.Read` (der Leser: Tabellen, Konstanten, Funktionen, Chunks, Klassen, Typen, Externs, `RelinkAfterDeserialize`), `IsProgram` | `src/fire/Bytecode/ProgramFile.cs` (`Reader`) |
| `tools/report.c` | `ForgeReportWriter`, `WantsReport`, `BeginReport`, `RunReported`, `ReportBuild`, `ReportBytecode`, `ReportBuildFiles`, `ToolchainFor`, `NativeOutcome`, `ReportCopyContent`, `ReportBuildProject`, `BuildProgram`, `ReportBuildAll`, `ReportPublish`, `BuildSafe` | `ForgeReport.cs`, `CommandLine.Report.cs`, `Debugging/NativeLaunch.cs` (`DebugToolchain`), `NativeBuilder.cs` (`BuildSafe`) |

`json.c` hat dafür einen strengen Modus (`JsonDocument.Parse` ohne Optionen, wie glTF gelesen wird: keine Kommentare, keine Kommas am Ende) und die Meldung einer Zahl, die
nach ihrem Bruchteil falsch endet ("Expected 'E' or 'e'.", wie .NET - vorher "Expected a delimiter.").

### Entscheidungen

* **Ausnahmen von .NET**: eine Ausnahme des Wandlers ist in C ein `FcError` mit dem Namen der .NET-Ausnahme und ihrer Meldung; die Aufrufer fangen wie in C# nur
  IOException, InvalidDataException, FormatException, KeyNotFoundException, InvalidOperationException und IndexOutOfRangeException. Eine andere beendete `forge model`
  wie das C#-forge mit "Unhandled exception. ..." und 134 (im Präprozessor das `internal: <typ>: <meldung>` von FrontendDump) - die bekannten Fälle sind jetzt in beiden
  eine `InvalidDataException` mit Grund: eine `.glb` mit kaputtem JSON ist wie bei `.gltf` "Not glTF (...)", ein Puffer-Index außerhalb "A buffer view names the
  buffer N, which does not exist.", ein Accessor vor dem Anfang seines Puffers (ein negativer Offset oder Stride; die Stellen in 64 Bit) "An accessor reaches before the
  start of its buffer.", eine negative `count` "An accessor has a negative count.", eine negative Länge eines Chunks der `.glb` (die Schleife endete sonst nicht)
  "A chunk of the .glb file has a negative length." - das Korpus erreicht keine unbehandelte Ausnahme mehr. Die Meldungen von .NET sind nachgeschrieben; gemessen sind die, die das Korpus
  erreicht.
* **Zahlen**: die Rechnungen stehen in der Reihenfolge von C# (kein FMA, `float` wo C# `float` rechnet: die Quantisierung der Ecken); `Math.Round` ist `nearbyint`.
* **Die VM für `forge test`**: FIRE_VM_EXE, sonst `fire` (`fire-i32`, ...) neben forge-c, wie `CVm.Locate` sie findet. Eine VM in den Cache zu bauen, wenn keine da
  ist, gehört zu `CVm` (die Engine `vm`, Schritt 3) und fehlt hier (`unported: ...`). Die Bibliotheken der Pakete des Stores findet die VM neben sich (`Packages/`).
* **Die eigenen Natives eines Projekts** (ein Paketprojekt mit `native`, getestet als Projekt): wie das C#-forge für seine VM baut forge-c ihre Bibliothek (die
  Überlagerung des Stores, `nvEnsureLibraries` der Engine `vm`) und gibt ihren Ordner der VM in FIRE_PKG_PATH; die VM anderer Breiten (`-f 32`, `-i 32`) baut es in
  den Cache (`CVm.Locate`).
* **Nicht portiert**: der Kreisel einer Konsole (`[\] ...` während ein Test läuft - nur in einem Terminal; forge-c liest die Ausgabe der VM, wenn sie geendet hat);
  das Abbrechen eines Tests ist `TestRunner.Cancel` - spark ruft es, die Befehlszeile hat es nicht.
* **Der Bericht**: die Ereignisse `background` (seit 23.11.2026; `BackgroundWork` in `diag.c`: `fcBgBegin`/`fcBgEnd` beim Installieren der Standardpakete -
  "Installing the package <name> (<i>/<n>)", das Warten auf ein anderes Programm, das installiert - und beim Bauen der Bibliothek von graphics und windows; spark zeigt
  sie in der Statusleiste) kommen, wenn die Arbeit beginnt und endet, ohne Text, wenn nichts mehr läuft; ohne Bericht mit `FIRE_PROGRESS_LINES` als Zeilen
  `##fire-progress <done> <total> <text>` auf der Fehlerausgabe (`BackgroundWork.WriteLines`). Die Meldungen des Installierens gehen als Zeilen des Protokolls durch
  einen Haken (`nsEnsureInstalledIn`, vorher eine Datei im Speicher), so stehen sie zwischen den Ereignissen wie in C#. Abbrechen: forge-c beendet die Werkzeuge, die laufen (die Kinder des Prozesses,
  unter Linux), und hält beim nächsten Schritt an; ein Schritt des Frontends läuft zu Ende, und der Ordner des Builds bleibt (das C# räumt ihn im `finally` auf).
* **`run`**: `Environment.ProgramPath` ist im Programm der Pfad der VM, nicht der der Quelle (das C#-forge setzt ihn in seiner VM; die VM in C nimmt den, den das
  System ihr sagt). `--host` (linkage plugin) nimmt `publish` wie das C#-forge nicht (eine Meldung der Befehlszeile). `stats` liest die Berichte wie `JsonSerializer.Deserialize<Report>` (die Namen
  genau, was nicht passt, ist kein Bericht); die Zahlen von `Compare` sind aus ihren genauen Dezimalstellen gerundet, die Hälfte von der Null weg (die Formate von
  .NET). `dump` schreibt die Zahlen wie `double.ToString("R")` (`fgRoundTrip` des Generators).
* **`dump` einer Programmdatei** (seit 23.11.2026, `programread.c`: `ProgramFile.Read` 1:1): was der C#-Leser aus den Bytes macht - die Einheit, wie sie geschrieben ist
  (`Unit.Restore`), ein Int eines Enums wie `MakeEnum` (64 Bit, nicht breit, ohne Einheit), ein Schlüssel, der zweimal kommt, an seinem ersten Platz mit dem letzten
  Wert (Dictionary), die Enums neu interniert, die Basen der Klassen über ihre Namen (`RelinkAfterDeserialize`). Die Fehler sind die `ProgramFileException`s mit ihren
  Texten, die Ausnahmen von .NET, die der Leser in "damaged (...)" einpackt, mit den Meldungen von .NET (Index außerhalb, Überlauf, ungültiges UTF-8 - von diesem das
  erste falsche Byte); eine fehlende Basisklasse endet wie in C# als unbehandelte `InvalidOperationException` (134).
* **`tables`** (seit 23.11.2026): forge-c schreibt die Tabellen aus dem, was es selbst trägt - opcodes.def, errors.def und natives.def sind beim Übersetzen von
  forge-c eingebunden und werden Zeile für Zeile wieder ausgegeben (`Tables.CString` nachgeschrieben), prelude.fire ist das Prelude, das forge-c liest (`FIRE_TABLES`).
  Die Quelle der Tabellen bleibt das C# (`ErrorCatalog`, `OpCode`, `NativeRegistry`, `Prelude.Source`), solange es lebt; `tables` von forge-c über `native/tables`
  sagt "The tables are up to date." genau dann, wenn forge-c mit diesen Dateien gebaut ist. bridges.def schreibt keins von beiden (seit M1 ist die Datei die Quelle).
* **`prebuild-standard`** (seit 23.11.2026): `StandardSources.Prebuild` 1:1 - die Projekte von Source in einen Store unter dem Temp-Ordner (`nsEnsureInstalledIn` ohne
  `UsePrebuilt`), jede Bibliothek mit `nlEnsure` (das schon Gebaute unter `Prebuilt/` neben forge-c nimmt es, wie `Locate`), `StorePrebuilt` mit `.hash` und `.runtime`,
  danach graphics und windows; der Store wird gelöscht.
* **`publish` mit `--overlay`, `-D`, `-i`** (seit 23.11.2026): wie das C#-forge - die Overlays gehen als `PublishOptions.TextOf` in den Plan jedes Projekts
  (`pjPublishPlanHook`), `-D`, `-i`, `--types` und `--global-jobs` nimmt die Befehlszeile an, und `ProjectPublisher` nutzt sie nicht (es baut mit den Einstellungen
  der Projekte). Das ist das Verhalten des C#-forge, kein Fehler des Ports: wer es ändern will, ändert beide.
* **Beobachtet am C#-forge** (behoben): `forge test packages/tls/tls.fireproj --engine native` mit tcc scheiterte: `fire_net_c.h` wurde nicht gefunden (ein Header des
  Projekts wird eingesetzt, wo er eingebunden wird - `NativeHeaders.Inline` -, sein `#include` des Headers von `net`, das `tls` über `native.uses` nimmt, zeigte nicht
  auf `pkg/net/`). `InstalledImport.ReadNativeSources` richtet jetzt auch die eingesetzten Header aus (`NativeHeaders.Point`); forge-c legt den Header dazu. Das Protokoll eines nativen Builds mit einem
  Übersetzungsfehler ist `CompileErrors.Describe` (`publish`, `build --all`): forge-c schrieb dort `DescribeErrors` - jetzt wie C#.
* **Beobachtet am C#-forge** (behoben, in beiden forges gleich): eine `.glb` mit kaputtem JSON, ein Puffer-Index außerhalb, ein negativer Offset eines Accessors und
  eine negative Chunk-Länge endeten in einer unbehandelten Ausnahme oder (eine unbekannte Art) einer Schleife ohne Ende - jetzt Fehler mit Grund ("Ausnahmen von .NET",
  oben). `forge test <datei>` übersetzte die Datei als Text (`RuntimeSession.Build` ohne Pfad): ihr `#include` ging vom aktuellen Ordner aus, nicht von dem der Datei,
  und eine Meldung nannte die Datei nicht - jetzt wie `forge build <datei>` (C#: `TestRunner.RunInVm`/`RunNative` mit `sourcePaths`; forge-c: `tcTestFilesPlan`, die
  Meldungen behalten ihren Dateinamen).

### Der Rest der Befehlszeile

Die Befehle des C#-forge (`Program.cs`, `CommandLine*.cs`) und was davon in forge-c ist:

| Befehl | forge-c |
|---|---|
| `tokens`, `ast`, `diagnostics`, `fbc`, `cgen`, `buildplan`, `projectplan`, `fpk`, `pkglibplan`, `bootimage`, `peres`, `toolchaintest` (Messungen) | ja |
| `ui`, `biscuit`, `fdata`, `falstad` | ja, mit `-o` (Schritt 4) |
| `model` | ja (Schritt 4) |
| `test` | ja (Schritt 4), mit mehreren Dateien, `-D`, `-f`, `-i`, `--types` |
| `build` | Dateien und Projekt/Solution, die Engines `vm` und `native`, `--report json`, `--overlay`, `--debug-info`, `-D`, `-i`, `--global-jobs` (Schritt 4), linkage static/shared/plugin mit `--host` (Schritt 2); ohne `--stats` |
| `publish` | ja (Schritt 3), mit `--report json`, `--overlay`, `-D`, `-i` (Schritt 4) |
| `bytecode` | Dateien und Projekte, `--report json`, `-D`, `-i`, `-f`, `--overlay` (Schritt 4) |
| `serve` | ja (Phase C 6) |
| `data migrate\|status\|diff\|import` | ja (Schritt 4): der Treiber in der VM in C |
| `run` | ja (Schritt 4): in der VM in C |
| `native` (das C nach `out.c`) | ja (Schritt 4) |
| `dump` (ein Programm als Text) | ja (Schritt 4), Quellen, Projekte und Programmdateien (`.fbc`) |
| `stats` (zwei Berichte von `--stats`) | ja (Schritt 4) |
| `install-standard`, `build-vm` | ja (Schritt 3) |
| `prebuild-standard`, `tables` | ja (Schritt 4, 23.11.2026; `tables` aus den Tabellen, die forge-c trägt) |
| `toolchain-setup w64devkit\|watcom\|tcc [--archive <datei>]` | nur forge-c (Schritt 5): was das Setup von spark tut (SetupSteps), auf der Befehlszeile |
| der Packer | `fire.Runtime.Packer` (die .NET-Einzeldatei, `FIRE_VM_ENGINE=dotnet`) ist seit M1 auch aus dem C#-forge entfernt - in C entfällt er; das Packen mit der VM in C (`CVm.Pack`, `FIREVMP1`) ist die Engine `vm` von Schritt 3 |

## Windows als Host (Phase D Schritte 2 und 3)

Stand 23.11.2026. forge-c läuft auch unter Windows - der Builder, die Projekte und der Rest der Befehlszeile, nicht nur das Frontend. Was diese Teile von POSIX nehmen,
gibt ihnen `native/forge/win32.h` mit den Mitteln von Win32 und der C-Laufzeit: die Ordner (`opendir` über `FindFirstFile`), die Dateien (`stat`, `mkdir`, `realpath`
über `_fullpath`, `rename`, das ersetzt, `flock` über `LockFileEx`, die Zeiten), die Threads (`pthread_create` über `_beginthreadex`, der Mutex ein SRW-Lock - auch
`PTHREAD_MUTEX_INITIALIZER`), die Umgebung, die Zeit, und die Programme eines Builds (`fwRun`: `CreateProcess` mit der Befehlszeile, wie .NET `Arguments` weitergibt,
die Ausgabe und die Fehler über zwei Pipes, die Umgebung - `PATH` mit dem Ordner des Compilers, die Variablen von Open Watcom, `FIRE_PKG_PATH` - unter einem Lock um
`CreateProcess`). Gebaut wird es mit MinGW ohne eine Bibliothek von POSIX (weder `unistd.h` noch `dirent.h` noch winpthreads) und ohne Konstrukte nur von GNU
(`__attribute__` hat überall einen Ersatz), damit MSVC es auch übersetzt:

```
x86_64-w64-mingw32-gcc -O2 native/forge/forge.c -o forge-c.exe -static      (WinHTTP für Schritt 5 wird zur Laufzeit geladen: keine Bibliothek zu linken)
cl /O2 native\forge\forge.c /Fe:forge-c.exe                                (nicht geprüft: kein MSVC hier)
```

Was unter Windows anders ist: die Pfade sind innen UTF-8 mit `/` (`fcFullPath` kennt das Laufwerk, `C:/a/b`; `fcDirectoryName` von `C:/a` ist `C:/`, `fcIsRootDir`
beendet die Wege nach oben - `.git`, `fire.native.json` -; `nbIsRooted`, `nbDirName`, `nbFileName` auch `\`), die Namen wie in C# (`PackageLibrary.Rid` `win-x64`, die
Bibliotheken der Pakete `fire_pkg_<paket>_<import>.dll` ohne `lib`, die VM `fire.exe`, das Programm der VM eines Projekts `<name>.exe`), der
Temp-Ordner ist `GetTempPath`, der Ordner von forge-c `GetModuleFileName`, die Programme haben `.exe` (`FindProgram` wie in C#: `<name>.exe` vor `<name>`, der `PATH`
mit `;`, dazu die bekannten Orte von w64devkit, MSYS2, MinGW, LLVM; Open Watcom in `binnt64`/`binnt`), die GUIDs der Ordner sind das SHA-256 von Zeit, Zähler,
Prozess und Thread (kein `/dev/urandom`), die Zeiten der Dateien haben Sekunden, stdout und stderr sind binär (kein CR vor LF), die PE-Ressourcen werden geschrieben
(`ApplyPeMetadata`, [oben](#phase-d-schritt-2-der-builder)). Nicht unter Windows: das Beenden der Werkzeuge beim Abbrechen (`frEndChildren` liest `/proc`),
Pfade außerhalb der Codepage (die ANSI-Funktionen des Systems).

Gemessen unter Wine (Ubuntu, Wine 9.0, eigenes `WINEPREFIX`, `env -u TMPDIR`; `fire.exe` mit MinGW aus `native/vm/fire.c` gebaut wie die Gruppe `cvmwin` von
fire.Testing): `forge-c.exe tokens`, `bytecode` (die `.fbc` byte-gleich mit der von forge-c unter Linux), `dump` (gleich), `run` (in `fire.exe`), `build` (Engine vm:
`fire.exe` mit dem Programm dahinter, das gepackte Programm läuft), `build` und `projectplan` einer Solution (Bibliothek und Programm: `App.exe` läuft), `test`
einer Datei mit einem Paket (`#import "testing"`, Ausgabe wie unter Linux), ein nativer Build
mit der Toolchain `files` (die Quellen für `windows`, mit MinGW übersetzt, läuft unter Wine) und ohne Compiler (die Meldung E7003). Ein nativer Build mit einem
Compiler in Wine ist nicht gemessen: ein MinGW für Windows (w64devkit) gibt es hier nicht (kein Netz), und Wine startet zwar Programme von Linux, gibt aber ihren
Exitcode nicht zurück.

## Phase D Schritt 5: Toolchains herunterladen

Stand 23.11.2026, `native/forge/tools/setup.c` (von `src/fire.Native/ToolchainSetup.cs`, `TinyccSetup.cs`, `DownloadTransfer.cs`, `ToolchainProvider.Require`): wenn
kein Compiler da ist, bietet forge-c wie das C#-forge an, einen zu laden (`ConsoleToolchainPrompt`: nur mit einem Terminal, "Fix automatically? [F]ix / [C]ancel (f): ")
- w64devkit unter Windows x64 (`InstallW64devkit`: die neueste Version über die API von GitHub, sonst v2.0.0, das 7z entpackt sich selbst mit `-y -o<Toolchain>`,
danach `toolchain.json` wie `SaveMachineToolchain`), Open Watcom unter Windows und Linux x64 für seine Ziele (`WatcomSetup.Install`: `ow_portable_v2_stable.zip`,
`Unpack` mit `FindRoot`, die Rechte von `binl64`/`binl`). Dieselben Quellen, dieselben Ordner (`Toolchain/` neben forge-c), dieselben Meldungen und Zeilen
("Downloading... n% (m MB)"). Das portable TinyCC (`TinyccSetup`: die Zips von savannah mit ihrem festen SHA-256, nur Windows) fragt das C#-forge nicht an - das tut
das Setup von spark; forge-c hat dafür `toolchain-setup`:

```
forge-c toolchain-setup w64devkit|watcom|tcc [--archive <datei>]   (laden, prüfen - tcc: SHA-256 -, entpacken; --archive: diese Datei statt des Downloads)
FIRE_TOOLCHAIN_ANSWER=f                                            (die Antwort auf die Frage ohne Terminal - für Tests)
```

Der Download geht über das System, nicht über ein TLS von forge-c: unter Windows WinHTTP (zur Laufzeit geladen; der Proxy des Systems oder `http_proxy`/`https_proxy`),
unter POSIX `http://` über einen Socket (HTTP/1.0, Weiterleitungen, `http_proxy`) und `https://` mit `curl` (sonst `wget`) der Maschine. Die Zips entpackt forge-c
selbst (der Leser von `.fpk`, `project/fpk.c`: stored und deflate, die CRC geprüft, kein Name außerhalb des Ziels).

Gemessen ohne Netz (die Server der Toolchains sind von hier nicht erreichbar) mit einem lokalen Server, der als `http_proxy` jede Anfrage mit einer Datei beantwortet:
`toolchain-setup watcom` unter Linux und unter Wine (WinHTTP mit dem Proxy; ein Zip im Aufbau von `ow_portable_v2_stable.zip`, die Dateien byte-gleich mit dem
Entpacken durch Python, `owcc` ausführbar), ein nativer Build für `dos32` ohne Open Watcom mit `FIRE_TOOLCHAIN_ANSWER=f` (die Frage, der Download, das Entpacken, dann
der Build mit dem gefundenen `owcc`), `toolchain-setup w64devkit --archive` unter Wine (ein falsches 7z, das sich "entpackt": `toolchain.json` geschrieben),
`toolchain-setup tcc --archive` mit einem falschen Zip (die Meldung des SHA-256), die Meldungen von w64devkit und TinyCC unter Linux. Nicht gemessen: ein echter
Download (https, w64devkit, TinyCC) - kein Netz.

## Nachziehen

Ändert sich auf `master` eine der portierten C#-Dateien (`git log <commit>..master -- src/fire.Compiler/Lexer.cs` usw.; Schritt 3:
`Resolver*.cs`, `TypeChecker*.cs`, `SetterClaims.cs`, `Linker.cs`, `ImportedPreludes.cs`, `Values/Unit.cs` - Stand c7f13d97; Schritt 4: `Compiler.cs`,
`Compiler.Trees.cs`, `ReadingMethods.cs`, `Bytecode/ProgramFile.cs`, `Bytecode/Chunk.cs`, `Bytecode/FunctionProto.cs`, `Bytecode/RuntimeClass.cs`,
`Bytecode/DeclaredType.cs`, `Bytecode/DeclaredKind.cs`, `Runtime/FloatNarrowing.cs`, `PackageImports.cs` und wieder `TypeChecker.cs`/`Linker.cs` -
Stand 6bc618e7; Schritt 5: `src/fire.UI.Markup/*.cs` außer `DataModelDocument.cs`, `MarkupText.cs`, `MarkupCompletion.cs`, `CircuitSamples.cs` - Stand
7a594769), wird die Änderung in die C-Datei übertragen und der Commit im Kopf nachgeführt; `tests/compare-frontend.sh` (mit
`FRONTEND_MUTANTS=2`, und `fbc`; für das Markup `markup` mit `FRONTEND_MUTANTS=3`) zeigt, ob beide wieder gleich sind.
Eine neue Knotenart oder ein neues Feld kommt in `ast.h` an dieselbe Stelle wie im Record und in `ast.c` in die Ausgabe.
Der Generator (Phase D Schritt 1, `native/forge/gen/`): `src/fire.Native/CppGenerator*.cs`, `NativeCTypes.cs`, `NativeConfig.cs`,
`src/fire/Bytecode/Overloads.cs` (`OverloadHint`), `src/fire/Standard/JsonMethods.cs`, `LibraryMethods.cs` und von `src/fire.Package.Manager`
`Manifest.cs`, `NativeSources.cs`, `PackageStore.cs` - Stand 808e9788; das Maß ist `tests/compare-frontend.sh ... native` mit den
Optionssätzen der Tabelle oben. Der Builder (Phase D Schritt 2, `native/forge/build/`, `gen/split.c`): `src/fire.Compiler/NativeBuilder.cs`,
`NativeBuilder.Quick.cs`, `NativeBuilder.ProgramCache.cs`, `PackageLibrary.cs`, `StandardSources.cs` (`NativeHashOf`), `ConsoleToolchainPrompt.cs`,
`Assembly/AssemblyInfo.cs`, `CommandLine.Report.cs` (`ReportBuildFiles`), `src/fire/Utilities/PeResourceEditor.cs`,
`src/fire.Native/Toolchain.cs`, `ToolchainSetup.cs`, `MachineToolchains.cs`, `NativeConfig.cs`, `NativeRuntimeFiles.cs`, `ProgramSplit.cs`, `CppGenerator.Split.cs`,
`Toolchains/*.cs`, `src/fire/Runtime/TargetProfile*.cs`, `PackageStore.cs` (`PackageHeaders`) - Stand 6f21d1a4, die Werkzeuge (`build/pkglib.c`,
`provider.c`, `peres.c`, `bootimage.c`) Stand de45e7e4; das Maß ist `tests/compare-frontend.sh ... buildplan`, `... build` und `... pkglib`, dazu `bootimage` und
`peres` auf beiden Seiten.
Projekte, Solutions, Pakete (Phase D Schritt 3, `native/forge/project/`): `src/fire.Project/FireProject.cs`, `FireSolution.cs`, `ProjectSettings.cs`, `ProjectPublish.cs`,
`ProjectOutput.cs`, `ProjectFiles.cs`, `ProjectFolders.cs`, `Workspace.cs`, `BuildPlan.cs`, `ProjectNatives.cs`, `Notes.cs` (`Folder`), von `src/fire.Package.Manager`
`Fpk.cs`, `Manifest.cs`, `NativeSources.cs` (`NativeSourceOrder`), von `src/fire.Compiler` `ProjectBuilder.cs`, `ProjectLibraries.cs`, `ProjectNativeOverlay.cs`,
`ProjectPublisher.cs`, `CommandLine.cs`/`CommandLine.Report.cs` (die Projektteile) und wieder `Linker.cs` (mit `Plan`) - Stand de45e7e4; das Maß ist
`tests/compare-frontend.sh ... project` (mit `PROJECT_EXTRA`) und `... projectbuild` (mit `PROJECT_COPY`). Dazu `src/fire.Compiler/CVm.cs`, `src/fire/Runtime/CVmPayload.cs`,
`GraphicsLibrary.cs` (`Locate`), `StandardSources.cs`, von `src/fire.Package.Manager` `PackageLock.cs`, `GitHubSource.cs` (`GitHubSpec`), `PackageStore.cs` (`FindBySpec`,
`Install`) - Stand de45e7e4; das Maß ist `... vmbuild`, `install-standard` auf beiden Seiten.
Der Rest der Befehlszeile (Phase D Schritt 4): der Modellwandler (`native/forge/model/`) `src/fire.Compiler/ModelConverter.cs`, `BlockModel.cs`, `MeshModel.cs`,
`Preprocessor.cs` (`ResourceTable.Add`) - Stand 6c1b0f0e, das Maß ist `tests/compare-frontend.sh ... model`; `forge test` (`native/forge/tools/`) `TestDriver.cs`,
`TestFilter.cs`, `TestRunner.cs`, `CommandLine.cs` (`Test`), `Linker.cs` (`TestDriver`) - Stand b833e71b, gemessen mit `forge test` beider forges (Ausgabe ohne die
Sekunden, Fehler, Exitcode) an den Paketprojekten und den Quellen von `src/fire.Testing/TestingTests.cs`; die übrigen Befehle (`tools/commands.c`, `tools/report.c`)
`CommandLine.cs`, `CommandLine.Report.cs`, `ForgeReport.cs`, `DataCommand.cs`, `Program.cs`, `Debugging/NativeLaunch.cs` (`DebugToolchain`), `NativeBuilder.cs`
(`BuildSafe`, die Schritte des Monitors) - Stand b833e71b, `src/fire/Bytecode/ProgramDump.cs` - Stand a3e43117, `src/fire.Native/NativeStats.cs` (`FromJson`,
`Compare`) - Stand 808e9788; das Maß ist `tests/compare-commands.sh` (mit `COMMANDS_PACKAGES="vm native"`) und `dump` beider forges über das Korpus.
Dazu (Stand 044bb27b) `src/fire/Bytecode/ProgramFile.cs` (`Reader`, `programread.c`), `src/fire.Compiler/Tables.cs`, `StandardSources.cs` (`Prebuild`, `StorePrebuilt`),
`src/fire.Native/BackgroundWork.cs`, `CommandLine.cs` (`Publish` und die Werte `Linker.Process*`); das Maß ist `tests/compare-commands.sh` (mit
`COMMANDS_PREBUILD=1`) und `dump` der Programmdateien des C#-forge mit beiden. Der Download (Schritt 5, `tools/setup.c`): `src/fire.Native/ToolchainSetup.cs`,
`TinyccSetup.cs`, `DownloadTransfer.cs`, `src/fire.Compiler/ConsoleToolchainPrompt.cs` - Stand 044bb27b, geprüft mit einem lokalen Server als Proxy.
