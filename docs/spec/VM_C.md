# Die VM in C (`native/vm`)

Status: **Phase B, Schritte 1 bis 5** aus [ROADMAP_C_PORT.md](../ROADMAP_C_PORT.md#phase-b---die-vm-in-c) (19.11.2026). Der Spike davor:
[VM_SPIKE.md](VM_SPIKE.md). Die VM ist ein Interpreter auf `fire_rt.h` (L1): dieselben `Value`s, Objekte, Besitzlisten, Ausnahmen, Texte und
Threads wie ein natives Programm; was nur die VM braucht, liegt in ihren Seitentabellen. Die Runtime ist **nicht** geändert (der eine Eingriff hinter
`FIRE_VM` aus dem Spike bleibt der einzige, auch die Schritte 3 bis 5 brauchten keinen), der native Build ist damit unverändert.

```
cc -O2 native/vm/fire.c -o fire -lm -lpthread -ldl    (gcc 13, clang 18; eine Datei, die alles einbindet)
forge bytecode x.fire -o x.fbc && FIRE_PKG_PATH=<ordner von forge> ./fire x.fbc [-- args]
tests/run-cases-vm.sh <forge> [<gruppe>...] [--fire <fire>] [--cache <ordner>] [--list]
```

`tests/run-cases-vm.sh` übersetzt jeden Fall aus `tests/cases` mit dem C#-forge zu einer `.fbc` (`forge bytecode`, in jedem Modus aus `//# modes:`),
führt sie mit der C-VM aus und vergleicht nach denselben Regeln wie `tests/run-cases.sh` (`.expected`, `.unhandled`, `UNBEHANDELT:`, `.error`,
`.diagnostics` = Fehlerausgabe von forge und VM zusammen). Mit `--cache` bleiben die Programmdateien liegen und werden nur neu geschrieben, wenn
der Fall oder forge neuer ist - der zweite Lauf über den ganzen Korpus dauert dann rund 20 s (4 Kerne). Am Ende: Zählung je Ordner. Die Bibliotheken
der Paket-Natives sucht die VM im Ordner von forge (der Runner setzt `FIRE_PKG_PATH`), siehe [Natives](#natives).

## Dateien

| Datei | Inhalt | Gegenstück in C# |
|---|---|---|
| `native/base/fire_base.h` | Arena, internierte Namen, geordnete Map (Einfügereihenfolge), Datei lesen - ohne globalen Zustand, für VM und später forge | - |
| `vm.h` | Programm (`VmProgram`, `VmClass`, `VmProto`, `VmChunk`), Interpreter (`Vm`, `VmFrame`, `VmScope`, `VmHandlerRec`) | `RuntimeClass`, `FunctionProto`, `Chunk`, `VM` |
| `vm_load.c` | der Lader: das ganze `.fbc`-Format in der Reihenfolge von `ProgramFile.Read`, behalten wird alles, was die Ausführung braucht | `ProgramFile.Read`, `CompiledProgram` (OwnerClass) |
| `vm_core.c` | Scopes, Klassen bei der Runtime registrieren, Layout/Felder/Statics, Wahl der Methode (`Candidates`, `Overloads.Select`), Zugriff, Aufrufe, Spezialisierungen, Rückrufe der Runtime, Einheiten, Typargumente | `RuntimeClass`, `Overloads`, `VM.EnterCall`, `VM.IsMemberAccessAllowed` |
| `vm_natives.c` | die Kern-Natives (`natives.def`) als Tabelle nach Namen auf den Funktionen der Runtime; die Natives der Importe nach Index (graphics, windows, reflection, Pakete - `bridges.def`), gebunden beim Start; die Natives, die ein Paket dem Host lässt (`Sleep`, `__time_unit_ticks`, die Wartefunktionen der Geräte) | `NativeRegistry.CreateDefault`, `StringMethods`, `HostNatives`, `TimeNatives`, `DeviceHostNatives` |
| `vm_pkg.c` | die Natives der Pakete als Shared Library über die C-ABI v1 (`native/abi/fire_pkg_abi.h`), der Host `fire_host` in C (Konsole, IO-/Netz-Regeln, Lambdas, Dienste, Halte), auch die Bibliothek von graphics und windows (`libfire_gfx`) | `PackageNativeBinding`, `PackageHost` |
| `vm_lib.c` | `Library` (`__LibCall`) und `extern`: Aufrufe nach einer C-Signatur, die erst zur Laufzeit feststeht | `VM.Library.cs`, `VM.ResolveDynamicExtern` |
| `vm_reflect.c` | Reflection (`__refl_*`) und Probes (`probe`, `silence`, `Reflect.Probe`) | `ReflectionNatives`, `VM.Reflection.cs`, `VM.Probe.cs` |
| `vm_json.c` | JSON: die Engine des nativen Builds (`fire_json.h`) auf Tabellen, die die VM aus dem Programm baut | `JsonMethods`, `VM.Json.cs` (`CppGenerator.Json.cs` für die Tabellen) |
| `vm_threads.c` | Threads, Tasks, Aktoren, Sektionen, Jobs, `catch threads`/`catch terminate`, die Natives der Threads (Kanäle, `Completion`, Abbruch, `await [...]`, `handover`, `Sleep`), Anfang und Ende des Programms mit Threads | `FireRuntime`, `GlobalsBroker`, `VM.Tasks`, `Channels`, `Completions`, `Cancellation` |
| `vm_run.c` | die Schleife (ein `switch` über `opcodes.def`), `DeclaredType`, Arrays einer Klasse, `finally`, Abwicklung | `VM.Execute`, `DeclaredType`, `VM.ThrowException` |
| `fire.c` | das Programm `fire`: Programme nacheinander in einem Prozess, Abbau nach jedem (L6) | `fire.Runtime/Program.cs` |

## Lader

Gelesen wird alles, behalten: Namen (interniert: ein Name vergleicht sich über den Zeiger), Einheiten (die Tabelle der Datei wird beim Start die
Tabelle der Runtime, `unitsInit`; Zeile 0 ist "ohne Einheit"), Enums (`g_enums`), die eigenen Einheiten (`customUnits`: Texte, die eine Einheit
nennen, `g_textUnits`), Ressourcen (`g_resources`), die Package-Natives mit den Dateinamen ihrer Bibliotheken, die Externs (als C-Signatur),
Klassen mit Feldern, `fieldInfo` (Einheit, Art, Klasse, typisiertes Array, `arrayOfParam`, Zugriff, Attribute), die Meta-Daten der Reflection (`ClassMeta`:
Mitglieder mit Art, Typ, Zugriff, Parametern und Attributen; Basisnamen, Attribute und eigene Interfaces der Klasse), `thisReads` (was ein Getter von
`this` liest: Probes auf berechneten Properties), Statics, Methoden, Konstruktoren nach Arität und die zusätzlichen der Überladung nach Typ,
Destruktor; Funktionen mit Defaults, Zugriff, Masken (`refMask`, `captureCopyMask`), Überladungs-Deskriptoren (`paramTypes`), `paramNames`,
Typparametern und Spezialisierungen (`specArgs`, `specParamKinds`), der Selektor-Pfad eines Lambdas (`c => c.radius`); Chunks mit Code, Konstanten,
Einheiten, Funktionen, Handlern, Zeilentabellen.
Nach dem Lesen bekommt jede Funktion einer Klasse ihren `OwnerClass` (rekursiv: Lambdas, Defaults, Spezialisierungen).
Übersprungen: `receiverTypeArgs`, `keptMembers` (Stripping des nativen Backends), `graphicsNatives` (die VM hat ihre Reihenfolge selbst), Namen der Locals
(Debugger, B6).

## Interpreter

**Werte und Besitz**: die Funktionen, die der Generator für jeden Opcode ruft (`CppGenerator.Step` ist die Liste), mit den schnellen Pfaden inline.
Ein Scope hat dynamische Slots (`n` deklariert, `hi` benutzt - ein Global, das vor seiner Deklaration geschrieben wird, bekommt seinen Wert bei der
Deklaration) und zwei `OwnList`s (`own` mit der Marke des Pools, `temp` für die Anweisung); `own.parent` ist der Scope außen (TakeUpwards). Ein String
oder Lambda, das aus einem Feld, einem Global, einem Static oder einem Element gelesen wird, bekommt einen Zähler im Pool der Anweisung (`holdTemp`,
wie der Generator für "geborgte" Slots), damit Code dazwischen es nicht freigibt.

**Aufrufe**: Frames auf einem eigenen Stapel, kein C-Rekursion für Aufrufe aus dem Bytecode. `vmPushCall` übernimmt die Argumente als Slots, wendet
`CopyArgs` an (Kopie, `ref`, Ergebnis eines Aufrufs, `take`, `try take`), wählt die Spezialisierung (`Add<int>`, `VM.SpecializationFor`) und füllt
Defaults (verschachtelt). Was ein Frame dem Aufrufer gibt, sagt `retMode`: das Ergebnis, einen anderen Wert (Setter, `SetIndex`: der zugewiesene Wert)
oder nichts. Methoden werden über den Namen gesucht (`Candidates` wie `RuntimeClass`: Basisklassen, überschriebene ausgelassen, exakte Arität vor
Defaults; bei Überladung nach Typ entscheiden die Werte, `Overloads.Score/Select`), mit einem Inline-Cache je Codeposition, wenn die Wahl nicht von
den Werten abhängt. `OverloadHint` wird übergangen (die Wertung findet dieselbe Überladung). Properties: ein fehlendes Feld ist `get_`/`set_`.
Operatoren einer Klasse (`operator+`, ...) für den linken Operanden, auch in den verschmolzenen `JumpIfNot*`. Methoden auf Basistypen: die
Erweiterungsklassen `$string`/`$char`/... des Preludes, dann die eingebauten (`ToString`, `ToBytes`, die Puffer-Methoden, die Besitz-Methoden).
`GetEnumerator` auf Array/Puffer/String baut den `ListEnumerator` des Preludes.

**Zugriff** (Debug/Release, nicht Performance): private/protected von Methoden, Feldern, Statics, Konstruktoren und Erweiterungen, mit den Texten
der C#-VM.

**Ausnahmen**: wie im Spike über den Handler-Stapel der Runtime (catch auf der Wurfstelle). Jede Klasse kann geworfen werden (Catch-Typen für alle
Klassen). Neu: `finally` wie in der C#-VM über den Operandenstapel (Nutzlast und Art der Vollendung: normal, Wurf, `return`, Sprung;
`EndFinally` macht weiter) - eine Ausnahme, die ein `try` mit `finally` passiert, landet dort (`UW_RETHROW`), ein `return` im `try` oder im `catch`
läuft durch das `finally`, was ein beendeter `catch` für sein `finally` auf den Stapel legt, reist mit seinem Sprung.

**Typen**: `GuardLocal`/`GuardTop` (`guardSlot`), `DeclaredType` (Klassen, Interfaces, Alternativen, Einheiten-Bedingungen, Lambdas, Typparameter der
Klasse über die Typargumente des Objekts und die `baseTypeArgs` der Kette, Typparameter des Aufrufs) mit `FitsOrConvert` (eine Zahl für ein `T`, das
für eine Zahl steht) und Text für ein `T`, das für `string` steht. Typargumente: interniert über die Runtime (`internTArgsN`), die `TArg`s sind die
der VM (Text, keine Rückrufe), ihre Länge merkt sich die VM.

**Arrays**: Literale, `new`, Puffer, verschachtelte Arrays, typisierte Arrays (`typeArray`, `newTypedArray`), Arrays einer Klasse, eines Strings oder
eines Enums über `g_arrClasses` (siehe unten), Arrays nach einem Typargument (`T items[]`), Index-Zugriffe auf Objekte (`GetIndex`/`SetIndex`),
Zeiger (`PtrV`, `addressOfIndex`, mit `FIRE_PTRBOUNDS`).

**Records**: `==` und Hash nach Inhalt, unveränderlich nach der Konstruktion (`g_recInit` während des Konstruktors, `InitNext` für `with`), die
Prüfung am Ende des Konstruktors.

**Einheiten**: die Tabelle der Datei, die Einheiten des Programms mit Offset und Umrechnungsfunktionen, Einheiten-Forderungen als Text (auch die
"§"-kodierten Einheiten des Programms), `adaptUnit`, `coerceUnit`, `relabelUnit`, `stepOf`.

**Ende**: `terminate(code)` beendet das Skript mit dem Code; eine unbehandelte Ausnahme und ein `fatal` der Runtime beenden nur das Skript (L6).
Nach jedem Programm ist nichts mehr da (geprüft mit `--stats` für 60 Fälle in einem Prozess: 0 Handles, 0 Strings, 0 im Pool, 0 Klassen).

### Rückrufe ohne Kontext (VM_SPIKE, Punkt 1)

Gelöst mit den beiden Wegen, die der Spike vorschlug:

* **eigene Fassungen der VM**: `TArg.holds/adapt` bleiben leer - die VM prüft Typargumente mit `vmFitsDeclared`/`vmTypeArgOf` statt mit `fitsTArg`.
  `FireClassDesc.recEq/recHash/recCheck` sind generisch (die Klasse steht im Objekt).
* **ein fester Vorrat an Trampolinen**: `UnitConvFn` (16 Paare `vmUnitTo_k`/`vmUnitFrom_k`, je mit dem Index der Klasse der Umrechnung) und
  `ArrClass.fits/zero` (64 Paare, je mit dem `DeclaredType`-Text des Arrays). Reicht der Vorrat nicht, lehnt der Lader das Programm ab bzw. bleibt das
  Array ungetypt.

Aus Schritt 5 dazu: `ProbeRawSet/Get` (die Probes) haben eine **eigene Fassung der VM** - `vmProbedSet` schreibt und liest das Mitglied über seinen
Namen statt über die Rückrufe, die der Generator je Feld erzeugt; die Einträge (`ProbeNode`, `ProbeEntry`) und `probeRun` sind die der Runtime.
`RfClass`/`RfMember` (mit `make` und `attrs`) braucht die VM gar nicht: ihre Reflection beschreibt die Klassen aus den Meta-Daten der Datei
(`vm_reflect.c`), nicht aus den Tabellen eines nativen Builds. `FireLibThunk` (eine Funktion je Signatur, `Library.Get`) ersetzt ein Aufruf nach der
Signatur zur Laufzeit (`vm_lib.c`).

Noch offen von Punkt 1: `MsgFn` (Aktoren), `ThreadsCatch.fn` (B3).

## Natives

**Kern-Natives** (`natives.def`, vom Generator heute direkt gerufen): eine Tabelle Name -> Funktion (`g_vmCoreNatives`: `print`, `__StringCall`,
`__CharCall`, `__ResourceCall`, `__MathCall`, `__JsonCall`, `__EnvCall`, `__LibCall`, `__BitsCall`, `__array_copy`, `__type_arg`, `__SbCall`), beim Start
an die Indizes von `natives.def` gebunden; dieselben Funktionen der Runtime wie im erzeugten C (`stringCall`, `mathCall`, `envCall`, ...). Die Natives der
Threads, Tasks, Kanäle und Completions kommen mit Schritt 3 in dieselbe Tabelle.

**Natives der Importe**: hinter `natives.def` in der Reihenfolge, in der der Compiler sie registriert (`ImportedPreludes.Insert`): die von `graphics`,
dann `windows`, dann `reflection` (die Datei nennt sie nicht - ein Aufruf adressiert sie über den Index: `native/tables/bridges.def`, geschrieben von
`forge tables` aus `GraphicsBridge.RegisterStubs`, `WindowsBridge.RegisterStubs` und `ReflectionNatives.Register`), dann die Natives der Pakete, die die
Datei mit ihren Bibliotheken nennt (`packageNatives`, `packageNativeLibraries`). Jeder wird beim Laden nach Namen gebunden: an eine Funktion der VM
(`g_vmHostNatives`: die Reflection und was ein Paket seinem Host lässt, `"host": true` - `Sleep`, `__time_unit_ticks`, `__DEVWaitFor`,
`__DEVWaitForString`) oder an seine Bibliothek, die beim ersten Aufruf geladen wird.

**Pakete über die C-ABI v1** (`vm_pkg.c`): wie die C#-VM - dieselben Bibliotheken, die `PackageLibrary` baut (`libfire_pkg_<paket>_<import>.so`, für
graphics und windows `libfire_gfx.so`). Gesucht werden sie in den Ordnern von `FIRE_PKG_PATH` (`:`, unter Windows `;`), dann neben der Programmdatei:
im Ordner selbst, in `Packages/<paket>/lib/<rid>/` (der Paketspeicher eines forge, dort baut `forge run` sie) und in `Prebuilt/<paket>/<rid>/` (was ein
Build von forge mitbringt). Eine Bibliothek bleibt für die folgenden Programme geladen; am Ende jedes Programms `fire_pkg_reset`. Werte gehen als
Daten hinüber (Zahlen, Text, Arrays kopiert - ein Text ohne Kopie, die UTF-16 der Runtime sind die der ABI -, Byte- und Wortpuffer per Referenz),
ein Objekt oder Zeiger ist die `ArgumentException` der C#-VM. Ein Fehler der Bibliothek (1) beendet das Programm mit dem Text, eine Ausnahme (2)
wird mit `fireError` geworfen (die Klasse des Programms, sonst das Ende mit ihrem Text).

