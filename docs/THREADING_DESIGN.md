# ScriptLang – Multithreading-Design (Referenzdokument, Stand vor Implementierung)

Dieses Dokument hält das Ergebnis der Design-Diskussion fest, bevor die
Umsetzung beginnt. Es ist bewusst als eigenständige Referenz gehalten (nicht
Teil von SPEC.md/BYTECODE.md), da sich das Threading-Modell noch in der
Entwurfsphase befindet und bis zur ersten Implementierung vermutlich noch
Detailanpassungen erfährt.

## 0. Grundphilosophie

Kein klassisches Java/C#-artiges Shared-Memory-Modell. Stattdessen:

- Kein generelles Shared Memory zwischen Threads.
- Keine Data Races auf normalen Objekten.
- Kommunikation über **Ownership-Kopien** (`taking`) und explizite
  **Synchronisationspunkte** (`sync`/`try sync`), nicht über implizite,
  überall mögliche gemeinsame Zugriffe.
- Actors als zusätzliches, bewusst *anderes* Kommunikationsmodell
  (Message-Passing), nicht als Sonderfall des Ownership-Modells.

Alle Entscheidungen unten sind Präzisierungen an den Rändern dieses
Kernmodells - das Kernmodell selbst steht.

## 1. Thread-Erzeugung: `fire`

```
fire { ... }
fire MethodA()
fire taking player { ... }
fire MethodA() taking player
fire with actorA { ... }
fire MethodA() with actorA taking player
```

- `taking X` (siehe Abschnitt 3): übergibt dem Kind-Thread eine isolierte
  Arbeitskopie von `X`.
- `with actorA` (siehe Abschnitt 2): bindet eine Actor-Referenz (Handle,
  keine Kopie) in den Fire-Block ein.
- Beide Bindungen sind unabhängig voneinander kombinierbar.
- Fire-Threads haben **keine Rückgabewerte**. Wer wissen muss, wann ein
  Thread fertig ist, nutzt eine Actor-Nachricht (z.B.
  `actorA.respond("finished")`) als expliziten, objektorientierten
  "Join"-Ersatz - es gibt keinen impliziten Join-Mechanismus.
- Ein Fire-Thread endet automatisch am Blockende, oder vorzeitig durch
  `leave` (Abschnitt 6).

## 2. Actors

**Implementierungsstand: vollständig umgesetzt** (Laufzeit + Sprachsyntax,
siehe Abschnitt 8) - `actor Name { ... }`, `fire ... with actorA { ... }`,
`process actorA`/`try process actorA`.

Actors sind eine **eigene Deklarationsart**, keine normalen Klassen (z.B.
`actor Foo { ... }` statt `class Foo`) - das ist eine bewusste Abweichung
von der ursprünglichen Formulierung "Actor ist ein normales Runtime-Objekt",
weil diese mit dem Ownership-Modell nicht konsistent wäre: entweder würde
ein Actor wie jedes andere Objekt per `taking` kopiert (dann würden
Antworten ins Leere laufen, da niemand außerhalb des Kind-Threads die
Kopie sieht), oder er würde als Referenz durchgereicht (dann wäre er ein
Shared-Memory-Loch, genau das, was das Modell vermeiden soll).

Stattdessen:

- Jeder Actor ist an seinen **Heimat-Thread** gebunden (der Thread, der
  ihn erzeugt hat).
- Ein Methodenaufruf auf einer Actor-Referenz von **irgendeinem** Thread
  aus (inklusive des Heimat-Threads selbst) erzeugt **keine direkte
  Ausführung**, sondern eine **Nachricht in der Mailbox** des Actors.
- Der Heimat-Thread arbeitet seine Mailbox selbst ab, über zwei neue
  Schlüsselwörter (bewusst symmetrisch zu `sync`/`try sync`):
  - **`process actorA`** - blockierend: wartet, bis eine Nachricht
    ankommt, und führt sie aus.
  - **`try process actorA`** - nicht-blockierend: `true`, wenn eine
    Nachricht verarbeitet wurde, sonst `false`.
