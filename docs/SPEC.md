# fire – Sprachspezifikation (v0.1)

Referenzdokument für den Interpreter. Wird während der Implementierung erweitert.

Multithreading (`fire`/`taking`/`with`/`sync`/`leave`/`terminate`/
`catch threads`/`catch terminate`/Actors) ist bewusst NICHT Teil dieses
Dokuments – siehe `THREADING_DESIGN.md` dafür (vollständig implementiert,
Architektur UND Sprachsyntax).

## 0. Statement-Trennung

Zwischen zwei Statements muss ein `;` **oder** ein Zeilenumbruch stehen – fehlen beide, ist das ein Parse-Fehler (statt eines stillen Fehlverhaltens, bei dem der Parser über eine Zeile hinweg mehrdeutig weiterliest). Blockende (`}`) und Dateiende zählen ebenfalls als gültiger Abschluss, das erlaubt einzeilige Blöcke wie `{ return x }`.

Konsequenz für den Parser: Jede "optionale Weiterlese"-Entscheidung (Binär-Operatoren, Zuweisung `=`, die Postfix-Kette inkl. der Coercion-Suffixe `:`/`!`) bricht an einem Zeilenumbruch ab, statt gierig in die nächste Zeile zu greifen. Innerhalb einer offenen `(` oder `[` gilt das nicht – mehrzeilige Funktionsaufrufe, Argumentlisten und Bedingungen funktionieren wie gewohnt, weil dort ohnehin durch die Klammer klar ist, dass der Ausdruck weitergeht. Ein Ausdruck kann trotzdem über mehrere Zeilen laufen, wenn der Operator am **Ende** der ersten Zeile steht (`var x = a +\n    b`), da das Parsen des rechten Operanden nach einem bereits konsumierten Operator kein "optionales Weiterlesen" mehr ist, sondern zwingend erwartet wird.

**Explizite Fortsetzung mit `_`:** Steht ein alleinstehendes `_` als letztes "Wort" einer Zeile (optional gefolgt von einem Zeilenkommentar), wird der nachfolgende Zeilenumbruch nicht als Statement-Trenner gewertet – die nächste Zeile setzt die aktuelle Anweisung fort, als gäbe es dort keinen Zeilenumbruch. Das `_` selbst erzeugt kein Token (wird schon im Lexer verschluckt) und bleibt an jeder anderen Position ein ganz normaler, gültiger Bezeichner (z.B. als Variablenname).

```
var x = a _
    + b + c    // eine Anweisung, auch ohne '(' und ohne trailing-Operator

var _ = foo()  // '_' hier ganz normaler Bezeichnername, keine Fortsetzung
```

```
var a = 1
var b = 2          // ok: Zeilenumbruch trennt

var c = 1; var d = 2   // ok: Semikolon trennt

var e = 1 var f = 2    // FEHLER: weder ';' noch Zeilenumbruch
```

## 1. Grundtypen

`bool`, `int`, `float`, `char`, `string`, `class`, `undefined`

- `bool`, `char`, `string` sind Value-Types (Kopie bei Zuweisung), tragen keine Einheit.
- `int`, `float`, `undefined` tragen zusätzlich eine **Einheit** (siehe Abschnitt 3). Fehlt sie, ist der Default `unitless`.
- `class` referenziert eine Objektinstanz (Referenztyp, unterliegt dem Ownership-Modell, siehe Abschnitt 2).

```
var x = 1;              // int, unitless
var y = 2mm;             // int, Einheit mm
var z = undefined : kg;  // undefined, Einheit kg
```

### 1.1 Operatoren (arithmetisch, bitweise) & Präzedenz

Von locker (weit oben) nach fest bindend (weit unten):

```
||                        logisches Oder
&&                        logisches Und
|                         bitweises Oder     (nur int)
#                         bitweises Exklusiv-Oder (nur int - NICHT '^', das ist Potenz)
&                         bitweises Und      (nur int)
== != ##                  Gleichheit (`##` ist ein Synonym für `!=`)
< <= > >=                 Vergleich
<< >>                     Bit-Schiebeoperatoren (nur int)
+ -                       Addition/Subtraktion
* / %                     Multiplikation/Division/Modulo
^                         Potenz               (rechts-assoziativ, NICHT bitweises XOR)
- ! ~ * &                 unär (Vorzeichen/Negation/Bit-Inversion/Dereferenzierung/Address-of)
```

**Sonderfall `^` und unäre Operatoren** (Pythons `**`-Konvention, nicht
"unär bindet immer am stärksten"): ein Vorzeichen VOR der gesamten Potenz
bindet SCHWÄCHER als `^` selbst - `-2^2` ist `-(2^2) = -4`, nicht
`(-2)^2 = 4`. Der EXPONENT (rechts von `^`) darf dagegen ganz normal mit
einem unären Operator beginnen, ohne Klammern setzen zu müssen - `2^-2`
ist `0.25`, nicht abgelehnt oder `-4`.

`+`/`-`/`*`/`/`/`%` funktionieren auf `int`/`float` (siehe Abschnitt 3 für
das Zusammenspiel mit Einheiten) sowie `+` zusätzlich für String-
Konkatenation (sobald eine Seite ein `string` ist, wird die andere über
ihre normale `ToString()`-Darstellung angehängt). `^` (Potenz) akzeptiert
ebenfalls `int`/`float`; bei `int^int` mit nicht-negativem Exponenten
bleibt das Ergebnis `int` (ganzzahlig-schnelle Exponentiation), sonst wird
über `float` gerechnet (auch bei negativem Exponenten, da eine reine
`int`-Ganzzahlrundung von Werten <1 sonst fast immer nur 0 ergäbe).

`&`/`|`/`#`/`<<`/`>>` (bitweise Operatoren, inkl. dem unären `~`)
akzeptieren **ausschließlich** `int` - ein Laufzeitfehler bei `float`/
anderen Typen. Der rechte Operand von `<<`/`>>` (die Schiebeweite) ist
bewusst von jeder Einheiten-Prüfung ausgenommen (reine Zählgröße), das
Ergebnis übernimmt die Einheit des linken Operanden unverändert; `&`/`|`/
`#` verlangen dagegen wie `+`/`-`/`%` beidseitig dieselbe Einheit.

**Wichtig:** `<<`/`>>` werden bewusst NICHT vom Lexer als eigene Zwei-
Zeichen-Tokens erkannt (anders als z.B. `==`), sondern erst vom Parser als
zwei aufeinanderfolgende `<`/`>`-Tokens zusammengezogen - `<`/`>` bleiben
für den Lexer immer Einzelzeichen (außer `<=`/`>=`), weil `>` auch zum
Schließen einer generischen Typ-Argumentliste dient, auch beliebig tief
VERSCHACHTELT (`new Box<Box<int>>()`, siehe Abschnitt 5.8) - ein Lexer,
der `>>` gierig zu einem Schiebeoperator-Token zusammenzöge, würde das
zerstören. Da Typ-Argumentlisten über eigene, komplett getrennte Parser-
Methoden laufen (nie über die normale Ausdrucks-Präzedenzkette), gibt es
dabei ohnehin keinen Konflikt.

**Zahlen-Literale in binärer/hexadezimaler Schreibweise**: `0b`/`0x`-Präfix
(Groß-/Kleinschreibung beider Buchstaben erlaubt), z.B. `0b01101100`,
`0xFFCC8080` - immer `int`, nie `float` (kein Bruchteil/Exponent für eine
Bitmuster-Schreibweise). Unterstützen denselben Einheiten-Suffix wie ein
normales Zahlen-Literal (`0xFFmm`). Da die Sprache bereits eine Basiseinheit
`b`/`B` (Bit, mit SI-Präfixen) kennt, wird `0b`/`0x` nur dann als Radix-
Präfix gelesen, wenn DIREKT danach mindestens eine für den jeweiligen
Radix gültige Ziffer folgt - `0b` ohne folgende gültige Binärziffer bedeutet
deshalb weiterhin "die Zahl `0` mit Einheiten-Suffix `b`" (null Bit), kein
Lexer-Fehler.

## 2. Ownership-Modell

Jede Objektinstanz (`class`) hat **genau einen Owner**: entweder einen Scope (Block/Funktion/global) oder eine andere Objektinstanz.

### 2.1 Initialer Owner bei Erzeugung (`new Foo()`)

- Wird das neue Objekt **direkt einem Feld eines anderen Objekts zugewiesen** (`obj1.Foo = new Bar()`), ist der Owner sofort `obj1`.
- In allen anderen Fällen (lokale Variable, Parameterwert, Ausdruck) ist der Owner der **aktuelle Scope**.
- Das gilt auch für den Initialisierer eines Instanzfelds (`Item it = new Item(1)` im Klassenkörper) und für einen bloßen Feldnamen in einer Klasse (`feld = new X()` statt `this.feld = new X()`): das Objekt gehört der Instanz. (Früher gehörte es dort dem Initialisierer-/Methoden-Scope und wurde beim Verlassen zerstört, obwohl das Feld noch darauf zeigte.)
- Dieselbe Regel gilt für Lambda-Werte: direkte Feldzuweisung → Owner ist das Objekt; sonst → aktueller Scope. Das `on`-Binding (this-Kontext, s. 4.2) ist davon unabhängig und ändert den Owner nicht.

### 2.2 Ownership-Transfer (Member-Funktionen auf Objektinstanzen)

- `obj.TakeUpwards()` – Owner wird der Parent-Scope des aktuellen Owner-Scopes (nur sinnvoll, wenn aktueller Owner ein Scope ist).
- `obj.TakeGlobal()` – Owner wird der globale Scope.
- `obj.TakeTo(other)` – Owner wird `other` (eine Objektinstanz).
- Alle drei sind eingebaute Methoden jeder Objektinstanz (eine Klasse, die eine gleichnamige Methode selbst deklariert, geht vor). Eine Funktion behält damit ein Objekt, das ihr gehört (z.B. eine als Parameter übergebene Kopie, 2.4): `param.TakeTo(this)`.
- **Zyklenschutz:** `TakeTo(other)` prüft, ob `other` transitiv bereits ein "Nachfahre" (direkt oder indirekt im Besitz) von `obj` ist. Falls ja: Laufzeitfehler statt Zyklus im Ownership-Baum.
- **Race mit laufender Löschung:** Befindet sich `other` (das Ziel von `TakeTo`) selbst gerade in Kaskadenlöschung (ihr eigener Owner wurde gerade zerstört, ihre `destruct()`-Kaskade läuft bereits), wird die Übergabe so behandelt, als wäre sie eine Sekunde *vor* Beginn dieser Löschung erfolgt: `obj` wird ebenfalls sofort in die laufende Kaskade aufgenommen und mitgelöscht (inkl. `destruct()`-Aufruf), statt als Waise mit einem halb-zerstörten Owner zurückzubleiben.
- Variablen-Bindings (Name → Wert) selbst wandern **nicht** – nur Objekt-Ownership ist transferierbar.

### 2.3 Lebenszeit / Kaskadenlöschung

- Wird ein Owner (Scope beim Verlassen, oder Objekt beim Löschen) zerstört, werden alle Objekte, deren Owner er noch ist, rekursiv mitzerstört (Kaskade). Dabei wird pro Objekt `destruct()` aufgerufen (s. 5.3).
- **Programmende, `leave` und `terminate`:** Beim **normalen Ende** des Programms, bei `leave` und bei `terminate` wird der **globale Scope** wie jeder andere
  Scope beim Verlassen freigegeben: `destruct()` läuft für alles, was ihm (transitiv) gehört, offene Streams werden also geschlossen. `leave` beendet
  den **aufrufenden Thread**, `terminate` **alle Threads** (von überall auslösbar, das sanfte Ende für alles). Der Thread, der `leave`/`terminate` aufruft,
  hält **sofort** an (keine weitere Anweisung, in jedem Ausführungsmodus, auch mitten in einer Property/einem Destruktor/einer Operator-Überladung); danach werden seine
  offenen Scopes abgewickelt und `finally`-Blöcke laufen. Das Hauptprogramm **wartet an seinem Ende auf alle laufenden
  `fire`-Threads** (sie können per `sync` in seine Objekte zurückschreiben; das gilt auch nach `leave`/`terminate`), erst danach werden seine Globals zerstört - die
  globalen Destruktoren laufen also nach dem Ende des letzten Threads. Ein Fire-Thread zerstört an seinem
  Ende nur Objekte, die er selbst angelegt hat - Kopien von Objekten des Hauptprogramms (Globals-Schnappschuss, `taking`) bleiben unberührt, damit sie z.B. kein
  geteiltes Handle schließen. Eine unbehandelte Exception wickelt die offenen Scopes ab, gibt den globalen Scope aber nicht frei. Als
  Sicherheitsnetz schließt der Host am Ende außerdem alle Streams, die noch offen sind (`IoBridge.RegisterAll(...).Dispose()`). Ein Destruktor sollte nie
  werfen: ein unbehandelter Fehler darin beendet das Programm (die `IO`-Destruktoren verschlucken deshalb IO-Fehler).
- **Ausnahme `return`:** Wird aus einem lokalen Scope eine Objektinstanz per `return` zurückgegeben, *und* war dieser Scope ihr Owner, geht das Ownership implizit an den aufrufenden/Parent-Scope über (kein Kaskadenlöschen in diesem Fall).

### 2.4 Kopieren: `flat` und `copy`

```
var a = new Box("a")
a.item = new Item(1)

var f = flat a        // flache Kopie: neues Box-Objekt, a.item wird geteilt
var d = copy a        // Tiefenkopie: neues Box-Objekt UND neues Item
holder.other = copy a // direkt einem Feld zugewiesen: die Kopie gehört holder (wie bei `new`, 2.1)
Work(flat a)          // auch als Argument
```

`flat` und `copy` sind Präfixe vor einem Ausdruck (`copy a.b` kopiert `a.b`) und als Wort reserviert - `copy` ist damit
kein Bezeichner mehr, `flat` war es schon (`sync flat`).

**`flat x`** kopiert das Objekt selbst samt seiner Felder (auch der Backing-Felder von Auto-Properties). Wertartige Felder
(`bool`/`int`/`float`/`char`/`string`/`undefined`) werden als Wert übernommen, alles Referenzartige - Objekte, Arrays, Puffer,
Lambdas, Pointer - bleibt **dieselbe Referenz wie im Original**.

**`copy x`** ist eine Tiefenkopie: jede vom Operanden aus über Felder und Array-Elemente erreichbare Instanz wird genau **einmal**
kopiert. Kommt dieselbe Instanz (oder dasselbe Array) wieder vor - gemeinsam genutzt oder zyklisch -, zeigt die Kopie auf die
schon gemachte Kopie: die Struktur des Originals (Teilen, Zyklen) bleibt erhalten.

**Owner.** Die Kopie ist ein neues Objekt und bekommt einen Owner, abhängig davon, wohin sie geht:
- **Als Argument** eines Aufrufs (`f(copy a)`, `obj.M(flat a)`, `new X(copy a)`, `base(copy a)`, Lambda-Aufruf): die Kopie gehört dem **Scope der
  aufgerufenen Funktion** und wird mit deren Ende zerstört - es sei denn, die Funktion gibt sie zurück (dann geht sie an den Aufrufer, 2.3) oder
  behält sie mit `param.TakeTo(...)`/`TakeGlobal()` (2.2). Das gilt auch für Konstruktoren: `construct(i) { this.held = i }` allein reicht nicht,
  `i.TakeTo(this)` gehört dazu. Bei einer **nativen** Funktion (`print(copy a)`) und bei Nachrichten an einen Actor gibt es keine solche Scope - dort gehört die
  Kopie dem aktuellen Scope. Die Kopie wird erst **beim Aufruf** angelegt, nachdem alle Argumente ausgewertet sind (`f(copy a, a.Inc())` kopiert also den
  Stand nach `Inc()`); `flat`/`copy` als Argument ist für die ersten 16 Argumente möglich.