**Der Host** (`fire_host`): Konsole = die des Prozesses (`std_write` geht durch `stdout` der Runtime: die Reihenfolge mit `print` bleibt), Pfade und
Hosts sind alle erlaubt (wie die gepackte Runtime der C#-VM: sie hat keinen anderen Host), Geräte des Hosts keine (das devices-Paket nimmt seine
eigenen Treiber), `win_attach`/`win_detach` gehen an `libfire_gfx`. **Lambdas** (`FIRE_LAMBDA`): ein Handle je Lambda, das bis zum Ende des Programms
lebt; `call_lambda` auf dem Thread der VM läuft sofort, verschachtelt (`fire_cbCall`) - eine Ausnahme darin, während das Programm auf den Native wartet,
behält die VM und wirft sie am Aufruf des Natives, wenn er zurück ist (2, SPEC 8.1.4); wo niemand wartet (ein Dienst, ein Leerlauf), meldet sie sie.
Von einem anderen Thread wird der Aufruf (mit kopierten Argumenten) gemerkt und am nächsten sicheren Punkt ausgeführt - jeder Aufruf eines Paket-Natives,
jede Runde eines Wartens (`Sleep`, das Ende des Programms). **Dienste** (`request_service`): an denselben Punkten `fire_pkg_service` aller
Bibliotheken, die es haben; **Leerlauf** (`fire_pkg_idle`) in jeder Runde eines Wartens; **Halte** (`hold`): das Ende des Programms wartet, solange
eine Bibliothek es hält (offene Fenster), und bedient dabei, was ankommt. Fenster laufen so wie in der C#-VM: `Window.Show()` startet den Manager
mit seinem eigenen Thread in der Bibliothek, dessen Zyklen kommen als Dienste (mit `FIRE_DISPLAY=headless` ohne Bildschirm).

