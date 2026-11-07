# Natives Backend (AOT): fire -> C

Status: **experimentell**, Branch `aot`. Dieses Dokument beschreibt Ziel, Architektur, den aktuellen Stand und die Messwerte.

## Ziel

fire-Programme sollen sich auf Wunsch zu **richtig schnellen, eigenständigen Binaries** übersetzen lassen - auf dem Desktop
und auf einem ESP32 (FreeRTOS), wo die Sprache nur dann sinnvoll ist, wenn die Leistung stimmt.

## Entscheidung: den Code direkt als C einbetten

Der Bytecode wird **nicht** als Datenblock neben einem Interpreter ausgeliefert, sondern in **C-Anweisungen übersetzt**, die der
C-Compiler zusammen mit der Runtime sieht und optimiert. (Bis zum Branch `native-c` war es C++; der erzeugte Code hat nichts von C++ gebraucht,
C läuft mit jedem Compiler, auch denen der Mikrocontroller, und ist die Grenze, an der später getrennt übersetzte Bibliotheken zusammenkommen.) Dafür sprechen:

* Der C-Compiler erledigt die Typinferenz mit: `var i = 0` ist ein `Int(0)`, die Art des Werts steht zur Übersetzungszeit fest,
  und der generische `Value`-Pfad (Art prüfen, Einheit prüfen, rechnen) faltet sich zu nacktem `int64`-Code zusammen.
  Das haben wir gemessen (siehe unten): schon die *generische* Übersetzung ist mehr als 20x schneller als die VM.
* Kein Programmformat, kein Lader, kein Dispatch. Auf dem ESP32 liegt der Code als normaler Code im Flash (Firmware), nicht als
  Daten, die ein Interpreter erst lesen müsste.
* Jede fire-Funktion (statische Methode, Methode, Konstruktor, Lambda) wird eine C-Funktion; kleine werden vom
  Compiler eingebettet, der Rest bleibt ein Aufruf - genau so, wie man es erwartet.

Ein portables Binärformat braucht nur noch der **Interpreter** - und den braucht man nur, wenn Skripte auf dem Gerät *zur Laufzeit*
nachgeladen werden sollen (ohne neu zu flashen). Das ist ein eigener, späterer Schritt; die
AOT-Übersetzung hängt nicht davon ab.

## Architektur

```
Skript -> Lexer -> Parser -> Resolver -> Compiler -> Bytecode (LinkedProgram)
                                                        |
                          +-----------------------------+------------------------+
                          v                                                      v
                  C#-VM (Editor, Debugger)                       fire.Native (CppGenerator)
                                                                                 |
                                                      fire_program.c (C11) + native/runtime/fire_rt.h
                                                      [fire_natives.cpp (C++) + fire_rt_cpp.hpp: Natives in C++]
                                                                                 |
                                                                  gcc/clang/MSVC/xtensa-gcc -> Binary
```

* `src/fire.Native` - der Generator (`CppGenerator`), liest `LinkedProgram` und schreibt das Programm als C (`fire_program.c`). Die Runtime
  (`fire_rt.h`) steckt als Ressource in der DLL und wird daneben geschrieben.
* `native/runtime/fire_rt.h` - die Runtime in C11 (übersetzt auch als C++): `Value` (16 Byte), Arithmetik/Vergleiche mit schnellem Pfad und kaltem
  Fehlerpfad, Ausgabeformat identisch zu `Value.ToString()`. Ihr globaler Zustand liegt in der Einheit, die `FIRE_RT_IMPLEMENTATION` definiert (das Programm,
  oder die Bibliothek eines Packages für die VM).
* **Natives in C++** (die Natives der Packages, die noch C++ sind; die Bridges von `graphics` und `windows` sind C und Teil von `fire_program.c`): eine eigene Datei `fire_natives.cpp`, die
  `native/runtime/fire_rt_cpp.hpp` einbindet - `fire_rt.h` als C++ (Funktionen in `namespace fire`, mit C-Linkage, also *eine* Runtime für beide Dateien), die
  C++-Plattformschicht (`fire::plat`, `FIRE_PLATFORM_CPP_HEADER`) und die paar Überladungen mit Referenzen, mit denen die Natives geschrieben sind. Das Programm ruft
  jeden Native über eine C-Funktion `fire_nat_N` auf, die der Generator am Ende von `fire_natives.cpp` schreibt (und im Programm deklariert). Ein Programm ohne
  solche Natives (auch eines mit JSON) ist reines C.
* **JSON** (`#import "json"`): `native/runtime/fire_json.h` - die Engine in C (Schreiben, Parser, Lesen; dieselben Texte und Fehler wie `JsonMethods` der VM; was sie
  für sich braucht, kommt aus einer Arena des Aufrufs) und die Tabellen der Klassen des Programms, die der Generator dahinter schreibt (`kJsonClasses`, `jsonClassAt`, ...).
  Schreiben zieht keine Klassen nach; Lesen die der deklarierten Typen (mit Unterklassen und Feldtypen), ein Slot ohne Klasse alle; nicht serialisierbare Klassen nie ([JSON](../stdlib/json.md)).
* **Bauen** (`NativeBuilder.BuildCommands`): ohne Natives in C ein Aufruf des C-Compilers (`gcc -std=c11 ... -lm`); mit Natives wird das Programm zur Objektdatei
  übersetzt und vom C++-Compiler zusammen mit `fire_natives.cpp` gelinkt. Der C-Compiler gehört zum C++-Compiler der Toolchain (`g++` -> `gcc`, `clang++` -> `clang`,
  `em++` -> `emcc`, `x86_64-w64-mingw32-g++` -> `...-gcc`; MSVC: `cl /TC`), oder `toolchain.cCompiler`/`cStd`. Ein ESP-IDF-Komponent listet beide Dateien.
* `fire.Compiler native <dateien> [-o out.c]` - Befehlszeile (schreibt mit Natives auch `fire_natives.cpp`).

### C statt C++: was der Generator anders macht

* **Catch-Blöcke** waren C++-Lambdas (`[&]`), die über der Wurfstelle laufen (damit `resume` dort weitermacht). In C ist jeder `try` mit Catch-Blöcken eine eigene
  Funktion `C_<funktion>_<n>(void* ctx, uint32_t ci, Value e)`; die Funktion, zu der er gehört, hält die Variablen, die ihre Catch-Blöcke benutzen, in einem
  **Frame** (`struct FR_<funktion>`: die Handler, die Stackplätze der Catch-Blöcke und von Parametern, Stackplätzen, Locals und Besitzlisten nur die, die im Code der
  Catch-Funktionen vorkommen), `Handler.ctx` zeigt darauf. Alles andere bleibt eine gewöhnliche Variable, die der C-Compiler in einem Register halten kann. Der Code behält seine Namen: ein Makro je
  Variable macht aus `s0` ein `(FR->s0)`, in der Funktion und in ihren Catch-Funktionen (danach `#undef`). Funktionen ohne Catch-Block bleiben, wie sie waren.
* **Aufruftiefe** (`try x.Take...`): statt eines RAII-Zählers ein Wrapper, der zählt und `<funktion>_b` aufruft (die Trampoline für Spezialisierungen rufen `_b` direkt).
* **Statische Initialisierung** (Einheitentabelle, Ressourcen, Hooks) läuft in `fire_init()` vor `fire_main()`; Globals beginnen mit `FIRE_UNDEF_INIT`.
* Variadische Templates und `std::initializer_list` sind Compound Literals: `ownValueN(o, v, (OwnList* const[]){&OP, &O3}, 2)`, `internTArgsN(...)`. Die Tests
  generischer Parameter bei der Überladungswahl sind kleine Funktionen `ovg_N(value, owner)` statt sofort aufgerufener Lambdas.
* Kein `namespace`, kein `nullptr`, kein `std::`, keine Referenzen (`timeObjTicks(v, &ticks)`), `fire_char16` statt `char16_t`, `objFields(o)` statt `o->fields()`.

### Wie der Bytecode übersetzt wird

* **Der Operandenstack verschwindet.** Seine Tiefe ist an jeder Instruktion bekannt (der Generator verfolgt sie über alle Wege und
  verlangt, dass sie sich an Zusammenflüssen deckt). Stackplatz `k` ist die C++-Variable `s{k}`; der Compiler macht daraus Register.
* **Scopes verschwinden, solange sie nur Werte halten.** Die Scope-Kette ist statisch bekannt. Eine lokale Variable ist eine
  C++-Variable (`B{scope}_{slot}`, Parameter `P{slot}`, Globale `G{slot}`).
* **Sprünge werden `goto`**, jede Funktion eine C++-Funktion.
* **Der Generator lehnt ab, was er nicht kann** (`NativeNotSupportedException` mit dem Namen des Opcodes), er übersetzt nie
  mit anderer Semantik.

### Wie Objekte übersetzt werden

* **Klasse = Klassen-Id + Feldlayout** (Basisfelder zuerst, wie in der VM). Ein Objekt ist ein `fire::Obj` (Kopf mit Klassen-Id, Besitzer,
  Geschwisterliste und der Liste der eigenen Objekte) mit den Feldern als `Value` dahinter. Der Zugriff läuft über kleine erzeugte
  Hilfsfunktionen `gf_<feld>`/`sf_<feld>`: haben alle Klassen das Feld am selben Index (der Normalfall), ist es ein fester Index, sonst
  entscheidet die Klassen-Id.
* **Methodenaufruf** über eine erzeugte Weiche je (Name, Argumentzahl): die Klassen des Programms sind bekannt (geschlossene Welt), die
  Weiche wählt nach Klassen-Id; gibt es nur eine Implementierung, wird sie direkt aufgerufen und der Compiler bettet sie ein.
* **Ownership**: Jeder Scope, der Objekte besitzen kann (er erzeugt welche oder ruft etwas auf, das eines zurückgibt), bekommt eine lokale
  `OwnList`. `new` hängt das Objekt in die Liste des innersten Scopes, `field = new X()` in die Liste des Objekts. Beim Verlassen des
  Scopes und bei `return` werden die Listen von innen nach außen zerstört (Reihenfolge der Erzeugung, erst `destruct()`, dann die
  eigenen Objekte); ein zurückgegebenes Objekt, das dem verlassenen Scope gehörte, wandert zum Aufrufer (`transferOut`/`adopt`). Scopes ohne
  Objekte kosten nichts.
* Objekte werden beim Zerstören **freigegeben** - aber erst am Ende des **Zerstörungsstapels** (`DestroyBatch`: das Verlassen eines Scopes, ein `delete`, das Ende des Programms):
  alle Destruktoren eines Scopes sehen die Objekte des Scopes also noch (die Reihenfolge ist die der Erzeugung: der Destruktor eines Writers leert einen Stream, der schon
  zerstört ist). Danach ist das Objekt **tot** (SPEC 2.3): sein Handle läuft ab, jede Benutzung wirft eine `DestroyedException` (ohne Ausnahmen im Programm: Programmende mit Meldung).
  Mit `-DFIRE_KEEP_DESTROYED` bleibt der Speicher stehen (Fehlersuche). Die Tests laufen unter AddressSanitizer/UBSan.

#### Tote Objekte, `return` und `Takes`

* **Handles.** Der `Value` eines Objekts trägt wie der eines Arrays den Platz seines Kopfs in einer Tabelle von Generationen (`unit` = Platz, `reserved` = Generation, `ObjV`). Am Ende des
  Stapels bekommt der Platz eine neue Generation (`slotRelease`), bevor der Speicher frei wird. Die erzeugten Zugriffe (`gf_`/`sf_`/`fp_`, der Methoden-Verteiler) prüfen das Handle
  (`leafAlive`: eine Ladung und ein Vergleich) und melden sonst `destroyedError`; das Programm prüft danach das Unwinding, wenn es Ausnahmen benutzt. Mit `#performance` (`FIRE_UNCHECKED`)
  gibt es keine Tabelle und keine Prüfung. Objekte werden **nicht** gezählt - der Besitzer entscheidet, das Handle erkennt, was nicht mehr da ist.
* **`return` nimmt den Baum mit** (`transferTree`): gehört der zurückgegebene Wert einem der Scopes, die verlassen werden (auch über Objekte hinweg, die gleich mit ihnen sterben), wird er ausgehängt
  (er reist, `adopt` des Aufrufers übernimmt ihn) und `moveReachable` geht durch alles, was an ihm hängt: was ebenfalls diesen Scopes gehört, wandert zu dem Objekt, das darauf zeigt
  (`carrierOf`: das Objekt; bei einem Array der Owner des Arrays, wenn der ein Objekt ist, sonst das Array selbst über seine Teileliste `parts`). Jeder Knoten wird einmal besucht (Flag `F_VISIT`),
  was unter sich selbst landen würde, bleibt. Der Generator übergibt die Listen der verlassenen Scopes als Feld (`T_`).
* **`Takes`** (`TakeLocal`, `TakeUpwards`, `TakeGlobal`, `TakeTo` mit einem `Takes`-Wert als letztem Argument): `ownMethodT` setzt erst den Owner wie bisher, dann `moveReachable` mit dem Modus
  (`TK_THIS` nichts weiter, `TK_CHILDREN` die unmittelbaren Mitglieder, `TK_LOCALS` wie `return` mit den Listen der offenen Scopes der Funktion, `TK_ALL` alles Erreichbare). Über den Methoden-Verteiler
  (eine Klasse deklariert selbst ein `Take...`) legt die Aufrufstelle die Scopes des Aufrufers vorher in `g_scopeLists`/`g_scopeCount`; der eingebaute Rückfall im Verteiler liest sie, `Takes.Locals` findet also dasselbe wie ohne Verteiler.
* **`Takes.Children` und `IEnumerable`**: nimmt ein Objekt, das `IEnumerable` implementiert, seine Items über den Enumerator mit, erzeugt der Generator `fire_enumerateItems` (GetEnumerator, MoveNext, GetCurrent über die Verteiler)
  und setzt `g_enumerateItems`; `moveReachable` benutzt sie statt der Felder. Ein Array nimmt seine Elemente mit.
* **`take x`** (SPEC 2.2): als Argument `bits == 5` im `CopyArgs`-Präfix: `takeArg` (nimmt den Wert aus seiner Liste und hängt ihn in die Argumentliste `AL` des Aufrufs, danach wie `f(g())` `finishArgs`); der Opcode `TakeCheck` ist nativ nur ein Marker
  (`takeAlive` im Aufruf wirft die DestroyedException). Zuweisungen: `TakeToScope` (`takeToList` auf die Liste des Scopes der Variablen, `g_globalOwn` für Globale), `TakeToObject`/`TakeToArray` (`takeToHolder`: Objekt über `takeToObject`, Array über seine Teileliste).
  Ein Besitzer zerstört zuerst seine Objekte (in Erzeugungsreihenfolge), dann seine Arrays und Puffer - wie die VM (`destroyList`).
* **`try x.Take...(...)`** (`ownMethodTry`): der Compiler macht daraus einen Aufruf von `tryTake...`; verschoben wird nur, wenn das Ding einem Scope des laufenden Aufrufs (die Listen der offenen Scopes), dem
  aktuellen Objekt (`self`) oder dem Argument-Besitz des eigenen Aufrufs gehört: das Ergebnis eines weitergereichten Aufrufs `f(g())` und `f(take x)` reisen in der Argumentliste `AL` des Aufrufs (`AL_MARK`). Damit der
  Aufgerufene seine eigene Liste erkennt, zählt jede Funktion die Aufruftiefe (`CallDepth`, `g_callDepth`; nur wenn das Programm ein `try x.Take...` enthält) und die Liste merkt sich die Tiefe des Aufrufers (`OwnList::depth`):
  eigen ist nur eine Liste mit `depth + 1 == g_callDepth` - wie in der VM, wo das Argument nur dem Scope des Aufrufs gehört, nicht einem tieferen Aufgerufenen. Das Ergebnis ist `Bool`.
* Ein Array kann Objekte (und Arrays) besitzen, die `return`/`Takes` mitnehmen - nativ über seine Teileliste (`parts`), in der VM als `IOwner` (`ScriptArray`): sie sterben mit dem Array, auch wenn es weitergegeben wird.

### Speicher: Besitz für Objekte, Arrays und Puffer, Zähler für Strings und Lambdas

* **Objekte, Arrays und Puffer haben genau einen Besitzer** (SPEC 2, 2.5) und werden mit ihm zerstört: sie hängen in derselben `OwnList` (gemeinsamer
  Kopf `fire::Owned`: Vorgänger, Nachfolger, Besitzer, Art). `new int[n]`, Literale und `new byte[n]` kommen in die Liste des innersten Scopes, direkt einem
  Feld zugewiesen (`OwnValue`) in die des Objekts; `return` gibt sie an den Aufrufer weiter (`transferOut`/`adopt`), `x.TakeLocal()`, `TakeUpwards`, `TakeGlobal`,
  `TakeTo(obj)` und `delete x` sind kleine Laufzeitfunktionen (`ownMethod`, `deleteValue`). Die inneren Arrays von `new int[3][4]` und `[[1, 2], [3]]` hängen in
  der Teileliste (`parts`) des äußeren. Es gibt **keine Zähler** für diese Werte.
* **Zuweisung nach oben, Aufrufergebnisse** (SPEC 2.1): `HoistValue` wird zu `hoistFrom(v, &innerer_Scope, &Funktions_Scope)` für jeden inneren Scope der Funktion
  (nur was dem inneren Scope gehört, wandert). `OwnValue` ist bedingt: `ownValue(objekt, v, &Scope1, &Scope2, ...)` gibt den Wert nur dem Objekt, wenn er niemandem oder
  einem Scope dieser Funktion gehört. `f(g())`: der Rückgabewert von `g` reist in der Argumentliste `AL` des Aufrufs (`reownArg`), nach dem Aufruf zerstört `finishArgs`
  was die aufgerufene Funktion nicht behalten hat. Die VM verhält sich genauso: der Wert gehört dem Aufruf (`Scope.AddArgument`) und stirbt als letztes, nach allem, was die aufgerufene Funktion selbst angelegt hat.
* **Handle-Tabelle gegen hängende Referenzen.** Ein zerstörtes Array darf nicht mehr benutzt werden (SPEC 2.5). In den geprüften Modi trägt der `Value` eines
  Arrays/Puffers den Platz seines Kopfs in einer Tabelle von Generationen (`unit` = Platz, `reserved` = Generation); beim Zerstören bekommt der Platz eine neue
  Generation, jeder Zugriff vergleicht (`leafAlive`: eine Ladung und ein Vergleich) und meldet sonst die fangbare `DestroyedException`, bevor freigegebener
  Speicher berührt wird. Mit `#performance` definiert der Generator `FIRE_UNCHECKED`: keine Tabelle, keine Prüfung (undefiniert, wie in der VM-Doku).
  **Objekte** haben dasselbe Handle (siehe oben).
* **Typisierte Arrays werden gepackt** (SPEC 8.4.2): ein Array in einer Deklaration mit Elementtyp (`int a[]`, `int[8] a[]`, `float[32] a[]`, `bool`,
  `char`; eine `byte`-Deklaration hält dagegen einen Puffer: `OpCode.TypeArray` mit `byte` wird `typeBuffer(...)`, das Array wird durch einen neuen Puffer
  mit seinen Elementen ersetzt - `bufFromArray`, in der VM `BufferFromArrayValue`; `byte a[n]` ist gleich `newBuffer`) bekommt einen `ArrTyped` (`Arr::typed`, sonst null): Elementtyp (`E_I8` ..., dieselben Nummern wie `Values.ArrayElem` der VM), Einheit
  und die Daten - eine C-Zahl pro Element statt eines `Value`. `undefined` ist ein Merker (ein selten benutzter Wert des Typs: `INT8_MIN`, ein NaN mit
  festem Payload, `0xFF`, `0xFFFF`, bei `bool` die 2); nur wenn er als echte Zahl gespeichert wird, setzt das ein Bit in `real` (dann angelegt). Gelesen wird
  die Maske also nur, wenn der Merker gefunden wird (`arrPackedGet`). `OpCode.TypeArray` wird `typeArray(...)` (wandelt die Elemente um und packt);
  steht es direkt hinter `NewArray`, entsteht das Array gleich gepackt in einem Block (`newTypedArray`). `arraySet` wandelt um (Abschneiden, Runden,
  Einheit, sonst die Ausnahme) und liefert den gespeicherten Wert, der auch das Ergebnis der Zuweisung ist. Wer die `Value`s braucht (`arrItems`: die
  Natives der Packages, JSON, `ref a[i]`), entpackt das Array einmal (`arrUnpack`); der Typ bleibt und wird weiter durchgesetzt (auch beim Schreiben
  über einen Zeiger, dafür kennen Zeiger in Arrays ihr Array über die Slot-Tabelle, `FIRE_ELEMPTRS`). `copy`, `taking` und `sync` kopieren die Zahlen
  (`arrCopyTyped`).
* **Strings und Lambdas werden gezählt** (sie werden frei weitergegeben: `var t = s`, in Feldern und Arrays gespeichert, zurückgegeben):
  * **Speicherorte halten einen Zähler**: Variablen, Parameter, Felder, Array-Elemente und statische Felder (`retain` beim Speichern, `release` beim
    Überschreiben, beim Verlassen des Scopes und beim Zerstören des Objekts/Arrays). Stack-Zwischenwerte halten nichts.
  * **Frische Werte** (Verkettung, Methoden von `string`, ...) kommen mit Zähler 1 in den **temporären Pool**; jeder Scope merkt sich dessen
    Höhe beim Eintritt und gibt beim Verlassen alles darüber frei. Ein Zwischenwert lebt also bis zum Ende seines Scopes.
  * **`return`** hält den Rückgabewert mit einem Zähler fest, der Aufrufer übernimmt ihn in seinen Pool (`adopt`).
  * **Konstanten** liegen statisch und sind unsterblich (nie gezählt).
  * Der Pool ist ein einfaches Array statt einer verketteten Liste: derselbe Wert darf mehrfach darin stehen (ein Fehler dieser Art wurde
    durch AddressSanitizer gefunden).
* **"Kann eine Referenz sein"** leitet der Generator für jede Variable und jeden Stack-Platz ab (Konstanten, Zahlenrechnung und Vergleiche sind
  es nie; Parameter, Aufrufergebnisse, Feld- und Array-Lesungen schon). Nur dann entstehen `retain`/`release` (für Strings und Lambdas) und das stringfähige
  `addR` statt `add`. Reine Zahlenschleifen sind deshalb so schnell wie zuvor.
* `TakeUpwards` braucht den Elternscope: nutzt ein Programm die Methode, bekommt jeder Scope eine Liste und kennt seinen Eltern (`OwnList::parent`; ein
  Funktions-Scope hat den globalen Scope als Eltern, wie in der VM).

Bekannte Abweichungen zur VM: Unicode-Klassifizierung und Groß-/Kleinschreibung nur für Basic Latin, Latin-1, Griechisch und Kyrillisch
(`char.IsDigit` nur ASCII-Ziffern). Zahlenformate: `X`, `D`, `B`, `F`, `E` wie in der VM (jedes andere ist dort ein Laufzeitfehler, nativ ein Fehler beim Erzeugen).

### Breite von `int` und Größe eines Werts (`#intwidth 32`, SPEC 8.2.2)

* `FIRE_INT32` (der Generator schreibt es bei `#intwidth 32`): `Value::i` ist ein `int32_t` (`FireInt`). Die Operationen bleiben, wie sie
  sind - sie rechnen mit den auf 64 Bit erweiterten Operanden, und das Ergebnis wird beim Speichern in den `int32_t` abgeschnitten. Genau so
  schneidet die VM jeden Int ab, den sie erzeugt (`Value.Ints32`); auch die Sonderfälle (`INT_MIN / -1`, Schieben um 32 und mehr) geben in
  beiden dasselbe.
* `FIRE_COMPACT` (von selbst, wenn `FIRE_INT32`, `FIRE_FLOAT32` und 32-Bit-Zeiger zusammenkommen; `FIRE_WIDE_VALUE` schaltet es ab): der
  Kopf eines Werts sind 32 Bit als Bitfelder - `kind` 4, `reserved` 12 (Enum), `unit` 16 (Einheit, Versatz eines Zeigers auf eine Variable:
  `FIRE_UNIT_OFFSET` liest ihn mit Vorzeichen) - dazu 32 Bit Nutzlast: 8 statt 16 Byte. Mehr als 65535 Einheiten beenden das Programm mit
  einer Meldung. Geprüft wird es auf dem Rechner als 32-Bit-Build (`gcc -m32`, gcc-multilib).