- **An ein Objekt zugewiesen** (`obj.feld = copy x`, `this.feld = ...`, bloßer `feld = ...` in einer Klasse, Feld-Initialisierer `Item i = copy x`): das
  Objekt wird der Owner, wie bei `copy.TakeTo(obj)` (2.2) - auch die Sonderregel gilt: ist das Zielobjekt schon in der Kaskadenlöschung, wird die Kopie
  sofort mitzerstört.
- **Sonst** (lokale Variable, Index-Zuweisung, Ausdruck): der aktuelle Scope.

Bei `copy` gilt für die Instanzen darunter: war eine kopierte Instanz im Original im Besitz einer ebenfalls kopierten Instanz, gehört ihre Kopie deren
Kopie (der Besitzbaum wird nachgebildet); alles andere - insbesondere Instanzen, die im Original jemand anderem gehören (ein Scope, ein Objekt außerhalb der
Kopie) - gehört dem Owner der Wurzel-Kopie. So wird jede Kopie mit ihrem Owner zerstört (2.3), und die Originale bleiben unberührt.

**Weitere Regeln**
- Es läuft **kein Konstruktor** - die Feldwerte werden einfach übertragen. Der Destruktor läuft für die Kopie wie für jedes Objekt.
  Vorsicht bei Objekten, die eine externe Ressource halten (z.B. einen `IO.FileStream` mit seinem Handle): die Kopie teilt sich den
  Handle mit dem Original, beide schließen ihn beim Zerstören.
- Ein **Actor** als Operand ist ein Fehler; ein Actor im Innern einer Kopie bleibt eine geteilte Referenz (Actor-Referenzen sind zum
  Herumreichen da). Ebenso bleibt ein bereits zerstörtes Objekt im Innern eine geteilte Referenz; als Operand ist es ein Fehler.
- Lambdas und Pointer werden in beiden Fällen geteilt, nicht kopiert.
- Operanden ohne Inhalt (Zahl, Text, `true`, `undefined`) ergeben einfach sich selbst. Ein **Array** als Operand: `flat` legt ein neues
  Array mit denselben Elementen an, `copy` kopiert auch die Elemente; ein **Puffer** (`byte[]`) wird in beiden Fällen byteweise kopiert.
- `flat` kann zwischen Original und Kopie geteilte Referenzen hinterlassen, deren Besitzer das Original ist: wird das Original
  zerstört (samt dem, was es besitzt), zeigt die flache Kopie auf zerstörte Objekte. Wer ein eigenständiges Gebilde braucht, nimmt `copy`.
- Anders als `taking` (Kopie für einen Thread, lehnt jede Referenz aus dem Besitzbaum hinaus ab, `sync` schreibt zurück) ist das eine
  gewöhnliche Kopie ohne Rückverknüpfung zum Original.

## 3. Einheiten (Units)

### 3.1 Syntax

- Direkt an einem Zahlenliteral ohne Trennzeichen: `5mm`, `2mm`, `1km`.
- `:`-Operator zur **Einheiten**-Annotation/-Coercion an einem Wert: `undefined : km`.
  - `wert:` (ohne Argument) → Einheit wird aus dem Kontext der Operation automatisch abgeleitet (siehe 3.4).
  - `wert:einheit` → Einheit wird explizit auf `einheit` erzwungen/umgerechnet.
- `!`-Operator zur **Typ**-Coercion: `wert!`, `wert!int`.
  - `wert!` (ohne Argument) → Typ wird aus Kontext abgeleitet.
  - `wert!typ` → Typ wird explizit erzwungen.
- `:` (Einheit) und `!` (Typ) sind unabhängige Operatoren und beliebig kombinierbar: `a:km!`, `a:!`.

### 3.1.1 `!` ist kontextabhängig durch Position (Präfix vs. Suffix)

- **Präfix** `!ausdruck` → logische Negation (klassisches "not").
- **Suffix** `ausdruck!`, `ausdruck!typ` → Typ-Coercion (siehe oben).
- Da beide Positionen syntaktisch eindeutig unterscheidbar sind (Präfix vor einem Unary-Operanden, Suffix nach einem bereits geparsten Ausdruck), reicht ein einziges Lexer-Token (`Bang`); die Disambiguierung erfolgt im Parser über die Grammatikposition.
- **`~ausdruck`** (nur Präfix) → bitweise Inversion.

Den **Typ** einer Variable gibt man VOR dem Namen an (`int x`, `float y = 2.5`, `Foo f`); `var x` leitet ihn aus dem Wert ab. Ein `:` hinter dem Variablennamen (`var x : mm`) legt dagegen nur die **Einheit** fest (siehe „Einheiten-Deklarationen“) – `var x : int` als Typ-Deklaration gibt es nicht mehr.

### 3.2 Zieleinheit bei Operationen ("Anker-Regel")

Bei einer binären Operation zwischen Operanden mit Einheiten:

- Fordert **genau ein** Operand keine `:`-Coercion an, ist dessen Einheit die Zieleinheit der gesamten Operation; alle anderen (kompatiblen) Operanden werden dorthin umgerechnet.
- Fordern **mehrere/alle** Operanden `:`-Auto-Coercion an (ohne dass irgendeiner eine explizite Zieleinheit nennt), ist die Zieleinheit `unitless`.
- Inkompatible Dimensionen ohne passende Coercion → Laufzeitfehler.
- **Ketten mit mehr als zwei Operanden** (`a + b + c`) werden klassisch links-assoziativ ausgewertet: `(a + b) + c`. Die Anker-Regel wird bei jedem Teilschritt erneut angewendet, wobei das Zwischenergebnis (inkl. seiner bereits bestimmten Einheit) als linker Operand des nächsten Schritts gilt – es gibt also keine globale "alle Operanden auf einmal"-Betrachtung über die ganze Kette.

```
int a = 5mm
float b = undefined:km

var c = b + a:!     // a wird auto-coerced (Einheit+Typ), b ist Anker -> c : float:km
var d = b: + a:!    // beide fordern Auto-Coercion -> kein Anker -> d : float:unitless

b = a:km!            // a explizit nach km (Einheit) und automatisch nach float (Typ)
```

### 3.3 Präfixsystem

Präfixe (dezimal, implizit coerced):

| Symbol | Name  | Faktor |
|--------|-------|--------|
| f      | femto | 1e-15  |
| p      | pico  | 1e-12  |
| µ      | mikro | 1e-6   |
| m      | milli | 1e-3   |
| c      | centi | 1e-2   |
| k      | kilo  | 1e3    |
| M      | mega  | 1e6    |
| G      | giga  | 1e9    |

Präfixfähige Basiseinheiten: `m` (Meter), `g` (Gramm), `s` (Sekunde), `b` (Bit), `B` (Byte, 8 `b`).

Nicht-präfixfähige, aber mit `s` dimensionskompatible Zeiteinheiten mit festen Faktoren:

| Einheit | Faktor zu `s` |
|---------|----------------|
| `min`   | 60             |
| `h`     | 3600           |
| `d`     | 86400          |

Jede sonstige, nicht erkannte Suffix-Zeichenfolge an einem Literal wird als **atomare Einheit** behandelt (kompatibel nur zu sich selbst), z. B. `5apples`.

### 3.4 Dimensionsarithmetik

- Multiplikation/Division kombiniert Dimensionsvektoren additiv/subtraktiv über die Exponenten (`mm * mm → m²`-Dimension mit Skalierungsfaktor 1e-6, `m / s → m·s⁻¹`).
- Zwei Einheiten sind kompatibel (addierbar/vergleichbar), wenn ihre Dimensionsvektoren übereinstimmen; die Umrechnung erfolgt über das Verhältnis ihrer Skalierungsfaktoren zur Basiseinheit.
- Multiplikation/Division mit einem unitless Operanden liefert GENAU die Einheit des anderen Operanden zurück (`20mm / 2 → 10mm`, nicht irgendeine neu konstruierte, unbenannte Einheit) - sonst würde die Anzeige den Skalierungsfaktor verschlucken (siehe `mm`-Beispiel). Dasselbe benannte Einheit-mal-sich-selbst (`radius * radius`) bekommt ebenfalls einen synthetisierten Anzeige-Namen (`mm^2`). Für alle anderen zusammengesetzten Fälle (z.B. verkettete `a*a*a`, oder wirklich gemischte Einheiten mit einem Skalierungsfaktor ≠ 1.0) zeigt die Ausgabe den Faktor explizit an (`m^3(×1e-09)`) statt ihn stillschweigend zu ignorieren - eine bewusste, einfache Grenze statt eines vollständigen "hübsche Einheitennamen für beliebige Kombinationen synthetisieren"-Systems.

## 4. Scopes & Lambdas

### 4.1 Scope-Hierarchie

- Jeder Block (`{}`), jede Schleifeniteration, jede Funktion erzeugt einen eigenen Scope-Knoten in einem Baum mit Parent-Zeiger, bis hinauf zum globalen Scope.
- Namensauflösung für **normalen Code** läuft die Scope-Kette klassisch nach oben (lexikalisch).

### 4.2 Lambdas

- Eine Lambda sieht beim Namens-Lookup **ihren eigenen Scope, den globalen Scope und Kopien der äußeren lokalen Werte, die ihr Körper benutzt** (Captures, siehe 4.2.1) – keine Closure über dazwischenliegende Scopes.
- `on obj` bindet ein Objekt als `this`-Kontext, entweder bei Definition (`func (X) on obj => { ... }`) oder nachträglich bei Zuweisung (`var b = a on obj2;`, erzeugt einen neuen Lambda-Wert mit anderem `this`, `a` bleibt unverändert).
- Membervariablen des gebundenen `this`-Objekts sind im Lambda-Body unqualifiziert sichtbar.
- Ownership des Lambda-Werts folgt Abschnitt 2.1 (Feldzuweisung → Objekt-Owner, sonst Scope-Owner) – unabhängig vom `on`-Binding.
- Für eine Typ-Annotation, die einen Lambda-Wert erwartet (Feld, Parameter, Rückgabetyp, `var`), steht der Typname **`lambda`** zur Verfügung, optional mit Signatur: `[RückgabeTyp] lambda[<ParamTyp1,...,ParamTypN>]`. Bewusst **nicht** `func` (das leitet einen Lambda-*Ausdruck* ein, `func (x) => ...`, und würde als Typname mit dieser Ausdrucks-Syntax kollidieren). Details siehe 4.3.

### 4.2.1 Kurzsyntax und Captures

**Kurzsyntax.** Neben `func (x) => ...` gibt es `x => ausdruck`, `(a, b) => ausdruck`, `() => ausdruck` und jeweils `=> { ... }` mit Block. Parameter dürfen
wie sonst Typen/Standardwerte tragen (`(int a, int b) => a + b`); `on obj` gibt es nur bei der `func`-Form.

**Captures.** Benutzt der Körper einer Lambda Namen, die im umschließenden Code **lokale Variablen oder Parameter** sind (Methodenparameter, `var` in Blöcken und
Schleifen, Parameter einer umschließenden Lambda), werden deren **Werte beim Erzeugen der Lambda kopiert**:

```
class T {
    static Run() {
        var limit = 3
        var f = x => x > limit          // limit wird kopiert
        limit = 10
        print(f(5))                     // True - die Lambda sieht weiter 3
    }
}
```

- Es wird der **Wert** kopiert, nicht die Variable: spätere Änderungen draußen sind drinnen unsichtbar und umgekehrt (es gibt keine geteilten, veränderlichen Variablen - auch
  keine Schleifenvariablen-Falle: `for (...) { fs.Add(() => i) }` erfasst je Durchlauf den aktuellen Wert).
- Eine **Zuweisung an einen Capture** im Lambda ist ein Fehler („ist im Lambda eine KOPIE …“); eine eigene Deklaration mit demselben Namen (`var limit = 100`) verdeckt ihn.
- **Objekte** werden als Referenz kopiert (der Wert ist die Referenz): die Lambda sieht und verändert dasselbe Objekt. Es bleibt im Besitz seines ursprünglichen Owners - überlebt die Lambda
  ihn, ist es danach zerstört. Eine eigene Kopie erzwingt man mit `copy x`/`flat x` in einer lokalen Variable davor.
- **Globale** Variablen werden nicht kopiert, sie bleiben lebendig (`g = 7` ist in der Lambda sichtbar). Das gilt auch für Top-Level-Variablen.
- `this` wird nicht erfasst (dafür `on this`). `fire global { }` erfasst nichts - dort gilt allein `taking`.
- Nicht erfasst werden Namen, die kein Lokal des umschließenden Codes sind (Klassenmitglieder, Natives, Klassen): sie lösen wie bisher auf.

### 4.3 Lambda-Typen mit Signatur

(`lambda member<T> name` & Co. - ein Selektor, der ein Mitglied eines Objekts auswählt - steht in 8.13.)

```
class Runner {
    Execute(lambda<int> callback, int x) {
        return callback(x)
    }
}

var r = new Runner()
var doubleIt = func (n) => n * 2      // Kurzform: '=> ausdruck' statt '=> { return ausdruck }'
print(r.Execute(doubleIt, 21))         // 42

int lambda<int, int> adder = func (a, b) => { return a + b }
print(adder(3, 4))                     // 7

lambda greet = func () => { print("hi") }   // 'lambda' ohne '<...>' = 0 Parameter
greet()
```

`lambda` allein steht für eine parameterlose Lambda; `lambda<P1,...,Pn>` für
eine mit `n` Parametern; ein optionaler Rückgabetyp steht **vor** `lambda`
(`int lambda<int>`, nicht in den spitzen Klammern) – das macht die
Grammatik unzweideutig, ohne ein Trennzeichen zwischen Rückgabe- und
Parametertypen zu brauchen. Anders als sonstige Typ-Annotationen (SPEC
8.1: rein syntaktisch, nicht zur Laufzeit erzwungen) wird bei `lambda` die
**Parameteranzahl tatsächlich geprüft** – und zwar überall dort, wo ein
Wert einer `lambda`-Annotation zugewiesen wird: bei einer `var`-
Deklaration mit Initializer, und bei jedem Funktionsaufruf für einen so
typisierten Parameter (direkt am Anfang des Funktionskörpers, bevor
irgendein Nutzer-Code läuft). Passt die Parameterzahl nicht, ist das ein
Laufzeitfehler. Die einzelnen Parameter-/Rückgabe-**Typnamen** selbst
werden dagegen nicht geprüft (nur ihre Anzahl) – eine Lambda legt ihre
Parametertypen zur Laufzeit nicht verlässlich offen (dynamisch typisierte
Sprache), und ein Parameter- oder Rückgabetyp, der selbst wieder ein
`lambda<...>` mit eigener Signatur wäre (verschachtelt), wird nicht
unterstützt.

## 5. Klassen

### 5.1 Deklaration & Vererbung

- Single-Inheritance: `class Foo : Bar { ... }`
- `base(...)` im Konstruktor ruft den Elternkonstruktor auf.
- `base.Method(...)` ruft eine überschriebene Elternmethode auf.

### 5.2 Konstruktor

```
class Foo : Bar {
    construct(int x) : base(x) {
        // ...
    }
}
```

### 5.3 Destruktor

```
class Foo {
    destruct() {
        // Aufräumarbeiten, wird bei Kaskadenlöschung durch Ownership aufgerufen
    }
}
```

