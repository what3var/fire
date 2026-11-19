# Die VM in C (`native/vm`)

Status: **Phase B, Schritte 1 bis 3** aus [ROADMAP_C_PORT.md](../ROADMAP_C_PORT.md#phase-b---die-vm-in-c) (19.11.2026). Der Spike davor:
[VM_SPIKE.md](VM_SPIKE.md). Die VM ist ein Interpreter auf `fire_rt.h` (L1): dieselben `Value`s, Objekte, Besitzlisten, Ausnahmen, Texte und
Threads wie ein natives Programm; was nur die VM braucht, liegt in ihren Seitentabellen. Die Runtime ist **nicht** geändert (der eine Eingriff hinter
`FIRE_VM` aus dem Spike bleibt der einzige, auch Schritt 3 brauchte keinen), der native Build ist damit unverändert.

```
cc -O2 native/vm/fire.c -o fire -lm -lpthread          (gcc 13, clang 18; eine Datei, die alles einbindet)
forge bytecode x.fire -o x.fbc && ./fire x.fbc [-- args]
tests/run-cases-vm.sh <forge> [<gruppe>...] [--fire <fire>] [--cache <ordner>] [--list]
```

`tests/run-cases-vm.sh` übersetzt jeden Fall aus `tests/cases` mit dem C#-forge zu einer `.fbc` (`forge bytecode`, in jedem Modus aus `//# modes:`),
führt sie mit der C-VM aus und vergleicht nach denselben Regeln wie `tests/run-cases.sh` (`.expected`, `.unhandled`, `UNBEHANDELT:`, `.error`,
`.diagnostics` = Fehlerausgabe von forge und VM zusammen). Mit `--cache` bleiben die Programmdateien liegen und werden nur neu geschrieben, wenn
der Fall oder forge neuer ist - der zweite Lauf über den ganzen Korpus dauert dann rund 20 s (4 Kerne). Am Ende: Zählung je Ordner.

## Dateien

| Datei | Inhalt | Gegenstück in C# |
|---|---|---|
| `native/base/fire_base.h` | Arena, internierte Namen, geordnete Map (Einfügereihenfolge), Datei lesen - ohne globalen Zustand, für VM und später forge | - |
| `vm.h` | Programm (`VmProgram`, `VmClass`, `VmProto`, `VmChunk`), Interpreter (`Vm`, `VmFrame`, `VmScope`, `VmHandlerRec`) | `RuntimeClass`, `FunctionProto`, `Chunk`, `VM` |
| `vm_load.c` | der Lader: das ganze `.fbc`-Format in der Reihenfolge von `ProgramFile.Read`, behalten wird alles, was die Ausführung braucht | `ProgramFile.Read`, `CompiledProgram` (OwnerClass) |
| `vm_core.c` | Scopes, Klassen bei der Runtime registrieren, Layout/Felder/Statics, Wahl der Methode (`Candidates`, `Overloads.Select`), Zugriff, Aufrufe, Spezialisierungen, Rückrufe der Runtime, Einheiten, Typargumente | `RuntimeClass`, `Overloads`, `VM.EnterCall`, `VM.IsMemberAccessAllowed` |
| `vm_natives.c` | die Natives jedes Programms (`natives.def`) auf den Funktionen der Runtime | `NativeRegistry.CreateDefault`, `StringMethods` ... |
| `vm_threads.c` | Threads, Tasks, Aktoren, Sektionen, Jobs, `catch threads`/`catch terminate`, die Natives der Threads (Kanäle, `Completion`, Abbruch, `await [...]`, `handover`, `Sleep`), Anfang und Ende des Programms mit Threads | `FireRuntime`, `GlobalsBroker`, `VM.Tasks`, `Channels`, `Completions`, `Cancellation` |
| `vm_run.c` | die Schleife (ein `switch` über `opcodes.def`), `DeclaredType`, Arrays einer Klasse, `finally`, Abwicklung | `VM.Execute`, `DeclaredType`, `VM.ThrowException` |
| `fire.c` | das Programm `fire`: Programme nacheinander in einem Prozess, Abbau nach jedem (L6) | `fire.Runtime/Program.cs` |

## Lader

Gelesen wird alles, behalten: Namen (interniert: ein Name vergleicht sich über den Zeiger), Einheiten (die Tabelle der Datei wird beim Start die
Tabelle der Runtime, `unitsInit`; Zeile 0 ist "ohne Einheit"), Enums (`g_enums`), die eigenen Einheiten (`customUnits`: Texte, die eine Einheit
nennen, `g_textUnits`), Ressourcen (`g_resources`), die Namen der Package-Natives (für Meldungen), Klassen mit Feldern, `fieldInfo` (Einheit, Art,
Klasse, typisiertes Array, `arrayOfParam`, Zugriff), Statics, Methoden, Konstruktoren nach Arität und die zusätzlichen der Überladung nach Typ,
Destruktor; Funktionen mit Defaults, Zugriff, Masken (`refMask`, `captureCopyMask`), Überladungs-Deskriptoren (`paramTypes`), `paramNames`,
Typparametern und Spezialisierungen (`specArgs`, `specParamKinds`); Chunks mit Code, Konstanten, Einheiten, Funktionen, Handlern, Zeilentabellen.
Nach dem Lesen bekommt jede Funktion einer Klasse ihren `OwnerClass` (rekursiv: Lambdas, Defaults, Spezialisierungen).
Übersprungen: Meta-Daten der Klassen (Reflection, B5), Externs, `receiverTypeArgs`, Namen der Locals (Debugger, B6).

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