* Objekte, Arrays und Puffer im kompakten Format (`FIRE_SLOTREF`: kompakt und geprüft): die Nutzlast ist der **Slot** (32 Bit), die 28 Bit
  des Kopfs hinter `kind` sind die **Generation** (`FIRE_VGEN`/`FIRE_SETGEN`, `FIRE_GEN_MASK`); die Adresse steht in der Slot-Tabelle neben
  der Generation (`SlotEntry {hdr, gen}`: eine Ladung für `arrOf`/`objAt` und die Prüfung). Keine Grenze bei den lebenden Objekten (Slot 0
  bleibt frei). Zeiger in Arrays und Puffer (`ref a[i]`, `p + n`) zeigen auf einen **Kasten** `PtrBox {rc/type, Adresse, Slot}` (16 Byte pro
  lebendem Zeiger, die Generation im Kopf des Werts), gezählt wie Strings (`isRef`) und über eine Freiliste pro Thread wiederverwendet
  (`boxNew`/`boxFree`, höchstens 64 frei). `==` vergleicht Slot und Lebenszeit (eine Generation zwischen `born` und heute: der alte Verweis
  vor einem `fire handover` ist dasselbe Objekt). Mit `#performance` (`FIRE_UNCHECKED`) trägt jeder Wert seine Adresse direkt; nimmt das
  Programm Zeiger in Arrays (`FIRE_ELEMPTRS`), bleibt die Slot-Tabelle für sie (der Slot steht dann im Kopf des Zeigers).
  Die Runtime liest Handles nur über `leafOf`, `leafSet`, `sameLeaf`, `leafKey`, `ptrAddr`, `ptrSlot`, `leafPtrV` - nie über `.p`.

### Regeln für die Runtime (gelernt)

* **Alle Funktionen nehmen `Value` per Wert**, nie per Referenz, und die Fehlerpfade nur die Operandenarten. Eine Referenz auf eine
  Variable, die an einen nicht eingebetteten Aufruf geht, lässt die Variable "entkommen": der Compiler hält sie dann - und weil der
  Generator die Stack-Variablen wiederverwendet, *alle* - im Speicher statt in Registern. Das allein kostete Faktor 5-10.
* Kein C++-`throw`, kein RTTI (ESP32-Toolchains schalten beides oft ab). Ausnahmen der Sprache laufen über einen eigenen Mechanismus
  (siehe "Ausnahmen"); Fehler der Runtime, die die VM nicht als Ausnahme meldet, beenden das Programm mit Exitcode 1.
* Reines C++17, `std::to_chars` für die Zahlenausgabe (kürzeste Darstellung wie .NET).

## Ausnahmen

`throw` rollt den Stack nicht selbst ab. Der Handler-Stack (`g_handlers`, eine verkettete Liste von `Handler`-Objekten in den Frames der
Funktionen mit `try`) wird von oben durchsucht:

* **Passender `catch`**: sein Block läuft *auf* dem Stack der Wurfstelle (ein Aufruf in die Funktion, die den `try` besitzt). Die Frames der
  Wurfstelle leben also noch, und `e.resume(wert)` kann dort weitermachen: das `throw` liefert `wert`. Dafür ist jeder `try` mit `catch` ein
  C++-Lambda (`C{n}`), das die Variablen der Funktion per Referenz sieht. Seine eigenen Operanden-Slots heißen `c{n}_{k}`, damit sie die der
  Wurfstelle nicht überschreiben.
* **Kein passender `catch`** (oder nur ein `finally`): die Frames oberhalb werden über ein Statusflag abgerollt (`g_unwind`): jede erzeugte
  Funktion prüft es nach einem Aufruf, der werfen kann (`if (g_unwind.active) goto U..`), verlässt ihre Scopes (Destruktoren laufen) und kehrt zurück.
  In der Funktion des `try` führt der Weg zur **Landestelle** (`LAND{n}`), die das `finally` ausführt und weiterwirft.
* **Ein `catch`-Block endet** mit `exitJump` (Sprung hinter den `try`, `break`/`continue` aus dem Block, Sprung ins `finally`) oder `exitReturn`
  (`return` im `catch`): das Lambda kehrt zurück, die Frames der Wurfstelle rollen sich ab (erst jetzt laufen ihre Destruktoren, wie in der VM
  nach `ClearPendingResume`), und die Landestelle der Funktion macht dort weiter. Operanden, die der Sprung mitnimmt (der Abschluss eines `finally`),
  reisen in `g_unwind.regs`.
* **`finally`**: ein Block pro `try`, mit dem Abschluss (Nutzlast, Art) in zwei Operanden-Slots wie im Bytecode (`EnterFinallyNormal`, `PushJump`,
  `EndFinally`). `return` durch ein `finally` läuft statisch: der Generator kennt die offenen Handler an der Stelle (`Flow.Handlers`) und springt
  ins `finally`; `EndFinally` setzt die Rückkehr fort.

Der Generator fügt die Prüfungen nur ein, wenn das Programm überhaupt `throw` oder `try` enthält (`FIRE_EXCEPTIONS`); sonst ändert sich der
erzeugte Code nicht. Typisiertes `catch` ordnet die Klasse über eine erzeugte Tabelle zu (`excMatches`: Klasse, Basisklassen, Interfaces).

Bekannte Abweichungen zur VM:

* `resume` geht nur an die Wurfstelle selbst. Läuft die Ausnahme an einem `try` ohne passenden `catch` vorbei (oder durch ein `finally`), ist die
  Wurfstelle schon abgerollt; `resume` meldet dann einen Fehler (die VM setzt dort an einer verschobenen Stelle fort).
* Ein Objekt, das erst im `catch` mit `new` entsteht, kann nicht an `resume` übergeben werden (es gehört dem `catch`-Scope, der endet).
* `resume` nach einer `UnitMismatchException` setzt hinter dem `CheckUnit` fort; die VM schiebt dort einen zusätzlichen Wert auf den Stack.
* Eine Ausnahme, die ein Destruktor wirft, wird beim Abrollen ignoriert.

## Zeilen des Programms im C (`#line`)

Jede Instruktion steht im erzeugten C hinter einer `#line`-Direktive mit ihrer fire-Zeile (`#line 12 "/pfad/main.fire" // 41: Add`; eine Quelle ohne Datei heißt
`fire:source/N`, der Prelude und die Preludes der Imports `fire:prelude/N`). Nach jeder Funktion nennt sich die C-Datei wieder selbst (`#line <eigene Zeile>
"fire_program.c"`), damit die Hilfsfunktionen, Tabellen und Wrapper ihre echten Zeilen behalten. So zeigen gdb/lldb, die Meldungen des C-Compilers und
Absturzberichte fire-Zeilen: `gdb -ex "break main.fire:7"` hält an der Zeile, `bt` zeigt die Aufrufer mit ihren fire-Zeilen. Das gilt in jedem Modus;
`"lineDirectives": false` im Ziel von `fire.native.json` schaltet es ab (das C liest sich dann als C).

## Debug-Builds

Im Modus `debug` (`#debug`, `-m DEBUG`) wird das Programm für den Debugger von spark gebaut (ROADMAP §8, Protokoll: [NATIVE_DEBUG_PROTOCOL.md](NATIVE_DEBUG_PROTOCOL.md)):

* **Übersetzt** mit `-O0 -g` (msvc `/Od /Zi`) und nicht gestrippt; Release und Performance sind nicht über spark debugbar. `NativeBuilder` erkennt den Debug-Build an
  `#define FIRE_DEBUG 1` am Anfang des Programms.
* **Schatten-Stack**: jede fire-Funktion hat einen Wrapper (`fN` ruft `fN_b`), der einen Frame (`DbgFrame`, `fire_debug.h`) auf den Stack des Threads legt und
  beim Verlassen wieder abnimmt. Der Frame kennt die Funktion, die Adressen ihrer Variablen (`dv_[]`, auch die der Catch-Blöcke: eine Funktion mit `try`
  hält im Debug-Build alle Variablen im Frame-Struct), ihr `this` und die Stelle (Quelle, Zeile, Scope-Zustand).
* **Haken** an denselben Stellen, an denen die Einzelschritt-Schleifen der VM eine neue Position sehen - am Anfang jedes Bereichs der Zeilentabelle und an jedem
  Sprungziel: `FIRE_DBG_L(quelle, zeile, zustand)` schreibt die Stelle in den Frame und ruft, nur wenn ein Debugger verbunden ist (`g_dbgArmed`), `fire_dbgLine`.
  Ein Wrapper prüft beim Zurückkehren die Stelle des Aufrufers noch einmal (die VM sieht nach `Return` eine neue Position: ein Haltepunkt auf der Zeile des
  Aufrufs trifft wieder), ebenso das Weitermachen an der Wurfstelle nach `resume`. Ohne Debugger kostet ein Haken einen nicht genommenen Sprung. Gemessen
  (`Fib(30)` und eine Schleife über 3 Mio.): Release `-O2` 0,035 s, Release mit `-O0` 0,33 s, der Debug-Build ohne Debugger 0,38 s, mit verbundenem
  Debugger und einem Haltepunkt 0,6 s.
* **Was die VM verschachtelt ausführt**, ist für Haltepunkte und Schritte unsichtbar (wie in der VM, die es innerhalb einer Instruktion laufen lässt):
  Getter und Setter von Properties, Destruktoren, Operatoren, `GetIndex`/`SetIndex`, Standardwerte von Parametern, Umrechnungen von Einheiten (diese
  Funktionen sind "hidden"), `ToString()` bei der Umwandlung in Text, die Ausnahmen der Runtime, der Enumerator eines Arrays und Lambdas, die die Runtime
  oder ein Native aufruft (`g_dbgNest`).
* **Catch-Blöcke** laufen auf dem Stack der Wurfstelle; solange sie laufen, ist der Frame ihrer Funktion oben (die VM hat die Frames der Wurfstelle
  verlassen), danach wieder die Wurfstelle (für `resume`).
* **Statische Tabellen** je Funktion: die Ebenen ihrer Scope-Zustände mit den Namen der Variablen (wie `VM.DebugScopeChain`: der Name aus dem `DeclareLocal`,
  Parameter aus `DebugLocalNames`, sonst `(local N)`), dazu die Globals (`g_dbgGlobalCount` zählt die bisher angelegten), die Felder jeder Klasse, die
  Quellen mit Datei und SHA-256 (`LinkedProgram.SourceHashes`) und ein Setter je Feld für `set` (`fire_dbgSetField`).
* Ein Debug-Build hat immer Ausnahmen (`FIRE_EXCEPTIONS`): jeder Laufzeitfehler ist eine Ausnahme, an der der Debugger anhalten kann. Eine Ausnahme, die kein
  `catch` des Threads nehmen würde (`wouldBeCaught`), hält vor dem Abrollen an; `resume` lässt den `throw` `undefined` ergeben.
* **Der Agent** (`fire_debug.h`, `FIRE_DEBUG_AGENT`: posix, windows, freertos, esp32) ist ein eigener Thread (auf FreeRTOS eine Task mit
  `FIRE_DEBUG_STACK_BYTES`, Standard 8192, Priorität `FIRE_DEBUG_PRIORITY`). Mit `FIRE_DEBUG=tcp:127.0.0.1:PORT;token=T` verbindet sich das Programm beim Start
  mit spark und wartet vor der ersten Instruktion; ohne die Variable läuft es ohne Debugger. Weitere Transporte (Protokoll, "Transport"): `FIRE_DEBUG=stdio`
  (gerahmt über stdout/stdin), auf dem ESP32 fest die Konsolen-UART (`FIRE_DEBUG_TRANSPORT_UART`), auf FreeRTOS fest stdio (`FIRE_DEBUG_TRANSPORT_STDIO`) -
  gerahmt wartet das Programm höchstens 3 s auf den Debugger. Er liest Werte nur, solange ihr
  Thread steht (mit Threads unter der GIL), ruft nie fire-Code zum Anzeigen auf und fasst keinen Zähler an; `set` führt der angehaltene Thread selbst aus.
  Ein angehaltener Thread wartet ohne GIL, die anderen laufen weiter; neue fire-Threads melden sich und laufen mit den Haltepunkten los.
* Getestet in `fire.Testing` (`NativeDebugTests.cs`, Fälle "Native-Debugger"): 22 Skripte (Schleifen, Rekursion, Objekte, Properties, Destruktoren, Lambdas,
  try/catch/finally/resume, Strings, Einheiten, Arrays, statische Felder, Vererbung, Operatoren/ToString, Standardwerte, break/continue, Listen,
  unbehandelte Ausnahmen), je Haltepunkte/Schritte mit Aufrufstapel, Scopes, `this` und Globals sowie jeder Step Into und Step Over vom Start bis zum
  Ende gegen die VM; dazu `#line`, gcc/clang/tcc ohne Warnung, gdb, `expand`/`set`, `pause`, `detach`, Threads, unter AddressSanitizer/UBSan; der Agent für Windows mit MinGW unter Wine.
  Gerahmt: drei Fälle über `FIRE_DEBUG=stdio` und zwei auf dem FreeRTOS-Simulator (`native/sim`, Ziel `freertos`) mit denselben Plänen gegen die VM, die Eingabe
  des Programms durch den Agenten, Text, der wie ein Rahmen aussieht, ein verfälschter Rahmen, die Wartezeit ohne Debugger; ein ESP32 unter QEMU (siehe "ESP32").

## Messwerte

**C gegen C++** (während des Ports, gcc 13 `-O2`, beste von 25 Läufen, mit Prozessstart): die neun Benchmarks von `fire.Benchmarks` waren als C 4-16 % schneller
als der C++-Code davor. 2 Millionen Aufrufe einer Funktion mit `try`/`catch`: 12,9 ms (C++: 15,2 ms) - mit allen Variablen im Frame waren es 34,7 ms, daher hält
der Frame nur, was die Catch-Blöcke benutzen.

### Benchmarks vor und nach dem C-Port

Verglichen: der Generator vor dem Port (Commit `9de4c77`, der Vorgänger von `b0c73d9` "native-c: C runtime fire_rt.h"; erzeugt C++ gegen `fire_rt.hpp`) und der
heutige (C11 gegen `fire_rt.h`), dieselben neun Programme aus `src/fire.Benchmarks/BenchmarkScripts.cs`, jeweils mit `forge build --engine native` gebaut - also
`g++ -std=c++17 -O2` gegen `gcc -std=c11 -O2` (sonst dieselben Schalter: `-Wall -Wextra -pthread -s`). Rechner: Intel Xeon @ 2,10 GHz (4 vCPUs, VM), Linux 6.18,
gcc/g++ 13.3.0. Gemessen wird die Wandzeit eines Programmlaufs mit Prozessstart; alt und neu laufen abwechselnd, die Tabelle zeigt das Minimum (60 Läufe je
Programm, bei den 20-fachen 20 Läufe). Auf dem Rechner liefen gleichzeitig andere Prozesse (Last ~4); die Mediane geben dasselbe Bild. Die Ausgaben sind in
allen Fällen gleich. Weil die Programme nur wenige Millisekunden laufen, gibt es jedes zusätzlich mit etwa 20-facher Arbeit (größere Schleifenzahlen, `Fib(29)`
statt `Fib(23)`) - dort zählt der Programmstart kaum noch.

| Benchmark | C++ vorher | C jetzt | Verhältnis | 20-fach: C++ vorher | C jetzt | Verhältnis |
|---|---:|---:|---:|---:|---:|---:|
| leeres Programm (`print(0)`) | 1,73 ms | 1,20 ms | 0,69 | - | - | - |
| `loop` | 2,74 ms | 2,15 ms | 0,79 | 27,7 ms | 26,4 ms | 0,95 |
| `float` | 5,48 ms | 5,05 ms | 0,92 | 81,8 ms | 81,2 ms | 0,99 |
| `fib` | 1,95 ms | 1,39 ms | 0,71 | 8,7 ms | 7,5 ms | 0,85 |
| `method` | 1,84 ms | 2,66 ms | 1,44 | 8,8 ms | 34,3 ms (jetzt ~10 ms) | 3,88 (jetzt ~1,1) |
| `array` | 6,63 ms | 5,84 ms | 0,88 | 84,6 ms | 73,6 ms | 0,87 |
| `string` | 2,61 ms | 2,13 ms | 0,81 | 18,7 ms | 20,2 ms | 1,08 |
| `alloc` | 3,16 ms | 2,58 ms | 0,82 | 34,5 ms | 27,4 ms | 0,79 |
| `lambda` | 3,63 ms | 3,18 ms | 0,88 | 41,4 ms | 42,5 ms | 1,03 |
| `list` | 4,41 ms | 3,87 ms | 0,88 | 44,9 ms | 47,6 ms | 1,06 |

Alle neun ließen sich mit dem alten Generator bauen. Was man sieht:

* Der **Programmstart** ist als C gut 0,5 ms kürzer (keine libstdc++, keine statischen Initialisierer); bei den Programmen, wie sie in `fire.Benchmarks` stehen,
  macht das den größten Teil des Unterschieds aus.
* Die **reine Rechenzeit** (20-fach) ist meist gleich oder etwas besser: `fib` (Aufrufe) und `alloc` (Objekte anlegen und freigeben) 15-20 % schneller, `array`
  13 %; `string`, `lambda` und `list` liegen innerhalb von ±8 % (die Mediane: 0,97/1,02/1,04 - Rauschen).
* **`method` ist langsamer - nicht wegen C**, sondern wegen einer späteren Änderung des Generators (Zugriffsschutz): weil `List` (und die Hashtabelle hinter `Dictionary`) im Prelude ein
  privates Feld `count` haben, gehen `this.count` lesen und schreiben im Benchmark über die allgemeinen Zugriffsfunktionen `gf_count`/`sf_count` (mit
  Klassen-Switch und Prüfung des Aufrufers) statt direkt auf das Feld. Mit dem Feld umbenannt (`tally`) braucht die C-Fassung 8,3 ms - so viel wie der C++-Code.
  **Behoben:** Ein Feld, dessen Name irgendwo privat oder geschützt ist, liest und schreibt der Generator jetzt direkt über den Index, wenn der Typ des Objekts
  bekannt ist und der Code der Funktion das Feld auf jeder möglichen Klasse benutzen darf (die Prüfung steht dann schon beim Übersetzen fest, z. B. `this.count` in
  einer Methode von `List`); `method` braucht damit wieder ~10 ms. Dasselbe gilt jetzt für Methoden und Properties: ein Programm, das in einer Schleife eine
  private Methode, eine private Property und eine geschützte Methode über `this` benutzt, braucht 36 statt 50 ms.

Rechner dieser Sitzung, g++ 13 `-O2`, VM im Modus *Performance*, Zeiten ohne Prozessstart (1,3 ms):

| Benchmark | VM | erzeugt (g++ -O2) | Faktor |
|---|---:|---:|---:|
| `loop` (1,5 Mio. Iterationen, Int) | 158 ms | 2,3 ms | ~68x |
| `float` (600 000 Iterationen) | 99 ms | 5,3 ms | ~19x |
| `fib` (rekursiv, `Fib(23)`) | 29 ms | 1,3 ms | ~21x |
| `method` (250 000 Methodenaufrufe, Felder) | 59 ms | 2,6 ms | ~23x |
| `array` (Array füllen und summieren) | 126 ms | 4,6 ms | ~28x |
| `string` (Verkettung, Zeichenkettenmethoden) | 39 ms | 1,9 ms | ~21x |
| `alloc` (60 000 Objekte erzeugen und freigeben) | 38 ms | 1,9 ms | ~20x |
| `list` (`List` aus dem Prelude, `foreach`) | 44 ms | 3,4 ms | ~13x |

Damit laufen alle neun Benchmarks nativ (`lambda` siehe unten). Mit clang++ sind die Werte ähnlich.

*Handgeschrieben generisch* heißt: alles bleibt ein getaggter `Value`, so wie es der Generator erzeugt; mit nackten `int64`/`double`
(Typinferenz) wird `fib` weitere ~4x schneller (`native/spike/spike.cpp`). Die Zahlen sind Momentaufnahmen von einer
einzigen Maschine, keine Garantie.

### Klassen-Deskriptoren statt `switch` in den Hooks der Runtime (ROADMAP 9, A0)

Was die Runtime über eine Klasse wissen muss - ihr Name (`className`), die Destruktoren ihrer Kette (`runDestructors`), `ToString()` (`userToString`), das Feld
`message` einer Ausnahme, die nichts fängt (`excMessageIndex`), das ganzzahlige Feld `ticks` eines TimeSpan (`timeObjTicks`) und welche `catch`-Typen ein Objekt ist
(`excMatches`) -, steht in einer Tabelle `FireClassDesc kClassDescs[]` (eine Zeile je Klassen-Id, `fire_init` setzt `g_classDescs`), nicht mehr in je einer generierten
Funktion mit einem `switch` über die Klassen-Ids. Die Funktionen sind jetzt Teil der Runtime (`fire_rt.h`, am Ende). Der `catch`-Typ `Exception` hat immer die Id 0
(`FIRE_EXC_ANY`), jede Klasse listet die Ids der übrigen `catch`-Typen, die sie ist. Das ist die Voraussetzung dafür, dass Module ihre Klassen selbst beschreiben
(Abschnitt "Getrennte Übersetzung: `linkage`").

Gemessen (gcc 13 `-O2`, eigene Programme: die Benchmarks von `fire.Benchmarks` mit etwa 20-facher Arbeit plus `exceptions` - 300 000 geworfene Ausnahmen
zweier Klassen einer Hierarchie, zwei `catch` - und `virtual` - Aufrufe über die Basisklasse, `ToString`, ein Destruktor), alt und neu abwechselnd, 30 Läufe, mit
Prozessstart; gleiche Ausgaben:

| Benchmark | vorher min | nachher min | Verhältnis | Median-Verhältnis |
|---|---:|---:|---:|---:|
| `alloc` | 43,8 ms | 45,1 ms | 1,03 | 0,98 |
| `exceptions` | 44,5 ms | 44,5 ms | 1,00 | 1,03 |
| `fib` | 12,5 ms | 13,0 ms | 1,04 | 1,01 |
| `list` | 88,3 ms | 89,2 ms | 1,01 | 1,03 |
| `method` | 15,8 ms | 14,4 ms | 0,91 | 0,89 |
| `string` | 31,2 ms | 30,0 ms | 0,96 | 0,96 |
| `virtual` | 58,0 ms | 58,4 ms | 1,01 | 0,96 |

Alles innerhalb des Rauschens (±4 %, Last auf dem Rechner ~1). Die gestrippten Programme sind 0-8 KB größer (Seitengrenzen; die Tabelle ersetzt die Switches).
Nach Phase A (Module; mit `linkage` `"source"` ändert sich am erzeugten C nur ein Feld der Tabellen von `try`) noch einmal gegen den Stand vor A0, dieselbe Messung:
min-Verhältnis `alloc` 1,00, `exceptions` 1,03, `fib` 1,03, `list` 1,01, `method` 0,92, `string` 0,99, `virtual` 1,02 (Mediane 0,94-1,05) - Rauschen.

## Stand

Übersetzt wird eine Teilmenge der ISA:

* Konstanten (Int/Float/Bool/Char/String/Undefined, mit Einheit), `Pop`/`Dup`/`Swap`
* Lokale und globale Variablen, Blockscopes, verschmolzene Instruktionen (`StoreLocalPop`, `JumpIfNotLt`, `ArithLocalConstPop` ...)
* `+ - * / %`, Bit-Operationen, Vergleiche, `&&`, `||`, `!`, `if`/`while`/`for`/`do`, `break`/`continue`
* Statische Methoden (inkl. Rekursion), `print`
* **Objekte**: Klassen mit Feldern und Feld-Initialisierern, Konstruktoren (inkl. `base(...)`), Methoden mit virtuellem Aufruf,
  `base.Methode()`, statische Felder, Vererbung, Destruktoren (abgeleitete Klasse zuerst), **Ownership** (SPEC 2): Besitz durch Scopes und
  Objekte, Kaskadenlöschung beim Verlassen eines Scopes und bei `return`, Übergabe eines zurückgegebenen Objekts an den Aufrufer
* Einheiten: Rechnen und Vergleichen **gleicher** Einheiten
* Float-Genauigkeit 32 oder 64 Bit (`#floatwidth`, `-f`), identisch zur VM

* **Strings** (UTF-16 wie in der VM): Konstanten, `+` mit allen Wertarten (inkl. `ToString()` von Klassen), `$"{x:F2}"` (Formate `X`, `D`, `B`, `F`),
  `.Length`, Indexierung, alle Methoden von `string` (`IndexOf`, `LastIndexOf`, `Substring`, `CharAt`, `Contains`, `StartsWith`, `EndsWith`,
  `ToUpper`, `ToLower`, `Trim*`, `Replace`, `Split`, `PadLeft`, `PadRight`) und von `char`
* **Arrays und Puffer**: `new T[n]`, Literale, Zugriff und Zuweisung, `++` auf Elementen, `length`, verschachtelte (gezackte) Arrays,
  `byte[]`, `foreach` über Arrays, Index-Methoden von Klassen (`GetIndex`/`SetIndex`) und damit `List` aus dem Prelude; als Teil des Besitzmodells
  (SPEC 2.5): `TakeLocal()`, `TakeUpwards()`, `TakeGlobal()`, `TakeTo(obj)`, `delete x`, `DestroyedException`

* **Lambdas**: `func (x) => ...`, Kurzschreibweisen, Captures als Kopie (SPEC 4.2.1), `on`-Ziel, verschachtelte Lambdas, Signaturprüfung
  (`lambda<...>`), Einheitenprüfung (`CheckUnit`). Ein Lambda ist ein referenzgezählter Wert (Funktionszeiger, kopierte Captures, `on`-Ziel); die
  Captures sind Variablen hinter den Parametern. Standard-Parameterwerte (Methoden, Konstruktoren, Erweiterungen, Lambdas): jeder Standardwert ist eine
  eigene Funktion (sie sieht `this` des Ziels, bei einem Lambda dessen `on`-Ziel); der Aufrufer wertet die fehlenden der Reihe nach aus (`adoptV`) und ruft
  dann mit allen Argumenten, der Dispatcher einer Methode mit Standardwerten bekommt dafür die Liste des Aufrufers; ein Lambda trägt `nreq` und `dflt`
  (`callLam` füllt auf).

