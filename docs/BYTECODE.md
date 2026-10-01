# ScriptLang – Bytecode-ISA (v0.1)

Ergänzt `SPEC.md` um die konkrete Bytecode-Zielarchitektur. Stack-basierte VM,
bewusst klein und orthogonal – jede Instruktion soll sich später ohne größere
Umwege in eine kurze Sequenz nativer Instruktionen übersetzen lassen
(Registermaschine als Zwischenschritt zu echtem nativem Code).

## 1. Aufbau eines Chunks

- `Code`: Byte-Stream aus Opcode + Operanden (u16 = 2 Bytes little-endian, u8 = 1 Byte).
- `Constants`: Konstanten-Pool (`Value`), referenziert per u16-Index.
- `Units`: Einheiten-Pool (`Unit`), referenziert per u16-Index (statische Coercion-Ziele).

## 2. Opcodes

| Opcode | Operanden | Wirkung |
|---|---|---|
| `LoadConst` | u16 constIdx | push Constants[constIdx] |
| `Pop` | – | pop |
| `Dup` | – | push Peek() |
| `Swap` | – | vertauscht die obersten zwei Stack-Werte |
| `LoadLocal` | u16 depth, u16 slot | push GetAncestor(depth).GetSlot(slot) |
| `StoreLocal` | u16 depth, u16 slot | GetAncestor(depth).SetSlot(slot, Peek()) |
| `LoadGlobal` | u16 slot | push GlobalScope.GetSlot(slot) |
| `StoreGlobal` | u16 slot | GlobalScope.SetSlot(slot, Peek()) |
| `DeclareLocal` | – | pop v; CurrentScope.DefineSlot(v) |
| `Add`/`Sub`/`Mul`/`Div`/`Mod` | – | pop b, pop a; push a OP b |
| `Neg`/`LogicalNot`/`BitNot` | – | pop v; push OP v |
| `Eq`/`NotEq`/`Lt`/`LtEq`/`Gt`/`GtEq` | – | pop b, pop a; push (a OP b) als bool |
| `CoerceUnit` | u16 unitIdx | pop v; push v.CoerceUnit(Units[unitIdx]) |
| `CoerceUnitDynamic` | – | candidate=pop, anchor=peek; push candidate.CoerceUnit(anchor.Unit) |
| `CoerceType` | u8 typeTag | pop v; push v.CoerceType(TagToKind(typeTag)) |
| `CoerceTypeDynamic` | – | candidate=pop, anchor=peek; push candidate.CoerceType(anchor.Kind) |
| `Jump` | u16 addr | ip = addr |
| `JumpIfFalse` | u16 addr | cond=pop; if !cond: ip = addr |
| `JumpIfFalsePeek` | u16 addr | cond=peek; if !cond: ip = addr (kein Pop, für `&&`) |
| `JumpIfTruePeek` | u16 addr | cond=peek; if cond: ip = addr (kein Pop, für `\|\|`) |
| `EnterScope` | – | CurrentScope = new Scope(CurrentScope) |
| `ExitScope` | – | CurrentScope.Release(...); CurrentScope = Parent |
| `CallNative` | u16 nativeIdx, u8 argCount | ruft eine registrierte native Funktion auf |
| `CallExtern` | u16 nameIdx, u8 argCount | ruft eine per Host verlinkte extern-Funktion auf (siehe Abschnitt 12) |
| `MakeLambda` | u16 protoIdx, u8 hasOnTarget | erzeugt einen LambdaValue aus Functions[protoIdx] (pop On-Target-Wert falls hasOnTarget≠0) |
| `Call` | u8 argCount | ruft den Lambda-Wert unterhalb der Argumente auf (siehe Abschnitt 5) |
| `Return` | – | pop Rückgabewert; Scope/Chunk/ip des Aufrufers wiederherstellen |
| `CallProtoWithThis` | u16 protoIdx, u8 argCount | ruft Functions[protoIdx] mit 'this' = TOS-unterhalb-der-Args auf (Feld-Initialisierer) |
| `AddressOfLocal` | u16 depth, u16 slot | push Pointer auf GetAncestor(depth)-Slot(slot) |
| `AddressOfGlobal` | u16 slot | push Pointer auf GlobalScope-Slot(slot) |
| `AddressOfField` | u16 fieldNameIdx | pop obj; push Pointer auf obj.Fields[name] |
| `PtrRead` | – | pop ptr; push ptr.Read() |
| `PtrWrite` | – | pop value, pop ptr; ptr.Write(value); push value |
| `NewArray` | – | pop size (int); push neues Array der Länge size |
| `MakeArrayLiteral` | u16 count | pop count Werte (in Reihenfolge); push neues Array daraus |
| `ArrayGet` | – | pop index, pop array; push array[index] |
| `ArraySet` | – | pop value, pop index, pop array; array[index]=value; push value |
| `RegisterHandler` | u16 handlerTemplateIdx | registriert einen try-Handler (siehe Chunk.Handlers) |
| `UnregisterHandler` | – | entfernt den zuletzt registrierten Handler (try erfolgreich durchlaufen) |
| `Throw` | – | pop value; wirft (siehe VM.ThrowException) |
| `IsInUnit` | u16 unitIdx | pop value; push (value.Unit dimensional kompatibel zu Units[unitIdx]) |
| `IsOfType` | u16 typeNameIdx | pop value; push (value "is of" Typname) |
| `IsFrom` | u8 transitive | pop ownerVal, pop operandVal; push Ownership-Check-Ergebnis |
| `ResumeException` | – | pop resumeValue, pop excValue; setzt die eingefrorene Wurfstelle fort |
| `ClearPendingResume` | – | pop excValue; verwirft eine nie fortgesetzte eingefrorene Wurfstelle sauber |
| `Halt` | – | Ausführung beenden |

`TypeTag`: `Bool=0, Int=1, Float=2, Char=3, String=4` (entspricht genau den
Basistypen, die der Parser als `!`-Postfix-Ziel zulässt).

## 3. Warum echte `Scope`-Objekte statt eines flachen Stack-Frames?

Klassische Stack-VMs (z.B. clox) legen Locals flach auf einen Funktions-Stack
und brauchen nur bei Closures eine Sonderbehandlung ("Upvalues"). Hier ist es
umgekehrt: **jeder Block braucht ohnehin ein echtes `Runtime.Scope`-Objekt**,
weil das Ownership-Modell (SPEC 2) einen Scope als potenziellen Owner von
Objektinstanzen braucht, dessen `Release()` beim Verlassen kaskadierend
löscht. `EnterScope`/`ExitScope` erzeugen und lösen also *echte* Scope-Objekte
auf, nicht nur Index-Bereiche. Der Vorteil: Lambdas brauchen dadurch **keine**
Upvalue-Mechanik – sie sehen ohnehin nur ihren eigenen Scope + global (SPEC
4.2), das hat der Resolver schon durch die Parent-Verdrahtung gelöst (siehe
`Resolver.ResolveLambda`), zur Laufzeit ist das einfach ein Scope, dessen
`Parent` der globale Scope ist.

## 4. Die Anker-Regel zur Laufzeit (SPEC 3.2)

Wichtige Erkenntnis: Da Variablen ihre Einheit/ihren Typ erst als Teil ihres
`Value` zur Laufzeit tragen (nicht statisch im Typsystem), kann der Compiler
die Anker-Regel **nicht** vollständig zur Kompilierzeit auflösen. Er entscheidet
zur Kompilierzeit nur, *welcher* Operand (syntaktisch) der Anker ist -
`CoerceUnitDynamic`/`CoerceTypeDynamic` lesen den tatsächlichen Zielwert dann
zur Laufzeit vom Anker ab.

Reihenfolge wichtig: **Typ-Coercion vor Einheiten-Coercion.** Sonst geht bei
einer Kombination aus int→float-Konvertierung und Einheitenumrechnung
Präzision durch vorzeitige Ganzzahlrundung verloren (`500m` → `1km` statt
`0.5km`, wenn erst auf km gerundet und danach erst zu float promotet würde).

`Compiler.EmitCoercedOperands` fasst die Fälle zusammen:

- Genau ein Operand fordert weder `:` noch `!` ohne Argument an → er ist der
  Anker; der andere wird per `CoerceTypeDynamic`/`CoerceUnitDynamic` an ihn
  angeglichen (Stack-Reihenfolge ggf. per `Swap` hergestellt, da Operanden
  immer erst strikt links-nach-rechts ausgewertet werden, unabhängig davon,
  auf welcher Seite der Anker steht).
- Beide fordern "automatisch" (kein Argument) an → Einheit fällt auf
  `unitless` zurück (SPEC-Vorgabe); der Typ bleibt unangetastet (keine
  explizite SPEC-Vorgabe für diesen Fall - `Add`/`Sub`/etc. werten int+float
  ohnehin automatisch zu float auf, das deckt den praktischen Fall ab).
- Explizite Ziele (`a:km`, `a!int`) werden immer angewendet, unabhängig von
  der Anker-Frage.

## 5. Funktions-/Call-Frames (Lambdas, `return`)

Eine Lambda braucht - anders als klassische Closures - **keine Upvalues**:
da sie ohnehin nur ihren eigenen Scope + global sieht (SPEC 4.2), reicht ein
simpler eigener Adressraum (äußere Locals kommen als KOPIERTE Werte hinzu, siehe
Abschnitt 36). Konkret:

- Der Compiler kompiliert jeden Lambda-Body **einmal** in einen eigenen
  `Chunk` (`FunctionProto`, in `Chunk.Functions` abgelegt), unabhängig davon,
  wie oft die umgebende `LambdaExpr` zur Laufzeit ausgewertet wird (z.B. in
  einer Schleife). Jede Auswertung erzeugt nur einen neuen `LambdaValue`
  (`MakeLambda`), der denselben `FunctionProto` referenziert - nur das
  `on`-Ziel kann pro Auswertung unterschiedlich sein.
- `on obj` wird im **umschließenden** Scope kompiliert/ausgewertet (vor
  `MakeLambda`), nicht im eingeschränkten Lambda-Scope - konsistent mit dem
  Resolver (siehe `Resolver.ResolveLambda`).
- **Aufruf-Konvention** (`Call`): Operanden-Reihenfolge auf dem Stack vor dem
  Aufruf ist `[..., callee, arg0, arg1, ..., argN-1]` (Callee zuerst, dann
  Argumente in Deklarationsreihenfolge). Die VM legt einen `CallFrame`
  (Rückkehr-Chunk, Rückkehr-`ip`, Rückkehr-Scope) auf einen separaten
  Frame-Stack, erzeugt einen neuen `Scope` mit `Parent = GlobalScope` (genau
  wie der Resolver es für Lambdas vorsieht) und definiert die Argumente darin
  als Slots 0..N-1 - **nicht** über eigene Bytecode-Instruktionen im
  Funktionskörper, sondern direkt durch den `Call`-Opcode-Handler selbst, da
  die Parameterbindung Teil der Aufruf-Konvention ist, nicht des Bodys.
- `Return` poppt den Rückgabewert, ruft `Scope.Release(...)` auf dem
  aktuellen (Funktions-)Scope auf (Ownership-Kaskade, aktuell noch folgenlos
  ohne Klassen), stellt Chunk/ip/Scope des Aufrufers wieder her und pusht den
  Rückgabewert zurück auf den (gemeinsamen) Werte-Stack. Fällt ein
  Lambda-Body ohne explizites `return` durch, hängt der Compiler implizit
  `LoadConst undefined; Return` an.
- Argumentanzahl-Mismatch beim Aufruf ist aktuell ein harter Laufzeitfehler
  (keine variable Arität/Default-Werte) - bewusste Vereinfachung für diese
  Ausbaustufe.

## 6. Klassen/Objekte im Bytecode

Baut direkt auf den Funktions-/Call-Frames (Abschnitt 5) auf - derselbe
Push-Frame-/Chunk-Wechsel-Mechanismus, nur zusätzlich mit einer `this`-Bindung
(`_currentThis` in der VM, pro Frame gesichert/wiederhergestellt).

- **Kompilierte Klassen-Metadaten** (`RuntimeClass`, `Compiler.CompileClasses`
  als Vorab-Pass wie beim Resolver): Felder als 0-Arg-`FunctionProto`s (mit
  `this` ausgewertet), Methoden als `Dictionary<string, FunctionProto>`
  (Grundlage der virtuellen Auflösung), ein Konstruktor-Proto (**immer
  vorhanden** - wird synthetisiert, wenn die Klasse keinen eigenen
  `construct` deklariert, damit `new` einheitlich funktioniert).
- **Konstruktor-Kette**: `CompileConstructorProto` kompiliert je Klasse:
  `[Basis-Konstruktor-Aufruf (ConstructBase, explizit mit Argumenten oder
  implizit mit 0)] -> [eigene Feld-Initialisierer] -> [eigener Body] ->
  return undefined`. `ConstructBase` verwendet denselben Frame-Mechanismus
  wie `Call`, verändert aber bewusst NICHT `_currentThis` - dieselbe Instanz
  wird weiter konstruiert, nicht eine neue erzeugt.
- **`NewObject`**: erzeugt die `ObjectInstance`, markiert den Frame mit
  `ConstructedInstance` - beim passenden `Return` wird dadurch NICHT der
  (verworfene) Konstruktor-Rückgabewert gepusht, sondern die neue Instanz.
- **Virtuelle Methodenauflösung** (`CallMethod`): sucht beim tatsächlichen
  Laufzeit-Typ von `obj` beginnend über `RuntimeClass.FindMethod` (eigene
  Klasse zuerst, dann die Basisklassen-Kette) - das ist der ganze
  "Override"-Mechanismus.
- **`base.Method(...)`** (`CallBaseMethod`): wird **statisch zur
  Kompilierzeit** auf die Basisklasse der *deklarierenden* Klasse aufgelöst
  (`Compiler._enclosingClass`, durch alle Klassen-Kompilierfunktionen
  durchgereicht) - nicht auf die Basisklasse der tatsächlichen
  Laufzeit-Instanz. Sonst wäre das bei dreistufiger Vererbung (C : B : A,
  B.Method ruft base.Method) falsch, wenn `this` zur Laufzeit eine
  C-Instanz ist: `base` in B muss immer A sein, unabhängig vom tatsächlichen
  Instanztyp.
- **Felder** (`GetField`/`SetField`): dynamischer Dictionary-Zugriff über
  `ObjectInstance.Fields`, kein Slot - Feldnamen sind einfache String-
  Konstanten im Konstanten-Pool.

**Beide zuvor offenen Vereinfachungen sind jetzt behoben:**
- **Ownership-Politik bei `new`** (`NewObjectOwned`-Opcode): `obj.feld = new Foo(...)` erkennt der Compiler an der AST-Form (`AssignExpr` mit `MemberExpr`-Ziel und `NewExpr`-Wert) und dupliziert das Zielobjekt auf dem Stack, bevor die Konstruktor-Argumente folgen - dadurch bekommt die neue Instanz das Zielobjekt als Owner, nicht den aktuellen Scope (SPEC 2.1).
- **Destruktor-Ausführung**: gelöst über eine verschachtelte, aber denselben Frame-Stack nutzende Ausführung (`VM.RunNestedUntil`) - die Hauptschleife wurde dafür in eine wiederverwendbare `Execute(OpCode)`-Methode aufgeteilt. `VM` implementiert jetzt selbst `IDestructRunner`: wird mitten in `ExitScope`/`Return` ein Destruktor fällig, wird ein Frame gepusht und die Ausführung läuft (rekursiv verschachtelbar, falls ein Destruktor weitere Destruktoren auslöst) bis exakt zu dem Punkt, an dem dieser eine Frame per `RETURN` wieder abgebaut ist - der äußere Opcode-Handler sieht danach wieder konsistenten Zustand. **Wichtige Falle dabei**: `RETURN` pusht immer einen Rückgabewert auf den Werte-Stack; bei einem normalen Aufruf holt der Aufrufer ihn ab, bei einem Destruktor-Aufruf (kein Ausdruckskontext) muss `RunDestructor` ihn explizit selbst wegpoppen, sonst "verwächst" der Stack bei jeder Destruktor-Ausführung um einen Wert.

## 7. Pointer/unsafe

**Design-Entscheidung:** Statt eines rohen Byte-Puffers (der in einer
verwalteten .NET-Umgebung ohne echten Unsafe-Interop fragil und aufwändig
wäre) zeigt ein Pointer auf einen *existierenden, verwalteten Speicherort*:
entweder einen Scope-Slot (`Runtime.ScopeSlotPointerTarget`) oder ein
Objekt-Feld (`Runtime.FieldPointerTarget`) – beide implementieren das
gemeinsame, in `Values` liegende `PointerTarget` (dieselbe
Schichtungs-Regel wie bei `ObjectInstance`/`LambdaValue`: konkrete
Runtime-Typen werden in `Values` nur lose über die abstrakte Basisklasse
referenziert, nie umgekehrt).

- **Echtes Aliasing**: `*p = x` schreibt direkt in den Scope-Slot bzw. das
  Feld, auf das `p` zeigt – keine Kopie. Das nutzt die schon vorhandene,
  korrekte Scope-/Ownership-Infrastruktur, statt sie zu duplizieren.
- **`&ausdruck`** ist nur auf *adressierbare* Ausdrücke anwendbar –
  Identifier (lokal/global) oder `obj.feld` – analog zu lvalues in C#.
  `CompileAddressOf` prüft das explizit; alles andere (z.B. `&(a+b)`) wirft
  einen klaren Compile-Fehler.
- **Pointer-Arithmetik** (`ptr + n`, `ptr - n`) läuft über die normalen
  `Add`/`Sub`-Opcodes – `Value.Add`/`Value.Subtract` erkennen den
  Pointer-Fall zur Laufzeit selbst (kein eigener Opcode nötig, dieselbe
  Technik wie bei der String-Konkatenation über `+`). "n weiter" bedeutet
  "n logische Elemente weiter" (bei einem Scope-Slot-Pointer: n Slots in
  derselben, ohnehin zusammenhängend gespeicherten Slot-Liste; bei einem
  Feld-Pointer nur bei `n=0` gültig, da Felder nicht zusammenhängend liegen).
- **`unsafe { }`** selbst erzeugt keinen eigenen Code – die Berechtigung
  (Dereferenzierung/Address-of nur innerhalb eines solchen Blocks) prüft
  bereits der Resolver (`_unsafeDepth`-Zähler), der Compiler kompiliert den
  Block-Body einfach normal.
- **Bewusst zurückgestellt**: die Umwandlung eines solchen "verwalteten"
  Pointers in eine echte native Speicheradresse (für tatsächliche
  `extern`-Aufrufe) ist noch nicht gebaut – das gehört zum `extern`-Linking
  selbst (Marshalling in einen gepinnten/unmanaged Puffer), nicht zu dieser
  Ausbaustufe.

## 8. Arrays

Arrays fester Größe (`ScriptArray` in `Values`, braucht wie `Value` selbst
keine Runtime-Abhängigkeit - reine Value-Liste). `new Type[size]` →
`NewArray`-Opcode (pop Größe, erzeugt Array mit `undefined`-Elementen).
Index-Zugriff (`arr[i]`, lesend über `ArrayGet`, schreibend über `ArraySet`)
und `arr.length` (Sonderfall in `GetField`, da Arrays keine `ObjectInstance`
sind).

