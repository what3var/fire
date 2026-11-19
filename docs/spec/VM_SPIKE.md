# Spike: die VM als Interpreter auf `fire_rt.h` (Phase B, Schritt 0)

Status: **durchgeführt (19.11.2026). Ergebnis: L1 trägt.** Die VM in C kann ein Interpreter auf der Runtime des nativen Backends sein; der native
Build zahlt nichts dafür. Gebraucht wurde **ein** Eingriff in die Runtime hinter `FIRE_VM` (8 Zeilen, `exitNow`), sonst nichts. Der Fallback aus
[ROADMAP_C_PORT.md, "Risiken"](../ROADMAP_C_PORT.md#risiken) (eigenständige VM mit eigenem Wertmodell) ist nicht nötig.

Geprüft wurde, was die Roadmap für den Spike verlangt ([L1](../ROADMAP_C_PORT.md#l1-eine-runtime-die-vm-ist-ein-interpreter-auf-fire_rth),
[L6](../ROADMAP_C_PORT.md#l6-compiler-und-vm-sind-bibliotheken-und-packages), Phase B Schritt 0): Rechnen, Locals/Scopes mit dynamischen Slots
(BYTECODE 3), Aufrufe, eine Klasse mit Feldern und Destruktor, `try`/`catch`/`resume`, Klassen zur Laufzeit registriert, `print`, String-Verkettung,
und zwei (hier: 16) Programme nacheinander in einem Prozess, darunter solche, die mit einer unbehandelten Ausnahme bzw. einem Laufzeitfehler enden.

## Was gebaut wurde

`native/vm` (rund 1 750 Zeilen C, ohne Build-System):

| Datei | Inhalt |
|---|---|
| `fire.c` | das Programm `fire`: bindet die Runtime (`FIRE_VM`, `FIRE_EXCEPTIONS`, `FIRE_RT_IMPLEMENTATION`) und die anderen Dateien ein; führt die übergebenen `.fbc` nacheinander aus, räumt nach jedem ab; `--stats` meldet, was danach noch lebt |
| `vm.h` | die Strukturen: Programm, Chunk, Funktion, Klasse; Scope, Frame, registrierter Handler, VM |
| `vm_load.c` | der Lader des `.fbc` (Arena, internierte Namen) |
| `vm_run.c` | der Interpreter, die Rückrufe für die Runtime (Destruktor, `ToString`, `catch`, Lambda, die `make*Error` des Generators), die Registrierung der Klassen |
| `tests/*.fire`, `tests/run.sh` | die Testskripte und der Vergleich mit `forge run` |

```
cc -O2 native/vm/fire.c -o fire -lm              (gcc 13 und clang 18, -Wall -Wextra ohne Warnung; gcc -std=c11 -Wpedantic ohne Warnung)
forge bytecode x.fire -o x.fbc && ./fire x.fbc     (mehrere Dateien: nacheinander in einem Prozess)
native/vm/tests/run.sh <forge>                     (alle Skripte: C-VM gegen C#-VM, dann alle zweimal in einem Prozess)
```

`-lm` braucht nur glibc (wie jedes C-Programm mit `fmod`). MSVC war nicht verfügbar; der Code benutzt nichts außer C11, `setjmp` und der
Plattformschicht der Runtime.

### Lader: das ganze Format, ein Teil davon behalten

Gelesen wird **das ganze Format** aus BYTECODE 48 in der Reihenfolge von `ProgramFile.Read` - auch, was der Spike nicht braucht, damit eine Datei des
C#-Compilers bis zum Ende gelesen und wie dort geprüft wird (Magic, Version, "bytes behind its end", Bereiche der Verweise, abgeschnittene Datei).
Behalten werden: die Namen (interniert, ein Name vergleicht sich über seinen Zeiger), die Klassen (Basis, Flags, Felder mit Initialisierer, statische
Werte und Felder, Methoden mit Überladungen, Konstruktoren nach Arität, Destruktor), die Chunks (Code, Konstanten, Funktionen, Handler-Vorlagen) und die
Funktionen (Parameterzahl, Default-Werte, Zugriff, `static`). Die Format-Regel "was zweimal vorkommt, steht einmal da" (Funktion/Chunk über ihre
Nummer) ergibt dieselbe Gestalt wie in C#.

Geparst, aber nicht ausgewertet: Einheiten (außer "ohne Einheit"), Enums und eigene Einheiten (eine Enum-Konstante behält ihre Nummer, `EnumV`),
Ressourcen, Externs, die Meta-Daten der Klassen (Reflection), `fieldInfo`, `extraConstructors`, Zeilentabellen, Namen der Locals. Ein Programm mit dem
Prelude ist rund 110 KB groß (die Prelude-Klassen kommen immer mit); Laden dauert unter 1 ms, der ganze Prozess für `print(1)` 2 ms.

String-Konstanten werden beim Laden zu `Str` der Runtime mit `rc = IMMORTAL` (wie die statischen Konstanten des Generators), gehören dem Programm und
gehen mit ihm. Ihr Text ist zugleich ein internierter UTF-8-Name, denn Feld-, Methoden- und Klassennamen stehen im Bytecode als String-Konstanten.

### Interpreter: 69 Opcodes

`LoadConst Pop Dup Swap RotateUnderTop LoadLocal StoreLocal StoreLocalPop LoadGlobal StoreGlobal StoreGlobalPop DeclareLocal`,
`Add Sub Mul Div Mod Neg LogicalNot Eq NotEq Lt LtEq Gt GtEq ArithLocalConstPop ArithGlobalConstPop`,
`Jump JumpIfFalse JumpIfFalsePeek JumpIfTruePeek JumpIfNotLt JumpIfNotLtEq JumpIfNotGt JumpIfNotGtEq JumpIfNotEq JumpIfNotNotEq`,
`EnterScope ExitScope ReleaseTemps ClaimToScope HoistValue`, `CallNative` (nur `print`), `MakeLambda Call Return Halt LoadThis CallProtoWithThis
CallMethod CallStaticMethod NewObject ConstructBase CopyArgs`, `GetField SetField SetFieldOnThis SetFieldClaim SetStaticFieldOnInit SetStaticField
GetStaticField`, `GuardLocal GuardTop CheckUnit`, `RegisterHandler UnregisterHandler Throw ResumeException ClearPendingResume`.

Jeder Opcode ist die Funktion der Runtime, die der Generator für ihn aufruft: der schnelle Pfad (zwei Ints derselben Einheit) steht wie im erzeugten
Code inline, alles andere geht an `addR`/`sub`/`mul`/`divide`/`modulo`/`negate`/`opEq`/`lt`/`truthy`, `concat`, `print`, `newObject`, `leave`,
`claimToList`/`hoistFrom`/`claimToHolder`/`transferTree`/`adopt`, `guardSlot`/`guardStr`, `throwValue`/`resumeThrow`/`clearPending`/`exitJump`/`exitReturn`.
Ein nicht unterstützter Opcode, Native oder Fall ist ein `fatal` der Runtime (siehe unten: beendet nur das Skript).

**Scopes** (BYTECODE 3): ein `VmScope` hat dynamische Slots und als Besitzer zwei `OwnList`s der Runtime - `own` (Marke: Höhe des Temporär-Pools beim
Eintritt) und `temp` (die Temporären der Anweisung, `home = &own`). Das ist genau das Paar `OP`/`TOP`, das der Generator je Scope anlegt; `ReleaseTemps`
ist `leave(&temp)`, `ExitScope` `leave(&temp); leave(&own)`. Slots halten wie die Variablen des erzeugten Codes eine Zählung (retain beim Speichern,
release beim Verlassen). Die Scope eines Aufrufs hat den globalen Scope als Eltern (BYTECODE 5); Scopes werden wiederverwendet (freie Liste).

**Aufrufe**: Frames liegen auf einem eigenen Stapel (fest: 8 192 Frames, 65 536 Werte), ein Aufruf aus dem Bytecode rekursiert nicht in C. Argumente
werden Slots 0.. (der Aufgerufene hält seine eigenen, wie im erzeugten Code); fehlende bekommen ihren Default-Wert (eine Funktion, verschachtelt
ausgeführt). `Return` gibt den Wert mit `transferTree` über alle Scope-Listen des Aufrufs an den Aufrufer weiter, zählt ihn für die Reise, der Aufrufer
übernimmt ihn mit `adopt` - dieselbe Folge wie im erzeugten Code. Methoden und Felder werden über den Namen gesucht (Klassenkette), mit einem
Inline-Cache je Codeposition (Klassen-Id -> Funktion bzw. Feldindex), in einer Seitentabelle des Chunks.

**Lambdas**: `allocLam` der Runtime mit `fn = vmLamThunk`; die Funktion des Lambdas reist als **verstecktes letztes Capture** mit (ein `Int` mit dem
Zeiger) - kein neues Feld in `Lam`. `Call` erkennt ein Lambda der VM am `fn` und ruft ohne C-Rekursion; ein Lambda, das ein Native der Runtime ruft,
läuft über `vmLamThunk` verschachtelt.

**Klassen zur Laufzeit**: beim Laden registriert die VM die Klassen des Programms bei der Runtime, wie `fireRegisterPlugin` es für die Klassen eines
Plugins tut: `g_classDescs` wird eine neue Tabelle (die alten Zeilen vorne, die Klassen bekommen die nächsten Ids), je Klasse Name, Zahl der Felder
(Layout: die der Basis zuerst), Index von `message`, `destroy` (wenn die Klasse oder eine Basis einen Destruktor hat), `toString` (wenn sie `ToString()`
hat), `excTypes` (die Catch-Typen einer Ausnahmeklasse: Id + 1 jeder Klasse ihrer Kette; `Exception` ist `FIRE_EXC_ANY`), `flags` (`[NoCopy]`). Die
Catch-Typen eines `try` werden beim ersten `RegisterHandler` über die Klassennamen aufgelöst. Beim Entladen wird die Tabelle wieder auf den Stand davor
gekürzt - das nächste Programm bekommt dieselben Ids.

Die Rückrufe der Deskriptoren (`destroy(Obj*)`, `toString(Value)`) haben keinen Kontext; sie sind je **ein** generischer Rückruf der VM, der die Klasse
über `o->cls` in einer Seitentabelle der VM findet und den Destruktor (die Kette, abgeleitete Klasse zuerst) bzw. `ToString()` verschachtelt ausführt.

**Reflection-Zeilen** (`RfClass`) wurden nicht gebaut: in der Runtime gibt es dafür nur den Datentyp, alles was sie liest (`rf_find`, die Funktionen
hinter `Reflect.*`) erzeugt der Generator. Die VM schreibt diese Funktionen also ohnehin selbst (Phase B Schritt 5) und braucht dafür nichts von der
Runtime; die Zeilen sind dort nur Daten aus den Meta-Daten der Klasse, die der Lader schon liest.

**Ausnahmen**: die VM benutzt den Handler-Stapel der Runtime. `RegisterHandler` legt einen `VmHandlerRec` an (am Anfang ein `Handler` der Runtime mit
`fn = vmCatchFn`, dazu Frame, Scope und Stapelhöhe des `try`) und `pushHandler`. Wirft der Bytecode (`Throw`) oder die Runtime (`divideByZero`,
`fireError`, `destroyedError`, ...), sucht `throwValue` wie im nativen Programm den Handler und ruft seinen catch-Block **auf der Wurfstelle** auf: die
VM führt ihn verschachtelt auf einem Pseudo-Frame der Funktion aus, der das `try` gehört (gleicher Chunk, `this`, Scope-Kette). Verlassen wird er über
den Abwicklungszustand der Runtime, genau wie die erzeugten catch-Funktionen es tun:

* Ende des catch-Scopes, dann der Sprung hinter das `try` -> `exitJump(h, Adresse)`; ein `break`/`continue` aus dem catch (weitere `ExitScope` vor dem
  Sprung) wird gezählt und beim Landen nachgeholt;
* `return` im catch -> `exitReturn(h, Wert)`;
* `e.resume(v)` -> `resumeThrow`: der catch-Frame geht, `throwValue` gibt an der Wurfstelle `v` zurück, die Frames dort sind nie abgebaut worden.

Ist `g_unwind.active` gesetzt, baut der Interpreter seine Frames ab, bis zum Frame, dem der Ziel-Handler gehört (`leave` der Scopes; Destruktoren laufen
dabei, wie die Runtime es vorsieht), und landet dort: Sprung an die Adresse, `return`, oder - der Handler hatte keinen passenden catch (`UW_RETHROW`) -
erneut `throwValue(x, false)`. Das ist die Abwicklung des erzeugten Codes ("jede Funktion prüft das Flag nach einem Aufruf"), nur über die Frames der VM.
Gezeigt in `03_exc` und `08_exc2`: eigene Ausnahmeklasse, Fehler der Runtime als Ausnahme (Division durch 0), Wurf durch mehrere Aufrufe, ein nicht
passendes inneres `try` (Rethrow), Destruktoren beim Abwickeln (auch die Reihenfolge stimmt mit der C#-VM überein), `return` im catch, `resume` über
vier Aufrufe hinweg, eine Ausnahme aus einem catch-Block, `break` aus einem catch.

### L6: ein Skript endet, der Prozess nicht

* **Unbehandelte Ausnahme**: am Boden jedes Skripts liegt ein Handler der VM (Catch-Typ -1, fängt alles). Er schreibt den Text der VM (`Unhandled
  exception of type 'Exception': boom` - dieselbe Funktion `reportUnhandled`, die der native Build benutzt) und verlässt das Skript mit `exitJump` auf
  sich selbst: alle Frames werden abgebaut, danach der globale Scope, die statischen Felder, die Klassen. Wie in der C#-VM und im nativen Build laufen
  dabei **keine Destruktoren** (das Programm "endet hier"; dafür setzt die VM `g_zombies.quiet` der Runtime, mit dem sie ohnehin zerstört, ohne
  Destruktoren zu rufen).
* **Fehler der Runtime** (`fatal`, `fatalException`, `unsupported`, `requireRef`, `takingViolation`, ein nicht behandelter Wurf ohne Handler): alle Wege
  enden in `exitNow`. Mit `FIRE_VM` ruft `exitNow` vorher `fire_vmExit`, das per `longjmp` zum Starter des Skripts zurückkehrt; der setzt den
  halbfertigen Zustand der Runtime zurück (Handler-Stapel, `g_pending`, Abwicklung, Batch-Tiefe) und räumt ab wie oben. Gezeigt mit `07_fatal`
  (Array-Literal: im Spike nicht unterstützt -> `fatal`).
* **Prüfung**: `run.sh` führt alle Programme zweimal hintereinander in **einem** Prozess aus (16 Läufe, darunter die zwei scheiternden je zweimal); nach
  jedem meldet `--stats` 0 lebende Handles (Objekte, Arrays), 0 Strings (gezählt mit `FIRE_HEAP_STATS`), 0 Einträge im Temporär-Pool, 0 registrierte
  Klassen. Die Ausgabe ist dieselbe wie bei den Einzelläufen. Mit AddressSanitizer/UBSan (gcc, dazu Leak-Prüfung): keine Fehler, kein Leck.

## Was ohne Eingriff in die Runtime ging

Alles außer dem Ende des Prozesses: das Wertmodell (`Value`, Strings mit Zählung und Pool, Objekte mit Handles), Besitz (Scopes als `OwnList`-Paare,
Claims, Hoisting, Weitergabe bei `return`, Destruktoren in Batches), Arithmetik und Vergleiche samt ihrer Ausnahmen, die Formatierung von `print`,
Ausnahmen mit `resume` über den Handler-Stapel der Runtime, Klassen-Deskriptoren und Catch-Typen zur Laufzeit, die Ausnahmen, die die Runtime selbst
baut (die VM stellt `makeDivideError`, `makeClassError`, `makeIndexError`, ... bereit, wie der Generator sie erzeugt: Klasse nach Namen, Konstruktor
verschachtelt), Abbauen eines Programms bis auf null. Die Prüfungen der Deklarationen (`GuardLocal`/`GuardTop`) sind die Funktionen der Runtime
(`guardSlot`), mit derselben Meldung.

Dabei brauchte die VM **keine neuen Felder** in `Value`, im Objektkopf, in `FireClassDesc`, `Lam`, `OwnList` oder `Handler`: was sie mehr weiß, liegt in
ihren Seitentabellen (Klasse je Id, Inline-Caches je Codeposition, `VmHandlerRec` um den `Handler`, die Funktion des Lambdas als verstecktes Capture).

## Eingriffe hinter `FIRE_VM`

Genau einer, in `native/runtime/fire_rt.h`:

```c
#ifdef FIRE_VM
FIRE_NORETURN void fire_vmExit(int code);   /* (die VM: zurück zum Starter des Skripts) */
#endif
FIRE_NORETURN FIRE_FN void exitNow(int code) {
    ...
    fflush(stdout);
    fflush(stderr);
#ifdef FIRE_VM
    fire_vmExit(code);
#endif
    fire_plat_exit(code);
}
```

Nötig, weil `fatal` & Co. für erzeugten Code zu Recht den Prozess beenden (L6: "kein `exit`, kein `fatal`"). Alternativen ohne Eingriff gäbe es nicht:
`exitNow` ist `static inline` und wird überall direkt gerufen.

## Prüfungen für den nativen Build

| Prüfung | Ergebnis |
|---|---|
| Präprozessor | `gcc -E -P` eines erzeugten Programms mit dem alten und dem neuen `fire_rt.h`: **identisch** (387 844 Bytes), ebenso mit `FIRE_THREADS` + `FIRE_DEBUG` |
| `fire_program.c` | der Generator ist nicht berührt; mit dem forge vorher und nachher (`fire_rt.h` ist in `fire.Native.dll` eingebettet, also neu gebaut) erzeugt: **gleich** für 100 Programme (die 9 Benchmarks, die 8 Spike-Skripte, jeder 6. Fall aus `tests/cases/native`) |
| Programme | `list` und `string` nativ gebaut, vorher und nachher: **byte-gleiche** Executables |
| Größe, Geschwindigkeit | `tests/native-baseline.py measure` vorher/nachher, `compare`: alle Größen **+0** (Bytes und Code), Laufzeiten -26 % ... +8 % (Rauschen: auf der Maschine liefen andere Agenten parallel; die Executables sind byte-gleich), **"nothing got worse"** |

```
program        bytes              code            run ms
empty          44888       +0    38656      +0      0.80   -10.1%
alloc          80096       +0    72555      +0     11.86    -9.8%
array          98848       +0    89583      +0     81.09    +7.7%
fib            45496       +0    39176      +0      1.29    -8.5%
float          45624       +0    39328      +0     21.39    +1.3%
lambda         81824       +0    74355      +0     24.70   -24.9%
list          174568       +0   161601      +0     30.71   -25.7%
loop           71272       +0    64028      +0     25.58    -0.8%
method         53968       +0    47551      +0     12.31    +4.1%
string        109776       +0   100307      +0     16.27   -16.6%
```

## Vergleich mit der C#-VM

Ausgaben: alle 8 Skripte und die 6 Benchmarks aus `tests/bench`, die der Spike ausführen kann (`alloc`, `fib`, `float`, `lambda`, `loop`, `method`;
`array`, `list` und `string` brauchen Arrays bzw. String-Methoden), geben mit der C-VM dieselbe Ausgabe und denselben Exitcode wie `forge run`; bei
`04_unhandled` auch denselben Text auf stderr.

Geschwindigkeit: die Benchmarks mit 20-facher Schleifenzahl (`fib(30)`), bester von 5 (C) bzw. 3 (C#) Läufen; für C# ist die Zeit von `forge run`
abzüglich `forge run` eines leeren Programms (Start, Übersetzen des Preludes, JIT bleibt zum Teil drin). Zwei Messreihen auf einer Maschine mit 4 Kernen,
auf der parallel andere Arbeit lief:

| Programm | C#-VM | C-VM (gcc -O2) | C-VM (clang -O2) | C# / C |
|---|---|---|---|---|
| `loop` (30 M Durchläufe) | 2,90 - 2,98 s | 0,98 - 1,23 s | 1,12 - 1,20 s | 2,4 - 3,0 |
| `fib(30)` (1,6 M Aufrufe) | 0,43 - 0,78 s | 0,12 - 0,16 s | 0,14 - 0,17 s | 2,8 - 6,3 |
| `float` (12 M) | 1,85 - 2,01 s | 0,58 - 0,66 s | 0,69 - 0,76 s | 3,0 - 3,2 |
| `method` (5 M Methodenaufrufe) | 1,14 - 1,17 s | 0,37 - 0,40 s | 0,37 - 0,38 s | 2,9 - 3,2 |
| `lambda` (4 M Lambda-Aufrufe) | 0,91 - 0,99 s | 0,19 - 0,23 s | 0,24 - 0,26 s | 4,3 - 4,8 |
| `alloc` (1,2 M Objekte) | 0,84 - 0,91 s | 0,17 - 0,25 s | 0,19 - 0,23 s | 3,7 - 5,0 |
| Prozess für `print(1)` | 0,8 - 1,0 s (mit Übersetzen) | 0,002 s | | |

Die C-VM ist ohne jede Optimierung (ein `switch`, keine verschmolzenen Befehle außer denen des Compilers, Scopes je Block wie im Bytecode, Feld- und
Methodensuche über Inline-Caches) 2,4- bis 6-mal so schnell wie die C#-VM. Der Weg über die Runtime kostet also keine Größenordnung; die schnellen Pfade
der Runtime (Inline-Fälle für Ints) übernimmt der Interpreter in seine Opcodes, der Rest sind ohnehin Aufrufe. Das Risiko "Geschwindigkeit der VM auf
einer Runtime, deren schnelle Pfade für den C-Compiler gedacht sind" (Roadmap) ist damit kleiner als befürchtet; gemessen wird weiter in B7.

## Was nicht passt (und wie es in Phase B geht)

Nichts davon kippt L1; es sind die Stellen, an denen die VM - nach der Regel "wo das nicht reicht, verliert die VM" - einen eigenen Weg braucht.

1. **Rückrufe ohne Kontext in Tabellen der Runtime.** Der Generator erzeugt je Klasse, Typargument, Einheit, Methode eine eigene C-Funktion; die VM
   kann zur Laufzeit keine Funktionen erzeugen. Gelöst im Spike: `FireClassDesc.destroy/toString` (die Klasse steht im Objekt), `Lam.fn` (verstecktes
   Capture), `Handler.fn` (hat `ctx`). Offen, mit dem Weg dafür:
   * `TArg.holds/adapt`, `ArrClass.fits/zero` (Generics, typisierte Arrays von Klassen), `UnitConvFn` (Einheiten mit Umrechnungsfunktion),
     `MsgFn` (Aktoren), `ThreadsCatch.fn`, `ProbeRawSet/Get`, `RfClass.make`, `RfMember.attrs`, `FireClassDesc.recEq/recHash/recCheck` (Records - die
     haben den Wert, also die Klasse: wie `destroy`). Für die übrigen: die VM ruft die Funktionen der Runtime, die diese Tabellen lesen (`fitsTArg`,
     ...), nicht, sondern hat eigene Fassungen, die ihre Seitentabellen lesen - oder ein fester Vorrat an Trampolinen (`holds_0` ... `holds_N`, je mit
     seinem Index in eine Tabelle der VM). Ein Kontextfeld hinter `FIRE_VM` in `TArg`/`ArrClass` wäre erlaubt (keine der in L1 genannten Strukturen),
     ist aber nicht nötig.
2. **Die Konfiguration der Runtime steht beim Übersetzen fest.** `FIRE_NDIMS` (die VM nimmt 16, wie die Linkage "shared"), `FIRE_EXCEPTIONS`,
   `FIRE_THREADS`, `FIRE_REFLECTION`, und vor allem `#intwidth`/`#floatwidth` (`FIRE_INT32`, `FIRE_FLOAT32`: `Real` und `FireInt` sind Typen der
   Runtime). Ein VM-Programm hat eine Konfiguration: die Obermenge (Ausnahmen, Threads, Reflection an) für alle Programme, und für `#floatwidth 32` /
   `#intwidth 32|16` je eine weitere Variante der VM (oder die VM lehnt solche Programme ab). Für `forge build --engine vm` (Schritt 8) heißt das: die
   passende Variante anhängen.
3. **Der Zustand der Runtime ist global** (`g_classDescs`, die Einheitentabelle, die Handle-Tabelle, der Pool, der Handler-Stapel je Thread). Nacheinander
   laufende Programme sind kein Problem (gezeigt); ihre Klassen-Ids werden nach dem Entladen wiederverwendet. Zwei Programme **gleichzeitig** in einem
   Prozess (L6, "mehrere Programme nebeneinander") teilen sich die Tabellen: Registrieren hängt an (geht), Entladen in anderer Reihenfolge als
   umgekehrt nicht - dafür bekommt die VM eine eigene Verwaltung der Ids (Zeilen eines entladenen Programms bleiben als leere Zeilen stehen, freie Ids
   werden wiederverwendet). Die VM selbst hält keinen globalen Zustand außer dem Zeiger auf die laufende VM (`g_vmCur`, für die Rückrufe der Runtime;
   mit Threads je Thread).
4. **Einheiten** wurden nicht geladen. Die Runtime nimmt sie zur Laufzeit an (`unitAppend`, wie bei Plugins in `firePluginUnits`); die Einheiten der
   `.fbc` gehen denselben Weg. Ebenso die Enums (`g_enums`).
5. **Nach einem `fatal`** ist der Zustand der Runtime halb fertig (z. B. ein Zerstörungs-Batch). Der Starter setzt zurück, was er kennt; was in einem
   abgebrochenen Batch lag, ist dann ein Leck. Für Fehler der Runtime (Grenzen, interne Fehler) annehmbar.
6. **Destruktoren nach einer unbehandelten Ausnahme**: wie in der C#-VM und nativ laufen keine. Für das Einbetten (`fire_embed.h`) ist zu entscheiden,
   ob ein Wirt das will (Dateien schließen); die VM könnte es mit einem Schalter (ohne `quiet`) - die Runtime kann beides.
7. **Das Modell von `catch`** ist das der Runtime (catch auf der Wurfstelle, Abbau danach), nicht das der C#-VM (Frames einfrieren, beim Ende des catch
   verwerfen). In allen Testfällen ist das beobachtbare Verhalten gleich (auch die Reihenfolge der Destruktoren). `finally` kann die Runtime
   (`hasFinally`, finally-only-Handler); der Spike hat es nicht angeschlossen.

Nicht Teil des Spikes (unverändert Arbeit von Phase B): Arrays, Strings als Empfänger von Methoden (`$string`), Überladungen nach Typ, Properties,
Generics, Zeiger, Records, Threads/Tasks/Aktoren, die übrigen Natives, Paket-Natives, Debugger.

## Nachtrag: Phase B, Schritte 1 und 2

Der Spike ist in `native/vm` aufgegangen; Aufbau und Stand der VM beschreibt [VM_C.md](VM_C.md). Von den Punkten oben: **1** ist für Generics
(`TArg`: eigene Fassungen der VM), Records (generische Rückrufe), Einheiten mit Umrechnungsfunktionen (`UnitConvFn`: 16 Trampoline) und typisierte
Arrays einer Klasse (`ArrClass`: 64 Trampoline) gelöst, offen für Aktoren, `catch threads`, Probes und Reflection; **4** ist gelöst (Einheiten, eigene
Einheiten und Enums der Datei werden beim Start die Tabellen der Runtime); **7** ist um `finally` ergänzt (über den Operandenstapel wie die C#-VM, auf
dem Handler-Stapel der Runtime); **2** bleibt (`#intwidth`/`#floatwidth` brauchen eine Variante der VM). Die Runtime ist weiterhin nur um den Eingriff
in `exitNow` geändert.

## Empfehlung

**L1 hält.** Die VM wird ein Interpreter auf `fire_rt.h`: derselbe Code für Werte, Formatierung, Besitz und Ausnahmen, und der native Build bleibt
byte-gleich. Für die nächsten Schritte:

* den Eingriff `FIRE_VM`/`fire_vmExit` behalten (B1);
* den Lader aus dem Spike übernehmen (er liest schon das ganze Format) und um Einheiten/Enums ergänzen (B1);
* für die Rückrufe ohne Kontext (oben 1) früh entscheiden, welche die VM selbst implementiert - vor allem Generics (`TArg`) und typisierte Arrays (B2);
* die Varianten der VM nach `#floatwidth`/`#intwidth` in den Bau aufnehmen (B7/B8);
* die Verwaltung der Klassen-Ids für mehrere gleichzeitige Programme mit dem Entwurf des Einbettens (L6) festlegen.
