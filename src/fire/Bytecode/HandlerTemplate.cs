using System.Collections.Generic;
using MemoryPack;

namespace fire.Bytecode
{
    /// <summary>
    /// Compiled metadata for a `try` handler (stored in Chunk.Handlers,
    /// referenced by index via RegisterHandler - analogous to
    /// Constants/Units/Functions).
    ///
    /// FinallyProtoIdx points to a SEPARATELY compiled 0-arg proto for the
    /// finally block, which is needed only if an exception propagates PAST this
    /// handler (none of its catch clauses match) - for that
    /// the VM must be able to call it independently (nested), see
    /// VM.RunFinallyNested. The NORMAL path (try succeeds, or an exception
    /// caught here), on the other hand, runs via inline-compiled bytecode
    /// directly in the surrounding chunk (normal jump/fallthrough flow) - two
    /// compilations of the same finally body, because the two cases cannot be expressed with
    /// the same "what happens next" semantics in a single piece of bytecode
    /// (continue running vs. keep throwing outward).
    /// </summary>
    [MemoryPackable]
    public sealed partial class HandlerTemplate
    {
        public List<(string? TypeName, int CatchAddr)> Catches { get; set; } = new();
        /// <summary>Address of the `finally` block in the SAME chunk (only one copy of the block, with access to all local variables), or null.
        /// It is entered on every path that leaves the `try` - normally, via `break`/`continue`, `return`, exception or `leave`/`terminate` -
        /// with a completion value (payload, kind) on top of the operand stack, which `EndFinally` evaluates at the end (see OpCode.EndFinally).</summary>
        public int? FinallyAddr { get; set; }
    }
}