**ABI v2**: die Kopfdatei ist Version 1, die Lambdas, `request_service` und `hold` sind Felder von `fire_host` (an `size` erkannt) - die VM bietet alle.
Eine ABI, in der die Bibliothek die `Value`s der VM direkt benutzt (Roadmap: "später"), gibt es noch nicht; die Pakete benutzen sie also nicht.

**Library und extern** (`vm_lib.c`): ein natives Programm erzeugt einen Thunk je Signatur, die VM ruft nach der Signatur, die sie zur Laufzeit liest:
ganze Zahlen (Zeiger, bool, char, int jeder Breite - auf 64 Bit erweitert) und Floats (ein `float[32]` als die Bits eines float in der unteren Hälfte des
Registers) werden getrennt gezählt, die Funktion wird als eine mit 8 ganzzahligen und 8 Float-Parametern gerufen. Das gilt für die Aufrufkonventionen,
in denen beide eigene Register der Reihe nach haben (System V x86-64, AArch64) - unter Windows x64 ist eine Signatur "nicht bekannt" (Get sagt es).
`extern` mit `#extern "lib"` geht denselben Weg (CallExtern); `inline c` braucht einen C-Compiler zur Laufzeit und fehlt. Fire-Plugins
(`.fireplugin`, `__LibCall(5)`) lädt die VM noch nicht: `Library.Load` sagt, dass das Programm keine laden kann.

