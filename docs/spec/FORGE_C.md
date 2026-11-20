# forge in C (`native/forge`)

Status: **Phase C, Schritte 1 bis 5** aus [ROADMAP_C_PORT.md](../ROADMAP_C_PORT.md#phase-c---das-frontend-in-c) (20.11.2026), dazu der größte
Teil von **Phase D Schritt 1**, dem Generator Bytecode -> C ([unten](#phase-d-schritt-1-der-generator)), und von **Schritt 2**, dem Builder
([unten](#phase-d-schritt-2-der-builder): `forge-c build` mit linkage `source`), und **Schritt 3**, Projekte, Solutions und das Paketformat `.fpk`
([unten](#phase-d-schritt-3-projekte-solutions-pakete): `forge-c build`/`publish` eines Projekts oder einer Solution), und aus **Schritt 4** der Modellwandler und
`forge test` ([unten](#phase-d-schritt-4-der-rest-der-befehlszeile)): Lexer,
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
| Stichprobe, `debug`, `split` | 504 | 131 (die Fehler des Frontends) | 373 | 0 |

(Die Stichprobe: jede dritte Quelle von `tests/cases` und alle von `packages` und `src/fire.Project`, 504 Quellen.)

Noch nicht portiert (`unported: ...`, über die Fälle mit Standardoptionen): die Reflection (`CppGenerator.Reflection.cs`: `__refl_*`, die Selektoren
der Lambdas, die Probes - alle 73 noch nicht portierten Fälle und das eine Paket, `fire-reflection`), Debug-Builds (`CppGenerator.Debug.cs`), die Teilung in
Einheiten (`ProgramSplit.cs`, `CppGenerator.Split.cs` - seit Schritt 2 portiert, [unten](#phase-d-schritt-2-der-builder)), `linkage` static/shared (`CppGenerator.Modules.cs`, `ModuleDispatch.cs`, `StaticLinkage.cs`,
`Plugins.cs`: der Generator in C kennt nur die eine Rolle des ganzen Programms - `Modular`, `LibraryRole` sind `false`) und `--stats`.

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
| `gen/misc.c` | Konstanten (`Constant`, `DoubleLiteral` wie `"R"` von .NET, `CString`), `UnitId`, Prototypen, die Thunks von `Library.Get` (mit `LibraryMethods.Parse`), die Zeigerkonvention der Open-Watcom-Ziele (`PointerFunction`, `PointerCallsPass`), was jedes Programm von der Reflection braucht; die Stummel der nicht portierten Teile | `CppGenerator.cs`, `PointerCalls.cs`, `Modules.cs`, `Debug.cs`, `Reflection.cs`, `Split.cs` (Teile), `Standard/LibraryMethods.cs` |
| `gen/json.c` | JSON: die Ziele des Lesens, die Tabellen der Klassen und Felder, die Typargumente | `CppGenerator.Json.cs`, `Standard/JsonMethods.cs` (`JsonFieldsOf`) |
| `gen/cppgenerator4.c` ... `gen/cppgenerator8.c` | `StepCore`: seine lokalen Funktionen über dem Zustand eines Schritts (`FgSC`), `CFields`, dann der `switch` in der Reihenfolge der C#-Datei (Stapel, Variablen, Natives; Rechnen, Einheiten, Vergleiche, Sprünge; Aufrufe, Felder, `new`; Lambdas, Wächter, Arrays, Besitz, Zeiger, Ausnahmen, Threads) | `CppGenerator.cs` 4680-8555, `CppGenerator.CFields.cs` |
| `gen/native.c` | die Ziele, `NativeBuilder.GenerateLinked` mit `files` (das Linken mit den Symbolen des Ziels), `forge-c cgen` | `NativeConfig.cs`, `Runtime/TargetProfile.cs`, `NativeBuilder` (Teil), `FrontendDump.cs` (`DumpNative`) |

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
* **Warnungen**: die Helfer der noch nicht portierten Teile (Reflection, JSON, Debug) sind schon da und noch unbenutzt (`-Wunused-function`).

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
| `build/command.c` | `forge-c build`, `forge-c buildplan` | `CommandLine.Report.cs` (`ReportBuildFiles`, `NativeOutcome`), `FrontendDump.cs` (`DumpBuildPlan`) |
| `build/pkglib.c` | die Bibliotheken der Pakete für die VM: `Rid`, `HostPlatformKeys`, `FileNameFor`, `Prebuilt`, `BuiltDirectories`, `NewestSource`, `Fingerprint`, `RuntimeKey`, `NativeHashOf` (die Funktionen als JSON wie `System.Text.Json`), die Wrapper in C, C++ und der von graphics und windows, `Locate`, `Ensure`, `EnsureGraphics`; `forge-c pkglib`, `pkglibplan` | `PackageLibrary.cs`, `NativeRuntimeFiles.cs` (`Fingerprint`), `StandardSources.cs` (`NativeHashOf`), `CppGenerator.cs` (`DisplayLibraries`) |
| `build/provider.c` | `ToolchainProvider.Require`, `ToolchainRequest.Message`, die Frage der Befehlszeile, `TestToolchain`; `toolchaintest` | `ToolchainSetup.cs` (`ToolchainProvider`, `ToolchainRequest`), `ConsoleToolchainPrompt.cs`, `NativeBuilder.cs` (`TestToolchain`) |
| `build/peres.c` | `PeVersionInfo`, `ToVersionInfo`, VS_VERSIONINFO, die Bilder eines Icons und ihre Gruppe, `SetSubsystem`, `ApplyPeMetadata`; `peres` | `src/fire/Utilities/PeResourceEditor.cs`, `Assembly/AssemblyInfo.cs`, `NativeBuilder.cs` (`ApplyPeMetadata`) |
| `build/bootimage.c` | `Fat12Image` (`Create`, `FromFolder`, `ShortName`), der Bootsektor von bios16, `AfterLink`; `bootimage` | `Toolchains/Fat12Image.cs`, `Toolchains/BiosBootSectors.cs`, `Toolchains/WatcomToolchain.cs` (`AfterLink`) |
| `build/tools.c` | die Befehle der Werkzeuge (`pkglib` und die Messungen) | `BuilderToolsDump.cs` (die Messungen auf der Seite von C#) |

### Entscheidungen

* **Nur POSIX** (Linux, macOS): `Run` startet die Werkzeuge mit `fork`/`execve`, `CompileUnits` mit pthreads. Unter Windows baut forge-c `build/` nicht ein.
* **Die Argumente** gehen als eine Zeile in `ProcessStartInfo`; forge-c teilt sie wie .NET (Anführungszeichen, Backslashes) - die Zeilen im Protokoll sind dieselben.
* **Zeitstempel**: `FileInfo` einer symbolischen Verknüpfung (`/usr/bin/gcc -> /etc/alternatives/gcc`) hat Größe und Zeit der Verknüpfung selbst - die
  Stempel von Toolchain und Cache nehmen darum `lstat`; sonst wäre der Schlüssel für gcc ein anderer als in C#.
* **Was fehlt, sagt es**: linkage `static`/`shared`/`plugin` (der Generator hat noch keine Module) und `--stats` sind `unported: ...`.
* **`ToolchainProvider`**: der Host von forge-c ist die Befehlszeile - gefragt wird nur, wenn jemand da ist (Ein- und Ausgabe ein Terminal), und dann wie das C#-forge
  hier: w64devkit lädt fire nur unter Windows, also steht der Text da und die Antwort ist "abbrechen"; Open Watcom ließe sich unter Linux x64 laden, das
  Herunterladen ist aber Schritt 5 - forge-c zeigt den Text und sagt das.
* **PE-Ressourcen**: das C#-forge schreibt sie mit der Ressourcen-API von Win32 (`UpdateResource`) und nur unter Windows; forge-c baut nur unter POSIX, also lässt
  `ApplyPeMetadata` das Programm wie das C#-forge dort in Ruhe (auch beim Cross-Build mit MinGW). Portiert ist der Inhalt der Ressourcen (gemessen mit `peres`) und
  das Subsystem (`-mwindows`). Ein Fehler des Icons (`ReadIconImages`) ist in C ein Text statt einer Ausnahme; die Meldungen von .NET für eine zu kurze Datei sind
  nachgeschrieben.
* **Boot-Image**: Open Watcom ist hier nicht installiert - `bootimage` prüft `AfterLink` auf einer beliebigen Datei als FIRE.EXE; ein echter Build für bios16 ruft
  dieselbe Funktion nach dem Link. Die Zeit von FIRE.EXE ist `DateTime.Now` (die Messung lässt diese 10 Bytes aus), die der Dateien von `disk` ihre Änderungszeit,
  beide in der Ortszeit. Großbuchstaben (`ToUpperInvariant`) nur für ASCII - ein Name mit anderen Zeichen ist ohnehin kein 8.3-Name, nur die Meldung schreibt ihn
  dann klein.
* **`NativeHashOf`**: `JsonSerializer.Serialize` der Funktionen ist nachgeschrieben (die Eigenschaften in ihrer Reihenfolge, `null`, der Standard-Encoder: `"`, `<`,
  `>`, `&`, `'`, `+`, `` ` `` und alles außer druckbarem ASCII als `\uXXXX`).
* **fire.native.json**: Kommentare (`//`, `/* */`) und ein Komma vor `]`/`}` werden vor dem Lesen entfernt (`ReadCommentHandling.Skip`, `AllowTrailingCommas`); die
  Meldung einer kaputten Datei ist kürzer als die von `System.Text.Json`.

## Phase D Schritt 3: Projekte, Solutions, Pakete

Projekte und Solutions ([ROADMAP_C_PORT.md](../ROADMAP_C_PORT.md#phase-d---backend-und-werkzeuge-in-c), Schritt 3) in `native/forge/project/`, Stand de45e7e4 von
`src/fire.Project`, `src/fire.Package.Manager` und den Projektteilen von `src/fire.Compiler`: das Laden von `.fireproj`/`.firesln` (System.Text.Json mit den Optionen
von `FireProject`/`FireSolution`: Namen in jeder Schreibung, Kommentare, Kommas am Ende, die Meldungen des Deserialisierers samt Pfad und Position), die Dateien eines
Projekts (Muster, `exclude`, die Einstiegsdatei zuletzt, Markup zuerst, `testing/`, `templates/`), die Ordner (eingeschlossen, unentschieden, ausgeschlossen - mit
`.gitignore`), die Build-Aktionen (`buildActions`: Copy, CopyAlways, Ignore), die Bauordnung (`BuildOrder`), das Startprojekt, der `BuildPlan` mit den Bibliotheken
und den Natives der Projekte, der Linker mit einem Plan (die Einstellungen vor den Tags, `#import` der Bibliotheken, ihre Namensräume, die Natives der Projekte im
Overlay des Stores, eine Bibliothek ohne Einstiegspunkt), `ProjectBuilder.Check`/`PackLibrary`, das Paketformat `.fpk` (schreiben und lesen) und Publish.

```
forge-c build <projekt.fireproj | solution.firesln> [-p <projekt>] [--engine native] [-o <aus>] [-t <ziel>] [--toolchain <name>] [--quick[=0|1]] [-m <modus>] ...
                                                     (CommandLine.RunProject/BuildPlanned: eine Bibliothek wird gepackt - `name-version.fpk` im Ordner von -o, `output`
                                                     oder `bin/` -, ein Programm nativ gebaut, ohne -o nach `bin/<ziel>-<modus>/<name>`; die Copy-Dateien daneben)
forge-c build <...> --check                          (eine Bibliothek nur übersetzen)
forge-c build <...> --all [--engine native]          (Build Solution: jedes Projekt in der Bauordnung, wie `forge build --all` mit dem Text-Reporter)
forge-c publish <projekt | solution> [-p <projekt>] [--toolchain <name>] [--quick[=0|1]]   (ProjectPublisher: Programme nativ, Bibliotheken als Paket, in die Zielordner)
forge projectplan <datei>   /   forge-c projectplan <datei>   (das Maß: Workspace, Bauordnung, je Projekt Dateien, Ordner, Build-Aktionen, Publish-Ordner und der BuildPlan
                                                     mit Quellen (SHA-256), Bibliotheken, Einstellungen, Natives, Flatten einer Bibliothek)
forge fpk <datei.fpk>       /   forge-c fpk <datei.fpk>        (ein Paket, wie Fpk.ReadManifest es liest, und jeder Eintrag: Name, Größe, CRC, Attribute, SHA-256)
forge buildplan <optionen> <projekt>  /  forge fbc <modus|native> <projekt>   (wie für eine Datei, mit dem Programm des Startprojekts; `fbc native`: gelinkt wie für
                                                     einen nativen Build für diese Maschine - die Eingabe des Generators)
tests/compare-frontend.sh <forge> <forge-c> project [<ordner>]       (PROJECT_EXTRA="<ordner oder dateien>": weitere Solutions, nur gelesen)
tests/compare-frontend.sh <forge> <forge-c> projectbuild [<ordner>]  (echte Builds der Programmprojekte; PROJECT_COPY="<ordner>": `build --all` und `publish` an Kopien)
```

Ohne `--engine` baut `forge build` ein Programm für die VM; forge-c baut nur nativ (`unported: the engine vm`), verglichen wird mit `--engine native` (oder `"engine":
"native"` im Projekt). Publish hat `native` als Vorgabe.

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

Die drei Unterschiede bei `buildplan` liegen nicht im Projektteil: `Scene3D Models` sind die Modelle als Ressourcen (`ModelConverter`, Schritt 4; schon in Schritt 1 so),
`Raser.Viewer` und `Raser.Game` vertauschen im erzeugten C die Nummern zweier Klassen (`Raser.Chunk`, `Raser.ChunkManifest`: in C registriert `RegisterJsonTargets`
die zweite vor der ersten). Das gelinkte Programm ist gleich (`fbc native` byte-gleich), es ist eine Abweichung des Generators (Schritt 1), die erst ein so großes
Programm zeigt.

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
| `project/linkplan.c` | `Linker.CompileAndLink` mit einem Plan, für die VM (`Check`) und für ein Ziel (`NativeBuilder.Build` über `NbBuild.link`) | `Linker.cs`, `ProjectLibraries.cs`, `ProjectNativeOverlay.cs`, `ImportedPreludes.Insert` (mit `own`) |
| `project/fpk.c` | Zip schreiben und lesen (Deflate von `packages/compression`), `Fpk.Forge`, `ReadManifest`, `Extract` | `Fpk.cs` |
| `project/command.c` | `build` eines Projekts (`RunProject`, `BuildPlanned`, `--all` mit dem Text-Reporter), `Check`, `PackLibrary`, `ChooseEngine`, der Sammler der Warnungen | `CommandLine.cs`, `CommandLine.Report.cs`, `ProjectBuilder.cs` |
| `project/publisher.c` | Publish (Stempel, `Companions`, `CleanProblem`, Zusammenfassung) | `ProjectPublisher.cs` |
| `project/cli.c` | die Optionen von `build`/`publish` mit Projekt, `publish`, `projectplan`, `fpk`, `buildplan`/`fbc` eines Projekts | `CommandLine.cs` (Teile), `ProjectDump.cs`, `FrontendDump.cs` |

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
* **Was fehlt, sagt es** (`unported: ...`): die Engine `vm` (Programme, die die VM tragen), die C++-Natives eines Projekts in einem nativen Programm (der Generator liest
  Natives aus dem Store), `settings.globalJobs` eines Projekts, eine Paketreferenz `github:...` (die Lock-Datei des Stores). Die Meldungen des Deserialisierers geben den
  Pfad im JSON nur an der Wurzel genau an (`Path: $` bei Lesefehlern).
* **Nur POSIX**, wie der Builder.

## Phase D Schritt 4: der Rest der Befehlszeile

Der Rest der Befehlszeile ([ROADMAP_C_PORT.md](../ROADMAP_C_PORT.md#phase-d---backend-und-werkzeuge-in-c), Schritt 4): der **Modellwandler** in `native/forge/model/`
(Stand 6c1b0f0e von `src/fire.Compiler/ModelConverter.cs`, `BlockModel.cs`, `MeshModel.cs`) und **`forge test`** in `native/forge/tools/` (Stand b833e71b von
`TestDriver.cs`, `TestFilter.cs`, `TestRunner.cs`, `CommandLine.Test`), 1:1 (L3) wie die Schritte davor.

```
forge-c model <datei.gltf|.glb|.fblocks|.fmesh> [-o <datei.f3d>] [--fps n]   (wie `forge model`: die Modelldatei, "<aus> (<n> bytes)"; Fehler: "<datei>: <meldung>", 1)
forge-c test [<datei> | <projekt.fireproj> | <solution.firesln>] [-p <projekt>] [--engine vm|native] [-m <modus>] [--filter A.B,C] [--toolchain <name>] [--config <datei>]
                                                     (wie `forge test`: die Tests in der VM in C oder als natives Programm, die Liste und die Summen; Exitcode 0/1/2)
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
`--engine native` als natives Programm für diese Maschine (`nbBuild` mit dem Treiber, dann gestartet).

### Stand

| Messung | Dateien | gleich | noch nicht portiert | verschieden |
|---|---|---|---|---|
| `model`: das Korpus von `tests/model-corpus.py` (die Modelle des Repositorys, der Roboter als `.glb` und mit externem Puffer, 60 zufällige glTF mit jeder Komponentenart, `normalized`, Strides, Offsets, Matrizen, Bäumen, Szenen, Materialien, Animationen; 50 `.fblocks`, 40 `.fmesh`; 70 Fehlerpfade - fehlende Schlüssel, falsche Arten, Indizes außerhalb, kaputtes Base64, Fehler des JSON-Lesers, abgeschnittene `.glb` -; 270 beschädigte Kopien): Exitcode, Ausgabe, Fehler, `.f3d` | 576 | 576 (252 Modelle, 324 Fehler) | 0 | 0 |
| dasselbe Korpus als Ressource eines Programms (`fbc release`: die Modelldatei im Programm oder der Fehler des Präprozessors) | 576 | 576 | 0 | 0 |
| `fbc release` / `cgen default` / `buildplan default` und `tc:gcc+quick` über `tests/cases` / `packages` / `src/fire.Project` nach dem Modellwandler | 1129 / 93 / 34 | fbc 1129 / 93 / 34, cgen und buildplan 1119 / 93 / 34 | 10 (Reflection, Schritt 1) / 0 / 0 | 0 (`Scene3D Models` gleich) |
| `projectbuild` (gcc): die 25 Programmprojekte von `src/fire.Project` | 25 | 22 Programme byte-gleich, darunter `Scene3D Models` (Protokoll gleich) | 3 | 0 (ein Protokoll mit den Zeilen "Installing the standard package ..." des ersten Starts des C#-forge) |
| `test` (VM): jedes Paketprojekt von `packages` | 53 | 47 | 6 (die eigenen Natives eines Projekts, siehe unten) | 0 |
| `test` (VM und `--engine native`): die Quellen von `src/fire.Testing/TestingTests.cs` und weitere (Parameter mit Bereichen und Texten, Phasen, `[CleanTest]` wirft, `[PrepareTesting]` wirft, generische Klasse, Ausgaben mit Zeilenumbrüchen und Markern, `leave`, eine Ausnahme in einem Thread, keine Tests, ein Übersetzungsfehler, eine Solution aus Bibliothek und Programm, `-p`, `--filter` mit `*`, `-m debug`, eine fehlende Datei, ein Projekt mit fehlender Referenz) | 15 je Engine | 15 / 15 | 0 | 0 |

(Verglichen werden stdout, stderr und Exitcode; die Sekunden der Summenzeile sind ausgenommen, ebenso auf der Seite von C# die Zeilen "Building the native part of
the package ..." - sie hängen vom Cache der Bibliotheken ab.)

### Dateien

| Datei | Inhalt | Gegenstück in C# |
|---|---|---|
| `model/modelconverter.c` | was die C#-Dateien von .NET nehmen (die Operationen von `JsonElement` mit ihren Ausnahmen, `Math.Round` gerade, die sättigenden Umwandlungen, `Math.Min/Max` mit NaN und -0, `Enumerable.Min/Max`, `Array.BinarySearch`, `Convert.FromBase64String`, `Uri.UnescapeDataString`, `File.ReadAllBytes`), `Part`, `Write`, `QuatOf`, `PoseMatrix`, `Decompose`, `Read`, `Convert`, `ConvertFile` | `ModelConverter.cs` |
| `model/blockmodel.c` | `Parse` (mit `ParseParts`, `ParseAnimations`, `ParseColor`), `Set`, `FaceVisible`, `Quads`, `ToF3d`, `Rotation`, `PoseOf` | `BlockModel.cs` (Teile) |
| `model/meshmodel.c` | `CreateDefault`, `Parse`, `Normal`, `ToF3d` | `MeshModel.cs` (Teile) |
| `model/command.c` | `ResourceTable.Add` für Modelle, `fcResourceRead`, `forge-c model` | `Preprocessor.cs` (`ResourceTable`), `Program.cs` (`model`) |
| `tools/testdriver.c` | `TestFilter`, `TestDriver.Generate`, `fcTestDriverApply` (der Teil des Linkers) | `TestDriver.cs`, `TestFilter.cs`, `Linker.cs` (`TestDriver`) |
| `tools/testrunner.c` | `TestProtocol`, `RunInVm`, `RunNative`, `Driver`, `WriteSummary`, `forge-c test` | `TestRunner.cs`, `CommandLine.cs` (`Test`) |

`json.c` hat dafür einen strengen Modus (`JsonDocument.Parse` ohne Optionen, wie glTF gelesen wird: keine Kommentare, keine Kommas am Ende) und die Meldung einer Zahl, die
nach ihrem Bruchteil falsch endet ("Expected 'E' or 'e'.", wie .NET - vorher "Expected a delimiter.").

### Entscheidungen

* **Ausnahmen von .NET**: eine Ausnahme des Wandlers ist in C ein `FcError` mit dem Namen der .NET-Ausnahme und ihrer Meldung; die Aufrufer fangen wie in C# nur
  IOException, InvalidDataException, FormatException, KeyNotFoundException, InvalidOperationException und IndexOutOfRangeException. Eine andere (eine kaputte
  `.glb`: `JsonReaderException`; ein Puffer außerhalb der Liste: `ArgumentOutOfRangeException`) beendet `forge model` wie das C#-forge mit "Unhandled exception. ..."
  und 134, im Präprozessor ist sie das `internal: <typ>: <meldung>` von FrontendDump. Die Meldungen von .NET sind nachgeschrieben; gemessen sind die, die das Korpus
  erreicht.
* **Zahlen**: die Rechnungen stehen in der Reihenfolge von C# (kein FMA, `float` wo C# `float` rechnet: die Quantisierung der Ecken); `Math.Round` ist `nearbyint`.
* **Die VM für `forge test`**: FIRE_VM_EXE, sonst `fire` (`fire-i32`, ...) neben forge-c, wie `CVm.Locate` sie findet. Eine VM in den Cache zu bauen, wenn keine da
  ist, gehört zu `CVm` (die Engine `vm`, Schritt 3) und fehlt hier (`unported: ...`). Die Bibliotheken der Pakete des Stores findet die VM neben sich (`Packages/`).
* **Die eigenen Natives eines Projekts** (ein Paketprojekt mit `native`, getestet als Projekt): das C#-forge baut ihre Bibliothek für seine VM (die Überlagerung des
  Stores, `PackageLibrary`); forge-c nicht - braucht ein Test sie, sagt forge-c `unported: the natives of a project in the VM ...` (6 der 53 Paketprojekte; die übrigen
  rufen sie in ihren Tests nicht auf). Die native Engine hat dieselbe Lücke aus Schritt 3 (die C++-Natives eines Projekts in einem nativen Build).
* **Nicht portiert** (`forge-c test` sagt es): mehrere Dateien auf einmal, `-D`, `-f`, der Kreisel einer Konsole (`[\] ...` - nur in einem Terminal), das Abbrechen.
* **Beobachtet am C#-forge** (portiert wie es ist, gemeldet): eine `.glb` mit kaputtem JSON beendet `forge model` und den Build eines Programms, das sie einbindet, mit
  einer unbehandelten Ausnahme (`JsonReaderException` steht nicht in den gefangenen Arten, bei `.gltf` wird sie zu "Not glTF (...)"); ebenso ein Puffer-Index außerhalb
  (`ArgumentOutOfRangeException`) und ein negativer Offset eines Accessors; eine Chunk-Länge kleiner 0 einer unbekannten Art ließe die Schleife über die `.glb` nicht
  enden (forge-c bricht dort ab). `forge test <datei>` übersetzt die Datei als Text (`RuntimeSession.Build` ohne Pfad): ihr `#include` geht vom aktuellen Ordner aus,
  nicht von dem der Datei, und eine Meldung nennt die Datei nicht (forge-c macht es ebenso).

### Der Rest der Befehlszeile

Die Befehle des C#-forge (`Program.cs`, `CommandLine*.cs`) und was davon in forge-c ist:

| Befehl | forge-c |
|---|---|
| `tokens`, `ast`, `diagnostics`, `fbc`, `cgen`, `buildplan`, `projectplan`, `fpk`, `pkglibplan`, `bootimage`, `peres`, `toolchaintest` (Messungen) | ja |
| `ui`, `biscuit`, `fdata`, `falstad` | ja, ohne `-o` |
| `model` | ja (Schritt 4) |
| `test` | ja (Schritt 4), ohne mehrere Dateien, `-D`, `-f` |
| `build` | Datei und Projekt/Solution, nur `--engine native` (die Engine `vm`: Schritt 3), ohne `--report json`, `--overlay`, `--debug-info`, `-D`, `-i`, linkage static/shared/plugin, `--stats` |
| `publish` | ja (Schritt 3), Programme nativ |
| `bytecode` | eine Datei (`-o`, `-m`), ohne Projekte, `--report json`, `-D` |
| `serve` | ja (Phase C 6) |
| `data migrate\|status\|diff\|import` | **nein** - läuft wie `test` als Treiber in der VM (`DataCommand.RunDriver`); braucht dazu einen Link mit mehreren Quellen (die Dateien, eine Quelle mit `#import "data"` und dem Treiber des Schemas der URL) |
| `run` | **nein** (übersetzen und in der VM starten: der Weg von `test` ohne Treiber) |
| `native` (das C nach `out.c`), `dump` (ein Programm als Text) | **nein** (`cgen` und `fbc` messen dasselbe) |
| `stats` (zwei Berichte von `--stats`) | **nein** (mit `--stats`, Schritt 2) |
| `prebuild-standard`, `install-standard`, `build-vm`, `tables` | **nein** (Werkzeuge des Baus der Solution; `pkglib --all` baut die Bibliotheken) |
| der Packer | `fire.Runtime.Packer` packt nur noch mit `FIRE_VM_ENGINE=dotnet` die .NET-Einzeldatei - in C entfällt er; das Packen mit der VM in C (`CVm.Pack`, `FIREVMP1`) ist die Engine `vm` von Schritt 3 |

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
`tests/compare-frontend.sh ... project` (mit `PROJECT_EXTRA`) und `... projectbuild` (mit `PROJECT_COPY`).
Der Rest der Befehlszeile (Phase D Schritt 4): der Modellwandler (`native/forge/model/`) `src/fire.Compiler/ModelConverter.cs`, `BlockModel.cs`, `MeshModel.cs`,
`Preprocessor.cs` (`ResourceTable.Add`) - Stand 6c1b0f0e, das Maß ist `tests/compare-frontend.sh ... model`; `forge test` (`native/forge/tools/`) `TestDriver.cs`,
`TestFilter.cs`, `TestRunner.cs`, `CommandLine.cs` (`Test`), `Linker.cs` (`TestDriver`) - Stand b833e71b, gemessen mit `forge test` beider forges (Ausgabe ohne die
Sekunden, Fehler, Exitcode) an den Paketprojekten und den Quellen von `src/fire.Testing/TestingTests.cs`.
