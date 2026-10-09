using System.Collections.Generic;
using fire.Values;
using MemoryPack;

namespace fire.Bytecode
{
    /// <summary>A compiled program: instruction bytes plus the pools referenced
    /// by index (constants, units), as well as optional
    /// debug information (line-number table, local variable names) for
    /// tools such as the step debugger in the editor sub-project - the actual
    /// VM needs none of it, it is purely for external inspection.</summary>
    [MemoryPackable]
    public sealed partial class Chunk
    {
        public List<byte> Code { get; }
        public List<Value> Constants { get; }
        public List<Unit> Units { get; }
        public List<FunctionProto> Functions { get; }
        public List<HandlerTemplate> Handlers { get; }

        /// <summary>The class whose method/constructor/property accessor
        /// this chunk is - `null` for top-level code, free lambdas and
        /// everything else outside a class. Set by the compiler (see
        /// CompileMethodProto/CompileConstructorProto), read by the VM for
        /// the access-modifier check (see VM.
        /// IsMemberAccessAllowed): this is the class whose CODE is currently
        /// actually executing - deliberately NOT the same as the concrete
        /// class of `this` (VM._currentThis)! If, e.g., a `Derived`
        /// instance calls an inherited, non-overridden `Base` method,
        /// or `Base`'s own constructor runs as part of a `Derived`
        /// construction (see ConstructBase), `this` is concretely a
        /// `Derived` instance although `Base`'s own code is running -
        /// for "may this code access Base's private member"
        /// the class of the EXECUTING CODE (Base) counts, not the
        /// concrete instance class (Derived).
        ///
        /// SERIALIZATION: creates a real cycle (RuntimeClass ->
        /// method/field initialiser/constructor -> this chunk ->
        /// OwnerClass -> the same RuntimeClass) - MemoryPack does not track
        /// object identity/cycles (see the CompiledProgram.
        /// RelinkAfterDeserialize docs for the same reasoning for
        /// RuntimeClass.Base), so it would recurse endlessly ("reached depth
        /// limit"). Therefore excluded and restored after deserialisation via
        /// RelinkAfterDeserialize.</summary>
        [MemoryPackIgnore]
        public RuntimeClass? OwnerClass { get; set; }

        // Line-number table: instead of storing one line PER instruction
        // (lots of redundancy, consecutive instructions almost always belong
        // to the same source line), only the POINTS at which the line
        // changes (run-length style) - (byte offset in code, source index, line).
        // GetLocation searches for the last entry with offset <= the requested offset.
        //
        // Source index (SourceIndex): position of the respective source in the
        // `sources` list that went to Parser.ParseMultiple (0 = usually
        // the prelude) - see Ast.ClassDecl.SourceIndex/Compiler.
        // CurrentSourceIndex. Needed since a program can consist of MULTIPLE
        // files (SPEC "Multiple source files"): a bare line number
        // alone is then ambiguous (line 5 in file A and line 5 in
        // file B are different places) - tools such as the
        // step debugger in the editor sub-project need BOTH to show the
        // right file/line and to hit breakpoints correctly.
        private readonly List<(int Offset, int SourceIndex, int Line)> _lineTable = new();
        private int _lastMarkedSourceIndex = -1;
        private int _lastMarkedLine = -1;

        /// <summary>Called by the compiler before the instructions for a
        /// new statement are emitted (see Compiler.CompileStmt) -
        /// creates a new line-table entry, but ONLY if source+line have
        /// actually changed compared to the last marked location (several
        /// instructions of the same location share
        /// one entry).</summary>
        public void MarkLine(int sourceIndex, int line)
        {
            if (sourceIndex == _lastMarkedSourceIndex && line == _lastMarkedLine) return;
            _lineTable.Add((Code.Count, sourceIndex, line));
            _lastMarkedSourceIndex = sourceIndex;
            _lastMarkedLine = line;
        }

        /// <summary>Returns the source index and source line to which the
        /// byte offset `ip` belongs ((0, 0) if no line information is
        /// present, e.g. for chunks built programmatically/without a
        /// compiler).</summary>
        public (int SourceIndex, int Line) GetLocation(int ip) => GetLocationRange(ip, out _, out _);

        /// <summary>Like <see cref="GetLocation"/>, but additionally returns the byte range [start, endExclusive) in which
        /// the same location applies - whoever queries many consecutive offsets (the debugger when running on) needs
        /// to look up again only on leaving this range. The table is sorted by offset (MarkLine always appends
        /// at the current end of the code): binary search instead of a pass from the start for every query.</summary>
        public (int SourceIndex, int Line) GetLocationRange(int ip, out int start, out int endExclusive)
        {
            var table = _lineTable;
            int lo = 0, hi = table.Count - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (table[mid].Offset <= ip) { found = mid; lo = mid + 1; }
                else hi = mid - 1;
            }

            start = found < 0 ? 0 : table[found].Offset;
            endExclusive = found + 1 < table.Count ? table[found + 1].Offset : int.MaxValue;
            return found < 0 ? (0, 0) : (table[found].SourceIndex, table[found].Line);
        }

        // Debug names for local variables: (depth relative to the respective
        // declaration site at COMPILE TIME, slot) -> name. "Depth" here
        // uses the same counting as the LoadLocal opcode (0 = the scope
        // in which the variable was declared) - a debugger that, at
        // runtime, pauses at a particular location must therefore interpret this depth
        // relative to the CURRENT scope depth at that location
        // (see VM.DescribeLocals). Deliberately a best-effort register, not a
        // perfect 1:1 mapping for every conceivable nesting case -
        // but sufficient for the vast majority of cases (parameters, top-level local
        // variables of a function/method/lambda).
        public Dictionary<(int Depth, int Slot), string> DebugLocalNames { get; }

