# Lambda-Ausdrücke, Reflection, `selector` und `probe` - Entwurf

Stand: Schritt 1 (Captures, Kurzsyntax, LINQ) und Schritt 2 (Reflection samt `selector`) sind umgesetzt (siehe SPEC 4.2.1/8.13, BYTECODE.md Abschnitt 36/37).
Schritt 3 (`probe`/`silence`) ist hier festgehalten, so wie er abgestimmt wurde. Die Beschreibung von Schritt 2 unten ist der ursprüngliche Entwurf - maßgeblich ist SPEC 8.13.

## Entscheidungen

1. **Captures kopieren einzelne Werte** (nicht ganze Scopes, keine geteilten Variablen). Umgesetzt.
2. **Kurzsyntax** `x => ...` neben `func (x) => ...`. Umgesetzt.
3. **`probe`**: `changing` kann per `false` abbrechen; `probe` wird mit dem Schlüsselwort **`silence`** wieder entfernt.
4. **Reflection** respektiert `private`/`readonly` (und Einheiten, Property-Setter, Sektions-Locking der Globals); die deklarierten Typnamen werden als Metadaten
   mitgeschrieben (nur wenn das Programm Reflection benutzt).
5. **Lambda-Typ `selector`**: eine Lambda, die ein Mitglied eines Objekts auswählt (siehe unten).

## Schritt 2: Reflection

Was die VM schon weiß: Klassenname, Basisklasse, Feldnamen (Index, Zugriff, Einheit), Methoden (Überladungen mit Parameteranzahl, Zugriff, `static`), Konstruktoren,
statische Felder, Properties (als `get_X`/`set_X`). Neu mitzuschreiben: deklarierte Typnamen von Feldern/Parametern/Rückgaben, Parameternamen (`RuntimeClass.Decl` wird
nicht serialisiert).

API (ohne neue Syntax, `Type`/`Reflect`):
```
var t = Type.Of(obj)                  // t.Name, t.Base, t.Fields, t.Methods, t.Properties (Name, Typ, Zugriff, static, CanRead/CanWrite)
Reflect.Get(obj, "radius")            Reflect.Set(obj, "Diameter", 20.0)
Reflect.Call(obj, "Area", [])         Reflect.New("Circle", [5.0])
```
Alle Zugriffe laufen über dieselben Pfade wie normaler Code (`OpSetFieldSlow`, `CallMethodNested`), also gelten Zugriffsprüfung, `readonly`, Einheiten und Locking dort wie sonst.

### `selector`

Eine Lambda, die ein Kind/Mitglied eines Objekts auswählt, als Argument statt eines Namens-Strings:

```
Watch(lambda property<Circle> selector, Circle c) {
    print(selector.Name)              // "radius"
    var v = selector.Get(c)           // liest c.radius
    selector.Set(c, 3.0)              // schreibt (Zugriffs-/readonly-Regeln wie überall)
}
Watch(c => c.radius, myCircle)        // der Aufrufer schreibt eine gewöhnliche Lambda
```

- `lambda property<T> name` ist eine Lambda-Typ-Annotation (wie `lambda<int>`): der Parameter `name` enthält im Funktionskörper **die Reflection des gewählten Mitglieds** (Name,
  Typ, Zugriff, `Get`/`Set`/`Probe`), nicht die Lambda selbst. Mit einer Instanz lässt sich damit alles tun, was Reflection erlaubt.
- Der Compiler erkennt Lambdas, deren Körper eine reine **Mitgliedskette auf dem Parameter** ist (`c => c.radius`, `p => p.address.city`), und hinterlegt den Pfad im Proto; die
  Umwandlung geschieht beim Aufruf-Eintritt des typisierten Parameters (wie die Arität-Prüfung von `lambda<...>`). Ist der Körper keine solche Kette (Aufruf, Operator, Index),
  gibt es einen klaren Fehler ("Lambda ist kein Selektor").
- Das `T` in `property<T>` ist ein Hinweis für Dokumentation/Editor; zur Laufzeit wird gegen die Klasse der übergebenen Instanz geprüft.
- Damit lässt sich `probe` auch ohne String schreiben: `Reflect.Probe(obj, c => c.volume, "changed", handler)`.

## Schritt 3: `probe` / `silence`

```
probe cfg.volume changed { print("neu: " + value) }              // Block; implizit: sender, old, value
probe cfg.volume changing (old, new) => new <= 100               // Ausdruck; false = abbrechen
probe player.stats.hp changed (o, v) => ui.Refresh(v)            // Objekt = player.stats, Mitglied = hp
probe cfg.volume changed handlerLambda                           // beliebiger Lambda-Wert
probe cfg.* changed { print(name + " -> " + value) }             // alle Mitglieder
var h = probe cfg.volume changed => ...                          // liefert einen Handle
silence h                                                        // entfernt diese Probe
silence cfg.volume                                               // entfernt alle Proben dieses Mitglieds
silence cfg                                                      // alle Proben des Objekts
```

- Eine Probe hängt am **Objekt**, nicht am Slot (`probe a.b.prop` wertet `a.b` einmal aus). Beobachtet werden Schreibzugriffe auf genau dieses Mitglied (Felder und Properties,
  auch `+=`/`++`); Mutationen *innerhalb* eines Objekts (`obj.list.Add`) nicht. Eine berechnete Property meldet nichts, wenn sich ihre Quelle ändert - dafür das Feld proben.
- `changing` läuft vor dem Schreiben (kann mit `false` abbrechen, eine Exception darin bricht ebenfalls ab); `changed` danach und nur bei tatsächlicher Änderung.
- Läuft synchron auf dem schreibenden Thread (bei Objekten der Globals innerhalb der Sektion); kein rekursives Feuern für dasselbe (Objekt, Mitglied); Lebenszeit am Objekt.
- Kosten nur für Objekte mit Probe: sie nehmen dieselbe Sperrmarkierung wie die Globals-Objekte (`ThreadLock != null` schaltet die Feld-Schnellpfade ab) und gehen über den Slow-Pfad.
- Klassenweite Proben (`probe Circle.radius changed`) später (Inline-Cache muss solche Klassen ausnehmen).
- `changed`/`changing`/`silence` sind kontextabhängige Schlüsselwörter, `probe` wird per Lookahead erkannt - bestehende Skripte bleiben gültig.