**Mehrdimensionale ("jagged") Arrays** (SPEC 8.4: mehrere `[...]`-Gruppen =
Array von Arrays, keine rechteckige Matrix): `new int[3][4]` und der
Deklarator-Zucker `int matrix[3][4]` laufen beide über
`Compiler.CompileArrayAlloc` - rekursiv, äußeres Array allozieren, dann
(falls die nächste Dimension ebenfalls eine Größe hat) jedes Element per
ECHTER Laufzeit-Schleife (Größen sind Ausdrücke, keine Compile-Zeit-
Konstanten, also kein Unrolling) mit einem rekursiv allozierten inneren
Array befüllen. Eine Dimension ohne Größe (`int arr[3][]`) bricht die
Rekursion ab, die Slots ab dort bleiben `undefined`. Die dafür temporär
angelegte Schleifen-Scope wird bewusst per `EnterScope`/`ExitScope` sauber
wieder verlassen, BEVOR der fertige Wert zurückgegeben wird - unbedenklich,
weil Arrays (anders als class-Instanzen) nicht am Ownership-System hängen,
`Release()` betrifft dort also nur die temporären Slots, nicht den
Array-Wert selbst.

**Array-Literale** (`[1, 2, 3]`, verschachtelt `[[1,2],[3,4]]`): eigener
`MakeArrayLiteral`-Opcode (u16 count), pop `count` Werte in Reihenfolge,
baut daraus ein `ScriptArray`. Ein Element, das selbst wieder ein
Array-Literal ist, ergibt ganz natürlich ein verschachteltes Array - keine
Sonderbehandlung nötig, das läuft einfach über normale rekursive Kompilierung
der Elemente.

**Bounds-Checking als fangbare Skript-Exception**: `ScriptArray.TryGet`/
`TrySet` (siehe Abschnitt 19, `Values.ByteBuffer` ebenso) liefern bei einem
ungültigen Index `false` statt zu werfen (bewusst OHNE C#-Exception im Hot
Path, siehe docs/PORTING.md) - `ArrayGet`/`ArraySet` prüfen genau DIESEN
Rückgabewert und rufen bei `false` `VM.ThrowIndexOutOfBounds` auf, das eine
`IndexOutOfBoundsException`-Instanz (Prelude, `: Exception`) verschachtelt
konstruiert (`ConstructNested`, dieselbe Technik wie `CallMethodNested`) und
ganz normal über `ThrowException` wirft - ab da läuft alles wie ein
gewöhnlicher `throw` (fangbar, `resume`-fähig, Ownership-Kaskade beim
Unwinding). Die `[]`-Operator-Überladung (`GetIndex`/`SetIndex`, Abschnitt
10) profitiert automatisch mit: `List` indiziert intern über genau dieselben
Opcodes, ein ungültiger Index dort landet also ebenfalls als fangbare
`IndexOutOfBoundsException`, ohne dass `List` selbst dafür irgendetwas tun
müsste.

**Wichtige Voraussetzung**: `IndexOutOfBoundsException` lebt in der Prelude
(wie `List`/`IEnumerable`) - ein Programm, das mit `Parser.Parse(...)`
(ohne Prelude) kompiliert wird, kennt diesen Klassennamen NICHT. Das wirft
schon beim Resolven eines `catch (IndexOutOfBoundsException e)` einen
klaren Fehler ("Unbekannter Exception-Typ"), lange bevor überhaupt ein
Index verletzt wird - sobald irgendwo im Programm `catch` auf diesen Typ
lauern soll (nicht nur beim tatsächlichen Werfen!), muss also `Parser.
ParseWithPrelude(...)` statt `Parser.Parse(...)` verwendet werden.

**Eine subtile Falle dabei** (`CallMethodNested`, siehe Abschnitt 10): wird
eine Exception INNERHALB einer verschachtelten Methode wie `SetIndex`
geworfen und von einem `catch` AUSSERHALB dieses Aufrufs gefangen, springt
`ThrowException` per Continuation-Erfassung direkt dorthin - `SetIndex`
selbst erreicht dann NIE sein eigenes `Return`, es wurde also auch NIE ein
Rückgabewert gepusht. `CallMethodNested` erkennt das (nicht über die
Frame-Tiefe, die könnte zufällig wieder passen, sondern robust darüber, ob
Chunk/Ip/Scope nach der verschachtelten Ausführung exakt wieder beim
Ausgangszustand gelandet sind) und liefert `null` statt eines `Value` -
`ArrayGet`/`ArraySet` lassen ihr eigenes `Push` dann ebenfalls aus, sonst
bliebe ein überzähliger Wert auf dem Stack zurück, der die (bereits an ganz
anderer Stelle weiterlaufende) Ausführung durcheinanderbringen würde.

## 9. Exceptions (`throw`/`try`/`catch`/`finally`, inkl. `resume`)

Da die VM für Skript-Aufrufe ohnehin keinen echten C#-Rekursions-Stack nutzt
(alles läuft über den expliziten `_frames`-Stack + `_currentChunk`/`_ip`/
`_currentScope`), ist `throw` als reine Zustandsmanipulation umgesetzt - kein
C#-Exception-Mechanismus für die Skript-Ebene nötig (nur für den
"unbehandelt"-Fall, siehe unten).

- **Handler-Registrierung**: `RegisterHandler` legt beim Betreten eines
  `try` einen `ActiveHandler` auf `VM._handlers` ab: Frame-Tiefe + Ziel-Scope
  zum Zeitpunkt der Registrierung (für späteres Unwinding), plus die
  kompilierten Catch-Adressen (mit optionalem Typnamen fürs Matching) und
  einen optionalen `finally`-Proto-Index (`Chunk.Handlers`/`HandlerTemplate`,
  analog zu Constants/Units/Functions).
- **Zwei Kompilate für `finally`**: der Compiler kompiliert einen
  `finally`-Block ZWEIMAL - einmal inline in den umgebenden Chunk (normaler
  Sprung-/Fallthrough-Fluss für "try erfolgreich" oder "hier gefangen"), und
  einmal separat als eigenständigen 0-Arg-`FunctionProto` (für den Fall, dass
  eine Exception an diesem Handler *vorbeipropagiert* - dafür muss die VM ihn
  verschachtelt aufrufen können, siehe `VM.RunFinallyNested`, dieselbe
  Technik wie bei Destruktoren). Ein einzelnes Kompilat hätte nicht
  ausgedrückt werden können, welches der beiden "was passiert danach"-
  Verhalten gilt.