- Damit kann der Heimat-Thread wahlweise eine reine Nachrichtenschleife
  sein (`while (true) { process actorA }`) oder eigene Arbeit mit
  gelegentlichem Nachsehen mischen
  (`while (running) { try process actorA; doOtherWork() }`) - die Sprache
  erzwingt keine feste Event-Loop-Struktur.
- Actor-**Referenzen** (Handles) dürfen frei zwischen Threads herumgereicht
  werden - sie sind leichtgewichtig (keine Kopie des tatsächlichen
  Actor-Zustands), das ist der einzige bewusste, kontrollierte Bruch mit
  "kein Shared Memory".

## 3. Ownership-Kopien: `taking`

```
fire taking player { player.health -= 10 }
```

- `taking X` erzeugt eine **vollständige, isolierte Tiefenkopie** von X's
  gesamtem **eigenem** Ownership-Baum. Main-Thread-Objekt und Kind-Thread-
  Kopie sind danach vollständig getrennt, kein Shared Memory.
- **Harte Regel**: Enthält der kopierte Baum irgendwo eine Referenz auf
  etwas, das **nicht** zum Ownership-Baum von X gehört (eine Referenz auf
  ein unabhängig besessenes Objekt, oder ein roher Pointer via
  `unsafe`/`&`, der aus dem kopierten Baum hinauszeigt), **schlägt
  `taking` mit einem klaren Fehler fehl**. Kein stilles Kopieren des
  Referenzwerts (das wäre ein verstecktes Shared-Memory-Loch), kein
  stilles Nullen (stiller Datenverlust).
- Jede `taking`-Kopie trägt eine **versteckte Rückverknüpfung** zu ihrem
  Original - notwendig, damit `sync`/`sync flat` (Abschnitt 4) überhaupt
  wissen, wohin zurückgeschrieben werden soll.
- Das Original wird nicht vorzeitig destruiert, solange noch eine lebende,
  potenziell zurücksynchende `taking`-Kopie existiert (Checkout-artige
  Lebenszeitverlängerung) - siehe auch Abschnitt 5 (Locking).

## 4. Synchronisation: `sync` / `try sync` / `sync flat` / `try sync flat`

### 4.1 Varianten

|                    | blockierend | nicht-blockierend |
|--------------------|-------------|--------------------|
| Voller Baum        | `sync X`    | `try sync X`       |
| Nur direkte Kinder | `sync flat X` | `try sync flat X` |

Rückgabewerte (einheitlich für alle vier Varianten):

- **`true`** - erfolgreich synchronisiert.
- **`false`** - nur bei den `try`-Varianten: Lock war belegt, nichts
  passiert, Aufrufer kann später erneut versuchen.
- **`undefined`** - das Sync-Ziel (das Original, siehe Abschnitt 3)
  existiert nicht mehr. Eindeutig unterscheidbar von "Lock belegt". Ein
  guter natürlicher Zeitpunkt für ein `leave` im aufrufenden Thread.

`sync X`/`sync flat X` (ohne `try`) **blockieren**, bis der Lock verfügbar
ist - sie geben deshalb nie `false` zurück, nur `true` oder `undefined`.

### 4.2 Semantik

- Rekursiver `sync`: steigt durch den **gesamten** Objektgraphen (SPEC-
  Beispiel: `player` → `inventory` → `items`/`gold`, `stats`, ... - alle
  Ebenen werden synchronisiert).
- `sync flat`: nur die **direkten Kinder** des angegebenen Knotens. Bei
  Objekten sind das die Felder; bei Arrays sind die **Elemente** die
  direkten Kinder (nicht das Array selbst als Kind seines Besitzer-
  Objekts - `sync flat player` synct also nicht `player.inventory.items`,
  aber `sync flat player.inventory.items` synct `items[0]`, `items[1]`,
  ...).
- Quelle überschreibt Ziel, **Last-Writer-Wins** - keine
  Konfliktauflösung, keine Versionsnummern, kein Merge, kein
  Change-Tracking.

### 4.3 Objektreferenzen bei `sync flat` (Fall A/B/C)

| Fall | Quelle | Ziel | Ergebnis |
|------|--------|------|----------|
| A | != null | == null | Komplette Kopie des Objekts wird erzeugt |
| B | == null | != null | Ziel wird `null`; verliert es dadurch Ownership, läuft die Destruct-Kaskade |
| C | != null | != null | Referenz bleibt bestehen, **kein** rekursiver Sync |

