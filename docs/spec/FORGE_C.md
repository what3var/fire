# forge in C (`native/forge`)

Status: **Phase C, Schritte 1 bis 4** aus [ROADMAP_C_PORT.md](../ROADMAP_C_PORT.md#phase-c---das-frontend-in-c) (19.11.2026): Lexer,
Präprozessor mit `#if`, Diagnosen, AST und Parser (1, 2); Resolver, Typprüfer und `SetterClaims` mit dem, was sie vom Linker brauchen - die
Direktiven eines Builds mit ihrer Wirkung, `#import` mit den Preludes und den Natives der installierten Pakete (3); der Bytecode-Compiler, was die
Typprüfung ihm hinterlässt (mit der Herleitung von `defined`), der Rest des Linkers und der Schreiber der Programmdatei `.fbc` (4); dazu **Schritt 6**,
die Sprachdienste als Prozess: `forge-c serve` ([FORGE_SERVE.md](FORGE_SERVE.md), [unten](#forge-serve)). Mechanischer Port 1:1 (L3): je C#-Datei eine C-Datei mit denselben Namen in derselben
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
forge-c serve [--tables <ordner>] [--packages <ordner>] [--debounce <ms>] [--log <datei>]   (die Sprachdienste, JSON-Zeilen über stdio: FORGE_SERVE.md;
                                                     -lpthread auf POSIX)
tests/compare-frontend.sh <forge> <forge-c> [tokens|ast|both|diag|fbc|all] [<ordner>]   (FRONTEND_MUTANTS=n: dazu n beschädigte Kopien je Quelle;
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

## Stand gegen den Korpus

| Teil | Dateien | `tokens` gleich | `ast` gleich | `diagnostics` gleich | `fbc` Debug | `fbc` Release | `fbc` Performance |
|---|---|---|---|---|---|---|---|
| `tests/cases` | 1129 | 1129 | 1129 | 1129 | 1129 | 1129 | 1129 |
| `packages` | 93 | 93 | 93 | 93 | 93 | 93 | 93 |
| `src/fire.Project` (Beispiele, Vorlagen) | 34 | 33 | 33 | 33 | 32 | 32 | 32 |
| beschädigte Kopien (`FRONTEND_MUTANTS=2`; `fbc`: `=1`) | 2512 | 2512 | 2512 | 2512 | - | 1256 von 1256 | - |
| der Korpus strikt (jede Quelle mit `//# types: strict`) | 1256 | - | - | 1255 | - | 1254 | - |

`fbc` über die Fälle: 957 der 1129 übersetzen (zu Programmdateien von 112 KB bis 1,8 MB - jede mit der ganzen Prelude und den Preludes ihrer
Importe), 172 sind Fehler des Frontends oder des Compilers; von den beschädigten Kopien übersetzen 361. `tests/run-cases-vm.sh` mit `forge-c`:
**1126 von 1128** richtig (1 nicht gelaufen) - dieselbe Zahl wie mit dem C#-forge ([VM_C.md](VM_C.md#stand-gegen-den-korpus)), die
Programmdateien sind dieselben, also auch die zwei falschen (`native/3210`, `native/4110`: Textumbruch der UI). Die zwei Abweichungen von `fbc`: `FxmlBinding` (`.fxml`, Schritt 5) und
`Scene3D Models`, das Modelle als Ressourcen einbindet (`new Resource("robot.gltf")`, `.fblocks`, `.fmesh`) - die macht der C#-Linker beim Einbinden
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

Die eine Abweichung: `Samples/Graphics and UI/FxmlBinding/main.fire` bindet mit `#include` eine `.fxml` ein - die Markup-Generatoren sind Phase C
Schritt 5 (`forge-c` meldet dort `internal: ... markup files are not ported yet`). Die beschädigten Kopien (abgeschnitten, ein Zeichen weg, ein
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
| `forge.c` | das Programm `forge-c` (`tokens`, `ast`, `diagnostics`, `fbc`, `bytecode`, `serve`) | `FrontendDump.cs`, `CommandLine.cs` (`Bytecode`) |
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
  `Parser.SourceImportNamespaces` (für den Resolver), die Ressourcen von `new Resource("...")` (nur mit einer `ResourceTable`), Markup über `#include`.
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

## Nachziehen

Ändert sich auf `master` eine der portierten C#-Dateien (`git log <commit>..master -- src/fire.Compiler/Lexer.cs` usw.; Schritt 3:
`Resolver*.cs`, `TypeChecker*.cs`, `SetterClaims.cs`, `Linker.cs`, `ImportedPreludes.cs`, `Values/Unit.cs` - Stand c7f13d97; Schritt 4: `Compiler.cs`,
`Compiler.Trees.cs`, `ReadingMethods.cs`, `Bytecode/ProgramFile.cs`, `Bytecode/Chunk.cs`, `Bytecode/FunctionProto.cs`, `Bytecode/RuntimeClass.cs`,
`Bytecode/DeclaredType.cs`, `Bytecode/DeclaredKind.cs`, `Runtime/FloatNarrowing.cs`, `PackageImports.cs` und wieder `TypeChecker.cs`/`Linker.cs` -
Stand 6bc618e7), wird die Änderung in die C-Datei übertragen und der Commit im Kopf nachgeführt; `tests/compare-frontend.sh` (mit
`FRONTEND_MUTANTS=2`, und `fbc`) zeigt, ob beide wieder gleich sind.
Eine neue Knotenart oder ein neues Feld kommt in `ast.h` an dieselbe Stelle wie im Record und in `ast.c` in die Ausgabe.
