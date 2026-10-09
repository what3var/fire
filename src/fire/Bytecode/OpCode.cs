namespace fire.Bytecode
{
    /// <summary>
    /// Stack-based bytecode instructions. Deliberately kept small and orthogonal
    /// - each instruction should later be translatable without major detours into a
    /// short sequence of native instructions (register machine).
    ///
    /// Operand encoding directly in the code stream after the opcode byte:
    /// u16 = 2 Bytes little-endian, u8 = 1 Byte.
    /// </summary>
    public enum OpCode : byte
    {
        LoadConst,          // u16 constIdx        : push Constants[constIdx]
        Pop,                //                       : pop
        Dup,                //                       : push Peek()
        Swap,               //                       : swaps the top two values
        RotateUnderTop,     //                       : [A,B,C] (bottom->top) -> [B,A,C] - swaps the
                            //                         both values UNDER the top one, leaves it itself
                            //                         untouched (see Compiler.CompileIncDec for
                            //                         `++`/`--` on a field/index target: obj/index
                            //                         must be kept for reading AND writing,
                            //                         while the old value for the postfix result
                            //                         is kept separately - not achievable with Swap alone (only
                            //                         the ABSOLUTE top two),
                            //                         without evaluating the target expression a second time)
        IncDecIndex,        // arr,idx -> value       : `++`/`--` on an index target (arr[i]++ etc.) -
                            //                         read+compute+write ATOMICALLY in the VM instead of via
                            //                         stack reordering, since here TWO "address" parts
                            //                         (array AND index) must be kept - with
                            //                         RotateUnderTop (only 3 values) alone not cleanly
                            //                         solvable. Operands: 1 byte isIncrement, 1 byte
                            //                         isPrefix (see Compiler.CompileIncDec)

        LoadLocal,          // u16 depth, u16 slot  : push GetAncestor(depth).GetSlot(slot)
        StoreLocal,         // u16 depth, u16 slot  : GetAncestor(depth).SetSlot(slot, Peek())
        LoadGlobal,         // u16 slot             : push GlobalScope.GetSlot(slot)
        StoreGlobal,        // u16 slot             : GlobalScope.SetSlot(slot, Peek())
        DeclareLocal,       //                       : pop v; CurrentScope.DefineSlot(v)

        Add, Sub, Mul, Div, Mod,
        BitAnd, BitOr, BitXor, ShiftLeft, ShiftRight, // '&' '|' '#' '<<' '>>' - each only on int, see Values.Value
        Power, // '^' (power, NOT bitwise XOR - that is BitXor/'#')
        FormatValue, // u16 constant (format specifier string) - see Value.Format, for $"...{x:F2}..."
        Neg, LogicalNot, BitNot,
        Eq, NotEq, Lt, LtEq, Gt, GtEq,

        CoerceUnit,         // u16 unitIdx          : pop v; push v.CoerceUnit(Units[unitIdx])
        CoerceUnitDynamic,  //                       : candidate=pop, anchor=peek; push candidate.CoerceUnit(anchor.Unit)
        CoerceType,         // u8 typeTag           : pop v; push v.CoerceType(TagToKind(typeTag))
        CoerceTypeDynamic,  //                       : candidate=pop, anchor=peek; push candidate.CoerceType(anchor.Kind)

        Jump,               // u16 addr             : ip = addr
        JumpIfFalse,        // u16 addr             : cond=pop; if (!cond) ip = addr
        JumpIfFalsePeek,    // u16 addr             : cond=peek; if (!cond) ip = addr (no pop - for &&)
        JumpIfTruePeek,     // u16 addr             : cond=peek; if (cond) ip = addr (no pop - for ||)

        EnterScope,         //                       : CurrentScope = new Scope(CurrentScope)
        ExitScope,          //                       : CurrentScope.Release(...); CurrentScope = Parent

        CallNative,         // u16 nativeIdx, u8 argCount : calls a registered native function
        CallTryableNative,  // u16 tryableIdx, u8 argCount : calls a "tryable" native function - success -> result value, failure/timeout -> undefined (see NativeRegistry.RegisterTryable)
        CallExtern,         // u16 nameIdx, u8 argCount   : calls a function linked by the host via ExternRegistry (see VM.MarshalArgsOut)

        MakeLambda,         // u16 protoIdx, u8 hasOnTarget : creates a LambdaValue from Functions[protoIdx]
                            //                                 (pop on-target value if hasOnTarget != 0)
        Call,               // u8 argCount          : calls the lambda value below the arguments
        Return,             //                       : pop return value; restore scope/chunk/ip/this of the caller

        NewObject,          // u16 classNameIdx, u8 argCount : creates an instance (owner = current scope) + runs through the constructor chain
        NewObjectOwned,     // u16 classNameIdx, u8 argCount : like NewObject, but owner = object below the arguments (direct field assignment, SPEC 2.1)
        GetField,           // u16 fieldNameIdx     : pop obj; push obj.Fields[name]
        SetField,           // u16 fieldNameIdx     : pop value, pop obj; obj.Fields[name] = value; push value
        LoadThis,           //                       : push currently bound 'this'
        SetFieldOnThis,     // u16 fieldNameIdx     : pop value; current 'this'.Fields[name] = value (for field initialisers)
        CallMethod,         // u16 methodNameIdx, u8 argCount : virtual method call (runtime class of 'obj' below the arguments)
        CallBaseMethod,     // u16 baseClassNameIdx, u16 methodNameIdx, u8 argCount : `base.Method(...)`, this remains the current 'this'
        ConstructBase,      // u16 baseClassNameIdx, u8 argCount : calls the base constructor for the current 'this'
        CallProtoWithThis,  // u16 protoIdx, u8 argCount : calls Functions[protoIdx] with 'this' = TOS-below-the-args (field initialiser)

        // Static members (SPEC "Static members") - one shared
        // storage location per CLASS instead of per instance (see RuntimeClass.
        // StaticFieldValues), NO object on the stack (unlike
        // GetField/SetField/CallMethod) - instead the class name directly
        // as a constant in the bytecode, since 'ClassName.Member' is already
        // resolved unambiguously at compile time (see Resolver.
        // TryResolveStaticMemberAccess).
        GetStaticField,   // u16 classNameConstIdx, u16 fieldNameConstIdx : pushes the current value
        SetStaticField,   // u16 classNameConstIdx, u16 fieldNameConstIdx : pops value, stores it, pushes it again (like SetField)
        SetStaticFieldOnInit, // u16 classNameConstIdx, u16 fieldNameConstIdx : like SetStaticField, but WITHOUT access-modifier
                              //   check (like SetFieldOnThis vs. SetField) - ONLY for the one-time initialisation of a
                              //   static field at program start (see Compiler.Compile), which runs as top-level code
                              //   without a matching OwnerClass context, but is the class's own initialisation and
                              //   should therefore ALWAYS be allowed, even for a private field - just like an instance-field initialiser
        CallStaticMethod, // u16 classNameConstIdx, u16 methodNameConstIdx, u8 argCount : calls WITHOUT a bound 'this'

        AddressOfLocal,     // u16 depth, u16 slot  : push pointer to GetAncestor(depth) slot(slot)
        AddressOfGlobal,    // u16 slot             : push pointer to GlobalScope slot(slot)
        AddressOfField,     // u16 fieldNameIdx      : pop obj; push pointer to obj.Fields[name]
        PtrRead,            //                       : pop ptr; push ptr.Read()
        PtrWrite,           //                       : pop value, pop ptr; ptr.Write(value); push value

        NewArray,           //                       : pop size (int); push new array of length size
        MakeArrayLiteral,   // u16 count             : pop count values (in order); push new array from them
        MakeBuffer,         // pop size (int), push new ByteBuffer (host byte order, all bytes 0)
        ArrayGet,           //                       : pop index, pop array; push array[index]
        ArraySet,           //                       : pop value, pop index, pop array; array[index]=value; push value

        RegisterHandler,    // u16 handlerTemplateIdx : registers a try handler (see Chunk.Handlers)
        UnregisterHandler,  //                       : removes the most recently registered handler (try passed through successfully)
        Throw,              //                       : pop value; wirft (siehe VM.ThrowException)

        IsInUnit,           // u16 unitIdx          : pop value; push (value.Unit dimensional kompatibel zu Units[unitIdx])
        IsOfType,           // u16 typeNameIdx      : pop value; push (value "is of" Typname, siehe VM.IsOfType)
        IsFrom,             // u8 transitive        : pop ownerVal, pop operandVal; push Ownership-Check-Ergebnis

        ResumeException,    //                       : pop resumeValue, pop excValue; resumes the frozen throw site
        ClearPendingResume, //                       : pop excValue; cleanly discards a frozen throw site that is never resumed

        CheckLambdaSignature, // u8 expectedParamCount : checks that Peek() is a lambda with exactly this parameter count, otherwise throws (see VM)
        CheckUnit,            // u16 constIdx (expected unit as string) : checks Peek().Unit == Unit.Parse(expected) exactly
                              //   (Values.Unit.Equals - dimension AND scaling, "mm" != "m"), otherwise throws UnitMismatchException
                              //   (see VM.ThrowUnitMismatch) - does NOT consume (like CheckLambdaSignature), the caller pops if
                              //   needed (see Compiler.EmitLambdaParamChecks/CompileAssign/VarDeclStmt compilation)

        Fire, // u16 functionProtoIdx, u16 globalSlotCount, u8 takingCount, u8 hasWith : spawns a real thread (see Runtime.FireRuntime) with a read-only globals snapshot, takingCount popped taking values and optionally a with value
        Sync, // u8 flags (bit0=isTry, bit1=isFlat) : pop target; calls SyncEngine.Sync/SyncFlat; push true/false/undefined
        Leave, // calls VM.RequestLeave() on this VM instance (handling happens at the next check point)
        Terminate, // pop value; calls VM.RequestTerminate(value) (static, global)
        RegisterThreadsCatch, // u16 protoIdx, u8 hasType, [u16 typeNameConstIdx] : registers a global 'catch threads(...)' handler
        RegisterTerminateCatch, // u16 protoIdx : registers the global 'catch terminate(...)' handler
        Process, // pop target; process an actor message, blocking (see Runtime.ActorMailbox)
        TryProcess, // pop target; non-blocking; push true/false

        Halt,

        // Deliberately appended AFTER Halt so that the numeric values of all previous opcodes (including Halt's) stay stable.
        CopyValue,      // u8 flags (bit0 = deep) : pop source; push copy (`flat x` / `copy x`), owner = current scope
        CopyValueOwned, // u8 flags (bit0 = deep) : pop source, pop owner object; push copy, owner = the object (like NewObjectOwned, SPEC 2.1)
        CopyArgs,       // u16 x4 (64 bits: 4 bits per argument, 1 copy flat, 2 copy deep, 3 address for `ref`, 4 fresh return value goes to the called function, 5 `take x`: the value goes to the called function, no matter who owned it) : prefix DIRECTLY before a call opcode (Call/CallMethod/CallStaticMethod/CallBaseMethod/NewObject/
                        //   NewObjectOwned/ConstructBase) - 2 bits per argument (bit 2i = copy flat, bit 2i+1 = copy deep): the call
                        //   copies these arguments itself as soon as the scope of the called function exists, and the copy belongs to this scope
                        //   (SPEC 2.4). At most 16 arguments.

        // Global variables and fire threads (docs/THREADING_DESIGN.md section 7) - again appended AFTER all previous ones.
        SyncGlobals,    //                : `sync globals`: the main program works off the queue of its fire threads; push count (int)
        SectionEnter,   //                : `sync global { ` - a fire thread registers and waits until the main program grants the section
                        //                  (no effect in the main program); counterpart SectionExit sits in the `finally` of the block
        SectionExit,    //                : ends the section
        SetAutoSync,    // u8 on        : `#nosync` (on = 0) turns off the automatic processing of the queue at safe points (emitted at program start)
        PostGlobal,     // u8 argCount   : `fire global { ... }`: pop lambda, pop argCount arguments; enqueues the lambda (object arguments as a copy)
                        //                  as a job for the main program that runs at its next `sync globals`; the caller does not wait
        EnterFinallyNormal, //            : pushes the completion "normal" (undefined, 0) - the normal entry into a finally block precedes it
        PushJump,       // u16 addr      : pushes the completion "jump" (addr, 3): `break`/`continue` out of a try with finally - after the finally execution continues at addr
        EndFinally,     //               : pop kind, pop payload: 0 normal (continue), 1 rethrow exception, 2 continue `return`, 3 jump to payload,
                        //                 4 return from a nested-started finally (leave/terminate)
        Probe,          // u16 nameConstIdx, u8 flags (1 = changing, 2 = all members) : pop handler, pop object; creates the probe, pushes its handle (int)
        SilenceMember,  // u16 nameConstIdx, u8 wildcard : pop object; removes the probes of this member (wildcard: all of the object)
        SilenceValue,   //                : pop value; a probe handle (int) removes this probe, an object all its probes
        MakeLambdaCapturing, // u16 protoIdx, u8 hasOnTarget, u8 captureCount : like MakeLambda; below it lie captureCount COPIED values (stack: c0..cn-1, [onTarget]),
                        //                  which the lambda call places as slots after the parameters in the new scope (lambda captures, SPEC 4.2)

        // ------------------------------------------------------------------------------------------------
        // Fused instructions (compiler: Chunk.EndsWithOp/ReplaceLastOp and the expression statements) - each replaces a
        // fixed sequence of ordinary instructions with exactly the same result, but saves the repeated dispatch.
        // ------------------------------------------------------------------------------------------------
        StoreLocalPop,  // u16 depth, u16 slot : StoreLocal + Pop (expression statement `x = ...`): writes the top value and removes it from the stack
        StoreGlobalPop, // u16 slot            : StoreGlobal + Pop
        JumpIfNotLt,    // u16 addr            : Lt + JumpIfFalse - compares the top two values, jumps if NOT a < b (both are consumed)
        JumpIfNotLtEq,  // u16 addr            : LtEq + JumpIfFalse
        JumpIfNotGt,    // u16 addr            : Gt + JumpIfFalse
        JumpIfNotGtEq,  // u16 addr            : GtEq + JumpIfFalse
        JumpIfNotEq,    // u16 addr            : Eq + JumpIfFalse
        JumpIfNotNotEq, // u16 addr            : NotEq + JumpIfFalse
        ArithLocalConstPop,  // u16 depth, u16 slot, u16 constIdx, u8 sub : `x = x + c` / `x++` (sub = 0) or `x = x - c` / `x--` (sub = 1) on a local variable as a statement
        ArithGlobalConstPop, // u16 slot, u16 constIdx, u8 sub            : the same for a global variable

        SetTimeout,          //                     : pop v; `#timeout value` sets the default wait time of the wait functions (emitted at program start)

        // Deliberately appended at the end (stable numeric values).
        NewJagged,           // u8 rankCount : pop rankCount sizes (outermost pushed first); push the multi-dimensional array (`new int[3][4]`), the inner ones belong to the outer one
        MakeArrayLiteralParts, // u16 count, u16 maskLo, u16 maskHi : like MakeArrayLiteral; bit i of the mask: element i is an array/buffer created in the literal itself and belongs to the new array
        OwnValue,            //                     : pop value (array/buffer), pop owner object; the owner takes over the value (freshly created, assigned directly to a field, SPEC 2.1); push the value
        HoistValue,          //                     : Peek (assignment to a variable of an outer scope): if the value belongs to an inner block of the running function, it moves to that function's scope (SPEC 2.1)
        Delete,              //                     : pop value; destroys an object, an array or a buffer immediately (`delete x`, SPEC 2.5)
        RequireRefParam,     // u16 slot, u16 nameConstIdx : checks that the `ref` parameter in the slot (depth 0) holds a pointer (the caller passed a variable), otherwise error
        AddressOfIndex,      //                     : pop index, pop array/buffer; push pointer to the element (argument for a `ref` parameter, SPEC 5.4.2)

        // `take x` (SPEC 2.2): the value (on top of the stack, stays there) now belongs to the holder - unconditionally.
        TakeToScope,         // u16 depth            : the scope `depth` levels above the current one (0xFFFF = the global scope): `var a = take x`, `v = take x`
        TakeToObject,        //                     : Stack [obj, value]: the object `obj` takes over the value (`obj.field = take x`)
        TakeToArray,         //                     : Stack [array, index, value]: the array takes over the value (`a[i] = take x`)
        TakeCheck,           //                     : Peek: a destroyed object/array/buffer on top of the stack throws the DestroyedException (before the call with `f(take x)`)
    }

    /// <summary>Target type for CoerceType/CoerceTypeDynamic - corresponds exactly to the
    /// base types the parser permits as the optional postfix '!' target
    /// (bool/int/float/char/string; 'class'/'undefined' are deliberately
    /// excluded there, see SPEC 3.1).</summary>
    public enum TypeTag : byte { Bool, Int, Float, Char, String }
}
