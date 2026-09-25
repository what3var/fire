using System.Collections.Generic;
using MemoryPack;

namespace fire.Bytecode
{
    /// <summary>
    /// Kompilierte Metadaten für einen `try`-Handler (in Chunk.Handlers
    /// abgelegt, per Index über RegisterHandler referenziert - analog zu
    /// Constants/Units/Functions).
    ///
    /// FinallyProtoIdx zeigt auf einen SEPARAT kompilierten 0-Arg-Proto für den
    /// finally-Block, der nur gebraucht wird, wenn eine Exception an diesem
    /// Handler VORBEI propagiert (keine seiner Catch-Klauseln passt) - dafür
    /// muss die VM ihn eigenständig (verschachtelt) aufrufen können, siehe
    /// VM.RunFinallyNested. Der NORMALE Durchlauf (try erfolgreich, oder hier
    /// gefangene Exception) läuft dagegen über inline kompilierten Bytecode
    /// direkt im umgebenden Chunk (normaler Sprung-/Fallthrough-Fluss) - zwei
    /// Kompilate desselben finally-Bodys, weil sich die beiden Fälle nicht mit
    /// derselben "was passiert danach"-Semantik in einem einzigen Bytecode-
    /// Stück ausdrücken lassen (weiterlaufen vs. weiter nach außen werfen).
    /// </summary>
    [MemoryPackable]
    public sealed partial class HandlerTemplate
    {
        public List<(string? TypeName, int CatchAddr)> Catches { get; } = new();
        public int? FinallyProtoIdx { get; set; }
    }
}
