# forge in C (`native/forge`)

Status: **Phase C, Schritte 1 bis 5** aus [ROADMAP_C_PORT.md](../ROADMAP_C_PORT.md#phase-c---das-frontend-in-c) (20.11.2026), dazu der größte
Teil von **Phase D Schritt 1**, dem Generator Bytecode -> C ([unten](#phase-d-schritt-1-der-generator)): Lexer,
Präprozessor mit `#if`, Diagnosen, AST und Parser (1, 2); Resolver, Typprüfer und `SetterClaims` mit dem, was sie vom Linker brauchen - die
Direktiven eines Builds mit ihrer Wirkung, `#import` mit den Preludes und den Natives der installierten Pakete (3); der Bytecode-Compiler, was die
Typprüfung ihm hinterlässt (mit der Herleitung von `defined`), der Rest des Linkers und der Schreiber der Programmdatei `.fbc` (4); die
Markup-Generatoren - `.fxml` (Oberflächen), `.biscuit` (Schaltpläne), `.fdata` (Datenmodelle), der Import von Falstads Schaltungen - hinter
`#include` und als eigene Befehle (5). Mechanischer Port 1:1 (L3): je C#-Datei eine C-Datei mit denselben Namen in derselben
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
| `forge.c` | das Programm `forge-c` (`tokens`, `ast`, `diagnostics`, `fbc`, `bytecode`, `ui`, `biscuit`, `fdata`, `falstad`, `markup`) | `FrontendDump.cs`, `CommandLine.cs` (`Bytecode`) |

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
Einheiten (`ProgramSplit.cs`, `CppGenerator.Split.cs`), `linkage` static/shared (`CppGenerator.Modules.cs`, `ModuleDispatch.cs`, `StaticLinkage.cs`,
`Plugins.cs`: der Generator in C kennt nur die eine Rolle des ganzen Programms - `Modular`, `LibraryRole` sind `false`), die Pointer-Calls der
16-Bit-Ziele (`PointerCalls.cs`) und `--stats`.

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
| `gen/misc.c` | Konstanten (`Constant`, `DoubleLiteral` wie `"R"` von .NET, `CString`), `UnitId`, Prototypen, die Thunks von `Library.Get` (mit `LibraryMethods.Parse`), was jedes Programm von der Reflection braucht; die Stummel der nicht portierten Teile | `CppGenerator.cs`, `Modules.cs`, `Debug.cs`, `Reflection.cs`, `Split.cs` (Teile), `Standard/LibraryMethods.cs` |
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
Optionssätzen der Tabelle oben.
