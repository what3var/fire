# ScriptLang – Interpreter (WIP)

Objektorientierte Skriptsprache mit ownership-basiertem Scope-Modell,
eingeschränkten Lambda-Closures und einem Zahlensystem mit physikalischen
Einheiten. Siehe `docs/SPEC.md` für die vollständige Sprachspezifikation und
`docs/BYTECODE.md` für die Bytecode-ISA (Compiler + Stack-VM).

## Lokal bauen & testen

Voraussetzung: .NET 8 SDK (hier im Sandbox-Environment nicht verfügbar,
daher noch **ungetestet** – bitte einmal lokal verifizieren).

```bash
cd src/ScriptLang
dotnet run
```

`Program.cs` enthält aktuell einen manuellen Smoke-Test für Lexer und
Unit-System (noch kein Parser/Evaluator).

## Editor (`src/ScriptLang.Editor`)

Ein kleiner WPF-Editor mit Syntax-Highlighting und einem Step-Debugger, in
einem eigenen Unterprojekt, das per Projektreferenz direkt auf
Lexer/Parser/Resolver/Compiler/VM des Hauptprojekts zugreift (keine
kopierten/verlinkten DLLs). **Nur unter Windows lauffähig** (WPF). Öffnen
über `ScriptLang.sln` im Repo-Root, oder direkt:

```bash
cd src/ScriptLang.Editor
dotnet run
```

- **Syntax-Highlighting**: nutzt den ECHTEN `ScriptLang.Lexing.Lexer` zum
  Tokenisieren (keine zweite, eigene Tokenisierung) - Kommentare werden
  zusätzlich per einfacher Lücken-Suche erkannt, da der Lexer sie beim
  Tokenisieren selbst überspringt.
- **Step-Debugger, thread-fähig**: F5 kompiliert (inkl. Prelude) und
  bereitet die VM vor, F10 führt eine Quelltextzeile aus ("Step Over" -
  läuft nicht in tiefer verschachtelte Aufrufe hinein), F11 dasselbe als
  "Step Into" (springt bei einem Aufruf auf dessen erste Zeile), Shift+F11
  verlässt die aktuelle Funktion ("Step Out"), F9 setzt/entfernt einen
  Haltepunkt auf der Cursor-Zeile, F8 läuft bis zum nächsten Haltepunkt,
  Strg+F5 läuft bis zum Ende durch. Zeigt Aufruftiefe, Wert-Stack, `this`
  (falls gebunden) und eine nach Scope-Ebene gegliederte Baumsicht der
  aktiven Scope-Kette an (aktiver/innerster Block zuerst, dann
  umschließende Ebenen bis zur Funktionsgrenze, dann Global).
  Jeder per `fire` entstehende Thread bekommt automatisch eine eigene
  VM-Instanz UND taucht als eigener Eintrag im Threads-Panel auf (siehe
  `DebugThreadContext`/`Runtime.FireRuntime.ThreadBodyInterceptor`) - läuft
  standardmäßig automatisch weiter (wie ein "Weiter" mit Haltepunkten),
  bleibt aber an einem Haltepunkt stehen und kann dann per Klick im
  Threads-Panel ausgewählt und genauso einzeln durchgesteuert werden wie
  der Main-Thread. "Aktiven Thread anhalten" unterbricht einen gerade
  laufenden "Weiter"/"Bis Ende durchlaufen"-Schritt kooperativ an der
  nächsten Instruktion (bei Step Line/Into/Out nicht möglich - deren
  interne Schleife läuft direkt in der VM, dem Debugger nicht zugänglich;
  ein einzelner Schritt ist aber ohnehin fast immer schnell fertig).
  JEDE VM-Instanz (Main UND jeder Fire-Thread) läuft dabei auf ihrem
  EIGENEN Hintergrund-Thread, NIE auf dem UI-Thread - Steuerung von der UI
  aus ist bewusst nicht-blockierend (anfordern statt warten), sonst würde
  z.B. "Bis Ende durchlaufen" bei einem länger laufenden Skript die
  komplette Anwendung einfrieren.
- **Live-Fehleranalyse**: läuft debounced im Hintergrund (Parser + Resolver
  + Compiler; kennt die Standard-Prelude UND die Preludes von `#import
  "graphics"`/`"devices"`; Resolver/Compiler melden ALLE Fehler, nicht nur
  den ersten), zeigt Fehler in einem eigenen Panel (Doppelklick springt zur
  Zeile) und unterkringelt die betroffene Zeile im Editor direkt (eine
  ECHTE wellenförmige Linie über einen gekachelten Zickzack-Pinsel als
  Underline-Pen, nicht nur eine gerade rote Linie).
