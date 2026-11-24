# Entscheidungen

Hier stehen Fragen zur Sprache und zur Laufzeit, die eine Entscheidung brauchen oder bekommen haben. Jeder Eintrag nennt den Stand
(**offen**, **entschieden**), die Frage, was dafür und dagegen spricht und - sobald entschieden - was gilt und wo es umgesetzt ist.
Neue Einträge kommen oben in ihren Abschnitt. Was nur noch Arbeit ist, steht in [ROADMAP.md](ROADMAP.md).

## Offen

### D2. `try delete x` als Sprachmittel

Vorschlag des Benutzers: `try delete x` versucht, den Inhalt des Bezeichners zu löschen; ist er kein Objekt oder undefined, gibt es `false`,
sonst `true`. Für die Container wurde es nicht gebraucht (`x is from this` genügt, D3). Offen ist, ob es trotzdem kommt - und ob es nur löscht,
was dem Aufrufer gehört, oder jedes Objekt wie `delete`.

## Entschieden

### D16. `replace this.f = v`: der alte Wert geht, wenn er dem Objekt gehört (D1)

Eine Zuweisung an ein Feld zerstört den alten Wert nicht (D1): sonst müsste fire bei jeder Zuweisung ausschließen, dass noch jemand auf den alten
Wert oder seine Kinder zeigt, und Zerstören soll nie stillschweigend geschehen. Entwurf des Benutzers: das neue Schlüsselwort **`replace`** sagt es
ausdrücklich.

* **Form:** `replace <Feld> = <Wert>` als Anweisung. Ziel: ein Feld (`this.f`, `f` in einer Klasse, `obj.f`, eine Property) oder ein statisches
  Feld (`Class.f`, Besitzer ist das Programm, der globale Scope). Nicht für Variablen (`E4080`): ihre Werte gehören ihrem Scope und gehen mit ihm;
  wer früher aufräumen will, schreibt `delete x` und dann die Zuweisung. Nicht für Elemente, Zeiger, `+=` (Parserfehler `E3165`/`E3166`).
* **Was geht:** der alte Wert, wenn er ein Objekt, Array, Puffer oder Lambda ist, das der Halter **direkt besitzt** (`alt is from obj`, beim
  statischen Feld `is from global`) und das Feld ihn nicht wieder hält (`replace this.f = this.f`). Zerstört wie mit `delete` (Destruktor, was es
  besitzt, geht mit - ein `Dictionary` samt Inhalt).
* **Was bleibt:** ein Wert, der dem Halter nicht gehört - eine Referenz auf Fremdes, ein Wert, den der neue Ausdruck mit `take` herausgenommen
  hat -, `undefined`, Werttypen: nur überschrieben, **kein Fehler**. Begründung: `delete` zerstört alles, was man ihm gibt (es prüft keinen
  Besitz); `replace` ist dagegen das Aufräumen des Objekts in seinen eigenen Feldern. Ein Feld hält mal Eigenes, mal eine Referenz (ein Elternteil,
  ein geteilter Cache) - ein Fehler bei der Referenz machte `replace` für solche Felder unbrauchbar, und Zerstören von Fremdem wäre genau das
  stille Zerstören, das D1 vermeiden will.
* **Reihenfolge:** alten Wert lesen, neuen Wert auswerten, speichern (mit dem Besitz von `=`: Frisches gehört dem Objekt, `take` verschiebt),
  **dann** den alten zerstören. Abweichend vom ersten Vorschlag (erst löschen, dann speichern): so zeigt das Feld nie auf einen zerstörten Wert -
  der Destruktor des alten sieht schon den neuen, eine Ausnahme im Destruktor lässt das Feld mit dem neuen Wert zurück, und die Debug-Warnung für
  hängende Referenzen (DanglingWatch) sieht im Feld keinen toten Wert. `replace this.f = Make(this.f)` liest den alten Wert, weil der neue zuerst
  ausgewertet wird; der Besitz wird erst nach der Auswertung geprüft, also bleibt ein mit `take this.f` herausgenommener Wert am Leben.
