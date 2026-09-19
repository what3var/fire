using System;
using System.Collections.Generic;

namespace ScriptLang.Bytecode
{
    /// <summary>
    /// Kompilierter Funktionskörper: ein eigener Chunk (die Lambda sieht ja
    /// ohnehin nur ihren eigenen Scope + global, braucht also keine Upvalues/
    /// eingefangenen Variablen - ein simpler eigener Adressraum reicht) plus die
    /// Anzahl erwarteter Parameter. Wird einmal beim Kompilieren einer LambdaExpr
    /// erzeugt; jede Auswertung dieser LambdaExpr zur Laufzeit (z.B. in einer
    /// Schleife) erzeugt dagegen einen neuen LambdaValue, der auf denselben
    /// (wiederverwendeten) FunctionProto verweist.
    ///
    /// ParamDefaults: parallel zu den Parametern, ein eigener 0-Arg-Proto pro
    /// optionalem Parameter (null für Pflichtparameter) - wird von VM.
    /// FillDefaultArgs ausgewertet, wenn ein Aufruf weniger Argumente liefert
    /// als ParamCount (siehe Ast.LambdaParam.DefaultValue-Doku).
    /// </summary>
    public sealed class FunctionProto
    {
        public Chunk Chunk { get; }
        public int ParamCount { get; }
        public IReadOnlyList<FunctionProto?> ParamDefaults { get; }

        public FunctionProto(Chunk chunk, int paramCount, IReadOnlyList<FunctionProto?>? paramDefaults = null)
        {
            Chunk = chunk;
            ParamCount = paramCount;
            ParamDefaults = paramDefaults ?? Array.Empty<FunctionProto?>();
        }
    }
}
