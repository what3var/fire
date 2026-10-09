using fire.Ast;
using MemoryPack;
using System;
using System.Collections.Generic;

namespace fire.Bytecode
{
    /// <summary>
    /// Compiled function body: a chunk of its own (the lambda sees only
    /// its own scope + global anyway, so needs no upvalues/
    /// captured variables - a simple address space of its own suffices) plus the
    /// number of expected parameters. Created once on compiling a LambdaExpr;
    /// each evaluation of this LambdaExpr at runtime (e.g. in a
    /// loop), on the other hand, creates a new LambdaValue that points to the same
    /// (reused) FunctionProto.
    ///
    /// ParamDefaults: parallel to the parameters, a 0-arg proto of its own per
    /// optional parameter (null for required parameters) - evaluated by VM.
    /// FillDefaultArgs when a call supplies fewer arguments
    /// than ParamCount (see Ast.LambdaParam.DefaultValue docs).
    /// </summary>
    [MemoryPackable]
    public sealed partial class FunctionProto
    {
        public Chunk Chunk { get; }
        public int ParamCount { get; }
        public IReadOnlyList<FunctionProto?> ParamDefaults { get; }

        public AccessModifier? Access { get; set; }

        /// <summary>SPEC "Static members" - `true` for a `static`
        /// declared method/property accessor (constructors are never
        /// static). Determines on call whether VM.CallMethod expects an object
        /// (instance method) or VM.CallStaticMethod runs without a
        /// bound 'this'.</summary>
        public bool IsStatic { get; set; }

        /// <summary>Bit i: parameter i is declared `ref` (SPEC 5.4.2) - its slot holds a pointer to the caller's variable.</summary>
        public uint RefMask { get; set; }

        /// <summary>If the body of a lambda with exactly one parameter consists only of a member chain on that parameter
        /// (`c => c.radius`, `p => p.address.city`), the names from outside to inside - otherwise null. Basis of the lambda type `selector`.</summary>
        public string[]? SelectorPath { get; set; }

        private NativeForwarder? _forwarder;
        private bool _forwarderChecked;

        /// <summary>If the method is a pure forwarding to a native function (see NativeForwarder), otherwise null.
        /// Determined from the bytecode on first need (not serialised).</summary>
        [MemoryPackIgnore]
        public NativeForwarder? Forwarder
        {
            get
            {
                if (!_forwarderChecked)
                {
                    _forwarder = NativeForwarder.TryCreate(Chunk, ParamCount);
                    _forwarderChecked = true;
                }
                return _forwarder;
            }
        }

        public FunctionProto(Chunk chunk, int paramCount, AccessModifier? access, IReadOnlyList<FunctionProto?>? paramDefaults = null, bool isStatic = false)
        {
            Chunk = chunk;
            ParamCount = paramCount;
            ParamDefaults = paramDefaults ?? Array.Empty<FunctionProto?>();
            Access = access;
            IsStatic = isStatic;
        }
    }
}