* **Andere Referenzen** auf den alten Wert prüft `replace` nicht (wie `delete`): sie bekommen die DestroyedException.
* `replace` ist ein Schlüsselwort (kein Name mehr; der Parameter `replace` in circuits.ui heißt jetzt `replacement`), im Editor hervorgehoben.
* Umgesetzt: Lexer/TokenType.Replace, Parser.ParseReplace (AssignExpr.Replace), Resolver (E4080), Compiler.CompileReplace, OpCode.ReplaceOld
  (VM.cs, CppGenerator.cs: `isFrom`/`isFromGlobal`, `sameRef`, `deleteValue`). Tests: SyntaxTests, TypeTests, native Fälle 4405-4407.
  Doku: 13-ownership "replace", 01-basics "Keywords", SPEC 2.5.

### D1. Zerstört eine Feldzuweisung den alten Wert, wenn er dem Objekt gehört?

**Entschieden (D16): nein - `=` zerstört nie; wer den alten Wert loswerden will, schreibt `replace`.**

`this.d = new Dictionary<string, Node>()` ein zweites Mal: das alte Wörterbuch samt Inhalt lebt weiter, bis der Besitzer (das Objekt) endet.
Das gilt für jedes Objekt in einem Feld, nicht nur für Wörterbücher. Gefunden in Raser: ein Nachbau wuchs in sechs Runden von 142 auf 846 MB.

* Dafür: kein stilles Wachsen, wenn ein Feld immer wieder neu belegt wird.
* Dagegen (Tendenz des Benutzers: **nicht automatisch zerstören**): auf den alten Wert kann noch jemand zeigen - ein Kind oder Kindeskind des
  Objekts, ein Lambda, das ihn mitgenommen hat, ein Thread, dem er mit `handover` übergeben wurde. Das müsste vor dem Zerstören ausgeschlossen
  werden; sonst läuft diese Referenz später in eine DestroyedException. Die Container (D3) zerstören nur als Komfort, und nur, was ihnen
  ausdrücklich mit Besitz übergeben wurde.
* Heute: `delete this.d` vor der neuen Zuweisung. Möglich wäre stattdessen ein Hinweis des Typprüfers, wenn ein Feld, das ein eigenes Objekt hält,
  ohne `delete` neu belegt wird.

### D15. `x!` zu einem String bleibt ein Fehler

`a! + s` mit einem String-Anker, `5!string` und `x!` neben einem bool werfen weiter (`Cannot coerce type Int to String: '!' does not make the text of
a value (write "" + x or x.ToString()).`), und wo die Typen bekannt sind, ist es zur Übersetzungszeit `TC013` (auch lax, auch für `5!string`).
Entscheidung des Benutzers: `!` macht keinen Text; der Text eines Werts ist `"" + x` oder `x.ToString()` (D14). SPEC 3.1, 02-types.md
"Converting", 05-units.md.

### D14. Eine Deklaration `string` hält einen String: was hineingeht, wird sein Text (`x.ToString()`)

Gefunden bei `string k = Color.Blue`: `k is of string` war False. Das galt für jeden Wert und jede Stelle - `string j = 5`, ein `string`-Feld,
-Parameter, eine `string`-Rückgabe hielten die Zahl 5 (`j.Length` war `Field access on a value of type Int which is not an object.`), weil die
Laufzeit eine `string`-Deklaration nicht umwandelte und nicht prüfte (nur `defined string` und `string s[]`, dort mit einer ArgumentException).
Entscheidung des Benutzers: **umwandeln, wie die Doku sagt.**

