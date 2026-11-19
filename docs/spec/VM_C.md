# Die VM in C (`native/vm`)

Status: **Phase B, Schritte 1 bis 6** aus [ROADMAP_C_PORT.md](../ROADMAP_C_PORT.md#phase-b---die-vm-in-c) (19.11.2026). Der Spike davor:
[VM_SPIKE.md](VM_SPIKE.md). Die VM ist ein Interpreter auf `fire_rt.h` (L1): dieselben `Value`s, Objekte, Besitzlisten, Ausnahmen, Texte und
Threads wie ein natives Programm; was nur die VM braucht, liegt in ihren Seitentabellen. Die Runtime ist **nicht** geändert (der eine Eingriff hinter
`FIRE_VM` aus dem Spike - und für den Debugger `"engine": "vm"` im `hello` - sind die einzigen, auch die Schritte 3 bis 6 brauchten keine), der native
Build ist damit unverändert.

```
cc -O2 native/vm/fire.c -o fire -lm -lpthread -ldl    (gcc 13, clang 18; eine Datei, die alles einbindet)
forge bytecode x.fire -o x.fbc && FIRE_PKG_PATH=<ordner von forge> ./fire x.fbc [-- args]
./fire --debug 'tcp:127.0.0.1:PORT;token=T' x.fbc        (oder FIRE_DEBUG=...; der Debugger, siehe "Debugger")
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
| `vm_debug.c` | der Debugger: das Protokoll nativer Programme über den Agenten der Runtime (`fire_debug.h`), die Halte vor jeder Instruktion, die Frames für den Agenten | `VM.StepLine`/`StepInto`/`StepOut`/`RunUntilBreakpoint`, `DebugScopeChain`, `DebugGlobals`, `PauseOnUnhandled` |
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
Übersprungen: `receiverTypeArgs`, `keptMembers` (Stripping des nativen Backends), `graphicsNatives` (die VM hat ihre Reihenfolge selbst). Für den
Debugger behalten: die Namen der Locals (`DebugLocalNames`: Tiefe -1 nach dem Offset des `DeclareLocal`, Tiefe 0 die Parameter), `firstUserSource`,
die Quellen (Bit 1 der Datei, [BYTECODE.md](BYTECODE.md#48-das-dateiformat-fbc)), die Liste aller Chunks (die Zeilen mit Code).

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

## Debugger (Schritt 6, `vm_debug.c`)

`fire x.fbc` mit `FIRE_DEBUG` (oder `fire --debug <spec> x.fbc`, dasselbe) spricht das [Debug-Protokoll nativer Programme](NATIVE_DEBUG_PROTOCOL.md):
`NativeDebugTarget` in spark debuggt ein Programm der VM wie ein natives (L4). Das Protokoll selbst ist **der Agent der Runtime** (`fire_debug.h`,
`FIRE_DEBUG`/`FIRE_DEBUG_AGENT` im Build von `fire`): Transporte tcp und stdio (gerahmt, mit der Eingabe des Programms), sein Thread, alle Befehle
(`breakpoints`, `continue`, `step`, `pause`, `threads`, `trace`, `scopes`, `globals`, `this`, `expand`, `set`, `resume`, `terminate`, `profile`, `detach`),
die Werte (V, `ref`-Pfade), `set` (durch den angehaltenen Thread selbst, ein Feld über `vmReflWrite` mit Einheit, Art, Typ, Record, Probes), der Profiler,
die Threads (`thread`/`threadExited` über `threadEntry` der Runtime). Was ein natives Programm dafür erzeugt, liefert die VM: das `DbgProgram` (Quellen aus
der Datei, `firstUserSource`, Einstieg, Zeilen mit Code, die Globals), `fire_dbgClass` (die Felder nach `layout`) und `fire_dbgSetField`. `hello` sagt
`"engine": "vm"` (der einzige Eingriff in `fire_debug.h`, hinter `FIRE_VM`). Ohne `FIRE_DEBUG` kostet der Debugger eine Abfrage von `g_dbgArmed` je Instruktion.

**Halte**: ein Haken vor jeder Instruktion (`vmRun`, nur mit angeschlossenem Debugger) entscheidet wie die Schrittschleifen der C#-VM nach jeder
Instruktion: Position = `Chunk.GetLocation` der Instruktion (mit dem Bereich der Zeile gemerkt), Tiefe = die Frames der C#-VM; Haltepunkt beim Betreten
einer Zeile, `step line`/`into`/`out`, `pause`. **Verschachtelt** (unsichtbar wie in der C#-VM) ist ein Frame, den keine Aufruf-Instruktion des Frames
darunter gemacht hat (`Call`, `CallMethod`, `CallBaseMethod`, `CallStaticMethod`, `CallProtoWithThis`, `NewObject*`, `ConstructBase`) oder der
verschachtelt läuft (`FR_NESTED`): Getter, Setter, Operatoren, `GetIndex`/`SetIndex`, Destruktoren, `ToString`, Standardwerte, Lambdas eines Natives, die
Ausnahme, die die Runtime macht. Ein `catch`-Block (`FR_CATCH`, auf der Wurfstelle) ist der Frame seiner Funktion; die Frames der Wurfstelle darunter sind
für den Debugger weg. **Unbehandelt**: ein `throw`, den kein `catch` nähme (der Boden des Skripts zählt nicht, ein `finally` dazwischen auch nicht), hält
vor dem Wurf an; `resume` macht den `throw` zu `undefined`. Eine Ausnahme der Runtime (Index, Division, ...) hält im Hauptprogramm am Boden des Skripts an
(`vmUnhandledFn`, auf der Wurfstelle), in einem fire-Thread im Agenten der Runtime (`fire_dbgUnhandled`, mit den Frames, die `vmMakeException` vorher
festhält).

**Frames für den Agenten**: der Agent liest den Schattenstapel eines nativen Debug-Builds (`DbgFrame`). Die VM baut ihn, wenn ein Thread anhält: die
logischen Frames (innerster zuerst, Name wie `CppGenerator.DebugFunctionName`: `<main>`, `<fire>`, `<lambda>`, `Klasse.Methode`, `Klasse.construct`; ein
Aufrufer auf der Zeile des Aufrufs), die Scope-Kette des innersten als Ebenen (je Scope eine, `functionTop`, wo der Scope darüber der globale ist), ein
fire-Thread mit der Ebene seiner eigenen Globals (`T:`), `this`. Die Namen der Slots schreibt der Haken beim `DeclareLocal` in den Scope
(`VmScope.names`, wie `Scope.SetDebugName`), sonst die Parameter des Chunks, sonst `(local N)` bzw. `(global N)` - wie `VM.DebugScopeChain`/`DebugGlobals`.

**Tests**: `FIRE_TESTS_ONLY=native FIRE_TESTS_FILTER=VM-Debugger` (`NativeDebugTests.Vm.cs`): `fire` aus `native/vm/fire.c` gebaut, jeder Fall des nativen
Debuggers als `.fbc` (Modus Debug), über TCP gegen die C#-VM verglichen - Halte, Tiefen, Aufrufstapel, Scopes, `this`, Globals an jedem Halt, jeder
Step Into/Over bis zum Ende (auch durch den Prelude) -, dazu die Protokoll-Prüfungen der nativen Builds, die nicht das C betreffen (expand/set, gepackte
Felder, hello mit Quellen und SHA-256, detach, Profiler, pause/terminate, Threads), und ein Fall über stdio. Stand: VMDBG_STAND.

**Offen**: die Eingabe des Programms über den gerahmten Transport erreicht nur, was `fire_dbgStdinRead` liest (die Natives der Pakete lesen stdin
selbst); fire-Plugins (`sources`) lädt die VM noch nicht; Aufgaben mit `await [...]` auf eigenen Threads melden sich als fire-Threads; ein Frame, der an
derselben Stelle des Stapels mit derselben Funktion und demselben (wiederverwendeten) Scope einen verschachtelten ersetzt, behält dessen Einordnung.

## Stand gegen den Korpus

`tests/run-cases-vm.sh` mit dem C#-forge, alle Ordner, nach dem Zusammenführen der Schritte 3 (Threads) und 4/5 (Natives, JSON, Reflection)
(der Spike: 306; nach den Schritten 1 und 2: 751; Schritte 4/5 allein 869, Schritt 3 allein 910):

| Ordner | richtig | falsch | | Ordner | richtig | falsch |
|---|---|---|---|---|---|---|
| native | 450 | 53 | | vm-gl | 27 | 0 |
| types | 270 | 5 | | vm-lq | 30 | 2 |
| syntax | 62 | 0 | | vm-perf | 19 | 0 (1 nicht) |
| smoke | 58 | 0 | | vm-rf | 18 | 0 |
| diag | 38 | 0 | | vm-sc | 28 | 2 |
| vm-arr, vm-arr-error | 9 | 0 | | vm-sc-checked | 5 | 1 |
| vm-clone, vm-clone-error | 15 | 0 | | vm-shutdown | 6 | 0 |
| vm-ext, vm-ext-error | 20 | 0 | | vm-life | 10 | 0 |
| **zusammen** | **1065** | **63** | | | | |

Offen sind damit vor allem: `#intwidth`/`#floatwidth` (eine VM je Breite), `try take`/`Takes.Children`, `defined`-Arrays, Typargumente mit
Einheiten, die Reihenfolge der Destruktoren nach einer Ausnahme, einige Meldungstexte, `vm-lq/0180` (ein Array-Literal an `Linq.From` in einer
Methode ist zerstört, wenn die Abfrage läuft), `vm-sc/0060` (return im try/catch/finally verschachtelter Blöcke), `vm-sc/0070` (Zeiger auf eine Lokale).
