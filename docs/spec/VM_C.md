# Die VM in C (`native/vm`)

Status: **Phase B, Schritte 1 und 2** aus [ROADMAP_C_PORT.md](../ROADMAP_C_PORT.md#phase-b---die-vm-in-c) (19.11.2026). Der Spike davor:
[VM_SPIKE.md](VM_SPIKE.md). Die VM ist ein Interpreter auf `fire_rt.h` (L1): dieselben `Value`s, Objekte, Besitzlisten, Ausnahmen und Texte wie ein
natives Programm; was nur die VM braucht, liegt in ihren Seitentabellen. Die Runtime ist **nicht** geändert (der eine Eingriff hinter `FIRE_VM` aus
dem Spike bleibt der einzige), der native Build ist damit unverändert.

```
cc -O2 native/vm/fire.c -o fire -lm                    (gcc 13, clang 18; eine Datei, die alles einbindet)
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

Noch offen von Punkt 1: `MsgFn` (Aktoren), `ThreadsCatch.fn` (B3), `ProbeRawSet/Get`, `RfClass.make`, `RfMember.attrs` (B5).

## Stand gegen den Korpus

`tests/run-cases-vm.sh` mit dem C#-forge, alle Ordner (der Spike vorher: 306):

| Ordner | richtig | falsch | | Ordner | richtig | falsch |
|---|---|---|---|---|---|---|
| native | 216 | 285 | | vm-gl | 8 | 19 |
| types | 261 | 14 | | vm-lq | 23 | 9 |
| syntax | 56 | 6 | | vm-perf | 19 | 0 (1 nicht) |
| smoke | 52 | 6 | | vm-rf | 1 | 17 |
| diag | 35 | 3 | | vm-sc | 28 | 2 |
| vm-arr, vm-arr-error | 9 | 0 | | vm-sc-checked | 4 | 2 |
| vm-clone, vm-clone-error | 13 | 2 | | vm-shutdown | 0 | 6 |
| vm-ext, vm-ext-error | 19 | 1 | | vm-life | 7 | 3 |
| **zusammen** | **751** | **375** | | | | |

Von den 375 brauchen rund 320 etwas aus den späteren Schritten von Phase B, die VM meldet es als Laufzeitfehler ("does not support ... yet"):

* **Schritt 3** (rund 150): Threads, Tasks, Aktoren - `Fire`, `PostGlobal`, `Process`/`TryProcess`, `Leave`, `RegisterThreadsCatch`, Kanäle,
  `Completion`, Abbruch, `await`, `handover`; dazu `vm-shutdown` und der größte Teil von `vm-gl`.
* **Schritt 4** (rund 70): Natives der Packages (`io`, `net`, `time`, `data`, `hash`, `circuits`, `s3d`, `gpio`, ...).
* **Schritt 5** (rund 85): JSON (`__JsonCall`), Reflection (`vm-rf`, die Natives `__refl_*`), Probes, `linq`.

Der Rest (rund 55, falsche Ausgabe) ist Arbeit an Schritt 2: `#intwidth 32|16` und `#floatwidth 32` (VM_SPIKE Punkt 2, eine Variante der VM je
Breite - rund 10 Fälle), benannte Argumente (`ArgNames`), die Reihenfolge der Destruktoren weitergereichter Argumente (die C#-VM gibt sie dem Scope
des Aufgerufenen, der Generator einer Argumentliste), Typargumente von Deklarationen generischer Klassen (`List<Dog>` gegen `List<Animal>`,
DECISIONS D10), Besitz in Property-Accessoren (`SetFieldAccessor` ohne `PullBackAdopted`), einige Meldungen.