## JSON

`#import "json"`: die Engine des nativen Builds (`fire_json.h`, dieselben Texte und Fehler wie `JsonMethods`) auf Tabellen, die die VM beim ersten
Aufruf aus dem Programm baut, so wie `CppGenerator.Json.cs` sie schreibt: je Klasse ihre Felder, wie JSON sie sieht (`JsonFieldsOf`: Basisklasse zuerst,
`[NonSerialized]` weggelassen, `[JsonName]`), dahinter die `defined`-Felder, die nicht nach JSON gehen (ein gelesenes Objekt hat ihre Nullen), Einheit,
typisiertes Array, Klasse der Elemente; die Flags (Aktor, `OnDeserialized`, `List`, `JsonObject`, nicht serialisierbar); die Plätze von `List` und
`JsonObject`; die Texte der Typargumente (die der VM sind Texte). Jede Klasse steht in der Tabelle (`"$type"` kann jede nennen). `$id`/`$ref`, Zyklen,
Einheiten, Enums, generische Klassen kommen damit von selbst.

## Reflection und Probes

`#import "reflection"` (`vm_reflect.c`): die Beschreibungen (`Type`, `Member`, Attribute) aus den Meta-Daten der Datei; Lesen, Schreiben, Aufrufen und
Erzeugen nach Namen auf den Wegen des Interpreters - Felder mit ihren Prüfungen (Zugriff, Einheit, Art, Typ, Record), Properties und Methoden als
verschachtelte Aufrufe (Überladung nach den Werten, Defaults, Spezialisierungen), `Reflect.New` mit dem Konstruktor (ein Record gilt währenddessen als im
Bau). Die Zugriffsprüfung sieht den Code, der die Bibliothek gerufen hat (der erste Frame, dessen Klasse keine der Bibliothek ist, `VC_REFLHELPER`).