Wichtig zum Verständnis von Fall A: es entsteht eine **neue, unabhängige
Instanz** im Zielbaum - keine Referenzverknüpfung mit der Quelle. Nach
diesem Zeitpunkt divergieren beide wieder unabhängig, bis ein erneuter
Sync stattfindet.

### 4.4 Arrays

Arrays werden bei `sync flat` als **möglichst exakte 1:1-Kopie** der
Quelle behandelt: das Ziel-Array wird zuerst auf die Länge der Quelle
gebracht (wachsen oder schrumpfen), danach werden die Fall-A/B/C-Regeln
elementweise angewendet.

### 4.5 Locking

- **Ein Lock für jeden Zugriff** - nicht nur für `sync`-Operationen,
  sondern auch für ganz normale Lese-/Schreibzugriffe vom **besitzenden**
  Thread selbst. Nur so ist "keine Data Races" wirklich garantiert (sonst
  wären Torn Reads möglich, wenn der Haupt-Thread mitten in einem
  laufenden Sync liest).
- Kosten-Optimierung: der Lock-Mechanismus muss nur für Objekte aktiv
  sein, die **jemals** Ziel von `taking` waren (ein Flag, einmalig beim
  ersten `taking`/`fire` gesetzt) - reine Single-Thread-Objekte, die nie
  mit einem Fire-Thread in Berührung kommen, zahlen keinen Overhead.
- **Lock-Reihenfolge**: Locks werden in einer festen, globalen Reihenfolge
  erworben (z.B. nach Objekt-Erzeugungsreihenfolge/ID, nicht nach
  Aufrufreihenfolge im Code), um klassische AB-BA-Deadlocks bei
  verschachtelten/überlappenden Syncs zu vermeiden. **Offener
  Implementierungspunkt**: `taking` auf ein Unterobjekt, dessen Vorfahre
  bereits an einen anderen Thread ausgecheckt ist, braucht besondere
  Behandlung (denselben Lock wie der Vorfahre verwenden, oder verbieten) -
  hier ist besondere Sorgfalt bei der Umsetzung nötig.

## 5. Exceptions über Thread-Grenzen

- **`resume()` darf ausschließlich vom werfenden Thread selbst aufgerufen
  werden.** Die Continuation (Frames/Scope/IP) ist Ausführungszustand
  *einer bestimmten* VM-Instanz - sie darf niemals von einer anderen
  VM-Instanz/einem anderen Thread aus mutiert werden (das wäre keine
  gewöhnliche Data Race mehr, sondern potenzielle Interpreter-Korruption).