Bei einer abgeleiteten Klasse läuft die **ganze Kette** der Destruktoren: erst der
der abgeleiteten Klasse, dann der jeder Basisklasse (wie in C#) - eine Klasse ohne eigenen
`destruct()` räumt also trotzdem mit dem ihrer Basisklasse auf.

### 5.4 Methodenüberladung

```
class Calculator {
    int Add(int a, int b) {
        return a + b
    }

    int Add(int a, int b, int c) {
        return a + b + c
    }
}
```

Mehrere Methoden desselben Namens sind erlaubt, solange sie sich in der
**Parameteranzahl** unterscheiden – das ist bei einer dynamisch typisierten
Sprache das einzige zur Aufrufzeit generell verlässliche
Unterscheidungsmerkmal (eine Überladung nach Parameter-*Typ* wäre nicht
durchgängig prüfbar, da Typen zur Laufzeit an Werten hängen, nicht an
Variablen). Zwei Methoden mit demselben Namen **und** derselben
Parameteranzahl in derselben Klasse sind ein Fehler. Eine abgeleitete
Klasse kann eine Überladung mit einer ANDEREN Arity ergänzen, ohne die
geerbten Überladungen der Basisklasse zu verdecken – der Aufruf sucht über
die ganze Vererbungskette nach der zur tatsächlichen Argumentzahl
passenden Überladung.

**Konstruktoren** funktionieren genauso – mehrere `construct(...)` mit
unterschiedlicher Parameteranzahl in derselben Klasse:

```
class Point {
    int x
    int y

    construct() { this.x = 0; this.y = 0 }
    construct(int x, int y) { this.x = x; this.y = y }
}
```

(anders als bei Methoden aber ohne Basisklassen-Kette – `new Derived(...)`
nutzt immer nur Deriveds eigene Konstruktoren, nie die der Basisklasse.)

### 5.4.1 Optionale Parameter

```
string Greet(string name, string greeting = "Hallo") {
    return greeting + ", " + name
}

Greet("Welt")             // "Hallo, Welt"
Greet("Welt", "Servus")   // "Servus, Welt"
```

Ein Parameter kann einen **Standardwert** bekommen (`= ausdruck`), der
verwendet wird, wenn der Aufruf weniger Argumente liefert. Optionale
Parameter müssen am **Ende** der Parameterliste zusammenhängen – kein
Pflichtparameter nach einem optionalen. Gilt für Methoden, Konstruktoren
und Lambdas gleichermaßen. Ein Parameter wird wie eine Variable geschrieben:
`Typ name` (der Typ vor dem Namen, wie bei `int x`), optional gefolgt von
`= Standardwert`. Die frühere Schreibweise `name : Typ` gibt es nicht mehr.

```
f(int x = 42) { ... }
```

Der Standardwert-Ausdruck sieht dabei nur seinen eigenen Kontext + global +
`this` (wie ein Feld-Initialisierer) – **nicht** die anderen Parameter
derselben Funktion, da er unabhängig von diesen (erst beim eigentlichen
Aufruf, falls das Argument fehlt) ausgewertet wird.

### 5.5 Extension-Klassen (`class extends`)

```
class Animal {
    string name
    construct(string name) { this.name = name }
}

class extends Animal {
    int age

    string Describe() {
        return this.name
    }
}
```

`class extends Name { ... }` fügt die enthaltenen Mitglieder (Felder,
Methoden, Properties, ...) direkt zur **bestehenden** Klasse `Name` hinzu –
kein neuer Klassenname, keine Vererbung, sondern "Reopening" wie in Ruby:
die neuen Mitglieder landen 1:1 in der ursprünglichen Klasse, als hätten
sie von Anfang an dort gestanden. `Name` muss im selben (bei Nutzung mit
Prelude: kombinierten) Programm bekannt sein, sonst ist es ein Fehler –
eine Erweiterung kann keine neue Klasse anlegen. Mehrere `class extends
Name`-Blöcke für denselben Namen werden alle zusammengeführt. Das erlaubt
z.B. eigene Zusatzmethoden für `List` aus der Standardbibliothek, ohne
deren Quelltext selbst anfassen zu müssen.

### 5.5.1 Basistypen erweitern (`class extends string`)

```
class extends string {
    string Shout() { return this.ToUpper() + "!" }
    bool IsBlank() { return this.Trim().Length == 0 }
}

class extends int {
    bool IsEven() { return this % 2 == 0 }
}

print("hallo".Shout())      // HALLO!
int n = 21
print(n.IsEven())           // False
```

Auch die Basistypen `string`, `char`, `int`, `float` und `bool` lassen sich mit `class extends` erweitern.
Innerhalb der Methoden ist `this` der **Wert selbst** (kein Objekt), sonst gilt alles wie bei
Methoden (Überladung nach Parameteranzahl, optionale Parameter, `private`, Ausnahmen, Aufruf anderer
Erweiterungsmethoden über `this.`). Mehrere Blöcke für denselben Typ - auch aus anderen Dateien oder
Namespaces - werden zusammengeführt; die Erweiterung gilt für alle Werte dieses Typs.

Erlaubt sind **nur Methoden**: ein Basiswert hat keinen Speicher, in dem ein Feld oder eine Property
liegen könnte. Ein Feld, eine (Auto-)Property, ein Konstruktor/Destruktor, eine `static`-Methode
(`string.Foo()` gibt es nicht) und eine Operator-Überladung sind ein Fehler bei der Übersetzung
(„'class extends string': Feld 'x' nicht erlaubt …“). `byte` lässt sich nicht erweitern - ein `byte`
ist zur Laufzeit ein `int`, also `class extends int`. Für **Arrays** gibt es `class extends array { ... }` (ein Bezeichner, kein Schlüsselwort; `this` ist das Array, es gelten dieselben Regeln: nur Instanzmethoden) - so bekommt jedes
Array z.B. die LINQ-Operatoren (`#import "linq"`). Puffer sind nicht erweiterbar.

Die Methoden des Prelude für `string` und `char` (8.12) sind genau solche Erweiterungen. Eine eigene
Methode mit demselben Namen und derselben Parameteranzahl wie eine bestehende ist - wie bei jeder Klasse -
eine Doppeldefinition.

### 5.6 `with`-Statement

```
with objekt {
    .feld = 5
    .Methode()
    print(.anderesFeld)
}
```

BASIC-artiges `with`: der `with`-Ausdruck wird **einmal** ausgewertet, und
jeder Ausdruck, der innerhalb des Blocks mit `.` beginnt, bezieht sich
implizit auf dieses Ergebnis (`.feld` statt `objekt.feld`) – sowohl lesend
als auch als Zuweisungsziel. Rein syntaktischer Zucker, keine eigene
Laufzeit-Semantik: `with p { .x = 5 }` verhält sich exakt wie
```
{
    var __temp__ = p
    __temp__.x = 5
}
```
(inklusive eigenem Block-Scope für die temporäre Variable). `with`-Blöcke
dürfen verschachtelt werden; ein `.` bezieht sich dabei immer auf den
**innersten** umschließenden `with`-Block.

### 5.8 Generische Klassen (`class Name<T>`)

```
class Animal { }
class Dog : Animal { }

class Container<T> where T is of Animal {
    T item
}

var c = new Container<Dog>()   // ok, Dog erfüllt "is of Animal"
```

Eine Klasse kann Typ-Parameter deklarieren (`class Name<T1, T2>`), mit
optionalen `where`-Bedingungen (eine `where`-Klausel **pro** Typ-Parameter,
beliebige Reihenfolge, vor der öffnenden `{`):

```
class Precise<T> where T is of float : is in "mm" { }
```

Jede `where`-Klausel besteht aus einer oder mehreren **Bedingungsgruppen**,
mit `,` verbunden (**ODER** – mindestens eine Gruppe muss erfüllt sein);
innerhalb einer Gruppe können mehrere Bedingungen mit `:` verbunden werden
(**UND** – alle müssen erfüllt sein). Eine einzelne Bedingung ist entweder
`is of Name` (Name ist eine Klasse/ein Interface, oder die Basisklassen-/
Interface-Kette des Typ-Arguments enthält Name) oder `is in "einheit"`
(das Typ-Argument, als Einheitenname gelesen, ist dimensional kompatibel).

Eine generische Klasse wird mit **expliziten** Typ-Argumenten
instanziiert: `new Name<Arg1, Arg2>(...)`. Die Argumente werden zur
Resolve-Zeit gegen die `where`-Bedingungen geprüft – ein Verstoß, eine
falsche Argumentanzahl oder Typ-Argumente an einer nicht-generischen
Klasse sind Fehler. `T` selbst darf innerhalb der Klasse überall dort
verwendet werden, wo sonst ein Typname steht (Felder, Parameter,
Rückgabetypen) – ohne dass der Resolver "unbekannter Typ" meldet.

Typ-ARGUMENTE dürfen beliebig tief VERSCHACHTELT sein
(`new Container<Box<int>>(...)`, `new Container<Box<Box<int>>>(...)`, ...)
- syntaktisch vollständig unterstützt. Da es aber (siehe nächster Absatz)
KEINE echte generische Spezialisierung gibt, zählt für die
Constraint-Prüfung (`where`) bei einem verschachtelten Typ-Argument nur
dessen ÄUSSERER Name (`Box`, nicht `Box<int>`) – die inneren Argumente
werden rein syntaktisch gelesen und danach verworfen.

**Gleicher Name wie eine nicht-generische Klasse**: Eine generische Klasse
darf denselben Namen tragen wie eine nicht-generische (wie in C#):

```
class Box { }                 // nicht-generisch
class Box<T> { T item }       // generisch, anderer Typ

var a = new Box()             // die nicht-generische
var b = new Box<int>()        // die generische
```

Die **Anzahl der Typ-Argumente** bei `new` wählt die Klasse. Jede Stelle
OHNE Typ-Argumente (Typ-Annotation, `is of Box`, `catch (Box e)`,
`Box.Statisch`, Basisklasse, `class extends Box`) meint die nicht-generische
Klasse - für die generische gibt es (nur) die Form `new Box<...>(...)`.
Intern heißt die generische Klasse dann `Box`1` (Name + '`' + Anzahl der
Typ-Parameter, siehe `Ast.GenericClassNames`), das ist nur in Fehlermeldungen
und Stack-Ausgaben zu sehen. Erlaubt ist nur "generisch NEBEN nicht-generisch";
zwei generische Klassen gleichen Namens bleiben eine Doppeldefinition (auch
mit unterschiedlicher Typ-Parameter-Anzahl).

**Wichtige Einschränkung**: Da diese Sprache dynamisch typisiert ist,
findet **keine echte Typ-Substitution** statt (anders als in C#, wo der
Compiler pro Instanziierung spezialisierten Code erzeugt) – die Prüfung
der Typ-Argumente gegen die `where`-Bedingungen passiert **einmalig bei
`new Name<...>(...)`**, danach verhält sich `T` im Rumpf der Klasse wie ein
normaler, nicht weiter geprüfter Platzhalter-Typname. Generische
**Methoden** (`RetType Name<T>(params) where T ... { }`) werden geparst
und die Typ-Parameter akzeptiert, aber **nicht** mit expliziten
Typ-Argumenten am Aufrufort geprüft – `Name<T>(x)` als Aufruf-Syntax wäre
mit `<`/`>` als Vergleichsoperatoren an normalen Ausdrucks-Stellen
mehrdeutig (anders als bei `new Name<...>`, wo direkt nach dem Klassennamen
zwingend eine Argumentliste folgen muss, also unzweideutig ist). Eine
generische Methode wird deshalb ganz normal ohne `<...>` aufgerufen.

### 5.9 `switch`-Statement (mit Vergleichsoperatoren)

```
switch (x) {
    case <= 1:
        print("klein")
        break
    case 2:
        print("zwei")
        break
    case default:
        print("gross")
}
```

Jeder `case` besteht aus einem optionalen Vergleichsoperator (`<`, `<=`,
`>`, `>=`, `==`, `!=`) gefolgt von einem Wert – fehlt der Operator, wird
`==` angenommen (`case 2:` bedeutet also `x == 2`). Der switch-Ausdruck
wird **einmal** ausgewertet und bei jedem `case` erneut mit der
angegebenen Bedingung verglichen; anders als in C-artigen switches gibt es
**kein Fallthrough** – jeder Zweig ist exklusiv, es läuft höchstens einer.
`case default:` fängt alles auf, was keine der vorherigen Bedingungen
erfüllt (optional, am besten – aber nicht zwingend – als letzter Zweig).
`break` beendet den aktuellen Zweig; da es ohnehin kein Fallthrough gibt,
ist es rein syntaktischer Abschluss (kein Sprung) und deshalb **optional**
– ein Zweig endet genauso am nächsten `case` oder am `}`. Wird `break`
verwendet, muss es die letzte Anweisung des Zweigs sein. `switch (x) {
case OP wert: ... }` verhält sich exakt wie eine If/Else-if-Kette:
```
{
    var __temp__ = x
    if (__temp__ <= 1) { print("klein") }
    else if (__temp__ == 2) { print("zwei") }
    else { print("gross") }
}
```

`break` ist innerhalb eines `switch` **ausschließlich** als Zweig-Abschluss
gültig (siehe oben) – das ist etwas anderes als das allgemeine
Schleifen-`break` aus 5.10, auch wenn beide dasselbe Schlüsselwort
benutzen. Steht ein `switch` innerhalb einer Schleife, beendet ein `break`
direkt in einem seiner `case`-Zweige deshalb **den switch-Zweig, nicht die
Schleife** (wie in den meisten C-artigen Sprachen) – für die Schleife
selbst müsste das `break` in einer eigenen, in den `case`-Zweig
verschachtelten Schleifenkonstruktion stehen.

### 5.10 `break`/`continue` in Schleifen

```
var i = 0
while (i < 10) {
    i = i + 1
    if (i == 3) { continue }
    if (i == 6) { break }
    print(i)
}
```

`break` beendet die **innerste** umschließende Schleife (`while`/`for`/
`foreach`) sofort; `continue` überspringt den Rest des aktuellen
Durchlaufs und springt zur nächsten Bedingungsprüfung – bei `for`
**nachdem** der Increment-Schritt noch ausgeführt wurde (sonst würde
`continue` die Schleifenvariable nie weiterzählen). Beide dürfen beliebig
tief in verschachtelten Blöcken/`if`s innerhalb des Schleifenkörpers
stehen; alle dazwischenliegenden Scopes werden dabei sauber geschlossen.

**Einschränkungen**:
- `break`/`continue` sind nur innerhalb einer Schleife gültig – außerhalb
  ist das ein Fehler.
- Eine verschachtelte Lambda "sieht" die Schleife einer umschließenden
  Funktion nicht – `break`/`continue` innerhalb einer Lambda sind nur
  gültig, wenn die Lambda SELBST eine Schleife umschließt.
- `break`/`continue` dürfen aus dem `try`- und den `catch`-Blöcken heraus verwendet werden: der
  Exception-Handler wird abgemeldet und ein vorhandenes `finally` läuft vor dem Sprung (bei mehreren
  verschachtelten `try` von innen nach außen). Nur aus dem `finally`-Block selbst heraus sind sie ein
  Fehler (eine Schleife IM `finally` darf natürlich `break`/`continue` benutzen).

### 5.11 Operator-Überladung

```
class Vector2 {
    float x
    float y

    construct(float x, float y) {
        this.x = x
        this.y = y
    }

    operator+(class other) {
        return new Vector2(this.x + other.x, this.y + other.y)
    }

    operator==(class other) {
        return this.x == other.x && this.y == other.y
    }
}

var a = new Vector2(1.0, 2.0)
var b = new Vector2(3.0, 4.0)
print(a + b)     // Vector2(4, 6)
print(a == b)    // false
```

Eine Klasse kann jeden der arithmetischen/bitweisen/Vergleichsoperatoren
(`+ - * / % ^ & | # << >> == != < <= > >=`) sowie den Index-Operator `[]`
überladen: `operator SYMBOL(params) { body }`. Intern rein syntaktischer
Zucker – jede Überladung wird zu einer ganz normalen Methode mit einem
speziellen, für Skript-Code selbst nicht als gewöhnlicher Bezeichner
schreibbaren Namen (`"operator+"`, `"operator=="`, ...) – Vererbung,
Überladung nach Parameteranzahl und alles andere an der bestehenden
Methoden-Infrastruktur funktioniert deshalb automatisch mit.

- **Alle Operatoren außer `[]`** erwarten genau **einen** Parameter (den
  rechten Operanden – `this` ist implizit der linke). Nur der **linke**
  Operand wird zur Laufzeit auf eine passende Überladung geprüft (kein
  Äquivalent zu C#s Überladung für vertauschte Operanden-Typen oder
  Pythons `__radd__`) – ist er kein Objekt, oder ein Objekt ohne passende
  Methode, greift die eingebaute Standard-Operation (bei `==`/`!=` z.B.
  Referenzgleichheit).
- **`[]` (Index-Operator)** ist ein Sonderfall: `operator[](int index) { ... }`
  (ein Parameter) überlädt den **lesenden** Zugriff (`arr[i]`),
  `operator[](int index, class value) { ... }` (zwei Parameter) den
  **schreibenden** (`arr[i] = wert`) – unterschieden rein über die
  Parameteranzahl, wie jede andere Methodenüberladung in dieser Sprache.
  `operator[]` ist dabei reines Parser-Sugar für eine bereits länger
  bestehende Namenskonvention: intern entstehen ganz normale Methoden
  namens `GetIndex`/`SetIndex`, die der Index-Zugriff (`OpCode.ArrayGet`/
  `ArraySet`) schon zuvor per Namenskonvention gesucht hat, falls das Ziel
  kein echtes Array, sondern ein Objekt ist – `operator[](...)` und
  `GetIndex(...)`/`SetIndex(...)` von Hand zu schreiben sind deshalb
  gleichbedeutend, ersteres nur die explizitere, empfohlene Schreibweise.

## 6. Prüf-Operatoren: `is in`, `is of`, `is from`

- **`wert is in einheit`** → `bool`. Prüft, ob `wert` (int/float/undefined) eine zu `einheit` dimensional kompatible Einheit trägt (siehe 3.4), unabhängig von Präfix/Skalierungsfaktor. Beispiel: `5mm is in m` → `true`, `5mm is in kg` → `false`.
- **`wert is of Typ`** → `bool`. Prüft die Typzugehörigkeit **rekursiv**: bei Basistypen einfacher Kind-Vergleich; bei `class`-Instanzen wird die Vererbungskette nach oben durchsucht (Instanz selbst oder eine ihrer Elternklassen entspricht `Typ`). Beispiel: `a is of float`. Auch ein **Interface** ist als Typ erlaubt (`wert is of IEnumerable`): wahr, wenn die Klasse (oder eine Basisklasse) es in `class X : IFoo` nennt; **Arrays und Puffer** erfüllen `IEnumerable`.
- **`objekt is from ownerAusdruck`** → `bool`. Prüft, ob der aktuelle Owner von `objekt` genau `ownerAusdruck` ist (direkter Owner-Vergleich). Beispiel: `obj is from objList`.
- **`objekt is under ownerAusdruck`** → `bool`. Wie `is from`, aber **transitiv**: prüft, ob `ownerAusdruck` irgendwo in der Ownership-Kette oberhalb von `objekt` liegt (direkter Owner, dessen Owner, usw., beliebig tief).

## 7. Exceptions

### 7.1 Basisklasse

Es gibt eine eingebaute Basisklasse `Exception` (mind. mit einer `message`-Eigenschaft), von der alle Exception-Klassen erben:

```
class Exception {
    string message

    construct(string message) {
        this.message = message
    }
}

class InvalidUnitException : Exception {
    construct(string message) : base(message) { }
}
```

`throw` erwartet einen Wert, der (direkt oder indirekt) von `Exception` abstammt (geprüft wie `is of`); andernfalls Laufzeitfehler bereits beim `throw` selbst.

### 7.2 Werfen

```
throw ausdruck;
```

### 7.3 Fangen

```
try {
    // ...
} catch (ErrorType e) {
    // nur wenn geworfener Wert Instanz von ErrorType oder einer Subklasse ist
} catch (e) {
    // ungetypter catch-all, fängt alles Übrige
} finally {
    // wird immer ausgeführt, auch bei uncaught exceptions vor der Weiterpropagierung
}
```

- Mehrere `catch`-Blöcke werden der Reihe nach geprüft; ein getypter `catch (Type name)` filtert per `is of`-Check, ein ungetypter `catch (name)` fängt alles. Der Typ steht - wie bei jeder Deklaration (`int x`) - VOR dem Namen.
- `finally` ist optional und läuft **immer**, auf jedem Weg, der den `try` verlässt: normal, nach einem `catch`, bei einer Exception, die an diesem `try` vorbeigeht (auch aus einem `catch`-Block heraus), bei `return`
  (auch im `try`/`catch`/in einem `foreach` darin), bei `break`/`continue` und bei `leave`/`terminate`. Der Block sieht die lokalen Variablen der Funktion. Ein `return` im `finally` ersetzt den Rückgabewert, eine `throw`
  darin ersetzt die ursprüngliche Exception; `break`/`continue` aus dem `finally` heraus sind ein Fehler. Der Rückgabewert eines `return` im `try` steht fest, bevor das `finally` läuft (ändert es die Variable, bleibt er).

### 7.4 `catch` ohne `try` – impliziter Block-Scope-Catch

`catch` kann auch **ohne vorangehendes `try`** stehen. Es wirkt dann wie ein impliziter `try`-Block, der an der Stelle des `catch` beginnt und bis zum Ende des aktuellen umgebenden Blocks reicht – alle danach im selben Block geworfenen Fehler werden von diesem `catch` behandelt:

```
{
    riskyStepOne()

    catch (e) {
        log(e.message)
    }

    riskyStepTwo()   // Fehler hier werden vom catch oben gefangen
    riskyStepThree()
} // Schutzbereich endet hier mit dem Blockende
```

### 7.5 Default-Verhalten & Resume

Exceptions sind ganz normale `class`-Instanzen (Subklassen von `Exception`) – Resume ist keine Sonderfunktion eines speziellen Typs, sondern eine eingebaute Methode (`resume()`, kleingeschrieben), die jede `Exception`-Instanz mitbringt. Da Exceptions normale Objekte sind, können sie wie jeder andere Wert weitergereicht, in Variablen gespeichert oder an andere Funktionen übergeben werden, bevor `resume()` aufgerufen wird.

- `throw ausdruck` ist sowohl als eigenständiges **Statement** (`throw new Foo();`) als auch als **Ausdruck** innerhalb eines größeren Ausdrucks nutzbar. An der Stelle, an der geworfen wird, entsteht ein **Resume-Punkt**.
- **Default:** Ohne aktiven `catch` bricht das Programm mit einer Fehlermeldung ab.
- **Mit Resume:** Ruft ein `catch`-Handler `e.resume(ersatzwert)` auf, wird die Ausführung *exakt am Resume-Punkt* fortgesetzt – der `throw`-Ausdruck wertet an dieser Stelle zu `ersatzwert` aus, der umgebende Code läuft normal weiter, als hätte dort nie eine Exception gestanden. Bei `throw` als reinem Statement wird `ersatzwert` einfach ignoriert und mit der nächsten Anweisung fortgefahren.
- `e.resume()` ohne Argument entspricht `e.resume(undefined)`.
- Da die Exception-Instanz bis zum Resume "lebt" (ihr Owner – typischerweise der werfende Scope – bleibt bestehen, solange der Stack an dieser Stelle offen ist), kann `resume()` auch von tiefer verschachteltem Code aufgerufen werden, dem die Exception-Instanz weitergereicht wurde.

### 7.6 Zusammenspiel mit Ownership

Beim Stack-Unwinding durch eine geworfene Exception werden alle verlassenen Scopes regulär aufgelöst wie beim normalen Verlassen eines Blocks: Objekte, deren Owner einer dieser Scopes ist, werden kaskadiert gelöscht (inkl. `destruct()`-Aufrufe), bevor die Exception weiterpropagiert bzw. im `catch` behandelt wird. Bei `resume()` unterbleibt das Unwinding entsprechend, da die Ausführung an der ursprünglichen Stelle fortgesetzt wird.

## 8. APIs, Bitbreiten, Pointer/unsafe, Arrays, Enumerables

### 8.1 Native Funktionen deklarieren (`extern`)

```
extern int MessageBoxW(int hwnd, string text, string caption, int type)
extern PlaySound(string path)   // kein Rückgabetyp -> liefert praktisch 'undefined'
```

`extern` deklariert nur die **Signatur** – kein Body. Der Name wird dadurch
aufrufbar bekannt. Es gibt zwei unabhängige Wege, wie ein tatsächlicher
Aufruf dann an eine echte native Implementierung kommt:

1. **Manuelle Host-Registrierung**: ein einbettender Host registriert den
   Namen zur Laufzeit über `Bytecode.ExternRegistry` mit einer eigenen
   C#-Implementierung (Signatur im Skript, Implementierung in C#).
2. **Dynamisches Linking gegen eine native Bibliothek** über die
   `#extern "libName"`-Direktive (Abschnitt 8.1.1) – ganz ohne Host-Code.

Ohne eine der beiden ist der Name zwar bekannt/aufrufbar-deklariert, ein
tatsächlicher Aufruf schlägt zur Laufzeit mit einem klaren "weder verlinkt
noch dynamisch geladen"-Fehler fehl – das Kompilieren selbst schlägt NIE
daran fehl (Deklaration und Verlinkung sind bewusst entkoppelt, wie bei
echtem "extern" in kompilierten Sprachen).

#### 8.1.1 `#extern "libName"` – dynamisches Linking

```
#extern "kernel32.dll"
extern int GetTickCount()

#extern "user32.dll"
extern int MessageBoxW(int hwnd, string text, string caption, int type)

print(GetTickCount())
MessageBoxW(0, "Hallo!", "Titel", 0)
```

`#extern "libName"` ist eine Präprozessor-Direktive (kein Ausdruck, kein
Statement mit Laufzeitwirkung) – sie legt fest, gegen welche native
Bibliothek **alle nachfolgenden** `extern`-Deklarationen im Quelltext
dynamisch gebunden werden, bis eine weitere `#extern`-Direktive das ändert.
Beim tatsächlichen Aufruf lädt die Laufzeit die Bibliothek (falls noch
nicht geschehen), sucht den Export unter genau dem deklarierten Namen und
ruft ihn mit den übergebenen Argumenten auf – ganz ohne Host-seitigen
C#-Code. Unterstützte Parameter-/Rückgabetypen: `bool`/`int`/`float`/
`char`/`string` sowie Pointer-Typen (als rohe native Adresse). Eine
manuelle Host-Registrierung (siehe oben) hat immer Vorrang vor dynamischem
Linking, falls beides für denselben Namen vorliegt.

Das eigentliche **Spiegeln** einzelner Betriebssystem-APIs (vorgefertigte
`extern`-Deklarationen für die komplette WinAPI/POSIX o.ä.) ist bewusst
NICHT Teil der Sprache selbst, sondern Aufgabe eines späteren
Bibliotheks-Frameworks, das auf `extern`/`#extern` aufbaut.

### 8.1.2 `#include "fileName"`

```
#include "shapes.script"

var c = new Circle(2.0)
```

Reine **textuelle Vorverarbeitung** vor dem eigentlichen Parsen (wie ein
klassischer C-Präprozessor): `#include "fileName"` wird durch den (rekursiv
ebenfalls vorverarbeiteten) Inhalt der referenzierten Datei ersetzt, relativ
zum Verzeichnis der includierenden Datei. Anders als bei C wird dieselbe
Datei insgesamt nur **einmal** eingefügt (spätere `#include`s derselben,
bereits eingefügten Datei werden stillschweigend übersprungen – wie
`#pragma once`) – das verhindert doppelte Klassen-/`extern`-Deklarationen,
wenn zwei Dateien dieselbe dritte Datei includieren. Zirkuläre Includes
werden erkannt und brechen mit einem klaren Fehler ab.

**Globale Komposition statt separater Einfüge-Bäume**: "insgesamt nur
einmal" gilt seit der Direktiven-Ausbaustufe (siehe 8.1.5) über die
GESAMTE Kompilierung hinweg, nicht nur innerhalb einer einzelnen Wurzel-
Datei. `Parser.ParseWithPrelude` (Prelude + Nutzer-Skript zusammen) teilt
deshalb EINE gemeinsame "bereits eingefügt"-Menge zwischen beiden Hälften
(`Preprocessor.Process(..., alreadyIncluded)`, dieselbe `HashSet<string>`-
Instanz für beide Aufrufe) - includiert sowohl die Prelude als auch das
Nutzer-Skript (direkt oder transitiv über eigene Includes) dieselbe Datei,
landet sie trotzdem nur EIN einziges Mal im kombinierten Programm, statt
einmal pro Seite. `#include` selbst ist dabei nur noch die eingebaute
Default-Registrierung des generischen Direktiven-Mechanismus (siehe 8.1.5),
keine Sonderbehandlung mehr im Rest des Preprocessors.

### 8.1.3 Timeout-fähige native APIs (`try Name(...)`)

```csharp
// Host-seitige C#-Registrierung:
natives.RegisterTryable("TryReadSensor", (Value[] args, out Value result) =>
{
    // Die eigentliche Timeout-/Fehler-Logik liegt komplett hier, im Host -
    // die Sprache selbst weiß nichts davon. args[0].Unit ist bereits
    // öffentlich lesbar, falls ein Timeout-Argument mit Einheit übergeben
    // wurde (z.B. "500ms").
    if (/* Zeitüberschreitung o.ä. */ false) { result = default; return false; }
    result = Value.MakeInt(42);
    return true;
});
```
```
var wert = try TryReadSensor(500ms)
if (wert == undefined) { print("Timeout oder Fehlschlag") }
```

Eine als "tryable" registrierte native Funktion (`Bytecode.NativeRegistry.
RegisterTryable`, Delegate-Typ `TryableNativeFunction`) ist NUR über
`try Name(...)` aufrufbar - ein direkter Aufruf `Name(...)` ohne `try` ist
ein Compile-Fehler. Bei Erfolg liefert der Ausdruck den Ergebniswert, bei
Fehlschlag/Timeout `undefined` - **keine Exception**: die eigentliche
Erfolg-/Fehlschlag-Entscheidung (Timeout, nicht verfügbare Hardware, o.ä.)
trifft ausschließlich die Host-Implementierung (typischerweise IO:
serielle Schnittstellen, Netzwerk, Dateien), über den Rückgabewert `bool`
ihres C#-Delegaten (`TryableNativeFunction`, dasselbe Grundmuster wie C#s
eigenes `TryParse` - Erfolg/Fehlschlag über `bool`, Ergebnis über `out`).

