Ich wollte eigentlich nur einen alten 3D-Drucker steuern.

Dann brauchte ich eine Skriptsprache, da GCode mMn ein Krampf ist.

Also habe ich mich am Vibe Coding versucht. Jetzt brennt alles. 😒

🥁 Trommelwirbel füüüüüürr...

# 🔥 Fire

> Eine wahrlich deutsche Programmiersprache.

Fire entstand aus einer einfachen Frage:

> Was würde passieren, wenn man eine moderne Skriptsprache entwickelt, diese aber konsequent von einem ~~deutschen~~alkoholabhängigen Ingenieur prüfen lässt?

Die Antwort lautet: **Einheiten** sind **Teil der Sprache**, **Ausnahmen** sind **ansichtssache** (resumable), **Besitz** ist **klar definiert**, **nicht** aber die Zeilenenden. 🇩🇪

Warum sollte man sich nur für *eine* jahrzehntealte Tradition entscheiden, wenn man auch mehr haben kann?

---

## Was ist Fire?

Fire ist eine objektorientierte Skriptsprache mit:

- 📏 eingebautem Einheitensystem
- 🧩 dynamischer Typisierung
- 🏗️ Klassen, Vererbung und Interfaces
- 🔥 integriertem Multithreading über `fire`
- 🎭 Actor-Modell
- ⚡ nativen APIs und dynamischem Linking
- ♻️ resumable Exceptions
- 🔧 Ownership-basierter Speicherverwaltung

[Alle Features](docs/FEATURES.md)
[Die Specs](docs/SPEC.md)

Das Ziel war nie, möglichst klein oder besonders akademisch zu sein.

Das Ziel war, eine Sprache zu entwickeln, die gerade als Skriptsprache interessante Features bereithält, und das Beste aus C# kopiert. Gleichzeitig wurde die Garbage Collection durch ein experimentelles Ownership-Modell ersetzt, dass die Lebenszeit von Objekten klar definiert und einen vorhersehbaren Speicher-Fußabdruck garantiert.

---

## Deutsche Eigenschaften™

### 📏 Ordnung muss sein

Einheiten sind Teil der Sprache.

Meter sind Meter. Kilogramm sind Kilogramm. Und wenn jemand versucht, beides zusammenzuaddieren, gibt es eine freundliche, aber bestimmte Beschwerde.

```fire
var length = 500mm
var distance = 2m

print(length + distance)
```

Fire weiß dabei, dass beide Werte dieselbe physikalische Dimension haben und führt notwendige Umrechnungen automatisch durch.

Falsche Kombinationen erzeugen keine stillen Fehler, sondern eine Exception.

```fire
var weight = 5kg
var length = 20cm

print(weight + length)   // nope 😐
print(weight + length!)  // Ich will aber!
```

Fire versucht, Mist zu verhindern. *versucht*.

---

### 🏛️ Tradition verpflichtet

Wir schätzen Innovation.

Aber bitte nicht so viel.

Deshalb akzeptiert Fire sowohl:

```fire
var a = 1;
var b = 2;
```

als auch:

```fire
var a = 1
var b = 2
```

C und BASIC haben Jahrzehnte lang darum gekämpft, wer Recht hat.

Fire sagt:

> Beide.

Denn warum sollte man sich für genau *eine* jahrzehntealte Tradition entscheiden, wenn man einfach beide haben kann?

---

### 🗂️ Bürokratieabbau ist Programm!

Natürlich lieben wir Präzision. So, wie wir klare Definitionen lieben.

Aber wir lieben es auch, früher Feierabend zu machen.

Deshalb bringt Fire einen alten Bekannten zurück:

```fire
with player {
    .health = .health - 10
    .gold = .gold + 100
    .LevelUp()
}
```

Surprise 🎉

Ja, das ist tatsächlich `with`.

Andere Sprachen haben es entfernt, versteckt oder sprechen nur noch hinter vorgehaltener Hand darüber. Und dann tun sie sowas:

```csharp
invoice.customer.address.city = "Berlin";
invoice.customer.address.zipCode = "10115";
invoice.customer.address.street = "Musterstraße 42";
```

Fire:

```fire
with invoice.customer.address {
    .city = "Berlin"
    .zipCode = "10115"
    .street = "Musterstraße 42"
}
```

Fire sagt:

> Wenn wir schon wissen, auf welchem Objekt wir die nächsten zehn Zeilen arbeiten, warum sollten wir den Namen zehnmal hinschreiben?

---

### 🔧 Eigentum verpflichtet

Wir teilen gerne.

Aber wir sind auch keine Kommunisten.

Deshalb hat in Fire jedes Objekt genau einen Besitzer.

Und wenn etwas den Besitzer wechseln soll, dann bitte ordentlich:

```fire
tuete.TakeTo(what3var)
```

Jetzt gib mir die Tüte zurück!

Ownership regelt Lebenszeit, Aufräumen und Verantwortlichkeiten im Objektgraphen.

Denn irgendwo muss schließlich dokumentiert sein, wem das Zeug gerade gehört.

---

### 🔥 This is fine

Auch wenn es brennt, behalten wir gerne die Kontrolle.

Wer soll denn sonst überwachen, ob die Hütte korrekt abbrennt? Du?

Kontrolle benötigt Macht.

Und Macht benötigt Werkzeuge.

Let me introduce our good old friend:

```fire
catch (e) {
    e.resume()
}
```

Ein `resume` für (beinahe) alle Fälle!

Warum bei einem Fehler sofort aufgeben, wenn man ihn stattdessen inspizieren, verändern und anschließend genau dort weitermachen kann, wo es geknallt hat?

Normale Programmiersprachen behandeln Ausnahmen wie Notfälle.

Fire behandelt sie wie einen Antrag auf Sondergenehmigung.

---

### 🚂 Geordnete Parallelität

Andere Sprachen geben mehreren Threads dieselben Daten und hoffen auf das Beste.

Fire bevorzugt kontrollierte Kopien, explizite Synchronisation und Actors.

```fire
fire taking player {
    player.health = player.health - 10

    sync player
}
```

Data Races sind ein faszinierendes Konzept.

Am liebsten allerdings in anderen Programmiersprachen.

---

## Warum Fire?

Weil Programmiersprachen langweilig geworden sind.

Fire kombiniert Ideen aus:

- BASIC
- C
- C++
- C#
- Python
- Actor-Systemen
- klassischen Skriptsprachen

... und ergänzt sie um einige eigene Konzepte wie Units, Ownership, resumable Exceptions und kontrollierte Parallelität.

Das Ergebnis ist keine Sprache, die versucht, jeden Trend mitzunehmen.

Es ist eine Sprache, die konsequent ihre eigenen Ideen verfolgt. Und die ihres wahnsinnigen Designers.