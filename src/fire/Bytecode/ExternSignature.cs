using fire.Ast;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace fire.Bytecode
{
    /// <summary>Die Signatur einer `extern`-Deklaration, wie sie für
    /// dynamisches Linking gebraucht wird (siehe VM.CallExtern) - LibName ist
    /// null, wenn keine `#extern "libName"`-Direktive vor der Deklaration
    /// stand (dann bleibt nur manuelle Host-Registrierung über
    /// Bytecode.ExternRegistry möglich).</summary>
    public sealed class ExternSignature
    {
        public required string? LibName { get; init; }
        public required IReadOnlyList<TypeRef?> ParamTypes { get; init; }
        public required TypeRef? ReturnType { get; init; }
    }
}