`try Name(...)` ist syntaktisch verwandt mit `try sync`/`try process`
(SPEC 3 - alle drei sind Ausdrücke, kein try/catch-Block), aber
eigenständig: `try sync`/`try process` liefern einen reinen `bool`
(Erfolg der Synchronisation selbst), `try Name(...)` dagegen den
TATSÄCHLICHEN Ergebniswert der nativen Funktion bei Erfolg. Der Parser
unterscheidet einen try/catch-Block von allen drei Ausdrucksformen rein
daran, ob direkt nach `try` eine `{` folgt (Block) oder nicht (Ausdruck).

### 8.1.4 Native Callbacks

**Wo ein Callback läuft.** `FireRuntime.RunCallback(lambda, args, natives, classes, snapshotGlobals, onUnhandled)` entscheidet:

- **Auf dem Thread einer laufenden VM** (der Normalfall: das Skript ruft selbst z.B. `Window.Tick`, und dabei feuern die Ereignisse) läuft das Lambda **verschachtelt auf dieser VM**
  (`VM.CallLambdaInline`): mit den **echten globalen Variablen**, lesend und schreibend, wie jedes andere Lambda (4.2) - es gibt nichts zu isolieren, weil nichts nebenläufig ist. Objekte mit
  Lambda-Feldern oder Verweisen auf fremde Objekte sind als Globals kein Problem, und `leave`/`terminate` im Callback wirken auf das Programm. Eine unbehandelte Exception im Callback bricht nur den
  Callback ab (ein `try`/`catch` um den auslösenden Aufruf sieht sie nicht): sie geht als Text an `onUnhandled`, das Programm läuft weiter. Wie bei Destruktoren und Properties wird während eines
  verschachtelten Callbacks nicht auf `leave`/`terminate` anderer Threads geprüft (erst danach).
