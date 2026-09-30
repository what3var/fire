# Portierung der VM nach C++ (Ziel: FreeRTOS)

Ziel: der VM-KERN (Lexing/Ast/Parsing/Resolving/Values/Bytecode/Runtime -
alles unter `src/ScriptLang`) soll sich nach C++ übertragen lassen und dort
auf FreeRTOS laufen. AUSDRÜCKLICH NICHT im Umfang: `ScriptLang.Editor`
(WPF, reines Desktop-Werkzeug) und die Grafik-Brücke (`ScriptLang.Terminal*`,
`ScriptLang.Terminal.Bridge*`, siehe docs/CONSOLE.md) - deren native
Andockstellen (NativeRegistry-Funktionen) wären auf einem Embedded-Target
ohnehin komplett andere (echte Hardware/Peripherie statt SDL/GDI+).

Dies ist ein LEBENDES Dokument - jeder gefundene C#-Bau, der sich nicht
1:1 übertragen lässt, wird hier eingetragen (Status: offen/erledigt),
statt die gesamte Portierung in einem Schritt zu versuchen.

## Warum das überhaupt ein Thema ist

C# bringt zur Laufzeit Dinge mit, die es auf FreeRTOS entweder gar nicht
gibt, oder die dort bewusst vermieden werden (Speicherbudget, Determinismus,
oft `-fno-exceptions`/kein RTTI in Embedded-Toolchains):
Garbage Collector, Exceptions als Kontrollfluss-Mechanismus, Reflection,
echte OS-Threads/`Task`, dynamisches Nachladen von Bibliotheken (P/Invoke).
Die Sprache selbst (ScriptLang) ist hier gut vorbereitet - das Ownership-
Modell (siehe docs/SPEC.md, Destruktor-Kaskade) ersetzt GC-Abhängigkeit
bereits weitgehend -, aber die HEUTIGE C#-IMPLEMENTIERUNG nutzt an
mehreren Stellen C#-eigene Bequemlichkeiten, die beim Übertragen einzeln
durch etwas Portables ersetzt werden müssen.

## Status

### Erledigt

- **Array-/Puffer-Zugriff ohne Exceptions** (`ScriptArray`/`Values.ByteBuffer`):
  `Get`/`Set` (warf `ScriptArrayBoundsException`, von der VM per `try`/`catch`
  aufgefangen) ersetzt durch `TryGet`/`TrySet` (`bool` Rückgabewert + `out`-
  Parameter, kein Werfen mehr). Übersetzt sich 1:1 nach C++
  (`bool TryGet(int64_t index, Value& out)`), keine Exceptions im Hot Path
  mehr nötig. Die Umwandlung "ungültiger Index -> fangbare
  ScriptLang-`IndexOutOfBoundsException`" bleibt VM-seitig (VM.
  ThrowIndexOutOfBounds) - das ist ein SKRIPT-Sprachfeature (`try`/`catch`
  im Skript selbst), kein Implementierungsdetail der Datenstruktur, und
  bleibt deshalb unverändert bestehen.

