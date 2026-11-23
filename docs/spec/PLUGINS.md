# Der Scope eines Plugins

Entwurf und Stand (22.11.): jedes fire-Plugin, das ein Programm mit `Library.Load` lädt, hat **seinen eigenen globalen Scope** - unabhängig davon, welcher
Thread es lädt. Gilt gleich für die C#-VM (`VM.Plugins.cs`), die C-VM (`native/vm/vm_plugin.c`) und native Plugins (die Generatoren
`src/fire.Native/CppGenerator.Plugins.cs` und `native/forge/gen/plugins.c`, die Runtime `fire_rt.h`). Was ein Plugin sonst ist (Prüfungen, Klassen,
Einheiten, Natives): [NATIVE_BACKEND.md](NATIVE_BACKEND.md#plugins-phase-d-b), [VM_C.md](VM_C.md#plugins), für Benutzer
[18-native-code.md](../language/18-native-code.md#fire-plugins).

Anlass: ein Plugin, das in einem fire-Thread geladen wurde, legte die Objekte seiner statischen Felder in den globalen Scope **des Threads** - in allen drei
Engines; das Ende des Threads zerstörte sie, das Hauptprogramm bekam die `DestroyedException`.

## (a) Wem gehört was

* Der **Scope des Plugins** entsteht, wenn es gelinkt wird (das erste `Library.Load`), und gehört der Runtime, nicht dem ladenden Code.
* Was die **statischen Initialisierer** seiner Klassen liefern (und was diese Objekte besitzen), gehört dem Scope des Plugins - auch wenn ein Thread das Plugin
  lädt.
* Ein **Speichern in ein statisches Feld einer Klasse des Plugins** (aus dem Code des Plugins; das Programm kennt die Klassen eines Plugins nur über Reflection
  und Schnittstellen) folgt den üblichen Regeln, nur dass "der globale Scope" der Scope des Plugins ist: ein unbeanspruchter Wert (`new`, das Ergebnis eines
  Aufrufs, `take x`) geht in den Scope des Plugins - wie er bei einem statischen Feld des Programms in den globalen Scope des Programms geht; ein Objekt, das
  schon jemand besitzt, bleibt eine Referenz. Aus einem Thread gilt Regel 10 wie für die statischen Felder des Programms (ein frisches oder genommenes Objekt
  zieht um, in einer Sektion; ein anderes des Threads ist die `ArgumentException`). Begründung: ein statisches Feld gehört zu seiner Klasse, der Besitz folgt
  dem Ort - beim Programm dessen globaler Scope, beim Plugin dessen eigener.
* **Über Reflection** gilt dasselbe, denn es ist der Code des Plugins, der speichert: `Reflect.Set(obj, "P", v)` auf eine Property, deren Setter ein
  statisches Feld schreibt, `Reflect.Call` einer Methode, die es tut - auch aus einem fire-Thread. Ein statisches Feld oder eine statische Property selbst
  erreicht Reflection nicht (wie [16-probes-and-reflection.md](../language/16-probes-and-reflection.md) sagt): bis 22.11.2026 fanden die beiden VMs eine
  statische Property über das Objekt (`Reflect.Set(st, "Cur", x)` rief ihren Setter), und der Wert ging an das Objekt (`try value??.TakeTo(obj)` des
  Preludes), nicht in den Scope des Plugins - nativ war es "left out of the build". Jetzt ist es in allen drei Engines die `ReflectionException`
  `'Cur' is a static property - Reflect.Set only sets members of objects.` (`Reflect.Get`: `... only reads members of objects.`; VM.Reflection.cs,
  vm_reflect.c, `rf_get`/`rf_set` beider Generatoren), wie `Reflect.Call` für eine statische Methode. LinkageTests (UKit/UHost) prüft beides nativ, in der VM und
  der C-VM.

## (b) Entladen: `lib.Unload()`

* Neu: die Methode `Unload()` des `Library`-Objekts (`__LibCall(8, handle)`, `LibraryMethod.Unload`). Eine C-Bibliothek: die `LibraryException`
  ("only a fire plugin can be unloaded").
* Der Scope des Plugins wird zerstört - in seiner Reihenfolge wie jeder Scope von fire (die Objekte in der Reihenfolge, in der sie hineinkamen; ihre
  Destruktoren laufen), danach sind seine statischen Felder `undefined`. Eine Referenz des Programms auf ein Objekt aus dem Scope ist danach die eines
  zerstörten Objekts (die `DestroyedException` wie bei jedem). Laufen gerade seine Initialisierer in einem anderen Thread, wartet `Unload` auf sie.
* **Der Code bleibt**: Klassen, Methoden, Lambdas, `class extends`, Einheiten, Enums, Natives in C++ des Plugins bleiben registriert, bis das Programm endet
  (wie bisher: ein Plugin wird nie aus dem Prozess entfernt). Objekte von Klassen des Plugins, die das Programm besitzt, leben weiter und funktionieren -
  ihr Code ist noch da; was sie von den statischen Feldern lesen, ist `undefined`. Deshalb muss `Unload` nichts verweigern oder aufschieben.
* Threads, die gerade Code des Plugins ausführen, laufen weiter; das Entladen ist eine Sektion (sie sehen den Scope ganz oder gar nicht zerstört).
* Ein `Library.Load` danach lädt es wieder: dieselben Klassen, die statischen Initialisierer laufen noch einmal, in einen neuen Scope. Ein zweites `Unload`
  tut nichts.

## (c) Das Ende des Programms

Zuerst der globale Scope des Programms, danach die Scopes der Plugins in der Reihenfolge, in der sie geladen wurden (die nicht entladenen). Begründung: die
Destruktoren der Objekte des Programms dürfen noch die statischen Felder der Plugins benutzen (ein Plugin als Dienst des Programms); und zwischen den Scopes
gilt dieselbe Reihenfolge wie in einem Scope (wer zuerst kam, geht zuerst).

## (d) Threads

Die Scopes der Plugins gehören zur Domäne der Globals: ab dem ersten `fire` (oder ab dem Link, wenn es schon Threads gibt) sind ihre Objekte geteilt wie die
Globals des Programms (C-VM: `ownLink` + `markOwnedShared`, `vmPluginsShare` beim ersten `fire`; C#: `ShareValue`; nativ: `fire_pl_share` über
`firePluginShareGlobals`); ein Thread schreibt ein statisches Feld eines Plugins in einer Sektion, ein frisches Objekt zieht dabei in den Scope des
Plugins (`toMainGlobals` mit dem Scope des Plugins als Ziel).

## (e) Bibliotheken mit `#import`

Keine eigenen Scopes. Ihre statischen Felder setzt das Hauptprogramm an seinem Anfang, bevor ein Thread läuft (der Fehler, um den es hier geht, kann nicht
auftreten); sie sind Teil des Programms (in seine Programmdatei, statisch oder als gemeinsame Bibliothek gelinkt) und werden nie entladen. Ein eigener Scope
je Modul würde nur die Reihenfolge am Ende ändern (die Objekte einer Bibliothek nach denen des Programms) und verlangte in beiden Generatoren und beiden VMs
eine eigene Zuordnung jedes `ClaimToScope` zum globalen Scope - ohne einen Gewinn. Nicht umgesetzt.
