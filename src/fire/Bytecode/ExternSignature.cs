using fire.Ast;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MemoryPack;

namespace fire.Bytecode
{
    /// <summary>The signature of an `extern` declaration as needed for
    /// dynamic linking (see VM.CallExtern) - LibName is
    /// null if no `#extern "libName"` directive preceded the declaration
    /// (then only manual host registration via
    /// Bytecode.ExternRegistry remains possible).</summary>
    [MemoryPackable]
    public sealed partial class ExternSignature
    {
        public required string? LibName { get; init; }
        public required IReadOnlyList<TypeRef?> ParamTypes { get; init; }
        public required TypeRef? ReturnType { get; init; }
        /// <summary>`inline c` (ROADMAP 13.2): the C body (null for a plain `extern`), the names of the parameters (the body uses them), the source and the line of
        /// the body (`#line` in the generated C). The VM cannot run it; the native generator puts the function into the program.</summary>
        public string? InlineC { get; init; }
        public IReadOnlyList<string>? ParamNames { get; init; }
        public int InlineSource { get; init; }
        public int InlineLine { get; init; }
        /// <summary>How the program calls it (`M.Add` for an `inline c` method of a class; null: by its name).</summary>
        public string? DisplayName { get; init; }
    }
}
