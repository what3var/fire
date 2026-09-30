namespace fire.Bytecode
{
    /// <summary>
    /// Stack-basierte Bytecode-Instruktionen. Bewusst klein und orthogonal
    /// gehalten - jede Instruktion soll sich später ohne größere Umwege in eine
    /// kurze Sequenz nativer Instruktionen übersetzen lassen (Registermaschine).
    ///
    /// Operanden-Kodierung direkt im Code-Stream nach dem Opcode-Byte:
    /// u16 = 2 Bytes little-endian, u8 = 1 Byte.
    /// </summary>
    public enum OpCode : byte
    {
        LoadConst,          // u16 constIdx        : push Constants[constIdx]
        Pop,                //                       : pop
        Dup,                //                       : push Peek()
        Swap,               //                       : vertauscht die obersten zwei Werte
        RotateUnderTop,     //                       : [A,B,C] (unten->oben) -> [B,A,C] - vertauscht die
                            //                         beiden Werte UNTER dem obersten, lässt ihn selbst
                            //                         unangetastet (siehe Compiler.CompileIncDec für
                            //                         `++`/`--` auf einem Feld-/Index-Ziel: obj/Index
                            //                         müssen für Lesen UND Schreiben erhalten bleiben,
                            //                         während der alte Wert für das Postfix-Ergebnis
                            //                         separat aufgehoben wird - mit Swap allein (nur
                            //                         die ABSOLUTEN obersten zwei) nicht erreichbar,
                            //                         ohne den Zielausdruck ein zweites Mal auszuwerten)
        IncDecIndex,        // arr,idx -> wert        : `++`/`--` auf einem Index-Ziel (arr[i]++ usw.) -
                            //                         Lesen+Rechnen+Schreiben ATOMAR in der VM statt über
                            //                         Stack-Umsortierung, da hier ZWEI "Adress"-Teile
                            //                         (Array UND Index) erhalten bleiben müssen - mit
                            //                         RotateUnderTop (nur 3 Werte) allein nicht sauber
                            //                         lösbar. Operanden: 1 Byte isIncrement, 1 Byte
                            //                         isPrefix (siehe Compiler.CompileIncDec)

        LoadLocal,          // u16 depth, u16 slot  : push GetAncestor(depth).GetSlot(slot)
        StoreLocal,         // u16 depth, u16 slot  : GetAncestor(depth).SetSlot(slot, Peek())
        LoadGlobal,         // u16 slot             : push GlobalScope.GetSlot(slot)
        StoreGlobal,        // u16 slot             : GlobalScope.SetSlot(slot, Peek())
        DeclareLocal,       //                       : pop v; CurrentScope.DefineSlot(v)

        Add, Sub, Mul, Div, Mod,
        BitAnd, BitOr, BitXor, ShiftLeft, ShiftRight, // '&' '|' '#' '<<' '>>' - jeweils nur auf int, siehe Values.Value
        Power, // '^' (Potenz, NICHT bitweises XOR - das ist BitXor/'#')
        FormatValue, // u16 Konstante (Format-Spezifizierer-String) - siehe Value.Format, für $"...{x:F2}..."
        Neg, LogicalNot, BitNot,
        Eq, NotEq, Lt, LtEq, Gt, GtEq,

        CoerceUnit,         // u16 unitIdx          : pop v; push v.CoerceUnit(Units[unitIdx])
        CoerceUnitDynamic,  //                       : candidate=pop, anchor=peek; push candidate.CoerceUnit(anchor.Unit)
        CoerceType,         // u8 typeTag           : pop v; push v.CoerceType(TagToKind(typeTag))
        CoerceTypeDynamic,  //                       : candidate=pop, anchor=peek; push candidate.CoerceType(anchor.Kind)

        Jump,               // u16 addr             : ip = addr
        JumpIfFalse,        // u16 addr             : cond=pop; if (!cond) ip = addr
        JumpIfFalsePeek,    // u16 addr             : cond=peek; if (!cond) ip = addr (kein Pop - für &&)
        JumpIfTruePeek,     // u16 addr             : cond=peek; if (cond) ip = addr (kein Pop - für ||)

        EnterScope,         //                       : CurrentScope = new Scope(CurrentScope)
        ExitScope,          //                       : CurrentScope.Release(...); CurrentScope = Parent

        CallNative,         // u16 nativeIdx, u8 argCount : ruft eine registrierte native Funktion auf
        CallTryableNative,  // u16 tryableIdx, u8 argCount : ruft eine "tryable" native Funktion auf - Erfolg -> Ergebniswert, Fehlschlag/Timeout -> undefined (siehe NativeRegistry.RegisterTryable)
        CallExtern,         // u16 nameIdx, u8 argCount   : ruft eine per Host per ExternRegistry verlinkte Funktion auf (siehe VM.MarshalArgsOut)

        MakeLambda,         // u16 protoIdx, u8 hasOnTarget : erzeugt einen LambdaValue aus Functions[protoIdx]
                            //                                 (pop On-Target-Wert falls hasOnTarget != 0)
        Call,               // u8 argCount          : ruft den Lambda-Wert unterhalb der Argumente auf
        Return,             //                       : pop Rückgabewert; Scope/Chunk/ip/this des Aufrufers wiederherstellen

        NewObject,          // u16 classNameIdx, u8 argCount : erzeugt eine Instanz (Owner = aktueller Scope) + durchläuft die Konstruktor-Kette
        NewObjectOwned,     // u16 classNameIdx, u8 argCount : wie NewObject, aber Owner = Objekt unterhalb der Argumente (direkte Feldzuweisung, SPEC 2.1)
        GetField,           // u16 fieldNameIdx     : pop obj; push obj.Fields[name]
        SetField,           // u16 fieldNameIdx     : pop value, pop obj; obj.Fields[name] = value; push value
        LoadThis,           //                       : push aktuell gebundenes 'this'
        SetFieldOnThis,     // u16 fieldNameIdx     : pop value; aktuelles 'this'.Fields[name] = value (für Feld-Initialisierer)
        CallMethod,         // u16 methodNameIdx, u8 argCount : virtueller Methodenaufruf (Laufzeit-Klasse von 'obj' unterhalb der Argumente)
        CallBaseMethod,     // u16 baseClassNameIdx, u16 methodNameIdx, u8 argCount : `base.Method(...)`, this bleibt das aktuelle 'this'
        ConstructBase,      // u16 baseClassNameIdx, u8 argCount : ruft den Basis-Konstruktor für das aktuelle 'this' auf
        CallProtoWithThis,  // u16 protoIdx, u8 argCount : ruft Functions[protoIdx] mit 'this' = TOS-unterhalb-der-Args auf (Feld-Initialisierer)

        // Statische Mitglieder (SPEC "Statische Mitglieder") - eine geteilte
        // Speicherstelle pro KLASSE statt pro Instanz (siehe RuntimeClass.
        // StaticFieldValues), KEIN Objekt auf dem Stack (anders als
        // GetField/SetField/CallMethod) - stattdessen der Klassenname direkt
        // als Konstante im Bytecode, da 'ClassName.Member' schon zur
        // Compile-Zeit eindeutig aufgelöst wird (siehe Resolver.
        // TryResolveStaticMemberAccess).
        GetStaticField,   // u16 classNameConstIdx, u16 fieldNameConstIdx : pusht den aktuellen Wert
        SetStaticField,   // u16 classNameConstIdx, u16 fieldNameConstIdx : poppt Wert, speichert, pusht ihn erneut (wie SetField)
        SetStaticFieldOnInit, // u16 classNameConstIdx, u16 fieldNameConstIdx : wie SetStaticField, aber OHNE Zugriffsmodifikator-
                              //   Prüfung (wie SetFieldOnThis vs. SetField) - NUR für die einmalige Initialisierung eines
                              //   statischen Feldes beim Programmstart (siehe Compiler.Compile), die läuft als Top-Level-Code
                              //   ohne passenden OwnerClass-Kontext, ist aber die eigene Initialisierung der Klasse selbst und
                              //   soll deshalb IMMER dürfen, auch für ein privates Feld - genau wie ein Instanzfeld-Initialisierer
        CallStaticMethod, // u16 classNameConstIdx, u16 methodNameConstIdx, u8 argCount : ruft OHNE gebundenes 'this' auf

        AddressOfLocal,     // u16 depth, u16 slot  : push Pointer auf GetAncestor(depth)-Slot(slot)
        AddressOfGlobal,    // u16 slot             : push Pointer auf GlobalScope-Slot(slot)
        AddressOfField,     // u16 fieldNameIdx      : pop obj; push Pointer auf obj.Fields[name]
        PtrRead,            //                       : pop ptr; push ptr.Read()
        PtrWrite,           //                       : pop value, pop ptr; ptr.Write(value); push value

        NewArray,           //                       : pop size (int); push neues Array der Länge size
        MakeArrayLiteral,   // u16 count             : pop count Werte (in Reihenfolge); push neues Array daraus
        MakeBuffer,         // pop Größe (int), push neuer ByteBuffer (Host-Byte-Order, alle Bytes 0)
        ArrayGet,           //                       : pop index, pop array; push array[index]
        ArraySet,           //                       : pop value, pop index, pop array; array[index]=value; push value

        RegisterHandler,    // u16 handlerTemplateIdx : registriert einen try-Handler (siehe Chunk.Handlers)
        UnregisterHandler,  //                       : entfernt den zuletzt registrierten Handler (try erfolgreich durchlaufen)
        Throw,              //                       : pop value; wirft (siehe VM.ThrowException)

        IsInUnit,           // u16 unitIdx          : pop value; push (value.Unit dimensional kompatibel zu Units[unitIdx])
        IsOfType,           // u16 typeNameIdx      : pop value; push (value "is of" Typname, siehe VM.IsOfType)
        IsFrom,             // u8 transitive        : pop ownerVal, pop operandVal; push Ownership-Check-Ergebnis

        ResumeException,    //                       : pop resumeValue, pop excValue; setzt die eingefrorene Wurfstelle fort
        ClearPendingResume, //                       : pop excValue; verwirft eine nie fortgesetzte eingefrorene Wurfstelle sauber

        CheckLambdaSignature, // u8 expectedParamCount : prüft Peek() ist Lambda mit genau dieser Parameterzahl, wirft sonst (siehe VM)
        CheckUnit,            // u16 constIdx (erwartete Einheit als String) : prüft Peek().Unit == Unit.Parse(erwartet) exakt
                              //   (Values.Unit.Equals - Dimension UND Skalierung, "mm" != "m"), wirft sonst UnitMismatchException
                              //   (siehe VM.ThrowUnitMismatch) - konsumiert NICHT (wie CheckLambdaSignature), Aufrufer poppt bei
                              //   Bedarf selbst (siehe Compiler.EmitLambdaParamChecks/CompileAssign/VarDeclStmt-Kompilierung)

        Fire, // u16 functionProtoIdx, u16 globalSlotCount, u8 takingCount, u8 hasWith : spawnt einen echten Thread (siehe Runtime.FireRuntime) mit Read-only-Globals-Snapshot, takingCount gepoppten taking-Werten und optional einem with-Wert
        Sync, // u8 flags (bit0=isTry, bit1=isFlat) : pop target; ruft SyncEngine.Sync/SyncFlat auf; push true/false/undefined
        Leave, // ruft VM.RequestLeave() auf dieser VM-Instanz auf (Abwicklung passiert am nächsten Prüfpunkt)
        Terminate, // pop value; ruft VM.RequestTerminate(value) auf (statisch, global)
        RegisterThreadsCatch, // u16 protoIdx, u8 hasType, [u16 typeNameConstIdx] : registriert einen globalen 'catch threads(...)'-Handler
        RegisterTerminateCatch, // u16 protoIdx : registriert den globalen 'catch terminate(...)'-Handler
        Process, // pop target; blockierend eine Actor-Nachricht abarbeiten (siehe Runtime.ActorMailbox)
        TryProcess, // pop target; nicht-blockierend; push true/false

        Halt,

        // Bewusst NACH Halt angehängt, damit die Zahlenwerte aller bisherigen Opcodes (auch die von Halt) stabil bleiben.
        CopyValue,      // u8 flags (bit0 = tief) : pop Quelle; push Kopie (`flat x` / `copy x`), Owner = aktueller Scope
        CopyValueOwned, // u8 flags (bit0 = tief) : pop Quelle, pop Owner-Objekt; push Kopie, Owner = das Objekt (wie NewObjectOwned, SPEC 2.1)
        CopyArgs,       // u16 lo, u16 hi : Präfix DIREKT vor einem Aufruf-Opcode (Call/CallMethod/CallStaticMethod/CallBaseMethod/NewObject/
                        //   NewObjectOwned/ConstructBase) - 2 Bit je Argument (Bit 2i = flach kopieren, Bit 2i+1 = tief kopieren): der Aufruf
                        //   kopiert diese Argumente selbst, sobald die Scope der aufgerufenen Funktion steht, und die Kopie gehört dieser Scope
                        //   (SPEC 2.4). Höchstens 16 Argumente.
    }

    /// <summary>Zieltyp für CoerceType/CoerceTypeDynamic - entspricht genau den
    /// Basistypen, die der Parser als optionales Postfix-'!'-Ziel zulässt
    /// (bool/int/float/char/string; 'class'/'undefined' sind dort bewusst
    /// ausgeschlossen, siehe SPEC 3.1).</summary>
    public enum TypeTag : byte { Bool, Int, Float, Char, String }
}