- **Sprung zu Definitionen** (Strg+Klick): auf eine Klasse springt zu deren
  `class`/`actor`-Deklaration, auf `x.Methode(...)`/`this.Methode(...)` zu
  deren Definition (Empfänger-Typ nach denselben Heuristiken wie die
  Autovervollständigung bestimmt - siehe dortige Einschränkungen),
  auf eine `#include "datei"`-Zeile öffnet die referenzierte Datei. Wird
  das Ziel nicht im aktuellen Dokument gefunden, aber es gibt `#include`s,
  wird zusätzlich in JEDER eingebundenen Datei gesucht (braucht dafür einen
  gespeicherten Pfad, um relative Include-Pfade auflösen zu können). Eine
  andere Datei öffnet sich in einem eigenen, schreibgeschützten Popup-
  Fenster (`FileViewerWindow`) mit eigenem Syntax-Highlighting und eigener,
  rekursiv verkettbarer Strg+Klick-Navigation (bewusst kein Tab-System -
  einfacher als eine vollständige Mehrdokument-Architektur, siehe dortigen
  Klassenkommentar für die Begründung).
- **Autovervollständigung**: automatisch nach `.` (Member-Vervollständigung),
  manuell per Strg+Leertaste (allgemeine Bezeichner-Vervollständigung -
  Keywords, Typ-Keywords, Klassen-/Enum-Namen, Parameter/lokale Variablen
  der umschließenden Funktion, Mitglieder der umschließenden Klasse).
  Pfeiltasten zum Navigieren, Enter/Tab/Doppelklick zum Übernehmen, Escape
  zum Schließen. Bewusst TOKEN-basiert (`ScriptSymbolIndex`, über den echten
  Lexer) statt über den echten Parser/Resolver - der scheitert beim Live-
  Tippen zu oft genau an der Cursor-Stelle und würde dann gar nichts liefern.
  Nach `Ausdruck.` wird der TYP des Ausdrucks hergeleitet
  (`ScriptSymbolIndex.ResolveReceiver`, Datei `ScriptSymbolIndex.Types.cs`)
  und nur dessen Mitglieder angeboten: `new X(...)`, `this`/`base`,
  Variablen/Parameter/Felder (auch ohne `this.`), Klassen-/Enum-Namen und
  beliebige Ketten daraus (`a.B().c[0].`). Variablentypen kommen aus
  `T x` (auch qualifiziert: `Geometry.Circle x`), typisierten Parametern,
  `foreach`, oder - bei `var x = ausdruck`/`x = ausdruck` - aus dem Typ des
  Ausdrucks (`var x : einheit` legt nur eine Einheit fest, der Typ kommt
  dann ebenfalls aus dem Initialisierer); bei
  Mitgliedern aus dem deklarierten Typ, sonst bei Methoden aus den
  `return`-Ausdrücken, bei Feldern aus `= new X()` bzw. `this.feld = ...`.
  Angezeigt werden auch geerbte Mitglieder (Basisklasse UND Interfaces, mit
  Klassenname dahinter; nähere Klassen ranken höher), `private`/`protected`
  nur, wo sie zugreifbar sind, bei `Klasse.` nur `static`-, bei einer
  Instanz nur Instanz-Mitglieder, bei `Enum.` die Enum-Werte, nach `new `
  nur Klassen. **Namespaces**: `Klassen` sind unter ihrem vollqualifizierten
  Namen indiziert (`Geometry.Circle`, wie beim Compiler); `Geometry.` zeigt
  deren Klassen/Interfaces/Enums und Unter-Namespaces (hinter `new
  Geometry.` nur Klassen und Namespaces), `Geometry.Circle.` die statischen
  Mitglieder, `new Geometry.Circle()` bzw. `Geometry.Circle c` liefern
  Instanzen. Ohne Qualifizierung erscheinen nur Klassen, die dort
  tatsächlich ansprechbar sind (aktueller Namespace oder `#using`, siehe
  `ContextAt`), sonst der Namespace oberster Ebene; nach `#using ` werden
  Namespaces vorgeschlagen. Typnamen in Basisklassen/Feldern werden relativ
  zum Namespace ihrer Klasse aufgelöst. Ein Ausdruck `Circle.` (statischer
  Zugriff) wird - wie im Resolver - nur über den EXAKT geschriebenen Namen
  aufgelöst, nicht über `#using`. Die Preludes von `#import "graphics"`/`"devices"` sind
  bekannt (siehe `ImportedPreludes`). NUR wenn sich der Typ gar nicht
  bestimmen lässt (dynamische Typisierung, z.B. `var x = irgendwas()` ohne
  Rückgabetyp), fällt es auf Mitglieder ALLER bekannten Klassen zurück,
  statt gar nichts vorzuschlagen (Klassen des Dokuments vor der Prelude).