* Ein Wert, der dorthin geht, wo `string` deklariert ist - Variable, Feld, statisches Feld, Parameter, Rückgabe, Property, Element von `string a[]`,
  ein `T` mit dem Typargument `string` (`List<string>`) - wird `x.ToString()`, in VM und nativ, Debug und Release. `undefined` bleibt undefined
  (`defined string`: die ArgumentException wie bisher; erst wird umgewandelt, dann undefined geprüft).
* **Jede Klasse hat `ToString()`** und darf es überschreiben; ohne eigenes gibt es `<object Name>` (auch ein Record). Jeder Wert außer undefined
  hat `ToString()` - vorher fehlte es für int, float, bool, Arrays, Objekte ohne eigenes (`MissingMemberException`; der Typprüfer meldete TC008).
  `x.ToString()` und `"" + x` geben denselben Text: Zahl mit Einheit (`5m`), `True`/`False`, das Zeichen, der Name des Enum-Members, `<array>`,
  `<object Name>`. Ausnahme: ein Byte-Puffer - `buffer.ToString()` dekodiert seine Bytes (so war es schon), `"" + buffer` ist `<buffer n bytes>`;
  eine `string`-Deklaration nimmt den Text von `ToString()` (offen, ob `"" + buffer` folgen soll).
* Ein Objekt mit eigenem `ToString()`: es läuft an der Stelle der Deklaration (VM: `StringifyForText`, verschachtelt; nativ: `toStringValue` ->
  `userToString`). Wirft es, läuft die Ausnahme von dort weiter, die Deklaration behält, was sie hatte. Ein `ToString()`, das selbst in
  `string`-Deklarationen speichert, geht (getestet).
* **Kosten:** wo der Typprüfer weiß, dass ein String hineingeht, wird nichts übersetzt (lokale Variablen, globale, Rückgaben: GuardTop nur für
  Werte, die nicht sicher ein String sind). Ein `string`-Parameter, dem jeder Aufruf des Programms einen String gibt, bekommt keine Prüfung im
  Prolog (TypeChecker: DerivedStrings, hergeleitet wie `defined`, mit denselben Bedingungen - nicht für Pakete, Module, Reflection, Namen in
  Strings). Sonst bleibt eine Prüfung der Art (`kind == String || kind == Undefined`, ein Vergleich) am Parameter und am Feld, wie bei
  `int`-Feldern und -Parametern; umgewandelt wird nur, was kein String ist. Nativ bleibt ein `string`-Parameter eine gezählte Variable (wie zuvor).
* **Typprüfer:** die Deklaration hält danach einen String (der statische Typ ist `string`). Ein Wert, der sicher kein String ist (Zahl, bool, Enum,
  Objekt), in eine `string`-Deklaration ist im **strict**-Modus die Warnung **`TC034`** (nie ein Fehler; lax nichts). Begründung: die Umwandlung
  ist Regel der Sprache und vorhandener Code gibt Zahlen in `string`-Parameter; TC001/TC002/TC003 wären im strict-Modus Fehler und würden ihn
  brechen. `string j = 5; j == 5` ist jetzt False.
* **Nachtrag (24.11.2026, vom Benutzer beauftragt):** eine Zahl, ein `char` oder ein `bool` an einem **Parameter** `string` (Argument, Indexer,
  Operator, Setter, typisiertes Lambda, Default-Wert) ist die Warnung **`TC037`**, in lax und strict (dort statt `TC034`). Ein Name oder Schlüssel
  als Zahl ist dort meist ein Versehen (`row[0]` am früheren `operator[](string name)` von `Data.Row`). Zuweisungen an Variablen, Felder und Elemente
  bleiben ohne Warnung in lax (die Deklaration `string` steht für die Umwandlung; strict: `TC034`). Die Umwandlung selbst bleibt.