Probes: die Runtime hält sie (`FIRE_REFLECTION` in der VM: `ProbeNode` je Objekt, Flag 2 am Objekt, freigegeben mit dem Objekt); ein `SetField` auf ein
Objekt mit Flag 2 geht durch `vmProbedSet` - `changing`-Handler (einer, der `false` gibt, bricht ab), das Schreiben, `changed`-Handler bei einem anderen
Wert, dann die Probes auf berechneten Properties, die das Mitglied lesen (`ComputedSources` aus `thisReads`). Die Opcodes `Probe`, `SilenceMember`,
`SilenceValue` und `Reflect.Probe`/`Silence` mit den Fehlern der C#-VM.

## Stand gegen den Korpus

`tests/run-cases-vm.sh` mit dem C#-forge, alle Ordner (der Spike: 306; nach den Schritten 1 und 2: 751 von 1128 - der Korpus hat seitdem zwei Fälle mehr):

| Ordner | richtig | falsch | vorher | | Ordner | richtig | falsch | vorher |
|---|---|---|---|---|---|---|---|---|
| native | 306 | 197 | 216 | | vm-gl | 8 | 19 | 8 |
| types | 263 | 12 | 261 | | vm-lq | 28 | 4 | 23 |
| syntax | 56 | 6 | 56 | | vm-perf | 19 | 0 (1 nicht) | 19 |
| smoke | 52 | 6 | 52 | | vm-rf | 18 | 0 | 1 |
| diag | 38 | 0 | 35 | | vm-sc | 28 | 2 | 28 |
| vm-arr, vm-arr-error | 9 | 0 | 9 | | vm-sc-checked | 4 | 2 | 4 |
| vm-clone, vm-clone-error | 13 | 2 | 13 | | vm-shutdown | 0 | 6 | 0 |
| vm-ext, vm-ext-error | 20 | 0 | 19 | | vm-life | 7 | 3 | 7 |
| **zusammen** | **869** | **259** | **751** | | | | | |