- Dafür wurden zwei kleine, rein additive Debug-Erweiterungen ins
  Kernprojekt eingebaut: eine Zeilennummern-Tabelle im `Chunk`
  (`MarkLine`/`GetLine`) und eine öffentliche Stepping-/Inspektions-API in
  der `VM` (`StepInstruction`/`StepLine`/`StepInto`/`StepOut`/`Continue`/
  `DebugScopeChain`/`DebugGlobals`/`DebugThisDescription`/
  `DebugStackSnapshot` usw.) - siehe `docs/BYTECODE.md` Abschnitt 15. Die
  Autovervollständigung berührt das Kernprojekt dagegen gar nicht (rein
  innerhalb von `ScriptLang.Editor`).

**Bekannte, bewusste Einschränkungen** (Zeitbudget-Abwägung, kein
vollwertiges Debugger-/IDE-Feature-Set):
- Ein neu entstehender Fire-Thread bekommt einen SCHNAPPSCHUSS der
  aktuellen Haltepunkte (siehe `DebugSession.UpdateBreakpoints`) - danach
  im Editor gesetzte/entfernte Haltepunkte wirken sich auf einen bereits
  automatisch laufenden Fire-Thread erst beim nächsten Anstoß aus, nicht
  sofort (bewusst, um keine über mehrere Threads hinweg geteilte,
  nebenläufig veränderliche Breakpoint-Menge synchronisieren zu müssen).
- Die im Threads-Panel angezeigte aktuelle Zeile eines gerade automatisch
  laufenden (nicht angehaltenen) Fire-Threads wird ohne Sperre gelesen -
  theoretisch kann sie deshalb kurzzeitig veraltet/inkonsistent sein
  (keine Abstürze, nur eine rein kosmetische Ungenauigkeit).
- Variablen werden nur für **Parameter** der jeweiligen Funktions-Top-
  Level-Scope zuverlässig benannt angezeigt (`Chunk.DebugLocalNames` deckt
  nur diese ab) - `var`-Deklarationen in verschachtelten Blöcken erscheinen
  als `(local N)`, globale Variablen als `(global N)` (kein Namens-Register
  für Top-Level-`var`-Deklarationen).
- Die Autovervollständigung macht KEIN echtes Block-Scope-Tracking (eine
  Variable aus einem bereits verlassenen Geschwister-Block der SELBEN
  Funktion wird ggf. noch vorgeschlagen; Variablen anderer Funktionen sind
  dagegen nicht sichtbar) und keine echte Typinferenz: Klammerausdrücke
  `(a).`, Operator-Ergebnisse (`a + b`), Lambda-Aufrufe, Generics-
  Substitution (`Box<Animal>.item` bleibt `T`) und die Elemente einer
  `List` (dynamisch typisiert) sind nicht herleitbar - dort greift der
  Fallback (siehe oben). Die Herleitung ist rekursiv, mit Tiefenlimit.
- Die Cursor-Position wird beim Neu-Einfärben über (Absatz-Index, Zeichen-
  Offset INNERHALB dieses Absatzes) gesichert/wiederhergestellt, nicht über
  einen Gesamt-Dokument-Offset - WPFs `TextPointer.GetPositionAtOffset`
  zählt über Absatzgrenzen hinweg nämlich NICHT wie reine Zeichen (das
  Problem verschärft sich mit jeder Zeile vor dem Cursor); innerhalb EINES
  Absatzes stimmt die Zählung dagegen überein. Damit sollte die Position
  zuverlässig exakt erhalten bleiben - komplett ausschließen lässt sich bei
  dieser API aber nie ganz, dass es in einem Randfall trotzdem hakt.
- **Komplett ungetestet** (siehe unten) - insbesondere die WPF-Teile
  (`RichTextBox`/`FlowDocument`-Handling) sind eine bekannt fehleranfällige
  API-Ecke selbst für erfahrene WPF-Entwickler; hier ist mit Nacharbeit zu
  rechnen.