* Umgesetzt: DeclaredKind.StringKind / OfStore (die Art einer `string`-Deklaration, nie mit NonNullBit), VM.GuardDeclared, VM.DeclaredText,
  ArrayElem.IsStringText, DeclaredType.WantsString, Compiler.EmitLocalGuard, TypeChecker.GuardLocalValue/GuardValue/NoteCall (DerivedStrings),
  fire_rt.h guardStr/guardStrSlot, CppGenerator.GuardStmt/GuardKindCall/ClassGuard. Tests: TypeTests (VM Debug/Release, TC034), nativer Fall 1656.

### D13. `x!`, `x:`, `x:!` haben einen statischen Typ, die Umwandlung steht zur Übersetzungszeit fest

Vorher kannte nur der Compiler die Ankerregel (SPEC 3.2) und entschied zur Laufzeit; der Typprüfer hielt `b + a:!` für unbekannt (`var c = b + a:!`
war TC019), ein bloßes `!` wirkte nur neben einem Anker (`int i = 2.6!` war die ArgumentException), und `F(a:!)` war ein Syntaxfehler.
Entscheidung des Benutzers:

* Der Typprüfer spiegelt die Ankerregel (TypeChecker.InferOperands): genau ein Operand fragt - er bekommt Art und Einheit des Ankers; beide fragen -
  unitless, die Typen bleiben; ohne Kontext bleibt der Typ, die Einheit wird unitless.
* Wo Art und Einheit des Ankers statisch bekannt sind, übersetzt der Compiler die feste Umwandlung (ResolveResult.FixedCoercions); sonst bleibt der
  Laufzeitweg. Die Ergebnisse sind dieselben (die Tests übersetzen jeden Fall auf beide Arten, VM und nativ, Debug und Release).
* Der Kontext einer Deklaration zählt: `int i = 2.6!` (3, gerundet wie `!int`), `float:km d = a:!`, ein typisierter Parameter, ein typisiertes
  Feld, eine typisierte Rückgabe. `string`, `bool`, Klassen und `any` sind kein Kontext.
* Der statische Typ ist nie genauer als die Laufzeit: neben `any` ist das Ergebnis `any`; eine ganze Zahl, die `:` aus ihrer Dimension nimmt
  (`5mm:unitless` ist 0.005, `2km:unitless` ist 2000), ist "int oder float". Dabei fiel auf, dass `int c = a:unitless` bisher einen float in der
  `int`-Variablen hielt (der Prüfer hielt das Ergebnis für int und ließ die Laufzeitprüfung weg) - jetzt die ArgumentException
  `Variable 'c' is an int, not Float.`
* `:` zum Anker scheitert nie (eine andere Dimension rechnet in die Basiseinheit, `500m: + 2kg` ist `502kg`), also gibt es dafür keinen
  Übersetzungsfehler; eine ganze Zahl in die gröbere Einheit des Ankers ist TC007. Was immer scheitert - `x!` neben einem String oder bool - ist TC013.
* Geändert im Fehlerfall: `a! + u` mit undefiniertem `u` meldet jetzt den undefinierten Operanden der Rechnung (`Type Undefined is not numeric.`)
  statt des undefinierten Ankers; `a! == u` meldet weiter den Anker.
* Parser: `F(a:)`, `F(a:!)`, `F(a:, b)`, `F(a:!int)`, `F(a:! + b)` sind Umwandlungen; `F(size: 2)`, `F(flag: !x)` und `F(a: -b)` bleiben benannte
  Argumente (nach dem Doppelpunkt steht dort ein Wert). Entscheidung des Benutzers zu `F(a: -b)`: benannt ist `name:` nur **am Anfang eines
  Arguments** (`F(a: -b)`, `F(x, a: - b)`); sonst gilt das übliche Parsen - `F(1 + a: - b)`, `F(g(a:) - b)`, `F((a: - b))` sind Ausdrücke mit der
  Umwandlung `a:` (TypeTests "Parser: nur am Anfang eines Arguments", 03-operators.md, 05-units.md).

### D12. `defined` wird im Debug-Build hergeleitet wie im Release-Build