* **Ausnahmen** (SPEC 7): `throw` (auch als Ausdruck), `try`/`catch` (nach Typ, `catch (e)`, mehrere Klauseln, `catch` ohne `try`), `finally` auf
  jedem Weg (normal, `break`, `continue`, `return`, Ausnahme, aus dem `catch` heraus), Weiterwerfen, `e.resume(wert)` an der Wurfstelle, auch aus
  verschachtelten Aufrufen heraus. Die Fehler der Sprache sind fangbar: `IndexOutOfBoundsException` (Array, Puffer, Zeichenkette, Methoden von `string`)
  und `UnitMismatchException` (`CheckUnit`, Felder mit Einheit). Eine nicht gefangene Ausnahme meldet die Klasse auf stderr und beendet
  das Programm mit Exitcode 1, ohne Abwickeln - wie die VM.

* **`extern`** (SPEC 8.1.1): eine mit `#extern "lib"` deklarierte Funktion wird als C-Funktion deklariert (`extern "C" ... __asm__("name")`, damit der Name nicht mit der Runtime
  kollidiert) und direkt aufgerufen; die Bibliothek muss beim Übersetzen des C++ dazugelinkt werden (die Zeile der Deklaration nennt sie). Typen: `bool`, `int` (so breit wie
  deklariert, `int[32]` = 32 Bit, sonst Zeigerbreite), `float` (`float[32]` = `float`), `char`, `string` (UTF-8, nach dem Aufruf freigegeben), Zeiger (ein Zeiger auf eine Variable
  bekommt eine Kopie, die danach zurückgeschrieben wird, wie in der VM). Ohne `#extern` (Host-Registrierung in C#) ist die Funktion nicht übersetzbar; `try Name(...)` (Timeouts) auch nicht.

* **Reflection und `probe`** (SPEC 8.13/8.14, `#import "reflection"`): was das Programm per Name erreichen kann, hängt davon ab, welche `__refl_*`-Funktionen es aufruft
  (Stufen, `CppGenerator.Reflection.cs`, siehe "Größe des Programms"): Mitglieder eines Objekts per Name (`Reflect.Get/Set/Has`, Selektoren, Proben) brauchen die Felder und
  Property-Zugriffe der Klassen, deren Objekte es gibt; `Reflect.Call` deren Instanzmethoden, die da sind; `Reflect.New` die Konstruktoren der Klassen in den Tabellen. Die Tabellen
  beschreiben in den Stufen 0-2 jede Klasse, in Stufe 3 die erreichten (gemacht, benutzt, ihre Basisklassen, `[Keep]`, als Literal an die Reflection gegeben); eine weggelassene
  Methode steht nicht darin, nur ihr Name (für den klaren Fehler, siehe "Größe des Programms").
  Die Beschreibungen sind Tabellen (`RfClass`/`RfMember`: die eigenen Mitglieder jeder Klasse; die geerbten liest `rf_visible` aus den Zeilen der Basisklassen, ein abgeleitetes
  verdeckt gleichnamige - dieselbe Liste wie `ReflectionNatives.Members` der VM); `Reflect.Get/Set/Call/New/Has`
  sind erzeugte Funktionen über die Feldhelfer und Dispatcher des Programms - mit denselben Zugriffs-, Einheiten- und Property-Regeln und denselben Fehlermeldungen
  (`ReflectionException`). Für private/protected zählt der Code, der die Bibliothek aufgerufen hat: ein Aufruf einer Methode der Bibliotheksklassen von außen setzt `g_reflCaller`.
  Ein Selektor (`c => c.radius`) trägt seine Mitgliedskette in der `Lam` (`sel`). `probe`/`silence`: ein Objekt mit Proben hat Flag 2 und eine `ProbeNode` (Seitentabelle);
  die Setter-Helfer (`sfo_` = der eigentliche Setter, `sf_` prüft zuerst das Flag) rufen dann `probedSet`: `changing`-Handler (ein `false` bricht ab), Schreiben, `changed`-Handler
  bei geändertem Wert, ohne Wiederholung für dasselbe Mitglied. Die Bibliotheks-Prelude hält ihre Objekte selbst (`TakeTo`, `flat`): eine Liste besitzt ihre Elemente nicht, und die Arrays
  nativer Aufrufe sterben mit dem Scope, der sie bekam.

* **Zugriffsmodifikatoren** (SPEC 5.7): `private`/`protected` bei Feldern, Methoden, statischen Mitgliedern, Properties und Konstruktoren werden wie in der VM geprüft
  (Debug und Release, nicht `#performance`), der Fehler ist die fangbare `AccessDeniedException` mit derselben Meldung. Was der Generator beim Übersetzen entscheiden
  kann (statische Aufrufe, statische Felder und Properties, Konstruktoren, `base.M()`: er kennt die Klasse des aufrufenden Codes, `Chunk.OwnerClass`), kostet nichts.
  Ist die Klasse des Objekts bekannt (`this`, typisierte Variablen, `new`), gilt das auch für Felder, Methoden und Properties: darf der Code das Mitglied auf jeder
  Klasse benutzen, die das Objekt sein kann (die Klasse und ihre Unterklassen), wird es ohne Prüfung direkt angesprochen - das Feld über den Index, die Methode
  und der Getter/Setter als Funktion (auch die Abkürzung über den `OverloadHint` nimmt nur erlaubte Klassen, die anderen gehen zum Dispatcher); eine verbotene
  Stelle meldet schon der Typprüfer (`TC012`). Nur wo die Klasse nicht bekannt ist (`any`, Reflection), bekommen Feldhelfer (`gf_`/`sf_`) und Dispatcher eines
  irgendwo eingeschränkt deklarierten Namens den Parameter `caller` (die Klasse des aufrufenden Codes) und prüfen je Klasse; alle anderen Namen behalten den
  schnellen Pfad. Der Initialisierer eines statischen Feldes läuft ungeprüft. Ein Parameter mit einem Interface als Typ (nicht `IEnumerable`) hat den statischen Typ
  „ein Objekt einer Klasse, die das Interface implementiert, oder undefined" (die Prüfung am Parameter lässt nichts anderes durch). Ein Aufruf darauf, den alle diese
  Klassen öffentlich implementieren (für die Methoden des Interfaces erzwingt das `E4063`), geht an einen eigenen Verteiler `icall_<Interface>_<Methode>_<n>`: nur
  diese Klassen, ohne `caller` und ohne Prüfung, ohne den Rückgriff auf den allgemeinen Dispatcher (eine Methode, die nur an dieser Stelle gerufen wird, bleibt so
  inline). Er entsteht nur, wo er etwas spart: der Name ist irgendwo eingeschränkt, oder eine Klasse außerhalb des Interfaces hat die Methode auch. Messung
  (gcc -O2, 2·10⁸ Aufrufe `s.F(acc)` über `IShape s`, eine fremde Klasse mit `private int F(int)`): 0,65 s → 0,40 s, so schnell wie ohne die fremde Klasse; wird die
  Methode auch anderswo gerufen (nicht mehr inline), ändert sich nichts messbar (~1,1 s vorher wie nachher).
  Eine Property eines Interfaces (`int Limit { get; set; }`) über einen Wert des Interface-Typs geht an die Feldhelfer `gf_`/`sf_` des Namens, wie jede Property
  eines Objekts unbekannter Klasse (die Klasse des Objekts entscheidet, ihr Getter/Setter ist öffentlich). Ein Interface, das andere erweitert (`interface
  IConnection : IO.IStream`), braucht nichts Eigenes: der Compiler schreibt in die Interface-Liste jeder Klasse (`RuntimeClass.Interfaces`) auch die geerbten
  Interfaces, daraus entstehen die `isof_`-Prüfungen, die Klassen eines `icall_`-Verteilers und die Interface-Listen der Klassenbeschreibungen für Module und Plugins.

* **Eingebaute Umwandlungen**: `string.ToBytes/ToUnicode`, `char.ToByte/ToUnicode`, `byte.ToChar`, `buffer.ToString/ToUnicode/ToUnicodeChar/ToLittleEndian/ToBigEndian` und `buffer.littleEndian`
  (SPEC 8.10; ein Puffer trägt seine Byte-Reihenfolge, die der Maschine wird zur Laufzeit festgestellt) stehen im Dispatcher der Methode, wenn keine Klasse sie selbst deklariert.

* **Bekannte Abweichungen von der VM**: (Beim Verlassen eines `catch` zerstören VM und nativ zuerst die Objekte des Wurfortes, dann die des `catch`: der Scope des `catch` wird erst an der Landestelle verlassen.) Ein Zeiger auf eine lokale Variable (`unsafe`), der die Funktion überlebt, zeigt in der VM
  auf den noch lebenden Scope, nativ ins Leere (wie in C).

* **`copy` und `flat`** (SPEC 2.4): `flatCopy`/`deepCopy` arbeiten allgemein auf den Köpfen (Klassen-Id, Felderzahl, Felder): kein Konstruktor, die Kopie gehört dem
  Besitzer wie jedes neue Objekt; `copy` kopiert alles Erreichbare einmal (Identitätstabelle `CopyMap`, Zyklen eingeschlossen) und hält die Besitzverhältnisse
  (was einem mitkopierten Objekt gehörte, gehört dessen Kopie, Arrays und Puffer dem Besitzer der Kopie). `obj.feld = copy x` gibt die Kopie dem Objekt
  (`copyOwned`). Als **Argument** (`f(copy x)`) reist die Kopie in der Argumentliste `AL` des Aufrufs wie ein durchgereichter Rückgabewert (`copyArg`/`finishArgs`):
  sie gehört der aufgerufenen Funktion und stirbt mit ihr, außer die Funktion behält sie. (Destruktoren dieser Kopie laufen nach dem Aufruf, nicht davor.)

* **Einheiten** (SPEC 3): `Value::unit` ist der Index in eine Tabelle (`g_ud`), die zur Laufzeit wächst: eine Einheit ist ein Exponentenvektor über den
  Basissymbolen des Programms (`g_dimNames`, vom Generator gesammelt und wie in der VM sortiert) und ein Faktor zur Basis; `mm * mm` oder `m / s` legen neue Einträge an
  (gleiche Dimension, Faktor und Anzeigetext = derselbe Index, `unitEq` vergleicht auch verschiedene Indizes mit der Toleranz 1e-12 der VM). Der schnelle Pfad
  (gleicher Index, oder beide ohne Einheit) bleibt unverändert; sonst rechnen `addUnits`/`mulUnits`/... wie `Value.Add` usw.: `500mm + 2m` wird umgerechnet (zwei
  `int` bleiben `int`, die feinere Einheit ist das Ziel), `Unit.Multiply`/`Divide` mit Anzeigetext aus den Symbolen der Operanden (`mm^2`, `km/h`, `kg*m/s^2`, `ms^-1`: `unitCombinedName`). Dazu `value:unit` (`coerceUnit`,
  `int` halb-gerade gerundet), `value!type` (`coerceType`), `is in`, `is of` (je Typname eine erzeugte Funktion `isof_...`), `is from`/`is under` (`isFrom`) und
  `^` (`power`). Unverträgliche Einheiten sind wie in der VM ein nicht fangbarer Fehler (stderr, Exitcode 1). Das Zahlenformat `E` (`1.234568E+004`) kommt
  aus `formatValue`.

* **Properties** (SPEC 8.8): `get_Name`/`set_Name` sind gewöhnliche Methoden. Der Feldzugriff `gf_`/`sf_` entscheidet je Klasse wie die VM: ein echtes Feld der
  Klasse gewinnt, sonst wird der Getter/Setter aufgerufen (dessen Ergebnis gehört der Liste des Aufrufers, darum bekommen die Helfer von Namen mit Property
  die `OwnList*`). Hat jede Klasse, die ein Objekt bekannten Typs sein kann, dieselbe Property (und kein Feld des Namens), ruft der Generator ihren Getter/Setter
  direkt (ohne Klassen-Switch). Ein Setter ohne Getter (und umgekehrt) und die Adresse einer Property sind Laufzeitfehler wie in der VM. Statische Properties
  rufen `GetStaticField`/`SetStaticField` direkt.

* **Operator-Überladung** (SPEC 5.11): überlädt irgendeine Klasse des Programms `operator+` usw., gehen alle Operationen dieses Operators durch einen erzeugten
  Wrapper `ov_...(a, b, list)`: ein Objekt als linker Operand mit dieser Überladung ruft sie über den Dispatcher der Methode auf (das Ergebnis gehört der
  Liste des Aufrufers), sonst gilt die eingebaute Operation (`==` auf Objekten ohne Überladung: Identität). `==` und `!=` sind getrennte Überladungen wie in
  der VM. Programme ohne Überladung eines Operators behalten den schnellen Pfad. `[]` läuft schon über `GetIndex`/`SetIndex`.

* **`ref`-Parameter** (SPEC 5.4.2): der Aufrufer übergibt einen Zeiger auf die Variable (`PtrV(&B3_0)`), das Feld (`fp_name`) oder das
  Array-/Puffer-Element (`addressOfIndex`); der Parameter liest und schreibt durch ihn (`ptrRead`/`ptrWrite`, der Speicher zählt mit). Eine Variable,
  deren Adresse genommen wird, ist ab dann im Speicher (nur diese Funktion wird langsamer). Eine Methode ohne `ref` an derselben Stelle bekommt den
  Wert: bei bekanntem Ziel setzt der Aufrufer `ptrRead`, beim virtuellen Aufruf der Dispatcher (`derefArg`).

* **Threads** ([fire – Multithreading-Design](THREADING_DESIGN.md)): ein Programm mit `fire`, `leave`, `terminate`, `sync`, Actors oder `catch threads/terminate` bekommt `FIRE_THREADS` (Binary mit
  `-pthread` linken; nur auf Hosts, nicht auf Embedded-Zielen). Jeder Fire-Thread ist ein echter `std::thread`; aller fire-Code läuft unter **einer globalen Sperre (GIL)**,
  einem fairen Ticket-Lock: ein Thread gibt sie nur beim Warten (`sync`, `process`, Sektionen) und an sicheren Punkten ab (Schleifen-Rücksprung, Funktionsanfang, alle 4096 Stationen
  ein `gilYield`). Dadurch sehen Besitzstrukturen, Handle-Tabelle und Zähler nie zwei Threads zugleich. Zustand eines Ausführungsstrangs (`g_unwind`, `g_handlers`, `g_pending`,
  Temporärpool, `g_globalOwn`, `g_reflCaller`) ist `thread_local`.
  Der Rumpf eines `fire`-Blocks ist eine eigene Funktion (`FuncKind.FireBody`, wie das Hauptprogramm: eigener globaler Scope `OG`); die Globals des Hauptprogramms (Slots unterhalb
  `globalSlotCount`) sind die geteilten `G{n}`, die Slots dahinter (Erfassungen und Variablen des Blocks) Variablen des Threads `T{n}`. `taking x` kopiert **im Aufrufer**:
  `takeCopy` = `DeepCopier` im Modus `taking` (nur der eigene Besitzbaum, sonst Abbruch "taking rejected"; Lambda/Zeiger im Graphen ebenso; Arrays und Puffer werden mitkopiert).
  Vorher prüft `takingNoCopyFree` alle Erfassungen auf `[NoCopy]` (`noCopyFree`: die Entdeckungsphase des `DeepCopier`, ohne zu kopieren) - eine `ArgumentException`, der Thread
  startet nicht; ebenso `postJob` (die Objekt-Argumente von `fire global`, vor dem Einreihen) und `syncValue` (`syncNoCopyFields` geht den Weg von `syncFields`, ohne zu schreiben).
  Die Kopien reisen in einer Liste (`travel`) und gehören dem globalen Scope des Threads; sie tragen Flag 16 (am Ende des Threads läuft ihr Destruktor nicht, was der Thread selbst
  darin angelegt hat wird zerstört) und - die Wurzel - Flag 32 samt Eintrag in `g_originOf`/`g_copiesOf` (der Original-Zeiger für `sync`; ein zerstörtes Original gibt `undefined`).
  `sync`/`sync flat` (Fall A/B/C, Arrays elementweise neu, Puffer kopiert) arbeiten auf den Feldern gleicher Klasse.
  `leave`/`terminate` sind **keine Ausnahme, aber laufen wie eine**: `g_unwind` mit `UW_LEAVE`; kein `catch` passt, ein `finally` läuft (Abschluss 4), Landeplätze und
  `EndFinally` kennen es. Ein Thread endet damit ohne Rest; `terminate(v)` setzt (erster Aufruf gewinnt) ein globales Signal, jeder Thread bemerkt es am nächsten sicheren Punkt oder
  Warten. Das Hauptprogramm führt danach `catch terminate(v)` aus, wartet auf alle Threads (und bedient dabei deren Sektionen und Ausnahmen, `mainFinish`) und zerstört erst dann
  seine Globals; `terminate(n)` mit einer Zahl ist der Exitcode. Eine unbehandelte Ausnahme in einem Thread wickelt ihn ab (der globale Scope bleibt, die Ausnahme gehört ihm:
  `graveyard`) und geht an das Hauptprogramm: `catch threads(...)`, sonst Meldung und Exitcode 1.
  **Actors**: ein Actor-Objekt hat Flag 8 und eine Mailbox (Seitentabelle); jeder Dispatcher heißt dann `direct_...`, `call_...` stellt für einen Actor eine Nachricht
  (Thunk `msg_...`, Argumente referenzgezählt) in die Mailbox, `process`/`try process` führt eine aus (blockierend: Warten ohne GIL).
  **Globals** (THREADING_DESIGN 7): ab dem ersten `fire` gehört alles, was der globale Scope des Hauptprogramms besitzt, zum geteilten Bereich (Flag 64, `link` hält ihn aktuell).
  Ein Thread liest direkt; eine Zuweisung an ein Global, ein Feld/Array-Element/statisches Feld des Bereichs und der Aufruf einer Methode eines Objekts des Bereichs laufen in einer
  **Sektion** (`SectionScope`): Anmeldung in der Warteschlange, Warten (ohne GIL), bis das Hauptprogramm sie an einem sicheren Punkt (wie `#globaljobs` sagt; bei `sync`/`#nosync` erst bei `sync globals`) erteilt;
  es wartet dann, bis sie endet. `sync global { }` ist `sectionEnterOp`/`sectionExitOp`, `fire global { }` ein Auftrag (`postJob`, Objekt-Argumente als Kopie), den das
  Hauptprogramm auf seinem Strang mit den echten Globals ausführt (eine Ausnahme darin geht an `catch threads`).
  Abweichungen von der VM: Actor-Referenzen und Objekt-Argumente von Nachrichten gelten als Referenzen (ein Actor muss die Threads überleben, die ihn benutzen); `try sync`
  liefert nie `false` (es gibt nur einen Strang zur Zeit); ein `leave`/`terminate` in einem Destruktor/einer Property wirkt nativ erst an deren Ende; nach einem `leave` in
  einem Thread zerstört dieser auch das, was er in `taking`-Kopien angelegt hat (die VM ließ es liegen). Hosts brauchen `-pthread`
(`compileArgs` des Ziels); auf FreeRTOS sind Fire-Threads Tasks (`threadStart`), eine kleine Stackgröße ist die häufigste Fehlerquelle (`FIRE_THREAD_STACK_BYTES`, `stackBytes`).

### Bridges: Time und `Sleep` - umgesetzt

Die Natives einer `#import`-Bibliothek stehen in einem eigenen Header `packages/<name>/native/fire_bridge_<name>.hpp`, den die erzeugte Datei nur einbindet, wenn das Programm
die Bibliothek importiert (`bridges/` liegt neben `fire_rt.h`; `NativeRuntimeFiles` schreibt sie mit). Sie benutzen die Runtime und die Plattformschicht (`plat::`), nie ein
Betriebssystem-Header direkt. **`time`** (`fire_bridge_time.hpp`): `Sleep`, `DateTime.Now/UtcNow`, Kalenderrechnung, `ToString(format)` (die .NET-Zeitformate, Invariant Culture),
`Parse`/`TryParse`, `TimeSpan`-Text. Was der Header von der Plattform braucht: `plat::unixMicros()` (Wanduhr) und für `Sleep` ohne Threads `plat::sleepMs`; die Zeitzone kommt aus
`localtime_r` (mit `FIRE_NO_LOCALTIME` auf Boards ohne Zeitzonen: Ortszeit = UTC).

* `Sleep(zeit)` (`TimeSpan`, `500ms`, Zahl = Millisekunden) schläft **nicht taub**: mit Threads wartet es ohne GIL (`blockUntil`), `terminate` beendet es sofort (danach geht
  das Programm den Weg von `leave`), und das Hauptprogramm bedient dabei die Warteschlange der Threads (Sektionen, `fire global`-Aufträge, ihre Ausnahmen).
  Eine falsche Angabe ist eine `TimeException` mit den Texten der VM.
* Abweichung: `DateTime.Parse` liest eine Teilmenge der .NET-Formate (ISO-Datum und -Zeit mit `Z`/`+hh:mm`, `yyyy-MM`, `M/d/yyyy`, Monatsnamen (auch `March 2024`), `3:45 PM`, `GMT`); sonst `undefined` bzw. `TimeException`.

**`io`** (`fire_bridge_io.h`, C - auch mit TinyCC): Streams (Datei, Speicher, Konsole), Dateien, Verzeichnisse, Pfade, UTF-8 (der Decoder ersetzt ungültige Folgen wie .NET). Die
Handle-Tabelle und die Fehlerbehandlung (`__IOLastError` je Thread: `g_ioError` in den Thread-Variablen) sind in der Brücke, das Dateisystem bringt das Paket selbst mit
(`platformSources` von `io.fireproj`, je Plattform eine Datei in `packages/io/native/`): `fire_fs_open/tell/seek/truncate/fileSize/fileTime/removeFile/copyFile/moveFile/makeDirs/removeDir/list/
fullPath/...` (die Schnittstelle und das Gemeinsame: `fire_fs_c.h`). Mitgeliefert: `fire_fs_posix.h` (POSIX-Aufrufe; für `posix`, `wasm` und - über `fire_fs_board.h` mit `FIRE_FS_ROOT` für relative Pfade und
32-Bit-Positionen - `esp32` und `freertos`: SPIFFS/FAT/LittleFS über das virtuelle Dateisystem von ESP-IDF), `fire_fs_win32.h` (die Wide-API von Windows), `fire_fs_dos.h` und `fire_fs_none.h`
(`default`, und auf einem Board mit `FIRE_NO_FS`: Dateioperationen scheitern mit `NotSupported`, Konsole und `MemoryStream` gehen). Ein neues System ist eine neue Datei im Paket. (`graphics` liest Bilder und
Systemschriften über dieselbe Schicht; eine C++-Schicht für Dateien gibt es nicht mehr.)
Die Richtlinie des Hosts (`IoPolicy`) ist ein Übersetzungsschalter: `FIRE_IO_POLICY(vollerPfad, zugriff, grund, größe)` (eine Funktion `int(const char*, int, char*, size_t)`, 1 erlaubt; vor den
Includes in der Zielkonfiguration), Standard: alles erlaubt.
Abweichungen: Standardeingabe wartet bei `Read` auf die verlangte Byteanzahl oder das Ende; `IO.Stdio` schreibt direkt auf `stdout`/`stderr` (gemeinsame Pufferung mit `print`);
was ein Skript offen lässt, wird am Programmende geschlossen (statisches Destruktor-Netz).

**`devices`** (`fire_bridge_devices.h`, C): Geräteverwaltung (Handles, Kennungen `treiber:port`, Standardgerät), Empfangspuffer mit Paketgrenzen und `WaitFor`/`WaitForString`. Welche Geräte
es gibt, bestimmen **Treiber**: `FIRE_DEVICES` (Komma-Liste in der Reihenfolge der Anmeldung, Standard `"serial"`; `"loopback"` ist ein simuliertes Gerät `loopback:echo`, das nach 5 ms zurücksendet,
was es bekommt) und `FIRE_DEFAULT_DEVICE` (Kennung des Standardgeräts, `Device.Default`) - beides als `defines` im Ziel. Die seriellen Ports bringt das Paket selbst mit
(`platformSources` von `devices.fireproj`, je Plattform eine Datei in `packages/devices/native/`, Schnittstelle in `fire_dev_c.h`): `fire_dev_serialNames()` und `FireDevSerialPort` (115200 Baud 8N1, `fire_dev_serialOpen/Close/Read/Write`, Lesen ohne
Blockieren). Mitgeliefert: `fire_dev_posix.h` (termios; Linux `/dev/ttyS*|ttyUSB*|ttyACM*|ttyAMA*`, macOS `/dev/tty.*|cu.*`), `fire_dev_win32.h` (Win32-Kommunikations-API, Ports aus der Registry),
`fire_dev_esp32.h` (UART-Treiber von ESP-IDF; `FIRE_SERIAL_PORTS`, `FIRE_UART<n>_TX/RX`) und `fire_dev_none.h` (keine Ports). Die Windows- und ESP32-Teile sind noch nicht auf echter Hardware probiert.
**Es gibt keine Hintergrund-Threads**: Empfangenes wird eingesammelt, wenn das Programm fragt (`HasData`, `Read...`, die Warte-Funktionen, `Connect`) - bis dahin hält es der Puffer des Betriebssystems bzw. des
UART-Treibers. Die Gerätliste entsteht beim ersten Zugriff auf eine Geräte-Funktion und bei jedem `Refresh` (in einem VM-Programm ohne Editor ist sie vor dem ersten `Refresh` leer). Die Warte-Funktionen
benutzen `#timeout` (`SetTimeout` setzt `g_defaultTimeoutTicks`), warten mit freigegebenem GIL und enden bei `terminate`.

**`graphics`** (`fire_bridge_graphics.h`, C - auch mit TinyCC; Zeichencode in `fire_gfx.h`, `fire_gfx_ttf.h`, `fire_gfx_text.h`, `fire_gfx_images.h`, `fire_gfx_slicer.h` daneben in `packages/graphics/native/`;
die Funktion `__GRPHName` heißt im Programm `fire_gfx_Name`): `Framebuffer` (RGBA und Palette, Palette, Transparenz), `Renderer` (Text mit der 8x14-Schrift, Alpha-Blending schaltbar), `Brush` (Füllungen: Rechtecke, Kreise, Ellipsen, Dreiecke, Polygone, Flood-Fill) und `Pen` (Punkt, Linie, Pfad, Umrisse; vorgerenderte Spitze), `Blit` zwischen den Farbmodi, Bilder (PNG, BMP, GIF aus Bytes und aus Dateien) und der `Slicer`. Das ist ein Port von `src/fire.Terminal` ohne
Betriebssystem und ohne Ausnahmen - dieselben Pixel kommen heraus (die Tests vergleichen Prüfsummen mit der VM). Dateien liest es über das Dateisystem von `io` (`"uses": [ "io" ]` in `graphics.fireproj`: `fire_fs_*`, begrenzt durch
`FIRE_IO_POLICY` wie bei IO; Systemschriften ebenso). Die Meldung der letzten fehlgeschlagenen Funktion steht in `g_gfxError` (je Thread). Abweichungen: eine unbekannte oder zerstörte Ressourcen-ID beendet das Programm mit einer Meldung (die VM wirft eine .NET-Ausnahme); die verschachtelten Arrays,
die `Slicer.Slice` liefert, gehören dem globalen Scope (die VM liefert freie Arrays, die ein `ToolPath` behält).

**`windows`** (`fire_bridge_windows.h`, C - auch mit TinyCC; `__GRPHWinName` heißt im Programm `fire_win_WinName`): `Window` zeigt einen Framebuffer und liefert Ereignisse, wie in der VM: `Tick` holt die Ereignisse, zeigt den Framebuffer und führt dann die angemeldeten
Callbacks aus (auf dem aufrufenden Thread mit den echten Globals; eine nicht gefangene Ausnahme beendet nur den Callback und wird gemeldet); `EnableEvents`/`NextEvent` liefern sie als Warteschlange.
Das Fenster selbst ist die **Anzeige**, die das Paket `windows` je Plattform mitbringt (`platformSources` von `windows.fireproj`: `fire_display_select.h` für `posix` und `windows`, `fire_display_wasm.h`, `fire_display_dos.h`, sonst `fire_display_none.h`; die Dateien liegen in `packages/windows/native/`; die Schnittstelle, die Ereignisse und das
Systemaussehen: `fire_display_c.h`): `FireDisplayWindow` mit `fire_display_open/close/pump/present/setVSync/...`.

**Die eigene Anzeige (Standard, ohne SDL).** `fire_display_desktop.h` wählt beim ersten Fenster, mit welchem Fenstersystem es spricht: Wayland (`fire_display_wayland.h`, wenn
`WAYLAND_DISPLAY` gesetzt ist und libwayland-client lädt), sonst X11 (`fire_display_x11.h`), Win32 (`fire_display_win32.h`), Cocoa (`fire_display_cocoa.h`, aus C über die
Objective-C-Runtime), das Canvas der Seite (`fire_display_web.h`), oder ohne Bildschirm (`fire_display_headless.h`, `FIRE_DISPLAY=headless`; `SDL_VIDEODRIVER=dummy` wird
ebenso verstanden). `FIRE_DISPLAY=x11|wayland|win32|cocoa|web|headless` fragt nach einer. Gelinkt wird nur, was das Betriebssystem immer hat: Linux lädt libX11/libwayland-client/libxkbcommon
zur Laufzeit (`-ldl -lpthread`), Windows linkt user32 und gdi32 (imm32, dwmapi, ole32 werden geladen), macOS die Frameworks Cocoa, QuartzCore und GameController; der Browser nichts.
Gemeinsam: `fire_display_keys.h` (die Nummern der Tasten, Modifier, Maustasten, Cursor - die von SDL3, die Programme kennen sie), `fire_display_common.h` (die Warteschlange
eines Fensters, Drops, Kompositionen, die Ereignisse der Tests), `fire_display_joystick.h` (Joysticks: `/dev/input/js*`, XInput, GameController, Gamepad-API). Neu gegenüber den
SDL-Anzeigen: Kompositionen einer Eingabemethode (`FIRE_EV_TEXT_EDITING`, `Window.SetTextInputArea`), `FIRE_EV_SCALE`, Maus herein/hinaus, Fokus, und was hinter dem Fenster liegt
(`Window.Backdrop`: transparent, Blur, Acryl, Mica - je nach System). Entwurf und Stand: [Eigene Plattformschicht statt SDL, Effekte und ein modernerer Stil](UI_PLATFORM.md).

**SDL (wählbar).** `"sdl": 2|3` des Ziels (0: der Build probiert) oder `FIRE_SDL=2|3|auto` nehmen statt der eigenen Anzeige SDL (das Programm sagt dann `#define FIRE_DISPLAY_SDL 1`).
Mitgeliefert: `fire_display_sdl3.h` (SDL3, für `posix` und `windows`, wenn
der Build `FIRE_SDL3` definiert; das Programm bittet dann mit der Zeile `// fire-link: SDL3` um `-lSDL3` bzw. `SDL3.lib`), `fire_display_sdl.h` (SDL2, für `posix` und `windows` sonst und immer für `wasm`;
`// fire-link: SDL2`, `-lSDL2`; beide mit dem Systemaussehen aus `fire_systheme.h`)
und `fire_display_none.h` (kein Bildschirm: `new Window` wirft eine `HandleUnavailableException`; ein Board-Paket liefert seine eigene Anzeige). Am Programmende (`atexit`) halten die Threads der
Fenstermanager an und die Fenster schließen sich. Die VM nutzt SDL3, die Tasten- und
Maus-Werte sind dieselben. Die Tests laufen mit dem SDL-Dummy-Treiber (`SDL_VIDEODRIVER=dummy`) und `-DFIRE_DISPLAY_SELFTEST`, das beim Öffnen eine feste Folge von Ereignissen einspeist (beide
Anzeigen speisen dieselbe Folge ein und liefern dieselbe Ausgabe); die UI-Bibliothek (`#import "ui"`, reines fire) läuft damit nativ und zeichnet dieselben Pixel wie in der VM.

**SDL3 oder SDL2.** Welche SDL ein Programm mit Fenster bekommt, entscheidet der Build (`NativeBuilder.ChooseSdl`), und zwar erst, wenn das Programm wirklich ein Fenster hat: zuerst die
Umgebungsvariable `FIRE_SDL=2|3`, dann `"sdl": 2|3` des Ziels in `fire.native.json`, dann `"sdl"` der Toolchain; sagt niemand etwas, probiert der Build, ob die Toolchain SDL3 für das Ziel übersetzen
und binden kann (ein winziges Programm mit `<SDL3/SDL.h>` und `SDL_Init`, mit denselben `-I`/`-L` wie der Build) - SDL3, wenn ja, sonst SDL2. Die Antwort wird gemerkt (`NativeProbeCache`: im Speicher und in
`native-probes.json` im Ordner der lokalen Daten von fire, `FIRE_PROBE_CACHE=0` schaltet die Datei ab), unter dem Compiler (Pfad und Zeit), dem Ziel und dem Ort von SDL3 (Ordner, Zeit des Headers):
wer SDL3 nachinstalliert, wird neu gefragt. Ohne Toolchain (`files`, `forge native`) gilt SDL2, außer `FIRE_SDL`/`sdl` sagen 3; `wasm` bleibt bei SDL2 von Emscripten. Das erzeugte Programm sagt
`// fire-link: SDL3` und `#define FIRE_SDL3 1` (`fire_display_select.h` nimmt dann `fire_display_sdl3.h`) bzw. `// fire-link: SDL2`. TinyCC übersetzt beide (ohne `__has_include`:
SDL3 liegt immer unter `<SDL3/SDL.h>`; `SDL_DISABLE_IMMINTRIN_H` wie bei SDL2). Was nur SDL3 hat: das **Schweben** eines Zugs aus einem anderen Programm (`FIRE_EV_DRAG_HOVER` 42 mit `x`, `y` aus
`SDL_EVENT_DROP_POSITION`, `FIRE_EV_DRAG_LEAVE` 43 bei `SDL_EVENT_DROP_COMPLETE` ohne Datei und Text; die Stelle eines Drops nennt SDL3 selbst), und **Bilder und Dateien in der Zwischenablage**:
`fire_display_clipboardGetImage/SetImage/HasImage` (PNG- oder BMP-Datei; gesetzt als `image/png` und `image/bmp` - SDL3 legt `image/bmp` unter Windows als `CF_DIB` ab) und
`fire_display_clipboardGetFiles/SetFiles/HasFiles` (Pfade zeilenweise; `text/uri-list` und `x-special/gnome-copied-files`, unter Windows `CF_HDROP` - das hat SDL3 nicht, die Anzeige spricht dafür selbst
mit der Zwischenablage von Windows). Die SDL2-Anzeige sagt dort "nicht vorhanden" (`GetImage` undefined, `SetImage` false, keine Dateien), `fire_display_none.h` hat für alle Arten eine Zwischenablage im
Programm (`fire_display_memClipboard`). `-DFIRE_DISPLAY_SELFTEST_HOVER` speist ein Schweben mit Verlassen und eines mit Drop ein. SDL 3.2 unter X11 kennt die Arten in der Zwischenablage
erst, wenn ihr Besitzer wechselt, während es läuft (XFixes meldet es, dann fragt SDL nach `TARGETS`; `SDL_GetClipboardData` lehnt jede Art ab, die es nicht kennt) - was ein anderes Programm vor dem
Start kopiert hat, sähe ein SDL3-Programm nicht. Deshalb schickt `fire_display_x11ClipboardRefresh` beim ersten Zugriff auf die Zwischenablage, wenn SDL keine Art kennt, CLIPBOARD aber einen
Besitzer hat, über SDLs eigene Verbindung ein `XFixesSelectionNotify` an ein verstecktes Fenster von SDL (libX11 per `dlopen` aus `fire_dragout.h`); SDL bekommt es zurück, fragt selbst nach
`TARGETS`, und die Anzeige holt Ereignisse ab, bis es die Arten kennt (höchstens eine halbe Sekunde). Die VM macht dasselbe (`X11Refresh` in `SdlClipboard.cs`). SDL2 fragt jedes Mal neu.

**Ziehen aus dem Fenster** (`Window.StartDrag`) können weder SDL2 noch SDL3 beginnen: `fire_display_startDrag` beider Anzeigen holt sich das Fenster des Betriebssystems (SDL3: die Eigenschaften
`SDL_PROP_WINDOW_WIN32_HWND_POINTER` bzw. `..._X11_DISPLAY_POINTER`/`..._X11_WINDOW_NUMBER`; SDL2: `SDL_GetWindowWMInfo`, dessen Struktur `fire_display_sdl.h` selbst beschreibt, damit
`SDL_syswm.h` nicht die Header von X11 verlangt) und gibt es an `fire_dragout.h`: unter Windows OLE (`DoDragDrop`, `IDataObject` mit `CF_UNICODETEXT`/`CF_HDROP` und `IDropSource` in C,
`ole32.dll` zur Laufzeit), unter X11 die Quelle von XDND (libX11 per `dlopen`, die Typen von Xlib selbst beschrieben - kein Header, keine Link-Bibliothek; die Schleife hält `fire_display_lockSdl`).
Unter Wayland (nur SDL3: `SDL_PROP_WINDOW_WAYLAND_DISPLAY_POINTER`/`..._SURFACE_POINTER`) `fire_dragout_wayland`: auf SDLs `wl_display` eine eigene Warteschlange mit eigener
Registry, `wl_seat` (bis Version 5), `wl_data_device_manager` (Version 3) und einem eigenen `wl_pointer`, der die Seriennummer des Tastendrucks für `start_drag` lernt (`fire_dragout_wlPump` nach jedem
`fire_display_pump`, unter `fire_display_lockSdl`); fürs Ziehen eine `wl_data_source` und ein `wl_data_device`, dann wird die eigene Warteschlange abgearbeitet, bis `dnd_finished` (die letzte
`action`: Kopie/Verschieben) oder `cancelled` kommt - `send` wird in den Deskriptor geschrieben (ohne SIGPIPE). libwayland-client per `dlopen`, die nötigen Signaturen selbst beschrieben (kein Header).
macOS, der Browser und `fire_display_none.h` sagen 0; `fire_display_canStartDrag` (`Window.CanStartDrag`) sagt das vorher (der Videotreiber `windows`, `x11`, oder `wayland` nativ mit SDL3, sobald die eigene Warteschlange bereit ist), und das `ui` lässt ein Ziehen sonst im Fenster. Zeigt der Thread eines Managers das Fenster, bekommt er den Auftrag (`FireWinShared.dragReq`) und zieht dort; das Programm wartet
ohne die GIL. Die Tests ziehen unter Xvfb mit einer "Hand" aus XTest aus einem Fenster (SDL2, SDL3, `Window.Show`, die VM) in ein natives SDL3-Programm und unter Wine (OLE, zwei Prozesse) genauso; Wayland
mit weston (X11-Backend unter Xvfb), die Hand findet die Fenster dort an ihrer Farbe (ein SDL3 mit Wayland-Treiber: das des Systems oder der Ordner `FIRE_TEST_SDL3_WAYLAND`).

**SDL finden (`SdlSetup`).** Das Programm mit Fenster wird gegen die Entwicklungsdateien von SDL3 oder SDL2 übersetzt (`SDL3/SDL.h` bzw. `SDL.h`, Import-Bibliothek). Der Build sucht sie in dieser Reihenfolge:
die Umgebungsvariable `SDL3_DIR` bzw. `SDL2_DIR` (ein Ordner mit `include` und `lib`, bei MinGW z.B. `SDL3-3.2.26\x86_64-w64-mingw32` oder `SDL2-2.30.9\x86_64-w64-mingw32`), der Ordner `Toolchain\SDL3`
bzw. `Toolchain\SDL2` neben dem Programm, das Präfix des Compilers (MSYS2: `ucrt64\include\SDL3`), das System (`/usr/include/SDL3`, `/usr/local`, Homebrew) und `pkg-config sdl3`/`sdl2`. Für einen
Windows-Build unter Linux (MinGW) zeigt `SDL3_DIR` auf das MinGW-Paket (auf dieser Maschine: `/opt/sdl-mingw/SDL3-3.2.26`, daneben `SDL2-2.30.12`). Fehlt die SDL unter Windows (das portable w64devkit bringt sie nicht mit), fragt der Build wie bei der Toolchain
(Editor: Dialog, Konsole: `[F]ix / [C]ancel`) und lädt das MinGW-Entwicklungspaket (etwa 7 MB) nach `Toolchain\SDL3` bzw. `Toolchain\SDL2`; es wird nichts im System installiert. Danach liegt `SDL3.dll` bzw. `SDL2.dll` neben dem gebauten Programm.
Unter Linux und macOS gibt es keinen Download: die Meldung nennt das Paket (`sudo apt install libsdl2-dev`, `brew install sdl2`). Das Programm definiert `SDL_MAIN_HANDLED`, braucht also weder `SDL2main` noch `-lmingw32`.
Ändert der Nutzer die Fenstergröße, liefert `pump` das Ereignis `FIRE_EV_RESIZE` (4, mit `width`/`height`; nur wenn sich die Größe wirklich geändert hat); `Window.AutoResize` bringt dann den Framebuffer
(`fire_gfx_fbResize`, gleiche Gültigkeitsgrenzen wie in der VM) auf diese Größe. `-DFIRE_DISPLAY_SELFTEST_RESIZE` speist stattdessen ein Resize auf 90x70 ein.
Finger und Joysticks meldet die Anzeige als `FIRE_EV_TOUCH_DOWN/MOVE/UP` (16-18; `finger`, `x`, `y` in Framebuffer-Pixeln, `pressure`) und `FIRE_EV_JOY_AXIS/BUTTON_DOWN/BUTTON_UP/HAT/ADDED/REMOVED` (32-37; `joystick`, `index`, `value`); `fire_display_setTouchMouse`/`fire_display_touchMouse` schalten SDLs Mausereignisse aus Fingern (Window.TouchMouse). Die SDL2-Anzeige öffnet jeden angesteckten Joystick (`SDL_JOYDEVICEADDED`). `-DFIRE_DISPLAY_SELFTEST_INPUT` speist eine feste Folge solcher Ereignisse ein.

**Zeiger** (`unsafe`, SPEC 8.3): ein Zeiger ist ein `K_Pointer`-Wert auf die Speicherstelle einer Variablen, eines Feldes oder eines Array-/Puffer-Elements (`ref`-Argumente). `&`, `*`, `*p = v`,
Zeiger auf Zeiger, der Vergleich, `p + n`, `p - n` und `p - q` laufen wie in der VM. `width` sagt, wohin er zeigt: 0 eine Variable oder ein Feld (der Versatz in Elementen steckt in `unit`: ein Zeiger darauf ist ein
"Array mit einem Element", benutzbar nur bei Versatz 0), 1 ein Byte eines Puffers, 2 ein Element eines Arrays (beide tragen das Handle des Arrays: `unit` = Platz, `reserved` = Generation). `ptrRead`/`ptrWrite`
prüfen mit `ptrValid` (Versatz, zerstörtes Array, Grenzen: eine fangbare `IndexOutOfBoundsException`/`DestroyedException` wie in der VM); für die Grenzen hält die Handle-Tabelle den Kopf jedes Platzes
(`FIRE_PTRBOUNDS`, nur wenn das Programm die Adresse eines Elements nimmt). Mit `#performance` (`FIRE_UNCHECKED`) wird nichts geprüft.

### Plattformschicht

Alles, was vom Betriebssystem oder Board abhängt, steht **in einem Ordner je Plattform**: `native/platform/<name>/fire_platform.h` (C, für die Runtime) und `fire_platform.hpp` (C++, `fire::plat` für die Natives). Die Runtime (`fire_rt.h`) benutzt
nur diese Schnittstelle (`fire::plat`):

| | |
|---|---|
| `nowMs()` | monotone Uhr in Millisekunden |
| `exitProcess(code)` | Programm beenden (hosted: `exit`/`_Exit`; FreeRTOS: `abort`, es gibt keinen Prozess) |
| `Mutex`, `CondVar` (`wait`, `waitFor(ms)`, `notifyAll`, immer mit gehaltenem Mutex) | Sperren und Warten (GIL, Ereignisse, `process`, Sektionen) |
| `threadStart(fn, arg, stackBytes)`, `threadJoin` | ein Fire-Thread |
| `tlsGet/tlsSet` (nur mit `FIRE_TLS_STRUCT`) | ein Zeiger je Task, wo es kein `thread_local` gibt |

Mitgelieferte Pakete: `std` (gemeinsame Basis der Hosts: `std::thread`, `std::mutex`, `std::condition_variable`), `posix` und `windows` (nehmen `std`; hier kommen später Dateisystem,
Konsole, serielle Ports, Fenster hinzu), `freertos` (Tasks, Semaphoren; `CondVar` aus binären Semaphoren: jeder Wartende reiht seine eigene ein, `notifyAll` gibt alle frei)
und `esp32` (nimmt `freertos`; Stack in Bytes, Kerne). Der Zustand eines Ausführungsstrangs (`g_unwind`, `g_handlers`, Pool, ...) ist eine Liste (`FIRE_THREAD_VARS`):
auf Hosts `thread_local`-Variablen, mit `FIRE_TLS_STRUCT` Mitglieder einer Struktur, die am Task hängt (`vTaskSetThreadLocalStoragePointer`, Slot `FIRE_TLS_INDEX`, es muss
`configNUM_THREAD_LOCAL_STORAGE_POINTERS` größer sein) und über Makros wie `g_unwind` erreichbar sind - Programme ohne Threads zahlen nichts.
Einstellungen des FreeRTOS-Pakets (als `defines` der Zielkonfiguration): `FIRE_THREAD_STACK_BYTES` (Standard 8192), `FIRE_FREERTOS_STACK_BYTES` (der Stack wird in Bytes angegeben, ESP-IDF),
`FIRE_THREAD_PRIORITY`, `FIRE_THREAD_CORE`, `FIRE_TLS_INDEX`.

Eine eigene Plattform ist ein Ordner mit `fire_platform.hpp` (Vorlage: die mitgelieferten); die Zielkonfiguration nennt ihn (`platformPath`). Der **FreeRTOS-Simulator**
(`native/sim`, auf pthreads; `NativeRuntimeFiles.WriteSimulatorTo`) erlaubt, ein FreeRTOS-Programm am PC zu probieren; die Tests lassen dieselben Thread-Programme darauf laufen
(`FreeRTOS == VM`).

### Ein Ziel, ein Compiler, eine Toolchain ergänzen (ROADMAP 13)

Drei Achsen, jede mit einer Datei pro Fall - keine große Datei voller `#ifdef`:

* **Compiler-Dialekt** (`native/runtime/cc/`): `fire_cc.h` wählt **eine** Datei je Compiler-Familie - `fire_cc_gcc.h` (gcc, clang),
  `fire_cc_msvc.h`, `fire_cc_watcom.h` (Open Watcom), `fire_cc_generic.h` (TinyCC und jeder andere C99-Compiler). Sie definiert, was C nicht überall
  gleich sagt: `FIRE_COLD`, `FIRE_LIKELY`, `FIRE_NORETURN`, `FIRE_NOT_REACHED(r)` (nach einem Aufruf, dessen Nicht-Rückkehr der Compiler nicht kennt),
  `FIRE_TLS_KEYWORD`, `FIRE_STATIC_ASSERT_C` (ohne `_Static_assert`: ein Array negativer Größe), `FIRE_CC_RUNTIME_BEGIN/END` (Warnungen um die Runtime),
  `FIRE_FAR`/`FIRE_HUGE` (16-Bit-x86) und, wo die C-Bibliothek oder die FPU anders rechnet, `FIRE_CC_FEQ` (`==` mit NaN: die 8087-Emulation von
  Open Watcom meldet "gleich") und `FIRE_CC_FIXED` (`printf("%.*f")` rundet dort eine Hälfte weg von null), dazu ein `nearbyint`, das zur geraden Zahl rundet.
  `fire_rt.h` enthält keine Abfrage eines Compilers mehr. Ein neuer Compiler: eine Datei hier und eine Zeile in `fire_cc.h`.
* **Toolchain** (`src/fire.Native/Toolchains/`): eine Art von Toolchain mit eigenem Treiber implementiert `IToolchainDriver` (Kommandozeile aus einer
  `CompileRequest`, Suche des Compilers, Umgebung seiner Programme) und steht in `ToolchainDrivers`; ihr Standard (`Default`) wird ein eingebauter Name
  (`"toolchain": "watcom"`). `NativeBuilder.CompilerCommand` fragt zuerst die Treiber - gcc, clang, msvc, tcc und emscripten stehen (noch) dort selbst.
  `WatcomToolchain`: `owcc` übersetzt und bindet in einem Schritt (C99, jede Funktion in einem eigenen Segment - ein Segment des Real Mode fasst 64 KB),
  gefunden auf dem PATH, unter `%WATCOM%`, `Toolchain/watcom` oder `/opt/watcom`; es setzt `WATCOM` und `INCLUDE` für seine Programme.
* **Ziel und Plattform**: das Zielprofil (`TargetProfile`; die alten PCs in `TargetProfile.Pc.cs`, eine Familie je Datei) nennt Plattformpaket, Toolchain,
  Breiten und die Schalter des Ziels für den Compiler (`dos16`: `-bdos -mcmodel=h -march=i86 -mstack-size=...`). Das Plattformpaket ist ein Ordner aus kleinen
  Auswahl-Headern je Teil (`fire_platform.h`), die auf gemeinsame Umsetzungen in `std/` zeigen (`fire_platform_dos.h`,
  `fire_env_dos.h`, `fire_*_none.h`); eine neue Umsetzung eines Teils ist eine neue Datei in `std/`, ein neues Ziel ein neuer Ordner. Alle übrigen Teile (Dateisystem, Netz, TLS,
  Geräte, GPIO/I2C/SPI, USB, WLAN, Ton, Anzeige, Konsole, Hardware) bringen die Pakete selbst mit (`platformSources`, ROADMAP 13.3b); ein Paket, das die Schicht eines
  anderen braucht, nennt es in `native.uses` (graphics -> io, tls -> net, circuits -> audio). Die Auswahl je Plattform bleibt wie geschrieben: die Datei der Plattform bindet die Umsetzung ein, die sie wählt (auch in `#if`) - die Dateien, die so eingebunden werden, stehen in `native.headers` und liegen in jedem Build als Dateien unter `pkg/<import>/` neben dem Programm (das `#include` nennt sie dort).

**`dos32`** (DOS mit DOS-Extender ab 386, ROADMAP 13.4): Open Watcom `wcc386` über `owcc -bdos32a` - der Stub ist der ganze Extender DOS/32A, das Programm
ist eine .exe (der Treiber legt `binw` auf den PATH, dort liegen die Stubs). Flacher Speicher, `int`/`float` 64 Bit, `word` 32 Bit, 256 KB Stack, fire-Threads
über den eigenen Scheduler (`std/sched/fire_ctx_x86_32.h`: setjmp/longjmp, ESP für einen neuen Task, IRQ 0 als Takt). Die Plattform ist dieselbe Datei wie bei
dos16 (`std/fire_platform_dos.h`: Uhr, Schlafen, Ende über die C-Bibliothek, die unter DPMI genauso geht). Die Packages: `platform/std/fire_dos_bios.h` macht die
DOS-Dateien von console, windows und devices für beide Ziele gleich; machine `fire_machine_x86_32.h`, dos `fire_dos_dpmi.h`; xms und x86 nicht. Getestet in
DOSBox-X mit den Programmen von dos16 (dieselbe Ausgabe wie die VM mit den Breiten von dos32).

**`dos16`** (DOS im Real Mode ab 8086, ROADMAP 13.3) - Stand: Programme ohne Imports laufen (Zahlen, Strings, Objekte, Arrays, Ausnahmen, Einheiten), getestet in
DOSBox-X: von den 139 nativen Testprogrammen ohne Imports laufen alle wie in der VM außer `#performance` (weicht gewollt ab); dazu Dateien (`io`/`filesystem`
mit DOS-Pfaden). Huge Model (`-mcmodel=h`): Zeiger 32 Bit und über Segmentgrenzen gerechnet, also mit `int`/`float` von 32 Bit (Standard des Ziels) ein 8-Byte-Wert;
`word` 16 Bit. Die Runtime rechnet die Größe jedes Blocks breit (`blockSize`, nie ein kleinerer Block); die Daten von Arrays und Puffern dürfen über 64 KB groß
sein (`dataAlloc`: `halloc`; Löschen, Kopieren und `fread`/`fwrite` in Stücken bis zum Segmentende: `FIRE_CC_ZERO`, `FIRE_CC_COPY`, `FIRE_CC_IO_STEP`).
Gebunden wird gegen die C-Bibliothek des Large Model (ihr Code passt in das eine Segment `_TEXT`, der des Huge Model nicht; gleiche Aufrufkonvention).
fire-Threads laufen dort als Aufgaben des eigenen Schedulers (`std/fire_sched.h`, ROADMAP 13.2b): gewechselt wird an den Safepoints (der GIL gibt ab, `FIRE_PLAT_YIELD`)
und beim Warten; `INT 1Ch` setzt nur ein Flag (`FIRE_PLAT_PREEMPTED`), im Interrupt wird nie gewechselt. Der Wechsel des Kontexts je Architektur in `std/sched/`.
Die Konsole (`#import "console"`) über das BIOS: `std/fire_console_dos.h`.
Offen: Grafik, die Packages `dos`/`machine`/`xms`, `#intwidth 16`.

### Zielkonfiguration (`fire.native.json`)

Das erzeugte C++ hat oben einen **Zielabschnitt**, den die Konfiguration des Ziels bestimmt: die `defines`, die `includes` (z.B. die FreeRTOS-Header, vor dem Plattformpaket),
`FIRE_PLATFORM_HEADER` (welches Paket) und am Ende der **Einsprung** (`int main()` als Prozess mit dem Exitcode von `terminate(n)`, oder eine Funktion, die der Startcode des Boards
aus einem Task aufruft, z.B. `extern "C" void app_main(void)`). Alles andere liegt im Plattformpaket.

Die Datei (gesucht ab dem Ordner der Quelle nach oben, oder `--config`) enthält, was `build` tut und welche Ziele und Toolchains es gibt:

```json
{
  "engine": "native",            // "vm" (Standard) oder "native": was `build` erzeugt
  "target": "my-board",          // Standardziel; sonst dieser Rechner
  "toolchain": "gcc",            // Standard-Toolchain für Ziele ohne eigene; sonst die erste gefundene
  "stripping": 3,                // was wegfällt: 0 "none" .. 3 "all" (Standard), Zahl oder Name (siehe "Größe des Programms")
  "globalJobs": "safepoints",    // wann das Hauptprogramm `fire global`-Aufträge abarbeitet: "safepoints" (Standard), "sleep", "sync" (THREADING_DESIGN.md 7)
  "targets": {
    "my-board": {
      "extends": "freertos",     // eingebautes Ziel oder anderes aus dieser Datei; nur Abweichungen stehen hier
      "includes": ["FreeRTOS.h", "task.h", "semphr.h"],
      "defines": ["FIRE_THREAD_PRIORITY=3", "FIRE_THREAD_STACK_BYTES=6000"],
      "entry": { "name": "board_fire_main", "kind": "function", "externC": true },
      "floatWidth": 32, "stackBytes": 6000, "toolchain": "my-gcc"
    }
  },
  "toolchains": {
    "my-gcc": { "extends": "gcc", "compiler": "/opt/arm/bin/arm-none-eabi-g++", "optimization": "-Os", "args": ["-mcpu=cortex-m4"] }
  }
}
```

Felder eines Ziels: `extends`, `platform`, `platformPath`, `includes`, `defines` (`NAME` oder `NAME=WERT`), `entry` (`name`, `kind` = `process`|`function`, `externC`), `compileArgs`, `linkLibs`,
`supportsThreads`, `floatWidth`, `intWidth`, `wordWidth`, `stackBytes`, `symbols`, `imports`, `embedded`, `toolchain`. Eingebaute Ziele: `windows`, `linux`, `macos`, `esp32`, `freertos`.
`wordWidth` (16, 32 oder 64) ist die Breite von `word`, dem vorzeichenlosen Maschinenwort des Ziels (SPEC 8.10a); ohne Angabe die des Ziels, von dem es erbt: 64 Bit auf `windows`,
`linux`, `macos`, 32 auf `esp32`, `freertos`, `wasm`. Das generierte C bekommt `#define FIRE_WORD_BITS N` (`#if word16|word32|word64`, `Word.Bits`); ein Wort ist ein Wert der Art
`K_Word` (die Bits in `v.w`, oberhalb der Breite 0), ein Wortpuffer ein `Buf` mit `elem = sizeof(FireWord)` (`bufWords`, `allocBufOf`). Ein 8-Byte-Wert (`FIRE_COMPACT`) nur mit
höchstens 32 Bit breiten Wörtern. **Schmales C:** wo der Generator ein Wort oder einen Puffer selbst gemacht hat (eine Umwandlung nach `word`, `byte a[n]`/`new byte[n]`, `word a[n]`/
`new word[n]`, ein Element davon), schreibt er Wort-Arithmetik und -Vergleiche, die Speicherung in `byte`/`word` und die Elemente und `length` des Puffers als C aus (`WordV(x + y)`,
`bufOf(b)->data[i]` mit Prüfung von Lebendigkeit und Bereich, sonst der allgemeine Zugriff), dazu `& | # << >>` zweier ints. Eine Prüfsummen-Schleife über einen Bytepuffer
(2 x 200 x 1.000.000 Elemente, `word`-Summe und `byte`-xor, gcc -O2): 4,45 s vorher, 0,41 s nachher. Die Locals bleiben 16-Byte-Werte (gcc hält die Zahlen in Registern);
eigene `uint8_t`/`uintN_t`-Variablen für `byte`/`word`-Locals sind offen (ROADMAP 13, Punkt 2).
**ESP32:** `build -t esp32 -o <projekt>/main` schreibt den Komponenten `main` eines ESP-IDF-Projekts (dazu eine `CMakeLists.txt` mit `include($ENV{IDF_PATH}/tools/cmake/project.cmake)`,
dann `idf.py build`). `app_main` startet das Programm auf einem eigenen Task, weil der Main-Task von ESP-IDF nur 3,5 KB Stack hat: `FIRE_MAIN_STACK_BYTES` (Standard 16384, als `defines`
des Ziels). Geprüft mit ESP-IDF 5.5 im Emulator (`qemu-system-xtensa`, Testfall "ESP32 (ESP-IDF, QEMU)", läuft, wenn `IDF_PATH` gesetzt ist).
**Ein fremdes Ziel ist immer nativ:** `build -t esp32` (oder `wasm`, oder ein anderes System als dieses) baut ohne `--engine` nativ - die VM gibt es nur für den eigenen Rechner
(sie bringt die .NET-Runtime mit), die Ziele des nativen Backends brauchen kein .NET. `--engine vm` mit einem fremden Ziel ist ein Fehler der Befehlszeile.
Toolchains (`kind`): `gcc`, `clang`, `msvc` (Compiler, Standard, Optimierung, Argumente, Bibliotheken, Include-Ordner), `custom` (`command` mit `{cpp}`, `{dir}`, `{out}`, `{args}`, `{libs}`) und
`files` (schreibt nur die Quellen; `layout: "idf-component"` legt dazu eine `CMakeLists.txt` an, `buildCommand` läuft danach im Ordner, z.B. `idf.py build`). Eingebaut: `gcc`, `clang`, `msvc`,
`files`, `esp-idf`. Die Zeilen mit `//` und Kommas am Ende sind erlaubt.

**Größe des Programms.** Ein Programm wird beim Linken gestrippt (gcc/clang: `-s`, macOS `-Wl,-x`): Symbole und Debug-Informationen machen ohne Strippen etwa die Hälfte der Datei aus
(MinGW: 945 KB → 468 KB). `"strip": false` in der Toolchain lässt sie stehen (zum Debuggen); eine Bibliothek für die VM wird nie gestrippt. `"optimization": "size"` wählt das kleine
Profil: gcc/clang `-Os -ffunction-sections -fdata-sections` und beim Linken `--gc-sections` (macOS `-dead_strip`), msvc `/O1 /Gy /Gw` und `/OPT:REF /OPT:ICF` (Beispiel 3D-Szene unter Linux:
464 KB → 404 KB gestrippt → 285 KB mit `size`). Was der Generator gar nicht erst übersetzt, steht im folgenden Abschnitt. Der Editor kennt beides im Dialog „Toolchain Settings“
(Toolchain: Optimization, Strip symbols).

### Größe des Programms: was der Generator weglässt

Der Generator übersetzt nur, was vom Programm aus erreichbar ist: Funktionen werden erst beim ersten Aufruf nach Name und Argumentzahl angelegt, Klassen erst beim `new`
(`RegisterClass`), ein Aufruf über den Verteiler (`call_Name_n`) zieht die Methode dieses Namens aller *gemachten* Klassen nach; der C-Compiler wirft nicht erreichte
`static`-Funktionen weg. Die statischen Initialisierer aller Klassen laufen weiter am Anfang (wie in der VM). Was per Name erreichbar ist, hält Code fest:

* **Reflection** (Stufen nach den aufgerufenen `__refl_*`): Mitglieder per Name (`Reflect.Get/Set/Has`, Selektoren, Proben) halten die Felder und Property-Zugriffe der gemachten
  Klassen fest, `Reflect.Call` ihre Instanzmethoden, `Reflect.New` die Konstruktoren aller Klassen der Tabellen. Die Tabellen (`kRfClasses`, `Reflect.Classes()`, `Type.Named`)
  beschreiben die **erreichten** Klassen: gemacht, mit benutztem Code oder statischen Feldern, ihre Basisklassen, `[Keep]`. Vorher erzeugte ein einziger Aufruf von `Reflect.Has`
  (das `ui` tut das für Stile, Trigger und Bindungen) **alle** Klassen des Programms mit allen Konstruktoren und Methoden.
* **JSON**: Schreiben zieht nichts nach; Lesen die Klassen der deklarierten Typen (`Json.Deserialize<T>`, ihre Feldtypen, Unterklassen, Klassen eines Interfaces), ein Slot ohne
  Klasse (`any`, ein Typname als Text) alle; `[NotSerializable]` und `"serializable": false` (das Package `ui`) schließen Klassen aus ([JSON](../stdlib/json.md)).
* **`[Keep]`** (SPEC 5.12) vor einer Klasse behält sie mit allen Mitgliedern, vor einem Mitglied dieses und seine Klasse; nicht vererbt.
* Tabellen: Feldhelfer und Verteiler nur für die Namen, die erreicht werden; gleiche Zweige eines `switch` über die Klassen stehen einmal (`case 3: case 9: ...`); `fp_` (die Adresse
  eines Felds) nur für Namen, deren Adresse das Programm nimmt; die Reflection-Tabellen halten nur die eigenen Mitglieder jeder Klasse (die geerbten kommen zur Laufzeit aus der
  Basisklasse) und jede Namensliste einmal; die Proben-Tabellen ebenso.

**Stufen des Weglassens** (`"stripping"` in `fire.native.json` oder in den Einstellungen des Projekts, `--stripping` auf der Befehlszeile von `build`, `native` und `test`;
eine Zahl oder ein Name; die Befehlszeile geht vor das Projekt, das Projekt vor `fire.native.json`). Jede Stufe lässt weg, was die vorige behält, und mehr:

| `"stripping"` | was bleibt |
|---|---|
| `0` `"none"` | alles: jede Klasse mit allen Konstruktoren, Methoden (auch statischen) und Feldern - wie die VM. `--keep-all` sagt dasselbe; ein **Debug-Build** (`#debug`, `-m DEBUG`) hat immer Stufe 0 (der Debugger, ROADMAP Abschnitt 8). |
| `1` `"protected"` | jede Klasse und jeder öffentliche Member (Methoden, auch statische, Properties, Konstruktoren); ein privater oder geschützter Member nur, wenn das Programm ihn erreicht (ein geschützter, den eine Unterklasse benutzt, ist erreicht). Das ist die Regel einer vorgebauten Library (ihre Schnittstelle und was sie erreicht). |
| `2` `"methods"` | jede Klasse mit ihren Konstruktoren (`Reflect.New` macht jede), Feldern und Properties (Getter und Setter: Proben, `Reflect.Get/Set/Has` und JSON arbeiten an allem); eine Methode nur, wenn das Programm sie benutzt - die Reflection-Tabellen beschreiben sie dann auch nicht. |
| `3` `"all"` (Standard) | was das Programm erreicht: auch eine Klasse, die es nie macht oder benutzt, fehlt (das frühere `"reached"`). |

In jeder Stufe bleiben die Klassen und Member mit `[Keep]` und die **Namen, die das Programm als Literal an die Reflection gibt**: ruft eine Funktion eine Methode von
`Reflect`, `Type`, `Member`, `Selector` oder `Attribute` (`Reflect.New("Dog", [])`, `Type.Named("Dog")`, `Reflect.Call(o, "Bark", [])`, `Reflect.Get(o, "f")`,
`t.Find("Bark")`), behält der Generator jeden Text, den sie als Wert lädt und der eine Klasse nennt (wie `[Keep]`: mit allen Membern), oder der ein Name ist (die Member
dieses Namens der Klassen, deren Objekte es gibt: ihre Methoden mit Verteilern, ihre Felder und Properties). Auch `var n = "Bark"` vor dem Aufruf zählt; ein Text, der
erst zur Laufzeit entsteht, ein `"$type"` von JSON und ein Name aus einer Datei nicht. Die Erkennung ist bewusst grob (jeder Text der Funktion, nicht nur die Argumente
des Aufrufs): sie behält höchstens etwas mehr.

Wird zur Laufzeit etwas gesucht, das weggelassen ist, ist das ein klarer Fehler (`ReflectionException`) statt einer unbekannten Klasse:

* eine Klasse per Name (`Reflect.New`, `Type.Named`, `Type.Of(text)`; nur in Stufe 3): `Class 'Ghost' was left out of the build (stripping 3). Add [Keep] to 'Ghost' or use stripping <= 2.`
  (die Namen der weggelassenen Klassen stehen dafür in `kRfStrippedClasses`);
* ein Member per Name (`Reflect.Call`, auch ein Feld oder eine Property ohne Helfer): `Member 'Dog.Wag' was left out of the build (stripping 2). Add [Keep] to 'Dog.Wag' or use
  stripping <= 1.` - für einen privaten oder geschützten `... or use stripping 0.` (die Zeile `RfClass` einer Klasse nennt dafür ihre weggelassenen Methoden in `stripped`,
  mit der Stufe, die sie behält, vorne: `"1Wag"`).

`Type.Names()` listet die Klassen, die da sind, `Type.Of(o).Methods()` die Methoden, die da sind; `Reflect.Has(o, name)` ist für eine weggelassene Methode `false`.
Die VM behält alles - der Fehler kommt nur in einem nativen Build vor, dieselbe Meldung gibt es in der VM nicht.
`"trim"` (der frühere Schalter) bleibt als Alias: `"all"` ist 0, `"reflection"` (jede Klasse, sobald eine per Name gesucht wird) ist 1, `"reached"` ist 3; steht beides da,
gilt `"stripping"`. Mit `linkage` `"static"`/`"shared"` behält eine Library in jeder Stufe außer 0 ihre Schnittstelle (Stufe 1, siehe "Getrennte Übersetzung"); ein Member
einer Library per Name ist dann auch in Stufe 2 und 3 da.

**VM und nativ.** Die VM hat jede Klasse des gelinkten Programms; ob das Programm eine Klasse erreicht, weiß sie nicht (dafür bräuchte sie dieselbe Analyse, die der Generator
nebenbei beim Übersetzen macht - und die hängt an seinem Verteiler nach Namen; ein Weglassen in der VM ist Abschnitt 11.6 der Roadmap, offen). Im Standard unterscheiden sich
deshalb genau die Suchen mit einem Namen, der erst zur Laufzeit entsteht, nach etwas, das das Programm sonst nie erreicht (und JSON-`"$type"` außerhalb der deklarierten
Typen); alles, was an einem vorhandenen Objekt arbeitet, ist gleich. `[Keep]` oder eine kleinere Stufe machen die Ausgabe wieder gleich.
Getestet in `fire.Testing` ("Weglassen (stripping): ..." in allen vier Stufen gegen die VM - Literale, Laufzeitnamen, privat, `[Keep]`, Proben in 2 und 3, JSON, die
Aliase; `[Keep]` und JSON als Fälle "Native == VM"; mit `static` in `FIRE_TESTS_ONLY=linkage`).

**Die Stufen am minimalen `ui`-Programm** (Framebuffer, Window, `UI.Root`, ein `UI.Label`, `Run()`; gcc 13 `-std=c11 -O2 -c`, ohne ccache, 4 vCPUs):

| Stufe | C | Zeilen | `gcc -O2 -c` | Objektdatei (davon Code, `size`) |
|---|---:|---:|---:|---:|
| 0 `none` (wie `--keep-all`) | 8,75 MB | 240.700 | 149 s | 6,49 MB (3,46 MB) |
| 1 `protected` | 8,75 MB | 240.700 | 155 s | 6,49 MB (3,46 MB) |
| 2 `methods` | 7,31 MB | 199.700 | 149 s | 6,41 MB (3,46 MB) |
| 3 `all` (Standard) | 2,76 MB | 72.800 | 56 s | 2,74 MB (1,44 MB) |

Die Stufen 1 und 2 sind hier so groß wie 0 (Stufe 1 ergibt dieselbe Objektdatei): `ui` hat kaum privaten Code, den kein öffentlicher Member erreicht, und die
Konstruktoren der rund 140 Klassen des Packages ziehen fast ihren ganzen Code nach (Stufe 2 spart nur die Methoden, die keiner aufruft - gcc hatte die als nicht benutzte
`static`-Funktionen ohnehin verworfen). Für ein `ui`-Programm lohnen nur 3 und, zum Vergleich mit der VM, 0; 1 und 2 sind für kleine Programme ohne große Packages
gedacht, die Klassen per Name machen (der Testfall "Weglassen (stripping)": 990 KB / 990 KB / 495 KB / 303 KB C in den Stufen 0-3). Vor den Stufen (Stand 64b4ccc)
hatte Stufe 3 2,77 MB und `--keep-all` 8,75 MB.

**Messung** (gcc 13 `-std=c11 -O2 -c`, ohne ccache, derselbe Rechner, vorher = Stand vor Abschnitt 11):

| Programm | C vorher | C nachher | gcc -O2 vorher | gcc -O2 nachher |
|---|---|---|---|---|
| minimales `ui` (Framebuffer, Window, `UI.Root`, ein `UI.Label`, `Run()`) | 7,85 MB (220.000 Zeilen, 1430 Funktionen) | 2,29 MB (65.800 Zeilen, 363 Funktionen) | 140 s | 49 s |
| `graphics` + `windows` (Renderer, Rechteck, Linie, Text) | 417 KB | 412 KB | 5,2 s | 5,3 s |
| `scene3d` (der Testfall "Scene3D") | 1,27 MB | 1,23 MB | 17,3 s | 17,5 s |
| JSON (zwei Klassen, `Serialize` und `Deserialize<T>`) | 161 KB | 71 KB | 5,5 s | 3,8 s |
| Reflection (`Type.Of`, `Get/Set/Call/Has`) | 680 KB | 348 KB | 15,5 s | 9,2 s |

`graphics` und `scene3d` benutzen keine Reflection: dort war schon vorher nur das Erreichte übersetzt, der Rest ist echter Code und die C-Bridges. Mit Stufe 0 ist das
`ui`-Programm 8,1 MB groß (alles, auch was vorher ohne Reflection-Zugriff wegfiel).

Was im minimalen `ui`-Programm bleibt, ist im Wesentlichen echter Code: `UI.Root` bringt Ereignisse, Fokus, Popups, Ziehen und Ablegen mit (die Funktionen machen etwa die Hälfte
aus, die Feldhelfer ein Fünftel, die C-Bridges von `graphics` und `windows` ein Achtel). Ein Filter für den Verteiler nach Rapid Type Analysis (eine Methode nur, wenn eine Aufrufstelle
mit passendem statischen Typ sie erreichen kann) bringt hier wenig: 81 % der Aufrufstellen über den Verteiler kennen die Klasse des Empfängers schon, und die Methoden, die ein solcher
Filter höchstens weglassen könnte, sind 4 % des Funktionscodes (die meisten davon gehören Unterklassen von `UI.Element` und blieben) - er ist nicht eingebaut. Ebenso nicht die Natives der Bridges in `#if`-Abschnitten: gcc verwirft ihre nicht benutzten
`static`-Funktionen vor dem Optimieren (Gewinn nur bei TinyCC und an der Größe der Datei).

### Bauen: `build` zeigt auf nativ

`fire.Compiler build skript.fire [-o ziel] [--engine vm|native] [-t ziel] [--toolchain name] [--config datei] [--keep] [--stripping 0-3] [--keep-all]`: ohne `--engine` gilt das `engine` der Konfiguration (Standard `vm`: die
eigenständige Datei mit der VM, wie bisher). Mit `native` wird für das Ziel übersetzt (`NativeBuilder`: C++ erzeugen, Runtime und Plattformpaket daneben legen) und mit der Toolchain gebaut;
bei `files` ist `-o` ein Ordner. `fire.Compiler native ...` erzeugt weiter nur das C++ (samt Runtime und Plattformpaket des Ziels). Der Editor hat denselben Weg:
**Tools > Toolchain Settings...** (Seiten links: *Build* mit der Standard-Engine, *Target* mit Plattform/Includes/Defines/Einsprung, *Toolchain* mit Compiler und Argumenten; speichert `fire.native.json` neben dem Skript bzw. Projekt; auch erreichbar über *Project Properties > Native & Toolchain*), **Run > Build Native...**,
und **Build** folgt dem `engine` der Konfiguration.

### Ausführen und Debuggen aus spark (ROADMAP 8.7)

Neben der Modus-Box steht die **Engine-Box** (*VM* / *Native*, auch **Run > Engine: VM / Native**). Die Wahl gilt für das Projekt des aktiven Dokuments
(`settings.engine`, gespeichert in der `.fireproj`) bzw. für einzelne Skripte (spark-Einstellungen); ohne Wahl gilt das `engine` von `fire.native.json`, sonst die VM.
Mit *Native* bauen F5, F10, F11 und *Run to End* das Programm für das gewählte Ziel (es muss auf diesem Rechner laufen: Plattform `posix` bzw. `windows`, eine
Toolchain, die ein Programm macht) in den Ordner `bin/<ziel>-<modus>` des Projekts (ein Skript: neben sich, ungespeichert: im Temp-Ordner) und starten es:

* **Debug**: gebaut mit `-O0 -g` (MSVC `/Od /Zi`), Symbole bleiben (`NativeLaunch.DebugToolchain`). spark lauscht auf `127.0.0.1` an einem freien Port, startet das
  Programm mit `FIRE_DEBUG=tcp:127.0.0.1:PORT;token=T`, nimmt **eine** Verbindung an, deren `hello` das Token trägt, und wartet, bis der Hauptthread am Eintritt steht
  (`NativeLaunch.LaunchDebug`, `NativeDebugListener`). Danach ist es eine gewöhnliche Debug-Sitzung über `NativeDebugTarget` (`src/fire.Compiler/Debugging`,
  Protokoll: [Debug-Protokoll nativer Programme](NATIVE_DEBUG_PROTOCOL.md)): Haltepunkte, Schritte, Pause, Threads, Scope-Ansicht mit *Set Value...*, unbehandelte Ausnahmen mit *Ignore Error and
  Continue*. Das Stack-Panel (der Operandenstapel der VM) ist ausgeblendet. Passen die Hashes der Quellen im `hello` nicht zu den Texten des Editors, steht eine Warnung
  im Output-Panel. *Stop* schickt `terminate` und beendet den Prozess.
* **Release/Performance**: nicht debugbar (Entscheidung 8.4) - das Programm läuft einfach; stdout und stderr kommen zeilenweise ins Output-Panel, das Ende mit
  Exitcode in die Statusleiste. *Stop* beendet den Prozess.

Auf dem Desktop läuft die Ausgabe des Programms über seinen Prozess, nicht über die Debug-Verbindung (ein ESP32: siehe unten). Getestet ohne die Runtime gegen einen Schein-Agenten in C#
(`FIRE_TESTS_ONLY=debugger`, Block "Natives Debug-Ziel"), dazu Start eines Prozesses mit `FIRE_DEBUG` (ein Programm in bash, das das Protokoll spricht) und - sobald die
Runtime den Debug-Agenten hat - Ende-zu-Ende mit einem echten Debug-Build gegen die VM (Haltepunkt in einer Methode, Tiefe, Locals, `this`, `set`, Step Over bis
zur unbehandelten Ausnahme mit Meldung und Ort, `resume`, die Ausgabe des Prozesses); ohne Agenten in der Runtime wird dieser Teil übersprungen.

#### Fortschritt und Abbrechen (das Build-Fenster von spark)

Jeder Bau, den spark startet (F5 mit der VM, wenn übersetzt wird; Build Native; natives Run/Debug; ESP32 mit `idf.py build flash`; Build; Pack/Install Library), ist ein
**Bau-Auftrag**: dauert er länger als 0,5 s, erscheint ein Fenster mit der Phase, einem Fortschrittsbalken und dem Log (die Befehle der Compiler und ihre Ausgabe, während
sie laufen), dazu **Hide** (Escape; die Statusleiste zeigt den Fortschritt, ein Klick holt das Fenster zurück) und **Cancel** (Ctrl+Break, Ctrl+Pause, Shift+F5,
*Run > Cancel Build*). Erfolg schließt das Fenster (das Log bleibt im Build-Log), ein Fehler lässt es mit der Meldung offen, Abbrechen schreibt *Build cancelled* ins
Output-Panel.

Die Schnittstelle dafür ist **`BuildMonitor`** (`src/fire.Native/BuildMonitor.cs`), kein Auswerten von Text:

* `NativeBuilder.Build(..., monitor)`, `NativeBuilder.BuildSafe(..., monitor)` und `ProjectBuilder.PackLibrary(plan, ordner, monitor)` nehmen einen Monitor
  (`new BuildMonitor(token, progress, log)`); innen ist er `BuildMonitor.Current` (ein `AsyncLocal`, das in die parallelen Schritte mitgeht), so dass der
  Prozessstarter (`NativeBuilder.Run`), der Linker und die Generatoren keinen weiteren Parameter brauchen. `DebugSession.CompileAsync` (spark) übersetzt so für die VM.
* **Fortschritt** (`IProgress<BuildProgress>`): `BuildProgress(Phase, Text, Done, Total)` mit `BuildPhase` `CompileFire` (der Linker meldet seine vier Schritte:
  Parsen, Namen, Typen, Bytecode), `PackageNatives` (die Natives von Packages und Bibliotheksprojekten für die VM, `PackageLibrary.Ensure`: *Building natives of
  package X*, bei mehreren je Paket gezählt - `PackageLibrary.CountBuilds` zählt die, die nicht gebaut sind, `n-1/N` beim Beginn, `n/N` am Ende; beim Start eines
  Programms in der VM, beim Packen und beim Bytecode eines Plugins), `GenerateC`, `CompileC` (linkage `static`/`shared`: `BeginCount` meldet 0/N, jede fertige Library eins mehr - N sind die Module,
  die nicht im Cache sind; linkage `source`: das C des Programms), `Link`, `Flash` (`Esp32Launch.BuildAndFlash(..., cancel, progress)`: die Schritte von ninja `[n/m]`
  und von esptool `(n %)` - hier gibt es nur die Ausgabe des Werkzeugs) und `Pack`. `Total` 0 heißt: ohne Zahl. Die Meldungen kommen in der Reihenfolge der Phasen
  (fällt ein Bau auf linkage `source` zurück, beginnt er neu bei `CompileFire`).
* **Log**: jeder Befehl (`gcc ...`) und jede Zeile seiner Ausgabe, sobald sie kommt; das `Log` des Ergebnisses ist wie bisher.
* **Abbrechen**: das Token des Monitors. Ein laufendes Werkzeug endet mit seinem ganzen Prozessbaum (`BuildProcess.Terminate`: erst SIGTERM an alle - gcc und ninja
  löschen dann, was sie gerade schrieben -, nach 2 s SIGKILL, auch für Kinder, deren Eltern schon weg sind; Windows: `Kill(entireProcessTree)`); die Teile im Prozess
  halten an ihrem nächsten Punkt (`BuildMonitor.Step`/`Checkpoint`) mit `OperationCanceledException`. `BuildSafe` gibt dann ein Ergebnis mit `Cancelled` zurück
  und löscht das Programm, wenn dieser Bau es schon zu schreiben begonnen hatte. Im Cache bleibt keine halbe Library: eine Library entsteht in einer temporären Datei,
  die ein abgebrochener oder fehlgeschlagener Bau löscht, und die Schnittstelle wird erst nach ihr geschrieben (siehe "Cache und Schnittstelle").
  Ebenso die Library der Natives eines Packages: sie entsteht in einem eigenen Ordner (`fire-pkgbuild-*`, danach gelöscht) und kommt über eine `.partial`-Datei mit einem
  Umbenennen in den Cache; ein Abbruch merkt sich keinen Fehlschlag (`.failed`), der nächste Lauf baut sie neu.

Getestet ohne Fenster (`FIRE_TESTS_ONLY=linkage`, `FIRE_TESTS_FILTER=cancel`): ein Bau mit linkage `static` und leerem Cache wird abgebrochen, während gcc die Module
übersetzt - gcc/cc1 laufen binnen Sekunden nicht mehr, kein temporäres File und keine Library ohne Schnittstelle im Cache, kein Programm; der nächste Bau in denselben
Cache gelingt, seine Phasen kommen in Reihenfolge, `CompileC` zählt 0..N, das Log hat die Befehle. `FIRE_TESTS_ONLY=projects`: die Phasen von Pack, ein abgebrochenes
Pack hinterlässt kein `.fpk`; ein Programm in der VM mit zwei Bibliotheken mit Natives in C: abgebrochen, während gcc die erste übersetzt - gcc läuft danach nicht mehr,
`OperationCanceledException`, keine Library (auch kein `.partial`/`.failed`); der nächste Lauf meldet `PackageNatives` nach `CompileFire` mit 1/2 und 2/2 und hat die zwei
Befehle im Log, ein dritter keine solche Phase mehr. **Nicht geprüft**: das Fenster selbst und die Statusleiste (spark ist hier nicht zu klicken), das Abbrechen von `idf.py` auf einem
echten ESP-IDF und Windows.

#### Ziel `esp32`: Run und Debug auf dem Board (ROADMAP 8.6)

Mit dem Ziel `esp32` (Build-Leiste bzw. `settings.target`) machen F5/F10/F11 aus dem Programm ein **ESP-IDF-Projekt** in `bin/esp32-<modus>` (die Komponente, die
`build -t esp32` schreibt, in `main/`, daneben die `CMakeLists.txt` des Projekts, `Esp32Launch.WriteProject`) und rufen `idf.py [-p PORT] build flash` (mit
`export.sh`/`export.bat` von ESP-IDF; der Ordner kommt aus `IDF_PATH` bzw. `idfPath` der spark-Einstellungen - fehlt er, sagt spark das; die Ausgabe steht im
Build-Log, der erste Bau von ESP-IDF dauert Minuten). Danach öffnet spark die **serielle Schnittstelle** selbst (System.IO.Ports, `SerialFramedTransport`) und
startet das Board neu (RTS, wie `idf.py monitor`):

* **Port**: `"port"` in den Einstellungen des Projekts (`settings.port`) oder im Ziel von `fire.native.json` (`"targets": {"esp32": {"port": "/dev/ttyUSB0"}}`),
  sonst `ESPPORT`, sonst die einzige USB-Seriell-Schnittstelle des Rechners (Linux `/dev/ttyUSB*`, `/dev/ttyACM*`; macOS `/dev/cu.usbserial*`, ...; Windows die
  COM-Ports), sonst die, die esptool beim Flashen nimmt und nennt (`Serial port ...`). **Baud**: `"baud"` (Standard 115200).
* **In spark einstellen**: *Project Properties > Native & Toolchain > Board (ESP32)* (`settings.port`/`settings.baud`) und *Tools > Toolchain Settings > Target > Board*
  (`port`/`baud` des Ziels in `fire.native.json`): der Port aus einer Liste der seriellen Schnittstellen des Rechners (`Esp32Launch.SerialPorts`: die USB-Seriellen von
  `CandidatePorts` zuerst, dann die übrigen von System.IO.Ports; *Refresh* sucht neu) oder getippt, *auto*/leer schreibt keinen Schlüssel; die Baudrate aus den üblichen
  (`Esp32Launch.CommonBauds`, 115200 zuerst) oder getippt (keine Zahl: markiert, nicht übernommen). Der Ordner von ESP-IDF unter *Tools > Toolchain Settings > ESP32 Board*
  (`idfPath` in `spark-settings.json`, leer: `IDF_PATH`) mit Ordnerauswahl und der Prüfung `Esp32Launch.CheckIdf` (*idf.py found* - `export.sh`/`export.bat` und
  `tools/idf.py` sind da - oder warum nicht); die Projekteigenschaften zeigen diese Prüfung mit an.
* **Run** (Release/Performance): die Konsole des Boards im Output-Panel (Rahmen eines Debug-Builds werden ausgefiltert); unter dem Output-Panel ist eine
  **Eingabezeile**, deren Zeilen das Board als Standardeingabe bekommt. *Stop* schließt die Schnittstelle.
* **Debug**: ein Debug-Build (`FIRE_DEBUG_TRANSPORT_UART`, Hauptstack der Task 32 KB statt 16 KB) sagt beim Start `hello` über die Konsolen-UART und wartet 3 s auf
  den Debugger; spark wartet auf das `hello` (das Boot-Log davor kommt ins Output-Panel), schickt sofort den ersten Befehl und vergleicht die Hashes der Quellen
  (passen sie nicht: "das Board führt ein anderes Programm aus"). Dann ist es eine gewöhnliche Sitzung über `NativeDebugTarget.OpenFramed` - Haltepunkte,
  Schritte, Threads, Scopes, *Set Value...*, unbehandelte Ausnahmen; die Konsole zwischen den Rahmen und die Eingabezeile wie bei Run. *Stop* schickt
  `terminate` (das Board startet neu).

Getestet: `SerialFramedTransport` gegen ein Schein-Board über eine serielle Leitung im Speicher (`FIRE_TESTS_ONLY=debugger`: Boot-Log, verfälschter Rahmen, Rahmen
mitten in einer Konsolenzeile, Threads mit ihren eigenen Globals, Eingabe, ein zweites `hello`, kein `hello`), und Ende-zu-Ende ein Debug-Build auf
**QEMU** (`qemu-system-xtensa` von ESP-IDF, `-serial tcp:...,server=on,wait=on`: die Konsolen-UART ist ein Socket; Fälle "Native-Debugger: ESP32 (QEMU, UART)",
mit `IDF_PATH`, Projekt in `~/.cache/fire-tests/esp32-dbg`): das Boot-Log vor dem `hello`, ein Plan aus Haltepunkt, Step Over/Into/Out, Scopes, `this`,
Globals, unbehandelter Ausnahme mit `resume` gegen die VM, die Eingabe über die UART; dasselbe Programm ohne Debugger läuft nach 3 s allein, liest seine
Eingabe von der UART und endet an seiner Ausnahme. Ohne Fenster (`FIRE_TESTS_ONLY=projects`, "Board: ..."): `port`/`baud` des Projekts und eines Ziels und `idfPath`
bleiben beim Speichern und Laden (auto schreibt keinen Schlüssel), `CheckIdf` (kein Ordner, nur `export.sh`, mit `tools/idf.py`), die Liste der Ports und die Auswahl
von Port und Baud. **Nicht geprüft**: ein echtes Board (USB-Seriell-Chip, RTS-Reset, Port-Erkennung, `idf.py flash`) und die Klicks in spark (auch die neuen Felder:
die Dialoge sind hier nicht zu bedienen).

Getestet wird per **Differential-Test** (`fire.Testing`, Block "Native-Backend"): jeder Fall läuft in der VM und als erzeugtes
C++ (g++/clang++, mit `-Wall -Wextra`, ohne Warnung), die Ausgabe muss gleich sein.

## Roadmap

1. **Sprachumfang**: Objekte/Felder/Methoden (Layout pro Klasse statt Dictionary), Strings, Arrays, Lambdas (Closure-Conversion),
   Einheiten-Algebra (Tabelle zur Übersetzungszeit, Konvertierungsfaktoren als Konstanten).
2. **Ownership**: Scopes, die besitzende Objekte halten, behalten eine Laufzeit-Scope-Kette (für die Destruktor-Kaskade); alle anderen
   bleiben aufgelöst.
3. ~~**Ausnahmen mit Resume**~~ - umgesetzt, siehe "Ausnahmen".
4. ~~**Threads, `sync`, Safe-Points** (`leave`/`terminate`)~~ - umgesetzt (`std::thread` oder FreeRTOS-Tasks + GIL), siehe "Threads" und "Plattformschicht".
5. **Bridges** (Variante A): eine C++-Implementierung mit C-ABI, die auch der C#-Editor per P/Invoke nutzt - IO und Time zuerst,
   dann Graphics (mit der Anzeige über SDL2/SDL3), zuletzt Devices.
6. **Optimierungen**: Typinferenz und Einheiten-Folding, Devirtualisierung (geschlossene Welt), Inlining, Scope-Elision.
7. **Zielprofil und ESP32**: Ziele (`--target`), `#if`, Stack-Analyse, Plattformpakete, ESP-IDF-Komponente.
8. **Editor**: `Run -> Build Native`, Toolchain-Erkennung.

## Umschalter und Ziele

Alles, was sich je nach Ziel ändert, hängt an **einer** Beschreibung des Ziels (Betriebssystem/Board, Float-Genauigkeit,
Stackgröße, verfügbare Bridges), die Übersetzer, VM und Editor teilen.

### Zielprofil - umgesetzt (Basis)

`TargetProfile` (`src/fire/Runtime/TargetProfile.cs`) beschreibt ein Ziel an einer Stelle; Linker, Generator und später der
Präprozessor lesen dasselbe:

| Feld | Bedeutung | `esp32` |
|---|---|---|
| `Name` | `--target`/`-t` auf der Befehlszeile | `esp32` |
| `Symbols` | Symbole für `#if` (SPEC 8.1.7) | `esp32`, `freertos` |
| `FloatWidth` | Standard-Genauigkeit von `float` | 32 |
| `DefaultStackBytes` | Standard-Stack eines `fire`-Threads | 8192 |
| `Imports` | welche `#import`-Bibliotheken es dort gibt | print, io, devices, time, reflection, linq (kein `graphics`/`windows`/`ui`) |
| `HalPackage` | Plattformpaket der C++-Runtime | `esp32` |
| `IsEmbedded` | kein Betriebssystem-Prozess (der Einsprung steht in `Native.Entry`: `app_main`) | ja |

Eingebaut: `windows`, `linux`, `macos`, `esp32`, `freertos`, `wasm`; `TargetProfile.Host` ist das Ziel der VM im Editor.

* **Rangfolge der Float-Genauigkeit:** `-f` > `#floatwidth` im Programm > Standard des Ziels > 64.
* **Rangfolge von `globalJobs`** (wann das Hauptprogramm seine Warteschlange abarbeitet, THREADING_DESIGN.md Abschnitt 7) genauso: `--global-jobs` >
  `globalJobs` des Projekts > `#globaljobs`/`#nosync` im Programm > `"globalJobs"` in fire.native.json > `safepoints`. Der Compiler schreibt den Modus als
  `SetAutoSync` an den Programmanfang; der Generator macht daraus `g_globalJobs = n` (und nimmt `ATTN_SECT` aus der Maske des Hauptprogramms, damit der
  Rücksprung einer Schleife in `sleep`/`sync` nichts prüft). Sichere Punkte nativ wie in der VM: `pollLoop()` am Rücksprung (Warteschlange nur in
  `safepoints`), `pollSignals()` am Funktionsanfang und nach einer wartenden Funktion des Hosts (nur Signale), `mainPoll(true)` in `Sleep` (auch
  `Sleep(0)`) und in den Wartefunktionen der Geräte (`blockUntilServing`); `g_noSyncDepth` sperrt sie in Destruktoren, Host-Diensten und
  `catch threads`/`catch terminate`, `g_jobDepth` in einem laufenden Auftrag.
* `Linker.CompileAndLink(..., target:)` lehnt `#import` einer Bibliothek ab, die es auf dem Ziel nicht gibt (Fehler beim Übersetzen, nicht
  erst auf dem Gerät).
* Der Generator schreibt `FIRE_TARGET`, `FIRE_TARGET_<NAME>`, `FIRE_HAL_<PAKET>` und `FIRE_DEFAULT_STACK_BYTES` in die Datei; für
  eingebettete Ziele den Einstieg `extern "C" void app_main(void)`. Die Plattformschicht der Runtime wählt später über diese Defines.
* Befehlszeile: `fire.Compiler native skript.fire -t esp32 -o main.cpp`.

Zusätzliche Ziele (eigene Boards) stehen in der Zielkonfiguration `fire.native.json` (siehe "Zielkonfiguration"); `TargetProfile.Native` (`NativeTarget`) trägt dort, was der Bau braucht
(Plattformpaket, Includes, Defines, Einsprung, Compiler-Argumente). Eingebaut ist auch `freertos` (jedes Board mit FreeRTOS; der Einsprung `fire_start` wird vom Board aus einem Task aufgerufen).

### Float-Genauigkeit - umgesetzt

`#floatwidth 32|64` (SPEC 8.2.1), überschreibbar mit `-f 32|64`. **Die VM nutzt den Schalter genauso**: sie rundet jedes Float-Ergebnis,
jede Float-Konstante und jede Ganzzahl-nach-Float-Umwandlung auf 32 Bit und druckt die kürzeste 32-Bit-Darstellung. Für `+ - * /` ist
"in `double` rechnen und auf `float` runden" bit-identisch zur echten `float`-Rechnung (ein `double` hat mehr als 2p+2 Bit), die
Ergebnisse auf dem Desktop und auf dem ESP32 sind also gleich. Nativ ist `Real` dann `float` (`FIRE_FLOAT32`), auf dem ESP32 die
Hardware-FPU. Der Differential-Test deckt beide Genauigkeiten ab. Für das Ziel ESP32 ist 32 der Vorgabewert (kommt mit dem Zielprofil).

### Stackgröße der Threads

**Vorab ermitteln geht, solange das Programm nicht rekursiv ist** - sonst nicht:

* Der Generator kennt den Aufrufgraphen (geschlossene Welt: alle Funktionen, Methoden, Lambdas stehen fest). Für jeden Thread-Einstieg
  (`main`, jeder `fire`-Block) ist der Stackbedarf der **längste Pfad** durch diesen Graphen plus die Summe der Rahmengrößen darauf.
* Rahmengrößen: als sichere Obergrenze aus den Variablen der Funktion (16 Byte je `Value` plus Aufrufrahmen); genauer aus den
  Compiler-Angaben (`-fstack-usage` bei gcc/clang, auch im ESP-IDF), dann ist ein zweiter Durchlauf nötig.
* Aufrufe über Lambdas und virtuelle Methoden zählen konservativ alle in Frage kommenden Ziele (gleiche Parameterzahl bzw. Name).
* **Rekursion** (ein Zyklus im Graphen) hat keine statische Grenze. Dann gilt die vom Nutzer angegebene Tiefe oder der Standardwert.

Vorschlag für die Einstellung (Standard `auto`):

```
#stacksize auto          // Analyse; wo sie nicht geht (Rekursion): Standardwert, mit Warnung
#stacksize 6144          // feste Größe in Byte für alle Threads
#recursion 200           // geschätzte maximale Rekursionstiefe, damit "auto" auch rekursive Programme abdeckt
```

und `--stack auto|<Byte>`, `--default-stack <Byte>` auf der Befehlszeile (wie `-f` überschreibt die Befehlszeile die Direktive). Ein
Prolog-Test (`sp < Grenze` -> fire-Fehler statt stillem Überschreiben) macht eine zu knappe Größe diagnostizierbar; FreeRTOS liefert
mit `uxTaskGetStackHighWaterMark` die tatsächliche Nutzung, die ein Debug-Build ausgibt, damit man die Größe nachjustieren kann.

### Code abhängig vom Ziel

Zwei Ebenen, die zusammenarbeiten:

1. **In fire (Quelltext):** bedingte Übersetzung im Präprozessor,

   ```
   #if windows
       var port = "serial:COM3"
   #elif esp32
       var port = "uart:1"
   #else
       var port = "serial:/dev/ttyUSB0"
   #endif
   ```

   Die Symbole setzt das Ziel: Betriebssystem/Board (`windows`, `linux`, `macos`, `posix`, `esp32`, `freertos`), Engine (`vm`, `native`), `float32`;
   dazu `#define NAME` und `-D NAME`. Weil das Textersetzung vor dem Parser ist, werden nicht gewählte Zweige nie gelesen - sie
   dürfen auch Bridges benutzen, die es auf dem Ziel gar nicht gibt. Die VM setzt die Symbole für das Host-System selbst, damit
   dasselbe Skript im Editor und als Binary dasselbe tut (SPEC 8.1.7). Umgesetzt in `Conditional.cs` (Ausdrücke, `#if`-Stapel pro Datei)
   und `Preprocessor`; die Symbolmenge hängt an der `DirectiveRegistry`, `Linker.Engine`/`Linker.Defines` und `-D` füllen sie. *Noch offen:* der Editor
   graut nicht gewählte Zweige aus.
2. **In C++ (Plattformschicht):** siehe nächster Abschnitt.


### Bridges: ein C++-Interface, plattformabhängig angesteckt (Variante A)

Schichten, von oben nach unten:

```
fire-Programm  ->  Natives der Bridges (IO, Time, Devices, Graphics, später Hardware)
                     |  reine C++-Logik: Handles, IoPolicy, Paketprotokoll, Zeichenfunktionen
                     v
                   HAL-Schnittstellen  fire::hal::*   (abstrakte Klassen/Funktionstabellen, kein OS-Header)
                     |
        +------------+-------------+-----------------+
        v            v             v                 v
   platform/posix  platform/windows  platform/esp32   platform/mock (Tests)
                                  (SDL3 für Fenster und Eingabe auf dem Desktop)
```

* Die **Bridges** enthalten die Sprachanbindung und alles Plattformunabhängige; sie sehen nur die HAL-Schnittstellen.
* **HAL-Schnittstellen** (je eine kleine, ausnahmefreie Klasse, Fehler über Statuswerte): `Clock`/`Sleep`, `FileSystem`, `Console`
  (stdin/stdout/stderr), `SerialPort` und `SerialEnumerator` (Portnamen sind Sache der Plattform), `Thread`/`Mutex`/`Signal`
  (`std::thread` oder FreeRTOS-Tasks), `Window`/`Input`/`Framebuffer`, auf dem ESP32 zusätzlich `Gpio`/`I2c`/`Spi`.
* **Plattformpakete** implementieren sie und werden beim Bauen ausgewählt (CMake-Option bzw. `--target`); dadurch kostet die Schicht auf
  dem ESP32 nichts zur Laufzeit. Das heutige `loopback`-Gerät wird zur Mock-Plattform, mit der sich die Bridges ohne Hardware testen
  lassen.
* **C-ABI-Hülle** (`fire_bridge_*.h`) über den Bridges: der C#-Editor bindet dieselben Bibliotheken per P/Invoke an. Es gibt eine
  Implementierung, die C#-Bridges werden dünne Wrapper.

Reihenfolge: IO und Time (Dateisystem und Uhr), dann Devices, dann Graphics.

## Getrennte Übersetzung: `linkage` (ROADMAP 9)

Ein natives Programm entsteht normalerweise als **eine** C-Datei (`linkage` `"source"`, der Standard, Entscheidung 9.1): der Generator sieht das ganze Programm
(eine geschlossene Welt) und lässt weg, was es nicht erreicht. Mit `"linkage": "static"` (in `fire.native.json`, in den Einstellungen des Projekts oder
`--linkage static` auf der Befehlszeile) wird jede **Library** des Programms - das Prelude (`core`), jeder Import (`json`, `ui`, ein Package), jede Library des
Projekts - zu einer **statischen Bibliothek** (`.a`, mit msvc `.lib`), einmal gebaut und in einem Cache des Benutzers abgelegt; das Programm selbst wird bei jedem Bau
übersetzt und mit ihnen gebunden. Zwei Programme, die dieselben Libraries mit denselben Schaltern benutzen, teilen sich ihre Bibliotheken.

### Module

Die Quellen des gelinkten Programms tragen ihr **Modul** (`LinkedProgram.SourceModules`: `core` für das Prelude, der Import-Schlüssel für das Prelude eines Imports,
der Import-Name für die Dateien einer Library des Projekts, `null` für das Programm), dazu die Module, die ein Modul braucht (`ModuleDeps`: seine `#import`s, die
Referenzen einer Library). Ein Modul gehört zu der Quelle, in der sein Code steht: die Methoden einer `class extends List` von `linq` gehören zu `linq`, nicht zu `core`
(der Compiler merkt sich dafür die Quelle des Members, nicht die der Klasse - auch der Debugger zeigt sie jetzt in der richtigen Datei).

* Eine **Library** wird von einem Generator erzeugt, der nur ihre Klassen und die der Module sieht, die sie braucht (`LibraryView`). Sie behält, was andere Module
  benutzen können - die öffentlichen und geschützten Member ihrer Klassen (ihre Schnittstelle ist die Wurzel dessen, was bleibt - §11; `protected` zählt dazu, eine
  Klasse eines anderen Moduls kann davon erben) -, ein privater Member bleibt nur, wenn ihr eigener Code ihn erreicht (mit Stufe 0, in Debug-Builds und mit
  `[Keep]` alle). Das ist Stufe 1 des Weglassens, in jeder Stufe außer 0: eine Library wird einmal für alle Programme gebaut (ihr Schlüssel im Cache unterscheidet nur 0
  und den Rest); das Programm selbst lässt weg wie mit `source`. Sie nummeriert ihre Klassen selbst (nach Namen sortiert) und schreibt ihre Ids als `fire_mb_<modul> + k`. Member anderer Module ruft sie über
  **exportierte Namen** (`fx_<modul>_<Klasse>__<Member>_<i>`, aus dem Namen des Members abgeleitet - jedes Modul kommt auf denselben Namen). Statische Felder
  heißen `SFx_<modul>_<Klasse>_<Feld>`; das Modul der Klasse definiert sie.
* **Jedes Modul bedient seine eigenen Klassen** (Phase B2): seine Verteiler (`<modul>_call_X_n`), Feld-Helfer (`gf_x`/`sf_x`/`fp_x`), `is of` (`isof_X`), Operatoren,
  Klassen-Wertungen von Überladungen (`ovd_X`) und Typargumente von Objekten (`targOf_C_k`) erzeugt es für die Aufrufe seines eigenen Codes, mit einem `switch` nur über
  seine eigenen Klassen (eine Library zählt sie ab 0: `cls - fire_mb_M`; die Klassen anderer Module, die es mit Membern erweitert - `class extends List` von `linq` -
  hinter ihnen). Ein Objekt einer Klasse, die das Modul nicht kennt - einer Library, die es braucht, des Programms (auch eine Klasse des Programms, die von einer Klasse
  der Library erbt), später eines Plugins -, geht über die **Runtime**: jedes Modul schreibt für jede seiner Klassen eine Tabelle der Member, die sie selbst deklariert
  (`FireMembers` in fire_rt.h: Methoden nach Name und Zahl der Argumente, Felder nach Name - sortiert, binär gesucht), das Programm hängt sie in die
  Klassen-Deskriptoren (die Klasse zuerst, dann ihre Basen; zu einer Klasse auch die Tabellen der Module, die sie erweitern), dazu die Namen ihrer Kette und Interfaces
  und eine Funktion für ihre Typargumente. `fireFindMethod`/`fireGetField`/`fireSetField`/`fireIsOf`/`fireClassScore`/`fireTArgOf` gehen sie durch. Eine Zeile einer
  Tabelle ruft den Verteiler ihres Moduls für diese Klasse (`disp_X_n(k, ...)`): Überladungen, Defaults und Zugriffsprüfungen gelten wie bei eigenen Aufrufen
  (die Prüfung von `protected` fragt die Kette der Klasse des Aufrufers zur Laufzeit). Fehlt der Member, kommen die Fehler der VM (eine Klasse mit dem Member mit
  anderen Zahlen von Argumenten: ArgumentException mit ihrer Liste).
* Das **Programm** kennt als einziges Modul alle Klassen (eine geschlossene Welt): es ruft Member von Libraries direkt, wo der Typ es sagt (Devirtualisierung mit dem
  Wissen aller Klassen), erzeugt aber Verteiler und Feld-Helfer nur für seine eigenen Klassen. Es definiert die Klassen-Deskriptoren (A0, mit den Spalten oben), die
  JSON-Tabellen, die Liste der Reflection-Zeilen und die Debug-Tabellen. Die Tabellen seiner Klassen nennen nur die Member, die ein Modul beim Namen ruft (die **Needs**
  der Libraries: ihre Verteiler und Feldnamen; mit Reflect.Get/Call alle). Die Klassen, von denen eine Library Objekte anlegt, stehen in ihren Needs.
* **Reflection** je Modul: die Zeilen der Reflection-Tabellen (`RfClass` mit Membern und Attributen) und Reflect.New einer Klasse (`fire_new_<modul>_<Klasse>`) stehen
  in der Datei ihres Moduls; das Programm listet die Klassen, die es erreicht. Reflect.Get/Set/Call gehen über die Tabellen der Member (`fireGetField`, ...); Probes
  sehen die Setter jedes Moduls selbst nach, Reflect.Set auf ein Objekt mit Probes schreibt über den Namen (`probedSet` ohne Rohfunktion).
* **Zahlen, die erst das Programm festlegt**, liest eine Library aus Tabellen des Programms: Einheiten (`fire_units_<m>[k]`), Enums (`fire_enums_<m>[k]`), die Typen
  von `catch` (`fire_exct_<m>`, `HandlerInfo.typeMap`), die Quellen für den Debugger (`fire_dbgsrc_<m>[k]`, k ist die Stelle unter den Quellen des Moduls),
  die Basis ihrer Klassen-Ids (`fire_mb_<m>`). `FIRE_NDIMS` ist mit `static` fest 16.
* **Natives in C** (Packages in C, die Bridges von graphics und windows) stehen in der Datei des Moduls ihres Imports, wenn sie für sich stehen: ihr C ruft keine
  Funktion der Natives eines anderen Imports des Programms und keiner ruft ihre (hash, compression, io, time; nicht graphics und windows zusammen - die Bridge von
  windows benutzt die Framebuffer von graphics -, nicht scene3d). Ihr Zustand gibt es so einmal; jedes andere Modul und das Programm rufen sie über die
  `fire_cn_<funktion>` ihres Moduls. Die anderen stehen in der Datei des Programms (die Libraries rufen sie über `fire_cn_` des Programms); ob sie in der Datei ihres
  Moduls stehen, ist Teil seines Schlüssels. Die **JSON-Engine** (`FIRE_JSON_IMPLEMENTATION`) steht in der Datei des Moduls, das sie ruft (`json`). Die Natives in
  C++ bleiben in `fire_natives.cpp` (die Thunks der Libraries kommen dazu).
* **Schalter des Programms** (Ausnahmen, Threads, Reflection, Probes, Zeiger mit Grenzen, `try x.Take...`, JSON, ...) gelten für alle Module: jedes Modul wird mit
  der Vereinigung aller Schalter erzeugt (Fixpunkt über die Module); für jede Menge von Schaltern gibt es eine eigene Variante im Cache (Entscheidung 9.4). Was ein
  Schalter im Programm kostet, kostet er nur noch dort: die Reflection einer Library (`linq` benutzt sie) erzeugt im Programm keine Helfer mehr für die Klassen der
  Libraries.
* Kein Inlining über Modulgrenzen; eine Library ruft auch ihre eigenen Klassen über ihre Verteiler (eine Klasse eines anderen Moduls kann überschreiben). Eine LTO gibt
  es nicht (Entscheidung 9.5: optional, nicht umgesetzt). Die Bibliotheken werden mit einer Section je Funktion übersetzt (`-ffunction-sections -fdata-sections`),
  das Programm mit `--gc-sections` gebunden: der Linker lässt weg, was nichts erreicht.
* Ein Member, den das Ziel nicht übersetzen kann (fire-Threads auf wasm), wird in einer Library zu einer Funktion, die beim Aufruf mit dem Grund endet (mit `source`
  wäre er nicht erreicht worden).

### Cache und Schnittstelle

Der Cache liegt unter dem Ordner für Anwendungsdaten des Benutzers: `~/.local/share/fire/cache/native/<ziel>/<schlüssel>/` (Windows `%LOCALAPPDATA%\fire\cache\native`,
macOS `~/Library/Application Support/fire/cache/native`; `FIRE_NATIVE_CACHE` verlegt ihn). Der **Schlüssel** ist der SHA-256 über alles, wovon das C einer Library
abhängt: die Version des Generators (MVID von fire.Native, damit auch die Runtime), die Toolchain (Art, Compiler mit Größe und Zeit, C-Standard, Optimierung,
Argumente), das Ziel, Modus und Float-Breite, die Texte der Quellen des Moduls, die **ABI-Hashes** der Module, die es braucht, ihre Natives in C und die Schalter.
In einem Ordner liegen `<modul>.c` (für gdb und `#line`), `fire_<modul>.a` und die **Schnittstelle** `<modul>.fireiface` (JSON: `Format` - die Version des Formats,
heute 1 -, `Abi` - der Hash der Teile, die andere Module sehen: Klassen, Basis, Interfaces, Felder mit Layout, Art, Einheit, Typ und Zugriff, Member mit Parametern,
Typen, Zugriff und Defaults -, `Key`, die Schalter, die Klassen, die Exporte und die Needs). Die Schnittstelle wird zuletzt geschrieben: ohne sie gilt die
Bibliothek als nicht da. Eine Library, deren Code sich ändert, bekommt einen neuen Schlüssel; die Libraries, die sie brauchen, nur wenn sich ihr ABI-Hash ändert.
Deklariert das Programm eine Klasse einer Library neu (eine eigene `class Exception`), passen die Klassen nicht mehr zur Bibliothek: der Bau nimmt `source` und sagt es.
`ember cache` zeigt Ort und Größe, `ember cache clean` leert den Cache.

### Ziele

* **Desktop** (gcc, clang, TinyCC mit `tcc -ar`, MinGW mit `x86_64-w64-mingw32-ar`, msvc mit `lib`): das Programm wird mit den Bibliotheken gebunden, eine Library vor
  denen, die sie braucht.
* **wasm** (Emscripten): die Bibliotheken mit `emcc -c` und `emar`, gebunden wie oben.
* **ESP32** (Toolchain `esp-idf`, nur Dateien): die Module stehen als eigene Übersetzungseinheiten (`fire_<modul>.c`, ihr C aus dem Cache) in der Komponente;
  `idf.py` übersetzt sie in die Bibliothek der Komponente. Ein fertiges `.a` aus dem Cache wird dort nicht benutzt (das übersetzt die Toolchain von ESP-IDF mit ihren
  eigenen Schaltern).
* `"shared"` (Phase C): Linux und macOS mit gcc oder clang. Jede Library wird eine **Shared Library** (`libfire_<modul>_<schlüssel>.so`/`.dylib`, positionsunabhängig,
  eine eigene Variante im Cache), das Programm wird gegen sie gebunden und exportiert, was sie von ihm brauchen (`-rdynamic`: der Zustand der Runtime - auch die
  thread-lokalen Variablen -, die Tabellen und Thunks des Programms); die Bibliotheken liegen nach dem Bau **neben dem Programm** (`rpath $ORIGIN`). Jede Bibliothek
  nennt die Schnittstelle, mit der sie gebaut ist (`fire_abi_<modul>`: Formatversion und ABI-Hash); das Programm prüft sie beim Start, vor allem anderen - liegt eine
  andere daneben, endet es mit „The library of the module 'X' does not fit this program …“ (Exitcode 70) statt abzustürzen. Andere Ziele und Toolchains (msvc,
  TinyCC, die Boards, wasm) bauen `static` und sagen es im Protokoll. Ein Debug-Build übersetzt die Shared Libraries mit `-O2`.
* `"shared"` unter **Windows** (MinGW: gcc oder clang, auch von Linux aus): jede Library wird eine **DLL**, die **Runtime eine DLL für sich**
  (`libfire_rt_<schlüssel>.dll`: der Zustand und die Funktionen von fire_rt.h - `FIRE_DLL_RUNTIME`; ihr Schlüssel ist der Hash des Kopfs der Dateien, also der
  Schalter und des Ziels). Eine DLL kann nur aus anderen DLLs importieren, über deren Dateinamen - nicht aus dem Programm:
  * Das Programm und die Module importieren den Zustand der Runtime (`FIRE_RT_DATA`: `__declspec(dllimport)`, in der Runtime `dllexport`; Funktionen `FIRE_RT_FN`).
  * Die **thread-lokalen Variablen** (`__declspec(thread)`/`_Thread_local` lassen sich nicht importieren) liegen in der Runtime-DLL in einem Block je Thread
    (`ThreadLocals`, wie bei `FIRE_TLS_STRUCT`); das Programm und die Module fragen sie mit `fire_tl()` danach (`g_unwind` ist dort `fire_tl()->g_unwind`;
    `__attribute__((const))`: einmal je Funktion).
  * Was die Runtime und ein Modul **vom Programm** brauchen - die Hooks der Header (`makeClassError`, `make…Error`, die JSON-Tabellen `jsonClassAt` …, die Klassen
    für den Debugger, `g_dimNames`), die Tabellen, die das Programm für ein Modul anlegt (`fire_mb_M`, `fire_units_M`, `fire_enums_M`, `fire_exct_M`, `fire_dbgsrc_M`),
    die Reflection-Funktionen `rf_*`, die Thunks der Natives in C++ und die `fire_cn_` der Natives im Programm -, sind **Zeiger der DLL** (`FIRE_DLL_LINKED`: der Kopf
    der Datei schreibt `#define makeClassError (*fire_hp_makeClassError)`, eine Tabelle von Zahlen ist ein Array der DLL, auf das ihre statischen Tabellen zeigen).
    Jede DLL hat eine Funktion `fire_link_<modul>(find)`, die sie füllt: das Programm hat eine nach Namen sortierte Tabelle (`kHostSyms`, `fire_hostFind`) und ruft
    beim Start - nach der Prüfung der Schnittstellen, vor allem anderen - erst `fire_link_rt`, dann die der Module. Fehlt dem Programm etwas, das eine DLL braucht
    (eine fremde DLL daneben), endet es mit „… it needs 'X', which the program does not have …“ (Exitcode 70). Was eine Library vom Programm braucht, steht in ihrer
    Schnittstelle (`HostImports`).
  * Eine DLL wird nach ihrer Schnittstelle und der Runtime benannt (`libfire_<modul>_<hash>.dll`), nicht nach ihrem Schlüssel: die DLLs, die sie brauchen, importieren
    sie mit diesem Namen, ein neuer Bau ihres Codes behält ihn. Gebaut werden die Objektdateien nebeneinander, gebunden in der Reihenfolge des Plans (die Runtime
    zuerst, ein Modul nach denen, die es braucht; `-shared -static-libgcc -Wl,--export-all-symbols`); alle DLLs liegen nach dem Bau neben dem Programm, die Prüfung beim
    Start ist die von Linux (auch für die Runtime-DLL).
  * Ein Modul ruft Member, die ein Modul hinzufügt, das es nicht kennt (`Join` von linq auf `List` von core), über die Runtime (`miss_`, die Tabellen der Klasse) -
    nicht direkt: die Datei eines Moduls hängt nicht davon ab, welche Module das Programm sonst hat (sonst gäbe es einen Kreis core <-> linq).
  * Getestet mit MinGW (x86_64-w64-mingw32-gcc) unter Wine (`FIRE_TESTS_ONLY=linkage`: ein Projekt mit Libraries, Threads, Reflection, JSON, zwei Programme teilen sich
    die DLLs, eine DLL mit anderer Schnittstelle). **msvc** baut `static`: der C-Code hält sich an das, was msvc braucht (`dllimport` für Daten, Exporte der
    Runtime), aber die Tabellen des Programms zeigen in statischen Initialisierern auf Daten der Modul-DLLs (MinGW löst das mit seinen Pseudo-Relocations, msvc nicht),
    und die Exporte der Modul-DLLs bräuchten eine `.def`-Datei - nicht umgesetzt, ohne msvc hier nicht zu testen.
* **Laden zur Laufzeit** (Phase D a): `Library.Load(path)` / `lib.Get(name, signatur)` ([Native code](../language/18-native-code.md)) - die Klasse `Library` steht im Prelude
  (`fire.Standard.LibraryMethods`, eine native Funktion `__LibCall`). Die VM lädt mit `NativeLibrary` und ruft über einen Delegate, den sie für die Signatur baut (wie
  `#extern`; `VM.Library.cs`). Nativ lädt die Plattformschicht (`fire_plat_dlopen`/`fire_plat_dlsym`: `dlopen` unter POSIX, `LoadLibraryW` unter Windows, auf den
  Boards und im Browser keine); ein Aufruf geht über einen **Thunk je Signatur**: das Programm erzeugt einen für jeden Text des Programms (aller Module), der eine
  Signatur ist (`fire_lt_k`: die Argumente wie bei einem `extern`-Aufruf umgewandelt, die Adresse mit diesem C-Typ gerufen), und `fire_libThunk(signatur)` findet
  ihn (mit Modulen im Programm, bei `"shared"` unter Windows ein Hook). Eine Signatur, die erst zur Laufzeit entsteht, hat keinen: `Get` sagt es. Die Fehler wirft
  der fire-Code der Klasse (`LibraryException`, in VM und nativ gleich).
* **`release:`** (`lib.Get(name, signatur, release: "free")`): die Freigabefunktion der Bibliothek (`void(void*)`) wird bei `Get` in derselben Bibliothek gesucht
  (`__LibCall(2, ...)`). Ein zurückgegebener Zeiger wird im Prelude zu einem Objekt der Klasse `NativeHandle` (Adresse und Adresse der Freigabefunktion als Felder),
  sein Destruktor ruft `__LibCall(7, release, adresse)` - nativ direkt `((void (*)(void*))release)(adresse)`, ohne Thunk; einmal, weil ein Destruktor einmal läuft.
  Ein Argument, das ein `NativeHandle` ist, gibt seine Adresse (`NativeHandle.Arg` im Lambda, nur bei Signaturen mit Zeiger-Parametern; ein freigegebenes wirft
  die `DestroyedException`). Ein zurückgegebener String: der Thunk bekommt die Freigabefunktion als Parameter (`FireLibThunk(fn, args, release, list)`), kopiert
  den Text (`externResultString`) und gibt den C-Speicher gleich danach frei. `__LibCall(6, signatur)` sagt dem Prelude, ob das Ergebnis ein Zeiger oder ein String ist
  und ob ein Parameter ein Zeiger ist (`fire_libSigShape`).

### Plugins (Phase D b)

Ein **fire-Plugin** ist ein Library-Modul, das für sich gebaut und von einem laufenden Programm geladen wird (`Library.Load`, [Native code](../language/18-native-code.md)
"fire plugins"). Gebaut wird es mit `--linkage plugin` (oder `"linkage": "plugin"` im Projekt): die Klassen der eigenen Dateien des Programms werden das Modul des
Plugins (benannt nach dem Projekt bzw. der Ausgabe), alle anderen Module (core, die Imports, die Libraries des Projekts) sind die, die es braucht. Heraus kommen
`<ausgabe>.so` (`.dll`, `.dylib`) und `<ausgabe>.fireplugin`, der Bytecode für die VM (Entscheidung 9.8; für die VM übersetzt - `#if vm` -, mit den Namen seiner
Natives und den Schnittstellen der Module, wie die VM sie sieht).

* **Das Plugin** ist ein Modul mit der Rolle einer Library (`_plugin`): es nummeriert seine Klassen ab einer Basis, ruft die anderen Module über ihre exportierten
  Namen, bedient seine eigenen Klassen selbst. Was das Programm sonst für eine Library festlegt, legt es selbst an und füllt es beim Registrieren: `fire_mb_<p>`
  (die Id seiner ersten Klasse), `fire_exct_<p>` (seine Typen von `catch`, nach Namen), `fire_enums_<p>` (seine Enums, nach Namen), `fire_units_<p>` (seine
  Einheiten: eine Zeile `UnitInit` je Einheit über die Namen ihrer Dimensionen - `kPlDims` -, die Umrechnungsfunktionen der Einheiten mit `coerce to`/`from`, ob eine
  davon zu einer Einheit des Plugins selbst gehört, und die Einheiten, die ein Text nennen darf - die von fire, des Plugins und seiner Libraries). Dazu schreibt es die Deskriptoren seiner Klassen (wie das Programm: Destruktoren der Kette, ToString, `message`/`ticks`, die
  Namen der Kette und der Interfaces, die Tabellen der Member - seine eigenen und die exportierten `fire_mem_<m>_<Klasse>` der Module seiner Basisklassen -, die
  Funktion der Typargumente), seine Reflection-Zeilen (mit Reflect.New: `RfClass.make`; die Metadaten seiner Klassen - `ClassMeta`: Typen, Parameter, Attribute -
  legt der Compiler für ein Plugin immer an, auch wenn es `reflection` nicht selbst importiert: das Programm erreicht seine Klassen per Reflection, `Linker.ClassMeta`), eine Funktion mit den statischen Initialisierern seiner Klassen (was sie
  anlegen, gehört dem globalen Scope) und `fire_module_info` (`FirePlugin` in fire_rt.h).
* **Registrieren** (`fireRegisterPlugin`, `Library.Load` ruft es über `__LibCall(5, handle)`, wenn die Bibliothek `fire_module_info` exportiert): geprüft wird nur über
  Hashes (Entscheidung 9.6) - die Schnittstelle jedes Moduls, das es braucht (`format:hash`, wie bei `"shared"`), muss die des Programms sein, und der Schlüssel der
  Konfiguration der Runtime (Generator, Schalter der Module, Kopf der Dateien) der des Programms. Dann bekommen seine Klassen die nächsten Ids (`g_classDescs` wächst:
  kopiert und angehängt, die alte Tabelle bleibt für andere Threads), seine Typen von `catch` die Ids des Programms (ein Typ, den das Programm nicht kennt, eine neue,
  `excMatches` vergleicht ihn über den Namen der Klasse), seine Enums die Nummern des Programms (sonst neue), seine Reflection-Zeilen kommen in `g_pluginRf`, dann
  laufen die statischen Initialisierer. **Einheiten** (`firePluginUnits`): ihre Dimensionen bekommen nach Namen die Plätze des Programms in `g_dimNames`, eine
  Dimension, die das Programm nicht hat (`pears`, das `V` eines `prefixed unit Volt: V` des Plugins), einen freien Platz (`FIRE_NDIMS` ist mit `"shared"` 16 - reicht der
  Platz nicht, sagt `Load` es; `g_dimNames` ist dafür nicht mehr `const`); eine Einheit wird die des Programms mit denselben Dimensionen, Faktor, Offset und Symbol,
  sonst eine neue. Eine Einheit mit Umrechnungsfunktionen einer Library (dBm von `Api`) ist die des Programms, wenn es sie hat; eine des Plugins selbst ist immer neu
  (ihre versteckte Klasse gehört dem Plugin - hätte das Programm eine Klasse dieses Namens, lädt das Plugin ohnehin nicht), ihre Funktionen bekommen die nächsten
  Nummern in `g_unitTo`/`g_unitFrom` (kopiert und angehängt). Die Einheiten, die ein Text nennen darf (`"7in".ToFloat()`), kommen zu `g_textUnits`, wenn das
  Programm das Symbol nicht hat. Hat ein Plugin Dimensionen hinzugefügt (`g_dimsAdded`), schreibt die Runtime die Namen von Einheiten in der Ordnung der Namen wie
  die VM (`unitDimOrder`: Buchstaben ohne Groß/klein, dann klein vor groß), nicht in der ihrer Plätze. Ein zweites `Load` desselben Plugins gibt dieselben Klassen; ein anderes Plugin mit demselben Namen oder eine Klasse, die das
  Programm schon hat, ist ein Fehler. Die Fehler sind Texte, die `Library.Load` als `LibraryException` wirft („The plugin 'Kit' cannot be loaded: ...“).
* **Das Programm** (nur mit `"shared"`) ist eine offene Welt (`_openWorld`): keine Devirtualisierung ohne Prüfung (`DirectMethod`, `DirectAccessor`), keine
  Verteiler eines Interfaces (die allgemeinen gehen für eine fremde Klasse über die Runtime), `is of`, Feld-Helfer, Klassen-Wertungen fragen für Klassen nach den eigenen
  (`FIRE_PROG_END`) die Runtime; jede Klasse der Libraries hat ihren vollen Deskriptor (ein Plugin kann Objekte jeder Klasse anlegen), die Tabellen der eigenen
  Klassen nennen jeden Member (ein Plugin ruft sie beim Namen), `makeClassError` kennt jede Ausnahme der Libraries. Es sagt der Runtime, wogegen sie prüft
  (`g_pluginHost`: der Schlüssel der Konfiguration, die Schnittstellen seiner Module, die Namen seiner Typen von `catch`, unter Windows `fire_hostFind`), und
  `rf_find`/`rf_classes`/`rf_new` sehen die Klassen der Plugins. Neben das Programm schreibt der Bau `<programm>.firehost` (JSON: Ziel, Schalter, SDL, Schlüssel der
  Runtime, die Module mit Schnittstelle und Datei ihrer Bibliothek); `--host <programm>` baut ein Plugin mit diesen Schaltern (braucht das Plugin einen weiteren,
  sagt es der Bau). Ein Programm ohne `"shared"` exportiert nichts, was ein Plugin braucht: `Load` sagt, dass es keine Plugins laden kann.
* **Natives in C++** (der Ordner `native/` des Projekts des Plugins, ein Package in C++, das es importiert): eine Datei für sich (`fire_plugin_natives.cpp`: der
  Kopf seiner C-Datei, `fire_rt_cpp.hpp`, das C++ der Packages, deren Natives sein Code ruft, seine Thunks `fire_nat_<p>_k` - nicht die des Programms), mit dem
  C++-Compiler übersetzt und in dieselbe Bibliothek gebunden (Linux mit `-lstdc++`, MinGW mit `-static-libstdc++`). Sie sind die **eigene Kopie** des Plugins: ihr
  Zustand ist nicht der des Programms, auch wenn das Programm dasselbe Package importiert (Linux: mit `-fvisibility=hidden` übersetzt, wie es eine DLL ohnehin ist).
  Unter Windows sind die Zeiger auf das Programm (`makeClassError`, ...) die der C-Datei des Plugins: der Kopf der C++-Datei sagt `FIRE_DLL_LINKED_ELSEWHERE`, dann
  deklariert `FIRE_HOST_EXTERN` sie `extern` (die Hooks der Header sind dafür `FIRE_HOST_EXTERN` deklariert). Eine Ausnahme eines Natives (`fireError`) macht
  `makeClassError` des Programms - eine Klasse, die nur das Plugin kennt, wird so nicht geworfen (das Programm endet mit ihr). Für die VM baut der Bau ihre
  Bibliothek mit der C-ABI (`PackageLibrary`, wie für ein Package) und legt sie neben die `.fireplugin`-Datei; die nennt die Natives und ihre Bibliotheken
  (`PluginBytecode.PackageNatives`/`PackageLibraries`), die VM registriert beim Laden die, die das Programm nicht hat (`PackageNativeBinding`).
* **Natives in C** (ein Package oder eine Library mit `"language": "c"`): sie gehören dem Programm (wie bei `"static"`: ein Plugin ruft die `fire_cn_<funktion>` des
  Programms, oder die des Moduls, in dessen Datei sie liegen). Ein Programm, das Plugins laden kann, legt `fire_cn_` für **jedes** Native in C der Packages in seiner
  Datei an (`OpenWorldNativesC`; unter Windows auch in `fire_hostFind`), nicht nur für die, die seine Libraries rufen - ein Plugin darf eines rufen, das weder das
  Programm noch die Library ruft. Von graphics und windows nur die, die seine Libraries rufen. Fehlt dem Programm eines (es wurde vor der neuen Version des Packages
  gebaut), sagt `Load` es lesbar statt „could not be loaded“: unter Linux erkennt `libCall` `fire_cn_` im Text von `dlerror()` (`fire_plat_dlerror`) und gibt einen
  Text zurück, den `Library.Load` als `LibraryException` wirft; unter Windows meldet es die `fire_link_<p>` des Plugins beim Registrieren - dieselbe Meldung
  (`FIRE_PLUGIN_CNATIVE`: „... cannot be loaded: it calls the native function 'x' in C, which this program does not export to plugins ...“).
* **JSON** (das Programm hat `#import "json"`, dann hat es auch das Plugin - dieselben Schalter): das Plugin schreibt die Zeilen `JsonClassInfo` seiner Klassen und
  seine Typargumente (`FirePlugin.jsonAt`, `jsonNamed`, `jsonTargText`, `jsonTargFor`); die Einheiten der Felder sind die des Programms, sobald es registriert ist
  (die Tabellen der Felder sind nicht `const`, `fire_pl_init` setzt sie aus `fire_units_<p>`). `jsonClassAt`, `jsonClassNamed`, `jsonTArgText`, `jsonTArgFor` eines
  Programms, das Plugins laden kann, fragen für das, was nicht in seinen Tabellen steht, die Runtime (`firePluginJsonAt`, ...: die Plugins nach Id oder Namen) - so
  schreibt und liest JSON Objekte der Plugins (`"$type": "Kit.Settings"`, auch als Unterklasse einer Klasse einer Library), in der VM wie nativ.
* **Linux/macOS**: das Plugin ist eine Shared Library ohne Abhängigkeiten beim Linken; was es vom Programm und den Libraries braucht, findet der Lader im Programm
  (`-rdynamic`) und in den Bibliotheken, die es schon geladen hat.
* **Windows** (MinGW): die DLL des Plugins importiert aus der DLL der Runtime und den DLLs der Module des Programms (ihre Namen aus `.firehost`, `--host` ist dort nötig);
  was es vom Programm braucht, sind Zeiger, die seine `fire_link_<p>` beim Registrieren aus der Tabelle des Programms füllt (`fire_hostFind`; die Tabelle eines
  Programms, das Plugins laden kann, nennt auch die Basen aller Module und seine Reflection-Funktionen).
* **VM** (`VM.Plugins.cs`): `Library.Load` einer `.fireplugin`-Datei (oder einer Bibliothek, neben der eine liegt) liest den Bytecode; beim Registrieren werden die
  Schnittstellen der Module verglichen (`ModuleInterface`, die Module des laufenden Programms - ein gepacktes Programm kennt sie nicht und prüft nicht), die Klassen
  in die des Programms gehängt (ihre Basisklassen sind die des Programms), die Nummern der Natives im Code nach Namen umgeschrieben, die statischen Initialisierer
  ausgeführt. Code, der Variablen der obersten Ebene seiner Datei oder `extern`/versuchbare Natives benutzt, lädt nicht (mit einer Meldung). Eine Einheit der VM
  trägt ihre Dimensionen und die versteckte Klasse ihrer Umrechnung beim Namen - nur die Einheiten, die ein Text nennen darf, kommen zu denen des Programms
  (`Unit.AddCustomUnits`; ein gepacktes Programm kennt seine Liste nicht und nimmt sie nicht auf).
* **`class extends` im Plugin** (einer Klasse einer Library - `class extends Api.Device`, der Name darf qualifiziert sein -, von `List`, eines Basistyps
  `string`/`int`/...): ein Plugin fügt **Methoden** (auch statische) und **Properties mit Rümpfen** hinzu; ein Feld (auch eine Auto-Property), ein Konstruktor,
  Destruktor oder Operator ist ein Fehler beim Bau (`PluginExtensionProblem`: „'class extends Api.Device' in a plugin (line 4): the field 'age' cannot be added
  ...“) - die Objekte dieser Klassen legt das Programm an, ohne Platz dafür. Nativ schreibt das Plugin für jede solche Klasse eine Tabelle der Member (wie sonst
  ein Modul, das eine Klasse eines anderen erweitert) und nennt sie in `FirePlugin.extClasses`/`extTables` (`FIRE_PLUGIN_FORMAT` 4); beim Registrieren kommen sie
  in `g_extTables` (kopiert und angehängt), und `fireFindMethod` sucht nach den Tabellen des Deskriptors in denen, deren Klasse in der Kette des Objekts steht
  (`isa`) - so finden die Verteiler des Plugins (über `miss_`), `any`-Aufrufe und Reflect.Call des Programms die Methoden auf jedem Objekt, auch einer Unterklasse
  im Programm. Basiswerte (`"x".Shout()` über `any`) fragt `fireFindValueMethod` nach `$string`, `$int`, ...; ist das Plugin das einzige, das einen Basistyp
  erweitert, ist `$int` eine Klasse des Plugins - sie wird nicht genannt und nicht gegen das Programm geprüft, ihre Tabelle ist eine Erweiterung wie die anderen.
  Fügt ein zweites Plugin dieselbe Methode (Name und Zahl der Argumente) hinzu, lädt es nicht („... it adds the method 'Tag' (0 arguments) to 'Api.Device', which
  has it already ...“). Die VM hängt die Methoden an die Klassen des Programms (die `.fireplugin`-Datei nennt sie: `PluginBytecode.Extensions`), vergisst die
  gefundenen Methoden aller Klassen (`RuntimeClass.ForgetFoundMethods`) und vergleicht die Schnittstellen der Module weiter mit denen, die das Programm vor dem
  ersten Plugin hatte. Dabei behoben: ein Programm, das Plugins laden kann, nennt in den Tabellen seiner Klassen **jede** Methode (`TableWants` kannte die
  offene Welt nicht) - eine Klasse des Programms, die eine Methode einer Library überschreibt, die nur ein Plugin ruft, rief vorher die der Library.
* Noch nicht: benannte Argumente in Aufrufen des nativen Programms auf Objekten
  eines Plugins (die Verteiler des Programms kennen nur seine Klassen - die Runtime bräuchte die Namen und Vorgaben der Parameter in den Tabellen der Member);
  msvc.
* **Debuggen** (ein Debug-Build des Programms mit `"shared"` und des Plugins, `-m debug` für beide; das Plugin wird dann mit `-O0 -g` übersetzt): die Zeilen des
  Plugins melden ihre Quelle über `fire_dbgsrc_<p>`, das jetzt nicht mehr konstant ist - `fireRegisterPlugin` ruft `fire_dbgPlugin` (fire_debug.h), das seinen
  Quellen die nächsten Indizes nach denen des Programms gibt (`FirePlugin.nsources`/`srcFiles`/`srcHashes`/`srcMap`, `FIRE_PLUGIN_FORMAT` 5), dem Debugger das
  Ereignis `sources` schickt und auf seine Haltepunkte wartet ([Debug-Protokoll nativer Programme](NATIVE_DEBUG_PROTOCOL.md)). Die Tabellen der Funktionen und Scopes braucht die Runtime nicht zu
  kennen: jeder Rahmen zeigt auf die seiner Funktion (`DbgFrame.fn`); die Namen der Felder seiner Klassen gibt das Plugin über `FirePlugin.dbgClass`. In der **VM**
  trägt die `.fireplugin`-Datei die Zeilentabellen ihres Codes (`Chunk.KeepLineTable`, in `DebugLocalNames` - die Form eines Chunks bleibt) und ihre eigenen
  Quellen (`PluginBytecode.Sources`); `LinkPlugin` schreibt die Quellindizes ihrer Zeilen auf die nächsten des Programms um (die Quellen ihrer Libraries auf 0) und
  meldet sie (`VM.PluginSourcesLinked`); `VmDebugTarget` hängt sie an `SourceFiles` an (`IDebugTarget.SourcesAdded`) und nimmt die Haltepunkte nach Datei in ihnen
  sofort in den laufenden `RunUntilBreakpoint` auf. spark gibt seine Haltepunkte auch nach Datei (`SetFileBreakpoints`), die Sitzung übernimmt die neuen Quellen.
  Getestet: `FIRE_TESTS_ONLY=linkage FIRE_TESTS_FILTER=plugindebug` (nativ: Haltepunkt im Programm, `sources`, Step Into in die Methode des Plugins, Scopes,
  `this` mit dem Feld des Plugins, Haltepunkt nach Datei im Plugin, Step Out) und derselbe Plan in der VM (`FIRE_TESTS_ONLY=debugger`, "Plugins in der VM").
  Nicht geprüft: das Fenster von spark (die Haltepunkte im Editor in einer Datei des Plugins), Windows.
* **Nur für die VM** (`NativeBuilder.WriteVmPlugin`, ohne native Bibliothek): spark lädt so die Treiber installierter Packages ([Devices](../devices/README.md), `PackageDriverAgent`).
  Das Prelude eines Imports ist die eigene Quelle des Plugins: `Linker.OwnImports` nennt den Import, seine Natives sind die des Plugins, sein Prelude wird nicht noch
  einmal eingefügt (was das Package verlangt, kommt wie sonst dazu). Die `.fireplugin`-Datei nennt seine Natives mit dem vollen Pfad der Bibliothek des Packages für die
  VM (`PackageNatives`/`PackageLibraries`; ein Pfad statt eines Dateinamens geht, `Path.Combine` mit dem Ordner des Plugins lässt ihn stehen).
* Getestet (`FIRE_TESTS_ONLY=linkage`, nur die Plugins mit `FIRE_TESTS_FILTER=plugin`): ein Projekt mit der Library `Api`, dem Programm (shared) und dem Plugin `Kit`
  - Interface, Basisklasse mit überschriebenen Methoden, Reflect.New/Get/Set/Call, Type.Of (auch `Fields()`/`Find` der Felder von Kit, das `reflection` nicht
  importiert), Ausnahmen beider Richtungen, Enum, statische Felder - nativ, in der VM und
  unter Windows (MinGW, Wine); eine andere Schnittstelle von `Api`, ein Programm ohne `"shared"`, andere Schalter. Das Plugin `Meter` mit Einheiten: eigene (neue
  Dimension, Faktor, Offset, eigene Umrechnungsfunktionen), dBm von `Api` (Umrechnungsfunktionen einer Library, auch im Programm), die von fire, Einheiten des
  Programms hinein und zurück (`ft`, `apples`), Texte mit seinen Einheiten, ein Name aus einer hinzugefügten und einer Dimension des Programms; dazu Natives in C++ im Ordner `native/` des Projekts
  (`std::string`, eigener Zustand, eine Ausnahme aus C++). JSON (ein Programm mit `#import "json"`, Kit und Meter dafür gebaut): Serialize/Deserialize eines
  Objekts von Kit (eine Liste, ein Feld mit einem Objekt des Plugins), `Json.Deserialize<Api.Device>` mit `"$type"` einer Klasse von Kit, Felder mit Einheiten von
  Meter - nativ, VM, Windows. `class extends` in Kit (Api.Device: Methode, Property, statische Methode; `string`, `int` - nur von Kit erweitert -, `List`) auf
  Objekten des Programms (eine Unterklasse, die `Speed` überschreibt) und des Plugins, über `any` und Reflect.Call; ein Feld - der Fehler beim Bau; ein zweites
  Plugin `Kit2` mit derselben Methode - die Meldung (nativ, VM, Windows). Natives in C (`CApi` mit `"language": "c"`): das Plugin ruft eines, das weder das Programm noch `CApi` ruft; eines, das das Programm
  nicht hat - die lesbare Meldung (Linux, Windows; die VM ruft es).

### Debuggen

Ein Debug-Build mit `static` debuggt wie sonst: jede Funktion einer Library hat ihre Tabellen (Variablen, Scopes) in ihrem Modul, ihre Zeilen melden die Quelle über
`fire_dbgsrc_<m>`, das Programm hat die Quellen, Globals und Klassen. Ein Haltepunkt in einer Datei einer Library des Projekts hält dort (Test `FIRE_TESTS_ONLY=linkage`).

### Aus einer Datei: `FIRE_MODULES=1`

Dieselbe Erzeugung in Modulen, aber alles in **einer** Datei (die Libraries hinter der Runtime, dann das Programm; die Namen einer Library haben den Präfix ihres Moduls):
mit `FIRE_MODULES=1` erzeugt jeder native Bau so (für die Tests: die native Gruppe läuft damit genauso). Ein Programm, das sich nicht teilen lässt, wird ganz erzeugt
(`FIRE_MODULES_LOG=<datei>` schreibt auf, welches - in der nativen Gruppe zwei: eines deklariert `class Exception` neu, eines `class Shape` neben der von `ui`).
Gelaufen ist die native Gruppe damit bis zu den Fenstern (402 Prüfungen, ~2 h - jeder Fall enthält das ganze Prelude); verschieden sind nur Prüfungen, die die
Form des erzeugten C ansehen (der eigene Verteiler eines Interfaces, die Größe des minimalen ui-Programms, der Name `fire_nat_1`), alle Ausgaben sind gleich.

### Messwerte

Gemessen auf dem Rechner dieser Sitzung (4 vCPUs, gcc 13 `-O2`, ohne ccache), Zeit des ganzen Baus mit `forge build`:

| Programm | `source` | `static`, erster Bau (leerer Cache) | `static`, zweiter Bau | Größe `source` / `static` |
|---|---:|---:|---:|---:|
| Phase B: CLI mit `json`, `linq`, `hash`, `compression` (9 Module) | 9-10,5 s | 51 s | 27-29 s | 226 KB / 2,5 MB |
| Phase B: `ui`-Beispiel (5 Module) | 98 s | 223 s | 85 s | 2,3 MB / 5,7 MB |
| **B2**: CLI | 9,7-10,4 s | 41-45 s | **6,5 s** | 226 KB / 2,7 MB |
| **B2**: `ui`-Beispiel (Samples/Graphics and UI/UI) | 100 s | 237 s | **16 s** | 2,3 MB / 6,5 MB |
| **B2**: `print("hi")` (nur `core`) | 2,7 s | 20 s | 3,6 s | 26 KB / 395 KB |
| **C**: CLI mit `"shared"` | 9,7-10,4 s | 60 s | 6,2 s | 226 KB / 285 KB + 3,8 MB in 9 Shared Libraries |

Die Ausgaben sind gleich. Die Bibliotheken eines ersten Baus werden nebeneinander übersetzt (so viele wie Kerne). Seit B2 übersetzt der zweite Bau nur noch,
was das Programm selbst ist: das C des CLI-Programms ist von 1,2 MB auf 214 KB geschrumpft (Verteiler und Feld-Helfer nur für seine Klassen, keine Reflection-Helfer
für die Klassen der Libraries, Natives von hash/compression/io/time und die JSON-Engine in ihren Bibliotheken), das des `ui`-Beispiels enthält im Wesentlichen
noch die Bridges von graphics und windows (sie gehören zusammen und stehen im Programm). Ein Programm nur mit `core` bleibt etwas langsamer als `source`: es
bindet eine Bibliothek, `source` übersetzt die paar Funktionen, die es braucht.
Die Programme sind größer: eine Library behält ihre Schnittstelle, ihre Tabellen nennen alle Member ihrer Klassen, und das Programm legt die Deskriptoren aller
Klassen an, von denen irgendein Code einer Library Objekte anlegt (ob dieser Code erreicht wird, weiß es nicht) - der Linker (`--gc-sections`) kann nur weglassen,
was keine Tabelle nennt. Näher an `source` käme `static` mit einer Erreichbarkeit über die Modulgrenzen (die Schnittstelle sagt je Export, was er ruft und anlegt)
oder einer LTO.

## WebAssembly (`wasm`) - Programme im Browser

`fire.Compiler native programm.fire -t wasm` bzw. `fire.Compiler build programm.fire -t wasm` baut ein Programm, das im Browser läuft. Das Ergebnis ist **eine** Datei
`out.html` (Standard), die sich so öffnen lässt - auch per Doppelklick aus einem Ordner, ohne Webserver: die WebAssembly-Binärdatei steckt in der Seite (`-sSINGLE_FILE=1`).

**Werkzeug:** Emscripten (emsdk; das Programm ist C: `emcc` mit `-std=gnu11`, denn `EM_ASM` braucht den GNU-Modus). Die Toolchain `emscripten` ist eingebaut und gehört zum Ziel `wasm`; `em++` muss im Pfad sein (nach `emsdk_env`). Argumente: `-fexceptions` (fire-Ausnahmen sind C++-Ausnahmen), `-sASYNCIFY` (siehe unten),
`-sALLOW_MEMORY_GROWTH=1`, `-sSINGLE_FILE=1`, `-sMINIFY_HTML=0`; `// fire-link: SDL2` wird zu `-sUSE_SDL=2` (SDL2 bringt Emscripten selbst mit, es gibt nichts zu installieren).
Den SDL2-Port lädt Emscripten beim ersten Bau eines Programms mit Fenster von GitHub (als Zip) und übersetzt ihn einmal (danach im Cache von emsdk); ohne
Zugang zu diesem Download nimmt es einen Ordner mit den Quellen: `git clone --branch release-2.32.10 https://github.com/libsdl-org/SDL` und
`EMCC_LOCAL_PORTS=sdl2=<Ordner>`. Geprüft mit Emscripten 6.0 (Testgruppe `packer`: ein Programm unter node wie die VM, ein Fenster im Chromium der Testmaschine, headless).
Für eine eigene Seite: `toolchains` in `fire.native.json` mit `"extends": "emscripten"` und `args` bzw. `--shell-file`.

**Plattformpaket `native/platform/wasm/`:**

| | im Browser |
|---|---|
| Takt/`Sleep` | `Sleep` gibt den Thread mit `emscripten_sleep` (ASYNCIFY) an den Browser zurück: die Seite zeichnet und liefert Ereignisse, solange das Programm wartet. Eine Schleife ohne `Sleep`/Fenster-Ereignisse hält die Seite an, wie überall im Browser |
| Fenster (`windows`, `ui`) | SDL2 von Emscripten zeichnet in das Canvas der Seite; `Window.Tick`/`PollEvents` und das Zeichnen geben den Thread ebenfalls kurz zurück |
| Ton | die SDL2-Audio von Emscripten (Web Audio) |
| Dateien | das virtuelle Dateisystem von Emscripten (im Speicher; es lebt, solange die Seite offen ist) |
| Ausgabe (`print`) | in das Textfeld der Seite und in die Konsole des Browsers |
| Threads (`fire { }`, Actors) | nicht vorhanden: der Generator meldet es beim Übersetzen (`fire threads are not supported by the platform 'wasm'`) |
| Netzwerk, TLS, Geräte, GPIO, I2C, SPI, WiFi | nicht vorhanden (die `none`-Varianten: die Bibliothek meldet zur Laufzeit "not supported") |

`#if wasm` und `#if browser` unterscheiden den Code (SPEC 8.1.7). Die Genauigkeit von `float` ist 64 Bit wie am Desktop.

## ESP32 / FreeRTOS - weitere Besonderheiten

* **Speicher**: `Value` hat 16 Byte. Objektlayouts pro Klasse halten den Heap klein. Kein GC - die Ownership-Kaskade ist deterministisch.
* **Code im Flash**: erzeugtes C++ landet als normaler Code im Flash; String-Konstanten sind `static const` (Flash/DROM).
* `#extern "lib"` (dynamisches Laden) entfällt dort; `extern` wird zum statischen Bindungspunkt.
* **Debuggen**: ein Debug-Build hat den Agenten auf der Konsolen-UART (gerahmt, 3 s Wartezeit, die Standardeingabe des Programms geht durch den Agenten);
  spark baut, flasht und debuggt über `idf.py` und die serielle Schnittstelle - siehe "Ziel `esp32`: Run und Debug auf dem Board" und NATIVE_DEBUG_PROTOCOL.md.
  Ein generisches FreeRTOS-Ziel (`freertos`) debuggt gerahmt über stdio.

## Natives von Packages in der VM (Shared Library, C-ABI)

Die C++-Natives eines Packages ([Packages and ember](../packages/README.md), [Writing the natives of a package (C or C++) and connecting them to the prelude](../packages/natives.md)) laufen in der VM nicht als Fehler-Stub, sondern über eine **Shared Library**: `PackageLibrary` (fire.Compiler) erzeugt einen
Wrapper (`native/abi/fire_pkg_wrapper.hpp` + je Funktion ein Thunk), kompiliert ihn zusammen mit `fire_rt.h` (als C++, über `fire_rt_cpp.hpp`) in dem Modus `FIRE_LIBRARY` (`fatal`/`unsupported` werfen eine `FireFatalError`, statt das
Programm zu beenden; `runDestructors` ist leer) und den Quellen des Packages für die Plattform dieses Rechners (`platformSources`) mit dem Toolchain-Aufruf von `NativeBuilder.CompilerCommand(sharedLibrary: true)`.
Die Bibliothek exportiert die C-Schnittstelle aus `native/abi/fire_pkg_abi.h` (`fire_pkg_call` mit `fire_val`: Zahlen, Text als UTF-16, Arrays und Puffer werden kopiert). `PackageNativeBinding` (fire.Runtime) lädt sie
(`NativeLibrary`, im gepackten Programm über den Payload wie SDL) und übersetzt `Value` <-> `fire_val`. Ein Package kann fertige Bibliotheken je Runtime-Identifier mitbringen (`native.libraries`).
Aufrufe in eine Bibliothek laufen unter einem Mutex (die Runtime darin ist nicht threadsicher). Nicht geprüft ist der Aufbau mit MSVC und auf macOS.