Von den 259 brauchen rund 185 **Schritt 3** (die VM meldet es als Laufzeitfehler "does not support ... yet"): `Fire` (126 Fälle), `PostGlobal`,
`Process`/`TryProcess`, `Leave`, `RegisterThreadsCatch`/`RegisterTerminateCatch`, Kanäle (`__chan_*`), `Completion`, Abbruch, `await`, `handover`,
`__fd_wait`; dazu `vm-shutdown` und der größte Teil von `vm-gl`. Aus den Schritten 4 und 5 meldet kein Fall mehr etwas.

Der Rest (rund 75, falsche Ausgabe oder eine Ausnahme, die nicht sein soll) ist Arbeit an Schritt 2: `#intwidth 32|16` und `#floatwidth 32` (VM_SPIKE
Punkt 2), benannte Argumente (`ArgNames`: auch die UI-Fälle `Parameter 'window' is a Window, not Bool`), die Reihenfolge der Destruktoren
weitergereichter Argumente, Typargumente von Deklarationen generischer Klassen (DECISIONS D10), Besitz in Property-Accessoren und bei `try take` -
darunter `vm-lq/0180` (ein Array-Literal, das in einer Methode an `Linq.From` geht, ist zerstört, wenn die Abfrage läuft), drei UI-Fälle (ein Element,
das ein Klick neu macht) und `native/3370` (circuits: ein Array-Literal in `foreach`).