Bisher leitete ein Debug-Build nichts her (die Begründung: der Debugger ruft Methoden mit dem auf, was man ihm gibt). Dadurch hatte
`[InferDefined]` dort keine Wirkung, `Reflect.Set(c, "count", undefined)` speicherte im Debug-Build das undefined und warf im Release-Build,
und ein Feld ohne Initialisierer begann einmal bei undefined und einmal bei 0. Entscheidung des Benutzers: beim Entwickeln sieht man dasselbe
Verhalten wie im Release-Build - dieselben Prüfungen, Meldungen und Anfangswerte, in VM und nativ. Der Debugger schreibt mit `set` nur Werte der
Art, die die Variable hält (nie undefined). Ausgenommen bleiben Plugins und Module. Linker.CompileAndLink (TypeChecker.DeriveDefinedOverride).

### D11. Ein Methodenname ist statisch oder für Instanzen, nicht beides

Gefunden von einem Agenten: `static bool Inside(R a, R b)` neben `bool Inside(int x, int y)` in einer Klasse. Als Programm meldete der Compiler
`E4057` für `R.Inside(a, a)`, obwohl die statische Überladung passte; im Paket `ui` kam beim Übersetzen nichts und erst zur Laufzeit eine
ArgumentException ("not a static method"). Entscheidung des Benutzers: verboten, schon bei der Deklaration - `E4076` im Resolver, für eine Klasse
und über ihre Basisklassen, auch in Paketen (das Paket wird dann nicht gebaut). SPEC 5.4.3, Resolver.ValidateStaticAndInstance.
**Die Regel gilt für jeden Member mit Namen** (der Benutzer: "statisch und Instanz desselben Namens dürfen nicht gemischt werden"): Methoden, Felder
und Properties, auch über Kreuz - `static int n` neben `int n` (übersetzte bisher, `A.n` scheiterte zur Laufzeit mit "has no static field"), eine
statische Property neben einer Instanz-Property (bisher `E5000 Internal error: method 'get_Size' ... registered twice`) oder neben einer
Instanz-Methode, eine statische Methode neben einem Instanz-Feld - in einer Klasse und über die Basisklassen. Kein Standardpaket mischte sie.
Dazu `E4077`: ein Feld oder eine Property ist in einer Klasse einmal deklariert (zwei Properties, zwei Felder oder Feld und Property desselben
Namens; bisher E5000 bzw. still die letzte Deklaration). Erlaubt bleiben ein Feld der Basisklasse, das die abgeleitete noch einmal deklariert, und
eine Methode neben einem Feld oder einer Property desselben Namens (beide statisch oder beide für Instanzen).

### D9. `print` eines zerstörten Objekts ist die DestroyedException

`print(x)`, `"" + x` und `$"{x}"` mit einem zerstörten Objekt werfen die DestroyedException, wie jede andere Verwendung - in VM und nativ, mit und
ohne `ToString()`. Vorher schrieb die VM für eine Klasse ohne `ToString()` `<object P>`, und nativ las `print` freigegebenen Speicher (dann
`<object>`). Ein lebendes Objekt ohne `ToString()` zeigt weiter `<object P>`. VM.StringifyForText, fire_rt.h pieceOf.

### D10. Eine Deklaration `Bag<int> b` prüft die Typargumente