        /// <summary>Normal use (the compiler builds up the chunk step by step
        /// via EmitByte/AddConstant/...) - all collections empty.</summary>
        public Chunk()
        {
            Code = new();
            Constants = new();
            Units = new();
            Functions = new();
            Handlers = new();
            DebugLocalNames = new();
        }

        /// <summary>For MemoryPack (see class docs "SERIALIZATION") - WITHOUT a
        /// constructor of its own with these parameters the generated
        /// deserialiser would have no way to put the values read from the stream
        /// anywhere (the properties deliberately have NO
        /// setter, see above) - it would simply DISCARD them and
        /// instead create `new Chunk()` with only empty collections,
        /// without any error message. Parameter names must (ignoring
        /// case) match the property names, which is
        /// the convention by which MemoryPack maps constructor parameters to
        /// properties.</summary>
        [MemoryPackConstructor]
        public Chunk(List<byte> code, List<Value> constants, List<Unit> units, List<FunctionProto> functions,
            List<HandlerTemplate> handlers, Dictionary<(int Depth, int Slot), string> debugLocalNames)
        {
            Code = code;
            Constants = constants;
            Units = units;
            Functions = functions;
            Handlers = handlers;
            DebugLocalNames = debugLocalNames;
        }

        public void MarkLocalName(int depth, int slot, string name) => DebugLocalNames[(depth, slot)] = name;

        // Array copies of code and constants for the VM (see VM.ReadByte/LoadConst): an array access is
        // considerably cheaper than the indexer of a List<T>, and the code is read 1-3 times per instruction.
        // On building (Emit*/Patch*/AddConstant) the copy is discarded and recreated on the next read;
        // after compiling nothing changes any more. Two threads may create it at the same time (same content).
        [MemoryPackIgnore] private byte[]? _codeArray;
        [MemoryPackIgnore] private Value[]? _constantsArray;

        [MemoryPackIgnore]
        public byte[] CodeArray => _codeArray ??= Code.ToArray();

        /// <summary>Inline caches of the call sites of this chunk, indexed by the byte offset of the opcode
        /// (see SiteCache). Created only on first need (after compiling the code length is fixed).</summary>
        [MemoryPackIgnore]
        public SiteCache?[]? SiteCaches;

        public SiteCache?[] EnsureSiteCaches() => SiteCaches ??= new SiteCache?[Code.Count + 1];

        [MemoryPackIgnore]
        public Value[] ConstantsArray => _constantsArray ??= Constants.ToArray();

        /// <summary>Replaces a constant in place (used when float constants are narrowed to 32 bits, see FloatNarrowing).</summary>
        public void ReplaceConstant(int index, Value v)
        {
            _constantsArray = null;
            Constants[index] = v;
        }

        public int AddConstant(Value v)
        {
            _constantsArray = null;
            Constants.Add(v);
            return Constants.Count - 1;
        }

        public int AddUnit(Unit u)
        {
            Units.Add(u);
            return Units.Count - 1;
        }

        public int AddFunctionProto(FunctionProto proto)
        {
            Functions.Add(proto);
            return Functions.Count - 1;
        }

        public int AddHandlerTemplate(HandlerTemplate template)
        {
            Handlers.Add(template);
            return Handlers.Count - 1;
        }

        public void EmitByte(byte b)
        {
            _codeArray = null;
            Code.Add(b);
        }
        public void EmitOp(OpCode op)
        {
            _lastOpStart = Code.Count;
            EmitByte((byte)op);
        }

        // For merging instructions during translation (see EndsWithOp): where the most recently emitted opcode begins
        // and at which position a jump target/patch point was most recently queried (every mark arises via `Here`).
        [MemoryPackIgnore] private int _lastOpStart = -1;
        [MemoryPackIgnore] private int _lastHere = -1;

        /// <summary>Is `op` (with `operandBytes` operand bytes) the most recently emitted instruction, and does NO jump target point behind it?
        /// Only then may the compiler merge it with the next one into a fused instruction: a mark behind it
        /// would, after merging, point into the middle of the new instruction.</summary>
        public bool EndsWithOp(OpCode op, int operandBytes) =>
            _lastOpStart >= 0 && _lastOpStart == Code.Count - 1 - operandBytes && Code[_lastOpStart] == (byte)op && _lastHere != Code.Count;

        /// <summary>Replaces the opcode of the most recently emitted instruction (same operands) - after a successful EndsWithOp.</summary>
        public void ReplaceLastOp(OpCode op)
        {
            _codeArray = null;
            Code[_lastOpStart] = (byte)op;
        }

        public void EmitU16(int value)
        {
            _codeArray = null;
            Code.Add((byte)(value & 0xFF));
            Code.Add((byte)((value >> 8) & 0xFF));
        }

        /// <summary>Writes a u16 value back to an already emitted location
        /// (backpatching for jump targets that are only known after compiling
        /// the skipped code).</summary>
        public void PatchU16(int at, int value)
        {
            _codeArray = null;
            Code[at] = (byte)(value & 0xFF);
            Code[at + 1] = (byte)((value >> 8) & 0xFF);
        }

        /// <summary>Current write position - useful as a jump target or as a starting point
        /// for a later PatchU16.</summary>
        [MemoryPackIgnore]
        public int Here
        {
            get
            {
                _lastHere = Code.Count;
                return Code.Count;
            }
        }
    }
}
