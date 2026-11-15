# Entscheidungen

Hier stehen Fragen zur Sprache und zur Laufzeit, die eine Entscheidung brauchen oder bekommen haben. Jeder Eintrag nennt den Stand
(**offen**, **entschieden**), die Frage, was dafür und dagegen spricht und - sobald entschieden - was gilt und wo es umgesetzt ist.
Neue Einträge kommen oben in ihren Abschnitt. Was nur noch Arbeit ist, steht in [ROADMAP.md](ROADMAP.md).

## Offen

### D1. Zerstört eine Feldzuweisung den alten Wert, wenn er dem Objekt gehört?

`this.d = new Dictionary<string, Node>()` ein zweites Mal: das alte Wörterbuch samt Inhalt lebt weiter, bis der Besitzer (das Objekt) endet.
Das gilt für jedes Objekt in einem Feld, nicht nur für Wörterbücher. Gefunden in Raser: ein Nachbau wuchs in sechs Runden von 142 auf 846 MB.

* Dafür: kein stilles Wachsen, wenn ein Feld immer wieder neu belegt wird.
* Dagegen (Tendenz des Benutzers: **nicht automatisch zerstören**): auf den alten Wert kann noch jemand zeigen - ein Kind oder Kindeskind des
  Objekts, ein Lambda, das ihn mitgenommen hat, ein Thread, dem er mit `handover` übergeben wurde. Das müsste vor dem Zerstören ausgeschlossen
  werden; sonst läuft diese Referenz später in eine DestroyedException. Die Container (D3) zerstören nur als Komfort, und nur, was ihnen
  ausdrücklich mit Besitz übergeben wurde.
* Heute: `delete this.d` vor der neuen Zuweisung. Möglich wäre stattdessen ein Hinweis des Typprüfers, wenn ein Feld, das ein eigenes Objekt hält,
  ohne `delete` neu belegt wird.

### D2. `try delete x` als Sprachmittel

Vorschlag des Benutzers: `try delete x` versucht, den Inhalt des Bezeichners zu löschen; ist er kein Objekt oder undefined, gibt es `false`,
sonst `true`. Für die Container wurde es nicht gebraucht (`x is from this` genügt, D3). Offen ist, ob es trotzdem kommt - und ob es nur löscht,
was dem Aufrufer gehört, oder jedes Objekt wie `delete`.

## Entschieden

### D11. Ein Methodenname ist statisch oder für Instanzen, nicht beides

Gefunden von einem Agenten: `static bool Inside(R a, R b)` neben `bool Inside(int x, int y)` in einer Klasse. Als Programm meldete der Compiler
`E4057` für `R.Inside(a, a)`, obwohl die statische Überladung passte; im Paket `ui` kam beim Übersetzen nichts und erst zur Laufzeit eine
ArgumentException ("not a static method"). Entscheidung des Benutzers: verboten, schon bei der Deklaration - `E4076` im Resolver, für eine Klasse
und über ihre Basisklassen, auch in Paketen (das Paket wird dann nicht gebaut). SPEC 5.4.3, Resolver.ValidateStaticAndInstance.

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
DeclaredType.WithTypeArgs / TypeArgsFit, VM.ClassArrayIsOf, nativ ClassGuard mit `guardTypeArgsFailed`, `TArg.isA` / `ArrClass.isA` mit `typeNameIn`.

### D8. Eine Deklaration `Color c` prüft zur Laufzeit

Ein Parameter, ein Feld, eine Property, eine Rückgabe oder eine Variable, die mit einem Enum deklariert ist, nimmt einen Wert dieses Enums oder
undefined - alles andere ist eine ArgumentException (`Parameter 'c' is a Color, not Int.`), wie bei `Dog d` für Klassen. Vorher prüfte das nur
der Typprüfer (und zur Laufzeit `defined Color c`, `Color cs[]` und ein `T` mit dem Typargument `Color`). Eine Zahl geht mit einem Cast hinein
(`5!Color`); ein Enum-Wert geht weiter ohne Cast in ein `int`. Entscheidung des Benutzers. Compiler.DeclaredTypeOf, TypeChecker.GuardLocalValue /
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