- **Auf einem Thread ohne laufende VM** (ein Host-Thread, z.B. ein Seriell-Ereignis) wäre der Zugriff auf die Globals ein Datenrennen: der Callback wird deshalb dem Hauptprogramm **eingereiht**
  (`VM.PostCallback`, über `RunCallback(..., owner)`) und dort - automatisch an einem sicheren Punkt oder bei `sync globals`, mit `#nosync` nur dann - mit den echten Globals ausgeführt. Läuft das
  Hauptprogramm nicht (mehr), gilt die isolierte Kopie wie unten beschrieben (`FireRuntime.CallCallback`).

Der Rest dieses Abschnitts beschreibt diesen isolierten Fall.

```csharp
// Host-seitige C#-Registrierung (RegisterCallback ist eine GEWÖHNLICHE
// native Funktion - Lambdas sind bereits first-class Values, keine
// Sondersyntax nötig):
var callbacks = new Dictionary<string, LambdaValue>();
natives.Register("RegisterCallback", args =>
{
    callbacks[args[0].AsString()] = (LambdaValue)args[1].AsLambda();
    return Value.MakeUndefined();
});

// Wenn das native Event später (evtl. auf einem ANDEREN Thread) feuert:
var snapshot = vm.SnapshotGlobals();   // SYNCHRON, während das Skript nicht läuft
FireRuntime.CallCallback(callbacks["OnTick"], args, natives, classes, snapshot);
```
```
var counter = 0
RegisterCallback("OnTick", func(int n) => {
    counter = counter + n   // ändert nur die SNAPSHOT-Kopie, nicht das Original
})
```

Eine Callback-Lambda wird wie jede andere Lambda per Argument an eine
(gewöhnliche) native Funktion übergeben - `RegisterCallback` selbst ist
nichts Besonderes, nur eine normale, host-registrierte Funktion, die den
übergebenen `LambdaValue` irgendwo (z.B. einem `Dictionary`) für später
aufhebt. Die eigentliche Neuigkeit liegt auf der C#-Seite:

- **`VM.SnapshotGlobals()`**: nimmt einen Snapshot aller aktuellen Werte
  des globalen Scopes dieser VM auf - dieselbe Grundidee wie der interne
  Snapshot vor einem `fire`-Block, nur von AUSSEN (Host-Code) statt von
  einem Skript-Opcode ausgelöst. MUSS synchron aufgerufen werden, während
  diese VM-Instanz nicht gleichzeitig auf einem anderen Thread läuft (kein
  eingebautes Locking) - für den üblichen Fall (Registrierung direkt im
  Anschluss an den nativen Aufruf, während das Skript in genau diesem
  Aufruf steht) automatisch gegeben.
- **`FireRuntime.CallCallback(lambda, args, natives, classes, snapshot)`**:
  ruft die Lambda SYNCHRON auf dem aufrufenden (nativen) Thread auf einer
  FRISCHEN, eigenen VM-Instanz auf (`VM.CallLambdaEntry`) - anders als
  `Fire`/`FireVm` wird hier bewusst KEIN neuer Thread gestartet: der native
  Host-Code hat Aufruf-Zeitpunkt und -Thread bereits selbst bestimmt (z.B.
  ein .NET-Threadpool-Thread bei einem seriellen Port-Event), diese Methode
  reiht sich nur ein. Der neue globale Scope entsteht komplett aus dem
  `snapshot` (Objekte darin als isolierte Tiefenkopie, wie bei `taking`) -
  **niemals** der echte, geteilte globale Scope des Hauptprogramms, exakt
  wie gefordert. Eine unbehandelte Skript-Exception im Callback-Body
  schlägt NICHT nach außen in den nativen Aufrufer durch, sondern geht nur
  an einen optionalen `onUnhandled`-Callback.

Das deckt sich exakt mit der bestehenden Lambda-Semantik (SPEC 4.2,
`Runtime.LambdaValue`): eine Lambda sieht ohnehin IMMER nur ihren eigenen
(bei jedem Aufruf neu erzeugten) Scope plus den globalen Scope, nie
umgebende Locals irgendeiner Art - "der globale Scope" ist hier eben der
Snapshot statt des Originals, mehr Einschränkung braucht ein Callback nicht
zusätzlich. Der Snapshot-Zeitpunkt liegt bewusst bei der Registrierung
(bzw. wann auch immer der Host `SnapshotGlobals()` aufruft) - keine
automatische, fortlaufende Aktualisierung.

### 8.1.5 Frei definierbare Präprozessor-Direktiven

```
#greeting "Welt", 42
```
```csharp
// Host-seitige C#-Registrierung:
var registry = new DirectiveRegistry();  // oder DirectiveRegistry.CreateDefault() für '#include' obendrauf
registry.Register("greeting", 2, (ctx, args, line) =>
{
    Console.WriteLine($"Hallo {args[0].AsString()}, die Antwort ist {args[1].AsInt()}");
    return null; // keine Ersatz-Text-Ausgabe für diese Zeile
});
string preprocessed = Preprocessor.Process(source, basePath, registry);
```