**Offen in den Schritten 4 und 5**: Fire-Plugins und Module (`Library.Load` einer `.fireplugin`, `__LibCall(5)`: das Laden der Klassen eines zweiten
Programms in ein laufendes, VM.Plugins.cs - kein Fall des Korpus), `inline c` (ein C-Compiler zur Laufzeit), `Library`/`extern` unter Windows x64,
Regeln für Pfade und Hosts (die VM erlaubt alles, wie die gepackte Runtime der C#-VM; ein Host mit Regeln - spark - kommt mit Schritt 9), die Geräte
des Hosts (`dev_*` von `fire_host`: ebenfalls spark), Typargumente eines aus JSON gelesenen Objekts (die VM kennt ihre Anzahl nur für die, die sie
selbst macht).

Schritt 3 dazu: der Körper eines Threads (`ThreadStart.fn`, `void (*)(const Value*)`) und die Funktion einer Nachricht (`MsgFn` - Aktoren, die Methoden
der Objekte der Globals aus einem Thread) bekommen, was die VM braucht, **in den Werten, die sie bekommen**: die Funktion und die Zahl der Argumente als
zwei Zahlen vor den Argumenten (die Runtime kopiert, zählt und gibt sie frei wie jede Zahl). `ThreadsCatch.fn` und `g_terminateFn`: ein Vorrat von
16 + 1 Trampolinen. Noch offen von Punkt 1: `ProbeRawSet/Get`, `RfClass.make`, `RfMember.attrs` (B5).

### Threads (Schritt 3, `vm_threads.c`)

**Das Modell der Runtime mit GIL** (`FIRE_THREADS` + `FIRE_GIL` in `fire.c`, das Modell eines Debug-Builds): ein fire-Thread ist ein Thread des
Systems, aller fire-Code läuft mit dem GIL; die Sektionen, Jobs des Hauptprogramms, Postfächer der Aktoren, Kanäle, Completions, Abbruch, Regel 10
(`toMainGlobals`, `toMainHolder`, `globalsCall`) und das Ende (`mainFinish`, `#endwait`) sind die der Runtime. Entscheidung: **GIL statt ohne GIL mit
Tasks** - ohne GIL liefe fire-Code parallel, die VM bräuchte Sperren für alles, was sie zur Laufzeit schreibt (Inline-Caches, Kandidaten, Spezialisierungen,
internierte Typargumente) und ihren Zustand am Task statt am Thread (`FIRE_TASK_STATE`: ein Feld in `ThreadLocals` - das wäre ein zweiter Eingriff in die
Runtime). Mit dem GIL sind diese Tabellen ohne Sperre sicher; Tasks (`FIRE_TASKS`) gibt die Runtime nur ohne GIL, `await [...]` läuft darum nacheinander,
`fire` ist ein Thread des Systems (600 Threads gehen: `native/1810`).

**Ein `Vm` je Strang**: der Hauptstrang hat den der Datei, ein fire-Thread einen eigenen (`vmStrandNew`: Operandenstapel, Frames, Scopes,
`finally`-Sätze); was dem Programm gehört (Klassen, Deskriptoren, internierte Typargumente) bleibt beim Hauptprogramm (`top`). `g_vmCur` ist
thread-lokal. Der globale Scope eines Threads hält seine eigenen Slots (die Argumente von `taking`/`handover`/`with`, seine Deklarationen); die Slots
unter `sharedCount` (`Fire`, Operand 2) sind die Globals des Hauptprogramms, die der Thread direkt liest und in einer Sektion schreibt (VM.cs
`LoadSharedGlobal`/`StoreSharedGlobal`; ein Objekt geht nach Regel 10 hinüber). Felder und Elemente der Objekte und Arrays der Globals, statische
Felder, Methoden ihrer Objekte (ein Job des Hauptprogramms, `globalsCall`, oder - ist das Hauptprogramm in einem Native - eine Sektion) wie im Generator.
Die Argumente einer Nachricht oder eines Jobs, die niemandem gehören (`new`, ein Ergebnis, `copy x`/`take x` in `CopyArgs`), reisen in der
Argumentliste des Aufrufs (`AL_MARK`); die aufgerufene Methode behält, was sie nimmt, der Rest stirbt mit ihrem Scope.

**Sichere Punkte** wie im erzeugten Code: der Eintritt einer Funktion (`pollSignals` am Ende von `vmPushCall` - beginnt dort ein Abwickeln, geht der
neue Frame sofort: `g_vmUnwindStub`) und der Sprung einer Schleife zurück (`pollLoop`, `VM_LOOP_POINT`: `Jump`, `JumpIfFalse`/`...Peek`, die
verschmolzenen `JumpIfNot*`, auch nach einem Operator). Dort gibt der Strang den GIL ab, bedient das Hauptprogramm seine Warteschlange (`#globaljobs`),
beginnen `leave`/`terminate`. `Sleep` (eine Funktion, die der Wirt selbst ausführt: `HostNatives`) wartet ohne GIL, `terminate` beendet es.
Ein `fire` gibt dem neuen Thread den GIL einmal, bevor der Starter weitermacht (der Thread beginnt, bevor eine kurze Schleife des Starters fertig ist -
in der C#-VM ist der Interpreter langsamer; `vm-shutdown/0040` war sonst ein Rennen).

**Ende**: `leave` und `terminate` sind das Abwickeln `UW_LEAVE` der Runtime - die `finally`-Blöcke laufen (Vollendung 4, `EndFinally` beginnt das
Verlassen neu), kein `catch` passt; danach wartet das Hauptprogramm auf seine Threads (`mainFinish`: `catch terminate`, `#endwait`), dann gehen die Globals
**mit** ihren Destruktoren (vorher endete `terminate` das Skript ohne). Der Exitcode ist der von `terminate(n)`. Ein Thread mit einer Ausnahme, die
nichts fängt, endet (`threadUnhandled`), sein globaler Scope geht auf den Friedhof der Runtime, die Ausnahme zum Hauptprogramm (`catch threads` oder
das Ende des Programms). Ein Laufzeitfehler auf einem Thread (`fatal`) beendet den Thread und das Programm mit 1 (wie `FireRuntime`); die Handler
gehen vor dem Sprung (`fire_vmExit`: einige liegen auf dem Stapel der Funktionen der Runtime, die der Sprung verlässt). Nach jedem Skript ist der
Zustand der Threads zurückgesetzt (Terminate, Signale, Domäne, `catch threads`, Friedhof, `#globaljobs`, `#endwait`); übrige Threads werden beendet und
eingesammelt - geprüft mit `--stats` für 36 Programme mit Threads in einem Prozess: nichts bleibt.

**Lambdas** mit einem Ziel in einem anderen Thread oder einem Aktor (`Lam.home`) ruft die VM über `callLamU` (dort laufen sie, mit dem GIL: hier, außer
ihr Ziel ist mit seinem Thread gestorben); eine `Completion`, ein Kanal (`with`: ein eigenes Ende, `__End`), Abbruch (`until`: `CancelPush`/`CancelPop`)
und `await on` sind die Funktionen der Runtime.

Geprüft unter AddressSanitizer (mit LeakSanitizer) und ThreadSanitizer (gcc 13): `vm-gl`, `vm-shutdown`, `vm-life`, `native` - ohne Befund außer in
Fällen, die an fehlenden Natives scheitern (ein Leck nach dem `fatal`). Unter ASan ist `native/4391` (ein Thread rechnet, während das Hauptprogramm
Strings tauscht) zu langsam für das Zeitlimit.

## Stand gegen den Korpus

`tests/run-cases-vm.sh` mit dem C#-forge, alle Ordner (der Spike: 306; nach Schritt 2: 751):

| Ordner | richtig | falsch | | Ordner | richtig | falsch |
|---|---|---|---|---|---|---|
| native | 322 | 181 | | vm-gl | 27 | 0 |
| types | 268 | 7 | | vm-lq | 26 | 6 |
| syntax | 62 | 0 | | vm-perf | 19 | 0 (1 nicht) |
| smoke | 58 | 0 | | vm-rf | 1 | 17 |
| diag | 35 | 3 | | vm-sc | 28 | 2 |
| vm-arr, vm-arr-error | 9 | 0 | | vm-sc-checked | 5 | 1 |
| vm-clone, vm-clone-error | 15 | 0 | | vm-shutdown | 6 | 0 |
| vm-ext, vm-ext-error | 19 | 1 | | vm-life | 10 | 0 |
| **zusammen** | **910** | **218** | | | | |

Von den 218 brauchen 176 etwas aus den späteren Schritten von Phase B, die VM meldet es als Laufzeitfehler ("does not support ... yet"):

* **Schritt 4** (rund 100): Natives der Packages (`io`, `net`, `time` - `__time_now` -, `data`, `hash`, `circuits`, `s3d`, `gpio`, ...); darunter
  rund 25 Fälle der Threads (Server, Natives in Threads, `Sleep` mit Zeiten aus `time`), die erst damit laufen.
* **Schritt 5** (rund 75): JSON (`__JsonCall`), Reflection (`vm-rf`, die Natives `__refl_*`, die "tryable" Natives), Probes, `linq`; darunter
  `native/1080`-`1100` (`#globaljobs` mit `linq`).

Der Rest (42, falsche Ausgabe) ist Arbeit an Schritt 2: `#intwidth 32|16` und `#floatwidth 32` (VM_SPIKE Punkt 2, eine Variante der VM je Breite -
rund 12 Fälle), `try take`/`take` als Wert und in Zuweisungen, `Takes.Children` (rund 8), Arrays mit `defined` und Typargumente mit Einheiten,
die Reihenfolge der Destruktoren beim Verlassen nach einer Ausnahme, einige Meldungen. Erledigt aus Schritt 2 sind: benannte Argumente (`ArgNames`,
`VM.FillNamedArgs`), Besitz in Property-Accessoren (`SetFieldAccessor`: an einen Setter wie `take x`, ein Feld von `this` übernimmt es, die Rückgabe
eines Getters gibt es zurück - `accKeep`/`accReturn` der Runtime; die Claim-Stellen eines Setters, `SetFieldClaim` mit dem Setter-Bit und `CopyArgs` 6),
Typargumente in Deklarationen generischer Klassen (`List<Dog>` geht nach `List<Animal>`, DECISIONS D10), die Reihenfolge der Destruktoren
weitergereichter Argumente (nach den Locals des Aufgerufenen, wie im Generator), `break`/`continue`/`return` aus einem `catch` in einem `catch`, der
Zugriff nach dem Freigeben in `native/1656` (eine `string`-Deklaration, deren `ToString()` wirft) und in `native/0902` (`Take...` eines zerstörten Werts).
