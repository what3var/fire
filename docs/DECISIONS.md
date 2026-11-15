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

### D8. Prüft eine einzelne Deklaration `Color c` zur Laufzeit?

Heute nimmt ein Parameter, ein Feld oder eine Variable `Color c` (ohne `defined`) zur Laufzeit jeden Wert; nur der Typprüfer meldet eine falsche
Zuweisung. Geprüft wird dagegen `defined Color c`, ein Array `Color cs[]` und - seit D4 - ein `T` mit dem Typargument `Color`. Soll auch
`Color c` prüfen (ein Wert des Enums oder undefined, sonst eine ArgumentException), wie es `Dog d` für Klassen tut? Dafür: einheitlich.
Dagegen: Programme, die heute eine Zahl in eine Enum-Deklaration legen, bekämen eine Ausnahme.

## Entschieden

### D4. Ein Enum als Typargument ist zur Laufzeit bekannt

`Bag<Color>`: bisher ließ der Compiler das Typargument eines Enums zur Laufzeit leer - mit der Begründung, dass auch eine einzelne Deklaration
`Color c` zur Laufzeit nimmt, was kommt (der Typprüfer prüft sie). Dadurch typisierte `T items[]` für ein Enum nichts, `T is of Color` war falsch
und `T item` nahm jeden Wert. Jetzt (Wunsch des Benutzers) trägt das Typargument den vollen Namen des Enums: `T item` nimmt nur Werte dieses
Enums oder undefined, `T items[]`/`new T[n]` ist ein Array davon (wie `Color cs[]`), `T is of Color` stimmt; `List<Color>`,
`Dictionary<Color, V>`, `HashSet<Color>` prüfen so ihre Elemente. Compiler.TypeArgText, DeclaredType.ArrayOfTypeArg (ROADMAP 19).
Offen bleibt die Folgefrage D8.

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