Eine Präprozessor-Direktive hat die Form `#name wert1, wert2, ...` - der
NAME entscheidet, welcher (von Host-C#-Code per `DirectiveRegistry.
Register(name, paramCount, handler)` registrierte) Handler aufgerufen wird.
`paramCount` legt die ERWARTETE Anzahl Parameter fest - eine abweichende
Anzahl in einem tatsächlichen Aufruf ist ein klarer Compile-Fehler. Jeder
Parameter wird wie eine normale, literale Argumentliste gelesen (über den
echten Lexer tokenisiert): String, Ganzzahl/Fließkommazahl (optional mit
Einheiten-Suffix, z.B. `74mm`), Zeichen, `true`/`false`, `undefined`,
optional mit führendem `-`. Bewusst KEINE volle Ausdrucks-Grammatik (keine
Variablen, keine Operatoren außer dem Vorzeichen) - zum Zeitpunkt der
Vorverarbeitung, VOR dem eigentlichen Lexen/Parsen des restlichen Programms,
gibt es noch keine Variablen, die referenziert werden könnten.

Ein Handler bekommt die geparsten Werte (als `Values.Value`, dieselbe
Laufzeit-Werte-Repräsentation wie überall sonst in dieser Sprache) sowie
einen `DirectiveContext` (aktuelles Basisverzeichnis für relative Pfade,
eine Methode `ProcessFile(fullPath)` zum rekursiven Einschleusen einer
anderen Datei) und liefert den Text, der die Direktiven-Zeile in der
vorverarbeiteten Ausgabe ersetzt - leer/`null` bedeutet "nichts einfügen".

`#include` (siehe 8.1.2) ist die einzige EINGEBAUTE Direktive
(`DirectiveRegistry.CreateDefault()`) - technisch nichts Besonderes mehr,
nur eine Registrierung wie jede andere, mit 1 Parameter (dem Pfad als
String) und einem Handler, der die Zieldatei liest und rekursiv
vorverarbeitet. Eine `#name ...`-Zeile, deren Name NICHT registriert ist
(z.B. `#extern "libName"`, `#noshadow` - beide bleiben PARSER-Direktiven,
siehe SPEC 8.1/THREADING_DESIGN, da sie eine über mehrere nachfolgende
Statements hinweg wirkende STICKY Bedeutung haben, keine reine "ersetze
diese eine Zeile"-Semantik wie `#include`), wird unverändert durchgereicht -
der Präprozessor mischt sich nur in Direktiven ein, die er tatsächlich
kennt.

### 8.1.6 Gruppierte native Registrierung (`NativeRegistry.RegisterGroup`)

```csharp
var namen = registry.RegisterGroup("__GRPH", new Dictionary<string, NativeFunction>
{
    ["set"] = args => { /* ... */ return Value.MakeUndefined(); },
    ["get"] = args => { /* ... */ },
});
// namen == ["__GRPHset", "__GRPHget"]
```

Registriert mehrere zusammengehörige native Funktionen auf einmal, alle
unter demselben Namens-Präfix - der tatsächlich registrierte Name jedes
Eintrags ist `prefix + suffix`. Reines Komfort-/Namensschema für
abgeschlossene API-Gruppen (z.B. eine Grafik-/Konsolen-Brücke, siehe
`ScriptLang.Terminal.Bridge`, docs/CONSOLE.md) - entspricht mehreren
einzelnen `Register(...)`-Aufrufen, liefert zusätzlich die Liste der
erzeugten Namen zurück.

### 8.2 Bitbreiten für `int`/`float`

```
int[8] a        // 8 Bit
int[16] b       // 16 Bit
int c           // Default: höchste Genauigkeit (64 Bit)
float[32] Compute(int[16] x) { ... }   // auch bei Parametern/Rückgabetypen
```

Syntax: `[Bitbreite]` direkt hinter dem Basistyp (`int`/`float`), erlaubt
sind `8`, `16`, `32`, `64`. Ohne Angabe gilt die höchste Genauigkeit (64 Bit,
also int64 bzw. double). Diese Klammer steht bewusst direkt hinter dem *Typ*
– im Gegensatz zu Array-Klammern, die hinter dem *Bezeichner* stehen (siehe
8.4), dadurch gibt es keine Mehrdeutigkeit. Ein **Array-Rückgabetyp** (8.4.1)
hat keinen Bezeichner, hinter den die Klammern könnten: dort stehen *leere*
Klammern hinter dem Typ (`int[]`) - eine Bitbreite hat immer eine Zahl
(`int[8]`), das unterscheidet beide.

**Kopierverhalten:** Wird ein Wert in eine Variable/einen Parameter mit
geringerer deklarierter Bitbreite kopiert, wird abgeschnitten (`Value.TruncateTo`):
bei `int` klassische Zweierkomplement-Kürzung (Cast-Kette über
sbyte/short/int), bei `float` entsprechend über float (32 Bit) bzw.
IEEE754-binary16 (16 Bit, `System.Half`) bzw. ein dokumentiertes,
einfaches 8-Bit-Minifloat (kein verbreitetes Standardformat vorhanden, siehe
Kommentar bei `NumericWidth`). Der Mechanismus (`Value.Width` +
`Value.TruncateTo`) ist implementiert; die *automatische* Anwendung bei jeder
Zuweisung (der Resolver müsste dafür den deklarierten Typ jedes Slots
nachhalten) ist noch nicht verdrahtet – nächste Ausbaustufe.

### 8.3 Pointer & `unsafe`

```
unsafe {
    int[32]* p = &x
    var y = *p
    p = p + 1        // Pointer-Arithmetik
}
```

- `Type*` (ein oder mehrere `*`) ist ein Pointer-Typ, C#-artig direkt hinter
  dem (ggf. bitbreiten-qualifizierten) Basistyp.
- `&ausdruck` (Address-of) und `*ausdruck` (Dereferenzierung) sind Präfix-
  Operatoren, **nur innerhalb eines `unsafe { }`-Blocks gültig** (vom
  Resolver geprüft) – analog zu C#.
- Bei APIs zeigen Pointer auf die *unterliegenden Werte*, nicht auf die
  Objekte mit Unit/Width-Metadaten – diese Umrechnung passiert erst beim
  tatsächlichen API-Aufruf (Marshalling), nicht schon bei `&x` selbst.

**Implementiert** (siehe `docs/BYTECODE.md` Abschnitt für die Details): Ein
Pointer zeigt auf einen *existierenden, verwalteten Speicherort* – einen
Scope-Slot (lokale/globale Variable) oder ein Objekt-Feld – statt auf eine
rohe Speicheradresse. Das gibt echtes Aliasing (`*p = x` verändert
tatsächlich die Variable, auf die `p` zeigt) ohne ein eigenes
Byte-Speichermodell nachzubauen. `&` ist dadurch nur auf *adressierbare*
Ausdrücke anwendbar (Variablen, Objektfelder) – analog zu lvalues in C#,
nicht auf beliebige Zwischenwerte. "Pointer-Arithmetik" (`ptr + n`) bedeutet
entsprechend "n Elemente weiter" statt "n Bytes weiter". Die Umwandlung in
echte native Adressen für tatsächliche `extern`-Aufrufe (Marshalling in
einen gepinnten Puffer) ist bewusst zurückgestellt, bis `extern`-Linking
selbst ansteht.

### 8.4 Arrays

```
int arr[]                // Array unbestimmter Größe
int fixedArr[10]          // Array fester Größe
int matrix[][]            // mehrdimensional (Array von Arrays)
var a = new int[10]       // Array-Allokation
```

Die Array-Klammern stehen **hinter dem Bezeichner**, nicht hinter dem Typ
(bewusst anders als C#, wo `int[] arr` üblich ist) – dadurch keine Kollision
mit der Bitbreiten-Klammer, die hinter dem Typ steht. Mehrere `[...]`-Gruppen
hintereinander ergeben ein mehrdimensionales Array (Rang = Anzahl Gruppen).

`new Type[sizeExpr]` alloziert ein Array. Der Elementtyp hier ist bewusst nur
ein Basisname (ohne eigene Bitbreiten-Klammer, aus demselben
Kollisionsgrund) – eine bestimmte Elementbreite legt man stattdessen über den
deklarierten Variablentyp fest (`int[16] a = new int[10]`).

**Implementiert**: Laufzeit-Repräsentation (`Values.ScriptArray`, fest
allozierte `Value[]`, Elemente `undefined`-initialisiert), Indexzugriff
lesend/schreibend (`arr[i]`/`arr[i] = wert`, Opcodes `ArrayGet`/`ArraySet`),
Array-Literale (`[1, 2, 3]`, Opcode `MakeArrayLiteral`), Bounds-Checking
(ein Index außerhalb `0..Length-1` liefert eine fangbare
`IndexOutOfBoundsException`, siehe VM.ThrowIndexOutOfBounds - intern über
`ScriptArray.TryGet`/`TrySet`, ohne C#-Exception im Hot Path, siehe
docs/BYTECODE.md Abschnitt 18/19).
Mehrdimensionale Arrays sind bewusst NICHT eine eigene mehrdimensionale
Laufzeit-Repräsentation, sondern verschachtelte `ScriptArray`-Instanzen
("jagged arrays", wie in Java/C#).

**Array-Initialisierer**: eine `var`-Deklaration darf Array-Klammern UND
einen Array-Literal-Initializer gleichzeitig tragen:

```
var a[] = [1, 2, 3, 4]        // Größe implizit aus dem Literal (4)
var b[] : int = [1, 2, 3, 4]  // mit Typ-Annotation
var c[4] = [1, 2, 3, 4]       // explizite Größe, MUSS zur Literalgröße passen
```

Ist eine explizite Größe angegeben UND ist sie selbst ein Ganzzahl-Literal
(der häufige Fall), prüft der Resolver, dass sie mit der Anzahl der
Literal-Elemente übereinstimmt – ein Mismatch ist ein Compile-Fehler statt
eines stillschweigend andersgroßen Arrays. Eine dynamische Größe (Variable/
Ausdruck statt Literal) wird nicht geprüft.

Für `List` (siehe Prelude) gibt es dieselbe Idee über eine zweite
Konstruktor-Überladung: `new List([1, 2, 3, 4])` kopiert die Array-Elemente
einzeln über `Add()` in eine neue Liste.

### 8.4.1 Arrays als Rückgabetyp

```
class Kennel {
    int[] Numbers() { return [1, 2, 3] }
    Dog[] Dogs() { ... }
    string[][] Grid() { ... }        // mehrere Klammerpaare: Array von Arrays
    byte[] Bytes() { return "AB".ToBytes() }
    int[8][] Small() { ... }         // Array aus 8-Bit-Ganzzahlen (Bitbreite + leere Klammern)
}
interface IHolder { int[] Items() }
```

Eine Methode, ein Interface-Eintrag oder eine Property darf ein Array liefern: `Typ[] Name(...)`, mit **leeren**
Klammern hinter dem Typ. Nur dort - überall, wo ein Bezeichner da ist, bleiben die Klammern dahinter
(`int werte[]`); `int[] werte` als Feld, Parameter oder Variable ist ein Fehler mit genau diesem Hinweis, ebenso
ein Array-Rückgabetyp bei `extern` (die native Schnittstelle kennt keine Skript-Arrays). Wie jeder Rückgabetyp wird
er nicht zur Laufzeit erzwungen, der Resolver prüft nur, dass der Typname existiert; der Editor nutzt ihn für die
Typ-Herleitung (`k.Dogs()[0].` schlägt die Mitglieder von `Dog` vor, `k.Numbers().` `Length`).

### 8.5 `IEnumerable`/`IEnumerator` & `interface`

**Implementiert.** Da `IEnumerable`/`IEnumerator` von mehreren, sonst
unabhängigen Klassen implementierbar sein müssen (z.B. soll `List`
enumerable sein können, während andere Klassen ganz andere Basisklassen
haben), reicht die bestehende Single-Inheritance dafür nicht aus. Dafür
gibt es das schlanke **`interface`-Konstrukt** zusätzlich zu `class`:

```
interface IEnumerator {
    bool MoveNext()
    class GetCurrent()
}

interface IEnumerable {
    IEnumerator GetEnumerator()
}

class List : IEnumerable {
    // ...
    IEnumerator GetEnumerator() { ... }
}
```

- Eine Klasse hat weiterhin höchstens **eine** Basisklasse (`class Foo : Bar`),
  kann aber zusätzlich **mehrere** Interfaces implementieren
  (`class Foo : Bar, IEnumerable, IComparable`) – das Vererbungsmodell bleibt
  single, nur Interfaces sind mehrfach implementierbar.
- Ein `interface` deklariert nur Methodensignaturen (keine Felder, kein
  Konstruktor), ähnlich `extern`, nur eben für klasseninterne Verträge statt
  native Funktionen.
- **Arrays und Byte-Puffer sind `IEnumerable`:** `arr.GetEnumerator()` liefert einen Enumerator (`ListEnumerator`), `arr is of IEnumerable` ist wahr, und alles, was eine `IEnumerable` verarbeitet (`foreach`, `Linq.From`, eigene Methoden), nimmt sie an.
- `foreach (x in collection)` (SPEC 5) läuft über `GetEnumerator()`/
  `MoveNext()`/`GetCurrent()` – rein per NAMENS-Dispatch, funktioniert also
  auch auf jeder anderen Klasse mit denselben drei Methoden, nicht nur auf
  `IEnumerable`-Instanzen im formalen Sinn. Auch ein **Array** (`[1, 2, 3]`,
  `new string[3]`) und ein **Byte-Puffer** sind direkt durchlaufbar
  (`foreach (x in arr)`) - die VM liefert dafür einen `ListEnumerator` der
  Prelude (ohne Prelude bleibt es bei einem Fehler).
- `IEnumerable`/`IEnumerator`/`List`/`ListEnumerator` sowie
  `IndexOutOfBoundsException` sind Teil der **Prelude**
  (`Standard/Prelude.cs`) – bewusst in ScriptLang selbst geschrieben statt
  nativ in C#, da die Sprache dafür längst genug Substanz hat (Klassen,
  Arrays, Interfaces), und wird jedem Programm automatisch vorangestellt
  (`Parser.ParseWithPrelude`). `List` nutzt intern ein Array fester Größe,
  das bei Bedarf verdoppelt wird (klassisches dynamisches Array); ein
  ungültiger Index wirft `IndexOutOfBoundsException` (von der VM selbst
  konstruiert, siehe `VM.ThrowIndexOutOfBounds`), ganz normal per `try`/
  `catch` fangbar.

### 8.6 `readonly` (Konstanten)

```
readonly var PI = 3.14159
readonly int MAX_SIZE = 100

class Circle {
    readonly float radius

    construct(float radius) {
        this.radius = radius
    }
}
```

`readonly` ist ein optionales Präfix vor jeder Variablen- oder Feld-
Deklaration. Eine `readonly`-Variable (lokal oder global) MUSS einen
Initializer haben (oder eine Array-Größe, `readonly int arr[10]`) und kann
danach nie wieder zugewiesen werden – jede spätere `=`-Zuweisung ist ein
Compile-Fehler. Ein `readonly`-Feld ist nur innerhalb eines Konstruktors
**der deklarierenden Klasse selbst** per `this.feld = ...` zuweisbar (nicht
in anderen Methoden derselben Klasse, nicht in abgeleiteten Klassen, nicht
von außen) – damit lassen sich pro Instanz einmalig gesetzte, danach
unveränderliche Felder ausdrücken (wie C#s `readonly`).

**Bekannte Grenze**: die Prüfung ist rein statisch und bezieht sich nur auf
den lexikalisch erkennbaren Fall `this.feld = ...`. Eine Zuweisung über ein
dynamisches Ziel (`obj.feld = ...`, wobei `obj` ein beliebiger Ausdruck ist)
wird NICHT erkannt, da die Sprache kein statisches Typsystem hat, das zur
Compile-Zeit wüsste, von welcher Klasse `obj` eine Instanz ist – eine echte
Laufzeit-Absicherung dafür ist eine mögliche spätere Ausbaustufe.

### 8.7 `enum`

```
enum Color { Red, Green, Blue }
enum Status {
    Active = 10,
    Inactive,   // 11 - Auto-Increment vom Vorgänger
    Paused = 20,
    Done        // 21
}

print(Color.Red)     // 0
print(Status.Paused)  // 20
```

Ein `enum` ist eine reine Compile-Zeit-Konstantenliste – `Name.Mitglied`
löst der Compiler direkt zu einem Int-Literal auf, es gibt **keine eigene
Laufzeit-Repräsentation** (kein eigener Wert-Typ, keine Instanzen). Ohne
expliziten Wert bekommt ein Mitglied den Wert des Vorgängers + 1 (0 beim
ersten Mitglied) – klassisches C-artiges Auto-Increment. Ein expliziter Wert
muss ein Int-Literal sein (keine beliebigen Ausdrücke, da diese Sprache
keine allgemeine Compile-Zeit-Konstantenauswertung hat).

Da ein Enum-Wert schlicht ein `int` ist, verhält er sich auch so:
`Color.Red is of int` ist wahr, Vergleiche/Arithmetik funktionieren normal,
aber es gibt keine eigenständige `is of Color`-Prüfung (kein Typ `Color` zur
Laufzeit) und keine automatische Namens-Darstellung beim Ausgeben
(`print(Color.Red)` zeigt `0`, nicht `"Red"`).

### 8.8 Properties

```
class Circle {
    float radius

    construct(float radius) {
        this.radius = radius
    }

    float Diameter {
        get { return this.radius * 2 }
        set { this.radius = value / 2 }
    }

    float Area {
        get { return this.radius * this.radius * 3 }   // kein 'set' -> nur lesbar
    }
}

var c = new Circle(5.0)
print(c.Diameter)      // 10 - ruft den Getter
c.Diameter = 20.0       // ruft den Setter, 'value' ist implizit 20.0
print(c.radius)         // 10
```

C#-artige Properties: `Typ Name { get { ... } set { ... } }`, mindestens
eines von `get`/`set` muss vorhanden sein (eine reine Lese-Property lässt
`set` weg, eine reine Schreib-Property lässt `get` weg). Im Setter-Body
steht `value` implizit für den zugewiesenen Wert, wie in C#. `get`, `set`
und `value` sind bewusst KEINE reservierten Schlüsselwörter - rein
kontextabhängige Bezeichner, nur innerhalb eines Property-Bodies (bzw. des
Setter-Bodies für `value`) mit Sonderbedeutung; anderswo im Programm
weiterhin als normale Namen (Variablen, Methoden, ...) verwendbar.

#### Auto-Properties

```
class Person {
    string Name { get; set; }
    int Age { get; }

    construct(string name, int age) {
        this.Name = name
        this._AutoAge = age   // Backing-Field direkt, da 'Age' keinen Setter hat
    }
}
```

`get;`/`set;` (ohne eigenen Body, mit `;` statt `{ ... }`) sind eine
Kurzform, die automatisch ein **Backing-Field** samt trivialem Getter/
Setter erzeugt - reiner Parser-Zucker, komplett zu genau der Form
desugart, die man auch von Hand schreiben würde. Das Backing-Field heißt
`_AutoName` (`_Auto` + Property-Name) und ist ein **ganz normales Feld**
der Klasse, ohne Sonderstatus - Code innerhalb der Klasse (auch der
Konstruktor) kann jederzeit direkt darauf zugreifen (`this._AutoName`),
zum Beispiel um eine get-only Auto-Property trotzdem zu initialisieren, da
sie über die Property selbst ja keinen Setter hat. Explizite und
automatische Accessor dürfen gemischt werden (`get { ... } set;`). Da das
Backing-Field per Namenskonvention entsteht, nicht durch einen garantiert
eindeutigen Mechanismus: ein selbst deklariertes Feld mit demselben Namen
(`_AutoName`) würde kollidieren - wird nicht eigens geprüft, einfach
vermeiden (das `Auto`-Infix macht eine zufällige Kollision mit einem
"normalen" `_name`-Feld deutlich unwahrscheinlicher als ein reiner
Unterstrich-Präfix).

**Auflösung ist rein namensbasiert** (wie `[]`-Operator-Überladung per
`GetIndex`/`SetIndex`, SPEC 8.4): eine Property `Name` erzeugt intern zwei
gewöhnliche Methoden `get_Name`/`set_Name`. `obj.Name` (lesend) bzw.
`obj.Name = x` (schreibend) versuchen zuerst ein ECHTES Feld namens `Name` -
nur wenn keines existiert, wird `get_Name()`/`set_Name(x)` aufgerufen. Eine
Property braucht deshalb praktisch immer ein ANDERS benanntes Feld als
Backing-Store (`radius`, nicht `Diameter`) - ein Feld und eine Property mit
demselben Namen wären sonst mehrdeutig (das Feld gewinnt).

**Fehlerfälle**: Zuweisung an eine reine Lese-Property (`get` ohne `set`)
ist ein klarer Laufzeitfehler (keine stillschweigende Feld-Erzeugung - das
würde die Property sonst dauerhaft durch ein gleichnamiges Feld
überschatten). Lesen einer reinen Schreib-Property ist ebenso ein klarer
Fehler.

**Keine Auto-Properties**: anders als C#s `{ get; set; }`-Kurzform (ohne
Body, mit implizitem Backing-Feld) muss hier IMMER ein echter Body
angegeben werden - ein Feld allein deckt den "einfachen Fall ohne eigene
Logik" schon ab, eine automatisch generierte Backing-Feld-Variante würde
dafür wenig zusätzlichen Wert bringen.

### 8.9 Format-Strings (`$"..."`)

```
var name = "Welt"
var x = 255
print($"Hallo, {name}!")                    // Hallo, Welt!
print($"Hex: {x:X}, Binär: {x:B}")          // Hex: FF, Binär: 11111111
print($"Gepolstert: {x:D5}")                // Gepolstert: 00255
print($"Pi ≈ {3.14159:F2}")                 // Pi ≈ 3.14
print($"Escapte Klammern: {{wie diese}}")   // Escapte Klammern: {wie diese}
```

Ein `$"..."`-String funktioniert wie ein normaler String-Literal, darf aber
`{ausdruck}` bzw. `{ausdruck:Format}` enthalten - zur Laufzeit durch den
(ggf. formatierten) Wert des Ausdrucks ersetzt. `{{`/`}}` erzeugen eine
literale `{`/`}`. Innerhalb von `{...}` ist JEDER Ausdruck erlaubt (auch
Methodenaufrufe, Objektfelder, weitere String-Literale, ...), nicht nur
einfache Variablennamen.

Ein `{...}`-Abschnitt wird eigenständig neu gelext und geparst (nicht Teil
der äußeren Grammatik) - dadurch braucht diese Funktion keinen eigenen,
parallelen Ausdrucks-Parser. Daraus folgt eine Einschränkung: ein `:`
INNERHALB des Ausdrucks selbst (z.B. die Einheiten-Koersion `x : km`) wird
NUR erkannt, wenn er innerhalb einer eigenen Klammerung steht - ein
`:` auf oberster Ebene trennt immer Ausdruck und Format-Spezifizierer.
Braucht der Ausdruck selbst einen ungeklammerten `:`, muss er geklammert
werden: `{(x : km)}` statt `{x : km}`.

**Format-Spezifizierer** (nach einem `:`, optional):

| Spezifizierer | Bedeutung | Typ | Beispiel |
|---|---|---|---|
| `X`/`x` | Hexadezimal (Groß-/Kleinbuchstaben) | `int` | `{255:X}` → `FF` |
| `B` | Binär | `int` | `{5:B}` → `101` |
| `D` | Dezimal, mit `0` aufgefüllt | `int` | `{5:D3}` → `005` |
| `F` | Festkomma | `int`/`float` | `{3.14159:F2}` → `3.14` |
| `E` | Wissenschaftliche Notation | `int`/`float` | `{1234.5:E2}` → `1.23E+003` |

Alle außer `B` reichen den Spezifizierer direkt an .NETs eingebaute
Zahlenformat-Strings durch (u.a. deshalb auch eine optionale Breiten-/
Nachkommastellenangabe direkt am Buchstaben: `X4`, `F2`, `D5`, ...); `B`
(binär) wird von Hand gebaut, da .NET kein natives Binärformat kennt. Ein
Format-Spezifizierer auf einem Wert vom falschen Typ (z.B. `X` auf einem
`float`) oder ein unbekannter Spezifizierer ist ein klarer Laufzeitfehler.

**Implementierung**: `$"..."` wird vom Lexer in Text-/Ausdrucks-Segmente
zerlegt (`Lexing.InterpolationSegment`), zu einem `InterpolatedStringExpr`
geparst und zu einer Kette normaler `+`-Verkettungen kompiliert (nutzt die
bestehende String-Konkatenation aus `Value.Add`), ein Format-Spezifizierer
wandelt seinen Wert vorher über einen eigenen Opcode (`FormatValue`,
`Value.Format`) explizit um, statt sich auf `ToString()` zu verlassen.

### 8.10 Byte-Puffer (`new byte[n]`) & String/Char-Konvertierung

```
var buf = new byte[4]      // Values.ByteBuffer, NICHT ScriptArray
buf[0] = 0x48
buf[1] = 0x69
print(buf.ToString())      // "Hi" (ASCII-Dekodierung)

var bytes = "Hi".ToBytes()             // string -> buffer (ASCII, 1 Byte/Zeichen)
var wide = "Hi".ToUnicode(2)           // string -> buffer (fest 2 Byte/Zeichen)
print(wide.ToUnicode())                // "Hi" (mit derselben Breite dekodiert)
```

Ein `byte`-Puffer ist ein eigener Laufzeit-Typ (`Values.ByteBuffer`,
`ValueKind.Buffer`) - bewusst GETRENNT von `ScriptArray` (das boxte
`Value[]`-Elemente hält, mehrere Byte Overhead pro Element): ein
`ByteBuffer` ist ein echtes, kompaktes `byte[]`, gedacht für Binärdaten aus
IO (seriell, Netzwerk, Dateien).

`byte` als **skalarer Typ** (z.B. `byte b = 5`) ist dagegen KEIN
eigener `ValueKind`, sondern reines Parser-Sugar für `int[8]` (eine
explizite Bitbreite direkt nach `byte` ist deshalb ein Fehler, sie wäre
redundant) - ein einzelnes Element eines Puffers (`buf[i]`) ist also
schlicht ein ganz normaler `int`-Wert mit dieser Breite, keine
Sonderrepräsentation.

**`new byte[n]`** erzeugt den Puffer (bewusst nur eindimensional - anders
als bei normalen Arrays gibt es kein `new byte[n][m]`), mit `n` auf `0`
initialisierten Bytes und der Host-Byte-Order als Default (siehe unten).
Indexzugriff lesend/schreibend funktioniert wie bei Arrays (`buf[i]`/
`buf[i] = wert`, dieselben Opcodes `ArrayGet`/`ArraySet`, dieselbe fangbare
`IndexOutOfBoundsException` bei ungültigem Index). `buf.length` (int) und
`buf.littleEndian` (bool) sind lesbare Felder.

**Konvertierungsmethoden** (Methodenaufrufe auf primitiven Werten - siehe
"Primitive Methoden" unten):

| Aufruf | Ergebnis | Bedeutung |
|---|---|---|
| `string.ToBytes()` | `buffer` | ASCII, 1 Byte/Zeichen (niedrigstes Byte des `char`-Werts) |
| `char.ToByte()` | `byte` (= `int[8]`) | dito, ein einzelnes Zeichen |
| `buffer.ToString()` | `string` | ASCII-Dekodierung, 1 Zeichen/Byte |
| `byte.ToChar()` | `char` | dito, ein einzelnes Byte (niedrigste 8 Bit des `int`-Werts) |
| `string.ToUnicode(int len)` | `buffer` | feste Breite `len` Byte/Zeichen (typisch 2, "Unicode" im Windows-Sinn/UTF-16-Breite) |
| `char.ToUnicode(int len)` | `buffer` | dito, ein einzelnes Zeichen |
| `buffer.ToUnicode()` / `buffer.ToUnicode(int len)` | `string` | Dekodierung mit fester Breite (Default `len=2`, wenn weggelassen) |
| `buffer.ToUnicodeChar()` / `buffer.ToUnicodeChar(int len)` | `char` | dito, liest nur die ERSTEN `len` Byte |

"Unicode" meint hier ausdrücklich NICHT eine vollständige Unicode-Bibliothek
(keine Normalisierung, keine Surrogatpaar-Behandlung) - da ein `char`
dieser Sprache ohnehin nur eine einzelne 16-Bit-Codeeinheit ist, ist die
Kodierung schlicht "jedes Zeichen als `len` Byte seines Ordinalwerts,
gemäß der aktuellen Byte-Order" (frei wählbare Breite statt eines fest
codierten UTF-16/UTF-32). `ToBytes`/`ToUnicode(len)` erzeugen den Puffer
immer mit der HOST-Byte-Order.

**Endianness**: `ByteBuffer` trägt eine `ByteOrder` (Little/Big) - reine
Metadaten, die gespeicherten Bytes selbst ändern sich nie von allein.
`buffer.ToLittleEndian()`/`buffer.ToBigEndian()` liefern eine Kopie: stimmt
die Ziel-Order schon mit der aktuellen überein, eine unveränderte Kopie
(kein Byte-Swap); sonst eine mit dem GESAMTEN Puffer als EINEM
zusammenhängenden Block gespiegelte Kopie (nicht Element für Element in
fester Breite - für ein einzelnes Mehrbyte-Feld, z.B. ein 4-Byte-`int`, ist
das exakt die richtige Bedeutung). Eine explizite Order bei der Erzeugung
gibt es bewusst nicht als eigene Syntax - `new byte[n].ToLittleEndian()`
erreicht dasselbe direkt im Anschluss.

Die HOST-Byte-Order selbst wird zur LAUFZEIT ermittelt (kein Compiler-Flag,
keine Annahme über die Zielplattform): ein 16-Bit-Wert wird geschrieben,
dann geprüft, ob das niedrigstwertige Byte im Speicher zuerst liegt
(`Values.ByteConversions.HostByteOrder`) - derselbe klassische Laufzeit-
Test, den auch eine spätere C++-Portierung dieser VM verwenden würde
(dort typischerweise über einen rohen Zeiger-Cast statt `BitConverter`).

**Primitive Methoden**: `CallMethod` (der Opcode hinter jedem
`ziel.Methode(...)`-Aufruf) akzeptiert jetzt NEBEN Objektinstanzen auch
String/Char/Int/Buffer-Werte als Ziel - dafür wird zuerst eine feste Liste
eingebauter Methoden geprüft (siehe VM.TryCallBuiltinMethod), bevor (nur
bei einer ObjectInstance) der normale, dynamische Methoden-Dispatch über
eine RuntimeClass greift. Diese Konvertierungen sind deshalb ganz normale
Methodenaufrufe, keine Operatoren/Sondersyntax - `SPEC 5.11`s Operator-
Überladung bleibt davon unberührt (unterschiedliche Opcodes: `CallMethod`
hier, `BinaryNumericOrOperator` dort).

### 8.11 Streams und Dateizugriff (`#import "io"`)

`#import "io"` schaltet den Namespace `IO` frei (Bridge `fire.IO.Bridge`, wie `graphics`/
`devices`: native Funktionen `__IO...` plus ein fire-Prelude). Alles liegt in `namespace IO`,
damit es nicht mit eigenen Klassen wie `File` oder `Stream` kollidiert; ein Enum in einem
Namespace ist nur **vollqualifiziert** erreichbar (`IO.FileMode.Create`).

Aufbau: Streams (`IO.FileStream`, `IO.MemoryStream`, eigene Streams), Datei-/Verzeichnis-API
(`IO.File`, `IO.Directory`, `IO.Path`, `IO.Utf8`), Text (`IO.TextReader`, `IO.TextWriter`) und
Standardein-/-ausgabe (`IO.Stdio`) - alles unten beschrieben.

```
#import "io"

var w = new IO.FileStream("out.bin", IO.FileMode.Create)   // ohne access: Open->Read, Append->Write, sonst ReadWrite
w.Write("Hallo".ToBytes())          // Write(buffer) / Write(buffer, offset, count) -> Anzahl Bytes
w.WriteByte(33)
w.Close()                           // ein zweites Close() ist wirkungslos

var r = new IO.FileStream("out.bin")             // IO.FileMode.Open, IO.FileAccess.Read
var head = r.ReadBytes(3)           // bis zu 3 Bytes als neuer Puffer (kürzer am Ende)
var b = r.ReadByte()                // 0..255, -1 am Ende
r.Seek(-1, IO.SeekOrigin.End)       // -> neue Position;  r.Position = 0 geht auch
var rest = r.ReadAll()              // alles bis zum Ende als Puffer
```

| Mitglied | Bedeutung |
|---|---|
| `Read(buffer[, offset, count])` | liest in einen Puffer, liefert die Anzahl (0 = Ende) |
| `Write(buffer[, offset, count])` | schreibt aus einem Puffer, liefert die Anzahl |
| `ReadByte()` / `WriteByte(v)` | einzelnes Byte (`ReadByte` -1 am Ende) |
| `ReadBytes(n)` / `ReadAll()` / `CopyTo(ziel)` | Hilfen, aufgebaut auf `Read`/`Write` |
| `Position`, `Length` | Property (lesen/setzen); nur bei `CanSeek` |
| `Seek(offset, origin)` | `IO.SeekOrigin.Begin/Current/End`, liefert die neue Position |
| `CanRead`/`CanWrite`/`CanSeek`, `IsClosed` | Fähigkeiten |
| `Flush()`, `Close()` | |
| `MemoryStream.ToBuffer()` | der gesamte Inhalt als Puffer; `new IO.MemoryStream(buffer)` startet mit einer Kopie |
| `FileStream.Name` | der Pfad, wie angegeben |

`IO.FileMode`: `Open` (muss existieren), `Create` (anlegen/leeren), `CreateNew` (muss neu
sein), `OpenOrCreate`, `Append`. `IO.FileAccess`: `Read`, `Write`, `ReadWrite`.

**Datei- und Verzeichnis-API.** Statische Methoden, immer mit dem Namespace geschrieben
(`IO.File.Exists(...)`, ein statischer Zugriff wird nur über den exakt geschriebenen Namen
aufgelöst).

```
var f = IO.Path.Combine("daten", "notizen.txt")
IO.Directory.Create("daten")
IO.File.WriteAllText(f, "Grüße\nzweite Zeile")
foreach (line in IO.File.ReadAllLines(f)) { print(line) }   // eine List von Strings (.count, [i])
print(IO.File.Size(f) + " Bytes, geändert " + IO.File.ModifiedTime(f))   // Sekunden seit 1970 mit Einheit s
IO.File.Copy(f, "backup.txt", true)
foreach (name in IO.Directory.GetFiles("daten", "*.txt")) { print(IO.Path.FileName(name)) }
```

| Klasse | Methoden |
|---|---|
| `IO.File` | `Exists`, `Size`, `ModifiedTime`, `Delete` (eine fehlende Datei ist kein Fehler), `Copy(quelle, ziel, overwrite = false)`, `Move(quelle, ziel, overwrite = false)`, `ReadAllBytes`, `WriteAllBytes`, `AppendAllBytes`, `ReadAllText`, `WriteAllText`, `AppendAllText`, `ReadAllLines` (liefert eine `List`), `WriteAllLines` (List oder Array) |
| `IO.Directory` | `Exists`, `Create` (auch Zwischenverzeichnisse, ein vorhandenes ist kein Fehler), `Delete(pfad, recursive = false)`, `GetFiles(pfad, pattern = "*", recursive = false)`, `GetDirectories(...)` (vollständige Pfade als sortierte `List`), `Current()` |
| `IO.Path` | `Combine(a, b[, c])` (ein absoluter Teil verwirft alles davor), `FileName`, `Stem`, `Extension` (mit Punkt), `Parent`, `FullPath`, `Temp()`, `IsRooted`, `Separator()` |
| `IO.Utf8` | `GetBytes(text)`, `GetString(buffer[, offset, count])` |

Text ist **UTF-8** (`string.ToBytes()` ist dagegen nur ASCII): geschrieben ohne Byte-Order-Mark,
gelesen mit Entfernung eines BOM, ungültige Folgen werden zu U+FFFD. `WriteAllLines` schließt jede
Zeile mit `\n` ab, `ReadAllLines` erkennt `\n`, `\r\n` und `\r` (ein abschließender Umbruch
erzeugt keine leere letzte Zeile). Jeder Pfad geht vor dem Zugriff durch die `IoPolicy` (Lesen:
`Exists`/`Size`/`ModifiedTime`/`Copy`-Quelle; Schreiben: `Create`/`Copy`-Ziel/`Move`-Ziel;
Löschen: `Delete`/`Move`-Quelle; Auflisten: `GetFiles`/`GetDirectories`); ein abgelehnter Zugriff
ist `IO.PermissionException` (Code 6), auch bei `Exists` - eine Abfrage darf nicht verraten, was
es außerhalb des erlaubten Bereichs gibt. `IO.Path` selbst ist reine Textverarbeitung ohne
Dateizugriff. `Copy`/`Move` auf ein vorhandenes Ziel ohne `overwrite` wirft
`IO.FileExistsException`, ein fehlendes Verzeichnis `IO.DirectoryNotFoundException`.

**Text (`IO.TextReader`, `IO.TextWriter`).** UTF-8, zeilenweise, auf Dateien oder beliebigen Streams:

```
var out = new IO.TextWriter("log.txt")            // überschreibt; ("log.txt", true) hängt an
out.WriteLine("Grüße")
out.Write("Wert: ")
out.Write(42)                                      // Zahlen usw. werden als Text geschrieben
out.Close()

foreach (zeile in new IO.TextReader("log.txt")) { print(zeile) }   // Zeile für Zeile

var reader = IO.File.OpenText("log.txt")           // auch CreateText / AppendText
var erste = reader.ReadLine()                      // undefined am Ende
var rest = reader.ReadAll()                        // der Rest als ein String
reader.Close()
```

`new IO.TextReader(quelle[, leaveOpen])` / `new IO.TextWriter(ziel[, flag])`: bei einem **Pfad**
öffnen sie die Datei selbst (Writer: `flag` = append); bei einem **Stream** (`IO.IStream`) lesen/
schreiben sie darauf und **schließen ihn mit**, außer `leaveOpen`/`flag` ist true. Zeilen enden mit
`\n` oder `\r\n` (das `\r` gehört nicht zur Zeile), geschrieben wird `\n`. `TextReader`: `ReadLine()`,
`ReadAll()`, `ReadLines()` (List), `EndOfStream`, `foreach`. Beide schließen sich in `destruct()`
und werfen `IO.StreamClosedException`, wenn man nach `Close()` weitermacht.

**Standardein-/-ausgabe (`IO.Stdio`).** `IO.Stdio.Write(x)`, `WriteLine(x)`, `ErrorWrite(x)`,
`ErrorLine(x)`, `Flush()`, `ReadLine()` (undefined am Ende), `ReadAll()`; `In()`/`Out()`/`Err()`
liefern sie als Stream (z.B. `new IO.TextWriter(IO.Stdio.Out(), true)`), `Close()` darauf ändert
nichts. **Wohin** das führt, bestimmt der Host: die echte Konsole (Vorgabe, `IoStdio.SystemConsole`)
oder Rückruffunktionen (`IoStdio.Custom(ausgabe, fehler, eingabe)` - der Editor leitet sie in sein
Ausgabefenster, die Eingabe ist dort leer). Ausgabe läuft immer als UTF-8; beim `Custom`-Ziel wird
zeilenweise weitergegeben, eine unvollständige Zeile bleibt bis zum Umbruch oder `Flush()` liegen.
`ReadLine`/`ReadAll` lesen gepuffert - nicht mit rohen Lesezugriffen auf `In()` mischen. (Anders als
`print` gibt es hier keinen automatischen Zeilenumbruch.)

**Aufräumen:** `NativeStream.destruct()` schließt das Handle, wenn der Besitzer-Scope endet
(siehe 2 und 5.3) - ein vergessenes `Close()` bleibt nicht offen.

**Eigene Streams:** `IO.IStream` (`Read`, `Write`, `Flush`, `Close`) ist die kleinste
Schnittstelle; bequemer leitet man von `IO.Stream` ab, überschreibt `Read`/`Write` (und
`CanRead`/`CanWrite`/`Position`/... was unterstützt wird) - `ReadByte`, `ReadBytes`, `ReadAll`,
`CopyTo` funktionieren dann automatisch.

**Fehler** sind fangbare Exceptions, alle von `IO.IOException` (Felder `message`, `code`):
`IO.FileNotFoundException` (3), `IO.DirectoryNotFoundException` (4), `IO.FileExistsException`
(7), `IO.StreamClosedException` (2), `IO.PermissionException` (5 = Betriebssystem, 6 =
Richtlinie des Hosts), sonst `IO.IOException` (1 ungültiges Argument, 8 nicht unterstützt,
9 sonstiges). Die Namen vermeiden bewusst `AccessDeniedException`, das die VM selbst für
Zugriffsmodifikatoren wirft.

**Sicherheit: der HOST entscheidet.** Das Skript kann nichts einschränken oder aufweichen:
`RuntimeSession.Build(..., ioPolicy)` bekommt eine `IoPolicy` (`AllowAll` = Vorgabe, `DenyAll`,
`Rooted(verzeichnis, readOnly)` oder eine eigene Ableitung). Jeder Pfad wird vor dem Öffnen
vollständig normalisiert (`Path.GetFullPath`, also ohne `..`) geprüft; ein abgelehnter Zugriff
wird zu `IO.PermissionException`. Symbolische Links werden nicht aufgelöst (ein Link innerhalb
eines erlaubten Verzeichnisses, der nach außen zeigt, führt heraus). `MemoryStream` ist von der
Richtlinie nicht betroffen.

**Threads:** die Handle-Tabelle ist nebenläufigkeitssicher, jeder Zugriff auf einen Stream ist
gesperrt; der Fehlerstatus (`__IOLastError`) gilt pro Thread. Lesen blockiert den aufrufenden
VM-Thread (für Hintergrundarbeit `fire { ... }`).

### 8.12 Strings und Zeichen: `Length`, Suche, Teilstrings

Ein `string` ist eine **unveränderliche** Folge von 16-Bit-Zeichen (`char`); alle Positionen zählen in
solchen Einheiten, Vergleiche und Suchen sind **ordinal** (groß/klein zählt, keine Kultur).

Die Methoden stehen im **Prelude** als Erweiterung des Basistyps (`class extends string { ... }`, 5.5.1)
und rufen jeweils die **eine** native Funktion `__StringCall(id, text, argumente...)` auf; für `char` ebenso
`__CharCall(id, zeichen)`. Die Methode wird über ihre **ID** gewählt (`StringMethod`/`CharMethod` in
`fire.Standard`, feste Zahlen), nicht über den Namen - kein Zeichenkettenvergleich in der VM. Den fire-Text
`class extends string { ... }` erzeugt `StringMethods.PreludeSource` aus einer Tabelle, die IDs stehen also
nur in C#. Ohne Prelude gibt es diese Methoden nicht.

Nicht im Prelude, sondern von der VM selbst kommen die Properties `Length` (auch bei Arrays und
Puffern; `length` ist ein Alias) und die Indexierung `s[i]` (liefert ein `char`; Zuweisung ist ein Fehler).
Eine Property kann eine Erweiterung nicht definieren (5.5.1).

```
string s = "Hello, World, again"
print(s.Length)                 // 19   (`s.length` ist ein Alias)
print(s.IndexOf("o"))           // 4    -1, wenn nichts gefunden wird
print(s.IndexOf("o", 5))        // 8    Suche ab Position 5
print(s.LastIndexOf(","))       // 12   von hinten
print(s.Substring(7, 5))        // "World";  Substring(14) = "again"
print(s[1])                     // 'e'
foreach (p in "a,b,c".Split(",")) { print(p) }
```

| `string` | Bedeutung |
|---|---|
| `IndexOf(x[, start])` | erste Position von `x` (string oder char) ab `start`, sonst -1 |
| `LastIndexOf(x[, start])` | letzte Position von `x`; mit `start` beginnt die Rückwärtssuche dort (`0 <= start < Length`) |
| `Substring(start[, count])` | Teilstring; ohne `count` bis zum Ende (`Substring(Length)` = `""`) |
| `CharAt(i)` / `s[i]` | das Zeichen an Position `i` |
| `Contains(x)`, `StartsWith(x)`, `EndsWith(x)` | `bool` |
| `ToUpper()`, `ToLower()` | invariante Groß-/Kleinschreibung |
| `Trim()`, `TrimStart()`, `TrimEnd()` | Leerraum entfernen |
| `Replace(alt, neu)` | ersetzt alle Vorkommen (leeres `alt` lässt den String unverändert) |
| `Split(trenner)` | Array von Strings (leerer Trenner: der ganze String als einziges Element) |
| `PadLeft(breite[, füll])`, `PadRight(...)` | auf Mindestbreite auffüllen (Standard: Leerzeichen) |

| `char` | Bedeutung |
|---|---|
| `IsDigit()`, `IsLetter()`, `IsLetterOrDigit()`, `IsWhiteSpace()`, `IsUpper()`, `IsLower()` | Unicode-Klassifizierung der einzelnen 16-Bit-Einheit, `bool` |
| `ToUpper()`, `ToLower()` | invariant, liefert ein `char` |
| `ToString()`, `ToInt()` | als string bzw. als Zahlenwert der Codeeinheit |

(`ToByte()` und `ToUnicode(n)` bei `char`, `ToBytes()`/`ToUnicode(n)` bei `string` und `ToChar()` bei `int`
bleiben die eingebauten Konvertierungen aus 8.10.)

Eine Position außerhalb des erlaubten Bereichs wirft `IndexOutOfBoundsException` (Text beginnt mit
„String-Index“). Das Ergebnis von `Split` ist ein Array und deshalb per `foreach` durchlaufbar.
Der Editor kennt diese Methoden aus dem Prelude (und auch die eigenen Erweiterungen des Nutzers): `text.`
schlägt sie vor, und die Typen der Ergebnisse (`Trim()` → string, `Split()` → string[], `IndexOf()` → int,
…) laufen durch Methodenketten.

### 8.13 Reflection (`#import "reflection"`)

Klassen und ihre Mitglieder lassen sich zur Laufzeit beschreiben und über ihren **Namen** benutzen. Die Bibliothek ist reiner fire-Quelltext über ein paar native Funktionen
(`fire.Standard.ReflectionPrelude`, `fire.Runtime.ReflectionNatives`); bei Programmen ohne den Import entsteht kein Mehraufwand, und der Compiler schreibt die Typ-Metadaten (`ClassMeta`) nur mit,
wenn er sie braucht.

```
#import "reflection"

var t = Type.Of(circle)                 // oder Type.Of("Circle"); Type.Named("Gibts") liefert undefined statt zu werfen
print(t.Name + " : " + t.Base.Name)     // Circle : Shape
foreach (m in t.All) { print(m.Kind + " " + m.Access + " " + m.TypeName + " " + m.Name) }

Reflect.Get(circle, "radius")           Reflect.Set(circle, "Diameter", 20.0)      // Felder UND Properties
Reflect.Call(circle, "Scale", [2.0, 1]) Reflect.New("Circle", [5.0])
Reflect.Has(circle, "Area")
t.Find("radius").Get(circle)            // Member.Get/Set/Call(obj, ...)
```

- **`Type`**: `Name`, `Base` (ein `Type` oder `undefined`), `IsActor`, `Interfaces` (Namen), `All` (alle `Member`, auch geerbte; eine abgeleitete Klasse verdeckt gleichnamige der Basis, Konstruktoren nur die eigenen),
  `Fields()`/`Properties()`/`Methods()`/`Constructors()`, `Find(name)`/`Has(name)`, `IsSubclassOf(type)`, `New(args)`; `Type.Of(x)`, `Type.Named(name)`, `Type.Names()`.
- **`Member`**: `Name`, `Kind` (`"field"`, `"property"`, `"method"`, `"constructor"`), `TypeName` (der deklarierte Typ wie im Quelltext, bei Methoden der Rückgabetyp, `""` ohne Angabe), `Access` (`"public"`/`"private"`/`"protected"`),
  `IsStatic`, `IsReadonly`, `CanRead`/`CanWrite` (Properties), `Unit` (geforderte Einheit), `DeclaredIn`, `ParamNames`/`ParamTypes`, `ParamCount()`, dazu `Get(obj)`, `Set(obj, wert)`, `Call(obj, args)`.
- **Regeln:** Reflection umgeht nichts, sie läuft durch dieselben Pfade wie normaler Code. `private`/`protected` gelten für den Code, der die Bibliothek **aufgerufen** hat (aus einer Methode der Klasse selbst ist `Reflect.Get(this, "secret")`
  erlaubt, von außen nicht: `AccessDeniedException`; im Modus `Performance` entfällt die Prüfung wie überall); ein `readonly`-Feld lässt sich nicht zuweisen; Einheiten werden geprüft (`UnitMismatchException`); Property-Accessoren
  laufen als normale Methoden (eine Exception darin läuft zum äußeren `catch`); für Objekte der Globals gelten die Sektionsregeln (THREADING_DESIGN.md Abschnitt 7).
- **Fehler** sind fangbare **`ReflectionException`** (`message`): unbekanntes oder nicht lesbares/beschreibbares Mitglied, falsche Argumentzahl, kein Objekt, unbekannte Klasse.
- **Grenzen:** Statische Mitglieder stehen in der Beschreibung, lassen sich aber nicht über `Reflect` lesen/schreiben/aufrufen. `Reflect.New` baut über eine verschachtelte Ausführung: wirft ein Konstruktor eine Exception, ist das ein
  interner Fehler statt einer fangbaren Exception. Dynamisch angelegte Felder (ohne Deklaration) erscheinen nicht in `Type`, `Reflect.Has` kennt sie.

#### Selektoren: `lambda member<T> name` (und `field`, `property`, `method`, `selector`)

Ein Parameter mit einem Selektor-Typ nimmt eine Lambda entgegen, die ein Mitglied **auswählt**; im Körper enthält der Parameter dann die **Reflection des gewählten Mitglieds** (einen `Selector`), nicht die Lambda:

```
class Watch {
    static Show(lambda member<Circle> sel, Circle c) {
        print(sel.Name + " = " + sel.Get(c))       // radius = 5
        sel.Set(c, 3.0)
        print(sel.Describe(c).TypeName)             // float (das `Member`)
    }
}
Watch.Show(c => c.radius, myCircle)
```

Fünf Arten, je nachdem, was die Lambda auswählen darf:

| Typ | erlaubt |
|---|---|
| `lambda field<T>` | nur ein **Feld** |
| `lambda property<T>` | nur eine **Property** |
| `lambda member<T>` | ein Feld **oder** eine Property |
| `lambda method<T>` | nur eine **Methode** |
| `lambda selector<T>` | **alles**: Feld, Property und Methode |

`T` ist der Klassenname, gegen den der Resolver prüft (`lambda selector<>` ohne Typ geht auch); die Instanz wird zur Laufzeit nicht gegen `T` geprüft. Passt das gewählte Mitglied nicht zur Art, ist das eine `ReflectionException`
("'P' ist eine Property, erwartet (lambda field<...>): ein Feld") - sobald es ein Objekt gibt (`Get`/`Set`/`Call`/`Describe`/`Probe`).

`Selector`: `Name` (das gewählte Mitglied), `Path` (alle Namen, bei `p => p.address.city`: `address`, `city`), `Kind` (die Art des Parametertyps), `Parent(obj)`, `ActualKind(obj)` (`"field"`, `"property"`, `"method"`), `Get(obj)`, `Set(obj, wert)`,
`Call(obj, args)` (nur bei einer Methode, also mit `method<T>` oder `selector<T>`), `Describe(obj)` (das `Member`), `Probe`/`Silence` (nicht für Methoden). `Get`/`Set` auf einer Methode sind eine `ReflectionException` (dafür gibt es `Call`). Eine Methode wählt man ohne Aufruf: `x => x.Twice`.
Die Lambda muss genau einen Parameter haben, und ihr Körper darf nur eine **Mitgliedskette auf diesem Parameter** sein; alles andere ist eine `ReflectionException` ("Die Lambda ist kein Selektor ..."). Wird ein schon umgewandelter Selektor an einen weiteren
Selektor-Parameter weitergereicht, bleibt er unverändert (und behält die Art des ersten Parameters). Ohne `#import "reflection"` ist jeder Selektor-Typ ein Fehler.

### 8.14 `probe` und `silence`

Ein `probe` hängt einen Handler an **Schreibzugriffe auf ein Mitglied eines Objekts** - auch von außen, ohne die Klasse zu ändern. `silence` nimmt Proben wieder weg. Beides braucht keinen Import.

```
var h = probe cfg.volume changed { print(name + ": " + old + " -> " + value) }   // Block: implizite Namen sender, name, old, value
probe cfg.volume changing (old, new) => new <= 100                              // false bricht das Schreiben ab
probe player.stats.hp changed (o, v) => ui.Refresh(v)                           // Pfad: Objekt = player.stats, Mitglied = hp
probe cfg.volume changed handlerLambda                                          // beliebiger Lambda-Wert
probe cfg.* changed (s, n, a, b) => print(n + " " + a + "->" + b)               // alle Mitglieder

silence h                // Handle (int) -> genau diese Probe
silence cfg.volume       // alle Proben dieses Mitglieds
silence cfg.*            // alle Proben des Objekts (ebenso: silence cfg)
```

- **Ziel:** `probe a.b.c ...` wertet `a.b` **einmal** aus; die Probe hängt an **diesem Objekt**, nicht am Slot (wird `a.b` später ersetzt, bleibt sie am alten Objekt). Das Mitglied muss existieren (Feld, Property oder Methode),
  sonst ist es ein Fehler beim Anmelden. `probe ...` ist ein Ausdruck und liefert das Handle (`int`), als Statement wird es verworfen. `silence x` mit einem Objekt entfernt alle seine Proben; ein schon entferntes Handle ist kein Fehler.
- **Handler:** der Block und `=> ausdruck` bekommen die vier Namen `sender` (das Objekt), `name` (das Mitglied), `old`, `value`; eine Lambda mit Parameterliste bekommt je nach **Anzahl** 0 nichts, 1 `(neu)`, 2 `(alt, neu)`,
  3 `(Objekt, alt, neu)`, 4 `(Objekt, Name, alt, neu)` (mehr als 4 ist ein Fehler). Sie darf lokale Werte erfassen (4.2.1).
- **Wann:** `changing` läuft **vor** dem Schreiben; liefert ein Handler `false`, wird nicht geschrieben (der Zuweisungsausdruck wertet trotzdem zum zugewiesenen Wert aus), die übrigen laufen nicht mehr. `changed` läuft **nach** dem
  Schreiben und nur, wenn sich der Wert wirklich geändert hat (Vergleich wie `==`, Objekte per Referenz). Mehrere Proben laufen in der Reihenfolge ihrer Anmeldung.
- **Was beobachtet wird:** Schreibzugriffe auf das Mitglied (`=`, `++`, `+=`, über Reflection) - bei Properties vor/nach dem Aufruf des Setters (alter Wert = Ergebnis des Getters, falls es einen gibt); schreibt der Setter selbst Felder, feuern auch
  deren Proben. **Nicht** beobachtet: Änderungen *innerhalb* eines Objekts (`obj.list.Add(...)`, Array-Elemente), eine berechnete Property, deren Quelle sich ändert (dafür das Feld proben), und Schreibzugriffe von `sync`-Rückschreibungen.
- **Ablauf:** synchron auf dem Thread des Schreibers (bei Objekten der Globals innerhalb der Sektion). Schreibt ein Handler dasselbe Mitglied desselben Objekts, feuert dafür nichts erneut. Eine Exception im Handler läuft zum
  Schreiber: bei `changing` bleibt der Wert unverändert, bei `changed` ist er schon geschrieben. Proben leben mit dem Objekt (sein Ende entfernt sie).
- **Kosten:** nur Objekte mit Probe nehmen den langsamen Schreibpfad; alle anderen behalten die schnellen Pfade unverändert.
- **Schlüsselwörter:** `probe`, `silence`, `changed`, `changing` sind kontextabhängig (`probe`/`silence` nur, wenn direkt ein Bezeichner oder `this` folgt) - als Variablennamen bleiben sie nutzbar.
- **Mit Reflection** (`#import "reflection"`, 8.13): `Reflect.Probe(obj, "name", "changed"|"changing", handler)`, `Reflect.ProbeAll`, `Reflect.Silence(obj, "name")`, `Reflect.SilenceAll(obj)`, `Reflect.SilenceHandle(h)`, `Member.Probe(obj, kind, handler)`
  und `Selector.Probe(obj, kind, handler)`/`Selector.Silence(obj)` - z.B. `Watch(c => c.volume, cfg)` mit `lambda member<Cfg> sel` und `sel.Probe(cfg, "changed", ...)`.

## 9. Offene Punkte

Der einzige frühere Punkt hier – die Methoden-Deklarationssyntax
`[TypeKeyword] name(params) { }` innerhalb von Klassen – ist durch die
tatsächliche Implementierung und breite Verwendung längst bestätigt.

Ein bekannter, noch nicht verdrahteter Punkt:
- **8.2**: die *automatische* Anwendung der Bitbreiten-Kürzung bei jeder
  Zuweisung (der Mechanismus selbst, `Value.Width`/`Value.TruncateTo`, ist
  fertig; der Resolver müsste dafür zusätzlich den deklarierten Typ jedes
  Slots nachhalten, um sie automatisch anzuwenden).

## 10. Architektur-Hinweis

Der Interpreter läuft nicht mehr als Tree-Walker, sondern als
Bytecode-VM (Lexer → Parser → Resolver → Compiler → VM) - siehe
`BYTECODE.md` für die vollständige Opcode-Liste und Laufzeit-Architektur.
Alle unten aufgeführten, ursprünglich als Vorbereitung dafür gedachten
Entwurfsentscheidungen sind umgesetzt, inklusive der Resumable Exceptions
(7.5) über explizite, wieder-aufnehmbare Handler-/Continuation-Objekte statt
reinem nativen Call-Stack-Unwinding.

