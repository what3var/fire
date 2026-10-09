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
    }
}