Ein Parameter, ein Feld, eine Rückgabe oder eine Variable, die mit einer generischen Klasse und Typargumenten deklariert ist, nimmt nur ein Objekt
mit diesen Typargumenten: `Parameter 'b' is a Bag<int>, not Bag<string>.` (eine ArgumentException, in VM und nativ gleich). Vorher prüfte die
Deklaration nur die Klasse; die VM rechnete mit dem falschen Wert weiter, nativ kam die Meldung erst am Ergebnis. Durch gehen weiterhin: ein Objekt
ohne bekannte Typargumente (`new Bag()`), eine abgeleitete Klasse, die der Basisklasse diese Typargumente gibt (`class IntBag : Bag<int>`), und
Stellen, an denen das Typargument der Deklaration nicht bekannt ist (`Bag<any>`). In generischem Code zählt das Typargument des Objekts
(`Bag<T> other` in `Bag<int>`: ein `Bag<int>`).
**Kovarianz** (Entscheidung des Benutzers: was von Animal abstammt, ist als Animal zuweisbar und nutzbar): ein `List<Dog>` geht dorthin, wo
`List<Animal>` oder `List<INamed>` deklariert ist, ein Dog-Array dorthin, wo `Animal a[]` steht. Das Objekt bzw. das Array **behält seinen Typ** und
prüft weiter, was hineinkommt: `animals.Add(cat)` auf einem `List<Dog>` und `all[0] = cat` auf einem Dog-Array sind die ArgumentException
(`Parameter 'value' is a T (Dog), not Cat.`). Die Gegenrichtung (`List<Animal>` in `List<Dog>`) und fremde Klassen gehen nicht. Das ersetzt die
frühere Regel, dass ein Array einer abgeleiteten Klasse nicht in die Deklaration der Basisklasse geht.
**`new Dog[n]` ist von Anfang an ein Dog-Array** (Klasse, Interface, Record, `string`, Enum; Compiler: TypeArray nach NewArray): vorher war es
untypisiert, bis eine Deklaration es typisierte - `any d = new Dog[2]; Cat c[] = d` ging durch und machte es zum Cat-Array. Ein Zahlen-Array
(`new int[n]`) bleibt offen, bis eine Deklaration ihm Breite und Einheit gibt - **es merkt sich aber seine Art** (int, float, bool, char;
ScriptArray.OpenKind, nativ Arr.open, im Bytecode TypeArray mit ArrayElem.OpenFlag nach NewArray): `new int[n]` geht dorthin, wo ints jeder
Breite und Einheit deklariert sind (`int[8] a[]`, `int:cm a[]`), und in eine float-Deklaration (eine ganze Zahl geht in einen float, wie bei
Werten und Literalen; die Doku verbietet `float f[] = new int[2]` nicht), nicht in eine Klasse, einen String, bool oder char;
`new float[n]` nicht in `int a[]` (`An array of int cannot be stored where an array of Cat is declared.`, TC001 wo beide Typen bekannt sind).
Vorher ging `any d = new int[2]; Cat c[] = d` durch und machte das Array zum Cat-Array. Was in ein noch offenes Array gespeichert wird, ist
weiter ungeprüft; ein Literal (`[1, 2]`) merkt sich nichts. Nativ kostet das Merkmal ein Byte im Array-Kopf (auf 64 Bit im Füllbereich, auf
32-Bit-Zielen 8 Byte je Array).
**Ein Array als Rückgabetyp wird geprüft** wie die Zuweisung an eine Array-Variable dieses Typs (Compiler: TypeArray vor Return): `Cat[] F(any a)
{ return a }` gab ein Dog-Array zurück, `int[] F(any a)` sogar einen String. Der Typprüfer meldet ein Array der Basisklasse in der Deklaration
der abgeleiteten (`Dog d[] = animals`) als TC001 (ein einzelnes Objekt darf weiter die abgeleitete Klasse sein).
DeclaredType.WithTypeArgs / TypeArgsFit, VM.ClassArrayIsOf, nativ ClassGuard mit `guardTypeArgsFailed`, `TArg.isA` / `ArrClass.isA` mit `typeNameIn`.

### D8. Eine Deklaration `Color c` prüft zur Laufzeit

