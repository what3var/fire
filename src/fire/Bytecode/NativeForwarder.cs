using System;
using fire.Values;

namespace fire.Bytecode
{
    /// <summary>
    /// Eine Methode, die nichts weiter tut, als eine native Funktion mit `this.feld` und ihren eigenen Parametern aufzurufen:
    ///
    /// <code>Print(string text) { __GRPHRndPrint(this.id, text) }</code>
    ///
    /// So sind alle Methoden der Brücken-Preludes (Grafik, Geräte, Dateien, ...) gebaut. Für solche Methoden spart die VM
    /// den ganzen Aufruf-Apparat (Scope, Slots, Frame, Rückkehr) und ruft die native Funktion direkt auf (siehe VM.OpCallMethod:
    /// Schnellpfad der Inline-Caches). Erkannt wird das einmal pro Methode am fertigen Bytecode:
    ///
    /// <c>LoadThis; GetField f; LoadLocal 0,0 ... LoadLocal 0,n-1; CallNative i, n+1; (Return | Pop; LoadConst undefined; Return)</c>
    ///
    /// Die native Funktion selbst wird - wie überall - über ihren INDEX angesprungen, der beim Übersetzen feststeht.
    /// </summary>
    public sealed class NativeForwarder
    {
        public int NativeIndex { get; }

        /// <summary>Das Feld, dessen Wert das erste Argument ist (`id`, `handle`).</summary>
        public string FieldName { get; }

        /// <summary>Anzahl der Parameter der Methode (die native Funktion bekommt ein Argument mehr: das Feld).</summary>
        public int ParamCount { get; }

        /// <summary>Die Methode gibt das Ergebnis der nativen Funktion zurück (sonst `undefined`).</summary>
        public bool ReturnsResult { get; }

        private NativeForwarder(int nativeIndex, string fieldName, int paramCount, bool returnsResult)
        {
            NativeIndex = nativeIndex;
            FieldName = fieldName;
            ParamCount = paramCount;
            ReturnsResult = returnsResult;
        }

        /// <summary>Prüft den Körper einer Methode; null, wenn er nicht genau dieses Muster hat.</summary>
        public static NativeForwarder? TryCreate(Chunk chunk, int paramCount)
        {
            var code = chunk.Code;
            int i = 0;

            bool Next(OpCode expected)
            {
                if (i >= code.Count || (OpCode)code[i] != expected) return false;
                i++;
                return true;
            }
            int U16() { int v = code[i] | (code[i + 1] << 8); i += 2; return v; }
            bool Has(int bytes) => i + bytes <= code.Count;

            if (!Next(OpCode.LoadThis)) return null;
            if (!Has(3) || !Next(OpCode.GetField)) return null;
            int fieldConst = U16();
            if (fieldConst >= chunk.Constants.Count || chunk.Constants[fieldConst].Kind != ValueKind.String) return null;
            string fieldName = chunk.Constants[fieldConst].AsString();

            for (int k = 0; k < paramCount; k++)
            {
                if (!Has(5) || !Next(OpCode.LoadLocal)) return null;
                int depth = U16(), slot = U16();
                if (depth != 0 || slot != k) return null;
            }

            if (!Has(4) || !Next(OpCode.CallNative)) return null;
            int nativeIndex = U16();
            int argc = code[i++];
            if (argc != paramCount + 1) return null;

            bool returnsResult;
            if (Next(OpCode.Return)) returnsResult = true;
            else
            {
                // Pop; LoadConst <undefined>; Return
                if (!Next(OpCode.Pop) || !Has(3) || !Next(OpCode.LoadConst)) return null;
                int c = U16();
                if (c >= chunk.Constants.Count || chunk.Constants[c].Kind != ValueKind.Undefined) return null;
                if (!Next(OpCode.Return)) return null;
                returnsResult = false;
            }

            return new NativeForwarder(nativeIndex, fieldName, paramCount, returnsResult);
        }
    }
}