## Stand der Implementierung

- [x] `docs/SPEC.md` – Sprachspezifikation
- [x] `Values/Unit.cs` – Dimensionsvektoren, Präfixe, Umrechnung
- [x] `Values/Value.cs` – Laufzeitwerte, Coercion (`:`/`!`), einfache Arithmetik
- [x] `Lexing/Token.cs`, `Lexing/Lexer.cs` – Tokenizer
- [x] `Ast/Expr.cs`, `Ast/Stmt.cs` – AST-Knoten
- [x] `Parsing/Parser.cs` – rekursiver-Abstieg-Parser (Token-Stream -> AST)
- [x] `Resolving/Resolver.cs` – Variablen -> statische Slots (Depth+Index), Klassen-/`base`-/`return`-Validierung
- [x] `Runtime/Scope.cs`, `Runtime/ObjectInstance.cs`, `Runtime/LambdaValue.cs` – Ownership-Modell (TakeUpwards/TakeGlobal/TakeTo, Zyklenschutz, Kaskadenlöschung inkl. Race-Fall)
- [x] `Bytecode/OpCode.cs`, `Chunk.cs`, `Compiler.cs`, `VM.cs`, `NativeRegistry.cs` – Bytecode-Compiler + Stack-VM (siehe `docs/BYTECODE.md` für die ISA und den aktuellen Abdeckungsgrad)
- [x] `Ast/TypeRef.cs`, `extern`/`unsafe`-Syntax, Bitbreiten (`int[16]`), Array-Deklaratoren (`int x[]`), Pointer (`int[32]*`, `&`/`*`) – Parser + Resolver-Validierung vollständig, siehe `docs/SPEC.md` Abschnitt 8 für Details und offene Ausführungs-Lücken
- [x] `Values/NumericWidth.cs`, `Value.TruncateTo()` – Bitbreiten-Mechanismus (noch nicht automatisch bei jeder Zuweisung verdrahtet)
- [x] `Bytecode/FunctionProto.cs` + `Call`/`Return`/`MakeLambda`-Opcodes – Funktions-/Call-Frames: Lambdas sind aufrufbar, `return` funktioniert (siehe `docs/BYTECODE.md` Abschnitt 5)
- [x] `Bytecode/RuntimeClass.cs` + `NewObject`/`NewObjectOwned`/`GetField`/`SetField`/`LoadThis`/`CallMethod`/`CallBaseMethod`/`ConstructBase`-Opcodes – Klassen/Objekte: `new`, Felder, virtuelle Methoden, `this`/`base`, Konstruktor-Verkettung, korrekte Ownership-Politik bei direkter Feldzuweisung, Destruktor-Ausführung via verschachtelter VM-Ausführung (siehe `docs/BYTECODE.md` Abschnitt 6)
- [x] `Values/PointerTarget.cs`, `Runtime/PointerTargets.cs` + `AddressOfLocal`/`AddressOfGlobal`/`AddressOfField`/`PtrRead`/`PtrWrite`-Opcodes – Pointer/`unsafe`: echtes Aliasing auf Scope-Slots und Objekt-Feldern, Pointer-Arithmetik über `Value.Add`/`Subtract` (siehe `docs/BYTECODE.md` Abschnitt 7; Marshalling zu echten nativen Adressen folgt erst mit `extern`-Linking)
- [x] `Values/ScriptArray.cs` + `NewArray`/`MakeArrayLiteral`/`ArrayGet`/`ArraySet`-Opcodes – Arrays: `new Type[size]` (auch mehrdimensional/"jagged", `new int[3][4]`), Index-Zugriff, `.length`, Deklarator-Sugar `int arr[10]`/`int matrix[3][4]`, Array-Literale `[1,2,3]` (auch verschachtelt), Bounds-Checking als fangbare `IndexOutOfBoundsException` statt roher C#-Exception (siehe `docs/BYTECODE.md` Abschnitt 8)
- [x] `Bytecode/HandlerTemplate.cs` + `RegisterHandler`/`UnregisterHandler`/`Throw`-Opcodes – Exceptions: `throw`/`try`/`catch`/`finally`, typisiertes Matching über die Basisklassen-Kette, korrektes Unwinding inkl. Destruktor-Ausführung (siehe `docs/BYTECODE.md` Abschnitt 9)
- [x] `resume` – fortsetzbare Exceptions (`ResumeException`/`ClearPendingResume`-Opcodes, Wurfstellen-Zustand wird eingefroren statt sofort abgewickelt; bekannte Lücke bei frühem `return` im `catch`, siehe `docs/BYTECODE.md` Abschnitt 9)
- [x] `interface`-Konstrukt (reiner Resolver-Check, keine Laufzeit-Repräsentation) + `Standard/Prelude.cs` (`IEnumerable`/`IEnumerator`/`List` in ScriptLang selbst geschrieben) + `foreach` (`GetEnumerator`/`MoveNext`/`GetCurrent` per Duck-Typing) + `[]`-Operator-Überladung (`GetIndex`/`SetIndex` per Namenskonvention) – siehe `docs/BYTECODE.md` Abschnitt 10
- [x] `IsInUnit`/`IsOfType`/`IsFrom`-Opcodes – `is in`/`is of`/`is from`/`is under` (siehe `docs/BYTECODE.md` Abschnitt 11)
- [x] `extern`-Linking – `Bytecode/ExternRegistry.cs` + `CallExtern`-Opcode: zwei unabhängige Linking-Wege (manuelle Host-Registrierung `ExternRegistry.Register`, ODER dynamisches Laden per `#extern "libName"`-Direktive über `NativeLibrary.Load`/`Marshal.GetDelegateForFunctionPointer`, per Reflection zusammengebaute `Action<...>`/`Func<...>`-Delegates), Deklaration und Verlinkung bewusst getrennt (kompiliert immer, schlägt erst beim Aufruf fehl), echtes Pointer-Marshalling zu unmanaged Speicher (`Marshal.AllocHGlobal`, Copy-In/Copy-Out), Demo-Verlinkung gegen echte WinAPI-Funktionen per P/Invoke (siehe `docs/BYTECODE.md` Abschnitt 12; bekannte Grenze: kein Marshalling für Puffer/Arrays variabler Länge hinter einem Pointer)
- [x] `#include "fileName"` – `Parsing/Preprocessor.cs`: reine Textvorverarbeitung vor dem Lexer, rekursiv, "Include once"-Semantik, Zyklen-Erkennung (siehe `docs/SPEC.md` Abschnitt 8.1.2)