Ein Parameter, ein Feld, eine Property, eine Rückgabe oder eine Variable, die mit einem Enum deklariert ist, nimmt einen Wert dieses Enums oder
undefined - alles andere ist eine ArgumentException (`Parameter 'c' is a Color, not Int.`), wie bei `Dog d` für Klassen. Vorher prüfte das nur
der Typprüfer (und zur Laufzeit `defined Color c`, `Color cs[]` und ein `T` mit dem Typargument `Color`). Eine Zahl geht mit einem Cast hinein
(`5!Color`); ein Enum-Wert geht weiter ohne Cast in ein `int` - und ist dort seine Zahl (`int n = Color.Blue`: `print(n)` gibt `2`, `n is of int`
stimmt; vorher blieb der Wert in einer `int`-Deklaration ohne `defined` ein Enum-Wert, in VM und nativ, und in `int a[]` nur in der VM;
DeclaredKind.Accept, fire_rt.h guardKind). Entscheidung des Benutzers. Compiler.DeclaredTypeOf, TypeChecker.GuardLocalValue /
GuardValue (SPEC 8.10b).

### D4. Ein Enum als Typargument ist zur Laufzeit bekannt

`Bag<Color>`: bisher ließ der Compiler das Typargument eines Enums zur Laufzeit leer - mit der Begründung, dass auch eine einzelne Deklaration
`Color c` zur Laufzeit nimmt, was kommt (der Typprüfer prüft sie). Dadurch typisierte `T items[]` für ein Enum nichts, `T is of Color` war falsch
und `T item` nahm jeden Wert. Jetzt (Wunsch des Benutzers) trägt das Typargument den vollen Namen des Enums: `T item` nimmt nur Werte dieses
Enums oder undefined, `T items[]`/`new T[n]` ist ein Array davon (wie `Color cs[]`), `T is of Color` stimmt; `List<Color>`,
`Dictionary<Color, V>`, `HashSet<Color>` prüfen so ihre Elemente. Compiler.TypeArgText, DeclaredType.ArrayOfTypeArg (ROADMAP 19).
Die Folgefrage ist D8.

### D3. Container zerstören, was ihnen gehört, wenn es herausfällt

`List`, `Dictionary` und `HashSet` zerstören bei `Remove`, `RemoveAt`, Überschreiben und `Clear` jedes Element, das ihnen gehört
(`x is from this`): im Aufruf erzeugt, mit `take` oder `x.TakeTo(dict)` übergeben, oder mit `Owns.Values`/`Owns.Keys` übernommen. Ein fremdes
Objekt (nur referenziert) fällt nur heraus; `Take`/`TakeAt` geben ein eigenes dem Aufrufer. Vorher galt das nur mit `Owns.Values`/`Owns.Keys`.
Umgesetzt im Prelude (`DropElement`, `Drop`), beschrieben in [Arrays und Listen](language/07-arrays-and-lists.md#containers-and-ownership-owns).

### D5. Ein `defined Dog`-Feld wird nativ weiter als „vielleicht undefined“ gelesen

Bevor der Konstruktor es setzt (Feld-Initialisierer, ein Basiskonstruktor, der eine überschriebene Methode ruft), ist es noch undefined. Gespart
würde ein Vergleich neben der ohnehin nötigen Prüfung, ob das Objekt lebt. Bleibt so (ROADMAP 19).

### D6. Arrays von Lambdas prüfen die Parameterzahl, nicht die Typen der Signatur

`lambda<int, int> fs[]`: ein Element ist ein Lambda mit zwei Parametern oder undefined - dasselbe, was eine einzelne `lambda<...>`-Deklaration
prüft. Die Typen der Signatur sind Hinweise für den Typprüfer; zwei Lambda-Arrays mit gleicher Parameterzahl gelten als gleich typisiert (SPEC 8.4.2).

### D7. `defined` für Objekte: der Konstruktor muss setzen

`defined Dog pet` hat keinen Nullwert: eine Variable braucht ihren Initialisierer, ein Feld ohne einen setzt jeder Konstruktor als eine seiner
Anweisungen, sonst ein Übersetzungsfehler. `defined string` beginnt bei `""`, ein `defined` Enum beim ersten Wert (SPEC 8.10b).