- Ein Exception-**Objekt** darf trotzdem eine Thread-Grenze überqueren
  (als normale Daten, über `sync`, eine Actor-Nachricht, oder die
  Zustellung einer unbehandelten Exception an `catch threads(...)`, siehe
  Abschnitt 6) - aber **beim Grenzübertritt wird die Resumability
  automatisch verworfen**. Ein späterer `resume()`-Versuch auf der
  herübergereichten Kopie wirft einen klaren Fehler ("nicht mehr
  fortsetzbar, Thread-Grenze überquert"), kein stiller No-Op.
- Möchte ein außenstehender Thread trotzdem Einfluss auf eine offene
  Continuation nehmen, geschieht das ausschließlich indirekt: der
  werfende Thread selbst fragt (über `sync`/Actor-Nachricht) beim
  außenstehenden Thread nach der gewünschten Information und ruft
  `resume()` dann selbst auf. Die Entscheidungshoheit über `resume()`
  bleibt immer beim werfenden Thread.

## 6. Thread-Beendigung: `leave`, unbehandelte Exceptions, `terminate`

Drei klar getrennte Mechanismen:

### 6.1 `leave` - einen einzelnen Fire-Thread verlassen

```
fire {
    if (cancelled) leave
    DoWork()
}
```

- Intern eine spezielle Exception (`ThreadLeaveException` oder ähnlich),
  die **nicht** von normalen `catch`/`catch(e)`-Blöcken *innerhalb* des
  Threads gefangen werden kann - sonst würde ein harmloser, generischer
  `catch (e) { ... }` irgendwo im Code versehentlich das `leave` abfangen
  und den Thread am Beenden hindern.
- Läuft beim Verlassen durch alle offenen `finally`-Blöcke (korrektes
  Aufräumen/Destruktoren), endet dann still am Rand des jeweiligen
  Fire-Blocks.
- **Muss nirgendwo zugestellt werden** - kein `catch threads(...)` nötig,
  kein Fehler. Ein `leave` ist ein gewollter, sauberer Ausstieg; das
  Programm läuft normal weiter, der Thread ist einfach zu seinem
  natürlichen Ende gekommen.

### 6.2 Unbehandelte Nutzer-Exception in einem Fire-Thread

```
catch threads(ExceptionType e)
{
    // ...
}
catch threads()
{
    // Fallback für alles andere
}
```

- Syntax analog zum bestehenden catch-ohne-try-Muster.
- Eine Exception, die innerhalb eines Fire-Threads nicht gefangen wird
  (und **nicht** `leave` ist), wird als **Kopie ohne Resumability**
  (Abschnitt 5) zum **Main-Thread** transportiert und läuft dort in einen
  aktiven `catch threads(...)`-Handler, falls vorhanden. Die Zustellung
  erfolgt kooperativ, am nächsten sicheren Punkt im Main-Thread (analog zu
  `terminate`, Abschnitt 6.3).
- Der Handler läuft auf dem Main-Thread (nicht auf einem separaten
  Koordinations-Thread) - dadurch entsteht kein Konflikt mit der
  `resume()`-Regel aus Abschnitt 5: das Exception-Objekt ist an dieser
  Stelle bereits eine Kopie ohne Resumability, ein `resume()`-Versuch im
  Handler bekommt denselben "nicht mehr fortsetzbar"-Fehler wie überall
  sonst nach einem Grenzübertritt - kein Sonderfall nötig.
- **Kein passender `catch threads(...)` registriert**: das ganze Programm
  bricht ab, wie bei einer unbehandelten Exception im Main-Thread heute
  schon.

### 6.3 `terminate` - globaler Not-Aus

```
terminate()
terminate(42)
terminate(new ExitInfo("Wartungsmodus", 3))
```

```
catch terminate(v)
{
    // letztes Aufräumen/Logging, läuft (schließlich) im Main-Thread
}
```

- Stoppt **alle** Threads (inklusive Main-Thread) - das globale Pendant
  zu `leave`.
- **Endgültig, nicht umkehrbar.** Der `catch terminate(v)`-Handler ist ein
  reiner Beobachtungs-Hook (letztes Log/Cleanup) - er kann das Beenden
  **nicht** verhindern, anders als ein normaler `catch`. Nach dem Handler
  (oder wenn keiner registriert ist) endet der Prozess in jedem Fall.
- **Synchronisiert beenden**: Jeder Thread bemerkt das globale
  Terminate-Signal kooperativ an seinem nächsten sicheren Punkt (z.B. bei
  jedem Funktionsaufruf/jeder Schleifen-Iteration), löst dort intern ein
  `leave`-artiges Signal aus und läuft durch seine eigenen `finally`-
  Blöcke. Der tatsächliche Prozess-Exit wartet, bis **alle** Threads ihr
  Aufräumen abgeschlossen haben, bevor das Programm tatsächlich endet -
  "erst räumen alle auf, auch wenn das Dach brennt". Kein Thread wird
  mitten in einem `finally` abgeschnitten, nur weil ein anderer (z.B. der
  Main-Thread) schneller fertig war.
- Aufrufbar von **jedem** Thread (Main oder Fire) - ein Not-Aus muss von
  überall auslösbar sein.
- **Erster Aufruf gewinnt**: wird `terminate` gleichzeitig von mehreren
  Threads mit unterschiedlichen Werten aufgerufen, gewinnt der erste, der
  das globale Signal setzt - alle weiteren `terminate`-Aufrufe werden zu
  No-Ops (das Programm wird ja ohnehin schon beendet).
- **Rückgabewert/Exit-Code**: `terminate` kann einen beliebigen Wert
  mitnehmen (Klasse, `int`, o.ä.). Dieser Wert ist zweifach sichtbar:
  1. Im `catch terminate(v)`-Handler als `v`.
  2. Nach vollständigem Prozessende über eine neue Eigenschaft
     `VM.ExitValue`, auslesbar vom einbettenden Code (Host), der die VM
     gestartet hat - praktisch ein Exit-Code. `undefined`, falls das
     Programm regulär durchgelaufen ist (kein `terminate` aufgetreten).

## 7. Globale Variablen

**Implementierungsstand: umgesetzt** (siehe Abschnitt 8 sowie Resolving.
Resolver.ResolveFireStmt/Runtime.FireRuntime.FireVmTaking).

- **Lesen erlaubt** aus einem Fire-Thread heraus, aber nur als **Snapshot
  zum Fire-Zeitpunkt** (nicht live/aktuell).
- **Schreiben verboten** - ein Zuweisungsversuch auf eine globale Variable
  aus einem Fire-Thread heraus ist ein Fehler. Jede Änderung an globalem
  Zustand muss explizit über `sync`/Actor-Nachrichten kommuniziert werden,
  genau wie bei normalen Objekten.

## 8. Offene Implementierungspunkte (bewusst hier vermerkt, nicht vergessen)

- **Sprachsyntax-Stand**: `fire { ... }`/`fire taking X { ... }` (auch
  mehrfach: `fire taking X taking Y { ... }`)/`fire with actorA { ... }`
  (auch kombiniert, in beliebiger Reihenfolge)/`fire MethodA(args)
  [taking/with-Klauseln]` (Aufrufform, siehe Parser.ParseFireCallForm),
  `sync X`/`try sync X`/`sync flat X`/`try sync flat X`, `leave`,
  `terminate(wert)`, die globalen Deklarationen `catch threads(...)`/
  `catch threads()`/`catch terminate(v)`, sowie `actor Name { ... }`
  (auch `actor extends X { ... }`/`class extends AktorName { ... }`) und
  `process X`/`try process X` sind ALLE als echte Sprachsyntax umgesetzt -
  KEINE offenen Randfälle mehr aus der ursprünglichen Liste. `catch
  threads(...)`/`catch terminate(v)` sind dabei bewusst NUR an
  Top-Level-Programmposition erkannt (siehe Parser.ParseProgram) -
  innerhalb eines Blocks verschachtelt kollidieren sie sonst mit dem
  bestehenden "catch ohne try erweitert den Block"-Feature.
- **`fire MethodA(args)` ist reines Parser-Sugar** (siehe Ast.FireStmt-Doku/
  Parser.ParseFireCallForm) - erzeugt KEINEN eigenen AST-Knoten, sondern
  entzuckert direkt zu einem normalen `FireStmt`: `this` und jedes Argument
  werden wie zusätzliche `taking`-Erfassungen unter internen Namen
  (`__fire_this__`, `__fire_argN__`) gebunden, der Body besteht aus einem
  einzigen synthetischen Methodenaufruf darauf. Dadurch kein einziger
  zusätzlicher Resolver-/Compiler-/VM-Codepfad nötig.
- **BUGFIX**: `taking` eines PRIMITIVEN Werts (int/string/bool/...) belegte
  vorher keinen Slot (die Kopierlogik prüfte nur `Kind == ValueKind.Class`
  und tat für alles andere schlicht nichts) - wurde beim Entwurf von `fire
  MethodA(args)` entdeckt (Methodenargumente sind oft primitiv) und
  behoben (siehe Runtime.FireRuntime.FireVmTaking).
- **Actor-Methodenaufrufe sind IMMER asynchron**, auch vom eigenen
  Heimat-Thread aus (auch `this.andereMethode()` INNERHALB einer
  Actor-Methode) - eine bewusste Vereinfachung: es gibt (noch) keine
  Unterscheidung "synchron, weil selber Thread" vs. "asynchron, weil
  fremder Thread". Ein Actor kann sich also nicht direkt selbst
  aufrufen, ohne über die eigene Mailbox zu gehen (`process`/
  `try process` auf sich selbst).
- **`base.Methode()`/Konstruktor-Verkettung sind NICHT über die Mailbox
  umgeleitet** - nur der allgemeine, virtuelle `obj.Methode()`-Aufruf
  (`OpCode.CallMethod`) prüft auf eine Mailbox; `CallBaseMethod`/
  Konstruktions-Pfade laufen synchron durch, wie bei normalen Klassen -
  sonst könnte z.B. eine Actor-Konstruktor-Kette nie fertig laufen.
- **Fire-Block sieht jetzt echte Hauptprogramm-Globals, read-only** (siehe
  Resolving.Resolver.ResolveFireStmt/Runtime.FireRuntime.FireVmTaking) -
  entspricht jetzt Abschnitt 7 ("Globals: lesbar als Snapshot"). Beim
  Betreten eines `fire`-Blocks wird JEDE Hauptprogramm-Variable unter
  ihrem eigenen Namen sichtbar, an DENSELBEN Slots wie im Hauptprogramm -
  der Laufzeit-Snapshot wird SYNCHRON auf dem AUFRUFENDEN Thread gelesen
  (primitive Werte direkt, Objekte als isolierte Tiefenkopie wie bei
  `taking`), BEVOR der neue Thread startet - ein lazy Zugriff auf die
  lebendige Scope-Instanz von dort aus wäre ein Daten-Wettlauf gewesen.
  Ein Schreibzugriff auf ein Hauptprogramm-Global wird zur Compile-Zeit
  abgelehnt (`'x' ist 'readonly' ...`) - eine `taking`/`with`-Erfassung
  MIT DEMSELBEN NAMEN schattiert den Schnappschuss ganz normal und bleibt
  dabei selbst schreibbar (kein Widerspruch: die Erfassung ist dann
  gemeint, nicht das gleichnamige Global). Per **`#noshadow`**-Direktive
  (Ast.NoShadowDirective, wirkt fürs GANZE Programm, unabhängig davon, VOR
  oder NACH welchem `fire`-Block sie als Top-Level-Anweisung steht - MUSS
  dafür aber eine Top-Level-Anweisung sein, nicht verschachtelt in einer
  Klasse/Methode) komplett abschaltbar - dann verhält sich jeder
  `fire`-Block wieder wie vor Einführung des Snapshots (nur `taking`/
  `with`, keine andere Hauptprogramm-Variable sichtbar) UND der
  Laufzeit-Snapshot wird gar nicht erst gebaut (kein Overhead, auch nicht
  für Objekt-Globals, die sonst per `ObjectCopier.Take` kopiert würden).
- **`return` innerhalb eines `fire`-Blocks** wird abgelehnt (wie außerhalb
  jeder Funktion) - konsistent mit "keine Rückgabewerte" (Abschnitt 1).

- **Lock-Reihenfolge bei verschachtelten/überlappenden `taking`/`sync`**
  (Abschnitt 4.5) - braucht besondere Sorgfalt, sonst sind AB-BA-Deadlocks
  möglich, jetzt, wo `sync` (ohne `try`) tatsächlich blockiert.
- **VM-Architektur**: Die aktuelle VM ist eine einzelne Instanz mit
  global-veränderlichem Zustand (`_frames`, `_currentScope`, `_ip`, ...).
  Für echtes Multithreading braucht **jeder Thread seine eigene
  VM-Instanz**. Kompilierte, unveränderliche Strukturen (`RuntimeClass`,
  `FunctionProto`, `Chunk`) können sicher zwischen VM-Instanzen geteilt
  werden; alles Laufzeit-Veränderliche nicht. Das ist eine deutlich
  größere Umbaustelle als die bisherigen Sprachfeatures - ein neues
  Subsystem (Thread-Lifecycle, echte OS-Locks, Cross-Thread-
  Kopieralgorithmus für `taking`, Mailbox-Implementierung für Actors),
  keine reine Spracherweiterung.
- **Kooperative Prüfpunkte**: sowohl `terminate` als auch die Zustellung
  unbehandelter Exceptions an `catch threads(...)` brauchen dieselbe
  Art "sicherer Punkt" (Funktionsaufruf-/Schleifengrenze) - sollte als
  ein gemeinsamer Mechanismus implementiert werden, nicht zweimal separat.