Alle ursprünglich geplanten Ausbaustufen sind damit umgesetzt (siehe `docs/BYTECODE.md` Abschnitt 13 für bekannte, bewusste Grenzen statt offener Punkte).

Neue, vom Nutzer vorgeschlagene Ausbaustufen (Stand: `readonly`/`enum` gerade fertiggestellt):
- [x] `#extern "libName"` (dynamisches Linking) + `#include "fileName"` (siehe oben)
- [x] `readonly`-Variablen/Konstanten – Resolver-Check, reine Compile-Zeit-Prüfung, kein eigener Opcode (siehe `docs/SPEC.md` Abschnitt 8.6)
- [x] Enumerationen (`enum`) – reine Compile-Zeit-Konstanten, `Name.Mitglied` löst der Compiler direkt zu einem Int-Literal auf (siehe `docs/SPEC.md` Abschnitt 8.7)
- [x] Properties (Getter/Setter mit feldartiger Syntax) – reine Namenskonvention `get_`/`set_` (wie `GetIndex`/`SetIndex`), kein eigener Opcode, `VM.GetField`/`SetField` fallen nur auf sie zurück, wenn kein gleichnamiges Feld existiert (siehe `docs/SPEC.md` Abschnitt 8.8, `docs/BYTECODE.md` Abschnitt 14)

Damit sind alle vier ursprünglich vorgeschlagenen Ideen umgesetzt.

