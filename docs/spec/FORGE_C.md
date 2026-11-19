# forge in C (`native/forge`)

Status: **Phase C, Schritte 1 und 2** aus [ROADMAP_C_PORT.md](../ROADMAP_C_PORT.md#phase-c---das-frontend-in-c) (19.11.2026): Lexer,
Präprozessor mit `#if`, Diagnosen, AST und Parser. Mechanischer Port 1:1 (L3): je C#-Datei eine C-Datei mit denselben Namen in derselben
Reihenfolge, im Kopf jeder Datei die C#-Datei und der Commit, dem sie entspricht. Kein globaler Zustand und kein `exit` (L6): alles hängt an
`FcCtx`, Fehler kommen als Ausnahme auf dem Kontext beim Aufrufer an.

```
cc -O2 native/forge/forge.c -o forge-c -lm          (gcc 13, clang; -Wall -Wextra ohne Warnung; eine Datei, die alles einbindet)
forge-c tokens x.fire                                (die Token der vorverarbeiteten Quelle, dann die Diagnosen)
forge-c ast x.fire                                   (der Syntaxbaum, dann die Diagnosen)
forge-c tokens|ast --list <dateien.txt> --out <ordner>   (eine Ausgabe je Zeile der Liste: <ordner>/<n>.txt)
tests/compare-frontend.sh <forge> <forge-c> [tokens|ast|both] [<ordner>]   (FRONTEND_MUTANTS=n: dazu n beschädigte Kopien je Quelle)
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

## Stand gegen den Korpus

| Teil | Dateien | `tokens` gleich | `ast` gleich |
|---|---|---|---|
| `tests/cases` | 1129 | 1129 | 1129 |
| `packages` | 93 | 93 | 93 |
| `src/fire.Project` (Beispiele, Vorlagen) | 34 | 33 | 33 |
| beschädigte Kopien (`FRONTEND_MUTANTS=2`) | 2512 | 2512 | 2512 |

Die eine Abweichung: `Samples/Graphics and UI/FxmlBinding/main.fire` bindet mit `#include` eine `.fxml` ein - die Markup-Generatoren sind Phase C
Schritt 5 (`forge-c` meldet dort `internal: ... markup files are not ported yet`). Die beschädigten Kopien (abgeschnitten, ein Zeichen weg, ein
Stück wiederholt) laufen durch die Fehlerwege: 75 verschiedene Codes von Lexer, Präprozessor und Parser, alle gleich; ASan/UBSan ohne Befund.

Die Diagnosen der Fälle (`.diagnostics`, von `forge run` festgehalten): jede, die aus Lexer, Präprozessor oder Parser kommt, gibt `forge-c ast`
wörtlich so aus (`syntax` 25 von 29 Fällen mit Diagnosen, `diag` 12 von 26, `types` 7 von 160 - der Rest sind Codes des Resolvers und der
Typprüfung, Schritt 3).

Geschwindigkeit: `ast` über `tests/cases` und `packages` (1222 Dateien, ein Prozess) 0,55 s gegen 2,2 s des C#-forge.

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
| `forge.c` | das Programm `forge-c` (die beiden Ausgaben) | `FrontendDump.cs` |

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

## Nachziehen

Ändert sich auf `master` eine der portierten C#-Dateien (`git log <commit>..master -- src/fire.Compiler/Lexer.cs` usw.), wird die Änderung in die
C-Datei übertragen und der Commit im Kopf nachgeführt; `tests/compare-frontend.sh` (mit `FRONTEND_MUTANTS=2`) zeigt, ob beide wieder gleich sind.
Eine neue Knotenart oder ein neues Feld kommt in `ast.h` an dieselbe Stelle wie im Record und in `ast.c` in die Ausgabe.