- **VM-interner Kontrollfluss außerhalb von Skript-`try`/`catch`**
  (ehem. Punkt 3): Analyse ergab, dass `ThrowException` selbst für den
  NORMALEN Fall ("ein `catch` passt") bereits KEINE C#-Exception nutzt,
  sondern reine Zustandsumschaltung (`_ip`/`_currentChunk`/`_currentScope`
  direkt gesetzt, dann normal `return`) - das war schon immer portabel.
  Die ZWEI verbleibenden Stellen, an denen eine C#-Exception den Aufrufer
  von `VM.Run()` verlässt (beide: "GAR KEIN Handler hat gepasst, das
  Programm bricht komplett ab"), sind jetzt ebenfalls auf reine
  Zustandssignalisierung umgestellt: neues Feld `VM.UnhandledException`
  (gesetzt statt geworfen, `_stopExecutionRequested = true`, `Run()` kehrt
  über den nächsten CheckShutdownSignals-Prüfpunkt ganz normal zurück).
  `Bytecode.UncaughtScriptException` bleibt als reine, OPTIONALE C#-
  Bequemlichkeit für Host-Code bestehen (kann aus `vm.UnhandledException`
  konstruiert - aber nicht mehr von der VM selbst geworfen - werden).
  Betrifft: `ThrowException` (kein Handler in dieser VM-Instanz) und
  `HandleDeliveredThreadException` (kein `catch threads(...)` zugestellt).

- **Shutdown-Signale ohne Exceptions und ohne Dauer-Abfrage:** `leave`/`terminate`/
  eine zugestellte Thread-Exception werden nur noch an den sicheren Punkten der VM
  geprüft (Schleifen-Rücksprung, Aufruf, nach einem nativen Aufruf; ein Vergleich des
  globalen Zählers `s_signalEpoch` mit dem zuletzt gesehenen Stand), und das Beenden
  läuft über `VM.StopExecution()` - `_currentChunk` auf einen Chunk aus nur `Halt`
  stellen, `Run()` liest als Nächstes dieses `Halt`. Keine C#-Exception, kein Stop-Flag
  pro Instruktion in `Run()`. (Der Vorschlag, das Beenden als Exception zu werfen, wurde
  verworfen: es widerspricht dieser Regel, und ein Signal von einem anderen Thread lässt
  sich ohnehin nicht in einen laufenden Thread werfen - der muss es selbst bemerken.)

- **`Value.RequireKind`** (ehem. Punkt 4): Analyse ergab, dass
  `InvalidOperationException` (aus RequireKind wie auch den ~48 ähnlichen
  Stellen in VM.cs) NIRGENDS im Kern gezielt als BEHANDELBARER Fehler
  gefangen wird - es ist immer ein harter, kompletter Abbruch. Das ist
  strukturell ANDERS als der Array-Fall: eine verletzte Compiler-/VM-
  Invariante (z.B. `AsInt()` auf einem Value, das laut korrekt kompiliertem
  Bytecode gar nicht diesen Kind haben dürfte) ist kein durch ein GÜLTIGES
  Skript erreichbarer Zustand, sondern ein VM-/Compiler-BUG - dafür ist ein
  `TryX()`-Rückgabewert das falsche Muster (niemand "behandelt" eine
  Assertion sinnvoll weiter). Stattdessen: neuer, klar benannter Typ
  `Values.VmInvariantViolationException` (nur für `RequireKind` bereits
  eingesetzt) - markiert für den späteren Porter explizit "das wird
  `assert()`/Panic-Handler in C++, kein `TryX()`". Die übrigen ~48
  `InvalidOperationException`-Stellen in VM.cs folgen demselben Muster
  und können bei Gelegenheit auf denselben Typ umgestellt werden (rein
  kosmetisch, keine Verhaltensänderung, deshalb nicht in einem Rutsch
  mitgemacht).

- **Feldzugriff ohne generisches `Dictionary<string,Value>` pro Instanz**
  (docs/BYTECODE.md Abschnitt 21): `ObjectInstance.Fields` ist jetzt
  `Runtime.FieldStore` - ein Array für zur Kompilierzeit bekannte,
  deklarierte Felder (fester Index über `RuntimeClass.FieldIndex`, analog
  zu lokalen Variablen), Dictionary-Fallback nur noch für Fälle ohne
  bekannte RuntimeClass (reine Tests außerhalb der Compiler/VM-Pipeline).
  Betraf mehr als nur ObjectInstance selbst - SyncEngine (Cross-Thread-
  `sync`), ObjectCopier (`taking`-Kopien) und PointerTargets (`&obj.feld`)
  hängen alle von Feldern als benannten, iterierbaren Einträgen ab;
  FieldStore hält bewusst dieselbe API-Oberfläche (Indexer/TryGetValue/
  ContainsKey/Aufzählung) bereit, damit diese drei UNVERÄNDERT bleiben
  konnten. Array + vorab berechnete Index-Tabelle pro Klasse ist auch für
  die C++-Portierung deutlich näher an der letztlich nötigen Struktur als
  ein generisches Dictionary pro Instanz.

### Grundsatz (wichtig, gilt für den gesamten weiteren Fahrplan)

Funktionalität wird NUR im eigentlichen C++-Port entfernt (z.B. `#extern`
komplett streichen, siehe Punkt weiter unten), NIE vorab in der C#-
Referenzimplementierung - jede Änderung hier bleibt ein vollständig
funktionsgleiches, lauffähiges C#-Programm. Wo eine C#-Bequemlichkeit
(z.B. eine geworfene Exception) für Host-Code weiterhin sinnvoll ist,
bleibt sie als DÜNNE, OPTIONALE Hülle um den portablen Kern bestehen
(siehe `UncaughtScriptException` oben).

### Offen (grob nach vermuteter Dringlichkeit für FreeRTOS)


- **`FireRuntime`/`fire`-Threads**: nutzt echte .NET-`Thread`/`Task`/
  `ConcurrentQueue`. FreeRTOS hat ein eigenes, sehr anderes Task-/Queue-
  Modell (feste Task-Stacks, eigene Scheduler-Primitiven) - das ist
  vermutlich der GRÖSSTE Einzelbrocken der ganzen Portierung, siehe
  docs/THREADING_DESIGN.md.
- **`#extern "libName"` / `ExternRegistry`**: dynamisches Nachladen +
  P/Invoke-artiges Marshalling zu einer Laufzeit-Bibliothek - auf FreeRTOS
  gibt es kein dynamisches Laden von `.so`/`.dll`-artigen Modulen. Für den
  Embedded-Zweig vermutlich ersatzlos gestrichen oder durch rein statisch
  zur Compile-Zeit gebundene native Funktionen (ohnehin schon vorhanden:
  `NativeRegistry`) ersetzt. Wichtig nach dem Grundsatz oben: diese
  Streichung passiert NUR im C++-Port selbst, `#extern` bleibt in der
  C#-Referenzimplementierung vollständig erhalten.
- **Container/Collections**: `List<T>`/`Dictionary<K,V>`/`HashSet<T>` aus
  `System.Collections.Generic` durchziehen den ganzen Kern - in C++ durch
  `std::vector`/`std::unordered_map`/`std::unordered_set` ersetzbar, kein
  strukturelles Problem, aber viel Übersetzungsarbeit (jede Fundstelle).
- **LINQ**: vereinzelt genutzt (`.Where`/`.Select`/`.FirstOrDefault`/etc.) -
  muss durch normale Schleifen ersetzt werden (LINQ selbst hat keine
  sinnvolle C++-Entsprechung als Bibliotheksaufruf).
- **String-Handling**: C#-`string` (UTF-16, immutable, GC-verwaltet) vs.
  ein für FreeRTOS geeignetes String-Modell (vermutlich `std::string`,
  ggf. mit eigenem Allocator wegen begrenztem Heap) - betrifft u.a.
  Format-Strings (`$"..."`), String-Konkatenation über `+`.
- **Delegates für native Funktionen**: `NativeFunction`/
  `TryableNativeFunction` sind C#-`delegate`s - in C++ Funktionszeiger oder
  `std::function` (letzteres hat eigene Heap-Allokations-Tücken auf
  Embedded-Targets, eher ersteres bevorzugen).
- **`Value` als Struct mit `object`-Feldern**: für Referenztypen (Array/
  Buffer/ObjectInstance/Lambda) nutzt `Value` intern vermutlich ein
  geboxtes `object`-Feld (C#-Boxing) - in C++ eher ein Tagged-Union/
  `std::variant`-artiger Ansatz nötig, um GC-Abhängigkeit zu vermeiden.
- **Speicherverwaltung allgemein**: das Ownership-Modell ersetzt GC für
  vom Skript selbst erzeugte Objekte bereits - zu klären bleibt, WELCHER
  Allocator in C++ für Chunk/Scope/etc. sinnvoll ist (FreeRTOS-Heap ist
  oft klein und fragmentierungsanfällig - ein Pool-Allocator wäre
  vermutlich sinnvoll, aber das ist ein eigenes Thema für sich).

## Vorgehen

Kein "großer Bang" - jeder Punkt oben wird EINZELN angegangen (wie das
Array-Beispiel), in der Reihenfolge, die du vorgibst. Jede Änderung bleibt
dabei ein normales, lauffähiges C#-Programm (keine Zwischenstände, die nur
noch in C++ Sinn ergeben) - die C#-Fassung ist die Referenzimplementierung,
an der sich die spätere C++-Übersetzung orientiert.