- **`Throw`**: `VM.ThrowException` sucht von innen nach außen durch
  `_handlers`, für jeden Handler geprüft per `FindMatchingCatch` (Basisklassen-
  Kette hochlaufen, Namensvergleich - `"Exception"` matcht immer, da die
  eingebaute Basisklasse keine eigene `RuntimeClass` hat). Handler, an denen
  vorbeipropagiert wird, laufen trotzdem ihr `finally` (falls vorhanden), bevor
  die Suche weitergeht. Kein Match irgendwo → `UncaughtScriptException`
  (echter C#-Fehler, bricht `VM.Run()` ab).
- **`UnwindTo`**: löst Scopes bis zum Ziel-Scope/-Frame korrekt per
  `Scope.Release` auf (Ownership-Kaskade inkl. Destruktoren laufen also auch
  beim Abbruch durch eine Exception). Erkennt Frame-Grenzen daran, dass JEDE
  Frame-Basis-Scope als Parent immer direkt den globalen Scope hat (so legen
  `Call`/`CallMethod`/`NewObject`/etc. ihre Scopes an) - dafür ist kein
  separater Tracking-Stack nötig. Läuft NUR noch für Handler, an denen
  vorbeipropagiert wird (kein Match) - für einen TREFFER wird stattdessen
  eingefroren, siehe `resume` unten.
- **Wichtige Falle**: das geworfene Exception-Objekt gehört noch dem
  werfenden Scope - ohne Gegenmaßnahme würde es beim (evtl. verzögerten)
  Unwinding selbst kaskadierend mit zerstört, bevor der `catch`-Block es lesen
  kann. `ThrowException` übergibt es deshalb sofort per `TakeGlobal` dem
  globalen Scope.

### `resume`

Statt bei einem Handler-Treffer sofort destruktiv abzuwickeln, **friert**
`ThrowException` den Wurfstellen-Zustand ein (`CaptureContinuation`): die
Frames zwischen Wurfstelle und Handler werden zwar von `_frames` abgehoben
(genau wie bei `UnwindTo`, inkl. korrektem Nachziehen von `_currentThis` pro
Frame), aber NICHT per `Release()` zerstört - nichts wird kaskadiert
gelöscht, solange nicht klar ist, ob `resume()` noch kommt. Das Ergebnis
(`SavedContinuation`: Chunk/Ip/Scope/This/Frames) wird zusammen mit dem
`ActiveHandler` selbst in `VM._pendingResumes` unter der Exception-Instanz
abgelegt (`PendingResume`), dann geht's normal in den `catch` hinein.

- **`e.resume(wert)`** ist kein echter Methodenaufruf, sondern ein
  reservierter Name (wie `GetIndex`/`GetEnumerator`), den der Compiler direkt
  zu einem eigenen Opcode `ResumeException` kompiliert (`resume()` ohne
  Argument → `resume(undefined)`). Die VM sucht den `PendingResume`-Eintrag
  zur Exception-Instanz, wickelt den GERADE laufenden `catch`-Kontext ab
  (`UnwindTo` bis zur Handler-Registrierung - funktioniert auch, wenn
  `resume()` aus einem verschachtelten Funktionsaufruf INNERHALB des `catch`
  heraus kommt), spielt dann die gesicherten Frames zurück, setzt
  Chunk/Ip/Scope/This auf den eingefrorenen Wurfstellen-Zustand - und
  registriert den Handler NEU (`_handlers.Add`), da die fortgesetzte Stelle
  konzeptionell "immer noch im `try`" ist (ein erneuter `throw` dort muss
  wieder denselben `catch` erreichen können, und `UnregisterHandler` am Ende
  des `try`-Blocks erwartet seinen Eintrag). Der `resume`-Wert landet als
  Rückgabewert von `throw` auf dem Stack.
- **`ThrowStmt` bekommt ein zusätzliches `Pop`** nach `Throw` (das
  `ThrowExpr` NICHT hat) - sonst würde ein `resume()` bei `throw` als
  Statement einen Wert auf dem Stack hinterlassen, den niemand konsumiert.
- **Aufräumen ohne `resume`**: läuft ein `catch`-Block normal durch, OHNE
  `resume()` aufzurufen, muss die eingefrorene Continuation nachträglich
  sauber verworfen werden (sonst Ownership-Leck: die Exception-Objekte
  würden nie destruiert, die Scope-Kette bliebe für immer referenziert). Der
  Compiler emittiert dafür am Ende jedes `catch`-Zweigs (vor dessen
  `ExitScope`) `LoadLocal(0,0)` (die Exception-Variable liegt dort immer an
  Depth 0/Slot 0) + `ClearPendingResume`, das `DiscardContinuation` aufruft -
  exakt `UnwindTo`s Logik, nur auf den GESICHERTEN Frames/Scopes statt auf
  dem Live-Zustand. Wurde `resume()` bereits aufgerufen, ist der Eintrag
  schon weg und das ist ein No-op.
- **Bekannte Lücke**: verlässt ein `catch`-Block seinen normalen
  Fall-Through-Pfad anders (z.B. ein `return` mitten im `catch`, ohne vorher
  `resume()` aufzurufen), wird die `ClearPendingResume`-Aufräumstelle am Ende
  des Zweigs nicht erreicht - die eingefrorene Continuation bleibt dann
  bestehen (funktional harmlos, da nie wieder abgerufen, aber ein
  Ownership-/Speicher-Leck). Für eine vollständige Lösung müsste jeder
  mögliche Austrittspfad aus einem `catch`-Block das Aufräumen mit
  auslösen - bewusst nicht in dieser Ausbaustufe behandelt.

## 10. Interfaces + `IEnumerable`/`IEnumerator`/`List` + `foreach`

**Kernentscheidung:** Da Methodenaufruf in dieser Sprache immer ein
dynamischer Namens-Lookup ist (kein statisches Typsystem, keine Vtables),
brauchen Interfaces **keine eigene Laufzeit-Repräsentation**. Sie sind rein
ein Resolver-Check ("erfüllt diese Klasse - inkl. geerbter Methoden über die
Basisklassen-Kette - alle Methoden des Interfaces per Name+Arität"). Weder
`RuntimeClass` noch die VM wissen überhaupt, dass es Interfaces gibt.

- **Grammatik**: `class Foo : Bar, IBaz, IQux` - der Parser sammelt nach `:`
  einfach eine rohe Namensliste (`ClassDecl.BaseNames`), da er zum
  Parse-Zeitpunkt noch nicht wissen kann, welcher Name eine Klasse und
  welche Interfaces sind. Der Resolver löst das auf: höchstens ein Name in
  der Liste darf eine bekannte Klasse (oder `"Exception"`) sein (wird zur
  Basisklasse), alle anderen müssen bekannte Interfaces sein - **egal in
  welcher Reihenfolge** (anders als bei C#, wo die Basisklasse zwingend
  zuerst stehen muss).
- **`interface Name { [ReturnType] Method(params) ... }`**: reine
  Methodensignaturen ohne Body, geparst wie Klassen-Methodenköpfe nur ohne
  `{ }`-Body, terminiert durch `;`/Zeilenumbruch. Keine Felder, kein
  Konstruktor.
- **`List`** (und `IEnumerable`/`IEnumerator` selbst) sind bewusst **in
  ScriptLang selbst geschrieben**, nicht nativ in C# (`Standard/Prelude.cs`,
  ein Stück ScriptLang-Quelltext, das `Parser.ParseWithPrelude` vor jedes
  Programm setzt) - klassisches dynamisches Array (verdoppelt sich bei
  Bedarf), nutzt die Arrays/Klassen/Interfaces, die schon stehen, statt eine
  Sonderimplementierung zu brauchen.
- **`foreach (x in iterable)`** kompiliert rein über Namens-Dispatch:
  `iterable.GetEnumerator()`, dann pro Iteration `.MoveNext()`/`.GetCurrent()`
  - funktioniert dadurch auf JEDEM Objekt mit diesen drei Methoden, nicht nur
  auf offiziell `: IEnumerable`-deklarierten Klassen (Duck-Typing, wie
  Methodenaufruf hier ohnehin überall funktioniert). Der Enumerator selbst
  lebt bewusst auf dem Werte-Stack (per `Dup` dupliziert), nicht in einem
  eigenen Scope-Slot - der Resolver kennt für `foreach` nur einen Scope (die
  Schleifenvariable), ein zusätzlicher Slot hätte dessen Tiefenrechnung
  inkonsistent gemacht.
- **Wichtiger Fund beim Testen der eigenen Prelude**: `return new Foo(...)`
  aus einer Methode heraus hätte die gerade erzeugte Instanz sofort wieder
  zerstört, weil sie noch dem (gleich per `Release()` aufgelösten)
  Methoden-Scope gehörte - SPEC 2.3s "return übergibt Ownership an den
  aufrufenden Scope" war in der VM schlicht noch nicht umgesetzt. Jetzt macht
  das der `Return`-Opcode selbst: gehört der Rückgabewert (falls ein Objekt)
  dem gerade verlassenen Scope, wird er per `ObjectInstance.ReparentTo` an
  den Scope des Aufrufers übergeben, BEVOR dieser Scope aufgelöst wird.

- **`[]`-Operator-Überladung per Namenskonvention**: `ArrayGet`/`ArraySet`
  greifen bei einem rohen Array direkt zu, bei einer Klasseninstanz aber
  dynamisch über `GetIndex(i)`/`SetIndex(i, wert)` (per `CallMethodNested`,
  derselben verschachtelten Ausführung wie bei Destruktoren, nur mit
  Rückgabewert statt Verwerfen) - funktioniert auf jeder Klasse mit
  passender Methode, nicht nur auf `List`. Der Compiler emittiert dafür
  KEINEN Sonderfall: `arr[i]`/`obj[i]` kompilieren identisch zu
  `ArrayGet`/`ArraySet`, die Entscheidung "Array oder Methodenaufruf" fällt
  rein zur Laufzeit anhand des tatsächlichen Werts. Seit der Operator-
  Überladung (SPEC 5.11) ist `GetIndex`/`SetIndex` von Hand zu schreiben
  gleichbedeutend mit `operator[](...)`/`operator[](..., ...)` - Letzteres
  ist reiner Parser-Zucker, der exakt diese beiden Methodennamen erzeugt,
  hier also KEINE zusätzliche VM-Änderung nötig war.

- **Operator-Überladung, allgemein** (SPEC 5.11): dieselbe Idee wie bei
  `[]` oben, jetzt für ALLE arithmetischen/bitweisen/Vergleichsoperatoren
  verallgemeinert (`VM.BinaryNumericOrOperator`) - ist der LINKE Operand
  ein Objekt mit einer Methode namens `"operator+"`/`"operator=="`/etc.
  (ein Parameter: der rechte Operand), wird sie per `CallMethodNested`
  genestet aufgerufen, sonst greift die eingebaute Operation unverändert.
  Auch hier fällt die Entscheidung komplett zur Laufzeit, der Compiler
  emittiert für JEDEN `+`/`==`/etc.-Ausdruck weiterhin denselben einzelnen
  Opcode wie zuvor (`Add`/`Eq`/...), unabhängig davon, ob der Operand am
  Ende ein Objekt mit Überladung ist.

## 11. `is in`/`is of`/`is from`/`is under`

Alle vier nutzen bereits vorhandene Infrastruktur, keine neuen Laufzeit-
Konzepte:

- **`is in`** (`IsInUnit`): prüft `Unit.IsCompatibleWith` gegen eine zur
  Kompilierzeit im Units-Pool abgelegte Ziel-Einheit - dieselbe Kompatibilitäts-
  prüfung, die auch `CoerceUnit` nutzt.
- **`is of`** (`IsOfType`): bei Basistyp-Namen ein einfacher `Value.Kind`-
  Vergleich; bei Klassennamen dieselbe Basisklassen-Ketten-Suche, die schon
  für typisiertes `catch`-Matching gebaut wurde (`InstanceMatchesClassName`,
  umbenannt aus `ExceptionMatchesType`, jetzt für beide Zwecke genutzt).
  `"Exception"` matcht dabei bewusst pauschal jede Instanz (siehe Kommentar
  dort) - dieselbe Einschränkung wie beim `catch`-Matching, da die
  eingebaute Basisklasse keine eigene `RuntimeClass` hat.
- **`is from`/`is under`** (`IsFrom`, transitive als Flag): ruft direkt
  `ObjectInstance.IsOwnedBy`/`IsTransitivelyOwnedBy` auf, die schon seit der
  Ownership-Modell-Ausbaustufe existieren. Nur Objekt-zu-Objekt-Vergleiche
  sind möglich (`obj is from anderesObjekt`) - ein Vergleich gegen einen
  Scope direkt (z.B. "ist das Objekt vom globalen Scope besessen") ist aus
  Skript-Code heraus nicht ausdrückbar, da Scopes keine eigenen Werte sind.

## 12. `extern`-Linking (echte native Anbindung)

`CallNative`/`NativeRegistry` und `CallExtern`/`ExternRegistry` sind bewusst
die einzigen Stellen, über die der Bytecode mit der Außenwelt spricht - aber
mit unterschiedlicher Bindungszeit:

- **`NativeRegistry`** (Abschnitt 12 alt): Namen werden dem RESOLVER vorher
  bekannt gemacht (`Resolver.Resolve(program, natives.Names)`), der Index in
  der Registry wird schon beim KOMPILIEREN fest in den Bytecode gebacken
  (`CallNative nativeIdx`) - Registry muss also vor dem Kompilieren feststehen.
- **`ExternRegistry`** ist bewusst anders: `extern`-Deklarationen (SPEC 8.1)
  sind reiner Skript-Text, dem Resolver schon vorher bekannt
  (`ResolvedRef.Extern`, unabhängig von jeder Registry) - ein Skript
  kompiliert also, AUCH WENN die Implementierung noch gar nicht existiert. Der
  Compiler kennt beim `CallExtern`-Opcode nur den NAMEN (als String-Konstante,
  `u16 nameIdx`) - die eigentliche Verlinkung passiert ERST beim
  tatsächlichen `VM.Run()`, und schlägt erst dann (klar, per
  `InvalidOperationException`) fehl, falls für diesen Namen nichts verlinkt
  wurde. Klassisches "extern" - deklarieren und kompilieren geht immer,
  aufrufen nur mit Linkage.

**Zwei unabhängige Linking-Wege**, beide für denselben `CallExtern`-Opcode:
manuelle Host-Registrierung (`ExternRegistry`, wie gehabt) UND dynamisches
Laden gegen eine per `#extern "libName"` benannte native Bibliothek (SPEC
8.1.1) - `VM.CallExtern` prüft erst `ExternRegistry` (Host-Override gewinnt
immer), dann `_externSignatures` (siehe unten). Beide können nebeneinander
im selben Programm vorkommen, sogar für unterschiedliche `extern`-Namen.

**`#extern "libName"` - wo die Information herkommt und hinfließt:**
`#extern` ist ein eigenes Lexer-Token (`Hash` + das schon existierende
`Extern`-Keyword), das der PARSER selbst konsumiert (`ParseDirective`) -
erzeugt keinen eigenen AST-Knoten, sondern setzt nur ein Parser-internes
Feld (`_currentExternLib`), mit dem jede nachfolgend geparste `ExternDecl`
gestempelt wird (`ExternDecl.LibName`). Der Resolver sammelt alle
`ExternDecl`s ohnehin schon (`CollectExterns`) und legt sie jetzt zusätzlich
offen (`ResolveResult.Externs`) - der Compiler baut daraus pro Programm eine
`Dictionary<string, ExternSignature>` (Name -> LibName + Parameter-/
Rückgabetypen, `CompiledProgram.ExternSignatures`), die der VM als
zusätzlicher Konstruktor-Parameter übergeben wird.

**`VM.ResolveDynamicExtern`** (gecached pro Name, plus ein separater
Bibliotheks-Cache, da `NativeLibrary.Load` nicht ganz billig ist): lädt die
Bibliothek per `System.Runtime.InteropServices.NativeLibrary.Load`, sucht
den Export unter GENAU dem `extern`-Namen per `NativeLibrary.GetExport`,
baut per `System.Reflection.Emit` einen ECHTEN, NICHT-generischen
Delegate-Typ passend zur Skript-Signatur (dieselbe Typ-Zuordnung wie beim
Pointer-Marshalling: bool/int/float/char/string direkt, jeder Pointer-Typ
als `IntPtr`) und macht daraus per `Marshal.GetDelegateForFunctionPointer`
einen echten aufrufbaren Delegate. Der eigentliche Aufruf läuft über
`Delegate.DynamicInvoke` mit denselben bereits marshallten Argumenten
(`object?[]`), die auch der manuelle `ExternRegistry`-Pfad bekommt - **die
komplette Calling-Convention-/Argument-Marshalling-Mechanik übernimmt dabei
vollständig die eingebaute .NET-Interop-Schicht**, hier wird nur zur
Laufzeit die PASSENDE Delegate-Form zusammengebaut (das geht zur Compile-
Zeit nicht, die Signatur steht ja erst durch die geparste `extern`-
Deklaration fest).

**Wichtige, erst zur Laufzeit entdeckte Falle**: `Marshal.
GetDelegateForFunctionPointer` lehnt JEDEN generischen Delegate-Typ ab -
auch einen bereits vollständig GESCHLOSSENEN wie `Func<long>` (die
Fehlermeldung "The specified Type must not be a generic type" prüft
offenbar `Type.IsGenericType`, nicht `ContainsGenericParameters`). Die
BCL-Delegates `Action<...>`/`Func<...>` sind für diesen Zweck deshalb NICHT
nutzbar, obwohl geschlossen-generische Typen sonst überall sonst wie normale
Typen behandelt werden. `BuildNonGenericDelegateType` erzeugt deshalb per
`TypeBuilder` (von `MulticastDelegate` abgeleitet, mit Konstruktor + einer
virtuellen `Invoke`-Methode der gewünschten Signatur, beide als
"runtime-implementiert" markiert) einen ECHTEN Delegate-Typ zur Laufzeit -
dasselbe Muster, das der C#-Compiler für ein `delegate`-Schlüsselwort selbst
verwendet, nur eben zur Laufzeit statt zur Compile-Zeit. Ein statisches
`ModuleBuilder`-Feld (eine dynamische Assembly mit `AssemblyBuilderAccess.
Run`, nie gespeichert) plus ein global eindeutiger Namens-Zähler
(`Interlocked.Increment`) sorgen dafür, dass jeder erzeugte Typ einen
eindeutigen Namen bekommt.

**`#include "fileName"`** (SPEC 8.1.2) ist bewusst KEIN Lexer-/Parser-
Konstrukt, sondern eine reine Textvorverarbeitung VOR dem Lexer
(`Parsing.Preprocessor.Process`, aufgerufen von `Parser.Parse(source,
basePath)`) - anders als `#extern`, das eine echte semantische Bedeutung für
nachfolgende Deklarationen hat, ist `#include` rein syntaktisches Spleißen
(rekursiv, mit Zyklen-Erkennung und "Include once"-Semantik wie `#pragma
once` statt C's rohem Mehrfach-Einfügen). Zeilennummern in Fehlermeldungen
werden für eingefügten Text dadurch ungenau - eine bekannte, akzeptierte
Grenze dieser einfachen Umsetzung (wie bei jedem Text-Präprozessor ohne
eigene `#line`-Direktiven).

**Pointer-Marshalling zu echten nativen Adressen** (das in der
`PointerTarget`-Doku angekündigte fehlende Stück): ein Skript-Pointer zeigt
auf einen verwalteten Ort (Scope-Slot/Feld, siehe Abschnitt 7) - für einen
`extern`-Aufruf braucht die native Seite aber eine ECHTE Adresse. `VM.
MarshalArgsOut` alloziert dafür pro Pointer-Argument 8 Byte unmanaged Speicher
(`Marshal.AllocHGlobal`), schreibt den aktuellen Wert hinein (Copy-In), gibt
der nativen Funktion die rohe `IntPtr`, und liest nach dem Aufruf den
(eventuell von der nativen Seite veränderten) Wert wieder zurück ins
PointerTarget (Copy-Out) - über einen pro Argument gesammelten Cleanup-
Delegate, der auch bei einer C#-Exception aus der nativen Funktion noch läuft
(`finally`), damit kein natives Speicherleck entsteht. Werttypen
(bool/int/float/char/string) werden direkt in ihr natives CLR-Gegenstück
kopiert, keine eigene Adresse nötig. Gilt für BEIDE Linking-Wege gleichermaßen
(die Marshalling-Schicht liegt VOR der Fallunterscheidung ExternRegistry vs.
dynamisches Laden).

**Demo-Verlinkung** (manueller Weg): `ExternRegistry.CreateWinApiDemo()`
verlinkt drei echte WinAPI-Funktionen per P/Invoke (`user32.MessageBoxW`,
`kernel32.GetTickCount`, `kernel32.QueryPerformanceCounter`) - Letztere
zeigt dabei auch die Pointer-Seite (schreibt einen 64-Bit-Zähler über die
übergebene Adresse, passt exakt zur generischen 8-Byte-Slot-Größe). Läuft
naturgemäß nur unter Windows (die DLLs existieren nur dort) - unter
Linux/macOS baut man sich stattdessen eine eigene Registry gegen die
dortigen Plattform-APIs (z.B. libc/libm per `DllImport("libc")`/
`DllImport("libm")`), das Muster (Copy-In → nativer Aufruf → Copy-Out)
bleibt exakt dasselbe; für den DYNAMISCHEN Weg (`#extern "libName"`) gilt
dasselbe Plattform-Argument - `"kernel32.dll"`/`"user32.dll"` sind nur unter
Windows ladbar, unter Linux `#extern "libc.so.6"` o.ä. Bewusst NICHT
verlinkt: Funktionen mit variabler Puffer-/String-Länge hinter einem Pointer
(z.B. `strlen`) - die generische 8-Byte-Kopie ist nur für einzelne
Primitivwerte hinter einem Pointer korrekt, nicht für Puffer/Arrays
beliebiger Länge (ein Lesen/Schreiben über die 8 Byte hinaus wäre
undefiniertes Verhalten). Das ist eine bewusste Grenze dieser Ausbaustufe,
kein Bug.

## 13. `readonly` und `enum`

Beide sind bewusst REIN COMPILE-/RESOLVE-ZEIT-Konzepte, kein eigener
Bytecode nötig:

- **`readonly`**: `VarDeclStmt`/`FieldDecl` tragen ein `IsReadonly`-Flag. Für
  Variablen trackt der Resolver das pro Scope-Slot (`ResolverScope.
  ReadonlySlots`) und prüft es in `ResolveAssignTarget` - jede Zuweisung an
  einen als readonly markierten Slot ist ein `ResolverException`. Für Felder
  gilt der Check nur für den statisch erkennbaren Fall `this.feld = ...`
  (Ziel lexikalisch `this`, aktuelle Klasse bekannt): erlaubt nur, während
  `_inConstructor` gesetzt ist - ein EXPLIZITER `bool isConstructor`-
  Parameter an `ResolveFunctionLike` (nicht aus `baseArgs != null`
  abgeleitet: das heißt nur "hat ein explizites `base(...)`", eine Klasse
  OHNE Basisklasse hat nie `baseArgs`, ihr Konstruktor braucht das Flag aber
  trotzdem - ein Bug in einer früheren Fassung dieser Ausbaustufe) - und
  bewusst zurückgesetzt beim Betreten eines Lambda-Bodies (der läuft
  typischerweise erst NACH Abschluss der Konstruktion, `this.readonlyFeld=x`
  darin wäre nicht sicher). Ein dynamisches Ziel (`obj.feld = ...`) bleibt
  ungeprüft (siehe SPEC 8.6) - reine Namens-/Kind-basierte Prüfung, kein
  Laufzeit-Opcode dafür.
- **`enum`**: `EnumDecl`/`EnumMember` im AST, vorab eingesammelt
  (`Resolver.CollectEnums`, wie Klassen/externs) - dabei werden die
  tatsächlichen Int-Werte direkt berechnet (Auto-Increment oder Int-Literal-
  Validierung). Ein Zugriff `Name.Mitglied` ist syntaktisch ein ganz
  normaler `MemberExpr` - der Resolver erkennt ihn NUR daran, dass der
  Zielname (als bloßer `IdentifierExpr`) ein bekannter enum-Name ist, und
  hängt dann `ResolvedRef.EnumMember(wert)` an genau diesen `MemberExpr`-
  Knoten (nicht an einen `IdentifierExpr`, wie die anderen `ResolvedRef`-
  Fälle - eine bewusste Erweiterung des Mechanismus). Der Compiler prüft das
  beim Kompilieren eines `MemberExpr` ZUERST und emittiert bei einem Treffer
  direkt `LoadConst` mit dem Int-Wert - keine `GetField`/Laufzeit-Auflösung,
  kein eigener Opcode. Ein enum-Name "gewinnt" dabei immer gegen eine
  gleichnamige Variable im Scope (wie ein Klassenname auch nicht durch eine
  Variable verschattet werden kann).

## 14. Properties

Reine Namenskonvention, wie schon `GetIndex`/`SetIndex` (Abschnitt 10) - kein
eigener Opcode, kein eigener Wert-Typ: `PropertyDecl` (Getter/Setter je
optional, mindestens einer nötig) wird beim Kompilieren der Klasse (`Compiler.
CompileClasses`) zu zwei GANZ NORMALEN Einträgen in `RuntimeClass.Methods`
(`get_Name`/`set_Name`) - der Setter bekommt dabei einen ganz normalen,
zusätzlichen Parameter namens `value` (Resolver/Compiler behandeln ihn exakt
wie jeden anderen Methodenparameter, keine Sonderbehandlung nötig, da `value`
nur eine KONTEXTABHÄNGIGE Bedeutung beim PARSEN hat, nicht beim Resolven/
Kompilieren).

`VM.GetField`/`SetField` (die schon immer für `obj.feld`-Zugriff zuständigen
Opcodes - Properties brauchen also KEINEN eigenen Opcode) prüfen zuerst ein
ECHTES Feld (`ObjectInstance.Fields`, schneller Dictionary-Lookup, der
Normalfall) und fallen NUR wenn keines existiert auf `get_`/`set_`+Name
zurück (`CallMethodNested`, dieselbe Technik wie bei `GetIndex`/`SetIndex`
und robust gegen eine Exception, die den Aufruf per Continuation-Sprung
verlässt - siehe Abschnitt 10). Properties haben deshalb NIE einen eigenen
`Fields`-Eintrag ihres eigenen Namens; ein gleichnamiges Feld würde die
Property komplett überschatten (Feld gewinnt immer).

**Wichtige, beim Testen entdeckte Falle**: `SetField`s ursprüngliches
Verhalten für einen unbekannten Feldnamen war "einfach ein neues Feld
anlegen" (dynamische Sprache, keine Vorab-Deklarationspflicht). Das hätte
für eine reine Lese-Property (`get` ohne `set`) bedeutet: eine Zuweisung
würde STILLSCHWEIGEND ein gleichnamiges Feld erzeugen, das die Property ab
diesem Zeitpunkt dauerhaft überschattet - auch für künftige Lesezugriffe,
da `GetField` Felder vor Properties prüft! Deshalb prüft `SetField` VOR dem
"neues Feld anlegen"-Fallback explizit, ob wenigstens ein Getter (`get_`+
Name) existiert, und wirft dann einen klaren Fehler statt das Feld
anzulegen.

**Auto-Properties** (`get;`/`set;` ohne Body): reiner PARSER-Zucker, wie
`with`/`switch` - Resolver/Compiler/VM sehen davon nichts, nur ganz normale
`FieldDecl`+`PropertyDecl`-Knoten. `Parser.ParsePropertyBody` erkennt einen
Accessor ohne `{...}` (stattdessen direkt ein Statement-Terminator) als
"auto" und synthetisiert dafür: ein `FieldDecl` mit Namen `_AutoName`
(`_Auto`-Präfix, bewusst nicht nur ein Unterstrich - reduziert die Gefahr
einer stillen Kollision mit einem "normalen" `_name`-Feld) OHNE
Initializer, sowie triviale Getter-/Setter-`BlockStmt`s (`return
this._AutoName` bzw. `this._AutoName = value`), die exakt dieselbe
AST-Form haben wie ein von Hand geschriebener Getter/Setter - landen
dadurch beim Kompilieren automatisch in `RuntimeClass.Fields` bzw.
`RuntimeClass.Methods` (`get_Name`/`set_Name`), ganz ohne Sonderbehandlung
in `Compiler.CompileClassBody`. Da `ParseClassMember` davor nur GENAU EIN
`Stmt` pro Aufruf zurückgeben konnte, wurde die Signatur auf `List<Stmt>`
umgestellt (beide Aufrufstellen - normale Klasse UND `class extends`,
siehe Abschnitt 16 - nutzen jetzt `AddRange` statt `Add`) - eine
Auto-Property liefert zwei Members (Feld + Property) aus einem
Parser-Aufruf. Das Backing-Field ist ein STINKNORMALES Feld, keine eigene
Sichtbarkeitsstufe (diese Sprache kennt keine Zugriffsmodifikatoren) - der
`_Auto`-Präfix ist reine Namenskonvention, keine erzwungene Grenze: Code
innerhalb der Klasse greift bewusst direkt darauf zu (`this._AutoName`),
z.B. um eine get-only Auto-Property im Konstruktor zu initialisieren.
Kollisions-Risiko (ein selbst deklariertes Feld `_AutoName`) wird NICHT
geprüft - siehe SPEC 8.8.

**`lambda`-Typen mit Signatur**: `Resolver.PrimitiveTypeNames` hat
`"lambda"` (klein geschrieben - siehe dortige Doku für die Begründung,
warum nicht `func`, das mit der Lambda-AUSDRUCKS-Syntax kollidieren würde).
Anders als jede andere Typ-Annotation (SPEC 8.1: rein syntaktisch, nicht
zur Laufzeit erzwungen) wird bei `lambda` tatsächlich etwas geprüft - die
PARAMETERANZAHL, nicht die einzelnen Typnamen (eine Lambda legt ihre
Parametertypen zur Laufzeit nicht verlässlich offen) und nicht der
Rückgabetyp (nur durch tatsächliches Ausführen prüfbar, das wäre ein
komplett anderer Mechanismus).

Syntax: `[RückgabeTyp] lambda[&lt;P1,...,Pn&gt;]` - `Ast.TypeRef` trägt dafür
ein neues optionales Feld `LambdaSignature` (`Ast.LambdaSignature`:
`ReturnTypeName` + `ParamTypeNames`, beides reine NAMEN, keine rekursiven
`TypeRef`s - ein Parameter- oder Rückgabetyp, der selbst wieder ein
`lambda&lt;...&gt;` mit eigener Signatur wäre, wird bewusst nicht unterstützt).
`Parser.ParseTypeRef` erkennt das Muster: der zuerst gelesene Name ist in
Wahrheit der RÜCKGABETYP, wenn direkt danach das (nicht reservierte) Wort
`lambda` folgt (`baseName != "lambda" && Peek().Lexeme == "lambda"`) -
`ParseLambdaSignature` liest dann optional `&lt;...&gt;` (leer = 0 Parameter,
das deckt auch das bare `lambda` ohne jede Klammer ab - siehe SPEC 4.3).
`Parser.NextLooksLikeTypeThenName()` erkennt zusätzlich ein alleinstehendes
`lambda`-Token als Typ-Start (nicht nur "Identifier gefolgt von
Identifier", da `lambda<int> x` mit einem `<` statt eines zweiten
Bezeichners weitergeht).

Durchsetzung: neuer Opcode `CheckLambdaSignature` (u8 erwartete
Parameterzahl) - prüft `Peek()` (nicht `Pop()`, der Wert bleibt für die
normale Weiterverwendung erhalten) ist ein Lambda-Wert mit exakt dieser
`Proto.ParamCount`, wirft sonst eine klare `InvalidOperationException`.
Emittiert wird das an zwei Stellen: `Compiler.CompileStmt` (VarDeclStmt
mit Initializer, über `EmitCheckLambdaSignatureIfNeeded`) und - der
aufwendigere Fall - GANZ AM ANFANG jedes Funktionskörpers für jeden so
typisierten Parameter (`Compiler.EmitLambdaParamChecks`, aufgerufen aus
`CompileMethodProto`/`CompileConstructorProto`/`CompileLambda`): da
Parameter-Slots vom AUFRUFER befüllt werden (siehe VM.CallMethod/
NewObject/Call), nicht durch eigenen Bytecode der Callee, liest die
Prüfung den Wert per `LoadLocal` zurück, prüft ihn, und verwirft die
gepeekte Kopie wieder (`Pop`) - der eigentliche Parameter-Slot bleibt
unangetastet. Ein Property-Setter (synthetischer `value`-Parameter mit dem
Property-Typ) profitiert davon automatisch mit, ganz ohne eigene
Verdrahtung, da er über denselben `CompileMethodProto`-Pfad läuft.
Auch Feld-Initialisierer werden geprüft (`CompileFieldInitProto` bekam
dafür den deklarierten Typ als zusätzlichen Parameter).

## 15. Debug-Erweiterungen (für `src/ScriptLang.Editor`)

Zwei rein additive Ergänzungen, ausschließlich für externe Werkzeuge (den
Editor) - die normale Ausführung (`VM.Run()`) ist davon unberührt:

- **`Chunk`-Zeilentabelle**: `Compiler.CompileStmt` ruft zu Beginn jedes
  Statements `Chunk.MarkLine(stmt.Line)` auf - legt nur dann einen neuen
  Eintrag an, wenn sich die Zeile gegenüber der zuletzt markierten
  tatsächlich geändert hat (Run-Length-artig, keine Redundanz pro
  Instruktion). `Chunk.GetLine(ip)` sucht den letzten Eintrag mit
  Offset ≤ `ip`.
- **`Chunk.DebugLocalNames`**: `(Depth, Slot) -> Name`, aber NUR für
  Parameter (Methoden/Konstruktoren/Lambdas tragen ihre Parameter direkt
  beim Kompilieren ein, immer an Depth 0 - der Depth ihrer eigenen
  Top-Level-Scope). Verschachtelte `var`-Deklarationen werden NICHT erfasst
  (dafür bräuchte es einen zum Resolver parallelen Slot-Zähler im Compiler,
  der bei jedem Scope-Wechsel mitgeführt wird - für diese Ausbaustufe
  bewusst nicht gebaut).
- **`VM`-Stepping-API**: `StepInstruction()` (eine einzelne Instruktion,
  Gegenstück zu `Run()`s Schleifenkörper - stoppt zusätzlich sauber, sobald
  `UnhandledException` gesetzt wird, siehe dort, statt einfach weiterzulaufen
  als wäre nichts passiert), `StepLine()` (Step Over - bis die
  Zeile wechselt, nur auf gleicher oder flacherer Aufruf-Tiefe),
  `StepInto()` (wie StepLine, stoppt aber auch beim Wechsel auf eine TIEFERE
  Aufruf-Tiefe - landet damit auf der ersten Zeile eines betretenen
  Aufrufs), `StepOut()` (läuft bis die aktuelle Funktion verlassen wurde),
  `Continue(breakpointLines)` (bis eine Zeile aus der Menge erreicht wird,
  nur beim tatsächlichen Zeilenwechsel geprüft), `CurrentLine`, `IsHalted`,
  `DebugStackSnapshot`, `DebugCallDepth`, `DebugThisDescription` (aktuell
  gebundenes `this`, falls vorhanden, als Textbeschreibung),
  `DebugThisValue` (dasselbe als echter `Value` statt nur Text - Grundlage
  für die aufklappbare Feldanzeige im Editor, siehe unten),
  `DebugScopeChain()` (die Scope-Kette
  der aktuellen Funktion als Liste einzelner Ebenen - Depth 0 ist der GERADE
  AKTIVE innerste Block, absteigend bis zur Funktions-Top-Level-Scope -
  jede Ebene separat statt einer einzigen flachen Liste, damit der Editor
  sichtbar machen kann, welche Variablen zum aktiven Block gehören und
  welche aus einer umschließenden Ebene "durchgereicht" werden),
  `DebugGlobals()` (globale Variablen - ohne Namens-Register, siehe
  `DebugLocalNames`-Kommentar, erscheinen als `(global N)`),
  `UnhandledException` (siehe Abschnitt 19: gesetzt statt geworfen, wenn ein
  `throw` in DIESER VM-Instanz keinen passenden `catch` findet - Editor/
  Host-Code prüft dieses Feld NACH einem Run()/Step*()-Aufruf, statt einen
  `try`/`catch` darum zu legen).

### Editor-UI (WPF, `src/ScriptLang.Editor/MainWindow.xaml(.cs)`)

- **Hotkeys** laufen über einen explizit registrierten `PreviewKeyDown`-
  Handler am Fenster (`Window_PreviewKeyDown`, per `AddHandler(...,
  handledEventsToo: true)` im Konstruktor - bewusst kein `OnKeyDown`/
  `OnPreviewKeyDown`-Override), aus zwei Gründen: WPF behandelt F10 bei
  vorhandenem `Menu`-Element speziell (aktiviert die Tastatur-Navigation
  des Menüs), Preview (Tunneling) läuft VOR dieser eingebauten Behandlung.
  WICHTIGER, der eigentliche ursprüngliche Bug: F10 ist (wie Alt) unter
  Windows eine SYSTEM-Taste (WM_SYSKEYDOWN) - WPF liefert dafür
  `e.Key == Key.System`, die tatsächliche Taste steht in `e.SystemKey`,
  NICHT in `e.Key` - ein Vergleich gegen `e.Key == Key.F10` matcht deshalb
  NIE, unabhängig von der Event-Phase. Der Handler löst das über
  `Key key = e.Key == Key.System ? e.SystemKey : e.Key;` VOR dem eigentlichen
  `switch`. Die `InputGestureText`-Angaben an den `MenuItem`s (z.B. "F5")
  sind rein kosmetisch (zeigen nur Text im Menü) und erzeugen KEINE echte
  Tastatur-Bindung - die eigentliche Zuordnung Taste -> Aktion lebt
  ausschließlich in diesem einen `switch`, muss also bei einer neuen Aktion
  an BEIDEN Stellen gepflegt werden (Menü-Text UND switch-Fall).
  Belegung: F5 (Starten), Strg+F5 (Bis Ende durchlaufen), Umschalt+F5
  (Stopp), F10 (Step Over), F11 (Step Into), Umschalt+F11 (Step Out), F8
  (Weiter bis Haltepunkt), F9 (Haltepunkt umschalten), Strg+N/O/S (Neu/
  Öffnen/Speichern).
- **Aufklappbare Feldanzeige** im Scope-Baum (`RefreshDebugPanels`/
  `BuildVariableTreeItem`): Objekte (Klassenname + Erzeugungs-ID,
  `obj.Fields` aufklappbar) und Arrays (`Array[N]`, Elemente `[0]`, `[1]`,
  ... aufklappbar) lassen sich verschachtelt öffnen, inkl. `this` (über
  `VM.DebugThisValue`). LAZY per `TreeViewItem.Expanded` (baut Kind-Knoten
  erst beim tatsächlichen Aufklappen, nicht den ganzen Graphen sofort) mit
  Zyklenschutz über die Menge der bereits auf dem aktuellen Pfad besuchten
  Objekte/Arrays (Referenzidentität) - eine mehrfache Referenz auf
  dasselbe Objekt an VERSCHIEDENEN Stellen ist dagegen kein Zyklus und
  bleibt aufklappbar. Byte-Puffer bleiben bewusst eine einzeilige Anzeige
  (`<buffer N bytes>`), keine Byte-für-Byte-Aufklappung.

## 16. Methodenüberladung, `with`, Extension-Klassen, Konstruktor-Überladung, optionale Parameter, `switch`, Generics

**Methodenüberladung**: `RuntimeClass.Methods` ist jetzt `Dictionary<string,
List<FunctionProto>>` statt `Dictionary<string, FunctionProto>` - eine Liste
pro Name, EINE Überladung pro Parameteranzahl. `FindMethod(name, argCount)`
läuft über die Basisklassen-Kette und sucht in JEDER Ebene nach einer
Überladung mit passender Arity (bricht NICHT ab, sobald eine Klasse den
Namen überhaupt kennt, aber ohne passende Arity - eine abgeleitete Klasse
kann so eine Überladung ergänzen, ohne die geerbten zu verdecken). Alle
Aufrufstellen (`CallMethod`, `CallBaseMethod`, `CallMethodNested`, die
`get_`/`set_`-Property-Checks mit ihrer jeweils festen Arity) übergeben
jetzt die tatsächliche Argumentzahl. Der Resolver verbietet zwei Methoden
gleichen Namens UND gleicher Parameteranzahl in derselben Klasse
(`ResolveClass`, `seenMethodSignatures`). Bewusst NICHT unterstützt:
Konstruktor-Überladung (bräuchte Änderungen an `NewObject`/
`BeginConstruction`/der `base(...)`-Auflösung, die über den reinen
Namenskonventions-Ansatz der Methoden hinausgehen - nicht Teil dieser
Ausbaustufe).

**`with`**: reines Parser-Zucker, komplett vor dem Resolven/Kompilieren
aufgelöst - Resolver/Compiler/VM sehen davon nichts. `Parser.ParseWithStmt`
erzeugt einen synthetischen `VarDeclStmt` (`__withN__ = ausdruck`) als
erstes Statement eines neuen `Stmt.BlockStmt` (eigener, isolierter Scope)
und merkt sich den Namen auf `_withVarStack` (Stack für Verschachtelung).
`ParsePrimary` bekommt einen neuen `TokenType.Dot`-Fall: ein `.` an
Ausdrucksanfang (nur gültig mit nicht-leerem `_withVarStack`) liefert einen
`IdentifierExpr` für die innerste with-Variable, OHNE den `.` selbst zu
konsumieren - die direkt anschließende `ParsePostfix`-Schleife (die '.'
schon für normale Mitgliederzugriffe versteht) hängt den Rest ganz normal
an. `.feld = x` funktioniert damit automatisch auch als Zuweisungsziel
(`ParseAssignment` prüft nur auf `MemberExpr`, unabhängig davon, wie dessen
Ziel-Ausdruck zustande kam). Ownership-seitig unbedenklich: `var __with0__
= p` erzeugt nur einen ALIAS (Value.Kind=Class hält eine Referenz), keine
Ownership-Änderung - das Verlassen des with-Blocks gibt nur die temporäre
Variable frei, nicht das Objekt selbst.

**Extension-Klassen** (`class extends Name { ... }`): ebenfalls komplett
VOR dem Resolven/Kompilieren aufgelöst, diesmal aber nicht im Parser für
EIN Statement, sondern als eigener Merge-Pass über das GANZE Programm
(`Parser.MergeClassExtensions`) - die neuen Mitglieder müssen ja in eine
ANDERE, an beliebiger Stelle im Programm stehende `ClassDecl` wandern.
Läuft NACH dem reinen Parsen (`ParseRaw`), aber VOR allem anderen: `Parser.
Parse` ruft es direkt auf, `Parser.ParseWithPrelude` **kombiniert Prelude
und Nutzer-Code zuerst und merged erst DANACH** (beide Hälften einzeln mit
`ParseRaw` geparst, nicht mit dem mergenden `Parse`) - sonst würde z.B.
`class extends List` im Nutzer-Code fälschlich fehlschlagen, weil `List`
beim isolierten Mergen nur der Nutzer-Hälfte noch unbekannt wäre. Eine
`ClassDecl` wird per `record`-`with`-Ausdruck (`cd with { Members = ... }`)
nicht-destruktiv um die gesammelten Erweiterungs-Mitglieder ergänzt - der
Rest der Pipeline sieht danach nur noch eine ganz normale, bereits
vollständige `ClassDecl`, keine Sonderbehandlung nötig. Referenziert eine
Erweiterung eine im (kombinierten) Programm unbekannte Klasse, wirft der
Merge-Pass eine `ParseException`. Resolver und Compiler haben je einen
defensiven `case ClassExtensionDecl:`-Fall, der einen internen Fehler
wirft, falls doch einmal ein unaufgelöster Knoten durchrutschen sollte
(z.B. weil ein Programm nicht über `Parser.Parse`/`ParseWithPrelude`,
sondern direkt über `Parser.ParseProgram()` gebaut wurde).

**Konstruktor-Überladung**: `RuntimeClass.Constructors` ist jetzt
`Dictionary<int, FunctionProto>` (Arity -> Proto) statt eines einzelnen
`Constructor`-Felds - bewusst OHNE Basisklassen-Kette (anders als
`FindMethod`): `new Derived(...)` nutzt immer nur Deriveds eigene
Konstruktoren. Alle Aufrufstellen (`NewObject`, `NewObjectOwned`,
`ConstructBase`, `ConstructNested`) nutzen jetzt `FindConstructor(argCount)`
mit einer hilfreichen Fehlermeldung bei fehlender Überladung
(`DescribeConstructorNotFound`, analog zu `DescribeMethodNotFound`).

**Optionale Parameter**: `LambdaParam.DefaultValue` (Ast), `FunctionProto.
ParamDefaults` (Bytecode - ein 0-Arg-Proto pro optionalem Parameter,
parallel zu den Parametern, kompiliert von `Compiler.CompileParamDefaults`
im selben Klassen-Kontext wie die Methode/der Konstruktor selbst, damit
`this` im Standardwert funktioniert). Der Resolver löst Standardwert-
Ausdrücke isoliert auf (wie Feld-Initialisierer: eigener Scope mit Parent
`_globalScope`, sieht also NICHT die anderen Parameter derselben Funktion)
und prüft, dass optionale Parameter am Ende zusammenhängen.
`RuntimeClass.FindBestMatch` (jetzt gemeinsame Grundlage für `FindMethod`
UND `FindConstructor`) akzeptiert eine Überladung mit MEHR Parametern als
die Aufruf-Argumentzahl, wenn alle darüber hinausgehenden (immer
TRAILING) Parameter Standardwerte haben - unter mehreren so passenden
Überladungen gewinnt die mit den WENIGSTEN Parametern (die "engste"
passende). `VM.CheckArity` ist entsprechend erweitert (akzeptiert
`argCount < ParamCount`, wenn `RuntimeClass.AllTrailingHaveDefaults` das
bestätigt), `VM.FillDefaultArgs` baut das VOLLSTÄNDIGE Argument-Array auf,
indem es für jeden fehlenden Parameter dessen Standardwert-Proto
verschachtelt auswertet (`EvaluateDefaultNested`, dieselbe robuste
Continuation-Sprung-Erkennung wie `CallMethodNested`/`ConstructNested`).
Betrifft ALLE Aufruf-Wege gleichermaßen: Methoden, Konstruktoren UND
Lambdas (auch `func (...) => {...}` unterstützt optionale Parameter).

**`switch`**: wie `with` reiner Parser-Zucker (siehe `Parser.
ParseSwitchStmt`), desugarn zu einer If/Else-if-Kette aus ganz normalen
`IfStmt`/`BinaryExpr`-Knoten - keine eigene Laufzeit-Unterstützung, kein
neuer Opcode. Der switch-Ausdruck landet in einer synthetischen Variable
(`var __switchN__ = ausdruck`), jede `case`-Bedingung wird zu
`__switchN__ OP wert` (OP fehlt -> `BinaryOp.Eq`), `case default` wird zum
abschließenden `else`. Die Kette wird von HINTEN nach VORNE aufgebaut
(letzter `case`/`default` zuerst), damit jeder vorherige Zweig den
nächsten als sein `Else` bekommt. `break` existiert NUR als
Parser-Konzept: `Parser.ParseSwitchCaseBody` parst Statements bis zum
nächsten `case`/`}`/einem `break` auf EIGENER Ebene und KONSUMIERT ein
gefundenes `break` einfach als Zweig-Ende, ohne es in irgendeinen AST-Knoten
zu übernehmen - es gibt keinen `BreakStmt`. Ein `break` an JEDER anderen
Stelle im Programm (`ParseStatement`) wirft sofort eine klare
`ParseException`, da diese Sprache (noch) kein allgemeines
Schleifen-`break` kennt und ein `break` dort sonst nur verwirrend als
unbekannter Ausdrucksanfang fehlschlagen würde.

**Generische Klassen** (`class Name<T> where T ...`): Da die Sprache
dynamisch typisiert ist, gibt es KEINE echte Typ-Substitution/
-Spezialisierung wie in C# - Generics sind hier primär Syntax +
Constraint-PRÜFUNG. `Ast.TypeParam`/`TypeConstraint`/`TypeConstraintGroup`
(neu in Ast/Stmt.cs) modellieren `<T1,T2>` samt `where`-Klauseln (','
zwischen Gruppen = ODER, ':' innerhalb einer Gruppe = UND). `ClassDecl`
und `MethodDecl` tragen jetzt optional `TypeParams`; `NewExpr` trägt
optional `TypeArgs` (die reinen Namen aus `new Name<Arg1,...>(...)`).
Geparst in `Parser.ParseOptionalTypeParamNames`/`ParseWhereClauses`/
`ParseOneTypeConstraint` - `<...>` direkt nach `new Name` ist unzweideutig
(danach MUSS zwingend eine Argumentliste folgen, nie ein Vergleich), bei
einer Methode direkt nach dem Namen ebenso (ein Feld könnte an der Stelle
nie sinnvoll ein '<' haben). Da Typ-ARGUMENTE reine NAMEN sind (keine
Laufzeit-Werte), passiert die GESAMTE Constraint-Prüfung statisch im
Resolver (`Resolver.CheckTypeArgs`/`SatisfiesConstraint`/
`TypeNameSatisfiesIsOf`), ausgelöst beim Resolven eines `NewExpr` mit
`TypeArgs` - keine VM-/Compiler-Änderung nötig, `TypeArgs` erreicht die
Bytecode-Ebene gar nicht erst (nach der Prüfung "vergessen"). `is of`
zwischen zwei NAMEN prüft rekursiv die Basisklassen-/Interface-Kette
(`ClassDecl.BaseNames`, analog zu `ClassHasMethod`); `is in` interpretiert
das Typ-Argument selbst als Einheitenname und prüft dimensionale
Kompatibilität über `Unit.Parse(...).IsCompatibleWith(...)` (wirft nie -
ein unbekanntes Symbol wird zu einer atomaren, nur zu sich selbst
kompatiblen Einheit). `T` als Typname INNERHALB der generischen Klasse
wird über `Resolver._currentTypeParamNames` (Referenzzähler statt reinem
HashSet, damit ein gleichnamiger Methoden-Typ-Parameter beim Verlassen der
Methode nicht versehentlich auch den äußeren Klassen-Typ-Parameter
entfernt) als bekannter Typname akzeptiert (`ValidateTypeName`). Generische
METHODEN werden geparst und ihre Typ-Parameter/`where`-Klauseln
gespeichert, aber NICHT mit Typ-Argumenten am Aufrufort geprüft - siehe
SPEC 5.8 für die Begründung (Mehrdeutigkeit mit Vergleichsoperatoren).

**Generische Klasse mit demselben Namen wie eine nicht-generische**: Alle
Klassentabellen (Resolver `_classes`, Compiler/VM `Classes`) sind nach dem
Namen geschlüsselt - deshalb bekommt in einem Namenskonflikt die GENERISCHE
Klasse den Schlüssel `Name`N` (N = Anzahl der Typ-Parameter, siehe
`Ast.GenericClassNames`), die nicht-generische behält ihren Namen. Das
passiert in `Parser.DisambiguateGenericClasses`, nach dem Parsen ALLER
Quellen und VOR `MergeClassExtensions` (eine `class extends Name` trifft
dadurch die nicht-generische Klasse) - ohne Konflikt bleibt alles
unverändert. Aufgelöst wird über `GenericClassNames.ResolveNewTarget`
(Anzahl der Typ-Argumente von `new Name<...>`), im Resolver UND im Compiler
(`ResolveNewClassName`) identisch; alles ohne Typ-Argumente greift wie
bisher über den Klassennamen und trifft deshalb die nicht-generische Klasse.
Das Backing-Field einer statischen Auto-Property in einer generischen
Klasse adressiert der Parser über `Ast.SelfClassExpr` (statt über den
Klassennamen, der bei einem Konflikt auf die falsche Klasse zeigen würde) -
der Resolver löst das auf `_currentClass` auf.

**Fehler sammeln statt sofort abbrechen** (Resolver und Compiler): ein
Fehler verwirft das Ergebnis, ändert aber nichts daran, dass der Nutzer alle
weiteren Fehler sehen will. `Resolver.Resolve` und `Compiler.Compile`
werfen deshalb erst NACH dem kompletten Durchlauf - EINE
`ResolverException`/`CompilerException` (die zweite ist eine
`NotSupportedException`, bestehende `catch`-Blöcke greifen weiter), deren
`Message`/`Line` der erste gesammelte Fehler sind und deren `Errors` ALLE
enthält (`CompileErrors.Messages/Describe` liefert sie als Text). Wiederaufsetzpunkte
sind jedes Statement (`ResolveStmt`/`CompileStmt`), im Resolver zusätzlich
jeder Ausdruck (`ResolveExpr`, damit bei `f(a, b)` beide unbekannten Namen
gemeldet werden) und jedes Klassenmitglied. Ein Fehler setzt den
Resolver-Zustand (Scope-Kette, Tiefenzähler, aktuelle Klasse...) bzw. die
Scope-/Schleifen-Zähler des Compilers auf den Stand vor dem Wiederaufsetzpunkt
zurück; ein fehlgeschlagenes `var x = <Fehler>` deklariert `x` trotzdem, und ein
Enum mit fehlerhaftem Mitglied bleibt bekannt - beides gegen reine
Folgefehler. Der Compiler-Lauf findet nur statt, wenn der Resolver fehlerfrei
war (er braucht dessen Ergebnis). Compiler-Fehler tragen jetzt eine Zeile
(die des innersten betroffenen Ausdrucks/Statements). Der Parser bricht
weiterhin beim ersten Syntaxfehler ab (kein Wiederaufsetzen).

**Live-Diagnostik und Preludes**: die Editor-Live-Diagnostik (`LiveDiagnostics`)
kompiliert wie der Linker: Standard-Prelude UND die Preludes der per
`#import "graphics"`/`"devices"` zugeschalteten Erweiterungen (samt ihrer
nativen Platzhalter), gemeinsam in `ImportedPreludes` (von `Linker.CompileAndLink`
und `LiveDiagnostics` benutzt). Ein `#import` in einer ANDEREN Projektdatei gilt
auch für die gerade bearbeitete (`AnalyzeInProject`), wie beim echten Kompilieren
des ganzen Projekts.

## 17. `break`/`continue` für Schleifen

Anders als `with`/`switch` (reiner Parser-Zucker) brauchen echte
Schleifen-`break`/`continue` tatsächliche Sprünge im Bytecode, da die VM
Scopes über GEPAARTE `EnterScope`/`ExitScope`-Opcodes verwaltet
(Ownership/Destruktoren hängen daran) - ein naiver Sprung aus
verschachtelten Blöcken heraus, ohne die dazwischenliegenden Scopes
sauber zu schließen, würde die Scope-Kette der VM korrumpieren.

**AST/Parser**: `Ast.BreakStmt`/`ContinueStmt` (neu), `continue`-Keyword.
`break` bleibt innerhalb eines switch-case weiterhin der bestehende reine
Zweig-Abschluss (`Parser.ParseSwitchCaseBody` fängt es ab, BEVOR die
allgemeine `ParseStatement`-Dispatch es je sieht) - keine Kollision
zwischen den beiden Bedeutungen desselben Schlüsselworts.

**Resolver**: `_loopDepth`/`_tryDepth` (neu, analog zu `_functionDepth`,
aber anders als dieses beim Betreten einer neuen Methode/Lambda GESICHERT
UND AUF 0 ZURÜCKGESETZT statt nur erhöht - eine Schleife der
umschließenden Funktion darf aus einer verschachtelten Lambda heraus nicht
per `break` erreichbar sein). `break`/`continue` werfen, wenn
`_loopDepth == 0` (keine Schleife) oder `_tryDepth > 0` (würde über eine
`try`/`catch`/`finally`-Grenze springen - siehe unten, bewusst abgelehnt).

**Compiler** (der aufwendigste Teil): `_currentScopeDepth` zählt seit
Beginn des aktuellen Funktionskörpers offene `EnterScope`-Opcodes ohne
passendes `ExitScope` - NUR über die neuen `EmitEnterScope()`/
`EmitExitScope()`-Helfer verändert, die ALLE vorherigen rohen
`_chunk.EmitOp(OpCode.Enter/ExitScope)`-Aufrufe ersetzt haben (Ausnahmen
bewusst belassen: `CompileArrayAlloc`s interne, rein compiler-generierte
Schleife, die nie Nutzer-Code enthalten kann, und die catch-
Exception-Scope, die von der VM selbst erzeugt wird, OHNE einen
kompilierten `EnterScope`-Opcode - sie würde die Zählung verfälschen, wenn
man sie mitzählen würde). Zurückgesetzt wird `_currentScopeDepth` nie
manuell - jede Methode/jeder Konstruktor/jede Lambda bekommt ohnehin ein
FRISCHES `Compiler`-Objekt (`new Compiler(...)`), das Feld startet also
automatisch wieder bei 0.

Pro aktiver Schleife (`Stack<LoopCompileContext>` für Verschachtelung,
`break`/`continue` betreffen immer die INNERSTE) hält ein
`LoopCompileContext` die Scope-Tiefe beim Betreten des Schleifenkörpers
(`ScopeDepthAtLoopBodyStart`) sowie Sammel-Listen noch zu patchender
Sprungadressen. `CompileWhile`/`CompileFor`/`CompileForeach` wurden
entsprechend umgebaut: `continue` springt bei `while`/`foreach` direkt vor
den Rücksprung zur Bedingung, bei `for` gezielt VOR den Increment-Schritt
(sonst würde `continue` die Schleifenvariable nie weiterzählen - eine
faktische Endlosschleife). `break` springt ans Schleifenende, das bei
`for`/`foreach` korrekt in die vorhandene Aufräumlogik mündet (Schließen
des Init-Scopes bzw. Pop des Enumerators - derselbe Zielpunkt wie beim
normalen Bedingung-false-Austritt).

`Compiler.EmitScopeUnwindForJump` emittiert vor jedem break/continue-
Sprung `_currentScopeDepth - ctx.ScopeDepthAtLoopBodyStart` rohe
`ExitScope`-Opcodes (bewusst NICHT über `EmitExitScope()`, das würde
`_currentScopeDepth` selbst mitverändern) - das sind rein TEMPORÄRE Closes
nur für DIESEN Sprungpfad; die normale sequentielle Kompilierung (eigene
`ExitScope`-Aufrufe der umschließenden Blöcke) läuft danach unverändert
weiter, ist aber unerreichbar (der Sprung ist unbedingt), genau wie
Bytecode nach einem `return` mitten in einem Block.

**Bewusste Einschränkung**: `break`/`continue` funktionieren NICHT über
eine `try`/`catch`/`finally`-Grenze hinweg - vom Resolver hart abgelehnt
(`_tryDepth`), nicht nur "nicht implementiert". Grund: die catch-
Exception-Scope wird von der VM selbst (nicht über einen kompilierten
Opcode) erzeugt, UND ein Sprung aus einem `try`-Block heraus müsste
zusätzlich den registrierten Exception-Handler abmelden
(`UnregisterHandler`) - beides korrekt zu modellieren wäre ein deutlich
größeres, riskanteres Stück Arbeit gewesen, das hier bewusst nicht
angegangen wurde, statt es halbfertig/potenziell fehlerhaft einzubauen.

## 18. Aktueller Stand / Grenzen

Abgedeckt: Literale, Variablen (lokal/global), Arithmetik inkl. Anker-Regel,
Vergleiche, Kurzschluss-`&&`/`\|\|`, unäre Operatoren, `if`/`while`/`for`,
Blöcke mit echtem Ownership-Scope, native Aufrufe, Lambdas inkl. Aufruf und
`return` (Funktions-/Call-Frames, inkl. korrekter Ownership-Übergabe an den
Aufrufer), Klassen/Objekte (`new`, Felder, Methoden inkl. virtueller
Auflösung, `this`/`base`, Konstruktor-Verkettung, korrekte Ownership-Politik
bei direkter Feldzuweisung, Destruktor-Ausführung), Pointer/`unsafe`
(`&`/`*`, Pointer-Arithmetik, echtes Aliasing), Arrays (`new`/Deklarator-
Sugar (auch mehrdimensional/"jagged"), Array-Literale (auch verschachtelt),
Index-Zugriff/`length`, `[]`-Operator-Überladung per Namenskonvention
`GetIndex`/`SetIndex`, Bounds-Checking als fangbare `IndexOutOfBoundsException`),
Exceptions (`throw`/`try`/`catch`/`finally`, **inkl. `resume`**), Interfaces
(reiner Resolver-Check, keine Laufzeit-Repräsentation), `foreach` (über
`GetEnumerator`/`MoveNext`/`GetCurrent`, Duck-Typing), `List`
(Standardbibliotheks-Prelude in ScriptLang selbst), `is in`/`is of`/
`is from`/`is under`, `extern`-Linking (echte native Aufrufe per
`ExternRegistry` oder dynamisch per `#extern "libName"`, inkl. Pointer-
Marshalling zu echtem unmanaged Speicher), `#include` (Textvorverarbeitung),
`readonly` (Variablen/Felder), `enum` (Compile-Zeit-Konstanten), Properties
(`get`/`set` per Namenskonvention `get_`/`set_`, siehe Abschnitt 14),
Methodenüberladung (nach Parameteranzahl), `with`-Statement (reiner
Parser-Zucker), Extension-Klassen (`class extends`, siehe Abschnitt 16),
Konstruktor-Überladung, optionale Parameter mit Standardwert (Methoden/
Konstruktoren/Lambdas), `switch` mit Vergleichsoperatoren (reiner
Parser-Zucker, desugart zu If/Else-if, siehe Abschnitt 16), generische
Klassen mit `where`-Constraints (Syntax + statische Prüfung bei `new
Name<...>`, keine echte Typ-Substitution, siehe Abschnitt 16),
`break`/`continue` für Schleifen (echte Sprünge mit korrektem
Scope-Unwind, siehe Abschnitt 17).

Alle in der ursprünglichen Roadmap genannten Ausbaustufen sind damit
umgesetzt (plus die vom Nutzer nachträglich vorgeschlagenen `#extern`/
`#include`/`readonly`/`enum`/Properties/Methodenüberladung/`with`/
Extension-Klassen/Konstruktor-Überladung/optionale Parameter/`switch`/
Generics/`break`/`continue`). Multithreading wurde explizit als eigener,
späterer Schritt zurückgestellt (noch nicht begonnen). Bekannte Lücken (kein harter Fehler, bewusste Grenzen
statt Bugs): `resume`s Aufräum-Mechanismus greift nur beim normalen
Fall-Through eines `catch`-Blocks, nicht bei einem frühen `return` darin
(Abschnitt 9); `extern`-Pointer-Marshalling unterstützt nur einzelne
Primitivwerte hinter einem Pointer, keine Puffer/Arrays variabler Länge
(Abschnitt 12); Bounds-Checking gilt für `arr[i]`/`ArraySet`, nicht aber für
rohen Zeiger-Zugriff über `unsafe`/`&`/`*` (SPEC 8.3); `readonly`-Felder
werden nur für den statischen Fall `this.feld = ...` geprüft, nicht für ein
dynamisches Ziel (`obj.feld = ...`, SPEC 8.6); `enum` hat keine eigene
Laufzeit-Repräsentation (reine Int-Konstanten, kein `is of EnumName`, SPEC
8.7); `&obj.Property`/Pointer-Zugriff auf eine Property ist nicht sinnvoll
unterstützt (SPEC 8.8, gilt auch für Auto-Property-Backing-Fields); ein
selbst deklariertes Feld, das zufällig genauso heißt wie das Backing-Field
einer Auto-Property (`_AutoName`), kollidiert unbemerkt (SPEC 8.8); generische
Methoden werden nicht mit Typ-Argumenten am Aufrufort geprüft (Abschnitt
16); `break`/`continue` funktionieren nicht über eine
`try`/`catch`/`finally`-Grenze hinweg
(Abschnitt 17).

## 19. VM-Ausführungsmodi (Performance-Stufen)

`VmExecutionMode` (Bytecode/VmExecutionMode.cs), per optionalem Konstruktor-
Parameter an `VM` übergeben (Default `Debug`, unverändertes bisheriges
Verhalten - reine Opt-in-Erweiterung, kein Bytecode-Format-Unterschied,
derselbe kompilierte Chunk läuft unter jedem Modus):

- **`Debug`** (Default): der kooperative Shutdown-Prüfpunkt
  (`CheckShutdownSignals`, für `leave`/`terminate`) läuft vor JEDER
  einzelnen Instruktion. Alle Bounds-Prüfungen (Array/Puffer) aktiv.
- **`Release`**: NUR der kooperative Overhead sinkt - der Shutdown-
  Prüfpunkt läuft periodisch (alle 64 Instruktionen, `ShutdownCheckInterval`)
  statt vor jeder einzelnen. Bounds-Prüfungen bleiben vollständig aktiv,
  ein ungültiger Index bleibt eine fangbare `IndexOutOfBoundsException`.
- **`Performance`**: wie Release (Shutdown-Prüfpunkt alle 4096
  Instruktionen), zusätzlich überspringen `ArrayGet`/`ArraySet` die
  Bounds-Prüfung komplett (`ScriptArray`/`Values.ByteBuffer.
  Get/SetUnchecked`) - ein ungültiger Index führt zu einer ROHEN,
  ungefangenen .NET-`IndexOutOfRangeException` statt einer sauberen
  Skript-Exception (verwalteter C#-Code kann die Bounds-Prüfung des .NET-
  Arrays selbst nicht umgehen, nur die AUFWENDIGERE Umwandlung in eine
  Skript-Exception entfällt - kein `unsafe`-Speicherzugriff). Nur für
  bereits ausführlich getesteten Code gedacht.

Bewusst NICHT abgeschaltet (auch nicht in `Performance`):
`Value.RequireKind`-Typprüfungen (`AsInt()`/`AsString()`/etc.) - dafür
bräuchte es einen globalen, statischen Schalter (Value ist ein einfacher
Struct ohne Bezug zur VM-Instanz, ein Modus-Parameter müsste durch
tausende Aufrufstellen durchgereicht werden) statt eines lokal
begrenzten Eingriffs wie bei den Opcode-Handlern hier - als eigener,
separater Schritt zurückgestellt, falls der Bedarf sich zeigt.

`Runtime.FireRuntime.FireVm`/`FireVmTaking`/`CallCallback` haben denselben
optionalen `executionMode`-Parameter (Default `Debug`, wie beim VM-
Konstruktor selbst). Der VM-interne `fire { ... }`-Opcode-Handler gibt dabei
automatisch den `ExecutionMode` DER AUFRUFENDEN VM an den neuen Fire-Thread
weiter - ein `fire`-Thread, der aus einer `Performance`-VM heraus entsteht,
läuft also ebenfalls im `Performance`-Modus, statt stillschweigend auf
`Debug` zurückzufallen. `CallCallback` (native Callbacks, kein `fire`) hat
keine "aufrufende VM" und bleibt deshalb beim Default, sofern der
aufrufende Host-Code nichts anderes angibt.

## 20. Methodenauflösungs-Cache (`RuntimeClass.FindMethod`)

`RuntimeClass.FindMethod(name, argCount)` memoisiert jetzt sein Ergebnis
(`Dictionary<(string Name, int ArgCount), FunctionProto?>`) - vorher lief
JEDER einzelne `obj.Methode(...)`-Aufruf zur Laufzeit erneut über die
komplette Basisklassen-Kette (ein Dictionary-Lookup pro Vererbungsebene)
plus einen linearen Scan über die Überladungen. Sicher, weil `Methods`/
`Base` einer `RuntimeClass` NUR während der einmaligen Kompilierung
befüllt werden (siehe Compiler.CompileClass) und danach zur Laufzeit nie
mehr verändert werden - der Cache ist ab dem ersten Treffer für immer
gültig, keine Invalidierung nötig, kein Unterschied im Ergebnis (rein
additiv, jede vorher funktionierende Auflösung liefert weiterhin
dasselbe). `FindConstructor` bekommt aus demselben Grund einen O(1)-
Schnellpfad für den exakten Arity-Treffer (`Constructors` ist ohnehin
schon nach Parameteranzahl indiziert) statt immer den linearen
FindBestMatch-Scan zu durchlaufen.

Bewusst NICHT angegangen (größerer, hier zurückgestellter Schritt): der
noch verbleibende dictionary-basierte Feldzugriff auf `ObjectInstance`
(`obj.feld`) - jede Instanz hat ihre EIGENEN Feldwerte, ein reiner
Ergebnis-Cache wie bei FindMethod greift hier also nicht; das bräuchte
eine echte Slot-Zuweisung pro Klasse zur Kompilierzeit (ähnlich der
lokalen Variablen, siehe Abschnitt 1) statt eines Dictionary pro Instanz -
eine größere Architekturänderung, kein lokal begrenzter Eingriff.

## 21. Feldzugriff über Runtime.FieldStore (der oben zurückgestellte Schritt, jetzt umgesetzt)

`ObjectInstance.Fields` ist nicht mehr ein rohes `Dictionary<string, Value>`,
sondern `Runtime.FieldStore`: ein Array für die zur Kompilierzeit BEKANNTEN,
deklarierten Felder (fester Slot-Index über `RuntimeClass.FieldIndex`,
analog zu lokalen Variablen), mit Dictionary-Fallback für alles andere.

- `RuntimeClass.FlattenedFieldNames`/`FieldIndex`: die vollständige,
  vererbungsflache Feldliste (Basisklasse zuerst, rekursiv, dann die
  eigenen Felder) samt Name -> Index-Zuordnung - EINMALIG pro Klasse
  berechnet und gecacht (wie bei FindMethod, Abschnitt 20: Fields/Base
  ändern sich nach dem Kompilieren nie mehr).
- `ObjectInstance` bekommt zusätzlich zu `ClassDecl` jetzt optional die
  zugehörige `RuntimeClass` (`RtClass`) - gesetzt bei jeder Erzeugung über
  die normale VM-Pipeline (`NewObject`/`NewObjectWithOwner`/
  `ConstructNested`), `null` nur für ObjectInstances, die AUSSERHALB davon
  direkt konstruiert werden (z.B. reine Ownership-Modell-Tests ohne
  Klassendeklaration) - `Fields` fällt dann komplett auf den Dictionary-
  Fallback zurück, funktional unverändert.
- `FieldStore` implementiert bewusst DIESELBE API-Oberfläche wie vorher das
  rohe Dictionary (Indexer, `TryGetValue`, `ContainsKey`, aufzählbar als
  `(Name, Value)`-Paare) - SyncEngine (Cross-Thread-`sync`), ObjectCopier
  (`taking`-Kopien), PointerTargets (`&obj.feld`) und
  UncaughtScriptException brauchten dadurch KEINE Änderung ihrer eigenen
  Logik, nur ihre `new ObjectInstance(...)`-Aufrufe geben jetzt zusätzlich
  die RuntimeClass mit (für den schnellen Pfad auch in Kopien).
- Deklariert eine abgeleitete Klasse ein Feld mit demselben Namen wie eine
  Basisklasse erneut, gewinnt automatisch der spätere (eigene) Index - der
  geerbte Slot wird ungenutzt (etwas Speicher verschwendet, aber
  unbedenklich, siehe RuntimeClass.FieldIndex-Doku).

Für die C++-Portierung (docs/PORTING.md) ist das zusätzlich ein Schritt in
die richtige Richtung: Array + vorab berechneter Index-Tabelle ist deutlich
näher an dem, was eine C++-Fassung ohnehin bräuchte, als ein generisches
`Dictionary<string,Value>` pro Instanz.

## 22. Scope-Allokation in Schleifen (lazy `_slots`/`_owned`)

Jeder Schleifenkörper bekommt ein eigenes `EnterScope`/`ExitScope`-Paar -
läuft also bei JEDER einzelnen Iteration erneut (siehe Compiler.
CompileScopedBody, aufgerufen aus CompileWhile/CompileFor/CompileForeach;
`foreach` sogar ZWEIMAL pro Iteration - einmal für die Schleifenvariable,
einmal für den Body-Block, siehe CompileForeach). `VM.EnterScope` legt dafür
bisher IMMER eine neue `Scope`-Instanz an, deren Konstruktor wiederum IMMER
zwei leere `List<T>` anlegte (`_slots`/`_owned`) - macht bei einem simplen
Schleifenkörper ohne eigene `var`-Deklaration und ohne `new`-Ausdruck DREI
Heap-Allokationen PRO ITERATION, für einen Scope, der am Ende doch leer
bleibt.

`Runtime.Scope._slots`/`_owned` sind jetzt `null`, bis tatsächlich ein Slot
definiert (`DefineSlot`) bzw. ein Objekt übernommen wird (`AddOwned`) - rein
additiv, keine Verhaltensänderung (`OwnedObjects` liefert bei `null` ein
gemeinsames, leeres `Array.Empty<ObjectInstance>()`; `Release()` prüft
`null` und kehrt sofort zurück, ohne die (dann ohnehin leere) Liste zu
durchlaufen). Für den häufigen Fall eines Schleifenkörpers ohne lokale
Deklaration/Objekterzeugung sinkt das von drei Allokationen pro Iteration
auf eine (die `Scope`-Instanz selbst).

**Bewusst NICHT angegangen** (größere, riskantere Schritte, hier nur
vermerkt statt umgesetzt):

- **Scope-Objekt selbst wiederverwenden/poolen** statt pro Iteration neu
  anzulegen - spart auch noch die letzte Allokation, ist aber nur sicher,
  wenn NACHWEISLICH keine in dieser Iteration erzeugte Lambda den Scope per
  Closure erfasst hat (sonst sähe eine bereits erfasste Closure aus
  Iteration N plötzlich die Werte aus Iteration N+1 - ein subtiler
  Korrektheitsfehler, kein reines Performance-Detail). Bräuchte entweder
  eine statische Analyse (deklariert der Körper irgendwo eine Lambda?) oder
  eine Laufzeit-Markierung ("wurde dieser Scope je von einer Closure
  erfasst?").
- **`EnterScope`/`ExitScope` komplett überspringen**, wenn der Schleifenkörper
  nachweislich weder eine lokale Variable deklariert NOCH (auch nicht
  verschachtelt in einem Ausdruck) ein Objekt erzeugt - dafür reicht die
  Slot-Anzahl allein nicht, ein `new X()` mitten in einem Ausdruck OHNE
  eigene `var`-Zuweisung würde sonst plötzlich vom UMSCHLIESSENDEN statt vom
  Iterations-Scope besessen (verzögerte/verspätete Destruktor-Ausführung -
  bricht die deterministische Zerstörungs-Garantie, die diese Sprache
  bewusst verspricht). Bräuchte eine sorgfältige, rekursive AST-Prüfung
  (unter Ausschluss verschachtelter Blöcke/Lambda-Bodies, die ihre eigene
  Scope-Grenze mitbringen).
- **`foreach` auf EINEN Scope statt zwei pro Iteration verschlanken** -
  Schleifenvariable und Body teilen sich aktuell zwei separate,
  verschachtelte Scopes (siehe CompileForeach). Zusammenlegen würde die vom
  Resolver berechneten Tiefen (`ResolvedRef.Local(depth, slot)`) für alles
  im Body neu berechnen müssen - ein Fehler dabei bricht Variablenauflösung
  auf eine schwer zu findende Art, deshalb hier nicht leichtfertig
  angegangen.

## 23. IO-Bridge (`#import "io"`) und zwei Sprachänderungen dazu

`src/fire.IO.Bridge`: dasselbe Muster wie `GraphicsBridge`/`DeviceBridge` - `IoBridge.RegisterAll`
(echt) / `RegisterStubs` (nur Namen, für Linker und Live-Diagnostik über `ImportedPreludes`),
`IoBridge.PreludeSource` (fire, `namespace IO`), `NativeImports.IO = "io"`. Streams sind
Handles (`ConcurrentDictionary<int, StreamEntry>` pro `RegisterAll`, jeder Stream mit eigener
Sperre); jede native Funktion meldet einen Fehler mit `-1` und merkt sich Code/Meldung pro Thread
(`[ThreadStatic]`, `__IOLastError`/`__IOLastErrorMessage`) - der Prelude wirft daraus die typisierte
Exception (`IOErrors.Throw`). `ReadByte` liefert -2 am Ende (-1 = Fehler). Die `IoPolicy` des Hosts
(`AllowAll`/`DenyAll`/`Rooted`) wird vor jedem `FileOpen` mit dem vollständigen Pfad befragt;
`RuntimeSession.Build` (Compiler- und Runtime-Fassung) nimmt sie als optionalen Parameter.

`FileStream`/`MemoryStream` übergeben ihrer Basis `NativeStream` zuerst `-1` und öffnen im eigenen
Konstruktor-Body: scheitert das Öffnen, ist das Objekt trotzdem vollständig aufgebaut und sein
`destruct()` schließt nichts. (Scheitert ein Konstruktor dagegen schon beim Auswerten der
Basis-Argumente, haben die Felder nur den Standardwert `false`.)

Datei-API (`IoFileSystem.cs`, `IoHost` ist `partial`): jeder Skript-Pfad läuft durch `Authorize`
(`Path.GetFullPath` + `IoPolicy.IsAllowed`), `OnPath` fängt .NET-Fehler (`FailFrom`). Listen kommen als
`ScriptArray` von Strings zurück, Zeiten als `int` mit der Einheit `s`, Text über `Utf8Encode`/
`Utf8Decode`/`SplitLines`. `IO.File`/`IO.Directory`/`IO.Path`/`IO.Utf8` sind statische fire-Klassen
im Prelude (die Ganzdatei-Funktionen bauen auf `FileStream` auf).

Text/Stdio: `TextReader` liest in 4096-Byte-Blöcken vom Stream und sucht Zeilenenden mit dem nativen
`BufferIndexOf` (ein Byte-Lauf in fire wäre zu langsam), Zeilen über Blockgrenzen sammelt ein
`MemoryStream`; UTF-8 ist zeilenweise sicher, weil `0x0A` in Mehrbyte-Folgen nie vorkommt. Der Host
gibt `IoStdio` mit (`SystemConsole` oder `Custom(...)` mit einem `LineCallbackStream`, der über
Chunk-Grenzen dekodiert); die drei Standardstreams sind pro `RegisterAll` einmalig, ihre Handles
`Permanent` (ein `Close()` aus dem Skript schließt sie nicht), Ausgabe/Eingabe laufen über
`StdWrite`/`StdReadLine`/`StdReadAll`.

Dafür waren Änderungen an der Sprache nötig:
- **Destruktor-Kette:** `VM.RunDestructor` ruft die Destruktoren der ganzen Klassenkette (abgeleitete
  Klasse zuerst, dann jede Basisklasse) - vorher nur den der konkreten Klasse, eine abgeleitete
  Klasse ohne eigenen `destruct()` hätte den ihrer Basis also nie ausgeführt.
- **Enums in Namespaces:** `Resolver` erkennt `Name.Mitglied` jetzt auch für einen punktierten Namen
  (`IO.FileMode.Create`, `DottedName`) - wie beim statischen Klassenzugriff zählt nur der exakt
  geschriebene Name. Und `class X : Namespace.Basis` akzeptiert einen qualifizierten Basisnamen.
- **Felder vor der Konstruktion:** `FieldStore` belegt deklarierte Felder mit `undefined` statt mit
  `default(Value)` (= `false`). Bricht ein Konstruktor ab, bevor die Feld-Initialisierer liefen (z.B. eine
  Exception beim Auswerten der `base(...)`-Argumente), sieht ein `destruct()` `undefined`, kein erfundenes
  `false`.
- **`foreach` über Arrays/Puffer:** `CallMethod` auf einem Array/Puffer mit `GetEnumerator()` konstruiert
  (`ConstructNested`) einen `ListEnumerator` der Prelude über die Elemente.
- **Statische Property-Setter:** `SetStaticField` fällt auf die statische `set_`-Methode zurück (wie
  `GetStaticField` auf `get_`).

## 24. Basistyp-Erweiterungen (`class extends string`) und die String-/Char-Methoden

**Parser.** `class extends <string|char|int|float|bool>` (Schlüsselwort statt Bezeichner,
`ParseClassExtensionDecl`) wird wie jede Erweiterung als `ClassExtensionDecl` geparst, danach aber sofort
geprüft (`ValidateBaseTypeExtensionMember`): nur `MethodDecl`, weder `static` noch `operator`/`GetIndex`/
`SetIndex`; sonst `ParseException` (Feld/Property/Konstruktor/Destruktor/static/Operator; `byte` und andere
Typen werden abgelehnt). `MergeClassExtensions` hat keine `ClassDecl`, in die es mergen könnte - es sammelt
alle Blöcke desselben Basistyps in **eine synthetische `ClassDecl`** mit dem Namen `$string`, `$char`, ...
(`fire.Standard.BaseTypeExtensions.ClassName`; `$` kann kein Bezeichner enthalten, es kollidiert also nie mit
einer Nutzerklasse). Resolver, Compiler und Linker sehen eine ganz normale Klasse ohne Sonderfall.

**VM.** Der Konstruktor legt `_baseTypeClasses` an (Array, indiziert mit `(int)ValueKind`; gefüllt aus
`_classes` über `BaseTypeExtensions.ClassNameFor`). `CallMethod` auf einem Nicht-Objekt sucht dort zuerst
die Methode (`FindMethodWithAccess`, dann `CheckArity`/`FillDefaultArgs` wie bei Objekten) und ruft sie mit
dem Wert selbst als `this` (`_currentThis` ist ein geboxter `Value`, `LoadThis` kannte das schon); erst wenn
keine Erweiterungsmethode passt, kommt `TryCallBuiltinMethod` (Konvertierungen aus 8.10). `SetFieldOnThis`
auf so einem `this` scheitert - ein Basiswert hat keine Felder.

**Native Funktionen.** `fire.Standard.StringMethods`/`CharMethods` (im Kernprojekt `fire`, nicht in
`fire.Runtime`): je eine `NativeFunction` (`__StringCall`, `__CharCall`), deren erstes Argument die Methoden-ID
(`enum StringMethod`/`CharMethod`, feste Zahlen) und das zweite der Wert ist; `switch` über die ID,
Argumentzahl je Fall geprüft. `NativeRegistry.RegisterBaseTypeNatives()` registriert beide und muss in
**jeder** Registry direkt hinter `print` stehen (Linker, beide `RuntimeSession.Build`, `CreateDefault`) - native
Funktionen werden über ihren Index aufgerufen, und Kompilier- und Laufzeit-Registry müssen dieselbe
Reihenfolge haben. Der fire-Text des Prelude (`class extends string { int IndexOf(value) { return
__StringCall(1, this, value) } ... }`) wird aus einer Signaturtabelle erzeugt (`PreludeSource`);
`Prelude.Source` ist deshalb `static readonly` (Kernklassen + beide Erweiterungen).

**Fehler aus nativen Funktionen.** Ein ungültiger Index wirft `NativeIndexOutOfRangeException(index, length,
what)`; `VM.CallNativeGuarded` (bewusst eine eigene Methode statt try/catch mitten in `Execute`) fängt sie am
`CallNative`-Aufruf und macht daraus per `ThrowIndexOutOfBounds` die fangbare `IndexOutOfBoundsException`
(„String-Index …“). Nicht abgefangen bleiben andere .NET-Fehler (falsche Argumenttypen an der nativen
Funktion) - sie sind Programmierfehler, keine Skript-Exceptions.

**Weiter in der VM statt im Prelude:** `Length`/`length` (`GetField` auf String/Array/Puffer), `s[i]`
(`ArrayGet` liefert ein `char`, `ArraySet` auf einem String wirft) - eine Erweiterung darf keine Property
definieren.

**Editor.** `ScriptSymbolIndex.HarvestClassExtension` legt für ein Basistyp-Schlüsselwort ebenfalls die
Sammelklasse `$string` an (aus dem Prelude-Index UND dem Dokument, sie werden wie bei `class extends List`
zusammengeführt); `CompletionEngine` und `ScriptSymbolIndex.MemberType` lesen für Primitive daraus Methoden und
Rückgabetypen (`BuiltinMembers.ExtensionClassOf`). `BuiltinMembers` enthält nur noch, was die VM selbst
liefert (`Length`, `ToBytes()`, Puffer-Mitglieder), plus einen Typ-Hinweis für `Split` (ein Array-Rückgabetyp
lässt sich in fire nicht hinschreiben). Die Sammelklassen erscheinen nicht als Typen in der
Vervollständigung. "Zu Definition springen" erkennt Prelude-Mitglieder jetzt an der Herkunft des Mitglieds
(`MemberInfo.Source`) statt am Flag der Klasse - wichtig, sobald eine Klasse (`$string`) Mitglieder aus
Prelude UND Nutzerdokument hat.

**Kosten.** Ein Aufruf wie `s.IndexOf("o")` läuft jetzt über einen fire-Frame plus die native Funktion statt
über einen direkten `switch` in der VM; im Debug-Modus gemessen etwa 30 % langsamer als die frühere
VM-eingebaute Fassung (300 000 × `IndexOf` + `Length`, dann 300 000 × `Substring(..).Length`: 3,0 s statt
2,3 s). Der Namensvergleich ist es nicht - er steckt nur noch im normalen Methoden-Lookup der Klasse.

## 25. Array-Rückgabetypen (`int[] Name()`)

`TypeRef` hat das Feld `ArrayRank` (Anzahl leerer Klammerpaare, Vorgabe 0). `Parser.ParseTypeRef(allowArray)` liest
`[` `]` hinter Basisname/Bitbreite/`*`; `[8]` (mit Zahl) bleibt die Bitbreite (`NextIsEmptyBrackets` unterscheidet,
`byte[]` ist erlaubt, `byte[8]` nicht). `allowArray` ist nur bei Methoden-/Property-/Interface-Rückgabetypen gesetzt;
sonst (Variable, Parameter, `extern`) wirft `ParseArrayTypeSuffix` einen Fehler mit dem Hinweis auf `Typ name[]`, bei
Feldern erst nach dem Namen (Klassenmitglieder wissen vorher nicht, ob eine Methode folgt). `NextLooksLikeTypeThenName`
überspringt leere Klammerpaare hinter einem Klassennamen (`Dog[] Name`; `Name[] Name` kommt in keinem Ausdruck vor).
Resolver, Compiler und VM lesen `ArrayRank` nicht - Rückgabetypen werden nur auf existierende Typnamen geprüft.
Editor: `HarvestMember` überspringt die Klammern und setzt `MemberInfo.TypeIsArray` (für Methoden und Properties =
Elementtyp + Array), `TypeOfMember` macht daraus `TypeKind.Array`. Das Prelude nutzt es für `string[] Split(...)`.

## 26. VM-Performance und das Benchmark-Projekt

**Messen.** `src/fire.Benchmarks` (`dotnet run -c Release --project src/fire.Benchmarks -- --mode all`) führt neun kleine
fire-Programme aus (`loop`, `float`, `fib`, `method`, `array`, `string`, `alloc`, `lambda`, `list`; Beschreibung mit `--list`),
misst nur `VM.Run()` (1 Aufwärmlauf, dann `--runs N`, Ausgabe: kleinste und mittlere Zeit) und zeigt das Ergebnis jedes
Programms. **Regressionsschutz:** `--save datei` speichert diese Ergebnisse, `--check datei` prüft sie später (Exit-Code 1
bei Abweichung) - eine Optimierung darf sie nie ändern. `--file x.fire` misst ein eigenes Skript. Die Zeiten schwanken auf
geteilten Rechnern stark (±20 %); für Vergleiche zwei Builds abwechselnd laufen lassen und jeweils das Minimum nehmen.

**Ergebnis** (Modus Performance, 4-Kern-Xeon 2,1 GHz, kleinste Zeit in ms):

| | vorher | nachher |
|---|---:|---:|
| loop (1,5 Mio. Iterationen) | 4542 | 216 |
| float | 2293 | 114 |
| fib(23) | 212 | 18 |
| method (250 000 Aufrufe) | 1099 | 74 |
| array | 3356 | 150 |
| string | 319 | 33 |
| alloc (60 000 Objekte) | 562 | 62 |
| lambda | 816 | 67 |
| list | 836 | 57 |
| **Summe** | **14035** | **791** |

Alle Ergebnisse (und die komplette Testsuite) sind unverändert. Was den Unterschied macht, in der Reihenfolge der Wirkung:

1. **`Execute()` nicht pro Instruktion aufrufen.** Die Methode ist riesig und hat sehr viele lokale Variablen (v.a. `Value`-
   Structs mit Referenzen); der JIT nullt ihren Stackframe bei JEDEM Aufruf. Das war der größte Einzelposten. Jetzt gibt es
   `Step(op)` mit den häufigsten Opcodes direkt ausgeschrieben (Laden/Speichern, Rechnen, Vergleichen, Springen, Scopes) -
   `[AggressiveInlining]`, es steckt also in `Run()`, `RunNestedUntil()` und `StepInstruction()` selbst; alles andere geht an
   `Execute()`. Die schweren Fälle (Aufrufe, Feldzugriffe, `new`, Arrays, ...) sind eigene Methoden `Op<Name>()` (Schnellpfad)
   und `Op<Name>Slow(...)` (der bisherige Code, unverändert). **Regel für Änderungen:** häufige Opcodes gehören in `Step`
   bzw. eine `Op*`-Methode, nicht als neuer `case` mit vielen Locals in `Execute`.
2. **`Value` von 64 auf 24 Byte** (`Kind`, `Width`, ein `long` für Int/Float-Bits/Bool/Char und EINE Referenz für
   String/Objekt/`Unit`). Ein Value wird bei jedem Stack-Zugriff kopiert.
3. **Arrays statt `List<T>`** für Werte-Stack (`_stack`/`_sp`), Code und Konstanten (`Chunk.CodeArray`/`ConstantsArray`, beim
   Bauen verworfen, danach fest) und Scope-Slots (`Scope`, Parameter werden beim Aufruf direkt vom Stack hineinkopiert:
   `VM.EnterCall`).
4. **Rechnen ohne Umwege:** `Unit.Equals`/`Value.Add` & Co. erkennen "dieselbe Einheit-Instanz" (meist `Unit.Unitless`) per
   Referenzvergleich; `Value.Try*InPlace` überschreiben den linken Operanden direkt im Stack. Kein Schnellpfad, wenn die
   Einheiten verschieden sind, `%`/`/` durch 0 oder ein Objekt links steht - dann läuft der bisherige Weg samt Ausnahmen.
5. **Inline-Caches** (`Bytecode.SiteCache`, `Chunk.SiteCaches`, indiziert mit dem Byte-Offset des Opcodes) für
   `CallMethod`, `CallStaticMethod`, `GetField`, `SetField`, `SetFieldOnThis` und `NewObject`: die Stelle merkt sich Klasse des
   Empfängers und Ziel (Methode bzw. Feld-Index) und überspringt beim nächsten Mal die Dictionary-Lookups (Klassenname,
   Methodenname+Arity, Feldname). Ein Eintrag entsteht erst, nachdem die langsame Route ALLE Prüfungen bestanden hat
   (Zugriffsmodifikator, Argumentanzahl, Einheiten-Vorgabe eines Feldes - ein Feld `int x : mm` wird deshalb nie gecacht),
   und gilt nur für dieselbe Empfängerklasse; ein Objekt mit Actor-Postfach oder Thread-Lock (`ThreadLock != null`) geht
   immer den langsamen Weg. Einträge sind unveränderlich und werden als Ganzes ersetzt (Threads teilen sich die Chunks).
6. Kleinteiliges: `Array`-`Get`/`Set` mit gültigem int-Index ohne Umweg, `ObjectInstance` legt seine Besitz-Liste erst bei
   Bedarf an.

**Offen / Ideen** (nicht gemacht, weil sie Semantik berühren oder wenig bringen): Scopes von Schleifenkörpern ohne
Deklarationen weglassen (würde die Besitz-/Destruktor-Zeitpunkte von in dem Block erzeugten Objekten verschieben, Resolver und
Compiler müssten sich einig sein), Superinstruktionen im Compiler (`LoadLocal+LoadConst+Add`), eine Ein-Element-Besitzliste
in `Scope` (spart bei `new` in Schleifen zwei Allokationen), die Signalprüfung pro Instruktion im Debug-Modus
(`CheckShutdownSignals` fragt eine `ConcurrentQueue` ab), `_frames` als Array.

## 27. Kopier-Präfixe `flat x` / `copy x`

**Parser/AST.** `copy` ist ein neues Schlüsselwort (`TokenType.Copy`), `flat` gab es schon (`sync flat`). `ParseUnary` liest
`flat`/`copy` als Präfix und erzeugt ein `UnaryExpr` mit den neuen `UnaryOp.FlatCopy`/`DeepCopy` (Operand wieder ein Unary-Ausdruck;
`sync flat x` liest sein `flat` in `ParseSync` selbst und kommt hier nie an). Der Resolver braucht nichts Eigenes (er löst den Operanden auf).

**Compiler/VM.** Zwei neue Opcodes, hinter `Halt` angehängt (damit alle bisherigen Zahlenwerte stabil bleiben): `CopyValue u8 flags`
(bit0 = tief; pop Quelle, push Kopie, Owner = aktueller Scope) und `CopyValueOwned u8 flags` (pop Quelle, pop Owner-Objekt, push Kopie) -
letzteren erzeugt `CompileAssign` genau dort, wo es auch `NewObjectOwned` erzeugt (`obj.feld = copy x`; eine bloße `feld = copy x` im Innern
einer Klasse gehört wie bei `new` dem Scope). Beide laufen über `Execute` (kein heißer Pfad); die Arbeit macht `Runtime.ObjectCloner.Clone`.

**ObjectCloner.** `flat`: neue `ObjectInstance` (gleiche Klasse, Owner wie übergeben) und Feld für Feld übertragen (`Snapshot` unter dem
Baum-Lock, falls das Objekt an einem `taking`-Thread hängt). `copy` (`DeepCopier`) in drei Phasen: (1) Entdecken - alles über Felder/Array-
Elemente Erreichbare mit Feld-Schnappschüssen sammeln (Stack statt Rekursion, Actors und Zerstörtes bleiben draußen); (2) Anlegen - die
Wurzel zuerst, danach jede Instanz unter der Kopie ihres Besitzers, sofern der mitkopiert wird (Rekursion über die Besitzer endet, der Besitzbaum
ist zyklenfrei), sonst unter dem Wurzel-Owner; (3) Füllen - Feldwerte über die Identitätstabellen (`_copies`, `_arrayCopies`, `_bufferCopies`)
abbilden; ein Array trägt sich vor dem Füllen ein, darf sich also selbst enthalten. Ein Array als Operand läuft durch dieselben Phasen ohne
Wurzel-Instanz.

**Editor.** `copy` steht in der Keyword-Liste und im Syntax-Highlighter; `EvalExprRangeCore` überspringt das Präfix, `var d = copy a` hat also den
Typ von `a`.

## 28. Kopien als Argument/Zuweisung (Owner), `leave`, `TakeTo`

**`CopyArgs`-Präfix.** `Compiler.CompileArgs` wertet bei einem Aufruf, der eine Scope für die aufgerufene Funktion erzeugt (Lambda, Methode, `static`, `base.`,
`new`, `base(...)`), ein Argument `flat x`/`copy x` nur als `x` aus und sammelt eine Maske (2 Bit je Argument, höchstens 16); `EmitCopyArgsPrefix` schreibt
`CopyArgs lo hi` DIREKT vor den Aufruf-Opcode. Jeder dieser Opcodes holt die Maske sofort zu Beginn ab (`VM.TakeCopyMask`, sie gelangt so nie an einen späteren Aufruf) und
wendet sie an, sobald die Scope der aufgerufenen Funktion steht (`ApplyCopyMask`: `ObjectCloner.Clone(slot, scope, deep)`) - auch in den Inline-Cache-Schnellpfaden
(`EnterCall(..., copyMask)`). Aufrufe ohne Scope (eingebaute Methoden, Actor-Nachrichten) kopieren auf dem Stack mit dem aktuellen Scope als Owner
(`ApplyCopyMaskToArgs`); native Funktionen bekommen vom Compiler gar kein Präfix, dort bleibt es `CopyValue`.

**Zuweisung an ein Objekt.** `Compiler.IsOwnedCreation`/`TryCompileOwnedCreation` fassen `new`, `flat` und `copy` zusammen: der künftige Owner liegt schon auf dem Stack
(`Dup` bei `obj.feld = ...`, `LoadThis` bei einem bloßen Feldnamen und im Instanzfeld-Initialisierer), danach `NewObjectOwned` bzw. `CopyValueOwned`. `CopyValueOwned` läuft über
`ObjectCloner.CloneOwnedBy` - ist der Owner schon zerstört, entsteht die Kopie in einer Wegwerf-Scope, die sofort freigegeben wird (Verhalten wie `TakeTo`, SPEC 2.2).
Der Instanzfeld-Initialisierer (`CompileFieldInitProto`) und ein bloßer Feldname in `CompileAssign` gehörten bisher der Initialisierer-/Methoden-Scope: das `new` darin wurde
beim Verlassen zerstört, obwohl das Feld darauf zeigte - jetzt gehört es der Instanz (SPEC 2.1).

**`TakeTo`/`TakeUpwards`/`TakeGlobal`** (SPEC 2.2) waren nur in C# vorhanden. `VM.TryCallOwnershipMethod` macht sie zu eingebauten Methoden jedes Objekts (greift nur, wenn die Klasse
nichts Gleichnamiges deklariert, also nach dem Methoden-Lookup).

**`leave`.** `Leave`/`Terminate` rufen `ForceShutdownCheck()` (setzt den Instruktionszähler auf "fällig"), damit `Run()` das Signal vor der nächsten Instruktion prüft statt erst nach
dem Prüfintervall (Release 16, Performance 4096). `UnwindForShutdown(destroyGlobalScope: true)` gibt bei `leave` nach dem Abwickeln der Frames auch `_globalScope` frei; `terminate`
und die Abwicklung nach einer unbehandelten Exception lassen den globalen Scope wie bisher aus.

**Host-Sicherheitsnetz.** `IoBridge.RegisterAll` gibt ein `IDisposable` zurück (`IoHost.Dispose` schließt alle noch offenen, nicht-permanenten Streams); die Sessions halten es
(`IoResources`), `Runtime.Session.Run()` ruft es nach dem Lauf, `RuntimeSession.CloseHostResources()` ist für Hosts, die die VM selbst treiben (der Step-Debugger `DebugSession` ruft es, sobald alle Threads des Laufs beendet sind, und in `Reset()` bei einem abgebrochenen Lauf; höchstens einmal je Lauf).

## 29. Shutdown-Signale an sicheren Punkten, Programmende räumt ab

**Prüfung.** `Run()` fragt nichts mehr pro Instruktion ab. Jedes Signal (`RequestLeave`, `RequestTerminate`, eine eingereihte Fire-Thread-Exception) erhöht den globalen Zähler
`s_signalEpoch` (`RaiseSignal`); jede VM merkt sich den zuletzt gesehenen Stand (`_seenEpoch`). Die sicheren Punkte rufen `PollSignals()` (ein `Volatile.Read` und ein Vergleich):
Rücksprung-`Jump` (Ziel ≤ aktuelle Position, d.h. jede Schleife), Beginn von `Call`/`CallMethod`/`CallStaticMethod`/`NewObject`, und - als `PollSignalsAfterOp` - nach
`CallNative`/`CallTryableNative`/`CallExtern` (eine native Funktion darf `leave` auslösen) sowie in den Opcodes `Leave`/`Terminate` selbst. Nur bei einer Abweichung läuft die eigentliche
Prüfung `CheckShutdownSignals`; sie liegt mitten in einer Instruktion, deshalb rechnet `PollSignalsSlow` mit `_ip--` auf die Instruktionsgrenze (ein dort genesteter `catch threads`-Handler
kehrt an die richtige Stelle zurück). In verschachtelten Ausführungen (`_nestedDepth > 0`: Destruktor, Operator-Überladung, Property) wird nicht geprüft, das Signal bleibt stehen.
Debug/Release/Performance unterscheiden sich dadurch nicht mehr (`ShutdownCheckInterval` ist weg).

**Beenden ohne Exception.** `StopExecution()` setzt `_stopExecutionRequested` und stellt Chunk/ip auf `StopChunk` (nur `Halt`); die aufrufende Instruktion kehrt sofort zurück
(`if (PollSignals()) return;`, wie nach `ThrowException`), `Run()` liest das `Halt` und endet. Verschachtelte Schleifen, `StepInstruction` und `CallLambdaEntry` prüfen das Flag weiterhin
(sie sind nicht der heiße Pfad); `RunDestructor` und die `RunNestedUntil`-Aufrufer verwerfen den Rückgabewert nur, wenn nicht gestoppt wurde (`PopNestedResult`), und ein Destruktor, der
die VM beendet, bricht die restliche Kaskade ab. (Eine Ausnahme als Abbruchweg wurde bewusst nicht gewählt, siehe docs/PORTING.md.)

**Programmende.** Beim `Halt` der Hauptschleife (nicht beim `Halt` des `StopChunk`) wird der globale Scope freigegeben (`ReleaseGlobalScopeAtEnd`): das Hauptprogramm (jede VM, die kein
Fire-Thread ist) wartet vorher mit `FireRuntime.WaitForAllFireThreads()` auf alle lebenden Fire-Threads (`FireRuntime.Fire` zählt sie), ein Fire-Thread gibt nur Objekte ohne `SyncOrigin`
frei (`Scope.ReleaseWhere`; Globals-Schnappschuss und `taking`-Kopien sind Kopien von Objekten des Hauptprogramms und dürfen z.B. kein geteiltes Handle schließen). `leave` tut dasselbe
(`UnwindForShutdown(destroyGlobalScope: true)`), `terminate` und die Abwicklung nach unbehandelter Exception nicht. `VM.DestroyGlobalsAtEnd = false` schaltet es für Hosts ab, die die
Objekte nach dem Lauf noch verwenden (die Thread-Tests tun das); im Einzelschritt (`StepInstruction`) wird ohne Warten freigegeben. Die IO-Destruktoren (`NativeStream`, `TextReader`,
`TextWriter`) verschlucken IO-Fehler, weil ein Destruktor nie werfen soll.

## 30. Packer: eigenständige Datei, Bridges nur bei Bedarf, eigener Lader (kein Costura/Fody mehr)

**Ergebnis.** `Linker.CompileAndLink(..., outname)` erzeugt EINE Datei, die ohne daneben liegende DLLs läuft. Sie besteht aus
1. dem **Start-Stück**: `fire.Runtime.exe` (apphost) + `fire.Runtime.dll` + `fire.Runtime.runtimeconfig.json` als .NET-Single-File-Bundle (Format v6, `BundleWriter`; dasselbe Layout wie
   `dotnet publish -p:PublishSingleFile=true` für framework-abhängige Apps, selbst geschrieben, damit der Compiler kein SDK braucht). Mehr braucht der .NET-Host nicht; der Host liest nur ab dem im apphost
   eingetragenen Header-Offset, Daten dahinter stören nicht. Es läuft weiterhin eine installierte .NET-8-Runtime vor (framework-abhängig).
2. dem **Payload** dahinter (`PayloadFile`): Programm (MemoryPack), Kern (`fire.dll`, `MemoryPack.Core`) und - NUR bei entsprechendem `#import` - die Bridge-DLLs samt Abhängigkeiten und nativen Bibliotheken. Jeder
   Eintrag ist einzeln Brotli-gepackt (nur wenn es etwas bringt) und trägt eine SHA-256-Prüfsumme. Stufe: das Programm mit `Optimal` (Millisekunden), die DLLs mit `SmallestSize`
   (Qualität 11, für SDL3-CS.dll ~3 s) - aber nur einmal: das Ergebnis liegt im Cache `%TEMP%/fire-pack-cache/<SHA-256 der DLL>.br`, jeder weitere Build liest es nur (Build ~0,3 s statt 1,6-5 s). Ohne Schreibrecht
   auf den Cache wird schnell (`Optimal`) gepackt. Layout: `[Einträge][Index][Fuß: int64 Index-Offset, int32 Index-Länge, "FIREPAK1"]` - gelesen wird über den
   Fuß, die frühere Marker-Suche (`DA 1D`) gibt es nicht mehr.

**Welche DLLs.** `PackagePlan.Create(nativeImports, baseDir)`: Kern immer; je Import die Einstiegs-Assemblies (`graphics`: `fire.Terminal.Bridge`/`.Windows`/`.Sdl` + natives SDL3; `devices`: `fire.Device.Bridge`/
`fire.Device.Manager` (+ `libSystem.IO.Ports.Native` außerhalb von Windows); `io`: `fire.IO.Bridge`; `print`: nichts). Der Rest folgt aus den Assembly-Verweisen der DLLs (System.Reflection.Metadata): alles, was neben
dem Compiler liegt und nicht zum .NET-Framework gehört, kommt mit. Plattform-Unterordner (`runtimes/win/lib/...`, `runtimes/unix/lib/...`) haben Vorrang vor dem Hauptordner (System.IO.Ports liefert dort
nur eine Attrappe). `PackagePlan.Unresolved` (Verweis ohne Datei und nicht im Framework) lässt `Packer.PackProgram` mit einer Fehlermeldung abbrechen statt eine kaputte Datei zu erzeugen. Native Bibliotheken
werden nur für die Plattform des Compilers eingebunden (passend zum apphost, den er mitbringt).

**Laden zur Laufzeit.** `Program.cs` ruft nur `Bootstrap.Start()`: `PayloadLoader.Install()` öffnet den Payload der eigenen Datei (`Environment.ProcessPath`) und hängt `AssemblyLoadContext.Default.Resolving`
(verwaltete DLLs, `LoadFromStream`) und `ResolvingUnmanagedDll` (native Bibliotheken) ein; erst danach läuft `Bootstrap.Run` (`[NoInlining]`), der als Erstes `fire.dll` braucht. Die Ereignisse feuern erst,
wenn der Standard-Kontext eine Assembly nicht findet, also genau beim ersten echten Gebrauch - eine nicht eingebundene Bridge wird nie angefasst. Native DLLs (Windows kann keine aus dem Speicher laden) werden einmal nach
`%TEMP%/fire-native/<Prüfsumme>/` geschrieben (über eine Zwischendatei + `File.Move`, also auch bei gleichzeitigen Starts sicher) und von dort geladen; spätere Starts nutzen die Kopie.

**Regel für `fire.Runtime`.** Der JIT löst einen Typ schon beim Übersetzen einer Methode auf, die ihn in Signatur, lokaler Variable oder Aufruf erwähnt. Deshalb steht jeder Zugriff auf Bridge-Typen in
`Session.RegisterGraphics/RegisterDevices/RegisterIo` (`[MethodImpl(NoInlining)]`, nur betreten wenn der Import da ist); `Session` selbst hat keine Bridge-Typen in Feldern/Properties/Parametern (die früheren
Properties `WindowManager`/`FramebufferManager`/`ConsoleManager` und die Parameter `ioPolicy`/`ioStdio` von `Session.Build` sind entfallen - Hosts mit eigener Policy nutzen `fire.Compiler.RuntimeSession`). Ebenso
darf `Main` keinen Typ aus `fire.dll` erwähnen. Neue Bridge = Eintrag in `PackagePlan.Imports` + eigene `Register...`-Methode.

**Icon/Version.** `PeResourceEditor` ändert die PE-Ressourcen und verschiebt damit Dateiinhalt: das geschieht jetzt auf einer Kopie des apphost VOR dem Bündeln (`PackProgram(..., customizeApphost)`), sonst wäre
der Header-Offset des Bundles ungültig. Geschrieben wird erst in `<outname>.tmp`, dann umbenannt. Die Runtime liest ihr Programm aus der eigenen Datei (früher hart `tempout.exe` neben der Runtime).

**Größen (Linux-Build, Richtwerte).** Nur `print` ~300 KB, `io` ~320 KB, `devices` ~350 KB, `graphics` ~700 KB (Windows zusätzlich komprimiertes SDL3.dll). Noch kleiner ginge es, wenn auch die VM (`fire.Runtime.dll`,
~80-100 KB, im Bundle unkomprimiert) in den Payload wanderte und das Start-Stück nur aus einer winzigen Bootstrap-Assembly bestünde.

**Tests.** Suite-Block "Packer / Payload / Lader": Plan je Import, Payload-Rundlauf inkl. Marker-Bytes im Inhalt, Kompression, Prüfsummenfehler, nativer Lader (Linux), Ende-zu-Ende (packen, außerhalb des
Compiler-Ordners starten: `print`, `io`; Größenreihenfolge; nackte Runtime meldet fehlenden Payload).

**Nachtrag: `leave`/`terminate` enden wie das normale Programmende, der Aufrufer hält sofort.** `leave`/`terminate` rufen `ShutdownSelfNow()`: die eigene VM geht unabhängig vom Signalzähler
sofort in den Halt (auch wenn `terminate` schon von einem anderen Thread gesetzt war). Die Abwicklung (`CheckShutdownSignals` -> `UnwindForShutdown`) gibt den globalen Scope nicht mehr selbst frei, sondern
setzt `_shutdownReleasePending`; am Halt (`Run`, im Einzelschritt `StepInstruction`) läuft dann wie beim normalen Ende `ReleaseGlobalScopeAtEnd` - das Hauptprogramm wartet also auch nach `leave`/`terminate` auf alle
Fire-Threads, und jeder Fire-Thread zerstört vorher seine eigenen Objekte (nur nach einer unbehandelten Exception bleibt der globale Scope stehen). Der Stopp-Zustand (`_stopExecutionRequested`) wird dafür kurz aufgehoben
(`ReleaseGlobalScopeAfterStop`), weil `RunDestructor` in ihm nichts mehr ausführt. In einer VERSCHACHTELTEN Ausführung (Destruktor, Property, Operator, Callback) hält die VM sofort an und merkt sich
`_shutdownDeferred`; `FinishDeferredShutdown()` (am Halt in `Run`, in `StepInstruction`, in `CallLambdaEntry`) wickelt dann nach, sobald `_nestedDepth` wieder 0 ist.

## 31. Font-Rendering, Pixel-Text, Ereignis-Warteschlange und die UI-Bibliothek

**Font.** `IBitmapGlyphFont.GetGlyphRows(char)` (Bitmap-Zeilen zu je ≤ 8 Bit) + `GlyphMasks.Table` (256 Zeilen x 2 `Vector128<uint>`-Masken): `TerminalCanvas.DrawGlyph` schreibt eine 8 Pixel
breite Zeile mit zwei `ConditionalSelect` (opak) bzw. Lesen-Auswählen-Schreiben (transparent, leere Zeilen übersprungen) direkt in `Framebuffer.Pixels`; die Zelle muss vollständig im Target liegen, sonst der
pixelweise Weg. Spalten/Zeilen-Raster und Zellgröße sind einmal berechnet (`UpdateGrid`), `DrawGlyph` ist `AggressiveOptimization` (kein Warten auf das Hochstufen). Messung (80x30 Zeichen):
opak 370 -> 32 ns/Zeichen, transparent 270 -> 26 ns/Zeichen. Der Test-Block "Font-Rendering" vergleicht schnellen und allgemeinen Weg Pixel für Pixel (beide Schriftgrößen, opak/transparent, Positionen
über den Rand hinaus, Scrollen). `IntegratedGlyphFont`: Tabellen statisch, Zeichen > 255 als `?`.

**Pixel-Text.** `TerminalCanvas.DrawText/MeasureText`, `ConsoleManager.DrawText/GetCellWidth/GetCellHeight`, in der Bridge `Console.FillRect/DrawRect/DrawLine/DrawText/CellWidth/CellHeight` (rohe Farben).

**Ereignisse.** `WindowManager.EnableEventQueue/NextEvent/EncodeEvent` (siehe `docs/CONSOLE.md`), `Window.EnableEvents()/NextEvent()`. `SdlFramebufferRenderer` rechnet die Mausposition von Fenster- auf
Framebuffer-Koordinaten um (`SDL.GetWindowSize`) und startet die Texteingabe. `WindowManager(framebuffers, runner, rendererFactory)`.

**Warum Abfrage statt Callback.** `Session.CallLambda` -> `FireRuntime.CallCallback` kopiert für JEDES Ereignis alle globalen Objekte (`ObjectCopier.Take`, außerdem wird dabei auf deren Baum
Thread-Sharing aktiviert); ein Objekt mit Lambda-Feld oder mit einem Verweis auf ein fremdes Objekt wirft dabei `TakingViolationException` (der Callback schlägt fehl, SPEC 8.1.4 beschreibt das so). Ein Widget-Baum
mit `onClick`-Lambdas wäre davon betroffen, deshalb holt `UI.Root.Tick` die Ereignisse per Warteschlange im Hauptprogramm ab.

**UI.** `src/fire.UI.Bridge` (`UiBridge.PreludeSource`, `namespace UI`, siehe `docs/UI.md`); `NativeImports.Ui = "ui"`, `ImportedPreludes.WithDependencies("ui") = graphics + ui` (Linker, Editor-Diagnose und
`CreateProjectDirectiveRegistry` tragen alle Schlüssel ein), `PackagePlan` kennt `ui` ohne eigene DLL. Innerhalb eines Namespace sind statische Klassen nur vollqualifiziert erreichbar (`UI.Color.Rgb`), Felder
brauchen einen Typ (`Element hoverElement`), ein Lambda-Feld ruft man über eine lokale Variable (`var callback = this.onClick`, `callback()`), nicht als `this.onClick()`. Tests: Suite-Block "UI-Bibliothek".

## 32. Native Callbacks laufen verschachtelt auf der VM des Threads

`Session.CallLambda` (Runtime- und Compiler-Fassung) ruft `FireRuntime.RunCallback`. Auf dem Thread einer laufenden VM (`VM.CurrentThreadVm`, wird am Ende von `Run` wieder null; im Debugger zusätzlich `!IsHalted`) führt
`VM.CallLambdaInline` das Lambda verschachtelt aus (`RunNestedUntil`): Frame auf den Aufrufer-Zustand, neuer Scope mit dem globalen Scope als Parent, `this` = `OnTarget`. Die Globals sind die echten, nichts wird kopiert,
kein Thread-Sharing aktiviert. Für eine unbehandelte Exception im Callback gibt es eine `CallbackBoundary` (Frame-Tiefe, Scope, Handler-Untergrenze, Stackhöhe des Aufrufers): `ThrowException` sucht nur Handler über der
Untergrenze (die try/catch des Aufrufers sind für den Callback unsichtbar); findet es keinen, wickelt es bis zur Grenze ab (`UnwindTo`), stellt die Stackhöhe wieder her und merkt sich die Exception in `_callbackError`,
die `CallLambdaInline` an den Host zurückgibt (Text an `onUnhandled`, Programm läuft weiter). Eine rohe C#-Ausnahme (z.B. Performance-Modus ohne Prüfungen) stellt den Aufrufer-Zustand im `catch` wieder her und wird vom
Host gemeldet; `RunNestedUntil` gibt `_nestedDepth` jetzt auch dann wieder frei (`finally`). `leave`/`terminate` im Callback laufen über den bekannten Weg (`_shutdownDeferred`, `FinishDeferredShutdown`).
`WindowManager.Tick` kopiert die Callback-Liste je Ereignis (`ToList`), ein Callback darf Ereignisse an-/abmelden. Ohne laufende VM auf dem Thread bleibt es beim isolierten `FireRuntime.CallCallback` mit Kopie.
Tests: Suite-Block "Callbacks auf dem VM-Thread".

## 33. Globals und Fire-Threads: Broker, Sektionen, `sync globals`

Siehe docs/THREADING_DESIGN.md Abschnitt 7 für das Verhalten. Umsetzung:

**`GlobalsBroker`** (`fire.Runtime/GlobalsBroker.cs`): entsteht im Hauptprogramm beim ersten `fire`/`fire global` (`VM.EnsureOwnerBroker`), hält den globalen Scope des Hauptprogramms, den gemeinsamen
`ThreadShareLock` und eine FIFO-Warteschlange aus `SectionRequest` (ein Thread wartet auf `Granted`, das Hauptprogramm wartet danach auf `Done`) und `JobRequest` (`fire global`). `Drain()` ist
`sync globals`. `Close()` (am Ende von `VM.Run` des Besitzers) gewährt wartende und künftige Sektionen sofort. `FireRuntime.WaitForAllFireThreads(broker)` bedient die Warteschlange, solange Threads leben
(`WakeWaitingOwner` weckt es), und `FireRuntime.FireVmTaking` hängt den Thread per `VM.AttachToGlobals` an; ein `finally` ruft `ReleaseGlobalsSections` (Sicherheitsnetz gegen ein hängendes Hauptprogramm).

**Geteilter Bereich.** `ShareGlobals` (beim ersten `fire`) markiert alles, was die Globals erreicht: Objekte per `ObjectInstance.MarkGlobalsDomain` (setzt `InGlobalsDomain`, aktiviert den Baum-Lock, rekursiv über den Besitz),
Arrays per `ScriptArray.IsShared`, dazu die statischen Felder. `Scope.SharingLock` (globaler Scope) und `ObjectInstance.AddOwned` geben das an jedes später besessene Objekt weiter. Die Schnellpfade für Feldzugriffe
sind für gesperrte Objekte ohnehin aus (`ThreadLock != null`), die Array-Schnellpfade prüfen zusätzlich `IsShared`. Benchmarks vor/nach: kein messbarer Unterschied (837/852 ms vs. 848/839 ms, Performance).

**Thread-Seite.** Slots `< _sharedCount` der Globals gehen in `LoadGlobal`/`StoreGlobal`/`DeclareLocal`-Nachbarn über den Broker (`LoadSharedGlobal` unter dem Lock, `StoreSharedGlobal` in einer Sektion); der private `_globalScope`
enthält Platzhalter für diese Slots und dahinter die `taking`/`with`-Erfassungen. Sektionen sind in `_sectionDepth` gezählt (`EnterGlobalsSection`/`ExitGlobalsSection`); in einer schon erteilten Sektion laufen alle
Schreibzugriffe direkt (unter dem Lock). Eingebaut in: `OpSetFieldSlow` (Objekt im geteilten Bereich), `OpCallMethod` (Schnellpfad nur, wenn das Objekt nicht im geteilten Bereich liegt oder die Sektion gehalten wird;
sonst `CallGlobalsMethodInSection` über `CallMethodNested`), Array-Zugriffe (`TryGetSharedElement`/`TrySetSharedElement`), statische Felder, `IncDecIndex`. Ein Array, das ein Thread über die Globals erreicht, wird dabei
nachträglich als geteilt markiert (`MarkShared`). Das Hauptprogramm schreibt nach dem ersten `fire` Globals und Deklarationen unter dem Lock (`StoreOwnerGlobal`/`DeclareOwnerGlobal`).

**Syntax.** Opcodes `SyncGlobals`, `SectionEnter`, `SectionExit`, `PostGlobal` (am Ende von `OpCode`). Der Parser entzuckert `sync global { B }` zu `SectionEnter; try { B } finally { SectionExit }` (`TryStmt.IsSyncSection`
nur für die Fehlermeldung bei `break`/`continue`) und `fire global { B } taking x` zu `PostGlobalStmt(Lambda(x) => B, [x])`, das der Resolver wie ein Lambda (Globals sichtbar, Locals des Aufrufers nicht) und der Compiler als
Argumente + Lambda + `PostGlobal n` behandelt. `PostGlobal` kopiert Objekt-Argumente tief (`ObjectCloner.Clone`, Besitzer ein Halter-Scope, den das Hauptprogramm nach dem Lauf freigibt); ein Fehler im Auftrag geht wie
der eines Fire-Threads an die Warteschlange der Thread-Exceptions (`catch threads`). Der Resolver schützt die Globals in einem `fire`-Block nicht mehr (`ReadonlySlots` entfällt für die Schatten-Einträge).

Tests: Suite-Block "Globals und Fire-Threads" (14 Fälle in drei Modi: direktes Lesen, atomare Methoden und Blöcke, Locals im Block, `fire global` mit Wert/Objekt, Programmende ohne `sync globals`, Exception im Block, Arrays,
statische Felder, verschachtelte Threads).


## 34. Automatisches Abarbeiten, `#nosync`, Host-Callbacks in der Warteschlange

Siehe docs/THREADING_DESIGN.md Abschnitt 7. Umsetzung (alles in `VM.cs`, Besitzer = Hauptprogramm-VM):

- `SyncGlobalsNow()` (= `sync globals`) liefert `DrainInbound()` + `GlobalsBroker.Drain()`.
- `PostCallback(lambda, args, onUnhandled)` (threadsicher, von jedem Thread) reiht ein `InboundCallback` in eine `ConcurrentQueue` und hebt das Signal; es liefert false, wenn die VM nicht läuft (`_acceptingCallbacks`
  ist nur während `Run()`/Einzelschritt gesetzt). `FireRuntime.RunCallback(..., owner)` nimmt diesen Weg für Threads ohne VM und fällt sonst auf die isolierte Kopie zurück; beide `RuntimeSession.CallLambda` übergeben
  die Haupt-VM.
- `GlobalsBroker.EnterSection`/`PostJob` rufen `VM.RaiseSignal()` (globale Epoche), damit der Besitzer es an seinem nächsten sicheren Punkt bemerkt.
- `AutoSyncNow()` (nicht in verschachtelter Ausführung, nur bei `_autoSync`) wird von `PollSignalsAfterOp` und `PollSignalsSlow` gerufen, also an denselben Punkten wie die Shutdown-Signale, und im Einzelschritt
  nach jeder Instruktion (`StepInstruction`). Kein Mehraufwand im Normalfall: dieselbe Epochen-Prüfung wie bisher (Benchmark vor/nach 852/866 ms vs. 854/855 ms, Performance).
- `#nosync` = `NoSyncDirective` (AST) -> der Compiler setzt am Programmanfang `SetAutoSync 0` -> `_autoSync = false`.

Tests: Suite-Blöcke "Callbacks auf dem VM-Thread" (Host-Thread-Callback automatisch/`#nosync`/Exception) und "Globals und Fire-Threads" (Standard-Automatik, `fire global` ohne `sync globals`, `#nosync`).

## 35. `break`/`continue` aus `try`/`catch` heraus

Löst die Einschränkung aus Abschnitt 17 auf (Resolver: `_tryDepth` zählt nur noch umgebende `finally`-Blöcke, je Schleife neu). Der Compiler führt einen `_tryStack` (`TryCompileContext`: Phase Try/Catch, Scope-Tiefe außerhalb,
Anzahl offener Schleifen beim Betreten); die von der VM erzeugte Catch-Scope zählt in `_currentScopeDepth` mit. `CompileBreakOrContinue` geht die `try`s der aktuellen Schleife von innen nach außen durch: Scopes bis zur Tiefe des
`try` schließen, im Try-Teil `UnregisterHandler`, im Catch-Teil `ClearPendingResume` + `ExitScope` (wie am normalen catch-Ende), dann das `finally` **inline** (Handler sind schon abgemeldet, ein Fehler darin propagiert normal) -
zuletzt die restlichen Scopes bis zum Schleifenkörper und der Sprung. Aus dem `finally` selbst bleibt `break`/`continue` ein Resolver-Fehler (das `finally` wird zusätzlich als eigene Funktion für den Ausnahmepfad
kompiliert, dort gibt es die Schleife nicht). Das gilt auch für `sync global { }` (intern `try`/`finally`). Tests: Suite-Block "Globals und Fire-Threads" (8 Fälle: finally, Locals, Handler-Abmeldung, catch, verschachtelt,
innere Schleife, Sektion) und der Resolver-Test "break in finally".

## 36. Lambda-Captures, Kurzsyntax, `return` im `foreach`, die Abfrage-Bibliothek

**Captures (SPEC 4.2.1).** Resolver (`DefineCaptures`): vor dem Auflösen des Körpers sammelt `AstNames.Collect` per Reflection alle Bezeichner des Körpers (auch aus verschachtelten Lambdas, Interpolationen, `taking`-Quellen -
neue Syntax braucht keine Pflege). Jeder, der im umschließenden Code ein **nicht-globaler** Name ist und kein Parameter der Lambda, wird als Slot direkt hinter den Parametern definiert (Slot-Nummern müssen vor dem Körper
feststehen) und in `ResolverScope.CaptureNames` vermerkt; ein synthetischer `IdentifierExpr` je Capture wird im UMSCHLIESSENDEN Scope aufgelöst und als `ResolvedRef.LambdaCaptures` unter dem `LambdaExpr` in `_refs`
hinterlegt (so braucht der Compiler keinen neuen Kanal). Deklariert der Körper selbst einen als Capture angelegten Namen, verschiebt `Define` den Capture-Slot unter einen unzugänglichen Schlüssel (Slot-Zählung bleibt
lückenlos); `ResolveAssignTarget` lehnt Zuweisungen an einen Capture ab. `LambdaExpr.AutoCapture = false` (nur `fire global`) schaltet das ab.
Compiler: lädt die Capture-Werte im umschließenden Scope und emittiert `MakeLambdaCapturing u16 proto, u8 hasOn, u8 count` (Stack: c0..cn-1, [onTarget]); ohne Captures bleibt es beim alten `MakeLambda`. VM: `LambdaValue.Captures`
(`Value[]`); alle vier Aufrufwege legen sie hinter den Argumenten in den neuen Scope (`EnterCall(..., captures)` im Schnellpfad, `OpCallSlow`, `CallLambdaInline`, `CallLambdaEntry`) - `WithOnTarget` behält sie.
Benchmarks vor/nach: kein Unterschied.

**Kurzsyntax.** Parser: `Identifier =>` und `( ... ) =>` (Lookahead bis zur passenden `)`, `IsParenLambda`) laufen über `ParseLambdaTail`, wie die `func`-Form.

**`return` im `foreach`.** `foreach` hält seinen Enumerator auf dem Operanden-Stack; ein `return` darin ließ ihn dort liegen und verschob Operanden des Aufrufers (`10 + f() + f()` mit `return` im `foreach` von `f` warf „Typ Class
ist nicht numerisch“). Der Compiler emittiert jetzt vor dem `Return` je umgebendem `foreach` `Swap; Pop` (`LoopCompileContext.IsForeach`). Offen: dieselbe Leiche entsteht, wenn eine Exception aus einem `foreach` heraus von
außerhalb gefangen wird (nur sichtbar, wenn dabei Operanden pending sind).

**`#import "linq"`** (`fire.Standard.LinqPrelude`, reiner fire-Quelltext wie die UI-Bibliothek, keine DLL): `Linq.From(quelle)`/`Linq.Range`/`Linq.Repeat` liefern eine `Query` (eine Fabrik-Lambda, die je Durchlauf einen frischen
Enumerator liefert - nutzt selbst Captures); träge Operatoren `Where/Select/SelectMany/Take/Skip/TakeWhile/SkipWhile/Concat/Zip` als Enumerator-Klassen, eifrig `OrderBy/OrderByDescending` (stabiler Mergesort)/`Reverse/Distinct`,
Abschluss `ToList/ToArray/First/FirstOrDefault/Last/ElementAt/Any/All/Count/Sum/Min/Max/Average/Aggregate/Contains/ForEach/Join` (Überladung nach Parameteranzahl). `List` bekommt dieselben Operatoren über `class extends List`
(aus einer Tabelle erzeugt). Arrays: `Linq.From(array).…` (Arrays sind nicht erweiterbar). Leere Folgen: `LinqEmptyException`.

Tests: Suite-Block "Lambda-Captures und LINQ" (Kurzsyntax, Capture-Semantik, Verdecken, Globals, Zuweisungsfehler, Objekte, `return` im `foreach`, LINQ in vier Gruppen) und der angepasste Resolver-Test.

## 37. Reflection und Selektoren

**Metadaten.** `ClassMeta`/`MemberMeta` (`fire/Bytecode/ClassMeta.cs`) hängen als `RuntimeClass.Meta` an jeder Klasse und halten, was die Laufzeit sonst verliert: deklarierte Typnamen (Feld, Property, Rückgabe, Parameter), Parameternamen,
`readonly`, Property-Form (`CanRead`/`CanWrite`), `IsStatic`, Zugriff, Einheit, Basisnamen. Der Compiler (`BuildClassMeta`) schreibt sie nur mit, wenn die native Funktion `__refl_members` registriert ist (`#import "reflection"`,
`Compiler.Reflection`) - ohne den Import bleibt alles wie vorher. MemoryPack serialisiert sie mit (gepackte Programme). Dieselbe Bedingung setzt `RuntimeClass.IsReflectionHelper` für `Reflect`, `Type`, `Member`, `Selector`.

**Aufrufer für Zugriffsprüfungen.** `IsMemberAccessAllowed` nimmt `_currentChunk.OwnerClass`; ist das eine Reflection-Klasse, läuft `ReflectionCallerClass()` die `_frames` von oben nach unten ab und liefert die erste Klasse, die keine
ist (der Code, der `Reflect.Get` & Co. aufgerufen hat, auch über `Member.Get` → `Reflect.Get`). Nur im Nicht-Public-Pfad, kein Mehraufwand sonst.

**Zugriff nach Namen.** Die Langsam-Pfade von `GetField`/`SetField` sind in `GetFieldSlowCore(name, site)`/`SetFieldSlow(name, site)` herausgelöst (liefern `true`, wenn das Ergebnis auf dem Stack liegt, `false` bei einer in einen Handler umgeleiteten Exception;
`site < 0` = kein Inline-Cache): die Opcodes und `VM.ReflectGet/ReflectSet` (`VM.Reflection.cs`, `partial class VM`) teilen sich damit Zugriffsprüfung, Einheiten, Property-Accessoren und die Sektionsregeln der Globals. `ReflectCall` ruft über
`CallMethodNested` (bzw. `CallGlobalsMethodInSection`), `ReflectNew` über `ConstructNested` (mit Zugriffsprüfung am Konstruktor).

**Native Funktionen mit Umleitung.** Löst eine native Funktion eine Exception aus (`ReflectFail` baut eine `ReflectionException` und ruft `ThrowException`), liegt die Ausführung schon im Handler: `_nativeRedirected` sagt
`CallNativeGuarded`, dass kein Ergebnis zu pushen ist (wie bei `NativeIndexOutOfRangeException`). Dasselbe gilt, wenn ein Getter/eine Methode in einen äußeren Handler umgeleitet wird.

**Selektoren.** `LambdaSignature.IsSelector` (Parser: `lambda property<T>`), `FunctionProto.SelectorPath` (`TrySelectorPath`: ein Parameter, Körper nur `ReturnStmt` einer `MemberExpr`-Kette auf ihm). `EmitLambdaParamChecks` emittiert für einen
Selector-Parameter am Funktionsanfang `LoadLocal; CallStaticMethod Reflect.SelectorOf; StoreLocal; Pop` (der Parameter wird ersetzt); `Reflect.SelectorOf` baut über `__refl_selector_path` ein `Selector`-Objekt. Der Resolver verlangt die Klasse
`Reflect` (sonst "braucht ... #import \"reflection\"") und kennt `T`.

**Nebenbei behoben.** `HandlerTemplate.Catches` war eine Nur-Lese-Eigenschaft und wurde von MemoryPack nicht mitgeschrieben: in einem **gepackten** Programm gab es dadurch keine einzige `catch`-Klausel (eine Exception beendete das Programm
still). Jetzt `{ get; set; }`; der Test "Gepacktes Programm ..." sichert es. Außerdem druckte `print` der gepackten Runtime nur Strings (`AsString`), jetzt wie die Befehlszeile jeden Wert.

Tests: Suite-Block "Reflection" (Typ-Beschreibung, Find/Has/Interfaces, Get/Set/Call/New, Zugriffsregeln, Fehler und umgeleitete Exceptions, Selektoren inkl. Fehlerfälle, gepackter Rundlauf).