Weitere, danach vorgeschlagene Ausbaustufen:
- [x] Methodenüberladung – nach Parameteranzahl (nicht nach Typ, da dynamisch typisiert), über die Basisklassen-Kette hinweg auflösbar; bewusst nicht für Konstruktoren (siehe `docs/SPEC.md` Abschnitt 5.4, `docs/BYTECODE.md` Abschnitt 16)
- [x] `with`-Statement (BASIC-artig) – reiner Parser-Zucker, komplett vor dem Resolven/Kompilieren aufgelöst, keine Laufzeit-Unterstützung nötig (siehe `docs/SPEC.md` Abschnitt 5.6)
- [x] Extension-Klassen (`class extends Name { ... }`) – Ruby-artiges "Reopening" bestehender Klassen (auch aus der Prelude, z.B. `List`), als eigener Merge-Pass vor dem Resolven (siehe `docs/SPEC.md` Abschnitt 5.5, `docs/BYTECODE.md` Abschnitt 16)
- [x] Konstruktor-Überladung – wie Methodenüberladung, aber ohne Basisklassen-Kette (siehe `docs/SPEC.md` Abschnitt 5.4)
- [x] Optionale Parameter mit Standardwert (`f(int x = 42)`) – für Methoden, Konstruktoren UND Lambdas, am Ende zusammenhängend (siehe `docs/SPEC.md` Abschnitt 5.4.1, `docs/BYTECODE.md` Abschnitt 16)
- [x] `switch`-Statement mit Vergleichsoperatoren (`case <= 1:`, `case default:`) – reiner Parser-Zucker, desugart zu einer If/Else-if-Kette, kein Fallthrough (siehe `docs/SPEC.md` Abschnitt 5.7, `docs/BYTECODE.md` Abschnitt 16)
- [x] Streams und Dateizugriff (`#import "io"`, `namespace IO`) – `IO.FileStream`/`IO.MemoryStream`/eigene Streams (`IO.IStream`, Basisklasse `IO.Stream`), typisierte Exceptions, `destruct()` schließt das Handle, Sicherheitsrichtlinie (`IoPolicy`) legt der HOST fest; dazu `IO.File`/`IO.Directory`/`IO.Path`/`IO.Utf8` (Datei- und Verzeichnis-API, UTF-8-Text), `IO.TextReader`/`IO.TextWriter` und `IO.Stdio` (Ziel bestimmt der Host: `IoStdio`) – siehe `docs/SPEC.md` Abschnitt 8.11, `docs/BYTECODE.md` Abschnitt 23
- [x] Generische Klassen (`class Name<T> where T is of X, is of Y`) – `,` = ODER zwischen Bedingungsgruppen, `:` = UND innerhalb einer Gruppe; Constraint-Prüfung statisch bei `new Name<...>(...)`; darf denselben Namen wie eine nicht-generische Klasse tragen (die Typ-Argument-Anzahl bei `new` wählt), **keine echte Typ-Substitution** (dynamisch typisierte Sprache) – generische Methoden werden geparst, aber ohne Typ-Argumente am Aufrufort geprüft (Mehrdeutigkeit mit Vergleichsoperatoren, siehe `docs/SPEC.md` Abschnitt 5.8)
- [x] `break`/`continue` für Schleifen (`while`/`for`/`foreach`) – echte Sprünge mit korrektem Scope-Unwind durch verschachtelte Blöcke; bricht die innerste Schleife; funktioniert **nicht** über eine `try`/`catch`/`finally`-Grenze hinweg (bewusste Einschränkung, siehe `docs/SPEC.md` Abschnitt 5.10, `docs/BYTECODE.md` Abschnitt 17)
- [x] Auto-Properties (`Typ Name { get; set; }`) – reiner Parser-Zucker, synthetisiert ein Backing-Field `_AutoName` samt trivialem Getter/Setter; das Backing-Field ist ein ganz normales, von innerhalb der Klasse direkt zugängliches Feld (z.B. um eine get-only Property im Konstruktor zu initialisieren) – siehe `docs/SPEC.md` Abschnitt 8.8, `docs/BYTECODE.md` Abschnitt 14
- [x] `lambda`-Typen mit Signatur (`[RückgabeTyp] lambda[<P1,...,Pn>]`, z.B. `lambda<int>` oder `int lambda<int,int>`) – der Rückgabetyp steht als Präfix vor `lambda` (eindeutig ohne Trennzeichen); anders als sonstige Typ-Annotationen wird die **Parameteranzahl tatsächlich zur Laufzeit geprüft** (bei `var`-Deklarationen und Funktionsparametern), Einzeltypen und Rückgabewert nicht (nicht verlässlich prüfbar in einer dynamisch typisierten Sprache) – siehe `docs/SPEC.md` Abschnitt 4.3, `docs/BYTECODE.md` Abschnitt 14
- [x] Multithreading (`docs/THREADING_DESIGN.md`) – Threads/Locking/`taking`/`sync`/`leave`/`terminate`/`catch threads`/`catch terminate`/Actors: Architektur UND Sprachsyntax vollständig umgesetzt, inklusive aller Randfälle (`fire MethodA(args)`-Aufrufform, `actor extends X`, mehrfaches `taking`)


## Bekannte offene Design-Fragen

Siehe Abschnitt 6 in `docs/SPEC.md`, insbesondere die `!`/`not`-Kollision
bei logischer Negation.
